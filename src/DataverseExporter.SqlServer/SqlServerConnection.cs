using System.Security.Cryptography;
using System.Text;
using DataverseExporter.Core.Model;
using DataverseExporter.Core.Targets;
using Microsoft.Data.SqlClient;

namespace DataverseExporter.SqlServer;

public class SqlServerConnection : ITargetConnection
{
    private readonly SqlConnection _conn;
    private readonly string _schema;

    private string ProgressTable => $"[{_schema}].[_MigrationProgress]";
    private string VerificationTable => $"[{_schema}].[_MigrationVerification]";

    public SqlServerConnection(string connectionString, string schema)
    {
        _schema = schema;
        _conn = new SqlConnection(connectionString);
        _conn.Open();
    }

    public string Description => $"{_conn.DataSource}/{_conn.Database} (SQL Server {_conn.ServerVersion})";

    internal string QuotedTableName(string tableName) => $"[{_schema}].[{Escape(tableName)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]");

    private void Execute(string sql, int timeoutSeconds = 300, params SqlParameter[] parameters)
    {
        using var cmd = new SqlCommand(sql, _conn) { CommandTimeout = timeoutSeconds };
        cmd.Parameters.AddRange(parameters);
        cmd.ExecuteNonQuery();
    }

    // ---- Schema ----

    public void Initialize()
    {
        Execute($"""
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{Escape(_schema)}')
                EXEC('CREATE SCHEMA [{Escape(_schema)}]');
            """);

        Execute($"""
            IF OBJECT_ID(N'{ProgressTable}', N'U') IS NULL
            CREATE TABLE {ProgressTable} (
                EntityName     NVARCHAR(128) NOT NULL PRIMARY KEY,
                Status         NVARCHAR(20)  NOT NULL,
                NextPageNumber INT           NOT NULL,
                PagingCookie   NVARCHAR(MAX) NULL,
                RowsCopied     BIGINT        NOT NULL,
                LastUpdated    DATETIME2     NOT NULL
            );
            """);

        Execute($"""
            IF OBJECT_ID(N'{VerificationTable}', N'U') IS NULL
            CREATE TABLE {VerificationTable} (
                EntityName            NVARCHAR(128) NOT NULL PRIMARY KEY,
                TargetRows            BIGINT        NOT NULL,
                CopiedRows            BIGINT        NOT NULL,
                CrmSnapshotRows       BIGINT        NULL,
                SamplesChecked        INT           NOT NULL,
                SampleMismatches      INT           NOT NULL,
                ChangedAfterMigration INT           NOT NULL,
                Status                NVARCHAR(10)  NOT NULL,
                Details               NVARCHAR(MAX) NULL,
                VerifiedAt            DATETIME2     NOT NULL
            );
            """);
    }

    public void CreateTable(TableDefinition table, bool recreate)
    {
        var quoted = QuotedTableName(table.Name);

        if (recreate)
            Execute($"IF OBJECT_ID(N'{quoted}', N'U') IS NOT NULL DROP TABLE {quoted};");

        var columnDefs = table.Columns.Select(col =>
            $"    [{Escape(col.Name)}] {SqlServerTypeMapper.ToStoreType(col)} " +
            (col.IsPrimaryKey ? "NOT NULL PRIMARY KEY" : "NULL"));

        Execute($"""
            IF OBJECT_ID(N'{quoted}', N'U') IS NULL
            CREATE TABLE {quoted} (
            {string.Join(",\n", columnDefs)}
            );
            """);
    }

    /// <summary>
    /// Aligns an existing table with the desired definition: adds missing columns
    /// (fields added to Dataverse later / tables created by an older version) and widens
    /// NVARCHAR columns whose Dataverse length grew — otherwise bulk inserts would fail
    /// permanently with truncation errors. Never narrows.
    /// </summary>
    public void EnsureColumns(TableDefinition table)
    {
        var quoted = QuotedTableName(table.Name);

        // max_length: bytes for nvarchar (chars x 2), -1 for MAX.
        var existing = new Dictionary<string, (string Type, short MaxLength, byte Precision, byte Scale)>(
            StringComparer.OrdinalIgnoreCase);
        using (var cmd = new SqlCommand($"""
            SELECT c.name, t.name, c.max_length, c.precision, c.scale
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'{quoted}');
            """, _conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                existing[reader.GetString(0)] =
                    (reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4));
        }

        if (existing.Count == 0)
            return; // table missing; CreateTable's job

        var ddl = new List<string>();
        var changes = new List<string>();

        foreach (var col in table.Columns)
        {
            var storeType = SqlServerTypeMapper.ToStoreType(col);

            if (!existing.TryGetValue(col.Name, out var current))
            {
                ddl.Add($"ALTER TABLE {quoted} ADD [{Escape(col.Name)}] {storeType} NULL;");
                changes.Add($"+{col.Name}");
            }
            else if (current.Type == "nvarchar"
                     && current.MaxLength != -1
                     && SqlServerTypeMapper.DesiredNvarcharChars(col) is int desired
                     && (desired == -1 || desired > current.MaxLength / 2))
            {
                ddl.Add($"ALTER TABLE {quoted} ALTER COLUMN [{Escape(col.Name)}] {storeType} NULL;");
                changes.Add($"~{col.Name}");
            }
            else if (current.Type == "decimal" && col.Type == LogicalType.Decimal)
            {
                var (dp, ds) = (col.Precision ?? 28, col.Scale ?? 12);
                // only when the new shape covers the current data losslessly and is truly wider
                if (ds >= current.Scale && dp - ds >= current.Precision - current.Scale
                    && (ds > current.Scale || dp - ds > current.Precision - current.Scale))
                {
                    ddl.Add($"ALTER TABLE {quoted} ALTER COLUMN [{Escape(col.Name)}] {storeType} NULL;");
                    changes.Add($"~{col.Name}");
                }
            }
        }

        if (ddl.Count == 0)
            return;

        Execute(string.Join("\n", ddl), timeoutSeconds: 600);
        Console.WriteLine($"      Schema aligned ({changes.Count} columns): {string.Join(", ", changes)}");
    }

    public void TruncateTable(TableDefinition table) =>
        Execute($"TRUNCATE TABLE {QuotedTableName(table.Name)};");

    // ---- Data ----

    public IEntityWriter CreateWriter(TableDefinition table) =>
        new SqlServerEntityWriter(this, _conn, table);

    // ---- Resume progress ----

    internal void UpsertProgress(SqlTransaction? tx, EntityProgress progress)
    {
        var sql = $"""
            UPDATE {ProgressTable}
               SET Status = @status, NextPageNumber = @page, PagingCookie = @cookie,
                   RowsCopied = @rows, LastUpdated = SYSUTCDATETIME()
             WHERE EntityName = @entity;
            IF @@ROWCOUNT = 0
                INSERT INTO {ProgressTable} (EntityName, Status, NextPageNumber, PagingCookie, RowsCopied, LastUpdated)
                VALUES (@entity, @status, @page, @cookie, @rows, SYSUTCDATETIME());
            """;
        using var cmd = new SqlCommand(sql, _conn, tx);
        cmd.Parameters.AddWithValue("@entity", progress.EntityName);
        cmd.Parameters.AddWithValue("@status", progress.Status);
        cmd.Parameters.AddWithValue("@page", progress.NextPageNumber);
        cmd.Parameters.AddWithValue("@cookie", (object?)progress.PagingCookie ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rows", progress.RowsCopied);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, EntityProgress> LoadProgress()
    {
        var result = new Dictionary<string, EntityProgress>(StringComparer.OrdinalIgnoreCase);
        using var cmd = new SqlCommand(
            $"SELECT EntityName, Status, NextPageNumber, PagingCookie, RowsCopied FROM {ProgressTable};", _conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var p = new EntityProgress(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4));
            result[p.EntityName] = p;
        }
        return result;
    }

    public void ResetProgress() => Execute($"DELETE FROM {ProgressTable};");

    // ---- Relationships ----

    public void DropManagedForeignKeys()
    {
        Execute("""
            DECLARE @sql NVARCHAR(MAX) = N'';
            SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))
                         + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id))
                         + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
            FROM sys.foreign_keys
            WHERE name LIKE N'FK\_crm\_%' ESCAPE N'\'
              AND OBJECT_SCHEMA_NAME(parent_object_id) = @schema;
            EXEC sp_executesql @sql;
            """, parameters: new SqlParameter("@schema", _schema));
    }

    public (int Created, IReadOnlyList<(string Name, string Error)> Failed) CreateForeignKeys(
        IReadOnlyList<ForeignKeyDefinition> foreignKeys)
    {
        var created = 0;
        var failed = new List<(string, string)>();

        foreach (var fk in foreignKeys)
        {
            var fkName = FkName(fk.FromTable, fk.FromColumn);

            // WITH NOCHECK: Dataverse data may reference rows that were deleted or not
            // exported; existing data is not validated but the relationship still exists
            // in the schema and is usable in joins.
            var sql = $"""
                IF OBJECT_ID(N'{QuotedTableName(fk.FromTable)}', N'U') IS NOT NULL
                   AND OBJECT_ID(N'{QuotedTableName(fk.ToTable)}', N'U') IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'{Escape(fkName)}')
                    ALTER TABLE {QuotedTableName(fk.FromTable)} WITH NOCHECK
                        ADD CONSTRAINT [{Escape(fkName)}]
                        FOREIGN KEY ([{Escape(fk.FromColumn)}])
                        REFERENCES {QuotedTableName(fk.ToTable)} ([{Escape(fk.ToColumn)}]);
                """;

            try
            {
                Execute(sql);
                created++;
            }
            catch (Exception ex)
            {
                failed.Add((fkName, ex.Message));
            }
        }

        return (created, failed);
    }

    /// <summary>
    /// SQL Server's identifier limit is 128 characters; plain truncation could make two
    /// different FKs collide on the same name and silently skip the second one. When
    /// truncation is needed, a deterministic hash suffix is appended (SHA256 because
    /// GetHashCode is not stable across processes).
    /// </summary>
    private static string FkName(string tableName, string columnName)
    {
        var name = $"FK_crm_{tableName}_{columnName}";
        if (name.Length <= 128)
            return name;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return name[..119] + "_" + hash;
    }

    // ---- Verification ----

    public long CountRows(TableDefinition table)
    {
        using var cmd = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM {QuotedTableName(table.Name)};", _conn) { CommandTimeout = 600 };
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// ORDER BY NEWID() would require a full scan on large tables, so the cheap
    /// TABLESAMPLE is tried first; when it returns too few rows (small table) a plain
    /// TOP query is used instead.
    /// </summary>
    public IReadOnlyList<Guid> SampleIds(TableDefinition table, int count)
    {
        var ids = new List<Guid>();
        if (count <= 0 || table.PrimaryKey == null)
            return ids;

        var quoted = QuotedTableName(table.Name);
        var pk = Escape(table.PrimaryKey.Name);

        try
        {
            using var cmd = new SqlCommand(
                $"SELECT TOP ({count}) [{pk}] FROM {quoted} TABLESAMPLE SYSTEM (1 PERCENT);", _conn)
            {
                CommandTimeout = 300
            };
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetGuid(0));
        }
        catch
        {
            // TABLESAMPLE not supported; fall through to the plain query
        }

        if (ids.Count < count)
        {
            ids.Clear();
            using var cmd = new SqlCommand($"SELECT TOP ({count}) [{pk}] FROM {quoted};", _conn)
            {
                CommandTimeout = 300
            };
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> FetchRows(
        TableDefinition table, IReadOnlyList<Guid> ids)
    {
        var pk = table.PrimaryKey
            ?? throw new InvalidOperationException($"Table {table.Name} has no primary key.");

        var colList = string.Join(", ", table.Columns.Select(c => $"[{Escape(c.Name)}]"));
        var paramList = string.Join(", ", ids.Select((_, i) => $"@p{i}"));
        using var cmd = new SqlCommand(
            $"SELECT {colList} FROM {QuotedTableName(table.Name)} WHERE [{Escape(pk.Name)}] IN ({paramList});",
            _conn)
        {
            CommandTimeout = 300
        };
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", ids[i]);

        var result = new Dictionary<Guid, IReadOnlyDictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < table.Columns.Count; i++)
                row[table.Columns[i].Name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result[(Guid)row[pk.Name]!] = row;
        }
        return result;
    }

    public bool RowExists(TableDefinition table, Guid id)
    {
        var pk = table.PrimaryKey
            ?? throw new InvalidOperationException($"Table {table.Name} has no primary key.");

        using var cmd = new SqlCommand(
            $"SELECT COUNT(*) FROM {QuotedTableName(table.Name)} WHERE [{Escape(pk.Name)}] = @id;", _conn);
        cmd.Parameters.AddWithValue("@id", id);
        return (int)cmd.ExecuteScalar()! > 0;
    }

    public void SaveVerification(VerificationResult r)
    {
        var sql = $"""
            UPDATE {VerificationTable}
               SET TargetRows = @target, CopiedRows = @copied, CrmSnapshotRows = @snap,
                   SamplesChecked = @samples, SampleMismatches = @mismatches,
                   ChangedAfterMigration = @changed, Status = @status, Details = @details,
                   VerifiedAt = SYSUTCDATETIME()
             WHERE EntityName = @entity;
            IF @@ROWCOUNT = 0
                INSERT INTO {VerificationTable}
                    (EntityName, TargetRows, CopiedRows, CrmSnapshotRows, SamplesChecked,
                     SampleMismatches, ChangedAfterMigration, Status, Details, VerifiedAt)
                VALUES (@entity, @target, @copied, @snap, @samples, @mismatches, @changed,
                        @status, @details, SYSUTCDATETIME());
            """;
        using var cmd = new SqlCommand(sql, _conn);
        cmd.Parameters.AddWithValue("@entity", r.EntityName);
        cmd.Parameters.AddWithValue("@target", r.TargetRows);
        cmd.Parameters.AddWithValue("@copied", r.CopiedRows);
        cmd.Parameters.AddWithValue("@snap", (object?)r.CrmSnapshotRows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@samples", r.SamplesChecked);
        cmd.Parameters.AddWithValue("@mismatches", r.SampleMismatches);
        cmd.Parameters.AddWithValue("@changed", r.ChangedAfterMigration);
        cmd.Parameters.AddWithValue("@status", r.Status);
        cmd.Parameters.AddWithValue("@details", (object?)r.Details ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
