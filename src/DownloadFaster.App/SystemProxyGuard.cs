using System.Diagnostics;
using System.Text.Json;
using DownloadFaster.Core.Proxy;

namespace DownloadFaster.App;

/// <summary>
/// Points Windows at the all-apps proxy and puts the user's own proxy settings back afterwards,
/// even if the app crashes: the original settings are saved to a file first, and a small
/// watchdog process restores them if this process dies.
/// </summary>
internal static class SystemProxyGuard
{
    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadFaster", "proxy-backup.json");

    private static bool _watchdogStarted;

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Makes Windows send app traffic to the proxy on <paramref name="port"/>.</summary>
    public static void Enable(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var current = WindowsSystemProxy.Query();
        var backup = LoadBackup();

        // Only remember settings that are not already ours, so a restore never "restores" our proxy.
        if (backup is null || !IsOurs(current, backup))
        {
            backup = new Backup
            {
                Flags = current.Flags,
                ProxyServer = current.ProxyServer,
                Bypass = current.Bypass,
                AutoConfigUrl = current.AutoConfigUrl,
            };
        }

        var ours = SystemProxySettings.ForLocalProxy(port);
        backup.Applied = ours.ProxyServer!;
        backup.OwnerPid = Environment.ProcessId;
        SaveBackup(backup);
        WindowsSystemProxy.Apply(ours);
        StartWatchdog();
    }

    /// <summary>Restores the user's own proxy settings, if ours are still the active ones.</summary>
    public static void Disable()
    {
        if (!OperatingSystem.IsWindows() || LoadBackup() is not { } backup)
        {
            return;
        }

        if (IsOurs(WindowsSystemProxy.Query(), backup))
        {
            WindowsSystemProxy.Apply(new SystemProxySettings(backup.Flags, backup.ProxyServer, backup.Bypass, backup.AutoConfigUrl));
        }

        try
        {
            File.Delete(BackupPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Undoes settings left behind by an instance that crashed.</summary>
    public static void RecoverIfOrphaned()
    {
        if (LoadBackup() is { } backup && !IsProcessAlive(backup.OwnerPid))
        {
            Disable();
        }
    }

    /// <summary>Watchdog entry point: waits for the app to exit, then cleans up if it could not.</summary>
    public static void RunWatchdog(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Already gone.
        }

        // A newer instance may have taken over in the meantime; leave its settings alone.
        if (LoadBackup() is { } backup && backup.OwnerPid == pid)
        {
            Disable();
        }
    }

    private static void StartWatchdog()
    {
        if (_watchdogStarted || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        _watchdogStarted = true;

        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Without the watchdog, a crash is still cleaned up the next time the app starts.
        }
    }

    private static bool IsOurs(SystemProxySettings settings, Backup backup) =>
        settings.UsesProxyServer && string.Equals(settings.ProxyServer, backup.Applied, StringComparison.OrdinalIgnoreCase);

    private static bool IsProcessAlive(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Backup? LoadBackup()
    {
        try
        {
            return File.Exists(BackupPath) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveBackup(Backup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
    }

    private sealed class Backup
    {
        public int Flags { get; set; }

        public string? ProxyServer { get; set; }

        public string? Bypass { get; set; }

        public string? AutoConfigUrl { get; set; }

        /// <summary>The proxy address we set, to recognise our own settings later.</summary>
        public string Applied { get; set; } = "";

        public int OwnerPid { get; set; }
    }
}
