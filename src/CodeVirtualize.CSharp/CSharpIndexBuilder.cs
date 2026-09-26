using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeVirtualize.CSharp;

public sealed record CSharpBuildRequest(
    string WorkspacePath,
    string StorePath,
    AnalysisMode Mode = AnalysisMode.SyntaxOnly,
    string Configuration = "Debug",
    TimeSpan? WriterWait = null);

public sealed record CSharpBuildResult(
    ManifestContract Manifest,
    IReadOnlyList<SymbolContract> Symbols,
    bool Published,
    bool SemanticLoadAttempted);

public sealed class CSharpIndexBuilder
{
    // Bumped from "csharp-1": the extractor now populates SymbolContract.ContainerId for members and
    // nested types. Bumping AdapterVersion changes analysisKey (SemanticConfigFingerprint.CreateAnalysisKey),
    // so any on-disk generation built by the old extractor (ContainerId always null) is treated as an
    // analysis-key mismatch and forces a full rebuild (see CSharpIncrementalIndexBuilder.Build) instead of
    // reusing stale symbols with a missing containerId alongside freshly extracted ones.
    public const string AdapterVersion = "csharp-2";

    private readonly IWorkspaceTrustPolicy trustPolicy;

    public CSharpIndexBuilder(IWorkspaceTrustPolicy? trustPolicy = null)
    {
        this.trustPolicy = trustPolicy ?? DenyAllWorkspaceTrustPolicy.Instance;
    }

    public CSharpBuildResult Build(CSharpBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StorePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Configuration);

        var inventory = WorkspaceInventoryReader.Read(request.WorkspacePath);
        var trustedSemantic = request.Mode == AnalysisMode.TrustedSemantic && trustPolicy.IsTrusted(inventory.WorkspaceRoot);
        var actualMode = trustedSemantic ? AnalysisMode.TrustedSemantic : AnalysisMode.SyntaxOnly;
        var analysisKey = SemanticConfigFingerprint.CreateAnalysisKey(new AnalysisConfigFingerprintInput(
            ContractVersions.Current,
            AdapterVersion,
            typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            inventory.Projects.Select(project => project.RelativeProjectPath).ToArray(),
            inventory.Projects.Select(project => project.ReferencesFingerprint).ToArray(),
            inventory.Projects.Select(project => project.TargetFramework ?? "unknown").ToArray(),
            inventory.Projects.SelectMany(project => project.Defines).ToArray(),
            actualMode));
        var workspaceKey = SemanticConfigFingerprint.CreateWorkspaceKey(new WorkspaceFingerprintInput(inventory.WorkspaceRoot));

        var manifestFiles = new Dictionary<string, ManifestFileContract>(PathComparer);
        var symbols = new List<SymbolContract>();
        var failedFiles = new HashSet<string>(PathComparer);
        var analyzedFiles = new HashSet<string>(PathComparer);
        var unknownFiles = new HashSet<string>(PathComparer);
        var projectContracts = new List<ManifestProjectContract>();
        var coverageLimitations = new List<string>(inventory.Limitations);
        if (!trustedSemantic)
        {
            coverageLimitations.Add(request.Mode == AnalysisMode.TrustedSemantic
                ? "semantic-load-requires-explicit-exact-workspace-trust"
                : "syntax-only-analysis-does-not-evaluate-msbuild-restore-build-analyzers-or-generators");
        }
        else
        {
            coverageLimitations.Add("trusted-semantic-in-process-compilation-does-not-evaluate-msbuild-or-project-references");
        }

