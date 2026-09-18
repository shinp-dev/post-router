using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PostRouter.Application;

namespace PostRouter.Infrastructure;

public sealed class InstagramCallbackListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly TimeSpan _timeout;
    private int _used;

    public InstagramCallbackListener(int port = 8765, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start(1);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<AccountConnection> ReceiveAsync(AuthorizationSession session,
        Func<string, string, CancellationToken, Task<AccountConnection>> complete,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("oauth_callback_used");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("oauth_callback_invalid");
                if (requestLine.Length > 8192) throw new InvalidOperationException("oauth_callback_invalid");
                var completeHeaders = false;
                for (var index = 0; index < 100; index++)
                {
                    var header = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (header is null || header.Length > 8192) throw new InvalidOperationException("oauth_callback_invalid");
                    if (header.Length == 0) { completeHeaders = true; break; }
                }
                if (!completeHeaders) throw new InvalidOperationException("oauth_callback_invalid");
                var parts = requestLine.Split(' ', 3);
                if (parts.Length != 3 || parts[0] != "GET" || parts[2] != "HTTP/1.1" ||
                    !parts[1].StartsWith('/')) throw new InvalidOperationException("oauth_callback_invalid");
                var uri = new Uri(new Uri("http://127.0.0.1/"), parts[1]);
                if (uri.AbsolutePath != "/instagram/callback") throw new InvalidOperationException("oauth_callback_invalid");
                var query = ParseQuery(uri.Query);
                if (query.ContainsKey("error")) throw new InvalidOperationException("oauth_authorization_failed");
                if (!query.TryGetValue("state", out var state) || !FixedEquals(session.State, state))
                    throw new InvalidOperationException("oauth_state_mismatch");
                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                    throw new InvalidOperationException("oauth_code_missing");
                var connection = await complete(code, state, timeout.Token).ConfigureAwait(false);
                await WritePageAsync(stream, true, timeout.Token).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                try { await WritePageAsync(stream, false, CancellationToken.None).ConfigureAwait(false); }
                catch (IOException) { }
                throw;
            }
        }
        finally { _listener.Stop(); }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new InvalidOperationException("oauth_callback_invalid");
        }
        return result;
    }

    private static bool FixedEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static async Task WritePageAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(success
            ? "<!doctype html><meta charset=utf-8><title>Post Router</title><p>Instagramとの接続が完了しました。Post Routerに戻ってください。</p>"
            : "<!doctype html><meta charset=utf-8><title>Post Router</title><p>Instagramとの接続に失敗しました。Post Routerに戻ってください。</p>");
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
