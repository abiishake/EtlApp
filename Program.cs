using EtlApp.Etl;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

const long MaxUploadBytes = 20L * 1024 * 1024;

// `dotnet EtlApp.dll --healthcheck` is the container's HEALTHCHECK (the runtime image has no curl / wget)
if (args.Contains("--healthcheck"))
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var res = await http.GetAsync("http://127.0.0.1:8080/healthz");
        return res.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SavedFilterStore>();

var app = builder.Build();

// Optional password (APP_PASSWORD): the browser asks once, any user name works. Off when the variable is empty.
var password = app.Configuration["APP_PASSWORD"];
if (!string.IsNullOrEmpty(password))
{
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/healthz")) { await next(); return; }
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var given = decoded[(decoded.IndexOf(':') + 1)..];
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), expected)) { await next(); return; }
            }
            catch (FormatException) { }
        }
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Research Form ETL\", charset=\"UTF-8\"";
    });
}

app.UseDefaultFiles();   // serves wwwroot/index.html at "/"
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok("ok"));

// only one workbook is parsed at a time: parsing is the memory-hungry step
var uploadGate = new SemaphoreSlim(1, 1);

// 1) Upload the workbook -> renames + calculated columns (no clean-up rules) -> return columns (with types + distinct values for the filters)
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

    if (!await uploadGate.WaitAsync(TimeSpan.FromSeconds(60)))
        return Results.Json(new { error = "The server is busy with another upload. Please try again in a moment." }, statusCode: 503);
    try
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        var (headers, rows, sheet) = ExcelIo.Read(ms, SessionStore.MaxCellsPerUpload);
        var result = Transformer.Run(headers, rows);
        var id = store.Add(file.FileName, sheet, headers, rows, result);
        return Results.Ok(SessionInfo(id, store.GetEntry(id)!));
    }
    catch (InvalidDataException ex)          // our own, readable messages (empty / too large sheet)
    {
        log.LogWarning(ex, "Upload rejected");
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Upload failed");
        return Results.BadRequest(new { error = "The file could not be read as an Excel workbook (.xlsx). Make sure it is not password-protected or damaged." });
    }
    finally { uploadGate.Release(); }
});

// 1a) Current state of an upload (columns change when clean-up rules are switched on / off on the clean-up page)
app.MapGet("/api/session/{id}", (string id, SessionStore store) =>
{
    var e = store.GetEntry(id);
    return e == null ? Expired() : Results.Ok(SessionInfo(id, e));
});

// 1b) Every cell the clean-up rules changed, original vs new (used by changes.html)
app.MapGet("/api/changes/{id}", (string id, SessionStore store) =>
{
    var e = store.GetEntry(id);
    if (e == null) return Expired();
    return Results.Ok(new { notes = e.Data.Notes, changes = e.Data.Changes ?? new List<CellChange>(), cleanup = e.Cleanup, fileName = e.FileName });
});

