namespace DshRemote.Core;

/// <summary>
/// Raised by <see cref="ISessionFollowClient.FollowEventsAsync"/> when a reconnect left a sequence hole that
/// the durable log can no longer repair (history pruned past the last seen seq). It is a recoverable signal:
/// the consumer should call <see cref="ISessionFollowClient.OpenSessionAsync"/> again for a fresh snapshot,
/// replace/merge its local history with that baseline, and resubscribe.
/// </summary>
public sealed class SessionFollowResyncException : DshRemoteException
{
    public SessionFollowResyncException(string sessionId, long lastSeq, long throughSeq)
        : base(
            "session/follow-resync",
            $"session '{sessionId}' has an unrecoverable event gap after seq {lastSeq} (through {throughSeq}); "
            + "open a fresh snapshot to resync")
    {
        SessionId = sessionId;
        LastSeq = lastSeq;
        ThroughSeq = throughSeq;
    }

    /// <summary>The session whose stream needs a fresh baseline.</summary>
    public string SessionId { get; }

    /// <summary>The last seq the consumer can trust.</summary>
    public long LastSeq { get; }

    /// <summary>The cut the server could still report when the gap was detected.</summary>
    public long ThroughSeq { get; }
}
