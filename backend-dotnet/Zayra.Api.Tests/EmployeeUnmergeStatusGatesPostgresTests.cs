using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Unmerge puts the record back to the status it had before the merge ONLY when the same gates as a normal status change pass (the
/// activation/readiness gate, the establishment seat check, the tenant's active-employee limit). Otherwise the record comes back as
/// Draft with a note saying why. Whatever the status, the login the merge closed stays closed and has to be re-issued. Also the
/// merge's early refusals (before any finance lock) and the footprint-row timing rule for the salary-structure restore.
/// </summary>
public sealed partial class EmployeeMergeOwnedRecordsPostgresTests
{
    private async Task<IActionResult> UnmergeAsync(Twins t)
    {
        await using var db = _fx.CreateDb();
        return await Controller(db, t).ResolveDuplicate(t.Duplicate.Id, new ResolveDuplicateRequest("unmerge", null, "wrong twins"), CancellationToken.None);
    }

    private static JsonElement Json(IActionResult result)
    {
        var ok = Assert.IsAssignableFrom<ObjectResult>(result);
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement.Clone();
    }

    private async Task<string> StatusAsync(int id)
    {
        await using var db = _fx.CreateDb();
        return await db.Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.Status).SingleAsync();
    }

    /// <summary>A Saudi-jurisdiction expatriate twin: complete (has the residence permit the activation gate needs) or not.</summary>
    private async Task MakeActivationFixtureAsync(Twins t, bool complete)
    {
        await using var db = _fx.CreateDb();
        var dup = await db.Employees.SingleAsync(e => e.Id == t.Duplicate.Id);
        dup.CountryCode = "SA"; dup.Nationality = "Indian";
        if (complete) { dup.IqamaNumber = "2000000001"; dup.GosiReference = "GOSI-1"; }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Unmerge_RestoresThePreMergeStatus_WhenTheStatusGatesPass()
    {
        var t = await SeedTwinsAsync();
        await MakeActivationFixtureAsync(t, complete: true);
        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        var body = Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Active");
        body.GetProperty("status").GetString().Should().Be("Active");
        body.GetProperty("statusRestored").GetBoolean().Should().BeTrue();
        await using var read = _fx.CreateDb();
        var rows = await read.EmployeeStatusHistories.IgnoreQueryFilters().Where(h => h.EmployeeId == t.Duplicate.Id).ToListAsync();
        rows.Should().Contain(h => h.OldStatus == "Draft" && h.NewStatus == "Active", "the return to Active goes through the normal status change and is recorded by it");
    }

    [Fact]
    public async Task Unmerge_ComesBackAsDraft_WithTheReason_WhenTheActivationGateBlocks()
    {
        var t = await SeedTwinsAsync();
        await MakeActivationFixtureAsync(t, complete: false);   // a Saudi-jurisdiction expatriate with no residence permit cannot be activated
        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        var body = Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Draft");
        body.GetProperty("status").GetString().Should().Be("Draft");
        body.GetProperty("requestedStatus").GetString().Should().Be("Active");
        body.GetProperty("statusRestored").GetBoolean().Should().BeFalse();
        body.GetProperty("fallbackReason").GetString().Should().Be("activation_blocked");
        body.GetProperty("message").GetString().Should().Contain("Draft").And.Contain("activation");
        body.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
        await using var read = _fx.CreateDb();
        var note = await read.EmployeeStatusHistories.IgnoreQueryFilters().Where(h => h.EmployeeId == t.Duplicate.Id && h.NewStatus == "Draft").Select(h => h.Reason).SingleAsync();
        note.Should().Contain("Draft").And.Contain("activation");
        (await read.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == t.Duplicate.Id)).IsDeleted.Should().BeFalse("the record is restored either way");
    }

    [Fact]
    public async Task Unmerge_ComesBackAsDraft_WhenTheEstablishmentSeatIsTaken()
    {
        var t = await SeedTwinsAsync();
        await MakeActivationFixtureAsync(t, complete: true);
        await using (var seed = _fx.CreateDb())
        {
            var dept = new Department { TenantId = t.TenantId, Code = "OPS", NameEn = "Operations", IsActive = true };
            var level = new StaffingLevel { TenantId = t.TenantId, Code = "MGR", NameEn = "Manager", NameAr = "مدير", Rank = 2 };
            var desig = new Designation { TenantId = t.TenantId, Code = "OPS-MGR", TitleEn = "Operations Manager", IsActive = true, StaffingLevelId = level.Id };
            seed.AddRange(dept, level, desig);
            seed.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = t.TenantId, DepartmentId = dept.Id, StaffingLevelId = level.Id, BudgetedHeadcount = 1 });
            foreach (var id in new[] { t.Duplicate.Id, t.Survivor.Id })
            {
                var e = await seed.Employees.SingleAsync(x => x.Id == id);
                e.DepartmentId = dept.Id; e.Department = dept.NameEn; e.DesignationId = desig.Id; e.Designation = desig.TitleEn;
            }
            await seed.SaveChangesAsync();
        }
        // While the duplicate was merged away, its seat was needed: the survivor holds the only budgeted one. (Merge itself frees the duplicate's.)
        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        var body = Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Draft");
        body.GetProperty("fallbackReason").GetString().Should().Be("establishment_full");
        body.GetProperty("message").GetString().Should().Contain("seat");
        body.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
    }

    [Fact]
    public async Task Unmerge_ComesBackAsDraft_WhenThePlansActiveEmployeeLimitIsReached()
    {
        var t = await SeedTwinsAsync();
        await MakeActivationFixtureAsync(t, complete: true);
        await using (var seed = _fx.CreateDb())
        {
            seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = t.TenantId, Plan = "Starter", Status = "Active", MaxEmployees = 1 });
            await seed.SaveChangesAsync();
        }
        Assert.IsType<OkObjectResult>(await MergeAsync(t));   // the survivor is the one active employee the plan allows

        var body = Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Draft");
        body.GetProperty("fallbackReason").GetString().Should().Be("plan_limit");
        body.GetProperty("message").GetString().Should().Contain("plan");
        body.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
    }

    [Fact]
    public async Task Unmerge_RestoresActive_WhenThePlanStillHasRoom()
    {
        var t = await SeedTwinsAsync();
        await MakeActivationFixtureAsync(t, complete: true);
        await using (var seed = _fx.CreateDb())
        {
            seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = t.TenantId, Plan = "Starter", Status = "Active", MaxEmployees = 5 });
            await seed.SaveChangesAsync();
        }
        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        Json(await UnmergeAsync(t)).GetProperty("status").GetString().Should().Be("Active");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unmerge_NeverReopensTheLogin_AndSaysItMustBeReissued(bool gatesPass)
    {
        var t = await SeedTwinsAsync();
        await MakeActivationFixtureAsync(t, complete: gatesPass);
        var (userId, linkId, _) = await SeedLoginAsync(t);
        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        var body = Json(await UnmergeAsync(t));

        await using var read = _fx.CreateDb();
        (await read.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == userId)).IsActive.Should().BeFalse("unmerge never re-opens a login: that is a separate, privileged access decision");
        (await read.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.Id == linkId)).AccessMode.Should().Be(AccessModes.NoLogin);
        body.GetProperty("loginClosed").GetBoolean().Should().BeTrue();
        body.GetProperty("loginReissueRequired").GetBoolean().Should().BeTrue();
        body.GetProperty("message").GetString().Should().Contain("login stays closed");
        body.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
    }

    // ── Unmerge's salary-structure restore uses only the footprint row of THIS merge ───────────────

    /// <summary>Rows are saved oldest first: the audit chain never lets a row be older than the one before it (it moves it forward), so a
    /// footprint row that must be OLDER than the merge row (a merge from before the merge row was staged first) has to be saved first.</summary>
    private async Task SeedMergeRowsAsync(Twins t, DateTime mergeAt, DateTime? footprintAt, int salaryDeactivated, string? previousStatus = null, int profilesDeactivated = 0)
    {
        var merge = new AuditLog { TenantId = t.TenantId, Action = "employee.merged_into_existing", EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(),
            Metadata = JsonSerializer.Serialize(new { reason = "same person", previousStatus }), CreatedAtUtc = mergeAt };
        var rows = new List<AuditLog> { merge };
        if (footprintAt is { } at)
            rows.Add(new AuditLog
            {
                TenantId = t.TenantId, Action = "employee.payroll_footprint_deactivated", EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(), CreatedAtUtc = at,
                Metadata = JsonSerializer.Serialize(new { reason = "merged_duplicate", salaryDeactivated, profileDeactivated = profilesDeactivated }),
            });
        await using var seed = _fx.CreateDb();
        foreach (var row in rows.OrderBy(r => r.CreatedAtUtc))
        {
            seed.AuditLogs.Add(row);
            await seed.SaveChangesAsync();
        }
    }

    private async Task<bool> ClosedStructureIsActiveAfterUnmergeAsync(Twins t)
    {
        Assert.IsType<OkObjectResult>(await UnmergeAsync(t));
        await using var read = _fx.CreateDb();
        return (await read.EmployeeSalaryStructures.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == t.Duplicate.Id)).IsActive;
    }

    private async Task SeedClosedStructureAsync(Twins t)
    {
        await using var seed = _fx.CreateDb();
        seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 5000m, Currency = "SAR",
            EffectiveDate = new DateOnly(2021, 1, 1), IsActive = false,
        });
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Unmerge_IgnoresAFootprintRowOfAnEarlierCycleThatWasAlreadyUndone()
    {
        var t = await SeedTwinsAsync();
        await MarkMergedAsync(t);
        await SeedClosedStructureAsync(t);
        var now = DateTime.UtcNow;
        // An EARLIER merge-and-unmerge cycle left a footprint row behind (it deactivated one structure). The merge being undone now has its
        // own audit row, written later, and deactivated nothing, so it wrote no footprint row of its own.
        await using (var seed = _fx.CreateDb())
        {
            seed.AuditLogs.Add(new AuditLog { TenantId = t.TenantId, Action = "employee.payroll_footprint_deactivated", EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(), CreatedAtUtc = now.AddMinutes(-30),
                Metadata = JsonSerializer.Serialize(new { reason = "merged_duplicate", salaryDeactivated = 1, profileDeactivated = 0 }) });
            await seed.SaveChangesAsync();
        }
        await using (var seed = _fx.CreateDb())
        {
            seed.AuditLogs.Add(new AuditLog { TenantId = t.TenantId, Action = "employee.duplicate_merge_reversed", EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(), CreatedAtUtc = now.AddMinutes(-20),
                Metadata = JsonSerializer.Serialize(new { restoredStatus = "Draft" }) });
            await seed.SaveChangesAsync();
        }
        await SeedMergeRowsAsync(t, mergeAt: now.AddMinutes(-5), footprintAt: null, salaryDeactivated: 0, previousStatus: "Confirmed");

        (await ClosedStructureIsActiveAfterUnmergeAsync(t)).Should().BeFalse("the old cycle's footprint row is not this merge's: a structure closed long before must stay closed");
    }

    [Fact]
    public async Task Unmerge_UsesTheFootprintRowWrittenAtOrAfterTheMerge()
    {
        var t = await SeedTwinsAsync();
        await MarkMergedAsync(t);
        await SeedClosedStructureAsync(t);
        var now = DateTime.UtcNow;
        await SeedMergeRowsAsync(t, mergeAt: now.AddMinutes(-5), footprintAt: now.AddMinutes(-5).AddSeconds(1), salaryDeactivated: 1, previousStatus: "Confirmed");

        (await ClosedStructureIsActiveAfterUnmergeAsync(t)).Should().BeTrue("the merge deactivated it, so unmerge puts it back");
    }

    // ── A refused status change leaves a plain Draft; older merges still restore; the outcome decides statusRestored ──────────

    /// <summary>Stands in for the status service: ChangeStatusAsync returns null, or throws what it is told to; nothing else is called.</summary>
    public class StatusServiceStub : System.Reflection.DispatchProxy
    {
        public Exception? Throw { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IEmployeeManagementService.ChangeStatusAsync)) throw new NotSupportedException(targetMethod.Name);
            if (Throw is not null) throw Throw;
            return Task.FromResult<EmployeeDetailDto?>(null);
        }
    }

    private async Task<IActionResult> UnmergeWithStatusServiceAsync(Twins t, Exception? failure)
    {
        var stub = System.Reflection.DispatchProxy.Create<IEmployeeManagementService, StatusServiceStub>();
        ((StatusServiceStub)(object)stub).Throw = failure;
        await using var db = _fx.CreateDb();
        var controller = Controller(db, t);
        controller.HttpContext.RequestServices = new ServiceCollection().AddSingleton(stub).BuildServiceProvider();
        return await controller.ResolveDuplicate(t.Duplicate.Id, new ResolveDuplicateRequest("unmerge", null, "wrong twins"), CancellationToken.None);
    }

    /// <summary>The twin as a merge with a payroll footprint leaves it: a cleared WPS profile and a closed salary structure.</summary>
    private async Task SeedMergedPayrollFootprintAsync(Twins t, string previousStatus, DateTime mergeAt, DateTime footprintAt)
    {
        await MarkMergedAsync(t);
        await SeedClosedStructureAsync(t);
        await using (var seed = _fx.CreateDb())
        {
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, WpsEligible = false });
            await seed.SaveChangesAsync();
        }
        await SeedMergeRowsAsync(t, mergeAt, footprintAt, salaryDeactivated: 1, previousStatus: previousStatus, profilesDeactivated: 1);
    }

    private async Task<(bool Wps, bool SalaryActive)> PayrollStateAsync(Twins t)
    {
        await using var read = _fx.CreateDb();
        var wps = (await read.EmployeePayrollProfiles.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == t.Duplicate.Id)).WpsEligible;
        var salary = (await read.EmployeeSalaryStructures.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == t.Duplicate.Id)).IsActive;
        return (wps, salary);
    }

    [Fact]
    public async Task Unmerge_RefusedByAGate_LeavesWpsAndSalaryUnchanged()
    {
        var t = await SeedTwinsAsync();
        await MakeActivationFixtureAsync(t, complete: false);   // the activation gate will refuse Active
        var now = DateTime.UtcNow;
        await SeedMergedPayrollFootprintAsync(t, "Active", now.AddMinutes(-5), now.AddMinutes(-5).AddSeconds(1));

        var body = Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Draft");
        (await PayrollStateAsync(t)).Should().Be((false, false), "a plain Draft is not WPS-eligible and has no active salary structure");
        body.GetProperty("payrollRestored").GetBoolean().Should().BeFalse();
        body.GetProperty("message").GetString().Should().Contain("WPS").And.Contain("not restored");
        body.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
    }

    [Fact]
    public async Task Unmerge_ThatReachesAnEmployedStatus_RestoresWpsAndSalary()
    {
        var t = await SeedTwinsAsync();
        var now = DateTime.UtcNow;
        await SeedMergedPayrollFootprintAsync(t, "Confirmed", now.AddMinutes(-5), now.AddMinutes(-5).AddSeconds(1));

        var body = Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Confirmed");
        (await PayrollStateAsync(t)).Should().Be((true, true));
        body.GetProperty("payrollRestored").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Unmerge_ToAnExitStatus_RestoresSalaryButNeverWps()
    {
        var t = await SeedTwinsAsync();
        var now = DateTime.UtcNow;
        await SeedMergedPayrollFootprintAsync(t, "Terminated", now.AddMinutes(-5), now.AddMinutes(-5).AddSeconds(1));

        Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Terminated");
        (await PayrollStateAsync(t)).Should().Be((false, true), "the exit cascade's never-pay-again gate stays shut, but a settlement still needs the pay package");
    }

    [Fact]
    public async Task Unmerge_OfAMergeMadeBeforeTheFootprintOrdering_StillRestoresTheSalaryStructure()
    {
        var t = await SeedTwinsAsync();
        var now = DateTime.UtcNow;
        // Legacy order: the footprint row was staged BEFORE the merge row, so it is older than the merge row.
        await SeedMergedPayrollFootprintAsync(t, "Confirmed", mergeAt: now.AddMinutes(-5), footprintAt: now.AddMinutes(-5).AddSeconds(-1));

        Json(await UnmergeAsync(t));

        (await PayrollStateAsync(t)).SalaryActive.Should().BeTrue("the footprint is bounded by the last reversal, not by the merge row");
    }

    [Fact]
    public async Task Unmerge_StatusRestored_IsFalse_WhenChangeStatusReturnsNull()
    {
        var t = await SeedTwinsAsync();
        var now = DateTime.UtcNow;
        await SeedMergeRowsAsync(t, now.AddMinutes(-5), footprintAt: null, salaryDeactivated: 0, previousStatus: "Active");
        await MarkMergedAsync(t);

        var body = Json(await UnmergeWithStatusServiceAsync(t, failure: null));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Draft");
        body.GetProperty("statusRestored").GetBoolean().Should().BeFalse("the record is a Draft, not the Active it was asked to become");
        body.GetProperty("fallbackReason").GetString().Should().Be("status_change_refused");
        body.GetProperty("message").GetString().Should().Contain("Draft");
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    public async Task Unmerge_AnUnrelatedFailureInTheStatusChange_IsNotReportedAsARefusal(Type failure)
    {
        var t = await SeedTwinsAsync();
        var now = DateTime.UtcNow;
        await SeedMergeRowsAsync(t, now.AddMinutes(-5), footprintAt: null, salaryDeactivated: 0, previousStatus: "Active");
        await MarkMergedAsync(t);

        var act = () => UnmergeWithStatusServiceAsync(t, (Exception)Activator.CreateInstance(failure, "boom")!);

        await act.Should().ThrowAsync<Exception>().Where(e => e.GetType() == failure);
    }

    [Fact]
    public async Task Unmerge_AGateRefusalFromTheStatusChange_StillFallsBackToDraft()
    {
        var t = await SeedTwinsAsync();
        var now = DateTime.UtcNow;
        await SeedMergeRowsAsync(t, now.AddMinutes(-5), footprintAt: null, salaryDeactivated: 0, previousStatus: "Active");
        await MarkMergedAsync(t);

        var body = Json(await UnmergeWithStatusServiceAsync(t, new EmployeeStatusChangeRefusedException("open settlement")));

        body.GetProperty("fallbackReason").GetString().Should().Be("status_change_refused");
        body.GetProperty("statusRestored").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Unmerge_ToProbation_HitsThePlansEmployeeLimit()
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            var dup = await seed.Employees.SingleAsync(e => e.Id == t.Duplicate.Id);
            dup.Status = "Probation";
            seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = t.TenantId, Plan = "Starter", Status = "Active", MaxEmployees = 1 });
            await seed.SaveChangesAsync();
        }
        Assert.IsType<OkObjectResult>(await MergeAsync(t));   // the survivor is the one employed person the plan allows

        var body = Json(await UnmergeAsync(t));

        (await StatusAsync(t.Duplicate.Id)).Should().Be("Draft");
        body.GetProperty("fallbackReason").GetString().Should().Be("plan_limit");
        body.GetProperty("statusRestored").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Unmerge_ReadsTheMergedRowUnderALock_SoAConcurrentRestoreIsSeen()
    {
        var t = await SeedTwinsAsync();
        Assert.IsType<OkObjectResult>(await MergeAsync(t));
        var locked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var row = (await db.Employees.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag).Where(e => e.Id == t.Duplicate.Id).ToListAsync()).Single();
                // Another unmerge is half way through: the row is restored in this transaction, not yet committed.
                row.IsDeleted = false; row.DuplicateOfEmployeeId = null; row.Status = "Draft";
                await db.SaveChangesAsync();
                locked.TrySetResult();
                await release.Task;
                await tx.CommitAsync();
            });
        });
        await locked.Task;

        var unmerge = UnmergeAsync(t);
        var finished = await Task.WhenAny(unmerge, Task.Delay(2500));
        var waited = finished != unmerge;
        release.SetResult();
        await holder;
        var result = await unmerge;

        waited.Should().BeTrue("unmerge takes a row lock on the merged employee before it reads it");
        result.Should().BeOfType<NotFoundResult>("after the wait it sees the record already restored, instead of restoring it a second time");
        await using var read = _fx.CreateDb();
        (await read.EmployeeStatusHistories.IgnoreQueryFilters().CountAsync(h => h.EmployeeId == t.Duplicate.Id && h.Reason.StartsWith("Merge undone"))).Should().Be(0);
    }

    [Fact]
    public async Task Unmerge_TwoAtOnce_RestoreOnce()
    {
        var t = await SeedTwinsAsync();
        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        var results = await Task.WhenAll(UnmergeAsync(t), UnmergeAsync(t));

        results.Count(r => r is OkObjectResult).Should().Be(1);
        results.Count(r => r is NotFoundResult).Should().Be(1, "the second one finds the record already restored");
        await using var read = _fx.CreateDb();
        (await read.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "employee.duplicate_merge_reversed" && a.EntityId == t.Duplicate.Id.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Merge_WritesItsAuditRowBeforeItsFootprintRow_SoTheFootprintIsNeverOlderThanTheMerge()
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, WpsEligible = true });
            await seed.SaveChangesAsync();
        }
        Assert.IsType<OkObjectResult>(await MergeAsync(t));

        await using var read = _fx.CreateDb();
        var mergeAt = (await read.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.Action == "employee.merged_into_existing" && a.EntityId == t.Duplicate.Id.ToString())).CreatedAtUtc;
        var footprintAt = (await read.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.Action == "employee.payroll_footprint_deactivated" && a.EntityId == t.Duplicate.Id.ToString())).CreatedAtUtc;
        footprintAt.Should().BeOnOrAfter(mergeAt);
    }
}
