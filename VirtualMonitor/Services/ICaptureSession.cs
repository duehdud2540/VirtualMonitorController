namespace VirtualMonitor;

// Borrowed frame memory/texture remains valid until Read or Dispose on its owning worker.
internal interface ICaptureSession : IDisposable
{
    nint Pixels { get; }
    nint Texture { get; }
    nint Device { get; }
    int Width { get; }
    int Height { get; }
    int Stride { get; }
    int BufferSize { get; }
    string DisplayName { get; }
    bool Read(MonitorDisplay display, bool gpu = false);
}
