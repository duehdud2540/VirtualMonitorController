using System.IO;
using System.Xml.Linq;

namespace VirtualMonitor;

internal static class MonitorIdentity
{
    private const string Prefix = "virtualmonitor:ids=";
    internal static List<string> Read(string xml)
    {
        int count = MonitorSettings.ReadCount(xml);
        var comments = XDocument.Parse(xml).Root!.Element("monitors")!.Nodes().OfType<XComment>()
            .Where(c => c.Value.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        if (comments.Length == 0) return Enumerable.Range(0, count).Select(i => $"legacy-{i}").ToList();
        if (comments.Length != 1) throw new InvalidDataException("중복 모니터 식별자 설정입니다.");
        var ids = comments[0].Value[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (ids.Count != count || ids.Distinct(StringComparer.Ordinal).Count() != count || ids.Any(id => !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new InvalidDataException("모니터 개수와 영구 식별자가 일치하지 않습니다.");
        return ids;
    }

    internal static string Write(string xml, IEnumerable<string> ids)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var monitors = document.Root!.Element("monitors")!;
        foreach (var comment in monitors.Nodes().OfType<XComment>().Where(c => c.Value.StartsWith(Prefix, StringComparison.Ordinal)).ToArray()) comment.Remove();
        monitors.Add(new XComment(Prefix + string.Join(',', ids)));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    internal static string At(IReadOnlyList<string> ids, int slot) => slot >= 0 && slot < ids.Count ? ids[slot] : "";
}
