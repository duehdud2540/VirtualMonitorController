namespace VirtualMonitor;

internal record MonitorDevice(string InstanceId, uint Node, bool Started, uint Problem)
    {
        public string StableId { get; init; } = "";
        public string AdapterInstanceId { get; init; } = "";
        public int SlotIndex { get; init; } = -1;
        public string MonitorId { get; init; } = "";
        public string Alias { get; init; } = "";
        public string GdiDeviceName { get; init; } = "";
        public string DisplayName => SlotIndex < 0
            ? InstanceId
            : string.IsNullOrEmpty(Alias)
                ? $"가상 모니터 {SlotIndex + 1}"
                : $"{Alias} (가상 모니터 {SlotIndex + 1})";
        public string Label => SlotIndex < 0
            ? $"{InstanceId} · {(Started ? "켜짐" : Problem == 22 ? "꺼짐" : $"확인 필요 (코드 {Problem})")}"
            : $"{DisplayName}{(Started && !string.IsNullOrEmpty(GdiDeviceName) ? $" · {GdiDeviceName}" : "")} · {(Started ? "켜짐" : Problem is 0 or 22 ? "꺼짐" : $"확인 필요 (코드 {Problem})")}";
    }
