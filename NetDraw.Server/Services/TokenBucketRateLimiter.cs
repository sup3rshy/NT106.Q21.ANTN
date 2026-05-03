using System.Collections.Concurrent;

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
        var bucket = _buckets.GetOrAdd(client, _ => new Bucket(_capacity, DateTimeOffset.UtcNow));
        lock (bucket)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - bucket.LastRefill).TotalSeconds;
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

    public void Forget(ClientHandler client) => _buckets.TryRemove(client, out _);

    private sealed class Bucket
    {
        public double Tokens;
        public DateTimeOffset LastRefill;
        public Bucket(double tokens, DateTimeOffset lastRefill)
        {
            Tokens = tokens;
            LastRefill = lastRefill;
        }
    }
}
