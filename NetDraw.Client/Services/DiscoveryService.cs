using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Discovery;
using Newtonsoft.Json;

namespace NetDraw.Client.Services;

public record DiscoveredServer(
    string ServerId,
    string Name,
    string Host,
    int Port,
    string AppVersion,
    int Rooms,
    int Clients,
    int MaxClients,
    DateTimeOffset LastSeen);

public class DiscoveryService : IDisposable
{
    private readonly string _group;
    private readonly int _port;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, DiscoveredServer> _seen = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private Task? _loop;

    // Fires for every previously-unseen serverId. Subscribers run on the receive thread —
    // marshal to UI thread at the consumer (MainViewModel) to keep this service WPF-free.
    public event Action<DiscoveredServer>? ServerDiscovered;

    public IReadOnlyCollection<DiscoveredServer> KnownLive => _seen.Values.ToList();

    public DiscoveryService(string group = "239.255.77.12", int port = 5099, Action<string>? log = null)
    {
        _group = group;
        _port = port;
        _log = log ?? (_ => { });
    }

    public void Start()
    {
        if (_cts != null) return;

        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

            var groupAddr = IPAddress.Parse(_group);
            int joined = 0;
            foreach (var local in MulticastInterfaceIPv4Addresses())
            {
                try { _udp.JoinMulticastGroup(groupAddr, local); joined++; }
                catch (Exception ex) { _log($"[Discovery] JoinMulticastGroup failed on {local}: {ex.Message}"); }
            }
            if (joined == 0)
            {
                // Default-interface join is the safety net when no NIC qualified above
                // (e.g. host with only loopback or no IPv4 unicast). Demo will still
                // not work in those environments, but at least the listener loop runs.
                try { _udp.JoinMulticastGroup(groupAddr); joined = 1; }
                catch (Exception ex) { _log($"[Discovery] Default JoinMulticastGroup failed: {ex.Message}"); }
            }
            if (joined == 0) throw new InvalidOperationException("no multicast interfaces available");
            _log($"[Discovery] Joined {_group} on {joined} interface(s)");
        }
        catch (Exception ex)
        {
            _log($"[Discovery] Failed to join {_group}:{_port}: {ex.Message}");
            _udp?.Dispose();
            _udp = null;
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token));
        _log($"[Discovery] Listening on {_group}:{_port}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _cts = null;
        _loop = null;
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken ct)
    {
        if (_udp == null) return;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log($"[Discovery] Receive error: {ex.Message}");
                continue;
            }

            BeaconV1? beacon = TryParse(result.Buffer);
            if (beacon == null) continue;

            // The beacon's `port` is TCP; the host is whatever interface delivered the packet.
            // Servers don't know their externally-visible IP — the routing table picks the right one.
            var host = result.RemoteEndPoint.Address.ToString();
            var server = new DiscoveredServer(
                ServerId: beacon.ServerId ?? string.Empty,
                Name: beacon.Name ?? string.Empty,
                Host: host,
                Port: beacon.Port,
                AppVersion: beacon.AppVersion ?? string.Empty,
                Rooms: beacon.Rooms,
                Clients: beacon.Clients,
                MaxClients: beacon.MaxClients,
                LastSeen: DateTimeOffset.UtcNow);

            if (string.IsNullOrEmpty(server.ServerId)) continue;

            // Dedup by serverId — same host emitting the same beacon over multiple NICs would
            // otherwise show up twice. Server restart picks a new id, so this counts as a new entry.
            bool isNew = _seen.TryAdd(server.ServerId, server);
            if (isNew)
            {
                _log($"[Discovery] New server: {server.Name} @ {server.Host}:{server.Port} (id={server.ServerId})");
                try { ServerDiscovered?.Invoke(server); }
                catch (Exception ex) { _log($"[Discovery] Subscriber threw: {ex.Message}"); }
            }
            else
            {
                // Refresh in-place to keep counts/last-seen current; no event fired.
                _seen[server.ServerId] = server;
            }
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

    private static BeaconV1? TryParse(byte[] buffer)
    {
        try
        {
            var json = Encoding.UTF8.GetString(buffer);
            return JsonConvert.DeserializeObject<BeaconV1>(json);
        }
        catch
        {
            return null;
        }
    }
}
