using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.UserJoined"/> and <see cref="MessageType.UserLeft"/> messages.
/// </summary>
public class UserPayload : IPayload
{
    [JsonProperty("user")]
    public UserInfo User { get; set; } = null!;
}
