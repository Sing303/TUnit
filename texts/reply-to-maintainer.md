Thanks, that's much simpler. I've adopted it in 92c54e2:

- One helper runs `InitializeAsync` and completes a plain `TaskCompletionSource`, and every caller waits on it the same way.
- `RunContinuationsAsynchronously`, `StartInitializer`, the null-task `InvalidOperationException`, `PublishOutcomeAsync` and the `Task.Exception` read are gone.
- The tests are down to your five cases (9 test cases). The failure case is parameterised over sync/async and ordinary/`OperationCanceledException` failures, and the initializing-caller case over success/failure. `Waiters_Do_Not_Resume_Inline_...` is gone.

I kept one difference on purpose: `InitializeCoreAsync` stays `async` and ends with `await initializationTask.WaitAsync(cancellationToken)` instead of returning `new ValueTask(...)`. With the non-async form, an `OperationCanceledException` thrown by `InitializeAsync` leaves callers' tasks Faulted instead of Canceled as on `main`. That changes what `Task.WhenAll` surfaces in `StaticPropertyHandler`, which goes through `.AsTask()`. The failure test covers this, and its two OCE cases fail against the non-async form. The cost is the async-method box `main` already has. Against `main`, that is +77 B per first initialization of a per-test fixture whose `InitializeAsync` really awaits, and no time difference beyond noise. If you'd rather save the allocation and accept Faulted, it's a two-line change plus dropping two asserts.

One data point on "continuations run inline as they do on `main` today": that's true when the shared `InitializeAsync` completes asynchronously. It isn't true when it blocks and then completes synchronously. On `main` the other callers were blocked in `Monitor.Enter`, and each one resumed on its own thread. With this change their continuations run inline on the initializing thread. Each one carries its test worker on into its next, already-initialized, tests, so the rest of the run stays on that one thread.

Here are the numbers for 1,000 tests with 2 ms synchronous bodies on one shared fixture (TUnit-reported duration, median of 5 runs, real TUnit builds):

| Shared fixture | `main` | this PR | + waiter yield |
|---|---|---|---|
| blocks 300 ms, completes synchronously | 1,425 ms (5–10 threads) | 2,998 ms (1 thread) | 1,402 ms (5 threads) |
| sync-over-async, completes synchronously | 5,615 ms | 2,727 ms (1 thread) | 1,209 ms |
| awaits 300 ms | 3,015 ms (1 thread) | 3,074 ms (1 thread) | 1,521 ms (5 threads) |

The last column is a small change that keeps the single completion path. Callers that find the initialization still running do `await Task.Yield()` after waiting. The initializing caller and already-initialized objects are untouched, so the work items are still the same as `main` (0 / 1 per initialization) and there's no measurable difference for per-test fixtures. It also fixes the existing single-thread behaviour on `main` for shared fixtures that complete asynchronously (last row).

I have it ready as a separate commit with a deterministic test. I can push it here or send it as the follow-up, whichever you prefer. The PR description has the benchmark sources and raw numbers.
