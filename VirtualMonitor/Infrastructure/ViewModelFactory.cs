using System.Windows.Threading;

namespace VirtualMonitor;

// ViewModels receive shared services; capture workers are created per preview.
internal sealed class ViewModelFactory(
    IMonitorStateReader state,
    IConfigurationChanges changes,
    IMonitorOperations operations,
    IPreviewOwnership ownership,
    IAppLog log,
    Func<Dispatcher, IPreviewCaptureService> createCapture)
{
    internal MainViewModel CreateMain(Dispatcher dispatcher) => new(dispatcher, state, changes, operations, ownership, log);
    internal MonitorManagerViewModel CreateMonitorManager() => new(state, changes, operations, log);
    internal PreviewViewModel CreatePreview(Dispatcher dispatcher) => new(dispatcher, createCapture(dispatcher), state, ownership, log);
}
