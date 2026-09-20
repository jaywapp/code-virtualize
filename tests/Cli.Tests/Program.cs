var helpOutput = new StringWriter();
var helpExitCode = CliApplication.Run(["--help"], helpOutput, TextWriter.Null);
Assert(helpExitCode == 0 && helpOutput.ToString().Contains("syntax-only", StringComparison.Ordinal), "Help must describe the safe default.");

var output = new StringWriter();
var exitCode = CliApplication.Run(["analyze", "--workspace", Path.GetTempPath()], output, TextWriter.Null);
Assert(exitCode == 0, "Default CLI analysis must complete without a semantic load.");
Assert(output.ToString().Contains("syntactic", StringComparison.Ordinal), "Default CLI analysis must report syntactic coverage.");

ResolutionCliTests.Run();
LifecycleCliTests.Run();

Console.WriteLine("CLI contract tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
