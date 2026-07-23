namespace Pricing.Infrastructure.Persistence;

internal sealed class ClientAccountProjection
{
    public Guid Id { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public Guid? PrivateTariffId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

internal sealed class IdempotencyRecord
{
    public Guid OwnerOrganizationId { get; private set; }
    public string Scope { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public byte[] RequestHash { get; private set; } = [];
    public int? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public Guid? ResourceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
}
