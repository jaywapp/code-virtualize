using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Snapshots;

namespace CodeVirtualize.Core.Diff;

public sealed record SessionSnapshotDiffRequest(
    string StorePath,
    string SessionId,
    CoverageContract Coverage,
    IReadOnlyList<SymbolContract> Symbols,
    IReadOnlyList<DiffRemarkSnapshot> Remarks);

public sealed class SessionBaselineProvider
{
    private readonly SessionSnapshotStore store;

    public SessionBaselineProvider(SessionSnapshotStore? store = null) =>
        this.store = store ?? new SessionSnapshotStore();

    public SymbolDiffSnapshot Capture(SessionSnapshotDiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new DiffException(DiffErrorCodes.SessionBaseMissing, "A session ID is required for a session baseline.");
        }

        try
        {
            var manifest = store.Open(request.StorePath, request.SessionId);
            var paths = RequiredPaths(request.Symbols, request.Remarks);
            var files = manifest.Files.ToDictionary(
                file => NormalizePath(file.Path),
                file => file,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var sources = new List<DiffSourceDocument>(paths.Count);
            foreach (var path in paths)
            {
                if (!files.TryGetValue(path, out var file))
                {
                    throw new DiffException(DiffErrorCodes.SourceMissing, $"Session baseline does not contain '{path}'.");
                }

                var bytes = store.ReadSource(request.StorePath, request.SessionId, path);
                sources.Add(new DiffSourceDocument(path, bytes, file.Encoding, file.ContentHash));
            }

            var fingerprint = SnapshotFingerprint(request.SessionId, sources);
            return new SymbolDiffSnapshot(
                $"session-snapshot:{fingerprint[7..]}",
                fingerprint,
                manifest.CapturedAt,
                request.Coverage,
                request.Symbols,
                sources,
                request.Remarks);
        }
        catch (DiffException)
        {
            throw;
        }
        catch (SnapshotException exception) when (exception.ErrorCode == SnapshotErrorCodes.SessionNotFound)
        {
            throw new DiffException(DiffErrorCodes.SessionBaseMissing, "The immutable session baseline is missing.", exception);
        }
        catch (SnapshotException exception)
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, "The session baseline failed validation.", exception);
        }
    }

    public static BaselineContract CreateBaseline(
        SymbolDiffSnapshot sessionBase,
        SymbolDiffSnapshot target,
        string sessionId) => new(
            BaselineKind.Session,
            "session",
            sessionBase.SnapshotId,
            target.SnapshotId,
            sessionId,
            sessionBase.CapturedAt,
            sessionBase.InputFingerprint);

    private static IReadOnlyList<string> RequiredPaths(
        IReadOnlyList<SymbolContract> symbols,
        IReadOnlyList<DiffRemarkSnapshot> remarks) =>
        symbols.SelectMany(symbol => symbol.Declarations).Select(item => item.Location.Path)
            .Concat(remarks.Select(item => item.Location.Path))
            .Where(path => path is not null)
            .Select(path => NormalizePath(path!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string SnapshotFingerprint(string sessionId, IReadOnlyList<DiffSourceDocument> sources) =>
        DiffDigests.Utf8(string.Join("\n", new[] { sessionId }.Concat(sources.OrderBy(source => source.Path, StringComparer.Ordinal)
            .Select(source => $"{source.Path}\n{source.ContentHash}"))));

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}