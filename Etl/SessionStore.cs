using System.Collections.Concurrent;

namespace EtlApp.Etl;

/// <summary>Keeps uploads in memory between "upload" and "download". Tiny and self-cleaning.
/// An upload stays available for 1 hour after it was last used (every filter count / export renews it).
/// Each upload keeps the rows as read from Excel, so the optional clean-up rules can be switched on and off again
/// (the transformed table is rebuilt from the raw rows every time).
/// Memory is bounded by a total cell budget: the least recently used uploads are dropped first.</summary>
public sealed class SessionStore
{
    public sealed class Entry
    {
        internal Entry(string fileName, string sheet, List<string> headers, List<Dictionary<string, object?>> raw,
                       TransformResult data, long cells, DateTime now)
        {
            FileName = fileName; Sheet = sheet; Headers = headers; Raw = raw; Data = data; Cells = cells; LastUsed = now;
        }
        public string FileName { get; }
        public string Sheet { get; }
        public List<string> Headers { get; }
        public List<Dictionary<string, object?>> Raw { get; }
        /// the current transformed table (swapped as a whole when the clean-up rules change)
        public TransformResult Data { get; private set; }
        public IReadOnlyList<string> Cleanup { get; private set; } = Array.Empty<string>();
        /// bumped on every clean-up change, so open pages can notice that the table changed
        public int Version { get; private set; } = 1;
        internal long Cells { get; }
        internal DateTime LastUsed { get; set; }

        internal readonly object Gate = new();

        public void SetCleanup(IReadOnlyList<string> rules, TransformResult data)
        {
            lock (Gate) { Cleanup = rules; Data = data; Version++; }
        }
    }

    static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// largest single upload (rows x columns) we accept
    public const long MaxCellsPerUpload = 1_000_000;
    /// all uploads together (raw + transformed copies are both counted by this one number)
    const long MaxTotalCells = 1_500_000;      // ~185 MB of rows (measured ~123 MB per 1M cells incl. both copies)

    readonly ConcurrentDictionary<string, Entry> _items = new();
    readonly object _addGate = new();

    public string Add(string fileName, string sheet, List<string> headers, List<Dictionary<string, object?>> raw, TransformResult data)
    {
        long cells = (long)Math.Max(1, headers.Count) * Math.Max(1, raw.Count);
        var id = Guid.NewGuid().ToString("N");
        lock (_addGate)
        {
            Prune(cells);
            _items[id] = new Entry(fileName, sheet, headers, raw, data, cells, DateTime.UtcNow);
        }
        return id;
    }

    public Entry? GetEntry(string id)
    {
        if (string.IsNullOrEmpty(id) || !_items.TryGetValue(id, out var e)) return null;
        var now = DateTime.UtcNow;
        if (now - e.LastUsed >= Ttl) { _items.TryRemove(id, out _); return null; }
        e.LastUsed = now;     // sliding expiry: working on the file keeps it alive
        return e;
    }

    public TransformResult? Get(string id) => GetEntry(id)?.Data;

    void Prune(long incoming)
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _items.Where(kv => kv.Value.LastUsed < cutoff).ToList())
            _items.TryRemove(kv.Key, out _);

        while (!_items.IsEmpty && _items.Values.Sum(e => e.Cells) + incoming > MaxTotalCells)
        {
            var oldest = _items.OrderBy(kv => kv.Value.LastUsed).First().Key;
            _items.TryRemove(oldest, out _);
        }
    }
}
