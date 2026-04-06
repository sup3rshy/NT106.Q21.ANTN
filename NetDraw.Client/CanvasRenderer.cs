using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetDraw.Shared.Models;
using DashStyleModel = NetDraw.Shared.Models.DashStyle;

namespace NetDraw.Client;

/// <summary>
/// Render DrawAction lên WPF Canvas
/// Hỗ trợ: Pen, Line, Arrow, Shape, Text, Eraser + Opacity + Dash Style
/// </summary>
public static class CanvasRenderer
{
    public static UIElement? Render(DrawAction action)
    {
        UIElement? element = action.Tool switch
        {
            DrawTool.Pen => RenderPen(action),
            DrawTool.Calligraphy => RenderCalligraphy(action),
            DrawTool.Highlighter => RenderHighlighter(action),
            DrawTool.Spray => RenderSpray(action),
            DrawTool.Line => RenderLine(action),
            DrawTool.Arrow => RenderArrow(action),
            DrawTool.Shape => RenderShape(action),
            DrawTool.Text => RenderText(action),
            DrawTool.Eraser => RenderEraser(action),
            _ => null
        };

        // Áp dụng opacity
        if (element != null && action.Opacity < 1.0)
            element.Opacity = action.Opacity;

        return element;
    }

    private static DoubleCollection? GetDashArray(DashStyleModel style)
    {
        return style switch
        {
            DashStyleModel.Dashed => new DoubleCollection { 6, 3 },
            DashStyleModel.Dotted => new DoubleCollection { 1, 3 },
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
            StrokeDashArray = GetDashArray(action.DashStyle),
            Tag = action.Id
        };
        foreach (var pt in action.Points)
            polyline.Points.Add(new Point(pt.X, pt.Y));
        return polyline;
    }

