using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetDraw.Shared.Models;

namespace NetDraw.Client;

/// <summary>
/// Render DrawAction lên WPF Canvas
/// </summary>
public static class CanvasRenderer
{
    /// <summary>
    /// Render một DrawAction lên canvas
    /// </summary>
    public static UIElement? Render(DrawAction action)
    {
        return action.Tool switch
        {
            DrawTool.Pen => RenderPen(action),
            DrawTool.Line => RenderLine(action),
            DrawTool.Shape => RenderShape(action),
            DrawTool.Text => RenderText(action),
            DrawTool.Eraser => RenderEraser(action),
            _ => null
        };
    }

    private static UIElement? RenderPen(DrawAction action)
    {
        if (action.Points.Count < 2) return null;

        var polyline = new Polyline
        {
            Stroke = BrushFromHex(action.Color),
            StrokeThickness = action.StrokeWidth,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Tag = action.Id
        };

        foreach (var pt in action.Points)
        {
            polyline.Points.Add(new Point(pt.X, pt.Y));
        }

        return polyline;
    }

    private static UIElement? RenderLine(DrawAction action)
    {
        if (action.Points.Count < 2) return null;

        var line = new Line
        {
            X1 = action.Points[0].X,
            Y1 = action.Points[0].Y,
            X2 = action.Points[^1].X,
            Y2 = action.Points[^1].Y,
            Stroke = BrushFromHex(action.Color),
            StrokeThickness = action.StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Tag = action.Id
        };

        return line;
    }

    private static UIElement? RenderShape(DrawAction action)
    {
        var stroke = BrushFromHex(action.Color);
        Brush? fill = action.FillColor != null ? BrushFromHex(action.FillColor) : null;

        Shape? shape = action.ShapeType switch
        {
            ShapeType.Rectangle => new Rectangle
            {
                Width = Math.Abs(action.Width),
                Height = Math.Abs(action.Height),
                Stroke = stroke,
                StrokeThickness = action.StrokeWidth,
                Fill = fill,
                Tag = action.Id
            },
            ShapeType.Ellipse or ShapeType.Circle => new Ellipse
            {
                Width = Math.Abs(action.Width),
                Height = Math.Abs(action.Height),
                Stroke = stroke,
                StrokeThickness = action.StrokeWidth,
                Fill = fill,
                Tag = action.Id
            },
            ShapeType.Triangle => CreateTriangle(action, stroke, fill),
            ShapeType.Star => CreateStar(action, stroke, fill),
            _ => null
        };

        if (shape == null) return null;

        // Xác định vị trí
        double left = action.X;
        double top = action.Y;

        // Nếu width/height dương, shape bắt đầu từ (x, y)
        // Nếu âm, cần điều chỉnh vị trí
        if (action.ShapeType == ShapeType.Circle || action.ShapeType == ShapeType.Ellipse)
        {
            // Cho circle/ellipse, X,Y là tâm
            left = action.X - Math.Abs(action.Width) / 2;
            top = action.Y - Math.Abs(action.Height) / 2;
        }

        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);

        return shape;
    }

    private static Polygon CreateTriangle(DrawAction action, Brush stroke, Brush? fill)
    {
        double w = Math.Abs(action.Width);
        double h = Math.Abs(action.Height);

        var polygon = new Polygon
        {
            Points = new PointCollection
            {
                new Point(w / 2, 0),    // Đỉnh trên
                new Point(0, h),         // Góc trái dưới
                new Point(w, h)          // Góc phải dưới
            },
            Stroke = stroke,
            StrokeThickness = action.StrokeWidth,
            Fill = fill,
            Tag = action.Id
        };

        return polygon;
    }

    private static Polygon CreateStar(DrawAction action, Brush stroke, Brush? fill)
    {
        double r = Math.Abs(action.Width) / 2;
        double ri = r * 0.4;
        var points = new PointCollection();

        for (int i = 0; i < 10; i++)
        {
            double angle = Math.PI / 2 + i * Math.PI / 5;
            double radius = (i % 2 == 0) ? r : ri;
            points.Add(new Point(
                r + radius * Math.Cos(angle),
                r - radius * Math.Sin(angle)
            ));
        }

        return new Polygon
        {
            Points = points,
            Stroke = stroke,
            StrokeThickness = action.StrokeWidth,
            Fill = fill,
            Tag = action.Id
        };
    }

    private static UIElement? RenderText(DrawAction action)
    {
        var textBlock = new TextBlock
        {
            Text = action.Text ?? "Text",
            Foreground = BrushFromHex(action.Color),
            FontSize = action.FontSize,
            Tag = action.Id
        };

        Canvas.SetLeft(textBlock, action.X);
        Canvas.SetTop(textBlock, action.Y);

        return textBlock;
    }

    private static UIElement? RenderEraser(DrawAction action)
    {
        if (action.Points.Count < 2) return null;

        var polyline = new Polyline
        {
            Stroke = Brushes.White,
            StrokeThickness = action.EraserSize,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Tag = action.Id
        };

        foreach (var pt in action.Points)
        {
            polyline.Points.Add(new Point(pt.X, pt.Y));
        }

        return polyline;
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return Brushes.Black;
        }
    }
}
