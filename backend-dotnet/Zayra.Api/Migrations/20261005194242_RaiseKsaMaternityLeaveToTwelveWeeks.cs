using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Raises Saudi maternity leave that was configured at the pre-2025 figure to the 12 weeks the
    /// Labour Law now grants.
    ///
    /// <para><b>The defect.</b> The setup assistant drafted every tenant's maternity leave at 70 days
    /// (Art. 151's old "10 weeks"). Royal Decree M/44, in force 19 February 2025, made it "a fully paid
    /// maternity leave of (twelve) weeks" — 84 days. A Saudi tenant that applied the draft holds a
    /// 70-day policy entitlement, a 70-day per-request cap and a 70-day cap on the leave type, and a
    /// 12-week request is refused by all three.</para>
    ///
    /// <para><b>What it touches — and nothing else.</b></para>
    /// <list type="number">
    /// <item><c>leave_policies</c>: <c>annual_entitlement_days</c> below 84 → 84, and
    /// <c>maximum_days_per_request</c> in (0, 84) → 84 (0 means "no cap" and is left alone), on
    /// non-archived policies for a maternity leave type that reach Saudi employees: the policy's country
    /// is SA/SAU, or it has no country and its company is Saudi, or it has neither and the tenant has a
    /// Saudi company.</item>
    /// <item><c>leave_types</c>: <c>max_consecutive_days</c> in (0, 84) → 84 for a maternity leave
    /// type in a tenant with a Saudi company, or with a SA/SAU policy for that type. A leave type has no
    /// country; raising a ceiling never refuses anything, and a non-Saudi policy's own cap still
    /// governs non-Saudi employees.</item>
    /// <item><c>leave_audit_logs</c>: one <c>StatutoryFloorRaised</c> row per policy or leave type
    /// changed, carrying the old and new figures, so the change can be traced and, if ever needed,
    /// reversed row by row.</item>
    /// </list>
    ///
    /// <para><b>A "maternity leave type"</b> is the predicate <c>KsaStatutorySpecialLeave.Classify</c>
    /// applies in C#: code MAT or MATERNITY, or a name containing MATERNITY, or category Maternity — but
    /// not a type whose name says UNPAID or EXTENSION (Art. 151's own unpaid extension) and not an
    /// iddah type.</para>
    ///
    /// <para><b>Raise only, and idempotent.</b> Every predicate is "below 84", so a value at or above
    /// 84 — including a company that already grants more — is never touched, nothing is ever lowered, and
    /// a second run finds no rows and writes no audit. Leave balances and leave requests are NOT touched:
    /// no code path grants a maternity balance (it is a "Yearly" policy, which the accrual sweep does not
    /// visit), so any maternity balance on file was keyed in by a person and is theirs to review. The
    /// migration reports how many such rows sit below 84 so they can be found.</para>
    ///
    /// <para><b>Down is a no-op.</b> Lowering a statutory entitlement is not a rollback this migration
    /// will perform. The audit rows hold every old value if a human decides otherwise.</para>
    /// </summary>
    public partial class RaiseKsaMaternityLeaveToTwelveWeeks : Migration
    {
        /// <summary>KSA Art. 151 as amended: 12 weeks.</summary>
        internal const int FloorDays = 84;

        internal const string AuditActor = "system: migration RaiseKsaMaternityLeaveToTwelveWeeks";

        private const string Reason =
            "Saudi Labour Law Art. 151 as amended by Royal Decree M/44 (in force 2025-02-19): maternity leave is "
            + "12 weeks (84 days) fully paid. Raised from below that floor; values at or above it were not changed.";

        /// <summary>Maternity leave types, as <c>KsaStatutorySpecialLeave.Classify</c> defines them.</summary>
        private const string MaternityTypes = @"
    SELECT lt.id, lt.tenant_id
      FROM leave_types lt
     WHERE (upper(btrim(coalesce(lt.code, ''))) IN ('MAT', 'MATERNITY')
            OR upper(coalesce(lt.name_en, '')) LIKE '%MATERNITY%'
            OR upper(btrim(coalesce(lt.category, ''))) = 'MATERNITY')
       AND upper(coalesce(lt.name_en, '')) NOT LIKE '%UNPAID%'
       AND upper(coalesce(lt.name_en, '')) NOT LIKE '%EXTENSION%'
       AND upper(btrim(coalesce(lt.code, ''))) NOT IN ('IDDAH', 'IDDA')
       AND upper(coalesce(lt.name_en, '')) NOT LIKE '%IDDAH%'
       AND upper(btrim(coalesce(lt.category, ''))) <> 'IDDAH'";

        private const string KsaCompanies = @"
    SELECT c.id, c.tenant_id
      FROM companies c
     WHERE NOT c.is_deleted
       AND upper(btrim(coalesce(c.country_code, ''))) IN ('SA', 'SAU')";

        /// <summary>The whole correction. Exposed so the test suite can run it a second time and
        /// prove the second run changes nothing.</summary>
        internal static readonly string UpSql = $@"
DO $$
DECLARE
    policies_raised integer;
    types_raised integer;
    balances_below integer;
BEGIN
    WITH mat_types AS ({MaternityTypes}
    ),
    ksa_companies AS ({KsaCompanies}
    ),
    targets AS (
        SELECT lp.id,
               lp.annual_entitlement_days  AS old_days,
               lp.maximum_days_per_request AS old_max
          FROM leave_policies lp
          JOIN mat_types mt ON mt.id = lp.leave_type_id AND mt.tenant_id = lp.tenant_id
         WHERE lp.status <> 'Archived'
           AND (lp.annual_entitlement_days < {FloorDays}
                OR (lp.maximum_days_per_request > 0 AND lp.maximum_days_per_request < {FloorDays}))
           AND (upper(btrim(lp.country_code)) IN ('SA', 'SAU')
                OR (btrim(coalesce(lp.country_code, '')) = ''
                    AND ((lp.company_id IS NOT NULL
                          AND EXISTS (SELECT 1 FROM ksa_companies kc WHERE kc.id = lp.company_id))
                      OR (lp.company_id IS NULL
                          AND EXISTS (SELECT 1 FROM ksa_companies kc WHERE kc.tenant_id = lp.tenant_id)))))
    ),
    raised AS (
        UPDATE leave_policies lp
           SET annual_entitlement_days  = GREATEST(lp.annual_entitlement_days, {FloorDays}),
               maximum_days_per_request = CASE
                   WHEN lp.maximum_days_per_request > 0 AND lp.maximum_days_per_request < {FloorDays}
                   THEN {FloorDays} ELSE lp.maximum_days_per_request END,
               updated_at_utc = now()
          FROM targets t
         WHERE lp.id = t.id
     RETURNING lp.id, lp.tenant_id, t.old_days, t.old_max,
               lp.annual_entitlement_days, lp.maximum_days_per_request
    )
    INSERT INTO leave_audit_logs
        (id, tenant_id, entity_type, entity_id, action, old_value, new_value, performed_by_name, reason, created_at_utc)
    SELECT gen_random_uuid(), r.tenant_id, 'LeavePolicy', r.id::text, 'StatutoryFloorRaised',
           format('annual_entitlement_days=%s; maximum_days_per_request=%s', r.old_days, r.old_max),
           format('annual_entitlement_days=%s; maximum_days_per_request=%s', r.annual_entitlement_days, r.maximum_days_per_request),
           '{AuditActor}', '{Reason}', now()
      FROM raised r;
    GET DIAGNOSTICS policies_raised = ROW_COUNT;

    WITH mat_types AS ({MaternityTypes}
    ),
    ksa_companies AS ({KsaCompanies}
    ),
    targets AS (
        SELECT lt.id, lt.max_consecutive_days AS old_max
          FROM leave_types lt
          JOIN mat_types mt ON mt.id = lt.id
         WHERE lt.max_consecutive_days > 0
           AND lt.max_consecutive_days < {FloorDays}
           AND (EXISTS (SELECT 1 FROM ksa_companies kc WHERE kc.tenant_id = lt.tenant_id)
                OR EXISTS (SELECT 1 FROM leave_policies lp
                            WHERE lp.leave_type_id = lt.id
                              AND lp.tenant_id = lt.tenant_id
                              AND lp.status <> 'Archived'
                              AND upper(btrim(lp.country_code)) IN ('SA', 'SAU')))
    ),
    raised AS (
        UPDATE leave_types lt
           SET max_consecutive_days = {FloorDays}
          FROM targets t
         WHERE lt.id = t.id
     RETURNING lt.id, lt.tenant_id, t.old_max, lt.max_consecutive_days
    )
    INSERT INTO leave_audit_logs
        (id, tenant_id, entity_type, entity_id, action, old_value, new_value, performed_by_name, reason, created_at_utc)
    SELECT gen_random_uuid(), r.tenant_id, 'LeaveType', r.id::text, 'StatutoryFloorRaised',
           format('max_consecutive_days=%s', r.old_max),
           format('max_consecutive_days=%s', r.max_consecutive_days),
           '{AuditActor}', '{Reason}', now()
      FROM raised r;
    GET DIAGNOSTICS types_raised = ROW_COUNT;

    -- Reported, not changed: a balance is a per-employee figure someone keyed in.
    WITH mat_types AS ({MaternityTypes}
    ),
    ksa_companies AS ({KsaCompanies}
    )
    SELECT count(*) INTO balances_below
      FROM employee_leave_balances b
      JOIN mat_types mt ON mt.id = b.leave_type_id AND mt.tenant_id = b.tenant_id
     WHERE b.year >= 2025
       AND GREATEST(b.entitled, b.accrued) > 0
       AND GREATEST(b.entitled, b.accrued) < {FloorDays}
       AND EXISTS (SELECT 1 FROM ksa_companies kc WHERE kc.tenant_id = b.tenant_id);

    RAISE NOTICE 'RaiseKsaMaternityLeaveToTwelveWeeks: % leave polic(ies) and % leave type(s) raised to % days; % maternity balance row(s) from 2025 on in Saudi tenants hold a grant below % days and were left for HR to review.',
        policies_raised, types_raised, {FloorDays}, balances_below, {FloorDays};
END $$;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: this migration only ever raises a statutory entitlement to the legal
            // minimum, and rolling the code back does not make 70 days lawful again. Every changed row
            // has a StatutoryFloorRaised entry in leave_audit_logs with its previous value.
        }
    }
}
