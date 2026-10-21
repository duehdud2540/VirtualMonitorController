using System.IO;
using System.Text.Json;

namespace VirtualMonitor;

internal interface IConfigurationRecovery
{
    Task RestoreAsync();
    void Complete();
}

// Captured before any mutation; used by every entry point, including mode changes and extension.
internal sealed class ConfigurationRecovery : IConfigurationRecovery
{
    private readonly IMonitorNames namesStore;
    private readonly IWindowsDisplayAdapter adapterService;
    private readonly IDisplayService displayService;
    private readonly IAtomicFile atomicFile;
    private readonly IRecoveryJournalStore journals;
    private readonly Dictionary<string, string> files;
    private readonly Dictionary<string, string> names;
    private readonly IReadOnlyList<MonitorDevice> adapters;
    private readonly HashSet<int> active;
    private readonly Action restoreTopology;
    private readonly string journal = Guid.NewGuid().ToString("N");
    private string state = "pending";

    internal ConfigurationRecovery(IMonitorSettings settings, IMonitorNames namesStore,
        IWindowsDisplayAdapter adapterService, IDisplayService displayService,
        IAtomicFile atomicFile, IRecoveryJournalStore journals)
    {
        this.namesStore = namesStore;
        this.adapterService = adapterService;
        this.displayService = displayService;
        this.atomicFile = atomicFile;
        this.journals = journals;
        files = settings.ReadFiles();
        names = namesStore.Load();
        adapters = adapterService.GetDevices();
        active = displayService.GetVirtualMonitorTargets().Where(t => t.IsActive).Select(t => t.SlotIndex).ToHashSet();
        restoreTopology = displayService.CaptureTopology();
        SaveJournal();
    }

    private void SaveJournal() => journals.Write(journal, JsonSerializer.Serialize(new
    {
        state, time = DateTimeOffset.UtcNow, files, names,
        adapters = adapters.Select(a => new { a.InstanceId, a.Started }), activeSlots = active,
        note = "pending/recovery-required이면 자동 복원이 완료되지 않은 기록입니다. files는 변경 전 XML, names는 변경 전 별칭입니다."
    }, new JsonSerializerOptions { WriteIndented = true }));

    public void Complete() { state = "complete"; SaveJournal(); }

    public async Task RestoreAsync()
    {
        var failures = new List<Exception>();
        foreach (var file in files)
            try { atomicFile.Write(file.Key, file.Value); } catch (Exception ex) { failures.Add(ex); }
        try { namesStore.Restore(names); } catch (Exception ex) { failures.Add(ex); }
        foreach (var adapter in adapters)
        {
            try
            {
                await adapterService.SetEnabledAsync(adapter.InstanceId, false);
                if (adapter.Started)
                {
                    await adapterService.SetEnabledAsync(adapter.InstanceId, true);
                    await displayService.WaitForTargetsAsync(Math.Max(1, MonitorSettings.ReadCount(files.First().Value)));
                }
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (adapters.Any(a => a.Started))
            try { await displayService.ApplyAndVerifyAsync(active); } catch (Exception ex) { failures.Add(ex); }
        try { restoreTopology(); } catch (Exception ex) { failures.Add(ex); }
        state = failures.Count == 0 ? "restored" : "recovery-required";
        try { SaveJournal(); } catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException($"일부 복구 실패. 복구 기록: {journal}", failures);
    }

}
