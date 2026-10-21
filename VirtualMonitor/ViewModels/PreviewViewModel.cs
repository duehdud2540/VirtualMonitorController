using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VirtualMonitor;

internal sealed class PreviewViewModel : ObservableObject, IDisposable
{
    private readonly IAppLog log;
    private readonly IPreviewCaptureService capture;
    private readonly IMonitorStateReader state;
    private readonly IPreviewOwnership ownership;
    private readonly DispatcherTimer deviceTimer;
    private readonly DispatcherTimer statisticsTimer;
    private bool outputError;
    private long framesAtError;
    private bool changingConfiguration;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private IReadOnlyList<MonitorDisplay> displays = Array.Empty<MonitorDisplay>();
    private MonitorDisplay? selectedDisplay;
    private WriteableBitmap? frame;
    private string status = "";
    private bool actualPixels, compactMode, fullScreen, topmost, paused, disposed;
    private int frameCount;
    private double lastLogged;
    private double statsStarted;
    internal Func<ICaptureSession, bool>? OutputFrame { get => capture.WorkerOutput; set => capture.WorkerOutput = value; }
    internal Action? OutputTick { get => capture.WorkerTick; set => capture.WorkerTick = value; }
    internal event Action? OutputFailed { add => capture.OutputFailed += value; remove => capture.OutputFailed -= value; }
    internal Task StopAsync() => capture.StopAsync();
    internal Action? ClearOutput { get; set; }
    internal Func<string>? SampleOutputStatistics { get; set; }
    internal Action? ResetOutputStatistics { get; set; }

