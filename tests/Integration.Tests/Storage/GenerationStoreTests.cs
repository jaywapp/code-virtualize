using System.Diagnostics;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Integration.Tests.Storage;

internal static class GenerationStoreTests
{
    private static readonly string HashA = $"sha256:{new string('a', 64)}";
    private static readonly string HashB = $"sha256:{new string('b', 64)}";

    public static void Run()
    {
        PublishAndReadRoundTrips();
        SecondPublishAtomicallyReplacesPointer();
        CorruptShardIsRejected();
        UnsupportedShardVersionIsRejected();
        GenerationCannotBeOverwritten();
        TruncatedShardDoesNotReplaceCurrent();
        ManifestWriteCrashDoesNotReplaceCurrent();
        GenerationCommitCrashDoesNotReplaceCurrent();
        PointerWriteCrashDoesNotReplaceCurrent();
        PointerReplaceCrashDoesNotReplaceCurrent();
        TransientPointerHolderDoesNotFailPublish();
        PersistentPointerHolderKeepsCurrent();
        ConcurrentReadersNeverSeeMissingPointer();
        RepeatedPublishReplacesPointerEveryTime();
        DiskFullDoesNotReplaceCurrent();
        WriterContentionIsExplicit();
        BoundedWriterWaitAcquiresAfterRelease();
        ReaderPinPreventsDeletionOnWindows();
        TraversalIsRejected();
        ReparsePointIsRejected();
    }

    private static void PublishAndReadRoundTrips()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        var expectedRecord = SymbolRecord("analysis-001");
        var published = store.Publish(Request("gen-001", "analysis-001"));
        Assert(published.Shards.Count == 1, "Publish must add verified shard metadata to the manifest.");
        Assert(published.Shards[0].ByteLength > 0 && published.Shards[0].RecordCount == 1, "Shard byte length and record count must be recorded.");
        AssertSha256(published.Shards[0].ContentHash);

