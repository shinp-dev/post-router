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
    public async Task<AuthGrantRecord> RefreshAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        await using var maintenance = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false);
        await using var grantLock = await grantLocks.AcquireAsync(grantId, cancellationToken).ConfigureAwait(false);
        var grant = await store.GetAuthGrantAsync(grantId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Auth grant not found.");
        if (grant.ExpiresAt > timeProvider.GetUtcNow().AddMinutes(5)) return grant;

        var bytes = await vault.GetAsync(grant.VaultBlobId, "auth-token", cancellationToken).ConfigureAwait(false);
        TokenMaterial current;
        try { current = JsonSerializer.Deserialize<TokenMaterial>(bytes) ?? throw new InvalidDataException("Invalid token material."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var provider = providers.SingleOrDefault(x => x.ProviderKey == grant.Provider)
            ?? throw new InvalidOperationException("Auth provider is not registered.");
        var refreshed = await provider.RefreshAsync(current, cancellationToken).ConfigureAwait(false);
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
}
