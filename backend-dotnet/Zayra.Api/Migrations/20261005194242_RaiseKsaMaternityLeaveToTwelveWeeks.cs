using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Raises Saudi maternity leave that was configured at the pre-2025 figure to the 12 weeks the
    /// Labour Law now grants — where that can be done safely — and records for HR review every Saudi
    /// maternity policy it cannot safely raise.
    ///
    /// <para><b>The defect.</b> The setup assistant drafted every tenant's maternity leave at 70 days
    /// (Art. 151's old "10 weeks"). Royal Decree M/44, in force 19 February 2025, made it "a fully paid
    /// maternity leave of (twelve) weeks" — 84 CALENDAR days.</para>
    ///
    /// <para><b>Which policies reach Saudi employees only</b> ("Saudi-only"): the policy's country is
    /// SA/SAU; or it has no country and belongs to a Saudi company; or it has neither and every company
    /// in the tenant is Saudi. A no-country, no-company policy in a tenant that ALSO has a non-Saudi (or
    /// country-less) company is "mixed" — it governs non-Saudi staff too — and is never raised.</para>
    ///
    /// <para><b>What it changes — and nothing else.</b></para>
    /// <list type="number">
    /// <item><c>leave_policies</c>, non-archived, maternity type, Saudi-only, counted on the CALENDAR
    /// (<c>weekends_included</c> AND <c>public_holidays_included</c>): <c>annual_entitlement_days</c>
    /// below 84 → 84, and <c>maximum_days_per_request</c> in (0, 84) → 84 (0 = no cap, left alone).
    /// <c>updated_at_utc</c> is not touched; the audit row records the change.</item>
    /// <item><c>leave_types</c>: <c>max_consecutive_days</c> in (0, 84) → 84 for a maternity type,
    /// only in a tenant with no non-Saudi company and with a Saudi company or an SA/SAU policy for the
    /// type. In a mixed tenant the type cap is left alone — raising it would lift non-Saudi staff — and
    /// Saudi staff are covered at request time, where <c>LeaveService</c> raises a below-floor cap to
    /// the statutory figure for a Saudi employee.</item>
    /// <item><c>leave_audit_logs</c>, action <c>StatutoryFloorRaised</c>: one row per policy or type
    /// raised, with old and new figures.</item>
    /// <item><c>leave_audit_logs</c>, action <c>StatutoryReviewNeeded</c>: one row per policy NOT
    /// raised but needing a person — (a) a Saudi-only maternity policy counted in WORKING days (84
    /// working days is ~17 weeks, not 12, so the number cannot simply be set; HR must switch the
    /// counting to calendar days and set 84), and (b) a mixed policy that is below 84 calendar days
    /// while no compliant Saudi-scoped maternity policy exists for the same type (HR must create one).
    /// These rows are durable and queryable by tenant: <c>WHERE action = 'StatutoryReviewNeeded'</c>.</item>
    /// </list>
    ///
    /// <para><b>Raise only, and idempotent.</b> Every raise predicate is "below 84", so nothing is ever
    /// lowered and a second run raises nothing. A review row is written only if that policy has none
    /// from this migration yet. Leave balances and requests are not touched: KSA statutory event leave
    /// no longer draws on an accrued balance (see <c>LeaveService.SubmitRequestCoreAsync</c>).</para>
    ///
    /// <para><b>Recognition is by name.</b> A "maternity leave type" is the predicate
    /// <c>KsaStatutorySpecialLeave.Classify</c> applies in C#: code MAT or MATERNITY, or a name
    /// containing MATERNITY, or category Maternity — but not a name saying UNPAID or EXTENSION (Art.
    /// 151's own unpaid extension) and not an iddah type. A type renamed so that none of these holds is
    /// invisible to this correction, as it is to the floor guard.</para>
    ///
    /// <para><b>Down is a no-op.</b> Lowering a statutory entitlement is not a rollback this migration
    /// will perform. If a person decides otherwise, the audit rows restore every raised value exactly:</para>
    /// <code>
    /// UPDATE leave_policies lp
    ///    SET annual_entitlement_days  = substring(a.old_value from 'annual_entitlement_days=([0-9.]+)')::numeric,
    ///        maximum_days_per_request = substring(a.old_value from 'maximum_days_per_request=([0-9.]+)')::numeric
    ///   FROM leave_audit_logs a
    ///  WHERE a.action = 'StatutoryFloorRaised' AND a.entity_type = 'LeavePolicy'
    ///    AND a.performed_by_name = 'system: migration RaiseKsaMaternityLeaveToTwelveWeeks'
    ///    AND a.entity_id = lp.id::text AND a.tenant_id = lp.tenant_id;
    /// UPDATE leave_types lt
    ///    SET max_consecutive_days = substring(a.old_value from 'max_consecutive_days=([0-9]+)')::integer
    ///   FROM leave_audit_logs a
    ///  WHERE a.action = 'StatutoryFloorRaised' AND a.entity_type = 'LeaveType'
    ///    AND a.performed_by_name = 'system: migration RaiseKsaMaternityLeaveToTwelveWeeks'
    ///    AND a.entity_id = lt.id::text AND a.tenant_id = lt.tenant_id;
    /// </code>
    /// </summary>
    public partial class RaiseKsaMaternityLeaveToTwelveWeeks : Migration
    {
        /// <summary>KSA Art. 151 as amended: 12 weeks.</summary>
        internal const int FloorDays = 84;

        internal const string AuditActor = "system: migration RaiseKsaMaternityLeaveToTwelveWeeks";

        private const string RaisedReason =
            "Saudi Labour Law Art. 151 as amended by Royal Decree M/44 (in force 2025-02-19): maternity leave is "
            + "12 weeks (84 calendar days) fully paid. Raised from below that floor; values at or above it were not changed.";

        private const string WorkingDaysReason =
            "Saudi maternity leave is 12 weeks — 84 CALENDAR days (Art. 151 as amended from 2025-02-19). This policy "
            + "counts working days, so it was not raised automatically. Switch its counting to calendar days (include "
            + "weekends and public holidays) and set the entitlement and per-request cap to at least 84.";

        private const string MixedReason =
            "This maternity policy has no country or company and the tenant has Saudi and non-Saudi companies, so it "
            + "was not raised: 84 days is the Saudi figure and may not be right for the other countries. Saudi employees fall "
            + "under it and it is below 84 calendar days. Create a Saudi policy (country SA) at 84 calendar days.";

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

        /// <summary>Every live company, flagged Saudi or not. A company with no country is NOT Saudi:
        /// an unknown jurisdiction must not have the Saudi figure forced onto it.</summary>
        private const string Companies = @"
    SELECT c.id, c.tenant_id, upper(btrim(coalesce(c.country_code, ''))) IN ('SA', 'SAU') AS is_saudi
      FROM companies c
     WHERE NOT c.is_deleted";

        /// <summary>Non-archived maternity policies, each classified by reach and counting basis.</summary>
        private const string Classified = @"
    SELECT lp.id, lp.tenant_id, lp.leave_type_id,
           lp.annual_entitlement_days AS days, lp.maximum_days_per_request AS max_days,
           (lp.weekends_included AND lp.public_holidays_included) AS calendar,
           CASE
             WHEN upper(btrim(coalesce(lp.country_code, ''))) IN ('SA', 'SAU') THEN 'saudi'
             WHEN btrim(coalesce(lp.country_code, '')) <> '' THEN 'none'
             WHEN lp.company_id IS NOT NULL THEN
                  CASE WHEN EXISTS (SELECT 1 FROM cos c WHERE c.id = lp.company_id AND c.is_saudi) THEN 'saudi' ELSE 'none' END
             WHEN NOT EXISTS (SELECT 1 FROM cos c WHERE c.tenant_id = lp.tenant_id AND c.is_saudi) THEN 'none'
             WHEN EXISTS (SELECT 1 FROM cos c WHERE c.tenant_id = lp.tenant_id AND NOT c.is_saudi) THEN 'mixed'
             ELSE 'saudi'
           END AS reach
      FROM leave_policies lp
      JOIN mat_types mt ON mt.id = lp.leave_type_id AND mt.tenant_id = lp.tenant_id
     WHERE lp.status <> 'Archived'";

        private static readonly string Ctes = $@"
    WITH mat_types AS ({MaternityTypes}
    ),
    cos AS ({Companies}
    ),
    pol AS ({Classified}
    )";

        /// <summary>The whole correction. Exposed so the test suite can run it a second time and
        /// prove the second run changes nothing.</summary>
        internal static readonly string UpSql = $@"
DO $$
DECLARE
    policies_raised integer;
    types_raised integer;
    working_day_reviews integer;
    mixed_reviews integer;
BEGIN
    -- 1. Raise Saudi-only, calendar-counted policies below 84.
{Ctes},
    raised AS (
        UPDATE leave_policies lp
           SET annual_entitlement_days  = GREATEST(lp.annual_entitlement_days, {FloorDays}),
               maximum_days_per_request = CASE
                   WHEN lp.maximum_days_per_request > 0 AND lp.maximum_days_per_request < {FloorDays}
                   THEN {FloorDays} ELSE lp.maximum_days_per_request END
          FROM pol p
         WHERE lp.id = p.id
           AND p.reach = 'saudi' AND p.calendar
           AND (p.days < {FloorDays} OR (p.max_days > 0 AND p.max_days < {FloorDays}))
     RETURNING lp.id, lp.tenant_id, p.days AS old_days, p.max_days AS old_max,
               lp.annual_entitlement_days, lp.maximum_days_per_request
    )
    INSERT INTO leave_audit_logs
        (id, tenant_id, entity_type, entity_id, action, old_value, new_value, performed_by_name, reason, created_at_utc)
    SELECT gen_random_uuid(), r.tenant_id, 'LeavePolicy', r.id::text, 'StatutoryFloorRaised',
           format('annual_entitlement_days=%s; maximum_days_per_request=%s', r.old_days, r.old_max),
           format('annual_entitlement_days=%s; maximum_days_per_request=%s', r.annual_entitlement_days, r.maximum_days_per_request),
           '{AuditActor}', '{RaisedReason}', now()
      FROM raised r;
    GET DIAGNOSTICS policies_raised = ROW_COUNT;

    -- 2. Saudi-only policies counted in working days: not raised, recorded for HR.
{Ctes}
    INSERT INTO leave_audit_logs
        (id, tenant_id, entity_type, entity_id, action, old_value, new_value, performed_by_name, reason, created_at_utc)
    SELECT gen_random_uuid(), p.tenant_id, 'LeavePolicy', p.id::text, 'StatutoryReviewNeeded',
           format('annual_entitlement_days=%s; maximum_days_per_request=%s; counting=working days', p.days, p.max_days),
           'required: at least {FloorDays} days; counting=calendar days',
           '{AuditActor}', '{WorkingDaysReason}', now()
      FROM pol p
     WHERE p.reach = 'saudi' AND NOT p.calendar
       AND NOT EXISTS (SELECT 1 FROM leave_audit_logs a
                        WHERE a.tenant_id = p.tenant_id AND a.entity_id = p.id::text
                          AND a.action = 'StatutoryReviewNeeded' AND a.performed_by_name = '{AuditActor}');
    GET DIAGNOSTICS working_day_reviews = ROW_COUNT;

    -- 3. Mixed policies below the Saudi floor with no compliant Saudi-scoped sibling: not raised, recorded.
{Ctes}
    INSERT INTO leave_audit_logs
        (id, tenant_id, entity_type, entity_id, action, old_value, new_value, performed_by_name, reason, created_at_utc)
    SELECT gen_random_uuid(), p.tenant_id, 'LeavePolicy', p.id::text, 'StatutoryReviewNeeded',
           format('annual_entitlement_days=%s; maximum_days_per_request=%s; counting=%s', p.days, p.max_days,
                  CASE WHEN p.calendar THEN 'calendar days' ELSE 'working days' END),
           'required: a separate Saudi policy (country SA) at {FloorDays} calendar days',
           '{AuditActor}', '{MixedReason}', now()
      FROM pol p
     WHERE p.reach = 'mixed'
       AND (NOT p.calendar OR p.days < {FloorDays} OR (p.max_days > 0 AND p.max_days < {FloorDays}))
       AND NOT EXISTS (SELECT 1 FROM pol s
                        WHERE s.tenant_id = p.tenant_id AND s.leave_type_id = p.leave_type_id
                          AND s.reach = 'saudi' AND s.calendar AND s.days >= {FloorDays}
                          AND (s.max_days = 0 OR s.max_days >= {FloorDays}))
       AND NOT EXISTS (SELECT 1 FROM leave_audit_logs a
                        WHERE a.tenant_id = p.tenant_id AND a.entity_id = p.id::text
                          AND a.action = 'StatutoryReviewNeeded' AND a.performed_by_name = '{AuditActor}');
    GET DIAGNOSTICS mixed_reviews = ROW_COUNT;

    -- 4. Leave-type caps, only where no non-Saudi staff can be lifted by it.
{Ctes},
    targets AS (
        SELECT lt.id, lt.max_consecutive_days AS old_max
          FROM leave_types lt
          JOIN mat_types mt ON mt.id = lt.id
         WHERE lt.max_consecutive_days > 0
           AND lt.max_consecutive_days < {FloorDays}
           AND NOT EXISTS (SELECT 1 FROM cos c WHERE c.tenant_id = lt.tenant_id AND NOT c.is_saudi)
           AND (EXISTS (SELECT 1 FROM cos c WHERE c.tenant_id = lt.tenant_id AND c.is_saudi)
                OR EXISTS (SELECT 1 FROM pol p WHERE p.leave_type_id = lt.id AND p.tenant_id = lt.tenant_id
                                                 AND p.reach = 'saudi'))
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
           '{AuditActor}', '{RaisedReason}', now()
      FROM raised r;
    GET DIAGNOSTICS types_raised = ROW_COUNT;

    RAISE NOTICE 'RaiseKsaMaternityLeaveToTwelveWeeks: raised % polic(ies) and % leave type(s) to % days; recorded % working-day and % mixed-tenant polic(ies) as StatutoryReviewNeeded in leave_audit_logs.',
        policies_raised, types_raised, {FloorDays}, working_day_reviews, mixed_reviews;
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
            // minimum, and rolling the code back does not make 70 days lawful again. The class summary
            // carries the reversal SQL, driven by the StatutoryFloorRaised audit rows, for a human
            // decision to use.
        }
    }
}
