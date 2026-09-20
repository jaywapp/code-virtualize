using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeVirtualize.Core.Contracts;

public sealed record PageCursorPayload(
    string GenerationId,
    string QueryFingerprint,
    int Offset,
    string Schema = ContractSchemas.Cursor,
    int SchemaVersion = ContractVersions.Current) : IContractValidatable
{
    public void Validate()
    {
        if (!string.Equals(Schema, ContractSchemas.Cursor, StringComparison.Ordinal) ||
            SchemaVersion != ContractVersions.Current)
        {
            throw new ContractValidationException("CURSOR_INVALID", "Cursor schema is unsupported.");
        }

        ContractGuard.Required(GenerationId, nameof(GenerationId));
        ContractGuard.Sha256(QueryFingerprint, nameof(QueryFingerprint));
        ContractGuard.NonNegative(Offset, nameof(Offset));
    }
}

public static class PageCursorCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 8
    };

    public static string Encode(PageCursorPayload cursor)
    {
        cursor.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, Options);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static PageCursorPayload Decode(
        string encodedCursor,
        string expectedGenerationId,
        string expectedQueryFingerprint)
    {
        ContractGuard.Required(encodedCursor, nameof(encodedCursor));
        try
        {
            var cursor = JsonSerializer.Deserialize<PageCursorPayload>(DecodeBase64Url(encodedCursor), Options)
                ?? throw new ContractValidationException("CURSOR_INVALID", "Cursor payload is empty.");
            cursor.Validate();

            if (!string.Equals(cursor.GenerationId, expectedGenerationId, StringComparison.Ordinal))
            {
                throw new ContractValidationException(
                    "CURSOR_EXPIRED",
                    "Cursor belongs to a different generation. Restart the query against the selected generation.");
            }

            if (!string.Equals(cursor.QueryFingerprint, expectedQueryFingerprint, StringComparison.Ordinal))
            {
                throw new ContractValidationException("CURSOR_INVALID", "Cursor belongs to a different query.");
            }

            return cursor;
        }
        catch (ContractValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NotSupportedException)
        {
            throw new ContractValidationException("CURSOR_INVALID", "Cursor is malformed.", exception);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(base64);
    }
}
