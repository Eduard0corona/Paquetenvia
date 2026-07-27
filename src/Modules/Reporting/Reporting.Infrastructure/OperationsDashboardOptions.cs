namespace Reporting.Infrastructure;

public enum OperationsDashboardProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class OperationsDashboardOptions
{
    public const string SectionName = "OperationsDashboard";

    public OperationsDashboardProviderKind Provider { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 5;
    public int DefaultPageSize { get; set; } = 50;
    public int MaximumPageSize { get; set; } = 100;
    public int MaximumDateRangeDays { get; set; } = 31;
}
