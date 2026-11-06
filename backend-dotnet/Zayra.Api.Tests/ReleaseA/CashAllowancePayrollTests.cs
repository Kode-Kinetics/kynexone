using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Step 2, slice A — customer-defined monthly cash allowances paid in salary (<c>ALW_*</c>).
///
/// <para>Two halves. The DEFINITION (spec tests 8 and 9): "Add a benefit" with kind Cash creates a tenant pay component that the
/// engine never pays, with a required GL driver and a reasoned EOSB choice, and a grid row in the Wage group. The PAYROLL RUN
/// (spec tests 1–4): the money comes only from <see cref="EmployeeSalaryAllowance"/> rows on the salary record in force, is
/// prorated like OTHER_ALLOWANCES, never touches the GOSI input, is zero on a supplemental run, and an employee with no rows
/// is paid and posted exactly as before.</para>
/// </summary>
public class CashAllowancePayrollTests
{
    private const string Driver = "EARN:CAR";
    private const string Code = "ALW_CAR";

    // ══ 1. GOLDEN — nobody without an allowance row moves ═══════════════════════════════════════

    [Fact]
    public async Task EmployeesWithoutAllowanceRows_AreSlippedEarnedDeductedAndPostedExactlyAsBefore()
    {
        var plain = await RunAndLockAsync(definition: false, allowances: []);
        var featureOnNobodyHasOne = await RunAndLockAsync(definition: true, allowances: []);

        // The key figures, stated: 13,000 gross (10,000 + 2,000 + 1,000), three earning lines, no allowance anywhere.
        plain.Slips.Should().OnlyContain(s => s.Gross == 13_000m && s.Other == 0m);
        plain.Earnings.Select(e => e.ComponentCode).Distinct().Should().BeEquivalentTo("BASIC", "HOUSING", "TRANSPORT");

        featureOnNobodyHasOne.Slips.Should().Equal(plain.Slips);
        featureOnNobodyHasOne.Earnings.Should().Equal(plain.Earnings);
        featureOnNobodyHasOne.Deductions.Should().Equal(plain.Deductions);
        featureOnNobodyHasOne.Gl.Should().Equal(plain.Gl, "the journal is the same lines for the same money");
    }

    // ══ 2. An allowance of 500 ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnAllowance_IsItsOwnEarningLine_RaisesGrossAndNetByExactlyItself_AndLeavesTheGosiBaseAlone()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, b) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        await AddDefinitionAsync(db, tid);
        await GiveAsync(db, a.Id, Code, 500m);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        await BonusLifecycleGlTests.LockRun(db, PayrollProrationArrearsTests.Ctrl(db, tid), run);

