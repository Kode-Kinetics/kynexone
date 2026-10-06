using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Models;

/// <summary>
/// EF configuration for Release A (grade entitlements, contract-year package, renewal case) — the live bridge
/// of rev 8.3.2. Kept next to the models so <c>ZayraDbContext</c> stays append-only, as
/// <see cref="TimesheetModelConfiguration"/> does. What EF cannot model — the EXCLUDE, the employee
/// PublicId foreign keys, and the four triggers — is added by the <c>ReleaseAEntitlementsAndRenewals</c>
/// migration from constants the Postgres test fixture also applies.
///
/// <para><b>Value-set literals.</b> Each IN (…) list below is the SQL spelling of a C# constants class
/// (<see cref="GradeEntitlementValueTypes"/>, <see cref="RenewalStates"/>, …). <c>ReleaseAContractTests</c>
/// asserts the two agree, so a value added to one and not the other fails the build.</para>
/// </summary>
internal static class ReleaseAModelConfiguration
{
    internal static string In(IEnumerable<string> values) => "(" + string.Join(",", values.Select(v => $"'{v}'")) + ")";

    internal static readonly string ValueTypesIn = In(GradeEntitlementValueTypes.All);
    internal static readonly string CoverageTiersIn = In(CoverageTiers.All);
    internal static readonly string DependantScopesIn = In(DependantScopes.All);
    internal static readonly string LimitPeriodsIn = In(EntitlementLimitPeriods.All);
    internal static readonly string NationalityScopesIn = In(NationalityScopes.All);

    /// <summary>The value-shape rule shared by grade cells and employee rows: the primary value column for each
    /// type is present, and the columns that belong to another type are absent. coverage_tier and quantity are
    /// also allowed as qualifiers (a Quantity ticket carries its class; an Amount education allowance its
    /// child count).</summary>
    internal const string ValueShapeSql =
        "(value_type = 'Amount' AND amount IS NOT NULL AND amount >= 0 AND rate IS NULL)"
        + " OR (value_type = 'PercentOfBasic' AND rate IS NOT NULL AND rate > 0 AND rate <= 1 AND amount IS NULL)"
        + " OR (value_type IN ('MultipleOfBasic','MultipleOfGross','MultipleOfHousing') AND rate IS NOT NULL AND rate > 0 AND amount IS NULL)"
        + " OR (value_type IN ('InKind','EligibilityOnly') AND amount IS NULL AND rate IS NULL)"
        + " OR (value_type = 'CoverageTier' AND coverage_tier IS NOT NULL AND amount IS NULL AND rate IS NULL)"
        + " OR (value_type = 'Quantity' AND quantity IS NOT NULL AND amount IS NULL AND rate IS NULL)";

    internal static readonly string RenewalStatesIn = In(RenewalStates.All);
    internal static readonly string TerminalStatesIn = In(RenewalStates.Terminal);

    /// <summary>The stage-dependent next hard deadline (rev 8.3 §2.3), NULL once the case is closed.</summary>
    internal const string NextHardDeadlineSql =
        "CASE"
        + " WHEN state IN ('Applied','NonRenewed','Cancelled') THEN NULL"
        + " WHEN state IN ('NeedsConfirmation','Open','AwaitingManager','OfferInPreparation','InApproval') THEN offer_due_on"
        + " WHEN state IN ('OfferSent','Accepted') THEN qiwa_submit_due_on"
        // The earlier of the two Qiwa dates, spelled without LEAST/MIN so the expression is portable (SQLite fixtures).
        + " WHEN state = 'QiwaPending' THEN CASE WHEN qiwa_respond_by_on IS NULL THEN qiwa_gate_due_on"
        + " WHEN qiwa_gate_due_on IS NULL OR qiwa_respond_by_on < qiwa_gate_due_on THEN qiwa_respond_by_on ELSE qiwa_gate_due_on END"
        + " WHEN state = 'ReadyToApply' THEN expiring_end_date"
        + " ELSE notice_due_on END";

