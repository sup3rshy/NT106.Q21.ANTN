using Microsoft.Extensions.Logging;
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

// LOG_LEVEL must be a named LogLevel (e.g. "Warning"). Numeric strings parse but produce unmapped
// values that silently disable all logging.
var rawLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
LogLevel minLevel = LogLevel.Information;
if (!string.IsNullOrWhiteSpace(rawLevel))
{
    if (Enum.TryParse<LogLevel>(rawLevel, ignoreCase: true, out var lvl) && Enum.IsDefined(typeof(LogLevel), lvl))
        minLevel = lvl;
    else
        Console.Error.WriteLine($"[Startup] LOG_LEVEL={LogHelper.SanitizeForLog(rawLevel, 40)} is not a valid LogLevel name; defaulting to Information.");
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(minLevel);
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.IncludeScopes = false;
        o.TimestampFormat = "HH:mm:ss.fff ";
    });
});

var startupLogger = loggerFactory.CreateLogger("NetDraw.Startup");

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
var mcpClient = new McpClient(apiKey, mcpProjectPath, loggerFactory.CreateLogger<McpClient>(), canvasWidth: 3000, canvasHeight: 2000);
var fallbackParser = new FallbackAiParser();

// Start MCP connection in background
_ = Task.Run(async () =>
{
    try { await mcpClient.ConnectAsync(); }
    catch (Exception ex) { startupLogger.LogError(ex, "MCP background connect failed"); }
});

// Pipeline
var dispatcher = new MessageDispatcher(rateLimiter, loggerFactory.CreateLogger<MessageDispatcher>());
dispatcher.Register(new RoomHandler(roomService, clientRegistry));
dispatcher.Register(new DrawHandler(roomService));
dispatcher.Register(new ObjectHandler(roomService));
dispatcher.Register(new PresenceHandler(roomService));
dispatcher.Register(new ChatHandler(roomService));
dispatcher.Register(new AiHandler(roomService, mcpClient, fallbackParser, loggerFactory.CreateLogger<AiHandler>(), maxPromptBytes: maxAiPromptBytes));

// Health endpoint for the load balancer; port via HEALTH_PORT to avoid changing the positional CLI.
int healthPort = int.TryParse(Environment.GetEnvironmentVariable("HEALTH_PORT"), out var hp) ? hp : 5050;
var healthServer = new HttpHealthServer(healthPort, roomService, clientRegistry);
var healthCts = new CancellationTokenSource();
_ = Task.Run(() => healthServer.RunAsync(healthCts.Token));

// Start server
var server = new DrawServer(port, dispatcher, clientRegistry, roomService, rateLimiter, loggerFactory);
startupLogger.LogInformation("Starting on port {Port}", port);
startupLogger.LogInformation("Health endpoint: http://+:{HealthPort}/health", healthPort);
startupLogger.LogInformation("Claude API key: {KeyStatus}", string.IsNullOrWhiteSpace(apiKey) ? "(none — fallback parser only)" : "present");
startupLogger.LogInformation("MCP project: {McpProjectPath}", mcpProjectPath ?? "(not found)");
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
