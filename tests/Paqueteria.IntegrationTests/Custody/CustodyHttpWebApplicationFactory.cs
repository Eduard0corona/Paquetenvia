using Custody.Application.ProofUploads;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Custody;

public sealed class CustodyHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid MissingOrderId =
        Guid.Parse("c1000000-0000-0000-0000-000000000001");
    internal static readonly Guid NotReadySessionId =
        Guid.Parse("c1000000-0000-0000-0000-000000000002");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProofUploadSessionService>();
            services.RemoveAll<IProofFinalizationService>();
            services.AddSingleton<IProofUploadSessionService, StubProofService>();
            services.AddSingleton<IProofFinalizationService, StubProofService>();
        });
    }

    private sealed class StubProofService : IProofUploadSessionService, IProofFinalizationService
    {
        public Task<ProofUploadSessionResult> CreateAsync(
            CreateProofUploadSessionCommand command,
            CancellationToken cancellationToken)
        {
            Authorize(command.ActorId, command.MfaSatisfied);
            if (command.OrderId == MissingOrderId)
            {
                throw new ProofNotFoundException();
            }

            var id = Guid.NewGuid();
            return Task.FromResult(new ProofUploadSessionResult(
                id,
                command.OrderId,
                $"quarantine/{command.OrganizationId:D}/{command.OrderId:D}/{id:D}",
                $"https://storage.synthetic.local/proofs/{id:D}?signature=redacted",
                new Dictionary<string, string>
                {
                    ["Content-Type"] = command.ContentType,
                    ["x-amz-meta-session-id"] = id.ToString("D"),
                    ["x-amz-meta-proof-type"] = command.ProofType,
                },
                DateTimeOffset.UtcNow.AddMinutes(15),
                "CREATED"));
        }

        public Task<ProofResult> FinalizeAsync(
            FinalizeProofCommand command,
            CancellationToken cancellationToken)
        {
            Authorize(command.ActorId, command.MfaSatisfied);
            if (command.OrderId == MissingOrderId)
            {
                throw new ProofNotFoundException();
            }

            if (command.UploadSessionId == NotReadySessionId)
            {
                throw new ProofConflictException("UPLOAD_SESSION_NOT_READY");
            }

            if (command.RecipientName is not null)
            {
                throw new ProofConflictException("PII_PROTECTION_UNAVAILABLE");
            }

            return Task.FromResult(new ProofResult(
                Guid.NewGuid(),
                command.OrderId,
                command.UploadSessionId,
                command.ProofType,
                "image/png",
                8,
                Convert.ToHexString(command.Sha256).ToLowerInvariant(),
                command.CapturedAt,
                command.Latitude,
                command.Longitude,
                DateTimeOffset.UtcNow));
        }

        private static void Authorize(Guid actorId, bool mfaSatisfied)
        {
            if (actorId == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3") ||
                actorId == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"))
            {
                throw new ProofForbiddenException();
            }

            if (actorId == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2") &&
                !mfaSatisfied)
            {
                throw new ProofForbiddenException();
            }
        }
    }
}
