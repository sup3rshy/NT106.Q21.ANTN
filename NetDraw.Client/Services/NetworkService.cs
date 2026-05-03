using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Client.Services;

public class NetworkService : INetworkService
{
    private TcpClient? _client;
    // Stream not NetworkStream: TLS path wraps in SslStream; plaintext path
    // hands the NetworkStream straight through. Both expose the same surface.
    private Stream? _stream;
    private bool _isConnected;
    private readonly bool _insecure;
    private readonly string _pin;
    private readonly StringBuilder _buffer = new();
    // Decoder (not Encoding.GetString) — keeps state across reads so a UTF-8 sequence
    // split between two ReadAsync chunks decodes correctly.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
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

    // Phase 1 TLS opt-in. Defaults read from env so adding CLI plumbing to
    // App.xaml.cs stays out of scope for this PR. Phase 2 will flip the default.
    //   NETDRAW_INSECURE=1   → plaintext (Phase 1 default)
    //   NETDRAW_PIN=<HEX>    → required when not insecure; the SHA-256 thumbprint
    //                          of the server's leaf cert (uppercase hex, no separators).
    public NetworkService()
        : this(
            insecure: Environment.GetEnvironmentVariable("NETDRAW_INSECURE") != "0",
            pin: Environment.GetEnvironmentVariable("NETDRAW_PIN") ?? string.Empty)
    {
    }

    public NetworkService(bool insecure, string pin)
    {
        _insecure = insecure;
        _pin = pin ?? string.Empty;
    }

    public async Task<bool> ConnectAsync(string host, int port)
    {
        try
        {
            _decoder.Reset();
            _buffer.Clear();
            // Token lifetime is bounded to one TCP connection; never carry one across reconnects.
            // LastSessionToken survives so the caller can attempt Resume; SessionToken does not.
            SessionToken = string.Empty;
            _userInitiatedDisconnect = false;
            LastDisconnectWasUserInitiated = false;

            if (!_insecure && string.IsNullOrWhiteSpace(_pin))
            {
                // No pin → no way to validate the server. Refusing here rather than
                // silently accepting any cert preserves the only thing TLS+pin gives us.
                Disconnected?.Invoke("TLS bật nhưng NETDRAW_PIN trống — không thể xác thực server.");
                return false;
            }

            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            Stream stream = _client.GetStream();

            if (!_insecure)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = ValidatePinned,
                    });
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException)
                {
                    try { ssl.Dispose(); } catch { }
                    try { _client?.Close(); } catch { }
                    Disconnected?.Invoke($"TLS handshake thất bại: {ex.Message}");
                    return false;
                }
                stream = ssl;
            }

            _stream = stream;
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

    // Pin the leaf, not the chain. Self-signed dev cert means SslPolicyErrors will
    // include UntrustedRoot and possibly RemoteCertificateNameMismatch — both are
    // expected and ignored. Only the SHA-256 thumbprint of the presented leaf decides.
    private bool ValidatePinned(object _, X509Certificate? cert, X509Chain? __, SslPolicyErrors ___)
    {
        if (cert is null) return false;
        var leaf = cert as X509Certificate2 ?? new X509Certificate2(cert);
        var actual = leaf.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        return FixedTimeEquals(actual, _pin);
    }

    // Length-tolerant constant-time string compare. A timing oracle on a public
    // SHA-256 thumbprint is not a real attack (the value is meant to be public),
    // but the constant-time compare is the discipline this layer should keep.
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            // Case-insensitive over ASCII hex.
            if (ca >= 'a' && ca <= 'z') ca = (char)(ca - 32);
            if (cb >= 'a' && cb <= 'z') cb = (char)(cb - 32);
            diff |= ca ^ cb;
        }
        return diff == 0;
    }

    private async Task ListenAsync()
    {
        try
        {
            byte[] buffer = new byte[8192];
            char[] charBuffer = new char[8192];
            while (_isConnected && _client?.Connected == true)
            {
                int bytesRead = await _stream!.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                int charsDecoded = _decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
                _buffer.Append(charBuffer, 0, charsDecoded);
                string data = _buffer.ToString();
                int idx;
                while ((idx = data.IndexOf('\n')) >= 0)
                {
                    string json = data[..idx];
                    data = data[(idx + 1)..];
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var envelope = MessageEnvelope.Parse(json);
                        if (envelope != null)
                            MessageReceived?.Invoke(envelope.Type, envelope.SenderId, envelope.SenderName, envelope.RoomId, envelope.RawPayload);
                    }
                }
                _buffer.Clear();
                _buffer.Append(data);
            }
        }
        catch
        {
            // Swallow read errors; finally fires the single Disconnected notification.
        }
        finally
        {
            CloseSocket();
            Disconnected?.Invoke(_userInitiatedDisconnect ? "Đã ngắt kết nối" : "Mất kết nối");
        }
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
