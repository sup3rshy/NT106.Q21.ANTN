using Newtonsoft.Json;
using NetDraw.Shared.Models;

namespace NetDraw.Shared.Models.Actions;

/// <summary>
/// Geometric shape drawing action.
/// </summary>
public class ShapeAction : DrawActionBase
{
    [JsonProperty("type")]
    public override string Type => "shape";

    [JsonProperty("shapeType")]
    public ShapeType ShapeType { get; set; } = ShapeType.Rect;

    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    [JsonProperty("width")]
    public double Width { get; set; }

    [JsonProperty("height")]
    public double Height { get; set; }

    [JsonProperty("fillColor", NullValueHandling = NullValueHandling.Ignore)]
    public string? FillColor { get; set; }
}

/// <summary>
/// Supported geometric shape types.
/// </summary>
public enum ShapeType
{
    Rect,
    Ellipse,
    Circle,
    Triangle,
    Star
}