        foreach (var project in inventory.Projects)
        {
            var projectLimitations = new List<string>(project.Limitations);
            var parsedSources = new List<ParsedSource>();
            foreach (var file in project.Files)
            {
                try
                {
                    var source = Parse(file, project.Defines);
                    parsedSources.Add(source);
                    AddManifestFile(manifestFiles, source, project.ProjectId);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    failedFiles.Add(file.PhysicalPath);
                    projectLimitations.Add($"source-read-failed:{file.RelativePath}");
                }
            }

            if (project.LoadStatus == ProjectLoadStatus.Failed)
            {
                foreach (var source in parsedSources) failedFiles.Add(source.File.PhysicalPath);
                coverageLimitations.Add($"project-load-failed:{project.RelativeProjectPath}");
                projectContracts.Add(ProjectContract(project, request.Configuration, ContractAnalysisLevel.Unknown, projectLimitations));
                continue;
            }

            if (project.LoadStatus == ProjectLoadStatus.Unknown)
            {
                foreach (var source in parsedSources)
                {
                    unknownFiles.Add(source.File.PhysicalPath);
                }
                coverageLimitations.Add($"project-scope-unknown:{project.RelativeProjectPath}");
                projectContracts.Add(ProjectContract(project, request.Configuration, ContractAnalysisLevel.Unknown, projectLimitations));
                continue;
            }

            CSharpCompilation? compilation = null;
            var analysisLevel = ContractAnalysisLevel.SyntaxOnly;
            if (trustedSemantic)
            {
                compilation = CreateTrustedCompilation(project, parsedSources);
                analysisLevel = ContractAnalysisLevel.Semantic;
                var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(5).ToArray();
                if (errors.Length > 0)
                {
                    projectLimitations.Add("semantic-compilation-has-errors");
                    coverageLimitations.Add($"semantic-compilation-partial:{project.RelativeProjectPath}");
                }
                projectLimitations.Add("trusted-semantic-compilation-does-not-evaluate-msbuild-or-run-analyzers-or-generators");
            }

            foreach (var source in parsedSources)
            {
                analyzedFiles.Add(source.File.PhysicalPath);
                if (source.Tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    failedFiles.Add(source.File.PhysicalPath);
                    projectLimitations.Add($"syntax-errors:{source.File.RelativePath}");
                    coverageLimitations.Add($"syntax-errors:{source.File.RelativePath}");
                }
            }

            symbols.AddRange(CSharpSymbolExtractor.Extract(project, analysisKey, parsedSources, compilation));
            projectContracts.Add(ProjectContract(project, request.Configuration, analysisLevel, projectLimitations));
        }

        var failedProjects = projectContracts.Where(project => project.LoadStatus == ProjectLoadStatus.Failed)
            .Select(project => project.ProjectId).Order(StringComparer.Ordinal).ToArray();
        var limitations = coverageLimitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var isComplete = trustedSemantic && coverageLimitations.Count == 0 && failedFiles.Count == 0 && unknownFiles.Count == 0 && failedProjects.Length == 0 &&
                         !limitations.Any(limitation => limitation.Contains("partial", StringComparison.Ordinal) || limitation.Contains("failed", StringComparison.Ordinal));
        if (!isComplete && limitations.Count == 0)
        {
            limitations.Add("analysis-coverage-is-not-complete");
        }

        var coverage = new CoverageContract(
            trustedSemantic ? "static-csharp-trusted-in-process-compilation" : "static-csharp-syntax-only-safe-inventory",
            isComplete ? CoverageLevel.CompleteWithinScope : CoverageLevel.Partial,
            analyzedFiles.Count,
            inventory.ExcludedFileCount,
            failedFiles.Count,
            unknownFiles.Count,
            failedProjects,
            limitations,
            false);

        var orderedSymbols = symbols.GroupBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .SelectMany(group => group.GroupBy(symbol => symbol.ProjectId, StringComparer.Ordinal).Select(projectGroup => Merge(projectGroup)))
            .OrderBy(symbol => symbol.ProjectId, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Signature, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .ToArray();
        var orderedFiles = manifestFiles.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var inputFingerprint = CreateInputFingerprint(analysisKey, orderedFiles, projectContracts);
        var generationId = $"gen_{inputFingerprint[7..]}";
        var manifest = new ManifestContract(
            generationId,
            workspaceKey,
            analysisKey,
            DateTimeOffset.UtcNow,
            AdapterVersion,
            inputFingerprint,
            isComplete ? GenerationState.Valid : GenerationState.Partial,
            orderedFiles,
            projectContracts.OrderBy(project => project.ProjectId, StringComparer.Ordinal).ToArray(),
            [],
            coverage);

        var store = new GenerationStore(Path.GetFullPath(request.StorePath));
        try
        {
            using var current = store.OpenCurrent();
            if (string.Equals(current.Manifest.GenerationId, generationId, StringComparison.Ordinal) &&
                string.Equals(current.Manifest.InputFingerprint, inputFingerprint, StringComparison.Ordinal) &&
                string.Equals(current.Manifest.AnalysisKey, analysisKey, StringComparison.Ordinal))
            {
                return new CSharpBuildResult(current.Manifest, orderedSymbols, false, trustedSemantic);
            }
        }
        catch (StorageException exception) when (exception.ErrorCode == StorageErrorCodes.NoCurrentGeneration)
        {
        }

        var shard = new GenerationShardWriteRequest(
            "symbol",
            "symbols.jsonl",
            ContractSchemas.Symbol,
            orderedSymbols.Select(ContractJson.Serialize).ToArray());
        ManifestContract published;
        try
        {
            published = store.Publish(new GenerationPublishRequest(manifest, [shard]), request.WriterWait);
        }
        catch (StorageException exception) when (exception.ErrorCode == StorageErrorCodes.GenerationExists)
        {
            manifest = manifest with { GenerationId = $"{generationId}_{Guid.NewGuid():N}" };
            published = store.Publish(new GenerationPublishRequest(manifest, [shard]), request.WriterWait);
        }

        return new CSharpBuildResult(published, orderedSymbols, true, trustedSemantic);
    }

