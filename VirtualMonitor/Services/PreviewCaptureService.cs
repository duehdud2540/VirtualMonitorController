using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VirtualMonitor;

// Owns capture and GPU presentation on one worker. Only the CPU fallback uses FrameReady on the UI thread.
internal sealed class PreviewCaptureService(Dispatcher dispatcher, Func<MonitorDisplay, ICaptureSession> createSession, IAppLog log) : IPreviewCaptureService, IAsyncDisposable
{
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<ICaptureSession, bool>? workerOutput;
    public Func<ICaptureSession, bool>? WorkerOutput { get => Volatile.Read(ref workerOutput); set => Volatile.Write(ref workerOutput, value); }
    public Action? WorkerTick { get; set; }
    public event Action? OutputFailed;
    private long submittedFrames;
    public long SubmittedFrameCount => Interlocked.Read(ref submittedFrames);
    public double DispatcherWaitMilliseconds { get; private set; }
    private readonly CancellationTokenSource stop = new();
    private readonly object lifetime = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private MonitorDisplay? selected;
    private bool paused;
    private bool started;
    private volatile bool disposed;
    private bool workerFinished;
    private TaskCompletionSource? releaseRequest;
    public event Action<MonitorDisplay, ICaptureSession>? FrameReady;
    public event Action<string>? Error;
    public double CaptureMilliseconds { get; private set; }
    public double PresentationMilliseconds { get; private set; }
    public long CaptureRequests { get; private set; }

    // Volatile.Write로 새 화면 참조를 게시하여 캡처 스레드가 최신 선택을 읽게 합니다.
    public void SelectDisplay(MonitorDisplay? display) => Volatile.Write(ref selected, display);
    // 작업 스레드가 확인할 정지 플래그를 변경합니다. 캡처 자원 해제는 CaptureLoop가 수행합니다.
    public void SetPaused(bool value) => Volatile.Write(ref paused, value);

    // 정지를 요청하고 네이티브 자원 해제 완료를 Task로 알려 줍니다. 드라이버 재시작 전에 이 Task를 기다립니다.
    public Task PauseAndReleaseAsync()
    {
        lock (lifetime)
        {
            Volatile.Write(ref paused, true);
            if (!started || workerFinished) return Task.CompletedTask;
            releaseRequest ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return releaseRequest.Task;
        }
    }

    // 캡처 전용 백그라운드 스레드를 한 번만 시작합니다.
    public void Start()
    {
        if (started || disposed) return;
        started = true;
        new Thread(CaptureLoop) { IsBackground = true, Name = "VirtualMonitor capture" }.Start();
    }

    // 취소를 요청하고 실제 종료 정리는 작업 스레드에 맡깁니다. UI에서 동기 대기하면 프레임 전달과 교착될 수 있습니다.
    public void Dispose()
    {
        lock (lifetime)
        {
            if (disposed) return;
            disposed = true;
            if (!workerFinished) stop.Cancel();
            if (!started) { stop.Dispose(); workerFinished = true; finished.TrySetResult(); }
        }
    }

    public Task StopAsync() { Dispose(); return finished.Task; }
    public async ValueTask DisposeAsync() => await StopAsync();

    private void SwitchToWpf(Exception error, CancellationToken cancellation)
    {
        log.Write("preview.fallback", error: error);
        Volatile.Write(ref workerOutput, null);
        WorkerTick = null;
        dispatcher.InvokeAsync(() => { if (!disposed) OutputFailed?.Invoke(); }, DispatcherPriority.Render, cancellation)
            .Task.GetAwaiter().GetResult();
    }

    private void TickOutput(CancellationToken cancellation)
    {
        try { WorkerTick?.Invoke(); }
        catch (Exception ex) { SwitchToWpf(ex, cancellation); }
    }

