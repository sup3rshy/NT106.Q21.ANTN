using NetDraw.Server;
using NetDraw.Server.Ai;
using NetDraw.Server.Handlers;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5000;

// Claude API key: args[1] > env var
string? apiKey = (args.Length > 1 ? args[1] : null)
                 ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                 ?? Environment.GetEnvironmentVariable("CLAUDE_API_KEY");

// Locate NetDraw.McpServer.csproj by walking up from the server binary directory.
string? mcpProjectPath = ResolveMcpProjectPath();

// Services
var clientRegistry = new ClientRegistry();
var roomService = new RoomService();

int rateCapacity = ReadIntEnv("RATE_LIMIT_CAPACITY", 200, min: 1);
double rateRefill = ReadDoubleEnv("RATE_LIMIT_REFILL_PER_SEC", 50, min: 0.0001);
int maxAiPromptBytes = ReadIntEnv("MAX_AI_PROMPT_BYTES", 4096, min: 1);

var rateLimiter = new TokenBucketRateLimiter(capacity: rateCapacity, refillPerSec: rateRefill);
// Canvas dimensions must match the client's DrawCanvas (MainWindow.xaml) — currently 3000×2000.
// Claude uses these numbers to pick sensible (cx, cy, size) params; if they mismatch, every
// drawing lands in the wrong place on the real canvas.
var mcpClient = new McpClient(apiKey, mcpProjectPath, canvasWidth: 3000, canvasHeight: 2000);
var fallbackParser = new FallbackAiParser();

// Start MCP connection in background
_ = Task.Run(async () =>
{
    try { await mcpClient.ConnectAsync(); }
    catch (Exception ex) { Console.WriteLine($"[MCP] Background connect failed: {ex.Message}"); }
});

// Pipeline
var dispatcher = new MessageDispatcher(rateLimiter);
dispatcher.Register(new RoomHandler(roomService, clientRegistry));
dispatcher.Register(new DrawHandler(roomService));
dispatcher.Register(new ObjectHandler(roomService));
dispatcher.Register(new PresenceHandler(roomService));
dispatcher.Register(new ChatHandler(roomService));
dispatcher.Register(new AiHandler(roomService, mcpClient, fallbackParser, maxPromptBytes: maxAiPromptBytes));

// Start server
var server = new DrawServer(port, dispatcher, clientRegistry, roomService, rateLimiter);
Console.WriteLine($"[NetDraw Server] Starting on port {port}...");
Console.WriteLine($"[NetDraw Server] Claude API key: {(string.IsNullOrWhiteSpace(apiKey) ? "(none — fallback parser only)" : "present")}");
Console.WriteLine($"[NetDraw Server] MCP project:    {mcpProjectPath ?? "(not found)"}");
await server.StartAsync();

static int ReadIntEnv(string name, int @default, int min)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw)) return @default;
    if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min)
        return v;
    Console.Error.WriteLine($"[config] Invalid {name}=\"{raw}\" (need int >= {min}); using default {@default}");
    return @default;
}

static double ReadDoubleEnv(string name, double @default, double min)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw)) return @default;
    if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min)
        return v;
    Console.Error.WriteLine($"[config] Invalid {name}=\"{raw}\" (need number >= {min}); using default {@default}");
    return @default;
}

static string? ResolveMcpProjectPath()
{
    // Search up from AppContext.BaseDirectory for NetDraw.McpServer/NetDraw.McpServer.csproj.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "NetDraw.McpServer", "NetDraw.McpServer.csproj");
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
    }
    return null;
}
