using DataverseExporter.Core.Model;
using DataverseExporter.Core.Settings;
using DataverseExporter.Core.Targets;

namespace DataverseExporter.SqlServer;

/// <summary>
/// Microsoft SQL Server target provider. Register it with the
/// <see cref="ProviderRegistry"/> under the name "SqlServer".
/// </summary>
public class SqlServerProvider : ITargetProvider
{
    private readonly TargetSettings _settings;

    public SqlServerProvider(TargetSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException("Target:ConnectionString is required for the SqlServer provider.");
        _settings = settings;
    }

    public string Name => "SqlServer";

    public ITargetConnection Connect() =>
        new SqlServerConnection(_settings.ConnectionString, _settings.Schema);

    public IDataExplorer CreateExplorer() =>
        new SqlServerExplorer(_settings.ConnectionString, _settings.Schema);

    public string ToStoreType(ColumnDefinition column) => SqlServerTypeMapper.ToStoreType(column);
}
