using System.IO;
using System.Xml.Linq;

namespace VirtualMonitor;

internal sealed class MonitorSettings : IMonitorSettings
{
    private readonly IFileSystem fileSystem;
    private readonly IReadOnlyList<string> paths;

    internal MonitorSettings(IFileSystem fileSystem, IReadOnlyList<string> paths)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    private const string EmptyMarker = "virtualmonitor:empty";

    // 설정 XML을 검증하고 개수를 읽습니다. count=1이어도 empty 주석이 있으면 앱에서는 0개로 해석합니다.
    internal static int ReadCount(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var monitors = root?.Element("monitors");
        if (root?.Name != "vdd_settings" || monitors == null || !int.TryParse(monitors.Element("count")?.Value, out int count) || count < 1)
            throw new InvalidDataException("MttVDD 모니터 개수 설정이 올바르지 않습니다.");
        if (monitors.Nodes().OfType<XComment>().Any(c => c.Value.Trim().Equals(EmptyMarker, StringComparison.OrdinalIgnoreCase)))
            return 0;
        return count;
    }

    // 최대 16개 제한을 검사하고 개수를 하나 늘린 XML 문자열을 반환합니다. 이 메서드 자체는 파일을 쓰지 않습니다.
    internal static string AddMonitor(string xml)
    {
        int count = ReadCount(xml);
        if (count >= 16) throw new InvalidOperationException("이 앱에서는 최대 16개까지 추가할 수 있습니다.");
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var monitors = document.Root!.Element("monitors")!;
        RemoveEmptyMarker(monitors);
        monitors.Element("count")!.Value = (count + 1).ToString();
        return document.ToString(SaveOptions.DisableFormatting);
    }

    // 개수를 하나 줄인 XML을 반환합니다. 마지막 모니터는 드라이버용 count=1과 앱용 empty 표시로 표현합니다.
    internal static string RemoveMonitor(string xml)
    {
        int count = ReadCount(xml);
        if (count < 1) throw new InvalidOperationException("삭제할 가상 모니터가 없습니다.");
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var monitors = document.Root!.Element("monitors")!;
        RemoveEmptyMarker(monitors);
        if (count == 1)
        {
            // Keep hardware driver count >= 1 for IddCx while marking 0 configured monitors for the app.
            monitors.Element("count")!.Value = "1";
            monitors.Add(new XComment(EmptyMarker));
        }
        else
        {
            monitors.Element("count")!.Value = (count - 1).ToString();
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    // 다른 설정을 유지하면서 count=1과 empty 주석으로 앱의 0개 상태를 표현합니다.
    internal static string RemoveAllMonitors(string xml)
    {
        int count = ReadCount(xml);
        if (count < 1) throw new InvalidOperationException("삭제할 가상 모니터가 없습니다.");
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var monitors = document.Root!.Element("monitors")!;
        RemoveEmptyMarker(monitors);
        monitors.Element("count")!.Value = "1";
        monitors.Add(new XComment(EmptyMarker));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    // 개수를 다시 기록하기 전에 기존 empty XML 주석만 제거합니다.
    private static void RemoveEmptyMarker(XElement monitors)
    {
        foreach (var comment in monitors.Nodes().OfType<XComment>()
            .Where(c => c.Value.Trim().Equals(EmptyMarker, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            comment.Remove();
        }
    }

    // 설치 위치 후보에서 XML을 읽습니다. 파일이 없거나 여러 파일의 개수가 다르면 오류를 발생시킵니다.
    public Dictionary<string, string> ReadFiles()
    {
        var files = paths.Where(fileSystem.FileExists).ToDictionary(p => p, fileSystem.ReadAllText);
        if (files.Count == 0) throw new FileNotFoundException("설치된 MttVDD 설정 파일이 없습니다. 드라이버 설치를 확인하세요.");
        if (files.Values.Select(ReadCount).Distinct().Count() != 1)
            throw new InvalidDataException("두 MttVDD 설정 파일의 모니터 개수가 다릅니다. 설정을 먼저 일치시켜 주세요.");
        if (files.Values.Select(v => string.Join(',', XDocument.Parse(v).Root!.Element("monitors")!.Nodes()
            .OfType<XComment>().Where(c => c.Value.StartsWith("virtualmonitor:ids=", StringComparison.Ordinal)).Select(c => c.Value))).Distinct().Count() != 1)
            throw new InvalidDataException("두 설정 파일의 모니터 식별자가 다릅니다.");
        return files;
    }

    public int Count => ReadCount(ReadFiles().First().Value);

}
