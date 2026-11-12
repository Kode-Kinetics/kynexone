using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The annual-leave provision through the REAL Approve and Lock on PostgreSQL with the cost-accounting triggers:
/// the expense is split per cost centre and sums to the uncosted total, the provision lines stay uncosted, the
/// ERP export carries the lines (and no employee identifier) and ties out to the ledger, and the database itself
/// refuses a second measurement of the same employee and month.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LeaveProvisionPostgresTests
{
    private readonly PostgresFixture _fx;
    public LeaveProvisionPostgresTests(PostgresFixture fx) => _fx = fx;

    private const string Expense = "5115";
    private const string Provision = "2330";

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
            e.JoiningDate = new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc); e.Nationality = "SAU"; e.ContractType = "Indefinite"; e.WorkEmail = $"{code.ToLowerInvariant()}@test.com";
        }
        var company = await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId);
        company.CountryCode = "SAU"; company.Jurisdiction = "KSA-mainland"; company.CostCentreSmallCellMin = smallCell;
        k.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = "SAU", EosbEnabled = true });
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2023, 1, 1), IsActive = true };
        k.Db.SalaryStructures.Add(structure);
        var annual = new LeaveType { TenantId = tenantId, CompanyId = k.CompanyId, Code = "ANNUAL", NameEn = "Annual leave", Category = "Annual", IsPaid = true, IsActive = true };
        k.Db.LeaveTypes.Add(annual);
        await k.Db.SaveChangesAsync();
        // E1 (sales): 12 unused days at 9,000 / 30 = 300 -> 3,600.  E2 (finance): 5 days at 300 -> 1,500.
        foreach (var (code, days) in new[] { ("E1", 12m), ("E2", 5m) })
        {
            k.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = k.Emp[code].Id, SalaryStructureId = structure.Id, BasicSalary = 6_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
                Currency = "SAR", EffectiveDate = new DateOnly(2023, 6, 30), IsActive = true,
            });
            k.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId, EmployeeId = k.Emp[code].Id, EmployeeName = k.Emp[code].FullName, LeaveTypeId = annual.Id, LeaveTypeName = annual.NameEn, Year = 2026, Accrued = days,
            });
        }
        k.Db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tenantId, CompanyId = k.CompanyId, RateKey = LeaveProvisioningRateKeys.Mode, RateCategory = "PayParameter", RateValue = LeaveProvisioningRateKeys.BalanceMonthly,
            DataType = "string", Unit = "enum", EffectiveFrom = new DateOnly(2026, 6, 1), Status = CompanyPolicyStatuses.Active,
        });
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();
        return k;
    }

    private static PayrollController Ksa(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var ctrl = new PayrollController(db, new DataScopeService(db), new HttpContextAccessor(), new F2NullNotifications(), new LeaveKsaResolver(rules), rules,
            new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, "Leave provision test"), new(ClaimTypes.Role, "Admin"),
            new("permission", "payroll.read"), new("permission", "payroll.write"), new("permission", "payroll.approve"), new("permission", "payroll.lock"),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return ctrl;
    }

    private static async Task<IActionResult> ApproveThenLockAsync(CostingLockKit k, Guid runId)
    {
        await using (var db = k.K.NewContext())
        {
            await ReconTestSupport.SignOffAsync(db, runId);
            (await Ksa(db, k.TenantId, k.ApproverId).Approve(runId, new PayrollDecisionRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        }
        await using var lockDb = k.K.NewContext();
        return await Ksa(lockDb, k.TenantId, k.LockerId).Lock(runId, default);
    }

    [Fact]
    public async Task LeaveProvisionOn_EmployeesInTwoCostCentres_TheExpenseSplitsPerCostCentre_TheProvisionStaysUncosted()
    {
        await using var k = await KitAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 6000m), E("E2", "BASIC", 6000m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 6);

        var res = await ApproveThenLockAsync(k, run.Id);

        res.Should().BeOfType<OkObjectResult>(Json(res).ToString());
        await using var read = k.K.NewContext();
        var lines = await read.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == k.TenantId && l.SourceEntityId == run.Id && l.EventType == GlEventTypes.LeaveProvisionAccrual).ToListAsync();

        var expense = lines.Where(l => l.DebitAccount.StartsWith(Expense)).ToList();
        expense.Select(l => (l.CostCenterCode, l.Amount)).OrderBy(x => x.CostCenterCode).Should().Equal(("CC-110", 1_500m), ("CC-200", 3_600m));
        expense.Sum(l => l.Amount).Should().Be(5_100m);
        expense.Should().OnlyContain(l => l.CostCenterId != null && l.PostingKey != null);

        var provision = lines.Where(l => l.CreditAccount.StartsWith(Provision)).ToList();
        provision.Should().HaveCount(2, "one uncosted provision line per employee");
        provision.Should().OnlyContain(l => l.CostCenterId == null && l.CostCenterCode == null && l.PostingKey != null);
        provision.Sum(l => l.Amount).Should().Be(5_100m);
        lines.Where(l => l.PostingKey != null).Select(l => l.PostingKey).Should().OnlyHaveUniqueItems();

        read.LeaveProvisionSnapshots.AsNoTracking().Count(s => s.PayrollRunId == run.Id).Should().Be(2);
    }

    [Fact]
    public async Task ALeaveBalanceThatMovesBetweenApproveAndLock_DoesNotSendTheCostedRunBack_AndTheProvisionUsesTheBalanceAtLock()
    {
        await using var k = await KitAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 6000m), E("E2", "BASIC", 6000m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 6);
        await using (var db = k.K.NewContext())
        {
            await ReconTestSupport.SignOffAsync(db, run.Id);
            (await Ksa(db, k.TenantId, k.ApproverId).Approve(run.Id, new PayrollDecisionRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        }
        // After approval E1 takes 2 days of leave: the balance, and so the provision, moves. The approval fingerprint must not care.
        await using (var db = k.K.NewContext())
        {
            var balance = await db.EmployeeLeaveBalances.SingleAsync(b => b.TenantId == k.TenantId && b.EmployeeId == k.Emp["E1"].Id);
            balance.Used += 2m;
            await db.SaveChangesAsync();
        }

        await using var lockDb = k.K.NewContext();
        var res = await Ksa(lockDb, k.TenantId, k.LockerId).Lock(run.Id, default);

        res.Should().BeOfType<OkObjectResult>(Json(res).ToString());
        await using var read = k.K.NewContext();
        var e1 = await read.LeaveProvisionSnapshots.AsNoTracking().SingleAsync(s => s.PayrollRunId == run.Id && s.EmployeeId == k.Emp["E1"].Id);
        (e1.Days, e1.Obligation).Should().Be((10m, 3_000m), "12 days less the 2 taken after approval, at 300 a day");
    }

    [Fact]
    public async Task TheErpExport_CarriesTheLeaveLines_TiesOutToTheLedger_AndNamesNoEmployee()
    {
        await using var k = await KitAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 6000m), E("E2", "BASIC", 6000m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 6);
        (await ApproveThenLockAsync(k, run.Id)).Should().BeOfType<OkObjectResult>();

        await using var db = k.K.NewContext();
        var service = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var (build, refusal) = await service.BuildAsync(k.TenantId, new JournalExportFilter(Period: "2026-06", CompanyId: k.CompanyId, Currency: "SAR"), "generic-csv", default, new Dictionary<string, string>());
        build.Should().NotBeNull($"the export tie-out passes: {System.Text.Json.JsonSerializer.Serialize(refusal?.Payload)}");
        var csv = Encoding.UTF8.GetString(build!.Bytes);
        var rows = CostExportHarness.ParseCsv(csv);
        var sourceRef = rows[0].IndexOf("SourceRef");
        sourceRef.Should().BeGreaterThanOrEqualTo(0);

        var leaveRows = rows.Skip(1).Where(r => r.Any(c => c.Contains("annual leave provision", StringComparison.OrdinalIgnoreCase))).ToList();
        leaveRows.Should().NotBeEmpty("the leave provision lines are in the file");
        leaveRows.Select(r => r[sourceRef]).Should().OnlyContain(v => v == "LEAVE-2026-06" || v == "2026-06", "the export reference is a period label, never an employee link");
        leaveRows.Select(r => r[sourceRef]).Should().Contain("LEAVE-2026-06", "the per-employee provision lines say LEAVE-<period>, not the employee id");
        rows.Skip(1).Select(r => r[sourceRef]).Should().NotContain(k.Emp.Values.Select(e => e.Id.ToString()));

        // The frozen export lines equal the journal the ledger holds for the provision, line for line.
        var ledger = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == k.TenantId && l.EventType == GlEventTypes.LeaveProvisionAccrual).ToListAsync();
        var selection = await JournalExportBuilder.SelectAsync(db, k.TenantId, new JournalExportFilter(Period: "2026-06", CompanyId: k.CompanyId, Currency: "SAR"));
        var expanded = JournalExportBuilder.Expand(selection.Entries.Where(e => e.EventType == GlEventTypes.LeaveProvisionAccrual).ToList(), "CO");
        expanded.Where(l => l.Side == "DR").Sum(l => l.Amount).Should().Be(ledger.Where(l => l.DebitAccount != "").Sum(l => l.Amount));
        expanded.Where(l => l.Side == "CR").Sum(l => l.Amount).Should().Be(ledger.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        expanded.Where(l => l.Side == "DR").Sum(l => l.Amount).Should().Be(expanded.Where(l => l.Side == "CR").Sum(l => l.Amount), "the provision journal balances on its own");
        expanded.Select(l => l.SourceEntityRef).Should().OnlyContain(r => r == "LEAVE-2026-06" || r == "2026-06");
        expanded.Where(l => l.AccountCode == Provision).Select(l => l.SourceEntityRef).Should().OnlyContain(r => r == "LEAVE-2026-06");
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesASecondMeasurementOfTheSameEmployeeAndMonth()
    {
        await using var k = await KitAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 6000m), E("E2", "BASIC", 6000m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 6);
        (await ApproveThenLockAsync(k, run.Id)).Should().BeOfType<OkObjectResult>();

        await using var db = k.K.NewContext();
        db.LeaveProvisionSnapshots.Add(new LeaveProvisionSnapshot
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, EmployeeId = k.Emp["E1"].Id, Period = "2026-06", MeasuredAt = new DateOnly(2026, 6, 30),
            PayrollRunId = run.Id, Days = 1m, DailyRate = 1m, Basis = "Gross", Obligation = 1m, Movement = 1m, Currency = "SAR",
        });
        var act = async () => await db.SaveChangesAsync();

        (await act.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>()
            .Which.ConstraintName.Should().Be("ux_leave_provision_snapshots_tenant_employee_period");
    }
}

file sealed class LeaveKsaResolver(StubRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => cc is "SAU" or "SA" ? new KsaDeductionCalculator(rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
