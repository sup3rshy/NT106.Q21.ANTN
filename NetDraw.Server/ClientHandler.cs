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
    private int _tornDown;

    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = "Anonymous";
    public string UserColor { get; set; } = "#7F8C8D";

    // Write-once via RoomHandler.HandleJoinAsync. The dispatcher reads SessionTokenBytes
    // lock-free on the receive thread; safety relies on the assignment happening-before
    // the JoinRoom reply that the client must read before sending any token-bearing message.
    public string SessionToken { get; set; } = string.Empty;
    public byte[]? SessionTokenBytes { get; set; }

    public event Func<ClientHandler, MessageEnvelope.Envelope, Task>? MessageReceived;
    public event Func<ClientHandler, Task>? Disconnected;

    public ClientHandler(TcpClient tcpClient, ILogger<ClientHandler> logger)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _logger = logger;
    }

    public async Task ListenAsync()
    {
        var endpoint = _tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogInformation("Client connected from {Endpoint}", endpoint);

        try
        {
            byte[] buffer = new byte[8192];
            char[] charBuffer = new char[8192];
            // Decoder (not Encoding.GetString) — keeps state across reads so a UTF-8 sequence
            // split between two ReadAsync chunks decodes correctly.
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
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            _logger.LogInformation("Client {UserId} disconnected: {Reason}", UserId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Client {UserId} read loop ended with unexpected error", UserId);
        }
        finally
        {
            await TearDownAsync();
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
                if (envelope == null) continue;

                if (envelope.Type == MessageType.JoinRoom && envelope.Version != ProtocolVersion.Current)
                {
                    var err = NetMessage<ErrorPayload>.Create(
                        MessageType.Error, "server", "Server", envelope.RoomId,
                        new ErrorPayload { Message = $"Protocol version {envelope.Version} not supported (server expects {ProtocolVersion.Current})" });
                    await SendAsync(err);
                    _isConnected = false;
                    break;
                }

                if (MessageReceived != null)
                    await MessageReceived(this, envelope);
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
            _logger.LogWarning("Send to {UserId} failed: {ExceptionType}: {Error}", UserId, ex.GetType().Name, ex.Message);
            fatal = true;
        }
        finally { _writeLock.Release(); }

        if (fatal) await TearDownAsync();
    }

    private async Task TearDownAsync()
    {
        if (Interlocked.Exchange(ref _tornDown, 1) == 1) return;
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
