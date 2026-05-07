using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetDraw.Server.Services;

public class TokenBucketRateLimiter : IRateLimiter
{
    private readonly int _capacity;
    private readonly double _refillPerSec;
    private readonly ConcurrentDictionary<ClientHandler, Bucket> _buckets = new();
    // Tombstone set: clients that have been Forget()'d. The value byte is unused — we only need
    // ContainsKey. Entries self-expire after a few seconds (see Forget).
    private readonly ConcurrentDictionary<ClientHandler, byte> _forgottenClients = new();

    public TokenBucketRateLimiter(int capacity = 200, double refillPerSec = 50)
    {
        _capacity = capacity;
        _refillPerSec = refillPerSec;
    }

    public bool TryAcquire(ClientHandler client)
    {
        var bucket = _buckets.GetOrAdd(client, _ => new Bucket(_capacity, Stopwatch.GetTimestamp()));

        // Race window:
        //   T1: Forget() flips _forgottenClients[client]=true and TryRemove()s the bucket
        //   T2: TryAcquire() runs GetOrAdd just AFTER the TryRemove, creating a fresh bucket
        // Without the post-GetOrAdd re-check below, the fresh bucket would leak in the dict
        // forever even though the client has torn down. Verify forgotten-state and evict.
        if (_forgottenClients.ContainsKey(client))
        {
            _buckets.TryRemove(client, out _);
            return false;
        }

        lock (bucket)
        {
            if (bucket.Forgotten)
            {
                _buckets.TryRemove(client, out _);
                return false;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = Math.Max(0, (now - bucket.LastRefill) / (double)Stopwatch.Frequency);
            bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsed * _refillPerSec);
            bucket.LastRefill = now;

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return true;
            }
            return false;
        }
    }

    public void Forget(ClientHandler client)
    {
        // Set the forgotten flag FIRST so any concurrent TryAcquire that has already
        // resolved its bucket reference will observe Forgotten=true on the next lock,
        // and any TryAcquire that runs GetOrAdd after this point sees the client in
        // _forgottenClients and evicts immediately.
        _forgottenClients[client] = 0;
        if (_buckets.TryRemove(client, out var bucket))
        {
            lock (bucket) bucket.Forgotten = true;
        }
        // Drop the tombstone shortly after — keeps the set bounded. We schedule a
        // best-effort clear: the only consumer is TryAcquire which is fine if the flag
        // is gone (the bucket is also gone). 5 s is plenty for in-flight messages.
        var capturedClient = client;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(t =>
        {
            _forgottenClients.TryRemove(capturedClient, out _);
        });
    }

    private sealed class Bucket
    {
        public double Tokens;
        public long LastRefill;
        public bool Forgotten;
        public Bucket(double tokens, long lastRefill)
        {
            Tokens = tokens;
            LastRefill = lastRefill;
        }
    }
}
