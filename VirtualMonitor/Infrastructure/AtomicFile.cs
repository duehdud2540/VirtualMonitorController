using System.IO;
using System.Text;

namespace VirtualMonitor;

internal sealed class AtomicFile : IAtomicFile
{
    public void Write(string path, string text, Action<string>? beforeCommit = null)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            beforeCommit?.Invoke(temporary);
            if (File.Exists(path)) File.Replace(temporary, path, path + "." + Guid.NewGuid().ToString("N") + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
