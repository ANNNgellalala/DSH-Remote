using System.Net.WebSockets;
using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// The Gateway WebSocket logical-stream mux frames (see <c>dsh-api-gateway</c> <c>stream-protocol.ts</c>):
/// client → host <c>open/item/end/cancel</c>, host → client <c>item/end/error</c>.
/// A stream's <c>open.payload</c> is the same <c>{args:...}</c> wrapper as a unary RPC payload.
/// </summary>
internal static class MuxWire
{
    public const string RemoteMuxPath = "api/remote.mux";
    public const string ObserveEventEndpoint = "session/observeEvent";
    public const string FollowEndpoint = "session/follow";
    public const string WorkspaceFollowEndpoint = "workspace/follow";
    public const string EventsEndpoint = "$events";
    public const string EventsResultMethod = "$events/result";

    /// <summary><c>{"type":"open","streamId":"1","endpoint":"...","payload":{"args":{...}}}</c>.</summary>
    public static string BuildOpen(long streamId, string endpoint, JsonElement args)
    {
        using var buffer = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "open");
            writer.WriteString("streamId", streamId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("endpoint", endpoint);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WritePropertyName("args");
            if (args.ValueKind == JsonValueKind.Object)
            {
                args.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary><c>{"type":"end","streamId":"1"}</c>.</summary>
    public static string BuildEnd(long streamId) => BuildSimple("end", streamId);

    /// <summary><c>{"type":"cancel","streamId":"1"}</c>.</summary>
    public static string BuildCancel(long streamId) => BuildSimple("cancel", streamId);

    private static string BuildSimple(string type, long streamId)
    {
        using var buffer = new MemoryStream(96);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("streamId", streamId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads a numeric logical stream id that may arrive as a JSON number or a numeric string.</summary>
    public static bool TryGetStreamId(JsonElement frame, out long streamId)
    {
        streamId = 0;
        if (!frame.TryGetProperty("streamId", out var id))
        {
            return false;
        }

        if (id.ValueKind == JsonValueKind.Number)
        {
            return id.TryGetInt64(out streamId);
        }

        return id.ValueKind == JsonValueKind.String &&
               long.TryParse(id.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out streamId);
    }

    /// <summary>Reads the <c>type</c> discriminator of one mux frame.</summary>
    public static string? TypeOf(JsonElement frame) =>
        frame.ValueKind == JsonValueKind.Object &&
        frame.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    /// <summary>Reads (and clones) the optional <c>value</c> field of an <c>item</c> frame.</summary>
    public static JsonElement ItemValue(JsonElement frame) =>
        frame.TryGetProperty("value", out var value) ? value.Clone() : default;

    /// <summary>Builds the error carried by a server <c>error</c> frame.</summary>
    public static DshRemoteException ParseErrorFrame(JsonElement frame)
    {
        if (frame.TryGetProperty("error", out var error))
        {
            return RpcWire.CreateError(error);
        }

        return new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "stream error frame without an error object");
    }
}

/// <summary>Minimal transport seam over one WebSocket, so the mux and the state machine are testable.</summary>
internal interface IRemoteSocket : IAsyncDisposable
{
    /// <summary>Sends one complete text frame.</summary>
    ValueTask SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Receives one complete text frame; returns <c>null</c> when the socket closed.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}

/// <summary><see cref="IRemoteSocket"/> over a real <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientWebSocketRemoteSocket : IRemoteSocket
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public ClientWebSocketRemoteSocket(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream(4 * 1024);
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > 16 * 1024 * 1024)
            {
                throw new DshRemoteException(DshRemoteException.Codes.Transport, "remote mux frame exceeded 16 MiB");
            }

            if (result.EndOfMessage)
            {
                return System.Text.Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closing", timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort — the transport is going away either way.
        }
        finally
        {
            _socket.Dispose();
            _sendLock.Dispose();
        }
    }
}
