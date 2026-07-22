namespace Organizations.Domain;

public sealed class Organization
{
    private Organization()
    {
    }

    public Organization(
        Guid id,
        string legalName,
        string displayName,
        OrganizationType organizationType,
        OrganizationStatus status,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty organization id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(legalName) || string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Legal and display names are required.");
        }

        Id = id;
        LegalName = legalName;
        DisplayName = displayName;
        OrganizationType = organizationType;
        Status = status;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string LegalName { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public OrganizationType OrganizationType { get; private set; }

    public OrganizationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
