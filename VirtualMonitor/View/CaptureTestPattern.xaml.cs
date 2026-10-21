using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VirtualMonitor;

// Only shown during the explicit animated capture diagnostic; never in normal use.
internal sealed partial class CaptureTestPattern : Window
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    // 진단용 창을 대상 모니터에 배치하고 렌더링 이벤트를 연결합니다. 닫을 때 구독을 해제합니다.
    public CaptureTestPattern(MonitorDisplay display)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SetWindowPos(new WindowInteropHelper(this).Handle, 0, display.Left + 20, display.Top + 20, 700, 240, 0x14);
            CompositionTarget.Rendering += Animate;
        };
        Closed += (_, _) => CompositionTarget.Rendering -= Animate;
    }
    // 시간의 사인값으로 도형을 왕복 이동시켜 캡처 대상에 화면 변화가 생기게 합니다.
    private void Animate(object? sender, EventArgs e)
    {
        motion.X = 280 + 260 * Math.Sin(elapsed.Elapsed.TotalSeconds * 3);
        motion.Y = 60;
    }
    // 진단 창의 위치와 크기를 실제 화면 좌표로 지정합니다.
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
