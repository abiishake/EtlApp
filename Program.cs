using EtlApp.Etl;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;

const long MaxUploadBytes = 50L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SavedFilterStore>();

var app = builder.Build();

app.UseDefaultFiles();   // serves wwwroot/index.html at "/"
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok("ok"));

// 1) Upload the workbook -> run the ETL -> return columns (with types + distinct values for the filters)
app.MapPost("/api/upload", async (HttpRequest req, SessionStore store, ILogger<Program> log) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "Please upload an .xlsx file." });

    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No file received." });
    if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .xlsx files are supported." });

    try
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        var (headers, rows, sheet) = ExcelIo.Read(ms);
        var result = Transformer.Run(headers, rows);
        var id = store.Add(result);

        return Results.Ok(new
        {
            id,
            fileName = file.FileName,
            sheet,
            rowCount = result.Rows.Count,
            columns = result.Columns,
            columnInfo = Filtering.Describe(result),
            notes = result.Notes
        });
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Upload failed");
        return Results.BadRequest(new { error = $"Could not process the file: {ex.Message}" });
    }
});

// 2) How many rows match the current filters?
app.MapPost("/api/count", (CountRequest body, SessionStore store) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null)
        return Results.NotFound(new { error = "Session expired. Please upload the file again." });

    try
    {
        var rows = Filtering.Apply(data, body.Filters, body.Mode);
        return Results.Ok(new { count = rows.Count, total = data.Rows.Count });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 1b) Every cell the clean-up rules changed, original vs new (used by changes.html)
app.MapGet("/api/changes/{id}", (string id, SessionStore store) =>
{
    var data = store.Get(id);
    if (data == null)
        return Results.NotFound(new { error = "This upload has expired. Please upload the file again." });
    return Results.Ok(new { notes = data.Notes, changes = data.Changes ?? new List<CellChange>() });
});

