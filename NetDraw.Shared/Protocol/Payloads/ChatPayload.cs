using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.ChatMessage"/> messages.
/// </summary>
public class ChatPayload : IPayload
{
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("isSystem")]
    public bool IsSystem { get; set; }
}
