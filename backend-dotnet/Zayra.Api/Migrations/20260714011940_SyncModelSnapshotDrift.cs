using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations;

/// <summary>
/// Reconciles ZayraDbContextModelSnapshot with the model, and repairs the migration chain.
///
/// Several migrations (AddEnterpriseIdentityBoundary, AddBenefitsCompensationFoundation,
/// AddStatutoryFilingAndErpPostingLifecycle, ...) were authored on parallel branches with
/// out-of-order timestamps, so each regenerated the snapshot from a stale baseline and
/// clobbered the one before it. Two things broke as a result:
///
///   1. EF stopped detecting real model changes, because the snapshot no longer described
///      the schema the migrations actually produce. That is how entity properties reached
///      production with no migration behind them — the deployed code selected columns the
///      database did not have, and every request touching them failed with 42703. For
///      /api/auth/login that meant a 500 on every sign-in.
///   2. The chain could no longer build a database from scratch: some columns exist in the
///      live database (accumulated over time) that no migration creates.
///
/// This migration closes both gaps. Every statement is idempotent, so it converges the
/// schema whether it runs against the live database (most objects already present) or an
/// empty one (preceding migrations create most, this fills the rest). Verified by applying
/// the full chain to an empty database.
/// </summary>
public partial class SyncModelSnapshotDrift : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS acknowledged_at_utc timestamp with time zone;

ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS filing_status character varying(40) NOT NULL DEFAULT '';

ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS rejected_at_utc timestamp with time zone;

ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS rejection_reason character varying(1000);

ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS resubmission_number integer NOT NULL DEFAULT 0;

ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS resubmission_of_wps_file_batch_id uuid;

ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS submission_reference character varying(120);

ALTER TABLE wps_file_batches ADD COLUMN IF NOT EXISTS submitted_at_utc timestamp with time zone;

ALTER TABLE users ADD COLUMN IF NOT EXISTS external_id character varying(256) NOT NULL DEFAULT '';

ALTER TABLE users ADD COLUMN IF NOT EXISTS identity_provider character varying(40) NOT NULL DEFAULT 'Local';

ALTER TABLE users ADD COLUMN IF NOT EXISTS last_provisioned_at_utc timestamp with time zone;

ALTER TABLE users ADD COLUMN IF NOT EXISTS provisioning_source character varying(40) NOT NULL DEFAULT 'Local';

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS eligible_designation_ids_json json NOT NULL DEFAULT '{}';

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS eligible_grade_ids_json json NOT NULL DEFAULT '{}';

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS max_basic_salary numeric(14,2) NOT NULL DEFAULT 0.0;

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS max_gross_salary numeric(14,2) NOT NULL DEFAULT 0.0;

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS min_basic_salary numeric(14,2) NOT NULL DEFAULT 0.0;

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS min_gross_salary numeric(14,2) NOT NULL DEFAULT 0.0;

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS previous_version_id uuid;

ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS version_number integer NOT NULL DEFAULT 0;

ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS erp_posting_failure_reason character varying(1000);

ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS erp_posting_reference character varying(120);

ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS erp_posting_status character varying(40) NOT NULL DEFAULT '';

ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS erp_posting_status_changed_at_utc timestamp with time zone;

ALTER TABLE payroll_payment_batches ALTER COLUMN wps_status TYPE character varying(40);

ALTER TABLE payroll_payment_batches ADD COLUMN IF NOT EXISTS wps_rejection_reason character varying(1000);

ALTER TABLE payroll_payment_batches ADD COLUMN IF NOT EXISTS wps_status_changed_at_utc timestamp with time zone;

ALTER TABLE payroll_payment_batches ADD COLUMN IF NOT EXISTS wps_submission_reference character varying(120);

ALTER TABLE finance_gl_entries ADD COLUMN IF NOT EXISTS erp_document_number character varying(120);

ALTER TABLE finance_gl_entries ADD COLUMN IF NOT EXISTS erp_posting_status character varying(40) NOT NULL DEFAULT '';

ALTER TABLE finance_gl_entries ADD COLUMN IF NOT EXISTS erp_rejection_reason character varying(1000);

ALTER TABLE finance_gl_entries ADD COLUMN IF NOT EXISTS erp_status_changed_at_utc timestamp with time zone;

