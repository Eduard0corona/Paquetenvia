namespace Orders.Application.Tracking;

public interface IPublicTrackingTelemetry
{
    void LookupCompleted(string outcome);
    void LookupFailed(string category);
    void RateLimitRejected();
}

public sealed class NoOpPublicTrackingTelemetry : IPublicTrackingTelemetry
{
    public static NoOpPublicTrackingTelemetry Instance { get; } = new();

    private NoOpPublicTrackingTelemetry()
    {
    }

    public void LookupCompleted(string outcome)
    {
    }

    public void LookupFailed(string category)
    {
    }

    public void RateLimitRejected()
    {
    }
}
