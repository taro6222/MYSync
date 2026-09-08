using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class FileResolutionChecks
{
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var area = Path.Combine(scratch, "file-resolution");
        var lroot = Path.Combine(area, "left"); var rroot = Path.Combine(area, "right");
        Directory.CreateDirectory(lroot); Directory.CreateDirectory(rroot);
        var local = new LocalEndpoint(lroot, Path.Combine(area, "left-recovery"));
        var remote = new LocalEndpoint(rroot, Path.Combine(area, "right-recovery"));
        var journal = new SyncJournal(Path.Combine(area, "journal.db")); var pair = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(lroot, "file.txt"), "local");
        await File.WriteAllTextAsync(Path.Combine(rroot, "file.txt"), "remote");
        await File.WriteAllTextAsync(Path.Combine(lroot, "other.txt"), "other");
        var result = await FileResolution.ApplyAsync(journal, pair, "file.txt", true, local, remote);
        Check(await File.ReadAllTextAsync(Path.Combine(rroot, "file.txt")) == "local", "explicit local overwrite failed");
        Check(!File.Exists(Path.Combine(rroot, "other.txt")), "one-file resolution executed an unrelated job");
        var copies = Directory.GetFiles(lroot, "file.txt.conflict-*");
        Check(copies.Length == 2 && copies.Select(File.ReadAllText).Order().SequenceEqual(new[] { "local", "remote" }), "original versions not preserved");
        Check(Directory.GetFiles(rroot, "file.txt.conflict-*").Length == 2, "remote preservation copies missing");
        await File.WriteAllTextAsync(Path.Combine(rroot, "file.txt"), "remote-update");
        await FileResolution.ApplyAsync(journal, pair, "file.txt", false, local, remote);
        Check(await File.ReadAllTextAsync(Path.Combine(lroot, "file.txt")) == "remote-update", "explicit remote overwrite on ordinary update failed");
        var before = Directory.GetFiles(lroot).Length + Directory.GetFiles(rroot).Length;
        await FileResolution.ApplyAsync(journal, pair, "file.txt", true, local, remote);
        Check(Directory.GetFiles(lroot).Length + Directory.GetFiles(rroot).Length == before, "identical file created redundant copies");
        var control = new TransferControl(); control.SkipOnce("other.txt");
        var report = await new SyncExecutor(journal).RunAsync(pair, local, remote, control: control);
        Check(report.UserDeferred && !File.Exists(Path.Combine(rroot, "other.txt")) && control.State("other.txt") is null,
            "one-run exclusion did not skip exactly one run");
        Check((await new SyncExecutor(journal).RunAsync(pair, local, remote, control: control)).Converged &&
            File.Exists(Path.Combine(rroot, "other.txt")), "once-excluded file was not eligible next run");

        var stalePair = Guid.NewGuid();
        var old = new SyncEntry("same.txt", EntryKind.File, "OLD");
        journal.Enqueue(stalePair, new([new("same.txt", SyncAction.Conflict, old, old with { ContentHash = "OTHER" }, "stale")], []));
        var source = new MemoryEndpoint(); source.Seed("same.txt", "now-equal");
        var destination = new MemoryEndpoint(); destination.Seed("same.txt", "now-equal");
        Check((await new SyncExecutor(journal).RunAsync(stalePair, source, destination)).Converged,
            "stale conflict remained attention after both sides became identical");
        Console.WriteLine("PASS: explicit file directions, preserved originals, one-file scope, identical no-op, one-run exclusion and stale equal conflict resolution");
    }
}
