using Npgsql;
using NpgsqlTypes;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.Infrastructure.Database.Outbox;

/// <summary>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the <c>trace_parent</c> column of <c>platform.outbox_events</c> and
/// <c>platform.location_outbox_events</c>, which keeps the W3C context of the trace that wrote the row
/// (<see cref="OutboxTraceContext"/> defines the value). Observability code never touches the database
/// (OBS-002 rule), so the column's parameter and reader live here, with the outbox model.
/// </summary>
public static class OutboxTraceContextColumn
{
    /// <summary>The column of both outbox tables that holds the producing trace context.</summary>
    public const string Name = "trace_parent";

    /// <summary>The SQL parameter name every producer binds the value to (<c>@trace_parent</c>).</summary>
    public const string ParameterName = "trace_parent";

    /// <summary>
    /// The transaction-local setting a caller of the operator outbox definer
    /// (<c>security.append_operator_order_outbox</c>, DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03) sets with
    /// <c>set_config(..., true)</c>, like <c>app.current_org_ids</c> and <c>app.current_user_id</c>. The definer keeps its
    /// published signature, so an application version that predates the column keeps calling it during a deploy.
    /// </summary>
    public const string TransactionSetting = "app.trace_parent";

    /// <summary>
    /// The <c>@trace_parent</c> parameter of an outbox INSERT, bound to <see cref="OutboxTraceContext.Current"/> (NULL
    /// outside a trace). The producer writes it with the rest of the row, in its own transaction, without RETURNING.
    /// </summary>
    public static NpgsqlParameter Parameter() =>
        new NpgsqlParameter<string?>(ParameterName, NpgsqlDbType.Text) { TypedValue = OutboxTraceContext.Current() };

    /// <summary>
    /// The column of a row returned by a claim function (every claim function returns the full row type of its outbox
    /// table), or null when the row has none.
    /// </summary>
    public static string? Read(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ordinal = reader.GetOrdinal(Name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
