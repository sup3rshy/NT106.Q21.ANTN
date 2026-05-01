using System.ComponentModel;
using ModelContextProtocol.Server;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.McpServer;

/// <summary>
/// Extended drawing library: filled polygons, icon stickers, more composites,
/// affine transform wrapper, palette helper.
///
/// Design constraint: the client renderer only knows shape/line/pen/text. So
/// "filled organic shape" is implemented server-side as a SCANLINE FILL — we
/// emit one horizontal LineAction per scan row and let the client draw them
/// like any other strokes. yStep ≈ strokeWidth so adjacent rows blend visually.
/// For a 200-tall fill at yStep=3, that's ~67 lines per fill. Manageable.
/// </summary>
public static partial class DrawingTools
{
    private static readonly JsonSerializerSettings BatchJson = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    // ─── Filled polygons (the cornerstone — unlocks every organic icon) ───────

    [McpServerTool, Description(
        "Fill an ARBITRARY polygon with a solid color (no stroke). Pair with DrawPolygon if you want both fill and outline. " +
        "Works for convex AND concave polygons. Implemented as horizontal scanlines, so you get a solid fill " +
        "without the renderer needing a polygon-fill primitive.")]
    public static string DrawFilledPolygon(
        [Description("Alternating x,y of polygon vertices (>= 3 points). Auto-closed.")] double[] coordinates,
        [Description("Fill color #RRGGBB")] string fillColor,
        [Description("Scanline spacing in px. Smaller = smoother but more strokes (default 2.5).")] double yStep = 2.5,
        double opacity = 1.0,
        string? groupId = null)
    {
        var pts = PointsFromFlat(coordinates);
        if (pts.Count < 3) throw new ArgumentException("DrawFilledPolygon needs at least 3 vertices.");
        string gid = groupId ?? Guid.NewGuid().ToString();
        var lines = ScanlineFill(pts, fillColor, Math.Max(1.0, yStep), opacity, gid);
        return JsonConvert.SerializeObject(lines.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "Fill a SMOOTH organic shape defined by control points (Catmull–Rom). " +
        "Use this for filled blobs, hair silhouettes, body shapes, leaves — anything you'd draw with " +
        "DrawSmoothCurve(closed=true) but want filled. Returns the fill PLUS an optional outline stroke.")]
    public static string DrawFilledSmoothShape(
        [Description("Alternating x,y control points (>= 3). Auto-closed.")] double[] coordinates,
        string fillColor,
        [Description("Outline color #RRGGBB, or null for fill-only")] string? strokeColor = null,
        double strokeWidth = 2,
        string? penStyle = null,
        double yStep = 2.5,
        double opacity = 1.0,
        string? groupId = null)
    {
        var ctrl = PointsFromFlat(coordinates);
        if (ctrl.Count < 3) throw new ArgumentException("DrawFilledSmoothShape needs at least 3 control points.");
        string gid = groupId ?? Guid.NewGuid().ToString();
        var smoothed = SampleCatmullRom(ctrl, closed: true, segmentsPerSpan: 16);
        var actions = new List<DrawActionBase>();
        actions.AddRange(ScanlineFill(smoothed, fillColor, Math.Max(1.0, yStep), opacity, gid));
        if (!string.IsNullOrWhiteSpace(strokeColor))
        {
            actions.Add(new PenAction
            {
                Points = smoothed, Color = strokeColor!, StrokeWidth = strokeWidth,
                PenStyle = ParsePenStyle(penStyle), Opacity = Math.Clamp(opacity, 0.0, 1.0), GroupId = gid
            });
        }
        return JsonConvert.SerializeObject(actions.ToArray(), BatchJson);
    }

    // ─── Icon library (stickers — fully filled, parameterized) ────────────────

