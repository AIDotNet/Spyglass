using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Spyglass.App.Core.Lan;

public sealed record LanAddress(string Interface, string Address);

public static class LanAddresses
{
    /// <summary>Non-loopback, non-link-local IPv4 addresses of interfaces that are up: what a phone or emulator would connect to.</summary>
    public static IReadOnlyList<LanAddress> Get()
    {
        var list = new List<LanAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = u.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a)) continue;
                    var b = a.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    list.Add(new LanAddress(nic.Name, a.ToString()));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }
}
