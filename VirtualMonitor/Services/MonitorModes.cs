using System.Runtime.InteropServices;

namespace VirtualMonitor;

internal sealed class MonitorModes : IMonitorModes
{
    private readonly IMonitorCatalog catalog;

    internal MonitorModes(IMonitorCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    internal sealed record Mode(int Width, int Height, int Hertz)
    {
        public string Label => $"{Width} × {Height} · {Hertz} Hz";
    }

    // Windows가 지원하는 모드를 열거하여 32비트 색상과 유효한 주사율만 중복 없이 정렬해 반환합니다.
    public IReadOnlyList<Mode> Get(string name)
    {
        var result = new HashSet<Mode>();
        for (int i = 0; ; i++)
        {
            var mode = NewMode();
            if (!EnumDisplaySettingsEx(name, i, ref mode, 0)) break;
            if (mode.BitsPerPel == 32 && mode.Frequency > 1)
                result.Add(new((int)mode.Width, (int)mode.Height, (int)mode.Frequency));
        }
        return result.OrderBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => m.Hertz).ToArray();
    }

    // 현재 설정 인덱스(-1)로 해상도와 주사율을 조회합니다. 실패하면 null입니다.
    public Mode? Current(string name)
    {
        var mode = NewMode();
        return EnumDisplaySettingsEx(name, -1, ref mode, 0)
            ? new((int)mode.Width, (int)mode.Height, (int)mode.Frequency) : null;
    }

    // 활성 모니터를 ID·슬롯으로 다시 찾아 모드를 검증합니다. 시험 적용 성공 후 해상도·주사율만 저장하여 화면 배치를 유지합니다.
    public void Apply(string monitorId, Mode selected)
    {
        var displays = catalog.GetDisplays().Where(d => d.IsVirtual).ToList();
        var display = displays.SingleOrDefault(d => d.MonitorId.Equals(monitorId, StringComparison.OrdinalIgnoreCase))
            ?? (MonitorNames.TryGetSlotIndex(monitorId, out int slot) ? displays.SingleOrDefault(d => d.SlotIndex == slot) : null)
            ?? throw new InvalidOperationException("선택한 가상 모니터가 켜져 있지 않거나 연결이 바뀌었습니다. 모니터를 켠 뒤 다시 시도하세요.");
        if (!Get(display.Name).Contains(selected)) throw new InvalidOperationException("현재 지원되지 않는 화면 모드입니다.");
        var mode = NewMode();
        if (!EnumDisplaySettingsEx(display.Name, -1, ref mode, 0)) throw new InvalidOperationException("현재 화면 모드를 읽지 못했습니다.");
        mode.Width = (uint)selected.Width; mode.Height = (uint)selected.Height; mode.Frequency = (uint)selected.Hertz;
        mode.Fields = 0x80000 | 0x100000 | 0x400000; // Width, height, refresh rate only; preserve placement.
        int result = ChangeDisplaySettingsEx(display.Name, ref mode, 0, 2, 0); // CDS_TEST
        if (result != 0) throw new InvalidOperationException($"화면 모드 검증 실패 ({result}).");
        result = ChangeDisplaySettingsEx(display.Name, ref mode, 0, 1, 0); // CDS_UPDATEREGISTRY
        if (result != 0) throw new InvalidOperationException(result == 1 ? "화면 모드 적용에 Windows 재시작이 필요합니다." : $"화면 모드 적용 실패 ({result}).");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Current(display.Name) == selected) return;
            Thread.Sleep(100); // Called only by the background configuration transaction.
        }
        throw new TimeoutException("Windows가 요청한 해상도와 주사율을 적용했는지 확인하지 못했습니다.");
    }

    // Win32 DEVMODE 구조체의 크기와 고정 문자열을 초기화합니다.
    private static DevMode NewMode() => new() { Size = (ushort)Marshal.SizeOf<DevMode>(), DeviceName = "", FormName = "" };
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    // Windows에서 지원 모드 또는 현재 화면 설정을 읽는 P/Invoke 선언입니다.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string name, int index, ref DevMode mode, uint flags);
    // 화면 모드의 시험 적용·저장을 요청합니다. 결과 0이 성공이며 HRESULT와는 다른 코드입니다.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string name, ref DevMode mode, nint hwnd, uint flags, nint parameter);
}
