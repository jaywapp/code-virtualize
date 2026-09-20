namespace CodeVirtualize.Cli.Commands;

internal static class CliOptions
{
    public static string? Read(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Missing value for option: {option}.");
                }

                return args[index + 1];
            }
        }

        return null;
    }

    public static bool Has(IReadOnlyList<string> args, string option) =>
        args.Any(argument => string.Equals(argument, option, StringComparison.Ordinal));

    public static IReadOnlyList<string> ReadAll(IReadOnlyList<string> args, string option)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Missing value for option: {option}.");
                }

                values.Add(args[++index]);
            }
        }

        return values;
    }
    public static string Format(IReadOnlyList<string> args)
    {
        var format = Read(args, "--format") ?? "text";
        if (format is not ("text" or "json"))
        {
            throw new ArgumentException("--format must be text or json.");
        }

        return format;
    }

    public static string StorePath(IReadOnlyList<string> args, string? workspace)
    {
        var store = Read(args, "--store");
        if (!string.IsNullOrWhiteSpace(store)) return Path.GetFullPath(store);
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("Either --store or --workspace is required.");
        return Path.Combine(Path.GetFullPath(workspace), ".code-virtualize");
    }
}
