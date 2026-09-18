using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class PublicMediaOperationsTests
{
    [Fact]
    public async Task Successful_stage_removes_only_its_temporary_copy_and_keeps_path_free_metadata()
    {
        using var fixture = new Fixture();
        var sharedSpool = Path.Combine(fixture.Directory, "spool");
        Directory.CreateDirectory(sharedSpool);
        var source = fixture.CreateImage(Path.Combine(sharedSpool, "shared-source.jpg"));
        var host = new FakeHost(fixture.Journal);
        var result = await fixture.Operations.StageAsync(source, host);

        Assert.Equal("Staged", result.Status);
        Assert.NotNull(result.PublicUrl);
        Assert.True(host.JournalExistedBeforeUpload);
        Assert.True(File.Exists(source)); // A different provider may still use the shared spool.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Directory, "media-staging-payload")));
        var record = (await fixture.Journal.ReadAsync(result.Id))!;
        Assert.NotNull(record.Staged);
        Assert.Equal(7, record.SizeBytes);
        Assert.Equal("image/jpeg", record.MimeType);
        Assert.Equal(64, record.Sha256.Length);
        Assert.True(record.UpdatedAt >= record.CreatedAt);
        var json = await File.ReadAllTextAsync(fixture.RecordPath(result.Id));
        Assert.DoesNotContain(source, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StorageRef", json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await fixture.Operations.ListAsync());
    }

    [Fact]
    public async Task Pending_upload_can_be_recovered_after_restart_without_payload_or_second_upload_then_deleted()
    {
        using var fixture = new Fixture();
        var source = fixture.CreateImage();
        var host = new FakeHost(fixture.Journal) { LoseUploadResponse = true };
        var pending = await fixture.Operations.StageAsync(source, host);

        Assert.Equal("Pending", pending.Status);
        Assert.Equal("github_upload_outcome_unknown", pending.LastErrorCode);
        Assert.Null(pending.PublicUrl);
        Assert.Equal(1, host.UploadCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Directory, "media-staging-payload")));
        Assert.NotNull(await fixture.Journal.ReadAsync(pending.Id));

        var restarted = new PublicMediaOperations(new TemporaryPublicMediaPayloadStore(fixture.Directory),
            new FilePublicMediaOperationStore(fixture.Directory));
        var recovered = await restarted.RecoverAsync(pending.Id, host);
        Assert.Equal("Staged", recovered.Status);
        Assert.Equal(1, host.UploadCalls);
        Assert.Equal(1, host.RecoverCalls);
        Assert.Equal(recovered.PublicUrl, (await restarted.GetAsync(pending.Id)).PublicUrl);

        var deleted = await restarted.DeleteAsync(pending.Id, host);
        Assert.Equal("Deleted", deleted.Status);
        Assert.Null(deleted.PublicUrl);
        Assert.Equal(1, host.DeleteCalls);
        Assert.Null(await fixture.Journal.ReadAsync(pending.Id));
        Assert.False(File.Exists(fixture.RecordPath(pending.Id)));
        Assert.Empty(await restarted.ListAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => restarted.GetAsync(pending.Id));
        Assert.True(File.Exists(source)); // The caller's original input is not owned by staging.
    }

    [Fact]
    public async Task Recover_only_reads_remote_when_upload_is_not_found()
    {
        using var fixture = new Fixture();
        var host = new FakeHost(fixture.Journal) { FailBeforeUpload = true };
        var pending = await fixture.Operations.StageAsync(fixture.CreateImage(), host);
        Assert.Equal("Pending", pending.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Directory, "media-staging-payload")));
        var stillPending = await fixture.Operations.RecoverAsync(pending.Id, host);
        Assert.Equal("Pending", stillPending.Status);
        Assert.Equal("github_asset_not_found", stillPending.LastErrorCode);
        Assert.Equal(1, host.UploadCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.DeleteAsync(pending.Id, host));
        Assert.Equal(0, host.DeleteCalls);
        Assert.NotNull(await fixture.Journal.ReadAsync(pending.Id));
    }

    [Fact]
    public async Task Already_missing_remote_asset_allows_metadata_removal()
    {
        using var fixture = new Fixture();
        var host = new FakeHost(fixture.Journal);
        var staged = await fixture.Operations.StageAsync(fixture.CreateImage(), host);
        host.RemoteAlreadyMissing = true; // The host treats a confirmed GET/DELETE 404 as absent.
        await fixture.Operations.DeleteAsync(staged.Id, host);
        Assert.Null(await fixture.Journal.ReadAsync(staged.Id));
        Assert.Empty(await fixture.Operations.ListAsync());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("network")]
    [InlineData("server")]
    [InlineData("response_loss")]
    public async Task Uncertain_delete_preserves_remote_handle_and_metadata_for_retry(string failure)
    {
        using var fixture = new Fixture();
        var host = new FakeHost(fixture.Journal);
        var staged = await fixture.Operations.StageAsync(fixture.CreateImage(), host);
        var before = (await fixture.Journal.ReadAsync(staged.Id))!;
        host.DeleteFailure = failure;
        await Assert.ThrowsAsync<TemporaryPublicMediaException>(
            () => fixture.Operations.DeleteAsync(staged.Id, host));
        var after = (await fixture.Journal.ReadAsync(staged.Id))!;
        Assert.Equal(before.Staged!.Handle, after.Staged!.Handle);
        Assert.Equal(before.Staged.PublicUrl, after.Staged.PublicUrl);
        Assert.Equal(before.Operation, after.Operation);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Single(await fixture.Operations.ListAsync());
        host.DeleteFailure = null;
        await fixture.Operations.DeleteAsync(staged.Id, host);
        Assert.Empty(await fixture.Operations.ListAsync());
    }

    [Fact]
    public async Task Failed_payload_cleanup_is_reported_and_retryable_without_losing_remote_handle()
    {
        using var fixture = new Fixture();
        var payloads = new FailingCleanupPayloadStore(fixture.Directory);
        var operations = new PublicMediaOperations(payloads, fixture.Journal);
        var host = new FakeHost(fixture.Journal);
        var staged = await operations.StageAsync(fixture.CreateImage(), host);
        Assert.Equal("Staged", staged.Status);
        Assert.Equal("media_payload_cleanup_failed", staged.LastErrorCode);
        Assert.NotNull((await fixture.Journal.ReadAsync(staged.Id))!.Staged);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Directory, "media-staging-payload")));
        payloads.FailCleanup = false;
        var recovered = await operations.RecoverAsync(staged.Id, host);
        Assert.Null(recovered.LastErrorCode);
        Assert.Equal(1, host.UploadCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Directory, "media-staging-payload")));
        await operations.DeleteAsync(staged.Id, host);
        Assert.Null(await fixture.Journal.ReadAsync(staged.Id));
    }

    [Fact]
    public async Task Pending_recovery_keeps_cleanup_warning_until_temporary_copy_is_removed()
    {
        using var fixture = new Fixture();
        var payloads = new FailingCleanupPayloadStore(fixture.Directory);
        var operations = new PublicMediaOperations(payloads, fixture.Journal);
        var host = new FakeHost(fixture.Journal) { LoseUploadResponse = true };
        var pending = await operations.StageAsync(fixture.CreateImage(), host);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal("media_payload_cleanup_failed", pending.LastErrorCode);
        var staged = await operations.RecoverAsync(pending.Id, host);
        Assert.Equal("Staged", staged.Status);
        Assert.Equal("media_payload_cleanup_failed", staged.LastErrorCode);
        Assert.Equal(1, host.UploadCalls);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Directory, "media-staging-payload")));
        await Assert.ThrowsAsync<IOException>(() => operations.DeleteAsync(pending.Id, host));
        Assert.NotNull(await fixture.Journal.ReadAsync(pending.Id));
        payloads.FailCleanup = false;
        await operations.DeleteAsync(pending.Id, host);
        Assert.Null(await fixture.Journal.ReadAsync(pending.Id));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Directory, "media-staging-payload")));
    }

    [Fact]
    public async Task Failed_local_record_removal_keeps_complete_handle_for_confirmed_absence_retry()
    {
        using var fixture = new Fixture();
        var journal = new FailingRecordRemovalStore(fixture.Journal);
        var operations = new PublicMediaOperations(new TemporaryPublicMediaPayloadStore(fixture.Directory), journal);
        var host = new FakeHost(fixture.Journal);
        var staged = await operations.StageAsync(fixture.CreateImage(), host);
        var before = await File.ReadAllTextAsync(fixture.RecordPath(staged.Id));
        await Assert.ThrowsAsync<IOException>(() => operations.DeleteAsync(staged.Id, host));
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.RecordPath(staged.Id)));
        Assert.NotNull((await fixture.Journal.ReadAsync(staged.Id))!.Staged?.Handle);
        journal.FailRemoval = false;
        host.RemoteAlreadyMissing = true;
        await operations.DeleteAsync(staged.Id, host);
        Assert.False(File.Exists(fixture.RecordPath(staged.Id)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "post-router-media-operation-tests",
            Guid.NewGuid().ToString("N"));
        public FilePublicMediaOperationStore Journal { get; }
        public PublicMediaOperations Operations { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Journal = new FilePublicMediaOperationStore(Directory);
            Operations = new PublicMediaOperations(new TemporaryPublicMediaPayloadStore(Directory), Journal);
        }

        public string CreateImage(string? path = null)
        {
            path ??= Path.Combine(Directory, "private-name.jpg");
            File.WriteAllBytes(path, [0xff, 0xd8, 0xff, 1, 2, 3, 4]);
            return path;
        }

        public string RecordPath(Guid id) => Path.Combine(Directory, "github-media-operations", $"{id:N}.json");
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class FailingCleanupPayloadStore(string directory) : ITemporaryPublicMediaPayloadStore
    {
        private readonly TemporaryPublicMediaPayloadStore _inner = new(directory);
        public bool FailCleanup { get; set; } = true;
        public Task<MediaAsset> ImportAsync(Guid id, string path, CancellationToken cancellationToken = default) =>
            _inner.ImportAsync(id, path, cancellationToken);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            FailCleanup ? Task.FromException(new IOException("private local path")) : _inner.DeleteAsync(id, cancellationToken);
    }

    private sealed class FailingRecordRemovalStore(IPublicMediaOperationStore inner) : IPublicMediaOperationStore
    {
        public bool FailRemoval { get; set; } = true;
        public ValueTask<IAsyncDisposable> AcquireAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.AcquireAsync(id, cancellationToken);
        public Task<PublicMediaOperationRecord?> ReadAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(id, cancellationToken);
        public Task<IReadOnlyList<PublicMediaOperationRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken);
        public Task SaveAsync(PublicMediaOperationRecord record, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(record, cancellationToken);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            FailRemoval ? Task.FromException(new IOException("private local path")) : inner.DeleteAsync(id, cancellationToken);
    }

    private sealed class FakeHost(IPublicMediaOperationStore journal) : ITemporaryPublicMediaHost
    {
        private StagedPublicAsset? _remote;
        public bool LoseUploadResponse { get; init; }
        public bool FailBeforeUpload { get; init; }
        public bool RemoteAlreadyMissing { get; set; }
        public string? DeleteFailure { get; set; }
        public bool JournalExistedBeforeUpload { get; private set; }
        public int UploadCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public PublicMediaStagingOperation Prepare(MediaAsset asset, DateTimeOffset? expiresAt = null) =>
            new(Guid.NewGuid().ToString("N"));

        public async Task<StagedPublicAsset> StageAsync(MediaAsset asset, PublicMediaStagingOperation operation,
            CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            var record = Assert.Single(await journal.ListAsync(cancellationToken));
            JournalExistedBeforeUpload = record.Operation == operation && record.Staged is null;
            if (FailBeforeUpload) throw new TemporaryPublicMediaException("github_request_rejected");
            _remote = new StagedPublicAsset(
                new Uri($"https://github.com/example/media/releases/download/staging/{operation.Value}.jpg"),
                new StagedPublicAssetHandle(operation.Value), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(24), asset.Sha256, asset.SizeBytes);
            if (LoseUploadResponse) throw new TemporaryPublicMediaException("github_upload_outcome_unknown");
            return _remote;
        }

        public Task<StagedPublicAsset?> RecoverAsync(PublicMediaStagingOperation operation,
            CancellationToken cancellationToken = default)
        {
            RecoverCalls++;
            return Task.FromResult(_remote);
        }

        public Task DeleteAsync(StagedPublicAssetHandle handle, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (DeleteFailure is not null) throw new TemporaryPublicMediaException("github_" + DeleteFailure);
            if (!RemoteAlreadyMissing) _remote = null;
            return Task.CompletedTask;
        }
    }
}
