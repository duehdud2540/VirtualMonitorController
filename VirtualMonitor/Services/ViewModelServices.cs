namespace VirtualMonitor;

internal interface IMonitorStateReader
{
    Task<MonitorSnapshot> ReadAsync();
}

internal interface IConfigurationChanges
{
    bool IsBusy { get; }
    event Action? StateChanged;
    Task RunAsync(string operation, Func<Task> action);
    void Register(IConfigurationParticipant participant);
    void Unregister(IConfigurationParticipant participant);
}

internal interface IPreviewOwnershipReader
{
    event Action? Changed;
    bool IsOwned(string key);
}

internal interface IMonitorOperations
{
    IReadOnlyList<MonitorDevice> GetDevices();
    IReadOnlyList<MonitorDisplay> GetManagedMonitors();
    Task SetEnabledAsync(string instanceId, bool enabled);
    void Extend();
    Task<int> CreateAsync();
    Task<int> RemoveAsync(int slotIndex);
    Task<int> RemoveAllAsync();
    void SaveName(string monitorId, string alias);
    IReadOnlyList<MonitorModes.Mode> GetModes(string name);
    MonitorModes.Mode? GetCurrentMode(string name);
    void ApplyMode(string monitorId, MonitorModes.Mode mode);
}

internal sealed class WindowsMonitorOperations : IMonitorOperations
{
    private readonly IMonitorConfiguration configuration;
    private readonly IMonitorCatalog catalog;
    private readonly IVirtualDisplayDevice device;
    private readonly IDisplayService displayService;
    private readonly IMonitorNames names;
    private readonly IMonitorModes modes;

    internal WindowsMonitorOperations(IMonitorConfiguration configuration, IMonitorCatalog catalog, IVirtualDisplayDevice device, IDisplayService displayService, IMonitorNames names, IMonitorModes modes)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        this.names = names ?? throw new ArgumentNullException(nameof(names));
        this.modes = modes ?? throw new ArgumentNullException(nameof(modes));
    }

    public IReadOnlyList<MonitorDevice> GetDevices() => catalog.GetDevices();
    public IReadOnlyList<MonitorDisplay> GetManagedMonitors() => catalog.GetManagedVirtualMonitors();
    public Task SetEnabledAsync(string instanceId, bool enabled) => device.SetEnabledAsync(instanceId, enabled);
    public void Extend() => displayService.Extend();
    public Task<int> CreateAsync() => configuration.CreateAsync();
    public Task<int> RemoveAsync(int slotIndex) => configuration.RemoveAsync(slotIndex);
    public Task<int> RemoveAllAsync() => configuration.RemoveAllAsync();
    public void SaveName(string monitorId, string alias) => names.Save(monitorId, alias);
    public IReadOnlyList<MonitorModes.Mode> GetModes(string name) => modes.Get(name);
    public MonitorModes.Mode? GetCurrentMode(string name) => modes.Current(name);
    public void ApplyMode(string monitorId, MonitorModes.Mode mode) => modes.Apply(monitorId, mode);
}
