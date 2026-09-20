using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalSendBackRounds : Migration
    {
        /// <summary>
        /// W2-E — send back and resubmission. A resubmitted request restarts at step 1 in a new
        /// submission round, so decisions become unique per (request, round, step) instead of per
        /// (request, step).
        ///
        /// Additive: two NOT NULL integer columns with a database default of 1 (every existing request
        /// and decision is round 1, and an older build that does not know the column still inserts
        /// round 1), and the unique index widened by one column. The new index is created before the
        /// old one is dropped, so uniqueness is enforced throughout.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "submission_round",
                table: "approval_requests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "submission_round",
                table: "approval_decisions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_request_round_step",
                table: "approval_decisions",
                columns: new[] { "tenant_id", "approval_request_id", "submission_round", "step_order" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_approval_decisions_tenant_id_approval_request_id_step_order",
                table: "approval_decisions");
        }

        /// <summary>
        /// Restores the (request, step) index. If a request was resubmitted after a send back it has two
        /// decisions for the same step, so the old UNIQUE index cannot be rebuilt without deleting audit
        /// history. In that case the index is rebuilt non-unique and a NOTICE is raised; the previous
        /// build still has its DecisionVersion compare-and-swap, which is the primary guard. No decision
        /// row is ever deleted. Requests left in ReturnedToRequester are not Pending to the previous build,
        /// so they leave every approver queue; their leave reservation was already released at send back.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM approval_decisions
                        GROUP BY tenant_id, approval_request_id, step_order
                        HAVING COUNT(*) > 1)
                    THEN
                        RAISE NOTICE 'approval_decisions has resubmitted rounds; rebuilding IX_approval_decisions_tenant_id_approval_request_id_step_order as NON-unique.';
                        CREATE INDEX "IX_approval_decisions_tenant_id_approval_request_id_step_order"
                            ON approval_decisions (tenant_id, approval_request_id, step_order);
                    ELSE
                        CREATE UNIQUE INDEX "IX_approval_decisions_tenant_id_approval_request_id_step_order"
                            ON approval_decisions (tenant_id, approval_request_id, step_order);
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_approval_decisions_request_round_step",
                table: "approval_decisions");

            migrationBuilder.DropColumn(
                name: "submission_round",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "submission_round",
                table: "approval_decisions");
        }
    }
}
