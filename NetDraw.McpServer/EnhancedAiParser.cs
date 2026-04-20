using System.Text.RegularExpressions;
using NetDraw.Shared.Interfaces;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;

namespace NetDraw.McpServer;

/// <summary>
/// Rule-based AI parser — fallback when Claude API key is not set.
/// Supports Vietnamese + English, multi-object scenes, and repeated shapes.
/// Canvas safe-zone: 0-1000 × 0-700 (visible area at 1× zoom).
/// </summary>
public class EnhancedAiParser : IAiParser
{
    private static readonly Random Rng = new();

    // Working viewport (not the full 3000×2000 canvas)
    private const double W = 1000;
    private const double H = 700;

    public Task<List<DrawActionBase>> ParseAsync(string command) =>
        Task.FromResult(Parse(command, "ai"));

    public static List<DrawActionBase> Parse(string prompt, string userId)
    {
        var p = prompt.ToLower().Trim();

        // 1. Complex scenes
        var scene = ParseScene(p, userId);
        if (scene.Count > 0) return Stamp(scene, userId);

        // 2. "draw N <shape>" — repeated shapes
        var repeated = ParseRepeated(p, userId);
        if (repeated.Count > 0) return Stamp(repeated, userId);

        // 3. Single shape / element
        var single = ParseSingle(p, userId);
        if (single.Count > 0) return Stamp(single, userId);

        // 4. Nothing matched — draw a random star as fallback so the user sees *something*
        return Stamp(new List<DrawActionBase> {
            ShapeCenter(userId, ShapeType.Star, W/2, H/2, 120, 120, "#FFD700", "#FFD700")
        }, userId);
    }

    // ─── Scene detection ───────────────────────────────────────────────────────

