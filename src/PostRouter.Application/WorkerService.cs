using System.Collections.Concurrent;
using System.Security.Cryptography;
using PostRouter.Domain;

namespace PostRouter.Application;

public sealed class WorkerService(
    IPostRouterStore store,
    IProviderRegistry providers,
    IWorkerLockFactory workerLockFactory,
    IMaintenanceGate maintenanceGate,
    TimeProvider timeProvider,
    IRetryPolicy? retryPolicy = null,
    IAccountOperationLockFactory? accountOperationLocks = null) : IAsyncDisposable
{
    private readonly IRetryPolicy _retryPolicy = retryPolicy ?? new FullJitterRetryPolicy();
    private readonly IAccountOperationLockFactory _accountOperationLocks = accountOperationLocks ?? new NoOpAccountOperationLockFactory();
    private readonly SemaphoreSlim _global = new(2, 2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new(StringComparer.Ordinal);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var workerLock = await workerLockFactory.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (workerLock is null) throw new InvalidOperationException("Another worker owns this installation.");
        await using var maintenance = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var runId = await store.StartWorkerRunAsync(now, cancellationToken).ConfigureAwait(false);
        try
        {
            await store.ClearStopRequestAsync(cancellationToken).ConfigureAwait(false);
            await store.RecoverAbandonedClaimsAsync(runId, now, cancellationToken).ConfigureAwait(false);
            return await RunBatchAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await store.StopWorkerRunAsync(runId, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task RunAsync(TimeSpan idleDelay, CancellationToken cancellationToken)
    {
        await using var workerLock = await workerLockFactory.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (workerLock is null) throw new InvalidOperationException("Another worker owns this installation.");
        await using var maintenance = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        var runId = await store.StartWorkerRunAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var inFlight = new List<Task>();
        try
        {
            await store.ClearStopRequestAsync(cancellationToken).ConfigureAwait(false);
            await store.RecoverAbandonedClaimsAsync(runId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var completed in inFlight.Where(task => task.IsCompleted).ToArray())
                {
                    inFlight.Remove(completed);
                    await completed.ConfigureAwait(false);
                }
                if (await store.IsStopRequestedAsync(cancellationToken).ConfigureAwait(false))
                {
                    await Task.WhenAll(inFlight).ConfigureAwait(false);
                    return;
                }
                var capacity = 2 - inFlight.Count;
                if (capacity > 0 && !await store.IsQuarantinedAsync(cancellationToken).ConfigureAwait(false))
                {
                    var items = await store.ClaimDueAsync(runId, timeProvider.GetUtcNow(), capacity, cancellationToken).ConfigureAwait(false);
                    foreach (var item in items) inFlight.Add(ExecuteItemAsync(item, cancellationToken));
                    if (items.Count > 0) continue;
                }
                var delay = Task.Delay(idleDelay, timeProvider, cancellationToken);
                if (inFlight.Count == 0) await delay.ConfigureAwait(false);
                else await Task.WhenAny([delay, .. inFlight]).ConfigureAwait(false);
            }
        }
        finally
        {
            try { await Task.WhenAll(inFlight).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            await store.StopWorkerRunAsync(runId, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<int> RunBatchAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (await store.IsQuarantinedAsync(cancellationToken).ConfigureAwait(false)) return 0;
        var items = await store.ClaimDueAsync(runId, timeProvider.GetUtcNow(), 32, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(items.Select(item => ExecuteItemAsync(item, cancellationToken))).ConfigureAwait(false);
        return items.Count;
    }

    private async Task ExecuteItemAsync(PublicationWorkItem item, CancellationToken cancellationToken)
    {
        var key = $"{item.Publication.ProviderKey}/{item.Publication.AccountId:D}";
        var accountLock = _accountLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var globalOwned = false;
        try
        {
            await using var crossProcessAccountLock = await _accountOperationLocks.AcquireAsync(item.Publication.AccountId, cancellationToken).ConfigureAwait(false);
            await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
            globalOwned = true;
            var adapter = providers.GetRequired(item.Publication.ProviderKey);
            ProviderStep step;
            StepResult result;
            if (item.Job.Kind == JobKind.Reconcile)
            {
                step = new ProviderStep("reconcile", StepEffect.ReadOnly, ReplaySafety.SafeRead, "reconcile", timeProvider.GetUtcNow());
                var attempt = await store.PrepareDispatchAsync(item, step, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                if (attempt is null) return;
                result = await adapter.ReconcileAsync(item.Input, item.Checkpoint, cancellationToken).ConfigureAwait(false);
                await store.CommitResultAsync(item, attempt, result, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            step = await adapter.PlanNextStepAsync(item.Input, item.Checkpoint, cancellationToken).ConfigureAwait(false);
            var prepared = await store.PrepareDispatchAsync(item, step, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (prepared is null) return;
            try
            {
                result = await adapter.ExecuteStepAsync(step, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = step.Effect is StepEffect.MayPublish or StepEffect.CreateRemoteObject
                    ? new StepResult(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, SafeError: "operation_cancelled_after_dispatch")
                    : new StepResult(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: "operation_cancelled");
            }
            catch (Exception ex)
            {
                result = step.Effect is StepEffect.MayPublish or StepEffect.CreateRemoteObject
                    ? new StepResult(StepOutcome.Ambiguous, EffectCertainty.Ambiguous, SafeError: ex.GetType().Name)
                    : new StepResult(StepOutcome.Pending, EffectCertainty.NoSideEffect, SafeError: ex.GetType().Name);
            }
            if (result.Outcome == StepOutcome.Pending && result.RetryAt is null)
                result = result with { RetryAt = _retryPolicy.NextAttempt(timeProvider.GetUtcNow(), item.Job.AttemptNo + 1) };
            await store.CommitResultAsync(item, prepared, result, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            accountLock.Release();
            if (globalOwned) _global.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _global.Dispose();
        foreach (var semaphore in _accountLocks.Values) semaphore.Dispose();
        _accountLocks.Clear();
        return ValueTask.CompletedTask;
    }
}

public sealed class FullJitterRetryPolicy : IRetryPolicy
{
    public DateTimeOffset NextAttempt(DateTimeOffset now, int attemptNumber)
    {
        var exponent = Math.Clamp(attemptNumber - 1, 0, 20);
        var ceilingMilliseconds = Math.Min(300_000L, checked(2_000L * (1L << exponent)));
        var delayMilliseconds = RandomNumberGenerator.GetInt32(checked((int)ceilingMilliseconds + 1));
        return now.AddMilliseconds(delayMilliseconds);
    }
}

internal sealed class NoOpAccountOperationLockFactory : IAccountOperationLockFactory
{
    public ValueTask<IAccountOperationLock> AcquireAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IAccountOperationLock>(new NoOpLock());
    private sealed class NoOpLock : IAccountOperationLock { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
