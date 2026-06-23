using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Domain void operation tests.
///
/// VoidRun is the audited, auditor-safe path for retiring a wrong payroll run:
///   • status → "Voided" (soft-delete — records are never hard-deleted)
///   • payslips → "Voided" (excluded from ESS / YTD / reporting)
///   • GL contra-entries written for Locked runs (original entries preserved, IsReversed=true)
///   • PayrollAuditLog entry: who/when/why
///   • Partial unique index allows a replacement run for the same (tenant, year, month) period
///   • RBAC: Admin or Finance Controller only
///
/// Covered:
///   (a) ProcessedRun void — status, payslips, audit log, no GL change (no prior GL)
///   (b) LockedRun void — GL contra-entries written, originals marked IsReversed=true, balanced
///   (c) Reason required — 400 when Notes is empty
///   (d) Already-voided — 409 Conflict
///   (e) RBAC attribute gate — Admin,Finance Controller only (reflection test)
///   (f) Voided run excluded from GL totals — replacement run's GL is clean
///   (g) No KSA Saudi 0-GOSI can survive void + reprocess cycle
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollVoidTests
{
    private readonly PostgresFixture _fx;
    public PayrollVoidTests(PostgresFixture fx) => _fx = fx;

    // ── (a) Void a Processed run ─────────────────────────────────────────────────

    [Fact]
    public async Task VoidRun_ProcessedRun_MarksVoidedAuditLogs_NoGlChange()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);

        var (_, _, run) = await SeedKsaRunProcessed(db, tenantId, year: 2026, month: 2);

        var ctrl   = BuildCtrl(db, tenantId, "Finance Controller");
        var result = await ctrl.VoidRun(run.Id, new PayrollDecisionRequest("IntelliFlow 0-GOSI batch void"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("void of a processed run must succeed");

        var reloaded = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
        reloaded.Status.Should().Be("Voided");
        reloaded.VoidReason.Should().Contain("IntelliFlow");
        reloaded.VoidedAtUtc.Should().NotBeNull();
        reloaded.VoidedByName.Should().NotBeNullOrEmpty();

        var slips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync();
        slips.Should().NotBeEmpty("there must be at least one payslip to void");
        slips.Should().OnlyContain(s => s.Status == "Voided", "every payslip must be marked Voided");

        var glCount = await db.FinanceGlEntries
            .CountAsync(g => g.SourceModule == "Payroll" && g.SourceEntityId == run.Id);
        glCount.Should().Be(0, "processed run has no GL — void must not create spurious entries");

        var audit = await db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.EntityId == run.Id.ToString() && a.Action == "payroll.run.voided")
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull("void must write an audit log entry");
    }

    // ── (b) Void a Locked run — GL contra-entries ────────────────────────────────

    [Fact]
    public async Task VoidRun_LockedRun_WritesContraGlEntries_OriginalsPreserved()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);

        var (_, _, run) = await SeedKsaRunProcessed(db, tenantId, year: 2026, month: 3);

        // Lock the run so GL entries are written
        run.Status = "Processed";
        await db.SaveChangesAsync();
        (await BuildCtrl(db, tenantId, "Finance Controller").Lock(run.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("lock must succeed before void test");

        var originalGl = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.SourceModule == "Payroll" && g.SourceEntityId == run.Id)
            .ToListAsync();
        originalGl.Should().NotBeEmpty("lock must write GL entries");

        db.ChangeTracker.Clear();
        run = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);

        (await BuildCtrl(db, tenantId, "Finance Controller")
            .VoidRun(run.Id, new PayrollDecisionRequest("Wrong GOSI calc — void + reprocess"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("void of a locked run must succeed");

        db.ChangeTracker.Clear();

        var allGl = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.SourceModule == "Payroll" && g.SourceEntityId == run.Id)
            .ToListAsync();

        var originals = allGl.Where(g => g.EventType == "PayrollLock").ToList();
        var contras   = allGl.Where(g => g.EventType == "PayrollVoid").ToList();

        originals.Should().HaveCount(originalGl.Count, "original GL entries must not be deleted");
        originals.Should().OnlyContain(g => g.IsReversed, "original entries must be flagged IsReversed=true");

        contras.Should().HaveCount(originalGl.Count, "one contra-entry per original entry");
        contras.Should().OnlyContain(g => g.ReversalOfEntryId.HasValue, "contra-entries must reference their original via ReversalOfEntryId");

        var originalIds = originals.Select(g => g.Id).ToHashSet();
        contras.Should().OnlyContain(g => originalIds.Contains(g.ReversalOfEntryId!.Value),
            "every contra-entry must point to an existing original entry");

        // Net GL effect = 0: sum of originals + contras must cancel (DR↔CR swap)
        var totalDebitOriginals  = originals.Where(g => !string.IsNullOrEmpty(g.DebitAccount)).Sum(g => g.Amount);
        var totalDebitContras    = contras.Where(g => !string.IsNullOrEmpty(g.DebitAccount)).Sum(g => g.Amount);
        var totalCreditOriginals = originals.Where(g => !string.IsNullOrEmpty(g.CreditAccount)).Sum(g => g.Amount);
        var totalCreditContras   = contras.Where(g => !string.IsNullOrEmpty(g.CreditAccount)).Sum(g => g.Amount);

        Math.Abs(totalDebitOriginals  - totalCreditContras).Should().BeLessThan(0.01m,
            "contra-entries must zero out original DR amounts via equal CR contra-entries");
        Math.Abs(totalCreditOriginals - totalDebitContras).Should().BeLessThan(0.01m,
            "contra-entries must zero out original CR amounts via equal DR contra-entries");
    }

    // ── (c) Reason required ──────────────────────────────────────────────────────

    [Fact]
    public async Task VoidRun_EmptyReason_ReturnsBadRequest()
    {
        await using var db = _fx.CreateDb();
        var tenantId       = await PostgresFixture.SeedMinimalTenant(db);
        var (_, _, run)    = await SeedKsaRunProcessed(db, tenantId, year: 2026, month: 4);

        var result = await BuildCtrl(db, tenantId, "Finance Controller")
            .VoidRun(run.Id, new PayrollDecisionRequest(null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>("voiding without a reason must return 400");

        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id))
            .Status.Should().NotBe("Voided", "run must not be voided when reason is missing");
    }

    // ── (d) Already-voided → 409 ─────────────────────────────────────────────────

    [Fact]
    public async Task VoidRun_AlreadyVoided_Returns409Conflict()
    {
        await using var db = _fx.CreateDb();
        var tenantId       = await PostgresFixture.SeedMinimalTenant(db);
        var (_, _, run)    = await SeedKsaRunProcessed(db, tenantId, year: 2026, month: 5);

        var ctrl = BuildCtrl(db, tenantId, "Finance Controller");

        (await ctrl.VoidRun(run.Id, new PayrollDecisionRequest("First void"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        (await ctrl.VoidRun(run.Id, new PayrollDecisionRequest("Second void attempt"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("re-voiding an already-voided run must return 409");
    }

    // ── (e) RBAC — attribute-level gate ─────────────────────────────────────────

    [Fact]
    public void VoidRun_RbacAttribute_RequiresAdminOrFinanceController()
    {
        var method = typeof(PayrollController).GetMethod(nameof(PayrollController.VoidRun));
        method.Should().NotBeNull("VoidRun endpoint must exist");

        var attr = method!.GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .FirstOrDefault();
        attr.Should().NotBeNull("[Authorize] must be present on VoidRun");

        var roles = (attr!.Roles ?? string.Empty)
            .Split(',').Select(r => r.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        roles.Should().Contain("Admin",              "Admin must be able to void a run");
        roles.Should().Contain("Finance Controller", "Finance Controller must be able to void a run");
        roles.Should().NotContain("Payroll Officer", "Payroll Officer must NOT be able to void a run");
        roles.Should().NotContain("HR Manager",      "HR Manager must NOT be able to void payroll runs");
    }

    // ── (f) Voided run excluded from GL totals; period re-usable ────────────────

    [Fact]
    public async Task VoidedRun_PeriodReusable_ReplacementRunGlIsClean()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);
        var company         = await SeedKsaCompany(db, tenantId);

        // Run A: wrong run for 2026-06 (simulates a 0-GOSI stale run)
        var runA = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 6, Status = "Processed",
            TotalGrossSalary = 10_000m, TotalDeductions = 0m, TotalNetSalary = 10_000m,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(runA);
        await db.SaveChangesAsync();

        (await BuildCtrl(db, tenantId, "Finance Controller")
            .VoidRun(runA.Id, new PayrollDecisionRequest("Stale 0-GOSI batch — voiding before reprocess"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("void must succeed");

        // Run B: replacement for the SAME period — must not be blocked by unique constraint
        var runB = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 6, Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(runB);
        var saveAct = async () => await db.SaveChangesAsync();
        await saveAct.Should().NotThrowAsync(
            "partial unique index must allow a new run for the same period after the old one is voided");

        var ctrl = BuildCtrl(db, tenantId, "Finance Controller");
        (await ctrl.Process(runB.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("replacement run must process successfully");

        runB.Status = "Processed";
        await db.SaveChangesAsync();
        (await ctrl.Lock(runB.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("replacement run lock must succeed");

        // RunB GL must exist and not be reversed — these are the live entries
        (await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.SourceModule == "Payroll" && g.SourceEntityId == runB.Id)
            .ToListAsync())
            .Should().NotBeEmpty("replacement run must produce GL entries after lock")
            .And.OnlyContain(g => !g.IsReversed, "replacement GL must not be pre-reversed");

        // RunA (voided, never locked) must have zero GL
        (await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.SourceModule == "Payroll" && g.SourceEntityId == runA.Id)
            .CountAsync())
            .Should().Be(0, "voided run that was never locked must have no GL entries");
    }

    // ── (g) No KSA Saudi 0-GOSI can survive void + reprocess cycle ──────────────

    [Fact]
    public async Task VoidAndReprocess_KsaSaudi_ProducesNonZeroGosi()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);
        var company         = await SeedKsaCompany(db, tenantId);

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"VD-S-{Guid.NewGuid():N}",
            FullName = "Saudi Void Test", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 10_000m, HousingAllowance = 2_000m,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = "MOL-VD01", SalaryCurrency = "SAR",
        });

        // Stale run: manually built with wrong (0-GOSI) numbers — pre-fix production data
        var staleRun = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 7, Status = "Processed",
            TotalGrossSalary = 12_000m, TotalDeductions = 0m, TotalNetSalary = 12_000m,
            EmployeeCount = 1,
            CreatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var staleSlip = new PayrollSlip
        {
            TenantId = tenantId, RunId = staleRun.Id, EmployeeId = emp.Id,
            EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, GrossSalary = 12_000m,
            Deductions = 0m, NetSalary = 12_000m,    // net == gross: the wrong pre-fix state
            EmployeeStatutoryTotal = 0m, EmployerStatutoryTotal = 0m,
            Status = "Draft",
        };
        db.PayrollRuns.Add(staleRun);
        db.PayrollSlips.Add(staleSlip);
        await db.SaveChangesAsync();

        (await BuildCtrl(db, tenantId, "Finance Controller")
            .VoidRun(staleRun.Id, new PayrollDecisionRequest("0-GOSI void"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        (await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.Id == staleSlip.Id))
            .Status.Should().Be("Voided");

        var freshRun = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 7, Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(freshRun);
        await db.SaveChangesAsync();

        (await BuildCtrl(db, tenantId, "Finance Controller").Process(freshRun.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("fresh run must process with the KSA pack");

        // CRITICAL: net < gross for the Saudi employee — GOSI must have been applied
        var freshSlip = await db.PayrollSlips.AsNoTracking()
            .FirstAsync(s => s.RunId == freshRun.Id && s.EmployeeId == emp.Id);
        freshSlip.NetSalary.Should().BeLessThan(freshSlip.GrossSalary,
            "Saudi employee must have GOSI deductions — net must be less than gross");
        freshSlip.EmployeeStatutoryTotal.Should().BeGreaterThan(0m,
            "EmployeeStatutoryTotal must be non-zero for a Saudi KSA employee");

        (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == freshRun.Id).ToListAsync())
            .Should().NotContain(s => s.Status == "Voided",
                "fresh run slips must not inherit the voided status from the stale run");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private static async Task<(Company company, Employee emp, PayrollRun run)> SeedKsaRunProcessed(
        ZayraDbContext db, Guid tenantId, int year, int month)
    {
        var company = await SeedKsaCompany(db, tenantId);

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"VD-{Guid.NewGuid():N}",
            FullName = "Void Test Employee", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 10_000m, HousingAllowance = 2_000m,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = $"MOL-VT-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = year, Month = month, Status = "Draft",
            CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        await BuildCtrl(db, tenantId, "Finance Controller").Process(run.Id, CancellationToken.None);

        db.ChangeTracker.Clear();
        run = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        return (company, emp, run);
    }

    private static async Task<Company> SeedKsaCompany(ZayraDbContext db, Guid tenantId)
    {
        var existing = await db.Companies.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.IsActive);
        if (existing is not null) return existing;

        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Void Test KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"VD-REG-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static PayrollController BuildCtrl(ZayraDbContext db, Guid tenantId, string role = "Admin")
    {
        var ctrl = new PayrollController(
            db,
            new DataScopeService(db),
            new HttpContextAccessor(),
            new VoidNullNotifications(),
            new VoidKsaPackResolver(BuildKsaRules()),
            BuildKsaRules(),
            new VoidNullLetterStub(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id",               tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name,           "void-test-user"),
                    new Claim(ClaimTypes.Role,           role),
                }, "Test"))
            }
        };
        return ctrl;
    }

    private static StubRuleReader BuildKsaRules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);
}

// ── Test-local stubs ────────────────────────────────────────────────────────────

file sealed class VoidKsaPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public VoidKsaPackResolver(IStatutoryRuleReader r) => _r = r;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? new KsaDeductionCalculator(_r) : (IStatutoryDeductionCalculator)new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator       ResolveEndOfServiceCalculator(string cc, string j)   => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter       ResolveWageProtectionExporter(string cc, string j)   => new DefaultWageProtectionExporter();
    public INationalizationTracker       ResolveNationalizationTracker(string cc, string j)   => new DefaultNationalizationTracker();
    public ILocalizationProfile          ResolveLocalizationProfile(string cc, string j)      => new DefaultLocalizationProfile();
    public ICountryPackDescriptor        ResolveDescriptor(string cc, string j)               => new DefaultCountryPackDescriptor();
}

file sealed class VoidNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? id, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string tpl, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class VoidNullLetterStub : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default)     => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default)  => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default)  => Task.FromResult(Array.Empty<byte>());
}
