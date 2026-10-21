using System.IO;
using System.Text.Json;

namespace VirtualMonitor;

internal interface IRecoveryJournalStore
{
    void Write(string id, string json);
    IReadOnlyList<string> PendingJournals();
}

internal sealed class RecoveryJournalStore(IFileSystem fileSystem, IAtomicFile atomicFile, string directory) : IRecoveryJournalStore
{
    public void Write(string id, string json) => atomicFile.Write(Path.Combine(directory, id + ".json"), json);

    public IReadOnlyList<string> PendingJournals()
    {
        if (!fileSystem.DirectoryExists(directory)) return [];
        return fileSystem.EnumerateFiles(directory, "*.json").Where(path =>
        {
            using var document = JsonDocument.Parse(fileSystem.ReadAllText(path));
            return document.RootElement.GetProperty("state").GetString() is "pending" or "recovery-required";
        }).ToArray();
    }
}
