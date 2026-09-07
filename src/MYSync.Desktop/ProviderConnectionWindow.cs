using System.Windows;
using System.Windows.Controls;
using MYSync.Provider.Abstractions;
namespace MYSync.Desktop;

public sealed class ProviderConnectionWindow : Window
{
    public IReadOnlyDictionary<string, string> Values { get; private set; } = new Dictionary<string, string>();
    public ProviderConnectionWindow(IReadOnlyList<ConnectionField> fields)
    {
        Title = "클라우드 연결"; Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        var inputs = new Dictionary<string, Control>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 10, 0, 5) });
            Control input = field.IsSecret ? new PasswordBox() : new TextBox();
            input.Padding = new Thickness(6); inputs.Add(field.Key, input); panel.Children.Add(input);
        }
        panel.Children.Add(new TextBlock { Text = "이번 실행 중에만 연결합니다. 인증 정보는 디스크에 저장하지 않습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) });
        var button = new Button { Content = "연결", Padding = new Thickness(14, 8, 14, 8), IsDefault = true };
        button.Click += (_, _) =>
        {
            Values = inputs.ToDictionary(x => x.Key, x => x.Value is PasswordBox password ? password.Password : ((TextBox)x.Value).Text);
            foreach (var input in inputs.Values.OfType<PasswordBox>()) input.Clear();
            DialogResult = true;
        };
        panel.Children.Add(button); Content = panel;
    }
}
