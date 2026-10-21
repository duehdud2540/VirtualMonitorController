using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VirtualMonitor;

// Windows Graphics Capture delivers GPU frames with the system cursor included.
// The mapped staging buffer is reused and copied by WPF before the next read.
internal sealed class ScreenCapture : ICaptureSession
{
    private nint handle;
    private bool apartmentInitialized;
    public nint Pixels { get; private set; }
    public nint Texture { get; private set; }
    public nint Device { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; private set; }
    public int BufferSize => checked(Stride * Height);
    public string DisplayName { get; }

    // 현재 스레드의 WinRT를 준비하고 화면 중심 좌표로 모니터를 찾아 C++ 캡처 세션을 생성합니다. 실패하면 부분 생성 자원도 정리합니다.
    public ScreenCapture(MonitorDisplay display)
    {
        Width = display.Width; Height = display.Height; DisplayName = display.Name;
        try
        {
            int apartment = RoInitialize(1);
            if (apartment >= 0) apartmentInitialized = true;
            else if (apartment != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(apartment);
            nint monitor = MonitorFromPoint(new Point { X = display.Left + display.Width / 2, Y = display.Top + display.Height / 2 }, 0);
            if (monitor == 0) throw new InvalidOperationException("선택한 가상 모니터를 찾을 수 없습니다.");
            Check(CaptureCreate(monitor, out handle));
        }
        catch { Dispose(); throw; }
    }

    // 새 프레임의 포인터와 stride를 갱신합니다. S_FALSE는 새 프레임 없음이며 메모리는 다음 읽기·해제 전에 복사해야 합니다.
    public bool Read(MonitorDisplay display, bool gpu = false)
    {
        if (display.Width != Width || display.Height != Height || display.Name != DisplayName)
            throw new InvalidOperationException("화면 구성이 변경되었습니다.");
        nint pixels = 0, texture = 0, device = 0;
        int width, height, stride = 0;
        int result = gpu
            ? CaptureReadTexture(handle, out device, out texture, out width, out height)
            : CaptureRead(handle, out pixels, out width, out height, out stride);
        if (result == 1) return false; // No new frame yet; retain the last displayed image.
        Check(result);
        if (width != Width || height != Height)
            throw new InvalidOperationException("화면 해상도가 변경되었습니다. 화면 목록을 갱신하는 중입니다.");
        Pixels = pixels; Stride = stride;
        Texture = texture; Device = device;
        return true;
    }

    // 진단용 한 장을 얻기 위해 프레임을 제한 횟수만큼 기다립니다. 비트맵으로 복사하고 Freeze하여 다른 스레드에서도 읽을 수 있게 합니다.
    public static BitmapSource Capture(MonitorDisplay display, Func<MonitorDisplay, ICaptureSession> createSession)
    {
        using var capture = createSession(display);
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (capture.Read(display))
            {
                var frame = BitmapSource.Create(capture.Width, capture.Height, 96, 96, PixelFormats.Bgr32,
                    null, capture.Pixels, capture.BufferSize, capture.Stride);
                frame.Freeze();
                return frame;
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException("Windows에서 캡처 프레임을 받지 못했습니다.");
    }

    // 실패 HRESULT를 오류 코드와 설명을 포함한 관리 예외로 바꿉니다.
    private static void Check(int result)
    {
        if (result < 0) throw new COMException($"GPU 화면 캡처 실패 (0x{result:X8}): {Marshal.GetExceptionForHR(result)?.Message}", result);
    }
    // 네이티브 캡처를 해제하고 포인터를 비웁니다. WinRT 해제는 초기화에 성공한 같은 스레드에서 횟수를 맞춰야 합니다.
    public void Dispose()
    {
        if (handle != 0) { CaptureDestroy(handle); handle = 0; }
        Pixels = 0;
        if (apartmentInitialized) { RoUninitialize(); apartmentInitialized = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    // 좌표가 속한 모니터의 HMONITOR 핸들을 조회합니다.
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    // 현재 스레드에서 Windows Runtime 사용을 초기화합니다.
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    // 성공했던 WinRT 초기화와 짝을 이루어 현재 스레드의 사용을 종료합니다.
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    // C++ 캡처 DLL에서 세션을 만들고 불투명 포인터를 받습니다.
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Capture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureCreate(nint monitor, out nint capture);
    // C++에서 프레임 포인터·크기·행 간격을 받습니다. S_OK는 새 프레임, S_FALSE는 프레임 없음입니다.
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Capture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureRead(nint capture, out nint pixels, out int width, out int height, out int stride);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Capture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureReadTexture(nint capture, out nint device, out nint texture, out int width, out int height);
    // C++ 캡처 객체와 GPU 자원을 생성한 DLL에서 해제합니다.
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("VirtualMonitor.Capture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CaptureDestroy(nint capture);
}
