using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The attendance policies API behind Setup -> Attendance rules. Attendance processing has always read these rows
/// (grace, early-exit, half-day, standard hours, break) but nothing could edit them; the old setup studio created one.
/// </summary>
public class AttendancePoliciesApiTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AttendancePoliciesController Controller(ZayraDbContext db, Guid tenantId, string role = "HR Manager") =>
        new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], "test"))
                }
            }
        };

    private static AttendancePolicyRequest Req(string code = "STD", string name = "Standard", Guid? branch = null, Guid? dept = null, Guid? grade = null,
        int grace = 10, int early = 15, int half = 240, int std = 480, int brk = 60) =>
        new(code, name, branch, dept, grade, grace, early, half, std, brk);

    private static AttendancePolicyDto Created(ActionResult<AttendancePolicyDto> result)
    {
        var created = Assert.IsType<CreatedResult>(result.Result);
        return Assert.IsType<AttendancePolicyDto>(created.Value);
    }

    private static async Task<AttendancePolicyDto> SeedCompanyWide(AttendancePoliciesController ctrl, string code = "DEFAULT") =>
        Created(await ctrl.Create(Req(code, "Company-wide"), CancellationToken.None));

    [Fact]
    public async Task List_ShowsOnlyTheCallersTenant_AndSeedsTheDefaultPolicyWhenTheTenantHasNone()
    {
        await using var db = CreateDb();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = b, Code = "OTHER", Name = "Other tenant" });
        await db.SaveChangesAsync();

        var list = Assert.IsAssignableFrom<IReadOnlyCollection<AttendancePolicyDto>>(Assert.IsType<OkObjectResult>((await Controller(db, a).List(CancellationToken.None)).Result).Value);

        var only = Assert.Single(list);
        Assert.Equal("DEFAULT", only.Code);
        Assert.True(only.IsActive);
        Assert.Single(await db.AttendancePolicies.Where(x => x.TenantId == a).ToListAsync());
        Assert.Single(await db.AttendancePolicies.Where(x => x.TenantId == b).ToListAsync());
    }

    [Fact]
    public async Task Create_StoresTheRulesNormalisesTheCode_AndWritesAnAuditEvent()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        await SeedCompanyWide(ctrl);
        var branch = new Branch { TenantId = tenantId, CompanyId = Guid.NewGuid(), Code = "RYD", NameEn = "Riyadh" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        var dto = Created(await ctrl.Create(Req(" ryd-day ", "  Riyadh day  ", branch: branch.Id, grace: 5, early: 10, half: 200, std: 450, brk: 45), CancellationToken.None));

        Assert.Equal("RYD-DAY", dto.Code);
        Assert.Equal("Riyadh day", dto.Name);
        Assert.Equal((5, 10, 200, 450, 45), (dto.GraceMinutes, dto.EarlyExitThresholdMinutes, dto.HalfDayThresholdMinutes, dto.StandardWorkMinutes, dto.BreakMinutes));
        Assert.Equal(branch.Id, dto.BranchId);
        Assert.Contains(db.AuditLogs, x => x.TenantId == tenantId && x.Action == "attendance.policy_created" && x.EntityId == dto.Id.ToString());
    }

    [Fact]
    public async Task Create_RefusesACodeAlreadyUsedInTheTenant_IgnoringCase_EvenWhenInactive()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        await SeedCompanyWide(ctrl);
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "NIGHT", Name = "Night", IsActive = false, DepartmentId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        Assert.IsType<ConflictObjectResult>((await ctrl.Create(Req("night", "Night again"), CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData("", "Name", 10, 15, 240, 480, 60)]          // no code
    [InlineData("A B", "Name", 10, 15, 240, 480, 60)]       // code with a space
    [InlineData("OK", " ", 10, 15, 240, 480, 60)]           // no name
    [InlineData("OK", "Name", -1, 15, 240, 480, 60)]        // negative grace
    [InlineData("OK", "Name", 121, 15, 240, 480, 60)]       // grace above two hours
    [InlineData("OK", "Name", 10, 241, 240, 480, 60)]       // early exit above four hours
    [InlineData("OK", "Name", 10, 15, 0, 480, 60)]          // half-day threshold of zero
    [InlineData("OK", "Name", 10, 15, 500, 480, 60)]        // half-day threshold above the working day
    [InlineData("OK", "Name", 10, 15, 240, 59, 60)]         // working day under an hour
    [InlineData("OK", "Name", 10, 15, 240, 961, 60)]        // working day over sixteen hours
    [InlineData("OK", "Name", 10, 15, 240, 480, 480)]       // break as long as the working day
    [InlineData("OK", "Name", 10, 15, 240, 480, -5)]        // negative break
    public async Task Create_RefusesValuesTheProcessorCouldNotApplySensibly(string code, string name, int grace, int early, int half, int std, int brk)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        await SeedCompanyWide(ctrl);

        Assert.IsType<BadRequestObjectResult>((await ctrl.Create(Req(code, name, grace: grace, early: early, half: half, std: std, brk: brk), CancellationToken.None)).Result);
        Assert.Single(await db.AttendancePolicies.Where(x => x.TenantId == tenantId).ToListAsync());
    }

    [Fact]
    public async Task Create_RefusesABranchDepartmentOrGradeFromAnotherTenantOrThatDoesNotExist()
    {
        await using var db = CreateDb();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var ctrl = Controller(db, a);
        await SeedCompanyWide(ctrl);
        var foreignBranch = new Branch { TenantId = b, CompanyId = Guid.NewGuid(), Code = "X", NameEn = "Foreign" };
        var foreignDept = new Department { TenantId = b, Code = "D", NameEn = "Foreign dept" };
        var foreignGrade = new Grade { TenantId = b, Code = "G", Name = "Foreign grade" };
        var deletedGrade = new Grade { TenantId = a, Code = "OLD", Name = "Deleted", IsDeleted = true };
        db.AddRange(foreignBranch, foreignDept, foreignGrade, deletedGrade);
        await db.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>((await ctrl.Create(Req("P1", "P1", branch: foreignBranch.Id), CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await ctrl.Create(Req("P2", "P2", dept: foreignDept.Id), CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await ctrl.Create(Req("P3", "P3", grade: foreignGrade.Id), CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await ctrl.Create(Req("P4", "P4", grade: deletedGrade.Id), CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await ctrl.Create(Req("P5", "P5", branch: Guid.NewGuid()), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Create_RefusesASecondActivePolicyForTheSameScope_BecauseTheProcessorWouldPickByCode()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        await SeedCompanyWide(ctrl);
        var grade = new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1" };
        db.Grades.Add(grade);
        await db.SaveChangesAsync();
        Created(await ctrl.Create(Req("GR1", "Grade one", grade: grade.Id), CancellationToken.None));

        Assert.IsType<ConflictObjectResult>((await ctrl.Create(Req("GR1B", "Grade one too", grade: grade.Id), CancellationToken.None)).Result);
        Assert.IsType<ConflictObjectResult>((await ctrl.Create(Req("ALL2", "Another company-wide"), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Create_ARestrictedPolicyNeedsAnActiveCompanyWidePolicyToFallBackOn()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var grade = new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1" };
        db.Grades.Add(grade);
        await db.SaveChangesAsync();

        // No policy at all yet: the processor would then treat the restricted policy as everyone's.
        var refused = Assert.IsType<ConflictObjectResult>((await Controller(db, tenantId).Create(Req("GR1", "Grade one", grade: grade.Id), CancellationToken.None)).Result);
        Assert.Contains("company-wide", refused.Value!.ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_ChangesTheRules_KeepsTheCode_AndNeverTouchesDaysAlreadyProcessed()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        var policy = await SeedCompanyWide(ctrl);
        var record = new AttendanceDailyRecord { TenantId = tenantId, EmployeeId = 1, EmployeeName = "A", WorkDate = new DateOnly(2026, 10, 1), LateMinutes = 12, EarlyExitMinutes = 3, Status = "Late" };
        db.AttendanceDailyRecords.Add(record);
        await db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>((await ctrl.Update(policy.Id, Req("DEFAULT", "Company-wide (revised)", grace: 30, early: 45), CancellationToken.None)).Result);

        var dto = Assert.IsType<AttendancePolicyDto>(ok.Value);
        Assert.Equal((30, 45, "Company-wide (revised)"), (dto.GraceMinutes, dto.EarlyExitThresholdMinutes, dto.Name));
        var reloaded = await db.AttendanceDailyRecords.AsNoTracking().SingleAsync(x => x.Id == record.Id);
        Assert.Equal((12, 3), (reloaded.LateMinutes, reloaded.EarlyExitMinutes));
        Assert.Contains(db.AuditLogs, x => x.Action == "attendance.policy_updated" && x.EntityId == policy.Id.ToString());
    }

    [Fact]
    public async Task Update_RefusesToRenameTheCode_BecauseEmployeesAreAssignedByCode()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        var policy = await SeedCompanyWide(ctrl);

        Assert.IsType<BadRequestObjectResult>((await ctrl.Update(policy.Id, Req("RENAMED", "Company-wide"), CancellationToken.None)).Result);
        Assert.Equal("DEFAULT", (await db.AttendancePolicies.SingleAsync(x => x.Id == policy.Id)).Code);
    }

    [Fact]
    public async Task Update_AndGet_TreatAnotherTenantsPolicyAsNotFound()
    {
        await using var db = CreateDb();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var foreign = await SeedCompanyWide(Controller(db, b));
        var ctrl = Controller(db, a);

        Assert.IsType<NotFoundResult>((await ctrl.Update(foreign.Id, Req("DEFAULT", "Hijack", grace: 120), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await ctrl.Get(foreign.Id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>(await ctrl.Deactivate(foreign.Id, CancellationToken.None));
        Assert.Equal(10, (await db.AttendancePolicies.AsNoTracking().SingleAsync(x => x.Id == foreign.Id)).GraceMinutes);
    }

    [Fact]
    public async Task Deactivate_RefusesTheLastActivePolicy_AndTheLastCompanyWideOne()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        var companyWide = await SeedCompanyWide(ctrl);
        Assert.IsType<ConflictObjectResult>(await ctrl.Deactivate(companyWide.Id, CancellationToken.None));

        var grade = new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1" };
        db.Grades.Add(grade);
        await db.SaveChangesAsync();
        var scoped = Created(await ctrl.Create(Req("GR1", "Grade one", grade: grade.Id), CancellationToken.None));

        // Two active policies, but only one is company-wide: it is the fallback for everyone else.
        Assert.IsType<ConflictObjectResult>(await ctrl.Deactivate(companyWide.Id, CancellationToken.None));
        Assert.True((await db.AttendancePolicies.AsNoTracking().SingleAsync(x => x.Id == companyWide.Id)).IsActive);

        Assert.IsType<OkObjectResult>(await ctrl.Deactivate(scoped.Id, CancellationToken.None));
        Assert.False((await db.AttendancePolicies.AsNoTracking().SingleAsync(x => x.Id == scoped.Id)).IsActive);
        Assert.Contains(db.AuditLogs, x => x.Action == "attendance.policy_deactivated" && x.EntityId == scoped.Id.ToString());
    }

    [Fact]
    public async Task Reactivate_RestoresThePolicy_UnlessItsScopeIsNowCoveredByAnotherActiveOne()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = Controller(db, tenantId);
        await SeedCompanyWide(ctrl);
        var grade = new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1" };
        db.Grades.Add(grade);
        await db.SaveChangesAsync();
        var first = Created(await ctrl.Create(Req("GR1", "Grade one", grade: grade.Id), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await ctrl.Deactivate(first.Id, CancellationToken.None));
        var second = Created(await ctrl.Create(Req("GR1B", "Grade one, newer", grade: grade.Id), CancellationToken.None));

        Assert.IsType<ConflictObjectResult>(await ctrl.Reactivate(first.Id, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await ctrl.Deactivate(second.Id, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await ctrl.Reactivate(first.Id, CancellationToken.None));
        Assert.Contains(db.AuditLogs, x => x.Action == "attendance.policy_reactivated" && x.EntityId == first.Id.ToString());
    }

    [Fact]
    public void EveryEndpointNeedsTheDedicatedPolicyKey_NotTheBroadAttendanceKeys()
    {
        // attendance.read / attendance.write are held by HR Officer, Payroll Manager, Supervisor and others; these rules
        // change who is late and what payroll deducts, so reads and writes both need attendance.policy_manage.
        var type = typeof(AttendancePoliciesController);
        Assert.Contains("Admin,HR Manager", type.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Roles).Where(r => r is not null));
        string[] actions = [nameof(AttendancePoliciesController.List), nameof(AttendancePoliciesController.Get), nameof(AttendancePoliciesController.Create),
            nameof(AttendancePoliciesController.Update), nameof(AttendancePoliciesController.Deactivate), nameof(AttendancePoliciesController.Reactivate)];
        foreach (var name in actions)
            Assert.Equal(["attendance.policy_manage"], type.GetMethod(name)!.GetCustomAttribute<HasPermissionAttribute>()!.Permissions);
    }

    [Fact]
    public async Task OnlyAdminHrManagerAndHrDirectorHoldThePolicyKey()
    {
        var bundles = (await Security.LegacyRoleGateBypassSweepTests.CallerBundlesAsync()).Where(b => b.Label == b.Role).ToList();
        var holders = bundles.Where(b => b.Permissions.Contains("attendance.policy_manage")).Select(b => b.Role).OrderBy(r => r).ToList();
        Assert.Equal(["Admin", "HR Director", "HR Manager"], holders);
    }
}
