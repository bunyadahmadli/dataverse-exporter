using DataverseExporter.Core.Model;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseExporter.Core.Dataverse;

/// <summary>
/// One target-table column together with the Dataverse attribute it came from.
/// Companion "_entitytype" columns reference the lookup attribute they belong to.
/// </summary>
public record ExportColumn(ColumnDefinition Column, AttributeMetadata Attribute, bool IsEntityTypeColumn);

/// <summary>
/// Everything the pipeline needs to know about one entity: its metadata, the exportable
/// columns with their Dataverse attributes, and the provider-neutral table definition.
/// </summary>
public class EntityTableModel
{
    public EntityMetadata Entity { get; }
    public IReadOnlyList<ExportColumn> Columns { get; }
    public TableDefinition Table { get; }

    private EntityTableModel(EntityMetadata entity, List<ExportColumn> columns)
    {
        Entity = entity;
        Columns = columns;
        Table = new TableDefinition(entity.LogicalName, columns.Select(c => c.Column).ToList());
    }

    /// <summary>Builds the table model; returns null when no attribute is exportable.</summary>
    public static EntityTableModel? Build(EntityMetadata entity)
    {
        var attributes = entity.Attributes
            .Where(DataverseColumnMapper.IsExportable)
            .OrderBy(a => a.LogicalName != entity.PrimaryIdAttribute) // primary key first
            .ThenBy(a => a.LogicalName)
            .ToList();

        if (attributes.Count == 0)
            return null;

        var attributeNames = attributes.Select(a => a.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var columns = new List<ExportColumn>();
        foreach (var attr in attributes)
        {
            var isPk = attr.LogicalName == entity.PrimaryIdAttribute;
            columns.Add(new ExportColumn(DataverseColumnMapper.ToColumn(attr, isPk)!, attr, false));

            // Skip the companion column in the (very unlikely) case its name collides
            // with a real attribute.
            if (DataverseColumnMapper.NeedsEntityTypeColumn(attr)
                && !attributeNames.Contains(attr.LogicalName + "_entitytype"))
            {
                columns.Add(new ExportColumn(
                    new ColumnDefinition(attr.LogicalName + "_entitytype", LogicalType.String, Length: 128),
                    attr, true));
            }
        }

        return new EntityTableModel(entity, columns);
    }
}
