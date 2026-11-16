using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// Phone-side client for the relay-owned compact session list endpoint
/// (<c>GET /__relay/sessions/summary</c>, relay 0.6.0+) plus the follow-baseline seed used by the App's local
/// session cache.
/// </summary>
/// <remarks>
/// The compact route is served by the relay plugin itself rather than the official DSH Remote RPC namespace,
/// so it reuses the pinned TLS <see cref="HttpClient"/> and authenticates with the pairing token as a bearer
/// credential — the same pattern as the ACL client in <c>DshClient.Acl.cs</c>. Every outcome that means "this
/// relay does not speak the compact schema" (old relay 404/405, a non-JSON error page, an unknown schema, a
/// malformed payload) falls back to the official <c>session/list</c> RPC and never throws; only a transport
/// failure surfaces to the caller.
/// </remarks>
public sealed partial class DshClient
{
    /// <summary>
    /// Relay-owned compact list route. Relative to the connection base address (host:port root), like the
    /// ACL routes.
    /// </summary>
    private const string SessionSummaryPath = "/__relay/sessions/summary";

    /// <summary>Parsed compact payload; <c>null</c> from the fetch helper means "fall back to session/list".</summary>
    private sealed record CompactSessionList(
        IReadOnlyList<SessionSummary> Items,
        string? Cursor,
        int TotalCount,
        int HiddenSubagentCount);

    /// <inheritdoc />
    public async Task<SessionListSnapshot> ListSessionSummariesAsync(CancellationToken cancellationToken = default)
    {
        var compact = await TryFetchCompactSessionListAsync(cancellationToken).ConfigureAwait(false);
        if (compact is not null)
        {
            _options.Log?.Invoke(
                $"session summary: compact {compact.Items.Count}/{compact.TotalCount} items "
                + $"(hidden subagents {compact.HiddenSubagentCount}, cursor {compact.Cursor ?? "none"})");
            return new SessionListSnapshot(
                compact.Items,
                compact.Cursor,
                UsedCompactEndpoint: true,
                SchemaVersion: 1,
                TotalCount: compact.TotalCount,
                HiddenSubagentCount: compact.HiddenSubagentCount);
        }

        // Old relay (or an unusable payload): the official RPC still works, so the list keeps loading.
        var items = await ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        _options.Log?.Invoke($"session summary: falling back to session/list ({items.Count} items)");
        return new SessionListSnapshot(
            items,
            Cursor: null,
            UsedCompactEndpoint: false,
            SchemaVersion: 0,
            TotalCount: items.Count);
    }

