namespace Zayra.Api.Infrastructure.Audit;

/// <summary>
/// Single source for the audit-trail database guards: this class owns the SQL; the migrations
/// <c>HardenAuditTrailIntegrity</c> and <c>AddAuditChainForkGuardIndex</c> reference it (the production image
/// compiles without Migrations/, so app code must never name a migration type) and the Postgres integration
/// tests install exactly what production applies.
///
/// <para><b>Append-only.</b> Every audit table refuses UPDATE, DELETE and table wipes at the database. The
/// only exception is DELETE inside a transaction whose transaction-local flag <see cref="ErasureFlag"/>
/// names the TENANT being erased (see <see cref="ErasureFlagFor"/>) and only for rows of that tenant, used
/// solely by the two owner-confirmed tenant-erasure paths. A stolen flag erases one tenant at most.</para>
///
/// <para><b>No chain fork.</b> A unique index over (tenant, previous_hash) on <c>audit_logs</c> makes two
/// rows chaining off the same tail impossible even if the application lock is bypassed.</para>
/// </summary>
public static class AuditTrailGuardSql
{
    public const string ErasureFlag = "app.audit_erasure";

    /// <summary>Run inside the erasing transaction (is_local = true so it ends with the transaction).
    /// Parameterised: the tenant id is a bound value, never concatenated.</summary>
    public static FormattableString ErasureFlagFor(Guid tenantId) =>
        $"SELECT set_config('app.audit_erasure', {tenantId.ToString("D")}, true) AS \"Value\"";

    /// <summary>Append-only tables that use the generic trigger (payroll_audit_logs has its own function).</summary>
    public static readonly string[] AuditTables =
    [
        "audit_logs",
        "admin_audit_logs",
        "attendance_audit_logs",
        "leave_audit_logs",
        "overtime_audit_logs",
        "performance_audit_logs",
        "recruitment_audit_logs",
        "compliance_audit_logs",
        "loan_audit_logs",
        "advance_audit_logs",
        "bonus_audit_logs",
        "employee_self_service_audit_logs",
        "employee_payslip_access_logs",
        "retention_purge_audits",
    ];

    public const string ForkGuardIndexName = "ux_audit_logs_tenant_previous_hash";

