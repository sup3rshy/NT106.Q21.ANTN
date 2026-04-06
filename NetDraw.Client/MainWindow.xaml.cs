using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DashStyleModel = NetDraw.Shared.Models.DashStyle;

namespace NetDraw.Client;

public partial class MainWindow : Window
{
    private readonly NetworkClient _network = new();

    // Drawing state
    private DrawTool _currentTool = DrawTool.Pen;
    private string _currentColor = "#000000";
    private double _strokeWidth = 2;
    private double _opacity = 1.0;
    private DashStyleModel _dashStyle = DashStyleModel.Solid;
    private bool _isDrawing;
    private Point _startPoint;
    private DrawAction? _currentAction;
    private UIElement? _previewShape;

    // Select tool state
    private UIElement? _selectedElement;
    private DrawAction? _selectedAction;
    private Point _selectStartPoint;
    private bool _isDraggingSelected;
    private System.Windows.Shapes.Rectangle? _selectionRect;

    // Pan & Zoom
    private double _zoomLevel = 1.0;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX, _panStartY;

    // Cursor tracking
    private readonly Dictionary<string, (UIElement cursor, UIElement label)> _remoteCursors = new();
    private DateTime _lastCursorSend = DateTime.MinValue;

    // Live drawing preview từ user khác
    private readonly Dictionary<string, UIElement> _remoteDrawingPreviews = new();
    private DateTime _lastDrawingUpdateSend = DateTime.MinValue;

    // Undo/Redo
    private readonly List<DrawAction> _allActions = new();
    private readonly List<DrawAction> _myActions = new();
    private readonly Stack<DrawAction> _undoStack = new();

    // User info
    private string _userId = "";
    private string _userName = "";
    private string _currentRoomId = "";

    // Shape type
    private ShapeType _currentShapeType = ShapeType.Rectangle;

