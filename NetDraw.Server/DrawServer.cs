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
    private readonly IRateLimiter _rateLimiter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DrawServer> _logger;
    private readonly CancellationTokenSource _cts = new();

    public DrawServer(int port, MessageDispatcher dispatcher, IClientRegistry clientRegistry, IRoomService roomService, IRateLimiter rateLimiter, ILoggerFactory loggerFactory)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _dispatcher = dispatcher;
        _clientRegistry = clientRegistry;
        _roomService = roomService;
        _rateLimiter = rateLimiter;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DrawServer>();
    }

    // Read after StartAsync() has bound the listener (port 0 → kernel-assigned port).
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task StartAsync()
    {
        _listener.Start();
        _logger.LogInformation("Listening on port {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);

        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
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
                _rateLimiter.Forget(client);
                _dispatcher.ForgetClient(client);

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

    public Task StopAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }
}
