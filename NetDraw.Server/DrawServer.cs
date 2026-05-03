using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server;

public class DrawServer
{
    private readonly TcpListener _listener;
    private readonly MessageDispatcher _dispatcher;
    private readonly IClientRegistry _clientRegistry;
    private readonly IRoomService _roomService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DrawServer> _logger;

    public DrawServer(int port, MessageDispatcher dispatcher, IClientRegistry clientRegistry, IRoomService roomService, ILoggerFactory loggerFactory)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _dispatcher = dispatcher;
        _clientRegistry = clientRegistry;
        _roomService = roomService;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DrawServer>();
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _logger.LogInformation("Listening on port {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);

        while (true)
        {
            var tcpClient = await _listener.AcceptTcpClientAsync();
            var handler = new ClientHandler(tcpClient, _loggerFactory.CreateLogger<ClientHandler>());

            handler.MessageReceived += async (sender, type, senderId, senderName, roomId, payload) =>
            {
                try
                {
                    await _dispatcher.DispatchAsync(type, senderId, senderName, roomId, payload, sender);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dispatch error for {MessageType} from {SenderId}", type, senderId);
                }
            };

            handler.Disconnected += async client =>
            {
                _logger.LogInformation("Client disconnected: {UserName}", client.UserName);
                var roomId = _roomService.GetRoomIdForClient(client);
                _roomService.RemoveUserFromRoom(client);
                _clientRegistry.Unregister(client.UserId);

                if (roomId != null)
                {
                    var leftMsg = NetMessage<UserPayload>.Create(
                        MessageType.UserLeft, client.UserId, client.UserName, roomId,
                        new UserPayload { User = new UserInfo { UserId = client.UserId, UserName = client.UserName } });
                    await _roomService.BroadcastToRoomAsync(roomId, leftMsg);
                }
            };

            _ = Task.Run(handler.ListenAsync);
        }
    }
}
