using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;

namespace CodeVirtualize.Core.Remarks;

public enum SourceVerificationStatus
{
    Verified,
    Stale,
    Error
}

public sealed record VerifiedSource(
    ManifestFileContract File,
    string FullPath,
    byte[] Bytes,
    string Text,
    string ContentHash);

public sealed record SourceVerificationResult(
    SourceVerificationStatus Status,
    VerifiedSource? Source,
    string? ErrorCode,
    string? ErrorMessage);

public static class VerifiedSourceReader
{
    public static SourceVerificationResult Read(string workspacePath, ManifestFileContract file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(file);

        string path;
        try
        {
            path = SourceResolver.WorkspacePath(workspacePath, file.Path);
        }
        catch (ArgumentException exception)
        {
            return Error("PATH_OUTSIDE_WORKSPACE", exception.Message);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Stale("SOURCE_MISSING", "Indexed source file is missing.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Error("SOURCE_READ_FAILED", "Indexed source file could not be read.");
        }

        var digest = SourceResolver.Digest(bytes);
        if (!string.Equals(digest, file.ContentHash, StringComparison.Ordinal))
        {
            return Stale("SOURCE_STALE", "Source digest differs from the indexed generation.");
        }

        try
        {
            var text = SourceResolver.Decode(bytes, file.Encoding);
            return new SourceVerificationResult(
                SourceVerificationStatus.Verified,
                new VerifiedSource(file, path, bytes, text, digest),
                null,
                null);
        }
        catch (DecoderFallbackException)
        {
            return Stale("ENCODING_INVALID", "Source encoding differs from the indexed generation.");
        }
    }

    private static SourceVerificationResult Stale(string code, string message) =>
        new(SourceVerificationStatus.Stale, null, code, message);

    private static SourceVerificationResult Error(string code, string message) =>
        new(SourceVerificationStatus.Error, null, code, message);
}
