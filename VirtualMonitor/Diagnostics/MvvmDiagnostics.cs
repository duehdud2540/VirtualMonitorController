using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VirtualMonitor;

public partial class App
{
    // Explicit UI regression diagnostic; never enables/disables display devices.
    // 실제 WPF 창에서 바인딩·단축키·표시 모드·중복 창을 검사합니다. 창과 추적 설정을 정리한 뒤 결과 코드로 종료합니다.
    private async void DiagnoseMvvm()
    {
        var checks = new Dictionary<string, bool>();
        using var bindingErrors = new BindingErrorListener();
        var trace = PresentationTraceSources.DataBindingSource;
        var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Error;
        trace.Listeners.Add(bindingErrors);
        MainWindow? main = null;
        PreviewWindow? preview = null;
        int result = 1;
        try
        {
            main = services.Windows.CreateMain();
            preview = services.Windows.CreatePreview();
            main.Show();
            preview.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var vm = (PreviewViewModel)preview.DataContext;
            var mainVm = (MainViewModel)main.DataContext;
            await mainVm.RefreshAsync();
            var f11 = preview.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.F11);
            var escape = preview.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.Escape);
            checks["KeyBindingsResolved"] = ReferenceEquals(f11.Command, vm.ToggleFullScreenCommand)
                && ReferenceEquals(escape.Command, vm.ExitFullScreenCommand);
            checks["EscapeDisabledOutsideFullScreen"] = !escape.Command.CanExecute(null);
            var originalState = preview.WindowState;
            f11.Command.Execute(null);
            checks["F11EntersFullScreen"] = vm.IsFullScreen && preview.WindowStyle == WindowStyle.None
                && preview.WindowState == WindowState.Maximized && escape.Command.CanExecute(null);
            escape.Command.Execute(null);
            checks["EscapeRestoresWindow"] = !vm.IsFullScreen && preview.WindowState == originalState
                && preview.WindowStyle == WindowStyle.SingleBorderWindow;

            vm.CompactModeCommand.Execute(null);
            checks["CompactMode"] = vm.IsCompactMode && preview.WindowStyle == WindowStyle.None
                && ((FrameworkElement)preview.FindName("compactHeader")).Visibility == Visibility.Visible;
            f11.Command.Execute(null);
            escape.Command.Execute(null);
            checks["FullScreenRestoresCompactMode"] = vm.IsCompactMode && !vm.IsFullScreen
                && ((FrameworkElement)preview.FindName("compactHeader")).Visibility == Visibility.Visible;
            vm.NormalModeCommand.Execute(null);
            checks["NormalMode"] = !vm.IsCompactMode && preview.WindowStyle == WindowStyle.SingleBorderWindow;

            vm.IsTopmost = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            checks["TopmostBinding"] = preview.Topmost;
            vm.IsTopmost = false;
            var pixels = (CheckBox)preview.FindName("actualPixels");
            pixels.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            checks["ActualPixelsBinding"] = vm.ActualPixels && ((Image)preview.FindName("preview")).Stretch == Stretch.Fill;
            pixels.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
            checks["FitToWindowBinding"] = !vm.ActualPixels && ((Image)preview.FindName("preview")).Stretch == Stretch.Uniform;
            checks["StatusBinding"] = ((TextBlock)preview.FindName("status")).Text == vm.Status;
            checks["ConnectAvailability"] = mainVm.ConnectCommand.CanExecute(null)
                == (mainVm.SelectedDevice is { Started: false });
            checks["DisconnectAvailability"] = mainVm.DisconnectCommand.CanExecute(null)
                == (mainVm.SelectedDevice != null && mainVm.SelectedDevice.Problem != 22);

