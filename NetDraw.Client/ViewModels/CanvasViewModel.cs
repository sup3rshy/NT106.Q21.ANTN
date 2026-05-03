using System.Windows;
using System.Windows.Input;
using NetDraw.Client.Drawing;
using NetDraw.Client.Infrastructure;
using NetDraw.Client.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Client.ViewModels;

public record RenderActionEvent(DrawActionBase Action);
public record ClearCanvasEvent;
public record RemoveActionEvent(string ActionId);
public record MoveActionEvent(string ActionId, double DeltaX, double DeltaY);
public record SnapshotEvent(List<DrawActionBase> Actions);

public class CanvasViewModel : ViewModelBase
{
    private readonly INetworkService _network;
    private readonly ToolbarViewModel _toolbar;
    private readonly EventAggregator _events;
    public readonly HistoryManager History;

    private double _zoomLevel = 1.0;
    private double _panX, _panY;
    private DrawActionBase? _currentAction;
    private Point _startPoint;
    private bool _isDrawing;

    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string UserColor { get; set; } = "#000000";
    public string RoomId { get; set; } = "";

    public double ZoomLevel { get => _zoomLevel; set => SetProperty(ref _zoomLevel, Math.Clamp(value, 0.2, 5.0)); }
    public double PanX { get => _panX; set => SetProperty(ref _panX, value); }
    public double PanY { get => _panY; set => SetProperty(ref _panY, value); }

    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ClearCommand { get; }

    public CanvasViewModel(INetworkService network, HistoryManager history, ToolbarViewModel toolbar, EventAggregator events)
    {
        _network = network;
        History = history;
        _toolbar = toolbar;
        _events = events;

        UndoCommand = new RelayCommand(async () => await UndoAsync());
        RedoCommand = new RelayCommand(async () => await RedoAsync());
        ClearCommand = new RelayCommand(async () => await ClearAsync());
    }

    public void StartDraw(Point pos)
    {
        if (!_network.IsConnected) return;
        _startPoint = pos;
        _isDrawing = true;

        var tool = _toolbar.ActiveTool;
        _currentAction = tool switch
        {
            DrawTool.Pen or DrawTool.Calligraphy or DrawTool.Highlighter or DrawTool.Spray =>
                new PenAction
                {
                    UserId = UserId, UserName = UserName, Color = _toolbar.CurrentColor, StrokeWidth = _toolbar.StrokeWidth,
                    Opacity = _toolbar.Opacity, DashStyle = _toolbar.DashStyle,
                    PenStyle = _toolbar.GetPenStyle(),
                    Points = { new PointData(pos.X, pos.Y) }
                },
            DrawTool.Eraser =>
                new EraseAction
                {
                    UserId = UserId, UserName = UserName, EraserSize = _toolbar.StrokeWidth * 5,
                    Points = { new PointData(pos.X, pos.Y) }
                },
            DrawTool.Line or DrawTool.Arrow =>
                new LineAction
                {
                    UserId = UserId, UserName = UserName, Color = _toolbar.CurrentColor, StrokeWidth = _toolbar.StrokeWidth,
                    Opacity = _toolbar.Opacity, DashStyle = _toolbar.DashStyle,
                    HasArrow = tool == DrawTool.Arrow,
                    StartX = pos.X, StartY = pos.Y, EndX = pos.X, EndY = pos.Y
                },
            DrawTool.Shape =>
                new ShapeAction
                {
                    UserId = UserId, UserName = UserName, Color = _toolbar.CurrentColor, StrokeWidth = _toolbar.StrokeWidth,
                    Opacity = _toolbar.Opacity, DashStyle = _toolbar.DashStyle,
                    ShapeType = _toolbar.ActiveShapeType,
                    FillColor = _toolbar.Fill ? _toolbar.CurrentColor : null,
                    X = pos.X, Y = pos.Y
                },
            _ => null
        };
    }