    /// <summary>Seeds the follow baseline from the App's local cache (never regresses an existing baseline).</summary>
    public void SeedFollowBaseline(string sessionId, long lastSeq, long cursor)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        }

        // The baseline feeds the pump's "events after this seq" repair, so it must only move forwards: a
        // stale local cache seeding an older position must not make the pump re-deliver events.
        _followBaselines.AddOrUpdate(
            sessionId,
            _ => new FollowBaseline(lastSeq, cursor),
            (_, existing) => new FollowBaseline(
                Math.Max(existing.LastSeq, lastSeq),
                Math.Max(existing.Cursor, cursor)));
    }

    /// <inheritdoc />
    public bool TryGetFollowBaseline(string sessionId, out long lastSeq, out long cursor)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            _followBaselines.TryGetValue(sessionId, out var baseline))
        {
            lastSeq = baseline.LastSeq;
            cursor = baseline.Cursor;
            return true;
        }

        // -1 is the client's "no baseline yet" sentinel: the next follow snapshot then emits its records.
        lastSeq = -1;
        cursor = -1;
        return false;
    }

    // ---- compact list fetch/parse -------------------------------------------------------------------

    private async Task<CompactSessionList?> TryFetchCompactSessionListAsync(CancellationToken cancellationToken)
    {
        var (connection, _) = await WaitForConnectionAsync(-1, cancellationToken).ConfigureAwait(false);
        var pairing = Pairing;
        if (pairing is null)
        {
            _options.Log?.Invoke("session summary: no pairing credential; falling back to session/list");
            return null;
        }

        var http = GetHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.BaseUri, SessionSummaryPath));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + pairing.Token);
        // The public gateway routes a request to a desktop by its dsh-auth cookie; without it the gateway
        // answers 404 unknown-session before the relay's compact list is ever reached. The relay itself
        // authenticates with the bearer above, so both hops are covered (same pattern as the ACL client).
        request.Headers.TryAddWithoutValidation("Cookie", connection.CookieHeader);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            // Only a lost transport is a caller-visible failure; CallAsync resolves the same condition by
            // dropping the generation and surfacing Transport, so do the same here.
            SignalTransportFailure(ex.Message);
            throw new DshRemoteException(
                DshRemoteException.Codes.Transport,
                $"compact session list failed on the transport: {ex.Message}",
                null,
                ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Old relay: 404/405. Auth mismatch: 401/403. Upstream hiccup: 5xx. All of them mean "use
                // the official list instead" — a response cannot be repaired by retrying on this route.
                _options.Log?.Invoke($"session summary: compact endpoint answered HTTP {(int)response.StatusCode}");
                return null;
            }

            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SignalTransportFailure(ex.Message);
                throw new DshRemoteException(
                    DshRemoteException.Codes.Transport,
                    $"compact session list body read failed: {ex.Message}",
                    null,
                    ex);
            }

            return ParseCompactSessionList(text);
        }
    }

    /// <summary>
    /// Tolerant parser for the compact envelope. Returns <c>null</c> (never throws) for anything that is not a
    /// schema-1 object with an items array, so the caller can fall back to <c>session/list</c>.
    /// </summary>
    private CompactSessionList? ParseCompactSessionList(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _options.Log?.Invoke("session summary: compact payload is not a JSON object");
                return null;
            }

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                _options.Log?.Invoke("session summary: compact endpoint returned ok=false");
                return null;
            }

            var schema = DshParsing.GetInt64(root, "schema") ?? 0;
            if (schema != 1)
            {
                _options.Log?.Invoke($"session summary: unsupported compact schema {schema}");
                return null;
            }

            if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            {
                _options.Log?.Invoke("session summary: compact payload has no items array");
                return null;
            }

            var items = new List<SessionSummary>(itemsElement.GetArrayLength());
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var sessionId = DshParsing.GetString(item, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    continue;
                }

                // Unknown fields are ignored and every known field is optional: a missing boolean is false,
                // a missing number is 0 and a missing string is null.
                items.Add(new SessionSummary(
                    SessionId: sessionId!,
                    Title: DshParsing.GetString(item, "title"),
                    Cwd: DshParsing.GetString(item, "cwd"),
                    Model: DshParsing.GetString(item, "model"),
                    Running: DshParsing.GetBool(item, "running") ?? false,
                    AgentAvailable: DshParsing.GetBool(item, "agentAvailable") ?? false,
                    Blank: DshParsing.GetBool(item, "blank") ?? false,
                    UpdatedAt: DshParsing.GetInt64(item, "updatedAt") ?? 0,
                    LastPromptAt: DshParsing.GetInt64(item, "lastPromptAt") ?? 0,
                    ParentSessionId: DshParsing.GetString(item, "parentSessionId"),
                    Origin: DshParsing.GetString(item, "origin")));
            }

            var counts = DshParsing.TryGetObject(root, "counts");
            var totalCount = counts is { } total
                ? ClampCount(DshParsing.GetInt64(total, "total") ?? 0)
                : 0;
            var hiddenSubagentCount = counts is { } hidden
                ? ClampCount(DshParsing.GetInt64(hidden, "hiddenSubagents") ?? 0)
                : 0;

            return new CompactSessionList(items, ReadCursor(root, "cursor"), totalCount, hiddenSubagentCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes JsonException (bad JSON), and any unexpected shape surfaced while walking the tree.
            _options.Log?.Invoke($"session summary: compact payload could not be parsed ({ex.Message})");
            return null;
        }
    }

    private static int ClampCount(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static string? ReadCursor(JsonElement root, string property) =>
        !root.TryGetProperty(property, out var value)
            ? null
            : value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
}
