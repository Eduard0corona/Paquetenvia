using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Finance.Domain.Settlements;
using Paqueteria.Application.Idempotency;

namespace Finance.Application.Settlements;

public sealed record CreateSettlementCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid DriverId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    bool MfaSatisfied,
    string? RequestId);

public sealed record GetSettlementQuery(
    Guid ActorId,
    Guid OrganizationId,
    Guid SettlementId,
    bool MfaSatisfied);

/// <summary>
/// AI05-LIST-SETTLEMENTS: one server-sized page of the selected organization's settlements, newest first.
/// Every filter is optional; a period filter keeps the settlements whose whole period lies inside it, and a
/// payee filter keeps the settlements of that payee.
/// </summary>
public sealed record ListSettlementsQuery(
    Guid ActorId,
    Guid OrganizationId,
    string? Status,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    SettlementCursor? Cursor,
    bool MfaSatisfied,
    Guid? PayeeId = null);

/// <summary>The keyset position after the last settlement of a page: its created_at and id.</summary>
public sealed record SettlementCursor(DateTimeOffset CreatedAt, Guid Id);

public sealed record SettlementPageResult(IReadOnlyList<SettlementResult> Items, string? NextCursor)
{
    public IReadOnlyList<SettlementResult> Items { get; } = Items.ToArray();
}

public sealed record AddSettlementAdjustmentCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid SettlementId,
    long AmountCents,
    string? Reason,
    bool MfaSatisfied,
    string? RequestId);

/// <summary>Approval and payment: a lifecycle step that carries nothing but the settlement it applies to.</summary>
public sealed record SettlementTransitionCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid SettlementId,
    bool MfaSatisfied,
    string? RequestId);

public sealed record VoidSettlementCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid SettlementId,
    string? Reason,
    bool MfaSatisfied,
    string? RequestId);

public sealed record SettlementLineResult(
    Guid Id,
    string LineType,
    Guid? OrderId,
    long AmountCents,
    string SourceReference,
    DateTimeOffset CreatedAt);

/// <summary>A persisted settlement header and its immutable lines, exactly as stored.</summary>
public sealed record SettlementResult(
    Guid Id,
    string PayeeType,
    Guid PayeeId,
    string Status,
    long TotalCents,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SettlementLineResult> Lines)
{
    public IReadOnlyList<SettlementLineResult> Lines { get; } = Lines.ToArray();
}

public sealed record SettlementCsvDocument(string FileName, byte[] Content);

public interface ISettlementService
{
    Task<SettlementResult> CreateAsync(CreateSettlementCommand command, CancellationToken cancellationToken);

    Task<SettlementResult> GetAsync(GetSettlementQuery query, CancellationToken cancellationToken);

    Task<SettlementPageResult> ListAsync(ListSettlementsQuery query, CancellationToken cancellationToken);

    Task<SettlementResult> AddAdjustmentAsync(
        AddSettlementAdjustmentCommand command,
        CancellationToken cancellationToken);

    Task<SettlementResult> ApproveAsync(SettlementTransitionCommand command, CancellationToken cancellationToken);

    Task<SettlementResult> MarkPaidAsync(SettlementTransitionCommand command, CancellationToken cancellationToken);

    Task<SettlementResult> VoidAsync(VoidSettlementCommand command, CancellationToken cancellationToken);

    Task<SettlementCsvDocument> ExportCsvAsync(GetSettlementQuery query, CancellationToken cancellationToken);
}

public enum SettlementConflictCode
{
    InvalidRequest,
    IdempotencyConflict,
    InconsistentReplayEvidence,
    SettlementStateConflict,
    CashPending,
    IncidentPending,
    ClaimPending,
}

/// <summary>
/// A settlement request that conflicts with its shape, its idempotency evidence or the current state. The
/// code is the whole public statement; the current status and source evidence are never disclosed.
/// </summary>
public sealed class SettlementConflictException(SettlementConflictCode code, Exception? inner = null)
    : Exception("The settlement operation conflicts with current state.", inner)
{
    public SettlementConflictCode Code { get; } = code;
}

/// <summary>
/// SET-001 capability. Settlements are a Finance control: an active FINANCE member may operate them and an
/// active PLATFORM_ADMIN only with a satisfied MFA challenge. DISPATCHER, DRIVER, VIEWER and every other
/// role may neither read nor change them. The one membership that decides is the most privileged active
/// one, so a PLATFORM_ADMIN who is also FINANCE still has to satisfy MFA.
/// </summary>
public static class SettlementAuthorizationPolicy
{
    public static bool CanOperate(FinanceAuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.UserActive &&
            context.MembershipActive &&
            context.ActiveRole switch
            {
                "FINANCE" => true,
                "PLATFORM_ADMIN" => context.MfaSatisfied,
                _ => false,
            };
    }