    /// <summary>DELETE is allowed only for a row of the tenant named by the transaction-local flag.
    /// UPDATE is never allowed. A flag naming one tenant cannot erase another tenant's trail.</summary>
    public const string GenericFunctionSql = @"
CREATE OR REPLACE FUNCTION audit_trail_append_only() RETURNS trigger AS $$
BEGIN
    IF (TG_OP = 'DELETE'
        AND coalesce(current_setting('app.audit_erasure', true), '') <> ''
        AND current_setting('app.audit_erasure', true) = coalesce(to_jsonb(OLD)->>'tenant_id', '')) THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% is append-only: % is not permitted (row id=%)', TG_TABLE_NAME, TG_OP, to_jsonb(OLD)->>'id'
        USING ERRCODE = 'restrict_violation';
END;
$$ LANGUAGE plpgsql;";
    public const string StatementGuardFunctionSql = @"
CREATE OR REPLACE FUNCTION audit_trail_no_wipe() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% is append-only: a table wipe is not permitted', TG_TABLE_NAME
        USING ERRCODE = 'restrict_violation';
END;
$$ LANGUAGE plpgsql;";
    public const string PayrollFunctionSql = @"
CREATE OR REPLACE FUNCTION payroll_audit_logs_append_only() RETURNS trigger AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        IF coalesce(current_setting('app.audit_erasure', true), '') <> ''
           AND current_setting('app.audit_erasure', true) = OLD.tenant_id::text THEN
            RETURN OLD;
        END IF;
        RAISE EXCEPTION 'payroll_audit_logs is append-only: DELETE is not permitted (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    IF (OLD.entry_hash IS DISTINCT FROM '') THEN
        RAISE EXCEPTION 'payroll_audit_logs is append-only: sealed rows are immutable (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;";
    public const string PayrollFunctionOriginalSql = @"
CREATE OR REPLACE FUNCTION payroll_audit_logs_append_only() RETURNS trigger AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        RAISE EXCEPTION 'payroll_audit_logs is append-only: DELETE is not permitted (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    IF (OLD.entry_hash IS DISTINCT FROM '') THEN
        RAISE EXCEPTION 'payroll_audit_logs is append-only: sealed rows are immutable (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;";
    public const string CreateTriggersSql = @"
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['audit_logs', 'admin_audit_logs', 'attendance_audit_logs', 'leave_audit_logs', 'overtime_audit_logs', 'performance_audit_logs', 'recruitment_audit_logs', 'compliance_audit_logs', 'loan_audit_logs', 'advance_audit_logs', 'bonus_audit_logs', 'employee_self_service_audit_logs', 'employee_payslip_access_logs', 'retention_purge_audits'] LOOP
        IF to_regclass(t) IS NOT NULL THEN
            EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', 'trg_' || t || '_append_only', t);
            EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION audit_trail_append_only()', 'trg_' || t || '_append_only', t);
            EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', 'trg_' || t || '_no_wipe', t);
            EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION audit_trail_no_wipe()', 'trg_' || t || '_no_wipe', t);
        END IF;
    END LOOP;
END $$;";
    public const string DropTriggersSql = @"
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['audit_logs', 'admin_audit_logs', 'attendance_audit_logs', 'leave_audit_logs', 'overtime_audit_logs', 'performance_audit_logs', 'recruitment_audit_logs', 'compliance_audit_logs', 'loan_audit_logs', 'advance_audit_logs', 'bonus_audit_logs', 'employee_self_service_audit_logs', 'employee_payslip_access_logs', 'retention_purge_audits'] LOOP
        IF to_regclass(t) IS NOT NULL THEN
            EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', 'trg_' || t || '_append_only', t);
            EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', 'trg_' || t || '_no_wipe', t);
        END IF;
    END LOOP;
END $$;";
    /// <summary>
    /// Refuses (changes nothing) when audit_logs already holds chain forks and no fork guard exists yet.
    /// The message carries the counts for the deploy log. The operator follows
    /// docs/runbooks/audit-trail-fork-guard.md to accept the legacy forks explicitly (build the partial
    /// guard by hand); a re-run then passes because the guard index exists.
    /// Rows with an empty previous_hash are excluded here and from the index: every row written before the
    /// hash chain existed (migration AddMigrationImportBatchLedger defaulted the column to '') is an
    /// unsealed legacy row, and a tenant legitimately holds many of them.
    /// </summary>
    public const string ForkPrecheckSql = @"
DO $$
DECLARE dup_groups bigint; dup_rows bigint;
BEGIN
    IF to_regclass('audit_logs') IS NULL THEN RETURN; END IF;
    IF to_regclass('ux_audit_logs_tenant_previous_hash') IS NOT NULL THEN RETURN; END IF;
    SELECT count(*), coalesce(sum(n), 0) INTO dup_groups, dup_rows FROM (
        SELECT count(*) AS n FROM audit_logs
        WHERE previous_hash <> ''
        GROUP BY coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), previous_hash
        HAVING count(*) > 1) d;
    IF dup_groups > 0 THEN
        RAISE EXCEPTION 'audit_logs holds % chain fork group(s) covering % row(s); nothing was modified. Review and accept them explicitly: see docs/runbooks/audit-trail-fork-guard.md', dup_groups, dup_rows
            USING ERRCODE = 'restrict_violation';
    END IF;
END $$;";
    public const string CreateForkGuardIndexSql = "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_audit_logs_tenant_previous_hash ON audit_logs ((coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)), previous_hash) WHERE previous_hash <> ''";
    public const string DropForkGuardIndexSql = "DROP INDEX IF EXISTS ux_audit_logs_tenant_previous_hash;";
}
