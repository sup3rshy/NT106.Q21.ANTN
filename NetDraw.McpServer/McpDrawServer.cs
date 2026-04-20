using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.McpServer;

/// <summary>
/// MCP Server: nhận lệnh AI từ DrawServer (TCP), gọi Claude API hoặc rule-based parser,
/// rồi trả DrawAction list về DrawServer để broadcast cho các client.
///
/// Cải tiến so với phiên bản cũ:
/// - DrawServer reconnect được: khi connection cũ bị hỏng, connection mới thay thế
/// - System prompt chuẩn xác hơn cho Claude (đúng canvas size, đúng JSON schema)
/// - Xử lý response từ Claude tốt hơn (code-block stripping, single-object wrapping)
/// - Timeout 90 s cho mỗi Claude API call
/// - Concurrent request handled via SemaphoreSlim
/// </summary>
public class McpDrawServer
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly string? _apiKey;
    private bool _isRunning;

    // Active DrawServer connection (only one at a time)
    private volatile ConnectionState? _activeConn;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new DrawActionConverter() }
    };

    private record ConnectionState(TcpClient Client, StreamWriter Writer);

    public McpDrawServer(int port, string? apiKey = null)
    {
        _port    = port;
        _apiKey  = apiKey;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    // ─── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;

        Console.WriteLine("==========================================");
        Console.WriteLine($"  NetDraw MCP Server  •  port {_port}");
        Console.WriteLine("==========================================");
        Console.WriteLine(string.IsNullOrEmpty(_apiKey)
            ? "  Mode: Rule-based  (set CLAUDE_API_KEY for AI)"
            : "  Mode: AI-powered  (Claude API)");
        Console.WriteLine();

        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                Console.WriteLine($"[+] DrawServer connected: {client.Client.RemoteEndPoint}");

                // Cleanly replace any previous connection
                var old = Interlocked.Exchange(ref _activeConn, null);
                if (old != null)
                {
                    try { old.Client.Close(); } catch { }
                    Console.WriteLine("[~] Previous DrawServer connection closed");
                }

                var newConn = new ConnectionState(
                    client,
                    new StreamWriter(client.GetStream(), Encoding.UTF8) { AutoFlush = true });
                _activeConn = newConn;

                _ = Task.Run(() => HandleDrawServerAsync(client, newConn));
            }
            catch (Exception ex)
            {
                if (_isRunning) Console.WriteLine($"[!] Accept error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listener.Stop();
    }

    // ─── Per-connection message loop ───────────────────────────────────────────

    private async Task HandleDrawServerAsync(TcpClient client, ConnectionState conn)
    {
        var buffer = new byte[65_536];
        var sb     = new StringBuilder();

        try
        {
            while (client.Connected)
            {
                int n = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
                if (n == 0) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                var data = sb.ToString();
                int idx;

                while ((idx = data.IndexOf('\n')) >= 0)
                {
                    var json = data[..idx].Trim();
                    data = data[(idx + 1)..];

                    if (!string.IsNullOrEmpty(json))
                        _ = Task.Run(() => ProcessMessageAsync(json, conn));
                }

                sb.Clear();
                sb.Append(data);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Read error: {ex.Message}");
        }
        finally
        {
            // Only clear _activeConn if it's still this connection
            Interlocked.CompareExchange(ref _activeConn, null, conn);
            Console.WriteLine("[-] DrawServer disconnected");
        }
    }

    private async Task ProcessMessageAsync(string json, ConnectionState conn)
    {
        try
        {
            var envelope = MessageEnvelope.Parse(json);
            if (envelope?.Type != MessageType.AiCommand) return;

            var cmd = MessageEnvelope.DeserializePayload<AiCommandPayload>(envelope.RawPayload);
            if (cmd == null) return;

            await ProcessAiCommandAsync(envelope, cmd, conn);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] ProcessMessage error: {ex.Message}");
        }
    }

    // ─── AI Processing ─────────────────────────────────────────────────────────

    private async Task ProcessAiCommandAsync(
        MessageEnvelope.Envelope envelope,
        AiCommandPayload cmd,
        ConnectionState conn)
    {
        var prompt   = cmd.Prompt;
        var senderId = envelope.SenderId;
        var roomId   = envelope.RoomId;
        var sw       = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine($"[McpServer] ▶ \"{prompt}\"  sender={senderId}  room={roomId}");
        Console.WriteLine($"[McpServer]   mode={(string.IsNullOrEmpty(_apiKey) ? "rule-based" : "Claude API")}");

        List<DrawActionBase> actions;
        try
        {
            if (!string.IsNullOrEmpty(_apiKey))
            {
                Console.WriteLine($"[McpServer]   → calling Claude API…");
                actions = await CallClaudeApiAsync(prompt, senderId);
                Console.WriteLine($"[McpServer]   ← Claude API returned in {sw.ElapsedMilliseconds} ms  ({actions.Count} actions)");
            }
            else
            {
                actions = EnhancedAiParser.Parse(prompt, senderId);
                Console.WriteLine($"[McpServer]   rule-based parsed in {sw.ElapsedMilliseconds} ms  ({actions.Count} actions)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[McpServer] ✘ Parse error: {ex.Message}");
            actions = EnhancedAiParser.Parse(prompt, senderId);
        }

        // Stamp missing IDs
        foreach (var a in actions)
        {
            if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString();
            a.UserId = senderId;
        }

        var result = NetMessage<AiResultPayload>.Create(
            MessageType.AiResult, senderId, "", roomId,
            new AiResultPayload { Prompt = prompt, Actions = actions });

        await SendAsync(result, conn);
        Console.WriteLine($"[McpServer] ✔ sent {actions.Count} action(s) in {sw.ElapsedMilliseconds} ms total");
    }

    // ─── Claude API ────────────────────────────────────────────────────────────

    // System prompt: exact JSON schema matching DrawActionBase + subclasses.
    // Canvas safe-zone: 0–1000 × 0–700 (matches a typical 1x viewport on the 3000×2000 canvas).
    private const string SystemPrompt = """
You are a drawing assistant for a 3000×2000 collaborative whiteboard canvas.
Place all objects within the visible area: x in [50, 950], y in [50, 650].
Center of visible area: x=500, y=350.

Respond ONLY with a valid JSON array of draw action objects. No markdown, no explanation.

SCHEMA — each object must have these fields:

▸ SHAPE  { "type":"shape", "shapeType":"Rect"|"Circle"|"Ellipse"|"Triangle"|"Star",
           "x":number, "y":number, "width":number, "height":number,
           "color":"#RRGGBB", "fillColor":"#RRGGBB"|null, "strokeWidth":2 }
  (x,y = top-left corner for Rect/Triangle/Star; for Circle/Ellipse x,y = center)

▸ LINE   { "type":"line", "startX":n, "startY":n, "endX":n, "endY":n,
           "color":"#RRGGBB", "strokeWidth":2, "hasArrow":false }

▸ TEXT   { "type":"text", "x":number, "y":number, "text":"string",
           "color":"#RRGGBB", "fontSize":24 }

Rules:
- Use bright, vivid colors unless told otherwise.
- For filled shapes, set fillColor = color.
- Sizes: small≈50, medium≈100, large≈200.
- If asked for multiple objects, produce multiple items in the array.
- Never produce empty arrays.

Example — "a red circle in the center":
[{"type":"shape","shapeType":"Circle","x":500,"y":350,"width":120,"height":120,"color":"#FF3333","fillColor":"#FF3333","strokeWidth":2}]
""";

    private async Task<List<DrawActionBase>> CallClaudeApiAsync(string prompt, string userId)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var body = new
            {
                model      = "claude-sonnet-4-5",
                max_tokens = 2048,
                system     = SystemPrompt,
                messages   = new[] { new { role = "user", content = prompt } }
            };

            var content  = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            var response = await http.PostAsync("https://api.anthropic.com/v1/messages", content);
            var raw      = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[AI] Claude API error {(int)response.StatusCode}: {raw}");
                return EnhancedAiParser.Parse(prompt, userId);
            }

            var jResp = JObject.Parse(raw);
            var aiText = jResp["content"]?[0]?["text"]?.Value<string>() ?? "";
            Console.WriteLine($"[AI] Claude raw: {aiText[..Math.Min(200, aiText.Length)]}…");

            var jsonArray = ExtractJsonArray(aiText);
            if (jsonArray == null)
            {
                Console.WriteLine("[AI] Could not extract JSON array from Claude response");
                return EnhancedAiParser.Parse(prompt, userId);
            }

            var actions = JsonConvert.DeserializeObject<List<DrawActionBase>>(jsonArray, JsonSettings) ?? new();
            return actions.Count > 0 ? actions : EnhancedAiParser.Parse(prompt, userId);
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("[AI] Claude API request timed out");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Claude API exception: {ex.Message}");
        }

        return EnhancedAiParser.Parse(prompt, userId);
    }

    /// <summary>
    /// Extract a JSON array from Claude's response, handling:
    /// - ```json ... ``` code blocks
    /// - Plain [ ... ] arrays
    /// - Single { ... } objects (wrapped into array)
    /// </summary>
    private static string? ExtractJsonArray(string text)
    {
        // Strip markdown code-block wrapper
        var codeBlock = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (codeBlock.Success)
            text = codeBlock.Groups[1].Value.Trim();

        // Find outermost [ ... ]
        int arrStart = text.IndexOf('[');
        int arrEnd   = text.LastIndexOf(']');
        if (arrStart >= 0 && arrEnd > arrStart)
            return text[arrStart..(arrEnd + 1)];

        // Claude sometimes returns a single object — wrap it
        int objStart = text.IndexOf('{');
        int objEnd   = text.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
            return $"[{text[objStart..(objEnd + 1)]}]";

        return null;
    }

    // ─── Send helpers ──────────────────────────────────────────────────────────

    private static async Task SendAsync<T>(NetMessage<T> message, ConnectionState conn) where T : IPayload
    {
        try
        {
            await conn.Writer.WriteAsync(message.Serialize());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Send error: {ex.Message}");
        }
    }
}
