using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3, T-021: EOSB provisioning through the REAL Approve and Lock on PostgreSQL with migration M2 (the planner unit test
/// alone proved the arithmetic, not the journal Lock writes). Employees in two cost centres: the 5110 expense is split per cost centre and sums to
/// today's total; 2310 stays uncosted; every snapshot carries its employee's cost centre; no line or key breaks M2's triggers.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class EosbCostedLockPostgresTests
{
    private readonly PostgresFixture _fx;
    public EosbCostedLockPostgresTests(PostgresFixture fx) => _fx = fx;

    private const string Expense = "5110";
    private const string Provision = "2310";

    private async Task<CostingLockKit> KitAsync(short smallCell = 0)
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var k = await CostingLockKit.CreateAsync(true, true, _fx.CreateDb, tenantId);
        await k.AddCc("CC-110", "G_AND_A");
        await k.AddCc("CC-200", "SELLING");
        await k.AddDept("D-FIN", "CC-110");
        await k.AddDept("D-SAL", "CC-200");
        foreach (var (code, dept) in new[] { ("E1", "D-SAL"), ("E2", "D-FIN") })
        {
            var e = await k.AddEmployee(code, dept);
            e.JoiningDate = new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc); // service counts its last day: 1 Jul 2023 to 30 Jun 2026 is exactly three years e.Nationality = "SAU"; e.ContractType = "Indefinite"; e.WorkEmail = $"{code.ToLowerInvariant()}@test.com";
        }
        var company = await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId);
        company.CountryCode = "SAU"; company.Jurisdiction = "KSA-mainland"; company.CostCentreSmallCellMin = smallCell;
        k.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = "SAU", EosbEnabled = true });
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2023, 1, 1), IsActive = true };
        k.Db.SalaryStructures.Add(structure);
        await k.Db.SaveChangesAsync();
        foreach (var code in new[] { "E1", "E2" })
            k.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = k.Emp[code].Id, SalaryStructureId = structure.Id, BasicSalary = 8_000m, HousingAllowance = 2_000m, TransportAllowance = 0m,
                Currency = "SAR", EffectiveDate = new DateOnly(2023, 6, 30), IsActive = true,
            });
        k.Db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tenantId, CompanyId = k.CompanyId, RateKey = EosbProvisioningRateKeys.Mode, RateCategory = "PayParameter", RateValue = EosbProvisioningRateKeys.StatutoryMonthly,
            DataType = "string", Unit = "enum", EffectiveFrom = new DateOnly(2026, 6, 1), Status = CompanyPolicyStatuses.Active,
        });
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();
        return k;
    }

    /// <summary>The real PayrollController with the KSA country pack (so the EOSB obligation is the Art. 84 figure), acting as an Admin.</summary>
    private static PayrollController Ksa(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var ctrl = new PayrollController(db, new DataScopeService(db), new HttpContextAccessor(), new F2NullNotifications(), new EosbKsaResolver(rules), rules,
            new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, "Eosb test"), new(ClaimTypes.Role, "Admin"),
            new("permission", "payroll.read"), new("permission", "payroll.write"), new("permission", "payroll.approve"), new("permission", "payroll.lock"),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return ctrl;
    }

    private static async Task<IActionResult> ApproveThenLockAsync(CostingLockKit k, Guid runId)
    {
        await using (var db = k.K.NewContext())
            (await Ksa(db, k.TenantId, k.ApproverId).Approve(runId, new PayrollDecisionRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        await using var lockDb = k.K.NewContext();
        return await Ksa(lockDb, k.TenantId, k.LockerId).Lock(runId, default);
    }

    [Fact]
    public async Task T021_EosbProvisioningOn_EmployeesInTwoCostCentres_LockSplitsTheExpensePerCostCentre_ThePayableStaysUncosted()
    {
        await using var k = await KitAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 8000m), E("E2", "BASIC", 8000m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 6);

        var res = await ApproveThenLockAsync(k, run.Id);

        res.Should().BeOfType<OkObjectResult>(Json(res).ToString());
        await using var read = k.K.NewContext();
        var lines = await read.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == k.TenantId && l.SourceEntityId == run.Id && l.EventType == GlEventTypes.EosbProvisionAccrual).ToListAsync();

        // exactly three years at half a month of a 10,000 wage each: 15,000 per person, 30,000 in total (the figure the uncosted journal books)
        var expense = lines.Where(l => l.DebitAccount.StartsWith(Expense)).ToList();
        expense.Select(l => (l.CostCenterCode, l.Amount)).OrderBy(x => x.CostCenterCode).Should().Equal(("CC-110", 15_000m), ("CC-200", 15_000m));
        expense.Sum(l => l.Amount).Should().Be(30_000m);
        expense.Should().OnlyContain(l => l.CostCenterId != null && l.PostingKey != null);

        var provision = lines.Where(l => l.CreditAccount.StartsWith(Provision)).ToList();
        provision.Should().HaveCount(2, "one uncosted provision line per employee");
        provision.Should().OnlyContain(l => l.CostCenterId == null && l.CostCenterCode == null);
        provision.Sum(l => l.Amount).Should().Be(30_000m);

        var snapshots = await read.EosbProvisionSnapshots.AsNoTracking().Where(s => s.PayrollRunId == run.Id).ToListAsync();
        snapshots.Should().HaveCount(2);
        snapshots.Single(s => s.EmployeeId == k.Emp["E1"].Id).CostCenterId.Should().Be(k.Cc["CC-200"].Id);
        snapshots.Single(s => s.EmployeeId == k.Emp["E2"].Id).CostCenterId.Should().Be(k.Cc["CC-110"].Id);
        lines.Where(l => l.PostingKey != null).Select(l => l.PostingKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task T021_WithK3_TheEosbExpenseRollsUp_SoNoCostCentreLineIsOnePersonsMovement()
    {
        await using var k = await KitAsync(smallCell: 3);
        await k.SetDefaultCc("CC-110");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 8000m), E("E2", "BASIC", 8000m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 6);

        (await ApproveThenLockAsync(k, run.Id)).Should().BeOfType<OkObjectResult>();

        await using var read = k.K.NewContext();
        var expense = (await read.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == k.TenantId && l.SourceEntityId == run.Id && l.EventType == GlEventTypes.EosbProvisionAccrual && l.DebitAccount.StartsWith(Expense)).ToListAsync());
        expense.Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(new[] { ("CC-110", 30_000m) }); // two people in two one-person cost centres roll to the company default
    }
}

/// <summary>The KSA pack for "SAU" (the code the fixtures' companies carry), so the EOSB obligation is the real Art. 84 figure.</summary>
file sealed class EosbKsaResolver(StubRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => cc is "SAU" or "SA" ? new KsaDeductionCalculator(rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
