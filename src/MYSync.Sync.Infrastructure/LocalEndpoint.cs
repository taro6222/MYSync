using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MYSync.Sync.Core;

namespace MYSync.Sync.Infrastructure;

public sealed record RecoveryRecord(string Root, string RelativePath, string Action, SyncEntry Expected, string BackupPath, bool Verified);

/// <summary>Local filesystem adapter with retained originals. Recovery must be on the same volume, outside sync roots.</summary>
public sealed class LocalEndpoint : ISyncEndpoint
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;
    private readonly string recovery;
    private readonly SemaphoreSlim gate;
    // Advisory only. Writes and deletes always re-hash the real file before acting.
    private readonly ConcurrentDictionary<string, FileFingerprint> fingerprints = new(StringComparer.OrdinalIgnoreCase);
    public LocalEndpoint(string root, string recoveryDirectory)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        recovery = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recoveryDirectory));
        if (Contains(this.root, recovery) || Contains(recovery, this.root)) throw new ArgumentException("복구 폴더는 동기화 폴더와 분리해야 합니다.");
        if (!string.Equals(Path.GetPathRoot(this.root), Path.GetPathRoot(recovery), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("복구 폴더는 같은 볼륨에 있어야 합니다.");
        if (!Directory.Exists(this.root)) throw new DirectoryNotFoundException(this.root);
        CheckAncestors(this.root);
        CheckAncestors(recovery);
        Directory.CreateDirectory(recovery);
        gate = Gates.GetOrAdd(this.root, _ => new SemaphoreSlim(1, 1));
    }
    public async Task<ScanResult> ScanAsync(CancellationToken ct)
    {
        try
        {
            CheckAncestors(root); CheckAncestors(recovery);
            var pending = ReadRecovery().Where(x => !x.Verified).ToArray();
            if (pending.Length > 0) return new([], ["확인되지 않은 파일 작업이 있습니다. 복구 기록을 확인하세요: " + recovery]);
            return await new LocalScanner(fingerprints).ScanAsync(root, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new([], [ex.Message]); }
    }
    public IReadOnlyList<RecoveryRecord> ReadRecovery()
    {
        CheckAncestors(recovery);
        return Directory.EnumerateFiles(recovery, "*.json").Select(p => JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(p)) ?? throw new InvalidDataException("복구 기록 오류"))
            .Where(x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public async Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct)
    {
        if (expected.Kind != EntryKind.File || expected.ContentHash is null) throw new SyncPreconditionException("파일 해시가 필요합니다.");
        var path = Resolve(expected.Path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (hash != expected.ContentHash) throw new SyncPreconditionException("읽기 대상이 변경되었습니다.");
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    public async Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            EnsureNoPending();
            var target = Resolve(path);
            if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("부모 폴더가 없습니다.");
            if (expected is not null && (expected.Path != path || expected.Kind != EntryKind.File)) throw new SyncPreconditionException("예상 파일 정보 오류");
            CheckAncestors(recovery);
            staging = Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".partial");
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, ct);
                await output.FlushAsync(ct); output.Flush(true); output.Position = 0;
                if (Convert.ToHexString(await SHA256.HashDataAsync(output, ct)) != sha256) throw new SyncPreconditionException("전송 무결성 검사 실패");
            }
            ct.ThrowIfCancellationRequested();
            if (await Inspect(path, ct) != expected) throw new SyncPreconditionException("대상 파일이 변경되었습니다.");
            target = Resolve(path);
            if (expected is null) File.Move(staging, target, false);
            else
            {
                var (recordPath, record) = BeginRecovery(path, "replace", expected);
                // Preserve the actual file replaced, including a late external edit.
                File.Replace(staging, target, record.BackupPath);
                if (await HashFile(record.BackupPath, CancellationToken.None) != expected.ContentHash)
                    throw new SyncPreconditionException("교체 직전 변경된 원본을 복구 폴더에 보존했습니다.");
                SaveRecord(recordPath, record with { Verified = true });
            }
        }
        finally
        {
            try { if (staging is not null && File.Exists(staging)) File.Delete(staging); }
            finally { gate.Release(); }
        }
    }
    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureNoPending(); var target = Resolve(path); ct.ThrowIfCancellationRequested();
            if (File.Exists(target) || Directory.Exists(target)) throw new SyncPreconditionException("항목이 이미 존재합니다.");
            if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("부모 폴더가 없습니다.");
            // Directory.Move provides create-only publication, unlike CreateDirectory(target).
            var temp = Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".directory");
            Directory.CreateDirectory(temp);
            try { Directory.Move(temp, target); }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, false); }
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(SyncEntry expected, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureNoPending();
            if (await Inspect(expected.Path, ct) != expected) throw new SyncPreconditionException("삭제 대상이 변경되었습니다.");
            var target = Resolve(expected.Path); ct.ThrowIfCancellationRequested();
            if (expected.Kind == EntryKind.Directory && Directory.EnumerateFileSystemEntries(target).Any()) throw new SyncPreconditionException("비어 있지 않은 폴더입니다.");
            var (recordPath, record) = BeginRecovery(expected.Path, "delete", expected);
            if (expected.Kind == EntryKind.File)
            {
                File.Move(target, record.BackupPath, false);
                if (await HashFile(record.BackupPath, CancellationToken.None) != expected.ContentHash) throw new SyncPreconditionException("삭제 직전 변경된 파일을 복구 폴더에 보존했습니다.");
            }
            else
            {
                Directory.Move(target, record.BackupPath);
                if (Directory.EnumerateFileSystemEntries(record.BackupPath).Any()) throw new SyncPreconditionException("삭제 직전 추가된 폴더 내용을 복구 폴더에 보존했습니다.");
            }
            SaveRecord(recordPath, record with { Verified = true });
        }
        finally { gate.Release(); }
    }
    private void EnsureNoPending()
    {
        if (ReadRecovery().Any(x => !x.Verified)) throw new SyncPreconditionException("미확인 복구 기록이 있어 쓰기를 중단합니다.");
    }
    private (string Path, RecoveryRecord Record) BeginRecovery(string path, string action, SyncEntry expected)
    {
        CheckAncestors(recovery);
        var id = Guid.NewGuid().ToString("N");
        var record = new RecoveryRecord(root, path, action, expected, Path.Combine(recovery, id + ".original"), false);
        var recordPath = Path.Combine(recovery, id + ".json"); SaveRecord(recordPath, record); return (recordPath, record);
    }
    private static void SaveRecord(string path, RecoveryRecord record)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, record); file.Flush(true); }
        File.Move(temp, path, true);
    }
    private async Task<SyncEntry?> Inspect(string path, CancellationToken ct)
    {
        var target = Resolve(path);
        if (Directory.Exists(target)) return new(path, EntryKind.Directory, null);
        if (!File.Exists(target)) return null;
        return new(path, EntryKind.File, await HashFile(target, ct));
    }
    private static async Task<string> HashFile(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }
    private string Resolve(string relative)
    {
        var parts = relative.Split('/');
        if (string.IsNullOrWhiteSpace(relative) || parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(p)))
            throw new SyncPreconditionException("지원하지 않는 상대 경로: " + relative);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!Contains(root, full) || full == root) throw new SyncPreconditionException("루트 외부 경로");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        CheckAncestors(full); return full;
    }
    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]);
    }
    private static bool Contains(string parent, string path) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void CheckAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new SyncPreconditionException("링크 경로는 지원하지 않습니다: " + current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
