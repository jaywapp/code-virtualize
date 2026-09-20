using System.Text.Json;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Search;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Cli.Commands;

internal static class FindCommand
{
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        try
        {
            var workspace = CliOptions.Read(args, "--workspace");
            var store = CliOptions.StorePath(args, workspace);
            var format = CliOptions.Format(args);
            var limitText = CliOptions.Read(args, "--limit");
            var limit = 50;
            if (limitText is not null && !int.TryParse(limitText, out limit))
            {
                throw new ArgumentException("--limit must be an integer.");
            }
            var query = new SymbolSearchQuery(
                CliOptions.Read(args, "--query") ?? PositionalQuery(args),
                CliOptions.Read(args, "--exact"),
                CliOptions.Read(args, "--qualified"),
                CliOptions.Read(args, "--name"),
                CliOptions.Read(args, "--kind"),
                CliOptions.Read(args, "--accessibility"),
                CliOptions.Read(args, "--project"),
                CliOptions.Read(args, "--path"),
                limit,
                CliOptions.Read(args, "--cursor"));
            var response = new SymbolSearchService().Search(store, query);
            if (format == "json")
            {
                output.WriteLine(ContractJson.Serialize(response));
            }
            else
            {
                output.WriteLine($"Generation    {response.GenerationId}");
                output.WriteLine($"Status        {response.Status.ToString().ToLowerInvariant()}");
                output.WriteLine($"Coverage      {response.Coverage.Level.ToString().ToLowerInvariant()} / {response.Coverage.Scope}");
                foreach (var result in response.Results)
                {
                    var symbol = result.Deserialize<SymbolContract>(ContractJsonOptions())!;
                    var location = symbol.Declarations[0].Location;
                    output.WriteLine($"{symbol.Kind,-14} {symbol.Accessibility,-20} {symbol.QualifiedName} [{symbol.ProjectId}]");
                    output.WriteLine($"  {symbol.SymbolId} {location.Path}:{location.Span.StartLine}");
                }

                if (response.NextCursor is not null) output.WriteLine($"NextCursor    {response.NextCursor}");
                foreach (var limitation in response.Coverage.Limitations) output.WriteLine($"Limitation    {limitation}");
                foreach (var item in response.Errors) error.WriteLine($"{item.Code}: {item.Message}");
            }

            if (response.Status == ResponseStatus.Error) return 2;
            return response.Status == ResponseStatus.Partial || response.Coverage.Level != CoverageLevel.CompleteWithinScope ? 3 : 0;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch (StorageException exception)
        {
            error.WriteLine($"{exception.ErrorCode}: {exception.Message}");
            return 4;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(exception.Message);
            return 4;
        }
    }

    private static string? PositionalQuery(IReadOnlyList<string> args)
    {
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            return args[index];
        }

        return null;
    }

    private static System.Text.Json.JsonSerializerOptions ContractJsonOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

