using System.Collections.Concurrent;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

public sealed class FakeProvider : IProviderAdapter, IAuthProvider, IMetricsProvider
{
    private readonly ConcurrentQueue<StepResult> _publish = new();
    private readonly ConcurrentQueue<StepResult> _reconcile = new();
    private int _publishCalls;
    private int _refreshCalls;
    public string ProviderKey => "fake";
    public bool RequiresConnectedAccount => false;
    public void Validate(Content content, TargetIntent target) { }
    public int PublishCalls => Volatile.Read(ref _publishCalls);
    public int RefreshCalls => Volatile.Read(ref _refreshCalls);
    public TimeSpan Delay { get; set; }

    public void QueuePublish(params StepResult[] results) { foreach (var result in results) _publish.Enqueue(result); }
    public void QueueReconcile(params StepResult[] results) { foreach (var result in results) _reconcile.Enqueue(result); }

    public Task<ProviderStep> PlanNextStepAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderStep("publish", StepEffect.MayPublish, ReplaySafety.NotReplayable, $"fake:{input.Publication.Id:D}:{input.Content.Kind}", DateTimeOffset.UtcNow));

    public async Task<StepResult> ExecuteStepAsync(ProviderStep providerStep, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _publishCalls);
        var delay = Delay;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        return _publish.TryDequeue(out var result) ? result : new(StepOutcome.Completed, EffectCertainty.Confirmed, $"fake-{Guid.NewGuid():N}", ObservedState: PublicationState.Published);
    }

    public Task<StepResult> ReconcileAsync(ProviderPublication input, string? checkpoint, CancellationToken cancellationToken) =>
        Task.FromResult(_reconcile.TryDequeue(out var result) ? result : new StepResult(StepOutcome.Pending, EffectCertainty.NoSideEffect, RetryAt: DateTimeOffset.UtcNow.AddMinutes(1)));

    public Task<RefreshResult> RefreshAsync(AuthGrantRecord grant, TokenMaterial current, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refreshCalls);
        return Task.FromResult(new RefreshResult(new($"access-{Guid.NewGuid():N}", current.RefreshToken ?? "refresh", DateTimeOffset.UtcNow.AddHours(1)), "fake-request"));
    }

    public Task<StatsWrite> FetchMetricsAsync(Guid accountId, string subjectRef, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("{\"view_count\":100,\"like_count\":5}");
        var snapshot = new MetricsSnapshot(Guid.NewGuid(), accountId, ProviderKey, subjectRef, "fake-v1", now, null, null, "{}", "fake/v1",
        [
            new("view_count", "exposure.views", 1, 100, null, "count", MetricValueStatus.Available),
            new("like_count", "engagement.likes", 1, 5, null, "count", MetricValueStatus.Available),
            new("watch_time", "consumption.watch_time", 1, null, null, "seconds", MetricValueStatus.NotReturned),
        ]);
        return Task.FromResult(new StatsWrite(new(ProviderKey, "fake-v1", subjectRef, raw, now.AddDays(7)), snapshot));
    }
}

public sealed class ProviderRegistry(IEnumerable<IProviderAdapter> adapters) : IProviderRegistry
{
    private readonly Dictionary<string, IProviderAdapter> _adapters = adapters.ToDictionary(x => x.ProviderKey, StringComparer.OrdinalIgnoreCase);
    public IProviderAdapter GetRequired(string providerKey) => _adapters.TryGetValue(providerKey, out var adapter) ? adapter : throw new NotSupportedException($"Provider '{providerKey}' is not registered.");
}
