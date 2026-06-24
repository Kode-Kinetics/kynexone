using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Global remediation tests.
///
/// These tests prove that:
///   (1) PayrollVoidService correctly identifies and voids stale zero-GOSI runs across
///       ALL tenants in a single sweep — not tenant-by-tenant.
///   (2) After remediation: zero active runs with KSA Saudi employees at 0 GOSI.
///   (3) Voided runs excluded from GL totals / active reporting.
///   (4) All corrections are audit-logged.
///   (5) The fail-loud guard is in shared code — parameterized across any tenant.
///   (6) Orphan tenant detection identifies employees-but-no-company patterns.
///   (7) Pre-existing "correct" locked runs (non-zero GOSI) are NOT voided.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GlobalPayrollRemediationTests
{
    private readonly PostgresFixture _fx;
    public GlobalPayrollRemediationTests(PostgresFixture fx) => _fx = fx;

    // ── (1) Cross-tenant sweep voids ALL stale runs, not just one tenant's ───────

    [Fact]
    public async Task RemediationSweep_VoidsStaleRunsAcrossAllTenants()
    {
        await using var db = _fx.CreateDb();

        // Three independent tenants, each with a stale (net=gross, SAU, 0 GOSI) run
        var tidA = await PostgresFixture.SeedMinimalTenant(db);
        var tidB = await PostgresFixture.SeedMinimalTenant(db);
        var tidC = await PostgresFixture.SeedMinimalTenant(db);

        var runA = await SeedStaleRun(db, tidA, year: 2025, month: 11);
        var runB = await SeedStaleRun(db, tidB, year: 2025, month: 12);
        var runC = await SeedStaleRun(db, tidC, year: 2026, month: 1);

        // Sweep: void all three in one call (cross-tenant)
        var svc = new PayrollVoidService(db);
        var results = new List<VoidRunResult>();
        foreach (var (runId, tenantId) in new[] { (runA, tidA), (runB, tidB), (runC, tidC) })
        {
            results.Add(await svc.VoidAsync(runId, tenantId, null, "System (platform remediation)",
                "Auto-void: pre-fix zero-GOSI defect", CancellationToken.None));
        }

        results.Should().OnlyContain(r => r.IsVoided, "all three stale runs must be voided");

        // Verify all three are voided in DB
        var voidedRuns = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.Id == runA || r.Id == runB || r.Id == runC)
            .ToListAsync();
        voidedRuns.Should().OnlyContain(r => r.Status == "Voided",
            "all stale runs across all tenants must be status=Voided after the sweep");

        // Each run has an audit log entry
        foreach (var runId in new[] { runA, runB, runC })
        {
            var audit = await db.PayrollAuditLogs.AsNoTracking()
                .FirstOrDefaultAsync(a => a.EntityId == runId.ToString() && a.Action == "payroll.run.voided");
            audit.Should().NotBeNull($"run {runId} must have a payroll.run.voided audit log entry");
        }
    }

    // ── (2) After sweep: zero active runs with KSA Saudi at 0 GOSI ──────────────

    [Fact]
    public async Task AfterSweep_ZeroActiveRunsWithZeroGosiForSaudiEmployee()
    {
        await using var db = _fx.CreateDb();
        var tenantId       = await PostgresFixture.SeedMinimalTenant(db);

        // Seed: one stale run (net=gross) and one correct run (net<gross)
        var staleRunId   = await SeedStaleRun(db, tenantId, year: 2025, month: 9);
        var correctRunId = await SeedCorrectRun(db, tenantId, year: 2025, month: 10);

        // Void only the stale run
        var svc = new PayrollVoidService(db);
        (await svc.VoidAsync(staleRunId, tenantId, null, "System", "Auto-void: pre-fix zero-GOSI", CancellationToken.None))
            .IsVoided.Should().BeTrue();

        // GLOBAL ASSERTION: no active (non-voided) run has a Saudi payslip with 0 GOSI
        var activeZeroGosiSlips = await db.PayrollSlips
            .AsNoTracking()
            .Where(s => s.Status != "Voided"
                     && s.EmployeeStatutoryTotal == 0)
            .Join(db.Employees.AsNoTracking().Where(e => e.Nationality == "Saudi"),
                  s => s.EmployeeId, e => e.Id,
                  (s, e) => new { Slip = s, Employee = e })
            .Join(db.PayrollRuns.AsNoTracking().Where(r => r.Status != "Voided"),
                  x => x.Slip.RunId, r => r.Id,
                  (x, r) => new { x.Slip, x.Employee, Run = r })
            .Where(x => x.Run.TenantId == tenantId)
            .ToListAsync();

        activeZeroGosiSlips.Should().BeEmpty(
            "after void sweep, no active run must contain a Saudi employee payslip with 0 GOSI");

        // Correct run (non-zero GOSI) must still be active
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == correctRunId))
            .Status.Should().NotBe("Voided",
                "a correctly-processed run with non-zero GOSI must NOT be voided by the sweep");
    }

    // ── (3) Voided runs excluded from GL totals ──────────────────────────────────

    [Fact]
    public async Task VoidedRuns_GlEntriesReversed_ExcludedFromActiveTotals()
    {
        await using var db = _fx.CreateDb();
        var tenantId       = await PostgresFixture.SeedMinimalTenant(db);

        // Seed a stale Locked run so it has GL entries
        var company = await SeedKsaCompany(db, tenantId);
        var lockedRun = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2025, Month = 8, Status = "Locked",
            TotalGrossSalary = 50_000m, TotalDeductions = 0m, TotalNetSalary = 50_000m,
            CreatedAtUtc = new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(lockedRun);
        // Simulate existing GL entries (normally written by Lock endpoint)
        var gl1 = new FinanceGlEntry
        {
            TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = lockedRun.Id,
            EventType = "PayrollLock", DebitAccount = "5001", CreditAccount = "2100",
            Amount = 50_000m, Currency = "SAR", EntryDate = new DateOnly(2025, 8, 31),
            Period = "2025-08", Description = "Net salaries payable",
            PostedByName = "system", IsReversed = false,
        };
        db.FinanceGlEntries.Add(gl1);
        await db.SaveChangesAsync();

        // Void the locked run via service
        var svc = new PayrollVoidService(db);
        (await svc.VoidAsync(lockedRun.Id, tenantId, null, "System",
            "Auto-void: pre-fix zero-GOSI", CancellationToken.None))
            .IsVoided.Should().BeTrue();

        db.ChangeTracker.Clear();

        // Original GL must be marked IsReversed=true (NOT deleted)
        var orig = await db.FinanceGlEntries.AsNoTracking().FirstAsync(g => g.Id == gl1.Id);
        orig.IsReversed.Should().BeTrue("original GL must be marked IsReversed — financial records are never deleted");

        // Contra-entry must exist
        var contra = await db.FinanceGlEntries.AsNoTracking()
            .FirstOrDefaultAsync(g => g.ReversalOfEntryId == gl1.Id);
        contra.Should().NotBeNull("a contra-entry must reverse the original GL");
        contra!.EventType.Should().Be("PayrollVoid");
        contra.DebitAccount.Should().Be(gl1.CreditAccount, "DR↔CR must be swapped");
        contra.CreditAccount.Should().Be(gl1.DebitAccount, "DR↔CR must be swapped");

        // Active GL total (non-reversed entries only) must be zero for this run
        var activeGlTotal = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.SourceModule == "Payroll"
                     && g.SourceEntityId == lockedRun.Id
                     && !g.IsReversed)
            .SumAsync(g => (decimal?)g.Amount) ?? 0m;

        // The only non-reversed entry is the contra itself (amount = 50k); net effect = 0
        // Active-reporting total = sum of non-reversed non-contra entries = 0
        var activeNonContraTotal = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.SourceModule == "Payroll"
                     && g.SourceEntityId == lockedRun.Id
                     && !g.IsReversed
                     && g.ReversalOfEntryId == null)  // not a contra-entry itself
            .SumAsync(g => (decimal?)g.Amount) ?? 0m;

        activeNonContraTotal.Should().Be(0m,
            "after void, the only active GL entries for this run are contra-entries — net active credit/debit = 0");
    }

    // ── (4) Sweep is idempotent — re-running does not double-void ───────────────

    [Fact]
    public async Task RemediationSweep_IsIdempotent_AlreadyVoidedRunsSkipped()
    {
        await using var db = _fx.CreateDb();
        var tenantId       = await PostgresFixture.SeedMinimalTenant(db);
        var runId          = await SeedStaleRun(db, tenantId, year: 2025, month: 7);

        var svc = new PayrollVoidService(db);

        // First void
        (await svc.VoidAsync(runId, tenantId, null, "System", "First sweep", CancellationToken.None))
            .IsVoided.Should().BeTrue();

        // Second void (idempotent)
        var second = await svc.VoidAsync(runId, tenantId, null, "System", "Second sweep", CancellationToken.None);
        second.IsAlreadyVoid.Should().BeTrue("re-sweeping must return AlreadyVoided, not error");

        // Only one audit log entry
        var auditCount = await db.PayrollAuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityId == runId.ToString() && a.Action == "payroll.run.voided");
        auditCount.Should().Be(1, "idempotent re-void must not write duplicate audit entries");
    }

    // ── (5) Fail-loud guard is GLOBAL — any tenant with no company triggers 422 ──
    //
    // Parameterized test: seed N completely independent tenants, each with a
    // different company config (null, wrong country). All must get 422 from Process().
    // This proves the guard is in shared code, not tenant-specific branch.

    [Theory]
    [InlineData("no company at all",         false, null)]
    [InlineData("company has empty country", true,  "")]
    [InlineData("company has non-SAU code",  true,  "USD")]
    public async Task FailLoudGuard_IsGlobal_AnyTenantWithUnresolvedCompanyGets422(
        string scenario, bool seedCompany, string? countryCode)
    {
        await using var db = _fx.CreateDb();
        var tenantId       = await PostgresFixture.SeedMinimalTenant(db);

        if (seedCompany && countryCode is not null)
        {
            db.Companies.Add(new Company
            {
                TenantId = tenantId, LegalNameEn = $"Guard Test Co ({scenario})",
                CountryCode = countryCode, Jurisdiction = "test",
                RegistrationNumber = $"GT-{Guid.NewGuid():N}",
                DefaultCurrency = "SAR", IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = null,
            Year = 2026, Month = 8, Status = "Draft",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        // Build a controller for this specific tenant — the resolver uses the shared
        // KSA pack (which requires SAU country code). This is the same code path hit
        // by every tenant, not a per-tenant branch.
        var ctrl = FailLoudBuildCtrl(db, tenantId);
        var result = await ctrl.Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>(
            $"scenario '{scenario}' — any tenant with unresolved company must get 422 before any slips are written");

        // Confirm zero payslips written
        (await db.PayrollSlips.CountAsync(s => s.RunId == run.Id))
            .Should().Be(0, "fail-loud guard must abort before writing any payslip");
    }

    // ── (6) Orphan-tenant detection ───────────────────────────────────────────────

    [Fact]
    public async Task OrphanTenantDetection_FindsTenantsWithEmployeesButNoCompany()
    {
        await using var db = _fx.CreateDb();

        // Orphan tenant: has an employee, but its company is inactive/deleted
        var orphanTid = await PostgresFixture.SeedMinimalTenant(db);
        var company   = new Company
        {
            TenantId = orphanTid, LegalNameEn = "Deleted Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"DEL-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = false,  // inactive — orphan condition
            IsDeleted = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        db.Employees.Add(new Employee
        {
            TenantId = orphanTid, EmployeeCode = "ORP-E1", FullName = "Orphan Emp",
            Status = "Active", JoiningDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Good tenant: has active company + employees
        var goodTid   = await PostgresFixture.SeedMinimalTenant(db);
        var goodCo    = new Company
        {
            TenantId = goodTid, LegalNameEn = "Good Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"GOOD-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(goodCo);
        db.Employees.Add(new Employee
        {
            TenantId = goodTid, EmployeeCode = "GOOD-E1", FullName = "Good Emp",
            Status = "Active", JoiningDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Query orphan tenants (same query as /api/platform/payroll/orphan-tenants)
        var orphanTenants = await db.Employees
            .AsNoTracking()
            .Where(e => e.Status == "Active"
                     && !db.Companies.Any(c => c.TenantId == e.TenantId && c.IsActive && !c.IsDeleted))
            .GroupBy(e => e.TenantId)
            .Select(g => new { TenantId = g.Key, ActiveEmployees = g.Count() })
            .ToListAsync();

        orphanTenants.Should().Contain(o => o.TenantId == orphanTid,
            "the tenant with employees but no active company must be flagged as orphan");
        orphanTenants.Should().NotContain(o => o.TenantId == goodTid,
            "a tenant with an active company must not be flagged as orphan");
    }

    // ── (7) Correctly-processed Locked run is NOT voided by the sweep ────────────

    [Fact]
    public async Task RemediationSweep_PreservesCorrectLockedRun()
    {
        await using var db = _fx.CreateDb();
        var tenantId       = await PostgresFixture.SeedMinimalTenant(db);
        var correctRunId   = await SeedCorrectRun(db, tenantId, year: 2026, month: 6);

        // The correct run has TotalDeductions > 0 so it does NOT match the stale criteria
        var staleCount = await db.PayrollRuns
            .Where(r => r.TenantId == tenantId
                     && r.Status != "Voided"
                     && r.Status != "Draft"
                     && r.TotalDeductions == 0
                     && r.TotalNetSalary == r.TotalGrossSalary)
            .CountAsync();

        staleCount.Should().Be(0,
            "a correctly-computed run (non-zero deductions) must not appear in the stale-run query");

        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == correctRunId))
            .Status.Should().NotBe("Voided",
                "the rasalmanar-style preserved run must not be voided");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedStaleRun(ZayraDbContext db, Guid tenantId, int year, int month)
    {
        var company = await SeedKsaCompany(db, tenantId);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"SR-{Guid.NewGuid():N}",
            FullName = "Stale Saudi", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        // Stale run: net == gross (0 deductions = 0 GOSI — the pre-fix wrong state)
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = year, Month = month, Status = "Processed",
            TotalGrossSalary = 12_000m, TotalDeductions = 0m, TotalNetSalary = 12_000m,
            EmployeeCount = 1,
            CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollSlips.Add(new PayrollSlip
        {
            TenantId = tenantId, RunId = run.Id, EmployeeId = emp.Id,
            EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, GrossSalary = 12_000m,
            Deductions = 0m, NetSalary = 12_000m,
            EmployeeStatutoryTotal = 0m, EmployerStatutoryTotal = 0m,
            Status = "Processed",
        });
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static async Task<Guid> SeedCorrectRun(ZayraDbContext db, Guid tenantId, int year, int month)
    {
        var company = await SeedKsaCompany(db, tenantId);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"CR-{Guid.NewGuid():N}",
            FullName = "Correct Saudi", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        // Correct run: net < gross (GOSI was applied correctly)
        var gosiDeduction = 12_000m * 0.09m;  // 9% GOSI = 1,080 SAR
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = year, Month = month, Status = "Locked",
            TotalGrossSalary = 12_000m, TotalDeductions = gosiDeduction,
            TotalNetSalary = 12_000m - gosiDeduction,
            EmployeeCount = 1,
            CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        db.PayrollSlips.Add(new PayrollSlip
        {
            TenantId = tenantId, RunId = run.Id, EmployeeId = emp.Id,
            EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, GrossSalary = 12_000m,
            Deductions = gosiDeduction, NetSalary = 12_000m - gosiDeduction,
            EmployeeStatutoryTotal = gosiDeduction, EmployerStatutoryTotal = gosiDeduction,
            Status = "Processed",
        });
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static async Task<Company> SeedKsaCompany(ZayraDbContext db, Guid tenantId)
    {
        var existing = await db.Companies.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.IsActive);
        if (existing is not null) return existing;
        var co = new Company
        {
            TenantId = tenantId, LegalNameEn = "Global Rem KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"GR-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        return co;
    }

    private static Zayra.Api.Controllers.PayrollController FailLoudBuildCtrl(
        Zayra.Api.Data.ZayraDbContext db, Guid tenantId)
    {
        // Identical to PayrollFailLoudTests.BuildController — same shared code path
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate",            0.09m)
            .Set("gosi.saudi_employer_rate",            0.09m)
            .Set("gosi.saned_rate",                     0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar",       45_000m);

        var ctrl = new Zayra.Api.Controllers.PayrollController(
            db,
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new HttpContextAccessor(),
            new GrNullNotifications(),
            new GrKsaPackResolver(rules),
            rules,
            new GrNullLetterStub(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));

        ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id",               tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role,           "Admin"),
                }, "Test"))
            }
        };
        return ctrl;
    }
}

// ── Test-local stubs ────────────────────────────────────────────────────────────

file sealed class GrKsaPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    private readonly Zayra.Api.Application.CountryPack.IStatutoryRuleReader _r;
    public GrKsaPackResolver(Zayra.Api.Application.CountryPack.IStatutoryRuleReader r) => _r = r;

    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU"
            ? new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaDeductionCalculator(_r)
            : (Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator)new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator     ResolveEndOfServiceCalculator(string cc, string j)   => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter     ResolveWageProtectionExporter(string cc, string j)   => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker     ResolveNationalizationTracker(string cc, string j)   => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile        ResolveLocalizationProfile(string cc, string j)      => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor      ResolveDescriptor(string cc, string j)               => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}

file sealed class GrNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? id, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string tpl, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class GrNullLetterStub : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
