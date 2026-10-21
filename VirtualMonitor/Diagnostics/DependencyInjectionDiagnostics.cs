using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace VirtualMonitor;

// 전체 서비스 그래프를 메모리 구현으로 검증합니다. 실제 파일·장치·창은 사용하지 않습니다.
internal static class DependencyInjectionDiagnostics
{
    internal static async Task RunAsync(Dispatcher dispatcher)
    {
        var files = new MemoryFiles();
        const string xml = "<vdd_settings><monitors><count>2</count></monitors></vdd_settings>";
        files.Write("settings", xml);
        var settings = new MonitorSettings(files, ["settings"]);
        var names = new MonitorNames(files, files, "names");
        names.Save("MttVDD#UID256", "first");
        var hardware = new Hardware();
        var catalog = new MonitorCatalog(hardware, hardware, settings, names);
        var snapshot = catalog.Read();
        Require(snapshot.Count == 2 && snapshot.Devices[0].Alias == "first", "catalog uses injected settings and aliases");
        Require(snapshot.Devices[0].StableId == "legacy-0", "catalog preserves identity projection");

        var otherFiles = new MemoryFiles();
        otherFiles.Write("settings", MonitorSettings.AddMonitor(xml));
        Require(new MonitorSettings(otherFiles, ["settings"]).Count == 3 && settings.Count == 2, "settings instances are isolated");

        var log = new AppLog(files, "logs");
        var journals = new RecoveryJournalStore(files, files, "journals");
        var source = new CountingSource();
        var cache = new MonitorStateStore(source);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => cache.ReadAsync()));
        Require(source.Calls == 1, "shared cache coalesces reads");
        var changes = new ConfigurationChanges(cache, log,
            () => new ConfigurationRecovery(settings, names, hardware, hardware, files, journals));
        bool resumed = false;
        changes.Register(new Participant(() =>
        {
            Require(settings.Count == 2 && names.GetAlias(null, 0) == "first", "rollback precedes participant resume");
            resumed = true;
        }));
        try
        {
            await changes.RunAsync("injected-failure", () =>
            {
                files.Write("settings", MonitorSettings.AddMonitor(xml));
                names.Save("MttVDD#UID256", "changed");
                throw new InvalidOperationException("simulated device failure");
            });
            throw new Exception("mutation failure was lost");
        }
        catch (AggregateException ex)
        {
            Require(ex.Flatten().InnerExceptions.Count == 1, "original failure reaches caller");
        }
        Require(resumed && !changes.IsBusy && hardware.TopologyRestored, "transaction restores and releases shared gate");
        Require(journals.PendingJournals().Count == 0, "restored journal is not pending");
        Require(files.Contents.Values.Any(value => value.Contains("\"restored\"")), "recovery journal uses injected storage");
        await cache.ReadAsync();
        Require(source.Calls == 2, "transaction invalidates injected cache");
        Require(files.Contents.Keys.Any(path => path.StartsWith("logs")), "log uses injected filesystem");

        var serial = new ConfigurationChanges(cache, log, () => null);
        int active = 0, maximum = 0;
        async Task Work()
        {
            int current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            await Task.Delay(10);
            Interlocked.Decrement(ref active);
        }
        await Task.WhenAll(serial.RunAsync("a", Work), serial.RunAsync("b", Work));
        Require(maximum == 1, "shared coordinator serializes mutations");

        var blocked = new BlockingSource();
        var versioned = new MonitorStateStore(blocked);
        var read = versioned.ReadAsync();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        versioned.Invalidate();
        blocked.Release.TrySetResult();
        Require((await read).Count == 2, "invalidation rejects in-flight stale snapshot");

        var ownership = new PreviewOwnership();
        var independentOwnership = new PreviewOwnership();
        object owner = new();
        ownership.TryAcquire("test", owner);
        Require(!independentOwnership.IsOwned("test"), "ownership is scoped, not global");
        ownership.Release("test", new object());
        Require(ownership.IsOwned("test"), "wrong owner cannot release reservation");
        ownership.Release("test", owner);

        var state = new SnapshotReader(snapshot);
        var operations = new Operations();
        using var main = new MainViewModel(dispatcher, state, serial, operations, ownership, log);
        await main.RefreshAsync();
        Require(main.Devices.Count == 2, "main receives injected snapshot");
        main.SelectedDevice = main.Devices[0] with { Started = true };
        var capture = new Capture();
        var preview = new PreviewViewModel(dispatcher, capture, state, ownership, log);
        var display = new MonitorDisplay("fake", "", 0, 0, 1, 1, true) { StableId = "legacy-0", SlotIndex = 0 };
        preview.SelectedDisplay = display;
        Require(capture.Selected == display && !main.PreviewCommand.CanExecute(null), "preview and main share injected ownership");
        var secondCapture = new Capture();
        using var secondPreview = new PreviewViewModel(dispatcher, secondCapture, state, ownership, log);
        secondPreview.SelectedDisplay = display;
        Require(secondPreview.SelectedDisplay == null, "duplicate preview cannot acquire shared ownership");
        preview.Dispose();
        Require(capture.Disposed && main.PreviewCommand.CanExecute(null), "preview disposal releases capture and ownership");
        using var manager = new MonitorManagerViewModel(state, serial, operations, log);
        Require(manager.Monitors.Count == 2 && manager.CanEdit, "manager uses the injected snapshot and coordinator");
        Console.WriteLine("Dependency injection diagnostics passed (storage, recovery, cache, transactions, ownership, three ViewModels).");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class MemoryFiles : IFileSystem, IAtomicFile
    {
        internal readonly Dictionary<string, string> Contents = new();
        public bool FileExists(string path) => Contents.ContainsKey(path);
        public string ReadAllText(string path) => Contents[path];
        public void CreateDirectory(string path) { }
        public void AppendAllText(string path, string text) => Contents[path] = Contents.GetValueOrDefault(path, "") + text;
        public bool DirectoryExists(string path) => Contents.Keys.Any(key => Path.GetDirectoryName(key) == path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern) => Contents.Keys
            .Where(key => Path.GetDirectoryName(key) == path && key.EndsWith(pattern.TrimStart('*'))).ToArray();
        public void Write(string path, string text, Action<string>? beforeCommit = null)
        {
            beforeCommit?.Invoke(path);
            Contents[path] = text;
        }
    }

    private sealed class Hardware : IWindowsDisplayAdapter, IDisplayService
    {
        internal bool TopologyRestored;
        public IReadOnlyList<MonitorDevice> GetDevices() => [new("adapter", 1, true, 0)];
        public Task SetEnabledAsync(string instanceId, bool enabled) => Task.CompletedTask;
        public IReadOnlyList<MonitorDisplay> GetDisplays(IReadOnlyList<DisplayService.VirtualMonitorTarget>? targetSnapshot = null) => [];
        public IReadOnlyList<DisplayService.VirtualMonitorTarget> GetVirtualMonitorTargets() => [];
        public void SetVirtualMonitorsActive(IReadOnlySet<int> activeSlots) { }
        public Action CaptureTopology() => () => TopologyRestored = true;
        public Task WaitForTargetsAsync(int count) => Task.CompletedTask;
        public Task ApplyAndVerifyAsync(IReadOnlySet<int> slots) => Task.CompletedTask;
        public void Extend() { }
    }

    private sealed class Participant(Action resume) : IConfigurationParticipant
    {
        public Task PrepareAsync() => Task.CompletedTask;
        public Task ResumeAsync() { resume(); return Task.CompletedTask; }
    }

    private sealed class CountingSource : IMonitorSnapshotSource
    {
        internal int Calls;
        public MonitorSnapshot Read() { Interlocked.Increment(ref Calls); return new([], [], [], [], 0); }
    }

    private sealed class BlockingSource : IMonitorSnapshotSource
    {
        private int calls;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MonitorSnapshot Read()
        {
            int count = Interlocked.Increment(ref calls);
            if (count == 1) { Entered.TrySetResult(); Release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            return new([], [], [], [], count);
        }
    }

    private sealed class SnapshotReader(MonitorSnapshot snapshot) : IMonitorStateReader
    {
        public Task<MonitorSnapshot> ReadAsync() => Task.FromResult(snapshot);
    }

    private sealed class Operations : IMonitorOperations
    {
        public IReadOnlyList<MonitorDevice> GetDevices() => [];
        public IReadOnlyList<MonitorDisplay> GetManagedMonitors() => [];
        public Task SetEnabledAsync(string instanceId, bool enabled) => Task.CompletedTask;
        public void Extend() { }
        public Task<int> CreateAsync() => Task.FromResult(0);
        public Task<int> RemoveAsync(int slotIndex) => Task.FromResult(0);
        public Task<int> RemoveAllAsync() => Task.FromResult(0);
        public void SaveName(string monitorId, string alias) { }
        public IReadOnlyList<MonitorModes.Mode> GetModes(string name) => [];
        public MonitorModes.Mode? GetCurrentMode(string name) => null;
        public void ApplyMode(string monitorId, MonitorModes.Mode mode) { }
    }

    private sealed class Capture : IPreviewCaptureService
    {
        internal MonitorDisplay? Selected;
        internal bool Disposed;
        public Func<ICaptureSession, bool>? WorkerOutput { get; set; }
        public Action? WorkerTick { get; set; }
        public event Action? OutputFailed { add { } remove { } }
        public event Action<MonitorDisplay, ICaptureSession>? FrameReady { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public long SubmittedFrameCount => 0;
        public double DispatcherWaitMilliseconds => 0;
        public double CaptureMilliseconds => 0;
        public double PresentationMilliseconds => 0;
        public long CaptureRequests => 0;
        public void SelectDisplay(MonitorDisplay? display) => Selected = display;
        public void SetPaused(bool value) { }
        public void Start() { }
        public Task PauseAndReleaseAsync() => Task.CompletedTask;
        public Task StopAsync() { Dispose(); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
}
