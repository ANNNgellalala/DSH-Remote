using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// Tolerant parsers for the documented Remote result values. Fields are read by name with fallbacks and
/// unknown fields are ignored, so new server releases do not break the phone client.
/// </summary>
internal static class DshParsing
{
    /// <summary>Parses <c>session/list</c> → <c>{items:[SessionSummary]}</c>.</summary>
    public static IReadOnlyList<SessionSummary> ParseSessionList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/list result has no items array");
        }

        var sessions = new List<SessionSummary>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var sessionId = GetString(item, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            var projections = TryGetObject(item, "projections");
            var values = projections is { } p ? TryGetObject(p, "values") : null;

            var title = values is { } v ? GetString(v, "title") : null;
            var workingDirectory = values is { } v2 ? GetString(v2, "workingDirectory") : null;
            var model = values is { } v3 ? GetStringFromPath(v3, "modelSelection", "lastUsed", "model")
                          ?? GetStringFromPath(v3, "modelSelection", "next", "model")
                          : null;
            var modelSelection = TryReadModelSelection(values);
            var metadata = values is { } v4 ? TryGetObject(v4, "sessionListMetadata") : null;

            var updatedAt = GetInt64(item, "updatedAt") ?? 0;
            var blank = GetBool(item, "blank")
                        ?? (metadata is { } m ? GetBool(m, "blank") : null)
                        ?? false;
            var lastPromptAt = metadata is { } m2 ? GetInt64(m2, "lastPromptAt") : null;

            sessions.Add(new SessionSummary(
                SessionId: sessionId!,
                Title: title,
                Cwd: GetString(item, "cwd") ?? workingDirectory,
                Model: model,
                Running: GetBool(item, "running") ?? false,
                AgentAvailable: GetBool(item, "agentAvailable") ?? false,
                Blank: blank,
                UpdatedAt: updatedAt,
                LastPromptAt: lastPromptAt ?? updatedAt,
                ParentSessionId: GetString(item, "parentSessionId"),
                Origin: GetString(item, "origin"),
                ModelSelection: modelSelection));
        }

        return sessions;
    }

    /// <summary>Parses <c>session/events</c> → <c>{cursor,records:[{type:'event',event}],hasMore}</c>.</summary>
    public static SessionHistoryPage ParseHistoryPage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/events result must be an object");
        }

        var cursor = GetInt64(value, "cursor") ?? 0;
        var hasMore = GetBool(value, "hasMore") ?? false;
        var records = new List<SessionEventRecord>();
        if (value.TryGetProperty("records", out var recordsElement) && recordsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in recordsElement.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var recordType = GetString(record, "type");
                if (!string.Equals(recordType, "event", StringComparison.Ordinal))
                {
                    // Unknown record kinds (e.g. assistant-stream deltas) are not durable SessionEvents.
                    continue;
                }

                if (TryGetObject(record, "event") is { } eventElement)
                {
                    var parsed = ParseEvent(eventElement);
                    if (parsed is not null)
                    {
                        records.Add(parsed);
                        if (parsed.Seq > cursor)
                        {
                            cursor = parsed.Seq;
                        }
                    }
                }
            }
        }

        return new SessionHistoryPage(records, cursor, hasMore);
    }

    /// <summary>Parses one SessionEvent envelope (<c>{type,seq,time,data,…}</c>).</summary>
    public static SessionEventRecord? ParseEvent(JsonElement eventElement)
    {
        if (eventElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetString(eventElement, "type");
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        var data = eventElement.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : default;
        var isSurface = SessionEventRecord.IsSurfaceEventType(type!);
        var (surfaceOp, replaceSpan) = isSurface
            ? ParseSurfaceOp(eventElement)
            : (SurfaceOpKind.None, (SurfaceReplaceSpan?)null);
        return new SessionEventRecord(
            Seq: GetInt64(eventElement, "seq") ?? 0,
            Time: GetInt64(eventElement, "time") ?? 0,
            Type: type!,
            Data: data,
            RawJson: eventElement.GetRawText())
        {
            IsSurfaceEvent = isSurface,
            SurfaceOp = surfaceOp,
            ReplaceSpan = replaceSpan,
            SourceEventSeqs = ParseSeqList(eventElement, "sourceEventSeqs"),
            Ignorable = GetBool(eventElement, "ignorable") == true
        };
    }

    /// <summary>
    /// Parses one raw envelope line back into a record (cache round-trip path). Malformed lines return
    /// <c>null</c> instead of throwing so a damaged cache entry cannot break a session open.
    /// </summary>
    internal static SessionEventRecord? ParseRecord(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return ParseEvent(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the conditional <c>surfaceOp</c> envelope field. Unknown shapes are treated as
    /// <see cref="SurfaceOpKind.Replace"/>: a payload that is not an explicit append must never be
    /// promoted to a new transcript row (DSH's own <c>isReplacementSurfaceEvent</c> uses the same rule).
    /// </summary>
    private static (SurfaceOpKind Kind, SurfaceReplaceSpan? Span) ParseSurfaceOp(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("surfaceOp", out var op) ||
            op.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            // Older host format: no surface metadata; surface events are treated as appends.
            return (SurfaceOpKind.None, null);
        }

        if (op.ValueKind == JsonValueKind.String)
        {
            return string.Equals(op.GetString(), "append", StringComparison.Ordinal)
                ? (SurfaceOpKind.Append, null)
                : (SurfaceOpKind.Replace, null);
        }

        if (op.ValueKind == JsonValueKind.Object)
        {
            var start = GetInt64(op, "startSeq");
            var end = GetInt64(op, "endSeq");
            if (string.Equals(GetString(op, "op"), "replace", StringComparison.Ordinal) &&
                start is not null && end is not null)
            {
                return (SurfaceOpKind.Replace, new SurfaceReplaceSpan(start.Value, end.Value));
            }

            return (SurfaceOpKind.Replace, null);
        }

        return (SurfaceOpKind.Replace, null);
    }

    /// <summary>Reads an optional numeric array field (<c>sourceEventSeqs</c>).</summary>
    private static IReadOnlyList<long>? ParseSeqList(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<long>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var seq))
            {
                list.Add(seq);
            }
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Parses one <c>session/observeEvent</c> item value. Returns false for the opening
    /// <c>{"type":"ready"}</c> frame; returns true with an event for <c>{"type":"event",event}</c>.
    /// </summary>
    public static bool TryParseObserveValue(JsonElement value, out SessionEventRecord record)
    {
        record = null!;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = GetString(value, "type");
        if (string.Equals(type, "ready", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(type, "event", StringComparison.Ordinal) && TryGetObject(value, "event") is { } eventElement)
        {
            var parsed = ParseEvent(eventElement);
            if (parsed is not null)
            {
                record = parsed;
                return true;
            }

            return false;
        }

        // Tolerate a server that hands the raw event envelope as the item value.
        if (value.TryGetProperty("seq", out _) && value.TryGetProperty("data", out _) && !string.IsNullOrEmpty(type))
        {
            var raw = ParseEvent(value);
            if (raw is not null)
            {
                record = raw;
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses <c>session/state</c> → <c>{cursor,active,running,queue,pendingInput}</c>.</summary>
    public static SessionRuntimeState ParseState(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/state result must be an object");
        }

        var queue = new List<QueuedPrompt>();
        if (value.TryGetProperty("queue", out var queueElement) && queueElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in queueElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(item, "id");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var placement = GetString(item, "placement") ?? "queued";
                var text = item.TryGetProperty("content", out var content) ? ContentText(content) : null;
                queue.Add(new QueuedPrompt(id, placement, text));
            }
        }

        var pending = new List<string>();
        if (value.TryGetProperty("pendingInput", out var pendingElement) && pendingElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in pendingElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } text)
                {
                    pending.Add(text);
                }
            }
        }

        return new SessionRuntimeState(
            Cursor: GetInt64(value, "cursor") ?? 0,
            Active: GetBool(value, "active") ?? false,
            Running: GetBool(value, "running") ?? false,
            Queue: queue,
            PendingInput: pending);
    }

    /// <summary>Joins the <c>text</c> content blocks of a content array; null when there is none.</summary>
    public static string? ContentText(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                GetString(part, "type") == "text" &&
                GetString(part, "text") is { } text)
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    // ---- model catalog / selection ------------------------------------------------------------------

    /// <summary>
    /// Parses <c>session/modelCatalog</c> → <c>{default,routableProviders,groups,failures}</c>.
    /// Leaf fields are tolerated (missing optional values become null/empty, unknown fields are ignored and a
    /// malformed model row is skipped), but the core shape — an object with a <c>groups</c> array of groups
    /// carrying an <c>id</c> and a <c>models</c> array — is required and throws
    /// <see cref="DshRemoteException.Codes.ResultInvalid"/> when broken.
    /// </summary>
    public static DshModelCatalog ParseModelCatalog(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/modelCatalog result must be an object");
        }

        if (!value.TryGetProperty("groups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/modelCatalog result has no groups array");
        }

        DshModelSelection? defaultSelection = null;
        if (TryGetObject(value, "default") is { } defaultElement &&
            TryParseModelSelection(defaultElement, out var parsedDefault))
        {
            defaultSelection = parsedDefault;
        }

        var routableProviders = new List<string>();
        if (value.TryGetProperty("routableProviders", out var providersElement) && providersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var providerElement in providersElement.EnumerateArray())
            {
                if (providerElement.ValueKind == JsonValueKind.String &&
                    providerElement.GetString() is { Length: > 0 } providerId)
                {
                    routableProviders.Add(providerId);
                }
            }
        }

        var groups = new List<DshModelGroup>(groupsElement.GetArrayLength());
        foreach (var groupElement in groupsElement.EnumerateArray())
        {
            groups.Add(ParseModelGroup(groupElement));
        }

        var failures = new List<DshModelFailure>();
        if (value.TryGetProperty("failures", out var failuresElement) && failuresElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var failureElement in failuresElement.EnumerateArray())
            {
                if (failureElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(failureElement, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                failures.Add(new DshModelFailure(
                    id!,
                    GetString(failureElement, "name") ?? id!,
                    GetString(failureElement, "message") ?? string.Empty));
            }
        }

        return new DshModelCatalog(defaultSelection, routableProviders, groups, failures);
    }

    /// <summary>
    /// Parses <c>session/selectModel</c> → <c>{selected:{provider,model,reasoningEffort?}}</c>.
    /// Unlike <see cref="TryParseModelSelection"/>, a missing/invalid <c>selected</c> is an error: the server
    /// always echoes the normalised selection on success.
    /// </summary>
    public static DshModelSelection ParseSelectModelResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || TryGetObject(value, "selected") is not { } selected)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.ResultInvalid,
                "session/selectModel result has no selected object");
        }

        if (!TryParseModelSelection(selected, out var selection) || selection is null)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.ResultInvalid,
                "session/selectModel selected value has no provider/model");
        }

        return selection;
    }

    /// <summary>
    /// Tolerant reader for a <c>{provider,model,reasoningEffort?}</c> selection (used by the catalog default,
    /// the <c>selected</c> result and the <c>projections.values.modelSelection</c> next/lastUsed entries).
    /// Returns false (and <c>null</c>) when the value is not an object or lacks a provider/model string;
    /// <c>reasoningEffort</c> is null when absent or blank (adapter default).
    /// </summary>
    public static bool TryParseModelSelection(JsonElement? value, out DshModelSelection? selection)
    {
        selection = null;
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var provider = GetString(element, "provider");
        var model = GetString(element, "model");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var effort = GetString(element, "reasoningEffort");
        selection = new DshModelSelection(
            provider!,
            model!,
            string.IsNullOrWhiteSpace(effort) ? null : effort);
        return true;
    }

    /// <summary>
    /// Reads <c>projections.values.modelSelection</c>; the wire view is <c>next ?? lastUsed</c> (both null for a
    /// brand-new session, in which case this returns null and the caller falls back to the catalog default).
    /// </summary>
    internal static DshModelSelection? TryReadModelSelection(JsonElement? projectionsValues)
    {
        if (projectionsValues is not JsonElement values ||
            values.ValueKind != JsonValueKind.Object ||
            TryGetObject(values, "modelSelection") is not { } modelSelection)
        {
            return null;
        }

        if (modelSelection.TryGetProperty("next", out var next) &&
            TryParseModelSelection(next, out var nextSelection))
        {
            return nextSelection;
        }

        if (modelSelection.TryGetProperty("lastUsed", out var lastUsed) &&
            TryParseModelSelection(lastUsed, out var lastUsedSelection))
        {
            return lastUsedSelection;
        }

        return null;
    }

    private static DshModelGroup ParseModelGroup(JsonElement groupElement)
    {
        if (groupElement.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/modelCatalog group must be an object");
        }

        var id = GetString(groupElement, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/modelCatalog group has no id");
        }

        if (!groupElement.TryGetProperty("models", out var modelsElement) || modelsElement.ValueKind != JsonValueKind.Array)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.ResultInvalid,
                $"session/modelCatalog group '{id}' has no models array");
        }

        var models = new List<DshModelDescriptor>(modelsElement.GetArrayLength());
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            if (TryParseModelDescriptor(modelElement) is { } descriptor)
            {
                models.Add(descriptor);
            }
        }

        return new DshModelGroup(id!, GetString(groupElement, "name") ?? id!, models);
    }

    private static DshModelDescriptor? TryParseModelDescriptor(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new DshModelDescriptor(
            id!,
            GetString(element, "name") ?? id!,
            GetString(element, "description"),
            TryParseModelReasoning(element),
            TryParseModelBadge(element));
    }

    private static DshModelReasoning? TryParseModelReasoning(JsonElement modelElement)
    {
        if (TryGetObject(modelElement, "reasoning") is not { } reasoning)
        {
            return null;
        }

        var efforts = new List<DshModelEffort>();
        if (reasoning.TryGetProperty("efforts", out var effortsElement) && effortsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var effortElement in effortsElement.EnumerateArray())
            {
                if (effortElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(effortElement, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                efforts.Add(new DshModelEffort(
                    id!,
                    GetString(effortElement, "name") ?? id!,
                    GetString(effortElement, "description")));
            }
        }

        var defaultEffort = GetString(reasoning, "defaultEffort");
        return new DshModelReasoning(efforts, string.IsNullOrWhiteSpace(defaultEffort) ? null : defaultEffort);
    }

    private static DshModelBadge? TryParseModelBadge(JsonElement modelElement)
    {
        if (TryGetObject(modelElement, "badge") is not { } badge)
        {
            return null;
        }

        var text = ParseLocalizedText(badge, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tone = GetString(badge, "tone");
        return new DshModelBadge(text!, string.IsNullOrWhiteSpace(tone) ? "neutral" : tone!);
    }

    /// <summary>
    /// Reads a badge text that the wire types as a union: a literal string, or an i18n object such as
    /// <c>{en:"New",zh:"新"}</c>. Prefers <c>zh</c> (including <c>zh-*</c>), then <c>en</c>, then any language.
    /// </summary>
    private static string? ParseLocalizedText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var literal = value.GetString();
            return string.IsNullOrWhiteSpace(literal) ? null : literal;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? english = null;
        string? any = null;
        foreach (var entry in value.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String ||
                entry.Value.GetString() is not { Length: > 0 } text ||
                string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (entry.Name.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            if (entry.Name.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                english ??= text;
                continue;
            }

            any ??= text;
        }

        return english ?? any;
    }

    // ---- small JSON helpers -------------------------------------------------------------------------

    internal static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static JsonElement? TryGetObject(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    internal static long? GetInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    internal static bool? GetBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? GetStringFromPath(JsonElement element, params string[] path)
    {
        var current = element;
        for (var i = 0; i < path.Length; i++)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[i], out var next))
            {
                return null;
            }

            if (i == path.Length - 1)
            {
                return next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            }

            current = next;
        }

        return null;
    }
}
