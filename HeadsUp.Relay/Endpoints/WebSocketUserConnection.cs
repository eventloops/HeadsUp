using System.Net.WebSockets;
using System.Text;
using HeadsUp.Relay.Routing;

namespace HeadsUp.Relay.Endpoints;

/// <summary>
/// One connected desktop, over a WebSocket.
///
/// <see cref="UserId"/> comes from the relay-issued session validated at handshake time — never
/// from anything the client sends — which is what stops a client choosing whose events it
/// receives.
/// </summary>
public sealed class WebSocketUserConnection : IUserConnection
{
    private readonly WebSocket _socket;
    // Sends are serialized: concurrent writes to one WebSocket corrupt the frame stream, and
    // routing can fan several events to the same connection at once.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketUserConnection(string userId, WebSocket socket)
    {
        UserId = userId;
        _socket = socket;
    }

    public string UserId { get; }

    public async Task SendAsync(string payload, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Holds the request open until the client goes away, since the registration is scoped to
    /// the request. Nothing the desktop sends is read as instruction — the relay only pushes.
    /// </summary>
    public async Task WaitUntilClosedAsync(CancellationToken ct)
    {
        var buffer = new byte[1024];

        try
        {
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                // Anything else is discarded: this channel is push-only by design.
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (WebSocketException)
        {
            // Client vanished; the registration is disposed by the caller either way.
        }
    }
}
