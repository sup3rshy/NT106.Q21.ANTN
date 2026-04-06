using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server;

/// <summary>
/// TCP Server chính - xử lý kết nối, phòng, và broadcast
/// </summary>
public class DrawServer
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, ClientHandler> _clients = new();
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly int _port;
    private bool _isRunning;

    // MCP Server connection (kết nối đến McpServer qua TCP)
    private TcpClient? _mcpClient;
    private NetworkStream? _mcpStream;
    private readonly string _mcpHost;
    private readonly int _mcpPort;

    public DrawServer(int port, string mcpHost = "127.0.0.1", int mcpPort = 5001)
    {
        _port = port;
        _mcpHost = mcpHost;
        _mcpPort = mcpPort;
        _listener = new TcpListener(IPAddress.Any, port);

        // Tạo phòng mặc định
        _rooms.TryAdd("default", new Room("default", "Phòng chung"));
        _rooms.TryAdd("room1", new Room("room1", "Phòng 1"));
        _rooms.TryAdd("room2", new Room("room2", "Phòng 2"));
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;

        Console.WriteLine($"========================================");
        Console.WriteLine($"  NetDraw Server started on port {_port}");
        Console.WriteLine($"========================================");
        Console.WriteLine($"  Rooms: {string.Join(", ", _rooms.Keys)}");

        // Thử kết nối MCP Server
        _ = Task.Run(ConnectMcpServerAsync);

        while (_isRunning)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();
                var handler = new ClientHandler(tcpClient, this);
                _clients.TryAdd(handler.ClientId, handler);

                // Mỗi client chạy trên task riêng
                _ = Task.Run(() => handler.StartAsync());
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    Console.WriteLine($"[!] Accept error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Xử lý message từ client
    /// </summary>
    public async Task HandleMessageAsync(ClientHandler client, NetMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case MessageType.JoinRoom:
                    await HandleJoinRoomAsync(client, message);
                    break;

                case MessageType.LeaveRoom:
                    await HandleLeaveRoomAsync(client);
                    break;

                case MessageType.RequestRoomList:
                    await HandleRequestRoomListAsync(client);
                    break;

                case MessageType.DrawLine:
                case MessageType.DrawShape:
                case MessageType.DrawText:
                case MessageType.Erase:
                    await HandleDrawAsync(client, message);
                    break;

                case MessageType.ClearCanvas:
                    await HandleClearCanvasAsync(client, message);
                    break;

                case MessageType.Undo:
                case MessageType.Redo:
                    await HandleUndoRedoAsync(client, message);
                    break;

                case MessageType.ChatMessage:
                    await HandleChatAsync(client, message);
                    break;

                case MessageType.AiCommand:
                    await HandleAiCommandAsync(client, message);
                    break;

                case MessageType.CursorMove:
                    await HandleCursorMoveAsync(client, message);
                    break;

                case MessageType.MoveObject:
                case MessageType.DeleteObject:
                    await HandleObjectManipAsync(client, message);
                    break;

                case MessageType.Ping:
                    await client.SendAsync(NetMessage.Create(MessageType.Pong, "server", "Server", ""));
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] HandleMessage error: {ex.Message}");
            await client.SendAsync(NetMessage.Create(MessageType.Error, "server", "Server", "", new { error = ex.Message }));
        }
    }

    private async Task HandleJoinRoomAsync(ClientHandler client, NetMessage message)
    {
        string roomId = message.RoomId;
        string userName = message.Payload?["userName"]?.ToString() ?? "Anonymous";

        // Rời phòng cũ nếu có
        if (client.CurrentRoomId != null)
        {
            await HandleLeaveRoomAsync(client);
        }

        client.UserName = userName;

        // Tạo phòng mới nếu chưa tồn tại
        if (!_rooms.ContainsKey(roomId))
        {
            _rooms.TryAdd(roomId, new Room(roomId, $"Phòng {roomId}"));
        }

        var room = _rooms[roomId];
        if (!room.AddClient(client))
        {
            await client.SendAsync(NetMessage.Create(MessageType.Error, "server", "Server", roomId,
                new { error = "Phòng đã đầy!" }));
            return;
        }

        client.CurrentRoomId = roomId;

        // Gửi xác nhận cho client
        await client.SendAsync(NetMessage.Create(MessageType.RoomJoined, "server", "Server", roomId,
            new { roomName = room.RoomName, userColor = client.UserColor }));

        // Gửi danh sách user
        await client.SendAsync(NetMessage.Create(MessageType.RoomUserList, "server", "Server", roomId,
            new { users = room.GetUserInfoList() }));

        // Gửi lịch sử vẽ cho user mới
        var history = room.GetDrawHistory();
        if (history.Count > 0)
        {
            await client.SendAsync(NetMessage.Create(MessageType.CanvasSnapshot, "server", "Server", roomId,
                new { actions = history }));
        }

        // Thông báo cho room
        await room.BroadcastAsync(
            NetMessage.Create(MessageType.UserJoined, "server", "Server", roomId,
                new UserInfo { UserId = client.ClientId, UserName = client.UserName, Color = client.UserColor }),
            client.ClientId);

        Console.WriteLine($"[Room] {client.UserName} ({client.ClientId}) joined room '{roomId}' ({room.UserCount} users)");
    }

    public async Task HandleLeaveRoomAsync(ClientHandler client)
    {
        if (client.CurrentRoomId == null) return;

        string roomId = client.CurrentRoomId;
        if (_rooms.TryGetValue(roomId, out var room))
        {
            room.RemoveClient(client);

            // Thông báo cho room
            await room.BroadcastAsync(
                NetMessage.Create(MessageType.UserLeft, "server", "Server", roomId,
                    new { userId = client.ClientId, userName = client.UserName }));

            Console.WriteLine($"[Room] {client.UserName} left room '{roomId}' ({room.UserCount} users)");

            // Xóa phòng rỗng (trừ phòng mặc định)
            if (room.UserCount == 0 && roomId != "default" && roomId != "room1" && roomId != "room2")
            {
                _rooms.TryRemove(roomId, out _);
            }
        }

        client.CurrentRoomId = null;
    }

    private async Task HandleRequestRoomListAsync(ClientHandler client)
    {
        var rooms = _rooms.Values.Select(r => r.ToRoomInfo()).ToList();
        await client.SendAsync(NetMessage.Create(MessageType.RoomList, "server", "Server", "",
            new { rooms }));
    }

    private async Task HandleDrawAsync(ClientHandler client, NetMessage message)
    {
        if (client.CurrentRoomId == null) return;
        if (!_rooms.TryGetValue(client.CurrentRoomId, out var room)) return;

        // Lưu draw action vào lịch sử
        var action = message.Payload?.ToObject<DrawAction>();
        if (action != null)
        {
            action.UserId = client.ClientId;
            room.AddDrawAction(action);
        }

        // Broadcast đến tất cả trong phòng (bao gồm sender để đồng bộ)
        message.SenderId = client.ClientId;
        message.SenderName = client.UserName;
        message.RoomId = client.CurrentRoomId;
        await room.BroadcastAsync(message, client.ClientId);
    }

    private async Task HandleClearCanvasAsync(ClientHandler client, NetMessage message)
    {
        if (client.CurrentRoomId == null) return;
        if (!_rooms.TryGetValue(client.CurrentRoomId, out var room)) return;

        room.ClearHistory();

        message.SenderId = client.ClientId;
        message.SenderName = client.UserName;
        await room.BroadcastAllAsync(message);
    }

    private async Task HandleUndoRedoAsync(ClientHandler client, NetMessage message)
    {
        if (client.CurrentRoomId == null) return;
        if (!_rooms.TryGetValue(client.CurrentRoomId, out var room)) return;

        message.SenderId = client.ClientId;
        message.SenderName = client.UserName;
        await room.BroadcastAllAsync(message);
    }

    private async Task HandleCursorMoveAsync(ClientHandler client, NetMessage message)
    {
        if (client.CurrentRoomId == null) return;
        if (!_rooms.TryGetValue(client.CurrentRoomId, out var room)) return;

        message.SenderId = client.ClientId;
        message.SenderName = client.UserName;
        // Broadcast cursor chỉ đến người khác (không gửi lại cho sender)
        await room.BroadcastAsync(message, client.ClientId);
    }

    private async Task HandleObjectManipAsync(ClientHandler client, NetMessage message)
    {
        if (client.CurrentRoomId == null) return;
        if (!_rooms.TryGetValue(client.CurrentRoomId, out var room)) return;

        // Nếu là delete, xóa khỏi history
        if (message.Type == MessageType.DeleteObject)
        {
            string? actionId = message.Payload?["actionId"]?.ToString();
            if (actionId != null)
                room.RemoveDrawAction(actionId);
        }

        message.SenderId = client.ClientId;
        message.SenderName = client.UserName;
        await room.BroadcastAsync(message, client.ClientId);
    }

    private async Task HandleChatAsync(ClientHandler client, NetMessage message)
    {
        if (client.CurrentRoomId == null) return;
        if (!_rooms.TryGetValue(client.CurrentRoomId, out var room)) return;

        message.SenderId = client.ClientId;
        message.SenderName = client.UserName;
        await room.BroadcastAllAsync(message);

        Console.WriteLine($"[Chat:{client.CurrentRoomId}] {client.UserName}: {message.Payload?["message"]}");
    }

    private async Task HandleAiCommandAsync(ClientHandler client, NetMessage message)
    {
        if (client.CurrentRoomId == null) return;

        string prompt = message.Payload?["prompt"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await client.SendAsync(NetMessage.Create(MessageType.AiError, "server", "Server",
                client.CurrentRoomId, new { error = "Lệnh AI không được rỗng" }));
            return;
        }

        Console.WriteLine($"[AI] {client.UserName} requested: {prompt}");

        // Thông báo đang xử lý
        if (_rooms.TryGetValue(client.CurrentRoomId, out var room))
        {
            await room.BroadcastAllAsync(NetMessage.Create(MessageType.ChatMessage, "server", "AI Assistant",
                client.CurrentRoomId, new ChatMsg { Message = $"Đang xử lý lệnh: \"{prompt}\"...", IsSystem = true }));
        }

        // Gửi đến MCP Server
        bool sent = await SendToMcpAsync(client.ClientId, client.CurrentRoomId, prompt);
        if (!sent)
        {
            // Nếu MCP không kết nối, dùng fallback parser
            var actions = FallbackAiParser.Parse(prompt, client.ClientId);
            if (actions.Count > 0)
            {
                await HandleAiResultAsync(client.ClientId, client.CurrentRoomId, prompt, actions);
            }
            else
            {
                await client.SendAsync(NetMessage.Create(MessageType.AiError, "server", "Server",
                    client.CurrentRoomId, new { error = "Không thể xử lý lệnh AI. MCP Server chưa kết nối và fallback parser không hiểu lệnh." }));
            }
        }
    }

    /// <summary>
    /// Xử lý kết quả AI và broadcast đến phòng
    /// </summary>
    public async Task HandleAiResultAsync(string clientId, string roomId, string prompt, List<DrawAction> actions)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;

        // Lưu vào lịch sử
        foreach (var action in actions)
        {
            room.AddDrawAction(action);
        }

        // Broadcast kết quả AI vẽ đến tất cả trong phòng
        await room.BroadcastAllAsync(NetMessage.Create(MessageType.AiDrawResult, "server", "AI Assistant", roomId,
            new AiResultPayload { Prompt = prompt, Actions = actions }));

        // Chat thông báo
        await room.BroadcastAllAsync(NetMessage.Create(MessageType.ChatMessage, "server", "AI Assistant", roomId,
            new ChatMsg { Message = $"Đã vẽ xong: \"{prompt}\" ({actions.Count} hình)", IsSystem = true }));

        Console.WriteLine($"[AI] Done: {prompt} -> {actions.Count} shapes");
    }

    #region MCP Server Connection

    private async Task ConnectMcpServerAsync()
    {
        while (_isRunning)
        {
            try
            {
                if (_mcpClient == null || !_mcpClient.Connected)
                {
                    _mcpClient = new TcpClient();
                    await _mcpClient.ConnectAsync(_mcpHost, _mcpPort);
                    _mcpStream = _mcpClient.GetStream();
                    Console.WriteLine($"[MCP] Connected to MCP Server at {_mcpHost}:{_mcpPort}");

                    // Lắng nghe kết quả từ MCP
                    _ = Task.Run(ListenMcpAsync);
                }
            }
            catch
            {
                Console.WriteLine($"[MCP] Cannot connect to MCP Server at {_mcpHost}:{_mcpPort}. Retrying in 10s...");
                Console.WriteLine($"[MCP] Fallback AI parser is active.");
            }

            await Task.Delay(10000);
        }
    }

    private async Task ListenMcpAsync()
    {
        try
        {
            byte[] buffer = new byte[65536];
            var sb = new System.Text.StringBuilder();

            while (_mcpClient?.Connected == true)
            {
                int bytesRead = await _mcpStream!.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                sb.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead));
                string data = sb.ToString();
                int newlineIdx;

                while ((newlineIdx = data.IndexOf('\n')) >= 0)
                {
                    string line = data[..newlineIdx];
                    data = data[(newlineIdx + 1)..];

                    var msg = NetMessage.Deserialize(line);
                    if (msg?.Type == MessageType.AiDrawResult)
                    {
                        var result = msg.Payload?.ToObject<AiResultPayload>();
                        if (result != null)
                        {
                            await HandleAiResultAsync(msg.SenderId, msg.RoomId, result.Prompt, result.Actions);
                        }
                    }
                }

                sb.Clear();
                sb.Append(data);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MCP] Connection lost: {ex.Message}");
        }
    }

    private async Task<bool> SendToMcpAsync(string clientId, string roomId, string prompt)
    {
        if (_mcpClient?.Connected != true || _mcpStream == null) return false;

        try
        {
            var msg = NetMessage.Create(MessageType.AiCommand, clientId, "", roomId,
                new AiCommandPayload { Prompt = prompt });
            byte[] data = System.Text.Encoding.UTF8.GetBytes(msg.Serialize());
            await _mcpStream.WriteAsync(data, 0, data.Length);
            await _mcpStream.FlushAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    public void RemoveClient(ClientHandler client)
    {
        _clients.TryRemove(client.ClientId, out _);
    }

    public void Stop()
    {
        _isRunning = false;
        _listener.Stop();
        _mcpClient?.Close();
    }
}
