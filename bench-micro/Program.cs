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
