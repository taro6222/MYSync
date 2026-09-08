using System.Collections.ObjectModel;
using MYSync.Sync.Infrastructure;

namespace MYSync.Desktop;

public sealed class TransferHistory
{
    public const int Limit = 1000;
    public ObservableCollection<TransferRow> Rows { get; } = [];
    public long SessionBytes { get; private set; }
    public int CompletedFiles { get; private set; }
    public DateTime? LastSynchronized { get; private set; }
    public void Apply(Guid pairId, string name, SyncProgress report)
    {
        if (report.ActivityId != Guid.Empty && report.Path is not null)
        {
            var row = Rows.FirstOrDefault(x => x.ActivityId == report.ActivityId);
            if (row is null) { row = new(pairId, name, report); Rows.Insert(0, row); }
            if (report.Event == TransferEvent.Started) Rows.Move(Rows.IndexOf(row), 0);
            var wasDone = row.Done;
            SessionBytes += row.Apply(report);
            if (!wasDone && report.Event == TransferEvent.Completed && row.IsFile) CompletedFiles++;
        }
        else if (report.Event == TransferEvent.Held)
        {
            foreach (var row in Rows.Where(x => x.RunId == report.RunId && x.Applied && !x.Done)) row.Finish("검증 보류");
        }
        else if (report.Phase is SyncPhase.Idle or SyncPhase.Attention)
        {
            foreach (var row in Rows.Where(x => x.RunId == report.RunId && !x.Done))
            {
                var completed = report.Phase == SyncPhase.Idle && row.Applied;
                row.Finish(completed ? "완료" : "확인 필요");
                if (completed && row.IsFile) CompletedFiles++;
            }
            if (report.Phase == SyncPhase.Idle) LastSynchronized = DateTime.Now;
        }
        while (Rows.Count > Limit)
        {
            var oldest = Rows.LastOrDefault(x => x.Done);
            if (oldest is null) break;
            Rows.Remove(oldest);
        }
    }
    public void Stop(Guid pairId)
    {
        foreach (var row in Rows.Where(x => x.PairId == pairId && !x.Done)) row.Finish("중단");
    }
}
