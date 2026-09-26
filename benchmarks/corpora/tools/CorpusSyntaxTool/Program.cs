// CorpusSyntaxTool
//
// Independent corpus measurement tool for benchmarks/protocol.md Revision 2
// (R2-1 grading metrics, R2-2 NAV target sampling, R2-2 DIFF commit-pair selection).
//
// This tool deliberately depends only on the public Roslyn syntax API
// (Microsoft.CodeAnalysis.CSharp) and the local `git` executable. It does not
// reference, build against, or read output from any CodeVirtualize.* assembly
// or CLI, so its measurements are independent of the system under test.
//
// Usage:
//   CorpusSyntaxTool grade         <corpusRoot> <outJson>
//   CorpusSyntaxTool nav-candidates <corpusRoot> <outJson>
//   CorpusSyntaxTool select-nav    <corpusRoot> <candidatesJson> <outJson> [seed] [take]
//   CorpusSyntaxTool select-diff   <corpusRoot> <pinnedCommit> <outJson> [maxCommits] [pairs]
//
// All paths in output JSON are corpus-relative, POSIX-separated. No absolute
// local paths and no source text are written to output (R2-1 "원문 비복사").

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    public const string ToolName = "CorpusSyntaxTool";
    // Keep in sync with CorpusSyntaxTool.csproj PackageReference version and
    // record in rev2-manifest.json per protocol R2-1 "독립 계수 도구 버전".
    public const string ToolVersion = "1.0.0";
    public const string RoslynPackageVersion = "4.14.0";

    private static readonly string[] GeneratedPathMarkers =
    [
        ".g.cs",
        ".designer.cs",
        ".generated.cs",
    ];

    private static readonly string[] GeneratedDirMarkers =
    [
        "/generated/",
        "/obj/",
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            switch (args[0])
            {
                case "grade":
                    return RunGrade(args);
                case "nav-candidates":
                    return RunNavCandidates(args);
                case "select-nav":
                    return RunSelectNav(args);
                case "select-diff":
                    return RunSelectDiff(args);
                default:
                    Console.Error.WriteLine($"Unknown mode '{args[0]}'.");
                    PrintUsage();
                    return 2;
            }
        }
        catch (ToolUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            CorpusSyntaxTool modes:
              grade          <corpusRoot> <outJson>
              nav-candidates <corpusRoot> <outJson>
              select-nav     <corpusRoot> <candidatesJson> <outJson> [seed] [take]
              select-diff    <corpusRoot> <pinnedCommit> <outJson> [maxCommits] [pairs]
            """);
    }

    // ------------------------------------------------------------------
    // grade: R2-1 grading metrics (five indicators)
    // ------------------------------------------------------------------

    private static int RunGrade(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ToolUsageException("grade requires <corpusRoot> <outJson>");
        }

        string root = Path.GetFullPath(args[1]);
        string outPath = args[2];

        var csFiles = EnumerateCsFiles(root, excludeGenerated: false).ToList();
        var csprojFiles = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).ToList();

        long csBytes = 0;
        long typeDecls = 0;
        long methodDecls = 0;
        long constructorDecls = 0;
        long propertyDecls = 0;
        long indexerDecls = 0;
        long eventDecls = 0;
        long fieldVariableDecls = 0;
        long enumMemberDecls = 0;
        long delegateDecls = 0;
        long operatorDecls = 0;
        long identifierOrGenericNameNodes = 0;
        long excludedDeclarationNameNodes = 0;
        int parseFailures = 0;

        foreach (var file in csFiles)
        {
            byte[] bytes = File.ReadAllBytes(file);
            csBytes += bytes.Length;

            string text;
            try
            {
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                parseFailures++;
                continue;
            }

            SyntaxTree tree;
            try
            {
                tree = CSharpSyntaxTree.ParseText(text, path: file);
            }
            catch (Exception)
            {
                parseFailures++;
                continue;
            }

            var root2 = tree.GetRoot();
            if (root2.ContainsDiagnostics)
            {
                // Still count declarations from whatever parsed; Roslyn's syntax
                // parser is error-tolerant. We do not skip files with diagnostics
                // because grading counts syntactic declarations, not semantic
                // correctness.
            }

            var counter = new DeclarationCounter();
            counter.Visit(root2);

            typeDecls += counter.TypeDecls;
            methodDecls += counter.MethodDecls;
            constructorDecls += counter.ConstructorDecls;
            propertyDecls += counter.PropertyDecls;
            indexerDecls += counter.IndexerDecls;
            eventDecls += counter.EventDecls;
            fieldVariableDecls += counter.FieldVariableDecls;
            enumMemberDecls += counter.EnumMemberDecls;
            delegateDecls += counter.DelegateDecls;
            operatorDecls += counter.OperatorDecls;
            identifierOrGenericNameNodes += counter.IdentifierOrGenericNameNodes;
            excludedDeclarationNameNodes += counter.ExcludedDeclarationNameNodes;
        }

        long declaredSymbolCount = typeDecls + methodDecls + constructorDecls + propertyDecls
            + indexerDecls + eventDecls + fieldVariableDecls + enumMemberDecls + delegateDecls
            + operatorDecls;
        long nameReferenceCount = identifierOrGenericNameNodes - excludedDeclarationNameNodes;

        var result = new
        {
            tool = ToolName,
            toolVersion = ToolVersion,
            roslynPackageVersion = RoslynPackageVersion,
            generatedAtUtc = DateTime.UtcNow.ToString("o"),
            corpusRoot = "REDACTED", // never emit local absolute paths
            metrics = new
            {
                csFileCount = csFiles.Count,
                csBytes,
                csprojCount = csprojFiles.Count,
                declaredSymbolCount,
                nameReferenceCount,
            },
            declaredSymbolBreakdown = new
            {
                type = typeDecls,
                method = methodDecls,
                constructor = constructorDecls,
                property = propertyDecls,
                indexer = indexerDecls,
                @event = eventDecls,
                fieldVariable = fieldVariableDecls,
                enumMember = enumMemberDecls,
                @delegate = delegateDecls,
                @operator = operatorDecls,
            },
            nameReferenceBreakdown = new
            {
                identifierOrGenericNameNodesTotal = identifierOrGenericNameNodes,
                excludedDeclarationNameNodes,
                note = "excludedDeclarationNameNodes covers NamespaceDeclaration/FileScopedNamespaceDeclaration Name and UsingDirective Alias Name, the only declaration-name positions Roslyn represents as IdentifierName/QualifiedName(IdentifierName)/GenericName nodes rather than plain identifier tokens.",
            },
            parseFailures,
        };

        WriteJson(outPath, result);
        Console.WriteLine($"grade: wrote {outPath}");
        return 0;
    }

    private sealed class DeclarationCounter : CSharpSyntaxWalker
    {
        public long TypeDecls;
        public long MethodDecls;
        public long ConstructorDecls;
        public long PropertyDecls;
        public long IndexerDecls;
        public long EventDecls;
        public long FieldVariableDecls;
        public long EnumMemberDecls;
        public long DelegateDecls;
        public long OperatorDecls;
        public long IdentifierOrGenericNameNodes;
        public long ExcludedDeclarationNameNodes;

        public DeclarationCounter() : base(SyntaxWalkerDepth.Node)
        {
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) { TypeDecls++; base.VisitClassDeclaration(node); }
        public override void VisitStructDeclaration(StructDeclarationSyntax node) { TypeDecls++; base.VisitStructDeclaration(node); }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) { TypeDecls++; base.VisitInterfaceDeclaration(node); }
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) { TypeDecls++; base.VisitRecordDeclaration(node); }
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node) { TypeDecls++; base.VisitEnumDeclaration(node); }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node) { MethodDecls++; base.VisitMethodDeclaration(node); }
        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) { ConstructorDecls++; base.VisitConstructorDeclaration(node); }
        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) { PropertyDecls++; base.VisitPropertyDeclaration(node); }
        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node) { IndexerDecls++; base.VisitIndexerDeclaration(node); }
        public override void VisitEventDeclaration(EventDeclarationSyntax node) { EventDecls++; base.VisitEventDeclaration(node); }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            EventDecls += node.Declaration.Variables.Count;
            base.VisitEventFieldDeclaration(node);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            FieldVariableDecls += node.Declaration.Variables.Count;
            base.VisitFieldDeclaration(node);
        }

        public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) { EnumMemberDecls++; base.VisitEnumMemberDeclaration(node); }
        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node) { DelegateDecls++; base.VisitDelegateDeclaration(node); }
        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node) { OperatorDecls++; base.VisitOperatorDeclaration(node); }
        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) { OperatorDecls++; base.VisitConversionOperatorDeclaration(node); }

        // local functions (LocalFunctionStatementSyntax) are intentionally not
        // visited/counted here — they are a distinct node type from
        // MethodDeclarationSyntax, so no explicit exclusion logic is needed.

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            IdentifierOrGenericNameNodes++;
            if (IsExcludedDeclarationNamePosition(node))
            {
                ExcludedDeclarationNameNodes++;
            }
            base.VisitIdentifierName(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            IdentifierOrGenericNameNodes++;
            // GenericName never appears as a NamespaceDeclaration/UsingDirective
            // alias name, so no exclusion check is needed here.
            base.VisitGenericName(node);
        }

        private static bool IsExcludedDeclarationNamePosition(IdentifierNameSyntax node)
        {
            // Case 1: this identifier is (part of) the declared name of a
            // namespace declaration, e.g. `namespace Foo.Bar` — Foo and Bar are
            // IdentifierNameSyntax nodes inside NamespaceDeclarationSyntax.Name
            // (possibly nested under QualifiedNameSyntax), not references.
            SyntaxNode? cursor = node;
            while (cursor is NameSyntax)
            {
                var parent = cursor.Parent;
                if (parent is NamespaceDeclarationSyntax nsDecl && nsDecl.Name == cursor)
                {
                    return true;
                }
                if (parent is FileScopedNamespaceDeclarationSyntax fsNsDecl && fsNsDecl.Name == cursor)
                {
                    return true;
                }
                if (parent is NameEqualsSyntax nameEquals && nameEquals.Name == cursor
                    && nameEquals.Parent is UsingDirectiveSyntax)
                {
                    // Case 2: alias name in `using X = Y;` — X is the declared
                    // alias identifier, not a reference.
                    return true;
                }
                cursor = parent as NameSyntax;
            }
            return false;
        }
    }

    private static IEnumerable<string> EnumerateCsFiles(string root, bool excludeGenerated)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsUnderDotGit(root, file))
            {
                continue;
            }
            if (excludeGenerated && IsGeneratedPath(root, file))
            {
                continue;
            }
            yield return file;
        }
    }

    private static bool IsUnderDotGit(string root, string file)
    {
        string rel = ToRelativePosix(root, file);
        return rel.StartsWith(".git/", StringComparison.Ordinal) || rel == ".git";
    }

    private static bool IsGeneratedPath(string root, string file)
    {
        string rel = ToRelativePosix(root, file).ToLowerInvariant();
        foreach (var marker in GeneratedPathMarkers)
        {
            if (rel.EndsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        foreach (var marker in GeneratedDirMarkers)
        {
            if (("/" + rel).Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string ToRelativePosix(string root, string file)
    {
        string rel = Path.GetRelativePath(root, file);
        return rel.Replace('\\', '/');
    }

    // ------------------------------------------------------------------
    // nav-candidates: R2-2 NAV target sampling, rule 1 (ordinary method
    // declarations, generated paths excluded)
    // ------------------------------------------------------------------

    private sealed record MethodCandidate(
        string SimpleName,
        string QualifiedName,
        List<string> ParameterTypes,
        string Path,
        int Line);

    private static int RunNavCandidates(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ToolUsageException("nav-candidates requires <corpusRoot> <outJson>");
        }

        string root = Path.GetFullPath(args[1]);
        string outPath = args[2];

        var candidates = new List<MethodCandidate>();

        foreach (var file in EnumerateCsFiles(root, excludeGenerated: true))
        {
            string text;
            try
            {
                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception)
            {
                continue;
            }

            SyntaxTree tree;
            try
            {
                tree = CSharpSyntaxTree.ParseText(text, path: file);
            }
            catch (Exception)
            {
                continue;
            }

            var relPath = ToRelativePosix(root, file);
            var collector = new MethodCandidateCollector(relPath, tree);
            collector.Visit(tree.GetRoot());
            candidates.AddRange(collector.Candidates);
        }

        // Sort by (path ordinal, line) per R2-2 rule 3.
        candidates.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Path, b.Path);
            return c != 0 ? c : a.Line.CompareTo(b.Line);
        });

        var output = candidates.Select(c => new
        {
            simpleName = c.SimpleName,
            qualifiedName = c.QualifiedName,
            parameterTypes = c.ParameterTypes,
            path = c.Path,
            line = c.Line,
        }).ToList();

        var result = new
        {
            tool = ToolName,
            toolVersion = ToolVersion,
            roslynPackageVersion = RoslynPackageVersion,
            generatedAtUtc = DateTime.UtcNow.ToString("o"),
            rule = "R2-2 NAV target sampling rule 1: ordinary method declarations (constructor/operator/accessor/local-function excluded), generated paths excluded, sorted by (path ordinal, line).",
            count = output.Count,
            candidates = output,
        };

        WriteJson(outPath, result);
        Console.WriteLine($"nav-candidates: wrote {outPath} ({output.Count} candidates)");
        return 0;
    }

    private sealed class MethodCandidateCollector : CSharpSyntaxWalker
    {
        private readonly string _relPath;
        private readonly SyntaxTree _tree;
        private readonly List<string> _typeStack = [];
        private string? _namespace;

        public List<MethodCandidate> Candidates { get; } = [];

        public MethodCandidateCollector(string relPath, SyntaxTree tree) : base(SyntaxWalkerDepth.Node)
        {
            _relPath = relPath;
            _tree = tree;
        }

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var previous = _namespace;
            _namespace = CombineNamespace(previous, node.Name.ToString());
            base.VisitNamespaceDeclaration(node);
            _namespace = previous;
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            _namespace = CombineNamespace(_namespace, node.Name.ToString());
            base.VisitFileScopedNamespaceDeclaration(node);
        }

        private static string CombineNamespace(string? outer, string inner)
            => string.IsNullOrEmpty(outer) ? inner : outer + "." + inner;

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) { PushType(node.Identifier.Text); base.VisitClassDeclaration(node); PopType(); }
        public override void VisitStructDeclaration(StructDeclarationSyntax node) { PushType(node.Identifier.Text); base.VisitStructDeclaration(node); PopType(); }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) { PushType(node.Identifier.Text); base.VisitInterfaceDeclaration(node); PopType(); }
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) { PushType(node.Identifier.Text); base.VisitRecordDeclaration(node); PopType(); }

        private void PushType(string name) => _typeStack.Add(name);
        private void PopType() => _typeStack.RemoveAt(_typeStack.Count - 1);

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // MethodDeclarationSyntax already excludes constructors, operators,
            // accessors (get/set/init/add/remove use AccessorDeclarationSyntax),
            // and local functions (LocalFunctionStatementSyntax) by construction —
            // no additional filtering is required for "ordinary method".
            string simpleName = node.Identifier.Text;
            string containingType = string.Join(".", _typeStack);
            string qualifiedName = string.Join(".", new[] { _namespace, containingType, simpleName }
                .Where(s => !string.IsNullOrEmpty(s)));

            var paramTypes = node.ParameterList.Parameters
                .Select(p => p.Type?.ToString() ?? string.Empty)
                .ToList();

            var linePos = _tree.GetLineSpan(node.Identifier.Span).StartLinePosition;
            int line1Based = linePos.Line + 1;

            Candidates.Add(new MethodCandidate(simpleName, qualifiedName, paramTypes, _relPath, line1Based));

            base.VisitMethodDeclaration(node);
        }
    }

    // ------------------------------------------------------------------
    // select-nav: apply `rg -c -w` occurrence filter (2..40) then seeded
    // Fisher-Yates shuffle (System.Random(seed), standard modern
    // Fisher-Yates: for i = n-1 downto 1, j = random.Next(0, i+1), swap).
    // ------------------------------------------------------------------

    private static int RunSelectNav(string[] args)
    {
        if (args.Length < 4)
        {
            throw new ToolUsageException("select-nav requires <corpusRoot> <candidatesJson> <outJson> [seed] [take]");
        }

        string root = Path.GetFullPath(args[1]);
        string candidatesPath = args[2];
        string outPath = args[3];
        int seed = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 20260920;
        int take = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 12;

        using var doc = JsonDocument.Parse(File.ReadAllText(candidatesPath));
        var candidatesEl = doc.RootElement.GetProperty("candidates");

        var rawCandidates = new List<(string SimpleName, string QualifiedName, List<string> ParamTypes, string Path, int Line)>();
        foreach (var el in candidatesEl.EnumerateArray())
        {
            string simpleName = el.GetProperty("simpleName").GetString()!;
            string qualifiedName = el.GetProperty("qualifiedName").GetString()!;
            string path = el.GetProperty("path").GetString()!;
            int line = el.GetProperty("line").GetInt32();
            var paramTypes = el.GetProperty("parameterTypes").EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToList();
            rawCandidates.Add((simpleName, qualifiedName, paramTypes, path, line));
        }

        var distinctNames = rawCandidates.Select(c => c.SimpleName).Distinct(StringComparer.Ordinal).ToList();
        var occurrenceByName = CountWordOccurrenceLinesBulk(root, distinctNames);

        var filtered = new List<(string SimpleName, string QualifiedName, List<string> ParamTypes, string Path, int Line, int NameLineOccurrences)>();
        foreach (var c in rawCandidates)
        {
            int occurrences = occurrenceByName.GetValueOrDefault(c.SimpleName, 0);
            if (occurrences is >= 2 and <= 40)
            {
                filtered.Add((c.SimpleName, c.QualifiedName, c.ParamTypes, c.Path, c.Line, occurrences));
            }
        }

        // Already sorted by (path ordinal, line) from nav-candidates output;
        // re-sort defensively in case of manual edits.
        filtered.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Path, b.Path);
            return c != 0 ? c : a.Line.CompareTo(b.Line);
        });

        var array = filtered.ToArray();
        var random = new Random(seed);
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = random.Next(0, i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }

        var picked = array.Take(take).Select((c, idx) => new
        {
            order = idx,
            role = idx == 0 ? "tuning" : "held-out",
            simpleName = c.SimpleName,
            qualifiedName = c.QualifiedName,
            parameterTypes = c.ParamTypes,
            path = c.Path,
            line = c.Line,
            nameLineOccurrences = c.NameLineOccurrences,
        }).ToList();

        var result = new
        {
            tool = ToolName,
            toolVersion = ToolVersion,
            seed,
            take,
            filteredPoolSize = filtered.Count,
            totalCandidatesBeforeFilter = candidatesEl.GetArrayLength(),
            shuffleAlgorithm = "Fisher-Yates (Durstenfeld), System.Random(seed), for i=n-1 downto 1: j=random.Next(0,i+1); swap(a[i],a[j])",
            occurrenceFilterRule = "rg -c -w --type cs -e <simpleName> <corpusRoot>, summed per-file counts, kept if 2 <= total <= 40 (R2-2 rule 2)",
            picked,
        };

        WriteJson(outPath, result);
        Console.WriteLine($"select-nav: wrote {outPath} ({picked.Count} picked of {filtered.Count} filtered / {candidatesEl.GetArrayLength()} total)");
        return 0;
    }

    private static readonly Regex RgOutputLineRegex = new(@"^(?<path>.+?):(?<line>\d+):(?<match>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Computes, for every name in <paramref name="names"/>, the number of
    /// distinct lines across the corpus where that name appears as a whole
    /// word — equivalent to summing `rg -c -w --type cs -e &lt;name&gt; &lt;root&gt;`
    /// per-file counts for each name individually, but done in a single
    /// ripgrep invocation (`rg -n -o -w -F --type cs -f &lt;patterns&gt; .`) using
    /// fixed-string multi-pattern matching (Aho-Corasick) so it scales to
    /// tens of thousands of candidate names on large corpora.
    /// </summary>
    private static Dictionary<string, int> CountWordOccurrenceLinesBulk(string root, IReadOnlyList<string> names)
    {
        var result = names.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return result;
        }

        string patternsFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(patternsFile, names);

            var psi = new ProcessStartInfo
            {
                FileName = "rg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = root,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("-F");
            psi.ArgumentList.Add("--type");
            psi.ArgumentList.Add("cs");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(patternsFile);
            psi.ArgumentList.Add(".");

            using var process = Process.Start(psi)!;
            var seen = new HashSet<(string Path, int Line, string Name)>();
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                var m = RgOutputLineRegex.Match(line);
                if (!m.Success)
                {
                    continue;
                }
                string path = m.Groups["path"].Value;
                int lineNo = int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture);
                string matchedName = m.Groups["match"].Value;
                if (seen.Add((path, lineNo, matchedName)) && result.ContainsKey(matchedName))
                {
                    result[matchedName]++;
                }
            }
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // rg exits 1 when there are no matches at all; that's not an error.
            if (process.ExitCode > 1)
            {
                throw new InvalidOperationException($"rg -F -f bulk occurrence scan failed (exit {process.ExitCode}): {stderr}");
            }
        }
        finally
        {
            File.Delete(patternsFile);
        }

        return result;
    }

    // ------------------------------------------------------------------
    // select-diff: R2-2 DIFF commit-pair selection (rules 1-5)
    // ------------------------------------------------------------------

    private static readonly Regex GeneratedPathRegex = new(
        @"(\.g\.cs$|\.designer\.cs$|\.generated\.cs$|/generated/|^generated/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int RunSelectDiff(string[] args)
    {
        if (args.Length < 4)
        {
            throw new ToolUsageException("select-diff requires <corpusRoot> <pinnedCommit> <outJson> [maxCommits] [pairs]");
        }

        string root = Path.GetFullPath(args[1]);
        string pinnedCommit = args[2];
        string outPath = args[3];
        int maxCommits = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 500;
        int pairsWanted = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 6;

        // Step 1: first-parent walk from pinned commit, newest first, includes
        // the pinned commit itself as the first entry.
        var commits = RunGit(root, "rev-list", "--first-parent", $"--max-count={maxCommits}", pinnedCommit)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();

        var eligible = new List<EligibleCommit>();
        foreach (var commit in commits)
        {
            string parent;
            try
            {
                parent = RunGit(root, "rev-parse", commit + "^").Trim();
            }
            catch (InvalidOperationException)
            {
                continue; // root commit with no parent; cannot be an eligible commit
            }

            var (files, addedDeleted) = DiffCsNumstat(root, parent, commit);
            if (files.Count < 1 || files.Count > 10)
            {
                continue;
            }
            if (addedDeleted < 2 || addedDeleted > 400)
            {
                continue;
            }

            string wDiff = RunGit(root, "diff", "-w", parent, commit, "--", "*.cs");
            if (string.IsNullOrWhiteSpace(wDiff))
            {
                continue;
            }

            eligible.Add(new EligibleCommit(commit, parent, files.Count, addedDeleted));
        }

        // Step 3-5: greedy newest-to-oldest pairing over the eligible list.
        var pairs = new List<DiffPair>();
        int idx = 0;
        var rejected = new List<object>();
        while (pairs.Count < pairsWanted && idx + 1 < eligible.Count)
        {
            var cPrime = eligible[idx];
            var c = eligible[idx + 1];

            var (rangeFiles, rangeAddedDeleted) = DiffCsNumstat(root, c.Commit, cPrime.Commit);
            if (rangeFiles.Count <= 20 && rangeAddedDeleted <= 800)
            {
                pairs.Add(new DiffPair(
                    P: c.Parent,
                    C: c.Commit,
                    CPrime: cPrime.Commit,
                    CFilesChanged: c.FilesChanged,
                    CLinesChanged: c.LinesChanged,
                    RangeFilesChanged: rangeFiles.Count,
                    RangeLinesChanged: rangeAddedDeleted));
                idx += 2;
            }
            else
            {
                rejected.Add(new
                {
                    reason = "tree(c)->tree(c') exceeds 20 files / 800 lines",
                    cPrimeCandidate = cPrime.Commit,
                    cCandidate = c.Commit,
                    rangeFilesChanged = rangeFiles.Count,
                    rangeLinesChanged = rangeAddedDeleted,
                });
                idx += 1;
            }
        }

        var result = new
        {
            tool = ToolName,
            toolVersion = ToolVersion,
            generatedAtUtc = DateTime.UtcNow.ToString("o"),
            pinnedCommit,
            maxCommitsWalked = commits.Count,
            eligibleCommitCount = eligible.Count,
            pairsWanted,
            pairsFound = pairs.Count,
            algorithm = "Sequential sliding window over eligible commits (newest-first): c'=eligible[i], c=eligible[i+1]; if tree(c)->tree(c') .cs changes <= 20 files and <= 800 added+deleted lines, accept pair and advance i+=2; otherwise discard c' and advance i+=1. p = first-parent(c).",
            pairs = pairs.Select(p => new
            {
                p = p.P,
                c = p.C,
                cPrime = p.CPrime,
                cFilesChanged = p.CFilesChanged,
                cLinesChanged = p.CLinesChanged,
                rangeFilesChanged = p.RangeFilesChanged,
                rangeLinesChanged = p.RangeLinesChanged,
            }).ToList(),
            rejectedCandidates = rejected,
        };

        WriteJson(outPath, result);
        Console.WriteLine($"select-diff: wrote {outPath} ({pairs.Count} of {pairsWanted} pairs found, {eligible.Count} eligible commits among {commits.Count} walked)");
        return pairs.Count < pairsWanted ? 1 : 0;
    }

    private readonly record struct EligibleCommit(string Commit, string Parent, int FilesChanged, int LinesChanged);
    private readonly record struct DiffPair(string P, string C, string CPrime, int CFilesChanged, int CLinesChanged, int RangeFilesChanged, int RangeLinesChanged);

    private static readonly Regex NumstatCountsRegex = new(@"^(?<added>-|\d+)\t(?<deleted>-|\d+)\t(?<path>.*)$", RegexOptions.Compiled);

    private static (List<string> Files, int AddedDeleted) DiffCsNumstat(string root, string from, string to)
    {
        // Uses git's own default rename-detection behavior (no -M, no
        // --no-renames) per team-lead direction: R2-2 rules 2/3 say literally
        // "git diff --numstat" with no flags, and this git installation's
        // runtime default (diff.renames unset in both the global and corpus
        // configs) is rename detection ON — empirically verified to produce
        // output identical to an explicit -M. This does depend on the
        // ambient diff.renames config default not being overridden; see
        // README.md "DIFF rename-detection default" for the verification
        // steps and that caveat.
        //
        // -z is used only to get an unambiguous, NUL-delimited encoding of
        // renames instead of the "old => new" / "dir/{old => new}/rest"
        // human-readable compaction that plain --numstat prints for renames
        // (both of which are awkward/ambiguous to parse back into a single
        // path). A rename record under -z is:
        //   "<added>\t<deleted>\t" NUL <oldPath> NUL <newPath> NUL
        // (empty third field signals "this is a rename, two paths follow").
        // A non-rename record is:
        //   "<added>\t<deleted>\t<path>" NUL
        // Per team-lead direction, renames are accounted under the NEW path
        // (post-rename name), consistent with how condition A/B's `git diff
        // -M` evidence would present the file to a reader.
        string output = RunGit(root, "diff", "--numstat", "-z", from, to, "--", "*.cs");
        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var files = new List<string>();
        int total = 0;
        int i = 0;
        while (i < tokens.Length)
        {
            var m = NumstatCountsRegex.Match(tokens[i]);
            if (!m.Success)
            {
                i++;
                continue;
            }

            string path;
            if (m.Groups["path"].Value.Length == 0)
            {
                // Rename record: next two tokens are old path, new path.
                if (i + 2 >= tokens.Length)
                {
                    break; // malformed/truncated output; stop rather than misparse
                }
                path = tokens[i + 2]; // new path, per team-lead direction
                i += 3;
            }
            else
            {
                path = m.Groups["path"].Value;
                i += 1;
            }

            if (GeneratedPathRegex.IsMatch(path.Replace('\\', '/')))
            {
                continue;
            }

            files.Add(path);

            // Binary files report "-" for added/deleted; treat as 0 (excluded
            // from the numeric line-count budget, still counted as a changed
            // file). No binary .cs files are expected in practice.
            if (int.TryParse(m.Groups["added"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int added))
            {
                total += added;
            }
            if (int.TryParse(m.Groups["deleted"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int deleted))
            {
                total += deleted;
            }
        }
        return (files, total);
    }

    private static string RunGit(string root, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        // Keep line endings/whitespace-normalization out of scope of this tool;
        // callers pass through git's own -w / -M flags where relevant.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    // ------------------------------------------------------------------
    // shared helpers
    // ------------------------------------------------------------------

    private static void WriteJson(string outPath, object value)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };
        string json = JsonSerializer.Serialize(value, options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

internal sealed class ToolUsageException(string message) : Exception(message);
