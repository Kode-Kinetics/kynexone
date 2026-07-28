using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Real-Postgres establishment concurrency tests (design plan
/// EstablishmentConcurrencyIntegrationTests, AC7): two parallel hires racing for the last
/// budgeted slot serialize on pg_advisory_xact_lock — exactly one wins, the loser gets the typed
/// exception and its block audit survives the rollback (written on a fresh connection). Also
/// exercises the guard under EnableRetryOnFailure (consultant R-D: a bare BeginTransaction throws
/// under a retrying execution strategy; the guard must wrap the whole unit in the strategy).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class EstablishmentConcurrencyIntegrationTests
{
    private readonly PostgresFixture _fx;
    public EstablishmentConcurrencyIntegrationTests(PostgresFixture fx) => _fx = fx;

    private ZayraDbContext CreateRetryingDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, o => o.EnableRetryOnFailure(maxRetryCount: 3))
            .Options);

    private sealed record Org(Guid TenantId, Guid DeptId, string DeptName, Guid LevelId, Guid DesignationId);

    private async Task<Org> SeedOrg(int budget)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var dept = new Department { TenantId = tenantId, Code = $"OPS{Guid.NewGuid():N}"[..12], NameEn = $"Operations-{tenantId:N}"[..20], IsActive = true };
        var level = new StaffingLevel { TenantId = tenantId, Code = "MGR", NameEn = "Manager", NameAr = "مدير", Rank = 2 };
        var desig = new Designation { TenantId = tenantId, Code = $"MGR{Guid.NewGuid():N}"[..12], TitleEn = "Operations Manager", IsActive = true, StaffingLevelId = level.Id };
        db.AddRange(dept, level, desig);
        db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget
        {
            TenantId = tenantId, DepartmentId = dept.Id, StaffingLevelId = level.Id, BudgetedHeadcount = budget
        });
        await db.SaveChangesAsync();
        return new Org(tenantId, dept.Id, dept.NameEn, level.Id, desig.Id);
    }

    private static Employee NewOccupant(Org org, string code) => new()
    {
        TenantId = org.TenantId,
        EmployeeCode = code,
        FullName = $"Racer {code}",
        Status = "Active",
        JoiningDate = DateTime.UtcNow,
        DepartmentId = org.DeptId,
        Department = org.DeptName,
        DesignationId = org.DesignationId
    };

    [Fact]
    public async Task TwoParallelHires_ForTheLastSlot_ExactlyOneWins_LoserGets409Payload()
    {
        var org = await SeedOrg(budget: 1);

        async Task<Exception?> Hire(string code)
        {
            // Each contender gets its own context + guard, exactly like two concurrent requests.
            await using var db = _fx.CreateDb();
            var guard = new EstablishmentGuardService(db);
            try
            {
                await guard.EnforceAndExecuteAsync<bool>(org.TenantId, org.DeptId, org.DesignationId,
                    excludeEmployeeId: null, path: "create", new RequestContext("t", "t", Guid.NewGuid(), org.TenantId),
                    async () =>
                    {
                        db.Employees.Add(NewOccupant(org, code));
                        await db.SaveChangesAsync();
                        return true;
                    });
                return null;
            }
            catch (Exception ex) { return ex; }
        }

        var results = await Task.WhenAll(Hire($"A-{Guid.NewGuid():N}"[..12]), Hire($"B-{Guid.NewGuid():N}"[..12]));

        var failures = results.Where(r => r is not null).ToList();
        failures.Should().HaveCount(1, "the advisory lock serializes the two writers; the second re-counts and loses (AC7)");
        var blockEx = failures[0].Should().BeOfType<EstablishmentBudgetExceededException>().Subject;
        blockEx.Block.Budgeted.Should().Be(1);
        blockEx.Block.Current.Should().Be(1);
        blockEx.Block.LevelNameAr.Should().Be("مدير");

        await using var verify = _fx.CreateDb();
        (await EstablishmentOccupancy.Occupying(verify.Employees.IgnoreQueryFilters().AsNoTracking(), org.TenantId)
                .CountAsync(e => e.DepartmentId == org.DeptId))
            .Should().Be(1, "exactly one hire may be persisted");

        // The loser's block audit survived its transaction rollback (fresh-connection write).
        (await verify.AuditLogs.CountAsync(a => a.TenantId == org.TenantId && a.Action == "establishment.assignment_blocked"))
            .Should().Be(1, "the compliance record of the denied assignment must exist despite the rollback");
    }

    [Fact]
    public async Task Guard_UnderEnableRetryOnFailure_ExecutesTransactionally_AndBlocksWithTypedException()
    {
        // Program.cs registers the DbContext with EnableRetryOnFailure; a bare BeginTransaction
        // throws InvalidOperationException under that strategy. The guard must keep working —
        // both the allowed path (persists) and the blocked path (typed exception, nothing saved).
        var org = await SeedOrg(budget: 1);

        await using (var db = CreateRetryingDb())
        {
            var guard = new EstablishmentGuardService(db);
            await guard.EnforceAndExecuteAsync<bool>(org.TenantId, org.DeptId, org.DesignationId,
                null, "create", new RequestContext("t", "t", Guid.NewGuid(), org.TenantId), async () =>
                {
                    db.Employees.Add(NewOccupant(org, $"R1-{Guid.NewGuid():N}"[..12]));
                    await db.SaveChangesAsync();
                    return true;
                });
        }

        await using (var db = CreateRetryingDb())
        {
            var guard = new EstablishmentGuardService(db);
            var second = () => guard.EnforceAndExecuteAsync<bool>(org.TenantId, org.DeptId, org.DesignationId,
                null, "create", new RequestContext("t", "t", Guid.NewGuid(), org.TenantId), async () =>
                {
                    db.Employees.Add(NewOccupant(org, $"R2-{Guid.NewGuid():N}"[..12]));
                    await db.SaveChangesAsync();
                    return true;
                });
            await second.Should().ThrowAsync<EstablishmentBudgetExceededException>();
        }

        await using var verify = _fx.CreateDb();
        (await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == org.TenantId))
            .Should().Be(1, "the blocked hire's transaction rolled back cleanly under the retry strategy");
    }

    [Fact]
    public async Task BudgetEdit_AndHire_SerializeOnTheSameCellLock()
    {
        // A hire and a concurrent budget write for the same (dept, level) cell must serialize on
        // the same advisory-lock key so neither ever reads a half-written state. We assert the
        // lock key is stable and cross-context, and that a hire inside a held lock waits.
        var org = await SeedOrg(budget: 5);
        var key1 = EstablishmentGuardService.ComputeLockKey(org.TenantId, org.DeptId, org.LevelId);
        var key2 = EstablishmentGuardService.ComputeLockKey(org.TenantId, org.DeptId, org.LevelId);
        key1.Should().Be(key2, "lock keys must be deterministic across processes");

        await using var holder = _fx.CreateDb();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await new EstablishmentGuardService(holder).AcquireSlotLockAsync(org.TenantId, org.DeptId, org.LevelId);

        var hire = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            await new EstablishmentGuardService(db).EnforceAndExecuteAsync<bool>(org.TenantId, org.DeptId, org.DesignationId,
                null, "create", new RequestContext("t", "t", Guid.NewGuid(), org.TenantId), async () =>
                {
                    db.Employees.Add(NewOccupant(org, $"W-{Guid.NewGuid():N}"[..12]));
                    await db.SaveChangesAsync();
                    return true;
                });
        });

        var finishedWhileHeld = await Task.WhenAny(hire, Task.Delay(1500)) == hire;
        finishedWhileHeld.Should().BeFalse("the hire must wait while another writer holds the cell lock");

        await tx.RollbackAsync(); // releases the advisory lock
        await hire; // now completes
        await using var verify = _fx.CreateDb();
        (await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == org.TenantId)).Should().Be(1);
    }
}
