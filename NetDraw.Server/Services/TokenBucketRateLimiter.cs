using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetDraw.Server.Services;

public class TokenBucketRateLimiter : IRateLimiter
{
    private readonly int _capacity;
    private readonly double _refillPerSec;
    private readonly ConcurrentDictionary<ClientHandler, Bucket> _buckets = new();

    public TokenBucketRateLimiter(int capacity = 200, double refillPerSec = 50)
    {
        _capacity = capacity;
        _refillPerSec = refillPerSec;
    }

    public bool TryAcquire(ClientHandler client)
    {
        var bucket = _buckets.GetOrAdd(client, _ => new Bucket(_capacity, Stopwatch.GetTimestamp()));
        lock (bucket)
        {
            // Lost a Forget race: drop the new ghost bucket so the dict can't grow per
            // disconnect, and refuse the token — the client is leaving anyway.
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
        if (_buckets.TryRemove(client, out var bucket))
        {
            lock (bucket) bucket.Forgotten = true;
        }
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
