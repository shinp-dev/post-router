using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostRouter.Application;
using PostRouter.Domain;

namespace PostRouter.Infrastructure;

public sealed class SqliteStore(SqliteDatabase database, ISecretProtector protector) : IPostRouterStore, IVault
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => database.InitializeAsync(cancellationToken);

    public async Task<EnqueueResult> EnqueueAsync(CanonicalPostIntent intent, byte[] canonicalBytes, string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id,canonical_intent FROM posts WHERE client_request_id=$key";
            existing.Parameters.AddWithValue("$key", intent.ClientRequestId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var postId = Guid.Parse(reader.GetString(0));
                var saved = (byte[])reader[1];
                if (!saved.AsSpan().SequenceEqual(canonicalBytes)) throw new InvalidOperationException("Idempotency key conflicts with a different intent.");
                await reader.DisposeAsync();
                var ids = await PublicationIdsAsync(connection, transaction, postId, cancellationToken);
                transaction.Commit();
                return new(postId, true, ids);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var post = Guid.NewGuid();
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "INSERT INTO contents(id,kind,text,title) VALUES($id,$kind,$text,$title)", cancellationToken,
            ("$id", Id(intent.Content.Id)), ("$kind", intent.Content.Kind.ToString()), ("$text", intent.Content.Text), ("$title", intent.Content.Title));
        for (var ordinal = 0; ordinal < intent.Content.MediaAssets.Count; ordinal++)
        {
            var media = intent.Content.MediaAssets[ordinal];
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO media_assets(id,content_id,ordinal,sha256,size_bytes,detected_mime,storage_ref,duration_ticks,width,height) VALUES($id,$content,$ordinal,$hash,$size,$mime,$storage,$duration,$width,$height)", cancellationToken,
                ("$id", Id(media.Id)), ("$content", Id(intent.Content.Id)), ("$ordinal", ordinal), ("$hash", media.Sha256), ("$size", media.SizeBytes),
                ("$mime", media.DetectedMime), ("$storage", media.StorageRef), ("$duration", media.Duration?.Ticks), ("$width", media.Width), ("$height", media.Height));
        }
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "INSERT INTO posts(id,client_request_id,intent_hash,canonical_intent,content_id,created_at) VALUES($id,$key,$hash,$bytes,$content,$at)", cancellationToken,
            ("$id", Id(post)), ("$key", intent.ClientRequestId), ("$hash", hash), ("$bytes", canonicalBytes), ("$content", Id(intent.Content.Id)), ("$at", At(now)));

        var publications = new List<Guid>();
        foreach (var targetIntent in intent.Targets)
        {
            var target = Guid.NewGuid();
            var schedule = Guid.NewGuid();
            var publication = Guid.NewGuid();
            var job = Guid.NewGuid();
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO accounts(id,provider_key,alias,status) VALUES($id,$provider,$alias,'Ready') ON CONFLICT(id) DO NOTHING", cancellationToken,
                ("$id", Id(targetIntent.AccountId)), ("$provider", targetIntent.ProviderKey), ("$alias", targetIntent.AccountAlias ?? targetIntent.AccountId.ToString("D")));
            var savedProvider = await SqliteDatabase.ScalarAsync<string>(connection, transaction, "SELECT provider_key FROM accounts WHERE id=$id", cancellationToken, ("$id", Id(targetIntent.AccountId)));
            if (!string.Equals(savedProvider, targetIntent.ProviderKey, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Account identity conflicts with its provider.");
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO targets(id,post_id,account_id,provider_key,visibility,options_schema,options_version,options_json) VALUES($id,$post,$account,$provider,$visibility,$schema,$version,$json)", cancellationToken,
                ("$id", Id(target)), ("$post", Id(post)), ("$account", Id(targetIntent.AccountId)), ("$provider", targetIntent.ProviderKey),
                ("$visibility", targetIntent.Visibility), ("$schema", targetIntent.OptionsSchema), ("$version", targetIntent.OptionsVersion), ("$json", targetIntent.CanonicalOptionsJson));
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO schedules(id,mode,due_at_utc,max_lateness_seconds,original_local,zone_id,selected_offset_seconds,zone_rules_fingerprint) VALUES($id,$mode,$due,$late,$local,$zone,$offset,$fingerprint)", cancellationToken,
                ("$id", Id(schedule)), ("$mode", intent.Schedule.Mode.ToString()), ("$due", At(intent.Schedule.DueAtUtc)),
                ("$late", checked((long)intent.Schedule.MaxLateness.TotalSeconds)), ("$local", intent.Schedule.RequestedLocalTime),
                ("$zone", intent.Schedule.ZoneId), ("$offset", intent.Schedule.SelectedOffset is null ? null : checked((long)intent.Schedule.SelectedOffset.Value.TotalSeconds)),
                ("$fingerprint", intent.Schedule.ZoneRulesFingerprint));
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO publications(id,post_id,target_id,account_id,provider_key,schedule_id,state,execution_mode,created_at) VALUES($id,$post,$target,$account,$provider,$schedule,'Ready','Local',$at)", cancellationToken,
                ("$id", Id(publication)), ("$post", Id(post)), ("$target", Id(target)), ("$account", Id(targetIntent.AccountId)),
                ("$provider", targetIntent.ProviderKey), ("$schedule", Id(schedule)), ("$at", At(now)));
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO jobs(id,kind,publication_id,priority,step_key,state,due_at) VALUES($id,'Publish',$publication,100,'initial','Queued',$due)", cancellationToken,
                ("$id", Id(job)), ("$publication", Id(publication)), ("$due", At(intent.Schedule.DueAtUtc)));
            publications.Add(publication);
        }
        transaction.Commit();
        return new(post, false, publications);
    }

    public async Task<PostSummary?> GetPostAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        string? key = null;
        DateTimeOffset created = default;
        await using (var post = connection.CreateCommand())
        {
            post.CommandText = "SELECT client_request_id,created_at FROM posts WHERE id=$id";
            post.Parameters.AddWithValue("$id", Id(postId));
            await using var reader = await post.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            key = reader.GetString(0);
            created = ParseAt(reader.GetString(1));
        }
        var publications = new List<Publication>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT p.id,p.target_id,p.account_id,p.provider_key,p.state,p.execution_mode,s.due_at_utc,s.max_lateness_seconds,p.created_at,p.first_submitted_at,p.confirmed_at,p.published_at,p.safe_error,p.generation
