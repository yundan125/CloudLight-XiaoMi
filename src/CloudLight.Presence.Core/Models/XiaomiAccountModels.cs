namespace CloudLight.Presence.Core.Models;

public enum XiaomiAccountDeviceType
{
    Unknown = 0,
    Router = 1,
    Switch = 2,
    Light = 3,
    Sensor = 4,
    AirConditioner = 5,
    Camera = 6,
    Vacuum = 7,
    Lock = 8,
    Plug = 9,
    Other = 10
}

public sealed record XiaomiPowerCapability(
    int Siid,
    int Piid,
    bool Readable,
    bool Writable,
    string? ServiceType = null,
    string? PropertyType = null,
    bool? CurrentValue = null);

public sealed record XiaomiDeviceCapabilities
{
    public XiaomiDeviceCapabilities(
        bool isRouter = false,
        bool isSwitch = false,
        bool isLight = false,
        bool isSensor = false,
        bool isAirConditioner = false,
        bool isCamera = false,
        bool isVacuum = false,
        bool isLock = false,
        bool isPlug = false,
        IReadOnlyList<XiaomiPowerCapability>? powerProperties = null)
    {
        IsRouter = isRouter;
        IsSwitch = isSwitch;
        IsLight = isLight;
        IsSensor = isSensor;
        IsAirConditioner = isAirConditioner;
        IsCamera = isCamera;
        IsVacuum = isVacuum;
        IsLock = isLock;
        IsPlug = isPlug;
        PowerProperties = powerProperties ?? [];
    }

    public bool IsRouter { get; init; }
    public bool IsSwitch { get; init; }
    public bool IsLight { get; init; }
    public bool IsSensor { get; init; }
    public bool IsAirConditioner { get; init; }
    public bool IsCamera { get; init; }
    public bool IsVacuum { get; init; }
    public bool IsLock { get; init; }
    public bool IsPlug { get; init; }
    public IReadOnlyList<XiaomiPowerCapability> PowerProperties { get; init; }

    public bool CanPowerOnOff => PowerProperties.Any(value => value.Readable && value.Writable);

    public XiaomiPowerCapability? PowerCapability =>
        PowerProperties.FirstOrDefault(value => value.Readable && value.Writable);
}

public sealed record XiaomiAccountDevice(
    string Did,
    string? Model,
    string Name,
    string? CustomName,
    XiaomiAccountDeviceType DeviceType,
    bool? Online,
    string? LocalIp,
    string? HomeId,
    string? RoomId,
    string? HomeName,
    string? RoomName,
    string? PartnerId,
    string? Hardware,
    string? FirmwareVersion,
    bool IsShared,
    XiaomiDeviceCapabilities Capabilities,
    string? SpecType = null,
    XiaomiDeviceDefinition? Definition = null)
{
    public string DisplayName => FirstNonEmpty(CustomName, Name, Model, Did);

    public bool IsRouter => DeviceType == XiaomiAccountDeviceType.Router || Capabilities.IsRouter;

    public string SearchText => string.Join(' ', new[]
    {
        DisplayName, Name, CustomName, Model, HomeName, RoomName, DeviceType.ToString()
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;
}

public sealed record XiaomiPowerStateResult(
    bool Success,
    bool? Value,
    int? XiaomiCode = null,
    string? Error = null);

/// <summary>
/// A normalized subset of a Xiaomi MIoT Spec value format.  The raw spec is
/// intentionally kept out of the UI layer; callers use this model instead.
/// </summary>
public enum XiaomiMiotValueType
{
    Unknown = 0,
    Boolean = 1,
    Integer = 2,
    Number = 3,
    String = 4,
    Object = 5
}

public sealed record XiaomiValueRange(decimal Minimum, decimal Maximum, decimal Step);

public sealed record XiaomiValueListItem(
    object? Value,
    string RawValue,
    string Name,
    string ChineseName,
    string? OfficialDescription = null);

public sealed record XiaomiPropertyDefinition(
    int Siid,
    int Piid,
    string Type,
    string Name,
    string ChineseName,
    bool Readable,
    bool Writable,
    bool Notifiable,
    XiaomiMiotValueType ValueType,
    XiaomiValueRange? ValueRange,
    IReadOnlyList<XiaomiValueListItem> ValueList,
    string? Unit,
    string? OfficialDescription = null,
    object? CurrentValue = null)
{
    public bool HasValueList => ValueList.Count > 0;
    public bool IsNumeric => ValueType is XiaomiMiotValueType.Integer or XiaomiMiotValueType.Number;
}

public sealed record XiaomiActionArgument(
    int Piid,
    string Name,
    string ChineseName,
    XiaomiMiotValueType ValueType,
    XiaomiValueRange? ValueRange,
    IReadOnlyList<XiaomiValueListItem> ValueList,
    string? Unit,
    bool Required = true);

public sealed record XiaomiActionDefinition(
    int Siid,
    int Aiid,
    string Type,
    string Name,
    string ChineseName,
    IReadOnlyList<XiaomiActionArgument> InputArguments,
    IReadOnlyList<XiaomiActionArgument> OutputArguments,
    string? OfficialDescription = null)
{
    public bool HasInputArguments => InputArguments.Count > 0;
}

public sealed record XiaomiEventDefinition(
    int Siid,
    int Eiid,
    string Type,
    string Name,
    string ChineseName,
    IReadOnlyList<XiaomiActionArgument> Arguments,
    string? OfficialDescription = null);

public sealed record XiaomiServiceDefinition(
    int Siid,
    string Type,
    string Name,
    string ChineseName,
    IReadOnlyList<XiaomiPropertyDefinition> Properties,
    IReadOnlyList<XiaomiActionDefinition> Actions,
    IReadOnlyList<XiaomiEventDefinition> Events,
    string? OfficialDescription = null);

public sealed record XiaomiDeviceDefinition(
    string? SpecType,
    IReadOnlyList<XiaomiServiceDefinition> Services)
{
    public static XiaomiDeviceDefinition Empty(string? specType = null) => new(specType, []);

    public IReadOnlyList<XiaomiPropertyDefinition> Properties =>
        Services.SelectMany(value => value.Properties).ToArray();

    public IReadOnlyList<XiaomiPropertyDefinition> ReadableProperties =>
        Properties.Where(value => value.Readable).ToArray();

    public IReadOnlyList<XiaomiPropertyDefinition> WritableProperties =>
        Properties.Where(value => value.Writable).ToArray();

    public IReadOnlyList<XiaomiActionDefinition> Actions =>
        Services.SelectMany(value => value.Actions).ToArray();

    public IReadOnlyList<XiaomiEventDefinition> Events =>
        Services.SelectMany(value => value.Events).ToArray();

    public XiaomiDeviceDefinition WithCurrentValues(IReadOnlyDictionary<(int Siid, int Piid), object?> values) =>
        this with
        {
            Services = Services.Select(service => service with
            {
                Properties = service.Properties.Select(property =>
                    values.TryGetValue((property.Siid, property.Piid), out var value)
                        ? property with { CurrentValue = value }
                        : property).ToArray()
            }).ToArray()
        };
}

public sealed record XiaomiPropertyReadResult(
    int Siid,
    int Piid,
    bool Success,
    object? Value,
    int? XiaomiCode = null,
    string? Error = null);

public sealed record XiaomiPropertyOperationResult(
    bool Success,
    int? XiaomiCode = null,
    string? Error = null);

public sealed record XiaomiActionInvocationResult(
    bool Success,
    IReadOnlyList<object?> OutputArguments,
    int? XiaomiCode = null,
    string? Error = null);
