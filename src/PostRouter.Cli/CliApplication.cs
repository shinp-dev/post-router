using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PostRouter.Infrastructure;

namespace PostRouter.Cli;

public static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<int> RunAsync(string[] args)
    {
        var dataDirectory = new Option<string?>("--data-dir") { Description = "Installation data directory" };
        var root = new RootCommand("Durable multi-provider publication router");
        root.Options.Add(dataDirectory);
        root.Subcommands.Add(BuildPost(dataDirectory));
        root.Subcommands.Add(BuildQueue(dataDirectory));
        root.Subcommands.Add(BuildWorker(dataDirectory));
        root.Subcommands.Add(BuildStats(dataDirectory));
        root.Subcommands.Add(BuildDoctor(dataDirectory));
        root.Subcommands.Add(BuildDatabase(dataDirectory));
        root.Subcommands.Add(BuildAccount(dataDirectory));
        return root.Parse(args).InvokeAsync();
    }

    private static Command BuildPost(Option<string?> dataDirectory)
    {
        var command = new Command("post", "Validate, spool, and enqueue a publication");
        var file = new Option<FileInfo?>("--file") { Description = "Versioned publication manifest" };
        command.Options.Add(file);
        command.SetAction((result, token) => ExecuteAsync(async () =>
        {
            var input = result.GetValue(file) ?? throw new ArgumentException("--file is required.");
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            var accounts = await runtime.Posts.AccountsAsync(token);
            var intent = await ManifestReader.ReadAsync(input.FullName, runtime.Spool, accounts, TimeProvider.System, token);
            var queued = await runtime.Posts.EnqueueAsync(intent, token);
            return new { postId = queued.PostId, queued.Existing, publicationIds = queued.PublicationIds };
        }));

        var status = new Command("status", "Show persisted publication state");
        var postId = new Argument<Guid>("post-id");
        status.Arguments.Add(postId);
        status.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Posts.GetAsync(result.GetValue(postId), token) ?? throw new KeyNotFoundException("Post not found.");
        }));
        var cancel = new Command("cancel", "Cancel unsent publications or record a cancellation request");
        var cancelPostId = new Argument<Guid>("post-id");
        cancel.Arguments.Add(cancelPostId);
        cancel.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            var changed = await runtime.Posts.CancelAsync(result.GetValue(cancelPostId), token);
            if (changed == 0) throw new InvalidOperationException("No cancellable publication was found.");
            return new { postId = result.GetValue(cancelPostId), changed };
        }));
        command.Subcommands.Add(status);
        command.Subcommands.Add(cancel);
        return command;
    }

    private static Command BuildQueue(Option<string?> dataDirectory)
    {
        var command = new Command("queue", "List durable jobs");
        command.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Posts.QueueAsync(token);
        }));
        return command;
    }

    private static Command BuildWorker(Option<string?> dataDirectory)
    {
        var command = new Command("worker", "Run and control the local coordinator");
        var once = new Command("once", "Process the current due batch");
        once.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return new { processed = await runtime.Worker.RunOnceAsync(token) };
        }));
        var run = new Command("run", "Run until stopped");
        run.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            await runtime.Worker.RunAsync(TimeSpan.FromSeconds(2), token);
            return new { stopped = true };
        }));
        var stop = new Command("stop", "Request graceful stop and wait for the worker lock");
        stop.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            await runtime.Store.RequestStopAsync(token);
            await WaitForWorkerExitAsync(runtime.DataDirectory, TimeSpan.FromSeconds(30), token);
            return new { stopped = true };
        }));
        var install = new Command("install", "Register the worker for the current Windows user");
        var executable = new Option<FileInfo?>("--executable") { Description = "Published pub executable; defaults to the current process" };
        install.Options.Add(executable);
        install.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            var path = result.GetValue(executable)?.FullName ?? Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable.");
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("worker install requires --executable pointing to a published pub executable when invoked through dotnet.");
            await WindowsTaskScheduler.InstallAsync(path, runtime.DataDirectory, token);
            return await WindowsTaskScheduler.StatusAsync(token);
        }));
        var start = new Command("start", "Start the registered Windows task");
        start.SetAction((_, token) => ExecuteAsync(async () => { await WindowsTaskScheduler.StartAsync(token); return new { started = true }; }));
        var taskStatus = new Command("status", "Inspect the registered Windows task settings");
        taskStatus.SetAction((_, token) => ExecuteAsync(async () => await WindowsTaskScheduler.StatusAsync(token)));
        var uninstall = new Command("uninstall", "Stop and remove the registered task without deleting queue data");
        uninstall.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            await runtime.Store.RequestStopAsync(token);
            await WaitForWorkerExitAsync(runtime.DataDirectory, TimeSpan.FromSeconds(30), token);
            await WindowsTaskScheduler.UninstallAsync(token);
            return new { uninstalled = true, dataPreserved = true };
        }));
        command.Subcommands.Add(once);
        command.Subcommands.Add(run);
        command.Subcommands.Add(stop);
        command.Subcommands.Add(install);
        command.Subcommands.Add(start);
        command.Subcommands.Add(taskStatus);
        command.Subcommands.Add(uninstall);
        return command;
    }

    private static async Task WaitForWorkerExitAsync(string dataDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var factory = new FileWorkerLockFactory(Path.Combine(dataDirectory, "worker.lock"));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var owned = await factory.TryAcquireAsync(cancellationToken);
            if (owned is not null) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("Worker did not release its installation lock within 30 seconds.");
    }

    private static Command BuildStats(Option<string?> dataDirectory)
    {
        var command = new Command("stats", "Read and maintain metric snapshots");
        var sync = new Command("sync", "Fetch and persist a provider metrics snapshot");
        var syncProvider = new Option<string>("--provider") { Required = true };
        var syncAccount = new Option<Guid>("--account") { Required = true };
        var syncSubject = new Option<string>("--subject") { Required = true };
        sync.Options.Add(syncProvider); sync.Options.Add(syncAccount); sync.Options.Add(syncSubject);
        sync.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Stats.SyncAsync(result.GetValue(syncProvider)!, result.GetValue(syncAccount), result.GetValue(syncSubject)!, token);
        }));
        var show = new Command("show");
        var account = new Option<Guid>("--account") { Required = true };
        var subject = new Option<string>("--subject") { Required = true };
        show.Options.Add(account); show.Options.Add(subject);
        show.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Stats.ShowAsync(result.GetValue(account), result.GetValue(subject)!, token);
        }));
        var purge = new Command("purge-expired");
        purge.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return new { deleted = await runtime.Stats.PurgeExpiredAsync(token) };
        }));
        command.Subcommands.Add(sync); command.Subcommands.Add(show); command.Subcommands.Add(purge);
        return command;
    }

    private static Command BuildDoctor(Option<string?> dataDirectory)
    {
        var command = new Command("doctor", "Check database, schema, vault, and quarantine state");
        command.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            var checks = (await runtime.Maintenance.DoctorAsync(token)).ToList();
            if (OperatingSystem.IsWindows())
            {
                var status = await WindowsTaskScheduler.StatusAsync(token);
                checks.Add(new("worker-task", status.Installed && status.DefinitionValid,
                    !status.Installed ? "not_installed" : status.DefinitionValid ? "ok" : "invalid_definition",
                    !status.Installed ? "not installed" : $"ExecutionTimeLimit={status.ExecutionTimeLimit}; MultipleInstancesPolicy={status.MultipleInstancesPolicy}"));
            }
            else checks.Add(new("worker-task", true, "not_applicable", "Windows Task Scheduler is not used on this platform."));
            return checks;
        }));
        return command;
    }

    private static Command BuildDatabase(Option<string?> dataDirectory)
    {
        var command = new Command("db", "Database maintenance");
        var backup = new Command("backup");
        var output = new Option<DirectoryInfo>("--output") { Required = true };
        backup.Options.Add(output);
        backup.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            await runtime.Store.RequestStopAsync(token);
            return await runtime.Maintenance.BackupAsync(result.GetValue(output)!.FullName, token);
        }));
        var check = new Command("check");
        check.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Maintenance.DoctorAsync(token);
        }));
        var restore = new Command("restore");
        var input = new Argument<FileInfo?>("backup") { Description = "Backup database file" };
        restore.Arguments.Add(input);
        restore.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            await runtime.Store.RequestStopAsync(token);
            var backupFile = result.GetValue(input) ?? throw new ArgumentException("backup is required.");
            await runtime.Maintenance.RestoreAsync(backupFile.FullName, token);
            return new { restored = true, quarantined = true };
        }));
        var restoreStatus = new Command("status", "Classify publications after restore");
        restoreStatus.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Maintenance.RestoreStatusAsync(token);
        }));
        var suppress = new Command("suppress", "Prevent a restored publication from being sent");
        var publicationId = new Argument<Guid>("publication-id");
        var reason = new Option<string>("--reason") { Required = true };
        suppress.Arguments.Add(publicationId);
        suppress.Options.Add(reason);
        suppress.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            await runtime.Maintenance.SuppressAsync(result.GetValue(publicationId), result.GetValue(reason)!, token);
            return new { publicationId = result.GetValue(publicationId), suppressed = true };
        }));
        var release = new Command("release", "Release quarantine only after all publications are resolved or suppressed");
        release.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            await runtime.Maintenance.ReleaseAsync(token);
            return new { quarantined = false };
        }));
        restore.Subcommands.Add(restoreStatus);
        restore.Subcommands.Add(suppress);
        restore.Subcommands.Add(release);
        command.Subcommands.Add(backup); command.Subcommands.Add(check); command.Subcommands.Add(restore);
        return command;
    }

    private static Command BuildAccount(Option<string?> dataDirectory)
    {
        var account = new Command("account", "Connect and manage provider accounts");
        var list = new Command("list");
        list.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Posts.AccountsAsync(token);
        }));
        var connect = new Command("connect", "Connect an X account with OAuth 2.0 PKCE");
        var connectProvider = new Argument<string>("provider");
        var clientId = new Option<string>("--client-id") { Required = true };
        var redirectUri = new Option<Uri>("--redirect-uri") { Required = true };
        var alias = new Option<string?>("--alias");
        connect.Arguments.Add(connectProvider); connect.Options.Add(clientId); connect.Options.Add(redirectUri); connect.Options.Add(alias);
        connect.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            var session = runtime.Accounts.BeginConnect(result.GetValue(connectProvider)!, result.GetValue(clientId)!, result.GetValue(redirectUri)!, result.GetValue(alias));
            var callback = await ReceiveOAuthCallbackAsync(session, token);
            return await runtime.Accounts.CompleteConnectAsync(session, callback.Code, callback.State, token);
        }));
        var status = new Command("status", "Show non-secret connection metadata");
        var statusAccount = new Option<Guid>("--account") { Required = true };
        status.Options.Add(statusAccount);
        status.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Accounts.StatusAsync(result.GetValue(statusAccount), token) ?? throw new KeyNotFoundException("Account not found.");
        }));
        var disconnect = BuildDisconnectCommand("disconnect", "Remove local credentials and pause queued jobs", dataDirectory);
        var reset = BuildDisconnectCommand("reset", "Reset local authentication without deleting history", dataDirectory);
        var revoke = new Command("revoke", "Revoke at X, then remove local credentials");
        var revokeAccount = new Option<Guid>("--account") { Required = true };
        revoke.Options.Add(revokeAccount);
        revoke.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            return await runtime.Accounts.RevokeAsync(result.GetValue(revokeAccount), token);
        }));
        var reconnect = new Command("reconnect", "Reconnect the same remote account");
        var reconnectAccount = new Option<Guid>("--account") { Required = true };
        var reconnectRedirect = new Option<Uri>("--redirect-uri") { Required = true };
        reconnect.Options.Add(reconnectAccount); reconnect.Options.Add(reconnectRedirect);
        reconnect.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            var session = await runtime.Accounts.BeginReconnectAsync(result.GetValue(reconnectAccount), result.GetValue(reconnectRedirect)!, token);
            var callback = await ReceiveOAuthCallbackAsync(session, token);
            return await runtime.Accounts.CompleteConnectAsync(session, callback.Code, callback.State, token);
        }));
        account.Subcommands.Add(list);
        account.Subcommands.Add(connect);
        account.Subcommands.Add(status);
        account.Subcommands.Add(disconnect);
        account.Subcommands.Add(reset);
        account.Subcommands.Add(revoke);
        account.Subcommands.Add(reconnect);
        return account;
    }

    private static Command BuildDisconnectCommand(string name, string description, Option<string?> dataDirectory)
    {
        var command = new Command(name, description);
        var accountId = new Option<Guid>("--account") { Required = true };
        command.Options.Add(accountId);
        command.SetAction((result, token) => ExecuteAsync(async () =>
        {
            await using var runtime = await RuntimeFactory.CreateAsync(result.GetValue(dataDirectory), cancellationToken: token);
            var id = result.GetValue(accountId);
            if (!await runtime.Accounts.DisconnectAsync(id, token)) throw new KeyNotFoundException("Account not found.");
            return new { accountId = id, status = "Disconnected", historyPreserved = true, queuedJobsPreserved = true };
        }));
        return command;
    }

    private static async Task<(string Code, string State)> ReceiveOAuthCallbackAsync(PostRouter.Application.AuthorizationSession session, CancellationToken cancellationToken)
    {
        var address = string.Equals(session.RedirectUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Parse(session.RedirectUri.Host);
        var listener = new TcpListener(address, session.RedirectUri.Port);
        listener.Start(1);
        try
        {
            try { Process.Start(new ProcessStartInfo(session.AuthorizationUri.AbsoluteUri) { UseShellExecute = true }); }
            catch { Console.Error.WriteLine($"Open this authorization URL in your browser: {session.AuthorizationUri.AbsoluteUri}"); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidDataException("OAuth callback request is empty.");
            if (requestLine.Length > 8192) throw new InvalidDataException("OAuth callback request is too large.");
            for (var lines = 0; lines < 100; lines++)
            {
                var header = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidDataException("OAuth callback headers are incomplete.");
                if (header.Length == 0) break;
                if (header.Length > 8192 || lines == 99) throw new InvalidDataException("OAuth callback headers are too large.");
            }
            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !string.Equals(parts[0], "GET", StringComparison.Ordinal)) throw new InvalidDataException("OAuth callback method is invalid.");
            var callback = new Uri(session.RedirectUri, parts[1]);
            if (!string.Equals(callback.Scheme, session.RedirectUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(callback.Host, session.RedirectUri.Host, StringComparison.OrdinalIgnoreCase) || callback.Port != session.RedirectUri.Port ||
                !string.Equals(callback.AbsolutePath, session.RedirectUri.AbsolutePath, StringComparison.Ordinal))
                throw new InvalidDataException("OAuth callback path does not match the registered redirect URI.");
            var query = ParseQuery(callback.Query);
            var code = query.GetValueOrDefault("code");
            var state = query.GetValueOrDefault("state");
            var success = !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(state);
            var html = success ? "Authentication received. You may close this window." : "Authentication failed. Return to the CLI.";
            var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>post-router</title><p>{html}</p>");
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, timeout.Token); await stream.WriteAsync(body, timeout.Token);
            if (!success) throw new InvalidOperationException("oauth_authorization_failed");
            return (code!, state!);
        }
        finally { listener.Stop(); }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static async Task<int> ExecuteAsync(Func<Task<object>> action)
    {
        try { WriteSuccess(await action()); return 0; }
        catch (ArgumentException ex) { WriteError("invalid_input", ex.Message); return 2; }
        catch (PlatformNotSupportedException ex) { WriteError("platform", ex.Message); return 3; }
        catch (NotSupportedException ex) { WriteError("unsupported", ex.Message); return 2; }
        catch (KeyNotFoundException ex) { WriteError("not_found", ex.Message); return 6; }
        catch (TimeoutException ex) { WriteError("busy", ex.Message); return 7; }
        catch (InvalidDataException ex) { WriteError("invalid_data", ex.Message); return 7; }
        catch (InvalidOperationException ex) { WriteError("invalid_state", ex.Message); return 7; }
        catch (UnauthorizedAccessException) { WriteError("access_denied", "The operation is not permitted for the current user."); return 7; }
        catch (System.Security.Cryptography.CryptographicException) { WriteError("credential_unavailable", "Protected data could not be read."); return 7; }
        catch (IOException) { WriteError("io_error", "A required local file operation failed."); return 7; }
        catch (JsonException) { WriteError("invalid_json", "A JSON document is malformed or incompatible."); return 2; }
        catch (XProviderException ex) { WriteError("provider_error", ex.SafeCode); return ex.Retryable ? 7 : 2; }
        catch (Exception ex) { WriteError("internal_error", ex.GetType().Name); return 1; }
    }

    private static void WriteSuccess(object result) => Console.Out.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, requestId = Guid.NewGuid(), result, warnings = Array.Empty<string>(), error = (object?)null }, JsonOptions));
    private static void WriteError(string code, string message) => Console.Out.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, requestId = Guid.NewGuid(), result = (object?)null, warnings = Array.Empty<string>(), error = new { code, message } }, JsonOptions));
}
