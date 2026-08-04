using DataverseExporter.Core.Dataverse;
using DataverseExporter.Core.Model;

namespace DataverseExporter.Core.Services;

/// <summary>
/// Computes the foreign keys that can be materialized in the target database:
/// only single-target lookups qualify (multi-target lookups like customerid/ownerid
/// cannot reference one table; their target is kept in the "_entitytype" companion
/// column instead). Providers turn the plan into dialect-specific DDL.
/// </summary>
public static class ForeignKeyPlanner
{
    public static List<ForeignKeyDefinition> Plan(
        IReadOnlyList<EntityTableModel> models, Func<string, bool> isEligible)
    {
        var byName = models.ToDictionary(m => m.Entity.LogicalName, StringComparer.OrdinalIgnoreCase);
        var plan = new List<ForeignKeyDefinition>();

        foreach (var model in models)
        {
            if (!isEligible(model.Entity.LogicalName))
                continue;

            foreach (var column in model.Columns)
            {
                if (column.IsEntityTypeColumn)
                    continue;

                var target = DataverseColumnMapper.SingleTarget(column.Attribute);
                if (target == null || !isEligible(target) || !byName.TryGetValue(target, out var targetModel))
                    continue;

                plan.Add(new ForeignKeyDefinition(
                    model.Entity.LogicalName,
                    column.Column.Name,
                    target,
                    targetModel.Entity.PrimaryIdAttribute));
            }
        }

        return plan;
    }
}
