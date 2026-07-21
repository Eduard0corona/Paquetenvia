namespace Paqueteria.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
