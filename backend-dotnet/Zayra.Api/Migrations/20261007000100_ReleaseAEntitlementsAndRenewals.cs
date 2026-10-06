using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseAEntitlementsAndRenewals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Release A (rev 8.3.2) — the ONLY Release A migration (plan §2 R0). Expand phase: two new tables
            // (employee_entitlements, contract_renewal_cases) and nullable/defaulted columns on existing tables, so the
            // previous release keeps running against this schema (N-1). No data is written: seeds go through the seeders
            // and backfills through flag-gated app jobs. The grade_entitlements CHECKs are replaced by WIDER ones that
            // every L1 loan cell already satisfies. What EF cannot model is added at the end from the constants below,
            // which the Postgres test fixture applies too (ReleaseAMigrationPostgresTests proves the two agree).
            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__ineligible_has_no_values",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__value_shape",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__value_type",
                table: "grade_entitlements");

            migrationBuilder.AddColumn<bool>(
                name: "is_offered",
                table: "pay_components",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "after_probation",
                table: "grade_entitlements",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "coverage_tier",
                table: "grade_entitlements",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dependant_scope",
                table: "grade_entitlements",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "limit_period",
                table: "grade_entitlements",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "max_dependants",
                table: "grade_entitlements",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "min_service_months",
                table: "grade_entitlements",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nationality_basis",
                table: "grade_entitlements",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nationality_scope",
                table: "grade_entitlements",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Any");

            migrationBuilder.AddColumn<short>(
                name: "quantity",
                table: "grade_entitlements",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "housing_basis",
                table: "employee_salary_structures",
                type: "character varying(14)",
                maxLength: 14,
                nullable: false,
                defaultValue: "Amount");

            migrationBuilder.AddColumn<decimal>(
                name: "housing_rate",
                table: "employee_salary_structures",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "qiwa_confirmed_on",
                table: "employee_salary_structures",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "qiwa_evidence_document_id",
                table: "employee_salary_structures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "renewal_case_id",
                table: "employee_salary_structures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transport_basis",
                table: "employee_salary_structures",
                type: "character varying(14)",
                maxLength: 14,
                nullable: false,
                defaultValue: "Amount");

            migrationBuilder.AddColumn<decimal>(
                name: "transport_rate",
                table: "employee_salary_structures",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cap_base_wage",
                table: "employee_loans",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "consent_document_id",
                table: "employee_loans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_renew",
                table: "employee_contracts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "chain_started_on",
                table: "employee_contracts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "non_renewal_notice_days",
                table: "employee_contracts",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provisional_basis",
                table: "employee_contracts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "renewal_number",
                table: "employee_contracts",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "renewed_from_contract_id",
                table: "employee_contracts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "worker_nationality_class",
                table: "employee_contracts",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload",
                table: "approval_requests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload_sha256",
                table: "approval_requests",
                type: "character(64)",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_grade_entitlements_tenant_id_id_pay_component_code",
                table: "grade_entitlements",
                columns: new[] { "tenant_id", "id", "pay_component_code" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_employee_salary_structures_tenant_id_id",
                table: "employee_salary_structures",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_employee_documents_tenant_id_id",
                table: "employee_documents",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_employee_contracts_tenant_id_employee_id_id",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "employee_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_approval_requests_tenant_id_id",
                table: "approval_requests",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "contract_renewal_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiring_contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiring_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    worker_nationality_class = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    allowed_actions = table.Column<string[]>(type: "text[]", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recommended_action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    recommended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    manager_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    contract_action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    term_months = table.Column<short>(type: "smallint", nullable: true),
                    fallback_if_rejected = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    employee_response = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    employee_responded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    employee_responded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response_channel = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    employee_response_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_response_confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_acceptance_required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    hold_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    held_from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    renewal_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_version = table.Column<short>(type: "smallint", nullable: false),
                    offer_sha256 = table.Column<string>(type: "character(64)", nullable: true),
                    offer_cost_delta_monthly = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    notice_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    offer_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    qiwa_submit_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    qiwa_gate_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    qiwa_required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    qiwa_request_no = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    qiwa_sent_on = table.Column<DateOnly>(type: "date", nullable: true),
                    qiwa_attempts = table.Column<short>(type: "smallint", nullable: false),
                    qiwa_respond_by_on = table.Column<DateOnly>(type: "date", nullable: true),
                    qiwa_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qiwa_evidence_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qiwa_evidence_outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    qiwa_evidence_recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    qiwa_evidence_verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    non_renewal_notice_served_on = table.Column<DateOnly>(type: "date", nullable: true),
                    non_renewal_notice_channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    non_renewal_notice_by = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    non_renewal_notice_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resulting_contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_by = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    apply_idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    next_hard_deadline = table.Column<DateOnly>(type: "date", nullable: true, computedColumnSql: "CASE WHEN state IN ('Applied','NonRenewed','Cancelled') THEN NULL WHEN state IN ('NeedsConfirmation','Open','AwaitingManager','OfferInPreparation','InApproval') THEN offer_due_on WHEN state IN ('OfferSent','Accepted') THEN qiwa_submit_due_on WHEN state = 'QiwaPending' THEN CASE WHEN qiwa_respond_by_on IS NULL THEN qiwa_gate_due_on WHEN qiwa_gate_due_on IS NULL OR qiwa_respond_by_on < qiwa_gate_due_on THEN qiwa_respond_by_on ELSE qiwa_gate_due_on END WHEN state = 'ReadyToApply' THEN expiring_end_date ELSE notice_due_on END", stored: true),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_renewal_cases", x => x.id);
                    table.UniqueConstraint("AK_contract_renewal_cases_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_contract_renewal_cases__applied_has_result", "state <> 'Applied' OR (resulting_contract_id IS NOT NULL AND applied_at IS NOT NULL AND applied_by IS NOT NULL)");
                    table.CheckConstraint("ck_contract_renewal_cases__closed_iff_terminal", "(closed_at IS NULL) = (state NOT IN ('Applied','NonRenewed','Cancelled'))");
                    table.CheckConstraint("ck_contract_renewal_cases__deadlines_once_confirmed", "state IN ('NeedsConfirmation','OnHold','Cancelled') OR notice_due_on IS NOT NULL");
                    table.CheckConstraint("ck_contract_renewal_cases__employee_response", "employee_response IS NULL OR (employee_response IN ('Accepted','Declined','NoResponse') AND employee_responded_at IS NOT NULL AND response_channel IS NOT NULL)");
                    table.CheckConstraint("ck_contract_renewal_cases__fallback_if_rejected", "fallback_if_rejected IS NULL OR fallback_if_rejected IN ('RenewAsIs','NonRenew')");
                    table.CheckConstraint("ck_contract_renewal_cases__held_from_state", "held_from_state IS NULL OR held_from_state IN ('NeedsConfirmation','Open','AwaitingManager','OfferInPreparation','InApproval','OfferSent','Accepted','QiwaPending','ReadyToApply')");
                    table.CheckConstraint("ck_contract_renewal_cases__hold_iff_held_from", "(state = 'OnHold') = (held_from_state IS NOT NULL)");
                    table.CheckConstraint("ck_contract_renewal_cases__hold_iff_reason", "(state = 'OnHold') = (hold_reason IS NOT NULL)");
                    table.CheckConstraint("ck_contract_renewal_cases__hold_reason", "hold_reason IS NULL OR hold_reason IN ('Resignation','UnpaidLeave','Abroad','Transfer','LabourDispute')");
                    table.CheckConstraint("ck_contract_renewal_cases__non_renewed_on_time", "state <> 'NonRenewed' OR (non_renewal_notice_served_on IS NOT NULL AND notice_due_on IS NOT NULL AND non_renewal_notice_served_on <= notice_due_on)");
                    table.CheckConstraint("ck_contract_renewal_cases__notice_by", "non_renewal_notice_by IS NULL OR non_renewal_notice_by IN ('Employer','Employee')");
                    table.CheckConstraint("ck_contract_renewal_cases__notice_channel", "non_renewal_notice_channel IS NULL OR non_renewal_notice_channel IN ('Qiwa','Written')");
                    table.CheckConstraint("ck_contract_renewal_cases__offer_before_notice", "offer_due_on IS NULL OR notice_due_on IS NULL OR offer_due_on < notice_due_on");
                    table.CheckConstraint("ck_contract_renewal_cases__offer_version", "offer_version >= 0");
                    table.CheckConstraint("ck_contract_renewal_cases__paper_response_two_people", "response_channel IS NULL OR response_channel <> 'PaperUpload' OR employee_response_confirmed_by IS NULL OR (employee_responded_by_user_id IS NOT NULL AND employee_response_confirmed_by <> employee_responded_by_user_id)");
                    table.CheckConstraint("ck_contract_renewal_cases__qiwa_attempts", "qiwa_attempts >= 0");
                    table.CheckConstraint("ck_contract_renewal_cases__qiwa_evidence_outcome", "qiwa_evidence_outcome IS NULL OR qiwa_evidence_outcome IN ('Approved','Rejected','ChangesRequested','NoResponse')");
                    table.CheckConstraint("ck_contract_renewal_cases__qiwa_evidence_two_people", "qiwa_evidence_verified_by IS NULL OR (qiwa_evidence_recorded_by IS NOT NULL AND qiwa_evidence_verified_by <> qiwa_evidence_recorded_by)");
                    table.CheckConstraint("ck_contract_renewal_cases__recommended_action", "recommended_action IS NULL OR recommended_action IN ('RenewAsIs','RenewWithChanges','ConvertIndefinite','NonRenew')");
                    table.CheckConstraint("ck_contract_renewal_cases__response_channel", "response_channel IS NULL OR response_channel IN ('ESS','PaperUpload')");
                    table.CheckConstraint("ck_contract_renewal_cases__response_document_is_paper", "employee_response_document_id IS NULL OR (response_channel IS NOT NULL AND response_channel = 'PaperUpload')");
                    table.CheckConstraint("ck_contract_renewal_cases__state", "state IN ('NeedsConfirmation','Open','AwaitingManager','OfferInPreparation','InApproval','OfferSent','Accepted','QiwaPending','ReadyToApply','Applied','NonRenewed','Cancelled','OnHold')");
                    table.CheckConstraint("ck_contract_renewal_cases__term_months", "term_months IS NULL OR term_months > 0");
                    table.CheckConstraint("ck_contract_renewal_cases__worker_nationality_class", "worker_nationality_class IN ('Saudi','NonSaudi')");
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_approval_requests_tenant_id_current_~",
                        columns: x => new { x.tenant_id, x.current_approval_request_id },
                        principalTable: "approval_requests",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_approval_requests_tenant_id_renewal_~",
                        columns: x => new { x.tenant_id, x.renewal_batch_id },
                        principalTable: "approval_requests",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_employee_contracts_tenant_id_employe~",
                        columns: x => new { x.tenant_id, x.employee_id, x.expiring_contract_id },
                        principalTable: "employee_contracts",
                        principalColumns: new[] { "tenant_id", "employee_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_employee_contracts_tenant_id_employ~1",
                        columns: x => new { x.tenant_id, x.employee_id, x.resulting_contract_id },
                        principalTable: "employee_contracts",
                        principalColumns: new[] { "tenant_id", "employee_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_employee_documents_tenant_id_employe~",
                        columns: x => new { x.tenant_id, x.employee_response_document_id },
                        principalTable: "employee_documents",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_employee_documents_tenant_id_non_ren~",
                        columns: x => new { x.tenant_id, x.non_renewal_notice_document_id },
                        principalTable: "employee_documents",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_employee_documents_tenant_id_qiwa_ev~",
                        columns: x => new { x.tenant_id, x.qiwa_evidence_document_id },
                        principalTable: "employee_documents",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_renewal_cases_statutory_rules_qiwa_rule_id",
                        column: x => x.qiwa_rule_id,
                        principalTable: "statutory_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "employee_entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pay_component_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entitlement_class = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    rate = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    max_outstanding_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    coverage_tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    quantity = table.Column<short>(type: "smallint", nullable: true),
                    dependant_scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "None"),
                    max_dependants = table.Column<short>(type: "smallint", nullable: true),
                    limit_period = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    resolved_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    resolved_basis_salary_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verification_state = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false, defaultValue: "Verified"),
                    grade_entitlement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    renewal_case_id = table.Column<Guid>(type: "uuid", nullable: true),
                    carried_from_entitlement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correction_basis_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_entitlements", x => x.id);
                    table.UniqueConstraint("AK_employee_entitlements_tenant_id_employee_id_id", x => new { x.tenant_id, x.employee_id, x.id });
                    table.UniqueConstraint("AK_employee_entitlements_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_employee_entitlements__approval_iff_exception", "(source IN ('Exception','Correction')) = (approval_request_id IS NOT NULL)");
                    table.CheckConstraint("ck_employee_entitlements__carried_iff_origin", "(source = 'Carried') = (carried_from_entitlement_id IS NOT NULL)");
                    table.CheckConstraint("ck_employee_entitlements__correction_iff_document", "(source = 'Correction') = (correction_basis_document_id IS NOT NULL)");
                    table.CheckConstraint("ck_employee_entitlements__coverage_tier", "coverage_tier IS NULL OR coverage_tier IN ('CchiBasic','C','B','A','VIP','Economy','Business')");
                    table.CheckConstraint("ck_employee_entitlements__dates", "effective_to IS NULL OR effective_to >= effective_from");
                    table.CheckConstraint("ck_employee_entitlements__dependant_scope", "dependant_scope IN ('None','Spouse','Children','Family')");
                    table.CheckConstraint("ck_employee_entitlements__entitlement_class", "entitlement_class IN ('Contractual','Facility')");
                    table.CheckConstraint("ck_employee_entitlements__grade_default_cites_cell", "source <> 'GradeDefault' OR grade_entitlement_id IS NOT NULL");
                    table.CheckConstraint("ck_employee_entitlements__limit_period", "limit_period IS NULL OR limit_period IN ('PerTerm','Monthly','Annual','PerDay','Lifetime')");
                    table.CheckConstraint("ck_employee_entitlements__max_dependants", "max_dependants IS NULL OR (max_dependants >= 0 AND dependant_scope <> 'None')");
                    table.CheckConstraint("ck_employee_entitlements__outstanding_is_facility", "max_outstanding_amount IS NULL OR (entitlement_class = 'Facility' AND max_outstanding_amount >= 0)");
                    table.CheckConstraint("ck_employee_entitlements__percent_has_basis", "(value_type = 'PercentOfBasic') = (resolved_basis_salary_id IS NOT NULL)");
                    table.CheckConstraint("ck_employee_entitlements__quantity", "quantity IS NULL OR quantity > 0");
                    table.CheckConstraint("ck_employee_entitlements__resolved_amount", "resolved_amount IS NULL OR resolved_amount >= 0");
                    table.CheckConstraint("ck_employee_entitlements__source", "source IN ('GradeDefault','Exception','Correction','Migrated','Carried')");
                    table.CheckConstraint("ck_employee_entitlements__value_shape", "(value_type = 'Amount' AND amount IS NOT NULL AND amount >= 0 AND rate IS NULL) OR (value_type = 'PercentOfBasic' AND rate IS NOT NULL AND rate > 0 AND rate <= 1 AND amount IS NULL) OR (value_type IN ('MultipleOfBasic','MultipleOfGross','MultipleOfHousing') AND rate IS NOT NULL AND rate > 0 AND amount IS NULL) OR (value_type IN ('InKind','EligibilityOnly') AND amount IS NULL AND rate IS NULL) OR (value_type = 'CoverageTier' AND coverage_tier IS NOT NULL AND amount IS NULL AND rate IS NULL) OR (value_type = 'Quantity' AND quantity IS NOT NULL AND amount IS NULL AND rate IS NULL)");
                    table.CheckConstraint("ck_employee_entitlements__value_type", "value_type IN ('Amount','PercentOfBasic','MultipleOfBasic','MultipleOfGross','MultipleOfHousing','InKind','CoverageTier','Quantity','EligibilityOnly')");
                    table.CheckConstraint("ck_employee_entitlements__verification_state", "verification_state IN ('Unverified','Verified')");
                    table.ForeignKey(
                        name: "FK_employee_entitlements_approval_requests_tenant_id_approval_~",
                        columns: x => new { x.tenant_id, x.approval_request_id },
                        principalTable: "approval_requests",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_entitlements_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_entitlements_contract_renewal_cases_tenant_id_rene~",
                        columns: x => new { x.tenant_id, x.renewal_case_id },
                        principalTable: "contract_renewal_cases",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_entitlements_employee_contracts_tenant_id_employee~",
                        columns: x => new { x.tenant_id, x.employee_id, x.contract_id },
                        principalTable: "employee_contracts",
                        principalColumns: new[] { "tenant_id", "employee_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_entitlements_employee_documents_tenant_id_correcti~",
                        columns: x => new { x.tenant_id, x.correction_basis_document_id },
                        principalTable: "employee_documents",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_entitlements_employee_entitlements_tenant_id_emplo~",
                        columns: x => new { x.tenant_id, x.employee_id, x.carried_from_entitlement_id },
                        principalTable: "employee_entitlements",
                        principalColumns: new[] { "tenant_id", "employee_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_entitlements_employee_salary_structures_tenant_id_~",
                        columns: x => new { x.tenant_id, x.resolved_basis_salary_id },
                        principalTable: "employee_salary_structures",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_entitlements_grade_entitlements_tenant_id_grade_en~",
                        columns: x => new { x.tenant_id, x.grade_entitlement_id, x.pay_component_code },
                        principalTable: "grade_entitlements",
                        principalColumns: new[] { "tenant_id", "id", "pay_component_code" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__floor_always_offered",
                table: "pay_components",
                sql: "statutory_floor = 'None' OR is_offered");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__coverage_tier",
                table: "grade_entitlements",
                sql: "coverage_tier IS NULL OR coverage_tier IN ('CchiBasic','C','B','A','VIP','Economy','Business')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__dependant_scope",
                table: "grade_entitlements",
                sql: "dependant_scope IN ('None','Spouse','Children','Family')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__ineligible_has_no_values",
                table: "grade_entitlements",
                sql: "eligible OR (amount IS NULL AND rate IS NULL AND max_outstanding_amount IS NULL AND coverage_tier IS NULL AND quantity IS NULL AND max_dependants IS NULL AND dependant_scope = 'None')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__limit_period",
                table: "grade_entitlements",
                sql: "limit_period IS NULL OR limit_period IN ('PerTerm','Monthly','Annual','PerDay','Lifetime')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__max_dependants",
                table: "grade_entitlements",
                sql: "max_dependants IS NULL OR (max_dependants >= 0 AND dependant_scope <> 'None')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__min_service_months",
                table: "grade_entitlements",
                sql: "min_service_months IS NULL OR min_service_months >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__nationality_has_basis",
                table: "grade_entitlements",
                sql: "nationality_scope = 'Any' OR (nationality_basis IS NOT NULL AND trim(nationality_basis) <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__nationality_scope",
                table: "grade_entitlements",
                sql: "nationality_scope IN ('Any','Saudi','NonSaudi')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__quantity",
                table: "grade_entitlements",
                sql: "quantity IS NULL OR quantity > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__value_shape",
                table: "grade_entitlements",
                sql: "(value_type = 'Amount' AND amount IS NOT NULL AND amount >= 0 AND rate IS NULL) OR (value_type = 'PercentOfBasic' AND rate IS NOT NULL AND rate > 0 AND rate <= 1 AND amount IS NULL) OR (value_type IN ('MultipleOfBasic','MultipleOfGross','MultipleOfHousing') AND rate IS NOT NULL AND rate > 0 AND amount IS NULL) OR (value_type IN ('InKind','EligibilityOnly') AND amount IS NULL AND rate IS NULL) OR (value_type = 'CoverageTier' AND coverage_tier IS NOT NULL AND amount IS NULL AND rate IS NULL) OR (value_type = 'Quantity' AND quantity IS NOT NULL AND amount IS NULL AND rate IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__value_type",
                table: "grade_entitlements",
                sql: "value_type IN ('Amount','PercentOfBasic','MultipleOfBasic','MultipleOfGross','MultipleOfHousing','InKind','CoverageTier','Quantity','EligibilityOnly')");

            migrationBuilder.CreateIndex(
                name: "IX_employee_salary_structures_tenant_id_qiwa_evidence_document~",
                table: "employee_salary_structures",
                columns: new[] { "tenant_id", "qiwa_evidence_document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_salary_structures_tenant_id_renewal_case_id",
                table: "employee_salary_structures",
                columns: new[] { "tenant_id", "renewal_case_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__housing_amount_matches_basis",
                table: "employee_salary_structures",
                sql: "(housing_basis <> 'PercentOfBasic' OR housing_allowance = round(basic_salary * housing_rate, 2)) AND (housing_basis <> 'InKind' OR housing_allowance = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__housing_basis",
                table: "employee_salary_structures",
                sql: "housing_basis IN ('Amount','PercentOfBasic','InKind')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__housing_rate_pair",
                table: "employee_salary_structures",
                sql: "(housing_basis = 'PercentOfBasic') = (housing_rate IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__housing_rate_range",
                table: "employee_salary_structures",
                sql: "housing_rate IS NULL OR (housing_rate > 0 AND housing_rate <= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__qiwa_evidence_dated",
                table: "employee_salary_structures",
                sql: "qiwa_evidence_document_id IS NULL OR qiwa_confirmed_on IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__transport_amount_matches_basis",
                table: "employee_salary_structures",
                sql: "(transport_basis <> 'PercentOfBasic' OR transport_allowance = round(basic_salary * transport_rate, 2)) AND (transport_basis <> 'InKind' OR transport_allowance = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__transport_basis",
                table: "employee_salary_structures",
                sql: "transport_basis IN ('Amount','PercentOfBasic','InKind')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__transport_rate_pair",
                table: "employee_salary_structures",
                sql: "(transport_basis = 'PercentOfBasic') = (transport_rate IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_salary_structures__transport_rate_range",
                table: "employee_salary_structures",
                sql: "transport_rate IS NULL OR (transport_rate > 0 AND transport_rate <= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_employee_loans_tenant_id_consent_document_id",
                table: "employee_loans",
                columns: new[] { "tenant_id", "consent_document_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_loans__cap_base_wage",
                table: "employee_loans",
                sql: "cap_base_wage IS NULL OR cap_base_wage >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_employee_contracts__end_date",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "end_date" },
                filter: "end_date IS NOT NULL AND NOT is_deleted");

            migrationBuilder.CreateIndex(
                name: "IX_employee_contracts_tenant_id_employee_id_renewed_from_contr~",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "employee_id", "renewed_from_contract_id" });

            migrationBuilder.CreateIndex(
                name: "ux_employee_contracts__renewed_from",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "renewed_from_contract_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_contracts__non_renewal_notice_days",
                table: "employee_contracts",
                sql: "non_renewal_notice_days IS NULL OR non_renewal_notice_days >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_contracts__not_renewed_from_itself",
                table: "employee_contracts",
                sql: "renewed_from_contract_id IS NULL OR renewed_from_contract_id <> id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_contracts__provisional_basis",
                table: "employee_contracts",
                sql: "provisional_basis IS NULL OR provisional_basis IN ('DeemedRenewal','Art55Indefinite')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_contracts__provisional_has_no_renewal_number",
                table: "employee_contracts",
                sql: "provisional_basis IS NULL OR renewal_number IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_contracts__renewal_number",
                table: "employee_contracts",
                sql: "renewal_number IS NULL OR renewal_number >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_contracts__worker_nationality_class",
                table: "employee_contracts",
                sql: "worker_nationality_class IS NULL OR worker_nationality_class IN ('Saudi','NonSaudi')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_approval_requests__payload_pair",
                table: "approval_requests",
                sql: "(payload IS NULL) = (payload_sha256 IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_contract_renewal_cases__deadline",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "company_id", "next_hard_deadline" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_company_id",
                table: "contract_renewal_cases",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_qiwa_rule_id",
                table: "contract_renewal_cases",
                column: "qiwa_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_company_id",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_current_approval_request_id",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "current_approval_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_employee_id_expiring_contr~",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "employee_id", "expiring_contract_id" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_employee_id_resulting_cont~",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "employee_id", "resulting_contract_id" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_employee_response_document~",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "employee_response_document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_non_renewal_notice_documen~",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "non_renewal_notice_document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_qiwa_evidence_document_id",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "qiwa_evidence_document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_renewal_cases_tenant_id_renewal_batch_id",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "renewal_batch_id" });

            migrationBuilder.CreateIndex(
                name: "ux_contract_renewal_cases__expiring_contract",
                table: "contract_renewal_cases",
                columns: new[] { "tenant_id", "expiring_contract_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employee_entitlements__as_of",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "employee_id", "effective_from" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_company_id",
                table: "employee_entitlements",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_tenant_id_company_id",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_tenant_id_correction_basis_document_id",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "correction_basis_document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_tenant_id_employee_id_carried_from_en~",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "employee_id", "carried_from_entitlement_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_tenant_id_employee_id_contract_id",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "employee_id", "contract_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_tenant_id_grade_entitlement_id_pay_co~",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "grade_entitlement_id", "pay_component_code" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_tenant_id_renewal_case_id",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "renewal_case_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_entitlements_tenant_id_resolved_basis_salary_id",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "resolved_basis_salary_id" });

            migrationBuilder.CreateIndex(
                name: "ux_employee_entitlements__approval_component",
                table: "employee_entitlements",
                columns: new[] { "tenant_id", "approval_request_id", "pay_component_code" },
                unique: true,
                filter: "carried_from_entitlement_id IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_employee_contracts_employee_contracts_tenant_id_employee_id~",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "employee_id", "renewed_from_contract_id" },
                principalTable: "employee_contracts",
                principalColumns: new[] { "tenant_id", "employee_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_employee_loans_employee_documents_tenant_id_consent_documen~",
                table: "employee_loans",
                columns: new[] { "tenant_id", "consent_document_id" },
                principalTable: "employee_documents",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_employee_salary_structures_contract_renewal_cases_tenant_id~",
                table: "employee_salary_structures",
                columns: new[] { "tenant_id", "renewal_case_id" },
                principalTable: "contract_renewal_cases",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_employee_salary_structures_employee_documents_tenant_id_qiw~",
                table: "employee_salary_structures",
                columns: new[] { "tenant_id", "qiwa_evidence_document_id" },
                principalTable: "employee_documents",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(AddEmployeePublicIdForeignKeysSql);
            migrationBuilder.Sql(AddEntitlementExclusionSql);
            migrationBuilder.Sql(AddPostgresOnlyChecksSql);
            migrationBuilder.Sql(CreateEntitlementCloseOnlyTriggerSql);
            migrationBuilder.Sql(CreateEntitlementContainmentTriggersSql);
            migrationBuilder.Sql(CreateRenewalTransitionGuardSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never roll back over evidence: a frozen package, a renewal case, a skipped benefit, a contract chain, a
            // Qiwa confirmation, an approval witness or a loan consent is history. Refuse, and fix forward instead.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM employee_entitlements)
                        OR EXISTS (SELECT 1 FROM contract_renewal_cases)
                        OR EXISTS (SELECT 1 FROM pay_components WHERE NOT is_offered)
                        OR EXISTS (SELECT 1 FROM employee_contracts WHERE renewed_from_contract_id IS NOT NULL OR renewal_number IS NOT NULL
                                   OR chain_started_on IS NOT NULL OR worker_nationality_class IS NOT NULL OR non_renewal_notice_days IS NOT NULL
                                   OR provisional_basis IS NOT NULL OR NOT auto_renew)
                        OR EXISTS (SELECT 1 FROM employee_salary_structures WHERE housing_basis <> 'Amount' OR transport_basis <> 'Amount'
                                   OR qiwa_confirmed_on IS NOT NULL OR qiwa_evidence_document_id IS NOT NULL OR renewal_case_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM approval_requests WHERE payload IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM employee_loans WHERE consent_document_id IS NOT NULL OR cap_base_wage IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM grade_entitlements WHERE value_type NOT IN ('Amount','MultipleOfBasic','MultipleOfGross','EligibilityOnly')
                                   OR coverage_tier IS NOT NULL OR quantity IS NOT NULL OR max_dependants IS NOT NULL OR limit_period IS NOT NULL
                                   OR min_service_months IS NOT NULL OR nationality_basis IS NOT NULL OR dependant_scope <> 'None'
                                   OR after_probation OR nationality_scope <> 'Any') THEN
                        RAISE EXCEPTION 'Release A entitlement or renewal data exists. Preserve it and use a forward corrective migration.';
                    END IF;
                END $$;
                """);
            // The trigger on employee_contracts outlives the two tables; triggers on the tables go with them.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_employee_contracts__entitlement_containment ON employee_contracts;");

            migrationBuilder.DropForeignKey(
                name: "FK_employee_contracts_employee_contracts_tenant_id_employee_id~",
                table: "employee_contracts");

            migrationBuilder.DropForeignKey(
                name: "FK_employee_loans_employee_documents_tenant_id_consent_documen~",
                table: "employee_loans");

            migrationBuilder.DropForeignKey(
                name: "FK_employee_salary_structures_contract_renewal_cases_tenant_id~",
                table: "employee_salary_structures");

            migrationBuilder.DropForeignKey(
                name: "FK_employee_salary_structures_employee_documents_tenant_id_qiw~",
                table: "employee_salary_structures");

            migrationBuilder.DropTable(
                name: "employee_entitlements");

            migrationBuilder.DropTable(
                name: "contract_renewal_cases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_components__floor_always_offered",
                table: "pay_components");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_grade_entitlements_tenant_id_id_pay_component_code",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__coverage_tier",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__dependant_scope",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__ineligible_has_no_values",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__limit_period",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__max_dependants",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__min_service_months",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__nationality_has_basis",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__nationality_scope",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__quantity",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__value_shape",
                table: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grade_entitlements__value_type",
                table: "grade_entitlements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_employee_salary_structures_tenant_id_id",
                table: "employee_salary_structures");

            migrationBuilder.DropIndex(
                name: "IX_employee_salary_structures_tenant_id_qiwa_evidence_document~",
                table: "employee_salary_structures");

            migrationBuilder.DropIndex(
                name: "IX_employee_salary_structures_tenant_id_renewal_case_id",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__housing_amount_matches_basis",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__housing_basis",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__housing_rate_pair",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__housing_rate_range",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__qiwa_evidence_dated",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__transport_amount_matches_basis",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__transport_basis",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__transport_rate_pair",
                table: "employee_salary_structures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_salary_structures__transport_rate_range",
                table: "employee_salary_structures");

            migrationBuilder.DropIndex(
                name: "IX_employee_loans_tenant_id_consent_document_id",
                table: "employee_loans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_loans__cap_base_wage",
                table: "employee_loans");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_employee_documents_tenant_id_id",
                table: "employee_documents");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_employee_contracts_tenant_id_employee_id_id",
                table: "employee_contracts");

            migrationBuilder.DropIndex(
                name: "ix_employee_contracts__end_date",
                table: "employee_contracts");

            migrationBuilder.DropIndex(
                name: "IX_employee_contracts_tenant_id_employee_id_renewed_from_contr~",
                table: "employee_contracts");

            migrationBuilder.DropIndex(
                name: "ux_employee_contracts__renewed_from",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__non_renewal_notice_days",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__not_renewed_from_itself",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__provisional_basis",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__provisional_has_no_renewal_number",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__renewal_number",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__worker_nationality_class",
                table: "employee_contracts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_approval_requests_tenant_id_id",
                table: "approval_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_approval_requests__payload_pair",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "is_offered",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "after_probation",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "coverage_tier",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "dependant_scope",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "limit_period",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "max_dependants",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "min_service_months",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "nationality_basis",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "nationality_scope",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "quantity",
                table: "grade_entitlements");

            migrationBuilder.DropColumn(
                name: "housing_basis",
                table: "employee_salary_structures");

            migrationBuilder.DropColumn(
                name: "housing_rate",
                table: "employee_salary_structures");

            migrationBuilder.DropColumn(
                name: "qiwa_confirmed_on",
                table: "employee_salary_structures");

            migrationBuilder.DropColumn(
                name: "qiwa_evidence_document_id",
                table: "employee_salary_structures");

            migrationBuilder.DropColumn(
                name: "renewal_case_id",
                table: "employee_salary_structures");

            migrationBuilder.DropColumn(
                name: "transport_basis",
                table: "employee_salary_structures");

            migrationBuilder.DropColumn(
                name: "transport_rate",
                table: "employee_salary_structures");

            migrationBuilder.DropColumn(
                name: "cap_base_wage",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "consent_document_id",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "auto_renew",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "chain_started_on",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "non_renewal_notice_days",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "provisional_basis",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "renewal_number",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "renewed_from_contract_id",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "worker_nationality_class",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "payload",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "payload_sha256",
                table: "approval_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__ineligible_has_no_values",
                table: "grade_entitlements",
                sql: "eligible OR (amount IS NULL AND rate IS NULL AND max_outstanding_amount IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__value_shape",
                table: "grade_entitlements",
                sql: "(value_type = 'Amount' AND amount IS NOT NULL AND amount >= 0 AND rate IS NULL) OR (value_type IN ('MultipleOfBasic','MultipleOfGross') AND rate IS NOT NULL AND rate > 0 AND amount IS NULL) OR (value_type = 'EligibilityOnly' AND amount IS NULL AND rate IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grade_entitlements__value_type",
                table: "grade_entitlements",
                sql: "value_type IN ('Amount','MultipleOfBasic','MultipleOfGross','EligibilityOnly')");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS employee_entitlements_close_only();
                DROP FUNCTION IF EXISTS employee_entitlements_containment();
                DROP FUNCTION IF EXISTS employee_contracts_entitlement_containment();
                DROP FUNCTION IF EXISTS contract_renewal_cases_transition_guard();
                """);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
        // The DDL EF cannot model. FROZEN history: never edit these — a change is a new migration with its own
        // constants. The Postgres test fixture applies exactly this list after EnsureCreated, so every integration
        // test runs against the constraints production gets.
        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The order the fixture applies them in (the same order Up does).</summary>
        internal static readonly string[] PostgresOnlyDdl =
        [
            AddEmployeePublicIdForeignKeysSql,
            AddEntitlementExclusionSql,
            AddPostgresOnlyChecksSql,
            CreateEntitlementCloseOnlyTriggerSql,
            CreateEntitlementContainmentTriggersSql,
            CreateRenewalTransitionGuardSql,
        ];

        /// <summary>Both new tables store the employee's PublicId (as employee_contracts does). The FK targets the existing
        /// unique index ux_employees_tenant_public_id, which EF cannot name as a principal key without a duplicate AK.</summary>
        internal const string AddEmployeePublicIdForeignKeysSql = """
            ALTER TABLE employee_entitlements ADD CONSTRAINT fk_employee_entitlements__employee
                FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, public_id) ON DELETE RESTRICT;
            ALTER TABLE contract_renewal_cases ADD CONSTRAINT fk_contract_renewal_cases__employee
                FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, public_id) ON DELETE RESTRICT;
            """;

        internal const string EntitlementExclusionName = "ex_employee_entitlements__no_overlap";

        /// <summary>One row per employee per component per day (btree_gist was installed by AddGradeLoanLimits).</summary>
        internal const string AddEntitlementExclusionSql = """
            ALTER TABLE employee_entitlements ADD CONSTRAINT ex_employee_entitlements__no_overlap EXCLUDE USING gist (
                tenant_id WITH =, employee_id WITH =, pay_component_code WITH =,
                daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&);
            """;

        /// <summary>The CHECKs only PostgreSQL can express (regex, array operators). They are kept out of the EF model so the
        /// SQLite and InMemory schemas the unit suite builds from it stay valid; on PostgreSQL they are as binding as the rest.</summary>
        internal const string AddPostgresOnlyChecksSql = """
            ALTER TABLE approval_requests ADD CONSTRAINT ck_approval_requests__payload_sha256
                CHECK (payload_sha256 IS NULL OR payload_sha256 ~ '^[0-9a-f]{64}$');
            ALTER TABLE contract_renewal_cases ADD CONSTRAINT ck_contract_renewal_cases__offer_sha256
                CHECK (offer_sha256 IS NULL OR offer_sha256 ~ '^[0-9a-f]{64}$');
            ALTER TABLE contract_renewal_cases ADD CONSTRAINT ck_contract_renewal_cases__allowed_actions
                CHECK (allowed_actions <@ ARRAY['RenewAsIs','RenewWithChanges','ConvertIndefinite','NonRenew']::text[]);
            ALTER TABLE contract_renewal_cases ADD CONSTRAINT ck_contract_renewal_cases__non_saudi_never_converts
                CHECK (worker_nationality_class = 'Saudi' OR NOT ('ConvertIndefinite' = ANY(allowed_actions)));
            ALTER TABLE contract_renewal_cases ADD CONSTRAINT ck_contract_renewal_cases__action_allowed
                CHECK (contract_action IS NULL OR contract_action = ANY(allowed_actions));
            """;

        /// <summary>Close-only: values never change; effective_to may only be shortened; a migrated row may be confirmed
        /// once (Unverified → Verified). A frozen row is never deleted. Errors start with a stable code for the API.</summary>
        internal const string CreateEntitlementCloseOnlyTriggerSql = """
            CREATE OR REPLACE FUNCTION employee_entitlements_close_only() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'ENTITLEMENT_CLOSE_ONLY: entitlement % is history and is never removed; shorten its effective_to instead', OLD.id
                        USING ERRCODE = '23514';
                END IF;
                IF (to_jsonb(NEW) - 'effective_to' - 'verification_state') IS DISTINCT FROM (to_jsonb(OLD) - 'effective_to' - 'verification_state') THEN
                    RAISE EXCEPTION 'ENTITLEMENT_CLOSE_ONLY: entitlement % is frozen; close it and write a new row', OLD.id
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.verification_state IS DISTINCT FROM OLD.verification_state
                   AND NOT (OLD.verification_state = 'Unverified' AND NEW.verification_state = 'Verified') THEN
                    RAISE EXCEPTION 'ENTITLEMENT_CLOSE_ONLY: entitlement % can only be confirmed, from Unverified to Verified', OLD.id
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.effective_to IS DISTINCT FROM OLD.effective_to
                   AND (NEW.effective_to IS NULL OR (OLD.effective_to IS NOT NULL AND NEW.effective_to > OLD.effective_to)) THEN
                    RAISE EXCEPTION 'ENTITLEMENT_CLOSE_ONLY: entitlement % may only be shortened, never extended or reopened', OLD.id
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END
            $fn$;
            CREATE TRIGGER trg_employee_entitlements__close_only
                BEFORE UPDATE OR DELETE ON employee_entitlements
                FOR EACH ROW EXECUTE FUNCTION employee_entitlements_close_only();
            """;

        /// <summary>
        /// Two-sided containment (rev 8.3.2 §3), deferred so statement order inside Apply cannot cause a false failure:
        /// a row lies inside [start_date, end_date] of its contract, is written onto a contract that is neither Draft nor
        /// Superseded nor deleted, for the same company (DEVIATION: a trigger, not a composite FK, because live
        /// employee_contracts.company_id is nullable). A PercentOfBasic witness must be this employee's salary row. A
        /// Carried row equals its origin (rev 8.3.2 §11) and starts after it ends. Changing a contract's dates or company
        /// re-checks its rows.
        /// </summary>
        internal const string CreateEntitlementContainmentTriggersSql = """
            CREATE OR REPLACE FUNCTION employee_entitlements_containment() RETURNS trigger LANGUAGE plpgsql AS $fn$
            DECLARE
                c record;
                o record;
                basic numeric;
            BEGIN
                SELECT status, start_date, end_date, company_id, is_deleted INTO c
                  FROM employee_contracts
                 WHERE tenant_id = NEW.tenant_id AND employee_id = NEW.employee_id AND id = NEW.contract_id
                   FOR SHARE;
                -- Written only onto a term in force. Checked when the row is written, not when it is later shortened:
                -- closing the rows of a term that has since been superseded must stay possible.
                IF NOT FOUND OR (TG_OP = 'INSERT' AND (c.is_deleted OR c.status IN ('Draft', 'Superseded'))) THEN
                    RAISE EXCEPTION 'ENTITLEMENT_CONTRACT_NOT_IN_FORCE: entitlement % belongs to a contract that is draft, superseded or deleted', NEW.id
                        USING ERRCODE = '23514';
                END IF;
                IF c.company_id IS DISTINCT FROM NEW.company_id THEN
                    RAISE EXCEPTION 'ENTITLEMENT_COMPANY_MISMATCH: entitlement % is not for the company on its contract', NEW.id
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.effective_from < c.start_date
                   OR COALESCE(NEW.effective_to, 'infinity'::date) > COALESCE(c.end_date, 'infinity'::date) THEN
                    RAISE EXCEPTION 'ENTITLEMENT_OUTSIDE_TERM: entitlement % runs outside its contract term', NEW.id
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.resolved_basis_salary_id IS NOT NULL THEN
                    SELECT s.basic_salary INTO basic
                      FROM employee_salary_structures s
                      JOIN employees e ON e.tenant_id = s.tenant_id AND e.id = s.employee_id
                     WHERE s.tenant_id = NEW.tenant_id AND s.id = NEW.resolved_basis_salary_id AND e.public_id = NEW.employee_id;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'ENTITLEMENT_BASIS_NOT_OWN_SALARY: entitlement % cites a salary row of another employee', NEW.id
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
                IF NEW.carried_from_entitlement_id IS NOT NULL THEN
                    SELECT * INTO o FROM employee_entitlements
                     WHERE tenant_id = NEW.tenant_id AND employee_id = NEW.employee_id AND id = NEW.carried_from_entitlement_id;
                    IF o.pay_component_code <> NEW.pay_component_code OR o.value_type <> NEW.value_type
                       OR o.entitlement_class <> NEW.entitlement_class OR o.rate IS DISTINCT FROM NEW.rate
                       OR o.coverage_tier IS DISTINCT FROM NEW.coverage_tier OR o.quantity IS DISTINCT FROM NEW.quantity
                       OR o.dependant_scope <> NEW.dependant_scope OR o.max_dependants IS DISTINCT FROM NEW.max_dependants
                       OR o.limit_period IS DISTINCT FROM NEW.limit_period
                       OR o.max_outstanding_amount IS DISTINCT FROM NEW.max_outstanding_amount
                       OR (NEW.value_type <> 'PercentOfBasic'
                           AND (o.amount IS DISTINCT FROM NEW.amount OR o.resolved_amount IS DISTINCT FROM NEW.resolved_amount))
                       OR (NEW.value_type = 'PercentOfBasic' AND NEW.resolved_amount IS DISTINCT FROM round(basic * NEW.rate, 2)) THEN
                        RAISE EXCEPTION 'ENTITLEMENT_CARRIED_DIFFERS: carried entitlement % does not equal the row it carries', NEW.id
                            USING ERRCODE = '23514';
                    END IF;
                    IF o.effective_to IS NULL OR o.effective_to >= NEW.effective_from THEN
                        RAISE EXCEPTION 'ENTITLEMENT_CARRIED_OVERLAPS: carried entitlement % must start after the row it carries ends', NEW.id
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
                RETURN NULL;
            END
            $fn$;
            CREATE CONSTRAINT TRIGGER trg_employee_entitlements__containment
                AFTER INSERT OR UPDATE ON employee_entitlements
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION employee_entitlements_containment();

            CREATE OR REPLACE FUNCTION employee_contracts_entitlement_containment() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM employee_entitlements e
                     WHERE e.tenant_id = NEW.tenant_id AND e.employee_id = NEW.employee_id AND e.contract_id = NEW.id
                       AND (e.effective_from < NEW.start_date
                            OR COALESCE(e.effective_to, 'infinity'::date) > COALESCE(NEW.end_date, 'infinity'::date)
                            OR e.company_id IS DISTINCT FROM NEW.company_id)) THEN
                    RAISE EXCEPTION 'ENTITLEMENT_OUTSIDE_TERM: contract % would leave its entitlements outside the term or company; close them first', NEW.id
                        USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END
            $fn$;
            CREATE CONSTRAINT TRIGGER trg_employee_contracts__entitlement_containment
                AFTER UPDATE OF start_date, end_date, company_id ON employee_contracts
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION employee_contracts_entitlement_containment();
            """;

        /// <summary>
        /// The money and legal path of the renewal state machine (rev T11), enforced by the database:
        /// <list type="bullet">
        /// <item>a case is created Open or NeedsConfirmation, for the company and tenant of its expiring contract, and
        /// its identity (tenant, company, employee, expiring contract) never changes;</item>
        /// <item>a closed case never moves; a hold records where it came from and is released back there (or cancelled);</item>
        /// <item>Accepted only from OfferSent, by a response to the offer version that was sent — in the app by the
        /// employee, or on paper with the signed document confirmed by a second user;</item>
        /// <item>ReadyToApply only from Accepted when no Qiwa step is required, or from QiwaPending with verified approving
        /// evidence (renewal) or a notice served by the notice date (non-renewal);</item>
        /// <item>Applied only from ReadyToApply, with its result and idempotency key, an Approved approval whose payload
        /// hash is the offer's (or an Approved batch), the employee's acceptance unless it was waived, and verified Qiwa
        /// evidence when required; NonRenewed only from ReadyToApply, approved, with the notice served on time.</item>
        /// </list>
        /// Errors start with a block-reason code from ReleaseABlockReasons (or RENEWAL_TRANSITION) for the API to map.
        /// </summary>
        internal const string CreateRenewalTransitionGuardSql = """
            CREATE OR REPLACE FUNCTION contract_renewal_cases_transition_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
            DECLARE
                a record;
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    IF NEW.state NOT IN ('Open', 'NeedsConfirmation') THEN
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: a renewal case opens as Open or NeedsConfirmation, not %', NEW.state
                            USING ERRCODE = '23514';
                    END IF;
                    PERFORM 1 FROM employee_contracts c
                     WHERE c.tenant_id = NEW.tenant_id AND c.employee_id = NEW.employee_id AND c.id = NEW.expiring_contract_id
                       AND c.company_id = NEW.company_id;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'RENEWAL_NO_COMPANY: case % is not for the company on its expiring contract', NEW.id
                            USING ERRCODE = '23514';
                    END IF;
                    PERFORM 1 FROM companies co WHERE co.id = NEW.company_id AND co.tenant_id = NEW.tenant_id;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'RENEWAL_NO_COMPANY: case % names a company of another tenant', NEW.id
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;

                IF (NEW.tenant_id, NEW.company_id, NEW.employee_id, NEW.expiring_contract_id)
                   IS DISTINCT FROM (OLD.tenant_id, OLD.company_id, OLD.employee_id, OLD.expiring_contract_id) THEN
                    RAISE EXCEPTION 'RENEWAL_TRANSITION: the tenant, company, employee and expiring contract of case % are fixed', OLD.id
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.state IS NOT DISTINCT FROM OLD.state THEN
                    RETURN NEW;
                END IF;
                IF OLD.state IN ('Applied', 'NonRenewed', 'Cancelled') THEN
                    RAISE EXCEPTION 'RENEWAL_TRANSITION: case % is closed (%) and cannot move to %', OLD.id, OLD.state, NEW.state
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.state = 'OnHold' THEN
                    IF NEW.held_from_state IS DISTINCT FROM OLD.state THEN
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: a hold on case % must record the state it was taken from (%)', OLD.id, OLD.state
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;
                IF OLD.state = 'OnHold' THEN
                    IF NEW.state <> 'Cancelled' AND NEW.state IS DISTINCT FROM OLD.held_from_state THEN
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: case % was held from % and is released back there, not to %', OLD.id, OLD.held_from_state, NEW.state
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;

                IF NEW.state = 'Accepted' THEN
                    IF OLD.state <> 'OfferSent' OR NEW.employee_response IS DISTINCT FROM 'Accepted'
                       OR NEW.employee_responded_at IS NULL OR NEW.employee_responded_by_user_id IS NULL OR NEW.response_channel IS NULL THEN
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: case % can be accepted only from OfferSent, by a recorded response', OLD.id
                            USING ERRCODE = '23514';
                    END IF;
                    IF NEW.offer_sha256 IS NULL OR NEW.offer_sha256 IS DISTINCT FROM OLD.offer_sha256 THEN
                        RAISE EXCEPTION 'RENEWAL_OFFER_STALE: case % was accepted against an offer version that was not the one sent', OLD.id
                            USING ERRCODE = '23514';
                    END IF;
                    IF NEW.response_channel = 'PaperUpload'
                       AND (NEW.employee_response_document_id IS NULL OR NEW.employee_response_confirmed_by IS NULL
                            OR NEW.employee_response_confirmed_by = NEW.employee_responded_by_user_id) THEN
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: a paper acceptance on case % needs the signed document and a second user''s confirmation', OLD.id
                            USING ERRCODE = '23514';
                    END IF;
                ELSIF NEW.state = 'ReadyToApply' THEN
                    IF OLD.state = 'Accepted' THEN
                        IF NEW.qiwa_required THEN
                            RAISE EXCEPTION 'APPLY_QIWA_EVIDENCE_MISSING: case % needs its Qiwa step before it is ready to apply', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                    ELSIF OLD.state = 'QiwaPending' THEN
                        IF NEW.contract_action = 'NonRenew' THEN
                            IF NEW.non_renewal_notice_served_on IS NULL OR NEW.notice_due_on IS NULL
                               OR NEW.non_renewal_notice_served_on > NEW.notice_due_on THEN
                                RAISE EXCEPTION 'RENEWAL_NOTICE_SERVED_LATE: case % has no non-renewal notice served by the notice date', OLD.id
                                    USING ERRCODE = '23514';
                            END IF;
                        ELSIF NEW.qiwa_evidence_document_id IS NULL OR NEW.qiwa_evidence_verified_by IS NULL
                              OR NEW.qiwa_evidence_outcome IS DISTINCT FROM 'Approved' THEN
                            RAISE EXCEPTION 'APPLY_QIWA_EVIDENCE_MISSING: case % needs verified Qiwa evidence of approval', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: case % becomes ready to apply only from Accepted or QiwaPending, not %', OLD.id, OLD.state
                            USING ERRCODE = '23514';
                    END IF;
                ELSIF NEW.state IN ('Applied', 'NonRenewed') THEN
                    IF OLD.state <> 'ReadyToApply' THEN
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: case % is applied only from ReadyToApply, not %', OLD.id, OLD.state
                            USING ERRCODE = '23514';
                    END IF;
                    IF NEW.current_approval_request_id IS NOT NULL THEN
                        SELECT status, payload_sha256 INTO a FROM approval_requests
                         WHERE tenant_id = NEW.tenant_id AND id = NEW.current_approval_request_id;
                        IF a.status IS DISTINCT FROM 'Approved' OR a.payload_sha256 IS DISTINCT FROM NEW.offer_sha256 THEN
                            RAISE EXCEPTION 'RENEWAL_OFFER_STALE: case % was not approved in the version being applied', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                    ELSIF NEW.renewal_batch_id IS NOT NULL THEN
                        SELECT status INTO a FROM approval_requests WHERE tenant_id = NEW.tenant_id AND id = NEW.renewal_batch_id;
                        IF a.status IS DISTINCT FROM 'Approved' THEN
                            RAISE EXCEPTION 'RENEWAL_TRANSITION: the batch approval of case % is not approved', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        RAISE EXCEPTION 'RENEWAL_TRANSITION: case % has no approval to apply', OLD.id
                            USING ERRCODE = '23514';
                    END IF;
                    IF NEW.state = 'Applied' THEN
                        IF NEW.contract_action IS NULL OR NEW.contract_action NOT IN ('RenewAsIs', 'RenewWithChanges', 'ConvertIndefinite')
                           OR NEW.resulting_contract_id IS NULL OR NEW.applied_by IS NULL OR NEW.applied_at IS NULL
                           OR NEW.apply_idempotency_key IS NULL THEN
                            RAISE EXCEPTION 'RENEWAL_TRANSITION: case % is applied with its new term, its applier and an idempotency key', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                        IF NEW.employee_acceptance_required AND NEW.employee_response IS DISTINCT FROM 'Accepted' THEN
                            RAISE EXCEPTION 'RENEWAL_TRANSITION: case % needs the employee''s acceptance before it is applied', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                        IF NEW.qiwa_required AND (NEW.qiwa_evidence_document_id IS NULL OR NEW.qiwa_evidence_verified_by IS NULL
                                                  OR NEW.qiwa_evidence_outcome IS DISTINCT FROM 'Approved') THEN
                            RAISE EXCEPTION 'APPLY_QIWA_EVIDENCE_MISSING: case % needs verified Qiwa evidence of approval before it is applied', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF NEW.contract_action IS DISTINCT FROM 'NonRenew' OR NEW.non_renewal_notice_document_id IS NULL THEN
                            RAISE EXCEPTION 'RENEWAL_TRANSITION: case % ends in non-renewal only as a NonRenew action with its notice document', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                        IF NEW.non_renewal_notice_served_on IS NULL OR NEW.notice_due_on IS NULL
                           OR NEW.non_renewal_notice_served_on > NEW.notice_due_on THEN
                            RAISE EXCEPTION 'RENEWAL_NOTICE_SERVED_LATE: case % has no non-renewal notice served by the notice date', OLD.id
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                END IF;
                RETURN NEW;
            END
            $fn$;
            CREATE TRIGGER trg_contract_renewal_cases__transition_guard
                BEFORE INSERT OR UPDATE ON contract_renewal_cases
                FOR EACH ROW EXECUTE FUNCTION contract_renewal_cases_transition_guard();
            """;
    }
}
