using System.Collections.Concurrent;

namespace EtlApp.Etl;

/// <summary>Keeps the transformed table in memory between "upload" and "download". Tiny and self-cleaning.
/// An upload stays available for 1 hour after it was last used (every filter count / export renews it).</summary>
public sealed class SessionStore
{
    sealed class Entry
    {
        public Entry(TransformResult data, DateTime now) { Data = data; LastUsed = now; }
        public TransformResult Data { get; }
        public DateTime LastUsed { get; set; }
    }

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

    public TransformResult? Get(string id)
    {
        if (!_items.TryGetValue(id, out var e)) return null;
        var now = DateTime.UtcNow;
        if (now - e.LastUsed >= Ttl) { _items.TryRemove(id, out _); return null; }
        e.LastUsed = now;     // sliding expiry: working on the file keeps it alive
        return e.Data;
    }

    void Prune()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _items.Where(kv => kv.Value.LastUsed < cutoff).ToList())
            _items.TryRemove(kv.Key, out _);

        while (_items.Count >= MaxEntries)
        {
            var oldest = _items.OrderBy(kv => kv.Value.LastUsed).First().Key;
            _items.TryRemove(oldest, out _);
        }
    }
}
