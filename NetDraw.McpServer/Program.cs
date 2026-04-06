using NetDraw.McpServer;

Console.Title = "NetDraw MCP Server";

int port = 5001;

// Đọc port từ args
if (args.Length > 0 && int.TryParse(args[0], out int customPort))
    port = customPort;

// Đọc API key từ biến môi trường hoặc args
string? apiKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
if (args.Length > 1)
    apiKey = args[1];

var mcpServer = new McpDrawServer(port, apiKey);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[*] Shutting down MCP Server...");
    mcpServer.Stop();
};

await mcpServer.StartAsync();
