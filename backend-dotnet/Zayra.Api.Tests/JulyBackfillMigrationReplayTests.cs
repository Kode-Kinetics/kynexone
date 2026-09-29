using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Replays the two 2026-07-13 backfill migrations against a database in PRODUCTION'S state.
///
/// <para>Three migrations from 15148d0 were hand-written without <c>[Migration]</c>, so EF could
/// not see them and <c>database update</c> exited 0 having skipped them for 70 days. The
/// attributes were restored in 7687d16, which means the next <c>--migrate</c> will try to apply
/// whatever the history table is missing. Verified against the live database on 2026-09-21:</para>
/// <list type="bullet">
///   <item><c>20260713061000_AddSalaryStructureEligibilityAndVersioning</c> IS in
///   <c>__EFMigrationsHistory</c>. It will not re-run, which matters because it is the one of the
///   three that uses <c>AddColumn</c> — bare <c>ALTER TABLE … ADD COLUMN</c>, no IF NOT EXISTS —
///   and would abort with 42701 on a second run. No action needed, and no history row to insert.</item>
///   <item><c>20260713062000</c> and <c>20260713073000</c> are absent and WILL run.</item>
/// </list>
///
/// <para>Production's shape is peculiar and neither a fresh database nor a fully-migrated one
/// reproduces it: <c>20260816013100_RepairMigrationModelParity</c> has already created every
/// column and index that <c>20260713073000</c> would add, and — crucially — DROPPED the defaults
/// off the seven NOT NULL approval-queue columns to match the EF model. This fixture rebuilds
/// exactly that: migrate to head, then withdraw the two migrations from the history table and
/// restore the pre-backfill data, so the replay runs against the schema production actually has.
///
/// <para>Own container rather than the shared fixture: these migrations sweep every tenant.</para>
/// </summary>
public sealed class JulyBackfillMigrationReplayTests : IAsyncLifetime
{
    private const string BackfillMigration = "20260713062000_BackfillEmployeeChangeApprovalRequests";
    private const string QueueMigration = "20260713073000_AddApprovalQueueAccountability";
    private const string SalaryStructureMigration = "20260713061000_AddSalaryStructureEligibilityAndVersioning";

