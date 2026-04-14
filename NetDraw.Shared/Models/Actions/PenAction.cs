using Newtonsoft.Json;
using NetDraw.Shared.Models;

namespace NetDraw.Shared.Models.Actions;

/// <summary>
/// Freehand pen drawing action.
/// </summary>
public class PenAction : DrawActionBase
{
    [JsonProperty("type")]
    public override string Type => "pen";

    [JsonProperty("points")]
    public List<PointData> Points { get; set; } = new();

    [JsonProperty("penStyle")]
    public PenStyle PenStyle { get; set; } = PenStyle.Normal;
}

/// <summary>
/// Style variant for the pen tool.
/// </summary>
public enum PenStyle
{
    Normal,
    Calligraphy,
    Highlighter,
    Spray
}
