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

    // Opacity (0.0 - 1.0)
    [JsonProperty("opacity")]
    public double Opacity { get; set; } = 1.0;

    // Dash style
    [JsonProperty("dashStyle")]
    public DashStyle DashStyle { get; set; } = DashStyle.Solid;

    // Cho Image (Base64)
    [JsonProperty("imageData", NullValueHandling = NullValueHandling.Ignore)]
    public string? ImageData { get; set; }

    // Group ID: nhóm nhiều action lại (vd: template) để undo/redo cả nhóm
    [JsonProperty("groupId", NullValueHandling = NullValueHandling.Ignore)]
    public string? GroupId { get; set; }
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
    Eraser,
    Select,
    Arrow,
    Calligraphy,  // Bút thư pháp (nét dày-mỏng theo hướng)
    Highlighter,  // Bút highlight (bán trong suốt)
    Spray,        // Bút phun sơn
    Image         // Ảnh import (Base64)
}

public enum DashStyle
{
    Solid,
    Dashed,
    Dotted
}

public enum ShapeType
{
    Rectangle,
    Ellipse,
    Circle,
    Triangle,
    Star
}
