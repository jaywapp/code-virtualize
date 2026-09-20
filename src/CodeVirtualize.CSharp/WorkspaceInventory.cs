using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.CSharp;

internal sealed record InventorySourceFile(string PhysicalPath, string RelativePath, string DocumentPath);

internal sealed class InventoryProject
{
    public required string ProjectId { get; init; }
    public required string Identity { get; init; }
    public required string RelativeProjectPath { get; init; }
    public required string ProjectDirectory { get; init; }
    public required string? TargetFramework { get; init; }
    public required IReadOnlyList<string> Defines { get; init; }
    public required string ReferencesFingerprint { get; init; }
    public required ProjectLoadStatus LoadStatus { get; init; }
    public required List<string> Limitations { get; init; }
    public required List<InventorySourceFile> Files { get; init; }
}

internal sealed record WorkspaceInventory(
    string WorkspaceRoot,
    IReadOnlyList<InventoryProject> Projects,
    IReadOnlyList<InventorySourceFile> AllFiles,
    int ExcludedFileCount,
    IReadOnlyList<string> Limitations);

internal static class WorkspaceInventoryReader
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".cv", ".code-virtualize", "bin", "obj"
    };

    public static WorkspaceInventory Read(string workspacePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workspace does not exist: {root}");
        }

        EnsureNoReparsePoint(root);
        var projectPaths = EnumerateFiles(root, "*.csproj").Order(StringComparer.Ordinal).ToArray();
        var projects = new List<InventoryProject>();
        var inventoryLimitations = new List<string> { "default-excluded-directories:.git,.cv,.code-virtualize,bin,obj" };
        var excludedFileCount = CountExcludedCSharpFiles(root, inventoryLimitations);

        foreach (var projectPath in projectPaths)
        {
            projects.Add(ReadProject(root, projectPath));
        }

        if (projects.Count == 0)
        {
            var files = EnumerateFiles(root, "*.cs")
                .Select(path => ToSourceFile(root, path, Path.GetRelativePath(root, path)))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
            projects.Add(new InventoryProject
            {
                ProjectId = CreateProjectId("workspace"),
                Identity = "workspace",
                RelativeProjectPath = "<workspace>",
                ProjectDirectory = root,
                TargetFramework = null,
                Defines = [],
                ReferencesFingerprint = HashUtf8("workspace-no-project"),
                LoadStatus = ProjectLoadStatus.Analyzed,
                Limitations = ["no-project-file-syntax-inventory"],
                Files = files
            });
            inventoryLimitations.Add("workspace-has-no-project-files");
            if (files.Count == 0)
            {
                inventoryLimitations.Add("workspace-contains-no-csharp-source-files");
            }
        }
        else
        {
            AddOrphanFiles(root, projects, inventoryLimitations);
        }

        var allFiles = projects.SelectMany(project => project.Files)
            .GroupBy(file => file.PhysicalPath, PathComparer)
            .Select(group => group.First())
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return new WorkspaceInventory(root, projects.OrderBy(project => project.Identity, StringComparer.Ordinal).ToArray(), allFiles, excludedFileCount, inventoryLimitations);
    }

    private static InventoryProject ReadProject(string root, string projectPath)
    {
        var relativeProjectPath = Normalize(Path.GetRelativePath(root, projectPath));
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var projectId = CreateProjectId(relativeProjectPath);
        var identity = $"{Normalize(Path.GetFullPath(root))}|{relativeProjectPath}";
        var projectBytes = File.ReadAllBytes(projectPath);
        var referencesFingerprint = HashBytes(projectBytes);

        try
        {
            using var stream = new MemoryStream(projectBytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var elements = document.Descendants().ToArray();
            var targetFramework = elements.FirstOrDefault(element => element.Name.LocalName == "TargetFramework")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(targetFramework))
            {
                targetFramework = elements.FirstOrDefault(element => element.Name.LocalName == "TargetFrameworks")?.Value.Trim();
            }

            var defines = elements.Where(element => element.Name.LocalName == "DefineConstants")
                .SelectMany(element => element.Value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var defaultCompileItems = !string.Equals(
                elements.LastOrDefault(element => element.Name.LocalName == "EnableDefaultCompileItems")?.Value.Trim(),
                "false",
                StringComparison.OrdinalIgnoreCase);

            var limitations = new List<string> { "project-file-read-without-msbuild-evaluation" };
            var files = new Dictionary<string, InventorySourceFile>(PathComparer);
            if (defaultCompileItems)
            {
                foreach (var file in EnumerateFiles(projectDirectory, "*.cs"))
                {
                    files[file] = ToSourceFile(root, file, Path.GetRelativePath(projectDirectory, file));
                }
            }

            foreach (var compile in elements.Where(element => element.Name.LocalName == "Compile"))
            {
                var include = compile.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Include")?.Value;
                var remove = compile.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Remove")?.Value;
                if (!string.IsNullOrWhiteSpace(remove))
                {
                    ApplyRemove(projectDirectory, remove, files, limitations);
                }

                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                if (ContainsMsBuildExpressionOrWildcard(include))
                {
                    limitations.Add($"unevaluated-compile-include:{Normalize(include)}");
                    continue;
                }

                var physicalPath = Path.GetFullPath(Path.Combine(projectDirectory, include));
                if (!IsWithin(root, physicalPath) || !File.Exists(physicalPath) || !string.Equals(Path.GetExtension(physicalPath), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    limitations.Add($"unavailable-compile-include:{Normalize(include)}");
                    continue;
                }

                EnsureNoReparsePoints(root, physicalPath);
                var link = compile.Elements().FirstOrDefault(element => element.Name.LocalName == "Link")?.Value.Trim();
                var documentPath = string.IsNullOrWhiteSpace(link) ? include : link;
                files[physicalPath] = ToSourceFile(root, physicalPath, documentPath);
            }

            return new InventoryProject
            {
                ProjectId = projectId,
                Identity = identity,
                RelativeProjectPath = relativeProjectPath,
                ProjectDirectory = projectDirectory,
                TargetFramework = targetFramework,
                Defines = defines,
                ReferencesFingerprint = referencesFingerprint,
                LoadStatus = ProjectLoadStatus.Analyzed,
                Limitations = limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                Files = files.Values.OrderBy(file => file.DocumentPath, StringComparer.Ordinal).ThenBy(file => file.RelativePath, StringComparer.Ordinal).ToList()
            };
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException)
        {
            var files = EnumerateFiles(projectDirectory, "*.cs")
                .Select(path => ToSourceFile(root, path, Path.GetRelativePath(projectDirectory, path)))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
            return new InventoryProject
            {
                ProjectId = projectId,
                Identity = identity,
                RelativeProjectPath = relativeProjectPath,
                ProjectDirectory = projectDirectory,
                TargetFramework = null,
                Defines = [],
                ReferencesFingerprint = referencesFingerprint,
                LoadStatus = ProjectLoadStatus.Failed,
                Limitations = ["project-file-malformed"],
                Files = files
            };
        }
    }

    private static void AddOrphanFiles(string root, List<InventoryProject> projects, List<string> limitations)
    {
        var claimed = new HashSet<string>(projects.SelectMany(project => project.Files).Select(file => file.PhysicalPath), PathComparer);
        var orphanFiles = EnumerateFiles(root, "*.cs")
            .Where(path => !claimed.Contains(path))
            .Select(path => ToSourceFile(root, path, Path.GetRelativePath(root, path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
        if (orphanFiles.Count == 0)
        {
            return;
        }

        limitations.Add("csharp-files-outside-discovered-project-inventory");
        projects.Add(new InventoryProject
        {
            ProjectId = CreateProjectId("orphan-files"),
            Identity = "orphan-files",
            RelativeProjectPath = "<orphan-files>",
            ProjectDirectory = root,
            TargetFramework = null,
            Defines = [],
            ReferencesFingerprint = HashUtf8("orphan-files"),
            LoadStatus = ProjectLoadStatus.Unknown,
            Limitations = ["files-not-associated-with-a-safely-discovered-project"],
            Files = orphanFiles
        });
    }

    private static void ApplyRemove(
        string projectDirectory,
        string remove,
        IDictionary<string, InventorySourceFile> files,
        ICollection<string> limitations)
    {
        if (ContainsMsBuildExpressionOrWildcard(remove))
        {
            limitations.Add($"unevaluated-compile-remove:{Normalize(remove)}");
            return;
        }

        var path = Path.GetFullPath(Path.Combine(projectDirectory, remove));
        files.Remove(path);
    }

    private static bool ContainsMsBuildExpressionOrWildcard(string value) =>
        value.Contains("$(", StringComparison.Ordinal) || value.IndexOfAny(['*', '?']) >= 0;

    private static InventorySourceFile ToSourceFile(string root, string path, string documentPath)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureNoReparsePoints(root, fullPath);
        return new InventorySourceFile(fullPath, Normalize(Path.GetRelativePath(root, fullPath)), Normalize(documentPath));
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureNoReparsePoint(directory);
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                {
                    yield return Path.GetFullPath(file);
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).OrderDescending(StringComparer.Ordinal))
            {
                if (ExcludedDirectories.Contains(Path.GetFileName(child)))
                {
                    continue;
                }

                EnsureNoReparsePoint(child);
                pending.Push(child);
            }
        }
    }

    private static int CountExcludedCSharpFiles(string root, ICollection<string> limitations)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (ExcludedDirectories.Contains(Path.GetFileName(child)))
                    {
                        count += CountCSharpFilesSafely(child);
                    }
                    else
                    {
                        pending.Push(child);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            limitations.Add("excluded-file-count-incomplete");
        }

        return count;
    }

    private static int CountCSharpFilesSafely(string root)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            count += Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly).Count();
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                }
            }
        }

        return count;
    }
    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        if (!IsWithin(root, path))
        {
            throw new IOException("A source path escapes the workspace boundary.");
        }

        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        EnsureNoReparsePoint(current);
        foreach (var part in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                EnsureNoReparsePoint(current);
            }
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Workspace inventory does not cross reparse points.");
        }
    }

    private static string CreateProjectId(string identity) => $"proj_{HashHex(identity)}";

    internal static string HashUtf8(string value) => $"sha256:{HashHex(value)}";

    internal static string HashBytes(ReadOnlySpan<byte> bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static string HashHex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string Normalize(string path) => path.Replace('\\', '/');

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}


