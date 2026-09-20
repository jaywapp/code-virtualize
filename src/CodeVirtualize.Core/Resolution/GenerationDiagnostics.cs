using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Resolution;

public sealed class GenerationDiagnostics
{
    public ResponseContract Validate(string workspace, string store, string? generation = null)
    {
        using var reader = new GenerationStore(store).OpenCurrent();
        var manifest = reader.Manifest;
        if (generation is not null && generation != manifest.GenerationId) return Error(manifest, ResolutionErrorCodes.GenerationNotCurrent, "Requested generation is not current.");
        var issues = new List<ValidationIssue>(); var verified = 0; var stale = 0; var missing = 0; var invalid = 0;
        foreach (var file in manifest.Files)
        {
            string path;
            try { path = SourceResolver.WorkspacePath(workspace, file.Path); }
            catch (ArgumentException e) { invalid++; issues.Add(new(file.Path, ResolutionErrorCodes.PathOutsideWorkspace, e.Message)); continue; }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { missing++; issues.Add(new(file.Path, ResolutionErrorCodes.FileMissing, "Indexed source file is missing.")); continue; }
            if (SourceResolver.Digest(bytes) != file.ContentHash)
            { stale++; issues.Add(new(file.Path, ResolutionErrorCodes.SourceStale, "Source digest differs from index.")); continue; }
            try { _ = SourceResolver.Decode(bytes, file.Encoding); }
            catch (DecoderFallbackException)
            { invalid++; issues.Add(new(file.Path, ResolutionErrorCodes.EncodingInvalid, "Source encoding differs from manifest.")); continue; }
            verified++;
        }
        var result = new ValidationResult(manifest.Files.Count, verified, stale, missing, invalid, issues);
        var coverage = issues.Count == 0 ? manifest.Coverage : Partial(manifest.Coverage, "source-validation-failed");
        var status = issues.Count != 0 || coverage.Level != CoverageLevel.CompleteWithinScope ? ResponseStatus.Partial : ResponseStatus.Ok;
        var response = new ResponseContract(Id(), manifest.GenerationId, status,
            new(issues.Count == 0 ? FreshnessState.Verified : FreshnessState.Stale, issues.Count == 0 ? FreshnessState.Verified : FreshnessState.Stale),
            coverage, [ContractJson.ToElement(result)], false, null, new(false, null), new(RepairStatus.NotRequested, null),
            issues.Select(x => new ErrorContract(x.Code, x.Message, false, new Dictionary<string, string> { ["path"] = x.Path })).ToArray(), null, null);
        response.Validate(); return response;
    }

    public ResponseContract Inspect(string store, string? generation = null)
    {
        using var reader = new GenerationStore(store).OpenCurrent(); var m = reader.Manifest;
        if (generation is not null && generation != m.GenerationId) return Error(m, ResolutionErrorCodes.GenerationNotCurrent, "Requested generation is not current.");
        var result = new InspectionResult(m.GenerationId, m.WorkspaceKey, m.AnalysisKey, m.CreatedAt, m.State.ToString().ToLowerInvariant(),
            m.Files.Count, m.Projects.Count, m.Shards.Count, m.Projects, m.Shards);
        var status = m.Coverage.Level == CoverageLevel.CompleteWithinScope ? ResponseStatus.Ok : ResponseStatus.Partial;
        var response = new ResponseContract(Id(), m.GenerationId, status, new(FreshnessState.NotApplicable, FreshnessState.Unknown),
            m.Coverage, [ContractJson.ToElement(result)], false, null, new(false, null), new(RepairStatus.NotRequested, null), [], null, null);
        response.Validate(); return response;
    }

    private static CoverageContract Partial(CoverageContract c, string reason) => c with
        { Level = CoverageLevel.Partial, Limitations = c.Limitations.Append(reason).Distinct().Order().ToArray() };
    private static ResponseContract Error(ManifestContract m, string code, string message)
    { var r = new ResponseContract(Id(), m.GenerationId, ResponseStatus.Error, new(FreshnessState.NotApplicable, FreshnessState.Unknown), Partial(m.Coverage, code.ToLowerInvariant().Replace('_', '-')),
        [], false, null, new(false, null), new(RepairStatus.NotRequested, null), [new(code, message, false, new Dictionary<string, string>())], null, null); r.Validate(); return r; }
    private static string Id() => $"req_{Guid.NewGuid():N}";
}