            var displayList = (ListBox)main.FindName("activeDisplays");
            bool selectionMatches = true;
            foreach (var display in mainVm.Displays.Where(d => d.IsVirtual).ToArray())
            {
                displayList.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, display);
                selectionMatches &= mainVm.SelectedDisplay == display
                    && mainVm.SelectedDevice != null
                    && MainViewModel.GetMonitorKey(mainVm.SelectedDevice) == PreviewViewModel.GetDisplayKey(display);
                await mainVm.RefreshAsync();
                selectionMatches &= mainVm.SelectedDisplay?.Name == display.Name;
            }
            checks["ActiveDisplaySelectionAndRefresh"] = selectionMatches;
            mainVm.SelectedDevice = null;
            checks["PreviewDisabledWithoutSelection"] = !mainVm.PreviewCommand.CanExecute(null);
            mainVm.SelectedDevice = new MonitorDevice("inactive", 1, false, 22) { SlotIndex = 15 };
            checks["PreviewDisabledForInactiveMonitor"] = !mainVm.PreviewCommand.CanExecute(null);
            await mainVm.RefreshAsync();

            var devA = new MonitorDevice("ROOT\\MTTVDD\\0000#0", 1, true, 0) { SlotIndex = 0, Alias = "모니터A" };
            var devB = new MonitorDevice("ROOT\\MTTVDD\\0000#1", 1, true, 0) { SlotIndex = 1, Alias = "모니터B" };
            var prevSelected = mainVm.SelectedDevice;
            int initialPreviewCount = Windows.OfType<PreviewWindow>().Count();
            mainVm.SelectedDevice = devA;
            bool canPreviewBeforeOpen = mainVm.PreviewCommand.CanExecute(null);
            mainVm.PreviewCommand.Execute(null);
            bool disabledWhenSelectedMonitorOpen = !mainVm.PreviewCommand.CanExecute(null);
            mainVm.SelectedDevice = devB;
            bool enabledWhenOtherMonitorSelected = mainVm.PreviewCommand.CanExecute(null);
            mainVm.PreviewCommand.Execute(null);
            bool bothSeparateWindowsOpen = Windows.OfType<PreviewWindow>().Count() == initialPreviewCount + 2
                && !mainVm.PreviewCommand.CanExecute(null);
            mainVm.SelectedDevice = devA;
            int beforeDupCount = main.DuplicateMonitorNoticeCount;
            mainVm.OpenPreview();
            bool duplicateEventFired = main.DuplicateMonitorNoticeCount == beforeDupCount + 1;
            foreach (var extra in Windows.OfType<PreviewWindow>().Where(w => !ReferenceEquals(w, preview)).ToArray())
            {
                await extra.StopAsync();
                extra.Close();
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            bool reenabledAfterClose = mainVm.PreviewCommand.CanExecute(null);
            mainVm.SelectedDevice = prevSelected;
            checks["PreviewDisabledForOpenMonitorAndIsolated"] = canPreviewBeforeOpen
                && disabledWhenSelectedMonitorOpen
                && enabledWhenOtherMonitorSelected
                && bothSeparateWindowsOpen
                && duplicateEventFired
                && reenabledAfterClose;

            var manager = services.Windows.CreateMonitorManager();
            var managerVm = (MonitorManagerViewModel)manager.DataContext;
            await managerVm.RefreshAsync();
            checks["MainAndManagerMonitorCountMatch"] = mainVm.Devices.Count == managerVm.Monitors.Count;
            manager.Close();
            checks["NoBindingErrors"] = bindingErrors.Errors.Count == 0;
            result = checks.Values.All(passed => passed) ? 0 : 1;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Checks = checks, MainDevices = mainVm.Devices.Select(d => d.Label), BindingErrors = bindingErrors.Errors }));
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            preview?.Close();
            main?.Close();
            trace.Listeners.Remove(bindingErrors);
            trace.Switch.Level = previousLevel;
            Shutdown(result);
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = new();
        // 비어 있지 않은 WPF 바인딩 추적 메시지를 오류 목록에 모읍니다.
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
        // 줄 단위 메시지도 같은 오류 수집 경로로 전달합니다.
        public override void WriteLine(string? message) => Write(message);
    }
}
