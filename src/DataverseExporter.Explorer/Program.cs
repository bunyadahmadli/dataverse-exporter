using System.Text;
using DataverseExporter.Core.Settings;
using DataverseExporter.Core.Targets;
using DataverseExporter.SqlServer;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true); // connection string stays out of git

// Built-in target providers. To browse another database, implement IDataExplorer in
// its provider and register it here — see docs/adding-a-provider.md.
var registry = new ProviderRegistry();
registry.Register("SqlServer", s => new SqlServerProvider(s));

builder.Services.AddSingleton<IDataExplorer>(sp =>
{
    var target = sp.GetRequiredService<IConfiguration>().GetSection("Target").Get<TargetSettings>()
        ?? throw new InvalidOperationException("The Target section is missing from appsettings.json.");
    return registry.Create(target).CreateExplorer();
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// All tables containing data (row count descending).
app.MapGet("/api/tables", (IDataExplorer explorer) =>
    explorer.GetTables().Select(t => new
    {
        name = t.Name,
        rowCount = t.RowCount,
        columnCount = t.Columns.Count
    }));

app.MapGet("/api/tables/{table}/columns", (string table, IDataExplorer explorer) =>
    explorer.FindTable(table) is { } t
        ? Results.Ok(t.Columns.Select(c => new { name = c.Name, storeType = c.StoreType, isPrimaryKey = c.IsPrimaryKey }))
        : Results.NotFound(new { error = "Table not found." }));

// Paged, filtered, sorted data.
// Filter: ?f=column|op|value (repeatable; op: eq,ne,gt,gte,lt,lte,contains,startswith,null,notnull)
app.MapGet("/api/tables/{table}/data", (string table, IDataExplorer explorer, HttpRequest request,
    int page = 1, int pageSize = 50, string? sort = null, string dir = "asc", string? columns = null) =>
{
    if (explorer.FindTable(table) is null)
        return Results.NotFound(new { error = "Table not found." });

    try
    {
        var query = BuildQuery(request, page, pageSize, sort, dir, columns);
        var result = explorer.Query(table, query);

        var rows = result.Rows.Select(row =>
        {
            var dict = new Dictionary<string, object?>();
            for (var i = 0; i < result.Columns.Count; i++)
                dict[result.Columns[i].Name] = row[i];
            return dict;
        });

        return Results.Ok(new { total = result.Total, page, pageSize, rows });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// CSV export with the current filters/sorting (up to 100,000 rows).
app.MapGet("/api/tables/{table}/export", (string table, IDataExplorer explorer, HttpRequest request,
    string? sort = null, string dir = "asc", string? columns = null) =>
{
    if (explorer.FindTable(table) is null)
        return Results.NotFound(new { error = "Table not found." });

    IReadOnlyList<ExplorerColumn> exportColumns;
    IEnumerable<object?[]> rows;
    try
    {
        var query = BuildQuery(request, page: 1, pageSize: 50, sort, dir, columns);
        (exportColumns, rows) = explorer.Stream(table, query, maxRows: 100_000);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    return Results.Stream(async stream =>
    {
        // UTF-8 with BOM so Excel opens it correctly.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync(string.Join(',', exportColumns.Select(c => Csv(c.Name))));

        foreach (var row in rows)
        {
            var fields = new string[row.Length];
            for (var i = 0; i < row.Length; i++)
                fields[i] = row[i] is null ? "" : Csv(FormatValue(row[i]!));
            await writer.WriteLineAsync(string.Join(',', fields));
        }
    }, "text/csv; charset=utf-8", $"{table}.csv");
});

// Refresh the schema/row-count cache.
app.MapPost("/api/refresh", (IDataExplorer explorer) =>
{
    explorer.Refresh();
    return Results.Ok(new { tables = explorer.GetTables().Count });
});

app.Run();

static TableQuery BuildQuery(HttpRequest request, int page, int pageSize,
    string? sort, string dir, string? columns)
{
    var filters = request.Query["f"]
        .Where(v => !string.IsNullOrEmpty(v))
        .Select(v =>
        {
            var parts = v!.Split('|', 3);
            if (parts.Length < 2)
                throw new ArgumentException($"Invalid filter format: '{v}' (expected: column|operator|value)");
            return new FilterClause(parts[0], parts[1], parts.Length > 2 ? parts[2] : null);
        })
        .ToList();

    var selectedColumns = string.IsNullOrWhiteSpace(columns)
        ? null
        : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    return new TableQuery(page, pageSize, sort,
        dir.Equals("desc", StringComparison.OrdinalIgnoreCase), selectedColumns, filters);
}

static string FormatValue(object value) => value switch
{
    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
    bool b => b ? "1" : "0",
    _ => value.ToString() ?? ""
};

static string Csv(string field) =>
    field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
        ? $"\"{field.Replace("\"", "\"\"")}\""
        : field;
