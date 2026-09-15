using System.Security.Cryptography;
using PostRouter.Application;

namespace PostRouter.Infrastructure;

public sealed class PostRouterRuntime : IAsyncDisposable
{
    private readonly AesGcmSecretProtector _protector;
    private readonly HttpClient _httpClient;
    internal PostRouterRuntime(string dataDirectory, SqliteStore store, FakeProvider fakeProvider, bool fakeEnabled, FileMaintenanceGate maintenanceGate, AesGcmSecretProtector protector, HttpClient httpClient)
    {
        DataDirectory = dataDirectory;
        Store = store;
        FakeProvider = fakeProvider;
        MaintenanceGate = maintenanceGate;
        _protector = protector;
        _httpClient = httpClient;
        var accountLocks = new FileAccountOperationLockFactory(Path.Combine(dataDirectory, "locks"));
        var grantLocks = new FileAuthGrantLockFactory(Path.Combine(dataDirectory, "locks"));
        var xClient = new XApiClient(httpClient, TimeProvider.System);
        var xAuth = new XAuthProvider(xClient, TimeProvider.System);
        Auth = new(store, store, grantLocks, maintenanceGate, [fakeProvider, xAuth], TimeProvider.System);
        Accounts = new(store, store, maintenanceGate, accountLocks, [xAuth], Auth);
        var adapters = fakeEnabled
            ? new IProviderAdapter[] { fakeProvider, new XProviderAdapter(Auth, xClient, TimeProvider.System) }
            : [new XProviderAdapter(Auth, xClient, TimeProvider.System)];
        var providers = new ProviderRegistry(adapters);
        Posts = new(store, maintenanceGate, providers, TimeProvider.System);
        Worker = new(store, providers, new FileWorkerLockFactory(Path.Combine(dataDirectory, "worker.lock")), maintenanceGate, TimeProvider.System, accountOperationLocks: accountLocks);
        Stats = new(store, maintenanceGate, providers, TimeProvider.System);
        var spoolDirectory = Path.Combine(dataDirectory, "spool");
        Maintenance = new(new DatabaseMaintenance(new SqliteDatabase(Path.Combine(dataDirectory, "post-router.db")), maintenanceGate, spoolDirectory), store, TimeProvider.System);
        Spool = new SpoolStore(spoolDirectory);
    }
    public string DataDirectory { get; }
    public SqliteStore Store { get; }
    public FakeProvider FakeProvider { get; }
    public FileMaintenanceGate MaintenanceGate { get; }
    public AuthCoordinator Auth { get; }
    public AccountConnectionService Accounts { get; }
    public PostService Posts { get; }
    public WorkerService Worker { get; }
    public StatsService Stats { get; }
    public MaintenanceService Maintenance { get; }
    public SpoolStore Spool { get; }
    public async ValueTask DisposeAsync() { await Worker.DisposeAsync(); _httpClient.Dispose(); _protector.Dispose(); }
}

public static class RuntimeFactory
{
    public static async Task<PostRouterRuntime> CreateAsync(string? dataDirectory = null, IMasterKeyStore? keyStore = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory());
        Directory.CreateDirectory(directory);
        keyStore ??= OperatingSystem.IsWindows() ? new WindowsCredentialMasterKeyStore() : TestKeyStoreFromEnvironment();
        var key = keyStore.GetOrCreate("default");
        try
        {
            var protector = new AesGcmSecretProtector(key);
            var maintenanceGate = new FileMaintenanceGate(directory);
            var database = new SqliteDatabase(Path.Combine(directory, "post-router.db"));
            var store = new SqliteStore(database, protector);
            await using (var lease = await maintenanceGate.AcquireSharedAsync(cancellationToken).ConfigureAwait(false))
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var fakeEnabled = string.Equals(Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE"), "test", StringComparison.Ordinal);
            var httpClient = new HttpClient { BaseAddress = new Uri("https://api.x.com/"), Timeout = TimeSpan.FromSeconds(100) };
            return new PostRouterRuntime(directory, store, new FakeProvider(), fakeEnabled, maintenanceGate, protector, httpClient);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static string DefaultDataDirectory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "post-router");
    private static InMemoryMasterKeyStore TestKeyStoreFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE") != "test")
            throw new PlatformNotSupportedException("A supported OS credential store is required. Linux support is not enabled in Phase 1.");
        var encoded = Environment.GetEnvironmentVariable("POST_ROUTER_TEST_MASTER_KEY");
        return new InMemoryMasterKeyStore(encoded is null ? new byte[32] : Convert.FromBase64String(encoded));
    }
}
