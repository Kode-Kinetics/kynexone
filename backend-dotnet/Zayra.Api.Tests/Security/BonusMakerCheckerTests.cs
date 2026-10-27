using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>HR prepares a bonus batch; a different person approves it; nobody approves or releases a payment to
/// themselves, and the approver does not also mark it paid.</summary>
public class BonusMakerCheckerTests
{
    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private static async Task<(ScopeSodWorld W, Guid BatchId, Guid Maker)> SeedAsync(string status = "PendingApproval")
    {
        var w = await ScopeSodWorld.CreateAsync();
        var maker = Guid.NewGuid();
        await using var db = w.Plain();
        var type = new BonusType { TenantId = w.Tenant, NameEn = "Performance", Code = "PERF" };
        db.BonusTypes.Add(type);
        var batch = new BonusBatch
        {
            TenantId = w.Tenant, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-1", BatchName = "B1",
            PaymentPeriod = "2026-10", PaymentDate = new DateOnly(2026, 10, 25), Status = status, EmployeeCount = 1, TotalAmount = 100m,
            CreatedBy = maker, UpdatedBy = maker,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = w.Tenant, CompanyId = w.CompanyA, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = w.EmpA,
            EmployeeName = "Alpha Emp", BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 5000m, CalculationMethod = "Fixed",
            CalculationValue = 100m, GrossBonusAmount = 100m, BonusAmount = 100m, PaymentPeriod = "2026-10", Status = "Draft",
        });
        await db.SaveChangesAsync();
        return (w, batch.Id, maker);
    }

    private static BonusesController Ctl(ScopeSodWorld w, Guid userId)
    {
        var p = w.GroupAdmin(userId, "payroll.write", "payroll.approve");
        return ScopeSodWorld.Wire(new BonusesController(w.DbFor(p), new OrgScope()), p);
    }

    private static string ErrorOf(IActionResult r) =>
        r.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!;

    [Fact]
    public async Task TheMakerCannotApproveTheirOwnBatch()
    {
        var (w, id, maker) = await SeedAsync();
        ErrorOf(await Ctl(w, maker).ApproveBatch(id, new BatchApproveRequest(null), default)).Should().Contain("maker_cannot_approve");
        await using var check = w.Plain();
        (await check.BonusBatches.AsNoTracking().SingleAsync(b => b.Id == id)).Status.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task ARecipientCannotApproveTheBatch()
    {
        var (w, id, _) = await SeedAsync();
        ErrorOf(await Ctl(w, w.UserA).ApproveBatch(id, new BatchApproveRequest(null), default)).Should().Contain("subject_cannot_decide");
    }

    [Fact]
    public async Task ADifferentFinanceApproverCanApprove()
    {
        var (w, id, _) = await SeedAsync();
        (await Ctl(w, Guid.NewGuid()).ApproveBatch(id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TheApproverCannotAlsoMarkItPaid_ButAThirdPersonCan()
    {
        var (w, id, _) = await SeedAsync();
        var approver = Guid.NewGuid();
        (await Ctl(w, approver).ApproveBatch(id, new BatchApproveRequest(null), default)).Should().BeOfType<OkObjectResult>();
        ErrorOf(await Ctl(w, approver).MarkBatchPaid(id, new MarkBatchPaidRequest(null), default)).Should().Contain("maker_cannot_approve");
        (await Ctl(w, Guid.NewGuid()).MarkBatchPaid(id, new MarkBatchPaidRequest(null), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ARecipientCannotMarkTheirOwnBonusPaid()
    {
        var (w, id, _) = await SeedAsync("Approved");
        ErrorOf(await Ctl(w, w.UserA).MarkBatchPaid(id, new MarkBatchPaidRequest(null), default)).Should().Contain("subject_cannot_decide");
    }

    [Fact]
    public async Task AnyoneWhoEditedALine_CannotApproveTheBatch()
    {
        var (w, id, _) = await SeedAsync();
        var editor = Guid.NewGuid();
        await using (var db = w.Plain())
        {
            var line = await db.EmployeeBonuses.SingleAsync(b => b.BonusBatchId == id);
            line.UpdatedBy = editor; await db.SaveChangesAsync();
        }
        ErrorOf(await Ctl(w, editor).ApproveBatch(id, new BatchApproveRequest(null), default)).Should().Contain("maker_cannot_approve");
    }

    [Fact]
    public async Task ThePreparer_CannotMarkTheirOwnBatchPaid_ButAnotherFinanceUserCan()
    {
        var (w, id, maker) = await SeedAsync(status: "Approved");
        ErrorOf(await Ctl(w, maker).MarkBatchPaid(id, new MarkBatchPaidRequest(null), default)).Should().Contain("maker_cannot_approve");
        await using var check = w.Plain();
        (await check.BonusBatches.AsNoTracking().SingleAsync(b => b.Id == id)).Status.Should().Be("Approved");
    }
}
