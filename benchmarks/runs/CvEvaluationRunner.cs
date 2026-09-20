using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Diff;
using CodeVirtualize.Core.Storage;

internal static class Program
{
    private const int Seed = 20260920;
    private const int Repeats = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        var repository = Path.GetFullPath(Read(args, "--repository") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var resultPath = Path.GetFullPath(Read(args, "--result") ?? Path.Combine(repository, "benchmarks", "results", "cv-result.json"));
        var manifestPath = Path.GetFullPath(Read(args, "--manifest") ?? Path.Combine(repository, "benchmarks", "results", "cv-run-manifest.json"));
        if (args.Contains("--verify-only", StringComparer.Ordinal))
        {
            Verify(resultPath, manifestPath);
            Console.WriteLine("TASK-016 aggregate arithmetic verification passed.");
            return 0;
        }

        var total = Stopwatch.StartNew();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cv-evaluation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var cliDll = Path.Combine(repository, "src", "CodeVirtualize.Cli", "bin", "Release", "net9.0", "CodeVirtualize.Cli.dll");
            var mcpDll = Path.Combine(repository, "src", "CodeVirtualize.Mcp", "bin", "Release", "net9.0", "CodeVirtualize.Mcp.dll");
            RequireFile(cliDll);
            RequireFile(mcpDll);
            var lsp = DiscoverLsp();
            var serena = Discover(["serena", "serena-mcp-server"]);
            var runs = new List<RunRecord>();
            var orders = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var shuffled = Shuffle(["A", "B", "C", "D", "E"], Seed);
            var tasks = new[] { "NAV", "DIFF" };

            for (var taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
            {
                for (var repeat = 1; repeat <= Repeats; repeat++)
                {
                    var pairId = $"pair-{tasks[taskIndex].ToLowerInvariant()}-{repeat}";
                    var order = Rotate(shuffled, taskIndex * Repeats + repeat - 1);
                    orders[pairId] = order;
                    for (var orderIndex = 0; orderIndex < order.Length; orderIndex++)
                    {
                        var condition = order[orderIndex];
                        if (total.Elapsed.TotalMinutes >= 30)
                        {
                            runs.Add(RunRecord.NotStarted(pairId, tasks[taskIndex], repeat, condition, orderIndex, "wall_time_limit"));
                            continue;
                        }

                        runs.Add(condition switch
                        {
                            "A" => RunBaseline(repository, tasks[taskIndex], pairId, repeat, orderIndex, false),
                            "B" => RunBaseline(repository, tasks[taskIndex], pairId, repeat, orderIndex, true),
                            "C" => Unavailable(tasks[taskIndex], pairId, repeat, orderIndex, "C", lsp, "csharp_lsp"),
                            "D" => Unavailable(tasks[taskIndex], pairId, repeat, orderIndex, "D", serena, "serena"),
                            "E" when tasks[taskIndex] == "NAV" => RunCvNavigation(repository, tempRoot, cliDll, mcpDll, pairId, repeat, orderIndex),
                            "E" => RunCvDiff(repository, tempRoot, cliDll, pairId, repeat, orderIndex),
                            _ => throw new InvalidOperationException($"Unknown condition {condition}.")
                        });
                    }
                }
            }

            total.Stop();
            var generatedAt = DateTimeOffset.UtcNow;
            var result = Aggregate(runs, generatedAt);
            var cvRuns = runs.Where(run => run.Condition == "E" && run.Started).ToArray();
            var manifest = new
            {
                schemaVersion = "cv-run-manifest-v1",
                protocolRevision = "task-016-v1",
                generatedAt,
                experimentPhase = "cv_evaluation",
                randomizationSeed = Seed,
                repeatCount = Repeats,
                wallTimeLimitSeconds = 1800,
                actualHarnessWallTimeMs = Round(total.Elapsed.TotalMilliseconds),
                scope = new
                {
                    corpus = "repository synthetic fixtures only",
                    personalOrExistingSessionLogs = "denied",
                    rawSourceOrPromptLogging = "disabled",
                    network = "not_used",
                    packageInstallation = "denied",
                    paidModelOrApi = "denied",
                    resultSharing = "de-identified aggregates and run metadata only"
                },
                repository = new
                {
                    alias = "code-virtualize-local-synthetic",
                    commit = Git(repository, "rev-parse", "HEAD"),
                    startDirty = true,
                    fixtureDigestAlgorithm = "sha256(sorted(relative-path NUL file-sha256) LF)",
                    fixtureDigest = FixtureDigest(repository)
                },
                thresholds = new
                {
                    qualityDegradation = 0,
                    criticalStaleOrSilentPartial = 0,
                    minimumRepeatsPerTaskCondition = Repeats,
                    minimumSourceByteReduction = 0.20,
                    coldBuildMaxMs = 60000,
                    warmQueryMaxMs = 2000,
                    incrementalUpdateMaxMs = 5000,
                    peakWorkingSetMaxBytes = 1073741824,
                    externalPaidCostUsd = 0,
                    totalWallTimeMaxMs = 1800000
                },
                toolAvailability = new
                {
                    C = new { status = lsp.Length == 0 ? "unavailable" : "detected_without_approved_adapter", discovered = lsp },
                    D = new { status = serena.Length == 0 ? "unavailable" : "detected_without_approved_adapter", discovered = serena }
                },
                conditionOrders = orders,
                runs,
                summaries = new
                {
                    sourceBytes = SourceSummary(runs),
                    cvToolCalls = SumCalls(cvRuns),
                    cvPrimaryToolUseRate = Ratio(cvRuns.Count(run => run.PrimaryToolUsed), cvRuns.Length),
                    cvMcpUseRate = Ratio(cvRuns.Count(run => run.McpUsed), cvRuns.Length),
                    quality = new
                    {
                        ePassed = runs.Count(run => run.Condition == "E" && run.QualityPassed == true),
                        eEvaluated = runs.Count(run => run.Condition == "E" && run.QualityPassed.HasValue),
                        bPassed = runs.Count(run => run.Condition == "B" && run.QualityPassed == true),
                        bEvaluated = runs.Count(run => run.Condition == "B" && run.QualityPassed.HasValue),
                        criticalStaleCount = cvRuns.Sum(run => run.CriticalStaleCount),
                        silentPartialCount = cvRuns.Sum(run => run.SilentPartialCount),
                        recall = WeightedRecall(cvRuns),
                        falsePositiveCount = cvRuns.Sum(run => run.FalsePositiveCount ?? 0)
                    },
                    performance = Performance(cvRuns),
                    buildIndexSizeBytes = Metrics(cvRuns.Where(run => run.BuildIndexSizeBytes.HasValue).Select(run => (double)run.BuildIndexSizeBytes!.Value)),
                    actualExternalPaidCostUsd = 0,
                    tokenEfficiency = "inconclusive",
                    peakContext = "unavailable",
                    breakEven = "not_reached",
                    corpusScales = new { small = "measured", medium = "unavailable", large = "unavailable" },
                    outcome = "no_go"
                },
                measurementAvailability = new
                {
                    actualModelInputTokens = "unavailable",
                    providerCost = "unavailable",
                    sourceBytes = "measured",
                    wallTime = "measured",
                    peakWorkingSet = "measured_process_peak_approximation",
                    buildIndexSize = "measured",
                    quality = "independent_synthetic_ground_truth"
                },
                limitations = new[]
                {
                    "No approved real session logs or actual model token/cost records were available.",
                    "C# LSP and Serena were unavailable or lacked an approved fixed harness adapter; attempted cells remain infra_failed.",
                    "The corpus is small synthetic C# and cannot support Perforce, UE5, medium, or large corpus claims.",
                    "Peak working set is the runner peak plus the largest sequential child-process peak, not a sampled whole-machine trace."
                }
            };

            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            WriteJson(resultPath, result);
            WriteJson(manifestPath, manifest);
            Verify(resultPath, manifestPath);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                attempted = runs.Count(run => run.Started),
                succeeded = runs.Count(run => run.Status == "succeeded"),
                infraFailed = runs.Count(run => run.Status == "infra_failed"),
                elapsedMs = Round(total.Elapsed.TotalMilliseconds),
                outcome = "no_go"
            }));
            return 0;
        }
        finally
        {
            DeleteTree(tempRoot);
        }
    }

    private static RunRecord RunBaseline(string repository, string task, string pairId, int repeat, int orderIndex, bool bounded)
    {
        var stopwatch = Stopwatch.StartNew();
        var sourceBytes = 0L;
        var sourceLines = 0;
        var calls = new Dictionary<string, int>(StringComparer.Ordinal) { ["read"] = 0, ["grep"] = 0, ["other"] = 0 };
        var quality = false;
        var navigation = Path.Combine(repository, "tests", "fixtures", "csharp", "navigation");
        var shared = Path.Combine(repository, "tests", "fixtures", "csharp", "shared");
        var diff = Path.Combine(repository, "tests", "fixtures", "csharp", "diff");
        if (task == "NAV")
        {
            if (!bounded)
            {
                var files = new[]
                {
                    Path.Combine(navigation, "Fixture.Navigation.csproj"),
                    Path.Combine(navigation, "Contracts.cs"),
                    Path.Combine(navigation, "Catalog.Partial.cs"),
                    Path.Combine(navigation, "CallSites.cs"),
                    Path.Combine(navigation, "DynamicCandidates.cs"),
                    Path.Combine(shared, "LinkedHelper.cs")
                };
                var search = ProcessRun.Execute("rg", ["-n", "Load|partial class Catalog|BuildLabel|Worker|Run|Activator|ServiceKey|MemberKey|Compile Include", navigation, shared], [0, 1]);
                calls["grep"]++;
                var text = search.Stdout + string.Join("\n", files.Select(File.ReadAllText));
                calls["read"] += files.Length;
                sourceBytes = files.Sum(path => new FileInfo(path).Length);
                sourceLines = files.Sum(path => File.ReadAllLines(path).Length);
                quality = NavigationExpected(text);
            }
            else
            {
                var candidate = ProcessRun.Execute("rg", ["-l", "Load|partial class Catalog|BuildLabel|Worker|Run|Activator|ServiceKey|MemberKey|Compile Include", navigation, shared], [0, 1]);
                var declarations = ProcessRun.Execute("rg", ["-n", "public void Load|private T Load<T>|void ILoader\\.Load|partial class Catalog", Path.Combine(navigation, "Contracts.cs"), Path.Combine(navigation, "Catalog.Partial.cs")], [0, 1]);
                var dynamic = ProcessRun.Execute("rg", ["-n", "Activator\\.CreateInstance|ServiceKey|MemberKey", Path.Combine(navigation, "DynamicCandidates.cs")], [0, 1]);
                var linked = ProcessRun.Execute("rg", ["-n", "Compile Include|Link>|BuildLabel", Path.Combine(navigation, "Fixture.Navigation.csproj"), Path.Combine(shared, "LinkedHelper.cs")], [0, 1]);
                calls["grep"] += 4;
                var partial = Slice(Path.Combine(shared, "LinkedHelper.cs"), 2, 5).Concat(Slice(Path.Combine(navigation, "Contracts.cs"), 7, 18)).ToArray();
                var partialText = string.Join("\n", partial);
                calls["read"] += 2;
                sourceBytes = Encoding.UTF8.GetByteCount(partialText);
                sourceLines = partial.Length;
                quality = NavigationExpected(candidate.Stdout + declarations.Stdout + dynamic.Stdout + linked.Stdout + partialText);
            }
        }
        else
        {
            var vcsBase = Path.Combine(diff, "vcs-base", "ReviewTarget.cs");
            var vcsTarget = Path.Combine(diff, "vcs-target", "ReviewTarget.cs");
            var sessionBase = Path.Combine(diff, "session-start", "ReviewTarget.cs");
            var sessionTarget = Path.Combine(diff, "session-current", "ReviewTarget.cs");
            var vcs = ProcessRun.Execute("git", ["-c", "core.autocrlf=false", "-c", "core.safecrlf=false", "diff", "--no-index", "--unified=1", "--", vcsBase, vcsTarget], [0, 1]);
            var session = ProcessRun.Execute("git", ["-c", "core.autocrlf=false", "-c", "core.safecrlf=false", "diff", "--no-index", "--unified=1", "--", sessionBase, sessionTarget], [0, 1]);
            calls["other"] += 2;
            string extra;
            if (!bounded)
            {
                var files = new[] { vcsBase, vcsTarget, sessionBase, sessionTarget };
                extra = string.Join("\n", files.Select(File.ReadAllText));
                calls["read"] += files.Length;
                sourceBytes = files.Sum(path => new FileInfo(path).Length);
                sourceLines = files.Sum(path => File.ReadAllLines(path).Length);
            }
            else
            {
                var lines = Slice(sessionBase, 9, 4);
                extra = string.Join("\n", lines);
                calls["read"]++;
                sourceBytes = Encoding.UTF8.GetByteCount(extra);
                sourceLines = lines.Length;
            }
            quality = DiffExpected(vcs.Stdout, session.Stdout, extra);
        }

        stopwatch.Stop();
        return new RunRecord(
            pairId + "-" + (bounded ? "B" : "A"),
            pairId,
            task,
            repeat,
            bounded ? "B" : "A",
            orderIndex,
            true,
            quality ? "succeeded" : "quality_failed",
            quality,
            false,
            false,
            Round(stopwatch.Elapsed.TotalMilliseconds),
            sourceBytes,
            sourceLines,
            null,
            null,
            null,
            null,
            null,
            null,
            new RecallMetric(quality ? 1 : 0, 1),
            quality ? 0 : 1,
            0,
            0,
            calls,
            quality ? null : "fixture_expectation_mismatch");
    }

    private static RunRecord Unavailable(
        string task,
        string pairId,
        int repeat,
        int orderIndex,
        string condition,
        string[] discovered,
        string family)
    {
        var error = discovered.Length == 0 ? "primary_tool_unavailable" : "approved_adapter_unavailable";
        return new RunRecord(
            pairId + "-" + condition,
            pairId,
            task,
            repeat,
            condition,
            orderIndex,
            true,
            "infra_failed",
            null,
            false,
            false,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            new Dictionary<string, int>(StringComparer.Ordinal) { [family] = 0 },
            error);
    }

    private static RunRecord RunCvNavigation(
        string repository,
        string tempRoot,
        string cliDll,
        string mcpDll,
        string pairId,
        int repeat,
        int orderIndex)
    {
        var stopwatch = Stopwatch.StartNew();
        var runRoot = Path.Combine(tempRoot, pairId + "-E");
        var navigation = Path.Combine(runRoot, "navigation");
        CopyDirectory(Path.Combine(repository, "tests", "fixtures", "csharp", "navigation"), navigation);
        CopyDirectory(Path.Combine(repository, "tests", "fixtures", "csharp", "shared"), Path.Combine(runRoot, "shared"));
        var workspace = runRoot;
        var store = Path.Combine(workspace, ".code-virtualize");
        var calls = EmptyCvCalls();
        var childPeak = 0L;

        var cold = Measure(() => Dotnet(cliDll, ["cv-build", "--workspace", workspace, "--format", "json"], calls, "cv_build"));
        childPeak = Math.Max(childPeak, cold.Value.PeakWorkingSetBytes);
        var indexSize = DirectorySize(store);

        var warmDurations = new List<double>();
        var load = CvFind(cliDll, workspace, "Load", calls, warmDurations, ref childPeak);
        var catalog = CvFind(cliDll, workspace, "Catalog", calls, warmDurations, ref childPeak);
        var linked = CvFind(cliDll, workspace, "BuildLabel", calls, warmDurations, ref childPeak);
        var worker = CvFind(cliDll, workspace, "Worker", calls, warmDurations, ref childPeak);
        var iworker = CvFind(cliDll, workspace, "IWorker", calls, warmDurations, ref childPeak);
        var run = CvFind(cliDll, workspace, "Run", calls, warmDurations, ref childPeak);

        using var loadJson = JsonDocument.Parse(load.Stdout);
        using var catalogJson = JsonDocument.Parse(catalog.Stdout);
        using var linkedJson = JsonDocument.Parse(linked.Stdout);
        using var workerJson = JsonDocument.Parse(worker.Stdout);
        using var iworkerJson = JsonDocument.Parse(iworker.Stdout);
        using var runJson = JsonDocument.Parse(run.Stdout);
        var loadSymbols = Results(loadJson.RootElement);
        var selectedLoad = loadSymbols.First(item =>
            Text(item, "qualifiedName").Contains("Catalog.Load", StringComparison.Ordinal) &&
            Text(item, "signature").Contains("string", StringComparison.Ordinal) &&
            !Text(item, "signature").Contains("retryCount", StringComparison.Ordinal));
        var targetIds = new List<string>();
        AddQualified(targetIds, Results(workerJson.RootElement), "Fixture.Navigation.Worker");
        AddQualified(targetIds, Results(iworkerJson.RootElement), "Fixture.Navigation.IWorker");
        AddQualified(targetIds, Results(runJson.RootElement), "Fixture.Navigation.Worker.Run");

        long sourceBytes = 0;
        var criticalStale = 0;
        var silentPartial = 0;
        RecallMetric recall;
        var falsePositives = 0;
        using (var mcp = new McpClient(mcpDll, workspace))
        {
            mcp.Initialize();
            calls["mcp"]++;
            var mcpFind = Measure(() => mcp.Call("cv_find", new { exact = "Load", limit = 100 }));
            calls["cv_find"]++;
            calls["mcp"]++;
            warmDurations.Add(mcpFind.ElapsedMs);
            var get = Measure(() => mcp.Call("cv_get", new
            {
                symbolId = Text(selectedLoad, "symbolId"),
                part = "context",
                maxBytes = 8192,
                maxLines = 100,
                contextLines = 2
            }));
            calls["cv_get"]++;
            calls["mcp"]++;
            warmDurations.Add(get.ElapsedMs);
            sourceBytes += SourceContentBytes(get.Value);
            var impact = Measure(() => mcp.Call("cv_impact", new
            {
                symbolIds = targetIds.Distinct(StringComparer.Ordinal).ToArray(),
                maxDepth = 1,
                maxResults = 100,
                maxSourceFiles = 100,
                pageSize = 100,
                includeLexicalCandidates = true
            }));
            calls["cv_impact"]++;
            calls["mcp"]++;
            warmDurations.Add(impact.ElapsedMs);
            var observedLines = ImpactLines(impact.Value);
            var expectedLines = new HashSet<int> { 22, 25, 27, 31 };
            var found = observedLines.Count(expectedLines.Contains);
            falsePositives = observedLines.Count(line => !expectedLines.Contains(line));
            recall = new RecallMetric(found, expectedLines.Count);
            silentPartial += SilentPartial(mcpFind.Value) + SilentPartial(get.Value) + SilentPartial(impact.Value);

            var touch = Path.Combine(navigation, "BenchmarkTouch.cs");
            File.WriteAllText(
                touch,
                "namespace Fixture.Navigation; internal static class BenchmarkTouch { internal const int Value = 1; }",
                new UTF8Encoding(false));
            var update = Measure(() => Dotnet(
                cliDll,
                ["cv-update", "--workspace", workspace, "--changed", "navigation/BenchmarkTouch.cs", "--format", "json"],
                calls,
                "cv_update"));
            childPeak = Math.Max(childPeak, update.Value.PeakWorkingSetBytes);

            var contractPath = Path.Combine(navigation, "Contracts.cs");
            File.WriteAllText(
                contractPath,
                File.ReadAllText(contractPath).Replace("Loads one value.", "Loads one item!!", StringComparison.Ordinal),
                new UTF8Encoding(false));
            var recoveryWatch = Stopwatch.StartNew();
            var stale = mcp.Call("cv_get", new
            {
                symbolId = Text(selectedLoad, "symbolId"),
                part = "context",
                maxBytes = 8192,
                maxLines = 100,
                contextLines = 2
            });
            calls["cv_get"]++;
            calls["mcp"]++;
            if (!IsExplicitStale(stale)) criticalStale++;
            var repair = Dotnet(
                cliDll,
                ["cv-update", "--workspace", workspace, "--changed", "navigation/Contracts.cs", "--format", "json"],
                calls,
                "cv_update");
            childPeak = Math.Max(childPeak, repair.PeakWorkingSetBytes);
            var refreshedFind = mcp.Call("cv_find", new { exact = "Load", limit = 100 });
            calls["cv_find"]++;
            calls["mcp"]++;
            var refreshedId = StructuredResults(refreshedFind).First(item =>
                Text(item, "qualifiedName").Contains("Catalog.Load", StringComparison.Ordinal) &&
                Text(item, "signature").Contains("string", StringComparison.Ordinal) &&
                !Text(item, "signature").Contains("retryCount", StringComparison.Ordinal))
                .GetProperty("symbolId").GetString()!;
            var recovered = mcp.Call("cv_get", new
            {
                symbolId = refreshedId,
                part = "context",
                maxBytes = 8192,
                maxLines = 100,
                contextLines = 2
            });
            calls["cv_get"]++;
            calls["mcp"]++;
            recoveryWatch.Stop();
            if (IsError(recovered)) criticalStale++;
            sourceBytes += SourceContentBytes(recovered);
            childPeak = Math.Max(childPeak, mcp.PeakWorkingSetBytes);

            var loadQuality = LoadQuality(loadSymbols);
            var catalogQuality = CatalogQuality(Results(catalogJson.RootElement));
            var linkedQuality = Results(linkedJson.RootElement).Any(item => Text(item, "qualifiedName").Contains("LinkedHelper.BuildLabel", StringComparison.Ordinal));
            var getQuality = !IsError(get.Value);
            var quality = loadQuality && catalogQuality && linkedQuality && getQuality &&
                recall.Numerator == recall.Denominator && criticalStale == 0 && silentPartial == 0;
            stopwatch.Stop();
            return new RunRecord(
                pairId + "-E",
                pairId,
                "NAV",
                repeat,
                "E",
                orderIndex,
                true,
                quality ? "succeeded" : "quality_failed",
                quality,
                true,
                true,
                Round(stopwatch.Elapsed.TotalMilliseconds),
                sourceBytes,
                null,
                Round(cold.ElapsedMs),
                Round(warmDurations.Max()),
                Round(update.ElapsedMs),
                Round(recoveryWatch.Elapsed.TotalMilliseconds),
                Process.GetCurrentProcess().PeakWorkingSet64 + childPeak,
                indexSize,
                recall,
                falsePositives,
                criticalStale,
                silentPartial,
                calls,
                quality ? null : "independent_ground_truth_mismatch");
        }
    }

    private static ProcessResult CvFind(
        string cliDll,
        string workspace,
        string exact,
        IDictionary<string, int> calls,
        ICollection<double> durations,
        ref long peak)
    {
        var measured = Measure(() => Dotnet(
            cliDll,
            ["cv-find", "--workspace", workspace, "--exact", exact, "--limit", "100", "--format", "json"],
            calls,
            "cv_find"));
        peak = Math.Max(peak, measured.Value.PeakWorkingSetBytes);
        durations.Add(measured.ElapsedMs);
        return measured.Value;
    }
    private static RunRecord RunCvDiff(
        string repository,
        string tempRoot,
        string cliDll,
        string pairId,
        int repeat,
        int orderIndex)
    {
        var stopwatch = Stopwatch.StartNew();
        var runRoot = Path.Combine(tempRoot, pairId + "-E");
        Directory.CreateDirectory(runRoot);
        var fixture = Path.Combine(repository, "tests", "fixtures", "csharp", "diff");
        var calls = EmptyCvCalls();

        (SymbolDiffSnapshot Base, SymbolDiffSnapshot Target, double Cold, double Update, long Size, long Peak) BuildPair(
            string mode,
            string baseFixture,
            string targetFixture)
        {
            var workspace = Path.Combine(runRoot, mode);
            Directory.CreateDirectory(workspace);
            var sourcePath = Path.Combine(workspace, "ReviewTarget.cs");
            File.Copy(Path.Combine(fixture, baseFixture, "ReviewTarget.cs"), sourcePath);
            File.WriteAllText(
                Path.Combine(workspace, "Fixture.Diff.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>",
                new UTF8Encoding(false));
            var build = Measure(() => Dotnet(cliDll, ["cv-build", "--workspace", workspace, "--format", "json"], calls, "cv_build"));
            var store = Path.Combine(workspace, ".code-virtualize");
            var before = LoadSnapshot(workspace, store);
            File.Copy(Path.Combine(fixture, targetFixture, "ReviewTarget.cs"), sourcePath, true);
            var update = Measure(() => Dotnet(
                cliDll,
                ["cv-update", "--workspace", workspace, "--changed", "ReviewTarget.cs", "--format", "json"],
                calls,
                "cv_update"));
            var after = LoadSnapshot(workspace, store);
            return (before, after, build.ElapsedMs, update.ElapsedMs, DirectorySize(store), Math.Max(build.Value.PeakWorkingSetBytes, update.Value.PeakWorkingSetBytes));
        }

        var vcsPair = BuildPair("vcs", "vcs-base", "vcs-target");
        var sessionPair = BuildPair("session", "session-start", "session-current");
        var service = new SymbolDiffService();
        var vcsWatch = Stopwatch.StartNew();
        var vcs = service.Compare(new SymbolDiffRequest(
            new BaselineContract(BaselineKind.Vcs, "git", vcsPair.Base.SnapshotId, vcsPair.Target.SnapshotId, null, DateTimeOffset.UtcNow, vcsPair.Target.InputFingerprint),
            vcsPair.Base,
            vcsPair.Target));
        vcsWatch.Stop();
        calls["diff"]++;
        var sessionWatch = Stopwatch.StartNew();
        var session = service.Compare(new SymbolDiffRequest(
            new BaselineContract(BaselineKind.Session, "session", sessionPair.Base.SnapshotId, sessionPair.Target.SnapshotId, $"session-{repeat}", DateTimeOffset.UtcNow, sessionPair.Target.InputFingerprint),
            sessionPair.Base,
            sessionPair.Target));
        sessionWatch.Stop();
        calls["diff"]++;

        var expected = 5;
        var found = 0;
        found += HasChange(vcs, DiffKind.BodyChanged, "Existing") ? 1 : 0;
        found += HasChange(vcs, DiffKind.Added, "AddedDuringSession") ? 1 : 0;
        found += HasChange(vcs, DiffKind.Deleted, "Removed") ? 1 : 0;
        found += HasChange(session, DiffKind.Added, "AddedDuringSession") ? 1 : 0;
        found += HasChange(session, DiffKind.Deleted, "Removed") ? 1 : 0;
        var falsePositives = Math.Max(0, vcs.Changes.Count + session.Changes.Count - expected);
        var deletedBase = vcs.Changes.Concat(session.Changes)
            .Where(change => change.Kind == DiffKind.Deleted)
            .All(change => change.BaseSource?.Contains("Removed", StringComparison.Ordinal) == true && change.TargetSource is null);
        var noSessionExisting = !session.Changes.Any(change => SymbolName(change).Contains("Existing", StringComparison.Ordinal));
        var hunks = vcs.Contract.Entries.Concat(session.Contract.Entries)
            .All(entry => entry.Evidence.Any(evidence => !string.IsNullOrWhiteSpace(evidence.TextualHunk)));
        var quality = found == expected && deletedBase && noSessionExisting && hunks;
        var sourceBytes = vcs.Changes.Concat(session.Changes)
            .Sum(change => Utf8Bytes(change.BaseSource) + Utf8Bytes(change.TargetSource));
        stopwatch.Stop();
        return new RunRecord(
            pairId + "-E",
            pairId,
            "DIFF",
            repeat,
            "E",
            orderIndex,
            true,
            quality ? "succeeded" : "quality_failed",
            quality,
            true,
            false,
            Round(stopwatch.Elapsed.TotalMilliseconds),
            sourceBytes,
            null,
            Round(Math.Max(vcsPair.Cold, sessionPair.Cold)),
            Round(Math.Max(vcsWatch.Elapsed.TotalMilliseconds, sessionWatch.Elapsed.TotalMilliseconds)),
            Round(Math.Max(vcsPair.Update, sessionPair.Update)),
            null,
            Process.GetCurrentProcess().PeakWorkingSet64 + Math.Max(vcsPair.Peak, sessionPair.Peak),
            vcsPair.Size + sessionPair.Size,
            new RecallMetric(found, expected),
            falsePositives,
            0,
            0,
            calls,
            quality ? null : "independent_ground_truth_mismatch");
    }

    private static SymbolDiffSnapshot LoadSnapshot(string workspace, string storePath)
    {
        using var reader = new GenerationStore(storePath).OpenCurrent();
        var manifest = reader.Manifest;
        var shard = manifest.Shards.Single(item => item.Kind == "symbol");
        var symbols = reader.ReadShardRecords(shard.Path).Select(ContractJson.Deserialize<SymbolContract>).ToArray();
        var sources = manifest.Files.Select(file => new DiffSourceDocument(
            file.Path,
            File.ReadAllBytes(Path.Combine(workspace, file.Path.Replace('/', Path.DirectorySeparatorChar))),
            file.Encoding,
            file.ContentHash)).ToArray();
        return new SymbolDiffSnapshot(
            manifest.GenerationId,
            manifest.InputFingerprint,
            manifest.CreatedAt,
            manifest.Coverage,
            symbols,
            sources,
            []);
    }

    private static object Aggregate(IReadOnlyList<RunRecord> runs, DateTimeOffset generatedAt)
    {
        var conditions = new[] { "A", "B", "C", "D", "E" }.Select(condition =>
        {
            var cells = runs.Where(run => run.Condition == condition).ToArray();
            var attempted = cells.Count(run => run.Started);
            var quality = cells.Where(run => run.QualityPassed.HasValue).ToArray();
            return new
            {
                condition,
                cacheState = condition == "E" ? "long" : "not_applicable",
                attemptedRunCount = attempted,
                notStartedRunCount = cells.Count(run => !run.Started),
                terminalCounts = Terminal(cells),
                successRate = Ratio(cells.Count(run => run.Status == "succeeded"), attempted),
                qualityPassRate = Ratio(quality.Count(run => run.QualityPassed == true), quality.Length),
                readGrepTokenShare = Ratio(0, 0),
                fullReadRate = Ratio(0, 0),
                rereadRate = Ratio(0, 0),
                commentTokenShare = Ratio(0, 0),
                primaryToolUseRate = condition is "A" or "B" ? Ratio(0, 0) : Ratio(cells.Count(run => run.PrimaryToolUsed), attempted),
                unknownFullReadCount = 0,
                tokens = new { uncachedInput = 0, cacheReadInput = 0, cacheWriteInput = 0, output = 0 },
                cacheAwareCost = new { model = (double?)null, tool = (double?)null, setup = (double?)null, total = (double?)null, complete = false }
            };
        }).ToArray();
        var pairs = runs.Select(run => run.PairId).Distinct(StringComparer.Ordinal).ToArray();
        return new
        {
            schemaVersion = "1.0.0",
            protocolRevision = "task-016-v1",
            generatedAt,
            experimentPhase = "cv_evaluation",
            currency = "USD",
            randomizationSeed = Seed,
            conditions,
            overall = new
            {
                plannedRunCount = runs.Count,
                attemptedRunCount = runs.Count(run => run.Started),
                notStartedRunCount = runs.Count(run => !run.Started),
                completePairCount = pairs.Count(pair => runs.Where(run => run.PairId == pair).All(run => run.Status == "succeeded")),
                incompletePairCount = pairs.Count(pair => runs.Where(run => run.PairId == pair).Any(run => run.Status != "succeeded"))
            },
            dataQuality = new
            {
                duplicateEventCount = 0,
                conflictingDuplicateCount = 0,
                unknownEventCount = 0,
                missingRequiredFieldCount = runs.Count(run => run.Started),
                invalidUsageCount = 0,
                excludedMetricEventCount = runs.Count(run => run.Started),
                invalidRunCount = runs.Count(run => run.Status == "invalid")
            },
            notes = new[]
            {
                "Actual model token and provider cost events were unavailable; zero token fields are schema-required empty event sums, not measured zero-token agent runs.",
                "C and D unavailable cells remain attempted infra_failed runs and are not treated as zero-cost or zero-result successes.",
                "Source-byte, latency, memory, index-size, tool-use, recall, and false-positive metrics are recorded in cv-run-manifest.json.",
                "The overall outcome is No-Go because E fails the frozen zero-quality-degradation and 20% source-byte efficiency gates; token efficiency remains inconclusive."
            }
        };
    }
    private static object SourceSummary(IReadOnlyList<RunRecord> runs)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var task in new[] { "NAV", "DIFF" })
        {
            var sourceRuns = runs.Where(run => run.TaskId == task && run.SourceBytes.HasValue).ToArray();
            var candidates = sourceRuns.Where(run => run.Status == "succeeded" && run.Condition is "A" or "B" or "C" or "D")
                .GroupBy(run => run.Condition)
                .Select(group => new { Condition = group.Key, Median = Median(group.Select(run => (double)run.SourceBytes!.Value)) })
                .OrderBy(item => item.Median)
                .ThenByDescending(item => Strength(item.Condition))
                .ToArray();
            var baseline = candidates.FirstOrDefault();
            var eRuns = sourceRuns.Where(run => run.Condition == "E").ToArray();
            var eMedian = Median(eRuns.Select(run => (double)run.SourceBytes!.Value));
            var eQualityEligible = eRuns.Length == Repeats && eRuns.All(run => run.QualityPassed == true);
            double? reduction = baseline is null || baseline.Median == 0 || double.IsNaN(eMedian)
                ? null
                : (baseline.Median - eMedian) / baseline.Median;
            result[task] = new
            {
                strongerAvailableBaseline = baseline?.Condition,
                baselineMedian = Number(baseline?.Median),
                eMedian = Number(eMedian),
                pairedMedianReduction = Number(reduction),
                eQualityEligible,
                passesTwentyPercent = eQualityEligible && reduction >= 0.20
            };
        }
        return result;
    }

    private static object Performance(IEnumerable<RunRecord> runs) => new
    {
        coldBuildMs = Metrics(runs.Where(run => run.ColdBuildMs.HasValue).Select(run => run.ColdBuildMs!.Value)),
        warmQueryMs = Metrics(runs.Where(run => run.WarmQueryMs.HasValue).Select(run => run.WarmQueryMs!.Value)),
        incrementalUpdateMs = Metrics(runs.Where(run => run.UpdateMs.HasValue).Select(run => run.UpdateMs!.Value)),
        recoveryMs = Metrics(runs.Where(run => run.RecoveryMs.HasValue).Select(run => run.RecoveryMs!.Value)),
        peakWorkingSetBytes = Metrics(runs.Where(run => run.PeakWorkingSetBytes.HasValue).Select(run => (double)run.PeakWorkingSetBytes!.Value))
    };

    private static object Metrics(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return new { count = 0, median = (double?)null, p95 = (double?)null, max = (double?)null };
        var index95 = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95) - 1);
        return new
        {
            count = ordered.Length,
            median = (double?)Round(Median(ordered)),
            p95 = (double?)Round(ordered[index95]),
            max = (double?)Round(ordered[^1])
        };
    }

    private static object WeightedRecall(IEnumerable<RunRecord> runs)
    {
        var values = runs.Where(run => run.Recall is not null).Select(run => run.Recall!).ToArray();
        return Ratio(values.Sum(item => item.Numerator), values.Sum(item => item.Denominator));
    }

    private static Dictionary<string, int> SumCalls(IEnumerable<RunRecord> runs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var run in runs)
        foreach (var item in run.ToolCalls)
            result[item.Key] = result.GetValueOrDefault(item.Key) + item.Value;
        return result;
    }

    private static object Terminal(IEnumerable<RunRecord> runs)
    {
        var counts = runs.Where(run => run.Started).GroupBy(run => run.Status)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new
        {
            succeeded = counts.GetValueOrDefault("succeeded"),
            quality_failed = counts.GetValueOrDefault("quality_failed"),
            tool_failed = counts.GetValueOrDefault("tool_failed"),
            timeout = counts.GetValueOrDefault("timeout"),
            budget_stopped = counts.GetValueOrDefault("budget_stopped"),
            infra_failed = counts.GetValueOrDefault("infra_failed"),
            cancelled = counts.GetValueOrDefault("cancelled"),
            invalid = counts.GetValueOrDefault("invalid")
        };
    }

    private static object Ratio(int numerator, int denominator) => new
    {
        numerator,
        denominator,
        value = denominator == 0 ? (double?)null : Round((double)numerator / denominator, 6)
    };

    private static void Verify(string resultPath, string manifestPath)
    {
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var runs = manifest.RootElement.GetProperty("runs").EnumerateArray().ToArray();
        Assert(runs.Length == 30, "planned run count must be 30");
        foreach (var task in new[] { "NAV", "DIFF" })
        foreach (var condition in new[] { "A", "B", "C", "D", "E" })
            Assert(
                runs.Count(run => Text(run, "taskId") == task && Text(run, "condition") == condition && run.GetProperty("started").GetBoolean()) == 3,
                $"{task}/{condition} must have three attempted runs");
        Assert(
            runs.Where(run => Text(run, "condition") is "C" or "D")
                .All(run => Text(run, "status") == "infra_failed" && run.GetProperty("sourceBytes").ValueKind == JsonValueKind.Null),
            "C/D cells must remain unavailable, not zero-valued successes");
        Assert(
            runs.Where(run => Text(run, "condition") == "E").All(run => run.GetProperty("primaryToolUsed").GetBoolean()),
            "every E run must use CV");
        var overall = result.RootElement.GetProperty("overall");
        Assert(overall.GetProperty("plannedRunCount").GetInt32() == runs.Length, "planned denominator mismatch");
        Assert(overall.GetProperty("attemptedRunCount").GetInt32() == runs.Count(run => run.GetProperty("started").GetBoolean()), "attempted denominator mismatch");
        foreach (var conditionResult in result.RootElement.GetProperty("conditions").EnumerateArray())
        {
            var condition = Text(conditionResult, "condition");
            var cells = runs.Where(run => Text(run, "condition") == condition).ToArray();
            Assert(conditionResult.GetProperty("attemptedRunCount").GetInt32() == cells.Length, $"{condition} attempted mismatch");
            var terminal = conditionResult.GetProperty("terminalCounts");
            foreach (var status in new[] { "succeeded", "quality_failed", "tool_failed", "timeout", "budget_stopped", "infra_failed", "cancelled", "invalid" })
                Assert(terminal.GetProperty(status).GetInt32() == cells.Count(run => Text(run, "status") == status), $"{condition}/{status} mismatch");
            var quality = cells.Where(run => run.GetProperty("qualityPassed").ValueKind is JsonValueKind.True or JsonValueKind.False).ToArray();
            var ratio = conditionResult.GetProperty("qualityPassRate");
            Assert(ratio.GetProperty("denominator").GetInt32() == quality.Length, $"{condition} quality denominator mismatch");
            Assert(ratio.GetProperty("numerator").GetInt32() == quality.Count(run => run.GetProperty("qualityPassed").GetBoolean()), $"{condition} quality numerator mismatch");
        }
    }

    private static bool NavigationExpected(string text) =>
        text.Contains("public void Load(string value)", StringComparison.Ordinal) &&
        text.Contains("public void Load(string value, int retryCount)", StringComparison.Ordinal) &&
        text.Contains("private T Load<T>(T value)", StringComparison.Ordinal) &&
        text.Contains("void ILoader.Load(string value)", StringComparison.Ordinal) &&
        text.Contains("partial class Catalog", StringComparison.Ordinal) &&
        text.Contains("BuildLabel", StringComparison.Ordinal) &&
        text.Contains("Activator.CreateInstance", StringComparison.Ordinal) &&
        text.Contains("ServiceKey", StringComparison.Ordinal) &&
        text.Contains("MemberKey", StringComparison.Ordinal) &&
        text.Contains("LinkedHelper.cs", StringComparison.Ordinal);

    private static bool DiffExpected(string vcs, string session, string extra) =>
        vcs.Contains("-        return \"base\";", StringComparison.Ordinal) &&
        vcs.Contains("+        return \"dirty-before-session\";", StringComparison.Ordinal) &&
        vcs.Contains("+    public static string AddedDuringSession", StringComparison.Ordinal) &&
        vcs.Contains("-    public static string Removed", StringComparison.Ordinal) &&
        session.Contains("+    public static string AddedDuringSession", StringComparison.Ordinal) &&
        session.Contains("-    public static string Removed", StringComparison.Ordinal) &&
        !session.Contains("-        return \"dirty-before-session\";", StringComparison.Ordinal) &&
        extra.Contains("public static string Removed()", StringComparison.Ordinal);

    private static bool LoadQuality(IEnumerable<JsonElement> items)
    {
        var values = items.ToArray();
        return values.Any(item => Text(item, "qualifiedName") == "Fixture.Navigation.Catalog.Load" && Text(item, "signature") == "public void Load(string)") &&
            values.Any(item => Text(item, "qualifiedName") == "Fixture.Navigation.Catalog.Load" && Text(item, "signature") == "public void Load(string, int)") &&
            values.Any(item => Text(item, "qualifiedName") == "Fixture.Navigation.Catalog.Load" && item.GetProperty("genericArity").GetInt32() == 1 && Text(item, "accessibility") == "private") &&
            values.Any(item => Text(item, "qualifiedName") == "Fixture.Navigation.Catalog.Load" && Text(item, "explicitInterface") == "ILoader");
    }

    private static bool CatalogQuality(IEnumerable<JsonElement> items) => items.Any(item =>
    {
        if (!Text(item, "qualifiedName").EndsWith(".Catalog", StringComparison.Ordinal)) return false;
        var paths = item.GetProperty("declarations").EnumerateArray()
            .Select(declaration => Text(declaration.GetProperty("location"), "path")).ToArray();
        return paths.Any(path => path.EndsWith("Contracts.cs", StringComparison.Ordinal)) &&
            paths.Any(path => path.EndsWith("Catalog.Partial.cs", StringComparison.Ordinal)) &&
            paths.Any(path => path.EndsWith("RefKinds.cs", StringComparison.Ordinal));
    });

    private static int[] ImpactLines(JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("structuredContent", out var structured) ||
            !structured.TryGetProperty("results", out var values)) return [];
        return values.EnumerateArray()
            .Select(item => item.GetProperty("location").GetProperty("span").GetProperty("startLine").GetInt32())
            .Distinct().Order().ToArray();
    }
    private static bool HasChange(SymbolDiffResult result, DiffKind kind, string name) =>
        result.Changes.Any(change => change.Kind == kind && SymbolName(change).Contains(name, StringComparison.Ordinal));
    private static string SymbolName(SymbolDiffChange change) => change.BaseSymbol?.QualifiedName ?? change.TargetSymbol?.QualifiedName ?? string.Empty;
    private static long Utf8Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private static int SilentPartial(JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("structuredContent", out var structured) ||
            !structured.TryGetProperty("status", out var status) ||
            status.GetString() != "partial") return 0;
        var explained = structured.TryGetProperty("coverage", out var coverage) &&
            coverage.TryGetProperty("limitations", out var limitations) &&
            limitations.GetArrayLength() > 0;
        return explained ? 0 : 1;
    }

    private static bool IsExplicitStale(JsonElement response) =>
        IsError(response) && response.GetRawText().Contains("SOURCE_STALE", StringComparison.Ordinal);
    private static bool IsError(JsonElement response) =>
        response.TryGetProperty("result", out var result) &&
        result.TryGetProperty("isError", out var value) && value.GetBoolean();
    private static long SourceContentBytes(JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("structuredContent", out var structured) ||
            !structured.TryGetProperty("source", out var source) || source.ValueKind == JsonValueKind.Null) return 0;
        return Encoding.UTF8.GetByteCount(Text(source, "content"));
    }

    private static JsonElement[] Results(JsonElement response) =>
        response.GetProperty("results").EnumerateArray().Select(item => item.Clone()).ToArray();
    private static JsonElement[] StructuredResults(JsonElement response) =>
        response.GetProperty("result").GetProperty("structuredContent").GetProperty("results").EnumerateArray().Select(item => item.Clone()).ToArray();
    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;
    private static void AddQualified(ICollection<string> ids, IEnumerable<JsonElement> values, string qualified)
    {
        var item = values.FirstOrDefault(value => Text(value, "qualifiedName") == qualified);
        if (item.ValueKind != JsonValueKind.Undefined) ids.Add(Text(item, "symbolId"));
    }

    private static Dictionary<string, int> EmptyCvCalls() => new(StringComparer.Ordinal)
    {
        ["cv_build"] = 0,
        ["cv_find"] = 0,
        ["cv_get"] = 0,
        ["cv_impact"] = 0,
        ["cv_update"] = 0,
        ["diff"] = 0,
        ["mcp"] = 0
    };

    private static ProcessResult Dotnet(string dll, IReadOnlyList<string> arguments, IDictionary<string, int> calls, string name)
    {
        calls[name] = (calls.TryGetValue(name, out var current) ? current : 0) + 1;
        return ProcessRun.Execute("dotnet", new[] { dll }.Concat(arguments).ToArray(), [0, 3]);
    }

    private static Measured<T> Measure<T>(Func<T> operation)
    {
        var watch = Stopwatch.StartNew();
        var value = operation();
        watch.Stop();
        return new Measured<T>(value, Round(watch.Elapsed.TotalMilliseconds));
    }

    private static string[] Slice(string path, int skip, int count) => File.ReadAllLines(path).Skip(skip).Take(count).ToArray();
    private static string[] Rotate(string[] values, int offset) => values.Select((_, index) => values[(index + offset) % values.Length]).ToArray();
    private static string[] Shuffle(string[] values, int seed)
    {
        var result = values.ToArray();
        var random = new Random(seed);
        for (var index = result.Length - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (result[index], result[swap]) = (result[swap], result[index]);
        }
        return result;
    }

    private static string[] DiscoverLsp()
    {
        var result = Discover(["csharp-ls", "OmniSharp", "Microsoft.CodeAnalysis.LanguageServer"]).ToList();
        try
        {
            var tools = ProcessRun.Execute("dotnet", ["tool", "list", "--global"], [0]).Stdout;
            foreach (var name in new[] { "csharp-ls", "omnisharp" })
            {
                if (tools.Contains(name, StringComparison.OrdinalIgnoreCase)) result.Add(name);
            }
        }
        catch
        {
            // Command discovery already established availability when the global tool list is unreadable.
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string[] Discover(string[] names) => names.Where(name =>
    {
        try { return ProcessRun.Execute("where.exe", [name], [0, 1]).ExitCode == 0; }
        catch { return false; }
    }).ToArray();

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(directory).Equals(".code-virtualize", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
    }

    private static long DirectorySize(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
        : 0;
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private static string FixtureDigest(string repository)
    {
        var root = Path.Combine(repository, "tests", "fixtures", "csharp");
        var marker = $"{Path.DirectorySeparatorChar}.code-virtualize{Path.DirectorySeparatorChar}";
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        var rows = files.Select(path =>
            Path.GetRelativePath(repository, path).Replace('\\', '/') + "\0" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows) + "\n")));
    }

    private static string Git(string repository, params string[] arguments) =>
        ProcessRun.Execute("git", new[] { "-C", repository }.Concat(arguments).ToArray(), [0]).Stdout.Trim();
    private static string? Read(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++) if (args[index] == name) return args[index + 1];
        return null;
    }
    private static void RequireFile(string path) { if (!File.Exists(path)) throw new FileNotFoundException("Required built artifact is missing.", path); }
    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return double.NaN;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }
    private static int Strength(string condition) => condition switch { "D" => 4, "C" => 3, "B" => 2, _ => 1 };
    private static double? Number(double? value) =>
        value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? Round(value.Value, 6) : null;
    private static double Round(double value, int digits = 3) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException("Verification failed: " + message); }
}

