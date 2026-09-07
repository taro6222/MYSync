using System.Threading.Channels;

namespace MYSync.Sync.Infrastructure;

public enum MonitorState { Watching, Running, Paused, NeedsAttention }
public sealed record MonitorStatus(MonitorState State, string Message);

/// <summary>One sequential synchronization cycle, with coalesced notifications and periodic reconciliation.</summary>
public sealed class SyncMonitor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ExecutionReport>> cycle;
    private readonly TimeSpan interval;
    private readonly TimeSpan settle;
    private readonly int maxTransientCycles;
    private readonly TimeSpan transientDelay;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly FileSystemWatcher watcher;
    private Task? completion;
    public event Action<MonitorStatus>? StatusChanged;
    public Task Completion => completion ?? Task.CompletedTask;
    public SyncMonitor(string localRoot, Func<CancellationToken, Task<ExecutionReport>> cycle, TimeSpan? interval = null, TimeSpan? settle = null, int maxTransientCycles = 5, TimeSpan? transientDelay = null)
    {
        this.cycle = cycle; this.interval = interval ?? TimeSpan.FromMinutes(5); this.settle = settle ?? TimeSpan.FromSeconds(2);
        this.maxTransientCycles = maxTransientCycles; this.transientDelay = transientDelay ?? TimeSpan.FromSeconds(30);
        if (this.interval <= TimeSpan.Zero || this.settle < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        watcher = new FileSystemWatcher(localRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
        watcher.Error += (_, _) => RequestScan();
    }
    private void Changed(object sender, FileSystemEventArgs e) => RequestScan();
    public void RequestScan() => signals.Writer.TryWrite(true);
    public void Start()
    {
        if (completion is not null) throw new InvalidOperationException("감시가 이미 시작되었습니다.");
        watcher.EnableRaisingEvents = true; RequestScan(); completion = Task.Run(RunAsync);
    }
    private void Report(MonitorState state, string message) => StatusChanged?.Invoke(new(state, message));
    private async Task RunAsync()
    {
        var ticker = TickAsync();
        try
        {
            var transientCycles = 0;
            while (await signals.Reader.WaitToReadAsync(stop.Token))
            {
                await Task.Delay(settle, stop.Token);
                while (signals.Reader.TryRead(out _)) { }
                Report(MonitorState.Running, "자동 동기화 검사·전송 중");
                var report = await cycle(stop.Token);
                if (!report.Converged)
                {
                    // Only failures the engine classified as transient are retried, and only a bounded number of times.
                    if (report.TransientOnly && ++transientCycles <= maxTransientCycles)
                    {
                        Report(MonitorState.Watching, $"일시 오류로 다시 시도합니다 ({transientCycles}/{maxTransientCycles}). " + string.Join(" / ", report.Issues));
                        await Task.Delay(transientDelay, stop.Token);
                        RequestScan();
                        continue;
                    }
                    Report(MonitorState.NeedsAttention, "확인이 필요합니다. " + string.Join(" / ", report.Issues)); return;
                }
                transientCycles = 0;
                Report(MonitorState.Watching, report.Notices.Count == 0
                    ? "동기화 완료 · 변경 감시 중"
                    : $"동기화 완료 · 미지원 항목 {report.Notices.Count}개 · 변경 감시 중");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Report(MonitorState.Paused, "자동 동기화를 일시정지했습니다."); }
        catch (Exception ex) { Report(MonitorState.NeedsAttention, "자동 동기화 중단: " + ex.Message); }
        finally
        {
            stop.Cancel(); watcher.EnableRaisingEvents = false;
            try { await ticker; } catch (OperationCanceledException) { }
        }
    }
    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stop.Token)) RequestScan();
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); if (completion is not null) await completion;
        watcher.Dispose(); stop.Dispose();
    }
}
