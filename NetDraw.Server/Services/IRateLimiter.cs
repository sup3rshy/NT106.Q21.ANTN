namespace NetDraw.Server.Services;

public interface IRateLimiter
{
    bool TryAcquire(ClientHandler client);
    void Forget(ClientHandler client);
}
