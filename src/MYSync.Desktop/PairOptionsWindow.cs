using System.Windows;
using System.Windows.Controls;
using MYSync.Sync.Core;

namespace MYSync.Desktop;

public sealed class PairOptionsWindow : Window
{
    public string Exclusions { get; private set; }
    public long SpeedLimitKiB { get; private set; }
    public PairOptionsWindow(string exclusions, long speed)
    {
        Exclusions = exclusions; SpeedLimitKiB = speed;
        Title = "동기화별 제외·속도 설정"; Width = 560; Height = 520;
        MinWidth = 480; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "제외할 파일·폴더", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "한 줄에 규칙 하나: *.bak / desktop.ini / cache/ / logs/debug.log\n파일명은 모든 하위 폴더에, 경로는 동기화 루트 기준으로 적용됩니다.\n제외 항목은 양쪽 모두 보존하며, 다시 포함하면 새로 비교합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,10) });
        var rules = new TextBox { Text = exclusions, AcceptsReturn = true, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(rules);
        panel.Children.Add(new TextBlock { Text = "업로드·다운로드 합산 한도 (KiB/s) · 0 = 무제한", Margin = new Thickness(0,18,0,8) });
        var limit = new TextBox { Text = speed.ToString() }; panel.Children.Add(limit);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,0) }; panel.Children.Add(status);
        var save = new Button { Content = "설정 적용", Padding = new Thickness(18,8,18,8), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0), IsDefault = true };
        save.Click += (_, _) =>
        {
            try
            {
                if (!long.TryParse(limit.Text, out var value) || value < 0 || value > 1000000) throw new ArgumentException("속도는 0~1,000,000 사이 정수로 입력하세요.");
                _ = new SyncExclusions(rules.Text);
                Exclusions = rules.Text; SpeedLimitKiB = value; DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
