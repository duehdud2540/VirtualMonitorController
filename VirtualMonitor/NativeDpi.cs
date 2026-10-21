using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VirtualMonitor;

internal static class NativeDpi
{
    // The EXE manifest already enables this. Also establish it before WPF starts
    // when diagnostics are launched through `dotnet VirtualMonitor.dll`.
    // WPF 시작 전에 모니터별 DPI 인식을 설정합니다. dotnet으로 DLL을 실행하는 진단 경로도 고려합니다.
    [ModuleInitializer]
    internal static void Initialize() => SetProcessDpiAwarenessContext(-4);

    // 프로세스 DPI 인식을 지정합니다. 호출부의 -4는 Per-Monitor V2입니다.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
}
