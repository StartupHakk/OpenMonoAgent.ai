using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Local IPv4 discovery for advertising the LAN inference endpoint.
/// Pure managed queries; no sockets are opened.
/// </summary>
public static class LanNetwork
{
    /// <summary>
    /// Returns distinct non-loopback IPv4 addresses of up interfaces, sorted.
    /// Best effort: an empty list means "unknown", never an error.
    /// </summary>
    public static IReadOnlyList<string> GetLanIPv4Addresses()
    {
        var addresses = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    addresses.Add(unicast.Address.ToString());
                }
            }
        }
        catch
        {
            // Discovery must never break startup or settings pages.
        }

        return addresses.ToList();
    }
}
