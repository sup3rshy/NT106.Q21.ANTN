using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetDraw.Shared.Models;

namespace NetDraw.Client.Drawing;

public class RemotePresenceManager
{
    private readonly Dictionary<string, (Path Cursor, Border Label)> _cursors = new();
    private readonly Dictionary<string, UIElement> _previews = new();

    public void UpdateCursor(string userId, string userName, double x, double y, string color, Canvas canvas)
    {
        if (!_cursors.TryGetValue(userId, out var cursor))
        {
            var cursorPath = new Path
            {
                Data = Geometry.Parse("M0,0 L0,16 L4,12 L8,18 L10,17 L6,11 L12,11 Z"),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                IsHitTestVisible = false
            };
            var label = new Border
            {
                Child = new TextBlock { Text = userName, FontSize = 10, Foreground = Brushes.White },
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 1, 3, 1),
                IsHitTestVisible = false
            };
            canvas.Children.Add(cursorPath);
            canvas.Children.Add(label);
            _cursors[userId] = (cursorPath, label);
            cursor = (cursorPath, label);
        }

        // Always update the label text (name may arrive late or change)
        if (cursor.Label.Child is TextBlock tb && tb.Text != userName)
            tb.Text = userName;

        Canvas.SetLeft(cursor.Cursor, x);
        Canvas.SetTop(cursor.Cursor, y);
        Canvas.SetLeft(cursor.Label, x + 14);
        Canvas.SetTop(cursor.Label, y + 14);
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
