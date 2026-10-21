using System.IO;
using System.Text.Json;

namespace VirtualMonitor;

internal sealed class AppLog : IAppLog
{
    private readonly IFileSystem fileSystem;
    private readonly string folder;

    internal AppLog(IFileSystem fileSystem, string folder)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    private readonly object Gate = new();
    public void Write(string operation, object? detail = null, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                fileSystem.CreateDirectory(folder);
                fileSystem.AppendAllText(Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd}.jsonl"),
                    JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, operation, detail, error = error?.ToString() }) + Environment.NewLine);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
