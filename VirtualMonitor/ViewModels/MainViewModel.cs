using System.ComponentModel;
using System.Windows.Threading;

namespace VirtualMonitor;

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAppLog log;
    private readonly IMonitorStateReader state;
    private readonly IConfigurationChanges changes;
    private readonly IMonitorOperations operations;
    private readonly IPreviewOwnershipReader previewOwnership;
    private readonly DispatcherTimer refreshTimer;
    private bool disposed;
    private bool reading;
    private IReadOnlyList<MonitorDevice> devices = Array.Empty<MonitorDevice>();
    private IReadOnlyList<MonitorDisplay> displays = Array.Empty<MonitorDisplay>();
    private MonitorDevice? selectedDevice;
    private MonitorDisplay? selectedDisplay;
    private bool busy;
    private bool refreshingDisplays;
    private string status = "설치된 가상 모니터를 켜고 끌 수 있습니다.";
    private string deviceState = "";

    // 메인 화면 명령과 실행 조건, 2초 간격 갱신 타이머를 구성합니다. 타이머 시작은 Start가 담당합니다.
    public MainViewModel(Dispatcher dispatcher, IMonitorStateReader state, IConfigurationChanges changes, IMonitorOperations operations, IPreviewOwnershipReader previewOwnership, IAppLog log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.changes = changes ?? throw new ArgumentNullException(nameof(changes));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.previewOwnership = previewOwnership ?? throw new ArgumentNullException(nameof(previewOwnership));

        ConnectCommand = new AsyncRelayCommand(() => SetEnabledAsync(true), () => !IsBusy && SelectedDevice is { Started: false });
        DisconnectCommand = new AsyncRelayCommand(() => SetEnabledAsync(false), () => !IsBusy && SelectedDevice != null && SelectedDevice.Problem != 22);
        ExtendCommand = new AsyncRelayCommand(Extend, () => !IsBusy && SelectedDevice?.Started == true);
        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
        PreviewCommand = new RelayCommand(OpenPreview, () => !IsBusy && SelectedDevice is { Started: true } && !IsPreviewOpen(SelectedDevice));
        refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        refreshTimer.Stop();
        changes.StateChanged += OnChangeState;
        previewOwnership.Changed += UpdateCommands;
    }

    public event Action<MonitorDevice?>? PreviewRequested;
    public event Action<string>? DuplicateMonitorOpened;
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand ExtendCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public IReadOnlyList<MonitorDevice> Devices { get => devices; private set => SetProperty(ref devices, value); }
    public IReadOnlyList<MonitorDisplay> Displays { get => displays; private set => SetProperty(ref displays, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string DeviceState { get => deviceState; private set => SetProperty(ref deviceState, value); }
    public bool CanSelectDevice => !IsBusy;
    public bool IsBusy
    {
        get => busy || changes.IsBusy;
        private set
        {
            if (!SetProperty(ref busy, value)) return;
            OnPropertyChanged(nameof(CanSelectDevice));
            UpdateCommands();
        }
    }
    public MonitorDevice? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            if (refreshingDisplays) return;
            if (!SetProperty(ref selectedDevice, value)) return;
            SetProperty(ref selectedDisplay, Displays.FirstOrDefault(d => MatchesDevice(d, value)), nameof(SelectedDisplay));
            UpdateCommands();
        }
    }

    public MonitorDisplay? SelectedDisplay
    {
        get => selectedDisplay;
        set
        {
            if (refreshingDisplays) return;
            if (!SetProperty(ref selectedDisplay, value)) return;
            SetProperty(ref selectedDevice, value == null ? null : Devices.FirstOrDefault(d => MatchesDevice(value, d)), nameof(SelectedDevice));
            UpdateCommands();
        }
    }

    // 화면과 장치가 같은 가상 모니터인지 슬롯·모니터 ID·GDI 이름 순서로 비교합니다.
    private static bool MatchesDevice(MonitorDisplay display, MonitorDevice? device)
    {
        if (!display.IsVirtual || device == null) return false;
        if (!string.IsNullOrEmpty(device.StableId)) return display.StableId == device.StableId;
        if (display.SlotIndex >= 0 && device.SlotIndex >= 0) return display.SlotIndex == device.SlotIndex;
        if (!string.IsNullOrEmpty(display.MonitorId) && !string.IsNullOrEmpty(device.MonitorId))
            return display.MonitorId.Equals(device.MonitorId, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(device.GdiDeviceName)
            && display.Name.Equals(device.GdiDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    // 중복 창 검사 키를 만듭니다. 슬롯을 우선하여 표시 이름이 바뀌어도 같은 화면으로 인식합니다.
    internal static string GetMonitorKey(MonitorDevice? device) =>
        device == null
            ? "__default__"
            : !string.IsNullOrEmpty(device.StableId) ? $"stable:{device.StableId}"
            : device.SlotIndex >= 0
                ? $"slot:{device.SlotIndex}"
                : !string.IsNullOrEmpty(device.MonitorId)
                    ? $"id:{device.MonitorId}"
                    : device.InstanceId;

    // 해당 모니터의 키가 열린 미리보기 집합에 등록되어 있는지 검사합니다.
    public bool IsPreviewOpen(MonitorDevice? device) =>
        previewOwnership.IsOwned(GetMonitorKey(device));

    // 선택된 활성 모니터의 창 열기를 View에 요청합니다. 중복 요청이나 호출 중 예외는 알림으로 전달합니다.
    public void OpenPreview()
    {
        if (IsBusy || SelectedDevice is not { Started: true }) return;
        if (IsPreviewOpen(SelectedDevice))
        {
            NotifyDuplicateMonitorOpened($"이미 '{SelectedDevice.DisplayName}' 화면이 켜져 있습니다.");
            return;
        }
        try
        {
            PreviewRequested?.Invoke(SelectedDevice);
        }
        catch (Exception ex)
        {
            Status = $"미리보기 열기 실패: {ex.Message}";
            log.Write("preview.open", error: ex);
        }
    }

    // 상태 문구를 기록하고 View에 사용자 알림을 요청합니다.
    internal void NotifyDuplicateMonitorOpened(string message)
    {
        Status = message;
        DuplicateMonitorOpened?.Invoke(message);
    }

    // 처음 목록을 읽고 주기적 갱신을 시작합니다.
    public void Start() { Refresh(); refreshTimer.Start(); }
    // 장치 변경 중임을 표시하고 주기 조회를 멈춰 중간 상태의 UI 반영을 줄입니다.
    internal void SuspendRefresh() { IsBusy = true; refreshTimer.Stop(); }
    // 작업 상태를 해제하고 목록을 즉시 읽은 뒤 타이머를 재개합니다.
    internal void ResumeRefresh() { IsBusy = false; Refresh(); refreshTimer.Start(); }
    // 메인 창이 종료될 때 갱신 타이머를 멈춥니다.
    private void OnChangeState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanSelectDevice));
        UpdateCommands();
        if (!changes.IsBusy) Refresh();
    }
    public void Dispose()
    {
        disposed = true;
        refreshTimer.Stop();
        changes.StateChanged -= OnChangeState;
        previewOwnership.Changed -= UpdateCommands;
    }

    // 장치 변경 중에는 종료를 거부하고 상태 문구로 이유를 알립니다.
    public bool CanExit()
    {
        if (!IsBusy) return true;
        Status = "장치 상태 변경이 끝난 뒤 트레이의 종료를 다시 눌러 주세요.";
        return false;
    }

    // 선택과 작업 상태가 바뀌었을 때 모든 메인 버튼의 실행 조건을 재평가하게 합니다.
    private void UpdateCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ExtendCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
    }

    // 장치·화면 목록을 읽고 ID·슬롯으로 이전 선택을 복원합니다. 갱신 플래그로 선택 속성 간 상호 갱신을 잠시 막습니다.
    public void Refresh() => _ = RefreshAsync();
    internal async Task RefreshAsync()
    {
        if (IsBusy || disposed || reading) return;
        reading = true;
        try
        {
            string? stableId = SelectedDevice?.StableId;
            string? id = SelectedDevice?.InstanceId;
            int? slot = SelectedDevice?.SlotIndex;
            var previousDisplay = SelectedDisplay;
            var snapshot = await state.ReadAsync();
            if (disposed || IsBusy) return;
            refreshingDisplays = true;
            var adapters = snapshot.Adapters;
            var updated = snapshot.Devices;
            Devices = updated;
            var nextDevice = updated.FirstOrDefault(d => !string.IsNullOrEmpty(stableId) && d.StableId == stableId)
                ?? updated.FirstOrDefault(d => string.IsNullOrEmpty(stableId) && d.InstanceId == id)
                ?? (slot >= 0 ? updated.FirstOrDefault(d => d.SlotIndex == slot) : null)
                ?? updated.FirstOrDefault();
            Displays = snapshot.Displays;
            var nextDisplay = previousDisplay is { IsVirtual: false }
                ? Displays.FirstOrDefault(d => d.Name == previousDisplay.Name)
                : Displays.FirstOrDefault(d => MatchesDevice(d, nextDevice));
            SetProperty(ref selectedDevice, nextDisplay is { IsVirtual: false } ? null : nextDevice, nameof(SelectedDevice));
            SetProperty(ref selectedDisplay, nextDisplay, nameof(SelectedDisplay));
            DeviceState = adapters.Count == 0
                ? "설치된 MttVDD 장치가 없습니다. 드라이버와 장치를 최초 한 번 설치해야 합니다."
                : updated.Count == 0
                    ? "설정된 가상 모니터가 없습니다. '가상 모니터 관리'에서 모니터를 추가해 주세요."
                    : $"설정된 가상 모니터 {updated.Count}개 (켜짐 {updated.Count(d => d.Started)}개) · 선택한 가상 모니터만 개별적으로 켜고 끕니다.";
        }
        catch (Exception ex)
        {
            Devices = Array.Empty<MonitorDevice>();
            Displays = Array.Empty<MonitorDisplay>();
            SetProperty(ref selectedDevice, null, nameof(SelectedDevice));
            SetProperty(ref selectedDisplay, null, nameof(SelectedDisplay));
            DeviceState = $"장치 조회 실패: {ex.Message}";
        }
        finally { reading = false; refreshingDisplays = false; UpdateCommands(); }
    }

    // 선택 화면의 켜기·끄기를 요청하고 결과를 표시합니다. finally에서 작업 상태를 풀고 목록을 갱신합니다.
    private async Task SetEnabledAsync(bool enabled)
    {
        if (IsBusy || SelectedDevice is not { } device) return;
        if (enabled && device.Started)
        {
            try
            {
                throw new InvalidOperationException($"이미 '{device.DisplayName}' 모니터가 켜져 있습니다. 같은 가상 모니터는 중복해서 켤 수 없습니다.");
            }
            catch (Exception ex)
            {
                NotifyDuplicateMonitorOpened(ex.Message);
                return;
            }
        }
        IsBusy = true;
        Status = enabled ? $"{device.DisplayName} 켜는 중…" : $"{device.DisplayName} 끄는 중…";
        try
        {
            await changes.RunAsync(enabled ? "monitor.enable" : "monitor.disable", () =>
            {
                var current = operations.GetDevices().SingleOrDefault(d => d.StableId == device.StableId)
                    ?? throw new InvalidOperationException("선택한 모니터가 삭제되었거나 변경되었습니다.");
                return operations.SetEnabledAsync(current.InstanceId, enabled);
            });
            Status = enabled
                ? $"{device.DisplayName}을(를) 켰습니다. 화면이 보이지 않으면 '확장 모드 적용'을 눌러주세요."
                : $"{device.DisplayName}을(를) 껐습니다. '켜기'를 누르면 다시 사용할 수 있습니다.";
        }
        catch (Exception ex) { Status = $"변경 실패: {ex.Message}"; }
        finally { IsBusy = false; Refresh(); }
    }

    // Windows 확장 모드를 적용하고 목록을 갱신합니다. Win32 오류는 상태 문구로 표시합니다.
    private async Task Extend()
    {
        try
        {
            await changes.RunAsync("display.extend", () => { operations.Extend(); return Task.CompletedTask; });
            Status = "Windows 확장 모드를 적용했습니다.";
            Refresh();
        }
        catch (Exception ex) { Status = $"확장 모드 적용 실패: {ex.Message}"; }
    }
}
// 공부할 때는 MainViewModel → VirtualDisplayDevice → DisplayService, 미리보기는 PreviewViewModel → PreviewCaptureService → ScreenCapture 순서로 읽으면 좋습니다.
