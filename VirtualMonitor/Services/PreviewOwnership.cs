namespace VirtualMonitor;

// A single registry for both bound (possibly offline) monitors and unbound diagnostic views.
internal sealed class PreviewOwnership : IPreviewOwnership
{
    private readonly object Gate = new();
    private readonly Dictionary<string, object> Owners = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;
    public bool IsOwned(string key) => IsOwned(key, null);
    public bool IsOwned(string key, object? except)
    {
        lock (Gate) return Owners.TryGetValue(key, out var owner) && !ReferenceEquals(owner, except);
    }
    public bool TryAcquire(string key, object owner)
    {
        lock (Gate)
        {
            if (Owners.TryGetValue(key, out var current)) return ReferenceEquals(current, owner);
            Owners.Add(key, owner);
        }
        Changed?.Invoke();
        return true;
    }
    public void Release(string key, object owner)
    {
        lock (Gate)
        {
            if (!Owners.TryGetValue(key, out var current) || !ReferenceEquals(current, owner)) return;
            Owners.Remove(key);
        }
        Changed?.Invoke();
    }
}
