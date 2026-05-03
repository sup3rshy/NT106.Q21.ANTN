using NetDraw.Server;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NetDraw.Server.Tests;

/// <summary>
/// Wire-shape tests for the LAN-discovery beacon. We never bind the multicast socket here:
/// loopback multicast on CI runners is unreliable (Linux containers without MULTICAST on lo,
/// macOS routes it through the wrong interface, Windows requires firewall consent on first run).
/// The build/serialize path is the part that has to stay stable; the socket layer is exercised
/// by hand on two real machines per the design doc's Phase 1 acceptance.
/// </summary>
public class BeaconServiceTests
{
    [Fact]
    public void Beacon_HasExpectedShapeAndValues()
    {
        var rooms = new FakeRoomService(maxRooms: 100, maxUsersPerRoom: 10);
        rooms.Seed("alpha", userCount: 3);
        rooms.Seed("beta", userCount: 2);

        var svc = new BeaconService(rooms, tcpPort: 5000, serverId: "deadbeef", name: "test-host");

        var b = svc.BuildBeacon();

        Assert.Equal(1, b.Version);
        Assert.Equal("deadbeef", b.ServerId);
        Assert.Equal("test-host", b.Name);
        Assert.Equal(5000, b.Port);
        Assert.Equal(2, b.Rooms);
        Assert.Equal(5, b.Clients);
        Assert.Equal(1000, b.MaxClients); // 100 * 10
        Assert.False(string.IsNullOrEmpty(b.AppVersion));
        Assert.True(b.AppVersion.Length <= 16);
        Assert.True(b.UnixSeconds > 0);
        // ts is server clock, recent — within 5s of now is plenty of slack for slow CI.
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(b.UnixSeconds, nowUnix - 5, nowUnix + 5);
    }

    [Fact]
    public void SerializedJson_UsesWireFieldNames()
    {
        var rooms = new FakeRoomService();
        var svc = new BeaconService(rooms, tcpPort: 5050, serverId: "abc12345", name: "wire-test");

        var json = svc.SerializeBeacon();
        var obj = JObject.Parse(json);

        // The wire keys are a public API; clients on other machines will key off these names.
        Assert.Equal(1, (int)obj["v"]!);
        Assert.Equal("abc12345", (string?)obj["id"]);
        Assert.Equal("wire-test", (string?)obj["name"]);
        Assert.Equal(5050, (int)obj["port"]!);
        Assert.NotNull(obj["appVersion"]);
        Assert.NotNull(obj["rooms"]);
        Assert.NotNull(obj["clients"]);
        Assert.NotNull(obj["maxClients"]);
        Assert.NotNull(obj["ts"]);
    }

    [Fact]
    public void GeneratedServerId_IsEightHexChars_WhenNotInjected()
    {
        var svc = new BeaconService(new FakeRoomService(), tcpPort: 5000);
        Assert.Matches("^[0-9a-fA-F]{8}$", svc.ServerId);
    }

    [Fact]
    public void Name_TrimsAndCapsAt64Chars()
    {
        var longName = new string('x', 200);
        var svc = new BeaconService(new FakeRoomService(), tcpPort: 5000, name: "  " + longName + "  ");
        Assert.Equal(64, svc.Name.Length);
    }

    private sealed class FakeRoomService : IRoomService
    {
        private readonly List<RoomInfo> _rooms = new();
        public int MaxUsersPerRoom { get; }
        public int MaxRooms { get; }

        public FakeRoomService(int maxRooms = 100, int maxUsersPerRoom = 10)
        {
            MaxRooms = maxRooms;
            MaxUsersPerRoom = maxUsersPerRoom;
        }

        public void Seed(string id, int userCount)
        {
            _rooms.Add(new RoomInfo
            {
                RoomId = id,
                RoomName = id,
                UserCount = userCount,
                MaxUsers = MaxUsersPerRoom,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        public List<RoomInfo> GetAllRoomInfos() => _rooms.ToList();

        // Unused by BeaconService — throw so any future coupling shows up loudly in tests.
        public Room GetOrCreateRoom(string roomId) => throw new NotImplementedException();
        public Room? GetRoom(string roomId) => throw new NotImplementedException();
        public JoinResult AddUserToRoom(string roomId, ClientHandler client, UserInfo user) => throw new NotImplementedException();
        public bool RebindClient(string roomId, ClientHandler newClient, UserInfo user) => throw new NotImplementedException();
        public void RemoveUserFromRoom(ClientHandler client) => throw new NotImplementedException();
        public string? GetRoomIdForClient(ClientHandler client) => throw new NotImplementedException();
        public Task BroadcastToRoomAsync<T>(string roomId, NetMessage<T> message, ClientHandler? exclude = null) where T : IPayload
            => throw new NotImplementedException();
    }
}
