using System.Collections.ObjectModel;
using System.Globalization;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.App.ViewModels;

/// <summary>
/// View model for the account-device detail window.  The window is deliberately
/// driven by the normalized MIoT definition rather than by a device model name.
/// </summary>
public sealed class XiaomiAccountDeviceDetailViewModel : ObservableObject, IDisposable
{
    private readonly IXiaomiDeviceControlSource _source;
    private readonly IMiotLocalizationService _localization;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private XiaomiAccountDevice _device;
    private XiaomiDeviceDefinition? _definition;
    private bool _isLoading;
    private bool _isOperationInProgress;
    private bool _hasLoaded;
    private string _loadingText = "正在加载设备能力…";
    private string _operationStatus = "";
    private string _diagnosticMessage = "";

    public XiaomiAccountDeviceDetailViewModel(
        XiaomiAccountDevice device,
        IXiaomiDeviceControlSource source,
        IMiotLocalizationService localization)
    {
        _device = device;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None),
            () => !IsLoading && !IsOperationInProgress);
    }

    /// <summary>
    /// Called by the view when an action needs a confirmation and/or input
    /// dialog.  The window supplies this callback; keeping it as a callback
    /// leaves the view model usable from tests and from another host.
    /// Returning null cancels the action.
    /// </summary>
    public Func<XiaomiActionViewModel, Task<XiaomiActionRequestResult?>>? ActionRequestHandler { get; set; }

    public XiaomiAccountDevice Device => _device;
    public XiaomiDeviceDefinition? Definition => _definition;
    public ObservableCollection<XiaomiServiceViewModel> Services { get; } = [];
    public ObservableCollection<XiaomiPropertyViewModel> ReadableProperties { get; } = [];
    public ObservableCollection<XiaomiPropertyViewModel> WritableProperties { get; } = [];
    public ObservableCollection<XiaomiActionViewModel> Actions { get; } = [];
    public ObservableCollection<XiaomiEventViewModel> Events { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public string WindowTitle => $"{Name} · 设备详情 · CloudLight XiaoMi";
    public string Name => Device.DisplayName;
    public string ModelText => string.IsNullOrWhiteSpace(Device.Model) ? "型号未知" : Device.Model!;
    public string LocationText => FormatLocation(Device.HomeName, Device.RoomName);
    public string StatusText => Device.Online switch
    {
        true => "在线",
        false => "离线",
        _ => "状态未知"
    };
    public string StatusMark => Device.Online switch
    {
        true => "●",
        false => "○",
        _ => "◇"
    };
    public string StatusColor => Device.Online switch
    {
        true => "#16A34A",
        false => "#64748B",
        _ => "#D97706"
    };
    public bool IsOffline => Device.Online == false;
    public bool IsOnline => Device.Online == true;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool IsOperationInProgress { get => _isOperationInProgress; private set => Set(ref _isOperationInProgress, value); }
    public bool HasLoaded => _hasLoaded;
    public string LoadingText { get => _loadingText; private set => Set(ref _loadingText, value); }
    public string OperationStatus { get => _operationStatus; private set => Set(ref _operationStatus, value); }
    public string DiagnosticMessage { get => _diagnosticMessage; private set => Set(ref _diagnosticMessage, value); }

    public bool HasDefinition => Definition is { Services.Count: > 0 };
    public bool HasReadableProperties => ReadableProperties.Count > 0;
    public bool HasWritableProperties => WritableProperties.Count > 0;
    public bool HasActions => Actions.Count > 0;
    public bool HasEvents => Events.Count > 0;
    public bool HasAnyCapabilities => HasReadableProperties || HasWritableProperties || HasActions || HasEvents;
    public bool HasNoReadableProperties => !HasReadableProperties;
    public bool HasNoWritableProperties => !HasWritableProperties;
    public bool HasNoActions => !HasActions;
    public bool HasNoEvents => !HasEvents;
    public string NoControlsText => HasDefinition
        ? "暂未发现可用控制"
        : "暂未发现可用控制；设备仍可正常显示。";
    public string CapabilitySummary => HasDefinition
        ? $"已发现 {Services.Count} 个服务、{ReadableProperties.Count} 个可读属性、{WritableProperties.Count} 个可写属性、{Actions.Count} 个操作"
        : "暂未加载到设备能力描述";
    public string OfflineHint => IsOffline ? "设备当前离线，已发现的控制仍会保留，但暂时无法操作。" : "";

    /// <summary>
    /// Loads the current official MIoT definition and, when possible, the
    /// current values of all readable properties.
    /// </summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            IsLoading = true;
            LoadingText = "正在读取设备能力…";
            DiagnosticMessage = "";
            Raise(nameof(RefreshCommand));
            RefreshCommand.Refresh();

            XiaomiDeviceDefinition? definition = null;
            try
            {
                definition = await _source.GetDeviceDefinitionAsync(Device, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DiagnosticMessage = $"设备能力暂时无法读取：{exception.Message}";
            }

            // Account discovery may already have attached a normalized
            // definition.  It is a useful fallback if a second spec request
            // is temporarily unavailable.
            definition ??= Device.Definition;
            _definition = definition;
            RebuildCapabilityViewModels(definition);

            if (definition is { } loaded && !IsOffline && loaded.ReadableProperties.Count > 0)
            {
                LoadingText = "正在读取设备状态…";
                await ReadCurrentValuesAsync(loaded.ReadableProperties, cancellationToken);
            }

            _hasLoaded = true;
            Raise(nameof(HasLoaded));
            LoadingText = HasDefinition ? CapabilitySummary : "暂未发现可用设备能力";
            RaiseCapabilityProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A malformed or unavailable spec must never prevent the account
            // device detail window from opening.
            DiagnosticMessage = $"设备能力暂时无法读取：{exception.Message}";
            _definition = null;
            RebuildCapabilityViewModels(null);
            LoadingText = "暂未发现可用设备能力";
            _hasLoaded = true;
            Raise(nameof(HasLoaded));
            RaiseCapabilityProperties();
        }
        finally
        {
            IsLoading = false;
            RefreshAllCommands();
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Allows the account list to pass a refreshed snapshot into an already
    /// open detail window.  It does not infer or fabricate any state.
    /// </summary>
    public void UpdateDevice(XiaomiAccountDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Raise(nameof(Device));
        Raise(nameof(WindowTitle));
        Raise(nameof(Name));
        Raise(nameof(ModelText));
        Raise(nameof(LocationText));
        Raise(nameof(StatusText));
        Raise(nameof(StatusMark));
        Raise(nameof(StatusColor));
        Raise(nameof(IsOffline));
        Raise(nameof(IsOnline));
        Raise(nameof(OfflineHint));
        foreach (var property in WritableProperties) property.UpdateAvailability(IsOffline, IsOperationInProgress);
        foreach (var action in Actions) action.UpdateAvailability(IsOffline, IsOperationInProgress);
        RefreshAllCommands();
    }

    public async Task SetPropertyAsync(
        XiaomiPropertyViewModel property,
        object? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!WritableProperties.Contains(property) || IsOffline)
        {
            property.SetOperationResult(false, "设备当前离线，暂时无法操作");
            return;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken)) return;
        var previous = property.CurrentValue;
        try
        {
            IsOperationInProgress = true;
            OperationStatus = $"正在设置“{property.DisplayName}”…";
            property.SetBusy(true, "正在设置…");
            RefreshAllCommands();

            XiaomiPropertyOperationResult setResult;
            try
            {
                setResult = await _source.SetPropertyAsync(Device, property.Definition, value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                property.RestoreDraft();
                property.SetOperationResult(false, "操作失败，请稍后重试");
                OperationStatus = "操作失败，请稍后重试";
                return;
            }

            if (!setResult.Success)
            {
                property.CurrentValue = previous;
                property.RestoreDraft();
                property.SetOperationResult(false, "操作失败，请稍后重试");
                OperationStatus = "操作失败，请稍后重试";
                return;
            }

            IReadOnlyList<XiaomiPropertyReadResult> readback;
            try
            {
                readback = await _source.GetPropertiesAsync(Device, [property.Definition], cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                property.RestoreDraft();
                property.SetOperationResult(false, "无法读取设备确认状态，已保留原值");
                OperationStatus = "无法读取设备确认状态，已保留原值";
                return;
            }

            var confirmed = readback.FirstOrDefault(result => result.Siid == property.Siid && result.Piid == property.Piid);
            if (confirmed is null || !confirmed.Success || !AreValuesEquivalent(confirmed.Value, value))
            {
                property.RestoreDraft();
                property.SetOperationResult(false, "设备未确认本次设置，已保留原值");
                OperationStatus = "设备未确认本次设置，已保留原值";
                return;
            }

            property.CurrentValue = confirmed.Value;
            property.SetOperationResult(true, "已更新");
            OperationStatus = $"“{property.DisplayName}”已更新";
        }
        finally
        {
            property.SetBusy(false, property.LastOperationText);
            IsOperationInProgress = false;
            RefreshAllCommands();
            _operationGate.Release();
        }
    }

    public async Task ExecuteActionAsync(
        XiaomiActionViewModel action,
        IReadOnlyList<object?> inputArguments,
        bool userConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Actions.Contains(action) || IsOffline)
        {
            action.SetOperationResult(false, "设备当前离线，暂时无法操作");
            return;
        }

        if (action.RequiresConfirmation && !userConfirmed)
        {
            action.SetOperationResult(false, "此操作需要确认后才会执行");
            return;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            IsOperationInProgress = true;
            OperationStatus = $"正在执行“{action.DisplayName}”…";
            action.SetBusy(true, "正在执行…");
            RefreshAllCommands();
            XiaomiActionInvocationResult result;
            try
            {
                result = await _source.InvokeActionAsync(Device, action.Definition, inputArguments, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                action.SetOperationResult(false, "操作失败，请稍后重试");
                OperationStatus = "操作失败，请稍后重试";
                return;
            }

            if (!result.Success)
            {
                action.SetOperationResult(false, "操作失败，请稍后重试");
                OperationStatus = "操作失败，请稍后重试";
                return;
            }

            action.SetOperationResult(true, "操作已完成");
            OperationStatus = $"“{action.DisplayName}”已完成";
            // Actions can change a readable property without returning the
            // property value.  Refresh those values, but do not fail the
            // successful action merely because a follow-up read is unavailable.
            if (Definition is { } definition && !IsOffline && definition.ReadableProperties.Count > 0)
                await ReadCurrentValuesAsync(definition.ReadableProperties, cancellationToken);
        }
        finally
        {
            action.SetBusy(false, action.LastOperationText);
            IsOperationInProgress = false;
            RefreshAllCommands();
            _operationGate.Release();
        }
    }

    internal async Task RequestActionAsync(XiaomiActionViewModel action)
    {
        if (ActionRequestHandler is null)
        {
            if (action.HasInputArguments || action.RequiresConfirmation)
            {
                action.SetOperationResult(false, "无法打开操作确认窗口");
                return;
            }

            await ExecuteActionAsync(action, [], true);
            return;
        }

        XiaomiActionRequestResult? request;
        try
        {
            request = await ActionRequestHandler(action);
        }
        catch
        {
            action.SetOperationResult(false, "无法打开操作窗口");
            return;
        }

        if (request is null) return;
        await ExecuteActionAsync(action, request.Arguments, request.UserConfirmed);
    }

    public void Dispose()
    {
        _operationGate.Dispose();
        _refreshGate.Dispose();
    }

    private void RebuildCapabilityViewModels(XiaomiDeviceDefinition? definition)
    {
        Services.Clear();
        ReadableProperties.Clear();
        WritableProperties.Clear();
        Actions.Clear();
        Events.Clear();

        if (definition is null) return;

        foreach (var service in definition.Services)
        {
            var serviceViewModel = new XiaomiServiceViewModel(service, _localization, this);
            Services.Add(serviceViewModel);
            foreach (var property in serviceViewModel.AllProperties)
            {
                if (property.IsReadable) ReadableProperties.Add(property);
                if (property.IsWritable) WritableProperties.Add(property);
            }
            foreach (var action in serviceViewModel.Actions) Actions.Add(action);
            foreach (var @event in serviceViewModel.Events) Events.Add(@event);
        }

        RaiseCapabilityProperties();
    }

    private async Task ReadCurrentValuesAsync(
        IReadOnlyList<XiaomiPropertyDefinition> properties,
        CancellationToken cancellationToken)
    {
        if (properties.Count == 0) return;
        IReadOnlyList<XiaomiPropertyReadResult> values;
        try
        {
            values = await _source.GetPropertiesAsync(Device, properties, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticMessage = $"设备状态暂时无法读取：{exception.Message}";
            return;
        }

        foreach (var result in values.Where(value => value.Success))
        {
            var property = ReadableProperties.FirstOrDefault(value => value.Siid == result.Siid && value.Piid == result.Piid);
            property?.UpdateCurrentValue(result.Value);
        }
    }

    private bool CanOperate => !IsLoading && !IsOffline && !IsOperationInProgress;

    private void RefreshAllCommands()
    {
        RefreshCommand.Refresh();
        foreach (var property in WritableProperties)
        {
            property.UpdateAvailability(IsOffline, IsOperationInProgress);
            property.RefreshCommands();
        }
        foreach (var action in Actions)
        {
            action.UpdateAvailability(IsOffline, IsOperationInProgress);
            action.RefreshCommand();
        }
    }

    private void RaiseCapabilityProperties()
    {
        Raise(nameof(HasDefinition));
        Raise(nameof(HasReadableProperties));
        Raise(nameof(HasWritableProperties));
        Raise(nameof(HasActions));
        Raise(nameof(HasEvents));
        Raise(nameof(HasAnyCapabilities));
        Raise(nameof(HasNoReadableProperties));
        Raise(nameof(HasNoWritableProperties));
        Raise(nameof(HasNoActions));
        Raise(nameof(HasNoEvents));
        Raise(nameof(NoControlsText));
        Raise(nameof(CapabilitySummary));
        RefreshAllCommands();
    }

    private static bool AreValuesEquivalent(object? expected, object? actual)
    {
        if (expected is null || actual is null) return expected is null && actual is null;
        if (expected is bool expectedBool && TryConvertBoolean(actual, out var actualBool)) return expectedBool == actualBool;
        if (TryConvertDecimal(expected, out var expectedNumber) && TryConvertDecimal(actual, out var actualNumber))
            return expectedNumber == actualNumber;
        return string.Equals(Convert.ToString(expected, CultureInfo.InvariantCulture), Convert.ToString(actual, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    internal static bool TryConvertBoolean(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolean:
                result = boolean;
                return true;
            case string text when bool.TryParse(text, out var parsed):
                result = parsed;
                return true;
            case int integer when integer is 0 or 1:
                result = integer == 1;
                return true;
            case long longInteger when longInteger is 0 or 1:
                result = longInteger == 1;
                return true;
            case decimal decimalValue when decimalValue is 0 or 1:
                result = decimalValue == 1;
                return true;
            default:
                result = false;
                return false;
        }
    }

    internal static bool TryConvertDecimal(object? value, out decimal result)
    {
        if (value is null)
        {
            result = 0;
            return false;
        }
        try
        {
            result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception) when (value is IConvertible)
        {
            result = 0;
            return false;
        }
    }

    private static string FormatLocation(string? home, string? room)
    {
        if (!string.IsNullOrWhiteSpace(home) && !string.IsNullOrWhiteSpace(room)) return $"{home} · {room}";
        return string.IsNullOrWhiteSpace(home) ? string.IsNullOrWhiteSpace(room) ? "位置未知" : room! : home!;
    }
}

public sealed record XiaomiActionRequestResult(
    IReadOnlyList<object?> Arguments,
    bool UserConfirmed);

public sealed class XiaomiServiceViewModel : ObservableObject
{
    public XiaomiServiceViewModel(
        XiaomiServiceDefinition definition,
        IMiotLocalizationService localization,
        XiaomiAccountDeviceDetailViewModel owner)
    {
        Definition = definition;
        DisplayName = ResolveDisplayName(definition.ChineseName, definition.Name, definition.OfficialDescription, localization.ServiceName);
        TechnicalType = definition.Type;
        Siid = definition.Siid;
        AllProperties = definition.Properties
            .Select(value => new XiaomiPropertyViewModel(value, localization, owner))
            .ToArray();
        ReadableProperties = new ObservableCollection<XiaomiPropertyViewModel>(AllProperties.Where(value => value.IsReadable));
        WritableProperties = new ObservableCollection<XiaomiPropertyViewModel>(AllProperties.Where(value => value.IsWritable));
        Actions = new ObservableCollection<XiaomiActionViewModel>(definition.Actions.Select(value => new XiaomiActionViewModel(value, DisplayName, localization, owner)));
        Events = new ObservableCollection<XiaomiEventViewModel>(definition.Events.Select(value => new XiaomiEventViewModel(value, DisplayName, localization)));
    }

    public XiaomiServiceDefinition Definition { get; }
    public int Siid { get; }
    public string DisplayName { get; }
    public string TechnicalType { get; }
    public string TechnicalText => $"Service: {TechnicalType} · SIID: {Siid}";
    public IReadOnlyList<XiaomiPropertyViewModel> AllProperties { get; }
    public ObservableCollection<XiaomiPropertyViewModel> ReadableProperties { get; }
    public ObservableCollection<XiaomiPropertyViewModel> WritableProperties { get; }
    public ObservableCollection<XiaomiActionViewModel> Actions { get; }
    public ObservableCollection<XiaomiEventViewModel> Events { get; }
    public bool HasReadableProperties => ReadableProperties.Count > 0;
    public bool HasWritableProperties => WritableProperties.Count > 0;
    public bool HasActions => Actions.Count > 0;
    public bool HasEvents => Events.Count > 0;

    private static string ResolveDisplayName(
        string? chineseName,
        string rawName,
        string? officialDescription,
        Func<string, string?, string> resolver)
    {
        if (ContainsChinese(chineseName)) return chineseName!.Trim();
        return resolver(string.IsNullOrWhiteSpace(rawName) ? chineseName ?? "未知服务" : rawName, officialDescription);
    }

    internal static bool ContainsChinese(string? value) => value?.Any(character => character is >= '\u3400' and <= '\u9FFF') == true;
}

public sealed class XiaomiPropertyViewModel : ObservableObject
{
    private readonly IMiotLocalizationService _localization;
    private readonly XiaomiAccountDeviceDetailViewModel _owner;
    private object? _currentValue;
    private object? _valueBeforeEdit;
    private XiaomiValueOption? _selectedOption;
    private string _draftText = "";
    private double _sliderValue;
    private bool _isBusy;
    private bool _isUnavailable;
    private string _lastOperationText = "";

    internal XiaomiPropertyViewModel(
        XiaomiPropertyDefinition definition,
        IMiotLocalizationService localization,
        XiaomiAccountDeviceDetailViewModel owner)
    {
        Definition = definition;
        _localization = localization;
        _owner = owner;
        _currentValue = definition.CurrentValue;
        _valueBeforeEdit = _currentValue;
        ValueOptions = new ObservableCollection<XiaomiValueOption>(definition.ValueList.Select(value => new XiaomiValueOption(value, localization)));
        _selectedOption = FindOption(_currentValue);
        _draftText = Convert.ToString(_currentValue, CultureInfo.InvariantCulture) ?? "";
        _sliderValue = ToSliderValue(_currentValue) ?? MinimumDouble;
        ToggleCommand = new AsyncRelayCommand(
            ToggleAsync,
            () => CanOperate && BoolValue is not null);
        ApplyCommand = new AsyncRelayCommand(
            ApplyAsync,
            () => CanOperate && IsSupportedEditor && HasDraftValue);
    }

    public XiaomiPropertyDefinition Definition { get; }
    public int Siid => Definition.Siid;
    public int Piid => Definition.Piid;
    public bool IsReadable => Definition.Readable;
    public bool IsWritable => Definition.Writable;
    public string DisplayName => XiaomiServiceViewModel.ContainsChinese(Definition.ChineseName)
        ? Definition.ChineseName
        : _localization.PropertyName(Definition.Name, Definition.OfficialDescription);
    public string TechnicalText => $"Property: {Definition.Type} · SIID: {Siid} · PIID: {Piid}";
    public string? UnitText => _localization.UnitName(Definition.Unit);
    public XiaomiMiotValueType ValueType => Definition.ValueType;
    public bool IsBoolean => Definition.ValueType == XiaomiMiotValueType.Boolean;
    public bool IsEnum => Definition.ValueList.Count > 0;
    // A slider is only safe when the official spec supplies a range.  Do not
    // invent a default 0..100 range for an otherwise unknown numeric type.
    public bool IsNumber => !IsEnum && Definition.IsNumeric && Definition.ValueRange is not null;
    public bool IsString => !IsEnum && Definition.ValueType == XiaomiMiotValueType.String;
    public bool IsUnsupportedEditor => IsWritable && !IsBoolean && !IsEnum && !IsNumber && !IsString;
    public bool IsSupportedEditor => IsBoolean || IsEnum || IsNumber || IsString;
    public bool HasDraftValue => IsBoolean ? BoolValue is not null : IsEnum ? SelectedOption is not null : IsNumber || IsString;
    public ObservableCollection<XiaomiValueOption> ValueOptions { get; }
    public object? CurrentValue { get => _currentValue; internal set => UpdateCurrentValue(value); }
    public bool? BoolValue => XiaomiAccountDeviceDetailViewModel.TryConvertBoolean(_currentValue, out var value) ? value : null;
    public string CurrentValueText => FormatValue(_currentValue);
    public string BooleanControlText => BoolValue == true ? "已开启" : BoolValue == false ? "已关闭" : "状态未知";
    public XiaomiValueOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (!Set(ref _selectedOption, value)) return;
            Raise(nameof(HasDraftValue));
            ApplyCommand.Refresh();
        }
    }
    public string DraftText
    {
        get => _draftText;
        set
        {
            if (!Set(ref _draftText, value)) return;
            Raise(nameof(HasDraftValue));
            ApplyCommand.Refresh();
        }
    }
    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            var bounded = Math.Clamp(value, MinimumDouble, MaximumDouble);
            if (!Set(ref _sliderValue, bounded)) return;
            Raise(nameof(DraftNumberText));
            Raise(nameof(HasDraftValue));
            ApplyCommand.Refresh();
        }
    }
    public double MinimumDouble => (double)(Definition.ValueRange?.Minimum ?? 0m);
    public double MaximumDouble => (double)(Definition.ValueRange?.Maximum ?? 100m);
    public double StepDouble => (double)(Definition.ValueRange?.Step ?? 1m);
    public string DraftNumberText => FormatNumber((decimal)SliderValue);
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsUnavailable { get => _isUnavailable; private set => Set(ref _isUnavailable, value); }
    public string LastOperationText => _lastOperationText;
    public string OperationText => IsBusy ? "正在设置…" : _lastOperationText;
    public bool CanOperate => IsWritable && IsSupportedEditor && !IsUnavailable && !IsBusy && !_owner.IsLoading && !_owner.IsOperationInProgress;
    public string DisabledText => _owner.IsOffline
        ? "设备当前离线，暂时无法操作"
        : IsBusy || _owner.IsOperationInProgress
            ? "正在处理…"
            : IsUnsupportedEditor ? "此类型暂不支持编辑" : "";

    public AsyncRelayCommand ToggleCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }

    internal async Task ToggleAsync()
    {
        if (BoolValue is not { } current) return;
        await _owner.SetPropertyAsync(this, !current);
    }

    internal async Task ApplyAsync()
    {
        object? value = IsEnum
            ? SelectedOption?.Value
            : IsNumber
                ? (decimal)SliderValue
                : IsString
                    ? DraftText
                    : BoolValue;
        if (value is null && !IsBoolean) return;
        await _owner.SetPropertyAsync(this, value);
    }

    internal void UpdateCurrentValue(object? value)
    {
        _currentValue = value;
        _valueBeforeEdit = value;
        _selectedOption = FindOption(value);
        _draftText = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        _sliderValue = ToSliderValue(value) ?? MinimumDouble;
        Raise(nameof(CurrentValue));
        Raise(nameof(CurrentValueText));
        Raise(nameof(BoolValue));
        Raise(nameof(BooleanControlText));
        Raise(nameof(SelectedOption));
        Raise(nameof(DraftText));
        Raise(nameof(SliderValue));
        Raise(nameof(DraftNumberText));
        Raise(nameof(HasDraftValue));
        ToggleCommand.Refresh();
        ApplyCommand.Refresh();
    }

    internal void RestoreDraft()
    {
        _currentValue = _valueBeforeEdit;
        _selectedOption = FindOption(_currentValue);
        _draftText = Convert.ToString(_currentValue, CultureInfo.InvariantCulture) ?? "";
        _sliderValue = ToSliderValue(_currentValue) ?? MinimumDouble;
        Raise(nameof(CurrentValue));
        Raise(nameof(CurrentValueText));
        Raise(nameof(BoolValue));
        Raise(nameof(BooleanControlText));
        Raise(nameof(SelectedOption));
        Raise(nameof(DraftText));
        Raise(nameof(SliderValue));
        Raise(nameof(DraftNumberText));
        Raise(nameof(HasDraftValue));
    }

    internal void SetBusy(bool busy, string text)
    {
        IsBusy = busy;
        _lastOperationText = text;
        Raise(nameof(LastOperationText));
        Raise(nameof(OperationText));
        Raise(nameof(CanOperate));
        Raise(nameof(DisabledText));
        ToggleCommand.Refresh();
        ApplyCommand.Refresh();
    }

    internal void SetOperationResult(bool success, string text)
    {
        _lastOperationText = text;
        Raise(nameof(LastOperationText));
        Raise(nameof(OperationText));
        Raise(nameof(CanOperate));
        if (success) _valueBeforeEdit = _currentValue;
        ToggleCommand.Refresh();
        ApplyCommand.Refresh();
    }

    internal void UpdateAvailability(bool offline, bool operationInProgress)
    {
        IsUnavailable = offline || operationInProgress;
        Raise(nameof(CanOperate));
        Raise(nameof(DisabledText));
        ToggleCommand.Refresh();
        ApplyCommand.Refresh();
    }

    internal void RefreshCommands()
    {
        ToggleCommand.Refresh();
        ApplyCommand.Refresh();
    }

    private XiaomiValueOption? FindOption(object? value)
    {
        if (value is null) return null;
        return ValueOptions.FirstOrDefault(option => XiaomiAccountDeviceDetailViewModel.TryConvertDecimal(option.Value, out var optionNumber) && XiaomiAccountDeviceDetailViewModel.TryConvertDecimal(value, out var valueNumber)
            ? optionNumber == valueNumber
            : AreValuesEquivalent(option.Value, value));
    }

    private static bool AreValuesEquivalent(object? left, object? right) => string.Equals(
        Convert.ToString(left, CultureInfo.InvariantCulture),
        Convert.ToString(right, CultureInfo.InvariantCulture),
        StringComparison.OrdinalIgnoreCase);

    private double? ToSliderValue(object? value) => XiaomiAccountDeviceDetailViewModel.TryConvertDecimal(value, out var number) ? (double)number : null;

    private string FormatValue(object? value)
    {
        if (value is null) return "暂无数据";
        if (IsBoolean && XiaomiAccountDeviceDetailViewModel.TryConvertBoolean(value, out var boolean)) return boolean ? "开启" : "关闭";
        if (SelectedOption is { } option && AreValuesEquivalent(option.Value, value)) return option.DisplayName;
        if (Definition.ValueList.Count > 0)
        {
            var known = ValueOptions.FirstOrDefault(option => AreValuesEquivalent(option.Value, value));
            if (known is not null) return known.DisplayName;
        }
        return Convert.ToString(value, CultureInfo.CurrentCulture) ?? "暂无数据";
    }

    private string FormatNumber(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);
}

