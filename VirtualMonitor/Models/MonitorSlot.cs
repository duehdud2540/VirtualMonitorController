using System.Text.RegularExpressions;

namespace VirtualMonitor;

internal static class MonitorSlot
{
    private static readonly Regex Pattern = new(@"(?:&UID|^MttVDD#UID)(\d+)(?:#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static bool TryParse(string? monitorId, out int slot)
    {
        slot = -1;
        if (string.IsNullOrWhiteSpace(monitorId)) return false;
        var match = Pattern.Match(monitorId);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int uid) || uid < 256) return false;
        slot = uid - 256;
        return true;
    }
}
