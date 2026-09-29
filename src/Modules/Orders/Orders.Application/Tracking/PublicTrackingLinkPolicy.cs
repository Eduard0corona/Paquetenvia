namespace Orders.Application.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK lifetime of a public tracking link. A link lives while the order is in progress, including
/// RESCHEDULED (a rescheduled order is assigned again and goes out again), and for 24 hours after the order
/// first reaches a final public status: DELIVERED (which also covers CLOSED, CLAIM_OPEN and CLAIM_RESOLVED),
/// RETURNED or CANCELLED. None of those public statuses can be left, so the first such event fixes the end.
/// </summary>
/// <remarks>
/// The authoritative check is the SQL public lookup, <c>security.get_public_tracking_projection(text)</c>, which
/// applies the same rule atomically with the token lookup. This type mirrors it for the authenticated
/// get-or-create, which refuses to create links for finished orders and reports when a live one stops working.
/// </remarks>
public static class PublicTrackingLinkPolicy
{
    public static readonly TimeSpan FinalStatusGrace = TimeSpan.FromHours(24);

    /// <summary>The <c>public_event_code</c> values whose first occurrence ends an order's public lifecycle.</summary>
    public static readonly IReadOnlyList<string> FinalPublicEventCodes = ["DELIVERED", "RETURNED", "CANCELLED"];

    public static bool IsFinal(PublicOrderStatus status) =>
        status is PublicOrderStatus.Delivered or PublicOrderStatus.Returned or PublicOrderStatus.Cancelled;

    /// <summary>
    /// The end of the link's validity: null while the order is in progress; the first final event plus 24 hours
    /// once it is finished. A finished order without that event (not written by the productive transition paths)
    /// is already expired, as it is for the SQL lookup.
    /// </summary>
    public static DateTimeOffset? ValidUntil(
        PublicOrderStatus status,
        DateTimeOffset? firstFinalEventAt,
        DateTimeOffset now)
    {
        if (!IsFinal(status))
        {
            return null;
        }

        return firstFinalEventAt is { } finishedAt ? finishedAt + FinalStatusGrace : now;
    }

    /// <summary>
    /// The public page of a token: <c>{PublicBaseUrl}/track/{token}</c>. The base is validated at start with
    /// <see cref="IsValidPublicBaseUrl"/> and again here, on its own: <paramref name="allowLoopbackHttp"/> is the
    /// same Development/Testing decision the start validation uses (<see cref="PublicTrackingBaseUrlPolicy"/>), so
    /// outside those environments an http base, loopback or not, throws and no link is built.
    /// </summary>
    public static string BuildUrl(string publicBaseUrl, string token, bool allowLoopbackHttp)
    {
        if (!IsValidPublicBaseUrl(publicBaseUrl, allowLoopbackHttp))
        {
            throw new ArgumentException("The public tracking base URL is invalid.", nameof(publicBaseUrl));
        }

        if (token is not { Length: 43 } || token.Any(static value =>
                !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_'))
        {
            throw new ArgumentException("The public tracking token is invalid.", nameof(token));
        }

        return $"{publicBaseUrl.TrimEnd('/')}/track/{token}";
    }

    /// <summary>
    /// An absolute https origin, such as <c>https://paquetenvia.com</c>, with no user info, path, query or fragment.
    /// Development and Testing may also use an http loopback origin (<c>http://127.0.0.1:3000</c>).
    /// </summary>
    public static bool IsValidPublicBaseUrl(string? value, bool allowLoopbackHttp = false) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('*', StringComparison.Ordinal) &&
        !value.Contains('?', StringComparison.Ordinal) &&
        !value.Contains('#', StringComparison.Ordinal) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
        !string.IsNullOrEmpty(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/";
}

/// <summary>
/// TRK-002-AUTO-LINK: the environment decision for public tracking base URLs. Only Development and Testing may use an
/// http loopback origin; every other environment requires https. Orders' DependencyInjection creates the one
/// instance from the host environment, with the same flag that allows the synthetic link key, and both the start
/// validation and <see cref="PublicTrackingLinkPolicy.BuildUrl"/> read it. It is never bound from configuration.
/// </summary>
public sealed class PublicTrackingBaseUrlPolicy
{
    public PublicTrackingBaseUrlPolicy(bool allowLoopbackHttp) => AllowLoopbackHttp = allowLoopbackHttp;

    public bool AllowLoopbackHttp { get; }

    public bool IsValid(string? publicBaseUrl) =>
        PublicTrackingLinkPolicy.IsValidPublicBaseUrl(publicBaseUrl, AllowLoopbackHttp);

    public string BuildUrl(string publicBaseUrl, string token) =>
        PublicTrackingLinkPolicy.BuildUrl(publicBaseUrl, token, AllowLoopbackHttp);
}
