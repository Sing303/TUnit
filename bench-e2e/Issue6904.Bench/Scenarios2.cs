using System.Collections.Concurrent;
using System.Diagnostics;
using TUnit.Core.Interfaces;

namespace Issue6904.Bench;

internal static class ThreadLog
{
    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> Threads = new();

    public static void Record(string scenario) =>
        Threads.GetOrAdd(scenario, _ => new()).TryAdd(Environment.CurrentManagedThreadId, 0);

    public static void Burn(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration) { }
    }

    [After(TestSession)]
    public static void Report()
    {
        var path = Environment.GetEnvironmentVariable("ISSUE6904_THREADS_FILE");
        if (path is null) return;
        foreach (var (scenario, threads) in Threads)
        {
            File.AppendAllText(path, $"{scenario} threads={threads.Count}\n");
        }
    }
}

// 5. Shared fixture that blocks (plain, no pool threads needed) and then completes SYNCHRONOUSLY.
public sealed class BlockingSyncCompletingFixture : IAsyncInitializer
{
    public Task InitializeAsync()
    {
        Thread.Sleep(300);
        return Task.CompletedTask;
    }
}

public class SharedBlockingSyncCompletingTests
{
    [ClassDataSource<BlockingSyncCompletingFixture>(Shared = SharedType.PerTestSession)]
    public required BlockingSyncCompletingFixture Fixture { get; init; }

    public static IEnumerable<int> Cases() => Enumerable.Range(0, 1_000);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public void Run(int _)
    {
        ThreadLog.Record(nameof(SharedBlockingSyncCompletingTests));
        ThreadLog.Burn(TimeSpan.FromMilliseconds(2));
    }
}

// 6. Shared fixture doing sync-over-async (like Testcontainers' probe) and then completing SYNCHRONOUSLY.
public sealed class SyncOverAsyncSyncCompletingFixture : IAsyncInitializer
{
    public Task InitializeAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            Task.Run(async () => await Task.Delay(10)).GetAwaiter().GetResult();
        }

        return Task.CompletedTask;
    }
}

public class SharedSyncOverAsyncSyncCompletingTests
{
    [ClassDataSource<SyncOverAsyncSyncCompletingFixture>(Shared = SharedType.PerTestSession)]
    public required SyncOverAsyncSyncCompletingFixture Fixture { get; init; }

    public static IEnumerable<int> Cases() => Enumerable.Range(0, 1_000);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public void Run(int _)
    {
        ThreadLog.Record(nameof(SharedSyncOverAsyncSyncCompletingTests));
        ThreadLog.Burn(TimeSpan.FromMilliseconds(2));
    }
}

// 7. Same bodies, shared fixture that completes ASYNCHRONOUSLY (main already resumes waiters inline here).
public sealed class AsyncCompletingFixture : IAsyncInitializer
{
    public async Task InitializeAsync() => await Task.Delay(300);
}

public class SharedAsyncCompletingTests
{
    [ClassDataSource<AsyncCompletingFixture>(Shared = SharedType.PerTestSession)]
    public required AsyncCompletingFixture Fixture { get; init; }

    public static IEnumerable<int> Cases() => Enumerable.Range(0, 1_000);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public void Run(int _)
    {
        ThreadLog.Record(nameof(SharedAsyncCompletingTests));
        ThreadLog.Burn(TimeSpan.FromMilliseconds(2));
    }
}
