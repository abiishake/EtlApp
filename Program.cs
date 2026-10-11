using EtlApp.Etl;
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

// 3) Export the chosen columns of the (filtered) transformed data as .xlsx
app.MapPost("/api/export", (ExportRequest body, SessionStore store, SavedFilterStore saved, HttpContext http, ILogger<Program> log) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null)
        return Results.NotFound(new { error = "Session expired. Please upload the file again." });

    var chosen = new HashSet<string>(body.Columns ?? new List<string>());
    var columns = data.Columns.Where(chosen.Contains).ToList();   // keep the standard column order
    if (columns.Count == 0)
        return Results.BadRequest(new { error = "Select at least one column." });

    List<Dictionary<string, object?>> rows;
    try { rows = Filtering.Apply(data, body.Filters, body.Mode); }
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
        var active = (body.Filters ?? new List<FilterSpec>())
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => f with { Value = f.Value!.Trim() }).ToList();
        var savedOk = false;
        if (active.Count > 0)
        {
            var filterName = body.FilterName?.Trim();
            if (string.IsNullOrEmpty(filterName)) filterName = fileName;          // default: the file name
            if (filterName.Length > 100) filterName = filterName[..100].TrimEnd();
            try { saved.Save(filterName, fileName, string.Equals(body.Mode, "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and", active); savedOk = true; }
            catch (Exception ex) { log.LogWarning(ex, "Could not save filters"); }
        }
        http.Response.Headers["X-Filters-Saved"] = savedOk ? "1" : "0";
    }
    return Results.File(
        bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        fileName);
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
record ExportRequest(string? Id, List<string>? Columns, List<FilterSpec>? Filters, Dictionary<string, string>? Renames, string? Mode, string? FileName, bool? SaveFilters, string? FilterName);
