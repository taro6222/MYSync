using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;

/// <summary>
/// TransientOnly means every unfinished item failed for a reason the engine classified as temporary.
/// Notices are items that were identified but cannot be synchronised; they never prevent convergence.
/// </summary>
public sealed record ExecutionReport(bool Converged, IReadOnlyList<string> Issues, bool TransientOnly = false, IReadOnlyList<string>? Notices = null)
{
    public IReadOnlyList<string> Notices { get; init; } = Notices ?? [];
}

public enum SyncPhase { Idle, Scanning, Transferring, Verifying, Attention }
/// <summary>Advisory progress only. Never used to decide whether an operation completed.</summary>
public enum TransferEvent { None, Started, Retrying, Applied, Failed, Cancelled }
public sealed record SyncProgress(SyncPhase Phase, string Message, string? Path = null, SyncAction? Action = null, int Completed = 0, int Total = 0, long Bytes = 0, Guid RunId = default, Guid ActivityId = default, EntryKind? Kind = null, TransferEvent Event = TransferEvent.None);

/// <summary>Exponential backoff for temporary failures. Every attempt re-scans and re-plans, so a retry never repeats a side effect blindly.</summary>
public sealed record RetryPolicy(int MaxAttempts = 3, TimeSpan? FirstDelay = null, TimeSpan? MaxDelay = null)
{
    public TimeSpan DelayFor(int attempt)
    {
        if (MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        var first = FirstDelay ?? TimeSpan.FromSeconds(2);
        var ceiling = MaxDelay ?? TimeSpan.FromSeconds(30);
        var scaled = first * Math.Pow(2, Math.Max(0, attempt - 1));
        return scaled > ceiling ? ceiling : scaled;
    }
}

/// <summary>One sequential worker per sync pair. Call RecoverInterrupted before starting workers.</summary>
public sealed class SyncExecutor(SyncJournal journal, RetryPolicy? retryPolicy = null)
{
    private readonly RetryPolicy retry = retryPolicy ?? new RetryPolicy();

