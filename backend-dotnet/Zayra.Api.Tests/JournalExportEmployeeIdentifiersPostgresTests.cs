using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
/// Cost-accounting v1, WP-C3, final ledger review P2-3 and T-063 (extended): the monthly EOSB provision lines and the settlement accrual / provision
/// consumption lines are one line per employee, and what leaves for an ERP must not identify the employee: no code or name in a Description, and no
/// employee id as the reference. Run through the real Lock and the real settlement approval on PostgreSQL, then through EVERY formatter.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class JournalExportEmployeeIdentifiersPostgresTests
{
    private readonly PostgresFixture _fx;
    public JournalExportEmployeeIdentifiersPostgresTests(PostgresFixture fx) => _fx = fx;

    private const string Marker1 = "EMP-MK-7781";
    private const string Marker2 = "EMP-MK-7782";
    private const string Name1 = "Zaidmarker Alharbimarker";

    private static PayrollController Ksa(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var ctrl = new PayrollController(db, new DataScopeService(db), new HttpContextAccessor(), new F2NullNotifications(), new IdKsaResolver(rules), rules,
            new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, "Id test"), new(ClaimTypes.Role, "Admin"),
            new("permission", "payroll.read"), new("permission", "payroll.write"), new("permission", "payroll.approve"), new("permission", "payroll.lock"),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return ctrl;
    }

    private async Task<CostingLockKit> WorldAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var k = await CostingLockKit.CreateAsync(true, true, _fx.CreateDb, tenantId);
        await k.AddCc("CC-110", "G_AND_A");
        await k.AddCc("CC-200", "SELLING");
        await k.AddDept("D-FIN", "CC-110");
        await k.AddDept("D-SAL", "CC-200");
        foreach (var (code, dept) in new[] { (Marker1, "D-SAL"), (Marker2, "D-FIN") })
        {
            var e = await k.AddEmployee(code, dept);
            e.JoiningDate = new DateTime(2023, 6, 30, 0, 0, 0, DateTimeKind.Utc); e.Nationality = "SAU"; e.ContractType = "Indefinite"; e.WorkEmail = $"{code.ToLowerInvariant()}@test.com";
        }
        k.Emp[Marker1].FullName = Name1;
        var company = await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId);
        company.CountryCode = "SAU"; company.Jurisdiction = "KSA-mainland";
        k.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = "SAU", EosbEnabled = true });
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2023, 1, 1), IsActive = true };
        k.Db.SalaryStructures.Add(structure);
        await k.Db.SaveChangesAsync();
        foreach (var code in new[] { Marker1, Marker2 })
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

        // June: the monthly EOSB provision movement, per employee, through the real Approve and Lock.
        var june = await k.SeedRunAsync(new[] { E(Marker1, "BASIC", 8000m), E(Marker2, "BASIC", 8000m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 6);
        await using (var db = k.K.NewContext())
            (await Ksa(db, k.TenantId, k.ApproverId).Approve(june.Id, new PayrollDecisionRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        await using (var db = k.K.NewContext())
            (await Ksa(db, k.TenantId, k.LockerId).Lock(june.Id, default)).Should().BeOfType<OkObjectResult>();

        // September: the first employee leaves; the approval consumes their provision and accrues the settlement.
        var wages = await k.SeedRunAsync(new[] { E(Marker1, "BASIC", 100m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 9);
        var employee = k.Emp[Marker1];
        var lastDay = new DateOnly(2026, 9, 20);
        await using (var db = k.K.NewContext())
        {
            var offboarding = new EmployeeOffboarding
            {
                TenantId = k.TenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName, SeparationType = "Termination",
                Status = "InProgress", NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
            };
            var settlement = new EmployeeFinalSettlement
            {
                TenantId = k.TenantId, CompanyId = k.CompanyId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
                OffboardingId = offboarding.Id, LastWorkingDay = lastDay, ServiceStartDate = new DateOnly(2023, 6, 30), SettlementDueDate = lastDay.AddDays(7),
                TerminationReason = "Termination", ServiceYears = 3.2m, Currency = "SAR", GratuityAmount = 16_000m, LeaveEncashmentAmount = 0m, GrossPayable = 16_000m,
                TotalDeductions = 0m, NetPayable = 16_000m, WagesPaidByRunId = wages.Id, WageBaseDeltaAmount = 0m, Status = FinalSettlementStatuses.PendingApproval,
                CreatedByUserId = Guid.NewGuid(), CreatedByName = "Maker", SubmittedByUserId = Guid.NewGuid(), SubmittedByName = "Submitter", SubmittedAtUtc = DateTime.UtcNow,
            };
            db.EmployeeOffboardings.Add(offboarding);
            db.EmployeeFinalSettlements.Add(settlement);
            db.FinalSettlementLines.Add(new FinalSettlementLine
            {
                TenantId = k.TenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.Gratuity, ComponentName = "Gratuity",
                LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource, Amount = 16_000m, SortOrder = 0,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var body = JsonSerializer.Deserialize<ApproveFinalSettlementRequest>(
                """{"confirmTerminationReason":"Termination","accrualDate":"2026-09-21"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var result = await FinalSettlementMakerCheckerTests.Controller(db, k.TenantId, Guid.NewGuid()).ApproveFinalSettlement(settlement.Id, body, default);
            result.Should().BeOfType<OkObjectResult>(Json(result).ToString());
        }
        return k;
    }

    private static List<string> SourceRefColumn(string genericCsv)
    {
        var rows = CostExportHarness.ParseCsv(genericCsv);
        var at = rows[0].IndexOf("SourceRef");
        return rows.Skip(1).Select(r => r[at]).ToList();
    }

    [Fact]
    public async Task T063_EosbProvisionAndSettlementLines_LeaveNoEmployeeIdentifier_InAnyFormat()
    {
        await using var k = await WorldAsync();
        var employeeIds = new[] { k.Emp[Marker1].Id.ToString(), k.Emp[Marker2].Id.ToString() };
        await using var db = k.K.NewContext();
        var service = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var checkedLines = 0;

        foreach (var period in new[] { "2026-06", "2026-09" })
            foreach (var formatter in service.Formatters)
            {
                // Some formats need a tenant option before they will build (a ledger id, a journal name): supply a placeholder and go on.
                var options = new Dictionary<string, string>();
                JournalExportBuild? build = null; JournalExportRefusal? refusal = null;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    (build, refusal) = await service.BuildAsync(k.TenantId, new JournalExportFilter(Period: period, CompanyId: k.CompanyId, Currency: "SAR"), formatter.Key, default, options);
                    var payload = refusal is null ? default : JsonSerializer.SerializeToElement(refusal.Payload);
                    if (refusal is not null && payload.TryGetProperty("error", out var err) && err.GetString() == "format_option_required")
                        options[payload.GetProperty("option").GetString()!] = "1";
                    else break;
                }
                if (build is null) throw new Xunit.Sdk.XunitException($"{formatter.Key} {period} refused: {JsonSerializer.Serialize(refusal?.Payload)}");
                var text = Encoding.UTF8.GetString(build.Bytes) + Encoding.Latin1.GetString(build.Bytes);
                foreach (var marker in new[] { Marker1, Marker2, Name1, "Alharbimarker", "Person " + Marker2 })
                    text.Should().NotContain(marker, $"{formatter.Key} {period}: an employee code or name must not reach the ERP");
                if (formatter.Key == "generic-csv")
                {
                    var refs = SourceRefColumn(Encoding.UTF8.GetString(build.Bytes));
                    refs.Should().NotContain(employeeIds, $"the generic CSV's SourceRef is a run or settlement reference, never an employee id ({period})");
                    checkedLines += refs.Count;
                }
            }
        checkedLines.Should().BeGreaterThan(0);
        // The sub-ledger still keys on the employee link internally, so the provision is consumed correctly at the settlement.
        (await db.FinanceGlEntries.AsNoTracking().AnyAsync(l => l.TenantId == k.TenantId && l.EventType == GlEventTypes.EosbProvisionConsumption && l.Amount > 0m)).Should().BeTrue();
    }

    [Fact]
    public async Task T063_TheProvisionLinesKeepAFixedLabel_NotTheEmployeeCode()
    {
        await using var k = await WorldAsync();
        await using var db = k.K.NewContext();

        var lines = await db.FinanceGlEntries.AsNoTracking().Where(l => l.TenantId == k.TenantId && l.EventType == GlEventTypes.EosbProvisionAccrual).ToListAsync();

        lines.Should().NotBeEmpty();
        lines.Select(l => l.Description).Should().OnlyContain(d => !d.Contains(Marker1) && !d.Contains(Marker2));
        lines.Where(l => l.CreditAccount.StartsWith("2310")).Select(l => l.Description).Should().OnlyContain(d => d == EosbProvisionPlanner.CatchUpLabel);
    }
}

file sealed class IdKsaResolver(StubRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => cc is "SAU" or "SA" ? new KsaDeductionCalculator(rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
