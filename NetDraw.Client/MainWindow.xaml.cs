using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using Newtonsoft.Json.Linq;

namespace NetDraw.Client;

public partial class MainWindow : Window
{
    private readonly NetworkClient _network = new();

    // Drawing state
    private DrawTool _currentTool = DrawTool.Pen;
    private string _currentColor = "#000000";
    private double _strokeWidth = 2;
    private bool _isDrawing;
    private Point _startPoint;
    private DrawAction? _currentAction;
    private UIElement? _previewShape;

    // Undo/Redo
    private readonly List<DrawAction> _myActions = new();
    private readonly Stack<DrawAction> _undoStack = new();

    // User info
    private string _userId = "";
    private string _userName = "";
    private string _currentRoomId = "";

    public MainWindow()
    {
        InitializeComponent();

        // Random user name
        TxtUserName.Text = $"User{new Random().Next(100, 999)}";

        // Network events
        _network.MessageReceived += OnMessageReceived;
        _network.Disconnected += reason => Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = reason;
            TxtStatus.Foreground = CanvasRenderer.BrushFromHex("#F38BA8");
            BtnConnect.Content = "Kết nối";
            BtnConnect.Background = CanvasRenderer.BrushFromHex("#A6E3A1");
        });

        // Keyboard shortcuts
        KeyDown += MainWindow_KeyDown;

        // Highlight default tool
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
        TxtStatus.Foreground = CanvasRenderer.BrushFromHex("#F9E2AF");

        bool connected = await _network.ConnectAsync(host, port);
        if (connected)
        {
            _userId = _network.ClientId;
            TxtStatus.Text = $"Đã kết nối ({host}:{port})";
            TxtStatus.Foreground = CanvasRenderer.BrushFromHex("#A6E3A1");
            BtnConnect.Content = "Ngắt";
            BtnConnect.Background = CanvasRenderer.BrushFromHex("#F38BA8");

            // Tự động vào phòng
            await JoinRoomAsync(TxtRoomId.Text.Trim());
        }
        else
        {
            TxtStatus.Text = "Kết nối thất bại!";
            TxtStatus.Foreground = CanvasRenderer.BrushFromHex("#F38BA8");
        }
    }

    private async void BtnJoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!_network.IsConnected)
        {
            MessageBox.Show("Chưa kết nối server!", "Lỗi");
            return;
        }

        await JoinRoomAsync(TxtRoomId.Text.Trim());
    }

    private async Task JoinRoomAsync(string roomId)
    {
        if (string.IsNullOrEmpty(roomId)) roomId = "default";
        _currentRoomId = roomId;

        // Xóa canvas cũ
        DrawCanvas.Children.Clear();
        _myActions.Clear();
        _undoStack.Clear();

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
                string roomName = msg.Payload?["roomName"]?.ToString() ?? msg.RoomId;
                string userColor = msg.Payload?["userColor"]?.ToString() ?? "#000000";
                TxtRoomName.Text = $"Phòng: {roomName}";
                AppendChat($"[Hệ thống] Bạn đã vào phòng '{roomName}'", true);
                break;

            case MessageType.RoomUserList:
                var users = msg.Payload?["users"]?.ToObject<List<UserInfo>>();
                UpdateUserList(users);
                break;

            case MessageType.UserJoined:
                var joinedUser = msg.Payload?.ToObject<UserInfo>();
                if (joinedUser != null)
                {
                    AppendChat($"[+] {joinedUser.UserName} đã vào phòng", true);
                    // Refresh user list request would happen via server broadcast
                    AddUserToList(joinedUser);
                }
                break;

            case MessageType.UserLeft:
                string leftName = msg.Payload?["userName"]?.ToString() ?? "?";
                string leftId = msg.Payload?["userId"]?.ToString() ?? "";
                AppendChat($"[-] {leftName} đã rời phòng", true);
                RemoveUserFromList(leftId);
                break;

            case MessageType.DrawLine:
            case MessageType.DrawShape:
            case MessageType.DrawText:
            case MessageType.Erase:
                var drawAction = msg.Payload?.ToObject<DrawAction>();
                if (drawAction != null)
                {
                    RenderAction(drawAction);
                }
                break;

            case MessageType.ClearCanvas:
                DrawCanvas.Children.Clear();
                _myActions.Clear();
                _undoStack.Clear();
                AppendChat($"[Hệ thống] {msg.SenderName} đã xóa canvas", true);
                break;

            case MessageType.CanvasSnapshot:
                var actions = msg.Payload?["actions"]?.ToObject<List<DrawAction>>();
                if (actions != null)
                {
                    foreach (var action in actions)
                        RenderAction(action);
                }
                break;

            case MessageType.AiDrawResult:
                var aiResult = msg.Payload?.ToObject<AiResultPayload>();
                if (aiResult != null)
                {
                    foreach (var action in aiResult.Actions)
                        RenderAction(action);
                }
                break;

            case MessageType.AiError:
                string error = msg.Payload?["error"]?.ToString() ?? "Lỗi AI";
                AppendChat($"[AI Lỗi] {error}", true);
                break;

            case MessageType.ChatMessage:
                var chatPayload = msg.Payload?.ToObject<ChatMsg>();
                if (chatPayload != null)
                {
                    string prefix = chatPayload.IsSystem ? "" : $"{msg.SenderName}: ";
                    AppendChat($"{prefix}{chatPayload.Message}", chatPayload.IsSystem);
                }
                break;

            case MessageType.Error:
                string errMsg = msg.Payload?["error"]?.ToString() ?? "Lỗi";
                AppendChat($"[Lỗi] {errMsg}", true);
                break;
        }
    }

    #endregion

    #region Drawing - Canvas Events

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_network.IsConnected) return;

        _isDrawing = true;
        _startPoint = e.GetPosition(DrawCanvas);
        DrawCanvas.CaptureMouse();

        _currentAction = new DrawAction
        {
            UserId = _userId,
            Tool = _currentTool,
            Color = _currentColor,
            StrokeWidth = _strokeWidth,
            FillColor = ChkFill.IsChecked == true ? _currentColor : null,
            EraserSize = _currentTool == DrawTool.Eraser ? _strokeWidth * 5 : 20
        };

        if (_currentTool == DrawTool.Shape)
        {
            _currentAction.ShapeType = _currentShapeType;
        }

        if (_currentTool == DrawTool.Pen || _currentTool == DrawTool.Eraser)
        {
            _currentAction.Points.Add(new PointData(_startPoint.X, _startPoint.Y));
        }
        else if (_currentTool == DrawTool.Text)
        {
            // Prompt text input
            var inputDialog = new InputDialog("Nhập text:", "Hello!");
            inputDialog.Owner = this;
            if (inputDialog.ShowDialog() != true) { _isDrawing = false; return; }
            string text = inputDialog.InputText;
            if (string.IsNullOrEmpty(text))
            {
                _isDrawing = false;
                return;
            }
            _currentAction.X = _startPoint.X;
            _currentAction.Y = _startPoint.Y;
            _currentAction.Text = text;
            _currentAction.FontSize = _strokeWidth * 5 + 10;

            // Render locally & send
            RenderAction(_currentAction);
            SendDrawAction(_currentAction);
            _myActions.Add(_currentAction);
            _isDrawing = false;
            DrawCanvas.ReleaseMouseCapture();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawing || _currentAction == null) return;

        Point currentPoint = e.GetPosition(DrawCanvas);

        if (_currentTool == DrawTool.Pen || _currentTool == DrawTool.Eraser)
        {
            _currentAction.Points.Add(new PointData(currentPoint.X, currentPoint.Y));

            // Render incrementally
            if (_previewShape is Polyline polyline)
            {
                polyline.Points.Add(currentPoint);
            }
            else
            {
                // First render
                _previewShape = CanvasRenderer.Render(_currentAction);
                if (_previewShape != null)
                    DrawCanvas.Children.Add(_previewShape);
            }
        }
        else if (_currentTool == DrawTool.Line || _currentTool == DrawTool.Shape)
        {
            // Preview shape
            if (_previewShape != null)
                DrawCanvas.Children.Remove(_previewShape);

            UpdateShapeAction(_currentAction, _startPoint, currentPoint);
            _previewShape = CanvasRenderer.Render(_currentAction);
            if (_previewShape != null)
                DrawCanvas.Children.Add(_previewShape);
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing || _currentAction == null) return;

        _isDrawing = false;
        DrawCanvas.ReleaseMouseCapture();

        Point endPoint = e.GetPosition(DrawCanvas);

        if (_currentTool == DrawTool.Pen || _currentTool == DrawTool.Eraser)
        {
            if (_currentAction.Points.Count < 2) return;

            // Preview đã render rồi, chỉ cần gửi
            SendDrawAction(_currentAction);
            _myActions.Add(_currentAction);
        }
        else if (_currentTool == DrawTool.Line)
        {
            _currentAction.Points = new List<PointData>
            {
                new(_startPoint.X, _startPoint.Y),
                new(endPoint.X, endPoint.Y)
            };

            // Remove preview, render final
            if (_previewShape != null)
                DrawCanvas.Children.Remove(_previewShape);
            RenderAction(_currentAction);
            SendDrawAction(_currentAction);
            _myActions.Add(_currentAction);
        }
        else if (_currentTool == DrawTool.Shape)
        {
            UpdateShapeAction(_currentAction, _startPoint, endPoint);

            if (_previewShape != null)
                DrawCanvas.Children.Remove(_previewShape);
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

        action.X = x;
        action.Y = y;
        action.Width = w;
        action.Height = h;
        action.Radius = Math.Min(w, h) / 2;

        if (action.ShapeType == ShapeType.Circle)
        {
            double size = Math.Max(w, h);
            action.Width = size;
            action.Height = size;
        }

        action.Points = new List<PointData> { new(start.X, start.Y), new(end.X, end.Y) };
    }

    #endregion

    #region Render & Send

    private void RenderAction(DrawAction action)
    {
        var element = CanvasRenderer.Render(action);
        if (element != null)
        {
            DrawCanvas.Children.Add(element);
        }
    }

    private async void SendDrawAction(DrawAction action)
    {
        var msgType = action.Tool switch
        {
            DrawTool.Pen => MessageType.DrawLine,
            DrawTool.Line => MessageType.DrawLine,
            DrawTool.Shape => MessageType.DrawShape,
            DrawTool.Text => MessageType.DrawText,
            DrawTool.Eraser => MessageType.Erase,
            _ => MessageType.DrawLine
        };

        await _network.SendAsync(NetMessage.Create(msgType, _userId, _userName, _currentRoomId, action));
    }

    #endregion

    #region Tool Selection

    private void BtnPen_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Pen, BtnPen, "Bút vẽ");
    private void BtnLine_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Line, BtnLine, "Đường thẳng");
    private void BtnRect_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; SetShapeType(ShapeType.Rectangle); HighlightTool(BtnRect); TxtToolName.Text = "Hình chữ nhật"; }
    private void BtnCircle_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; SetShapeType(ShapeType.Circle); HighlightTool(BtnCircle); TxtToolName.Text = "Hình tròn"; }
    private void BtnEllipse_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; SetShapeType(ShapeType.Ellipse); HighlightTool(BtnEllipse); TxtToolName.Text = "Hình elip"; }
    private void BtnTriangle_Click(object sender, RoutedEventArgs e) { _currentTool = DrawTool.Shape; SetShapeType(ShapeType.Triangle); HighlightTool(BtnTriangle); TxtToolName.Text = "Tam giác"; }
    private void BtnText_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Text, BtnText, "Chèn Text");
    private void BtnEraser_Click(object sender, RoutedEventArgs e) => SelectTool(DrawTool.Eraser, BtnEraser, "Tẩy");

    private ShapeType _currentShapeType = ShapeType.Rectangle;
    private void SetShapeType(ShapeType type) => _currentShapeType = type;

    private void SelectTool(DrawTool tool, Button btn, string name)
    {
        _currentTool = tool;
        if (tool == DrawTool.Shape)
            _currentShapeType = ShapeType.Rectangle;
        HighlightTool(btn);
        TxtToolName.Text = name;
    }

    private Button? _activeToolBtn;
    private void HighlightTool(Button btn)
    {
        if (_activeToolBtn != null)
            _activeToolBtn.Background = CanvasRenderer.BrushFromHex("#313244");
        btn.Background = CanvasRenderer.BrushFromHex("#89B4FA");
        _activeToolBtn = btn;
    }

    private void ColorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string color)
        {
            _currentColor = color;
            CurrentColorPreview.Background = CanvasRenderer.BrushFromHex(color);
            TxtCurrentColor.Text = color;
        }
    }

    private void SliderSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _strokeWidth = e.NewValue;
        if (TxtSize != null)
            TxtSize.Text = ((int)e.NewValue).ToString();
    }

    #endregion

    #region Undo / Redo / Clear / Export

    private async void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_myActions.Count == 0) return;
        var lastAction = _myActions[^1];
        _myActions.RemoveAt(_myActions.Count - 1);
        _undoStack.Push(lastAction);

        // Remove from canvas
        RemoveFromCanvas(lastAction.Id);

        await _network.SendAsync(NetMessage.Create(MessageType.Undo, _userId, _userName, _currentRoomId,
            new { actionId = lastAction.Id }));
    }

    private async void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        _myActions.Add(action);

        RenderAction(action);

        await _network.SendAsync(NetMessage.Create(MessageType.Redo, _userId, _userName, _currentRoomId, action));
    }

    private async void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("Xóa toàn bộ canvas?", "Xác nhận", MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes) return;

        DrawCanvas.Children.Clear();
        _myActions.Clear();
        _undoStack.Clear();

        await _network.SendAsync(NetMessage.Create(MessageType.ClearCanvas, _userId, _userName, _currentRoomId));
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG Image|*.png",
            FileName = $"NetDraw_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog() == true)
        {
            ExportCanvasToPng(dialog.FileName);
            MessageBox.Show($"Đã lưu: {dialog.FileName}", "Xuất ảnh");
        }
    }

    private void ExportCanvasToPng(string filePath)
    {
        var bounds = VisualTreeHelper.GetDescendantBounds(DrawCanvas);
        double w = DrawCanvas.ActualWidth;
        double h = DrawCanvas.ActualHeight;

        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(new VisualBrush(DrawCanvas), null, new Rect(0, 0, w, h));
        }
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private void RemoveFromCanvas(string actionId)
    {
        var toRemove = DrawCanvas.Children.OfType<FrameworkElement>()
            .FirstOrDefault(e => e.Tag?.ToString() == actionId);
        if (toRemove != null)
            DrawCanvas.Children.Remove(toRemove);

        // Also check shapes
        var shapeToRemove = DrawCanvas.Children.OfType<Shape>()
            .FirstOrDefault(e => e.Tag?.ToString() == actionId);
        if (shapeToRemove != null)
            DrawCanvas.Children.Remove(shapeToRemove);
    }

    #endregion

    #region Chat

    private async void BtnChatSend_Click(object sender, RoutedEventArgs e)
    {
        await SendChatAsync();
    }

    private async void TxtChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SendChatAsync();
    }

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
        string time = DateTime.Now.ToString("HH:mm");
        string line = isSystem ? $"[{time}] {text}" : $"[{time}] {text}";
        TxtChat.Text += line + "\n";
        ChatScroll.ScrollToEnd();
    }

    #endregion

    #region AI Command

    private async void BtnAiSend_Click(object sender, RoutedEventArgs e)
    {
        await SendAiCommandAsync();
    }

    private async void TxtAiCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SendAiCommandAsync();
    }

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
        foreach (var user in users)
        {
            AddUserToList(user);
        }
    }

    private void AddUserToList(UserInfo user)
    {
        var item = new ListBoxItem
        {
            Content = $"● {user.UserName}",
            Foreground = CanvasRenderer.BrushFromHex(user.Color),
            Tag = user.UserId,
            FontSize = 12
        };
        LstUsers.Items.Add(item);
    }

    private void RemoveUserFromList(string userId)
    {
        var item = LstUsers.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == userId);
        if (item != null)
            LstUsers.Items.Remove(item);
    }

    #endregion

    #region Keyboard Shortcuts

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        // Bỏ qua nếu đang focus textbox
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.P: BtnPen_Click(BtnPen, new RoutedEventArgs()); break;
            case Key.L: BtnLine_Click(BtnLine, new RoutedEventArgs()); break;
            case Key.R: BtnRect_Click(BtnRect, new RoutedEventArgs()); break;
            case Key.C: BtnCircle_Click(BtnCircle, new RoutedEventArgs()); break;
            case Key.E: BtnEllipse_Click(BtnEllipse, new RoutedEventArgs()); break;
            case Key.T: BtnTriangle_Click(BtnTriangle, new RoutedEventArgs()); break;
            case Key.X: BtnEraser_Click(BtnEraser, new RoutedEventArgs()); break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                BtnUndo_Click(null!, new RoutedEventArgs()); break;
            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control:
                BtnRedo_Click(null!, new RoutedEventArgs()); break;
        }
    }

    #endregion

    // Override shape creation for current shape type
    private void Canvas_MouseLeftButtonDown_Shape()
    {
        if (_currentAction != null)
        {
            _currentAction.ShapeType = _currentShapeType;
        }
    }
}
