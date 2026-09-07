using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class LocalEndpointChecks
{
    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        static MemoryStream Bytes(string value) => new(Encoding.UTF8.GetBytes(value));
        static async Task Reject(Func<Task> action, string message)
        { try { await action(); } catch (IOException) { return; } throw new Exception(message); }
        var area = Path.Combine(scratch, "real-endpoints");
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
        Directory.CreateDirectory(Path.Combine(leftRoot, "빈 폴더"));
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "한글.txt"), "left");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "remote.txt"), "right");
        await Sync();
        Check(await File.ReadAllTextAsync(Path.Combine(rightRoot, "한글.txt")) == "left" && await File.ReadAllTextAsync(Path.Combine(leftRoot, "remote.txt")) == "right", "disk roundtrip failed");
        Check(Directory.Exists(Path.Combine(rightRoot, "빈 폴더")), "empty folder missing");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "한글.txt"), "updated"); await Sync();
        var replacement = right.ReadRecovery().Single(x => x.Action == "replace");
        Check(replacement.Verified && await File.ReadAllTextAsync(replacement.BackupPath) == "left", "replacement original missing");
        File.Delete(Path.Combine(leftRoot, "한글.txt")); Directory.Delete(Path.Combine(leftRoot, "빈 폴더")); await Sync();
        var deletion = right.ReadRecovery().Single(x => x.Action == "delete" && x.Expected.Kind == EntryKind.File);
        Check(deletion.Verified && await File.ReadAllTextAsync(deletion.BackupPath) == "updated", "deleted file recovery missing");
        Check(!File.Exists(Path.Combine(rightRoot, "한글.txt")) && !Directory.Exists(Path.Combine(rightRoot, "빈 폴더")), "delete not propagated");
        Check(!(await right.ScanAsync(default)).Entries.Any(x => x.Path.Contains("original")), "recovery leaked into sync");
        Console.WriteLine("PASS: real disk bidirectional transfer, replace with retained original, recoverable file/folder deletion");

        await Reject(() => right.PutFileAsync("bad.txt", null, Bytes("bad"), Hash("expected"), default), "wrong hash accepted");
        Check(!File.Exists(Path.Combine(rightRoot, "bad.txt")), "partial published");
        await Reject(() => right.PutFileAsync("../escape", null, Bytes("x"), Hash("x"), default), "traversal accepted");
        await Reject(() => right.PutFileAsync("CON.txt", null, Bytes("x"), Hash("x"), default), "device path accepted");
        await Reject(() => right.PutFileAsync("trailing.", null, Bytes("x"), Hash("x"), default), "ambiguous name accepted");
        await Reject(() => right.PutFileAsync("remote.txt", null, Bytes("x"), Hash("x"), default), "create overwrote file");
        var stale = new SyncEntry("remote.txt", EntryKind.File, Hash("wrong"));
        await Reject(() => right.PutFileAsync("remote.txt", stale, Bytes("x"), Hash("x"), default), "stale overwrite accepted");
        await Reject(() => right.DeleteAsync(stale, default), "stale delete accepted");
        Check(await File.ReadAllTextAsync(Path.Combine(rightRoot, "remote.txt")) == "right", "precondition changed original");
        Directory.CreateDirectory(Path.Combine(rightRoot, "nonempty")); await File.WriteAllTextAsync(Path.Combine(rightRoot, "nonempty", "child"), "keep");
        await Reject(() => right.DeleteAsync(new("nonempty", EntryKind.Directory, null), default), "recursive delete accepted");
        Check(File.Exists(Path.Combine(rightRoot, "nonempty", "child")), "child lost");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await right.PutFileAsync("cancelled", null, Bytes("x"), Hash("x"), cancelled.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }
        Check(!Directory.EnumerateFiles(rightRecovery, "*.partial").Any(), "temporary files remain");
        Console.WriteLine("PASS: hash mismatch, unsafe paths, stale writes/deletes, nonempty folder and cancellation rejected");

        // Simulate process termination after durable intent, before verification.
        var pending = new RecoveryRecord(rightRoot, "remote.txt", "replace", new("remote.txt", EntryKind.File, Hash("right")), Path.Combine(rightRecovery, "interrupted.original"), false);
        await File.WriteAllTextAsync(Path.Combine(rightRecovery, "interrupted.json"), JsonSerializer.Serialize(pending));
        var restarted = new LocalEndpoint(rightRoot, rightRecovery);
        Check(!(await restarted.ScanAsync(default)).IsComplete, "unverified operation treated as complete");
        await Reject(() => restarted.PutFileAsync("blocked", null, Bytes("x"), Hash("x"), default), "unverified operation allowed writes");
        Console.WriteLine("PASS: persistent unverified recovery record blocks scans and writes after restart");

        var conflictLeft = Path.Combine(area, "conflict-left"); var conflictRight = Path.Combine(area, "conflict-right");
        Directory.CreateDirectory(conflictLeft); Directory.CreateDirectory(conflictRight);
        await File.WriteAllTextAsync(Path.Combine(conflictLeft, "file"), "one"); await File.WriteAllTextAsync(Path.Combine(conflictRight, "file"), "two");
        var cl = new LocalEndpoint(conflictLeft, Path.Combine(area, "cl-recovery")); var cr = new LocalEndpoint(conflictRight, Path.Combine(area, "cr-recovery"));
        var cp = Guid.NewGuid(); journal.Enqueue(cp, SyncPlanner.Compare(await cl.ScanAsync(default), await cr.ScanAsync(default), []));
        var executor = new SyncExecutor(journal); Check(!(await executor.RunAsync(cp, cl, cr)).Converged, "conflict silently completed");
        Check(Directory.GetFiles(conflictLeft).Length == 3 && Directory.GetFiles(conflictRight).Length == 3, "disk conflict copies missing");
        await executor.RunAsync(cp, cl, cr);
        Check(Directory.GetFiles(conflictLeft).Length == 3 && await File.ReadAllTextAsync(Path.Combine(conflictLeft, "file")) == "one", "disk conflict retry changed originals");
        Console.WriteLine("PASS: real disk conflict preservation and idempotent retry");
    }
}
