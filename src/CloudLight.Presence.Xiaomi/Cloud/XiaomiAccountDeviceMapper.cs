using System.Text.Json;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Xiaomi.Cloud;

public static class XiaomiAccountDeviceMapper
{
    public static XiaomiAccountDevice? Map(
        JsonElement data,
        string? homeId = null,
        string? homeName = null,
        string? roomId = null,
        string? roomName = null,
        bool isShared = false)
    {
        var did = XiaomiAppGatewayClient.Text(data, "did");
        if (string.IsNullOrWhiteSpace(did)) return null;
        var model = XiaomiAppGatewayClient.Text(data, "model");
        var specType = XiaomiAppGatewayClient.Text(data, "spec_type", "specType", "miot_spec", "miotSpec", "miot_type", "miotType", "spec", "urn");
        var capabilities = XiaomiDeviceCapabilityResolver.FromMetadata(model, specType);
        var customName = XiaomiAppGatewayClient.Text(data, "custom_name", "customName", "nickname", "alias");
        var name = XiaomiAppGatewayClient.Text(data, "name", "device_name", "deviceName") ?? model ?? "未命名设备";
        var resolvedHomeId = XiaomiAppGatewayClient.Text(data, "home_id", "homeId") ?? homeId;
        var resolvedRoomId = XiaomiAppGatewayClient.Text(data, "room_id", "roomId") ?? roomId;
        var type = XiaomiDeviceCapabilityResolver.ClassifyDeviceType(model, specType, capabilities);
        return new XiaomiAccountDevice(
            did,
            model,
            name,
            customName,
            type,
            Boolean(data, "isOnline", "online", "is_online"),
            XiaomiAppGatewayClient.Text(data, "localip", "localIp", "local_ip", "ip"),
            resolvedHomeId,
            resolvedRoomId,
            XiaomiAppGatewayClient.Text(data, "home_name", "homeName") ?? homeName,
            XiaomiAppGatewayClient.Text(data, "room_name", "roomName") ?? roomName,
            XiaomiAppGatewayClient.Text(data, "partner_id", "partnerId", "partnerID"),
            XiaomiAppGatewayClient.Text(data, "hardware", "hardwareModel", "hw_ver", "hardware_version"),
            XiaomiAppGatewayClient.Text(data, "firmwareVersion", "firmware_version", "fw_ver", "fwVersion"),
            isShared,
            capabilities,
            specType);
    }

    private static bool? Boolean(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value))
            {
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
                if (int.TryParse(value.ToString(), out var integer) && integer is 0 or 1) return integer == 1;
                if (bool.TryParse(value.ToString(), out var result)) return result;
            }
        return null;
    }
}
