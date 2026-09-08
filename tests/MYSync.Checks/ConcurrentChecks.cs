using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class ConcurrentChecks
{
    private sealed class Endpoint(LocalEndpoint inner) : ISyncEndpoint, IFileStateEndpoint
    {
        public bool Fail = true;
        private int active;
        public int Maximum;
        public bool SupportsConcurrentFiles => true;
        public Task<ScanResult> ScanAsync(CancellationToken ct) => inner.ScanAsync(ct);
        public Task<SyncEntry?> InspectFileAsync(string path, CancellationToken ct) => inner.InspectFileAsync(path, ct);
        public Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct) => inner.OpenReadAsync(expected, ct);
        public async Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref active);
            lock (this) Maximum = Math.Max(Maximum, count);
            try
            {
                await Task.Delay(60, ct);
                if (path == "failed.txt" && Fail) throw new SyncTransferException("test permission failure", SyncFailureKind.Permission);
                await inner.PutFileAsync(path, expected, content, sha256, ct);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(SyncEntry entry, CancellationToken ct) => inner.DeleteAsync(entry, ct);
    }
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var area = Path.Combine(scratch, "concurrent");
        var leftRoot = Path.Combine(area, "left"); var rightRoot = Path.Combine(area, "right");
        Directory.CreateDirectory(Path.Combine(leftRoot, "nested")); Directory.CreateDirectory(rightRoot);
        foreach (var name in new[] { "failed.txt", "b.txt", "c.txt", "d.txt", "nested/child.txt" })
            await File.WriteAllTextAsync(Path.Combine(leftRoot, name), "first");
        var local = new LocalEndpoint(leftRoot, Path.Combine(area, "left-recovery"));
        var remote = new Endpoint(new LocalEndpoint(rightRoot, Path.Combine(area, "right-recovery")));
        var pair = Guid.NewGuid(); var journal = new SyncJournal(Path.Combine(area, "journal.db"));
        async Task<ExecutionReport> Cycle()
        {
            var l = await local.ScanAsync(default); var r = await remote.ScanAsync(default);
            journal.CommitVerifiedPaths(pair, l, r);
            journal.RefreshPlan(pair, SyncPlanner.Compare(l, r, journal.ReadBaseline(pair)));
            return await new SyncExecutor(journal).RunAsync(pair, local, remote);
        }
        var first = await Cycle();
        Check(!first.Converged && first.Issues.Count == 1 && remote.Maximum is >= 2 and <= 3, "bounded concurrency or failure isolation failed");
        Check(File.Exists(Path.Combine(rightRoot, "nested/child.txt")) && !File.Exists(Path.Combine(rightRoot, "failed.txt")), "directory dependencies or failed file protection failed");
        Check(journal.ReadBaseline(pair).Any(x => x.Path == "b.txt") && journal.ReadJobs(pair).Count(x => x.State == JobState.NeedsReconcile) == 1,
            "verified files did not checkpoint independently");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "b.txt"), "updated");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "new.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "failed.txt"), "latest");
        var second = await Cycle();
        Check(!second.Converged && await File.ReadAllTextAsync(Path.Combine(rightRoot, "b.txt")) == "updated" &&
            await File.ReadAllTextAsync(Path.Combine(rightRoot, "new.txt")) == "new", "failed file blocked later additions or overwrites");
        remote.Fail = false;
        Check((await Cycle()).Converged && await File.ReadAllTextAsync(Path.Combine(rightRoot, "failed.txt")) == "latest",
            "stale queued expectation prevented recovery");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "b.txt"), "local-change");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "b.txt"), "remote-change");
        var conflict = await Cycle();
        Check(!conflict.Converged && await File.ReadAllTextAsync(Path.Combine(leftRoot, "b.txt")) == "local-change" &&
            await File.ReadAllTextAsync(Path.Combine(rightRoot, "b.txt")) == "remote-change", "concurrent external edits were overwritten");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "new.txt"), "after-conflict");
        await Cycle();
        Check(await File.ReadAllTextAsync(Path.Combine(rightRoot, "new.txt")) == "after-conflict", "unresolved conflict blocked unrelated update");
        var legacyPair = Guid.NewGuid();
        var published = new SyncEntry("legacy.txt", EntryKind.File, "old-hash");
        journal.Enqueue(legacyPair, new([new("legacy.txt", SyncAction.Upload, published, null, "legacy")], []));
        var legacyJob = journal.ReadJobs(legacyPair).Single();
        Check(journal.TryAcquire(legacyJob.Id), "legacy acquire");
        journal.SetOutcome(legacyJob.Id, JobState.Applied);
        var legacyLocal = new ScanResult([published with { ContentHash = "new-hash" }], []);
        var legacyRemote = new ScanResult([published], []);
        journal.CommitVerifiedPaths(legacyPair, legacyLocal, legacyRemote);
        var legacyPlan = SyncPlanner.Compare(legacyLocal, legacyRemote, journal.ReadBaseline(legacyPair));
        Check(legacyPlan.Operations.Single().Action == SyncAction.Upload, "old Applied state turned later update into a conflict");
        journal.RefreshPlan(legacyPair, legacyPlan);
        Check(journal.ReadJobs(legacyPair).Single(x => x.State == JobState.Pending).Operation.ExpectedLocal?.ContentHash == "new-hash",
            "legacy applied queue blocked current update");
        Console.WriteLine("PASS: three bounded workers, folder dependencies, partial checkpoints, later updates after failure, stale queue refresh and conflict preservation");
    }
}