    // 프레임 도착 신호에 맞춰 처리합니다. 유한 대기로 정지·화면 변경·재출력 요청에도 반응합니다.
    private void CaptureLoop()
    {
        ICaptureSession? capture = null;
        var cancellation = stop.Token;
        double nextStatistics = 0;
        int failures = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (cancellation.IsCancellationRequested) break;
                var display = Volatile.Read(ref selected);
                if (display == null || Volatile.Read(ref paused))
                {
                    TickOutput(cancellation);
                    capture?.Dispose();
                    capture = null;
                    lock (lifetime) { releaseRequest?.TrySetResult(); releaseRequest = null; }
                    if (cancellation.WaitHandle.WaitOne(100)) break;
                    continue;
                }
                try
                {
                    if (capture == null || capture.Width != display.Width || capture.Height != display.Height || capture.DisplayName != display.Name)
                    {
                        capture?.Dispose(); capture = null;
                        capture = createSession(display);
                        nextStatistics = clock.Elapsed.TotalMilliseconds;
                    }
                    nint ready = capture.FrameAvailableEvent;
                    if (ready != 0)
                    {
                        uint wait = WaitForMultipleObjects(2,
                            new[] { cancellation.WaitHandle.SafeWaitHandle.DangerousGetHandle(), ready }, false, 16);
                        if (wait == 0) break;
                        if (wait == uint.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        if (Volatile.Read(ref paused) || !ReferenceEquals(display, Volatile.Read(ref selected))) continue;
                        if (wait == 258) { TickOutput(cancellation); continue; }
                    }
                    else if (cancellation.WaitHandle.WaitOne(7)) break; // Non-event diagnostic sessions.
                    if (clock.Elapsed.TotalMilliseconds >= nextStatistics)
                    {
                        log.Write("capture.frames", new { display = display.Name, capture.Statistics });
                        nextStatistics = clock.Elapsed.TotalMilliseconds + 5000;
                    }
                    double started = clock.Elapsed.TotalMilliseconds;
                    CaptureRequests++;
                    var output = Volatile.Read(ref workerOutput);
                    bool received = capture.Read(display, output != null);
                    double captured = clock.Elapsed.TotalMilliseconds;
                    CaptureMilliseconds += captured - started;
                    var buffer = capture;
                    if (Volatile.Read(ref paused) || !ReferenceEquals(display, Volatile.Read(ref selected))) continue;
                    if (received && output != null)
                    {
                        try
                        {
                            if (output(buffer)) Interlocked.Increment(ref submittedFrames);
                        }
                        catch (Exception ex)
                        {
                            SwitchToWpf(ex, cancellation);
                        }
                    }
                    else if (received)
                    {
                        // CPU fallback buffers remain mapped until the dispatcher copies them.
                        double queued = clock.Elapsed.TotalMilliseconds;
                        dispatcher.InvokeAsync(() =>
                        {
                            DispatcherWaitMilliseconds += clock.Elapsed.TotalMilliseconds - queued;
                            FrameReady?.Invoke(display, buffer);
                        }, DispatcherPriority.Render, cancellation).Task.GetAwaiter().GetResult();
                    }
                    TickOutput(cancellation);
                    PresentationMilliseconds += clock.Elapsed.TotalMilliseconds - captured;
                    failures = 0;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    capture?.Dispose(); capture = null;
                    if (cancellation.IsCancellationRequested) break;
                    dispatcher.InvokeAsync(() =>
                    {
                        if (!disposed) Error?.Invoke($"미리보기 오류: {ex.Message}");
                    }, DispatcherPriority.Background, cancellation).Task.GetAwaiter().GetResult();
                    failures++;
                    log.Write("capture.retry", new { attempt = failures, display = display.StableId }, ex);
                    bool permanent = ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                        || ex.HResult is unchecked((int)0x80004001) or unchecked((int)0x80070005);
                    if (permanent || failures >= 8)
                    {
                        Volatile.Write(ref paused, true);
                        dispatcher.InvokeAsync(() => { if (!disposed) Error?.Invoke("캡처를 중지했습니다. 원인을 해결한 후 창을 다시 열어 주세요. " + ex.Message); });
                    }
                    if (cancellation.WaitHandle.WaitOne(Math.Min(4000, 250 * (1 << Math.Min(failures, 4))))) break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                dispatcher.InvokeAsync(() => { if (!disposed) Error?.Invoke($"미리보기 오류: {ex.Message}"); });
        }
        finally
        {
            capture?.Dispose();
            lock (lifetime)
            {
                workerFinished = true;
                releaseRequest?.TrySetResult(); releaseRequest = null;
                stop.Dispose();
                finished.TrySetResult();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint timeout);

}
