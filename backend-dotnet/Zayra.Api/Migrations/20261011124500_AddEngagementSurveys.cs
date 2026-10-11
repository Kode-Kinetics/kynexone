using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// ENG — engagement surveys: six new tables and two defaulted columns on tenant_hr_configs. Expand-only.
    /// <para>Two guarantees EF cannot model are raw SQL (<see cref="PostgresOnlyDdl"/>, mirrored into the test
    /// PostgresFixture): a published template version is immutable, and a submitted response is never updated.</para>
    /// </summary>
    public partial class AddEngagementSurveys : Migration
    {
        /// <summary>The raw DDL, frozen here so the test fixture runs exactly what production gets.</summary>
        public static readonly string[] PostgresOnlyDdl =
        [
            """
            CREATE OR REPLACE FUNCTION survey_templates_published_immutable() RETURNS trigger AS $fn$
            BEGIN
              IF OLD.status <> 'Draft' AND (
                   NEW.content_json IS DISTINCT FROM OLD.content_json
                OR NEW.name_en IS DISTINCT FROM OLD.name_en
                OR NEW.name_ar IS DISTINCT FROM OLD.name_ar
                OR NEW.description_en IS DISTINCT FROM OLD.description_en
                OR NEW.description_ar IS DISTINCT FROM OLD.description_ar
                OR NEW.family_id IS DISTINCT FROM OLD.family_id
                OR NEW.version_number IS DISTINCT FROM OLD.version_number
                OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                OR NEW.status = 'Draft') THEN
                RAISE EXCEPTION 'survey_template_published_immutable: a published survey template version cannot change; create a new version'
                  USING ERRCODE = 'check_violation';
              END IF;
              RETURN NEW;
            END
            $fn$ LANGUAGE plpgsql;
            """,
            """
            DROP TRIGGER IF EXISTS trg_survey_templates__published_immutable ON survey_templates;
            CREATE TRIGGER trg_survey_templates__published_immutable BEFORE UPDATE ON survey_templates
              FOR EACH ROW EXECUTE FUNCTION survey_templates_published_immutable();
            """,
            """
            CREATE OR REPLACE FUNCTION survey_responses_append_only() RETURNS trigger AS $fn$
            BEGIN
              RAISE EXCEPTION 'survey_response_append_only: a submitted survey response is never changed'
                USING ERRCODE = 'check_violation';
            END
            $fn$ LANGUAGE plpgsql;
            """,
            """
            DROP TRIGGER IF EXISTS trg_survey_responses__append_only ON survey_responses;
            CREATE TRIGGER trg_survey_responses__append_only BEFORE UPDATE ON survey_responses
              FOR EACH ROW EXECUTE FUNCTION survey_responses_append_only();
            """,
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "engagement_free_text_retention_months",
                table: "tenant_hr_configs",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "engagement_min_cohort_size",
                table: "tenant_hr_configs",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.CreateTable(
                name: "survey_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description_en = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_templates", x => x.id);
                    table.CheckConstraint("ck_survey_templates__published_stamp", "status = 'Draft' OR published_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_survey_templates__status", "status IN ('Draft','Published','Retired')");
                    table.CheckConstraint("ck_survey_templates__version_positive", "version_number >= 1");
                });

            migrationBuilder.CreateTable(
                name: "surveys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    audience_json = table.Column<string>(type: "jsonb", nullable: false),
                    opens_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closes_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    recurrence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_number = table.Column<int>(type: "integer", nullable: false),
                    min_cohort_size = table.Column<int>(type: "integer", nullable: false),
                    frozen_min_cohort_size = table.Column<int>(type: "integer", nullable: true),
                    comment_dimension = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reminder_offsets_days = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    invitation_count = table.Column<int>(type: "integer", nullable: false),
                    launched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    launched_by = table.Column<Guid>(type: "uuid", nullable: true),
                    invites_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_cycle_created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_surveys", x => x.id);
                    table.CheckConstraint("ck_surveys__closes_after_opens", "closes_at_utc > opens_at_utc");
                    table.CheckConstraint("ck_surveys__comment_dimension", "comment_dimension IN ('none','company','department','designation','grade','tenure')");
                    table.CheckConstraint("ck_surveys__cycle_positive", "cycle_number >= 1");
                    table.CheckConstraint("ck_surveys__launched_stamp", "status IN ('Draft','Cancelled') OR launched_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_surveys__frozen_k_not_lower", "frozen_min_cohort_size IS NULL OR frozen_min_cohort_size >= min_cohort_size");
                    table.CheckConstraint("ck_surveys__min_cohort_floor", "min_cohort_size >= 3");
                    table.CheckConstraint("ck_surveys__mode", "mode IN ('Identified','Confidential','Anonymous')");
                    table.CheckConstraint("ck_surveys__recurrence", "recurrence IN ('None','Monthly','Quarterly','SemiAnnual','Annual')");
                    table.CheckConstraint("ck_surveys__status", "status IN ('Draft','Scheduled','Open','Closed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_surveys_survey_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "survey_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "survey_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    survey_id = table.Column<Guid>(type: "uuid", nullable: true),
                    survey_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    owner_employee_id = table.Column<int>(type: "integer", nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    progress_json = table.Column<string>(type: "jsonb", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    overdue_reminded_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_actions", x => x.id);
                    table.CheckConstraint("ck_survey_actions__done_stamp", "status <> 'Done' OR completed_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_survey_actions__source_kind", "source_kind IN ('Overall','Driver','Item','Cohort')");
                    table.CheckConstraint("ck_survey_actions__status", "status IN ('Open','InProgress','Done','Cancelled')");
                    table.ForeignKey(
                        name: "FK_survey_actions_employees_owner_employee_id",
                        column: x => x.owner_employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_survey_actions_surveys_survey_id",
                        column: x => x.survey_id,
                        principalTable: "surveys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "survey_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    survey_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    invited_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    first_opened_on = table.Column<DateOnly>(type: "date", nullable: true),
                    submitted_on = table.Column<DateOnly>(type: "date", nullable: true),
                    session_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    draft_cipher = table.Column<string>(type: "character varying(64000)", maxLength: 64000, nullable: true),
                    draft_saved_on = table.Column<DateOnly>(type: "date", nullable: true),
                    identified_response_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reminders_sent = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_invitations", x => x.id);
                    table.CheckConstraint("ck_survey_invitations__anonymous_ledger_minimal", "mode <> 'Anonymous' OR (status <> 'Opened' AND first_opened_on IS NULL AND submitted_on IS NULL AND draft_saved_on IS NULL AND session_token_hash IS NULL AND version = CASE WHEN status = 'Submitted' THEN 1 ELSE 0 END)");
                    table.CheckConstraint("ck_survey_invitations__identified_link_only_when_identified", "mode = 'Identified' OR identified_response_id IS NULL");
                    table.CheckConstraint("ck_survey_invitations__mode", "mode IN ('Identified','Confidential','Anonymous')");
                    table.CheckConstraint("ck_survey_invitations__status", "status IN ('Invited','Opened','Submitted','Cancelled')");
                    table.CheckConstraint("ck_survey_invitations__submitted_has_date", "status <> 'Submitted' OR submitted_on IS NOT NULL OR mode = 'Anonymous'");
                    table.CheckConstraint("ck_survey_invitations__submitted_no_draft", "status <> 'Submitted' OR (draft_cipher IS NULL AND session_token_hash IS NULL)");
                    table.ForeignKey(
                        name: "FK_survey_invitations_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_survey_invitations_surveys_survey_id",
                        column: x => x.survey_id,
                        principalTable: "surveys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "survey_responses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    survey_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_band = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    tenure_band = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    cohort_manager_employee_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_responses", x => x.id);
                    table.CheckConstraint("ck_survey_responses__tenure_band", "tenure_band IN ('lt1y','1to3y','3to5y','5yplus','unknown')");
                    table.ForeignKey(
                        name: "FK_survey_responses_surveys_survey_id",
                        column: x => x.survey_id,
                        principalTable: "surveys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "survey_answers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    survey_id = table.Column<Guid>(type: "uuid", nullable: false),
                    response_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    numeric_value = table.Column<int>(type: "integer", nullable: true),
                    choice_keys = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    text_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    text_purged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_answers", x => x.id);
                    table.CheckConstraint("ck_survey_answers__has_value", "numeric_value IS NOT NULL OR choice_keys IS NOT NULL OR text_value IS NOT NULL OR text_purged");
                    table.CheckConstraint("ck_survey_answers__numeric_range", "numeric_value IS NULL OR numeric_value BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_survey_answers__purged_has_no_text", "NOT text_purged OR text_value IS NULL");
                    table.ForeignKey(
                        name: "FK_survey_answers_survey_responses_response_id",
                        column: x => x.response_id,
                        principalTable: "survey_responses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_survey_answers_surveys_survey_id",
                        column: x => x.survey_id,
                        principalTable: "surveys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_hr_configs__engagement_min_cohort_floor",
                table: "tenant_hr_configs",
                sql: "engagement_min_cohort_size >= 3");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_hr_configs__engagement_text_retention",
                table: "tenant_hr_configs",
                sql: "engagement_free_text_retention_months BETWEEN 1 AND 120");

            migrationBuilder.CreateIndex(
                name: "IX_survey_actions_owner_employee_id",
                table: "survey_actions",
                column: "owner_employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_actions_status_due_on",
                table: "survey_actions",
                columns: new[] { "status", "due_on" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_actions_survey_id",
                table: "survey_actions",
                column: "survey_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_actions_tenant_id_company_id",
                table: "survey_actions",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_actions_tenant_id_owner_employee_id_status",
                table: "survey_actions",
                columns: new[] { "tenant_id", "owner_employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_actions_tenant_id_survey_id",
                table: "survey_actions",
                columns: new[] { "tenant_id", "survey_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_response_id_question_key",
                table: "survey_answers",
                columns: new[] { "response_id", "question_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_survey_id",
                table: "survey_answers",
                column: "survey_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_tenant_id_company_id",
                table: "survey_answers",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_tenant_id_survey_id_question_key",
                table: "survey_answers",
                columns: new[] { "tenant_id", "survey_id", "question_key" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_invitations_employee_id",
                table: "survey_invitations",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_invitations_survey_id_status",
                table: "survey_invitations",
                columns: new[] { "survey_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_invitations_tenant_id_company_id",
                table: "survey_invitations",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_invitations_tenant_id_employee_id",
                table: "survey_invitations",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_invitations_tenant_id_survey_id_employee_id",
                table: "survey_invitations",
                columns: new[] { "tenant_id", "survey_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_responses_survey_id",
                table: "survey_responses",
                column: "survey_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_responses_tenant_id_company_id",
                table: "survey_responses",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_responses_tenant_id_survey_id",
                table: "survey_responses",
                columns: new[] { "tenant_id", "survey_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_templates_tenant_id_company_id",
                table: "survey_templates",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_templates_tenant_id_family_id_version_number",
                table: "survey_templates",
                columns: new[] { "tenant_id", "family_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_templates_tenant_id_updated_at_utc",
                table: "survey_templates",
                columns: new[] { "tenant_id", "updated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_surveys_status_closes_at_utc",
                table: "surveys",
                columns: new[] { "status", "closes_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_surveys_status_opens_at_utc",
                table: "surveys",
                columns: new[] { "status", "opens_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_surveys_template_id",
                table: "surveys",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "IX_surveys_tenant_id_company_id",
                table: "surveys",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_surveys_tenant_id_opens_at_utc",
                table: "surveys",
                columns: new[] { "tenant_id", "opens_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_surveys_tenant_id_series_id_cycle_number",
                table: "surveys",
                columns: new[] { "tenant_id", "series_id", "cycle_number" },
                unique: true);

            foreach (var ddl in PostgresOnlyDdl) migrationBuilder.Sql(ddl);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_survey_responses__append_only ON survey_responses;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_survey_templates__published_immutable ON survey_templates;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS survey_responses_append_only();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS survey_templates_published_immutable();");

            migrationBuilder.DropTable(
                name: "survey_actions");

            migrationBuilder.DropTable(
                name: "survey_answers");

            migrationBuilder.DropTable(
                name: "survey_invitations");

            migrationBuilder.DropTable(
                name: "survey_responses");

            migrationBuilder.DropTable(
                name: "surveys");

            migrationBuilder.DropTable(
                name: "survey_templates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_hr_configs__engagement_min_cohort_floor",
                table: "tenant_hr_configs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_hr_configs__engagement_text_retention",
                table: "tenant_hr_configs");

            migrationBuilder.DropColumn(
                name: "engagement_free_text_retention_months",
                table: "tenant_hr_configs");

            migrationBuilder.DropColumn(
                name: "engagement_min_cohort_size",
                table: "tenant_hr_configs");
        }
    }
}
