using System.Globalization;
using System.Text.RegularExpressions;

namespace EtlApp.Etl;

public sealed record TransformResult(
    List<string> Columns,                       // final column names, in output order
    List<Dictionary<string, object?>> Rows,     // keyed by final column name
    Dictionary<string, string> Formats,         // column -> Excel number format
    List<string> Notes);                        // human-readable log of what was done

/// <summary>
/// All ETL rules from the "Instructions For AI" sheet live here.
/// Pure C# (no Excel / web dependencies) so it is easy to read, change and test.
/// </summary>
public static class Transformer
{
    // ---------------------------------------------------------------- source column names
    const string ColToday        = "today";
    const string ColDob          = "Date Of Birth";
    const string ColCaste        = "Caste";
    const string ColOtherCaste   = "Enter Other Caste";
    const string ColEthnicity    = "Ethnicity";
    const string ColOtherEth     = "Enter Other Ethnicity";
    const string ColOtherDisease = "Other disease or symptoms of any?";
    const string ColTwinQuestion = "Is he/she has a twin sister/brother?";
    const string ColTwinId       = "If yes - enter a identifier (number) e.g for Ram and Shyam enter '1'.";
    const string ColRingL        = "Length of Ring Finger (Left Hand)";
    const string ColIndexL       = "Length of Index Finger (Left Hand)";
    const string ColRingR        = "Length of Ring Finger (Right Hand)";
    const string ColIndexR       = "Length of Index Finger (Right Hand)";

    // ---------------------------------------------------------------- calculated column names
    const string OutAge      = "Age";
    const string OutTwin     = "Is Sample Twin";
    const string OutRatioL   = "Left Hand Ratio (2D:4D)";
    const string OutRatioR   = "Right Hand Ratio (2D:4D)";

