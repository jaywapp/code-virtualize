using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Storage;

public sealed class GenerationStore
{
    private const long MaxManifestByteLength = 16L * 1024 * 1024;
    private const long MaxShardByteLength = 256L * 1024 * 1024;
    private const int MaxRecordByteLength = 8 * 1024 * 1024;
    private const int MaxShardRecordCount = 10_000_000;
    private const string ManifestFileName = "manifest.cv";
    private const string PinFileName = ".reader.pin";
    private const string PointerFileName = "current.json";
    private const string PointerSchema = "code-virtualize/current-pointer";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions PointerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 16
    };

    private readonly IStorageFaultInjector faultInjector;
    private readonly string generationsRoot;
    private readonly string storeRoot;

    public GenerationStore(string storeRoot, IStorageFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        this.storeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeRoot));
        this.faultInjector = faultInjector ?? NoStorageFaultInjector.Instance;

        Directory.CreateDirectory(this.storeRoot);
        PathBoundary.EnsureNotReparsePoint(this.storeRoot);
        generationsRoot = PathBoundary.ResolveChild(this.storeRoot, "generations");
        Directory.CreateDirectory(generationsRoot);
        PathBoundary.EnsureNotReparsePoint(generationsRoot);
    }

    public string StoreRoot => storeRoot;

    public ManifestContract Publish(GenerationPublishRequest request, TimeSpan? writerWait = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Manifest);
        ArgumentNullException.ThrowIfNull(request.Shards);
        ValidateGenerationId(request.Manifest.GenerationId);
        if (request.Manifest.Shards.Count != 0)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "The store computes shard metadata; the input manifest shard list must be empty.");
        }

        request.Manifest.Validate();
        ValidateShardRequests(request.Shards);

        using var writerLease = AcquireWriterLease(writerWait ?? TimeSpan.Zero);
        var generationPath = PathBoundary.ResolveChild(generationsRoot, request.Manifest.GenerationId);
        if (Directory.Exists(generationPath) || File.Exists(generationPath))
        {
            throw new StorageException(StorageErrorCodes.GenerationExists, "The requested immutable generation already exists.");
        }

        var temporaryPath = PathBoundary.ResolveChild(generationsRoot, $".tmp-{Guid.NewGuid():N}");
        var generationCommitted = false;
        try
        {
            Directory.CreateDirectory(temporaryPath);
            CreateDurableEmptyFile(PathBoundary.ResolveChild(temporaryPath, PinFileName));

            var shardContracts = new List<ManifestShardContract>(request.Shards.Count);
            foreach (var shard in request.Shards)
            {
                var shardPath = PathBoundary.ResolveRelative(temporaryPath, shard.RelativePath);
                var parent = Path.GetDirectoryName(shardPath)
                    ?? throw new StorageException(StorageErrorCodes.PathOutsideStore, "A shard path has no parent directory.");
                Directory.CreateDirectory(parent);
                PathBoundary.EnsureExistingPathHasNoReparsePoints(temporaryPath, parent);

                faultInjector.OnFaultPoint(StorageFaultPoint.BeforeShardWrite, shardPath);
                WriteShard(shardPath, shard);
                faultInjector.OnFaultPoint(StorageFaultPoint.AfterShardWrite, shardPath);
                shardContracts.Add(ValidateShard(shardPath, shard.Kind, shard.RelativePath, shard.RecordSchema, shard.Records.Count));
            }

            var manifest = request.Manifest with { Shards = shardContracts };
            manifest.Validate();
            var manifestPath = PathBoundary.ResolveChild(temporaryPath, ManifestFileName);
            faultInjector.OnFaultPoint(StorageFaultPoint.BeforeManifestWrite, manifestPath);
            var manifestBytes = StrictUtf8.GetBytes(ContractJson.Serialize(manifest));
            WriteDurableFile(manifestPath, manifestBytes);
            faultInjector.OnFaultPoint(StorageFaultPoint.AfterManifestWrite, manifestPath);

            using (var verified = OpenGeneration(temporaryPath, expectedManifestHash: null, requireDirectoryNameMatch: false))
            {
                EnsureManifestIdentity(manifest, verified.Manifest);
            }

            faultInjector.OnFaultPoint(StorageFaultPoint.BeforeGenerationCommit, generationPath);
            Directory.Move(temporaryPath, generationPath);
            generationCommitted = true;
            faultInjector.OnFaultPoint(StorageFaultPoint.AfterGenerationCommit, generationPath);

            string manifestHash;
            using (var verified = OpenGeneration(generationPath, expectedManifestHash: null))
            {
                EnsureManifestIdentity(manifest, verified.Manifest);
                manifestHash = ComputeHash(PathBoundary.ResolveChild(generationPath, ManifestFileName));
            }

            PublishPointer(new CurrentPointer(PointerSchema, ContractVersions.Current, manifest.GenerationId, manifestHash));
            return manifest;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (ContractValidationException exception)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "Generation contract validation failed.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            throw new StorageException(StorageErrorCodes.IoError, "Generation storage I/O failed.", exception);
        }
        finally
        {
            if (!generationCommitted && Directory.Exists(temporaryPath))
            {
                TryDeleteTemporaryDirectory(temporaryPath);
            }
        }
    }

    public GenerationReader OpenCurrent()
    {
        try
        {
            var pointerPath = PathBoundary.ResolveChild(storeRoot, PointerFileName);
            if (!File.Exists(pointerPath))
            {
                throw new StorageException(StorageErrorCodes.NoCurrentGeneration, "No current generation has been published.");
            }

            PathBoundary.EnsureNotReparsePoint(pointerPath);
            var pointerBytes = ReadPointerBytes(pointerPath);
            CurrentPointer pointer;
            try
            {
                pointer = JsonSerializer.Deserialize<CurrentPointer>(pointerBytes, PointerJsonOptions)
                    ?? throw new StorageException(StorageErrorCodes.CorruptGeneration, "The current generation pointer is empty.");
            }
            catch (JsonException exception)
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "The current generation pointer is malformed.", exception);
            }

            ValidatePointer(pointer);
            ValidateGenerationId(pointer.GenerationId);
            var generationPath = PathBoundary.ResolveChild(generationsRoot, pointer.GenerationId);
            if (!Directory.Exists(generationPath))
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "The current generation directory is missing.");
            }

            return OpenGeneration(generationPath, pointer.ManifestHash);
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new StorageException(StorageErrorCodes.IoError, "Generation storage I/O failed.", exception);
        }
    }

    public GenerationReader Open(string generationId)
    {
        ValidateGenerationId(generationId);
        try
        {
            var generationPath = PathBoundary.ResolveChild(generationsRoot, generationId);
            if (!Directory.Exists(generationPath))
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "The requested generation directory is missing.");
            }

            return OpenGeneration(generationPath, expectedManifestHash: null);
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new StorageException(StorageErrorCodes.IoError, "Generation storage I/O failed.", exception);
        }
    }

    private FileStream AcquireWriterLease(TimeSpan wait)
    {
        if (wait < TimeSpan.Zero || wait > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(wait), "Writer wait must be between zero and one minute.");
        }

        var lockPath = PathBoundary.ResolveChild(storeRoot, "writer.lock");
        PathBoundary.EnsureExistingPathHasNoReparsePoints(storeRoot, lockPath);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
                WriteWriterLeaseIdentity(lease);
                return lease;
            }
            catch (IOException exception)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new StorageException(StorageErrorCodes.Busy, "Another writer holds the generation store lease.", exception);
                }

                Thread.Sleep(20);
            }
        }
    }

    private static void WriteWriterLeaseIdentity(FileStream lease)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            host = Environment.MachineName,
            processId = Environment.ProcessId,
            processStartedAt = process.StartTime.ToUniversalTime(),
            acquiredAt = DateTimeOffset.UtcNow
        });
        lease.SetLength(0);
        lease.Write(identity);
        lease.Flush(flushToDisk: true);
        lease.Position = 0;
    }

    private GenerationReader OpenGeneration(string generationPath, string? expectedManifestHash, bool requireDirectoryNameMatch = true)
    {
        PathBoundary.EnsureExistingPathHasNoReparsePoints(generationsRoot, generationPath);
        var pinPath = PathBoundary.ResolveChild(generationPath, PinFileName);
        PathBoundary.EnsureNotReparsePoint(pinPath);
        FileStream? pin = null;
        try
        {
            pin = new FileStream(pinPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifestPath = PathBoundary.ResolveChild(generationPath, ManifestFileName);
            PathBoundary.EnsureNotReparsePoint(manifestPath);
            var actualManifestHash = ComputeHash(manifestPath);
            if (expectedManifestHash is not null && !string.Equals(actualManifestHash, expectedManifestHash, StringComparison.Ordinal))
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "The generation manifest digest does not match the current pointer.");
            }

            var manifestBytes = ReadBoundedBytes(manifestPath, MaxManifestByteLength);
            ManifestContract manifest;
            try
            {
                manifest = ContractJson.Deserialize<ManifestContract>(StrictUtf8.GetString(manifestBytes));
            }
            catch (ContractValidationException exception)
            {
                var code = exception.ErrorCode == "SCHEMA_UNSUPPORTED"
                    ? StorageErrorCodes.SchemaUnsupported
                    : StorageErrorCodes.CorruptGeneration;
                throw new StorageException(code, "The generation manifest contract is invalid.", exception);
            }

            ValidateGenerationId(manifest.GenerationId);
            if (requireDirectoryNameMatch && !string.Equals(Path.GetFileName(generationPath), manifest.GenerationId, StringComparison.Ordinal))
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "The manifest generation ID does not match its directory.");
            }

            var paths = new HashSet<string>(PathBoundary.PathComparer);
            foreach (var shard in manifest.Shards)
            {
                if (!paths.Add(shard.Path))
                {
                    throw new StorageException(StorageErrorCodes.CorruptGeneration, "The manifest contains duplicate shard paths.");
                }

                var shardPath = PathBoundary.ResolveRelative(generationPath, shard.Path);
                PathBoundary.EnsureExistingPathHasNoReparsePoints(generationPath, shardPath);
                ValidateShard(shardPath, shard.Kind, shard.Path, KnownSchemaForKind(shard.Kind), shard.RecordCount, shard);
            }

            var reader = new GenerationReader(generationPath, manifest, pin);
            pin = null;
            return reader;
        }
        finally
        {
            pin?.Dispose();
        }
    }

    private static void WriteShard(string path, GenerationShardWriteRequest shard)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        foreach (var record in shard.Records)
        {
            var compact = ValidateAndCompactRecord(record, shard.RecordSchema);
            if (compact.Length > MaxRecordByteLength)
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard record exceeds the storage limit.");
            }

            stream.Write(compact);
            stream.WriteByte((byte)'\n');
        }

        stream.Flush(flushToDisk: true);
    }

    private static ManifestShardContract ValidateShard(
        string path,
        string kind,
        string relativePath,
        string? expectedSchema,
        int expectedRecordCount,
        ManifestShardContract? expected = null)
    {
        if (!File.Exists(path))
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A manifest shard is missing.");
        }

        PathBoundary.EnsureNotReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length > MaxShardByteLength)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard exceeds the storage size limit.");
        }

        if (expected is not null && info.Length != expected.ByteLength)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard byte length does not match its manifest.");
        }

        var digest = ComputeHash(path);
        if (expected is not null && !string.Equals(digest, expected.ContentHash, StringComparison.Ordinal))
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard digest does not match its manifest.");
        }

        var recordCount = ValidateJsonLines(path, expectedSchema);
        if (recordCount != expectedRecordCount || expected is not null && recordCount != expected.RecordCount)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard record count does not match its manifest.");
        }

        return new ManifestShardContract(kind, relativePath.Replace('\\', '/'), digest, info.Length, recordCount);
    }

    private static int ValidateJsonLines(string path, string? expectedSchema)
    {
        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            return 0;
        }

        using (var tail = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            tail.Seek(-1, SeekOrigin.End);
            if (tail.ReadByte() != '\n')
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "A JSONL shard is not terminated by a newline.");
            }
        }

        var count = 0;
        string? discoveredSchema = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false, 64 * 1024, leaveOpen: false);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || StrictUtf8.GetByteCount(line) > MaxRecordByteLength)
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "A JSONL shard contains an empty or oversized record.");
            }

            var schema = ValidateRecordEnvelope(line, expectedSchema ?? discoveredSchema);
            discoveredSchema ??= schema;
            count++;
            if (count > MaxShardRecordCount)
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard exceeds the record count limit.");
            }
        }

        return count;
    }

    private static byte[] ValidateAndCompactRecord(string record, string expectedSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSchema);
        try
        {
            using var document = JsonDocument.Parse(record, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            ValidateUniqueProperties(document.RootElement, "$", 0);
            ValidateRecordEnvelope(document.RootElement, expectedSchema);
            return JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard record is malformed JSON.", exception);
        }
    }

    private static string ValidateRecordEnvelope(string record, string? expectedSchema)
    {
        try
        {
            using var document = JsonDocument.Parse(record, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            ValidateUniqueProperties(document.RootElement, "$", 0);
            var schema = ValidateRecordEnvelope(document.RootElement, expectedSchema);
            ValidateKnownContract(record, schema);
            return schema;
        }
        catch (JsonException exception)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard record is malformed JSON.", exception);
        }
    }

    private static string ValidateRecordEnvelope(JsonElement root, string? expectedSchema)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("schemaVersion", out var versionElement) || !versionElement.TryGetInt32(out var version))
        {
            throw new StorageException(StorageErrorCodes.SchemaUnsupported, "A shard record requires schema and integer schemaVersion fields.");
        }

        var schema = schemaElement.GetString()!;
        if (version != ContractVersions.Current || expectedSchema is not null && !string.Equals(schema, expectedSchema, StringComparison.Ordinal))
        {
            throw new StorageException(StorageErrorCodes.SchemaUnsupported, "A shard record uses an unsupported schema or version.");
        }

        return schema;
    }

    private static void ValidateKnownContract(string json, string schema)
    {
        try
        {
            if (schema == ContractSchemas.Symbol)
            {
                _ = ContractJson.Deserialize<SymbolContract>(json);
            }
            else if (schema == ContractSchemas.Declaration)
            {
                _ = ContractJson.Deserialize<DeclarationContract>(json);
            }
            else if (schema == ContractSchemas.Reference)
            {
                _ = ContractJson.Deserialize<ReferenceContract>(json);
            }
            else if (schema == ContractSchemas.Diff)
            {
                _ = ContractJson.Deserialize<DiffContract>(json);
            }
        }
        catch (ContractValidationException exception)
        {
            var code = exception.ErrorCode == "SCHEMA_UNSUPPORTED"
                ? StorageErrorCodes.SchemaUnsupported
                : StorageErrorCodes.CorruptGeneration;
            throw new StorageException(code, "A shard record does not satisfy its registered contract.", exception);
        }
    }

    private void PublishPointer(CurrentPointer pointer)
    {
        var pointerPath = PathBoundary.ResolveChild(storeRoot, PointerFileName);
        var temporaryPointerPath = PathBoundary.ResolveChild(storeRoot, $".current-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(pointer, PointerJsonOptions);
            using (var stream = new FileStream(temporaryPointerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var split = bytes.Length / 2;
                stream.Write(bytes.AsSpan(0, split));
                faultInjector.OnFaultPoint(StorageFaultPoint.DuringPointerWrite, temporaryPointerPath);
                stream.Write(bytes.AsSpan(split));
                stream.Flush(flushToDisk: true);
            }

            faultInjector.OnFaultPoint(StorageFaultPoint.BeforePointerReplace, pointerPath);
            if (File.Exists(pointerPath))
            {
                PathBoundary.EnsureNotReparsePoint(pointerPath);
            }

            // A same-volume overwrite rename swaps the name in one step, so readers see either the
            // old or the new complete pointer and never a missing one (File.Replace leaves a gap).
            TransientFileConflict.Retry(() => File.Move(temporaryPointerPath, pointerPath, overwrite: true));
        }
        finally
        {
            if (File.Exists(temporaryPointerPath))
            {
                try
                {
                    File.Delete(temporaryPointerPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void ValidatePointer(CurrentPointer pointer)
    {
        if (!string.Equals(pointer.Schema, PointerSchema, StringComparison.Ordinal) || pointer.SchemaVersion != ContractVersions.Current)
        {
            throw new StorageException(StorageErrorCodes.SchemaUnsupported, "The current generation pointer uses an unsupported schema or version.");
        }

        ValidateSha256(pointer.ManifestHash);
    }

    private static void ValidateShardRequests(IReadOnlyList<GenerationShardWriteRequest> shards)
    {
        var paths = new HashSet<string>(PathBoundary.PathComparer);
        foreach (var shard in shards)
        {
            ArgumentNullException.ThrowIfNull(shard);
            ArgumentException.ThrowIfNullOrWhiteSpace(shard.Kind);
            ArgumentException.ThrowIfNullOrWhiteSpace(shard.RelativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(shard.RecordSchema);
            ArgumentNullException.ThrowIfNull(shard.Records);
            if (shard.Records.Count > MaxShardRecordCount)
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard exceeds the record count limit.");
            }

            var normalized = shard.RelativePath.Replace('\\', '/');
            if (!paths.Add(normalized))
            {
                throw new StorageException(StorageErrorCodes.CorruptGeneration, "Shard paths must be unique.");
            }

            var knownSchema = KnownSchemaForKind(shard.Kind);
            if (knownSchema is not null && !string.Equals(knownSchema, shard.RecordSchema, StringComparison.Ordinal))
            {
                throw new StorageException(StorageErrorCodes.SchemaUnsupported, "A known shard kind uses the wrong record schema.");
            }
        }
    }

    private static string? KnownSchemaForKind(string kind) => kind switch
    {
        "symbol" => ContractSchemas.Symbol,
        "declaration" => ContractSchemas.Declaration,
        "reference" => ContractSchemas.Reference,
        "diff" => ContractSchemas.Diff,
        _ => null
    };

    private static void EnsureManifestIdentity(ManifestContract expected, ManifestContract actual)
    {
        if (!string.Equals(ContractJson.Serialize(expected), ContractJson.Serialize(actual), StringComparison.Ordinal))
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "The verified manifest differs from the generation being published.");
        }
    }

    private static void ValidateGenerationId(string generationId)
    {
        if (string.IsNullOrWhiteSpace(generationId) || generationId.Length > 128 ||
            !char.IsAsciiLetterOrDigit(generationId[0]) ||
            generationId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new StorageException(StorageErrorCodes.PathOutsideStore, "The generation ID is not a safe path segment.");
        }
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A stored SHA-256 digest is invalid.");
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}";
    }

    private static byte[] ReadBoundedBytes(string path, long limit)
    {
        var info = new FileInfo(path);
        if (info.Length > limit || info.Length > int.MaxValue)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A storage metadata file exceeds the size limit.");
        }

        return File.ReadAllBytes(path);
    }

    // Delete sharing avoids denying the publisher's overwrite rename where the file system honors it;
    // NTFS still refuses to rename over an open file, so the short read keeps the hold brief and the
    // publisher retries. A reader that loses the race keeps the complete previous pointer, whose
    // generation is never deleted. The open itself can collide with the rename, so it retries too.
    private static byte[] ReadPointerBytes(string path)
    {
        using var stream = TransientFileConflict.Retry(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
        if (stream.Length > MaxManifestByteLength)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A storage metadata file exceeds the size limit.");
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteDurableFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CreateDurableEmptyFile(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateUniqueProperties(JsonElement element, string path, int depth)
    {
        if (depth > 64)
        {
            throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard record exceeds the nesting limit.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new StorageException(StorageErrorCodes.CorruptGeneration, "A shard record contains duplicate JSON properties.");
                }

                ValidateUniqueProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateUniqueProperties(item, path, depth + 1);
            }
        }
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record CurrentPointer(string Schema, int SchemaVersion, string GenerationId, string ManifestHash);
}

public sealed class GenerationReader : IDisposable
{
    private readonly string generationPath;
    private FileStream? pin;

    internal GenerationReader(string generationPath, ManifestContract manifest, FileStream pin)
    {
        this.generationPath = generationPath;
        Manifest = manifest;
        this.pin = pin;
    }

    public ManifestContract Manifest { get; }

    public IReadOnlyList<string> ReadShardRecords(string relativePath)
    {
        ObjectDisposedException.ThrowIf(pin is null, this);
        var shard = Manifest.Shards.SingleOrDefault(item => PathBoundary.PathComparer.Equals(item.Path, relativePath.Replace('\\', '/')))
            ?? throw new StorageException(StorageErrorCodes.CorruptGeneration, "The requested shard is not present in the manifest.");
        var path = PathBoundary.ResolveRelative(generationPath, shard.Path);
        PathBoundary.EnsureExistingPathHasNoReparsePoints(generationPath, path);
        return File.ReadLines(path, new UTF8Encoding(false, true)).ToArray();
    }

    public void Dispose()
    {
        pin?.Dispose();
        pin = null;
    }
}

internal static class PathBoundary
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string ResolveChild(string root, string child) => ResolveRelative(root, child);

    public static string ResolveRelative(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw Outside();
        }

        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw Outside();
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, Path.Combine(parts)));
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
        {
            throw Outside();
        }

        EnsureExistingPathHasNoReparsePoints(canonicalRoot, candidate);
        return candidate;
    }

    public static void EnsureExistingPathHasNoReparsePoints(string root, string candidate)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(canonicalRoot, fullCandidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw Outside();
        }

        EnsureNotReparsePoint(canonicalRoot);
        var current = canonicalRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                EnsureNotReparsePoint(current);
            }
        }
    }

    public static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Outside();
        }
    }

    private static StorageException Outside() =>
        new(StorageErrorCodes.PathOutsideStore, "A storage path escapes the configured root or crosses a reparse point.");
}
