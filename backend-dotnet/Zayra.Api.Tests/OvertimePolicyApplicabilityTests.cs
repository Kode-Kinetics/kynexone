using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Security review H1. Payroll now pays overtime under the policy STORED on the request, and the
/// request used to store whatever policy id the caller sent (checked only for tenant + active). An
/// ess.write employee could therefore file under a generous policy meant for another grade or branch.
/// The policy is now resolved server-side from the employee's branch/department/grade; a client id is
/// ignored on the self-service door and accepted from overtime administration only if it applies.
/// </summary>
public class OvertimePolicyApplicabilityTests
{
    private static readonly string[] SelfService = ["ess.write"];
    private static readonly string[] Hr = ["overtime.write"];

    private sealed record Setup(OvertimeFixKit.World W, OvertimePolicy Standard, OvertimePolicy OtherGrade, OvertimePolicy OwnGrade, Guid MyGrade);

    private static async Task<Setup> SeedAsync()
    {
        var w = await OvertimeFixKit.SeedAsync();
        var myGrade = Guid.NewGuid();
        w.Subject.GradeId = myGrade;
        // The seeded unscoped policy is "Standard"; it is also the oldest, so it wins the default order.
        w.Policy.CreatedAtUtc = DateTime.UtcNow.AddDays(-30);
        var other = new OvertimePolicy
        {
            TenantId = w.TenantId, Code = "GEN", Name = "Generous - other grade", IsActive = true, RoundingRule = "None",
            MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 6000, GradeId = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-20),
        };
        var own = new OvertimePolicy
        {
            TenantId = w.TenantId, Code = "OWN", Name = "My grade", IsActive = true, RoundingRule = "None",
            MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 6000, GradeId = myGrade,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
        };
        w.Db.OvertimePolicies.AddRange(other, own);
        await w.Db.SaveChangesAsync();
        return new Setup(w, w.Policy, other, own, myGrade);
    }

    private static OvertimeRequestCreate Body(Setup s, Guid? policyId)
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
        var start = day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc);
        return new OvertimeRequestCreate(s.W.Subject.Id, policyId, null, day, start, start.AddHours(2), "Manual", "Close");
    }

    private static OvertimeRequest Stored(ActionResult<OvertimeRequest> result) =>
        result.Result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<OvertimeRequest>().Subject;

    [Fact]
    public async Task SelfService_IgnoresAForeignPolicyId_AndStoresTheResolvedOne()
    {
        var s = await SeedAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, s.W.SubjectUserId, ["Employee"], SelfService, callerEmployeeId: s.W.Subject.Id);

        var stored = Stored(await ctrl.CreateRequest(Body(s, s.OtherGrade.Id), CancellationToken.None));

        stored.OvertimePolicyId.Should().NotBe(s.OtherGrade.Id, "a policy meant for another grade must never be reachable by naming it");
        stored.OvertimePolicyId.Should().Be(s.OwnGrade.Id, "the most specific policy that applies to the employee's grade wins");
    }

    [Fact]
    public async Task SelfService_WithNoPolicyId_ResolvesFromTheEmployee_NotTheTenantDefault()
    {
        var s = await SeedAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, s.W.SubjectUserId, ["Employee"], SelfService, callerEmployeeId: s.W.Subject.Id);

        var stored = Stored(await ctrl.CreateRequest(Body(s, null), CancellationToken.None));

        stored.OvertimePolicyId.Should().Be(s.OwnGrade.Id);
    }

    [Fact]
    public async Task SelfService_OnlyAPolicyForAnotherGradeExists_FallsBackToTheUnscopedOne_NeverTheForeignOne()
    {
        var s = await SeedAsync();
        s.OwnGrade.IsActive = false;
        await s.W.Db.SaveChangesAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, s.W.SubjectUserId, ["Employee"], SelfService, callerEmployeeId: s.W.Subject.Id);

        var stored = Stored(await ctrl.CreateRequest(Body(s, s.OtherGrade.Id), CancellationToken.None));

        stored.OvertimePolicyId.Should().Be(s.Standard.Id);
    }

    [Fact]
    public async Task NoPolicyAppliesToTheEmployee_IsRefused_NotPaidUnderAForeignPolicy()
    {
        var s = await SeedAsync();
        s.Standard.IsActive = false;
        s.OwnGrade.IsActive = false;
        await s.W.Db.SaveChangesAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, s.W.SubjectUserId, ["Employee"], SelfService, callerEmployeeId: s.W.Subject.Id);

        var result = await ctrl.CreateRequest(Body(s, null), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await s.W.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hr_WithAPolicyThatDoesNotApplyToTheEmployee_Gets422_AndNothingIsStored()
    {
        var s = await SeedAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, Guid.NewGuid(), ["HR Manager"], Hr);

        var result = await ctrl.CreateRequest(Body(s, s.OtherGrade.Id), CancellationToken.None);

        var refusal = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        refusal.Value!.GetType().GetProperty("code")!.GetValue(refusal.Value).Should().Be("overtime_policy_not_applicable");
        (await s.W.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hr_WithAPolicyThatAppliesToTheEmployee_IsAccepted_EvenIfAnotherIsMoreSpecific()
    {
        var s = await SeedAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, Guid.NewGuid(), ["HR Manager"], Hr);

        // The unscoped Standard policy applies to everyone, so HR may choose it over the grade policy.
        var stored = Stored(await ctrl.CreateRequest(Body(s, s.Standard.Id), CancellationToken.None));

        stored.OvertimePolicyId.Should().Be(s.Standard.Id);
    }

    [Fact]
    public async Task Hr_WithAPolicyFromAnotherTenant_IsStillRefused()
    {
        var s = await SeedAsync();
        var foreign = new OvertimePolicy { TenantId = Guid.NewGuid(), Code = "X", Name = "Foreign", IsActive = true };
        s.W.Db.OvertimePolicies.Add(foreign);
        await s.W.Db.SaveChangesAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, Guid.NewGuid(), ["HR Manager"], Hr);

        var result = await ctrl.CreateRequest(Body(s, foreign.Id), CancellationToken.None);

        result.Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await s.W.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hr_PolicyScopedToTheEmployeesBranchAndDepartment_Applies_AMismatchedOneDoesNot()
    {
        var s = await SeedAsync();
        var branch = Guid.NewGuid();
        var dept = Guid.NewGuid();
        s.W.Subject.BranchId = branch;
        s.W.Subject.DepartmentId = dept;
        var match = new OvertimePolicy { TenantId = s.W.TenantId, Code = "BD", Name = "Branch+Dept", IsActive = true, RoundingRule = "None", MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 6000, BranchId = branch, DepartmentId = dept };
        var half = new OvertimePolicy { TenantId = s.W.TenantId, Code = "BX", Name = "Branch+other dept", IsActive = true, RoundingRule = "None", MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 6000, BranchId = branch, DepartmentId = Guid.NewGuid() };
        s.W.Db.OvertimePolicies.AddRange(match, half);
        await s.W.Db.SaveChangesAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, Guid.NewGuid(), ["HR Manager"], Hr);

        (await ctrl.CreateRequest(Body(s, half.Id), CancellationToken.None)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        Stored(await ctrl.CreateRequest(Body(s, match.Id), CancellationToken.None)).OvertimePolicyId.Should().Be(match.Id);
    }

    [Fact]
    public async Task DetectFromAttendance_NeverStampsAPolicyThatDoesNotApplyToTheEmployee()
    {
        var s = await SeedAsync();
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
        s.W.Db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = s.W.TenantId, EmployeeId = s.W.Subject.Id, EmployeeName = s.W.Subject.FullName, WorkDate = day,
            OvertimeMinutes = 120, LastOutUtc = day.ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc), Status = "Present",
        });
        await s.W.Db.SaveChangesAsync();
        var ctrl = OvertimeFixKit.Controller(s.W, Guid.NewGuid(), ["HR Manager"], Hr);

        var result = await ctrl.DetectFromAttendance(new DetectOvertimeRequest(day, day, s.OtherGrade.Id), CancellationToken.None);

        var created = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DetectOvertimeResult>().Subject.Created;
        var only = created.Should().ContainSingle().Subject;
        only.OvertimePolicyId.Should().NotBe(s.OtherGrade.Id);
        only.OvertimePolicyId.Should().Be(s.OwnGrade.Id);
    }
}