    // ---------------------------------------------------------------- renames (source -> output)
    static readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["today"]                = "Date",
        ["_uuid"]                = "Id",
        ["Enter Other Caste"]    = "Other Caste",
        ["Enter Other Ethnicity"]= "Other Ethnicity",
    };

    // ---------------------------------------------------------------- Ethnicity clean-up
    static readonly Dictionary<string, string> OtherEthnicityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Maharashtra Muslim"] = "Muslim",
        ["Nepali"]             = "Nepali",
        ["Christainity"]       = "Christian",   // spelling exactly as in the form data
        ["Christianity"]       = "Christian",
        ["Jainism"]            = "Jain",
    };

    // ---------------------------------------------------------------- disease classification
    // First match wins, so order matters. Anything not matched is kept as typed.
    static readonly Regex[] DiseaseKeepAsIs =
    {
        new(@"schizo", RegexOptions.IgnoreCase | RegexOptions.Compiled),  // comorbidity, keep detail
        new(@"\bpnes\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),// non-epileptic seizures
    };

    static readonly (string Category, Regex Pattern)[] DiseaseRules =
    {
        ("Cancer",   R(@"cancer")),
        ("Epilepsy", R(@"epilep|se[iz]+ure|^\s*(IGE|JME|SD)\b|^\s*fits?\s*$")),
        ("Diabetes", R(@"diabet|^\s*type\s*2\b")),
        ("Blood Pressure", R(@"hypertension|blood\s*pressure|^\s*b\.?p\.?\s*$")),
        ("Thyroid",  R(@"thyroid")),
        ("Arthiritis", R(@"arthr")),                 // category spelled as in the form
        ("Asthma",   R(@"asthma")),
        ("Heart Problem", R(@"heart|cardiac")),
        ("Lung Problem",  R(@"lung|pulmon|bronch|tubercul|^\s*t\.?\s?b\.?\s*$")),
        ("Autism",   R(@"autis")),
        ("Allergies",R(@"allerg")),
        ("Liver Problem", R(@"liver|hepat")),
        ("PCOD / PCOS / Uterus related issues", R(@"pcos|pcod|uter|fibroid")),
    };

    static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ================================================================= entry point
    public static TransformResult Run(IReadOnlyList<string> headers, IEnumerable<Dictionary<string, object?>> inputRows)
    {
        var notes = new List<string>();
        var rows  = inputRows.Select(r => new Dictionary<string, object?>(r)).ToList();

        // tolerant header lookup (ignores case / extra spaces)
        var lookup = new Dictionary<string, string>();
        foreach (var h in headers) lookup.TryAdd(Norm(h), h);
        string? Col(string expected) => lookup.TryGetValue(Norm(expected), out var a) ? a : null;

        // ---------------------------------------------------- 1. Caste: move 'NT' from the "other" column
        var cCaste = Col(ColCaste);
        var cOtherCaste = Col(ColOtherCaste);
        if (cCaste != null && cOtherCaste != null)
        {
            int n = 0;
            foreach (var r in rows)
            {
                if (Text(r, cOtherCaste) is { } t && t.Equals("NT", StringComparison.OrdinalIgnoreCase))
                {
                    r[cCaste] = "NT";
                    r[cOtherCaste] = null;
                    n++;
                }
            }
            notes.Add($"Caste: moved 'NT' into Caste for {n} row(s).");
        }

        // ---------------------------------------------------- 2. Ethnicity: map known "other" values
        var cEth = Col(ColEthnicity);
        var cOtherEth = Col(ColOtherEth);
        if (cEth != null && cOtherEth != null)
        {
            int n = 0;
            foreach (var r in rows)
            {
                var t = Text(r, cOtherEth);
                if (t != null && OtherEthnicityMap.TryGetValue(t, out var mapped))
                {
                    r[cEth] = mapped;
                    r[cOtherEth] = null;
                    n++;
                }
            }
            notes.Add($"Ethnicity: mapped {n} 'other' value(s) into Ethnicity.");
        }

        // ---------------------------------------------------- 3. Disease free-text -> category
        var cDisease = Col(ColOtherDisease);
        if (cDisease != null)
        {
            int changed = 0;
            foreach (var r in rows)
            {
                var t = Text(r, cDisease);
                if (t == null) continue;
                var cat = ClassifyDisease(t);
                if (cat != null && cat != t) { r[cDisease] = cat; changed++; }
            }
            notes.Add($"Other disease/symptoms: {changed} value(s) classified into a standard disease; the rest kept as typed.");
        }

        // ---------------------------------------------------- 4. Calculated columns
        var cToday = Col(ColToday);
        var cDob   = Col(ColDob);
        bool hasAge = cDob != null;
        if (hasAge)
        {
            foreach (var r in rows)
            {
                var dob = AsDate(Get(r, cDob!));
                var on  = cToday != null ? AsDate(Get(r, cToday)) : DateTime.Today;
                r[OutAge] = (dob, on) is ({ } d, { } o) ? YearsBetween(d, o) : null;
            }
            if (cToday == null) notes.Add("Age: no 'today' column found, used the current date.");
        }
        else notes.Add("Age was not calculated: 'Date Of Birth' column not found.");

        var cTwinId = Col(ColTwinId);
        bool hasTwin = cTwinId != null;
        if (hasTwin)
            foreach (var r in rows) r[OutTwin] = Text(r, cTwinId!) != null ? "Yes" : "No";
        else notes.Add("Is Sample Twin was not calculated: twin identifier column not found.");

        var (cRingL, cIndexL, cRingR, cIndexR) = (Col(ColRingL), Col(ColIndexL), Col(ColRingR), Col(ColIndexR));
        bool hasRatioL = cRingL != null && cIndexL != null;
        bool hasRatioR = cRingR != null && cIndexR != null;
        foreach (var r in rows)
        {
            if (hasRatioL) r[OutRatioL] = Ratio(Get(r, cIndexL!), Get(r, cRingL!));
            if (hasRatioR) r[OutRatioR] = Ratio(Get(r, cIndexR!), Get(r, cRingR!));
        }
        if (!hasRatioL) notes.Add("Left hand ratio was not calculated: finger length columns not found.");
        if (!hasRatioR) notes.Add("Right hand ratio was not calculated: finger length columns not found.");

        // ---------------------------------------------------- 5. Column order (calculated columns slot in next to their inputs)
        var order = headers.ToList();
        void InsertAfter(string? anchor, string name)
        {
            int i = anchor == null ? -1 : order.IndexOf(anchor);
            if (i < 0) order.Add(name); else order.Insert(i + 1, name);
        }
        void InsertBefore(string? anchor, string name)
        {
            int i = anchor == null ? -1 : order.IndexOf(anchor);
            if (i < 0) order.Add(name); else order.Insert(i, name);
        }
        if (hasAge)  InsertAfter(cDob, OutAge);
        if (hasTwin) InsertBefore(Col(ColTwinQuestion) ?? cTwinId, OutTwin);
        if (hasRatioL) InsertAfter(cIndexR ?? headers.LastOrDefault(), OutRatioL);
        if (hasRatioR) InsertAfter(hasRatioL ? OutRatioL : (cIndexR ?? headers.LastOrDefault()), OutRatioR);

        // ---------------------------------------------------- 6. Drop "other" columns that ended up empty
        foreach (var c in new[] { cOtherCaste, cOtherEth })
        {
            if (c != null && rows.All(r => Text(r, c) == null))
            {
                order.Remove(c);
                notes.Add($"Removed '{Rename(c)}' column (empty after clean-up).");
            }
        }

        // ---------------------------------------------------- 7. Apply renames and shape the output
        string Rename(string c)
        {
            foreach (var kv in Renames) if (Norm(kv.Key) == Norm(c)) return kv.Value;
            return c;
        }

        var finalOrder = order.Select(Rename).ToList();
        var outRows = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var r in rows)
        {
            var o = new Dictionary<string, object?>(finalOrder.Count);
            foreach (var c in order) o[Rename(c)] = r.TryGetValue(c, out var v) ? v : null;
            outRows.Add(o);
        }

        // ---------------------------------------------------- 8. Number formats for Excel
        var formats = new Dictionary<string, string>();
        foreach (var c in finalOrder)
        {
            var first = outRows.Select(r => r[c]).FirstOrDefault(v => v != null);
            if (first is DateTime) formats[c] = "yyyy-mm-dd";
        }
        if (hasRatioL) formats[OutRatioL] = "0.0000";
        if (hasRatioR) formats[OutRatioR] = "0.0000";

        return new TransformResult(finalOrder, outRows, formats, notes);
    }

    // ================================================================= helpers
    /// Returns the standardised value for a free-text disease entry, or null to keep it as typed.
    /// Entries listing several things ("Uric acid, pcos") are classified part by part: recognised
    /// parts become their category, the other parts are kept, so nothing the person typed is lost.
    public static string? ClassifyDisease(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var result = new List<string>();
        bool anyClassified = false;
        foreach (var part in parts)
        {
            var cat = ClassifyPart(part);
            if (cat != null) anyClassified = true;
            var value = cat ?? part;
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
        }
        return anyClassified ? string.Join(", ", result) : null;
    }

    static string? ClassifyPart(string text)
    {
        foreach (var keep in DiseaseKeepAsIs) if (keep.IsMatch(text)) return null;
        foreach (var (category, pattern) in DiseaseRules) if (pattern.IsMatch(text)) return category;
        return null;
    }

    static object? Get(Dictionary<string, object?> r, string col) => r.TryGetValue(col, out var v) ? v : null;

    static string? Text(Dictionary<string, object?> r, string col) => Get(r, col) switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        var v => Convert.ToString(v, CultureInfo.InvariantCulture)
    };

    static DateTime? AsDate(object? v) => v switch
    {
        DateTime d => d,
        double n   => DateTime.FromOADate(n),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => null
    };

    static double? AsNumber(object? v) => v switch
    {
        double d => d,
        int i    => i,
        long l   => l,
        decimal m=> (double)m,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    /// whole years completed between birth date and the reference date
    static int YearsBetween(DateTime dob, DateTime on)
    {
        int years = on.Year - dob.Year;
        if (dob.Date.AddYears(years) > on.Date) years--;
        return years;
    }

    /// numerator / denominator rounded to 4 decimals (null if not computable)
    static double? Ratio(object? numerator, object? denominator)
    {
        var n = AsNumber(numerator);
        var d = AsNumber(denominator);
        if (n is null || d is null || d == 0) return null;
        return Math.Round(n.Value / d.Value, 4, MidpointRounding.AwayFromZero);
    }

    static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
}
