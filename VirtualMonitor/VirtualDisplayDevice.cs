namespace VirtualMonitor;

internal sealed class VirtualDisplayDevice : IVirtualDisplayDevice
{
    private readonly IWindowsDisplayAdapter adapterService;
    private readonly IDisplayService displayService;
    private readonly IMonitorCatalog catalog;

    internal VirtualDisplayDevice(IWindowsDisplayAdapter adapterService, IDisplayService displayService, IMonitorCatalog catalog)
    {
        this.adapterService = adapterService ?? throw new ArgumentNullException(nameof(adapterService));
        this.displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    // 식별자에서 슬롯을 구해 다른 슬롯의 상태를 유지하며 선택 화면을 켜거나 끕니다. 마지막 화면을 끄면 어댑터도 끄고 결과를 반복 조회합니다.
    public async Task SetEnabledAsync(string instanceId, bool enabled)
    {
        int hashIndex = instanceId.LastIndexOf('#');
        if (hashIndex <= 0 || !int.TryParse(instanceId[(hashIndex + 1)..], out int slotIndex) || slotIndex < 0)
        {
            await adapterService.SetEnabledAsync(instanceId, enabled);
            return;
        }

        string adapterInstanceId = instanceId[..hashIndex];
        await Task.Run(async () =>
        {
            var adapter = adapterService.GetDevices().SingleOrDefault(d => d.InstanceId == adapterInstanceId)
                ?? throw new InvalidOperationException("선택한 MttVDD 장치를 찾을 수 없습니다. 새로 고침해 주세요.");

            var currentMonitors = catalog.GetDevices();
            var desiredActiveSlots = currentMonitors
                .Where(m => m.Started)
                .Select(m => m.SlotIndex)
                .ToHashSet();

            if (enabled)
            {
                desiredActiveSlots.Add(slotIndex);
                if (!adapter.Started)
                {
                    await adapterService.SetEnabledAsync(adapter.InstanceId, true);
                    for (int i = 0; i < 20; i++)
                    {
                        if (displayService.GetVirtualMonitorTargets().Count > slotIndex) break;
                        await Task.Delay(250);
                    }
                }
                await displayService.ApplyAndVerifyAsync(desiredActiveSlots);
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    var current = catalog.GetDevices().FirstOrDefault(d => d.SlotIndex == slotIndex);
                    if (current is { Started: true }) return;
                    await Task.Delay(250);
                }
                throw new InvalidOperationException("가상 모니터 활성화 상태를 확인하지 못했습니다. '확장 모드 적용' 또는 '새로 고침'을 눌러 주세요.");
            }
            else
            {
                desiredActiveSlots.Remove(slotIndex);
                if (!adapter.Started) return;
                if (desiredActiveSlots.Count == 0)
                {
                    try { displayService.SetVirtualMonitorsActive(desiredActiveSlots); }
                    catch { /* Adapter will be disabled below */ }
                    await adapterService.SetEnabledAsync(adapter.InstanceId, false);
                    return;
                }
                await displayService.ApplyAndVerifyAsync(desiredActiveSlots);
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    var current = catalog.GetDevices().FirstOrDefault(d => d.SlotIndex == slotIndex);
                    if (current is { Started: false }) return;
                    await Task.Delay(250);
                }
                throw new InvalidOperationException("가상 모니터 비활성화 상태를 확인하지 못했습니다. 새로 고침해 주세요.");
            }
        });
    }

}
