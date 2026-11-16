using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// M8: host model catalog (<c>session/modelCatalog</c>) and session-local model/reasoning-effort selection
/// (<c>session/selectModel</c>).
/// </summary>
/// <remarks>
/// The catalog is host-level, so the client caches the parsed (or failed) result against the connection
/// generation that served it: one probe per generation, and a caller can <c>forceRefresh</c> for the picker's
/// retry button. Failures are returned with a <see cref="DshModelCatalogSupport"/> marker instead of being
/// thrown, because "old DSH has no such endpoint" must hide the UI silently while a transient failure should
/// offer a retry.
/// </remarks>
public sealed partial class DshClient
{
    /// <summary>Error codes that mean "this host does not have the endpoint", not "the network is down".</summary>
    private static readonly HashSet<string> EndpointMissingCodes = new(StringComparer.Ordinal)
    {
        DshRemoteException.Codes.EndpointUnavailable,
        "gateway/invocation-unavailable",
        "gateway/method-unavailable",
        "gateway/service-unavailable",
        "gateway/definition-unavailable"
    };

    private readonly object _modelCatalogGate = new();
    private DshModelCatalogResult? _modelCatalogCache;
    private long _modelCatalogCacheGeneration = -1;

    /// <inheritdoc />
    public async Task<DshModelCatalogResult> GetModelCatalogAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        long generation;
        try
        {
            (_, generation) = await WaitForConnectionAsync(-1, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not connected: report a retryable state instead of throwing a network error at the UI.
            return new DshModelCatalogResult(null, DshModelCatalogSupport.Unavailable, FailureMessage(ex));
        }

        lock (_modelCatalogGate)
        {
            if (!forceRefresh && _modelCatalogCache is not null && _modelCatalogCacheGeneration == generation)
            {
                return _modelCatalogCache;
            }
        }

        DshModelCatalogResult result;
        try
        {
            var value = await CallAsync("session/modelCatalog", DshArgs.ModelCatalog(), cancellationToken)
                .ConfigureAwait(false);
            result = new DshModelCatalogResult(DshParsing.ParseModelCatalog(value), DshModelCatalogSupport.Supported);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DshRemoteException ex) when (EndpointMissingCodes.Contains(ex.Code))
        {
            result = new DshModelCatalogResult(null, DshModelCatalogSupport.Unsupported, ex.Message);
        }
        catch (Exception ex)
        {
            result = new DshModelCatalogResult(null, DshModelCatalogSupport.Unavailable, FailureMessage(ex));
        }

        // Cache failures too: a generation gets exactly one probe unless the caller forces a refresh.
        lock (_modelCatalogGate)
        {
            _modelCatalogCache = result;
            _modelCatalogCacheGeneration = generation;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<DshModelSelection> SelectModelAsync(
        string sessionId,
        string provider,
        string model,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        var value = await CallAsync(
            "session/selectModel",
            DshArgs.SelectModel(sessionId, provider, model, reasoningEffort),
            cancellationToken).ConfigureAwait(false);
        return DshParsing.ParseSelectModelResult(value);
    }

    /// <summary>
    /// Reads <c>projections.values.modelSelection</c> from a follow/list payload. The wire view is
    /// <c>next ?? lastUsed</c>; returns null when the session has no recorded selection yet.
    /// </summary>
    internal static DshModelSelection? TryReadModelSelection(JsonElement projectionsValues) =>
        DshParsing.TryReadModelSelection(projectionsValues);

    private static string FailureMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
}
