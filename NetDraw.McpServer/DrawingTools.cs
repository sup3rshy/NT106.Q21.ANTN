using System.ComponentModel;
using ModelContextProtocol.Server;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using Newtonsoft.Json;

namespace NetDraw.McpServer;

/// <summary>
/// MCP tools exposed to the drawing host (NetDraw.Server → Claude).
///
/// Each tool returns a JSON-serialized <see cref="DrawActionBase"/> subclass.
/// The host (AiOrchestrator) collects every tool result from Claude's transcript
/// and deserializes them polymorphically using <see cref="DrawActionConverter"/>.
///
/// Coordinates are in canvas pixels, origin (0,0) top-left.
/// Colors are hex strings "#RRGGBB".
/// </summary>
[McpServerToolType]
public static class DrawingTools
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    // ─── Shape primitives ──────────────────────────────────────────────────────

    [McpServerTool, Description("Draw a rectangle. (x, y) is the TOP-LEFT corner.")]
    public static string DrawRectangle(
        [Description("Top-left X in pixels")] double x,
        [Description("Top-left Y in pixels")] double y,
        [Description("Width in pixels")] double width,
        [Description("Height in pixels")] double height,
        [Description("Stroke color as #RRGGBB")] string color,
        [Description("Fill color as #RRGGBB, or null for outline only")] string? fillColor = null,
        [Description("Stroke width in pixels (default 2)")] double strokeWidth = 2)
        => Ser(Shape(ShapeType.Rect, x, y, width, height, color, fillColor, strokeWidth));

    [McpServerTool, Description("Draw an ellipse inside the bounding box (x, y, width, height).")]
    public static string DrawEllipse(
        double x, double y, double width, double height,
        string color, string? fillColor = null, double strokeWidth = 2)
        => Ser(Shape(ShapeType.Ellipse, x, y, width, height, color, fillColor, strokeWidth));

    [McpServerTool, Description("Draw a circle. (cx, cy) is the CENTER.")]
    public static string DrawCircle(
        [Description("Center X")] double cx,
        [Description("Center Y")] double cy,
        [Description("Radius in pixels")] double radius,
        string color, string? fillColor = null, double strokeWidth = 2)
    {
        double d = radius * 2;
        return Ser(Shape(ShapeType.Circle, cx - radius, cy - radius, d, d, color, fillColor, strokeWidth));
    }

    [McpServerTool, Description("Draw a triangle inside the bounding box (x, y, width, height).")]
    public static string DrawTriangle(
        double x, double y, double width, double height,
        string color, string? fillColor = null, double strokeWidth = 2)
        => Ser(Shape(ShapeType.Triangle, x, y, width, height, color, fillColor, strokeWidth));

    [McpServerTool, Description("Draw a 5-point star inside the bounding box (x, y, width, height).")]
    public static string DrawStar(
        double x, double y, double width, double height,
        string color, string? fillColor = null, double strokeWidth = 2)
        => Ser(Shape(ShapeType.Star, x, y, width, height, color, fillColor, strokeWidth));

    // ─── Line / Pen ────────────────────────────────────────────────────────────

    [McpServerTool, Description("Draw a straight line from (x1,y1) to (x2,y2). Optionally with an arrowhead at (x2,y2).")]
    public static string DrawLine(
        double x1, double y1, double x2, double y2,
        string color,
        [Description("Stroke width (default 2)")] double strokeWidth = 2,
        [Description("Draw arrowhead at the end point")] bool hasArrow = false)
        => Ser(new LineAction
        {
            StartX = x1, StartY = y1, EndX = x2, EndY = y2,
            HasArrow = hasArrow,
            Color = color, StrokeWidth = strokeWidth
        });

    [McpServerTool, Description("Draw a freehand path through a sequence of points. Useful for curves, hair, paths, outlines.")]
    public static string DrawPath(
        [Description("Alternating x,y coordinates: [x1, y1, x2, y2, ...]. At least 2 points (4 numbers).")] double[] coordinates,
        string color,
        double strokeWidth = 2)
    {
        if (coordinates.Length < 4 || coordinates.Length % 2 != 0)
            throw new ArgumentException("coordinates must contain an even number of values >= 4");
        var points = new List<PointData>(coordinates.Length / 2);
        for (int i = 0; i < coordinates.Length; i += 2)
            points.Add(new PointData(coordinates[i], coordinates[i + 1]));
        return Ser(new PenAction
        {
            Points = points,
            Color = color, StrokeWidth = strokeWidth,
            PenStyle = PenStyle.Normal
        });
    }

    // ─── Text ──────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Draw text at (x, y). (x, y) is the top-left of the text block.")]
    public static string DrawText(
        double x, double y,
        string text,
        string color = "#000000",
        double fontSize = 18,
        bool isBold = false,
        bool isItalic = false)
        => Ser(new TextAction
        {
            X = x, Y = y,
            Text = text,
            Color = color,
            FontSize = fontSize,
            IsBold = isBold,
            IsItalic = isItalic
        });

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static ShapeAction Shape(
        ShapeType type, double x, double y, double w, double h,
        string color, string? fill, double strokeWidth) => new()
    {
        ShapeType = type,
        X = x, Y = y, Width = w, Height = h,
        Color = color, FillColor = fill, StrokeWidth = strokeWidth
    };

    private static string Ser(DrawActionBase action)
        => JsonConvert.SerializeObject(action, JsonSettings);
}
