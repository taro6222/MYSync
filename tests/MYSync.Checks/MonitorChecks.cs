using MYSync.Sync.Infrastructure;

internal static class MonitorChecks
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var root = Path.Combine(scratch, "monitor"); Directory.CreateDirectory(root);
        var first = Signal(); var next = Signal(); var release = Signal(); var calls = 0; var concurrent = 0; var maximum = 0;
        var monitor = new SyncMonitor(root, async ct =>
        {
            var count = Interlocked.Increment(ref concurrent); maximum = Math.Max(maximum, count);
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { first.TrySetResult(); await release.Task.WaitAsync(ct); }
                else next.TrySetResult();
                return new ExecutionReport(true, []);
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10));
        monitor.Start();
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 100; i++) monitor.RequestScan();
            release.TrySetResult(); await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(maximum == 1 && calls == 2, "event burst ran overlapping or duplicate cycles");
        }
        finally { await monitor.DisposeAsync(); }
        Check(monitor.Completion.IsCompleted && concurrent == 0, "pause returned before worker exit");
        Console.WriteLine("PASS: coalesced event burst, sequential cycles, stop waits for worker completion");

        var watched = Signal(); var ready = Signal(); var watchCalls = 0;
        await using (var fileMonitor = new SyncMonitor(root, ct =>
        {
            if (Interlocked.Increment(ref watchCalls) == 1) ready.TrySetResult(); else watched.TrySetResult();
            return Task.FromResult(new ExecutionReport(true, []));
        }, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10)))
        {
            fileMonitor.Start(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(root, "changed.txt"), "new content");
            await watched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var periodic = Signal(); var periodCalls = 0;
        await using (var timerMonitor = new SyncMonitor(root, ct =>
        {
            if (Interlocked.Increment(ref periodCalls) >= 2) periodic.TrySetResult();
            return Task.FromResult(new ExecutionReport(true, []));
        }, TimeSpan.FromMilliseconds(40), TimeSpan.Zero))
        { timerMonitor.Start(); await periodic.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        Console.WriteLine("PASS: real FileSystemWatcher event and periodic reconciliation without local changes");

        var faultCalls = 0; var attention = false;
        await using (var faultMonitor = new SyncMonitor(root, ct =>
        {
            Interlocked.Increment(ref faultCalls); return Task.FromResult(new ExecutionReport(false, ["conflict"]));
        }, TimeSpan.FromMilliseconds(20), TimeSpan.Zero))
        {
            faultMonitor.StatusChanged += status => attention |= status.State == MonitorState.NeedsAttention;
            faultMonitor.Start(); await faultMonitor.Completion.WaitAsync(TimeSpan.FromSeconds(5)); faultMonitor.RequestScan();
            Check(faultCalls == 1 && attention, "failure retried automatically");
        }
        var running = Signal(); var cancelled = false;
        var cancellable = new SyncMonitor(root, async ct =>
        {
            running.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return new ExecutionReport(true, []);
        }, settle: TimeSpan.Zero);
        cancellable.Start(); await running.Task.WaitAsync(TimeSpan.FromSeconds(5)); await cancellable.DisposeAsync();
        Check(cancelled && cancellable.Completion.IsCompleted, "running cycle not cancelled");
        Console.WriteLine("PASS: unresolved conflict stops monitor; pause cancels active cycle");
    }
}
