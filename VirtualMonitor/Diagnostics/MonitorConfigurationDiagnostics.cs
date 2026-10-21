namespace VirtualMonitor;

// 메모리 저장소와 가짜 장치만 사용합니다. 파일 및 실제 모니터 구성은 변경하지 않습니다.
internal static class MonitorConfigurationDiagnostics
{
    internal static async Task RunAsync()
    {
        var fake = new Dependencies(2, true, 0);
        var service = new MonitorConfiguration(fake, fake);
        Require(await service.CreateAsync() == 3, "add count");
        Require(MonitorIdentity.Read(fake.Xml).Take(2).SequenceEqual(new[] { "legacy-0", "legacy-1" }), "preserve identities");
        Require(fake.Active.SetEquals(new[] { 0, 2 }), "preserve active slots and enable new slot");
        Require(fake.Calls.SequenceEqual(new[] { "write", "disable", "enable", "wait:3", "apply" }), "add order");

        fake = new Dependencies(3, true, 0, 2);
        service = new(fake, fake);
        Require(await service.RemoveAsync(1) == 2, "remove count");
        Require(MonitorIdentity.Read(fake.Xml).SequenceEqual(new[] { "legacy-0", "legacy-2" }), "remove selected identity");
        Require(fake.Active.SetEquals(new[] { 0, 1 }), "shift active slots");
        Require(fake.Calls.SequenceEqual(new[] { "write", "shift:1:3", "disable", "enable", "wait:2", "apply" }), "remove order");

        fake = new Dependencies(1, true, 0) { FailDetach = true };
        service = new(fake, fake);
        Require(await service.RemoveAsync() == 0, "remove last");
        Require(MonitorSettings.ReadCount(fake.Xml) == 0 && fake.Calls.Last() == "disable", "disable despite detach failure");

        fake = new Dependencies(2, false);
        service = new(fake, fake);
        Require(await service.RemoveAllAsync() == 0, "remove all");
        Require(fake.Calls.SequenceEqual(new[] { "write", "clear" }), "offline remove all does not enable adapter");
        Require(MonitorIdentity.Read(fake.Xml).Count == 0, "clear identities");

        fake = new Dependencies(2, true, 0);
        service = new(fake, fake);
        await RejectAsync(() => service.RemoveAsync(2));
        Require(fake.Calls.Count == 0, "invalid slot has no side effects");

        fake = new Dependencies(2, true, 0) { FailWrite = true };
        service = new(fake, fake);
        await RejectAsync(() => service.CreateAsync());
        Require(fake.Calls.SequenceEqual(new[] { "write" }), "write failure prevents device changes");

        fake = new Dependencies(2, true, 0) { FailWait = true };
        service = new(fake, fake);
        await RejectAsync(() => service.CreateAsync());
        Require(!fake.Calls.Contains("apply"), "device failure propagates to transaction owner");
        Console.WriteLine("MonitorConfiguration diagnostics passed (7 scenarios).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RejectAsync(Func<Task<int>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected failure was not propagated.");
    }

    private sealed class Dependencies(int count, bool started, params int[] activeSlots)
        : IMonitorConfigurationStore, IMonitorConfigurationDisplay
    {
        internal string Xml = $"<vdd_settings><monitors><count>{count}</count></monitors></vdd_settings>";
        internal readonly List<string> Calls = [];
        internal HashSet<int> Active = activeSlots.ToHashSet();
        internal bool FailDetach, FailWrite, FailWait;
        public IReadOnlyDictionary<string, string> ReadFiles() => new Dictionary<string, string> { ["memory"] = Xml };
        public void WriteFiles(IReadOnlyDictionary<string, string> files)
        {
            Calls.Add("write");
            if (FailWrite) throw new InvalidOperationException("write failed");
            Xml = files["memory"];
        }
        public void RemoveSlotAndShift(int slot, int oldCount) => Calls.Add($"shift:{slot}:{oldCount}");
        public void ClearAllSlots() => Calls.Add("clear");
        public IReadOnlyList<MonitorDevice> GetAdapters() => [new("adapter", 0, started, 0)];
        public IReadOnlySet<int> GetActiveSlots() => Active;
        public Task SetAdapterEnabledAsync(string instanceId, bool enabled)
        {
            Calls.Add(enabled ? "enable" : "disable");
            return Task.CompletedTask;
        }
        public Task WaitForTargetsAsync(int targetCount)
        {
            Calls.Add($"wait:{targetCount}");
            if (FailWait) throw new InvalidOperationException("wait failed");
            return Task.CompletedTask;
        }
        public Task ApplyAndVerifyAsync(IReadOnlySet<int> slots)
        {
            Calls.Add("apply");
            Active = slots.ToHashSet();
            return Task.CompletedTask;
        }
        public void DeactivateMonitors()
        {
            Calls.Add("detach");
            if (FailDetach) throw new InvalidOperationException("detach failed");
        }
    }
}
