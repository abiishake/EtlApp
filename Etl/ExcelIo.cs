using ClosedXML.Excel;

namespace EtlApp.Etl;

/// <summary>Reads the uploaded .xlsx and writes the exported .xlsx (ClosedXML, no Office needed).</summary>
public static class ExcelIo
{
    const string DataSheetName = "Research Form - Data";

    public static (List<string> Headers, List<Dictionary<string, object?>> Rows, string Sheet) Read(Stream xlsx)
    {
        using var wb = new XLWorkbook(xlsx);

        // use the data sheet by name, otherwise the first sheet that isn't the instructions sheet
        IXLWorksheet? ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals(DataSheetName, StringComparison.OrdinalIgnoreCase))
                        ?? wb.Worksheets.FirstOrDefault(w => !w.Name.Contains("Instruction", StringComparison.OrdinalIgnoreCase))
                        ?? wb.Worksheets.FirstOrDefault();
        if (ws == null) throw new InvalidDataException("The workbook has no sheets.");

        int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastCol == 0 || lastRow < 1) throw new InvalidDataException($"Sheet '{ws.Name}' is empty.");

        // headers (row 1); skip blank headers, de-duplicate repeated ones
        var headers = new List<string>();
        var colIndex = new List<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 1; c <= lastCol; c++)
        {
            var h = ws.Cell(1, c).GetString().Trim();
            if (h.Length == 0) continue;
            var name = h; int k = 2;
            while (!seen.Add(name)) name = $"{h} ({k++})";
            headers.Add(name);
            colIndex.Add(c);
        }

        var rows = new List<Dictionary<string, object?>>(Math.Max(0, lastRow - 1));
        for (int r = 2; r <= lastRow; r++)
        {
            var row = new Dictionary<string, object?>(headers.Count);
            bool any = false;
            for (int i = 0; i < headers.Count; i++)
            {
                var v = ReadCell(ws.Cell(r, colIndex[i]));
                row[headers[i]] = v;
                if (v != null) any = true;
            }
            if (any) rows.Add(row);          // skip completely empty rows
        }
        return (headers, rows, ws.Name);
    }

    static object? ReadCell(IXLCell c)
    {
        switch (c.DataType)
        {
            case XLDataType.Blank:    return null;
            case XLDataType.DateTime: return c.GetDateTime();
            case XLDataType.Number:   return LooksLikeDate(c) ? c.GetDateTime() : c.GetDouble();
            case XLDataType.Boolean:  return c.GetBoolean();
            default:
                var s = c.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }

    // safety net: a numeric cell formatted as a date (built-in date formats, or a custom y/m/d format)
    static bool LooksLikeDate(IXLCell c)
    {
        var nf = c.Style.NumberFormat;
        int id = nf.NumberFormatId;
        if ((id >= 14 && id <= 22) || (id >= 45 && id <= 47)) return true;
        var f = nf.Format ?? "";
        return f.Length > 0 && f.IndexOfAny(new[] { 'y', 'Y', 'd', 'D' }) >= 0 && f.IndexOfAny(new[] { '0', '#' }) < 0;
    }

    public static byte[] Write(TransformResult data, IReadOnlyList<string> columns, IReadOnlyList<string>? headers = null)
    {
        headers ??= columns;   // header text shown in row 1 (defaults to the column names)
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Transformed Data");

        // header
        for (int c = 0; c < columns.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        }

        // calculated columns that are written as Excel formulas (only when both input columns are in the export)
        var position = new Dictionary<string, int>();
        for (int c = 0; c < columns.Count; c++) position[columns[c]] = c;
        var formulaCols = new (string Num, string Den)?[columns.Count];
        for (int c = 0; c < columns.Count; c++)
            if (data.Formulas != null && data.Formulas.TryGetValue(columns[c], out var f) &&
                position.TryGetValue(f.Numerator, out var ni) && position.TryGetValue(f.Denominator, out var di))
                formulaCols[c] = (XLHelper.GetColumnLetterFromNumber(ni + 1), XLHelper.GetColumnLetterFromNumber(di + 1));

        // body
        var widths = headers.Select(c => Math.Min(c.Length, 40)).ToArray();
        for (int r = 0; r < data.Rows.Count; r++)
        {
            var row = data.Rows[r];
            for (int c = 0; c < columns.Count; c++)
            {
                if (formulaCols[c] is { } fc)
                {
                    // numerator / denominator of the same row; blank when either is empty or the division is not possible
                    int xr = r + 2;
                    var fcell = ws.Cell(xr, c + 1);
                    fcell.FormulaA1 = $"IF(OR({fc.Num}{xr}=\"\",{fc.Den}{xr}=\"\"),\"\",IFERROR({fc.Num}{xr}/{fc.Den}{xr},\"\"))";
                    if (data.Formats.TryGetValue(columns[c], out var ffmt))
                        fcell.Style.NumberFormat.Format = ffmt;
                    widths[c] = Math.Max(widths[c], 10);
                    continue;
                }
                if (!row.TryGetValue(columns[c], out var v) || v == null) continue;
                var cell = ws.Cell(r + 2, c + 1);
                switch (v)
                {
                    case DateTime d:
                        cell.Value = d;
                        widths[c] = Math.Max(widths[c], 11);
                        break;
                    case bool b:
                        cell.Value = b;
                        break;
                    case double or float or int or long or decimal or short:
                        cell.Value = Convert.ToDouble(v);
                        widths[c] = Math.Max(widths[c], 10);
                        break;
                    default:
                        var s = v.ToString() ?? "";
                        cell.Value = s;
                        widths[c] = Math.Max(widths[c], Math.Min(s.Length, 40));
                        break;
                }
                if (data.Formats.TryGetValue(columns[c], out var fmt))
                    cell.Style.NumberFormat.Format = fmt;
            }
        }

        // manual widths (AdjustToContents needs system fonts, which a slim container doesn't have)
        for (int c = 0; c < columns.Count; c++) ws.Column(c + 1).Width = widths[c] + 2;

        ws.SheetView.FreezeRows(1);
        if (columns.Count > 0 && data.Rows.Count > 0)
            ws.Range(1, 1, data.Rows.Count + 1, columns.Count).SetAutoFilter();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
    public sealed record SheetExport(string Name, TransformResult Data, IReadOnlyList<string> Columns, IReadOnlyList<string> Headers);

    public static byte[] WriteMultiple(IReadOnlyList<SheetExport> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("Add at least one output sheet.");
        using var output = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sheets)
        {
            var name = (item.Name ?? string.Empty).Trim();
            if (name.Length == 0) throw new ArgumentException("Every output sheet needs a name.");
            if (name.Length > 31 || name.IndexOfAny(new[] { '[', ']', ':', '*', '?', '/', '\\' }) >= 0)
                throw new ArgumentException($"Invalid Excel sheet name: '{name}'. Names must be 1–31 characters and cannot contain : \\ / ? * [ ].");
            if (!usedNames.Add(name)) throw new ArgumentException($"Duplicate output sheet name '{name}'. Sheet names must be unique.");

            var oneBytes = Write(item.Data, item.Columns, item.Headers);
            using var one = new XLWorkbook(new MemoryStream(oneBytes));
            var source = one.Worksheet(1);
            var target = output.Worksheets.Add(name);
            var lastRow = source.LastRowUsed()?.RowNumber() ?? 1;
            var lastCol = source.LastColumnUsed()?.ColumnNumber() ?? 1;
            source.Range(1, 1, Math.Max(1, lastRow), Math.Max(1, lastCol)).CopyTo(target.Cell(1, 1));
            for (int c = 1; c <= lastCol; c++) target.Column(c).Width = source.Column(c).Width;
            target.SheetView.FreezeRows(1);
        }
        using var ms = new MemoryStream();
        output.SaveAs(ms);
        return ms.ToArray();
    }

}
