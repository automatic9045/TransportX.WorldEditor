namespace TransportX.WorldEditor.Core.Extensions;

/// <summary>
/// Contract for future domain-specific editor extensions (e.g., TransportX.Domains.RoadTraffic).
/// </summary>
public interface IDomainExtension
{
    string ExtensionId { get; }
    string DisplayName { get; }
    string RequiredDependencyMacro { get; }
}
