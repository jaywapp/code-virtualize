using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeVirtualize.Core.Contracts;

public static class ContractJson
{
    private static readonly IReadOnlyDictionary<Type, string> RegisteredSchemas =
        new Dictionary<Type, string>
        {
            [typeof(ManifestContract)] = ContractSchemas.Manifest,
            [typeof(SymbolContract)] = ContractSchemas.Symbol,
            [typeof(DeclarationContract)] = ContractSchemas.Declaration,
            [typeof(ReferenceContract)] = ContractSchemas.Reference,
            [typeof(DiffContract)] = ContractSchemas.Diff,
            [typeof(ResponseContract)] = ContractSchemas.Response
        };

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string Serialize<T>(T contract)
        where T : IVersionedContract
    {
        var expectedSchema = GetExpectedSchema(typeof(T));
        ContractGuard.Version(contract, expectedSchema);
        contract.Validate();
        return JsonSerializer.Serialize(contract, SerializerOptions);
    }

    public static T Deserialize<T>(string json)
        where T : IVersionedContract
    {
        var expectedSchema = GetExpectedSchema(typeof(T));

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            EnsureNoDuplicateProperties(document.RootElement, "$", 0);
            ValidateEnvelope(document.RootElement, expectedSchema);

            var contract = JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new ContractValidationException("CONTRACT_INVALID", "Contract JSON produced a null value.");
            ContractGuard.Version(contract, expectedSchema);
            contract.Validate();
            return contract;
        }
        catch (ContractValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ContractValidationException("CONTRACT_INVALID", "Contract JSON is malformed or does not match the registered contract.", exception);
        }
    }

    public static JsonElement ToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, SerializerOptions);
    }

    private static string GetExpectedSchema(Type type)
    {
        if (!RegisteredSchemas.TryGetValue(type, out var schema))
        {
            throw new InvalidOperationException($"Type '{type.FullName}' is not a registered top-level contract.");
        }

        return schema;
    }

    private static void ValidateEnvelope(JsonElement root, string expectedSchema)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema", out var schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("schemaVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version))
        {
            throw new ContractValidationException("SCHEMA_UNSUPPORTED", "Contract schema and integer schemaVersion are required.");
        }

        var schema = schemaElement.GetString();
        if (!string.Equals(schema, expectedSchema, StringComparison.Ordinal) || version != ContractVersions.Current)
        {
            throw new ContractValidationException(
                "SCHEMA_UNSUPPORTED",
                $"Unsupported contract schema '{schema}' version '{version}'. Expected '{expectedSchema}' version '{ContractVersions.Current}'.");
        }
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path, int depth)
    {
        if (depth > 64)
        {
            throw new ContractValidationException("CONTRACT_INVALID", "Contract JSON exceeds the maximum nesting depth.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ContractValidationException("CONTRACT_INVALID", $"Duplicate JSON property '{path}.{property.Name}' is not allowed.");
                }

                EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item, $"{path}[{index}]", depth + 1);
                index++;
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
