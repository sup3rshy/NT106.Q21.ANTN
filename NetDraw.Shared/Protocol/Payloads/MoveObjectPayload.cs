using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.MoveObject"/> messages.
/// </summary>
public class MoveObjectPayload : IPayload
{
    [JsonProperty("actionId")]
    public string ActionId { get; set; } = string.Empty;

    [JsonProperty("deltaX")]
    public double DeltaX { get; set; }

    [JsonProperty("deltaY")]
    public double DeltaY { get; set; }
}
