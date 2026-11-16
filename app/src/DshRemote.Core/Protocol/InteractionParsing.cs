using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// Parsers for the forwarded <c>$events</c> waterfall stream (approvals and structured user questions).
/// </summary>
/// <remarks>
/// Wire frames (after the mux <c>item.value</c> unwrap):
/// <code>
/// {"type":"ready","clientId":"…","host":{"home":"…"}}
/// {"type":"emit","event":"…","args":[…]}
/// {"type":"waterfall","event":"approval/request","eventId":"…","agentId":"session-…",
///  "request":{"toolName":"pwsh","callId":"call_…","reason":"…","displayReason":{"en":"…","zh":"…"}}}
/// {"type":"waterfall","event":"user-questions/request","eventId":"…","agentId":"session-…",
///  "request":{"questions":[{"id":"q1","question":"…","header":"…","options":[{"label":"A","description":"…"}],"multiSelect":false}]}}
/// {"type":"cancel","eventId":"…"}
/// </code>
/// </remarks>
internal static class InteractionParsing
{
    public const string ApprovalEvent = "approval/request";
    public const string QuestionEvent = "user-questions/request";

    /// <summary>Reads one <c>$events</c> value frame's discriminator.</summary>
    public static string? FrameType(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && DshParsing.GetString(value, "type") is { Length: > 0 } type
            ? type
            : null;

    /// <summary>Parses the opening <c>ready</c> frame and returns its clientId.</summary>
    public static bool TryParseReady(JsonElement value, out string clientId, out string? home)
    {
        clientId = string.Empty;
        home = null;
        if (value.ValueKind != JsonValueKind.Object ||
            !string.Equals(DshParsing.GetString(value, "type"), "ready", StringComparison.Ordinal))
        {
            return false;
        }

        clientId = DshParsing.GetString(value, "clientId") ?? string.Empty;
        if (clientId.Length == 0)
        {
            return false;
        }

        if (value.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object)
        {
            home = DshParsing.GetString(host, "home");
        }

        return true;
    }

    /// <summary>Reads the waterfall envelope fields shared by every forwarded event.</summary>
    public static bool TryParseWaterfallHeader(
        JsonElement value,
        out string eventName,
        out string eventId,
        out string agentId,
        out JsonElement request)
    {
        eventName = string.Empty;
        eventId = string.Empty;
        agentId = string.Empty;
        request = default;
        if (value.ValueKind != JsonValueKind.Object ||
            !string.Equals(DshParsing.GetString(value, "type"), "waterfall", StringComparison.Ordinal))
        {
            return false;
        }

        eventName = DshParsing.GetString(value, "event") ?? string.Empty;
        eventId = DshParsing.GetString(value, "eventId") ?? string.Empty;
        agentId = DshParsing.GetString(value, "agentId") ?? string.Empty;
        if (eventName.Length == 0 || eventId.Length == 0 || agentId.Length == 0 ||
            !value.TryGetProperty("request", out request) || request.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    /// <summary>Parses one <c>approval/request</c> waterfall frame.</summary>
    public static bool TryParseApproval(JsonElement value, out DshApprovalRequest approval)
    {
        approval = null!;
        if (!TryParseWaterfallHeader(value, out var eventName, out var eventId, out var agentId, out var request) ||
            !string.Equals(eventName, ApprovalEvent, StringComparison.Ordinal))
        {
            return false;
        }

        var toolName = DshParsing.GetString(request, "toolName") ?? "unknown";
        var reason = DshParsing.GetString(request, "reason");
        var displayReason = ResolveDisplayReason(request);
        approval = new DshApprovalRequest(
            RequestId: eventId,
            SessionId: agentId,
            ToolName: toolName,
            CallId: DshParsing.GetString(request, "callId"),
            Reason: reason,
            DisplayReason: displayReason ?? reason,
            ArgumentsJson: TryGetArgumentsJson(request),
            ExpiresAt: TryGetTimestamp(request, "expiresAt"));
        return true;
    }

    /// <summary>Parses one <c>user-questions/request</c> waterfall frame.</summary>
    public static bool TryParseQuestion(JsonElement value, out DshQuestionRequest question)
    {
        question = null!;
        if (!TryParseWaterfallHeader(value, out var eventName, out var eventId, out var agentId, out var request) ||
            !string.Equals(eventName, QuestionEvent, StringComparison.Ordinal))
        {
            return false;
        }

        var items = new List<DshQuestionItem>();
        if (request.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in questions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = DshParsing.GetString(item, "id");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var options = new List<string>();
                if (item.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionsElement.EnumerateArray())
                    {
                        if (option.ValueKind == JsonValueKind.String && option.GetString() is { } label)
                        {
                            options.Add(label);
                        }
                        else if (option.ValueKind == JsonValueKind.Object &&
                                 DshParsing.GetString(option, "label") is { } objectLabel)
                        {
                            options.Add(objectLabel);
                        }
                    }
                }

                items.Add(new DshQuestionItem(
                    Id: id,
                    Text: DshParsing.GetString(item, "question") ?? DshParsing.GetString(item, "text") ?? id,
                    Options: options,
                    AllowMultiple: DshParsing.GetBool(item, "multiSelect") ?? DshParsing.GetBool(item, "allowMultiple") ?? false,
                    // The wire answer item carries an optional `custom` string; offer free text for every question.
                    AllowFreeText: true,
                    Detail: DshParsing.GetString(item, "detail") ?? DshParsing.GetString(item, "header")));
            }
        }

        question = new DshQuestionRequest(
            RequestId: eventId,
            SessionId: agentId,
            Questions: items,
            ExpiresAt: TryGetTimestamp(request, "expiresAt"));
        return true;
    }

    /// <summary>Parses a <c>{"type":"cancel","eventId":…}</c> frame.</summary>
    public static bool TryParseCancel(JsonElement value, out string eventId)
    {
        eventId = string.Empty;
        if (value.ValueKind != JsonValueKind.Object ||
            !string.Equals(DshParsing.GetString(value, "type"), "cancel", StringComparison.Ordinal))
        {
            return false;
        }

        eventId = DshParsing.GetString(value, "eventId") ?? string.Empty;
        return eventId.Length > 0;
    }

    /// <summary>
    /// Picks a human display string from the localized <c>displayReason</c> map (zh first, then any language),
    /// tolerating a plain string from older hosts.
    /// </summary>
    public static string? ResolveDisplayReason(JsonElement request)
    {
        if (!request.TryGetProperty("displayReason", out var display))
        {
            return null;
        }

        if (display.ValueKind == JsonValueKind.String)
        {
            return display.GetString();
        }

        if (display.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (DshParsing.GetString(display, "zh") is { Length: > 0 } chinese)
        {
            return chinese;
        }

        foreach (var property in display.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? TryGetArgumentsJson(JsonElement request)
    {
        if (!request.TryGetProperty("arguments", out var arguments))
        {
            return null;
        }

        return arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : arguments.GetRawText();
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement request, string property)
    {
        if (!request.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epoch);
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
