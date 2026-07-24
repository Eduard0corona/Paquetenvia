namespace Realtime.Application.Configuration;

public enum RealtimeProviderKind
{
    Disabled,
    SignalR,
}

public enum RealtimeBackplaneKind
{
    InProcess,
}

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    public RealtimeProviderKind Provider { get; set; } = RealtimeProviderKind.Disabled;
    public RealtimeBackplaneKind Backplane { get; set; } = RealtimeBackplaneKind.InProcess;
    public string[] AllowedOrigins { get; set; } = [];
    public int ConnectionPermitLimit { get; set; } = 20;
    public int ConnectionWindowSeconds { get; set; } = 60;
    public int AuthorizationCommandTimeoutSeconds { get; set; } = 5;
    public int AuthorizationRetryCount { get; set; } = 2;
    public int MaximumDriverAssignmentGroups { get; set; } = 100;
    public int[] ReconnectDelaysMilliseconds { get; set; } = [0, 2_000, 10_000, 30_000];
}
