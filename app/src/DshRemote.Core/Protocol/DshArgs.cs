using System.Text.Json;
using System.Text.Json.Nodes;

namespace DshRemote.Core;

/// <summary>
/// Named-argument builders for the Remote endpoints the phone uses. Every object below is the exact
/// <c>args</c> payload (the <c>{"args":…}</c> wrapper is added by <see cref="RpcWire"/>/<see cref="MuxWire"/>).
/// </summary>
/// <remarks>
/// The Typert descriptor names each wire parameter: <c>session/list</c> uses <c>_request</c>; every other
/// endpoint this client calls uses a single <c>request</c> parameter. The gateway's
/// <c>assertExactArguments()</c> rejects missing or extra arg names, so these wrappers are mandatory.
/// The inner objects are validated by a strict codec with <c>additionalProperties:false</c>.
/// </remarks>
internal static class DshArgs
{
    private static readonly JsonElement EmptyObject = ParseObject("{}");

    /// <summary><c>session/list</c> → <c>{"_request":{}}</c> (or with <c>cursor</c>).</summary>
    public static JsonElement List(string? cursor = null)
    {
        var request = string.IsNullOrWhiteSpace(cursor)
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["cursor"] = cursor };
        return Serialize(new Dictionary<string, object?> { ["_request"] = request });
    }

    /// <summary><c>session/events</c> → <c>{"request":{address,beforeSeq?,maxMessages}}</c>.</summary>
    public static JsonElement Events(string sessionId, long? beforeSeq, int maxMessages)
    {
        var request = new Dictionary<string, object?>
        {
            ["address"] = new Dictionary<string, object?>
            {
                ["kind"] = "session",
                ["sessionId"] = RequireId(sessionId)
            },
            ["maxMessages"] = maxMessages
        };
        if (beforeSeq.HasValue)
        {
            request["beforeSeq"] = beforeSeq.Value;
        }

        return Serialize(new Dictionary<string, object?> { ["request"] = request });
    }

    /// <summary><c>session/state</c> → <c>{"request":{sessionId}}</c>.</summary>
    public static JsonElement State(string sessionId) =>
        Serialize(new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?> { ["sessionId"] = RequireId(sessionId) }
        });

    /// <summary><c>session/prompt</c> → requestId + text content + delivery mode.</summary>
    public static JsonElement Prompt(string requestId, string sessionId, string text, string mode, string? clientTimeZone)
    {
        var request = new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["sessionId"] = RequireId(sessionId),
            ["mode"] = mode,
            ["content"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(clientTimeZone))
        {
            request["clientTimeZone"] = clientTimeZone;
        }

        return Serialize(new Dictionary<string, object?> { ["request"] = request });
    }

    /// <summary><c>session/cancel</c> → <c>{"request":{sessionId}}</c>.</summary>
    public static JsonElement Cancel(string sessionId) => State(sessionId);

    /// <summary><c>session/observeEvent</c> → <c>{"request":{address:{kind:'session',sessionId}}}</c>.</summary>
    public static JsonElement ObserveEvent(string sessionId) =>
        Serialize(new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?>
            {
                ["address"] = new Dictionary<string, object?>
                {
                    ["kind"] = "session",
                    ["sessionId"] = RequireId(sessionId)
                }
            }
        });

    /// <summary><c>$events</c> → <c>{"args":{}}</c> (gateway requires exactly an empty args object).</summary>
    public static JsonElement EventsStream() => EmptyObject;

    /// <summary>
    /// <c>session/follow</c> → <c>{"request":{address,maxMessages,assistantStream:true}}</c>.
    /// The stream's first frame is a <c>snapshot</c>; durable <c>event</c> frames follow (plus cursorless
    /// <c>assistant-stream</c> frames when enabled).
    /// </summary>
    public static JsonElement Follow(string sessionId, int maxMessages = 80, bool assistantStream = true)
    {
        var request = new Dictionary<string, object?>
        {
            ["address"] = new Dictionary<string, object?>
            {
                ["kind"] = "session",
                ["sessionId"] = RequireId(sessionId)
            },
            ["maxMessages"] = Math.Max(1, maxMessages)
        };
        if (assistantStream)
        {
            // The strict codec types this field as the literal `true`.
            request["assistantStream"] = true;
        }

        return Serialize(new Dictionary<string, object?> { ["request"] = request });
    }

    /// <summary>
    /// <c>session/updateQueue</c> → <c>{"request":{sessionId,itemId,action}}</c>.
    /// <paramref name="kind"/> is one of <c>edit|remove|steer</c>; only <c>edit</c> carries text content.
    /// </summary>
    public static JsonElement UpdateQueue(string sessionId, string itemId, string kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("itemId is required", nameof(itemId));
        }

        Dictionary<string, object?> action;
        switch (kind)
        {
            case "edit":
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ArgumentException("edit requires replacement text", nameof(text));
                }

                action = new Dictionary<string, object?>
                {
                    ["kind"] = "edit",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text
                        }
                    }
                };
                break;
            case "remove":
            case "steer":
                action = new Dictionary<string, object?> { ["kind"] = kind };
                break;
            default:
                throw new ArgumentException($"unsupported queue edit action '{kind}'", nameof(kind));
        }

        return Serialize(new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?>
            {
                ["sessionId"] = RequireId(sessionId),
                ["itemId"] = itemId,
                ["action"] = action
            }
        });
    }

    /// <summary>
    /// <c>$events/result</c> special endpoint args (no <c>request</c> wrapper):
    /// <c>{clientId,eventId,outcome:{kind:'result',value?}}</c>.
    /// </summary>
    public static JsonElement EventsResult(string clientId, string eventId, JsonElement? value)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("clientId and eventId are required");
        }

        var outcome = new Dictionary<string, object?> { ["kind"] = "result" };
        if (value.HasValue)
        {
            outcome["value"] = value.Value;
        }

        return Serialize(new Dictionary<string, object?>
        {
            ["clientId"] = clientId,
            ["eventId"] = eventId,
            ["outcome"] = outcome
        });
    }

    /// <summary>Approval outcome wire vocabulary (<c>allowed-once|rejected|cancelled</c>).</summary>
    public static JsonElement ApprovalOutcomeValue(string outcome) => Serialize(outcome);

    /// <summary>Question answer batch wire value <c>{answers:[{id,selected,custom?}]}</c>.</summary>
    public static JsonElement QuestionAnswersValue(IReadOnlyList<DshQuestionAnswerItem> answers)
    {
        var items = new object?[answers.Count];
        for (var i = 0; i < answers.Count; i++)
        {
            var answer = answers[i];
            var item = new Dictionary<string, object?>
            {
                ["id"] = answer.Id,
                ["selected"] = answer.Selected
            };
            if (!string.IsNullOrEmpty(answer.Custom))
            {
                item["custom"] = answer.Custom;
            }

            items[i] = item;
        }

        return Serialize(new Dictionary<string, object?> { ["answers"] = items });
    }

    /// <summary>Cancelled question batch wire value <c>{cancelled:true}</c>.</summary>
    public static JsonElement QuestionCancelledValue() =>
        Serialize(new Dictionary<string, object?> { ["cancelled"] = true });

    // ---- M5: workspace, session creation, image prompts and attachments ------------------------------

    /// <summary>An empty <c>args</c> object (stream endpoints whose descriptor declares no parameters).</summary>
    public static JsonElement EmptyArgs() => EmptyObject;

    /// <summary><c>workspace/follow</c> → <c>{"args":{}}</c> (its descriptor declares no parameters).</summary>
    public static JsonElement WorkspaceFollow() => EmptyObject;

    /// <summary><c>workspace/archiveSession</c> → <c>{"request":{sessionId,stopActivity?}}</c>.</summary>
    public static JsonElement ArchiveSession(string sessionId, bool stopActivity)
    {
        var request = new Dictionary<string, object?> { ["sessionId"] = RequireId(sessionId) };
        if (stopActivity)
        {
            request["stopActivity"] = true;
        }

        return Serialize(new Dictionary<string, object?> { ["request"] = request });
    }

    /// <summary><c>workspace/unarchiveSession</c> → <c>{"request":{sessionId}}</c>.</summary>
    public static JsonElement UnarchiveSession(string sessionId) =>
        Serialize(new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?> { ["sessionId"] = RequireId(sessionId) }
        });

    /// <summary>
    /// <c>session/create</c> → <c>{"request":{workspaceId|cwd}}</c>. <c>workspaceId</c> and <c>cwd</c> are
    /// mutually exclusive; the server picks a configured default when both are omitted.
    /// </summary>
    public static JsonElement CreateSession(string? workspaceId, string? cwd)
    {
        var hasWorkspace = !string.IsNullOrWhiteSpace(workspaceId);
        var hasCwd = !string.IsNullOrWhiteSpace(cwd);
        if (hasWorkspace && hasCwd)
        {
            throw new ArgumentException("workspaceId and cwd are mutually exclusive", nameof(cwd));
        }

        var request = new Dictionary<string, object?>();
        if (hasWorkspace)
        {
            request["workspaceId"] = workspaceId;
        }
        else if (hasCwd)
        {
            request["cwd"] = cwd;
        }

        return Serialize(new Dictionary<string, object?> { ["request"] = request });
    }

    /// <summary>
    /// <c>session/attachment</c> → <c>{"request":{sessionId,attachment:{attachmentId,mediaType,bytes,width,height,name?}}}</c>.
    /// The attachment object is a strict schema: never send the <c>data</c> field back, only the reference.
    /// </summary>
    public static JsonElement AttachmentRequest(string sessionId, DshImageRef image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (string.IsNullOrWhiteSpace(image.AttachmentId))
        {
            throw new ArgumentException("image attachmentId is required", nameof(image));
        }

        if (!ImageMediaTypes.Contains(image.MediaType))
        {
            throw new ArgumentException($"unsupported image media type '{image.MediaType}'", nameof(image));
        }

        var attachment = new Dictionary<string, object?>
        {
            ["attachmentId"] = image.AttachmentId,
            ["mediaType"] = image.MediaType,
            ["bytes"] = image.Bytes,
            ["width"] = image.Width,
            ["height"] = image.Height
        };
        if (!string.IsNullOrWhiteSpace(image.Name))
        {
            attachment["name"] = image.Name;
        }

        return Serialize(new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?>
            {
                ["sessionId"] = RequireId(sessionId),
                ["attachment"] = attachment
            }
        });
    }

    /// <summary>
    /// <c>session/prompt</c> carrying text and/or images: text block first, then one image block per attachment
    /// (<c>{type:'image',mediaType,data:&lt;base64&gt;,name?}</c>), matching the strict prompt schema.
    /// </summary>
    public static JsonElement PromptWithImages(
        string requestId,
        string sessionId,
        string? text,
        IReadOnlyList<DshImageAttachment>? images,
        string mode,
        string? clientTimeZone)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId is required", nameof(requestId));
        }

        var content = new List<object?>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = text
            });
        }

        if (images is not null)
        {
            foreach (var image in images)
            {
                if (image is null)
                {
                    throw new ArgumentException("image attachments must not be null", nameof(images));
                }

                if (!ImageMediaTypes.Contains(image.MediaType))
                {
                    throw new ArgumentException(
                        $"unsupported image media type '{image.MediaType}' (expected image/png|jpeg|webp|gif)",
                        nameof(images));
                }

                if (image.Data is null || image.Data.Length == 0)
                {
                    throw new ArgumentException("image data is empty", nameof(images));
                }

                var part = new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["mediaType"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Data)
                };
                if (!string.IsNullOrWhiteSpace(image.Name))
                {
                    part["name"] = image.Name;
                }

                content.Add(part);
            }
        }

        if (content.Count == 0)
        {
            throw new ArgumentException("a prompt needs text or at least one image", nameof(text));
        }

        var request = new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["sessionId"] = RequireId(sessionId),
            ["mode"] = mode,
            ["content"] = content
        };
        if (!string.IsNullOrWhiteSpace(clientTimeZone))
        {
            request["clientTimeZone"] = clientTimeZone;
        }

        return Serialize(new Dictionary<string, object?> { ["request"] = request });
    }

    // ---- M8: model catalog + session-local model selection -------------------------------------------

    /// <summary>
    /// <c>session/modelCatalog</c> → <c>{"args":{}}</c>. The descriptor declares no parameters and the gateway
    /// rejects a non-empty args object (<c>gateway/arguments-invalid</c>), so this must stay exactly <c>{}</c>.
    /// </summary>
    public static JsonElement ModelCatalog() => EmptyObject;

    /// <summary>
    /// <c>session/selectModel</c> → <c>{"request":{sessionId,provider,model,reasoningEffort?}}</c>.
    /// <paramref name="reasoningEffort"/> is omitted when null/blank, meaning "use the provider default";
    /// the host strips unknown inner fields silently, so only the documented keys are ever sent.
    /// </summary>
    public static JsonElement SelectModel(string sessionId, string provider, string model, string? reasoningEffort = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["sessionId"] = RequireId(sessionId),
            ["provider"] = RequireArgument(provider, nameof(provider)),
            ["model"] = RequireArgument(model, nameof(model))
        };
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            request["reasoningEffort"] = reasoningEffort;
        }

        return Serialize(new Dictionary<string, object?> { ["request"] = request });
    }

    private static readonly HashSet<string> ImageMediaTypes = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    private static string RequireId(string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? throw new ArgumentException("sessionId is required", nameof(sessionId)) : sessionId;

    private static string RequireArgument(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required", name) : value;

    private static JsonElement Serialize(object? value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Resolves the physical base URI for a pairing. Production pairings pin the relay certificate over HTTPS;
/// a pairing without a fingerprint is a development fallback (M0): loopback goes plain HTTP with system
/// trust, anything else stays HTTPS with system trust and must present a publicly trusted certificate.
/// </summary>
internal static class DshEndpoint
{
    public static Uri ResolveBaseUri(DshPairingInfo pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        var scheme = TlsPinning.IsUsableFingerprint(pairing.SpkiSha256)
            ? "https"
            : IsLoopbackHost(pairing.Host) ? "http" : "https";
        return new UriBuilder(scheme, pairing.Host, pairing.Port, "/").Uri;
    }

    public static bool IsLoopbackHost(string? host) =>
        host is not null &&
        (host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("[::1]", StringComparison.OrdinalIgnoreCase));
}
