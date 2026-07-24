using System.Collections.Concurrent;
using Dispatch.Application.Assignments;
using Dispatch.Application.Stops;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Dispatch;

public sealed class DispatchHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly StubDispatchService service = new();

    internal static readonly Guid DispatcherId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10");
    internal static readonly Guid DriverActorId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");
    internal static readonly Guid AdminMfaId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
    internal static readonly Guid AdminNoMfaId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3");
    internal static readonly Guid ExpiredDocumentDriverId =
        Guid.Parse("d2000000-0000-0000-0000-000000000001");
    internal static readonly Guid IneligibleDriverId =
        Guid.Parse("d2000000-0000-0000-0000-000000000002");
    internal static readonly Guid MissingOrderId =
        Guid.Parse("d2000000-0000-0000-0000-000000000003");
    internal static readonly Guid MissingDriverId =
        Guid.Parse("d2000000-0000-0000-0000-000000000004");
    internal static readonly Guid CrossTenantOrderId =
        Guid.Parse("d2000000-0000-0000-0000-000000000005");
    internal static readonly Guid CrossTenantDriverId =
        Guid.Parse("d2000000-0000-0000-0000-000000000006");
    internal static readonly Guid ConflictOrderId =
        Guid.Parse("d2000000-0000-0000-0000-000000000007");

    internal void SetStops(IReadOnlyList<DriverStopResult> stops) => service.Stops = stops;
    internal int Effects => service.Effects;
    internal int Invocations => service.Invocations;

    internal void SeedIdempotency(
        Guid organizationId,
        string key,
        Guid orderId,
        Guid driverId,
        long costCents,
        IdempotencyStubState state,
        bool matchingSignature = true) =>
        service.SeedIdempotency(
            organizationId,
            key,
            orderId,
            driverId,
            costCents,
            state,
            matchingSignature);

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
            services.RemoveAll<IAssignmentService>();
            services.RemoveAll<IDriverStopsQuery>();
            services.AddSingleton<IAssignmentService>(service);
            services.AddSingleton<IDriverStopsQuery>(service);
        });
    }

    private sealed class StubDispatchService : IAssignmentService, IDriverStopsQuery
    {
        private readonly ConcurrentDictionary<(Guid Tenant, string Key), Stored> stored = new();
        private int effects;
        private int invocations;

        public IReadOnlyList<DriverStopResult> Stops { get; set; } = [];
        public int Effects => Volatile.Read(ref effects);
        public int Invocations => Volatile.Read(ref invocations);

        public Task<AssignmentResult> CreateOwnDriverAssignmentAsync(
            CreateOwnDriverAssignmentCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref invocations);
            if (command.ActorId == AdminNoMfaId ||
                (command.ActorId != AdminMfaId && command.ActorId != DispatcherId))
            {
                throw new AssignmentForbiddenException();
            }

            if (command.OrderId == MissingOrderId ||
                command.OrderId == CrossTenantOrderId ||
                command.DriverId == MissingDriverId ||
                command.DriverId == CrossTenantDriverId)
            {
                throw new AssignmentNotFoundException();
            }

            if (command.OrderId == ConflictOrderId)
            {
                throw new AssignmentConflictException(AssignmentConflictCode.InvalidOrderState);
            }

            var signature =
                $"{command.OrderId:D}|{command.DriverId:D}|{command.AssignmentType}|{command.CostCents}|null";
            if (stored.TryGetValue((command.OrganizationId, command.IdempotencyKey), out var existing))
            {
                if (existing.Signature != signature)
                {
                    throw new AssignmentConflictException(AssignmentConflictCode.IdempotencyConflict);
                }

                if (existing.State == IdempotencyStubState.Incomplete)
                {
                    throw new AssignmentConflictException(AssignmentConflictCode.IdempotencyConflict);
                }

                if (existing.State == IdempotencyStubState.InconsistentEvidence)
                {
                    throw new AssignmentConflictException(AssignmentConflictCode.InconsistentReplayEvidence);
                }

                return Task.FromResult(existing.Result!);
            }

            if (command.DriverId == ExpiredDocumentDriverId)
            {
                throw new AssignmentConflictException(AssignmentConflictCode.DriverDocumentExpired);
            }

            if (command.DriverId == IneligibleDriverId)
            {
                throw new AssignmentConflictException(AssignmentConflictCode.DriverIneligible);
            }

            var result = new AssignmentResult(
                Guid.NewGuid(),
                command.OrderId,
                command.DriverId,
                "ACCEPTED",
                new MoneyResult("MXN", command.CostCents!.Value));
            stored[(command.OrganizationId, command.IdempotencyKey)] =
                new Stored(signature, result, IdempotencyStubState.Completed);
            Interlocked.Increment(ref effects);
            return Task.FromResult(result);
        }

        public void SeedIdempotency(
            Guid organizationId,
            string key,
            Guid orderId,
            Guid driverId,
            long costCents,
            IdempotencyStubState state,
            bool matchingSignature)
        {
            var signatureDriver = matchingSignature ? driverId : Guid.NewGuid();
            var signature = $"{orderId:D}|{signatureDriver:D}|OWN|{costCents}|null";
            var result = new AssignmentResult(
                Guid.NewGuid(),
                orderId,
                driverId,
                "ACCEPTED",
                new MoneyResult("MXN", costCents));
            stored[(organizationId, key)] = new Stored(signature, result, state);
        }

        public Task<IReadOnlyList<DriverStopResult>> ListCurrentDriverStopsAsync(
            Guid actorId,
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return actorId == DriverActorId
                ? Task.FromResult(Stops)
                : throw new DriverStopsForbiddenException();
        }

        private sealed record Stored(
            string Signature,
            AssignmentResult? Result,
            IdempotencyStubState State);
    }
}

internal enum IdempotencyStubState
{
    Completed,
    Incomplete,
    InconsistentEvidence,
}
