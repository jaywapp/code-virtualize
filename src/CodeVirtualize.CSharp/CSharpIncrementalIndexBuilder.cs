using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeVirtualize.CSharp;

internal sealed record CSharpIncrementalBuildResult(
    CSharpBuildResult Build,
    int ReusedSymbolCount,
    int ReparsedFileCount,
    bool UsedFullRebuild);

internal sealed class CSharpIncrementalIndexBuilder
{
    private readonly IWorkspaceTrustPolicy trustPolicy;

    public CSharpIncrementalIndexBuilder(IWorkspaceTrustPolicy trustPolicy)
    {
        this.trustPolicy = trustPolicy;
    }

    public CSharpIncrementalBuildResult Build(
        CSharpBuildRequest request,
        ManifestContract previous,
        IReadOnlyList<SymbolContract> previousSymbols)
    {
        var inventory = WorkspaceInventoryReader.Read(request.WorkspacePath);
        var trustedSemantic = request.Mode == AnalysisMode.TrustedSemantic && trustPolicy.IsTrusted(inventory.WorkspaceRoot);
        var actualMode = trustedSemantic ? AnalysisMode.TrustedSemantic : AnalysisMode.SyntaxOnly;
        var analysisKey = SemanticConfigFingerprint.CreateAnalysisKey(new AnalysisConfigFingerprintInput(
            ContractVersions.Current,
            CSharpIndexBuilder.AdapterVersion,
            typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            inventory.Projects.Select(project => project.RelativeProjectPath).ToArray(),
            inventory.Projects.Select(project => project.ReferencesFingerprint).ToArray(),
            inventory.Projects.Select(project => project.TargetFramework ?? "unknown").ToArray(),
            inventory.Projects.SelectMany(project => project.Defines).ToArray(),
            actualMode));

        if (trustedSemantic || !string.Equals(previous.AnalysisKey, analysisKey, StringComparison.Ordinal) ||
            inventory.Projects.Any(project => project.LoadStatus != ProjectLoadStatus.Analyzed))
        {
            var full = new CSharpIndexBuilder(trustPolicy).Build(request);
            return new CSharpIncrementalBuildResult(full, 0, full.Manifest.Files.Count, true);
        }

        var oldFiles = previous.Files.ToDictionary(file => file.Path, PathComparer);
        var manifestFiles = new Dictionary<string, ManifestFileContract>(PathComparer);
        var changedSourcesByProject = new Dictionary<string, List<ParsedSource>>(StringComparer.Ordinal);
        var unchanged = new HashSet<(string ProjectId, string Path)>();
        var analyzedFiles = new HashSet<string>(PathComparer);
        var failedFiles = new HashSet<string>(PathComparer);
        var projectContracts = new List<ManifestProjectContract>();
        var limitations = new List<string>(inventory.Limitations)
        {
            "syntax-only-analysis-does-not-evaluate-msbuild-restore-build-analyzers-or-generators"
        };
        var reparsedFiles = new HashSet<string>(PathComparer);

        foreach (var project in inventory.Projects)
        {
            var projectLimitations = new List<string>(project.Limitations);
            var changedSources = new List<ParsedSource>();
            changedSourcesByProject[project.ProjectId] = changedSources;
            var priorProject = previous.Projects.SingleOrDefault(item => item.ProjectId == project.ProjectId);

            foreach (var file in project.Files)
            {
                try
                {
                    var bytes = File.ReadAllBytes(file.PhysicalPath);
                    var contentHash = WorkspaceInventoryReader.HashBytes(bytes);
                    analyzedFiles.Add(file.PhysicalPath);
                    if (oldFiles.TryGetValue(file.RelativePath, out var oldFile) &&
                        string.Equals(oldFile.ContentHash, contentHash, StringComparison.Ordinal))
                    {
                        AddManifestFile(manifestFiles, oldFile, project.ProjectId);
                        unchanged.Add((project.ProjectId, file.RelativePath));
                        continue;
                    }

                    var source = Parse(file, project.Defines, bytes, contentHash);
                    changedSources.Add(source);
                    reparsedFiles.Add(file.PhysicalPath);
                    AddManifestFile(manifestFiles, source, project.ProjectId);
                    if (source.Tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                    {
                        failedFiles.Add(file.PhysicalPath);
                        projectLimitations.Add($"syntax-errors:{file.RelativePath}");
                        limitations.Add($"syntax-errors:{file.RelativePath}");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    failedFiles.Add(file.PhysicalPath);
                    projectLimitations.Add($"source-read-failed:{file.RelativePath}");
                    limitations.Add($"source-read-failed:{file.RelativePath}");
                }
            }

            if (priorProject is not null)
            {
                foreach (var priorLimitation in priorProject.Limitations.Where(item => item.StartsWith("syntax-errors:", StringComparison.Ordinal)))
                {
                    var path = priorLimitation["syntax-errors:".Length..];
                    if (unchanged.Contains((project.ProjectId, path)))
                    {
                        projectLimitations.Add(priorLimitation);
                        limitations.Add(priorLimitation);
                        failedFiles.Add(Path.GetFullPath(Path.Combine(inventory.WorkspaceRoot, path.Replace('/', Path.DirectorySeparatorChar))));
                    }
                }
            }

            projectContracts.Add(new ManifestProjectContract(
                project.ProjectId,
                project.TargetFramework,
                request.Configuration,
                project.Defines,
                project.ReferencesFingerprint,
                project.LoadStatus,
                ContractAnalysisLevel.SyntaxOnly,
                projectLimitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }

        var reused = new List<SymbolContract>();
        foreach (var symbol in previousSymbols)
        {
            var declarations = symbol.Declarations.Where(declaration =>
                    declaration.Location.Path is not null &&
                    unchanged.Contains((symbol.ProjectId, declaration.Location.Path)))
                .ToArray();
            if (declarations.Length != 0)
            {
                reused.Add(symbol with { Declarations = declarations });
            }
        }

        var extracted = new List<SymbolContract>();
        foreach (var project in inventory.Projects)
        {
            extracted.AddRange(CSharpSymbolExtractor.Extract(
                project,
                analysisKey,
                changedSourcesByProject[project.ProjectId],
                compilation: null));
        }

        if (limitations.Count == 0)
        {
            limitations.Add("analysis-coverage-is-not-complete");
        }

        var coverage = new CoverageContract(
            "static-csharp-syntax-only-safe-inventory",
            CoverageLevel.Partial,
            analyzedFiles.Count,
            inventory.ExcludedFileCount,
            failedFiles.Count,
            0,
            [],
            limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            false);
        var orderedSymbols = reused.Concat(extracted)
            .GroupBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .SelectMany(group => group.GroupBy(symbol => symbol.ProjectId, StringComparer.Ordinal).Select(Merge))
            .OrderBy(symbol => symbol.ProjectId, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Signature, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .ToArray();
        var orderedFiles = manifestFiles.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var orderedProjects = projectContracts.OrderBy(project => project.ProjectId, StringComparer.Ordinal).ToArray();
        var inputFingerprint = CreateInputFingerprint(analysisKey, orderedFiles, orderedProjects);
        var manifest = new ManifestContract(
            $"gen_{inputFingerprint[7..]}",
            SemanticConfigFingerprint.CreateWorkspaceKey(new WorkspaceFingerprintInput(inventory.WorkspaceRoot)),
            analysisKey,
            DateTimeOffset.UtcNow,
            CSharpIndexBuilder.AdapterVersion,
            inputFingerprint,
            GenerationState.Partial,
            orderedFiles,
            orderedProjects,
            [],
            coverage);
        var shard = new GenerationShardWriteRequest(
            "symbol",
            "symbols.jsonl",
            ContractSchemas.Symbol,
            orderedSymbols.Select(ContractJson.Serialize).ToArray());
        var store = new GenerationStore(request.StorePath);
        try
        {
            using var current = store.OpenCurrent();
            if (current.Manifest.GenerationId == manifest.GenerationId &&
                current.Manifest.InputFingerprint == manifest.InputFingerprint)
            {
                return new CSharpIncrementalBuildResult(
                    new CSharpBuildResult(current.Manifest, orderedSymbols, false, false),
                    reused.Count,
                    reparsedFiles.Count,
                    false);
            }
        }
        catch (StorageException exception) when (exception.ErrorCode == StorageErrorCodes.NoCurrentGeneration)
        {
        }

        ManifestContract published;
        try
        {
            published = store.Publish(new GenerationPublishRequest(manifest, [shard]), request.WriterWait);
        }
        catch (StorageException exception) when (exception.ErrorCode == StorageErrorCodes.GenerationExists)
        {
            manifest = manifest with { GenerationId = $"{manifest.GenerationId}_{Guid.NewGuid():N}" };
            published = store.Publish(new GenerationPublishRequest(manifest, [shard]), request.WriterWait);
        }

        return new CSharpIncrementalBuildResult(
            new CSharpBuildResult(published, orderedSymbols, true, false),
            reused.Count,
            reparsedFiles.Count,
            false);
    }

    private static ParsedSource Parse(
        InventorySourceFile file,
        IReadOnlyList<string> defines,
        byte[] bytes,
        string contentHash)
    {
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
        return new ParsedSource(file, bytes, text, tree, contentHash, encodingName, newline);
    }

    private static void AddManifestFile(
        IDictionary<string, ManifestFileContract> files,
        ManifestFileContract oldFile,
        string projectId)
    {
        if (files.TryGetValue(oldFile.Path, out var existing))
        {
            files[oldFile.Path] = existing with
            {
                ProjectIds = existing.ProjectIds.Append(projectId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            };
            return;
        }

        files[oldFile.Path] = oldFile with { ProjectIds = [projectId] };
    }

    private static void AddManifestFile(
        IDictionary<string, ManifestFileContract> files,
        ParsedSource source,
        string projectId)
    {
        if (files.TryGetValue(source.File.RelativePath, out var existing))
        {
            files[source.File.RelativePath] = existing with
            {
                ProjectIds = existing.ProjectIds.Append(projectId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            };
            return;
        }

        files[source.File.RelativePath] = new ManifestFileContract(
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
            Limitations = ordered.SelectMany(symbol => symbol.Limitations)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
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

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}