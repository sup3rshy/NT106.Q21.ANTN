namespace NetDraw.Server.Services;

public interface IClientRegistry
{
    void Register(string userId, ClientHandler handler);
    void Unregister(string userId);
    ClientHandler? GetHandler(string userId);
    IEnumerable<ClientHandler> GetAll();
}
