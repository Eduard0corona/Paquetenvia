namespace Orders.Infrastructure;

public enum OrdersProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class OrdersOptions
{
    public const string SectionName = "Orders";

    public OrdersProviderKind Provider { get; set; } = OrdersProviderKind.Disabled;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int PageSize { get; set; } = 50;
    public int IdempotencyLifetimeMinutes { get; set; } = 1_440;
    public int PublicIdCollisionRetryCount { get; set; } = 3;
    public int ClaimWindowHours { get; set; } = 72;
    public int TransitionMetadataMaximumBytes { get; set; } =
        global::Orders.Application.Orders.OrderTransitionInputPolicy.DefaultMaximumMetadataUtf8Bytes;
}
