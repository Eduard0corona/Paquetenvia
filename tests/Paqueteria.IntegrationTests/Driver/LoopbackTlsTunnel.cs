using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// Terminates TLS on a loopback port with a certificate created in memory for
/// the test process and forwards the plaintext byte stream, unchanged, to a
/// loopback HTTP listener. Object storage containers expose plain HTTP, while
/// the deployable driver artifact only accepts HTTPS signed upload URLs; the
/// tunnel lets the API presign for this HTTPS origin and, because requests are
/// relayed byte for byte, the signed <c>Host</c> header reaches storage intact.
/// </summary>
internal sealed class LoopbackTlsTunnel : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _upstream;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    private LoopbackTlsTunnel(
        TcpListener listener,
        IPEndPoint upstream,
        X509Certificate2 certificate)
    {
        _listener = listener;
        _upstream = upstream;
        _certificate = certificate;
        Origin = new Uri(
            $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            UriKind.Absolute);
        _acceptLoop = AcceptAsync();
    }

    internal Uri Origin { get; }

    internal static LoopbackTlsTunnel Start(Uri upstream, X509Certificate2 certificate)
    {
        if (upstream.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(upstream.Host, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "The tunnel only relays to a loopback HTTP listener.",
                nameof(upstream));
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackTlsTunnel(
            listener,
            new IPEndPoint(address, upstream.Port),
            certificate);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _acceptLoop.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task AcceptAsync()
    {
        var connections = new List<Task>();
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                connections.RemoveAll(connection => connection.IsCompleted);
                connections.Add(RelayAsync(client));
            }
        }
        finally
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            try
            {
                client.NoDelay = true;
                await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                    },
                    _shutdown.Token);
                await upstream.ConnectAsync(_upstream, _shutdown.Token);
                upstream.NoDelay = true;
                await using var plain = upstream.GetStream();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var toUpstream = PumpAsync(tls, plain, linked.Token);
                var toClient = PumpAsync(plain, tls, linked.Token);
                await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
                await linked.CancelAsync();
                await Task.WhenAll(toUpstream, toClient).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A client that drops the connection (offline emulation, a
                // navigation, a closed browser) ends the relay for that socket.
            }
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, cancellationToken);
        }
        catch (Exception)
        {
            // Either side closing ends this direction; the caller tears down
            // the other direction.
        }
    }
}
