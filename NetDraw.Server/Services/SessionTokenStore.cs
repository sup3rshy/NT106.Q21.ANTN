using System.Collections.Concurrent;

namespace NetDraw.Server.Services;

public sealed class SessionEntry
{
    public byte[] Bytes { get; }
    public ClientHandler? Handler { get; set; }
    public string UserId { get; }
    public string? LastRoomId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public SessionEntry(byte[] bytes, ClientHandler handler, string userId, DateTimeOffset expiresAt)
    {
        Bytes = bytes;
        Handler = handler;
        UserId = userId;
        ExpiresAt = expiresAt;
    }
}

public interface ISessionTokenStore
{
    TimeSpan GraceWindow { get; }
    void Issue(string token, byte[] bytes, ClientHandler handler, string userId);
    void RecordRoom(ClientHandler handler, string roomId);
    bool MarkOrphaned(ClientHandler handler);
    bool TryClaim(string token, ClientHandler newHandler, out string userId, out string? lastRoomId);
    void Remove(string token);
    SessionEntry? Get(string token);
}

public class SessionTokenStore : ISessionTokenStore
{
    public TimeSpan GraceWindow { get; }

    private readonly ConcurrentDictionary<string, SessionEntry> _entries = new(StringComparer.Ordinal);

    public SessionTokenStore() : this(TimeSpan.Zero) { }

    public SessionTokenStore(TimeSpan graceWindow)
    {
        GraceWindow = graceWindow;
    }

    public void Issue(string token, byte[] bytes, ClientHandler handler, string userId)
    {
        _entries[token] = new SessionEntry(bytes, handler, userId, DateTimeOffset.MaxValue);
    }

    public void RecordRoom(ClientHandler handler, string roomId)
    {
        foreach (var kvp in _entries)
        {
            if (ReferenceEquals(kvp.Value.Handler, handler))
                kvp.Value.LastRoomId = roomId;
        }
    }

    public bool MarkOrphaned(ClientHandler handler)
    {
        bool persisted = false;
        foreach (var kvp in _entries)
        {
            if (!ReferenceEquals(kvp.Value.Handler, handler)) continue;

            if (GraceWindow <= TimeSpan.Zero)
            {
                _entries.TryRemove(kvp.Key, out _);
            }
            else
            {
                lock (kvp.Value)
                {
                    kvp.Value.Handler = null;
                    kvp.Value.ExpiresAt = DateTimeOffset.UtcNow + GraceWindow;
                }
                persisted = true;
            }
        }
        return persisted;
    }

    public bool TryClaim(string token, ClientHandler newHandler, out string userId, out string? lastRoomId)
    {
        userId = string.Empty;
        lastRoomId = null;
        if (string.IsNullOrEmpty(token)) return false;
        if (!_entries.TryGetValue(token, out var entry)) return false;

        // Per-entry lock makes the read-check-write of Handler atomic against another
        // concurrent TryClaim or a MarkOrphaned that races in mid-grace.
        lock (entry)
        {
            if (entry.Handler is not null) return false;
            if (DateTimeOffset.UtcNow >= entry.ExpiresAt)
            {
                _entries.TryRemove(token, out _);
                return false;
            }

            entry.Handler = newHandler;
            entry.ExpiresAt = DateTimeOffset.MaxValue;
            userId = entry.UserId;
            lastRoomId = entry.LastRoomId;
        }

        newHandler.SessionToken = token;
        newHandler.SessionTokenBytes = entry.Bytes;
        return true;
    }

    public void Remove(string token) => _entries.TryRemove(token, out _);

    public SessionEntry? Get(string token) =>
        _entries.TryGetValue(token, out var entry) ? entry : null;
}
