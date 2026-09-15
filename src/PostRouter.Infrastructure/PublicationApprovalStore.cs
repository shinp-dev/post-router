using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

public sealed class PublicationApprovalStore(SqliteDatabase database) : IPublicationApprovalStore
{
    private const string ApprovalEvent = "PublicationApproved";
    private const string ApprovalRequiredEvent = "PublicationApprovalRequired";

    public async Task<bool> TryEnterPublicationBoundaryAsync(
        PublicationWorkItem item,
        ProviderStep step,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (step.Effect != StepEffect.MayPublish) return true;

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var info = await ReadInfoAsync(connection, transaction, item.Publication.Id, cancellationToken).ConfigureAwait(false);
        if (info is null) throw new InvalidOperationException("Publication disappeared before approval gate.");
        if (info.Policy == ApprovalPolicy.Automatic ||
            await IsApprovedAsync(connection, transaction, item.Publication.Id, info.IntentHash, cancellationToken).ConfigureAwait(false))
        {
            transaction.Commit();
            return true;
        }

        var currentState = info.State;
        if (currentState is PublicationState.CancelRequested or PublicationState.Cancelled)
        {
            transaction.Commit();
            return true;
        }

        PublicationStateMachine.EnsureCanTransition(currentState, PublicationState.AwaitingApproval);
        var publicationChanged = await SqliteDatabase.ExecuteCountAsync(connection, transaction, """
UPDATE publications SET state='AwaitingApproval',generation=generation+1
WHERE id=$publication AND state=$expected
""", cancellationToken,
            ("$publication", Id(item.Publication.Id)), ("$expected", currentState.ToString())).ConfigureAwait(false);
        if (publicationChanged != 1) throw new InvalidOperationException("Publication changed while entering approval gate.");

        var jobChanged = await SqliteDatabase.ExecuteCountAsync(connection, transaction, """
UPDATE jobs SET state='Blocked',worker_run_id=NULL,claimed_at=NULL,safe_error='approval_required',generation=generation+1
WHERE id=$job AND state='Claimed'
""", cancellationToken, ("$job", Id(item.Job.Id))).ConfigureAwait(false);
        if (jobChanged != 1) throw new InvalidOperationException("Publication job changed while entering approval gate.");

        await SqliteDatabase.ExecuteAsync(connection, transaction, """
INSERT INTO audit_events(id,kind,subject_type,subject_id,reason,created_at)
VALUES($id,$kind,'Publication',$publication,$reason,$at)
""", cancellationToken,
            ("$id", Id(Guid.NewGuid())), ("$kind", ApprovalRequiredEvent),
            ("$publication", Id(item.Publication.Id)), ("$reason", Reason(info.IntentHash)), ("$at", At(now))).ConfigureAwait(false);
        transaction.Commit();
        return false;
    }

    public async Task<PublicationApprovalStatus> GetStatusAsync(Guid publicationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var info = await ReadInfoAsync(connection, null, publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Publication not found.");
        if (info.Policy == ApprovalPolicy.Automatic)
            return new(ApprovalPolicy.Automatic, true, null, false);

        var approvedAt = await ApprovedAtAsync(connection, null, publicationId, info.IntentHash, cancellationToken).ConfigureAwait(false);
        var canApprove = approvedAt is null && info.State is
            PublicationState.Pending or PublicationState.Preparing or PublicationState.Ready or PublicationState.AwaitingApproval;
        return new(info.Policy, approvedAt is not null, approvedAt, canApprove);
    }

    public async Task<bool> ApproveAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var info = await ReadInfoAsync(connection, transaction, publicationId, cancellationToken).ConfigureAwait(false);
        if (info is null || info.Policy != ApprovalPolicy.RequireApproval)
        {
            transaction.Commit();
            return false;
        }
        if (info.State is not (PublicationState.Pending or PublicationState.Preparing or PublicationState.Ready or PublicationState.AwaitingApproval))
        {
            transaction.Commit();
            return false;
        }
        if (await IsApprovedAsync(connection, transaction, publicationId, info.IntentHash, cancellationToken).ConfigureAwait(false))
        {
            transaction.Commit();
            return true;
        }

        await SqliteDatabase.ExecuteAsync(connection, transaction, """
INSERT INTO audit_events(id,kind,subject_type,subject_id,reason,created_at)
VALUES($id,$kind,'Publication',$publication,$reason,$at)
""", cancellationToken,
            ("$id", Id(Guid.NewGuid())), ("$kind", ApprovalEvent), ("$publication", Id(publicationId)),
            ("$reason", Reason(info.IntentHash)), ("$at", At(now))).ConfigureAwait(false);

        if (info.State == PublicationState.AwaitingApproval)
        {
            PublicationStateMachine.EnsureCanTransition(PublicationState.AwaitingApproval, PublicationState.Ready);
            await SqliteDatabase.ExecuteAsync(connection, transaction, """
UPDATE publications SET state='Ready',safe_error=NULL,generation=generation+1
WHERE id=$publication AND state='AwaitingApproval'
""", cancellationToken, ("$publication", Id(publicationId))).ConfigureAwait(false);
            await SqliteDatabase.ExecuteAsync(connection, transaction, """
UPDATE jobs SET state='Queued',due_at=CASE WHEN due_at<$now THEN $now ELSE due_at END,
  worker_run_id=NULL,claimed_at=NULL,safe_error=NULL,generation=generation+1
WHERE publication_id=$publication AND kind='Publish' AND state='Blocked' AND safe_error='approval_required'
""", cancellationToken, ("$publication", Id(publicationId)), ("$now", At(now))).ConfigureAwait(false);
        }

        transaction.Commit();
        return true;
    }

