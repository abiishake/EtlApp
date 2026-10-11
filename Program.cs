using EtlApp.Etl;
using Microsoft.AspNetCore.Http.Features;

const long MaxUploadBytes = 50L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);
builder.Services.AddSingleton<SessionStore>();

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
        var rows = Filtering.Apply(data, body.Filters);
        return Results.Ok(new { count = rows.Count, total = data.Rows.Count });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// 3) Export the chosen columns of the (filtered) transformed data as .xlsx
app.MapPost("/api/export", (ExportRequest body, SessionStore store) =>
{
    var data = store.Get(body.Id ?? "");
    if (data == null)
        return Results.NotFound(new { error = "Session expired. Please upload the file again." });

    var chosen = new HashSet<string>(body.Columns ?? new List<string>());
    var columns = data.Columns.Where(chosen.Contains).ToList();   // keep the standard column order
    if (columns.Count == 0)
        return Results.BadRequest(new { error = "Select at least one column." });

    List<Dictionary<string, object?>> rows;
    try { rows = Filtering.Apply(data, body.Filters); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

    if (rows.Count == 0)
        return Results.BadRequest(new { error = "No rows match the filters." });

    var bytes = ExcelIo.Write(data with { Rows = rows }, columns);
    return Results.File(
        bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"Research_Form_Transformed_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
});

app.Run();

record CountRequest(string? Id, List<FilterSpec>? Filters);
record ExportRequest(string? Id, List<string>? Columns, List<FilterSpec>? Filters);
