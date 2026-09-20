using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Impact;
using CodeVirtualize.Core.Remarks;
using CodeVirtualize.Core.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeVirtualize.CSharp.References;

public sealed class CSharpImpactService
{
    private static readonly string[] DynamicLimitations =
    [
        "lexical-candidates-are-not-confirmed-static-references",
        "reflection-invocations-and-strings-may-be-missed",
        "dependency-injection-registrations-may-be-missed",
        "xaml-bindings-are-not-analyzed",
        "generated-documents-are-not-analyzed",
        "trusted-in-process-compilation-does-not-evaluate-msbuild-project-references-analyzers-or-generators"
    ];

    public ImpactResponse Query(ImpactQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var requestId = query.RequestId ?? $"req_{Guid.NewGuid():N}";
        var store = new GenerationStore(query.StorePath);
        using var reader = query.GenerationId is null ? store.OpenCurrent() : store.Open(query.GenerationId);
        var manifest = reader.Manifest;
        var shard = manifest.Shards.SingleOrDefault(item => item.Kind == "symbol")
            ?? throw new StorageException(StorageErrorCodes.CorruptGeneration, "Symbol shard is missing.");
        var symbols = reader.ReadShardRecords(shard.Path)
            .Select(ContractJson.Deserialize<SymbolContract>)
            .ToArray();
        var symbolsById = symbols.ToDictionary(item => item.SymbolId, StringComparer.Ordinal);
        var missingTargets = query.TargetSymbolIds.Where(symbolId => !symbolsById.ContainsKey(symbolId))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missingTargets.Length != 0)
        {
            return Error(requestId, manifest, query.Budget, "SYMBOL_NOT_FOUND", "One or more target symbols are absent from the selected generation.",
                FreshnessState.NotApplicable, new Dictionary<string, string> { ["symbolIds"] = string.Join(',', missingTargets) });
        }

        var orderedFiles = manifest.Files.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var selectedFiles = orderedFiles.Take(query.Budget.MaxSourceFiles).ToArray();
        var verifiedSources = new Dictionary<string, VerifiedSource>(PathComparer);
        foreach (var file in selectedFiles)
        {
            var verification = VerifiedSourceReader.Read(query.WorkspacePath, file);
            if (verification.Status != SourceVerificationStatus.Verified || verification.Source is null)
            {
                return Error(
                    requestId,
                    manifest,
                    query.Budget,
                    verification.ErrorCode ?? "SOURCE_READ_FAILED",
                    verification.ErrorMessage ?? "Source verification failed.",
                    verification.Status == SourceVerificationStatus.Stale ? FreshnessState.Stale : FreshnessState.Unknown,
                    new Dictionary<string, string> { ["path"] = file.Path });
            }

            verifiedSources[file.Path] = verification.Source;
        }

        var analyses = BuildAnalyses(manifest, symbols, selectedFiles, verifiedSources);
        var staticReferences = analyses.SelectMany(CollectStaticReferences)
            .DistinctBy(Key)
            .ToArray();
        var initialTargets = query.TargetSymbolIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var (expanded, depthExhausted, expandedDepth) = ExpandCallers(staticReferences, initialTargets, query.Budget.MaxDepth);
        var results = new List<ImpactMatch>(expanded);
        if (query.IncludeLexicalCandidates)
        {
            var targetSymbols = initialTargets.Select(symbolId => symbolsById[symbolId]).ToArray();
            results.AddRange(analyses.SelectMany(analysis => CollectLexicalCandidates(analysis, targetSymbols)));
        }

