using Paqueteria.Application;
using Paqueteria.Infrastructure;

namespace Paqueteria.UnitTests;

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_returns_a_current_utc_timestamp()
    {
        IClock clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;

        var value = clock.UtcNow;

        var after = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.InRange(value, before, after);
    }
}
