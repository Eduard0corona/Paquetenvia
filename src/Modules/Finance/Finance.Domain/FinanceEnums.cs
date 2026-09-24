namespace Finance.Domain;

/// <summary>Delivery modality a cost is incurred through; mirrors dispatch.assignments.assignment_type.</summary>
public enum DeliveryModality { Own, External, AllyCapacity }

/// <summary>Lifecycle of finance.cod_transactions.status.</summary>
public enum CodStatus { Expected, Recorded, Reconciled, Disputed, Reversed }

public static class FinanceContractValues
{
    /// <summary>Every modality, in the stable order financial breakdowns are reported in.</summary>
    public static IReadOnlyList<DeliveryModality> AllModalities { get; } =
        [DeliveryModality.Own, DeliveryModality.External, DeliveryModality.AllyCapacity];

    public static string ToContractValue(this DeliveryModality value) => value switch
    {
        DeliveryModality.Own => "OWN",
        DeliveryModality.External => "EXTERNAL",
        DeliveryModality.AllyCapacity => "ALLY_CAPACITY",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this CodStatus value) => value switch
    {
        CodStatus.Expected => "EXPECTED",
        CodStatus.Recorded => "RECORDED",
        CodStatus.Reconciled => "RECONCILED",
        CodStatus.Disputed => "DISPUTED",
        CodStatus.Reversed => "REVERSED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParseModality(string? value, out DeliveryModality modality)
    {
        switch (value)
        {
            case "OWN": modality = DeliveryModality.Own; return true;
            case "EXTERNAL": modality = DeliveryModality.External; return true;
            case "ALLY_CAPACITY": modality = DeliveryModality.AllyCapacity; return true;
            default: modality = default; return false;
        }
    }

    public static bool TryParseCodStatus(string? value, out CodStatus status)
    {
        switch (value)
        {
            case "EXPECTED": status = CodStatus.Expected; return true;
            case "RECORDED": status = CodStatus.Recorded; return true;
            case "RECONCILED": status = CodStatus.Reconciled; return true;
            case "DISPUTED": status = CodStatus.Disputed; return true;
            case "REVERSED": status = CodStatus.Reversed; return true;
            default: status = default; return false;
        }
    }
}
