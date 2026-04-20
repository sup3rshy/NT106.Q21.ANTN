using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server.Services;

/// <summary>
/// TCP client that connects DrawServer to the MCP (AI) server.
/// - Maintains a persistent connection with automatic background reconnect.
/// - Serialises concurrent requests: at most one in-flight request at a time.
/// - Times out AI calls after 60 s so a slow model never blocks the room pipeline.
/// </summary>
public class McpClient : IMcpClient
{
    private TcpClient?    _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    private readonly string _host;
    private readonly int    _port;

    // Ensures at most one request travels the TCP pipe at any moment.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Used to cancel the background reconnect loop on Dispose.
    private readonly CancellationTokenSource _cts = new();

    public bool IsConnected => _client?.Connected == true;

    public McpClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    // ─── Connection lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget: starts the background loop that keeps the connection alive.
    /// Returns immediately; the first connect attempt happens asynchronously.
    /// </summary>
    public Task ConnectAsync()
    {
        _ = Task.Run(MaintainConnectionLoopAsync);
        return Task.CompletedTask;
    }

    private async Task MaintainConnectionLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_client?.Connected != true)
            {
                await TryConnectOnceAsync();
            }
            else
            {
                // Poll every 5 s to detect silent disconnects.
                try { await Task.Delay(5_000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task TryConnectOnceAsync()
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(_host, _port);
            var stream = client.GetStream();

            // Atomically swap in the new connection so SendCommandAsync always
            // sees a consistent (reader, writer) pair.
            await _lock.WaitAsync(_cts.Token);
            try
            {
                CloseCurrentConnection();
                _client = client;
                _reader  = new StreamReader(stream, Encoding.UTF8);
                _writer  = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            }
            finally { _lock.Release(); }

            Console.WriteLine($"[MCP] Connected to {_host}:{_port}");
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Console.WriteLine($"[MCP] Connection failed ({ex.Message}), retrying in 10 s…");
            try { await Task.Delay(10_000, _cts.Token); }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }

    private void CloseCurrentConnection()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _writer = null;
        _reader = null;
        _client = null;
    }

    // ─── Request / Response ────────────────────────────────────────────────────

    public async Task<AiResultPayload?> SendCommandAsync(string command, string roomId)
    {
        if (!IsConnected)
        {
            Console.WriteLine("[MCP] SendCommand — not connected, skipping");
            return null;
        }

        // Allow only one in-flight request; wait up to 65 s for a slot.
        Console.WriteLine($"[MCP] Waiting for send lock…  (command: \"{command[..Math.Min(60, command.Length)]}\")");
        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(65)))
        {
            Console.WriteLine("[MCP] Request dropped — channel busy for >65 s");
            return null;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_writer == null || _reader == null)
            {
                Console.WriteLine("[MCP] No writer/reader — connection dropped between lock and send");
                return null;
            }

            Console.WriteLine($"[MCP] → Sending to McpServer…");
            var serialized = NetMessage<AiCommandPayload>.Create(
                MessageType.AiCommand, "server", "Server", roomId,
                new AiCommandPayload { Prompt = command }).Serialize().TrimEnd();
            await _writer.WriteLineAsync(serialized);
            Console.WriteLine($"[MCP]   sent in {sw.ElapsedMilliseconds} ms, waiting for response…");

            // Time-box the AI call — Claude can take 5-60 s.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var line = await _reader.ReadLineAsync(timeout.Token);
            Console.WriteLine($"[MCP] ← Response received in {sw.ElapsedMilliseconds} ms  (null={line == null})");

            if (line == null) return null;

            var payload = MessageEnvelope.Deserialize<AiResultPayload>(line)?.Payload;
            Console.WriteLine($"[MCP]   parsed: {payload?.Actions.Count ?? -1} action(s)");
            return payload;
        }
        catch (OperationCanceledException)
        {
            // IMPORTANT: close the socket so any pending stale response from the
            // previous command is discarded. Otherwise the next request reads the
            // OLD response (observed in the wild as "null=True" followed by the
            // previous prompt's actions being mis-routed).
            Console.WriteLine($"[MCP] ✘ Timed out after {sw.ElapsedMilliseconds} ms (>60 s) — closing socket to drop stale response");
            CloseCurrentConnection();
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MCP] ✘ Error after {sw.ElapsedMilliseconds} ms: {ex.Message}");
            CloseCurrentConnection();
            return null;
        }
        finally
        {
            _lock.Release();
            Console.WriteLine($"[MCP] Lock released after {sw.ElapsedMilliseconds} ms");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        CloseCurrentConnection();
    }
}
