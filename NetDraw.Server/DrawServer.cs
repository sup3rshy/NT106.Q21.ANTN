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
    private readonly ISessionTokenStore _sessionTokenStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DrawServer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _bound;

    public DrawServer(int port, MessageDispatcher dispatcher, IClientRegistry clientRegistry, IRoomService roomService, IRateLimiter rateLimiter, ISessionTokenStore sessionTokenStore, ILoggerFactory loggerFactory)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _dispatcher = dispatcher;
        _clientRegistry = clientRegistry;
        _roomService = roomService;
        _rateLimiter = rateLimiter;
        _sessionTokenStore = sessionTokenStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DrawServer>();
    }

    // Read after Bind() (or StartAsync) so the kernel has assigned a port (port 0 → ephemeral).
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // Synchronous bind — lets callers (tests) read BoundPort before launching the accept loop.
    public void Bind()
    {
        if (_bound) return;
        _listener.Start();
        _bound = true;
    }

    public async Task StartAsync()
    {
        Bind();
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

            handler.MessageReceived += async (sender, envelope) =>
            {
                try
                {
                    await _dispatcher.DispatchAsync(envelope, sender);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dispatch error for {MessageType} from {SenderId}", envelope.Type, envelope.SenderId);
                }
            };

            handler.Disconnected += async client =>
            {
                _logger.LogInformation("Client disconnected: {UserName}", client.UserName);
                _rateLimiter.Forget(client);
                _dispatcher.ForgetClient(client);

                var roomId = _roomService.GetRoomIdForClient(client);
                bool persisted = _sessionTokenStore.MarkOrphaned(client);

                if (persisted && roomId != null)
                {
                    // Hold the user's room slot for the grace window so peers don't see
                    // UserLeft → instant rejoin churn on a flaky network. If TryClaim
                    // rebinds the entry before the timer fires, the deferred cleanup
                    // sees a different (live) handler bound to that UserId and bails.
                    ScheduleDeferredCleanup(client, roomId, client.SessionToken);
                    return;
                }

                await TeardownAsync(client, roomId);
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

    private void ScheduleDeferredCleanup(ClientHandler client, string roomId, string token)
    {
        var grace = _sessionTokenStore.GraceWindow;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(grace, _cts.Token); }
            catch (OperationCanceledException) { return; }

            // If TryClaim rebound the entry to a new (live) handler in the grace window,
            // the entry's Handler is no longer this one. Leave the room alone in that case
            // — the resume path already replayed UserJoined to peers.
            var entry = _sessionTokenStore.Get(token);
            if (entry?.Handler != null && !ReferenceEquals(entry.Handler, client)) return;

            await TeardownAsync(client, roomId);
            _sessionTokenStore.Remove(token);
        });
    }

    private async Task TeardownAsync(ClientHandler client, string? roomId)
    {
        // Always remove this dead handler from the room — RemoveUserFromRoom is keyed
        // by ClientHandler so it won't disturb a live handler that took over the slot.
        _roomService.RemoveUserFromRoom(client);

        // If a new handler claimed (or fresh-joined into) this user's slot during the
        // grace window, the room still has someone with that UserId. Don't unregister
        // them and don't broadcast UserLeft — they're still here.
        bool userStillPresent = roomId != null
            && !string.IsNullOrEmpty(client.UserId)
            && _roomService.GetRoom(roomId)?.GetUsers().Any(u => u.UserId == client.UserId) == true;
        if (userStillPresent) return;

        if (!string.IsNullOrEmpty(client.UserId)) _clientRegistry.Unregister(client.UserId);

        if (roomId != null)
        {
            var leftMsg = NetMessage<UserPayload>.Create(
                MessageType.UserLeft, client.UserId, client.UserName, roomId,
                new UserPayload { User = new UserInfo { UserId = client.UserId, UserName = client.UserName } });
            await _roomService.BroadcastToRoomAsync(roomId, leftMsg);
        }
    }
}
