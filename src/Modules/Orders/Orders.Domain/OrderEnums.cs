namespace Orders.Domain;

public enum PayerType
{
    Sender,
    Recipient,
    BusinessAccount,
}

public enum OrderStatus
{
    Draft,
}

public static class OrderContractValues
{
    public static string ToContractValue(this PayerType value) => value switch
    {
        PayerType.Sender => "SENDER",
        PayerType.Recipient => "RECIPIENT",
        PayerType.BusinessAccount => "BUSINESS_ACCOUNT",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown payer type."),
    };

    public static string ToContractValue(this OrderStatus value) => value switch
    {
        OrderStatus.Draft => "DRAFT",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown order status."),
    };
}
