using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Interfaces;

public interface IXiaomiAccountDeviceSource
{
    Task<IReadOnlyList<XiaomiAccountDevice>> DiscoverAccountDevicesAsync(CancellationToken cancellationToken);

    Task<XiaomiPowerStateResult> ReadPowerStateAsync(
        XiaomiAccountDevice device,
        XiaomiPowerCapability capability,
        CancellationToken cancellationToken);

    Task<XiaomiPowerStateResult> SetPowerStateAsync(
        XiaomiAccountDevice device,
        XiaomiPowerCapability capability,
        bool value,
        CancellationToken cancellationToken);
}

public interface IXiaomiDeviceCapabilityResolver
{
    Task<XiaomiDeviceCapabilities> ResolveCapabilitiesAsync(
        XiaomiAccountDevice device,
        CancellationToken cancellationToken);
}

public interface IXiaomiDeviceControlSource
{
    Task<XiaomiDeviceDefinition?> GetDeviceDefinitionAsync(
        XiaomiAccountDevice device,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(
        XiaomiAccountDevice device,
        IReadOnlyList<XiaomiPropertyDefinition> properties,
        CancellationToken cancellationToken);

    Task<XiaomiPropertyOperationResult> SetPropertyAsync(
        XiaomiAccountDevice device,
        XiaomiPropertyDefinition property,
        object? value,
        CancellationToken cancellationToken);

    Task<XiaomiActionInvocationResult> InvokeActionAsync(
        XiaomiAccountDevice device,
        XiaomiActionDefinition action,
        IReadOnlyList<object?> inputArguments,
        CancellationToken cancellationToken);
}

public interface IMiotLocalizationService
{
    string ServiceName(string rawName, string? officialDescription = null);
    string PropertyName(string rawName, string? officialDescription = null);
    string ActionName(string rawName, string? officialDescription = null);
    string EventName(string rawName, string? officialDescription = null);
    string ValueName(string rawValue, string? officialDescription = null);
    string? UnitName(string? rawUnit);
    bool IsHighRiskAction(XiaomiActionDefinition action);
}
