using System.Net;
using System.Net.NetworkInformation;
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
        // Send beacon out every qualifying interface, not just whichever the OS picks
        // for 239.255.77.12 in the routing table. On a typical student laptop the routing
        // table will pick a Hyper-V/WSL/VirtualBox virtual switch and the classroom
        // WiFi never sees the beacon — silent demo failure.
        IPAddress[] localAddrs;
        try
        {
            localAddrs = MulticastInterfaceIPv4Addresses().ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Beacon NIC enumeration failed; discovery disabled for this run");
            return;
        }
        if (localAddrs.Length == 0)
        {
            _log.LogWarning("Beacon found no Up + multicast-capable IPv4 interfaces; discovery disabled for this run");
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
            return;
        }

        var senders = new List<(UdpClient Udp, IPAddress Local)>();
        try
        {
            foreach (var local in localAddrs)
            {
                UdpClient? udp = null;
                try
                {
                    udp = new UdpClient(AddressFamily.InterNetwork);
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        local.GetAddressBytes());
                    senders.Add((udp, local));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Beacon socket setup failed for interface {Local}; skipping", local);
                    udp?.Dispose();
                }
            }
            if (senders.Count == 0)
            {
                _log.LogWarning("Beacon failed to bind any interface; discovery disabled for this run");
                return;
            }

            _log.LogInformation("Beacon broadcasting to {Group}:{Port} every {IntervalMs}ms via {NicCount} NIC(s) (id={Id}, name={Name}, ifaces=[{Ifaces}])",
                Group, Port, IntervalMs, senders.Count, ServerId, Name, string.Join(", ", senders.Select(s => s.Local)));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var json = SerializeBeacon();
                    var bytes = Encoding.UTF8.GetBytes(json);
                    foreach (var (udp, _) in senders)
                    {
                        try { await udp.SendAsync(bytes, bytes.Length, endpoint); }
                        catch (Exception ex) { _log.LogWarning(ex, "Beacon send failed on one interface"); }
                    }
                    _log.LogDebug("Beacon sent ({Bytes} bytes × {NicCount} NIC)", bytes.Length, senders.Count);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Beacon serialize failed");
                }

                try { await Task.Delay(IntervalMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            foreach (var (udp, _) in senders) { try { udp.Dispose(); } catch { } }
            _log.LogInformation("Beacon stopped");
        }
    }

    internal static IEnumerable<IPAddress> MulticastInterfaceIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return ua.Address;
            }
        }
    }

    // Exposed for tests — assert wire shape without binding a socket.
    public string SerializeBeacon() => JsonConvert.SerializeObject(BuildBeacon());

    public BeaconV1 BuildBeacon()
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
