using System.Diagnostics;

namespace VirtualMonitor;

internal interface IConfigurationParticipant
{
    Task PrepareAsync();
    Task ResumeAsync();
}

// All UI mutations use one transaction boundary. Participants are registered on the dispatcher.
internal sealed class ConfigurationChanges(IMonitorStateStore stateStore, IAppLog log, Func<IConfigurationRecovery?> captureRecovery) : IConfigurationChanges
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<IConfigurationParticipant> participants = new();
    public bool IsBusy { get; private set; }
    public event Action? StateChanged;
    private void NotifyState()
    {
        if (StateChanged == null) return;
        foreach (Action subscriber in StateChanged.GetInvocationList())
            try { subscriber(); } catch (Exception ex) { log.Write("configuration.observer", error: ex); }
    }
    public void Register(IConfigurationParticipant participant) => participants.Add(participant);
    public void Unregister(IConfigurationParticipant participant) => participants.Remove(participant);

    public async Task RunAsync(string operation, Func<Task> action)
    {
        await gate.WaitAsync();
        var prepared = new List<IConfigurationParticipant>();
        var failures = new List<Exception>();
        var watch = Stopwatch.StartNew();
        IConfigurationRecovery? recovery = null;
        bool mutationStarted = false;
        try
        {
            IsBusy = true;
            NotifyState();
            log.Write(operation + ".start");
            foreach (var participant in participants.ToArray())
            {
                prepared.Add(participant); // Resume even a partially prepared participant.
                await participant.PrepareAsync();
            }
            recovery = await Task.Run(captureRecovery);
            mutationStarted = true;
            await Task.Run(action);
            if (recovery != null) await Task.Run(recovery.Complete);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            if (mutationStarted && recovery != null)
                try { await Task.Run(recovery.RestoreAsync); }
                catch (Exception restoreError) { failures.Add(restoreError); }
        }
        finally
        {
            stateStore.Invalidate();
            foreach (var participant in prepared.AsEnumerable().Reverse())
                try { await participant.ResumeAsync(); } catch (Exception ex) { failures.Add(ex); }
            IsBusy = false;
            gate.Release();
            NotifyState();
            log.Write(operation + ".finish", new { milliseconds = watch.Elapsed.TotalMilliseconds, success = failures.Count == 0 },
                failures.Count == 0 ? null : new AggregateException(failures));
        }
        if (failures.Count != 0) throw new AggregateException("구성 변경 또는 복구 확인이 필요합니다.", failures);
    }
}