    /// <summary>
    /// D7-SETTLEMENT-MFA (D7-SETTLEMENT-RULES, AI-05 x-pilot-contract-deltas): approveSettlement and
    /// markSettlementPaid require a satisfied MFA challenge for every permitted role, FINANCE included.
    /// </summary>
    public static bool CanApproveOrPay(FinanceAuthorizationContext context) =>
        CanOperate(context) && context.MfaSatisfied;
}

/// <summary>Request shape, decided before any productive transaction is opened.</summary>
public static class SettlementInputPolicy
{
    public static bool IsValid(CreateSettlementCommand value) =>
        Actor(value.ActorId, value.OrganizationId) &&
        IdempotencyKeyPolicy.IsValid(value.IdempotencyKey) &&
        value.DriverId != Guid.Empty &&
        value.PeriodTo >= value.PeriodFrom;

    public static bool IsValid(GetSettlementQuery value) =>
        Actor(value.ActorId, value.OrganizationId) && value.SettlementId != Guid.Empty;

    /// <summary>An inverted period window can match nothing, so it is a request error rather than an empty page.</summary>
    public static bool IsValid(ListSettlementsQuery value) =>
        Actor(value.ActorId, value.OrganizationId) &&
        (value.Status is null || SettlementContractValues.TryParseStatus(value.Status, out _)) &&
        (value.PeriodFrom is not { } from || value.PeriodTo is not { } to || from <= to) &&
        (value.Cursor is null || value.Cursor.Id != Guid.Empty) &&
        value.PayeeId != Guid.Empty;

    /// <summary>A non-zero signed amount: an adjustment that does not move the total records nothing.</summary>
    public static bool IsValid(AddSettlementAdjustmentCommand value) =>
        Actor(value.ActorId, value.OrganizationId) &&
        IdempotencyKeyPolicy.IsValid(value.IdempotencyKey) &&
        value.SettlementId != Guid.Empty &&
        value.AmountCents != 0 &&
        SettlementReasonPolicy.IsValid(value.Reason);

    public static bool IsValid(SettlementTransitionCommand value) =>
        Actor(value.ActorId, value.OrganizationId) &&
        IdempotencyKeyPolicy.IsValid(value.IdempotencyKey) &&
        value.SettlementId != Guid.Empty;

    public static bool IsValid(VoidSettlementCommand value) =>
        Actor(value.ActorId, value.OrganizationId) &&
        IdempotencyKeyPolicy.IsValid(value.IdempotencyKey) &&
        value.SettlementId != Guid.Empty &&
        SettlementReasonPolicy.IsValid(value.Reason);

    private static bool Actor(Guid actorId, Guid organizationId) =>
        actorId != Guid.Empty && organizationId != Guid.Empty;
}

/// <summary>
/// The page size of listSettlements is owned by the server, like every other AI-05 list: clients cannot
/// configure it. Each item carries its lines, so the page stays small.
/// </summary>
public static class SettlementListPolicy
{
    public const int PageSize = 50;
}

/// <summary>
/// Opaque Base64URL cursor: the UTC ticks of the last item's created_at and its id. A cursor that does not
/// decode exactly is rejected as INVALID_REQUEST; it is never reinterpreted.
/// </summary>
public static class SettlementCursorCodec
{
    public static string Encode(SettlementCursor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = string.Create(CultureInfo.InvariantCulture, $"{value.CreatedAt.UtcTicks}:{value.Id:D}");
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out SettlementCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                0 => string.Empty,
                _ => throw new FormatException("Invalid Base64URL."),
            };
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 ||
                !long.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !Guid.TryParseExact(text.AsSpan(separator + 1), "D", out var id) || id == Guid.Empty ||
                Encode(new SettlementCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id)) != value)
            {
                return false;
            }

            cursor = new SettlementCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// The request hash an Idempotency-Key is bound to: the tenant plus every field that changes the outcome.
/// Each operation has its own idempotency scope, so equal hashes of different operations never meet.
/// </summary>
public static class SettlementCanonicalizer
{
    public static byte[] Create(CreateSettlementCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("driver_id", value.DriverId);
        writer.WriteString("period_from", Date(value.PeriodFrom));
        writer.WriteString("period_to", Date(value.PeriodTo));
    });

    public static byte[] Adjust(AddSettlementAdjustmentCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("settlement_id", value.SettlementId);
        writer.WriteNumber("amount_cents", value.AmountCents);
        writer.WriteString("reason", value.Reason);
    });

    public static byte[] Transition(SettlementTransitionCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("settlement_id", value.SettlementId);
    });

    public static byte[] Void(VoidSettlementCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("settlement_id", value.SettlementId);
        writer.WriteString("reason", value.Reason);
    });

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static byte[] Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return SHA256.HashData(stream.ToArray());
    }

    private static void Tenant(Utf8JsonWriter writer, Guid organizationId) =>
        writer.WriteString("tenant", organizationId.ToString("D", CultureInfo.InvariantCulture));
}
