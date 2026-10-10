using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DownloadFaster.Core.Proxy;

/// <summary>The current user's Windows proxy settings (Settings &gt; Network &amp; internet &gt; Proxy).</summary>
public sealed record SystemProxySettings(int Flags, string? ProxyServer, string? Bypass, string? AutoConfigUrl)
{
    public const int Direct = 1;
    public const int UseProxyServer = 2;
    public const int UseAutoConfigUrl = 4;
    public const int AutoDetect = 8;

    /// <summary>Addresses that must never go through the proxy: this PC and the local network.</summary>
    public static readonly string LocalBypass = string.Join(';',
        new[] { "localhost", "127.*", "10.*", "192.168.*", "169.254.*", "*.local" }
            .Concat(Enumerable.Range(16, 16).Select(n => $"172.{n}.*"))
            .Append("<local>"));

    public bool UsesProxyServer => (Flags & UseProxyServer) != 0;

    public static SystemProxySettings ForLocalProxy(int port) =>
        new(Direct | UseProxyServer, $"127.0.0.1:{port}", LocalBypass, null);
}

/// <summary>
/// Reads and changes the Windows proxy settings through WinINET, the same way the Settings app
/// does, and tells running apps about the change.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsSystemProxy
{
    private const int InternetOptionPerConnectionOption = 75;
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private const int PerConnFlags = 1;
    private const int PerConnProxyServer = 2;
    private const int PerConnProxyBypass = 3;
    private const int PerConnAutoConfigUrl = 4;

    /// <summary>Windows 7+ name for the flags that also reports "automatically detect settings".</summary>
    private const int PerConnFlagsUi = 10;

    public static SystemProxySettings Query()
    {
        try
        {
            return QueryWith(PerConnFlagsUi);
        }
        catch (Win32Exception)
        {
            return QueryWith(PerConnFlags);
        }
    }

    public static void Apply(SystemProxySettings settings)
    {
        try
        {
            // Set both the classic flags and the Windows 7+ ones that the Settings app shows.
            ApplyWith([PerConnFlags, PerConnFlagsUi], settings);
        }
        catch (Win32Exception)
        {
            ApplyWith([PerConnFlags], settings);
        }

        // Tell WinINET and the apps listening to it (browsers, Steam) to pick up the change.
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    private static SystemProxySettings QueryWith(int flagsOption)
    {
        int[] ids = [flagsOption, PerConnProxyServer, PerConnProxyBypass, PerConnAutoConfigUrl];
        var options = ids.Select(id => new PerConnOption { Option = id }).ToArray();
        using var list = new NativeOptionList(options);
        int size = list.Size;
        if (!InternetQueryOption(IntPtr.Zero, InternetOptionPerConnectionOption, list.Pointer, ref size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var result = list.ReadBack();
        return new SystemProxySettings(
            result[0].Value.Number,
            TakeString(result[1].Value.Pointer),
            TakeString(result[2].Value.Pointer),
            TakeString(result[3].Value.Pointer));
    }

    private static void ApplyWith(int[] flagsOptions, SystemProxySettings settings)
    {
        var strings = new List<IntPtr>();
        IntPtr Text(string? value)
        {
            // An empty string clears the field; a null pointer would leave the old value behind.
            IntPtr p = Marshal.StringToHGlobalUni(value ?? "");
            strings.Add(p);
            return p;
        }

        try
        {
            PerConnOption[] options =
            [
                .. flagsOptions.Select(id => new PerConnOption { Option = id, Value = new OptionValue { Number = settings.Flags } }),
                new() { Option = PerConnProxyServer, Value = new OptionValue { Pointer = Text(settings.ProxyServer) } },
                new() { Option = PerConnProxyBypass, Value = new OptionValue { Pointer = Text(settings.Bypass) } },
                new() { Option = PerConnAutoConfigUrl, Value = new OptionValue { Pointer = Text(settings.AutoConfigUrl) } },
            ];
            using var list = new NativeOptionList(options);
            if (!InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, list.Pointer, list.Size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            strings.ForEach(Marshal.FreeHGlobal);
        }
    }

    /// <summary>Copies a string WinINET allocated for us and frees it.</summary>
    private static string? TakeString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        string? value = Marshal.PtrToStringUni(pointer);
        GlobalFree(pointer);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetQueryOption(IntPtr hInternet, int option, IntPtr buffer, ref int bufferLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>INTERNET_PER_CONN_OPTION_LIST plus its option array, in unmanaged memory.</summary>
    private sealed class NativeOptionList : IDisposable
    {
        private static readonly int OptionSize = Marshal.SizeOf<PerConnOption>();
        private readonly IntPtr _options;
        private readonly int _count;

        public NativeOptionList(PerConnOption[] options)
        {
            _count = options.Length;
            _options = Marshal.AllocHGlobal(OptionSize * _count);
            for (int i = 0; i < _count; i++)
            {
                Marshal.StructureToPtr(options[i], _options + (i * OptionSize), false);
            }

            Size = Marshal.SizeOf<PerConnOptionList>();
            Pointer = Marshal.AllocHGlobal(Size);
            var list = new PerConnOptionList
            {
                Size = Size,
                Connection = IntPtr.Zero, // The LAN / default connection.
                OptionCount = _count,
                OptionError = 0,
                Options = _options,
            };
            Marshal.StructureToPtr(list, Pointer, false);
        }

        public IntPtr Pointer { get; }

        public int Size { get; }

        public PerConnOption[] ReadBack() =>
            Enumerable.Range(0, _count).Select(i => Marshal.PtrToStructure<PerConnOption>(_options + (i * OptionSize))).ToArray();

        public void Dispose()
        {
            Marshal.FreeHGlobal(_options);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerConnOptionList
    {
        public int Size;
        public IntPtr Connection;
        public int OptionCount;
        public int OptionError;
        public IntPtr Options;
    }

    // Matches the native layout on 64-bit Windows (x64 and ARM64), which is all this app ships for.
    [StructLayout(LayoutKind.Sequential)]
    private struct PerConnOption
    {
        public int Option;
        public OptionValue Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct OptionValue
    {
        [FieldOffset(0)]
        public int Number;

        [FieldOffset(0)]
        public IntPtr Pointer;
    }
}
