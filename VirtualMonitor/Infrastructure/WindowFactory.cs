using System.Windows.Threading;

namespace VirtualMonitor;

internal interface IApplicationLifetime
{
    bool TrayEnabled { get; }
    bool IsExiting { get; }
}

internal interface IWindowFactory
{
    MainWindow CreateMain();
    MonitorManagerWindow CreateMonitorManager();
    PreviewWindow CreatePreview(MonitorDevice? device = null);
}

// 창 생성은 이 조립 지점에만 둡니다. View는 App이나 전역 서비스에 접근하지 않습니다.
internal sealed class WindowFactory(Dispatcher dispatcher, ViewModelFactory viewModels,
    IConfigurationChanges changes, IApplicationLifetime lifetime, IAppLog log) : IWindowFactory
{
    public MainWindow CreateMain() => new(viewModels.CreateMain(dispatcher), this, lifetime, log);
    public MonitorManagerWindow CreateMonitorManager() => new(viewModels.CreateMonitorManager());
    public PreviewWindow CreatePreview(MonitorDevice? device = null) => new(viewModels.CreatePreview(dispatcher),
        changes, device);
}
