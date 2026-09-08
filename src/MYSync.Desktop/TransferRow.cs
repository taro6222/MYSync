using System.ComponentModel;
using System.Diagnostics;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

namespace MYSync.Desktop;

/// <summary>One operation. Stream bytes are advisory, not measured network throughput.</summary>
public sealed class TransferRow(Guid pairId, string name, SyncProgress initial) : INotifyPropertyChanged
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long attemptBytes;
    public Guid PairId { get; } = pairId;
    public Guid ActivityId { get; } = initial.ActivityId;
    public Guid RunId { get; } = initial.RunId;
    public string Name { get; } = name;
    public string Path { get; } = initial.Path ?? "";
    public bool IsFile { get; } = initial.Kind == EntryKind.File;
    public string Operation { get; } = initial.Action switch
    {
        SyncAction.Upload => "업로드", SyncAction.Download => "다운로드",
        SyncAction.DeleteLocal => "로컬 삭제", SyncAction.DeleteRemote => "원격 삭제", _ => "충돌"
    };
    public string State { get; private set; } = "검사 중";
    public string Message { get; private set; } = "";
    public string Details => Path + "\n" + Message;
    public string Started { get; } = DateTime.Now.ToString("MM-dd HH:mm:ss");
    public string Finished { get; private set; } = "—";
    public long Bytes { get; private set; }
    public bool Applied { get; private set; }
    public bool Done { get; private set; }
    public string Transferred => Size(Bytes);
    public string Speed => Bytes == 0 ? "—" : Size((long)(Bytes / Math.Max(clock.Elapsed.TotalSeconds, 0.001))) + "/s";
    public string Duration => clock.Elapsed.ToString(@"hh\:mm\:ss");
    public event PropertyChangedEventHandler? PropertyChanged;
    public long Apply(SyncProgress report)
    {
        if (Done) return 0;
        Message = report.Message;
        var delta = 0L;
        if (report.Event == TransferEvent.Retrying) attemptBytes = 0;
        if (report.Phase == SyncPhase.Transferring)
        {
            delta = Math.Max(0, report.Bytes - attemptBytes);
            Bytes += delta; attemptBytes = report.Bytes;
        }
        State = report.Phase switch
        {
            SyncPhase.Transferring => "전송 중", SyncPhase.Verifying => "검증 대기",
            SyncPhase.Attention => "확인 필요", _ => "검사 중"
        };
        if (report.Event == TransferEvent.Queued) State = "대기 중";
        if (report.Event == TransferEvent.Held) Finish(report.Message);
        if (report.Event == TransferEvent.Started) clock.Restart();
        if (report.Event == TransferEvent.Applied) Applied = true;
        if (report.Event == TransferEvent.Retrying) State = "재시도 대기";
        if (report.Event is TransferEvent.Failed or TransferEvent.Cancelled)
            Finish(report.Event == TransferEvent.Cancelled ? "중단" : "확인 필요");
        Refresh();
        return delta;
    }
    public void Finish(string state)
    {
        if (Done) return;
        Done = true; State = state; clock.Stop();
        Finished = DateTime.Now.ToString("MM-dd HH:mm:ss"); Refresh();
    }
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#") + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#") + " MB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.##") + " GB"
    };
}
