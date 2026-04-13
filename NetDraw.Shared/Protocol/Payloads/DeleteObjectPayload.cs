using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.DeleteObject"/> messages.
/// </summary>
public class DeleteObjectPayload : IPayload
{
    [JsonProperty("actionId")]
    public string ActionId { get; set; } = string.Empty;
}
