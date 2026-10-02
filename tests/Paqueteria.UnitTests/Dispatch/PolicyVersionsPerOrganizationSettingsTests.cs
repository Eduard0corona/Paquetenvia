using Dispatch.Infrastructure;
using Drivers.Application.Eligibility;
using Drivers.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Paqueteria.UnitTests.Dispatch;

/// <summary>
/// POLICY-VERSIONS-PER-ORG-2026-10-02: the global assignment and driver eligibility policy versions are gone.
/// Each organization carries its own, so a host that still configures either setting refuses to start.
/// </summary>
public sealed class PolicyVersionsPerOrganizationSettingsTests
{
    [Fact]
    public void The_removed_global_assignment_policy_version_setting_fails_startup_validation()
    {
        Assert.Null(Validate<DispatchOptions>(new Dictionary<string, string?>(), AddDispatch));
        var rejected = Validate<DispatchOptions>(
            new Dictionary<string, string?> { ["Dispatch:AssignmentPolicyVersion"] = "piloto-2026-10-v1" },
            AddDispatch);
        Assert.NotNull(rejected);
        Assert.Contains("POLICY-VERSIONS-PER-ORG-2026-10-02", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_removed_global_driver_eligibility_policy_version_setting_fails_startup_validation()
    {
        Assert.Null(Validate<DriversOptions>(new Dictionary<string, string?>(), AddDrivers));
        var rejected = Validate<DriversOptions>(
            new Dictionary<string, string?> { ["Drivers:Eligibility:PolicyVersion"] = "piloto-2026-10-v1" },
            AddDrivers);
        Assert.NotNull(rejected);
        Assert.Contains("POLICY-VERSIONS-PER-ORG-2026-10-02", rejected.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("piloto-2026-10-v1", true)]
    [InlineData("ORG_b.2026-11", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("v1\n", false)]
    [InlineData("versión", false)]
    [InlineData("v1/2", false)]
    public void Organization_policy_version_format_matches_the_AI06_check(string? value, bool valid)
    {
        Assert.Equal(valid, OrganizationPolicyVersionFormat.IsValid(value));
        Assert.True(OrganizationPolicyVersionFormat.IsValid(new string('v', 64)));
        Assert.False(OrganizationPolicyVersionFormat.IsValid(new string('v', 65)));
        Assert.Equal("^[A-Za-z0-9._-]{1,64}$", OrganizationPolicyVersionFormat.SqlPattern);
    }

    private static void AddDispatch(IServiceCollection services, IConfiguration configuration) =>
        services.AddDispatchInfrastructure(configuration);

    private static void AddDrivers(IServiceCollection services, IConfiguration configuration) =>
        services.AddDriversInfrastructure(configuration);

    private static OptionsValidationException? Validate<TOptions>(
        Dictionary<string, string?> settings,
        Action<IServiceCollection, IConfiguration> register)
        where TOptions : class
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        register(services, configuration);
        using var provider = services.BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<IOptions<TOptions>>().Value;
            return null;
        }
        catch (OptionsValidationException exception)
        {
            return exception;
        }
    }
}
