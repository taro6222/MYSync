using MYSync.Sync.Core;

namespace MYSync.Sync.Infrastructure;

/// <summary>Planning snapshots may be reused by a cycle. Every mutation still revalidates the
/// actual path; directory mutations still perform a fresh structural scan.</summary>
public sealed record SyncSnapshots(ScanResult Local, ScanResult Remote)
{
    public static async Task<SyncSnapshots> ReadAsync(ISyncEndpoint local, ISyncEndpoint remote, CancellationToken ct)
    {
        var left = local.ScanAsync(ct);
        var right = remote.ScanAsync(ct);
        await Task.WhenAll(left, right);
        return new(await left, await right);
    }
}
