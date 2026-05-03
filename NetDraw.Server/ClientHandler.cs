using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server;

public class ClientHandler
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly StringBuilder _buffer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<ClientHandler> _logger;
    private bool _isConnected = true;

    private static readonly string[] Colors = {
        "#E74C3C", "#3498DB", "#2ECC71", "#F39C12", "#9B59B6",
        "#1ABC9C", "#E67E22", "#34495E", "#16A085", "#C0392B"
    };
    private static int _colorIndex;

    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = "Anonymous";
    public string UserColor { get; }

    public event Func<ClientHandler, MessageType, string, string, string, JObject?, Task>? MessageReceived;
    public event Func<ClientHandler, Task>? Disconnected;

    public ClientHandler(TcpClient tcpClient, ILogger<ClientHandler> logger)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _logger = logger;
        UserColor = Colors[Interlocked.Increment(ref _colorIndex) % Colors.Length];
    }

    public async Task ListenAsync()
    {
        var endpoint = _tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogInformation("Client connected from {Endpoint}", endpoint);

        try
        {
            byte[] buffer = new byte[8192];
            while (_isConnected && _tcpClient.Connected)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                _buffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                await ProcessBufferAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Client {UserId} read loop ended with error", UserId);
        }
        finally
        {
            _isConnected = false;
            if (Disconnected != null)
                await Disconnected(this);
            try { _stream.Close(); _tcpClient.Close(); } catch { }
        }
    }

    private async Task ProcessBufferAsync()
    {
        string data = _buffer.ToString();
        int idx;
        while ((idx = data.IndexOf('\n')) >= 0)
        {
            string json = data[..idx];
            data = data[(idx + 1)..];

            if (!string.IsNullOrWhiteSpace(json))
            {
                var envelope = MessageEnvelope.Parse(json);
                if (envelope != null && MessageReceived != null)
                {
                    await MessageReceived(this, envelope.Type, envelope.SenderId, envelope.SenderName, envelope.RoomId, envelope.RawPayload);
                }
            }
        }
        _buffer.Clear();
        _buffer.Append(data);
    }

    public async Task SendRawAsync(string json)
    {
        if (!_isConnected) return;
        await _writeLock.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }
        catch { }
        finally { _writeLock.Release(); }
    }

    public async Task SendAsync<T>(NetMessage<T> message) where T : IPayload
    {
        await SendRawAsync(message.Serialize());
    }
}
