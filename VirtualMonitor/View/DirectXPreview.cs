using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace VirtualMonitor;

// GPU submission runs on the capture worker. UI never waits for a presenting worker.
internal sealed class DirectXPreview : HwndHost
{
    private readonly object renderGate = new();
    private nint device;
    private int clearRequested, redrawRequested, resetRequested;
    private volatile bool visible;
    internal DirectXPreview(DirectXFrameStatistics statistics) { this.statistics = statistics; IsVisibleChanged += (_, _) => visible = IsVisible; }
    private nint renderer;
    private nint window;
    private volatile bool suspended;
    // 출력을 정지하고 렌더러를 해제하여 장치 변경 전에 GPU 자원을 정리합니다.
    internal void SuspendOutput() { suspended = true; ReleaseRenderer(); }
    // 출력을 허용하고 통계를 초기화합니다. 실제 렌더러는 다음 Present에서 만듭니다.
    internal void ResumeOutput() { suspended = false; ResetStatistics(); }

    private readonly DirectXFrameStatistics statistics;
    // 측정 구간을 초기화해 정지 시간과 이전 모니터 프레임을 제외합니다.
    internal void ResetStatistics() => Interlocked.Exchange(ref resetRequested, 1);
    // 구간별 수신·제출·재출력 FPS와 평균 시간을 상태 문구로 만듭니다.
    internal string SampleStatistics()
    {
        DirectXFrameStatistics.Snapshot sample;
        if (!Monitor.TryEnter(renderGate)) return "DirectX 출력 처리 중";
        try { sample = statistics.Sample(); }
        finally { Monitor.Exit(renderGate); }
        return $"DirectX 새 프레임 제출 {sample.SubmittedFps:F1}fps · 캡처 수신 {sample.ReceivedFps:F1}fps · " +
            $"재출력 {sample.RedrawFps:F1}fps · 업로드/제출 {sample.AverageSubmitMilliseconds:F2}ms";
    }

    // WPF HwndHost 안에 Win32 자식 창을 만들어 DirectX 출력용 HWND를 제공합니다.
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        window = CreateWindowEx(0, "static", "", 0x50000000 | 0x04000000 | 0x02000000,
            0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new HandleRef(this, window);
    }

    // 렌더러를 필요할 때 생성하고 픽셀을 업로드·제출합니다. 실패 시 자원을 해제하며 S_OK만 성공으로 반환합니다.
    internal bool Present(ICaptureSession capture)
    {
        lock (renderGate)
        {
            if (Interlocked.Exchange(ref resetRequested, 0) != 0) statistics.Reset();
            statistics.Receive();
            if (suspended || window == 0 || !visible) return false;
            try
            {
                if (device != capture.Device) ReleaseRenderer();
                if (renderer == 0)
                {
                    Marshal.ThrowExceptionForHR(PreviewCreateOnDevice(window, capture.Device, out renderer));
                    device = capture.Device;
                }
                Interlocked.Exchange(ref clearRequested, 0);
                Interlocked.Exchange(ref redrawRequested, 0);
                long started = Stopwatch.GetTimestamp();
                int result = PreviewDrawTexture(renderer, capture.Texture);
                statistics.Submit(result, true, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Marshal.ThrowExceptionForHR(result);
                return result == 0;
            }
            catch { ReleaseRenderer(); throw; }
        }
    }

    // Called by the capture worker even when the desktop has no new frame.
    internal void Tick()
    {
        lock (renderGate)
        {
            if (renderer == 0 || suspended) return;
            if (Interlocked.Exchange(ref clearRequested, 0) != 0) PreviewClear(renderer);
            if (Interlocked.Exchange(ref redrawRequested, 0) == 0) return;
            long started = Stopwatch.GetTimestamp();
            int result = PreviewDraw(renderer, 0, 0, 0, 0);
            statistics.Submit(result, false, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Marshal.ThrowExceptionForHR(result);
        }
    }

    // 통계를 초기화하고 렌더러의 마지막 이미지를 비웁니다.
    internal void Clear()
    {
        ResetStatistics();
        Interlocked.Exchange(ref clearRequested, 1);
    }

    // 호스트 크기·위치가 바뀌면 마지막 프레임을 새 크기에 맞춰 다시 그립니다.
    protected override void OnWindowPositionChanged(Rect bounds)
    {
        base.OnWindowPositionChanged(bounds);
        Redraw();
    }

    // 새 픽셀 없이 기존 텍스처를 재제출합니다. 새 프레임과 별도 통계를 기록하고 오류를 알립니다.
    private void Redraw() => Interlocked.Exchange(ref redrawRequested, 1);

    // 배경 지우기·그리기 메시지를 처리합니다. DirectX가 배경을 그려 기본 지우기로 인한 깜박임을 줄입니다.
    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0014) { handled = true; return 1; } // DirectX owns the background.
        if (msg == 0x000F)
        {
            BeginPaint(hwnd, out var paint);
            try { Redraw(); }
            finally { EndPaint(hwnd, ref paint); }
            handled = true;
            return 0;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // 네이티브 렌더러를 해제하고 핸들을 0으로 만들어 이중 해제를 방지합니다.
    private void ReleaseRenderer()
    {
        lock (renderGate) { if (renderer != 0) { PreviewDestroy(renderer); renderer = 0; } device = 0; }
    }

    // 렌더러를 먼저 해제하고 Win32 자식 창을 파괴합니다.
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ReleaseRenderer();
        DestroyWindow(hwnd.Handle);
        window = 0;
    }

    // DirectX가 그릴 Win32 자식 창을 생성합니다.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    // 생성한 Win32 창을 파괴합니다.
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint Hdc;
        public int Erase, Left, Top, Right, Bottom, Restore, IncUpdate;
        public long Reserved0, Reserved1, Reserved2, Reserved3;
    }
    // WM_PAINT 처리를 시작하고 그리기 정보를 얻습니다.
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, out PaintStruct paint);
    // BeginPaint에 대응하여 그리기 처리가 끝났음을 Windows에 알립니다.
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hwnd, ref PaintStruct paint);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Preview.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PreviewCreateOnDevice(nint hwnd, nint device, out nint renderer);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Preview.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PreviewDrawTexture(nint renderer, nint texture);

    // C++ 출력 DLL에 장치와 스왑 체인 생성을 요청합니다. 결과는 HRESULT입니다.
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Preview.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PreviewCreate(nint hwnd, out nint renderer);
    // C++에 픽셀과 stride를 전달합니다. pixels가 0이면 마지막 텍스처를 다시 그립니다.
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Preview.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PreviewDraw(nint renderer, nint pixels, int width, int height, int stride);
    // C++에서 이미지 자원을 비우고 검은 화면을 그립니다.
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Preview.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void PreviewClear(nint renderer);
    // C++에서 생성한 렌더러를 같은 DLL에서 해제하여 메모리 소유권을 맞춥니다.
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Preview.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void PreviewDestroy(nint renderer);
}
