namespace VirtualMonitor;

internal interface IPreviewCaptureService : IDisposable
{
    Func<ICaptureSession, bool>? WorkerOutput { get; set; }
    Action? WorkerTick { get; set; }
    event Action? OutputFailed;
    event Action<MonitorDisplay, ICaptureSession>? FrameReady;
    event Action<string>? Error;
    long SubmittedFrameCount { get; }
    double DispatcherWaitMilliseconds { get; }
    double CaptureMilliseconds { get; }
    double PresentationMilliseconds { get; }
    long CaptureRequests { get; }
    void SelectDisplay(MonitorDisplay? display);
    void SetPaused(bool value);
    void Start();
    Task PauseAndReleaseAsync();
    Task StopAsync();
}
