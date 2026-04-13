using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.CursorMove"/> messages.
/// </summary>
public class CursorPayload : IPayload
{
    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    [JsonProperty("color")]
    public string Color { get; set; } = "#000000";
}
