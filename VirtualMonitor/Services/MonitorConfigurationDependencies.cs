namespace VirtualMonitor;

internal interface IMonitorConfiguration
{
    Task<int> CreateAsync();
    Task<int> RemoveAsync(int? slotIndex = null);
    Task<int> RemoveAllAsync();
}

internal interface IMonitorConfigurationStore
{
    IReadOnlyDictionary<string, string> ReadFiles();
    void WriteFiles(IReadOnlyDictionary<string, string> files);
    void RemoveSlotAndShift(int slot, int oldCount);
    void ClearAllSlots();
}

internal interface IMonitorConfigurationDisplay
{
    IReadOnlyList<MonitorDevice> GetAdapters();
    IReadOnlySet<int> GetActiveSlots();
    Task SetAdapterEnabledAsync(string instanceId, bool enabled);
    Task WaitForTargetsAsync(int count);
    Task ApplyAndVerifyAsync(IReadOnlySet<int> slots);
    void DeactivateMonitors();
}

internal sealed class MonitorConfigurationStore : IMonitorConfigurationStore
{
    private readonly IMonitorSettings settings;
    private readonly IMonitorNames names;
    private readonly IAtomicFile atomicFile;

    internal MonitorConfigurationStore(IMonitorSettings settings, IMonitorNames names, IAtomicFile atomicFile)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.names = names ?? throw new ArgumentNullException(nameof(names));
        this.atomicFile = atomicFile ?? throw new ArgumentNullException(nameof(atomicFile));
    }

    public IReadOnlyDictionary<string, string> ReadFiles() => settings.ReadFiles();
    public void WriteFiles(IReadOnlyDictionary<string, string> files)
    {
        foreach (var pair in files) atomicFile.Write(pair.Key, pair.Value);
    }
    public void RemoveSlotAndShift(int slot, int oldCount) => names.RemoveSlotAndShift(slot, oldCount);
    public void ClearAllSlots() => names.ClearAllSlots();
}

internal sealed class WindowsMonitorConfigurationDisplay : IMonitorConfigurationDisplay
{
    private readonly IWindowsDisplayAdapter adapters;
    private readonly IMonitorCatalog catalog;
    private readonly IDisplayService displays;

    internal WindowsMonitorConfigurationDisplay(IWindowsDisplayAdapter adapters, IMonitorCatalog catalog, IDisplayService displays)
    {
        this.adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.displays = displays ?? throw new ArgumentNullException(nameof(displays));
    }

    public IReadOnlyList<MonitorDevice> GetAdapters() => adapters.GetDevices();
    public IReadOnlySet<int> GetActiveSlots() => catalog.GetDevices()
        .Where(device => device.Started).Select(device => device.SlotIndex).ToHashSet();
    public Task SetAdapterEnabledAsync(string instanceId, bool enabled) => adapters.SetEnabledAsync(instanceId, enabled);
    public Task WaitForTargetsAsync(int count) => displays.WaitForTargetsAsync(count);
    public Task ApplyAndVerifyAsync(IReadOnlySet<int> slots) => displays.ApplyAndVerifyAsync(slots);
    public void DeactivateMonitors() => displays.SetVirtualMonitorsActive(new HashSet<int>());
}
