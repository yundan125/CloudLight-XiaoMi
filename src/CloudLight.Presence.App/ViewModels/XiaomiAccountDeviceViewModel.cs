using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.App.ViewModels;

public sealed class XiaomiAccountDeviceCardViewModel : ObservableObject
{
    private readonly IXiaomiAccountDeviceSource _source;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private XiaomiPowerCapability? _powerCapability;
    private XiaomiAccountDevice _device;
    private bool? _powerState;
    private bool _isCommandInProgress;
    private string _commandStatus = "";

    public XiaomiAccountDeviceCardViewModel(
        XiaomiAccountDevice device,
        IXiaomiAccountDeviceSource source,
        Action<XiaomiAccountDevice>? open = null,
        Action<XiaomiAccountDevice>? openRouterPresence = null)
    {
        _device = device;
        _source = source;
        _powerCapability = device.Capabilities.PowerCapability;
        OpenCommand = new RelayCommand(() => open?.Invoke(Device), () => open is not null);
        RouterPresenceCommand = new RelayCommand(() => openRouterPresence?.Invoke(Device), () => IsRouter && openRouterPresence is not null);
        PowerCommand = new AsyncRelayCommand(TogglePowerAsync, () => CanTogglePower);
    }

    public XiaomiAccountDevice Device => _device;
    public string Name => Device.DisplayName;
    public string TypeText => DeviceTypeText(Device.DeviceType);
    public string ModelText => string.IsNullOrWhiteSpace(Device.Model) ? "型号未知" : Device.Model!;
    public string LocationText => FormatLocation(Device.HomeName, Device.RoomName);
    public bool IsRouter => Device.IsRouter;
    public bool IsShared => Device.IsShared;
    public bool CanPowerOnOff => !IsRouter && _powerCapability is { Readable: true, Writable: true };
    public bool IsPowerControlVisible => CanPowerOnOff;
    public bool IsOnline => Device.Online == true;
    public bool IsOffline => Device.Online == false;
    public bool CanTogglePower => CanPowerOnOff && !IsOffline && _powerState is not null && !IsCommandInProgress;
    public bool IsCommandInProgress { get => _isCommandInProgress; private set => Set(ref _isCommandInProgress, value); }
    public bool? PowerState => _powerState;
    public string PowerButtonText => _powerState == true ? "关闭" : _powerState == false ? "打开" : "状态不可用";
    public string StatusText => IsOffline
        ? "设备离线"
        : CanPowerOnOff
            ? IsCommandInProgress ? _commandStatus : _commandStatus is "打开失败" or "关闭失败" or "状态暂时不可用"
                ? _commandStatus
                : _powerState switch
                {
                    true => "已开启",
                    false => "已关闭",
                    _ => "状态暂时不可用"
                }
            : Device.Online switch
            {
                true => "在线",
                false => "离线",
                _ => "状态未知"
            };
    public string StatusMark => IsOffline ? "○" : IsOnline ? "●" : "◇";
    public string StatusColor => IsOffline ? "#64748B" : IsOnline ? "#16A34A" : "#D97706";
    public string SecondaryText => IsShared ? "共享设备" : IsRouter ? "路由器 Presence" : "Xiaomi 账号设备";
    public RelayCommand OpenCommand { get; }
    public RelayCommand RouterPresenceCommand { get; }
    public AsyncRelayCommand PowerCommand { get; }

    public async Task RefreshPowerStateAsync(CancellationToken cancellationToken)
    {
        if (!CanPowerOnOff || IsOffline)
        {
            _powerState = null;
            _commandStatus = "";
            RaisePowerProperties();
            return;
        }

        try
        {
            var result = await _source.ReadPowerStateAsync(Device, _powerCapability!, cancellationToken);
            _powerState = result.Success ? result.Value : null;
            _commandStatus = result.Success ? "" : "状态暂时不可用";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _powerState = null;
            _commandStatus = "状态暂时不可用";
        }
        RaisePowerProperties();
    }

