using System.Text.RegularExpressions;
using NetDraw.Shared.Models;

namespace NetDraw.Server;

/// <summary>
/// Fallback AI Parser - khi MCP Server không kết nối
/// Parse lệnh tiếng Việt/Anh đơn giản thành DrawAction
/// Ví dụ: "vẽ hình tròn màu đỏ ở giữa", "draw blue rectangle at 100 200"
/// </summary>
public static class FallbackAiParser
{
    private static readonly Random Rng = new();

    public static List<DrawAction> Parse(string prompt, string userId)
    {
        var actions = new List<DrawAction>();
        prompt = prompt.ToLower().Trim();

        // Phân tích màu
        string color = ParseColor(prompt);

        // Phân tích vị trí
        var (x, y) = ParsePosition(prompt);

        // Phân tích kích thước
        double size = ParseSize(prompt);

        // Phân tích hình dạng
        if (ContainsAny(prompt, "tròn", "circle", "hình tròn"))
        {
            actions.Add(CreateShapeAction(userId, ShapeType.Circle, x, y, size, size, color));
        }
        else if (ContainsAny(prompt, "vuông", "square", "hình vuông"))
        {
            actions.Add(CreateShapeAction(userId, ShapeType.Rectangle, x, y, size, size, color));
        }
        else if (ContainsAny(prompt, "chữ nhật", "rectangle", "rect", "hình chữ nhật"))
        {
            actions.Add(CreateShapeAction(userId, ShapeType.Rectangle, x, y, size * 1.5, size, color));
        }
        else if (ContainsAny(prompt, "tam giác", "triangle", "hình tam giác"))
        {
            actions.Add(CreateShapeAction(userId, ShapeType.Triangle, x, y, size, size, color));
        }
        else if (ContainsAny(prompt, "ngôi sao", "star", "sao"))
        {
            actions.Add(CreateShapeAction(userId, ShapeType.Star, x, y, size, size, color));
        }
        else if (ContainsAny(prompt, "ellipse", "elip", "hình elip"))
        {
            actions.Add(CreateShapeAction(userId, ShapeType.Ellipse, x, y, size * 1.3, size, color));
        }
        else if (ContainsAny(prompt, "đường thẳng", "line", "đường"))
        {
            actions.Add(CreateLineAction(userId, x, y, x + size, y, color));
        }
        else if (ContainsAny(prompt, "text", "chữ", "viết"))
        {
            string text = ExtractText(prompt);
            actions.Add(CreateTextAction(userId, x, y, text, color));
        }
        // Các mẫu phức tạp hơn
        else if (ContainsAny(prompt, "mặt trời", "sun"))
        {
            // Vẽ mặt trời = circle vàng + các line xung quanh
            actions.Add(CreateShapeAction(userId, ShapeType.Circle, x, y, 50, 50, "#FFD700", "#FFD700"));
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                double x1 = x + 55 * Math.Cos(angle);
                double y1 = y + 55 * Math.Sin(angle);
                double x2 = x + 80 * Math.Cos(angle);
                double y2 = y + 80 * Math.Sin(angle);
                actions.Add(CreateLineAction(userId, x1, y1, x2, y2, "#FFD700", 3));
            }
        }
        else if (ContainsAny(prompt, "nhà", "house", "ngôi nhà"))
        {
            // Vẽ nhà = rect + triangle mái
            actions.Add(CreateShapeAction(userId, ShapeType.Rectangle, x - 40, y, 80, 60, "#8B4513", "#DEB887"));
            actions.Add(CreateShapeAction(userId, ShapeType.Triangle, x - 50, y, 100, 50, "#8B0000", "#B22222"));
            // Cửa
            actions.Add(CreateShapeAction(userId, ShapeType.Rectangle, x - 10, y + 30, 20, 30, "#8B4513", "#654321"));
        }
        else if (ContainsAny(prompt, "cây", "tree"))
        {
            // Vẽ cây = rect thân + circle lá
            actions.Add(CreateShapeAction(userId, ShapeType.Rectangle, x - 8, y + 20, 16, 50, "#8B4513", "#8B4513"));
            actions.Add(CreateShapeAction(userId, ShapeType.Circle, x, y, 35, 35, "#228B22", "#32CD32"));
        }
        else if (ContainsAny(prompt, "trái tim", "heart", "tim"))
        {
            actions.Add(CreateShapeAction(userId, ShapeType.Circle, x - 15, y - 10, 25, 25, "#FF1493", "#FF1493"));
            actions.Add(CreateShapeAction(userId, ShapeType.Circle, x + 15, y - 10, 25, 25, "#FF1493", "#FF1493"));
            actions.Add(CreateShapeAction(userId, ShapeType.Triangle, x, y + 20, 55, 35, "#FF1493", "#FF1493"));
        }

