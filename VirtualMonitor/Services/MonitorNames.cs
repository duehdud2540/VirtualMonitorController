using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VirtualMonitor;

internal sealed class MonitorNames : IMonitorNames
{
    private readonly IFileSystem fileSystem;
    private readonly IAtomicFile atomicFile;
    private readonly string filePath;

    internal MonitorNames(IFileSystem fileSystem, IAtomicFile atomicFile, string filePath)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.atomicFile = atomicFile ?? throw new ArgumentNullException(nameof(atomicFile));
        this.filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    private static readonly Regex UidPattern = new(@"^(.*&UID)(\d+)(#.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FallbackSlotPattern = new(@"^MttVDD#UID(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);


    // ID와 별칭을 JSON에서 읽습니다. 파일이 없으면 빈 사전을 반환하고, 잘못된 JSON은 오류로 전달합니다.
    public Dictionary<string, string> Load(string? path = null)
    {
        path ??= filePath;
        if (!fileSystem.FileExists(path)) return new(StringComparer.OrdinalIgnoreCase);
        return new(JsonSerializer.Deserialize<Dictionary<string, string>>(fileSystem.ReadAllText(path))
            ?? throw new InvalidDataException("모니터 이름 파일을 읽을 수 없습니다."), StringComparer.OrdinalIgnoreCase);
    }

    // 모니터 ID의 UID에서 256을 빼 0부터 시작하는 슬롯을 구합니다. 꺼진 화면용 대체 ID도 지원합니다.
    internal static bool TryGetSlotIndex(string? monitorId, out int slotIndex) => MonitorSlot.TryParse(monitorId, out slotIndex);

    // 기존 ID를 찾거나 저장된 ID 형식으로 슬롯 식별자를 구성합니다. 참고할 ID가 없으면 MttVDD#UID 형식을 씁니다.
    public string GetMonitorIdForSlot(int slotIndex, Dictionary<string, string>? names = null)
    {
        names ??= Load();
        foreach (string key in names.Keys)
        {
            if (TryGetSlotIndex(key, out int existingSlot) && existingSlot == slotIndex)
                return key;
        }
        foreach (string key in names.Keys)
        {
            var match = UidPattern.Match(key);
            if (match.Success)
                return $"{match.Groups[1].Value}{256 + slotIndex}{match.Groups[3].Value}";
        }
        return $"MttVDD#UID{256 + slotIndex}";
    }

    // ID의 정확한 일치를 우선하고, 없으면 같은 슬롯의 별칭을 찾아 장치 경로 변경에도 이름을 이어 씁니다.
    public string GetAlias(string? monitorId, int slotIndex = -1, Dictionary<string, string>? names = null)
    {
        names ??= Load();
        if (!string.IsNullOrWhiteSpace(monitorId) && names.TryGetValue(monitorId, out string? exact))
            return exact;
        if (slotIndex < 0 && !TryGetSlotIndex(monitorId, out slotIndex))
            return "";
        foreach (var pair in names)
        {
            if (TryGetSlotIndex(pair.Key, out int keySlot) && keySlot == slotIndex)
                return pair.Value;
        }
        return "";
    }

    // 이름 길이·제어 문자를 검증하고 저장합니다. 같은 슬롯의 다른 ID 항목을 정리하여 별칭 중복을 막습니다.
    public void Save(string monitorId, string name, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(monitorId)) throw new InvalidOperationException("모니터 식별자가 없습니다.");
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
            throw new ArgumentException("이름은 제어 문자 없이 1~64자로 입력하세요.");
        path ??= filePath;
        var names = Load(path);
        if (TryGetSlotIndex(monitorId, out int slotIndex))
        {
            foreach (string key in names.Keys.Where(k => !k.Equals(monitorId, StringComparison.OrdinalIgnoreCase) &&
                                                         TryGetSlotIndex(k, out int s) && s == slotIndex).ToList())
            {
                names.Remove(key);
            }
        }
        names[monitorId] = name;
        WriteNames(path, names);
    }

    // 삭제 슬롯의 이름을 빼고 뒤쪽 별칭의 슬롯 번호를 하나 줄여 UID 재배치에 맞춥니다.
    public void RemoveSlotAndShift(int removedSlotIndex, int totalCountBeforeRemove, string? path = null)
    {
        path ??= filePath;
        if (!fileSystem.FileExists(path)) return;
        var names = Load(path);
        var slotAliases = new Dictionary<int, string>();
        for (int s = 0; s < totalCountBeforeRemove; s++)
        {
            string alias = GetAlias(null, s, names);
            if (!string.IsNullOrEmpty(alias)) slotAliases[s] = alias;
        }
        var sampleKey = names.Keys.FirstOrDefault(k => UidPattern.IsMatch(k));
        foreach (string key in names.Keys.Where(k => TryGetSlotIndex(k, out _)).ToList())
            names.Remove(key);
        foreach (var pair in slotAliases.OrderBy(p => p.Key))
        {
            if (pair.Key == removedSlotIndex) continue;
            int newSlot = pair.Key > removedSlotIndex ? pair.Key - 1 : pair.Key;
            string newKey = sampleKey != null
                ? UidPattern.Replace(sampleKey, $"${{1}}{256 + newSlot}${{3}}")
                : $"MttVDD#UID{256 + newSlot}";
            names[newKey] = pair.Value;
        }
        WriteNames(path, names);
    }

    // 슬롯으로 식별되는 별칭만 사전에서 제거하고 JSON을 다시 저장합니다.
    public void ClearAllSlots(string? path = null)
    {
        path ??= filePath;
        if (!fileSystem.FileExists(path)) return;
        var names = Load(path);
        foreach (string key in names.Keys.Where(k => TryGetSlotIndex(k, out _)).ToList())
            names.Remove(key);
        WriteNames(path, names);
    }

    // 폴더를 준비하고 기존 파일의 고유 백업을 만든 뒤 들여쓰기한 JSON을 저장합니다.
    public void Restore(Dictionary<string, string> names) => WriteNames(filePath, names);

    private void WriteNames(string path, Dictionary<string, string> names)
    {
        fileSystem.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Keep the preceding version; do not overwrite an unrelated or malformed file.
        atomicFile.Write(path, JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }));
    }
}
