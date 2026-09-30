using System.Security.Claims;
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
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F02, end to end on real Postgres: Process freezes each Saudi national's GOSI cohort and rate basis on the
/// slip, the validator raises per-employee findings from what was frozen, a new entrant blocks Approve, a
/// tenant-wide acknowledgement cannot lift that block, and GOSI reconciliation ties out on the frozen cohort.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GosiEntrantCohortProcessTests
{
    private readonly PostgresFixture _fx;
    public GosiEntrantCohortProcessTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Process_FreezesTheCohortPerPayslip_AndOnlyTheNewEntrantBlocksApproval()
    {
        await using var db = _fx.CreateDb();
        var w = await World.SeedAsync(db);
        var ctrl = Controller(db, w.TenantId, Rules());

        (await ctrl.Process(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == w.Run.Id).ToDictionaryAsync(s => s.EmployeeId);

        // ── The calculation explanation, per payslip ──
        slips[w.Unknown.Id].GosiCohort.Should().Be(GosiCohorts.Unknown);
        slips[w.Unknown.Id].StatutoryBasis.Should().Contain("unverified");
        slips[w.Existing.Id].GosiCohort.Should().Be(GosiCohorts.PreJuly2024);
        slips[w.Existing.Id].StatutoryBasis.Should().Contain("existing subscriber (first registered 2016-05-10");
        slips[w.Entrant.Id].GosiCohort.Should().Be(GosiCohorts.NewEntrant);
        slips[w.Entrant.Id].StatutoryBasis.Should().Contain("NOT MODELLED");
        slips[w.Expat.Id].GosiCohort.Should().BeNull("no Saudi cohort applies to an expatriate");
        slips[w.Expat.Id].StatutoryBasis.Should().StartWith("Non-Saudi");

        // ── ...and the payroll register (GET runs/{id}/slips) carries it to the reviewer ──
        var register = (Zayra.Api.Application.Common.PagedResult<Zayra.Api.Application.Finance.PayrollSlipDto>)
            ((OkObjectResult)await ctrl.Slips(w.Run.Id, 1, 50, CancellationToken.None)).Value!;
        register.Items.Single(s => s.EmployeeId == w.Entrant.Id).Should().Match<Zayra.Api.Application.Finance.PayrollSlipDto>(s =>
            s.GosiCohort == GosiCohorts.NewEntrant && s.StatutoryBasis == slips[w.Entrant.Id].StatutoryBasis);

        // ── No figure moved: same package, same GOSI, whatever the cohort ──
        slips[w.Existing.Id].EmployeeStatutoryTotal.Should().Be(slips[w.Unknown.Id].EmployeeStatutoryTotal);
        slips[w.Entrant.Id].EmployeeStatutoryTotal.Should().Be(slips[w.Unknown.Id].EmployeeStatutoryTotal);
        slips[w.Unknown.Id].EmployeeStatutoryTotal.Should().Be(1_267.50m);

        // ── Per-employee findings, persisted by Process ──
        var results = await db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == w.Run.Id).ToListAsync();
        results.Should().ContainSingle(r => r.Code == PayrollValidationEngine.GosiCohortNotRecorded)
            .Which.EmployeeId.Should().Be(w.Unknown.Id);
        results.Should().ContainSingle(r => r.Code == PayrollValidationEngine.GosiNewEntrantScheduleNotModelled)
            .Which.Should().Match<PayrollValidationResult>(r => r.EmployeeId == w.Entrant.Id && r.Severity == "Error");
        results.Should().NotContain(r => r.Code == "WARN_GOSI_ENTRANT_COHORT_NOT_MODELLED");

        // ── /validate rebuilds the same findings from the frozen slips ──
        (await ctrl.Validate(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var revalidated = await db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == w.Run.Id).ToListAsync();
        revalidated.Where(r => r.Code is PayrollValidationEngine.GosiCohortNotRecorded or PayrollValidationEngine.GosiNewEntrantScheduleNotModelled)
            .Select(r => (r.Code, r.EmployeeId))
            .Should().BeEquivalentTo(new[]
            {
                (PayrollValidationEngine.GosiCohortNotRecorded, (int?)w.Unknown.Id),
                (PayrollValidationEngine.GosiNewEntrantScheduleNotModelled, (int?)w.Entrant.Id),
            });

        // ── The block is real: it is the run's ONLY error, and Approve refuses the run on it ──
        revalidated.Where(r => r.Severity == "Error").Select(r => r.Code).Should()
            .OnlyContain(c => c == PayrollValidationEngine.GosiNewEntrantScheduleNotModelled);
        var approve = await ctrl.Approve(w.Run.Id, new PayrollDecisionRequest("approve"), CancellationToken.None);
        approve.Should().BeOfType<UnprocessableEntityObjectResult>();

        // ── Reconciliation recomputes on the frozen cohort and ties out ──
        var recon = await new GosiReconciliationService(db, new CohortPackResolver(Rules()))
            .ReconcileAsync(w.TenantId, await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == w.Run.Id), CancellationToken.None);
        recon.VarianceCount.Should().Be(0);
    }

    [Fact]
    public async Task ATenantWideAcknowledgement_DoesNotLiftAPerEmployeeNewEntrantBlock()
    {
        await using var db = _fx.CreateDb();
        var w = await World.SeedAsync(db);
        // The flag that used to silence the run-wide warning for the whole population.
        var ctrl = Controller(db, w.TenantId, Rules().Set("gosi.new_entrant_scheme_acknowledged", "true"));

        (await ctrl.Process(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollValidationResults.AsNoTracking()
                .AnyAsync(r => r.PayrollRunId == w.Run.Id && r.EmployeeId == w.Entrant.Id
                            && r.Code == PayrollValidationEngine.GosiNewEntrantScheduleNotModelled && r.Severity == "Error"))
            .Should().BeTrue("a blanket statement about the population cannot answer a per-person fact");
        (await db.PayrollValidationResults.AsNoTracking()
                .AnyAsync(r => r.PayrollRunId == w.Run.Id && r.EmployeeId == w.Unknown.Id
                            && r.Code == PayrollValidationEngine.GosiCohortNotRecorded))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ADateRecordedAfterProcess_IsHonouredByReprocessing_NotByRevalidating()
    {
        await using var db = _fx.CreateDb();
        var w = await World.SeedAsync(db);
        var ctrl = Controller(db, w.TenantId, Rules());
        (await ctrl.Process(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // HR records the date (the approved change lands on the column).
        await db.Employees.Where(e => e.Id == w.Unknown.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.GosiFirstRegisteredOn, new DateOnly(2018, 1, 15)));
        db.ChangeTracker.Clear();

        (await ctrl.Validate(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.PayrollValidationResults.AsNoTracking()
                .SingleAsync(r => r.PayrollRunId == w.Run.Id && r.Code == PayrollValidationEngine.GosiCohortNotRecorded))
            .Message.Should().Contain("computed before the GOSI first-registration date (2018-01-15)")
            .And.Contain("re-process");

        // A Processed run cannot be processed again in place; the exit the message names is reopen → process.
        db.ChangeTracker.Clear();
        (await ctrl.ReopenRun(w.Run.Id, new PayrollReasonRequest("GOSI first-registration date recorded"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await ctrl.Process(w.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == w.Run.Id && s.EmployeeId == w.Unknown.Id))
            .GosiCohort.Should().Be(GosiCohorts.PreJuly2024);
        (await db.PayrollValidationResults.AsNoTracking()
                .AnyAsync(r => r.PayrollRunId == w.Run.Id && r.Code == PayrollValidationEngine.GosiCohortNotRecorded))
            .Should().BeFalse();
    }

    // ── World ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Guid TenantId, PayrollRun Run, Employee Unknown, Employee Existing, Employee Entrant, Employee Expat)
    {
        public static async Task<World> SeedAsync(ZayraDbContext db)
        {
            var tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var company = new Company
            {
                Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Cohort Co", CountryCode = "SAU",
                Jurisdiction = "KSA-mainland", RegistrationNumber = $"COH-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
                IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Companies.Add(company);

            Employee Emp(string code, string nationality, DateOnly? firstRegisteredOn) => new()
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"{code}-{Guid.NewGuid():N}"[..20],
                FullName = $"Cohort {code}", Nationality = nationality, Status = "Active",
                JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), GosiFirstRegisteredOn = firstRegisteredOn,
            };
            var unknown = Emp("UNK", "Saudi", null);
            var existing = Emp("PRE", "Saudi", new DateOnly(2016, 5, 10));
            var entrant = Emp("NEW", "Saudi", new DateOnly(2025, 2, 1));
            var expat = Emp("EXP", "Indian", null);
            db.Employees.AddRange(unknown, existing, entrant, expat);
            await db.SaveChangesAsync();

            foreach (var e in new[] { unknown, existing, entrant, expat })
            {
                db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
                {
                    TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(),
                    BasicSalary = 10_000m, HousingAllowance = 3_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
                });
                // Complete bank details, so the ONLY blocking error left on the run is the one under test.
                db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
                {
                    TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234", MolId = "MOL123456",
                    SalaryCurrency = "SAR", BankName = "Test Bank",
                });
            }
            var run = new PayrollRun
            {
                TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 9,
                CreatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            return new World(tenantId, run, unknown, existing, entrant, expat);
        }
    }

    private static StubRuleReader Rules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m);

    private static PayrollController Controller(ZayraDbContext db, Guid tenantId, StubRuleReader rules)
    {
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(), new CohortNotifications(),
            new CohortPackResolver(rules), rules, new CohortLetters(), new NullDocumentStorage(),
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
                    new Claim("permission", "employees.read"),   // organisation-wide data scope, as a payroll role has
                    new Claim("is_group_scope", "true"),
                }, "Test")),
            },
        };
        return ctrl;
    }
}

file sealed class CohortPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public CohortPackResolver(IStatutoryRuleReader r) => _r = r;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
        cc is "SAU" or "SA" ? new KsaDeductionCalculator(_r) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class CohortNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId,
        CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class CohortLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
