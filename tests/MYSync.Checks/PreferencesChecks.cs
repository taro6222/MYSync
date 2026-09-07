using MYSync.Sync.Infrastructure;

internal static class PreferencesChecks
{
    public static void Run(string scratch)
    {
        var path = Path.Combine(scratch, "desktop", "preferences.json");
        var store = new DesktopPreferencesStore(path);
        if (!store.Load().CloseToTray) throw new Exception("Default tray setting missing");
        store.Save(new(false));
        if (new DesktopPreferencesStore(path).Load().CloseToTray) throw new Exception("Close setting did not survive restart");
        store.Save(new(true));
        if (!new DesktopPreferencesStore(path).Load().CloseToTray || Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length != 0) throw new Exception("Preference replacement failed");
        Console.WriteLine("PASS: tray preference default, restart persistence and complete file replacement");
    }
}
