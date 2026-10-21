using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VirtualMonitor;

// View-only operations: window lifetime, native window chrome and DPI-dependent sizing.
internal sealed partial class PreviewWindow : Window, IConfigurationParticipant
{
    private readonly PreviewViewModel viewModel;
    private readonly IConfigurationChanges changes;
    internal long PresentedFrames => viewModel.PresentedFrames;
    internal string DiagnosticStatus => viewModel.Status;
    internal double CaptureMilliseconds => viewModel.CaptureMilliseconds;
    internal double PresentationMilliseconds => viewModel.PresentationMilliseconds;
    internal long CaptureRequests => viewModel.CaptureRequests;
    internal event Action<string>? DuplicateMonitorOpened
    {
        add => viewModel.DuplicateMonitorOpened += value;
        remove => viewModel.DuplicateMonitorOpened -= value;
    }
    // 캡처 자원 해제를 기다린 뒤 장치 재구성을 허용합니다.
    internal async Task PrepareConfigurationChangeAsync()
    {
        await viewModel.PrepareConfigurationChangeAsync();
    }
    // ViewModel의 목록 감시와 비트맵 캡처를 재개합니다.
    internal async Task CompleteConfigurationChangeAsync()
    {
        await viewModel.CompleteConfigurationChangeAsync();
    }
    // 창을 특정 가상 모니터에 연결하도록 ViewModel에 전달합니다.
    internal void SelectVirtualMonitor(MonitorDevice? device) => viewModel.SelectVirtualMonitor(device);

    // 이 브랜치는 XAML Image에 바인딩된 WriteableBitmap을 항상 사용합니다.
    Task IConfigurationParticipant.PrepareAsync() => PrepareConfigurationChangeAsync();
    Task IConfigurationParticipant.ResumeAsync() => CompleteConfigurationChangeAsync();

    internal PreviewWindow(PreviewViewModel viewModel, IConfigurationChanges changes, MonitorDevice? device = null)
    {
        this.viewModel = viewModel;
        this.changes = changes;
        try
        {
            InitializeComponent();
            if (device != null) viewModel.SelectVirtualMonitor(device);
            DataContext = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdatePreviewChrome();
            ApplyScale();
            changes.Register(this);
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }
    }

    internal Task StopAsync() => viewModel.StopAsync();

    private bool closeReady, closing;
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!closeReady)
        {
            e.Cancel = true;
            if (!closing)
            {
                closing = true;
                await viewModel.StopAsync();
                closeReady = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        base.OnClosing(e);
    }

    // 창이 준비되면 캡처와 타이머를 시작합니다.
    private void Window_Loaded(object sender, RoutedEventArgs e) => viewModel.Start();
    // 창의 DPI 배율이 바뀌면 픽셀 표시 크기를 다시 계산합니다.
    private void Window_DpiChanged(object sender, DpiChangedEventArgs e) => ApplyScale();
    // 최소화 중 캡처를 정지하여 보이지 않는 화면의 작업을 줄입니다.
    private void Window_StateChanged(object? sender, EventArgs e) =>
        viewModel.SetPaused(WindowState == WindowState.Minimized);
    // 속성 이벤트 구독을 해제하고 캡처·네이티브 출력 자원을 정리합니다.
    private void Window_Closed(object? sender, EventArgs e)
    {
        changes.Unregister(this);
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.Dispose();
    }

    // 속성 변화를 실제 창 배율·제목 표시줄·전체 화면에 반영합니다.
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewViewModel.Frame):
            case nameof(PreviewViewModel.SelectedDisplay):
            case nameof(PreviewViewModel.ActualPixels):
                ApplyScale();
                break;
            case nameof(PreviewViewModel.IsCompactMode):
                UpdatePreviewChrome();
                break;
            case nameof(PreviewViewModel.IsFullScreen):
                ApplyFullScreen();
                break;
        }
    }

    // 사용자 정의 제목 표시줄의 최소화 명령을 Windows 창 명령에 연결합니다.
    private void Minimize_Executed(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    // 사용자 정의 제목 표시줄에서 현재 창을 닫습니다.
    private void Close_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
    // 현재 최대화 상태에 따라 복원 또는 최대화를 요청합니다.
    private void MaximizeRestore_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    // 1:1 모드에서는 실제 픽셀 수를 DPI 배율로 나눠 WPF 논리 크기를 구합니다. 창 맞춤 모드에서는 비율을 유지하고 스크롤 설정을 바꿉니다.
    private void ApplyScale()
    {
        bool native = viewModel.ActualPixels;
        var surface = viewModel.Frame;
        var dpi = VisualTreeHelper.GetDpi(this);
        preview.Width = native && surface != null ? surface.PixelWidth / dpi.DpiScaleX : double.NaN;
        preview.Height = native && surface != null ? surface.PixelHeight / dpi.DpiScaleY : double.NaN;
        preview.Stretch = native ? Stretch.Fill : Stretch.Uniform;
        viewport.HorizontalScrollBarVisibility = viewport.VerticalScrollBarVisibility = native ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        viewport.HorizontalContentAlignment = native ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        viewport.VerticalContentAlignment = native ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        RenderOptions.SetBitmapScalingMode(preview, native ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }
}
