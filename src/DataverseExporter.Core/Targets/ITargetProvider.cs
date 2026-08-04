using DataverseExporter.Core.Model;

namespace DataverseExporter.Core.Targets;

/// <summary>
/// Entry point of a target database implementation. A provider is a cheap, stateless
/// factory: every export worker calls <see cref="Connect"/> to get its own connection.
/// Implement this interface (plus <see cref="ITargetConnection"/>, <see cref="IEntityWriter"/>
/// and optionally <see cref="IDataExplorer"/>) to add support for a new database —
/// see docs/adding-a-provider.md.
/// </summary>
public interface ITargetProvider
{
    /// <summary>Provider name as used in configuration (e.g. "SqlServer").</summary>
    string Name { get; }

    /// <summary>Opens a new connection to the target database.</summary>
    ITargetConnection Connect();

    /// <summary>
    /// Creates the read-side used by the data explorer web app. Providers that only
    /// support exporting may throw <see cref="NotSupportedException"/>.
    /// </summary>
    IDataExplorer CreateExplorer();

    /// <summary>
    /// The provider's store type name for a column (e.g. "decimal(28, 10)").
    /// Used by the model generator to emit EF Core <c>[Column(TypeName = ...)]</c> hints.
    /// </summary>
    string ToStoreType(ColumnDefinition column);
}
