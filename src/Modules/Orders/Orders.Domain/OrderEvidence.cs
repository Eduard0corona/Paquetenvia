namespace Orders.Domain;

public sealed class PackageItem
{
    private PackageItem()
    {
    }

    public PackageItem(
        Guid id,
        Guid orderId,
        Guid ownerOrganizationId,
        string description,
        int weightGrams,
        long declaredValueCents,
        string dimensionsMm)
    {
        if (id == Guid.Empty || orderId == Guid.Empty || ownerOrganizationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(description) || description.Length > 250 ||
            weightGrams <= 0 || declaredValueCents < 0 || string.IsNullOrWhiteSpace(dimensionsMm))
        {
            throw new ArgumentException("The package item is invalid.");
        }

        Id = id;
        OrderId = orderId;
        OwnerOrganizationId = ownerOrganizationId;
        Description = description;
        WeightGrams = weightGrams;
        DeclaredValueCents = declaredValueCents;
        DimensionsMm = dimensionsMm;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid? OperatorOrganizationId { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public int WeightGrams { get; private set; }
    public long DeclaredValueCents { get; private set; }
    public string DimensionsMm { get; private set; } = string.Empty;
}

public sealed class OrderEvent
{
    private OrderEvent()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid? OperatorOrganizationId { get; private set; }
    public int AggregateVersion { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string? PublicEventCode { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public Guid? ActorId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}

public sealed class OrderAcceptance
{
    private OrderAcceptance()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid QuoteId { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid? ActorId { get; private set; }
    public string TermsVersion { get; private set; } = string.Empty;
    public string PrivacyVersion { get; private set; } = string.Empty;
    public DateTimeOffset AcceptedAtClient { get; private set; }
    public DateTimeOffset RecordedAtServer { get; private set; }
    public string AcceptanceChannel { get; private set; } = string.Empty;
    public string EvidenceSchemaVersion { get; private set; } = string.Empty;
    public byte[] EvidenceHash { get; private set; } = [];
}
