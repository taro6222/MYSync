using System.Security.Cryptography;
using System.Text.Json;
using MYSync.Sync.Core;

namespace MYSync.Sync.Infrastructure;

public enum RecoveryOutcome { AppliedOriginalPreserved, NotApplied, Indeterminate }

/// <summary>A recovery record compared against the current target and retained original. Recomputed on every read; never inferred from the stored Verified flag alone.</summary>
public sealed record RecoveryInspection(
    string RecordPath, RecoveryRecord Record, RecoveryOutcome Outcome,
    bool BackupExists, string? BackupHash, bool TargetExists, string? TargetHash, string Summary)
{
    public string Action => Record.Action == "replace" ? "교체" : Record.Action == "delete" ? "삭제" : Record.Action;
    public string RelativePath => Record.RelativePath;
    public bool Verified => Record.Verified;
    public string BackupPath => Record.BackupPath;
    public bool NeedsReview => Outcome == RecoveryOutcome.Indeterminate;
    public string OutcomeText => Outcome switch
    {
        RecoveryOutcome.AppliedOriginalPreserved => "적용됨 · 원본 보관",
        RecoveryOutcome.NotApplied => "적용 안 됨 · 대상 유지",
        _ => "판정 불가 · 확인 필요"
    };
}

public sealed record AcknowledgedRecovery(RecoveryRecord Record, string Outcome, string Summary, DateTimeOffset AcknowledgedAt, bool ManuallyReviewed);

