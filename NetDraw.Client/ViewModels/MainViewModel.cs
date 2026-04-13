using System.Windows;
using System.Windows.Input;
using NetDraw.Client.Drawing;
using NetDraw.Client.Infrastructure;
using NetDraw.Client.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Client.ViewModels;

public class MainViewModel : ViewModelBase
{
    public readonly INetworkService Network;
    private readonly IFileService _fileService;
    private readonly EventAggregator _events;

    private bool _isConnected;
    private string _serverHost = "127.0.0.1";
    private int _serverPort = 5000;
    private string _roomId = "default";
    private string _userName = $"User{new Random().Next(100, 999)}";
    private string _statusText = "Chưa kết nối";

    public CanvasViewModel Canvas { get; }
    public ToolbarViewModel Toolbar { get; }
    public ChatViewModel Chat { get; }
    public UserListViewModel UserList { get; }

    public bool IsConnected { get => _isConnected; set => SetProperty(ref _isConnected, value); }
    public string ServerHost { get => _serverHost; set => SetProperty(ref _serverHost, value); }
    public int ServerPort { get => _serverPort; set => SetProperty(ref _serverPort, value); }
    public string RoomId { get => _roomId; set => SetProperty(ref _roomId, value); }
    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public ICommand ConnectCommand { get; }
    public ICommand JoinRoomCommand { get; }

    public MainViewModel(INetworkService network, IFileService fileService, HistoryManager history, EventAggregator events)
    {
        Network = network;
        _fileService = fileService;
        _events = events;

        Toolbar = new ToolbarViewModel(events);
        Canvas = new CanvasViewModel(network, history, Toolbar, events);
        Chat = new ChatViewModel(network, events);
        UserList = new UserListViewModel(events);

        ConnectCommand = new RelayCommand(async () => await ToggleConnectAsync());
        JoinRoomCommand = new RelayCommand(async () => await JoinRoomAsync(RoomId));

        Network.MessageReceived += OnMessageReceived;
        Network.Disconnected += reason => Application.Current.Dispatcher.Invoke(() =>
        {
            IsConnected = false;
            StatusText = reason;
        });
    }

    private async Task ToggleConnectAsync()
    {
        if (IsConnected) { Network.Disconnect(); return; }

        StatusText = "Đang kết nối...";
        bool ok = await Network.ConnectAsync(ServerHost, ServerPort);
        if (ok)
        {
            IsConnected = true;
            StatusText = $"Đã kết nối ({ServerHost}:{ServerPort})";
            SyncUserInfo();
            await JoinRoomAsync(RoomId);
        }
        else
        {
            StatusText = "Kết nối thất bại!";
        }
    }

    private async Task JoinRoomAsync(string roomId)
    {
        if (!IsConnected) return;
        if (string.IsNullOrEmpty(roomId)) roomId = "default";
        RoomId = roomId;

        Canvas.History.Clear();
        _events.Publish(new ClearCanvasEvent());
        SyncUserInfo();

        var msg = NetMessage<UserPayload>.Create(MessageType.JoinRoom, Network.ClientId, UserName, roomId,
            new UserPayload { User = new UserInfo { UserId = Network.ClientId, UserName = UserName } });
        await Network.SendAsync(msg);
    }

    private void SyncUserInfo()
    {
        Canvas.UserId = Network.ClientId;
        Canvas.UserName = UserName;
        Canvas.RoomId = RoomId;
        Chat.UserId = Network.ClientId;
        Chat.UserName = UserName;
        Chat.RoomId = RoomId;
    }

    private void OnMessageReceived(MessageType type, string senderId, string senderName, string roomId, JObject? payload)
    {
        Application.Current.Dispatcher.Invoke(() => ProcessMessage(type, senderId, senderName, roomId, payload));
    }