    public void UpdateDevice(XiaomiAccountDevice device)
    {
        var oldPower = _powerCapability;
        _device = device;
        var currentPower = device.Capabilities.PowerCapability;
        _powerCapability = currentPower;
        if (oldPower is null || currentPower is null || oldPower.Siid != currentPower.Siid || oldPower.Piid != currentPower.Piid || device.Online == false)
            _powerState = null;
        if (!CanPowerOnOff) _powerState = null;
        _commandStatus = "";
        Raise(nameof(Device));
        Raise(nameof(Name)); Raise(nameof(TypeText)); Raise(nameof(ModelText)); Raise(nameof(LocationText));
        Raise(nameof(IsRouter)); Raise(nameof(IsShared)); Raise(nameof(CanPowerOnOff)); Raise(nameof(IsPowerControlVisible));
        Raise(nameof(IsOnline)); Raise(nameof(IsOffline)); Raise(nameof(StatusText)); Raise(nameof(StatusMark)); Raise(nameof(StatusColor)); Raise(nameof(SecondaryText));
        OpenCommand.Refresh(); RouterPresenceCommand.Refresh(); PowerCommand.Refresh(); RaisePowerProperties();
    }

    public async Task TogglePowerAsync()
    {
        if (!await _commandGate.WaitAsync(0)) return;
        var previous = false;
        var target = false;
        try
        {
            if (!CanTogglePower || _powerCapability is null || _powerState is not { } current) return;
            previous = current;
            target = !previous;
            IsCommandInProgress = true;
            _commandStatus = target ? "正在打开…" : "正在关闭…";
            RaisePowerProperties();
            XiaomiPowerStateResult setResult;
            try
            {
                setResult = await _source.SetPowerStateAsync(Device, _powerCapability, target, CancellationToken.None);
            }
            catch
            {
                _powerState = previous;
                _commandStatus = target ? "打开失败" : "关闭失败";
                return;
            }

            if (!setResult.Success)
            {
                _powerState = previous;
                _commandStatus = target ? "打开失败" : "关闭失败";
                return;
            }

            XiaomiPowerStateResult readback;
            try
            {
                readback = await _source.ReadPowerStateAsync(Device, _powerCapability, CancellationToken.None);
            }
            catch
            {
                _powerState = null;
                _commandStatus = "状态暂时不可用";
                return;
            }

            if (!readback.Success || readback.Value is null)
            {
                _powerState = null;
                _commandStatus = "状态暂时不可用";
                return;
            }

            _powerState = readback.Value;
            _commandStatus = readback.Value == target
                ? target ? "已开启" : "已关闭"
                : target ? "打开失败" : "关闭失败";
        }
        catch
        {
            _powerState = previous;
            _commandStatus = target ? "打开失败" : "关闭失败";
        }
        finally
        {
            if (IsCommandInProgress)
            {
                IsCommandInProgress = false;
                RaisePowerProperties();
            }
            _commandGate.Release();
        }
    }

    private void RaisePowerProperties()
    {
        Raise(nameof(PowerState)); Raise(nameof(PowerButtonText)); Raise(nameof(StatusText));
        Raise(nameof(CanTogglePower)); Raise(nameof(IsCommandInProgress)); PowerCommand.Refresh();
    }

    private static string FormatLocation(string? home, string? room)
    {
        if (!string.IsNullOrWhiteSpace(home) && !string.IsNullOrWhiteSpace(room)) return $"{home} · {room}";
        return string.IsNullOrWhiteSpace(home) ? string.IsNullOrWhiteSpace(room) ? "位置未知" : room! : home!;
    }

    private static string DeviceTypeText(XiaomiAccountDeviceType type) => type switch
    {
        XiaomiAccountDeviceType.Router => "路由器",
        XiaomiAccountDeviceType.Switch => "智能开关",
        XiaomiAccountDeviceType.Light => "灯",
        XiaomiAccountDeviceType.Sensor => "传感器",
        XiaomiAccountDeviceType.AirConditioner => "空调",
        XiaomiAccountDeviceType.Camera => "摄像头",
        XiaomiAccountDeviceType.Vacuum => "扫地机器人",
        XiaomiAccountDeviceType.Lock => "门锁",
        XiaomiAccountDeviceType.Plug => "插座",
        _ => "米家设备"
    };
}
