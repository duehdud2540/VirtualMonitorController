using System.IO;
using System.Windows.Threading;

namespace VirtualMonitor;

// 앱의 유일한 서비스 조립 지점. 생성만 수행하며 파일 조회나 장치 변경은 하지 않습니다.
internal sealed class ApplicationServices
{
    internal IWindowFactory Windows { get; }
    internal IMonitorCatalog Catalog { get; }
    internal IAppLog Log { get; }
    internal IRecoveryJournalStore Journals { get; }
    internal IMonitorNames Names { get; }
    internal IAtomicFile AtomicFile { get; }
    internal Func<MonitorDisplay, ICaptureSession> CreateCaptureSession { get; }

    internal ArchitectureDiagnostics CreateArchitectureDiagnostics()
    {
        var log = new AppLog(new PhysicalFileSystem(), Path.Combine(AppContext.BaseDirectory, "DiagnosticArtifacts", "Logs"));
        return new(log, AtomicFile, new MonitorManagerDiagnostics(Names, Windows), new PreviewOwnership());
    }

    internal ApplicationServices(Dispatcher dispatcher, IApplicationLifetime lifetime, bool useWpfPreview)
    {
        var files = new PhysicalFileSystem();
        AtomicFile = new AtomicFile();
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualMonitor");
        Log = new AppLog(files, Path.Combine(profile, "Logs"));
        var settings = new MonitorSettings(files, [@"C:\VirtualDisplayDriver\vdd_settings.xml", @"C:\IddSampleDriver\vdd_settings.xml"]);
        Names = new MonitorNames(files, AtomicFile, Path.Combine(profile, "monitor-names.json"));
        var adapters = new WindowsDisplayAdapter();
        var displays = new DisplayService();
        Catalog = new MonitorCatalog(adapters, displays, settings, Names);
        var state = new MonitorStateStore(Catalog);
        Journals = new RecoveryJournalStore(files, AtomicFile, Path.Combine(profile, "Recovery"));
        var changes = new ConfigurationChanges(state, Log,
            () => new ConfigurationRecovery(settings, Names, adapters, displays, AtomicFile, Journals));
        var ownership = new PreviewOwnership();
        var configuration = new MonitorConfiguration(new MonitorConfigurationStore(settings, Names, AtomicFile),
            new WindowsMonitorConfigurationDisplay(adapters, Catalog, displays));
        var operations = new WindowsMonitorOperations(configuration, Catalog,
            new VirtualDisplayDevice(adapters, displays, Catalog), displays, Names, new MonitorModes(Catalog));
        CreateCaptureSession = display => new ScreenCapture(display);
        var viewModels = new ViewModelFactory(state, changes, operations, ownership, Log,
            ui => new PreviewCaptureService(ui, CreateCaptureSession, Log));
        Windows = new WindowFactory(dispatcher, viewModels, changes, lifetime, Log, useWpfPreview);
    }
}