// 2b) First few rows of the (filtered) data with the chosen columns, for the preview table
app.MapPost("/api/preview", (PreviewRequest body, SessionStore store) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null)
        return Results.NotFound(new { error = "Session expired. Please upload the file again." });

    var columns = OrderedColumns(data.Columns, body.Columns);
    try
    {
        var rows = SortRows(Filtering.Apply(data, body.Filters, body.Mode), body.Sorts, data.Columns);
        var limit = Math.Clamp(body.Limit ?? 10, 1, 50);
        var headers = columns.Select(c =>
            body.Renames != null && body.Renames.TryGetValue(c, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : c).ToList();
        var sample = rows.Take(limit)
            .Select(r => columns.Select(c => PreviewText(r.TryGetValue(c, out var v) ? v : null)).ToList())
            .ToList();
        return Results.Ok(new { headers, rows = sample, total = rows.Count });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 3) Export the chosen columns of the (filtered) transformed data as .xlsx
app.MapPost("/api/export", (ExportRequest body, SessionStore store, SavedFilterStore saved, HttpContext http, ILogger<Program> log) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null)
        return Results.NotFound(new { error = "Session expired. Please upload the file again." });

    var columns = OrderedColumns(data.Columns, body.Columns);   // Id is always exported; the user's column order is kept
    if (columns.Count == 0)
        return Results.BadRequest(new { error = "Select at least one column." });

    List<Dictionary<string, object?>> rows;
    try { rows = SortRows(Filtering.Apply(data, body.Filters, body.Mode), body.Sorts, data.Columns); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

    if (rows.Count == 0)
        return Results.BadRequest(new { error = "No rows match the filters." });

    // optional header renames (export only; data lookup still uses the original column names)
    var headers = columns.Select(c =>
        body.Renames != null && body.Renames.TryGetValue(c, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : c).ToList();
    var dup = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
    if (dup != null)
        return Results.BadRequest(new { error = $"Duplicate column name \"{dup.Key}\" after renaming. Please use unique names." });

    var bytes = ExcelIo.Write(data with { Rows = rows }, columns, headers);
    // optional custom file name; the extension is always .xlsx (any typed extension is dropped)
    var baseName = SanitizeFileName(body.FileName);
    var fileName = baseName != null ? $"{baseName}.xlsx" : $"Research_Form_Transformed_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
    // optionally remember the filters under the downloaded file name (a failure here must not block the download)
    if (body.SaveFilters == true)
    {
        var active = Filtering.Active(body.Filters);
        var savedOk = false;
        if (active.Count > 0)
        {
            var filterName = body.FilterName?.Trim();
            if (string.IsNullOrEmpty(filterName)) filterName = fileName;          // default: the file name
            if (filterName.Length > 100) filterName = filterName[..100].TrimEnd();
            var keptRenames = new Dictionary<string, string>();       // only columns that actually got a new header
            for (var i = 0; i < columns.Count; i++)
                if (headers[i] != columns[i]) keptRenames[columns[i]] = headers[i];
            try { saved.Save(filterName, fileName, string.Equals(body.Mode, "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and", active, columns, keptRenames); savedOk = true; }
            catch (Exception ex) { log.LogWarning(ex, "Could not save filters"); }
        }
        http.Response.Headers["X-Filters-Saved"] = savedOk ? "1" : "0";
    }
    return Results.File(
        bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        fileName);
});

// Multi-sheet export: each sheet has independent filters, columns, renames, and output name.
app.MapPost("/api/export-multiple", (MultiExportRequest body, SessionStore store, SavedFilterStore saved, HttpContext http, ILogger<Program> log) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null) return Results.NotFound(new { error = "Session expired. Please upload the file again." });
    if (body.Sheets == null || body.Sheets.Count == 0) return Results.BadRequest(new { error = "Add at least one output sheet." });
    try
    {
        var requestedFilterNames = body.Sheets
            .Where(sheet => sheet.SaveFilters == true && Filtering.Active(sheet.Filters).Count > 0)
            .Select(sheet => sheet.FilterName?.Trim() ?? "")
            .ToList();
        if (requestedFilterNames.Any(string.IsNullOrWhiteSpace))
            return Results.BadRequest(new { error = "Enter a filter name for every sheet where Remember these filters is selected." });
        var duplicateFilterName = requestedFilterNames
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFilterName != null)
            return Results.BadRequest(new { error = $"The filter name '{duplicateFilterName.Key}' is used by more than one sheet. Give each saved filter a unique name." });

        var exports = new List<ExcelIo.SheetExport>();
        // filters are remembered only after the workbook was built successfully (a failed export must not leave half-saved filters)
        var pendingSaves = new List<(string Name, string Label, string Mode, List<FilterSpec> Filters, List<string> Columns, Dictionary<string, string> Renames)>();
        foreach (var sheet in body.Sheets)
        {
            var cols = OrderedColumns(data.Columns, sheet.Columns);
            if (cols.Count == 0) throw new ArgumentException($"Sheet '{sheet.SheetName}' has no selected columns.");
            var rows = SortRows(Filtering.Apply(data, sheet.Filters, sheet.Mode), sheet.Sorts, data.Columns);
            if (rows.Count == 0) throw new ArgumentException($"No rows match the filters for sheet '{sheet.SheetName}'.");
            var headers = cols.Select(c => sheet.Renames != null && sheet.Renames.TryGetValue(c, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : c).ToList();
            var dup = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"Sheet '{sheet.SheetName}' has duplicate column name '{dup.Key}' after renaming.");
            exports.Add(new ExcelIo.SheetExport(sheet.SheetName ?? "", data with { Rows = rows }, cols, headers));
            if (sheet.SaveFilters == true)
            {
                var active = Filtering.Active(sheet.Filters);
                if (active.Count > 0)
                {
                    // Save using the exact filter name entered for this sheet; do not append the sheet name.
                    var filterName = sheet.FilterName?.Trim();
                    if (string.IsNullOrWhiteSpace(filterName))
                        throw new ArgumentException($"Enter a filter name for sheet '{sheet.SheetName}'.");
                    if (filterName.Length > 100)
                        throw new ArgumentException($"Filter name for sheet '{sheet.SheetName}' must be 100 characters or fewer.");
                    var sheetName = (sheet.SheetName ?? "Sheet").Trim();
                    var keptRenames = new Dictionary<string, string>();
                    for (var i = 0; i < cols.Count; i++)
                        if (headers[i] != cols[i]) keptRenames[cols[i]] = headers[i];
                    pendingSaves.Add((filterName, sheetName, string.Equals(sheet.Mode, "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and", active, cols, keptRenames));
                }
            }
        }
        var bytes = ExcelIo.WriteMultiple(exports);
        var failedSaves = 0;
        foreach (var ps in pendingSaves)
        {
            try { saved.Save(ps.Name, ps.Label, ps.Mode, ps.Filters, ps.Columns, ps.Renames); }
            catch (Exception ex) { failedSaves++; log.LogWarning(ex, "Could not save filters '{FilterName}'", ps.Name); }
        }
        if (pendingSaves.Count > 0) http.Response.Headers["X-Filters-Save-Failed"] = failedSaves.ToString();   // the page shows a warning when > 0
        var baseName = SanitizeFileName(body.FileName);
        var fileName = baseName != null ? $"{baseName}.xlsx" : $"Transformed_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) { log.LogError(ex, "Multi-sheet export failed"); return Results.Problem("Could not create the workbook."); }
});

// 4) Filters remembered from earlier downloads (newest first)
app.MapGet("/api/saved-filters", (SavedFilterStore saved, ILogger<Program> log) =>
{
    try { return Results.Ok(saved.List()); }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Could not read saved filters");
        return Results.Json(new { error = "Saved filters are unavailable." }, statusCode: 500);
    }
});

