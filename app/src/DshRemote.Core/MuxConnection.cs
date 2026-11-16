using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace DshRemote.Core;

/// <summary>
/// One logical-stream multiplexer over a single <c>/api/remote.mux</c> WebSocket.
/// Frames: client <c>open/item/end/cancel</c>; server <c>item/end/error</c>. The socket closing fails every
/// live stream and completes <see cref="Closed"/>; the owner then performs a fresh <c>/connect</c>.
/// </summary>
internal sealed class MuxConnection : IAsyncDisposable
{
    private readonly IRemoteSocket _socket;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, Channel<JsonElement>> _streams = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private long _nextStreamId;
    private Exception? _failure;
    private bool _disposed;

    public MuxConnection(IRemoteSocket socket, Action<string>? log = null)
    {
        _socket = socket;
        _log = log;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Completes when the socket is gone (clean close, network loss, or <see cref="Abort"/>).</summary>
    public Task Closed => _closed.Task;

    /// <summary>The cause of the loss when the mux was aborted locally or the socket failed.</summary>
    public Exception? Failure => _failure;

    /// <summary>Opens one logical stream and yields each <c>item.value</c> frame until <c>end</c>/<c>error</c>.</summary>
    public async IAsyncEnumerable<JsonElement> OpenAsync(
        string endpoint,
        JsonElement args,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streamId = Interlocked.Increment(ref _nextStreamId);
        var channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _streams[streamId] = channel;
        var terminal = false;
        try
        {
            try
            {
                await SendAsync(MuxWire.BuildOpen(streamId, endpoint, args), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new DshRemoteException(DshRemoteException.Codes.Transport, $"cannot open remote stream '{endpoint}': {ex.Message}", null, ex);
            }

            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            terminal = true;
        }
        finally
        {
            _streams.TryRemove(streamId, out _);
            if (!terminal)
            {
                // Half-close/cancel the logical stream; the physical socket may already be gone.
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendAsync(MuxWire.BuildCancel(streamId), timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    /// <summary>Fails every live stream with <paramref name="cause"/> and tears the socket down.</summary>
    public void Abort(Exception cause)
    {
        _failure ??= cause;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    /// <summary>Completes every live stream and waits for the pump to finish.</summary>
    public async Task CompleteAsync(Exception cause)
    {
        _failure ??= cause;
        _streams.Clear();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // The pump records failure itself.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Abort(new DshRemoteException(DshRemoteException.Codes.Transport, "remote mux disposed"));
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }

        await _socket.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _lifetime.Dispose();
    }

    private async Task SendAsync(string frame, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendTextAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                string? text;
                try
                {
                    text = await _socket.ReceiveTextAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _failure ??= ex;
                    break;
                }

                if (text is null)
                {
                    break;
                }

                if (!HandleFrame(text))
                {
                    break;
                }
            }
        }
        finally
        {
            var cause = _failure ?? new DshRemoteException(DshRemoteException.Codes.Transport, "remote mux closed");
            foreach (var stream in _streams)
            {
                stream.Value.Writer.TryComplete(cause);
            }

            _streams.Clear();
            _closed.TrySetResult();
        }
    }

    /// <summary>Handles one inbound text frame; returns false when the mux must stop.</summary>
    private bool HandleFrame(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            _failure ??= new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "remote mux sent invalid JSON", null, ex);
            return false;
        }

        using (document)
        {
            var frame = document.RootElement;
            var type = MuxWire.TypeOf(frame);
            if (!MuxWire.TryGetStreamId(frame, out var streamId))
            {
                return true;
            }

            if (!_streams.TryGetValue(streamId, out var channel))
            {
                return true;
            }

            switch (type)
            {
                case "item":
                    channel.Writer.TryWrite(MuxWire.ItemValue(frame));
                    return true;
                case "end":
                    _streams.TryRemove(streamId, out _);
                    channel.Writer.TryComplete();
                    return true;
                case "error":
                    _streams.TryRemove(streamId, out _);
                    channel.Writer.TryComplete(MuxWire.ParseErrorFrame(frame));
                    return true;
                default:
                    _log?.Invoke($"mux: ignored frame type '{type}' for stream {streamId}");
                    return true;
            }
        }
    }
}
