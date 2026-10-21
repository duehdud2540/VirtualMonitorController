using System.Configuration;
using System.Data;
using System.Windows;

namespace VirtualMonitor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application, IApplicationLifetime
    {
        private ApplicationServices services = null!;
        bool IApplicationLifetime.TrayEnabled => TrayEnabled;
        bool IApplicationLifetime.IsExiting => IsExiting;

        private Mutex? instanceMutex;
        private bool ownsInstance;
        private System.Windows.Forms.NotifyIcon? tray;
        private System.Windows.Forms.ContextMenuStrip? trayMenu;
        internal bool TrayEnabled => tray != null;
        internal bool IsExiting { get; private set; }

        // 트레이 메뉴와 아이콘을 구성하여 창 복원·미리보기·종료를 연결합니다. 종료 전 진행 작업을 확인합니다.
        private void InitializeTray(MainWindow main)
        {
            trayMenu = new System.Windows.Forms.ContextMenuStrip();
            trayMenu.Items.Add("메인 화면 열기", null, (_, _) => main.ShowFromTray());
            trayMenu.Items.Add("가상 화면 보기", null, (_, _) => main.ShowPreviewFromTray());
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            trayMenu.Items.Add("종료", null, async (_, _) =>
            {
                if (!main.CanExit()) return;
                IsExiting = true;
                await Task.WhenAll(Windows.OfType<PreviewWindow>().Select(w => w.StopAsync()));
                Shutdown();
            });
            tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "VirtualMonitor · 더블클릭: 메인 화면 / 우클릭: 종료",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            tray.DoubleClick += (_, _) => main.ShowFromTray();
            SessionEnding += (_, _) => IsExiting = true;
        }

        // 앱 종료 시 트레이 아이콘과 메뉴의 네이티브 자원을 정리합니다.
        protected override void OnExit(ExitEventArgs e)
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            trayMenu?.Dispose();
            if (ownsInstance) instanceMutex?.ReleaseMutex();
            instanceMutex?.Dispose();
            base.OnExit(e);
        }

        // 캡처 수명 진단을 실행하고 성공 0·실패 1로 앱을 종료합니다.
        private async void DiagnoseCaptureLifecycle()
        {
            try { await new CaptureLifecycleDiagnostics(ui => new PreviewCaptureService(ui, services.CreateCaptureSession, services.Log), services.Log).RunAsync(Dispatcher); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
        }

        // 최소화·복원·닫기와 미리보기 독립 수명을 검사하고 JSON 결과를 출력합니다.
        private async void DiagnoseTray(MainWindow main)
        {
            int result = 0;
            try
            {
                main.ShowPreviewFromTray();
                await Task.Delay(500);
                var view = Windows.OfType<PreviewWindow>().Single();
                main.WindowState = WindowState.Minimized;
                await Task.Delay(250);
                bool minimizeIndependent = !main.IsVisible && view.IsVisible && view.WindowState != WindowState.Minimized && view.Owner == null;
                main.ShowFromTray();
                bool restored = main.IsVisible && main.WindowState != WindowState.Minimized;
                main.Close();
                bool closeToTray = !main.IsVisible && view.IsVisible && TrayEnabled;
                view.Close();
                bool traySurvives = TrayEnabled && !main.IsVisible;
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { minimizeIndependent, restored, closeToTray, traySurvives }));
                if (!minimizeIndependent || !restored || !closeToTray || !traySurvives) result = 1;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { IsExiting = true; Shutdown(result); }
        }

        // 명령행 옵션에 따라 진단 또는 일반 실행을 선택합니다. 일반 실행은 트레이와 메인 창을 만들고 명시적 종료 방식으로 유지합니다.
        protected override void OnStartup(StartupEventArgs e)
        {
            services = new ApplicationServices(Dispatcher, this);
            if (e.Args.Contains("--diagnose-dependency-injection"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DiagnoseDependencyInjection();
                return;
            }
            if (e.Args.Contains("--diagnose-monitor-configuration"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DiagnoseMonitorConfiguration();
                return;
            }
            if (e.Args.Contains("--diagnose-architecture"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DiagnoseArchitecture();
                return;
            }
            if (e.Args.Contains("--diagnose-capture-lifecycle"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DiagnoseCaptureLifecycle();
                return;
            }
            if (e.Args.Contains("--diagnose-monitor-manager"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { new MonitorManagerDiagnostics(services.Names, services.Windows).Run(); Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
                return;
            }
            if (e.Args.Contains("--diagnose-directx-statistics"))
            {
                try { DirectXFrameStatisticsDiagnostics.Run(); Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
                return;
            }
            if (e.Args.Contains("--diagnose-mvvm"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DiagnoseMvvm();
                return;
            }
            if (e.Args.Contains("--diagnose-preview"))
            {
                var preview = services.Windows.CreatePreview();
                CaptureTestPattern? pattern = null;
                if (e.Args.Contains("--animate"))
                {
                    var display = services.Catalog.GetDisplays().FirstOrDefault(d => d.IsVirtual);
                    if (display != null) { pattern = new CaptureTestPattern(display); pattern.Show(); }
                }
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                bool reported = false;
                // 미리보기 진단의 프레임 수와 처리 시간을 JSON으로 한 번만 출력하는 지역 함수입니다.
                void Report()
                {
                    if (reported) return;
                    reported = true;
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        preview.PresentedFrames,
                        Seconds = elapsed.Elapsed.TotalSeconds,
                        AverageUpdateFps = preview.PresentedFrames / elapsed.Elapsed.TotalSeconds,
                        preview.CaptureMilliseconds,
                        preview.CaptureRequests,
                        preview.PresentationMilliseconds,
                        Status = preview.DiagnosticStatus
                    }));
                }
                preview.Closed += (_, _) => { timer.Stop(); Report(); pattern?.Close(); };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Report();
                    preview.Close();
                    Shutdown(0);
                };
                preview.Show();
                timer.Start();
                return;
            }
            if (e.Args.Contains("--diagnose"))
            {
                try
                {
                    var devices = services.Catalog.GetDevices();
                    var displays = services.Catalog.GetDisplays();
                    var captures = displays.Where(d => d.IsVirtual).Select(d =>
                    {
                        var frame = ScreenCapture.Capture(d, services.CreateCaptureSession);
                        return new { d.Name, frame.PixelWidth, frame.PixelHeight };
                    }).ToArray();
                    // Construct the XAML tree without showing a window or changing any device.
                    var window = services.Windows.CreateMain();
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Devices = devices.Select(d => new { d.InstanceId, d.Started, d.Problem }),
                        Displays = displays,
                        Captures = captures,
                        WindowConstructed = window.IsInitialized
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    Shutdown(0);
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
                return;
            }
            instanceMutex = new Mutex(false, @"Global\VirtualMonitor.ConfigurationOwner.v1");
            try { ownsInstance = instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstance = true; }
            if (!ownsInstance)
            {
                MessageBox.Show("VirtualMonitor가 이미 실행 중입니다. 트레이 아이콘에서 열어 주세요.");
                Shutdown();
                return;
            }
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var main = services.Windows.CreateMain();
            MainWindow = main;
            InitializeTray(main);
            main.Show();
            try
            {
                var pending = services.Journals.PendingJournals();
                if (pending.Count > 0)
                    MessageBox.Show("이전 구성 변경의 복구가 완료되지 않은 기록이 있습니다. 현재 모니터 구성을 확인하세요.\n" + string.Join("\n", pending), "구성 복구 기록");
            }
            catch (Exception ex) { services.Log.Write("recovery.scan", error: ex); }
            if (e.Args.Contains("--diagnose-tray")) DiagnoseTray(main);
        }

        private async void DiagnoseArchitecture()
        {
            try { await services.CreateArchitectureDiagnostics().RunAsync(Dispatcher); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
        }

        private async void DiagnoseMonitorConfiguration()
        {
            try { await MonitorConfigurationDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
        }

        private async void DiagnoseDependencyInjection()
        {
            try { await DependencyInjectionDiagnostics.RunAsync(Dispatcher); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
        }
    }

}
