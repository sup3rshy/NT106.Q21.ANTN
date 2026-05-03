using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Shared.Discovery;
using Newtonsoft.Json;

namespace NetDraw.Server.Services;

public class BeaconService
{
    private readonly IRoomService _rooms;
    private readonly int _tcpPort;
    private readonly ILogger<BeaconService> _log;

    public string Group { get; }
    public int Port { get; }
    public int IntervalMs { get; }
    public string Name { get; }
    public string AppVersion { get; }
    public string ServerId { get; }

    public BeaconService(
        IRoomService rooms,
        int tcpPort,
        ILogger<BeaconService>? logger = null,
        string group = "239.255.77.12",
        int port = 5099,
        int intervalMs = 2000,
        string? name = null,
        string? serverId = null)
    {
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _tcpPort = tcpPort;
        _log = logger ?? NullLogger<BeaconService>.Instance;
        Group = group;
        Port = port;
        IntervalMs = intervalMs;
        Name = TrimToMax(name ?? Environment.MachineName, 64);
        AppVersion = ResolveAppVersion();
        // Per-process random ID — design says regenerated on every server start so
        // dedup distinguishes restarts. Caller may inject for tests.
        ServerId = serverId ?? Guid.NewGuid().ToString("N")[..8];
    }

    public async Task RunAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            // TTL=1 keeps the multicast inside the local L2 segment — no router hops, no internet leak.
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Beacon socket setup failed; discovery disabled for this run");
            udp?.Dispose();
            return;
        }

        IPEndPoint endpoint;
        try
        {
            endpoint = new IPEndPoint(IPAddress.Parse(Group), Port);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Beacon group/port invalid (group={Group}, port={Port}); discovery disabled", Group, Port);
            udp.Dispose();
            return;
        }

        _log.LogInformation("Beacon broadcasting to {Group}:{Port} every {IntervalMs}ms (id={Id}, name={Name})",
            Group, Port, IntervalMs, ServerId, Name);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var json = SerializeBeacon();
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await udp.SendAsync(bytes, bytes.Length, endpoint);
                    _log.LogDebug("Beacon sent ({Bytes} bytes)", bytes.Length);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Beacon send failed");
                }

                try { await Task.Delay(IntervalMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            udp.Dispose();
            _log.LogInformation("Beacon stopped");
        }
    }

    // Exposed for tests — assert wire shape without binding a socket.
    internal string SerializeBeacon() => JsonConvert.SerializeObject(BuildBeacon());

    internal BeaconV1 BuildBeacon()
    {
        var infos = _rooms.GetAllRoomInfos();
        int clients = infos.Sum(r => r.UserCount);
        int maxClients = checked(_rooms.MaxRooms * _rooms.MaxUsersPerRoom);

        return new BeaconV1
        {
            Version = 1,
            ServerId = ServerId,
            Name = Name,
            Port = (ushort)_tcpPort,
            AppVersion = AppVersion,
            Rooms = ClampUshort(infos.Count),
            Clients = ClampUshort(clients),
            MaxClients = ClampUshort(maxClients),
            UnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    private static ushort ClampUshort(int v) => v < 0 ? (ushort)0 : v > ushort.MaxValue ? ushort.MaxValue : (ushort)v;

    private static string TrimToMax(string s, int max)
    {
        s = (s ?? string.Empty).Trim();
        return s.Length <= max ? s : s[..max];
    }

    private static string ResolveAppVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(BeaconService).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString()
                       ?? "0.0.0";
            // Strip any +commit suffix and cap to the design budget.
            int plus = info.IndexOf('+');
            if (plus >= 0) info = info[..plus];
            return info.Length <= 16 ? info : info[..16];
        }
        catch
        {
            return "0.0.0";
        }
    }
}
