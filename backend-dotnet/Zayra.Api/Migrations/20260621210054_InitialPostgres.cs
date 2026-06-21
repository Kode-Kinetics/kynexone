using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "absence_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    absence_date = table.Column<DateOnly>(type: "date", nullable: false),
                    absence_type = table.Column<string>(type: "text", nullable: false),
                    is_regularized = table.Column<bool>(type: "boolean", nullable: false),
                    payroll_impact = table.Column<string>(type: "text", nullable: false),
                    regularization_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_absence_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "absence_regularization_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    absence_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    manager_notes = table.Column<string>(type: "text", nullable: false),
                    h_r_notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_absence_regularization_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "admin_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    old_values_json = table.Column<string>(type: "text", nullable: false),
                    new_values_json = table.Column<string>(type: "text", nullable: false),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    ip_address = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "advance_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    advance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    approver_role = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advance_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "advance_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    advance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    old_values_json = table.Column<string>(type: "text", nullable: false),
                    new_values_json = table.Column<string>(type: "text", nullable: false),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advance_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "advance_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    advance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_number = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_due = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount_paid = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advance_installments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "advance_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_name = table.Column<string>(type: "text", nullable: false),
                    max_percentage_of_salary = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    max_advances_per_year = table.Column<int>(type: "integer", nullable: false),
                    min_service_months = table.Column<int>(type: "integer", nullable: false),
                    allow_installments = table.Column<bool>(type: "boolean", nullable: false),
                    max_installments = table.Column<int>(type: "integer", nullable: false),
                    cooldown_months = table.Column<int>(type: "integer", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advance_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_hr_query_cache",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cache_key = table.Column<string>(type: "character varying(191)", maxLength: 191, nullable: false),
                    query_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    normalized_query = table.Column<string>(type: "longtext", nullable: false),
                    intent_classified = table.Column<string>(type: "text", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    user_role_signature = table.Column<string>(type: "longtext", nullable: false),
                    permission_signature = table.Column<string>(type: "longtext", nullable: false),
                    answer = table.Column<string>(type: "longtext", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    response_status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    human_review_required = table.Column<bool>(type: "boolean", nullable: false),
                    is_advisory_label_shown = table.Column<bool>(type: "boolean", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    response_time_ms = table.Column<int>(type: "integer", nullable: false),
                    hit_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_hit_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_hr_query_cache", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_hr_query_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    user_role = table.Column<string>(type: "text", nullable: false),
                    query = table.Column<string>(type: "text", nullable: false),
                    logged_prompt = table.Column<string>(type: "longtext", nullable: false),
                    prompt_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    prompt_summary = table.Column<string>(type: "longtext", nullable: false),
                    response = table.Column<string>(type: "text", nullable: false),
                    intent_classified = table.Column<string>(type: "text", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    was_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    blocked_reason = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    response_status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    human_review_required = table.Column<bool>(type: "boolean", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    response_time_ms = table.Column<int>(type: "integer", nullable: false),
                    is_advisory_label_shown = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_hr_query_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_insights",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    insight_type = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    data_json = table.Column<string>(type: "json", nullable: false),
                    generated_by = table.Column<string>(type: "text", nullable: false),
                    is_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_insights", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_model_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_name = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    use_case = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    config_json = table.Column<string>(type: "json", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_model_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_recommendations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    a_i_insight_id = table.Column<Guid>(type: "uuid", nullable: true),
                    module = table.Column<string>(type: "text", nullable: false),
                    recommendation_type = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    recommendation_text = table.Column<string>(type: "text", nullable: false),
                    action_label = table.Column<string>(type: "text", nullable: false),
                    action_route = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_advisory_only = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actioned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    actioned_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_recommendations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "application_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    performed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "appraisal_appeals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    appeal_reason = table.Column<string>(type: "text", nullable: false),
                    employee_justification = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    hr_response = table.Column<string>(type: "text", nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by_name = table.Column<string>(type: "text", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appraisal_appeals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "appraisal_calibrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    original_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    adjusted_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    adjustment_reason = table.Column<string>(type: "text", nullable: false),
                    original_rating = table.Column<string>(type: "text", nullable: false),
                    adjusted_rating = table.Column<string>(type: "text", nullable: false),
                    calibrated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calibrated_by_name = table.Column<string>(type: "text", nullable: false),
                    calibrated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appraisal_calibrations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "appraisal_competency_ratings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competency_name = table.Column<string>(type: "text", nullable: false),
                    competency_category = table.Column<string>(type: "text", nullable: false),
                    self_rating = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    manager_rating = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    self_comments = table.Column<string>(type: "text", nullable: false),
                    manager_comments = table.Column<string>(type: "text", nullable: false),
                    weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appraisal_competency_ratings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "appraisal_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_name = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    scorecard_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kpi_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    competency_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    attendance_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    productivity_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    feedback_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    discipline_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    final_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    final_rating = table.Column<string>(type: "text", nullable: false),
                    calibration_adjustment = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    calibration_notes = table.Column<string>(type: "text", nullable: false),
                    self_assessment_notes = table.Column<string>(type: "text", nullable: false),
                    manager_notes = table.Column<string>(type: "text", nullable: false),
                    hr_notes = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    self_assessment_submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    manager_reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_appealed = table.Column<bool>(type: "boolean", nullable: false),
                    reviewer_manager_id = table.Column<int>(type: "integer", nullable: true),
                    reviewer_manager_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appraisal_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "appraisal_score_breakdowns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component = table.Column<string>(type: "text", nullable: false),
                    raw_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    weighted_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appraisal_score_breakdowns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_authorities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authority_scope = table.Column<string>(type: "text", nullable: false),
                    approver_role = table.Column<string>(type: "text", nullable: false),
                    amount_limit = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "text", nullable: false),
                    can_final_approve = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_authorities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_delegations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_employee_id = table.Column<int>(type: "integer", nullable: false),
                    to_employee_id = table.Column<int>(type: "integer", nullable: false),
                    from_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_delegations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    current_step_order = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_workflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    entity_name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_workflows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assessment_questions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    question_type = table.Column<string>(type: "text", nullable: false),
                    question_text = table.Column<string>(type: "text", nullable: false),
                    options_json = table.Column<string>(type: "json", nullable: false),
                    correct_answer = table.Column<string>(type: "text", nullable: false),
                    marks = table.Column<int>(type: "integer", nullable: false),
                    difficulty = table.Column<string>(type: "text", nullable: false),
                    skill_tag = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessment_questions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assessment_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    assessment_type = table.Column<string>(type: "text", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    passing_score = table.Column<int>(type: "integer", nullable: false),
                    total_marks = table.Column<int>(type: "integer", nullable: false),
                    is_randomized = table.Column<bool>(type: "boolean", nullable: false),
                    audience = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessment_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_ai_insights",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    insight_type = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    data_json = table.Column<string>(type: "json", nullable: false),
                    is_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_ai_insights", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_name = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    metadata_json = table.Column<string>(type: "json", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_correction_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    regularization_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_level = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_correction_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_daily_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    first_in_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_out_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    total_worked_minutes = table.Column<int>(type: "integer", nullable: false),
                    break_minutes = table.Column<int>(type: "integer", nullable: false),
                    late_minutes = table.Column<int>(type: "integer", nullable: false),
                    early_exit_minutes = table.Column<int>(type: "integer", nullable: false),
                    overtime_minutes = table.Column<int>(type: "integer", nullable: false),
                    undertime_minutes = table.Column<int>(type: "integer", nullable: false),
                    missing_punch = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    work_mode = table.Column<string>(type: "text", nullable: false),
                    manual_correction_status = table.Column<string>(type: "text", nullable: false),
                    is_payroll_locked = table.Column<bool>(type: "boolean", nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_daily_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_device_connectors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connector_code = table.Column<string>(type: "text", nullable: false),
                    vendor = table.Column<string>(type: "text", nullable: false),
                    connector_type = table.Column<string>(type: "text", nullable: false),
                    settings_json = table.Column<string>(type: "json", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_device_connectors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_device_sync_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sync_method = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    raw_events_received = table.Column<int>(type: "integer", nullable: false),
                    raw_events_processed = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_device_sync_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_name = table.Column<string>(type: "text", nullable: false),
                    device_type = table.Column<string>(type: "text", nullable: false),
                    vendor = table.Column<string>(type: "text", nullable: false),
                    serial_number = table.Column<string>(type: "text", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_name = table.Column<string>(type: "text", nullable: false),
                    ip_address = table.Column<string>(type: "text", nullable: false),
                    endpoint_url = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: true),
                    api_key_reference = table.Column<string>(type: "text", nullable: false),
                    sync_method = table.Column<string>(type: "text", nullable: false),
                    sync_frequency = table.Column<string>(type: "text", nullable: false),
                    auth_type = table.Column<string>(type: "text", nullable: false),
                    auth_credentials_json = table.Column<string>(type: "text", nullable: false),
                    custom_headers_json = table.Column<string>(type: "text", nullable: false),
                    device_parameters_json = table.Column<string>(type: "text", nullable: false),
                    field_mappings_json = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    last_sync_status = table.Column<string>(type: "text", nullable: false),
                    last_sync_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_log = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    daily_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    exception_type = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "text", nullable: false),
                    is_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_exceptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_geofences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attendance_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    radius_meters = table.Column<int>(type: "integer", nullable: false),
                    clock_in_required_inside = table.Column<bool>(type: "boolean", nullable: false),
                    clock_out_required_inside = table.Column<bool>(type: "boolean", nullable: false),
                    spoofing_risk_check_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_geofences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    imported_rows = table.Column<int>(type: "integer", nullable: false),
                    failed_rows = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_import_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_import_errors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: false),
                    raw_row = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_import_errors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_type = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_locations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_lock_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    lock_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    locked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    locked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_lock_periods", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_payroll_impacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    impact_type = table.Column<string>(type: "text", nullable: false),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    daily_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_payroll_impacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grace_minutes = table.Column<int>(type: "integer", nullable: false),
                    late_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    early_exit_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    half_day_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    absent_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    standard_work_minutes = table.Column<int>(type: "integer", nullable: false),
                    break_minutes = table.Column<int>(type: "integer", nullable: false),
                    rounding_rule = table.Column<string>(type: "text", nullable: false),
                    requires_overtime_approval = table.Column<bool>(type: "boolean", nullable: false),
                    allow_absence_to_leave_conversion = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_raw_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    punch_timestamp_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    punch_direction = table.Column<string>(type: "text", nullable: false),
                    location_name = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: false),
                    photo_reference = table.Column<string>(type: "text", nullable: false),
                    raw_payload_json = table.Column<string>(type: "json", nullable: false),
                    sync_batch_reference = table.Column<string>(type: "text", nullable: false),
                    verification_method = table.Column<string>(type: "text", nullable: false),
                    confidence_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    is_processed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_raw_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_records",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    time_in = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    time_out = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    overtime_hours = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_regularization_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    request_type = table.Column<string>(type: "text", nullable: false),
                    requested_in_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    requested_out_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payroll_lock_checked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_regularization_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attendance_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_type = table.Column<string>(type: "text", nullable: false),
                    rule_value_json = table.Column<string>(type: "json", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entity_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    metadata = table.Column<string>(type: "json", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bank_transfer_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_content = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_transfer_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bonus_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bonus_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    approver_role = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bonus_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bonus_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bonus_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_bonus_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    old_values_json = table.Column<string>(type: "text", nullable: false),
                    new_values_json = table.Column<string>(type: "text", nullable: false),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bonus_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bonus_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bonus_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bonus_type_name = table.Column<string>(type: "text", nullable: false),
                    batch_number = table.Column<string>(type: "text", nullable: false),
                    batch_name = table.Column<string>(type: "text", nullable: false),
                    payment_period = table.Column<string>(type: "text", nullable: false),
                    payment_date = table.Column<DateOnly>(type: "date", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    employee_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    is_locked_by_payroll = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bonus_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bonus_recommendations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    bonus_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    bonus_type = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    recommended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recommended_by_name = table.Column<string>(type: "text", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bonus_recommendations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bonus_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    calculation_method = table.Column<string>(type: "text", nullable: false),
                    default_calculation_value = table.Column<decimal>(type: "numeric", nullable: false),
                    frequency = table.Column<string>(type: "text", nullable: false),
                    min_service_months = table.Column<int>(type: "integer", nullable: false),
                    pro_rata_eligibility = table.Column<bool>(type: "boolean", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    is_included_in_eosb = table.Column<bool>(type: "boolean", nullable: false),
                    is_included_in_gosi_base = table.Column<bool>(type: "boolean", nullable: false),
                    is_included_in_wps = table.Column<bool>(type: "boolean", nullable: false),
                    is_taxable = table.Column<bool>(type: "boolean", nullable: false),
                    tax_region = table.Column<string>(type: "text", nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bonus_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    address_line1 = table.Column<string>(type: "text", nullable: false),
                    address_line2 = table.Column<string>(type: "text", nullable: false),
                    time_zone_id = table.Column<string>(type: "text", nullable: false),
                    labor_office_code = table.Column<string>(type: "text", nullable: false),
                    is_head_office = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "burnout_risk_signals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    signal_type = table.Column<string>(type: "text", nullable: false),
                    signal_value = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    detected_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_burnout_risk_signals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "candidate_ai_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_opening_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resume_parse_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    overall_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    skill_match_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    experience_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    education_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    skill_match_details = table.Column<string>(type: "text", nullable: false),
                    strengths = table.Column<string>(type: "text", nullable: false),
                    concerns = table.Column<string>(type: "text", nullable: false),
                    recommendation = table.Column<string>(type: "text", nullable: false),
                    is_advisory_only = table.Column<bool>(type: "boolean", nullable: false),
                    generated_by = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_ai_scores", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "candidate_assessments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    invitation_token = table.Column<string>(type: "text", nullable: false),
                    sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    score_obtained = table.Column<int>(type: "integer", nullable: true),
                    total_marks = table.Column<int>(type: "integer", nullable: true),
                    score_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    passed = table.Column<bool>(type: "boolean", nullable: true),
                    result_json = table.Column<string>(type: "json", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_assessments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "candidate_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_url = table.Column<string>(type: "text", nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    uploaded_by_name = table.Column<string>(type: "text", nullable: false),
                    uploaded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_name = table.Column<string>(type: "text", nullable: false),
                    last_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false),
                    current_job_title = table.Column<string>(type: "text", nullable: false),
                    current_company = table.Column<string>(type: "text", nullable: false),
                    total_experience_years = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    education_level = table.Column<string>(type: "text", nullable: false),
                    nationality = table.Column<string>(type: "text", nullable: false),
                    linked_in_url = table.Column<string>(type: "text", nullable: false),
                    resume_url = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    tags = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comp_off_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    worked_date = table.Column<DateOnly>(type: "date", nullable: false),
                    work_type = table.Column<string>(type: "text", nullable: false),
                    hours_worked = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    days_earned = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    manager_approval_notes = table.Column<string>(type: "text", nullable: false),
                    approved_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_off_credits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comp_off_usages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    comp_off_credit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    days_used = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_off_usages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_name_en = table.Column<string>(type: "text", nullable: false),
                    legal_name_ar = table.Column<string>(type: "text", nullable: false),
                    trade_name = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    registration_number = table.Column<string>(type: "text", nullable: false),
                    tax_number = table.Column<string>(type: "text", nullable: false),
                    wps_employer_id = table.Column<string>(type: "text", nullable: false),
                    gosi_employer_id = table.Column<string>(type: "text", nullable: false),
                    qiwa_establishment_id = table.Column<string>(type: "text", nullable: false),
                    default_currency = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "competencies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    behavioral_indicators = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competencies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "compliance_ai_insights",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    insight_type = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    recommended_action = table.Column<string>(type: "text", nullable: false),
                    is_advisory = table.Column<bool>(type: "boolean", nullable: false),
                    is_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    generated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_ai_insights", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "compliance_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    performed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metadata_json = table.Column<string>(type: "json", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "compliance_reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    reminder_type = table.Column<string>(type: "text", nullable: false),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    scheduled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_reminders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "compliance_renewals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    document_number = table.Column<string>(type: "text", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    renewal_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    assigned_to_name = table.Column<string>(type: "text", nullable: false),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_renewals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "compliance_requirements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type_name = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    applicable_to = table.Column<string>(type: "text", nullable: false),
                    applicable_value = table.Column<string>(type: "text", nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    alert_days_before_expiry = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_requirements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "continuous_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    given_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    given_by_name = table.Column<string>(type: "text", nullable: false),
                    feedback_type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    is_private = table.Column<bool>(type: "boolean", nullable: false),
                    linked_review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_continuous_feedback", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contract_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    contract_type = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    content_html_en = table.Column<string>(type: "longtext", nullable: false),
                    content_html_ar = table.Column<string>(type: "longtext", nullable: false),
                    variables = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cost_centers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cost_centers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "country_payroll_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    rule_key = table.Column<string>(type: "text", nullable: false),
                    rule_value = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    is_override = table.Column<bool>(type: "boolean", nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_country_payroll_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_center_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    manager_employee_id = table.Column<int>(type: "integer", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "designations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    title_en = table.Column<string>(type: "text", nullable: false),
                    title_ar = table.Column<string>(type: "text", nullable: false),
                    job_grade = table.Column<string>(type: "text", nullable: false),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_level = table.Column<string>(type: "text", nullable: false),
                    job_description = table.Column<string>(type: "text", nullable: false),
                    is_manager_role = table.Column<bool>(type: "boolean", nullable: false),
                    is_system_default = table.Column<bool>(type: "boolean", nullable: false),
                    level_rank = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_designations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "doc_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    expiry_required = table.Column<bool>(type: "boolean", nullable: false),
                    alert_days_before_expiry = table.Column<int>(type: "integer", nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    applicable_countries = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doc_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_action_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_action_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_ai_query_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_ai_query_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_announcements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    audience = table.Column<string>(type: "text", nullable: false),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_announcements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_bonuses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bonus_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_int_id = table.Column<int>(type: "integer", nullable: true),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department = table.Column<string>(type: "text", nullable: false),
                    bonus_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bonus_type_name = table.Column<string>(type: "text", nullable: false),
                    basic_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    calculation_method = table.Column<string>(type: "text", nullable: false),
                    calculation_value = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    gross_bonus_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    tax_withheld = table.Column<decimal>(type: "numeric", nullable: false),
                    bonus_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_region = table.Column<string>(type: "text", nullable: false),
                    payment_period = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_bonuses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    sensitive_fields = table.Column<string>(type: "text", nullable: false),
                    proposed_changes_json = table.Column<string>(type: "json", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    applied_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_change_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_churn_predictions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    churn_probability = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    time_horizon = table.Column<string>(type: "text", nullable: false),
                    model_version = table.Column<string>(type: "text", nullable: false),
                    is_advisory_only = table.Column<bool>(type: "boolean", nullable: false),
                    computed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_churn_predictions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_compliance_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    field_key = table.Column<string>(type: "text", nullable: false),
                    field_label = table.Column<string>(type: "text", nullable: false),
                    field_value = table.Column<string>(type: "text", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_compliance_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_contracts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contract_number = table.Column<string>(type: "text", nullable: false),
                    contract_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    basic_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    content_html_en = table.Column<string>(type: "longtext", nullable: false),
                    content_html_ar = table.Column<string>(type: "longtext", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    previous_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_by_employee_name = table.Column<string>(type: "text", nullable: false),
                    signed_by_employee_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    signed_by_hr_name = table.Column<string>(type: "text", nullable: false),
                    signed_by_hr_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_url = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_contracts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_dependents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    relationship = table.Column<string>(type: "text", nullable: false),
                    national_id = table.Column<string>(type: "text", nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    visa_expiry_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_dependents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_document_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    request_type = table.Column<string>(type: "text", nullable: false),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_document_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_document_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    storage_url = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_document_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    document_category = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    storage_url = table.Column<string>(type: "text", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    renewal_reminder_date = table.Column<DateOnly>(type: "date", nullable: true),
                    approval_status = table.Column<string>(type: "text", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    uploaded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    verified_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_downloaded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_downloaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_step = table.Column<string>(type: "text", nullable: false),
                    english_name = table.Column<string>(type: "text", nullable: false),
                    arabic_name = table.Column<string>(type: "text", nullable: false),
                    personal_email = table.Column<string>(type: "text", nullable: false),
                    work_email = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false),
                    gender = table.Column<string>(type: "text", nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    marital_status = table.Column<string>(type: "text", nullable: false),
                    emergency_contact_name = table.Column<string>(type: "text", nullable: false),
                    emergency_contact_phone = table.Column<string>(type: "text", nullable: false),
                    nationality = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    department = table.Column<string>(type: "text", nullable: false),
                    designation = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    work_location = table.Column<string>(type: "text", nullable: false),
                    manager_employee_id = table.Column<int>(type: "integer", nullable: true),
                    joining_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    contract_type = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    cost_center = table.Column<string>(type: "text", nullable: false),
                    contract_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    contract_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    probation_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    payroll_profile_code = table.Column<string>(type: "text", nullable: false),
                    salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    bank_name = table.Column<string>(type: "text", nullable: false),
                    bank_iban = table.Column<string>(type: "text", nullable: false),
                    wps_bank_details = table.Column<string>(type: "text", nullable: false),
                    shift_policy_code = table.Column<string>(type: "text", nullable: false),
                    leave_policy_code = table.Column<string>(type: "text", nullable: false),
                    sponsor_name = table.Column<string>(type: "text", nullable: false),
                    passport_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    visa_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    residency_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    work_permit_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    passport_number = table.Column<string>(type: "text", nullable: false),
                    passport_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    visa_number = table.Column<string>(type: "text", nullable: false),
                    visa_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    iqama_number = table.Column<string>(type: "text", nullable: false),
                    muqeem_number = table.Column<string>(type: "text", nullable: false),
                    gosi_reference = table.Column<string>(type: "text", nullable: false),
                    qiwa_contract_number = table.Column<string>(type: "text", nullable: false),
                    emirates_id = table.Column<string>(type: "text", nullable: false),
                    labor_card_number = table.Column<string>(type: "text", nullable: false),
                    visa_file_number = table.Column<string>(type: "text", nullable: false),
                    qid = table.Column<string>(type: "text", nullable: false),
                    work_permit_number = table.Column<string>(type: "text", nullable: false),
                    civil_id = table.Column<string>(type: "text", nullable: false),
                    residency_number = table.Column<string>(type: "text", nullable: false),
                    profile_completeness_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    activated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_drafts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_goals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    kpi_type = table.Column<string>(type: "text", nullable: false),
                    measurement_unit = table.Column<string>(type: "text", nullable: false),
                    target_value = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    actual_value = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    baseline_value = table.Column<decimal>(type: "numeric", nullable: false),
                    achievement_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    manager_approved = table.Column<bool>(type: "boolean", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_goals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_histories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    field_name = table.Column<string>(type: "text", nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: false),
                    new_value = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supporting_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snapshot_json = table.Column<string>(type: "json", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_histories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_id_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    company_prefix = table.Column<string>(type: "text", nullable: false),
                    use_country_prefix = table.Column<bool>(type: "boolean", nullable: false),
                    use_branch_prefix = table.Column<bool>(type: "boolean", nullable: false),
                    use_department_prefix = table.Column<bool>(type: "boolean", nullable: false),
                    use_year = table.Column<bool>(type: "boolean", nullable: false),
                    padding_length = table.Column<int>(type: "integer", nullable: false),
                    next_sequence = table.Column<int>(type: "integer", nullable: false),
                    allow_manual_override = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_id_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_leave_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_type_name = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    entitled = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    accrued = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    used = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    pending = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    carried_forward = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    encashed = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    expired = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    manual_adjustment = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    negative_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_leave_balances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    employee_int_id = table.Column<int>(type: "integer", nullable: true),
                    loan_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_type_name = table.Column<string>(type: "text", nullable: false),
                    loan_number = table.Column<string>(type: "text", nullable: false),
                    requested_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    approved_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    requested_installments = table.Column<int>(type: "integer", nullable: false),
                    approved_installments = table.Column<int>(type: "integer", nullable: false),
                    installment_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    repayment_frequency = table.Column<string>(type: "text", nullable: false),
                    disbursement_date = table.Column<DateOnly>(type: "date", nullable: true),
                    repayment_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    total_repaid = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    outstanding_balance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    is_locked_by_payroll = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_loans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_mobile_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    device_identifier = table.Column<string>(type: "text", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    push_token = table.Column<string>(type: "text", nullable: false),
                    biometric_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    registered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_mobile_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_notification_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    push_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sms_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    quiet_hours_json = table.Column<string>(type: "json", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_notification_preferences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    notification_type = table.Column<string>(type: "text", nullable: false),
                    is_read = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    read_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_payroll_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    bank_name = table.Column<string>(type: "text", nullable: false),
                    iban = table.Column<string>(type: "text", nullable: false),
                    account_number = table.Column<string>(type: "text", nullable: false),
                    payment_method = table.Column<string>(type: "text", nullable: false),
                    salary_currency = table.Column<string>(type: "text", nullable: false),
                    payroll_group = table.Column<string>(type: "text", nullable: false),
                    salary_structure_reference = table.Column<string>(type: "text", nullable: false),
                    wps_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    eosb_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    social_insurance_reference = table.Column<string>(type: "text", nullable: false),
                    mol_id = table.Column<string>(type: "text", nullable: false),
                    bank_routing_code = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_payroll_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_payslip_access_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    payslip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_payslip_access_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_policy_acknowledgements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_policy_acknowledgements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_profile_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    requested_changes_json = table.Column<string>(type: "json", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    contains_sensitive_fields = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_profile_change_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_risk_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    churn_risk_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    burnout_risk_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    performance_decline_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    overall_risk_level = table.Column<string>(type: "text", nullable: false),
                    risk_factors_json = table.Column<string>(type: "json", nullable: false),
                    recommendations = table.Column<string>(type: "text", nullable: false),
                    is_advisory_only = table.Column<bool>(type: "boolean", nullable: false),
                    computed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_risk_scores", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_salary_structures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    salary_structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    basic_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    housing_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    transport_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    food_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    mobile_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    other_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    fixed_deduction = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_salary_structures", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_self_service_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_name = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_self_service_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_sentiment_pulses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_sentiment_pulses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_status_histories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    old_status = table.Column<string>(type: "text", nullable: false),
                    new_status = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_status_histories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_transfer_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    current_branch = table.Column<string>(type: "text", nullable: false),
                    current_department = table.Column<string>(type: "text", nullable: false),
                    current_designation = table.Column<string>(type: "text", nullable: false),
                    current_manager_employee_id = table.Column<int>(type: "integer", nullable: true),
                    new_department = table.Column<string>(type: "text", nullable: false),
                    new_branch = table.Column<string>(type: "text", nullable: false),
                    new_designation = table.Column<string>(type: "text", nullable: false),
                    new_manager_employee_id = table.Column<int>(type: "integer", nullable: true),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_manager_approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    new_manager_approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    hr_approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_transfer_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    english_name = table.Column<string>(type: "text", nullable: false),
                    arabic_name = table.Column<string>(type: "text", nullable: false),
                    preferred_name = table.Column<string>(type: "text", nullable: false),
                    profile_photo_url = table.Column<string>(type: "text", nullable: false),
                    personal_email = table.Column<string>(type: "text", nullable: false),
                    work_email = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false),
                    gender = table.Column<string>(type: "text", nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    marital_status = table.Column<string>(type: "text", nullable: false),
                    emergency_contact_name = table.Column<string>(type: "text", nullable: false),
                    emergency_contact_phone = table.Column<string>(type: "text", nullable: false),
                    nationality = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    department = table.Column<string>(type: "text", nullable: false),
                    designation = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_center_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_location = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    manager_employee_id = table.Column<int>(type: "integer", nullable: true),
                    second_level_manager_employee_id = table.Column<int>(type: "integer", nullable: true),
                    supervisor_employee_id = table.Column<int>(type: "integer", nullable: true),
                    h_r_business_partner_employee_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    joining_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    confirmation_date = table.Column<DateOnly>(type: "date", nullable: true),
                    probation_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    contract_type = table.Column<string>(type: "text", nullable: false),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    job_title = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    cost_center = table.Column<string>(type: "text", nullable: false),
                    notice_period_days = table.Column<int>(type: "integer", nullable: true),
                    contract_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    contract_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    probation_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    payroll_profile_code = table.Column<string>(type: "text", nullable: false),
                    salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    bank_name = table.Column<string>(type: "text", nullable: false),
                    bank_iban = table.Column<string>(type: "text", nullable: false),
                    wps_bank_details = table.Column<string>(type: "text", nullable: false),
                    shift_policy_code = table.Column<string>(type: "text", nullable: false),
                    leave_policy_code = table.Column<string>(type: "text", nullable: false),
                    attendance_policy_code = table.Column<string>(type: "text", nullable: false),
                    sponsor_name = table.Column<string>(type: "text", nullable: false),
                    passport_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    visa_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    residency_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    work_permit_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    passport_number = table.Column<string>(type: "text", nullable: false),
                    passport_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    visa_number = table.Column<string>(type: "text", nullable: false),
                    visa_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    iqama_number = table.Column<string>(type: "text", nullable: false),
                    muqeem_number = table.Column<string>(type: "text", nullable: false),
                    gosi_reference = table.Column<string>(type: "text", nullable: false),
                    qiwa_contract_number = table.Column<string>(type: "text", nullable: false),
                    emirates_id = table.Column<string>(type: "text", nullable: false),
                    labor_card_number = table.Column<string>(type: "text", nullable: false),
                    visa_file_number = table.Column<string>(type: "text", nullable: false),
                    qid = table.Column<string>(type: "text", nullable: false),
                    work_permit_number = table.Column<string>(type: "text", nullable: false),
                    civil_id = table.Column<string>(type: "text", nullable: false),
                    residency_number = table.Column<string>(type: "text", nullable: false),
                    saudi_or_non_saudi = table.Column<string>(type: "text", nullable: false),
                    id_type = table.Column<string>(type: "text", nullable: false),
                    id_number = table.Column<string>(type: "text", nullable: false),
                    occupation_code = table.Column<string>(type: "text", nullable: false),
                    establishment_id = table.Column<string>(type: "text", nullable: false),
                    work_location_id = table.Column<string>(type: "text", nullable: false),
                    contract_reference = table.Column<string>(type: "text", nullable: false),
                    work_permit_reference = table.Column<string>(type: "text", nullable: false),
                    qiwa_employee_reference = table.Column<string>(type: "text", nullable: false),
                    qiwa_sync_status = table.Column<string>(type: "text", nullable: false),
                    medical_information = table.Column<string>(type: "text", nullable: false),
                    disciplinary_records = table.Column<string>(type: "text", nullable: false),
                    termination_reason = table.Column<string>(type: "text", nullable: false),
                    profile_completeness_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employees", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eosb_calculations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    calculation_date = table.Column<DateOnly>(type: "date", nullable: false),
                    eligible_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    calculated_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    rules_snapshot_json = table.Column<string>(type: "json", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eosb_calculations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ess_dashboard_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    widget_layout_json = table.Column<string>(type: "json", nullable: false),
                    locale = table.Column<string>(type: "text", nullable: false),
                    rtl_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ess_dashboard_preferences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feedback_360",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_employee_id = table.Column<int>(type: "integer", nullable: false),
                    reviewer_name = table.Column<string>(type: "text", nullable: false),
                    reviewer_role = table.Column<string>(type: "text", nullable: false),
                    is_anonymous = table.Column<bool>(type: "boolean", nullable: false),
                    score = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    strengths = table.Column<string>(type: "text", nullable: false),
                    improvements = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_360", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "finance_gl_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_module = table.Column<string>(type: "text", nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_entity_ref = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    debit_account = table.Column<string>(type: "text", nullable: false),
                    credit_account = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    posted_by_name = table.Column<string>(type: "text", nullable: false),
                    posted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_reversed = table.Column<bool>(type: "boolean", nullable: false),
                    reversal_of_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_gl_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_years",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_years", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gcc_compliance_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    wps_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    wps_agent_id = table.Column<string>(type: "text", nullable: false),
                    wps_mol_code = table.Column<string>(type: "text", nullable: false),
                    sif_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    eosb_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    eosb_years1_to5_rate = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    eosb_years_above5_rate = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    eosb_min_years = table.Column<int>(type: "integer", nullable: false),
                    work_week = table.Column<string>(type: "text", nullable: false),
                    weekend_days = table.Column<string>(type: "text", nullable: false),
                    visa_tracking_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    visa_alert_days = table.Column<int>(type: "integer", nullable: false),
                    iqama_required = table.Column<bool>(type: "boolean", nullable: false),
                    iqama_alert_days = table.Column<int>(type: "integer", nullable: false),
                    emirates_id_required = table.Column<bool>(type: "boolean", nullable: false),
                    ramadan_hours_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ramadan_reduced_hours_per_day = table.Column<int>(type: "integer", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gcc_compliance_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "goal_progress_updates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_value = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_name = table.Column<string>(type: "text", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goal_progress_updates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gosi_contribution_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    branch = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    min_contributory_wage = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    max_contributory_wage = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    source_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gosi_contribution_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "grades",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    band = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grades", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hr_request_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    h_r_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    storage_url = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_request_attachments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hr_request_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    default_sla_hours = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_request_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hr_request_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    h_r_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_request_comments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hr_request_slas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<string>(type: "text", nullable: false),
                    sla_hours = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_request_slas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hr_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_name = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "increment_recommendations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    current_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    recommended_increment_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    recommended_increment_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    new_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    recommended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recommended_by_name = table.Column<string>(type: "text", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_increment_recommendations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "interview_feedbacks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    interview_schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    interviewer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    interviewer_name = table.Column<string>(type: "text", nullable: false),
                    interviewer_role = table.Column<string>(type: "text", nullable: false),
                    communication_score = table.Column<int>(type: "integer", nullable: false),
                    technical_score = table.Column<int>(type: "integer", nullable: false),
                    culture_fit_score = table.Column<int>(type: "integer", nullable: false),
                    problem_solving_score = table.Column<int>(type: "integer", nullable: false),
                    leadership_score = table.Column<int>(type: "integer", nullable: false),
                    overall_score = table.Column<int>(type: "integer", nullable: false),
                    strengths = table.Column<string>(type: "text", nullable: false),
                    concerns = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    recommendation = table.Column<string>(type: "text", nullable: false),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview_feedbacks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "interview_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    interview_type = table.Column<string>(type: "text", nullable: false),
                    interviewer_names = table.Column<string>(type: "text", nullable: false),
                    scheduled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    meeting_link = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    overall_rating = table.Column<int>(type: "integer", nullable: true),
                    recommendation = table.Column<string>(type: "text", nullable: false),
                    feedback_notes = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview_schedules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_opening_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_title = table.Column<string>(type: "text", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_name = table.Column<string>(type: "text", nullable: false),
                    candidate_email = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    stage_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rejection_reason = table.Column<string>(type: "text", nullable: false),
                    offered_salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    applied_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    stage_changed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    hired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    onboarding_draft_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_openings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_code = table.Column<string>(type: "text", nullable: false),
                    requisition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    head_count = table.Column<int>(type: "integer", nullable: false),
                    filled_count = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    requirements = table.Column<string>(type: "text", nullable: false),
                    responsibilities = table.Column<string>(type: "text", nullable: false),
                    salary_from = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    salary_to = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    location = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assigned_hr_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_hr_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_openings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_accrual_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accrual_frequency = table.Column<string>(type: "text", nullable: false),
                    accrual_days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    carry_forward_expiry_days = table.Column<int>(type: "integer", nullable: false),
                    carry_forward_max_days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    negative_balance_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_accrual_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_ai_insights",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    insight_type = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    affected_employee_id = table.Column<int>(type: "integer", nullable: true),
                    affected_department = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "text", nullable: false),
                    is_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    acknowledged_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_ai_insights", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_number = table.Column<int>(type: "integer", nullable: false),
                    approver_role = table.Column<string>(type: "text", nullable: false),
                    approver_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approver_name = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    acted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    storage_url = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_attachments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: false),
                    new_value = table.Column<string>(type: "text", nullable: false),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_balance_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    transaction_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    balance_before = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_balance_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_blackout_dates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    is_company_wide = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_blackout_dates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_cancellation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reviewed_by_name = table.Column<string>(type: "text", nullable: false),
                    review_notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_cancellation_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_delegations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    delegate_employee_id = table.Column<int>(type: "integer", nullable: false),
                    delegate_employee_name = table.Column<string>(type: "text", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    delegation_type = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_delegations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_encashment_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_type_name = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    days_to_encash = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    amount_per_day = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    h_r_notes = table.Column<string>(type: "text", nullable: false),
                    payroll_notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_encashment_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_modification_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    new_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    new_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    new_total_days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reviewed_by_name = table.Column<string>(type: "text", nullable: false),
                    review_notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_modification_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_payroll_impacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    pay_period = table.Column<string>(type: "text", nullable: false),
                    impact_type = table.Column<string>(type: "text", nullable: false),
                    days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_payroll_impacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    contract_type = table.Column<string>(type: "text", nullable: false),
                    gender = table.Column<string>(type: "text", nullable: false),
                    applies_on_probation = table.Column<bool>(type: "boolean", nullable: false),
                    annual_entitlement_days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    accrual_method = table.Column<string>(type: "text", nullable: false),
                    carry_forward_max = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    carry_forward_expiry = table.Column<int>(type: "integer", nullable: false),
                    encashment_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    encashment_max_days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    minimum_days_per_request = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    maximum_days_per_request = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    notice_required_days = table.Column<int>(type: "integer", nullable: false),
                    weekends_included = table.Column<bool>(type: "boolean", nullable: false),
                    public_holidays_included = table.Column<bool>(type: "boolean", nullable: false),
                    payroll_impact = table.Column<string>(type: "text", nullable: false),
                    approval_workflow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_policy_eligibilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    contract_type = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_policy_eligibilities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_request_dates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_date = table.Column<DateOnly>(type: "date", nullable: false),
                    day_value = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    is_public_holiday = table.Column<bool>(type: "boolean", nullable: false),
                    is_weekend = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_request_dates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_type_name = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    total_days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    day_type = table.Column<string>(type: "text", nullable: false),
                    hours_requested = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    is_emergency = table.Column<bool>(type: "boolean", nullable: false),
                    attachment_path = table.Column<string>(type: "text", nullable: false),
                    payroll_impact = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    manager_approval_notes = table.Column<string>(type: "text", nullable: false),
                    h_r_approval_notes = table.Column<string>(type: "text", nullable: false),
                    rejection_reason = table.Column<string>(type: "text", nullable: false),
                    cancellation_reason = table.Column<string>(type: "text", nullable: false),
                    return_date = table.Column<DateOnly>(type: "date", nullable: true),
                    delegate_employee_id = table.Column<int>(type: "integer", nullable: true),
                    delegate_employee_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leave_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    is_paid = table.Column<bool>(type: "boolean", nullable: false),
                    is_half_day_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    is_hourly_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    requires_attachment = table.Column<bool>(type: "boolean", nullable: false),
                    requires_reason = table.Column<bool>(type: "boolean", nullable: false),
                    max_consecutive_days = table.Column<int>(type: "integer", nullable: false),
                    color_code = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loan_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    approver_role = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loan_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    old_values_json = table.Column<string>(type: "text", nullable: false),
                    new_values_json = table.Column<string>(type: "text", nullable: false),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loan_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_number = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_due = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount_paid = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_installments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loan_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_name = table.Column<string>(type: "text", nullable: false),
                    max_concurrent_loans = table.Column<int>(type: "integer", nullable: false),
                    max_multiplier_of_salary = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    cooldown_months_after_repayment = table.Column<int>(type: "integer", nullable: false),
                    allow_early_settlement = table.Column<bool>(type: "boolean", nullable: false),
                    allow_rescheduling = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loan_settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settlement_type = table.Column<string>(type: "text", nullable: false),
                    settlement_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    settlement_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_settlements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loan_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    max_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    max_installments = table.Column<int>(type: "integer", nullable: false),
                    repayment_frequency = table.Column<string>(type: "text", nullable: false),
                    is_interest_free = table.Column<bool>(type: "boolean", nullable: false),
                    interest_rate = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    min_service_months = table.Column<int>(type: "integer", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    address_line1 = table.Column<string>(type: "text", nullable: false),
                    address_line2 = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    postal_code = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    geofence_radius_meters = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_activity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email_attempted = table.Column<string>(type: "text", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_activity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "manpower_requisitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requisition_number = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    head_count = table.Column<int>(type: "integer", nullable: false),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    justification = table.Column<string>(type: "text", nullable: false),
                    required_skills = table.Column<string>(type: "text", nullable: false),
                    min_experience_years = table.Column<int>(type: "integer", nullable: true),
                    max_experience_years = table.Column<int>(type: "integer", nullable: true),
                    budget_from = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    budget_to = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    target_joining_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "text", nullable: false),
                    requested_by_employee_id = table.Column<int>(type: "integer", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manpower_requisitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "master_data_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    is_system_defined = table.Column<bool>(type: "boolean", nullable: false),
                    allow_custom_values = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_master_data_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "master_data_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value_en = table.Column<string>(type: "text", nullable: false),
                    value_ar = table.Column<string>(type: "text", nullable: false),
                    extra_json = table.Column<string>(type: "json", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_system_defined = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_master_data_values", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mfa_challenge_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    platform_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mfa_challenge_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    subject_en = table.Column<string>(type: "text", nullable: false),
                    subject_ar = table.Column<string>(type: "text", nullable: false),
                    body_en = table.Column<string>(type: "text", nullable: false),
                    body_ar = table.Column<string>(type: "text", nullable: false),
                    variables = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    entity_name = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    read_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "numbering_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    prefix = table.Column<string>(type: "text", nullable: false),
                    suffix = table.Column<string>(type: "text", nullable: false),
                    padding_length = table.Column<int>(type: "integer", nullable: false),
                    separator = table.Column<string>(type: "text", nullable: false),
                    include_year = table.Column<bool>(type: "boolean", nullable: false),
                    include_month = table.Column<bool>(type: "boolean", nullable: false),
                    current_sequence = table.Column<int>(type: "integer", nullable: false),
                    reset_yearly = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_numbering_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "offer_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_letter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    approver_name = table.Column<string>(type: "text", nullable: false),
                    approver_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approver_role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "offer_letters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_name = table.Column<string>(type: "text", nullable: false),
                    offered_job_title = table.Column<string>(type: "text", nullable: false),
                    offered_department = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    basic_salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    housing_allowance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    transport_allowance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    other_allowances = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    gross_salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    probation_months = table.Column<int>(type: "integer", nullable: false),
                    content_html = table.Column<string>(type: "longtext", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    generated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    response_deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    accepted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    declined_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decline_reason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_letters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "onboarding_checklists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    applicable_to = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_checklists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "onboarding_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checklist_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_title = table.Column<string>(type: "text", nullable: false),
                    task_description = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    assigned_to_name = table.Column<string>(type: "text", nullable: false),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    completed_date = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_tasks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    overtime_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hours_adjustment = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    amount_adjustment = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_adjustments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overtime_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_level = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_name = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    metadata_json = table.Column<string>(type: "json", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    budget_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    consumed_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_budgets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_calculations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overtime_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    approved_hours = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    hourly_rate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    multiplier = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    calculation_json = table.Column<string>(type: "json", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_calculations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_comp_off_conversions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overtime_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    overtime_hours = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    comp_off_days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_comp_off_conversions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_multipliers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overtime_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overtime_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    day_category = table.Column<string>(type: "text", nullable: false),
                    multiplier = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_multipliers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_payroll_impacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overtime_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hours = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_payroll_impacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hourly_rate_basis = table.Column<string>(type: "text", nullable: false),
                    fixed_hourly_rate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    standard_monthly_hours = table.Column<int>(type: "integer", nullable: false),
                    minimum_minutes = table.Column<int>(type: "integer", nullable: false),
                    maximum_minutes_per_day = table.Column<int>(type: "integer", nullable: false),
                    monthly_cap_minutes = table.Column<int>(type: "integer", nullable: false),
                    rounding_rule = table.Column<string>(type: "text", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    allow_comp_off_conversion = table.Column<bool>(type: "boolean", nullable: false),
                    ramadan_reduced_hours_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    overtime_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    overtime_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    start_time_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_time_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    requested_minutes = table.Column<int>(type: "integer", nullable: false),
                    approved_minutes = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attendance_daily_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overtime_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_type = table.Column<string>(type: "text", nullable: false),
                    rule_value_json = table.Column<string>(type: "json", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overtime_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "passport_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    passport_number = table.Column<string>(type: "text", nullable: false),
                    nationality = table.Column<string>(type: "text", nullable: false),
                    issuing_country = table.Column<string>(type: "text", nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    place_of_issue = table.Column<string>(type: "text", nullable: false),
                    is_held_by_company = table.Column<bool>(type: "boolean", nullable: false),
                    returned_to_employee_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    file_url = table.Column<string>(type: "text", nullable: false),
                    renewal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passport_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    adjustment_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_adjustments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_ai_validation_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    validation_type = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    data_json = table.Column<string>(type: "json", nullable: false),
                    is_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "text", nullable: false),
                    is_advisory_only = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_ai_validation_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_allowances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    allowance_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_allowances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_level = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_name = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    metadata_json = table.Column<string>(type: "json", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_cycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_cycles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_deductions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    component_code = table.Column<string>(type: "text", nullable: false),
                    component_name = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_deductions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_earnings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    component_code = table.Column<string>(type: "text", nullable: false),
                    component_name = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_earnings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    exception_type = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_exceptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_payment_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_number = table.Column<string>(type: "text", nullable: false),
                    payment_method = table.Column<string>(type: "text", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    wps_status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_payment_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_payment_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    iban = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    wps_reference = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_payment_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_run_employees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    gross_earnings = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_deductions = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    net_pay = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_run_employees", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    total_gross_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_deductions = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_net_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    employee_count = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    locked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_slips",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department = table.Column<string>(type: "text", nullable: false),
                    basic_salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    housing_allowance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    transport_allowance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    other_allowances = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    gross_salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    deductions = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    net_salary = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    ytd_gross = table.Column<decimal>(type: "numeric", nullable: false),
                    ytd_deductions = table.Column<decimal>(type: "numeric", nullable: false),
                    ytd_net = table.Column<decimal>(type: "numeric", nullable: false),
                    loan_deductions = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_slips", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_validation_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    is_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_validation_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payslip_components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payslip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component_type = table.Column<string>(type: "text", nullable: false),
                    component_name = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payslip_components", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payslips",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    payslip_number = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    is_published_to_ess = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payslips", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: false),
                    new_value = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    performed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_cycle_employees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    scorecard_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    enrolled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_cycle_employees", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_cycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    cycle_type = table.Column<string>(type: "text", nullable: false),
                    review_period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    review_period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    enable_calibration = table.Column<bool>(type: "boolean", nullable: false),
                    enable360_feedback = table.Column<bool>(type: "boolean", nullable: false),
                    enable_self_assessment = table.Column<bool>(type: "boolean", nullable: false),
                    enable_forced_distribution = table.Column<bool>(type: "boolean", nullable: false),
                    self_assessment_deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    manager_review_deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    calibration_deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    default_scorecard_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    launched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_cycles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_improvement_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    trigger_review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performance_gaps = table.Column<string>(type: "text", nullable: false),
                    improvement_goals = table.Column<string>(type: "text", nullable: false),
                    support_plan = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    hr_notes = table.Column<string>(type: "text", nullable: false),
                    manager_notes = table.Column<string>(type: "text", nullable: false),
                    employee_comments = table.Column<string>(type: "text", nullable: false),
                    initiated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initiated_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_improvement_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_rating_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    min_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    max_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    color = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_rating_options", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_rating_scales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    scale_points = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_rating_scales", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_scorecard_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    kpi_weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    competency_weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    attendance_weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    productivity_weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    feedback_weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    discipline_weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    min_passing_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    requires_calibration = table.Column<bool>(type: "boolean", nullable: false),
                    requires360_feedback = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    rating_labels = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_scorecard_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permission_grantor_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grantor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_scope = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    can_sub_delegate = table.Column<bool>(type: "boolean", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission_grantor_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    module = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pip_check_ins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_in_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    checked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checked_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pip_check_ins", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_announcements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    target_plan = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_announcements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_compliance_controls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    control_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    evidence_note = table.Column<string>(type: "text", nullable: true),
                    evidence_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_platform_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_compliance_controls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_config_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by_platform_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_config_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    contact_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    assigned_to = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    converted_to_tenant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_leads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_security_incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reporter = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    affected_systems = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by_platform_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_security_incidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_support_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    started_by_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    started_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    token_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_support_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    full_name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    mfa_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    mfa_secret_encrypted = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    mfa_configured_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_login_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_login_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "policy_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    original_name = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pricing_config",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    group = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    plan = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_config", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "pricing_module_configs",
                columns: table => new
                {
                    module_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    module_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    included_in_trial = table.Column<bool>(type: "boolean", nullable: false),
                    included_in_starter = table.Column<bool>(type: "boolean", nullable: false),
                    included_in_growth = table.Column<bool>(type: "boolean", nullable: false),
                    included_in_enterprise = table.Column<bool>(type: "boolean", nullable: false),
                    is_enterprise_only = table.Column<bool>(type: "boolean", nullable: false),
                    addon_price_monthly = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_module_configs", x => x.module_key);
                });

            migrationBuilder.CreateTable(
                name: "pricing_quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    contact_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    org_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    num_companies = table.Column<int>(type: "integer", nullable: false),
                    num_branches = table.Column<int>(type: "integer", nullable: false),
                    num_employees = table.Column<int>(type: "integer", nullable: false),
                    num_admin_users = table.Column<int>(type: "integer", nullable: false),
                    num_countries = table.Column<int>(type: "integer", nullable: false),
                    needs_arabic = table.Column<bool>(type: "boolean", nullable: false),
                    selected_modules_json = table.Column<string>(type: "json", nullable: false),
                    estimated_monthly_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    estimated_annual_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    converted_to_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_quotes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "probation_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    probation_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    probation_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    review_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    performance_summary = table.Column<string>(type: "text", nullable: false),
                    overall_rating = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    manager_recommendation = table.Column<string>(type: "text", nullable: false),
                    manager_notes = table.Column<string>(type: "text", nullable: false),
                    hr_decision = table.Column<string>(type: "text", nullable: false),
                    hr_notes = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reviewed_by_manager_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by_manager_name = table.Column<string>(type: "text", nullable: false),
                    approved_by_hr_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    manager_reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    hr_approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probation_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotion_recommendations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    current_designation = table.Column<string>(type: "text", nullable: false),
                    proposed_designation = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    recommended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recommended_by_name = table.Column<string>(type: "text", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_recommendations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "public_holiday_calendars",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calendar_year = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_public_holiday_calendars", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "public_holidays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_ar = table.Column<string>(type: "text", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    hijri_date = table.Column<string>(type: "text", nullable: false),
                    is_recurring = table.Column<bool>(type: "boolean", nullable: false),
                    is_optional = table.Column<bool>(type: "boolean", nullable: false),
                    holiday_type = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_public_holidays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "qiwa_api_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    encrypted_client_secret = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    token_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cached_access_token = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qiwa_api_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "QiwaSyncLogs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trigger_source = table.Column<string>(type: "text", nullable: false),
                    request_payload_json = table.Column<string>(type: "text", nullable: true),
                    response_payload_json = table.Column<string>(type: "text", nullable: true),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    triggered_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    max_retries = table.Column<int>(type: "integer", nullable: false),
                    last_retried_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dead_letter_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QiwaSyncLogs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "QiwaTenantConnections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    establishment_id = table.Column<string>(type: "text", nullable: false),
                    establishment_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    unified_organisation_number = table.Column<string>(type: "text", nullable: false),
                    last_connected_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_checked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_message = table.Column<string>(type: "text", nullable: true),
                    configured_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QiwaTenantConnections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recruitment_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    performed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    old_values_json = table.Column<string>(type: "text", nullable: false),
                    new_values_json = table.Column<string>(type: "text", nullable: false),
                    ip_address = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recruitment_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_execution_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    report_key = table.Column<string>(type: "text", nullable: false),
                    report_name = table.Column<string>(type: "text", nullable: false),
                    filters_json = table.Column<string>(type: "json", nullable: false),
                    export_format = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    file_url = table.Column<string>(type: "text", nullable: true),
                    run_by = table.Column<Guid>(type: "uuid", nullable: true),
                    run_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_execution_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_key = table.Column<string>(type: "text", nullable: false),
                    report_name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    filters_json = table.Column<string>(type: "json", nullable: false),
                    frequency = table.Column<string>(type: "text", nullable: false),
                    delivery_method = table.Column<string>(type: "text", nullable: false),
                    recipients = table.Column<string>(type: "text", nullable: false),
                    export_format = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_run_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_run_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_schedules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reporting_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    manager_employee_id = table.Column<int>(type: "integer", nullable: false),
                    relationship_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reporting_lines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resume_parse_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    storage_url = table.Column<string>(type: "text", nullable: false),
                    parsed_text_json = table.Column<string>(type: "json", nullable: false),
                    raw_text = table.Column<string>(type: "text", nullable: false),
                    parse_status = table.Column<string>(type: "text", nullable: false),
                    parsed_by = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    parsed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resume_parse_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role_competencies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    designation_title = table.Column<string>(type: "text", nullable: false),
                    expected_level = table.Column<string>(type: "text", nullable: false),
                    weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_competencies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "salary_advances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    employee_int_id = table.Column<int>(type: "integer", nullable: true),
                    advance_number = table.Column<string>(type: "text", nullable: false),
                    requested_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    approved_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    repayment_type = table.Column<string>(type: "text", nullable: false),
                    installments = table.Column<int>(type: "integer", nullable: false),
                    installment_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    repayment_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    total_repaid = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    outstanding_balance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    is_locked_by_payroll = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_salary_advances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "salary_components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_structure_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    component_type = table.Column<string>(type: "text", nullable: false),
                    calculation_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    is_taxable = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_salary_components", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "salary_structures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_salary_structures", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saved_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    filters_json = table.Column<string>(type: "json", nullable: false),
                    columns_json = table.Column<string>(type: "json", nullable: false),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "text", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "security_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_min_length = table.Column<int>(type: "integer", nullable: false),
                    password_require_uppercase = table.Column<bool>(type: "boolean", nullable: false),
                    password_require_lowercase = table.Column<bool>(type: "boolean", nullable: false),
                    password_require_digit = table.Column<bool>(type: "boolean", nullable: false),
                    password_require_special = table.Column<bool>(type: "boolean", nullable: false),
                    password_expiry_days = table.Column<int>(type: "integer", nullable: false),
                    password_history_count = table.Column<int>(type: "integer", nullable: false),
                    max_failed_login_attempts = table.Column<int>(type: "integer", nullable: false),
                    lockout_duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    session_timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    refresh_token_expiry_days = table.Column<int>(type: "integer", nullable: false),
                    allow_multiple_sessions = table.Column<bool>(type: "boolean", nullable: false),
                    mfa_required = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shift_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    shift_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_name = table.Column<string>(type: "text", nullable: false),
                    shift_code = table.Column<string>(type: "text", nullable: false),
                    shift_color = table.Column<string>(type: "text", nullable: false),
                    assigned_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shift_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    break_minutes = table.Column<int>(type: "integer", nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sif_file_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wps_file_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    iban = table.Column<string>(type: "text", nullable: false),
                    net_pay = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    mol_id = table.Column<string>(type: "text", nullable: false),
                    routing_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sif_file_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "statutory_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    country_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    jurisdiction = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    rule_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    rule_value = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statutory_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    setting_key = table.Column<string>(type: "text", nullable: false),
                    setting_value = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    is_encrypted = table.Column<bool>(type: "boolean", nullable: false),
                    is_read_only = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_ai_usage",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year_month = table.Column<int>(type: "integer", nullable: false),
                    tokens_used = table.Column<long>(type: "bigint", nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false),
                    blocked_count = table.Column<int>(type: "integer", nullable: false),
                    last_updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_ai_usage", x => new { x.tenant_id, x.year_month });
                });

            migrationBuilder.CreateTable(
                name: "tenant_brandings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logo_url = table.Column<string>(type: "text", nullable: false),
                    primary_color = table.Column<string>(type: "text", nullable: false),
                    accent_color = table.Column<string>(type: "text", nullable: false),
                    company_name_en = table.Column<string>(type: "text", nullable: false),
                    company_name_ar = table.Column<string>(type: "text", nullable: false),
                    portal_title = table.Column<string>(type: "text", nullable: false),
                    favicon_url = table.Column<string>(type: "text", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_brandings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_feature_flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_key = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    config_json = table.Column<string>(type: "json", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_feature_flags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_field_help_texts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_field_help_texts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_hr_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    use_dept_head_approval = table.Column<bool>(type: "boolean", nullable: false),
                    use_hr_final_approval = table.Column<bool>(type: "boolean", nullable: false),
                    use_supervisor_before_manager = table.Column<bool>(type: "boolean", nullable: false),
                    allow_dotted_line_approval = table.Column<bool>(type: "boolean", nullable: false),
                    auto_create_dept_on_import = table.Column<bool>(type: "boolean", nullable: false),
                    auto_create_designation_on_import = table.Column<bool>(type: "boolean", nullable: false),
                    require_import_preview_before_commit = table.Column<bool>(type: "boolean", nullable: false),
                    allow_cross_dept_manager = table.Column<bool>(type: "boolean", nullable: false),
                    allow_cross_location_manager = table.Column<bool>(type: "boolean", nullable: false),
                    require_cost_center_for_payroll = table.Column<bool>(type: "boolean", nullable: false),
                    require_grade_for_approval_policy = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_hr_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_invoice_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_invoice_lines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payment_method = table.Column<string>(type: "text", nullable: true),
                    payment_reference = table.Column<string>(type: "text", nullable: true),
                    period_description = table.Column<string>(type: "text", nullable: true),
                    invoice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    paid_date = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    recipient_email = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_invoices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_localization_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_language = table.Column<string>(type: "text", nullable: false),
                    rtl_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    calendar_system = table.Column<string>(type: "text", nullable: false),
                    default_timezone = table.Column<string>(type: "text", nullable: false),
                    date_format = table.Column<string>(type: "text", nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    week_start_day = table.Column<string>(type: "text", nullable: false),
                    work_week = table.Column<string>(type: "text", nullable: false),
                    hijri_dates_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_localization_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    received_by_platform_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_payments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    max_employees = table.Column<int>(type: "integer", nullable: false),
                    max_users = table.Column<int>(type: "integer", nullable: false),
                    max_companies = table.Column<int>(type: "integer", nullable: false),
                    max_admin_users = table.Column<int>(type: "integer", nullable: false),
                    billing_email = table.Column<string>(type: "text", nullable: false),
                    billing_cycle = table.Column<string>(type: "text", nullable: false),
                    monthly_amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "visa_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    visa_type = table.Column<string>(type: "text", nullable: false),
                    visa_number = table.Column<string>(type: "text", nullable: false),
                    iqama_number = table.Column<string>(type: "text", nullable: false),
                    emirates_id_number = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    sponsor = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    file_url = table.Column<string>(type: "text", nullable: false),
                    renewal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visa_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "work_permit_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    permit_number = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    permit_type = table.Column<string>(type: "text", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    issuing_authority = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    file_url = table.Column<string>(type: "text", nullable: false),
                    renewal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_permit_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workforce_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "text", nullable: false),
                    plan_year = table.Column<int>(type: "integer", nullable: false),
                    plan_name = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_name = table.Column<string>(type: "text", nullable: false),
                    current_headcount = table.Column<int>(type: "integer", nullable: false),
                    planned_headcount = table.Column<int>(type: "integer", nullable: false),
                    gap_count = table.Column<int>(type: "integer", nullable: false),
                    budget_allocated = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    budget_utilized = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_name = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workforce_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wps_file_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sif_file_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    generated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_count = table.Column<int>(type: "integer", nullable: false),
                    total_salary_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    file_hash = table.Column<string>(type: "text", nullable: false),
                    format_version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wps_file_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_policy_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    step_name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    approver_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    specific_employee_id = table.Column<int>(type: "integer", nullable: true),
                    approver_role = table.Column<string>(type: "text", nullable: true),
                    escalation_after_hours = table.Column<int>(type: "integer", nullable: true),
                    is_final_step = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_policy_steps", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_policy_steps_approval_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "approval_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_decisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_decisions_approval_requests_approval_request_id",
                        column: x => x.approval_request_id,
                        principalTable: "approval_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_workflow_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    step_name = table.Column<string>(type: "text", nullable: false),
                    approver_role = table.Column<string>(type: "text", nullable: false),
                    approver_type = table.Column<string>(type: "text", nullable: false),
                    specific_employee_id = table.Column<int>(type: "integer", nullable: true),
                    escalation_after_hours = table.Column<int>(type: "integer", nullable: true),
                    is_final_step = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_workflow_steps", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_workflow_steps_approval_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "approval_workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_chunks", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_chunks_policy_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "policy_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    authority_level = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_editable = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_roles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    full_name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    phone_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    preferred_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "en"),
                    timezone = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "UTC"),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Active"),
                    access_mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "FullPortal"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    last_password_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    m_f_a_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    mfa_secret_encrypted = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    mfa_configured_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    mfa_last_verified_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    mfa_failed_count = table.Column<int>(type: "integer", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_login_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.ForeignKey(
                        name: "FK_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_user_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    access_mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    requires_password_setup = table.Column<bool>(type: "boolean", nullable: false),
                    invitation_token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    invitation_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    invited_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    invitation_accepted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    login_disabled_reason = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_user_accounts", x => x.id);
                    table.ForeignKey(
                        name: "FK_employee_user_accounts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_reset_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    replaced_by_token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revoked_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_entity_accesses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_entity_accesses", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_entity_accesses_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_user_entity_accesses_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_permission_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    effect = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_permission_overrides", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_permission_overrides_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_absence_records_tenant_id_employee_id_absence_date",
                table: "absence_records",
                columns: new[] { "tenant_id", "employee_id", "absence_date" });

            migrationBuilder.CreateIndex(
                name: "IX_absence_regularization_requests_tenant_id_status",
                table: "absence_regularization_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_logs_tenant_id_created_at_utc",
                table: "admin_audit_logs",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_logs_tenant_id_entity_type_entity_id",
                table: "admin_audit_logs",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_advance_approvals_tenant_id_advance_id_step_order",
                table: "advance_approvals",
                columns: new[] { "tenant_id", "advance_id", "step_order" });

            migrationBuilder.CreateIndex(
                name: "IX_advance_audit_logs_tenant_id_advance_id",
                table: "advance_audit_logs",
                columns: new[] { "tenant_id", "advance_id" });

            migrationBuilder.CreateIndex(
                name: "IX_advance_installments_tenant_id_advance_id_installment_number",
                table: "advance_installments",
                columns: new[] { "tenant_id", "advance_id", "installment_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_advance_policies_tenant_id_is_active",
                table: "advance_policies",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_hr_query_cache_tenant_id_cache_key",
                table: "ai_hr_query_cache",
                columns: new[] { "tenant_id", "cache_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_hr_query_cache_tenant_id_expires_at_utc",
                table: "ai_hr_query_cache",
                columns: new[] { "tenant_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_hr_query_cache_tenant_id_intent_classified_module",
                table: "ai_hr_query_cache",
                columns: new[] { "tenant_id", "intent_classified", "module" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_hr_query_logs_tenant_id_created_at_utc",
                table: "ai_hr_query_logs",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_hr_query_logs_tenant_id_user_id_created_at_utc",
                table: "ai_hr_query_logs",
                columns: new[] { "tenant_id", "user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_insights_tenant_id_employee_id",
                table: "ai_insights",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_insights_tenant_id_module_insight_type_is_acknowledged",
                table: "ai_insights",
                columns: new[] { "tenant_id", "module", "insight_type", "is_acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_configs_tenant_id_use_case_is_active",
                table: "ai_model_configs",
                columns: new[] { "tenant_id", "use_case", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_recommendations_tenant_id_employee_id",
                table: "ai_recommendations",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_recommendations_tenant_id_module_status",
                table: "ai_recommendations",
                columns: new[] { "tenant_id", "module", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_application_events_tenant_id_application_id_created_at_utc",
                table: "application_events",
                columns: new[] { "tenant_id", "application_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_appeals_tenant_id_review_id",
                table: "appraisal_appeals",
                columns: new[] { "tenant_id", "review_id" });

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_appeals_tenant_id_status",
                table: "appraisal_appeals",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_calibrations_tenant_id_cycle_id",
                table: "appraisal_calibrations",
                columns: new[] { "tenant_id", "cycle_id" });

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_calibrations_tenant_id_review_id",
                table: "appraisal_calibrations",
                columns: new[] { "tenant_id", "review_id" });

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_competency_ratings_tenant_id_review_id_competency~",
                table: "appraisal_competency_ratings",
                columns: new[] { "tenant_id", "review_id", "competency_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_reviews_tenant_id_cycle_id_employee_id",
                table: "appraisal_reviews",
                columns: new[] { "tenant_id", "cycle_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_reviews_tenant_id_department_name",
                table: "appraisal_reviews",
                columns: new[] { "tenant_id", "department_name" });

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_reviews_tenant_id_status",
                table: "appraisal_reviews",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_appraisal_score_breakdowns_tenant_id_review_id",
                table: "appraisal_score_breakdowns",
                columns: new[] { "tenant_id", "review_id" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_authorities_tenant_id_employee_id_authority_scope_~",
                table: "approval_authorities",
                columns: new[] { "tenant_id", "employee_id", "authority_scope", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_approval_request_id",
                table: "approval_decisions",
                column: "approval_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_tenant_id_approval_request_id_step_order",
                table: "approval_decisions",
                columns: new[] { "tenant_id", "approval_request_id", "step_order" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_delegations_tenant_id_from_employee_id_to_employee~",
                table: "approval_delegations",
                columns: new[] { "tenant_id", "from_employee_id", "to_employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_delegations_tenant_id_start_date_end_date",
                table: "approval_delegations",
                columns: new[] { "tenant_id", "start_date", "end_date" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_policies_tenant_id_workflow_type_department_id_gra~",
                table: "approval_policies",
                columns: new[] { "tenant_id", "workflow_type", "department_id", "grade_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_approval_policies_tenant_id_workflow_type_is_default_is_act~",
                table: "approval_policies",
                columns: new[] { "tenant_id", "workflow_type", "is_default", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_policy_steps_policy_id",
                table: "approval_policy_steps",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "IX_approval_policy_steps_tenant_id_policy_id_step_order",
                table: "approval_policy_steps",
                columns: new[] { "tenant_id", "policy_id", "step_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_approval_requests_tenant_id_entity_name_entity_id_status",
                table: "approval_requests",
                columns: new[] { "tenant_id", "entity_name", "entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_workflow_steps_tenant_id_workflow_id_step_order",
                table: "approval_workflow_steps",
                columns: new[] { "tenant_id", "workflow_id", "step_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_approval_workflow_steps_workflow_id",
                table: "approval_workflow_steps",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "IX_approval_workflows_tenant_id_code",
                table: "approval_workflows",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assessment_questions_tenant_id_template_id_order_index",
                table: "assessment_questions",
                columns: new[] { "tenant_id", "template_id", "order_index" });

            migrationBuilder.CreateIndex(
                name: "IX_assessment_templates_tenant_id_code",
                table: "assessment_templates",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assessment_templates_tenant_id_is_active_is_deleted",
                table: "assessment_templates",
                columns: new[] { "tenant_id", "is_active", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_ai_insights_tenant_id_insight_type_is_acknowledg~",
                table: "attendance_ai_insights",
                columns: new[] { "tenant_id", "insight_type", "is_acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_audit_logs_tenant_id_entity_name_entity_id_creat~",
                table: "attendance_audit_logs",
                columns: new[] { "tenant_id", "entity_name", "entity_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_correction_approvals_tenant_id_regularization_re~",
                table: "attendance_correction_approvals",
                columns: new[] { "tenant_id", "regularization_request_id", "approval_level" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_daily_records_tenant_id_employee_id_work_date",
                table: "attendance_daily_records",
                columns: new[] { "tenant_id", "employee_id", "work_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_daily_records_tenant_id_missing_punch",
                table: "attendance_daily_records",
                columns: new[] { "tenant_id", "missing_punch" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_daily_records_tenant_id_work_date_status",
                table: "attendance_daily_records",
                columns: new[] { "tenant_id", "work_date", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_device_connectors_tenant_id_connector_code",
                table: "attendance_device_connectors",
                columns: new[] { "tenant_id", "connector_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_device_sync_logs_tenant_id_device_id_started_at_~",
                table: "attendance_device_sync_logs",
                columns: new[] { "tenant_id", "device_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_devices_tenant_id_is_deleted",
                table: "attendance_devices",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_devices_tenant_id_serial_number",
                table: "attendance_devices",
                columns: new[] { "tenant_id", "serial_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_devices_tenant_id_vendor_device_type_is_active",
                table: "attendance_devices",
                columns: new[] { "tenant_id", "vendor", "device_type", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_exceptions_tenant_id_work_date_exception_type_is~",
                table: "attendance_exceptions",
                columns: new[] { "tenant_id", "work_date", "exception_type", "is_resolved" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_geofences_tenant_id_attendance_location_id",
                table: "attendance_geofences",
                columns: new[] { "tenant_id", "attendance_location_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_import_batches_tenant_id_created_at_utc",
                table: "attendance_import_batches",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_import_errors_tenant_id_import_batch_id",
                table: "attendance_import_errors",
                columns: new[] { "tenant_id", "import_batch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_locations_tenant_id_branch_id",
                table: "attendance_locations",
                columns: new[] { "tenant_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_lock_periods_tenant_id_period_start_period_end_l~",
                table: "attendance_lock_periods",
                columns: new[] { "tenant_id", "period_start", "period_end", "lock_type" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_payroll_impacts_tenant_id_employee_id_work_date",
                table: "attendance_payroll_impacts",
                columns: new[] { "tenant_id", "employee_id", "work_date" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_payroll_impacts_tenant_id_status",
                table: "attendance_payroll_impacts",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_policies_tenant_id_code",
                table: "attendance_policies",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_policies_tenant_id_is_active",
                table: "attendance_policies",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_raw_events_tenant_id_employee_id_punch_timestamp~",
                table: "attendance_raw_events",
                columns: new[] { "tenant_id", "employee_id", "punch_timestamp_utc", "punch_direction", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_raw_events_tenant_id_is_processed_punch_timestam~",
                table: "attendance_raw_events",
                columns: new[] { "tenant_id", "is_processed", "punch_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_raw_events_tenant_id_sync_batch_reference",
                table: "attendance_raw_events",
                columns: new[] { "tenant_id", "sync_batch_reference" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_tenant_id_employee_id_work_date",
                table: "attendance_records",
                columns: new[] { "tenant_id", "employee_id", "work_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_tenant_id_work_date_status",
                table: "attendance_records",
                columns: new[] { "tenant_id", "work_date", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_regularization_requests_tenant_id_employee_id_wo~",
                table: "attendance_regularization_requests",
                columns: new[] { "tenant_id", "employee_id", "work_date" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_regularization_requests_tenant_id_status",
                table: "attendance_regularization_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_rules_tenant_id_attendance_policy_id_rule_type",
                table: "attendance_rules",
                columns: new[] { "tenant_id", "attendance_policy_id", "rule_type" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_tenant_id_created_at_utc",
                table: "audit_logs",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_tenant_id_entity_name_entity_id_created_at_utc",
                table: "audit_logs",
                columns: new[] { "tenant_id", "entity_name", "entity_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_user_id_created_at_utc",
                table: "audit_logs",
                columns: new[] { "user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_transfer_files_tenant_id_payment_batch_id",
                table: "bank_transfer_files",
                columns: new[] { "tenant_id", "payment_batch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bonus_approvals_tenant_id_bonus_batch_id_step_order",
                table: "bonus_approvals",
                columns: new[] { "tenant_id", "bonus_batch_id", "step_order" });

            migrationBuilder.CreateIndex(
                name: "IX_bonus_audit_logs_tenant_id_bonus_batch_id",
                table: "bonus_audit_logs",
                columns: new[] { "tenant_id", "bonus_batch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bonus_batches_tenant_id_batch_number",
                table: "bonus_batches",
                columns: new[] { "tenant_id", "batch_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bonus_batches_tenant_id_status",
                table: "bonus_batches",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_bonus_recommendations_tenant_id_status",
                table: "bonus_recommendations",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_bonus_types_tenant_id_code",
                table: "bonus_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id_code",
                table: "branches",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id_company_id",
                table: "branches",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id_is_deleted",
                table: "branches",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_burnout_risk_signals_tenant_id_employee_id_detected_date",
                table: "burnout_risk_signals",
                columns: new[] { "tenant_id", "employee_id", "detected_date" });

            migrationBuilder.CreateIndex(
                name: "IX_burnout_risk_signals_tenant_id_signal_type_is_acknowledged",
                table: "burnout_risk_signals",
                columns: new[] { "tenant_id", "signal_type", "is_acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_ai_scores_tenant_id_candidate_id_job_opening_id",
                table: "candidate_ai_scores",
                columns: new[] { "tenant_id", "candidate_id", "job_opening_id" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_assessments_tenant_id_application_id",
                table: "candidate_assessments",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_assessments_tenant_id_candidate_id_status",
                table: "candidate_assessments",
                columns: new[] { "tenant_id", "candidate_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_assessments_tenant_id_invitation_token",
                table: "candidate_assessments",
                columns: new[] { "tenant_id", "invitation_token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candidate_documents_tenant_id_application_id",
                table: "candidate_documents",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_documents_tenant_id_candidate_id_is_deleted",
                table: "candidate_documents",
                columns: new[] { "tenant_id", "candidate_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_candidates_tenant_id_email",
                table: "candidates",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candidates_tenant_id_status",
                table: "candidates",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_off_credits_tenant_id_employee_id_status",
                table: "comp_off_credits",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_companies_tenant_id_is_deleted",
                table: "companies",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_companies_tenant_id_legal_name_en",
                table: "companies",
                columns: new[] { "tenant_id", "legal_name_en" });

            migrationBuilder.CreateIndex(
                name: "IX_companies_tenant_id_registration_number",
                table: "companies",
                columns: new[] { "tenant_id", "registration_number" });

            migrationBuilder.CreateIndex(
                name: "IX_competencies_tenant_id_category_is_active",
                table: "competencies",
                columns: new[] { "tenant_id", "category", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_ai_insights_tenant_id_employee_id",
                table: "compliance_ai_insights",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_ai_insights_tenant_id_insight_type_is_acknowledg~",
                table: "compliance_ai_insights",
                columns: new[] { "tenant_id", "insight_type", "is_acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_audit_logs_tenant_id_employee_id_created_at_utc",
                table: "compliance_audit_logs",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_audit_logs_tenant_id_entity_type_entity_id",
                table: "compliance_audit_logs",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_reminders_tenant_id_employee_id_status",
                table: "compliance_reminders",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_reminders_tenant_id_reminder_type_status",
                table: "compliance_reminders",
                columns: new[] { "tenant_id", "reminder_type", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_renewals_tenant_id_employee_id_status",
                table: "compliance_renewals",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_renewals_tenant_id_expiry_date_status",
                table: "compliance_renewals",
                columns: new[] { "tenant_id", "expiry_date", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_requirements_tenant_id_doc_type_id_country_code",
                table: "compliance_requirements",
                columns: new[] { "tenant_id", "doc_type_id", "country_code" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_requirements_tenant_id_is_active",
                table: "compliance_requirements",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_continuous_feedback_tenant_id_employee_id",
                table: "continuous_feedback",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_continuous_feedback_tenant_id_feedback_type",
                table: "continuous_feedback",
                columns: new[] { "tenant_id", "feedback_type" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_templates_tenant_id_code",
                table: "contract_templates",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contract_templates_tenant_id_is_active_is_deleted",
                table: "contract_templates",
                columns: new[] { "tenant_id", "is_active", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_cost_centers_tenant_id_code",
                table: "cost_centers",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cost_centers_tenant_id_company_id",
                table: "cost_centers",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cost_centers_tenant_id_is_deleted",
                table: "cost_centers",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_country_payroll_rules_tenant_id_country_code_rule_key_effec~",
                table: "country_payroll_rules",
                columns: new[] { "tenant_id", "country_code", "rule_key", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_branch_id",
                table: "departments",
                columns: new[] { "tenant_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_code",
                table: "departments",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_is_deleted",
                table: "departments",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_parent_department_id",
                table: "departments",
                columns: new[] { "tenant_id", "parent_department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_code",
                table: "designations",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_department_id",
                table: "designations",
                columns: new[] { "tenant_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_is_deleted",
                table: "designations",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_doc_types_tenant_id_code",
                table: "doc_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_doc_types_tenant_id_is_active_is_deleted",
                table: "doc_types",
                columns: new[] { "tenant_id", "is_active", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_document_chunks_document_id",
                table: "document_chunks",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_chunks_tenant_id_document_id_chunk_index",
                table: "document_chunks",
                columns: new[] { "tenant_id", "document_id", "chunk_index" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_action_items_tenant_id_employee_id_status",
                table: "employee_action_items",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_ai_query_logs_tenant_id_employee_id_created_at_utc",
                table: "employee_ai_query_logs",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_announcements_tenant_id_is_active_published_at_utc",
                table: "employee_announcements",
                columns: new[] { "tenant_id", "is_active", "published_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_bonuses_tenant_id_bonus_batch_id_employee_id",
                table: "employee_bonuses",
                columns: new[] { "tenant_id", "bonus_batch_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_bonuses_tenant_id_employee_id_status",
                table: "employee_bonuses",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_change_requests_tenant_id_employee_id_status",
                table: "employee_change_requests",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_churn_predictions_tenant_id_employee_id_computed_a~",
                table: "employee_churn_predictions",
                columns: new[] { "tenant_id", "employee_id", "computed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_compliance_records_tenant_id_employee_id_country_c~",
                table: "employee_compliance_records",
                columns: new[] { "tenant_id", "employee_id", "country_code", "field_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_compliance_records_tenant_id_expiry_date",
                table: "employee_compliance_records",
                columns: new[] { "tenant_id", "expiry_date" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_contracts_tenant_id_contract_number",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "contract_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_contracts_tenant_id_employee_id_status",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_contracts_tenant_id_is_deleted",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_dependents_tenant_id_employee_id",
                table: "employee_dependents",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_document_requests_tenant_id_employee_id_status",
                table: "employee_document_requests",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_document_versions_tenant_id_employee_document_id_v~",
                table: "employee_document_versions",
                columns: new[] { "tenant_id", "employee_document_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_documents_tenant_id_draft_id",
                table: "employee_documents",
                columns: new[] { "tenant_id", "draft_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_documents_tenant_id_employee_id",
                table: "employee_documents",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_documents_tenant_id_employee_id_document_type_is_d~",
                table: "employee_documents",
                columns: new[] { "tenant_id", "employee_id", "document_type", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_drafts_tenant_id_status",
                table: "employee_drafts",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_goals_tenant_id_cycle_id",
                table: "employee_goals",
                columns: new[] { "tenant_id", "cycle_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_goals_tenant_id_employee_id_status",
                table: "employee_goals",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_histories_tenant_id_employee_id_created_at_utc",
                table: "employee_histories",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_id_rules_tenant_id_company_id_is_active",
                table: "employee_id_rules",
                columns: new[] { "tenant_id", "company_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_leave_balances_tenant_id_employee_id_leave_type_id~",
                table: "employee_leave_balances",
                columns: new[] { "tenant_id", "employee_id", "leave_type_id", "year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_loans_tenant_id_employee_id_status",
                table: "employee_loans",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_loans_tenant_id_loan_number",
                table: "employee_loans",
                columns: new[] { "tenant_id", "loan_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_mobile_devices_tenant_id_employee_id_device_identi~",
                table: "employee_mobile_devices",
                columns: new[] { "tenant_id", "employee_id", "device_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_notification_preferences_tenant_id_employee_id",
                table: "employee_notification_preferences",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_notifications_tenant_id_employee_id_created_at_utc",
                table: "employee_notifications",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_notifications_tenant_id_employee_id_is_read",
                table: "employee_notifications",
                columns: new[] { "tenant_id", "employee_id", "is_read" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_payroll_profiles_tenant_id_employee_id",
                table: "employee_payroll_profiles",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_payslip_access_logs_tenant_id_employee_id_payslip_~",
                table: "employee_payslip_access_logs",
                columns: new[] { "tenant_id", "employee_id", "payslip_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_policy_acknowledgements_tenant_id_employee_id_poli~",
                table: "employee_policy_acknowledgements",
                columns: new[] { "tenant_id", "employee_id", "policy_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_profile_change_requests_tenant_id_employee_id_stat~",
                table: "employee_profile_change_requests",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_risk_scores_tenant_id_employee_id_computed_at_utc",
                table: "employee_risk_scores",
                columns: new[] { "tenant_id", "employee_id", "computed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_risk_scores_tenant_id_overall_risk_level",
                table: "employee_risk_scores",
                columns: new[] { "tenant_id", "overall_risk_level" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_salary_structures_tenant_id_employee_id_is_active",
                table: "employee_salary_structures",
                columns: new[] { "tenant_id", "employee_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_self_service_audit_logs_tenant_id_employee_id_crea~",
                table: "employee_self_service_audit_logs",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_sentiment_pulses_tenant_id_employee_id_created_at_~",
                table: "employee_sentiment_pulses",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_status_histories_tenant_id_employee_id_created_at_~",
                table: "employee_status_histories",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_transfer_requests_tenant_id_employee_id_status",
                table: "employee_transfer_requests",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_user_accounts_tenant_id_employee_id_is_primary",
                table: "employee_user_accounts",
                columns: new[] { "tenant_id", "employee_id", "is_primary" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_user_accounts_tenant_id_invitation_token_hash",
                table: "employee_user_accounts",
                columns: new[] { "tenant_id", "invitation_token_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_user_accounts_tenant_id_user_id",
                table: "employee_user_accounts",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_user_accounts_user_id",
                table: "employee_user_accounts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_id_department",
                table: "employees",
                columns: new[] { "tenant_id", "department" });

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_id_employee_code",
                table: "employees",
                columns: new[] { "tenant_id", "employee_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_id_is_deleted",
                table: "employees",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_id_status",
                table: "employees",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_eosb_calculations_tenant_id_employee_id_status",
                table: "eosb_calculations",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_ess_dashboard_preferences_tenant_id_employee_id",
                table: "ess_dashboard_preferences",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedback_360_tenant_id_review_id",
                table: "feedback_360",
                columns: new[] { "tenant_id", "review_id" });

            migrationBuilder.CreateIndex(
                name: "IX_finance_gl_entries_tenant_id_period",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "period" });

            migrationBuilder.CreateIndex(
                name: "IX_finance_gl_entries_tenant_id_source_module_source_entity_id",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "source_module", "source_entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_years_tenant_id_is_current",
                table: "fiscal_years",
                columns: new[] { "tenant_id", "is_current" });

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_years_tenant_id_year",
                table: "fiscal_years",
                columns: new[] { "tenant_id", "year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gcc_compliance_settings_tenant_id_country_code",
                table: "gcc_compliance_settings",
                columns: new[] { "tenant_id", "country_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_goal_progress_updates_tenant_id_goal_id",
                table: "goal_progress_updates",
                columns: new[] { "tenant_id", "goal_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gosi_contribution_rules_tenant_id_classification_branch_pay~",
                table: "gosi_contribution_rules",
                columns: new[] { "tenant_id", "classification", "branch", "payer", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_grades_tenant_id_code",
                table: "grades",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grades_tenant_id_is_deleted",
                table: "grades",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_request_attachments_tenant_id_h_r_request_id",
                table: "hr_request_attachments",
                columns: new[] { "tenant_id", "h_r_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_request_categories_tenant_id_code",
                table: "hr_request_categories",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_hr_request_comments_tenant_id_h_r_request_id_created_at_utc",
                table: "hr_request_comments",
                columns: new[] { "tenant_id", "h_r_request_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_request_slas_tenant_id_category_id_priority",
                table: "hr_request_slas",
                columns: new[] { "tenant_id", "category_id", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_requests_tenant_id_due_at_utc",
                table: "hr_requests",
                columns: new[] { "tenant_id", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_requests_tenant_id_employee_id_status",
                table: "hr_requests",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_increment_recommendations_tenant_id_status",
                table: "increment_recommendations",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_interview_feedbacks_tenant_id_application_id",
                table: "interview_feedbacks",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_interview_feedbacks_tenant_id_interview_schedule_id",
                table: "interview_feedbacks",
                columns: new[] { "tenant_id", "interview_schedule_id" });

            migrationBuilder.CreateIndex(
                name: "IX_interview_schedules_tenant_id_application_id",
                table: "interview_schedules",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_tenant_id_job_opening_id_candidate_id",
                table: "job_applications",
                columns: new[] { "tenant_id", "job_opening_id", "candidate_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_tenant_id_job_opening_id_stage",
                table: "job_applications",
                columns: new[] { "tenant_id", "job_opening_id", "stage" });

            migrationBuilder.CreateIndex(
                name: "IX_job_openings_tenant_id_job_code",
                table: "job_openings",
                columns: new[] { "tenant_id", "job_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_openings_tenant_id_status",
                table: "job_openings",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_accrual_rules_tenant_id_leave_policy_id_is_active",
                table: "leave_accrual_rules",
                columns: new[] { "tenant_id", "leave_policy_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_ai_insights_tenant_id_insight_type_is_acknowledged",
                table: "leave_ai_insights",
                columns: new[] { "tenant_id", "insight_type", "is_acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_approvals_tenant_id_leave_request_id",
                table: "leave_approvals",
                columns: new[] { "tenant_id", "leave_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_attachments_tenant_id_leave_request_id",
                table: "leave_attachments",
                columns: new[] { "tenant_id", "leave_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_audit_logs_tenant_id_entity_type_entity_id",
                table: "leave_audit_logs",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_balance_transactions_tenant_id_employee_id_leave_type~",
                table: "leave_balance_transactions",
                columns: new[] { "tenant_id", "employee_id", "leave_type_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_blackout_dates_tenant_id_start_date",
                table: "leave_blackout_dates",
                columns: new[] { "tenant_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_delegations_tenant_id_employee_id_status",
                table: "leave_delegations",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_encashment_requests_tenant_id_status",
                table: "leave_encashment_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_payroll_impacts_tenant_id_status",
                table: "leave_payroll_impacts",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_policies_tenant_id_leave_type_id_status",
                table: "leave_policies",
                columns: new[] { "tenant_id", "leave_type_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_policy_eligibilities_tenant_id_leave_policy_id_is_act~",
                table: "leave_policy_eligibilities",
                columns: new[] { "tenant_id", "leave_policy_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_request_dates_tenant_id_leave_request_id_leave_date",
                table: "leave_request_dates",
                columns: new[] { "tenant_id", "leave_request_id", "leave_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_tenant_id_employee_id_start_date",
                table: "leave_requests",
                columns: new[] { "tenant_id", "employee_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_tenant_id_status",
                table: "leave_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_types_tenant_id_code",
                table: "leave_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leave_types_tenant_id_is_active",
                table: "leave_types",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_approvals_tenant_id_loan_id_step_order",
                table: "loan_approvals",
                columns: new[] { "tenant_id", "loan_id", "step_order" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_audit_logs_tenant_id_loan_id",
                table: "loan_audit_logs",
                columns: new[] { "tenant_id", "loan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_installments_tenant_id_loan_id_installment_number",
                table: "loan_installments",
                columns: new[] { "tenant_id", "loan_id", "installment_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loan_installments_tenant_id_status",
                table: "loan_installments",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_policies_tenant_id_loan_type_id",
                table: "loan_policies",
                columns: new[] { "tenant_id", "loan_type_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_settlements_tenant_id_loan_id",
                table: "loan_settlements",
                columns: new[] { "tenant_id", "loan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_types_tenant_id_code",
                table: "loan_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_tenant_id_code",
                table: "locations",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_tenant_id_is_active",
                table: "locations",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_login_activity_occurred_at_utc",
                table: "login_activity",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_login_activity_tenant_id",
                table: "login_activity",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_login_activity_tenant_id_event_type_occurred_at_utc",
                table: "login_activity",
                columns: new[] { "tenant_id", "event_type", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_login_activity_user_id",
                table: "login_activity",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_manpower_requisitions_tenant_id_requisition_number",
                table: "manpower_requisitions",
                columns: new[] { "tenant_id", "requisition_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manpower_requisitions_tenant_id_status",
                table: "manpower_requisitions",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_master_data_types_tenant_id_code",
                table: "master_data_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_master_data_types_tenant_id_is_active",
                table: "master_data_types",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_master_data_values_tenant_id_type_id_code",
                table: "master_data_values",
                columns: new[] { "tenant_id", "type_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_master_data_values_tenant_id_type_id_is_active",
                table: "master_data_values",
                columns: new[] { "tenant_id", "type_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_mfa_challenge_tokens_expires_at_utc",
                table: "mfa_challenge_tokens",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_mfa_challenge_tokens_token_hash",
                table: "mfa_challenge_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_templates_tenant_id_code_channel",
                table: "notification_templates",
                columns: new[] { "tenant_id", "code", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_templates_tenant_id_event_type",
                table: "notification_templates",
                columns: new[] { "tenant_id", "event_type" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_tenant_id_user_id_status_created_at_utc",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_numbering_rules_tenant_id_entity_type",
                table: "numbering_rules",
                columns: new[] { "tenant_id", "entity_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_approvals_tenant_id_application_id_status",
                table: "offer_approvals",
                columns: new[] { "tenant_id", "application_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_approvals_tenant_id_offer_letter_id_step_order",
                table: "offer_approvals",
                columns: new[] { "tenant_id", "offer_letter_id", "step_order" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_letters_tenant_id_application_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_checklists_tenant_id_code",
                table: "onboarding_checklists",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_checklists_tenant_id_is_active_is_deleted",
                table: "onboarding_checklists",
                columns: new[] { "tenant_id", "is_active", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_tasks_tenant_id_application_id_status",
                table: "onboarding_tasks",
                columns: new[] { "tenant_id", "application_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_tasks_tenant_id_checklist_id_order_index",
                table: "onboarding_tasks",
                columns: new[] { "tenant_id", "checklist_id", "order_index" });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_tasks_tenant_id_employee_id_status",
                table: "onboarding_tasks",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_approvals_tenant_id_overtime_request_id",
                table: "overtime_approvals",
                columns: new[] { "tenant_id", "overtime_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_audit_logs_tenant_id_entity_name_entity_id",
                table: "overtime_audit_logs",
                columns: new[] { "tenant_id", "entity_name", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_budgets_tenant_id_year_month",
                table: "overtime_budgets",
                columns: new[] { "tenant_id", "year", "month" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_calculations_tenant_id_overtime_request_id",
                table: "overtime_calculations",
                columns: new[] { "tenant_id", "overtime_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_multipliers_tenant_id_overtime_policy_id_day_categ~",
                table: "overtime_multipliers",
                columns: new[] { "tenant_id", "overtime_policy_id", "day_category" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_payroll_impacts_tenant_id_employee_id_status",
                table: "overtime_payroll_impacts",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_policies_tenant_id_code",
                table: "overtime_policies",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_overtime_requests_tenant_id_employee_id_work_date",
                table: "overtime_requests",
                columns: new[] { "tenant_id", "employee_id", "work_date" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_requests_tenant_id_status",
                table: "overtime_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_rules_tenant_id_overtime_policy_id_rule_type",
                table: "overtime_rules",
                columns: new[] { "tenant_id", "overtime_policy_id", "rule_type" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_types_tenant_id_code",
                table: "overtime_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_passport_records_tenant_id_employee_id_is_deleted",
                table: "passport_records",
                columns: new[] { "tenant_id", "employee_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_passport_records_tenant_id_expiry_date_status",
                table: "passport_records",
                columns: new[] { "tenant_id", "expiry_date", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_passport_records_tenant_id_passport_number",
                table: "passport_records",
                columns: new[] { "tenant_id", "passport_number" });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_token_hash",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_tenant_id_payroll_run_id_employee_id",
                table: "payroll_adjustments",
                columns: new[] { "tenant_id", "payroll_run_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_ai_validation_results_tenant_id_is_resolved",
                table: "payroll_ai_validation_results",
                columns: new[] { "tenant_id", "is_resolved" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_ai_validation_results_tenant_id_payroll_run_id_seve~",
                table: "payroll_ai_validation_results",
                columns: new[] { "tenant_id", "payroll_run_id", "severity" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_approvals_tenant_id_payroll_run_id",
                table: "payroll_approvals",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_audit_logs_tenant_id_entity_name_entity_id",
                table: "payroll_audit_logs",
                columns: new[] { "tenant_id", "entity_name", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_cycles_tenant_id_year_month",
                table: "payroll_cycles",
                columns: new[] { "tenant_id", "year", "month" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_deductions_tenant_id_payroll_run_id_employee_id",
                table: "payroll_deductions",
                columns: new[] { "tenant_id", "payroll_run_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_earnings_tenant_id_payroll_run_id_employee_id",
                table: "payroll_earnings",
                columns: new[] { "tenant_id", "payroll_run_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_exceptions_tenant_id_payroll_run_id_status",
                table: "payroll_exceptions",
                columns: new[] { "tenant_id", "payroll_run_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_groups_tenant_id_code",
                table: "payroll_groups",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_payment_batches_tenant_id_payroll_run_id",
                table: "payroll_payment_batches",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_payment_records_tenant_id_payment_batch_id_employee~",
                table: "payroll_payment_records",
                columns: new[] { "tenant_id", "payment_batch_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_employees_tenant_id_payroll_run_id_employee_id",
                table: "payroll_run_employees",
                columns: new[] { "tenant_id", "payroll_run_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_tenant_id_company_id_status",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_tenant_id_company_id_year_month",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "company_id", "year", "month" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_slips_tenant_id_run_id_employee_id",
                table: "payroll_slips",
                columns: new[] { "tenant_id", "run_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_validation_results_tenant_id_payroll_run_id_severity",
                table: "payroll_validation_results",
                columns: new[] { "tenant_id", "payroll_run_id", "severity" });

            migrationBuilder.CreateIndex(
                name: "IX_payslip_components_tenant_id_payslip_id",
                table: "payslip_components",
                columns: new[] { "tenant_id", "payslip_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payslips_tenant_id_payroll_run_id_employee_id",
                table: "payslips",
                columns: new[] { "tenant_id", "payroll_run_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_performance_audit_logs_tenant_id_created_at_utc",
                table: "performance_audit_logs",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_audit_logs_tenant_id_entity_type_entity_id",
                table: "performance_audit_logs",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_cycle_employees_tenant_id_cycle_id_employee_id",
                table: "performance_cycle_employees",
                columns: new[] { "tenant_id", "cycle_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_performance_cycle_employees_tenant_id_cycle_id_status",
                table: "performance_cycle_employees",
                columns: new[] { "tenant_id", "cycle_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_cycles_tenant_id_status",
                table: "performance_cycles",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_improvement_plans_tenant_id_employee_id_status",
                table: "performance_improvement_plans",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_rating_options_tenant_id_scale_id",
                table: "performance_rating_options",
                columns: new[] { "tenant_id", "scale_id" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_rating_scales_tenant_id_is_default",
                table: "performance_rating_scales",
                columns: new[] { "tenant_id", "is_default" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_scorecard_templates_tenant_id_is_active",
                table: "performance_scorecard_templates",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_permission_grantor_records_tenant_id_grantor_user_id_is_act~",
                table: "permission_grantor_records",
                columns: new[] { "tenant_id", "grantor_user_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_permissions_permission_key",
                table: "permissions",
                column: "permission_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pip_check_ins_tenant_id_pip_id",
                table: "pip_check_ins",
                columns: new[] { "tenant_id", "pip_id" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_compliance_controls_category_control_id",
                table: "platform_compliance_controls",
                columns: new[] { "category", "control_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_config_entries_key",
                table: "platform_config_entries",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_security_incidents_status_severity",
                table: "platform_security_incidents",
                columns: new[] { "status", "severity" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_support_sessions_target_user_id",
                table: "platform_support_sessions",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_platform_support_sessions_tenant_id_started_at_utc",
                table: "platform_support_sessions",
                columns: new[] { "tenant_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_email",
                table: "platform_users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policy_documents_tenant_id_is_deleted",
                table: "policy_documents",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_policy_documents_tenant_id_status",
                table: "policy_documents",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_pricing_quotes_created_at_utc",
                table: "pricing_quotes",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_pricing_quotes_status",
                table: "pricing_quotes",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_probation_reviews_tenant_id_employee_id_status",
                table: "probation_reviews",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_recommendations_tenant_id_status",
                table: "promotion_recommendations",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_public_holiday_calendars_tenant_id_country_code_calendar_ye~",
                table: "public_holiday_calendars",
                columns: new[] { "tenant_id", "country_code", "calendar_year" });

            migrationBuilder.CreateIndex(
                name: "IX_public_holidays_tenant_id_calendar_id_date",
                table: "public_holidays",
                columns: new[] { "tenant_id", "calendar_id", "date" });

            migrationBuilder.CreateIndex(
                name: "IX_qiwa_api_credentials_tenant_id",
                table: "qiwa_api_credentials",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QiwaSyncLogs_tenant_id_status",
                table: "QiwaSyncLogs",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_recruitment_audit_logs_tenant_id_entity_type_entity_id_crea~",
                table: "recruitment_audit_logs",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_execution_logs_tenant_id_created_at_utc",
                table: "report_execution_logs",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_report_execution_logs_tenant_id_report_key",
                table: "report_execution_logs",
                columns: new[] { "tenant_id", "report_key" });

            migrationBuilder.CreateIndex(
                name: "IX_report_schedules_tenant_id_is_active",
                table: "report_schedules",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_reporting_lines_tenant_id_employee_id_relationship_type_is_~",
                table: "reporting_lines",
                columns: new[] { "tenant_id", "employee_id", "relationship_type", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_reporting_lines_tenant_id_manager_employee_id_is_active",
                table: "reporting_lines",
                columns: new[] { "tenant_id", "manager_employee_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_resume_parse_results_tenant_id_candidate_id",
                table: "resume_parse_results",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "IX_resume_parse_results_tenant_id_parse_status",
                table: "resume_parse_results",
                columns: new[] { "tenant_id", "parse_status" });

            migrationBuilder.CreateIndex(
                name: "IX_role_competencies_tenant_id_department_name",
                table: "role_competencies",
                columns: new[] { "tenant_id", "department_name" });

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_permission_id",
                table: "role_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "IX_roles_tenant_id_normalized_name",
                table: "roles",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_salary_advances_tenant_id_advance_number",
                table: "salary_advances",
                columns: new[] { "tenant_id", "advance_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_salary_advances_tenant_id_employee_id_status",
                table: "salary_advances",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_salary_components_tenant_id_salary_structure_id_code",
                table: "salary_components",
                columns: new[] { "tenant_id", "salary_structure_id", "code" });

            migrationBuilder.CreateIndex(
                name: "IX_salary_structures_tenant_id_code",
                table: "salary_structures",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_salary_structures_tenant_id_company_id",
                table: "salary_structures",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_saved_reports_tenant_id_category",
                table: "saved_reports",
                columns: new[] { "tenant_id", "category" });

            migrationBuilder.CreateIndex(
                name: "IX_saved_reports_tenant_id_created_by",
                table: "saved_reports",
                columns: new[] { "tenant_id", "created_by" });

            migrationBuilder.CreateIndex(
                name: "IX_security_settings_tenant_id",
                table: "security_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shift_assignments_tenant_id_assigned_date",
                table: "shift_assignments",
                columns: new[] { "tenant_id", "assigned_date" });

            migrationBuilder.CreateIndex(
                name: "IX_shift_assignments_tenant_id_employee_id_assigned_date",
                table: "shift_assignments",
                columns: new[] { "tenant_id", "employee_id", "assigned_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shift_definitions_tenant_id_code",
                table: "shift_definitions",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sif_file_records_tenant_id_wps_file_batch_id",
                table: "sif_file_records",
                columns: new[] { "tenant_id", "wps_file_batch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_statutory_rules_tenant_id_country_code_jurisdiction_rule_ke~",
                table: "statutory_rules",
                columns: new[] { "tenant_id", "country_code", "jurisdiction", "rule_key", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_system_settings_tenant_id_category_setting_key",
                table: "system_settings",
                columns: new[] { "tenant_id", "category", "setting_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_brandings_tenant_id",
                table: "tenant_brandings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_feature_flags_tenant_id_feature_key",
                table: "tenant_feature_flags",
                columns: new[] { "tenant_id", "feature_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_field_help_texts_tenant_id_field_key",
                table: "tenant_field_help_texts",
                columns: new[] { "tenant_id", "field_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_hr_configs_tenant_id",
                table: "tenant_hr_configs",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_invoice_lines_invoice_id_sort_order",
                table: "tenant_invoice_lines",
                columns: new[] { "invoice_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_invoice_lines_tenant_id",
                table: "tenant_invoice_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_invoices_tenant_id_invoice_date",
                table: "tenant_invoices",
                columns: new[] { "tenant_id", "invoice_date" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_invoices_tenant_id_status",
                table: "tenant_invoices",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_localization_settings_tenant_id",
                table: "tenant_localization_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_payments_invoice_id",
                table: "tenant_payments",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_payments_tenant_id",
                table: "tenant_payments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_payments_tenant_id_status",
                table: "tenant_payments",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_subscriptions_tenant_id_status",
                table: "tenant_subscriptions",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_entity_accesses_company_id",
                table: "user_entity_accesses",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_entity_accesses_tenant_id_user_id_company_id_role",
                table: "user_entity_accesses",
                columns: new[] { "tenant_id", "user_id", "company_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_entity_accesses_tenant_id_user_id_is_active",
                table: "user_entity_accesses",
                columns: new[] { "tenant_id", "user_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_user_entity_accesses_user_id",
                table: "user_entity_accesses",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_permission_overrides_tenant_id_user_id_permission_key",
                table: "user_permission_overrides",
                columns: new[] { "tenant_id", "user_id", "permission_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_permission_overrides_user_id",
                table: "user_permission_overrides",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_role_id",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_tenant_id_is_deleted",
                table: "users",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_users_tenant_id_normalized_email",
                table: "users",
                columns: new[] { "tenant_id", "normalized_email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_tenant_id_status",
                table: "users",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_visa_records_tenant_id_employee_id_is_deleted",
                table: "visa_records",
                columns: new[] { "tenant_id", "employee_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_visa_records_tenant_id_expiry_date_status",
                table: "visa_records",
                columns: new[] { "tenant_id", "expiry_date", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_visa_records_tenant_id_visa_number",
                table: "visa_records",
                columns: new[] { "tenant_id", "visa_number" });

            migrationBuilder.CreateIndex(
                name: "IX_work_permit_records_tenant_id_employee_id_is_deleted",
                table: "work_permit_records",
                columns: new[] { "tenant_id", "employee_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_work_permit_records_tenant_id_expiry_date_status",
                table: "work_permit_records",
                columns: new[] { "tenant_id", "expiry_date", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_work_permit_records_tenant_id_permit_number",
                table: "work_permit_records",
                columns: new[] { "tenant_id", "permit_number" });

            migrationBuilder.CreateIndex(
                name: "IX_workforce_plans_tenant_id_is_deleted",
                table: "workforce_plans",
                columns: new[] { "tenant_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_workforce_plans_tenant_id_plan_code",
                table: "workforce_plans",
                columns: new[] { "tenant_id", "plan_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workforce_plans_tenant_id_plan_year_status",
                table: "workforce_plans",
                columns: new[] { "tenant_id", "plan_year", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_wps_file_batches_tenant_id_payment_batch_id",
                table: "wps_file_batches",
                columns: new[] { "tenant_id", "payment_batch_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "absence_records");

            migrationBuilder.DropTable(
                name: "absence_regularization_requests");

            migrationBuilder.DropTable(
                name: "admin_audit_logs");

            migrationBuilder.DropTable(
                name: "advance_approvals");

            migrationBuilder.DropTable(
                name: "advance_audit_logs");

            migrationBuilder.DropTable(
                name: "advance_installments");

            migrationBuilder.DropTable(
                name: "advance_policies");

            migrationBuilder.DropTable(
                name: "ai_hr_query_cache");

            migrationBuilder.DropTable(
                name: "ai_hr_query_logs");

            migrationBuilder.DropTable(
                name: "ai_insights");

            migrationBuilder.DropTable(
                name: "ai_model_configs");

            migrationBuilder.DropTable(
                name: "ai_recommendations");

            migrationBuilder.DropTable(
                name: "application_events");

            migrationBuilder.DropTable(
                name: "appraisal_appeals");

            migrationBuilder.DropTable(
                name: "appraisal_calibrations");

            migrationBuilder.DropTable(
                name: "appraisal_competency_ratings");

            migrationBuilder.DropTable(
                name: "appraisal_reviews");

            migrationBuilder.DropTable(
                name: "appraisal_score_breakdowns");

            migrationBuilder.DropTable(
                name: "approval_authorities");

            migrationBuilder.DropTable(
                name: "approval_decisions");

            migrationBuilder.DropTable(
                name: "approval_delegations");

            migrationBuilder.DropTable(
                name: "approval_policy_steps");

            migrationBuilder.DropTable(
                name: "approval_workflow_steps");

            migrationBuilder.DropTable(
                name: "assessment_questions");

            migrationBuilder.DropTable(
                name: "assessment_templates");

            migrationBuilder.DropTable(
                name: "attendance_ai_insights");

            migrationBuilder.DropTable(
                name: "attendance_audit_logs");

            migrationBuilder.DropTable(
                name: "attendance_correction_approvals");

            migrationBuilder.DropTable(
                name: "attendance_daily_records");

            migrationBuilder.DropTable(
                name: "attendance_device_connectors");

            migrationBuilder.DropTable(
                name: "attendance_device_sync_logs");

            migrationBuilder.DropTable(
                name: "attendance_devices");

            migrationBuilder.DropTable(
                name: "attendance_exceptions");

            migrationBuilder.DropTable(
                name: "attendance_geofences");

            migrationBuilder.DropTable(
                name: "attendance_import_batches");

            migrationBuilder.DropTable(
                name: "attendance_import_errors");

            migrationBuilder.DropTable(
                name: "attendance_locations");

            migrationBuilder.DropTable(
                name: "attendance_lock_periods");

            migrationBuilder.DropTable(
                name: "attendance_payroll_impacts");

            migrationBuilder.DropTable(
                name: "attendance_policies");

            migrationBuilder.DropTable(
                name: "attendance_raw_events");

            migrationBuilder.DropTable(
                name: "attendance_records");

            migrationBuilder.DropTable(
                name: "attendance_regularization_requests");

            migrationBuilder.DropTable(
                name: "attendance_rules");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "bank_transfer_files");

            migrationBuilder.DropTable(
                name: "bonus_approvals");

            migrationBuilder.DropTable(
                name: "bonus_audit_logs");

            migrationBuilder.DropTable(
                name: "bonus_batches");

            migrationBuilder.DropTable(
                name: "bonus_recommendations");

            migrationBuilder.DropTable(
                name: "bonus_types");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropTable(
                name: "burnout_risk_signals");

            migrationBuilder.DropTable(
                name: "candidate_ai_scores");

            migrationBuilder.DropTable(
                name: "candidate_assessments");

            migrationBuilder.DropTable(
                name: "candidate_documents");

            migrationBuilder.DropTable(
                name: "candidates");

            migrationBuilder.DropTable(
                name: "comp_off_credits");

            migrationBuilder.DropTable(
                name: "comp_off_usages");

            migrationBuilder.DropTable(
                name: "competencies");

            migrationBuilder.DropTable(
                name: "compliance_ai_insights");

            migrationBuilder.DropTable(
                name: "compliance_audit_logs");

            migrationBuilder.DropTable(
                name: "compliance_reminders");

            migrationBuilder.DropTable(
                name: "compliance_renewals");

            migrationBuilder.DropTable(
                name: "compliance_requirements");

            migrationBuilder.DropTable(
                name: "continuous_feedback");

            migrationBuilder.DropTable(
                name: "contract_templates");

            migrationBuilder.DropTable(
                name: "cost_centers");

            migrationBuilder.DropTable(
                name: "country_payroll_rules");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "designations");

            migrationBuilder.DropTable(
                name: "doc_types");

            migrationBuilder.DropTable(
                name: "document_chunks");

            migrationBuilder.DropTable(
                name: "employee_action_items");

            migrationBuilder.DropTable(
                name: "employee_ai_query_logs");

            migrationBuilder.DropTable(
                name: "employee_announcements");

            migrationBuilder.DropTable(
                name: "employee_bonuses");

            migrationBuilder.DropTable(
                name: "employee_change_requests");

            migrationBuilder.DropTable(
                name: "employee_churn_predictions");

            migrationBuilder.DropTable(
                name: "employee_compliance_records");

            migrationBuilder.DropTable(
                name: "employee_contracts");

            migrationBuilder.DropTable(
                name: "employee_dependents");

            migrationBuilder.DropTable(
                name: "employee_document_requests");

            migrationBuilder.DropTable(
                name: "employee_document_versions");

            migrationBuilder.DropTable(
                name: "employee_documents");

            migrationBuilder.DropTable(
                name: "employee_drafts");

            migrationBuilder.DropTable(
                name: "employee_goals");

            migrationBuilder.DropTable(
                name: "employee_histories");

            migrationBuilder.DropTable(
                name: "employee_id_rules");

            migrationBuilder.DropTable(
                name: "employee_leave_balances");

            migrationBuilder.DropTable(
                name: "employee_loans");

            migrationBuilder.DropTable(
                name: "employee_mobile_devices");

            migrationBuilder.DropTable(
                name: "employee_notification_preferences");

            migrationBuilder.DropTable(
                name: "employee_notifications");

            migrationBuilder.DropTable(
                name: "employee_payroll_profiles");

            migrationBuilder.DropTable(
                name: "employee_payslip_access_logs");

            migrationBuilder.DropTable(
                name: "employee_policy_acknowledgements");

            migrationBuilder.DropTable(
                name: "employee_profile_change_requests");

            migrationBuilder.DropTable(
                name: "employee_risk_scores");

            migrationBuilder.DropTable(
                name: "employee_salary_structures");

            migrationBuilder.DropTable(
                name: "employee_self_service_audit_logs");

            migrationBuilder.DropTable(
                name: "employee_sentiment_pulses");

            migrationBuilder.DropTable(
                name: "employee_status_histories");

            migrationBuilder.DropTable(
                name: "employee_transfer_requests");

            migrationBuilder.DropTable(
                name: "employee_user_accounts");

            migrationBuilder.DropTable(
                name: "employees");

            migrationBuilder.DropTable(
                name: "eosb_calculations");

            migrationBuilder.DropTable(
                name: "ess_dashboard_preferences");

            migrationBuilder.DropTable(
                name: "feedback_360");

            migrationBuilder.DropTable(
                name: "finance_gl_entries");

            migrationBuilder.DropTable(
                name: "fiscal_years");

            migrationBuilder.DropTable(
                name: "gcc_compliance_settings");

            migrationBuilder.DropTable(
                name: "goal_progress_updates");

            migrationBuilder.DropTable(
                name: "gosi_contribution_rules");

            migrationBuilder.DropTable(
                name: "grades");

            migrationBuilder.DropTable(
                name: "hr_request_attachments");

            migrationBuilder.DropTable(
                name: "hr_request_categories");

            migrationBuilder.DropTable(
                name: "hr_request_comments");

            migrationBuilder.DropTable(
                name: "hr_request_slas");

            migrationBuilder.DropTable(
                name: "hr_requests");

            migrationBuilder.DropTable(
                name: "increment_recommendations");

            migrationBuilder.DropTable(
                name: "interview_feedbacks");

            migrationBuilder.DropTable(
                name: "interview_schedules");

            migrationBuilder.DropTable(
                name: "job_applications");

            migrationBuilder.DropTable(
                name: "job_openings");

            migrationBuilder.DropTable(
                name: "leave_accrual_rules");

            migrationBuilder.DropTable(
                name: "leave_ai_insights");

            migrationBuilder.DropTable(
                name: "leave_approvals");

            migrationBuilder.DropTable(
                name: "leave_attachments");

            migrationBuilder.DropTable(
                name: "leave_audit_logs");

            migrationBuilder.DropTable(
                name: "leave_balance_transactions");

            migrationBuilder.DropTable(
                name: "leave_blackout_dates");

            migrationBuilder.DropTable(
                name: "leave_cancellation_requests");

            migrationBuilder.DropTable(
                name: "leave_delegations");

            migrationBuilder.DropTable(
                name: "leave_encashment_requests");

            migrationBuilder.DropTable(
                name: "leave_modification_requests");

            migrationBuilder.DropTable(
                name: "leave_payroll_impacts");

            migrationBuilder.DropTable(
                name: "leave_policies");

            migrationBuilder.DropTable(
                name: "leave_policy_eligibilities");

            migrationBuilder.DropTable(
                name: "leave_request_dates");

            migrationBuilder.DropTable(
                name: "leave_requests");

            migrationBuilder.DropTable(
                name: "leave_types");

            migrationBuilder.DropTable(
                name: "loan_approvals");

            migrationBuilder.DropTable(
                name: "loan_audit_logs");

            migrationBuilder.DropTable(
                name: "loan_installments");

            migrationBuilder.DropTable(
                name: "loan_policies");

            migrationBuilder.DropTable(
                name: "loan_settlements");

            migrationBuilder.DropTable(
                name: "loan_types");

            migrationBuilder.DropTable(
                name: "locations");

            migrationBuilder.DropTable(
                name: "login_activity");

            migrationBuilder.DropTable(
                name: "manpower_requisitions");

            migrationBuilder.DropTable(
                name: "master_data_types");

            migrationBuilder.DropTable(
                name: "master_data_values");

            migrationBuilder.DropTable(
                name: "mfa_challenge_tokens");

            migrationBuilder.DropTable(
                name: "notification_templates");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "numbering_rules");

            migrationBuilder.DropTable(
                name: "offer_approvals");

            migrationBuilder.DropTable(
                name: "offer_letters");

            migrationBuilder.DropTable(
                name: "onboarding_checklists");

            migrationBuilder.DropTable(
                name: "onboarding_tasks");

            migrationBuilder.DropTable(
                name: "overtime_adjustments");

            migrationBuilder.DropTable(
                name: "overtime_approvals");

            migrationBuilder.DropTable(
                name: "overtime_audit_logs");

            migrationBuilder.DropTable(
                name: "overtime_budgets");

            migrationBuilder.DropTable(
                name: "overtime_calculations");

            migrationBuilder.DropTable(
                name: "overtime_comp_off_conversions");

            migrationBuilder.DropTable(
                name: "overtime_multipliers");

            migrationBuilder.DropTable(
                name: "overtime_payroll_impacts");

            migrationBuilder.DropTable(
                name: "overtime_policies");

            migrationBuilder.DropTable(
                name: "overtime_requests");

            migrationBuilder.DropTable(
                name: "overtime_rules");

            migrationBuilder.DropTable(
                name: "overtime_types");

            migrationBuilder.DropTable(
                name: "passport_records");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "payroll_adjustments");

            migrationBuilder.DropTable(
                name: "payroll_ai_validation_results");

            migrationBuilder.DropTable(
                name: "payroll_allowances");

            migrationBuilder.DropTable(
                name: "payroll_approvals");

            migrationBuilder.DropTable(
                name: "payroll_audit_logs");

            migrationBuilder.DropTable(
                name: "payroll_cycles");

            migrationBuilder.DropTable(
                name: "payroll_deductions");

            migrationBuilder.DropTable(
                name: "payroll_earnings");

            migrationBuilder.DropTable(
                name: "payroll_exceptions");

            migrationBuilder.DropTable(
                name: "payroll_groups");

            migrationBuilder.DropTable(
                name: "payroll_payment_batches");

            migrationBuilder.DropTable(
                name: "payroll_payment_records");

            migrationBuilder.DropTable(
                name: "payroll_run_employees");

            migrationBuilder.DropTable(
                name: "payroll_runs");

            migrationBuilder.DropTable(
                name: "payroll_slips");

            migrationBuilder.DropTable(
                name: "payroll_validation_results");

            migrationBuilder.DropTable(
                name: "payslip_components");

            migrationBuilder.DropTable(
                name: "payslips");

            migrationBuilder.DropTable(
                name: "performance_audit_logs");

            migrationBuilder.DropTable(
                name: "performance_cycle_employees");

            migrationBuilder.DropTable(
                name: "performance_cycles");

            migrationBuilder.DropTable(
                name: "performance_improvement_plans");

            migrationBuilder.DropTable(
                name: "performance_rating_options");

            migrationBuilder.DropTable(
                name: "performance_rating_scales");

            migrationBuilder.DropTable(
                name: "performance_scorecard_templates");

            migrationBuilder.DropTable(
                name: "permission_grantor_records");

            migrationBuilder.DropTable(
                name: "pip_check_ins");

            migrationBuilder.DropTable(
                name: "platform_announcements");

            migrationBuilder.DropTable(
                name: "platform_compliance_controls");

            migrationBuilder.DropTable(
                name: "platform_config_entries");

            migrationBuilder.DropTable(
                name: "platform_leads");

            migrationBuilder.DropTable(
                name: "platform_security_incidents");

            migrationBuilder.DropTable(
                name: "platform_support_sessions");

            migrationBuilder.DropTable(
                name: "platform_users");

            migrationBuilder.DropTable(
                name: "pricing_config");

            migrationBuilder.DropTable(
                name: "pricing_module_configs");

            migrationBuilder.DropTable(
                name: "pricing_quotes");

            migrationBuilder.DropTable(
                name: "probation_reviews");

            migrationBuilder.DropTable(
                name: "promotion_recommendations");

            migrationBuilder.DropTable(
                name: "public_holiday_calendars");

            migrationBuilder.DropTable(
                name: "public_holidays");

            migrationBuilder.DropTable(
                name: "qiwa_api_credentials");

            migrationBuilder.DropTable(
                name: "QiwaSyncLogs");

            migrationBuilder.DropTable(
                name: "QiwaTenantConnections");

            migrationBuilder.DropTable(
                name: "recruitment_audit_logs");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "report_execution_logs");

            migrationBuilder.DropTable(
                name: "report_schedules");

            migrationBuilder.DropTable(
                name: "reporting_lines");

            migrationBuilder.DropTable(
                name: "resume_parse_results");

            migrationBuilder.DropTable(
                name: "role_competencies");

            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropTable(
                name: "salary_advances");

            migrationBuilder.DropTable(
                name: "salary_components");

            migrationBuilder.DropTable(
                name: "salary_structures");

            migrationBuilder.DropTable(
                name: "saved_reports");

            migrationBuilder.DropTable(
                name: "security_settings");

            migrationBuilder.DropTable(
                name: "shift_assignments");

            migrationBuilder.DropTable(
                name: "shift_definitions");

            migrationBuilder.DropTable(
                name: "sif_file_records");

            migrationBuilder.DropTable(
                name: "statutory_rules");

            migrationBuilder.DropTable(
                name: "system_settings");

            migrationBuilder.DropTable(
                name: "tenant_ai_usage");

            migrationBuilder.DropTable(
                name: "tenant_brandings");

            migrationBuilder.DropTable(
                name: "tenant_feature_flags");

            migrationBuilder.DropTable(
                name: "tenant_field_help_texts");

            migrationBuilder.DropTable(
                name: "tenant_hr_configs");

            migrationBuilder.DropTable(
                name: "tenant_invoice_lines");

            migrationBuilder.DropTable(
                name: "tenant_invoices");

            migrationBuilder.DropTable(
                name: "tenant_localization_settings");

            migrationBuilder.DropTable(
                name: "tenant_payments");

            migrationBuilder.DropTable(
                name: "tenant_subscriptions");

            migrationBuilder.DropTable(
                name: "user_entity_accesses");

            migrationBuilder.DropTable(
                name: "user_permission_overrides");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "visa_records");

            migrationBuilder.DropTable(
                name: "work_permit_records");

            migrationBuilder.DropTable(
                name: "workforce_plans");

            migrationBuilder.DropTable(
                name: "wps_file_batches");

            migrationBuilder.DropTable(
                name: "approval_requests");

            migrationBuilder.DropTable(
                name: "approval_policies");

            migrationBuilder.DropTable(
                name: "approval_workflows");

            migrationBuilder.DropTable(
                name: "policy_documents");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "companies");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
