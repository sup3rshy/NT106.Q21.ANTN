using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Protocol;

namespace NetDraw.Server;

/// <summary>
/// Xử lý một client kết nối đến server
/// Mỗi client có một thread riêng để đọc message
/// </summary>
public class ClientHandler
{
    public string ClientId { get; } = Guid.NewGuid().ToString("N")[..8];
    public string UserName { get; set; } = "Anonymous";
    public string? CurrentRoomId { get; set; }
    public string UserColor { get; set; }

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly DrawServer _server;
    private readonly StringBuilder _buffer = new();
    private bool _isConnected = true;

    private static readonly string[] Colors = {
        "#E74C3C", "#3498DB", "#2ECC71", "#F39C12", "#9B59B6",
        "#1ABC9C", "#E67E22", "#34495E", "#16A085", "#C0392B"
    };
    private static int _colorIndex = 0;

    public ClientHandler(TcpClient tcpClient, DrawServer server)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _server = server;
        UserColor = Colors[_colorIndex++ % Colors.Length];
    }

    /// <summary>
    /// Bắt đầu lắng nghe message từ client (chạy trên thread riêng)
    /// </summary>
    public async Task StartAsync()
    {
        var endpoint = _tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Console.WriteLine($"[+] Client connected: {ClientId} from {endpoint}");

        try
        {
            byte[] buffer = new byte[8192];
            while (_isConnected && _tcpClient.Connected)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break; // Client disconnected
                }

                string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                _buffer.Append(data);

                // Xử lý từng message (phân tách bằng newline)
                ProcessBuffer();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Client {ClientId} error: {ex.Message}");
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    private void ProcessBuffer()
    {
        string bufferStr = _buffer.ToString();
        int newlineIndex;

        while ((newlineIndex = bufferStr.IndexOf('\n')) >= 0)
        {
            string messageJson = bufferStr[..newlineIndex];
            bufferStr = bufferStr[(newlineIndex + 1)..];

            if (!string.IsNullOrWhiteSpace(messageJson))
            {
                var message = NetMessage.Deserialize(messageJson);
                if (message != null)
                {
                    message.SenderId = ClientId;
                    message.SenderName = UserName;
                    _ = Task.Run(() => _server.HandleMessageAsync(this, message));
                }
            }
        }

        _buffer.Clear();
        _buffer.Append(bufferStr);
    }

    /// <summary>
    /// Gửi message đến client này
    /// </summary>
    public async Task SendAsync(NetMessage message)
    {
        if (!_isConnected || !_tcpClient.Connected) return;

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message.Serialize());
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Send error to {ClientId}: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        if (!_isConnected) return;
        _isConnected = false;

        Console.WriteLine($"[-] Client disconnected: {ClientId} ({UserName})");

        // Rời phòng nếu đang trong phòng
        if (CurrentRoomId != null)
        {
            await _server.HandleLeaveRoomAsync(this);
        }

        _server.RemoveClient(this);

        try
        {
            _stream.Close();
            _tcpClient.Close();
        }
        catch { }
    }
}
