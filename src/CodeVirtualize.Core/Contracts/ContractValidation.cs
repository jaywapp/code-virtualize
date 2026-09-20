using System.Globalization;

namespace CodeVirtualize.Core.Contracts;

public static class ContractVersions
{
    public const int Current = 1;
}

public static class ContractSchemas
{
    public const string Manifest = "code-virtualize/manifest";
    public const string Symbol = "code-virtualize/symbol";
    public const string Declaration = "code-virtualize/declaration";
    public const string Reference = "code-virtualize/reference";
    public const string Diff = "code-virtualize/diff";
    public const string Response = "code-virtualize/response";
    public const string Cursor = "code-virtualize/cursor";
}

public static class ContractValues
{
    public const string Utf16CodeUnit = "utf16_code_unit";
    public const int OneBasedLine = 1;
}

public interface IContractValidatable
{
    void Validate();
}

public interface IVersionedContract : IContractValidatable
{
    string Schema { get; }

    int SchemaVersion { get; }
}

public sealed class ContractValidationException : Exception
{
    public ContractValidationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public ContractValidationException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

internal static class ContractGuard
{
    public static void Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(name, "must not be empty");
        }
    }

    public static void NonNegative(int value, string name)
    {
        if (value < 0)
        {
            throw Invalid(name, "must be non-negative");
        }
    }

    public static void Positive(int value, string name)
    {
        if (value <= 0)
        {
            throw Invalid(name, "must be positive");
        }
    }

    public static void Defined<TEnum>(TEnum value, string name)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw Invalid(name, "contains an unsupported enum value");
        }
    }

    public static void NoNullItems<T>(IReadOnlyList<T?> values, string name)
        where T : class
    {
        if (values.Any(value => value is null))
        {
            throw Invalid(name, "must not contain null items");
        }
    }

    public static void Sha256(string value, string name)
    {
        const string Prefix = "sha256:";
        Required(value, name);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length != Prefix.Length + 64)
        {
            throw Invalid(name, "must use the sha256:<64 lowercase hex characters> format");
        }

        foreach (var character in value.AsSpan(Prefix.Length))
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw Invalid(name, "must use lowercase hexadecimal characters");
            }
        }
    }

    public static void SymbolId(string value, string name)
    {
        const string Prefix = "sym_";
        Required(value, name);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length != Prefix.Length + 64)
        {
            throw Invalid(name, "must use the sym_<64 lowercase hex characters> format");
        }

        foreach (var character in value.AsSpan(Prefix.Length))
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw Invalid(name, "must use lowercase hexadecimal characters");
            }
        }
    }

    public static void Version(IVersionedContract value, string expectedSchema)
    {
        if (!string.Equals(value.Schema, expectedSchema, StringComparison.Ordinal) ||
            value.SchemaVersion != ContractVersions.Current)
        {
            throw new ContractValidationException(
                "SCHEMA_UNSUPPORTED",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Unsupported contract schema '{0}' version '{1}'. Expected '{2}' version '{3}'.",
                    value.Schema,
                    value.SchemaVersion,
                    expectedSchema,
                    ContractVersions.Current));
        }
    }

    public static ContractValidationException Invalid(string name, string reason) =>
        new("CONTRACT_INVALID", $"Contract field '{name}' {reason}.");
}
