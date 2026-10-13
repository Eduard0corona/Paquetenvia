using Custody.Endpoints;
using Dispatch.Endpoints;
using Drivers.Endpoints;
using Finance.Endpoints;
using Identity.Endpoints.AuthCenter;
using Locations.Endpoints;
using Microsoft.Extensions.Options;
using Orders.Endpoints;
using Paqueteria.Infrastructure.Security.RateLimiting;
using Pricing.Endpoints;
using Realtime.Endpoints;
using Reporting.Endpoints;

namespace Paqueteria.Api.Http;

/// <summary>
/// SEC-004: every rate-limit policy the API serves, with its partition, its 429 headers and counter, and the accessor
/// its numbers come from. Modules only name a policy (<c>RequireRateLimiting</c>); none registers a limiter.
/// <para>Phase 4 PR 2, the numbers each module used before SEC-004, read from that module's validated options:</para>
/// <list type="table">
/// <item><term><c>PublicTrackingLookup</c></term><description><c>PublicTracking:LookupPermitLimit</c> per
/// <c>LookupWindowSeconds</c> (60 per 60 s) per client network; the uniform 429, with <c>Retry-After</c> like every
/// other 429 (owner decision of 2026-10-13), and the <c>public_tracking.rate_limit.rejected</c> counter.
/// </description></item>
/// <item><term><c>Realtime.Connections</c></term><description><c>Realtime:ConnectionPermitLimit</c> per
/// <c>ConnectionWindowSeconds</c> (20 per 60 s) for negotiate and connection requests of the three hubs together,
/// per subject, or per client network when anonymous.</description></item>
/// <item><term><c>Drivers.LocationIngestion</c></term><description><c>Drivers:LocationIngestion:BatchPermitLimit</c>
/// per <c>WindowSeconds</c> (30 per 60 s) per subject, or per client network when anonymous.</description></item>
/// </list>
/// <para>
/// Phase 4 PR 3, the decided list (SEC-004-RATE-LIMITS-2026-10-12 and the owner's answers Q1 to Q6 of 2026-10-12),
/// read from <see cref="ApiRateLimitOptions"/> (<c>RateLimits</c>), per user (the subject hash, or the client network
/// when anonymous), except sign-in, per client network:
/// </para>
/// <list type="table">
/// <item><term><c>Orders.CsvImport</c></term><description><c>previewOrderCsv</c> and <c>commitOrderCsv</c> together,
/// <c>RateLimits:CsvImport</c> (10 per 60 s).</description></item>
/// <item><term><c>Orders.CsvImportCommit</c></term><description><c>commitOrderCsv</c>: spends the
/// <c>Orders.CsvImport</c> window, and one commit per user in flight at a time.</description></item>
/// <item><term><c>Finance.SettlementExport</c></term><description><c>exportSettlementCsv</c>, 10 per 60 s.</description></item>
/// <item><term><c>Locations.Create</c></term><description><c>createLocation</c>, 30 per 60 s.</description></item>
/// <item><term><c>Custody.ProofUploadSession</c></term><description><c>createProofUploadSession</c>, 60 per 60 s.</description></item>
/// <item><term><c>Custody.ProofFinalize</c></term><description><c>finalizeProof</c>, 60 per 60 s.</description></item>
/// <item><term><c>Reporting.OperationsDashboard</c></term><description><c>GET /operations/dashboard</c>, 60 per
/// 60 s.</description></item>
/// <item><term><c>Reporting.QueueCounts</c></term><description><c>getOperationsQueueCounts</c>, 60 per 60 s.</description></item>
/// <item><term><c>Orders.List</c></term><description><c>listOrders</c>, 120 per 60 s.</description></item>
/// <item><term><c>Pricing.Quote</c></term><description><c>createQuote</c>, 60 per 60 s.</description></item>
/// <item><term><c>Dispatch.AssignableDrivers</c></term><description><c>listAssignableDrivers</c>, 60 per 60 s.</description></item>
/// <item><term><c>Identity.SignIn</c></term><description><c>startAuthCenterSignIn</c> (<c>GET /auth/login</c>), 20
/// per 60 s per client network; a browser navigation sees a simple page.</description></item>
/// </list>
/// Recipient calls are not a limiter policy: their limit (<c>Drivers:RecipientCalls</c>) is counted in the database
/// per driver and order, and their 429 uses the same <c>TooManyRequests</c> shape.
/// </summary>
public static class ApiRateLimitPolicies
{
    /// <summary>
    /// "1 importación a la vez" (owner answer Q1): one CSV commit per user in flight. A rule, not a rate, so it is not
    /// configuration.
    /// </summary>
    public const int CsvCommitsInFlight = 1;

