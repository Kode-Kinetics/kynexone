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
    public async Task MissingCell_BlocksWithTheConfigReason_AndNotifiesHrOnce()
    {
        await using var h = await H.Create();
        h.Cell(h.G1, amount: 1_000m);                                 // G2 (the employee's grade) has no cell
        var hrUser = h.AddStaff("HR Manager");
        await h.Db.SaveChangesAsync();

        var refused = await h.Controller("Employee").CreateLoan(h.Request(500m), default);
        Assert.Contains(GradeLimitCodes.NotConfigured, Codes(refused));
        var body = Json(((BadRequestObjectResult)refused).Value);
        Assert.Contains("HR has been notified", body);
        await h.Controller("Employee").GetLoanEligibility(h.Type.Id, 500m, 4, ct: default);
        var notes = await h.Db.Notifications.Where(x => x.UserId == hrUser.Id).ToListAsync();
        Assert.Single(notes);                                         // de-duplicated while unread
        Assert.Equal("LoanType", notes[0].EntityName);
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
        Assert.IsType<OkObjectResult>(await hr.SetLoanTypeGradeLimited(h.Type.Id, new SetGradeLimitedRequest(true), default));

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
