using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Protocol;

namespace NetDraw.Client;

/// <summary>
/// Kết nối TCP đến server, gửi/nhận NetMessage
/// </summary>
public class NetworkClient
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _isConnected;
    private readonly StringBuilder _buffer = new();

    public string ClientId { get; private set; } = "";
    public bool IsConnected => _isConnected && (_client?.Connected ?? false);

    public event Action<NetMessage>? MessageReceived;
    public event Action<string>? Disconnected;

    public async Task<bool> ConnectAsync(string host, int port)
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();
            _isConnected = true;
            ClientId = Guid.NewGuid().ToString("N")[..8];

            // Bắt đầu lắng nghe
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
            while (_isConnected && _client?.Connected == true)
            {
                int bytesRead = await _stream!.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                _buffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                string data = _buffer.ToString();
                int idx;
                while ((idx = data.IndexOf('\n')) >= 0)
                {
                    string json = data[..idx];
                    data = data[(idx + 1)..];

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var msg = NetMessage.Deserialize(json);
                        if (msg != null)
                        {
                            MessageReceived?.Invoke(msg);
                        }
                    }
                }

                _buffer.Clear();
                _buffer.Append(data);
            }
        }
        catch (Exception ex)
        {
            if (_isConnected)
                Disconnected?.Invoke($"Mất kết nối: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    public async Task SendAsync(NetMessage message)
    {
        if (!IsConnected || _stream == null) return;

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
    }

    public void Disconnect()
    {
        if (!_isConnected) return;
        _isConnected = false;

        try
        {
            _stream?.Close();
            _client?.Close();
        }
        catch { }

        Disconnected?.Invoke("Đã ngắt kết nối");
    }
}
