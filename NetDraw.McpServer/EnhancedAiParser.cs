using System.Text.RegularExpressions;
using NetDraw.Shared.Models;

namespace NetDraw.McpServer;

/// <summary>
/// Enhanced AI Parser - phân tích lệnh vẽ bằng ngôn ngữ tự nhiên
/// Hỗ trợ cả tiếng Việt và tiếng Anh
/// Phức tạp hơn FallbackParser, có thể vẽ nhiều hình, scene phức tạp
/// </summary>
public static class EnhancedAiParser
{
    private static readonly Random Rng = new();
    private const double CanvasW = 800;
    private const double CanvasH = 600;

    public static List<DrawAction> Parse(string prompt, string userId)
    {
        prompt = prompt.ToLower().Trim();

        // Kiểm tra scene phức tạp trước
        var sceneActions = ParseScene(prompt, userId);
        if (sceneActions.Count > 0) return sceneActions;

        // Parse đơn lẻ
        return ParseSingle(prompt, userId);
    }

    private static List<DrawAction> ParseScene(string prompt, string userId)
    {
        var actions = new List<DrawAction>();

        // === Cảnh bầu trời / landscape ===
        if (ContainsAny(prompt, "bầu trời", "sky", "phong cảnh", "landscape", "cảnh"))
        {
            // Nền trời
            actions.Add(Shape(userId, ShapeType.Rectangle, 0, 0, CanvasW, CanvasH * 0.6, "#87CEEB", "#87CEEB"));
            // Nền đất
            actions.Add(Shape(userId, ShapeType.Rectangle, 0, CanvasH * 0.6, CanvasW, CanvasH * 0.4, "#228B22", "#228B22"));

            if (ContainsAny(prompt, "mặt trời", "sun", "nắng"))
            {
                actions.AddRange(DrawSun(userId, 650, 80));
            }

            if (ContainsAny(prompt, "mây", "cloud", "clouds"))
            {
                actions.AddRange(DrawCloud(userId, 150, 80));
                actions.AddRange(DrawCloud(userId, 400, 50));
            }

            if (ContainsAny(prompt, "cây", "tree", "trees"))
            {
                actions.AddRange(DrawTree(userId, 200, 300));
                actions.AddRange(DrawTree(userId, 500, 280));
            }

            if (ContainsAny(prompt, "nhà", "house"))
            {
                actions.AddRange(DrawHouse(userId, 350, 280));
            }

            return actions;
        }

        // === Mặt cười / smiley ===
        if (ContainsAny(prompt, "mặt cười", "smiley", "smile", "emoji", "mặt vui"))
        {
            string color = ParseColor(prompt, "#FFD700");
            double cx = CanvasW / 2, cy = CanvasH / 2;
            // Mặt
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy, 120, 120, "#000000", color));
            // Mắt trái
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx - 30, cy - 20, 15, 15, "#000000", "#000000"));
            // Mắt phải
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx + 30, cy - 20, 15, 15, "#000000", "#000000"));
            // Miệng (dùng ellipse nửa dưới)
            actions.Add(ShapeCenter(userId, ShapeType.Ellipse, cx, cy + 25, 50, 25, "#000000", null));
            return actions;
        }

        // === Hoa ===
        if (ContainsAny(prompt, "hoa", "flower", "bông hoa"))
        {
            string color = ParseColor(prompt, "#FF69B4");
            var (cx, cy) = ParsePosition(prompt);
            // Cánh hoa
            for (int i = 0; i < 6; i++)
            {
                double angle = i * Math.PI / 3;
                double px = cx + 35 * Math.Cos(angle);
                double py = cy + 35 * Math.Sin(angle);
                actions.Add(ShapeCenter(userId, ShapeType.Circle, px, py, 30, 30, color, color));
            }
            // Nhụy
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy, 25, 25, "#FFD700", "#FFD700"));
            // Thân
            actions.Add(Line(userId, cx, cy + 30, cx, cy + 120, "#228B22", 4));
            return actions;
        }

        // === Ngôi nhà ===
        if (ContainsAny(prompt, "nhà", "house", "ngôi nhà"))
        {
            var (cx, cy) = ParsePosition(prompt);
            return DrawHouse(userId, cx, cy);
        }

        // === Cây ===
        if (ContainsAny(prompt, "cây", "tree"))
        {
            var (cx, cy) = ParsePosition(prompt);
            return DrawTree(userId, cx, cy);
        }

        // === Mặt trời ===
        if (ContainsAny(prompt, "mặt trời", "sun"))
        {
            var (cx, cy) = ParsePosition(prompt);
            return DrawSun(userId, cx, cy);
        }

        // === Trái tim ===
        if (ContainsAny(prompt, "trái tim", "heart", "tim"))
        {
            string color = ParseColor(prompt, "#FF1493");
            var (cx, cy) = ParsePosition(prompt);
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx - 20, cy - 15, 35, 35, color, color));
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx + 20, cy - 15, 35, 35, color, color));
            actions.Add(Shape(userId, ShapeType.Triangle, cx - 38, cy, 76, 50, color, color));
            return actions;
        }

        // === Xe hơi ===
        if (ContainsAny(prompt, "xe", "car", "ô tô", "xe hơi"))
        {
            string color = ParseColor(prompt, "#E74C3C");
            var (cx, cy) = ParsePosition(prompt);
            // Thân xe
            actions.Add(Shape(userId, ShapeType.Rectangle, cx - 60, cy - 15, 120, 30, color, color));
            // Cabin
            actions.Add(Shape(userId, ShapeType.Rectangle, cx - 30, cy - 40, 60, 25, "#3498DB", "#3498DB"));
            // Bánh xe
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx - 35, cy + 15, 20, 20, "#333", "#333"));
            actions.Add(ShapeCenter(userId, ShapeType.Circle, cx + 35, cy + 15, 20, 20, "#333", "#333"));
            return actions;
        }

        // === Cầu vồng ===
        if (ContainsAny(prompt, "cầu vồng", "rainbow"))
        {
            string[] rainbowColors = { "#FF0000", "#FF7F00", "#FFFF00", "#00FF00", "#0000FF", "#4B0082", "#9400D3" };
            double baseR = 180;
            for (int i = 0; i < rainbowColors.Length; i++)
            {
                double r = baseR - i * 15;
                actions.Add(ShapeCenter(userId, ShapeType.Ellipse, CanvasW / 2, CanvasH * 0.6, r * 2, r, rainbowColors[i], null, 12));
            }
            return actions;
        }

        return actions;
    }

    private static List<DrawAction> ParseSingle(string prompt, string userId)
    {
        var actions = new List<DrawAction>();
        string color = ParseColor(prompt, "#000000");
        var (x, y) = ParsePosition(prompt);
        double size = ParseSize(prompt);

        if (ContainsAny(prompt, "tròn", "circle"))
            actions.Add(ShapeCenter(userId, ShapeType.Circle, x, y, size, size, color, HasFill(prompt) ? color : null));
        else if (ContainsAny(prompt, "vuông", "square"))
            actions.Add(Shape(userId, ShapeType.Rectangle, x - size / 2, y - size / 2, size, size, color, HasFill(prompt) ? color : null));
        else if (ContainsAny(prompt, "chữ nhật", "rectangle", "rect"))
            actions.Add(Shape(userId, ShapeType.Rectangle, x - size * 0.75, y - size / 2, size * 1.5, size, color, HasFill(prompt) ? color : null));
        else if (ContainsAny(prompt, "tam giác", "triangle"))
            actions.Add(Shape(userId, ShapeType.Triangle, x - size / 2, y - size / 2, size, size, color, HasFill(prompt) ? color : null));
        else if (ContainsAny(prompt, "ngôi sao", "star", "sao"))
            actions.Add(Shape(userId, ShapeType.Star, x - size / 2, y - size / 2, size, size, color, HasFill(prompt) ? color : null));
        else if (ContainsAny(prompt, "ellipse", "elip"))
            actions.Add(Shape(userId, ShapeType.Ellipse, x - size * 0.65, y - size / 2, size * 1.3, size, color, HasFill(prompt) ? color : null));
        else if (ContainsAny(prompt, "đường", "line"))
            actions.Add(Line(userId, x - size / 2, y, x + size / 2, y, color));
        else if (ContainsAny(prompt, "text", "chữ", "viết"))
        {
            string text = ExtractText(prompt);
            actions.Add(Text(userId, x, y, text, color));
        }

        return actions;
    }

    #region Drawing Helpers

    private static List<DrawAction> DrawSun(string userId, double cx, double cy)
    {
        var actions = new List<DrawAction>();
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy, 50, 50, "#FFD700", "#FFD700"));
        for (int i = 0; i < 12; i++)
        {
            double angle = i * Math.PI / 6;
            actions.Add(Line(userId,
                cx + 30 * Math.Cos(angle), cy + 30 * Math.Sin(angle),
                cx + 55 * Math.Cos(angle), cy + 55 * Math.Sin(angle),
                "#FFD700", 3));
        }
        return actions;
    }

    private static List<DrawAction> DrawCloud(string userId, double cx, double cy)
    {
        var actions = new List<DrawAction>();
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy, 40, 40, "#FFF", "#FFF"));
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx + 25, cy - 5, 35, 35, "#FFF", "#FFF"));
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx - 25, cy + 5, 30, 30, "#FFF", "#FFF"));
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx + 10, cy + 10, 35, 35, "#FFF", "#FFF"));
        return actions;
    }

    private static List<DrawAction> DrawTree(string userId, double cx, double cy)
    {
        var actions = new List<DrawAction>();
        // Thân cây
        actions.Add(Shape(userId, ShapeType.Rectangle, cx - 10, cy, 20, 60, "#8B4513", "#8B4513"));
        // Tán lá
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx, cy - 10, 60, 60, "#228B22", "#32CD32"));
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx - 20, cy, 40, 40, "#228B22", "#2E8B57"));
        actions.Add(ShapeCenter(userId, ShapeType.Circle, cx + 20, cy, 40, 40, "#228B22", "#2E8B57"));
        return actions;
    }

    private static List<DrawAction> DrawHouse(string userId, double cx, double cy)
    {
        var actions = new List<DrawAction>();
        // Tường
        actions.Add(Shape(userId, ShapeType.Rectangle, cx - 50, cy, 100, 80, "#8B4513", "#DEB887"));
        // Mái
        actions.Add(Shape(userId, ShapeType.Triangle, cx - 60, cy, 120, 50, "#B22222", "#B22222"));
        // Cửa
        actions.Add(Shape(userId, ShapeType.Rectangle, cx - 12, cy + 40, 24, 40, "#654321", "#654321"));
        // Cửa sổ
        actions.Add(Shape(userId, ShapeType.Rectangle, cx + 20, cy + 20, 20, 20, "#87CEEB", "#87CEEB"));
        actions.Add(Shape(userId, ShapeType.Rectangle, cx - 40, cy + 20, 20, 20, "#87CEEB", "#87CEEB"));
        return actions;
    }

    #endregion

    #region Parsing Helpers

    private static string ParseColor(string prompt, string defaultColor)
    {
        if (ContainsAny(prompt, "đỏ", "red")) return "#FF0000";
        if (ContainsAny(prompt, "xanh dương", "blue", "xanh da trời")) return "#0000FF";
        if (ContainsAny(prompt, "xanh lá", "green", "xanh lục")) return "#00FF00";
        if (ContainsAny(prompt, "vàng", "yellow")) return "#FFD700";
        if (ContainsAny(prompt, "cam", "orange")) return "#FF8C00";
        if (ContainsAny(prompt, "tím", "purple")) return "#800080";
        if (ContainsAny(prompt, "hồng", "pink")) return "#FF69B4";
        if (ContainsAny(prompt, "trắng", "white")) return "#FFFFFF";
        if (ContainsAny(prompt, "đen", "black")) return "#000000";
        if (ContainsAny(prompt, "nâu", "brown")) return "#8B4513";

        // Try hex color in prompt
        var match = Regex.Match(prompt, @"#[0-9a-fA-F]{6}");
        if (match.Success) return match.Value;

        return defaultColor;
    }

    private static (double x, double y) ParsePosition(string prompt)
    {
        if (ContainsAny(prompt, "giữa", "center", "chính giữa")) return (CanvasW / 2, CanvasH / 2);
        if (ContainsAny(prompt, "góc trái trên", "top left")) return (120, 120);
        if (ContainsAny(prompt, "góc phải trên", "top right")) return (CanvasW - 120, 120);
        if (ContainsAny(prompt, "góc trái dưới", "bottom left")) return (120, CanvasH - 120);
        if (ContainsAny(prompt, "góc phải dưới", "bottom right")) return (CanvasW - 120, CanvasH - 120);
        if (ContainsAny(prompt, "trên", "top")) return (CanvasW / 2, 120);
        if (ContainsAny(prompt, "dưới", "bottom")) return (CanvasW / 2, CanvasH - 120);
        if (ContainsAny(prompt, "trái", "left")) return (120, CanvasH / 2);
        if (ContainsAny(prompt, "phải", "right")) return (CanvasW - 120, CanvasH / 2);

        var match = Regex.Match(prompt, @"(?:at|tại|vị trí)\s+(\d+)\s+(\d+)");
        if (match.Success) return (double.Parse(match.Groups[1].Value), double.Parse(match.Groups[2].Value));

        return (CanvasW / 2, CanvasH / 2);
    }

    private static double ParseSize(string prompt)
    {
        if (ContainsAny(prompt, "nhỏ", "small", "bé")) return 40;
        if (ContainsAny(prompt, "rất to", "very big", "huge")) return 160;
        if (ContainsAny(prompt, "to", "lớn", "big", "large")) return 100;

        var match = Regex.Match(prompt, @"(?:size|kích thước|r|radius)\s*=?\s*(\d+)");
        if (match.Success) return double.Parse(match.Groups[1].Value);

        return 80;
    }

    private static bool HasFill(string prompt) =>
        ContainsAny(prompt, "tô", "fill", "đặc", "tô màu", "solid");

    private static string ExtractText(string prompt)
    {
        var match = Regex.Match(prompt, "\"([^\"]+)\"");
        if (match.Success) return match.Groups[1].Value;
        match = Regex.Match(prompt, "'([^']+)'");
        if (match.Success) return match.Groups[1].Value;
        return "Hello!";
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    #endregion

    #region DrawAction Factory

    private static DrawAction Shape(string userId, ShapeType type, double x, double y, double w, double h,
        string color, string? fill, double strokeWidth = 2) => new()
    {
        UserId = userId, Tool = DrawTool.Shape, ShapeType = type,
        X = x, Y = y, Width = w, Height = h, Radius = Math.Min(w, h) / 2,
        Color = color, FillColor = fill, StrokeWidth = strokeWidth
    };

    private static DrawAction ShapeCenter(string userId, ShapeType type, double cx, double cy, double w, double h,
        string color, string? fill, double strokeWidth = 2) => new()
    {
        UserId = userId, Tool = DrawTool.Shape, ShapeType = type,
        X = cx, Y = cy, Width = w, Height = h, Radius = Math.Min(w, h) / 2,
        Color = color, FillColor = fill, StrokeWidth = strokeWidth
    };

    private static DrawAction Line(string userId, double x1, double y1, double x2, double y2,
        string color, double strokeWidth = 2) => new()
    {
        UserId = userId, Tool = DrawTool.Line, Color = color, StrokeWidth = strokeWidth,
        Points = new List<PointData> { new(x1, y1), new(x2, y2) }
    };

    private static DrawAction Text(string userId, double x, double y, string text, string color) => new()
    {
        UserId = userId, Tool = DrawTool.Text, X = x, Y = y, Text = text, Color = color, FontSize = 24
    };

    #endregion
}
