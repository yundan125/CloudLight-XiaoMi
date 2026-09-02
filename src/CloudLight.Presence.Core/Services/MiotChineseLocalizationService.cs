using System.Text.RegularExpressions;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

/// <summary>
/// Central Chinese wording for MIoT's shared vocabulary.  Device-specific
/// descriptions still win when Xiaomi provides a Chinese one, so this table
/// only supplies the stable standard vocabulary and readable fallbacks.
/// </summary>
public sealed class MiotChineseLocalizationService : IMiotLocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> ServiceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["switch"] = "开关",
        ["outlet"] = "插座",
        ["device-information"] = "设备信息",
        ["device-status"] = "设备状态",
        ["light"] = "灯光",
        ["fan"] = "风扇",
        ["air-conditioner"] = "空调",
        ["air-purifier"] = "空气净化",
        ["environment"] = "环境信息",
        ["illumination"] = "照明信息",
        ["temperature-humidity-sensor"] = "温湿度传感器",
        ["battery"] = "电池",
        ["sleep"] = "睡眠",
        ["vital-signs"] = "生命体征",
        ["vibration"] = "震动",
        ["motion-data"] = "运动数据",
        ["indicator-light"] = "指示灯",
        ["router"] = "路由器",
        ["camera-control"] = "摄像头控制",
        ["vacuum"] = "扫地机器人",
        ["lock"] = "门锁",
        ["physical-controls-locked"] = "物理按键锁定",
        ["power-consumption"] = "用电统计",
        ["charging-protection"] = "充电保护",
        ["cycle"] = "循环任务",
        ["quick-countdown"] = "快速倒计时",
        ["max-power-limit"] = "最大功率限制",
        ["over-use-ele-alert"] = "超额用电提醒",
        ["on-off-count"] = "开关次数",
        ["charge-prt-ext"] = "充电保护扩展",
        ["power-limit-ext"] = "功率限制扩展"
    };

    private static readonly IReadOnlyDictionary<string, string> PropertyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["on"] = "电源",
        ["manufacturer"] = "制造商",
        ["model"] = "型号",
        ["serial-number"] = "序列号",
        // Some devices expose both serial-number and the legacy serial-no
        // alias. Keep both properties visible while making the labels distinct.
        ["serial-no"] = "设备序列号",
        ["firmware-revision"] = "固件版本",
        ["power-consumption"] = "功率",
        ["electric-power"] = "功率",
        ["power"] = "功率",
        ["power-ext"] = "扩展功率",
        ["download-speed"] = "下载速度",
        ["upload-speed"] = "上传速度",
        ["connected-device-number"] = "已连接设备数",
        ["voltage"] = "电压",
        ["electric-current"] = "电流",
        ["temperature"] = "温度",
        ["target-temperature"] = "目标温度",
        ["relative-humidity"] = "湿度",
        ["illumination"] = "光照度",
        ["brightness"] = "亮度",
        ["color-temperature"] = "色温",
        ["mode"] = "工作模式",
        ["fan-level"] = "风速",
        ["fan-speed-level"] = "风速",
        ["volume"] = "音量",
        ["indicator-light"] = "指示灯",
        ["child-lock"] = "童锁",
        ["physical-controls-locked"] = "按键锁定",
        ["power-off-memory"] = "断电记忆",
        ["power-off-memory-mode"] = "断电记忆",
        ["default-power-on-state"] = "通电默认状态",
        ["usb-on"] = "USB 电源",
        ["usb-switch"] = "USB 电源",
        ["charging-state"] = "充电状态",
        ["sleep-state"] = "睡眠状态",
        ["device-wearing-status"] = "佩戴状态",
        ["battery-level"] = "电量",
        ["status"] = "状态",
        ["alarm"] = "报警状态",
        ["fault"] = "故障状态",
        ["filter-life-level"] = "滤芯剩余",
        ["water-level"] = "水位",
        ["timer"] = "定时",
        ["countdown"] = "倒计时",
        ["start-time"] = "开始时间",
        ["end-time"] = "结束时间",
        ["duration"] = "持续时间",
        ["left-time"] = "剩余时间",
        ["protect-time"] = "保护时间",
        ["data-value"] = "数据值",
        ["over-ele-day"] = "当日超额用电量",
        ["over-ele-month"] = "当月超额用电量",
        ["on-off-count"] = "开关次数",
        ["sleep-mode"] = "睡眠模式",
        ["air-quality"] = "空气质量",
        ["pm2.5-density"] = "PM2.5",
        ["motor-speed"] = "电机转速",
        ["door-state"] = "门状态",
        ["lock-state"] = "锁状态",
        ["service-name"] = "设备名称"
    };

    private static readonly IReadOnlyDictionary<string, string> ActionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = "开始",
        ["stop"] = "停止",
        ["pause"] = "暂停",
        ["resume"] = "继续",
        ["identify"] = "识别设备",
        ["toggle"] = "切换",
        ["execute"] = "执行",
        ["set-mode"] = "设置模式",
        ["turn-on"] = "打开",
        ["turn-off"] = "关闭",
        ["find"] = "查找设备",
        ["locate"] = "定位设备",
        ["measure-heatrate"] = "测量心率",
        ["end-measure-heatrate"] = "结束心率测量",
        ["vibration"] = "震动",
        ["end-vibration"] = "停止震动",
        ["clean"] = "开始清扫",
        ["start-sweep"] = "开始清扫",
        ["stop-sweeping"] = "停止清扫",
        ["dock"] = "返回充电座",
        ["charge"] = "开始充电",
        ["reset"] = "重置",
        ["factory-reset"] = "恢复出厂设置",
        ["unlock"] = "解锁",
        ["unlatch"] = "解锁",
        ["delete"] = "删除数据",
        ["clear"] = "清除数据",
        ["unbind"] = "解除绑定"
    };

    private static readonly IReadOnlyDictionary<string, string> EventNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["no-one-determine-time"] = "无人状态",
        ["alarm"] = "报警事件",
        ["motion-state-changed"] = "移动状态变化",
        ["fault"] = "故障事件",
        ["button-pressed"] = "按键事件",
        ["water-leakage"] = "漏水事件",
        ["open-event"] = "开启事件",
        ["over-day-push"] = "当日超额提醒",
        ["over-month-push"] = "当月超额提醒",
        ["abnormal-vital-signs"] = "异常生命体征",
        ["sport-state-change"] = "运动状态变化",
        ["vitality-goal-achieve"] = "活力目标达成"
    };

    private static readonly IReadOnlyDictionary<string, string> ValueNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["on"] = "开启",
        ["off"] = "关闭",
        ["true"] = "开启",
        ["false"] = "关闭",
        ["auto"] = "自动",
        ["silent"] = "静音",
        ["quiet"] = "静音",
        ["sleep"] = "睡眠",
        ["low"] = "低",
        ["medium"] = "中",
        ["mid"] = "中",
        ["high"] = "高",
        ["turbo"] = "强劲",
        ["favorite"] = "最爱",
        ["normal"] = "标准",
        ["manual"] = "手动",
        ["eco"] = "节能",
        ["none"] = "无",
        ["enabled"] = "开启",
        ["disabled"] = "关闭",
        ["open"] = "打开",
        ["close"] = "关闭",
        ["locked"] = "已锁定",
        ["unlocked"] = "已解锁",
        ["charging"] = "充电中",
        ["idle"] = "空闲",
        ["running"] = "运行中",
        ["paused"] = "已暂停",
        ["error"] = "故障"
    };

    private static readonly IReadOnlyDictionary<string, string> UnitNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["percentage"] = "%",
        ["celsius"] = "℃",
        ["fahrenheit"] = "℉",
        ["kelvin"] = "K",
        ["watt"] = "W",
        ["kilowatt"] = "kW",
        ["volt"] = "V",
        ["ampere"] = "A",
        ["minute"] = "分钟",
        ["minutes"] = "分钟",
        ["hour"] = "小时",
        ["hours"] = "小时",
        ["second"] = "秒",
        ["seconds"] = "秒",
        ["liter"] = "L",
        ["ppm"] = "ppm",
        ["lux"] = "lx",
        ["arcdegrees"] = "°",
        ["none"] = ""
    };

    public string ServiceName(string rawName, string? officialDescription = null) =>
        Resolve(rawName, officialDescription, ServiceNames);

    public string PropertyName(string rawName, string? officialDescription = null) =>
        Resolve(rawName, officialDescription, PropertyNames);

    public string ActionName(string rawName, string? officialDescription = null) =>
        Resolve(rawName, officialDescription, ActionNames);

    public string EventName(string rawName, string? officialDescription = null) =>
        Resolve(rawName, officialDescription, EventNames);

    public string ValueName(string rawValue, string? officialDescription = null)
    {
        if (ContainsChinese(officialDescription)) return officialDescription!.Trim();
        if (ValueNames.TryGetValue(Normalize(rawValue), out var value)) return value;
        if (!string.IsNullOrWhiteSpace(officialDescription) &&
            ValueNames.TryGetValue(Normalize(officialDescription), out value)) return value;
        return Resolve(rawValue, officialDescription, ValueNames);
    }

    public string? UnitName(string? rawUnit)
    {
        if (string.IsNullOrWhiteSpace(rawUnit)) return null;
        var key = Normalize(rawUnit);
        return UnitNames.TryGetValue(key, out var value) ? value : rawUnit.Trim();
    }

    public bool IsHighRiskAction(XiaomiActionDefinition action)
    {
        var combined = string.Join(' ', new[] { action.Name, action.ChineseName, action.Type, action.OfficialDescription }
            .Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        return new[]
        {
            "factory-reset", "factory reset", "恢复出厂", "reset", "重置", "erase", "clear-data", "删除数据", "清除数据",
            "delete", "unbind", "解除绑定", "unlock", "unlatch", "解锁", "disable-security", "关闭安全", "disarm"
        }.Any(token => combined.Contains(token, StringComparison.Ordinal));
    }

    private static string Resolve(string rawName, string? officialDescription, IReadOnlyDictionary<string, string> dictionary)
    {
        if (ContainsChinese(officialDescription)) return officialDescription!.Trim();
        var key = Normalize(rawName);
        if (dictionary.TryGetValue(key, out var value)) return value;
        var fallback = Humanize(key);
        return string.IsNullOrWhiteSpace(fallback) ? rawName : fallback;
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < trimmed.Length) trimmed = trimmed[(colon + 1)..];
        return trimmed.Trim().ToLowerInvariant();
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未知功能";
        var words = Regex.Replace(value, "[-_]+", " ").Trim();
        return words.Length == 0 ? "未知功能" : words;
    }

    private static bool ContainsChinese(string? value) => value?.Any(character => character is >= '\u3400' and <= '\u9FFF') == true;
}