    [McpServerTool, Description(
        "ICON — draws a HEART centered at (cx,cy) with given size (height in pixels). " +
        "Filled heart with optional outline. Perfect for love/like decorations.")]
    public static string DrawHeart(
        double cx, double cy, double size,
        string fillColor = "#E74C3C",
        string? strokeColor = "#8B1A0F",
        double strokeWidth = 2,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        // Parametric heart: x = 16 sin³(t), y = -(13 cos t − 5 cos 2t − 2 cos 3t − cos 4t), scaled.
        // Sample 64 points, scale to 'size' tall.
        double s = size / 32.0; // raw heart range is ~32 in y
        var pts = new List<PointData>(64);
        for (int i = 0; i < 64; i++)
        {
            double t = i * 2 * Math.PI / 64;
            double sinT = Math.Sin(t);
            double x = 16 * sinT * sinT * sinT;
            double y = -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t));
            pts.Add(new PointData(cx + x * s, cy + y * s));
        }
        var actions = new List<DrawActionBase>();
        actions.AddRange(ScanlineFill(pts, fillColor, 2.0, 1.0, gid));
        if (!string.IsNullOrWhiteSpace(strokeColor))
        {
            var outline = new List<PointData>(pts) { new(pts[0].X, pts[0].Y) };
            actions.Add(new PenAction { Points = outline, Color = strokeColor!, StrokeWidth = strokeWidth, GroupId = gid });
        }
        return JsonConvert.SerializeObject(actions.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "ICON — draws a fluffy CLOUD as 4 overlapping filled circles. " +
        "(cx,cy) is the cloud center; width/height define overall bounding box.")]
    public static string DrawCloud(
        double cx, double cy, double width, double height,
        string fillColor = "#FFFFFF",
        string? strokeColor = "#A8B5C0",
        double strokeWidth = 2,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        // 4 puffs: left, mid-left (tall), mid-right, right
        double r1 = height * 0.35;
        double r2 = height * 0.5;
        double r3 = height * 0.45;
        double r4 = height * 0.32;
        void Puff(double pcx, double pcy, double pr)
            => list.Add(Stamp(Shape(ShapeType.Circle, pcx - pr, pcy - pr, pr * 2, pr * 2, strokeColor ?? fillColor, fillColor, strokeWidth), 1, null, gid));
        Puff(cx - width * 0.35, cy + height * 0.08, r1);
        Puff(cx - width * 0.10, cy - height * 0.15, r2);
        Puff(cx + width * 0.18, cy - height * 0.05, r3);
        Puff(cx + width * 0.38, cy + height * 0.10, r4);
        // Bottom flat: a filled rectangle to hide the puff bottoms below baseline
        list.Add(Stamp(Shape(ShapeType.Rect, cx - width * 0.45, cy + height * 0.15, width * 0.9, height * 0.30, fillColor, fillColor, 0.5), 1, null, gid));
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "ICON — draws a CRESCENT MOON. (cx,cy) is the center, radius is the moon size. " +
        "phase: full|gibbous|half|crescent — controls the cut depth (full = no cut).")]
    public static string DrawMoon(
        double cx, double cy, double radius,
        string fillColor = "#FFE5A0",
        string phase = "crescent",
        string? strokeColor = "#C4A04C",
        double strokeWidth = 2,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        // Crescent = full disc minus an offset disc. We sample the boundary as
        // outer arc + inner arc (reversed), forming a closed crescent polygon, then fill.
        double offset = phase switch
        {
            "full" => 0,
            "gibbous" => radius * 0.4,
            "half" => radius * 0.7,
            _ => radius * 0.95,
        };
        if (offset == 0)
        {
            // Just a full disc
            var actions = new List<DrawActionBase>
            {
                Stamp(Shape(ShapeType.Circle, cx - radius, cy - radius, radius * 2, radius * 2, strokeColor ?? fillColor, fillColor, strokeWidth), 1, null, gid)
            };
            return JsonConvert.SerializeObject(actions.ToArray(), BatchJson);
        }
        // Outer boundary: full circle (right half), inner: offset circle (right half)
        // Actually for a left-facing crescent (cut on the right): outer = full disc, then subtract a circle offset to the right.
        var outer = SampleEllipseArc(cx, cy, radius, radius, Deg2Rad(90), Deg2Rad(270), 0, 64);
        var inner = SampleEllipseArc(cx + offset, cy, radius, radius, Deg2Rad(270), Deg2Rad(90), 0, 64);
        var poly = new List<PointData>();
        poly.AddRange(outer);
        // inner is sampled going from 270 down to 90 — but I want the "inside" of the crescent;
        // since they meet at top/bottom of the disc, just append.
        poly.AddRange(inner);
        var list = new List<DrawActionBase>();
        list.AddRange(ScanlineFill(poly, fillColor, 2.0, 1.0, gid));
        if (!string.IsNullOrWhiteSpace(strokeColor))
        {
            poly.Add(new PointData(poly[0].X, poly[0].Y));
            list.Add(new PenAction { Points = poly, Color = strokeColor!, StrokeWidth = strokeWidth, GroupId = gid });
        }
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "ICON — draws a LIGHTNING BOLT (zigzag) as a filled polygon. " +
        "(cx,cy) is the bolt center; size is total height.")]
    public static string DrawLightning(
        double cx, double cy, double size,
        string fillColor = "#FFD93D",
        string? strokeColor = "#B89020",
        double strokeWidth = 2,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        double w = size * 0.45, h = size;
        // 7-vertex bolt shape, classic
        double[] coords = new[]
        {
            cx + w * 0.10, cy - h * 0.50,
            cx - w * 0.40, cy + h * 0.05,
            cx - w * 0.05, cy + h * 0.05,
            cx - w * 0.20, cy + h * 0.50,
            cx + w * 0.40, cy - h * 0.05,
            cx + w * 0.05, cy - h * 0.05,
            cx + w * 0.30, cy - h * 0.50,
        };
        var pts = PointsFromFlat(coords);
        var list = new List<DrawActionBase>();
        list.AddRange(ScanlineFill(pts, fillColor, 2.0, 1.0, gid));
        if (!string.IsNullOrWhiteSpace(strokeColor))
        {
            var outline = new List<PointData>(pts) { new(pts[0].X, pts[0].Y) };
            list.Add(new PenAction { Points = outline, Color = strokeColor!, StrokeWidth = strokeWidth, GroupId = gid });
        }
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "ICON — draws an ARROW from (x1,y1) to (x2,y2) as a filled polygon (shaft + head). " +
        "headSize = arrowhead length in pixels. shaftWidth = thickness of the body.")]
    public static string DrawFilledArrow(
        double x1, double y1, double x2, double y2,
        double shaftWidth = 14, double headSize = 36,
        string fillColor = "#2C3E50",
        string? strokeColor = null,
        double strokeWidth = 2,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) throw new ArgumentException("Arrow length too small.");
        double ux = dx / len, uy = dy / len;        // along
        double px = -uy, py = ux;                    // perpendicular
        double sw2 = shaftWidth / 2;
        double headW = headSize * 0.7;
        double bx = x2 - ux * headSize, by = y2 - uy * headSize; // base of head along axis
        // 7-vertex arrow polygon (start of shaft → head → end of shaft)
        double[] coords = new[]
        {
            x1 + px * sw2, y1 + py * sw2,
            bx + px * sw2, by + py * sw2,
            bx + px * headW, by + py * headW,
            x2,             y2,
            bx - px * headW, by - py * headW,
            bx - px * sw2,   by - py * sw2,
            x1 - px * sw2,   y1 - py * sw2,
        };
        var pts = PointsFromFlat(coords);
        var list = new List<DrawActionBase>();
        list.AddRange(ScanlineFill(pts, fillColor, 2.0, 1.0, gid));
        if (!string.IsNullOrWhiteSpace(strokeColor))
        {
            var outline = new List<PointData>(pts) { new(pts[0].X, pts[0].Y) };
            list.Add(new PenAction { Points = outline, Color = strokeColor!, StrokeWidth = strokeWidth, GroupId = gid });
        }
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "ICON — draws a FLOWER: N filled circular petals around a center. " +
        "(cx,cy) is the flower center; size is the bloom diameter.")]
    public static string DrawFlower(
        double cx, double cy, double size,
        string petalColor = "#F4A6CD",
        string centerColor = "#FFD93D",
        int petals = 6,
        string? strokeColor = "#8B5A7A",
        double strokeWidth = 1.5,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        double r = size / 2;
        double petalR = r * 0.42;
        double ringR = r * 0.55;
        for (int i = 0; i < petals; i++)
        {
            double a = i * 2 * Math.PI / petals - Math.PI / 2;
            double px = cx + Math.Cos(a) * ringR;
            double py = cy + Math.Sin(a) * ringR;
            list.Add(Stamp(Shape(ShapeType.Circle, px - petalR, py - petalR, petalR * 2, petalR * 2,
                                  strokeColor ?? petalColor, petalColor, strokeWidth), 1, null, gid));
        }
        // center
        double cR = r * 0.32;
        list.Add(Stamp(Shape(ShapeType.Circle, cx - cR, cy - cR, cR * 2, cR * 2,
                              strokeColor ?? centerColor, centerColor, strokeWidth), 1, null, gid));
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "ICON — draws a LEAF (almond/eye-shape) at (cx,cy), oriented along angleDeg. " +
        "length = tip-to-tip in pixels. Filled with fillColor + central vein.")]
    public static string DrawLeaf(
        double cx, double cy, double length, double angleDeg = 0,
        string fillColor = "#4CAF50",
        string? strokeColor = "#2E7D32",
        double strokeWidth = 2,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        double rad = Deg2Rad(angleDeg);
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double L = length / 2;
        double W = length * 0.32;
        // Local leaf vertices (centered at origin, axis = +x)
        // Build via 24-point smooth boundary: top arc + bottom arc
        var local = new List<PointData>();
        for (int i = 0; i <= 12; i++)
        {
            double t = (double)i / 12 * Math.PI;
            local.Add(new PointData(L * Math.Cos(t), W * Math.Sin(t)));
        }
        for (int i = 0; i <= 12; i++)
        {
            double t = (double)i / 12 * Math.PI;
            local.Add(new PointData(-L * Math.Cos(t), -W * Math.Sin(t)));
        }
        // Rotate + translate
        var pts = local.Select(p => new PointData(cx + p.X * cos - p.Y * sin, cy + p.X * sin + p.Y * cos)).ToList();
        var list = new List<DrawActionBase>();
        list.AddRange(ScanlineFill(pts, fillColor, 2.0, 1.0, gid));
        if (!string.IsNullOrWhiteSpace(strokeColor))
        {
            var outline = new List<PointData>(pts) { new(pts[0].X, pts[0].Y) };
            list.Add(new PenAction { Points = outline, Color = strokeColor!, StrokeWidth = strokeWidth, GroupId = gid });
            // central vein
            list.Add(new LineAction
            {
                StartX = cx - L * cos, StartY = cy - L * sin,
                EndX = cx + L * cos, EndY = cy + L * sin,
                Color = strokeColor!, StrokeWidth = strokeWidth * 0.7, Opacity = 1, GroupId = gid
            });
        }
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "ICON — draws a RAINDROP / TEAR (round bottom, pointed top) at (cx,cy), filled. " +
        "size = total height in pixels.")]
    public static string DrawRaindrop(
        double cx, double cy, double size,
        string fillColor = "#4FA8E0",
        string? strokeColor = "#1F6FA8",
        double strokeWidth = 2,
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        double h = size, w = size * 0.7;
        double bcy = cy + h * 0.20;            // center of the round bottom bulb
        double br = w / 2;
        // Build teardrop boundary: pointed top → right side curving down → bottom semicircle → left side curving up.
        var pts = new List<PointData> { new(cx, cy - h / 2) };
        pts.AddRange(SampleQuadBezier(cx, cy - h / 2, cx + br, cy - h * 0.05, cx + br, bcy, 16).Skip(1));
        // Bottom semicircle: from (cx+br, bcy) through bottom (cx, bcy+br) to (cx-br, bcy)
        for (int i = 1; i <= 32; i++)
        {
            double t = i / 32.0 * Math.PI;
            pts.Add(new PointData(cx + Math.Cos(t) * br, bcy + Math.Sin(t) * br));
        }
        pts.AddRange(SampleQuadBezier(cx - br, bcy, cx - br, cy - h * 0.05, cx, cy - h / 2, 16).Skip(1));
        var list = new List<DrawActionBase>();
        list.AddRange(ScanlineFill(pts, fillColor, 2.0, 1.0, gid));
        if (!string.IsNullOrWhiteSpace(strokeColor))
        {
            var outline = new List<PointData>(pts) { new(pts[0].X, pts[0].Y) };
            list.Add(new PenAction { Points = outline, Color = strokeColor!, StrokeWidth = strokeWidth, GroupId = gid });
        }
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    // ─── More composites ──────────────────────────────────────────────────────

    [McpServerTool, Description(
        "COMPOSITE — draws a STICK PERSON: head + body + arms + legs. " +
        "(cx,cy) is the center of the head. size = total figure height. " +
        "pose: standing|waving|running|cheering.")]
    public static string DrawStickPerson(
        double cx, double cy, double size,
        string color = "#000000",
        string? skinColor = null,
        string pose = "standing",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        double headR = size * 0.10;
        double sw = Math.Max(2, size / 80);
        double bodyTop = cy + headR;
        double bodyBot = cy + headR + size * 0.40;
        double hipY = bodyBot;
        double footY = cy + headR + size * 0.85;
        double armY = bodyTop + size * 0.10;
        double armSpan = size * 0.30;
        double legSpread = size * 0.18;

        // Head (circle)
        list.Add(Stamp(Shape(ShapeType.Circle, cx - headR, cy - headR, headR * 2, headR * 2,
                              color, skinColor ?? "#FFE0BD", sw), 1, null, gid));
        // Body
        list.Add(new LineAction { StartX = cx, StartY = bodyTop, EndX = cx, EndY = bodyBot, Color = color, StrokeWidth = sw, Opacity = 1, GroupId = gid });

        // Arms — pose-dependent
        (double laX, double laY, double raX, double raY) = pose switch
        {
            "waving"   => (cx - armSpan, armY + size * 0.10, cx + armSpan * 0.8, armY - size * 0.25),
            "running"  => (cx - armSpan * 0.6, armY - size * 0.10, cx + armSpan * 0.6, armY + size * 0.20),
            "cheering" => (cx - armSpan * 0.7, armY - size * 0.30, cx + armSpan * 0.7, armY - size * 0.30),
            _          => (cx - armSpan, armY + size * 0.05, cx + armSpan, armY + size * 0.05),
        };
        list.Add(new LineAction { StartX = cx, StartY = armY, EndX = laX, EndY = laY, Color = color, StrokeWidth = sw, Opacity = 1, GroupId = gid });
        list.Add(new LineAction { StartX = cx, StartY = armY, EndX = raX, EndY = raY, Color = color, StrokeWidth = sw, Opacity = 1, GroupId = gid });

        // Legs — pose-dependent
        (double llX, double rlX) = pose == "running" ? (cx - legSpread * 1.4, cx + legSpread * 0.6) : (cx - legSpread, cx + legSpread);
        list.Add(new LineAction { StartX = cx, StartY = hipY, EndX = llX, EndY = footY, Color = color, StrokeWidth = sw, Opacity = 1, GroupId = gid });
        list.Add(new LineAction { StartX = cx, StartY = hipY, EndX = rlX, EndY = footY, Color = color, StrokeWidth = sw, Opacity = 1, GroupId = gid });

        // Tiny smile on the head
        list.Add(Arc(cx, cy + headR * 0.15, headR * 0.4, 20, 160, color, sw * 0.7, gid));
        // Eye dots
        list.Add(Stamp(Shape(ShapeType.Circle, cx - headR * 0.35, cy - headR * 0.15, headR * 0.18, headR * 0.18, color, color, 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, cx + headR * 0.20, cy - headR * 0.15, headR * 0.18, headR * 0.18, color, color, 1), 1, null, gid));
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a cartoon DOG face (front view): head + 2 floppy ears + eyes + nose + mouth + tongue. " +
        "(cx,cy) is the face center; size is the head diameter.")]
    public static string DrawDogFace(
        double cx, double cy, double size,
        string furColor = "#C99363",
        string earColor = "#8B5A2B",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        double r = size / 2;
        string outline = "#2B1810";
        double sw = Math.Max(2, size / 100);
        var list = new List<DrawActionBase>();
        // Floppy ears (filled smooth-ish blobs) — drawn first so head sits on top
        // Left ear
        var leftEar = new[] {
            cx - r * 0.85, cy - r * 0.30,
            cx - r * 1.10, cy + r * 0.05,
            cx - r * 1.05, cy + r * 0.50,
            cx - r * 0.75, cy + r * 0.55,
            cx - r * 0.60, cy + r * 0.10,
        };
        list.AddRange(ScanlineFill(SampleCatmullRom(PointsFromFlat(leftEar), true, 12), earColor, 2.0, 1.0, gid));
        // Right ear (mirror)
        var rightEar = new[] {
            cx + r * 0.85, cy - r * 0.30,
            cx + r * 1.10, cy + r * 0.05,
            cx + r * 1.05, cy + r * 0.50,
            cx + r * 0.75, cy + r * 0.55,
            cx + r * 0.60, cy + r * 0.10,
        };
        list.AddRange(ScanlineFill(SampleCatmullRom(PointsFromFlat(rightEar), true, 12), earColor, 2.0, 1.0, gid));

        // Head (slightly squashed circle = ellipse)
        list.Add(Stamp(Shape(ShapeType.Ellipse, cx - r, cy - r * 0.95, r * 2, r * 1.9, outline, furColor, sw), 1, null, gid));
        // Snout (lighter ellipse low)
        double snW = r * 0.85, snH = r * 0.55;
        list.Add(Stamp(Shape(ShapeType.Ellipse, cx - snW / 2, cy + r * 0.10, snW, snH, outline, "#F4D8B0", sw), 1, null, gid));
        // Nose (filled black triangle on top of snout)
        double nW = r * 0.28, nH = r * 0.18;
        list.Add(Pen(new[] { cx - nW / 2, cy + r * 0.10, cx + nW / 2, cy + r * 0.10, cx, cy + r * 0.10 + nH }, "#1A1A1A", sw, true, gid));
        list.AddRange(ScanlineFill(PointsFromFlat(new[] { cx - nW / 2, cy + r * 0.10, cx + nW / 2, cy + r * 0.10, cx, cy + r * 0.10 + nH }), "#1A1A1A", 1.5, 1.0, gid));
        // Mouth (downward U + small tongue)
        list.Add(Arc(cx, cy + r * 0.40, r * 0.20, 10, 170, outline, sw, gid));
        list.Add(Stamp(Shape(ShapeType.Ellipse, cx - r * 0.10, cy + r * 0.42, r * 0.20, r * 0.15, outline, "#FF7A8A", sw * 0.6), 1, null, gid));
        // Eyes (white + black pupil)
        double eyeDX = r * 0.32, eyeY = cy - r * 0.20, eyeR = r * 0.15;
        list.Add(Stamp(Shape(ShapeType.Circle, cx - eyeDX - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2, outline, "#FFF", sw * 0.7), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, cx + eyeDX - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2, outline, "#FFF", sw * 0.7), 1, null, gid));
        double pR = eyeR * 0.55;
        list.Add(Stamp(Shape(ShapeType.Circle, cx - eyeDX - pR, eyeY - pR + 2, pR * 2, pR * 2, "#000", "#000", 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, cx + eyeDX - pR, eyeY - pR + 2, pR * 2, pR * 2, "#000", "#000", 1), 1, null, gid));
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a simple cartoon BIRD (side view): body + wing + tail + beak + eye. " +
        "(cx,cy) is the body center; size is body length. facing: left|right.")]
    public static string DrawBird(
        double cx, double cy, double size,
        string bodyColor = "#5BAEE6",
        string bellyColor = "#E8F4FB",
        string beakColor = "#FFB347",
        string facing = "right",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        int dir = facing == "left" ? -1 : 1;
        var list = new List<DrawActionBase>();
        double w = size, h = size * 0.65;
        // Body ellipse
        list.Add(Stamp(Shape(ShapeType.Ellipse, cx - w / 2, cy - h / 2, w, h, "#222", bodyColor, 2), 1, null, gid));
        // Belly (smaller lighter ellipse below center)
        double bw = w * 0.6, bh = h * 0.55;
        list.Add(Stamp(Shape(ShapeType.Ellipse, cx - bw / 2 - dir * w * 0.05, cy + h * 0.05, bw, bh, bellyColor, bellyColor, 1), 1, null, gid));
        // Wing (filled smooth shape on top of body)
        var wing = new[] {
            cx - dir * w * 0.10, cy - h * 0.10,
            cx + dir * w * 0.05, cy - h * 0.30,
            cx + dir * w * 0.30, cy - h * 0.05,
            cx + dir * w * 0.20, cy + h * 0.15,
            cx - dir * w * 0.05, cy + h * 0.10,
        };
        list.AddRange(ScanlineFill(SampleCatmullRom(PointsFromFlat(wing), true, 12), "#3A85B8", 2.0, 1.0, gid));
        // Tail (triangle pointing back-down)
        var tail = new[] {
            cx - dir * w * 0.45, cy - h * 0.05,
            cx - dir * w * 0.85, cy + h * 0.05,
            cx - dir * w * 0.45, cy + h * 0.20,
        };
        list.AddRange(ScanlineFill(PointsFromFlat(tail), bodyColor, 2.0, 1.0, gid));
        list.Add(Pen(tail, "#222", 2, true, gid));
        // Beak (filled triangle pointing forward)
        double beakX0 = cx + dir * w * 0.45;
        var beak = new[] {
            beakX0,                         cy - h * 0.05,
            beakX0 + dir * w * 0.18,         cy + 2,
            beakX0,                         cy + h * 0.10,
        };
        list.AddRange(ScanlineFill(PointsFromFlat(beak), beakColor, 1.5, 1.0, gid));
        list.Add(Pen(beak, "#222", 1.5, true, gid));
        // Eye
        double eyeR = h * 0.07;
        list.Add(Stamp(Shape(ShapeType.Circle, cx + dir * w * 0.30 - eyeR, cy - h * 0.18 - eyeR, eyeR * 2, eyeR * 2, "#000", "#000", 1), 1, null, gid));
        // Tiny leg
        list.Add(new LineAction { StartX = cx + dir * w * 0.05, StartY = cy + h / 2, EndX = cx + dir * w * 0.05, EndY = cy + h / 2 + h * 0.30, Color = "#FFB347", StrokeWidth = 3, Opacity = 1, GroupId = gid });
        list.Add(new LineAction { StartX = cx - dir * w * 0.10, StartY = cy + h / 2, EndX = cx - dir * w * 0.10, EndY = cy + h / 2 + h * 0.30, Color = "#FFB347", StrokeWidth = 3, Opacity = 1, GroupId = gid });
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a cartoon FISH (side view): body + tail fin + eye + mouth + side fin. " +
        "(cx,cy) is body center; size is body length. facing: left|right.")]
    public static string DrawFish(
        double cx, double cy, double size,
        string bodyColor = "#FF8E3C",
        string finColor = "#E0701F",
        string facing = "right",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        int dir = facing == "right" ? 1 : -1;
        var list = new List<DrawActionBase>();
        double w = size, h = size * 0.55;
        // Body — fish-shaped (pointy at front, curved at back where tail meets)
        var body = new[] {
            cx + dir * w * 0.45, cy - h * 0.15,             // mouth top
            cx + dir * w * 0.50, cy + h * 0.05,             // mouth bottom (point)
            cx + dir * w * 0.15, cy + h / 2,                // belly
            cx - dir * w * 0.30, cy + h * 0.30,             // tail base bottom
            cx - dir * w * 0.25, cy,                        // tail base mid
            cx - dir * w * 0.30, cy - h * 0.30,             // tail base top
            cx + dir * w * 0.15, cy - h / 2,                // back top
        };
        list.AddRange(ScanlineFill(SampleCatmullRom(PointsFromFlat(body), true, 14), bodyColor, 2.0, 1.0, gid));
        // Tail fin
        var tail = new[] {
            cx - dir * w * 0.30, cy - h * 0.30,
            cx - dir * w * 0.50, cy - h * 0.45,
            cx - dir * w * 0.45, cy,
            cx - dir * w * 0.50, cy + h * 0.45,
            cx - dir * w * 0.30, cy + h * 0.30,
        };
        list.AddRange(ScanlineFill(PointsFromFlat(tail), finColor, 2.0, 1.0, gid));
        list.Add(Pen(tail, "#222", 2, true, gid));
        // Side fin
        var sideFin = new[] {
            cx + dir * w * 0.0, cy + h * 0.05,
            cx - dir * w * 0.05, cy + h * 0.30,
            cx + dir * w * 0.15, cy + h * 0.20,
        };
        list.AddRange(ScanlineFill(PointsFromFlat(sideFin), finColor, 2.0, 1.0, gid));
        // Eye
        double eyeR = h * 0.10;
        list.Add(Stamp(Shape(ShapeType.Circle, cx + dir * w * 0.28 - eyeR, cy - h * 0.10 - eyeR, eyeR * 2, eyeR * 2, "#222", "#FFF", 1.5), 1, null, gid));
        double pR = eyeR * 0.55;
        list.Add(Stamp(Shape(ShapeType.Circle, cx + dir * w * 0.30 - pR, cy - h * 0.10 - pR, pR * 2, pR * 2, "#000", "#000", 1), 1, null, gid));
        // Body outline
        var bodyOutline = SampleCatmullRom(PointsFromFlat(body), true, 14);
        bodyOutline.Add(new PointData(bodyOutline[0].X, bodyOutline[0].Y));
        list.Add(new PenAction { Points = bodyOutline, Color = "#222", StrokeWidth = 2, GroupId = gid });
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a simple CAR (side view): body + roof + 2 wheels + 2 windows + headlight. " +
        "(baseX, baseY) = ground-line center of the car; width = car length.")]
    public static string DrawCar(
        double baseX, double baseY, double width,
        string bodyColor = "#E84A4A",
        string windowColor = "#B8DCF4",
        string wheelColor = "#222222",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        double bodyH = width * 0.25;
        double roofH = width * 0.18;
        double wheelR = width * 0.10;

        double bodyY = baseY - wheelR - bodyH;
        // Body (rounded-ish: a rect)
        list.Add(Stamp(Shape(ShapeType.Rect, baseX - width / 2, bodyY, width, bodyH, "#222", bodyColor, 2), 1, null, gid));
        // Roof — trapezoid via filled polygon
        var roof = new[] {
            baseX - width * 0.30, bodyY,
            baseX - width * 0.18, bodyY - roofH,
            baseX + width * 0.20, bodyY - roofH,
            baseX + width * 0.32, bodyY,
        };
        list.AddRange(ScanlineFill(PointsFromFlat(roof), bodyColor, 2.0, 1.0, gid));
        list.Add(Pen(roof, "#222", 2, true, gid));
        // Windows
        list.Add(Stamp(Shape(ShapeType.Rect, baseX - width * 0.26, bodyY - roofH + 4, width * 0.20, roofH - 8, "#222", windowColor, 1.5), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Rect, baseX + width * 0.02, bodyY - roofH + 4, width * 0.16, roofH - 8, "#222", windowColor, 1.5), 1, null, gid));
        // Headlight
        list.Add(Stamp(Shape(ShapeType.Circle, baseX + width / 2 - width * 0.06, bodyY + bodyH * 0.30, width * 0.05, width * 0.05, "#222", "#FFE070", 1.5), 1, null, gid));
        // Wheels (outer black, inner grey hub)
        double wheelY = baseY - wheelR;
        double wheelX1 = baseX - width * 0.30;
        double wheelX2 = baseX + width * 0.30;
        list.Add(Stamp(Shape(ShapeType.Circle, wheelX1 - wheelR, wheelY - wheelR, wheelR * 2, wheelR * 2, "#111", wheelColor, 2), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, wheelX2 - wheelR, wheelY - wheelR, wheelR * 2, wheelR * 2, "#111", wheelColor, 2), 1, null, gid));
        double hubR = wheelR * 0.4;
        list.Add(Stamp(Shape(ShapeType.Circle, wheelX1 - hubR, wheelY - hubR, hubR * 2, hubR * 2, "#888", "#888", 1), 1, null, gid));
        list.Add(Stamp(Shape(ShapeType.Circle, wheelX2 - hubR, wheelY - hubR, hubR * 2, hubR * 2, "#888", "#888", 1), 1, null, gid));
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a MOUNTAIN (or mountain range with peaks=N). " +
        "(baseX, baseY) is the base center; width = total range width; height = peak height. " +
        "snowCap = true to add white tops.")]
    public static string DrawMountain(
        double baseX, double baseY, double width, double height,
        int peaks = 1,
        bool snowCap = true,
        string color = "#5C6E83",
        string snowColor = "#FFFFFF",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        peaks = Math.Max(1, peaks);
        double left = baseX - width / 2, right = baseX + width / 2;
        // Build polyline: left base → up to peak1 → down to valley → peak2 → … → right base.
        var pts = new List<PointData> { new(left, baseY) };
        for (int i = 0; i < peaks; i++)
        {
            double peakX = left + width * (i + 0.5) / peaks;
            // Vary peak height a bit
            double pH = height * (0.85 + 0.15 * (i % 2 == 0 ? 1 : -1));
            pts.Add(new PointData(peakX, baseY - pH));
            if (i < peaks - 1)
            {
                double valleyX = left + width * (i + 1.0) / peaks;
                double vY = baseY - pH * 0.45;
                pts.Add(new PointData(valleyX, vY));
            }
        }
        pts.Add(new PointData(right, baseY));
        // Fill
        list.AddRange(ScanlineFill(pts, color, 3.0, 1.0, gid));
        // Outline
        list.Add(new PenAction { Points = new List<PointData>(pts) { new(pts[0].X, pts[0].Y) }, Color = "#222", StrokeWidth = 2, GroupId = gid });
        // Snow caps (a small triangle at each peak)
        if (snowCap)
        {
            for (int i = 0; i < peaks; i++)
            {
                double peakX = left + width * (i + 0.5) / peaks;
                double pH = height * (0.85 + 0.15 * (i % 2 == 0 ? 1 : -1));
                double peakY = baseY - pH;
                double capH = pH * 0.22;
                double capW = capH * 1.4;
                var cap = new[] {
                    peakX - capW / 2 + capW * 0.10, peakY + capH,
                    peakX, peakY,
                    peakX + capW / 2 - capW * 0.10, peakY + capH,
                    peakX + capW * 0.10, peakY + capH * 0.7,
                    peakX - capW * 0.10, peakY + capH * 0.6,
                };
                list.AddRange(ScanlineFill(PointsFromFlat(cap), snowColor, 2.0, 1.0, gid));
            }
        }
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    [McpServerTool, Description(
        "COMPOSITE — draws a BUTTERFLY (top view): 4 wings + body + 2 antennae. " +
        "(cx,cy) is the center; size is wingspan.")]
    public static string DrawButterfly(
        double cx, double cy, double size,
        string wingColor = "#9B5DE5",
        string wingAccent = "#F15BB5",
        string bodyColor = "#1A1A1A",
        string? groupId = null)
    {
        string gid = groupId ?? Guid.NewGuid().ToString();
        var list = new List<DrawActionBase>();
        double w = size / 2;
        // Upper wings (large rounded teardrops)
        for (int side = -1; side <= 1; side += 2)
        {
            var upper = new[] {
                cx,                       cy - size * 0.05,
                cx + side * w * 0.35,      cy - size * 0.45,
                cx + side * w * 0.95,      cy - size * 0.30,
                cx + side * w * 1.00,      cy - size * 0.05,
                cx + side * w * 0.55,      cy + size * 0.02,
            };
            list.AddRange(ScanlineFill(SampleCatmullRom(PointsFromFlat(upper), true, 14), wingColor, 2.0, 1.0, gid));
            // accent dot
            list.Add(Stamp(Shape(ShapeType.Circle, cx + side * w * 0.55, cy - size * 0.25, size * 0.10, size * 0.10, wingAccent, wingAccent, 1), 1, null, gid));
        }
        // Lower wings (smaller)
        for (int side = -1; side <= 1; side += 2)
        {
            var lower = new[] {
                cx,                       cy + size * 0.02,
                cx + side * w * 0.55,      cy + size * 0.05,
                cx + side * w * 0.75,      cy + size * 0.30,
                cx + side * w * 0.45,      cy + size * 0.45,
                cx + side * w * 0.10,      cy + size * 0.20,
            };
            list.AddRange(ScanlineFill(SampleCatmullRom(PointsFromFlat(lower), true, 14), wingAccent, 2.0, 1.0, gid));
        }
        // Body (skinny vertical ellipse)
        list.Add(Stamp(Shape(ShapeType.Ellipse, cx - size * 0.03, cy - size * 0.30, size * 0.06, size * 0.70, bodyColor, bodyColor, 1), 1, null, gid));
        // Head dot
        list.Add(Stamp(Shape(ShapeType.Circle, cx - size * 0.05, cy - size * 0.38, size * 0.10, size * 0.10, bodyColor, bodyColor, 1), 1, null, gid));
        // Antennae — curved bezier strokes
        var antA = SampleQuadBezier(cx - 2, cy - size * 0.36, cx - size * 0.08, cy - size * 0.50, cx - size * 0.18, cy - size * 0.55, 16);
        var antB = SampleQuadBezier(cx + 2, cy - size * 0.36, cx + size * 0.08, cy - size * 0.50, cx + size * 0.18, cy - size * 0.55, 16);
        list.Add(new PenAction { Points = antA, Color = bodyColor, StrokeWidth = 2, GroupId = gid });
        list.Add(new PenAction { Points = antB, Color = bodyColor, StrokeWidth = 2, GroupId = gid });
        return JsonConvert.SerializeObject(list.ToArray(), BatchJson);
    }

    // ─── Transform wrapper ────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Apply an AFFINE TRANSFORM (rotate + scale + translate) to a batch of inner tool calls. " +
        "Pass actionsJson in the same format as DrawMany. The transform is applied around the pivot " +
        "(pivotX, pivotY) — typically the center of the subject. " +
        "Use this to draw a rotated version of any composite/icon (e.g. a heart tilted 30°). " +
        "NOTE: rotation is exact for line/pen/text; for axis-aligned shapes (rect/ellipse/triangle/star/circle) " +
        "only the center point is rotated and the bbox stays axis-aligned (a tilted rect will look the same). " +
        "For rotated shapes prefer DrawFilledPolygon / DrawSmoothCurve which support arbitrary geometry.")]
    public static string DrawTransformed(
        [Description("Same JSON array format as DrawMany.")] string actionsJson,
        double pivotX, double pivotY,
        double rotateDeg = 0, double scale = 1.0,
        double dx = 0, double dy = 0)
    {
        var arr = JArray.Parse(actionsJson);
        var raw = new List<DrawActionBase>();
        foreach (var item in arr)
        {
            var toolName = (string?)item["tool"] ?? throw new ArgumentException("Each batch item needs 'tool'.");
            var args = item["args"] as JObject ?? new JObject();
            string? str = InvokeTool(toolName, args);
            if (string.IsNullOrWhiteSpace(str)) continue;
            var token = JToken.Parse(str);
            if (token is JArray a) foreach (var t in a) raw.Add(JsonConvert.DeserializeObject<DrawActionBase>(t.ToString(), new JsonSerializerSettings { Converters = { new DrawActionConverter() } })!);
            else raw.Add(JsonConvert.DeserializeObject<DrawActionBase>(token.ToString(), new JsonSerializerSettings { Converters = { new DrawActionConverter() } })!);
        }
        double rad = Deg2Rad(rotateDeg);
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        PointData Map(double px, double py)
        {
            double x0 = (px - pivotX) * scale, y0 = (py - pivotY) * scale;
            double xr = x0 * cos - y0 * sin, yr = x0 * sin + y0 * cos;
            return new PointData(xr + pivotX + dx, yr + pivotY + dy);
        }
        foreach (var a in raw)
        {
            switch (a)
            {
                case ShapeAction s:
                    var c = Map(s.X + s.Width / 2, s.Y + s.Height / 2);
                    s.Width = Math.Abs(s.Width * scale); s.Height = Math.Abs(s.Height * scale);
                    s.X = c.X - s.Width / 2; s.Y = c.Y - s.Height / 2; s.StrokeWidth *= scale; break;
                case LineAction l:
                    var p1 = Map(l.StartX, l.StartY); var p2 = Map(l.EndX, l.EndY);
                    l.StartX = p1.X; l.StartY = p1.Y; l.EndX = p2.X; l.EndY = p2.Y; l.StrokeWidth *= scale; break;
                case PenAction pen:
                    pen.Points = pen.Points.Select(pt => Map(pt.X, pt.Y)).ToList(); pen.StrokeWidth *= scale; break;
                case TextAction t:
                    var pt = Map(t.X, t.Y); t.X = pt.X; t.Y = pt.Y; t.FontSize *= scale; break;
            }
        }
        return JsonConvert.SerializeObject(raw.ToArray(), BatchJson);
    }

    // ─── Palette helper ───────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Returns a harmonious 5-color palette (#RRGGBB strings) for a theme. " +
        "theme: warm|cool|pastel|monochrome|earth|neon|forest|ocean|sunset|grayscale. " +
        "Use this to pick coordinated colors for a scene without inventing hex codes.")]
    public static string MakePalette(string theme = "warm")
    {
        string[] colors = theme.ToLowerInvariant() switch
        {
            "cool"      => new[] { "#1A3A5F", "#2E6BA8", "#4FA8E0", "#A8D8F0", "#E8F4FB" },
            "pastel"    => new[] { "#FFB7B2", "#FFDAC1", "#FFF1B6", "#B5EAD7", "#C7CEEA" },
            "monochrome"=> new[] { "#1A1A1A", "#4A4A4A", "#7A7A7A", "#AAAAAA", "#E0E0E0" },
            "earth"     => new[] { "#3E2C20", "#6B4530", "#A77F58", "#D4B895", "#F4E4C8" },
            "neon"      => new[] { "#FF006E", "#FB5607", "#FFBE0B", "#8338EC", "#3A86FF" },
            "forest"    => new[] { "#1B3A1F", "#2D5A3D", "#5A8B5A", "#A8C4A3", "#E8F0DC" },
            "ocean"     => new[] { "#003049", "#0A6E80", "#4FA8B8", "#90D8DC", "#E0F4F4" },
            "sunset"    => new[] { "#22223B", "#4A4E69", "#9A8C98", "#F2A65A", "#FFD93D" },
            "grayscale" => new[] { "#000000", "#444444", "#888888", "#CCCCCC", "#FFFFFF" },
            _           => new[] { "#8B1A0F", "#C9402F", "#E8A85C", "#FFD93D", "#FFF8E0" }, // warm
        };
        return JsonConvert.SerializeObject(colors);
    }

    // ─── Internal: scanline polygon fill ──────────────────────────────────────

    /// <summary>
    /// Standard horizontal scanline polygon fill. For each y-row in the polygon's
    /// bounding box, we find every edge intersection, sort them, and emit one
    /// LineAction per [xL, xR] pair. Works for convex AND concave polygons.
    /// yStep ≈ 2–3px gives a visually solid fill; smaller yStep = smoother but
    /// more strokes. StrokeWidth is set slightly bigger than yStep so adjacent
    /// rows blend without gaps.
    /// </summary>
    private static List<LineAction> ScanlineFill(
        List<PointData> poly, string fillColor, double yStep, double opacity, string gid)
    {
        var lines = new List<LineAction>();
        if (poly.Count < 3) return lines;
        double minY = poly.Min(p => p.Y), maxY = poly.Max(p => p.Y);
        int n = poly.Count;
        double sw = yStep + 0.6;
        for (double y = minY + yStep / 2; y <= maxY; y += yStep)
        {
            var xs = new List<double>(8);
            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];
                // Edge crosses scanline if exactly one endpoint is above. Use half-open rule
                // to avoid double-counting at vertices: count if (a.Y <= y < b.Y) || (b.Y <= y < a.Y).
                bool aBelow = a.Y <= y;
                bool bBelow = b.Y <= y;
                if (aBelow == bBelow) continue;
                if (Math.Abs(b.Y - a.Y) < 1e-9) continue;
                double t = (y - a.Y) / (b.Y - a.Y);
                xs.Add(a.X + t * (b.X - a.X));
            }
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                lines.Add(new LineAction
                {
                    StartX = xs[i], StartY = y, EndX = xs[i + 1], EndY = y,
                    Color = fillColor, StrokeWidth = sw,
                    Opacity = Math.Clamp(opacity, 0.0, 1.0), GroupId = gid
                });
            }
        }
        return lines;
    }
}