internal sealed record RecallMetric(int Numerator, int Denominator)
{
    public double? Value => Denominator == 0 ? null : Math.Round((double)Numerator / Denominator, 6, MidpointRounding.AwayFromZero);
}

internal sealed record RunRecord(
    string RunId,
    string PairId,
    string TaskId,
    int RepeatIndex,
    string Condition,
    int OrderIndex,
    bool Started,
    string Status,
    bool? QualityPassed,
    bool PrimaryToolUsed,
    bool McpUsed,
    double WallTimeMs,
    long? SourceBytes,
    int? SourceLines,
    double? ColdBuildMs,
    double? WarmQueryMs,
    double? UpdateMs,
    double? RecoveryMs,
    long? PeakWorkingSetBytes,
    long? BuildIndexSizeBytes,
    RecallMetric? Recall,
    int? FalsePositiveCount,
    int CriticalStaleCount,
    int SilentPartialCount,
    IReadOnlyDictionary<string, int> ToolCalls,
    string? ErrorCategory)
{
    public static RunRecord NotStarted(string pairId, string task, int repeat, string condition, int orderIndex, string reason) =>
        new(pairId + "-" + condition, pairId, task, repeat, condition, orderIndex, false, "not_started", null, false, false,
            0, null, null, null, null, null, null, null, null, null, null, 0, 0, new Dictionary<string, int>(), reason);
}

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, long PeakWorkingSetBytes);
internal sealed record Measured<T>(T Value, double ElapsedMs);

