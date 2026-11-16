using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// The official Connection unary-RPC wire (see <c>dsh-client-connection</c> <c>createWebConnectionRpc</c>):
/// <code>
/// POST /api/&lt;namespace&gt;/&lt;method&gt;
/// {"type":"client-request","rpcId":"&lt;uuid&gt;","method":"&lt;ns&gt;/&lt;method&gt;","payload":{"args":{...}}}
/// →
/// {"type":"server-response","rpcId":"&lt;uuid&gt;","result":{"ok":true,"value":...}}
/// {"type":"server-response","rpcId":"&lt;uuid&gt;","result":{"ok":false,"error":{"code","message","details"}}}
/// </code>
/// The payload must hold exactly one plain-object <c>args</c> field — the gateway rejects anything else
/// (<c>remoteRequest()</c>).
/// </summary>
internal static class RpcWire
{
    public const string ClientRequestType = "client-request";
    public const string ServerResponseType = "server-response";

    /// <summary>Builds the exact UTF-8 body of one unary request and returns the minted rpcId.</summary>
    public static byte[] BuildClientRequest(string method, JsonElement args, out string rpcId)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("RPC method is required", nameof(method));
        }

        rpcId = Guid.NewGuid().ToString("D");
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", ClientRequestType);
            writer.WriteString("rpcId", rpcId);
            writer.WriteString("method", method);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WritePropertyName("args");
            if (args.ValueKind == JsonValueKind.Object)
            {
                args.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Parses a <c>server-response</c> envelope, validates the rpcId echo, and returns the success value.
    /// Throws <see cref="DshRemoteException"/> for a failure envelope or a malformed document.
    /// </summary>
    public static JsonElement ParseServerResponse(string body, string expectedRpcId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "server response was not valid JSON", null, ex);
        }

        using (document)
        {
            return ParseServerResponse(document.RootElement, expectedRpcId);
        }
    }

    /// <summary>Same as <see cref="ParseServerResponse(string, string)"/> over an already parsed envelope.</summary>
    public static JsonElement ParseServerResponse(JsonElement root, string expectedRpcId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "server response envelope must be an object");
        }

        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != ServerResponseType)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "server response envelope has an unexpected type");
        }

        if (!root.TryGetProperty("rpcId", out var rpcId) || rpcId.ValueKind != JsonValueKind.String)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "server response envelope is missing rpcId");
        }

        if (!string.Equals(rpcId.GetString(), expectedRpcId, StringComparison.Ordinal))
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.RpcIdMismatch,
                $"rpcId mismatch: sent '{expectedRpcId}', got '{rpcId.GetString()}'");
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "server response is missing a result object");
        }

        if (!result.TryGetProperty("ok", out var ok) || (ok.ValueKind != JsonValueKind.True && ok.ValueKind != JsonValueKind.False))
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "server response result is missing a boolean ok flag");
        }

        if (ok.ValueKind == JsonValueKind.True)
        {
            return result.TryGetProperty("value", out var value) ? value.Clone() : default;
        }

        throw CreateError(result);
    }

    /// <summary>Maps the <c>result.error</c> object of a failure envelope to an exception.</summary>
    public static DshRemoteException CreateError(JsonElement resultOrError)
    {
        var error = resultOrError;
        if (error.TryGetProperty("error", out var inner))
        {
            error = inner;
        }

        var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
            ? codeElement.GetString() ?? "gateway/internal"
            : "gateway/internal";
        var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString() ?? code
            : code;
        JsonElement? details = error.TryGetProperty("details", out var detailsElement) ? detailsElement.Clone() : null;
        return new DshRemoteException(code, message, details);
    }

    /// <summary>Builds the URL path for one endpoint: <c>api/&lt;namespace&gt;/&lt;method&gt;</c>.</summary>
    public static string ApiPath(string method)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Contains("..", StringComparison.Ordinal) || method.StartsWith('/'))
        {
            throw new ArgumentException($"invalid RPC endpoint '{method}'", nameof(method));
        }

        return "api/" + method;
    }
}
