using DataverseExporter.Core.Model;

namespace DataverseExporter.SqlServer;

/// <summary>Maps provider-neutral logical column types to SQL Server store types.</summary>
public static class SqlServerTypeMapper
{
    /// <summary>NVARCHAR's inline limit; longer strings become NVARCHAR(MAX).</summary>
    public const int MaxInlineNvarcharChars = 4000;

    public static string ToStoreType(ColumnDefinition column) => column.Type switch
    {
        LogicalType.String when column.Length is > 0 and <= MaxInlineNvarcharChars =>
            $"NVARCHAR({column.Length})",
        LogicalType.String or LogicalType.Text => "NVARCHAR(MAX)",
        LogicalType.Int32 => "INT",
        LogicalType.Int64 => "BIGINT",
        LogicalType.Boolean => "BIT",
        LogicalType.DateTime => "DATETIME2",
        LogicalType.Decimal => $"DECIMAL({column.Precision ?? 28}, {column.Scale ?? 12})",
        LogicalType.Double => "FLOAT",
        LogicalType.Guid => "UNIQUEIDENTIFIER",
        _ => throw new NotSupportedException($"Unsupported logical type: {column.Type}")
    };

    /// <summary>
    /// Desired NVARCHAR length in characters for string-ish columns (-1 = MAX),
    /// or null when the column is not a string.
    /// </summary>
    public static int? DesiredNvarcharChars(ColumnDefinition column) => column.Type switch
    {
        LogicalType.String when column.Length is > 0 and <= MaxInlineNvarcharChars => column.Length,
        LogicalType.String or LogicalType.Text => -1,
        _ => null
    };
}