public sealed class XiaomiValueOption
{
    internal XiaomiValueOption(XiaomiValueListItem item, IMiotLocalizationService localization)
    {
        Value = item.Value;
        RawValue = item.RawValue;
        DisplayName = XiaomiServiceViewModel.ContainsChinese(item.ChineseName)
            ? item.ChineseName.Trim()
            : localization.ValueName(item.RawValue, item.OfficialDescription);
    }

    public object? Value { get; }
    public string RawValue { get; }
    public string DisplayName { get; }
}

public sealed class XiaomiActionViewModel : ObservableObject
{
    private readonly XiaomiAccountDeviceDetailViewModel _owner;
    private bool _isBusy;
    private bool _isUnavailable;
    private string _lastOperationText = "";

    internal XiaomiActionViewModel(
        XiaomiActionDefinition definition,
        string serviceName,
        IMiotLocalizationService localization,
        XiaomiAccountDeviceDetailViewModel owner)
    {
        Definition = definition;
        _owner = owner;
        ServiceName = serviceName;
        DisplayName = XiaomiServiceViewModel.ContainsChinese(definition.ChineseName)
            ? definition.ChineseName
            : localization.ActionName(definition.Name, definition.OfficialDescription);
        InputArguments = new ObservableCollection<XiaomiActionArgumentViewModel>(definition.InputArguments.Select(value => new XiaomiActionArgumentViewModel(value, localization)));
        OutputArguments = new ObservableCollection<XiaomiActionArgumentViewModel>(definition.OutputArguments.Select(value => new XiaomiActionArgumentViewModel(value, localization)));
        RequiresConfirmation = localization.IsHighRiskAction(definition);
        RiskWarning = BuildRiskWarning(DisplayName);
        ExecuteCommand = new AsyncRelayCommand(
            () => _owner.RequestActionAsync(this),
            () => CanOperate);
    }

