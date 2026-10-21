using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VirtualMonitor;

internal sealed class DisplayService : IDisplayService
{
    private const uint QDC_ALL_PATHS = 1;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 1;
    private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    private const uint SDC_TOPOLOGY_EXTEND = 0x4;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    private const uint SDC_APPLY = 0x80;
    private const uint SDC_SAVE_TO_DATABASE = 0x200;
    private const uint SDC_ALLOW_CHANGES = 0x400;



    internal sealed record VirtualMonitorTarget(int SlotIndex, string MonitorId, bool IsActive, string GdiDeviceName, Luid AdapterId, uint TargetId);

    // 활성 모니터를 열거하여 좌표·크기·가상 여부를 구하고 별칭과 슬롯을 합칩니다. UI와 캡처가 이 목록을 사용합니다.
    public IReadOnlyList<MonitorDisplay> GetDisplays(IReadOnlyList<VirtualMonitorTarget>? targetSnapshot = null)
    {
        var displays = new List<MonitorDisplay>();
        var targets = targetSnapshot ?? GetVirtualMonitorTargets();
        MonitorEnum callback = (nint monitor, nint dc, ref Rect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
            if (GetMonitorInfo(monitor, ref info))
            {
                var target = targets.FirstOrDefault(t => t.IsActive && t.GdiDeviceName.Equals(info.Device, StringComparison.OrdinalIgnoreCase));
                string id = GetMonitorId(info.Device);
                if (string.IsNullOrEmpty(id) && target != null) id = target.MonitorId;
                bool isVirtual = target != null || IsMttMonitor(info.Device) || id.Contains("DISPLAY#MTT", StringComparison.OrdinalIgnoreCase);
                int slotIndex = isVirtual
                    ? (MonitorSlot.TryParse(id, out int s) ? s : target?.SlotIndex ?? -1)
                    : -1;
                displays.Add(new MonitorDisplay(info.Device,
                    $"{info.Bounds.Right - info.Bounds.Left} × {info.Bounds.Bottom - info.Bounds.Top} px · 위치 ({info.Bounds.Left}, {info.Bounds.Top})" +
                    ((info.Flags & 1) != 0 ? " · 주 모니터" : ""),
                    info.Bounds.Left, info.Bounds.Top, info.Bounds.Right - info.Bounds.Left,
                    info.Bounds.Bottom - info.Bounds.Top, isVirtual)
                    {
                        SlotIndex = slotIndex,
                        IsActive = true,
                        MonitorId = id,
                        Alias = ""
                    });
            }
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        return displays;
    }

    // 비활성 경로까지 조회해 MttVDD 타깃을 찾습니다. 어댑터·타깃 중복은 합치되 활성 경로를 우선하고 UID로 슬롯을 정합니다.
    public IReadOnlyList<VirtualMonitorTarget> GetVirtualMonitorTargets()
    {
        var (paths, modes) = ReadTopology(QDC_ALL_PATHS);
        uint pathCount = (uint)paths.Length;

        var rawTargets = new Dictionary<(Luid AdapterId, uint TargetId), (string MonitorId, bool IsActive, string GdiName, int SortUid)>();
        for (int i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            if (p.TargetInfo.TargetAvailable == 0) continue;
            var key = (p.TargetInfo.AdapterId, p.TargetInfo.Id);
            bool active = (p.Flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
            if (!rawTargets.TryGetValue(key, out var existing))
            {
                string monitorPath = GetTargetDevicePath(p.TargetInfo.AdapterId, p.TargetInfo.Id);
                if (!monitorPath.Contains("DISPLAY#MTT", StringComparison.OrdinalIgnoreCase)) continue;
                int sortUid = MonitorSlot.TryParse(monitorPath, out int slot) ? slot : (int)p.TargetInfo.Id;
                string gdi = active ? GetSourceDeviceName(p.SourceInfo.AdapterId, p.SourceInfo.Id) : "";
                rawTargets[key] = (monitorPath, active, gdi, sortUid);
            }
            else if (active && !existing.IsActive)
            {
                string gdi = GetSourceDeviceName(p.SourceInfo.AdapterId, p.SourceInfo.Id);
                rawTargets[key] = (existing.MonitorId, true, gdi, existing.SortUid);
            }
        }

        var ordered = rawTargets
            .OrderBy(kv => kv.Value.SortUid)
            .ThenBy(kv => kv.Key.TargetId)
            .ToList();

        var result = new List<VirtualMonitorTarget>(ordered.Count);
        for (int idx = 0; idx < ordered.Count; idx++)
        {
            var kv = ordered[idx];
            int slotIndex = MonitorSlot.TryParse(kv.Value.MonitorId, out int parsed) && parsed >= 0 ? parsed : idx;
            result.Add(new VirtualMonitorTarget(
                slotIndex,
                kv.Value.MonitorId,
                kv.Value.IsActive,
                kv.Value.GdiName,
                kv.Key.AdapterId,
                kv.Key.TargetId));
        }
        return result;
    }

    // 설정과 Windows 타깃을 합쳐 꺼진 모니터도 포함합니다. 비활성 슬롯은 크기 0의 자리표시 항목으로 만듭니다.


    // 켜 둘 슬롯 집합을 반영합니다. 직접 적용 결과가 부족하면 Windows 확장 구성을 계산한 뒤 선택하지 않은 가상 타깃을 끕니다.
    public void SetVirtualMonitorsActive(IReadOnlySet<int> activeSlots)
    {
        var targets = GetVirtualMonitorTargets();
        if (targets.Count == 0)
        {
            if (activeSlots.Count > 0) throw new InvalidOperationException("활성화할 모니터 타깃이 없습니다.");
            return;
        }

        bool needActivate = targets.Any(t => activeSlots.Contains(t.SlotIndex) && !t.IsActive);
        bool needDeactivate = targets.Any(t => !activeSlots.Contains(t.SlotIndex) && t.IsActive);
        if (!needActivate && !needDeactivate) return;

        if (needActivate)
        {
            bool directApplied = TryApplyMonitorSelection(activeSlots, targets);
            var activeVirtuals = GetDisplays().Where(d => d.IsVirtual).ToList();
            bool hasClonedAtOrigin = activeVirtuals.Any(d => d.Left == 0 && d.Top == 0);
            if (!directApplied || activeVirtuals.Count < activeSlots.Count || hasClonedAtOrigin)
            {
                // Let Windows compute valid extended modes for all connected targets, then disconnect unselected targets.
                Extend();
                targets = GetVirtualMonitorTargets();
                if (targets.Any(t => !activeSlots.Contains(t.SlotIndex)) && !DeactivateUnselectedTargets(activeSlots, targets))
                    throw new InvalidOperationException("선택하지 않은 화면 비활성화에 실패했습니다.");
            }
        }
        else if (needDeactivate)
        {
            if (!DeactivateUnselectedTargets(activeSlots, targets) && !TryApplyMonitorSelection(activeSlots, targets))
                throw new InvalidOperationException("화면 선택 적용에 실패했습니다.");
        }
    }

    // 물리 경로와 유지할 가상 경로를 보존하고 새 가상 화면에는 미사용 소스를 할당합니다. 모드 재계산을 허용해 적용하고 성공 여부를 반환합니다.
    private static bool TryApplyMonitorSelection(IReadOnlySet<int> activeSlots, IReadOnlyList<VirtualMonitorTarget> targets)
    {
        var (allPaths, allModes) = ReadTopology(QDC_ALL_PATHS);
        uint pathCount = (uint)allPaths.Length, modeCount = (uint)allModes.Length;

        var virtualMap = targets.ToDictionary(t => (t.AdapterId, t.TargetId), t => t.SlotIndex);
        var selectedPaths = new List<DisplayConfigPathInfo>();
        var usedSources = new HashSet<(Luid AdapterId, uint SourceId)>();
        var activatedVirtualSlots = new HashSet<int>();

        // 1. Keep all currently active physical paths and already-active desired virtual paths.
        for (int i = 0; i < pathCount; i++)
        {
            var p = allPaths[i];
            if ((p.Flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            var targetKey = (p.TargetInfo.AdapterId, p.TargetInfo.Id);
            if (virtualMap.TryGetValue(targetKey, out int slot))
            {
                if (!activeSlots.Contains(slot) || !activatedVirtualSlots.Add(slot))
                    continue;
            }
            selectedPaths.Add(p);
            usedSources.Add((p.SourceInfo.AdapterId, p.SourceInfo.Id));
        }

        // 2. Add newly requested virtual monitor paths with unused source IDs.
        foreach (var target in targets.Where(t => activeSlots.Contains(t.SlotIndex) && !activatedVirtualSlots.Contains(t.SlotIndex)))
        {
            DisplayConfigPathInfo? chosen = null;
            for (int i = 0; i < pathCount; i++)
            {
                var p = allPaths[i];
                if (p.TargetInfo.AdapterId != target.AdapterId || p.TargetInfo.Id != target.TargetId || p.TargetInfo.TargetAvailable == 0)
                    continue;
                if (usedSources.Contains((p.SourceInfo.AdapterId, p.SourceInfo.Id)))
                    continue;
                chosen = p;
                if (p.SourceInfo.Id == (uint)target.SlotIndex) break;
            }
            if (chosen is { } path)
            {
                path.Flags |= DISPLAYCONFIG_PATH_ACTIVE;
                path.SourceInfo.ModeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                path.TargetInfo.ModeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                selectedPaths.Add(path);
                usedSources.Add((path.SourceInfo.AdapterId, path.SourceInfo.Id));
                activatedVirtualSlots.Add(target.SlotIndex);
            }
        }

        if (selectedPaths.Count == 0) return false;
        var pathArray = selectedPaths.ToArray();
        int result = SetDisplayConfig((uint)pathArray.Length, pathArray, modeCount, allModes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        return result == 0;
    }

    // 선택 집합 밖의 가상 타깃만 끕니다. 남는 화면이 없으면 거부하고 적용 실패 시 활성 경로만 전달하여 재시도합니다.
    private static bool DeactivateUnselectedTargets(IReadOnlySet<int> activeSlots, IReadOnlyList<VirtualMonitorTarget> targets)
    {
        var unwantedTargets = targets
            .Where(t => !activeSlots.Contains(t.SlotIndex))
            .Select(t => (t.AdapterId, t.TargetId))
            .ToHashSet();
        if (unwantedTargets.Count == 0) return true;

        var (paths, modes) = ReadTopology(QDC_ONLY_ACTIVE_PATHS);
        uint pathCount = (uint)paths.Length, modeCount = (uint)modes.Length;

        int remainingActive = 0;
        for (int i = 0; i < pathCount; i++)
        {
            if (unwantedTargets.Contains((paths[i].TargetInfo.AdapterId, paths[i].TargetInfo.Id)))
                paths[i].Flags &= ~DISPLAYCONFIG_PATH_ACTIVE;
            else if ((paths[i].Flags & DISPLAYCONFIG_PATH_ACTIVE) != 0)
                remainingActive++;
        }
        if (remainingActive == 0) return false;

        int result = SetDisplayConfig(pathCount, paths, modeCount, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        if (result == 0) return true;

        // Fallback: pass only the remaining active paths.
        var activeOnly = paths.Take((int)pathCount).Where(p => (p.Flags & DISPLAYCONFIG_PATH_ACTIVE) != 0).ToArray();
        result = SetDisplayConfig((uint)activeOnly.Length, activeOnly, modeCount, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        return result == 0;
    }

    private static (DisplayConfigPathInfo[], DisplayConfigModeInfo[]) ReadTopology(uint flags)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int result = GetDisplayConfigBufferSizes(flags, out uint pc, out uint mc);
            if (result != 0) throw new Win32Exception(result);
            var paths = new DisplayConfigPathInfo[pc];
            var modes = new DisplayConfigModeInfo[mc];
            result = QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, 0);
            if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: topology changed between calls.
            if (result != 0) throw new Win32Exception(result);
            return (paths.Take((int)pc).ToArray(), modes.Take((int)mc).ToArray());
        }
        throw new InvalidOperationException("화면 구성이 계속 변경되어 조회하지 못했습니다. 다시 시도하세요.");
    }

    public Action CaptureTopology()
    {
        var (paths, modes) = ReadTopology(QDC_ONLY_ACTIVE_PATHS);
        return () =>
        {
            int result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE);
            if (result != 0) throw new Win32Exception(result, "이전 화면 배치 복원에 실패했습니다.");
        };
    }

    public async Task WaitForTargetsAsync(int count)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (GetVirtualMonitorTargets().Count == count) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Windows에서 모니터 {count}개가 나타나는 것을 확인하지 못했습니다.");
    }

    public async Task ApplyAndVerifyAsync(IReadOnlySet<int> slots)
    {
        SetVirtualMonitorsActive(slots);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var actual = GetVirtualMonitorTargets().Where(t => t.IsActive).Select(t => t.SlotIndex).ToHashSet();
            var visible = GetDisplays().Where(d => d.IsVirtual).Select(d => d.SlotIndex).ToHashSet();
            if (actual.SetEquals(slots) && visible.SetEquals(slots)) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("요청한 모니터 활성 상태가 실제 Windows 상태와 일치하지 않습니다.");
    }

    // 어댑터·타깃 ID로 모니터 경로를 읽습니다. MTT 표식과 UID로 가상 모니터를 구분합니다.
    private static string GetTargetDevicePath(Luid adapterId, uint targetId)
    {
        var info = new DisplayConfigTargetDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = 2, // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
                Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                AdapterId = adapterId,
                Id = targetId
            },
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty
        };
        int result = DisplayConfigGetDeviceInfo(ref info);
        if (result != 0) throw new Win32Exception(result, "모니터 타깃 식별자 조회에 실패했습니다.");
        return info.MonitorDevicePath ?? string.Empty;
    }

