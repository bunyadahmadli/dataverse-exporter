using System.Data;
using DataverseExporter.Core.Model;
using DataverseExporter.Core.Targets;
using Microsoft.Data.SqlClient;

namespace DataverseExporter.SqlServer;

/// <summary>
/// Streams pages into one table via SqlBulkCopy. Each page and its resume checkpoint
/// are committed in a single transaction: an interruption at any point leaves the page
/// either fully written or not written at all — never duplicated.
/// </summary>
public class SqlServerEntityWriter : IEntityWriter
{
    private readonly SqlServerConnection _owner;
    private readonly SqlConnection _conn;
    private readonly TableDefinition _table;
    private readonly DataTable _buffer;
    private readonly string _quotedName;

    internal SqlServerEntityWriter(SqlServerConnection owner, SqlConnection conn, TableDefinition table)
    {
        _owner = owner;
        _conn = conn;
        _table = table;
        _quotedName = owner.QuotedTableName(table.Name);

        _buffer = new DataTable(table.Name);
        foreach (var col in table.Columns)
            _buffer.Columns.Add(col.Name, col.ClrType);
    }

    public void WritePage(IReadOnlyList<object?[]> rows, EntityProgress checkpoint)
    {
        foreach (var row in rows)
        {
            var dataRow = _buffer.NewRow();
            for (var i = 0; i < row.Length; i++)
                dataRow[i] = row[i] ?? DBNull.Value;
            _buffer.Rows.Add(dataRow);
        }

        using var tx = _conn.BeginTransaction();

        if (_buffer.Rows.Count > 0)
        {
            using var bulk = new SqlBulkCopy(_conn, SqlBulkCopyOptions.TableLock, tx)
            {
                DestinationTableName = _quotedName,
                BulkCopyTimeout = 600,
                BatchSize = 5000
            };
            foreach (var col in _table.Columns)
                bulk.ColumnMappings.Add(col.Name, col.Name);

            bulk.WriteToServer(_buffer);
        }

        _owner.UpsertProgress(tx, checkpoint);
        tx.Commit();

        _buffer.Clear();
    }

    public void Dispose() => _buffer.Dispose();
}
