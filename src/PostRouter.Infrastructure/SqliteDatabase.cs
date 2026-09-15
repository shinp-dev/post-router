using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PostRouter.Infrastructure;

public sealed class SqliteDatabase
{
    internal const int CurrentSchemaVersion = 2;
    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        DatabasePath = fullPath;
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecutePragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var initialVersion = await ScalarAsync<long>(connection, null, "PRAGMA user_version", cancellationToken);
        if (initialVersion > CurrentSchemaVersion) throw new InvalidDataException($"Database schema {initialVersion} is newer than supported {CurrentSchemaVersion}.");
        await using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at TEXT NOT NULL, app_version TEXT NOT NULL);", cancellationToken);

        foreach (var migration in Migrations.All)
        {
            var existing = await ScalarAsync<string?>(connection, transaction,
                "SELECT checksum FROM schema_migrations WHERE version=$version", cancellationToken, ("$version", migration.Version));
            if (existing is not null)
            {
                if (!string.Equals(existing, migration.Checksum, StringComparison.Ordinal))
                    throw new InvalidDataException($"Migration checksum mismatch at version {migration.Version}.");
                continue;
            }
            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations(version,name,checksum,applied_at,app_version) VALUES($version,$name,$checksum,$at,$app)",
                cancellationToken, ("$version", migration.Version), ("$name", migration.Name), ("$checksum", migration.Checksum),
                ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$app", typeof(SqliteDatabase).Assembly.GetName().Version?.ToString() ?? "0"));
        }

        await ExecuteAsync(connection, transaction, $"PRAGMA user_version={CurrentSchemaVersion}", cancellationToken);
        transaction.Commit();
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> ExecuteCountAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T?> ScalarAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull) return default;
        return (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecutePragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);
    }

    private sealed record Migration(int Version, string Name, string Sql)
    {
        public string Checksum { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
    }

    private static class Migrations
    {
        private const string Initial = """
CREATE TABLE installations(id TEXT PRIMARY KEY, schema_version INTEGER NOT NULL, restore_epoch INTEGER NOT NULL DEFAULT 0, quarantined INTEGER NOT NULL DEFAULT 0 CHECK(quarantined IN (0,1)), created_at TEXT NOT NULL);
CREATE TABLE contents(id TEXT PRIMARY KEY,kind TEXT NOT NULL,text TEXT NULL,title TEXT NULL);
CREATE TABLE media_assets(id TEXT PRIMARY KEY,content_id TEXT NOT NULL REFERENCES contents(id),ordinal INTEGER NOT NULL,sha256 TEXT NOT NULL,size_bytes INTEGER NOT NULL,detected_mime TEXT NOT NULL,storage_ref TEXT NOT NULL,duration_ticks INTEGER NULL,width INTEGER NULL,height INTEGER NULL,UNIQUE(content_id,ordinal));
CREATE TABLE posts(id TEXT PRIMARY KEY, client_request_id TEXT NOT NULL UNIQUE, intent_hash TEXT NOT NULL, canonical_intent BLOB NOT NULL, content_id TEXT NOT NULL REFERENCES contents(id), created_at TEXT NOT NULL, duplicate_of TEXT NULL);
CREATE TABLE accounts(id TEXT PRIMARY KEY,provider_key TEXT NOT NULL,alias TEXT NOT NULL,status TEXT NOT NULL,UNIQUE(provider_key,alias));
CREATE TABLE targets(id TEXT PRIMARY KEY, post_id TEXT NOT NULL REFERENCES posts(id), account_id TEXT NOT NULL REFERENCES accounts(id), provider_key TEXT NOT NULL, visibility TEXT NOT NULL, options_schema TEXT NOT NULL, options_version INTEGER NOT NULL, options_json TEXT NOT NULL, UNIQUE(post_id,account_id));
CREATE TABLE schedules(id TEXT PRIMARY KEY, mode TEXT NOT NULL, due_at_utc TEXT NOT NULL, max_lateness_seconds INTEGER NOT NULL CHECK(max_lateness_seconds>=0), original_local TEXT NULL, zone_id TEXT NULL, selected_offset_seconds INTEGER NULL, zone_rules_fingerprint TEXT NULL);
CREATE TABLE publications(id TEXT PRIMARY KEY, post_id TEXT NOT NULL REFERENCES posts(id), target_id TEXT NOT NULL UNIQUE REFERENCES targets(id), account_id TEXT NOT NULL, provider_key TEXT NOT NULL, schedule_id TEXT NOT NULL REFERENCES schedules(id), state TEXT NOT NULL, execution_mode TEXT NOT NULL, created_at TEXT NOT NULL, first_submitted_at TEXT NULL, confirmed_at TEXT NULL, published_at TEXT NULL, safe_error TEXT NULL, generation INTEGER NOT NULL DEFAULT 0);
CREATE TABLE worker_runs(id TEXT PRIMARY KEY, process_id INTEGER NOT NULL, started_at TEXT NOT NULL, stopped_at TEXT NULL, heartbeat_at TEXT NOT NULL);
CREATE TABLE worker_control(singleton INTEGER PRIMARY KEY CHECK(singleton=1), stop_requested INTEGER NOT NULL DEFAULT 0 CHECK(stop_requested IN (0,1)), maintenance_reason TEXT NULL);
INSERT INTO worker_control(singleton,stop_requested) VALUES(1,0);
CREATE TABLE vault_blobs(id TEXT PRIMARY KEY, purpose TEXT NOT NULL, key_version INTEGER NOT NULL, nonce BLOB NOT NULL, tag BLOB NOT NULL, ciphertext BLOB NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE auth_grants(id TEXT PRIMARY KEY, provider TEXT NOT NULL, subject TEXT NOT NULL, generation INTEGER NOT NULL, expires_at TEXT NOT NULL, vault_blob_id TEXT NOT NULL REFERENCES vault_blobs(id), status TEXT NOT NULL);
CREATE TABLE stats_sync_runs(id TEXT PRIMARY KEY,scope TEXT NOT NULL,state TEXT NOT NULL,requested_at TEXT NOT NULL,finished_at TEXT NULL,safe_error TEXT NULL);
CREATE TABLE data_deletions(id TEXT PRIMARY KEY,scope TEXT NOT NULL,reason TEXT NOT NULL,state TEXT NOT NULL,requested_at TEXT NOT NULL,finished_at TEXT NULL);
CREATE TABLE jobs(id TEXT PRIMARY KEY, kind TEXT NOT NULL, publication_id TEXT NULL REFERENCES publications(id), auth_grant_id TEXT NULL REFERENCES auth_grants(id), stats_sync_run_id TEXT NULL REFERENCES stats_sync_runs(id), data_deletion_id TEXT NULL REFERENCES data_deletions(id), priority INTEGER NOT NULL, step_key TEXT NOT NULL, state TEXT NOT NULL, due_at TEXT NOT NULL, generation INTEGER NOT NULL DEFAULT 0, attempt_count INTEGER NOT NULL DEFAULT 0, worker_run_id TEXT NULL REFERENCES worker_runs(id), claimed_at TEXT NULL, safe_error TEXT NULL,
CHECK((publication_id IS NOT NULL)+(auth_grant_id IS NOT NULL)+(stats_sync_run_id IS NOT NULL)+(data_deletion_id IS NOT NULL)=1),
CHECK((kind IN ('Publish','Prepare','Poll','Reconcile','Delete') AND publication_id IS NOT NULL) OR (kind='Refresh' AND auth_grant_id IS NOT NULL) OR (kind='StatsCollect' AND stats_sync_run_id IS NOT NULL) OR (kind='Purge' AND data_deletion_id IS NOT NULL)));
CREATE UNIQUE INDEX ux_jobs_active_step ON jobs(COALESCE(publication_id,auth_grant_id,stats_sync_run_id,data_deletion_id),kind,step_key) WHERE state IN ('Queued','Claimed');
CREATE INDEX ix_jobs_due ON jobs(state,priority,due_at);
CREATE TABLE attempts(id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(id), step_key TEXT NOT NULL, dispatch_state TEXT NOT NULL, request_digest TEXT NOT NULL, effect TEXT NOT NULL, replay_safety TEXT NOT NULL, effect_certainty TEXT NOT NULL, receipt_ref TEXT NULL, safe_error TEXT NULL, started_at TEXT NOT NULL, finished_at TEXT NULL);
CREATE TABLE remote_objects(id TEXT PRIMARY KEY, publication_id TEXT NOT NULL REFERENCES publications(id), account_id TEXT NOT NULL, kind TEXT NOT NULL, provider_object_id TEXT NOT NULL, observed_at TEXT NOT NULL, UNIQUE(account_id,kind,provider_object_id));
CREATE TABLE provider_checkpoints(publication_id TEXT PRIMARY KEY REFERENCES publications(id), ciphertext BLOB NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE raw_metric_responses(id TEXT PRIMARY KEY, provider TEXT NOT NULL, api_version TEXT NOT NULL, subject_ref TEXT NOT NULL, retrieved_at TEXT NOT NULL, payload BLOB NOT NULL, payload_encoding TEXT NOT NULL, payload_hash TEXT NOT NULL, expires_at TEXT NOT NULL);
CREATE TABLE metrics_snapshots(id TEXT PRIMARY KEY, raw_id TEXT NOT NULL REFERENCES raw_metric_responses(id) ON DELETE CASCADE, account_id TEXT NOT NULL, provider TEXT NOT NULL, subject_ref TEXT NOT NULL, api_version TEXT NOT NULL, retrieved_at TEXT NOT NULL, period_start TEXT NULL, period_end TEXT NULL, dimensions_json TEXT NOT NULL, mapping_version TEXT NOT NULL);
CREATE TABLE metric_observations(id TEXT PRIMARY KEY, snapshot_id TEXT NOT NULL REFERENCES metrics_snapshots(id) ON DELETE CASCADE, provider_key TEXT NOT NULL, canonical_key TEXT NULL, definition_version INTEGER NOT NULL, value_decimal TEXT NULL, value_text TEXT NULL, unit TEXT NOT NULL, value_status TEXT NOT NULL, dimensions_json TEXT NOT NULL);
CREATE TABLE audit_events(id TEXT PRIMARY KEY,kind TEXT NOT NULL,subject_type TEXT NOT NULL,subject_id TEXT NOT NULL,reason TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE INDEX ix_metrics_subject ON metrics_snapshots(account_id,subject_ref,retrieved_at);
CREATE INDEX ix_raw_expiry ON raw_metric_responses(expires_at);
INSERT INTO installations(id,schema_version,created_at) VALUES('default',1,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
""";

        private const string ProviderAccounts = """
ALTER TABLE accounts ADD COLUMN remote_subject TEXT NULL;
ALTER TABLE accounts ADD COLUMN display_name TEXT NULL;
ALTER TABLE accounts ADD COLUMN client_id TEXT NULL;
ALTER TABLE accounts ADD COLUMN scope TEXT NULL;
ALTER TABLE accounts ADD COLUMN auth_grant_id TEXT NULL REFERENCES auth_grants(id);
ALTER TABLE auth_grants ADD COLUMN account_id TEXT NULL REFERENCES accounts(id);
ALTER TABLE auth_grants ADD COLUMN client_id TEXT NULL;
ALTER TABLE auth_grants ADD COLUMN scope TEXT NULL;
CREATE UNIQUE INDEX ux_accounts_provider_subject ON accounts(provider_key,remote_subject) WHERE remote_subject IS NOT NULL;
CREATE UNIQUE INDEX ux_accounts_provider_alias_nocase ON accounts(provider_key COLLATE NOCASE,alias COLLATE NOCASE);
CREATE UNIQUE INDEX ux_auth_grants_account ON auth_grants(account_id) WHERE account_id IS NOT NULL;
UPDATE installations SET schema_version=2;
""";

        public static IReadOnlyList<Migration> All { get; } =
        [
            new(1, "initial", Initial),
            new(2, "provider_accounts", ProviderAccounts),
        ];
    }
}
