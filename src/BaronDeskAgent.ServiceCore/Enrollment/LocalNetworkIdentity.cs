using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BaronDeskAgent.ServiceCore.Enrollment;

/// <summary>
/// The MAC and IP the station uses to reach the server, reported at enrollment (informational: MAC is not authentication).
/// </summary>
internal static class LocalNetworkIdentity
{
    public static async Task<(string Mac, string Ip)> ResolveAsync(Uri server, CancellationToken cancellationToken)
    {
        var localAddress = await GetRouteSourceAddressAsync(server, cancellationToken);
        var networkInterface = FindInterface(localAddress) ?? FindPrimaryInterface();

        var ip = localAddress is { } address && !IPAddress.IsLoopback(address)
            ? address
            : networkInterface?.GetIPProperties().UnicastAddresses
                .Select(unicast => unicast.Address)
                .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork) ?? localAddress;

        var mac = networkInterface is null
            ? string.Empty
            : string.Join(':', networkInterface.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));

        return (mac, ip?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// The local address the OS would use to reach <paramref name="server"/>. Connecting a UDP socket sends no packet;
    /// it only runs route selection.
    /// </summary>
    private static async Task<IPAddress?> GetRouteSourceAddressAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server.IdnHost, cancellationToken);
            var remote = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (remote is null)
            {
                return null;
            }

            using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, server.Port);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static NetworkInterface? FindInterface(IPAddress? address) =>
        address is null
            ? null
            : UsableInterfaces().FirstOrDefault(nic =>
                nic.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(address)));

    /// <summary>Fallback (e.g. a server on loopback during development): the first physical interface with a gateway.</summary>
    private static NetworkInterface? FindPrimaryInterface() =>
        UsableInterfaces()
            .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses.Count > 0)
            .FirstOrDefault();

    private static IEnumerable<NetworkInterface> UsableInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(nic =>
            nic.OperationalStatus == OperationalStatus.Up &&
            nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
            nic.GetPhysicalAddress().GetAddressBytes().Length == 6);
}
