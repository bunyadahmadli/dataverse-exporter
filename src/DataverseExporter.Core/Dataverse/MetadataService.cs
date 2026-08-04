using DataverseExporter.Core.Settings;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseExporter.Core.Dataverse;

public class MetadataService
{
    private readonly ServiceClient _client;
    private readonly ExportSettings _settings;

    public MetadataService(ServiceClient client, ExportSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    /// <summary>All exportable entities with their attribute metadata.</summary>
    public List<EntityMetadata> GetExportableEntities()
    {
        var request = new RetrieveAllEntitiesRequest
        {
            EntityFilters = EntityFilters.Entity | EntityFilters.Attributes,
            RetrieveAsIfPublished = true
        };

        var response = (RetrieveAllEntitiesResponse)_client.Execute(request);

        var include = new HashSet<string>(_settings.IncludeEntities, StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(_settings.ExcludeEntities, StringComparer.OrdinalIgnoreCase);

        return response.EntityMetadata
            .Where(e => e.PrimaryIdAttribute != null)
            .Where(e => e.DataProviderId == null) // virtual entities hold no data in Dataverse
            .Where(e => _settings.IncludeIntersectTables || e.IsIntersect != true)
            .Where(e => include.Count == 0 || include.Contains(e.LogicalName))
            .Where(e => !exclude.Contains(e.LogicalName))
            .OrderBy(e => e.LogicalName)
            .ToList();
    }
}
