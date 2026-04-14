using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Services;

public class McpClient : IMcpClient
{
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly string _host;
    private readonly int _port;

    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new DrawActionConverter() }
    };

    public bool IsConnected => _client?.Connected ?? false;

    public McpClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        while (true)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_host, _port);
                var stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                Console.WriteLine($"[MCP] Connected to {_host}:{_port}");
                return;
            }
            catch
            {
                Console.WriteLine("[MCP] Connection failed, retrying in 10s...");
                await Task.Delay(10000);
            }
        }
    }

    public async Task<AiResultPayload?> SendCommandAsync(string command, string roomId)
    {
        if (!IsConnected || _writer == null || _reader == null) return null;
        try
        {
            var request = NetMessage<AiCommandPayload>.Create(
                MessageType.AiCommand,
                "server",
                "Server",
                roomId,
                new AiCommandPayload { Prompt = command });

            await _writer.WriteAsync(request.Serialize());
            
            var response = await _reader.ReadLineAsync();
            if (response == null) return null;

            var envelope = MessageEnvelope.Deserialize<AiResultPayload>(response);
            return envelope?.Payload;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MCP] Error: {ex.Message}");
            _client?.Close();
            _client = null;
            return null;
        }
    }
}
