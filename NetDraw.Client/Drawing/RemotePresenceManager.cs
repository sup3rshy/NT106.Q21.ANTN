using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NetDraw.Shared.Models;

namespace NetDraw.Client.Drawing;

public class RemotePresenceManager
{
    private readonly Dictionary<string, (Path Cursor, Border Label)> _cursors = new();
    private readonly Dictionary<string, UIElement> _previews = new();

    // Smooth cursor motion: animate position transitions between updates
    private static readonly Duration CursorTween = new(TimeSpan.FromMilliseconds(90));
    private static readonly IEasingFunction CursorEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    public void UpdateCursor(string userId, string userName, double x, double y, string color, Canvas canvas)
    {
        if (!_cursors.TryGetValue(userId, out var cursor))
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();

            var cursorPath = new Path
            {
                // Clean Microsoft Whiteboard-style arrow pointer with integrated white outline for
                // contrast against any background — avoids needing a DropShadowEffect (which forces
                // software rendering and causes mouse-move lag).
                Data = Geometry.Parse("M0,0 L0,16 L4,12 L7,18 L9,17 L6,11 L11,11 Z"),
                Fill = brush,
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            var label = new Border
            {
                Child = new TextBlock
                {
                    Text = userName,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI Variable, Segoe UI")
                },
                Background = brush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2, 7, 2),
                IsHitTestVisible = false
                // No DropShadowEffect — animating an element with an effect re-rasterizes on every
                // frame and tanks mouse responsiveness with multiple remote users in a room.
            };
            canvas.Children.Add(cursorPath);
            canvas.Children.Add(label);
            // Initial placement (no animation on first appearance)
            Canvas.SetLeft(cursorPath, x);
            Canvas.SetTop(cursorPath, y);
            Canvas.SetLeft(label, x + 14);
            Canvas.SetTop(label, y + 14);
            _cursors[userId] = (cursorPath, label);
            return;
        }

        // Always update the label text (name may arrive late or change)
        if (cursor.Label.Child is TextBlock tb && tb.Text != userName)
            tb.Text = userName;

        AnimateTo(cursor.Cursor, x, y);
        AnimateTo(cursor.Label, x + 14, y + 14);
    }

    private static void AnimateTo(UIElement element, double x, double y)
    {
        element.BeginAnimation(Canvas.LeftProperty,
            new DoubleAnimation { To = x, Duration = CursorTween, EasingFunction = CursorEase, FillBehavior = FillBehavior.HoldEnd });
        element.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation { To = y, Duration = CursorTween, EasingFunction = CursorEase, FillBehavior = FillBehavior.HoldEnd });
    }

    public void RemoveCursor(string userId, Canvas canvas)
    {
        if (_cursors.Remove(userId, out var cursor))
        {
            canvas.Children.Remove(cursor.Cursor);
            canvas.Children.Remove(cursor.Label);
        }
    }

    public void UpdatePreview(string userId, UIElement preview, Canvas canvas)
    {
        ClearPreview(userId, canvas);
        _previews[userId] = preview;
        canvas.Children.Add(preview);
    }

    public void ClearPreview(string userId, Canvas canvas)
    {
        if (_previews.Remove(userId, out var old))
            canvas.Children.Remove(old);
    }

    public void ClearAll(Canvas canvas)
    {
        foreach (var (cursor, label) in _cursors.Values)
        {
            canvas.Children.Remove(cursor);
            canvas.Children.Remove(label);
        }
        _cursors.Clear();

        foreach (var preview in _previews.Values)
            canvas.Children.Remove(preview);
        _previews.Clear();
    }
}
