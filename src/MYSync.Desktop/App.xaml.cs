namespace MYSync.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private EventWaitHandle? showRequest;
    private RegisteredWaitHandle? listener;
    private bool ownsInstance;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        instance = new Mutex(false, "Local\\MYSync-Desktop");
        try { ownsInstance = instance.WaitOne(0); } catch (AbandonedMutexException) { ownsInstance = true; }
        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MYSync-ShowWindow");
        if (!ownsInstance) { showRequest.Set(); Shutdown(); return; }
        listener = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow is MainWindow main) main.ShowFromTray();
        })), null, Timeout.Infinite, false);
        base.OnStartup(e);
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        listener?.Unregister(null); showRequest?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose(); base.OnExit(e);
    }
}
