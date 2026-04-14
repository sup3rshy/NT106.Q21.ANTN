using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using DashStyleModel = NetDraw.Shared.Models.DashStyle;

namespace NetDraw.Client.Drawing;

public class WpfCanvasRenderer : ICanvasRenderer
{
    public UIElement? Render(DrawActionBase action)
    {
        UIElement? element = action switch
        {
            PenAction pen => pen.PenStyle switch
            {
                PenStyle.Calligraphy => RenderCalligraphy(pen),
                PenStyle.Highlighter => RenderHighlighter(pen),
                PenStyle.Spray => RenderSpray(pen),
                _ => RenderPen(pen)
            },
            LineAction line => line.HasArrow ? RenderArrow(line) : RenderLine(line),
            ShapeAction shape => RenderShape(shape),
            TextAction text => RenderText(text),
            EraseAction erase => RenderEraser(erase),
            ImageAction img => RenderImage(img),
            _ => null
        };

        if (element != null && action.Opacity < 1.0)
            element.Opacity = action.Opacity;
        return element;
    }

    public void Clear(Canvas canvas) => canvas.Children.Clear();

    public void RemoveAction(Canvas canvas, string actionId)
    {
        for (int i = canvas.Children.Count - 1; i >= 0; i--)
        {
            if (canvas.Children[i] is FrameworkElement fe && fe.Tag as string == actionId)
            {
                canvas.Children.RemoveAt(i);
                return;
            }
        }
    }

    private static DoubleCollection? GetDashArray(DashStyleModel style) => style switch
    {
        DashStyleModel.Dashed => new DoubleCollection { 6, 3 },
        DashStyleModel.Dotted => new DoubleCollection { 1, 3 },
        _ => null
    };

