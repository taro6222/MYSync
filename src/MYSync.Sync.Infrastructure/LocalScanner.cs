using System.Security.Cryptography;
using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;
public sealed record FileFingerprint(long Length, DateTime LastWriteUtc, string Hash, DateTimeOffset VerifiedAt = default);
/// <summary>
/// Recursive local scan. An optional fingerprint cache skips re-hashing files whose length and last write time
/// are unchanged. The cache is advisory only: every write and delete re-hashes the real file before acting,
/// so a stale entry can delay detecting a change but can never make the endpoint act on the wrong content.
/// </summary>
public sealed class LocalScanner(IDictionary<string, FileFingerprint>? cache = null, TimeProvider? clock = null, TimeSpan? cacheLifetime = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan lifetime = cacheLifetime ?? TimeSpan.FromMinutes(5);
    public async Task<ScanResult> ScanAsync(string root, CancellationToken cancellationToken = default)
    {
        if (lifetime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
        var entries = new List<SyncEntry>(); var errors = new List<string>(); var unsupported = new List<UnsupportedItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return new([], ["로컬 루트에 접근할 수 없습니다: " + root]);
        await Visit(root);
        // Only a complete traversal proves a cached path is absent. Never prune on cancellation or scan failure.
        if (cache is not null && errors.Count == 0)
            foreach (var key in cache.Keys.ToArray())
                if (!seen.Contains(key)) cache.Remove(key);
        return new(entries, errors, unsupported);
        async Task Visit(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { errors.Add("링크 폴더 미지원: " + directory); return; }
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                        // Reported, not an error: one link must not stop the whole pair, and its target is never followed.
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { unsupported.Add(new(relative, "링크·정션은 동기화하지 않습니다.")); continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        { entries.Add(new(relative, EntryKind.Directory, null)); await Visit(path); }
                        else
                        {
                            var before = new FileInfo(path); var size = before.Length; var stamp = before.LastWriteTimeUtc;
                            seen.Add(path);
                            var now = time.GetUtcNow();
                            if (cache is not null && cache.TryGetValue(path, out var known) && known.Length == size && known.LastWriteUtc == stamp
                                && now >= known.VerifiedAt && now - known.VerifiedAt < lifetime)
                            { entries.Add(new(relative, EntryKind.File, known.Hash)); continue; }
                            string hash;
                            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                            var after = new FileInfo(path);
                            if (after.Length != size || after.LastWriteTimeUtc != stamp) { errors.Add("검사 중 파일 변경: " + relative); continue; }
                            if (cache is not null) cache[path] = new(size, stamp, hash, now);
                            entries.Add(new(relative, EntryKind.File, hash));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(path + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(directory + ": " + ex.Message); }
        }
    }
}
