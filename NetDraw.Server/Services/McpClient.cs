using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Services;

/// <summary>
/// MCP-SDK-based implementation of <see cref="IMcpClient"/>.
///
/// This replaces the old custom TCP link. The new pipeline is:
///   1. On <see cref="ConnectAsync"/>, we launch NetDraw.McpServer as a child process
///      over stdio and speak the real Model Context Protocol (JSON-RPC 2.0).
///   2. Tool schemas are fetched via <c>tools/list</c>.
///   3. On <see cref="SendCommandAsync"/>, we send the user prompt to Claude with those
///      tools attached. <see cref="Microsoft.Extensions.AI"/>'s function-invocation
///      middleware forwards each <c>tool_use</c> to the MCP server and feeds the result
///      back to Claude automatically.
///   4. We walk the final transcript, extract every <see cref="FunctionResultContent"/>,
///      deserialize each result as a <see cref="DrawActionBase"/> (polymorphic via
///      <see cref="DrawActionConverter"/>), and return them as an <see cref="AiResultPayload"/>.
///
/// The public interface is unchanged — <see cref="AiHandler"/> and <see cref="Program"/>
/// call the same three members as before.
/// </summary>
public class McpClient : IMcpClient, IAsyncDisposable
{
    private const string Model = "claude-sonnet-4-5-20250929";
    // Headroom for complex scenes: a manga/character drawing easily produces 30-80 tool calls,
    // each with its own JSON payload in the transcript. 16k leaves room for thinking + results.
    private const int MaxTokens = 16384;

    private static readonly JsonSerializerSettings ActionJsonSettings = new()
    {
        Converters = { new DrawActionConverter() }
    };

    private readonly string? _apiKey;
    private readonly string? _mcpProjectPath;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly ILogger<McpClient> _logger;

    private ModelContextProtocol.Client.McpClient? _mcp;
    private IList<McpClientTool>? _tools;
    private IChatClient? _chat;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _ready;
    private bool _disposed;

    public McpClient(string? apiKey, string? mcpProjectPath, ILogger<McpClient> logger, int canvasWidth = 1000, int canvasHeight = 700)
    {
        _apiKey = apiKey;
        _mcpProjectPath = mcpProjectPath;
        _logger = logger;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
    }

    public bool IsConnected => _ready;

    // ─── Lifecycle ─────────────────────────────────────────────────────────────

