namespace DownloadFaster.App;

internal static class Program
{
    private const string InstanceName = @"Local\DownloadFaster.Instance";
    private const string ShowEventName = @"Local\DownloadFaster.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        // Watchdog mode: no window, just restore the proxy settings if the app dies.
        if (args is ["--watchdog", var pidText] && int.TryParse(pidText, out int pid))
        {
            SystemProxyGuard.RunWatchdog(pid);
            return;
        }

        // One instance only; starting it again brings the running one to the front.
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out bool isFirst);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            showEvent.Set();
            return;
        }

        SystemProxyGuard.RecoverIfOrphaned();

        ApplicationConfiguration.Initialize();
        using var form = new MainForm(startHidden: args.Contains("--minimized"));
        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (form.IsDisposed)
                {
                    return;
                }

                try
                {
                    form.BeginInvoke(new Action(form.ShowFromTray));
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "Show listener" };
        listener.Start();

        Application.Run(form);
    }
}
