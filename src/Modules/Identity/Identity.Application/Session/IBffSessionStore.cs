namespace Identity.Application.Session;

/// <summary>
/// Port to the PostgreSQL BFF session store (BFF-SESSION-TABLE-SHAPE). Every call reaches
/// <c>identity.bff_sessions</c> only through the SECURITY DEFINER functions of
/// <c>paqueteria_session_executor</c>; the caller never sees the table. Keys arrive already hashed
/// (SHA-256, <see cref="KeyHashLength"/> bytes) and tickets already protected, so neither the opaque
/// session key nor a readable token ever reaches the database. A technical database failure surfaces as
/// <see cref="Bootstrap.IdentityContextInfrastructureException"/> (503), never as an anonymous request.
/// </summary>
public interface IBffSessionStore
{
    /// <summary><c>security.create_bff_session</c>: stores one new live session.</summary>
    Task CreateAsync(BffSessionRecord session, CancellationToken cancellationToken);

    /// <summary>
    /// <c>security.resolve_bff_session(bytea)</c>: the protected ticket of a live session, or null when the
    /// key is unknown, revoked or expired (indistinguishable by design).
    /// </summary>
    Task<byte[]?> ResolveAsync(byte[] keyHash, CancellationToken cancellationToken);

    /// <summary><c>security.revoke_bff_session(bytea)</c>: logout and session replacement.</summary>
    Task<int> RevokeAsync(byte[] keyHash, CancellationToken cancellationToken);

    /// <summary><c>security.revoke_bff_session(text)</c>: every live session of an AuthCenter <c>sid</c>.</summary>
    Task<int> RevokeByAuthCenterSessionAsync(string authCenterSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>security.revoke_bff_session(text,timestamptz)</c>: every live session of <paramref name="subject"/>
    /// created at or before <paramref name="issuedBefore"/> (never after the database clock).
    /// </summary>
    Task<int> RevokeBySubjectAsync(string subject, DateTimeOffset issuedBefore, CancellationToken cancellationToken);

    public const int KeyHashLength = 32;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumTicketLength = 65_536;
}

/// <summary>
/// One BFF session as the store receives it: the SHA-256 of the opaque session key, the AuthCenter
/// subject and (optional) session id used by back-channel logout, the Data Protection ticket ciphertext
/// and the fixed expiry.
/// </summary>
public sealed record BffSessionRecord(
    byte[] KeyHash,
    string Subject,
    string? AuthCenterSessionId,
    byte[] ProtectedTicket,
    DateTimeOffset ExpiresAt);
