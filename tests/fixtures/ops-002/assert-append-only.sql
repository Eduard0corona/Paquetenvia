\set ON_ERROR_STOP on

\if :{?ops002_append_only_table}
\else
\set ops002_append_only_table all
\endif

CREATE TEMP TABLE ops002_append_only_evidence (
  table_filter text NOT NULL,
  tables_expected integer NOT NULL,
  permission_checks_verified integer NOT NULL DEFAULT 0,
  triggers_verified integer NOT NULL DEFAULT 0,
  update_guards_verified integer NOT NULL DEFAULT 0,
  delete_guards_verified integer NOT NULL DEFAULT 0,
  trigger_failures_verified integer NOT NULL DEFAULT 0,
  permission_failures integer NOT NULL DEFAULT 0,
  rows_intact_verified integer NOT NULL DEFAULT 0
) ON COMMIT PRESERVE ROWS;

INSERT INTO ops002_append_only_evidence(table_filter, tables_expected)
SELECT
  :'ops002_append_only_table',
  CASE WHEN :'ops002_append_only_table' = 'all' THEN 4 ELSE 1 END;

DO $ops002$
DECLARE
  guard record;
  original_row jsonb;
  current_row jsonb;
  matching_rows integer;
  mutation_rejected boolean;
  selected_tables integer := 0;
