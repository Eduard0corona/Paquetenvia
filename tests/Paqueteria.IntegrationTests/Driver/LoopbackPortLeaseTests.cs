using System.Net;
using System.Net.Sockets;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// <see cref="LoopbackPortLease"/> on the kernel it relies on. Each probe socket is confined to the one port under test
/// (<c>IP_LOCAL_PORT_RANGE</c>, Linux 6.3 and later), so "the kernel never gives this port to a socket that asks for
/// any port" is checked exactly instead of by sampling. The first test is the control: the reservation the harness
/// used before (listen on port 0, close, bind later) loses its port to an outgoing connection and the server then fails
/// with <c>EADDRINUSE</c>, as in #307.
/// </summary>
public sealed class LoopbackPortLeaseTests
{
    // <netinet/in.h> on Linux: the range of local ports the kernel may give this socket, low in the lower 16 bits.
    private const int IpProtocolLevel = 0;
    private const int IpLocalPortRange = 51;

    [Fact]
    public void A_port_reserved_by_closing_a_listener_is_taken_by_an_outgoing_connection_and_the_server_cannot_bind_it()
    {
        if (!LoopbackPortLease.HeldAcrossServerBind)
        {
            return;
        }
        using var peer = StartListener(0);
        int port;
        using (var reservation = StartListener(0))
        {
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
        }

        using var outgoing = ConfinedTo(port);
        outgoing.Connect(peer.LocalEndpoint);
        Assert.Equal(port, ((IPEndPoint)outgoing.LocalEndPoint!).Port);

        var server = new TcpListener(IPAddress.Loopback, port);
        var failure = Assert.Throws<SocketException>(() => server.Start());
        Assert.Equal(SocketError.AddressAlreadyInUse, failure.SocketErrorCode);
        server.Stop();
    }

    [Fact]
    public void While_leased_the_port_is_never_given_to_a_connection_or_to_a_bind_to_any_port()
    {
        if (!LoopbackPortLease.HeldAcrossServerBind)
        {
            return;
        }
        using var peer = StartListener(0);
        using var lease = LoopbackPortLease.Acquire();

        AssertNotGivenAway(lease.Port, peer.LocalEndpoint);
    }

    [Fact]
    public async Task The_server_binds_next_to_the_lease_gets_every_connection_and_restarts_without_losing_the_port()
    {
        if (!LoopbackPortLease.HeldAcrossServerBind)
        {
            return;
        }
        using var peer = StartListener(0);
        using var lease = LoopbackPortLease.Acquire();

        for (var start = 0; start < 2; start++)
        {
            lease.BeforeServerBinds();
            Assert.True(lease.IsHeld);
            // TcpListener binds as Kestrel's socket transport does (Socket.Bind, which sets SO_REUSEADDR on Linux);
            // Node.js (libuv) sets it as well, which the Next.js suites exercise.
            var server = StartListener(lease.Port);
            try
            {
                for (var connection = 0; connection < 3; connection++)
                {
                    using var accepted = await ExchangeAsync(server, lease.Port);
                }
                AssertNotGivenAway(lease.Port, peer.LocalEndpoint);
            }
            finally
            {
                server.Stop();
            }

            // Between one server and the next the lease alone keeps the port: the clients closed first, so no
            // TIME_WAIT socket of the server's side holds it.
            AssertNotGivenAway(lease.Port, peer.LocalEndpoint);
        }
    }

    [Fact]
    public void Disposing_the_lease_gives_the_port_back()
    {
        var lease = LoopbackPortLease.Acquire();
        var port = lease.Port;
        lease.Dispose();
        lease.Dispose();
        Assert.False(lease.IsHeld);

        if (!LoopbackPortLease.HeldAcrossServerBind)
        {
            return;
        }
        using var probe = ConfinedTo(port);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.Equal(port, ((IPEndPoint)probe.LocalEndPoint!).Port);
    }

    [Fact]
    public void The_lease_ends_before_the_server_binds_only_where_a_listener_cannot_bind_next_to_it()
    {
        using var lease = LoopbackPortLease.Acquire();
        Assert.True(lease.IsHeld);

        lease.BeforeServerBinds();

        Assert.Equal(OperatingSystem.IsLinux(), LoopbackPortLease.HeldAcrossServerBind);
        Assert.Equal(LoopbackPortLease.HeldAcrossServerBind, lease.IsHeld);
    }

    private static void AssertNotGivenAway(int port, EndPoint peer)
    {
        using (var outgoing = ConfinedTo(port))
        {
            var failure = Assert.Throws<SocketException>(() => outgoing.Connect(peer));
            Assert.Equal(SocketError.AddressNotAvailable, failure.SocketErrorCode);
        }
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.Any })
        {
            using var bound = ConfinedTo(port);
            var failure = Assert.Throws<SocketException>(() => bound.Bind(new IPEndPoint(address, 0)));
            Assert.Equal(SocketError.AddressAlreadyInUse, failure.SocketErrorCode);
        }
    }

    /// <summary>One byte from a client to <paramref name="server"/>; the client closes first and the server sees it.</summary>
    private static async Task<TcpClient> ExchangeAsync(TcpListener server, int port)
    {
        var timeout = TimeSpan.FromSeconds(5);
        TcpClient? accepted = null;
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(timeout);
                accepted = await server.AcceptTcpClientAsync().WaitAsync(timeout);
                await client.GetStream().WriteAsync(new byte[] { 42 });
            }
            var received = new byte[2];
            Assert.Equal(1, await accepted.GetStream().ReadAsync(received.AsMemory(0, 1)).AsTask().WaitAsync(timeout));
            Assert.Equal(42, received[0]);
            Assert.Equal(0, await accepted.GetStream().ReadAsync(received.AsMemory(1, 1)).AsTask().WaitAsync(timeout));
            return accepted;
        }
        catch
        {
            accepted?.Dispose();
            throw;
        }
    }

    private static TcpListener StartListener(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    private static Socket ConfinedTo(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetRawSocketOption(
                IpProtocolLevel,
                IpLocalPortRange,
                BitConverter.GetBytes((uint)port | ((uint)port << 16)));
            return socket;
        }
        catch (SocketException exception)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                "This kernel cannot confine a socket to one local port (IP_LOCAL_PORT_RANGE needs Linux 6.3 or later).",
                exception);
        }
    }
}
