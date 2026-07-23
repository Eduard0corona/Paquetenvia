namespace Paqueteria.Application.Idempotency;

public static class IdempotencyKeyPolicy
{
    public const int MinimumLength = 16;
    public const int MaximumLength = 128;

    public static bool IsValid(string? value) =>
        value is { Length: >= MinimumLength and <= MaximumLength } &&
        !string.IsNullOrWhiteSpace(value) &&
        !char.IsWhiteSpace(value[0]) &&
        !char.IsWhiteSpace(value[^1]);
}
