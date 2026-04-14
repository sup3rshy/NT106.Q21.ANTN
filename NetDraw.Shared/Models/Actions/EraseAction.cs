using Newtonsoft.Json;
using NetDraw.Shared.Models;

namespace NetDraw.Shared.Models.Actions;

/// <summary>
/// Eraser action that removes strokes along a path.
/// </summary>
public class EraseAction : DrawActionBase
{
    [JsonProperty("type")]
    public override string Type => "erase";

    [JsonProperty("points")]
    public List<PointData> Points { get; set; } = new();

    [JsonProperty("eraserSize")]
    public double EraserSize { get; set; } = 20;
}
