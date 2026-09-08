using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;
using System.Text.Json;

internal static class TransferControlChecks
{
    private sealed class Reports(Action<SyncProgress> action) : IProgress<SyncProgress> { public void Report(SyncProgress value) => action(value); }
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        var root = Path.Combine(scratch, "file-control"); var leftRoot = Path.Combine(root, "left"); var rightRoot = Path.Combine(root, "right");
        Directory.CreateDirectory(leftRoot); Directory.CreateDirectory(rightRoot);
        await File.WriteAllBytesAsync(Path.Combine(leftRoot, "a.bin"), new byte[200000]);
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "b.txt"), "second");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "c.txt"), "third");
        var left = new LocalEndpoint(leftRoot, Path.Combine(root, "left-recovery")); var right = new LocalEndpoint(rightRoot, Path.Combine(root, "right-recovery"));
        var journal = new SyncJournal(Path.Combine(root, "journal.db")); var pair = Guid.NewGuid();
        journal.Enqueue(pair, SyncPlanner.Compare(await left.ScanAsync(default), await right.ScanAsync(default), []));
        var control = new TransferControl();
        control.Hold("folder", "중지"); Check(control.State("folder/child.txt") == "중지", "folder hold missed child"); control.Resume("folder");
        control.Hold("b.txt", "취소");
        var queued = new HashSet<string>();
        var reports = new Reports(report =>
        {
            if (report.Event == TransferEvent.Queued) queued.Add(report.Path!);
            if (report.Path == "a.bin" && report.Bytes > 0) control.Hold("a.bin", "중지");
        });
        var result = await new SyncExecutor(journal).RunAsync(pair, left, right, progress: reports, control: control);
        Check(result.UserDeferred && !result.Converged && queued.Count == 3, "queue not exposed or user hold treated as success");
        Check(!File.Exists(Path.Combine(rightRoot, "a.bin")) && !File.Exists(Path.Combine(rightRoot, "b.txt")) && File.Exists(Path.Combine(rightRoot, "c.txt")), "file controls stopped unrelated job or published partial content");
        control.Resume("a.bin"); control.Resume("b.txt");
        result = await new SyncExecutor(journal).RunAsync(pair, left, right, control: control);
        Check(result.Converged && new FileInfo(Path.Combine(rightRoot, "a.bin")).Length == 200000, "resume did not recheck and complete");
        var directory = SyncDiagnostics.DirectoryPath; var limit = SyncDiagnostics.MaxBytes;
        try
        {
            SyncDiagnostics.DirectoryPath = Path.Combine(root, "logs"); SyncDiagnostics.MaxBytes = 2048;
            for (var i = 0; i < 100; i++) SyncDiagnostics.Write("event-" + i);
            var file = Path.Combine(SyncDiagnostics.DirectoryPath, "sync.jsonl");
            Check(new FileInfo(file).Length <= 2048, "log exceeded configured cap");
            var lines = File.ReadAllLines(file);
            foreach (var line in lines) using (JsonDocument.Parse(line)) { }
            Check(lines[^1].Contains("event-99") && !lines.Any(x => x.Contains("\"event-0\"")), "log did not discard oldest and retain newest");
            SyncDiagnostics.MaxBytes = 1024; SyncDiagnostics.Write("smaller");
            Check(new FileInfo(file).Length <= 1024, "reduced limit not enforced");
            SyncDiagnostics.MaxBytes = 8192;
            var compacted = false;
            for (var i = 0; i < 100; i++)
            {
                var oldSize = new FileInfo(file).Length;
                SyncDiagnostics.Write("capacity-" + i);
                var newSize = new FileInfo(file).Length;
                if (newSize >= oldSize) continue;
                compacted = true;
                Check(newSize < 8192 * 0.85, "log compaction left no append headroom");
                SyncDiagnostics.Write("append-after-compaction");
                Check(new FileInfo(file).Length > newSize, "next log entry recopied the capped log again");
                break;
            }
            Check(compacted, "log compaction test did not reach the cap");
        }
        finally { SyncDiagnostics.DirectoryPath = directory; SyncDiagnostics.MaxBytes = limit; }
        Console.WriteLine("PASS: queued/current per-file controls, unrelated transfer continuation, safe resume, bounded rolling JSONL logs");
    }
}