BEGIN
  IF current_setting('session_replication_role') <> 'origin' THEN
    RAISE EXCEPTION 'OPS002_APPEND_ONLY_SESSION_REPLICATION_ROLE_INVALID';
  END IF;

  FOR guard IN
    SELECT *
    FROM (VALUES
      (
        'orders.order_events',
        'order_events_append_only',
        'id=''10000000-0000-4000-8000-000000000001''::uuid',
        'event_type=event_type'
      ),
      (
        'orders.order_acceptances',
        'order_acceptances_append_only',
        'id=''10000000-0000-4000-8000-000000000011''::uuid',
        'terms_version=terms_version'
      ),
      (
        'custody.proofs',
        'proofs_append_only',
        'id=''10000000-0000-4000-8000-000000000027''::uuid',
        'proof_type=proof_type'
      ),
      (
        'platform.audit_logs',
        'audit_logs_append_only',
        'id=''10000000-0000-4000-8000-000000000029''::uuid',
        'action=action'
      )
    ) AS expected(table_name, trigger_name, row_predicate, update_expression)
    WHERE (SELECT table_filter FROM ops002_append_only_evidence) = 'all'
       OR (SELECT table_filter FROM ops002_append_only_evidence) = expected.table_name
    ORDER BY expected.table_name
  LOOP
    selected_tables := selected_tables + 1;

    IF NOT has_table_privilege(current_user, guard.table_name, 'UPDATE') OR
       NOT has_table_privilege(current_user, guard.table_name, 'DELETE') THEN
      UPDATE ops002_append_only_evidence
      SET permission_failures = permission_failures + 1;
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_TEST_ACTOR_PERMISSION_INVALID: %', guard.table_name;
    END IF;
    UPDATE ops002_append_only_evidence
    SET permission_checks_verified = permission_checks_verified + 1;

    IF NOT EXISTS (
      SELECT 1
      FROM pg_trigger trigger_definition
      JOIN pg_proc trigger_function
        ON trigger_function.oid = trigger_definition.tgfoid
      JOIN pg_namespace function_schema
        ON function_schema.oid = trigger_function.pronamespace
      WHERE trigger_definition.tgrelid = guard.table_name::regclass
        AND trigger_definition.tgname = guard.trigger_name
        AND NOT trigger_definition.tgisinternal
    ) THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_TRIGGER_MISSING: %', guard.table_name;
    END IF;

    IF EXISTS (
      SELECT 1
      FROM pg_trigger trigger_definition
      WHERE trigger_definition.tgrelid = guard.table_name::regclass
        AND trigger_definition.tgname = guard.trigger_name
        AND trigger_definition.tgenabled <> 'O'
    ) THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_TRIGGER_DISABLED: %', guard.table_name;
    END IF;

    IF NOT EXISTS (
      SELECT 1
      FROM pg_trigger trigger_definition
      JOIN pg_proc trigger_function
        ON trigger_function.oid = trigger_definition.tgfoid
      JOIN pg_namespace function_schema
        ON function_schema.oid = trigger_function.pronamespace
      WHERE trigger_definition.tgrelid = guard.table_name::regclass
        AND trigger_definition.tgname = guard.trigger_name
        AND trigger_definition.tgenabled = 'O'
        AND trigger_definition.tgtype = 27
        AND function_schema.nspname = 'platform'
        AND trigger_function.proname = 'reject_runtime_mutation'
        AND trigger_function.pronargs = 0
    ) THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_TRIGGER_CONTRACT_INVALID: %', guard.table_name;
    END IF;
    UPDATE ops002_append_only_evidence
    SET triggers_verified = triggers_verified + 1;

    EXECUTE format('SELECT count(*) FROM %s WHERE %s', guard.table_name, guard.row_predicate)
      INTO matching_rows;
    IF matching_rows <> 1 THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_KNOWN_ROW_INVALID: %', guard.table_name;
    END IF;
    EXECUTE format(
      'SELECT to_jsonb(candidate) FROM %s candidate WHERE %s',
      guard.table_name,
      guard.row_predicate
    ) INTO STRICT original_row;

    mutation_rejected := false;
    BEGIN
      EXECUTE format(
        'UPDATE %s SET %s WHERE %s',
        guard.table_name,
        guard.update_expression,
        guard.row_predicate
      );
    EXCEPTION
      WHEN SQLSTATE '42501' THEN
        IF SQLERRM <> guard.table_name || ' is append-only' THEN
          UPDATE ops002_append_only_evidence
          SET permission_failures = permission_failures + 1;
          RAISE EXCEPTION
            'OPS002_APPEND_ONLY_TEST_ACTOR_PERMISSION_INVALID: % [%]',
            SQLERRM,
            SQLSTATE;
        END IF;
        mutation_rejected := true;
      WHEN OTHERS THEN
        RAISE EXCEPTION
          'OPS002_APPEND_ONLY_UNRELATED_UPDATE_ERROR: % [%]',
          SQLERRM,
          SQLSTATE;
    END;
    IF NOT mutation_rejected THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_UPDATE_WAS_ACCEPTED: %', guard.table_name;
    END IF;
    UPDATE ops002_append_only_evidence
    SET update_guards_verified = update_guards_verified + 1,
        trigger_failures_verified = trigger_failures_verified + 1;

    EXECUTE format(
      'SELECT to_jsonb(candidate) FROM %s candidate WHERE %s',
      guard.table_name,
      guard.row_predicate
    ) INTO STRICT current_row;
    IF current_row IS DISTINCT FROM original_row THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_ROW_CHANGED_AFTER_UPDATE: %', guard.table_name;
    END IF;
    UPDATE ops002_append_only_evidence
    SET rows_intact_verified = rows_intact_verified + 1;

    mutation_rejected := false;
    BEGIN
      EXECUTE format(
        'DELETE FROM %s WHERE %s',
        guard.table_name,
        guard.row_predicate
      );
    EXCEPTION
      WHEN SQLSTATE '42501' THEN
        IF SQLERRM <> guard.table_name || ' is append-only' THEN
          UPDATE ops002_append_only_evidence
          SET permission_failures = permission_failures + 1;
          RAISE EXCEPTION
            'OPS002_APPEND_ONLY_TEST_ACTOR_PERMISSION_INVALID: % [%]',
            SQLERRM,
            SQLSTATE;
        END IF;
        mutation_rejected := true;
      WHEN OTHERS THEN
        RAISE EXCEPTION
          'OPS002_APPEND_ONLY_UNRELATED_DELETE_ERROR: % [%]',
          SQLERRM,
          SQLSTATE;
    END;
    IF NOT mutation_rejected THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_DELETE_WAS_ACCEPTED: %', guard.table_name;
    END IF;
    UPDATE ops002_append_only_evidence
    SET delete_guards_verified = delete_guards_verified + 1,
        trigger_failures_verified = trigger_failures_verified + 1;

    EXECUTE format(
      'SELECT to_jsonb(candidate) FROM %s candidate WHERE %s',
      guard.table_name,
      guard.row_predicate
    ) INTO STRICT current_row;
    IF current_row IS DISTINCT FROM original_row THEN
      RAISE EXCEPTION 'OPS002_APPEND_ONLY_ROW_CHANGED_AFTER_DELETE: %', guard.table_name;
    END IF;
    UPDATE ops002_append_only_evidence
    SET rows_intact_verified = rows_intact_verified + 1;
  END LOOP;

  IF selected_tables <> (SELECT tables_expected FROM ops002_append_only_evidence) THEN
    RAISE EXCEPTION 'OPS002_APPEND_ONLY_TABLE_FILTER_INVALID';
  END IF;
END
$ops002$;

SELECT format(
  'OPS002_APPEND_ONLY_RESULT|tables_expected=%s|permission_checks_verified=%s|triggers_verified=%s|update_guards_verified=%s|delete_guards_verified=%s|trigger_failures_verified=%s|permission_failures=%s|rows_intact_verified=%s|sqlstate=42501|message_contract=qualified_table_is_append_only',
  tables_expected,
  permission_checks_verified,
  triggers_verified,
  update_guards_verified,
  delete_guards_verified,
  trigger_failures_verified,
  permission_failures,
  rows_intact_verified
)
FROM ops002_append_only_evidence;
