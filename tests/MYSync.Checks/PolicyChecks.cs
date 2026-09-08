using System.Diagnostics;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class PolicyChecks
{
    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var rules = new SyncExclusions("*.bak\ncache/\nlogs/debug.log\n**/temp/");
        Check(rules.Matches("sub/X.BAK", EntryKind.File), "extension exclusion failed");
        Check(rules.Matches("sub/cache/a.txt", EntryKind.File), "nested excluded folder leaked");
        Check(!rules.Matches("cache", EntryKind.File), "folder rule excluded a same-name file");
        Check(rules.Matches("logs/debug.log", EntryKind.File) && !rules.Matches("sub/logs/debug.log", EntryKind.File), "root-relative rule failed");
        Check(rules.Matches("temp/a", EntryKind.File) && rules.Matches("sub/temp/a", EntryKind.File), "recursive glob failed");
        var db = new SettingsStore(Path.Combine(scratch, "policies.db"));
        var pair = new SyncPair(Guid.NewGuid(), "test", scratch, "r", "r", true, Exclusions: "*.bak\ncache/", SpeedLimitKiB: 32);
        db.Save(pair);
        Check(db.Load().Single() == pair, "pair options not persisted");
        var root = Path.Combine(scratch, "policy-local"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(root, "secret.bak"), "backup");
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        await File.WriteAllTextAsync(Path.Combine(root, "cache", "data"), "cache");
        var policy = new SyncPolicy(pair.Exclusions);
        var endpoint = new PolicyEndpoint(new LocalEndpoint(root, Path.Combine(scratch, "policy-recovery")), policy);
        using var locked = new FileStream(Path.Combine(root, "secret.bak"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scan = await endpoint.ScanAsync(default);
        Check(scan.IsComplete && scan.Entries.Single().Path == "keep.txt" && scan.Unsupported.Count == 2, "excluded locked file or folder scanned");
        var other = new ScanResult([new("secret.bak", EntryKind.File, "old")], []);
        var plan = SyncPlanner.Compare(scan, other, other.Entries);
        Check(plan.Operations.All(x => x.Path != "secret.bak"), "excluded file planned as deletion");
        var rejected = false;
        try { await endpoint.PutFileAsync("new.bak", null, new MemoryStream(), "hash", default); }
        catch (SyncPreconditionException) { rejected = true; }
        Check(rejected, "saved operation bypassed exclusion");
        var journal = new SyncJournal(Path.Combine(scratch, "policy-journal.db"));
        journal.Enqueue(pair.Id, new([new("old.bak", SyncAction.Upload, new("old.bak", EntryKind.File, "hash"), null, "queued")], []));
        journal.SkipExcluded(pair.Id, policy.Exclusions);
        Check(journal.ReadJobs(pair.Id).Single().State == JobState.Completed, "excluded saved job blocked the queue");
        var limiter = new BandwidthLimiter(64);
        using var payload = new ByteArrayContent(new byte[32768]);
        using var limited = limiter.Limit(payload);
        using var destination = new MemoryStream();
        var timer = Stopwatch.StartNew();
        await limited.CopyToAsync(destination);
        Check(timer.Elapsed >= TimeSpan.FromMilliseconds(450) && destination.Length == 32768, "upload body bypassed rate limit or lost bytes");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var stopped = false;
        try { await new BandwidthLimiter(1).WaitAsync(65536, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "throttle could not be cancelled");
        using var unlimitedSource = new MemoryStream(new byte[50000]); using var unlimitedTarget = new MemoryStream();
        await new BandwidthLimiter(0).CopyAsync(unlimitedSource, unlimitedTarget, default);
        Check(unlimitedTarget.Length == 50000, "unlimited transfer failed");
        Console.WriteLine("PASS: per-pair persisted exclusions and speed, locked excluded files, subtree/deletion safety, saved-job skip, upload throttling and cancellation");
    }
}
