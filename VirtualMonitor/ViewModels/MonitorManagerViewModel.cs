namespace VirtualMonitor;

internal sealed class MonitorManagerViewModel : ObservableObject, IDisposable
{
    private readonly IAppLog log;
    private readonly IMonitorStateReader state;
    private readonly IConfigurationChanges changes;
    private readonly IMonitorOperations operations;
    private IReadOnlyList<MonitorDisplay> monitors = [];
    private IReadOnlyList<MonitorModes.Mode> modes = [];
    private MonitorDisplay? selected;
    private MonitorModes.Mode? selectedMode;
    private string name = "", status = "", countText = "";
    private int configuredCount;
    private bool busy, disposed, valid;
    private int selectionVersion;

    internal MonitorManagerViewModel(IMonitorStateReader state, IConfigurationChanges changes, IMonitorOperations operations, IAppLog log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.changes = changes ?? throw new ArgumentNullException(nameof(changes));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));

        AddCommand = new(() => ChangeAsync("monitor.add", async () => { await operations.CreateAsync(); }), () => CanEdit && configuredCount < 16);
        RemoveCommand = new(RemoveAsync, () => CanEdit && Selected != null);
        RemoveAllCommand = new(() => ChangeAsync("monitor.remove-all", async () => { await operations.RemoveAllAsync(); }), () => CanEdit && configuredCount > 0);
        ToggleSelectedCommand = new(ToggleAsync, () => CanEdit && Selected != null);
        SaveNameCommand = new(SaveNameAsync, () => CanEdit && Selected != null);
        ApplyModeCommand = new(ApplyModeAsync, () => CanEdit && Selected is { IsActive: true } && SelectedMode != null);
        ExtendCommand = new(() => ChangeAsync("display.extend", () => { operations.Extend(); return Task.CompletedTask; }), () => CanEdit && Monitors.Any(m => m.IsActive));
        RefreshCommand = new(RefreshAsync, () => !IsBusy);
        changes.StateChanged += Changed;
        _ = RefreshAsync();
    }

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public AsyncRelayCommand RemoveAllCommand { get; }
    public AsyncRelayCommand ToggleSelectedCommand { get; }
    public AsyncRelayCommand SaveNameCommand { get; }
    public AsyncRelayCommand ApplyModeCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExtendCommand { get; }
    public IReadOnlyList<MonitorDisplay> Monitors { get => monitors; private set => SetProperty(ref monitors, value); }
    public IReadOnlyList<MonitorModes.Mode> Modes { get => modes; private set => SetProperty(ref modes, value); }
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string CountText { get => countText; private set => SetProperty(ref countText, value); }
    public bool IsBusy => busy || changes.IsBusy;
    public bool CanEdit => !IsBusy && valid;
    public string SelectedStateText => Selected == null ? "모니터를 선택하세요."
        : Selected.IsActive ? $"켜짐 · {Selected.Details}" : "꺼짐 · 이름 변경과 다시 켜기가 가능합니다.";
    public string ToggleSelectedButtonText => Selected is { IsActive: true } ? "선택 모니터 끄기" : "선택 모니터 켜기";
    public MonitorDisplay? Selected
    {
        get => selected;
        set
        {
            SetProperty(ref selected, value);
            Name = value?.Alias ?? "";
            Modes = [];
            SelectedMode = null;
            OnPropertyChanged(nameof(SelectedStateText));
            OnPropertyChanged(nameof(ToggleSelectedButtonText));
            _ = ReadModesAsync(value, ++selectionVersion);
            Changed();
        }
    }
    public MonitorModes.Mode? SelectedMode { get => selectedMode; set { SetProperty(ref selectedMode, value); Changed(); } }

    private async Task ReadModesAsync(MonitorDisplay? display, int version)
    {
        if (display is not { IsActive: true }) return;
        try
        {
            var result = await Task.Run(() => (Modes: operations.GetModes(display.Name), Current: operations.GetCurrentMode(display.Name)));
            if (disposed || selectionVersion != version) return;
            Modes = result.Modes;
            SelectedMode = Modes.FirstOrDefault(m => m == result.Current);
        }
        catch (Exception ex) { if (!disposed && selectionVersion == version) Status = ex.Message; log.Write("modes.read", error: ex); }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        AddCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); RemoveAllCommand.NotifyCanExecuteChanged();
        ToggleSelectedCommand.NotifyCanExecuteChanged(); SaveNameCommand.NotifyCanExecuteChanged(); ApplyModeCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged(); ExtendCommand.NotifyCanExecuteChanged();
    }

    internal async Task RefreshAsync()
    {
        try
        {
            var snapshot = await state.ReadAsync();
            if (disposed) return;
            string? previous = Selected?.StableId;
            Monitors = snapshot.Managed;
            configuredCount = snapshot.Count;
            valid = snapshot.Adapters.Count == 1;
            Selected = Monitors.FirstOrDefault(m => !string.IsNullOrEmpty(previous) && m.StableId == previous) ?? Monitors.FirstOrDefault();
            CountText = $"설정 {configuredCount}개 · 켜짐 {Monitors.Count(m => m.IsActive)}개";
            Status = valid ? "모니터를 선택해 구성할 수 있습니다." : "설치된 MttVDD 어댑터가 없습니다.";
        }
        catch (Exception ex) { valid = false; Status = "목록 조회 실패: " + ex.Message; log.Write("manager.refresh", error: ex); }
        finally { Changed(); }
    }

    private async Task ChangeAsync(string operation, Func<Task> action)
    {
        busy = true; Changed();
        Status = "구성 변경 중…";
        try
        {
            await changes.RunAsync(operation, action);
            await RefreshAsync();
            if (valid) Status = "구성 변경과 Windows 상태 확인을 완료했습니다.";
        }
        catch (Exception ex) { Status = ex is AggregateException a ? string.Join(" / ", a.Flatten().InnerExceptions.Select(e => e.Message)) : ex.Message; }
        finally { busy = false; Changed(); }
    }

    private Task RemoveAsync()
    {
        var target = Selected;
        return ChangeAsync("monitor.remove", async () => { await operations.RemoveAsync(Resolve(target).SlotIndex); });
    }
    private Task ToggleAsync()
    {
        var target = Selected;
        bool enable = target is { IsActive: false };
        return ChangeAsync("monitor.toggle", async () =>
        {
            var current = Resolve(target);
            var device = operations.GetDevices().Single(d => d.StableId == current.StableId);
            await operations.SetEnabledAsync(device.InstanceId, enable);
        });
    }
    private Task SaveNameAsync()
    {
        var target = Selected; string alias = Name;
        return ChangeAsync("monitor.rename", () => { operations.SaveName(Resolve(target).MonitorId, alias); return Task.CompletedTask; });
    }
    private Task ApplyModeAsync()
    {
        var target = Selected; var mode = SelectedMode;
        return ChangeAsync("monitor.mode", () =>
        {
            if (mode == null) throw new InvalidOperationException("화면 모드를 선택하세요.");
            operations.ApplyMode(Resolve(target).MonitorId, mode);
            return Task.CompletedTask;
        });
    }
    private MonitorDisplay Resolve(MonitorDisplay? target) =>
        operations.GetManagedMonitors().SingleOrDefault(m => target != null && m.StableId == target.StableId)
        ?? throw new InvalidOperationException("선택한 모니터가 삭제되었거나 구성이 변경되었습니다.");

    public void Dispose() { disposed = true; changes.StateChanged -= Changed; }
}