        var slipA = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        var slipB = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == b.Id);
        slipA.GrossSalary.Should().Be(13_500m);
        slipB.GrossSalary.Should().Be(13_000m);
        slipA.OtherAllowances.Should().Be(500m, "the slip header carries it as other pay, like every other non-package earning");
        slipA.YtdGross.Should().Be(13_500m);
        slipA.NetSalary.Should().Be(slipB.NetSalary + 500m, "the allowance is not taxed, deducted from or capped here");
        slipA.EmployeeStatutoryTotal.Should().Be(slipB.EmployeeStatutoryTotal, "the GOSI base is basic + housing, pack-owned, and never sees the allowance");
        slipA.EmployerStatutoryTotal.Should().Be(slipB.EmployerStatutoryTotal);
        slipA.Deductions.Should().Be(slipB.Deductions);

        var lines = await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id && e.EmployeeId == a.Id).ToListAsync();
        var line = lines.Single(e => e.ComponentCode == Code);
        (line.Amount, line.GlDriverKey, line.ComponentName).Should().Be((500m, Driver, "Car allowance"));
        lines.Sum(e => e.Amount).Should().Be(slipA.GrossSalary, "the slip's lines add up to its gross");
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == run.Id && v.Code == "PAYSLIP_LINES_MISMATCH")).Should().BeFalse();
        (await db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == b.Id && e.ComponentCode == Code)).Should().BeFalse();

        // The journal expenses it on the allowance's own driver, and still balances.
        var gl = await BonusLifecycleGlTests.AllGl(db, tid);
        gl.Where(g => g.Description == $"Payroll earning: {Code}").Sum(g => g.Amount).Should().Be(500m);
        BonusLifecycleGlTests.Debits(gl.Where(g => g.Description == $"Payroll earning: {Code}"), "5210").Should().Be(500m);
        BonusLifecycleGlTests.AssertBalanced(gl);
    }

    [Fact]
    public async Task APercentOfBasicAllowance_IsPaidAtTheAmountStoredWhenItWasWritten()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, _) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        await AddDefinitionAsync(db, tid);
        await GiveAsync(db, a.Id, Code, 1_000m, rate: 0.10m);   // 10% of basic 10,000, rounded when written
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await PayrollProrationArrearsTests.Ctrl(db, tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollEarnings.AsNoTracking().SingleAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == a.Id && e.ComponentCode == Code))
            .Amount.Should().Be(1_000m);
    }

    // ══ 3. Joiner mid-month ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AJoinerMidMonth_IsPaidAProratedAllowance_LikeOtherAllowances()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, _) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).JoiningDate = new DateTime(2026, 6, 16);
        await AddDefinitionAsync(db, tid);
        await GiveAsync(db, a.Id, Code, 500m);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await PayrollProrationArrearsTests.Ctrl(db, tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        slip.PaidDays.Should().Be(15);
        var lines = await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id && e.EmployeeId == a.Id).ToListAsync();
        lines.Single(e => e.ComponentCode == Code).Amount.Should().Be(250m, "15 of 30 days");
        lines.Sum(e => e.Amount).Should().Be(slip.GrossSalary);
    }

    [Fact]
    public async Task WhenTheCompanyDoesNotProrateOtherAllowances_TheAllowanceIsPaidInFull()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tid, CompanyId = null, RateKey = ProrationRateKeys.ProratedComponents, RateCategory = "PayParameter",
            RateValue = "BASIC,HOUSING,TRANSPORT,FIXED_DEDUCTION", DataType = "string",
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
        });
        var (companyId, a, _) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).JoiningDate = new DateTime(2026, 6, 16);
        await AddDefinitionAsync(db, tid);
        await GiveAsync(db, a.Id, Code, 500m);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await PayrollProrationArrearsTests.Ctrl(db, tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollEarnings.AsNoTracking().SingleAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == a.Id && e.ComponentCode == Code))
            .Amount.Should().Be(500m, "the company's own proration choice for other allowances governs it");
    }

    // ══ 4. Supplemental run ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ASupplementalRun_PaysNoAllowance()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, _) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        await AddDefinitionAsync(db, tid);
        await GiveAsync(db, a.Id, Code, 500m);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6, runType: PayrollRunTypes.OffCycle);
        run.IncludesRecurringPay = false;
        await db.SaveChangesAsync();
        var ctrl = PayrollProrationArrearsTests.Ctrl(db, tid);
        (await ctrl.UpsertRunSelection(run.Id, new PayrollRunSelectionRequest("Include", "supplemental allowance check", [a.Id]), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == run.Id && e.ComponentCode == Code)).Should().BeFalse();
        (await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id)).GrossSalary.Should().Be(0m);
    }

    // ══ Tax: an allowance is taxed only if its definition says so ═══════════════════════════════

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 50)]
    public async Task AnAllowanceJoinsTheTaxBase_OnlyWhenItsDefinitionIsTaxable(bool taxable, int extraTax)
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.CompanyTaxPolicies.Add(new CompanyTaxPolicy
        {
            TenantId = tid, CompanyId = null, CountryCode = "SA", EffectiveFrom = new DateOnly(2025, 1, 1), IncomeTaxRatePercent = 10m,
        });
        var (companyId, a, b) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        await AddDefinitionAsync(db, tid);
        (await db.PayComponents.SingleAsync(c => c.Code == Code)).IsTaxable = taxable;
        await GiveAsync(db, a.Id, Code, 500m);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await PayrollProrationArrearsTests.Ctrl(db, tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slipA = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        var slipB = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == b.Id);
        slipB.Deductions.Should().BeGreaterThan(0m, "the 10% policy is in force");
        slipA.Deductions.Should().Be(slipB.Deductions + extraTax);
        slipA.NetSalary.Should().Be(slipB.NetSalary + 500m - extraTax);
    }

    // ══ A broken allowance line refuses the run ═════════════════════════════════════════════════

    [Theory]
    [InlineData("missing")]
    [InlineData("no-driver")]
    [InlineData("deleted")]
    public async Task AnAllowanceLineWithoutALiveDefinitionOrGlDriver_RefusesTheRun_NamingTheEmployeeAndCode(string fault)
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, _) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        if (fault != "missing")
        {
            await AddDefinitionAsync(db, tid);
            var definition = await db.PayComponents.SingleAsync(c => c.Code == Code);
            if (fault == "no-driver") definition.GlDriverKey = null; else definition.IsDeleted = true;
        }
        await GiveAsync(db, a.Id, Code, 500m);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        var result = await PayrollProrationArrearsTests.Ctrl(db, tid).Process(run.Id, CancellationToken.None);

        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(422);
        var body = JsonSerializer.Serialize(refusal.Value);
        body.Should().Contain("cash_allowance_definition_invalid").And.Contain(a.EmployeeCode).And.Contain(Code);
        (await db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == run.Id)).Should().BeFalse("nothing is paid or saved");
        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == run.Id)).Should().BeFalse();
    }

    // ══ 8. The definition ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ACashAllowance_IsAnEarningTheEngineNeverPays_WithGosiAndWpsFixed()
    {
        await using var h = await Mx.Create();
        var added = await h.AddCash("Car", driver: Driver);
        Mx.Status(added).Should().Be(200, Mx.Describe(added));

        var row = await h.Db.PayComponents.SingleAsync(x => x.Code == Code);
        (row.ComponentType, row.CalcMethod, row.EntitlementClass, row.StatutoryFloor, row.CompanyId)
            .Should().Be((PayComponentTypes.Earning, PayComponentCalcMethods.GradePackage, PayEntitlementClasses.QiwaWage, PayStatutoryFloors.None, (Guid?)null));
        (row.GosiSubject, row.WpsIncluded, row.IsTaxable, row.EosbIncluded, row.EosbExclusionReason, row.GlDriverKey, row.IsActive, row.IsOffered)
            .Should().Be((false, true, false, true, (string?)null, Driver, true, true));
        row.Value.Should().BeNull();
        row.ProviderKey.Should().BeNull();
        row.StructureField.Should().BeNull();

        // The definition can't be edited through the pay-component API (GOSI and WPS are locked with everything else).
        var update = await new PayComponentsController(h.Db) { ControllerContext = h.Context() }
            .Update(row.Id, new PayComponentsController.UpdateRequest("Hacked", null, "Fixed", 1m, Driver, new DateOnly(2026, 11, 1)), CancellationToken.None);
        Mx.Json(update).GetProperty("error").GetString().Should().Be("grade_package_component");

        // The catalogue emits nothing for it, whatever the run holds.
        var inEffect = PayComponentEngine.ResolveInEffect([row], h.Tid, new DateOnly(2026, 11, 1));
        inEffect.Should().Contain(x => x.Code == Code, "it is a real earning definition, resolved like the rest");
        var computed = PayComponentEngine.Compute(inEffect, new PayComponentContext { Basic = 10_000m, Housing = 2_000m, Transport = 1_000m, Gross = 13_000m });
        computed.Earnings.Should().NotContain(l => l.Code == Code);
        computed.Deductions.Should().NotContain(l => l.Code == Code);
        PayComponentEngine.IsConfiguredValue(row).Should().BeFalse();

        // Nor does the grade pay-package editor (legacy pay scale, which would pay it through OTHER_ALLOWANCES) offer it.
        GradePackageCatalog.IsPackageComponent(row).Should().BeFalse();
    }

    [Fact]
    public async Task ACashAllowance_NeedsAGlDriver_ThatExistsPostsToAnEarningAccountAndIsMapped()
    {
        await using var h = await Mx.Create();
        Mx.Error(await h.AddCash("Car", driver: null)).Should().Be("gl_driver_required");
        Mx.Error(await h.AddCash("Car", driver: "  ")).Should().Be("gl_driver_required");
        Mx.Error(await h.AddCash("Car", driver: "EARN:NOWHERE")).Should().Be("gl_driver_invalid", "it resolves to no account (Unmapped)");
        Mx.Error(await h.AddCash("Car", driver: "EARN:MISSING")).Should().Be("gl_driver_invalid", "it does not exist");
        Mx.Error(await h.AddCash("Car", driver: "DED:UNION")).Should().Be("gl_driver_invalid", "an earning can't post to a deduction driver");
        Mx.Error(await h.AddCash("Car", driver: "DED:STATUTORY_EE")).Should().Be("gl_driver_invalid", "statutory drivers are reserved");
        (await h.Db.PayComponents.AnyAsync(x => x.Code == Code)).Should().BeFalse("nothing is written for a refused allowance");
        (await h.Db.AuditLogs.AnyAsync(a => a.Action == EntitlementMatrixService.AuditBenefitAdded)).Should().BeFalse();

        Mx.Status(await h.AddCash("Car", driver: Driver)).Should().Be(200);
    }

    [Fact]
    public async Task LeavingAnAllowanceOutOfTheEosbWage_NeedsAReason_ThatIsKept()
    {
        await using var h = await Mx.Create();
        Mx.Error(await h.AddCash("Car", driver: Driver, eosb: false, reason: null)).Should().Be("eosb_reason_required");
        Mx.Error(await h.AddCash("Car", driver: Driver, eosb: false, reason: "   ")).Should().Be("eosb_reason_required");
        Mx.Error(await h.AddCash("Car", driver: Driver, eosb: false, reason: new string('x', 501))).Should().Be("eosb_reason_too_long");
        (await h.Db.PayComponents.AnyAsync(x => x.Code == Code)).Should().BeFalse();

        Mx.Status(await h.AddCash("Car", driver: Driver, eosb: false, reason: "  Reimbursement of fuel, not wage  ")).Should().Be(200);
        var row = await h.Db.PayComponents.SingleAsync(x => x.Code == Code);
        (row.EosbIncluded, row.EosbExclusionReason).Should().Be((false, "Reimbursement of fuel, not wage"));
        (await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitAdded)).Metadata
            .Should().Contain("Reimbursement of fuel, not wage").And.Contain(Driver).And.Contain("Cash");

        // A reason given for an allowance that IS in the EOSB wage means nothing, and is not kept.
        Mx.Status(await h.AddCash("Phone Allowance", driver: Driver, eosb: true, reason: "ignored")).Should().Be(200);
        (await h.Db.PayComponents.SingleAsync(x => x.Code == "ALW_PHONE_ALLOWANCE")).EosbExclusionReason.Should().BeNull();
    }

    [Fact]
    public async Task TheKindIsContractOrCash_AndAContractBenefitIsUnchanged()
    {
        await using var h = await Mx.Create();
        Mx.Error(await h.Add(new CreateBenefitRequest("Gym", null, "Bonus", Driver))).Should().Be("kind_invalid");

        Mx.Status(await h.Add(new CreateBenefitRequest("Parking", null))).Should().Be(200);
        Mx.Status(await h.Add(new CreateBenefitRequest("Gym", null, BenefitKinds.Contract, Driver, false, "ignored"))).Should().Be(200);
        var gym = await h.Db.PayComponents.SingleAsync(x => x.Code == "BEN_GYM");
        (gym.ComponentType, gym.CalcMethod, gym.GlDriverKey, gym.EosbIncluded, gym.EosbExclusionReason, gym.WpsIncluded, gym.EntitlementClass)
            .Should().Be((PayComponentTypes.Benefit, PayComponentCalcMethods.Fixed, (string?)null, false, (string?)null, false, PayEntitlementClasses.Contractual),
                "a contract benefit takes nothing from the cash fields");
    }

    [Fact]
    public async Task NamesAreUniqueAcrossContractBenefitsAndCashAllowances_AndAnArabicOnlyNameGetsAnAlwCode()
    {
        await using var h = await Mx.Create();
        Mx.Status(await h.Add(new CreateBenefitRequest("Parking", null))).Should().Be(200);
        Mx.Error(await h.AddCash("parking", driver: Driver)).Should().Be("benefit_exists", "a contract benefit already has the name");

        Mx.Status(await h.AddCash("Car Allowance", driver: Driver)).Should().Be(200);
        Mx.Error(await h.Add(new CreateBenefitRequest("Car allowance", null))).Should().Be("benefit_exists", "a cash allowance already has the name");
        Mx.Error(await h.AddCash("car-allowance", driver: Driver)).Should().Be("benefit_exists");
        (await h.Db.PayComponents.CountAsync(x => x.Code == "ALW_CAR_ALLOWANCE")).Should().Be(1);

        // Another pay component (not an ALW_ allowance) already on the code is a clash too.
        h.Db.PayComponents.Add(new PayComponent { TenantId = h.Tid, CompanyId = null, Code = "ALW_TAKEN", NameEn = "Taken", NameAr = "Taken", ComponentType = PayComponentTypes.Earning, CalcMethod = PayComponentCalcMethods.Fixed, Value = 5m });
        await h.Db.SaveChangesAsync();
        Mx.Error(await h.AddCash("Taken", driver: Driver)).Should().Be("benefit_exists");

        Mx.Status(await h.AddCash(null, nameAr: "بدل سيارة", driver: Driver)).Should().Be(200);
        var arabic = await h.Db.PayComponents.SingleAsync(x => x.NameAr == "بدل سيارة");
        arabic.Code.Should().MatchRegex("^ALW_[0-9A-F]{8}$");
    }

    // ══ 9. The grid (the part of it slice A owns) ═══════════════════════════════════════════════

    [Fact]
    public async Task ACashAllowanceIsAWageRowInTheGrid_AmountOrPercent_Monthly_NeverSkippablePerCompany()
    {
        await using var h = await Mx.Create();
        Mx.Status(await h.AddCash("Car", driver: Driver)).Should().Be(200);

        var matrix = await h.Read(null, Mx.Today);
        var component = matrix.Components.Single(c => c.Code == Code);
        (component.Group, component.Class, component.Floor, component.IsCustom, component.CanBeSkipped, component.IsFloor, component.IsLoanFacility)
            .Should().Be((EntitlementMatrixService.Groups.Wage, PayEntitlementClasses.QiwaWage, PayStatutoryFloors.None, true, false, false, false));
        component.AllowedValueTypes.Should().BeEquivalentTo(
            [GradeEntitlementValueTypes.Amount, GradeEntitlementValueTypes.PercentOfBasic], o => o.WithStrictOrdering());
        component.AllowedLimitPeriods.Should().Equal(EntitlementLimitPeriods.Monthly);
        component.DefaultLimitPeriod.Should().Be(EntitlementLimitPeriods.Monthly);
        component.AllowedDependantScopes.Should().Equal(DependantScopes.None);
        component.AllowedCoverageTiers.Should().BeEmpty();

        // Amount, percent and "Not offered" publish; in kind (and the like) does not.
        var done = await h.PublishGroup(Mx.Nov1, [Mx.Amount(h.G[1], Code, 500m), Mx.Pct(h.G[2], Code, 0.10m), Mx.NotOffered(h.G[3], Code)]);
        Mx.Status(done).Should().Be(200, Mx.Describe(done));
        var cells = await h.Db.GradeEntitlements.Where(x => x.PayComponentCode == Code).ToListAsync();
        cells.Should().HaveCount(3);
        cells.Should().OnlyContain(c => c.EntitlementClass == PayEntitlementClasses.QiwaWage);
        cells.Single(c => c.GradeId == h.G[1].Id).LimitPeriod.Should().Be(EntitlementLimitPeriods.Monthly);
        Mx.CellErrors(await h.PublishGroup(Mx.Nov1, [Mx.InKind(h.G[4], Code)])).Single().Code.Should().Be("value_type_not_allowed");
        Mx.CellErrors(await h.PublishGroup(Mx.Nov1, [Mx.Amount(h.G[4], Code, 0m)])).Single().Code.Should().Be("invalid_value");
        Mx.CellErrors(await h.PublishGroup(Mx.Nov1, [Mx.Pct(h.G[4], Code, 1.5m)])).Single().Code.Should().Be("invalid_value");
        Mx.CellErrors(await h.PublishGroup(Mx.Nov1, [Mx.Amount(h.G[4], "ALW_GHOST", 5m)])).Single().Code.Should().Be("unknown_component", "no definition, no values");

        // A company can't stop offering it: it is paid wage, so "Not offered" per grade is how it is switched off.
        Mx.Error(await h.SetOffering(h.Facility, Code, offered: false, new DateOnly(2026, 12, 1))).Should().Be("wage_component_not_skippable");
    }

    [Fact]
    public async Task AnAllowanceThatNobodyUses_CanBeRemoved_ButNotOneWithGradeValuesOrOnASalaryRecord()
    {
        await using var h = await Mx.Create();
        Mx.Status(await h.AddCash("Car", driver: Driver)).Should().Be(200);
        Mx.Status(await h.Remove(Code.ToLowerInvariant())).Should().Be(200);
        (await h.Read(null, Mx.Today)).Components.Should().NotContain(c => c.Code == Code);
        (await h.Db.PayComponents.IgnoreQueryFilters().SingleAsync(x => x.Code == Code)).IsDeleted.Should().BeTrue();

        // The same name brings the row back (the unique key counts removed rows), still a cash allowance.
        Mx.Status(await h.AddCash("Car", driver: Driver)).Should().Be(200);
        (await h.Db.PayComponents.CountAsync(x => x.Code == Code)).Should().Be(1);
        (await h.Db.PayComponents.SingleAsync(x => x.Code == Code)).IsDeleted.Should().BeFalse();

        Mx.Status(await h.PublishGroup(Mx.Nov1, [Mx.NotOffered(h.G[1], Code)])).Should().Be(200);
        Mx.Error(await h.Remove(Code)).Should().Be("benefit_in_use");

        Mx.Status(await h.AddCash("Phone Allowance", driver: Driver)).Should().Be(200);
        h.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance { TenantId = h.Tid, SalaryStructureId = Guid.NewGuid(), Code = "ALW_PHONE_ALLOWANCE", Amount = 50m });
        await h.Db.SaveChangesAsync();
        Mx.Error(await h.Remove("ALW_PHONE_ALLOWANCE")).Should().Be("benefit_in_use", "an employee's salary carries it");
    }

    [Fact]
    public async Task AnAllowanceThatAPendingSalaryChangeProposes_CannotBeRemoved_UntilTheChangeIsDecided()
    {
        await using var h = await Mx.Create();
        Mx.Status(await h.AddCash("Car", driver: Driver)).Should().Be(200);
        Mx.Status(await h.AddCash("Phone Allowance", driver: Driver)).Should().Be(200);
        var change = new SalaryChangeRequest
        {
            TenantId = h.Tid, CompanyId = h.Facility.Id, EmployeeId = 1, EmployeeCode = "E1", EmployeeName = "E", SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 10_000m, EffectiveDate = new DateOnly(2026, 11, 1), Currency = "SAR", Status = SalaryChangeRequest.Pending,
            AllowancesJson = "[{\"code\":\"ALW_CAR\",\"basis\":\"Amount\",\"amount\":500}]",
        };
        h.Db.SalaryChangeRequests.Add(change);
        // A pending change that does not mention allowances (carry forward) or states none names nothing, so it blocks nothing.
        h.Db.SalaryChangeRequests.Add(new SalaryChangeRequest
        {
            TenantId = h.Tid, CompanyId = h.Facility.Id, EmployeeId = 2, EmployeeCode = "E2", EmployeeName = "E", SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 10_000m, EffectiveDate = new DateOnly(2026, 11, 1), Currency = "SAR", Status = SalaryChangeRequest.Pending, AllowancesJson = "[]",
        });
        await h.Db.SaveChangesAsync();

        var refused = await h.Remove(Code.ToLowerInvariant());
        Mx.Error(refused).Should().Be("benefit_in_use");
        Mx.Describe(refused).Should().Contain("waiting for approval");
        (await h.Db.PayComponents.SingleAsync(x => x.Code == Code)).IsDeleted.Should().BeFalse();

        Mx.Status(await h.Remove("ALW_PHONE_ALLOWANCE")).Should().Be(200, "no pending change names it");

        // Once the change is decided it no longer holds the allowance.
        (await h.Db.SalaryChangeRequests.SingleAsync(x => x.Id == change.Id)).Status = SalaryChangeRequest.Rejected;
        await h.Db.SaveChangesAsync();
        Mx.Status(await h.Remove(Code)).Should().Be(200);
    }

    // ══ Least-privilege GL driver list for "Add a benefit → cash allowance" ═════════════════════

    [Fact]
    public async Task TheCashGlDriverList_MarksEachEarningDriver_ByTheRuleAddingACashAllowanceApplies()
    {
        await using var h = await Mx.Create();
        // Beyond the harness's mapped / unmapped drivers: an employer-expense pair, a driver on a control account, and an inactive one.
        h.Db.GlDrivers.AddRange(
            new GlDriver
            {
                TenantId = h.Tid, CompanyId = null, Key = "EARN:PAIR", Label = "Employer pair", Category = GlDriverCategories.Earning, PostingSide = "DR",
                DefaultCode = "5300", DefaultName = "Pair", MatchMode = "Exact", MatchComponentCode = "PAIR", EmitsEmployerExpensePair = true, SortOrder = 71,
            },
            new GlDriver
            {
                TenantId = h.Tid, CompanyId = null, Key = "EARN:CONTROL", Label = "Posts to a control account", Category = GlDriverCategories.Earning, PostingSide = "DR",
                DefaultCode = "2101", DefaultName = "Social insurance payable", MatchMode = "Exact", MatchComponentCode = "CONTROL", SortOrder = 72,
            },
            new GlDriver
            {
                TenantId = h.Tid, CompanyId = null, Key = "EARN:OFF", Label = "Switched off", Category = GlDriverCategories.Earning, PostingSide = "DR",
                DefaultCode = "5400", DefaultName = "Off", MatchMode = "Exact", MatchComponentCode = "OFF", IsActive = false, SortOrder = 73,
            });
        await h.Db.SaveChangesAsync();

        var response = await new EntitlementMatrixController { ControllerContext = h.Context() }.CashGlDrivers(h.Service, default);

        var list = JsonSerializer.SerializeToElement(((OkObjectResult)response).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).EnumerateArray().ToList();
        string[] Keys(bool usable) => list.Where(d => d.GetProperty("usable").GetBoolean() == usable).Select(d => d.GetProperty("key").GetString()!).ToArray();
        list.Select(d => d.GetProperty("key").GetString()).Should().NotContain(["DED:UNION", "EARN:OFF"], "only active earning drivers are candidates");
        Keys(true).Should().Contain(["EARN:CAR", "EARN:BASIC", "EARN:OTHER"]).And.NotContain(["EARN:NOWHERE", "EARN:PAIR", "EARN:CONTROL"]);
        Keys(false).Should().BeEquivalentTo("EARN:NOWHERE", "EARN:PAIR", "EARN:CONTROL");
        list.Single(d => d.GetProperty("key").GetString() == "EARN:CAR").GetProperty("label").GetString().Should().Be("EARN:CAR");
        list.Single(d => d.GetProperty("key").GetString() == "EARN:NOWHERE").GetProperty("reasonCode").GetString().Should().Be("unmapped");
        list.Single(d => d.GetProperty("key").GetString() == "EARN:PAIR").GetProperty("reasonCode").GetString().Should().Be("employer_pair");
        list.Single(d => d.GetProperty("key").GetString() == "EARN:CAR").GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);

        // No chart of accounts leaks: no account number or name anywhere in the answer.
        var text = JsonSerializer.Serialize(((OkObjectResult)response).Value);
        text.Should().NotContain("5210").And.NotContain("2101").And.NotContain("Account 5210").And.NotContain("Social insurance payable");

        // The list and the rule agree: every usable driver is accepted by "Add a benefit"; every refused one is refused.
        var n = 0;
        foreach (var key in Keys(true))
            Mx.Status(await h.AddCash($"Probe {n++}", driver: key)).Should().Be(200, key);
        foreach (var key in Keys(false))
            Mx.Error(await h.AddCash($"Probe {n++}", driver: key)).Should().Be("gl_driver_invalid", key);
    }

    [Fact]
    public async Task AddingACashAllowance_NeedsAccessToEveryCompany()
    {
        await using var h = await Mx.Create();
        var oneCompany = new EntitlementMatrixController { ControllerContext = h.Context([h.Facility.Id]) };
        Mx.Error(await oneCompany.AddBenefit(h.Service, new CreateBenefitRequest("Car", null, BenefitKinds.Cash, Driver), default))
            .Should().Be("group_scope_required");
        (await h.Db.PayComponents.AnyAsync(x => x.Code == Code)).Should().BeFalse();
    }

    [Fact]
    public async Task AnAllowanceIsNotInTheEmployeesContractPackage_UntilItHasItsOwnSlice()
    {
        // The package view reads wage lines from the salary row; for a code it doesn't know it would show the OTHER pay figure
        // against the allowance. It stays out until the allowance has its own line (a later slice).
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"alw-package-{Guid.NewGuid():N}").Options);
        await using var _ = db;
        var seed = new PackageSeed();
        seed.Cell(Code, PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.Amount, amount: 500m, period: EntitlementLimitPeriods.Monthly);
        await seed.SaveAsync(db);
        db.PayComponents.Add(new PayComponent
        {
            TenantId = seed.TenantId, CompanyId = null, Code = Code, NameEn = "Car allowance", NameAr = "بدل سيارة", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, EntitlementClass = PayEntitlementClasses.QiwaWage, GlDriverKey = Driver,
        });
        await db.SaveChangesAsync();

        var package = await new EntitlementResolver(db).ResolveAsync(seed.TenantId, seed.Mohammed.Id, new DateOnly(2026, 10, 6), default);

        package.Lines.Should().NotContain(l => l.ComponentCode == Code);
        package.Lines.Should().Contain(l => l.ComponentCode == "HOUSING");
    }

    // ══ harness: payroll ════════════════════════════════════════════════════════════════════════

    private sealed record SlipFigures(string Employee, decimal Basic, decimal Housing, decimal Transport, decimal Other, decimal Gross,
        decimal Deductions, decimal Net, decimal EeStatutory, decimal ErStatutory, decimal YtdGross, decimal Loans);
    private sealed record EarningLine(string Employee, string ComponentCode, string ComponentName, decimal Amount, string Source, string? GlDriverKey);
    private sealed record DeductionLine(string Employee, string ComponentCode, decimal Amount, string Source, bool Employer);
    private sealed record GlLine(string EventType, string Debit, string Credit, decimal Amount, string Description);
    private sealed record Outcome(List<SlipFigures> Slips, List<EarningLine> Earnings, List<DeductionLine> Deductions, List<GlLine> Gl);

    /// <summary>Process and lock a June run for the two seeded employees; <paramref name="allowances"/> are given to the first one.</summary>
    private static async Task<Outcome> RunAndLockAsync(bool definition, (string Code, decimal Amount)[] allowances)
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, _) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        if (definition) await AddDefinitionAsync(db, tid);
        foreach (var (code, amount) in allowances) await GiveAsync(db, a.Id, code, amount);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();
        await BonusLifecycleGlTests.LockRun(db, PayrollProrationArrearsTests.Ctrl(db, tid), run);

        var codes = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.EmployeeCode);
        return new Outcome(
            (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync())
                .Select(s => new SlipFigures(codes[s.EmployeeId], s.BasicSalary, s.HousingAllowance, s.TransportAllowance, s.OtherAllowances, s.GrossSalary,
                    s.Deductions, s.NetSalary, s.EmployeeStatutoryTotal, s.EmployerStatutoryTotal, s.YtdGross, s.LoanDeductions))
                .OrderBy(s => s.Employee).ToList(),
            (await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id).ToListAsync())
                .Select(e => new EarningLine(codes[e.EmployeeId], e.ComponentCode, e.ComponentName, e.Amount, e.Source, e.GlDriverKey))
                .OrderBy(e => e.Employee).ThenBy(e => e.ComponentCode, StringComparer.Ordinal).ToList(),
            (await db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == run.Id).ToListAsync())
                .Select(d => new DeductionLine(codes[d.EmployeeId], d.ComponentCode, d.Amount, d.Source, d.IsEmployerContribution))
                .OrderBy(d => d.Employee).ThenBy(d => d.ComponentCode, StringComparer.Ordinal).ToList(),
            (await BonusLifecycleGlTests.AllGl(db, tid))
                .Select(g => new GlLine(g.EventType, g.DebitAccount, g.CreditAccount, g.Amount, g.Description))
                .OrderBy(g => g.Description, StringComparer.Ordinal).ThenBy(g => g.Debit, StringComparer.Ordinal).ThenBy(g => g.Credit, StringComparer.Ordinal)
                .ThenBy(g => g.Amount).ToList());
    }

    /// <summary>The ALW_CAR definition exactly as <c>CreateBenefitAsync</c> writes it, and the earning driver it posts to (account 5210).</summary>
    private static async Task AddDefinitionAsync(ZayraDbContext db, Guid tid, string code = Code, string driver = Driver, string name = "Car allowance")
    {
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = driver, Label = name, Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = "5210", DefaultName = "Car Allowance Expense", MatchMode = "Exact", MatchComponentCode = code, SortOrder = 60,
        });
        db.PayComponents.Add(new PayComponent
        {
            TenantId = tid, CompanyId = null, Code = code, NameEn = name, NameAr = name, ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = driver, GosiSubject = false, WpsIncluded = true, IsTaxable = false, EosbIncluded = true, DisplayOrder = 9100,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>An allowance on the employee's salary record (its row, not the template).</summary>
    private static async Task GiveAsync(ZayraDbContext db, int employeeId, string code, decimal amount, decimal? rate = null)
    {
        var salary = await db.EmployeeSalaryStructures.FirstAsync(x => x.EmployeeId == employeeId);
        db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance
        {
            TenantId = salary.TenantId, SalaryStructureId = salary.Id, Code = code, Amount = amount,
            Basis = rate is null ? AllowanceBases.Amount : AllowanceBases.PercentOfBasic, Rate = rate,
        });
        await db.SaveChangesAsync();
    }

    // ══ harness: the matrix ═════════════════════════════════════════════════════════════════════

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private sealed class Mx : IAsyncDisposable
    {
        public static readonly DateOnly Today = new(2026, 10, 6);
        public static readonly DateOnly Nov1 = new(2026, 11, 1);

        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Company Facility { get; private set; } = null!;
        public Grade[] G { get; } = new Grade[6];
        public EntitlementMatrixService Service { get; private set; } = null!;

        public static async Task<Mx> Create()
        {
            var h = new Mx();
            h.Facility = new Company { TenantId = h.Tid, LegalNameEn = "Masar Facility Services Co.", DefaultCurrency = "SAR" };
            var logistics = new Company { TenantId = h.Tid, LegalNameEn = "Masar Logistics Co.", DefaultCurrency = "SAR" };
            for (var i = 1; i <= 5; i++) h.G[i] = new Grade { TenantId = h.Tid, Code = $"G{i}", Name = $"Grade {i}", Level = i * 10 };
            h.Db.AddRange(h.Facility, logistics);
            h.Db.AddRange(h.G.Skip(1));
            h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = h.Tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
            // The posting drivers an allowance may name: a mapped earning driver, an unmapped one, a deduction driver.
            h.Db.GlDrivers.AddRange(
                Driver_(h.Tid, "EARN:CAR", "5210", GlDriverCategories.Earning),
                Driver_(h.Tid, "EARN:NOWHERE", "", GlDriverCategories.Earning),
                Driver_(h.Tid, "DED:UNION", "2150", GlDriverCategories.Deduction));
            await h.Db.SaveChangesAsync();
            h.Service = new EntitlementMatrixService(h.Db, new FixedClock(Today));
            return h;
        }

        private static GlDriver Driver_(Guid tid, string key, string account, string category) => new()
        {
            TenantId = tid, CompanyId = null, Key = key, Label = key, Category = category, PostingSide = category == GlDriverCategories.Earning ? "DR" : "CR",
            DefaultCode = account, DefaultName = account.Length == 0 ? "" : $"Account {account}", MatchMode = "Exact", MatchComponentCode = key[(key.IndexOf(':') + 1)..], SortOrder = 70,
        };

        public ControllerContext Context(Guid[]? companyScope = null)
        {
            var claims = new List<Claim>
            {
                new("tenant_id", Tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "HR Director"),
            };
            if (companyScope != null)
                claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = companyScope })));
            return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
        }

        private EntitlementMatrixController Controller() => new() { ControllerContext = Context() };

        public Task<IActionResult> Add(CreateBenefitRequest req) => Controller().AddBenefit(Service, req, default);

        public Task<IActionResult> AddCash(string? nameEn, string? driver, string? nameAr = null, bool eosb = true, string? reason = null) =>
            Add(new CreateBenefitRequest(nameEn, nameAr, BenefitKinds.Cash, driver, eosb, reason));

        public Task<IActionResult> Remove(string code) => Controller().RemoveBenefit(Service, code, default);

        public Task<IActionResult> PublishGroup(DateOnly from, List<MatrixCellInput> cells) =>
            Controller().Publish(Service, new PublishMatrixRequest(null, from, cells), false, default);

        public Task<IActionResult> SetOffering(Company company, string code, bool offered, DateOnly from) =>
            Controller().SetOffering(Service, new SetOfferingRequest(company.Id, code, offered, from), default);

        public async Task<EntitlementMatrixDto> Read(Guid? companyId, DateOnly asOf) =>
            (EntitlementMatrixDto)((OkObjectResult)await Controller().Matrix(Service, companyId, asOf, default)).Value!;

        public static MatrixCellInput Amount(Grade g, string code, decimal amount) =>
            new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "Amount", Amount = amount };
        public static MatrixCellInput Pct(Grade g, string code, decimal rate) =>
            new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "PercentOfBasic", Rate = rate };
        public static MatrixCellInput InKind(Grade g, string code) => new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "InKind" };
        public static MatrixCellInput NotOffered(Grade g, string code) => new() { GradeId = g.Id, ComponentCode = code, Eligible = false };

        public static int? Status(IActionResult result) => result switch
        {
            ObjectResult o => o.StatusCode,
            StatusCodeResult s => s.StatusCode,
            _ => null,
        };

        public static string Describe(IActionResult result) => result is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value)}" : result.GetType().Name;

        public static JsonElement Json(IActionResult result) =>
            JsonSerializer.SerializeToElement(((ObjectResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        public static string? Error(IActionResult result) => Json(result).GetProperty("error").GetString();

        public static List<MatrixCellError> CellErrors(IActionResult result)
        {
            Status(result).Should().Be(400, Describe(result));
            var errors = Json(result).TryGetProperty("errors", out var e) ? e.GetRawText() : "[]";
            return JsonSerializer.Deserialize<List<MatrixCellError>>(errors, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