FROM publications p JOIN schedules s ON s.id=p.schedule_id WHERE p.post_id=$id ORDER BY p.created_at,p.id
""";
            command.Parameters.AddWithValue("$id", Id(postId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) publications.Add(ReadPublication(reader, postId));
        }
        var remoteObjects = new List<RemoteObjectSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT publication_id,kind,provider_object_id,observed_at FROM remote_objects WHERE publication_id IN (SELECT id FROM publications WHERE post_id=$id) ORDER BY observed_at,id";
            command.Parameters.AddWithValue("$id", Id(postId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) remoteObjects.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ParseAt(reader.GetString(3))));
        }
        return new(postId, key, created, publications, remoteObjects);
    }

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,provider_key,alias,status FROM accounts ORDER BY provider_key,alias";
        var accounts = new List<Account>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) accounts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return accounts;
    }

    public async Task<IReadOnlyList<AccountConnection>> GetAccountConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT a.id,a.provider_key,a.alias,a.status,a.remote_subject,a.display_name,a.client_id,a.scope,g.expires_at,
  COALESCE(
    (SELECT p.safe_error FROM publications p
     WHERE p.account_id=a.id AND p.state='NeedsAttention' AND p.failure_category='Authentication'
     ORDER BY p.created_at DESC LIMIT 1),
    CASE WHEN a.status='Disconnected' AND EXISTS(
      SELECT 1 FROM jobs pending JOIN publications publication ON publication.id=pending.publication_id
      WHERE publication.account_id=a.id AND pending.state IN ('Queued','Claimed','Blocked')) THEN 'auth_disconnected' END)
FROM accounts a LEFT JOIN auth_grants g ON g.id=a.auth_grant_id
ORDER BY a.provider_key,a.alias
""";
        var accounts = new List<AccountConnection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            accounts.Add(new(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? reader.GetString(2) : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? null : ParseAt(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        return accounts;
    }

    public async Task<DashboardSummary> GetDashboardAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT
  SUM(CASE WHEN p.state='ScheduledRemote' OR (p.state IN ('Pending','Preparing','Ready') AND s.mode='AtTime' AND s.due_at_utc>$now) THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state IN ('Pending','Preparing','Ready') AND NOT (s.mode='AtTime' AND s.due_at_utc>$now) THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state IN ('Publishing','Processing','CancelRequested','AwaitingUser') THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state='Published' THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state='Failed' THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state='NeedsAttention' THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state='Unknown' THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state='Cancelled' THEN 1 ELSE 0 END),
  SUM(CASE WHEN p.state='Expired' THEN 1 ELSE 0 END),
  COALESCE(SUM(CASE WHEN p.state='NeedsAttention' AND p.failure_category='Authentication' THEN 1 ELSE 0 END),0)
    + (SELECT COUNT(*) FROM accounts account WHERE account.status='Disconnected' AND EXISTS(
      SELECT 1 FROM jobs pending JOIN publications publication ON publication.id=pending.publication_id
      WHERE publication.account_id=account.id AND pending.state IN ('Queued','Claimed','Blocked')))
FROM publications p JOIN schedules s ON s.id=p.schedule_id
""";
        command.Parameters.AddWithValue("$now", At(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        _ = await reader.ReadAsync(cancellationToken);
        return new(
            ReadCount(reader, 0), ReadCount(reader, 1), ReadCount(reader, 2), ReadCount(reader, 3),
            ReadCount(reader, 4), ReadCount(reader, 5), ReadCount(reader, 6), ReadCount(reader, 7),
            ReadCount(reader, 8), ReadCount(reader, 9));
    }

    public async Task<IReadOnlyList<PublicationListItem>> GetPublicationsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = BuildPublicationQuery(connection, "", "LIMIT $limit");
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<PublicationListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPublicationListItem(reader));
        return result;
    }

    public async Task<PublicationDetail?> GetPublicationDetailAsync(Guid publicationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = BuildPublicationQuery(connection, "WHERE p.id=$publication", "");
        command.Parameters.AddWithValue("$publication", Id(publicationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var summary = ReadPublicationListItem(reader);
        var text = reader.IsDBNull(17) ? null : reader.GetString(17);
        var visibility = reader.GetString(18);
        var optionsSchema = reader.GetString(19);
        var submitted = NullableAt(reader, 20);
        var confirmed = NullableAt(reader, 21);
        var providerError = reader.IsDBNull(22) ? null : reader.GetString(22);
        var reconcileQueued = reader.GetInt64(23) != 0;
        var retrySafe = reader.GetInt64(24) != 0 && summary.RemoteId is null && reader.IsDBNull(26);
        FailureCategory? normalizedError = reader.IsDBNull(25) ? null : Enum.Parse<FailureCategory>(reader.GetString(25));
        return new(summary, text, visibility, optionsSchema, submitted, confirmed, providerError,
            normalizedError, reconcileQueued, PublicationStateMachine.CanCancel(summary.PublicationState),
            PublicationStateMachine.CanRetry(summary.PublicationState) && retrySafe,
            PublicationStateMachine.CanReconcile(summary.PublicationState),
            reader.IsDBNull(26) ? null : reader.GetString(26));
    }

    public async Task<bool> RequestRetryAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var stateValue = await SqliteDatabase.ScalarAsync<string?>(connection, transaction,
            "SELECT state FROM publications WHERE id=$id", cancellationToken, ("$id", Id(publicationId)));
        if (stateValue is null) { transaction.Commit(); return false; }
        var state = Enum.Parse<PublicationState>(stateValue);
        if (!PublicationStateMachine.CanRetry(state)) { transaction.Commit(); return false; }
        var safeAttempt = await SqliteDatabase.ScalarAsync<long>(connection, transaction, """
SELECT COUNT(*) FROM attempts a JOIN jobs j ON j.id=a.job_id
WHERE j.publication_id=$id AND j.kind='Publish' AND a.effect_certainty IN ('NotSent','NoSideEffect')
  AND a.started_at=(SELECT MAX(a2.started_at) FROM attempts a2 JOIN jobs j2 ON j2.id=a2.job_id WHERE j2.publication_id=$id AND j2.kind='Publish')
""", cancellationToken, ("$id", Id(publicationId))) == 1;
        var ambiguousAttempt = await SqliteDatabase.ScalarAsync<long>(connection, transaction, """
SELECT COUNT(*) FROM attempts a JOIN jobs j ON j.id=a.job_id
WHERE j.publication_id=$id AND j.kind='Publish' AND a.effect_certainty='Ambiguous'
""", cancellationToken, ("$id", Id(publicationId))) != 0;
        var remoteExists = await SqliteDatabase.ScalarAsync<long>(connection, transaction,
            "SELECT COUNT(*) FROM remote_objects WHERE publication_id=$id", cancellationToken, ("$id", Id(publicationId))) != 0;
        var activeExists = await SqliteDatabase.ScalarAsync<long>(connection, transaction,
            "SELECT COUNT(*) FROM jobs WHERE publication_id=$id AND state IN ('Queued','Claimed','Blocked')", cancellationToken, ("$id", Id(publicationId))) != 0;
        if (!safeAttempt || ambiguousAttempt || remoteExists || activeExists) { transaction.Commit(); return false; }
        if (state == PublicationState.NeedsAttention)
        {
            PublicationStateMachine.EnsureCanTransition(state, PublicationState.Failed);
            state = PublicationState.Failed;
        }
        PublicationStateMachine.EnsureCanTransition(state, PublicationState.Pending);
        PublicationStateMachine.EnsureCanTransition(PublicationState.Pending, PublicationState.Ready);
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "UPDATE publications SET state='Ready',safe_error=NULL,failure_category=NULL,generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(publicationId)));
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "INSERT INTO jobs(id,kind,publication_id,priority,step_key,state,due_at) VALUES($job,'Publish',$publication,100,'manual-retry','Queued',$due)", cancellationToken,
            ("$job", Id(Guid.NewGuid())), ("$publication", Id(publicationId)), ("$due", At(now)));
        transaction.Commit();
        return true;
    }

    public async Task<bool> RequestReconcileAsync(Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var stateValue = await SqliteDatabase.ScalarAsync<string?>(connection, transaction,
            "SELECT state FROM publications WHERE id=$id", cancellationToken, ("$id", Id(publicationId)));
        if (stateValue is null || !PublicationStateMachine.CanReconcile(Enum.Parse<PublicationState>(stateValue)))
        {
            transaction.Commit();
            return false;
        }
        var updated = await SqliteDatabase.ExecuteCountAsync(connection, transaction,
            "UPDATE jobs SET due_at=$due,safe_error=NULL,generation=generation+1 WHERE publication_id=$id AND kind='Reconcile' AND state='Queued'",
            cancellationToken, ("$due", At(now)), ("$id", Id(publicationId)));
        if (updated == 0)
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO jobs(id,kind,publication_id,priority,step_key,state,due_at) VALUES($job,'Reconcile',$publication,110,'reconcile','Queued',$due)", cancellationToken,
                ("$job", Id(Guid.NewGuid())), ("$publication", Id(publicationId)), ("$due", At(now)));
        transaction.Commit();
        return true;
    }

    public async Task<int> RequestCancelAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var changed = await SqliteDatabase.ExecuteCountAsync(connection, transaction, """
UPDATE publications
SET state=CASE
  WHEN first_submitted_at IS NULL AND state IN ('Pending','Ready') THEN 'Cancelled'
  ELSE 'CancelRequested'
END,
safe_error='cancel_requested',generation=generation+1
WHERE post_id=$post AND state IN ('Pending','Preparing','Ready','Publishing','Processing','ScheduledRemote','AwaitingUser','NeedsAttention')
""", cancellationToken, ("$post", Id(postId)));
        await SqliteDatabase.ExecuteAsync(connection, transaction, """
UPDATE jobs SET state='Cancelled',generation=generation+1
WHERE publication_id IN (SELECT id FROM publications WHERE post_id=$post AND state='Cancelled')
  AND state IN ('Queued','Claimed')
""", cancellationToken, ("$post", Id(postId)));
        transaction.Commit();
        return changed;
    }

    public async Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.id,j.kind,j.state,j.due_at,COALESCE(j.publication_id,j.auth_grant_id,j.stats_sync_run_id,j.data_deletion_id),COALESCE(p.provider_key,'local'),j.attempt_count,j.safe_error FROM jobs j LEFT JOIN publications p ON p.id=j.publication_id WHERE j.state IN ('Queued','Claimed','Blocked') ORDER BY j.due_at,j.priority DESC";
        var result = new List<QueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), Enum.Parse<JobKind>(reader.GetString(1)), Enum.Parse<JobState>(reader.GetString(2)), ParseAt(reader.GetString(3)), Guid.Parse(reader.GetString(4)), reader.GetString(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return result;
    }

    public async Task<Guid> StartWorkerRunAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await using var connection = await database.OpenAsync(cancellationToken);
        await SqliteDatabase.ExecuteAsync(connection, null, "INSERT INTO worker_runs(id,process_id,started_at,heartbeat_at) VALUES($id,$pid,$at,$at)", cancellationToken,
            ("$id", Id(id)), ("$pid", Environment.ProcessId), ("$at", At(now)));
        return id;
    }

    public async Task StopWorkerRunAsync(Guid runId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await SqliteDatabase.ExecuteAsync(connection, null, "UPDATE worker_runs SET stopped_at=$at,heartbeat_at=$at WHERE id=$id", cancellationToken, ("$at", At(now)), ("$id", Id(runId)));
    }

    public async Task RecoverAbandonedClaimsAsync(Guid runId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var jobs = new List<(Guid Job, Guid Publication, string? Attempt, StepEffect? Effect, ReplaySafety? Replay)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
SELECT j.id,j.publication_id,a.id,a.effect,a.replay_safety
FROM jobs j LEFT JOIN attempts a ON a.id=(SELECT id FROM attempts WHERE job_id=j.id ORDER BY started_at DESC LIMIT 1)
WHERE j.state='Claimed' AND (j.worker_run_id IS NULL OR j.worker_run_id<>$run) AND j.publication_id IS NOT NULL
""";
            command.Parameters.AddWithValue("$run", Id(runId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) jobs.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : Enum.Parse<StepEffect>(reader.GetString(3)), reader.IsDBNull(4) ? null : Enum.Parse<ReplaySafety>(reader.GetString(4))));
        }
        foreach (var item in jobs)
        {
            if (item.Attempt is null || item.Replay is ReplaySafety.SafeRead or ReplaySafety.SafeRepeatNoPublication or ReplaySafety.ResumeKnownHandle)
            {
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Queued',worker_run_id=NULL,claimed_at=NULL,generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job)));
                continue;
            }
            await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Done',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job)));
            await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state='Unknown',safe_error='crash_after_dispatch',failure_category='Unknown',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Publication)));
            await EnsureReconcileJobAsync(connection, transaction, item.Publication, now, cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<PublicationWorkItem>> ClaimDueAsync(Guid runId, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var candidates = new List<(Guid JobId, Guid PublicationId, Guid AccountId, string Provider, DateTimeOffset DueAt, long MaxLatenessSeconds, string PublicationState, bool Submitted)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
SELECT j.id,p.id,p.account_id,p.provider_key,s.due_at_utc,s.max_lateness_seconds,p.state,p.first_submitted_at IS NOT NULL
FROM jobs j JOIN publications p ON p.id=j.publication_id JOIN schedules s ON s.id=p.schedule_id
JOIN accounts account ON account.id=p.account_id
WHERE j.state='Queued' AND j.due_at<=$now
  AND account.status IN ('Ready','Connected')
  AND ((j.kind='Publish' AND p.state IN ('Pending','Preparing','Ready','Publishing'))
    OR (j.kind='Poll' AND p.state='Processing')
    OR (j.kind='Reconcile' AND p.state IN ('Unknown','Processing','CancelRequested')))
  AND NOT EXISTS (
    SELECT 1 FROM jobs active JOIN publications active_p ON active_p.id=active.publication_id
    WHERE active.state='Claimed' AND active_p.account_id=p.account_id AND active_p.provider_key=p.provider_key)
ORDER BY j.due_at,j.priority DESC,j.id LIMIT $scan
""";
            select.Parameters.AddWithValue("$now", At(now));
            select.Parameters.AddWithValue("$scan", Math.Max(limit, checked(limit * 16)));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) candidates.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), ParseAt(reader.GetString(4)), reader.GetInt64(5), reader.GetString(6), reader.GetBoolean(7)));
        }
        var claimed = new List<Guid>();
        var claimedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (claimed.Count >= limit) break;
            if (!claimedAccounts.Add($"{candidate.Provider}/{candidate.AccountId:D}")) continue;
            var canExpire = !candidate.Submitted && candidate.PublicationState is "Pending" or "Ready";
            if (canExpire && candidate.DueAt.AddSeconds(candidate.MaxLatenessSeconds) < now)
            {
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state='Expired',safe_error='max_lateness_exceeded',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(candidate.PublicationId)));
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Done',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(candidate.JobId)));
                continue;
            }
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE jobs SET state='Claimed',worker_run_id=$run,claimed_at=$at,generation=generation+1 WHERE id=$id AND state='Queued'";
            update.Parameters.AddWithValue("$run", Id(runId)); update.Parameters.AddWithValue("$at", At(now)); update.Parameters.AddWithValue("$id", Id(candidate.JobId));
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1) claimed.Add(candidate.JobId);
        }
        var result = new List<PublicationWorkItem>();
        foreach (var id in claimed) result.Add(await ReadWorkItemAsync(connection, transaction, id, cancellationToken));
        transaction.Commit();
        return result;
    }

    public async Task<Attempt?> PrepareDispatchAsync(PublicationWorkItem item, ProviderStep providerStep, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var accountStatus = await SqliteDatabase.ScalarAsync<string>(connection, transaction,
            "SELECT status FROM accounts WHERE id=$id", cancellationToken, ("$id", Id(item.Publication.AccountId)));
        if (accountStatus is not "Ready" and not "Connected")
        {
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "UPDATE jobs SET state='Queued',worker_run_id=NULL,claimed_at=NULL,safe_error='auth_disconnected',generation=generation+1 WHERE id=$id AND state='Claimed'",
                cancellationToken, ("$id", Id(item.Job.Id)));
            transaction.Commit();
            return null;
        }
        var currentState = await SqliteDatabase.ScalarAsync<string>(connection, transaction, "SELECT state FROM publications WHERE id=$id", cancellationToken, ("$id", Id(item.Publication.Id)))
            ?? throw new InvalidOperationException("Publication disappeared before dispatch.");
        if (currentState is nameof(PublicationState.CancelRequested) or nameof(PublicationState.Cancelled))
        {
            if (currentState == PublicationState.CancelRequested.ToString())
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state='Cancelled',safe_error='cancelled_before_dispatch',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Publication.Id)));
            await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Cancelled',generation=generation+1 WHERE id=$id AND state='Claimed'", cancellationToken, ("$id", Id(item.Job.Id)));
            transaction.Commit();
            return null;
        }
        if (!string.Equals(currentState, item.Publication.State.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Publication changed before dispatch.");
        var state = providerStep.Effect == StepEffect.MayPublish ? PublicationState.Publishing : item.Publication.State;
        PublicationStateMachine.EnsureCanTransition(item.Publication.State, state);
        var attempt = new Attempt(Guid.NewGuid(), item.Job.Id, providerStep.StepKey, DispatchState.Prepared, now, null, providerStep.RequestDigest, providerStep.Effect, providerStep.ReplaySafety, EffectCertainty.NotSent);
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "INSERT INTO attempts(id,job_id,step_key,dispatch_state,request_digest,effect,replay_safety,effect_certainty,started_at) VALUES($id,$job,$step,'Prepared',$digest,$effect,$replay,'NotSent',$at)", cancellationToken,
            ("$id", Id(attempt.Id)), ("$job", Id(item.Job.Id)), ("$step", providerStep.StepKey), ("$digest", providerStep.RequestDigest), ("$effect", providerStep.Effect.ToString()), ("$replay", providerStep.ReplaySafety.ToString()), ("$at", At(now)));
        await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET attempt_count=attempt_count+1,step_key=$step WHERE id=$id AND state='Claimed'", cancellationToken, ("$step", providerStep.StepKey), ("$id", Id(item.Job.Id)));
        await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state=$state,first_submitted_at=COALESCE(first_submitted_at,$at),generation=generation+1 WHERE id=$id", cancellationToken,
            ("$state", state.ToString()), ("$at", At(now)), ("$id", Id(item.Publication.Id)));
        transaction.Commit();
        return attempt;
    }

    public async Task CommitResultAsync(PublicationWorkItem item, Attempt attempt, StepResult result, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "UPDATE attempts SET dispatch_state='ReceiptCommitted',effect_certainty=$certainty,receipt_ref=$receipt,safe_error=$error,failure_category=$category,finished_at=$at WHERE id=$id", cancellationToken,
            ("$certainty", result.EffectCertainty.ToString()), ("$receipt", result.RemoteObjectId), ("$error", result.SafeError),
            ("$category", result.FailureCategory?.ToString()), ("$at", At(now)), ("$id", Id(attempt.Id)));

        var currentState = Enum.Parse<PublicationState>(await SqliteDatabase.ScalarAsync<string>(connection, transaction,
            "SELECT state FROM publications WHERE id=$id", cancellationToken, ("$id", Id(item.Publication.Id)))
            ?? throw new InvalidOperationException("Publication disappeared before receipt commit."));

        var knownRemoteId = await SqliteDatabase.ScalarAsync<string>(connection, transaction,
            "SELECT provider_object_id FROM remote_objects WHERE publication_id=$id AND kind='final' ORDER BY observed_at DESC LIMIT 1",
            cancellationToken, ("$id", Id(item.Publication.Id)));
        if (result.RemoteObjectId is not null)
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO remote_objects(id,publication_id,account_id,kind,provider_object_id,observed_at) VALUES($id,$publication,$account,$kind,$remote,$at)", cancellationToken,
                ("$id", Id(Guid.NewGuid())), ("$publication", Id(item.Publication.Id)), ("$account", Id(item.Publication.AccountId)),
                ("$kind", item.Publication.ProviderKey == "instagram" && attempt.StepKey == "instagram.reel.create.v1" ? "container" : "final"),
                ("$remote", result.RemoteObjectId), ("$at", At(now)));

        if (result.Checkpoint is not null)
        {
            var encrypted = protector.Protect($"checkpoint:{item.Publication.Id:D}", Encoding.UTF8.GetBytes(result.Checkpoint));
            var packed = JsonSerializer.SerializeToUtf8Bytes(encrypted);
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO provider_checkpoints(publication_id,ciphertext,updated_at) VALUES($id,$value,$at) ON CONFLICT(publication_id) DO UPDATE SET ciphertext=excluded.ciphertext,updated_at=excluded.updated_at", cancellationToken,
                ("$id", Id(item.Publication.Id)), ("$value", packed), ("$at", At(now)));
        }

        switch (result.Outcome)
        {
            case StepOutcome.Completed:
                {
                    var state = result.ObservedState ?? (attempt.Effect == StepEffect.MayPublish ? PublicationState.Published : PublicationState.Ready);
                    if (currentState == PublicationState.Ready && state == PublicationState.Published)
                    {
                        if (item.Publication.ProviderKey != "youtube" || item.Input.Target.Visibility != "private" ||
                            attempt.StepKey != "youtube.finish-private.v1" || attempt.Effect != StepEffect.ConfirmPrivate ||
                            result.EffectCertainty != EffectCertainty.Confirmed ||
                            string.IsNullOrWhiteSpace(knownRemoteId) ||
                            !string.Equals(knownRemoteId, result.RemoteObjectId, StringComparison.Ordinal))
                            throw new InvalidOperationException("Private publication confirmation is invalid.");
                    }
                    else PublicationStateMachine.EnsureCanTransition(currentState, state);
                    await SqliteDatabase.ExecuteAsync(connection, transaction,
                        "UPDATE publications SET state=$state,confirmed_at=$confirmed,published_at=$published,safe_error=NULL,failure_category=NULL,generation=generation+1 WHERE id=$id", cancellationToken,
                        ("$state", state.ToString()), ("$confirmed", At(now)), ("$published", state == PublicationState.Published ? At(now) : null), ("$id", Id(item.Publication.Id)));
                    await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Done',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job.Id)));
                    break;
                }
            case StepOutcome.Pending:
                if (currentState == PublicationState.CancelRequested && result.EffectCertainty == EffectCertainty.NoSideEffect)
                {
                    await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state='Cancelled',safe_error='cancelled_after_safe_result',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Publication.Id)));
                    await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Cancelled',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job.Id)));
                    break;
                }
                if (result.NextJobKind is { } nextJobKind)
                {
                    if (result.EffectCertainty == EffectCertainty.Ambiguous)
                        throw new InvalidOperationException("Ambiguous result cannot advance the durable workflow.");
                    if (nextJobKind is not (JobKind.Publish or JobKind.Poll))
                        throw new InvalidOperationException($"Unsupported publication continuation kind '{nextJobKind}'.");

                    var nextState = result.ObservedState ?? currentState;
                    PublicationStateMachine.EnsureCanTransition(currentState, nextState);
                    await SqliteDatabase.ExecuteAsync(connection, transaction,
                        "UPDATE publications SET state=$state,safe_error=$error,failure_category=$category,generation=generation+1 WHERE id=$id", cancellationToken,
                        ("$state", nextState.ToString()), ("$error", result.SafeError), ("$category", result.FailureCategory?.ToString()), ("$id", Id(item.Publication.Id)));
                    await SqliteDatabase.ExecuteAsync(connection, transaction,
                        "UPDATE jobs SET state='Done',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job.Id)));
                    await SqliteDatabase.ExecuteAsync(connection, transaction,
                        "INSERT INTO jobs(id,kind,publication_id,priority,step_key,state,due_at) VALUES($id,$kind,$publication,$priority,$step,'Queued',$due)", cancellationToken,
                        ("$id", Id(Guid.NewGuid())), ("$kind", nextJobKind.ToString()), ("$publication", Id(item.Publication.Id)),
                        ("$priority", ContinuationPriority(nextJobKind)), ("$step", nextJobKind == JobKind.Poll ? "poll" : "continue"),
                        ("$due", At(result.RetryAt ?? now)));
                    break;
                }
                if (item.Job.AttemptNo + 1 >= 8)
                {
                    await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state='NeedsAttention',safe_error='retry_limit_reached',failure_category=$category,generation=generation+1 WHERE id=$id", cancellationToken,
                        ("$category", result.FailureCategory?.ToString() ?? FailureCategory.Provider.ToString()), ("$id", Id(item.Publication.Id)));
                    await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Done',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job.Id)));
                }
                else
                    await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Queued',due_at=$due,worker_run_id=NULL,claimed_at=NULL,safe_error=$error,generation=generation+1 WHERE id=$id", cancellationToken,
                        ("$due", At(result.RetryAt ?? now.AddSeconds(2))), ("$error", result.SafeError), ("$id", Id(item.Job.Id)));
                break;
            case StepOutcome.Rejected:
                var rejectedState = currentState == PublicationState.CancelRequested && result.EffectCertainty == EffectCertainty.NoSideEffect
                    ? PublicationState.Cancelled
                    : result.ObservedState ?? PublicationState.Failed;
                PublicationStateMachine.EnsureCanTransition(currentState, rejectedState);
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state=$state,safe_error=$error,failure_category=$category,generation=generation+1 WHERE id=$id", cancellationToken,
                    ("$state", rejectedState.ToString()), ("$error", result.SafeError), ("$category", result.FailureCategory?.ToString()), ("$id", Id(item.Publication.Id)));
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Done',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job.Id)));
                break;
            case StepOutcome.Ambiguous:
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE publications SET state='Unknown',safe_error=$error,failure_category=$category,generation=generation+1 WHERE id=$id", cancellationToken,
                    ("$error", result.SafeError), ("$category", (result.FailureCategory ?? FailureCategory.Unknown).ToString()), ("$id", Id(item.Publication.Id)));
                await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Done',generation=generation+1 WHERE id=$id", cancellationToken, ("$id", Id(item.Job.Id)));
                await EnsureReconcileJobAsync(connection, transaction, item.Publication.Id, now, cancellationToken);
                break;
        }
        transaction.Commit();
    }

    public async Task<bool> IsStopRequestedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await SqliteDatabase.ScalarAsync<long>(connection, null, "SELECT stop_requested FROM worker_control WHERE singleton=1", cancellationToken) == 1;
    }
    public async Task RequestStopAsync(CancellationToken cancellationToken = default) { await using var c = await database.OpenAsync(cancellationToken); await SqliteDatabase.ExecuteAsync(c, null, "UPDATE worker_control SET stop_requested=1 WHERE singleton=1", cancellationToken); }
    public async Task ClearStopRequestAsync(CancellationToken cancellationToken = default) { await using var c = await database.OpenAsync(cancellationToken); await SqliteDatabase.ExecuteAsync(c, null, "UPDATE worker_control SET stop_requested=0 WHERE singleton=1", cancellationToken); }

    public async Task SaveStatsAsync(StatsWrite write, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var rawId = Guid.NewGuid();
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) brotli.Write(write.Raw.Payload);
            compressed = output.ToArray();
        }
        ProtectedValue protectedRaw;
        try { protectedRaw = protector.Protect($"raw-metrics:{rawId:D}", compressed); }
        finally { CryptographicOperations.ZeroMemory(compressed); }
        var packed = JsonSerializer.SerializeToUtf8Bytes(protectedRaw);
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "INSERT INTO raw_metric_responses(id,provider,api_version,subject_ref,retrieved_at,payload,payload_encoding,payload_hash,expires_at) VALUES($id,$provider,$api,$subject,$at,$payload,'br+aesgcm/v1',$hash,$expires)", cancellationToken,
            ("$id", Id(rawId)), ("$provider", write.Raw.Provider), ("$api", write.Raw.ApiVersion), ("$subject", write.Raw.SubjectRef),
            ("$at", At(write.Snapshot.RetrievedAt)), ("$payload", packed), ("$hash", Convert.ToHexStringLower(SHA256.HashData(write.Raw.Payload))), ("$expires", At(write.Raw.ExpiresAt)));
        var snapshot = write.Snapshot;
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "INSERT INTO metrics_snapshots(id,raw_id,account_id,provider,subject_ref,api_version,retrieved_at,period_start,period_end,dimensions_json,mapping_version) VALUES($id,$raw,$account,$provider,$subject,$api,$at,$start,$end,$dimensions,$mapping)", cancellationToken,
            ("$id", Id(snapshot.Id)), ("$raw", Id(rawId)), ("$account", Id(snapshot.AccountId)), ("$provider", snapshot.Provider), ("$subject", snapshot.SubjectRef),
            ("$api", snapshot.ApiVersion), ("$at", At(snapshot.RetrievedAt)), ("$start", snapshot.PeriodStart is null ? null : At(snapshot.PeriodStart.Value)),
            ("$end", snapshot.PeriodEnd is null ? null : At(snapshot.PeriodEnd.Value)), ("$dimensions", snapshot.DimensionsJson), ("$mapping", snapshot.MappingVersion));
        foreach (var observation in snapshot.Observations)
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO metric_observations(id,snapshot_id,provider_key,canonical_key,definition_version,value_decimal,value_text,unit,value_status,dimensions_json) VALUES($id,$snapshot,$provider,$canonical,$definition,$decimal,$text,$unit,$status,$dimensions)", cancellationToken,
                ("$id", Id(Guid.NewGuid())), ("$snapshot", Id(snapshot.Id)), ("$provider", observation.ProviderMetricKey), ("$canonical", observation.CanonicalKey),
                ("$definition", observation.DefinitionVersion), ("$decimal", observation.DecimalValue?.ToString(CultureInfo.InvariantCulture)), ("$text", observation.TextValue),
                ("$unit", observation.Unit), ("$status", observation.Status.ToString()), ("$dimensions", observation.DimensionsJson));
        transaction.Commit();
    }

    public async Task<IReadOnlyList<MetricsSnapshot>> GetStatsAsync(Guid accountId, string subjectRef, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = new List<(Guid Id, string Provider, string Api, DateTimeOffset At, DateTimeOffset? Start, DateTimeOffset? End, string Dimensions, string Mapping)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,provider,api_version,retrieved_at,period_start,period_end,dimensions_json,mapping_version FROM metrics_snapshots WHERE account_id=$account AND subject_ref=$subject ORDER BY retrieved_at";
            command.Parameters.AddWithValue("$account", Id(accountId)); command.Parameters.AddWithValue("$subject", subjectRef);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ParseAt(reader.GetString(3)), NullableAt(reader, 4), NullableAt(reader, 5), reader.GetString(6), reader.GetString(7)));
        }
        var snapshots = new List<MetricsSnapshot>();
        foreach (var row in rows)
        {
            var observations = new List<MetricObservation>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT provider_key,canonical_key,definition_version,value_decimal,value_text,unit,value_status,dimensions_json FROM metric_observations WHERE snapshot_id=$id ORDER BY id";
            command.Parameters.AddWithValue("$id", Id(row.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) observations.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), Enum.Parse<MetricValueStatus>(reader.GetString(6)), reader.GetString(7)));
            snapshots.Add(new(row.Id, accountId, row.Provider, subjectRef, row.Api, row.At, row.Start, row.End, row.Dimensions, row.Mapping, observations));
        }
        return snapshots;
    }

    public async Task<int> PurgeExpiredRawAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM raw_metric_responses WHERE expires_at<=$now";
        command.Parameters.AddWithValue("$now", At(now));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AuthGrantRecord?> GetAuthGrantAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider,subject,generation,expires_at,vault_blob_id,status,account_id,client_id,scope FROM auth_grants WHERE id=$id";
        command.Parameters.AddWithValue("$id", Id(grantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAuthGrant(reader, grantId) : null;
    }

    public async Task<AuthGrantRecord?> GetAuthGrantForAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,provider,subject,generation,expires_at,vault_blob_id,status,account_id,client_id,scope FROM auth_grants WHERE account_id=$account";
        command.Parameters.AddWithValue("$account", Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), ParseAt(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public async Task SaveAuthGrantAsync(AuthGrantRecord grant, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await SqliteDatabase.ExecuteAsync(connection, null, "INSERT INTO auth_grants(id,provider,subject,generation,expires_at,vault_blob_id,status,account_id,client_id,scope) VALUES($id,$provider,$subject,$generation,$expires,$blob,$status,$account,$client,$scope)", cancellationToken,
            ("$id", Id(grant.Id)), ("$provider", grant.Provider), ("$subject", grant.Subject), ("$generation", grant.Generation), ("$expires", At(grant.ExpiresAt)), ("$blob", grant.VaultBlobId), ("$status", grant.Status),
            ("$account", grant.AccountId is null ? null : Id(grant.AccountId.Value)), ("$client", grant.ClientId), ("$scope", grant.Scope));
    }

    public async Task<bool> ReplaceAuthGrantAsync(Guid id, long expectedGeneration, string vaultBlobId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE auth_grants SET vault_blob_id=$blob,expires_at=$expires,generation=generation+1 WHERE id=$id AND generation=$generation";
        command.Parameters.AddWithValue("$blob", vaultBlobId); command.Parameters.AddWithValue("$expires", At(expiresAt)); command.Parameters.AddWithValue("$id", Id(id)); command.Parameters.AddWithValue("$generation", expectedGeneration);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<AccountConnection?> GetAccountConnectionAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT a.provider_key,a.alias,a.status,a.remote_subject,a.display_name,a.client_id,a.scope,g.expires_at
FROM accounts a LEFT JOIN auth_grants g ON g.id=a.auth_grant_id
WHERE a.id=$id AND a.remote_subject IS NOT NULL
""";
        command.Parameters.AddWithValue("$id", Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(accountId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? reader.GetString(1) : reader.GetString(4), reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6), reader.IsDBNull(7) ? null : ParseAt(reader.GetString(7)));
    }

    public async Task<AccountConnection> SaveConnectedAccountAsync(AccountConnectionWrite write, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var existingId = await SqliteDatabase.ScalarAsync<string?>(connection, transaction,
            "SELECT id FROM accounts WHERE provider_key=$provider AND remote_subject=$subject", cancellationToken,
            ("$provider", write.Provider), ("$subject", write.RemoteSubject));
        var accountId = existingId is null ? Guid.NewGuid() : Guid.Parse(existingId);
        string? oldGrantId = null;
        string? oldBlobId = null;
        if (existingId is not null)
        {
            oldGrantId = await SqliteDatabase.ScalarAsync<string?>(connection, transaction, "SELECT auth_grant_id FROM accounts WHERE id=$id", cancellationToken, ("$id", existingId));
            if (oldGrantId is not null)
                oldBlobId = await SqliteDatabase.ScalarAsync<string?>(connection, transaction, "SELECT vault_blob_id FROM auth_grants WHERE id=$id", cancellationToken, ("$id", oldGrantId));
        }
        else
        {
            await SqliteDatabase.ExecuteAsync(connection, transaction,
                "INSERT INTO accounts(id,provider_key,alias,status,remote_subject,display_name,client_id,scope) VALUES($id,$provider,$alias,'Disconnected',$subject,$display,$client,$scope)", cancellationToken,
                ("$id", Id(accountId)), ("$provider", write.Provider), ("$alias", write.Alias), ("$subject", write.RemoteSubject),
                ("$display", write.DisplayName), ("$client", write.ClientId), ("$scope", write.Scope));
        }

        if (oldGrantId is not null)
        {
            await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE accounts SET auth_grant_id=NULL WHERE id=$id", cancellationToken, ("$id", Id(accountId)));
            await SqliteDatabase.ExecuteAsync(connection, transaction, "DELETE FROM auth_grants WHERE id=$id", cancellationToken, ("$id", oldGrantId));
        }
        var grantId = Guid.NewGuid();
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "INSERT INTO auth_grants(id,provider,subject,generation,expires_at,vault_blob_id,status,account_id,client_id,scope) VALUES($id,$provider,$subject,0,$expires,$blob,'Connected',$account,$client,$scope)", cancellationToken,
            ("$id", Id(grantId)), ("$provider", write.Provider), ("$subject", write.RemoteSubject), ("$expires", At(write.ExpiresAt)),
            ("$blob", write.VaultBlobId), ("$account", Id(accountId)), ("$client", write.ClientId), ("$scope", write.Scope));
        await SqliteDatabase.ExecuteAsync(connection, transaction,
            "UPDATE accounts SET alias=$alias,status='Connected',display_name=$display,client_id=$client,scope=$scope,auth_grant_id=$grant WHERE id=$id", cancellationToken,
            ("$alias", write.Alias), ("$display", write.DisplayName), ("$client", write.ClientId), ("$scope", write.Scope), ("$grant", Id(grantId)), ("$id", Id(accountId)));
        if (oldBlobId is not null)
            await SqliteDatabase.ExecuteAsync(connection, transaction, "DELETE FROM vault_blobs WHERE id=$id", cancellationToken, ("$id", oldBlobId));
        transaction.Commit();
        return new(accountId, write.Provider, write.Alias, "Connected", write.RemoteSubject, write.DisplayName, write.ClientId, write.Scope, write.ExpiresAt);
    }

    public async Task<bool> DisconnectAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var exists = await SqliteDatabase.ScalarAsync<long>(connection, transaction, "SELECT COUNT(*) FROM accounts WHERE id=$id", cancellationToken, ("$id", Id(accountId))) > 0;
        if (!exists) { transaction.Commit(); return false; }
        var grantId = await SqliteDatabase.ScalarAsync<string?>(connection, transaction, "SELECT auth_grant_id FROM accounts WHERE id=$id", cancellationToken, ("$id", Id(accountId)));
        string? blobId = null;
        if (grantId is not null)
            blobId = await SqliteDatabase.ScalarAsync<string?>(connection, transaction, "SELECT vault_blob_id FROM auth_grants WHERE id=$id", cancellationToken, ("$id", grantId));
        await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE accounts SET status='Disconnected',auth_grant_id=NULL WHERE id=$id", cancellationToken, ("$id", Id(accountId)));
        if (grantId is not null)
            await SqliteDatabase.ExecuteAsync(connection, transaction, "DELETE FROM auth_grants WHERE id=$id", cancellationToken, ("$id", grantId));
        if (blobId is not null)
            await SqliteDatabase.ExecuteAsync(connection, transaction, "DELETE FROM vault_blobs WHERE id=$id", cancellationToken, ("$id", blobId));
        transaction.Commit();
        return true;
    }

    public async Task SetQuarantineAsync(bool enabled, CancellationToken cancellationToken = default) { await using var c = await database.OpenAsync(cancellationToken); await SqliteDatabase.ExecuteAsync(c, null, "UPDATE installations SET quarantined=$value,restore_epoch=restore_epoch+CASE WHEN $value=1 THEN 1 ELSE 0 END", cancellationToken, ("$value", enabled ? 1 : 0)); }
    public async Task<bool> IsQuarantinedAsync(CancellationToken cancellationToken = default) { await using var c = await database.OpenAsync(cancellationToken); return await SqliteDatabase.ScalarAsync<long>(c, null, "SELECT quarantined FROM installations LIMIT 1", cancellationToken) == 1; }

    public async Task<RestoreStatus> GetRestoreStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var quarantined = await SqliteDatabase.ScalarAsync<long>(connection, null, "SELECT quarantined FROM installations LIMIT 1", cancellationToken) == 1;
        var publications = new List<RestorePublication>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT p.id,p.state,EXISTS(SELECT 1 FROM remote_objects r WHERE r.publication_id=p.id)
