using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRecruitmentSchedulingCompanyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "workforce_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "manpower_requisitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "job_openings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "interview_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "interview_feedbacks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "compliance_renewals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "candidate_assessments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workforce_plans_tenant_id_company_id",
                table: "workforce_plans",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_manpower_requisitions_tenant_id_company_id",
                table: "manpower_requisitions",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_job_openings_tenant_id_company_id",
                table: "job_openings",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_interview_schedules_tenant_id_company_id",
                table: "interview_schedules",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_interview_feedbacks_tenant_id_company_id",
                table: "interview_feedbacks",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_renewals_tenant_id_company_id",
                table: "compliance_renewals",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_assessments_tenant_id_company_id",
                table: "candidate_assessments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.Sql(BackfillSql);
        }

        /// <summary>
        /// Attributes each existing row to the company that owns the record it hangs from, so a company-scoped user keeps seeing
        /// what they saw and stops seeing the rest. Every statement only fills a still-NULL company_id from a parent that HAS one
        /// (never overwrites, never invents), stays inside the row's own tenant, and is re-runnable.
        ///   - interviews and assessments: the application's company; feedback: its interview's, else its application's;
        ///   - requisitions: the owning department's company, else the requester's employee record, else the opening raised from it;
        ///   - openings: the company its applications already belong to (the most common one), else the source requisition's;
        ///   - workforce plans: the department's company; compliance renewals: the employee's company.
        /// A row with no parent that resolves stays NULL here, as for the candidates, applications and offers
        /// (AddRecruitmentCompanyScope): a NULL operational row is visible to group scope only, and CompanyScopeBackfill assigns the
        /// tenant's default company to the leftovers at the next start.
        /// </summary>
        public const string BackfillSql = """
            UPDATE manpower_requisitions r SET company_id = d.company_id
            FROM departments d
            WHERE r.company_id IS NULL AND d.id = r.department_id AND d.tenant_id = r.tenant_id AND d.company_id IS NOT NULL;

            UPDATE manpower_requisitions r SET company_id = e.company_id
            FROM employees e
            WHERE r.company_id IS NULL AND r.requested_by_employee_id IS NOT NULL
              AND e.id = r.requested_by_employee_id AND e.tenant_id = r.tenant_id AND e.company_id IS NOT NULL;

            UPDATE job_openings o SET company_id = a.company_id
            FROM (
                SELECT DISTINCT ON (job_opening_id, tenant_id) job_opening_id, tenant_id, company_id
                FROM (
                    SELECT job_opening_id, tenant_id, company_id, count(*) AS n
                    FROM job_applications
                    WHERE company_id IS NOT NULL
                    GROUP BY job_opening_id, tenant_id, company_id
                ) g
                ORDER BY job_opening_id, tenant_id, n DESC, company_id
            ) a
            WHERE o.company_id IS NULL AND a.job_opening_id = o.id AND a.tenant_id = o.tenant_id;

            UPDATE job_openings o SET company_id = r.company_id
            FROM manpower_requisitions r
            WHERE o.company_id IS NULL AND r.id = o.requisition_id AND r.tenant_id = o.tenant_id AND r.company_id IS NOT NULL;

            UPDATE manpower_requisitions r SET company_id = o.company_id
            FROM job_openings o
            WHERE r.company_id IS NULL AND o.requisition_id = r.id AND o.tenant_id = r.tenant_id AND o.company_id IS NOT NULL;

            UPDATE workforce_plans p SET company_id = d.company_id
            FROM departments d
            WHERE p.company_id IS NULL AND d.id = p.department_id AND d.tenant_id = p.tenant_id AND d.company_id IS NOT NULL;

            UPDATE interview_schedules i SET company_id = a.company_id
            FROM job_applications a
            WHERE i.company_id IS NULL AND a.id = i.application_id AND a.tenant_id = i.tenant_id AND a.company_id IS NOT NULL;

            UPDATE candidate_assessments c SET company_id = a.company_id
            FROM job_applications a
            WHERE c.company_id IS NULL AND a.id = c.application_id AND a.tenant_id = c.tenant_id AND a.company_id IS NOT NULL;

            UPDATE interview_feedbacks f SET company_id = i.company_id
            FROM interview_schedules i
            WHERE f.company_id IS NULL AND i.id = f.interview_schedule_id AND i.tenant_id = f.tenant_id AND i.company_id IS NOT NULL;

            UPDATE interview_feedbacks f SET company_id = a.company_id
            FROM job_applications a
            WHERE f.company_id IS NULL AND a.id = f.application_id AND a.tenant_id = f.tenant_id AND a.company_id IS NOT NULL;

            UPDATE compliance_renewals r SET company_id = e.company_id
            FROM employees e
            WHERE r.company_id IS NULL AND e.public_id = r.employee_id AND e.tenant_id = r.tenant_id AND e.company_id IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workforce_plans_tenant_id_company_id",
                table: "workforce_plans");

            migrationBuilder.DropIndex(
                name: "IX_manpower_requisitions_tenant_id_company_id",
                table: "manpower_requisitions");

            migrationBuilder.DropIndex(
                name: "IX_job_openings_tenant_id_company_id",
                table: "job_openings");

            migrationBuilder.DropIndex(
                name: "IX_interview_schedules_tenant_id_company_id",
                table: "interview_schedules");

            migrationBuilder.DropIndex(
                name: "IX_interview_feedbacks_tenant_id_company_id",
                table: "interview_feedbacks");

            migrationBuilder.DropIndex(
                name: "IX_compliance_renewals_tenant_id_company_id",
                table: "compliance_renewals");

            migrationBuilder.DropIndex(
                name: "IX_candidate_assessments_tenant_id_company_id",
                table: "candidate_assessments");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "workforce_plans");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "manpower_requisitions");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "job_openings");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "interview_schedules");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "interview_feedbacks");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "compliance_renewals");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "candidate_assessments");
        }
    }
}
