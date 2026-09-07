using System.Windows;
using System.Windows.Controls;

namespace MYSync.Desktop;

public sealed class TextPromptWindow : Window
{
    public string Value { get; private set; } = "";
    public TextPromptWindow(string title, string label, string initial)
    {
        Title = title; Width = 460; Background = System.Windows.Media.Brushes.White;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = initial, Padding = new Thickness(6) };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "취소", Padding = new Thickness(14, 7, 14, 7), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "확인", Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; }
            Value = input.Text.Trim(); DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
