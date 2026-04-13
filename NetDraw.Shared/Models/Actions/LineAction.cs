using Newtonsoft.Json;
using NetDraw.Shared.Models;

namespace NetDraw.Shared.Models.Actions;

/// <summary>
/// Straight line drawing action.
/// </summary>
public class LineAction : DrawActionBase
{
    [JsonProperty("type")]
    public override string Type => "line";

    [JsonProperty("startX")]
    public double StartX { get; set; }

    [JsonProperty("startY")]
    public double StartY { get; set; }

    [JsonProperty("endX")]
    public double EndX { get; set; }

    [JsonProperty("endY")]
    public double EndY { get; set; }

    [JsonProperty("hasArrow")]
    public bool HasArrow { get; set; }
}
