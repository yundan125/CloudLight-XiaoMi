using System.Text.Json;
using System.Net;
using System.Net.Http;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using CloudLight.Presence.Xiaomi.Cloud;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class XiaomiAccountDeviceTests
{
    [Fact]
    public void DynamicSpecFindsWritableOnAtActualSiidAndPiid()
    {
        using var document = JsonDocument.Parse("""
        {
          "services": [
            { "iid": 2, "type": "urn:miot-spec-v2:service:switch:0000780C:1", "properties": [
              { "iid": 1, "type": "urn:miot-spec-v2:property:on:00000006:1", "format": "bool", "access": ["read", "write", "notify"] }
            ] }
          ]
        }
        """);

        var capabilities = XiaomiDeviceCapabilityResolver.ParseSpec(document.RootElement);

        Assert.True(capabilities.CanPowerOnOff);
        var power = Assert.Single(capabilities.PowerProperties);
        Assert.Equal(2, power.Siid);
        Assert.Equal(1, power.Piid);
        Assert.True(power.Readable);
        Assert.True(power.Writable);
    }

    [Fact]
    public void DynamicSpecDoesNotTreatAnUnrelatedBooleanAsPower()
    {
        using var document = JsonDocument.Parse("""
        {
          "services": [
            { "iid": 3, "type": "urn:miot-spec-v2:service:environment:0000780B:1", "properties": [
              { "iid": 2, "type": "urn:miot-spec-v2:property:status:00000006:1", "format": "bool", "access": ["read", "write"] }
            ] }
          ]
        }
        """);

        var capabilities = XiaomiDeviceCapabilityResolver.ParseSpec(document.RootElement);

        Assert.False(capabilities.CanPowerOnOff);
        Assert.Empty(capabilities.PowerProperties);
    }

    [Fact]
    public void DynamicSpecBuildsLocalizedPropertiesActionsArgumentsAndEvents()
    {
        using var document = JsonDocument.Parse("""
        {
          "services": [
            {
              "iid": 3,
              "type": "urn:miot-spec-v2:service:outlet:0000780C:1",
              "properties": [
                { "iid": 1, "type": "urn:miot-spec-v2:property:on:00000006:1", "format": "bool", "access": ["read", "write", "notify"] },
                { "iid": 2, "type": "urn:miot-spec-v2:property:mode:00000008:1", "format": "uint8", "access": ["read", "write"], "value-list": [{ "value": 0, "description": "auto" }, { "value": 1, "description": "silent" }] },
                { "iid": 3, "type": "urn:miot-spec-v2:property:brightness:0000000D:1", "format": "uint8", "access": ["read", "write"], "value-range": [1, 100, 1], "unit": "percentage" },
                { "iid": 4, "type": "urn:miot-spec-v2:property:service-name:00000000:1", "format": "string", "access": ["write"] },
                { "iid": 5, "type": "urn:miot-spec-v2:property:temperature:00000020:1", "format": "float", "access": ["read"], "unit": "celsius" }
              ],
              "actions": [
                { "iid": 1, "type": "urn:miot-spec-v2:action:start:00002802:1", "in": [], "out": [] },
                { "iid": 2, "type": "urn:miot-spec-v2:action:execute:00002804:1", "in": [2, 3], "out": [5] }
              ],
              "events": [
                { "iid": 1, "type": "urn:miot-spec-v2:event:alarm:00005001:1", "argument": [5] }
              ]
            }
          ]
        }
        """);

        var definition = XiaomiDeviceCapabilityResolver.ParseDeviceDefinition(document.RootElement, "urn:miot-spec-v2:device:outlet:0000A002:1");

        var service = Assert.Single(definition.Services);
        Assert.Equal("插座", service.ChineseName);
        Assert.Equal(5, service.Properties.Count);
        Assert.Equal(4, definition.WritableProperties.Count);
        var power = Assert.Single(service.Properties, value => value.Name == "on");
        Assert.Equal("电源", power.ChineseName);
        Assert.True(power.Readable);
        Assert.True(power.Writable);
        var mode = Assert.Single(service.Properties, value => value.Name == "mode");
        Assert.Equal("工作模式", mode.ChineseName);
        Assert.Equal(["自动", "静音"], mode.ValueList.Select(value => value.ChineseName).ToArray());
        var brightness = Assert.Single(service.Properties, value => value.Name == "brightness");
        Assert.Equal(new XiaomiValueRange(1, 100, 1), brightness.ValueRange);
        Assert.Equal("%", brightness.Unit);
        var temperature = Assert.Single(service.Properties, value => value.Name == "temperature");
        Assert.True(temperature.Readable);
        Assert.False(temperature.Writable);
        Assert.Equal("℃", temperature.Unit);
        var start = Assert.Single(service.Actions, value => value.Name == "start");
        Assert.Equal("开始", start.ChineseName);
        Assert.Empty(start.InputArguments);
        var execute = Assert.Single(service.Actions, value => value.Name == "execute");
        Assert.Equal(["工作模式", "亮度"], execute.InputArguments.Select(value => value.ChineseName).ToArray());
        var alarm = Assert.Single(service.Events);
        Assert.Equal("报警事件", alarm.ChineseName);
        Assert.Equal("温度", Assert.Single(alarm.Arguments).ChineseName);
    }

    [Fact]
    public async Task DetailViewModelBuildsEditorsFromDefinitionAndDisablesThemWhenOffline()
    {
        var definition = DetailedDefinition();
        var source = new FakeDeviceControlSource(definition);
        using var online = new XiaomiAccountDeviceDetailViewModel(
            Device(true) with { Definition = definition, SpecType = definition.SpecType },
            source,
            new MiotChineseLocalizationService());

        await online.LoadAsync();

        var power = Assert.Single(online.WritableProperties, value => value.Definition.Name == "on");
        var mode = Assert.Single(online.WritableProperties, value => value.Definition.Name == "mode");
        var brightness = Assert.Single(online.WritableProperties, value => value.Definition.Name == "brightness");
        var text = Assert.Single(online.WritableProperties, value => value.Definition.Name == "service-name");
        var temperature = Assert.Single(online.ReadableProperties, value => value.Definition.Name == "temperature");
        var start = Assert.Single(online.Actions, value => value.Definition.Name == "start");
        var execute = Assert.Single(online.Actions, value => value.Definition.Name == "execute");

        Assert.Equal("电源", power.DisplayName);
        Assert.True(power.IsBoolean);
        Assert.True(power.CanOperate);
        Assert.True(mode.IsEnum);
        Assert.Equal("自动", Assert.Single(mode.ValueOptions, value => Convert.ToInt64(value.Value) == 0).DisplayName);
        Assert.True(brightness.IsNumber);
        Assert.Equal(1d, brightness.MinimumDouble);
        Assert.Equal(100d, brightness.MaximumDouble);
        Assert.True(text.IsString);
        Assert.False(temperature.IsWritable);
        Assert.DoesNotContain(online.WritableProperties, value => value.Definition.Name == "temperature");
        Assert.Equal("开始", start.DisplayName);
        Assert.Empty(start.InputArguments);
        Assert.True(start.CanOperate);
        Assert.Equal(2, execute.InputArguments.Count);
        Assert.True(execute.InputArguments[0].IsEnum);
        Assert.True(execute.InputArguments[1].IsNumber);
        Assert.Single(online.Events);

        using var offline = new XiaomiAccountDeviceDetailViewModel(
            Device(false) with { Definition = definition, SpecType = definition.SpecType },
            source,
            new MiotChineseLocalizationService());
        await offline.LoadAsync();

        Assert.Equal(4, offline.WritableProperties.Count);
        Assert.Equal(2, offline.Actions.Count);
        Assert.All(offline.WritableProperties, value => Assert.False(value.CanOperate));
        Assert.All(offline.Actions, value => Assert.False(value.CanOperate));
    }

    [Fact]
    public async Task DetailPropertyWriteIsSingleFlightAndUsesReadbackBeforeUpdatingUi()
    {
        var definition = DetailedDefinition();
        var source = new FakeDeviceControlSource(definition) { BlockSet = true };
        using var viewModel = new XiaomiAccountDeviceDetailViewModel(
            Device(true) with { Definition = definition, SpecType = definition.SpecType },
            source,
            new MiotChineseLocalizationService());
        await viewModel.LoadAsync();
        var power = Assert.Single(viewModel.WritableProperties, value => value.Definition.Name == "on");

        var first = viewModel.SetPropertyAsync(power, false);
        await source.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = viewModel.SetPropertyAsync(power, false);
        source.SetRelease.TrySetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, source.SetCount);
        Assert.False(Assert.IsType<bool>(power.CurrentValue));
        Assert.Equal("已更新", power.LastOperationText);
    }

    [Fact]
    public void RouterCardKeepsDeviceDetailAndPresenceEntryAsSeparateCommands()
    {
        var router = Device(true) with
        {
            Model = "xiaomi.router.rd03",
            DeviceType = XiaomiAccountDeviceType.Router,
            Capabilities = new XiaomiDeviceCapabilities(isRouter: true)
        };
        XiaomiAccountDevice? openedDetail = null;
        XiaomiAccountDevice? openedPresence = null;
        var card = new XiaomiAccountDeviceCardViewModel(
            router,
            new FakeAccountDeviceSource(),
            device => openedDetail = device,
            device => openedPresence = device);

        Assert.True(card.OpenCommand.CanExecute(null));
        Assert.True(card.RouterPresenceCommand.CanExecute(null));
        card.OpenCommand.Execute(null);
        card.RouterPresenceCommand.Execute(null);

        Assert.Same(router, openedDetail);
        Assert.Same(router, openedPresence);
        Assert.False(card.CanPowerOnOff);
    }

    [Fact]
    public async Task MainViewModelRoutesOnlineOfflineAndNoSpecAccountCardsToDetailAndKeepsRouterPresence()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Xiaomi-Account-Navigation-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var wearableDefinition = WearableDefinitionWithFourActions();
            var routerDevice = Device(true) with
            {
                Did = "did-router",
                Model = "xiaomi.router.rd03",
                Name = "AX3000T",
                DeviceType = XiaomiAccountDeviceType.Router,
                Capabilities = new XiaomiDeviceCapabilities(isRouter: true)
            };
            var offlinePlug = Device(false) with
            {
                Did = "did-plug",
                Model = "chuangmi.plug.v3",
                Name = "米家智能插座3",
                DeviceType = XiaomiAccountDeviceType.Plug,
                Capabilities = new XiaomiDeviceCapabilities(
                    isPlug: true,
                    powerProperties: [new XiaomiPowerCapability(3, 1, true, true)]),
                SpecType = DetailedDefinition().SpecType,
                Definition = DetailedDefinition()
            };
            var wearable = Device(true) with
            {
                Did = "did-band",
                Model = "xiaomi.wearable.band10",
                Name = "小米手环10 陶瓷版",
                DeviceType = XiaomiAccountDeviceType.Other,
                SpecType = wearableDefinition.SpecType,
                Definition = wearableDefinition,
                Capabilities = new XiaomiDeviceCapabilities()
            };
            var phoneWithoutSpec = Device(true) with
            {
                Did = "did-phone",
                Model = "mphone.phone.online",
                Name = "Redmi K50",
                DeviceType = XiaomiAccountDeviceType.Other,
                SpecType = null,
                Definition = null,
                Capabilities = new XiaomiDeviceCapabilities()
            };
            var source = new NavigationSource([routerDevice, offlinePlug, wearable, phoneWithoutSpec]);
            var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
            using var viewModel = new MainViewModel(
                repository,
                new SubjectPresenceService(repository, new PresenceStatisticsService(repository)),
                source,
                monitor,
                new JsonSettingsStore(paths),
                accountDeviceSource: source);
            var openedDevices = new List<XiaomiAccountDevice>();
            Router? openedRouter = null;
            viewModel.OpenXiaomiAccountDeviceRequested += (_, device) => openedDevices.Add(device);
            viewModel.OpenRouterPresenceRequested += (_, router) => openedRouter = router;

            Assert.Same(source, viewModel.DeviceControlSource);
            await viewModel.RefreshAccountDevicesAsync(CancellationToken.None);
            viewModel.Routers.Add(new Router(1, routerDevice.Did, routerDevice.Model!, "partner-router", routerDevice.Name, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            foreach (var expected in new[] { offlinePlug, wearable, phoneWithoutSpec })
            {
                var card = Assert.Single(viewModel.AccountDevices, value => value.Device.Did == expected.Did);
                Assert.True(card.OpenCommand.CanExecute(null));
                card.OpenCommand.Execute(null);
            }

            var offlineCard = Assert.Single(viewModel.AccountDevices, value => value.Device.Did == offlinePlug.Did);
            Assert.False(offlineCard.PowerCommand.CanExecute(null));
            Assert.Equal(new[] { offlinePlug.Did, wearable.Did, phoneWithoutSpec.Did }, openedDevices.Select(value => value.Did));

            var routerCard = Assert.Single(viewModel.AccountDevices, value => value.Device.Did == routerDevice.Did);
            Assert.True(routerCard.RouterPresenceCommand.CanExecute(null));
            routerCard.RouterPresenceCommand.Execute(null);
            Assert.Equal(routerDevice.Did, openedRouter?.MiotDid);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModelUsesSinglePageNavigationForPresenceAndAccountDeviceDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Xiaomi-Single-Window-Navigation-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var source = new NavigationSource([]);
            var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
            using var viewModel = new MainViewModel(
                repository,
                new SubjectPresenceService(repository, new PresenceStatisticsService(repository)),
                source,
                monitor,
                new JsonSettingsStore(paths),
                accountDeviceSource: source);
            var router = new Router(1, "router-did", "xiaomi.router.rd03", "router-partner", "AX3000T", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var first = new XiaomiAccountDeviceDetailViewModel(Device(true) with { Did = "did-plug", Name = "米家智能插座3" }, source, new MiotChineseLocalizationService());
            var second = new XiaomiAccountDeviceDetailViewModel(Device(true) with { Did = "did-band", Name = "小米手环10" }, source, new MiotChineseLocalizationService());

            Assert.Equal(MainPage.XiaomiDeviceList, viewModel.CurrentPage);
            viewModel.ShowRouterPresence(router);
            Assert.Equal(MainPage.RouterPresence, viewModel.CurrentPage);
            Assert.Same(router, viewModel.CurrentPresenceRouter);
            viewModel.ReturnToDevicesCommand.Execute(null);
            Assert.Equal(MainPage.XiaomiDeviceList, viewModel.CurrentPage);

            viewModel.ShowXiaomiAccountDeviceDetail(first);
            Assert.Equal(MainPage.XiaomiAccountDeviceDetail, viewModel.CurrentPage);
            Assert.Same(first, viewModel.CurrentXiaomiAccountDeviceDetail);
            Assert.Equal("米家智能插座3 · 设备详情 · CloudLight XiaoMi", viewModel.MainWindowTitle);
            viewModel.ReturnToDevicesCommand.Execute(null);
            Assert.Equal(MainPage.XiaomiDeviceList, viewModel.CurrentPage);

            viewModel.ShowXiaomiAccountDeviceDetail(second);
            Assert.Equal(MainPage.XiaomiAccountDeviceDetail, viewModel.CurrentPage);
            Assert.Same(second, viewModel.CurrentXiaomiAccountDeviceDetail);
            Assert.Equal("小米手环10 · 设备详情 · CloudLight XiaoMi", viewModel.MainWindowTitle);
            Assert.True(typeof(System.Windows.Controls.UserControl).IsAssignableFrom(typeof(CloudLight.Presence.App.Views.XiaomiAccountDeviceDetailView)));
            Assert.True(typeof(System.Windows.Controls.UserControl).IsAssignableFrom(typeof(CloudLight.Presence.App.Views.RouterPresenceView)));
            Assert.False(typeof(System.Windows.Window).IsAssignableFrom(typeof(CloudLight.Presence.App.Views.XiaomiAccountDeviceDetailView)));
            Assert.False(typeof(System.Windows.Window).IsAssignableFrom(typeof(CloudLight.Presence.App.Views.RouterPresenceView)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DetailViewModelShowsBasicInformationWhenSpecReturns404()
    {
        var phone = Device(true) with
        {
            Did = "did-redmi-k50",
            Model = "mphone.phone.online",
            Name = "Redmi K50",
            DeviceType = XiaomiAccountDeviceType.Other,
            SpecType = null,
            Definition = null,
            Capabilities = new XiaomiDeviceCapabilities()
        };
        using var viewModel = new XiaomiAccountDeviceDetailViewModel(
            phone,
            new FakeDeviceControlSource(null) { ThrowOnGetDefinition = true },
            new MiotChineseLocalizationService());

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasLoaded);
        Assert.Equal("Redmi K50", viewModel.Name);
        Assert.Equal("mphone.phone.online", viewModel.ModelText);
        Assert.False(viewModel.HasAnyCapabilities);
        Assert.Contains("暂未发现可用控制", viewModel.NoControlsText);
        Assert.Contains("404", viewModel.DiagnosticMessage);
    }

    [Fact]
    public async Task DetailViewModelDisplaysAllDiscoveredWearableActionsWithoutInvokingThem()
    {
        var definition = WearableDefinitionWithFourActions();
        var source = new FakeDeviceControlSource(definition);
        using var viewModel = new XiaomiAccountDeviceDetailViewModel(
            Device(true) with
            {
                Did = "did-band-actions",
                Model = "xiaomi.wearable.band10",
                Name = "小米手环10 陶瓷版",
                DeviceType = XiaomiAccountDeviceType.Other,
                SpecType = definition.SpecType,
                Definition = definition,
                Capabilities = new XiaomiDeviceCapabilities()
            },
            source,
            new MiotChineseLocalizationService());

        await viewModel.LoadAsync();

        Assert.Equal(4, viewModel.Actions.Count);
        Assert.All(viewModel.Actions, action => Assert.True(action.CanOperate));
        Assert.Equal(0, source.ActionInvocationCount);
    }

    [Fact]
    public void RouterMetadataIsGenericAndUnknownModelStillHasDisplayName()
    {
        var routerCapabilities = XiaomiDeviceCapabilityResolver.FromMetadata("xiaomi.router.rd03", null);
        var routerType = XiaomiDeviceCapabilityResolver.ClassifyDeviceType("xiaomi.router.rd03", null, routerCapabilities);
        var device = new XiaomiAccountDevice(
            "did-unknown",
            "vendor.unknown.device",
            "未命名设备",
            null,
            XiaomiAccountDeviceType.Unknown,
            true,
            null,
            "home-1",
            "room-1",
            "我的家",
            "客厅",
            null,
            null,
            null,
            false,
            new XiaomiDeviceCapabilities());

        Assert.True(routerCapabilities.IsRouter);
        Assert.Equal(XiaomiAccountDeviceType.Router, routerType);
        Assert.Equal("未命名设备", device.DisplayName);
        Assert.Contains("vendor.unknown.device", device.SearchText);
    }

    [Fact]
    public void AccountDeviceMapperKeepsOwnedSharedAndUnknownDevices()
    {
        using var document = JsonDocument.Parse("""
        [
          { "did": "router-did", "model": "xiaomi.router.rd03", "name": "AX3000T", "isOnline": true },
          { "did": "switch-did", "model": "vendor.switch.v1", "name": "客厅开关", "online": 1 },
          { "did": "unknown-did", "model": "vendor.new.device", "name": "未知设备", "online": 0 }
        ]
        """);

        var devices = document.RootElement.EnumerateArray()
            .Select((value, index) => XiaomiAccountDeviceMapper.Map(
                value,
                "home-1",
                "我的家",
                index == 1 ? "room-1" : null,
                index == 1 ? "客厅" : null,
                index == 2))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();

        Assert.Equal(3, devices.Length);
        Assert.Equal(XiaomiAccountDeviceType.Router, devices[0].DeviceType);
        Assert.Equal("我的家", devices[1].HomeName);
        Assert.Equal("客厅", devices[1].RoomName);
        Assert.Contains("我的家", devices[1].SearchText);
        Assert.Contains("客厅", devices[1].SearchText);
        Assert.True(devices[2].IsShared);
        Assert.Equal("未知设备", devices[2].DisplayName);
        Assert.Contains("vendor.new.device", devices[2].SearchText);
        Assert.False(devices[2].Online);
    }

    [Fact]
    public async Task PowerControlReadsBackAndDoesNotSendConcurrentCommands()
    {
        var source = new FakeAccountDeviceSource();
        var device = Device(true);
        var card = new XiaomiAccountDeviceCardViewModel(device, source);

        await card.RefreshPowerStateAsync(CancellationToken.None);
        source.BlockSet = true;
        var first = card.TogglePowerAsync();
        await source.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = card.TogglePowerAsync();
        Assert.False(card.PowerCommand.CanExecute(null));
        source.SetRelease.TrySetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, source.SetCount);
        Assert.False(card.PowerState);
        Assert.Equal("已关闭", card.StatusText);
    }

    [Fact]
    public async Task PowerControlFailureRestoresPreviousStateAndOfflineCannotToggle()
    {
        var source = new FakeAccountDeviceSource { SetResult = new(false, null, 500, "failed") };
        var card = new XiaomiAccountDeviceCardViewModel(Device(true), source);
        await card.RefreshPowerStateAsync(CancellationToken.None);
        await card.TogglePowerAsync();

        Assert.True(card.PowerState);
        Assert.Equal("关闭失败", card.StatusText);

        card.UpdateDevice(Device(false));
        Assert.False(card.PowerCommand.CanExecute(null));
        Assert.Equal("设备离线", card.StatusText);
    }

    private static XiaomiAccountDevice Device(bool online) => new(
        "did-switch",
        "vendor.switch.device",
        "客厅开关",
        null,
        XiaomiAccountDeviceType.Switch,
        online,
        null,
        "home-1",
        "room-1",
        "我的家",
        "客厅",
        null,
        null,
        null,
        false,
        new XiaomiDeviceCapabilities(
            isSwitch: true,
            powerProperties: [new XiaomiPowerCapability(3, 2, true, true)]));

    private static XiaomiDeviceDefinition DetailedDefinition()
    {
        var properties = new XiaomiPropertyDefinition[]
        {
            new(3, 1, "urn:miot-spec-v2:property:on:00000006:1", "on", "电源", true, true, true, XiaomiMiotValueType.Boolean, null, [], null),
            new(3, 2, "urn:miot-spec-v2:property:mode:00000008:1", "mode", "工作模式", true, true, false, XiaomiMiotValueType.Integer, null,
                [new(0L, "0", "auto", "自动", "auto"), new(1L, "1", "silent", "静音", "silent")], null),
            new(3, 3, "urn:miot-spec-v2:property:brightness:0000000D:1", "brightness", "亮度", true, true, false, XiaomiMiotValueType.Integer, new XiaomiValueRange(1, 100, 1), [], "%"),
            new(3, 4, "urn:miot-spec-v2:property:service-name:00000000:1", "service-name", "设备文本设置", false, true, false, XiaomiMiotValueType.String, null, [], null),
            new(3, 5, "urn:miot-spec-v2:property:temperature:00000020:1", "temperature", "温度", true, false, true, XiaomiMiotValueType.Number, null, [], "℃")
        };
        var executeArguments = properties.Where(value => value.Piid is 2 or 3)
            .Select(value => new XiaomiActionArgument(value.Piid, value.Name, value.ChineseName, value.ValueType, value.ValueRange, value.ValueList, value.Unit))
            .ToArray();
        return new XiaomiDeviceDefinition(
            "urn:miot-spec-v2:device:outlet:0000A002:1",
            [new XiaomiServiceDefinition(
                3,
                "urn:miot-spec-v2:service:outlet:0000780C:1",
                "outlet",
                "插座",
                properties,
                [new XiaomiActionDefinition(3, 1, "urn:miot-spec-v2:action:start:00002802:1", "start", "开始", [], []),
                 new XiaomiActionDefinition(3, 2, "urn:miot-spec-v2:action:execute:00002804:1", "execute", "执行", executeArguments, [])],
                [new XiaomiEventDefinition(3, 1, "urn:miot-spec-v2:event:alarm:00005001:1", "alarm", "报警事件", [])])]);
    }

    private static XiaomiDeviceDefinition WearableDefinitionWithFourActions()
    {
        var baseDefinition = DetailedDefinition();
        var service = Assert.Single(baseDefinition.Services);
        var actions = Enumerable.Range(1, 4)
            .Select(index => new XiaomiActionDefinition(
                3,
                index,
                $"urn:miot-spec-v2:action:wearable-action-{index}:00002804:1",
                $"wearable-action-{index}",
                $"手环操作 {index}",
                [],
                []))
            .ToArray();
        return baseDefinition with { Services = [service with { Actions = actions }] };
    }

    private sealed class FakeAccountDeviceSource : IXiaomiAccountDeviceSource
    {
        private bool _state = true;
        public int SetCount { get; private set; }
        public bool BlockSet { get; set; }
        public TaskCompletionSource<bool> SetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SetRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public XiaomiPowerStateResult SetResult { get; init; } = new(true, null, 0);
        public Task<IReadOnlyList<XiaomiAccountDevice>> DiscoverAccountDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiAccountDevice>>([]);
        public Task<XiaomiPowerStateResult> ReadPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPowerStateResult(true, _state, 0));
        public async Task<XiaomiPowerStateResult> SetPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, bool value, CancellationToken cancellationToken)
        {
            SetCount++;
            if (BlockSet)
            {
                SetStarted.TrySetResult(true);
                await SetRelease.Task;
            }
            if (SetResult.Success) _state = value;
            return SetResult;
        }
    }

    private sealed class NavigationSource(IReadOnlyList<XiaomiAccountDevice> devices) : IXiaomiPresenceSource, IXiaomiAccountDeviceSource, IXiaomiDeviceControlSource
    {
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
        public Task<IReadOnlyList<XiaomiAccountDevice>> DiscoverAccountDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(devices);
        public Task<XiaomiPowerStateResult> ReadPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPowerStateResult(false, null));
        public Task<XiaomiPowerStateResult> SetPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, bool value, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPowerStateResult(false, null));
        public Task<XiaomiDeviceDefinition?> GetDeviceDefinitionAsync(XiaomiAccountDevice device, CancellationToken cancellationToken) => Task.FromResult(device.Definition);
        public Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(XiaomiAccountDevice device, IReadOnlyList<XiaomiPropertyDefinition> properties, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiPropertyReadResult>>([]);
        public Task<XiaomiPropertyOperationResult> SetPropertyAsync(XiaomiAccountDevice device, XiaomiPropertyDefinition property, object? value, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPropertyOperationResult(false));
        public Task<XiaomiActionInvocationResult> InvokeActionAsync(XiaomiAccountDevice device, XiaomiActionDefinition action, IReadOnlyList<object?> inputArguments, CancellationToken cancellationToken) => Task.FromResult(new XiaomiActionInvocationResult(false, []));
    }

    private sealed class FakeDeviceControlSource : IXiaomiDeviceControlSource
    {
        private readonly XiaomiDeviceDefinition? _definition;
        private readonly Dictionary<(int Siid, int Piid), object?> _values = new()
        {
            [(3, 1)] = true,
            [(3, 2)] = 0L,
            [(3, 3)] = 60L,
            [(3, 4)] = "初始文本",
            [(3, 5)] = 31m
        };

        public FakeDeviceControlSource(XiaomiDeviceDefinition? definition) => _definition = definition;
        public bool BlockSet { get; init; }
        public bool ThrowOnGetDefinition { get; init; }
        public int SetCount { get; private set; }
        public int ActionInvocationCount { get; private set; }
        public TaskCompletionSource<bool> SetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SetRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<XiaomiDeviceDefinition?> GetDeviceDefinitionAsync(XiaomiAccountDevice device, CancellationToken cancellationToken)
        {
            if (ThrowOnGetDefinition)
                throw new HttpRequestException("MIoT Spec HTTP 404", null, HttpStatusCode.NotFound);
            return Task.FromResult(_definition);
        }

        public Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(
            XiaomiAccountDevice device,
            IReadOnlyList<XiaomiPropertyDefinition> properties,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<XiaomiPropertyReadResult>>(properties
                .Select(value => new XiaomiPropertyReadResult(value.Siid, value.Piid, true, _values.GetValueOrDefault((value.Siid, value.Piid))))
                .ToArray());

        public async Task<XiaomiPropertyOperationResult> SetPropertyAsync(
            XiaomiAccountDevice device,
            XiaomiPropertyDefinition property,
            object? value,
            CancellationToken cancellationToken)
        {
            SetCount++;
            if (BlockSet)
            {
                SetStarted.TrySetResult(true);
                await SetRelease.Task.WaitAsync(cancellationToken);
            }
            _values[(property.Siid, property.Piid)] = value;
            return new XiaomiPropertyOperationResult(true);
        }

        public Task<XiaomiActionInvocationResult> InvokeActionAsync(
            XiaomiAccountDevice device,
            XiaomiActionDefinition action,
            IReadOnlyList<object?> inputArguments,
            CancellationToken cancellationToken)
        {
            ActionInvocationCount++;
            return Task.FromResult(new XiaomiActionInvocationResult(true, []));
        }
    }
}