/// <summary>
/// Reads, restores and acknowledges recovery records. Acknowledging never discards a retained original:
/// the backup and a durable acknowledgement are moved into the resolved folder, out of the blocking set.
/// </summary>
public sealed class RecoveryStore(string recoveryDirectory)
{
    public const string ResolvedFolderName = "resolved";
    private readonly string location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recoveryDirectory));
    public string Location => location;
    public string ResolvedLocation => Path.Combine(location, ResolvedFolderName);

    public async Task<IReadOnlyList<RecoveryInspection>> InspectAsync(CancellationToken ct)
    {
        if (!Directory.Exists(location)) return [];
        var result = new List<RecoveryInspection>();
        foreach (var path in Directory.EnumerateFiles(location, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            result.Add(await InspectOneAsync(path, ct));
        return result;
    }

    /// <summary>Copies the retained original to a location outside the sync root. Never overwrites and never publishes into the synchronised folder.</summary>
    public async Task<string> RestoreCopyAsync(RecoveryInspection inspection, string destinationDirectory, CancellationToken ct)
    {
        var current = await Reread(inspection, ct);
        if (!current.BackupExists) throw new InvalidOperationException("보관본이 없어 복원할 수 없습니다.");
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (Overlaps(destination, current.Record.Root)) throw new InvalidOperationException("동기화 폴더 안으로는 복원하지 않습니다. 다른 위치를 선택하세요.");
        if (Overlaps(destination, location)) throw new InvalidOperationException("복구 보관함 안으로는 복원할 수 없습니다.");
        var name = current.Record.RelativePath.Split('/')[^1];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("복원할 이름을 사용할 수 없습니다: " + name);
        Directory.CreateDirectory(destination);
        var target = Unique(Path.Combine(destination, name));
        if (Directory.Exists(current.Record.BackupPath)) { Directory.CreateDirectory(target); return target; }
        File.Copy(current.Record.BackupPath, target, false);
        if (current.BackupHash is not null && await HashAsync(target, ct) != current.BackupHash)
        { File.Delete(target); throw new IOException("복원한 파일이 보관본과 다릅니다."); }
        return target;
    }

    /// <summary>
    /// Clears one record from the blocking set after its outcome was determined or the user reviewed it.
    /// The retained original moves to the resolved folder together with a durable acknowledgement.
    /// </summary>
    public async Task<string> AcknowledgeAsync(RecoveryInspection inspection, bool manuallyReviewed, CancellationToken ct)
    {
        var current = await Reread(inspection, ct);
        if (current.NeedsReview && !manuallyReviewed)
            throw new InvalidOperationException("판정할 수 없는 기록은 보관본과 대상을 직접 확인한 후에만 처리할 수 있습니다.");
        Directory.CreateDirectory(ResolvedLocation);
        var id = Path.GetFileNameWithoutExtension(current.RecordPath);
        var record = current.Record;
        if (current.BackupExists)
        {
            var moved = Unique(Path.Combine(ResolvedLocation, id + ".original"));
            if (Directory.Exists(record.BackupPath)) Directory.Move(record.BackupPath, moved);
            else File.Move(record.BackupPath, moved, false);
            record = record with { BackupPath = moved };
        }
        var destination = Unique(Path.Combine(ResolvedLocation, id + ".json"));
        var acknowledged = new AcknowledgedRecovery(record, current.Outcome.ToString(), current.Summary, DateTimeOffset.Now, manuallyReviewed);
        var temp = destination + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, acknowledged); file.Flush(true); }
        File.Move(temp, destination, false);
        File.Delete(current.RecordPath);
        return destination;
    }

    private async Task<RecoveryInspection> Reread(RecoveryInspection inspection, CancellationToken ct)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(inspection.RecordPath)) != location) throw new InvalidOperationException("이 보관함의 기록이 아닙니다.");
        if (!File.Exists(inspection.RecordPath)) throw new InvalidOperationException("복구 기록이 이미 처리되었습니다. 다시 조회하세요.");
        var current = await InspectOneAsync(inspection.RecordPath, ct);
        if (current != inspection) throw new InvalidOperationException("복구 기록 또는 대상 상태가 조회 이후 변경되었습니다. 다시 조회하세요.");
        return current;
    }

    private static async Task<RecoveryInspection> InspectOneAsync(string recordPath, CancellationToken ct)
    {
        var record = JsonSerializer.Deserialize<RecoveryRecord>(await File.ReadAllBytesAsync(recordPath, ct))
            ?? throw new InvalidDataException("복구 기록을 읽을 수 없습니다: " + recordPath);
        string? target = null;
        try { target = Path.GetFullPath(Path.Combine(record.Root, Path.Combine(record.RelativePath.Split('/')))); }
        catch (ArgumentException) { }
        var backupIsDirectory = Directory.Exists(record.BackupPath);
        var backupExists = backupIsDirectory || File.Exists(record.BackupPath);
        var backupHash = backupExists && !backupIsDirectory ? await HashAsync(record.BackupPath, ct) : null;
        var backupEmpty = backupIsDirectory && !Directory.EnumerateFileSystemEntries(record.BackupPath).Any();
        var targetIsDirectory = target is not null && Directory.Exists(target);
        var targetExists = targetIsDirectory || (target is not null && File.Exists(target));
        var targetHash = targetExists && !targetIsDirectory ? await HashAsync(target!, ct) : null;
        var expectsDirectory = record.Expected.Kind == EntryKind.Directory;
        var preserved = expectsDirectory ? backupIsDirectory && backupEmpty : backupExists && !backupIsDirectory && backupHash == record.Expected.ContentHash;
        var untouched = expectsDirectory ? targetIsDirectory : targetExists && !targetIsDirectory && targetHash == record.Expected.ContentHash;
        var outcome = preserved ? RecoveryOutcome.AppliedOriginalPreserved
            : !backupExists && untouched ? RecoveryOutcome.NotApplied
            : RecoveryOutcome.Indeterminate;
        var action = record.Action == "replace" ? "교체" : record.Action == "delete" ? "삭제" : record.Action;
        var summary = outcome switch
        {
            RecoveryOutcome.AppliedOriginalPreserved => $"{action} 작업을 적용했고 이전 내용을 보관본에 보존했습니다.",
            RecoveryOutcome.NotApplied => $"{action} 작업이 실행되지 않았습니다. 대상이 이전 내용 그대로입니다.",
            _ when backupExists && !preserved => $"{action} 직전에 외부에서 변경된 내용을 보관했을 수 있습니다. 보관본과 현재 대상을 직접 비교하세요.",
            _ when !backupExists && !untouched => $"{action} 보관본이 없고 대상 내용도 예상과 다릅니다. 현재 대상을 직접 확인하세요.",
            _ => $"{action} 작업의 결과를 확정할 수 없습니다. 보관본과 대상을 직접 확인하세요."
        };
        return new(recordPath, record, outcome, backupExists, backupHash, targetExists, targetHash, summary);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static bool Overlaps(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
