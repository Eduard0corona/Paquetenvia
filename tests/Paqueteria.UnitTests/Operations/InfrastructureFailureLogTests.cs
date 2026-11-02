using System.Data.Common;
using Npgsql;
using Paqueteria.Application.Diagnostics;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// REL-001-INFRASTRUCTURE-503-2026-11-01 (owner decision Z3): the 503 log line describes an infrastructure failure by
/// the innermost exception type and a well-formed SQLSTATE only; no message, detail, where, parameter or row value can
/// reach it, whatever the exception chain carries.
/// </summary>
public sealed class InfrastructureFailureLogTests
{
    private const string Secret = "ana.sintetica@example.test 6671234567 d2000000-0000-0000-0000-0000000000aa";

    [Fact]
    public void A_postgres_unique_violation_gives_its_type_and_sqlstate_and_nothing_of_its_text()
    {
        // messageText, severity, invariantSeverity, sqlState, detail, hint, position, internalPosition, internalQuery,
        // where, schemaName, tableName, columnName, dataTypeName, constraintName, file, line, routine.
        var postgres = new PostgresException(
            $"duplicate key value violates unique constraint ({Secret})",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            $"Key (email)=({Secret}) already exists.",
            $"hint {Secret}",
            0,
            0,
            $"internal query {Secret}",
            $"SQL statement with {Secret}",
            "identity",
            "users",
            "email",
            "text",
            "users_email_key",
            "nbtinsert.c",
            "666",
            "_bt_check_unique");
        var wrapped = new InvalidOperationException($"wrapper {Secret}", postgres);

        var failure = InfrastructureFailureLog.Describe(wrapped);

        Assert.Equal(new InfrastructureFailure("PostgresException", "23505"), failure);
        AssertCarriesNothingOf(failure, Secret, "users_email_key", "duplicate key");
    }

    [Fact]
    public void A_transport_failure_gives_the_innermost_type_and_no_sqlstate()
    {
        var failure = InfrastructureFailureLog.Describe(
            new InvalidOperationException(
                $"routing store {Secret}",
                new NpgsqlException($"Exception while reading from stream {Secret}", new TimeoutException(Secret))));

        Assert.Equal(new InfrastructureFailure(nameof(TimeoutException), InfrastructureFailureLog.NoSqlState), failure);
        AssertCarriesNothingOf(failure, Secret);
    }

    [Fact]
    public void A_failure_without_an_inner_exception_is_described_by_its_own_type()
    {
        var failure = InfrastructureFailureLog.Describe(new InvalidOperationException($"inconsistent {Secret}"));

        Assert.Equal(
            new InfrastructureFailure(nameof(InvalidOperationException), InfrastructureFailureLog.NoSqlState),
            failure);
    }

    [Fact]
    public void The_first_well_formed_sqlstate_of_the_chain_wins_and_a_malformed_one_is_never_written()
    {
        var chain = new FakeDbException(
            Secret,
            new FakeDbException(
                "40001",
                new FakeDbException("57P01", inner: null)));

        Assert.Equal(new InfrastructureFailure(nameof(FakeDbException), "40001"), InfrastructureFailureLog.Describe(chain));

        foreach (var malformed in new[] { Secret, "2350", "235055", "23505 ", "4000a", "57p01", "", "23-05" })
        {
            var failure = InfrastructureFailureLog.Describe(new FakeDbException(malformed, inner: null));
            Assert.Equal(InfrastructureFailureLog.NoSqlState, failure.SqlState);
        }
    }

    [Fact]
    public void A_chain_longer_than_the_bound_is_described_by_the_deepest_exception_read()
    {
        Exception chain = new TimeoutException("deepest");
        for (var index = 0; index < InfrastructureFailureLog.MaximumDepth * 4; index++)
        {
            chain = index == InfrastructureFailureLog.MaximumDepth * 4 - 1
                ? new FakeDbException("08006", chain)
                : new InvalidOperationException(Secret, chain);
        }

        var failure = InfrastructureFailureLog.Describe(chain);

        Assert.Equal(new InfrastructureFailure(nameof(InvalidOperationException), "08006"), failure);
    }

    [Theory]
    [InlineData("23505", true)]
    [InlineData("57P01", true)]
    [InlineData("08006", true)]
    [InlineData("XX000", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("2350", false)]
    [InlineData("235050", false)]
    [InlineData("57p01", false)]
    [InlineData("23 05", false)]
    [InlineData("２３５０５", false)]
    public void Only_five_digits_or_uppercase_ascii_letters_are_a_sqlstate(string? value, bool expected) =>
        Assert.Equal(expected, InfrastructureFailureLog.IsSqlState(value));

    [Fact]
    public void A_null_exception_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => InfrastructureFailureLog.Describe(null!));

    [Fact]
    public void The_template_names_only_the_operation_the_error_class_and_the_sqlstate()
    {
        var placeholders = System.Text.RegularExpressions.Regex
            .Matches(InfrastructureFailureLog.Message, @"\{(?<name>[^{}]+)\}")
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        Assert.Equal(["Operation", "ErrorClass", "SqlState"], placeholders);
        Assert.InRange(InfrastructureFailureLog.EventId, 4700, 4799);
    }

    private static void AssertCarriesNothingOf(InfrastructureFailure failure, params string[] values)
    {
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, failure.ErrorClass, StringComparison.Ordinal);
            Assert.DoesNotContain(value, failure.SqlState, StringComparison.Ordinal);
            Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
        }
    }

    private sealed class FakeDbException(string sqlState, Exception? inner) : DbException(sqlState, inner)
    {
        public override string SqlState { get; } = sqlState;
    }
}
