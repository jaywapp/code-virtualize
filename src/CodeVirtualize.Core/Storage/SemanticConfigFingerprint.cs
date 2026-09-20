using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Analysis;

namespace CodeVirtualize.Core.Storage;

public sealed record WorkspaceFingerprintInput(string WorkspacePath, string? WorktreeIdentity = null);

public sealed record AnalysisConfigFingerprintInput(
    int SchemaVersion,
    string AdapterVersion,
    string CompilerVersion,
    IReadOnlyList<string> ProjectGraph,
    IReadOnlyList<string> References,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> Defines,
    AnalysisMode TrustMode);

public static class SemanticConfigFingerprint
{
    public static string CreateWorkspaceKey(WorkspaceFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspacePath);

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.WorkspacePath));
        var normalizedPath = fullPath.Replace(Path.DirectorySeparatorChar, '/');
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }

        return Hash(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "workspace");
            writer.WriteString("path", normalizedPath);
            WriteNullableNormalized(writer, "worktreeIdentity", input.WorktreeIdentity);
            writer.WriteEndObject();
        });
    }

    public static string CreateAnalysisKey(AnalysisConfigFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "SchemaVersion must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(input.AdapterVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.CompilerVersion);

        return Hash(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "analysis");
            writer.WriteNumber("schemaVersion", input.SchemaVersion);
            writer.WriteString("adapterVersion", input.AdapterVersion.Trim());
            writer.WriteString("compilerVersion", input.CompilerVersion.Trim());
            WriteSortedArray(writer, "projectGraph", input.ProjectGraph);
            WriteSortedArray(writer, "references", input.References);
            WriteSortedArray(writer, "targetFrameworks", input.TargetFrameworks);
            WriteSortedArray(writer, "defines", input.Defines);
            writer.WriteString("trustMode", input.TrustMode switch
            {
                AnalysisMode.SyntaxOnly => "syntax_only",
                AnalysisMode.TrustedSemantic => "trusted_semantic",
                _ => throw new ArgumentOutOfRangeException(nameof(input), "Unsupported analysis mode.")
            });
            writer.WriteEndObject();
        });
    }

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }

        return $"sha256:{Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant()}";
    }

    private static void WriteNullableNormalized(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value.Trim());
    }

    private static void WriteSortedArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.Select(value =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException($"{name} must contain non-empty values.", nameof(values));
                }

                return value.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        writer.WriteStartArray(name);
        foreach (var value in normalized)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
