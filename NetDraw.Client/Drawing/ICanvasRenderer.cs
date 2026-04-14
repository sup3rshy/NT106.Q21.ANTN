using System.Windows;
using System.Windows.Controls;
using NetDraw.Shared.Models;

namespace NetDraw.Client.Drawing;

public interface ICanvasRenderer
{
    UIElement? Render(DrawActionBase action);
    void Clear(Canvas canvas);
    void RemoveAction(Canvas canvas, string actionId);
}
