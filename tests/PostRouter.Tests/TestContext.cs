using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now = _now.Add(value);
}

internal sealed class TestContext : IAsyncDisposable
{
    private readonly AesGcmSecretProtector _protector;
    private TestContext(string directory, ManualTimeProvider time, AesGcmSecretProtector protector, SqliteDatabase database, SqliteStore store, FileMaintenanceGate gate, FakeProvider provider, PostService posts, WorkerService worker)
    {
        Directory = directory; Time = time; _protector = protector; Database = database; Store = store; Gate = gate; Provider = provider; Posts = posts; Worker = worker;
        Stats = new(store, gate, new ProviderRegistry([provider]), time);
        Maintenance = new(new DatabaseMaintenance(database, gate, Path.Combine(directory, "spool")), store, time);
    }
    public string Directory { get; }
    public ManualTimeProvider Time { get; }
    public SqliteDatabase Database { get; }
    public SqliteStore Store { get; }
    public FileMaintenanceGate Gate { get; }
    public FakeProvider Provider { get; }
    public PostService Posts { get; }
    public WorkerService Worker { get; }
    public StatsService Stats { get; }
    public MaintenanceService Maintenance { get; }
    public Guid AccountId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static async Task<TestContext> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        var database = new SqliteDatabase(Path.Combine(directory, "post-router.db"));
        var store = new SqliteStore(database, protector);
        var gate = new FileMaintenanceGate(directory);
        await store.InitializeAsync();
        var provider = new FakeProvider();
        var providers = new ProviderRegistry([provider]);
        var posts = new PostService(store, gate, providers, time);
        var worker = new WorkerService(store, providers, new FileWorkerLockFactory(Path.Combine(directory, "worker.lock")), gate, time);
        return new(directory, time, protector, database, store, gate, provider, posts, worker);
    }

    public CanonicalPostIntent Intent(string key = "release-a", string text = "hello", DateTimeOffset? due = null, TimeSpan? maxLateness = null) =>
        new(key, new Content(Guid.NewGuid(), ContentKind.TextOnly, text, null, []),
            [new TargetIntent(AccountId, "fake", "public", "fake-options/v1", 1, "{}")],
            new ScheduleIntent(due is null ? ScheduleMode.Immediate : ScheduleMode.AtTime, due ?? Time.GetUtcNow(), maxLateness ?? TimeSpan.FromMinutes(15)));

    public async ValueTask DisposeAsync()
    {
        await Worker.DisposeAsync();
        _protector.Dispose();
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
    }
}
