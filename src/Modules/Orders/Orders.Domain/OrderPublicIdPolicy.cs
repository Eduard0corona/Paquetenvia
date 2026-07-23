namespace Orders.Domain;

public static class OrderPublicIdPolicy
{
    public const string Prefix = "ORD_";
    public const int EntropyBytes = 16;
    public const int EncodedLength = 22;
    public const int TotalLength = 26;

    public static bool IsValid(string? value) =>
        value is { Length: TotalLength } &&
        value.StartsWith(Prefix, StringComparison.Ordinal) &&
        value.AsSpan(Prefix.Length).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;
}