    /// <summary>What the running application has established for the rows it already routed.</summary>
    private const string LiveQueue = "Role:HR Manager";
    private const int LiveSlaHours = 48;

    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private string _cs = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _cs = _container.GetConnectionString();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_cs).Options);

    [Fact]
    public async Task Replay_AgainstProductionShape_Succeeds_BackfillsTheUnroutedRow_AndLeavesLiveRoutingAlone()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        await using (var db = CreateDb())
            await db.Database.MigrateAsync();

        // ── Reproduce production ──────────────────────────────────────────────────────────────
        await WithdrawFromHistoryAsync(BackfillMigration, QueueMigration);

        // The seven approval-queue columns must be NOT NULL with NO DEFAULT, which is what
        // RepairMigrationModelParity leaves behind. Assert it rather than assume it: if this
        // drifted, the test would pass for the wrong reason and stop covering the real hazard.
        var noDefault = await ColumnsThatAreNotNullWithoutDefaultAsync();
        noDefault.Should().Contain(new[]
        {
            "current_approver_name", "current_approver_role", "current_approver_type",
            "current_queue", "sla_hours", "escalated_to_role", "priority",
        }, "this is the production shape that makes the 062000 INSERT illegal as originally written");

        int managerEmployeeId;
        int subjectEmployeeId;
        Guid routedChangeRequestId;
        Guid unroutedChangeRequestId;
        Guid routedApprovalRequestId;

        await using (var db = CreateDb())
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Replay", Slug = $"replay-{tenantId:N}"[..20] });
            await db.SaveChangesAsync();

            var manager = NewEmployee(tenantId, companyId, "MGR-1", null);
            db.Employees.Add(manager);
            await db.SaveChangesAsync();
            managerEmployeeId = manager.Id;

            var subject = NewEmployee(tenantId, companyId, "EMP-1", managerEmployeeId);
            db.Employees.Add(subject);
            await db.SaveChangesAsync();
            subjectEmployeeId = subject.Id;

            // Production already has the EMPLOYEE-CHANGE workflow and a step for this tenant, so
            // the migration's two workflow INSERTs must no-op.
            var workflow = new ApprovalWorkflow
            {
                TenantId = tenantId,
                Code = "EMPLOYEE-CHANGE",
                Name = "Employee Master Change Approval",
                EntityName = "EmployeeChangeRequest",
                IsActive = true,
            };
            db.ApprovalWorkflows.Add(workflow);
            await db.SaveChangesAsync();

            db.ApprovalWorkflowSteps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId,
                WorkflowId = workflow.Id,
                StepOrder = 1,
                StepName = "HR Manager Approval",
                ApproverRole = "HR Manager",
                ApproverType = "Role",
                IsFinalStep = true,
            });

            // (a) A pending change the application HAS already routed — 8 of these live in
            //     production, on Role:HR Manager with a 48-hour SLA.
            var routedApproval = new ApprovalRequest
            {
                TenantId = tenantId,
                WorkflowId = workflow.Id,
                EntityName = "EmployeeChangeRequest",
                Title = "Already routed",
                Status = "Pending",
                CurrentStepOrder = 1,
                CurrentApproverType = "Role",
                CurrentApproverRole = "HR Manager",
                CurrentQueue = LiveQueue,
                SlaHours = LiveSlaHours,
                Priority = "High",
                DueAtUtc = DateTime.UtcNow.AddHours(LiveSlaHours),
            };
            var routedChange = new EmployeeChangeRequest
            {
                TenantId = tenantId,
                EmployeeId = subjectEmployeeId,
                Status = "PendingApproval",
                EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
            };
            routedApproval.EntityId = routedChange.Id.ToString();
            routedChange.ApprovalRequestId = routedApproval.Id;
            db.ApprovalRequests.Add(routedApproval);
            db.EmployeeChangeRequests.Add(routedChange);

            // (b) The residual: 1 of 9 pending change requests with a NULL approval_request_id.
            //     This is the row the backfill exists for.
            var unroutedChange = new EmployeeChangeRequest
            {
                TenantId = tenantId,
                EmployeeId = subjectEmployeeId,
                Status = "PendingApproval",
                EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
                ApprovalRequestId = null,
            };
            db.EmployeeChangeRequests.Add(unroutedChange);

            await db.SaveChangesAsync();

            routedChangeRequestId = routedChange.Id;
            routedApprovalRequestId = routedApproval.Id;
            unroutedChangeRequestId = unroutedChange.Id;
        }

        // ── The next `--migrate` ──────────────────────────────────────────────────────────────
        // As shipped in 15148d0 this threw 23502 (not_null_violation) on current_approver_name and
        // took the whole pre-deploy migration step with it.
        await using (var db = CreateDb())
        {
            var replay = async () => await db.Database.MigrateAsync();
            await replay.Should().NotThrowAsync(
                "the two unapplied July migrations must be able to run against production's schema");
        }

        await using (var verify = CreateDb())
        {
            // Both are now recorded, so a later deploy will not try again.
            var applied = await AppliedMigrationsAsync();
            applied.Should().Contain(BackfillMigration).And.Contain(QueueMigration);
            applied.Should().Contain(SalaryStructureMigration,
                "it was already applied and is not disturbed by any of this");

            // The residual row is repaired ...
            var repaired = await verify.EmployeeChangeRequests
                .SingleAsync(x => x.Id == unroutedChangeRequestId);
            repaired.ApprovalRequestId.Should().NotBeNull("the backfill exists to fix exactly this row");

            // ... and routed by 20260713073000, which can only reach it because it was unrouted.
            var created = await verify.ApprovalRequests
                .SingleAsync(x => x.Id == repaired.ApprovalRequestId!.Value);
            created.RequestedForEmployeeId.Should().Be(subjectEmployeeId);
            created.CompanyId.Should().Be(companyId);
            created.CurrentApproverEmployeeId.Should().Be(managerEmployeeId);
            created.CurrentQueue.Should().StartWith("Manager:");

            // THE REGRESSION. 20260713073000's routing UPDATE assigns current_approver_*,
            // current_queue and sla_hours unconditionally. Without a guard it would have re-derived
            // this row from July-era employee data: moved it off the HR Manager queue onto the
            // individual manager and halved its SLA from 48h to 24h — silently changing who must
            // act on a pilot client's live approvals, with no audit row.
            var untouched = await verify.ApprovalRequests.SingleAsync(x => x.Id == routedApprovalRequestId);
            untouched.CurrentQueue.Should().Be(LiveQueue,
                "a backfill must not re-route an approval the running application has already routed");
            untouched.SlaHours.Should().Be(LiveSlaHours, "nor silently halve its SLA");
            untouched.CurrentApproverType.Should().Be("Role");
            untouched.CurrentApproverEmployeeId.Should().BeNull();

            var stillLinked = await verify.EmployeeChangeRequests.SingleAsync(x => x.Id == routedChangeRequestId);
            stillLinked.ApprovalRequestId.Should().Be(routedApprovalRequestId);

            // One workflow and one step: the migration's INSERTs are NOT EXISTS-guarded and must
            // not duplicate the ones production already has.
            (await verify.ApprovalWorkflows.CountAsync(w => w.TenantId == tenantId && w.Code == "EMPLOYEE-CHANGE"))
                .Should().Be(1);
            (await verify.ApprovalWorkflowSteps.CountAsync(s => s.TenantId == tenantId)).Should().Be(1);
        }

        // The ordering repair must leave the schema exactly as it found it, or the next model-parity
        // check would see drift that this migration introduced.
        (await ColumnsThatAreNotNullWithoutDefaultAsync()).Should().Contain(new[]
        {
            "current_approver_name", "current_approver_role", "current_approver_type",
            "current_queue", "sla_hours", "escalated_to_role", "priority",
        }, "062000 sets defaults only for the duration of its INSERT and must drop them again");
    }

    /// <summary>
    /// A second `--migrate` — a redeploy, or a Render OOM restart — must be a no-op, not a
    /// second backfill.
    /// </summary>
    [Fact]
    public async Task Replay_IsIdempotent_ASecondMigrateChangesNothing()
    {
        await using (var db = CreateDb())
            await db.Database.MigrateAsync();

        await WithdrawFromHistoryAsync(BackfillMigration, QueueMigration);

        // Seed a row for the backfill to find. Without this the INSERT selects nothing, no
        // constraint is evaluated and the test passes on an empty set — proving only that
        // migrating twice does not crash, which is not what is being claimed here.
        await SeedOnePendingChangeAwaitingBackfillAsync();

        await using (var db = CreateDb())
            await db.Database.MigrateAsync();

        var afterFirst = await CountAsync("SELECT COUNT(*)::int FROM approval_requests");
        afterFirst.Should().Be(1, "the first replay must actually create the backfilled approval request");

        // Withdraw and replay once more: the guards (NOT EXISTS, approval_request_id IS NULL,
        // current_queue = '') must make the second pass write nothing.
        await WithdrawFromHistoryAsync(BackfillMigration, QueueMigration);
        await using (var db = CreateDb())
        {
            var replay = async () => await db.Database.MigrateAsync();
            await replay.Should().NotThrowAsync();
        }

        (await CountAsync("SELECT COUNT(*)::int FROM approval_requests")).Should().Be(afterFirst,
            "re-running the backfill must not create a second approval request for the same change");
        (await CountAsync("SELECT COUNT(*)::int FROM approval_workflows WHERE code = 'EMPLOYEE-CHANGE'"))
            .Should().Be(1, "nor a second EMPLOYEE-CHANGE workflow");
        (await CountAsync("SELECT COUNT(*)::int FROM approval_workflow_steps")).Should().Be(1);
    }

    /// <summary>
    /// One tenant, one employee, one PendingApproval change request with a NULL
    /// approval_request_id — production's residual, and the only row either migration should touch.
    /// No EMPLOYEE-CHANGE workflow yet, so the migration has to create it.
    /// </summary>
    private async Task SeedOnePendingChangeAwaitingBackfillAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Idem", Slug = $"idem-{tenantId:N}"[..20] });
        await db.SaveChangesAsync();

        var employee = NewEmployee(tenantId, Guid.NewGuid(), "EMP-IDEM", null);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeChangeRequests.Add(new EmployeeChangeRequest
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            Status = "PendingApproval",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
            ApprovalRequestId = null,
        });
        await db.SaveChangesAsync();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static Employee NewEmployee(Guid tenantId, Guid companyId, string code, int? managerId) => new()
    {
        TenantId = tenantId,
        CompanyId = companyId,
        EmployeeCode = code,
        FullName = code,
        EnglishName = code,
        Designation = "Line Manager",
        Status = EmployeeStatuses.Active,
        JoiningDate = DateTime.UtcNow.AddDays(-90),
        ManagerEmployeeId = managerId,
    };

    /// <summary>
    /// Remove migrations from <c>__EFMigrationsHistory</c> so the next MigrateAsync replays them,
    /// reproducing "EF could not see this migration when the database was last updated".
    /// </summary>
    private async Task WithdrawFromHistoryAsync(params string[] migrationIds)
    {
        await using var db = CreateDb();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = ANY(@ids)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@ids";
        p.Value = migrationIds;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> AppliedMigrationsAsync()
    {
        await using var db = CreateDb();
        return (await db.Database.GetAppliedMigrationsAsync()).ToList();
    }

    private async Task<List<string>> ColumnsThatAreNotNullWithoutDefaultAsync()
    {
        await using var db = CreateDb();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText =
            "SELECT column_name FROM information_schema.columns " +
            "WHERE table_name = 'approval_requests' AND is_nullable = 'NO' AND column_default IS NULL";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var db = CreateDb();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
