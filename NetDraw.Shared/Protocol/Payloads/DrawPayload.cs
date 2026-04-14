using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.Draw"/> and <see cref="MessageType.DrawPreview"/> messages.
/// </summary>
public class DrawPayload : IPayload
{
    [JsonProperty("action")]
    public DrawActionBase Action { get; set; } = null!;
}
