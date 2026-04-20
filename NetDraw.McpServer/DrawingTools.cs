using System.ComponentModel;
using ModelContextProtocol.Server;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.McpServer;

/// <summary>
/// MCP tools exposed to the drawing host (NetDraw.Server → Claude).
///
/// Each tool returns JSON — either a single <see cref="DrawActionBase"/> or an
/// array of them. The host collects every tool result from the transcript and
/// deserializes them polymorphically via <see cref="DrawActionConverter"/>.
///
/// Design notes
/// ────────────
/// Client-side only knows four action types: <c>shape</c> (rect/ellipse/circle/
/// triangle/star, supports fill), <c>line</c>, <c>pen</c> (polyline, stroke-only,
/// supports pen styles incl. calligraphy), <c>text</c>. Every other primitive
/// here (arcs, bezier curves, polygons, rounded rects, mirrored paths, …) is
/// *synthesized* server-side: we sample the parametric curve at high density
/// and emit a <see cref="PenAction"/> that the client renders as-is. This way
/// we can grow the tool surface without touching the renderer.
///
/// Coordinates are in canvas pixels, origin (0,0) top-left. Colors are
/// "#RRGGBB". Angles are in degrees, 0° = +x (right), 90° = +y (down).
/// </summary>
[McpServerToolType]
public static class DrawingTools
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    // ─── Shape primitives (with fill) ──────────────────────────────────────────

    [McpServerTool, Description("Draw a rectangle. (x, y) is the TOP-LEFT corner. Supports solid fill.")]
    public static string DrawRectangle(
        [Description("Top-left X in pixels")] double x,
        [Description("Top-left Y in pixels")] double y,
        [Description("Width in pixels")] double width,
        [Description("Height in pixels")] double height,
        [Description("Stroke color as #RRGGBB")] string color,
        [Description("Fill color as #RRGGBB, or null for outline only")] string? fillColor = null,
        [Description("Stroke width in pixels (default 2)")] double strokeWidth = 2,
        [Description("Opacity 0..1 (default 1)")] double opacity = 1.0,
        [Description("Dash style: Solid|Dashed|Dotted (default Solid)")] string? dashStyle = null,
        [Description("Optional group id; strokes with the same id are one undoable unit.")] string? groupId = null)
        => Ser(Stamp(Shape(ShapeType.Rect, x, y, width, height, color, fillColor, strokeWidth), opacity, dashStyle, groupId));

    [McpServerTool, Description("Draw an axis-aligned ellipse inside the bounding box (x, y, width, height). Supports fill.")]
    public static string DrawEllipse(
        double x, double y, double width, double height,
        string color, string? fillColor = null, double strokeWidth = 2,
        double opacity = 1.0, string? dashStyle = null, string? groupId = null)
        => Ser(Stamp(Shape(ShapeType.Ellipse, x, y, width, height, color, fillColor, strokeWidth), opacity, dashStyle, groupId));

    [McpServerTool, Description("Draw a circle. (cx, cy) is the CENTER. Supports fill.")]
    public static string DrawCircle(
        [Description("Center X")] double cx,
        [Description("Center Y")] double cy,
        [Description("Radius in pixels")] double radius,
        string color, string? fillColor = null, double strokeWidth = 2,
        double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        double d = radius * 2;
        return Ser(Stamp(Shape(ShapeType.Circle, cx - radius, cy - radius, d, d, color, fillColor, strokeWidth), opacity, dashStyle, groupId));
    }

    [McpServerTool, Description("Draw an upward-pointing triangle inside the bounding box (x, y, width, height). Supports fill.")]
    public static string DrawTriangle(
        double x, double y, double width, double height,
        string color, string? fillColor = null, double strokeWidth = 2,
        double opacity = 1.0, string? dashStyle = null, string? groupId = null)
        => Ser(Stamp(Shape(ShapeType.Triangle, x, y, width, height, color, fillColor, strokeWidth), opacity, dashStyle, groupId));

    [McpServerTool, Description("Draw a 5-point star inside the bounding box (x, y, width, height). Supports fill.")]
    public static string DrawStar(
        double x, double y, double width, double height,
        string color, string? fillColor = null, double strokeWidth = 2,
        double opacity = 1.0, string? dashStyle = null, string? groupId = null)
        => Ser(Stamp(Shape(ShapeType.Star, x, y, width, height, color, fillColor, strokeWidth), opacity, dashStyle, groupId));

    // ─── Line / Pen ────────────────────────────────────────────────────────────

    [McpServerTool, Description("Draw a straight line from (x1,y1) to (x2,y2). Optionally with an arrowhead at (x2,y2).")]
    public static string DrawLine(
        double x1, double y1, double x2, double y2,
        string color,
        double strokeWidth = 2,
        bool hasArrow = false,
        double opacity = 1.0, string? dashStyle = null, string? groupId = null)
        => Ser(Stamp(new LineAction
        {
            StartX = x1, StartY = y1, EndX = x2, EndY = y2,
            HasArrow = hasArrow,
            Color = color, StrokeWidth = strokeWidth
        }, opacity, dashStyle, groupId));

    [McpServerTool, Description(
        "Draw a freehand polyline through a sequence of points. Good for jagged paths or pre-computed outlines. " +
        "For smooth organic curves use DrawSmoothCurve or DrawCubicBezier instead.")]
    public static string DrawPath(
        [Description("Alternating x,y coordinates: [x1, y1, x2, y2, ...]. At least 2 points (4 numbers).")] double[] coordinates,
        string color,
        double strokeWidth = 2,
        [Description("Pen style: Normal|Calligraphy|Highlighter|Spray. Calligraphy = tapered manga-style stroke.")] string? penStyle = null,
        double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        var pts = PointsFromFlat(coordinates);
        return Ser(Stamp(new PenAction
        {
            Points = pts,
            Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    // ─── Curves (sampled to PenAction) ─────────────────────────────────────────

    [McpServerTool, Description(
        "Draw a circular arc centered at (cx,cy) with given radius, from startAngle to endAngle (degrees). " +
        "Angles: 0° = right, 90° = down, 180° = left, 270° = up. Use for eyebrows, smiles, claws.")]
    public static string DrawArc(
        double cx, double cy, double radius,
        double startAngleDeg, double endAngleDeg,
        string color, double strokeWidth = 2,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
        => DrawEllipseArc(cx, cy, radius, radius, startAngleDeg, endAngleDeg, 0,
                          color, strokeWidth, penStyle, opacity, dashStyle, groupId);

    [McpServerTool, Description(
        "Draw an elliptical arc. (cx,cy)=center, rx/ry = radii, startAngle..endAngle sweep in degrees, " +
        "rotationDeg rotates the whole ellipse. Perfect for cat eyes, mouths, eyelids, curved whiskers.")]
    public static string DrawEllipseArc(
        double cx, double cy, double rx, double ry,
        double startAngleDeg, double endAngleDeg, double rotationDeg,
        string color, double strokeWidth = 2,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        var pts = SampleEllipseArc(cx, cy, rx, ry,
                                   Deg2Rad(startAngleDeg), Deg2Rad(endAngleDeg), Deg2Rad(rotationDeg),
                                   steps: Math.Max(24, (int)(Math.Abs(endAngleDeg - startAngleDeg) / 3)));
        return Ser(Stamp(new PenAction
        {
            Points = pts, Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    [McpServerTool, Description(
        "Draw a quadratic Bézier curve from (x1,y1) to (x2,y2) with one control point (cx,cy). " +
        "Simple smooth curve — good for tails, single-hump bends.")]
    public static string DrawQuadraticBezier(
        double x1, double y1, double cx, double cy, double x2, double y2,
        string color, double strokeWidth = 2,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        var pts = SampleQuadBezier(x1, y1, cx, cy, x2, y2, 48);
        return Ser(Stamp(new PenAction
        {
            Points = pts, Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    [McpServerTool, Description(
        "Draw a cubic Bézier curve from (x1,y1) to (x2,y2) with two control points. " +
        "S-shaped curves, manga hair strands, flowing tails, smile lines.")]
    public static string DrawCubicBezier(
        double x1, double y1, double c1x, double c1y, double c2x, double c2y, double x2, double y2,
        string color, double strokeWidth = 2,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        var pts = SampleCubicBezier(x1, y1, c1x, c1y, c2x, c2y, x2, y2, 64);
        return Ser(Stamp(new PenAction
        {
            Points = pts, Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    [McpServerTool, Description(
        "Draw a SMOOTH CURVE passing through all given control points (Catmull–Rom spline). " +
        "This is the fastest way to draw an organic outline (cat body, face contour, hair silhouette): " +
        "just place 4–12 key points in order and the server smooths them. " +
        "Set closed=true to close back to the first point (for a full silhouette).")]
    public static string DrawSmoothCurve(
        [Description("Alternating x,y: [x1,y1, x2,y2, ...]. At least 3 points (6 numbers).")] double[] coordinates,
        string color,
        double strokeWidth = 2,
        bool closed = false,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        var ctrl = PointsFromFlat(coordinates);
        if (ctrl.Count < 3)
            throw new ArgumentException("DrawSmoothCurve needs at least 3 control points (6 numbers).");
        var pts = SampleCatmullRom(ctrl, closed, segmentsPerSpan: 16);
        return Ser(Stamp(new PenAction
        {
            Points = pts, Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    // ─── Polygons & rounded rects (stroke-only via PenAction) ──────────────────

    [McpServerTool, Description(
        "Draw an arbitrary polygon outline through the given vertices. " +
        "Set closed=true to connect the last point back to the first. Stroke-only (no fill — " +
        "for filled convex shapes prefer DrawRectangle/Triangle/Ellipse).")]
    public static string DrawPolygon(
        [Description("Alternating x,y: [x1,y1, x2,y2, ...]. At least 3 points.")] double[] coordinates,
        string color,
        double strokeWidth = 2,
        bool closed = true,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        var pts = PointsFromFlat(coordinates);
        if (pts.Count < 3) throw new ArgumentException("DrawPolygon needs at least 3 points.");
        if (closed) pts.Add(new PointData(pts[0].X, pts[0].Y));
        return Ser(Stamp(new PenAction
        {
            Points = pts, Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    [McpServerTool, Description(
        "Draw a regular N-sided polygon centered at (cx,cy), inscribed in a circle of given radius. " +
        "sides=3 → triangle, 5 → pentagon, 6 → hexagon, 8 → octagon. rotationDeg rotates the polygon.")]
    public static string DrawRegularPolygon(
        double cx, double cy, double radius, int sides,
        string color, double rotationDeg = 0,
        double strokeWidth = 2,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        if (sides < 3) throw new ArgumentException("sides must be >= 3");
        var pts = new List<PointData>(sides + 1);
        double rot = Deg2Rad(rotationDeg) - Math.PI / 2; // start at top
        for (int i = 0; i <= sides; i++)
        {
            double a = rot + i * 2 * Math.PI / sides;
            pts.Add(new PointData(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
        }
        return Ser(Stamp(new PenAction
        {
            Points = pts, Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    [McpServerTool, Description(
        "Draw a rounded-rectangle outline at (x,y) of size (width,height) with corner radius. " +
        "Stroke-only. For filled rounded panels, layer a filled DrawRectangle underneath.")]
    public static string DrawRoundedRectangle(
        double x, double y, double width, double height, double cornerRadius,
        string color, double strokeWidth = 2,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        double r = Math.Min(cornerRadius, Math.Min(width, height) / 2);
        var pts = new List<PointData>();
        // top edge
        pts.Add(new PointData(x + r, y));
        pts.Add(new PointData(x + width - r, y));
        // top-right corner
        pts.AddRange(SampleEllipseArc(x + width - r, y + r, r, r, Deg2Rad(-90), Deg2Rad(0), 0, 10));
        // right edge
        pts.Add(new PointData(x + width, y + height - r));
        // bottom-right
        pts.AddRange(SampleEllipseArc(x + width - r, y + height - r, r, r, Deg2Rad(0), Deg2Rad(90), 0, 10));
        // bottom edge
        pts.Add(new PointData(x + r, y + height));
        // bottom-left
        pts.AddRange(SampleEllipseArc(x + r, y + height - r, r, r, Deg2Rad(90), Deg2Rad(180), 0, 10));
        // left edge
        pts.Add(new PointData(x, y + r));
        // top-left
        pts.AddRange(SampleEllipseArc(x + r, y + r, r, r, Deg2Rad(180), Deg2Rad(270), 0, 10));
        pts.Add(new PointData(x + r, y));
        return Ser(Stamp(new PenAction
        {
            Points = pts, Color = color, StrokeWidth = strokeWidth,
            PenStyle = ParsePenStyle(penStyle)
        }, opacity, dashStyle, groupId));
    }

    // ─── Symmetry helper ───────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Draw a polyline AND its mirror reflected across a vertical axis at x=axisX. " +
        "One call produces two strokes — ideal for symmetric faces, butterflies, wings, ears. " +
        "Both halves get the same groupId so they move together.")]
    public static string DrawMirroredPath(
        [Description("Alternating x,y of the ORIGINAL (right or left) half.")] double[] coordinates,
        double axisX,
        string color,
        double strokeWidth = 2,
        string? penStyle = null, double opacity = 1.0, string? dashStyle = null, string? groupId = null)
    {
        var original = PointsFromFlat(coordinates);
        var mirrored = original.Select(p => new PointData(2 * axisX - p.X, p.Y)).ToList();
        string gid = groupId ?? Guid.NewGuid().ToString();
        var a = Stamp(new PenAction { Points = original, Color = color, StrokeWidth = strokeWidth, PenStyle = ParsePenStyle(penStyle) },
                      opacity, dashStyle, gid);
        var b = Stamp(new PenAction { Points = mirrored, Color = color, StrokeWidth = strokeWidth, PenStyle = ParsePenStyle(penStyle) },
                      opacity, dashStyle, gid);
        return JsonConvert.SerializeObject(new DrawActionBase[] { a, b }, JsonSettings);
    }

    // ─── Text ──────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Draw text at (x, y) — (x, y) is the top-left of the text block.")]
    public static string DrawText(
        double x, double y,
        string text,
        string color = "#000000",
        double fontSize = 18,
        bool isBold = false,
        bool isItalic = false,
        double opacity = 1.0, string? groupId = null)
        => Ser(Stamp(new TextAction
        {
            X = x, Y = y, Text = text, Color = color,
            FontSize = fontSize, IsBold = isBold, IsItalic = isItalic
        }, opacity, null, groupId));

    // ─── Batch ─────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "BATCH: submit many primitives in one call. Pass a JSON array string where each item is " +
        "{ \"tool\": \"DrawCircle\", \"args\": { ... } } referencing any other tool here. " +
        "Use this to render a whole scene in a single round-trip instead of 30 separate tool calls. " +
        "Example: [{\"tool\":\"DrawCircle\",\"args\":{\"cx\":100,\"cy\":100,\"radius\":40,\"color\":\"#000\"}},{\"tool\":\"DrawLine\",\"args\":{\"x1\":0,\"y1\":0,\"x2\":50,\"y2\":50,\"color\":\"#f00\"}}]")]
    public static string DrawMany(
        [Description("JSON array string. Each element: {\"tool\": <tool name>, \"args\": {named args}}.")] string actionsJson)
    {
        var arr = JArray.Parse(actionsJson);
        var results = new JArray();
        foreach (var item in arr)
        {
            var toolName = (string?)item["tool"] ?? throw new ArgumentException("Each batch item needs a 'tool' field.");
            var args = item["args"] as JObject ?? new JObject();
            string? raw = InvokeTool(toolName, args);
            if (raw == null) continue;
            var parsed = JToken.Parse(raw);
            if (parsed is JArray nested) foreach (var n in nested) results.Add(n);
            else results.Add(parsed);
        }
        return results.ToString(Formatting.None);
    }

    /// <summary>
    /// Dispatch a batch item to the matching public tool method by name, binding JSON args by parameter name.
    /// Missing params fall back to defaults.
    /// </summary>
    private static string? InvokeTool(string toolName, JObject args)
    {
        var method = typeof(DrawingTools).GetMethod(toolName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (method == null) throw new ArgumentException($"Unknown tool '{toolName}' in batch.");
        var ps = method.GetParameters();
        var call = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (args.TryGetValue(p.Name!, StringComparison.OrdinalIgnoreCase, out var v) && v.Type != JTokenType.Null)
                call[i] = v.ToObject(p.ParameterType);
            else if (p.HasDefaultValue)
                call[i] = p.DefaultValue;
            else
                throw new ArgumentException($"Tool '{toolName}' missing required arg '{p.Name}'.");
        }
        return method.Invoke(null, call) as string;
    }

    // ─── Action factories ──────────────────────────────────────────────────────

    private static ShapeAction Shape(
        ShapeType type, double x, double y, double w, double h,
        string color, string? fill, double strokeWidth) => new()
    {
        ShapeType = type,
        X = x, Y = y, Width = w, Height = h,
        Color = color, FillColor = fill, StrokeWidth = strokeWidth
    };

    private static T Stamp<T>(T action, double opacity, string? dashStyle, string? groupId) where T : DrawActionBase
    {
        action.Opacity = Math.Clamp(opacity, 0.0, 1.0);
        if (!string.IsNullOrWhiteSpace(dashStyle) &&
            Enum.TryParse<DashStyle>(dashStyle, ignoreCase: true, out var ds))
            action.DashStyle = ds;
        if (!string.IsNullOrWhiteSpace(groupId)) action.GroupId = groupId;
        return action;
    }

    private static PenStyle ParsePenStyle(string? s)
        => !string.IsNullOrWhiteSpace(s) && Enum.TryParse<PenStyle>(s, true, out var v) ? v : PenStyle.Normal;

    private static string Ser(DrawActionBase action)
        => JsonConvert.SerializeObject(action, JsonSettings);

    // ─── Curve sampling ────────────────────────────────────────────────────────

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;

    private static List<PointData> PointsFromFlat(double[] coords)
    {
        if (coords == null || coords.Length < 4 || coords.Length % 2 != 0)
            throw new ArgumentException("coordinates must be an even-length array of length >= 4");
        var pts = new List<PointData>(coords.Length / 2);
        for (int i = 0; i < coords.Length; i += 2) pts.Add(new PointData(coords[i], coords[i + 1]));
        return pts;
    }

    private static List<PointData> SampleEllipseArc(
        double cx, double cy, double rx, double ry,
        double startRad, double endRad, double rotationRad, int steps)
    {
        var pts = new List<PointData>(steps + 1);
        double cosR = Math.Cos(rotationRad), sinR = Math.Sin(rotationRad);
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            double a = startRad + (endRad - startRad) * t;
            double ex = rx * Math.Cos(a);
            double ey = ry * Math.Sin(a);
            // rotate about origin then translate to (cx,cy)
            double x = ex * cosR - ey * sinR + cx;
            double y = ex * sinR + ey * cosR + cy;
            pts.Add(new PointData(x, y));
        }
        return pts;
    }

    private static List<PointData> SampleQuadBezier(
        double x1, double y1, double cx, double cy, double x2, double y2, int steps)
    {
        var pts = new List<PointData>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            double x = u * u * x1 + 2 * u * t * cx + t * t * x2;
            double y = u * u * y1 + 2 * u * t * cy + t * t * y2;
            pts.Add(new PointData(x, y));
        }
        return pts;
    }

    private static List<PointData> SampleCubicBezier(
        double x1, double y1, double c1x, double c1y, double c2x, double c2y, double x2, double y2, int steps)
    {
        var pts = new List<PointData>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            double uu = u * u, uuu = uu * u, tt = t * t, ttt = tt * t;
            double x = uuu * x1 + 3 * uu * t * c1x + 3 * u * tt * c2x + ttt * x2;
            double y = uuu * y1 + 3 * uu * t * c1y + 3 * u * tt * c2y + ttt * y2;
            pts.Add(new PointData(x, y));
        }
        return pts;
    }

    /// <summary>Centripetal-ish Catmull–Rom spline through control points.</summary>
    private static List<PointData> SampleCatmullRom(List<PointData> ctrl, bool closed, int segmentsPerSpan)
    {
        int n = ctrl.Count;
        var pts = new List<PointData>();
        int spans = closed ? n : n - 1;
        for (int i = 0; i < spans; i++)
        {
            var p0 = ctrl[closed ? (i - 1 + n) % n : Math.Max(0, i - 1)];
            var p1 = ctrl[i % n];
            var p2 = ctrl[(i + 1) % n];
            var p3 = ctrl[closed ? (i + 2) % n : Math.Min(n - 1, i + 2)];
            for (int s = 0; s < segmentsPerSpan; s++)
            {
                double t = (double)s / segmentsPerSpan;
                double t2 = t * t, t3 = t2 * t;
                double x = 0.5 * ((2 * p1.X) +
                    (-p0.X + p2.X) * t +
                    (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                    (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                double y = 0.5 * ((2 * p1.Y) +
                    (-p0.Y + p2.Y) * t +
                    (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                    (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                pts.Add(new PointData(x, y));
            }
        }
        // terminal point
        pts.Add(new PointData(ctrl[closed ? 0 : n - 1].X, ctrl[closed ? 0 : n - 1].Y));
        return pts;
    }
}
