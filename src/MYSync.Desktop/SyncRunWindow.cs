using System.IO;
using System.Windows;
using System.Windows.Controls;
using MYSync.Provider.Abstractions;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

namespace MYSync.Desktop;

public sealed class SyncRunWindow : Window
{
    private readonly TransferControl? control;
    private SyncPair pair;
    private Task? executionTask;
    private readonly IProvider provider;
    private readonly AccountStore accounts;
    private readonly SyncJournal journal;
    private readonly string recovery;
    private readonly IProgress<SyncProgress>? progress;
    private readonly DataGrid grid = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, Margin = new Thickness(0, 14, 0, 14) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button inspect = new() { Content = "변경 검사", Padding = new Thickness(16, 8, 16, 8) };
    private readonly Button execute = new() { Content = "표시된 작업 실행", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 8, 16, 8) };
    private readonly Button cancel = new() { Content = "중단", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 8, 16, 8) };
    private readonly Mutex runLock;
    private CancellationTokenSource? operation;
    private ISyncEndpoint? local;
    private ISyncEndpoint? remote;
    private SyncPlan? plan;
    private bool resume;
    public SyncRunWindow(SyncPair pair, IProvider provider, AccountStore accounts, string recovery, string journalPath, IProgress<SyncProgress>? progress = null, TransferControl? control = null, Func<string, EntryKind, SyncPair>? exclude = null)
    {
        this.control = control;
        this.pair = pair; this.provider = provider; this.accounts = accounts; this.recovery = recovery; this.progress = progress;
        runLock = new Mutex(false, "Local\\MYSync-pair-" + pair.Id.ToString("N"));
        bool acquired;
        try { acquired = runLock.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { runLock.Dispose(); throw new InvalidOperationException("다른 창 또는 앱에서 이 동기화 항목을 사용 중입니다."); }
        try { journal = new SyncJournal(journalPath); journal.RecoverInterrupted(); }
        catch { runLock.ReleaseMutex(); runLock.Dispose(); throw; }
        Title = "수동 동기화 — " + pair.RemoteFolderName; Width = 940; Height = 620; MinWidth = 760; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock { Text = pair.LocalPath + "  ⇄  " + pair.RemoteFolderName + "\n검사 후 작업을 실행하세요. 삭제 항목도 반대쪽에 반영됩니다.", TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        grid.Columns.Add(new DataGridTextColumn { Header = "작업", Binding = new System.Windows.Data.Binding("Action"), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "상대 경로", Binding = new System.Windows.Data.Binding("Path"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "이유", Binding = new System.Windows.Data.Binding("Reason"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(grid, 1); layout.Children.Add(grid);
        Grid.SetRow(status, 2); layout.Children.Add(status);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) }; buttons.Children.Add(inspect); buttons.Children.Add(execute); buttons.Children.Add(cancel);
        foreach (var label in new[] { "파일 중지", "파일 취소", "파일 재개", "파일 제외" })
        {
            var action = new Button { Content = label, Margin = new Thickness(8,0,0,0), Padding = new Thickness(8,6,8,6) };
            action.Click += async (_, _) =>
            {
                if (grid.SelectedItem is not PlannedOperation selected) return;
                try
                {
                    if (label == "파일 재개") control?.Resume(selected.Path);
                    else if (label == "파일 제외" && exclude is not null)
                    {
                        operation?.Cancel();
                        if (executionTask is not null) await executionTask;
                        pair = exclude(selected.Path, (selected.ExpectedLocal ?? selected.ExpectedRemote)?.Kind ?? EntryKind.File);
                        plan = null; status.Text = "제외했습니다. 변경 검사로 나머지 파일을 다시 확인하세요.";
                    }
                    else control?.Hold(selected.Path, label == "파일 중지" ? "중지" : "취소");
                }
                catch (Exception ex) { status.Text = ex.Message; }
            };
            buttons.Children.Add(action);
        }
        Grid.SetRow(buttons, 3); layout.Children.Add(buttons); Content = layout;
        inspect.Click += async (_, _) => await InspectAsync(); execute.Click += async (_, _) => { executionTask = ExecuteAsync(); await executionTask; }; cancel.Click += (_, _) => operation?.Cancel();
        Closing += (_, e) => { if (operation is not null) { e.Cancel = true; operation.Cancel(); status.Text = "중단 처리 중입니다. 완료 후 창을 닫으세요."; } };
        Closed += (_, _) => { runLock.ReleaseMutex(); runLock.Dispose(); };
        status.Text = "수동 실행입니다. 원격 파일 내용 검사에는 다운로드가 필요할 수 있습니다.";
    }
    private void Start()
    { operation = new CancellationTokenSource(); inspect.IsEnabled = false; execute.IsEnabled = false; cancel.IsEnabled = true; }
    private void Finish()
    { operation?.Dispose(); operation = null; inspect.IsEnabled = true; cancel.IsEnabled = false; }
    private async Task InspectAsync()
    {
        Start(); plan = null; grid.ItemsSource = null;
        Dictionary<string,string>? values = null;
        try
        {
            var ct = operation!.Token;
            if (provider is not ITransferProvider transfer || provider is not IConfigurableProvider configurable || pair.AccountId is null) throw new InvalidOperationException("전송 가능한 저장 계정이 필요합니다.");
            var account = accounts.List().SingleOrDefault(x => x.Id == pair.AccountId && x.ProviderId == pair.ProviderId) ?? throw new InvalidOperationException("계정을 찾을 수 없습니다.");
            status.Text = "계정 연결 및 파일 검사 중…";
            values = accounts.ReadValues(account); await configurable.ConnectAsync(values, ct);
            var policy = new SyncPolicy(pair.Exclusions, pair.SpeedLimitKiB);
            journal.SkipExcluded(pair.Id, policy.Exclusions);
            local = new PolicyEndpoint(new LocalEndpoint(pair.LocalPath, recovery), policy); remote = new PolicyEndpoint(transfer.OpenEndpoint(pair.RemoteFolderId), policy);
            var left = await local.ScanAsync(ct); var right = await remote.ScanAsync(ct);
            if (left.IsComplete && right.IsComplete) journal.CommitVerifiedPaths(pair.Id, left, right);
            plan = SyncPlanner.Compare(left, right, journal.ReadBaseline(pair.Id));
            if (!plan.CanExecute) throw new InvalidOperationException(string.Join(" / ", plan.Errors));
            journal.RefreshPlan(pair.Id, plan);
            var existing = journal.ReadJobs(pair.Id).Where(x => x.State != JobState.Completed).ToArray(); resume = existing.Length > 0;
            grid.ItemsSource = resume ? existing.Select(x => x.FailureReason is null ? x.Operation :
                x.Operation with { Reason = $"[{x.FailureKind?.ToString() ?? "중단"}] {x.FailureReason} ({x.FailedAt})" }).ToArray() : plan.Operations;
            execute.Content = resume ? "미완료 작업 재검사·재개" : "표시된 작업 실행";
            var skipped = plan.Unsupported.Count == 0 ? "" :
                $"\n미지원 항목 {plan.Unsupported.Count}개(동기화하지 않고 양쪽 모두 그대로 둡니다): " +
                string.Join(" / ", plan.Unsupported.Take(5).Select(x => x.Path + " — " + x.Reason)) + (plan.Unsupported.Count > 5 ? " …" : "");
            status.Text = (resume ? "미완료 작업을 표시합니다. 실행 시 현재 상태를 다시 확인합니다." : $"{plan.Operations.Count}개 작업. Upload=업로드, Download=다운로드, Delete=삭제, Conflict=충돌 보존.") + skipped;
            execute.IsEnabled = true;
        }
        catch (OperationCanceledException) { status.Text = "검사를 중단했습니다."; }
        catch (Exception ex) { status.Text = "검사 실패: " + ex.Message; progress?.Report(new(SyncPhase.Attention, status.Text)); }
        finally { values?.Clear(); Finish(); }
    }
    private async Task ExecuteAsync()
    {
        if (plan is null || local is null || remote is null) return;
        Start();
        try
        {
            status.Text = "동기화 중…";
            if (!resume) journal.Enqueue(pair.Id, plan);
            var report = await new SyncExecutor(journal).RunAsync(pair.Id, local, remote, operation!.Token, progress, control);
            status.Text = (report.Converged ? "양쪽 폴더의 내용이 일치합니다. 동기화를 완료했습니다." : "완료되지 않은 항목이 있습니다. " + string.Join(" / ", report.Issues))
                + (report.Notices.Count == 0 ? "" : "\n미지원 항목 " + report.Notices.Count + "개: " + string.Join(" / ", report.Notices.Take(5)));
        }
        catch (OperationCanceledException) { status.Text = "중단했습니다. 다시 검사해 미완료 작업을 재개할 수 있습니다."; }
        catch (Exception ex) { status.Text = "실행 실패: " + ex.Message; }
        finally { plan = null; Finish(); }
    }
}