    public XiaomiActionDefinition Definition { get; }
    public int Siid => Definition.Siid;
    public int Aiid => Definition.Aiid;
    public string DisplayName { get; }
    public string ServiceName { get; }
    public string TechnicalText => $"Action: {Definition.Type} · SIID: {Siid} · AIID: {Aiid}";
    public ObservableCollection<XiaomiActionArgumentViewModel> InputArguments { get; }
    public ObservableCollection<XiaomiActionArgumentViewModel> OutputArguments { get; }
    public bool HasInputArguments => InputArguments.Count > 0;
    public bool HasOutputArguments => OutputArguments.Count > 0;
    public bool RequiresConfirmation { get; }
    public string RiskWarning { get; }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsUnavailable { get => _isUnavailable; private set => Set(ref _isUnavailable, value); }
    public string LastOperationText => _lastOperationText;
    public string OperationText => IsBusy ? "正在执行…" : _lastOperationText;
    public bool CanOperate => !IsUnavailable && !IsBusy && !_owner.IsLoading && !_owner.IsOperationInProgress;
    public string DisabledText => _owner.IsOffline
        ? "设备当前离线，暂时无法操作"
        : IsBusy || _owner.IsOperationInProgress
            ? "正在处理…"
            : "";
    public AsyncRelayCommand ExecuteCommand { get; }

