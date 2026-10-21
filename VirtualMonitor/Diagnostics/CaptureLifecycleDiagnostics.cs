using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace VirtualMonitor;

internal sealed class CaptureLifecycleDiagnostics(Func<Dispatcher, PreviewCaptureService> createCapture, IAppLog log)
{
    // 화면 미선택 상태에서 시작 전·실행 중·종료 후 해제 요청이 제한 시간 내 완료되는지 검사합니다.
    internal async Task RunAsync(Dispatcher dispatcher)
    {
        using var capture = createCapture(dispatcher);
        await capture.PauseAndReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        capture.Start();
        for (int i = 0; i < 3; i++)
        {
            capture.SetPaused(false);
            await Task.Delay(120);
            await capture.PauseAndReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await capture.PauseAndReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        if (capture.CaptureRequests != 0) throw new InvalidOperationException("No monitor should be captured by this diagnostic.");
        await CheckActiveCaptureAsync(dispatcher);
        await CheckFrameEventsAsync(dispatcher);
        Console.WriteLine("Capture lifecycle: idle, injected session, GPU output fallback and disposal checks passed (no live devices).");
    }
    private async Task CheckFrameEventsAsync(Dispatcher dispatcher)
    {
        var display = new MonitorDisplay("event-fake", "", 0, 0, 1, 1, true);
        var created = new TaskCompletionSource<EventCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var submitted = new SemaphoreSlim(0);
        await using var service = new PreviewCaptureService(dispatcher, d =>
        {
            var session = new EventCapture(d);
            created.SetResult(session);
            return session;
        }, log);
        service.WorkerOutput = _ => { submitted.Release(); return true; };
        service.SelectDisplay(display);
        service.Start();
        var capture = await created.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(80);
        Require(capture.Reads == 0, "idle frame event does not poll the capture pool");
        for (int i = 0; i < 5; i++)
        {
            capture.Ready.Set();
            Require(await submitted.WaitAsync(TimeSpan.FromSeconds(2)), "frame event wakes capture worker");
        }
        await service.PauseAndReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Require(capture.Reads == 5 && capture.Released, "each frame signal is consumed and pause releases waiting session");
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class EventCapture(MonitorDisplay display) : ICaptureSession
    {
        public AutoResetEvent Ready { get; } = new(false);
        public int Reads;
        public bool Released;
        public nint FrameAvailableEvent => Ready.SafeWaitHandle.DangerousGetHandle();
        public nint Pixels => 0;
        public nint Texture => 0;
        public nint Device => 0;
        public int Width => 1;
        public int Height => 1;
        public int Stride => 4;
        public int BufferSize => 4;
        public string DisplayName => display.Name;
        public bool Read(MonitorDisplay target, bool gpu = false) { Interlocked.Increment(ref Reads); return true; }
        public void Dispose() { Ready.Dispose(); Released = true; }
    }
    private async Task CheckActiveCaptureAsync(Dispatcher dispatcher)
    {
        int uiThread = Environment.CurrentManagedThreadId, outputThread = 0, fallbackCount = 0;
        bool released = false;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var display = new MonitorDisplay("fake", "", 0, 0, 1, 1, true);
        await using var service = new PreviewCaptureService(dispatcher, d => new FakeCapture(d, () => released = true), log);
        service.WorkerOutput = _ =>
        {
            outputThread = Environment.CurrentManagedThreadId;
            throw new InvalidOperationException("injected output failure");
        };
        service.OutputFailed += () => fallbackCount++;
        service.FrameReady += (_, buffer) =>
        {
            if (Environment.CurrentManagedThreadId != uiThread || Marshal.ReadInt32(buffer.Pixels) != 0x123456)
                received.TrySetException(new InvalidOperationException("invalid CPU fallback buffer or thread"));
            else received.TrySetResult();
        };
        service.SelectDisplay(display);
        service.Start();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(outputThread != uiThread && fallbackCount == 1, "GPU output is off UI thread and fallback is delivered once");
        await service.PauseAndReleaseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Require(released, "pause acknowledgement follows actual frame resource disposal");
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class FakeCapture(MonitorDisplay display, Action disposed) : ICaptureSession
    {
        public nint Pixels { get; private set; } = Marshal.AllocHGlobal(4);
        public nint Texture => 0;
        public nint Device => 0;
        public int Width => 1;
        public int Height => 1;
        public int Stride => 4;
        public int BufferSize => 4;
        public string DisplayName => display.Name;
        public bool Read(MonitorDisplay target, bool gpu = false) { Marshal.WriteInt32(Pixels, 0x123456); return true; }
        public void Dispose() { if (Pixels != 0) { Marshal.FreeHGlobal(Pixels); Pixels = 0; disposed(); } }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
