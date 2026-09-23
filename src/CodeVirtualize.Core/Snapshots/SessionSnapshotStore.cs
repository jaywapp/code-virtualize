using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Snapshots;

public static class SnapshotErrorCodes
{
    public const string SessionExists = "SESSION_EXISTS";
    public const string SessionNotFound = "SESSION_BASE_MISSING";
    public const string SourceMissing = "SOURCE_FILE_MISSING";
    public const string SourceUnstable = "SOURCE_UNSTABLE";
    public const string SnapshotCorrupt = "SESSION_SNAPSHOT_CORRUPT";
    public const string PathOutsideWorkspace = "PATH_OUTSIDE_WORKSPACE";
}

public sealed class SnapshotException : Exception
{
    public SnapshotException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    public SnapshotException(string errorCode, string message, Exception innerException)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}

public enum SessionState
{
    Active,
    Closed
}

public sealed record SessionSnapshotSource(
    string Path,
    string ContentHash,
    string Encoding,
    IReadOnlyList<string> ProjectIds);

public sealed record SessionSnapshotRequest(
    string WorkspacePath,
    string StorePath,
    string SessionId,
    string GenerationId,
    string AnalysisKey,
    string InputFingerprint,
    IReadOnlyList<SessionSnapshotSource> Sources,
    IReadOnlyDictionary<string, string> Configuration,
    int MaxReadAttempts = 3);

public sealed record SessionSnapshotFile(
    string Path,
    string ContentHash,
    long ByteLength,
    string Encoding,
    string BlobPath,
    IReadOnlyList<string> ProjectIds);

public sealed record SessionSnapshotManifest(
    string Schema,
    int SchemaVersion,
    string SessionId,
    string GenerationId,
    string AnalysisKey,
    string InputFingerprint,
    DateTimeOffset CapturedAt,
    IReadOnlyList<SessionSnapshotFile> Files,
    IReadOnlyDictionary<string, string> Configuration);

public sealed record SessionMetadata(
    string Schema,
    int SchemaVersion,
    string SessionId,
    SessionState State,
    string GenerationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt);