    private static List<DrawActionBase> ParseScene(string p, string userId)
    {
        var a = new List<DrawActionBase>();

        // Landscape / bầu trời
        if (Has(p, "bầu trời", "sky", "phong cảnh", "landscape", "cảnh thiên nhiên"))
        {
            a.Add(Shape(userId, ShapeType.Rect, 0, 0, W, H * 0.6, "#87CEEB", "#87CEEB"));
            a.Add(Shape(userId, ShapeType.Rect, 0, H * 0.6, W, H * 0.4, "#228B22", "#228B22"));
            if (Has(p, "mặt trời", "sun", "nắng")) a.AddRange(DrawSun(userId, 800, 100));
            if (Has(p, "mây", "cloud"))             { a.AddRange(DrawCloud(userId, 200, 90)); a.AddRange(DrawCloud(userId, 500, 60)); }
            if (Has(p, "cây", "tree"))               { a.AddRange(DrawTree(userId, 250, 380)); a.AddRange(DrawTree(userId, 650, 360)); }
            if (Has(p, "nhà", "house"))               a.AddRange(DrawHouse(userId, 450, 370));
            return a;
        }

        // Mặt cười / smiley
        if (Has(p, "mặt cười", "smiley", "smile", "emoji", "mặt vui", "happy face"))
        {
            string col = Color(p, "#FFD700");
            double cx = W / 2, cy = H / 2;
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy, 160, 160, "#333333", col));
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx - 40, cy - 30, 22, 22, "#333333", "#333333"));
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx + 40, cy - 30, 22, 22, "#333333", "#333333"));
            a.Add(ShapeCenter(userId, ShapeType.Ellipse, cx, cy + 30, 70, 35, "#333333", null));
            return a;
        }

        // Hoa / flower
        if (Has(p, "hoa", "flower", "bông hoa"))
        {
            string col = Color(p, "#FF69B4");
            var (cx, cy) = Position(p);
            for (int i = 0; i < 6; i++)
            {
                double angle = i * Math.PI / 3;
                a.Add(ShapeCenter(userId, ShapeType.Circle,
                    cx + 45 * Math.Cos(angle), cy + 45 * Math.Sin(angle), 38, 38, col, col));
            }
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy, 30, 30, "#FFD700", "#FFD700"));
            a.Add(Line(userId, cx, cy + 35, cx, cy + 150, "#228B22", 5));
            return a;
        }

        // Nhà / house
        if (Has(p, "ngôi nhà", "ngôi nha", "house", "căn nhà"))
        {
            var (cx, cy) = Position(p);
            return DrawHouse(userId, cx, cy);
        }

        // Cây / tree
        if (Has(p, "cái cây", "cây xanh", "tree"))
        {
            var (cx, cy) = Position(p);
            return DrawTree(userId, cx, cy);
        }

        // Mặt trời / sun
        if (Has(p, "mặt trời", "sun"))
        {
            var (cx, cy) = Position(p);
            return DrawSun(userId, cx, cy);
        }

        // Trái tim / heart
        if (Has(p, "trái tim", "heart", "tình yêu", "love"))
        {
            string col = Color(p, "#FF1493");
            var (cx, cy) = Position(p);
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx - 28, cy - 20, 50, 50, col, col));
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx + 28, cy - 20, 50, 50, col, col));
            a.Add(Shape(userId, ShapeType.Triangle, cx - 52, cy + 2, 104, 62, col, col));
            return a;
        }

        // Xe / car
        if (Has(p, "xe hơi", "ô tô", "car", "xe ô tô"))
        {
            string col = Color(p, "#E74C3C");
            var (cx, cy) = Position(p);
            a.Add(Shape(userId, ShapeType.Rect, cx - 80, cy - 20, 160, 40, col, col));
            a.Add(Shape(userId, ShapeType.Rect, cx - 45, cy - 55, 90, 35, "#3498DB", "#3498DB"));
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx - 45, cy + 22, 28, 28, "#333", "#333"));
            a.Add(ShapeCenter(userId, ShapeType.Circle, cx + 45, cy + 22, 28, 28, "#333", "#333"));
            return a;
        }

        // Cầu vồng / rainbow
        if (Has(p, "cầu vồng", "rainbow"))
        {
            string[] cols = { "#FF0000", "#FF7F00", "#FFFF00", "#00CC00", "#0000FF", "#4B0082", "#9400D3" };
            for (int i = 0; i < cols.Length; i++)
                a.Add(ShapeCenter(userId, ShapeType.Ellipse, W / 2, H * 0.65,
                    (220 - i * 20) * 2, 220 - i * 20, cols[i], null, 14));
            return a;
        }

        // Mây / cloud (standalone)
        if (Has(p, "đám mây", "cloud", "mây"))
        {
            var (cx, cy) = Position(p);
            return DrawCloud(userId, cx, cy);
        }

        // Ngôi sao / star (explicit standalone — 1 star, not repeated)
        if (Has(p, "ngôi sao 5 cánh", "five-pointed star", "pentagram"))
        {
            string col = Color(p, "#FFD700");
            var (cx, cy) = Position(p);
            double sz = Size(p);
            a.Add(ShapeCenter(userId, ShapeType.Star, cx, cy, sz, sz, col, col));
            return a;
        }

        return a;
    }

    // ─── Repeated shapes ("vẽ 3 hình tròn") ──────────────────────────────────

    private static List<DrawActionBase> ParseRepeated(string p, string userId)
    {
        // Match patterns like "3 circles", "5 hình tròn", "vẽ 4 ngôi sao"
        var m = Regex.Match(p, @"\b(\d+)\s+(?:hình\s+)?(tròn|circle|vuông|square|tam giác|triangle|ngôi sao|star|chữ nhật|rectangle)\b", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(p, @"(?:draw|vẽ)\s+(\d+)\s+(\w+)", RegexOptions.IgnoreCase);
        if (!m.Success) return new();

        if (!int.TryParse(m.Groups[1].Value, out int count) || count < 1 || count > 20) return new();
        var shapeWord = m.Groups[2].Value.ToLower();

        ShapeType sType;
        if (Has(shapeWord, "tròn", "circle")) sType = ShapeType.Circle;
        else if (Has(shapeWord, "vuông", "square")) sType = ShapeType.Rect;
        else if (Has(shapeWord, "tam giác", "triangle")) sType = ShapeType.Triangle;
        else if (Has(shapeWord, "sao", "star")) sType = ShapeType.Star;
        else if (Has(shapeWord, "chữ nhật", "rectangle")) sType = ShapeType.Ellipse;
        else return new();

        string col = Color(p, RandomBright());
        double sz = Size(p) * 0.8;
        double margin = sz + 20;
        double usableW = W - margin * 2;
        var actions = new List<DrawActionBase>();

        for (int i = 0; i < count; i++)
        {
            double cx = margin + (usableW / Math.Max(count - 1, 1)) * i;
            if (count == 1) cx = W / 2;
            double cy = H / 2;
            string itemColor = Color(p, RandomBright());
            actions.Add(ShapeCenter(userId, sType, cx, cy, sz, sz, itemColor, HasFill(p) ? itemColor : null));
        }
        return actions;
    }

    // ─── Single-shape parser ──────────────────────────────────────────────────

    private static List<DrawActionBase> ParseSingle(string p, string userId)
    {
        var a = new List<DrawActionBase>();
        string col = Color(p, "#2196F3");
        var (x, y) = Position(p);
        double sz = Size(p);
        bool fill = HasFill(p);

        if (Has(p, "tròn", "circle", "hình tròn"))
            a.Add(ShapeCenter(userId, ShapeType.Circle, x, y, sz, sz, col, fill ? col : null));
        else if (Has(p, "elip", "ellipse", "hình elip"))
            a.Add(ShapeCenter(userId, ShapeType.Ellipse, x, y, sz * 1.5, sz, col, fill ? col : null));
        else if (Has(p, "vuông", "square", "hình vuông"))
            a.Add(Shape(userId, ShapeType.Rect, x - sz/2, y - sz/2, sz, sz, col, fill ? col : null));
        else if (Has(p, "chữ nhật", "rectangle", "rect"))
            a.Add(Shape(userId, ShapeType.Rect, x - sz*0.75, y - sz/2, sz*1.5, sz, col, fill ? col : null));
        else if (Has(p, "tam giác", "triangle"))
            a.Add(Shape(userId, ShapeType.Triangle, x - sz/2, y - sz/2, sz, sz, col, fill ? col : null));
        else if (Has(p, "ngôi sao", "star", "sao"))
            a.Add(Shape(userId, ShapeType.Star, x - sz/2, y - sz/2, sz, sz, col, fill ? col : null));
        else if (Has(p, "đường thẳng", "line", "đường"))
            a.Add(Line(userId, x - sz/2, y, x + sz/2, y, col));
        else if (Has(p, "mũi tên", "arrow"))
            a.Add(new LineAction { UserId = userId, Color = col, StrokeWidth = 3,
                StartX = x - sz/2, StartY = y, EndX = x + sz/2, EndY = y, HasArrow = true });
        else if (Has(p, "text", "chữ", "viết", "write"))
        {
            var txt = ExtractQuoted(p) ?? "Hello!";
            a.Add(new TextAction { UserId = userId, X = x - 60, Y = y - 16,
                Text = txt, Color = col, FontSize = 28 });
        }
        else if (Has(p, "xóa", "clear", "xóa tất cả", "clean"))
        {
            // Not handled here — return empty so caller can deal with it
        }

        return a;
    }

    // ─── Compound drawing helpers ─────────────────────────────────────────────

    private static List<DrawActionBase> DrawSun(string userId, double cx, double cy)
    {
        var a = new List<DrawActionBase>();
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy, 70, 70, "#FFD700", "#FFD700"));
        for (int i = 0; i < 12; i++)
        {
            double ang = i * Math.PI / 6;
            a.Add(Line(userId,
                cx + 42 * Math.Cos(ang), cy + 42 * Math.Sin(ang),
                cx + 72 * Math.Cos(ang), cy + 72 * Math.Sin(ang),
                "#FFD700", 4));
        }
        return a;
    }

    private static List<DrawActionBase> DrawCloud(string userId, double cx, double cy)
    {
        var a = new List<DrawActionBase>();
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx,      cy,      60, 60, "#FFF", "#FFF"));
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx + 40, cy - 8,  50, 50, "#FFF", "#FFF"));
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx - 35, cy + 8,  45, 45, "#FFF", "#FFF"));
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx + 12, cy + 14, 52, 52, "#FFF", "#FFF"));
        return a;
    }

    private static List<DrawActionBase> DrawTree(string userId, double cx, double cy)
    {
        var a = new List<DrawActionBase>();
        a.Add(Shape(userId, ShapeType.Rect, cx - 14, cy, 28, 80, "#8B4513", "#8B4513"));
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx,      cy - 15, 80, 80, "#228B22", "#32CD32"));
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx - 30, cy + 4,  55, 55, "#228B22", "#2E8B57"));
        a.Add(ShapeCenter(userId, ShapeType.Circle, cx + 30, cy + 4,  55, 55, "#228B22", "#2E8B57"));
        return a;
    }

    private static List<DrawActionBase> DrawHouse(string userId, double cx, double cy)
    {
        var a = new List<DrawActionBase>();
        a.Add(Shape(userId,   ShapeType.Rect,     cx - 65, cy,      130, 100, "#8B4513", "#DEB887"));
        a.Add(Shape(userId,   ShapeType.Triangle,  cx - 80, cy,      160, 65,  "#B22222", "#B22222"));
        a.Add(Shape(userId,   ShapeType.Rect,     cx - 16, cy + 50, 32, 50,  "#654321", "#654321"));
        a.Add(Shape(userId,   ShapeType.Rect,     cx + 26, cy + 24, 25, 25,  "#87CEEB", "#87CEEB"));
        a.Add(Shape(userId,   ShapeType.Rect,     cx - 52, cy + 24, 25, 25,  "#87CEEB", "#87CEEB"));
        return a;
    }

    // ─── Parsing helpers ──────────────────────────────────────────────────────

    private static string Color(string p, string def)
    {
        if (Has(p, "đỏ", "red"))               return "#F44336";
        if (Has(p, "xanh dương", "blue", "xanh da trời")) return "#2196F3";
        if (Has(p, "xanh lá", "green", "xanh lục"))       return "#4CAF50";
        if (Has(p, "vàng", "yellow"))           return "#FFD700";
        if (Has(p, "cam", "orange"))            return "#FF9800";
        if (Has(p, "tím", "purple", "violet"))  return "#9C27B0";
        if (Has(p, "hồng", "pink"))             return "#E91E63";
        if (Has(p, "trắng", "white"))           return "#FFFFFF";
        if (Has(p, "đen", "black"))             return "#212121";
        if (Has(p, "nâu", "brown"))             return "#795548";
        if (Has(p, "xám", "gray", "grey"))      return "#9E9E9E";
        if (Has(p, "cyan", "xanh ngọc"))        return "#00BCD4";

        var m = Regex.Match(p, @"#[0-9a-fA-F]{6}");
        return m.Success ? m.Value : def;
    }

    private static (double x, double y) Position(string p)
    {
        if (Has(p, "giữa", "center", "chính giữa", "middle")) return (W / 2, H / 2);
        if (Has(p, "góc trái trên", "top left"))               return (150, 130);
        if (Has(p, "góc phải trên", "top right"))              return (W - 150, 130);
        if (Has(p, "góc trái dưới", "bottom left"))            return (150, H - 130);
        if (Has(p, "góc phải dưới", "bottom right"))           return (W - 150, H - 130);
        if (Has(p, "trên", "top"))                             return (W / 2, 130);
        if (Has(p, "dưới", "bottom"))                          return (W / 2, H - 130);
        if (Has(p, "trái", "left"))                            return (150, H / 2);
        if (Has(p, "phải", "right"))                           return (W - 150, H / 2);

        var m = Regex.Match(p, @"(?:at|tại|vị trí|pos)\s+(\d+)[,\s]+(\d+)");
        if (m.Success)
            return (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value));

        return (W / 2, H / 2);
    }

    private static double Size(string p)
    {
        if (Has(p, "rất nhỏ", "tiny", "very small")) return 30;
        if (Has(p, "nhỏ", "small", "bé"))            return 60;
        if (Has(p, "rất to", "very big", "huge", "khổng lồ")) return 220;
        if (Has(p, "to", "lớn", "big", "large"))      return 130;

        var m = Regex.Match(p, @"(?:size|kích thước|r|radius|cỡ)\s*=?\s*(\d+)");
        if (m.Success) return double.Parse(m.Groups[1].Value);

        return 100;
    }

    private static bool HasFill(string p) =>
        Has(p, "tô", "fill", "đặc", "tô màu", "solid", "filled");

    private static string? ExtractQuoted(string p)
    {
        var m = Regex.Match(p, "\"([^\"]+)\"");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(p, "'([^']+)'");
        if (m.Success) return m.Groups[1].Value;
        return null;
    }

    private static bool Has(string text, params string[] kw) =>
        kw.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string RandomBright()
    {
        string[] colors = { "#F44336","#E91E63","#9C27B0","#2196F3","#00BCD4",
                            "#4CAF50","#FF9800","#FFD700","#FF5722","#607D8B" };
        return colors[Rng.Next(colors.Length)];
    }

    // ─── DrawAction factories ─────────────────────────────────────────────────

    private static ShapeAction Shape(string uid, ShapeType t, double x, double y, double w, double h,
        string col, string? fill, double sw = 2) =>
        new() { UserId = uid, Id = Guid.NewGuid().ToString(),
                ShapeType = t, X = x, Y = y, Width = w, Height = h,
                Color = col, FillColor = fill, StrokeWidth = sw };

    private static ShapeAction ShapeCenter(string uid, ShapeType t, double cx, double cy, double w, double h,
        string col, string? fill, double sw = 2) =>
        new() { UserId = uid, Id = Guid.NewGuid().ToString(),
                ShapeType = t, X = cx, Y = cy, Width = w, Height = h,
                Color = col, FillColor = fill, StrokeWidth = sw };

    private static LineAction Line(string uid, double x1, double y1, double x2, double y2,
        string col, double sw = 2) =>
        new() { UserId = uid, Id = Guid.NewGuid().ToString(),
                Color = col, StrokeWidth = sw,
                StartX = x1, StartY = y1, EndX = x2, EndY = y2 };

    // ─── Post-processing ──────────────────────────────────────────────────────

    private static List<DrawActionBase> Stamp(List<DrawActionBase> actions, string userId)
    {
        foreach (var a in actions)
        {
            if (string.IsNullOrEmpty(a.Id))    a.Id     = Guid.NewGuid().ToString();
            if (string.IsNullOrEmpty(a.UserId)) a.UserId = userId;
        }
        return actions;
    }
}
