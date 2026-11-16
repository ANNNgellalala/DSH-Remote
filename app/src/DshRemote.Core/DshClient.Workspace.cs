namespace DshRemote.Core;

/// <summary>
/// M5: workspace listing/archive/create and image attachment reads.
/// </summary>
/// <remarks>
/// <c>workspace/follow</c> is a stream with no parameters: opening it yields one complete
/// <c>baseline</c> frame, then ordered increments. The phone UI only needs the snapshot, so
/// <see cref="GetWorkspaceStateAsync"/> opens the stream, reads the baseline and closes it.
/// </remarks>
public sealed partial class DshClient
{
    /// <inheritdoc />
    public async Task<DshWorkspaceState> GetWorkspaceStateAsync(CancellationToken cancellationToken = default)
    {
        var (mux, _) = await WaitForMuxAsync(-1, cancellationToken).ConfigureAwait(false);
        var enumerator = mux.OpenAsync(MuxWire.WorkspaceFollowEndpoint, DshArgs.WorkspaceFollow(), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var frame = WorkspaceParsing.ParseFrame(enumerator.Current);
                if (frame is { State: not null } &&
                    frame.Kind is WorkspaceFrameKind.Baseline or WorkspaceFrameKind.Replace)
                {
                    return frame.State;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        throw new DshRemoteException(
            DshRemoteException.Codes.ResultInvalid,
            "workspace/follow ended before its opening baseline");
    }

    /// <inheritdoc />
    public async Task<DshWorkspaceState> ArchiveSessionAsync(
        string sessionId,
        bool stopActivity = false,
        CancellationToken cancellationToken = default)
    {
        var value = await CallAsync(
            "workspace/archiveSession",
            DshArgs.ArchiveSession(sessionId, stopActivity),
            cancellationToken).ConfigureAwait(false);
        // Keep server failures (`workspace/session-active`, `workspace/…`) intact; CallAsync already maps them.
        _ = WorkspaceParsing.ParseArchivedIds(value);
        return await GetWorkspaceStateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DshWorkspaceState> UnarchiveSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var value = await CallAsync(
            "workspace/unarchiveSession",
            DshArgs.UnarchiveSession(sessionId),
            cancellationToken).ConfigureAwait(false);
        _ = WorkspaceParsing.ParseArchivedIds(value);
        return await GetWorkspaceStateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> CreateSessionAsync(
        string? workspaceId = null,
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        var value = await CallAsync("session/create", DshArgs.CreateSession(workspaceId, cwd), cancellationToken)
            .ConfigureAwait(false);
        return WorkspaceParsing.ParseCreatedSessionId(value);
    }

    /// <inheritdoc />
    public async Task<DshImageData> GetImageAsync(
        string sessionId,
        DshImageRef image,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(image);
        var value = await CallAsync(
            "session/attachment",
            DshArgs.AttachmentRequest(sessionId, image),
            cancellationToken).ConfigureAwait(false);
        return WorkspaceParsing.ParseAttachmentResult(value, image);
    }
}