    internal void SetBusy(bool busy, string text)
    {
        IsBusy = busy;
        _lastOperationText = text;
        Raise(nameof(LastOperationText));
        Raise(nameof(OperationText));
        Raise(nameof(CanOperate));
        Raise(nameof(DisabledText));
        ExecuteCommand.Refresh();
    }

    internal void SetOperationResult(bool success, string text)
    {
        _lastOperationText = text;
        Raise(nameof(LastOperationText));
        Raise(nameof(OperationText));
        ExecuteCommand.Refresh();
    }

    internal void UpdateAvailability(bool offline, bool operationInProgress)
    {
        IsUnavailable = offline || operationInProgress;
        Raise(nameof(CanOperate));
        Raise(nameof(DisabledText));
        ExecuteCommand.Refresh();
    }

    internal void RefreshCommand() => ExecuteCommand.Refresh();

    private static string BuildRiskWarning(string actionName)
    {
        var normalized = actionName.ToLowerInvariant();
        if (normalized.Contains("恢复出厂", StringComparison.Ordinal) || normalized.Contains("重置", StringComparison.Ordinal))
            return "此操作可能清除设备配置，并需要重新添加设备。";
        if (normalized.Contains("删除", StringComparison.Ordinal) || normalized.Contains("清除", StringComparison.Ordinal))
            return "此操作可能删除设备上的数据，通常无法撤销。";
        if (normalized.Contains("解锁", StringComparison.Ordinal))
            return "此操作会改变设备的安全状态，请确认现场安全。";
        if (normalized.Contains("解除绑定", StringComparison.Ordinal))
            return "此操作会解除设备与当前账号的绑定。";
        return "此操作可能改变设备配置或安全状态，请确认确实要继续。";
    }
}

