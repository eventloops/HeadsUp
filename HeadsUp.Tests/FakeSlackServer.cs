using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HeadsUp.Tests;

/// <summary>
/// A minimal in-process Slack: serves the handful of web-API endpoints the app calls
/// (as canned JSON over raw HTTP/1.1) plus the real-time WebSocket, so the whole
/// client-session pipeline can run end-to-end with no network and no mocks.
/// Raw TCP rather than HttpListener so no URL-ACL/admin rights are ever needed.
/// </summary>
public sealed class FakeSlackServer : IDisposable // public: used in a [Theory] signature
{
    public enum Behavior { Normal, AuthFail, RtmFail, RtmNoUrl, UsersListFail }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _wsLock = new();
    private TaskCompletionSource<Stream> _wsStream =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _usersListCalls;
    private int _webSocketConnections;
    private readonly TaskCompletionSource<string> _firstClientFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, dnd.info reports the user snoozed in Slack until this time.</summary>
    public DateTimeOffset? DndUntil { get; set; }

    public Behavior Mode { get; set; } = Behavior.Normal;
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public int UsersListCalls => _usersListCalls;
    public int WebSocketConnections => _webSocketConnections;

    /// <summary>Completes once the app's (latest) WebSocket has connected (5s timeout).</summary>
    public Task<Stream> WebSocketConnected
    {
        get
        {
            lock (_wsLock)
                return _wsStream.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public FakeSlackServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    /// <summary>Pushes a text frame to the connected WebSocket client.</summary>
    public async Task SendTextFrameAsync(string json)
    {
        var stream = await WebSocketConnected;
        var payload = Encoding.UTF8.GetBytes(json);

        using var frame = new MemoryStream();
        frame.WriteByte(0x81); // FIN + text opcode
        if (payload.Length < 126)
        {
            frame.WriteByte((byte)payload.Length);
        }
        else
        {
            frame.WriteByte(126);
            frame.WriteByte((byte)(payload.Length >> 8));
            frame.WriteByte((byte)payload.Length);
        }
        frame.Write(payload);

        await stream.WriteAsync(frame.ToArray(), _cts.Token);
    }

    /// <summary>The first text frame the client sends over the WebSocket (e.g. a keep-alive ping).</summary>
    public Task<string> FirstClientFrame => _firstClientFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>Closes the WebSocket gracefully (close frame), like Slack recycling a connection.</summary>
    public async Task CloseWebSocketAsync()
    {
        var stream = await WebSocketConnected;
        await stream.WriteAsync(new byte[] { 0x88, 0x00 }, _cts.Token); // FIN + close, empty payload
    }

    // ------------------------------------------------------------------
    // Connection handling
    // ------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleConnectionAsync(client));
            }
        }
        catch
        {
            // Listener closed on dispose.
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            // HTTP keep-alive: serve requests on this connection until it closes.
            while (!_cts.IsCancellationRequested)
            {
                var request = await ReadRequestAsync(stream);
                if (request is null)
                    return;

                var (path, headers, body) = request.Value;

                if (path == "/ws")
                {
                    await CompleteWebSocketHandshakeAsync(stream, headers);
                    lock (_wsLock)
                    {
                        // A fresh TCS per connection so reconnect tests can await the next socket.
                        if (_wsStream.Task.IsCompleted)
                            _wsStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _wsStream.TrySetResult(stream);
                        Interlocked.Increment(ref _webSocketConnections);
                    }
                    await ReadClientFramesAsync(stream); // capture pings until shutdown
                    return;
                }

                await WriteJsonResponseAsync(stream, Route(path, body));
            }
        }
        catch
        {
            // Connection torn down mid-request (normal at end of a test).
        }
    }

    private string Route(string path, string body) => path switch
    {
        "/api/auth.test" => Mode == Behavior.AuthFail
            ? """{ "ok": false, "error": "invalid_auth" }"""
            : """
              { "ok": true, "user": "cameron", "team": "Procode",
                "team_id": "T123", "user_id": "USELF" }
              """,

        "/api/rtm.connect" => Mode switch
        {
            Behavior.RtmFail => """{ "ok": false, "error": "rtm_connect_failed" }""",
            Behavior.RtmNoUrl => """{ "ok": true }""",
            _ => $$"""{ "ok": true, "url": "ws://127.0.0.1:{{Port}}/ws" }""",
        },

        // An unknown user exercises the ok:false fallback in ResolveUserAsync.
        "/api/users.info" => body.Contains("UNKNOWN")
            ? """{ "ok": false, "error": "user_not_found" }"""
            : """
              { "ok": true, "user": { "id": "U2", "name": "sarah",
                "profile": { "display_name": "Sarah Whitfield",
                             "real_name": "Sarah Whitfield",
                             "image_72": "https://img/72.png" } } }
              """,

        // CPRIVATE exercises the not-a-member gate.
        "/api/conversations.info" => body.Contains("CPRIVATE")
            ? """
              { "ok": true, "channel": { "id": "CPRIVATE", "name": "private-elsewhere",
                "is_member": false, "is_im": false, "is_mpim": false } }
              """
            : """
              { "ok": true, "channel": { "id": "C1", "name": "general",
                "is_member": true, "is_im": false, "is_mpim": false } }
              """,

        "/api/users.list" => Mode == Behavior.UsersListFail
            ? """{ "ok": false, "error": "ratelimited" }"""
            : UsersListPage(body),

        "/api/dnd.info" => DndUntil is { } until
            ? $$"""{ "ok": true, "snooze_enabled": true, "snooze_endtime": {{until.ToUnixTimeSeconds()}} }"""
            : """{ "ok": true, "dnd_enabled": false, "snooze_enabled": false }""",

        _ => """{ "ok": false, "error": "unknown_method" }""",
    };

    private string UsersListPage(string body)
    {
        Interlocked.Increment(ref _usersListCalls);

        // Second page when the request carries a cursor; first page otherwise.
        // Page 1 includes a bot, a deleted user, and USLACKBOT - all must be filtered out.
        return body.Contains("cursor=")
            ? """
              { "ok": true, "members": [
                  { "id": "U3", "name": "alex",
                    "profile": { "display_name": "Alex Chen", "real_name": "Alexandra Chen" } }
                ],
                "response_metadata": { "next_cursor": "" } }
              """
            : """
              { "ok": true, "members": [
                  { "id": "U2", "name": "sarah",
                    "profile": { "display_name": "Sarah Whitfield", "real_name": "Sarah Whitfield",
                                 "image_72": "https://img/72.png" } },
                  { "id": "UBOT", "name": "botsy", "is_bot": true, "profile": {} },
                  { "id": "UDEL", "name": "gone", "deleted": true, "profile": {} },
                  { "id": "USLACKBOT", "name": "slackbot", "profile": {} }
                ],
                "response_metadata": { "next_cursor": "page2" } }
              """;
    }

    // ------------------------------------------------------------------
    // HTTP plumbing
    // ------------------------------------------------------------------

    private static async Task<(string Path, Dictionary<string, string> Headers, string Body)?>
        ReadRequestAsync(Stream stream)
    {
        // Read the head byte-by-byte until the blank line (loopback: speed is irrelevant).
        var head = new StringBuilder();
        var buffer = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                return null; // client closed the connection
            head.Append((char)buffer[0]);
        }

        var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var path = lines[0].Split(' ')[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var body = string.Empty;
        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, out var length) && length > 0)
        {
            var bytes = new byte[length];
            var got = 0;
            while (got < length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(got, length - got));
                if (read == 0)
                    return null;
                got += read;
            }
            body = Encoding.UTF8.GetString(bytes);
        }

        return (path, headers, body);
    }

    private static async Task WriteJsonResponseAsync(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                   $"Content-Length: {payload.Length}\r\nConnection: keep-alive\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(payload);
    }

    // ------------------------------------------------------------------
    // WebSocket plumbing
    // ------------------------------------------------------------------

    private static async Task CompleteWebSocketHandshakeAsync(
        Stream stream, Dictionary<string, string> headers)
    {
        var key = headers["Sec-WebSocket-Key"];
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
                       $"Connection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    }

    /// <summary>Reads (masked) client frames, surfacing the first text payload for assertions.</summary>
    private async Task ReadClientFramesAsync(Stream stream)
    {
        while (!_cts.IsCancellationRequested)
        {
            var header = await ReadExactAsync(stream, 2);
            if (header is null)
                return;

            var opcode = header[0] & 0x0F;
            var masked = (header[1] & 0x80) != 0;
            var length = header[1] & 0x7F;
            if (length == 126)
            {
                var extended = await ReadExactAsync(stream, 2);
                if (extended is null)
                    return;
                length = (extended[0] << 8) | extended[1];
            }

            var mask = masked ? await ReadExactAsync(stream, 4) : null;
            var payload = length > 0 ? await ReadExactAsync(stream, length) : [];
            if (payload is null || (masked && mask is null))
                return;

            if (mask is not null)
                for (var i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i % 4];

            if (opcode == 0x1) // text
                _firstClientFrame.TrySetResult(Encoding.UTF8.GetString(payload));
            else if (opcode == 0x8) // close
                return;
        }
    }

    private async Task<byte[]?> ReadExactAsync(Stream stream, int count)
    {
        var bytes = new byte[count];
        var got = 0;
        while (got < count)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(got, count - got), _cts.Token);
            if (read == 0)
                return null;
            got += read;
        }
        return bytes;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
