using System.Net;
using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json;

namespace NetDraw.McpServer;

/// <summary>
/// MCP Server - nhận lệnh AI từ DrawServer, gọi AI API, trả kết quả
/// Chạy TCP server trên port 5001, DrawServer kết nối vào
///
/// MCP (Model Context Protocol) flow:
/// 1. DrawServer kết nối đến McpServer qua TCP
/// 2. Client gửi lệnh AI → DrawServer forward → McpServer
/// 3. McpServer parse lệnh bằng AI (hoặc rule-based) → sinh DrawAction
/// 4. McpServer trả kết quả → DrawServer broadcast → All Clients
/// </summary>
public class McpDrawServer
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private TcpClient? _drawServerClient;
    private NetworkStream? _drawServerStream;
    private bool _isRunning;

    // AI configuration (có thể thay bằng Claude API key)
    private readonly string? _apiKey;

    public McpDrawServer(int port, string? apiKey = null)
    {
        _port = port;
        _apiKey = apiKey;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;

        Console.WriteLine($"==========================================");
        Console.WriteLine($"  NetDraw MCP Server started on port {_port}");
        Console.WriteLine($"==========================================");

        if (string.IsNullOrEmpty(_apiKey))
        {
            Console.WriteLine("  Mode: Rule-based (no API key)");
            Console.WriteLine("  Set CLAUDE_API_KEY env var for AI mode");
        }
        else
        {
            Console.WriteLine("  Mode: AI-powered (Claude API)");
        }

        Console.WriteLine();

        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                Console.WriteLine($"[+] DrawServer connected from {client.Client.RemoteEndPoint}");
                _drawServerClient = client;
                _drawServerStream = client.GetStream();

                _ = Task.Run(() => HandleDrawServerAsync(client));
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    Console.WriteLine($"[!] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleDrawServerAsync(TcpClient client)
    {
        var buffer = new byte[65536];
        var sb = new StringBuilder();

        try
        {
            while (client.Connected)
            {
                int bytesRead = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                string data = sb.ToString();
                int idx;

                while ((idx = data.IndexOf('\n')) >= 0)
                {
                    string json = data[..idx];
                    data = data[(idx + 1)..];

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var envelope = MessageEnvelope.Parse(json);
                        if (envelope?.Type == MessageType.AiCommand)
                        {
                            var payload = MessageEnvelope.DeserializePayload<AiCommandPayload>(envelope.RawPayload);
                            await ProcessAiCommandAsync(envelope, payload);
                        }
                    }
                }

                sb.Clear();
                sb.Append(data);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] DrawServer connection error: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("[-] DrawServer disconnected");
        }
    }

    private async Task ProcessAiCommandAsync(MessageEnvelope.Envelope envelope, AiCommandPayload? cmdPayload)
    {
        string prompt = cmdPayload?.Prompt ?? "";
        string clientId = envelope.SenderId;
        string roomId = envelope.RoomId;

        Console.WriteLine($"[AI] Processing: \"{prompt}\" from {clientId} in room {roomId}");

        List<DrawActionBase> actions;

        if (!string.IsNullOrEmpty(_apiKey))
        {
            // Gọi Claude API
            actions = await CallClaudeApiAsync(prompt, clientId);
        }
        else
        {
            // Rule-based parsing (enhanced)
            actions = EnhancedAiParser.Parse(prompt, clientId);
        }

        Console.WriteLine($"[AI] Generated {actions.Count} draw actions");

        // Gửi kết quả về DrawServer
        var result = NetMessage<AiResultPayload>.Create(MessageType.AiResult, clientId, "", roomId,
            new AiResultPayload { Prompt = prompt, Actions = actions });

        await SendToDrawServerAsync(result);
    }

    private async Task<List<DrawActionBase>> CallClaudeApiAsync(string prompt, string userId)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
            httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            string systemPrompt = @"You are a drawing assistant for a collaborative canvas app (800x600 pixels).
When the user asks you to draw something, respond with a JSON array of draw actions.
Each action should have these fields:
- type: ""shape"" or ""line"" or ""text"" (the action type discriminator)
- shapeType: ""Rect"", ""Circle"", ""Ellipse"", ""Triangle"", or ""Star"" (for shape type)
- x, y: position (top-left for shapes, or center for circles)
- width, height: size
- color: hex color string like ""#FF0000""
- fillColor: hex color for fill (or null for no fill)
- strokeWidth: line width (default 2)
- startX, startY, endX, endY: for line type
- text: for text type
- fontSize: for text type

Respond ONLY with a JSON array, no other text. Example:
[{""type"":""shape"",""shapeType"":""Circle"",""x"":400,""y"":300,""width"":100,""height"":100,""color"":""#FF0000"",""fillColor"":""#FF0000"",""strokeWidth"":2}]";

            var requestBody = new
            {
                model = "claude-sonnet-4-20250514",
                max_tokens = 2048,
                system = systemPrompt,
                messages = new[] { new { role = "user", content = prompt } }
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);
            var responseStr = await response.Content.ReadAsStringAsync();

            // Parse response
            dynamic responseObj = JsonConvert.DeserializeObject(responseStr)!;
            string aiText = (string)responseObj.content[0].text;

            // Extract JSON array from response
            int startIdx = aiText.IndexOf('[');
            int endIdx = aiText.LastIndexOf(']');
            if (startIdx >= 0 && endIdx > startIdx)
            {
                string jsonArray = aiText[startIdx..(endIdx + 1)];
                var settings = new JsonSerializerSettings
                {
                    Converters = { new DrawActionConverter() }
                };
                var actions = JsonConvert.DeserializeObject<List<DrawActionBase>>(jsonArray, settings) ?? new();
                foreach (var a in actions)
                {
                    a.UserId = userId;
                    a.Id = Guid.NewGuid().ToString();
                }
                return actions;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Claude API error: {ex.Message}");
        }

        // Fallback to rule-based
        return EnhancedAiParser.Parse(prompt, userId);
    }

    private async Task SendToDrawServerAsync<T>(NetMessage<T> message) where T : IPayload
    {
        if (_drawServerStream == null) return;

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message.Serialize());
            await _drawServerStream.WriteAsync(data, 0, data.Length);
            await _drawServerStream.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Send to DrawServer error: {ex.Message}");
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listener.Stop();
    }
}
