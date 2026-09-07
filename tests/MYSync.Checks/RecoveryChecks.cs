using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class RecoveryChecks
{
    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        static async Task Reject(Func<Task> action, string message)
        { try { await action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { return; } throw new Exception(message); }

        var area = Path.Combine(scratch, "recovery-ui");
        var leftRoot = Path.Combine(area, "left"); var rightRoot = Path.Combine(area, "right");
        Directory.CreateDirectory(leftRoot); Directory.CreateDirectory(rightRoot);
        var leftRecovery = Path.Combine(area, "left-recovery"); var rightRecovery = Path.Combine(area, "right-recovery");
        var left = new LocalEndpoint(leftRoot, leftRecovery); var right = new LocalEndpoint(rightRoot, rightRecovery);
        var journal = new SyncJournal(Path.Combine(area, "journal.db")); var pair = Guid.NewGuid();
        async Task Sync()
        {
            journal.Enqueue(pair, SyncPlanner.Compare(await left.ScanAsync(default), await right.ScanAsync(default), journal.ReadBaseline(pair)));
            var report = await new SyncExecutor(journal).RunAsync(pair, left, right);
            Check(report.Converged, string.Join(" / ", report.Issues));
        }
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "note.txt"), "first");
        await Sync();
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "note.txt"), "second");
        await Sync();

        var store = new RecoveryStore(rightRecovery);
        var applied = (await store.InspectAsync(default)).Single(x => x.Record.Action == "replace");
        Check(applied.Outcome == RecoveryOutcome.AppliedOriginalPreserved && !applied.NeedsReview, "verified replacement misjudged");
        Check(applied.BackupHash == Hash("first") && applied.TargetHash == Hash("second"), "inspection hashes wrong");

        var separate = Path.Combine(area, "restored");
        await Reject(() => store.RestoreCopyAsync(applied, Path.Combine(rightRoot, "sub"), default), "restore into sync root accepted");
        await Reject(() => store.RestoreCopyAsync(applied, rightRecovery, default), "restore into recovery folder accepted");
        var first = await store.RestoreCopyAsync(applied, separate, default);
        var second = await store.RestoreCopyAsync(applied, separate, default);
        Check(first != second && await File.ReadAllTextAsync(first) == "first" && await File.ReadAllTextAsync(second) == "first", "restore overwrote or lost content");
        Check(await File.ReadAllTextAsync(Path.Combine(rightRoot, "note.txt")) == "second", "restore changed the sync target");
        Console.WriteLine("PASS: recovery inspection of an applied replacement, restore outside the sync root without overwriting");

        var acknowledged = await store.AcknowledgeAsync(applied, false, default);
        Check(File.Exists(acknowledged) && !File.Exists(applied.RecordPath), "acknowledgement did not move the record");
        var kept = JsonSerializer.Deserialize<AcknowledgedRecovery>(await File.ReadAllBytesAsync(acknowledged))!;
        Check(await File.ReadAllTextAsync(kept.Record.BackupPath) == "first", "retained original lost on acknowledgement");
        Check(kept.Record.BackupPath.Contains(RecoveryStore.ResolvedFolderName) && !kept.ManuallyReviewed, "acknowledgement metadata wrong");
        Check((await store.InspectAsync(default)).Count == 0, "acknowledged record still blocks");
        await Reject(() => store.AcknowledgeAsync(applied, true, default), "acknowledging a processed record accepted");
        Console.WriteLine("PASS: explicit acknowledgement clears the record, keeps the retained original and rejects repeats");

        // Interrupted before verification: the operation may never have run.
        var target = new SyncEntry("note.txt", EntryKind.File, Hash("second"));
        var notApplied = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(rightRecovery, notApplied + ".json"),
            JsonSerializer.Serialize(new RecoveryRecord(rightRoot, "note.txt", "replace", target, Path.Combine(rightRecovery, notApplied + ".original"), false)));
        var unknown = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(rightRecovery, unknown + ".original"), "unexpected");
        await File.WriteAllTextAsync(Path.Combine(rightRecovery, unknown + ".json"),
            JsonSerializer.Serialize(new RecoveryRecord(rightRoot, "note.txt", "replace", target, Path.Combine(rightRecovery, unknown + ".original"), false)));
        Check(!(await new LocalEndpoint(rightRoot, rightRecovery).ScanAsync(default)).IsComplete, "unverified records did not block scanning");
        var pending = await store.InspectAsync(default);
        var clean = pending.Single(x => x.RecordPath.Contains(notApplied));
        var ambiguous = pending.Single(x => x.RecordPath.Contains(unknown));
        Check(clean.Outcome == RecoveryOutcome.NotApplied && !clean.NeedsReview, "missing backup with intact target misjudged");
        Check(ambiguous.Outcome == RecoveryOutcome.Indeterminate && ambiguous.NeedsReview, "unexpected backup content misjudged");
        await Reject(() => store.AcknowledgeAsync(ambiguous, false, default), "indeterminate record cleared without review");
        await store.AcknowledgeAsync(clean, false, default);
        await store.AcknowledgeAsync(ambiguous, true, default);
        Check(Directory.EnumerateFiles(Path.Combine(rightRecovery, RecoveryStore.ResolvedFolderName), "*.original").Count() == 2, "retained originals not kept after review");
        Check((await new LocalEndpoint(rightRoot, rightRecovery).ScanAsync(default)).IsComplete, "scanning still blocked after acknowledgement");
        Console.WriteLine("PASS: interrupted records classified, review required for indeterminate results, scanning unblocked only after acknowledgement");

        // Acknowledging must fail when the target changed after the inspection the user acted on.
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "stale.txt"), "one");
        await Sync();
        var staleId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(rightRecovery, staleId + ".json"),
            JsonSerializer.Serialize(new RecoveryRecord(rightRoot, "stale.txt", "delete", new SyncEntry("stale.txt", EntryKind.File, Hash("one")), Path.Combine(rightRecovery, staleId + ".original"), false)));
        var stale = (await store.InspectAsync(default)).Single();
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "stale.txt"), "changed outside");
        await Reject(() => store.AcknowledgeAsync(stale, true, default), "acknowledged a record whose target changed after inspection");
        await Reject(() => store.RestoreCopyAsync(stale, Path.Combine(area, "restored-stale"), default), "restored from a changed record");
        Console.WriteLine("PASS: stale inspections rejected for both restore and acknowledgement");

        await ConflictResolution(area);
    }

    private static async Task ConflictResolution(string area)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var leftRoot = Path.Combine(area, "conflict-left"); var rightRoot = Path.Combine(area, "conflict-right");
        Directory.CreateDirectory(Path.Combine(leftRoot, "sub")); Directory.CreateDirectory(Path.Combine(rightRoot, "sub"));
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "sub", "file.txt"), "local wins");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "sub", "file.txt"), "remote loses");
        var left = new LocalEndpoint(leftRoot, Path.Combine(area, "cl-recovery"));
        var right = new LocalEndpoint(rightRoot, Path.Combine(area, "cr-recovery"));
        var journal = new SyncJournal(Path.Combine(area, "conflict-journal.db"));
        var pair = Guid.NewGuid();
        journal.Enqueue(pair, SyncPlanner.Compare(await left.ScanAsync(default), await right.ScanAsync(default), []));
        var executor = new SyncExecutor(journal);
        Check(!(await executor.RunAsync(pair, left, right)).Converged, "conflict silently completed");
        var conflict = journal.ReadJobs(pair).Single(x => x.State == JobState.NeedsReconcile);

        var localScan = await left.ScanAsync(default); var remoteScan = await right.ScanAsync(default);
        try { journal.ResolveConflict(pair, conflict.Id, localScan, new ScanResult([], ["검사 실패"]), true); throw new Exception("resolved with an incomplete scan"); }
        catch (InvalidOperationException) { }
        var resolution = journal.ResolveConflict(pair, conflict.Id, localScan, remoteScan, true);
        Check(resolution.Operation.Action == SyncAction.Upload && resolution.Operation.Path == "sub/file.txt", "resolution produced the wrong operation");
        try { journal.ResolveConflict(pair, conflict.Id, localScan, remoteScan, true); throw new Exception("resolved the same conflict twice"); }
        catch (InvalidOperationException) { }

        var report = await executor.RunAsync(pair, left, right);
        Check(report.Converged, "resolved pair did not converge: " + string.Join(" / ", report.Issues));
        Check(await File.ReadAllTextAsync(Path.Combine(rightRoot, "sub", "file.txt")) == "local wins", "chosen side was not applied");
        Check(await File.ReadAllTextAsync(Path.Combine(leftRoot, "sub", "file.txt")) == "local wins", "chosen side changed");
        Check(Directory.GetFiles(Path.Combine(leftRoot, "sub")).Length == 3 && Directory.GetFiles(Path.Combine(rightRoot, "sub")).Length == 3, "conflict copies removed by resolution");
        var retained = await new RecoveryStore(Path.Combine(area, "cr-recovery")).InspectAsync(default);
        Check(retained.Any(x => x.Record.RelativePath == "sub/file.txt" && x.Outcome == RecoveryOutcome.AppliedOriginalPreserved), "discarded remote content not retained");
        Console.WriteLine("PASS: explicit conflict decision replans through the baseline, converges and retains both versions");
    }
}
