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
}
public partial class MainWindow : Window
{
    private readonly PluginCatalog catalog = new();
    private readonly SettingsStore store;
    private readonly MainViewModel model = new();
    public MainWindow()
    {
        InitializeComponent();
        store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "settings.db"));
        catalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        foreach (var p in catalog.Providers) model.Providers.Add(p);
        foreach (var p in store.Load()) model.Pairs.Add(p);
        DataContext = model;
        if (catalog.Issues.Count > 0) StatusText.Text = string.Join(" / ", catalog.Issues.Select(x => x.Message));
        else if (model.Providers.Count == 0) StatusText.Text = "로드된 Provider가 없습니다. plugins 폴더에 설치하고 재시작하세요.";
        Closed += (_, _) => catalog.Dispose();
    }
    private void ChooseLocal(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true) LocalPathBox.Text = dialog.FolderName;
    }
    private async void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoteBox.ItemsSource = null;
        if (ProvidersBox.SelectedItem is not IProvider p) return;
        try
        {
            var folders = await p.GetFoldersAsync(null, CancellationToken.None);
            if (ReferenceEquals(ProvidersBox.SelectedItem, p)) RemoteBox.ItemsSource = folders;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void SavePair(object sender, RoutedEventArgs e)
    {
        if (ProvidersBox.SelectedItem is not IProvider p || RemoteBox.SelectedItem is not RemoteFolder folder || !Directory.Exists(LocalPathBox.Text))
        { StatusText.Text = "Provider와 로컬·원격 폴더를 선택하세요."; return; }
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalPathBox.Text));
        if (model.Pairs.Any(x => Overlaps(x.LocalPath, path))) { StatusText.Text = "기존 동기화 폴더와 중복되거나 겹칩니다."; return; }
        try
        {
            var pair = new SyncPair(Guid.NewGuid(), p.Id, path, folder.Id, folder.Name, true);
            store.Save(pair); model.Pairs.Add(pair);
            StatusText.Text = "저장했습니다. 앱 재시작 후 복원됩니다. 실제 전송은 아직 지원하지 않습니다.";
        }
        catch (Exception ex) { StatusText.Text = "저장 실패: " + ex.Message; }
    }
    private static bool Overlaps(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
