using NetDraw.Server;
using NetDraw.Server.Ai;
using NetDraw.Server.Handlers;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5000;
string mcpHost = "127.0.0.1";
int mcpPort = 5001;

// Services
var clientRegistry = new ClientRegistry();
var roomService = new RoomService();
var mcpClient = new McpClient(mcpHost, mcpPort);
var fallbackParser = new FallbackAiParser();

// Start MCP connection in background
_ = Task.Run(async () =>
{
    try { await mcpClient.ConnectAsync(); }
    catch (Exception ex) { Console.WriteLine($"[MCP] Background connect failed: {ex.Message}"); }
});

// Pipeline
var dispatcher = new MessageDispatcher();
dispatcher.Register(new RoomHandler(roomService, clientRegistry));
dispatcher.Register(new DrawHandler(roomService));
dispatcher.Register(new ObjectHandler(roomService));
dispatcher.Register(new PresenceHandler(roomService));
dispatcher.Register(new ChatHandler(roomService));
dispatcher.Register(new AiHandler(roomService, mcpClient, fallbackParser));

// Start server
var server = new DrawServer(port, dispatcher, clientRegistry, roomService);
Console.WriteLine($"[NetDraw Server] Starting on port {port}...");
await server.StartAsync();
