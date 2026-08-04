namespace DataverseExporter.Core.Targets;

public record ExplorerColumn(string Name, string StoreType, bool IsPrimaryKey);

public record ExplorerTable(string Name, long RowCount, IReadOnlyList<ExplorerColumn> Columns)
{
    public ExplorerColumn? PrimaryKey => Columns.FirstOrDefault(c => c.IsPrimaryKey);

    public ExplorerColumn? FindColumn(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One filter clause. Operators: eq, ne, gt, gte, lt, lte, contains, startswith, null, notnull.
/// The raw string value is converted to the column's type by the provider.
/// </summary>
public record FilterClause(string Column, string Operator, string? Value);

/// <param name="Columns">Subset of columns to return; null/empty means all.</param>
public record TableQuery(
    int Page,
    int PageSize,
    string? SortColumn,
    bool Descending,
    IReadOnlyList<string>? Columns,
    IReadOnlyList<FilterClause> Filters);

public record QueryPage(long Total, IReadOnlyList<ExplorerColumn> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// Read-side of a provider, used by the data explorer web app. Implementations must
/// validate table/column names against their own schema whitelist and pass filter values
/// as command parameters — user input must never be concatenated into SQL.
/// </summary>
public interface IDataExplorer
{
    /// <summary>Reloads the cached schema and row counts from the target database.</summary>
    void Refresh();

    /// <summary>Tables that contain data, ordered by row count descending.</summary>
    IReadOnlyList<ExplorerTable> GetTables();

    ExplorerTable? FindTable(string name);

    /// <summary>Runs a paged, filtered, sorted query. Throws <see cref="ArgumentException"/> on invalid input.</summary>
    QueryPage Query(string table, TableQuery query);

    /// <summary>
    /// Streams up to <paramref name="maxRows"/> rows with the same filtering/sorting,
    /// for CSV export. The column list matches the arrays' layout.
    /// </summary>
    (IReadOnlyList<ExplorerColumn> Columns, IEnumerable<object?[]> Rows) Stream(
        string table, TableQuery query, int maxRows);
}
