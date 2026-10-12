using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSuccession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "succession_critical_positions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    risk_of_loss = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    impact_of_vacancy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    accountable_employee_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    retired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retired_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_succession_critical_positions", x => x.id);
                    table.CheckConstraint("ck_succession_critical_positions__impact", "impact_of_vacancy IN ('Low','Medium','High','Critical')");
                    table.CheckConstraint("ck_succession_critical_positions__rationale", "length(trim(rationale)) > 0");
                    table.CheckConstraint("ck_succession_critical_positions__retired_stamped", "status <> 'Retired' OR (retired_at_utc IS NOT NULL AND retired_reason IS NOT NULL)");
                    table.CheckConstraint("ck_succession_critical_positions__risk", "risk_of_loss IN ('Low','Medium','High')");
                    table.CheckConstraint("ck_succession_critical_positions__status", "status IN ('Active','Retired')");
                    table.ForeignKey(
                        name: "FK_succession_critical_positions_employees_accountable_employe~",
                        column: x => x.accountable_employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_succession_critical_positions_positions_position_id",
                        column: x => x.position_id,
                        principalTable: "positions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "talent_assessments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    review_period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    assessor_employee_id = table.Column<int>(type: "integer", nullable: true),
                    assessor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposed_potential = table.Column<int>(type: "integer", nullable: false),
                    potential_evidence = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    appraisal_review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performance_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    performance_band = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    band_rule_version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    calibrated_potential = table.Column<int>(type: "integer", nullable: true),
                    calibration_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    calibrated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calibrated_by_employee_id = table.Column<int>(type: "integer", nullable: true),
                    calibrated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    superseded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    nine_box_cell = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, computedColumnSql: "CASE WHEN performance_band IN ('Low','Moderate','High') THEN (CASE COALESCE(calibrated_potential, proposed_potential) WHEN 3 THEN 'P3' WHEN 2 THEN 'P2' ELSE 'P1' END) || '-' || performance_band ELSE NULL END", stored: true),
                    anonymised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_talent_assessments", x => x.id);
                    table.CheckConstraint("ck_talent_assessments__band", "performance_band IN ('Low','Moderate','High','Unrated')");
                    table.CheckConstraint("ck_talent_assessments__calibrated_potential", "calibrated_potential IS NULL OR calibrated_potential BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_talent_assessments__calibrated_stamped", "status <> 'Calibrated' OR (calibrated_potential IS NOT NULL AND calibrated_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_talent_assessments__calibration_reason", "calibrated_potential IS NULL OR calibrated_potential = proposed_potential OR (calibration_reason IS NOT NULL AND length(trim(calibration_reason)) >= 20)");
                    table.CheckConstraint("ck_talent_assessments__evidence", "length(trim(potential_evidence)) > 0");
                    table.CheckConstraint("ck_talent_assessments__not_own_predecessor", "supersedes_id IS NULL OR supersedes_id <> id");
                    table.CheckConstraint("ck_talent_assessments__proposed_potential", "proposed_potential BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_talent_assessments__score", "performance_score IS NULL OR (performance_score >= 0 AND performance_score <= 100)");
                    table.CheckConstraint("ck_talent_assessments__status", "status IN ('Draft','Submitted','Calibrated','Superseded')");
                    table.CheckConstraint("ck_talent_assessments__superseded_stamped", "status <> 'Superseded' OR superseded_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_talent_assessments__unrated_has_no_score", "(performance_band = 'Unrated') = (performance_score IS NULL)");
                    table.ForeignKey(
                        name: "FK_talent_assessments_appraisal_reviews_appraisal_review_id",
                        column: x => x.appraisal_review_id,
                        principalTable: "appraisal_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_talent_assessments_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_talent_assessments_talent_assessments_supersedes_id",
                        column: x => x.supersedes_id,
                        principalTable: "talent_assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "talent_pools",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    owner_employee_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_talent_pools", x => x.id);
                    table.CheckConstraint("ck_talent_pools__code", "length(trim(code)) > 0");
                    table.CheckConstraint("ck_talent_pools__status", "status IN ('Active','Archived')");
                    table.ForeignKey(
                        name: "FK_talent_pools_employees_owner_employee_id",
                        column: x => x.owner_employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "succession_slate_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    critical_position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    snapshot_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: true),
                    superseded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    returned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    return_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    revised_from_id = table.Column<Guid>(type: "uuid", nullable: true),
                    anonymised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    anonymised_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_succession_slate_versions", x => x.id);
                    table.CheckConstraint("ck_succession_slate_versions__anonymised_hash", "(anonymised_sha256 IS NULL) = (anonymised_at_utc IS NULL)");
                    table.CheckConstraint("ck_succession_slate_versions__approved_stamped", "status NOT IN ('Approved','Superseded') OR (approved_at_utc IS NOT NULL AND effective_from IS NOT NULL)");
                    table.CheckConstraint("ck_succession_slate_versions__cancel_reason", "status <> 'Cancelled' OR (cancelled_at_utc IS NOT NULL AND cancel_reason IS NOT NULL)");
                    table.CheckConstraint("ck_succession_slate_versions__not_own_predecessor", "revised_from_id IS NULL OR revised_from_id <> id");
                    table.CheckConstraint("ck_succession_slate_versions__returned_reason", "status <> 'ReturnedForRevision' OR (returned_at_utc IS NOT NULL AND return_reason IS NOT NULL AND length(trim(return_reason)) > 0)");
                    table.CheckConstraint("ck_succession_slate_versions__status", "status IN ('Draft','Submitted','ReturnedForRevision','Approved','Superseded','Cancelled')");
                    table.CheckConstraint("ck_succession_slate_versions__submitted_stamped", "status = 'Draft' OR (submitted_at_utc IS NOT NULL AND approval_request_id IS NOT NULL) OR status = 'Cancelled'");
                    table.CheckConstraint("ck_succession_slate_versions__superseded_stamped", "status <> 'Superseded' OR superseded_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_succession_slate_versions__version_no", "version_no >= 1");
                    table.ForeignKey(
                        name: "FK_succession_slate_versions_approval_requests_approval_reques~",
                        column: x => x.approval_request_id,
                        principalTable: "approval_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_succession_slate_versions_succession_critical_positions_cri~",
                        column: x => x.critical_position_id,
                        principalTable: "succession_critical_positions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_succession_slate_versions_succession_slate_versions_revised~",
                        column: x => x.revised_from_id,
                        principalTable: "succession_slate_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "succession_nominations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    critical_position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    talent_pool_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    readiness = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    evidence = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    competency_evidence_json = table.Column<string>(type: "jsonb", nullable: false),
                    nominated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    nominated_by_employee_id = table.Column<int>(type: "integer", nullable: true),
                    nominated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    readiness_previous = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    readiness_changed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    readiness_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    readiness_changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_position_id_at = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_company_id_at = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_manager_id_at = table.Column<int>(type: "integer", nullable: true),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    review_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    exit_reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    exit_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    exited_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    exited_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    anonymised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_succession_nominations", x => x.id);
                    table.CheckConstraint("ck_succession_nominations__closed_has_reason", "status NOT IN ('Declined','Withdrawn') OR (exit_reason IS NOT NULL AND exit_note IS NOT NULL AND length(trim(exit_note)) > 0 AND exited_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_succession_nominations__evidence", "length(trim(evidence)) > 0");
                    table.CheckConstraint("ck_succession_nominations__exit_reason", "exit_reason IS NULL OR exit_reason IN ('Declined','Withdrawn','Terminated','Ineligible','PositionRetired','Appointed','RecordMerged')");
                    table.CheckConstraint("ck_succession_nominations__on_slate_position_only", "status <> 'OnSlate' OR critical_position_id IS NOT NULL");
                    table.CheckConstraint("ck_succession_nominations__one_target", "(critical_position_id IS NULL) <> (talent_pool_id IS NULL)");
                    table.CheckConstraint("ck_succession_nominations__readiness", "readiness IS NULL OR readiness IN ('ReadyNow','OneToTwoYears','ThreePlusYears')");
                    table.CheckConstraint("ck_succession_nominations__readiness_change_reason", "readiness_changed_at_utc IS NULL OR (readiness_reason IS NOT NULL AND length(trim(readiness_reason)) > 0)");
                    table.CheckConstraint("ck_succession_nominations__readiness_previous", "readiness_previous IS NULL OR readiness_previous IN ('ReadyNow','OneToTwoYears','ThreePlusYears')");
                    table.CheckConstraint("ck_succession_nominations__readiness_required", "critical_position_id IS NULL OR status NOT IN ('Calibrated','OnSlate') OR readiness IS NOT NULL");
                    table.CheckConstraint("ck_succession_nominations__status", "status IN ('Proposed','InReview','Calibrated','OnSlate','Declined','Withdrawn')");
                    table.ForeignKey(
                        name: "FK_succession_nominations_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_succession_nominations_succession_critical_positions_critic~",
                        column: x => x.critical_position_id,
                        principalTable: "succession_critical_positions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_succession_nominations_talent_pools_talent_pool_id",
                        column: x => x.talent_pool_id,
                        principalTable: "talent_pools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    nomination_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_employee_id = table.Column<int>(type: "integer", nullable: true),
                    purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    next_review_on = table.Column<DateOnly>(type: "date", nullable: false),
                    last_reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_review_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    activated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    review_reminder_sent_for = table.Column<DateOnly>(type: "date", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_plans", x => x.id);
                    table.CheckConstraint("ck_development_plans__cancel_reason", "status <> 'Cancelled' OR (cancelled_at_utc IS NOT NULL AND cancel_reason IS NOT NULL)");
                    table.CheckConstraint("ck_development_plans__completed_stamped", "status <> 'Completed' OR completed_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_development_plans__purpose", "length(trim(purpose)) > 0");
                    table.CheckConstraint("ck_development_plans__status", "status IN ('Draft','Active','OnHold','Completed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_development_plans_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_plans_employees_owner_employee_id",
                        column: x => x.owner_employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_plans_succession_nominations_nomination_id",
                        column: x => x.nomination_id,
                        principalTable: "succession_nominations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "development_plan_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    learning_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_goal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mentor_employee_id = table.Column<int>(type: "integer", nullable: true),
                    owner_employee_id = table.Column<int>(type: "integer", nullable: true),
                    due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    progress_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completion_method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    completion_proposed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    overdue_notice_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_plan_actions", x => x.id);
                    table.CheckConstraint("ck_development_plan_actions__completed_stamped", "(status = 'Completed') = (completed_at_utc IS NOT NULL AND completion_method IS NOT NULL)");
                    table.CheckConstraint("ck_development_plan_actions__link_matches_type", "((action_type = 'Learning') = (learning_assignment_id IS NOT NULL)) AND ((action_type = 'Goal') = (employee_goal_id IS NOT NULL)) AND (action_type <> 'Mentoring' OR mentor_employee_id IS NOT NULL)");
                    table.CheckConstraint("ck_development_plan_actions__linked_method_fits", "(completion_method IS NULL OR completion_method <> 'LinkedLearningCompleted' OR action_type = 'Learning') AND (completion_method IS NULL OR completion_method <> 'LinkedGoalCompleted' OR action_type = 'Goal')");
                    table.CheckConstraint("ck_development_plan_actions__method", "completion_method IS NULL OR completion_method IN ('Manual','LinkedLearningCompleted','LinkedGoalCompleted')");
                    table.CheckConstraint("ck_development_plan_actions__proposal_manual_only", "completion_proposed_at_utc IS NULL OR action_type NOT IN ('Learning','Goal')");
                    table.CheckConstraint("ck_development_plan_actions__status", "status IN ('NotStarted','InProgress','Completed','Cancelled')");
                    table.CheckConstraint("ck_development_plan_actions__title", "length(trim(title)) > 0");
                    table.CheckConstraint("ck_development_plan_actions__type", "action_type IN ('Learning','Goal','Experience','Mentoring','Other')");
                    table.ForeignKey(
                        name: "FK_development_plan_actions_development_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "development_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_plan_actions_employee_goals_employee_goal_id",
                        column: x => x.employee_goal_id,
                        principalTable: "employee_goals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_plan_actions_employees_mentor_employee_id",
                        column: x => x.mentor_employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_plan_actions_employees_owner_employee_id",
                        column: x => x.owner_employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_plan_actions_learning_assignments_learning_assi~",
                        column: x => x.learning_assignment_id,
                        principalTable: "learning_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_development_plan_actions_employee_goal_id",
                table: "development_plan_actions",
                column: "employee_goal_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_plan_actions_learning_assignment_id",
                table: "development_plan_actions",
                column: "learning_assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_plan_actions_mentor_employee_id",
                table: "development_plan_actions",
                column: "mentor_employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_plan_actions_owner_employee_id",
                table: "development_plan_actions",
                column: "owner_employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_plan_actions_plan_id_sort_order",
                table: "development_plan_actions",
                columns: new[] { "plan_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "IX_development_plan_actions_tenant_id_company_id",
                table: "development_plan_actions",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_plan_actions_tenant_id_status_due_on",
                table: "development_plan_actions",
                columns: new[] { "tenant_id", "status", "due_on" });

            migrationBuilder.CreateIndex(
                name: "IX_development_plans_employee_id",
                table: "development_plans",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_plans_nomination_id",
                table: "development_plans",
                column: "nomination_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_plans_owner_employee_id",
                table: "development_plans",
                column: "owner_employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_development_plans_tenant_id_company_id",
                table: "development_plans",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_plans_tenant_id_status_next_review_on",
                table: "development_plans",
                columns: new[] { "tenant_id", "status", "next_review_on" });

            migrationBuilder.CreateIndex(
                name: "ux_development_plans_one_current_per_employee",
                table: "development_plans",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true,
                filter: "status IN ('Active','OnHold')");

            migrationBuilder.CreateIndex(
                name: "IX_succession_critical_positions_accountable_employee_id",
                table: "succession_critical_positions",
                column: "accountable_employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_succession_critical_positions_position_id",
                table: "succession_critical_positions",
                column: "position_id");

            migrationBuilder.CreateIndex(
                name: "IX_succession_critical_positions_tenant_id_company_id",
                table: "succession_critical_positions",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_succession_critical_positions_tenant_id_company_id_status",
                table: "succession_critical_positions",
                columns: new[] { "tenant_id", "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_succession_critical_positions_one_active",
                table: "succession_critical_positions",
                columns: new[] { "tenant_id", "position_id" },
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_succession_nominations_critical_position_id_status",
                table: "succession_nominations",
                columns: new[] { "critical_position_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_succession_nominations_employee_id",
                table: "succession_nominations",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_succession_nominations_talent_pool_id_status",
                table: "succession_nominations",
                columns: new[] { "talent_pool_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_succession_nominations_tenant_id_company_id",
                table: "succession_nominations",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_succession_nominations_one_live_per_pool",
                table: "succession_nominations",
                columns: new[] { "tenant_id", "talent_pool_id", "employee_id" },
                unique: true,
                filter: "talent_pool_id IS NOT NULL AND status NOT IN ('Declined','Withdrawn')");

            migrationBuilder.CreateIndex(
                name: "ux_succession_nominations_one_live_per_position",
                table: "succession_nominations",
                columns: new[] { "tenant_id", "critical_position_id", "employee_id" },
                unique: true,
                filter: "critical_position_id IS NOT NULL AND status NOT IN ('Declined','Withdrawn')");

            migrationBuilder.CreateIndex(
                name: "IX_succession_slate_versions_approval_request_id",
                table: "succession_slate_versions",
                column: "approval_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_succession_slate_versions_critical_position_id_version_no",
                table: "succession_slate_versions",
                columns: new[] { "critical_position_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_succession_slate_versions_revised_from_id",
                table: "succession_slate_versions",
                column: "revised_from_id");

            migrationBuilder.CreateIndex(
                name: "IX_succession_slate_versions_tenant_id_company_id",
                table: "succession_slate_versions",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_succession_slate_versions_one_open",
                table: "succession_slate_versions",
                column: "critical_position_id",
                unique: true,
                filter: "status IN ('Draft','Submitted')");

            migrationBuilder.CreateIndex(
                name: "IX_talent_assessments_appraisal_review_id",
                table: "talent_assessments",
                column: "appraisal_review_id");

            migrationBuilder.CreateIndex(
                name: "IX_talent_assessments_employee_id",
                table: "talent_assessments",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_talent_assessments_supersedes_id",
                table: "talent_assessments",
                column: "supersedes_id");

            migrationBuilder.CreateIndex(
                name: "IX_talent_assessments_tenant_id_company_id",
                table: "talent_assessments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_talent_assessments_tenant_id_review_period_status",
                table: "talent_assessments",
                columns: new[] { "tenant_id", "review_period", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_talent_assessments_one_current_per_period",
                table: "talent_assessments",
                columns: new[] { "tenant_id", "employee_id", "review_period" },
                unique: true,
                filter: "status <> 'Superseded'");

            migrationBuilder.CreateIndex(
                name: "IX_talent_pools_owner_employee_id",
                table: "talent_pools",
                column: "owner_employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_talent_pools_tenant_id_company_id",
                table: "talent_pools",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_talent_pools_tenant_id_company_id_code",
                table: "talent_pools",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true);

            // PostgreSQL-only DDL EF cannot model (also applied by the Postgres test fixture from PostgresOnlyDdl).
            foreach (var ddl in PostgresOnlyDdl) migrationBuilder.Sql(ddl);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropPostgresOnlyDdl);
            migrationBuilder.DropTable(
                name: "development_plan_actions");

            migrationBuilder.DropTable(
                name: "succession_slate_versions");

            migrationBuilder.DropTable(
                name: "talent_assessments");

            migrationBuilder.DropTable(
                name: "development_plans");

            migrationBuilder.DropTable(
                name: "succession_nominations");

            migrationBuilder.DropTable(
                name: "succession_critical_positions");

            migrationBuilder.DropTable(
                name: "talent_pools");
        }
        /// <summary>
        /// The DDL EF cannot express, frozen with this migration. PostgresFixture applies the same array after
        /// EnsureCreated, so the integration suite runs against exactly what production gets.
        /// </summary>
        internal static readonly string[] PostgresOnlyDdl =
        [
            OneApprovedSlateConstraintSql,
            SlateVersionGuardSql,
            TalentAssessmentGuardSql,
        ];

        /// <summary>
        /// One Approved slate version per critical position, checked at COMMIT: approving a version supersedes the
        /// previous one in the same transaction, and an immediate (index) check would refuse whichever row EF updated first.
        /// </summary>
        internal const string OneApprovedSlateConstraintSql = """
            ALTER TABLE succession_slate_versions ADD CONSTRAINT ex_succession_slate_versions__one_approved
                EXCLUDE USING btree (critical_position_id WITH =) WHERE (status = 'Approved') DEFERRABLE INITIALLY DEFERRED;
            """;

        /// <summary>
        /// SUC-06: a slate version is immutable from submission. Only its decision may move it (Submitted → Approved /
        /// ReturnedForRevision / Cancelled; Approved → Superseded); its identity, hash and submission stamps never change;
        /// its snapshot changes only when the retention job anonymises it (anonymised_at_utc advances), and the hash is kept.
        /// Only a draft can be deleted.
        /// </summary>
        internal const string SlateVersionGuardSql = """
            CREATE OR REPLACE FUNCTION succession_snapshot_skeleton(s jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $sk$
                SELECT (s - 'candidates' - 'incumbent') || jsonb_build_object(
                    'candidates', COALESCE((SELECT jsonb_agg(c - 'nameEn' - 'nameAr' - 'employeeCode' - 'rationale' - 'calibrationReason' ORDER BY o)
                                            FROM jsonb_array_elements(COALESCE(s -> 'candidates', '[]'::jsonb)) WITH ORDINALITY AS t(c, o)), '[]'::jsonb),
                    'incumbent', CASE WHEN jsonb_typeof(s -> 'incumbent') = 'object'
                                      THEN (s -> 'incumbent') - 'nameEn' - 'nameAr' - 'employeeCode' ELSE 'null'::jsonb END)
            $sk$;
            CREATE OR REPLACE FUNCTION succession_pseudonym(tenant uuid, employee_id integer) RETURNS text LANGUAGE sql IMMUTABLE AS $ps$
                SELECT 'Former employee #' || upper(left(encode(sha256(convert_to(replace(tenant::text, '-', '') || ':' || employee_id::text, 'UTF8')), 'hex'), 8))
            $ps$;
            -- Every rewritten person field must be that person's deterministic pseudonym (code emptied), and every rewritten
            -- free-text field the fixed redaction text: anonymisation cannot smuggle in other content.
            CREATE OR REPLACE FUNCTION succession_snapshot_rewrite_ok(o jsonb, n jsonb, tenant uuid) RETURNS boolean LANGUAGE plpgsql IMMUTABLE AS $rw$
            DECLARE
                i integer;
                oc jsonb;
                nc jsonb;
                redacted constant text := '[redacted after the retention period]';
            BEGIN
                FOR i IN 0 .. COALESCE(jsonb_array_length(n -> 'candidates'), 0) - 1 LOOP
                    oc := o -> 'candidates' -> i;
                    nc := n -> 'candidates' -> i;
                    IF (nc ->> 'nameEn') IS DISTINCT FROM (oc ->> 'nameEn') OR (nc ->> 'nameAr') IS DISTINCT FROM (oc ->> 'nameAr')
                       OR (nc ->> 'employeeCode') IS DISTINCT FROM (oc ->> 'employeeCode') THEN
                        IF (nc ->> 'nameEn') IS DISTINCT FROM succession_pseudonym(tenant, (nc ->> 'employeeId')::integer)
                           OR (nc ->> 'nameAr') IS DISTINCT FROM succession_pseudonym(tenant, (nc ->> 'employeeId')::integer)
                           OR COALESCE(nc ->> 'employeeCode', '') <> '' THEN
                            RETURN false;
                        END IF;
                    END IF;
                    IF (nc ->> 'rationale') IS DISTINCT FROM (oc ->> 'rationale') AND (nc ->> 'rationale') IS DISTINCT FROM redacted THEN
                        RETURN false;
                    END IF;
                    IF (nc ->> 'calibrationReason') IS DISTINCT FROM (oc ->> 'calibrationReason') AND (nc ->> 'calibrationReason') IS DISTINCT FROM redacted THEN
                        RETURN false;
                    END IF;
                END LOOP;
                IF jsonb_typeof(n -> 'incumbent') = 'object' AND (
                       (n -> 'incumbent' ->> 'nameEn') IS DISTINCT FROM (o -> 'incumbent' ->> 'nameEn')
                    OR (n -> 'incumbent' ->> 'nameAr') IS DISTINCT FROM (o -> 'incumbent' ->> 'nameAr')
                    OR (n -> 'incumbent' ->> 'employeeCode') IS DISTINCT FROM (o -> 'incumbent' ->> 'employeeCode')) THEN
                    IF (n -> 'incumbent' ->> 'nameEn') IS DISTINCT FROM succession_pseudonym(tenant, (n -> 'incumbent' ->> 'employeeId')::integer)
                       OR (n -> 'incumbent' ->> 'nameAr') IS DISTINCT FROM succession_pseudonym(tenant, (n -> 'incumbent' ->> 'employeeId')::integer)
                       OR COALESCE(n -> 'incumbent' ->> 'employeeCode', '') <> '' THEN
                        RETURN false;
                    END IF;
                END IF;
                RETURN true;
            END
            $rw$;
            CREATE OR REPLACE FUNCTION succession_slate_versions_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.status <> 'Draft' THEN
                        RAISE EXCEPTION 'succession: slate version % is % and cannot be deleted', OLD.id, OLD.status USING ERRCODE = 'P0001';
                    END IF;
                    RETURN OLD;
                END IF;
                IF OLD.status = 'Draft' THEN
                    RETURN NEW;
                END IF;
                IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.company_id IS DISTINCT FROM OLD.company_id
                   OR NEW.critical_position_id IS DISTINCT FROM OLD.critical_position_id OR NEW.version_no IS DISTINCT FROM OLD.version_no
                   OR NEW.snapshot_sha256 IS DISTINCT FROM OLD.snapshot_sha256 OR NEW.revised_from_id IS DISTINCT FROM OLD.revised_from_id
                   OR NEW.approval_request_id IS DISTINCT FROM OLD.approval_request_id OR NEW.submitted_at_utc IS DISTINCT FROM OLD.submitted_at_utc
                   OR NEW.submitted_by_user_id IS DISTINCT FROM OLD.submitted_by_user_id THEN
                    RAISE EXCEPTION 'succession: slate version % is immutable from submission', OLD.id USING ERRCODE = 'P0001';
                END IF;
                -- Anonymisation (retention) may rewrite ONLY names, codes and free text: the skeleton (ids, order, readiness,
                -- 9-box inputs, rule versions) must be identical, the anonymised_at stamp must advance and the
                -- post-anonymisation hash must be recorded with it.
                IF NEW.snapshot_json IS DISTINCT FROM OLD.snapshot_json
                   AND NOT (NEW.anonymised_at_utc IS NOT NULL AND (OLD.anonymised_at_utc IS NULL OR NEW.anonymised_at_utc > OLD.anonymised_at_utc)
                            AND NEW.anonymised_sha256 IS NOT NULL
                            AND succession_snapshot_skeleton(NEW.snapshot_json) = succession_snapshot_skeleton(OLD.snapshot_json)
                            AND succession_snapshot_rewrite_ok(OLD.snapshot_json, NEW.snapshot_json, NEW.tenant_id)) THEN
                    RAISE EXCEPTION 'succession: the snapshot of slate version % is immutable', OLD.id USING ERRCODE = 'P0001';
                END IF;
                IF NEW.anonymised_sha256 IS DISTINCT FROM OLD.anonymised_sha256 AND NEW.snapshot_json IS NOT DISTINCT FROM OLD.snapshot_json THEN
                    RAISE EXCEPTION 'succession: the anonymised hash of slate version % changes only with its anonymisation', OLD.id USING ERRCODE = 'P0001';
                END IF;
                IF NEW.status <> OLD.status AND NOT (
                       (OLD.status = 'Submitted' AND NEW.status IN ('Approved', 'ReturnedForRevision', 'Cancelled'))
                    OR (OLD.status = 'Approved' AND NEW.status = 'Superseded')) THEN
                    RAISE EXCEPTION 'succession: slate version % cannot move from % to %', OLD.id, OLD.status, NEW.status USING ERRCODE = 'P0001';
                END IF;
                IF (OLD.approved_at_utc IS NOT NULL AND NEW.approved_at_utc IS DISTINCT FROM OLD.approved_at_utc)
                   OR (OLD.effective_from IS NOT NULL AND NEW.effective_from IS DISTINCT FROM OLD.effective_from)
                   OR (OLD.returned_at_utc IS NOT NULL AND NEW.returned_at_utc IS DISTINCT FROM OLD.returned_at_utc)
                   OR (OLD.return_reason IS NOT NULL AND NEW.return_reason IS DISTINCT FROM OLD.return_reason)
                   OR (OLD.superseded_at_utc IS NOT NULL AND NEW.superseded_at_utc IS DISTINCT FROM OLD.superseded_at_utc) THEN
                    RAISE EXCEPTION 'succession: the decision on slate version % is already recorded', OLD.id USING ERRCODE = 'P0001';
                END IF;
                RETURN NEW;
            END
            $fn$;
            CREATE TRIGGER trg_succession_slate_versions__guard BEFORE UPDATE OR DELETE ON succession_slate_versions
                FOR EACH ROW EXECUTE FUNCTION succession_slate_versions_guard();
            """;

        /// <summary>
        /// SUC-03: a calibrated talent assessment is immutable. The only change is to Superseded (once, by a re-assessment
        /// in the same period), plus the retention job's redaction of its free text (anonymised_at_utc set). It cannot be deleted.
        /// </summary>
        internal const string TalentAssessmentGuardSql = """
            CREATE OR REPLACE FUNCTION talent_assessments_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.status IN ('Calibrated', 'Superseded') THEN
                        RAISE EXCEPTION 'succession: assessment % is % and cannot be deleted', OLD.id, OLD.status USING ERRCODE = 'P0001';
                    END IF;
                    RETURN OLD;
                END IF;
                IF OLD.status NOT IN ('Calibrated', 'Superseded') THEN
                    RETURN NEW;
                END IF;
                IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.company_id IS DISTINCT FROM OLD.company_id
                   OR NEW.employee_id IS DISTINCT FROM OLD.employee_id OR NEW.review_period IS DISTINCT FROM OLD.review_period
                   OR NEW.assessor_employee_id IS DISTINCT FROM OLD.assessor_employee_id OR NEW.assessor_user_id IS DISTINCT FROM OLD.assessor_user_id
                   OR NEW.proposed_potential IS DISTINCT FROM OLD.proposed_potential OR NEW.appraisal_review_id IS DISTINCT FROM OLD.appraisal_review_id
                   OR NEW.performance_score IS DISTINCT FROM OLD.performance_score OR NEW.performance_band IS DISTINCT FROM OLD.performance_band
                   OR NEW.band_rule_version IS DISTINCT FROM OLD.band_rule_version OR NEW.calibrated_potential IS DISTINCT FROM OLD.calibrated_potential
                   OR NEW.calibrated_by_user_id IS DISTINCT FROM OLD.calibrated_by_user_id OR NEW.calibrated_by_employee_id IS DISTINCT FROM OLD.calibrated_by_employee_id
                   OR NEW.calibrated_at_utc IS DISTINCT FROM OLD.calibrated_at_utc OR NEW.supersedes_id IS DISTINCT FROM OLD.supersedes_id
                   OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc THEN
                    RAISE EXCEPTION 'succession: calibrated assessment % is immutable', OLD.id USING ERRCODE = 'P0001';
                END IF;
                IF (NEW.potential_evidence IS DISTINCT FROM OLD.potential_evidence OR NEW.calibration_reason IS DISTINCT FROM OLD.calibration_reason)
                   AND NOT (NEW.anonymised_at_utc IS NOT NULL AND (OLD.anonymised_at_utc IS NULL OR NEW.anonymised_at_utc > OLD.anonymised_at_utc)) THEN
                    RAISE EXCEPTION 'succession: calibrated assessment % is immutable', OLD.id USING ERRCODE = 'P0001';
                END IF;
                IF NEW.status <> OLD.status AND NOT (OLD.status = 'Calibrated' AND NEW.status = 'Superseded') THEN
                    RAISE EXCEPTION 'succession: assessment % cannot move from % to %', OLD.id, OLD.status, NEW.status USING ERRCODE = 'P0001';
                END IF;
                IF OLD.superseded_at_utc IS NOT NULL AND NEW.superseded_at_utc IS DISTINCT FROM OLD.superseded_at_utc THEN
                    RAISE EXCEPTION 'succession: assessment % is already superseded', OLD.id USING ERRCODE = 'P0001';
                END IF;
                RETURN NEW;
            END
            $fn$;
            CREATE TRIGGER trg_talent_assessments__guard BEFORE UPDATE OR DELETE ON talent_assessments
                FOR EACH ROW EXECUTE FUNCTION talent_assessments_guard();
            """;

        internal const string DropPostgresOnlyDdl = """
            DROP TRIGGER IF EXISTS trg_succession_slate_versions__guard ON succession_slate_versions;
            DROP TRIGGER IF EXISTS trg_talent_assessments__guard ON talent_assessments;
            DROP FUNCTION IF EXISTS succession_slate_versions_guard();
            DROP FUNCTION IF EXISTS talent_assessments_guard();
            DROP FUNCTION IF EXISTS succession_snapshot_skeleton(jsonb);
            DROP FUNCTION IF EXISTS succession_snapshot_rewrite_ok(jsonb, jsonb, uuid);
            DROP FUNCTION IF EXISTS succession_pseudonym(uuid, integer);
            ALTER TABLE IF EXISTS succession_slate_versions DROP CONSTRAINT IF EXISTS ex_succession_slate_versions__one_approved;
            """;
    }
}
