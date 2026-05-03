using System.Net.Sockets;
using System.Text;
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

    public ClientHandler(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        UserColor = Colors[Interlocked.Increment(ref _colorIndex) % Colors.Length];
    }

    public async Task ListenAsync()
    {
        var endpoint = _tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Console.WriteLine($"[+] Client connected from {endpoint}");

        try
        {
            byte[] buffer = new byte[8192];
            char[] charBuffer = new char[8192];
            // Stateful decoder: keeps trailing bytes of an incomplete UTF-8 sequence between reads.
            // Without this, a multi-byte char split across two ReadAsync calls becomes "?" garbage —
            // breaks Vietnamese diacritics in long messages.
            var decoder = Encoding.UTF8.GetDecoder();
            while (_isConnected && _tcpClient.Connected)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                int charsDecoded = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
                _buffer.Append(charBuffer, 0, charsDecoded);
                await ProcessBufferAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Client {UserId} error: {ex.Message}");
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
        bool fatal = false;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Console.WriteLine($"[!] Send to {UserId} failed: {ex.GetType().Name}: {ex.Message}");
            fatal = true;
        }
        finally { _writeLock.Release(); }

        if (fatal) await TearDownAsync();
    }

    private async Task TearDownAsync()
    {
        if (!_isConnected) return;
        _isConnected = false;
        try { _stream.Close(); _tcpClient.Close(); } catch { }
        if (Disconnected != null)
            await Disconnected(this);
    }

    public async Task SendAsync<T>(NetMessage<T> message) where T : IPayload
    {
        await SendRawAsync(message.Serialize());
    }
}
