using MYSync.Sync.Infrastructure;

internal static class ScanCacheChecks
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var root = Path.Combine(scratch, "cache-expiration"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "same.txt"); await File.WriteAllTextAsync(path, "aaaa");
        var clock = new Clock(); var cache = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        var scanner = new LocalScanner(cache, clock);
        var initial = (await scanner.ScanAsync(root)).Entries.Single();
        var stamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "bbbb"); File.SetLastWriteTimeUtc(path, stamp);
        clock.Now += TimeSpan.FromMinutes(4);
        Check((await scanner.ScanAsync(root)).Entries.Single() == initial, "cache not reused within lifetime");
        clock.Now += TimeSpan.FromMinutes(1);
        var refreshed = (await scanner.ScanAsync(root)).Entries.Single();
        Check(refreshed.ContentHash != initial.ContentHash, "same-size timestamp-preserved edit not detected at expiry");
        Check(cache[path].VerifiedAt == clock.Now, "verification time not refreshed");
        await File.WriteAllTextAsync(path, "cccc"); File.SetLastWriteTimeUtc(path, stamp);
        clock.Now -= TimeSpan.FromHours(1);
        Check((await scanner.ScanAsync(root)).Entries.Single().ContentHash != refreshed.ContentHash, "clock rollback reused a future cache entry");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            clock.Now += TimeSpan.FromHours(2);
            Check(!(await scanner.ScanAsync(root)).IsComplete && cache.ContainsKey(path), "failed scan pruned cache");
        }
        File.Delete(path);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await scanner.ScanAsync(root, cancelled.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { }
            Check(cache.ContainsKey(path), "cancelled scan pruned cache");
        }
        Check((await scanner.ScanAsync(root)).IsComplete && cache.Count == 0, "deleted file cache not pruned");
        Console.WriteLine("PASS: bounded local hash cache detects preserved-metadata edits, handles clock rollback, prunes only after complete scans");
    }
}
