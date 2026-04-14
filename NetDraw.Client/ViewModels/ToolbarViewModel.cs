using NetDraw.Client.Infrastructure;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using ShapeType = NetDraw.Shared.Models.Actions.ShapeType;

namespace NetDraw.Client.ViewModels;

public record ToolChangedEvent;

public enum DrawTool { Pen, Line, Shape, Text, Eraser, Select, Arrow, Calligraphy, Highlighter, Spray, Image }

public class ToolbarViewModel : ViewModelBase
{
    private readonly EventAggregator _events;

    private DrawTool _activeTool = DrawTool.Pen;
    private string _currentColor = "#000000";
    private double _strokeWidth = 2;
    private double _opacity = 1.0;
    private DashStyle _dashStyle = DashStyle.Solid;
    private ShapeType _activeShapeType = ShapeType.Rect;
    private bool _fill;

    public DrawTool ActiveTool { get => _activeTool; set { if (SetProperty(ref _activeTool, value)) _events.Publish(new ToolChangedEvent()); } }
    public string CurrentColor { get => _currentColor; set { SetProperty(ref _currentColor, value); _events.Publish(new ToolChangedEvent()); } }
    public double StrokeWidth { get => _strokeWidth; set { SetProperty(ref _strokeWidth, value); } }
    public double Opacity { get => _opacity; set => SetProperty(ref _opacity, value); }
    public DashStyle DashStyle { get => _dashStyle; set => SetProperty(ref _dashStyle, value); }
    public ShapeType ActiveShapeType { get => _activeShapeType; set => SetProperty(ref _activeShapeType, value); }
    public bool Fill { get => _fill; set => SetProperty(ref _fill, value); }

    public RelayCommand SelectToolCommand { get; }

    public ToolbarViewModel(EventAggregator events)
    {
        _events = events;
        SelectToolCommand = new RelayCommand(param =>
        {
            if (param is string toolName && Enum.TryParse<DrawTool>(toolName, out var tool))
                ActiveTool = tool;
        });
    }

    public PenStyle GetPenStyle() => ActiveTool switch
    {
        DrawTool.Calligraphy => PenStyle.Calligraphy,
        DrawTool.Highlighter => PenStyle.Highlighter,
        DrawTool.Spray => PenStyle.Spray,
        _ => PenStyle.Normal
    };
}
