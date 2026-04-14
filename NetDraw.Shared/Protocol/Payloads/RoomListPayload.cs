using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.RoomList"/> messages.
/// </summary>
public class RoomListPayload : IPayload
{
    [JsonProperty("rooms")]
    public List<RoomInfo> Rooms { get; set; } = new();
}
