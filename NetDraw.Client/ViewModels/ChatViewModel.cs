using System.Collections.ObjectModel;
using System.Windows.Input;
using NetDraw.Client.Infrastructure;
using NetDraw.Client.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Client.ViewModels;

public record ChatMessageEvent(string SenderName, ChatPayload Payload);
public record AiResultEvent(List<DrawActionBase> Actions, string? Error);
public record AppendChatEvent(string Text, bool IsSystem);

public class ChatViewModel : ViewModelBase
{
    private readonly INetworkService _network;
    private readonly EventAggregator _events;
    private string _inputText = "";
    private string _aiCommandText = "";

    public ObservableCollection<string> Messages { get; } = new();
    public string InputText { get => _inputText; set => SetProperty(ref _inputText, value); }
    public string AiCommandText { get => _aiCommandText; set => SetProperty(ref _aiCommandText, value); }

    public ICommand SendMessageCommand { get; }
    public ICommand SendAiCommand { get; }

    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string RoomId { get; set; } = "";

    public ChatViewModel(INetworkService network, EventAggregator events)
    {
        _network = network;
        _events = events;

        SendMessageCommand = new RelayCommand(async () => await SendChatAsync());
        SendAiCommand = new RelayCommand(async () => await SendAiCommandAsync());

        events.Subscribe<ChatMessageEvent>(e =>
        {
            var text = e.Payload.IsSystem ? e.Payload.Message : $"{e.SenderName}: {e.Payload.Message}";
            Messages.Add($"[{DateTime.Now:HH:mm}] {text}");
        });

        events.Subscribe<AppendChatEvent>(e =>
            Messages.Add($"[{DateTime.Now:HH:mm}] {e.Text}"));

        events.Subscribe<AiResultEvent>(e =>
        {
            if (e.Error != null)
                Messages.Add($"[{DateTime.Now:HH:mm}] [AI Lỗi] {e.Error}");
        });
    }

    private async Task SendChatAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || !_network.IsConnected) return;
        var msg = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, UserId, UserName, RoomId,
            new ChatPayload { Message = InputText });
        await _network.SendAsync(msg);
        InputText = "";
    }

    private async Task SendAiCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(AiCommandText) || !_network.IsConnected) return;
        var msg = NetMessage<AiCommandPayload>.Create(MessageType.AiCommand, UserId, UserName, RoomId,
            new AiCommandPayload { Prompt = AiCommandText });
        await _network.SendAsync(msg);
        Messages.Add($"[{DateTime.Now:HH:mm}] [AI] Bạn: {AiCommandText}");
        AiCommandText = "";
    }
}
