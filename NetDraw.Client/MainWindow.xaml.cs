using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetDraw.Client.Drawing;
using NetDraw.Client.Infrastructure;
using NetDraw.Client.Services;
using NetDraw.Client.ViewModels;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;
using DashStyleModel = NetDraw.Shared.Models.DashStyle;

namespace NetDraw.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly WpfCanvasRenderer _renderer;
    private readonly HistoryManager _history;
    private readonly RemotePresenceManager _presence;
    private readonly EventAggregator _events;

    // Pan state (needs UIElement access)
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX, _panStartY;
    private bool _spaceHeld;

    // Select tool state (needs UIElement access)
    private UIElement? _selectedElement;
    private DrawActionBase? _selectedAction;
    private Point _selectStartPoint;
    private bool _isDraggingSelected;
    private System.Windows.Shapes.Rectangle? _selectionRect;
    private UIElement? _previewShape;

    // Cursor throttle
    private DateTime _lastCursorSend = DateTime.MinValue;

    // History panel
    private readonly ObservableCollection<HistoryItem> _historyItems = new();

    public MainWindow(MainViewModel vm, WpfCanvasRenderer renderer, HistoryManager history, EventAggregator events)
    {
        InitializeComponent();
        _vm = vm;
        _renderer = renderer;
        _history = history;
        _presence = new RemotePresenceManager();
        _events = events;
        DataContext = vm;

        TxtUserName.Text = vm.UserName;
        HighlightTool(BtnPen);
        LstHistory.ItemsSource = _historyItems;
        LstUsers.ItemsSource = vm.UserList.Users;

        // Subscribe to VM events for canvas updates
        events.Subscribe<RenderActionEvent>(e => { RenderAction(e.Action); AddHistoryItem(e.Action); });
        events.Subscribe<ClearCanvasEvent>(_ => { DrawCanvas.Children.Clear(); ClearSelection(); _presence.ClearAll(DrawCanvas); _historyItems.Clear(); });
        events.Subscribe<RemoveActionEvent>(e => { _renderer.RemoveAction(DrawCanvas, e.ActionId); RemoveHistoryItem(e.ActionId); });
        events.Subscribe<MoveActionEvent>(e => MoveElementOnCanvas(e.ActionId, e.DeltaX, e.DeltaY));
        events.Subscribe<SnapshotEvent>(e => { DrawCanvas.Children.Clear(); _historyItems.Clear(); foreach (var a in e.Actions) { RenderAction(a); AddHistoryItem(a); } });
        events.Subscribe<AppendChatEvent>(e => AppendChat(e.Text));
        events.Subscribe<UserLeftEvent>(e =>
        {
            _presence.RemoveCursor(e.UserId, DrawCanvas);
            _presence.ClearPreview(e.UserId, DrawCanvas);
        });

        // CursorMove and DrawPreview handled directly from network (needs Canvas reference)
        vm.Network.MessageReceived += (type, senderId, senderName, roomId, payload) =>
        {
            if (type == MessageType.CursorMove)
                Dispatcher.Invoke(() => HandleCursorMove(senderId, senderName, payload));
            if (type == MessageType.DrawPreview)
                Dispatcher.Invoke(() => HandleDrawPreview(senderId, payload));
        };

        KeyDown += MainWindow_KeyDown;
        KeyUp += MainWindow_KeyUp;
        Closing += (_, _) => vm.Network.Disconnect();
    }

    private void RenderAction(DrawActionBase action)
    {
        var element = _renderer.Render(action);
        if (element != null) DrawCanvas.Children.Add(element);
    }

    private void HandleCursorMove(string senderId, string senderName, JObject? payload)
    {
        // Bỏ qua cursor của chính mình
        if (senderId == _vm.Canvas.UserId) return;
        var data = MessageEnvelope.DeserializePayload<CursorPayload>(payload);
        if (data != null)
            _presence.UpdateCursor(senderId, senderName, data.X, data.Y, data.Color, DrawCanvas);
    }

    private void HandleDrawPreview(string senderId, JObject? payload)
    {
        var draw = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
        if (draw?.Action != null)
        {
            var el = _renderer.Render(draw.Action);
            if (el != null)
            {
                el.Opacity = Math.Max(draw.Action.Opacity * 0.7, 0.3);
                el.IsHitTestVisible = false;
                _presence.UpdatePreview(senderId, el, DrawCanvas);
            }
        }
    }

    #region Connection (delegates to VM)

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        _vm.UserName = TxtUserName.Text.Trim();
        _vm.ServerHost = TxtServerIp.Text.Trim();
        if (int.TryParse(TxtPort.Text.Trim(), out int port)) _vm.ServerPort = port;
        _vm.ConnectCommand.Execute(null);
    }

    private void BtnJoinRoom_Click(object sender, RoutedEventArgs e)
    {
        _vm.RoomId = TxtRoomId.Text.Trim();
        _vm.JoinRoomCommand.Execute(null);
    }

    #endregion

    #region Drawing - Canvas Events

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point pos = e.GetPosition(DrawCanvas);

        if (_vm.Toolbar.ActiveTool == DrawTool.Select)
        {
            HandleSelectMouseDown(pos);
            return;
        }

        if (_vm.Toolbar.ActiveTool == DrawTool.Text)
        {
            var dialog = new TextInputDialog("Hello!") { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                var action = _vm.Canvas.CreateTextAction(pos, dialog.InputText,
                    dialog.SelectedFontFamily, dialog.SelectedFontSize,
                    dialog.IsBold, dialog.IsItalic, dialog.IsUnderline, dialog.IsStrikethrough);
                if (action != null) { RenderAction(action); AddHistoryItem(action); }
            }
            return;
        }

        DrawCanvas.CaptureMouse();
        _vm.Canvas.StartDraw(pos);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(DrawCanvas);

        // Broadcast cursor (throttled)
        if (_vm.IsConnected && (DateTime.Now - _lastCursorSend).TotalMilliseconds > 50)
        {
            _lastCursorSend = DateTime.Now;
            var msg = NetMessage<CursorPayload>.Create(MessageType.CursorMove, _vm.Canvas.UserId, _vm.Canvas.UserName, _vm.Canvas.RoomId,
                new CursorPayload { X = pos.X, Y = pos.Y, Color = _vm.Canvas.UserColor });
            _ = _vm.Network.SendAsync(msg);
        }

        if (_vm.Toolbar.ActiveTool == DrawTool.Select && _isDraggingSelected)
        {
            HandleSelectMouseMove(pos);
            return;
        }

        var action = _vm.Canvas.UpdateDraw(pos);
        if (action == null) return;

        // Update preview
        if (action is NetDraw.Shared.Models.Actions.PenAction penAction)
        {
            // Live preview: use a fast Polyline that we just append to.
            // Final stroke (on MouseUp) will render with smoothed Bezier curves.
            if (_previewShape is System.Windows.Shapes.Polyline polyline)
            {
                polyline.Points.Add(pos);
            }
            else
            {
                if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
                var preview = new System.Windows.Shapes.Polyline
                {
                    Stroke = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(penAction.Color)),
                    StrokeThickness = penAction.PenStyle == NetDraw.Shared.Models.Actions.PenStyle.Highlighter
                        ? penAction.StrokeWidth * 4 : penAction.StrokeWidth,
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                    Opacity = penAction.PenStyle == NetDraw.Shared.Models.Actions.PenStyle.Highlighter
                        ? 0.4 * penAction.Opacity : penAction.Opacity,
                    IsHitTestVisible = false
                };
                foreach (var pt in penAction.Points) preview.Points.Add(new Point(pt.X, pt.Y));
                _previewShape = preview;
                DrawCanvas.Children.Add(_previewShape);
            }
        }
        else if (action is NetDraw.Shared.Models.Actions.EraseAction)
        {
            if (_previewShape is System.Windows.Shapes.Polyline polyline)
                polyline.Points.Add(pos);
            else
            {
                if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
                _previewShape = _renderer.Render(action);
                if (_previewShape != null) DrawCanvas.Children.Add(_previewShape);
            }
        }
        else
        {
            if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
            _previewShape = _renderer.Render(action);
            if (_previewShape != null) DrawCanvas.Children.Add(_previewShape);
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Point pos = e.GetPosition(DrawCanvas);

        if (_vm.Toolbar.ActiveTool == DrawTool.Select)
        {
            HandleSelectMouseUp(pos);
            return;
        }

        DrawCanvas.ReleaseMouseCapture();
        if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
        _previewShape = null;

        var action = _vm.Canvas.FinishDraw(pos);
        if (action != null) { RenderAction(action); AddHistoryItem(action); }
    }

    #endregion

    #region Select Tool

    private void HandleSelectMouseDown(Point pos)
    {
        ClearSelection();
        var hit = DrawCanvas.InputHitTest(pos) as UIElement;
        if (hit != null && hit != DrawCanvas)
        {
            var element = FindCanvasChild(hit);
            if (element != null)
            {
                _selectedElement = element;
                string? actionId = (element as FrameworkElement)?.Tag?.ToString();
                _selectedAction = _history.GetAll().FirstOrDefault(a => a.Id == actionId);
                ShowSelectionRect(element);
                _selectStartPoint = pos;
                _isDraggingSelected = true;
                DrawCanvas.CaptureMouse();
            }
        }
    }

    private void HandleSelectMouseMove(Point pos)
    {
        if (!_isDraggingSelected || _selectedElement == null) return;
        double dx = pos.X - _selectStartPoint.X, dy = pos.Y - _selectStartPoint.Y;
        double left = Canvas.GetLeft(_selectedElement), top = Canvas.GetTop(_selectedElement);
        if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
        Canvas.SetLeft(_selectedElement, left + dx); Canvas.SetTop(_selectedElement, top + dy);
        if (_selectionRect != null)
        {
            Canvas.SetLeft(_selectionRect, Canvas.GetLeft(_selectionRect) + dx);
            Canvas.SetTop(_selectionRect, Canvas.GetTop(_selectionRect) + dy);
        }
        _selectStartPoint = pos;
    }

    private void HandleSelectMouseUp(Point pos)
    {
        if (_isDraggingSelected && _selectedAction != null)
        {
            double totalDx = pos.X - _selectStartPoint.X, totalDy = pos.Y - _selectStartPoint.Y;
            if (Math.Abs(totalDx) > 1 || Math.Abs(totalDy) > 1)
            {
                var msg = NetMessage<MoveObjectPayload>.Create(MessageType.MoveObject, _vm.Canvas.UserId, _vm.Canvas.UserName, _vm.Canvas.RoomId,
                    new MoveObjectPayload { ActionId = _selectedAction.Id, DeltaX = totalDx, DeltaY = totalDy });
                _ = _vm.Network.SendAsync(msg);
            }
        }
        _isDraggingSelected = false;
        DrawCanvas.ReleaseMouseCapture();
    }

    private UIElement? FindCanvasChild(UIElement element)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (VisualTreeHelper.GetParent(current) == DrawCanvas) return current as UIElement;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ShowSelectionRect(UIElement element)
    {
        var bounds = element.TransformToAncestor(DrawCanvas).TransformBounds(new Rect(element.RenderSize));
        _selectionRect = new System.Windows.Shapes.Rectangle
        {
            Width = bounds.Width + 8, Height = bounds.Height + 8,
            Stroke = new SolidColorBrush(Colors.DodgerBlue), StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 2 }, Fill = Brushes.Transparent,
            IsHitTestVisible = false, Tag = "__selection__"
        };
        Canvas.SetLeft(_selectionRect, bounds.Left - 4); Canvas.SetTop(_selectionRect, bounds.Top - 4);
        DrawCanvas.Children.Add(_selectionRect);
    }

    private void ClearSelection()
    {
        _selectedElement = null; _selectedAction = null; _isDraggingSelected = false;
        if (_selectionRect != null) { DrawCanvas.Children.Remove(_selectionRect); _selectionRect = null; }
    }

    private void DeleteSelected()
    {
        if (_selectedAction == null) return;
        _renderer.RemoveAction(DrawCanvas, _selectedAction.Id);
        var msg = NetMessage<DeleteObjectPayload>.Create(MessageType.DeleteObject, _vm.Canvas.UserId, _vm.Canvas.UserName, _vm.Canvas.RoomId,
            new DeleteObjectPayload { ActionId = _selectedAction.Id });
        _ = _vm.Network.SendAsync(msg);
        ClearSelection();
    }

    #endregion

    #region Pan & Zoom

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e) =>
        SetZoom(_vm.Canvas.ZoomLevel + (e.Delta > 0 ? 0.1 : -0.1));

    private void Canvas_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartPan(e.GetPosition(CanvasWrapper));
        e.Handled = true;
    }

    private void Canvas_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopPan();
        e.Handled = true;
    }

    // Universal pan: middle-button drag, OR Space+left-drag (Figma-style)
    private void CanvasWrapper_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && _spaceHeld))
        {
            StartPan(e.GetPosition(CanvasWrapper));
            e.Handled = true; // prevent drawing
        }
    }

    private void CanvasWrapper_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && (e.ChangedButton == MouseButton.Middle ||
                           e.ChangedButton == MouseButton.Left))
        {
            StopPan();
            e.Handled = true;
        }
    }

    private void StartPan(Point startPos)
    {
        _isPanning = true;
        _panStart = startPos;
        _panStartX = CanvasPan.X;
        _panStartY = CanvasPan.Y;
        CanvasWrapper.CaptureMouse();
        CanvasWrapper.Cursor = System.Windows.Input.Cursors.ScrollAll;
        BrushCursor.Visibility = Visibility.Collapsed;
    }

    private void StopPan()
    {
        if (!_isPanning) return;
        _isPanning = false;
        CanvasWrapper.ReleaseMouseCapture();
        UpdateBrushCursor();
        if (CanvasWrapper.IsMouseOver &&
            _vm.Toolbar.ActiveTool != DrawTool.Select &&
            _vm.Toolbar.ActiveTool != DrawTool.Text)
        {
            BrushCursor.Visibility = Visibility.Visible;
        }
    }

    // Safety net: if we lose mouse capture unexpectedly (e.g. user releases the button off-screen,
    // alt-tab, or system takes focus), reset all drag state so the next click works normally.
    private void CanvasWrapper_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            UpdateBrushCursor();
        }
    }

    private void CanvasWrapper_MouseMove(object sender, MouseEventArgs e)
    {
        var current = e.GetPosition(CanvasWrapper);

        // Update local brush cursor overlay (screen-space). Use explicit Width/Height,
        // not ActualWidth — the latter is 0 before layout runs, causing first-frame glitch.
        if (BrushCursor.Visibility == Visibility.Visible)
        {
            Canvas.SetLeft(BrushCursor, current.X - BrushCursor.Width / 2);
            Canvas.SetTop(BrushCursor, current.Y - BrushCursor.Height / 2);
        }

        if (!_isPanning) return;
        CanvasPan.X = _panStartX + (current.X - _panStart.X);
        CanvasPan.Y = _panStartY + (current.Y - _panStart.Y);
    }

    private void CanvasWrapper_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateBrushCursor();
        if (_vm.Toolbar.ActiveTool != DrawTool.Select && _vm.Toolbar.ActiveTool != DrawTool.Text)
        {
            // Position immediately at current mouse pos so cursor doesn't flash at top-left
            var p = e.GetPosition(CanvasWrapper);
            Canvas.SetLeft(BrushCursor, p.X - BrushCursor.Width / 2);
            Canvas.SetTop(BrushCursor, p.Y - BrushCursor.Height / 2);
            BrushCursor.Visibility = Visibility.Visible;
        }
    }

    private void CanvasWrapper_MouseLeave(object sender, MouseEventArgs e)
    {
        BrushCursor.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Refresh the local brush cursor visual (size, color, shape) based on current tool/color/size/zoom.
    /// </summary>
    private void UpdateBrushCursor()
    {
        if (BrushCursor == null || _vm?.Toolbar == null) return;

        var tool = _vm.Toolbar.ActiveTool;

        // Select + Text tools use system cursor (restore it)
        if (tool == DrawTool.Select || tool == DrawTool.Text)
        {
            CanvasWrapper.Cursor = tool == DrawTool.Text
                ? System.Windows.Input.Cursors.IBeam
                : System.Windows.Input.Cursors.Arrow;
            BrushCursor.Visibility = Visibility.Collapsed;
            return;
        }
        CanvasWrapper.Cursor = System.Windows.Input.Cursors.None;

        // Compute visible cursor diameter in screen pixels
        double zoom = _vm.Canvas.ZoomLevel;
        double stroke = _vm.Toolbar.StrokeWidth;
        if (tool == DrawTool.Highlighter) stroke *= 4;
        else if (tool == DrawTool.Eraser) stroke = Math.Max(stroke, _vm.Toolbar.StrokeWidth * 6);
        double d = Math.Clamp(stroke * zoom, 10.0, 200.0);

        CursorHalo.Width = d + 6; CursorHalo.Height = d + 6;
        CursorRing.Width = d; CursorRing.Height = d;
        CursorRingInner.Width = Math.Max(d - 4, 6); CursorRingInner.Height = Math.Max(d - 4, 6);

        // Color ring = user's chosen color (for pen/shape/line); eraser = white ring on dark halo; highlighter = translucent
        var colorHex = _vm.Toolbar.CurrentColor ?? "#000000";
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
        var ringBrush = new System.Windows.Media.SolidColorBrush(color);
        ringBrush.Freeze();

        if (tool == DrawTool.Eraser)
        {
            CursorRing.Stroke = System.Windows.Media.Brushes.White;
            CursorRing.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 0, 0));
            CursorDot.Fill = System.Windows.Media.Brushes.White;
        }
        else if (tool == DrawTool.Highlighter)
        {
            CursorRing.Stroke = ringBrush;
            CursorRing.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(60, color.R, color.G, color.B));
            CursorDot.Fill = ringBrush;
        }
        else
        {
            CursorRing.Stroke = ringBrush;
            CursorRing.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(30, color.R, color.G, color.B));
            CursorDot.Fill = ringBrush;
        }

        // Center the Grid so positioning offset by w/2, h/2 works consistently
        BrushCursor.Width = CursorHalo.Width;
        BrushCursor.Height = CursorHalo.Height;
    }

    private void SetZoom(double level)
    {
        _vm.Canvas.ZoomLevel = level;
        CanvasScale.ScaleX = _vm.Canvas.ZoomLevel; CanvasScale.ScaleY = _vm.Canvas.ZoomLevel;
        TxtZoom.Text = $"{(int)(_vm.Canvas.ZoomLevel * 100)}%";
        UpdateBrushCursor();
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_vm.Canvas.ZoomLevel + 0.1);
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_vm.Canvas.ZoomLevel - 0.1);
    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) { _vm.Canvas.ResetZoom(); SetZoom(1.0); CanvasPan.X = 0; CanvasPan.Y = 0; }

    private void BtnTogglePanel_Click(object sender, RoutedEventArgs e)
    {
        SidePanel.Visibility = SidePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    #endregion

    #region Tool Selection (delegates to ToolbarVM)

    private void BtnSelect_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Select, BtnSelect);
    private void BtnPen_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Pen, BtnPen);
    private void BtnLine_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Line, BtnLine);
    private void BtnArrow_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Arrow, BtnArrow);
    private void BtnText_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Text, BtnText);
    private void BtnEraser_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Eraser, BtnEraser);
    private void BtnCalligraphy_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Calligraphy, BtnCalligraphy);
    private void BtnHighlighter_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Highlighter, BtnHighlighter);
    private void BtnSpray_Click(object s, RoutedEventArgs e) => SelectTool(DrawTool.Spray, BtnSpray);

    private void BtnRect_Click(object s, RoutedEventArgs e) { SelectTool(DrawTool.Shape, BtnRect); _vm.Toolbar.ActiveShapeType = NetDraw.Shared.Models.Actions.ShapeType.Rect; }
    private void BtnCircle_Click(object s, RoutedEventArgs e) { SelectTool(DrawTool.Shape, BtnCircle); _vm.Toolbar.ActiveShapeType = NetDraw.Shared.Models.Actions.ShapeType.Circle; }
    private void BtnEllipse_Click(object s, RoutedEventArgs e) { SelectTool(DrawTool.Shape, BtnEllipse); _vm.Toolbar.ActiveShapeType = NetDraw.Shared.Models.Actions.ShapeType.Ellipse; }
    private void BtnTriangle_Click(object s, RoutedEventArgs e) { SelectTool(DrawTool.Shape, BtnTriangle); _vm.Toolbar.ActiveShapeType = NetDraw.Shared.Models.Actions.ShapeType.Triangle; }
    private void BtnStar_Click(object s, RoutedEventArgs e) { SelectTool(DrawTool.Shape, BtnStar); _vm.Toolbar.ActiveShapeType = NetDraw.Shared.Models.Actions.ShapeType.Star; }

    private void SelectTool(DrawTool tool, Button btn)
    {
        _vm.Toolbar.ActiveTool = tool;
        HighlightTool(btn);
        if (tool != DrawTool.Select) ClearSelection();
        UpdateBrushCursor();
    }

    private Button? _activeToolBtn;
    private void HighlightTool(Button btn)
    {
        if (_activeToolBtn != null) _activeToolBtn.Tag = null;
        btn.Tag = "active";
        _activeToolBtn = btn;
    }

    private void ColorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string color)
        {
            _vm.Toolbar.CurrentColor = color;
            CurrentColorPreview.Background = WpfCanvasRenderer.BrushFromHex(color);
            TxtCurrentColor.Text = color;
            UpdateBrushCursor();
        }
    }

    private void BtnCustomColor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog(_vm.Toolbar.CurrentColor) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.Toolbar.CurrentColor = dialog.SelectedColor;
            CurrentColorPreview.Background = WpfCanvasRenderer.BrushFromHex(dialog.SelectedColor);
            TxtCurrentColor.Text = dialog.SelectedColor;
            UpdateBrushCursor();
        }
    }

    private void SliderSize_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vm?.Toolbar != null) _vm.Toolbar.StrokeWidth = e.NewValue;
        if (TxtSize != null) TxtSize.Text = ((int)e.NewValue).ToString();
        UpdateBrushCursor();
    }

    private void SliderOpacity_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vm?.Toolbar != null) _vm.Toolbar.Opacity = e.NewValue;
        if (TxtOpacity != null) TxtOpacity.Text = $"{(int)(e.NewValue * 100)}%";
    }

    private void CmbDash_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (CmbDash == null || _vm?.Toolbar == null) return;
        _vm.Toolbar.DashStyle = CmbDash.SelectedIndex switch { 1 => DashStyleModel.Dashed, 2 => DashStyleModel.Dotted, _ => DashStyleModel.Solid };
    }

    #endregion

    #region Actions (delegate to VM)

    private void BtnUndo_Click(object s, RoutedEventArgs e) => _vm.Canvas.UndoCommand.Execute(null);
    private void BtnRedo_Click(object s, RoutedEventArgs e) => _vm.Canvas.RedoCommand.Execute(null);
    private void BtnClear_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show("Xóa toàn bộ canvas?", "Xác nhận", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _vm.Canvas.ClearCommand.Execute(null);
    }

    private void BtnSave_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "NetDraw File|*.ndr|JSON|*.json", FileName = $"drawing_{DateTime.Now:yyyyMMdd_HHmmss}.ndr" };
        if (dialog.ShowDialog() != true) return;
        var fileService = new FileService();
        fileService.Save(dialog.FileName, _history.GetAll());
        _events.Publish(new AppendChatEvent($"[Hệ thống] Đã lưu: {System.IO.Path.GetFileName(dialog.FileName)}", true));
    }

    private void BtnLoad_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "NetDraw File|*.ndr|JSON|*.json|All|*.*" };
        if (dialog.ShowDialog() != true) return;
        var fileService = new FileService();
        var actions = fileService.Load(dialog.FileName);
        if (actions == null) { MessageBox.Show("File không hợp lệ!", "Lỗi"); return; }
        _vm.Canvas.HandleSnapshot(actions);
    }

    private void BtnExport_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG Image|*.png", FileName = $"NetDraw_{DateTime.Now:yyyyMMdd_HHmmss}.png" };
        if (dialog.ShowDialog() != true) return;
        var fileService = new FileService();
        fileService.ExportPng(DrawCanvas, dialog.FileName);
        MessageBox.Show($"Đã lưu: {dialog.FileName}", "Xuất ảnh");
    }

    private void BtnImportImage_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var importDlg = new ImageImportDialog(dialog.FileName) { Owner = this };
            if (importDlg.ShowDialog() != true || importDlg.ResultBitmap == null) return;
            double scale = importDlg.ScalePercent / 100.0;
            var bmp = importDlg.ResultBitmap;
            double imgW = bmp.PixelWidth * scale, imgH = bmp.PixelHeight * scale;

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            string base64 = Convert.ToBase64String(ms.ToArray());

            var action = new NetDraw.Shared.Models.Actions.ImageAction
            {
                UserId = _vm.Canvas.UserId, UserName = _vm.Canvas.UserName,
                X = Math.Max(0, (DrawCanvas.Width - imgW) / 2),
                Y = Math.Max(0, (DrawCanvas.Height - imgH) / 2), Width = imgW, Height = imgH, ImageData = base64
            };
            _history.Add(action, isLocal: true);
            RenderAction(action);
            AddHistoryItem(action);
        }
        catch (Exception ex) { MessageBox.Show($"Lỗi: {ex.Message}", "Import ảnh"); }
    }

    private void BtnTemplate_Click(object s, RoutedEventArgs e)
    {
        var dlg = new TemplateDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedActions == null) return;
        string groupId = Guid.NewGuid().ToString();
        foreach (var action in dlg.SelectedActions)
        {
            action.UserId = _vm.Canvas.UserId;
            action.GroupId = groupId;
            _history.Add(action, isLocal: true);
            RenderAction(action);
            AddHistoryItem(action);
        }
    }

    #endregion

    #region Chat

    private async void BtnChatSend_Click(object s, RoutedEventArgs e) => await SendChatAsync();
    private async void TxtChatInput_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) await SendChatAsync(); }

    private async Task SendChatAsync()
    {
        string text = TxtChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _vm.Chat.InputText = text;
        _vm.Chat.SendMessageCommand.Execute(null);
        TxtChatInput.Text = "";
    }

    private async void BtnAiSend_Click(object s, RoutedEventArgs e) => await SendAiAsync();
    private async void TxtAiCommand_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) await SendAiAsync(); }

    private async Task SendAiAsync()
    {
        string text = TxtAiCommand.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _vm.Chat.AiCommandText = text;
        _vm.Chat.SendAiCommand.Execute(null);
        TxtAiCommand.Text = "";
    }

    private void AppendChat(string text)
    {
        TxtChat.Text += text + "\n";
        ChatScroll.ScrollToEnd();
    }

    #endregion

    #region Helpers

    private void AddHistoryItem(DrawActionBase action)
    {
        _historyItems.Add(new HistoryItem(action));
        // Auto-scroll to bottom
        if (_historyItems.Count > 0)
            LstHistory.ScrollIntoView(_historyItems[^1]);
    }

    private void RemoveHistoryItem(string actionId)
    {
        for (int i = _historyItems.Count - 1; i >= 0; i--)
            if (_historyItems[i].ActionId == actionId)
            { _historyItems.RemoveAt(i); return; }
    }

    private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Click history item → highlight on canvas (select tool behavior)
        if (LstHistory.SelectedItem is HistoryItem item)
        {
            ClearSelection();
            var elements = DrawCanvas.Children.OfType<FrameworkElement>().Where(el => el.Tag?.ToString() == item.ActionId).ToList();
            if (elements.Count > 0)
            {
                _selectedElement = elements[0];
                _selectedAction = _history.GetAll().FirstOrDefault(a => a.Id == item.ActionId);
                ShowSelectionRect(elements[0]);
                _vm.Toolbar.ActiveTool = DrawTool.Select;
                HighlightTool(BtnSelect);
            }
            LstHistory.SelectedItem = null;
        }
    }

    private void MoveElementOnCanvas(string actionId, double dx, double dy)
    {
        var elements = DrawCanvas.Children.OfType<FrameworkElement>().Where(e => e.Tag?.ToString() == actionId).ToList();
        foreach (var el in elements)
        {
            double left = Canvas.GetLeft(el), top = Canvas.GetTop(el);
            if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
            Canvas.SetLeft(el, left + dx); Canvas.SetTop(el, top + dy);
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        // Space held → enable Figma-style pan (left-drag)
        if (e.Key == Key.Space && !_spaceHeld)
        {
            _spaceHeld = true;
            if (!_isPanning && CanvasWrapper.IsMouseOver)
            {
                CanvasWrapper.Cursor = System.Windows.Input.Cursors.Hand;
                BrushCursor.Visibility = Visibility.Collapsed;
            }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.V: BtnSelect_Click(BtnSelect, new RoutedEventArgs()); break;
            case Key.P: BtnPen_Click(BtnPen, new RoutedEventArgs()); break;
            case Key.L: BtnLine_Click(BtnLine, new RoutedEventArgs()); break;
            case Key.A: BtnArrow_Click(BtnArrow, new RoutedEventArgs()); break;
            case Key.R: BtnRect_Click(BtnRect, new RoutedEventArgs()); break;
            case Key.C: BtnCircle_Click(BtnCircle, new RoutedEventArgs()); break;
            case Key.E: BtnEllipse_Click(BtnEllipse, new RoutedEventArgs()); break;
            case Key.T: BtnTriangle_Click(BtnTriangle, new RoutedEventArgs()); break;
            case Key.H: BtnHighlighter_Click(BtnHighlighter, new RoutedEventArgs()); break;
            case Key.X: BtnEraser_Click(BtnEraser, new RoutedEventArgs()); break;
            case Key.Delete or Key.Back: if (_vm.Toolbar.ActiveTool == DrawTool.Select) DeleteSelected(); break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control: _vm.Canvas.UndoCommand.Execute(null); break;
            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control: _vm.Canvas.RedoCommand.Execute(null); break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control: BtnSave_Click(null!, new RoutedEventArgs()); e.Handled = true; break;
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control: BtnLoad_Click(null!, new RoutedEventArgs()); e.Handled = true; break;
            case Key.D0 when Keyboard.Modifiers == ModifierKeys.Control: BtnZoomReset_Click(null!, new RoutedEventArgs()); break;
        }
    }

    private void MainWindow_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            if (!_isPanning)
            {
                UpdateBrushCursor();
                if (CanvasWrapper.IsMouseOver &&
                    _vm.Toolbar.ActiveTool != DrawTool.Select &&
                    _vm.Toolbar.ActiveTool != DrawTool.Text)
                {
                    BrushCursor.Visibility = Visibility.Visible;
                }
            }
            e.Handled = true;
        }
    }

    #endregion
}

