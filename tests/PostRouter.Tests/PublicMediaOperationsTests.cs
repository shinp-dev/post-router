using System.Text.Json;
using PostRouter.Application;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Tests;

public sealed class PublicMediaOperationsTests
{
    [Fact]
    public async Task Pending_upload_can_be_recovered_after_restart_without_a_second_upload_then_deleted_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-media-operation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "private-name.jpg");
        await File.WriteAllBytesAsync(source, [0xff, 0xd8, 0xff, 1, 2, 3, 4]);
        try
        {
            var journal = new FilePublicMediaOperationStore(directory);
            var spool = new SpoolStore(Path.Combine(directory, "spool"));
            var host = new FakeHost(journal) { LoseUploadResponse = true };
            var operations = new PublicMediaOperations(spool, journal);
            var pending = await operations.StageAsync(source, host);
            Assert.Equal("Pending", pending.Status);
            Assert.Equal("github_upload_outcome_unknown", pending.LastErrorCode);
            Assert.Null(pending.PublicUrl);
            Assert.Equal(1, host.UploadCalls);
            Assert.True(host.JournalExistedBeforeUpload);
            Assert.DoesNotContain(source, JsonSerializer.Serialize(pending), StringComparison.Ordinal);
            Assert.Single(await operations.ListAsync());

            var restarted = new PublicMediaOperations(spool, new FilePublicMediaOperationStore(directory));
            var recovered = await restarted.RecoverAsync(pending.Id, host);
            Assert.Equal("Staged", recovered.Status);
            Assert.NotNull(recovered.PublicUrl);
            Assert.Equal(1, host.UploadCalls);
            Assert.Equal(1, host.RecoverCalls);
            Assert.Equal(recovered.PublicUrl, (await restarted.GetAsync(pending.Id)).PublicUrl);

            var deleted = await restarted.DeleteAsync(pending.Id, host);
            Assert.Equal("Deleted", deleted.Status);
            Assert.Null(deleted.PublicUrl);
            Assert.Equal(1, host.DeleteCalls);
            _ = await restarted.DeleteAsync(pending.Id, host);
            Assert.Equal(1, host.DeleteCalls);
            Assert.True(File.Exists(source));
            Assert.True(File.Exists((await journal.ReadAsync(pending.Id))!.Source.StorageRef));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Recover_only_reads_remote_when_upload_is_not_found()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-router-media-operation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.jpg");
        await File.WriteAllBytesAsync(source, [0xff, 0xd8, 0xff, 1]);
        try
        {
            var journal = new FilePublicMediaOperationStore(directory);
            var host = new FakeHost(journal) { FailBeforeUpload = true };
            var operations = new PublicMediaOperations(new SpoolStore(Path.Combine(directory, "spool")), journal);
            var pending = await operations.StageAsync(source, host);
            Assert.Equal("Pending", pending.Status);
            Assert.Equal(1, host.UploadCalls);
            var stillPending = await operations.RecoverAsync(pending.Id, host);
            Assert.Equal("Pending", stillPending.Status);
            Assert.Equal("github_asset_not_found", stillPending.LastErrorCode);
            Assert.Equal(1, host.UploadCalls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.DeleteAsync(pending.Id, host));
            Assert.Equal(0, host.DeleteCalls);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FakeHost(IPublicMediaOperationStore journal) : ITemporaryPublicMediaHost
    {
        private StagedPublicAsset? _remote;
        public bool LoseUploadResponse { get; init; }
        public bool FailBeforeUpload { get; init; }
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

        public Task<StagedPublicAsset?> RecoverAsync(PublicMediaStagingOperation operation, CancellationToken cancellationToken = default)
        {
            RecoverCalls++;
            return Task.FromResult(_remote);
        }

        public Task DeleteAsync(StagedPublicAssetHandle handle, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            _remote = null;
            return Task.CompletedTask;
        }
    }
}