    /// <summary>One accessor for both CSV policies: a shared window must have the same limit (the registry checks).</summary>
    private static readonly Func<IServiceProvider, FixedWindowLimit> CsvImport =
        static provider => Configured(provider).CsvImport.ToLimit();

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        // Fails closed: an invalid number stops the API at start instead of serving without the limit.
        services.AddOptions<ApiRateLimitOptions>()
            .BindConfiguration(ApiRateLimitOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApiRateLimitOptions>, ApiRateLimitOptionsValidator>();

        return services.AddPaqueteriaRateLimiting(
            new RateLimitPolicy(
                PublicTrackingEndpointDefaults.RateLimitPolicy,
                RateLimitPartitionKind.ClientNetwork,
                static provider => Window(PublicTrackingEndpointDefaults.RateLimit(provider)))
            {
                CountRejection = PublicTrackingEndpointDefaults.CountRateLimitRejection,
            },
            new RateLimitPolicy(
                RealtimeEndpointDefaults.RateLimitPolicy,
                RateLimitPartitionKind.SubjectOrClientNetwork,
                static provider => Window(RealtimeEndpointDefaults.ConnectionRateLimit(provider))),
            new RateLimitPolicy(
                DriverLocationEndpointOptions.RateLimitPolicy,
                RateLimitPartitionKind.SubjectOrClientNetwork,
                static provider => Window(DriverLocationEndpointOptions.RateLimit(provider))),
            new RateLimitPolicy(CsvOrderImportEndpoints.RateLimitPolicy, RateLimitPartitionKind.SubjectOrClientNetwork, CsvImport),
            new RateLimitPolicy(CsvOrderImportEndpoints.CommitRateLimitPolicy, RateLimitPartitionKind.SubjectOrClientNetwork, CsvImport)
            {
                SharesWindowOf = CsvOrderImportEndpoints.RateLimitPolicy,
                InFlightLimit = CsvCommitsInFlight,
            },
            PerUser(SettlementEndpoints.ExportRateLimitPolicy, static options => options.SettlementExport),
            PerUser(LocationEndpoints.CreateRateLimitPolicy, static options => options.LocationCreate),
            PerUser(ProofEndpoints.UploadSessionRateLimitPolicy, static options => options.ProofUploadSession),
            PerUser(ProofEndpoints.FinalizeRateLimitPolicy, static options => options.ProofFinalize),
            PerUser(OperationsDashboardEndpoints.RateLimitPolicy, static options => options.OperationsDashboard),
            PerUser(OperationsQueueCountsEndpoints.RateLimitPolicy, static options => options.QueueCounts),
            PerUser(OrderEndpoints.ListRateLimitPolicy, static options => options.OrderList),
            PerUser(QuoteEndpoints.CreateRateLimitPolicy, static options => options.Quote),
            PerUser(DispatchEndpoints.AssignableDriversRateLimitPolicy, static options => options.AssignableDrivers),
            new RateLimitPolicy(
                AuthCenterDefaults.SignInRateLimitPolicy,
                RateLimitPartitionKind.ClientNetwork,
                static provider => Configured(provider).SignIn.ToLimit()));
    }

    private static RateLimitPolicy PerUser(string name, Func<ApiRateLimitOptions, FixedWindowRateLimitOptions> select) =>
        new(name, RateLimitPartitionKind.SubjectOrClientNetwork, provider => select(Configured(provider)).ToLimit());