FROM publications p ORDER BY p.created_at,p.id
""";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = Enum.Parse<PublicationState>(reader.GetString(1));
            var hasRemote = reader.GetBoolean(2);
            var risk = state switch
            {
                PublicationState.Published when hasRemote => "known_remote",
                PublicationState.Failed or PublicationState.Expired or PublicationState.Cancelled or PublicationState.NeedsAttention => "send_suppressed",
                PublicationState.Unknown or PublicationState.Publishing or PublicationState.Processing => "reconcile_required",
                _ => "may_have_run_after_backup",
            };
            publications.Add(new(Guid.Parse(reader.GetString(0)), state, risk, hasRemote));
        }
        return new(quarantined, publications);
    }

    public async Task SuppressRestoredPublicationAsync(Guid publicationId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A suppression reason is required.");
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        if (await SqliteDatabase.ScalarAsync<long>(connection, transaction, "SELECT quarantined FROM installations LIMIT 1", cancellationToken) != 1)
            throw new InvalidOperationException("Restore quarantine is not enabled.");
        var changed = await SqliteDatabase.ExecuteCountAsync(connection, transaction,
            "UPDATE publications SET state='NeedsAttention',safe_error='restore_suppressed',generation=generation+1 WHERE id=$id AND state NOT IN ('Published','Failed','Expired','Cancelled','NeedsAttention')", cancellationToken, ("$id", Id(publicationId)));
        if (changed != 1) throw new InvalidOperationException("Publication is not an unresolved restored publication.");
        await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Cancelled',generation=generation+1 WHERE publication_id=$id AND state IN ('Queued','Claimed','Blocked')", cancellationToken, ("$id", Id(publicationId)));
        await SqliteDatabase.ExecuteAsync(connection, transaction, "INSERT INTO audit_events(id,kind,subject_type,subject_id,reason,created_at) VALUES($event,'RestoreSuppress','Publication',$id,$reason,$at)", cancellationToken,
            ("$event", Id(Guid.NewGuid())), ("$id", Id(publicationId)), ("$reason", reason), ("$at", At(now)));
        transaction.Commit();
    }

    public async Task ReleaseQuarantineAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        if (await SqliteDatabase.ScalarAsync<long>(connection, transaction, "SELECT quarantined FROM installations LIMIT 1", cancellationToken) != 1)
            throw new InvalidOperationException("Restore quarantine is not enabled.");
        var unresolved = await SqliteDatabase.ScalarAsync<long>(connection, transaction,
            "SELECT COUNT(*) FROM publications WHERE state NOT IN ('Published','Failed','Expired','Cancelled','NeedsAttention')", cancellationToken);
        if (unresolved != 0) throw new InvalidOperationException($"Restore quarantine has {unresolved} unresolved publication(s).");
        var unsafeJobs = await SqliteDatabase.ScalarAsync<long>(connection, transaction,
            "SELECT COUNT(*) FROM jobs WHERE state IN ('Queued','Claimed','Blocked') AND kind IN ('Publish','Prepare','Delete')", cancellationToken);
        if (unsafeJobs != 0) throw new InvalidOperationException($"Restore quarantine has {unsafeJobs} unsafe active job(s).");
        await SqliteDatabase.ExecuteAsync(connection, transaction, "UPDATE installations SET quarantined=0", cancellationToken);
        await SqliteDatabase.ExecuteAsync(connection, transaction, "INSERT INTO audit_events(id,kind,subject_type,subject_id,reason,created_at) VALUES($event,'RestoreRelease','Installation','default','all_publications_resolved',$at)", cancellationToken,
            ("$event", Id(Guid.NewGuid())), ("$at", At(now)));
        transaction.Commit();
    }

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var result = await SqliteDatabase.ScalarAsync<string>(connection, null, "PRAGMA quick_check", cancellationToken);
        var version = await SqliteDatabase.ScalarAsync<long>(connection, null, "PRAGMA user_version", cancellationToken);
        var probe = RandomNumberGenerator.GetBytes(32);
        var vaultHealthy = false;
        try
        {
            var protectedProbe = protector.Protect("doctor:vault", probe);
            var roundTrip = protector.Unprotect("doctor:vault", protectedProbe);
            try { vaultHealthy = CryptographicOperations.FixedTimeEquals(probe, roundTrip); }
            finally { CryptographicOperations.ZeroMemory(roundTrip); }
        }
        catch (CryptographicException) { }
        finally { CryptographicOperations.ZeroMemory(probe); }
        var quarantined = await IsQuarantinedAsync(cancellationToken);
        return
        [
            new("sqlite", result == "ok", result == "ok" ? "ok" : "corrupt", result ?? "no result"),
            new("schema", version == SqliteDatabase.CurrentSchemaVersion, "schema_version", $"{version}/{SqliteDatabase.CurrentSchemaVersion}"),
            new("vault", vaultHealthy, vaultHealthy ? "ok" : "unavailable", vaultHealthy ? "protected round-trip succeeded" : "protected round-trip failed"),
            new("quarantine", !quarantined, "restore_quarantine", quarantined ? "enabled" : "disabled"),
        ];
    }

    public async Task<string> PutAsync(string purpose, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("D");
        var value = protector.Protect($"vault:{id}:{purpose}", secret.Span);
        await using var connection = await database.OpenAsync(cancellationToken);
        await SqliteDatabase.ExecuteAsync(connection, null, "INSERT INTO vault_blobs(id,purpose,key_version,nonce,tag,ciphertext,created_at) VALUES($id,$purpose,$version,$nonce,$tag,$ciphertext,$at)", cancellationToken,
            ("$id", id), ("$purpose", purpose), ("$version", value.KeyVersion), ("$nonce", value.Nonce), ("$tag", value.Tag), ("$ciphertext", value.Ciphertext), ("$at", At(DateTimeOffset.UtcNow)));
        return id;
    }

    public async Task<byte[]> GetAsync(string blobId, string purpose, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT purpose,key_version,nonce,tag,ciphertext FROM vault_blobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", blobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Vault blob not found.");
        if (!string.Equals(reader.GetString(0), purpose, StringComparison.Ordinal)) throw new CryptographicException("Vault purpose mismatch.");
        return protector.Unprotect($"vault:{blobId}:{purpose}", new(reader.GetInt32(1), (byte[])reader[2], (byte[])reader[3], (byte[])reader[4]));
    }

    public async Task DeleteAsync(string blobId, CancellationToken cancellationToken = default) { await using var c = await database.OpenAsync(cancellationToken); await SqliteDatabase.ExecuteAsync(c, null, "DELETE FROM vault_blobs WHERE id=$id", cancellationToken, ("$id", blobId)); }

    private async Task<PublicationWorkItem> ReadWorkItemAsync(SqliteConnection connection, SqliteTransaction transaction, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT j.id,j.kind,j.priority,j.due_at,j.state,j.generation,j.attempt_count,j.worker_run_id,j.claimed_at,j.step_key,
p.id,p.post_id,p.target_id,p.account_id,p.provider_key,p.state,p.execution_mode,s.due_at_utc,s.max_lateness_seconds,p.created_at,p.first_submitted_at,p.confirmed_at,p.published_at,p.safe_error,p.generation,
content.id,content.kind,content.text,content.title,t.visibility,t.options_schema,t.options_version,t.options_json,c.ciphertext
FROM jobs j
JOIN publications p ON p.id=j.publication_id
JOIN schedules s ON s.id=p.schedule_id
JOIN posts post ON post.id=p.post_id
JOIN contents content ON content.id=post.content_id
JOIN targets t ON t.id=p.target_id
LEFT JOIN provider_checkpoints c ON c.publication_id=p.id WHERE j.id=$id
""";
        command.Parameters.AddWithValue("$id", Id(jobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Claimed work disappeared.");
        var publicationId = Guid.Parse(reader.GetString(10));
        var job = new Job(jobId, Enum.Parse<JobKind>(reader.GetString(1)), JobOwnerType.Publication, publicationId, reader.GetInt32(2), ParseAt(reader.GetString(3)), Enum.Parse<JobState>(reader.GetString(4)), reader.GetInt64(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)), NullableAt(reader, 8), reader.GetString(9));
        var publication = new Publication(publicationId, Guid.Parse(reader.GetString(11)), Guid.Parse(reader.GetString(12)), Guid.Parse(reader.GetString(13)), reader.GetString(14), Enum.Parse<PublicationState>(reader.GetString(15)), Enum.Parse<ExecutionMode>(reader.GetString(16)), ParseAt(reader.GetString(17)), TimeSpan.FromSeconds(reader.GetInt64(18)), ParseAt(reader.GetString(19)), NullableAt(reader, 20), NullableAt(reader, 21), NullableAt(reader, 22), reader.IsDBNull(23) ? null : reader.GetString(23), reader.GetInt64(24));
        var contentId = Guid.Parse(reader.GetString(25));
        var contentKind = Enum.Parse<ContentKind>(reader.GetString(26));
        var text = reader.IsDBNull(27) ? null : reader.GetString(27);
        var title = reader.IsDBNull(28) ? null : reader.GetString(28);
        var target = new TargetIntent(publication.AccountId, publication.ProviderKey, reader.GetString(29), reader.GetString(30), reader.GetInt32(31), reader.GetString(32));
        ProtectedValue? protectedCheckpoint = reader.IsDBNull(33) ? null : JsonSerializer.Deserialize<ProtectedValue>((byte[])reader[33]);
        await reader.DisposeAsync();
        var media = new List<MediaAsset>();
        await using (var mediaCommand = connection.CreateCommand())
        {
            mediaCommand.Transaction = transaction;
            mediaCommand.CommandText = "SELECT id,sha256,size_bytes,detected_mime,storage_ref,duration_ticks,width,height FROM media_assets WHERE content_id=$id ORDER BY ordinal";
            mediaCommand.Parameters.AddWithValue("$id", Id(contentId));
            await using var mediaReader = await mediaCommand.ExecuteReaderAsync(cancellationToken);
            while (await mediaReader.ReadAsync(cancellationToken)) media.Add(new(
                Guid.Parse(mediaReader.GetString(0)), mediaReader.GetString(1), mediaReader.GetInt64(2), mediaReader.GetString(3), mediaReader.GetString(4),
                mediaReader.IsDBNull(5) ? null : TimeSpan.FromTicks(mediaReader.GetInt64(5)), mediaReader.IsDBNull(6) ? null : mediaReader.GetInt32(6), mediaReader.IsDBNull(7) ? null : mediaReader.GetInt32(7)));
        }
        var checkpoint = protectedCheckpoint is null ? null : Encoding.UTF8.GetString(protector.Unprotect($"checkpoint:{publicationId:D}", protectedCheckpoint));
        return new(job, new(publication, new(contentId, contentKind, text, title, media), target), checkpoint);
    }

    private static Publication ReadPublication(SqliteDataReader reader, Guid postId) => new(
        Guid.Parse(reader.GetString(0)), postId, Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3),
        Enum.Parse<PublicationState>(reader.GetString(4)), Enum.Parse<ExecutionMode>(reader.GetString(5)), ParseAt(reader.GetString(6)),
        TimeSpan.FromSeconds(reader.GetInt64(7)), ParseAt(reader.GetString(8)), NullableAt(reader, 9), NullableAt(reader, 10), NullableAt(reader, 11),
        reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(13));

    private static AuthGrantRecord ReadAuthGrant(SqliteDataReader reader, Guid grantId) => new(
        grantId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2), ParseAt(reader.GetString(3)),
        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));

    private static async Task<IReadOnlyList<Guid>> PublicationIdsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid postId, CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT id FROM publications WHERE post_id=$id ORDER BY id"; command.Parameters.AddWithValue("$id", Id(postId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(Guid.Parse(reader.GetString(0))); return result;
    }

    private static async Task EnsureReconcileJobAsync(SqliteConnection connection, SqliteTransaction transaction, Guid publicationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await SqliteDatabase.ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO jobs(id,kind,publication_id,priority,step_key,state,due_at) VALUES($id,'Reconcile',$publication,110,'reconcile','Queued',$due)", cancellationToken,
            ("$id", Id(Guid.NewGuid())), ("$publication", Id(publicationId)), ("$due", At(now)));

    private static int ContinuationPriority(JobKind kind) => kind switch
    {
        JobKind.Publish => 100,
        JobKind.Poll => 80,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported publication continuation kind."),
    };

    private static SqliteCommand BuildPublicationQuery(SqliteConnection connection, string where, string limit)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT p.post_id,p.id,p.provider_key,p.account_id,a.alias,
  substr(COALESCE(content.text,''),1,160),p.state,s.mode,s.due_at_utc,p.created_at,p.published_at,
  (SELECT provider_object_id FROM remote_objects r WHERE r.publication_id=p.id AND r.kind='final' ORDER BY r.observed_at DESC LIMIT 1),
  j.kind,j.state,j.due_at,(SELECT COUNT(*) FROM attempts all_attempts JOIN jobs all_jobs ON all_jobs.id=all_attempts.job_id WHERE all_jobs.publication_id=p.id),p.safe_error,
  content.text,t.visibility,t.options_schema,p.first_submitted_at,p.confirmed_at,
  (SELECT attempt.safe_error FROM attempts attempt JOIN jobs attempt_job ON attempt_job.id=attempt.job_id WHERE attempt_job.publication_id=p.id ORDER BY attempt.started_at DESC LIMIT 1),
  EXISTS(SELECT 1 FROM jobs reconcile WHERE reconcile.publication_id=p.id AND reconcile.kind='Reconcile' AND reconcile.state IN ('Queued','Claimed','Blocked')),
  (EXISTS(SELECT 1 FROM attempts attempt JOIN jobs attempt_job ON attempt_job.id=attempt.job_id
    WHERE attempt_job.publication_id=p.id AND attempt_job.kind='Publish' AND attempt.effect_certainty IN ('NotSent','NoSideEffect')
      AND attempt.started_at=(SELECT MAX(last_attempt.started_at) FROM attempts last_attempt JOIN jobs last_job ON last_job.id=last_attempt.job_id WHERE last_job.publication_id=p.id AND last_job.kind='Publish'))
   AND NOT EXISTS(SELECT 1 FROM attempts ambiguous JOIN jobs ambiguous_job ON ambiguous_job.id=ambiguous.job_id
     WHERE ambiguous_job.publication_id=p.id AND ambiguous_job.kind='Publish' AND ambiguous.effect_certainty='Ambiguous')),
  COALESCE(p.failure_category,(SELECT category_attempt.failure_category FROM attempts category_attempt JOIN jobs category_job ON category_job.id=category_attempt.job_id WHERE category_job.publication_id=p.id ORDER BY category_attempt.started_at DESC LIMIT 1)),
  (SELECT provider_object_id FROM remote_objects container WHERE container.publication_id=p.id AND container.kind='container' ORDER BY container.observed_at DESC LIMIT 1)