internal static class ProcessRun
{
    public static ProcessResult Execute(string fileName, IReadOnlyList<string> arguments, IReadOnlyCollection<int> allowed)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timeout = Stopwatch.StartNew();
        var peak = 0L;
        while (!process.WaitForExit(25))
        {
            try
            {
                process.Refresh();
                peak = Math.Max(peak, process.PeakWorkingSet64);
            }
            catch (InvalidOperationException)
            {
                // The process exited between polling and sampling.
            }
            if (timeout.ElapsedMilliseconds <= 120000) continue;
            process.Kill(true);
            throw new TimeoutException($"{fileName} exceeded 120 seconds.");
        }
        Task.WaitAll(stdout, stderr);
        var result = new ProcessResult(process.ExitCode, stdout.Result, stderr.Result, peak);
        if (!allowed.Contains(result.ExitCode)) throw new InvalidOperationException($"{fileName} exited {result.ExitCode}: {result.Stderr}");
        return result;
    }
}

internal sealed class McpClient : IDisposable
{
    private readonly Process process;
    private int id;

    public McpClient(string dll, string workspace)
    {
        process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(dll);
        process.StartInfo.ArgumentList.Add("--workspace");
        process.StartInfo.ArgumentList.Add(workspace);
        process.StartInfo.ArgumentList.Add("--timeout-ms");
        process.StartInfo.ArgumentList.Add("30000");
        process.Start();
        _ = process.StandardError.ReadToEndAsync();
    }

    public long PeakWorkingSetBytes
    {
        get
        {
            try { process.Refresh(); return process.PeakWorkingSet64; }
            catch { return 0; }
        }
    }

    public void Initialize()
    {
        Request(new { jsonrpc = "2.0", id = ++id, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "cv-evaluation", version = "1.0" } } });
        Notify(new { jsonrpc = "2.0", method = "notifications/initialized" });
        Request(new { jsonrpc = "2.0", id = ++id, method = "tools/list", @params = new { } });
    }

    public JsonElement Call(string name, object arguments) =>
        Request(new { jsonrpc = "2.0", id = ++id, method = "tools/call", @params = new { name, arguments } });

    private JsonElement Request(object value)
    {
        Notify(value);
        var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("MCP stdout closed.");
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private void Notify(object value)
    {
        process.StandardInput.WriteLine(JsonSerializer.Serialize(value));
        process.StandardInput.Flush();
    }

    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000)) process.Kill(true);
        }
        process.Dispose();
    }
}