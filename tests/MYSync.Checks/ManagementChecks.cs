using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class ManagementChecks
{
    private sealed class Collector : IProgress<SyncProgress>
    {
        public readonly List<SyncProgress> Items = [];
        public void Report(SyncProgress value) { lock (Items) Items.Add(value); }
        public SyncProgress[] Snapshot() { lock (Items) return [.. Items]; }
    }

    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Reject(Action action, string message)
        { try { action(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; } throw new Exception(message); }

        var db = Path.Combine(scratch, "account-management.db");
        var store = new AccountStore(db);
        var account = new SavedAccount(Guid.NewGuid(), "mysync.webdav", "WebDAV · 최초 이름");
        store.Save(account, new Dictionary<string, string> { ["url"] = "https://nas.example.com/webdav/", ["username"] = "user", ["password"] = "first-secret" });

        store.Rename(account.Id, "  창고 NAS  ");
        Check(store.List().Single().DisplayName == "창고 NAS", "rename did not persist or trim");
        Check(store.ReadValues(account)["password"] == "first-secret", "rename broke decryption");
        Reject(() => store.Rename(account.Id, "   "), "empty account name accepted");
        Reject(() => store.Rename(Guid.NewGuid(), "없는 계정"), "rename of unknown account accepted");

        store.Save(account, new Dictionary<string, string> { ["url"] = "https://nas.example.com/webdav/", ["username"] = "user", ["password"] = "second-secret" });
        var reopened = new AccountStore(db);
        Check(reopened.List().Single().Id == account.Id, "credential update changed the account identity");
        Check(reopened.ReadValues(account)["password"] == "second-secret", "credential update did not persist");
        Reject(() => store.Save(account with { ProviderId = "other.provider" }, new Dictionary<string, string>()), "provider change accepted");

        reopened.Delete(account.Id);
        Check(new AccountStore(db).List().Count == 0, "account not removed");
        Reject(() => new AccountStore(db).ReadValues(account), "values readable after deletion");
        Reject(() => new AccountStore(db).Delete(account.Id), "repeated deletion accepted");
        Console.WriteLine("PASS: account rename, credential update keeping identity, deletion and invalid edits rejected");

        var area = Path.Combine(scratch, "progress");
        var leftRoot = Path.Combine(area, "left"); var rightRoot = Path.Combine(area, "right");
        Directory.CreateDirectory(leftRoot); Directory.CreateDirectory(rightRoot);
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(leftRoot, "big.bin"), payload);
        var left = new LocalEndpoint(leftRoot, Path.Combine(area, "left-recovery"));
        var right = new LocalEndpoint(rightRoot, Path.Combine(area, "right-recovery"));
        var journal = new SyncJournal(Path.Combine(area, "journal.db"));
        var pair = Guid.NewGuid();
        journal.Enqueue(pair, SyncPlanner.Compare(await left.ScanAsync(default), await right.ScanAsync(default), []));
        var collector = new Collector();
        var report = await new SyncExecutor(journal).RunAsync(pair, left, right, default, collector);
        Check(report.Converged, "progress run did not converge: " + string.Join(" / ", report.Issues));
        var reports = collector.Snapshot();
        Check(reports.Any(x => x.Phase == SyncPhase.Transferring && x.Path == "big.bin" && x.Action == SyncAction.Upload), "no upload progress reported");
        Check(reports.Where(x => x.Path == "big.bin").Max(x => x.Bytes) == payload.Length, "reported bytes do not match the transferred file");
        Check(reports.Any(x => x.Phase == SyncPhase.Verifying), "convergence check not reported");
        var history = new MYSync.Desktop.TransferHistory();
        foreach (var item in reports) history.Apply(pair, "test", item);
        Check(history.Rows.Single().Done && history.Rows.Single().State == "완료", "file history not finalized after verification");
        Check(history.SessionBytes == payload.Length && history.CompletedFiles == 1, "history counts do not match real transfer");
        Check(history.Rows.Single().Speed.EndsWith("/s") && history.Rows.Single().Finished != "—", "speed or timestamps absent");
        var last = reports[^1];
        Check(last.Phase == SyncPhase.Idle && last.Total == 1 && last.Completed == 1, "final progress did not report every operation as done");
        Check(await File.ReadAllBytesAsync(Path.Combine(rightRoot, "big.bin")) is { Length: 300_000 }, "file not transferred");
        Console.WriteLine("PASS: executor reports operation counts, transferred bytes and completion without affecting the result");

        var idle = new Collector();
        var second = await new SyncExecutor(journal).RunAsync(pair, left, right, default, idle);
        Check(second.Converged && idle.Snapshot()[0].Total == 0, "an empty queue was not reported as having no work");
        Console.WriteLine("PASS: empty queue reported as no work and still converges");
    }
}
