using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Client.Services;

public class NetworkService : INetworkService
{
    private const int MaxJsonLineLength = 1_048_576;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _isConnected;
    // Byte-mode buffer: needed once the server starts emitting binary frames (magic 0xFE).
    // Feeding raw bytes (which can include 0xFE / 0x00) into a UTF-8 Decoder corrupts the
    // decoder state for every subsequent JSON line. Keep raw bytes until a frame boundary
    // is in hand, then decode UTF-8 only on a complete {...} JSON slice.
    private readonly ByteFrameBuffer _buffer = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _userInitiatedDisconnect;

    public string ClientId { get; private set; } = "";
    public bool IsConnected => _isConnected && (_client?.Connected ?? false);

    // Set by MainViewModel from the RoomJoined / ResumeAccepted payload. Stamped onto
    // every outbound NetMessage so the server's per-message validator accepts us as
    // the original joiner.
    public string SessionToken { get; set; } = string.Empty;

    // Survives a reconnect: MainViewModel uses this to attempt a Resume on the new
    // connection before falling back to a fresh JoinRoom. Cleared only on user
    // Disconnect or after a clean fresh JoinRoom.
    public string LastSessionToken { get; private set; } = string.Empty;
    public bool LastDisconnectWasUserInitiated { get; private set; }

    public event Action<MessageType, string, string, string, JObject?>? MessageReceived;
    public event Action<string>? Disconnected;

    public async Task<bool> ConnectAsync(string host, int port)
    {
        try
        {
            _buffer.Clear();
            // Token lifetime is bounded to one TCP connection; never carry one across reconnects.
            // LastSessionToken survives so the caller can attempt Resume; SessionToken does not.
            SessionToken = string.Empty;
            _userInitiatedDisconnect = false;
            LastDisconnectWasUserInitiated = false;

            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();
            _isConnected = true;
            if (string.IsNullOrEmpty(ClientId)) ClientId = Guid.NewGuid().ToString("N")[..8];
            _ = Task.Run(ListenAsync);
            return true;
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke($"Không thể kết nối: {ex.Message}");
            return false;
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            byte[] readBuffer = new byte[8192];
            while (_isConnected && _client?.Connected == true)
            {
                int bytesRead = await _stream!.ReadAsync(readBuffer, 0, readBuffer.Length);
                if (bytesRead == 0) break;

                _buffer.Append(readBuffer.AsSpan(0, bytesRead));
                if (!ProcessBuffer()) break;
            }
        }
        catch (Exception ex)
        {
            // Don't swallow silently — debugging socket errors without any log is painful.
            // Stay non-fatal: the finally block emits the user-visible Disconnected event.
            Debug.WriteLine($"[NetworkService] read loop ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CloseSocket();
            Disconnected?.Invoke(_userInitiatedDisconnect ? "Đã ngắt kết nối" : "Mất kết nối");
        }
    }

    /// <summary>
    /// Per-frame state machine matching the server's ClientHandler.ProcessBufferAsync. Peeks
    /// the first byte at every frame boundary to decide between newline-delimited JSON (0x7B)
    /// and a length-prefixed binary frame (0xFE). Returns false if the loop should terminate.
    /// </summary>
    private bool ProcessBuffer()
    {
        int pos = 0;
        while (pos < _buffer.Length)
        {
            byte first = _buffer[pos];

            if (first == MessageEnvelope.BinaryMagic)
            {
                if (_buffer.Length - pos < 6) break;                                // need full header
                int payloadLength = (_buffer[pos + 3] << 16) | (_buffer[pos + 4] << 8) | _buffer[pos + 5];
                if (payloadLength > MessageEnvelope.MaxBinaryPayloadLength)
                {
                    Debug.WriteLine($"[NetworkService] binary frame length {payloadLength} exceeds cap; closing");
                    _buffer.Consume(pos);
                    return false;
                }
                int total = 6 + payloadLength;
                if (_buffer.Length - pos < total) break;                            // need body

                // Copy out before invoking the handler — handler is synchronous so this is just
                // defensive in case it grows the buffer (it doesn't today).
                byte[] frameCopy = _buffer.AsSpan(pos, total).ToArray();
                var envelope = MessageEnvelope.ParseBinary(frameCopy);
                if (envelope != null)
                    MessageReceived?.Invoke(envelope.Type, envelope.SenderId, envelope.SenderName, envelope.RoomId, envelope.RawPayload);
                pos += total;
                continue;
            }

            if (first == (byte)'{')
            {
                int nl = _buffer.IndexOf((byte)'\n', pos);
                if (nl < 0)
                {
                    if (_buffer.Length - pos > MaxJsonLineLength)
                    {
                        Debug.WriteLine($"[NetworkService] JSON line exceeded {MaxJsonLineLength} bytes without newline; closing");
                        _buffer.Consume(pos);
                        return false;
                    }
                    break;                                                           // wait for more bytes
                }
                int lineLen = nl - pos;
                if (lineLen > 0)
                {
                    // Decode UTF-8 only on a complete {...} slice — multi-byte sequences are
                    // already framed by the surrounding `{` and `\n`, so no decoder state is
                    // needed across reads.
                    string json = Encoding.UTF8.GetString(_buffer.AsSpan(pos, lineLen));
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var envelope = MessageEnvelope.Parse(json);
                        if (envelope != null)
                            MessageReceived?.Invoke(envelope.Type, envelope.SenderId, envelope.SenderName, envelope.RoomId, envelope.RawPayload);
                    }
                }
                pos = nl + 1;
                continue;
            }

            // Skip whitespace (CR/LF/TAB/SP) between frames.
            if (first == 0x0D || first == 0x0A || first == 0x09 || first == 0x20)
            {
                pos++;
                continue;
            }

            // Unrecognised framing byte — drop the rest of the buffer and bail.
            Debug.WriteLine($"[NetworkService] unrecognised framing byte 0x{first:x2}; closing");
            _buffer.Consume(pos);
            return false;
        }

        _buffer.Consume(pos);
        return true;
    }

    public async Task SendAsync<T>(NetMessage<T> message) where T : IPayload
    {
        if (!IsConnected || _stream == null) return;
        // Resume carries its credential in the payload, not the envelope; the connection
        // has no bound token yet at that point. JoinRoom is also pre-issuance. Both flow
        // through here with SessionToken empty, which is correct.
        message.SessionToken = SessionToken;
        await _sendLock.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message.Serialize());
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke($"Lỗi gửi: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void ClearLastSessionToken() => LastSessionToken = string.Empty;

    public void Disconnect()
    {
        if (!_isConnected) return;
        _userInitiatedDisconnect = true;
        LastDisconnectWasUserInitiated = true;
        LastSessionToken = string.Empty;
        SessionToken = string.Empty;
        CloseSocket();
        // ListenAsync's finally block will fire the Disconnected event once.
    }

    private void CloseSocket()
    {
        if (!_isConnected) return;
        // Cache the token so a non-user-initiated drop can reconnect and resume.
        if (!_userInitiatedDisconnect && !string.IsNullOrEmpty(SessionToken))
            LastSessionToken = SessionToken;
        _isConnected = false;
        try { _stream?.Close(); _client?.Close(); } catch { }
    }
}
