using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>The other readers of a held-back allowance: back pay, held pay, a company's own allowance widened while its change waits, and the approval
/// check after a confirmed correction.</summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    [Fact]
    public async Task BackPayUnderAPendingCorrection_PostsOnTheLastConfirmedDriver()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        foreach (var month in new[] { 1, 2 })
        {
            var locked = await w.RunAsync(2026, month);
            (await w.Process(locked)).Should().BeOfType<OkObjectResult>();
            await w.MarkAsync(locked, "Locked");
        }
        // A one-off Jan-Feb allowance on a backdated record, and a record from March without it: March pays the back pay.
        var structureId = (await w.Db.SalaryStructures.AsNoTracking().FirstAsync()).Id;
        var backdated = new EmployeeSalaryStructure { TenantId = w.Tid, EmployeeId = w.A.Id, SalaryStructureId = structureId, BasicSalary = 10_000m, HousingAllowance = 2_000m,
            TransportAllowance = 1_000m, EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 3, 1) };
        var current = new EmployeeSalaryStructure { TenantId = w.Tid, EmployeeId = w.A.Id, SalaryStructureId = structureId, BasicSalary = 10_000m, HousingAllowance = 2_000m,
            TransportAllowance = 1_000m, EffectiveDate = new DateOnly(2026, 3, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 3, 1, 0, 0, 1) };
        w.Db.EmployeeSalaryStructures.AddRange(backdated, current);
        w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance { TenantId = w.Tid, SalaryStructureId = backdated.Id, Code = Car, Amount = 700m });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car, new CorrectAllowanceRequest(new DateOnly(2026, 3, 1), "Re-route", GlDriverKey: "EARN:CAR2"), default))
            .Should200("pending from March");

        var march = await w.RunAsync(2026, 3);
        (await w.Process(march)).Should().BeOfType<OkObjectResult>();

        var lines = await w.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == march.Id && e.EmployeeId == w.A.Id && e.Source == "Arrears").ToListAsync();
        lines.Should().HaveCount(2).And.OnlyContain(l => l.ComponentCode == "ARREARS_ALW_CAR" && l.GlDriverKey == CarDriver,
            "back pay posts on the driver payroll pays this month, which is the last confirmed one until the correction is confirmed");
    }

    [Fact]
    public async Task HeldPayUnderAPendingSwitchOff_HoldsTheAllowance_AsTheLastConfirmedTermsPayIt()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        (await w.Db.Employees.SingleAsync(e => e.Id == w.A.Id)).Status = "Suspended";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));

        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();

        var hold = await w.Db.PayrollAdjustments.AsNoTracking().SingleAsync(a => a.SourceType == HeldPay.HoldSource && a.EmployeeId == w.A.Id);
        hold.Amount.Should().Be(13_500m, "10,000 + 2,000 + 1,000, and the 500 allowance on its last confirmed terms");
    }

    [Fact]
    public async Task ACompanysOwnAllowanceWidenedWhileItsChangeWaits_KeepsPayingItsOwnCompany_AndIsPendingForTheOthers()
    {
        await using var w = await World.Create();
        var other = new Company { TenantId = w.Tid, LegalNameEn = "Sister Co", CountryCode = "SAU", IsActive = true, DefaultCurrency = "SAR" };
        w.Db.Companies.Add(other);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var own = Component(await w.As(w.Hr).AddBenefit(w.Service, Contract("Car", b =>
        {
            b.Code = "CAR"; b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual";
            b.CompanyId = w.CompanyId;
        }), default));
        Status(await w.As(w.FinanceA, finance: true).ConfirmBenefit(w.Service, own.Code, null, default)).Should200("confirmed for its company");
        var salary = await w.Db.EmployeeSalaryStructures.FirstAsync(x => x.EmployeeId == w.A.Id);
        w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance { TenantId = w.Tid, SalaryStructureId = salary.Id, Code = own.Code, Amount = 400m, Basis = AllowanceBases.Amount });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var version = ((EntitlementMatrixDto)((OkObjectResult)await w.As(w.Hr).Matrix(w.Service, w.CompanyId, Bd.Today, default)).Value!).Components.Single(c => c.Code == own.Code).Definition!.Version;

        Component(await w.As(w.Hr).UpdateBenefit(w.Service, own.Code, new UpdateBenefitRequest(version, "Car", null) { Scope = new BenefitScopeInput([w.CompanyId, other.Id]) }, default))
            .CashAllowance!.FinancePending.Should().BeTrue("widening is a money change Finance confirms");

        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>("its own company keeps paying the terms Finance confirmed for it");
        (await w.Db.PayrollEarnings.AsNoTracking().SingleAsync(e => e.PayrollRunId == june.Id && e.ComponentCode == own.Code)).Amount.Should().Be(400m);
        var forSister = CashAllowanceDefinitions.InEffect((await CashAllowanceDefinitions.VersionsAsync(w.Db, w.Tid, [own.Code], default, companyId: other.Id, payable: true))[own.Code], June1);
        forSister!.FinanceConfirmedAtUtc.Should().BeNull("the kept terms were confirmed for one company only; for the others the widening waits for Finance");
    }

    [Fact]
    public async Task ARunProcessedOnHeldBackTerms_IsStaleOnceTheCorrectionIsConfirmed()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car, new CorrectAllowanceRequest(June1, "Re-route", GlDriverKey: "EARN:CAR2"), default)).Should200("");
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        var refused = await ApproveAsync(w, june);

        refused.Should().BeOfType<ConflictObjectResult>(Describe(refused));
        ErrorOf(refused).Should().Be("payroll_run_stale_definition");
    }
}
