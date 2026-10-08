using System.Net;
using System.Net.Sockets;

namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Port allocation with conflict tolerance. Probes 127.0.0.1:7474 first; when
/// busy and not our server, falls back to 8081, 8082, and so on, mirroring the
/// LLAMA_PORT fallback pattern in scripts/install.sh and the openmono launcher.
/// </summary>
public static class PortAllocator
{
    public static readonly int[] FallbackPorts = [8081, 8082, 8083, 8084, 8085, 9080];

    public static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pure selection helper used by unit tests: given a probe function,
    /// return the preferred port or the first free fallback.
    /// </summary>
    public static int SelectPort(int preferred, Func<int, bool> isFree)
    {
        if (isFree(preferred))
        {
            return preferred;
        }

        foreach (var candidate in FallbackPorts)
        {
            if (isFree(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No free inference port is available.");
    }

    public static int Allocate(int preferred) => SelectPort(preferred, IsPortFree);
}
