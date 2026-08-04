using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B2 — off-cycle / supplementary / correction run types.
///
/// Proves the four things that make multiple runs per period safe:
///   • exactly one non-voided REGULAR run per (tenant, company, period) — including null-company rows,
///     which the company-scoped index cannot constrain (Postgres NULLs are distinct);
///   • a supplemental run pays supplemental items ONLY (no second salary, no second EMI, no impact
///     consumption) and a cross-run ALREADY_PAID_THIS_PERIOD Error catches any run that tries anyway;
///   • the population selector is honoured by BOTH Process and Validate, is reported, and must be
///     acknowledged at Approve;
///   • a bonus is consumed by exactly one run, enforced by a compare-and-swap, not by convention.
/// </summary>
public class PayrollRunTypeTests
{
    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId, string user = "test-user")
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, user),
            new("permission", "payroll.write"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.approve"),
            new("permission", "payroll.export"),
            new("permission", "payroll.run_delete"),
            new(ClaimTypes.Role, "Admin"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(
            db, new _RtUnrestrictedScope(), new _RtHttpAccessor(httpCtx), new _RtNullNotifications(),
            new _RtKsaPackResolver(), _RtKsaRules.Rules, new _RtNullLetterService(), new _RtNullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    /// <summary>Two employees, a KSA company, salary structures, and NO run. Returns (companyId, empA, empB).</summary>
    private static async Task<(Guid companyId, Employee a, Employee b)> SeedCompanyAndEmployees(
        ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Test KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "STR-BASE", Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);

        var a = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E001", FullName = "Ali Hassan",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "ali@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        var b = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E002", FullName = "Bilal Omar",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "bilal@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.AddRange(a, b);
        await db.SaveChangesAsync();

        foreach (var e in new[] { a, b })
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = structure.Id,
                BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
                EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = e.Id,
                Iban = "SA4420000001234567891234", MolId = $"MOL-{e.EmployeeCode}", SalaryCurrency = "SAR",
            });
        }
        await db.SaveChangesAsync();
        return (company.Id, a, b);
    }

    private static PayrollRun AddRun(ZayraDbContext db, Guid tenantId, Guid? companyId, int year, int month,
        string runType = PayrollRunTypes.Regular, string status = "Draft", bool includesRecurringPay = true)
    {
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = year, Month = month,
            Status = status, RunType = runType, IncludesRecurringPay = includesRecurringPay,
        };
        db.PayrollRuns.Add(run);
        return run;
    }

    private static async Task<(Guid batchId, Guid bonusId)> SeedApprovedBonus(
        ZayraDbContext db, Guid tenantId, Guid companyId, Employee emp, int year, int month, decimal gross)
    {
        var type = new BonusType
        {
            TenantId = tenantId, Code = "PERF", NameEn = "Performance", IsIncludedInGosiBase = false, IsActive = true,
        };
        db.BonusTypes.Add(type);
        var batch = new BonusBatch
        {
            TenantId = tenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchName = "B1",
            PaymentPeriod = $"{year}-{month:D2}", Status = "Approved",
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        var bonus = new EmployeeBonus
        {
            TenantId = tenantId, CompanyId = companyId, BonusBatchId = batch.Id, BonusTypeId = type.Id,
            BonusTypeName = type.NameEn, EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
            GrossBonusAmount = gross, BonusAmount = gross, TaxWithheld = 0m,
            PaymentPeriod = $"{year}-{month:D2}", Status = "Approved",
        };
        db.EmployeeBonuses.Add(bonus);
        await db.SaveChangesAsync();
        return (batch.Id, bonus.Id);
    }

    private static async Task IncludeAsync(PayrollController ctrl, Guid runId, params int[] empIds) =>
        (await ctrl.UpsertRunSelection(runId,
            new PayrollRunSelectionRequest("Include", "Off-cycle bonus payment", empIds.ToList()),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();

    // ── 1. PayrollRunTypes.Normalize ────────────────────────────────────────────

    [Theory]
    [InlineData("Regular", "Regular")]
    [InlineData("offcycle", "OffCycle")]
    [InlineData("  CORRECTION  ", "Correction")]
    [InlineData("supplementary", "Supplementary")]
    [InlineData(null, "Regular")]
    [InlineData("", "Regular")]
    public void Normalize_IsCaseInsensitive_AndDefaultsToRegular(string? input, string expected) =>
        PayrollRunTypes.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("Reglar")]
    [InlineData("Replacement")]
    [InlineData("off-cycle")]
    public void Normalize_UnknownType_ReturnsNull_NeverSilentlyCoercesToRegular(string input) =>
        PayrollRunTypes.Normalize(input).Should().BeNull(
            "a typo'd run type must 400, never become the tenant's monthly run");

    // ── 2. CreateRun: type-aware conflict ───────────────────────────────────────

    [Fact]
    public async Task CreateRun_SecondRegular_Is409_ButEveryNonRegularTypeIsCreated()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();

        var second = await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId), CancellationToken.None);
        second.Should().BeOfType<ConflictObjectResult>();
        second.As<ConflictObjectResult>().Value!.ToString().Should().Contain("regular_run_exists");

        foreach (var t in new[] { PayrollRunTypes.OffCycle, PayrollRunTypes.Supplementary })
            (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, t), CancellationToken.None))
                .Should().BeOfType<CreatedResult>($"{t} runs may coexist with the regular run");

        // Two OffCycle runs in the same period are also legal.
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "OffCycle"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();

        (await db.PayrollRuns.CountAsync(r => r.TenantId == tenantId && r.Year == 2026 && r.Month == 6))
            .Should().Be(4);
    }

    [Fact]
    public async Task CreateRun_InvalidRunType_Is400()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        var res = await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "Replacement"), CancellationToken.None);
        res.Should().BeOfType<BadRequestObjectResult>();
        res.As<BadRequestObjectResult>().Value!.ToString().Should().Contain("invalid_run_type");
    }

    /// <summary>D1 — the 409 had NO status predicate, so a voided run bricked the period forever even
    /// though the partial index has carried `WHERE status != 'Voided'` since 20260624000001.</summary>
    [Fact]
    public async Task CreateRun_AfterVoidingTheRegularRun_IsAllowed()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var first = await db.PayrollRuns.FirstAsync(r => r.TenantId == tenantId);
        first.Status = "Voided";
        await db.SaveChangesAsync();

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId), CancellationToken.None))
            .Should().BeOfType<CreatedResult>("voiding a run must free the period — this is the POD-B3 seam");
    }

    /// <summary>M1 — a null-company run is UNSCOPED and competes with every company in the tenant.
    /// Postgres unique indexes treat NULL as distinct, so the company-scoped index cannot catch this.</summary>
    [Fact]
    public async Task CreateRun_WhenAnUnscopedRegularRunExists_Is409()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        // Exactly what AuthSeeder / DemoDataSeeder create: a run with no CompanyId.
        AddRun(db, tenantId, null, 2026, 6);
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var res = await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId), CancellationToken.None);
        res.Should().BeOfType<ConflictObjectResult>();
        res.As<ConflictObjectResult>().Value!.ToString().Should().Contain("regular_run_exists");
    }

    // ── 3. Parent-run validation ────────────────────────────────────────────────

    [Fact]
    public async Task CreateRun_ParentLinkRules_AreEnforced()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        var parent = AddRun(db, tenantId, companyId, 2026, 6, status: "Draft");
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        // Regular + parent → 400
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 7, companyId, "Regular", parent.Id), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("parent_not_allowed");

        // Correction without a parent → 400
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 7, companyId, "Correction"), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("parent_required");

        // Draft parent produced nothing to amend → 400
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 7, companyId, "Correction", parent.Id), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("parent_not_processed");

        parent.Status = "Locked";
        await db.SaveChangesAsync();
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 7, companyId, "Correction", parent.Id), CancellationToken.None))
            .Should().BeOfType<CreatedResult>("a correction booked in M+1 for M is normal practice");

        parent.Status = "Voided";
        await db.SaveChangesAsync();
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 8, companyId, "Correction", parent.Id), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("parent_voided");
    }

    // ── 4. The DB constraint itself ─────────────────────────────────────────────

    [Fact]
    public async Task DbIndex_RejectsTwoRegularRuns_ButAcceptsManyOffCycleRuns()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);

        // M10: assert the emitted DDL carries the partial predicate BEFORE asserting behaviour — the
        // "many off-cycle runs coexist" assertion would otherwise pass for the wrong reason if the
        // provider silently dropped the WHERE clause.
        var ddl = await db.Database.SqlQuery<string?>(
                $"SELECT sql AS \"Value\" FROM sqlite_master WHERE name = 'IX_payroll_runs_tenant_id_company_id_year_month'")
            .FirstOrDefaultAsync();
        ddl.Should().NotBeNull();
        ddl!.Should().Contain("run_type", "the unique index must be filtered to Regular runs");
        ddl.Should().Contain("Voided");

        AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        for (var i = 0; i < 5; i++) AddRun(db, tenantId, companyId, 2026, 6, PayrollRunTypes.OffCycle, includesRecurringPay: false);
        await db.SaveChangesAsync();
        (await db.PayrollRuns.CountAsync()).Should().Be(6);

        AddRun(db, tenantId, companyId, 2026, 6);
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("two regular runs for one period must be impossible at the DB level");
    }

    // ── 5. D2: a Voided run cannot be reprocessed ───────────────────────────────

    [Fact]
    public async Task Process_OnAVoidedRun_Is400_NotADbUpdateException()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        var voided = AddRun(db, tenantId, companyId, 2026, 6, status: "Voided");
        AddRun(db, tenantId, companyId, 2026, 6);   // the live replacement
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var res = await ctrl.Process(voided.Id, CancellationToken.None);
        res.Should().BeOfType<BadRequestObjectResult>();
        res.As<BadRequestObjectResult>().Value!.ToString().Should().Contain("run_voided");
    }

    // ── 6. Population selector ──────────────────────────────────────────────────

    [Fact]
    public async Task Selection_OnRegularRun_RejectsInclude_ButAcceptsExclude()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, b) = await SeedCompanyAndEmployees(db, tenantId);
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.UpsertRunSelection(run.Id, new PayrollRunSelectionRequest("Include", "why", new List<int> { a.Id }), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("include_not_allowed_on_regular_run");

        (await ctrl.UpsertRunSelection(run.Id, new PayrollRunSelectionRequest("Exclude", "On unpaid leave", new List<int> { b.Id }), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Selection_RequiresAReason()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.UpsertRunSelection(run.Id, new PayrollRunSelectionRequest("Exclude", "   ", new List<int> { a.Id }), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("reason_required");
    }

    [Fact]
    public async Task Selection_IsUpsert_NotDuplicate()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        await ctrl.UpsertRunSelection(run.Id, new PayrollRunSelectionRequest("Exclude", "reason one", new List<int> { a.Id }), CancellationToken.None);
        await ctrl.UpsertRunSelection(run.Id, new PayrollRunSelectionRequest("Exclude", "reason two", new List<int> { a.Id }), CancellationToken.None);

        var rows = await db.PayrollRunEmployeeSelections.Where(s => s.PayrollRunId == run.Id).ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].Reason.Should().Be("reason two");
    }

    [Fact]
    public async Task Selection_IsLockedOnceTheRunIsProcessed()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var run = AddRun(db, tenantId, companyId, 2026, 6, status: "Locked");
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.UpsertRunSelection(run.Id, new PayrollRunSelectionRequest("Exclude", "too late", new List<int> { a.Id }), CancellationToken.None))
            .As<ConflictObjectResult>().Value!.ToString().Should().Contain("population_locked");
    }

    /// <summary>A non-Regular run over the whole company is almost always operator error, because a
    /// supplemental run consumes the period's bonuses and adjustments out from under the Regular run.</summary>
    [Fact]
    public async Task Process_NonRegularRunWithoutAnIncludeSelection_Is422()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        var run = AddRun(db, tenantId, companyId, 2026, 6, PayrollRunTypes.OffCycle, includesRecurringPay: false);
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        var res = await ctrl.Process(run.Id, CancellationToken.None);
        res.As<UnprocessableEntityObjectResult>().Value!.ToString().Should().Contain("run_population_required");
    }

    // ── 7. THE HEADLINE: an off-cycle run must not pay a second salary ──────────

    [Fact]
    public async Task OffCycleRun_PaysOnlyTheBonus_NoSecondSalary_NoSecondEmi_NoImpactConsumption()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, b) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        // A has an active loan; B has an unprocessed attendance impact in the period.
        var loan = new EmployeeLoan
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeIntId = a.Id, EmployeeName = a.FullName,
            ApprovedAmount = 6_000m, OutstandingBalance = 6_000m, InstallmentAmount = 1_000m,
            Status = "Active", RepaymentStartDate = new DateOnly(2026, 1, 1),
        };
        db.EmployeeLoans.Add(loan);
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = tenantId, EmployeeId = b.Id, WorkDate = new DateOnly(2026, 6, 10),
            ImpactType = "Absence", Minutes = 480, Status = "Pending",
        });
        await db.SaveChangesAsync();

        // Regular run pays both, takes A's EMI, consumes B's absence.
        var regular = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await ctrl.Process(regular.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var loanAfterRegular = (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loan.Id)).OutstandingBalance;
        loanAfterRegular.Should().Be(5_000m);

        // Off-cycle bonus run for A only.
        await SeedApprovedBonus(db, tenantId, companyId, a, 2026, 6, 3_000m);
        db.ChangeTracker.Clear();
        var offCycle = AddRun(db, tenantId, companyId, 2026, 6, PayrollRunTypes.OffCycle, includesRecurringPay: false);
        await db.SaveChangesAsync();
        await IncludeAsync(ctrl, offCycle.Id, a.Id);
        (await ctrl.Process(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // A's off-cycle slip carries the BONUS ONLY — no BASIC row, gross == the bonus.
        var ocSlips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == offCycle.Id).ToListAsync();
        ocSlips.Should().HaveCount(1, "only the included employee is paid");
        ocSlips[0].EmployeeId.Should().Be(a.Id);
        ocSlips[0].BasicSalary.Should().Be(0m, "a supplemental run pays no recurring salary");
        ocSlips[0].GrossSalary.Should().Be(3_000m);

        var ocEarnings = await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == offCycle.Id).ToListAsync();
        ocEarnings.Should().NotContain(e => e.ComponentCode == "BASIC",
            "BASIC is SKIPPED (not emitted at 0.00) so no empty GL group is produced");
        ocEarnings.Should().OnlyContain(e => e.Source == "Bonus");

        // A's loan was NOT decremented a second time.
        (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loan.Id)).OutstandingBalance
            .Should().Be(5_000m, "a supplemental run collects no EMI, so it must retire no debt");
        (await db.PayrollDeductions.AsNoTracking()
            .AnyAsync(d => d.PayrollRunId == offCycle.Id && d.ComponentCode == "LOAN_EMI"))
            .Should().BeFalse();

        // B is untouched by the off-cycle run.
        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == offCycle.Id && s.EmployeeId == b.Id))
            .Should().BeFalse();
    }

    /// <summary>An off-cycle run must not eat the period's impacts for employees it did not pay.</summary>
    [Fact]
    public async Task SupplementalRun_DoesNotConsumeThePeriodsAttendanceImpacts()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = tenantId, EmployeeId = a.Id, WorkDate = new DateOnly(2026, 6, 10),
            ImpactType = "Absence", Minutes = 480, Status = "Pending",
        });
        await SeedApprovedBonus(db, tenantId, companyId, a, 2026, 6, 1_000m);
        var offCycle = AddRun(db, tenantId, companyId, 2026, 6, PayrollRunTypes.OffCycle, includesRecurringPay: false);
        await db.SaveChangesAsync();

        await IncludeAsync(ctrl, offCycle.Id, a.Id);
        (await ctrl.Process(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.AttendancePayrollImpacts.AsNoTracking().FirstAsync(x => x.EmployeeId == a.Id)).Status
            .Should().NotBe("Processed",
                "the impacts belong to the regular run — starving it would silently gift the employee free absence");
    }

    // ── 8. M2: the cross-run double-pay control ────────────────────────────────

    [Fact]
    public async Task FullBasisOffCycleRun_AfterTheRegularRun_RaisesAlreadyPaidThisPeriodError()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        var regular = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await ctrl.Process(regular.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        // The MISSED-JOINER shape: an OffCycle run that DOES pay recurring salary. Legitimate for a new
        // joiner, catastrophic for someone the regular run already paid — which is why the control is a
        // cross-run Error, not the "supplemental runs skip recurring pay" convention.
        var offCycle = AddRun(db, tenantId, companyId, 2026, 6, PayrollRunTypes.OffCycle, includesRecurringPay: true);
        await db.SaveChangesAsync();
        await IncludeAsync(ctrl, offCycle.Id, a.Id);
        (await ctrl.Process(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var errors = await db.PayrollValidationResults.AsNoTracking()
            .Where(v => v.PayrollRunId == offCycle.Id && v.Severity == "Error").ToListAsync();
        errors.Should().Contain(v => v.Code == "ALREADY_PAID_THIS_PERIOD");

        // And it BLOCKS: an Error-severity result stops Approve.
        (await ctrl.Approve(offCycle.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task MissedJoinerOffCycleRun_PaysFullRecurringSalary_WhenNobodyElseDid()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, b) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        // Regular run pays only A (B was "missed").
        var regular = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await ctrl.UpsertRunSelection(regular.Id,
            new PayrollRunSelectionRequest("Exclude", "Joined after cut-off; pay off-cycle", new List<int> { b.Id }),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await ctrl.Process(regular.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == regular.Id)).Should().Be(1);

        // Off-cycle, FULL basis, for B alone — the case a type-derived basis made impossible.
        db.ChangeTracker.Clear();
        var joinerRun = AddRun(db, tenantId, companyId, 2026, 6, PayrollRunTypes.OffCycle, includesRecurringPay: true);
        await db.SaveChangesAsync();
        await IncludeAsync(ctrl, joinerRun.Id, b.Id);
        (await ctrl.Process(joinerRun.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == joinerRun.Id);
        slip.EmployeeId.Should().Be(b.Id);
        slip.BasicSalary.Should().Be(10_000m, "a missed joiner needs exactly the recurring components");
        slip.GrossSalary.Should().Be(13_000m);
        (await db.PayrollValidationResults.AsNoTracking()
            .AnyAsync(v => v.PayrollRunId == joinerRun.Id && v.Code == "ALREADY_PAID_THIS_PERIOD"))
            .Should().BeFalse("nobody paid B recurring salary this period");
    }

    // ── 9. Hold-outs are reported and must be acknowledged ─────────────────────

    [Fact]
    public async Task ExcludedEmployee_IsReported_AndBlocksApproveUntilAcknowledged()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, b) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await ctrl.UpsertRunSelection(run.Id,
            new PayrollRunSelectionRequest("Exclude", "Suspended pending investigation", new List<int> { b.Id }),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // 1. Persisted outcome.
        (await db.PayrollRunEmployeeSelections.AsNoTracking().SingleAsync(s => s.PayrollRunId == run.Id))
            .Outcome.Should().Be(PayrollRunSelectionOutcomes.Excluded);
        // 2. Warning-severity validation result (must NOT block).
        var warn = await db.PayrollValidationResults.AsNoTracking()
            .SingleAsync(v => v.PayrollRunId == run.Id && v.Code == "EMPLOYEE_EXCLUDED_FROM_RUN");
        warn.Severity.Should().Be("Warning");
        warn.Message.Should().Contain("Suspended pending investigation");
        // 3. Only A was paid.
        (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).Select(s => s.EmployeeId).ToListAsync())
            .Should().BeEquivalentTo(new[] { a.Id });

        // 4. Approve refuses until the count is acknowledged. A SECOND controller = a different user,
        //    because maker-checker forbids the processor from approving their own run — and that is
        //    exactly the point: the approver is not the person who set the exclusion.
        var approver = MakeCtrl(db, tenantId, "approver");
        var unack = await approver.Approve(run.Id, new PayrollDecisionRequest("looks fine"), CancellationToken.None);
        unack.Should().BeOfType<ConflictObjectResult>();
        unack.As<ConflictObjectResult>().Value!.ToString().Should().Contain("excluded_employees_not_acknowledged");

        (await approver.Approve(run.Id, new PayrollDecisionRequest("checked", ExpectedExcludedCount: 99), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("a wrong count is not an acknowledgement");

        (await approver.Approve(run.Id, new PayrollDecisionRequest("checked", ExpectedExcludedCount: 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    /// <summary>Validate re-derived the population selector-blind, so a held-out employee accumulated a
    /// blocking MISSING_SALARY_STRUCTURE Error and the run could NEVER be approved or locked.</summary>
    [Fact]
    public async Task Validate_HonoursTheSelector_SoAHeldOutEmployeeCannotBlockTheRun()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, b) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        // B has NO salary structure — Rule 1 would raise an Error for them.
        await db.EmployeeSalaryStructures.Where(s => s.EmployeeId == b.Id).ExecuteDeleteAsync();

        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await ctrl.UpsertRunSelection(run.Id,
            new PayrollRunSelectionRequest("Exclude", "No structure yet; will be paid off-cycle", new List<int> { b.Id }),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await ctrl.Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollValidationResults.AsNoTracking()
            .AnyAsync(v => v.PayrollRunId == run.Id && v.Code == "MISSING_SALARY_STRUCTURE" && v.EmployeeId == b.Id))
            .Should().BeFalse("an excluded employee must not raise a blocking error for the run that excluded them");

        (await MakeCtrl(db, tenantId, "approver").Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedExcludedCount: 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    // ── 10. M3: a bonus is consumed by exactly one run ─────────────────────────

    [Fact]
    public async Task Bonus_IsConsumedByExactlyOneRun_AndTheStampIsACompareAndSwap()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);
        var (_, bonusId) = await SeedApprovedBonus(db, tenantId, companyId, a, 2026, 6, 2_000m);

        var offCycle = AddRun(db, tenantId, companyId, 2026, 6, PayrollRunTypes.OffCycle, includesRecurringPay: false);
        await db.SaveChangesAsync();
        await IncludeAsync(ctrl, offCycle.Id, a.Id);
        (await ctrl.Process(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var bonus = await db.EmployeeBonuses.AsNoTracking().FirstAsync(x => x.Id == bonusId);
        bonus.Status.Should().Be("PaidInPayroll");
        bonus.PayrollRunId.Should().Be(offCycle.Id);

        // The regular run finds nothing left to consume — the read filters PayrollRunId == null.
        db.ChangeTracker.Clear();
        var regular = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await ctrl.Process(regular.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollEarnings.AsNoTracking().CountAsync(e => e.Source == "Bonus"))
            .Should().Be(1, "the bonus must be paid exactly once across the period's runs");
        (await db.EmployeeBonuses.AsNoTracking().FirstAsync(x => x.Id == bonusId)).PayrollRunId
            .Should().Be(offCycle.Id, "the stamp is a compare-and-swap; a later run cannot steal it");
    }

    // ── 11. M4: a supplemental run cannot go net-negative ──────────────────────

    [Fact]
    public async Task SupplementalRun_WithNegativeNet_Is422_AndWritesNothing()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        var parent = AddRun(db, tenantId, companyId, 2026, 6, status: "Locked");
        var correction = AddRun(db, tenantId, companyId, 2026, 7, PayrollRunTypes.Correction, includesRecurringPay: false);
        correction.ParentRunId = parent.Id;
        await db.SaveChangesAsync();

        // A clawback expressed as a negative adjustment, with no offsetting earning.
        db.PayrollAdjustments.Add(new PayrollAdjustment
        {
            TenantId = tenantId, PayrollRunId = correction.Id, EmployeeId = a.Id,
            AdjustmentType = "Overpayment recovery", Amount = -500m, Status = "Approved",
        });
        await db.SaveChangesAsync();

        await IncludeAsync(ctrl, correction.Id, a.Id);
        var res = await ctrl.Process(correction.Id, CancellationToken.None);
        res.Should().BeOfType<ObjectResult>();
        res.As<ObjectResult>().StatusCode.Should().Be(422);
        res.As<ObjectResult>().Value!.ToString().Should().Contain("negative_net_unsupported");

        db.ChangeTracker.Clear();
        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == correction.Id))
            .Should().BeFalse("the abort rolls the whole transaction back — an unlockable run is never created");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == correction.Id)).Status
            .Should().Be("Draft");
    }

    // ── 12. Regular runs are untouched by all of the above ────────────────────

    [Fact]
    public async Task RegularRun_WithNoSelectionAndNoSiblings_IsUnchangedByPodB2()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, b) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync();
        slips.Should().HaveCount(2);
        slips.Should().OnlyContain(s => s.BasicSalary == 10_000m && s.GrossSalary == 13_000m);
        // GOSI EE = 9.75% of (10,000 + 2,000) = 1,170 → net 11,830, exactly as before B2.
        slips.Should().OnlyContain(s => s.NetSalary == 11_830m);

        var reloaded = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
        reloaded.RunType.Should().Be(PayrollRunTypes.Regular);
        reloaded.IncludesRecurringPay.Should().BeTrue();
        reloaded.GlPostingPeriod.Should().BeNull();

        (await db.PayrollValidationResults.AsNoTracking()
            .AnyAsync(v => v.PayrollRunId == run.Id
                        && (v.Code == "EMPLOYEE_EXCLUDED_FROM_RUN"
                         || v.Code == "PERIOD_HAS_SIBLING_RUNS"
                         || v.Code == "SUPPLEMENTAL_STATUTORY_BASE"
                         || v.Code == "ALREADY_PAID_THIS_PERIOD")))
            .Should().BeFalse("none of B2's new rules fire on a plain monthly run");
    }

    // ── 13. M7: a correction can book into a later open GL period ─────────────

    [Fact]
    public async Task CreateRun_GlPostingPeriod_IsRejectedForRegular_ValidatedForCorrection()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        var parent = AddRun(db, tenantId, companyId, 2026, 6, status: "Locked");
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, GlPostingPeriod: "2026-07"), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("gl_posting_period_not_allowed");

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "Correction", parent.Id, GlPostingPeriod: "2026-05"), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("gl_posting_period_before_pay_period");

        // A CLOSED target period is refused.
        db.GlPeriodCloses.Add(new GlPeriodClose
        {
            TenantId = tenantId, CompanyId = companyId, Period = "2026-07", Status = GlPeriodStatuses.Closed,
        });
        await db.SaveChangesAsync();
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "Correction", parent.Id, GlPostingPeriod: "2026-07"), CancellationToken.None))
            .As<UnprocessableEntityObjectResult>().Value!.ToString().Should().Contain("gl_period_closed");

        // An OPEN later period is accepted; the run still REPORTS under its pay period.
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "Correction", parent.Id, GlPostingPeriod: "2026-08"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var corr = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.RunType == PayrollRunTypes.Correction);
        corr.Year.Should().Be(2026);
        corr.Month.Should().Be(6);
        corr.GlPostingPeriod.Should().Be("2026-08");
        PayrollController.GlAccrualPeriod(corr).Should().Be("2026-08");
    }

    // ── 14. Basis override is only available to OffCycle ──────────────────────

    [Fact]
    public async Task CreateRun_BasisOverride_IsOffCycleOnly()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, _, _) = await SeedCompanyAndEmployees(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "Supplementary", IncludesRecurringPay: true), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("basis_not_overridable");

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "Regular", IncludesRecurringPay: false), CancellationToken.None))
            .As<BadRequestObjectResult>().Value!.ToString().Should().Contain("basis_not_overridable");

        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, companyId, "OffCycle", IncludesRecurringPay: true), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.RunType == PayrollRunTypes.OffCycle))
            .IncludesRecurringPay.Should().BeTrue();

        // Default for OffCycle is supplemental.
        (await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 7, companyId, "OffCycle"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.RunType == PayrollRunTypes.OffCycle && r.Month == 7))
            .IncludesRecurringPay.Should().BeFalse();
    }
}

// ── Test doubles (file-scoped; mirror SettlementPeriodCloseTests) ───────────────

file static class _RtKsaRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
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

file sealed class _RtUnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _RtHttpAccessor : IHttpContextAccessor
{
    public _RtHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _RtNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _RtKsaPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_RtKsaRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _RtNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _RtNullDocStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