    public DrawActionBase? UpdateDraw(Point pos)
    {
        if (!_isDrawing || _currentAction == null) return null;

        switch (_currentAction)
        {
            case PenAction pen:
                pen.Points.Add(new PointData(pos.X, pos.Y));
                break;
            case EraseAction erase:
                erase.Points.Add(new PointData(pos.X, pos.Y));
                break;
            case LineAction line:
                line.EndX = pos.X; line.EndY = pos.Y;
                break;
            case ShapeAction shape:
                shape.X = Math.Min(_startPoint.X, pos.X);
                shape.Y = Math.Min(_startPoint.Y, pos.Y);
                shape.Width = Math.Abs(pos.X - _startPoint.X);
                shape.Height = Math.Abs(pos.Y - _startPoint.Y);
                if (shape.ShapeType == ShapeType.Circle)
                {
                    var size = Math.Max(shape.Width, shape.Height);
                    shape.Width = size; shape.Height = size;
                }
                break;
        }
        return _currentAction;
    }

    public DrawActionBase? FinishDraw(Point pos)
    {
        if (!_isDrawing || _currentAction == null) return null;
        _isDrawing = false;

        UpdateDraw(pos);

        // Validate minimum content
        if (_currentAction is PenAction p && p.Points.Count < 2) { _currentAction = null; return null; }
        if (_currentAction is EraseAction e && e.Points.Count < 2) { _currentAction = null; return null; }

        var action = _currentAction;
        _currentAction = null;

        History.Add(action, isLocal: true);
        var msg = NetMessage<DrawPayload>.Create(MessageType.Draw, UserId, UserName, RoomId,
            new DrawPayload { Action = action });
        _ = _network.SendAsync(msg);

        return action;
    }

    public TextAction? CreateTextAction(Point pos, string text,
        string? fontFamily = null, double fontSize = 0,
        bool bold = false, bool italic = false, bool underline = false, bool strikethrough = false)
    {
        if (!_network.IsConnected || string.IsNullOrEmpty(text)) return null;
        var action = new TextAction
        {
            UserId = UserId, UserName = UserName, Color = _toolbar.CurrentColor, Opacity = _toolbar.Opacity,
            X = pos.X, Y = pos.Y, Text = text,
            FontSize = fontSize > 0 ? fontSize : _toolbar.StrokeWidth * 5 + 10,
            FontFamily = fontFamily,
            IsBold = bold, IsItalic = italic,
            IsUnderline = underline, IsStrikethrough = strikethrough
        };
        History.Add(action, isLocal: true);
        var msg = NetMessage<DrawPayload>.Create(MessageType.Draw, UserId, UserName, RoomId,
            new DrawPayload { Action = action });
        _ = _network.SendAsync(msg);
        return action;
    }

    public void HandleRemoteAction(DrawActionBase action)
    {
        History.Add(action);
    }

    public void HandleSnapshot(List<DrawActionBase> actions)
    {
        History.ReplaceAll(actions);
        _events.Publish(new SnapshotEvent(actions));
    }

    private async Task UndoAsync()
    {
        var action = History.Undo(UserId);
        if (action == null) return;
        _events.Publish(new RemoveActionEvent(action.Id));
        if (_network.IsConnected)
        {
            var msg = NetMessage<DeleteObjectPayload>.Create(MessageType.Undo, UserId, UserName, RoomId,
                new DeleteObjectPayload { ActionId = action.Id });
            await _network.SendAsync(msg);
        }
    }

    private async Task RedoAsync()
    {
        var action = History.Redo();
        if (action == null) return;
        _events.Publish(new RenderActionEvent(action));
        if (_network.IsConnected)
        {
            var msg = NetMessage<DrawPayload>.Create(MessageType.Redo, UserId, UserName, RoomId,
                new DrawPayload { Action = action });
            await _network.SendAsync(msg);
        }
    }

    private async Task ClearAsync()
    {
        History.Clear();
        _events.Publish(new ClearCanvasEvent());
        if (_network.IsConnected)
        {
            var msg = NetMessage<SnapshotPayload>.Create(MessageType.ClearCanvas, UserId, UserName, RoomId);
            await _network.SendAsync(msg);
        }
    }

    public void ResetZoom() { ZoomLevel = 1.0; PanX = 0; PanY = 0; }
}
