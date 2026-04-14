using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.CanvasSnapshot"/> messages.
/// Contains the full list of drawing actions representing the current canvas state.
/// </summary>
public class SnapshotPayload : IPayload
{
    [JsonProperty("actions")]
    public List<DrawActionBase> Actions { get; set; } = new();
}