app.MapDelete("/api/saved-filters/{id:long}", (long id, SavedFilterStore saved, ILogger<Program> log) =>
{
    try { return saved.Delete(id) ? Results.Ok() : Results.NotFound(new { error = "Not found." }); }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Could not delete saved filter");
        return Results.Json(new { error = "Could not delete." }, statusCode: 500);
    }
});

// Columns in the order the user arranged them (unknown names dropped, duplicates removed); Id is always included, first if the user did not place it.
static List<string> OrderedColumns(IReadOnlyList<string> all, List<string>? requested)
{
    var have = new HashSet<string>(all);
    var cols = (requested ?? new List<string>()).Where(have.Contains).Distinct().ToList();
    if (have.Contains(Transformer.IdColumn) && !cols.Contains(Transformer.IdColumn)) cols.Insert(0, Transformer.IdColumn);
    return cols;
}

// Multi-level sort (first entry = primary). Blank cells always go last; numbers, dates and text compare naturally.
static List<Dictionary<string, object?>> SortRows(List<Dictionary<string, object?>> rows, List<SortSpec>? sorts, IReadOnlyList<string> all)
{
    var have = new HashSet<string>(all);
    var levels = (sorts ?? new List<SortSpec>())
        .Where(s => !string.IsNullOrWhiteSpace(s.Column) && have.Contains(s.Column!))
        .GroupBy(s => s.Column).Select(g => g.First()).ToList();
    if (levels.Count == 0 || rows.Count < 2) return rows;
    IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
    foreach (var lv in levels)
    {
        var col = lv.Column!;
        var cmp = new CellComparer(string.Equals(lv.Dir, "desc", StringComparison.OrdinalIgnoreCase));
        Func<Dictionary<string, object?>, object?> key = r => r.TryGetValue(col, out var v) ? v : null;
        ordered = ordered == null ? rows.OrderBy(key, cmp) : ordered.ThenBy(key, cmp);   // stable: ties keep the file order
    }
    return ordered!.ToList();
}

static string PreviewText(object? v) => v switch
{
    null => "",
    DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm"),
    double x => x.ToString("0.####", CultureInfo.InvariantCulture),
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
    _ => v.ToString() ?? ""
};

static string? SanitizeFileName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return null;
    var n = name.Trim();
    if (n.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) n = n[..^5];
    n = new string(n.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch) && !char.IsControl(ch)).ToArray());
    n = n.Trim().TrimEnd('.');
    if (n.Length > 100) n = n[..100].TrimEnd();
    return n.Length == 0 ? null : n;
}

app.Run();

record CountRequest(string? Id, List<FilterSpec>? Filters, string? Mode);
record PreviewRequest(string? Id, List<string>? Columns, List<FilterSpec>? Filters, Dictionary<string, string>? Renames, string? Mode, int? Limit, List<SortSpec>? Sorts);
record ExportRequest(string? Id, List<string>? Columns, List<FilterSpec>? Filters, Dictionary<string, string>? Renames, string? Mode, string? FileName, bool? SaveFilters, string? FilterName, List<SortSpec>? Sorts);

record MultiExportRequest(string? Id, string? FileName, List<SheetExportRequest>? Sheets);
record SheetExportRequest(string? SheetName, List<string>? Columns, List<FilterSpec>? Filters, Dictionary<string, string>? Renames, string? Mode, bool? SaveFilters, string? FilterName, List<SortSpec>? Sorts);
record SortSpec(string? Column, string? Dir);

// Compares cell values for sorting: blanks last (in both directions), then numbers, dates, booleans, text (case-insensitive).
sealed class CellComparer(bool desc) : IComparer<object?>
{
    static bool Blank(object? v) => v == null || (v is string s && string.IsNullOrWhiteSpace(s));
    static int Rank(object v) => v switch { double or int or long or decimal or float => 0, DateTime => 1, bool => 2, _ => 3 };
    public int Compare(object? a, object? b)
    {
        bool ba = Blank(a), bb = Blank(b);
        if (ba || bb) return ba == bb ? 0 : ba ? 1 : -1;
        int ra = Rank(a!), rb = Rank(b!);
        int r = ra != rb ? ra.CompareTo(rb)
            : ra == 0 ? Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture))
            : ra == 1 ? ((DateTime)a!).CompareTo((DateTime)b!)
            : ra == 2 ? ((bool)a!).CompareTo((bool)b!)
            : StringComparer.OrdinalIgnoreCase.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
        return desc ? -r : r;
    }
}
