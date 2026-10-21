using System.ComponentModel;
using System.Windows;

namespace VirtualMonitor;

internal partial class MonitorManagerWindow : Window
{
    private readonly MonitorManagerViewModel viewModel;
    internal bool IsBusy => viewModel.IsBusy;
    // 관리 화면 XAML에 ViewModel을 연결합니다.
    internal MonitorManagerWindow(MonitorManagerViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    // 장치 변경 중 창 닫기를 취소하여 작업이 끝날 때까지 관리 화면을 유지합니다.
    protected override void OnClosed(EventArgs e) { viewModel.Dispose(); base.OnClosed(e); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (viewModel.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
