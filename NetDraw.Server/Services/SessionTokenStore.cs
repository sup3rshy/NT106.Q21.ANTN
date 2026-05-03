using System.Collections.Concurrent;

namespace NetDraw.Server.Services;

public sealed class SessionEntry
{
    public byte[] Bytes { get; }
    public ClientHandler? Handler { get; set; }
    public string UserId { get; }
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
    void Issue(string token, byte[] bytes, ClientHandler handler, string userId);
    void MarkOrphaned(ClientHandler handler);
    SessionEntry? Get(string token);
}

public class SessionTokenStore : ISessionTokenStore
{
    // Phase 1: zero grace window — orphaned tokens are removed outright.
    // Phase 2 (P8.T1) flips this to a configurable non-zero value to support resume.
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

    public void MarkOrphaned(ClientHandler handler)
    {
        // Walk the dictionary (small per-server) and detach matching entries.
        // With GraceWindow == zero the entry is removed; otherwise its handler is
        // detached and expiresAt is stamped so Phase 2 TryClaim can see it.
        foreach (var kvp in _entries)
        {
            if (!ReferenceEquals(kvp.Value.Handler, handler)) continue;

            if (GraceWindow <= TimeSpan.Zero)
            {
                _entries.TryRemove(kvp.Key, out _);
            }
            else
            {
                kvp.Value.Handler = null;
                kvp.Value.ExpiresAt = DateTimeOffset.UtcNow + GraceWindow;
            }
        }
    }

    public SessionEntry? Get(string token) =>
        _entries.TryGetValue(token, out var entry) ? entry : null;
}
