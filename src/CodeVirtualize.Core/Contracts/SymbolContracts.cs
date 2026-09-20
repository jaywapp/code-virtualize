using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CodeVirtualize.Core.Contracts;

public enum DocumentKind
{
    Source,
    Generated
}

public enum IdentityQuality
{
    Semantic,
    Syntactic
}

public enum ParameterRefKind
{
    None,
    Ref,
    Out,
    In,
    RefReadOnly
}

public sealed record ParameterIdentityContract(string TypeIdentity, ParameterRefKind RefKind) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(TypeIdentity, nameof(TypeIdentity));
        ContractGuard.Defined(RefKind, nameof(RefKind));
    }
}

public sealed record SymbolIdentityContract(
    string ProjectIdentity,
    string AnalysisKey,
    string Kind,
    string QualifiedMetadataName,
    int GenericArity,
    IReadOnlyList<ParameterIdentityContract> Parameters,
    string? ExplicitInterface) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(ProjectIdentity, nameof(ProjectIdentity));
        ContractGuard.Required(AnalysisKey, nameof(AnalysisKey));
        ContractGuard.Required(Kind, nameof(Kind));
        ContractGuard.Required(QualifiedMetadataName, nameof(QualifiedMetadataName));
        ContractGuard.NonNegative(GenericArity, nameof(GenericArity));
        ContractGuard.NoNullItems(Parameters, nameof(Parameters));
        foreach (var parameter in Parameters)
        {
            parameter.Validate();
        }
    }
}

public static class DeterministicSymbolId
{
    public static string Create(SymbolIdentityContract identity)
    {
        identity.Validate();
        using var payload = new MemoryStream();
        Write(payload, identity.ProjectIdentity);
        Write(payload, identity.AnalysisKey);
        Write(payload, identity.Kind);
        Write(payload, identity.QualifiedMetadataName);
        Write(payload, identity.GenericArity);
        Write(payload, identity.ExplicitInterface ?? string.Empty);
        Write(payload, identity.Parameters.Count);
        foreach (var parameter in identity.Parameters)
        {
            Write(payload, parameter.TypeIdentity);
            Write(payload, (int)parameter.RefKind);
        }

        return $"sym_{Convert.ToHexStringLower(SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length))))}";
    }

    private static void Write(Stream destination, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Write(destination, bytes.Length);
        destination.Write(bytes);
    }

    private static void Write(Stream destination, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        destination.Write(bytes);
    }
}

public sealed record DeclarationContract(
    string SymbolId,
    LocationContract Location,
    DocumentKind DocumentKind,
    string Schema = ContractSchemas.Declaration,
    int SchemaVersion = ContractVersions.Current) : IVersionedContract
{
    public void Validate()
    {
        ContractGuard.Version(this, ContractSchemas.Declaration);
        ContractGuard.SymbolId(SymbolId, nameof(SymbolId));
        ContractGuard.Defined(DocumentKind, nameof(DocumentKind));
        Location.Validate();
        if (DocumentKind == DocumentKind.Generated && string.IsNullOrWhiteSpace(Location.Uri))
        {
            throw ContractGuard.Invalid(nameof(Location), "must use a virtual URI for a generated document");
        }
    }
}

public sealed record SymbolContract(
    string SymbolId,
    string ProjectId,
    string AnalysisKey,
    string Kind,
    string Name,
    string QualifiedName,
    string Signature,
    string Accessibility,
    string? ContainerId,
    int GenericArity,
    IdentityQuality IdentityQuality,
    IReadOnlyList<ParameterIdentityContract> Parameters,
    string? ExplicitInterface,
    IReadOnlyList<DeclarationContract> Declarations,
    IReadOnlyList<string> Limitations,
    string Schema = ContractSchemas.Symbol,
    int SchemaVersion = ContractVersions.Current) : IVersionedContract
{
    public void Validate()
    {
        ContractGuard.Version(this, ContractSchemas.Symbol);
        ContractGuard.SymbolId(SymbolId, nameof(SymbolId));
        ContractGuard.Required(ProjectId, nameof(ProjectId));
        ContractGuard.Required(AnalysisKey, nameof(AnalysisKey));
        ContractGuard.Required(Kind, nameof(Kind));
        ContractGuard.Required(Name, nameof(Name));
        ContractGuard.Required(QualifiedName, nameof(QualifiedName));
        ContractGuard.Required(Signature, nameof(Signature));
        ContractGuard.Required(Accessibility, nameof(Accessibility));
        ContractGuard.NonNegative(GenericArity, nameof(GenericArity));
        ContractGuard.Defined(IdentityQuality, nameof(IdentityQuality));
        ContractGuard.NoNullItems(Parameters, nameof(Parameters));
        ContractGuard.NoNullItems(Declarations, nameof(Declarations));
        ContractGuard.NoNullItems(Limitations, nameof(Limitations));
        if (Declarations.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Declarations), "must contain at least one declaration");
        }

        foreach (var parameter in Parameters)
        {
            parameter.Validate();
        }

        foreach (var declaration in Declarations)
        {
            declaration.Validate();
            if (!string.Equals(declaration.SymbolId, SymbolId, StringComparison.Ordinal))
            {
                throw ContractGuard.Invalid(nameof(Declarations), "must all belong to the containing symbol");
            }
        }

        if (Limitations.Any(string.IsNullOrWhiteSpace))
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must contain non-empty values");
        }

        if (IdentityQuality == IdentityQuality.Syntactic && Limitations.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must explain a syntactic identity");
        }
    }
}
