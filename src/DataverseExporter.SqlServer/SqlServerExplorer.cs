using System.Globalization;
using DataverseExporter.Core.Targets;
using Microsoft.Data.SqlClient;

namespace DataverseExporter.SqlServer;

/// <summary>
/// Read-side of the SQL Server provider: caches schema + row counts in memory and runs
/// paged/filtered queries. Table and column names are always validated against the cached
/// whitelist and filter values are always sent as SqlParameters — user input never reaches
/// the SQL text, so injection is not possible.
/// </summary>
public class SqlServerExplorer : IDataExplorer
{
    private readonly string _connectionString;
    private readonly string _schema;
    private Dictionary<string, ExplorerTable> _tables = new(StringComparer.OrdinalIgnoreCase);

    public SqlServerExplorer(string connectionString, string schema)
    {
        _connectionString = connectionString;
        _schema = schema;
        Refresh();
    }

    public void Refresh()
    {
        var tables = new Dictionary<string, (long RowCount, List<ExplorerColumn> Columns)>(
            StringComparer.OrdinalIgnoreCase);

        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        // Internal bookkeeping tables (leading underscore) are not listed.
        var rowCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = new SqlCommand("""
            SELECT t.name, SUM(p.rows)
            FROM sys.tables t
            JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            WHERE SCHEMA_NAME(t.schema_id) = @schema AND t.name NOT LIKE '\_%' ESCAPE '\'
            GROUP BY t.name
            """, conn))
        {
            cmd.Parameters.AddWithValue("@schema", _schema);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rowCounts[reader.GetString(0)] = reader.GetInt64(1);
        }

        using (var cmd = new SqlCommand("""
            SELECT t.name, c.name, ty.name, c.max_length, c.precision, c.scale,
                   CASE WHEN ic.column_id IS NOT NULL THEN 1 ELSE 0 END
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_primary_key = 1
            LEFT JOIN sys.index_columns ic ON ic.object_id = t.object_id
                 AND ic.index_id = i.index_id AND ic.column_id = c.column_id
            WHERE SCHEMA_NAME(t.schema_id) = @schema AND t.name NOT LIKE '\_%' ESCAPE '\'
            ORDER BY t.name, c.column_id
            """, conn))
        {
            cmd.Parameters.AddWithValue("@schema", _schema);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var tableName = reader.GetString(0);
                var column = new ExplorerColumn(
                    reader.GetString(1),
                    RenderType(reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5)),
                    reader.GetInt32(6) == 1);

