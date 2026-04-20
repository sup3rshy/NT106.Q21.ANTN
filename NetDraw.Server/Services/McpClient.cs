using Anthropic.SDK;
using Microsoft.Extensions.AI;
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
    private const int MaxTokens = 4096;

    private static readonly JsonSerializerSettings ActionJsonSettings = new()
    {
        Converters = { new DrawActionConverter() }
    };

    private readonly string? _apiKey;
    private readonly string? _mcpProjectPath;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private ModelContextProtocol.Client.McpClient? _mcp;
    private IList<McpClientTool>? _tools;
    private IChatClient? _chat;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _ready;
    private bool _disposed;

    public McpClient(string? apiKey, string? mcpProjectPath, int canvasWidth = 1000, int canvasHeight = 700)
    {
        _apiKey = apiKey;
        _mcpProjectPath = mcpProjectPath;
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
                Console.WriteLine($"[MCP] Init failed: {ex.Message} — AI will use fallback parser.");
            }
        });
        return Task.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_ready) return;
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            Console.WriteLine("[MCP] No Claude API key — AI disabled, fallback parser will be used.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_mcpProjectPath) || !File.Exists(_mcpProjectPath))
        {
            Console.WriteLine($"[MCP] MCP server project not found at '{_mcpProjectPath}' — AI disabled.");
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_ready) return;

            Console.WriteLine("[MCP] Launching NetDraw.McpServer over stdio…");
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "NetDraw Drawing Tools",
                Command = "dotnet",
                Arguments = new[] { "run", "--no-build", "--project", _mcpProjectPath!, "--" }
            });

            _mcp = await ModelContextProtocol.Client.McpClient.CreateAsync(transport, cancellationToken: ct);
            _tools = await _mcp.ListToolsAsync(cancellationToken: ct);
            Console.WriteLine($"[MCP] Connected. {_tools.Count} tools: {string.Join(", ", _tools.Select(t => t.Name))}");

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
        Console.WriteLine($"[MCP] ▶ \"{Truncate(command, 80)}\"  room={roomId}");

        if (!_ready)
        {
            Console.WriteLine("[MCP] Not ready, attempting init…");
            try { await EnsureInitializedAsync(CancellationToken.None); }
            catch (Exception ex) { Console.WriteLine($"[MCP] Init error: {ex.Message}"); }
            if (!_ready) return null;
        }

        if (_chat == null || _tools == null) return null;

        string system =
$@"You are a drawing assistant for a {_canvasWidth}x{_canvasHeight} collaborative canvas (origin 0,0 top-left).
Realize the user's request by calling the provided drawing tools.
Keep every coordinate within the canvas bounds.
For complex scenes, issue multiple tool calls in back-to-front order (background first, foreground last).
Do not emit any prose after the drawing — tool calls are sufficient.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, command)
        };
        var options = new ChatOptions
        {
            ModelId = Model,
            MaxOutputTokens = MaxTokens,
            Tools = new List<AITool>(_tools)
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var response = await _chat.GetResponseAsync(messages, options, cts.Token);
            var actions = ExtractActions(response);
            Console.WriteLine($"[MCP] ← {actions.Count} action(s) extracted in {sw.ElapsedMilliseconds} ms");

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
            Console.WriteLine($"[MCP] ✘ Claude timeout after {sw.ElapsedMilliseconds} ms");
            return new AiResultPayload { Error = "Claude timeout (>90s)." };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MCP] ✘ Error after {sw.ElapsedMilliseconds} ms: {ex.Message}");
            return new AiResultPayload { Error = ex.Message };
        }
    }

    // ─── Transcript extraction ─────────────────────────────────────────────────

    /// <summary>
    /// Walk every tool result in the chat response and rebuild the
    /// <see cref="DrawActionBase"/> objects our renderer expects.
    /// </summary>
    private static List<DrawActionBase> ExtractActions(ChatResponse response)
    {
        var list = new List<DrawActionBase>();
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
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

    private static DrawActionBase? TryDeserialize(JToken payload)
    {
        try
        {
            return JsonConvert.DeserializeObject<DrawActionBase>(
                payload.ToString(Formatting.None), ActionJsonSettings);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[MCP] Failed to deserialize action: {ex.Message}");
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