    private static ParsedSource Parse(InventorySourceFile file, IReadOnlyList<string> defines)
    {
        var bytes = File.ReadAllBytes(file.PhysicalPath);
        string text;
        Encoding encoding;
        using (var stream = new MemoryStream(bytes, writable: false))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        var tree = CSharpSyntaxTree.ParseText(
            SourceText.From(text, encoding),
            new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: defines),
            file.DocumentPath);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "crlf" : text.Contains('\n') ? "lf" : text.Contains('\r') ? "cr" : "none";
        var encodingName = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? "utf-8-bom" : encoding.WebName;
        return new ParsedSource(file, bytes, text, tree, WorkspaceInventoryReader.HashBytes(bytes), encodingName, newline);
    }

    private static CSharpCompilation CreateTrustedCompilation(InventoryProject project, IReadOnlyList<ParsedSource> sources)
    {
        var trustedPlatformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator)
            ?? Array.Empty<string>();
        var references = trustedPlatformAssemblies.Where(File.Exists)
            .Distinct(PathComparer)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        return CSharpCompilation.Create(
            $"CodeVirtualize_{project.ProjectId}",
            sources.Select(source => source.Tree),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
    }

    private static ManifestProjectContract ProjectContract(
        InventoryProject project,
        string configuration,
        ContractAnalysisLevel analysisLevel,
        IEnumerable<string> limitations) => new(
            project.ProjectId,
            project.TargetFramework,
            configuration,
            project.Defines,
            project.ReferencesFingerprint,
            project.LoadStatus,
            analysisLevel,
            limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    private static void AddManifestFile(
        IDictionary<string, ManifestFileContract> files,
        ParsedSource source,
        string projectId)
    {
        if (files.TryGetValue(source.File.PhysicalPath, out var existing))
        {
            files[source.File.PhysicalPath] = existing with
            {
                ProjectIds = existing.ProjectIds.Append(projectId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            };
            return;
        }

        files[source.File.PhysicalPath] = new ManifestFileContract(
            $"file_{WorkspaceInventoryReader.HashUtf8(source.File.RelativePath)[7..]}",
            source.File.RelativePath,
            source.ContentHash,
            source.Bytes.LongLength,
            source.Encoding,
            source.Newline,
            [projectId]);
    }

    private static SymbolContract Merge(IEnumerable<SymbolContract> symbols)
    {
        var ordered = symbols.OrderBy(symbol => symbol.SymbolId, StringComparer.Ordinal).ToArray();
        var first = ordered[0];
        return first with
        {
            Declarations = ordered.SelectMany(symbol => symbol.Declarations)
                .DistinctBy(declaration => (declaration.Location.FileId, declaration.Location.Span.Start, declaration.Location.Span.Length))
                .OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.Location.Span.Start)
                .ToArray(),
            Limitations = ordered.SelectMany(symbol => symbol.Limitations).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            IdentityQuality = ordered.All(symbol => symbol.IdentityQuality == IdentityQuality.Semantic)
                ? IdentityQuality.Semantic
                : IdentityQuality.Syntactic
        };
    }

    private static string CreateInputFingerprint(
        string analysisKey,
        IReadOnlyList<ManifestFileContract> files,
        IReadOnlyList<ManifestProjectContract> projects)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(analysisKey);
        foreach (var project in projects.OrderBy(project => project.ProjectId, StringComparer.Ordinal))
        {
            writer.Write(project.ProjectId);
            writer.Write(project.ReferencesFingerprint);
            writer.Write((int)project.LoadStatus);
        }

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            writer.Write(file.Path);
            writer.Write(file.ContentHash);
            foreach (var projectId in file.ProjectIds.Order(StringComparer.Ordinal)) writer.Write(projectId);
        }

        writer.Flush();
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))}";
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}







