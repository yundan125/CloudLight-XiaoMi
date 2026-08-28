using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;

namespace CloudLight.Presence.Xiaomi.Cloud;

/// <summary>
/// Parses Xiaomi's MIOt Spec instance into the app's stable device definition.
/// SIID/PIID/AIID are taken only from the current instance, never from a model
/// lookup table.
/// </summary>
public sealed class XiaomiDeviceCapabilityResolver : IXiaomiDeviceCapabilityResolver
{
    private readonly HttpClient _http;
    private readonly IMiotLocalizationService _localization;
    private readonly ConcurrentDictionary<string, Task<XiaomiDeviceDefinition>> _specCache = new(StringComparer.Ordinal);

    public XiaomiDeviceCapabilityResolver(HttpClient? http = null, IMiotLocalizationService? localization = null)
    {
        _http = http ?? CreateHttpClient();
        _localization = localization ?? new MiotChineseLocalizationService();
    }

    public async Task<XiaomiDeviceCapabilities> ResolveCapabilitiesAsync(
        XiaomiAccountDevice device,
        CancellationToken cancellationToken)
    {
        var metadata = FromMetadata(device.Model, device.SpecType);
        var definition = await ResolveDefinitionAsync(device, cancellationToken);
        return Merge(metadata, ToCapabilities(definition, device.Model, device.SpecType));
    }

    public async Task<XiaomiDeviceDefinition> ResolveDefinitionAsync(
        XiaomiAccountDevice device,
        CancellationToken cancellationToken)
    {
        if (device.Definition is { Services.Count: > 0 } definition) return definition;
        if (string.IsNullOrWhiteSpace(device.SpecType)) return XiaomiDeviceDefinition.Empty(device.SpecType);

        var task = _specCache.GetOrAdd(
            device.SpecType,
            specType => LoadSpecDefinitionAsync(specType, CancellationToken.None));
        return await task.WaitAsync(cancellationToken);
    }

    public static XiaomiDeviceCapabilities ParseSpec(JsonElement root) => ParseSpec(root, null, null);

    public static XiaomiDeviceDefinition ParseDeviceDefinition(JsonElement root, string? specType = null) =>
        ParseDeviceDefinitionCore(root, specType, new MiotChineseLocalizationService());

    public static XiaomiDeviceCapabilities FromMetadata(string? model, string? specType)
    {
        var router = ContainsType(model, ".router.") || ContainsType(specType, ":device:router:");
        var switchDevice = ContainsType(specType, ":device:switch:");
        var light = ContainsType(specType, ":device:light:");
        var sensor = ContainsType(specType, ":device:sensor:");
        var airConditioner = ContainsType(specType, ":device:air-conditioner:") || ContainsType(specType, ":device:air_conditioner:");
        var camera = ContainsType(specType, ":device:camera:");
        var vacuum = ContainsType(specType, ":device:air-purifier:") || ContainsType(specType, ":device:vacuum:");
        var lockDevice = ContainsType(specType, ":device:lock:");
        var plug = ContainsType(specType, ":device:outlet:") || ContainsType(specType, ":device:plug:");
        return new XiaomiDeviceCapabilities(router, switchDevice, light, sensor, airConditioner, camera, vacuum, lockDevice, plug);
    }

    public static XiaomiAccountDeviceType ClassifyDeviceType(
        string? model,
        string? specType,
        XiaomiDeviceCapabilities capabilities)
    {
        if (capabilities.IsRouter || ContainsType(model, ".router.") || ContainsType(specType, ":device:router:")) return XiaomiAccountDeviceType.Router;
        if (capabilities.IsSwitch) return XiaomiAccountDeviceType.Switch;
        if (capabilities.IsLight) return XiaomiAccountDeviceType.Light;
        if (capabilities.IsSensor) return XiaomiAccountDeviceType.Sensor;
        if (capabilities.IsAirConditioner) return XiaomiAccountDeviceType.AirConditioner;
        if (capabilities.IsCamera) return XiaomiAccountDeviceType.Camera;
        if (capabilities.IsVacuum) return XiaomiAccountDeviceType.Vacuum;
        if (capabilities.IsLock) return XiaomiAccountDeviceType.Lock;
        if (capabilities.IsPlug) return XiaomiAccountDeviceType.Plug;
        return XiaomiAccountDeviceType.Unknown;
    }

