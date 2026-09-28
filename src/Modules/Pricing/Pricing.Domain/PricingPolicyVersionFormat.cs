using System.Text.RegularExpressions;

namespace Pricing.Domain;

/// <summary>
/// PRC-POLICY-VERSION-PER-ORG: every organization versions its own pricing policy on its tariff
/// rules (<c>pricing.tariff_rules.policy_version</c>). The format is the same one AI-06 enforces
/// with a CHECK constraint, so a label is safe to log, to copy into snapshots and to compare.
/// </summary>
public static partial class PricingPolicyVersionFormat
{
    /// <summary>The PostgreSQL CHECK pattern (in PostgreSQL <c>$</c> only matches at the very end).</summary>
    public const string SqlPattern = "^[A-Za-z0-9._-]{1,64}$";

    public static bool IsValid(string? value) => value is not null && Format().IsMatch(value);

    // .NET's `$` also matches before a trailing newline, so the end anchor here is `\z`.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Format();
}
