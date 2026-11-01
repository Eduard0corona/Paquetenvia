using System.Data.Common;

namespace Paqueteria.Application.Diagnostics;

/// <summary>
/// REL-001-INFRASTRUCTURE-503-2026-11-01 (owner decision Z3, AI-05 <c>ServiceUnavailable</c>): the single log line an
/// endpoint writes when an infrastructure failure ends a request with 503. The line names only the AI-05 operation id,
/// the type name of the innermost exception and the PostgreSQL SQLSTATE when the failure carries one. It never carries
/// an exception message, a <c>Detail</c> or <c>Where</c> field, a command parameter or a row value (a unique-violation
/// detail can hold row values), nor a tenant, actor, user or resource id (AI-01 invariant 5,
/// OBS-003-TENANT-PSEUDONYM-2026-10-12). The exception object itself is never handed to the logger.
/// </summary>
public static class InfrastructureFailureLog
{
    /// <summary>Outside the OBS-002 block (4600-4699): no alert reads it; the 503 already counts as API 5xx.</summary>
    public const int EventId = 4701;

    public const string EventName = "InfrastructureFailureUnavailable";

    public const string Message =
        "Operation {Operation} answered 503 after an infrastructure failure ({ErrorClass}, SQLSTATE {SqlState}).";

    /// <summary>The SQLSTATE written when no exception of the chain carries a well-formed one.</summary>
    public const string NoSqlState = "none";

    /// <summary>The inner exceptions read at most; a longer chain is described by the deepest one read.</summary>
    public const int MaximumDepth = 16;

    /// <summary>
    /// Describes <paramref name="exception"/> without reading its text: the type name of the innermost exception of
    /// the <see cref="Exception.InnerException"/> chain, and the first well-formed SQLSTATE (five digits or uppercase
    /// ASCII letters) a <see cref="DbException"/> of that chain reports, or <see cref="NoSqlState"/>.
    /// </summary>
    public static InfrastructureFailure Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var innermost = exception;
        string? sqlState = null;
        var current = exception;
        for (var depth = 0; current is not null && depth < MaximumDepth; depth++)
        {
            innermost = current;
            if (sqlState is null && current is DbException { SqlState: { } state } && IsSqlState(state))
            {
                sqlState = state;
            }

            current = current.InnerException;
        }

        return new InfrastructureFailure(innermost.GetType().Name, sqlState ?? NoSqlState);
    }

    /// <summary>A PostgreSQL SQLSTATE: exactly five characters, each a digit or an uppercase ASCII letter.</summary>
    public static bool IsSqlState(string? value) =>
        value is { Length: 5 } && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'Z');
}

/// <summary>The redacted description of an infrastructure failure: a type name and a SQLSTATE, nothing else.</summary>
public readonly record struct InfrastructureFailure(string ErrorClass, string SqlState);
