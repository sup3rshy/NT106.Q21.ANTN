using Newtonsoft.Json;

namespace NetDraw.Shared.Models;

/// <summary>
/// Một hành động vẽ trên canvas
/// </summary>
public class DrawAction
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonProperty("tool")]
    public DrawTool Tool { get; set; }

    [JsonProperty("points")]
    public List<PointData> Points { get; set; } = new();

    [JsonProperty("color")]
    public string Color { get; set; } = "#000000";

    [JsonProperty("strokeWidth")]
    public double StrokeWidth { get; set; } = 2;

    [JsonProperty("fillColor")]
    public string? FillColor { get; set; }

    // Cho hình shape
    [JsonProperty("shapeType")]
    public ShapeType? ShapeType { get; set; }

    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    [JsonProperty("width")]
    public double Width { get; set; }

    [JsonProperty("height")]
    public double Height { get; set; }

    [JsonProperty("radius")]
    public double Radius { get; set; }

    // Cho text
    [JsonProperty("text")]
    public string? Text { get; set; }

    [JsonProperty("fontSize")]
    public double FontSize { get; set; } = 14;

    // Cho eraser
    [JsonProperty("eraserSize")]
    public double EraserSize { get; set; } = 20;
}

public class PointData
{
    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    public PointData() { }

    public PointData(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public enum DrawTool
{
    Pen,
    Line,
    Shape,
    Text,
    Eraser
}

public enum ShapeType
{
    Rectangle,
    Ellipse,
    Circle,
    Triangle,
    Star
}
