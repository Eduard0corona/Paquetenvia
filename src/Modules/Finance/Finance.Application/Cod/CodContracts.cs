using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Finance.Application.Cod;

public sealed record RecordCodCollectionCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid OrderId,
    long AmountCents,
    string? Reference,
    bool MfaSatisfied,
    string? RequestId);

public sealed record ReconcileCodCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid CodTransactionId,
    bool MfaSatisfied,
    string? RequestId);

public sealed record CodTransactionResult(
    Guid Id,
    Guid OrderId,
    long AmountCents,
    string Status,
    DateTimeOffset? RecordedAt,
    DateTimeOffset? ReconciledAt);

public interface ICodTransactionService
{
    Task<CodTransactionResult> RecordAsync(
        RecordCodCollectionCommand command,
        CancellationToken cancellationToken);

    Task<CodTransactionResult> ReconcileAsync(
        ReconcileCodCommand command,
        CancellationToken cancellationToken);
}

public static class CodInputPolicy
{
    public const int MaximumReferenceLength = 200;

    public static bool IsValid(RecordCodCollectionCommand value) =>
        value.ActorId != Guid.Empty &&
        value.OrganizationId != Guid.Empty &&
        value.OrderId != Guid.Empty &&
        value.AmountCents > 0 &&
        !string.IsNullOrWhiteSpace(value.Reference) &&
        value.Reference.Length <= MaximumReferenceLength &&
        !char.IsWhiteSpace(value.Reference[0]) &&
        !char.IsWhiteSpace(value.Reference[^1]);

    public static bool IsValid(ReconcileCodCommand value) =>
        value.ActorId != Guid.Empty &&
        value.OrganizationId != Guid.Empty &&
        value.CodTransactionId != Guid.Empty;
}

public static class CodCanonicalizer
{
    public static byte[] Record(RecordCodCollectionCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("order_id", value.OrderId);
        writer.WriteNumber("amount_cents", value.AmountCents);
        writer.WriteString("reference", value.Reference);
    });

    public static byte[] Reconcile(ReconcileCodCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("cod_id", value.CodTransactionId);
    });

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

public enum FinanceTransactionStage
{
    CodInserted,
    CodReconciled,
    AuditInserted,
    BeforeCommit,
}

public interface IFinanceFailureInjector
{
    Task OnStageAsync(FinanceTransactionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpFinanceFailureInjector : IFinanceFailureInjector
{
    public Task OnStageAsync(FinanceTransactionStage stage, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
