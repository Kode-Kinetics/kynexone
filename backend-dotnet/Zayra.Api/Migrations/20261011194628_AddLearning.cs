using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "learning_courses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    description_ar = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    estimated_minutes = table.Column<int>(type: "integer", nullable: true),
                    content_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    lesson_html = table.Column<string>(type: "text", nullable: true),
                    link_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    cmi5_au_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    cmi5_activity_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cmi5_move_on = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    cmi5_au_origin = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    completion_rule = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    assessment_json = table.Column<string>(type: "jsonb", nullable: true),
                    pass_mark_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    max_attempts = table.Column<int>(type: "integer", nullable: true),
                    shuffle_questions = table.Column<bool>(type: "boolean", nullable: false),
                    validity_months = table.Column<int>(type: "integer", nullable: true),
                    issues_certificate = table.Column<bool>(type: "boolean", nullable: false),
                    requires_hr_verification = table.Column<bool>(type: "boolean", nullable: false),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_courses", x => x.id);
                    table.CheckConstraint("ck_learning_courses__assessment_pass_mark_positive", "status = 'Draft' OR completion_rule <> 'Assessment' OR pass_mark_percent > 0");
                    table.CheckConstraint("ck_learning_courses__completion_rule", "completion_rule IN ('Assessment','Cmi5MoveOn','Acknowledgement')");
                    table.CheckConstraint("ck_learning_courses__completion_rule_fits_content", "status = 'Draft' OR (completion_rule = 'Assessment' AND content_kind IN ('Lesson','Link') AND assessment_json IS NOT NULL     AND pass_mark_percent IS NOT NULL AND max_attempts IS NOT NULL) OR (completion_rule = 'Cmi5MoveOn' AND content_kind = 'Cmi5' AND cmi5_move_on IS NOT NULL AND assessment_json IS NULL) OR (completion_rule = 'Acknowledgement' AND content_kind IN ('Lesson','Link') AND assessment_json IS NULL)");
                    table.CheckConstraint("ck_learning_courses__content_kind", "content_kind IN ('Lesson','Link','Cmi5')");
                    table.CheckConstraint("ck_learning_courses__content_present", "status = 'Draft' OR (content_kind = 'Lesson' AND lesson_html IS NOT NULL) OR (content_kind = 'Link' AND link_url IS NOT NULL) OR (content_kind = 'Cmi5' AND cmi5_au_url IS NOT NULL AND cmi5_activity_id IS NOT NULL AND cmi5_au_origin IS NOT NULL)");
                    table.CheckConstraint("ck_learning_courses__hr_verification_cmi5_only", "NOT requires_hr_verification OR content_kind = 'Cmi5'");
                    table.CheckConstraint("ck_learning_courses__max_attempts", "max_attempts IS NULL OR (max_attempts >= 1 AND max_attempts <= 20)");
                    table.CheckConstraint("ck_learning_courses__move_on", "cmi5_move_on IS NULL OR cmi5_move_on IN ('Passed','Completed','CompletedAndPassed','CompletedOrPassed','NotApplicable')");
                    table.CheckConstraint("ck_learning_courses__pass_mark", "pass_mark_percent IS NULL OR (pass_mark_percent >= 0 AND pass_mark_percent <= 100)");
                    table.CheckConstraint("ck_learning_courses__published_stamped", "status = 'Draft' OR published_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_learning_courses__status", "status IN ('Draft','Published','Retired')");
                    table.CheckConstraint("ck_learning_courses__validity_months", "validity_months IS NULL OR (validity_months >= 1 AND validity_months <= 120)");
                    table.CheckConstraint("ck_learning_courses__version_number", "version_number >= 1");
                });

            migrationBuilder.CreateTable(
                name: "learning_paths",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    eligibility_json = table.Column<string>(type: "jsonb", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: true),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    renewal_window_days = table.Column<int>(type: "integer", nullable: false),
                    activated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_paths", x => x.id);
                    table.CheckConstraint("ck_learning_paths__effective_window", "effective_to IS NULL OR effective_from IS NULL OR effective_to >= effective_from");
                    table.CheckConstraint("ck_learning_paths__renewal_window", "renewal_window_days >= 0 AND renewal_window_days <= 365");
                    table.CheckConstraint("ck_learning_paths__status", "status IN ('Draft','Active','Archived')");
                });

            migrationBuilder.CreateTable(
                name: "learning_assignment_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_in_days = table.Column<int>(type: "integer", nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_applied_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_assignment_rules", x => x.id);
                    table.CheckConstraint("ck_learning_assignment_rules__due_in_days", "due_in_days >= 0 AND due_in_days <= 3650");
                    table.CheckConstraint("ck_learning_assignment_rules__target_kind", "target_kind IN ('Company','Department','Designation','Grade')");
                    table.ForeignKey(
                        name: "FK_learning_assignment_rules_learning_paths_path_id",
                        column: x => x.path_id,
                        principalTable: "learning_paths",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_path_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    prerequisite_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_path_items", x => x.id);
                    table.CheckConstraint("ck_learning_path_items__not_own_prerequisite", "prerequisite_item_id IS NULL OR prerequisite_item_id <> id");
                    table.CheckConstraint("ck_learning_path_items__sequence", "sequence >= 1");
                    table.ForeignKey(
                        name: "FK_learning_path_items_learning_courses_course_id",
                        column: x => x.course_id,
                        principalTable: "learning_courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_path_items_learning_path_items_prerequisite_item_id",
                        column: x => x.prerequisite_item_id,
                        principalTable: "learning_path_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_path_items_learning_paths_path_id",
                        column: x => x.path_id,
                        principalTable: "learning_paths",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "learning_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    path_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    renewal_of_credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    due_reminder_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    overdue_notice_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    no_recipient_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_assignments", x => x.id);
                    table.CheckConstraint("ck_learning_assignments__cancel_reason", "status <> 'Cancelled' OR (cancelled_at_utc IS NOT NULL AND cancel_reason IS NOT NULL)");
                    table.CheckConstraint("ck_learning_assignments__completed_stamped", "status <> 'Completed' OR completed_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_learning_assignments__cycle", "cycle >= 1");
                    table.CheckConstraint("ck_learning_assignments__renewal_source", "(renewal_of_credential_id IS NULL) = (source <> 'Renewal')");
                    table.CheckConstraint("ck_learning_assignments__rule_source", "rule_id IS NULL OR source = 'Rule'");
                    table.CheckConstraint("ck_learning_assignments__source", "source IN ('Employee','Department','Designation','Grade','Rule','Renewal')");
                    table.CheckConstraint("ck_learning_assignments__status", "status IN ('Assigned','InProgress','Completed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_learning_assignments_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_assignments_learning_assignment_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "learning_assignment_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_learning_assignments_learning_paths_path_id",
                        column: x => x.path_id,
                        principalTable: "learning_paths",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_enrollments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    path_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    first_launched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_activity_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cmi5_registration = table.Column<Guid>(type: "uuid", nullable: true),
                    progress_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completion_source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    completion_evidence_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    score_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    attempt_allowance = table.Column<int>(type: "integer", nullable: false),
                    attempts_used = table.Column<int>(type: "integer", nullable: false),
                    reset_count = table.Column<int>(type: "integer", nullable: false),
                    locked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_enrollments", x => x.id);
                    table.CheckConstraint("ck_learning_enrollments__attempts", "attempts_used >= 0 AND attempt_allowance >= 0 AND attempts_used <= attempt_allowance");
                    table.CheckConstraint("ck_learning_enrollments__completion_evidence", "(status IN ('Completed','PendingVerification')) = (completion_source IS NOT NULL AND completion_evidence_ref IS NOT NULL AND completed_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_learning_enrollments__completion_source", "completion_source IS NULL OR completion_source IN ('Assessment','Cmi5','Acknowledgement')");
                    table.CheckConstraint("ck_learning_enrollments__progress", "progress_percent IS NULL OR (progress_percent >= 0 AND progress_percent <= 100)");
                    table.CheckConstraint("ck_learning_enrollments__status", "status IN ('NotStarted','InProgress','Completed','Locked','Cancelled','PendingVerification')");
                    table.CheckConstraint("ck_learning_enrollments__verification_after_evidence", "verified_at_utc IS NULL OR status = 'Completed'");
                    table.ForeignKey(
                        name: "FK_learning_enrollments_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_enrollments_learning_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "learning_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_enrollments_learning_courses_course_id",
                        column: x => x.course_id,
                        principalTable: "learning_courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_enrollments_learning_path_items_path_item_id",
                        column: x => x.path_item_id,
                        principalTable: "learning_path_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    question_order_json = table.Column<string>(type: "jsonb", nullable: false),
                    answers_json = table.Column<string>(type: "jsonb", nullable: true),
                    score_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    pass_mark_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_attempts", x => x.id);
                    table.CheckConstraint("ck_learning_attempts__attempt_no", "attempt_no >= 1");
                    table.CheckConstraint("ck_learning_attempts__score", "score_percent IS NULL OR (score_percent >= 0 AND score_percent <= 100)");
                    table.CheckConstraint("ck_learning_attempts__scored_when_submitted", "(status = 'InProgress') = (submitted_at_utc IS NULL) AND (status = 'InProgress' OR score_percent IS NOT NULL)");
                    table.CheckConstraint("ck_learning_attempts__status", "status IN ('InProgress','Passed','Failed')");
                    table.ForeignKey(
                        name: "FK_learning_attempts_learning_enrollments_enrollment_id",
                        column: x => x.enrollment_id,
                        principalTable: "learning_enrollments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    path_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verification_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    completion_source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    score_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    supersedes_credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supersede_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    superseded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    renewal_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expiring_notice_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expired_notice_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    no_recipient_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_credentials", x => x.id);
                    table.CheckConstraint("ck_learning_credentials__completion_source", "completion_source IN ('Assessment','Cmi5','Acknowledgement')");
                    table.CheckConstraint("ck_learning_credentials__expiry_after_completion", "expires_on IS NULL OR expires_on > completed_on");
                    table.CheckConstraint("ck_learning_credentials__not_own_predecessor", "supersedes_credential_id IS NULL OR supersedes_credential_id <> id");
                    table.CheckConstraint("ck_learning_credentials__revoked_stamped", "status <> 'Revoked' OR (revoked_at_utc IS NOT NULL AND revoke_reason IS NOT NULL)");
                    table.CheckConstraint("ck_learning_credentials__status", "status IN ('Active','Expired','Revoked','Superseded')");
                    table.CheckConstraint("ck_learning_credentials__superseded_stamped", "status <> 'Superseded' OR superseded_at_utc IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_learning_credentials_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_credentials_learning_assignments_renewal_assignmen~",
                        column: x => x.renewal_assignment_id,
                        principalTable: "learning_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_learning_credentials_learning_courses_course_id",
                        column: x => x.course_id,
                        principalTable: "learning_courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_credentials_learning_credentials_supersedes_creden~",
                        column: x => x.supersedes_credential_id,
                        principalTable: "learning_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_credentials_learning_enrollments_enrollment_id",
                        column: x => x.enrollment_id,
                        principalTable: "learning_enrollments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_credentials_learning_paths_path_id",
                        column: x => x.path_id,
                        principalTable: "learning_paths",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_launch_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    registration = table.Column<Guid>(type: "uuid", nullable: false),
                    activity_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_account_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_home_page = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    au_origin = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    move_on = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    return_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fetch_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fetch_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    fetch_used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    auth_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    auth_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    launched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    initialized_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_statement_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_launch_sessions", x => x.id);
                    table.CheckConstraint("ck_learning_launch_sessions__auth_expiry", "(auth_token_hash IS NULL) = (auth_expires_at_utc IS NULL)");
                    table.CheckConstraint("ck_learning_launch_sessions__fetch_then_auth", "(auth_token_hash IS NULL) = (fetch_used_at_utc IS NULL)");
                    table.CheckConstraint("ck_learning_launch_sessions__move_on", "move_on IN ('Passed','Completed','CompletedAndPassed','CompletedOrPassed','NotApplicable')");
                    table.CheckConstraint("ck_learning_launch_sessions__status", "status IN ('Launched','Fetched','Initialized','Terminated','Abandoned')");
                    table.ForeignKey(
                        name: "FK_learning_launch_sessions_learning_enrollments_enrollment_id",
                        column: x => x.enrollment_id,
                        principalTable: "learning_enrollments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_xapi_statements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registration = table.Column<Guid>(type: "uuid", nullable: false),
                    verb = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    result_success = table.Column<bool>(type: "boolean", nullable: true),
                    result_completion = table.Column<bool>(type: "boolean", nullable: true),
                    score_scaled = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: true),
                    progress_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    issued_by_lms = table.Column<bool>(type: "boolean", nullable: false),
                    statement_json = table.Column<string>(type: "jsonb", nullable: false),
                    stored_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_xapi_statements", x => x.id);
                    table.CheckConstraint("ck_learning_xapi_statements__progress", "progress_percent IS NULL OR (progress_percent >= 0 AND progress_percent <= 100)");
                    table.ForeignKey(
                        name: "FK_learning_xapi_statements_learning_enrollments_enrollment_id",
                        column: x => x.enrollment_id,
                        principalTable: "learning_enrollments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_xapi_statements_learning_launch_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "learning_launch_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignment_rules_path_id",
                table: "learning_assignment_rules",
                column: "path_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignment_rules_tenant_id_is_active",
                table: "learning_assignment_rules",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_learning_assignment_rules_one_active_per_target",
                table: "learning_assignment_rules",
                columns: new[] { "tenant_id", "path_id", "target_kind", "target_id" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_employee_id",
                table: "learning_assignments",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_path_id",
                table: "learning_assignments",
                column: "path_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_renewal_of_credential_id",
                table: "learning_assignments",
                column: "renewal_of_credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_rule_id",
                table: "learning_assignments",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_tenant_id_company_id",
                table: "learning_assignments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_tenant_id_employee_id_status",
                table: "learning_assignments",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_tenant_id_path_id_status",
                table: "learning_assignments",
                columns: new[] { "tenant_id", "path_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_assignments_tenant_id_status_due_date",
                table: "learning_assignments",
                columns: new[] { "tenant_id", "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ux_learning_assignments_one_active_per_employee_path",
                table: "learning_assignments",
                columns: new[] { "tenant_id", "employee_id", "path_id" },
                unique: true,
                filter: "status IN ('Assigned','InProgress')");

            migrationBuilder.CreateIndex(
                name: "IX_learning_attempts_enrollment_id_attempt_no",
                table: "learning_attempts",
                columns: new[] { "enrollment_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_attempts_tenant_id_company_id",
                table: "learning_attempts",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_learning_attempts_one_open_per_enrollment",
                table: "learning_attempts",
                column: "enrollment_id",
                unique: true,
                filter: "status = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "IX_learning_courses_tenant_id_code_version_number",
                table: "learning_courses",
                columns: new[] { "tenant_id", "code", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_learning_courses_one_draft_per_code",
                table: "learning_courses",
                columns: new[] { "tenant_id", "code" },
                unique: true,
                filter: "status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "ux_learning_courses_one_published_per_code",
                table: "learning_courses",
                columns: new[] { "tenant_id", "code" },
                unique: true,
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_course_id",
                table: "learning_credentials",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_employee_id",
                table: "learning_credentials",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_enrollment_id",
                table: "learning_credentials",
                column: "enrollment_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_path_id",
                table: "learning_credentials",
                column: "path_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_renewal_assignment_id",
                table: "learning_credentials",
                column: "renewal_assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_tenant_id_certificate_number",
                table: "learning_credentials",
                columns: new[] { "tenant_id", "certificate_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_tenant_id_company_id",
                table: "learning_credentials",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_tenant_id_employee_id",
                table: "learning_credentials",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_credentials_tenant_id_status_expires_on",
                table: "learning_credentials",
                columns: new[] { "tenant_id", "status", "expires_on" });

            migrationBuilder.CreateIndex(
                name: "ux_learning_credentials_superseded_once",
                table: "learning_credentials",
                column: "supersedes_credential_id",
                unique: true,
                filter: "supersedes_credential_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_learning_enrollments_assignment_id_path_item_id",
                table: "learning_enrollments",
                columns: new[] { "assignment_id", "path_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_enrollments_course_id",
                table: "learning_enrollments",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_enrollments_employee_id",
                table: "learning_enrollments",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_enrollments_path_item_id",
                table: "learning_enrollments",
                column: "path_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_enrollments_tenant_id_company_id",
                table: "learning_enrollments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_enrollments_tenant_id_employee_id_status",
                table: "learning_enrollments",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_launch_sessions_au_origin_status",
                table: "learning_launch_sessions",
                columns: new[] { "au_origin", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_launch_sessions_enrollment_id",
                table: "learning_launch_sessions",
                column: "enrollment_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_launch_sessions_fetch_token_hash",
                table: "learning_launch_sessions",
                column: "fetch_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_launch_sessions_status_auth_expires_at_utc",
                table: "learning_launch_sessions",
                columns: new[] { "status", "auth_expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_launch_sessions_tenant_id_enrollment_id_launched_a~",
                table: "learning_launch_sessions",
                columns: new[] { "tenant_id", "enrollment_id", "launched_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_path_items_course_id",
                table: "learning_path_items",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_path_items_path_id_course_id",
                table: "learning_path_items",
                columns: new[] { "path_id", "course_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_path_items_path_id_sequence",
                table: "learning_path_items",
                columns: new[] { "path_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_path_items_prerequisite_item_id",
                table: "learning_path_items",
                column: "prerequisite_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_path_items_tenant_id_course_id",
                table: "learning_path_items",
                columns: new[] { "tenant_id", "course_id" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_paths_tenant_id_code",
                table: "learning_paths",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_paths_tenant_id_status",
                table: "learning_paths",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_xapi_statements_enrollment_id",
                table: "learning_xapi_statements",
                column: "enrollment_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_xapi_statements_session_id",
                table: "learning_xapi_statements",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_learning_xapi_statements_tenant_id_registration",
                table: "learning_xapi_statements",
                columns: new[] { "tenant_id", "registration" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_xapi_statements_tenant_id_statement_id",
                table: "learning_xapi_statements",
                columns: new[] { "tenant_id", "statement_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_learning_assignments_learning_credentials_renewal_of_creden~",
                table: "learning_assignments",
                column: "renewal_of_credential_id",
                principalTable: "learning_credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_learning_assignment_rules_learning_paths_path_id",
                table: "learning_assignment_rules");

            migrationBuilder.DropForeignKey(
                name: "FK_learning_assignments_learning_paths_path_id",
                table: "learning_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_learning_credentials_learning_paths_path_id",
                table: "learning_credentials");

            migrationBuilder.DropForeignKey(
                name: "FK_learning_path_items_learning_paths_path_id",
                table: "learning_path_items");

            migrationBuilder.DropForeignKey(
                name: "FK_learning_assignments_learning_assignment_rules_rule_id",
                table: "learning_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_learning_assignments_learning_credentials_renewal_of_creden~",
                table: "learning_assignments");

            migrationBuilder.DropTable(
                name: "learning_attempts");

            migrationBuilder.DropTable(
                name: "learning_xapi_statements");

            migrationBuilder.DropTable(
                name: "learning_launch_sessions");

            migrationBuilder.DropTable(
                name: "learning_paths");

            migrationBuilder.DropTable(
                name: "learning_assignment_rules");

            migrationBuilder.DropTable(
                name: "learning_credentials");

            migrationBuilder.DropTable(
                name: "learning_enrollments");

            migrationBuilder.DropTable(
                name: "learning_assignments");

            migrationBuilder.DropTable(
                name: "learning_path_items");

            migrationBuilder.DropTable(
                name: "learning_courses");
        }
    }
}