    private static ApiRateLimitOptions Configured(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<ApiRateLimitOptions>>().Value;

    private static FixedWindowLimit Window((int PermitLimit, TimeSpan Window) limit) =>
        new(limit.PermitLimit, limit.Window);
}

/// <summary>
/// SEC-004 PR 3: the numbers of the decided list (SEC-004-RATE-LIMITS-2026-10-12), bound from <c>RateLimits</c> and
/// validated at start: an invalid value stops the API. The defaults are the owner's numbers; "Se ajusta tras 2
/// semanas con métricas", so a later owner decision re-sets them here.
/// </summary>
public sealed class ApiRateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>CSV preview and commit together (owner answer Q1).</summary>
    public FixedWindowRateLimitOptions CsvImport { get; set; } = new(10);

    public FixedWindowRateLimitOptions SettlementExport { get; set; } = new(10);

    /// <summary>"crear dirección (cuesta Google)".</summary>
    public FixedWindowRateLimitOptions LocationCreate { get; set; } = new(30);

    /// <summary>"fotos": requesting the upload (owner answer Q2).</summary>
    public FixedWindowRateLimitOptions ProofUploadSession { get; set; } = new(60);

    /// <summary>"fotos": confirming the upload (owner answer Q2).</summary>
    public FixedWindowRateLimitOptions ProofFinalize { get; set; } = new(60);

    /// <summary>"tablero" (owner answer Q3: a budget of its own).</summary>
    public FixedWindowRateLimitOptions OperationsDashboard { get; set; } = new(60);

    /// <summary>"conteos" (owner answer Q3: a budget of its own).</summary>
    public FixedWindowRateLimitOptions QueueCounts { get; set; } = new(60);

    public FixedWindowRateLimitOptions OrderList { get; set; } = new(120);

    public FixedWindowRateLimitOptions Quote { get; set; } = new(60);

    public FixedWindowRateLimitOptions AssignableDrivers { get; set; } = new(60);

    /// <summary>"inicio de sesión 20/min por red" (owner answer Q5: <c>/auth/login</c> only).</summary>
    public FixedWindowRateLimitOptions SignIn { get; set; } = new(20);

    /// <summary>The name of the first invalid setting, or null when every one is valid.</summary>
    public string? Invalid() =>
        new (string Name, FixedWindowRateLimitOptions? Value)[]
            {
                (nameof(CsvImport), CsvImport),
                (nameof(SettlementExport), SettlementExport),
                (nameof(LocationCreate), LocationCreate),
                (nameof(ProofUploadSession), ProofUploadSession),
                (nameof(ProofFinalize), ProofFinalize),
                (nameof(OperationsDashboard), OperationsDashboard),
                (nameof(QueueCounts), QueueCounts),
                (nameof(OrderList), OrderList),
                (nameof(Quote), Quote),
                (nameof(AssignableDrivers), AssignableDrivers),
                (nameof(SignIn), SignIn),
            }
            .Where(static setting => setting.Value is null || !setting.Value.IsValid)
            .Select(static setting => setting.Name)
            .FirstOrDefault();
}

/// <summary>Names the first invalid <c>RateLimits</c> setting; any invalid one stops the API at start.</summary>
public sealed class ApiRateLimitOptionsValidator : IValidateOptions<ApiRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Invalid() is { } invalid
            ? ValidateOptionsResult.Fail(
                $"{ApiRateLimitOptions.SectionName}:{invalid} is invalid: PermitLimit must be 1 to 10000 and WindowSeconds 1 to 3600.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>A fixed window: <see cref="PermitLimit"/> requests per <see cref="WindowSeconds"/>, no queue.</summary>
public sealed class FixedWindowRateLimitOptions
{
    public FixedWindowRateLimitOptions()
    {
    }

    public FixedWindowRateLimitOptions(int permitLimit, int windowSeconds = 60)
    {
        PermitLimit = permitLimit;
        WindowSeconds = windowSeconds;
    }

    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; } = 60;

    /// <summary>The same bounds as <c>Drivers:LocationIngestion</c>.</summary>
    public bool IsValid => PermitLimit is >= 1 and <= 10_000 && WindowSeconds is >= 1 and <= 3600;

    public FixedWindowLimit ToLimit() => new(PermitLimit, TimeSpan.FromSeconds(WindowSeconds));
}
