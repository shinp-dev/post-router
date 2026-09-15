using System.Security.Cryptography;
using System.Text.Json;

namespace PostRouter.Application;

public sealed class AuthCoordinator(
    IPostRouterStore store,
    IVault vault,
    IAuthGrantLockFactory grantLocks,
    IMaintenanceGate maintenanceGate,
    IEnumerable<IAuthProvider> providers,
    TimeProvider timeProvider)
{
    public async Task<TokenMaterial> GetValidTokenAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var grant = await store.GetAuthGrantForAccountAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("auth_required");
        if (!string.Equals(grant.Status, "Connected", StringComparison.Ordinal))
            throw new InvalidOperationException("auth_required");
        if (grant.ExpiresAt <= timeProvider.GetUtcNow().AddMinutes(5))
            grant = await RefreshAsync(grant.Id, cancellationToken).ConfigureAwait(false);
        return await ReadMaterialAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthGrantRecord> RefreshAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        await using var maintenance = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        await using var grantLock = await grantLocks.AcquireAsync(grantId, cancellationToken).ConfigureAwait(false);
        var grant = await store.GetAuthGrantAsync(grantId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Auth grant not found.");
        if (grant.ExpiresAt > timeProvider.GetUtcNow().AddMinutes(5)) return grant;

        var current = await ReadMaterialAsync(grant, cancellationToken).ConfigureAwait(false);
        var provider = providers.SingleOrDefault(x => x.ProviderKey == grant.Provider)
            ?? throw new InvalidOperationException("Auth provider is not registered.");
        var refreshed = await provider.RefreshAsync(grant, current, cancellationToken).ConfigureAwait(false);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(refreshed.Material);
        string newBlob;
        try { newBlob = await vault.PutAsync("auth-token", serialized, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(serialized); }
        var replaced = await store.ReplaceAuthGrantAsync(grant.Id, grant.Generation, newBlob, refreshed.Material.ExpiresAt, cancellationToken).ConfigureAwait(false);
        if (!replaced)
        {
            await vault.DeleteAsync(newBlob, cancellationToken).ConfigureAwait(false);
            return await store.GetAuthGrantAsync(grantId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Auth grant disappeared during refresh.");
        }
        await vault.DeleteAsync(grant.VaultBlobId, cancellationToken).ConfigureAwait(false);
        return (await store.GetAuthGrantAsync(grantId, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<TokenMaterial> ReadMaterialAsync(AuthGrantRecord grant, CancellationToken cancellationToken)
    {
        var bytes = await vault.GetAsync(grant.VaultBlobId, "auth-token", cancellationToken).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<TokenMaterial>(bytes) ?? throw new InvalidDataException("Invalid token material."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed class AccountConnectionService(
    IPostRouterStore store,
    IVault vault,
    IMaintenanceGate maintenanceGate,
    IAccountOperationLockFactory accountLocks,
    IEnumerable<IInteractiveAuthProvider> providers,
    AuthCoordinator auth)
{
    public AuthorizationSession BeginConnect(string providerKey, string clientId, Uri redirectUri, string? alias = null)
    {
        var provider = GetProvider(providerKey);
        return provider.BeginAuthorization(clientId, redirectUri, alias: alias);
    }

    public async Task<AuthorizationSession> BeginReconnectAsync(Guid accountId, Uri redirectUri, CancellationToken cancellationToken = default)
    {
        var connection = await store.GetAccountConnectionAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Account not found.");
        var provider = GetProvider(connection.Provider);
        return provider.BeginAuthorization(connection.ClientId, redirectUri, accountId, connection.RemoteSubject, connection.Alias);
    }

    public async Task<AccountConnection> CompleteConnectAsync(AuthorizationSession session, string code, string returnedState, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(session.Provider);
        var identity = await provider.CompleteAuthorizationAsync(session, code, returnedState, cancellationToken).ConfigureAwait(false);
        if (session.ExpectedSubject is not null && !string.Equals(session.ExpectedSubject, identity.RemoteSubject, StringComparison.Ordinal))
            throw new InvalidOperationException("reconnect_account_mismatch");

        await using var maintenance = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(identity.Material);
        string blobId;
        try { blobId = await vault.PutAsync("auth-token", bytes, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        try
        {
            var alias = session.Alias ?? identity.DisplayName;
            var write = new AccountConnectionWrite(session.Provider, alias, identity.RemoteSubject, identity.DisplayName,
                session.ClientId, identity.Scope, identity.Material.ExpiresAt, blobId);
            AccountConnection connection;
            if (session.ExpectedAccountId is { } expectedAccountId)
            {
                await using var accountLock = await accountLocks.AcquireAsync(expectedAccountId, cancellationToken).ConfigureAwait(false);
                connection = await store.SaveConnectedAccountAsync(write, cancellationToken).ConfigureAwait(false);
            }
            else connection = await store.SaveConnectedAccountAsync(write, cancellationToken).ConfigureAwait(false);
            if (session.ExpectedAccountId is not null && connection.AccountId != session.ExpectedAccountId)
            {
                await store.DisconnectAccountAsync(connection.AccountId, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("reconnect_account_mismatch");
            }
            return connection;
        }
        catch
        {
            await vault.DeleteAsync(blobId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<AccountConnection?> StatusAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        store.GetAccountConnectionAsync(accountId, cancellationToken);

    public async Task<bool> DisconnectAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var maintenance = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        await using var accountLock = await accountLocks.AcquireAsync(accountId, cancellationToken).ConfigureAwait(false);
        return await store.DisconnectAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountRevokeResult> RevokeAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var maintenance = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        await using var accountLock = await accountLocks.AcquireAsync(accountId, cancellationToken).ConfigureAwait(false);
        var connection = await store.GetAccountConnectionAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Account not found.");
        var grant = await store.GetAuthGrantForAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var remoteRevoked = false;
        string? safeError = null;
        if (grant is not null)
        {
            try
            {
                var material = await auth.GetValidTokenAsync(accountId, cancellationToken).ConfigureAwait(false);
                await GetProvider(connection.Provider).RevokeAsync(grant, material, cancellationToken).ConfigureAwait(false);
                remoteRevoked = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { safeError = "remote_revoke_failed"; }
        }
        var disconnected = await store.DisconnectAccountAsync(accountId, CancellationToken.None).ConfigureAwait(false);
        return new(accountId, remoteRevoked, disconnected, safeError);
    }

    private IInteractiveAuthProvider GetProvider(string providerKey) =>
        providers.SingleOrDefault(candidate => string.Equals(candidate.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
        ?? throw new NotSupportedException($"Provider '{providerKey}' does not support interactive authentication.");
}
