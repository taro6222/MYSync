namespace MYSync.Sync.Infrastructure;

/// <summary>User holds are scoped to this app session. Cancellation never deletes source or destination files.</summary>
public sealed class TransferControl
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> holds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> active = new(StringComparer.OrdinalIgnoreCase);
    private static bool Covers(string parent, string path) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    public string? State(string path) { lock (gate) return holds.FirstOrDefault(x => Covers(x.Key, path)).Value; }
    public void Hold(string path, string state)
    {
        lock (gate) { holds[path] = state; foreach (var token in active.Where(x => Covers(path, x.Key)).Select(x => x.Value).ToArray()) token.Cancel(); }
    }
    public void Resume(string path) { lock (gate) holds.Remove(path); }
    public CancellationTokenSource Begin(string path, CancellationToken ct)
    {
        lock (gate)
        {
            var token = CancellationTokenSource.CreateLinkedTokenSource(ct);
            active[path] = token;
            if (State(path) is not null) token.Cancel();
            return token;
        }
    }
    public void End(string path) { lock (gate) { if (active.Remove(path, out var token)) token.Dispose(); } }
}