    /// <param name="isNpgsql">PostgreSQL (production, migrations) maps the case's row version onto the xmin system
    /// column. SQLite and InMemory — used only by the unit suite — have no xmin, so the token is not mapped there.</param>
    internal static void Configure(ModelBuilder modelBuilder, bool isNpgsql = true)
    {
        // ── FK targets on existing tables (each named by the FK that needs it) ──────────────────────
        modelBuilder.Entity<EmployeeDocument>().HasAlternateKey(x => new { x.TenantId, x.Id });       // evidence/consent FKs
        modelBuilder.Entity<ApprovalRequest>().HasAlternateKey(x => new { x.TenantId, x.Id });        // exception/offer approvals
        modelBuilder.Entity<EmployeeSalaryStructure>().HasAlternateKey(x => new { x.TenantId, x.Id }); // PercentOfBasic witness

        // ── pay_components: per-company skip (§1.1 "Skipped") ───────────────────────────────────────
        modelBuilder.Entity<PayComponent>(entity =>
        {
            entity.ToTable("pay_components", t => t.HasCheckConstraint("ck_pay_components__floor_always_offered",
                "statutory_floor = 'None' OR is_offered"));
            // ValueGeneratedNever: EF must always send the value, or a false (the CLR default) would be skipped on
            // insert and the database default would silently re-offer the component (same as loan_policies.is_offered).
            entity.Property(x => x.IsOffered).HasDefaultValue(true).ValueGeneratedNever();
        });

        // ── approval_requests: the frozen offer witness ─────────────────────────────────────────────
        modelBuilder.Entity<ApprovalRequest>(entity =>
        {
            entity.ToTable("approval_requests", t =>
            {
                t.HasCheckConstraint("ck_approval_requests__payload_pair", "(payload IS NULL) = (payload_sha256 IS NULL)");
                // The lower-case-hex format CHECK uses a regex, which only PostgreSQL has: it is in the migration's
                // AddPostgresOnlyChecksSql, so the SQLite and InMemory test schemas built from this model stay valid.
            });
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.Property(x => x.PayloadSha256).HasColumnType("character(64)");
        });

        // ── employee_loans: Art. 92 consent ─────────────────────────────────────────────────────────
        modelBuilder.Entity<EmployeeLoan>(entity =>
        {
            entity.ToTable("employee_loans", t => t.HasCheckConstraint("ck_employee_loans__cap_base_wage", "cap_base_wage IS NULL OR cap_base_wage >= 0"));
            entity.Property(x => x.CapBaseWage).HasPrecision(18, 2);
            entity.HasOne<EmployeeDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConsentDocumentId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        // ── employee_contracts: the renewal chain ───────────────────────────────────────────────────
        modelBuilder.Entity<EmployeeContract>(entity =>
        {
            entity.ToTable("employee_contracts", t =>
            {
                t.HasCheckConstraint("ck_employee_contracts__renewal_number", "renewal_number IS NULL OR renewal_number >= 0");
                t.HasCheckConstraint("ck_employee_contracts__non_renewal_notice_days", "non_renewal_notice_days IS NULL OR non_renewal_notice_days >= 0");
                t.HasCheckConstraint("ck_employee_contracts__worker_nationality_class",
                    "worker_nationality_class IS NULL OR worker_nationality_class IN " + In(WorkerNationalityClasses.All));
                t.HasCheckConstraint("ck_employee_contracts__provisional_basis",
                    "provisional_basis IS NULL OR provisional_basis IN " + In(ProvisionalBases.All));
                // DEVIATION (plan §2 R0 item 5): the plan's CHECK ((provisional_basis IS NULL) = (renewal_number IS NOT NULL))
                // NOT VALID would refuse every status change on an existing contract (NOT VALID only skips the initial scan;
                // each later UPDATE is checked) and every contract the current create/supersede endpoints write. This is its
                // safe half: a provisional term never carries a renewal number (it is assigned at confirmation). The other
                // half — every confirmed term has one — is added by the R4 chain census migration (R0b) once all writers set it.
                t.HasCheckConstraint("ck_employee_contracts__provisional_has_no_renewal_number",
                    "provisional_basis IS NULL OR renewal_number IS NULL");
                t.HasCheckConstraint("ck_employee_contracts__not_renewed_from_itself",
                    "renewed_from_contract_id IS NULL OR renewed_from_contract_id <> id");
            });
            // FK target for every per-employee child: (tenant, employee, contract) proves the child is this employee's term.
            entity.HasAlternateKey(x => new { x.TenantId, x.EmployeeId, x.Id });
            entity.Property(x => x.WorkerNationalityClass).HasMaxLength(10);
            entity.Property(x => x.ProvisionalBasis).HasMaxLength(20);
            entity.Property(x => x.AutoRenew).HasDefaultValue(true).ValueGeneratedNever();
            // A term is renewed at most once; a holdover term is PROMOTED by Apply, never duplicated (rev 8.3.1 §3).
            entity.HasIndex(x => new { x.TenantId, x.RenewedFromContractId }).IsUnique()
                .HasDatabaseName("ux_employee_contracts__renewed_from");
            entity.HasOne<EmployeeContract>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EmployeeId, x.RenewedFromContractId })
                .HasPrincipalKey(x => new { x.TenantId, x.EmployeeId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Serves the renewal opener and the dashboard buckets: fixed-term contracts by end date.
            entity.HasIndex(x => new { x.TenantId, x.EndDate })
                .HasDatabaseName("ix_employee_contracts__end_date")
                .HasFilter("end_date IS NOT NULL AND NOT is_deleted");
        });

        // ── employee_salary_structures (live alias of employee_salaries) ─────────────────────────────
        modelBuilder.Entity<EmployeeSalaryStructure>(entity =>
        {
            entity.ToTable("employee_salary_structures", t =>
            {
                t.HasCheckConstraint("ck_employee_salary_structures__housing_basis", "housing_basis IN " + In(AllowanceBases.All));
                t.HasCheckConstraint("ck_employee_salary_structures__transport_basis", "transport_basis IN " + In(AllowanceBases.All));
                t.HasCheckConstraint("ck_employee_salary_structures__housing_rate_pair",
                    "(housing_basis = 'PercentOfBasic') = (housing_rate IS NOT NULL)");
                t.HasCheckConstraint("ck_employee_salary_structures__transport_rate_pair",
                    "(transport_basis = 'PercentOfBasic') = (transport_rate IS NOT NULL)");
                t.HasCheckConstraint("ck_employee_salary_structures__housing_rate_range", "housing_rate IS NULL OR (housing_rate > 0 AND housing_rate <= 1)");
                t.HasCheckConstraint("ck_employee_salary_structures__transport_rate_range", "transport_rate IS NULL OR (transport_rate > 0 AND transport_rate <= 1)");
                t.HasCheckConstraint("ck_employee_salary_structures__housing_amount_matches_basis",
                    "(housing_basis <> 'PercentOfBasic' OR housing_allowance = round(basic_salary * housing_rate, 2))"
                    + " AND (housing_basis <> 'InKind' OR housing_allowance = 0)");
                t.HasCheckConstraint("ck_employee_salary_structures__transport_amount_matches_basis",
                    "(transport_basis <> 'PercentOfBasic' OR transport_allowance = round(basic_salary * transport_rate, 2))"
                    + " AND (transport_basis <> 'InKind' OR transport_allowance = 0)");
                t.HasCheckConstraint("ck_employee_salary_structures__qiwa_evidence_dated",
                    "qiwa_evidence_document_id IS NULL OR qiwa_confirmed_on IS NOT NULL");
            });
            entity.Property(x => x.HousingBasis).HasMaxLength(14).HasDefaultValue(AllowanceBases.Amount);
            entity.Property(x => x.TransportBasis).HasMaxLength(14).HasDefaultValue(AllowanceBases.Amount);
            entity.Property(x => x.HousingRate).HasPrecision(9, 6);
            entity.Property(x => x.TransportRate).HasPrecision(9, 6);
            entity.HasOne<EmployeeDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.QiwaEvidenceDocumentId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ContractRenewalCase>().WithMany().HasForeignKey(x => new { x.TenantId, x.RenewalCaseId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        // ── NEW employee_entitlements (rev 8.3 §2.2) ─────────────────────────────────────────────────
        modelBuilder.Entity<EmployeeEntitlement>(entity =>
        {
            entity.ToTable("employee_entitlements", t =>
            {
                t.HasCheckConstraint("ck_employee_entitlements__entitlement_class", "entitlement_class IN ('Contractual','Facility')");
                t.HasCheckConstraint("ck_employee_entitlements__value_type", "value_type IN " + ValueTypesIn);
                t.HasCheckConstraint("ck_employee_entitlements__value_shape", ValueShapeSql);
                t.HasCheckConstraint("ck_employee_entitlements__coverage_tier", "coverage_tier IS NULL OR coverage_tier IN " + CoverageTiersIn);
                t.HasCheckConstraint("ck_employee_entitlements__quantity", "quantity IS NULL OR quantity > 0");
                t.HasCheckConstraint("ck_employee_entitlements__dependant_scope", "dependant_scope IN " + DependantScopesIn);
                t.HasCheckConstraint("ck_employee_entitlements__max_dependants",
                    "max_dependants IS NULL OR (max_dependants >= 0 AND dependant_scope <> 'None')");
                t.HasCheckConstraint("ck_employee_entitlements__limit_period", "limit_period IS NULL OR limit_period IN " + LimitPeriodsIn);
                t.HasCheckConstraint("ck_employee_entitlements__outstanding_is_facility",
                    "max_outstanding_amount IS NULL OR (entitlement_class = 'Facility' AND max_outstanding_amount >= 0)");
                t.HasCheckConstraint("ck_employee_entitlements__resolved_amount", "resolved_amount IS NULL OR resolved_amount >= 0");
                t.HasCheckConstraint("ck_employee_entitlements__source", "source IN " + In(EntitlementSources.All));
                t.HasCheckConstraint("ck_employee_entitlements__verification_state", "verification_state IN " + In(EntitlementVerificationStates.All));
                t.HasCheckConstraint("ck_employee_entitlements__approval_iff_exception",
                    "(source IN ('Exception','Correction')) = (approval_request_id IS NOT NULL)");
                t.HasCheckConstraint("ck_employee_entitlements__carried_iff_origin",
                    "(source = 'Carried') = (carried_from_entitlement_id IS NOT NULL)");
                t.HasCheckConstraint("ck_employee_entitlements__correction_iff_document",
                    "(source = 'Correction') = (correction_basis_document_id IS NOT NULL)");
                t.HasCheckConstraint("ck_employee_entitlements__percent_has_basis",
                    "(value_type = 'PercentOfBasic') = (resolved_basis_salary_id IS NOT NULL)");
                t.HasCheckConstraint("ck_employee_entitlements__grade_default_cites_cell",
                    "source <> 'GradeDefault' OR grade_entitlement_id IS NOT NULL");
                t.HasCheckConstraint("ck_employee_entitlements__dates", "effective_to IS NULL OR effective_to >= effective_from");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasAlternateKey(x => new { x.TenantId, x.EmployeeId, x.Id });   // carried_from (and loans, later)
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.PayComponentCode).HasMaxLength(64);
            entity.Property(x => x.EntitlementClass).HasMaxLength(20);
            entity.Property(x => x.ValueType).HasMaxLength(20);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Rate).HasPrecision(9, 4);
            entity.Property(x => x.MaxOutstandingAmount).HasPrecision(18, 2);
            entity.Property(x => x.CoverageTier).HasMaxLength(20);
            entity.Property(x => x.DependantScope).HasMaxLength(10).HasDefaultValue(DependantScopes.None);
            entity.Property(x => x.LimitPeriod).HasMaxLength(10);
            entity.Property(x => x.ResolvedAmount).HasPrecision(18, 2);
            entity.Property(x => x.Source).HasMaxLength(20);
            entity.Property(x => x.VerificationState).HasMaxLength(12).HasDefaultValue(EntitlementVerificationStates.Verified);
            // ESS, loans, payroll and audit all ask "this employee's rows in force on a date", newest first.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.EffectiveFrom })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_employee_entitlements__as_of");
            // One row per component per approval; a Carried row inherits its origin's approval, so it is exempt.
            entity.HasIndex(x => new { x.TenantId, x.ApprovalRequestId, x.PayComponentCode }).IsUnique()
                .HasDatabaseName("ux_employee_entitlements__approval_component")
                .HasFilter("carried_from_entitlement_id IS NULL");
            entity.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeContract>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EmployeeId, x.ContractId })
                .HasPrincipalKey(x => new { x.TenantId, x.EmployeeId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GradeEntitlement>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.GradeEntitlementId, x.PayComponentCode })
                .HasPrincipalKey(x => new { x.TenantId, x.Id, x.PayComponentCode })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApprovalRequest>().WithMany().HasForeignKey(x => new { x.TenantId, x.ApprovalRequestId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ContractRenewalCase>().WithMany().HasForeignKey(x => new { x.TenantId, x.RenewalCaseId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeEntitlement>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EmployeeId, x.CarriedFromEntitlementId })
                .HasPrincipalKey(x => new { x.TenantId, x.EmployeeId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeSalaryStructure>().WithMany().HasForeignKey(x => new { x.TenantId, x.ResolvedBasisSalaryId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectionBasisDocumentId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        // ── NEW contract_renewal_cases (rev 8.3 §2.3) ────────────────────────────────────────────────
        modelBuilder.Entity<ContractRenewalCase>(entity =>
        {
            entity.ToTable("contract_renewal_cases", t =>
            {
                t.HasCheckConstraint("ck_contract_renewal_cases__state", "state IN " + RenewalStatesIn);
                t.HasCheckConstraint("ck_contract_renewal_cases__closed_iff_terminal",
                    "(closed_at IS NULL) = (state NOT IN " + TerminalStatesIn + ")");
                t.HasCheckConstraint("ck_contract_renewal_cases__hold_iff_reason", "(state = 'OnHold') = (hold_reason IS NOT NULL)");
                // T20 releases a hold back to exactly the state it came from (never skipping T2 for an unconfirmed case).
                t.HasCheckConstraint("ck_contract_renewal_cases__hold_iff_held_from", "(state = 'OnHold') = (held_from_state IS NOT NULL)");
                t.HasCheckConstraint("ck_contract_renewal_cases__held_from_state",
                    "held_from_state IS NULL OR held_from_state IN " + In(RenewalStates.All.Where(s => !RenewalStates.IsTerminal(s) && s != RenewalStates.OnHold)));
                // T10 paper path: the signed document, and a second user who is not the one who recorded it.
                t.HasCheckConstraint("ck_contract_renewal_cases__paper_response_two_people",
                    "response_channel IS NULL OR response_channel <> 'PaperUpload' OR employee_response_confirmed_by IS NULL"
                    + " OR (employee_responded_by_user_id IS NOT NULL AND employee_response_confirmed_by <> employee_responded_by_user_id)");
                t.HasCheckConstraint("ck_contract_renewal_cases__response_document_is_paper",
                    "employee_response_document_id IS NULL OR (response_channel IS NOT NULL AND response_channel = 'PaperUpload')");
                t.HasCheckConstraint("ck_contract_renewal_cases__hold_reason",
                    "hold_reason IS NULL OR hold_reason IN " + In(RenewalHoldReasons.All));
                // DEVIATION (plan: "state = 'NeedsConfirmation' OR notice_due_on IS NOT NULL"): T19/T21 let an unconfirmed
                // case be put on hold or cancelled (separation, transfer) before its dates exist, so those two are exempt too.
                t.HasCheckConstraint("ck_contract_renewal_cases__deadlines_once_confirmed",
                    "state IN ('NeedsConfirmation','OnHold','Cancelled') OR notice_due_on IS NOT NULL");
                t.HasCheckConstraint("ck_contract_renewal_cases__offer_before_notice",
                    "offer_due_on IS NULL OR notice_due_on IS NULL OR offer_due_on < notice_due_on");
                t.HasCheckConstraint("ck_contract_renewal_cases__non_renewed_on_time",
                    "state <> 'NonRenewed' OR (non_renewal_notice_served_on IS NOT NULL AND notice_due_on IS NOT NULL"
                    + " AND non_renewal_notice_served_on <= notice_due_on)");
                t.HasCheckConstraint("ck_contract_renewal_cases__worker_nationality_class",
                    "worker_nationality_class IN " + In(WorkerNationalityClasses.All));
                // allowed_actions ⊆ the four actions, a non-Saudi case never offers ConvertIndefinite, and the chosen action
                // is one of the allowed ones: array operators only PostgreSQL has, so these three CHECKs live in the
                // migration's AddPostgresOnlyChecksSql (applied by the Postgres fixture too), not in this portable model.
                t.HasCheckConstraint("ck_contract_renewal_cases__recommended_action",
                    "recommended_action IS NULL OR recommended_action IN " + In(ContractActions.All));
                t.HasCheckConstraint("ck_contract_renewal_cases__fallback_if_rejected",
                    "fallback_if_rejected IS NULL OR fallback_if_rejected IN " + In(RenewalValueSets.FallbackIfRejected));
                t.HasCheckConstraint("ck_contract_renewal_cases__term_months", "term_months IS NULL OR term_months > 0");
                t.HasCheckConstraint("ck_contract_renewal_cases__employee_response",
                    "employee_response IS NULL OR (employee_response IN " + In(RenewalValueSets.EmployeeResponses)
                    + " AND employee_responded_at IS NOT NULL AND response_channel IS NOT NULL)");
                t.HasCheckConstraint("ck_contract_renewal_cases__response_channel",
                    "response_channel IS NULL OR response_channel IN " + In(RenewalValueSets.ResponseChannels));
                t.HasCheckConstraint("ck_contract_renewal_cases__offer_version", "offer_version >= 0");
                // offer_sha256 format (regex): AddPostgresOnlyChecksSql.
                t.HasCheckConstraint("ck_contract_renewal_cases__qiwa_attempts", "qiwa_attempts >= 0");
                t.HasCheckConstraint("ck_contract_renewal_cases__qiwa_evidence_outcome",
                    "qiwa_evidence_outcome IS NULL OR qiwa_evidence_outcome IN " + In(RenewalValueSets.QiwaEvidenceOutcomes));
                // Four eyes on the Qiwa evidence: the verifier is a second user, and verification follows a recording.
                t.HasCheckConstraint("ck_contract_renewal_cases__qiwa_evidence_two_people",
                    "qiwa_evidence_verified_by IS NULL OR (qiwa_evidence_recorded_by IS NOT NULL AND qiwa_evidence_verified_by <> qiwa_evidence_recorded_by)");
                t.HasCheckConstraint("ck_contract_renewal_cases__notice_channel",
                    "non_renewal_notice_channel IS NULL OR non_renewal_notice_channel IN " + In(RenewalValueSets.NoticeChannels));
                t.HasCheckConstraint("ck_contract_renewal_cases__notice_by",
                    "non_renewal_notice_by IS NULL OR non_renewal_notice_by IN " + In(RenewalValueSets.NoticeBy));
                t.HasCheckConstraint("ck_contract_renewal_cases__applied_has_result",
                    "state <> 'Applied' OR (resulting_contract_id IS NOT NULL AND applied_at IS NOT NULL AND applied_by IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.WorkerNationalityClass).HasMaxLength(10);
            entity.Property(x => x.AllowedActions).HasColumnType("text[]");
            entity.Property(x => x.State).HasMaxLength(20);
            entity.Property(x => x.RecommendedAction).HasMaxLength(20);
            entity.Property(x => x.ContractAction).HasMaxLength(20);
            entity.Property(x => x.FallbackIfRejected).HasMaxLength(20);
            entity.Property(x => x.ReasonCode).HasMaxLength(64);
            entity.Property(x => x.EmployeeResponse).HasMaxLength(12);
            entity.Property(x => x.ResponseChannel).HasMaxLength(12);
            entity.Property(x => x.HoldReason).HasMaxLength(20);
            entity.Property(x => x.HeldFromState).HasMaxLength(20);
            entity.Property(x => x.EmployeeAcceptanceRequired).HasDefaultValue(true).ValueGeneratedNever();
            entity.Property(x => x.OfferSha256).HasColumnType("character(64)");
            entity.Property(x => x.OfferCostDeltaMonthly).HasPrecision(18, 2);
            entity.Property(x => x.QiwaRequired).HasDefaultValue(true).ValueGeneratedNever();
            entity.Property(x => x.QiwaRequestNo).HasMaxLength(64);
            entity.Property(x => x.QiwaEvidenceOutcome).HasMaxLength(20);
            entity.Property(x => x.NonRenewalNoticeChannel).HasMaxLength(10);
            entity.Property(x => x.NonRenewalNoticeBy).HasMaxLength(10);
            entity.Property(x => x.ApplyIdempotencyKey).HasMaxLength(100);
            entity.Property(x => x.NextHardDeadline).HasComputedColumnSql(NextHardDeadlineSql, stored: true);
            // PostgreSQL's xmin system column: every state change is a compare-and-set on the row version.
            if (isNpgsql) entity.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            else entity.Ignore(x => x.Version);
            // One case per expiring term — this is also what makes the daily opener idempotent (T1).
            entity.HasIndex(x => new { x.TenantId, x.ExpiringContractId }).IsUnique()
                .HasDatabaseName("ux_contract_renewal_cases__expiring_contract");
            // Serves the renewal dashboard: a company's open cases by their next hard deadline.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.NextHardDeadline })
                .HasDatabaseName("ix_contract_renewal_cases__deadline");
            // The employee's own case (ESS, profile panel) is served by the expiring-contract FK index
            // (tenant_id, employee_id, expiring_contract_id), so no separate (tenant_id, employee_id) index is declared.
            entity.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeContract>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EmployeeId, x.ExpiringContractId })
                .HasPrincipalKey(x => new { x.TenantId, x.EmployeeId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeContract>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.EmployeeId, x.ResultingContractId })
                .HasPrincipalKey(x => new { x.TenantId, x.EmployeeId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApprovalRequest>().WithMany().HasForeignKey(x => new { x.TenantId, x.CurrentApprovalRequestId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApprovalRequest>().WithMany().HasForeignKey(x => new { x.TenantId, x.RenewalBatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StatutoryRule>().WithMany().HasForeignKey(x => x.QiwaRuleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.QiwaEvidenceDocumentId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.NonRenewalNoticeDocumentId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.EmployeeResponseDocumentId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