public sealed class SessionSnapshotStore
{
    private const string SnapshotSchema = "code-virtualize/session-snapshot";
    private const string SessionSchema = "code-virtualize/session";
    private const int SchemaVersion = 1;
    private const long MaxSourceBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = false
    };

    public SessionSnapshotManifest Capture(SessionSnapshotRequest request)
    {
        ValidateRequest(request);
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.WorkspacePath));
        var store = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.StorePath));
        Directory.CreateDirectory(store);
        var sessionsRoot = Path.Combine(store, "sessions");
        var pinsRoot = Path.Combine(store, "pins", "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(pinsRoot);

        var sessionPath = ResolveSession(sessionsRoot, request.SessionId);
        if (Directory.Exists(sessionPath) || File.Exists(sessionPath))
        {
            throw new SnapshotException(SnapshotErrorCodes.SessionExists, "The session snapshot already exists.");
        }

        var temporaryPath = Path.Combine(sessionsRoot, $".tmp-{Guid.NewGuid():N}");
        var committed = false;
        try
        {
            Directory.CreateDirectory(temporaryPath);
            var blobsRoot = Path.Combine(temporaryPath, "baseline-source");
            Directory.CreateDirectory(blobsRoot);
            var files = new List<SessionSnapshotFile>(request.Sources.Count);
            foreach (var source in request.Sources.OrderBy(source => source.Path, StringComparer.Ordinal))
            {
                var normalized = Normalize(source.Path);
                var physicalPath = ResolveWorkspacePath(workspace, normalized);
                var bytes = ReadStable(physicalPath, source.ContentHash, request.MaxReadAttempts);
                var contentHash = Digest(bytes);
                var blobRelative = $"baseline-source/{contentHash[7..]}.source";
                var blobPath = Path.Combine(temporaryPath, blobRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(blobPath))
                {
                    WriteNew(blobPath, bytes);
                }

                files.Add(new SessionSnapshotFile(
                    normalized,
                    contentHash,
                    bytes.LongLength,
                    source.Encoding,
                    blobRelative,
                    source.ProjectIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
            }

            var capturedAt = DateTimeOffset.UtcNow;
            var manifest = new SessionSnapshotManifest(
                SnapshotSchema,
                SchemaVersion,
                request.SessionId,
                request.GenerationId,
                request.AnalysisKey,
                request.InputFingerprint,
                capturedAt,
                files,
                new SortedDictionary<string, string>(request.Configuration.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal), StringComparer.Ordinal));
            var baselinePath = Path.Combine(temporaryPath, "baseline.json");
            var baselineBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            WriteNew(baselinePath, baselineBytes);
            WriteNew(Path.Combine(temporaryPath, "baseline.sha256"), Encoding.ASCII.GetBytes(Digest(baselineBytes)));
            WriteJson(Path.Combine(temporaryPath, "session.json"), new SessionMetadata(
                SessionSchema,
                SchemaVersion,
                request.SessionId,
                SessionState.Active,
                request.GenerationId,
                capturedAt,
                null));

            Directory.Move(temporaryPath, sessionPath);
            committed = true;
            WritePin(Path.Combine(pinsRoot, $"{request.SessionId}.pin"), manifest);
            return manifest;
        }
        catch (SnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "Session snapshot capture failed.", exception);
        }
        finally
        {
            if (!committed && Directory.Exists(temporaryPath))
            {
                try { Directory.Delete(temporaryPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public SessionSnapshotManifest Open(string storePath, string sessionId)
    {
        ValidateSessionId(sessionId);
        var path = Path.Combine(ResolveSession(Path.Combine(Path.GetFullPath(storePath), "sessions"), sessionId), "baseline.json");
        if (!File.Exists(path))
        {
            throw new SnapshotException(SnapshotErrorCodes.SessionNotFound, "The session baseline snapshot is missing.");
        }

        try
        {
            var baselineBytes = File.ReadAllBytes(path);
            var digestPath = Path.Combine(Path.GetDirectoryName(path)!, "baseline.sha256");
            var expectedDigest = File.Exists(digestPath) ? File.ReadAllText(digestPath, Encoding.ASCII) : string.Empty;
            if (!string.Equals(Digest(baselineBytes), expectedDigest, StringComparison.Ordinal))
            {
                throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session baseline snapshot digest is invalid.");
            }

            var manifest = JsonSerializer.Deserialize<SessionSnapshotManifest>(baselineBytes, JsonOptions)
                ?? throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session baseline snapshot is empty.");
            if (manifest.Schema != SnapshotSchema || manifest.SchemaVersion != SchemaVersion || manifest.SessionId != sessionId)
            {
                throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session baseline snapshot identity is invalid.");
            }

            return manifest;
        }
        catch (SnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session baseline snapshot cannot be read.", exception);
        }
    }

    public byte[] ReadSource(string storePath, string sessionId, string relativePath)
    {
        var manifest = Open(storePath, sessionId);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var file = manifest.Files.SingleOrDefault(item => comparer.Equals(item.Path, Normalize(relativePath)))
            ?? throw new SnapshotException(SnapshotErrorCodes.SourceMissing, "The path is not present in the session inventory.");
        var sessionPath = ResolveSession(Path.Combine(Path.GetFullPath(storePath), "sessions"), sessionId);
        var blobPath = Path.GetFullPath(Path.Combine(sessionPath, file.BlobPath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(sessionPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!blobPath.StartsWith(prefix, comparison) || !File.Exists(blobPath))
        {
            throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session source blob is missing or outside the session.");
        }

        var bytes = File.ReadAllBytes(blobPath);
        if (bytes.LongLength != file.ByteLength || Digest(bytes) != file.ContentHash)
        {
            throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session source blob failed validation.");
        }

        return bytes;
    }

    public void Close(string storePath, string sessionId)
    {
        var manifest = Open(storePath, sessionId);
        var sessionPath = ResolveSession(Path.Combine(Path.GetFullPath(storePath), "sessions"), sessionId);
        var metadataPath = Path.Combine(sessionPath, "session.json");
        SessionMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllBytes(metadataPath), JsonOptions)
                ?? throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session metadata is empty.");
        }
        catch (SnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session metadata cannot be read.", exception);
        }

        if (metadata.SessionId != sessionId || metadata.GenerationId != manifest.GenerationId)
        {
            throw new SnapshotException(SnapshotErrorCodes.SnapshotCorrupt, "The session metadata identity is invalid.");
        }

        if (metadata.State != SessionState.Closed)
        {
            ReplaceJson(metadataPath, metadata with { State = SessionState.Closed, ClosedAt = DateTimeOffset.UtcNow });
        }

        var pinPath = Path.Combine(Path.GetFullPath(storePath), "pins", "sessions", $"{sessionId}.pin");
        if (File.Exists(pinPath))
        {
            File.Delete(pinPath);
        }
    }

    public bool IsPinned(string storePath, string sessionId)
    {
        ValidateSessionId(sessionId);
        return File.Exists(Path.Combine(Path.GetFullPath(storePath), "pins", "sessions", $"{sessionId}.pin"));
    }

    private static byte[] ReadStable(string path, string expectedHash, int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!File.Exists(path))
            {
                throw new SnapshotException(SnapshotErrorCodes.SourceMissing, "A source file disappeared during session capture.");
            }

            var before = new FileInfo(path);
            if (before.Length > MaxSourceBytes)
            {
                throw new SnapshotException(SnapshotErrorCodes.SourceUnstable, "A source file exceeds the session snapshot size limit.");
            }

            var bytes = File.ReadAllBytes(path);
            var after = new FileInfo(path);
            var hash = Digest(bytes);
            if (before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc &&
                bytes.LongLength == after.Length && hash == expectedHash)
            {
                return bytes;
            }

            if (hash != expectedHash && before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc)
            {
                throw new SnapshotException(SnapshotErrorCodes.SourceUnstable, "Source changed after the generation used to start the session.");
            }
        }

        throw new SnapshotException(SnapshotErrorCodes.SourceUnstable, "Source remained unstable during bounded session capture.");
    }

    private static string ResolveWorkspacePath(string workspace, string relativePath)
    {
        try
        {
            var path = SourceResolver.WorkspacePath(workspace, relativePath);
            PathBoundary.EnsureExistingPathHasNoReparsePoints(workspace, path);
            return path;
        }
        catch (Exception exception) when (exception is ArgumentException or StorageException)
        {
            throw new SnapshotException(SnapshotErrorCodes.PathOutsideWorkspace, "A session source path escapes the workspace boundary.", exception);
        }
    }

    private static string ResolveSession(string sessionsRoot, string sessionId)
    {
        ValidateSessionId(sessionId);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot));
        var result = Path.GetFullPath(Path.Combine(root, sessionId));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new SnapshotException(SnapshotErrorCodes.PathOutsideWorkspace, "The session path escapes the session root.");
        }

        return result;
    }

    private static void ValidateRequest(SessionSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StorePath);
        ValidateSessionId(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GenerationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AnalysisKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputFingerprint);
        ArgumentNullException.ThrowIfNull(request.Sources);
        ArgumentNullException.ThrowIfNull(request.Configuration);
        if (request.MaxReadAttempts is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxReadAttempts), "MaxReadAttempts must be between one and eight.");
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (request.Sources.Any(source => string.IsNullOrWhiteSpace(source.Path) ||
                                          string.IsNullOrWhiteSpace(source.ContentHash) ||
                                          string.IsNullOrWhiteSpace(source.Encoding)) ||
            request.Sources.Select(source => Normalize(source.Path)).Distinct(comparer).Count() != request.Sources.Count)
        {
            throw new ArgumentException("Session sources must have unique relative paths, hashes, and encodings.");
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 96 ||
            !char.IsAsciiLetterOrDigit(sessionId[0]) ||
            sessionId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Session ID must be a safe path segment.");
        }
    }

    private static void WritePin(string path, SessionSnapshotManifest manifest)
    {
        if (File.Exists(path))
        {
            throw new SnapshotException(SnapshotErrorCodes.SessionExists, "The session pin already exists.");
        }

        WriteJson(path, new
        {
            schema = "code-virtualize/session-pin",
            schemaVersion = SchemaVersion,
            manifest.SessionId,
            manifest.GenerationId,
            manifest.InputFingerprint,
            pinnedAt = DateTimeOffset.UtcNow
        });
    }

    private static void WriteJson<T>(string path, T value) =>
        WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static void ReplaceJson<T>(string path, T value)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            WriteNew(temporary, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
            TransientFileConflict.Retry(() => File.Move(temporary, path, overwrite: true));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static string Normalize(string path) => path.Replace('\\', '/');
}