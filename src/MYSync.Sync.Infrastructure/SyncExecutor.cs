using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;

public sealed record ExecutionReport(bool Converged, IReadOnlyList<string> Issues);

/// <summary>One sequential worker per sync pair. Call RecoverInterrupted before starting workers.</summary>
public sealed class SyncExecutor(SyncJournal journal)
{
    public async Task<ExecutionReport> RunAsync(Guid pair, ISyncEndpoint local, ISyncEndpoint remote, CancellationToken ct = default)
    {
        var issues = new List<string>();
        foreach (var job in journal.ReadJobs(pair).Where(x => x.State is JobState.Pending or JobState.NeedsReconcile))
        {
            ct.ThrowIfCancellationRequested();
            if (!journal.TryAcquire(job.Id)) continue;
            try
            {
                var left = await local.ScanAsync(ct); var right = await remote.ScanAsync(ct);
                var plan = SyncPlanner.Compare(left, right, journal.ReadBaseline(pair));
                if (!plan.CanExecute) throw new SyncPreconditionException(string.Join(" / ", plan.Errors));
                var op = job.Operation;
                var l = left.Entries.SingleOrDefault(x => x.Path == op.Path);
                var r = right.Entries.SingleOrDefault(x => x.Path == op.Path);
                if (op.Action != SyncAction.Conflict && OutcomeAlreadyPresent(op, l, r))
                { journal.SetOutcome(job.Id, JobState.Applied); continue; }
                // Replanning protects directory deletion when a descendant changed after enqueue.
                if (!plan.Operations.Any(x => x.Path == op.Path && x.Action == op.Action) || l != op.ExpectedLocal || r != op.ExpectedRemote)
                    throw new SyncPreconditionException("계획 이후 상태가 변경되었습니다. 새 계획이 필요합니다.");
                switch (op.Action)
                {
                    case SyncAction.Upload: await Copy(local, remote, op.ExpectedLocal!, op.Path, op.ExpectedRemote, ct); break;
                    case SyncAction.Download: await Copy(remote, local, op.ExpectedRemote!, op.Path, op.ExpectedLocal, ct); break;
                    case SyncAction.DeleteLocal: await local.DeleteAsync(op.ExpectedLocal!, ct); break;
                    case SyncAction.DeleteRemote: await remote.DeleteAsync(op.ExpectedRemote!, ct); break;
                    case SyncAction.Conflict:
                        await PreserveConflict(job, local, remote, ct);
                        journal.SetOutcome(job.Id, JobState.NeedsReconcile);
                        issues.Add(op.Path + ": 충돌 원본과 보존 사본을 확인해야 합니다.");
                        continue;
                }
                journal.SetOutcome(job.Id, JobState.Applied);
            }
            catch (OperationCanceledException)
            { journal.SetOutcome(job.Id, JobState.NeedsReconcile); throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { journal.SetOutcome(job.Id, JobState.NeedsReconcile); issues.Add(job.Operation.Path + ": " + ex.Message); }
        }
        var jobs = journal.ReadJobs(pair);
        if (jobs.Any(x => x.State is JobState.Pending or JobState.Running or JobState.NeedsReconcile))
            return new(false, issues);
        try
        {
            journal.CommitConverged(pair, await local.ScanAsync(ct), await remote.ScanAsync(ct));
            return new(true, issues);
        }
        catch (InvalidOperationException ex) { issues.Add(ex.Message); return new(false, issues); }
    }
    private static bool OutcomeAlreadyPresent(PlannedOperation op, SyncEntry? l, SyncEntry? r) => op.Action switch
    {
        SyncAction.Upload => l == op.ExpectedLocal && r == op.ExpectedLocal,
        SyncAction.Download => r == op.ExpectedRemote && l == op.ExpectedRemote,
        SyncAction.DeleteLocal or SyncAction.DeleteRemote => l is null && r is null,
        _ => false
    };
    private static async Task Copy(ISyncEndpoint source, ISyncEndpoint target, SyncEntry entry, string path, SyncEntry? expected, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Directory) { await target.CreateDirectoryAsync(path, ct); return; }
        if (entry.ContentHash is null) throw new SyncPreconditionException("내용 해시가 없어 안전한 전송을 실행할 수 없습니다.");
        await using var stream = await source.OpenReadAsync(entry, ct);
        await target.PutFileAsync(path, expected, stream, entry.ContentHash, ct);
    }
    private static async Task PreserveConflict(JournalJob job, ISyncEndpoint local, ISyncEndpoint remote, CancellationToken ct)
    {
        var op = job.Operation;
        if (op.ExpectedLocal?.Kind == EntryKind.Directory || op.ExpectedRemote?.Kind == EntryKind.Directory)
            throw new SyncPreconditionException("폴더·유형 충돌은 자동 변경하지 않습니다.");
        foreach (var (source, entry, side) in new[] { (local, op.ExpectedLocal, "local"), (remote, op.ExpectedRemote, "remote") })
        {
            if (entry is null) continue;
            var archive = op.Path + $".conflict-{job.PairId:N}-{job.Id}-{side}";
            foreach (var target in new[] { local, remote })
            {
                var snapshot = await target.ScanAsync(ct);
                if (!snapshot.IsComplete) throw new SyncPreconditionException("충돌 사본 대상 검사 실패");
                var existing = snapshot.Entries.SingleOrDefault(x => x.Path == archive);
                if (existing is not null)
                {
                    if (existing.Kind != EntryKind.File || existing.ContentHash != entry.ContentHash)
                        throw new SyncPreconditionException("충돌 사본 경로에 다른 내용이 있습니다.");
                    continue;
                }
                await Copy(source, target, entry, archive, null, ct);
            }
        }
    }
}
