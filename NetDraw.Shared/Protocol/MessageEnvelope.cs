using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.Shared.Protocol;

/// <summary>
/// Utilities for parsing protocol messages in two phases:
/// (1) envelope-only parsing (no payload deserialization),
/// (2) typed payload deserialization on demand.
///
/// This allows the server to route messages by <see cref="MessageType"/>
/// without knowing the concrete payload type upfront.
/// </summary>
public static class MessageEnvelope
{
    private static readonly JsonSerializerSettings DeserializerSettings = new()
    {
        Converters = { new DrawActionConverter() }
    };

    /// <summary>
    /// Parsed envelope fields without a deserialized payload.
    /// </summary>
    public record Envelope(
        MessageType Type,
        string SenderId,
        string SenderName,
        string RoomId,
        long Timestamp,
        JObject? RawPayload,
        int Version,
        string SessionToken);

    /// <summary>
    /// Parse only the envelope fields from a JSON message string.
    /// The payload is kept as a raw <see cref="JObject"/> for deferred deserialization.
    /// Returns <c>null</c> if the JSON is malformed or missing required fields.
    /// </summary>
    public static Envelope? Parse(string json)
    {
        try
        {
            var jObject = JObject.Parse(json.Trim());

            var typeToken = jObject["type"];
            if (typeToken is null)
                return null;

            if (!Enum.TryParse<MessageType>(typeToken.Value<string>(), ignoreCase: false, out var type)
                && !Enum.TryParse<MessageType>(typeToken.Value<int>().ToString(), out type))
            {
                return null;
            }

            var senderId     = jObject["senderId"]?.Value<string>()     ?? string.Empty;
            var senderName   = jObject["senderName"]?.Value<string>()   ?? string.Empty;
            var roomId       = jObject["roomId"]?.Value<string>()       ?? string.Empty;
            var timestamp    = jObject["timestamp"]?.Value<long>()      ?? 0L;
            var version      = jObject["version"]?.Value<int>()         ?? 0;
            var sessionToken = jObject["sessionToken"]?.Value<string>() ?? string.Empty;
            var rawPayload   = jObject["payload"] as JObject;

            return new Envelope(type, senderId, senderName, roomId, timestamp, rawPayload, version, sessionToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deserialize a raw <see cref="JObject"/> payload into the specified typed payload.
    /// Uses <see cref="DrawActionConverter"/> so that draw payloads resolve correctly.
    /// Returns <c>default</c> if <paramref name="rawPayload"/> is <c>null</c>.
    /// </summary>
    public static T? DeserializePayload<T>(JObject? rawPayload) where T : IPayload
    {
        if (rawPayload is null)
            return default;

        var serializer = JsonSerializer.Create(DeserializerSettings);
        return rawPayload.ToObject<T>(serializer);
    }

    /// <summary>
    /// Full deserialization of a JSON message string into a strongly-typed <see cref="NetMessage{T}"/>.
    /// Returns <c>null</c> if parsing fails.
    /// </summary>
    public static NetMessage<T>? Deserialize<T>(string json) where T : IPayload
    {
        try
        {
            return JsonConvert.DeserializeObject<NetMessage<T>>(json.Trim(), DeserializerSettings);
        }
        catch
        {
            return null;
        }
    }
}
