using System.Diagnostics;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Diff;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Vcs;

public sealed record GitRevisionSnapshotRequest(
    string WorkspacePath,
    string BaseRevision,
    CoverageContract Coverage,
    IReadOnlyList<SymbolContract> Symbols,
    IReadOnlyList<DiffRemarkSnapshot> Remarks,
    IReadOnlyDictionary<string, string>? Encodings = null);

public sealed record GitWorkingTreeSnapshotRequest(
    string WorkspacePath,
    CoverageContract Coverage,
    IReadOnlyList<SymbolContract> Symbols,
    IReadOnlyList<DiffRemarkSnapshot> Remarks,
    IReadOnlyDictionary<string, string>? Encodings = null,
    int MaxReadAttempts = 3);

public sealed class GitBaselineProvider
{
    public SymbolDiffSnapshot CaptureRevision(GitRevisionSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BaseRevision))
        {
            throw new DiffException(DiffErrorCodes.BaseRequired, "An explicit Git base revision is required.");
        }

        var root = ResolveRepository(request.WorkspacePath);
        var revision = ResolveRevision(root, request.BaseRevision);
        var sources = RequiredPaths(request.Symbols, request.Remarks).Select(path =>
        {
            var bytes = ReadRevisionBytes(root, revision, path);
            return new DiffSourceDocument(path, bytes, EncodingFor(path, request.Encodings), DiffDigests.Bytes(bytes));
        }).ToArray();
        var fingerprint = SnapshotFingerprint(revision, sources);
        return new SymbolDiffSnapshot(
            revision,
            fingerprint,
            CommitTime(root, revision),
            request.Coverage,
            request.Symbols,
            sources,
            request.Remarks);
    }

    public SymbolDiffSnapshot CaptureWorkingTree(GitWorkingTreeSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxReadAttempts is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxReadAttempts), "MaxReadAttempts must be between one and eight.");
        }

        var root = ResolveRepository(request.WorkspacePath);
        var sources = RequiredPaths(request.Symbols, request.Remarks).Select(path =>
        {
            var bytes = ReadWorkingTreeBytes(root, path, request.MaxReadAttempts);
            return new DiffSourceDocument(path, bytes, EncodingFor(path, request.Encodings), DiffDigests.Bytes(bytes));
        }).ToArray();
        var fingerprint = SnapshotFingerprint("working-tree", sources);
        return new SymbolDiffSnapshot(
            $"working-tree:{fingerprint[7..]}",
            fingerprint,
            DateTimeOffset.UtcNow,
            request.Coverage,
            request.Symbols,
            sources,
            request.Remarks);
    }

    public BaselineContract CreateBaseline(SymbolDiffSnapshot revisionBase, SymbolDiffSnapshot target) => new(
        BaselineKind.Vcs,
        "git",
        revisionBase.SnapshotId,
        target.SnapshotId,
        null,
        revisionBase.CapturedAt,
        revisionBase.InputFingerprint);

    public string ReadTextualDiff(string workspacePath, string baseRevision, string? targetRevision, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseRevision))
        {
            throw new DiffException(DiffErrorCodes.BaseRequired, "An explicit Git base revision is required.");
        }

        var root = ResolveRepository(workspacePath);
        var resolvedBase = ResolveRevision(root, baseRevision);
        var path = NormalizeRelativePath(relativePath);
        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--unified=3", "--no-renames", resolvedBase };
        if (!string.IsNullOrWhiteSpace(targetRevision))
        {
            arguments.Add(ResolveRevision(root, targetRevision));
        }
        arguments.Add("--");
        arguments.Add(path);
        var result = RunGit(root, arguments);
        EnsureSuccess(result, DiffErrorCodes.VcsCommandFailed, "Git textual diff failed.");
        return DecodeUtf8(result.Output, "Git textual diff output");
    }

    public byte[] ReadRevisionSource(string workspacePath, string revision, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new DiffException(DiffErrorCodes.BaseRequired, "An explicit Git base revision is required.");
        }
        var root = ResolveRepository(workspacePath);
        return ReadRevisionBytes(root, ResolveRevision(root, revision), NormalizeRelativePath(relativePath));
    }

    private static string ResolveRepository(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var result = RunGit(workspace, ["rev-parse", "--show-toplevel"]);
        EnsureSuccess(result, DiffErrorCodes.VcsNotRepository, "Workspace is not a readable Git repository.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DecodeUtf8(result.Output, "Git repository root").Trim()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(workspace, root, comparison))
        {
            throw new DiffException(DiffErrorCodes.VcsNotRepository, "Workspace must be the Git repository root for an unambiguous relative-path baseline.");
        }
        return root;
    }

    private static string ResolveRevision(string root, string revision)
    {
        var result = RunGit(root, ["rev-parse", "--verify", "--end-of-options", $"{revision}^{{commit}}"]);
        EnsureSuccess(result, DiffErrorCodes.RevisionNotFound, $"Git revision '{revision}' was not found.");
        var resolved = DecodeUtf8(result.Output, "Git revision").Trim();
        if (resolved.Length != 40 || resolved.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new DiffException(DiffErrorCodes.RevisionNotFound, "Git returned an invalid commit identity.");
        }
        return resolved.ToLowerInvariant();
    }

    private static DateTimeOffset CommitTime(string root, string revision)
    {
        var result = RunGit(root, ["show", "-s", "--format=%cI", revision]);
        EnsureSuccess(result, DiffErrorCodes.RevisionNotFound, "Git commit timestamp could not be read.");
        if (!DateTimeOffset.TryParse(DecodeUtf8(result.Output, "Git commit timestamp").Trim(), out var timestamp))
        {
            throw new DiffException(DiffErrorCodes.VcsCommandFailed, "Git returned an invalid commit timestamp.");
        }
        return timestamp;
    }

    private static byte[] ReadRevisionBytes(string root, string revision, string relativePath)
    {
        var path = NormalizeRelativePath(relativePath);
        var result = RunGit(root, ["show", "--no-ext-diff", "--no-textconv", $"{revision}:{path}"]);
        EnsureSuccess(result, DiffErrorCodes.SourceMissing, $"Git source '{path}' is missing at revision '{revision}'.");
        return result.Output;
    }

    private static byte[] ReadWorkingTreeBytes(string root, string relativePath, int attempts)
    {
        var normalized = NormalizeRelativePath(relativePath);
        string fullPath;
        try
        {
            fullPath = PathBoundary.ResolveRelative(root, normalized);
        }
        catch (StorageException exception)
        {
            throw new DiffException(DiffErrorCodes.SourceMissing, "Working-tree source escapes the repository root or crosses a reparse point.", exception);
        }

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!File.Exists(fullPath))
            {
                throw new DiffException(DiffErrorCodes.SourceMissing, $"Working-tree source '{normalized}' is missing.");
            }
            var before = new FileInfo(fullPath);
            var bytes = File.ReadAllBytes(fullPath);
            var after = new FileInfo(fullPath);
            if (before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc && bytes.LongLength == after.Length)
            {
                return bytes;
            }
        }
        throw new DiffException(DiffErrorCodes.SourceStale, $"Working-tree source '{normalized}' remained unstable.");
    }

    private static IReadOnlyList<string> RequiredPaths(IReadOnlyList<SymbolContract> symbols, IReadOnlyList<DiffRemarkSnapshot> remarks) =>
        symbols.SelectMany(symbol => symbol.Declarations).Select(declaration => declaration.Location.Path)
            .Concat(remarks.Select(remark => remark.Location.Path))
            .Where(path => path is not null)
            .Select(path => NormalizeRelativePath(path!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) || normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new DiffException(DiffErrorCodes.SourceMissing, "Git source path must be a normalized repository-relative path.");
        }
        return normalized;
    }

    private static string EncodingFor(string path, IReadOnlyDictionary<string, string>? encodings)
    {
        if (encodings is null) return "utf-8";
        var pair = encodings.FirstOrDefault(item => string.Equals(NormalizeRelativePath(item.Key), path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(pair.Value) ? "utf-8" : pair.Value;
    }

    private static string SnapshotFingerprint(string identity, IReadOnlyList<DiffSourceDocument> sources) =>
        DiffDigests.Utf8(string.Join("\n", new[] { identity }.Concat(sources.OrderBy(source => source.Path, StringComparer.Ordinal)
            .Select(source => $"{source.Path}\n{source.ContentHash}"))));

    private static GitResult RunGit(string workingDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.quotepath=false");
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(workingDirectory);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git process could not be started.");
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            return new GitResult(process.ExitCode, output.ToArray(), error);
        }
        catch (DiffException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new DiffException(DiffErrorCodes.VcsCommandFailed, "Git read-only command could not be executed.", exception);
        }
    }

    private static void EnsureSuccess(GitResult result, string errorCode, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new DiffException(errorCode, string.IsNullOrWhiteSpace(result.Error) ? message : $"{message} {result.Error.Trim()}");
        }
    }

    private static string DecodeUtf8(byte[] bytes, string source)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception)
        {
            throw new DiffException(DiffErrorCodes.VcsCommandFailed, $"{source} is not valid UTF-8.", exception);
        }
    }

    private sealed record GitResult(int ExitCode, byte[] Output, string Error);
}