using Organizations.Application.Session;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints.Authorization;

/// <summary>The outcome of a tenant capability decision, taken before any persisted state is read.</summary>
public enum TenantCapabilityDecision
{
    Allowed,
    Forbidden,

    /// <summary>
    /// A granted role is held, but every such role needs a satisfied MFA challenge and the session has none.
    /// A second factor is the only unmet requirement, so the 403 carries <c>MFA_REQUIRED</c>
    /// (AUTH-001-MFA-STEP-UP).
    /// </summary>
    MfaRequired,
}

/// <summary>One role an operation admits and whether that role needs a satisfied MFA challenge.</summary>
public sealed record TenantCapabilityGrant(OrganizationRole Role, bool RequiresMfa);

/// <summary>
/// The capability an AI-05 tenant operation requires: the roles it admits. It is decided from the session's
/// active memberships in the selected organization only, so it never reads an order, a quote, an idempotency
/// record or any other persisted resource; the module service may still narrow it (a DRIVER's own assignment,
/// for example) once it is inside its transaction.
/// </summary>
public sealed class TenantCapability
{
    internal TenantCapability(string operationId, IReadOnlyList<TenantCapabilityGrant> grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count == 0 || grants.Select(grant => grant.Role).Distinct().Count() != grants.Count)
        {
            throw new ArgumentException("A capability admits at least one role, each once.", nameof(grants));
        }

        OperationId = operationId;
        Grants = grants;
    }

    /// <summary>The AI-05 operationId this capability guards.</summary>
    public string OperationId { get; }

    public IReadOnlyList<TenantCapabilityGrant> Grants { get; }

    /// <summary>
    /// Allowed when any active role the actor holds in the organization is granted and its MFA requirement,
    /// if any, is met. When the actor holds granted roles but all of them need MFA and the session has none,
    /// the decision is <see cref="TenantCapabilityDecision.MfaRequired"/>; otherwise it is the generic
    /// <see cref="TenantCapabilityDecision.Forbidden"/>.
    /// </summary>
    public TenantCapabilityDecision Evaluate(IEnumerable<OrganizationRole> activeRoles, bool mfaSatisfied)
    {
        ArgumentNullException.ThrowIfNull(activeRoles);
        var held = Grants.Where(grant => activeRoles.Contains(grant.Role)).ToArray();
        if (held.Length == 0)
        {
            return TenantCapabilityDecision.Forbidden;
        }

        return held.Any(grant => !grant.RequiresMfa) || mfaSatisfied
            ? TenantCapabilityDecision.Allowed
            : TenantCapabilityDecision.MfaRequired;
    }

    /// <summary>
    /// Decides from the session alone: an inactive identity, an unselected tenant or a tenant the session
    /// cannot access is always <see cref="TenantCapabilityDecision.Forbidden"/>.
    /// </summary>
    public TenantCapabilityDecision Evaluate(IOrganizationRequestSession session, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsAuthenticated || !session.IsActive || session.UserId is null ||
            organizationId == Guid.Empty || !session.HasOrganizationAccess(organizationId))
        {
            return TenantCapabilityDecision.Forbidden;
        }

        return Evaluate(
            session.ActiveMemberships
                .Where(membership => membership.OrganizationId == organizationId)
                .Select(membership => membership.Role),
            session.MfaSatisfied);
    }
}
