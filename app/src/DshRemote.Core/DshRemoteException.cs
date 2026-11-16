using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// A failure reported on the official DSH Remote wire (RPC error envelope, stream error frame,
/// `/connect` rejection, or transport loss). <see cref="Code"/> keeps the server code when one exists.
/// </summary>
public class DshRemoteException : Exception
{
    /// <summary>Well-known codes this client produces locally.</summary>
    public static class Codes
    {
        /// <summary>No live connection (never connected, disconnected, or reconnecting forever).</summary>
        public const string Transport = "gateway/transport";

        /// <summary>The launch token was rejected by `GET /connect` (HTTP 401).</summary>
        public const string Unauthorized = "connect/unauthorized";

        /// <summary>The host/authority was rejected by the desktop trust fence (HTTP 403).</summary>
        public const string Forbidden = "connect/forbidden";

        /// <summary>`/connect` answered something other than the documented 302.</summary>
        public const string ConnectFailed = "connect/failed";

        /// <summary>The response envelope (or a `result.value`) did not match the documented shape.</summary>
        public const string ResultInvalid = "gateway/result-invalid";

        /// <summary>The server response rpcId did not echo the request rpcId.</summary>
        public const string RpcIdMismatch = "gateway/rpc-id-mismatch";

        /// <summary>
        /// The endpoint does not exist on the connected host: HTTP 404 for the RPC path, or a gateway
        /// "unknown endpoint" code. This is a capability signal (old DSH build), not a transport failure —
        /// the UI hides features that need it instead of reporting "cannot reach the relay".
        /// </summary>
        public const string EndpointUnavailable = "gateway/endpoint-unavailable";

        /// <summary>
        /// A <c>session/follow</c> subscription for the same session is still registered. The old stream may
        /// be tearing down (its pump flushes a cancel frame); consumers should treat this as retryable, not
        /// terminal — <see cref="DshClient.FollowEventsAsync"/> first waits for the slot to drain.
        /// </summary>
        public const string FollowBusy = "session/follow-busy";
    }

    public DshRemoteException(string code, string message, JsonElement? details = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    /// <summary>Server error code (e.g. <c>gateway/input-invalid</c>, <c>session/not-found</c>).</summary>
    public string Code { get; }

    /// <summary>Optional structural details carried by the server error envelope.</summary>
    public JsonElement? Details { get; }

    /// <summary>True when the failure means the physical connection is gone and a reconnect is required.</summary>
    public bool IsTransportFailure =>
        Code is Codes.Transport or Codes.Unauthorized or Codes.Forbidden or Codes.ConnectFailed;
}