    public MainWindow()
    {
        InitializeComponent();

        TxtUserName.Text = $"User{new Random().Next(100, 999)}";

        _network.MessageReceived += OnMessageReceived;
        _network.Disconnected += reason => Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = reason;
            TxtStatus.Foreground = BrushFromHex("#F38BA8");
            BtnConnect.Content = "Kết nối";
            BtnConnect.Background = BrushFromHex("#A6E3A1");
        });

        KeyDown += MainWindow_KeyDown;
        HighlightTool(BtnPen);
        Closing += (_, _) => _network.Disconnect();
    }

    #region Connection & Room

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_network.IsConnected)
        {
            _network.Disconnect();
            return;
        }

        string host = TxtServerIp.Text.Trim();
        if (!int.TryParse(TxtPort.Text.Trim(), out int port))
        {
            MessageBox.Show("Port không hợp lệ!", "Lỗi");
            return;
        }

        _userName = TxtUserName.Text.Trim();
        if (string.IsNullOrEmpty(_userName)) _userName = "Anonymous";

        TxtStatus.Text = "Đang kết nối...";
        TxtStatus.Foreground = BrushFromHex("#F9E2AF");

        bool connected = await _network.ConnectAsync(host, port);
        if (connected)
        {
            _userId = _network.ClientId;
            TxtStatus.Text = $"Đã kết nối ({host}:{port})";
            TxtStatus.Foreground = BrushFromHex("#A6E3A1");
            BtnConnect.Content = "Ngắt";
            BtnConnect.Background = BrushFromHex("#F38BA8");
            await JoinRoomAsync(TxtRoomId.Text.Trim());
        }
        else
        {
            TxtStatus.Text = "Kết nối thất bại!";
            TxtStatus.Foreground = BrushFromHex("#F38BA8");
        }
    }

    private async void BtnJoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!_network.IsConnected) { MessageBox.Show("Chưa kết nối server!", "Lỗi"); return; }
        await JoinRoomAsync(TxtRoomId.Text.Trim());
    }

    private async Task JoinRoomAsync(string roomId)
    {
        if (string.IsNullOrEmpty(roomId)) roomId = "default";
        _currentRoomId = roomId;
        DrawCanvas.Children.Clear();
        _allActions.Clear();
        _myActions.Clear();
        _undoStack.Clear();
        ClearSelection();
        ClearRemoteCursors();

        await _network.SendAsync(NetMessage.Create(
            MessageType.JoinRoom, _userId, _userName, roomId,
            new { userName = _userName }));
    }

    #endregion

    #region Message Handling

    private void OnMessageReceived(NetMessage msg)
    {
        Dispatcher.Invoke(() => ProcessMessage(msg));
    }

    private void ProcessMessage(NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.RoomJoined:
                TxtRoomName.Text = $"Phòng: {msg.Payload?["roomName"]}";
                AppendChat($"[Hệ thống] Bạn đã vào phòng '{msg.Payload?["roomName"]}'", true);
                break;

            case MessageType.RoomUserList:
                UpdateUserList(msg.Payload?["users"]?.ToObject<List<UserInfo>>());
                break;

            case MessageType.UserJoined:
                var joinedUser = msg.Payload?.ToObject<UserInfo>();
                if (joinedUser != null)
                {
                    AppendChat($"[+] {joinedUser.UserName} đã vào phòng", true);
                    AddUserToList(joinedUser);
                }
                break;

            case MessageType.UserLeft:
                string leftId = msg.Payload?["userId"]?.ToString() ?? "";
                AppendChat($"[-] {msg.Payload?["userName"]} đã rời phòng", true);
                RemoveUserFromList(leftId);
                RemoveRemoteCursor(leftId);
                break;

            case MessageType.DrawLine:
            case MessageType.DrawShape:
            case MessageType.DrawText:
            case MessageType.Erase:
                var drawAction = msg.Payload?.ToObject<DrawAction>();
                if (drawAction != null)
                {
                    // Xóa live preview của user này (nét vẽ đã hoàn thành)
                    RemoveRemoteDrawingPreview(msg.SenderId);
                    _allActions.Add(drawAction);
                    RenderAction(drawAction);
                }
                break;

            case MessageType.ClearCanvas:
                DrawCanvas.Children.Clear();
                _allActions.Clear();
                _myActions.Clear();
                _undoStack.Clear();
                AppendChat($"[Hệ thống] {msg.SenderName} đã xóa canvas", true);
                break;

            case MessageType.CanvasSnapshot:
                var actions = msg.Payload?["actions"]?.ToObject<List<DrawAction>>();
                if (actions != null)
                {
                    foreach (var action in actions)
                    {
                        _allActions.Add(action);
                        RenderAction(action);
                    }
                }
                break;

            case MessageType.AiDrawResult:
                var aiResult = msg.Payload?.ToObject<AiResultPayload>();
                if (aiResult != null)
                    foreach (var action in aiResult.Actions)
                    {
                        _allActions.Add(action);
                        RenderAction(action);
                    }
                break;

            case MessageType.AiError:
                AppendChat($"[AI Lỗi] {msg.Payload?["error"]}", true);
                break;

            case MessageType.ChatMessage:
                var chatPayload = msg.Payload?.ToObject<ChatMsg>();
                if (chatPayload != null)
                    AppendChat(chatPayload.IsSystem ? chatPayload.Message : $"{msg.SenderName}: {chatPayload.Message}", chatPayload.IsSystem);
                break;

            case MessageType.CursorMove:
                UpdateRemoteCursor(msg.SenderId, msg.SenderName, msg.Payload?.ToObject<CursorData>());
                break;

            case MessageType.DrawingUpdate:
                var liveAction = msg.Payload?.ToObject<DrawAction>();
                if (liveAction != null)
                    UpdateRemoteDrawingPreview(msg.SenderId, liveAction);
                break;

            case MessageType.DeleteObject:
                string? delId = msg.Payload?["actionId"]?.ToString();
                if (delId != null) RemoveFromCanvas(delId);
                break;

            case MessageType.MoveObject:
                var moveData = msg.Payload?.ToObject<MoveObjectPayload>();
                if (moveData != null) MoveElementOnCanvas(moveData.ActionId, moveData.DeltaX, moveData.DeltaY);
                break;

            case MessageType.Undo:
                string? undoId = msg.Payload?["actionId"]?.ToString();
                if (undoId != null) RemoveFromCanvas(undoId);
                break;

            case MessageType.Redo:
                var redoAction = msg.Payload?.ToObject<DrawAction>();
                if (redoAction != null) RenderAction(redoAction);
                break;

            case MessageType.Error:
                AppendChat($"[Lỗi] {msg.Payload?["error"]}", true);
                break;
        }
    }

    #endregion

    #region Drawing - Canvas Events

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point pos = e.GetPosition(DrawCanvas);
        _startPoint = pos;

        // Select tool
        if (_currentTool == DrawTool.Select)
        {
            HandleSelectMouseDown(pos);
            return;
        }

        if (!_network.IsConnected) return;

        _isDrawing = true;
        DrawCanvas.CaptureMouse();

        _currentAction = new DrawAction
        {
            UserId = _userId,
            Tool = _currentTool,
            Color = _currentColor,
            StrokeWidth = _strokeWidth,
            FillColor = ChkFill.IsChecked == true ? _currentColor : null,
            EraserSize = _currentTool == DrawTool.Eraser ? _strokeWidth * 5 : 20,
            Opacity = _opacity,
            DashStyle = _dashStyle
        };

        if (_currentTool == DrawTool.Shape)
            _currentAction.ShapeType = _currentShapeType;

        if (_currentTool == DrawTool.Pen || _currentTool == DrawTool.Eraser || _currentTool == DrawTool.Calligraphy || _currentTool == DrawTool.Highlighter || _currentTool == DrawTool.Spray)
        {
            _currentAction.Points.Add(new PointData(pos.X, pos.Y));
        }
        else if (_currentTool == DrawTool.Text)
        {
            var inputDialog = new InputDialog("Nhập text:", "Hello!");
            inputDialog.Owner = this;
            if (inputDialog.ShowDialog() != true) { _isDrawing = false; DrawCanvas.ReleaseMouseCapture(); return; }
            string text = inputDialog.InputText;
            if (string.IsNullOrEmpty(text)) { _isDrawing = false; DrawCanvas.ReleaseMouseCapture(); return; }

            _currentAction.X = pos.X;
            _currentAction.Y = pos.Y;
            _currentAction.Text = text;
            _currentAction.FontSize = _strokeWidth * 5 + 10;

            _allActions.Add(_currentAction);
            RenderAction(_currentAction);
            SendDrawAction(_currentAction);
            _myActions.Add(_currentAction);
            _isDrawing = false;
            DrawCanvas.ReleaseMouseCapture();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point currentPoint = e.GetPosition(DrawCanvas);

        // Broadcast cursor position (throttled)
        if (_network.IsConnected && (DateTime.Now - _lastCursorSend).TotalMilliseconds > 50)
        {
            _lastCursorSend = DateTime.Now;
            _ = _network.SendAsync(NetMessage.Create(MessageType.CursorMove, _userId, _userName, _currentRoomId,
                new CursorData { X = currentPoint.X, Y = currentPoint.Y }));
        }

        // Select tool drag
        if (_currentTool == DrawTool.Select && _isDraggingSelected)
        {
            HandleSelectMouseMove(currentPoint);
            return;
        }

        if (!_isDrawing || _currentAction == null) return;

        if (_currentTool == DrawTool.Pen || _currentTool == DrawTool.Eraser || _currentTool == DrawTool.Calligraphy || _currentTool == DrawTool.Highlighter || _currentTool == DrawTool.Spray)
        {
            _currentAction.Points.Add(new PointData(currentPoint.X, currentPoint.Y));
            if (_previewShape is Polyline polyline)
                polyline.Points.Add(currentPoint);
            else
            {
                _previewShape = CanvasRenderer.Render(_currentAction);
                if (_previewShape != null) DrawCanvas.Children.Add(_previewShape);
            }
        }
        else if (_currentTool == DrawTool.Line || _currentTool == DrawTool.Arrow || _currentTool == DrawTool.Shape)
        {
            if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
            UpdateShapeAction(_currentAction, _startPoint, currentPoint);
            _previewShape = CanvasRenderer.Render(_currentAction);
            if (_previewShape != null) DrawCanvas.Children.Add(_previewShape);
        }

        // Gửi live drawing preview cho user khác (throttled 80ms)
        if (_network.IsConnected && (DateTime.Now - _lastDrawingUpdateSend).TotalMilliseconds > 80)
        {
            _lastDrawingUpdateSend = DateTime.Now;
            _ = _network.SendAsync(NetMessage.Create(MessageType.DrawingUpdate, _userId, _userName, _currentRoomId, _currentAction));
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Point endPoint = e.GetPosition(DrawCanvas);

        if (_currentTool == DrawTool.Select)
        {
            HandleSelectMouseUp(endPoint);
            return;
        }

        if (!_isDrawing || _currentAction == null) return;
        _isDrawing = false;
        DrawCanvas.ReleaseMouseCapture();

        if (_currentTool == DrawTool.Pen || _currentTool == DrawTool.Eraser || _currentTool == DrawTool.Calligraphy || _currentTool == DrawTool.Highlighter || _currentTool == DrawTool.Spray)
        {
            if (_currentAction.Points.Count < 2) { _previewShape = null; _currentAction = null; return; }
            _allActions.Add(_currentAction);
            SendDrawAction(_currentAction);
            _myActions.Add(_currentAction);
        }
        else if (_currentTool == DrawTool.Line || _currentTool == DrawTool.Arrow)
        {
            _currentAction.Points = new List<PointData> { new(_startPoint.X, _startPoint.Y), new(endPoint.X, endPoint.Y) };
            if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
            _allActions.Add(_currentAction);
            RenderAction(_currentAction);
            SendDrawAction(_currentAction);
            _myActions.Add(_currentAction);
        }
        else if (_currentTool == DrawTool.Shape)
        {
            UpdateShapeAction(_currentAction, _startPoint, endPoint);
            if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
            _allActions.Add(_currentAction);
            RenderAction(_currentAction);
            SendDrawAction(_currentAction);
            _myActions.Add(_currentAction);
        }

        _previewShape = null;
        _currentAction = null;
    }

    private void UpdateShapeAction(DrawAction action, Point start, Point end)
    {
        double x = Math.Min(start.X, end.X);
        double y = Math.Min(start.Y, end.Y);
        double w = Math.Abs(end.X - start.X);
        double h = Math.Abs(end.Y - start.Y);

        action.X = x; action.Y = y; action.Width = w; action.Height = h;
        action.Radius = Math.Min(w, h) / 2;

        if (action.ShapeType == ShapeType.Circle)
        {
            double size = Math.Max(w, h);
            action.Width = size; action.Height = size;
        }

        action.Points = new List<PointData> { new(start.X, start.Y), new(end.X, end.Y) };
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
                _selectedAction = _allActions.FirstOrDefault(a => a.Id == actionId);

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

        double dx = pos.X - _selectStartPoint.X;
        double dy = pos.Y - _selectStartPoint.Y;

        double currentLeft = Canvas.GetLeft(_selectedElement);
        double currentTop = Canvas.GetTop(_selectedElement);
        if (double.IsNaN(currentLeft)) currentLeft = 0;
        if (double.IsNaN(currentTop)) currentTop = 0;

        Canvas.SetLeft(_selectedElement, currentLeft + dx);
        Canvas.SetTop(_selectedElement, currentTop + dy);

        if (_selectionRect != null)
        {
            Canvas.SetLeft(_selectionRect, Canvas.GetLeft(_selectionRect) + dx);
            Canvas.SetTop(_selectionRect, Canvas.GetTop(_selectionRect) + dy);
        }

        _selectStartPoint = pos;
    }

    private async void HandleSelectMouseUp(Point pos)
    {
        if (_isDraggingSelected && _selectedAction != null)
        {
            double totalDx = pos.X - _startPoint.X;
            double totalDy = pos.Y - _startPoint.Y;

            if (Math.Abs(totalDx) > 1 || Math.Abs(totalDy) > 1)
            {
                _selectedAction.X += totalDx;
                _selectedAction.Y += totalDy;
                foreach (var pt in _selectedAction.Points) { pt.X += totalDx; pt.Y += totalDy; }

                await _network.SendAsync(NetMessage.Create(MessageType.MoveObject, _userId, _userName, _currentRoomId,
                    new MoveObjectPayload { ActionId = _selectedAction.Id, DeltaX = totalDx, DeltaY = totalDy }));
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
            if (VisualTreeHelper.GetParent(current) == DrawCanvas)
                return current as UIElement;
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
            Stroke = new SolidColorBrush(Colors.DodgerBlue),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            Tag = "__selection__"
        };
        Canvas.SetLeft(_selectionRect, bounds.Left - 4);
        Canvas.SetTop(_selectionRect, bounds.Top - 4);
        DrawCanvas.Children.Add(_selectionRect);
    }

    private void ClearSelection()
    {
        _selectedElement = null;
        _selectedAction = null;
        _isDraggingSelected = false;
        if (_selectionRect != null)
        {
            DrawCanvas.Children.Remove(_selectionRect);
            _selectionRect = null;
        }
    }

    private async void DeleteSelected()
    {
        if (_selectedAction == null) return;
        RemoveFromCanvas(_selectedAction.Id);
        _allActions.RemoveAll(a => a.Id == _selectedAction.Id);
        _myActions.RemoveAll(a => a.Id == _selectedAction.Id);
        await _network.SendAsync(NetMessage.Create(MessageType.DeleteObject, _userId, _userName, _currentRoomId,
            new DeleteObjectPayload { ActionId = _selectedAction.Id }));
        ClearSelection();
    }

    #endregion

    #region Pan & Zoom

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetZoom(_zoomLevel + (e.Delta > 0 ? 0.1 : -0.1));
    }

    private void Canvas_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(CanvasWrapper);
        _panStartX = CanvasPan.X;
        _panStartY = CanvasPan.Y;
        CanvasWrapper.CaptureMouse();
    }

    private void Canvas_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        CanvasWrapper.ReleaseMouseCapture();
    }

    private void CanvasWrapper_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        Point current = e.GetPosition(CanvasWrapper);
        CanvasPan.X = _panStartX + (current.X - _panStart.X);
        CanvasPan.Y = _panStartY + (current.Y - _panStart.Y);
    }

    private void SetZoom(double level)
    {
        _zoomLevel = Math.Clamp(level, 0.2, 5.0);
        CanvasScale.ScaleX = _zoomLevel;
        CanvasScale.ScaleY = _zoomLevel;
        TxtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomLevel + 0.1);
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomLevel - 0.1);
    private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(1.0);
        CanvasPan.X = 0; CanvasPan.Y = 0;
    }

    #endregion

    #region Remote Cursors

    private void UpdateRemoteCursor(string userId, string userName, CursorData? data)
    {
        if (data == null) return;

        if (!_remoteCursors.ContainsKey(userId))
        {
            var cursorBrush = BrushFromHex(GetUserColor(userId));
            var cursor = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 0,16 L 4,12 L 8,18 L 10,17 L 6,11 L 12,11 Z"),
                Fill = cursorBrush,
                IsHitTestVisible = false
            };
            var label = new Border
            {
                Background = cursorBrush, CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1), IsHitTestVisible = false,
                Child = new TextBlock { Text = userName, Foreground = Brushes.White, FontSize = 10 }
            };
            DrawCanvas.Children.Add(cursor);
            DrawCanvas.Children.Add(label);
            _remoteCursors[userId] = (cursor, label);
        }

        var (c, l) = _remoteCursors[userId];
        Canvas.SetLeft(c, data.X); Canvas.SetTop(c, data.Y);
        Canvas.SetLeft(l, data.X + 14); Canvas.SetTop(l, data.Y + 16);
    }

    private void RemoveRemoteCursor(string userId)
    {
        if (_remoteCursors.TryGetValue(userId, out var pair))
        {
            DrawCanvas.Children.Remove(pair.cursor);
            DrawCanvas.Children.Remove(pair.label);
            _remoteCursors.Remove(userId);
        }
    }

    private void ClearRemoteCursors()
    {
        foreach (var pair in _remoteCursors.Values)
        {
            DrawCanvas.Children.Remove(pair.cursor);
            DrawCanvas.Children.Remove(pair.label);
        }
        _remoteCursors.Clear();
    }

    #endregion

    #region Remote Drawing Preview

    /// <summary>
    /// Hiển thị live preview nét vẽ đang được vẽ bởi user khác
    /// </summary>
    private void UpdateRemoteDrawingPreview(string userId, DrawAction action)
    {
        // Xóa preview cũ
        if (_remoteDrawingPreviews.TryGetValue(userId, out var oldPreview))
            DrawCanvas.Children.Remove(oldPreview);

        // Render nét vẽ tạm
        var element = CanvasRenderer.Render(action);
        if (element != null)
        {
            element.Opacity = Math.Max(action.Opacity * 0.7, 0.3); // Mờ hơn để phân biệt preview
            element.IsHitTestVisible = false;
            DrawCanvas.Children.Add(element);
            _remoteDrawingPreviews[userId] = element;
        }
    }

    private void RemoveRemoteDrawingPreview(string userId)
    {
        if (_remoteDrawingPreviews.TryGetValue(userId, out var preview))
        {
            DrawCanvas.Children.Remove(preview);
            _remoteDrawingPreviews.Remove(userId);
        }
    }

    private string GetUserColor(string userId)
    {
        foreach (ListBoxItem item in LstUsers.Items)
            if (item.Tag?.ToString() == userId && item.Foreground is SolidColorBrush brush)
                return brush.Color.ToString();
        return "#89B4FA";
    }

    #endregion

    #region Render & Send

    private void RenderAction(DrawAction action)
    {
        var element = CanvasRenderer.Render(action);
        if (element != null) DrawCanvas.Children.Add(element);
    }

    private async void SendDrawAction(DrawAction action)
    {
        var msgType = action.Tool switch
        {
            DrawTool.Pen or DrawTool.Calligraphy or DrawTool.Highlighter or DrawTool.Spray => MessageType.DrawLine,
            DrawTool.Line or DrawTool.Arrow => MessageType.DrawLine,
            DrawTool.Shape => MessageType.DrawShape,
            DrawTool.Text => MessageType.DrawText,
            DrawTool.Eraser => MessageType.Erase,
            DrawTool.Image => MessageType.DrawLine,
            _ => MessageType.DrawLine
        };
        await _network.SendAsync(NetMessage.Create(msgType, _userId, _userName, _currentRoomId, action));
    }

    #endregion

    #region Tool Selection

    private void BtnSelect_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Select, BtnSelect, "Chọn / Di chuyển");
    private void BtnPen_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Pen, BtnPen, "Bút vẽ");
    private void BtnLine_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Line, BtnLine, "Đường thẳng");
    private void BtnArrow_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Arrow, BtnArrow, "Mũi tên");
    private void BtnText_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Text, BtnText, "Chèn Text");
    private void BtnEraser_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Eraser, BtnEraser, "Tẩy");

    private void BtnCalligraphy_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Calligraphy, BtnCalligraphy, "Bút thư pháp");
    private void BtnHighlighter_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Highlighter, BtnHighlighter, "Bút highlight");
    private void BtnSpray_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Spray, BtnSpray, "Phun sơn");

    private void BtnCustomColor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog(_currentColor);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            _currentColor = dialog.SelectedColor;
            CurrentColorPreview.Background = BrushFromHex(_currentColor);
            TxtCurrentColor.Text = _currentColor;
        }
    }

    private void BtnRect_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; _currentShapeType = ShapeType.Rectangle; HighlightTool(BtnRect); TxtToolName.Text = "Hình chữ nhật"; ClearSelection(); }
    private void BtnCircle_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; _currentShapeType = ShapeType.Circle; HighlightTool(BtnCircle); TxtToolName.Text = "Hình tròn"; ClearSelection(); }
    private void BtnEllipse_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; _currentShapeType = ShapeType.Ellipse; HighlightTool(BtnEllipse); TxtToolName.Text = "Hình elip"; ClearSelection(); }
    private void BtnTriangle_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; _currentShapeType = ShapeType.Triangle; HighlightTool(BtnTriangle); TxtToolName.Text = "Tam giác"; ClearSelection(); }
    private void BtnStar_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; _currentShapeType = ShapeType.Star; HighlightTool(BtnStar); TxtToolName.Text = "Ngôi sao"; ClearSelection(); }

    private void SelectTool(DrawTool tool, Button btn, string name)
    {
        _currentTool = tool;
        HighlightTool(btn);
        TxtToolName.Text = name;
        if (tool != DrawTool.Select) ClearSelection();
    }

    private Button? _activeToolBtn;
    private void HighlightTool(Button btn)
    {
        if (_activeToolBtn != null) _activeToolBtn.Background = BrushFromHex("#313244");
        btn.Background = BrushFromHex("#89B4FA");
        _activeToolBtn = btn;
    }

    private void ColorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string color)
        {
            _currentColor = color;
            CurrentColorPreview.Background = BrushFromHex(color);
            TxtCurrentColor.Text = color;
        }
    }

    private void SliderSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _strokeWidth = e.NewValue;
        if (TxtSize != null) TxtSize.Text = ((int)e.NewValue).ToString();
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _opacity = e.NewValue;
        if (TxtOpacity != null) TxtOpacity.Text = $"{(int)(e.NewValue * 100)}%";
    }

    private void CmbDash_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbDash == null) return;
        _dashStyle = CmbDash.SelectedIndex switch
        {
            1 => DashStyleModel.Dashed,
            2 => DashStyleModel.Dotted,
            _ => DashStyleModel.Solid
        };
    }

    #endregion

    #region Undo / Redo / Clear

    private async void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_myActions.Count == 0) return;
        var lastAction = _myActions[^1];

        // Nếu action thuộc group (template), undo toàn bộ group
        if (!string.IsNullOrEmpty(lastAction.GroupId))
        {
            string groupId = lastAction.GroupId;
            var groupActions = _myActions.Where(a => a.GroupId == groupId).ToList();
            foreach (var ga in groupActions)
            {
                _myActions.Remove(ga);
                _undoStack.Push(ga);
                RemoveFromCanvas(ga.Id);
                if (_network.IsConnected)
                    await _network.SendAsync(NetMessage.Create(MessageType.Undo, _userId, _userName, _currentRoomId,
                        new { actionId = ga.Id }));
            }
        }
        else
        {
            _myActions.RemoveAt(_myActions.Count - 1);
            _undoStack.Push(lastAction);
            RemoveFromCanvas(lastAction.Id);
            if (_network.IsConnected)
                await _network.SendAsync(NetMessage.Create(MessageType.Undo, _userId, _userName, _currentRoomId,
                    new { actionId = lastAction.Id }));
        }
    }

    private async void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();

        // Nếu action thuộc group (template), redo toàn bộ group
        if (!string.IsNullOrEmpty(action.GroupId))
        {
            string groupId = action.GroupId;
            var groupActions = new List<DrawAction> { action };
            while (_undoStack.Count > 0 && _undoStack.Peek().GroupId == groupId)
                groupActions.Add(_undoStack.Pop());

            foreach (var ga in groupActions)
            {
                _myActions.Add(ga);
                RenderAction(ga);
                if (_network.IsConnected)
                    await _network.SendAsync(NetMessage.Create(MessageType.Redo, _userId, _userName, _currentRoomId, ga));
            }
        }
        else
        {
            _myActions.Add(action);
            RenderAction(action);
            if (_network.IsConnected)
                await _network.SendAsync(NetMessage.Create(MessageType.Redo, _userId, _userName, _currentRoomId, action));
        }
    }

    private async void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Xóa toàn bộ canvas?", "Xác nhận", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        DrawCanvas.Children.Clear();
        _allActions.Clear(); _myActions.Clear(); _undoStack.Clear();
        ClearSelection();
        await _network.SendAsync(NetMessage.Create(MessageType.ClearCanvas, _userId, _userName, _currentRoomId));
    }

    private void RemoveFromCanvas(string actionId)
    {
        var toRemove = DrawCanvas.Children.OfType<FrameworkElement>()
            .Where(e => e.Tag?.ToString() == actionId).ToList();
        foreach (var el in toRemove) DrawCanvas.Children.Remove(el);
    }

    private void MoveElementOnCanvas(string actionId, double dx, double dy)
    {
        var elements = DrawCanvas.Children.OfType<FrameworkElement>()
            .Where(e => e.Tag?.ToString() == actionId).ToList();
        foreach (var el in elements)
        {
            double left = Canvas.GetLeft(el); double top = Canvas.GetTop(el);
            if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
            Canvas.SetLeft(el, left + dx); Canvas.SetTop(el, top + dy);
        }
        var action = _allActions.FirstOrDefault(a => a.Id == actionId);
        if (action != null)
        {
            action.X += dx; action.Y += dy;
            foreach (var pt in action.Points) { pt.X += dx; pt.Y += dy; }
        }
    }

    #endregion

    #region Save / Load / Export

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "NetDraw File|*.ndr|JSON|*.json",
            FileName = $"drawing_{DateTime.Now:yyyyMMdd_HHmmss}.ndr"
        };
        if (dialog.ShowDialog() != true) return;

        var file = new DrawingFile
        {
            CanvasWidth = DrawCanvas.Width, CanvasHeight = DrawCanvas.Height,
            Actions = _allActions.ToList()
        };
        File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(file, Formatting.Indented));
        AppendChat($"[Hệ thống] Đã lưu: {System.IO.Path.GetFileName(dialog.FileName)}", true);
    }

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "NetDraw File|*.ndr|JSON|*.json|All|*.*" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var file = JsonConvert.DeserializeObject<DrawingFile>(File.ReadAllText(dialog.FileName));
            if (file?.Actions == null) { MessageBox.Show("File không hợp lệ!", "Lỗi"); return; }

            DrawCanvas.Children.Clear();
            _allActions.Clear(); _myActions.Clear(); _undoStack.Clear();
            ClearSelection();

            foreach (var action in file.Actions) { _allActions.Add(action); RenderAction(action); }
            AppendChat($"[Hệ thống] Đã mở: {System.IO.Path.GetFileName(dialog.FileName)} ({file.Actions.Count} đối tượng)", true);
        }
        catch (Exception ex) { MessageBox.Show($"Lỗi: {ex.Message}", "Lỗi"); }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG Image|*.png", FileName = $"NetDraw_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog() != true) return;

        double w = DrawCanvas.ActualWidth, h = DrawCanvas.ActualHeight;
        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
            ctx.DrawRectangle(new VisualBrush(DrawCanvas), null, new Rect(0, 0, w, h));
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(dialog.FileName);
        encoder.Save(fs);
        MessageBox.Show($"Đã lưu: {dialog.FileName}", "Xuất ảnh");
    }

    private void BtnImportImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var importDlg = new ImageImportDialog(dialog.FileName) { Owner = this };
            if (importDlg.ShowDialog() != true || importDlg.ResultBitmap == null) return;

            double scale = importDlg.ScalePercent / 100.0;
            var bmp = importDlg.ResultBitmap;
            double imgW = bmp.PixelWidth * scale;
            double imgH = bmp.PixelHeight * scale;

            // Encode ảnh đã filter thành Base64 PNG
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            string base64 = Convert.ToBase64String(ms.ToArray());

            double cx = Math.Max(0, (DrawCanvas.Width - imgW) / 2);
            double cy = Math.Max(0, (DrawCanvas.Height - imgH) / 2);

            var action = new DrawAction
            {
                UserId = _userId,
                Tool = DrawTool.Image,
                X = cx, Y = cy,
                Width = imgW, Height = imgH,
                ImageData = base64
            };

            _allActions.Add(action);
            RenderAction(action);
            _myActions.Add(action);

            AppendChat($"[Hệ thống] Đã import ảnh ({(int)imgW}x{(int)imgH})", true);
        }
        catch (Exception ex) { MessageBox.Show($"Lỗi: {ex.Message}", "Import ảnh"); }
    }

    private void BtnTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TemplateDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedActions == null) return;

        // Gán cùng groupId để undo toàn bộ template 1 lần
        string groupId = Guid.NewGuid().ToString();
        foreach (var action in dlg.SelectedActions)
        {
            action.UserId = _userId;
            action.GroupId = groupId;
            _allActions.Add(action);
            _myActions.Add(action);
            RenderAction(action);
        }

        // Gửi template đến các user khác trong phòng
        if (_network.IsConnected)
        {
            foreach (var action in dlg.SelectedActions)
                SendDrawAction(action);
        }

        AppendChat($"[Hệ thống] Đã thêm template ({dlg.SelectedActions.Count} đối tượng)", true);
    }

    #endregion

    #region Chat

    private async void BtnChatSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();
    private async void TxtChatInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await SendChatAsync(); }

    private async Task SendChatAsync()
    {
        string text = TxtChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text) || !_network.IsConnected) return;
        await _network.SendAsync(NetMessage.Create(MessageType.ChatMessage, _userId, _userName, _currentRoomId,
            new ChatMsg { Message = text, IsSystem = false }));
        TxtChatInput.Text = "";
    }

    private void AppendChat(string text, bool isSystem)
    {
        TxtChat.Text += $"[{DateTime.Now:HH:mm}] {text}\n";
        ChatScroll.ScrollToEnd();
    }

    #endregion

    #region AI Command

    private async void BtnAiSend_Click(object sender, RoutedEventArgs e) => await SendAiCommandAsync();
    private async void TxtAiCommand_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await SendAiCommandAsync(); }

    private async Task SendAiCommandAsync()
    {
        string prompt = TxtAiCommand.Text.Trim();
        if (string.IsNullOrEmpty(prompt) || !_network.IsConnected) return;
        await _network.SendAsync(NetMessage.Create(MessageType.AiCommand, _userId, _userName, _currentRoomId,
            new AiCommandPayload { Prompt = prompt }));
        AppendChat($"[AI] Bạn: {prompt}", false);
        TxtAiCommand.Text = "";
    }

    #endregion

    #region User List

    private void UpdateUserList(List<UserInfo>? users)
    {
        LstUsers.Items.Clear();
        if (users == null) return;
        foreach (var user in users) AddUserToList(user);
    }

    private void AddUserToList(UserInfo user)
    {
        LstUsers.Items.Add(new ListBoxItem
        {
            Content = $"● {user.UserName}", Foreground = BrushFromHex(user.Color),
            Tag = user.UserId, FontSize = 12
        });
    }

    private void RemoveUserFromList(string userId)
    {
        var item = LstUsers.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == userId);
        if (item != null) LstUsers.Items.Remove(item);
    }

    #endregion

    #region Keyboard Shortcuts

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

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
            case Key.Delete or Key.Back:
                if (_currentTool == DrawTool.Select) DeleteSelected(); break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                BtnUndo_Click(null!, new RoutedEventArgs()); break;
            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control:
                BtnRedo_Click(null!, new RoutedEventArgs()); break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control:
                BtnSave_Click(null!, new RoutedEventArgs()); e.Handled = true; break;
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                BtnLoad_Click(null!, new RoutedEventArgs()); e.Handled = true; break;
            case Key.D0 when Keyboard.Modifiers == ModifierKeys.Control:
                BtnZoomReset_Click(null!, new RoutedEventArgs()); break;
        }
    }

    #endregion

    private static SolidColorBrush BrushFromHex(string hex) => CanvasRenderer.BrushFromHex(hex);
}