public sealed class XiaomiActionArgumentViewModel : ObservableObject
{
    private readonly IMiotLocalizationService _localization;
    private XiaomiValueOption? _selectedOption;
    private string _textValue = "";
    private bool _boolValue;
    private double _sliderValue;

    internal XiaomiActionArgumentViewModel(XiaomiActionArgument definition, IMiotLocalizationService localization)
    {
        Definition = definition;
        _localization = localization;
        ValueOptions = new ObservableCollection<XiaomiValueOption>(definition.ValueList.Select(value => new XiaomiValueOption(value, localization)));
        _selectedOption = ValueOptions.FirstOrDefault();
        _sliderValue = MinimumDouble;
    }

    public XiaomiActionArgument Definition { get; }
    public int Piid => Definition.Piid;
    public string DisplayName => XiaomiServiceViewModel.ContainsChinese(Definition.ChineseName)
        ? Definition.ChineseName
        : _localization.PropertyName(Definition.Name, null);
    public string? UnitText => _localization.UnitName(Definition.Unit);
    public bool IsRequired => Definition.Required;
    public bool IsBoolean => Definition.ValueType == XiaomiMiotValueType.Boolean;
    public bool IsEnum => Definition.ValueList.Count > 0;
    // A slider needs the range from the official spec.  For a numeric
    // argument without a range the dialog falls back to a validated text
    // input instead of inventing limits.
    public bool IsNumber => !IsEnum && Definition.ValueType is XiaomiMiotValueType.Integer or XiaomiMiotValueType.Number && Definition.ValueRange is not null;
    public bool IsString => !IsEnum && Definition.ValueType == XiaomiMiotValueType.String;
    public bool IsTextFallback => !IsBoolean && !IsEnum && !IsNumber;
    public ObservableCollection<XiaomiValueOption> ValueOptions { get; }
    public bool BoolValue { get => _boolValue; set => Set(ref _boolValue, value); }
    public XiaomiValueOption? SelectedOption
    {
        get => _selectedOption;
        set => Set(ref _selectedOption, value);
    }
    public string TextValue { get => _textValue; set => Set(ref _textValue, value); }
    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            if (!Set(ref _sliderValue, Math.Clamp(value, MinimumDouble, MaximumDouble))) return;
            Raise(nameof(SliderText));
        }
    }
    public double MinimumDouble => (double)(Definition.ValueRange?.Minimum ?? 0m);
    public double MaximumDouble => (double)(Definition.ValueRange?.Maximum ?? 100m);
    public string SliderText => ((decimal)SliderValue).ToString("0.##", CultureInfo.CurrentCulture);
    public string TechnicalText => $"PIID: {Piid}";

    public bool TryGetValue(out object? value, out string? error)
    {
        if (IsEnum)
        {
            value = SelectedOption?.Value;
            error = value is null && IsRequired ? $"请选择“{DisplayName}”。" : null;
            return error is null;
        }
        if (IsBoolean)
        {
            value = BoolValue;
            error = null;
            return true;
        }
        if (IsNumber)
        {
            value = (decimal)SliderValue;
            error = null;
            return true;
        }
        if (Definition.ValueType is XiaomiMiotValueType.Integer or XiaomiMiotValueType.Number)
        {
            if (!decimal.TryParse(TextValue, NumberStyles.Number, CultureInfo.CurrentCulture, out var number))
            {
                value = null;
                error = $"“{DisplayName}”必须是数字。";
                return false;
            }
            value = Definition.ValueType == XiaomiMiotValueType.Integer ? decimal.Truncate(number) : number;
            error = null;
            return true;
        }
        value = TextValue;
        error = IsRequired && string.IsNullOrWhiteSpace(TextValue) ? $"请填写“{DisplayName}”。" : null;
        return error is null;
    }
}

public sealed class XiaomiEventViewModel
{
    internal XiaomiEventViewModel(XiaomiEventDefinition definition, string serviceName, IMiotLocalizationService localization)
    {
        Definition = definition;
        ServiceName = serviceName;
        DisplayName = XiaomiServiceViewModel.ContainsChinese(definition.ChineseName)
            ? definition.ChineseName
            : localization.EventName(definition.Name, definition.OfficialDescription);
    }

    public XiaomiEventDefinition Definition { get; }
    public string DisplayName { get; }
    public string ServiceName { get; }
    public string TechnicalText => $"Event: {Definition.Type} · SIID: {Definition.Siid} · EIID: {Definition.Eiid}";
}