    public Task ConnectAsync()
    {
        // Fire-and-forget: initialize in the background so startup is not blocked
        // on the child-process spawn and the first tools/list round-trip.
        _ = Task.Run(async () =>
        {
            try { await EnsureInitializedAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP init failed — AI will use fallback parser");
            }
        });
        return Task.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_ready) return;
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogInformation("No Claude API key — AI disabled, fallback parser will be used");
            return;
        }
        if (string.IsNullOrWhiteSpace(_mcpProjectPath) || !File.Exists(_mcpProjectPath))
        {
            _logger.LogWarning("MCP server project not found at '{Path}' — AI disabled", _mcpProjectPath);
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_ready) return;

            _logger.LogInformation("Launching NetDraw.McpServer over stdio");
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "NetDraw Drawing Tools",
                Command = "dotnet",
                Arguments = new[] { "run", "--no-build", "--project", _mcpProjectPath!, "--" }
            });

            _mcp = await ModelContextProtocol.Client.McpClient.CreateAsync(transport, cancellationToken: ct);
            _tools = await _mcp.ListToolsAsync(cancellationToken: ct);
            _logger.LogInformation("MCP connected with {ToolCount} tools: {Tools}",
                _tools.Count, string.Join(", ", _tools.Select(t => t.Name)));

            var anthropic = new AnthropicClient(_apiKey!);
            _chat = anthropic.Messages
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();

            _ready = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ─── Request / Response ────────────────────────────────────────────────────

    public async Task<AiResultPayload?> SendCommandAsync(string command, string roomId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("MCP request in {RoomId}: {Command}", roomId, LogHelper.SanitizeForLog(command, 80));

        if (!_ready)
        {
            _logger.LogInformation("MCP not ready, attempting init");
            try { await EnsureInitializedAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogError(ex, "MCP init error"); }
            if (!_ready) return null;
        }

        if (_chat == null || _tools == null) return null;

        string system =
$@"You are an expert drawing assistant for a {_canvasWidth}×{_canvasHeight} collaborative canvas.

━━━ RULE #0 — USE COMPOSITE PREFABS. DO NOT SUPPLEMENT THEM. ━━━
For these subjects, a SINGLE composite call draws the entire subject, COMPLETE, in correct proportion.
Characters / animals (front-view face / silhouette):
  • cat / mèo            → draw_cat_face       (head, ears, eyes, nose, mouth, whiskers — done)
  • dog / chó            → draw_dog_face       (head, ears, eyes, snout, nose, mouth)
  • manga/anime person   → draw_manga_face     (silhouette, eyes, hair, mouth — done)
  • stick person / người → draw_stick_person   (head + body + arms + legs, pose param)
  • bird / chim          → draw_bird           (body + wing + tail + beak + eye)
  • fish / cá            → draw_fish           (body + fins + eye)
  • butterfly / bướm     → draw_butterfly      (4 wings + body + antennae)
Scenery:
  • tree / cây           → draw_tree
  • house / nhà          → draw_house
  • sun / mặt trời       → draw_sun
  • mountain / núi       → draw_mountain       (peaks=N for a range)
  • cloud / mây          → draw_cloud
  • moon / trăng         → draw_moon           (phase: full|gibbous|half|crescent)
Icons / stickers (filled, single-call):
  • heart / tim          → draw_heart
  • flower / hoa         → draw_flower
  • leaf / lá            → draw_leaf
  • lightning / chớp     → draw_lightning
  • raindrop / giọt mưa  → draw_raindrop
  • arrow                → draw_filled_arrow
  • speech bubble        → draw_speech_bubble  (with optional text)

HARD RULES:
  1. If the request names ONE of these subjects, your FIRST and ONLY response must be that composite call. Do NOT follow it with draw_triangle/draw_circle/draw_line to ""add"" ears/eyes/whiskers — those parts are already drawn by the composite. Adding them duplicates + misaligns the drawing (this has failed before).
  2. If you want a different expression/style, use the composite's parameters (mood, hairStyle, gender, furColor, …) — NEVER supplement with primitives.
  3. The only time to mix composites with primitives is for a MULTI-subject scene (e.g. ""cat sitting in front of a house""), and even then use draw_many to batch.
  4. No prose, no chain-of-thought tool calls — one decisive call per subject.

EXAMPLES:
  • ""vẽ con mèo""                    → draw_cat_face(cx=500, cy=350, size=400)                       [1 call, done]
  • ""vẽ con mèo buồn ngủ""           → draw_cat_face(cx=500, cy=350, size=400, mood=""sleepy"")       [1 call, done]
  • ""vẽ cô gái anime tóc dài""      → draw_manga_face(cx=500, cy=350, size=450, hairStyle=""long"")  [1 call, done]
  • ""vẽ mèo đứng trước nhà""         → draw_many([ draw_house(…), draw_cat_face(…) ])                [1 batch call]
  • ""vẽ hình tròn đỏ""               → draw_circle(…)                                                 [primitive is correct here]

━━━ RULE #1 — BATCH WITH draw_many ━━━
For any scene with ≥3 elements, use draw_many to submit the whole plan in ONE call.
Each item is {{""tool"": ""<tool name>"", ""args"": {{...}}}}. You may reference composites inside draw_many.
Example for a scene: draw_many([
  {{""tool"": ""draw_rectangle"", ""args"": {{""x"":0,""y"":0,""width"":{_canvasWidth},""height"":{_canvasHeight / 2},""color"":""#87CEEB"",""fillColor"":""#87CEEB""}}}},  // sky
  {{""tool"": ""draw_sun"", ""args"": {{""cx"":850,""cy"":120,""radius"":55}}}},
  {{""tool"": ""draw_tree"", ""args"": {{""baseX"":200,""baseY"":600,""height"":220}}}},
  {{""tool"": ""draw_house"", ""args"": {{""baseX"":520,""baseY"":600,""width"":260,""height"":220}}}},
  {{""tool"": ""draw_cat_face"", ""args"": {{""cx"":380,""cy"":480,""size"":120,""mood"":""happy""}}}}
])

━━━ RULE #2 — ANCHOR BEFORE YOU DRAW ━━━
If you must hand-draw without a composite, FIRST pick an anchor point (cx, cy) for the subject
and a size. Then express every feature as an offset from (cx, cy) in units of size. Never
guess an absolute number.

Canvas reference: origin (0,0) TOP-LEFT, +x right, +y down. Center is ({_canvasWidth / 2}, {_canvasHeight / 2}).
For a single subject, typical head center = ({_canvasWidth / 2}, {_canvasHeight * 2 / 5}) with head size ~= {_canvasHeight / 3}px.

━━━ RULE #3 — PICK THE RIGHT CURVE/FILL TOOL ━━━
draw_path makes a POLYLINE with straight segments — looks jagged for organic shapes.
  • Smooth organic outline (body, tail, hair) → draw_smooth_curve  (Catmull-Rom through control points)
  • Single arc (smile, eyebrow, eyelid)       → draw_arc / draw_ellipse_arc
  • S-curve (hair strand, flowing tail)        → draw_cubic_bezier
  • Symmetric features (eyes/ears/wings)       → draw_mirrored_path
  • FILLED arbitrary polygon (custom blob)     → draw_filled_polygon
  • FILLED smooth blob (organic body)          → draw_filled_smooth_shape
  • Need to rotate/scale a sub-scene           → draw_transformed (wraps a batch with affine transform)
  • Need a coordinated palette                 → make_palette (returns 5 hex codes)
Only use draw_path for genuinely angular shapes (zigzag, polygon you didn't want closed).

━━━ RULE #4 — STYLE ━━━
• Manga/anime look: penStyle=""Calligraphy"" on outlines/hair, strokeWidth 3–5 for silhouette, 2 for inner details, 1 for fine lines.
• Back-to-front order: sky → background → subject body → subject face/details → text/speech.
• Stick to a tight palette (3–6 hex codes). Composites already pick good defaults.
• Opacity 0.3–0.5 + dashStyle=""Dotted"" for construction/shadow hatching.
• Use groupId to bundle strokes of one entity (one face, one bubble, one house).

━━━ OUTPUT RULES ━━━
Emit only tool calls. No prose, no apology, no summary. For ""draw a cat"", one call to draw_cat_face is
enough — don't over-engineer. For ""draw a manga scene"", use draw_many with composite + scenery calls.

Canvas: {_canvasWidth} wide × {_canvasHeight} tall. Origin top-left. +x right, +y down.";

        // KEYWORD ROUTER — if the request is for a single known subject, expose ONLY
        // the matching composite tool. Claude cannot call primitives that aren't in the
        // tool list, so over-drawing (adding extra ears / eyes after the composite) is
        // structurally impossible. This is the drawio-mcp insight: constrain the tool
        // surface, don't rely on the prompt.
        var (restrictedTools, routedHint) = RouteByKeyword(command, _tools);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system + routedHint),
            new(ChatRole.User, command)
        };
        var options = new ChatOptions
        {
            ModelId = Model,
            MaxOutputTokens = MaxTokens,
            Tools = new List<AITool>(restrictedTools),
            // Force Claude to emit at least one tool call (no prose-only responses).
            ToolMode = ChatToolMode.RequireAny
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var response = await _chat.GetResponseAsync(messages, options, cts.Token);
            var actions = ExtractActions(response);
            _logger.LogInformation("MCP extracted {ActionCount} action(s) in {ElapsedMs} ms",
                actions.Count, sw.ElapsedMilliseconds);

            if (actions.Count == 0)
            {
                return new AiResultPayload
                {
                    Error = "Claude không gọi công cụ vẽ nào — thử viết lệnh cụ thể hơn."
                };
            }

            return new AiResultPayload { Actions = actions };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Claude timeout after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return new AiResultPayload { Error = "Claude timeout (>90s)." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP error after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return new AiResultPayload { Error = ex.Message };
        }
    }

    // ─── Keyword router ────────────────────────────────────────────────────────

    /// <summary>
    /// Subject keywords → the ONE composite tool that should be exposed. When a user
    /// asks for a single known subject, we remove every other tool from Claude's
    /// toolset so it cannot hand-draw duplicate parts. The order matters: longer /
    /// more specific keywords first.
    /// </summary>
    private static readonly (string[] keywords, string toolName, string hint)[] Routes = new[]
    {
        (new[] { "anime girl", "manga girl", "anime boy", "manga character", "nhân vật manga", "cô gái anime", "chàng trai anime", "manga", "anime", "nhân vật" },
         "draw_manga_face",
         "The user asked for a single anime/manga character. Call draw_manga_face ONCE with sensible params. Nothing else."),

        (new[] { "con mèo", "chú mèo", "kitten", " cat", "mèo" },
         "draw_cat_face",
         "The user asked for a cat. Your ONLY valid response is a single call to draw_cat_face with sensible cx/cy/size for the canvas center. No other tool calls are available."),

        (new[] { "con chó", "chú chó", "puppy", " dog", "chó" },
         "draw_dog_face",
         "The user asked for a dog. Call draw_dog_face ONCE with sensible cx/cy/size. Nothing else."),

        (new[] { "con chim", "chim chóc", " bird", "chim" },
         "draw_bird",
         "The user asked for a bird. Call draw_bird ONCE."),

        (new[] { "con cá", " fish", " cá" },
         "draw_fish",
         "The user asked for a fish. Call draw_fish ONCE."),

        (new[] { "con bướm", "butterfly", "bướm" },
         "draw_butterfly",
         "The user asked for a butterfly. Call draw_butterfly ONCE."),

        (new[] { "ô tô", "xe hơi", "chiếc xe", " car", " xe" },
         "draw_car",
         "The user asked for a car. Call draw_car ONCE."),

        (new[] { "ngọn núi", "dãy núi", "mountain", "núi" },
         "draw_mountain",
         "The user asked for a mountain. Call draw_mountain ONCE (use peaks=2 or 3 for a range)."),

        (new[] { "trái tim", "quả tim", " heart", "tim" },
         "draw_heart",
         "The user asked for a heart. Call draw_heart ONCE."),

        (new[] { "bông hoa", "đóa hoa", "flower", " hoa" },
         "draw_flower",
         "The user asked for a flower. Call draw_flower ONCE."),

        (new[] { "đám mây", " cloud", "mây" },
         "draw_cloud",
         "The user asked for a cloud. Call draw_cloud ONCE."),

        (new[] { "mặt trăng", "trăng lưỡi liềm", "moon", "trăng" },
         "draw_moon",
         "The user asked for a moon. Call draw_moon ONCE (default phase=crescent)."),

        (new[] { "tia chớp", "tia sét", "lightning", "sét", "chớp" },
         "draw_lightning",
         "The user asked for a lightning bolt. Call draw_lightning ONCE."),

        (new[] { "chiếc lá", "lá cây", " leaf", " lá " },
         "draw_leaf",
         "The user asked for a leaf. Call draw_leaf ONCE."),

        (new[] { "giọt nước", "giọt mưa", "raindrop", "teardrop" },
         "draw_raindrop",
         "The user asked for a raindrop. Call draw_raindrop ONCE."),

        (new[] { "que diêm", "stick figure", "stick person", "người que" },
         "draw_stick_person",
         "The user asked for a stick person. Call draw_stick_person ONCE."),

        (new[] { "speech bubble", "lời thoại", "bong bóng" },
         "draw_speech_bubble",
         "The user asked for a speech bubble. Call draw_speech_bubble ONCE."),

        (new[] { "ngôi nhà", "căn nhà", " nhà", "house" },
         "draw_house",
         "The user asked for a house. Call draw_house ONCE."),

        (new[] { "cái cây", " cây", "tree" },
         "draw_tree",
         "The user asked for a tree. Call draw_tree ONCE."),

        (new[] { "mặt trời", "sun" },
         "draw_sun",
         "The user asked for a sun. Call draw_sun ONCE."),
    };

    private (IList<McpClientTool> tools, string hint) RouteByKeyword(string command, IList<McpClientTool> allTools)
    {
        string lc = " " + command.ToLowerInvariant() + " ";
        foreach (var (kws, toolName, hint) in Routes)
        {
            if (!kws.Any(k => lc.Contains(k))) continue;
            var picked = allTools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (picked == null) continue;
            // Multi-subject guard: if the request mentions several subjects ("mèo và nhà"),
            // fall back to the full tool set with draw_many — keyword routing would hurt there.
            int matchedSubjects = Routes.Count(r => r.keywords.Any(k => lc.Contains(k)));
            if (matchedSubjects > 1) break;
            _logger.LogDebug("MCP routed: exposing only '{Tool}' (keyword match)", toolName);
            return (new[] { picked }, "\n\n━━━ ROUTED ━━━\n" + hint);
        }
        return (allTools, string.Empty);
    }

    // ─── Transcript extraction ─────────────────────────────────────────────────

    /// <summary>
    /// Walk every tool result in the chat response and rebuild the
    /// <see cref="DrawActionBase"/> objects our renderer expects.
    /// </summary>
    private List<DrawActionBase> ExtractActions(ChatResponse response)
    {
        var list = new List<DrawActionBase>();
        var toolCallCounts = new Dictionary<string, int>();
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                // Count which tools Claude actually invoked — composite prefabs vs raw primitives
                if (content is FunctionCallContent fc)
                {
                    toolCallCounts.TryGetValue(fc.Name, out var c);
                    toolCallCounts[fc.Name] = c + 1;
                    if (fc.Arguments != null && _logger.IsEnabled(LogLevel.Trace))
                    {
                        var argStr = string.Join(", ", fc.Arguments.Select(kv =>
                            $"{kv.Key}={LogHelper.SanitizeForLog(kv.Value?.ToString(), 120)}"));
                        _logger.LogTrace("MCP tool call: {Tool}({Args})", fc.Name, argStr);
                    }
                }
                if (content is not FunctionResultContent fr) continue;
                string? text = fr.Result switch
                {
                    string s => s,
                    null => null,
                    _ => fr.Result.ToString()
                };
                if (string.IsNullOrWhiteSpace(text)) continue;

                foreach (var payload in UnwrapTextBlocks(TryParse(text!)))
                {
                    var a = TryDeserialize(payload);
                    if (a != null) list.Add(a);
                }
            }
        }
        if (toolCallCounts.Count > 0)
        {
            var summary = string.Join(", ", toolCallCounts.Select(kv => $"{kv.Key}×{kv.Value}"));
            _logger.LogInformation("MCP tools used: {Summary}", summary);
        }
        return list;
    }

    private static JToken? TryParse(string text)
    {
        try { return JToken.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JToken> UnwrapTextBlocks(JToken? token)
    {
        if (token == null) yield break;

        // Case 1: full MCP CallToolResult shape with {"content":[{"type":"text","text":"{...}"}]}
        if (token is JObject obj && obj["content"] is JArray blocks)
        {
            foreach (var block in blocks)
            {
                if ((string?)block["type"] == "text" &&
                    block["text"] is JValue tv && tv.Value is string s)
                {
                    var inner = TryParse(s);
                    if (inner != null)
                        foreach (var p in UnwrapTextBlocks(inner)) yield return p;
                }
            }
            yield break;
        }
        // Case 2: array of actions
        if (token.Type == JTokenType.Array)
        {
            foreach (var item in (JArray)token)
                if (item.Type == JTokenType.Object) yield return item;
            yield break;
        }
        // Case 3: single action object
        if (token.Type == JTokenType.Object) yield return token;
    }

    private DrawActionBase? TryDeserialize(JToken payload)
    {
        try
        {
            return JsonConvert.DeserializeObject<DrawActionBase>(
                payload.ToString(Formatting.None), ActionJsonSettings);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MCP failed to deserialize action");
            return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ─── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_chat is IDisposable cd) cd.Dispose();
        if (_mcp is IAsyncDisposable ad)
        {
            try { await ad.DisposeAsync(); } catch { }
        }
        _initLock.Dispose();
    }
}