    // 캡처 이벤트, 화면 감시·통계 타이머와 표시 모드 명령을 연결합니다.
    public PreviewViewModel(Dispatcher dispatcher, IPreviewCaptureService capture, IMonitorStateReader state, IPreviewOwnership ownership, IAppLog log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        capture.FrameReady += Present;
        capture.Error += ReportError;
        deviceTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => _ = RefreshDisplaysAsync(), dispatcher);
        deviceTimer.Stop();
        statisticsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateOutputStatistics(), dispatcher);
        statisticsTimer.Stop();
        ToggleFullScreenCommand = new RelayCommand(() => IsFullScreen = !IsFullScreen);
        ExitFullScreenCommand = new RelayCommand(() => IsFullScreen = false, () => IsFullScreen);
        CompactModeCommand = new RelayCommand(() => IsCompactMode = true);
        NormalModeCommand = new RelayCommand(() => IsCompactMode = false);
    }

    public event Action<string>? DuplicateMonitorOpened;
    private string? boundKey;
    private string? preferredStableId;
    private bool reading;

    public RelayCommand ToggleFullScreenCommand { get; }
    public RelayCommand ExitFullScreenCommand { get; }
    public RelayCommand CompactModeCommand { get; }
    public RelayCommand NormalModeCommand { get; }
    public IReadOnlyList<MonitorDisplay> Displays
    {
        get => displays;
        private set => SetProperty(ref displays, value);
    }
    public MonitorDisplay? SelectedDisplay
    {
        get => selectedDisplay;
        set
        {
            if (Equals(selectedDisplay, value)) return;
            if (value != null)
            {
                try
                {
                    string key = GetDisplayKey(value);
                    if (!ownership.TryAcquire(key, this))
                        throw new InvalidOperationException($"이미 '{value.DisplayName}' 화면이 켜져 있습니다.");
                }
                catch (Exception ex)
                {
                    Status = ex.Message;
                    DuplicateMonitorOpened?.Invoke(ex.Message);
                    OnPropertyChanged();
                    return;
                }
            }
            UnregisterActiveDisplay(selectedDisplay);
            if (!SetProperty(ref selectedDisplay, value)) return;
            RegisterActiveDisplay(value);
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(WindowTitle));
            capture.SelectDisplay(value);
            Frame = null;
            ClearOutput?.Invoke();
            outputError = false;
            frameCount = 0;
            statsStarted = clock.Elapsed.TotalSeconds;
            if (value != null) Status = $"{value.Width} × {value.Height} 원본 캡처 · 프레임 도착에 맞춰 갱신";
        }
    }
    public WriteableBitmap? Frame { get => frame; private set => SetProperty(ref frame, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool ActualPixels { get => actualPixels; set => SetProperty(ref actualPixels, value); }
    public bool IsTopmost { get => topmost; set => SetProperty(ref topmost, value); }
    public bool IsCompactMode { get => compactMode; set => SetProperty(ref compactMode, value); }
    public bool CanSelectDisplay => !isBoundToDevice;
    public string HeaderTitle => SelectedDisplay?.DisplayName ?? preferredDisplayName ?? "가상 모니터";
    public string WindowTitle => $"{HeaderTitle} · 원본 화질 / 자동 프레임 갱신";
    public bool IsFullScreen
    {
        get => fullScreen;
        private set
        {
            if (SetProperty(ref fullScreen, value)) ExitFullScreenCommand.NotifyCanExecuteChanged();
        }
    }
    private long presentedFrames;
    public long PresentedFrames => presentedFrames + capture.SubmittedFrameCount;
    public double CaptureMilliseconds => capture.CaptureMilliseconds;
    public double PresentationMilliseconds => capture.PresentationMilliseconds;
    public long CaptureRequests => capture.CaptureRequests;

    // 화면 소유권 검사 키를 슬롯·ID·GDI 이름 순서로 만듭니다.
    internal static string GetDisplayKey(MonitorDisplay display) =>
        !string.IsNullOrEmpty(display.StableId) ? $"stable:{display.StableId}" :
        display.SlotIndex >= 0
            ? $"slot:{display.SlotIndex}"
            : !string.IsNullOrEmpty(display.MonitorId)
                ? $"id:{display.MonitorId}"
                : $"name:{display.Name}";

    // 공유 사전에서 현재 ViewModel을 화면 소유자로 등록합니다. 사전 접근은 lock으로 보호합니다.
    private void RegisterActiveDisplay(MonitorDisplay? display)
    {
        if (display == null) return;
        string key = GetDisplayKey(display);
        if (!ownership.TryAcquire(key, this)) throw new InvalidOperationException("다른 창이 모니터를 사용 중입니다.");
    }

    // 소유자가 자신일 때만 등록을 제거하여 다른 창의 소유권을 지우지 않게 합니다.
    private void UnregisterActiveDisplay(MonitorDisplay? display)
    {
        if (display == null) return;
        string key = GetDisplayKey(display);
        if (key != boundKey) ownership.Release(key, this);
    }

    // 해당 화면을 다른 ViewModel이 사용 중인지 공유 사전에서 확인합니다.
    private bool IsDisplayOwnedByOther(MonitorDisplay display)
    {
        string key = GetDisplayKey(display);
        return ownership.IsOwned(key, this);
    }

    // 목록과 통계를 초기화하고 타이머 및 캡처 서비스를 시작합니다.
    public void Start()
    {
        if (disposed) return;
        _ = RefreshDisplaysAsync();
        deviceTimer.Start();
        ResetOutputStatistics?.Invoke();
        statisticsTimer.Start();
        capture.Start();
    }

    // 최소화 상태를 캡처에 전달하고 측정을 초기화합니다. 구성 변경 중에는 최소화를 해제해도 캡처를 정지합니다.
    public void SetPaused(bool value)
    {
        paused = value;
        capture.SetPaused(value || changingConfiguration);
        ResetOutputStatistics?.Invoke();
        if (value && SampleOutputStatistics != null && !outputError && SelectedDisplay != null)
            Status = "DirectX · 최소화로 출력 일시 정지";
    }

    // 타이머를 멈추고 캡처 자원 해제를 최대 10초 기다립니다. 정지 요청과 실제 해제 완료를 구분합니다.
    internal async Task PrepareConfigurationChangeAsync()
    {
        if (disposed) return;
        changingConfiguration = true;
        deviceTimer.Stop();
        statisticsTimer.Stop();
        Status = "모니터 구성 변경 준비 중 · 캡처를 종료합니다.";
        await capture.PauseAndReleaseAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    // 모니터를 다시 찾고 기존 정지 상태를 반영한 뒤 타이머와 통계를 재개합니다.
    internal async Task CompleteConfigurationChangeAsync()
    {
        if (disposed) return;
        changingConfiguration = false;
        while (reading && !disposed) await Task.Delay(10);
        if (disposed) return;
        await RefreshDisplaysAsync();
        capture.SetPaused(paused);
        ResetOutputStatistics?.Invoke();
        deviceTimer.Start();
        statisticsTimer.Start();
    }

    // 정상 출력 중일 때만 DirectX 측정값과 픽셀 표시 모드를 상태 문구에 반영합니다.
    private void UpdateOutputStatistics()
    {
        if (disposed || paused || changingConfiguration || SelectedDisplay == null || SampleOutputStatistics == null) return;
        if (outputError && PresentedFrames <= framesAtError) return;
        outputError = false;
        Status = $"{SelectedDisplay.Width} × {SelectedDisplay.Height} · {SampleOutputStatistics()} · " +
            (ActualPixels ? "1:1 픽셀" : "창에 맞춤");
        if (clock.Elapsed.TotalSeconds - lastLogged >= 30)
        {
            lastLogged = clock.Elapsed.TotalSeconds;
            log.Write("preview.statistics", new
            {
                monitor = SelectedDisplay.StableId, submitted = PresentedFrames,
                capture.CaptureRequests, capture.CaptureMilliseconds,
                capture.PresentationMilliseconds, capture.DispatcherWaitMilliseconds
            });
        }
    }

    private bool isBoundToDevice;
    private int? preferredSlotIndex;
    private string? preferredMonitorId;
    private string? preferredGdiDeviceName;
    private string? preferredDisplayName;

    // 창을 특정 모니터에 고정하고 재조회에 사용할 슬롯·ID·이름을 기억합니다. 다른 화면 선택도 비활성화합니다.
    internal void SelectVirtualMonitor(MonitorDevice? device)
    {
        if (device == null) return;
        string nextKey = MainViewModel.GetMonitorKey(device);
        if (!ownership.TryAcquire(nextKey, this)) throw new InvalidOperationException("이미 이 모니터의 미리보기가 열려 있습니다.");
        if (boundKey != null && boundKey != nextKey) ownership.Release(boundKey, this);
        boundKey = nextKey;
        preferredStableId = device.StableId;
        isBoundToDevice = true;
        preferredSlotIndex = device.SlotIndex >= 0 ? device.SlotIndex : null;
        preferredMonitorId = !string.IsNullOrEmpty(device.MonitorId) ? device.MonitorId : null;
        preferredGdiDeviceName = !string.IsNullOrEmpty(device.GdiDeviceName) ? device.GdiDeviceName : null;
        preferredDisplayName = device.DisplayName;
        OnPropertyChanged(nameof(CanSelectDisplay));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(WindowTitle));
        if (Displays.Count > 0)
        {
            _ = RefreshDisplaysAsync();
        }
    }

    // 기억한 슬롯·ID·GDI 이름을 비교하여 이 창에 연결된 화면인지 검사합니다.
    private bool MatchesBoundMonitor(MonitorDisplay d)
    {
        if (!string.IsNullOrEmpty(preferredStableId)) return d.StableId == preferredStableId;
        if (preferredSlotIndex.HasValue && d.SlotIndex >= 0)
            return d.SlotIndex == preferredSlotIndex.Value;
        if (!string.IsNullOrEmpty(preferredMonitorId) && !string.IsNullOrEmpty(d.MonitorId))
            return d.MonitorId.Equals(preferredMonitorId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(preferredGdiDeviceName))
            return d.Name.Equals(preferredGdiDeviceName, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    // 가상 화면을 다시 열거하고 고정 대상 또는 사용 가능한 화면을 선택합니다. 대상이 사라지면 선택을 비우고 안내합니다.
    private async Task RefreshDisplaysAsync()
    {
        if (changingConfiguration || disposed || reading) return;
        reading = true;
        try
        {
            var snapshot = await state.ReadAsync();
            if (changingConfiguration || disposed) return;
            var allVirtual = snapshot.Displays.Where(d => d.IsVirtual).ToList();
            var updated = isBoundToDevice
                ? allVirtual.Where(MatchesBoundMonitor).ToList()
                : allVirtual;
            if (!Displays.SequenceEqual(updated))
            {
                var next = (preferredSlotIndex.HasValue ? updated.FirstOrDefault(d => d.SlotIndex == preferredSlotIndex.Value) : null)
                    ?? (!string.IsNullOrEmpty(preferredMonitorId) ? updated.FirstOrDefault(d => d.MonitorId.Equals(preferredMonitorId, StringComparison.OrdinalIgnoreCase)) : null)
                    ?? updated.FirstOrDefault(d => d.Name == SelectedDisplay?.Name)
                    ?? updated.FirstOrDefault(d => !IsDisplayOwnedByOther(d))
                    ?? updated.FirstOrDefault();
                Displays = updated;
                SelectedDisplay = next;
            }
            else if (SelectedDisplay == null && updated.Count > 0)
            {
                SelectedDisplay = updated.FirstOrDefault(d => !IsDisplayOwnedByOther(d)) ?? updated[0];
            }
            if (updated.Count == 0)
            {
                SelectedDisplay = null;
                Status = isBoundToDevice && !string.IsNullOrEmpty(preferredDisplayName)
                    ? $"'{preferredDisplayName}' 화면이 활성화되어 있지 않습니다. 모니터를 켜고 확장 모드를 적용해 주세요."
                    : "활성 MttVDD 화면이 없습니다. 모니터를 켜고 확장 모드를 적용해 주세요.";
            }
        }
        catch (Exception ex)
        {
            SelectedDisplay = null;
            Frame = null;
            Status = ex.Message;
            log.Write("preview.refresh", error: ex);
        }
        finally { reading = false; }
    }

    // 오류 상태를 기록하고 출력을 비운 뒤 메시지를 표시합니다. 종료된 ViewModel에는 적용하지 않습니다.
    internal void ReportError(string message)
    {
        if (disposed) return;
        outputError = true;
        framesAtError = PresentedFrames;
        Frame = null;
        ClearOutput?.Invoke();
        Status = message;
    }

    // CPU 대체 경로만 UI 스레드에서 현재 화면의 비트맵으로 복사합니다.
    private void Present(MonitorDisplay display, ICaptureSession buffer)
    {
        if (disposed || paused || changingConfiguration || SelectedDisplay != display) return;
        if (Frame == null || Frame.PixelWidth != buffer.Width || Frame.PixelHeight != buffer.Height)
            Frame = new WriteableBitmap(buffer.Width, buffer.Height, 96, 96, PixelFormats.Bgr32, null);
        Frame.WritePixels(new Int32Rect(0, 0, buffer.Width, buffer.Height), buffer.Pixels, buffer.BufferSize, buffer.Stride);
        outputError = false;
        frameCount++;
        presentedFrames++;
        double now = clock.Elapsed.TotalSeconds;
        if (now - statsStarted >= 1)
        {
            Status = $"{buffer.Width} × {buffer.Height} · 원본 무압축 · {frameCount / (now - statsStarted):F1}fps 갱신 · " +
                (ActualPixels ? "1:1 픽셀" : "창에 맞춤 (글자가 흐리면 1:1 선택)") + " · 보기 전용";
            frameCount = 0;
            statsStarted = now;
        }
    }

    // 화면 소유권, 타이머, 캡처와 이벤트를 정리합니다. 중복 호출에도 한 번만 처리합니다.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        UnregisterActiveDisplay(selectedDisplay);
        if (boundKey != null) ownership.Release(boundKey, this);
        deviceTimer.Stop();
        statisticsTimer.Stop();
        capture.Dispose();
        capture.FrameReady -= Present;
        capture.Error -= ReportError;
        Frame = null;
    }
}
