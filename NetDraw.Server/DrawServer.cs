using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
    private readonly X509Certificate2? _serverCert;
    private readonly CancellationTokenSource _cts = new();
    private bool _bound;

    public DrawServer(int port, MessageDispatcher dispatcher, IClientRegistry clientRegistry, IRoomService roomService, IRateLimiter rateLimiter, ISessionTokenStore sessionTokenStore, ILoggerFactory loggerFactory, X509Certificate2? serverCert = null)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _dispatcher = dispatcher;
        _clientRegistry = clientRegistry;
        _roomService = roomService;
        _rateLimiter = rateLimiter;
        _sessionTokenStore = sessionTokenStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DrawServer>();
        _serverCert = serverCert;
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
        _logger.LogInformation("Listening on port {Port} ({Mode})",
            ((IPEndPoint)_listener.LocalEndpoint).Port,
            _serverCert is null ? "plaintext" : "TLS 1.3");

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

            _ = Task.Run(() => OnboardClientAsync(tcpClient));
        }
    }

    private async Task OnboardClientAsync(TcpClient tcpClient)
    {
        Stream? stream = await TryWrapStreamAsync(tcpClient);
        if (stream is null) return; // handshake failed; tcpClient already disposed

        var handler = new ClientHandler(tcpClient, stream, _loggerFactory.CreateLogger<ClientHandler>());

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
                ScheduleDeferredCleanup(client, roomId, client.SessionToken);
                return;
            }

            await TeardownAsync(client, roomId);
        };

        await handler.ListenAsync();
    }

    // Returns the stream the ClientHandler should read/write on, or null if the
    // connection should be discarded (TLS handshake failed). The LB-prefix read
    // is intentionally absent in Phase 1 — no LB exists yet; deferred to the
    // LB rollout (Phase 2 of the LB design / docs/design/tls-in-house.md
    // "LB-prefix ordering").
    private async Task<Stream?> TryWrapStreamAsync(TcpClient tcpClient)
    {
        if (_serverCert is null) return tcpClient.GetStream();

        var raw = tcpClient.GetStream();
        var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
        using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, handshakeTimeout.Token);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, linked.Token);
            return ssl;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            _logger.LogWarning("TLS handshake failed from {Endpoint}: {Reason}",
                tcpClient.Client.RemoteEndPoint, ex.Message);
            try { ssl.Dispose(); } catch { }
            try { tcpClient.Dispose(); } catch { }
            return null;
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
