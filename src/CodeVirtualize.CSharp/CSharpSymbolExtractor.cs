using CodeVirtualize.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeVirtualize.CSharp;

internal sealed record ParsedSource(InventorySourceFile File, byte[] Bytes, string Text, SyntaxTree Tree, string ContentHash, string Encoding, string Newline);

internal static class CSharpSymbolExtractor
{
    private sealed record SymbolPart(
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
        DeclarationContract Declaration,
        IReadOnlyList<string> Limitations);

    public static IReadOnlyList<SymbolContract> Extract(
        InventoryProject project,
        string analysisKey,
        IReadOnlyList<ParsedSource> sources,
        CSharpCompilation? compilation)
    {
        var parts = new List<SymbolPart>();
        foreach (var source in sources)
        {
            var root = source.Tree.GetRoot();
            var semanticModel = compilation?.GetSemanticModel(source.Tree, ignoreAccessibility: true);
            foreach (var node in DeclarationNodes(root))
            {
                var part = CreatePart(project, analysisKey, source, node, semanticModel);
                if (part is not null)
                {
                    parts.Add(part);
                }
            }
        }

        return parts.GroupBy(part => part.SymbolId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.OrderBy(part => part.Declaration.Location.Path, StringComparer.Ordinal)
                    .ThenBy(part => part.Declaration.Location.Span.Start)
                    .First();
                var declarations = group.Select(part => part.Declaration)
                    .DistinctBy(declaration => (declaration.Location.FileId, declaration.Location.Path, declaration.Location.Span.Start, declaration.Location.Span.Length))
                    .OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
                    .ThenBy(declaration => declaration.Location.Span.Start)
                    .ToArray();
                var limitations = group.SelectMany(part => part.Limitations).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var quality = group.All(part => part.IdentityQuality == IdentityQuality.Semantic)
                    ? IdentityQuality.Semantic
                    : IdentityQuality.Syntactic;
                return new SymbolContract(
                    first.SymbolId,
                    first.ProjectId,
                    first.AnalysisKey,
                    first.Kind,
                    first.Name,
                    first.QualifiedName,
                    first.Signature,
                    first.Accessibility,
                    first.ContainerId,
                    first.GenericArity,
                    quality,
                    first.Parameters,
                    first.ExplicitInterface,
                    declarations,
                    limitations);
            })
            .OrderBy(symbol => symbol.ProjectId, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Signature, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<SyntaxNode> DeclarationNodes(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax or
                DestructorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or
                OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or EnumMemberDeclarationSyntax)
            {
                yield return node;
            }
            else if (node is FieldDeclarationSyntax field)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    yield return variable;
                }
            }
            else if (node is EventFieldDeclarationSyntax eventField)
            {
                foreach (var variable in eventField.Declaration.Variables)
                {
                    yield return variable;
                }
            }
        }
    }

    private static SymbolPart? CreatePart(
        InventoryProject project,
        string analysisKey,
        ParsedSource source,
        SyntaxNode node,
        SemanticModel? semanticModel)
    {
        var declaredSymbol = semanticModel?.GetDeclaredSymbol(node);
        var canUseSemanticIdentity = declaredSymbol is not null && !HasErrorIdentity(declaredSymbol);
        var descriptor = canUseSemanticIdentity
            ? DescribeSemantic(declaredSymbol!)
            : DescribeSyntactic(node);
        if (descriptor is null)
        {
            return null;
        }

        var identityQuality = canUseSemanticIdentity ? IdentityQuality.Semantic : IdentityQuality.Syntactic;
        var limitations = identityQuality == IdentityQuality.Syntactic
            ? new[] { semanticModel is null ? "syntax-only-identity" : "semantic-identity-unavailable" }
            : Array.Empty<string>();
        var identity = new SymbolIdentityContract(
            project.Identity,
            analysisKey,
            descriptor.Kind,
            descriptor.QualifiedMetadataName,
            descriptor.GenericArity,
            descriptor.Parameters,
            descriptor.ExplicitInterface);
        var symbolId = DeterministicSymbolId.Create(identity);
        var containerId = ContainerSymbolId(project, analysisKey, node, semanticModel);
        var lineSpan = source.Tree.GetLineSpan(node.Span).Span;
        var location = new LocationContract(
            FileId(source.File.RelativePath),
            source.ContentHash,
            new TextSpanContract(node.SpanStart, node.Span.Length, lineSpan.Start.Line + 1, lineSpan.End.Line + 1),
            source.File.RelativePath);
        var declaration = new DeclarationContract(symbolId, location, DocumentKind.Source);
        return new SymbolPart(
            symbolId,
            project.ProjectId,
            analysisKey,
            descriptor.Kind,
            descriptor.Name,
            descriptor.QualifiedName,
            descriptor.Signature,
            descriptor.Accessibility,
            containerId,
            descriptor.GenericArity,
            identityQuality,
            descriptor.Parameters,
            descriptor.ExplicitInterface,
            declaration,
            limitations);
    }

    /// <summary>
    /// The deterministic symbol ID of the immediately containing type declaration, or null for a
    /// top-level type (whose container is a namespace, which is not itself modeled as a symbol here).
    /// Computed the same way <see cref="CreatePart"/> would compute the container's own ID if that type's
    /// declaration node were processed directly, so a member's <c>containerId</c> always resolves to the
    /// exact <c>symbolId</c> of its containing type's merged <see cref="SymbolContract"/> — regardless of
    /// which partial declaration or file the member or the container was found in.
    /// </summary>
    private static string? ContainerSymbolId(InventoryProject project, string analysisKey, SyntaxNode node, SemanticModel? semanticModel)
    {
        var containerNode = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (containerNode is null)
        {
            return null;
        }

        var declaredSymbol = semanticModel?.GetDeclaredSymbol(containerNode);
        var canUseSemanticIdentity = declaredSymbol is not null && !HasErrorIdentity(declaredSymbol);
        var descriptor = canUseSemanticIdentity ? DescribeSemantic(declaredSymbol!) : DescribeSyntactic(containerNode);
        if (descriptor is null)
        {
            return null;
        }

        var identity = new SymbolIdentityContract(
            project.Identity,
            analysisKey,
            descriptor.Kind,
            descriptor.QualifiedMetadataName,
            descriptor.GenericArity,
            descriptor.Parameters,
            descriptor.ExplicitInterface);
        return DeterministicSymbolId.Create(identity);
    }

    private sealed record Descriptor(
        string Kind,
        string Name,
        string QualifiedName,
        string QualifiedMetadataName,
        string Signature,
        string Accessibility,
        int GenericArity,
        IReadOnlyList<ParameterIdentityContract> Parameters,
        string? ExplicitInterface);

    private static Descriptor DescribeSemantic(ISymbol symbol)
    {
        var kind = Kind(symbol);
        var name = symbol switch
        {
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => method.ContainingType.Name,
            IMethodSymbol method when method.MethodKind == MethodKind.StaticConstructor => method.ContainingType.Name,
            _ => symbol.Name
        };
        var qualifiedName = symbol.ToDisplayString(new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
        var metadataName = symbol is INamedTypeSymbol namedType
            ? QualifiedMetadataName(namedType)
            : $"{QualifiedMetadataName(symbol.ContainingType)}.{symbol.MetadataName}";
        var parameters = symbol switch
        {
            IMethodSymbol method => method.Parameters.Select(Parameter).ToArray(),
            IPropertySymbol property => property.Parameters.Select(Parameter).ToArray(),
            INamedTypeSymbol type when type.TypeKind == TypeKind.Delegate && type.DelegateInvokeMethod is not null => type.DelegateInvokeMethod.Parameters.Select(Parameter).ToArray(),
            _ => Array.Empty<ParameterIdentityContract>()
        };
        var arity = symbol switch
        {
            INamedTypeSymbol type => type.Arity,
            IMethodSymbol method => method.Arity,
            _ => 0
        };
        var explicitInterface = symbol switch
        {
            IMethodSymbol method when method.ExplicitInterfaceImplementations.Length > 0 => InterfaceIdentity(method.ExplicitInterfaceImplementations[0]),
            IPropertySymbol property when property.ExplicitInterfaceImplementations.Length > 0 => InterfaceIdentity(property.ExplicitInterfaceImplementations[0]),
            IEventSymbol eventSymbol when eventSymbol.ExplicitInterfaceImplementations.Length > 0 => InterfaceIdentity(eventSymbol.ExplicitInterfaceImplementations[0]),
            _ => null
        };
        var signature = symbol.ToDisplayString(new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers |
                           SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters |
                           SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
        return new Descriptor(kind, name, qualifiedName, metadataName, signature, Accessibility(symbol.DeclaredAccessibility), arity, parameters, explicitInterface);
    }

    private static Descriptor? DescribeSyntactic(SyntaxNode node)
    {
        var namespaceName = string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString()));
        var containingTypes = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(TypeMetadataName).ToArray();
        var container = string.Join('.', new[] { namespaceName }.Where(value => value.Length > 0).Concat(containingTypes));
        var kind = Kind(node);
        if (kind is null)
        {
            return null;
        }

        var name = Name(node);
        var arity = Arity(node);
        var selfMetadataName = node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax ? name + (arity > 0 ? $"`{arity}" : string.Empty) : name;
        var qualifiedMetadataName = string.IsNullOrEmpty(container) ? selfMetadataName : $"{container}.{selfMetadataName}";
        var explicitInterface = ExplicitInterface(node);
        var parameters = Parameters(node).Select(parameter => new ParameterIdentityContract(
            NormalizeType(parameter.Type?.ToString() ?? "?"),
            RefKind(parameter.Modifiers))).ToArray();
        var signature = Signature(node, name, parameters);
        var qualifiedName = string.IsNullOrEmpty(container) ? name : $"{container}.{name}";
        return new Descriptor(kind, name, qualifiedName, qualifiedMetadataName, signature, Accessibility(node), arity, parameters, explicitInterface);
    }

    private static string Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Class when type.IsRecord => "record",
            TypeKind.Struct when type.IsRecord => "record_struct",
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => "type"
        },
        IMethodSymbol method => method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => "constructor",
            MethodKind.Destructor => "destructor",
            MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
            _ => "method"
        },
        IPropertySymbol property => property.IsIndexer ? "indexer" : "property",
        IEventSymbol => "event",
        IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum => "enum_member",
        IFieldSymbol => "field",
        _ => symbol.Kind.ToString().ToLowerInvariant()
    };

    private static string? Kind(SyntaxNode node) => node switch
    {
        RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record_struct",
        RecordDeclarationSyntax => "record",
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        DelegateDeclarationSyntax => "delegate",
        ConstructorDeclarationSyntax => "constructor",
        DestructorDeclarationSyntax => "destructor",
        MethodDeclarationSyntax => "method",
        PropertyDeclarationSyntax => "property",
        IndexerDeclarationSyntax => "indexer",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
        OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
        EnumMemberDeclarationSyntax => "enum_member",
        VariableDeclaratorSyntax variable when variable.Parent?.Parent is EventFieldDeclarationSyntax => "event",
        VariableDeclaratorSyntax => "field",
        _ => null
    };

    private static string Name(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax declaration => declaration.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        DestructorDeclarationSyntax destructor => destructor.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        IndexerDeclarationSyntax => "this",
        EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.ValueText,
        OperatorDeclarationSyntax operation => $"operator {operation.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
        EnumMemberDeclarationSyntax enumMember => enumMember.Identifier.ValueText,
        VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
        _ => "?"
    };

    private static int Arity(SyntaxNode node) => node switch
    {
        TypeDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
        DelegateDeclarationSyntax declaration => declaration.TypeParameterList?.Parameters.Count ?? 0,
        MethodDeclarationSyntax method => method.TypeParameterList?.Parameters.Count ?? 0,
        _ => 0
    };

    private static IEnumerable<ParameterSyntax> Parameters(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
        DelegateDeclarationSyntax declaration => declaration.ParameterList.Parameters,
        IndexerDeclarationSyntax indexer => indexer.ParameterList.Parameters,
        RecordDeclarationSyntax record when record.ParameterList is not null => record.ParameterList.Parameters,
        _ => []
    };

    private static string? ExplicitInterface(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.ExplicitInterfaceSpecifier?.Name.ToString(),
        PropertyDeclarationSyntax property => property.ExplicitInterfaceSpecifier?.Name.ToString(),
        IndexerDeclarationSyntax indexer => indexer.ExplicitInterfaceSpecifier?.Name.ToString(),
        EventDeclarationSyntax eventDeclaration => eventDeclaration.ExplicitInterfaceSpecifier?.Name.ToString(),
        _ => null
    };

    private static string Signature(SyntaxNode node, string name, IReadOnlyList<ParameterIdentityContract> parameters)
    {
        var modifiers = Modifiers(node);
        var prefix = modifiers.Count == 0 ? string.Empty : string.Join(' ', modifiers.Select(token => token.ValueText)) + " ";
        var parameterText = string.Join(", ", parameters.Select(parameter => $"{RefText(parameter.RefKind)}{parameter.TypeIdentity}"));
        return node switch
        {
            BaseTypeDeclarationSyntax type => $"{prefix}{TypeKeyword(type)} {name}{TypeParameters(type)}{BaseList(type)}",
            DelegateDeclarationSyntax declaration => $"{prefix}delegate {NormalizeType(declaration.ReturnType.ToString())} {name}{TypeParameters(declaration)}({parameterText})",
            MethodDeclarationSyntax method => $"{prefix}{NormalizeType(method.ReturnType.ToString())} {(method.ExplicitInterfaceSpecifier is null ? string.Empty : method.ExplicitInterfaceSpecifier.Name + ".")}{name}{TypeParameters(method)}({parameterText})",
            ConstructorDeclarationSyntax => $"{prefix}{name}({parameterText})",
            DestructorDeclarationSyntax => $"~{name}()",
            PropertyDeclarationSyntax property => $"{prefix}{NormalizeType(property.Type.ToString())} {(property.ExplicitInterfaceSpecifier is null ? string.Empty : property.ExplicitInterfaceSpecifier.Name + ".")}{name} {AccessorShape(property.AccessorList)}",
            IndexerDeclarationSyntax indexer => $"{prefix}{NormalizeType(indexer.Type.ToString())} {(indexer.ExplicitInterfaceSpecifier is null ? string.Empty : indexer.ExplicitInterfaceSpecifier.Name + ".")}this[{parameterText}] {AccessorShape(indexer.AccessorList)}",
            EventDeclarationSyntax eventDeclaration => $"{prefix}event {NormalizeType(eventDeclaration.Type.ToString())} {name}",
            OperatorDeclarationSyntax operation => $"{prefix}{NormalizeType(operation.ReturnType.ToString())} {name}({parameterText})",
            ConversionOperatorDeclarationSyntax conversion => $"{prefix}{conversion.ImplicitOrExplicitKeyword.ValueText} {name}({parameterText})",
            EnumMemberDeclarationSyntax => name,
            VariableDeclaratorSyntax variable when variable.Parent is VariableDeclarationSyntax declaration && variable.Parent.Parent is EventFieldDeclarationSyntax => $"{prefix}event {NormalizeType(declaration.Type.ToString())} {name}",
            VariableDeclaratorSyntax variable when variable.Parent is VariableDeclarationSyntax declaration => $"{prefix}{NormalizeType(declaration.Type.ToString())} {name}",
            _ => name
        };
    }

    private static SyntaxTokenList Modifiers(SyntaxNode node) => node switch
    {
        MemberDeclarationSyntax member => member.Modifiers,
        VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax field => field.Modifiers,
        _ => default
    };

    private static string TypeKeyword(BaseTypeDeclarationSyntax type) => type switch
    {
        TypeDeclarationSyntax declaration => declaration.Keyword.ValueText,
        EnumDeclarationSyntax => "enum",
        _ => "type"
    };
    private static string TypeParameters(SyntaxNode node) => node switch
    {
        TypeDeclarationSyntax type => type.TypeParameterList?.ToString() ?? string.Empty,
        DelegateDeclarationSyntax declaration => declaration.TypeParameterList?.ToString() ?? string.Empty,
        MethodDeclarationSyntax method => method.TypeParameterList?.ToString() ?? string.Empty,
        _ => string.Empty
    };

    private static string BaseList(BaseTypeDeclarationSyntax type) => type.BaseList is null ? string.Empty : $" {type.BaseList}";

    private static string AccessorShape(AccessorListSyntax? list) => list is null
        ? "=>"
        : $"{{ {string.Join(' ', list.Accessors.Select(accessor => accessor.Keyword.ValueText + ";"))} }}";

    private static string Accessibility(SyntaxNode node)
    {
        var modifiers = Modifiers(node);
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword) && modifiers.Any(SyntaxKind.ProtectedKeyword)) return "private_protected";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword) && modifiers.Any(SyntaxKind.InternalKeyword)) return "protected_internal";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        if (node.Ancestors().OfType<InterfaceDeclarationSyntax>().Any() || node is EnumMemberDeclarationSyntax) return "public";
        if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax && !node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any()) return "internal";
        return "private";
    }

    private static string Accessibility(Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public",
        Microsoft.CodeAnalysis.Accessibility.Private => "private",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private_protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected_internal",
        _ => "not_applicable"
    };

    private static string QualifiedMetadataName(INamedTypeSymbol? type)
    {
        if (type is null) return "<global>";
        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            names.Push(current.MetadataName);
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } ? type.ContainingNamespace.ToDisplayString() : string.Empty;
        var nested = string.Join('.', names);
        return ns.Length == 0 ? nested : $"{ns}.{nested}";
    }

    private static string TypeMetadataName(BaseTypeDeclarationSyntax type)
    {
        var arity = type is TypeDeclarationSyntax declaration ? declaration.TypeParameterList?.Parameters.Count ?? 0 : 0;
        return type.Identifier.ValueText + (arity == 0 ? string.Empty : $"`{arity}");
    }

    private static string InterfaceIdentity(ISymbol member) => $"{QualifiedMetadataName(member.ContainingType)}.{member.MetadataName}";

    private static ParameterIdentityContract Parameter(IParameterSymbol parameter) => new(
        parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
        parameter.RefKind switch
        {
            Microsoft.CodeAnalysis.RefKind.Ref => ParameterRefKind.Ref,
            Microsoft.CodeAnalysis.RefKind.Out => ParameterRefKind.Out,
            Microsoft.CodeAnalysis.RefKind.In => ParameterRefKind.In,
            Microsoft.CodeAnalysis.RefKind.RefReadOnlyParameter => ParameterRefKind.RefReadOnly,
            _ => ParameterRefKind.None
        });

    private static ParameterRefKind RefKind(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.RefKeyword) && modifiers.Any(SyntaxKind.ReadOnlyKeyword)) return ParameterRefKind.RefReadOnly;
        if (modifiers.Any(SyntaxKind.RefKeyword)) return ParameterRefKind.Ref;
        if (modifiers.Any(SyntaxKind.OutKeyword)) return ParameterRefKind.Out;
        if (modifiers.Any(SyntaxKind.InKeyword)) return ParameterRefKind.In;
        return ParameterRefKind.None;
    }

    private static string RefText(ParameterRefKind refKind) => refKind switch
    {
        ParameterRefKind.Ref => "ref ",
        ParameterRefKind.Out => "out ",
        ParameterRefKind.In => "in ",
        ParameterRefKind.RefReadOnly => "ref readonly ",
        _ => string.Empty
    };

    private static bool HasErrorIdentity(ISymbol symbol)
    {
        static bool IsError(ITypeSymbol type) => type.TypeKind == TypeKind.Error;
        return symbol switch
        {
            IMethodSymbol method => IsError(method.ReturnType) || method.Parameters.Any(parameter => IsError(parameter.Type)),
            IPropertySymbol property => IsError(property.Type) || property.Parameters.Any(parameter => IsError(parameter.Type)),
            IFieldSymbol field => IsError(field.Type),
            IEventSymbol eventSymbol => IsError(eventSymbol.Type),
            _ => false
        };
    }

    private static string NormalizeType(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static string FileId(string relativePath) => $"file_{WorkspaceInventoryReader.HashUtf8(relativePath)[7..]}";
}


