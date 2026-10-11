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
/// The GOSI new-entrant schedule (Royal Decree M/273) on real Postgres: the boot seeder adds the rows to a
/// database that already holds the old GOSI rows, does nothing on a second boot, and a payroll run processed
/// through the real <c>PayrollController.Process</c> on the real <c>StatutoryRuleReader</c> prices a new
/// entrant on the step in force, is not blocked, and ties out in GOSI reconciliation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GosiNewEntrantSchedulePostgresTests
{
    private readonly PostgresFixture _fx;
    public GosiNewEntrantSchedulePostgresTests(PostgresFixture fx) => _fx = fx;

    private static readonly string[] NewKeys =
    {
        RuleKeys.GosiNewEntrantEmployeeRate, RuleKeys.GosiNewEntrantEmployerRate,
        RuleKeys.GosiMinContributoryWageAnnuitiesSar, RuleKeys.GosiMinContributoryWageOccupationalHazardsSar,
    };

    private static IQueryable<StatutoryRule> Platform(ZayraDbContext db) =>
        db.StatutoryRules.IgnoreQueryFilters().Where(r => r.TenantId == null);

    [Fact]
    public async Task TheSeeder_InsertsTheNewRows_IntoADatabaseThatAlreadyHasTheOldOnes_AndIsIdempotent()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);

        // An environment seeded before this change: every old row present, none of the new ones.
        await Platform(db).Where(r => NewKeys.Contains(r.RuleKey)).ExecuteDeleteAsync();
        var oldRows = await Platform(db).AsNoTracking().OrderBy(r => r.Id).Select(r => new { r.Id, r.RuleKey, r.RuleValue, r.EffectiveFrom }).ToListAsync();
        oldRows.Should().Contain(r => r.RuleKey == RuleKeys.GosiSaudiEmployeeRate && r.RuleValue == "0.09");
        db.ChangeTracker.Clear();

        // The next boot.
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        db.ChangeTracker.Clear();

        var added = await Platform(db).AsNoTracking().Where(r => NewKeys.Contains(r.RuleKey)).ToListAsync();
        added.Should().HaveCount(12, "5 employee steps + 5 employer steps + 2 minimum-wage rows");
        added.Where(r => r.RuleKey == RuleKeys.GosiNewEntrantEmployeeRate)
            .OrderBy(r => r.EffectiveFrom).Select(r => r.RuleValue)
            .Should().Equal("0.09", "0.095", "0.10", "0.105", "0.11");
        (await Platform(db).AsNoTracking().Where(r => !NewKeys.Contains(r.RuleKey))
                .OrderBy(r => r.Id).Select(r => new { r.Id, r.RuleKey, r.RuleValue, r.EffectiveFrom }).ToListAsync())
            .Should().Equal(oldRows, "existing rows are neither duplicated nor changed");

        // And the boot after that adds nothing.
        var before = await Platform(db).CountAsync();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        db.ChangeTracker.Clear();
        (await Platform(db).CountAsync()).Should().Be(before);

        // The real reader resolves the step by period.
        var reader = new StatutoryRuleReader(db);
        (await reader.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiNewEntrantEmployeeRate, new DateOnly(2025, 6, 1))).Should().Be(0.09m);
        (await reader.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiNewEntrantEmployeeRate, new DateOnly(2025, 7, 1))).Should().Be(0.095m);
        (await reader.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiNewEntrantEmployerRate, new DateOnly(2028, 7, 1))).Should().Be(0.11m);
    }

    [Fact]
    public async Task Process_PricesANewEntrantOnTheStepInForce_DoesNotBlock_AndReconciles()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);
        var w = await World.SeedAsync(db, year: 2025, month: 7);
        var ctrl = Controller(db, w.TenantId, reader);

        (await ctrl.Process(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var lines = await db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == w.Run.Id).ToListAsync();
        decimal Amount(Employee e, string code) => lines.Single(d => d.EmployeeId == e.Id && d.ComponentCode == code).Amount;

        // 13,000 covered wage. July 2025: new entrant 9.5% each side; existing subscriber 9%.
        Amount(w.Entrant, "GOSI-ANN-EE").Should().Be(1_235.00m);
        Amount(w.Entrant, "GOSI-ANN-ER").Should().Be(1_235.00m);
        Amount(w.Existing, "GOSI-ANN-EE").Should().Be(1_170.00m);
        Amount(w.Existing, "GOSI-ANN-ER").Should().Be(1_170.00m);
        Amount(w.Entrant, "GOSI-SANED-EE").Should().Be(97.50m);

        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == w.Run.Id && s.EmployeeId == w.Entrant.Id);
        slip.GosiCohort.Should().Be(GosiCohorts.NewEntrant);
        slip.StatutoryBasis.Should().Contain(KsaGosiCohortSchedule.NewEntrantScheduleAppliedMarker).And.Contain("9.5% employee");

        // Not blocked; a notice names the rate used. /validate rebuilds the same findings.
        foreach (var pass in new[] { "process", "validate" })
        {
            if (pass == "validate")
                (await ctrl.Validate(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
            var results = await db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == w.Run.Id).ToListAsync();
            results.Should().NotContain(r => r.Severity == "Error", $"nothing blocks the run after {pass}");
            results.Should().ContainSingle(r => r.Code == PayrollValidationEngine.GosiNewEntrantScheduleApplied && r.EmployeeId == w.Entrant.Id)
                .Which.Severity.Should().Be("Warning");
        }

        // GOSI reconciliation recomputes the slip on its frozen cohort with the same rows and ties out.
        var recon = await new GosiReconciliationService(db, new EntrantPackResolver(reader))
            .ReconcileAsync(w.TenantId, await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == w.Run.Id), CancellationToken.None);
        recon.VarianceCount.Should().Be(0);
    }

    // ── World ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Guid TenantId, PayrollRun Run, Employee Existing, Employee Entrant)
    {
        public static async Task<World> SeedAsync(ZayraDbContext db, int year, int month)
        {
            var tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var company = new Company
            {
                Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Entrant Co", CountryCode = "SAU",
                Jurisdiction = "KSA-mainland", RegistrationNumber = $"NEW-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
                IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Companies.Add(company);

            Employee Emp(string code, DateOnly firstRegisteredOn) => new()
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"{code}-{Guid.NewGuid():N}"[..20],
                FullName = $"Entrant {code}", Nationality = "Saudi", Status = "Active",
                JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), GosiFirstRegisteredOn = firstRegisteredOn,
            };
            var existing = Emp("PRE", new DateOnly(2016, 5, 10));
            var entrant = Emp("NEW", new DateOnly(2024, 8, 1));
            db.Employees.AddRange(existing, entrant);
            await db.SaveChangesAsync();

            foreach (var e in new[] { existing, entrant })
            {
                db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
                {
                    TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(),
                    BasicSalary = 10_000m, HousingAllowance = 3_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
                });
                db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
                {
                    TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234", MolId = "MOL123456",
                    SalaryCurrency = "SAR", BankName = "Test Bank",
                });
            }
            var run = new PayrollRun
            {
                TenantId = tenantId, CompanyId = company.Id, Year = year, Month = month,
                CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            return new World(tenantId, run, existing, entrant);
        }
    }

    private static PayrollController Controller(ZayraDbContext db, Guid tenantId, IStatutoryRuleReader rules)
    {
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(), new EntrantNotifications(),
            new EntrantPackResolver(rules), rules, new EntrantLetters(), new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "payroll.read"),
                    new Claim("permission", "payroll.write"),
                    new Claim("permission", "payroll.approve"),
                    new Claim("permission", "employees.read"),
                    new Claim("is_group_scope", "true"),
                }, "Test")),
            },
        };
        return ctrl;
    }
}

file sealed class EntrantPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public EntrantPackResolver(IStatutoryRuleReader r) => _r = r;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
        cc is "SAU" or "SA" ? new KsaDeductionCalculator(_r) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class EntrantNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId,
        CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class EntrantLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
