using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MYSync.PluginHost;
using MYSync.Provider.Abstractions;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;
namespace MYSync.Desktop;
public sealed class MainViewModel
{
    public ObservableCollection<IProvider> Providers { get; } = [];
    public ObservableCollection<SyncPair> Pairs { get; } = [];
    public ObservableCollection<SyncAlert> Alerts { get; } = [];
    public TransferHistory History { get; } = new();
    public ObservableCollection<TransferRow> Transfers => History.Rows;
}
public partial class MainWindow : Window
{
    private readonly PluginCatalog catalog = new();
    private readonly SettingsStore store;
    private readonly AccountStore accountStore;
    private Guid? activeAccountId;
    private readonly Dictionary<Guid, TransferControl> transferControls = [];
    private TransferControl ControlFor(Guid id) { if (!transferControls.TryGetValue(id, out var value)) transferControls[id] = value = new(); return value; }
    private string draftExclusions = "";
    private long draftSpeed;
    private readonly Queue<SyncAlert> notificationQueue = new();
    private readonly Dictionary<Guid, string> lastAlert = [];
    private SyncAlert? visibleAlert;
    private readonly MainViewModel model = new();
    private sealed record AutoRun(SyncMonitor Monitor, IProvider Provider, Mutex RunLock)
    {
        public Task? Stopping { get; set; }
        public MonitorState State { get; set; } = MonitorState.Watching;
        public string Message { get; set; } = "변경 감시 중";
    }
    private readonly Dictionary<Guid, AutoRun> autoRuns = [];
    private readonly System.Windows.Threading.DispatcherTimer indicatorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private System.Drawing.Icon? appIcon;
    private bool closing;
    private bool exitRequested;
    private readonly DesktopPreferencesStore preferencesStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "preferences.json"));
    private DesktopPreferences preferences = new();
    private readonly System.Windows.Forms.NotifyIcon tray = new();
    private readonly System.Windows.Forms.ToolStripMenuItem trayStatus = new("MYSync · 준비됨") { Enabled = false };
    private readonly System.Windows.Forms.ToolStripMenuItem trayPause = new("모두 일시정지");
    public MainWindow()
    {
        InitializeComponent();
        store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "settings.db"));
        accountStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "settings.db"));
        catalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        foreach (var p in catalog.Providers) model.Providers.Add(p);
        foreach (var p in store.Load()) model.Pairs.Add(p);
        DataContext = model;
        MainTabs.IsEnabledChanged += (_, _) => UpdatePreview();
        try { preferences = preferencesStore.Load(); }
        catch (Exception ex) { StatusText.Text = "앱 설정 읽기 실패: " + ex.Message; }
        SyncDiagnostics.DirectoryPath = Path.Combine(AppContext.BaseDirectory, ",log");
        SyncDiagnostics.MaxBytes = Math.Clamp(preferences.LogLimitMiB, 1, 1024) * 1024L * 1024;
        LogLimitBox.Text = preferences.LogLimitMiB.ToString();
        LogPathText.Text = SyncDiagnostics.DirectoryPath;
        CloseToTrayBox.IsChecked = preferences.CloseToTray;
        try { StartupBox.IsChecked = StartupRegistration.Enabled; }
        catch (Exception ex) { StatusText.Text = "로그인 설정 읽기 실패: " + ex.Message; }
        InitializeTray();
        model.Pairs.CollectionChanged += (_, _) => UpdateIndicators();
        indicatorTimer.Tick += (_, _) => { foreach (var row in model.Transfers.Where(x => !x.Done)) row.Refresh(); UpdateIndicators(); };
        indicatorTimer.Start();
        UpdateIndicators();
        if (catalog.Issues.Count > 0) StatusText.Text = string.Join(" / ", catalog.Issues.Select(x => x.Message));
        else if (model.Providers.Count == 0) StatusText.Text = "로드된 Provider가 없습니다. plugins 폴더에 설치하고 재시작하세요.";
        Closed += (_, _) => { tray.Visible = false; tray.Dispose(); appIcon?.Dispose(); indicatorTimer.Stop(); catalog.Dispose(); };
        Loaded += (_, _) =>
        {
            foreach (var pair in model.Pairs.Where(x => !x.Paused).ToArray())
            { try { StartAuto(pair); } catch (Exception ex) { SetPaused(pair.Id, true); StatusText.Text = ex.Message; AddAlert(pair, ex.Message); } }
            if (Environment.GetCommandLineArgs().Contains("--background", StringComparer.Ordinal)) Hide();
        };
        Closing += async (_, e) =>
        {
            if (!exitRequested && preferences.CloseToTray) { e.Cancel = true; Hide(); return; }
            if (autoRuns.Count == 0) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; MainTabs.IsEnabled = false;
            foreach (var id in autoRuns.Keys.ToArray()) await StopAuto(id, false);
            Close();
        };
    }
    private void InitializeTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(trayStatus); menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("MYSync 열기", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
        trayPause.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (closing) return;
            MainTabs.IsEnabled = false; trayPause.Enabled = false;
            try { foreach (var id in autoRuns.Keys.ToArray()) await StopAuto(id, true); StatusText.Text = "모든 자동 동기화를 일시정지했습니다."; }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { if (!closing) MainTabs.IsEnabled = true; trayPause.Enabled = true; UpdateTray(); }
        }));
        menu.Items.Add(trayPause);
        menu.Items.Add("종료", null, (_, _) => Dispatcher.BeginInvoke(new Action(RequestExit)));
        using (var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("MYSync.Desktop.Assets.MYSync.ico")!)
            appIcon = new System.Drawing.Icon(iconStream);
        tray.Icon = appIcon; tray.Text = "MYSync · 준비됨"; tray.ContextMenuStrip = menu;
        tray.BalloonTipClicked += (_, _) =>
        {
            var target = visibleAlert;
            Dispatcher.BeginInvoke(new Action(() => { if (target is not null) { ShowFromTray(); MainTabs.SelectedItem = AlertsTab; if (!model.Alerts.Contains(target)) model.Alerts.Insert(0, target); AlertsBox.SelectedItem = target; } }));
        };
        tray.BalloonTipClosed += (_, _) => Dispatcher.BeginInvoke(new Action(() => { visibleAlert = null; ShowNextNotification(); }));
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)); tray.Visible = true;
    }
    public void ShowFromTray()
    {
        if (closing) return;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
        foreach (Window owned in OwnedWindows) owned.Activate();
    }
    private void RequestExit()
    {
        if (OwnedWindows.Count > 0) { ShowFromTray(); StatusText.Text = "열린 작업 창을 닫은 후 종료하세요."; return; }
        exitRequested = true; Close();
    }
    private void ExitApp(object sender, RoutedEventArgs e) => RequestExit();
    private void UpdateTray()
    {
        trayStatus.Text = $"MYSync · 자동 켜짐 {autoRuns.Count}개 · 확인 대기 {autoRuns.Values.Count(x => x.State == MonitorState.NeedsAttention)}개";
        tray.Text = trayStatus.Text;
        UpdateIndicators();
    }
    private void CloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        try { var updated = preferences with { CloseToTray = CloseToTrayBox.IsChecked == true }; preferencesStore.Save(updated); preferences = updated; }
        catch (Exception ex) { CloseToTrayBox.IsChecked = preferences.CloseToTray; StatusText.Text = "설정 저장 실패: " + ex.Message; }
    }
    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        try { StartupRegistration.SetEnabled(StartupBox.IsChecked == true); StatusText.Text = "로그인 시 실행 설정을 변경했습니다."; }
        catch (Exception ex)
        {
            try { StartupBox.IsChecked = StartupRegistration.Enabled; } catch { StartupBox.IsChecked = false; }
            StatusText.Text = "로그인 설정 실패: " + ex.Message;
        }
    }
    private void ShowSync(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void ShowTransfers(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
    private void ShowAdd(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void OpenSync(object sender, RoutedEventArgs e) => OpenPairWindow(sender, (pair, provider, recovery, journalPath) => new SyncRunWindow(pair, provider, accountStore, recovery, journalPath, ProgressFor(pair, true), ControlFor(pair.Id), (path, kind) => SaveFileExclusion(pair.Id, path, kind)));
    private void OpenResolve(object sender, RoutedEventArgs e) => OpenPairWindow(sender, (pair, provider, recovery, journalPath) => new ResolveWindow(pair, provider, accountStore, recovery, journalPath, ControlFor(pair.Id), ProgressFor(pair)));
    private void OpenPairWindow(object sender, Func<SyncPair, IProvider, string, string, Window> create)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncPair pair) return;
        if (autoRuns.ContainsKey(pair.Id)) { StatusText.Text = "자동 동기화를 일시정지한 후 여세요."; return; }
        try
        {
            EnsureAvailable(pair);
            var provider = model.Providers.SingleOrDefault(x => x.Id == pair.ProviderId) ?? throw new InvalidOperationException("Provider가 설치되지 않았습니다.");
            var recovery = RecoveryPathFor(pair);
            var journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "journals", pair.Id.ToString("N") + ".db");
            var window = create(pair, provider, recovery, journalPath);
            window.Owner = this; window.ShowDialog();
            if (window is ResolveWindow resolved)
                foreach (var row in model.History.Rows.Where(x => x.PairId == pair.Id && resolved.ResolvedPaths.ContainsKey(x.Path) && x.State is "확인 필요" or "검증 보류" or "미실행"))
                    row.MarkHandled(resolved.ResolvedPaths[row.Path]);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { activeAccountId = null; RemoteBox.ItemsSource = null; }
    }
    private IProgress<SyncProgress> ProgressFor(SyncPair pair, bool notify = false) =>
        new Progress<SyncProgress>(report =>
        {
            model.History.Apply(pair.Id, pair.RemoteFolderName, report);
            if (report.Event != TransferEvent.None || report.Phase is SyncPhase.Idle or SyncPhase.Attention)
            { using var scope = SyncDiagnostics.BeginJob(pair.Id, 0); SyncDiagnostics.Write("transfer.state", phase: report.Phase + "/" + report.Event, bytes: report.Bytes); }
            if (notify && report.Phase == SyncPhase.Attention && report.ActivityId == Guid.Empty) AddAlert(pair, report.Message);
            UpdateIndicators();
        });
    private void UpdateIndicators()
    {
        if (SyncIndicator is null) return;
        SyncIndicator.Text = $"● 자동 {autoRuns.Count} / 연결 {model.Pairs.Count}";
        var active = model.Transfers.Count(x => !x.Done);
        FileIndicator.Text = $"작업 {active}개 · 완료 파일 {model.History.CompletedFiles}개";
        BytesIndicator.Text = "처리 " + TransferRow.Size(model.History.SessionBytes);
        TimeIndicator.Text = "최근 동기화 " + (model.History.LastSynchronized?.ToString("HH:mm:ss") ?? "—");
        var waiting = autoRuns.Values.Where(x => x.State == MonitorState.NeedsAttention).ToArray();
        if (waiting.Length > 0) SyncIndicator.Text += $" · 확인 대기 {waiting.Length}";
        SyncIndicator.Foreground = waiting.Length > 0 ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.SeaGreen;
        SyncIndicator.ToolTip = waiting.Length > 0 ? string.Join("\n", waiting.Select(x => x.Message)) : "자동 켜짐 상태에서는 완료 후에도 변경 감시를 유지합니다.";
    }
    private SavedAccount? SelectedAccount(out IConfigurableProvider? configurable)
    {
        configurable = ProvidersBox.SelectedItem as IConfigurableProvider;
        if (AccountsBox.SelectedItem is not SavedAccount account) { StatusText.Text = "저장된 계정을 선택하세요."; return null; }
        if (ProvidersBox.SelectedItem is IProvider provider && account.ProviderId != provider.Id) { StatusText.Text = "선택한 Provider의 계정이 아닙니다."; return null; }
        return account;
    }
    private void RenameAccount(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount(out _) is not { } account) return;
        var dialog = new TextPromptWindow("계정 이름 변경", "표시할 이름", account.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            accountStore.Rename(account.Id, dialog.Value);
            RefreshAccounts(account.ProviderId);
            AccountsBox.SelectedItem = AccountsBox.Items.Cast<SavedAccount>().FirstOrDefault(x => x.Id == account.Id);
            StatusText.Text = "계정 이름을 변경했습니다.";
        }
        catch (Exception ex) { StatusText.Text = "이름 변경 실패: " + ex.Message; }
    }
    private async void UpdateAccount(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount(out var configurable) is not { } account) return;
        if (configurable is null || ProvidersBox.SelectedItem is not IProvider provider) { StatusText.Text = "이 Provider는 인증 정보 수정이 없습니다."; return; }
        var connectionValues = CollectConnectionValues(configurable, provider);
        if (connectionValues is null) return;
        MainTabs.IsEnabled = false;
        activeAccountId = null;
        RemoteBox.ItemsSource = null;
        try
        {
            StatusText.Text = "새 인증 정보로 연결 확인 중…";
            await configurable.ConnectAsync(connectionValues, CancellationToken.None);
            var folders = await provider.GetFoldersAsync(null, CancellationToken.None);
            accountStore.Save(account, provider is IPersistableConnectionProvider persisted ? persisted.ExportConnectionValues() : connectionValues);
            activeAccountId = account.Id;
            SetFolders(folders);
            StatusText.Text = "인증 정보를 갱신했습니다. 기존 동기화 항목은 그대로 이 계정을 사용합니다.";
        }
        catch (Exception ex) { StatusText.Text = "인증 정보 수정 실패: " + ex.Message; }
        finally { MainTabs.IsEnabled = true; }
    }
    private void DeleteAccount(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount(out _) is not { } account) return;
        var referencing = model.Pairs.Where(x => x.AccountId == account.Id).Select(x => x.RemoteFolderName).ToArray();
        if (referencing.Length > 0)
        { StatusText.Text = "이 계정을 사용하는 동기화 항목이 있어 삭제할 수 없습니다: " + string.Join(", ", referencing); return; }
        if (MessageBox.Show(this, account.DisplayName + "\n\n저장된 인증 정보를 삭제합니다. 되돌릴 수 없습니다. 계속할까요?",
            "계정 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            accountStore.Delete(account.Id);
            if (activeAccountId == account.Id) { activeAccountId = null; RemoteBox.ItemsSource = null; }
            RefreshAccounts(account.ProviderId);
            StatusText.Text = "계정을 삭제했습니다.";
        }
        catch (Exception ex) { StatusText.Text = "계정 삭제 실패: " + ex.Message; }
    }
    private string RecoveryPathFor(SyncPair pair)
    {
        var parent = Directory.GetParent(pair.LocalPath)?.FullName ?? throw new InvalidOperationException("드라이브 전체 대신 하위 폴더를 선택하세요.");
        var recovery = Path.Combine(parent, ".MYSync-recovery", pair.Id.ToString("N"));

        return recovery;
    }
    private void ChooseLocal(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true) LocalPathBox.Text = dialog.FolderName;
    }
    private void SetPaused(Guid id, bool paused)
    {
        var index = model.Pairs.ToList().FindIndex(x => x.Id == id);
        if (index < 0) return;
        var updated = model.Pairs[index] with { Paused = paused };
        store.Save(updated); model.Pairs[index] = updated;
    }
    private async void ToggleAuto(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncPair pair) return;
        MainTabs.IsEnabled = false;
        try
        {
            if (autoRuns.ContainsKey(pair.Id)) await StopAuto(pair.Id, true);
            else StartAuto(pair);
        }
        catch (Exception ex) { StatusText.Text = "자동 동기화 설정 실패: " + ex.Message; AddAlert(pair, ex.Message); }
        finally { if (!closing) MainTabs.IsEnabled = true; }
    }
    private void StartAuto(SyncPair pair)
    {
        if (autoRuns.ContainsKey(pair.Id)) return;
        EnsureAvailable(pair);
        if (pair.AccountId is null) throw new InvalidOperationException("저장된 클라우드 계정이 필요합니다.");
        var account = accountStore.List().SingleOrDefault(x => x.Id == pair.AccountId && x.ProviderId == pair.ProviderId) ?? throw new InvalidOperationException("저장 계정을 찾을 수 없습니다.");
        var recovery = RecoveryPathFor(pair);
        var runLock = new Mutex(false, "Local\\MYSync-pair-" + pair.Id.ToString("N"));
        bool acquired;
        try { acquired = runLock.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { runLock.Dispose(); throw new InvalidOperationException("다른 앱에서 이 동기화 항목을 사용 중입니다."); }
        IProvider? session = null; SyncMonitor? monitor = null;
        try
        {
            session = catalog.CreateSession(pair.ProviderId);
            if (session is not ITransferProvider transfer || session is not IConfigurableProvider configurable) throw new InvalidOperationException("이 Provider는 자동 전송을 지원하지 않습니다.");
            var journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "journals", pair.Id.ToString("N") + ".db");
            var journal = new SyncJournal(journalPath); journal.RecoverInterrupted();
            var policy = new SyncPolicy(pair.Exclusions, pair.SpeedLimitKiB);
            journal.SkipExcluded(pair.Id, policy.Exclusions);
            var local = new PolicyEndpoint(new LocalEndpoint(pair.LocalPath, recovery), policy);
            var progress = ProgressFor(pair);
            var fileControl = ControlFor(pair.Id);
            ISyncEndpoint? remote = null;
            monitor = new SyncMonitor(pair.LocalPath, async ct =>
            {
                if (remote is null)
                {
                    var values = accountStore.ReadValues(account);
                    try { await configurable.ConnectAsync(values, ct); }
                    finally { values.Clear(); }
                    remote = new PolicyEndpoint(transfer.OpenEndpoint(pair.RemoteFolderId), policy);
                }
                var left = await local.ScanAsync(ct); var right = await remote.ScanAsync(ct);
                if (left.IsComplete && right.IsComplete) journal.CommitVerifiedPaths(pair.Id, left, right);
                var plan = SyncPlanner.Compare(left, right, journal.ReadBaseline(pair.Id));
                if (!plan.CanExecute)
                { progress.Report(new SyncProgress(SyncPhase.Attention, string.Join(" / ", plan.Errors))); return new ExecutionReport(false, plan.Errors); }
                journal.RefreshPlan(pair.Id, plan);
                return await new SyncExecutor(journal).RunAsync(pair.Id, local, remote, ct, progress, fileControl);
            });
            var run = new AutoRun(monitor, session, runLock);
            monitor.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!autoRuns.TryGetValue(pair.Id, out var current) || !ReferenceEquals(current, run)) return;
                if (status.State == MonitorState.NeedsAttention) AddAlert(pair, status.Message);
                else if (status.State == MonitorState.Watching) lastAlert.Remove(pair.Id);
                run.State = status.State; run.Message = pair.RemoteFolderName + " · " + status.Message;
                StatusText.Text = pair.RemoteFolderName + " · " + status.Message;
                UpdateTray();
            }));
            SetPaused(pair.Id, false); autoRuns.Add(pair.Id, run); monitor.Start(); UpdateTray();
        }
        catch
        {
            autoRuns.Remove(pair.Id);
            if (monitor is not null) monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            session?.Dispose(); runLock.ReleaseMutex(); runLock.Dispose();
            SetPaused(pair.Id, true); throw;
        }
    }
    private Task StopAuto(Guid id, bool persistPause)
    {
        if (!autoRuns.TryGetValue(id, out var run)) return Task.CompletedTask;
        return run.Stopping ??= StopAutoCore(id, run, persistPause);
    }
    private async Task StopAutoCore(Guid id, AutoRun run, bool persistPause)
    {
        try { await run.Monitor.DisposeAsync(); }
        finally
        {
            run.Provider.Dispose(); run.RunLock.ReleaseMutex(); run.RunLock.Dispose();
            autoRuns.Remove(id);
            model.History.Stop(id);
            UpdateTray();
            if (persistPause) SetPaused(id, true);
        }
    }
    private async void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        activeAccountId = null;
        RemoteBox.ItemsSource = null;
        if (ProvidersBox.SelectedItem is not IProvider p) return;
        RefreshAccounts(p.Id);
        ConnectProviderButton.Content = p is IBrowserLoginProvider browser ? browser.LoginButtonText : "계정 연결";
        UpdateAccountButton.Content = p is IBrowserLoginProvider ? "Google 다시 로그인" : "인증 정보 수정";
        if (p is IConfigurableProvider) { StatusText.Text = "새 계정을 연결하거나 저장 계정을 선택해 연결하세요."; return; }
        try
        {
            var folders = await p.GetFoldersAsync(null, CancellationToken.None);
            if (ReferenceEquals(ProvidersBox.SelectedItem, p)) SetFolders(folders);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private IReadOnlyDictionary<string, string>? CollectConnectionValues(IConfigurableProvider configurable, IProvider provider)
    {
        if (provider is IBrowserLoginProvider) return new Dictionary<string, string>();
        var dialog = new ProviderConnectionWindow(configurable.ConnectionFields, (provider as IPersistableConnectionProvider)?.ConnectionInstructions) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Values : null;
    }
    private async void ConnectProvider(object sender, RoutedEventArgs e)
    {
        if (ProvidersBox.SelectedItem is not IConfigurableProvider configurable) { StatusText.Text = "이 Provider는 별도 연결 설정이 없습니다."; return; }
        var provider = (IProvider)configurable;
        var connectionValues = CollectConnectionValues(configurable, provider);
        if (connectionValues is null) return;
        MainTabs.IsEnabled = false;
        activeAccountId = null;
        RemoteBox.ItemsSource = null;
        try
        {
            StatusText.Text = "연결 확인 중…";
            await configurable.ConnectAsync(connectionValues, CancellationToken.None);
            var folders = await provider.GetFoldersAsync(null, CancellationToken.None);
            var account = new SavedAccount(Guid.NewGuid(), provider.Id, $"{provider.DisplayName} · {DateTime.Now:MM-dd HH:mm:ss}");
            accountStore.Save(account, provider is IPersistableConnectionProvider persisted ? persisted.ExportConnectionValues() : connectionValues);
            RefreshAccounts(provider.Id);
            AccountsBox.SelectedItem = AccountsBox.Items.Cast<SavedAccount>().Single(x => x.Id == account.Id);
            activeAccountId = account.Id;
            SetFolders(folders);
            StatusText.Text = "계정을 암호화해 저장했습니다. 폴더를 선택해 연결을 저장하세요.";
        }
        catch (Exception ex) { StatusText.Text = "연결 실패: " + ex.Message; }
        finally { MainTabs.IsEnabled = true; }
    }
    private void RefreshAccounts(string providerId) => AccountsBox.ItemsSource = accountStore.List().Where(x => x.ProviderId == providerId).ToArray();
    private void AccountChanged(object sender, SelectionChangedEventArgs e)
    {
        activeAccountId = null;
        RemoteBox.ItemsSource = null;
        UpdatePreview();
    }
    private async void ReconnectAccount(object sender, RoutedEventArgs e)
    {
        if (ProvidersBox.SelectedItem is not IProvider provider || provider is not IConfigurableProvider configurable || AccountsBox.SelectedItem is not SavedAccount account)
        { StatusText.Text = "저장 계정을 선택하세요."; return; }
        if (account.ProviderId != provider.Id) return;
        MainTabs.IsEnabled = false;
        activeAccountId = null;
        RemoteBox.ItemsSource = null;
        Dictionary<string, string>? values = null;
        try
        {
            StatusText.Text = "저장 계정으로 연결 중…";
            values = accountStore.ReadValues(account);
            await configurable.ConnectAsync(values, CancellationToken.None);
            var folders = await provider.GetFoldersAsync(null, CancellationToken.None);
            activeAccountId = account.Id;
            SetFolders(folders);
            StatusText.Text = "저장 계정으로 연결했습니다.";
            if (provider is IPersistableConnectionProvider persisted) accountStore.Save(account, persisted.ExportConnectionValues());
        }
        catch (System.Security.Cryptography.CryptographicException) { StatusText.Text = "이 Windows 사용자로 계정 정보를 복호화할 수 없습니다. 새 계정을 연결하세요."; }
        catch (Exception ex) { StatusText.Text = "재연결 실패: " + ex.Message; }
        finally { values?.Clear(); MainTabs.IsEnabled = true; }
    }
    private async Task Browse(string? folderId)
    {
        if (ProvidersBox.SelectedItem is not IProvider provider) return;
        if (provider is IConfigurableProvider && activeAccountId is null) { StatusText.Text = "저장 계정을 먼저 연결하세요."; return; }
        MainTabs.IsEnabled = false;
        try
        {
            var folders = await provider.GetFoldersAsync(folderId, CancellationToken.None);
            if (ReferenceEquals(ProvidersBox.SelectedItem, provider)) SetFolders(folders);
        }
        catch (Exception ex) { StatusText.Text = "폴더 조회 실패: " + ex.Message; }
        finally { MainTabs.IsEnabled = true; }
    }
    private async void BrowseRemote(object sender, RoutedEventArgs e)
    { if (RemoteBox.SelectedItem is RemoteFolder folder) await Browse(folder.Id); }
    private async void BrowseRoot(object sender, RoutedEventArgs e) => await Browse(null);
    private void SavePair(object sender, RoutedEventArgs e)
    {
        if (ProvidersBox.SelectedItem is not IProvider p || RemoteBox.SelectedItem is not RemoteFolder folder || !Directory.Exists(LocalPathBox.Text))
        { StatusText.Text = "Provider와 로컬·원격 폴더를 선택하세요."; return; }
        if (p is IConfigurableProvider && activeAccountId is null) { StatusText.Text = "계정을 먼저 연결하세요."; return; }
        if (p is IConfigurableProvider && p is not ITransferProvider) { StatusText.Text = "이 Provider는 현재 연결·탐색 단계입니다. 전송 지원 후 동기화 폴더를 저장할 수 있습니다."; return; }
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalPathBox.Text));
        if (Directory.GetParent(path) is null || path.Split(Path.DirectorySeparatorChar).Any(x => x.Equals(".MYSync-recovery", StringComparison.OrdinalIgnoreCase)))
        { StatusText.Text = "드라이브 전체 또는 복구 보관함 대신 일반 하위 폴더를 선택하세요."; return; }
        try
        {
            var pair = new SyncPair(Guid.NewGuid(), p.Id, path, folder.Id, folder.Name, true, activeAccountId, draftExclusions, draftSpeed);
            store.Save(pair); model.Pairs.Add(pair); MainTabs.SelectedIndex = 0;
            StatusText.Text = "저장했습니다. 검사·실행 또는 자동 시작을 선택하세요.";
        }
        catch (Exception ex) { StatusText.Text = "저장 실패: " + ex.Message; }
    }
    private void SetFolders(IReadOnlyList<RemoteFolder> folders)
    {
        RemoteBox.ItemsSource = folders;
        RemoteBox.SelectedIndex = folders.Count > 0 ? 0 : -1;
        UpdatePreview();
    }
    private void PreviewChanged(object sender, RoutedEventArgs e) => UpdatePreview();
    private void UpdatePreview()
    {
        if (SavePairButton is null) return;
        var folder = RemoteBox.SelectedItem as RemoteFolder;
        PreviewLocal.Text = string.IsNullOrWhiteSpace(LocalPathBox.Text) ? "컴퓨터 폴더를 선택하세요" : LocalPathBox.Text;
        PreviewRemote.Text = folder?.Name ?? "클라우드 폴더를 선택하세요";
        var connected = ProvidersBox.SelectedItem is IProvider p && (p is not IConfigurableProvider || activeAccountId is not null);
        ConnectionState.Text = connected ? "● 연결됨 · 동기화할 폴더를 선택하세요" : "계정을 연결하면 클라우드 폴더가 표시됩니다.";
        var valid = Directory.Exists(LocalPathBox.Text);
        var count = valid ? model.Pairs.Count(x => Overlaps(x.LocalPath, LocalPathBox.Text)) : 0;
        OverlapHint.Text = count > 0 ? $"기존 연결 {count}개와 경로가 겹칩니다. 추가할 수 있으며, 겹치는 연결은 하나씩 실행하세요." : "양방향 동기화 · 저장 후 검사·실행으로 변경 내용을 확인하세요.";
        SavePairButton.IsEnabled = connected && valid && folder is not null && MainTabs.IsEnabled;
    }
    private void EnsureAvailable(SyncPair pair)
    {
        if (model.Pairs.Any(x => x.Id != pair.Id && autoRuns.ContainsKey(x.Id) && Overlaps(x.LocalPath, pair.LocalPath)))
            throw new InvalidOperationException("겹치는 로컬 폴더의 자동 동기화를 먼저 일시정지하세요.");
    }
    private async void DeletePair(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncPair pair) return;
        if (MessageBox.Show(this, pair.LocalPath + "\\n⇄ " + pair.RemoteFolderName + "\\n\\n이 동기화 연결을 삭제할까요? 실행 중인 감시를 중지합니다.\\n컴퓨터·클라우드 파일, 계정 및 복구 기록은 유지됩니다.",
            "동기화 연결 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        MainTabs.IsEnabled = false;
        try
        {
            await StopAuto(pair.Id, true);
            store.Delete(pair.Id);
            var saved = model.Pairs.FirstOrDefault(x => x.Id == pair.Id);
            if (saved is not null) model.Pairs.Remove(saved);
            model.History.Stop(pair.Id);
            StatusText.Text = "동기화 연결을 삭제했습니다. 실제 파일과 복구 기록은 유지됩니다.";
        }
        catch (Exception ex) { StatusText.Text = "연결 삭제 실패: " + ex.Message; }
        finally { MainTabs.IsEnabled = true; }
    }
    private void EditDraftOptions(object sender, RoutedEventArgs e)
    {
        var dialog = new PairOptionsWindow(draftExclusions, draftSpeed) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        draftExclusions = dialog.Exclusions; draftSpeed = dialog.SpeedLimitKiB;
        StatusText.Text = "새 연결의 제외·속도 설정을 적용했습니다.";
    }
    private async void EditPairOptions(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncPair pair) return;
        var dialog = new PairOptionsWindow(pair.Exclusions, pair.SpeedLimitKiB) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        MainTabs.IsEnabled = false;
        try
        {
            await StopAuto(pair.Id, true);
            var updated = pair with { Exclusions = dialog.Exclusions, SpeedLimitKiB = dialog.SpeedLimitKiB, Paused = true };
            store.Save(updated);
            var index = model.Pairs.ToList().FindIndex(x => x.Id == pair.Id);
            if (index >= 0) model.Pairs[index] = updated;
            StatusText.Text = "제외·속도 설정을 저장했습니다. 자동 시작을 누르면 새 설정으로 감시합니다.";
        }
        catch (Exception ex) { StatusText.Text = "설정 저장 실패: " + ex.Message; }
        finally { MainTabs.IsEnabled = true; }
    }
    private void AddAlert(SyncPair pair, string message)
    {
        if (lastAlert.TryGetValue(pair.Id, out var previous) && previous == message) return;
        lastAlert[pair.Id] = message;
        using (SyncDiagnostics.BeginJob(pair.Id, 0)) SyncDiagnostics.Write("sync.attention", phase: "NeedsAttention");
        var alert = new SyncAlert(pair.Id, pair.RemoteFolderName, message);
        model.Alerts.Insert(0, alert);
        if (model.Alerts.Count > 200) model.Alerts.RemoveAt(model.Alerts.Count - 1);
        if (notificationQueue.Count < 200) notificationQueue.Enqueue(alert);
        ShowNextNotification();
    }
    private void ShowNextNotification()
    {
        if (closing || visibleAlert is not null || !notificationQueue.TryDequeue(out var alert)) return;
        visibleAlert = alert;
        tray.ShowBalloonTip(6000, "MYSync · 확인 필요", (alert.Name.Length > 60 ? alert.Name[..60] : alert.Name) + " · 오류가 있습니다. 클릭하여 확인하세요.", System.Windows.Forms.ToolTipIcon.Warning);
    }
    private async void ResolveAlert(object sender, RoutedEventArgs e)
    {
        if (AlertsBox.SelectedItem is not SyncAlert alert) return;
        var pair = model.Pairs.FirstOrDefault(x => x.Id == alert.PairId);
        if (pair is null) { StatusText.Text = "삭제된 연결의 과거 오류입니다."; return; }
        MainTabs.IsEnabled = false;
        try
        {
            await StopAuto(pair.Id, true);
            OpenPairWindow(new Button { DataContext = pair }, (p, provider, recovery, journal) => new ResolveWindow(p, provider, accountStore, recovery, journal, ControlFor(p.Id), ProgressFor(p)));
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { MainTabs.IsEnabled = true; }
    }
    private void AlertAccount(object sender, RoutedEventArgs e)
    {
        if (AlertsBox.SelectedItem is not SyncAlert alert) return;
        var pair = model.Pairs.FirstOrDefault(x => x.Id == alert.PairId);
        if (pair is null) { StatusText.Text = "삭제된 연결의 과거 오류입니다."; return; }
        MainTabs.SelectedIndex = 1;
        ProvidersBox.SelectedItem = model.Providers.FirstOrDefault(x => x.Id == pair.ProviderId);
        AccountsBox.SelectedItem = AccountsBox.Items.Cast<SavedAccount>().FirstOrDefault(x => x.Id == pair.AccountId);
    }
    private void SaveLogSettings(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LogLimitBox.Text, out var size) || size is < 1 or > 1024) { StatusText.Text = "로그 크기는 1~1024 MiB로 입력하세요."; return; }
        try { var next = preferences with { LogLimitMiB = size }; preferencesStore.Save(next); preferences = next; SyncDiagnostics.MaxBytes = size * 1024L * 1024; SyncDiagnostics.Write("log.settings"); StatusText.Text = "로그 용량을 저장했습니다."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void OpenLogs(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(SyncDiagnostics.DirectoryPath); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SyncDiagnostics.DirectoryPath) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void HoldTransfer(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not TransferRow row || row.Done) return;
        if (row.Applied) { StatusText.Text = "이미 반영된 파일은 최종 검증 중입니다."; return; }
        var state = (sender as Button)?.Tag as string ?? "중지";
        ControlFor(row.PairId).Hold(row.Path, state);
        row.Apply(new(SyncPhase.Idle, state, Event: TransferEvent.Held));
        StatusText.Text = "해당 파일을 " + state + "했습니다. 이미 반영된 내용은 되돌리지 않으며 재개 시 다시 검사합니다.";
    }
    private async void OpenTransferResolution(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not TransferRow row) return;
        var pair = model.Pairs.FirstOrDefault(x => x.Id == row.PairId);
        if (pair is null) { StatusText.Text = "삭제된 연결의 기록입니다."; return; }
        var resume = autoRuns.ContainsKey(pair.Id);
        MainTabs.IsEnabled = false;
        try
        {
            await StopAuto(pair.Id, true);
            OpenPairWindow(new Button { DataContext = pair }, (p, provider, recovery, journal) =>
                new ResolveWindow(p, provider, accountStore, recovery, journal, ControlFor(p.Id), ProgressFor(p), row.Path));
            if (resume) StartAuto(model.Pairs.Single(x => x.Id == pair.Id));
        }
        catch (Exception ex) { StatusText.Text = "오류 해결 화면: " + ex.Message; }
        finally { MainTabs.IsEnabled = true; }
    }
    private void SkipTransferOnce(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not TransferRow row) return;
        ControlFor(row.PairId).SkipOnce(row.Path);
        row.MarkHandled("이번 실행 제외");
        if (autoRuns.TryGetValue(row.PairId, out var run)) run.Monitor.RequestScan();
        StatusText.Text = "현재 실행 중이면 이번에, 아니면 다음 실행에서 한 번 제외합니다. 이후 다시 동기화 대상이 됩니다.";
    }
    private void ResumeTransfer(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not TransferRow row) return;
        if (row.State is "확인 필요" or "검증 보류") { OpenTransferResolution(sender, e); return; }
        ControlFor(row.PairId).Resume(row.Path);
        if (autoRuns.TryGetValue(row.PairId, out var run)) run.Monitor.RequestScan();
        StatusText.Text = "파일 보류를 해제했습니다. 자동 감시 또는 다음 수동 실행에서 재검사합니다.";
    }
    private SyncPair SaveFileExclusion(Guid id, string path, EntryKind kind)
    {
        if (path.IndexOfAny(['*', '?']) >= 0) throw new InvalidOperationException("특수 문자가 있는 경로는 연결 설정에서 제외하세요.");
        var pair = model.Pairs.Single(x => x.Id == id);
        var updated = pair with { Exclusions = pair.Exclusions + "\n/" + path + (kind == EntryKind.Directory ? "/" : ""), Paused = true };
        store.Save(updated); model.Pairs[model.Pairs.IndexOf(pair)] = updated; return updated;
    }
    private async void ExcludeTransfer(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not TransferRow row) return;
        var pair = model.Pairs.FirstOrDefault(x => x.Id == row.PairId);
        if (pair is null) return;
        if (row.Path.IndexOfAny(['*', '?']) >= 0) { StatusText.Text = "이 경로는 연결 설정에서 제외 규칙을 지정하세요."; return; }
        try
        {
            await StopAuto(pair.Id, true);
            var rule = "/" + row.Path + (row.IsFile ? "" : "/");
            var updated = pair with { Exclusions = pair.Exclusions + "\n" + rule, Paused = true };
            store.Save(updated); model.Pairs[model.Pairs.ToList().FindIndex(x => x.Id == pair.Id)] = updated;
            StatusText.Text = "제외 규칙을 저장했습니다. 자동 시작으로 나머지 파일을 동기화하세요.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private static bool Overlaps(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