        return actions;
    }

    private static string ParseColor(string prompt)
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
        if (ContainsAny(prompt, "xám", "gray", "grey")) return "#808080";
        return "#000000";
    }

    private static (double x, double y) ParsePosition(string prompt)
    {
        // Vị trí bằng từ khóa
        double canvasW = 800, canvasH = 600;

        if (ContainsAny(prompt, "giữa", "center", "ở giữa", "chính giữa"))
            return (canvasW / 2, canvasH / 2);
        if (ContainsAny(prompt, "góc trái trên", "top left", "trái trên"))
            return (100, 100);
        if (ContainsAny(prompt, "góc phải trên", "top right", "phải trên"))
            return (canvasW - 100, 100);
        if (ContainsAny(prompt, "góc trái dưới", "bottom left", "trái dưới"))
            return (100, canvasH - 100);
        if (ContainsAny(prompt, "góc phải dưới", "bottom right", "phải dưới"))
            return (canvasW - 100, canvasH - 100);
        if (ContainsAny(prompt, "trên", "top", "phía trên"))
            return (canvasW / 2, 100);
        if (ContainsAny(prompt, "dưới", "bottom", "phía dưới"))
            return (canvasW / 2, canvasH - 100);
        if (ContainsAny(prompt, "trái", "left", "bên trái"))
            return (100, canvasH / 2);
        if (ContainsAny(prompt, "phải", "right", "bên phải"))
            return (canvasW - 100, canvasH / 2);

        // Tìm tọa độ số trong prompt: "at 100 200" hoặc "tại 100 200"
        var match = Regex.Match(prompt, @"(?:at|tại|vị trí)\s+(\d+)\s+(\d+)");
        if (match.Success)
            return (double.Parse(match.Groups[1].Value), double.Parse(match.Groups[2].Value));

        // Mặc định: giữa canvas
        return (canvasW / 2, canvasH / 2);
    }

    private static double ParseSize(string prompt)
    {
        if (ContainsAny(prompt, "nhỏ", "small", "bé")) return 30;
        if (ContainsAny(prompt, "to", "lớn", "big", "large")) return 100;
        if (ContainsAny(prompt, "rất to", "very big", "huge")) return 150;

        var match = Regex.Match(prompt, @"(?:size|kích thước|bán kính|radius)\s+(\d+)");
        if (match.Success)
            return double.Parse(match.Groups[1].Value);

        return 60; // Mặc định
    }

    private static string ExtractText(string prompt)
    {
        // Tìm text trong dấu ngoặc kép
        var match = Regex.Match(prompt, "\"([^\"]+)\"");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(prompt, "'([^']+)'");
        if (match.Success) return match.Groups[1].Value;

        return "Hello!";
    }

    private static DrawAction CreateShapeAction(string userId, ShapeType shape, double x, double y,
        double w, double h, string color, string? fill = null)
    {
        return new DrawAction
        {
            UserId = userId,
            Tool = DrawTool.Shape,
            ShapeType = shape,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            Radius = Math.Min(w, h) / 2,
            Color = color,
            FillColor = fill,
            StrokeWidth = 2
        };
    }

    private static DrawAction CreateLineAction(string userId, double x1, double y1, double x2, double y2,
        string color, double width = 2)
    {
        return new DrawAction
        {
            UserId = userId,
            Tool = DrawTool.Line,
            Points = new List<PointData> { new(x1, y1), new(x2, y2) },
            Color = color,
            StrokeWidth = width
        };
    }

    private static DrawAction CreateTextAction(string userId, double x, double y, string text, string color)
    {
        return new DrawAction
        {
            UserId = userId,
            Tool = DrawTool.Text,
            X = x,
            Y = y,
            Text = text,
            Color = color,
            FontSize = 20
        };
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
