using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;

public sealed record ExecutionReport(bool Converged, IReadOnlyList<string> Issues);

public enum SyncPhase { Idle, Scanning, Transferring, Verifying, Attention }
/// <summary>Advisory progress only. Never used to decide whether an operation completed.</summary>
public sealed record SyncProgress(SyncPhase Phase, string Message, string? Path = null, SyncAction? Action = null, int Completed = 0, int Total = 0, long Bytes = 0);

/// <summary>One sequential worker per sync pair. Call RecoverInterrupted before starting workers.</summary>
public sealed class SyncExecutor(SyncJournal journal)
{
    public async Task<ExecutionReport> RunAsync(Guid pair, ISyncEndpoint local, ISyncEndpoint remote, CancellationToken ct = default, IProgress<SyncProgress>? progress = null)
    {
        var issues = new List<string>();
        var queued = journal.ReadJobs(pair).Where(x => x.State is JobState.Pending or JobState.NeedsReconcile).ToArray();
        var total = queued.Length;
        var completed = 0;
        progress?.Report(new(SyncPhase.Scanning, total == 0 ? "실행할 작업이 없습니다." : $"{total}개 작업을 실행합니다.", Total: total));
        foreach (var job in queued)
        {
            ct.ThrowIfCancellationRequested();
            if (!journal.TryAcquire(job.Id)) continue;
            var op = job.Operation;
            try
            {
                progress?.Report(new(SyncPhase.Scanning, "현재 상태 재확인 중", op.Path, op.Action, completed, total));
                var left = await local.ScanAsync(ct); var right = await remote.ScanAsync(ct);
                var plan = SyncPlanner.Compare(left, right, journal.ReadBaseline(pair));
                if (!plan.CanExecute) throw new SyncPreconditionException(string.Join(" / ", plan.Errors));
                var l = left.Entries.SingleOrDefault(x => x.Path == op.Path);
                var r = right.Entries.SingleOrDefault(x => x.Path == op.Path);
                if (op.Action != SyncAction.Conflict && OutcomeAlreadyPresent(op, l, r))
                { journal.SetOutcome(job.Id, JobState.Applied); completed++; continue; }
                // Replanning protects directory deletion when a descendant changed after enqueue.
                if (!plan.Operations.Any(x => x.Path == op.Path && x.Action == op.Action) || l != op.ExpectedLocal || r != op.ExpectedRemote)
                    throw new SyncPreconditionException("계획 이후 상태가 변경되었습니다. 새 계획이 필요합니다.");
                var transferred = 0L;
                void Sent(long count)
                {
                    transferred += count;
                    progress?.Report(new(SyncPhase.Transferring, Describe(op.Action), op.Path, op.Action, completed, total, transferred));
                }
                progress?.Report(new(SyncPhase.Transferring, Describe(op.Action), op.Path, op.Action, completed, total));
                switch (op.Action)
                {
                    case SyncAction.Upload: await Copy(local, remote, op.ExpectedLocal!, op.Path, op.ExpectedRemote, ct, Sent); break;
                    case SyncAction.Download: await Copy(remote, local, op.ExpectedRemote!, op.Path, op.ExpectedLocal, ct, Sent); break;
                    case SyncAction.DeleteLocal: await local.DeleteAsync(op.ExpectedLocal!, ct); break;
                    case SyncAction.DeleteRemote: await remote.DeleteAsync(op.ExpectedRemote!, ct); break;
                    case SyncAction.Conflict:
                        await PreserveConflict(job, local, remote, ct);
                        journal.SetOutcome(job.Id, JobState.NeedsReconcile);
                        completed++;
                        issues.Add(op.Path + ": 충돌 원본과 보존 사본을 확인해야 합니다.");
                        continue;
                }
                journal.SetOutcome(job.Id, JobState.Applied);
                completed++;
            }
            catch (OperationCanceledException)
            { journal.SetOutcome(job.Id, JobState.NeedsReconcile); progress?.Report(new(SyncPhase.Idle, "중단했습니다.", op.Path, op.Action, completed, total)); throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
            { journal.SetOutcome(job.Id, JobState.NeedsReconcile); completed++; issues.Add(op.Path + ": " + ex.Message); }
        }
        var jobs = journal.ReadJobs(pair);
        if (jobs.Any(x => x.State is JobState.Pending or JobState.Running or JobState.NeedsReconcile))
        {
            progress?.Report(new(SyncPhase.Attention, "완료되지 않은 항목이 있습니다.", Completed: completed, Total: total));
            return new(false, issues);
        }
        try
        {
            progress?.Report(new(SyncPhase.Verifying, "양쪽 폴더 상태 확인 중", Completed: completed, Total: total));
            journal.CommitConverged(pair, await local.ScanAsync(ct), await remote.ScanAsync(ct));
            progress?.Report(new(SyncPhase.Idle, "동기화 완료", Completed: completed, Total: total));
            return new(true, issues);
        }
        catch (InvalidOperationException ex)
        { issues.Add(ex.Message); progress?.Report(new(SyncPhase.Attention, ex.Message, Completed: completed, Total: total)); return new(false, issues); }
    }
    private static string Describe(SyncAction action) => action switch
    {
        SyncAction.Upload => "업로드 중",
        SyncAction.Download => "다운로드 중",
        SyncAction.DeleteLocal => "컴퓨터에서 삭제 중",
        SyncAction.DeleteRemote => "원격에서 삭제 중",
        _ => "충돌 사본 보존 중"
    };
    private static bool OutcomeAlreadyPresent(PlannedOperation op, SyncEntry? l, SyncEntry? r) => op.Action switch
    {
        SyncAction.Upload => l == op.ExpectedLocal && r == op.ExpectedLocal,
        SyncAction.Download => r == op.ExpectedRemote && l == op.ExpectedRemote,
        SyncAction.DeleteLocal or SyncAction.DeleteRemote => l is null && r is null,
        _ => false
    };
    private static async Task Copy(ISyncEndpoint source, ISyncEndpoint target, SyncEntry entry, string path, SyncEntry? expected, CancellationToken ct, Action<long>? sent = null)
    {
        if (entry.Kind == EntryKind.Directory) { await target.CreateDirectoryAsync(path, ct); return; }
        if (entry.ContentHash is null) throw new SyncPreconditionException("내용 해시가 없어 안전한 전송을 실행할 수 없습니다.");
        await using var stream = await source.OpenReadAsync(entry, ct);
        if (sent is null) { await target.PutFileAsync(path, expected, stream, entry.ContentHash, ct); return; }
        await using var counting = new CountingStream(stream, sent);
        await target.PutFileAsync(path, expected, counting, entry.ContentHash, ct);
    }
    /// <summary>Counts bytes handed to the target. Does not own or dispose the underlying stream.</summary>
    private sealed class CountingStream(Stream inner, Action<long> sent) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0) sent(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            if (read > 0) sent(read);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
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
