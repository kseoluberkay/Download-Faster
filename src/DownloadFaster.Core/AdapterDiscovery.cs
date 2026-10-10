using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DownloadFaster.Core;

/// <summary>Finds the connected network adapters that can reach the internet.</summary>
public static class AdapterDiscovery
{
    private static readonly string[] VirtualHints =
    [
        "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "tap-", "tap adapter", "wintun", "wireguard",
        "openvpn", "vpn", "loopback", "pseudo", "docker", "wsl", "npcap", "zerotier", "tailscale",
    ];

    /// <param name="settingsFor">Returns stored settings for an adapter id, or null to use defaults.</param>
    public static IReadOnlyList<NetworkRoute> Discover(Func<NetworkRoute, RouteSettings?>? settingsFor = null)
    {
        var routes = new List<NetworkRoute>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return routes;
        }

        foreach (var nic in interfaces)
        {
            var route = TryCreateRoute(nic);
            if (route is null)
            {
                continue;
            }

            var stored = settingsFor?.Invoke(route);
            routes.Add(stored is null ? route : WithSettings(route, stored));
        }

        return routes
            .OrderBy(r => r.IsVirtual)
            .ThenBy(r => r.Kind switch { AdapterKind.Ethernet => 0, AdapterKind.WiFi => 1, AdapterKind.Cellular => 2, _ => 3 })
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Default settings for a freshly discovered adapter.</summary>
    public static RouteSettings DefaultSettingsFor(NetworkRoute route) => new()
    {
        Enabled = !route.IsVirtual && route.Kind != AdapterKind.Other,
        Connections = 4,
        Share = 50,
    };

    private static NetworkRoute? TryCreateRoute(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up
            || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return null;
        }

        IPInterfaceProperties props;
        try
        {
            props = nic.GetIPProperties();
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        // Only adapters with a default gateway can reach the internet.
        bool hasGateway = props.GatewayAddresses.Any(g =>
            !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
        if (!hasGateway)
        {
            return null;
        }

        var addresses = props.UnicastAddresses
            .Select(u => u.Address)
            .Where(IsUsableAddress)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();
        if (addresses.Count == 0)
        {
            return null;
        }

        long speed;
        try
        {
            speed = Math.Max(0, nic.Speed);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            speed = 0;
        }

        string description = nic.Description ?? "";
        var route = new NetworkRoute(nic.Id, nic.Name, Classify(nic), addresses)
        {
            Description = description,
            LinkSpeedBitsPerSecond = speed,
            IsVirtual = LooksVirtual(nic.Name + " " + description),
        };
        return WithSettings(route, DefaultSettingsFor(route));
    }

    private static NetworkRoute WithSettings(NetworkRoute route, RouteSettings settings) =>
        new(route.Id, route.Name, route.Kind, route.LocalAddresses, settings)
        {
            Description = route.Description,
            LinkSpeedBitsPerSecond = route.LinkSpeedBitsPerSecond,
            IsVirtual = route.IsVirtual,
        };

    internal static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Skip APIPA (169.254.x.x): it means DHCP failed and there is no internet.
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 169 && b[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !address.IsIPv6LinkLocal
            && !address.IsIPv6SiteLocal
            && !address.IsIPv6Multicast
            && !address.IsIPv6Teredo;
    }

    private static AdapterKind Classify(NetworkInterface nic) => nic.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 or NetworkInterfaceType.Ppp => AdapterKind.Cellular,
        _ => AdapterKind.Other,
    };

    private static bool LooksVirtual(string text)
    {
        string lower = text.ToLowerInvariant();
        return VirtualHints.Any(lower.Contains);
    }
}
