using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VirtualMonitor;

internal sealed class ArchitectureDiagnostics(IAppLog log, IAtomicFile atomicFile, MonitorManagerDiagnostics manager, IPreviewOwnership ownership)
{
    private int checks;
    private void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    internal async Task RunAsync(Dispatcher dispatcher)
    {
        checks = 0;
        string folder = Path.Combine(AppContext.BaseDirectory, "DiagnosticArtifacts", "architecture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        const string three = "<vdd_settings><monitors><count>3</count></monitors></vdd_settings>";
        var ids = MonitorIdentity.Read(three);
        string removed = MonitorIdentity.Write(MonitorSettings.RemoveMonitor(three), [ids[0], ids[2]]);
        Require(MonitorIdentity.Read(removed).SequenceEqual(new[] { ids[0], ids[2] }), "middle removal preserves identity");
        string newId = Guid.NewGuid().ToString("N");
        string added = MonitorIdentity.Write(MonitorSettings.AddMonitor(removed), [ids[0], ids[2], newId]);
        Require(!MonitorIdentity.Read(added).Contains(ids[1]), "new monitor cannot inherit deleted identity");
        var original = new MonitorDisplay("old", "", 0, 0, 1, 1, true) { StableId = ids[2], SlotIndex = 2 };
        var moved = original with { Name = "new", SlotIndex = 1 };
        Require(PreviewViewModel.GetDisplayKey(original) == PreviewViewModel.GetDisplayKey(moved), "preview key survives slot and GDI changes");
        object owner = new(), other = new();
        string key = PreviewViewModel.GetDisplayKey(original);
        Require(ownership.TryAcquire(key, owner) && !ownership.TryAcquire(key, other), "single monitor owner");
        ownership.Release(key, other);
        Require(ownership.IsOwned(key), "wrong owner cannot release reservation");
        ownership.Release(key, owner);
        Require(!ownership.IsOwned(key), "closed preview releases reservation");

        string file = Path.Combine(folder, "settings.xml");
        atomicFile.Write(file, three);
        try { atomicFile.Write(file, added, _ => throw new IOException("injected commit failure")); }
        catch (IOException) { }
        Require(File.ReadAllText(file) == three, "failed atomic commit preserves original");
        Require(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "failed commit cleans temporary file");
        atomicFile.Write(file, added);
        Require(File.ReadAllText(file) == added && Directory.EnumerateFiles(folder, "*.bak").Any(), "atomic replacement retains backup");

        var events = new List<string>();
        var recovery = new FakeRecovery(events);
        var changes = new ConfigurationChanges(new MonitorStateStore(new FakeSource()), log, () => recovery);
        var first = new Participant(events, "first");
        var second = new Participant(events, "second") { FailPrepare = true };
        changes.Register(first); changes.Register(second);
        bool actionCalled = false;
        try { await changes.RunAsync("test.prepare", () => { actionCalled = true; return Task.CompletedTask; }); }
        catch (AggregateException) { }
        Require(!actionCalled && events.SequenceEqual(new[] { "first.prepare", "second.prepare", "second.resume", "first.resume" }), "partial preparation resumes all attempted participants");
        Require(!changes.IsBusy, "failed preparation releases change gate");
        events.Clear(); second.FailPrepare = false;
        try { await changes.RunAsync("test.mutation", () => throw new IOException("injected device failure")); }
        catch (AggregateException) { }
        Require(events.IndexOf("restore") >= 0 && events.IndexOf("restore") < events.IndexOf("second.resume"), "rollback precedes capture resume");
        events.Clear(); recovery.FailRestore = true; second.FailResume = true;
        int errors = 0;
        try { await changes.RunAsync("test.recovery", () => throw new IOException("write failed")); }
        catch (AggregateException ex) { errors = ex.Flatten().InnerExceptions.Count; }
        Require(errors == 3 && events.Contains("first.resume") && !changes.IsBusy, "mutation, restore and resume failures remain visible");

        var serial = new ConfigurationChanges(new MonitorStateStore(new FakeSource()), log, () => null);
        int inFlight = 0, maximum = 0;
        async Task Work()
        {
            int current = Interlocked.Increment(ref inFlight);
            maximum = Math.Max(maximum, current);
            await Task.Delay(20);
            Interlocked.Decrement(ref inFlight);
        }
        await Task.WhenAll(serial.RunAsync("test.concurrent-a", Work), serial.RunAsync("test.concurrent-b", Work));
        Require(maximum == 1, "concurrent mutations are serialized");

        var source = new FakeSource();
        var store = new MonitorStateStore(source);
        var reads = Enumerable.Range(0, 16).Select(_ => store.ReadAsync()).ToArray();
        await Task.WhenAll(reads);
        Require(source.Calls == 1, "sixteen consumers share one snapshot read");
        store.Invalidate(); source.Fail = true;
        bool failed = false;
        try { await store.ReadAsync(); } catch (IOException) { failed = true; }
        Require(failed, "read failure is not an empty monitor list");
        source.Fail = false;
        await store.ReadAsync();
        Require(source.Calls == 3, "failed snapshot is not cached");

        var blocked = new BlockingSource();
        var versioned = new MonitorStateStore(blocked);
        var oldRead = versioned.ReadAsync();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        versioned.Invalidate(); blocked.Release.Set();
        Require((await oldRead).Count == 2, "invalidated in-flight snapshot cannot escape");

        await new CaptureLifecycleDiagnostics(ui => new PreviewCaptureService(ui, _ => throw new InvalidOperationException("Idle test must not capture"), log), log).RunAsync(dispatcher);
        Require(true, "capture stop completion without live devices");
        DirectXFrameStatisticsDiagnostics.Run();
        Require(true, "frame statistics exclude redraw and failed submissions");
        manager.Run();
        Require(true, "settings, aliases, slot migration and manager XAML regressions");
        foreach (var dll in new[] { "VirtualMonitor.Capture.dll", "VirtualMonitor.Preview.dll" })
        {
            nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, dll));
            try
            {
                string[] exports = dll.Contains("Capture") ? ["CaptureCreate", "CaptureRead", "CaptureReadTexture", "CaptureDestroy"]
                    : ["PreviewCreateOnDevice", "PreviewDrawTexture", "PreviewDraw", "PreviewDestroy"];
                Require(exports.All(name => NativeLibrary.TryGetExport(library, name, out _)), dll + " ABI exports");
                if (dll.Contains("Preview"))
                {
                    var test = Marshal.GetDelegateForFunctionPointer<NativeSelfTest>(NativeLibrary.GetExport(library, "PreviewSelfTest"));
                    Marshal.ThrowExceptionForHR(test());
                    Require(true, "native shared-device shader pixels and resize (offscreen WARP)");
                }
            }
            finally { NativeLibrary.Free(library); }
        }
        Console.WriteLine($"Architecture diagnostics: {checks} checks passed. No live display changes. Artifacts: {folder}");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeSelfTest();