        var ordered = results.DistinctBy(Key)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Depth)
            .ThenBy(item => item.TargetSymbolId, StringComparer.Ordinal)
            .ThenBy(item => item.Location.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Location.Span.Start)
            .ThenBy(item => item.Provenance, StringComparer.Ordinal)
            .ToArray();
        var queryFingerprint = QueryFingerprint(query);
        var offset = 0;
        if (query.Cursor is not null)
        {
            try
            {
                offset = PageCursorCodec.Decode(query.Cursor, manifest.GenerationId, queryFingerprint).Offset;
            }
            catch (ContractValidationException exception)
            {
                return Error(requestId, manifest, query.Budget, exception.ErrorCode, exception.Message,
                    FreshnessState.Verified, new Dictionary<string, string>());
            }
        }

        var capped = ordered.Take(query.Budget.MaxResults).ToArray();
        if (offset > capped.Length)
        {
            return Error(requestId, manifest, query.Budget, "CURSOR_INVALID", "Cursor offset exceeds the bounded result set.",
                FreshnessState.Verified, new Dictionary<string, string>());
        }

        var page = capped.Skip(offset).Take(query.Budget.PageSize).ToArray();
        var nextOffset = offset + page.Length;
        var hasNextPage = nextOffset < capped.Length;
        var exhausted = new List<string>();
        if (selectedFiles.Length < orderedFiles.Length) exhausted.Add("source");
        if (depthExhausted) exhausted.Add("depth");
        if (ordered.Length > query.Budget.MaxResults) exhausted.Add("result");
        if (hasNextPage) exhausted.Add("page");
        var truncated = exhausted.Count != 0;
        var nextCursor = hasNextPage
            ? PageCursorCodec.Encode(new PageCursorPayload(manifest.GenerationId, queryFingerprint, nextOffset))
            : null;
        var limitations = manifest.Coverage.Limitations.Concat(DynamicLimitations)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var coverage = new CoverageContract(
            "static-csharp-selected-configuration-plus-lexical-candidates",
            CoverageLevel.Partial,
            selectedFiles.Length,
            manifest.Coverage.ExcludedFiles,
            manifest.Coverage.FailedFiles,
            manifest.Coverage.UnknownFiles,
            manifest.Coverage.FailedProjects,
            limitations.Concat(exhausted.Select(item => $"{item}-budget-exhausted"))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            truncated);
        var status = page.Length == 0 && !truncated ? ResponseStatus.NotFound : ResponseStatus.Partial;
        var response = new ImpactResponse(
            requestId,
            manifest.GenerationId,
            status,
            new FreshnessContract(FreshnessState.Verified, FreshnessState.Unknown),
            coverage,
            page,
            truncated,
            nextCursor,
            query.Budget,
            new ImpactBudgetUsage(expandedDepth, ordered.Length, page.Length, selectedFiles.Length),
            exhausted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            limitations,
            []);
        response.Validate();
        return response;
    }

    private static IReadOnlyList<ProjectAnalysis> BuildAnalyses(
        ManifestContract manifest,
        IReadOnlyList<SymbolContract> symbols,
        IReadOnlyList<ManifestFileContract> selectedFiles,
        IReadOnlyDictionary<string, VerifiedSource> sources)
    {
        var references = TrustedPlatformReferences();
        var analyses = new List<ProjectAnalysis>();
        foreach (var project in manifest.Projects.Where(item => item.LoadStatus == ProjectLoadStatus.Analyzed)
                     .OrderBy(item => item.ProjectId, StringComparer.Ordinal))
        {
            var documents = selectedFiles.Where(file => file.ProjectIds.Contains(project.ProjectId, StringComparer.Ordinal))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file =>
                {
                    var source = sources[file.Path];
                    var tree = CSharpSyntaxTree.ParseText(
                        SourceText.From(source.Text, Encoding.UTF8),
                        new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: project.Defines),
                        file.Path);
                    return new AnalyzedDocument(file, source, tree);
                }).ToArray();
            if (documents.Length == 0)
            {
                continue;
            }

            var compilation = CSharpCompilation.Create(
                $"CodeVirtualize_Impact_{project.ProjectId}",
                documents.Select(item => item.Tree),
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
            var storedByDeclaration = symbols.Where(symbol => symbol.ProjectId == project.ProjectId)
                .SelectMany(symbol => symbol.Declarations.Select(declaration => (symbol.SymbolId, declaration)))
                .Where(item => item.declaration.DocumentKind == DocumentKind.Source && item.declaration.Location.Path is not null)
                .ToDictionary(
                    item => new DeclarationKey(Normalize(item.declaration.Location.Path!), item.declaration.Location.Span.Start, item.declaration.Location.Span.Length),
                    item => item.SymbolId);
            var roslynSymbols = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            var declaredNodes = new Dictionary<SyntaxNode, string>(ReferenceEqualityComparer.Instance);
            foreach (var document in documents)
            {
                var model = compilation.GetSemanticModel(document.Tree, ignoreAccessibility: true);
                var root = document.Tree.GetRoot();
                foreach (var pair in storedByDeclaration.Where(item => PathComparer.Equals(item.Key.Path, Normalize(document.File.Path))))
                {
                    var span = new TextSpan(pair.Key.Start, pair.Key.Length);
                    var node = root.FindNode(span, getInnermostNodeForTie: true);
                    if (node.Span != span)
                    {
                        node = root.DescendantNodes().FirstOrDefault(candidate => candidate.Span == span) ?? node;
                    }

                    var declared = model.GetDeclaredSymbol(node);
                    if (declared is null)
                    {
                        continue;
                    }

                    roslynSymbols[declared] = pair.Value;
                    declaredNodes[node] = pair.Value;
                }
            }

            analyses.Add(new ProjectAnalysis(project, documents, compilation, roslynSymbols, declaredNodes));
        }

        return analyses;
    }

    private static IEnumerable<ImpactMatch> CollectStaticReferences(ProjectAnalysis analysis)
    {
        foreach (var document in analysis.Documents)
        {
            var model = analysis.Compilation.GetSemanticModel(document.Tree, ignoreAccessibility: true);
            var root = document.Tree.GetRoot();
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                var targetSymbolId = StoredSymbolId(symbol, analysis.RoslynSymbols);
                if (targetSymbolId is null)
                {
                    continue;
                }

                var sourceSymbolId = SourceSymbolId(name, analysis.DeclaredNodes);
                var location = Location(document, name.Span);
                yield return new ImpactMatch(
                    targetSymbolId,
                    sourceSymbolId,
                    location,
                    ReferenceKind.Static,
                    StaticProvenance(name, symbol!),
                    0,
                    []);
            }
        }
    }

    private static IEnumerable<ImpactMatch> CollectLexicalCandidates(
        ProjectAnalysis analysis,
        IReadOnlyList<SymbolContract> targets)
    {
        foreach (var document in analysis.Documents)
        {
            var model = analysis.Compilation.GetSemanticModel(document.Tree, ignoreAccessibility: true);
            var root = document.Tree.GetRoot();
            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>()
                         .Where(item => item.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                var value = literal.Token.ValueText;
                foreach (var target in targets)
                {
                    if (!string.Equals(value, target.QualifiedName, StringComparison.Ordinal) &&
                        !string.Equals(value, target.Name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    yield return new ImpactMatch(
                        target.SymbolId,
                        SourceSymbolId(literal, analysis.DeclaredNodes),
                        Location(document, literal.Span),
                        ReferenceKind.LexicalCandidate,
                        LexicalProvenance(literal, target),
                        0,
                        ["lexical-candidate-is-not-a-confirmed-static-reference"]);
                }
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!invocation.Expression.ToString().EndsWith("Activator.CreateInstance", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var typeOf in invocation.DescendantNodes().OfType<TypeOfExpressionSyntax>())
                {
                    var type = model.GetTypeInfo(typeOf.Type).Type;
                    var targetSymbolId = StoredSymbolId(type, analysis.RoslynSymbols);
                    if (targetSymbolId is null || !targets.Any(item => item.SymbolId == targetSymbolId))
                    {
                        continue;
                    }

                    yield return new ImpactMatch(
                        targetSymbolId,
                        SourceSymbolId(invocation, analysis.DeclaredNodes),
                        Location(document, typeOf.Type.Span),
                        ReferenceKind.LexicalCandidate,
                        "reflection-activation-typeof",
                        0,
                        ["reflection-activation-candidate-is-not-a-confirmed-constructor-reference"]);
                }
            }
        }
    }

    private static (IReadOnlyList<ImpactMatch> Results, bool DepthExhausted, int ExpandedDepth) ExpandCallers(
        IReadOnlyList<ImpactMatch> staticReferences,
        IReadOnlyList<string> initialTargets,
        int maxDepth)
    {
        var byTarget = staticReferences.GroupBy(item => item.TargetSymbolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = initialTargets.ToArray();
        var results = new List<ImpactMatch>();
        var expandedDepth = 0;
        var depthExhausted = false;
        for (var depth = 0; depth <= maxDepth && frontier.Length != 0; depth++)
        {
            expandedDepth = depth;
            var next = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var target in frontier.Order(StringComparer.Ordinal))
            {
                if (!visited.Add(target) || !byTarget.TryGetValue(target, out var references))
                {
                    continue;
                }

                foreach (var reference in references)
                {
                    results.Add(reference with
                    {
                        Depth = depth,
                        Provenance = depth == 0 ? reference.Provenance : $"caller-expansion:{reference.Provenance}"
                    });
                    if (reference.SourceSymbolId is not null && !visited.Contains(reference.SourceSymbolId))
                    {
                        next.Add(reference.SourceSymbolId);
                    }
                }
            }

            if (depth == maxDepth)
            {
                depthExhausted = next.Any(symbolId => byTarget.ContainsKey(symbolId));
                break;
            }

            frontier = next.ToArray();
        }

        return (results, depthExhausted, expandedDepth);
    }

    private static string? StoredSymbolId(ISymbol? symbol, IReadOnlyDictionary<ISymbol, string> symbols)
    {
        if (symbol is null) return null;
        if (symbols.TryGetValue(symbol, out var value)) return value;
        if (symbol is IMethodSymbol { ReducedFrom: not null } method && symbols.TryGetValue(method.ReducedFrom, out value)) return value;
        if (!SymbolEqualityComparer.Default.Equals(symbol, symbol.OriginalDefinition) && symbols.TryGetValue(symbol.OriginalDefinition, out value)) return value;
        if (symbol is IMethodSymbol accessor && accessor.AssociatedSymbol is not null && symbols.TryGetValue(accessor.AssociatedSymbol, out value)) return value;
        return null;
    }

    private static string? SourceSymbolId(SyntaxNode node, IReadOnlyDictionary<SyntaxNode, string> declarations)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (declarations.TryGetValue(current, out var symbolId)) return symbolId;
        }

        return null;
    }

    private static LocationContract Location(AnalyzedDocument document, TextSpan span)
    {
        var lineSpan = document.Tree.GetLineSpan(span).Span;
        return new LocationContract(
            document.File.FileId,
            document.Source.ContentHash,
            new TextSpanContract(span.Start, span.Length, lineSpan.Start.Line + 1, lineSpan.End.Line + 1),
            document.File.Path);
    }

    private static string StaticProvenance(SimpleNameSyntax name, ISymbol symbol)
    {
        if (name.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is not null && symbol is IMethodSymbol)
            return "roslyn-semantic-invocation";
        if (symbol is INamedTypeSymbol) return "roslyn-semantic-type-reference";
        return "roslyn-semantic-member-reference";
    }

    private static string LexicalProvenance(LiteralExpressionSyntax literal, SymbolContract target)
    {
        var invocation = literal.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation?.Expression.ToString().EndsWith("Type.GetType", StringComparison.Ordinal) == true)
            return "reflection-string-qualified-type";
        return string.Equals(literal.Token.ValueText, target.QualifiedName, StringComparison.Ordinal)
            ? "lexical-string-qualified-name"
            : "lexical-string-member-name";
    }

    private static string QueryFingerprint(ImpactQuery query)
    {
        var values = query.TargetSymbolIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Concat([
                query.Budget.MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                query.Budget.MaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
                query.Budget.MaxSourceFiles.ToString(System.Globalization.CultureInfo.InvariantCulture),
                query.Budget.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                query.IncludeLexicalCandidates ? "true" : "false"
            ]);
        var value = string.Join("|", values);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
    }

    private static ImpactResponse Error(
        string requestId,
        ManifestContract manifest,
        ImpactBudget budget,
        string code,
        string message,
        FreshnessState freshness,
        IReadOnlyDictionary<string, string> details)
    {
        var limitations = manifest.Coverage.Limitations.Concat(DynamicLimitations)
            .Append(code.ToLowerInvariant().Replace('_', '-'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var coverage = manifest.Coverage with
        {
            Level = CoverageLevel.Partial,
            Limitations = limitations,
            Truncated = false
        };
        var response = new ImpactResponse(
            requestId,
            manifest.GenerationId,
            ResponseStatus.Error,
            new FreshnessContract(freshness, FreshnessState.Unknown),
            coverage,
            [],
            false,
            null,
            budget,
            new ImpactBudgetUsage(0, 0, 0, 0),
            [],
            limitations,
            [new ErrorContract(code, message, false, details)]);
        response.Validate();
        return response;
    }

    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences()
    {
        var trustedPlatformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator)
            ?? Array.Empty<string>();
        return trustedPlatformAssemblies.Where(File.Exists)
            .Distinct(PathComparer)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static object Key(ImpactMatch item) => new
    {
        item.TargetSymbolId,
        item.SourceSymbolId,
        item.Location.FileId,
        item.Location.Span.Start,
        item.Location.Span.Length,
        item.Kind,
        item.Depth,
        item.Provenance
    };

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record DeclarationKey(string Path, int Start, int Length);

    private sealed record AnalyzedDocument(
        ManifestFileContract File,
        VerifiedSource Source,
        SyntaxTree Tree);

    private sealed record ProjectAnalysis(
        ManifestProjectContract Project,
        IReadOnlyList<AnalyzedDocument> Documents,
        CSharpCompilation Compilation,
        IReadOnlyDictionary<ISymbol, string> RoslynSymbols,
        IReadOnlyDictionary<SyntaxNode, string> DeclaredNodes);
}
