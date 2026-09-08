namespace MYSync.Desktop;

public sealed record SyncAlert(Guid PairId, string Name, string Message)
{
    public string Time { get; } = DateTime.Now.ToString("MM-dd HH:mm:ss");
    public string Title => Time + " · " + Name;
}
