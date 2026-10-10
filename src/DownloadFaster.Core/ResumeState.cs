using System.Text.Json;

namespace DownloadFaster.Core;

/// <summary>
/// Sidecar file next to a partial download that lists the byte ranges still missing,
/// so a paused, failed or interrupted download can continue where it stopped.
/// </summary>
internal sealed class ResumeState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int Version { get; set; } = 1;

    public string Url { get; set; } = "";

    public long TotalBytes { get; set; }

    public string? ETag { get; set; }

    /// <summary>Missing byte ranges as [start, end) pairs.</summary>
    public List<long[]> Remaining { get; set; } = [];

    public static ResumeState? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(path), JsonOptions);
            return state is { Version: 1 } && state.Remaining.All(r => r.Length == 2) ? state : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns the missing ranges if they are consistent with a file of <paramref name="totalBytes"/>.</summary>
    public List<(long Start, long End)>? ValidRanges(long totalBytes)
    {
        var ranges = Remaining.Select(r => (Start: r[0], End: r[1])).OrderBy(r => r.Start).ToList();
        long previousEnd = 0;
        foreach (var (start, end) in ranges)
        {
            if (start < previousEnd || end <= start || end > totalBytes)
            {
                return null;
            }

            previousEnd = end;
        }

        return ranges;
    }
}