    // 소스 ID를 GDI 이름으로 바꿔 화면 경로와 모니터 열거 결과를 연결합니다.
    private static string GetSourceDeviceName(Luid adapterId, uint sourceId)
    {
        var info = new DisplayConfigSourceDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = 1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                AdapterId = adapterId,
                Id = sourceId
            },
            ViewGdiDeviceName = string.Empty
        };
        int result = DisplayConfigGetDeviceInfo(ref info);
        if (result != 0) throw new Win32Exception(result, "화면 소스 이름 조회에 실패했습니다.");
        return info.ViewGdiDeviceName ?? string.Empty;
    }

    // Windows가 유효한 확장 화면 구성을 계산하고 적용하도록 요청합니다.
    public void Extend()
    {
        // SDC_APPLY | SDC_TOPOLOGY_EXTEND: Windows chooses a valid extended topology.
        int result = SetDisplayConfig(0, 0, 0, 0, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        if (result != 0) throw new Win32Exception(result);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var targets = GetVirtualMonitorTargets();
            var active = GetDisplays().Where(d => d.IsVirtual).Select(d => d.SlotIndex).ToHashSet();
            if (targets.All(t => t.IsActive && active.Contains(t.SlotIndex))) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("Windows 확장 모드의 실제 활성 화면을 확인하지 못했습니다.");
    }

    // 활성 장치 하드웨어 ID가 MONITOR\MTT로 시작하는지 확인합니다.
    private static bool IsMttMonitor(string name)
    {
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(name, index, ref device, 0)) return false;
            if ((device.StateFlags & 1) != 0 &&
                device.DeviceId.StartsWith(@"MONITOR\MTT", StringComparison.OrdinalIgnoreCase)) return true;
        }
    }

    // GDI 화면에 연결된 활성 모니터의 인터페이스 ID를 읽습니다. 없으면 빈 문자열입니다.
    private static string GetMonitorId(string name)
    {
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(name, index, ref device, 1)) return "";
            if ((device.StateFlags & 1) != 0) return device.DeviceId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid : IEquatable<Luid>
    {
        public uint LowPart;
        public int HighPart;
        // LUID의 상위·하위 값을 비교합니다. 객체 오버로드는 타입도 확인하여 어댑터를 값 기준으로 구분합니다.
        public bool Equals(Luid other) => LowPart == other.LowPart && HighPart == other.HighPart;
        // LUID의 상위·하위 값을 비교합니다. 객체 오버로드는 타입도 확인하여 어댑터를 값 기준으로 구분합니다.
        public override bool Equals(object? obj) => obj is Luid other && Equals(other);
        // LUID의 두 값을 해시로 합쳐 사전과 집합에서 값 기준 조회를 지원합니다.
        public override int GetHashCode() => HashCode.Combine(LowPart, HighPart);
        public static bool operator ==(Luid left, Luid right) => left.Equals(right);
        public static bool operator !=(Luid left, Luid right) => !left.Equals(right);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfig2DRegion { public uint Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        public ulong PixelRate;
        public DisplayConfigRational HSyncFreq;
        public DisplayConfigRational VSyncFreq;
        public DisplayConfig2DRegion ActiveSize;
        public DisplayConfig2DRegion TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public DisplayConfigVideoSignalInfo ModeData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    // GDI 화면 및 하위 모니터 장치 정보를 열거합니다.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string name, uint index, ref DisplayDevice device, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Bounds, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    // 모니터마다 호출되는 콜백 형식입니다. true를 반환하면 열거를 계속합니다.
    private delegate bool MonitorEnum(nint monitor, nint dc, ref Rect bounds, nint data);
    // 현재 데스크톱 모니터를 지정 콜백으로 전달합니다.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);
    // 경계 좌표·작업 영역·주 모니터 여부와 GDI 이름을 읽습니다.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    // 화면 구성을 적용합니다. 포인터 오버로드는 자동 확장, 배열 오버로드는 선택 경로 적용에 사용합니다.
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint paths, nint pathInfo, uint modes, nint modeInfo, uint flags);
    // 화면 구성을 적용합니다. 포인터 오버로드는 자동 확장, 배열 오버로드는 선택 경로 적용에 사용합니다.
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint paths, [In] DisplayConfigPathInfo[] pathInfo, uint modes, [In] DisplayConfigModeInfo[] modeInfo, uint flags);
    // 화면 경로·모드 배열에 필요한 항목 수를 조회합니다.
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    // 배열에 화면 경로와 모드를 채웁니다. 플래그로 활성 경로만 또는 전체 경로를 선택합니다.
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathArray, ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, nint currentTopologyId);
    // 헤더 타입에 따라 타깃 모니터 경로 또는 소스 GDI 이름을 읽습니다.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);
    // 헤더 타입에 따라 타깃 모니터 경로 또는 소스 GDI 이름을 읽습니다.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);
}
