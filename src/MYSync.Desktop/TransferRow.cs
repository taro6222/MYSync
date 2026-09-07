using System.ComponentModel;
using System.Runtime.CompilerServices;
using MYSync.Sync.Infrastructure;

namespace MYSync.Desktop;

/// <summary>Advisory view of one pair's current activity. Never used to decide sync state.</summary>
public sealed class TransferRow(Guid pairId, string name) : INotifyPropertyChanged
{
    private string state = "대기";
    private string operation = "";
    private string path = "";
    private string progress = "";
    private string transferred = "";
    private string message = "";
    private string updated = "";
    public Guid PairId { get; } = pairId;
    public string Name { get; } = name;
    public string State { get => state; private set => Set(ref state, value); }
    public string Operation { get => operation; private set => Set(ref operation, value); }
    public string Path { get => path; private set => Set(ref path, value); }
    public string Progress { get => progress; private set => Set(ref progress, value); }
    public string Transferred { get => transferred; private set => Set(ref transferred, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public string Updated { get => updated; private set => Set(ref updated, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Apply(SyncProgress report)
    {
        State = report.Phase switch
        {
            SyncPhase.Scanning => "검사 중",
            SyncPhase.Transferring => "전송 중",
            SyncPhase.Verifying => "확인 중",
            SyncPhase.Attention => "확인 필요",
            _ => "대기"
        };
        Operation = report.Action switch
        {
            MYSync.Sync.Core.SyncAction.Upload => "업로드",
            MYSync.Sync.Core.SyncAction.Download => "다운로드",
            MYSync.Sync.Core.SyncAction.DeleteLocal => "로컬 삭제",
            MYSync.Sync.Core.SyncAction.DeleteRemote => "원격 삭제",
            MYSync.Sync.Core.SyncAction.Conflict => "충돌",
            _ => ""
        };
        Path = report.Path ?? "";
        Progress = report.Total > 0 ? $"{report.Completed}/{report.Total}" : "";
        Transferred = report.Bytes > 0 ? Size(report.Bytes) : "";
        Message = report.Message;
        Updated = DateTime.Now.ToString("HH:mm:ss");
    }
    public void Reset(string reason)
    {
        State = "대기"; Operation = ""; Path = ""; Progress = ""; Transferred = "";
        Message = reason; Updated = DateTime.Now.ToString("HH:mm:ss");
    }
    private static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#") + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#") + " MB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.##") + " GB"
    };
    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (field == value) return;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
