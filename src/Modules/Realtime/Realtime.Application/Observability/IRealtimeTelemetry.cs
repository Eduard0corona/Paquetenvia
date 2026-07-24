namespace Realtime.Application.Observability;

public interface IRealtimeTelemetry
{
    IDisposable MeasureAuthorization(string hub, string authKind);
    IDisposable MeasurePublication(string eventType);
    void ConnectionAccepted(string hub, string authKind);
    void ConnectionRejected(string hub, string authKind);
    void ConnectionClosed(string hub);
    void PublicationSucceeded(string eventType);
    void PublicationFailed(string eventType);
}
