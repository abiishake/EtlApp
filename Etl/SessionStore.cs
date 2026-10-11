using System.Collections.Concurrent;

namespace EtlApp.Etl;

/// <summary>Keeps the transformed table in memory between "upload" and "download". Tiny and self-cleaning.</summary>
public sealed class SessionStore
{
    sealed record Entry(TransformResult Data, DateTime Created);

    static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    const int MaxEntries = 10;

    readonly ConcurrentDictionary<string, Entry> _items = new();

    public string Add(TransformResult data)
    {
        Prune();
        var id = Guid.NewGuid().ToString("N");
        _items[id] = new Entry(data, DateTime.UtcNow);
        return id;
    }

    public TransformResult? Get(string id) =>
        _items.TryGetValue(id, out var e) && DateTime.UtcNow - e.Created < Ttl ? e.Data : null;

    void Prune()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _items.Where(kv => kv.Value.Created < cutoff).ToList())
            _items.TryRemove(kv.Key, out _);

        while (_items.Count >= MaxEntries)
        {
            var oldest = _items.OrderBy(kv => kv.Value.Created).First().Key;
            _items.TryRemove(oldest, out _);
        }
    }
}
