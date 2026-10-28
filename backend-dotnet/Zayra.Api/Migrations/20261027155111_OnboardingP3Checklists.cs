using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingP3Checklists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "blocks_activation",
                table: "onboarding_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "completed_by",
                table: "onboarding_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "due_offset_days",
                table: "onboarding_tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "due_override_reason",
                table: "onboarding_tasks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "evidence_document_id",
                table: "onboarding_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "evidence_required",
                table: "onboarding_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_statutory",
                table: "onboarding_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "link_section",
                table: "onboarding_tasks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner_role",
                table: "onboarding_tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "HR");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "onboarding_tasks",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<Guid>(
                name: "template_task_id",
                table: "onboarding_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title_ar",
                table: "onboarding_tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "onboarding_checklists",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "onboarding_checklists",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "name_ar",
                table: "onboarding_checklists",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "blocks_activation",
                table: "onboarding_checklist_template_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "condition_nationality_class",
                table: "onboarding_checklist_template_tasks",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "any");

            migrationBuilder.AddColumn<string>(
                name: "description_ar",
                table: "onboarding_checklist_template_tasks",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "evidence_required",
                table: "onboarding_checklist_template_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_statutory",
                table: "onboarding_checklist_template_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "link_section",
                table: "onboarding_checklist_template_tasks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner_role",
                table: "onboarding_checklist_template_tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "HR");

            migrationBuilder.AddColumn<string>(
                name: "title_ar",
                table: "onboarding_checklist_template_tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "hire_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    name_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    match_designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match_grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match_key = table.Column<string>(type: "character varying(110)", maxLength: 110, nullable: false),
                    checklist_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ess_completion_sections = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    defaults = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hire_templates", x => x.id);
                    table.ForeignKey(
                        name: "FK_hire_templates_branches_match_branch_id",
                        column: x => x.match_branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hire_templates_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hire_templates_designations_match_designation_id",
                        column: x => x.match_designation_id,
                        principalTable: "designations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hire_templates_grades_match_grade_id",
                        column: x => x.match_grade_id,
                        principalTable: "grades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hire_templates_onboarding_checklists_checklist_id",
                        column: x => x.checklist_id,
                        principalTable: "onboarding_checklists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_onb_task_blockers",
                table: "onboarding_tasks",
                columns: new[] { "tenant_id", "employee_id" },
                filter: "blocks_activation AND status IN ('Pending','InProgress','Blocked')");

            migrationBuilder.CreateIndex(
                name: "ix_onb_task_worklist",
                table: "onboarding_tasks",
                columns: new[] { "tenant_id", "owner_role", "due_date" },
                filter: "status IN ('Pending','InProgress','Blocked')");

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_tasks_evidence_document_id",
                table: "onboarding_tasks",
                column: "evidence_document_id");

            migrationBuilder.CreateIndex(
                name: "ux_onb_task_once",
                table: "onboarding_tasks",
                columns: new[] { "tenant_id", "employee_id", "template_task_id" },
                unique: true,
                filter: "template_task_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_onb_tasks__owner_role",
                table: "onboarding_tasks",
                sql: "owner_role IN ('HR','IT','Manager','Payroll','Finance','Admin','Gov','Employee')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_onb_tasks__source",
                table: "onboarding_tasks",
                sql: "source IN ('manual','template','offer')");

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_checklists_company_id",
                table: "onboarding_checklists",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ux_onb_checklist_default",
                table: "onboarding_checklists",
                columns: new[] { "tenant_id", "company_id" },
                unique: true,
                filter: "is_default AND NOT is_deleted AND company_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_onb_template_tasks__nationality_class",
                table: "onboarding_checklist_template_tasks",
                sql: "condition_nationality_class IN ('any','saudi','gcc','expat')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_onb_template_tasks__owner_role",
                table: "onboarding_checklist_template_tasks",
                sql: "owner_role IN ('HR','IT','Manager','Payroll','Finance','Admin','Gov','Employee')");

            migrationBuilder.CreateIndex(
                name: "IX_hire_templates_checklist_id",
                table: "hire_templates",
                column: "checklist_id");

            migrationBuilder.CreateIndex(
                name: "IX_hire_templates_company_id",
                table: "hire_templates",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_hire_templates_match_branch_id",
                table: "hire_templates",
                column: "match_branch_id");

            migrationBuilder.CreateIndex(
                name: "IX_hire_templates_match_designation_id",
                table: "hire_templates",
                column: "match_designation_id");

            migrationBuilder.CreateIndex(
                name: "IX_hire_templates_match_grade_id",
                table: "hire_templates",
                column: "match_grade_id");

            migrationBuilder.CreateIndex(
                name: "IX_hire_templates_tenant_id_company_id",
                table: "hire_templates",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_hire_templates_match",
                table: "hire_templates",
                columns: new[] { "tenant_id", "company_id", "match_key" },
                unique: true,
                filter: "is_active AND NOT is_deleted AND match_key <> '00000000-0000-0000-0000-000000000000|00000000-0000-0000-0000-000000000000|00000000-0000-0000-0000-000000000000'");

            migrationBuilder.CreateIndex(
                name: "ux_hire_templates_name",
                table: "hire_templates",
                columns: new[] { "tenant_id", "company_id", "name_key" },
                unique: true,
                filter: "NOT is_deleted");

            migrationBuilder.AddForeignKey(
                name: "FK_onboarding_checklists_companies_company_id",
                table: "onboarding_checklists",
                column: "company_id",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_onboarding_tasks_employee_documents_evidence_document_id",
                table: "onboarding_tasks",
                column: "evidence_document_id",
                principalTable: "employee_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_onboarding_checklists_companies_company_id",
                table: "onboarding_checklists");

            migrationBuilder.DropForeignKey(
                name: "FK_onboarding_tasks_employee_documents_evidence_document_id",
                table: "onboarding_tasks");

            migrationBuilder.DropTable(
                name: "hire_templates");

            migrationBuilder.DropIndex(
                name: "ix_onb_task_blockers",
                table: "onboarding_tasks");

            migrationBuilder.DropIndex(
                name: "ix_onb_task_worklist",
                table: "onboarding_tasks");

            migrationBuilder.DropIndex(
                name: "IX_onboarding_tasks_evidence_document_id",
                table: "onboarding_tasks");

            migrationBuilder.DropIndex(
                name: "ux_onb_task_once",
                table: "onboarding_tasks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_onb_tasks__owner_role",
                table: "onboarding_tasks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_onb_tasks__source",
                table: "onboarding_tasks");

            migrationBuilder.DropIndex(
                name: "IX_onboarding_checklists_company_id",
                table: "onboarding_checklists");

            migrationBuilder.DropIndex(
                name: "ux_onb_checklist_default",
                table: "onboarding_checklists");

            migrationBuilder.DropCheckConstraint(
                name: "ck_onb_template_tasks__nationality_class",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_onb_template_tasks__owner_role",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "blocks_activation",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "completed_by",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "due_offset_days",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "due_override_reason",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "evidence_document_id",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "evidence_required",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "is_statutory",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "link_section",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "owner_role",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "source",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "template_task_id",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "title_ar",
                table: "onboarding_tasks");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "onboarding_checklists");

            migrationBuilder.DropColumn(
                name: "is_default",
                table: "onboarding_checklists");

            migrationBuilder.DropColumn(
                name: "name_ar",
                table: "onboarding_checklists");

            migrationBuilder.DropColumn(
                name: "blocks_activation",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "condition_nationality_class",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "description_ar",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "evidence_required",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "is_statutory",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "link_section",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "owner_role",
                table: "onboarding_checklist_template_tasks");

            migrationBuilder.DropColumn(
                name: "title_ar",
                table: "onboarding_checklist_template_tasks");
        }
    }
}
