using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DualNet.Core.Models;

namespace DualNet.Core.Net;

/// <summary>
/// Detects active Ethernet / Wi-Fi adapters that downloads can bind to, and merges in
/// user-defined "manual network providers" whose IP the automatic scan does not offer
/// (VPN tunnels, virtual switches, multi-IP NICs, adapters without a gateway, …).
/// </summary>
public static class NetworkAdapterService
{
    /// <summary>
    /// Test seam: when set, replaces the automatic OS scan (manual providers are still merged).
    /// Must be cleared (set back to null) after each test.
    /// </summary>
    internal static Func<IReadOnlyList<NetworkAdapterInfo>>? OverrideForTests;

    public static IReadOnlyList<NetworkAdapterInfo> GetActiveAdapters(
        IEnumerable<ManualAdapterEntry>? manualAdapters = null)
    {
        var auto = OverrideForTests is not null ? OverrideForTests() : ScanAuto();
        var result = auto.ToList();
        return MergeManual(result, manualAdapters);
    }

    private static IReadOnlyList<NetworkAdapterInfo> ScanAuto()
    {
        var result = new List<NetworkAdapterInfo>();
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                var type = nic.NetworkInterfaceType;
                if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                        or NetworkInterfaceType.Ppp)
                    continue;

                var props = nic.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is null)
                    continue;

                bool hasGateway = props.GatewayAddresses
                    .Any(g => g.Address is { AddressFamily: AddressFamily.InterNetwork });
                if (!hasGateway)
                    continue;   // filters out docker0 / veth / virtual switches

                string ip = ipv4.ToString();
                if (!seenIps.Add(ip))
                    continue;

                string typeName = type switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or
                    NetworkInterfaceType.GigabitEthernet or
                    NetworkInterfaceType.FastEthernetT or
                    NetworkInterfaceType.FastEthernetFx => "Ethernet",
                    _ => "Other"
                };

                result.Add(new NetworkAdapterInfo(
                    Id: nic.Id,
                    Name: nic.Name,
                    Description: nic.Description,
                    TypeName: typeName,
                    IPv4Address: ip,
                    SpeedBitsPerSecond: nic.Speed > 0 ? (ulong)nic.Speed : 0));
            }
            catch
            {
                // A single broken adapter must never break detection of the others.
            }
        }

        return result;
    }

    /// <summary>Merges manual providers into the auto-detected list (returns a new sorted list).</summary>
    private static IReadOnlyList<NetworkAdapterInfo> MergeManual(
        List<NetworkAdapterInfo> auto, IEnumerable<ManualAdapterEntry>? manualAdapters)
    {
        var manual = manualAdapters as IList<ManualAdapterEntry> ?? manualAdapters?.ToList();
        if (manual is null || manual.Count == 0)
            return SortAdapters(auto);

        // Every IPv4 currently assigned to ANY local NIC (no gateway filter) — the
        // authority on whether a manual provider can actually be bound to right now.
        var localIps = new Dictionary<string, (string Description, ulong Speed)>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                ulong speed = nic.Speed > 0 ? (ulong)nic.Speed : 0;
                string description = nic.Description;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        localIps.TryAdd(addr.Address.ToString(), (description, speed));
            }
        }
        catch { }

        foreach (var entry in manual)
        {
            try
            {
                if (entry is null ||
                    !IPAddress.TryParse(entry.IPv4Address, out var parsed) ||
                    parsed.AddressFamily != AddressFamily.InterNetwork)
                    continue;   // garbage in settings — skip silently

                string ip = parsed.ToString();   // canonical form
                if (auto.Any(a => string.Equals(a.IPv4Address, ip, StringComparison.OrdinalIgnoreCase)))
                    continue;   // already auto-detected — manual copy is redundant

                bool online = localIps.ContainsKey(ip);
                auto.Add(new NetworkAdapterInfo(
                    Id: $"manual:{ip}",   // identity = the user-entered IP → stable across rescan
                    Name: string.IsNullOrWhiteSpace(entry.Name) ? $"Manual ({ip})" : entry.Name.Trim(),
                    Description: online
                        ? $"Manual network provider — {localIps[ip].Description}"
                        : "Manual network provider — IP is not present on this computer",
                    TypeName: string.IsNullOrWhiteSpace(entry.TypeName) ? "Other" : entry.TypeName,
                    IPv4Address: ip,
                    SpeedBitsPerSecond: online ? localIps[ip].Speed : 0,
                    IsManual: true,
                    IsOnline: online));
            }
            catch
            {
                // Never let one bad manual entry break the whole scan.
            }
        }

        return SortAdapters(auto);
    }

    private static IReadOnlyList<NetworkAdapterInfo> SortAdapters(List<NetworkAdapterInfo> adapters) =>
        adapters
            .OrderBy(a => a.TypeName switch { "Ethernet" => 0, "Wi-Fi" => 1, _ => 2 })
            .ThenBy(a => a.IsOnline ? 0 : 1)   // offline manual providers sink to the bottom
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
