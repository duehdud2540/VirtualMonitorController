namespace VirtualMonitor;

internal interface IAppLog
{
    void Write(string operation, object? detail = null, Exception? error = null);
}

internal interface IAtomicFile
{
    void Write(string path, string text, Action<string>? beforeCommit = null);
}

internal interface IFileSystem
{
    bool FileExists(string path);
    string ReadAllText(string path);
    void CreateDirectory(string path);
    void AppendAllText(string path, string text);
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateFiles(string path, string pattern);
}

internal interface IMonitorSettings
{
    Dictionary<string, string> ReadFiles();
    int Count { get; }
}

internal interface IMonitorNames
{
    Dictionary<string, string> Load(string? path = null);
    string GetMonitorIdForSlot(int slotIndex, Dictionary<string, string>? names = null);
    string GetAlias(string? monitorId, int slotIndex = -1, Dictionary<string, string>? names = null);
    void Save(string monitorId, string name, string? path = null);
    void RemoveSlotAndShift(int removedSlotIndex, int totalCountBeforeRemove, string? path = null);
    void ClearAllSlots(string? path = null);
    void Restore(Dictionary<string, string> names);
}

internal interface IWindowsDisplayAdapter
{
    IReadOnlyList<MonitorDevice> GetDevices();
    Task SetEnabledAsync(string instanceId, bool enabled);
}

internal interface IDisplayService
{
    IReadOnlyList<MonitorDisplay> GetDisplays(IReadOnlyList<DisplayService.VirtualMonitorTarget>? targetSnapshot = null);
    IReadOnlyList<DisplayService.VirtualMonitorTarget> GetVirtualMonitorTargets();
    void SetVirtualMonitorsActive(IReadOnlySet<int> activeSlots);
    Action CaptureTopology();
    Task WaitForTargetsAsync(int count);
    Task ApplyAndVerifyAsync(IReadOnlySet<int> slots);
    void Extend();
}

internal interface IMonitorCatalog : IMonitorSnapshotSource
{
    IReadOnlyList<MonitorDevice> GetDevices();
    IReadOnlyList<MonitorDisplay> GetDisplays();
    IReadOnlyList<MonitorDisplay> GetManagedVirtualMonitors();
}

internal interface IVirtualDisplayDevice
{
    Task SetEnabledAsync(string instanceId, bool enabled);
}

internal interface IMonitorModes
{
    IReadOnlyList<MonitorModes.Mode> Get(string name);
    MonitorModes.Mode? Current(string name);
    void Apply(string monitorId, MonitorModes.Mode selected);
}

internal interface IMonitorStateStore : IMonitorStateReader
{
    void Invalidate();
}

internal interface IPreviewOwnership : IPreviewOwnershipReader
{
    bool IsOwned(string key, object? except);
    bool TryAcquire(string key, object owner);
    void Release(string key, object owner);
}
