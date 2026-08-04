namespace DataverseExporter.Core.Model;

/// <summary>
/// Provider-neutral column types. The Dataverse side maps attribute metadata to these,
/// and each database provider maps them to its own store types (e.g. <c>Guid</c> becomes
/// UNIQUEIDENTIFIER on SQL Server and uuid on PostgreSQL).
/// </summary>
public enum LogicalType
{
    /// <summary>Bounded unicode string; <see cref="ColumnDefinition.Length"/> holds the character limit.</summary>
    String,

    /// <summary>Unbounded unicode text.</summary>
    Text,

    Int32,
    Int64,
    Boolean,

    /// <summary>UTC timestamp. Dataverse always stores DateTime values in UTC.</summary>
    DateTime,

    /// <summary>Exact numeric; <see cref="ColumnDefinition.Precision"/>/<see cref="ColumnDefinition.Scale"/> apply.</summary>
    Decimal,

    Double,
    Guid,
}

public static class LogicalTypeExtensions
{
    /// <summary>The CLR type used for in-memory rows handed to providers.</summary>
    public static Type ToClrType(this LogicalType type) => type switch
    {
        LogicalType.String or LogicalType.Text => typeof(string),
        LogicalType.Int32 => typeof(int),
        LogicalType.Int64 => typeof(long),
        LogicalType.Boolean => typeof(bool),
        LogicalType.DateTime => typeof(DateTime),
        LogicalType.Decimal => typeof(decimal),
        LogicalType.Double => typeof(double),
        LogicalType.Guid => typeof(Guid),
        _ => typeof(object)
    };
}
