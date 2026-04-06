using NetDraw.Server;

Console.Title = "NetDraw Server";

int port = 5000;
string mcpHost = "127.0.0.1";
int mcpPort = 5001;

// Đọc port từ args
if (args.Length > 0 && int.TryParse(args[0], out int customPort))
    port = customPort;

var server = new DrawServer(port, mcpHost, mcpPort);

// Graceful shutdown
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[*] Shutting down server...");
    server.Stop();
};

await server.StartAsync();
