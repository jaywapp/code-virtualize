using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Storage;

public static class StorageErrorCodes
{
    public const string Busy = "BUSY";
    public const string CorruptGeneration = "CORRUPT_GENERATION";
    public const string GenerationExists = "GENERATION_EXISTS";
    public const string IoError = "STORAGE_IO_ERROR";
    public const string NoCurrentGeneration = "NO_CURRENT_GENERATION";
    public const string PathOutsideStore = "PATH_OUTSIDE_STORE";
    public const string SchemaUnsupported = "SCHEMA_UNSUPPORTED";
}

public sealed class StorageException : Exception
{
    public StorageException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public StorageException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed record GenerationShardWriteRequest(
    string Kind,
    string RelativePath,
    string RecordSchema,
    IReadOnlyList<string> Records);

public sealed record GenerationPublishRequest(
    ManifestContract Manifest,
    IReadOnlyList<GenerationShardWriteRequest> Shards);

public enum StorageFaultPoint
{
    BeforeShardWrite,
    AfterShardWrite,
    BeforeManifestWrite,
    AfterManifestWrite,
    BeforeGenerationCommit,
    AfterGenerationCommit,
    DuringPointerWrite,
    BeforePointerReplace
}

public interface IStorageFaultInjector
{
    void OnFaultPoint(StorageFaultPoint point, string path);
}

public sealed class NoStorageFaultInjector : IStorageFaultInjector
{
    public static NoStorageFaultInjector Instance { get; } = new();

    private NoStorageFaultInjector()
    {
    }

    public void OnFaultPoint(StorageFaultPoint point, string path)
    {
    }
}
