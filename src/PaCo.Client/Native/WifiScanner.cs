using System.Runtime.InteropServices;
using System.Text;
using PaCo.Shared;

namespace PaCo.Client.Native;

/// <summary>
/// Lists the Wi-Fi access points around the PC (Native Wifi API). Used to locate the device via a Wi-Fi map when the
/// Windows location service has no precise fix. Only BSSID, SSID, signal and frequency are collected.
/// </summary>
internal static class WifiScanner
{
    private const int BssEntrySize = 360; // sizeof(WLAN_BSS_ENTRY)

    public static List<WifiNetwork> Scan(int max = 30)
    {
        var result = new List<WifiNetwork>();
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return result;
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return result;
            var interfaces = new List<Guid>();
            try
            {
                var count = Marshal.ReadInt32(list);
                for (var i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] (512) + state (4) = 532 bytes, list header 8 bytes.
                    var guidBytes = new byte[16];
                    Marshal.Copy(list + 8 + i * 532, guidBytes, 0, 16);
                    interfaces.Add(new Guid(guidBytes));
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }

            foreach (var g in interfaces)
            {
                var iface = g;
                WlanScan(handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            Thread.Sleep(4000); // scans complete asynchronously

            foreach (var g in interfaces)
            {
                var iface = g;
                if (WlanGetNetworkBssList(handle, ref iface, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out var bss) != 0) continue;
                try
                {
                    var n = Marshal.ReadInt32(bss + 4);
                    for (var i = 0; i < n; i++)
                    {
                        var e = bss + 8 + i * BssEntrySize;
                        var ssidLen = Math.Clamp(Marshal.ReadInt32(e), 0, 32);
                        var ssidBytes = new byte[ssidLen];
                        Marshal.Copy(e + 4, ssidBytes, 0, ssidLen);
                        var mac = new byte[6];
                        Marshal.Copy(e + 40, mac, 0, 6);
                        result.Add(new WifiNetwork
                        {
                            Bssid = string.Join(":", mac.Select(b => b.ToString("x2"))),
                            Ssid = Encoding.UTF8.GetString(ssidBytes),
                            Rssi = Marshal.ReadInt32(e + 56),
                            FrequencyMhz = Marshal.ReadInt32(e + 92) / 1000, // kHz → MHz
                        });
                    }
                }
                finally
                {
                    WlanFreeMemory(bss);
                }
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
        return result.DistinctBy(w => w.Bssid).OrderByDescending(w => w.Rssi).Take(max).ToList();
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool securityEnabled, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
