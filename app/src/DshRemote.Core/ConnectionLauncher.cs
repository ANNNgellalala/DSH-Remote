using System.Net;
using System.Net.WebSockets;

namespace DshRemote.Core;

/// <summary>A successful <c>/connect</c> + WebSocket upgrade: the cookie to reuse and the live mux socket.</summary>
internal sealed record LaunchedConnection(Uri BaseUri, string CookieHeader, IRemoteSocket Socket);

/// <summary>
/// Performs one physical connection generation: <c>GET /connect</c> (owner API cookie), then the
/// <c>/api/remote.mux</c> WebSocket upgrade with that cookie. Test seams replace this.
/// </summary>
internal interface IConnectionLauncher
{
    Task<LaunchedConnection> LaunchAsync(
        HttpClient http,
        Uri baseUri,
        DshPairingInfo pairing,
        CancellationToken cancellationToken);
}

/// <summary>Default launcher over the real HTTP/WebSocket wire.</summary>
internal sealed class HttpConnectionLauncher : IConnectionLauncher
{
    private readonly Action<string>? _log;

    public HttpConnectionLauncher(Action<string>? log = null)
    {
        _log = log;
    }

    public async Task<LaunchedConnection> LaunchAsync(
        HttpClient http,
        Uri baseUri,
        DshPairingInfo pairing,
        CancellationToken cancellationToken)
    {
        // M2: answering the forwarded `$events` waterfall requires an interactions=answer Peer; the relay
        // forwards and the host replays pending waterfall requests to it after every /connect.
        var connectUri = new Uri(
            baseUri,
            "connect?token=" + Uri.EscapeDataString(pairing.Token) + "&interactions=answer");
        using var response = await http.GetAsync(connectUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new DshRemoteException(
                    DshRemoteException.Codes.Unauthorized,
                    "GET /connect was rejected with 401 (launch token invalid or expired)");
            case HttpStatusCode.Forbidden:
                throw new DshRemoteException(
                    DshRemoteException.Codes.Forbidden,
                    "GET /connect was rejected with 403 (Host/Origin not trusted by the desktop)");
        }

        if ((int)response.StatusCode != 302)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.ConnectFailed,
                $"GET /connect answered HTTP {(int)response.StatusCode}, expected 302");
        }

        var cookie = ExtractCookieHeader(response) ??
                     throw new DshRemoteException(
                         DshRemoteException.Codes.ConnectFailed,
                         "GET /connect returned 302 without a Set-Cookie API credential");

        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Cookie", cookie);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.RemoteCertificateValidationCallback = TlsPinning.CreateValidationCallback(pairing, _log);
            var muxUri = ToWebSocketUri(baseUri, MuxWire.RemoteMuxPath);
            await socket.ConnectAsync(muxUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new DshRemoteException(
                DshRemoteException.Codes.Transport,
                $"WebSocket upgrade at {ToWebSocketUri(baseUri, MuxWire.RemoteMuxPath)} failed: {ex.Message}",
                null,
                ex);
        }

        _log?.Invoke($"connected: {baseUri} (cookie {Mask(cookie)})");
        return new LaunchedConnection(baseUri, cookie, new ClientWebSocketRemoteSocket(socket));
    }

    /// <summary>Maps an HTTP(S) base URI onto the matching ws(s) URI for the mux path.</summary>
    internal static Uri ToWebSocketUri(Uri baseUri, string path)
    {
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/" + path.TrimStart('/')
        };
        return builder.Uri;
    }

    /// <summary>Extracts the <c>name=value</c> API credential from the <c>/connect</c> Set-Cookie header.</summary>
    internal static string? ExtractCookieHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var header in values)
        {
            var parsed = ParseSetCookie(header);
            if (!string.IsNullOrEmpty(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>Parses the first <c>name=value</c> pair of one <c>Set-Cookie</c> header, dropping attributes.</summary>
    internal static string? ParseSetCookie(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var firstSegment = header.Split(';', 2)[0].Trim();
        var separator = firstSegment.IndexOf('=');
        if (separator <= 0 || separator == firstSegment.Length - 1)
        {
            return null;
        }

        var name = firstSegment[..separator].Trim();
        if (CookieAttributeNames.Contains(name))
        {
            return null;
        }

        return firstSegment;
    }

    private static readonly HashSet<string> CookieAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path", "Domain", "Max-Age", "Expires", "HttpOnly", "Secure", "SameSite", "Priority", "Partitioned"
    };

    private static string Mask(string cookie)
    {
        var separator = cookie.IndexOf('=');
        if (separator < 0 || cookie.Length <= separator + 12)
        {
            return "***";
        }

        return cookie[..(separator + 6)] + "…" + cookie[^4..];
    }
}
