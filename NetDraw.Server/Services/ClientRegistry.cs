using System.Collections.Concurrent;

namespace NetDraw.Server.Services;

public class ClientRegistry : IClientRegistry
{
    private readonly ConcurrentDictionary<string, ClientHandler> _clients = new();

    public void Register(string userId, ClientHandler handler) => _clients[userId] = handler;

    public void Unregister(string userId) => _clients.TryRemove(userId, out _);

    public ClientHandler? GetHandler(string userId)
    {
        _clients.TryGetValue(userId, out var handler);
        return handler;
    }

    public IEnumerable<ClientHandler> GetAll() => _clients.Values;
}
