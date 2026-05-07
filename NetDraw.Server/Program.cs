using Microsoft.Extensions.Logging;
using NetDraw.Server;
using NetDraw.Server.Ai;
using NetDraw.Server.Handlers;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5000;

// Claude API key: env var only.
//
// Previously we accepted args[1] as an alternative source. That was unsafe: on Linux/macOS
// `ps aux` exposes the full argv to every other user on the host, and on Windows the
// argv shows in Process Explorer / WMI. Dropping the argv form entirely; users must use
// ANTHROPIC_API_KEY (or the legacy CLAUDE_API_KEY).
if (args.Length > 1)
    Console.Error.WriteLine("[Startup] Warning: extra positional args are ignored. " +
                            "Set ANTHROPIC_API_KEY in the environment instead of passing it on the command line.");
string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
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
int graceSeconds = ReadIntEnv("SESSION_RESUME_GRACE_SECONDS", 30, min: 1, max: 600);
var sessionTokenStore = new SessionTokenStore(TimeSpan.FromSeconds(graceSeconds));

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
var dispatcher = new MessageDispatcher(rateLimiter, roomService, loggerFactory.CreateLogger<MessageDispatcher>());
dispatcher.Register(new RoomHandler(roomService, clientRegistry, sessionTokenStore));
dispatcher.Register(new DrawHandler(roomService));
dispatcher.Register(new ObjectHandler(roomService));
dispatcher.Register(new PresenceHandler(roomService));
dispatcher.Register(new ChatHandler(roomService, loggerFactory.CreateLogger<ChatHandler>()));
dispatcher.Register(new AiHandler(roomService, mcpClient, fallbackParser, loggerFactory.CreateLogger<AiHandler>(), maxPromptBytes: maxAiPromptBytes));

// Health endpoint for the load balancer; port via HEALTH_PORT to avoid changing the positional CLI.
int healthPort = ReadIntEnv("HEALTH_PORT", 5050, min: 1, max: 65535);
var healthServer = new HttpHealthServer(healthPort, roomService, loggerFactory.CreateLogger<HttpHealthServer>());
var healthCts = new CancellationTokenSource();
AppDomain.CurrentDomain.ProcessExit += (_, _) => { healthCts.Cancel(); healthCts.Dispose(); };
_ = Task.Run(() => healthServer.RunAsync(healthCts.Token));

// LAN discovery beacon — opt-out via LAN_DISCOVERY_DISABLE=1. Multicast group/port/interval
// override mirrors LOG_LEVEL: env vars only, no positional CLI shift.
CancellationTokenSource? beaconCts = null;
if (Environment.GetEnvironmentVariable("LAN_DISCOVERY_DISABLE") == "1")
{
    startupLogger.LogInformation("LAN discovery beacon: disabled (LAN_DISCOVERY_DISABLE=1)");
}
else
{
    var beaconGroup = Environment.GetEnvironmentVariable("LAN_BEACON_GROUP") ?? "239.255.77.12";
    var beaconPort = ReadIntEnv("LAN_BEACON_PORT", 5099, min: 1, max: 65535);
    var beaconIntervalSec = ReadIntEnv("LAN_BEACON_INTERVAL_SEC", 2, min: 1, max: 3600);
    var beaconName = Environment.GetEnvironmentVariable("BEACON_NAME");
    var beacon = new BeaconService(roomService, port,
        loggerFactory.CreateLogger<BeaconService>(),
        group: beaconGroup,
        port: beaconPort,
        intervalMs: beaconIntervalSec * 1000,
        name: beaconName);
    beaconCts = new CancellationTokenSource();
    var capturedCts = beaconCts;
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { capturedCts.Cancel(); capturedCts.Dispose(); } catch { } };
    _ = Task.Run(() => beacon.RunAsync(beaconCts.Token));
    startupLogger.LogInformation("LAN discovery beacon: {Group}:{Port} every {IntervalSec}s", beaconGroup, beaconPort, beaconIntervalSec);
}

// Start server
var server = new DrawServer(port, dispatcher, clientRegistry, roomService, rateLimiter, sessionTokenStore, loggerFactory);
startupLogger.LogInformation("Starting on port {Port}", port);
startupLogger.LogInformation("Session resume grace: {GraceSeconds}s", graceSeconds);
startupLogger.LogInformation("Health endpoint: {Prefix}health", healthServer.BoundPrefix);
startupLogger.LogInformation("Claude API key: {KeyStatus}", string.IsNullOrWhiteSpace(apiKey) ? "(none — fallback parser only)" : "present");
startupLogger.LogInformation("MCP project: {McpProjectPath}", mcpProjectPath ?? "(not found)");
await server.StartAsync();

static int ReadIntEnv(string name, int @default, int min, int max = int.MaxValue)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw)) return @default;
    if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max)
        return v;
    Console.Error.WriteLine($"[config] Invalid {name}=\"{raw}\" (need int in [{min}, {max}]); using default {@default}");
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
