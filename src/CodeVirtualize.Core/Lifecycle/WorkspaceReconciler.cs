using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Lifecycle;

public enum WorkspaceChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed
}

public sealed record WorkspaceChange(
    WorkspaceChangeKind Kind,
    string Path,
    string? PreviousPath,
    string? PreviousContentHash,
    string? ContentHash,
    IReadOnlyList<string> ProjectIds);

public sealed record ProjectInvalidation(string ProjectId, string Reason);

public sealed record WorkspaceReconciliation(
    IReadOnlyList<WorkspaceChange> Changes,
    IReadOnlyList<ProjectInvalidation> InvalidatedProjects,
    IReadOnlyList<string> UnreportedPaths,
    bool AnalysisConfigurationChanged)
{
    public bool HasChanges => Changes.Count != 0 || AnalysisConfigurationChanged;
}

public static class WorkspaceReconciler
{
    public static WorkspaceReconciliation Compare(
        ManifestContract previous,
        ManifestContract current,
        IReadOnlyCollection<string>? changedPathHints = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var oldFiles = previous.Files.ToDictionary(file => Normalize(file.Path), comparer);
        var newFiles = current.Files.ToDictionary(file => Normalize(file.Path), comparer);
        var changes = new List<WorkspaceChange>();

        foreach (var path in oldFiles.Keys.Intersect(newFiles.Keys, comparer).Order(StringComparer.Ordinal))
        {
            var oldFile = oldFiles[path];
            var newFile = newFiles[path];
            if (!string.Equals(oldFile.ContentHash, newFile.ContentHash, StringComparison.Ordinal) ||
                !oldFile.ProjectIds.Order(StringComparer.Ordinal).SequenceEqual(newFile.ProjectIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                changes.Add(new WorkspaceChange(
                    WorkspaceChangeKind.Modified,
                    newFile.Path,
                    null,
                    oldFile.ContentHash,
                    newFile.ContentHash,
                    Projects(oldFile, newFile)));
            }
        }

        var deleted = oldFiles.Keys.Except(newFiles.Keys, comparer)
            .Select(path => oldFiles[path])
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var added = newFiles.Keys.Except(oldFiles.Keys, comparer)
            .Select(path => newFiles[path])
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        foreach (var hash in deleted.Select(file => file.ContentHash)
                     .Intersect(added.Select(file => file.ContentHash), StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var oldMatches = deleted.Where(file => file.ContentHash == hash).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            var newMatches = added.Where(file => file.ContentHash == hash).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < Math.Min(oldMatches.Length, newMatches.Length); index++)
            {
                var oldFile = oldMatches[index];
                var newFile = newMatches[index];
                changes.Add(new WorkspaceChange(
                    WorkspaceChangeKind.Renamed,
                    newFile.Path,
                    oldFile.Path,
                    oldFile.ContentHash,
                    newFile.ContentHash,
                    Projects(oldFile, newFile)));
                deleted.Remove(oldFile);
                added.Remove(newFile);
            }
        }

        changes.AddRange(deleted.Select(file => new WorkspaceChange(
            WorkspaceChangeKind.Deleted,
            file.Path,
            null,
            file.ContentHash,
            null,
            file.ProjectIds.Order(StringComparer.Ordinal).ToArray())));
        changes.AddRange(added.Select(file => new WorkspaceChange(
            WorkspaceChangeKind.Added,
            file.Path,
            null,
            null,
            file.ContentHash,
            file.ProjectIds.Order(StringComparer.Ordinal).ToArray())));

        var configChanged = !string.Equals(previous.AnalysisKey, current.AnalysisKey, StringComparison.Ordinal);
        var invalidations = new Dictionary<string, string>(StringComparer.Ordinal);
        var semanticResultsPresent = previous.Projects.Concat(current.Projects)
            .Any(project => project.AnalysisLevel == ContractAnalysisLevel.Semantic);
        if (configChanged || ProjectConfigurationChanged(previous, current) ||
            semanticResultsPresent && changes.Count != 0)
        {
            var reason = configChanged || ProjectConfigurationChanged(previous, current)
                ? "semantic-configuration-or-project-graph-changed"
                : "semantic-dependent-source-changed";
            foreach (var projectId in previous.Projects.Select(project => project.ProjectId)
                         .Concat(current.Projects.Select(project => project.ProjectId))
                         .Distinct(StringComparer.Ordinal))
            {
                invalidations[projectId] = reason;
            }
        }
        else
        {
            foreach (var projectId in changes.SelectMany(change => change.ProjectIds).Distinct(StringComparer.Ordinal))
            {
                invalidations[projectId] = "source-inventory-or-content-changed";
            }
        }

        var hints = (changedPathHints ?? Array.Empty<string>()).Select(Normalize).ToHashSet(comparer);
        var changedPaths = changes.SelectMany(change => change.PreviousPath is null ? [change.Path] : new[] { change.Path, change.PreviousPath })
            .Select(Normalize)
            .ToHashSet(comparer);
        var unreported = hints.Count == 0
            ? Array.Empty<string>()
            : changedPaths.Where(path => !hints.Contains(path)).Order(StringComparer.Ordinal).ToArray();

        return new WorkspaceReconciliation(
            changes.OrderBy(change => change.Path, StringComparer.Ordinal).ThenBy(change => change.Kind).ToArray(),
            invalidations.Select(item => new ProjectInvalidation(item.Key, item.Value))
                .OrderBy(item => item.ProjectId, StringComparer.Ordinal).ToArray(),
            unreported,
            configChanged);
    }

    private static bool ProjectConfigurationChanged(ManifestContract previous, ManifestContract current)
    {
        static string Signature(ManifestProjectContract project) =>
            string.Join("|", project.ProjectId, project.TargetFramework, project.Configuration,
                string.Join(",", project.Defines.Order(StringComparer.Ordinal)), project.ReferencesFingerprint,
                project.LoadStatus, project.AnalysisLevel);
        return !previous.Projects.Select(Signature).Order(StringComparer.Ordinal)
            .SequenceEqual(current.Projects.Select(Signature).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> Projects(ManifestFileContract first, ManifestFileContract second) =>
        first.ProjectIds.Concat(second.ProjectIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string Normalize(string path) => path.Replace('\\', '/');
}