namespace Finance.Infrastructure;

public enum FinanceProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class FinanceOptions
{
    public const string SectionName = "Finance";

    /// <summary>The operational time zone of the pilot city, Culiacán.</summary>
    public const string DefaultOperationalTimeZone = "America/Mazatlan";

    public FinanceProviderKind Provider { get; set; } = FinanceProviderKind.Disabled;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int IdempotencyLifetimeMinutes { get; set; } = 1440;

    /// <summary>
    /// IANA time zone whose calendar days Finance periods are expressed in. A period date is converted to UTC
    /// boundaries in this zone before any timestamp is compared against it.
    /// </summary>
    public string OperationalTimeZone { get; set; } = DefaultOperationalTimeZone;
}

public static class FinanceOperationalTimeZone
{
    /// <summary>
    /// Resolves <paramref name="id"/> only when it is an IANA identifier this host knows. A Windows identifier or
    /// an unknown zone is refused, so a misconfigured host fails at startup instead of shifting periods.
    /// </summary>
    public static bool TryResolve(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found) ||
            !found.HasIanaId ||
            !string.Equals(found.Id, id, StringComparison.Ordinal))
        {
            return false;
        }

        zone = found;
        return true;
    }

    public static TimeZoneInfo Resolve(string? id) =>
        TryResolve(id, out var zone)
            ? zone
            : throw new InvalidOperationException("Finance:OperationalTimeZone is not a known IANA time zone.");
}
