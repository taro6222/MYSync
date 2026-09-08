using MYSync.Sync.Core;

namespace MYSync.Sync.Infrastructure;

/// <summary>Explicit one-file direction choice. Keeps copies before changing the comparison
/// baseline; still uses endpoint preconditions and never bypasses authentication or integrity.</summary>
public static class FileResolution
{
    public static async Task<string> ApplyAsync(SyncJournal journal, Guid pair, string path, bool preferLocal,
        ISyncEndpoint local, ISyncEndpoint remote, CancellationToken ct = default,
        IProgress<SyncProgress>? progress = null, TransferControl? control = null)
    {
        var left = await local.ScanAsync(ct); var right = await remote.ScanAsync(ct);
        var check = SyncPlanner.Compare(left, right, journal.ReadBaseline(pair));
        if (!check.CanExecute) throw new SyncPreconditionException(string.Join(" / ", check.Errors));
        if (check.Unsupported.Any(x => path.Equals(x.Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(x.Path + "/", StringComparison.OrdinalIgnoreCase)))
            throw new SyncPreconditionException("제외되었거나 지원하지 않는 항목입니다.");
        var l = left.Entries.SingleOrDefault(x => x.Path == path); var r = right.Entries.SingleOrDefault(x => x.Path == path);
        if (l?.Kind == EntryKind.Directory || r?.Kind == EntryKind.Directory)
            throw new SyncPreconditionException("폴더·유형 충돌은 파일 덮어쓰기로 해결할 수 없습니다.");
        if (l == r)
        {
            journal.CommitVerifiedPaths(pair, left, right);
            journal.RefreshPlan(pair, SyncPlanner.Compare(left, right, journal.ReadBaseline(pair)));
            return "양쪽 내용이 이미 같습니다. 전송할 필요가 없습니다.";
        }
        if ((preferLocal ? l : r) is not { Kind: EntryKind.File, ContentHash: not null })
            throw new SyncPreconditionException("선택한 쪽에 전송할 파일이 없습니다.");
        journal.CommitVerifiedPaths(pair, left, right);
        journal.RefreshPlan(pair, SyncPlanner.Compare(left, right, journal.ReadBaseline(pair)));
        var job = journal.ReadJobs(pair).Single(x => x.State != JobState.Completed && x.Operation.Path == path);
        await SyncExecutor.PreserveConflict(job, local, remote, ct);
        // Verify the originals again after preservation; a concurrent change must not be overwritten.
        left = await local.ScanAsync(ct); right = await remote.ScanAsync(ct);
        journal.ResolveFileChoice(pair, job.Id, left, right, preferLocal);
        control?.Resume(path);
        await new SyncExecutor(journal).RunAsync(pair, local, remote, ct, progress, control, path);
        var pending = journal.ReadJobs(pair).Where(x => x.State != JobState.Completed && x.Operation.Path == path).ToArray();
        if (pending.Length != 0) throw new SyncPreconditionException(string.Join(" / ", pending.Select(x => x.FailureReason ?? "결과 검증이 필요합니다.")));
        return "선택한 파일을 전송했습니다. 이전 내용은 보존 사본으로 남겼습니다.";
    }
}
