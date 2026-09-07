using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class UnsupportedChecks
{
    /// <summary>Adds a reported-but-unhandled item to a real endpoint's scan, as a provider would.</summary>
    private sealed class Reporting(ISyncEndpoint inner, params UnsupportedItem[] items) : ISyncEndpoint
    {
        public async Task<ScanResult> ScanAsync(CancellationToken ct)
        {
            var scan = await inner.ScanAsync(ct);
            return scan with { Unsupported = [.. scan.Unsupported, .. items] };
        }
        public Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct) => inner.OpenReadAsync(expected, ct);
        public Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct) => inner.PutFileAsync(path, expected, content, sha256, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(SyncEntry expected, CancellationToken ct) => inner.DeleteAsync(expected, ct);
    }

    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static SyncEntry File(string path, string hash) => new(path, EntryKind.File, hash);
        static SyncEntry Dir(string path) => new(path, EntryKind.Directory, null);

        // One unhandled item must not stop the rest of the pair.
        var local = new ScanResult([File("a.txt", "H1")], [], [new("link", "링크·정션은 동기화하지 않습니다.")]);
        var remote = new ScanResult([File("a.txt", "H1"), File("b.txt", "H2")], []);
        var plan = SyncPlanner.Compare(local, remote, []);
        Check(plan.CanExecute, "an unsupported item blocked the plan");
        Check(plan.Operations.Single().Path == "b.txt" && plan.Operations.Single().Action == SyncAction.Download, "the rest of the folder was not planned");
        Check(plan.Unsupported.Single().Path == "link", "the unsupported item was not reported");
        Check(local.IsComplete, "an unsupported item made the scan look incomplete");

        // The subtree of an unhandled folder was never scanned, so nothing under it may be planned - especially not deletions.
        var baseline = new[] { Dir("linked"), File("linked/child.txt", "H3"), File("keep.txt", "H4") };
        var withLink = new ScanResult([File("keep.txt", "H4")], [], [new("linked", "링크·정션은 동기화하지 않습니다.")]);
        var full = new ScanResult([Dir("linked"), File("linked/child.txt", "H3"), File("keep.txt", "H4")], []);
        var subtree = SyncPlanner.Compare(withLink, full, baseline);
        Check(subtree.CanExecute && subtree.Operations.Count == 0, "an unscanned subtree produced work: " + string.Join(" / ", subtree.Operations.Select(x => x.Path + "=" + x.Action)));
        Check(subtree.Unsupported.Single().Path == "linked", "the unsupported folder was not reported");

        // Names Windows cannot store are reported instead of planned, and their subtree is excluded too.
        var impossible = new ScanResult([File("ok.txt", "H5")], []);
        var awkward = new ScanResult([File("ok.txt", "H5"), File("aux.txt", "H6"), Dir("trailing."), File("trailing./x.txt", "H7"), File("a:b.txt", "H8")], []);
        var names = SyncPlanner.Compare(impossible, awkward, []);
        Check(!names.CanExecute, "a path with a reserved character must still be a scan error");
        var awkward2 = new ScanResult([File("ok.txt", "H5"), File("aux.txt", "H6"), Dir("trailing."), File("trailing./x.txt", "H7")], []);
        var names2 = SyncPlanner.Compare(impossible, awkward2, []);
        Check(names2.CanExecute && names2.Operations.Count == 0, "unrepresentable names produced work");
        Check(names2.Unsupported.Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["aux.txt", "trailing."]), "unrepresentable names were not reported: " + string.Join(",", names2.Unsupported.Select(x => x.Path)));

        // A genuinely incomplete scan still blocks everything.
        var broken = SyncPlanner.Compare(new ScanResult([], ["원격 검사 실패"]), remote, []);
        Check(!broken.CanExecute && broken.Operations.Count == 0, "an incomplete scan no longer blocks planning");
        Console.WriteLine("PASS: unsupported items reported without blocking, subtrees excluded, incomplete scans still block");

        // End to end: the pair converges, transfers everything else, and reports the item.
        var area = Path.Combine(scratch, "unsupported");
        var leftRoot = Path.Combine(area, "left"); var rightRoot = Path.Combine(area, "right");
        Directory.CreateDirectory(leftRoot); Directory.CreateDirectory(rightRoot);
        await System.IO.File.WriteAllTextAsync(Path.Combine(leftRoot, "normal.txt"), "payload");
        ISyncEndpoint left = new LocalEndpoint(leftRoot, Path.Combine(area, "left-recovery"));
        ISyncEndpoint right = new Reporting(new LocalEndpoint(rightRoot, Path.Combine(area, "right-recovery")), new UnsupportedItem("문서", "이 항목은 파일로 내려받을 수 없습니다."));
        var journal = new SyncJournal(Path.Combine(area, "journal.db"));
        var pair = Guid.NewGuid();
        journal.Enqueue(pair, SyncPlanner.Compare(await left.ScanAsync(default), await right.ScanAsync(default), []));
        var report = await new SyncExecutor(journal).RunAsync(pair, left, right);
        Check(report.Converged, "an unsupported remote item prevented convergence: " + string.Join(" / ", report.Issues));
        Check(await System.IO.File.ReadAllTextAsync(Path.Combine(rightRoot, "normal.txt")) == "payload", "the normal file was not transferred");
        Check(report.Notices.Single().StartsWith("문서:", StringComparison.Ordinal), "the unsupported item was not reported to the caller");
        var again = await new SyncExecutor(journal).RunAsync(pair, left, right);
        Check(again.Converged && again.Notices.Count == 1, "a repeat run did not stay converged with the same notice");
        Console.WriteLine("PASS: pair converges with an unsupported remote item, transfers the rest and reports it every run");
    }
}
