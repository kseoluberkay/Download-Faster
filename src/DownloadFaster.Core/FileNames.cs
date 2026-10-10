using System.Net.Http.Headers;
using System.Text;

namespace DownloadFaster.Core;

/// <summary>Picks safe Windows file names for downloads.</summary>
public static class FileNames
{
    private const int MaxLength = 180;

    private static readonly HashSet<char> Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string? FromContentDisposition(ContentDispositionHeaderValue? header)
    {
        string? name = header?.FileNameStar ?? header?.FileName;
        name = name?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(name) ? null : Sanitize(Path.GetFileName(name.Replace('\\', '/')));
    }

    public static string? FromUrl(Uri url)
    {
        string last = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        last = Uri.UnescapeDataString(last);
        return string.IsNullOrWhiteSpace(last) ? null : Sanitize(last);
    }

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(c < 32 || Invalid.Contains(c) ? '_' : c);
        }

        string result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            return "download";
        }

        string stem = Path.GetFileNameWithoutExtension(result);
        if (Reserved.Contains(stem))
        {
            result = "_" + result;
        }

        if (result.Length > MaxLength)
        {
            string ext = Path.GetExtension(result);
            ext = ext.Length > 20 ? "" : ext;
            result = result[..(MaxLength - ext.Length)] + ext;
        }

        return result;
    }

    /// <summary>Returns <paramref name="name"/>, or "name (2).ext" etc. when <paramref name="isTaken"/> says it is used.</summary>
    public static string MakeUnique(string name, Func<string, bool> isTaken)
    {
        if (!isTaken(name))
        {
            return name;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }
}
