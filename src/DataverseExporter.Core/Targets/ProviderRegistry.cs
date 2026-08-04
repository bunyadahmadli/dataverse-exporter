using DataverseExporter.Core.Settings;

namespace DataverseExporter.Core.Targets;

/// <summary>
/// Maps the configured provider name ("Target:Provider") to a factory. Applications
/// register the providers they ship at startup:
/// <code>
/// var registry = new ProviderRegistry();
/// registry.Register("SqlServer", s => new SqlServerProvider(s));
/// </code>
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, Func<TargetSettings, ITargetProvider>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, Func<TargetSettings, ITargetProvider> factory) =>
        _factories[name] = factory;

    public IEnumerable<string> Names => _factories.Keys;

    public ITargetProvider Create(TargetSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Provider))
            throw new InvalidOperationException(
                $"Target:Provider is not set. Available providers: {string.Join(", ", Names)}.");

        if (!_factories.TryGetValue(settings.Provider, out var factory))
            throw new InvalidOperationException(
                $"Unknown target provider '{settings.Provider}'. Available providers: {string.Join(", ", Names)}.");

        return factory(settings);
    }
}
