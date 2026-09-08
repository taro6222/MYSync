using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MYSync.Provider.Abstractions;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

namespace MYSync.Desktop;

/// <summary>Shows unresolved jobs, preserved conflict copies and recovery records, and applies explicit user decisions.</summary>
public sealed class ResolveWindow : Window
{
    private sealed record JobRow(long Id, string State, string Action, string Path, string Reason, string Copies, bool Resolvable);

    public Dictionary<string, string> ResolvedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransferControl control;
    private readonly IProgress<SyncProgress>? progress;
    private string? selectedPath;
    private readonly Button skipOnce = new() { Content = "이번만 제외·나머지 실행", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
    private readonly SyncPair pair;
    private readonly IProvider provider;
    private readonly AccountStore accounts;
    private readonly SyncJournal journal;
    private readonly RecoveryStore recoveryStore;
    private readonly string recovery;

    private readonly DataGrid jobs = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly DataGrid records = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Button scan = new() { Content = "연결·현재 상태 검사", Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button keepLocal = new() { Content = "로컬 → 클라우드 전송", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button keepRemote = new() { Content = "클라우드 → 로컬 전송", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button reload = new() { Content = "다시 조회", Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button restore = new() { Content = "보관본을 다른 위치로 복원…", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button acknowledge = new() { Content = "확인 처리", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
    private readonly CheckBox reviewed = new() { Content = "보관본과 현재 대상을 직접 비교했습니다", Margin = new Thickness(0, 12, 0, 0) };

    private readonly Mutex runLock;
    private CancellationTokenSource? operation;
    private ISyncEndpoint? local;
    private ISyncEndpoint? remote;
    private ScanResult? localScan;
    private ScanResult? remoteScan;

    public ResolveWindow(SyncPair pair, IProvider provider, AccountStore accounts, string recovery, string journalPath, TransferControl? control = null, IProgress<SyncProgress>? progress = null, string? selectedPath = null)
    {
        this.control = control ?? new(); this.progress = progress; this.selectedPath = selectedPath;
        this.pair = pair; this.provider = provider; this.accounts = accounts; this.recovery = recovery;
        recoveryStore = new RecoveryStore(recovery);
        runLock = new Mutex(false, "Local\\MYSync-pair-" + pair.Id.ToString("N"));
        bool acquired;
        try { acquired = runLock.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { runLock.Dispose(); throw new InvalidOperationException("다른 창 또는 앱에서 이 동기화 항목을 사용 중입니다."); }
        try { journal = new SyncJournal(journalPath); journal.RecoverInterrupted(); }
        catch { runLock.ReleaseMutex(); runLock.Dispose(); throw; }

        Title = "충돌·복구 확인 — " + pair.RemoteFolderName;
        Width = 1000; Height = 680; MinWidth = 820; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = pair.LocalPath + "  ⇄  " + pair.RemoteFolderName + "\n미해결 작업과 복구 기록을 확인하고 처리합니다. 보존된 원본은 삭제하지 않습니다.",
            TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 0, 0, 14)
        });

        jobs.Columns.Add(new DataGridTextColumn { Header = "상태", Binding = new System.Windows.Data.Binding("State"), Width = 110 });
        jobs.Columns.Add(new DataGridTextColumn { Header = "작업", Binding = new System.Windows.Data.Binding("Action"), Width = 100 });
        jobs.Columns.Add(new DataGridTextColumn { Header = "상대 경로", Binding = new System.Windows.Data.Binding("Path"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        jobs.Columns.Add(new DataGridTextColumn { Header = "이유", Binding = new System.Windows.Data.Binding("Reason"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        jobs.Columns.Add(new DataGridTextColumn { Header = "보존 사본", Binding = new System.Windows.Data.Binding("Copies"), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
        jobs.SelectionChanged += (_, _) => UpdateJobButtons();

        records.Columns.Add(new DataGridTextColumn { Header = "동작", Binding = new System.Windows.Data.Binding("Action"), Width = 80 });
        records.Columns.Add(new DataGridTextColumn { Header = "상대 경로", Binding = new System.Windows.Data.Binding("RelativePath"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        records.Columns.Add(new DataGridTextColumn { Header = "판정", Binding = new System.Windows.Data.Binding("OutcomeText"), Width = 160 });
        records.Columns.Add(new DataGridCheckBoxColumn { Header = "검증됨", Binding = new System.Windows.Data.Binding("Verified"), Width = 70 });
        records.Columns.Add(new DataGridTextColumn { Header = "보관 경로", Binding = new System.Windows.Data.Binding("BackupPath"), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
        records.SelectionChanged += (_, _) => UpdateRecordButtons();

        var jobButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        jobButtons.Children.Add(scan); jobButtons.Children.Add(keepLocal); jobButtons.Children.Add(keepRemote); jobButtons.Children.Add(skipOnce);
        var jobPanel = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(jobButtons, Dock.Bottom); jobPanel.Children.Add(jobButtons); jobPanel.Children.Add(jobs);

        var recordButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        recordButtons.Children.Add(reload); recordButtons.Children.Add(restore); recordButtons.Children.Add(acknowledge);
        var recordFooter = new StackPanel();
        recordFooter.Children.Add(detail); recordFooter.Children.Add(reviewed); recordFooter.Children.Add(recordButtons);
        var recordPanel = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(recordFooter, Dock.Bottom); recordPanel.Children.Add(recordFooter); recordPanel.Children.Add(records);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "미해결 작업", Content = jobPanel });
        tabs.Items.Add(new TabItem { Header = "복구 기록", Content = recordPanel });
        Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
        Grid.SetRow(status, 2); layout.Children.Add(status);
        Content = layout;

        scan.Click += async (_, _) => await ScanAsync();
        keepLocal.Click += async (_, _) => await ResolveAsync(true);
        keepRemote.Click += async (_, _) => await ResolveAsync(false);
        skipOnce.Click += async (_, _) => await SkipOnceAsync();
        reload.Click += async (_, _) => await LoadRecordsAsync();
        restore.Click += async (_, _) => await RestoreAsync();
        acknowledge.Click += async (_, _) => await AcknowledgeAsync();
        reviewed.Click += (_, _) => UpdateRecordButtons();
        Closing += (_, e) => { if (operation is not null) { e.Cancel = true; operation.Cancel(); status.Text = "중단 처리 중입니다. 완료 후 창을 닫으세요."; } };
        Closed += (_, _) => { runLock.ReleaseMutex(); runLock.Dispose(); };

        LoadJobs();
        Loaded += async (_, _) => { await LoadRecordsAsync(); await ScanAsync(); };
    }

    private void LoadJobs()
    {
        selectedPath = (jobs.SelectedItem as JobRow)?.Path ?? selectedPath;
        var rows = journal.ReadJobs(pair.Id).Where(x => x.State != JobState.Completed).Select(x => new JobRow(
            x.Id,
            x.State switch { JobState.NeedsReconcile => "확인 필요", JobState.Pending => "대기", JobState.Running => "실행 중", _ => "적용됨" },
            x.Operation.Action.ToString(), x.Operation.Path,
            x.FailureReason is null ? x.Operation.Reason : $"[{x.FailureKind?.ToString() ?? "중단"}] {x.FailureReason} ({x.FailedAt})",
            x.Operation.Action == SyncAction.Conflict ? Copies(x) : "",
            x.State != JobState.Running
                && x.Operation.ExpectedLocal?.Kind != EntryKind.Directory && x.Operation.ExpectedRemote?.Kind != EntryKind.Directory)).ToArray();
        jobs.ItemsSource = rows;
        jobs.SelectedItem = rows.FirstOrDefault(x => x.Path == selectedPath) ?? rows.FirstOrDefault();
        UpdateJobButtons();
        if (rows.Length == 0) status.Text = "미해결 작업이 없습니다.";
        else status.Text = $"미해결 작업 {rows.Length}개. 항목을 선택해 전송 방향을 지정하거나 이번 실행에서 제외하세요.";
    }

    private string Copies(JournalJob job)
    {
        var names = new List<string>();
        if (job.Operation.ExpectedLocal is not null) names.Add(job.Operation.Path + $".conflict-{pair.Id:N}-{job.Id}-local");
        if (job.Operation.ExpectedRemote is not null) names.Add(job.Operation.Path + $".conflict-{pair.Id:N}-{job.Id}-remote");
        return string.Join(" , ", names);
    }

    private void UpdateJobButtons()
    {
        var row = jobs.SelectedItem as JobRow;
        var ready = operation is null && row is { Resolvable: true } && localScan is not null && remoteScan is not null;
        keepLocal.IsEnabled = ready && localScan!.Entries.Any(x => x.Path == row!.Path && x.Kind == EntryKind.File);
        keepRemote.IsEnabled = ready && remoteScan!.Entries.Any(x => x.Path == row!.Path && x.Kind == EntryKind.File);
        skipOnce.IsEnabled = operation is null && row is not null && local is not null && remote is not null;
    }

    private void UpdateRecordButtons()
    {
        var row = records.SelectedItem as RecoveryInspection;
        restore.IsEnabled = operation is null && row is { BackupExists: true };
        acknowledge.IsEnabled = operation is null && row is not null && (!row.NeedsReview || reviewed.IsChecked == true);
        detail.Text = row is null ? "" : row.Summary + "\n보관 경로: " + row.BackupPath;
    }

    private void Start() { operation = new CancellationTokenSource(); scan.IsEnabled = reload.IsEnabled = false; skipOnce.IsEnabled = keepLocal.IsEnabled = keepRemote.IsEnabled = restore.IsEnabled = acknowledge.IsEnabled = false; }
    private void Finish() { operation?.Dispose(); operation = null; scan.IsEnabled = reload.IsEnabled = true; UpdateJobButtons(); UpdateRecordButtons(); }

    private async Task LoadRecordsAsync()
    {
        Start();
        try
        {
            var found = await recoveryStore.InspectAsync(operation!.Token);
            records.ItemsSource = found;
            var pending = found.Count(x => !x.Verified || x.NeedsReview);
            status.Text = found.Count == 0
                ? "복구 기록이 없습니다."
                : $"복구 기록 {found.Count}개. 확인이 필요한 항목 {pending}개. 확인 처리한 기록과 보관본은 {RecoveryStore.ResolvedFolderName} 폴더로 옮깁니다.";
        }
        catch (OperationCanceledException) { status.Text = "조회를 중단했습니다."; }
        catch (Exception ex) { status.Text = "복구 기록 조회 실패: " + ex.Message; }
        finally { Finish(); }
    }

    private async Task ScanAsync()
    {
        Start(); localScan = null; remoteScan = null;
        Dictionary<string, string>? values = null;
        try
        {
            var ct = operation!.Token;
            if (provider is not ITransferProvider transfer || provider is not IConfigurableProvider configurable || pair.AccountId is null)
                throw new InvalidOperationException("전송 가능한 저장 계정이 필요합니다.");
            var account = accounts.List().SingleOrDefault(x => x.Id == pair.AccountId && x.ProviderId == pair.ProviderId) ?? throw new InvalidOperationException("계정을 찾을 수 없습니다.");
            status.Text = "계정 연결 및 현재 상태 검사 중…";
            values = accounts.ReadValues(account);
            await configurable.ConnectAsync(values, ct);
            var policy = new SyncPolicy(pair.Exclusions, pair.SpeedLimitKiB);
            journal.SkipExcluded(pair.Id, policy.Exclusions);
            local = new PolicyEndpoint(new LocalEndpoint(pair.LocalPath, recovery), policy);
            remote = new PolicyEndpoint(transfer.OpenEndpoint(pair.RemoteFolderId), policy);
            var left = await local.ScanAsync(ct);
            if (!left.IsComplete) throw new InvalidOperationException("로컬 검사 실패: " + string.Join(" / ", left.Errors));
            var right = await remote.ScanAsync(ct);
            if (!right.IsComplete) throw new InvalidOperationException("원격 검사 실패: " + string.Join(" / ", right.Errors));
            var previous = journal.ReadJobs(pair.Id).Where(x => x.State != JobState.Completed).Select(x => x.Operation.Path).Append(selectedPath).OfType<string>().Distinct().ToArray();
            journal.CommitVerifiedPaths(pair.Id, left, right);
            journal.RefreshPlan(pair.Id, SyncPlanner.Compare(left, right, journal.ReadBaseline(pair.Id)));
            localScan = left; remoteScan = right;
            foreach (var path in previous)
                if (!journal.ReadJobs(pair.Id).Any(x => x.State != JobState.Completed && x.Operation.Path == path))
                    ResolvedPaths[path] = "재검사 완료";
            LoadJobs();
            status.Text = "현재 상태로 다시 판단했습니다. 같은 내용은 정리했습니다. 남은 항목은 전송 방향 또는 이번만 제외를 선택하세요.";
        }
        catch (OperationCanceledException) { status.Text = "검사를 중단했습니다."; }
        catch (Exception ex) { status.Text = "검사 실패: " + ex.Message; }
        finally { values?.Clear(); Finish(); }
    }

    private async Task ResolveAsync(bool preferLocal)
    {
        if (jobs.SelectedItem is not JobRow row || local is null || remote is null) return;
        var side = preferLocal ? "로컬 → 클라우드" : "클라우드 → 로컬";
        if (MessageBox.Show(this, $"{row.Path}\n\n{side} 방향으로 현재 파일을 전송합니다. 기존 양쪽 내용은 사본으로 보존합니다.\n권한·버전·무결성 검사는 유지됩니다.",
            "선택 파일 덮어쓰기", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Start();
        try
        {
            var message = await FileResolution.ApplyAsync(journal, pair.Id, row.Path, preferLocal, local, remote, operation!.Token, progress, control);
            ResolvedPaths[row.Path] = "해결됨";
            LoadJobs(); status.Text = row.Path + ": " + message;
        }
        catch (OperationCanceledException) { status.Text = "전송을 중단했습니다. 다시 검사하세요."; }
        catch (Exception ex) { status.Text = "선택 파일 처리 실패: " + ex.Message; }
        finally { localScan = null; remoteScan = null; Finish(); }
    }
    private async Task SkipOnceAsync()
    {
        if (jobs.SelectedItem is not JobRow row || local is null || remote is null) return;
        Start();
        try
        {
            control.SkipOnce(row.Path);
            await new SyncExecutor(journal).RunAsync(pair.Id, local, remote, operation!.Token, progress, control);
            ResolvedPaths[row.Path] = "이번 실행 제외";
            LoadJobs(); status.Text = row.Path + ": 이번 실행에서 제외하고 나머지 작업을 처리했습니다. 다음 검사에서는 다시 대상이 됩니다.";
        }
        catch (OperationCanceledException) { status.Text = "실행을 중단했습니다."; }
        catch (Exception ex) { status.Text = "처리 실패: " + ex.Message; }
        finally { localScan = null; remoteScan = null; Finish(); }
    }

    private async Task RestoreAsync()
    {
        if (records.SelectedItem is not RecoveryInspection row) return;
        var dialog = new OpenFolderDialog { Title = "보관본을 복원할 폴더 선택" };
        if (dialog.ShowDialog(this) != true) return;
        Start();
        try
        {
            var written = await recoveryStore.RestoreCopyAsync(row, dialog.FolderName, operation!.Token);
            status.Text = "보관본을 복원했습니다: " + written;
        }
        catch (OperationCanceledException) { status.Text = "복원을 중단했습니다."; }
        catch (Exception ex) { status.Text = "복원 실패: " + ex.Message; }
        finally { Finish(); }
    }

    private async Task AcknowledgeAsync()
    {
        if (records.SelectedItem is not RecoveryInspection row) return;
        if (MessageBox.Show(this, $"{row.RelativePath}\n\n{row.Summary}\n\n이 기록을 확인 처리하면 동기화 차단이 풀립니다. 보관본은 {RecoveryStore.ResolvedFolderName} 폴더에 남습니다. 계속할까요?",
            "복구 기록 확인", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Start();
        try
        {
            var written = await recoveryStore.AcknowledgeAsync(row, reviewed.IsChecked == true, operation!.Token);
            status.Text = "확인 처리했습니다: " + written;
            reviewed.IsChecked = false;
        }
        catch (OperationCanceledException) { status.Text = "처리를 중단했습니다."; }
        catch (Exception ex) { status.Text = "확인 처리 실패: " + ex.Message; }
        finally { Finish(); await LoadRecordsAsync(); }
    }
}