// 1c) Optional clean-up rules: what each would change (nothing is applied here)
app.MapPost("/api/cleanup/{id}/preview", (string id, CleanupRequest body, SessionStore store) =>
{
    var e = store.GetEntry(id);
    if (e == null) return Expired();
    var selected = KnownRules(body.Rules);

    // every value rule on, to show what each one would change; value rules touch different columns, so they do not interfere
    var valueRules = CleanupRules.All.Select(r => r.Key).Where(k => k != CleanupRules.DropEmptyOther).ToList();
    var all = Transformer.Run(e.Headers, e.Raw, valueRules);
    // which "Other" columns the drop rule would remove, given the rules currently ticked
    var withoutDrop = Transformer.Run(e.Headers, e.Raw, selected.Where(k => k != CleanupRules.DropEmptyOther).ToList());
    var withDrop = Transformer.Run(e.Headers, e.Raw, selected.Where(k => k != CleanupRules.DropEmptyOther).Append(CleanupRules.DropEmptyOther).ToList());
    var removed = withoutDrop.Columns.Except(withDrop.Columns).ToList();

    var rules = CleanupRules.All.Select(r =>
    {
        var changes = (all.Changes ?? new()).Where(c => c.RuleKey == r.Key).ToList();
        var mappings = changes
            .GroupBy(c => (c.Column, c.Original, c.Updated))
            .Select(g => new { column = g.Key.Column, original = g.Key.Original, updated = g.Key.Updated, count = g.Count() })
            .OrderByDescending(m => m.count).ThenBy(m => m.original, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new
        {
            key = r.Key, title = r.Title, description = r.Description,
            applied = e.Cleanup.Contains(r.Key),
            changedRows = changes.Select(c => c.RowNumber).Distinct().Count(),
            changedCells = changes.Count,
            mappings = mappings.Take(500).ToList(),
            mappingCount = mappings.Count,
            removedColumns = r.Key == CleanupRules.DropEmptyOther ? removed : null
        };
    }).ToList();
    return Results.Ok(new { fileName = e.FileName, version = e.Version, rules });
});

// 1d) Apply the chosen clean-up rules (rebuilt from the uploaded rows, so unticking a rule undoes it)
app.MapPost("/api/cleanup/{id}", (string id, CleanupRequest body, SessionStore store) =>
{
    var e = store.GetEntry(id);
    if (e == null) return Expired();
    var unknown = (body.Rules ?? new()).Where(k => !CleanupRules.IsKnown(k)).ToList();
    if (unknown.Count > 0) return Results.BadRequest(new { error = $"Unknown clean-up rule: {string.Join(", ", unknown)}." });
    var rules = KnownRules(body.Rules);
    e.SetCleanup(rules, Transformer.Run(e.Headers, e.Raw, rules));
    return Results.Ok(SessionInfo(id, e));
});

// 2) How many rows match the current filters?
app.MapPost("/api/count", (CountRequest body, SessionStore store) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null) return Expired();
    try
    {
        var rows = Filtering.Apply(data, body.Filters, body.Mode);
        return Results.Ok(new { count = rows.Count, total = data.Rows.Count });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 2b) First few rows of the (filtered) data with the chosen columns, for the preview table
app.MapPost("/api/preview", (PreviewRequest body, SessionStore store) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null) return Expired();

    var columns = OrderedColumns(data.Columns, body.Columns);
    try
    {
        var rows = SortRows(Filtering.Apply(data, body.Filters, body.Mode), body.Sorts, data.Columns);
        var limit = Math.Clamp(body.Limit ?? 10, 1, 50);
        var headers = OutputHeaders(columns, body.Renames);
        var sample = rows.Take(limit)
            .Select(r => columns.Select(c => PreviewText(r.TryGetValue(c, out var v) ? v : null)).ToList())
            .ToList();
        return Results.Ok(new { headers, rows = sample, total = rows.Count });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 3) Export: each sheet has independent filters, columns, renames, sorts and output name.
app.MapPost("/api/export-multiple", (MultiExportRequest body, SessionStore store, SavedFilterStore saved, HttpContext http, ILogger<Program> log) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null) return Expired();
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
        var pendingSaves = new List<Action>();
        foreach (var sheet in body.Sheets)
        {
            var sheetName = (sheet.SheetName ?? "").Trim();
            var cols = OrderedColumns(data.Columns, sheet.Columns);
            if (cols.Count == 0) throw new ArgumentException($"Sheet '{sheetName}' has no selected columns.");
            var rows = SortRows(Filtering.Apply(data, sheet.Filters, sheet.Mode), sheet.Sorts, data.Columns);
            if (rows.Count == 0) throw new ArgumentException($"No rows match the filters for sheet '{sheetName}'.");
            var headers = OutputHeaders(cols, sheet.Renames);
            var dup = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"Sheet '{sheetName}' has duplicate column name '{dup.Key}' after renaming.");
            exports.Add(new ExcelIo.SheetExport(sheetName, data with { Rows = rows }, cols, headers));

            var active = Filtering.Active(sheet.Filters);
            if (sheet.SaveFilters == true && active.Count > 0)
            {
                var filterName = sheet.FilterName!.Trim();
                if (filterName.Length > 100)
                    throw new ArgumentException($"Filter name for sheet '{sheetName}' must be 100 characters or fewer.");
                var keptRenames = new Dictionary<string, string>();       // only columns that actually got a new header
                for (var i = 0; i < cols.Count; i++)
                    if (headers[i] != cols[i]) keptRenames[cols[i]] = headers[i];
                var mode = string.Equals(sheet.Mode, "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and";
                pendingSaves.Add(() => saved.Save(filterName, sheetName.Length > 0 ? sheetName : "Sheet", mode, active, cols, keptRenames, sheet.Sorts, sheet.ColumnOrder));
            }
        }
        var bytes = ExcelIo.WriteMultiple(exports);
        var failedSaves = 0;
        foreach (var save in pendingSaves)
        {
            try { save(); }
            catch (Exception ex) { failedSaves++; log.LogWarning(ex, "Could not save filters"); }
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

// 5) Saved setups (all output sheets with their filters / columns), kept on the server so every device sees them
app.MapGet("/api/workflows", (SavedFilterStore saved, ILogger<Program> log) =>
{
    try
    {
        return Results.Ok(saved.ListWorkflows().Select(w => new
        {
            id = w.Id, name = w.Name, savedAt = w.SavedAt,
            workflow = JsonSerializer.Deserialize<JsonElement>(w.Json)
        }));
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Could not read saved setups");
        return Results.Json(new { error = "Saved setups are unavailable." }, statusCode: 500);
    }
});

app.MapPost("/api/workflows", (WorkflowRequest body, SavedFilterStore saved, ILogger<Program> log) =>
{
    var name = body.Name?.Trim() ?? "";
    if (name.Length == 0) return Results.BadRequest(new { error = "Enter a name for the setup." });
    if (name.Length > 100) return Results.BadRequest(new { error = "Setup names must be 100 characters or fewer." });
    if (body.Workflow is not { ValueKind: JsonValueKind.Object } wf) return Results.BadRequest(new { error = "Nothing to save." });
    var json = wf.GetRawText();
    if (json.Length > 1_000_000) return Results.BadRequest(new { error = "This setup is too large to save." });
    try { saved.SaveWorkflow(name, json); return Results.Ok(new { name }); }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Could not save setup");
        return Results.Json(new { error = "Could not save the setup on the server." }, statusCode: 500);
    }
});

app.MapDelete("/api/workflows/{id:long}", (long id, SavedFilterStore saved, ILogger<Program> log) =>
{
    try { return saved.DeleteWorkflow(id) ? Results.Ok() : Results.NotFound(new { error = "Not found." }); }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Could not delete setup");
        return Results.Json(new { error = "Could not delete." }, statusCode: 500);
    }
});

