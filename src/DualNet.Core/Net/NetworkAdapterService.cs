using System.Net.NetworkInformation;
using System.Net.Sockets;
using DualNet.Core.Models;

namespace DualNet.Core.Net;

/// <summary>Detects active Ethernet / Wi-Fi adapters that downloads can bind to.</summary>
public static class NetworkAdapterService
{
    public static IReadOnlyList<NetworkAdapterInfo> GetActiveAdapters()
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

        // Stable, human-friendly ordering: Ethernet first, then Wi-Fi, then others.
        return result
            .OrderBy(a => a.TypeName switch { "Ethernet" => 0, "Wi-Fi" => 1, _ => 2 })
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
