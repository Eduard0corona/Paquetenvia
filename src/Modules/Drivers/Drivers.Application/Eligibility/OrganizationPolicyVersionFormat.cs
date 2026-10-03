using System.Text.RegularExpressions;

namespace Drivers.Application.Eligibility;

/// <summary>
/// POLICY-VERSIONS-PER-ORG-2026-10-02: every organization versions its own assignment policy and its own
/// driver eligibility policy (<c>organizations.organizations.assignment_policy_version</c> and
/// <c>driver_eligibility_policy_version</c>). The format is the one AI-06 enforces with a CHECK constraint
/// (the same as the pricing policy version), so a label is safe to log and to copy into audit payloads.
/// </summary>
public static partial class OrganizationPolicyVersionFormat
{
    /// <summary>The PostgreSQL CHECK pattern (in PostgreSQL <c>$</c> only matches at the very end).</summary>
    public const string SqlPattern = "^[A-Za-z0-9._-]{1,64}$";

    public static bool IsValid(string? value) => value is not null && Format().IsMatch(value);

    // .NET's `$` also matches before a trailing newline, so the end anchor here is `\z`.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Format();
}
