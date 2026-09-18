using System.Collections.Concurrent;
using PostRouter.Application;
using PostRouter.Infrastructure;

namespace PostRouter.Gui;

public sealed record InstagramFlowStart(Guid FlowId, string AuthorizationUrl, int ExpiresInSeconds);
public sealed record InstagramFlowStatus(string State, string? ErrorCode, Guid? AccountId);

public sealed class InstagramGuiFlowManager(PostRouterRuntime runtime,
    Func<InstagramCallbackListener>? createListener = null, Action<string>? writeSafeFailure = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, InstagramFlowStatus> _statuses = new();
    private readonly Func<InstagramCallbackListener> _createListener = createListener ?? (() => new InstagramCallbackListener());
    private readonly Action<string> _writeSafeFailure = writeSafeFailure ?? Console.Error.WriteLine;
    private readonly CancellationTokenSource _shutdown = new();
    private InstagramFlowStatus? _latestStatus;
    private Task? _pending;
    private CancellationTokenSource? _flowCancellation;
    private Guid _activeFlowId;
    private int _active;

    public async Task<InstagramFlowStart> StartAsync(Guid? accountId, string? alias, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("instagram_flow_active");
        try
        {
            var appId = await runtime.InstagramOAuth.ReadAppIdAsync(cancellationToken).ConfigureAwait(false);
            var secret = await runtime.InstagramOAuth.ReadAppSecretAsync(cancellationToken).ConfigureAwait(false);
            var redirect = new Uri(InstagramOAuthConfigurationService.RedirectUri);
            var session = accountId is { } id
                ? await runtime.Accounts.BeginReconnectAsync(id, redirect, cancellationToken).ConfigureAwait(false)
                : runtime.Accounts.BeginConnect("instagram", appId, redirect, alias);
            if (session.Provider != "instagram" || session.ClientId != appId)
                throw new InvalidOperationException("instagram_app_id_mismatch");
            var listener = _createListener();
            var flowId = Guid.NewGuid();
            var flowCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _flowCancellation = flowCancellation;
            _activeFlowId = flowId;
            _latestStatus = _statuses[flowId] = new("Pending", null, null);
            _pending = RunAsync(flowId, listener, session, secret, flowCancellation);
            return new(flowId, session.AuthorizationUri.AbsoluteUri, 300);
        }
        catch
        {
            Interlocked.Exchange(ref _active, 0);
            throw;
        }
    }

    public InstagramFlowStatus? Status(Guid flowId) => _statuses.GetValueOrDefault(flowId);

    public InstagramFlowStatus? LatestStatus() => Volatile.Read(ref _latestStatus);

    public bool Cancel(Guid flowId)
    {
        if (_activeFlowId != flowId || _statuses.GetValueOrDefault(flowId)?.State != "Pending") return false;
        _flowCancellation?.Cancel();
        return true;
    }

    private async Task RunAsync(Guid flowId, InstagramCallbackListener listener, AuthorizationSession session, string secret,
        CancellationTokenSource flowCancellation)
    {
        try
        {
            await using (listener.ConfigureAwait(false))
            {
                var account = await listener.ReceiveAsync(session,
                    (code, state, token) => runtime.Accounts.CompleteConnectAsync(session, code, state, secret, token),
                    flowCancellation.Token).ConfigureAwait(false);
                Volatile.Write(ref _latestStatus, _statuses[flowId] = new("Connected", null, account.AccountId));
            }
        }
        catch (Exception ex)
        {
            var code = ex is OperationCanceledException
                ? flowCancellation.IsCancellationRequested ? "instagram_flow_cancelled" : "instagram_callback_timeout"
                : GuiApplication.OAuthErrorCode(ex);
            Volatile.Write(ref _latestStatus, _statuses[flowId] = new("Failed", code, null));
            _writeSafeFailure($"Instagram OAuth failed: {code}");
        }
        finally
        {
            _flowCancellation = null;
            flowCancellation.Dispose();
            Interlocked.Exchange(ref _active, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_pending is not null) await _pending.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
