using Microsoft.Win32;

namespace DownloadFaster.App;

/// <summary>Adds or removes the app from "start when I sign in to Windows".</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DownloadFaster";

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