    /// <summary>
    /// Bút thư pháp - nét dày hơn theo chiều dọc, mỏng theo chiều ngang
    /// </summary>
    private static UIElement? RenderCalligraphy(DrawAction action)
    {
        if (action.Points.Count < 2) return null;

        var canvas = new Canvas { Tag = action.Id };
        var brush = BrushFromHex(action.Color);

        for (int i = 0; i < action.Points.Count - 1; i++)
        {
            double x1 = action.Points[i].X, y1 = action.Points[i].Y;
            double x2 = action.Points[i + 1].X, y2 = action.Points[i + 1].Y;

            // Calligraphy: nét nghiêng 45 độ, dày hơn theo chiều dọc
            double dx = x2 - x1, dy = y2 - y1;
            double angle = Math.Atan2(dy, dx);
            double thickness = action.StrokeWidth * (1.0 + Math.Abs(Math.Sin(angle)) * 2);

            canvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat
            });
        }
        return canvas;
    }

    /// <summary>
    /// Highlighter - bán trong suốt, nét rộng
    /// </summary>
    private static UIElement? RenderHighlighter(DrawAction action)
    {
        if (action.Points.Count < 2) return null;

        var color = (Color)ColorConverter.ConvertFromString(action.Color);
        var semiTransparent = Color.FromArgb(100, color.R, color.G, color.B);

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(semiTransparent),
            StrokeThickness = action.StrokeWidth * 4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            Tag = action.Id
        };
        foreach (var pt in action.Points)
            polyline.Points.Add(new Point(pt.X, pt.Y));
        return polyline;
    }

    /// <summary>
    /// Spray/Airbrush - điểm ngẫu nhiên xung quanh nét vẽ
    /// </summary>
    private static UIElement? RenderSpray(DrawAction action)
    {
        if (action.Points.Count < 1) return null;

        var canvas = new Canvas { Tag = action.Id };
        var brush = BrushFromHex(action.Color);
        var rng = new Random(action.Id.GetHashCode()); // Deterministic random từ Id

        foreach (var pt in action.Points)
        {
            double radius = action.StrokeWidth * 3;
            int dotCount = (int)(action.StrokeWidth * 2);

            for (int i = 0; i < dotCount; i++)
            {
                double angle = rng.NextDouble() * Math.PI * 2;
                double dist = rng.NextDouble() * radius;
                double dx = pt.X + dist * Math.Cos(angle);
                double dy = pt.Y + dist * Math.Sin(angle);
                double dotSize = 1 + rng.NextDouble() * 2;

                var dot = new Ellipse
                {
                    Width = dotSize, Height = dotSize,
                    Fill = brush
                };
                Canvas.SetLeft(dot, dx);
                Canvas.SetTop(dot, dy);
                canvas.Children.Add(dot);
            }
        }
        return canvas;
    }

    private static UIElement? RenderLine(DrawAction action)
    {
        if (action.Points.Count < 2) return null;

        return new Line
        {
            X1 = action.Points[0].X, Y1 = action.Points[0].Y,
            X2 = action.Points[^1].X, Y2 = action.Points[^1].Y,
            Stroke = BrushFromHex(action.Color),
            StrokeThickness = action.StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = GetDashArray(action.DashStyle),
            Tag = action.Id
        };
    }

    private static UIElement? RenderArrow(DrawAction action)
    {
        if (action.Points.Count < 2) return null;

        double x1 = action.Points[0].X, y1 = action.Points[0].Y;
        double x2 = action.Points[^1].X, y2 = action.Points[^1].Y;

        var stroke = BrushFromHex(action.Color);
        var dash = GetDashArray(action.DashStyle);

        // Tạo group chứa line + arrowhead
        var canvas = new Canvas { Tag = action.Id };

        // Line
        canvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = stroke, StrokeThickness = action.StrokeWidth,
            StrokeDashArray = dash
        });

        // Arrowhead
        double angle = Math.Atan2(y2 - y1, x2 - x1);
        double arrowLen = 12 + action.StrokeWidth * 2;
        double arrowAngle = Math.PI / 7;

        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(x2, y2),
                new Point(x2 - arrowLen * Math.Cos(angle - arrowAngle), y2 - arrowLen * Math.Sin(angle - arrowAngle)),
                new Point(x2 - arrowLen * Math.Cos(angle + arrowAngle), y2 - arrowLen * Math.Sin(angle + arrowAngle))
            },
            Fill = stroke,
            Stroke = stroke,
            StrokeThickness = 1
        };
        canvas.Children.Add(arrow);

        return canvas;
    }

    private static UIElement? RenderShape(DrawAction action)
    {
        var stroke = BrushFromHex(action.Color);
        Brush? fill = action.FillColor != null ? BrushFromHex(action.FillColor) : null;
        var dash = GetDashArray(action.DashStyle);

        Shape? shape = action.ShapeType switch
        {
            ShapeType.Rectangle => new Rectangle
            {
                Width = Math.Abs(action.Width), Height = Math.Abs(action.Height),
                Stroke = stroke, StrokeThickness = action.StrokeWidth, Fill = fill,
                StrokeDashArray = dash, Tag = action.Id
            },
            ShapeType.Ellipse or ShapeType.Circle => new Ellipse
            {
                Width = Math.Abs(action.Width), Height = Math.Abs(action.Height),
                Stroke = stroke, StrokeThickness = action.StrokeWidth, Fill = fill,
                StrokeDashArray = dash, Tag = action.Id
            },
            ShapeType.Triangle => CreateTriangle(action, stroke, fill, dash),
            ShapeType.Star => CreateStar(action, stroke, fill, dash),
            _ => null
        };

        if (shape == null) return null;

        double left = action.X, top = action.Y;
        if (action.ShapeType == ShapeType.Circle || action.ShapeType == ShapeType.Ellipse)
        {
            left = action.X - Math.Abs(action.Width) / 2;
            top = action.Y - Math.Abs(action.Height) / 2;
        }

        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        return shape;
    }

    private static Polygon CreateTriangle(DrawAction action, Brush stroke, Brush? fill, DoubleCollection? dash)
    {
        double w = Math.Abs(action.Width), h = Math.Abs(action.Height);
        return new Polygon
        {
            Points = new PointCollection { new(w / 2, 0), new(0, h), new(w, h) },
            Stroke = stroke, StrokeThickness = action.StrokeWidth, Fill = fill,
            StrokeDashArray = dash, Tag = action.Id
        };
    }

    private static Polygon CreateStar(DrawAction action, Brush stroke, Brush? fill, DoubleCollection? dash)
    {
        double r = Math.Abs(action.Width) / 2, ri = r * 0.4;
        var points = new PointCollection();
        for (int i = 0; i < 10; i++)
        {
            double angle = Math.PI / 2 + i * Math.PI / 5;
            double radius = (i % 2 == 0) ? r : ri;
            points.Add(new Point(r + radius * Math.Cos(angle), r - radius * Math.Sin(angle)));
        }
        return new Polygon
        {
            Points = points, Stroke = stroke, StrokeThickness = action.StrokeWidth,
            Fill = fill, StrokeDashArray = dash, Tag = action.Id
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
            polyline.Points.Add(new Point(pt.X, pt.Y));
        return polyline;
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return Brushes.Black;
        }
    }
}
