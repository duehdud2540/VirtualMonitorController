using System.Windows;
using System.Windows.Shell;

namespace VirtualMonitor;

internal sealed partial class PreviewWindow
{
    private WindowState previousState;

    // 간소화·전체 화면 상태에 따라 테두리와 레이아웃을 조절합니다. WindowChrome으로 간소화 모드의 창 이동·크기 조절을 지원합니다.
    private void UpdatePreviewChrome()
    {
        bool compact = viewModel.IsCompactMode && !viewModel.IsFullScreen;
        WindowStyle = compact || viewModel.IsFullScreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowChrome.SetWindowChrome(this, compact ? new WindowChrome
        {
            CaptionHeight = 28, ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        } : null);
        previewLayout.Margin = compact ? new Thickness(0) : new Thickness(12);
        compactHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        previewControls.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        footer.Height = compact ? 22 : double.NaN;
        status.Margin = compact ? new Thickness(6, 0, 4, 0) : new Thickness(8);
        status.FontSize = compact ? 11 : 12;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        actualPixels.FontSize = compact ? 11 : 12;
    }

    // 기존 창 상태를 기억하고 테두리 없는 최대화로 전환합니다. 종료 시 이전 상태와 간소화 설정을 복원합니다.
    private void ApplyFullScreen()
    {
        if (!viewModel.IsFullScreen)
        {
            WindowState = WindowState.Normal;
            UpdatePreviewChrome();
            WindowState = previousState;
        }
        else
        {
            previousState = WindowState;
            WindowState = WindowState.Normal;
            UpdatePreviewChrome();
            WindowState = WindowState.Maximized;
        }
    }
}
