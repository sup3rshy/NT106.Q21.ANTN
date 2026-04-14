using System.Collections.Concurrent;

namespace NetDraw.Client.Infrastructure;

public class EventAggregator
{
    public static EventAggregator Instance { get; } = new();

    private readonly ConcurrentDictionary<Type, List<Delegate>> _subscriptions = new();
    private readonly object _lock = new();

    public void Subscribe<TEvent>(Action<TEvent> handler)
    {
        var list = _subscriptions.GetOrAdd(typeof(TEvent), _ => new List<Delegate>());
        lock (_lock) list.Add(handler);
    }

    public void Unsubscribe<TEvent>(Action<TEvent> handler)
    {
        if (_subscriptions.TryGetValue(typeof(TEvent), out var list))
            lock (_lock) list.Remove(handler);
    }

    public void Publish<TEvent>(TEvent evt)
    {
        if (!_subscriptions.TryGetValue(typeof(TEvent), out var list)) return;
        List<Delegate> snapshot;
        lock (_lock) snapshot = new List<Delegate>(list);
        foreach (var handler in snapshot)
            ((Action<TEvent>)handler)(evt);
    }
}