        using var reader = store.OpenCurrent();
        Assert(reader.Manifest.GenerationId == "gen-001", "The published generation must become current.");
        var records = reader.ReadShardRecords("symbol.cv");
        Assert(records.Count == 1 && records[0] == expectedRecord, "UTF-8 JSONL records must round-trip.");
    }

    private static void SecondPublishAtomicallyReplacesPointer()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        store.Publish(Request("gen-001", "analysis-001"));
        store.Publish(Request("gen-002", "analysis-002"));
        AssertCurrent(root.StorePath, "gen-002");
        Assert(Directory.Exists(Path.Combine(root.StorePath, "generations", "gen-001")), "Publishing a new generation must not delete the previous generation.");
    }

    private static void CorruptShardIsRejected()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        store.Publish(Request("gen-001", "analysis-001"));
        var shardPath = Path.Combine(root.StorePath, "generations", "gen-001", "symbol.cv");
        var bytes = File.ReadAllBytes(shardPath);
        bytes[^2] ^= 1;
        File.WriteAllBytes(shardPath, bytes);
        ThrowsStorage(() => { using var reader = store.OpenCurrent(); }, StorageErrorCodes.CorruptGeneration);
    }

    private static void UnsupportedShardVersionIsRejected()
    {
        using var root = new TemporaryRoot();
        var record = SymbolRecord("analysis-001").Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
        var request = new GenerationPublishRequest(
            Manifest("gen-001", "analysis-001"),
            [new GenerationShardWriteRequest("symbol", "symbol.cv", ContractSchemas.Symbol, [record])]);
        ThrowsStorage(() => new GenerationStore(root.StorePath).Publish(request), StorageErrorCodes.SchemaUnsupported);
    }

    private static void GenerationCannotBeOverwritten()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        store.Publish(Request("gen-001", "analysis-001"));
        ThrowsStorage(() => store.Publish(Request("gen-001", "analysis-changed")), StorageErrorCodes.GenerationExists);
        AssertCurrent(root.StorePath, "gen-001");
    }

    private static void TruncatedShardDoesNotReplaceCurrent()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        var injector = new ActionFaultInjector(StorageFaultPoint.AfterShardWrite, path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(stream.Length - 1);
        });
        var store = new GenerationStore(root.StorePath, injector);
        ThrowsStorage(() => store.Publish(Request("gen-next", "analysis-next")), StorageErrorCodes.CorruptGeneration);
        AssertCurrent(root.StorePath, "gen-base");
    }

    private static void ManifestWriteCrashDoesNotReplaceCurrent()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        AssertFaultKeepsCurrent(root.StorePath, StorageFaultPoint.AfterManifestWrite, new IOException("injected manifest crash"));
    }

    private static void GenerationCommitCrashDoesNotReplaceCurrent()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        AssertFaultKeepsCurrent(root.StorePath, StorageFaultPoint.AfterGenerationCommit, new IOException("injected process crash"));
        Assert(Directory.Exists(Path.Combine(root.StorePath, "generations", "gen-next")), "A committed but unpublished generation may remain as an immutable orphan.");
    }

    private static void PointerWriteCrashDoesNotReplaceCurrent()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        AssertFaultKeepsCurrent(root.StorePath, StorageFaultPoint.DuringPointerWrite, new IOException("injected partial pointer write"));
    }

    private static void PointerReplaceCrashDoesNotReplaceCurrent()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        AssertFaultKeepsCurrent(root.StorePath, StorageFaultPoint.BeforePointerReplace, new IOException("injected pointer publish crash"));
    }

    private static void TransientPointerHolderDoesNotFailPublish()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        var pointerPath = Path.Combine(root.StorePath, "current.json");
        Task? release = null;
        var injector = new ActionFaultInjector(StorageFaultPoint.BeforePointerReplace, _ =>
        {
            // Mimics an on-access scanner: opens without delete sharing, then lets go shortly after.
            var holder = new FileStream(pointerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            release = Task.Run(() =>
            {
                Thread.Sleep(30);
                holder.Dispose();
            });
        });
        new GenerationStore(root.StorePath, injector).Publish(Request("gen-next", "analysis-next"));
        release?.GetAwaiter().GetResult();
        AssertCurrent(root.StorePath, "gen-next");
        AssertNoTemporaryPointers(root.StorePath);
    }

    private static void PersistentPointerHolderKeepsCurrent()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        var pointerPath = Path.Combine(root.StorePath, "current.json");
        var store = new GenerationStore(root.StorePath);
        using (new FileStream(pointerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (OperatingSystem.IsWindows())
            {
                ThrowsStorage(() => store.Publish(Request("gen-next", "analysis-next")), StorageErrorCodes.IoError);
            }
            else
            {
                store.Publish(Request("gen-next", "analysis-next"));
            }
        }

        AssertCurrent(root.StorePath, OperatingSystem.IsWindows() ? "gen-base" : "gen-next");
        AssertNoTemporaryPointers(root.StorePath);
    }

    private static void ConcurrentReadersNeverSeeMissingPointer()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        var published = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        published["gen-base"] = 0;
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                // Any exception, including NO_CURRENT_GENERATION or a missing pointer file, fails the test:
                // the pointer name must never be absent while it is replaced.
                using (var reader = new GenerationStore(root.StorePath).OpenCurrent())
                {
                    Assert(published.ContainsKey(reader.Manifest.GenerationId), "A reader must only observe a completely published generation.");
                }

                reads++;
                Thread.Sleep(1);
            }

            return reads;
        })).ToArray();

        var store = new GenerationStore(root.StorePath);
        try
        {
            for (var index = 0; index < 150; index++)
            {
                var generationId = $"gen-{index:D3}";
                published[generationId] = 0;
                store.Publish(Request(generationId, $"analysis-{index:D3}"));
            }
        }
        finally
        {
            stop.Cancel();
        }

        var totalReads = readers.Sum(task => task.GetAwaiter().GetResult());
        Assert(totalReads > 0, "Concurrent readers must have observed the store while publishing.");
        AssertCurrent(root.StorePath, "gen-149");
        AssertNoTemporaryPointers(root.StorePath);
    }

    private static void RepeatedPublishReplacesPointerEveryTime()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        for (var index = 0; index < 200; index++)
        {
            var generationId = $"gen-{index:D3}";
            store.Publish(Request(generationId, $"analysis-{index:D3}"));
            AssertCurrent(root.StorePath, generationId);
        }

        AssertNoTemporaryPointers(root.StorePath);
    }

    private static void AssertNoTemporaryPointers(string storePath)
    {
        Assert(Directory.GetFiles(storePath, ".current-*.tmp").Length == 0, "Pointer publication must not leave temporary pointer files.");
    }

    private static void DiskFullDoesNotReplaceCurrent()
    {
        using var root = new TemporaryRoot();
        PublishBase(root.StorePath);
        AssertFaultKeepsCurrent(root.StorePath, StorageFaultPoint.DuringPointerWrite, new IOException("injected disk full"));
    }

    private static void WriterContentionIsExplicit()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        using var competingLease = new FileStream(Path.Combine(root.StorePath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ThrowsStorage(() => store.Publish(Request("gen-001", "analysis-001")), StorageErrorCodes.Busy);
    }

    private static void BoundedWriterWaitAcquiresAfterRelease()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        var competingLease = new FileStream(
            Path.Combine(root.StorePath, "writer.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var publish = Task.Run(() => store.Publish(
            Request("gen-wait", "analysis-wait"),
            TimeSpan.FromSeconds(2)));
        Thread.Sleep(100);
        competingLease.Dispose();
        var result = publish.GetAwaiter().GetResult();
        Assert(result.GenerationId == "gen-wait",
            "A bounded writer wait must acquire the lease after the competing writer releases it.");
    }

    private static void ReaderPinPreventsDeletionOnWindows()
    {
        using var root = new TemporaryRoot();
        var store = new GenerationStore(root.StorePath);
        store.Publish(Request("gen-001", "analysis-001"));
        using var reader = store.OpenCurrent();
        var pinPath = Path.Combine(root.StorePath, "generations", "gen-001", ".reader.pin");
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Delete(pinPath);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            throw new InvalidOperationException("An active Windows reader pin must prevent pin deletion.");
        }
    }

    private static void TraversalIsRejected()
    {
        using var root = new TemporaryRoot();
        var outsidePath = Path.Combine(root.RootPath, "escaped.cv");
        var request = new GenerationPublishRequest(
            Manifest("gen-001", "analysis-001"),
            [new GenerationShardWriteRequest("symbol", "..\\..\\escaped.cv", ContractSchemas.Symbol, [SymbolRecord("analysis-001")])]);
        ThrowsStorage(() => new GenerationStore(root.StorePath).Publish(request), StorageErrorCodes.PathOutsideStore);
        Assert(!File.Exists(outsidePath), "Traversal rejection must happen before any outside file is written.");
    }

    private static void ReparsePointIsRejected()
    {
        using var root = new TemporaryRoot(createStore: false);
        Directory.CreateDirectory(root.StorePath);
        var target = Path.Combine(root.RootPath, "junction-target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(root.StorePath, "generations");

        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(link);
            start.ArgumentList.Add(target);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start junction fixture process.");
            process.WaitForExit();
            Assert(process.ExitCode == 0, "The Windows junction fixture must be created.");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        try
        {
            ThrowsStorage(() => new GenerationStore(root.StorePath), StorageErrorCodes.PathOutsideStore);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    private static void AssertFaultKeepsCurrent(string storePath, StorageFaultPoint point, Exception failure)
    {
        var injector = new ActionFaultInjector(point, _ => throw failure);
        var store = new GenerationStore(storePath, injector);
        ThrowsStorage(() => store.Publish(Request("gen-next", "analysis-next")), StorageErrorCodes.IoError);
        AssertCurrent(storePath, "gen-base");
    }

    private static void PublishBase(string storePath)
    {
        new GenerationStore(storePath).Publish(Request("gen-base", "analysis-base"));
    }

    private static void AssertCurrent(string storePath, string generationId)
    {
        using var reader = new GenerationStore(storePath).OpenCurrent();
        Assert(reader.Manifest.GenerationId == generationId, "A failed publish must preserve the previous current generation.");
    }

    private static GenerationPublishRequest Request(string generationId, string analysisKey) =>
        new(Manifest(generationId, analysisKey),
            [new GenerationShardWriteRequest("symbol", "symbol.cv", ContractSchemas.Symbol, [SymbolRecord(analysisKey)])]);

    private static ManifestContract Manifest(string generationId, string analysisKey) =>
        new(
            generationId,
            "workspace-001",
            analysisKey,
            DateTimeOffset.Parse("2026-09-20T00:00:00Z"),
            "adapter-1",
            HashA,
            GenerationState.Valid,
            [],
            [],
            [],
            new CoverageContract("static-csharp-selected-configuration", CoverageLevel.CompleteWithinScope, 1, 0, 0, 0, [], ["dynamic-references-not-covered"], false));

    private static string SymbolRecord(string analysisKey)
    {
        var identity = new SymbolIdentityContract("project-001", analysisKey, "method", "Example.Catalog.Load", 0, [], null);
        var symbolId = DeterministicSymbolId.Create(identity);
        var declaration = new DeclarationContract(
            symbolId,
            new LocationContract("file-001", HashB, new TextSpanContract(0, 4, 1, 1), "Catalog.cs", null),
            DocumentKind.Source);
        return ContractJson.Serialize(new SymbolContract(
            symbolId,
            "project-001",
            analysisKey,
            "method",
            "Load",
            "Example.Catalog.Load",
            "void Load()",
            "public",
            null,
            0,
            IdentityQuality.Semantic,
            [],
            null,
            [declaration],
            []));
    }

    private static void ThrowsStorage(Action action, string errorCode)
    {
        try
        {
            action();
        }
        catch (StorageException exception) when (exception.ErrorCode == errorCode)
        {
            return;
        }

        throw new InvalidOperationException($"Expected StorageException '{errorCode}'.");
    }

    private static void AssertSha256(string value)
    {
        Assert(value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal), "Digest must use the SHA-256 contract format.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ActionFaultInjector(StorageFaultPoint point, Action<string> action) : IStorageFaultInjector
    {
        public void OnFaultPoint(StorageFaultPoint current, string path)
        {
            if (current == point)
            {
                action(path);
            }
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot(bool createStore = true)
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"code-virtualize-storage-{Guid.NewGuid():N}");
            StorePath = Path.Combine(RootPath, "store");
            Directory.CreateDirectory(RootPath);
            if (createStore)
            {
                Directory.CreateDirectory(StorePath);
            }
        }

        public string RootPath { get; }

        public string StorePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}