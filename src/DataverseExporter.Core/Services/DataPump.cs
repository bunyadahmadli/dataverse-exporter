using DataverseExporter.Core.Dataverse;
using DataverseExporter.Core.Model;
using DataverseExporter.Core.Targets;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseExporter.Core.Services;

/// <summary>
/// Pulls an entity's records from Dataverse page by page and hands each page to the
/// target provider's writer. Every page is committed together with its resume checkpoint,
/// so the export can be interrupted at any moment and continued later.
/// </summary>
public class DataPump
{
    private readonly ServiceClient _client;
    private readonly int _pageSize;

    public DataPump(ServiceClient client, int pageSize)
    {
        _client = client;
        _pageSize = Math.Clamp(pageSize, 1, 5000);
    }

    /// <summary>
    /// Copies all records of <paramref name="model"/> into the target table, resuming
    /// from <paramref name="resume"/> when given. Returns the total row count copied.
    /// </summary>
    public long Run(ITargetConnection target, EntityTableModel model, EntityProgress? resume)
    {
        var entity = model.Entity;
        var columns = model.Columns;

        var attributeNames = columns.Where(c => !c.IsEntityTypeColumn)
            .Select(c => c.Column.Name)
            .ToArray();

        // Column ordinal lookup so each record becomes an array aligned with the table.
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
            ordinals[columns[i].Column.Name] = i;

        var query = new QueryExpression(entity.LogicalName)
        {
            ColumnSet = new ColumnSet(attributeNames),
            PageInfo = new PagingInfo
            {
                PageNumber = resume?.NextPageNumber ?? 1,
                PagingCookie = resume?.PagingCookie,
                Count = _pageSize
            }
        };
        // Deterministic ordering is what makes paging stable across runs.
        query.AddOrder(entity.PrimaryIdAttribute, OrderType.Ascending);

        long total = resume?.RowsCopied ?? 0;
        if (resume != null)
            Console.WriteLine($"  [{entity.LogicalName}] resuming: page {resume.NextPageNumber}, " +
                              $"{total:N0} rows already copied.");

        using var writer = target.CreateWriter(model.Table);
        var rows = new List<object?[]>(_pageSize);

        while (true)
        {
            var page = RetrievePageWithRetry(query);

            foreach (var record in page.Entities)
            {
                var row = new object?[columns.Count];
                foreach (var (key, rawValue) in record.Attributes)
                {
                    if (!ordinals.TryGetValue(key, out var i))
                        continue; // fields outside the ColumnSet (e.g. auto-added ones)

                    row[i] = ValueConverter.Convert(rawValue);

                    // Multi-target lookup: also store which table the reference points to.
                    if (rawValue is EntityReference er
                        && ordinals.TryGetValue(key + "_entitytype", out var typeOrdinal))
                        row[typeOrdinal] = er.LogicalName;
                }
                rows.Add(row);
            }

            total += rows.Count;

            var checkpoint = page.MoreRecords
                ? new EntityProgress(entity.LogicalName, EntityProgress.StatusInProgress,
                    query.PageInfo.PageNumber + 1, page.PagingCookie, total)
                : new EntityProgress(entity.LogicalName, EntityProgress.StatusCompleted,
                    query.PageInfo.PageNumber, null, total);

            // The provider persists rows + checkpoint atomically: after an interruption
            // this page is either fully written or not written at all.
            writer.WritePage(rows, checkpoint);
            rows.Clear();

            if (!page.MoreRecords)
                break;

            Console.WriteLine($"  [{entity.LogicalName}] {total:N0} rows copied...");
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }

        return total;
    }

    /// <summary>
    /// A failure on the first page is most likely permanent (the entity does not support
    /// RetrieveMultiple) and is rethrown immediately; later pages usually fail for
    /// transient network/timeout reasons and are retried with increasing backoff so a
    /// large table is not abandoned halfway through.
    /// </summary>
    private EntityCollection RetrievePageWithRetry(QueryExpression query)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _client.RetrieveMultiple(query);
            }
            catch (Exception ex) when (query.PageInfo.PageNumber > 1 && attempt <= 3)
            {
                var waitSeconds = 10 * attempt;
                Console.WriteLine($"  [{query.EntityName}] page {query.PageInfo.PageNumber} failed ({ex.Message}), " +
                                  $"retrying in {waitSeconds}s ({attempt}/3)...");
                Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));
            }
        }
    }
}