                if (!tables.TryGetValue(tableName, out var entry))
                {
                    entry = (rowCounts.GetValueOrDefault(tableName), new List<ExplorerColumn>());
                    tables[tableName] = entry;
                }
                entry.Columns.Add(column);
            }
        }

        _tables = tables.ToDictionary(
            kv => kv.Key,
            kv => new ExplorerTable(kv.Key, kv.Value.RowCount, kv.Value.Columns),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ExplorerTable> GetTables() =>
        _tables.Values.Where(t => t.RowCount > 0).OrderByDescending(t => t.RowCount).ToList();

    public ExplorerTable? FindTable(string name) => _tables.GetValueOrDefault(name);

    public QueryPage Query(string table, TableQuery query)
    {
        var t = RequireTable(table);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var selected = SelectColumns(t, query.Columns);
        var (orderBy, where, parameters) = BuildQueryParts(t, query);

        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        // Without filters use the cached row count instead of COUNT (no full scan on big tables).
        long total;
        if (query.Filters.Count == 0)
        {
            total = t.RowCount;
        }
        else
        {
            using var countCmd = new SqlCommand(
                $"SELECT COUNT_BIG(*) FROM {Quoted(t.Name)} {where};", conn) { CommandTimeout = 120 };
            countCmd.Parameters.AddRange(parameters.Select(p => new SqlParameter(p.ParameterName, p.Value)).ToArray());
            total = (long)countCmd.ExecuteScalar()!;
        }

        var columnList = string.Join(", ", selected.Select(c => $"[{Escape(c.Name)}]"));
        using var cmd = new SqlCommand($"""
            SELECT {columnList} FROM {Quoted(t.Name)} {where}
            {orderBy}
            OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY;
            """, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddRange(parameters.ToArray());
        cmd.Parameters.AddWithValue("@off", (long)(page - 1) * pageSize);
        cmd.Parameters.AddWithValue("@ps", pageSize);

        var rows = new List<object?[]>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[selected.Count];
            for (var i = 0; i < selected.Count; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return new QueryPage(total, selected, rows);
    }

    public (IReadOnlyList<ExplorerColumn> Columns, IEnumerable<object?[]> Rows) Stream(
        string table, TableQuery query, int maxRows)
    {
        var t = RequireTable(table);
        var selected = SelectColumns(t, query.Columns);
        var (orderBy, where, parameters) = BuildQueryParts(t, query);

        return (selected, StreamRows(t, selected, orderBy, where, parameters, maxRows));
    }

    private IEnumerable<object?[]> StreamRows(ExplorerTable t, List<ExplorerColumn> selected,
        string orderBy, string where, List<SqlParameter> parameters, int maxRows)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        var columnList = string.Join(", ", selected.Select(c => $"[{Escape(c.Name)}]"));
        using var cmd = new SqlCommand($"""
            SELECT TOP ({maxRows}) {columnList} FROM {Quoted(t.Name)} {where}
            {orderBy};
            """, conn) { CommandTimeout = 600 };
        cmd.Parameters.AddRange(parameters.ToArray());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[selected.Count];
            for (var i = 0; i < selected.Count; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return row;
        }
    }

    private ExplorerTable RequireTable(string name) =>
        FindTable(name) ?? throw new ArgumentException($"Unknown table: '{name}'");

    private (string OrderBy, string Where, List<SqlParameter> Parameters) BuildQueryParts(
        ExplorerTable t, TableQuery query)
    {
        var sortColumn = t.FindColumn(query.SortColumn) ?? t.PrimaryKey ?? t.Columns[0];
        var orderBy = $"ORDER BY [{Escape(sortColumn.Name)}] {(query.Descending ? "DESC" : "ASC")}";
        var (where, parameters) = BuildWhere(t, query.Filters);
        return (orderBy, where, parameters);
    }

    private static List<ExplorerColumn> SelectColumns(ExplorerTable table, IReadOnlyList<string>? requested)
    {
        if (requested == null || requested.Count == 0)
            return table.Columns.ToList();

        var selected = requested
            .Select(table.FindColumn)
            .Where(c => c != null)
            .Select(c => c!)
            .Distinct()
            .ToList();
        return selected.Count > 0 ? selected : table.Columns.ToList();
    }

    /// <summary>Turns "column|operator|value" filters into a parameterized WHERE clause.</summary>
    private static (string Where, List<SqlParameter> Parameters) BuildWhere(
        ExplorerTable table, IReadOnlyList<FilterClause> filters)
    {
        var clauses = new List<string>();
        var parameters = new List<SqlParameter>();
        var index = 0;

        foreach (var filter in filters)
        {
            var column = table.FindColumn(filter.Column)
                ?? throw new ArgumentException($"Unknown column: '{filter.Column}'");
            var op = filter.Operator.ToLowerInvariant();

            if (op is "null" or "notnull")
            {
                clauses.Add($"[{Escape(column.Name)}] IS {(op == "null" ? "" : "NOT ")}NULL");
                continue;
            }

            if (string.IsNullOrEmpty(filter.Value))
                throw new ArgumentException($"Missing value for the '{column.Name}' filter.");

            var parameter = new SqlParameter($"@f{index++}", ConvertValue(column, op, filter.Value));
            parameters.Add(parameter);

            clauses.Add(op switch
            {
                "eq" => $"[{Escape(column.Name)}] = {parameter.ParameterName}",
                "ne" => $"[{Escape(column.Name)}] <> {parameter.ParameterName}",
                "gt" => $"[{Escape(column.Name)}] > {parameter.ParameterName}",
                "gte" => $"[{Escape(column.Name)}] >= {parameter.ParameterName}",
                "lt" => $"[{Escape(column.Name)}] < {parameter.ParameterName}",
                "lte" => $"[{Escape(column.Name)}] <= {parameter.ParameterName}",
                "contains" or "startswith" => $"[{Escape(column.Name)}] LIKE {parameter.ParameterName} ESCAPE '\\'",
                _ => throw new ArgumentException($"Unknown operator: '{op}'")
            });
        }

        return (clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses), parameters);
    }

    private static object ConvertValue(ExplorerColumn column, string op, string value)
    {
        if (op is "contains" or "startswith")
        {
            var escaped = value.Replace("\\", "\\\\").Replace("%", "\\%")
                               .Replace("_", "\\_").Replace("[", "\\[");
            return op == "contains" ? $"%{escaped}%" : $"{escaped}%";
        }

        var baseType = column.StoreType.Split('(')[0];
        try
        {
            return baseType switch
            {
                "uniqueidentifier" => Guid.Parse(value),
                "int" => int.Parse(value, CultureInfo.InvariantCulture),
                "bigint" => long.Parse(value, CultureInfo.InvariantCulture),
                "bit" => value is "1" or "true" or "True",
                "decimal" => decimal.Parse(NormalizeDecimal(value), CultureInfo.InvariantCulture),
                "float" => double.Parse(NormalizeDecimal(value), CultureInfo.InvariantCulture),
                "datetime2" => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => value
            };
        }
        catch (FormatException)
        {
            throw new ArgumentException(
                $"Value '{value}' cannot be converted to the type of column '{column.Name}' ({column.StoreType}).");
        }
    }

    /// <summary>Accept comma as the decimal separator too ("12,5" → "12.5").</summary>
    private static string NormalizeDecimal(string value) =>
        value.Contains(',') && !value.Contains('.') ? value.Replace(',', '.') : value;

    private string Quoted(string tableName) => $"[{Escape(_schema)}].[{Escape(tableName)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]");

    private static string RenderType(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "nvarchar" => maxLength == -1 ? "nvarchar(max)" : $"nvarchar({maxLength / 2})",
        "decimal" => $"decimal({precision},{scale})",
        _ => type
    };
}
