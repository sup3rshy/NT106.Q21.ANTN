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

    // ─── Composite "prefab" scenes ─────────────────────────────────────────────
    //
    // These are opinionated, always-in-proportion templates. They return an ARRAY
    // of pre-positioned primitives so Claude only has to pick a center + size
    // instead of computing 10 relative offsets (which it does badly, producing
    // floating ears / drifting eyes). Strongly prefer these for common subjects.

    [McpServerTool, Description(
        "TERMINAL — one call renders a COMPLETE cat face. Already draws: head, BOTH EARS " +
        "(with pink inner ears), BOTH EYES (white + iris + pupil + highlight), nose, mouth, " +
        "and 6 whiskers (3 per side). Nothing is missing. " +
        "Do NOT call draw_triangle/draw_circle/draw_line after this — the cat is complete. " +
        "For a different look, use the parameters (mood, furColor, eyeColor). " +
        "Typical call: draw_cat_face(cx=canvas_center_x, cy=canvas_center_y*0.9, size=min(canvas)/2).")]
    public static string DrawCatFace(
        [Description("Face center X")] double cx,
        [Description("Face center Y")] double cy,
        [Description("Overall face diameter in pixels (typical: 200–400)")] double size,
        [Description("Fur color #RRGGBB (default orange)")] string furColor = "#E8A85C",
        [Description("Iris color #RRGGBB (default green)")] string eyeColor = "#4CAF50",
        [Description("Mood: neutral|happy|sleepy|surprised|angry")] string mood = "neutral",
        [Description("Optional group id (default auto)")] string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        double r = size / 2;
        var list = new List<DrawActionBase>();
        string outline = "#2B1810";
        string pink = "#F4A6B8";
        double sw = Math.Max(2, size / 120);

        // Head
        list.Add(Stamp(Shape(ShapeType.Circle, cx - r, cy - r, size, size, outline, furColor, sw * 1.2), 1, null, gid));

        // Ears — use polygons (triangles) positioned by hand for correct orientation
        double earBaseL = cx - 0.75 * r, earBaseR = cx + 0.75 * r;
        double earInnerL = cx - 0.35 * r, earInnerR = cx + 0.35 * r;
        double earTopY = cy - r * 1.15, earBaseY = cy - r * 0.55;
        list.Add(Pen(new[] { earBaseL, earBaseY, cx - 0.55 * r, earTopY, earInnerL, earBaseY - 5 }, outline, sw, true, gid));
        list.Add(Pen(new[] { earBaseR, earBaseY, cx + 0.55 * r, earTopY, earInnerR, earBaseY - 5 }, outline, sw, true, gid));
        // Inner pink ears (smaller, same shape)
        list.Add(Pen(new[] { earBaseL + 0.06 * r, earBaseY - 0.05 * r, cx - 0.55 * r, earTopY + 0.2 * r, earInnerL - 0.06 * r, earBaseY - 0.08 * r }, pink, sw * 0.6, true, gid));
        list.Add(Pen(new[] { earBaseR - 0.06 * r, earBaseY - 0.05 * r, cx + 0.55 * r, earTopY + 0.2 * r, earInnerR + 0.06 * r, earBaseY - 0.08 * r }, pink, sw * 0.6, true, gid));

        // Eyes
        double eyeDX = 0.33 * r, eyeY = cy - 0.08 * r;
        double eyeW = 0.26 * r, eyeH = 0.32 * r;
        bool closed = mood is "sleepy" or "happy";
        bool big = mood is "surprised";
        if (big) { eyeW *= 1.25; eyeH *= 1.25; }
        if (!closed)
        {
            // whites
            list.Add(Stamp(Shape(ShapeType.Ellipse, cx - eyeDX - eyeW / 2, eyeY - eyeH / 2, eyeW, eyeH, outline, "#FFFFFF", sw), 1, null, gid));
            list.Add(Stamp(Shape(ShapeType.Ellipse, cx + eyeDX - eyeW / 2, eyeY - eyeH / 2, eyeW, eyeH, outline, "#FFFFFF", sw), 1, null, gid));
            // iris
            double irisW = eyeW * 0.7, irisH = eyeH * 0.85;
            list.Add(Stamp(Shape(ShapeType.Ellipse, cx - eyeDX - irisW / 2, eyeY - irisH / 2, irisW, irisH, eyeColor, eyeColor, 1), 1, null, gid));
            list.Add(Stamp(Shape(ShapeType.Ellipse, cx + eyeDX - irisW / 2, eyeY - irisH / 2, irisW, irisH, eyeColor, eyeColor, 1), 1, null, gid));
            // pupil (vertical slit for cat)
            double pupW = irisW * 0.2, pupH = irisH * 0.85;
            list.Add(Stamp(Shape(ShapeType.Ellipse, cx - eyeDX - pupW / 2, eyeY - pupH / 2, pupW, pupH, "#000", "#000", 1), 1, null, gid));
            list.Add(Stamp(Shape(ShapeType.Ellipse, cx + eyeDX - pupW / 2, eyeY - pupH / 2, pupW, pupH, "#000", "#000", 1), 1, null, gid));
            // highlights
            double hlR = eyeW * 0.1;
            list.Add(Stamp(Shape(ShapeType.Circle, cx - eyeDX - hlR, eyeY - eyeH * 0.3 - hlR, hlR * 2, hlR * 2, "#FFF", "#FFF", 1), 1, null, gid));
            list.Add(Stamp(Shape(ShapeType.Circle, cx + eyeDX - hlR, eyeY - eyeH * 0.3 - hlR, hlR * 2, hlR * 2, "#FFF", "#FFF", 1), 1, null, gid));
        }
        else
        {
            // closed/happy eyes: arcs
            list.Add(Arc(cx - eyeDX, eyeY, eyeW / 2, 200, 340, outline, sw * 1.5, gid));
            list.Add(Arc(cx + eyeDX, eyeY, eyeW / 2, 200, 340, outline, sw * 1.5, gid));
        }

        // Nose (small pink triangle, pointing down)
        double noseY = cy + 0.12 * r, noseW = 0.13 * r, noseH = 0.1 * r;
        list.Add(Pen(new[] { cx - noseW, noseY, cx + noseW, noseY, cx, noseY + noseH }, pink, sw, true, gid));

        // Mouth: classic "3-on-its-side" — two small arcs under the nose
        double mY = noseY + noseH + 2;
        list.Add(Arc(cx - 0.08 * r, mY + 0.04 * r, 0.09 * r, 0, 180, outline, sw, gid));
        list.Add(Arc(cx + 0.08 * r, mY + 0.04 * r, 0.09 * r, 0, 180, outline, sw, gid));
        // Angry: flip mouth to a frown
        if (mood == "angry")
        {
            list.RemoveRange(list.Count - 2, 2);
            list.Add(Arc(cx, mY + 0.12 * r, 0.12 * r, 200, 340, outline, sw, gid));
        }
        // Surprised: small O mouth
        if (mood == "surprised")
        {
            list.RemoveRange(list.Count - 2, 2);
            list.Add(Stamp(Shape(ShapeType.Ellipse, cx - 0.05 * r, mY, 0.1 * r, 0.12 * r, outline, "#000", sw), 1, null, gid));
        }

        // Whiskers: 3 on each side, slight droop
        double wBaseY = noseY - 0.02 * r;
        for (int i = -1; i <= 1; i++)
        {
            double dy = i * 0.08 * r;
            list.Add(new LineAction { StartX = cx - 0.25 * r, StartY = wBaseY + dy, EndX = cx - 0.85 * r, EndY = wBaseY + dy + i * 0.04 * r, Color = outline, StrokeWidth = sw * 0.8, Opacity = 1, GroupId = gid });
            list.Add(new LineAction { StartX = cx + 0.25 * r, StartY = wBaseY + dy, EndX = cx + 0.85 * r, EndY = wBaseY + dy + i * 0.04 * r, Color = outline, StrokeWidth = sw * 0.8, Opacity = 1, GroupId = gid });
        }

        return JsonConvert.SerializeObject(list.ToArray(), JsonSettings);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a manga/anime character head (front view): face silhouette, large " +
        "manga eyes with highlights, nose dot, small mouth, hair strands. " +
        "gender: female|male|neutral (affects hair & face shape). " +
        "hairStyle: short|long|ponytail|spiky.")]
    public static string DrawMangaFace(
        double cx, double cy,
        [Description("Head height in pixels (typical: 250–500)")] double size,
        string hairColor = "#3B2418",
        string eyeColor = "#5B8DEF",
        string skinColor = "#FCE4D0",
        string gender = "neutral",
        string hairStyle = "short",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        double h = size, w = size * 0.72; // head slightly narrower than tall
        string lineCol = "#1A1A1A";
        double sw = Math.Max(2, size / 140);
        var list = new List<DrawActionBase>();

        // Face silhouette — 8-point smooth closed curve (egg/heart-ish)
        double hx = w / 2, hy = h / 2;
        double[] face = new[]
        {
            cx,          cy - hy * 0.98,        // top
            cx + hx*0.95, cy - hy * 0.55,
            cx + hx,     cy - hy * 0.05,        // cheek
            cx + hx*0.75, cy + hy * 0.45,       // jaw
            cx + hx*0.35, cy + hy * 0.92,       // chin area
            cx,          cy + hy * 1.0,         // chin
            cx - hx*0.35, cy + hy * 0.92,
            cx - hx*0.75, cy + hy * 0.45,
            cx - hx,     cy - hy * 0.05,
            cx - hx*0.95, cy - hy * 0.55,
        };
        var facePts = PointsFromFlat(face);
        var smoothed = SampleCatmullRom(facePts, closed: true, segmentsPerSpan: 20);
        // Fill via a filled ellipse underneath (approximation), then stroke the silhouette
        list.Add(Stamp(Shape(ShapeType.Ellipse, cx - w / 2, cy - h / 2, w, h * 1.05, skinColor, skinColor, 1), 1, null, gid));
        list.Add(Stamp(new PenAction { Points = smoothed, Color = lineCol, StrokeWidth = sw * 1.3, PenStyle = PenStyle.Calligraphy }, 1, null, gid));

        // Eyes — large, lower third of face
        double eyeY = cy + h * 0.05;
        double eyeDX = w * 0.22;
        double eyeW = w * 0.25, eyeH = h * 0.22;
        for (int side = -1; side <= 1; side += 2)
        {
            double ex = cx + side * eyeDX;
            // eye white (slightly rounded rect approximated by ellipse)
            list.Add(Stamp(Shape(ShapeType.Ellipse, ex - eyeW / 2, eyeY - eyeH / 2, eyeW, eyeH, lineCol, "#FFFFFF", sw), 1, null, gid));
            // iris (tall oval — manga style)
            double irW = eyeW * 0.6, irH = eyeH * 1.05;
            list.Add(Stamp(Shape(ShapeType.Ellipse, ex - irW / 2, eyeY - irH / 2, irW, irH, eyeColor, eyeColor, 1), 1, null, gid));
            // pupil
            double pW = irW * 0.35, pH = irH * 0.5;
            list.Add(Stamp(Shape(ShapeType.Ellipse, ex - pW / 2, eyeY - pH / 2, pW, pH, "#000", "#000", 1), 1, null, gid));
            // large highlight
            double hl = eyeW * 0.16;
            list.Add(Stamp(Shape(ShapeType.Circle, ex - hl - eyeW * 0.1, eyeY - eyeH * 0.3, hl * 2, hl * 2, "#FFF", "#FFF", 1), 1, null, gid));
            // small highlight
            double hl2 = eyeW * 0.07;
            list.Add(Stamp(Shape(ShapeType.Circle, ex + eyeW * 0.08, eyeY + eyeH * 0.15, hl2 * 2, hl2 * 2, "#FFF", "#FFF", 1), 1, null, gid));
            // eyelashes (top arc, thicker)
            list.Add(Arc(ex, eyeY - eyeH * 0.35, eyeW * 0.55, 200, 340, lineCol, sw * 1.8, gid));
        }

        // Eyebrows
        for (int side = -1; side <= 1; side += 2)
        {
            double bx = cx + side * eyeDX;
            double by = eyeY - eyeH * 0.75;
            list.Add(Arc(bx, by + 8, eyeW * 0.4, 200, 340, lineCol, sw * 1.3, gid));
        }

        // Nose (tiny line/dot below eyes)
        list.Add(new LineAction { StartX = cx - 3, StartY = cy + h * 0.28, EndX = cx + 3, EndY = cy + h * 0.32, Color = lineCol, StrokeWidth = sw, Opacity = 1, GroupId = gid });

        // Mouth (small curve)
        list.Add(Arc(cx, cy + h * 0.45, w * 0.1, 10, 170, lineCol, sw, gid));

        // Hair — several curved strands on top/sides (depends on style)
        int strands = hairStyle == "long" ? 10 : hairStyle == "spiky" ? 8 : 6;
        for (int i = 0; i < strands; i++)
        {
            double t = (i + 0.5) / strands;
            double startX = cx + (t - 0.5) * w * 1.1;
            double startY = cy - h * 0.6 + Math.Sin(t * Math.PI) * -h * 0.25;
            double endY = cy - h * 0.1 + (hairStyle == "long" ? h * 0.6 : 0);
            if (hairStyle == "spiky") endY = cy - h * 0.35;
            double ctrlX = startX + (t < 0.5 ? -w * 0.15 : w * 0.15);
            double ctrlY = startY + h * 0.1;
            var pts = SampleQuadBezier(startX, startY, ctrlX, ctrlY, startX + (t < 0.5 ? -w * 0.05 : w * 0.05), endY, 20);
            list.Add(Stamp(new PenAction { Points = pts, Color = hairColor, StrokeWidth = sw * 2.0, PenStyle = PenStyle.Calligraphy }, 1, null, gid));
        }
        // Hair silhouette on top (rough cloud)
        double[] hair = new[]
        {
            cx - w * 0.55, cy - h * 0.35,
            cx - w * 0.5,  cy - h * 0.7,
            cx - w * 0.2,  cy - h * 0.95,
            cx + w * 0.15, cy - h * 1.0,
            cx + w * 0.45, cy - h * 0.8,
            cx + w * 0.55, cy - h * 0.4,
        };
        var hairPts = PointsFromFlat(hair);
        var hairSmooth = SampleCatmullRom(hairPts, closed: false, segmentsPerSpan: 18);
        list.Add(Stamp(new PenAction { Points = hairSmooth, Color = hairColor, StrokeWidth = sw * 2.5, PenStyle = PenStyle.Calligraphy }, 1, null, gid));

        return JsonConvert.SerializeObject(list.ToArray(), JsonSettings);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a speech bubble with a tail pointing at (tailTargetX, tailTargetY) " +
        "and optional text inside. The bubble body is a rounded rectangle centered at (cx,cy).")]
    public static string DrawSpeechBubble(
        double cx, double cy, double width, double height,
        double tailTargetX, double tailTargetY,
        string? text = null,
        string fillColor = "#FFFFFF",
        string strokeColor = "#000000",
        double fontSize = 18,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        double sw = 2.5;
        double x = cx - width / 2, y = cy - height / 2;
        double r = Math.Min(width, height) * 0.2;

        // White fill (rectangle with rounded feel — use ellipse layered on rect corners)
        list.Add(Stamp(Shape(ShapeType.Rect, x + r, y, width - 2 * r, height, fillColor, fillColor, 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Rect, x, y + r, width, height - 2 * r, fillColor, fillColor, 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, x, y, 2 * r, 2 * r, fillColor, fillColor, 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, x + width - 2 * r, y, 2 * r, 2 * r, fillColor, fillColor, 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, x, y + height - 2 * r, 2 * r, 2 * r, fillColor, fillColor, 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, x + width - 2 * r, y + height - 2 * r, 2 * r, 2 * r, fillColor, fillColor, 1), 1, null, gid));

        // Outline via rounded rect
        var outlinePts = new List<PointData>();
        outlinePts.Add(new PointData(x + r, y));
        outlinePts.Add(new PointData(x + width - r, y));
        outlinePts.AddRange(SampleEllipseArc(x + width - r, y + r, r, r, Deg2Rad(-90), Deg2Rad(0), 0, 10));
        outlinePts.Add(new PointData(x + width, y + height - r));
        outlinePts.AddRange(SampleEllipseArc(x + width - r, y + height - r, r, r, Deg2Rad(0), Deg2Rad(90), 0, 10));
        outlinePts.Add(new PointData(x + r, y + height));
        outlinePts.AddRange(SampleEllipseArc(x + r, y + height - r, r, r, Deg2Rad(90), Deg2Rad(180), 0, 10));
        outlinePts.Add(new PointData(x, y + r));
        outlinePts.AddRange(SampleEllipseArc(x + r, y + r, r, r, Deg2Rad(180), Deg2Rad(270), 0, 10));
        outlinePts.Add(new PointData(x + r, y));
        list.Add(Stamp(new PenAction { Points = outlinePts, Color = strokeColor, StrokeWidth = sw }, 1, null, gid));

        // Tail — small triangle pointing at target
        double tx = Math.Clamp(tailTargetX, x - width, x + 2 * width);
        double ty = tailTargetY;
        // Tail base on nearest bubble edge, perpendicular direction determines base points
        double baseX = Math.Clamp(tx, x + r, x + width - r);
        bool below = ty > cy;
        double baseY = below ? y + height : y;
        double baseHalf = width * 0.1;
        list.Add(Pen(new[] { baseX - baseHalf, baseY, tx, ty, baseX + baseHalf, baseY }, fillColor, 1, true, gid)); // fill poly approx
        list.Add(new LineAction { StartX = baseX - baseHalf, StartY = baseY, EndX = tx, EndY = ty, Color = strokeColor, StrokeWidth = sw, Opacity = 1, GroupId = gid });
        list.Add(new LineAction { StartX = tx, StartY = ty, EndX = baseX + baseHalf, EndY = baseY, Color = strokeColor, StrokeWidth = sw, Opacity = 1, GroupId = gid });

        // Text (centered — Claude should pass short strings)
        if (!string.IsNullOrEmpty(text))
        {
            double charW = fontSize * 0.55;
            double tw = text.Length * charW;
            list.Add(new TextAction
            {
                X = cx - tw / 2, Y = cy - fontSize / 2,
                Text = text, Color = strokeColor, FontSize = fontSize, GroupId = gid
            });
        }
        return JsonConvert.SerializeObject(list.ToArray(), JsonSettings);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a simple tree (trunk + foliage blob) centered at base point (baseX, baseY). " +
        "type: round|pine|bush.")]
    public static string DrawTree(
        double baseX, double baseY, double height,
        string type = "round",
        string trunkColor = "#6B3F1D",
        string leafColor = "#3E8E3E",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        double trunkW = height * 0.12, trunkH = height * 0.35;
        list.Add(Stamp(Shape(ShapeType.Rect, baseX - trunkW / 2, baseY - trunkH, trunkW, trunkH, trunkColor, trunkColor, 2), 1, null, gid));
        double fY = baseY - trunkH;
        double fH = height - trunkH;
        if (type == "pine")
        {
            for (int i = 0; i < 3; i++)
            {
                double t = 1 - i / 3.0;
                double w = height * 0.8 * t;
                double yTop = fY - fH * (i + 1) / 3.0;
                list.Add(Pen(new[] { baseX - w / 2, yTop + fH / 3, baseX, yTop, baseX + w / 2, yTop + fH / 3 }, leafColor, 2, true, gid));
            }
        }
        else if (type == "bush")
        {
            list.Add(Stamp(Shape(ShapeType.Ellipse, baseX - height * 0.45, fY - fH * 0.9, height * 0.9, fH, leafColor, leafColor, 2), 1, null, gid));
        }
        else // round
        {
            double r = height * 0.4;
            list.Add(Stamp(Shape(ShapeType.Circle, baseX - r, fY - r * 1.4, r * 2, r * 2, leafColor, leafColor, 2), 1, null, gid));
            list.Add(Stamp(Shape(ShapeType.Circle, baseX - r * 1.1, fY - r * 0.8, r * 1.5, r * 1.5, leafColor, leafColor, 2), 1, null, gid));
            list.Add(Stamp(Shape(ShapeType.Circle, baseX + r * 0.2, fY - r * 0.9, r * 1.5, r * 1.5, leafColor, leafColor, 2), 1, null, gid));
        }
        return JsonConvert.SerializeObject(list.ToArray(), JsonSettings);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a simple house: body + roof + door + 1-2 windows. " +
        "Centered at base point (baseX is house center, baseY is ground line).")]
    public static string DrawHouse(
        double baseX, double baseY, double width, double height,
        string wallColor = "#F4E4B8",
        string roofColor = "#A0524D",
        string doorColor = "#6B3F1D",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        double wallH = height * 0.65;
        list.Add(Stamp(Shape(ShapeType.Rect, baseX - width / 2, baseY - wallH, width, wallH, "#222", wallColor, 2), 1, null, gid));
        // Roof (triangle)
        list.Add(Pen(new[] {
            baseX - width / 2 - width * 0.05, baseY - wallH,
            baseX, baseY - height,
            baseX + width / 2 + width * 0.05, baseY - wallH
        }, roofColor, 2, true, gid));
        // Door
        double dw = width * 0.2, dh = wallH * 0.5;
        list.Add(Stamp(Shape(ShapeType.Rect, baseX - dw / 2, baseY - dh, dw, dh, "#222", doorColor, 2), 1, null, gid));
        // Windows
        double ww = width * 0.18;
        list.Add(Stamp(Shape(ShapeType.Rect, baseX - width * 0.35, baseY - wallH * 0.75, ww, ww, "#222", "#B8DCF4", 2), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Rect, baseX + width * 0.17, baseY - wallH * 0.75, ww, ww, "#222", "#B8DCF4", 2), 1, null, gid));
        return JsonConvert.SerializeObject(list.ToArray(), JsonSettings);
    }

    [McpServerTool, Description("COMPOSITE — draws a sun (filled circle + N rays). Great for skies.")]
    public static string DrawSun(
        double cx, double cy, double radius,
        string color = "#FFC83D",
        int rays = 8,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        list.Add(Stamp(Shape(ShapeType.Circle, cx - radius, cy - radius, radius * 2, radius * 2, color, color, 3), 1, null, gid));
        for (int i = 0; i < rays; i++)
        {
            double a = i * 2 * Math.PI / rays;
            double x1 = cx + Math.Cos(a) * radius * 1.2, y1 = cy + Math.Sin(a) * radius * 1.2;
            double x2 = cx + Math.Cos(a) * radius * 1.8, y2 = cy + Math.Sin(a) * radius * 1.8;
            list.Add(new LineAction { StartX = x1, StartY = y1, EndX = x2, EndY = y2, Color = color, StrokeWidth = 4, Opacity = 1, GroupId = gid });
        }
        return JsonConvert.SerializeObject(list.ToArray(), JsonSettings);
    }

    // Internal pen helper used by composites
    private static PenAction Pen(double[] coords, string color, double strokeWidth, bool closed, string gid)
    {
        var pts = PointsFromFlat(coords);
        if (closed) pts.Add(new PointData(pts[0].X, pts[0].Y));
        var a = new PenAction { Points = pts, Color = color, StrokeWidth = strokeWidth, GroupId = gid };
        return a;
    }

    private static PenAction Arc(double cx, double cy, double radius, double startDeg, double endDeg, string color, double strokeWidth, string gid)
    {
        var pts = SampleEllipseArc(cx, cy, radius, radius, Deg2Rad(startDeg), Deg2Rad(endDeg), 0,
            Math.Max(12, (int)(Math.Abs(endDeg - startDeg) / 6)));
        return new PenAction { Points = pts, Color = color, StrokeWidth = strokeWidth, GroupId = gid };
    }

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