    public async Task<int> CancelAwaitingApprovalForPostAsync(Guid postId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var publicationIds = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM publications WHERE post_id=$post AND state='AwaitingApproval'";
            command.Parameters.AddWithValue("$post", Id(postId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) publicationIds.Add(Guid.Parse(reader.GetString(0)));
        }
        foreach (var publicationId in publicationIds)
        {
            PublicationStateMachine.EnsureCanTransition(PublicationState.AwaitingApproval, PublicationState.Cancelled);
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "UPDATE publications SET state='Cancelled',safe_error='cancelled_before_approval',generation=generation+1 WHERE id=$id AND state='AwaitingApproval'",
                cancellationToken, ("$id", Id(publicationId))).ConfigureAwait(false);
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "UPDATE jobs SET state='Cancelled',generation=generation+1 WHERE publication_id=$id AND state IN ('Queued','Claimed','Blocked')",
                cancellationToken, ("$id", Id(publicationId))).ConfigureAwait(false);
            await SqliteDatabase.ExecuteAsync(connection, transaction, """
INSERT INTO audit_events(id,kind,subject_type,subject_id,reason,created_at)
VALUES($event,'PublicationApprovalCancelled','Publication',$publication,'cancelled_while_awaiting_approval',$at)
""", cancellationToken,
                ("$event", Id(Guid.NewGuid())), ("$publication", Id(publicationId)), ("$at", At(now))).ConfigureAwait(false);
        }
        transaction.Commit();
        return publicationIds.Count;
    }

    public async Task<int> CountAwaitingApprovalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var count = await SqliteDatabase.ScalarAsync<long>(connection, null,
            "SELECT COUNT(*) FROM publications WHERE state='AwaitingApproval'", cancellationToken).ConfigureAwait(false);
        return checked((int)count);
    }

    private static async Task<ApprovalInfo?> ReadInfoAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT p.account_id,p.state,post.intent_hash,post.canonical_intent
FROM publications p JOIN posts post ON post.id=p.post_id
WHERE p.id=$publication
""";
        command.Parameters.AddWithValue("$publication", Id(publicationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var accountId = Guid.Parse(reader.GetString(0));
        var state = Enum.Parse<PublicationState>(reader.GetString(1));
        var hash = reader.GetString(2);
        var canonical = (byte[])reader[3];
        return new(accountId, state, hash, ReadPolicy(canonical, accountId));
    }

    private static ApprovalPolicy ReadPolicy(byte[] canonicalIntent, Guid accountId)
    {
        using var document = JsonDocument.Parse(canonicalIntent);
        if (!document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
            return ApprovalPolicy.Automatic;
        foreach (var target in targets.EnumerateArray())
        {
            if (!target.TryGetProperty("accountId", out var accountValue) ||
                !Guid.TryParse(accountValue.GetString(), out var candidate) || candidate != accountId) continue;
            if (!target.TryGetProperty("approvalPolicy", out var policyValue)) return ApprovalPolicy.Automatic;
            return Enum.TryParse<ApprovalPolicy>(policyValue.GetString(), out var policy) ? policy : ApprovalPolicy.Automatic;
        }
        return ApprovalPolicy.Automatic;
    }

    private static async Task<bool> IsApprovedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid publicationId,
        string intentHash,
        CancellationToken cancellationToken) =>
        await ApprovedAtAsync(connection, transaction, publicationId, intentHash, cancellationToken).ConfigureAwait(false) is not null;

    private static async Task<DateTimeOffset?> ApprovedAtAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid publicationId,
        string intentHash,
        CancellationToken cancellationToken)
    {
        var value = await SqliteDatabase.ScalarAsync<string?>(connection, transaction, """
SELECT created_at FROM audit_events
WHERE kind=$kind AND subject_type='Publication' AND subject_id=$publication AND reason=$reason
ORDER BY created_at DESC LIMIT 1
""", cancellationToken,
            ("$kind", ApprovalEvent), ("$publication", Id(publicationId)), ("$reason", Reason(intentHash))).ConfigureAwait(false);
        return value is null ? null : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static string Reason(string intentHash) => $"intent:{intentHash}";
    private static string Id(Guid id) => id.ToString("D");
    private static string At(DateTimeOffset value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ApprovalInfo(Guid AccountId, PublicationState State, string IntentHash, ApprovalPolicy Policy);
}
