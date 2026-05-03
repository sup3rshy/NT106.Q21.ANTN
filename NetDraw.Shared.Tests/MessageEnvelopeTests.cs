using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Xunit;

namespace NetDraw.Shared.Tests;

public class MessageEnvelopeTests
{
    [Fact]
    public void Parse_ReturnsNull_OnMalformedJson()
    {
        Assert.Null(MessageEnvelope.Parse("not json"));
        Assert.Null(MessageEnvelope.Parse("{"));
    }

    [Fact]
    public void Parse_ReturnsNull_WhenTypeMissing()
    {
        Assert.Null(MessageEnvelope.Parse("""{"senderId":"x"}"""));
    }

    [Fact]
    public void Parse_ExtractsEnvelopeFields()
    {
        var msg = NetMessage<ChatPayload>.Create(
            MessageType.ChatMessage, "u1", "Alice", "room42",
            new ChatPayload { Message = "hello" });
        var json = msg.Serialize();

        var env = MessageEnvelope.Parse(json);
        Assert.NotNull(env);
        Assert.Equal(MessageType.ChatMessage, env!.Type);
        Assert.Equal("u1", env.SenderId);
        Assert.Equal("Alice", env.SenderName);
        Assert.Equal("room42", env.RoomId);
        Assert.NotNull(env.RawPayload);
    }

    [Fact]
    public void Parse_PreservesVietnameseDiacritics()
    {
        var msg = NetMessage<ChatPayload>.Create(
            MessageType.ChatMessage, "u1", "Lê Việt Hoàng", "phòng-1",
            new ChatPayload { Message = "Đây là một tin nhắn có dấu" });
        var json = msg.Serialize();

        var env = MessageEnvelope.Parse(json);
        Assert.NotNull(env);
        Assert.Equal("Lê Việt Hoàng", env!.SenderName);
        Assert.Equal("phòng-1", env.RoomId);

        var payload = MessageEnvelope.DeserializePayload<ChatPayload>(env.RawPayload);
        Assert.NotNull(payload);
        Assert.Equal("Đây là một tin nhắn có dấu", payload!.Message);
    }

    [Fact]
    public void DeserializePayload_RoundTripsDrawPayload()
    {
        var draw = new DrawPayload
        {
            Action = new PenAction
            {
                UserId = "u1",
                Color = "#112233",
                Points = { new PointData(1, 2), new PointData(3, 4) }
            }
        };
        var msg = NetMessage<DrawPayload>.Create(MessageType.Draw, "u1", "Alice", "r", draw);
        var env = MessageEnvelope.Parse(msg.Serialize())!;

        var revived = MessageEnvelope.DeserializePayload<DrawPayload>(env.RawPayload);
        Assert.NotNull(revived);
        Assert.IsType<PenAction>(revived!.Action);
        Assert.Equal("#112233", revived.Action!.Color);
    }

    [Fact]
    public void Deserialize_FullMessage_RoundTripsRoomJoined()
    {
        var payload = new RoomJoinedPayload
        {
            Room = new RoomInfo { RoomId = "r", RoomName = "r", UserCount = 1, MaxUsers = 10 },
            History = new List<DrawActionBase>
            {
                new PenAction { Points = { new PointData(0, 0) } },
                new ShapeAction { ShapeType = ShapeType.Star, Width = 10, Height = 10 }
            },
            Users = new List<UserInfo> { new() { UserId = "u1", UserName = "Alice", Color = "#FF0000" } }
        };
        var json = NetMessage<RoomJoinedPayload>.Create(MessageType.RoomJoined, "server", "Server", "r", payload).Serialize();

        var revived = MessageEnvelope.Deserialize<RoomJoinedPayload>(json);
        Assert.NotNull(revived);
        Assert.Equal(2, revived!.Payload!.History.Count);
        Assert.IsType<PenAction>(revived.Payload.History[0]);
        Assert.IsType<ShapeAction>(revived.Payload.History[1]);
    }

    [Fact]
    public void Serialize_AppendsNewlineDelimiter()
    {
        var json = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, "x", "x", "r", new ChatPayload { Message = "hi" }).Serialize();
        Assert.EndsWith("\n", json);
    }
}
