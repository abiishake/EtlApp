using System.Globalization;

namespace EtlApp.Etl;

public sealed record FilterSpec(string? Column, string? Op, string? Value);
public sealed record DistinctValue(string Value, int Count);
public sealed record ColumnInfo(
    string Name,
    string Type,                    // "text" or "number"
    int DistinctCount,
    List<DistinctValue>? Values,    // null when there are too many distinct values to list
    double? Min,
    double? Max);

/// <summary>Describes each column (type + distinct values) and applies row filters. Filters are AND-ed.</summary>
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
    /// Empty-value filters are ignored. Throws ArgumentException for invalid filters.
    public static List<Dictionary<string, object?>> Apply(TransformResult data, IEnumerable<FilterSpec>? filters)
    {
        var active = (filters ?? Enumerable.Empty<FilterSpec>())
            .Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (active.Count == 0) return data.Rows;

        var predicates = new List<Func<Dictionary<string, object?>, bool>>();
        foreach (var f in active)
        {
            var col = data.Columns.FirstOrDefault(c => c == f.Column)
                      ?? throw new ArgumentException($"Unknown column '{f.Column}'.");
            bool isNumber = IsNumberColumn(data, col);
            var val = f.Value!.Trim();

            switch (f.Op)
            {
                case "contains" when !isNumber:
                    predicates.Add(r => Shown(r, col)?.Contains(val, StringComparison.OrdinalIgnoreCase) == true);
                    break;

                case "equals" when !isNumber:
                    predicates.Add(r => string.Equals(Shown(r, col)?.Trim(), val, StringComparison.OrdinalIgnoreCase));
                    break;

                case "gt" or "lt" or "eq" when isNumber:
                    if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        throw new ArgumentException($"'{val}' is not a number (column '{col}').");
                    var op = f.Op;
                    predicates.Add(r => Num(r, col) is { } x &&
                        (op == "gt" ? x > n : op == "lt" ? x < n : Math.Abs(x - n) < 1e-9));
                    break;

                default:
                    throw new ArgumentException($"Filter '{f.Op}' is not valid for column '{col}'.");
            }
        }
        return data.Rows.Where(r => predicates.All(p => p(r))).ToList();
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
