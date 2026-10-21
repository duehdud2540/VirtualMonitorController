using System.IO;
using System.Xml.Linq;

namespace VirtualMonitor;

internal sealed class MonitorManagerDiagnostics(IMonitorNames namesStore, IWindowFactory windows)
{
    // 메모리 XML과 별도 진단 폴더의 JSON으로 개수·별칭·슬롯 이동·입력 거부를 검사합니다. 관리 창 생성도 확인하며 실제 장치 설정은 변경하지 않습니다.
    internal void Run()
    {
        const string xml = "<vdd_settings><!--keep--><monitors><count>1</count></monitors><gpu><friendlyname>custom</friendlyname></gpu><global><g_refresh_rate>144</g_refresh_rate></global></vdd_settings>";
        string changed = MonitorSettings.AddMonitor(xml);
        Require(MonitorSettings.ReadCount(changed) == 2, "Monitor count increment");
        var root = XDocument.Parse(changed).Root!;
        Require(root.Element("gpu")!.Value == "custom" && root.Element("global")!.Value == "144" && changed.Contains("<!--keep-->"), "Unrelated settings preserved");
        string decremented = MonitorSettings.RemoveMonitor(changed);
        Require(MonitorSettings.ReadCount(decremented) == 1, "Monitor count decrement to 1");
        string emptied = MonitorSettings.RemoveMonitor(decremented);
        Require(MonitorSettings.ReadCount(emptied) == 0, "Monitor count decrement to 0");
        Require(XDocument.Parse(emptied).Root!.Element("monitors")!.Element("count")!.Value == "1", "Driver hardware count stays >= 1 when app count is 0");
        Reject(() => MonitorSettings.RemoveMonitor(emptied));
        string readded = MonitorSettings.AddMonitor(emptied);
        Require(MonitorSettings.ReadCount(readded) == 1, "Monitor count increment from 0 to 1");
        string clearedAll = MonitorSettings.RemoveAllMonitors(changed);
        Require(MonitorSettings.ReadCount(clearedAll) == 0, "Remove all monitors sets count to 0");
        Reject(() => MonitorSettings.AddMonitor(xml.Replace("<count>1</count>", "<count>16</count>")));
        Reject(() => MonitorSettings.ReadCount(xml.Replace("<count>1</count>", "<count>0</count>")));
        Reject(() => MonitorSettings.ReadCount("<wrong/>"));

        // Keep diagnostic artifacts in this build folder, never touch live profile/driver settings.
        string path = Path.Combine(AppContext.BaseDirectory, "DiagnosticArtifacts", Guid.NewGuid().ToString("N"), "names.json");
        namesStore.Save("monitor-a", "작업 화면", path);
        namesStore.Save("monitor-b", "참고 화면", path);
        namesStore.Save("MONITOR-A", "수정한 이름", path);
        var names = namesStore.Load(path);
        Require(names.Count == 2 && names["monitor-a"] == "수정한 이름" && names["monitor-b"] == "참고 화면", "Identity-based rename and persistence");

        string slotPath = Path.Combine(AppContext.BaseDirectory, "DiagnosticArtifacts", Guid.NewGuid().ToString("N"), "slot-names.json");
        namesStore.Save(@"\\?\DISPLAY#MTT1337#1&28a6823a&0&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "첫번째", slotPath);
        namesStore.Save(@"\\?\DISPLAY#MTT1337#1&28a6823a&0&UID257#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "두번째", slotPath);
        namesStore.Save(@"\\?\DISPLAY#MTT1337#1&28a6823a&0&UID258#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "세번째", slotPath);
        namesStore.RemoveSlotAndShift(0, 3, slotPath);
        var shifted = namesStore.Load(slotPath);
        Require(namesStore.GetAlias(null, 0, shifted) == "두번째" && namesStore.GetAlias(null, 1, shifted) == "세번째" && string.IsNullOrEmpty(namesStore.GetAlias(null, 2, shifted)), "Slot alias shift on delete");
        namesStore.ClearAllSlots(slotPath);
        Require(namesStore.Load(slotPath).Count == 0, "Clear all slot aliases");

        Reject(() => namesStore.Save("monitor-a", " ", path));
        Reject(() => namesStore.Save("monitor-a", new string('a', 65), path));
        Reject(() => namesStore.Save("", "이름", path));
        File.WriteAllText(path, "invalid json");
        Reject(() => namesStore.Save("monitor-a", "이름", path));
        Require(File.ReadAllText(path) == "invalid json", "Malformed names must not be overwritten");
        var display = new MonitorDisplay("DISPLAY2", "", 0, 0, 1920, 1080, true) { MonitorId = "monitor-a", Alias = "작업 화면" };
        Require(display.DisplayName == "작업 화면 (DISPLAY2)" && display.Name == "DISPLAY2", "Alias does not change capture identity");
        var window = windows.CreateMonitorManager();
        Require(window.IsInitialized && window.DataContext is MonitorManagerViewModel, "Management XAML and view model construction");
        window.Close();
        Console.WriteLine("Monitor manager: all checks passed. Live driver settings and display modes were not changed.");
    }

    // 기대 조건이 맞지 않으면 진단을 실패시킵니다.
    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
    // 잘못된 입력이 예상한 종류의 예외로 거부되는지 검사합니다.
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException) { return; }
        throw new InvalidOperationException("Invalid input was accepted.");
    }
}
