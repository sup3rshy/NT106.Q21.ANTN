using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.RoomJoined"/> messages.
/// Carries room info, drawing history, and current user list.
/// </summary>
public class RoomJoinedPayload : IPayload
{
    [JsonProperty("room")]
    public RoomInfo Room { get; set; } = null!;

    [JsonProperty("history")]
    public List<DrawActionBase> History { get; set; } = new();

    [JsonProperty("users")]
    public List<UserInfo> Users { get; set; } = new();
}