/// <summary>
/// Item hiển thị trong Drawing History panel.
/// </summary>
public class HistoryItem
{
    public string ActionId { get; }
    public string Description { get; }
    public string UserLabel { get; }
    public System.Windows.Media.Brush ColorBrush { get; }

    public HistoryItem(DrawActionBase action)
    {
        ActionId = action.Id;
        ColorBrush = WpfCanvasRenderer.BrushFromHex(action.Color);
        UserLabel = string.IsNullOrWhiteSpace(action.UserName) ? "?" : action.UserName!;

        Description = action switch
        {
            PenAction p => p.PenStyle switch
            {
                PenStyle.Calligraphy => $"Thư pháp ({p.Points.Count} pts)",
                PenStyle.Highlighter => $"Highlight ({p.Points.Count} pts)",
                PenStyle.Spray => $"Phun sơn ({p.Points.Count} pts)",
                _ => $"Bút vẽ ({p.Points.Count} pts)"
            },
            LineAction l => l.HasArrow ? "Mũi tên" : "Đường thẳng",
            ShapeAction s => s.ShapeType switch
            {
                ShapeType.Rect => "Chữ nhật",
                ShapeType.Ellipse => "Elip",
                ShapeType.Circle => "Hình tròn",
                ShapeType.Triangle => "Tam giác",
                ShapeType.Star => "Ngôi sao",
                _ => "Hình"
            } + (s.FillColor != null ? " (tô)" : ""),
            TextAction t => $"Text: \"{Truncate(t.Text, 20)}\"",
            EraseAction => "Tẩy",
            ImageAction => "Ảnh import",
            _ => "Khác"
        };
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
}
