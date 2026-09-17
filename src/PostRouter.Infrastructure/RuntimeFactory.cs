using System.Security.Cryptography;
using PostRouter.Application;

namespace PostRouter.Infrastructure;

public sealed class PostRouterRuntime : IAsyncDisposable
{
    private readonly AesGcmSecretProtector _protector;
    private readonly HttpClient _xHttpClient;
    private readonly HttpClient _youtubeHttpClient;

    internal PostRouterRuntime(
        string dataDirectory,
        SqliteStore store,
        FakeProvider fakeProvider,
        bool fakeEnabled,
        FileMaintenanceGate maintenanceGate,
        AesGcmSecretProtector protector,
        HttpClient xHttpClient,
        HttpClient youtubeHttpClient)
    {
        DataDirectory = dataDirectory;
        Store = store;
        var mediaJournal = new FilePublicMediaOperationStore(dataDirectory);
        GitHubMedia = new GitHubMediaConfigurationService(new FileGitHubMediaConfigurationStore(dataDirectory), store, mediaJournal);
        FakeProvider = fakeProvider;
        MaintenanceGate = maintenanceGate;
        _protector = protector;
        _xHttpClient = xHttpClient;
        _youtubeHttpClient = youtubeHttpClient;
        var database = new SqliteDatabase(Path.Combine(dataDirectory, "post-router.db"));
        Approvals = new PublicationApprovalStore(database);
        var applicationStore = new ApprovalAwarePostRouterStore(store, Approvals, TimeProvider.System);
        var accountLocks = new FileAccountOperationLockFactory(Path.Combine(dataDirectory, "locks"));
        var grantLocks = new FileAuthGrantLockFactory(Path.Combine(dataDirectory, "locks"));
        var xClient = new XApiClient(xHttpClient, TimeProvider.System);
        var xAuth = new XAuthProvider(xClient, TimeProvider.System);
        var youtubeClient = new YouTubeApiClient(youtubeHttpClient, TimeProvider.System);
        var youtubeAuth = new YouTubeConsentAuthProvider(new YouTubeAuthProvider(youtubeClient, TimeProvider.System));
        Auth = new(applicationStore, store, grantLocks, maintenanceGate, [fakeProvider, xAuth, youtubeAuth], TimeProvider.System);
        Accounts = new(applicationStore, store, maintenanceGate, accountLocks, [xAuth, youtubeAuth], Auth);
        var xAdapter = new XProviderAdapter(Auth, xClient, TimeProvider.System);
        var youtubeAdapter = new YouTubeResumeSafeAdapter(
            new YouTubeProviderAdapter(Auth, youtubeClient, TimeProvider.System),
            TimeProvider.System,
            Auth,
            youtubeClient);
        var adapters = fakeEnabled
            ? new IProviderAdapter[] { fakeProvider, xAdapter, youtubeAdapter }
            : [xAdapter, youtubeAdapter];
        var providers = new ProviderRegistry(adapters);
        Posts = new(applicationStore, maintenanceGate, providers, TimeProvider.System);
        Operations = new(applicationStore, maintenanceGate, providers, Posts, TimeProvider.System, Approvals);
        Worker = new(applicationStore, providers, new FileWorkerLockFactory(Path.Combine(dataDirectory, "worker.lock")), maintenanceGate, TimeProvider.System, accountOperationLocks: accountLocks);
        Stats = new(applicationStore, maintenanceGate, providers, TimeProvider.System);
        var spoolDirectory = Path.Combine(dataDirectory, "spool");
        Maintenance = new(new DatabaseMaintenance(database, maintenanceGate, spoolDirectory), applicationStore, TimeProvider.System);
        Spool = new SpoolStore(spoolDirectory);
        MediaOperations = new PublicMediaOperations(Spool, mediaJournal);
    }

    public string DataDirectory { get; }
    public SqliteStore Store { get; }
    public GitHubMediaConfigurationService GitHubMedia { get; }

    public async Task<GitHubReleaseMediaHost> CreateGitHubMediaHostAsync(CancellationToken cancellationToken = default)
    {
        var settings = await new FileGitHubMediaConfigurationStore(DataDirectory).ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("github_media_settings_missing");
        if (settings.CredentialBlobId is null)
            throw new InvalidOperationException("github_media_credential_missing");
        return GitHubReleaseMediaHost.CreateProduction(
            new GitHubReleaseMediaHostOptions(settings.Owner, settings.Repository, settings.ReleaseTag),
            new VaultGitHubMediaTokenSource(Store, settings.CredentialBlobId));
    }

    public async Task<PublicMediaOperationView> StageGitHubMediaAsync(string sourcePath,
        Func<CancellationToken, Task<GitHubReleaseMediaHost>> createHost,
        GitHubMediaConfigurationStatus? expectedTarget = null, CancellationToken cancellationToken = default)
    {
        var settingsStore = new FileGitHubMediaConfigurationStore(DataDirectory);
        await using var lease = await settingsStore.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var settings = await settingsStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("github_media_settings_missing");
        if (expectedTarget is not null
            && (!string.Equals(settings.Owner, expectedTarget.Owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(settings.Repository, expectedTarget.Repository, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(settings.ReleaseTag, expectedTarget.ReleaseTag, StringComparison.Ordinal)))
            throw new InvalidOperationException("github_media_settings_changed");
        using var host = await createHost(cancellationToken).ConfigureAwait(false);
        await host.CheckConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await MediaOperations.StageAsync(sourcePath, host, cancellationToken).ConfigureAwait(false);
    }
    public PublicationApprovalStore Approvals { get; }
    public FakeProvider FakeProvider { get; }
    public FileMaintenanceGate MaintenanceGate { get; }
    public AuthCoordinator Auth { get; }
    public AccountConnectionService Accounts { get; }
    public PostService Posts { get; }
    public OperationsService Operations { get; }
    public WorkerService Worker { get; }
    public StatsService Stats { get; }
    public MaintenanceService Maintenance { get; }
    public SpoolStore Spool { get; }
    public PublicMediaOperations MediaOperations { get; }

    public async ValueTask DisposeAsync()
    {
        await Worker.DisposeAsync();
        _youtubeHttpClient.Dispose();
        _xHttpClient.Dispose();
        _protector.Dispose();
    }
}

public static class RuntimeFactory
{
    public static async Task<PostRouterRuntime> CreateAsync(
        string? dataDirectory = null,
        IMasterKeyStore? keyStore = null,
        CancellationToken cancellationToken = default)
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
            var xHttpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.x.com/"),
                Timeout = TimeSpan.FromSeconds(100),
            };
            // A resumable YouTube data PUT may legitimately run far longer than the normal API
            // request timeout. Bound it independently so a healthy large upload is not turned into
            // a sequence of artificial 100-second failures; interruption still resumes by remote offset.
            var youtubeHttpClient = new HttpClient
            {
                BaseAddress = new Uri("https://www.googleapis.com/"),
                Timeout = TimeSpan.FromHours(12),
            };
            return new PostRouterRuntime(
                directory, store, new FakeProvider(), fakeEnabled, maintenanceGate, protector,
                xHttpClient, youtubeHttpClient);
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
