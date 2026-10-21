namespace VirtualMonitor;

// ConfigurationChanges 트랜잭션 안에서 호출합니다. 실패 시 파일·별칭·장치 복구는 해당 트랜잭션이 담당합니다.
internal sealed class MonitorConfiguration : IMonitorConfiguration
{
    private readonly IMonitorConfigurationStore store;
    private readonly IMonitorConfigurationDisplay display;

    internal MonitorConfiguration(IMonitorConfigurationStore store, IMonitorConfigurationDisplay display)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.display = display ?? throw new ArgumentNullException(nameof(display));
    }

    // XML 개수를 늘리고 어댑터를 재시작합니다. 새 타깃 열거를 기다린 뒤 기존 활성 슬롯과 새 슬롯을 켭니다.
    public async Task<int> CreateAsync()
    {
        var adapters = display.GetAdapters();
        if (adapters.Count != 1) throw new InvalidOperationException("모니터 추가는 MttVDD 장치가 하나 설치된 구성에서 지원합니다.");
        var device = adapters[0];
        var activeBefore = display.GetActiveSlots().ToHashSet();
        var originals = store.ReadFiles();
        var identities = MonitorIdentity.Read(originals.First().Value);
        identities.Add(Guid.NewGuid().ToString("N"));
        var updates = originals.ToDictionary(pair => pair.Key, pair => MonitorIdentity.Write(MonitorSettings.AddMonitor(pair.Value), identities));
        int newCount = MonitorSettings.ReadCount(updates.First().Value);
        activeBefore.Add(newCount - 1);

        store.WriteFiles(updates);
        await RestartAdapterAsync(device, newCount, activeBefore);
        return newCount;
    }

    private async Task RestartAdapterAsync(MonitorDevice device, int count, IReadOnlySet<int> activeSlots)
    {
        if (device.Started) await display.SetAdapterEnabledAsync(device.InstanceId, false);
        await display.SetAdapterEnabledAsync(device.InstanceId, true);
        await display.WaitForTargetsAsync(count);
        await display.ApplyAndVerifyAsync(activeSlots);
    }

    private async Task DisableAdapterAsync(MonitorDevice device)
    {
        if (!device.Started) return;
        // 화면 분리에 실패해도 어댑터 비활성화는 계속 시도합니다.
        try { display.DeactivateMonitors(); } catch { }
        await display.SetAdapterEnabledAsync(device.InstanceId, false);
    }

    // 지정 슬롯을 제거하고 뒤쪽 별칭과 활성 슬롯 번호를 당깁니다. 남은 활성 화면이 없으면 어댑터도 끕니다.
    public async Task<int> RemoveAsync(int? slotIndex = null)
    {
        var adapters = display.GetAdapters();
        if (adapters.Count != 1) throw new InvalidOperationException("모니터 삭제는 MttVDD 장치가 하나 설치된 구성에서 지원합니다.");
        var device = adapters[0];
        var originals = store.ReadFiles();
        int oldCount = MonitorSettings.ReadCount(originals.First().Value);
        if (oldCount < 1) throw new InvalidOperationException("삭제할 가상 모니터가 없습니다.");

        if (slotIndex.HasValue && (slotIndex < 0 || slotIndex >= oldCount))
            throw new InvalidOperationException("선택한 슬롯이 더 이상 존재하지 않습니다.");
        int removedSlot = slotIndex ?? oldCount - 1;
        var remainingActive = new HashSet<int>();
        foreach (int activeSlot in display.GetActiveSlots())
        {
            if (activeSlot < removedSlot) remainingActive.Add(activeSlot);
            else if (activeSlot > removedSlot) remainingActive.Add(activeSlot - 1);
        }

        var identities = MonitorIdentity.Read(originals.First().Value);
        identities.RemoveAt(removedSlot);
        var updates = originals.ToDictionary(pair => pair.Key, pair => MonitorIdentity.Write(MonitorSettings.RemoveMonitor(pair.Value), identities));
        int newCount = MonitorSettings.ReadCount(updates.First().Value);

        store.WriteFiles(updates);
        store.RemoveSlotAndShift(removedSlot, oldCount);
        if (newCount == 0 || remainingActive.Count == 0)
            await DisableAdapterAsync(device);
        else if (device.Started)
            await RestartAdapterAsync(device, newCount, remainingActive);
        return newCount;
    }

    // 앱의 모니터 개수를 0으로 만들고 슬롯 별칭을 정리한 뒤 어댑터를 끕니다.
    public async Task<int> RemoveAllAsync()
    {
        var adapters = display.GetAdapters();
        if (adapters.Count != 1) throw new InvalidOperationException("모니터 삭제는 MttVDD 장치가 하나 설치된 구성에서 지원합니다.");
        var device = adapters[0];
        var originals = store.ReadFiles();
        int oldCount = MonitorSettings.ReadCount(originals.First().Value);
        if (oldCount < 1) throw new InvalidOperationException("삭제할 가상 모니터가 없습니다.");

        var updates = originals.ToDictionary(pair => pair.Key, pair => MonitorIdentity.Write(MonitorSettings.RemoveAllMonitors(pair.Value), []));
        store.WriteFiles(updates);
        store.ClearAllSlots();
        await DisableAdapterAsync(device);
        return 0;
    }
}
