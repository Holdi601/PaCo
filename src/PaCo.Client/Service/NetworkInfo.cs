using System.Net.NetworkInformation;
using System.Net.Sockets;
using PaCo.Client.Config;

namespace PaCo.Client.Service;

/// <summary>
/// Hardware (MAC) addresses of the PC's active, physical network adapters. The server compares them with what a LAN
/// watcher sees in its own network: a PC that is visibly on there while PaCo is silent gets reported to the parents.
/// With Windows "random hardware addresses" this is the address currently used in that network.
/// </summary>
internal static class NetworkInfo
{
    private static readonly string[] VirtualHints =
        ["virtual", "vpn", "hyper-v", "vmware", "virtualbox", "tap-", "tunnel", "loopback", "bluetooth", "wan miniport", "zerotier", "hamachi", "wireguard", "tailscale"];

    public static IReadOnlyList<string> ActiveMacs()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                            && !VirtualHints.Any(h => n.Description.Contains(h, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(h, StringComparison.OrdinalIgnoreCase))
                            && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .Select(n => n.GetPhysicalAddress().ToString())
                .Where(m => m.Length == 12)
                .Distinct()
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Netzwerkadressen nicht lesbar: {ex.Message}");
            return [];
        }
    }
}
