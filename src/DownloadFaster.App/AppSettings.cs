using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadFaster.Core;

namespace DownloadFaster.App;

/// <summary>Everything the app remembers between runs, stored in %LOCALAPPDATA%\DownloadFaster.</summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string DownloadFolder { get; set; } = DefaultDownloadFolder();

    public BalanceMode Mode { get; set; } = BalanceMode.Auto;

    public int ChunkSizeMB { get; set; } = 4;

    public string SpeedTestUrl { get; set; } = SpeedTest.DefaultUrl.ToString();

    /// <summary>Per-adapter settings keyed by Windows' adapter id.</summary>
    public Dictionary<string, AdapterPrefs> Adapters { get; set; } = [];

    /// <summary>Unfinished downloads, restored as paused on the next start.</summary>
    public List<SavedDownload> Downloads { get; set; } = [];

    private static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadFaster");

    private static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults if the file is unreadable.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string DefaultDownloadFolder()
    {
        // FOLDERID_Downloads, which honours a Downloads folder the user has moved.
        var downloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(downloadsId, 0, IntPtr.Zero, out string path) == 0)
        {
            return path;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out string pszPath);
}

internal sealed class AdapterPrefs
{
    public bool Enabled { get; set; } = true;

    public int Connections { get; set; } = 4;

    /// <summary>Speed cap in MB/s, 0 for none.</summary>
    public decimal MaxMBps { get; set; }

    public int Share { get; set; } = 50;

    public static AdapterPrefs From(RouteSettings settings) => new()
    {
        Enabled = settings.Enabled,
        Connections = settings.Connections,
        MaxMBps = Math.Round((decimal)settings.SpeedLimitBytesPerSecond / DownloadOptions.MiB, 1),
        Share = settings.Share,
    };

    public RouteSettings ToSettings() => new()
    {
        Enabled = Enabled,
        Connections = Connections,
        SpeedLimitBytesPerSecond = (long)(MaxMBps * DownloadOptions.MiB),
        Share = Share,
    };
}

internal sealed class SavedDownload
{
    public string Url { get; set; } = "";

    public string Folder { get; set; } = "";

    public string? FileName { get; set; }
}
