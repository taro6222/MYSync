using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class PairManagementChecks
{
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var local = Path.Combine(scratch, "pair-management");
        Directory.CreateDirectory(local);
        var file = Path.Combine(local, "keep.txt");
        await File.WriteAllTextAsync(file, "keep");
        var store = new SettingsStore(Path.Combine(scratch, "pair-management.db"));
        var pair = new SyncPair(Guid.NewGuid(), "sample", local, "remote", "remote", true);
        var duplicate = pair with { Id = Guid.NewGuid() };
        var nested = pair with { Id = Guid.NewGuid(), LocalPath = Path.Combine(local, "child") };
        store.Save(pair); store.Save(duplicate); store.Save(nested);
        Check(store.Load().Count == 3, "Duplicate or nested pair was not persisted");
        store.Delete(duplicate.Id);
        store.Delete(duplicate.Id);
        Check(store.Load().Select(x => x.Id).ToHashSet().SetEquals([pair.Id, nested.Id]), "Delete affected another connection");
        Check(await File.ReadAllTextAsync(file) == "keep", "Deleting a connection altered local data");
        var changed = pair with { Id = Guid.NewGuid(), LocalPath = Path.Combine(local, "new-root"), RemoteFolderId = "new-remote", Exclusions = "*.bak", SpeedLimitKiB = 128 };
        store.Replace(pair.Id, changed);
        Check(store.Load().Count == 2 && store.Load().Contains(changed) && !store.Load().Any(x => x.Id == pair.Id), "Folder edit did not replace identity and settings");
        var missingRejected = false;
        try { store.Replace(pair.Id, changed with { Id = Guid.NewGuid() }); } catch (InvalidOperationException) { missingRejected = true; }
        Check(missingRejected && store.Load().Contains(changed), "Missing edit target was silently added");
        var collisionRejected = false;
        try { store.Replace(changed.Id, changed with { Id = nested.Id }); } catch (Microsoft.Data.Sqlite.SqliteException) { collisionRejected = true; }
        Check(collisionRejected && store.Load().Contains(changed) && store.Load().Contains(nested), "Failed replacement was not atomic");
        Check(await File.ReadAllTextAsync(file) == "keep", "Folder editing moved or deleted local data");
        var recovery = Path.Combine(local, ".MYSync-recovery", "old-pair");
        Directory.CreateDirectory(recovery);
        await File.WriteAllTextAsync(Path.Combine(recovery, "backup.txt"), "retained");
        var scan = await new LocalScanner().ScanAsync(local);
        Check(scan.IsComplete && scan.Entries.Single().Path == "keep.txt", "Recovery content entered local scan");
        Check(scan.Unsupported.Single().Path == ".MYSync-recovery", "Recovery exclusion not reported");
        SyncEntry[] remote = [new(".MYSync-recovery", EntryKind.Directory, null), new(".MYSync-recovery/backup.txt", EntryKind.File, "hash")];
        var plan = SyncPlanner.Compare(new([], []), new(remote, []), remote);
        Check(plan.CanExecute && plan.Operations.Count == 0, "Reserved remote backup was scheduled for deletion");
        plan = SyncPlanner.Compare(new([], []), new(remote, []), []);
        Check(plan.CanExecute && plan.Operations.Count == 0, "Reserved remote backup was scheduled for download");
        var endpoint = new LocalEndpoint(local, Path.Combine(scratch, "pair-recovery"));
        var rejected = false;
        try { await endpoint.CreateDirectoryAsync(".MYSync-recovery/new", CancellationToken.None); }
        catch (IOException) { rejected = true; }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Direct endpoint operation accepted reserved backup path");
        Check(await File.ReadAllTextAsync(Path.Combine(recovery, "backup.txt")) == "retained", "Backup changed");
        Console.WriteLine("PASS: duplicate/nested connections, isolated deletion, retained files, reserved recovery scan and operation protection");
    }
}
