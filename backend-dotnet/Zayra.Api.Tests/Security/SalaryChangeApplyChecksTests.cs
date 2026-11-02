using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// L4: a staged salary change is checked again when Finance approves it (the employee or the grade can change in between).
/// L5: a second maker who replaces a pending proposal for the same employee and date tells the first one.
/// </summary>
public class SalaryChangeApplyChecksTests
{
    private static async Task<(ScopeSodWorld W, SalaryStructure S, Guid GradeId)> SeedAsync()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.Plain();
        var s = new SalaryStructure { TenantId = w.Tenant, Code = "STD", Name = "Standard", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1) };
        var grade = new Grade { TenantId = w.Tenant, Code = "G5", Name = "Grade 5", Level = 5, MinSalary = 1_000m, MidSalary = 8_000m, MaxSalary = 10_000m, Currency = "SAR", IsActive = true };
        db.SalaryStructures.Add(s);
        db.Grades.Add(grade);
        await db.SaveChangesAsync();
        var alice = await db.Employees.SingleAsync(e => e.Id == w.EmpA);
        alice.GradeId = grade.Id;
        await db.SaveChangesAsync();
        return (w, s, grade.Id);
    }

    private static EmployeeSalaryStructureRequest Package(ScopeSodWorld w, SalaryStructure s, decimal basic, string date = "2026-01-01") =>
        new(w.EmpA, s.Id, basic, 0m, 0m, 0m, 0m, 0m, 0m, DateOnly.Parse(date), "SAR");

    private static async Task<Guid> ProposeAsync(ScopeSodWorld w, SalaryStructure s, Guid maker, decimal basic, Zayra.Api.Infrastructure.Notifications.INotificationService? notify = null)
    {
        await using var db = w.Plain();
        var ctrl = notify is null ? SalaryApprovalKit.FinanceController(db, w.Tenant, maker, "payroll.write") : SalaryApprovalKit.FinanceController(db, w.Tenant, maker, notify, "payroll.write");
        (await ctrl.AssignEmployeeSalary(Package(w, s, basic), default)).Should().BeOfType<AcceptedResult>();
        return (await db.SalaryChangeRequests.AsNoTracking().OrderByDescending(x => x.RequestedAtUtc).FirstAsync()).Id;
    }

    [Fact]
    public async Task ApprovalIsRefused_WhenTheEmployeeHasLeftSinceItWasStaged()
    {
        var (w, s, _) = await SeedAsync();
        var id = await ProposeAsync(w, s, Guid.NewGuid(), 9_000m);
        await using (var db = w.Plain())
        {
            (await db.Employees.SingleAsync(e => e.Id == w.EmpA)).Status = "Terminated";
            await db.SaveChangesAsync();
        }

        await using var approverDb = w.Plain();
        var r = await SalaryApprovalKit.FinanceController(approverDb, w.Tenant).ApproveSalaryChange(id, null, default);

        r.Should().BeOfType<ConflictObjectResult>().Which.Value!.ToString().Should().Contain("salary_change_stale");
        (await approverDb.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == w.EmpA && x.BasicSalary == 9_000m)).Should().BeFalse();
        (await approverDb.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(SalaryChangeRequest.Pending);
    }

    [Fact]
    public async Task ApprovalIsRefused_WhenTheGradeMaximumFellBelowThePackageSinceItWasStaged()
    {
        var (w, s, gradeId) = await SeedAsync();
        var id = await ProposeAsync(w, s, Guid.NewGuid(), 9_000m);
        await using (var db = w.Plain())
        {
            (await db.Grades.SingleAsync(g => g.Id == gradeId)).MaxSalary = 8_000m;
            await db.SaveChangesAsync();
        }

        await using var approverDb = w.Plain();
        var r = await SalaryApprovalKit.FinanceController(approverDb, w.Tenant).ApproveSalaryChange(id, null, default);

        r.Should().BeOfType<ConflictObjectResult>().Which.Value!.ToString().Should().Contain("maximum");
        (await approverDb.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == w.EmpA && x.BasicSalary == 9_000m)).Should().BeFalse();
    }

    [Fact]
    public async Task ApprovalStillWorks_WhenNothingChanged()
    {
        var (w, s, _) = await SeedAsync();
        var id = await ProposeAsync(w, s, Guid.NewGuid(), 9_000m);
        await using var approverDb = w.Plain();
        (await SalaryApprovalKit.FinanceController(approverDb, w.Tenant).ApproveSalaryChange(id, null, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ASecondMakerReplacingAPendingProposal_TellsTheFirstMaker()
    {
        var (w, s, _) = await SeedAsync();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var notes = new SalaryApprovalKit.RecordingNotifications();
        var firstId = await ProposeAsync(w, s, first, 8_000m, notes);

        await ProposeAsync(w, s, second, 9_500m, notes);

        await using var db = w.Plain();
        (await db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == firstId)).Status.Should().Be(SalaryChangeRequest.Cancelled);
        notes.Sent.Should().ContainSingle(n => n.UserId == first && n.Title.Contains("replaced"));
    }

    [Fact]
    public async Task AMakerReplacingTheirOwnProposal_IsNotNotified()
    {
        var (w, s, _) = await SeedAsync();
        var maker = Guid.NewGuid();
        var notes = new SalaryApprovalKit.RecordingNotifications();
        await ProposeAsync(w, s, maker, 8_000m, notes);
        await ProposeAsync(w, s, maker, 8_500m, notes);
        notes.Sent.Should().BeEmpty();
    }
}
