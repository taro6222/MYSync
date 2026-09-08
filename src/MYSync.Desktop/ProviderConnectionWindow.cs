using System.Windows;
using System.Windows.Controls;
using MYSync.Provider.Abstractions;
namespace MYSync.Desktop;

public sealed class ProviderConnectionWindow : Window
{
    public IReadOnlyDictionary<string, string> Values { get; private set; } = new Dictionary<string, string>();
    public ProviderConnectionWindow(IReadOnlyList<ConnectionField> fields, string? instructions = null)
    {
        Title = "클라우드 계정 연결"; Width = 520; Background = System.Windows.Media.Brushes.White; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "클라우드 계정 연결", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = instructions ?? "주소와 포트를 입력하고 계정으로 로그인하세요.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var inputs = new Dictionary<string, Control>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 10, 0, 5) });
            Control input = field.IsSecret ? new PasswordBox() : new TextBox { Text = field.DefaultValue };
            input.Padding = new Thickness(6); inputs.Add(field.Key, input); panel.Children.Add(input);
        }
        panel.Children.Add(new TextBlock { Text = "연결에 성공하면 이 Windows 사용자만 읽을 수 있도록 인증 정보를 암호화해 저장합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) });
        var button = new Button { Content = "계정 연결", Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 41, 58)), Foreground = System.Windows.Media.Brushes.White, Padding = new Thickness(14, 8, 14, 8), IsDefault = true };
        button.Click += (_, _) =>
        {
            Values = inputs.ToDictionary(x => x.Key, x => x.Value is PasswordBox password ? password.Password : ((TextBox)x.Value).Text);
            foreach (var input in inputs.Values.OfType<PasswordBox>()) input.Clear();
            DialogResult = true;
        };
        panel.Children.Add(button); Content = panel;
    }
}