    public async Task<ExecutionReport> RunAsync(Guid pair, ISyncEndpoint local, ISyncEndpoint remote, CancellationToken ct = default, IProgress<SyncProgress>? progress = null)
    {
        var runId = Guid.NewGuid();
        var activityId = Guid.Empty;
        EntryKind? entryKind = null;
        void Report(SyncProgress value) => progress?.Report(value with { RunId = runId, ActivityId = activityId, Kind = entryKind });
        var issues = new List<string>();
        var permanent = 0;
        var queued = journal.ReadJobs(pair).Where(x => x.State is JobState.Pending or JobState.NeedsReconcile).ToArray();
        var total = queued.Length;
        var completed = 0;
        Report(new(SyncPhase.Scanning, total == 0 ? "실행할 작업이 없습니다." : $"{total}개 작업을 실행합니다.", Total: total));

        // One attempt: re-scan, re-plan, verify the expected state, then act. Returns true for an unresolved conflict.
        async Task<bool> Once(JournalJob job)
        {
            var op = job.Operation;
            Report(new(SyncPhase.Scanning, "현재 상태 재확인 중", op.Path, op.Action, completed, total));
            var left = await local.ScanAsync(ct); var right = await remote.ScanAsync(ct);
            var plan = SyncPlanner.Compare(left, right, journal.ReadBaseline(pair));
            if (!plan.CanExecute) throw new SyncPreconditionException(string.Join(" / ", plan.Errors));
            var l = left.Entries.SingleOrDefault(x => x.Path == op.Path);
            var r = right.Entries.SingleOrDefault(x => x.Path == op.Path);
            if (op.Action != SyncAction.Conflict && OutcomeAlreadyPresent(op, l, r)) return false;
            // Replanning protects directory deletion when a descendant changed after enqueue.
            if (!plan.Operations.Any(x => x.Path == op.Path && x.Action == op.Action) || l != op.ExpectedLocal || r != op.ExpectedRemote)
                throw new SyncPreconditionException("계획 이후 상태가 변경되었습니다. 새 계획이 필요합니다.");
            var transferred = 0L;
            void Sent(long count)
            {
                transferred += count;
                Report(new(SyncPhase.Transferring, Describe(op.Action), op.Path, op.Action, completed, total, transferred));
            }
            Report(new(SyncPhase.Transferring, Describe(op.Action), op.Path, op.Action, completed, total));
            switch (op.Action)
            {
                case SyncAction.Upload: await Copy(local, remote, op.ExpectedLocal!, op.Path, op.ExpectedRemote, ct, Sent); break;
                case SyncAction.Download: await Copy(remote, local, op.ExpectedRemote!, op.Path, op.ExpectedLocal, ct, Sent); break;
                case SyncAction.DeleteLocal: await local.DeleteAsync(op.ExpectedLocal!, ct); break;
                case SyncAction.DeleteRemote: await remote.DeleteAsync(op.ExpectedRemote!, ct); break;
                case SyncAction.Conflict: await PreserveConflict(job, local, remote, ct); return true;
            }
            return false;
        }

        async Task<bool> Attempt(JournalJob job)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await Once(job); }
                catch (Exception ex) when (attempt < retry.MaxAttempts && !ct.IsCancellationRequested && Handled(ex) && Classify(ex) == SyncFailureKind.Transient)
                {
                    var delay = retry.DelayFor(attempt);
                    SyncDiagnostics.Write("job.retry", failure: Classify(ex), exceptionType: ex.GetType().Name);
                    Report(new(SyncPhase.Attention,
                        $"일시 오류로 {delay.TotalSeconds:0.#}초 후 재시도합니다 ({attempt}/{retry.MaxAttempts}): {ex.Message}",
                        job.Operation.Path, job.Operation.Action, completed, total, Event: TransferEvent.Retrying));
                    await Task.Delay(delay, ct);
                }
            }
        }

        try
        {
        foreach (var job in queued)
        {
            ct.ThrowIfCancellationRequested();
            if (!journal.TryAcquire(job.Id)) continue;
            using var diagnosticScope = SyncDiagnostics.BeginJob(pair, job.Id);
            SyncDiagnostics.Write("job.started");
            var op = job.Operation;
            activityId = Guid.NewGuid(); entryKind = (op.ExpectedLocal ?? op.ExpectedRemote)?.Kind;
            Report(new(SyncPhase.Scanning, "작업 시작", op.Path, op.Action, completed, total, Event: TransferEvent.Started));
            try
            {
                if (await Attempt(job))
                {
                    journal.SetOutcome(job.Id, JobState.NeedsReconcile, "충돌 원본과 보존 사본을 확인해야 합니다.", SyncFailureKind.Precondition); completed++; permanent++;
                    SyncDiagnostics.Write("job.conflict", failure: SyncFailureKind.Precondition);
                    issues.Add(op.Path + ": 충돌 원본과 보존 사본을 확인해야 합니다.");
                    Report(new(SyncPhase.Attention, "충돌 확인 필요", op.Path, op.Action, completed, total, Event: TransferEvent.Failed));
                }
                else { journal.SetOutcome(job.Id, JobState.Applied); completed++; SyncDiagnostics.Write("job.applied"); Report(new(SyncPhase.Verifying, "반영됨 · 최종 검증 대기", op.Path, op.Action, completed, total, Event: TransferEvent.Applied)); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                journal.SetOutcome(job.Id, JobState.NeedsReconcile, "사용자가 실행을 중단했습니다. 결과를 재검사해야 합니다.");
                SyncDiagnostics.Write("job.cancelled");
                Report(new(SyncPhase.Idle, "중단했습니다.", op.Path, op.Action, completed, total, Event: TransferEvent.Cancelled));
                throw;
            }
            catch (Exception ex) when (Handled(ex))
            {
                var kind = Classify(ex);
                journal.SetOutcome(job.Id, JobState.NeedsReconcile, ex.Message, kind); completed++;
                SyncDiagnostics.Write("job.failed", failure: kind, exceptionType: ex.GetType().Name);
                if (kind != SyncFailureKind.Transient) permanent++;
                issues.Add($"{op.Path}: [{Label(kind)}] {ex.Message}");
                Report(new(SyncPhase.Attention, ex.Message, op.Path, op.Action, completed, total, Event: TransferEvent.Failed));
            }
        }
        activityId = Guid.Empty; entryKind = null;
        var transientOnly = issues.Count > 0 && permanent == 0;
        var jobs = journal.ReadJobs(pair);
        if (jobs.Any(x => x.State is JobState.Pending or JobState.Running or JobState.NeedsReconcile))
        {
            Report(new(SyncPhase.Attention, "완료되지 않은 항목이 있습니다.", Completed: completed, Total: total));
            return new(false, issues, transientOnly);
        }
        try
        {
            Report(new(SyncPhase.Verifying, "양쪽 폴더 상태 확인 중", Completed: completed, Total: total));
            var finalLocal = await local.ScanAsync(ct); var finalRemote = await remote.ScanAsync(ct);
            journal.CommitConverged(pair, finalLocal, finalRemote);
            var notices = SyncPlanner.Compare(finalLocal, finalRemote, []).Unsupported.Select(x => $"{x.Path}: {x.Reason}").ToArray();
            Report(new(SyncPhase.Idle, notices.Length == 0 ? "동기화 완료" : $"동기화 완료 · 미지원 항목 {notices.Length}개", Completed: completed, Total: total));
            return new(true, issues) { Notices = notices };
        }
        catch (InvalidOperationException ex)
        { issues.Add(ex.Message); Report(new(SyncPhase.Attention, ex.Message, Completed: completed, Total: total)); return new(false, issues); }
        }
        catch
        {
            activityId = Guid.Empty; entryKind = null;
            Report(new(SyncPhase.Attention, "실행이 중단되어 최종 검증을 완료하지 못했습니다.", Completed: completed, Total: total));
            throw;
        }
    }

    private static bool Handled(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or OperationCanceledException;
    /// <summary>A cancellation reaching here is a timeout, not the caller stopping: user cancellation is filtered before this.</summary>
    internal static SyncFailureKind Classify(Exception ex) => ex switch
    {
        SyncTransferException transfer => transfer.Kind,
        FileNotFoundException or DirectoryNotFoundException => SyncFailureKind.Missing,
        UnauthorizedAccessException => SyncFailureKind.Permission,
        HttpRequestException or OperationCanceledException => SyncFailureKind.Transient,
        _ => SyncFailureKind.Unknown
    };
    private static string Label(SyncFailureKind kind) => kind switch
    {
        SyncFailureKind.Transient => "일시 오류",
        SyncFailureKind.Authentication => "인증",
        SyncFailureKind.Permission => "권한",
        SyncFailureKind.Missing => "항목 없음",
        SyncFailureKind.Precondition => "상태 불일치",
        _ => "오류"
    };
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
