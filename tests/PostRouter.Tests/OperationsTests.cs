using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class OperationsTests
{
    [Fact]
    public async Task Worker_lock_rejects_duplicate_coordinator()
    {
        await using var context = await TestContext.CreateAsync();
        var factory = new FileWorkerLockFactory(Path.Combine(context.Directory, "worker.lock"));
        await using var owned = await factory.TryAcquireAsync();
        Assert.NotNull(owned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Worker.RunOnceAsync());
    }

    [Fact]
    public async Task Continuous_worker_claims_new_due_work_while_another_account_is_slow()
    {
        await using var context = await TestContext.CreateAsync();
        context.Provider.Delay = TimeSpan.FromMilliseconds(700);
        await context.Posts.EnqueueAsync(context.Intent(key: "slow"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = context.Worker.RunAsync(TimeSpan.FromMilliseconds(25), cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => context.Provider.PublishCalls == 1, TimeSpan.FromSeconds(2)));

        context.Provider.Delay = TimeSpan.Zero;
        var second = context.Intent(key: "urgent") with
        {
            Targets = [new TargetIntent(Guid.Parse("33333333-3333-3333-3333-333333333333"), "fake", "public", "fake-options/v1", 1, "{}")],
        };
        await context.Posts.EnqueueAsync(second);
        Assert.True(SpinWait.SpinUntil(() => context.Provider.PublishCalls == 2, TimeSpan.FromMilliseconds(500)));
        await context.Store.RequestStopAsync(cancellation.Token);
        await running;
    }

    [Fact]
    public async Task Maintenance_exclusive_waits_for_shared_and_blocks_new_shared()
    {
        await using var context = await TestContext.CreateAsync();
        await using var shared = await context.Gate.AcquireSharedAsync();
        Assert.Null(await context.Gate.TryAcquireExclusiveAsync(TimeSpan.FromMilliseconds(100)));
        await shared.DisposeAsync();
        await using var exclusive = await context.Gate.TryAcquireExclusiveAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(exclusive);
    }

    [Fact]
    public async Task Shared_auth_grant_refresh_is_serialized_and_rotated_once()
    {
        await using var context = await TestContext.CreateAsync();
        var token = new TokenMaterial("secret-access-marker", "secret-refresh-marker", context.Time.GetUtcNow().AddMinutes(-1));
        var blob = await context.Store.PutAsync("auth-token", JsonSerializer.SerializeToUtf8Bytes(token));
        var grantId = Guid.NewGuid();
        await context.Store.SaveAuthGrantAsync(new(grantId, "fake", "subject", 0, token.ExpiresAt, blob, "Ready"));
        var coordinator = new AuthCoordinator(context.Store, context.Store, new FileAuthGrantLockFactory(context.Directory), context.Gate, [context.Provider], context.Time);
        var results = await Task.WhenAll(coordinator.RefreshAsync(grantId), coordinator.RefreshAsync(grantId));
        Assert.Equal(1, context.Provider.RefreshCalls);
        Assert.All(results, result => Assert.Equal(1, result.Generation));
        var backup = await context.Maintenance.BackupAsync(Path.Combine(context.Directory, "secret-check"));
        var bytes = await File.ReadAllBytesAsync(backup.Path);
        Assert.DoesNotContain("secret-access-marker", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-refresh-marker", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_metrics_keep_meaning_and_expire_with_projection()
    {
        await using var context = await TestContext.CreateAsync();
        var snapshot = new MetricsSnapshot(Guid.NewGuid(), context.AccountId, "fake", "post-1", "v1", context.Time.GetUtcNow(), null, null, "{}", "fake/v1",
            [new("impression_count", "exposure.impressions", 1, 12, null, "count", MetricValueStatus.Available), new("reach", "exposure.reach", 1, null, null, "count", MetricValueStatus.NotReturned)]);
        var raw = new RawMetricInput("fake", "v1", "post-1", Encoding.UTF8.GetBytes("raw-secret-marker"), context.Time.GetUtcNow().AddHours(1));
        await context.Stats.SaveAsync(raw, snapshot);
        var loaded = Assert.Single(await context.Stats.ShowAsync(context.AccountId, "post-1"));
        Assert.Equal(2, loaded.Observations.Count);
        var notReturned = Assert.Single(loaded.Observations, observation => observation.Status == MetricValueStatus.NotReturned);
        Assert.Null(notReturned.DecimalValue);
        context.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await context.Stats.PurgeExpiredAsync());
        Assert.Empty(await context.Stats.ShowAsync(context.AccountId, "post-1"));
    }

    [Fact]
    public async Task Fake_metrics_sync_keeps_raw_keys_and_missing_values_distinct()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Posts.EnqueueAsync(context.Intent(key: "metrics-account"));
        var snapshot = await context.Stats.SyncAsync("fake", context.AccountId, "post-2");
        Assert.Contains(snapshot.Observations, metric => metric.ProviderMetricKey == "view_count" && metric.DecimalValue == 100);
        Assert.Contains(snapshot.Observations, metric => metric.ProviderMetricKey == "watch_time" && metric.Status == MetricValueStatus.NotReturned && metric.DecimalValue is null);
        Assert.Single(await context.Stats.ShowAsync(context.AccountId, "post-2"));
    }

    [Fact]
    public async Task Doctor_checks_database_schema_vault_and_quarantine()
    {
        await using var context = await TestContext.CreateAsync();
        var checks = await context.Maintenance.DoctorAsync();
        Assert.Contains(checks, check => check.Name == "integrity" && check.Healthy);
        Assert.Contains(checks, check => check.Name == "sqlite" && check.Healthy);
        Assert.Contains(checks, check => check.Name == "schema" && check.Healthy);
        Assert.Contains(checks, check => check.Name == "vault" && check.Healthy);
        Assert.Contains(checks, check => check.Name == "quarantine" && check.Healthy);
    }

    [Fact]
    public async Task Restore_is_integrity_checked_and_quarantined()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        var spoolDirectory = Path.Combine(context.Directory, "spool");
        Directory.CreateDirectory(spoolDirectory);
        var fixture = Path.Combine(spoolDirectory, "fixture.jpg");
        await File.WriteAllBytesAsync(fixture, [0xff, 0xd8, 0xff, 0xd9]);
        var backup = await context.Maintenance.BackupAsync(Path.Combine(context.Directory, "backups"));
        await File.WriteAllBytesAsync(fixture, [1, 2, 3]);
        await context.Worker.RunOnceAsync();
        Assert.Equal(PublicationState.Published, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        await context.Maintenance.RestoreAsync(backup.Path);
        Assert.True(await context.Store.IsQuarantinedAsync());
        Assert.Equal(PublicationState.Ready, Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).State);
        Assert.Equal(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }, await File.ReadAllBytesAsync(fixture));
        Assert.Equal(0, await context.Worker.RunOnceAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Maintenance.ReleaseAsync());
        var publicationId = Assert.Single((await context.Posts.GetAsync(queued.PostId))!.Publications).Id;
        await context.Maintenance.SuppressAsync(publicationId, "remote state checked by operator");
        await context.Maintenance.ReleaseAsync();
        Assert.False(await context.Store.IsQuarantinedAsync());
        Assert.Equal(0, await context.Worker.RunOnceAsync());
    }

    [Fact]
    public async Task Backup_hash_mismatch_is_rejected_before_restore()
    {
        await using var context = await TestContext.CreateAsync();
        var backup = await context.Maintenance.BackupAsync(Path.Combine(context.Directory, "backups"));
        await using (var append = new FileStream(backup.Path, FileMode.Append, FileAccess.Write, FileShare.None)) await append.WriteAsync(new byte[] { 0x00 });
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Maintenance.RestoreAsync(backup.Path));
    }

    [Fact]
    public async Task Migration_checksum_tampering_is_rejected()
    {
        await using var context = await TestContext.CreateAsync();
        await using (var connection = await context.Database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum='tampered' WHERE version=1";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Database.InitializeAsync());
    }

    [Fact]
    public async Task Newer_database_schema_is_rejected_before_migration()
    {
        await using var context = await TestContext.CreateAsync();
        await using (var connection = await context.Database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Database.InitializeAsync());
    }

    [Fact]
    public async Task Spool_validates_magic_size_and_hash()
    {
        await using var context = await TestContext.CreateAsync();
        var input = Path.Combine(context.Directory, "source.jpg");
        await File.WriteAllBytesAsync(input, [0xff, 0xd8, 0xff, 0x01, 0x02]);
        var spool = new SpoolStore(Path.Combine(context.Directory, "spool"), maximumBytes: 16);
        var asset = await spool.ImportAsync(input);
        Assert.Equal("image/jpeg", asset.DetectedMime);
        Assert.True(await spool.VerifyAsync(asset));
        await using (var append = new FileStream(asset.StorageRef, FileMode.Append, FileAccess.Write, FileShare.None)) await append.WriteAsync(new byte[] { 0x03 });
        Assert.False(await spool.VerifyAsync(asset));

        var unexpected = Path.Combine(context.Directory, "bad.bin");
        await File.WriteAllBytesAsync(unexpected, [1, 2, 3, 4]);
        await Assert.ThrowsAsync<InvalidDataException>(() => spool.ImportAsync(unexpected));
    }

    [Fact]
    public async Task Job_owner_check_rejects_multiple_owners()
    {
        await using var context = await TestContext.CreateAsync();
        await using var connection = await context.Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO jobs(id,kind,publication_id,auth_grant_id,priority,step_key,state,due_at) VALUES($id,'Refresh',$owner,$owner,1,'x','Queued',$due)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$owner", Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$due", context.Time.GetUtcNow().ToString("O"));
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Job_owner_check_rejects_wrong_owner_kind()
    {
        await using var context = await TestContext.CreateAsync();
        var queued = await context.Posts.EnqueueAsync(context.Intent());
        var publicationId = Assert.Single(queued.PublicationIds);
        await using var connection = await context.Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO jobs(id,kind,publication_id,priority,step_key,state,due_at) VALUES($id,'Refresh',$owner,1,'x','Queued',$due)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$owner", publicationId.ToString("D"));
        command.Parameters.AddWithValue("$due", context.Time.GetUtcNow().ToString("O"));
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public void Scheduled_task_has_unlimited_runtime_single_instance_and_absolute_paths()
    {
        var executable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "app dir", "pub.exe"));
        var xml = WindowsTaskScheduler.BuildTaskXml(executable, Path.Combine(Path.GetTempPath(), "data dir"), "S-1-5-21-1");
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml, StringComparison.Ordinal);
        Assert.Contains(executable, xml, StringComparison.Ordinal);
        Assert.Contains("worker run", xml, StringComparison.Ordinal);
    }
}
