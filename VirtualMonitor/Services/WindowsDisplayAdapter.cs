using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Device = VirtualMonitor.MonitorDevice;

namespace VirtualMonitor;

internal sealed class WindowsDisplayAdapter : IWindowsDisplayAdapter
{
    public IReadOnlyList<Device> GetDevices()
    {
        var result = new List<Device>();
        Guid displayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
        nint set = SetupDiGetClassDevs(ref displayClass, null, 0, 2); // Present devices only.
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var ids = new byte[8192];
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, 1, out _, ids, (uint)ids.Length, out _))
                    continue;
                if (!Encoding.Unicode.GetString(ids).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Any(id => id.Equals(@"Root\MttVDD", StringComparison.OrdinalIgnoreCase) ||
                               id.Equals("MttVDD", StringComparison.OrdinalIgnoreCase))) continue;
                var instance = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref info, instance, instance.Capacity, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Check(CM_Get_DevNode_Status(out uint status, out uint problem, info.Node, 0));
                string id = instance.ToString();
                result.Add(new Device(id, info.Node, (status & 8) != 0 && (status & 0x400) == 0, problem)
                {
                    AdapterInstanceId = id
                });
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
    public async Task SetEnabledAsync(string instanceId, bool enabled)
    {
        await Task.Run(async () =>
        {
            // Re-enumerate and validate the hardware ID immediately before modifying a device.
            var device = GetDevices().SingleOrDefault(d => d.InstanceId == instanceId)
                ?? throw new InvalidOperationException("선택한 MttVDD 장치를 찾을 수 없습니다. 새로 고침해 주세요.");
            Check(enabled ? CM_Enable_DevNode(device.Node, 0) : CM_Disable_DevNode(device.Node, 8)); // CM_DISABLE_PERSIST
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var current = GetDevices().SingleOrDefault(d => d.InstanceId == instanceId);
                if (current != null && (enabled ? current.Started : current.Problem == 22)) return;
                await Task.Delay(250);
            }
            throw new InvalidOperationException("변경 요청 후 장치 상태를 확인하지 못했습니다. 새로 고침하거나 Windows에서 재시작 필요 여부를 확인해 주세요.");
        });
    }
    private static void Check(uint result)
    {
        if (result == 0) return;
        uint error = CM_MapCrToWin32Err(result, 31);
        throw new Win32Exception((int)error, $"장치 제어 실패 (CONFIGRET 0x{result:X}): {new Win32Exception((int)error).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint Node; public nuint Reserved; }
    // 장치 클래스 정보 목록 핸들을 얻습니다. 사용 후 목록 해제가 필요합니다.
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    // 인덱스로 장치를 조회합니다. 오류 259는 열거할 장치가 더 없다는 뜻입니다.
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo info);
    // 하드웨어 ID 등 장치 레지스트리 속성을 읽어 MttVDD 필터링에 사용합니다.
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    // 장치 노드의 고유 인스턴스 문자열을 얻습니다.
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref DeviceInfo info, StringBuilder buffer, int size, out uint required);
    // SetupAPI 조회 목록을 해제합니다. 실제 장치를 삭제하는 함수가 아닙니다.
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    // 장치 노드의 상태 플래그와 문제 코드를 읽습니다.
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    // Windows에 장치 활성화를 요청합니다. 완료는 호출부에서 별도로 확인합니다.
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Enable_DevNode(uint node, uint flags);
    // Windows에 장치 비활성화를 요청합니다. 호출부는 지속 비활성화 플래그를 사용합니다.
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Disable_DevNode(uint node, uint flags);
    // Configuration Manager 오류를 Win32 오류로 변환합니다.
    [DllImport("cfgmgr32.dll")] private static extern uint CM_MapCrToWin32Err(uint result, uint fallback);
}
