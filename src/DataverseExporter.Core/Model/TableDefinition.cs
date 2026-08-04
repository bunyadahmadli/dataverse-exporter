namespace DataverseExporter.Core.Model;

/// <summary>
/// A single column of a target table, described in provider-neutral terms.
/// </summary>
/// <param name="Name">Column name (the Dataverse attribute logical name).</param>
/// <param name="Type">Logical type; providers translate it to their own store type.</param>
/// <param name="Length">Character limit for <see cref="LogicalType.String"/> columns.</param>
/// <param name="Precision">Precision for <see cref="LogicalType.Decimal"/> columns.</param>
/// <param name="Scale">Scale for <see cref="LogicalType.Decimal"/> columns.</param>
/// <param name="IsPrimaryKey">True for the entity's primary id column.</param>
public record ColumnDefinition(
    string Name,
    LogicalType Type,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    bool IsPrimaryKey = false)
{
    public Type ClrType => Type.ToClrType();
}

/// <summary>
/// A target table described in provider-neutral terms. Row arrays passed to
/// <see cref="Targets.IEntityWriter.WritePage"/> are aligned with <see cref="Columns"/>.
/// </summary>
public record TableDefinition(string Name, IReadOnlyList<ColumnDefinition> Columns)
{
    public ColumnDefinition? PrimaryKey { get; } = Columns.FirstOrDefault(c => c.IsPrimaryKey);
}

/// <summary>
/// A planned foreign key between two exported tables. Constraint naming, quoting and
/// dialect-specific options (e.g. NOCHECK) are left to the provider.
/// </summary>
public record ForeignKeyDefinition(string FromTable, string FromColumn, string ToTable, string ToColumn);
