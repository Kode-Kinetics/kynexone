using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Slice L1 — loan limits by grade. Each Fact names the contract test it proves (G1, G2, …). The money rules
/// are exercised through the same LoanEligibilityService the controllers use; the API rules through the
/// controllers themselves.
/// </summary>
public class GradeLoanLimitTests
{
    // ── G1: not eligible ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task G1_GradeNotEligible_IsRefused_AndAPolicyExceptionCannotWaiveIt()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, eligible: false);
        h.Policy.AllowExceptions = true;
        await h.Db.SaveChangesAsync();

        var result = await h.Assess(1_000);
        Assert.False(result.Eligible);
        Assert.Contains(GradeLimitCodes.NotEligible, result.Codes);
        Assert.Equal(0m, result.Available);
        Assert.Equal(0m, result.MaxAvailableAmount);

        var refused = await h.Controller("Employee").CreateLoan(h.Request(1_000) with { RequestPolicyException = true }, default);
        Assert.Contains(GradeLimitCodes.NotEligible, Codes(refused));
        Assert.Empty(await h.Db.EmployeeLoans.ToListAsync());
        Assert.DoesNotContain(GradeLimitCodes.NotEligible, LoanLifecycleService.ExceptionCodes);
    }

    // ── G2: per-loan cap ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task G2_PerLoanCapOf10000_Accepts10000_Refuses10001()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m);
        await h.Db.SaveChangesAsync();

        Assert.True((await h.Assess(10_000m)).Eligible);
        var over = await h.Assess(10_001m);
        Assert.False(over.Eligible);
        Assert.Equal(new[] { GradeLimitCodes.PerLoan }, over.Codes);
        Assert.Equal(10_000m, over.MaxAvailableAmount);
        Assert.Equal(LoanLimitKinds.GradePerLoan, over.BindingLimit);

        Assert.IsType<BadRequestObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(10_001m), default));
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(10_000m), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        Assert.Equal(h.G2.Id, loan.GradeIdAtRequest);
        Assert.Equal(10_000m, loan.GradePerLoanCap);
        Assert.Null(loan.GradeOutstandingCap);
        Assert.NotNull(loan.GradeEntitlementId);
    }

    [Fact]
    public async Task OutstandingCap_CountsSameTypeOutstandingAndPending_NotOtherTypes()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m, maxOutstanding: 12_000m);
        var other = new LoanType { TenantId = h.Tid, Code = "CAR", NameEn = "Car", MaxInstallments = 12 };
        h.Db.Add(other);
        var active = h.Loan("Active", 5_000m); active.OutstandingBalance = 4_000m; active.DisbursementDate = h.Today.AddMonths(-2);
        var pending = h.Loan("Pending", 3_000m);
        var otherType = h.Loan("Pending", 9_000m); otherType.LoanTypeId = other.Id;
        h.Db.AddRange(active, pending, otherType);
        await h.Db.SaveChangesAsync();

        var result = await h.Assess(5_001m);
        Assert.Equal(7_000m, result.GradeLimit!.OutstandingNow);      // 4,000 outstanding + 3,000 pending; car ignored
        Assert.Equal(5_000m, result.GradeLimit.Available);
        Assert.Equal(new[] { GradeLimitCodes.Outstanding }, result.Codes);
        Assert.Equal(LoanLimitKinds.GradeOutstanding, result.BindingLimit);
        var breakdown = Assert.Single(result.Limits!, x => x.Limit == LoanLimitKinds.GradeOutstanding);
        Assert.Equal((12_000m, 7_000m, 5_000m), (breakdown.Cap, breakdown.OutstandingNow!.Value, breakdown.Available!.Value));
        Assert.True((await h.Assess(5_000m)).Eligible);
    }

    [Fact]
    public async Task MultipleOfBasic_AndMultipleOfGross_UseTheSalaryInForce()
    {
        await using var h = await H.Create();
        h.Salary(basic: 6_000m, housing: 1_500m, transport: 500m, other: 250m);   // gross 8,250
        var cell = h.Cell(h.G2, rate: 2m, valueType: GradeEntitlementValueTypes.MultipleOfBasic);
        await h.Db.SaveChangesAsync();

        var basic = await h.Assess(12_000m);
        Assert.True(basic.Eligible);
        Assert.Equal(12_000m, basic.GradeLimit!.PerLoanCap);
        Assert.Equal(6_000m, basic.GradeLimit.SalaryBasisAmount);
        Assert.Contains(GradeLimitCodes.PerLoan, (await h.Assess(12_000.01m)).Codes);

        h.Db.Remove(cell);
        h.Cell(h.G2, rate: 1.5m, valueType: GradeEntitlementValueTypes.MultipleOfGross);
        await h.Db.SaveChangesAsync();
        var gross = await h.Assess(12_375m);                                    // 1.5 × 8,250
        Assert.True(gross.Eligible);
        Assert.Equal(12_375m, gross.GradeLimit!.PerLoanCap);
        Assert.Equal(8_250m, gross.GradeLimit.SalaryBasisAmount);
        var line = Assert.Single(gross.Limits!, x => x.Limit == LoanLimitKinds.GradePerLoan);
        Assert.Equal((GradeEntitlementValueTypes.MultipleOfGross, 1.5m, 8_250m), (line.Basis, line.Multiple!.Value, line.SalaryBasisAmount!.Value));
        Assert.Contains(GradeLimitCodes.PerLoan, (await h.Assess(12_375.01m)).Codes);
    }

    [Fact]
    public async Task MultipleBasis_WithNoSalaryOnFile_IsBlockedNotGuessed()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, rate: 3m, valueType: GradeEntitlementValueTypes.MultipleOfBasic);
        await h.Db.SaveChangesAsync();
        var result = await h.Assess(100m);
        Assert.Contains(GradeLimitCodes.SalaryMissing, result.Codes);
        Assert.Equal(0m, result.Available);
    }

    [Fact]
    public async Task CompanyOverride_BeatsTheTenantCell_ForThatCompanyOnly()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m);
        h.Cell(h.G2, amount: 4_000m, companyId: h.Company.Id);
        h.Cell(h.G2, amount: 1_000m, companyId: h.OtherCompany.Id);
        await h.Db.SaveChangesAsync();

        var result = await h.Assess(4_500m);
        Assert.Contains(GradeLimitCodes.PerLoan, result.Codes);
        Assert.Equal(4_000m, result.GradeLimit!.PerLoanCap);
        Assert.True(result.GradeLimit.IsCompanyOverride);

        var grid = Assert.IsType<List<GradeLimitRowDto>>(Ok(await h.Controller("HR Manager").GetGradeLimits(h.Type.Id, h.Company.Id, null, default)));
        var g2 = grid.Single(x => x.GradeId == h.G2.Id);
        Assert.Equal((4_000m, true), (g2.Amount!.Value, g2.IsCompanyOverride));
        var tenantGrid = Assert.IsType<List<GradeLimitRowDto>>(Ok(await h.Controller("HR Manager").GetGradeLimits(h.Type.Id, null, null, default)));
        Assert.Equal((10_000m, false), (tenantGrid.Single(x => x.GradeId == h.G2.Id).Amount!.Value, tenantGrid.Single(x => x.GradeId == h.G2.Id).IsCompanyOverride));
        Assert.Null(tenantGrid.Single(x => x.GradeId == h.G1.Id).CellId);              // no cell in force
        Assert.Equal(new[] { 1, 2, 3 }, tenantGrid.Select(x => x.Level));
    }

    [Fact]
    public async Task EffectiveLimit_IsTheStrictestOfPolicyAndGrade()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m);
        h.Policy.MaxAmount = 6_000m;
        await h.Db.SaveChangesAsync();
        var policyBinds = await h.Assess(7_000m);
        Assert.Equal(new[] { "AmountLimit" }, policyBinds.Codes);                     // grade allows it, policy does not
        Assert.Equal(LoanLimitKinds.PolicyMaxAmount, policyBinds.BindingLimit);
        Assert.Equal(6_000m, policyBinds.Available);

        h.Policy.MaxAmount = 50_000m;
        await h.Db.SaveChangesAsync();
        var gradeBinds = await h.Assess(11_000m);
        Assert.Equal(new[] { GradeLimitCodes.PerLoan }, gradeBinds.Codes);
        Assert.Equal(LoanLimitKinds.GradePerLoan, gradeBinds.BindingLimit);
        Assert.Equal(10_000m, gradeBinds.MaxAvailableAmount);
    }

    [Fact]
    public async Task PolicySalaryMultiple_IsExplained_AsMultipleTimesSalaryMinusOutstanding()
    {
        await using var h = await H.Create();
        h.Type.GradeLimited = false;
        h.Salary(basic: 12_000m);
        h.Policy.MaxMultiplierOfSalary = 2m;
        var active = h.Loan("Active", 6_000m); active.OutstandingBalance = 6_000m; active.DisbursementDate = h.Today.AddMonths(-1);
        h.Db.Add(active);
        await h.Db.SaveChangesAsync();
        var result = await h.Assess(1_000m);
        Assert.Equal(LoanLimitKinds.PolicySalaryMultiple, result.BindingLimit);
        var line = Assert.Single(result.Limits!, x => x.Limit == LoanLimitKinds.PolicySalaryMultiple);
        // "Eligible up to SAR 18,000 = 2 × SAR 12,000 − outstanding SAR 6,000"
        Assert.Equal((2m, 12_000m, 24_000m, 6_000m, 18_000m),
            (line.Multiple!.Value, line.SalaryBasisAmount!.Value, line.Cap, line.OutstandingNow!.Value, line.Available!.Value));
        Assert.Equal(18_000m, result.Available);
    }

    // ── GradeLimited = false: behaviour unchanged ────────────────────────────────────────────────

    [Fact]
    public async Task NotGradeLimited_BehavesExactlyAsBefore_EvenWithCellsAndNoPolicy()
    {
        await using var h = await H.Create();
        h.Type.GradeLimited = false;
        h.Db.Remove(h.Policy);                                       // the loan-type baseline applies, as before
        h.Cell(h.G2, eligible: false);                               // would refuse if grade limits applied
        h.Employee.GradeId = null;                                   // would refuse if grade limits applied
        h.Type.MaxAmount = 5_000m;
        await h.Db.SaveChangesAsync();

        var ok = await h.Assess(5_000m);
        Assert.True(ok.Eligible);
        Assert.False(ok.GradeLimit!.Applies);
        Assert.Equal(5_000m, ok.MaxAvailableAmount);
        Assert.Equal(new[] { "AmountLimit" }, (await h.Assess(5_001m)).Codes);
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(4_000m), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        Assert.Null(loan.GradeIdAtRequest);
        Assert.Null(loan.GradeEntitlementId);
        Assert.Null(loan.GradePerLoanCap);
    }

    // ── configuration gaps block, with a plain reason ────────────────────────────────────────────

    [Fact]
    public async Task MissingCell_BlocksWithTheConfigReason_AndNotifiesHrOnSubmitOnly_OncePer24Hours()
    {
        await using var h = await H.Create();
        h.Cell(h.G1, amount: 1_000m);                                 // G2 (the employee's grade) has no cell
        var hrUser = h.AddStaff("HR Manager");
        var financeUser = h.AddStaff("Finance");
        await h.Db.SaveChangesAsync();

        // A read never notifies.
        var preview = Json(Ok(await h.Controller("Employee").GetLoanEligibility(h.Type.Id, 500m, 4, ct: default)));
        Assert.Contains(GradeLimitCodes.NotConfigured, preview);
        Assert.Empty(await h.Db.Notifications.ToListAsync());

        var refused = await h.Controller("Employee").CreateLoan(h.Request(500m), default);
        Assert.Contains(GradeLimitCodes.NotConfigured, Codes(refused));
        Assert.Contains("HR has been notified", Json(((BadRequestObjectResult)refused).Value));
        var note = Assert.Single(await h.Db.Notifications.ToListAsync());
        Assert.Equal((hrUser.Id, GradeLoanLimitResolver.NotConfiguredEntity, GradeLoanLimitResolver.NotConfiguredEntityId(h.Type.Id, h.G2.Id)),
            (note.UserId!.Value, note.EntityName, note.EntityId!));

        // Read or not, a second submission within 24 hours raises nothing new.
        note.Status = "Read"; await h.Db.SaveChangesAsync();
        await h.Controller("Employee").CreateLoan(h.Request(400m), default);
        Assert.Single(await h.Db.Notifications.ToListAsync());

        // After 24 hours it is raised again.
        note.CreatedAtUtc = DateTime.UtcNow.AddHours(-25); await h.Db.SaveChangesAsync();
        await h.Controller("Employee").CreateLoan(h.Request(400m), default);
        Assert.Equal(2, await h.Db.Notifications.CountAsync(x => x.UserId == hrUser.Id));
        Assert.Equal(0, await h.Db.Notifications.CountAsync(x => x.UserId == financeUser.Id));
    }

    [Fact]
    public async Task EmployeeWithoutAGrade_IsBlocked()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m);
        h.Employee.GradeId = null;
        await h.Db.SaveChangesAsync();
        var result = await h.Assess(100m);
        Assert.Equal(new[] { GradeLimitCodes.Missing }, result.Codes);
        Assert.Contains("grade", result.Reasons.Single());
    }

    [Fact]
    public async Task NoCompanyPolicy_MeansTheTypeIsNotOffered()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m);
        h.Db.Remove(h.Policy);
        await h.Db.SaveChangesAsync();
        var result = await h.Assess(100m);
        Assert.Contains("LoanTypeNotOffered", result.Codes);
        Assert.Contains("aren't offered by your company", string.Join(" ", result.Reasons));

        var offered = Json(Ok(await h.Controller("Employee").ListOfferedLoanTypes(null, default)));
        Assert.Contains("\"offered\":false", offered);
        Assert.Contains("LoanTypeNotOffered", offered);
    }

    // ── witnesses and the approval re-check ──────────────────────────────────────────────────────

    [Fact]
    public async Task GradeChangeAfterApproval_DoesNotAlterTheLoanWitness()
    {
        await using var h = await H.Create();
        var g2Cell = h.Cell(h.G2, amount: 10_000m);
        h.Cell(h.G3, amount: 50_000m);
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(8_000m), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        var step = await h.Db.LoanApprovals.SingleAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").DecideApproval(loan.Id, step.Id, new("Approved", null, null, null, null), default));

        h.Employee.GradeId = h.G3.Id;                                // promotion
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(
            new PublishGradeLimitsRequest(h.Type.Id, null, h.Today, [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 2_000m)]), default));

        var reloaded = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync();
        Assert.Equal("Approved", reloaded.Status);
        Assert.Equal((h.G2.Id, g2Cell.Id, 10_000m), (reloaded.GradeIdAtRequest!.Value, reloaded.GradeEntitlementId!.Value, reloaded.GradePerLoanCap!.Value));
        // A NEW application uses the grade in force today.
        Assert.Equal(h.G3.Id, (await h.Assess(20_000m)).GradeLimit!.GradeId);
    }

    [Fact]
    public async Task ApprovalReCheck_RefusesAnOverLimitLoan_AndRefreshesTheWitness()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m);
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(9_000m), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        var step = await h.Db.LoanApprovals.SingleAsync();

        // HR lowers the grade's limit before the loan is decided.
        Assert.IsType<OkObjectResult>(await h.Controller("HR Director").PublishGradeLimits(
            new PublishGradeLimitsRequest(h.Type.Id, null, h.Today, [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 5_000m)]), default));
        var newCell = await h.Db.GradeEntitlements.SingleAsync(x => x.GradeId == h.G2.Id && x.EffectiveTo == null);

        var refused = await h.Controller("HR Manager").DecideApproval(loan.Id, step.Id, new("Approved", null, null, null, null), default);
        Assert.Contains(GradeLimitCodes.PerLoan, Codes(refused));
        Assert.Equal("Pending", (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).Status);

        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").DecideApproval(loan.Id, step.Id, new("Approved", null, 5_000m, null, null), default));
        var approved = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync();
        Assert.Equal(("Approved", 5_000m), (approved.Status, approved.ApprovedAmount));
        Assert.Equal((newCell.Id, 5_000m), (approved.GradeEntitlementId!.Value, approved.GradePerLoanCap!.Value));
    }

    // ── the grid API ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_ClosesTheCellInForce_AndOpensANewOne_LeavingOtherGradesUntouched()
    {
        await using var h = await H.Create();
        var oldG2 = h.Cell(h.G2, amount: 10_000m);
        var g3 = h.Cell(h.G3, amount: 20_000m);
        await h.Db.SaveChangesAsync();
        var from = h.Today.AddDays(10);

        var result = await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, from,
            [
                new(h.G2.Id, true, GradeEntitlementValueTypes.MultipleOfBasic, Rate: 2m, MaxOutstandingAmount: 30_000m),
                new(h.G1.Id, false, GradeEntitlementValueTypes.EligibilityOnly),
            ]), default);
        Assert.IsType<OkObjectResult>(result);
        var g2Versions = await h.Db.GradeEntitlements.Where(x => x.GradeId == h.G2.Id).OrderBy(x => x.EffectiveFrom).ToListAsync();
        Assert.Equal(2, g2Versions.Count);
        Assert.Equal(from.AddDays(-1), g2Versions[0].EffectiveTo);                  // closed, values untouched
        Assert.Equal(10_000m, g2Versions[0].Amount);
        Assert.Equal((from, (DateOnly?)null, 2m, 30_000m), (g2Versions[1].EffectiveFrom, g2Versions[1].EffectiveTo, g2Versions[1].Rate!.Value, g2Versions[1].MaxOutstandingAmount!.Value));
        Assert.Null((await h.Db.GradeEntitlements.SingleAsync(x => x.Id == g3.Id)).EffectiveTo);   // not in rows → untouched
        Assert.False((await h.Db.GradeEntitlements.SingleAsync(x => x.GradeId == h.G1.Id)).Eligible);
        Assert.Equal(2, await h.Db.AuditLogs.CountAsync(x => x.Action == "loans.grade_limit.published"));

        // Re-publishing identical values is a no-op; same-day re-publication never overwrites.
        var again = Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, from,
            [new(h.G2.Id, true, GradeEntitlementValueTypes.MultipleOfBasic, Rate: 2m, MaxOutstandingAmount: 30_000m)]), default));
        Assert.Contains("\"changed\":0", Json(again.Value));
        Assert.IsType<ConflictObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, from,
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 1m)]), default));
        Assert.Equal(oldG2.Amount, (await h.Db.GradeEntitlements.SingleAsync(x => x.Id == oldG2.Id)).Amount);
    }

    [Fact]
    public async Task Publish_RejectsMalformedRows_InPlainLanguage()
    {
        await using var h = await H.Create();
        var result = await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [
                new(h.G1.Id, false, GradeEntitlementValueTypes.EligibilityOnly, Amount: 5m),
                new(h.G2.Id, true, GradeEntitlementValueTypes.MultipleOfGross, Amount: 5m),
                new(h.G3.Id, true, GradeEntitlementValueTypes.EligibilityOnly, MaxOutstandingAmount: 10_000m),   // valid
            ]), default);
        var body = Json(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains("isn't eligible can't have limit figures", body);
        Assert.Contains("months of salary", body);
        Assert.Empty(await h.Db.GradeEntitlements.ToListAsync());
        Assert.IsType<BadRequestObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(
            new PublishGradeLimitsRequest(h.Type.Id, null, h.Today.AddDays(-1), [new(h.G3.Id, true, GradeEntitlementValueTypes.Amount, 1m)]), default));
    }

    [Fact]
    public async Task TenantLevelGrid_IsRefusedForACompanyScopedAdmin_WhoMayStillEditTheirCompany()
    {
        await using var h = await H.Create();
        var scoped = h.Controller("Admin", companyScope: [h.Company.Id]);
        var tenantWide = await scoped.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(tenantWide).StatusCode);
        Assert.IsType<ForbidResult>(await scoped.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, h.OtherCompany.Id, h.Today,
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default));
        Assert.IsType<OkObjectResult>(await scoped.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, h.Company.Id, h.Today,
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default));
        Assert.Equal(h.Company.Id, (await h.Db.GradeEntitlements.SingleAsync()).CompanyId);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(
            await scoped.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(false), default)).StatusCode);
    }

    [Fact]
    public async Task EnablingGradeLimits_IsRefusedUntilEveryGradeHasALimit_ThenCreatesTheFacilityComponent()
    {
        await using var h = await H.Create(gradeLimited: false);
        var hr = h.Controller("HR Manager");
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default));

        var refused = Assert.IsType<ConflictObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default));
        var body = Json(refused.Value, web: true);
        Assert.Contains("\"missingGrades\"", body);
        Assert.Contains("\"gradeName\":\"Grade 2\"", body);
        Assert.Contains("\"gradeCode\":\"G3\"", body);
        Assert.DoesNotContain("\"gradeCode\":\"G1\"", body);
        Assert.False((await h.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == h.Type.Id)).GradeLimited);

        // A company override covers its own company only: G2 for OtherCompany is still missing.
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, h.Company.Id, h.Today,
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 1_000m), new(h.G3.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default));
        Assert.IsType<ConflictObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default));
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 2_000m), new(h.G3.Id, false, GradeEntitlementValueTypes.EligibilityOnly)]), default));
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true, ConfirmStopOffering: true), default));

        var type = await h.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == h.Type.Id);
        Assert.True(type.GradeLimited);
        Assert.Equal("LOAN_PERSONAL", type.EntitlementComponentCode);
        var facility = await h.Db.PayComponents.SingleAsync(x => x.Code == "LOAN_PERSONAL");
        Assert.Equal((PayEntitlementClasses.Facility, PayComponentTypes.Facility, false), (facility.EntitlementClass, facility.ComponentType, facility.WpsIncluded));
        // GET /types carries the flag for the UI.
        var types = Json(Ok(await hr.ListLoanTypes(default)), web: true);
        Assert.Contains("\"gradeLimited\":true", types);
        Assert.Contains("\"entitlementComponentCode\":\"LOAN_PERSONAL\"", types);
    }

    [Fact]
    public async Task FacilityComponent_IsReadOnlyInThePayComponentCatalog()
    {
        await using var h = await H.Create(gradeLimited: false);
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default));
        var facility = await h.Db.PayComponents.SingleAsync(x => x.Code == "LOAN_PERSONAL");
        var catalog = new PayComponentsController(h.Db) { ControllerContext = h.Controller("Admin").ControllerContext };
        var refused = Assert.IsType<ConflictObjectResult>(await catalog.Update(facility.Id,
            new PayComponentsController.UpdateRequest("x", null, "Fixed", 100m, "EARN:OTHER", h.Today), default));
        Assert.Contains("facility_component", Json(refused.Value));
        Assert.DoesNotContain(PayComponentEngine.ResolveInEffect(await h.Db.PayComponents.ToListAsync(), h.Tid, new DateOnly(h.Today.Year, h.Today.Month, 1)),
            c => c.Code == "LOAN_PERSONAL");
    }

    // ── preview (no amount yet) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Eligibility_WithNoAmount_PreviewsTheLimit_WithoutFailingOnAmountRules()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m, maxOutstanding: 12_000m);
        var pending = h.Loan("Pending", 3_000m);
        h.Db.Add(pending);
        await h.Db.SaveChangesAsync();

        var result = Ok(await h.Controller("Employee").GetLoanEligibility(h.Type.Id, null, null, ct: default));
        var json = Json(result, web: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("eligible").GetBoolean());
        Assert.True(root.GetProperty("preview").GetBoolean());
        Assert.Equal(0, root.GetProperty("codes").GetArrayLength());
        Assert.Equal(9_000m, root.GetProperty("available").GetDecimal());
        Assert.Equal("GradeOutstanding", root.GetProperty("bindingLimit").GetString());
        var grade = root.GetProperty("gradeLimit");
        Assert.Equal(10_000m, grade.GetProperty("perLoanCap").GetDecimal());
        Assert.Equal(3_000m, grade.GetProperty("outstandingNow").GetDecimal());
        Assert.Equal("Grade 2", grade.GetProperty("gradeName").GetString());
        Assert.Equal("SAR", grade.GetProperty("currency").GetString());
        var limits = root.GetProperty("limitBreakdowns").EnumerateArray().Select(x => x.GetProperty("limit").GetString()).ToList();
        Assert.Contains("GradePerLoan", limits);
        Assert.Contains("GradeOutstanding", limits);

        // With an amount, the same request judges it.
        var judged = Json(Ok(await h.Controller("Employee").GetLoanEligibility(h.Type.Id, 9_500m, 4, ct: default)));
        Assert.Contains(GradeLimitCodes.Outstanding, judged);
    }

    [Fact]
    public async Task Preview_WithNothingLeftToBorrow_IsNotEligible_AndSaysWhichLimitIsUsedUp()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m, maxOutstanding: 6_000m);
        var active = h.Loan("Active", 6_000m); active.OutstandingBalance = 6_000m; active.DisbursementDate = h.Today.AddMonths(-1);
        h.Db.Add(active);
        await h.Db.SaveChangesAsync();
        using var doc = JsonDocument.Parse(Json(Ok(await h.Controller("Employee").GetLoanEligibility(h.Type.Id, null, null, ct: default)), web: true));
        var root = doc.RootElement;
        Assert.False(root.GetProperty("eligible").GetBoolean());
        Assert.Equal(0m, root.GetProperty("available").GetDecimal());
        Assert.Equal("GradeOutstanding", root.GetProperty("bindingLimit").GetString());
        Assert.Equal(GradeLimitCodes.Outstanding, root.GetProperty("codes")[0].GetString());
        Assert.Contains("fully used", root.GetProperty("reasons")[0].GetString());

        // Policy-side exhaustion is reported the same way, for a type that is not grade-limited.
        h.Type.GradeLimited = false; h.Policy.MaxTotalOutstanding = 6_000m;
        await h.Db.SaveChangesAsync();
        var policy = await new LoanEligibilityService(h.Db).EvaluateAsync(h.Tid, h.Employee, h.Type, 0, 0, "BankTransfer", preview: true);
        Assert.Equal((false, "PolicyTotalOutstanding"), (policy.Eligible, policy.BindingLimit));
        Assert.Contains("AmountLimit", policy.Codes);
    }

    [Fact]
    public async Task ReasonTexts_ReadCorrectly_ForTheEmployeeAndForHrOnTheirBehalf()
    {
        await using var h = await H.Create();
        h.Type.NameEn = "Car loan";
        h.Cell(h.G2, amount: 1_000m);
        await h.Db.SaveChangesAsync();
        var reason = (await h.Assess(2_000m)).Reasons.Single();
        Assert.Equal("Grade 2 allows up to 1,000.00 SAR per loan of this type (Car loan).", reason);
        Assert.DoesNotContain("loan loan", reason);
        Assert.DoesNotContain("Your", reason);
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today.AddDays(3),
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 2_000m)]), default));
        var conflict = Json(Assert.IsType<ConflictObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today.AddDays(3),
            [new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 3_000m)]), default)).Value);
        Assert.Contains("Grade 2 already has a limit", conflict);
        Assert.DoesNotContain("Grade Grade", conflict);
    }

    // ── Saudi law: employee loans are principal only (qard) ──────────────────────────────────────

    [Fact]
    public async Task InterestBearingLoanType_CannotBeCreated()
    {
        await using var h = await H.Create();
        foreach (var (free, rate) in new[] { (false, 0m), (true, 2.5m), (false, 5m) })
        {
            var result = await h.Controller("HR Manager").CreateLoanType(
                new LoanTypeRequest($"INT{rate}{free}", "Interest", null, 1_000m, 12, "Monthly", free, rate, 0, true), default);
            var body = Json(Assert.IsType<BadRequestObjectResult>(result).Value);
            Assert.Contains("Employee loans must be interest-free under Saudi law", body);
        }
        Assert.Equal(1, await h.Db.LoanTypes.CountAsync());
    }

    [Fact]
    public async Task LoanCreation_IsRefused_IfATypeSomehowCarriesInterest()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.Type.IsInterestFree = false; h.Type.InterestRate = 3m;            // e.g. a legacy row
        await h.Db.SaveChangesAsync();
        var refused = await h.Controller("Employee").CreateLoan(h.Request(100m), default);
        Assert.Contains("Employee loans must be interest-free under Saudi law", Json(Assert.IsType<BadRequestObjectResult>(refused).Value));
        Assert.Empty(await h.Db.EmployeeLoans.ToListAsync());
        Assert.Contains(LoanEligibilityCodes.InterestNotPermitted, (await h.Assess(100m)).Codes);
        Assert.IsType<BadRequestObjectResult>(await h.Controller("HR Manager").SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default));
        var offered = Json(Ok(await h.Controller("Employee").ListOfferedLoanTypes(null, default)));
        Assert.Contains(LoanEligibilityCodes.InterestNotPermitted, offered);
    }

    [Fact]
    public async Task InterestBearingType_AlreadyApplied_IsRefusedAtApproval()
    {
        await using var h = await H.Create(gradeLimited: false);
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(100m), default));
        h.Type.InterestRate = 1m;
        await h.Db.SaveChangesAsync();
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        var step = await h.Db.LoanApprovals.SingleAsync();
        var refused = await h.Controller("HR Manager").DecideApproval(loan.Id, step.Id, new("Approved", null, null, null, null), default);
        Assert.Contains(LoanEligibilityCodes.InterestNotPermitted, Codes(refused));
    }

    // ── fix round: ownership, collisions, offerings, Arabic names, in-flight loans ───────────────

    [Theory]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    [InlineData("Employee")]
    public async Task OnlyHrPolicyOwners_MayPublishGradeLimits_SwitchGradeLimiting_OrChangeOfferings(string role)
    {
        await using var h = await H.Create(gradeLimited: false);
        var actor = h.Controller(role);
        var publish = await actor.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default);
        var toggle = await actor.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default);
        var offering = await actor.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default);
        foreach (var result in new[] { publish, toggle, offering })
        {
            var refused = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
            Assert.Contains("hr_policy_owner_required", Json(refused.Value));
        }
        Assert.Empty(await h.Db.GradeEntitlements.ToListAsync());
        Assert.False((await h.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == h.Type.Id)).GradeLimited);
        Assert.Single(await h.Db.LoanPolicies.ToListAsync());
    }

    [Fact]
    public async Task LoanTypeCodes_ThatNormaliseToTheSameLimitCode_AreRefused()
    {
        await using var h = await H.Create();                         // PERSONAL, already LOAN_PERSONAL
        foreach (var code in new[] { "personal", "PERSONAL ", "PERSONAL_", "-Personal-" })
            Assert.IsType<ConflictObjectResult>(await h.Controller("HR Manager").CreateLoanType(
                new LoanTypeRequest(code, "Personal again", null, 1_000m, 12, "Monthly", true, 0m, 0, true), default));
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").CreateLoanType(
            new LoanTypeRequest("PER-SONAL", "Per sonal", null, 1_000m, 12, "Monthly", true, 0m, 0, true), default));
        var punctuation = Assert.IsType<ConflictObjectResult>(await h.Controller("HR Manager").CreateLoanType(
            new LoanTypeRequest("per.sonal", "Dots", null, 1_000m, 12, "Monthly", true, 0m, 0, true), default));
        Assert.Contains("too similar", Json(punctuation.Value));
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").CreateLoanType(
            new LoanTypeRequest("CAR", "Car", null, 1_000m, 12, "Monthly", true, 0m, 0, true), default));
    }

    [Fact]
    public async Task EnablingGradeLimits_IsRefused_WhenAnotherTypeAlreadyOwnsTheLimitCode()
    {
        await using var h = await H.Create();                         // h.Type owns LOAN_PERSONAL
        var legacy = new LoanType { TenantId = h.Tid, Code = "personal", NameEn = "Legacy personal", MaxInstallments = 12 };
        h.Db.Add(legacy);
        await h.Db.SaveChangesAsync();
        var refused = Assert.IsType<ConflictObjectResult>(await h.Controller("HR Manager").SetLoanTypeGradeLimited(legacy.Id, new SetGradeLimitedRequest(true), default));
        Assert.Contains("component_code_taken", Json(refused.Value));
        Assert.IsType<ConflictObjectResult>(await h.Controller("HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(legacy.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1m)]), default));
        Assert.Null((await h.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).EntitlementComponentCode);
    }

    [Fact]
    public async Task CompanyCanSwitchALoanTypeOff_EvenUnderAGroupWidePolicy_AndBackOn()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.Db.Remove(h.Policy);
        h.Db.Add(new LoanPolicy { TenantId = h.Tid, CompanyId = null, LoanTypeId = h.Type.Id, MaxAmount = 7_000m, MaxConcurrentLoans = 3,
            MaxInstallments = 24, PolicyName = "Group" });
        await h.Db.SaveChangesAsync();
        Assert.True((await h.Assess(100m)).Eligible);

        var hr = h.Controller("HR Manager");
        Assert.Contains("\"offered\":false", Json(Ok(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default)), web: true));
        var off = await h.Assess(100m);
        Assert.Equal(new[] { "LoanTypeNotOffered" }, off.Codes);
        Assert.Contains("\"offered\":false", Json(Ok(await h.Controller("Employee").ListOfferedLoanTypes(null, default)), web: true));
        Assert.IsType<BadRequestObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(100m), default));
        // The terms were copied from the group policy: only the offering changed.
        var companyPolicy = await h.Db.LoanPolicies.SingleAsync(x => x.CompanyId == h.Company.Id && x.IsActive);
        Assert.Equal((7_000m, 3, false), (companyPolicy.MaxAmount, companyPolicy.MaxConcurrentLoans, companyPolicy.IsOffered));
        Assert.Equal(1, await h.Db.AuditLogs.CountAsync(x => x.Action == "loans.type.not_offered"));
        // The other company still follows the group policy.
        var other = new Employee { TenantId = h.Tid, CompanyId = h.OtherCompany.Id, FullName = "Other", EmployeeCode = "O1", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-2) };
        h.Db.Add(other); await h.Db.SaveChangesAsync();
        Assert.True((await new LoanEligibilityService(h.Db).EvaluateAsync(h.Tid, other, h.Type, 100m, 4, "BankTransfer")).Eligible);

        // Asking for the state it is already in changes nothing; switching back on offers it again.
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default));
        Assert.Equal(1, await h.Db.LoanPolicies.CountAsync(x => x.CompanyId == h.Company.Id));
        var groupPolicyId = (await h.Db.LoanPolicies.SingleAsync(x => x.CompanyId == null)).Id;
        var on = Json(Ok(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, true), default)), web: true);
        // The switch undoes itself: the unchanged stub is retired and the group policy applies again.
        Assert.Contains("\"source\":\"GroupPolicy\"", on);
        Assert.Contains("\"detachedFromGroupPolicy\":false", on);
        var back = await h.Assess(100m);
        Assert.True(back.Eligible);
        Assert.Equal(groupPolicyId, back.PolicyId);
        Assert.Equal(0, await h.Db.LoanPolicies.CountAsync(x => x.CompanyId == h.Company.Id && x.IsActive));
        var offerings = Json(Ok(await hr.ListLoanTypeOfferings(h.Company.Id, default)), web: true);
        Assert.Contains("\"source\":\"GroupPolicy\"", offerings);
    }

    [Fact]
    public async Task BaselineOffThenOn_RestoresTheBaselineExactly_IncludingUnlimitedConcurrentLoans()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.Db.Remove(h.Policy);
        h.Db.AddRange(h.Loan("Pending", 100m), h.Loan("Pending", 100m));      // two open requests: fine on the baseline
        await h.Db.SaveChangesAsync();
        var before = await h.Assess(100m);
        Assert.True(before.Eligible);
        Assert.Null(before.PolicyId);

        var hr = h.Controller("HR Manager");
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default));
        Assert.Equal(new[] { "LoanTypeNotOffered" }, (await h.Assess(100m)).Codes);   // the only reason while off
        var on = Json(Ok(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, true), default)), web: true);
        Assert.Contains("\"source\":\"LoanTypeBaseline\"", on);

        var after = await h.Assess(100m);
        Assert.Equal((before.Eligible, before.MaxAvailableAmount, before.PolicyId, before.Available, before.BindingLimit),
            (after.Eligible, after.MaxAvailableAmount, after.PolicyId, after.Available, after.BindingLimit));
        Assert.Equal(before.Codes, after.Codes);
        static string Terms(string json)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node.Remove(nameof(LoanPolicy.Id)); node.Remove(nameof(LoanPolicy.CreatedAtUtc));   // the baseline is built per call
            return node.ToJsonString();
        }
        Assert.Equal(Terms(before.PolicySnapshotJson), Terms(after.PolicySnapshotJson));   // includes MaxConcurrentLoans = unlimited
        Assert.Equal(int.MaxValue, JsonSerializer.Deserialize<LoanPolicy>(after.PolicySnapshotJson)!.MaxConcurrentLoans);
        Assert.Equal(0, await h.Db.LoanPolicies.CountAsync(x => x.IsActive));
        Assert.Single(await h.Db.LoanPolicies.Where(x => x.CreatedByOfferingSwitch && x.CopiedFromPolicyId == null).ToListAsync());
    }

    [Fact]
    public async Task OffThenOn_UnderAGroupPolicy_KeepsAnEditedCompanyPolicy_AndSaysGroupChangesNoLongerApply()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.Db.Remove(h.Policy);
        h.Db.Add(new LoanPolicy { TenantId = h.Tid, CompanyId = null, LoanTypeId = h.Type.Id, MaxAmount = 7_000m, MaxConcurrentLoans = 3,
            MaxInstallments = 24, PolicyName = "Group" });
        await h.Db.SaveChangesAsync();
        var hr = h.Controller("HR Manager");
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default));
        // HR then edits the company's terms (still not offered) through the policy form.
        Assert.IsType<OkObjectResult>(await hr.CreateLoanPolicy(new LoanPolicyRequest(h.Company.Id, h.Type.Id, "Company terms", MaxAmount: 4_000m,
            AllowedEmploymentStatuses: ["Active"], AllowedRepaymentMethods: ["BankTransfer"], IsOffered: false), default));
        var on = Json(Ok(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, true), default)), web: true);
        Assert.Contains("\"source\":\"CompanyPolicy\"", on);
        Assert.Contains("\"detachedFromGroupPolicy\":true", on);
        var active = await h.Db.LoanPolicies.SingleAsync(x => x.CompanyId == h.Company.Id && x.IsActive);
        Assert.Equal((4_000m, true, false), (active.MaxAmount, active.IsOffered, active.CreatedByOfferingSwitch));
        Assert.Equal(new[] { "AmountLimit" }, (await h.Assess(5_000m)).Codes);
    }

    [Fact]
    public async Task EnablingGradeLimits_WarnsAboutCompaniesWithoutAPolicy_AndNeedsAnExplicitConfirmation()
    {
        await using var h = await H.Create(gradeLimited: false);           // Company has a policy; OtherCompany has none
        var hr = h.Controller("HR Manager");
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1_000m), new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 1_000m),
             new(h.G3.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default));
        var refused = Assert.IsType<ConflictObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default));
        var body = Json(refused.Value, web: true);
        Assert.Contains("\"error\":\"companies_without_policy\"", body);
        Assert.Contains($"\"id\":\"{h.OtherCompany.Id}\"", body);
        Assert.Contains("\"name\":\"Other Co\"", body);
        Assert.DoesNotContain(h.Company.Id.ToString(), body);
        Assert.False((await h.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == h.Type.Id)).GradeLimited);

        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true, ConfirmStopOffering: true), default));
        Assert.True((await h.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == h.Type.Id)).GradeLimited);
        var audit = await h.Db.AuditLogs.SingleAsync(x => x.Action == "loans.type.grade_limited_on");
        Assert.Contains(h.OtherCompany.Id.ToString(), audit.Metadata);
    }

    [Fact]
    public async Task TenantWideFixedAmounts_AreRefused_WhenCompaniesUseDifferentCurrencies()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.OtherCompany.DefaultCurrency = "AED";
        await h.Db.SaveChangesAsync();
        var hr = h.Controller("HR Manager");
        foreach (var row in new GradeLimitRowInput[]
                 {
                     new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1_000m),
                     new(h.G1.Id, true, GradeEntitlementValueTypes.MultipleOfBasic, Rate: 2m, MaxOutstandingAmount: 5_000m),
                     new(h.G1.Id, true, GradeEntitlementValueTypes.EligibilityOnly, MaxOutstandingAmount: 5_000m),
                 })
        {
            var refused = Assert.IsType<UnprocessableEntityObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today, [row]), default));
            Assert.Contains("grade_limit_currency_ambiguous", Json(refused.Value));
        }
        Assert.Empty(await h.Db.GradeEntitlements.ToListAsync());
        // Salary multiples (no fixed figure) stay allowed for all companies; fixed amounts per company are fine.
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.MultipleOfGross, Rate: 1.5m), new(h.G2.Id, false, GradeEntitlementValueTypes.EligibilityOnly)]), default));
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, h.OtherCompany.Id, h.Today,
            [new(h.G3.Id, true, GradeEntitlementValueTypes.Amount, 4_000m, MaxOutstandingAmount: 8_000m)]), default));
    }

    [Fact]
    public async Task ExplicitNotOffered_AppliesToTypesThatAreNotGradeLimited_AndNoPolicyStillUsesTheBaseline()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.Db.Remove(h.Policy); await h.Db.SaveChangesAsync();
        Assert.True((await h.Assess(100m)).Eligible);                  // no policy, not grade-limited: baseline, as before
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default));
        Assert.Contains("LoanTypeNotOffered", (await h.Assess(100m)).Codes);
        // Publishing a policy with "offered" unticked is the same switch through the policy form.
        var policy = await h.Controller("HR Manager").CreateLoanPolicy(new LoanPolicyRequest(h.Company.Id, h.Type.Id, "Off again",
            AllowedEmploymentStatuses: ["Active"], AllowedRepaymentMethods: ["BankTransfer"], IsOffered: true), default);
        Assert.Contains("\"isOffered\":true", Json(Ok(policy), web: true));
        Assert.True((await h.Assess(100m)).Eligible);
    }

    [Fact]
    public async Task GradeNameAr_IsReturnedInTheGrid_TheLimitCheck_AndMissingGrades()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.G2.NameAr = "الدرجة الثانية";
        await h.Db.SaveChangesAsync();
        var grid = Assert.IsType<List<GradeLimitRowDto>>(Ok(await h.Controller("HR Manager").GetGradeLimits(h.Type.Id, null, null, default)));
        Assert.Equal("الدرجة الثانية", grid.Single(x => x.GradeId == h.G2.Id).GradeNameAr);
        Assert.Null(grid.Single(x => x.GradeId == h.G1.Id).GradeNameAr);
        var missing = Assert.IsType<ConflictObjectResult>(await h.Controller("HR Manager").SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default));
        Assert.Contains("\"gradeNameAr\":\"الدرجة الثانية\"", Json(missing.Value, web: true));

        h.Type.GradeLimited = true; h.Type.EntitlementComponentCode = "LOAN_PERSONAL";
        h.Cell(h.G2, amount: 5_000m);
        await h.Db.SaveChangesAsync();
        var check = Json(Ok(await h.Controller("Employee").GetLoanEligibility(h.Type.Id, null, null, ct: default)), web: true);
        Assert.Contains("\"gradeNameAr\":\"الدرجة الثانية\"", check);
    }

    [Fact]
    public async Task EnablingGradeLimits_ReChecksLoansAlreadyWaitingForApproval()
    {
        await using var h = await H.Create(gradeLimited: false);
        Assert.IsType<OkObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(8_000m), default));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        Assert.Null(loan.GradeEntitlementId);                          // requested before grade limits applied
        var hr = h.Controller("HR Director");
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1_000m), new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 5_000m),
             new(h.G3.Id, true, GradeEntitlementValueTypes.Amount, 9_000m)]), default));
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true, ConfirmStopOffering: true), default));

        var step = await h.Db.LoanApprovals.SingleAsync();
        var refused = await h.Controller("HR Manager").DecideApproval(loan.Id, step.Id, new("Approved", null, null, null, null), default);
        Assert.Contains(GradeLimitCodes.PerLoan, Codes(refused));
        Assert.IsType<OkObjectResult>(await h.Controller("HR Manager").DecideApproval(loan.Id, step.Id, new("Approved", null, 5_000m, null, null), default));
        var approved = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync();
        Assert.Equal((h.G2.Id, 5_000m), (approved.GradeIdAtRequest!.Value, approved.GradePerLoanCap!.Value));
    }

    [Fact]
    public async Task BaselineStub_ThenGradeLimits_ThenSwitchOn_IsRefused_AndNeverOffersTheStubsTerms()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.Db.Remove(h.Policy);                                          // no policy anywhere: the baseline applies
        await h.Db.SaveChangesAsync();
        var hr = h.Controller("HR Manager");
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default));
        Assert.IsType<OkObjectResult>(await hr.PublishGradeLimits(new PublishGradeLimitsRequest(h.Type.Id, null, h.Today,
            [new(h.G1.Id, true, GradeEntitlementValueTypes.Amount, 1_000m), new(h.G2.Id, true, GradeEntitlementValueTypes.Amount, 1_000m),
             new(h.G3.Id, true, GradeEntitlementValueTypes.Amount, 1_000m)]), default));

        // The stub is not a policy: the company is listed as having none.
        var warned = Json(Assert.IsType<ConflictObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default)).Value, web: true);
        Assert.Contains("companies_without_policy", warned);
        Assert.Contains(h.Company.Id.ToString(), warned);
        Assert.Contains(h.OtherCompany.Id.ToString(), warned);
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true, ConfirmStopOffering: true), default));

        var refused = Assert.IsType<ConflictObjectResult>(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, true), default));
        Assert.Contains("policy_required", Json(refused.Value));
        Assert.False(await h.Db.LoanPolicies.AnyAsync(x => x.IsOffered && x.CompanyId == h.Company.Id));
        var stub = await h.Db.LoanPolicies.SingleAsync(x => x.CompanyId == h.Company.Id);
        Assert.True(stub.IsActive && stub.CreatedByOfferingSwitch && !stub.IsOffered);
        Assert.Contains("LoanTypeNotOffered", (await h.Assess(100m)).Codes);
    }

    [Fact]
    public async Task OffThenOn_FromTheCompanysOwnPolicy_CopiesThatPolicy_NotTheStub()
    {
        await using var h = await H.Create(gradeLimited: false);
        h.Policy.MaxAmount = 3_000m; h.Policy.MaxConcurrentLoans = 2;
        await h.Db.SaveChangesAsync();
        var hr = h.Controller("HR Manager");
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, false), default));
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeOffering(new SetLoanOfferingRequest(h.Company.Id, h.Type.Id, true), default));
        var active = await h.Db.LoanPolicies.SingleAsync(x => x.CompanyId == h.Company.Id && x.IsActive);
        Assert.Equal((3_000m, 2, true, false, (Guid?)null), (active.MaxAmount, active.MaxConcurrentLoans, active.IsOffered, active.CreatedByOfferingSwitch, active.CopiedFromPolicyId));
        Assert.True((await h.Assess(100m)).Eligible);
    }

    [Fact]
    public async Task TenantWideFixedCell_IsBlockedAtEligibility_WhenCompaniesLaterUseDifferentCurrencies()
    {
        await using var h = await H.Create();
        h.Cell(h.G2, amount: 10_000m);
        await h.Db.SaveChangesAsync();
        Assert.True((await h.Assess(1_000m)).Eligible);

        h.OtherCompany.DefaultCurrency = "AED";                           // a company changes currency after publishing
        await h.Db.SaveChangesAsync();
        var blocked = await h.Assess(1_000m);
        Assert.Equal(new[] { GradeLimitCodes.CurrencyAmbiguous }, blocked.Codes);
        Assert.Contains("set this company's own limit", blocked.Reasons.Single());
        Assert.Equal(0m, blocked.Available);

        h.Cell(h.G2, amount: 8_000m, companyId: h.Company.Id);             // the company's own cell resolves it
        await h.Db.SaveChangesAsync();
        var fixedForCompany = await h.Assess(1_000m);
        Assert.True(fixedForCompany.Eligible);
        Assert.Equal(8_000m, fixedForCompany.GradeLimit!.PerLoanCap);
    }

    [Fact]
    public async Task TenantWideSalaryMultiple_StaysValid_AcrossCurrencies()
    {
        await using var h = await H.Create();
        h.OtherCompany.DefaultCurrency = "AED";
        h.Salary(basic: 5_000m);
        h.Cell(h.G2, rate: 2m, valueType: GradeEntitlementValueTypes.MultipleOfBasic);
        await h.Db.SaveChangesAsync();
        var result = await h.Assess(1_000m);
        Assert.True(result.Eligible);
        Assert.Equal(10_000m, result.GradeLimit!.PerLoanCap);
    }

    // ── the real eligibility JSON the UI is built against ────────────────────────────────────────

    /// <summary>
    /// Serialises real GET /eligibility responses (ASP.NET's web JSON options) for every bindingLimit kind and
    /// the blocked / preview / not-offered states, and compares them with
    /// <c>frontend/unit/fixtures/loanEligibilityResponses.json</c>, which the frontend unit specs import. Ids are
    /// normalised. Set KYNEX_WRITE_FIXTURES=1 to regenerate after an intended API change.
    /// </summary>
    [Fact]
    public async Task EligibilityResponses_MatchTheFixtureTheFrontendSpecsUse()
    {
        var responses = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        async Task Capture(string name, Func<H, Task> arrange, decimal? amount, int? installments = 4, bool gradeLimited = true)
        {
            await using var h = await H.Create(gradeLimited);
            await arrange(h);
            await h.Db.SaveChangesAsync();
            var body = Ok(await h.Controller("Employee").GetLoanEligibility(h.Type.Id, amount, installments, ct: default));
            responses[name] = JsonSerializer.SerializeToElement(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        await Capture("GradePerLoan", h => { h.Cell(h.G2, amount: 10_000m); return Task.CompletedTask; }, 5_000m);
        await Capture("GradeOutstanding", h => { h.Cell(h.G2, amount: 10_000m, maxOutstanding: 12_000m); h.Db.Add(h.Loan("Pending", 3_000m)); return Task.CompletedTask; }, 5_000m);
        await Capture("GradeMultipleOfBasic", h => { h.Salary(6_000m); h.Cell(h.G2, rate: 2m, valueType: GradeEntitlementValueTypes.MultipleOfBasic); return Task.CompletedTask; }, 5_000m);
        await Capture("PolicyMaxAmount", h => { h.Policy.MaxAmount = 6_000m; return Task.CompletedTask; }, 5_000m, gradeLimited: false);
        await Capture("PolicyTotalOutstanding", h => { h.Policy.MaxTotalOutstanding = 8_000m; h.Db.Add(h.Loan("Pending", 3_000m)); return Task.CompletedTask; }, 1_000m, gradeLimited: false);
        await Capture("PolicySalaryMultiple", h =>
        {
            h.Salary(12_000m); h.Policy.MaxMultiplierOfSalary = 2m;
            var active = h.Loan("Active", 6_000m); active.OutstandingBalance = 6_000m; active.DisbursementDate = h.Today.AddMonths(-1); h.Db.Add(active);
            return Task.CompletedTask;
        }, 1_000m, gradeLimited: false);
        await Capture("PolicyInstallmentPercent", h => { h.Salary(10_000m); h.Policy.MaxInstallmentPercentOfSalary = 10m; return Task.CompletedTask; }, 1_000m, gradeLimited: false);
        await Capture("PolicyConcurrentLoans", h => { h.Policy.MaxConcurrentLoans = 1; h.Db.Add(h.Loan("Pending", 500m)); return Task.CompletedTask; }, 1_000m, gradeLimited: false);
        await Capture("GradeNotEligible", h => { h.Cell(h.G2, eligible: false); return Task.CompletedTask; }, 1_000m);
        await Capture("GradeSalaryMissing", h => { h.Cell(h.G2, rate: 3m, valueType: GradeEntitlementValueTypes.MultipleOfGross); return Task.CompletedTask; }, 1_000m);
        await Capture("GradeLimitNotConfigured", _ => Task.CompletedTask, 1_000m);
        await Capture("PreviewNoAmount", h => { h.Cell(h.G2, amount: 10_000m, maxOutstanding: 12_000m); return Task.CompletedTask; }, null, null);
        await Capture("LoanTypeNotOffered", h => { h.Cell(h.G2, amount: 10_000m); h.Db.Remove(h.Policy); return Task.CompletedTask; }, 1_000m);

        // The create-400 body the form shows when a submitted request is refused.
        {
            await using var h = await H.Create();
            h.Cell(h.G2, amount: 10_000m);
            await h.Db.SaveChangesAsync();
            var refused = Assert.IsType<BadRequestObjectResult>(await h.Controller("Employee").CreateLoan(h.Request(10_001m), default));
            responses["CreateRefusedGradeLimitPerLoan"] = JsonSerializer.SerializeToElement(refused.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        // GET /types/offered: one offered type, one grade-limited type with no policy, one interest-bearing legacy type.
        {
            await using var h = await H.Create();
            h.Db.AddRange(
                new LoanType { TenantId = h.Tid, Code = "HOME", NameEn = "Home", NameAr = "قرض سكن", MaxInstallments = 24, GradeLimited = true, EntitlementComponentCode = "LOAN_HOME" },
                new LoanType { TenantId = h.Tid, Code = "LEGACY", NameEn = "Legacy", MaxInstallments = 12, IsInterestFree = false, InterestRate = 2m });
            await h.Db.SaveChangesAsync();
            var offered = Ok(await h.Controller("Employee").ListOfferedLoanTypes(null, default));
            responses["TypesOffered"] = JsonSerializer.SerializeToElement(offered, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        var actual = NormaliseIds(JsonSerializer.Serialize(responses, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) + "\n";
        var path = RepoPath("frontend/unit/fixtures/loanEligibilityResponses.json");
        if (Environment.GetEnvironmentVariable("KYNEX_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }
        Assert.True(File.Exists(path), $"Missing {path}. Run this test with KYNEX_WRITE_FIXTURES=1 to create it.");
        Assert.Equal(await File.ReadAllTextAsync(path), actual);

        using var doc = JsonDocument.Parse(actual);
        foreach (var kind in LoanLimitKinds.Order)
            Assert.Equal(kind, doc.RootElement.GetProperty(kind).GetProperty("bindingLimit").GetString());
    }

    private static string NormaliseIds(string json)
    {
        var seen = new Dictionary<string, string>();
        return System.Text.RegularExpressions.Regex.Replace(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            m => seen.TryGetValue(m.Value, out var v) ? v : seen[m.Value] = $"00000000-0000-0000-0000-{seen.Count + 1:D12}");
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions RelaxedWeb = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Json(object? value, bool web = false) => JsonSerializer.Serialize(value, web ? RelaxedWeb : Relaxed);

    private static object Ok(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value!;

    private static string[] Codes(IActionResult result)
    {
        var json = Json(Assert.IsType<BadRequestObjectResult>(result).Value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Codes").EnumerateArray().Select(x => x.GetString()!).ToArray();
    }

    private sealed class H : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid EmployeeUserId { get; } = Guid.NewGuid();
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);
        public Company Company { get; private set; } = null!;
        public Company OtherCompany { get; private set; } = null!;
        public Employee Employee { get; private set; } = null!;
        public LoanType Type { get; private set; } = null!;
        public LoanPolicy Policy { get; private set; } = null!;
        public Grade G1 { get; private set; } = null!;
        public Grade G2 { get; private set; } = null!;
        public Grade G3 { get; private set; } = null!;

        public static async Task<H> Create(bool gradeLimited = true)
        {
            var h = new H();
            h.Company = new Company { TenantId = h.Tid, LegalNameEn = "Grade Co", DefaultCurrency = "SAR" };
            h.OtherCompany = new Company { TenantId = h.Tid, LegalNameEn = "Other Co", DefaultCurrency = "SAR" };
            h.G1 = new Grade { TenantId = h.Tid, Code = "G1", Name = "Grade 1", Level = 1 };
            h.G2 = new Grade { TenantId = h.Tid, Code = "G2", Name = "Grade 2", Level = 2 };
            h.G3 = new Grade { TenantId = h.Tid, Code = "G3", Name = "Grade 3", Level = 3 };
            h.Employee = new Employee { TenantId = h.Tid, CompanyId = h.Company.Id, UserAccountId = h.EmployeeUserId, FullName = "Grade borrower",
                EmployeeCode = "GL1", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), GradeId = h.G2.Id };
            h.Type = new LoanType { TenantId = h.Tid, Code = "PERSONAL", NameEn = "Personal", MaxInstallments = 24,
                GradeLimited = gradeLimited, EntitlementComponentCode = gradeLimited ? "LOAN_PERSONAL" : null };
            h.Policy = new LoanPolicy { TenantId = h.Tid, CompanyId = h.Company.Id, LoanTypeId = h.Type.Id, MaxConcurrentLoans = 10,
                MaxInstallments = 24, PolicyName = "Personal" };
            h.Db.AddRange(h.Company, h.OtherCompany, h.G1, h.G2, h.G3, h.Employee, h.Type, h.Policy);
            await h.Db.SaveChangesAsync();
            return h;
        }

        public GradeEntitlement Cell(Grade grade, bool eligible = true, decimal? amount = null, decimal? rate = null,
            decimal? maxOutstanding = null, string? valueType = null, Guid? companyId = null)
        {
            var cell = new GradeEntitlement
            {
                TenantId = Tid, CompanyId = companyId, GradeId = grade.Id, PayComponentCode = "LOAN_PERSONAL",
                EntitlementClass = PayEntitlementClasses.Facility, Eligible = eligible,
                ValueType = valueType ?? (amount.HasValue ? GradeEntitlementValueTypes.Amount : GradeEntitlementValueTypes.EligibilityOnly),
                Amount = amount, Rate = rate, MaxOutstandingAmount = maxOutstanding, EffectiveFrom = Today.AddDays(-30),
            };
            Db.Add(cell);
            return cell;
        }

        public void Salary(decimal basic, decimal housing = 0, decimal transport = 0, decimal other = 0) =>
            Db.Add(new EmployeeSalaryStructure { TenantId = Tid, EmployeeId = Employee.Id, BasicSalary = basic, HousingAllowance = housing,
                TransportAllowance = transport, OtherAllowance = other, Currency = "SAR", EffectiveDate = Today.AddMonths(-6), IsActive = true });

        public EmployeeLoan Loan(string status, decimal amount) => new()
        {
            TenantId = Tid, CompanyId = Company.Id, EmployeeIntId = Employee.Id, EmployeeId = Employee.PublicId, LoanTypeId = Type.Id,
            LoanNumber = $"LN-{Guid.NewGuid():N}", Status = status, RequestedAmount = amount, ApprovedAmount = amount,
            RequestedInstallments = 4, Currency = "SAR",
        };

        public User AddStaff(string roleName)
        {
            var user = new User { TenantId = Tid, Email = $"{Guid.NewGuid():N}@example.test", IsGroupScope = true };
            var role = new Role { TenantId = Tid, Name = roleName, NormalizedName = roleName.ToUpperInvariant() };
            Db.AddRange(user, role, new UserRole { UserId = user.Id, RoleId = role.Id });
            return user;
        }

        public CreateLoanRequest Request(decimal amount) => new(Employee.PublicId, "", Type.Id, amount, 4, null, Employee.Id);

        public Task<LoanEligibilityAssessment> Assess(decimal amount) =>
            new LoanEligibilityService(Db).EvaluateAsync(Tid, Employee, Type, amount, 4, "BankTransfer");

        public LoansController Controller(string role, Guid[]? companyScope = null)
        {
            var claims = new List<Claim>
            {
                new("tenant_id", Tid.ToString()),
                new(ClaimTypes.NameIdentifier, (role == "Employee" ? EmployeeUserId : Guid.NewGuid()).ToString()),
                new(ClaimTypes.Role, role),
            };
            if (companyScope != null)
                claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = companyScope })));
            return new(Db, new OrgScope())
            {
                ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
            };
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
