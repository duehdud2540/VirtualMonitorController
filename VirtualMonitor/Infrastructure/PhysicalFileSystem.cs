using System.IO;

namespace VirtualMonitor;

// 파일 시스템 경계. 호출자는 이 구현 대신 메모리 구현을 주입할 수 있습니다.
internal sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void AppendAllText(string path, string text) => File.AppendAllText(path, text);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public IEnumerable<string> EnumerateFiles(string path, string pattern) => Directory.EnumerateFiles(path, pattern);
}