    public static XiaomiDeviceCapabilities ParseSpec(JsonElement root, string? model, string? specType) =>
        Merge(FromMetadata(model, specType), ToCapabilities(ParseDeviceDefinition(root, specType), model, specType));

    private async Task<XiaomiDeviceDefinition> LoadSpecDefinitionAsync(string specType, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(XiaomiApiEndpoints.MiotSpecInstanceUrl)
        {
            Query = $"type={Uri.EscapeDataString(specType)}"
        }.Uri;
        using var response = await _http.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MIoT Spec HTTP {(int)response.StatusCode}。");
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return ParseDeviceDefinitionCore(root, specType, _localization);
    }

    private static XiaomiDeviceDefinition ParseDeviceDefinitionCore(
        JsonElement root,
        string? specType,
        IMiotLocalizationService localization)
    {
        if (!root.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
            return XiaomiDeviceDefinition.Empty(specType);

        var result = new List<XiaomiServiceDefinition>();
        foreach (var service in services.EnumerateArray())
        {
            if (!ReadInteger(service, "iid", out var siid)) continue;
            var type = Text(service, "type") ?? string.Empty;
            var rawName = TypeName(type, "service");
            var description = Text(service, "description_zh_cn", "descriptionZhCn", "description_zh", "description");
            var properties = ParseProperties(service, siid, localization);
            var lookup = properties.ToDictionary(value => value.Piid);
            result.Add(new XiaomiServiceDefinition(
                siid,
                type,
                rawName,
                localization.ServiceName(rawName, description),
                properties,
                ParseActions(service, siid, lookup, localization),
                ParseEvents(service, siid, lookup, localization),
                description));
        }

        return new XiaomiDeviceDefinition(specType, result);
    }

    private static IReadOnlyList<XiaomiPropertyDefinition> ParseProperties(
        JsonElement service,
        int siid,
        IMiotLocalizationService localization)
    {
        if (!service.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array) return [];
        var result = new List<XiaomiPropertyDefinition>();
        foreach (var property in properties.EnumerateArray())
        {
            if (!ReadInteger(property, "iid", out var piid)) continue;
            var type = Text(property, "type") ?? string.Empty;
            var rawName = TypeName(type, "property");
            var description = Text(property, "description_zh_cn", "descriptionZhCn", "description_zh", "description");
            var access = ReadAccess(property);
            result.Add(new XiaomiPropertyDefinition(
                siid,
                piid,
                type,
                rawName,
                localization.PropertyName(rawName, description),
                access.Contains("read"),
                access.Contains("write"),
                access.Contains("notify"),
                ValueType(Text(property, "format")),
                ParseRange(property),
                ParseValueList(property, localization),
                localization.UnitName(Text(property, "unit")),
                description));
        }
        return result;
    }

    private static IReadOnlyList<XiaomiActionDefinition> ParseActions(
        JsonElement service,
        int siid,
        IReadOnlyDictionary<int, XiaomiPropertyDefinition> properties,
        IMiotLocalizationService localization)
    {
        if (!service.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return [];
        var result = new List<XiaomiActionDefinition>();
        foreach (var action in actions.EnumerateArray())
        {
            if (!ReadInteger(action, "iid", out var aiid)) continue;
            var type = Text(action, "type") ?? string.Empty;
            var rawName = TypeName(type, "action");
            var description = Text(action, "description_zh_cn", "descriptionZhCn", "description_zh", "description");
            result.Add(new XiaomiActionDefinition(
                siid,
                aiid,
                type,
                rawName,
                localization.ActionName(rawName, description),
                ParseArguments(action, "in", properties),
                ParseArguments(action, "out", properties),
                description));
        }
        return result;
    }

    private static IReadOnlyList<XiaomiEventDefinition> ParseEvents(
        JsonElement service,
        int siid,
        IReadOnlyDictionary<int, XiaomiPropertyDefinition> properties,
        IMiotLocalizationService localization)
    {
        if (!service.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return [];
        var result = new List<XiaomiEventDefinition>();
        foreach (var value in events.EnumerateArray())
        {
            if (!ReadInteger(value, "iid", out var eiid)) continue;
            var type = Text(value, "type") ?? string.Empty;
            var rawName = TypeName(type, "event");
            var description = Text(value, "description_zh_cn", "descriptionZhCn", "description_zh", "description");
            result.Add(new XiaomiEventDefinition(
                siid,
                eiid,
                type,
                rawName,
                localization.EventName(rawName, description),
                ParseArguments(value, "argument", properties),
                description));
        }
        return result;
    }

    private static IReadOnlyList<XiaomiActionArgument> ParseArguments(
        JsonElement item,
        string name,
        IReadOnlyDictionary<int, XiaomiPropertyDefinition> properties)
    {
        if (!item.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        var result = new List<XiaomiActionArgument>();
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            index++;
            if (!int.TryParse(value.ToString(), out var piid)) continue;
            if (properties.TryGetValue(piid, out var property))
            {
                result.Add(new XiaomiActionArgument(
                    piid,
                    property.Name,
                    property.ChineseName,
                    property.ValueType,
                    property.ValueRange,
                    property.ValueList,
                    property.Unit));
            }
            else
            {
                result.Add(new XiaomiActionArgument(
                    piid,
                    $"argument-{index}",
                    $"操作参数 {index}",
                    XiaomiMiotValueType.String,
                    null,
                    [],
                    null));
            }
        }
        return result;
    }

    private static IReadOnlyList<XiaomiValueListItem> ParseValueList(JsonElement property, IMiotLocalizationService localization)
    {
        if (!TryProperty(property, out var values, "value-list", "valueList") || values.ValueKind != JsonValueKind.Array) return [];
        var result = new List<XiaomiValueListItem>();
        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("value", out var value)) continue;
            var typed = ToValue(value);
            var raw = value.ToString();
            var description = Text(item, "description_zh_cn", "descriptionZhCn", "description_zh", "description");
            result.Add(new XiaomiValueListItem(typed, raw, description ?? raw, localization.ValueName(raw, description), description));
        }
        return result;
    }

    private static XiaomiValueRange? ParseRange(JsonElement property)
    {
        if (!TryProperty(property, out var range, "value-range", "valueRange") || range.ValueKind != JsonValueKind.Array || range.GetArrayLength() < 2)
            return null;
        var values = range.EnumerateArray().Select(ToDecimal).ToArray();
        if (values.Length < 2 || values[0] is null || values[1] is null) return null;
        var step = values.Length > 2 && values[2] is > 0 ? values[2]!.Value : 1m;
        return new XiaomiValueRange(values[0]!.Value, values[1]!.Value, step);
    }

    private static XiaomiDeviceCapabilities ToCapabilities(
        XiaomiDeviceDefinition definition,
        string? model,
        string? specType)
    {
        var metadata = FromMetadata(model, specType);
        var router = metadata.IsRouter;
        var switchDevice = metadata.IsSwitch;
        var light = metadata.IsLight;
        var sensor = metadata.IsSensor;
        var airConditioner = metadata.IsAirConditioner;
        var camera = metadata.IsCamera;
        var vacuum = metadata.IsVacuum;
        var lockDevice = metadata.IsLock;
        var plug = metadata.IsPlug;
        var powers = new List<XiaomiPowerCapability>();

        foreach (var service in definition.Services)
        {
            var serviceType = service.Type;
            router |= ContainsType(serviceType, ":service:router:");
            switchDevice |= ContainsType(serviceType, ":service:switch:");
            light |= ContainsType(serviceType, ":service:light:");
            sensor |= ContainsType(serviceType, ":service:environment:") || ContainsType(serviceType, ":service:illumination:");
            airConditioner |= ContainsType(serviceType, ":service:air-conditioner:");
            camera |= ContainsType(serviceType, ":service:camera:");
            vacuum |= ContainsType(serviceType, ":service:air-purifier:") || ContainsType(serviceType, ":service:vacuum:");
            lockDevice |= ContainsType(serviceType, ":service:lock:");
            plug |= ContainsType(serviceType, ":service:outlet:");
            foreach (var property in service.Properties.Where(value =>
                         string.Equals(value.Name, "on", StringComparison.OrdinalIgnoreCase) &&
                         value.ValueType == XiaomiMiotValueType.Boolean))
            {
                powers.Add(new XiaomiPowerCapability(
                    property.Siid,
                    property.Piid,
                    property.Readable,
                    property.Writable,
                    serviceType,
                    property.Type,
                    property.CurrentValue is bool boolean ? boolean : null));
            }
        }

        return new XiaomiDeviceCapabilities(
            router,
            switchDevice,
            light,
            sensor,
            airConditioner,
            camera,
            vacuum,
            lockDevice,
            plug,
            powers.DistinctBy(value => (value.Siid, value.Piid)).ToArray());
    }

    private static XiaomiDeviceCapabilities Merge(XiaomiDeviceCapabilities metadata, XiaomiDeviceCapabilities resolved) =>
        resolved with
        {
            IsRouter = metadata.IsRouter || resolved.IsRouter,
            IsSwitch = metadata.IsSwitch || resolved.IsSwitch,
            IsLight = metadata.IsLight || resolved.IsLight,
            IsSensor = metadata.IsSensor || resolved.IsSensor,
            IsAirConditioner = metadata.IsAirConditioner || resolved.IsAirConditioner,
            IsCamera = metadata.IsCamera || resolved.IsCamera,
            IsVacuum = metadata.IsVacuum || resolved.IsVacuum,
            IsLock = metadata.IsLock || resolved.IsLock,
            IsPlug = metadata.IsPlug || resolved.IsPlug
        };

    private static HashSet<string> ReadAccess(JsonElement property)
    {
        var access = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!property.TryGetProperty("access", out var value)) return access;
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) access.Add(item.ToString());
        else if (value.ValueKind == JsonValueKind.String) access.Add(value.GetString() ?? string.Empty);
        return access;
    }

    private static XiaomiMiotValueType ValueType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "bool" => XiaomiMiotValueType.Boolean,
        "string" => XiaomiMiotValueType.String,
        "float" or "double" => XiaomiMiotValueType.Number,
        "int" or "int8" or "int16" or "int32" or "int64" or "uint" or "uint8" or "uint16" or "uint32" or "uint64" => XiaomiMiotValueType.Integer,
        "object" => XiaomiMiotValueType.Object,
        _ => XiaomiMiotValueType.Unknown
    };

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.Clone()
    };

    private static decimal? ToDecimal(JsonElement value) =>
        decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static bool ReadInteger(JsonElement item, string name, out int value)
    {
        value = 0;
        return item.TryGetProperty(name, out var itemValue) && int.TryParse(itemValue.ToString(), out value);
    }

    private static string TypeName(string? type, string kind)
    {
        if (string.IsNullOrWhiteSpace(type)) return "未知功能";
        var parts = type.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index + 1 < parts.Length; index++)
            if (string.Equals(parts[index], kind, StringComparison.OrdinalIgnoreCase)) return parts[index + 1];
        return type;
    }

    private static bool TryProperty(JsonElement item, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static bool ContainsType(string? value, string fragment) => value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;

    private static string? Text(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                return value.ToString();
        return null;
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(20) };
}