ALTER TABLE employee_change_requests ADD COLUMN IF NOT EXISTS approval_request_id uuid;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS company_id uuid;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_employee_id integer;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_name character varying(180) NOT NULL DEFAULT '';

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_role character varying(80) NOT NULL DEFAULT '';

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_type character varying(60) NOT NULL DEFAULT '';

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_user_id uuid;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_queue character varying(180) NOT NULL DEFAULT '';

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS due_at_utc timestamp with time zone;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS escalated_at_utc timestamp with time zone;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS escalated_to_role character varying(80) NOT NULL DEFAULT '';

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS last_routed_at_utc timestamp with time zone;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS priority character varying(40) NOT NULL DEFAULT '';

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS requested_for_employee_id integer;

ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS sla_hours integer NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS benefit_contributions (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    benefit_enrollment_id uuid NOT NULL,
    benefit_plan_id uuid NOT NULL,
    employee_id integer NOT NULL,
    employee_amount numeric(14,2) NOT NULL,
    employer_amount numeric(14,2) NOT NULL,
    frequency text NOT NULL,
    payroll_component_code text NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    is_active boolean NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    created_by uuid,
    CONSTRAINT ""PK_benefit_contributions"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS benefit_eligibility_rules (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    benefit_plan_id uuid NOT NULL,
    company_id uuid,
    grade_id uuid,
    effective_from date NOT NULL,
    effective_to date,
    is_active boolean NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    created_by uuid,
    CONSTRAINT ""PK_benefit_eligibility_rules"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS benefit_enrollments (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    benefit_plan_id uuid NOT NULL,
    employee_id integer NOT NULL,
    employee_name text NOT NULL,
    coverage_tier text NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    status text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at_utc timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ""PK_benefit_enrollments"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS benefit_payroll_deduction_links (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    benefit_enrollment_id uuid NOT NULL,
    benefit_contribution_id uuid NOT NULL,
    payroll_deduction_id uuid NOT NULL,
    payroll_run_id uuid NOT NULL,
    employee_id integer NOT NULL,
    linked_amount numeric(14,2) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    created_by uuid,
    CONSTRAINT ""PK_benefit_payroll_deduction_links"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS benefit_plans (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    code text NOT NULL,
    name text NOT NULL,
    plan_type text NOT NULL,
    currency text NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    requires_enrollment boolean NOT NULL,
    is_active boolean NOT NULL,
    is_deleted boolean NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    created_by uuid,
    CONSTRAINT ""PK_benefit_plans"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS enterprise_identity_provisioning_events (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    protocol character varying(40) NOT NULL,
    action character varying(120) NOT NULL,
    external_id character varying(256) NOT NULL,
    user_id uuid,
    employee_id integer,
    status character varying(40) NOT NULL,
    details_json json NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT ""PK_enterprise_identity_provisioning_events"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS onboarding_checklist_template_tasks (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    checklist_id uuid NOT NULL,
    task_title text NOT NULL,
    task_description text NOT NULL,
    category text NOT NULL,
    assigned_to_name text NOT NULL,
    assigned_to_user_id uuid,
    due_offset_days integer NOT NULL,
    order_index integer NOT NULL,
    is_mandatory boolean NOT NULL,
    is_active boolean NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone,
    CONSTRAINT ""PK_onboarding_checklist_template_tasks"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS payroll_opening_balances (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    employee_id integer NOT NULL,
    employee_code text NOT NULL,
    year integer NOT NULL,
    balance_type text NOT NULL,
    component_code text NOT NULL,
    amount numeric(14,2) NOT NULL,
    currency text NOT NULL,
    source_system text NOT NULL,
    source_record_id text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at_utc timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ""PK_payroll_opening_balances"" PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS tenant_identity_provider_settings (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    saml_enabled boolean NOT NULL,
    oidc_enabled boolean NOT NULL,
    scim_enabled boolean NOT NULL,
    enforce_sso_login boolean NOT NULL,
    scim_dry_run boolean NOT NULL,
    allowed_domains_csv character varying(2000) NOT NULL,
    saml_entity_id character varying(512) NOT NULL,
    saml_sso_url character varying(1024) NOT NULL,
    saml_certificate_thumbprint character varying(160) NOT NULL,
    oidc_authority character varying(1024) NOT NULL,
    oidc_client_id character varying(256) NOT NULL,
    oidc_client_secret_configured boolean NOT NULL,
    scim_token_hash character varying(128) NOT NULL,
    scim_token_rotated_at_utc timestamp with time zone,
    updated_at_utc timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT ""PK_tenant_identity_provider_settings"" PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS ""IX_wps_file_batches_tenant_id_filing_status"" ON wps_file_batches (tenant_id, filing_status);

CREATE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_erp_posting_status"" ON payroll_runs (tenant_id, erp_posting_status);

CREATE INDEX IF NOT EXISTS ""IX_finance_gl_entries_tenant_id_erp_posting_status"" ON finance_gl_entries (tenant_id, erp_posting_status);

CREATE INDEX IF NOT EXISTS ""IX_employee_change_requests_approval_request_id"" ON employee_change_requests (approval_request_id);

CREATE INDEX IF NOT EXISTS ""IX_approval_requests_tenant_id_company_id"" ON approval_requests (tenant_id, company_id);

CREATE INDEX IF NOT EXISTS ""IX_approval_requests_tenant_id_company_id_status"" ON approval_requests (tenant_id, company_id, status);

CREATE INDEX IF NOT EXISTS ""IX_approval_requests_tenant_id_status_current_approver_employe~"" ON approval_requests (tenant_id, status, current_approver_employee_id);

CREATE INDEX IF NOT EXISTS ""IX_approval_requests_tenant_id_status_current_approver_user_id"" ON approval_requests (tenant_id, status, current_approver_user_id);

CREATE INDEX IF NOT EXISTS ""IX_approval_requests_tenant_id_status_due_at_utc"" ON approval_requests (tenant_id, status, due_at_utc);

CREATE INDEX IF NOT EXISTS ""IX_benefit_contributions_tenant_id_benefit_enrollment_id_is_ac~"" ON benefit_contributions (tenant_id, benefit_enrollment_id, is_active);

CREATE INDEX IF NOT EXISTS ""IX_benefit_contributions_tenant_id_company_id"" ON benefit_contributions (tenant_id, company_id);

CREATE INDEX IF NOT EXISTS ""IX_benefit_contributions_tenant_id_employee_id_effective_from"" ON benefit_contributions (tenant_id, employee_id, effective_from);

CREATE INDEX IF NOT EXISTS ""IX_benefit_eligibility_rules_tenant_id_benefit_plan_id_company~"" ON benefit_eligibility_rules (tenant_id, benefit_plan_id, company_id, grade_id, is_active);

CREATE INDEX IF NOT EXISTS ""IX_benefit_eligibility_rules_tenant_id_company_id"" ON benefit_eligibility_rules (tenant_id, company_id);

CREATE INDEX IF NOT EXISTS ""IX_benefit_enrollments_tenant_id_benefit_plan_id_employee_id_s~"" ON benefit_enrollments (tenant_id, benefit_plan_id, employee_id, status);

CREATE INDEX IF NOT EXISTS ""IX_benefit_enrollments_tenant_id_company_id"" ON benefit_enrollments (tenant_id, company_id);

CREATE INDEX IF NOT EXISTS ""IX_benefit_enrollments_tenant_id_employee_id_effective_from"" ON benefit_enrollments (tenant_id, employee_id, effective_from);

CREATE INDEX IF NOT EXISTS ""IX_benefit_payroll_deduction_links_tenant_id_benefit_enrollmen~"" ON benefit_payroll_deduction_links (tenant_id, benefit_enrollment_id, payroll_run_id);

CREATE INDEX IF NOT EXISTS ""IX_benefit_payroll_deduction_links_tenant_id_company_id"" ON benefit_payroll_deduction_links (tenant_id, company_id);

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_benefit_payroll_deduction_links_tenant_id_payroll_deduction~"" ON benefit_payroll_deduction_links (tenant_id, payroll_deduction_id);

CREATE INDEX IF NOT EXISTS ""IX_benefit_plans_tenant_id_company_id"" ON benefit_plans (tenant_id, company_id);

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_benefit_plans_tenant_id_company_id_code"" ON benefit_plans (tenant_id, company_id, code);

CREATE INDEX IF NOT EXISTS ""IX_benefit_plans_tenant_id_company_id_is_active"" ON benefit_plans (tenant_id, company_id, is_active);

CREATE INDEX IF NOT EXISTS ""IX_enterprise_identity_provisioning_events_tenant_id_action_cr~"" ON enterprise_identity_provisioning_events (tenant_id, action, created_at_utc);

CREATE INDEX IF NOT EXISTS ""IX_enterprise_identity_provisioning_events_tenant_id_external_~"" ON enterprise_identity_provisioning_events (tenant_id, external_id);

CREATE INDEX IF NOT EXISTS ""IX_onboarding_checklist_template_tasks_tenant_id_checklist_id_~"" ON onboarding_checklist_template_tasks (tenant_id, checklist_id, order_index);

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_onboarding_checklist_template_tasks_tenant_id_checklist_id~1"" ON onboarding_checklist_template_tasks (tenant_id, checklist_id, task_title);

CREATE INDEX IF NOT EXISTS ""IX_payroll_opening_balances_tenant_id_company_id"" ON payroll_opening_balances (tenant_id, company_id);

CREATE INDEX IF NOT EXISTS ""IX_payroll_opening_balances_tenant_id_company_id_year"" ON payroll_opening_balances (tenant_id, company_id, year);

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_opening_balances_tenant_id_employee_id_year_balance~"" ON payroll_opening_balances (tenant_id, employee_id, year, balance_type, component_code);

CREATE INDEX IF NOT EXISTS ""IX_tenant_identity_provider_settings_scim_token_hash"" ON tenant_identity_provider_settings (scim_token_hash);

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_tenant_identity_provider_settings_tenant_id"" ON tenant_identity_provider_settings (tenant_id);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Not reversed: this only brings the schema up to what the model already declares.
        // Dropping these would break a live database without changing the model.
    }
}
