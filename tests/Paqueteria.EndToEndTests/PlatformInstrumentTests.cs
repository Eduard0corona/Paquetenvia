using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paqueteria.IntegrationTests.Observability;

namespace Paqueteria.EndToEndTests;

/// <summary>
/// The instruments two OPS-005 checks rely on, proven against the real runtime so those checks cannot pass by seeing
/// nothing. They need no container, browser or database, so they run in the <c>dotnet</c> job; the suite itself runs in
/// <c>driver-stops-pwa</c> under <see cref="PlatformEndToEndCategory"/>.
/// </summary>
public sealed class PlatformInstrumentTests
{
    /// <summary>
    /// "Nothing leaves the process": the recorder sees a real request this process sends through
    /// <see cref="HttpClient"/>, with its target, so an empty or all-loopback record means what it says.
    /// </summary>
    [Fact]
    public async Task The_outbound_request_recorder_sees_a_real_request_of_this_process()
    {
        await using var server = await TraceHeaderCaptureServer.StartAsync();
        var mark = OutboundHttpRequests.Instance.Mark();
        using (var client = new HttpClient())
        using (var response = await client.GetAsync(server.For("ops005-recorder-probe")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var seen = Assert.Single(
            OutboundHttpRequests.Instance.Since(mark),
            uri => uri.AbsolutePath == "/ops005-recorder-probe");
        Assert.True(seen.IsLoopback);
        Assert.Equal(server.BaseAddress.Port, seen.Port);
    }

    /// <summary>
    /// Invariant 13 and the trace check read the hosting "Request starting" lines: with the API's JSON console format,
    /// the reader gets the method, the path and a multipart content type of a real Kestrel request.
    /// </summary>
    [Fact]
    public async Task The_api_log_reader_reads_method_path_and_content_type_of_real_hosting_lines()
    {
        using var logs = new JsonConsoleCapture();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
        builder.Logging.AddProvider(logs);
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/ops005-log-probe", () => Results.NoContent());
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent([0x89, 0x50, 0x4e, 0x47]) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "photo", "synthetic.png" },
            };
            using var response = await client.PostAsync("/ops005-log-probe", form);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }

        var request = Assert.Single(ApiLogLines.HostingRequests(logs.Lines));
        Assert.Equal("POST", request.Method);
        Assert.Equal("/ops005-log-probe", request.Path);
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);
    }
}