    private sealed class Participant(List<string> events, string name) : IConfigurationParticipant
    {
        internal bool FailPrepare, FailResume;
        public Task PrepareAsync() { events.Add(name + ".prepare"); return FailPrepare ? Task.FromException(new IOException("prepare")) : Task.CompletedTask; }
        public Task ResumeAsync() { events.Add(name + ".resume"); return FailResume ? Task.FromException(new IOException("resume")) : Task.CompletedTask; }
    }
    private sealed class FakeRecovery(List<string> events) : IConfigurationRecovery
    {
        internal bool FailRestore;
        public void Complete() => events.Add("complete");
        public Task RestoreAsync() { events.Add("restore"); return FailRestore ? Task.FromException(new IOException("restore")) : Task.CompletedTask; }
    }
    private sealed class FakeSource : IMonitorSnapshotSource
    {
        internal int Calls;
        internal bool Fail;
        public MonitorSnapshot Read() { Interlocked.Increment(ref Calls); if (Fail) throw new IOException("read"); return new([], [], [], [], 0); }
    }
    private sealed class BlockingSource : IMonitorSnapshotSource
    {
        private int calls;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public MonitorSnapshot Read()
        {
            int count = Interlocked.Increment(ref calls);
            if (count == 1) { Entered.SetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            return new([], [], [], [], count);
        }
    }
}
