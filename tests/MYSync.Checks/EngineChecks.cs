using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;
internal static class EngineChecks
{
    public static async Task Run(string scratch)
    {
        static SyncEntry F(string p, string h) => new(p, EntryKind.File, h);
        static ScanResult S(params SyncEntry[] entries) => new(entries, []);
        static void Assert(bool ok, string name) { if (!ok) throw new Exception(name); }
        static void Expect(SyncAction action, ScanResult l, ScanResult r, params SyncEntry[] baseline)
        { var p = SyncPlanner.Compare(l, r, baseline); Assert(p.CanExecute && p.Operations.Single().Action == action, action.ToString()); }
        var old = F("a.txt", "old"); var changed = F("a.txt", "new");
        Expect(SyncAction.Upload, S(old), S());
        Expect(SyncAction.Download, S(), S(old));
        Expect(SyncAction.Conflict, S(old), S(changed));
        Expect(SyncAction.Upload, S(changed), S(old), old);
        Expect(SyncAction.Download, S(old), S(changed), old);
        Expect(SyncAction.DeleteRemote, S(), S(old), old);
        Expect(SyncAction.DeleteLocal, S(old), S(), old);
        Expect(SyncAction.Conflict, S(), S(changed), old);
        Expect(SyncAction.Conflict, S(changed), S(F("a.txt", "third")), old);
        Assert(SyncPlanner.Compare(S(), S(), [old]).Operations.Count == 0, "both deleted");
        Assert(SyncPlanner.Compare(S(changed), S(changed), [old]).Operations.Count == 0, "same concurrent edit");
        var incomplete = SyncPlanner.Compare(new([], ["network failure"]), S(old), [old]);
        Assert(!incomplete.CanExecute && incomplete.Operations.Count == 0, "incomplete scan safety");
        var folder = new SyncEntry("dir", EntryKind.Directory, null);
        var child = F("dir/a", "old");
        var folderConflict = SyncPlanner.Compare(S(folder, F("dir/a", "changed")), S(), [folder, child]);
        Assert(folderConflict.Operations.Count == 1 && folderConflict.Operations[0].Action == SyncAction.Conflict, "folder deletion protects changed child");
        var addedChild = SyncPlanner.Compare(S(folder, child, F("dir/new", "new")), S(), [folder, child]);
        Assert(addedChild.Operations.Count == 1 && addedChild.Operations[0].Action == SyncAction.Conflict, "folder deletion protects new child");
        var deletion = SyncPlanner.Compare(S(folder, child), S(), [folder, child]);
        Assert(deletion.Operations[0].Path == "dir/a" && deletion.Operations[1].Path == "dir", "child-first deletion");
        Assert(!SyncPlanner.Compare(S(F("A", "1"), F("a", "2")), S(), []).CanExecute, "case collision");
        Assert(!SyncPlanner.Compare(S(F("../escape", "1")), S(), []).CanExecute, "unsafe path");
        Expect(SyncAction.Conflict, S(new SyncEntry("a.txt", EntryKind.File, null)), S(new SyncEntry("a.txt", EntryKind.File, null)));
        Console.WriteLine("PASS: merge, edits, deletions, conflicts, subtree protection, order, invalid/incomplete snapshots");
        var scanRoot = Path.Combine(scratch, "scan"); Directory.CreateDirectory(Path.Combine(scanRoot, "빈 폴더"));
        await File.WriteAllTextAsync(Path.Combine(scanRoot, "한글.txt"), "first");
        var scanner = new LocalScanner(); var first = await scanner.ScanAsync(scanRoot);
        Assert(first.IsComplete && first.Entries.Count == 2, "recursive scan and empty folder");
        await File.WriteAllTextAsync(Path.Combine(scanRoot, "한글.txt"), "second");
        var second = await scanner.ScanAsync(scanRoot);
        Assert(first.Entries.Single(x => x.Kind == EntryKind.File).ContentHash != second.Entries.Single(x => x.Kind == EntryKind.File).ContentHash, "content detection");
        Assert(!(await scanner.ScanAsync(Path.Combine(scratch, "absent"))).IsComplete, "missing root");
        using (var locked = new FileStream(Path.Combine(scanRoot, "한글.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(!(await scanner.ScanAsync(scanRoot)).IsComplete, "locked file blocks incomplete scan");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await scanner.ScanAsync(scanRoot, cancellation.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }
        Console.WriteLine("PASS: real filesystem recursion, SHA256 changes, missing root, locked file, cancellation");
        var db = Path.Combine(scratch, "journal.db"); var id = Guid.NewGuid(); var journal = new SyncJournal(db);
        journal.CommitConverged(id, S(old), S(old));
        Assert(new SyncJournal(db).ReadBaseline(id).Single() == old, "baseline restore");
        journal.Enqueue(id, SyncPlanner.Compare(S(changed), S(old), [old]));
        var job = new SyncJournal(db).ReadJobs(id).Single();
        Assert(job.Operation.ExpectedRemote == old && job.Operation.ExpectedLocal == changed, "expected state retained");
        Assert(journal.TryStart(job.Id) && !journal.TryStart(job.Id), "single claim");
        var restored = new SyncJournal(db); restored.RecoverInterrupted();
        Assert(restored.ReadJobs(id).Single().State == JobState.NeedsReconcile, "uncertain outcome requires reconciliation");
        try { restored.CommitConverged(id, S(old), S(changed)); throw new Exception("mismatch accepted"); } catch (InvalidOperationException) { }
        Assert(restored.ReadBaseline(id).Single() == old, "failed verification preserves baseline");
        try { restored.Enqueue(id, SyncPlanner.Compare(S(changed), S(old), [old])); throw new Exception("duplicate accepted"); } catch (InvalidOperationException) { }
        restored.CommitConverged(id, S(changed), S(changed));
        Assert(restored.ReadJobs(id).Single().State == JobState.Completed && restored.ReadBaseline(id).Single() == changed, "atomic convergence commit");
        try { restored.Enqueue(Guid.NewGuid(), incomplete); throw new Exception("incomplete accepted"); } catch (InvalidOperationException) { }
        Console.WriteLine("PASS: durable baseline/queue, atomic claim, interrupted recovery, duplicate prevention, verified commit");
    }
}
