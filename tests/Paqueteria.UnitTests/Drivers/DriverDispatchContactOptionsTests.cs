using Drivers.Application.DispatchContacts;
using Drivers.Infrastructure;
using Drivers.Infrastructure.DispatchContacts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Paqueteria.UnitTests.Drivers;

/// <summary>
/// DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable (Recommended)"): <c>Drivers:DispatchContacts</c>
/// is empty by default; every entry needs a canonical organization id and a Mexican E.164 number, each organization
/// at most once, or the host refuses to start. The numbers below are synthetic.
/// </summary>
public sealed class DriverDispatchContactOptionsTests
{
    private const string FirstOrganization = "11111111-1111-1111-1111-111111111111";
    private const string SecondOrganization = "33333333-3333-4333-8333-333333333333";
    private const string FirstNumber = "+522000000001";
    private const string SecondNumber = "+522000000002";

    [Fact]
    public void Unconfigured_deployment_has_no_dispatch_number_for_any_organization()
    {
        using var provider = Build([]);

        Assert.Empty(provider.GetRequiredService<IOptions<DriversOptions>>().Value.DispatchContacts);
        var directory = provider.GetRequiredService<IDriverDispatchContactDirectory>();
        Assert.IsType<ConfiguredDriverDispatchContactDirectory>(directory);
        Assert.Null(directory.FindPhoneE164(Guid.Parse(FirstOrganization)));
        Assert.Null(directory.FindPhoneE164(Guid.Empty));
    }

    [Fact]
    public void Each_organization_gets_only_its_own_number()
    {
        using var provider = Build(Contacts((FirstOrganization, FirstNumber), (SecondOrganization, SecondNumber)));
        var directory = provider.GetRequiredService<IDriverDispatchContactDirectory>();

        Assert.Equal(FirstNumber, directory.FindPhoneE164(Guid.Parse(FirstOrganization)));
        Assert.Equal(SecondNumber, directory.FindPhoneE164(Guid.Parse(SecondOrganization)));
        Assert.Null(directory.FindPhoneE164(Guid.Parse("22222222-2222-2222-2222-222222222222")));
    }

    [Fact]
    public void The_same_number_may_serve_two_organizations()
    {
        using var provider = Build(Contacts((FirstOrganization, FirstNumber), (SecondOrganization, FirstNumber)));
        var directory = provider.GetRequiredService<IDriverDispatchContactDirectory>();

        Assert.Equal(FirstNumber, directory.FindPhoneE164(Guid.Parse(FirstOrganization)));
        Assert.Equal(FirstNumber, directory.FindPhoneE164(Guid.Parse(SecondOrganization)));
    }

    public static TheoryData<string?, string?> InvalidEntries() => new()
    {
        // Organization id: canonical lowercase hyphenated UUID only, never the nil UUID, never missing.
        { "AAAAAAAA-1111-1111-1111-111111111111", FirstNumber },
        { "{11111111-1111-1111-1111-111111111111}", FirstNumber },
        { "11111111111111111111111111111111", FirstNumber },
        { "00000000-0000-0000-0000-000000000000", FirstNumber },
        { " 11111111-1111-1111-1111-111111111111", FirstNumber },
        { "not-a-uuid", FirstNumber },
        { null, FirstNumber },
        // Number: +52 and ten ASCII digits, the first one 2-9; nothing else.
        { FirstOrganization, null },
        { FirstOrganization, "2000000001" },
        { FirstOrganization, "522000000001" },
        { FirstOrganization, "+52 2000000001" },
        { FirstOrganization, "+52-200-000-0001" },
        { FirstOrganization, "+520000000001" },
        { FirstOrganization, "+521000000001" },
        { FirstOrganization, "+52200000000" },
        { FirstOrganization, "+5220000000011" },
        { FirstOrganization, "+12025550100" },
        { FirstOrganization, "+522000000001\n" },
        { FirstOrganization, "+5220000000٠1" },
    };

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public void An_invalid_entry_stops_the_host_without_printing_the_number(string? organizationId, string? phone)
    {
        var settings = new Dictionary<string, string?>();
        if (organizationId is not null)
        {
            settings["Drivers:DispatchContacts:0:OrganizationId"] = organizationId;
        }

        if (phone is not null)
        {
            settings["Drivers:DispatchContacts:0:PhoneE164"] = phone;
        }

        var failure = AssertRefused(settings);
        if (phone is { Length: > 0 })
        {
            Assert.DoesNotContain(phone.Trim(), failure, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_organization_written_twice_stops_the_host_even_with_the_same_number()
    {
        AssertRefused(Contacts((FirstOrganization, FirstNumber), (FirstOrganization, SecondNumber)));
        AssertRefused(Contacts((FirstOrganization, FirstNumber), (FirstOrganization, FirstNumber)));
    }

    [Fact]
    public void Validation_runs_when_the_host_starts()
    {
        using var provider = Build(Contacts((FirstOrganization, "+520000000001")));

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("Drivers:DispatchContacts", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_and_directory_never_print_the_number()
    {
        var entry = new DriverDispatchContactOptions { OrganizationId = FirstOrganization, PhoneE164 = FirstNumber };
        Assert.DoesNotContain(FirstNumber, entry.ToString(), StringComparison.Ordinal);
        Assert.Contains(FirstOrganization, entry.ToString(), StringComparison.Ordinal);

        using var provider = Build(Contacts((FirstOrganization, FirstNumber)));
        var directory = provider.GetRequiredService<IDriverDispatchContactDirectory>();
        Assert.DoesNotContain(FirstNumber, directory.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", true)]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", true)]
    [InlineData("3F2504E0-4F89-41D3-9A0C-0305E82C3301", false)]
    [InlineData("3f2504e04f8941d39a0c0305e82c3301", false)]
    [InlineData("(3f2504e0-4f89-41d3-9a0c-0305e82c3301)", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Organization_ids_are_canonical_uuids(string? value, bool valid)
    {
        Assert.Equal(valid, DriverDispatchContactPolicy.TryParseOrganizationId(value, out var parsed));
        Assert.Equal(valid ? Guid.ParseExact(value!, "D") : Guid.Empty, parsed);
    }

    private static Dictionary<string, string?> Contacts(params (string OrganizationId, string Phone)[] contacts)
    {
        var settings = new Dictionary<string, string?>();
        for (var index = 0; index < contacts.Length; index++)
        {
            settings[$"Drivers:DispatchContacts:{index}:OrganizationId"] = contacts[index].OrganizationId;
            settings[$"Drivers:DispatchContacts:{index}:PhoneE164"] = contacts[index].Phone;
        }

        return settings;
    }

    private static string AssertRefused(Dictionary<string, string?> settings)
    {
        using var provider = Build(settings);
        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DriversOptions>>().Value);
        Assert.Contains("Drivers:DispatchContacts", failure.Message, StringComparison.Ordinal);
        return failure.Message + string.Join(' ', failure.Failures);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDriversInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
