using System.Net;
using System.Net.Sockets;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// A loopback TCP port that the harness knows before the server under test listens on it (an origin the API's CORS,
/// the storage CORS or the web build must name first), kept out of every other socket's reach until the lease is
/// disposed.
/// </summary>
/// <remarks>
/// <para>
/// Reserving a port by listening on port 0 and closing the listener left it free until the server bound it: for as long
/// as a Next.js build, the containers or a host start took (seconds to minutes), and between the API restarts of a
/// suite. Any socket the kernel gave a port to in between (an outgoing connection, another listener on port 0) could
/// take it, and the server then failed with <c>EADDRINUSE</c>: next start in #307, the OPS-005 API in run
/// 39443348695.
/// </para>
/// <para>
/// On Linux the lease is a socket bound to <c>127.0.0.1:Port</c> with <c>SO_REUSEADDR</c> (and not
/// <c>SO_REUSEPORT</c>) that never listens. The kernel then never hands the port to a socket that asks for any port:
/// a <c>bind</c> to port 0 skips it (<c>EADDRINUSE</c> for that one port) and so does the local port of a
/// <c>connect</c> (<c>EADDRNOTAVAIL</c>). A socket that binds exactly this port with <c>SO_REUSEADDR</c> and listens is
/// accepted next to the lease: that is what the servers under test do, since Node.js (libuv) and .NET set
/// <c>SO_REUSEADDR</c> on every TCP bind on Linux. The lease receives nothing (only a listening socket is given
/// connections), so the server's traffic is unchanged; it is held while the server runs and across its restarts, and
/// the port only goes back to the kernel when the lease is disposed. <c>LoopbackPortLeaseTests</c> proves each of these
/// points with a socket confined to this one port (<c>IP_LOCAL_PORT_RANGE</c>).
/// </para>
/// <para>
/// The one allocator the lease does not stop is Docker's: it hands out host ports in sequence without asking the kernel
/// and binds them with <c>SO_REUSEADDR</c>, so a container published while a lease is held can land on its port. Lease
/// after the containers of a fixture have started (<c>PlatformEndToEndFixture</c> does).
/// </para>
/// <para>
/// Windows and macOS do not let a listener bind next to another bound socket, so there the lease lets go of the port
/// immediately before the server binds (<see cref="BeforeServerBinds"/>): the window shrinks to the server's own start,
/// but it is not closed. CI runs on Linux.
/// </para>
/// </remarks>
internal sealed class LoopbackPortLease : IDisposable
{
    // <sys/socket.h> on Linux.
    private const int SolSocket = 1;
    private const int SoReuseAddress = 2;

    private readonly object _gate = new();
    private Socket? _socket;

    private LoopbackPortLease(Socket socket, int port)
    {
        _socket = socket;
        Port = port;
    }

    internal int Port { get; }

    /// <summary>Whether the lease keeps the port while the server under test binds and listens on it.</summary>
    internal static bool HeldAcrossServerBind => OperatingSystem.IsLinux();

    /// <summary>Whether the lease still keeps the port.</summary>
    internal bool IsHeld
    {
        get
        {
            lock (_gate) return _socket is not null;
        }
    }

    internal static LoopbackPortLease Acquire()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (HeldAcrossServerBind)
            {
                // Exactly SO_REUSEADDR: SocketOptionName.ReuseAddress would add SO_REUSEPORT on Linux, which lets
                // another listener share the port.
                socket.SetRawSocketOption(SolSocket, SoReuseAddress, BitConverter.GetBytes(1));
            }
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return new LoopbackPortLease(socket, ((IPEndPoint)socket.LocalEndPoint!).Port);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Called immediately before the server under test binds <see cref="Port"/>. On Linux nothing changes; elsewhere
    /// the lease ends here (<see cref="HeldAcrossServerBind"/>).
    /// </summary>
    internal void BeforeServerBinds()
    {
        if (!HeldAcrossServerBind)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        Socket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
        }
        socket?.Dispose();
    }
}
