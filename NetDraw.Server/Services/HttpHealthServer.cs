using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Services;

public class HttpHealthServer
{
    private readonly int _port;
    private readonly IRoomService _roomService;
    private readonly ILogger<HttpHealthServer> _logger;
    private readonly DateTimeOffset _startedAt;
    private readonly SemaphoreSlim _concurrencyLimit;

    public HttpHealthServer(int port, IRoomService roomService, ILogger<HttpHealthServer>? logger = null, int maxConcurrent = 16)
    {
        if (maxConcurrent < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        _port = port;
        _roomService = roomService;
        _logger = logger ?? NullLogger<HttpHealthServer>.Instance;
        _startedAt = DateTimeOffset.UtcNow;
        _concurrencyLimit = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new HttpListener();
        // "+" binds all local interfaces so an external load balancer can probe; "localhost" would
        // only answer same-host probes. On Linux this needs no ACL; on Windows it requires netsh.
        listener.Prefixes.Add($"http://+:{_port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogWarning(ex, "Failed to bind on port {Port} with prefix '+'; falling back to 'localhost'", _port);
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{_port}/");
            listener.Start();
        }

        _logger.LogInformation("Listening on port {Port}", _port);
        using var registration = ct.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }

            _ = Task.Run(async () =>
            {
                if (!await _concurrencyLimit.WaitAsync(0))
                {
                    try { await WriteAsync(ctx, 503, "text/plain", "Busy"); }
                    catch { try { ctx.Response.Abort(); } catch { } }
                    return;
                }
                try { await HandleAsync(ctx); }
                finally { _concurrencyLimit.Release(); }
            });
        }

        try { listener.Close(); } catch { }
        _logger.LogInformation("Stopped");
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url?.AbsolutePath ?? "/";
        _logger.LogDebug("{Method} {Path}", req.HttpMethod, path);

        try
        {
            if (req.HttpMethod == "GET" && path == "/health")
            {
                var rooms = _roomService.GetAllRoomInfos();
                var clientCount = rooms.Sum(r => r.UserCount);
                var uptime = (int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
                var body = new JObject
                {
                    ["status"] = "ok",
                    ["uptime_seconds"] = uptime,
                    ["rooms"] = rooms.Count,
                    ["clients"] = clientCount
                };
                await WriteAsync(ctx, 200, "application/json", JsonConvert.SerializeObject(body));
            }
            else
            {
                await WriteAsync(ctx, 404, "text/plain", "Not Found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handler error");
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
