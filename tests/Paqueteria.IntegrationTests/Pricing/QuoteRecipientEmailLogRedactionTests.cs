using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Pricing;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002): with every log level on and a scope-including formatter, neither
/// an accepted nor a refused destination e-mail reaches a log message, its structured state or a scope.
/// </summary>
public sealed class QuoteRecipientEmailLogRedactionTests(QuoteHttpWebApplicationFactory factory)
    : IClassFixture<QuoteHttpWebApplicationFactory>
{
    [Fact]
    public async Task No_log_entry_carries_the_destination_email()
    {
        var logs = new CapturingLoggerProvider();
        using var host = factory.WithWebHostBuilder(builder => ConfigureCapture(builder, logs));
        using var client = host.CreateClient();

        string[] emails =
        [
            "aceptado.ntf002@example.invalid",
            "rechazado.ntf002@@example.invalid",
            "sin-dominio.ntf002@example",
        ];
        var statuses = new List<HttpStatusCode>();
        foreach (var email in emails)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quotes")
            {
                Content = new StringContent(Body(email), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
            request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
            request.Headers.Add("Idempotency-Key", $"quote-email-logs-{Guid.NewGuid():N}");
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity], statuses);
        // The capture is real: the host logged the requests.
        Assert.Contains(logs.Entries, entry => entry.Contains("/api/v1/quotes", StringComparison.Ordinal));
        foreach (var email in emails)
        {
            Assert.DoesNotContain(logs.Entries, entry => entry.Contains(email, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(logs.Entries, entry => entry.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string Body(string email) =>
        $$"""
        {
          "origin": {"address_text": "Synthetic origin 100", "contact_name": "Synthetic Sender", "phone": "6671111111", "lat": 24.8, "lng": -107.4},
          "destination": {"address_text": "Synthetic destination 200", "contact_name": "Synthetic Receiver", "phone": "6672222222", "lat": 24.81, "lng": -107.41, "email": "{{email}}"},
          "service_type": "SAME_DAY",
          "consolidated_route": false,
          "packages": [{"description": "Synthetic parcel", "weight_grams": 1000, "declared_value_cents": 5000}]
        }
        """;

    private static void ConfigureCapture(IWebHostBuilder builder, CapturingLoggerProvider logs)
    {
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:Console:FormatterName"] = "json",
                ["Logging:Console:FormatterOptions:IncludeScopes"] = "true",
            }));
        builder.ConfigureLogging(logging => logging
            .AddProvider(logs)
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
    }
}
