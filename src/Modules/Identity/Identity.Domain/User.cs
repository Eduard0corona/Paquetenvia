namespace Identity.Domain;

public sealed class User
{
    private User()
    {
    }

    public User(Guid id, string identitySubject, byte[]? emailCiphertext, IdentityStatus status, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty user id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(identitySubject))
        {
            throw new ArgumentException("An identity subject is required.", nameof(identitySubject));
        }

        Id = id;
        IdentitySubject = identitySubject;
        EmailCiphertext = emailCiphertext;
        Status = status;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string IdentitySubject { get; private set; } = string.Empty;

    public byte[]? EmailCiphertext { get; private set; }

    public IdentityStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
