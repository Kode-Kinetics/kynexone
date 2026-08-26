using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionalHrInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Upgrade preflight: immutable outcome duplicates cannot be deleted or guessed without
            // corrupting audit/business history. Fail before changing indexes with an actionable
            // diagnostic. Operators must reconcile the named aggregate and rerun the migration.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM approval_decisions
                        GROUP BY tenant_id, approval_request_id, step_order HAVING COUNT(*) > 1)
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: duplicate approval_decisions for (tenant_id, approval_request_id, step_order). Reconcile the approval audit history before retrying.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM overtime_calculations
                        GROUP BY tenant_id, overtime_request_id HAVING COUNT(*) > 1)
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: duplicate overtime_calculations for one request. Reconcile the calculation history before retrying.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM overtime_payroll_impacts
                        GROUP BY tenant_id, overtime_request_id HAVING COUNT(*) > 1)
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: duplicate overtime_payroll_impacts for one request. Reconcile payroll impact history before retrying.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM overtime_comp_off_conversions
                        GROUP BY tenant_id, overtime_request_id HAVING COUNT(*) > 1)
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: duplicate overtime_comp_off_conversions for one request. Reconcile comp-off conversion history before retrying.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM leave_balance_transactions
                        WHERE transaction_type = 'Accrual'
                        GROUP BY tenant_id, employee_id, leave_type_id, year, reference HAVING COUNT(*) > 1)
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: duplicate monthly leave accrual ledger witnesses. Reconcile balances and their accrual history before retrying.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM overtime_approvals
                        GROUP BY tenant_id, overtime_request_id HAVING COUNT(*) > 2)
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: an overtime request has more than two legacy approval decisions. Reconcile its audit history before retrying.';
                    END IF;
                    IF EXISTS (
                        SELECT 1
                        FROM (
                            SELECT decision,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY tenant_id, overtime_request_id
                                       ORDER BY decided_at_utc NULLS LAST, id) AS rn,
                                   COUNT(*) OVER (PARTITION BY tenant_id, overtime_request_id) AS total
                            FROM overtime_approvals
                        ) ranked
                        WHERE total = 2 AND rn = 1 AND decision <> 'Approved')
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: a two-step overtime history has a non-approved first decision. Reconcile the request before retrying.';
                    END IF;
                END $$;

                -- Legacy code left both legitimate manager and final rows at the default level
                -- "Manager". Re-label (never delete) the chronological first/second decisions.
                WITH ranked AS (
                    SELECT id, tenant_id, overtime_request_id,
                           ROW_NUMBER() OVER (
                               PARTITION BY tenant_id, overtime_request_id
                               ORDER BY decided_at_utc NULLS LAST, id) AS rn,
                           COUNT(*) OVER (PARTITION BY tenant_id, overtime_request_id) AS total
                    FROM overtime_approvals
                )
                UPDATE overtime_approvals approvals
                SET approval_level = CASE
                    WHEN ranked.total = 2 AND ranked.rn = 1 THEN 'Manager'
                    WHEN ranked.total = 2 AND ranked.rn = 2 THEN 'Final'
                    WHEN requests.status = 'PendingHR' THEN 'Manager'
                    ELSE 'Final'
                END
                FROM ranked
                JOIN overtime_requests requests
                  ON requests.tenant_id = ranked.tenant_id
                 AND requests.id = ranked.overtime_request_id
                WHERE approvals.id = ranked.id;
                """);

            migrationBuilder.DropIndex(
                name: "IX_overtime_calculations_tenant_id_overtime_request_id",
                table: "overtime_calculations");

            migrationBuilder.DropIndex(
                name: "IX_overtime_approvals_tenant_id_overtime_request_id",
                table: "overtime_approvals");

            migrationBuilder.DropIndex(
                name: "IX_approval_decisions_tenant_id_approval_request_id_step_order",
                table: "approval_decisions");

            migrationBuilder.AddColumn<Guid>(
                name: "source_id",
                table: "payroll_adjustments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_type",
                table: "payroll_adjustments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "decision_version",
                table: "overtime_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "leave_encashment_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "leave_encashment_requests",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "decision_version",
                table: "leave_encashment_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "payroll_adjustment_id",
                table: "leave_encashment_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "payroll_run_id",
                table: "leave_encashment_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "void_reason",
                table: "leave_encashment_requests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "voided_at_utc",
                table: "leave_encashment_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "voided_by_user_id",
                table: "leave_encashment_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "idempotency_key",
                table: "comp_off_usages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "overtime_comp_off_conversion_id",
                table: "comp_off_credits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "usage_version",
                table: "comp_off_credits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "decision_version",
                table: "approval_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Legacy encashments pre-date legal-entity/currency linkage. Backfill only through the
            // tenant-scoped employee primary key and that employee's actual company relationship.
            // Historical payroll artifacts are deliberately not inferred or fabricated.
            migrationBuilder.Sql("""
                UPDATE leave_encashment_requests encashments
                SET company_id = employees.company_id,
                    currency = UPPER(TRIM(companies.default_currency))
                FROM employees
                JOIN companies
                  ON companies.tenant_id = employees.tenant_id
                 AND companies.id = employees.company_id
                WHERE employees.tenant_id = encashments.tenant_id
                  AND employees.id = encashments.employee_id
                  AND employees.company_id IS NOT NULL
                  AND (encashments.company_id IS NULL OR TRIM(encashments.currency) = '');

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM leave_encashment_requests
                        WHERE status = 'HRApproved'
                          AND (company_id IS NULL OR TRIM(currency) = ''))
                    THEN
                        RAISE EXCEPTION 'HR invariant upgrade blocked: one or more HRApproved leave encashments cannot be linked to an employee legal entity and currency. Assign the employee to a valid same-tenant company with default_currency, then retry. No payroll run or adjustment link was inferred.';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "worker_heartbeats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    worker_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    instance_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_succeeded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_failed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_heartbeats", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_tenant_id_source_type_source_id",
                table: "payroll_adjustments",
                columns: new[] { "tenant_id", "source_type", "source_id" },
                unique: true,
                filter: "\"source_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_overtime_payroll_impacts_tenant_id_overtime_request_id",
                table: "overtime_payroll_impacts",
                columns: new[] { "tenant_id", "overtime_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_overtime_comp_off_conversions_tenant_id_overtime_request_id",
                table: "overtime_comp_off_conversions",
                columns: new[] { "tenant_id", "overtime_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_overtime_calculations_tenant_id_overtime_request_id",
                table: "overtime_calculations",
                columns: new[] { "tenant_id", "overtime_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_overtime_approvals_tenant_id_overtime_request_id_approval_l~",
                table: "overtime_approvals",
                columns: new[] { "tenant_id", "overtime_request_id", "approval_level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leave_encashment_requests_tenant_id_company_id",
                table: "leave_encashment_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_encashment_requests_tenant_id_company_id_status",
                table: "leave_encashment_requests",
                columns: new[] { "tenant_id", "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_encashment_requests_tenant_id_payroll_adjustment_id",
                table: "leave_encashment_requests",
                columns: new[] { "tenant_id", "payroll_adjustment_id" },
                unique: true,
                filter: "\"payroll_adjustment_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_leave_encashment_requests_tenant_id_payroll_run_id",
                table: "leave_encashment_requests",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_balance_transactions_tenant_id_employee_id_leave_typ~1",
                table: "leave_balance_transactions",
                columns: new[] { "tenant_id", "employee_id", "leave_type_id", "year", "reference" },
                unique: true,
                filter: "\"transaction_type\" = 'Accrual'");

            migrationBuilder.CreateIndex(
                name: "IX_comp_off_usages_tenant_id_comp_off_credit_id_idempotency_key",
                table: "comp_off_usages",
                columns: new[] { "tenant_id", "comp_off_credit_id", "idempotency_key" },
                unique: true,
                filter: "\"idempotency_key\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_comp_off_credits_tenant_id_overtime_comp_off_conversion_id",
                table: "comp_off_credits",
                columns: new[] { "tenant_id", "overtime_comp_off_conversion_id" },
                unique: true,
                filter: "\"overtime_comp_off_conversion_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_tenant_id_approval_request_id_step_order",
                table: "approval_decisions",
                columns: new[] { "tenant_id", "approval_request_id", "step_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_worker_heartbeats_worker_name_instance_id",
                table: "worker_heartbeats",
                columns: new[] { "worker_name", "instance_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_worker_heartbeats_worker_name_updated_at_utc",
                table: "worker_heartbeats",
                columns: new[] { "worker_name", "updated_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worker_heartbeats");

            migrationBuilder.DropIndex(
                name: "IX_payroll_adjustments_tenant_id_source_type_source_id",
                table: "payroll_adjustments");

            migrationBuilder.DropIndex(
                name: "IX_overtime_payroll_impacts_tenant_id_overtime_request_id",
                table: "overtime_payroll_impacts");

            migrationBuilder.DropIndex(
                name: "IX_overtime_comp_off_conversions_tenant_id_overtime_request_id",
                table: "overtime_comp_off_conversions");

            migrationBuilder.DropIndex(
                name: "IX_overtime_calculations_tenant_id_overtime_request_id",
                table: "overtime_calculations");

            migrationBuilder.DropIndex(
                name: "IX_overtime_approvals_tenant_id_overtime_request_id_approval_l~",
                table: "overtime_approvals");

            migrationBuilder.DropIndex(
                name: "IX_leave_encashment_requests_tenant_id_company_id",
                table: "leave_encashment_requests");

            migrationBuilder.DropIndex(
                name: "IX_leave_encashment_requests_tenant_id_company_id_status",
                table: "leave_encashment_requests");

            migrationBuilder.DropIndex(
                name: "IX_leave_encashment_requests_tenant_id_payroll_adjustment_id",
                table: "leave_encashment_requests");

            migrationBuilder.DropIndex(
                name: "IX_leave_encashment_requests_tenant_id_payroll_run_id",
                table: "leave_encashment_requests");

            migrationBuilder.DropIndex(
                name: "IX_leave_balance_transactions_tenant_id_employee_id_leave_typ~1",
                table: "leave_balance_transactions");

            migrationBuilder.DropIndex(
                name: "IX_comp_off_usages_tenant_id_comp_off_credit_id_idempotency_key",
                table: "comp_off_usages");

            migrationBuilder.DropIndex(
                name: "IX_comp_off_credits_tenant_id_overtime_comp_off_conversion_id",
                table: "comp_off_credits");

            migrationBuilder.DropIndex(
                name: "IX_approval_decisions_tenant_id_approval_request_id_step_order",
                table: "approval_decisions");

            migrationBuilder.DropColumn(
                name: "source_id",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "source_type",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "decision_version",
                table: "overtime_requests");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "decision_version",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "payroll_adjustment_id",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "payroll_run_id",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "void_reason",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "voided_at_utc",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "voided_by_user_id",
                table: "leave_encashment_requests");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "comp_off_usages");

            migrationBuilder.DropColumn(
                name: "overtime_comp_off_conversion_id",
                table: "comp_off_credits");

            migrationBuilder.DropColumn(
                name: "usage_version",
                table: "comp_off_credits");

            migrationBuilder.DropColumn(
                name: "decision_version",
                table: "approval_requests");

            migrationBuilder.CreateIndex(
                name: "IX_overtime_calculations_tenant_id_overtime_request_id",
                table: "overtime_calculations",
                columns: new[] { "tenant_id", "overtime_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_approvals_tenant_id_overtime_request_id",
                table: "overtime_approvals",
                columns: new[] { "tenant_id", "overtime_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_tenant_id_approval_request_id_step_order",
                table: "approval_decisions",
                columns: new[] { "tenant_id", "approval_request_id", "step_order" });
        }
    }
}
