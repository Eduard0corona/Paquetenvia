using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Idempotency;
using Paqueteria.Infrastructure;

namespace Paqueteria.UnitTests.Operations;

/// <summary>OPS-003-SERVER-72H-REJECTION boundaries (AI-05 x-offline-operation-age).</summary>
public sealed class OfflineOperationAgePolicyTests
{
    private static readonly DateTimeOffset ServerNow = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Maximum_age_is_fixed_at_72_hours_and_the_code_is_stable()
    {
        Assert.Equal(TimeSpan.FromHours(72), OfflineOperationAgePolicy.MaximumAge);
        Assert.Equal("OFFLINE_OPERATION_EXPIRED", OfflineOperationAgePolicy.ExpiredCode);
        Assert.Equal(TimeSpan.FromMinutes(5), OfflineOperationAgePolicy.DefaultClockTolerance);
        Assert.Equal(TimeSpan.FromMinutes(5), OfflineOperationAgePolicy.MaximumClockTolerance);
        // Only the clock tolerance is a setting; the maximum age has no configuration surface.
        Assert.Equal(["ClockToleranceSeconds"], typeof(OfflineOperationOptions).GetProperties().Select(property => property.Name));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(71 * 3600 + 59 * 60)]
    [InlineData(72 * 3600)]
    public void Operations_up_to_exactly_72_hours_old_are_accepted(int ageSeconds) =>
        Assert.Equal(
            OfflineOperationAge.Accepted,
            OfflineOperationAgePolicy.Default.Evaluate(ServerNow.AddSeconds(-ageSeconds), ServerNow));

    [Theory]
    [InlineData(72 * 3600 + 1)]
    [InlineData(73 * 3600)]
    [InlineData(30 * 24 * 3600)]
    public void Operations_older_than_72_hours_are_expired(int ageSeconds) =>
        Assert.Equal(
            OfflineOperationAge.Expired,
            OfflineOperationAgePolicy.Default.Evaluate(ServerNow.AddSeconds(-ageSeconds), ServerNow));

    [Fact]
    public void One_tick_past_72_hours_is_already_expired()
    {
        var boundary = ServerNow - OfflineOperationAgePolicy.MaximumAge;
        Assert.Equal(OfflineOperationAge.Accepted, OfflineOperationAgePolicy.Default.Evaluate(boundary, ServerNow));
        Assert.Equal(OfflineOperationAge.Expired, OfflineOperationAgePolicy.Default.Evaluate(boundary.AddTicks(-1), ServerNow));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(300, 300, true)]
    [InlineData(300, 301, false)]
    [InlineData(120, 60, true)]
    [InlineData(120, 121, false)]
    public void Future_timestamps_are_accepted_only_within_the_clock_tolerance(
        int toleranceSeconds,
        int aheadSeconds,
        bool accepted)
    {
        var policy = new OfflineOperationAgePolicy(TimeSpan.FromSeconds(toleranceSeconds));

        Assert.Equal(
            accepted ? OfflineOperationAge.Accepted : OfflineOperationAge.AheadOfServerClock,
            policy.Evaluate(ServerNow.AddSeconds(aheadSeconds), ServerNow));
    }

    [Fact]
    public void A_future_timestamp_beyond_the_tolerance_is_never_treated_as_fresh()
    {
        Assert.Equal(
            OfflineOperationAge.AheadOfServerClock,
            OfflineOperationAgePolicy.Default.Evaluate(ServerNow.AddDays(10), ServerNow));
    }

    [Fact]
    public void A_missing_timestamp_is_not_declared_and_not_age_checked() =>
        Assert.Equal(OfflineOperationAge.NotDeclared, OfflineOperationAgePolicy.Default.Evaluate(null, ServerNow));

    [Fact]
    public void The_client_offset_does_not_change_the_instant_being_compared()
    {
        var justInside = new DateTimeOffset(2026, 9, 24, 5, 0, 0, TimeSpan.FromHours(-7));
        var justOutside = justInside.AddSeconds(-1);

        Assert.Equal(OfflineOperationAge.Accepted, OfflineOperationAgePolicy.Default.Evaluate(justInside, ServerNow));
        Assert.Equal(OfflineOperationAge.Expired, OfflineOperationAgePolicy.Default.Evaluate(justOutside, ServerNow));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void A_tolerance_outside_zero_to_five_minutes_is_refused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineOperationAgePolicy(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Registration_defaults_to_five_minutes_and_binds_the_configured_tolerance()
    {
        using (var defaults = Provider([]))
        {
            Assert.Equal(TimeSpan.FromMinutes(5), defaults.GetRequiredService<OfflineOperationAgePolicy>().ClockTolerance);
        }

        using var configured = Provider(new() { ["OfflineOperations:ClockToleranceSeconds"] = "30" });
        Assert.Equal(TimeSpan.FromSeconds(30), configured.GetRequiredService<OfflineOperationAgePolicy>().ClockTolerance);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("301")]
    public void An_out_of_range_tolerance_fails_options_validation(string value)
    {
        using var provider = Provider(new() { ["OfflineOperations:ClockToleranceSeconds"] = value });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<OfflineOperationOptions>>().Value);
        Assert.Contains("OfflineOperations:ClockToleranceSeconds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registering_from_several_modules_keeps_one_policy()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddOfflineOperationAgePolicy(configuration);
        services.AddOfflineOperationAgePolicy(configuration);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(OfflineOperationAgePolicy));
    }

    private static ServiceProvider Provider(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddOfflineOperationAgePolicy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }
}
