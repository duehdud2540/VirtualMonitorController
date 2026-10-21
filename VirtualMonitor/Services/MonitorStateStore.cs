namespace VirtualMonitor;

internal sealed record MonitorSnapshot(
    IReadOnlyList<MonitorDevice> Adapters,
    IReadOnlyList<MonitorDevice> Devices,
    IReadOnlyList<MonitorDisplay> Displays,
    IReadOnlyList<MonitorDisplay> Managed,
    int Count);

internal interface IMonitorSnapshotSource { MonitorSnapshot Read(); }

// Coalesces all windows' polling into one background read. Failed reads are never cached as empty data.
internal sealed class MonitorStateStore(IMonitorSnapshotSource source) : IMonitorStateStore
{
    private readonly object gate = new();
    private Task<MonitorSnapshot>? pending;
    private DateTime expires;
    private long generation;
    public void Invalidate() { lock (gate) { generation++; pending = null; expires = default; } }
    public async Task<MonitorSnapshot> ReadAsync()
    {
        while (true)
        {
            Task<MonitorSnapshot> task;
            long version;
            lock (gate)
            {
                version = generation;
                if (pending == null || (pending.IsCompleted && (pending.IsFaulted || DateTime.UtcNow >= expires)))
                {
                    expires = DateTime.UtcNow.AddSeconds(2);
                    pending = Task.Run(source.Read);
                }
                task = pending;
            }
            try
            {
                var result = await task.ConfigureAwait(false);
                lock (gate) { if (version == generation) return result; }
            }
            catch
            {
                lock (gate) { if (version == generation) throw; }
            }
        }
    }
}
