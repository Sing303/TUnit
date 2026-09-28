## Description

`ObjectInitializer` deduplicated `IAsyncInitializer.InitializeAsync()` with `Lazy<Task>` in `ExecutionAndPublication` mode. `Lazy` runs its factory, which here is `InitializeAsync()` itself, under a lock, so the lock covered everything the initializer does before its first suspending `await`. Every other test waiting on the same shared object meanwhile blocked a thread-pool thread in `Monitor.Enter`. If that synchronous part needs pool threads itself (sync-over-async, e.g. Testcontainers' Docker probe in the `TestcontainersSettings` static initializer), the pool starves and each step takes about a second.

This PR publishes the initialization task before any user code runs:

- The caller that wins `GetOrAdd` starts a helper that runs `InitializeAsync()` inline up to its first `await`, as before, but with no lock held. The helper completes a plain `TaskCompletionSource<bool>`.
- Every caller, including that one, waits on the published task the same way, so none of them blocks a thread.
- Unchanged:
  - `InitializeAsync()` runs exactly once per object.
  - Failures stay cached (#4715), and every caller gets the original exception object, including an `OperationCanceledException` thrown by the initializer.
  - A caller's token only stops that caller waiting. The initializing caller's cancellation can't poison the result, because the helper doesn't depend on its token.
- `InitializeCoreAsync` stays `async`. An `OperationCanceledException` thrown by `InitializeAsync` therefore still completes callers' tasks as Canceled, as on `main`. Returning the `WaitAsync` task directly would make them Faulted, which changes what `Task.WhenAll` surfaces in `StaticPropertyHandler` (it uses `.AsTask()`).

### Overhead

Measured with BenchmarkDotNet 0.15.8 (.NET 10.0.12, Linux, 4 cores) on verbatim copies of `main`'s and this PR's `InitializeCoreAsync`. Each operation is the first initialization of a fresh (per-test) fixture, with a cancellable token as TUnit passes:

| First initialization | `main` | This PR |
|---|---|---|
| `InitializeAsync` completes synchronously, 1 / 16 threads | 260 / 509 ns, 208 B | 277 / 514 ns, **168 B** |
| `InitializeAsync` awaits (`Task.Yield`), 1 / 16 threads | 2,873 / 1,292 ns, 527 B | 3,082 / 1,355 ns (±440 / ±150 ns StdDev), 604 B |
| Already initialized (every later test using a shared object) | 22.0 ns, 0 B | 22.7 ns, 0 B |

Thread-pool work items per initialization are the same as `main`: 0 for a synchronously completing initializer, 1 for one that awaits.

End to end, the TUnit-reported test-run duration on real TUnit builds is the same as `main` for 10,000 tests with a per-test fixture, whether the fixture completes synchronously or awaits. This PR was faster than `main` in 6/15 and 8/15 alternating pairs, and the median paired differences were +37 ms and −23 ms against about 1,950 ms runs.

### Shared fixtures

| Shared fixture (1,000 tests with 2 ms synchronous bodies; TUnit-reported duration, median of 5) | `main` | This PR |
|---|---|---|
| Repro from #6904 (16 tests) | **16/16 failed**, 1,683 ms | **0/16 failed**, 630 ms |
| `InitializeAsync` does sync-over-async, then completes synchronously | 5,615 ms | 2,727 ms |
| `InitializeAsync` awaits 300 ms | 3,015 ms (all test bodies on 1 thread) | 3,074 ms (1 thread) |
| `InitializeAsync` blocks 300 ms (no pool threads needed), then completes synchronously | 1,425 ms (5–10 threads) | **2,998 ms (1 thread)** |

One known trade-off is the last row. When a shared `InitializeAsync` blocks and then completes synchronously, `main` resumed the other callers, which were blocked in `Monitor.Enter`, each on its own thread. Here their continuations run inline on the initializing thread, one after another. Each continuation carries its test worker on into its next, already-initialized, tests, so the rest of the run stays on that thread. `main` already behaves this way for shared fixtures that complete asynchronously (third row).

A follow-up could fix both rows without adding a hop for the initializing caller or for already-initialized objects: callers that find the initialization still running would `await Task.Yield()` after waiting. With that change, the same rows measured 1,209 / 1,521 / 1,402 ms on 5–6 threads. The raw data below includes it as `y`.

### Behavior changes

- A caller whose token fires while another caller is still in the synchronous part of `InitializeAsync()` now observes it immediately. Before, it was stuck in `Monitor.Enter` until that part finished.
- `InitializeAsync()` returning `null` now surfaces as one cached `NullReferenceException` for every caller. Before, each caller got a new one, and `IsInitialized` threw.
- If `InitializeAsync()` *synchronously* re-entered `ObjectInitializer` for the *same* object, `Lazy` threw `InvalidOperationException`; that call would now wait on itself, as asynchronous re-entrancy already did. `ObjectInitializer` is internal and nothing does this: TUnit initializes nested objects before their parent, and the only nested call (`PageFixture` → `ContextFixture`) targets a different object.

## Related Issue

Fixes #6904

## Type of Change

- [x] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to change)
- [ ] Documentation update
- [ ] Performance improvement
- [ ] Refactoring (no functional changes)

## Checklist

### Required

- [x] I have read the [Contributing Guidelines](https://github.com/thomhurst/TUnit/blob/main/.github/CONTRIBUTING.md)
- [ ] If this is a new feature, I started a [discussion](https://github.com/thomhurst/TUnit/discussions) first and received agreement (n/a: bug fix)
- [x] My code follows the project's code style (modern C# syntax, proper naming conventions)
- [x] I have written tests that prove my fix is effective or my feature works

### TUnit-Specific Requirements

- [ ] **Dual-Mode Implementation**: n/a. `ObjectInitializer` is on the shared path used after metadata collection. Both modes were tested (see Testing).
- [ ] **Snapshot Tests**: n/a. No generator output or public API change (`ObjectInitializer` is internal).
- [x] **Performance**: If this change affects hot paths (test discovery, execution, assertions):
  - [x] I minimized allocations and avoided LINQ in hot paths
  - [x] I cached reflection results where appropriate (no reflection involved)
- [ ] **AOT Compatibility**: n/a. No reflection; verified with a NativeAOT publish of `TUnit.TestProject` (0 trim/AOT warnings from `TUnit.Core`).

### Testing

- [x] All existing tests pass (`dotnet test`)
- [x] I have added tests that cover my changes
- [x] I have tested both source-generated and reflection modes (if applicable)

`tests/TUnit.UnitTests/ObjectInitializerTests.cs`: the five cases from review, 9 test cases in total. They use gates and flags, not timing thresholds, and every wait is bounded:
- a waiter isn't blocked during the synchronous part (the regression test);
- `InitializeAsync` runs once under contention;
- a failure is cached and rethrown as the same exception object. This case is parameterized over synchronous/asynchronous and ordinary/`OperationCanceledException` failures, and for the latter it also checks the task stays Canceled;
- cancelling the initializing caller doesn't poison the result, with success/failure variants;
- cancelling a waiter doesn't cancel the initialization.

On `main` only the regression test fails. Against the non-`async` `return new ValueTask(...)` form, the two `OperationCanceledException` cases fail.

Runs (local; Linux, 4 cores, SDK 10.0.401):
- `TUnit.Core` builds for netstandard2.0, net8.0, net9.0 and net10.0 with 0 warnings.
- `TUnit.UnitTests`: 390/390 on net8.0, net9.0 and net10.0. The new tests passed 30/30 repeated runs, 10 of them with every core saturated.
- `TUnit.Engine.Tests` (net10.0, `GITHUB_ACTIONS=true`, so both reflection and NativeAOT modes ran): 475 passed, 0 failed, 2 skipped (`JsonOutputTests`, skipped by its own condition). `TUnit.TestProject` was published with `-p:Aot=true` as CI does, with no new trim/AOT warnings.

## Additional Notes

Not changed here, and worth separate follow-ups:
- `BeforeHookTaskCache` runs `Before(Class)`/`Before(Assembly)` hook bodies inside `ThreadSafeDictionary`'s `Lazy` (and `Before(TestSession)` under a `lock`), which is the same blocking pattern for hooks.
- Nothing in production calls `ObjectInitializer.ClearCache()`, so the cache keeps every initialized object for the whole run.

<details>
<summary>Microbenchmark raw reports (<code>Current_Lazy</code> = main, <code>Final_MaintainerShapeAsync</code> = this PR, <code>Final_WaiterYield</code> = the follow-up above, <code>Maintainer_Shape</code> = the non-async form)</summary>

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-LRZOYJ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

MaxIterationCount=60  MinIterationCount=30  

```
| Method                     | Fixture | Parallelism | Mean       | Error     | StdDev    | Median     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |-------- |------------ |-----------:|----------:|----------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **Current_Lazy**               | **Async**   | **1**           | **2,872.8 ns** | **179.23 ns** | **400.88 ns** | **2,824.4 ns** |  **1.02** |    **0.21** | **0.0284** |     **526 B** |        **1.00** |
| Final_MaintainerShapeAsync | Async   | 1           | 3,081.6 ns | 197.98 ns | 442.82 ns | 3,066.9 ns |  1.09 |    0.22 | 0.0341 |     604 B |        1.15 |
| Final_WaiterYield          | Async   | 1           | 3,417.0 ns | 210.47 ns | 470.74 ns | 3,436.6 ns |  1.21 |    0.24 | 0.0313 |     623 B |        1.18 |
| Maintainer_Shape           | Async   | 1           | 2,875.0 ns | 204.02 ns | 456.33 ns | 3,002.8 ns |  1.02 |    0.22 | 0.0260 |     487 B |        0.93 |
|                            |         |             |            |           |           |            |       |         |        |           |             |
| **Current_Lazy**               | **Async**   | **16**          | **1,292.3 ns** |  **68.53 ns** | **153.28 ns** | **1,323.0 ns** |  **1.02** |    **0.18** | **0.0293** |     **528 B** |        **1.00** |
| Final_MaintainerShapeAsync | Async   | 16          | 1,355.3 ns |  66.27 ns | 148.22 ns | 1,388.1 ns |  1.06 |    0.18 | 0.0352 |     608 B |        1.15 |
| Final_WaiterYield          | Async   | 16          | 1,316.6 ns |  66.48 ns | 148.69 ns | 1,339.6 ns |  1.03 |    0.18 | 0.0352 |     624 B |        1.18 |
| Maintainer_Shape           | Async   | 16          | 1,280.7 ns |  68.84 ns | 153.96 ns | 1,335.4 ns |  1.01 |    0.18 | 0.0273 |     488 B |        0.92 |
|                            |         |             |            |           |           |            |       |         |        |           |             |
| **Current_Lazy**               | **Sync**    | **1**           |   **260.3 ns** |  **11.71 ns** |  **25.69 ns** |   **253.2 ns** |  **1.01** |    **0.14** | **0.0117** |     **208 B** |        **1.00** |
| Final_MaintainerShapeAsync | Sync    | 1           |   277.0 ns |  10.30 ns |  22.18 ns |   272.4 ns |  1.07 |    0.13 | 0.0093 |     168 B |        0.81 |
| Final_WaiterYield          | Sync    | 1           |   250.7 ns |   9.16 ns |  20.31 ns |   248.3 ns |  0.97 |    0.12 | 0.0093 |     168 B |        0.81 |
| Maintainer_Shape           | Sync    | 1           |   248.0 ns |  12.53 ns |  27.78 ns |   242.9 ns |  0.96 |    0.14 | 0.0093 |     168 B |        0.81 |
|                            |         |             |            |           |           |            |       |         |        |           |             |
| **Current_Lazy**               | **Sync**    | **16**          |   **509.1 ns** |  **29.78 ns** |  **66.61 ns** |   **517.9 ns** |  **1.02** |    **0.20** | **0.0117** |     **208 B** |        **1.00** |
| Final_MaintainerShapeAsync | Sync    | 16          |   514.0 ns |  36.25 ns |  81.08 ns |   515.5 ns |  1.03 |    0.22 | 0.0098 |     168 B |        0.81 |
| Final_WaiterYield          | Sync    | 16          |   543.9 ns |  52.25 ns | 116.86 ns |   558.0 ns |  1.09 |    0.28 | 0.0098 |     168 B |        0.81 |
| Maintainer_Shape           | Sync    | 16          |   531.8 ns |  49.17 ns | 109.98 ns |   538.3 ns |  1.06 |    0.27 | 0.0098 |     168 B |        0.81 |

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method                     | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| Current_Lazy               | 22.02 ns | 0.648 ns | 1.891 ns |  1.01 |    0.12 |         - |          NA |
| Maintainer_Shape           | 12.74 ns | 0.387 ns | 1.111 ns |  0.58 |    0.07 |         - |          NA |
| Final_MaintainerShapeAsync | 22.65 ns | 1.007 ns | 2.955 ns |  1.04 |    0.16 |         - |          NA |
| Final_WaiterYield          | 20.97 ns | 0.636 ns | 1.856 ns |  0.96 |    0.12 |         - |          NA |

```
Sync   Current_Lazy                 work items per initialization: 0.000
Sync   Maintainer_Shape             work items per initialization: 0.000
Sync   Final_MaintainerShapeAsync   work items per initialization: 0.000
Sync   Final_WaiterYield            work items per initialization: 0.000
Async  Current_Lazy                 work items per initialization: 1.065
Async  Maintainer_Shape             work items per initialization: 1.025
Async  Final_MaintainerShapeAsync   work items per initialization: 1.079
Async  Final_WaiterYield            work items per initialization: 1.065
```
</details>

<details>
<summary>End-to-end raw summaries (<code>final</code> = this PR, <code>y</code> = the follow-up above; TUnit-reported duration)</summary>

Shared-fixture scenarios, 5 alternating runs:
```
SharedBlockingSyncCompletingTests        main : median  1425 ms, IQR 1422-1462, n=5, failed=['0'], test-body threads=['5', '6', '10']
SharedBlockingSyncCompletingTests        final: median  2998 ms, IQR 2992-3001, n=5, failed=['0'], test-body threads=['1']
SharedBlockingSyncCompletingTests        y    : median  1402 ms, IQR 1398-1415, n=5, failed=['0'], test-body threads=['5']
SharedSyncOverAsyncSyncCompletingTests   main : median  5615 ms, IQR 5124-5622, n=5, failed=['0'], test-body threads=['10', '15', '20']
SharedSyncOverAsyncSyncCompletingTests   final: median  2727 ms, IQR 2726-2853, n=5, failed=['0'], test-body threads=['1']
SharedSyncOverAsyncSyncCompletingTests   y    : median  1209 ms, IQR 1182-1251, n=5, failed=['0'], test-body threads=['5', '6']
SharedAsyncCompletingTests               main : median  3015 ms, IQR 3005-3126, n=5, failed=['0'], test-body threads=['1']
SharedAsyncCompletingTests               final: median  3074 ms, IQR 3049-3087, n=5, failed=['0'], test-body threads=['1']
SharedAsyncCompletingTests               y    : median  1521 ms, IQR 1487-1524, n=5, failed=['0'], test-body threads=['5']
SharedFixtureTests                       main : median  1683 ms, IQR 1149-1698, n=5, failed=['16'], test-body threads=['na']
SharedFixtureTests                       final: median   630 ms, IQR 566-654, n=5, failed=['0'], test-body threads=['na']
SharedFixtureTests                       y    : median   628 ms, IQR 612-729, n=5, failed=['0'], test-body threads=['na']
```

Per-test scenarios, 15 alternating runs:
```
PerTestSyncFixtureTests                  main : median  1934 ms, IQR 1864-2005, n=15, failed=['0'], test-body threads=['na']
PerTestSyncFixtureTests                  final: median  1975 ms, IQR 1908-2092, n=15, failed=['0'], test-body threads=['na']
PerTestSyncFixtureTests                  y    : median  1982 ms, IQR 1861-2038, n=15, failed=['0'], test-body threads=['na']
PerTestAsyncFixtureTests                 main : median  1979 ms, IQR 1924-2115, n=15, failed=['0'], test-body threads=['na']
PerTestAsyncFixtureTests                 final: median  1972 ms, IQR 1814-2171, n=15, failed=['0'], test-body threads=['na']
PerTestAsyncFixtureTests                 y    : median  2047 ms, IQR 1993-2175, n=15, failed=['0'], test-body threads=['na']
```
</details>

<details>
<summary>Microbenchmark source: <code>bdn.csproj</code> + <code>Program.cs</code> (run with <code>dotnet run -c Release -- --filter '*'</code>, or <code>-- hops</code> for the work-item count)</summary>

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Optimize>true</Optimize>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
  </ItemGroup>
</Project>
```

```csharp
using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "hops")
{
    await Hops.RunAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(InitBenchmarks).Assembly).Run(args);

public interface IAsyncInitializer { Task InitializeAsync(); }

public sealed class SyncFixture : IAsyncInitializer
{
    public Task InitializeAsync() => Task.CompletedTask;
}

public sealed class AsyncFixture : IAsyncInitializer
{
    public async Task InitializeAsync() => await Task.Yield();
}

// ---- Verbatim copy of main's ObjectInitializer.InitializeCoreAsync ----
public static class Current
{
    public static readonly ConcurrentDictionary<object, Lazy<Task>> InitializationTasks = new(ReferenceEqualityComparer.Instance);

    public static async ValueTask InitializeCoreAsync(object obj, IAsyncInitializer asyncInitializer, CancellationToken cancellationToken)
    {
        var lazyTask = InitializationTasks.GetOrAdd(obj,
            static (_, asyncInitializer) => new Lazy<Task>(
                asyncInitializer.InitializeAsync,
                LazyThreadSafetyMode.ExecutionAndPublication)
            , asyncInitializer);

        try
        {
            await lazyTask.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw;
        }
    }
}

// ---- Reporter's proposal from the issue: Lazy + Task.Run ----
public static class TaskRun
{
    public static readonly ConcurrentDictionary<object, Lazy<Task>> InitializationTasks = new(ReferenceEqualityComparer.Instance);

    public static async ValueTask InitializeCoreAsync(object obj, IAsyncInitializer asyncInitializer, CancellationToken cancellationToken)
    {
        var lazyTask = InitializationTasks.GetOrAdd(obj,
            static (_, asyncInitializer) => new Lazy<Task>(
                () => Task.Run(asyncInitializer.InitializeAsync),
                LazyThreadSafetyMode.ExecutionAndPublication)
            , asyncInitializer);

        await lazyTask.Value.WaitAsync(cancellationToken);
    }
}

// ---- Plain variant D: the winner also waits on the RunContinuationsAsynchronously source ----
public static class PlainTcs
{
    public static readonly ConcurrentDictionary<object, Task> InitializationTasks = new(ReferenceEqualityComparer.Instance);

    public static async ValueTask InitializeCoreAsync(object obj, IAsyncInitializer asyncInitializer, CancellationToken cancellationToken)
    {
        if (!InitializationTasks.TryGetValue(obj, out var initializationTask))
        {
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            initializationTask = InitializationTasks.GetOrAdd(obj, completionSource.Task);

            if (ReferenceEquals(initializationTask, completionSource.Task))
            {
                _ = RunInitializerAsync(asyncInitializer, completionSource);
            }
        }

        await initializationTask.WaitAsync(cancellationToken);
    }

    private static async Task RunInitializerAsync(IAsyncInitializer asyncInitializer, TaskCompletionSource<bool> completionSource)
    {
        try
        {
            await asyncInitializer.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            completionSource.SetException(ex);
            return;
        }

        completionSource.SetResult(true);
    }
}

// ---- Verbatim copy of this PR's ObjectInitializer (InitializeCoreAsync and helpers) ----
public static class Proposed
{
    public static readonly ConcurrentDictionary<object, Task> InitializationTasks = new(ReferenceEqualityComparer.Instance);

    public static async ValueTask InitializeCoreAsync(
        object obj,
        IAsyncInitializer asyncInitializer,
        CancellationToken cancellationToken)
    {
        if (!InitializationTasks.TryGetValue(obj, out var initializationTask))
        {
            // RunContinuationsAsynchronously: when the initialization completes, the continuations of
            // callers waiting on a shared object are queued, rather than run one after another inline
            // on the thread that completed it.
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            initializationTask = InitializationTasks.GetOrAdd(obj, completionSource.Task);

            if (ReferenceEquals(initializationTask, completionSource.Task))
            {
                // Only the caller that published the task runs InitializeAsync - inline, as before - and it
                // awaits it directly, so it pays no extra thread-pool hop or async frame for publishing it.
                var initializerTask = StartInitializer(asyncInitializer);

                try
                {
                    // ConfigureAwait(false): publishing the result for other callers mustn't wait for this
                    // caller's context; this caller's own await still resumes on it.
                    await initializerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Publish the initialization's own outcome - not this caller's cancellation - now,
                    // or once it completes if only this caller stopped waiting.
                    _ = PublishOutcomeAsync(initializerTask, completionSource);
                    throw;
                }

                completionSource.SetResult(true);
                return;
            }
        }

        // Do NOT remove faulted tasks from the cache - subsequent callers get the same error
        // immediately. Removing and retrying can cause hangs when InitializeAsync partially
        // initialized resources (e.g. started ports/processes) that block re-initialization (#4715).
        // The cancellation token only stops this caller waiting; the initialization keeps running.
        await initializationTask.WaitAsync(cancellationToken);
    }

    private static Task StartInitializer(IAsyncInitializer asyncInitializer)
    {
        try
        {
            return asyncInitializer.InitializeAsync()
                ?? throw new InvalidOperationException($"{asyncInitializer.GetType().FullName}.InitializeAsync() returned null.");
        }
        catch (Exception ex)
        {
            // Non-async implementations can throw synchronously (and a null task is a bug in the
            // initializer) - treat either like a faulted initialization.
            return Task.FromException(ex);
        }
    }

    private static async Task PublishOutcomeAsync(Task initializerTask, TaskCompletionSource<bool> completionSource)
    {
        try
        {
            await initializerTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SetException rather than SetCanceled, so callers get the original exception object -
            // including an OperationCanceledException thrown by InitializeAsync.
            completionSource.SetException(ex);

            // The failure itself was observed above; this copy exists for other callers, so don't report
            // it as unobserved if none of them ever awaits it.
            _ = completionSource.Task.Exception;
            return;
        }

        completionSource.SetResult(true);
    }
}


// ---- Maintainer's suggested shape (review on thomhurst/TUnit#6906), verbatim ----
public static class Maintainer
{
    public static readonly ConcurrentDictionary<object, Task> InitializationTasks = new(ReferenceEqualityComparer.Instance);

    public static ValueTask InitializeCoreAsync(object obj, IAsyncInitializer asyncInitializer, CancellationToken cancellationToken)
    {
        if (!InitializationTasks.TryGetValue(obj, out var initializationTask))
        {
            var tcs = new TaskCompletionSource<bool>();
            initializationTask = InitializationTasks.GetOrAdd(obj, tcs.Task);

            if (ReferenceEquals(initializationTask, tcs.Task))
            {
                // Runs inline up to the first await, with no lock held (#6904).
                _ = RunInitializerAsync(asyncInitializer, tcs);
            }
        }

        // Faulted tasks stay cached (#4715). The token only stops this caller waiting.
        return new ValueTask(initializationTask.WaitAsync(cancellationToken));
    }

    private static async Task RunInitializerAsync(IAsyncInitializer asyncInitializer, TaskCompletionSource<bool> tcs)
    {
        try
        {
            await asyncInitializer.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
            return;
        }

        tcs.SetResult(true);
    }
}


// ---- Verbatim copy: Final ----
public static class Final
{
    public static readonly ConcurrentDictionary<object, Task> InitializationTasks = new(ReferenceEqualityComparer.Instance);

    public static async ValueTask InitializeCoreAsync(
        object obj,
        IAsyncInitializer asyncInitializer,
        CancellationToken cancellationToken)
    {
        if (!InitializationTasks.TryGetValue(obj, out var initializationTask))
        {
            var completionSource = new TaskCompletionSource<bool>();
            initializationTask = InitializationTasks.GetOrAdd(obj, completionSource.Task);

            if (ReferenceEquals(initializationTask, completionSource.Task))
            {
                // Only the caller that published the task runs InitializeAsync - inline up to its
                // first await, as before, but with no lock held (#6904).
                _ = RunInitializerAsync(asyncInitializer, completionSource);
            }
        }

        // Do NOT remove faulted tasks from the cache - subsequent callers get the same error
        // immediately. Removing and retrying can cause hangs when InitializeAsync partially
        // initialized resources (e.g. started ports/processes) that block re-initialization (#4715).
        // The cancellation token only stops this caller waiting; the initialization keeps running.
        await initializationTask.WaitAsync(cancellationToken);
    }

    private static async Task RunInitializerAsync(IAsyncInitializer asyncInitializer, TaskCompletionSource<bool> completionSource)
    {
        try
        {
            await asyncInitializer.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SetException rather than SetCanceled, so callers get the original exception object -
            // including an OperationCanceledException thrown by InitializeAsync.
            completionSource.SetException(ex);
            return;
        }

        completionSource.SetResult(true);
    }
}

// ---- Verbatim copy: WaiterYield ----
public static class WaiterYield
{
    public static readonly ConcurrentDictionary<object, Task> InitializationTasks = new(ReferenceEqualityComparer.Instance);

    public static async ValueTask InitializeCoreAsync(
        object obj,
        IAsyncInitializer asyncInitializer,
        CancellationToken cancellationToken)
    {
        if (!InitializationTasks.TryGetValue(obj, out var initializationTask))
        {
            var completionSource = new TaskCompletionSource<bool>();
            initializationTask = InitializationTasks.GetOrAdd(obj, completionSource.Task);

            if (ReferenceEquals(initializationTask, completionSource.Task))
            {
                // Only the caller that published the task runs InitializeAsync - inline up to its
                // first await, as before, but with no lock held (#6904).
                _ = RunInitializerAsync(asyncInitializer, completionSource);
                await initializationTask.WaitAsync(cancellationToken);
                return;
            }
        }

        if (initializationTask.IsCompleted)
        {
            await initializationTask;
            return;
        }

        try
        {
            await initializationTask.WaitAsync(cancellationToken);
        }
        finally
        {
            // Another caller was still initializing: resume on this caller's own thread-pool work item
            // rather than one after another inline on the thread that completed the initialization.
            await Task.Yield();
        }
    }

    private static async Task RunInitializerAsync(IAsyncInitializer asyncInitializer, TaskCompletionSource<bool> completionSource)
    {
        try
        {
            await asyncInitializer.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SetException rather than SetCanceled, so callers get the original exception object -
            // including an OperationCanceledException thrown by InitializeAsync.
            completionSource.SetException(ex);
            return;
        }

        completionSource.SetResult(true);
    }
}

// First initialization of a fresh (per-test) fixture. Each operation creates a fixture, initializes it
// and removes it from the cache again (same cost for every variant) so the cache stays small.


[MemoryDiagnoser]
[MinIterationCount(30)]
[MaxIterationCount(60)]
public class InitBenchmarks
{
    private const int Ops = 16_000;
    // TUnit always passes a cancellable token (the test's).
    private readonly CancellationTokenSource _cts = new();

    [Params("Sync", "Async")]
    public string Fixture { get; set; } = "Sync";

    [Params(1, 16)]
    public int Parallelism { get; set; }

    private IAsyncInitializer Create() => Fixture == "Sync" ? new SyncFixture() : new AsyncFixture();

    private Task Run(Func<IAsyncInitializer, CancellationToken, ValueTask> init, Action<object> remove)
    {
        var ct = _cts.Token;
        var perWorker = Ops / Parallelism;

        async Task Worker()
        {
            for (var i = 0; i < perWorker; i++)
            {
                var f = Create();
                await init(f, ct);
                remove(f);
            }
        }

        if (Parallelism == 1)
        {
            return Worker();
        }

        var workers = new Task[Parallelism];
        for (var w = 0; w < Parallelism; w++)
        {
            workers[w] = Task.Run(Worker);
        }
        return Task.WhenAll(workers);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public Task Current_Lazy() => Run(static (f, ct) => Current.InitializeCoreAsync(f, f, ct), static f => Current.InitializationTasks.TryRemove(f, out _));

    [Benchmark(OperationsPerInvoke = Ops)]
    public Task Proposed_Tcs() => Run(static (f, ct) => Proposed.InitializeCoreAsync(f, f, ct), static f => Proposed.InitializationTasks.TryRemove(f, out _));

    [Benchmark(OperationsPerInvoke = Ops)]
    public Task Final_MaintainerShapeAsync() => Run(static (f, ct) => Final.InitializeCoreAsync(f, f, ct), static f => Final.InitializationTasks.TryRemove(f, out _));

    [Benchmark(OperationsPerInvoke = Ops)]
    public Task Final_WaiterYield() => Run(static (f, ct) => WaiterYield.InitializeCoreAsync(f, f, ct), static f => WaiterYield.InitializationTasks.TryRemove(f, out _));

    [Benchmark(OperationsPerInvoke = Ops)]
    public Task Maintainer_Shape() => Run(static (f, ct) => Maintainer.InitializeCoreAsync(f, f, ct), static f => Maintainer.InitializationTasks.TryRemove(f, out _));

    [Benchmark(OperationsPerInvoke = Ops)]
    public Task PlainTcs_WinnerAwaitsSource() => Run(static (f, ct) => PlainTcs.InitializeCoreAsync(f, f, ct), static f => PlainTcs.InitializationTasks.TryRemove(f, out _));

    [Benchmark(OperationsPerInvoke = Ops)]
    public Task TaskRun_Issue() => Run(static (f, ct) => TaskRun.InitializeCoreAsync(f, f, ct), static f => TaskRun.InitializationTasks.TryRemove(f, out _));
}

// Already-initialized object (every test after the first that uses a shared fixture).
[MemoryDiagnoser]
public class HitPathBenchmarks
{
    private readonly SyncFixture _fixture = new();
    private readonly CancellationTokenSource _cts = new();

    [GlobalSetup]
    public void Setup()
    {
        Current.InitializeCoreAsync(_fixture, _fixture, default).AsTask().GetAwaiter().GetResult();
        Proposed.InitializeCoreAsync(_fixture, _fixture, default).AsTask().GetAwaiter().GetResult();
        Maintainer.InitializeCoreAsync(_fixture, _fixture, default).AsTask().GetAwaiter().GetResult();
        Final.InitializeCoreAsync(_fixture, _fixture, default).AsTask().GetAwaiter().GetResult();
        WaiterYield.InitializeCoreAsync(_fixture, _fixture, default).AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public ValueTask Current_Lazy() => Current.InitializeCoreAsync(_fixture, _fixture, _cts.Token);

    [Benchmark]
    public ValueTask Proposed_Tcs() => Proposed.InitializeCoreAsync(_fixture, _fixture, _cts.Token);

    [Benchmark]
    public ValueTask Maintainer_Shape() => Maintainer.InitializeCoreAsync(_fixture, _fixture, _cts.Token);

    [Benchmark]
    public ValueTask Final_MaintainerShapeAsync() => Final.InitializeCoreAsync(_fixture, _fixture, _cts.Token);

    [Benchmark]
    public ValueTask Final_WaiterYield() => WaiterYield.InitializeCoreAsync(_fixture, _fixture, _cts.Token);
}

public static class Hops
{
    public static async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        var variants = new (string Name, Func<object, IAsyncInitializer, CancellationToken, ValueTask> Init)[]
        {
            ("Current_Lazy", Current.InitializeCoreAsync),
            ("Proposed_Tcs", Proposed.InitializeCoreAsync),
            ("Maintainer_Shape", Maintainer.InitializeCoreAsync),
            ("Final_MaintainerShapeAsync", Final.InitializeCoreAsync),
            ("Final_WaiterYield", WaiterYield.InitializeCoreAsync),
            ("PlainTcs_WinnerAwaitsSource", PlainTcs.InitializeCoreAsync),
            ("TaskRun_Issue", TaskRun.InitializeCoreAsync),
        };
        foreach (var fixtureKind in new[] { "Sync", "Async" })
        {
            foreach (var (name, init) in variants)
            {
                const int n = 100_000;
                var fixtures = new IAsyncInitializer[n];
                for (var i = 0; i < n; i++) fixtures[i] = fixtureKind == "Sync" ? new SyncFixture() : new AsyncFixture();
                // warm up
                for (var i = 0; i < 1000; i++) { IAsyncInitializer f = fixtureKind == "Sync" ? new SyncFixture() : new AsyncFixture(); await init(f, f, cts.Token); }
                await Task.Delay(100);
                var before = ThreadPool.CompletedWorkItemCount;
                foreach (var f in fixtures) await init(f, f, cts.Token);
                await Task.Delay(100);
                var after = ThreadPool.CompletedWorkItemCount;
                Console.WriteLine($"{fixtureKind,-6} {name,-28} work items per initialization: {(after - before - 1) / (double)n:F3}");
            }
        }
    }
}
```
</details>

<details>
<summary>End-to-end scenario project (at <code>benchmarks/Issue6904.Bench/</code> in each checkout) and runner</summary>

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\eng\TestProject.props" />

    <PropertyGroup>
        <TargetFrameworks></TargetFrameworks>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\TUnit.Engine\TUnit.Engine.csproj" />
        <ProjectReference Include="..\..\src\TUnit.Assertions\TUnit.Assertions.csproj" />
        <ProjectReference Include="..\..\src\TUnit.Core.SourceGenerator\TUnit.Core.SourceGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    </ItemGroup>

    <Import Project="..\..\eng\TestProject.targets" />

</Project>
```

```csharp
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
```

```csharp
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
```

```bash
#!/usr/bin/env bash
# Alternates the three builds; records wall-clock, TUnit-reported duration, failures and distinct test-body threads.
set -u
export TUNIT_DISABLE_HTML_REPORTER=true DOTNET_CLI_TELEMETRY_OPTOUT=1
declare -A DIRS=([main]=${MAIN:?} [final]=${PR:?} [y]=${Y:?})  # checkouts, each with benchmarks/Issue6904.Bench built
RUNS=${RUNS:-7}
for scenario in "$@"; do
  for i in $(seq 1 $RUNS); do
    for build in main final y; do
      dir=${DIRS[$build]}/benchmarks/Issue6904.Bench/bin/Release/net10.0
      tf=$(mktemp); export ISSUE6904_THREADS_FILE=$tf
      start=$(date +%s%N)
      out=$(cd $dir && timeout 300 ./Issue6904.Bench --treenode-filter "/*/*/$scenario/*" --no-progress 2>&1)
      end=$(date +%s%N)
      dur=$(grep -oE "duration: [0-9sm ]+ms" <<<"$out" | tail -1 | python3 -c "import sys,re; t=sys.stdin.read(); m=re.findall(r'(\d+)(m|s|ms)\b', t); print(sum(int(v)*{'m':60000,'s':1000,'ms':1}[u] for v,u in m))")
      failed=$(grep -oE "failed: [0-9]+" <<<"$out" | grep -oE "[0-9]+")
      threads=$(grep -oE "threads=[0-9]+" $tf | head -1 | cut -d= -f2); rm -f $tf
      echo "$scenario $build run=$i wall=$(( (end-start)/1000000 )) tunit=$dur failed=$failed threads=${threads:-na}"
    done
  done
done
```
</details>

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01SZ1LwaBqCw5AJXeAU1vVo4
