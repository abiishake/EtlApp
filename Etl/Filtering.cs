using System.Globalization;

namespace EtlApp.Etl;

/// <summary>
/// A filter tree node. A leaf has Column/Op/Value. A group has Items (its children) and a Mode ("and" | "or");
/// groups can contain leaves and further groups, so e.g. A AND (B OR (C AND D)) is possible.
/// </summary>
public sealed record FilterSpec(string? Column, string? Op, string? Value, string? Mode = null, List<FilterSpec>? Items = null);
public sealed record DistinctValue(string Value, int Count);
public sealed record ColumnInfo(
    string Name,
    string Type,                    // "text" or "number"
    int DistinctCount,
    List<DistinctValue>? Values,    // null when there are too many distinct values to list
    double? Min,
    double? Max);

/// <summary>Describes each column (type + distinct values) and applies row filters. Filters can be nested in AND / OR groups.</summary>
public static class Filtering
{
    public const int MaxListedValues = 200;

    // ------------------------------------------------------------------ column metadata for the UI
    public static List<ColumnInfo> Describe(TransformResult data)
    {
        var result = new List<ColumnInfo>(data.Columns.Count);
        foreach (var col in data.Columns)
        {
            var values = data.Rows.Select(r => r.TryGetValue(col, out var v) ? v : null)
                                  .Where(v => v != null).Select(v => v!).ToList();
            bool isNumber = values.Count > 0 && values.All(IsNumeric);

            // distinct values with counts (case-insensitive, first spelling wins)
            var counts = new Dictionary<string, (string Shown, double Num, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in values)
            {
                var shown = Display(v);
                var num = isNumber ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
                counts[shown] = counts.TryGetValue(shown, out var e) ? (e.Shown, e.Num, e.Count + 1) : (shown, num, 1);
            }

            List<DistinctValue>? listed = null;
            if (counts.Count <= MaxListedValues)
            {
                var ordered = isNumber
                    ? counts.Values.OrderBy(e => e.Num)
                    : counts.Values.OrderBy(e => e.Shown, StringComparer.OrdinalIgnoreCase);
                listed = ordered.Select(e => new DistinctValue(e.Shown, e.Count)).ToList();
            }

            double? min = isNumber ? counts.Values.Min(e => e.Num) : null;
            double? max = isNumber ? counts.Values.Max(e => e.Num) : null;
            result.Add(new ColumnInfo(col, isNumber ? "number" : "text", counts.Count, listed, min, max));
        }
        return result;
    }

    // ------------------------------------------------------------------ apply filters
    static bool IsOr(string? mode) => string.Equals(mode, "or", StringComparison.OrdinalIgnoreCase);

    /// Drops filters with an empty value and groups left with nothing in them; trims values and normalises modes.
    public static List<FilterSpec> Active(IEnumerable<FilterSpec>? filters)
    {
        var result = new List<FilterSpec>();
        foreach (var f in filters ?? Enumerable.Empty<FilterSpec>())
        {
            if (f.Items != null)
            {
                var sub = Active(f.Items);
                if (sub.Count > 0)
                    result.Add(new FilterSpec(null, null, null, IsOr(f.Mode) ? "or" : "and", sub));
            }
            else if (!string.IsNullOrWhiteSpace(f.Value))
                result.Add(new FilterSpec(f.Column, f.Op, f.Value!.Trim()));
        }
        return result;
    }

    /// Empty-value filters / empty groups are ignored. Throws ArgumentException for invalid filters.
    /// `mode` is how the top-level items combine ("or" = any, otherwise all).
    public static List<Dictionary<string, object?>> Apply(TransformResult data, IEnumerable<FilterSpec>? filters, string? mode = null)
    {
        var active = Active(filters);
        if (active.Count == 0) return data.Rows;
        var test = Combine(data, active, mode);
        return data.Rows.Where(test).ToList();
    }

    static Func<Dictionary<string, object?>, bool> Combine(TransformResult data, List<FilterSpec> items, string? mode)
    {
        var predicates = items.Select(f => f.Items != null ? Combine(data, f.Items, f.Mode) : Leaf(data, f)).ToList();
        if (IsOr(mode)) return r => predicates.Any(p => p(r));
        return r => predicates.All(p => p(r));
    }

    static Func<Dictionary<string, object?>, bool> Leaf(TransformResult data, FilterSpec f)
    {
        var col = data.Columns.FirstOrDefault(c => c == f.Column)
                  ?? throw new ArgumentException($"Unknown column '{f.Column}'.");
        bool isNumber = IsNumberColumn(data, col);
        var val = f.Value!.Trim();

        switch (f.Op)
        {
            case "contains" when !isNumber:
                return r => Shown(r, col)?.Contains(val, StringComparison.OrdinalIgnoreCase) == true;

            case "equals" when !isNumber:
                return r => string.Equals(Shown(r, col)?.Trim(), val, StringComparison.OrdinalIgnoreCase);

            case "gt" or "gte" or "lt" or "lte" or "eq" when isNumber:
                if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new ArgumentException($"'{val}' is not a number (column '{col}').");
                var op = f.Op;
                return r => Num(r, col) is { } x && op switch
                {
                    "gt"  => x > n,
                    "gte" => x >= n,
                    "lt"  => x < n,
                    "lte" => x <= n,
                    _     => Math.Abs(x - n) < 1e-9
                };

            default:
                throw new ArgumentException($"Filter '{f.Op}' is not valid for column '{col}'.");
        }
    }

    // ------------------------------------------------------------------ helpers
    static bool IsNumeric(object v) => v is double or float or int or long or decimal or short;

    static bool IsNumberColumn(TransformResult data, string col)
    {
        bool any = false;
        foreach (var r in data.Rows)
        {
            if (!r.TryGetValue(col, out var v) || v == null) continue;
            if (!IsNumeric(v)) return false;
            any = true;
        }
        return any;
    }

    static string? Shown(Dictionary<string, object?> r, string col) =>
        r.TryGetValue(col, out var v) && v != null ? Display(v) : null;

    static double? Num(Dictionary<string, object?> r, string col) =>
        r.TryGetValue(col, out var v) && v != null && IsNumeric(v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null;

    /// how a cell value appears in the filter UI (dates as yyyy-MM-dd, numbers without trailing zeros)
    static string Display(object v) => v switch
    {
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double or float or decimal => Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };
}
