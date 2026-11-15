using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
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

    private async Task SeedMergeRowsAsync(Twins t, DateTime mergeAt, DateTime? footprintAt, int salaryDeactivated)
    {
        await using var seed = _fx.CreateDb();
        seed.AuditLogs.Add(new AuditLog { TenantId = t.TenantId, Action = "employee.merged_into_existing", EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(), Metadata = "{\"reason\":\"same person\"}", CreatedAtUtc = mergeAt });
        await seed.SaveChangesAsync();
        if (footprintAt is { } at)
        {
            seed.AuditLogs.Add(new AuditLog
            {
                TenantId = t.TenantId, Action = "employee.payroll_footprint_deactivated", EntityName = "Employee", EntityId = t.Duplicate.Id.ToString(), CreatedAtUtc = at,
                Metadata = JsonSerializer.Serialize(new { reason = "merged_duplicate", salaryDeactivated, profileDeactivated = 0 }),
            });
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
    public async Task Unmerge_IgnoresAFootprintRowThatIsOlderThanTheMergeItIsUndoing()
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
        await SeedMergeRowsAsync(t, mergeAt: now.AddMinutes(-5), footprintAt: null, salaryDeactivated: 0);

        (await ClosedStructureIsActiveAfterUnmergeAsync(t)).Should().BeFalse("the old cycle's footprint row is not this merge's: a structure closed long before must stay closed");
    }

    [Fact]
    public async Task Unmerge_UsesTheFootprintRowWrittenAtOrAfterTheMerge()
    {
        var t = await SeedTwinsAsync();
        await MarkMergedAsync(t);
        await SeedClosedStructureAsync(t);
        var now = DateTime.UtcNow;
        await SeedMergeRowsAsync(t, mergeAt: now.AddMinutes(-5), footprintAt: now.AddMinutes(-5).AddSeconds(1), salaryDeactivated: 1);

        (await ClosedStructureIsActiveAfterUnmergeAsync(t)).Should().BeTrue("the merge deactivated it, so unmerge puts it back");
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
