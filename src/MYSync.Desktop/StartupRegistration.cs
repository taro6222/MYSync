using Microsoft.Win32;

namespace MYSync.Desktop;

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MYSync";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) is string; }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key) ?? throw new InvalidOperationException("로그인 시작 설정을 열 수 없습니다.");
        if (!enabled) { key.DeleteValue(Name, false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 위치를 찾을 수 없습니다.");
        if (!string.Equals(System.IO.Path.GetFileName(executable), "MYSync.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("배포된 MYSync.Desktop.exe에서 로그인 시 실행을 설정하세요.");
        key.SetValue(Name, $"\"{executable}\" --background", RegistryValueKind.String);
    }
}
