using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Client.Services;

public class NetworkService : INetworkService
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _isConnected;
    private readonly StringBuilder _buffer = new();
    // Decoder (not Encoding.GetString) — keeps state across reads so a UTF-8 sequence
    // split between two ReadAsync chunks decodes correctly.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string ClientId { get; private set; } = "";
    public bool IsConnected => _isConnected && (_client?.Connected ?? false);

    public event Action<MessageType, string, string, string, JObject?>? MessageReceived;
    public event Action<string>? Disconnected;

    public async Task<bool> ConnectAsync(string host, int port)
    {
        try
        {
            _decoder.Reset();
            _buffer.Clear();

            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();
            _isConnected = true;
            ClientId = Guid.NewGuid().ToString("N")[..8];
            _ = Task.Run(ListenAsync);
            return true;
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke($"Không thể kết nối: {ex.Message}");
            return false;
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            byte[] buffer = new byte[8192];
            char[] charBuffer = new char[8192];
            while (_isConnected && _client?.Connected == true)
            {
                int bytesRead = await _stream!.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                int charsDecoded = _decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
                _buffer.Append(charBuffer, 0, charsDecoded);
                string data = _buffer.ToString();
                int idx;
                while ((idx = data.IndexOf('\n')) >= 0)
                {
                    string json = data[..idx];
                    data = data[(idx + 1)..];
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var envelope = MessageEnvelope.Parse(json);
                        if (envelope != null)
                            MessageReceived?.Invoke(envelope.Type, envelope.SenderId, envelope.SenderName, envelope.RoomId, envelope.RawPayload);
                    }
                }
                _buffer.Clear();
                _buffer.Append(data);
            }
        }
        catch (Exception ex)
        {
            if (_isConnected) Disconnected?.Invoke($"Mất kết nối: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    public async Task SendAsync<T>(NetMessage<T> message) where T : IPayload
    {
        if (!IsConnected || _stream == null) return;
        await _sendLock.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message.Serialize());
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke($"Lỗi gửi: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Disconnect()
    {
        if (!_isConnected) return;
        _isConnected = false;
        try { _stream?.Close(); _client?.Close(); } catch { }
        Disconnected?.Invoke("Đã ngắt kết nối");
    }
}
