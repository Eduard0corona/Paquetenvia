namespace Paqueteria.ContractTests;

/// <summary>
/// REL-001 restore vigente (Z9, Z18): the DatabaseMigrator <c>assert</c> option <c>--compare-row-counts-env</c>, used
/// only by the ENV-001 restore drill. It belongs to <c>assert</c> alone, names one environment variable other than the
/// connection under assert, and is not repeated. No database.
/// </summary>
public sealed class RestoreDrillRowCountOptionsTests
{
    [Fact]
    public void Assert_takes_the_source_connection_of_the_drill()
    {
        var options = CommandOptions.Parse(
            ["assert", "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION", "--compare-row-counts-env", "PAQUETERIA_ROW_COUNT_SOURCE"]);

        Assert.Equal("assert", options.Command);
        Assert.Equal("PAQUETERIA_MIGRATION_CONNECTION", options.ConnectionEnvironment);
        Assert.Equal("PAQUETERIA_ROW_COUNT_SOURCE", options.CompareRowCountsEnvironment);
    }

    [Fact]
    public void Assert_without_the_option_compares_nothing()
    {
        Assert.Null(CommandOptions.Parse(["assert", "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION"])
            .CompareRowCountsEnvironment);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("preflight")]
    [InlineData("runtime-logins")]
    public void Only_assert_accepts_the_option(string command)
    {
        Assert.Throws<CommandLineException>(() => CommandOptions.Parse(
            [command, "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION", "--compare-row-counts-env", "PAQUETERIA_ROW_COUNT_SOURCE"]));
    }

    [Theory]
    [InlineData("PAQUETERIA_MIGRATION_CONNECTION")]
    [InlineData("1SOURCE")]
    [InlineData("SOURCE-CONNECTION")]
    [InlineData("Host=pilot;Password=x")]
    public void The_source_is_another_valid_variable_name(string name)
    {
        var refused = Assert.Throws<CommandLineException>(() => CommandOptions.Parse(
            ["assert", "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION", "--compare-row-counts-env", name]));

        Assert.DoesNotContain(name, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_option_is_not_repeated_and_needs_a_value()
    {
        Assert.Throws<CommandLineException>(() => CommandOptions.Parse(
            ["assert", "--connection-env", "C", "--compare-row-counts-env", "A", "--compare-row-counts-env", "B"]));
        Assert.Throws<CommandLineException>(() => CommandOptions.Parse(
            ["assert", "--connection-env", "C", "--compare-row-counts-env"]));
    }
}
