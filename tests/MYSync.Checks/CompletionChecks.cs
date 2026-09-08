using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class CompletionChecks
{
    private sealed class Reports(Action<SyncProgress> receive) : IProgress<SyncProgress>
    { public void Report(SyncProgress value) => receive(value); }
    private sealed class Delayed(LocalEndpoint inner) : ISyncEndpoint, IFileStateEndpoint
    {
        public TaskCompletionSource SlowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Scans;
        public bool SupportsConcurrentFiles => true;
        public async Task<ScanResult> ScanAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref Scans) >= 2)
            { FinalStarted.TrySetResult(); await ReleaseFinal.Task.WaitAsync(ct); }
            return await inner.ScanAsync(ct);
        }
        public Task<SyncEntry?> InspectFileAsync(string path, CancellationToken ct) => inner.InspectFileAsync(path, ct);
        public Task<Stream> OpenReadAsync(SyncEntry entry, CancellationToken ct) => inner.OpenReadAsync(entry, ct);
        public async Task PutFileAsync(string path, SyncEntry? expected, Stream input, string hash, CancellationToken ct)
        {
            if (path == "slow.txt") { SlowStarted.TrySetResult(); await ReleaseSlow.Task.WaitAsync(ct); }
            await inner.PutFileAsync(path, expected, input, hash, ct);
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(SyncEntry entry, CancellationToken ct) => inner.DeleteAsync(entry, ct);
    }
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        var area = Path.Combine(scratch, "early-completion");
        var lroot = Path.Combine(area, "left"); var rroot = Path.Combine(area, "right");
        Directory.CreateDirectory(lroot); Directory.CreateDirectory(rroot);
        await File.WriteAllTextAsync(Path.Combine(lroot, "fast.txt"), "fast");
        await File.WriteAllTextAsync(Path.Combine(lroot, "slow.txt"), "slow");
        var local = new LocalEndpoint(lroot, Path.Combine(area, "lr"));
        var remote = new Delayed(new LocalEndpoint(rroot, Path.Combine(area, "rr")));
        var journal = new SyncJournal(Path.Combine(area, "journal.db")); var pair = Guid.NewGuid();
        var snapshots = await SyncSnapshots.ReadAsync(local, remote, default);
        journal.Enqueue(pair, SyncPlanner.Compare(snapshots.Local, snapshots.Remote, []));
        var fastDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Reports(p =>
        {
            if (p.Event != TransferEvent.Completed) return;
            if (p.Path == "fast.txt") fastDone.TrySetResult();
            if (p.Path == "slow.txt") slowDone.TrySetResult();
        });
        var run = new SyncExecutor(journal).RunAsync(pair, local, remote, progress: progress, initialSnapshots: snapshots);
        try
        {
            await remote.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!run.IsCompleted && await File.ReadAllTextAsync(Path.Combine(rroot, "fast.txt")) == "fast", "fast file waited for slow transfer");
            remote.ReleaseSlow.TrySetResult();
            await slowDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await remote.FinalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!run.IsCompleted && remote.Scans == 2, "completion waited for final comparison or repeated the initial full scan");
        }
        finally { remote.ReleaseSlow.TrySetResult(); remote.ReleaseFinal.TrySetResult(); }
        Check((await run).Converged, "final comparison lost after early completion");

        await File.WriteAllTextAsync(Path.Combine(lroot, "fast.txt"), "planned");
        snapshots = await SyncSnapshots.ReadAsync(local, remote, default);
        journal.Enqueue(pair, SyncPlanner.Compare(snapshots.Local, snapshots.Remote, journal.ReadBaseline(pair)));
        await File.WriteAllTextAsync(Path.Combine(lroot, "fast.txt"), "changed-after-plan");
        var stale = await new SyncExecutor(journal).RunAsync(pair, local, remote, initialSnapshots: snapshots);
        Check(!stale.Converged && await File.ReadAllTextAsync(Path.Combine(rroot, "fast.txt")) == "fast",
            "reused planning snapshot bypassed current-file preconditions");
        Console.WriteLine("PASS: file completion before slower files and final scan, reused initial scan, stale snapshot mutation rejection");
    }
}
