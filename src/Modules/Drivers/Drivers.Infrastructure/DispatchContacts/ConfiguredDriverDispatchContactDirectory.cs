using System.Collections.Frozen;
using Drivers.Application.DispatchContacts;
using Microsoft.Extensions.Options;

namespace Drivers.Infrastructure.DispatchContacts;

/// <summary>
/// DRV-DISPATCH-CONTACT-2026-10-12: the dispatch numbers of <c>Drivers:DispatchContacts</c>, keyed by organization.
/// The options were validated at startup (<see cref="AreValid"/>), so every entry here is complete and each
/// organization appears once. Nothing is read from the database and nothing is logged.
/// </summary>
public sealed class ConfiguredDriverDispatchContactDirectory(IOptions<DriversOptions> options)
    : IDriverDispatchContactDirectory
{
    private readonly FrozenDictionary<Guid, string> numbers = Build(options.Value.DispatchContacts);

    public string? FindPhoneE164(Guid organizationId) =>
        numbers.TryGetValue(organizationId, out var phone) ? phone : null;

    /// <summary>
    /// Every entry has a canonical organization id and a Mexican E.164 number, and no organization appears twice
    /// (the same number may serve several organizations). An empty list is valid: no organization has one.
    /// </summary>
    public static bool AreValid(IReadOnlyList<DriverDispatchContactOptions>? contacts)
    {
        if (contacts is null)
        {
            return true;
        }

        var organizations = new HashSet<Guid>();
        foreach (var contact in contacts)
        {
            if (contact is null ||
                !DriverDispatchContactPolicy.TryParseOrganizationId(contact.OrganizationId, out var organizationId) ||
                !DriverDispatchContactPolicy.IsPhoneE164(contact.PhoneE164) ||
                !organizations.Add(organizationId))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => $"ConfiguredDriverDispatchContactDirectory {{ Count = {numbers.Count} }}";

    private static FrozenDictionary<Guid, string> Build(IReadOnlyList<DriverDispatchContactOptions>? contacts)
    {
        if (!AreValid(contacts))
        {
            // Unreachable after ValidateOnStart; never answer a partial or ambiguous directory.
            throw new InvalidOperationException("Drivers:DispatchContacts is invalid.");
        }

        return (contacts ?? [])
            .ToDictionary(
                contact => Guid.ParseExact(contact.OrganizationId!, "D"),
                contact => contact.PhoneE164!)
            .ToFrozenDictionary();
    }
}