FROM publications p
JOIN posts post ON post.id=p.post_id
JOIN contents content ON content.id=post.content_id
JOIN targets t ON t.id=p.target_id
JOIN schedules s ON s.id=p.schedule_id
JOIN accounts a ON a.id=p.account_id
LEFT JOIN jobs j ON j.id=(
  SELECT candidate.id FROM jobs candidate WHERE candidate.publication_id=p.id
  ORDER BY CASE candidate.state WHEN 'Claimed' THEN 0 WHEN 'Queued' THEN 1 WHEN 'Blocked' THEN 2 ELSE 3 END,
    candidate.due_at DESC,candidate.id DESC LIMIT 1)
{where}
ORDER BY p.created_at DESC,p.id DESC
{limit}
""";
        return command;
    }

    private static PublicationListItem ReadPublicationListItem(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), Guid.Parse(reader.GetString(3)),
        reader.GetString(4), reader.GetString(5), Enum.Parse<PublicationState>(reader.GetString(6)),
        Enum.Parse<ScheduleMode>(reader.GetString(7)), ParseAt(reader.GetString(8)), ParseAt(reader.GetString(9)),
        NullableAt(reader, 10), reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : Enum.Parse<JobKind>(reader.GetString(12)),
        reader.IsDBNull(13) ? null : Enum.Parse<JobState>(reader.GetString(13)),
        NullableAt(reader, 14), reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
        reader.IsDBNull(16) ? null : reader.GetString(16));

    private static int ReadCount(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : checked((int)reader.GetInt64(ordinal));

    private static string Id(Guid id) => id.ToString("D");
    private static string At(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseAt(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? NullableAt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseAt(reader.GetString(ordinal));
}
