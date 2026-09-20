using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Diff;
using CodeVirtualize.Core.Snapshots;
using CodeVirtualize.Core.Vcs;

namespace CodeVirtualize.Integration.Tests.Diff;

internal static class DiffTests
{
    private const string SourcePath = "ReviewTarget.cs";
    private const string AnalysisKey = "analysis-001";

    public static void Run()
    {
        using var fixture = new GitDiffFixture();
        var provider = new GitBaselineProvider();
        var service = new SymbolDiffService();

        fixture.Write(FixtureSource("base", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false));
        fixture.CommitAndTag("base");
        var baseModel = Model(fixture.Read(), "base", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false);
        var vcsBase = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "base", Coverage(), baseModel.Symbols, baseModel.Remarks));

        fixture.Write(FixtureSource("dirty-before-session", "Dirty remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false));
        var dirtySource = fixture.Read();
        var dirtyModel = Model(dirtySource, "dirty-before-session", "Dirty remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false);
        var snapshotStorePath = Path.Combine(fixture.RootPath, "snapshot-store");
        new SessionSnapshotStore().Capture(new SessionSnapshotRequest(
            fixture.RepositoryPath,
            snapshotStorePath,
            "session-001",
            "gen-dirty-start",
            AnalysisKey,
            HashUtf8(dirtySource),
            [new SessionSnapshotSource(SourcePath, HashUtf8(dirtySource), "utf-8", ["project-001"])],
            new Dictionary<string, string>()));
        var sessionBase = new SessionBaselineProvider().Capture(new SessionSnapshotDiffRequest(
            snapshotStorePath, "session-001", Coverage(), dirtyModel.Symbols, dirtyModel.Remarks));

        fixture.Write(FixtureSource("dirty-before-session", "Current remark", "current", includeRemoved: false, "string", "RenamedCandidate", includeAdded: true));
        var currentSource = fixture.Read();
        var currentModel = Model(currentSource, "dirty-before-session", "Current remark", "current", includeRemoved: false, "string", "RenamedCandidate", includeAdded: true);
        var current = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, Coverage(), currentModel.Symbols, currentModel.Remarks));

        var vcsRequest = new SymbolDiffRequest(provider.CreateBaseline(vcsBase, current), vcsBase, current);
        var sessionRequest = new SymbolDiffRequest(
            SessionBaselineProvider.CreateBaseline(sessionBase, current, "session-001"), sessionBase, current);
        var vcsResult = service.Compare(vcsRequest);
        var sessionResult = service.Compare(sessionRequest);

        Assert(vcsResult.Contract.Baseline.Kind == BaselineKind.Vcs, "VCS diff must preserve its explicit baseline kind.");
        Assert(sessionResult.Contract.Baseline.Kind == BaselineKind.Session, "Session diff must preserve its explicit baseline kind.");
        Assert(Has(vcsResult, DiffKind.BodyChanged, "Existing"), "VCS diff must include changes already dirty when the session started.");
        Assert(!Has(sessionResult, DiffKind.BodyChanged, "Existing"), "Session diff must exclude changes already present in the captured dirty start.");
        Assert(Has(sessionResult, DiffKind.Added, "AddedDuringSession"), "Session diff must include symbols added during the session.");
        Assert(Has(sessionResult, DiffKind.Deleted, "Removed"), "Session diff must include removed symbols.");
        Assert(Has(sessionResult, DiffKind.SignatureChanged, "Signature"), "Signature changes must be paired conservatively.");
        Assert(Has(sessionResult, DiffKind.BodyChanged, "Body"), "Body changes must be distinct from dirty-start changes.");
        Assert(Has(sessionResult, DiffKind.RemarkChanged, "Existing"), "Remark changes must be explicit.");
        Assert(Has(sessionResult, DiffKind.RenameCandidate, "RenameMe"), "Uncertain rename must be a candidate rather than a confirmed rename.");

        var deleted = sessionResult.Changes.Single(change => change.Kind == DiffKind.Deleted && change.BaseSymbol?.Name == "Removed");
        Assert(deleted.BaseSource?.Contains("Removed", StringComparison.Ordinal) == true && deleted.TargetSource is null,
            "A deleted symbol must retain validated base source without reading current source as a substitute.");
        Assert(sessionResult.Changes.All(change => change.Contract.Evidence.All(evidence => !string.IsNullOrWhiteSpace(evidence.TextualHunk))),
            "Every symbol change must carry textual hunk evidence.");

        var gitText = provider.ReadTextualDiff(fixture.RepositoryPath, "base", null, SourcePath);
        Assert(gitText.Contains("dirty-before-session", StringComparison.Ordinal) && gitText.Contains("AddedDuringSession", StringComparison.Ordinal),
            "Read-only Git diff must include dirty-start and during-session working-tree changes.");

        ContractRoundTripAndUnknownKind(vcsResult.Contract);
        ErrorCases(provider, service, fixture, baseModel, current, currentModel);
        ModeSwitchRejectsStaleResponse(vcsRequest, vcsResult, sessionRequest, sessionResult).GetAwaiter().GetResult();
    }

    private static void ContractRoundTripAndUnknownKind(DiffContract contract)
    {
        var json = ContractJson.Serialize(contract);
        var restored = ContractJson.Deserialize<DiffContract>(json);
        Assert(restored.Entries.Any(entry => entry.Kind == DiffKind.RemarkChanged) &&
               restored.Entries.Any(entry => entry.Kind == DiffKind.RenameCandidate),
            "New diff kinds must round-trip without changing schema version.");
        ThrowsContract(
            () => ContractJson.Deserialize<DiffContract>(json.Replace("\"rename_candidate\"", "\"unknown_future_kind\"", StringComparison.Ordinal)),
            "CONTRACT_INVALID");
    }

    private static void ErrorCases(
        GitBaselineProvider provider,
        SymbolDiffService service,
        GitDiffFixture fixture,
        SnapshotModel baseModel,
        SymbolDiffSnapshot current,
        SnapshotModel currentModel)
    {
        ThrowsDiff(() => provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "", Coverage(), baseModel.Symbols, baseModel.Remarks)), DiffErrorCodes.BaseRequired);
        ThrowsDiff(() => provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "branch-that-does-not-exist", Coverage(), baseModel.Symbols, baseModel.Remarks)), DiffErrorCodes.RevisionNotFound);
        ThrowsDiff(() => new SessionBaselineProvider().Capture(new SessionSnapshotDiffRequest(
            Path.Combine(fixture.RootPath, "missing-store"), "missing-session", Coverage(), baseModel.Symbols, baseModel.Remarks)), DiffErrorCodes.SessionBaseMissing);

        var staleSymbol = baseModel.Symbols[0];
        var staleDeclaration = staleSymbol.Declarations[0] with
        {
            Location = staleSymbol.Declarations[0].Location with { ContentHash = $"sha256:{new string('f', 64)}" }
        };
        var staleModel = baseModel with
        {
            Symbols = [staleSymbol with { Declarations = [staleDeclaration] }, .. baseModel.Symbols.Skip(1)]
        };
        var staleBase = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "base", Coverage(), staleModel.Symbols, staleModel.Remarks));
        ThrowsDiff(() => service.Compare(new SymbolDiffRequest(provider.CreateBaseline(staleBase, current), staleBase, current)), DiffErrorCodes.SourceStale);

        var invalidSymbol = currentModel.Symbols[0];
        var invalidDeclaration = invalidSymbol.Declarations[0] with
        {
            Location = invalidSymbol.Declarations[0].Location with
            {
                Span = invalidSymbol.Declarations[0].Location.Span with { Start = invalidSymbol.Declarations[0].Location.Span.Start + 1 }
            }
        };
        var invalidSnapshot = current with
        {
            Symbols = [invalidSymbol with { Declarations = [invalidDeclaration] }, .. currentModel.Symbols.Skip(1)]
        };
        ThrowsDiff(() => service.Compare(new SymbolDiffRequest(provider.CreateBaseline(current, invalidSnapshot), current, invalidSnapshot)), DiffErrorCodes.InvalidSnapshot);
    }

    private static async Task ModeSwitchRejectsStaleResponse(
        SymbolDiffRequest vcsRequest,
        SymbolDiffResult vcsResult,
        SymbolDiffRequest sessionRequest,
        SymbolDiffResult sessionResult)
    {
        var coordinator = new DiffRequestCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequest = coordinator.CompareAsync(vcsRequest, async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return vcsResult;
        });
        var current = await coordinator.CompareAsync(sessionRequest, (_, _) => Task.FromResult(sessionResult)).ConfigureAwait(false);
        Assert(current.Contract.Baseline.Kind == BaselineKind.Session, "The latest selected baseline mode must complete.");
        gate.SetResult();
        await ThrowsDiffAsync(async () => await oldRequest.ConfigureAwait(false), DiffErrorCodes.StaleResponse).ConfigureAwait(false);
    }

    private static bool Has(SymbolDiffResult result, DiffKind kind, string name) =>
        result.Changes.Any(change => change.Kind == kind && (change.BaseSymbol?.Name == name || change.TargetSymbol?.Name == name));

    private static SnapshotModel Model(
        string source,
        string existingValue,
        string remark,
        string bodyValue,
        bool includeRemoved,
        string signatureType,
        string renameName,
        bool includeAdded)
    {
        var symbols = new List<SymbolContract>
        {
            Symbol(source, "Existing", "public static string Existing()", $"public static string Existing()\n    {{\n        return \"{existingValue}\";\n    }}", []),
            Symbol(source, "Signature", $"public static string Signature({signatureType} value)", $"public static string Signature({signatureType} value) => value.ToString();",
                [new ParameterIdentityContract(signatureType == "int" ? "System.Int32" : "System.String", ParameterRefKind.None)]),
            Symbol(source, renameName, $"public static string {renameName}()", $"public static string {renameName}() => \"same\";", []),
            Symbol(source, "Body", "public static string Body()", $"public static string Body() => \"{bodyValue}\";", [])
        };
        if (includeRemoved) symbols.Add(Symbol(source, "Removed", "public static string Removed()", "public static string Removed() => \"gone\";", []));
        if (includeAdded) symbols.Add(Symbol(source, "AddedDuringSession", "public static string AddedDuringSession()", "public static string AddedDuringSession() => \"added\";", []));
        var existing = symbols.Single(symbol => symbol.Name == "Existing");
        var remarkText = $"/// <summary>{remark}</summary>";
        return new SnapshotModel(symbols.OrderBy(symbol => symbol.Name, StringComparer.Ordinal).ToArray(),
            [new DiffRemarkSnapshot(existing.SymbolId, Location(source, remarkText))]);
    }

    private static SymbolContract Symbol(
        string source,
        string name,
        string signature,
        string declaration,
        IReadOnlyList<ParameterIdentityContract> parameters)
    {
        var identity = new SymbolIdentityContract("project-identity", AnalysisKey, "method", $"Fixture.ReviewTarget.{name}", 0, parameters, null);
        var symbolId = DeterministicSymbolId.Create(identity);
        return new SymbolContract(symbolId, "project-001", AnalysisKey, "method", name, $"Fixture.ReviewTarget.{name}", signature,
            "public", null, 0, IdentityQuality.Semantic, parameters, null,
            [new DeclarationContract(symbolId, Location(source, declaration), DocumentKind.Source)], []);
    }

    private static LocationContract Location(string source, string value)
    {
        var start = source.IndexOf(value, StringComparison.Ordinal);
        Assert(start >= 0, $"Fixture fragment not found: {value}");
        return new LocationContract("file-review-target", HashUtf8(source),
            new TextSpanContract(start, value.Length, LineAt(source, start), LineAt(source, start + value.Length)), SourcePath, null);
    }

    private static string FixtureSource(
        string existingValue,
        string remark,
        string bodyValue,
        bool includeRemoved,
        string signatureType,
        string renameName,
        bool includeAdded)
    {
        var removed = includeRemoved ? "\n    public static string Removed() => \"gone\";\n" : string.Empty;
        var added = includeAdded ? "\n    public static string AddedDuringSession() => \"added\";\n" : string.Empty;
        return $$"""
            namespace Fixture;
            public static class ReviewTarget
            {
                public const string Emoji = "😀";
                /// <summary>{{remark}}</summary>
                public static string Existing()
                {
                    return "{{existingValue}}";
                }
            {{removed}}
                public static string Signature({{signatureType}} value) => value.ToString();
                public static string {{renameName}}() => "same";
                public static string Body() => "{{bodyValue}}";
            {{added}}}
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static CoverageContract Coverage() => new(
        "static-csharp-selected-configuration", CoverageLevel.CompleteWithinScope, 1, 0, 0, 0, [], ["dynamic-references-not-covered"], false);

    private static string HashUtf8(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static int LineAt(string text, int offset)
    {
        var line = 1;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\r')
            {
                line++;
                if (index + 1 < offset && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n') line++;
        }
        return line;
    }

    private static void ThrowsDiff(Action action, string code)
    {
        try { action(); }
        catch (DiffException exception) when (exception.ErrorCode == code) { return; }
        throw new InvalidOperationException($"Expected DiffException '{code}'.");
    }

    private static async Task ThrowsDiffAsync(Func<Task> action, string code)
    {
        try { await action().ConfigureAwait(false); }
        catch (DiffException exception) when (exception.ErrorCode == code) { return; }
        throw new InvalidOperationException($"Expected DiffException '{code}'.");
    }

    private static void ThrowsContract(Action action, string code)
    {
        try { action(); }
        catch (ContractValidationException exception) when (exception.ErrorCode == code) { return; }
        throw new InvalidOperationException($"Expected ContractValidationException '{code}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record SnapshotModel(IReadOnlyList<SymbolContract> Symbols, IReadOnlyList<DiffRemarkSnapshot> Remarks);

    private sealed class GitDiffFixture : IDisposable
    {
        public GitDiffFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"code-virtualize-diff-{Guid.NewGuid():N}");
            RepositoryPath = Path.Combine(RootPath, "repo");
            Directory.CreateDirectory(RepositoryPath);
            Git("init", "--initial-branch=main");
            Git("config", "user.email", "fixture@example.invalid");
            Git("config", "user.name", "Code Virtualize Fixture");
        }

        public string RootPath { get; }
        public string RepositoryPath { get; }

        public void Write(string source) =>
            File.WriteAllText(Path.Combine(RepositoryPath, SourcePath), source, new UTF8Encoding(false));

        public string Read() => File.ReadAllText(Path.Combine(RepositoryPath, SourcePath), new UTF8Encoding(false, true));

        public void CommitAndTag(string tag)
        {
            Git("add", "--", SourcePath);
            Git("commit", "-m", "fixture base");
            Git("tag", tag);
        }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath)) return;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!root.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Diff fixture cleanup escaped the temporary directory.");
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }

        private void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepositoryPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Git fixture command.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Git fixture command failed: {output} {error}");
        }
    }
}