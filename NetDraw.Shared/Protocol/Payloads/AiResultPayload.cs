using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.AiResult"/> messages.
/// </summary>
public class AiResultPayload : IPayload
{
    [JsonProperty("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonProperty("actions")]
    public List<DrawActionBase> Actions { get; set; } = new();

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }
}
