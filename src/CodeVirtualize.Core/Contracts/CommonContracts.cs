using System.Text;

namespace CodeVirtualize.Core.Contracts;

public enum CoverageLevel
{
    CompleteWithinScope,
    Partial,
    Unknown
}

public enum FreshnessState
{
    Verified,
    Stale,
    Unknown,
    NotApplicable
}

public enum BaselineKind
{
    Vcs,
    Session
}

public enum BudgetExhaustion
{
    None,
    Bytes,
    Lines,
    BytesAndLines
}

public sealed record TextSpanContract(
    int Start,
    int Length,
    int StartLine,
    int EndLine,
    string OffsetUnit = ContractValues.Utf16CodeUnit,
    int LineBase = ContractValues.OneBasedLine,
    bool EndLineInclusive = true) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.NonNegative(Start, nameof(Start));
        ContractGuard.NonNegative(Length, nameof(Length));
        if (!string.Equals(OffsetUnit, ContractValues.Utf16CodeUnit, StringComparison.Ordinal))
        {
            throw ContractGuard.Invalid(nameof(OffsetUnit), $"must equal '{ContractValues.Utf16CodeUnit}'");
        }

        if (LineBase != ContractValues.OneBasedLine)
        {
            throw ContractGuard.Invalid(nameof(LineBase), "must equal 1");
        }

        if (!EndLineInclusive)
        {
            throw ContractGuard.Invalid(nameof(EndLineInclusive), "must be true");
        }

        if (StartLine < LineBase || EndLine < StartLine)
        {
            throw ContractGuard.Invalid(nameof(StartLine), "must describe a 1-based inclusive line range");
        }
    }
}

public sealed record LocationContract(
    string FileId,
    string ContentHash,
    TextSpanContract Span,
    string? Path = null,
    string? Uri = null) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(FileId, nameof(FileId));
        ContractGuard.Sha256(ContentHash, nameof(ContentHash));
        if (string.IsNullOrWhiteSpace(Path) == string.IsNullOrWhiteSpace(Uri))
        {
            throw ContractGuard.Invalid(nameof(Path), "requires exactly one of path or uri");
        }

        Span.Validate();
    }
}

public sealed record CoverageContract(
    string Scope,
    CoverageLevel Level,
    int AnalyzedFiles,
    int ExcludedFiles,
    int FailedFiles,
    int UnknownFiles,
    IReadOnlyList<string> FailedProjects,
    IReadOnlyList<string> Limitations,
    bool Truncated) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(Scope, nameof(Scope));
        ContractGuard.Defined(Level, nameof(Level));
        ContractGuard.NonNegative(AnalyzedFiles, nameof(AnalyzedFiles));
        ContractGuard.NonNegative(ExcludedFiles, nameof(ExcludedFiles));
        ContractGuard.NonNegative(FailedFiles, nameof(FailedFiles));
        ContractGuard.NonNegative(UnknownFiles, nameof(UnknownFiles));
        ContractGuard.NoNullItems(FailedProjects, nameof(FailedProjects));
        ContractGuard.NoNullItems(Limitations, nameof(Limitations));

        if (FailedProjects.Any(string.IsNullOrWhiteSpace) || Limitations.Any(string.IsNullOrWhiteSpace))
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must contain non-empty values");
        }

        if (Level == CoverageLevel.CompleteWithinScope &&
            (FailedFiles != 0 || UnknownFiles != 0 || FailedProjects.Count != 0 || Truncated))
        {
            throw ContractGuard.Invalid(nameof(Level), "cannot be complete when failed, unknown, or truncated scope exists");
        }

        if (Level != CoverageLevel.CompleteWithinScope && Limitations.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must explain partial or unknown coverage");
        }
    }
}

public sealed record FreshnessContract(
    FreshnessState ReturnedFiles,
    FreshnessState Workspace) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Defined(ReturnedFiles, nameof(ReturnedFiles));
        ContractGuard.Defined(Workspace, nameof(Workspace));
    }
}

public sealed record BaselineContract(
    BaselineKind Kind,
    string Provider,
    string BaseId,
    string TargetId,
    string? SessionId,
    DateTimeOffset CapturedAt,
    string InputFingerprint) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Defined(Kind, nameof(Kind));
        ContractGuard.Required(Provider, nameof(Provider));
        ContractGuard.Required(BaseId, nameof(BaseId));
        ContractGuard.Required(TargetId, nameof(TargetId));
        ContractGuard.Sha256(InputFingerprint, nameof(InputFingerprint));

        if (Kind == BaselineKind.Vcs && !string.IsNullOrEmpty(SessionId))
        {
            throw ContractGuard.Invalid(nameof(SessionId), "must be null for a VCS baseline");
        }

        if (Kind == BaselineKind.Session)
        {
            ContractGuard.Required(SessionId, nameof(SessionId));
            if (!string.Equals(Provider, "session", StringComparison.Ordinal))
            {
                throw ContractGuard.Invalid(nameof(Provider), "must equal 'session' for a session baseline");
            }
        }
    }
}

public sealed record SourceBudgetContract(int MaxBytes, int MaxLines) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Positive(MaxBytes, nameof(MaxBytes));
        ContractGuard.Positive(MaxLines, nameof(MaxLines));
    }
}

public sealed record SourceUsageContract(int Bytes, int Lines) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.NonNegative(Bytes, nameof(Bytes));
        ContractGuard.NonNegative(Lines, nameof(Lines));
    }
}

public sealed record SourceSliceContract(
    string Content,
    TextSpanContract Span,
    SourceBudgetContract Budget,
    SourceUsageContract Usage,
    bool Truncated,
    BudgetExhaustion ExhaustedBy,
    string ContentHash,
    bool NotModified) : IContractValidatable
{
    public void Validate()
    {
        Span.Validate();
        Budget.Validate();
        Usage.Validate();
        ContractGuard.Sha256(ContentHash, nameof(ContentHash));

        if (NotModified)
        {
            if (Content.Length != 0)
            {
                throw ContractGuard.Invalid(nameof(Content), "must be empty when the slice is not modified");
            }

            if (Usage.Bytes != 0 || Usage.Lines != 0)
            {
                throw ContractGuard.Invalid(nameof(Usage), "must be zero when the slice is not modified");
            }
        }
        else
        {
            var actualBytes = Encoding.UTF8.GetByteCount(Content);
            var actualLines = CountLogicalLines(Content);
            if (Usage.Bytes != actualBytes || Usage.Lines != actualLines)
            {
                throw ContractGuard.Invalid(nameof(Usage), "must equal the UTF-8 byte count and logical line count of content");
            }

            if (Span.Length != Content.Length)
            {
                throw ContractGuard.Invalid(nameof(Span), "length must equal the returned content UTF-16 length");
            }
        }

        if (Usage.Bytes > Budget.MaxBytes || Usage.Lines > Budget.MaxLines)
        {
            throw ContractGuard.Invalid(nameof(Usage), "must not exceed the declared source budget");
        }

        ContractGuard.Defined(ExhaustedBy, nameof(ExhaustedBy));
        if (Truncated == (ExhaustedBy == BudgetExhaustion.None))
        {
            throw ContractGuard.Invalid(nameof(ExhaustedBy), "must identify the exhausted limit exactly when source is truncated");
        }
    }

    private static int CountLogicalLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                lines++;
                if (index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (content[index] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}
