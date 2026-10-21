namespace VirtualMonitor;

internal record MonitorDisplay(string Name, string Details, int Left, int Top, int Width, int Height, bool IsVirtual)
    {
        public string StableId { get; init; } = "";
        public int SlotIndex { get; init; } = -1;
        public bool IsActive { get; init; } = true;
        public string MonitorId { get; init; } = "";
        public string Alias { get; init; } = "";
        public string DisplayName => string.IsNullOrEmpty(Alias) ? Name : $"{Alias} ({Name})";
    }
