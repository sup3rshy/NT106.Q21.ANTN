using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.Error"/> messages.
/// </summary>
public class ErrorPayload : IPayload
{
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
}