static IResult Expired() => Results.NotFound(new { error = "This upload has expired (uploads are kept for 1 hour after last use). Please upload the file again." });

static object SessionInfo(string id, SessionStore.Entry e)
{
    var data = e.Data;
    return new
    {
        id,
        fileName = e.FileName,
        sheet = e.Sheet,
        rowCount = data.Rows.Count,
        columns = data.Columns,
        columnInfo = Filtering.Describe(data),
        notes = data.Notes,
        version = e.Version,
        cleanup = e.Cleanup,
        changeCount = data.Changes?.Count ?? 0
    };
}

// the requested rules in the canonical order, unknown / repeated ones dropped
static List<string> KnownRules(List<string>? rules) =>
    CleanupRules.All.Select(r => r.Key).Where(k => rules != null && rules.Contains(k)).ToList();

// header shown / exported for each column: the user's rename, or the column name itself
static List<string> OutputHeaders(List<string> columns, Dictionary<string, string>? renames) =>
    columns.Select(c => renames != null && renames.TryGetValue(c, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : c).ToList();

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
return 0;

record CountRequest(string? Id, List<FilterSpec>? Filters, string? Mode);
record PreviewRequest(string? Id, List<string>? Columns, List<FilterSpec>? Filters, Dictionary<string, string>? Renames, string? Mode, int? Limit, List<SortSpec>? Sorts);
record CleanupRequest(List<string>? Rules);
record WorkflowRequest(string? Name, JsonElement? Workflow);

record MultiExportRequest(string? Id, string? FileName, List<SheetExportRequest>? Sheets);
record SheetExportRequest(string? SheetName, List<string>? Columns, List<FilterSpec>? Filters, Dictionary<string, string>? Renames, string? Mode, bool? SaveFilters, string? FilterName, List<SortSpec>? Sorts, List<string>? ColumnOrder = null);
public sealed record SortSpec(string? Column, string? Dir);

// Compares cell values for sorting: blanks last (in both directions), then numbers (including number-like text, so "9" < "10"),
// dates, booleans, text (case-insensitive).
public sealed class CellComparer(bool desc) : IComparer<object?>
{
    static bool Blank(object? v) => v == null || (v is string s && string.IsNullOrWhiteSpace(s));

    static bool AsNumber(object v, out double n)
    {
        switch (v)
        {
            case double or int or long or decimal or float or short: n = Convert.ToDouble(v, CultureInfo.InvariantCulture); return true;
            case string s: return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
            default: n = 0; return false;
        }
    }

    static int Rank(object v, out double n) => AsNumber(v, out n) ? 0 : v switch { DateTime => 1, bool => 2, _ => 3 };

    public int Compare(object? a, object? b)
    {
        bool ba = Blank(a), bb = Blank(b);
        if (ba || bb) return ba == bb ? 0 : ba ? 1 : -1;
        int ra = Rank(a!, out var na), rb = Rank(b!, out var nb);
        int r = ra != rb ? ra.CompareTo(rb)
            : ra == 0 ? na.CompareTo(nb)
            : ra == 1 ? ((DateTime)a!).CompareTo((DateTime)b!)
            : ra == 2 ? ((bool)a!).CompareTo((bool)b!)
            : StringComparer.OrdinalIgnoreCase.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
        return desc ? -r : r;
    }
}
