using System.Text.Json;

namespace MYSync.Sync.Infrastructure;

public sealed record DesktopPreferences(bool CloseToTray = true);
public sealed class DesktopPreferencesStore(string path)
{
    public DesktopPreferences Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(path)) ?? throw new InvalidDataException("앱 설정이 비어 있습니다.")
        : new();
    public void Save(DesktopPreferences value)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value); file.Flush(true); }
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