    private void ProcessMessage(MessageType type, string senderId, string senderName, string roomId, JObject? payload)
    {
        switch (type)
        {
            case MessageType.RoomJoined:
                var joined = MessageEnvelope.DeserializePayload<RoomJoinedPayload>(payload);
                if (joined != null)
                {
                    _events.Publish(new AppendChatEvent($"[Hệ thống] Bạn đã vào phòng '{joined.Room.RoomName}'", true));
                    _events.Publish(new UserListUpdatedEvent(joined.Users));
                    Canvas.HandleSnapshot(joined.History);
                }
                break;

            case MessageType.UserJoined:
                var userPayload = MessageEnvelope.DeserializePayload<UserPayload>(payload);
                if (userPayload != null)
                {
                    _events.Publish(new UserJoinedEvent(userPayload.User));
                    _events.Publish(new AppendChatEvent($"[+] {userPayload.User.UserName} đã vào phòng", true));
                }
                break;

            case MessageType.UserLeft:
                var leftPayload = MessageEnvelope.DeserializePayload<UserPayload>(payload);
                if (leftPayload != null)
                {
                    _events.Publish(new UserLeftEvent(leftPayload.User.UserId));
                    _events.Publish(new AppendChatEvent($"[-] {leftPayload.User.UserName} đã rời phòng", true));
                }
                break;

            case MessageType.Draw:
                var drawPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (drawPayload?.Action != null)
                {
                    Canvas.HandleRemoteAction(drawPayload.Action);
                    _events.Publish(new RenderActionEvent(drawPayload.Action));
                }
                break;

            case MessageType.DrawPreview:
                var previewPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (previewPayload?.Action != null)
                    _events.Publish(new RenderActionEvent(previewPayload.Action)); // View handles as preview
                break;

            case MessageType.ClearCanvas:
                Canvas.History.Clear();
                _events.Publish(new ClearCanvasEvent());
                _events.Publish(new AppendChatEvent($"[Hệ thống] {senderName} đã xóa canvas", true));
                break;

            case MessageType.CanvasSnapshot:
                var snapshot = MessageEnvelope.DeserializePayload<SnapshotPayload>(payload);
                if (snapshot != null) Canvas.HandleSnapshot(snapshot.Actions);
                break;

            case MessageType.Undo:
                var undoPayload = MessageEnvelope.DeserializePayload<DeleteObjectPayload>(payload);
                if (undoPayload != null) _events.Publish(new RemoveActionEvent(undoPayload.ActionId));
                break;

            case MessageType.Redo:
                var redoPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (redoPayload?.Action != null)
                {
                    Canvas.HandleRemoteAction(redoPayload.Action);
                    _events.Publish(new RenderActionEvent(redoPayload.Action));
                }
                break;

            case MessageType.MoveObject:
                var movePayload = MessageEnvelope.DeserializePayload<MoveObjectPayload>(payload);
                if (movePayload != null) _events.Publish(new MoveActionEvent(movePayload.ActionId, movePayload.DeltaX, movePayload.DeltaY));
                break;

            case MessageType.DeleteObject:
                var deletePayload = MessageEnvelope.DeserializePayload<DeleteObjectPayload>(payload);
                if (deletePayload != null) _events.Publish(new RemoveActionEvent(deletePayload.ActionId));
                break;

            case MessageType.CursorMove:
                // Handled directly by View (needs Canvas reference)
                break;

            case MessageType.ChatMessage:
                var chatPayload = MessageEnvelope.DeserializePayload<ChatPayload>(payload);
                if (chatPayload != null) _events.Publish(new ChatMessageEvent(senderName, chatPayload));
                break;

            case MessageType.AiResult:
                var aiResult = MessageEnvelope.DeserializePayload<AiResultPayload>(payload);
                if (aiResult != null)
                {
                    foreach (var action in aiResult.Actions)
                    {
                        Canvas.HandleRemoteAction(action);
                        _events.Publish(new RenderActionEvent(action));
                    }
                    if (aiResult.Error != null)
                        _events.Publish(new AiResultEvent(aiResult.Actions, aiResult.Error));
                }
                break;

            case MessageType.Error:
                var error = MessageEnvelope.DeserializePayload<ErrorPayload>(payload);
                if (error != null) _events.Publish(new AppendChatEvent($"[Lỗi] {error.Message}", true));
                break;
        }
    }
}
