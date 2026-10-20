namespace Paqueteria.ContractTests;

/// <summary>
/// TEN-003-PLATFORM-BOOTSTRAP-2026-10-19: argument validation of the DatabaseMigrator <c>platform-bootstrap</c>
/// command. The subject must be the AuthCenter <c>sub</c> exactly (a lowercase UUID), the names follow the REG-001
/// onboarding limits, and no rejected value is ever echoed. No database.
/// </summary>
public sealed class PlatformBootstrapCommandOptionsTests
{
    private const string Subject = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string LegalName = "Plataforma Sintética S.A. de C.V.";
    private const string DisplayName = "Plataforma Sintética";

    [Fact]
    public void A_complete_command_parses_into_the_subject_and_the_two_names()
    {
        var options = CommandOptions.Parse(Arguments());

        Assert.Equal("platform-bootstrap", options.Command);
        Assert.Equal("PAQUETERIA_MIGRATION_CONNECTION", options.ConnectionEnvironment);
        Assert.Equal(new PlatformBootstrapOptions(Subject, LegalName, DisplayName), options.PlatformBootstrap);
        Assert.Null(options.MasterData);
        Assert.Null(options.MasterDataGate);
        Assert.False(options.ConfirmInitialBaseline);
    }

    [Theory]
    [InlineData("0F8FAD5B-D9CB-469F-A165-70867728950E")]
    [InlineData("{0f8fad5b-d9cb-469f-a165-70867728950e}")]
    [InlineData("0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("owner@example.test")]
    [InlineData("https://authcenter.info/admin-v2/users/0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData(" 0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("")]
    public void The_subject_must_be_the_AuthCenter_sub_as_a_lowercase_uuid(string subject)
    {
        var refused = Assert.Throws<CommandLineException>(() => CommandOptions.Parse(Arguments(subject: subject)));

        Assert.Equal("platform-bootstrap requires --identity-subject as the AuthCenter sub (a lowercase UUID).", refused.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" Plataforma")]
    [InlineData("Plataforma ")]
    [InlineData("Plata\tforma")]
    [InlineData("Plata\u0000forma")]
    public void The_legal_name_is_required_trimmed_and_free_of_control_characters(string? legalName)
    {
        var refused = Assert.Throws<CommandLineException>(() => CommandOptions.Parse(Arguments(legalName: legalName)));

        Assert.StartsWith("platform-bootstrap requires --legal-name", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_names_follow_the_onboarding_length_limits()
    {
        Assert.NotNull(CommandOptions.Parse(Arguments(legalName: new string('L', 200), displayName: new string('D', 80))).PlatformBootstrap);

        Assert.StartsWith(
            "platform-bootstrap requires --legal-name",
            Assert.Throws<CommandLineException>(() => CommandOptions.Parse(Arguments(legalName: new string('L', 201)))).Message,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "platform-bootstrap requires --display-name",
            Assert.Throws<CommandLineException>(() => CommandOptions.Parse(Arguments(displayName: new string('D', 81)))).Message,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "platform-bootstrap requires --display-name",
            Assert.Throws<CommandLineException>(() => CommandOptions.Parse(Arguments(displayName: null))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_connection_is_named_by_environment_variable_and_is_required()
    {
        var withoutConnection = Arguments().Where((_, index) => index is not (1 or 2)).ToArray();

        Assert.Equal(
            "platform-bootstrap requires --connection-env NAME.",
            Assert.Throws<CommandLineException>(() => CommandOptions.Parse(withoutConnection)).Message);
    }

    [Theory]
    [InlineData("--identity-subject")]
    [InlineData("--legal-name")]
    [InlineData("--display-name")]
    public void A_repeated_option_is_refused_instead_of_silently_taking_the_last_value(string option)
    {
        var refused = Assert.Throws<CommandLineException>(() => CommandOptions.Parse([.. Arguments(), option, "otro valor"]));

        Assert.Equal($"Unknown, repeated or incomplete option '{option}'.", refused.Message);
    }

    [Fact]
    public void Options_of_other_commands_and_baseline_flags_are_refused()
    {
        foreach (var extra in new[] { "--confirm-initial-baseline", "--azure-ownership-bridge" })
        {
            Assert.Throws<CommandLineException>(() => CommandOptions.Parse([.. Arguments(), extra]));
        }

        Assert.Throws<CommandLineException>(() => CommandOptions.Parse([.. Arguments(), "--platform-organization-id", Subject]));
        Assert.Throws<CommandLineException>(() => CommandOptions.Parse(
            ["master-data-gate", "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION", "--deployment-class", "REAL",
             "--platform-organization-id", Subject, "--identity-subject", Subject]));
    }

    [Fact]
    public void A_value_without_its_option_is_refused_without_echoing_it()
    {
        var refused = Assert.Throws<CommandLineException>(() => CommandOptions.Parse(
            ["platform-bootstrap", "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION", Subject,
             "--legal-name", LegalName, "--display-name", DisplayName]));

        Assert.DoesNotContain(Subject, refused.Message, StringComparison.Ordinal);
        Assert.Equal("Unexpected argument at position 3; its value is not shown.", refused.Message);
    }

    [Fact]
    public void No_rejection_message_contains_the_subject_or_a_name()
    {
        var attempts = new[]
        {
            Arguments(subject: Subject.ToUpperInvariant()),
            Arguments(legalName: " " + LegalName),
            Arguments(displayName: DisplayName + new string('x', 80)),
            [.. Arguments(), "--identity-subject", Subject],
            [.. Arguments(), LegalName],
        };

        foreach (var attempt in attempts)
        {
            var message = Assert.Throws<CommandLineException>(() => CommandOptions.Parse(attempt)).Message;
            Assert.DoesNotContain(Subject, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LegalName.Trim(), message, StringComparison.Ordinal);
            Assert.DoesNotContain(DisplayName, message, StringComparison.Ordinal);
        }
    }

    private static string[] Arguments(
        string? subject = Subject,
        string? legalName = LegalName,
        string? displayName = DisplayName)
    {
        var arguments = new List<string> { "platform-bootstrap", "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION" };
        if (subject is not null)
        {
            arguments.AddRange(["--identity-subject", subject]);
        }

        if (legalName is not null)
        {
            arguments.AddRange(["--legal-name", legalName]);
        }

        if (displayName is not null)
        {
            arguments.AddRange(["--display-name", displayName]);
        }

        return [.. arguments];
    }
}
