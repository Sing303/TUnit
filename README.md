# thomhurst/TUnit#6904: waiter-yield patch and benchmark sources

## Where things are

| What | Where (Sing303/TUnit) |
|---|---|
| The PR under review | thomhurst/TUnit#6906, from branch `claude/festive-hypatia-w79bcn` at `92c54e2`. It follows the maintainer's suggested shape and keeps an `async` wrapper. |
| Follow-up: `Task.Yield()` for waiting callers | branch `claude/issue6904-waiter-yield`: `92c54e2` plus `6f1c971`. It is not part of the PR yet; see `texts/reply-to-maintainer.md`. |
| These materials | branch `claude/issue6904-handoff`. It is an orphan branch and holds only this folder, no TUnit code. |

```bash
git fetch https://github.com/Sing303/TUnit claude/issue6904-waiter-yield claude/issue6904-handoff
git cherry-pick 6f1c971            # or apply waiter-yield.patch; both apply cleanly on 92c54e2
```

`texts/` holds the new description for thomhurst/TUnit#6906 and the reply to the maintainer's review as they were drafted; check #6906 for what was actually posted.

## waiter-yield.patch

- A `git format-patch` of commit `6f1c971`, made on top of `92c54e2`. That commit is the current head of `claude/festive-hypatia-w79bcn`, which is thomhurst/TUnit#6906.
- It changes `src/TUnit.Core/ObjectInitializer.cs`: callers that find the initialization still running do `await Task.Yield()` after waiting.
- It adds the test `Waiters_Resume_Off_The_Thread_That_Completes_A_Synchronously_Blocking_Initialization` to `tests/TUnit.UnitTests/ObjectInitializerTests.cs`. The test covers both a cancellable and a non-cancellable wait.

To apply:

```bash
git checkout claude/festive-hypatia-w79bcn   # at 92c54e2
git am waiter-yield.patch                    # or: git apply waiter-yield.patch
cd tests/TUnit.UnitTests
dotnet test -c Release -f net10.0 -- --treenode-filter "/*/*/ObjectInitializerTests/*"
```

Checked before handing over:
- `git apply --check` passes on `92c54e2`.
- Without the source change, the new test fails in both variants.
- With it, `ObjectInitializerTests` passes 11/11 and `TUnit.UnitTests` passes 392/392 on net8.0, net9.0 and net10.0. `TUnit.Core` builds with 0 warnings.
- The full engine-test and AOT runs were done for `92c54e2` only, not with this patch applied.

## bench-micro/ (BenchmarkDotNet)

Self-contained console project with BenchmarkDotNet 0.15.8. It holds verbatim copies of these variants of `InitializeCoreAsync`:

| Class | What it is |
|---|---|
| `Current` | `main` (`Lazy<Task>`) |
| `Final` | the current PR (`92c54e2`): the maintainer's shape with an `async` wrapper |
| `WaiterYield` | `92c54e2` plus `waiter-yield.patch` |
| `Maintainer` | the maintainer's literal non-async suggestion |
| `Proposed` | the earlier PR version |
| `PlainTcs` | a plain `TaskCompletionSource` with `RunContinuationsAsynchronously` |
| `TaskRun` | the issue's `Task.Run` proposal |

`global.json` pins SDK 10.0.401. Change or delete it if you use another SDK.

```bash
cd bench-micro
dotnet run -c Release -- --filter '*'           # InitBenchmarks + HitPathBenchmarks
dotnet run -c Release -- hops                    # thread-pool work items per initialization
```

## bench-e2e/ (real TUnit builds)

`Issue6904.Bench/` is a TUnit test project. It references TUnit through relative project paths (`..\..\src\...`), so copy it to `benchmarks/Issue6904.Bench/` inside each TUnit checkout you want to compare, then build it:

```bash
dotnet build benchmarks/Issue6904.Bench/Issue6904.Bench.csproj -c Release
```

`run.sh` alternates the builds and prints, for each run:
- wall-clock time;
- the duration TUnit reports;
- failures;
- the number of distinct threads that ran test bodies.

Usage:

```bash
MAIN=/path/to/tunit-main PR=/path/to/tunit-92c54e2 Y=/path/to/tunit-92c54e2+patch RUNS=5 \
  ./run.sh SharedBlockingSyncCompletingTests SharedSyncOverAsyncSyncCompletingTests SharedAsyncCompletingTests SharedFixtureTests
RUNS=15 ./run.sh PerTestSyncFixtureTests PerTestAsyncFixtureTests   # same MAIN/PR/Y
```

The thread count needs `ISSUE6904_THREADS_FILE`, which `run.sh` sets. It is written by an `[After(TestSession)]` hook in `Scenarios2.cs`.

## results/

Raw outputs from the runs quoted in the PR description and reply. All runs were on Linux with 4 cores and .NET 10.0.12.

- `micro/InitBenchmarks-report-github.md`, `micro/HitPathBenchmarks-report-github.md`: BenchmarkDotNet reports.
- `micro/hops.txt`: work-item counts.
- `e2e/results-4way-shared.txt`: one line per run for the shared-fixture scenarios, 5 runs per build. `final` is `92c54e2`, `y` is with the patch.
- `e2e/results-4way-pertest15.txt`: the same for the per-test scenarios, 15 runs per build.