    private static UIElement? RenderPen(PenAction action)
    {
        if (action.Points.Count < 2) return null;

        // For very short strokes, fall back to a simple line segment
        if (action.Points.Count == 2)
        {
            return new Line
            {
                X1 = action.Points[0].X, Y1 = action.Points[0].Y,
                X2 = action.Points[1].X, Y2 = action.Points[1].Y,
                Stroke = BrushFromHex(action.Color),
                StrokeThickness = action.StrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeDashArray = GetDashArray(action.DashStyle),
                Tag = action.Id
            };
        }

        // Smooth pen stroke using Catmull-Rom -> cubic Bezier conversion
        var pts = action.Points;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), isFilled: false, isClosed: false);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = i == 0 ? pts[0] : pts[i - 1];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = i + 2 < pts.Count ? pts[i + 2] : pts[i + 1];

                var cp1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                var cp2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                ctx.BezierTo(cp1, cp2, new Point(p2.X, p2.Y), isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();

        return new Path
        {
            Data = geometry,
            Stroke = BrushFromHex(action.Color),
            StrokeThickness = action.StrokeWidth,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = GetDashArray(action.DashStyle),
            Tag = action.Id
        };
    }

    private static UIElement? RenderCalligraphy(PenAction action)
    {
        if (action.Points.Count < 2) return null;
        var canvas = new Canvas { Tag = action.Id };
        var brush = BrushFromHex(action.Color);
        for (int i = 0; i < action.Points.Count - 1; i++)
        {
            double x1 = action.Points[i].X, y1 = action.Points[i].Y;
            double x2 = action.Points[i + 1].X, y2 = action.Points[i + 1].Y;
            double angle = Math.Atan2(y2 - y1, x2 - x1);
            double thickness = action.StrokeWidth * (1.0 + Math.Abs(Math.Sin(angle)) * 2);
            canvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat
            });
        }
        return canvas;
    }

    private static UIElement? RenderHighlighter(PenAction action)
    {
        if (action.Points.Count < 2) return null;
        var color = (Color)ColorConverter.ConvertFromString(action.Color);
        var semi = Color.FromArgb(100, color.R, color.G, color.B);
        var brush = new SolidColorBrush(semi);
        double thick = action.StrokeWidth * 4;

        if (action.Points.Count == 2)
        {
            return new Line
            {
                X1 = action.Points[0].X, Y1 = action.Points[0].Y,
                X2 = action.Points[1].X, Y2 = action.Points[1].Y,
                Stroke = brush, StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                Tag = action.Id
            };
        }

        var pts = action.Points;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), isFilled: false, isClosed: false);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = i == 0 ? pts[0] : pts[i - 1];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = i + 2 < pts.Count ? pts[i + 2] : pts[i + 1];
                var cp1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                var cp2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                ctx.BezierTo(cp1, cp2, new Point(p2.X, p2.Y), isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();

        return new Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = thick,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            Tag = action.Id
        };
    }

    private static UIElement? RenderSpray(PenAction action)
    {
        if (action.Points.Count < 1) return null;
        var canvas = new Canvas { Tag = action.Id };
        var brush = BrushFromHex(action.Color);
        var rng = new Random(action.Id.GetHashCode());
        foreach (var pt in action.Points)
        {
            double radius = action.StrokeWidth * 3;
            int dotCount = (int)(action.StrokeWidth * 2);
            for (int i = 0; i < dotCount; i++)
            {
                double a = rng.NextDouble() * Math.PI * 2;
                double dist = rng.NextDouble() * radius;
                double dotSize = 1 + rng.NextDouble() * 2;
                var dot = new Ellipse { Width = dotSize, Height = dotSize, Fill = brush };
                Canvas.SetLeft(dot, pt.X + dist * Math.Cos(a));
                Canvas.SetTop(dot, pt.Y + dist * Math.Sin(a));
                canvas.Children.Add(dot);
            }
        }
        return canvas;
    }

    private static UIElement? RenderLine(LineAction action) => new Line
    {
        X1 = action.StartX, Y1 = action.StartY,
        X2 = action.EndX, Y2 = action.EndY,
        Stroke = BrushFromHex(action.Color),
        StrokeThickness = action.StrokeWidth,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        StrokeDashArray = GetDashArray(action.DashStyle),
        Tag = action.Id
    };

    private static UIElement? RenderArrow(LineAction action)
    {
        var stroke = BrushFromHex(action.Color);
        var canvas = new Canvas { Tag = action.Id };
        canvas.Children.Add(new Line
        {
            X1 = action.StartX, Y1 = action.StartY, X2 = action.EndX, Y2 = action.EndY,
            Stroke = stroke, StrokeThickness = action.StrokeWidth,
            StrokeDashArray = GetDashArray(action.DashStyle)
        });
        double angle = Math.Atan2(action.EndY - action.StartY, action.EndX - action.StartX);
        double arrowLen = 12 + action.StrokeWidth * 2;
        double arrowAngle = Math.PI / 7;
        canvas.Children.Add(new Polygon
        {
            Points = new PointCollection
            {
                new(action.EndX, action.EndY),
                new(action.EndX - arrowLen * Math.Cos(angle - arrowAngle), action.EndY - arrowLen * Math.Sin(angle - arrowAngle)),
                new(action.EndX - arrowLen * Math.Cos(angle + arrowAngle), action.EndY - arrowLen * Math.Sin(angle + arrowAngle))
            },
            Fill = stroke, Stroke = stroke, StrokeThickness = 1
        });
        return canvas;
    }

    private static UIElement? RenderShape(ShapeAction action)
    {
        var stroke = BrushFromHex(action.Color);
        Brush? fill = action.FillColor != null ? BrushFromHex(action.FillColor) : null;
        var dash = GetDashArray(action.DashStyle);
        double w = Math.Abs(action.Width), h = Math.Abs(action.Height);

        Shape? shape = action.ShapeType switch
        {
            ShapeType.Rect => new Rectangle
            {
                Width = w, Height = h, Stroke = stroke, StrokeThickness = action.StrokeWidth,
                Fill = fill, StrokeDashArray = dash, Tag = action.Id
            },
            ShapeType.Ellipse or ShapeType.Circle => new Ellipse
            {
                Width = w, Height = h, Stroke = stroke, StrokeThickness = action.StrokeWidth,
                Fill = fill, StrokeDashArray = dash, Tag = action.Id
            },
            ShapeType.Triangle => CreateTriangle(action, stroke, fill, dash),
            ShapeType.Star => CreateStar(action, stroke, fill, dash),
            _ => null
        };
        if (shape == null) return null;

        double left = action.X, top = action.Y;
        if (action.ShapeType is ShapeType.Circle or ShapeType.Ellipse)
        {
            left = action.X - w / 2;
            top = action.Y - h / 2;
        }
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        return shape;
    }

    private static Polygon CreateTriangle(ShapeAction a, Brush stroke, Brush? fill, DoubleCollection? dash)
    {
        double w = Math.Abs(a.Width), h = Math.Abs(a.Height);
        return new Polygon
        {
            Points = new PointCollection { new(w / 2, 0), new(0, h), new(w, h) },
            Stroke = stroke, StrokeThickness = a.StrokeWidth, Fill = fill,
            StrokeDashArray = dash, Tag = a.Id
        };
    }

    private static Polygon CreateStar(ShapeAction a, Brush stroke, Brush? fill, DoubleCollection? dash)
    {
        double r = Math.Abs(a.Width) / 2, ri = r * 0.4;
        var points = new PointCollection();
        for (int i = 0; i < 10; i++)
        {
            double angle = Math.PI / 2 + i * Math.PI / 5;
            double radius = (i % 2 == 0) ? r : ri;
            points.Add(new Point(r + radius * Math.Cos(angle), r - radius * Math.Sin(angle)));
        }
        return new Polygon
        {
            Points = points, Stroke = stroke, StrokeThickness = a.StrokeWidth,
            Fill = fill, StrokeDashArray = dash, Tag = a.Id
        };
    }

    private static UIElement? RenderText(TextAction action)
    {
        var tb = new TextBlock
        {
            Text = action.Text ?? "Text",
            Foreground = BrushFromHex(action.Color),
            FontSize = action.FontSize,
            FontWeight = action.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = action.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            Tag = action.Id
        };
        if (!string.IsNullOrEmpty(action.FontFamily))
            tb.FontFamily = new FontFamily(action.FontFamily);

        var decorations = new TextDecorationCollection();
        if (action.IsUnderline) decorations.Add(TextDecorations.Underline);
        if (action.IsStrikethrough) decorations.Add(TextDecorations.Strikethrough);
        if (decorations.Count > 0) tb.TextDecorations = decorations;

        Canvas.SetLeft(tb, action.X);
        Canvas.SetTop(tb, action.Y);
        return tb;
    }

    private static UIElement? RenderEraser(EraseAction action)
    {
        if (action.Points.Count < 2) return null;
        var polyline = new Polyline
        {
            Stroke = Brushes.White, StrokeThickness = action.EraserSize,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Tag = action.Id
        };
        foreach (var pt in action.Points) polyline.Points.Add(new Point(pt.X, pt.Y));
        return polyline;
    }

    private static UIElement? RenderImage(ImageAction action)
    {
        if (string.IsNullOrEmpty(action.ImageData)) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(action.ImageData);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            var image = new Image
            {
                Source = bmp,
                Width = action.Width > 0 ? action.Width : bmp.PixelWidth,
                Height = action.Height > 0 ? action.Height : bmp.PixelHeight,
                IsHitTestVisible = false, Tag = action.Id
            };
            Canvas.SetLeft(image, action.X);
            Canvas.SetTop(image, action.Y);
            return image;
        }
        catch { return null; }
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return Brushes.Black; }
    }
}
