using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// AM-6, register level: the oracle's payroll register (earnings and the GOSI components per employee, 2026-09) against what the REAL
/// <c>PayrollController.Process</c> computes on the real KSA pack and statutory rules for the same employees. The journal tests seed the
/// oracle's register; this is the proof that the register itself is what the engine produces (the cohorts E001/E006/E008 and the expat
/// occupational-hazard rows).
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class CostAccountingOracleRegisterPostgresTests
{
    private readonly PostgresFixture _fx;
    public CostAccountingOracleRegisterPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TheEnginesRegister_ForTheOracleEmployees_EqualsTheOraclesRegister()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "ACME-SA", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"ACME-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var codes = new[] { "E001", "E004", "E006", "E007", "E008", "E010" };
        var people = OracleFiles.Csv("inputs/05_employees.csv").Where(r => codes.Contains(r["employee_code"])).ToList();
        var ids = new Dictionary<string, int>();
        foreach (var r in people)
        {
            var e = new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = r["employee_code"], FullName = $"Oracle {r["employee_code"]}",
                Nationality = r["nationality"] == "Saudi" ? "Saudi" : "Egypt", Status = "Active",
                JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                GosiFirstRegisteredOn = r["gosi_first_registered_on"] == "" ? null : DateOnly.Parse(r["gosi_first_registered_on"]),
            };
            db.Employees.Add(e);
            await db.SaveChangesAsync();
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = OracleFiles.D(r["basic"]),
                HousingAllowance = OracleFiles.D(r["housing"]), TransportAllowance = OracleFiles.D(r["transport"]),
                EffectiveDate = new DateOnly(2022, 1, 1), IsActive = true, Currency = "SAR",
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            { TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{r["employee_code"]}", SalaryCurrency = "SAR", BankName = "Test Bank" });
            await db.SaveChangesAsync();
            ids[r["employee_code"]] = e.Id;
        }
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 9, CreatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var ctrl = new PayrollController(db, new DataScopeService(db), new HttpContextAccessor(), new RegNotifications(), new RegPackResolver(reader), reader,
            new RegLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("permission", "payroll.read"), new Claim("permission", "payroll.write"), new Claim("permission", "payroll.approve"),
                        new Claim("permission", "employees.read"), new Claim("is_group_scope", "true"),
                    }, "Test")),
                },
            },
        };

        var processed = await ctrl.Process(run.Id, CancellationToken.None);
        processed.Should().BeOfType<OkObjectResult>();

        var earnings = await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id).ToListAsync();
        var deductions = await db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == run.Id).ToListAsync();
        var byId = ids.ToDictionary(kv => kv.Value, kv => kv.Key);

        var produced = earnings.Where(e => e.Amount != 0m).Select(e => $"{byId[e.EmployeeId]}|{e.ComponentCode}|{e.Amount:0.00}")
            .Concat(deductions.Where(d => d.ComponentCode.StartsWith("GOSI")).Select(d => $"{byId[d.EmployeeId]}|{d.ComponentCode}|{d.Amount:0.00}")).Order().ToList();
        var expected = OracleFiles.Csv("inputs/09_payroll_register_m1_regular.csv")
            .Where(r => codes.Contains(r["employee_code"]) && (r["kind"] == "Earning" && r["source"] != "Bonus" && r["component_code"] != "OVERTIME" && r["component_code"] != "OTHER_ALLOWANCES"
                                                              || r["component_code"].StartsWith("GOSI")))
            .Select(r => $"{r["employee_code"]}|{r["component_code"]}|{OracleFiles.D(r["amount"]):0.00}").Order().ToList();

        // AM-33: E001's SANED is 18,750.00 x 0.0075 = 140.625 EXACTLY, a tie. The engine rounds half to even (Math.Round default) and the oracle
        // register now carries the engine's 140.62: v1 does not change payroll computation, so the engine's value is authoritative.
        (18_750.00m * 0.0075m).Should().Be(140.625m, "an exact tie");
        expected.Should().Contain("E001|GOSI-SANED-EE|140.62").And.Contain("E001|GOSI-SANED-ER|140.62");
        expected = expected.Order().ToList();
        var onlyEngine = produced.Except(expected).ToList();
        var onlyOracle = expected.Except(produced).ToList();
        (onlyEngine.Count + onlyOracle.Count).Should().Be(0,
            $"the engine's register must equal the oracle's.\nonly in the engine: {string.Join(", ", onlyEngine)}\nonly in the oracle: {string.Join(", ", onlyOracle)}");
    }
}

file sealed class RegPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public RegPackResolver(IStatutoryRuleReader r) => _r = r;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
        cc is "SAU" or "SA" ? new KsaDeductionCalculator(_r) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class RegNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class RegLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
