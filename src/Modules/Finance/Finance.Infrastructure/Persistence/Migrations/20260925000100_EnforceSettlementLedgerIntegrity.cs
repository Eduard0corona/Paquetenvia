using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Finance.Infrastructure.Persistence.Migrations;

/// <summary>
/// SET-001 Slice 1 adopts the canonical AI-06 <c>finance.settlements</c> and
/// <c>finance.settlement_lines</c> tables and makes them a ledger the database itself keeps honest:
/// lines are append-only, headers follow a closed lifecycle with immutable identity and scope, every
/// committed total equals the exact sum of its lines, and one economic source is never paid by two
/// settlements that are not VOID. Following the NTF-001 and INC-001 precedent, the objects SET-001
/// introduces live in this module migration rather than in the frozen v0.6 bundle. Every step is
/// guarded, so the migration is idempotent; no table is created or dropped and no row is rewritten.
/// </summary>
[DbContext(typeof(FinanceDbContext))]
[Migration(MigrationId)]
public sealed class EnforceSettlementLedgerIntegrity : Migration
{
    public const string MigrationId = "20260925000100_EnforceSettlementLedgerIntegrity";

    public const string LedgerSql =
        """
        DO $ledger$
        DECLARE
          settlement_columns text[];
          line_columns text[];
          incoherent bigint;
          unreconciled bigint;
          duplicated bigint;
        BEGIN
          -- Canonical adoption guard: SET-001 enforces rules on the AI-06 tables, it never replaces
          -- them. Anything other than the published shape is refused before a single object exists.
          IF to_regclass('finance.settlements') IS NULL OR to_regclass('finance.settlement_lines') IS NULL THEN
            RAISE EXCEPTION 'SET-001 ledger adoption requires the canonical AI-06 settlement tables';
          END IF;
          IF to_regprocedure('platform.reject_runtime_mutation()') IS NULL THEN
            RAISE EXCEPTION 'SET-001 ledger adoption requires the canonical platform.reject_runtime_mutation() guard';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY ordinal_position)
          INTO settlement_columns
          FROM information_schema.columns
          WHERE table_schema='finance' AND table_name='settlements';
          IF settlement_columns IS DISTINCT FROM ARRAY[
            'id:uuid:NO','owner_org_id:uuid:NO','payee_type:text:NO','payee_id:uuid:NO','status:text:NO',
            'total_cents:bigint:NO','period_from:date:NO','period_to:date:NO',
            'created_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'finance.settlements columns do not match the canonical AI-06 contract';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY ordinal_position)
          INTO line_columns
          FROM information_schema.columns
          WHERE table_schema='finance' AND table_name='settlement_lines';
          IF line_columns IS DISTINCT FROM ARRAY[
            'id:uuid:NO','settlement_id:uuid:NO','owner_org_id:uuid:NO','order_id:uuid:YES',
            'line_type:text:NO','amount_cents:bigint:NO','source_reference:text:NO',
            'created_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'finance.settlement_lines columns do not match the canonical AI-06 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='finance.settlements'::regclass AND relrowsecurity AND relforcerowsecurity)
             OR NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='finance.settlement_lines'::regclass AND relrowsecurity AND relforcerowsecurity) THEN
            RAISE EXCEPTION 'canonical settlement RLS configuration is missing';
          END IF;

          IF (SELECT count(*) FROM pg_policies
              WHERE schemaname='finance'
                AND ((tablename='settlements' AND policyname='settlements_tenant')
                  OR (tablename='settlement_lines' AND policyname='settlement_lines_tenant'))) <> 2 THEN
            RAISE EXCEPTION 'canonical settlement tenant policies are missing';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint c
            WHERE c.conrelid='finance.settlement_lines'::regclass AND c.contype='u'
              AND ARRAY(
                SELECT a.attname::text
                FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ord)
                JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum
                ORDER BY k.ord) = ARRAY['settlement_id','source_reference']) THEN
            RAISE EXCEPTION 'the canonical per-settlement source_reference uniqueness is missing';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint c
            WHERE c.conrelid='finance.settlement_lines'::regclass AND c.contype='f'
              AND c.confrelid='finance.settlements'::regclass) THEN
            RAISE EXCEPTION 'the canonical settlement line parent reference is missing';
          END IF;

          -- No writer may slip a row in between the audit below and the enforcement it justifies.
          -- CREATE POLICY needs this lock anyway, so it is taken once, up front, until commit.
          LOCK TABLE finance.settlements, finance.settlement_lines IN ACCESS EXCLUSIVE MODE;

          -- Populated installations: the invariants are installed only over a ledger that already
          -- satisfies them, so after this migration they hold for every row, not just future ones.
          -- Both tables keep FORCE ROW LEVEL SECURITY and paqueteria_migrator is NOBYPASSRLS, so,
          -- following the INC-001 precedent, the owner grants itself a read-only policy scoped to
          -- this transaction and drops it again before anything is enforced. No row is changed:
          -- a ledger that does not reconcile is refused, never repaired.
          DROP POLICY IF EXISTS settlements_set001_adoption_audit ON finance.settlements;
          DROP POLICY IF EXISTS settlement_lines_set001_adoption_audit ON finance.settlement_lines;
          CREATE POLICY settlements_set001_adoption_audit ON finance.settlements
            AS PERMISSIVE FOR SELECT TO paqueteria_migrator USING (true);
          CREATE POLICY settlement_lines_set001_adoption_audit ON finance.settlement_lines
            AS PERMISSIVE FOR SELECT TO paqueteria_migrator USING (true);

          SELECT count(*) INTO incoherent
          FROM finance.settlement_lines l
          JOIN finance.settlements s ON s.id=l.settlement_id
          WHERE l.owner_org_id<>s.owner_org_id;

          SELECT count(*) INTO unreconciled
          FROM finance.settlements s
          WHERE s.total_cents::numeric <> COALESCE(
            (SELECT sum(l.amount_cents) FROM finance.settlement_lines l WHERE l.settlement_id=s.id), 0);

          SELECT count(*) INTO duplicated
          FROM (
            SELECT 1
            FROM finance.settlement_lines l
            JOIN finance.settlements s ON s.id=l.settlement_id
            WHERE l.line_type<>'ADJUSTMENT' AND s.status<>'VOID'
            GROUP BY l.owner_org_id,l.source_reference
            HAVING count(DISTINCT l.settlement_id) > 1) sources;

          DROP POLICY settlements_set001_adoption_audit ON finance.settlements;
          DROP POLICY settlement_lines_set001_adoption_audit ON finance.settlement_lines;

          IF incoherent <> 0 THEN
            RAISE EXCEPTION 'SET-001 ledger adoption refused: % settlement lines belong to another tenant than their settlement', incoherent
              USING ERRCODE='23514';
          END IF;
          IF unreconciled <> 0 THEN
            RAISE EXCEPTION 'SET-001 ledger adoption refused: % settlements do not reconcile with their lines', unreconciled
              USING ERRCODE='23514';
          END IF;
          IF duplicated <> 0 THEN
            RAISE EXCEPTION 'SET-001 ledger adoption refused: % economic sources are settled by more than one settlement that is not VOID', duplicated
              USING ERRCODE='23505';
          END IF;

          CREATE INDEX IF NOT EXISTS settlements_owner_payee_period_idx
            ON finance.settlements(owner_org_id,payee_type,payee_id,period_from);
          CREATE INDEX IF NOT EXISTS settlement_lines_owner_source_idx
            ON finance.settlement_lines(owner_org_id,source_reference);

          -- Header lifecycle. A settlement is born empty, its identity and scope never change, it
          -- only moves forward, and its total is frozen once it leaves DRAFT/CALCULATED. Deletion
          -- follows the platform append-only convention: only the migration lane may remove a row.
          CREATE OR REPLACE FUNCTION finance.guard_settlement_mutation() RETURNS trigger
          LANGUAGE plpgsql AS $guard$
          BEGIN
            IF TG_OP='INSERT' THEN
              IF NEW.status IS DISTINCT FROM 'DRAFT' OR NEW.total_cents IS DISTINCT FROM 0 THEN
                RAISE EXCEPTION 'a settlement must be created as DRAFT with total_cents 0' USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END IF;

            IF TG_OP='DELETE' THEN
              IF current_user <> 'paqueteria_migrator' THEN
                RAISE EXCEPTION 'finance.settlements is append-only' USING ERRCODE='42501';
              END IF;
              RETURN OLD;
            END IF;

            IF NEW.id IS DISTINCT FROM OLD.id
               OR NEW.owner_org_id IS DISTINCT FROM OLD.owner_org_id
               OR NEW.payee_type IS DISTINCT FROM OLD.payee_type
               OR NEW.payee_id IS DISTINCT FROM OLD.payee_id
               OR NEW.period_from IS DISTINCT FROM OLD.period_from
               OR NEW.period_to IS DISTINCT FROM OLD.period_to
               OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
              RAISE EXCEPTION 'settlement identity and scope are immutable' USING ERRCODE='23514';
            END IF;

            IF NOT ((OLD.status || '>' || NEW.status) = ANY (ARRAY[
              'DRAFT>DRAFT','DRAFT>CALCULATED','DRAFT>VOID',
              'CALCULATED>CALCULATED','CALCULATED>APPROVED','CALCULATED>VOID',
              'APPROVED>PAID','APPROVED>VOID',
              'PAID>PAID','VOID>VOID'])) THEN
              RAISE EXCEPTION 'settlement status transition % -> % is not allowed', OLD.status, NEW.status
                USING ERRCODE='23514';
            END IF;

            IF NEW.total_cents IS DISTINCT FROM OLD.total_cents
               AND NOT (OLD.status IN ('DRAFT','CALCULATED') AND NEW.status IN ('DRAFT','CALCULATED')) THEN
              RAISE EXCEPTION 'settlement total is frozen outside DRAFT and CALCULATED' USING ERRCODE='23514';
            END IF;

            RETURN NEW;
          END $guard$;

          -- Line admission. The database validates integrity only; amounts are derived elsewhere.
          -- The parent row is locked against a concurrent lifecycle change until this transaction
          -- ends, so a line can never be admitted under a status that is being left.
          CREATE OR REPLACE FUNCTION finance.guard_settlement_line_insert() RETURNS trigger
          LANGUAGE plpgsql AS $guard$
          DECLARE
            parent_owner uuid;
            parent_status text;
          BEGIN
            SELECT s.owner_org_id,s.status INTO parent_owner,parent_status
            FROM finance.settlements s
            WHERE s.id=NEW.settlement_id
            FOR NO KEY UPDATE;
            IF NOT FOUND THEN
              RAISE EXCEPTION 'a settlement line must belong to an existing settlement of the same tenant'
                USING ERRCODE='23503';
            END IF;
            IF NEW.owner_org_id IS DISTINCT FROM parent_owner THEN
              RAISE EXCEPTION 'a settlement line must have the owner organization of its settlement'
                USING ERRCODE='23514';
            END IF;
            IF btrim(NEW.source_reference)='' THEN
              RAISE EXCEPTION 'a settlement line must name its source' USING ERRCODE='23514';
            END IF;

            IF NEW.line_type IN ('DELIVERY','RETURN') THEN
              IF parent_status<>'DRAFT' THEN
                RAISE EXCEPTION '% lines are only admitted while the settlement is DRAFT', NEW.line_type
                  USING ERRCODE='23514';
              END IF;
              IF NEW.order_id IS NULL THEN
                RAISE EXCEPTION '% lines must reference their order', NEW.line_type USING ERRCODE='23514';
              END IF;
              IF NEW.amount_cents < 0 THEN
                RAISE EXCEPTION '% lines cannot be negative', NEW.line_type USING ERRCODE='23514';
              END IF;
            ELSIF NEW.line_type='ADJUSTMENT' THEN
              IF parent_status NOT IN ('DRAFT','CALCULATED') THEN
                RAISE EXCEPTION 'ADJUSTMENT lines are only admitted while the settlement is DRAFT or CALCULATED'
                  USING ERRCODE='23514';
              END IF;
              IF NEW.amount_cents=0 THEN
                RAISE EXCEPTION 'ADJUSTMENT lines must move the total' USING ERRCODE='23514';
              END IF;
              IF NEW.order_id IS NOT NULL THEN
                RAISE EXCEPTION 'ADJUSTMENT lines cannot reference an order' USING ERRCODE='23514';
              END IF;
              -- An adjustment names its own audit entry, not an economic source.
              RETURN NEW;
            ELSE
              -- ROUTE_BASE, BONUS, WAITING and COD stay in the AI-06 vocabulary but have no SET-001
              -- source yet, so the ledger refuses them rather than inventing their semantics.
              RAISE EXCEPTION 'settlement line type % is reserved and not admitted by the SET-001 ledger', NEW.line_type
                USING ERRCODE='23514';
            END IF;

            -- One economic source is paid at most once. The claim is serialized per tenant and
            -- source, and the lookup below runs on a fresh READ COMMITTED snapshot after the lock is
            -- granted, so it sees every settlement committed before it. A REPEATABLE READ or
            -- SERIALIZABLE snapshot could predate that commit, so those levels are refused here.
            IF current_setting('transaction_isolation') NOT IN ('read committed','read uncommitted') THEN
              RAISE EXCEPTION 'settlement economic lines must be written under READ COMMITTED'
                USING ERRCODE='0A000';
            END IF;
            PERFORM pg_advisory_xact_lock(hashtextextended(
              'SET-001:SETTLEMENT_SOURCE:' || NEW.owner_org_id::text || ':' || NEW.source_reference, 0));
            IF EXISTS (
              SELECT 1
              FROM finance.settlement_lines l
              JOIN finance.settlements s ON s.id=l.settlement_id
              WHERE l.owner_org_id=NEW.owner_org_id
                AND l.source_reference=NEW.source_reference
                AND l.line_type<>'ADJUSTMENT'
                AND l.settlement_id<>NEW.settlement_id
                AND s.status<>'VOID') THEN
              RAISE EXCEPTION 'economic source % is already settled by a settlement that is not VOID', NEW.source_reference
                USING ERRCODE='23505';
            END IF;

            RETURN NEW;
          END $guard$;

          -- Exact reconciliation, evaluated at commit for every settlement a transaction touched.
          -- The current row is re-read because an earlier event of the same transaction carries a
          -- stale total; the line sum is numeric, so it can never wrap around bigint.
          CREATE OR REPLACE FUNCTION finance.require_settlement_reconciliation() RETURNS trigger
          LANGUAGE plpgsql AS $reconcile$
          DECLARE
            affected uuid[];
            settlement uuid;
            settlement_total bigint;
            line_total numeric;
          BEGIN
            IF TG_TABLE_NAME='settlements' THEN
              affected := ARRAY[NEW.id];
            ELSIF TG_OP='INSERT' THEN
              affected := ARRAY[NEW.settlement_id];
            ELSIF TG_OP='DELETE' THEN
              affected := ARRAY[OLD.settlement_id];
            ELSE
              affected := ARRAY[OLD.settlement_id,NEW.settlement_id];
            END IF;

            FOREACH settlement IN ARRAY affected LOOP
              SELECT s.total_cents INTO settlement_total FROM finance.settlements s WHERE s.id=settlement;
              -- Only the migration lane can remove a settlement, and then its lines go with it.
              CONTINUE WHEN NOT FOUND;
              SELECT COALESCE(sum(l.amount_cents),0) INTO line_total
              FROM finance.settlement_lines l WHERE l.settlement_id=settlement;
              IF settlement_total::numeric <> line_total THEN
                RAISE EXCEPTION 'settlement % total_cents % does not equal the sum % of its lines',
                  settlement, settlement_total, line_total USING ERRCODE='23514';
              END IF;
            END LOOP;

            RETURN NULL;
          END $reconcile$;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='finance.settlements'::regclass
              AND tgname='settlements_guard_mutation' AND NOT tgisinternal) THEN
            CREATE TRIGGER settlements_guard_mutation
              BEFORE INSERT OR UPDATE OR DELETE ON finance.settlements
              FOR EACH ROW EXECUTE FUNCTION finance.guard_settlement_mutation();
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='finance.settlements'::regclass
              AND tgname='settlements_reconciled' AND NOT tgisinternal) THEN
            CREATE CONSTRAINT TRIGGER settlements_reconciled
              AFTER INSERT OR UPDATE ON finance.settlements
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION finance.require_settlement_reconciliation();
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='finance.settlement_lines'::regclass
              AND tgname='settlement_lines_guard_insert' AND NOT tgisinternal) THEN
            CREATE TRIGGER settlement_lines_guard_insert
              BEFORE INSERT ON finance.settlement_lines
              FOR EACH ROW EXECUTE FUNCTION finance.guard_settlement_line_insert();
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='finance.settlement_lines'::regclass
              AND tgname='settlement_lines_append_only' AND NOT tgisinternal) THEN
            CREATE TRIGGER settlement_lines_append_only
              BEFORE UPDATE OR DELETE ON finance.settlement_lines
              FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='finance.settlement_lines'::regclass
              AND tgname='settlement_lines_reconciled' AND NOT tgisinternal) THEN
            CREATE CONSTRAINT TRIGGER settlement_lines_reconciled
              AFTER INSERT OR UPDATE OR DELETE ON finance.settlement_lines
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION finance.require_settlement_reconciliation();
          END IF;

          -- Verification: exactly the five SET-001 triggers, enabled, on the intended events, bound
          -- to the intended functions, with both reconciliation triggers deferred to commit. A
          -- pre-existing object of the same name but a different shape is refused, not trusted.
          -- tgtype bits: ROW=1, BEFORE=2, INSERT=4, DELETE=8, UPDATE=16.
          IF (SELECT count(*) FROM pg_trigger t
              WHERE t.tgrelid IN ('finance.settlements'::regclass,'finance.settlement_lines'::regclass)
                AND NOT t.tgisinternal) <> 5
             OR (SELECT count(*) FROM pg_trigger t
              WHERE NOT t.tgisinternal AND t.tgenabled='O' AND t.tgqual IS NULL AND t.tgnargs=0
                AND cardinality(t.tgattr::int2[])=0
                AND (
                  (t.tgrelid='finance.settlements'::regclass AND t.tgname='settlements_guard_mutation'
                    AND t.tgtype=31 AND NOT t.tgdeferrable
                    AND t.tgfoid='finance.guard_settlement_mutation()'::regprocedure)
                  OR (t.tgrelid='finance.settlements'::regclass AND t.tgname='settlements_reconciled'
                    AND t.tgtype=21 AND t.tgdeferrable AND t.tginitdeferred
                    AND t.tgfoid='finance.require_settlement_reconciliation()'::regprocedure)
                  OR (t.tgrelid='finance.settlement_lines'::regclass AND t.tgname='settlement_lines_guard_insert'
                    AND t.tgtype=7 AND NOT t.tgdeferrable
                    AND t.tgfoid='finance.guard_settlement_line_insert()'::regprocedure)
                  OR (t.tgrelid='finance.settlement_lines'::regclass AND t.tgname='settlement_lines_append_only'
                    AND t.tgtype=27 AND NOT t.tgdeferrable
                    AND t.tgfoid='platform.reject_runtime_mutation()'::regprocedure)
                  OR (t.tgrelid='finance.settlement_lines'::regclass AND t.tgname='settlement_lines_reconciled'
                    AND t.tgtype=29 AND t.tgdeferrable AND t.tginitdeferred
                    AND t.tgfoid='finance.require_settlement_reconciliation()'::regprocedure))) <> 5 THEN
            RAISE EXCEPTION 'SET-001 settlement ledger enforcement is incomplete';
          END IF;

          IF (SELECT count(*)
              FROM pg_index i
              JOIN pg_class ix ON ix.oid=i.indexrelid
              JOIN pg_am am ON am.oid=ix.relam
              WHERE am.amname='btree' AND i.indisvalid AND NOT i.indisunique
                AND i.indpred IS NULL AND i.indexprs IS NULL
                AND (
                  (i.indexrelid=to_regclass('finance.settlements_owner_payee_period_idx')
                    AND i.indrelid='finance.settlements'::regclass
                    AND ARRAY(
                      SELECT a.attname::text
                      FROM unnest(i.indkey::int2[]) WITH ORDINALITY k(attnum,ord)
                      JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                      ORDER BY k.ord) = ARRAY['owner_org_id','payee_type','payee_id','period_from'])
                  OR (i.indexrelid=to_regclass('finance.settlement_lines_owner_source_idx')
                    AND i.indrelid='finance.settlement_lines'::regclass
                    AND ARRAY(
                      SELECT a.attname::text
                      FROM unnest(i.indkey::int2[]) WITH ORDINALITY k(attnum,ord)
                      JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                      ORDER BY k.ord) = ARRAY['owner_org_id','source_reference']))) <> 2 THEN
            RAISE EXCEPTION 'SET-001 settlement ledger indexes do not match the contract';
          END IF;

          -- The audit policies must not outlive the migration: runtime visibility is exactly the
          -- canonical tenant policies, no more.
          IF (SELECT count(*) FROM pg_policies
              WHERE schemaname='finance' AND tablename IN ('settlements','settlement_lines')) <> 2 THEN
            RAISE EXCEPTION 'settlement policies are not exactly the canonical tenant policies';
          END IF;
        END
        $ledger$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(LedgerSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // SET-001 rollback blocked: settlement lines are an append-only financial ledger and the
        // guards are what make its totals trustworthy. Canonical AI-06 objects are never dropped.
    }
}
