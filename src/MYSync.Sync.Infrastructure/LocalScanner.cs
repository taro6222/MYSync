using System.Security.Cryptography;
using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;
public sealed class LocalScanner
{
    public async Task<ScanResult> ScanAsync(string root, CancellationToken cancellationToken = default)
    {
        var entries = new List<SyncEntry>(); var errors = new List<string>();
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return new([], ["로컬 루트에 접근할 수 없습니다: " + root]);
        await Visit(root);
        return new(entries, errors);
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
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { errors.Add("링크 미지원: " + relative); continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        { entries.Add(new(relative, EntryKind.Directory, null)); await Visit(path); }
                        else
                        {
                            var before = new FileInfo(path); var size = before.Length; var stamp = before.LastWriteTimeUtc;
                            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                            var after = new FileInfo(path);
                            if (after.Length != size || after.LastWriteTimeUtc != stamp) { errors.Add("검사 중 파일 변경: " + relative); continue; }
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
