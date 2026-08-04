using DataverseExporter.Core.Model;

namespace DataverseExporter.Core.Targets;

/// <summary>
/// An open connection to the target database. Not thread-safe: each parallel worker
/// opens its own connection via <see cref="ITargetProvider.Connect"/>.
/// </summary>
public interface ITargetConnection : IDisposable
{
    /// <summary>Human-readable description of what we connected to, for logging.</summary>
    string Description { get; }

    // ---- Schema ----

    /// <summary>
    /// Creates the target schema/namespace and the internal bookkeeping tables
    /// (progress, verification) if they do not exist. Called once before the export.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Creates the table if it does not exist. When <paramref name="recreate"/> is true
    /// the table is dropped first.
    /// </summary>
    void CreateTable(TableDefinition table, bool recreate);

    /// <summary>
    /// Aligns an existing table with the desired definition: adds missing columns and
    /// widens string/decimal columns whose source grew. Must never narrow or drop columns.
    /// </summary>
    void EnsureColumns(TableDefinition table);

    void TruncateTable(TableDefinition table);

    // ---- Data ----

    /// <summary>Creates a writer that bulk-inserts pages into <paramref name="table"/>.</summary>
    IEntityWriter CreateWriter(TableDefinition table);

    // ---- Resume progress ----

    IReadOnlyDictionary<string, EntityProgress> LoadProgress();

    /// <summary>Deletes all progress checkpoints so the next run starts from scratch.</summary>
    void ResetProgress();

    // ---- Relationships ----

    /// <summary>Drops every foreign key previously created by this tool (and only those).</summary>
    void DropManagedForeignKeys();

    /// <summary>
    /// Creates the given foreign keys. Existing data must not be validated (targets may
    /// reference rows that were deleted in Dataverse). Returns how many were created and
    /// which ones failed with what error.
    /// </summary>
    (int Created, IReadOnlyList<(string Name, string Error)> Failed) CreateForeignKeys(
        IReadOnlyList<ForeignKeyDefinition> foreignKeys);

    // ---- Verification ----

    long CountRows(TableDefinition table);

    /// <summary>Picks up to <paramref name="count"/> random primary key values, cheaply on large tables.</summary>
    IReadOnlyList<Guid> SampleIds(TableDefinition table, int count);

    /// <summary>Fetches full rows by primary key. Database nulls are returned as CLR null.</summary>
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> FetchRows(
        TableDefinition table, IReadOnlyList<Guid> ids);

    bool RowExists(TableDefinition table, Guid id);

    void SaveVerification(VerificationResult result);
}

/// <summary>
/// Bulk writer for one entity's table. <see cref="WritePage"/> must persist the rows and
/// the checkpoint atomically (same transaction), which is what makes resume safe.
/// </summary>
public interface IEntityWriter : IDisposable
{
    /// <summary>
    /// Writes one page of rows (arrays aligned with the table's column list; null means
    /// database NULL) together with the resume checkpoint, atomically.
    /// </summary>
    void WritePage(IReadOnlyList<object?[]> rows, EntityProgress checkpoint);
}
