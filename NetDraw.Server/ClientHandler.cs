using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server;

public class ClientHandler
{
    private const int MaxJsonLineLength = 1_048_576;

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly ByteFrameBuffer _buffer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<ClientHandler> _logger;
    private bool _isConnected = true;
    private int _tornDown;

    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = "Anonymous";
    public string UserColor { get; set; } = "#7F8C8D";

    // Write-once via RoomHandler.HandleJoinAsync. The dispatcher reads SessionTokenBytes
    // lock-free on the receive thread; safety relies on the assignment happening-before
    // the JoinRoom reply that the client must read before sending any token-bearing message.
    public string SessionToken { get; set; } = string.Empty;
    public byte[]? SessionTokenBytes { get; set; }

    public event Func<ClientHandler, MessageEnvelope.Envelope, Task>? MessageReceived;
    public event Func<ClientHandler, Task>? Disconnected;

    public ClientHandler(TcpClient tcpClient, ILogger<ClientHandler> logger)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _logger = logger;
    }

    public async Task ListenAsync()
    {
        var endpoint = _tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogInformation("Client connected from {Endpoint}", endpoint);

        try
        {
            byte[] readBuffer = new byte[8192];
            while (_isConnected && _tcpClient.Connected)
            {
                int bytesRead = await _stream.ReadAsync(readBuffer, 0, readBuffer.Length);
                if (bytesRead == 0) break;

                _buffer.Append(readBuffer.AsSpan(0, bytesRead));
                await ProcessBufferAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            _logger.LogInformation("Client {UserId} disconnected: {Reason}", UserId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Client {UserId} read loop ended with unexpected error", UserId);
        }
        finally
        {
            await TearDownAsync();
        }
    }

    private async Task ProcessBufferAsync()
    {
        // Per-line peek state machine: the first byte at every frame boundary tells us
        // whether to consume newline-delimited JSON or a length-prefixed binary frame.
        // The buffer holds raw bytes; UTF-8 decoding only happens inside a known {...}
        // slice in HandleJsonLineAsync, which is the only place a Decoder would have
        // produced cross-frame state in the old StringBuilder model.
        int pos = 0;
        while (pos < _buffer.Length)
        {
            byte first = _buffer[pos];

            if (first == MessageEnvelope.BinaryMagic)
            {
                if (_buffer.Length - pos < 6) break; // need full header
                int payloadLength = (_buffer[pos + 3] << 16) | (_buffer[pos + 4] << 8) | _buffer[pos + 5];
                if (payloadLength > MessageEnvelope.MaxBinaryPayloadLength)
                {
                    await SendBinaryFatalErrorAsync(
                        ErrorCodes.BinaryBodyUnderrun,
                        $"binary frame length {payloadLength} exceeds {MessageEnvelope.MaxBinaryPayloadLength}-byte cap");
                    _isConnected = false;
                    return;
                }
                int total = 6 + payloadLength;
                if (_buffer.Length - pos < total) break; // need body

                // Copy out before await: ReadOnlySpan<byte> cannot survive an await,
                // and the underlying _buffer array may grow (and reallocate) on the next read.
                byte[] frameCopy = _buffer.AsSpan(pos, total).ToArray();
                bool ok = await HandleBinaryFrameAsync(frameCopy);
                pos += total;
                if (!ok) return;
                continue;
            }

            if (first == (byte)'{')
            {
                int nl = _buffer.IndexOf((byte)'\n', pos);
                if (nl < 0)
                {
                    if (_buffer.Length - pos > MaxJsonLineLength)
                    {
                        await SendBinaryFatalErrorAsync(
                            ErrorCodes.BinaryBodyUnderrun,
                            $"JSON line exceeded {MaxJsonLineLength} bytes without newline");
                        _isConnected = false;
                        return;
                    }
                    break;
                }
                int lineLen = nl - pos;
                byte[] lineCopy = _buffer.AsSpan(pos, lineLen).ToArray();
                bool ok = await HandleJsonLineAsync(lineCopy);
                pos = nl + 1;
                if (!ok) return;
                continue;
            }

            if (first == 0x0D || first == 0x0A || first == 0x09 || first == 0x20)
            {
                pos++;
                continue;
            }

            await SendBinaryFatalErrorAsync(
                ErrorCodes.BinaryBadMagic,
                $"unrecognised framing byte 0x{first:x2}");
            _isConnected = false;
            return;
        }

        _buffer.Consume(pos);
    }

    private async Task<bool> HandleJsonLineAsync(byte[] lineBytes)
    {
        if (lineBytes.Length == 0 || IsAllWhitespace(lineBytes)) return true;

        // Decode UTF-8 only on a complete {...} slice — this is the only point in the
        // pipeline that touches the UTF-8 decoder, so a multi-byte sequence cannot be
        // split across reads (it's already framed by the surrounding `{` and `\n`).
        string json = Encoding.UTF8.GetString(lineBytes);
        var envelope = MessageEnvelope.Parse(json);
        if (envelope is null) return true;

        if (envelope.Type == MessageType.JoinRoom && envelope.Version != ProtocolVersion.Current)
        {
            var err = NetMessage<ErrorPayload>.Create(
                MessageType.Error, "server", "Server", envelope.RoomId,
                new ErrorPayload
                {
                    Message = $"Protocol version {envelope.Version} not supported (server expects {ProtocolVersion.Current})",
                    Code = ErrorCodes.ProtocolVersion
                });
            await SendAsync(err);
            _isConnected = false;
            return false;
        }

        if (MessageReceived != null)
            await MessageReceived(this, envelope);
        return true;
    }

    private async Task<bool> HandleBinaryFrameAsync(byte[] frame)
    {
        var envelope = MessageEnvelope.ParseBinary(frame);
        if (envelope is null)
        {
            string code = (frame.Length > 0 && frame[0] != MessageEnvelope.BinaryMagic)
                ? ErrorCodes.BinaryBadMagic
                : (frame.Length > 1 && frame[1] != MessageEnvelope.BinaryVersion)
                    ? ErrorCodes.BinaryVersionUnsupported
                    : ErrorCodes.BinaryBodyUnderrun;
            await SendBinaryFatalErrorAsync(code,
                "malformed binary frame (magic, version, length, or type-id rejected)");
            _isConnected = false;
            return false;
        }

        await SendBinaryStubErrorAsync(envelope.Type);
        return true;
    }

    private async Task SendBinaryStubErrorAsync(MessageType originalType)
    {
        var msg = NetMessage<ErrorPayload>.Create(
            MessageType.Error, "server", "Server", roomId: string.Empty,
            new ErrorPayload
            {
                Message = $"binary type-id {(int)originalType} ({originalType}) has no decoder yet",
                Code = ErrorCodes.BinaryNotImplemented
            });
        byte[] frame = MessageEnvelope.SerializeBinary(msg);
        await SendRawBytesAsync(frame);
    }

    // JSON Error so a binary-mute client can still surface the close reason.
    private async Task SendBinaryFatalErrorAsync(string code, string message)
    {
        var err = NetMessage<ErrorPayload>.Create(
            MessageType.Error, "server", "Server", roomId: string.Empty,
            new ErrorPayload { Code = code, Message = message });
        try
        {
            await SendAsync(err);
        }
        catch
        {
            // The peer may already be gone — the caller is about to tear the connection down anyway.
        }
    }

    public async Task SendRawAsync(string json)
    {
        if (!_isConnected) return;
        await SendRawBytesAsync(Encoding.UTF8.GetBytes(json));
    }

    private async Task SendRawBytesAsync(byte[] bytes)
    {
        if (!_isConnected) return;
        await _writeLock.WaitAsync();
        bool fatal = false;
        try
        {
            await _stream.WriteAsync(bytes, 0, bytes.Length);
            await _stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogWarning("Send to {UserId} failed: {ExceptionType}: {Error}", UserId, ex.GetType().Name, ex.Message);
            fatal = true;
        }
        finally { _writeLock.Release(); }

        if (fatal) await TearDownAsync();
    }

    private async Task TearDownAsync()
    {
        if (Interlocked.Exchange(ref _tornDown, 1) == 1) return;
        _isConnected = false;
        try { _stream.Close(); _tcpClient.Close(); } catch { }
        if (Disconnected != null)
            await Disconnected(this);
    }

    public async Task SendAsync<T>(NetMessage<T> message) where T : IPayload
    {
        await SendRawAsync(message.Serialize());
    }

    private static bool IsAllWhitespace(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b != 0x20 && b != 0x09 && b != 0x0D && b != 0x0A) return false;
        }
        return true;
    }
}
