using Microsoft.Xrm.Sdk;

namespace DataverseExporter.Core.Dataverse;

public static class ValueConverter
{
    /// <summary>
    /// Reduces Dataverse SDK wrapper types to primitive values providers can store.
    /// The verification sample comparison uses the same conversion so both sides match.
    /// </summary>
    public static object? Convert(object? value) => value switch
    {
        null => null,
        EntityReference er => er.Id,
        OptionSetValue osv => osv.Value,
        OptionSetValueCollection osvc => string.Join(";", osvc.Select(o => o.Value)),
        Money money => money.Value,
        AliasedValue aliased => Convert(aliased.Value),
        BooleanManagedProperty bmp => bmp.Value,
        _ => value
    };
}
