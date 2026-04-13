using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.AiCommand"/> messages.
/// </summary>
public class AiCommandPayload : IPayload
{
    [JsonProperty("prompt")]
    public string Prompt { get; set; } = string.Empty;
}
