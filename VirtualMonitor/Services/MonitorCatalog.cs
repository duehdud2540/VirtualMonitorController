namespace VirtualMonitor;

// Application projection: native readers have no dependency on settings, lifecycle or this catalog.
internal sealed class MonitorCatalog : IMonitorCatalog
{
    private readonly IWindowsDisplayAdapter adapterService;
    private readonly IDisplayService displayService;
    private readonly IMonitorSettings settingsStore;
    private readonly IMonitorNames namesStore;

    internal MonitorCatalog(IWindowsDisplayAdapter adapterService, IDisplayService displayService, IMonitorSettings settingsStore, IMonitorNames namesStore)
    {
        this.adapterService = adapterService ?? throw new ArgumentNullException(nameof(adapterService));
        this.displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.namesStore = namesStore ?? throw new ArgumentNullException(nameof(namesStore));
    }

    public MonitorSnapshot Read()
    {
        var adapters = adapterService.GetDevices();
        if (adapters.Count > 1) throw new InvalidOperationException("여러 MttVDD 어댑터는 지원하지 않습니다. 단일 어댑터를 사용하세요.");
        var targets = adapters.Any(a => a.Started) ? displayService.GetVirtualMonitorTargets() : [];
        var raw = displayService.GetDisplays(targets);
        if (adapters.Count == 0) return new(adapters, [], raw, [], 0);
        var settings = settingsStore.ReadFiles().First().Value;
        int count = MonitorSettings.ReadCount(settings);
        var ids = MonitorIdentity.Read(settings);
        var names = namesStore.Load();
        var adapter = adapters[0];
        var displays = raw.Select(d => d.IsVirtual ? d with
        {
            StableId = MonitorIdentity.At(ids, d.SlotIndex),
            Alias = namesStore.GetAlias(d.MonitorId, d.SlotIndex, names)
        } : d).ToArray();
        // A transient target count is an observation, never a new configured monitor.
        var managed = new List<MonitorDisplay>();
        var devices = new List<MonitorDevice>();
        for (int slot = 0; slot < count; slot++)
        {
            var active = displays.FirstOrDefault(d => d.IsVirtual && d.SlotIndex == slot);
            var target = targets.FirstOrDefault(t => t.SlotIndex == slot);
            string monitorId = active?.MonitorId ?? target?.MonitorId ?? namesStore.GetMonitorIdForSlot(slot, names);
            string alias = namesStore.GetAlias(monitorId, slot, names);
            bool started = adapter.Started && active != null && target?.IsActive == true;
            string stableId = MonitorIdentity.At(ids, slot);
            var display = active ?? new MonitorDisplay($"가상 모니터 {slot + 1}", "꺼진 모니터", 0, 0, 0, 0, true);
            managed.Add(display with { StableId = stableId, SlotIndex = slot, MonitorId = monitorId, Alias = alias, IsActive = started });
            devices.Add(new($"{adapter.InstanceId}#{slot}", adapter.Node, started,
                started ? 0 : adapter.Problem is not (0 or 22) ? adapter.Problem : 22)
            {
                StableId = stableId, AdapterInstanceId = adapter.InstanceId, SlotIndex = slot,
                MonitorId = monitorId, Alias = alias, GdiDeviceName = active?.Name ?? ""
            });
        }
        return new(adapters, devices, displays, managed, count);
    }

    public IReadOnlyList<MonitorDevice> GetDevices() => Read().Devices;
    public IReadOnlyList<MonitorDisplay> GetDisplays() => Read().Displays;
    public IReadOnlyList<MonitorDisplay> GetManagedVirtualMonitors() => Read().Managed;
}
