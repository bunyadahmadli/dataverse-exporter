using DataverseExporter.Core.Model;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseExporter.Core.Dataverse;

/// <summary>
/// Maps Dataverse attribute metadata to provider-neutral logical column types.
/// This is the only place that decides what gets exported and as what shape;
/// database providers translate the result into their own store types.
/// </summary>
public static class DataverseColumnMapper
{
    /// <summary>
    /// Logical column shape for an attribute, or null when the attribute cannot be
    /// exported meaningfully (PartyList, files, virtual fields...).
    /// </summary>
    public static ColumnDefinition? ToColumn(AttributeMetadata attr, bool isPrimaryKey)
    {
        switch (attr.AttributeType)
        {
            case AttributeTypeCode.String:
                var maxLen = (attr as StringAttributeMetadata)?.MaxLength ?? 0;
                return maxLen > 0
                    ? new ColumnDefinition(attr.LogicalName, LogicalType.String, Length: maxLen, IsPrimaryKey: isPrimaryKey)
                    : new ColumnDefinition(attr.LogicalName, LogicalType.Text, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Memo:
                return new ColumnDefinition(attr.LogicalName, LogicalType.Text, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Integer:
                return new ColumnDefinition(attr.LogicalName, LogicalType.Int32, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.BigInt:
                return new ColumnDefinition(attr.LogicalName, LogicalType.Int64, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Boolean:
                return new ColumnDefinition(attr.LogicalName, LogicalType.Boolean, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.DateTime:
                return new ColumnDefinition(attr.LogicalName, LogicalType.DateTime, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Decimal:
                // Metadata precision cannot be trusted: fields like exchangerate carry
                // 12 decimals despite a declared precision of 10 (caught by verification
                // against real data) — use a generous fixed shape.
                return new ColumnDefinition(attr.LogicalName, LogicalType.Decimal,
                    Precision: 28, Scale: 12, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Double:
                return new ColumnDefinition(attr.LogicalName, LogicalType.Double, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Money:
                // Dataverse stores money with up to 10 decimals; the conventional
                // money shape (19,4) would silently round.
                return new ColumnDefinition(attr.LogicalName, LogicalType.Decimal,
                    Precision: 28, Scale: 10, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Uniqueidentifier:
            case AttributeTypeCode.Lookup:
            case AttributeTypeCode.Customer:
            case AttributeTypeCode.Owner:
                return new ColumnDefinition(attr.LogicalName, LogicalType.Guid, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Picklist:
            case AttributeTypeCode.State:
            case AttributeTypeCode.Status:
                return new ColumnDefinition(attr.LogicalName, LogicalType.Int32, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.EntityName:
                return new ColumnDefinition(attr.LogicalName, LogicalType.String, Length: 128, IsPrimaryKey: isPrimaryKey);

            case AttributeTypeCode.Virtual:
                // Multi-select picklist values are stored as "1;2;3".
                return attr is MultiSelectPicklistAttributeMetadata
                    ? new ColumnDefinition(attr.LogicalName, LogicalType.Text, IsPrimaryKey: isPrimaryKey)
                    : null;

            // PartyList data is exported separately via the activityparty table;
            // ManagedProperty/CalendarRules carry no reporting value.
            case AttributeTypeCode.PartyList:
            case AttributeTypeCode.ManagedProperty:
            case AttributeTypeCode.CalendarRules:
            default:
                return null;
        }
    }

    /// <summary>
    /// Lookups that can target more than one entity (customerid, ownerid, regardingobjectid...)
    /// need a companion "_entitytype" column, because the GUID alone does not say which
    /// table it points to.
    /// </summary>
    public static bool NeedsEntityTypeColumn(AttributeMetadata attr) =>
        attr is LookupAttributeMetadata lookup && (lookup.Targets?.Length ?? 0) != 1;

    /// <summary>Target entity logical name when the lookup has exactly one target (FK eligible).</summary>
    public static string? SingleTarget(AttributeMetadata attr) =>
        attr is LookupAttributeMetadata { Targets.Length: 1 } lookup ? lookup.Targets[0] : null;

    /// <summary>Whether the attribute becomes a column in the target schema.</summary>
    public static bool IsExportable(AttributeMetadata attr)
    {
        // Attributes with AttributeOf set are virtual derivatives of another column
        // (e.g. the "...name" of a lookup) — they carry no real data.
        if (!string.IsNullOrEmpty(attr.AttributeOf))
            return false;

        if (attr.IsValidForRead != true)
            return false;

        return ToColumn(attr, isPrimaryKey: false) != null;
    }
}
