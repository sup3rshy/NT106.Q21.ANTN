using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.Shared.Protocol;

/// <summary>
/// Generic, strongly-typed protocol message for all Client-Server communication.
/// Serialized as JSON + "\n" delimiter when transmitted over TCP.
/// </summary>
/// <typeparam name="T">The payload type, which must implement <see cref="IPayload"/>.</typeparam>
public class NetMessage<T> where T : IPayload
{
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Converters = { new DrawActionConverter() },
        NullValueHandling = NullValueHandling.Ignore
    };

    [JsonProperty("version")]
    public int Version { get; set; } = 0;

    [JsonProperty("type")]
    public MessageType Type { get; set; }

    [JsonProperty("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonProperty("senderName")]
    public string SenderName { get; set; } = string.Empty;

    [JsonProperty("roomId")]
    public string RoomId { get; set; } = string.Empty;

    [JsonProperty("sessionToken")]
    public string SessionToken { get; set; } = string.Empty;

    [JsonProperty("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonProperty("payload")]
    public T? Payload { get; set; }

    /// <summary>
    /// Serialize this message to a JSON string terminated with a newline delimiter.
    /// </summary>
    public string Serialize()
    {
        return JsonConvert.SerializeObject(this, Formatting.None, SerializerSettings) + "\n";
    }

    /// <summary>
    /// Factory method to create a new message with the given envelope fields and optional payload.
    /// </summary>
    public static NetMessage<T> Create(
        MessageType type,
        string senderId,
        string senderName,
        string roomId,
        T? payload = default)
    {
        return new NetMessage<T>
        {
            Version = ProtocolVersion.Current,
            Type = type,
            SenderId = senderId,
            SenderName = senderName,
            RoomId = roomId,
            Payload = payload
        };
    }
}
