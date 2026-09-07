using System.IO;
using System.Windows;
using System.Windows.Controls;
using MYSync.Provider.Abstractions;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

namespace MYSync.Desktop;

public sealed class SyncRunWindow : Window
{
    private readonly SyncPair pair;
    private readonly IProvider provider;
    private readonly AccountStore accounts;
    private readonly SyncJournal journal;
    private readonly string recovery;
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
    public SyncRunWindow(SyncPair pair, IProvider provider, AccountStore accounts, string recovery, string journalPath)
    {
        this.pair = pair; this.provider = provider; this.accounts = accounts; this.recovery = recovery;
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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) }; buttons.Children.Add(inspect); buttons.Children.Add(execute); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3); layout.Children.Add(buttons); Content = layout;
        inspect.Click += async (_, _) => await InspectAsync(); execute.Click += async (_, _) => await ExecuteAsync(); cancel.Click += (_, _) => operation?.Cancel();
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
            local = new LocalEndpoint(pair.LocalPath, recovery); remote = transfer.OpenEndpoint(pair.RemoteFolderId);
            var left = await local.ScanAsync(ct); var right = await remote.ScanAsync(ct);
            plan = SyncPlanner.Compare(left, right, journal.ReadBaseline(pair.Id));
            if (!plan.CanExecute) throw new InvalidOperationException(string.Join(" / ", plan.Errors));
            var existing = journal.ReadJobs(pair.Id).Where(x => x.State != JobState.Completed).ToArray(); resume = existing.Length > 0;
            grid.ItemsSource = resume ? existing.Select(x => x.Operation).ToArray() : plan.Operations;
            execute.Content = resume ? "미완료 작업 재검사·재개" : "표시된 작업 실행";
            status.Text = resume ? "미완료 작업을 표시합니다. 실행 시 현재 상태를 다시 확인합니다." : $"{plan.Operations.Count}개 작업. Upload=업로드, Download=다운로드, Delete=삭제, Conflict=충돌 보존.";
            execute.IsEnabled = true;
        }
        catch (OperationCanceledException) { status.Text = "검사를 중단했습니다."; }
        catch (Exception ex) { status.Text = "검사 실패: " + ex.Message; }
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
            var report = await new SyncExecutor(journal).RunAsync(pair.Id, local, remote, operation!.Token);
            status.Text = report.Converged ? "양쪽 폴더의 내용이 일치합니다. 동기화를 완료했습니다." : "완료되지 않은 항목이 있습니다. " + string.Join(" / ", report.Issues);
        }
        catch (OperationCanceledException) { status.Text = "중단했습니다. 다시 검사해 미완료 작업을 재개할 수 있습니다."; }
        catch (Exception ex) { status.Text = "실행 실패: " + ex.Message; }
        finally { plan = null; Finish(); }
    }
}
