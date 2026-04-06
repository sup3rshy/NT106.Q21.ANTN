using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.Shared.Protocol;

/// <summary>
/// Message chung cho toàn bộ giao tiếp Client-Server
/// Được serialize thành JSON + "\n" delimiter khi truyền qua TCP
/// </summary>
public class NetMessage
{
    [JsonProperty("type")]
    public MessageType Type { get; set; }

    [JsonProperty("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonProperty("senderName")]
    public string SenderName { get; set; } = string.Empty;

    [JsonProperty("roomId")]
    public string RoomId { get; set; } = string.Empty;

    [JsonProperty("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonProperty("payload")]
    public JObject? Payload { get; set; }

    public string Serialize()
    {
        return JsonConvert.SerializeObject(this, Formatting.None) + "\n";
    }

    public static NetMessage? Deserialize(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<NetMessage>(json.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tạo message nhanh với payload
    /// </summary>
    public static NetMessage Create(MessageType type, string senderId, string senderName, string roomId, object? payloadObj = null)
    {
        var msg = new NetMessage
        {
            Type = type,
            SenderId = senderId,
            SenderName = senderName,
            RoomId = roomId
        };

        if (payloadObj != null)
        {
            msg.Payload = JObject.FromObject(payloadObj);
        }

        return msg;
    }
}
