using DataverseExporter.Core.Dataverse;
using DataverseExporter.Core.Model;
using DataverseExporter.Core.Settings;
using DataverseExporter.Core.Targets;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseExporter.Core.Services;

/// <summary>
/// Post-export verification. Four checks per entity:
///  1. Target row count == rows read from Dataverse during the export (exact; Error on mismatch).
///  2. Cross-check against Dataverse's total record counter (the counter is a snapshot that
///     can lag up to 24h; Warning when the tolerance is exceeded).
///  3. N randomly sampled records are re-fetched from Dataverse and compared field by field;
///     records modified after the export (different modifiedon) do not count as corruption.
///  4. Is the newest Dataverse record present in the target ("tail" records created mid-export)?
/// Results are stored by the provider (e.g. in a _MigrationVerification table).
/// </summary>
public class VerificationService
{
    private readonly VerificationSettings _settings;

    public VerificationService(VerificationSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Fetches a record-count snapshot for all entities in one go. Used both to put the
    /// biggest tables at the front of the parallel queue and for verification later.
    /// </summary>
    public static Dictionary<string, long> GetRecordCountSnapshot(
        ServiceClient client, IReadOnlyCollection<string> logicalNames)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in logicalNames.Chunk(100))
        {
            try
            {
                var response = (RetrieveTotalRecordCountResponse)client.Execute(
                    new RetrieveTotalRecordCountRequest { EntityNames = chunk });
                foreach (var kv in response.EntityRecordCountCollection)
                    result[kv.Key] = kv.Value;
            }
            catch
            {
                // One unsupported entity must not sink the whole chunk; retry one by one.
                foreach (var name in chunk)
                {
                    try
                    {
                        var response = (RetrieveTotalRecordCountResponse)client.Execute(
                            new RetrieveTotalRecordCountRequest { EntityNames = new[] { name } });
                        foreach (var kv in response.EntityRecordCountCollection)
                            result[kv.Key] = kv.Value;
                    }
                    catch
                    {
                        // counter not supported; the cross-check is simply skipped
                    }
                }
            }
        }
        return result;
    }

    public VerificationResult VerifyEntity(ServiceClient client, ITargetConnection target,
        EntityTableModel model, long copiedRows, long? crmSnapshot)
    {
        var entity = model.Entity;
        var pk = entity.PrimaryIdAttribute;
        var details = new List<string>();
        bool error = false, warning = false;

        // 1) Exact check: rows written to the target vs rows read from Dataverse.
        var targetRows = target.CountRows(model.Table);
        if (targetRows != copiedRows)
        {
            error = true;
            details.Add($"row count mismatch: target={targetRows:N0}, copied from Dataverse={copiedRows:N0}");
        }

        // 2) Cross-check against the Dataverse counter snapshot (may lag → warning only).
        if (crmSnapshot is long snapshot)
        {
            var diff = Math.Abs(snapshot - targetRows);
            var tolerance = Math.Max(100, targetRows / 100);
            if (diff > tolerance)
            {
                warning = true;
                details.Add($"differs from the Dataverse counter by {diff:N0} (Dataverse~{snapshot:N0}, " +
                            $"target={targetRows:N0}; the counter can lag up to 24h)");
            }
        }

        // Calculated (SourceType=1) and rollup (SourceType=2) fields, plus their
        // _date/_state companions, are recomputed by Dataverse on read or asynchronously;
        // they can legitimately change after the export (without touching modifiedon!)
        // and are excluded from the sample comparison.
        var volatileAttrs = entity.Attributes
            .Where(a => a.SourceType is 1 or 2)
            .Select(a => a.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsVolatile(ExportColumn col)
        {
            var name = col.Attribute.LogicalName;
            return volatileAttrs.Contains(name)
                   || (name.EndsWith("_date") && volatileAttrs.Contains(name[..^5]))
                   || (name.EndsWith("_state") && volatileAttrs.Contains(name[..^6]));
        }

        // 3) Sampling: N random records compared field by field. Skipped for the rare
        // system entities whose primary key is not a GUID.
        int samplesChecked = 0, mismatches = 0, changedAfter = 0;
        var pkColumn = model.Table.PrimaryKey;
        var ids = pkColumn?.Type == LogicalType.Guid
            ? target.SampleIds(model.Table, _settings.SampleSize)
            : Array.Empty<Guid>();

        if (ids.Count > 0)
        {
            var attrNames = model.Columns.Where(c => !c.IsEntityTypeColumn)
                .Select(c => c.Column.Name).ToArray();
            var query = new QueryExpression(entity.LogicalName) { ColumnSet = new ColumnSet(attrNames) };
            query.Criteria.AddCondition(pk, ConditionOperator.In, ids.Cast<object>().ToArray());
            var crmById = client.RetrieveMultiple(query).Entities.ToDictionary(e => e.Id);
            var targetById = target.FetchRows(model.Table, ids);

            foreach (var id in ids)
            {
                samplesChecked++;

                if (!crmById.TryGetValue(id, out var record))
                {
                    changedAfter++;
                    details.Add($"sample {id}: not found in Dataverse (may have been deleted after the export)");
                    continue;
                }

                var targetRow = targetById[id];

                // Field differences on records updated after the export are not corruption.
                if (record.Attributes.TryGetValue("modifiedon", out var crmModified)
                    && targetRow.TryGetValue("modifiedon", out var targetModified)
                    && !ValuesEqual(ValueConverter.Convert(crmModified), targetModified))
                {
                    changedAfter++;
                    continue;
                }

                foreach (var col in model.Columns)
                {
                    if (IsVolatile(col))
                        continue;

                    object? expected;
                    if (col.IsEntityTypeColumn)
                        expected = record.Attributes.TryGetValue(col.Attribute.LogicalName, out var raw)
                            ? (raw as EntityReference)?.LogicalName
                            : null;
                    else
                        expected = record.Attributes.TryGetValue(col.Column.Name, out var raw)
                            ? ValueConverter.Convert(raw)
                            : null;

                    if (!ValuesEqual(expected, targetRow.GetValueOrDefault(col.Column.Name)))
                    {
                        mismatches++;
                        if (details.Count < 50)
                            details.Add($"sample {id} [{col.Column.Name}]: Dataverse='{Render(expected)}' " +
                                        $"target='{Render(targetRow.GetValueOrDefault(col.Column.Name))}'");
                    }
                }
            }

            if (mismatches > 0) error = true;
            if (changedAfter > 0) warning = true;
        }

        // 4) Tail check: is the newest Dataverse record present in the target?
        if (entity.Attributes.Any(a => a.LogicalName == "createdon"))
        {
            try
            {
                var newestQuery = new QueryExpression(entity.LogicalName)
                {
                    ColumnSet = new ColumnSet(false),
                    TopCount = 1
                };
                newestQuery.AddOrder("createdon", OrderType.Descending);
                var newest = client.RetrieveMultiple(newestQuery).Entities.FirstOrDefault();
                if (newest != null && !target.RowExists(model.Table, newest.Id))
                {
                    warning = true;
                    details.Add($"the newest Dataverse record ({newest.Id}) is missing from the target " +
                                "(it may have been created after the export)");
                }
            }
            catch
            {
                // some entities do not support ordering by createdon; don't fail verification
            }
        }

        var status = error ? VerificationResult.StatusError
            : warning ? VerificationResult.StatusWarning
            : VerificationResult.StatusOk;
        var detailText = details.Count == 0 ? null : string.Join("; ", details.Take(50));
        return new VerificationResult(entity.LogicalName, targetRows, copiedRows, crmSnapshot,
            samplesChecked, mismatches, changedAfter, status, detailText);
    }

    private static bool ValuesEqual(object? expected, object? actual)
    {
        if (expected == null || actual == null)
            return expected == null && actual == null;

        return (expected, actual) switch
        {
            // DateTime keeps tick precision; decimal equality ignores scale (1.0 == 1.00)
            (DateTime e, DateTime a) => e == a,
            (decimal e, decimal a) => e == a,
            _ => expected.Equals(actual)
        };
    }

    private static string Render(object? value)
    {
        if (value == null)
            return "(null)";
        var text = value is DateTime dt ? dt.ToString("O") : value.ToString() ?? "";
        return text.Length <= 60 ? text : text[..60] + "...";
    }
}
