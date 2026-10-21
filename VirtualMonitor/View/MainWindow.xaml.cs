using System.ComponentModel;
using System.Windows;

namespace VirtualMonitor;

// Window and tray lifetime stay in the View; device state and commands live in MainViewModel.
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly IWindowFactory windows;
    private readonly IApplicationLifetime lifetime;
    private readonly IAppLog log;
    private readonly Dictionary<string, PreviewWindow> previews = new(StringComparer.OrdinalIgnoreCase);
    private MonitorManagerWindow? monitorManager;
    internal int DuplicateMonitorNoticeCount { get; private set; }

    // XAML과 ViewModel을 연결하고 창 열기·중복 알림 이벤트를 구독합니다.
    internal MainWindow(MainViewModel viewModel, IWindowFactory windows, IApplicationLifetime lifetime, IAppLog log)
    {
        this.viewModel = viewModel;
        this.windows = windows;
        this.lifetime = lifetime;
        this.log = log;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PreviewRequested += ShowPreview;
        viewModel.DuplicateMonitorOpened += OnDuplicateMonitorOpened;
    }

    // 창이 로드되면 최초 조회와 주기 갱신을 시작합니다.
    private void Window_Loaded(object sender, RoutedEventArgs e) => viewModel.Start();
    // 관리 창을 모달로 엽니다. 변경 전 모든 미리보기 캡처를 해제하고 변경 후 캡처와 목록 갱신을 재개하도록 연결합니다.
    private void ManageMonitors_Click(object sender, RoutedEventArgs e)
    {
        monitorManager = windows.CreateMonitorManager();
        monitorManager.Owner = this;
        try { monitorManager.ShowDialog(); }
        finally { monitorManager = null; viewModel.Refresh(); }
    }
    // 트레이 사용 중에는 최소화된 메인 창을 숨깁니다.
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && lifetime.TrayEnabled) Hide();
    }

    // 모니터 키당 하나의 창을 생성하고 추적합니다. 창이 닫히면 등록을 해제하여 다시 열 수 있게 합니다.
    private void ShowPreview(MonitorDevice? device)
    {
        if (monitorManager?.IsBusy == true) return;
        string key = MainViewModel.GetMonitorKey(device);
        PreviewWindow? window = null;
        try
        {
            if (previews.TryGetValue(key, out var existing) && existing.IsLoaded)
            {
                string name = device?.DisplayName ?? "가상 모니터";
                viewModel.NotifyDuplicateMonitorOpened($"이미 '{name}' 화면이 켜져 있습니다.");
                return;
            }

            window = windows.CreatePreview(device);
            previews[key] = window;
            window.DuplicateMonitorOpened += OnDuplicateMonitorOpened;
            window.Closed += (_, _) =>
            {
                window.DuplicateMonitorOpened -= OnDuplicateMonitorOpened;
                if (previews.TryGetValue(key, out var current) && ReferenceEquals(current, window))
                {
                    previews.Remove(key);
                }
            };
            window.Show();
            CascadeIfOverlapping(window);
            window.Activate();
        }
        catch (Exception ex)
        {
            if (window != null)
            {
                previews.Remove(key);
                window.Close();
            }
            log.Write("preview.open", error: ex);
            MessageBox.Show($"미리보기를 열지 못했습니다: {ex.Message}", "미리보기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 시작 위치가 기존 창과 거의 같으면 새 창을 조금 이동하여 구분하기 쉽게 합니다.
    private void CascadeIfOverlapping(PreviewWindow window)
    {
        foreach (var other in previews.Values)
        {
            if (ReferenceEquals(other, window) || !other.IsVisible) continue;
            if (Math.Abs(other.Left - window.Left) < 10 && Math.Abs(other.Top - window.Top) < 10)
            {
                window.Left += 36;
                window.Top += 36;
            }
        }
    }

    // 중복 요청을 메시지 상자로 알립니다. MVVM 진단에서는 횟수만 기록해 자동 검사가 대화상자에 막히지 않게 합니다.
    private void OnDuplicateMonitorOpened(string message)
    {
        DuplicateMonitorNoticeCount++;
        if (Environment.GetCommandLineArgs().Contains("--diagnose-mvvm", StringComparer.OrdinalIgnoreCase))
            return;
        if (IsVisible)
            MessageBox.Show(this, message, "가상 모니터 중복 실행 알림", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, "가상 모니터 중복 실행 알림", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // 메인 창을 복원·활성화하고 장치 목록을 새로 읽습니다.
    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        viewModel.Refresh();
    }

    // 트레이 메뉴의 미리보기 요청을 ViewModel에 전달합니다.
    internal void ShowPreviewFromTray() => viewModel.OpenPreview();

    // 관리 또는 장치 변경 작업 중이면 해당 창을 보여 주고 종료를 보류합니다.
    internal bool CanExit()
    {
        if (monitorManager?.IsBusy == true)
        {
            monitorManager.Activate();
            return false;
        }
        if (viewModel.CanExit()) return true;
        ShowFromTray();
        return false;
    }

    // 트레이 실행 중 일반 닫기는 취소하고 숨깁니다. 명시적 앱 종료 중에는 실제 닫기를 허용합니다.
    protected override void OnClosing(CancelEventArgs e)
    {
        var app = lifetime;
        if (app.TrayEnabled && !app.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
    // 이벤트와 ViewModel 타이머를 정리하고 열린 미리보기 창을 닫습니다.
    protected override void OnClosed(EventArgs e)
    {
        viewModel.PreviewRequested -= ShowPreview;
        viewModel.DuplicateMonitorOpened -= OnDuplicateMonitorOpened;
        viewModel.Dispose();
        foreach (var window in previews.Values.ToArray())
            window.Close();
        base.OnClosed(e);
    }
}
