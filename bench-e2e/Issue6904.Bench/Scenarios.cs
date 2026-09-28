using System.Diagnostics;
using TUnit.Core.Interfaces;

namespace Issue6904.Bench;

// 1. Reproduction from https://github.com/thomhurst/TUnit/issues/6904 (verbatim test).
public sealed class SlowStartFixture : IAsyncInitializer
{
    public TimeSpan SynchronousPartTook { get; private set; }

    public async Task InitializeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        // Sync-over-async (simulated here, see Testcontainers for real-world example)
        Task.Run(async () => await Task.Delay(10)).GetAwaiter().GetResult();
        SynchronousPartTook = stopwatch.Elapsed;
        await Task.Delay(10);
    }
}

public class SharedFixtureTests
{
    [ClassDataSource<SlowStartFixture>(Shared = SharedType.PerTestSession)]
    public required SlowStartFixture Fixture { get; init; }

    public static IEnumerable<int> Cases() => Enumerable.Range(0, Environment.ProcessorCount * 4);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task SynchronousPartOfInitializeAsyncIsNotStarved(int _)
    {
        await Assert.That(Fixture.SynchronousPartTook).IsLessThan(TimeSpan.FromMilliseconds(250));
    }
}

// 2. Per-test fixture whose InitializeAsync completes synchronously (uncontended, most common).
public sealed class SyncFixture : IAsyncInitializer
{
    public Task InitializeAsync() => Task.CompletedTask;
}

public class PerTestSyncFixtureTests
{
    [ClassDataSource<SyncFixture>(Shared = SharedType.None)]
    public required SyncFixture Fixture { get; init; }

    public static IEnumerable<int> Cases() => Enumerable.Range(0, 10_000);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public void Run(int _) { }
}

// 3. Per-test fixture whose InitializeAsync really awaits (uncontended) - where a thread-pool
//    hop per initialization (Task.Run, or waiting on a RunContinuationsAsynchronously source) would show.
public sealed class AsyncFixture : IAsyncInitializer
{
    public async Task InitializeAsync() => await Task.Yield();
}

public class PerTestAsyncFixtureTests
{
    [ClassDataSource<AsyncFixture>(Shared = SharedType.None)]
    public required AsyncFixture Fixture { get; init; }

    public static IEnumerable<int> Cases() => Enumerable.Range(0, 10_000);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public void Run(int _) { }
}

// 4. One shared fixture, many tests waiting on it concurrently while it initializes asynchronously.
public sealed class SharedSlowFixture : IAsyncInitializer
{
    public async Task InitializeAsync() => await Task.Delay(200);
}

public class SharedContendedFixtureTests
{
    [ClassDataSource<SharedSlowFixture>(Shared = SharedType.PerTestSession)]
    public required SharedSlowFixture Fixture { get; init; }

    public static IEnumerable<int> Cases() => Enumerable.Range(0, 10_000);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public void Run(int _) { }
}
