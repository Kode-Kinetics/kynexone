using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-C3 — INDEPENDENT SDE-TEST-SME verification of mid-month proration + retro/arrears.
///
/// <para>Written against the SHIPPED ENDPOINTS ONLY (Process → GeneratePayslips → Approve → Lock →
/// pay → Void → Replacement), never against the calculator in isolation, and deliberately from angles
/// the implementation suite does not take:</para>
/// <list type="bullet">
///   <item><b>Hand-verified money, per component, not per sum.</b> The implementation suite asserts
///     <c>basic + housing + transport == 6,500</c>. That passes even if BASIC absorbed the whole
///     package and HOUSING went to zero. Every assertion here pins each component AND the net.</item>
///   <item><b>A control employee in every run.</b> Every proration test carries a second, unaffected
///     employee whose figures must be byte-identical to a pre-C3 month — the ~55-tenant bar, asserted
///     rather than asserted-about.</item>
///   <item><b>The A1 tie-out asserted in ABSOLUTE numbers</b> (expected == actual == GL == a number
///     computed by hand from the rates), not merely <c>delta == 0</c>. A delta of zero is also what two
///     identically-wrong sides produce.</item>
///   <item><b>The leaver tie-out</b>, which the implementation suite never exercises: it proves the
///     joiner and the arrears cases but not the third way the covered wage can move.</item>
///   <item><b>The whole tenant ledger</b> (Σ DR == Σ CR + no sign violations) after every phase.</item>
/// </list>
///
/// <para><b>THE FIXTURE'S HAND-COMPUTED CONSTANTS.</b> Saudi national, package 13,000 =
/// basic 10,000 + housing 2,000 + transport 1,000. The KSA covered wage is basic + housing = 12,000
/// (transport is NOT contributory), so at 9% annuity + 0.75% SANED the employee side is
/// 12,000 × 0.0975 = 1,170.00 and the employer side (+2% OH) is 12,000 × 0.1175 = 1,410.00. Every
/// figure below is derived from those four numbers and stated in the assertion.</para>
/// </summary>
public class PodC3IndependentSmeTests
{
    private static readonly Guid Maker   = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    // ── The fixture's arithmetic, named once ─────────────────────────────────────────────────────
    private const decimal Basic      = 10_000m;
    private const decimal Housing    =  2_000m;
    private const decimal Transport  =  1_000m;
    private const decimal Package    = Basic + Housing + Transport;   // 13,000
    private const decimal CoveredWage = Basic + Housing;              // 12,000 (transport is not contributory)
    private const decimal StatEe     = 1_170.00m;                     // 12,000 × 9.75%
    private const decimal StatEr     = 1_410.00m;                     // 12,000 × 11.75%

    private const string NetPayable = "2100 - Salaries Payable";
    private const string GosiEe     = "2101 - Social Insurance Payable (Employee)";
    private const string GosiEr     = "2106 - Social Insurance Employer Payable";
    private const string EmpOverpaid = "1420 - Employee Overpayment Receivable";
    private const string BasicExpense = "5001 - Basic Salary Expense";

    // ══ Harness ═══════════════════════════════════════════════════════════════════════════════════

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static DefaultHttpContext Ctx(Guid tenantId, Guid userId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userId == Maker ? "maker" : "checker"),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "payroll.read"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.export"),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        }, "test")),
    };

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var http = Ctx(tenantId, userId);
        return new PayrollController(
            db, new _C3iScope(), new _C3iHttp(http), new _C3iNotifications(),
            new _C3iPackResolver(), _C3iRules.Rules, new _C3iLetters(), new _C3iStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed record Fixture(Guid TenantId, Guid CompanyId, Employee Joiner, Employee Control, Guid StructureId);

    /// <summary>
    /// One KSA company, TWO Saudi employees on the identical 13,000 package. "Joiner" is the employee a
    /// test moves (joining date / offboarding / increment); "Control" never moves and its figures are the
    /// pre-C3 baseline every test re-asserts.
    /// </summary>
    private static async Task<Fixture> Seed(ZayraDbContext db, Guid tenantId, bool seedGlDrivers = false)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "C3 Independent KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "C3I", Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();

        var joiner = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "J001", FullName = "Layla Al-Otaibi",
            Status = "Active", JoiningDate = new DateTime(2022, 1, 1),
            WorkEmail = "layla@c3i.test", Nationality = "Saudi", ContractType = "Indefinite",
        };
        var control = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "K001", FullName = "Faisal Al-Harbi",
            Status = "Active", JoiningDate = new DateTime(2022, 1, 1),
            WorkEmail = "faisal@c3i.test", Nationality = "Saudi", ContractType = "Indefinite",
        };
        db.Employees.AddRange(joiner, control);
        await db.SaveChangesAsync();

        foreach (var e in new[] { joiner, control })
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = structure.Id,
                BasicSalary = Basic, HousingAllowance = Housing, TransportAllowance = Transport,
                EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
                CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = e.Id,
                Iban = "SA4420000001234567891234", MolId = $"MOL-{e.EmployeeCode}", SalaryCurrency = "SAR",
            });
        }
        if (seedGlDrivers)
            await GlDriverSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, company.Id, joiner, control, structure.Id);
    }

    private static PayrollRun AddRun(
        ZayraDbContext db, Fixture f, int year, int month,
        string status = "Draft", string runType = PayrollRunTypes.Regular, bool settlesArrears = true)
    {
        var run = new PayrollRun
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, Year = year, Month = month,
            Status = status, RunType = runType, IncludesRecurringPay = true,
            SettlesArrears = settlesArrears, CreatedByUserId = Maker,
        };
        db.PayrollRuns.Add(run);
        return run;
    }

    private static async Task<PayrollRun> AddRunAsync(
        ZayraDbContext db, Fixture f, int year, int month,
        string status = "Draft", string runType = PayrollRunTypes.Regular, bool settlesArrears = true)
    {
        var run = AddRun(db, f, year, month, status, runType, settlesArrears);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return run;
    }

    private static async Task ProcessAsync(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        (await Payroll(db, tenantId, Maker).Process(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    /// <summary>Process → payslips → Approve → Lock. The shipped order; Lock is what posts the journal
    /// and what refuses an unbalanced one (gl_unbalanced 422), so reaching Lock is itself an assertion.</summary>
    private static async Task ProcessApproveLock(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        await ProcessAsync(db, tenantId, runId);
        (await Payroll(db, tenantId, Maker).GeneratePayslips(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Checker).Approve(runId, new PayrollDecisionRequest("approved"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Checker).Lock(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    private static Task<List<FinanceGlEntry>> Ledger(ZayraDbContext db, Guid tenantId) =>
        db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync();

    /// <summary>Requirement (g): the trial balance. Σ debits == Σ credits over the WHOLE tenant ledger,
    /// plus no liability left in debit and no receivable left in credit.</summary>
    private static async Task AssertLedgerBalances(ZayraDbContext db, Guid tenantId, string because)
    {
        var rows = await Ledger(db, tenantId);
        rows.Should().NotBeEmpty("a locked run must have posted a journal");
        var dr = rows.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = rows.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        dr.Should().Be(cr, because);
        GlControlAccounts.FindSignViolations(rows).Should().BeEmpty(
            "a proration or arrears journal must never leave a liability in debit or a receivable in credit");
    }

    private static Task<PayrollSlip> Slip(ZayraDbContext db, Guid runId, int employeeId) =>
        db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == runId && s.EmployeeId == employeeId);

    private static Task<PayrollEarning> Earning(ZayraDbContext db, Guid runId, int employeeId, string code) =>
        db.PayrollEarnings.AsNoTracking().FirstAsync(e => e.PayrollRunId == runId && e.EmployeeId == employeeId && e.ComponentCode == code);

    private static Task<PayrollDeduction?> Deduction(ZayraDbContext db, Guid runId, int employeeId, string code) =>
        db.PayrollDeductions.AsNoTracking().FirstOrDefaultAsync(d => d.PayrollRunId == runId && d.EmployeeId == employeeId && d.ComponentCode == code)!;

    private static Task<bool> HasValidation(ZayraDbContext db, Guid runId, string code) =>
        db.PayrollValidationResults.AsNoTracking().AnyAsync(r => r.PayrollRunId == runId && r.Code == code);

    private static string Body(IActionResult r) => JsonSerializer.Serialize(r is ObjectResult o ? o.Value : null);

    /// <summary>Asserts the employee was paid the FULL, unprorated package — the pre-C3 baseline.</summary>
    private static void AssertFullMonth(PayrollSlip s, string who)
    {
        s.BasicSalary.Should().Be(Basic, $"{who} was employed for the whole period");
        s.HousingAllowance.Should().Be(Housing, who);
        s.TransportAllowance.Should().Be(Transport, who);
        s.GrossSalary.Should().Be(Package, who);
        s.NetSalary.Should().Be(Package - StatEe, who);
        s.ProrationFactor.Should().Be(1m, $"{who} must never be touched by proration arithmetic");
        s.ArrearsAmount.Should().Be(0m, who);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (a) A MID-PERIOD JOINER IS PAID THE PRORATED AMOUNT — AND THE FULL-MONTH COLLEAGUE IS UNTOUCHED
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// June 2026 is a 30-day month. Joining on the 16th ⇒ 16→30 inclusive = 15 days employed.
    /// Calendar30 ⇒ factor 15/30 = 0.5, and the package splits so the components still SUM to the
    /// prorated package to the halala:
    /// <code>
    ///   housing   = 2,000 × 0.5 =   1,000.00
    ///   transport = 1,000 × 0.5 =     500.00
    ///   basic     = 6,500 − 1,000 − 500 = 5,000.00   (basic absorbs the residual)
    ///   gross     = 13,000 × 0.5 =   6,500.00
    ///   statutory = 12,000 × 9.75% = 1,170.00        (FullMonth base — see (f))
    ///   NET       = 6,500 − 1,170 =  5,330.00
    /// </code>
    /// Every one of those is asserted, not just the total: a bug that put the whole 6,500 into BASIC
    /// would pass a sum-only assertion and mis-state three GL expense accounts.
    /// </summary>
    [Fact]
    public async Task MidPeriodJoiner_IsPaidFifteenThirtieths_ComponentByComponent_AndTheColleagueIsUnchanged()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, run.Id);

        var joiner = await Slip(db, run.Id, f.Joiner.Id);
        joiner.BasicSalary.Should().Be(5_000.00m, "10,000 basic + the residual of the 6,500 prorated package");
        joiner.HousingAllowance.Should().Be(1_000.00m, "2,000 × 15/30");
        joiner.TransportAllowance.Should().Be(500.00m, "1,000 × 15/30");
        joiner.GrossSalary.Should().Be(6_500.00m, "13,000 × 15/30, exactly");
        joiner.NetSalary.Should().Be(5_330.00m, "6,500 gross − 1,170 statutory");
        (joiner.BasicSalary + joiner.HousingAllowance + joiner.TransportAllowance)
            .Should().Be(joiner.GrossSalary, "no rounding dust may leak between the components and the package");

        // The witnesses that make the number explicable (requirement 7).
        joiner.PaidDays.Should().Be(15);
        joiner.ProrationDenominatorDays.Should().Be(30);
        joiner.PeriodDays.Should().Be(30);
        joiner.ProrationFactor.Should().Be(0.5m);
        joiner.ProrationBasis.Should().Be(ProrationBases.Calendar30);
        joiner.PaidFromDate.Should().Be(new DateOnly(2026, 6, 16));
        joiner.PaidToDate.Should().Be(new DateOnly(2026, 6, 30));
        joiner.FullBasicSalary.Should().Be(Basic, "the FULL package is the rate basis and the A1 statutory base");
        joiner.FullHousingAllowance.Should().Be(Housing);
        joiner.IsFinalWageMonth.Should().BeFalse("a joiner is not a leaver");

        // The emitted lines carry the same money, per component.
        (await Earning(db, run.Id, f.Joiner.Id, "BASIC")).Amount.Should().Be(5_000.00m);
        (await Earning(db, run.Id, f.Joiner.Id, "HOUSING")).Amount.Should().Be(1_000.00m);
        (await Earning(db, run.Id, f.Joiner.Id, "TRANSPORT")).Amount.Should().Be(500.00m);

        // ── THE ~55-TENANT BAR: the colleague who neither joined nor left is byte-identical ───────
        AssertFullMonth(await Slip(db, run.Id, f.Control.Id), "the control employee");
        (await Earning(db, run.Id, f.Control.Id, "BASIC")).ComponentName
            .Should().Be("Basic salary", "no narrative is appended when nothing was prorated");
    }

    /// <summary>
    /// THE PRE-FIX BEHAVIOUR, REPRODUCED ON DEMAND. <c>proration_basis = 'None'</c> is documented as the
    /// escape hatch that restores pre-C3 arithmetic exactly, so it is also the honest way to run the
    /// same fixture through the OLD behaviour: the joiner is paid a FULL month.
    ///
    /// <para>This test therefore PROVES that the 6,500 / 5,000 / 1,000 / 500 assertions in the test
    /// above would FAIL against pre-fix code — the same employee, the same run, 13,000 instead of
    /// 6,500 — and it pins the requirement that the escape hatch can never be silently forgotten
    /// (<c>WARN_PRORATION_DISABLED</c> on every run).</para>
    /// </summary>
    [Fact]
    public async Task PreFixBehaviour_IsReproducedByBasisNone_WhichPaysAJoinerAFullMonth_AndSaysSoEveryRun()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tid, CompanyId = f.CompanyId, RateKey = ProrationRateKeys.Basis,
            RateCategory = "PayParameter", RateValue = ProrationBases.None, DataType = "string",
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
        });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, run.Id);

        var joiner = await Slip(db, run.Id, f.Joiner.Id);
        joiner.GrossSalary.Should().Be(Package,
            "this IS the pre-C3 defect: an employee who joined on the 16th paid a full month. The " +
            "MidPeriodJoiner_… test above asserts 6,500 for the identical fixture, so it fails against " +
            "this behaviour — which is exactly what POD-C3 changed.");
        joiner.ProrationFactor.Should().Be(1m);
        (await HasValidation(db, run.Id, "WARN_PRORATION_DISABLED")).Should().BeTrue(
            "the choice to disable proration must be re-stated on every single run");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (b) A MID-PERIOD LEAVER IS PAID ONLY THROUGH THE LAST WORKING DAY
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// October 2026 is a 31-DAY month, chosen on purpose: under Calendar30 a leaver on the 15th is paid
    /// 15/30, NOT 15/31. The employee is still <c>Status = "Active"</c> here (the resignation has been
    /// keyed but the exit cascade has not run) — the ordinary shape of a notice period, and the one the
    /// implementation suite does not cover (it only exercises the already-Offboarded union path).
    /// </summary>
    [Fact]
    public async Task MidPeriodLeaver_IsPaidOnlyThroughTheLastWorkingDay_InAThirtyOneDayMonth()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, EmployeeCode = f.Joiner.EmployeeCode,
            EmployeeName = f.Joiner.FullName, NoticeDate = new DateOnly(2026, 9, 1),
            LastWorkingDay = new DateOnly(2026, 10, 15), Status = "InProgress",
        });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 10);

        await ProcessAsync(db, tid, run.Id);

        var leaver = await Slip(db, run.Id, f.Joiner.Id);
        leaver.PaidFromDate.Should().Be(new DateOnly(2026, 10, 1));
        leaver.PaidToDate.Should().Be(new DateOnly(2026, 10, 15), "nothing is owed after the last working day");
        leaver.PaidDays.Should().Be(15);
        leaver.ProrationDenominatorDays.Should().Be(30, "Calendar30 divides by 30 even in a 31-day month");
        leaver.PeriodDays.Should().Be(31);
        leaver.ProrationFactor.Should().Be(0.5m);
        leaver.BasicSalary.Should().Be(5_000.00m);
        leaver.HousingAllowance.Should().Be(1_000.00m);
        leaver.TransportAllowance.Should().Be(500.00m);
        leaver.GrossSalary.Should().Be(6_500.00m);
        leaver.NetSalary.Should().Be(5_330.00m);
        leaver.IsFinalWageMonth.Should().BeTrue(
            "the WAGE side of the final month is settled here — POD-C1 reads this flag as the handoff");

        AssertFullMonth(await Slip(db, run.Id, f.Control.Id), "the control employee");
    }

    /// <summary>
    /// BOUNDARY, LEAVER SIDE: a last working day ON the final day of the period is a FULL month's wage —
    /// but still the FINAL wage month. Getting either half of that wrong is a real defect: 30/30 paid as
    /// 29/30 short-pays every month-end leaver, and a missing IsFinalWageMonth flag would let the wage be
    /// drawn a second time next month.
    /// </summary>
    [Fact]
    public async Task LeaverOnTheLastDayOfThePeriod_IsPaidInFull_ButIsStillFlaggedAsTheFinalWageMonth()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, EmployeeCode = f.Joiner.EmployeeCode,
            EmployeeName = f.Joiner.FullName, NoticeDate = new DateOnly(2026, 9, 1),
            LastWorkingDay = new DateOnly(2026, 10, 31), Status = "InProgress",
        });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 10);

        await ProcessAsync(db, tid, run.Id);

        var leaver = await Slip(db, run.Id, f.Joiner.Id);
        leaver.GrossSalary.Should().Be(Package, "employed for the whole period ⇒ factor is exactly 1.0");
        leaver.ProrationFactor.Should().Be(1m);
        leaver.PaidToDate.Should().Be(new DateOnly(2026, 10, 31));
        leaver.IsFinalWageMonth.Should().BeTrue("the last working day still falls inside this period");
        (await Earning(db, run.Id, f.Joiner.Id, "BASIC")).ComponentName
            .Should().Be("Basic salary", "nothing was prorated, so no narrative is appended");
    }

    /// <summary>
    /// The STOP CONDITION, from the other side: once the final wage month is paid, the NEXT period must
    /// not pay it again. Asserted here on an employee whose status is flipped to Offboarded between the
    /// two runs — the real sequence (notice served, paid, then the exit cascade runs).
    /// </summary>
    [Fact]
    public async Task AfterTheFinalWageMonthIsPaid_TheNextPeriodPaysTheLeaverNothing()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, EmployeeCode = f.Joiner.EmployeeCode,
            EmployeeName = f.Joiner.FullName, NoticeDate = new DateOnly(2026, 9, 1),
            LastWorkingDay = new DateOnly(2026, 10, 15), Status = "InProgress",
        });
        await db.SaveChangesAsync();
        var october = await AddRunAsync(db, f, 2026, 10);
        await ProcessAsync(db, tid, october.Id);
        (await Slip(db, october.Id, f.Joiner.Id)).IsFinalWageMonth.Should().BeTrue();

        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).Status = EmployeeStatuses.Offboarded;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var november = await AddRunAsync(db, f, 2026, 11);
        await ProcessAsync(db, tid, november.Id);

        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == november.Id && s.EmployeeId == f.Joiner.Id))
            .Should().BeFalse("a settled leaver must never draw a second final wage");
        AssertFullMonth(await Slip(db, november.Id, f.Control.Id), "the control employee is unaffected");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (c) BOUNDARIES — THE FIRST DAY, THE LAST DAY, AND FEBRUARY
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Joining on the 1st is a WHOLE month. An off-by-one here would silently dock 1/30 of the
    /// wage of every employee who ever started on the first of a month — the single most common start
    /// date there is.</summary>
    [Fact]
    public async Task JoinerOnTheFirstDayOfThePeriod_IsPaidAFullMonth_WithNoProrationAtAll()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 1);
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, run.Id);

        var slip = await Slip(db, run.Id, f.Joiner.Id);
        AssertFullMonth(slip, "an employee who joined on the 1st");
        slip.PaidDays.Should().Be(30);
        slip.PaidFromDate.Should().Be(new DateOnly(2026, 6, 1));
        (await Earning(db, run.Id, f.Joiner.Id, "BASIC")).ComponentName.Should().Be("Basic salary");
    }

    /// <summary>
    /// Joining on the LAST day is ONE day of pay — not zero, and not a full month.
    /// <code>
    ///   factor    = round(1/30, 6) = 0.033333
    ///   housing   = round(2,000 × 0.033333, 2) =  66.67
    ///   transport = round(1,000 × 0.033333, 2) =  33.33
    ///   gross     = round(13,000 × 0.033333, 2) = 433.33
    ///   basic     = 433.33 − 66.67 − 33.33     = 333.33
    /// </code>
    /// </summary>
    [Fact]
    public async Task JoinerOnTheVeryLastDayOfThePeriod_IsPaidExactlyOneDay()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 30);
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, run.Id);

        var slip = await Slip(db, run.Id, f.Joiner.Id);
        slip.PaidDays.Should().Be(1, "one employed day is one day of pay, never an exclusion");
        slip.BasicSalary.Should().Be(333.33m);
        slip.HousingAllowance.Should().Be(66.67m);
        slip.TransportAllowance.Should().Be(33.33m);
        slip.GrossSalary.Should().Be(433.33m);
        (slip.BasicSalary + slip.HousingAllowance + slip.TransportAllowance).Should().Be(slip.GrossSalary);
        AssertFullMonth(await Slip(db, run.Id, f.Control.Id), "the control employee");
    }

    /// <summary>An employee who has not joined by the end of the period is EXCLUDED — no slip, no
    /// zero-value GL group — and the exclusion is REPORTED rather than silent.</summary>
    [Fact]
    public async Task JoinerAfterThePeriodEnds_IsExcludedEntirely_AndTheExclusionIsNamed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 7, 2);
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        var result = await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == run.Id && s.EmployeeId == f.Joiner.Id))
            .Should().BeFalse("a zero payslip is not a truthful representation of 'not employed'");
        (await db.PayrollRunEmployees.AsNoTracking().AnyAsync(x => x.PayrollRunId == run.Id && x.EmployeeId == f.Joiner.Id))
            .Should().BeFalse();

        // The exclusion is REPORTED, with the date that caused it — an unexplained absence from a run is
        // exactly how the notice-period unpay defect stayed hidden. (Process itself returns the run row,
        // so the operator-visible channel is the validation result, not the response body.)
        var exclusion = await db.PayrollValidationResults.AsNoTracking()
            .FirstOrDefaultAsync(r => r.PayrollRunId == run.Id
                                   && r.Code == "EMPLOYEE_SELECTION_NOT_ELIGIBLE"
                                   && r.EmployeeId == f.Joiner.Id);
        exclusion.Should().NotBeNull("someone who was eligible and is now unpaid must never vanish silently");
        exclusion!.Message.Should().Contain("2026-07-02", "the reason must name the date that caused it")
                          .And.Contain("Employment window is empty for 2026-06");

        AssertFullMonth(await Slip(db, run.Id, f.Control.Id), "the control employee");
    }

    /// <summary>
    /// FEBRUARY — the inertness case that would break every tenant at once. Under a /30 basis a
    /// 28-day month must still pay a full 30/30 to everyone employed throughout; only the employment
    /// window, never the calendar, may reduce a wage.
    /// </summary>
    [Theory]
    [InlineData(2026, 2)]   // 28-day February
    [InlineData(2024, 2)]   // leap February
    public async Task February_PaysEveryFullMonthEmployeeTheWholePackage_NotTwentyEightThirtieths(int year, int month)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var run = await AddRunAsync(db, f, year, month);

        await ProcessAsync(db, tid, run.Id);

        foreach (var e in new[] { f.Joiner, f.Control })
        {
            var slip = await Slip(db, run.Id, e.Id);
            AssertFullMonth(slip, $"{e.EmployeeCode} in a short month");
            slip.PaidDays.Should().Be(DateTime.DaysInMonth(year, month));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (d) PRORATION MUST NOT DOUBLE-REDUCE WITH LOP / UNPAID LEAVE
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DOUBLE-REDUCTION TEST. Two employees, the SAME two absent days (16 and 17 June). One joined
    /// on the 16th, one has been there for years.
    /// <code>
    ///   both:    LOP = 2 days × (FULL 10,000 ÷ 30) = 666.67   ← identical: an absent day costs the same
    ///   joiner:  gross 6,500 (15/30 of the package), NOT reduced a second time by the absence
    ///   joiner:  NET = 6,500 − 666.67 − 1,170 = 4,663.33
    ///   control: NET = 13,000 − 666.67 − 1,170 = 11,163.33
    /// </code>
    /// A double reduction shows up as either a LOP charged on the PRORATED basic (333.33 — under-charging
    /// the absence) or a gross below 6,500 (charging the unworked days twice). Both are pinned.
    /// </summary>
    [Fact]
    public async Task Absence_IsChargedOnceAtTheFullDayRate_AndProrationDoesNotReduceTheWageASecondTime()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        foreach (var empId in new[] { f.Joiner.Id, f.Control.Id })
            for (var d = 16; d <= 17; d++)
                db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
                {
                    TenantId = tid, EmployeeId = empId, WorkDate = new DateOnly(2026, 6, d),
                    ImpactType = "Absence", Minutes = 480, Status = "Pending",
                });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, run.Id);

        var joinerLop  = await Deduction(db, run.Id, f.Joiner.Id, "LOP_DEDUCTION");
        var controlLop = await Deduction(db, run.Id, f.Control.Id, "LOP_DEDUCTION");
        joinerLop!.Amount.Should().Be(666.67m, "2 × (FULL 10,000 ÷ 30) — the day rate is the monthly RATE, " +
            "never the prorated wage; 333.33 would be the double-reduction bug");
        controlLop!.Amount.Should().Be(joinerLop.Amount,
            "an absent day costs exactly the same whether or not you joined this month");

        var joiner = await Slip(db, run.Id, f.Joiner.Id);
        joiner.GrossSalary.Should().Be(6_500.00m, "proration alone decides the wage; the absence is a DEDUCTION");
        joiner.NetSalary.Should().Be(4_663.33m, "6,500 − 666.67 LOP − 1,170 statutory");

        var control = await Slip(db, run.Id, f.Control.Id);
        control.GrossSalary.Should().Be(Package);
        control.NetSalary.Should().Be(11_163.33m, "13,000 − 666.67 LOP − 1,170 statutory");
    }

    /// <summary>
    /// THE OTHER HALF OF THE SAME DEFECT: absent days recorded OUTSIDE the employment window. 20 absence
    /// days exist for June (1–20) but the employee only joined on the 16th, so at most 15 days can be
    /// charged. Uncapped, the LOP would be 20 × 333.33 = 6,666.67 against a 6,500 wage — a negative net
    /// on a Regular run, which is exactly the shape that makes a journal unbalanceable.
    /// <code>
    ///   capped LOP = 15 × (10,000 ÷ 30) = 5,000.00
    ///   NET        = 6,500 − 5,000 − 1,170 = 330.00
    /// </code>
    /// </summary>
    [Fact]
    public async Task AbsentDaysBeforeTheJoiningDate_CannotBeCharged_TheDeductionIsCappedAtDaysEmployed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        for (var d = 1; d <= 20; d++)
            db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = tid, EmployeeId = f.Joiner.Id, WorkDate = new DateOnly(2026, 6, d),
                ImpactType = "Absence", Minutes = 480, Status = "Pending",
            });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, run.Id);

        var lop = await Deduction(db, run.Id, f.Joiner.Id, "LOP_DEDUCTION");
        lop!.Amount.Should().Be(5_000.00m,
            "15 days employed is the ceiling on days that can be charged as absent; 6,666.67 would be " +
            "charging the employee for days before they were hired");
        lop.ComponentName.Should().Contain("15.00 d", "the payslip must say how many days were charged");

        var slip = await Slip(db, run.Id, f.Joiner.Id);
        slip.NetSalary.Should().Be(330.00m, "6,500 − 5,000 LOP − 1,170 statutory");
        slip.NetSalary.Should().BeGreaterThanOrEqualTo(0m);
    }

    /// <summary>
    /// UNPAID LEAVE spanning the joining date. The approved impact was snapshotted at the FULL monthly
    /// rate over the whole 11-day request (10–20 June = 3,300.00). Only 16–20 June — 5 of those 11 days —
    /// fall inside the employment window, so the charge is scaled to 5/11 and the reduction is NAMED.
    /// <code>
    ///   scale     = round(5/11, 6) = 0.454545
    ///   deduction = round(3,300 × 0.454545, 2) = 1,500.00
    /// </code>
    /// The control employee, whose window is the whole month, is charged the full 3,300.00 — proving the
    /// rescale is reached ONLY through the proration path.
    /// </summary>
    [Fact]
    public async Task UnpaidLeaveSpanningTheJoiningDate_IsScaledToTheEmploymentWindow_AndTheColleagueIsUnscaled()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        foreach (var e in new[] { f.Joiner, f.Control })
        {
            var req = new LeaveRequest
            {
                TenantId = tid, CompanyId = f.CompanyId, EmployeeId = e.Id, EmployeeName = e.FullName,
                LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Unpaid",
                StartDate = new DateOnly(2026, 6, 10), EndDate = new DateOnly(2026, 6, 20),
                TotalDays = 11m, Status = "Approved", PayrollImpact = "Unpaid",
            };
            db.LeaveRequests.Add(req);
            db.LeavePayrollImpacts.Add(new LeavePayrollImpact
            {
                TenantId = tid, LeaveRequestId = req.Id, EmployeeId = e.Id,
                PayPeriod = "2026-06", ImpactType = "Deduction", Days = 11m, Amount = 3_300.00m,
                Status = "Pending",
            });
        }
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, run.Id);

        (await Deduction(db, run.Id, f.Joiner.Id, "LEAVE"))!.Amount.Should().Be(1_500.00m,
            "5 of the 11 requested days fall inside the employment window — the employee cannot be charged " +
            "unpaid leave for days before they were hired");
        (await Deduction(db, run.Id, f.Control.Id, "LEAVE"))!.Amount.Should().Be(3_300.00m,
            "the rescale is reachable ONLY through the proration path; a full-month employee is untouched");
        (await HasValidation(db, run.Id, "WARN_IMPACT_OUTSIDE_EMPLOYMENT_WINDOW")).Should().BeTrue(
            "reducing an approved deduction is a decision that must be stated, never silent");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (e) RETRO / BACKDATED-INCREMENT ARREARS
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Runs April and May at the ORIGINAL package, locks them, then keys a backdated increment
    /// (basic 10,000 → 12,000) effective 1 April. Returns the June run, unprocessed.</summary>
    private static async Task<PayrollRun> SeedBackdatedIncrementAsync(ZayraDbContext db, Fixture f)
    {
        foreach (var m in new[] { 4, 5 })
        {
            var r = await AddRunAsync(db, f, 2026, m);
            await ProcessAsync(db, f.TenantId, r.Id);
            (await db.PayrollRuns.FirstAsync(x => x.Id == r.Id)).Status = "Locked";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = f.TenantId, EmployeeId = f.Joiner.Id, SalaryStructureId = f.StructureId,
            BasicSalary = 12_000m, HousingAllowance = Housing, TransportAllowance = Transport,
            EffectiveDate = new DateOnly(2026, 4, 1), IsActive = true,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await AddRunAsync(db, f, 2026, 6);
    }

    /// <summary>
    /// THE CORE RETRO CASE. Arrears = Σ over the intervening LOCKED periods of (new entitlement − what
    /// was actually paid), itemised per covered period, each line carrying the arithmetic that produced
    /// it and the assignment that caused it.
    /// <code>
    ///   April: entitled 12,000 − paid 10,000 − settled 0 = 2,000
    ///   May:   entitled 12,000 − paid 10,000 − settled 0 = 2,000
    ///   June's OWN wage is already at the new rate: 12,000 + 2,000 + 1,000 = 15,000
    ///   slip gross = 15,000 + 4,000 arrears = 19,000
    /// </code>
    /// The periods are asserted EXACTLY: March (no run) and June (the current period, paid directly)
    /// must produce no line at all.
    /// </summary>
    [Fact]
    public async Task BackdatedIncrement_SettlesExactlyTheInterveningPeriods_ItemisedAndFullyAuditable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await SeedBackdatedIncrementAsync(db, f);

        await ProcessAsync(db, tid, june.Id);

        var lines = await db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.PayrollRunId == june.Id).OrderBy(a => a.CoveredMonth).ToListAsync();
        lines.Should().HaveCount(2, "one line per covered period — April and May");
        lines.Select(l => (l.CoveredYear, l.CoveredMonth)).Should().Equal((2026, 4), (2026, 5));
        lines.Should().OnlyContain(l => l.EmployeeId == f.Joiner.Id,
            "the control employee's entitlement never changed, so it produces no arrears at all");
        foreach (var l in lines)
        {
            l.ComponentCode.Should().Be(PayrollArrearsComponents.Basic);
            l.EntitledAmount.Should().Be(12_000m, "the assignment now effective for that period");
            l.PaidAmount.Should().Be(10_000m, "what the period's non-voided runs actually paid on BASIC");
            l.PreviouslySettledAmount.Should().Be(0m);
            l.Amount.Should().Be(2_000m, "entitled − paid − previously settled");
            l.ProrationFactor.Should().Be(1m, "neither April nor May was a partial month for this employee");
            l.SourceEffectiveDate.Should().Be(new DateOnly(2026, 4, 1), "the audit trail back to the decision");
            l.IsGosiBearing.Should().BeTrue("BASIC is contributory under the default PeriodPaid treatment");
            l.Status.Should().Be(PayrollArrearsStatuses.Settled);
        }
        lines.Sum(l => l.Amount).Should().Be(4_000m, "2,000 × 2 months");

        (await db.PayrollArrearsLines.AsNoTracking().AnyAsync(a => a.CoveredMonth == 3))
            .Should().BeFalse("March has no finalised run — an unpaid month is not an underpaid one");
        (await db.PayrollArrearsLines.AsNoTracking().AnyAsync(a => a.CoveredMonth == 6))
            .Should().BeFalse("June's own wage is paid at the new rate directly, never as arrears");

        var slip = await Slip(db, june.Id, f.Joiner.Id);
        slip.ArrearsAmount.Should().Be(4_000m);
        slip.BasicSalary.Should().Be(12_000m, "June is paid on the new assignment");
        slip.GrossSalary.Should().Be(19_000m, "15,000 current package + 4,000 arrears");

        // Itemised on the payslip, per covered period, with the money on the line.
        var arrearsEarnings = await db.PayrollEarnings.AsNoTracking()
            .Where(e => e.PayrollRunId == june.Id && e.EmployeeId == f.Joiner.Id && e.Source == "Arrears")
            .OrderBy(e => e.ComponentName).ToListAsync();
        arrearsEarnings.Should().HaveCount(2);
        arrearsEarnings.Should().OnlyContain(e => e.Amount == 2_000m);
        arrearsEarnings.Select(e => e.ComponentName).Should()
            .Contain("Arrears — Basic salary (2026-04)").And.Contain("Arrears — Basic salary (2026-05)");

        AssertFullMonth(await Slip(db, june.Id, f.Control.Id), "the control employee");
    }

    /// <summary>
    /// ANTI-DOUBLE-PAY, BOTH DOORS. (1) Re-processing the SAME run must REBUILD, not duplicate — the
    /// idempotent wipe has to happen BEFORE the entitlement arithmetic, or the run reads its own previous
    /// output as "already settled" and self-cancels to zero. (2) A LATER run must settle nothing, because
    /// the "previously settled" term absorbs it: the formula is self-correcting, not merely guarded.
    /// </summary>
    [Fact]
    public async Task Arrears_AreNeverPaidTwice_NotByReProcessing_AndNotByASubsequentRun()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessAsync(db, tid, june.Id);

        // (1) Re-process the same run.
        (await db.PayrollRuns.FirstAsync(r => r.Id == june.Id)).Status = "Draft";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ProcessAsync(db, tid, june.Id);

        var afterReprocess = await db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.PayrollRunId == june.Id).ToListAsync();
        afterReprocess.Should().HaveCount(2, "the wipe rebuilds; it must not accumulate");
        afterReprocess.Sum(a => a.Amount).Should().Be(4_000m,
            "and it must not self-cancel to zero either — the wipe happens BEFORE the arithmetic");
        (await Slip(db, june.Id, f.Joiner.Id)).ArrearsAmount.Should().Be(4_000m);

        // (2) A later run.
        (await db.PayrollRuns.FirstAsync(r => r.Id == june.Id)).Status = "Locked";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var july = await AddRunAsync(db, f, 2026, 7);
        await ProcessAsync(db, tid, july.Id);

        (await db.PayrollArrearsLines.AsNoTracking().Where(a => a.PayrollRunId == july.Id).ToListAsync())
            .Should().BeEmpty("April and May were settled in June; the third term absorbs them exactly");
        (await Slip(db, july.Id, f.Joiner.Id)).ArrearsAmount.Should().Be(0m);
    }

    /// <summary>
    /// REQUIREMENT 4, LITERALLY: "it must never silently re-pay a whole month". A period that was LOCKED
    /// but in which this employee was paid NOTHING (a POD-B2 hold-out) is a MISSED month, not an
    /// underpaid one — settling it as arrears would pay a whole month's wage while bypassing validation,
    /// statutory, WPS and approval.
    /// </summary>
    [Fact]
    public async Task APeriodInWhichTheEmployeeWasHeldOut_IsNeverSettledAsAWholeMonthOfArrears()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);

        // May: a real, locked run that deliberately EXCLUDED the joiner (B2 hold-out).
        var may = await AddRunAsync(db, f, 2026, 5);
        (await Payroll(db, tid, Maker).UpsertRunSelection(may.Id,
            new PayrollRunSelectionRequest("Exclude", "Held out pending contract review", new List<int> { f.Joiner.Id }),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        await ProcessAsync(db, tid, may.Id);
        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == may.Id && s.EmployeeId == f.Joiner.Id))
            .Should().BeFalse("the hold-out was not paid in May");
        (await db.PayrollRuns.FirstAsync(r => r.Id == may.Id)).Status = "Locked";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var june = await AddRunAsync(db, f, 2026, 6);
        await ProcessAsync(db, tid, june.Id);

        (await db.PayrollArrearsLines.AsNoTracking().AnyAsync(a => a.PayrollRunId == june.Id))
            .Should().BeFalse("paying a never-paid month as 'arrears' would bypass the entire run lifecycle — " +
                              "that is an OffCycle run's job, and the seam is enforced here");
        (await Slip(db, june.Id, f.Joiner.Id)).ArrearsAmount.Should().Be(0m);
    }

    /// <summary>
    /// A retro DECREASE has no vehicle in an additive-only run. It must be COMPUTED (so the exposure is
    /// known), PERSISTED with its covered period (so nothing is lost), and the run REFUSED — never netted
    /// into pay as a negative earning, and never silently dropped.
    /// </summary>
    [Fact]
    public async Task ABackdatedDecrease_IsComputedAndPersisted_AndTheRunIsRefusedRatherThanClawingBack()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var may = await AddRunAsync(db, f, 2026, 5);
        await ProcessAsync(db, tid, may.Id);
        (await db.PayrollRuns.FirstAsync(r => r.Id == may.Id)).Status = "Locked";
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, SalaryStructureId = f.StructureId,
            BasicSalary = 8_000m, HousingAllowance = Housing, TransportAllowance = Transport,
            EffectiveDate = new DateOnly(2026, 5, 1), IsActive = true,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var june = await AddRunAsync(db, f, 2026, 6);

        var result = await Payroll(db, tid, Maker).Process(june.Id, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(result).Should().Contain("retro_decrease_unsupported");
        db.ChangeTracker.Clear();
        var pending = await db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.Status == PayrollArrearsStatuses.PendingRecovery).ToListAsync();
        pending.Should().NotBeEmpty("the exposure and its covered period survive the refusal");
        pending.Should().OnlyContain(a => a.Amount < 0m && a.PayrollRunId == null);
        pending.Sum(a => a.Amount).Should().Be(-2_000m, "May was over-paid by exactly 10,000 − 8,000");
        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == june.Id))
            .Should().BeFalse("the run is refused before anything is written — no half-processed month");
    }

    /// <summary>
    /// ARREARS SURVIVE A POD-B3 RECOVERY. The June run that settled 4,000 of arrears is VOIDED, and a
    /// REPLACEMENT run is processed for the same month. The arrears must become due AGAIN (the void
    /// un-paid them) and be settled exactly once more — never twice, never zero.
    /// </summary>
    [Fact]
    public async Task ArrearsSurviveAVoid_AndTheReplacementRunReproducesThemExactlyOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessApproveLock(db, tid, june.Id);

        var beforeVoid = await Slip(db, june.Id, f.Joiner.Id);
        beforeVoid.ArrearsAmount.Should().Be(4_000m);
        beforeVoid.GrossSalary.Should().Be(19_000m);
        await AssertLedgerBalances(db, tid, "the arrears-bearing month must post a balanced journal");

        (await Payroll(db, tid, Checker).VoidRun(
            june.Id, new PayrollDecisionRequest("June was processed on the wrong increment date"),
            CancellationToken.None,
            settlementDisposition: PayrollVoidDispositions.FundsRecalled, settlementReference: "RECALL-C3",
            remittanceDisposition: PayrollVoidDispositions.FundsRecalled, remittanceReference: "REFUND-C3"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollArrearsLines.AsNoTracking().Where(a => a.PayrollRunId == june.Id).ToListAsync())
            .Should().OnlyContain(a => a.Status == PayrollArrearsStatuses.Voided,
                "a voided run did not pay them — they must stop counting as settled");

        var created = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Replacement, june.Id),
            CancellationToken.None);
        created.Should().BeOfType<CreatedResult>();
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();
        await ProcessApproveLock(db, tid, replacement.Id);

        var replacementLines = await db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.PayrollRunId == replacement.Id).ToListAsync();
        replacementLines.Should().HaveCount(2);
        replacementLines.Sum(a => a.Amount).Should().Be(4_000m,
            "the replacement reproduces the same prorated/retro figures — the recovery is a re-run, not a top-up");
        replacementLines.Should().OnlyContain(a => a.PreviouslySettledAmount == 0m,
            "the voided lines must not be counted as 'already settled', or the replacement would pay nothing");

        (await Slip(db, replacement.Id, f.Joiner.Id)).GrossSalary.Should().Be(19_000m);
        (await db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.Status == PayrollArrearsStatuses.Settled).ToListAsync())
            .Sum(a => a.Amount)
            .Should().Be(4_000m, "across the voided month AND its replacement, the arrears are owed exactly once");
        await AssertLedgerBalances(db, tid, "the whole void → replacement cycle must leave the ledger balanced");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (f) *** THE CROSS-POD BAR: POD-A1's GOSI RECONCILIATION STILL TIES OUT ***
    //
    //     expected == actual == GL (2101 EE / 2106 ER), asserted in ABSOLUTE hand-computed numbers —
    //     because a delta of zero is also what two identically-wrong sides produce.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    private static async Task AssertTiesOut(
        ZayraDbContext db, Guid tenantId, Guid runId, decimal expectedEe, decimal expectedEr, string because)
    {
        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        var recon = await new GosiReconciliationService(db, new _C3iPackResolver())
            .ReconcileAsync(tenantId, run, CancellationToken.None);

        recon.HasStatutoryData.Should().BeTrue();
        recon.PackResolved.Should().BeTrue();
        recon.GlPosted.Should().BeTrue("the run is locked, so the liability must be on the ledger");

        recon.ActualEmployeeTotal.Should().Be(expectedEe, $"ACTUAL (what was deducted) — {because}");
        recon.ExpectedEmployeeTotal.Should().Be(expectedEe, $"EXPECTED (the A1 reconstruction) — {because}");
        recon.GlEmployeeLiability.Should().Be(expectedEe, $"GL 2101 — {because}");
        recon.ActualEmployerTotal.Should().Be(expectedEr, $"ACTUAL employer — {because}");
        recon.ExpectedEmployerTotal.Should().Be(expectedEr, $"EXPECTED employer — {because}");
        recon.GlEmployerLiability.Should().Be(expectedEr, $"GL 2106 — {because}");

        recon.ExpectedVsActualEmployeeDelta.Should().Be(0m);
        recon.ExpectedVsActualEmployerDelta.Should().Be(0m);
        recon.GlEmployeeDelta.Should().Be(0m);
        recon.GlEmployerDelta.Should().Be(0m);
        recon.VarianceCount.Should().Be(0, "not one employee may report a phantom variance on a filing source");
        recon.GlEmployeeAccount.Should().StartWith("2101");
        recon.GlEmployerAccount.Should().StartWith("2106");
        recon.SlipEmployeeStatutoryTotal.Should().Be(expectedEe, "the slip headers are the 4th witness");
        recon.SlipEmployerStatutoryTotal.Should().Be(expectedEr);
    }

    /// <summary>
    /// A PRORATED JOINER, under the shipped KSA default <c>proration_gosi_base = FullMonth</c>. The wage
    /// halves; the CONTRIBUTORY wage does not. That is only reconstructible because the slip persists the
    /// full package and the policy it used — so this is the test that fails the moment the run and the A1
    /// reconstruction drift apart.
    /// <code>
    ///   joiner  covered wage 12,000 (FULL month) → EE 1,170.00  ER 1,410.00
    ///   control covered wage 12,000             → EE 1,170.00  ER 1,410.00
    ///   run total                                  EE 2,340.00  ER 2,820.00
    /// </code>
    /// </summary>
    [Fact]
    public async Task A1TieOut_HoldsForAProratedJoiner_OnTheFullMonthContributoryBase()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessApproveLock(db, tid, run.Id);

        var joiner = await Slip(db, run.Id, f.Joiner.Id);
        joiner.GrossSalary.Should().Be(6_500m, "the WAGE is prorated …");
        joiner.EmployeeStatutoryTotal.Should().Be(StatEe, "… but the CONTRIBUTORY wage is not, under FullMonth");
        joiner.GosiBasePolicy.Should().Be(ProrationGosiBases.FullMonth);

        await AssertTiesOut(db, tid, run.Id, 2 * StatEe, 2 * StatEr, "a prorated joiner + a full-month colleague");

        var recon = await new GosiReconciliationService(db, new _C3iPackResolver())
            .ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id), CancellationToken.None);
        recon.ProratedEmployeeCount.Should().Be(1, "the reconciliation must NAME the proration, not hide it");
        recon.Rows.First(r => r.EmployeeId == f.Joiner.Id).CoveredWageBase.Should().Be(CoveredWage,
            "the joiner's covered wage is the FULL registered monthly wage — not the 6,500 actually paid");
        recon.ProrationScopeNote.Should().NotBeNullOrWhiteSpace(
            "a covered wage that is not simply 'the slip's basic + housing' must explain itself");

        await AssertLedgerBalances(db, tid, "a prorated month must post a balanced journal");
    }

    /// <summary>
    /// A MID-PERIOD LEAVER. The third way the covered wage can move, and the one the implementation
    /// suite never exercises — it proves the joiner and the arrears cases only. Same tie-out bar.
    /// </summary>
    [Fact]
    public async Task A1TieOut_HoldsForAMidPeriodLeaver()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, EmployeeCode = f.Joiner.EmployeeCode,
            EmployeeName = f.Joiner.FullName, NoticeDate = new DateOnly(2026, 9, 1),
            LastWorkingDay = new DateOnly(2026, 10, 15), Status = "InProgress",
        });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 10);

        await ProcessApproveLock(db, tid, run.Id);

        var leaver = await Slip(db, run.Id, f.Joiner.Id);
        leaver.GrossSalary.Should().Be(6_500m);
        leaver.IsFinalWageMonth.Should().BeTrue();
        leaver.EmployeeStatutoryTotal.Should().Be(StatEe);

        await AssertTiesOut(db, tid, run.Id, 2 * StatEe, 2 * StatEr, "a mid-period leaver + a full-month colleague");
        await AssertLedgerBalances(db, tid, "a leaver's final wage month must post a balanced journal");
    }

    /// <summary>
    /// AN ARREARS PAYMENT. The retro amount is a real addition to the contributory wage that is NOT
    /// recoverable from the slip's money columns (it is folded into OtherAllowances alongside overtime
    /// and bonuses), so the reconstruction must re-derive it from the run-stamped sub-ledger.
    /// <code>
    ///   joiner  covered = June 12,000 + 2,000 housing + 4,000 arrears = 18,000
    ///                     → EE 18,000 × 9.75%  = 1,755.00
    ///                     → ER 18,000 × 11.75% = 2,115.00
    ///   control covered = 12,000 → EE 1,170.00  ER 1,410.00
    ///   run total                  EE 2,925.00  ER 3,525.00
    /// </code>
    /// </summary>
    [Fact]
    public async Task A1TieOut_HoldsForAnArrearsPayment_AndTheArrearsSliceOfTheBaseIsNamed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await SeedBackdatedIncrementAsync(db, f);

        await ProcessApproveLock(db, tid, june.Id);

        const decimal arrearsEe = 18_000m * 0.0975m;   // 1,755.00
        const decimal arrearsEr = 18_000m * 0.1175m;   // 2,115.00
        await AssertTiesOut(db, tid, june.Id, arrearsEe + StatEe, arrearsEr + StatEr,
            "a run settling 4,000 of GOSI-bearing arrears");

        var recon = await new GosiReconciliationService(db, new _C3iPackResolver())
            .ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == june.Id), CancellationToken.None);
        recon.ArrearsGosiBase.Should().Be(4_000m, "the arrears slice of the contributory base must be NAMED, " +
            "not left to be inferred from an unexplained delta");
        recon.Rows.First(r => r.EmployeeId == f.Joiner.Id).CoveredWageBase.Should().Be(18_000m);
        (await HasValidation(db, june.Id, "WARN_ARREARS_GOSI_TREATMENT_REQUIRES_SIGNOFF")).Should().BeTrue(
            "[FLAG-COMPLIANCE-KSA] filing retro contributions in the period PAID needs a compliance officer");

        await AssertLedgerBalances(db, tid, "an arrears-bearing month must post a balanced journal");
    }

    /// <summary>
    /// THE STATUTORY CEILING, WITH ARREARS ON TOP — requirement 5's explicit "same care for the 45,000
    /// ceiling". The run folds arrears into the SAME housing slot the bonus uses so the cap is applied
    /// once; the A1 reconstruction must fold them into the same slot or the two sides cap different
    /// totals and every high earner reports a phantom variance.
    /// <code>
    ///   April & May paid at basic 40,000 + housing 8,000 → covered 48,000, capped at 45,000
    ///   backdated increment to basic 44,000 ⇒ arrears 4,000 × 2 months = 8,000
    ///   June covered = 44,000 + 8,000 + 8,000 arrears = 60,000 → capped at 45,000
    ///     → EE 45,000 × 9.75%  = 4,387.50   ER 45,000 × 11.75% = 5,287.50
    /// </code>
    /// And the [FLAG-COMPLIANCE-KSA] number that makes ceiling absorption AUDITABLE rather than merely
    /// flagged: on the EARNED basis each covered month had its own headroom, and this employee was
    /// already at the cap in both — so the earned-basis delta is 0.00 and the 8,000 of arrears attracts
    /// no contribution at all, in either reading.
    /// </summary>
    [Fact]
    public async Task A1TieOut_HoldsWhenArrearsPushTheCoveredWageThroughTheStatutoryCeiling()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, SalaryStructureId = f.StructureId,
            BasicSalary = 40_000m, HousingAllowance = 8_000m, TransportAllowance = Transport,
            EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        foreach (var m in new[] { 4, 5 })
        {
            var r = await AddRunAsync(db, f, 2026, m);
            await ProcessAsync(db, tid, r.Id);
            (await db.PayrollRuns.FirstAsync(x => x.Id == r.Id)).Status = "Locked";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, SalaryStructureId = f.StructureId,
            BasicSalary = 44_000m, HousingAllowance = 8_000m, TransportAllowance = Transport,
            EffectiveDate = new DateOnly(2026, 4, 1), IsActive = true,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var june = await AddRunAsync(db, f, 2026, 6);

        await ProcessApproveLock(db, tid, june.Id);

        var lines = await db.PayrollArrearsLines.AsNoTracking().Where(a => a.PayrollRunId == june.Id).ToListAsync();
        lines.Sum(a => a.Amount).Should().Be(8_000m, "4,000 × 2 months");
        lines.Sum(a => a.EarnedBasisGosiDelta).Should().Be(0m,
            "[FLAG-COMPLIANCE-KSA] this employee was already at the 45,000 cap in BOTH covered months, so " +
            "even an amended, earned-basis declaration would add nothing — the number, not just a flag");

        const decimal cappedEe = 45_000m * 0.0975m;   // 4,387.50
        const decimal cappedEr = 45_000m * 0.1175m;   // 5,287.50
        await AssertTiesOut(db, tid, june.Id, cappedEe + StatEe, cappedEr + StatEr,
            "arrears ride INSIDE the single 45,000 cap, on both sides of the reconciliation");

        var recon = await new GosiReconciliationService(db, new _C3iPackResolver())
            .ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == june.Id), CancellationToken.None);
        recon.ArrearsGosiBase.Should().Be(8_000m, "the arrears slice is still NAMED even when the cap absorbs it");
        recon.ArrearsGosiBaseEarnedBasis.Should().Be(0m);
        var row = recon.Rows.First(r => r.EmployeeId == f.Joiner.Id);
        row.CoveredWageBase.Should().Be(60_000m,
            "the RAW pre-ceiling base (44,000 basic + 8,000 housing + 8,000 arrears) — proving the arrears " +
            "really did enter the reconstruction's base rather than being quietly dropped once it exceeded the cap");
        row.ExpectedEmployee.Should().Be(cappedEe, "…and the CAP is then applied once, inside the pack, on both sides");
        await AssertLedgerBalances(db, tid, "a capped, arrears-bearing month must still balance");
    }

    /// <summary>
    /// PRORATION × ARREARS, THE INTERACTION. The employee joined mid-APRIL, so April's entitlement under
    /// the backdated increment must be computed on APRIL'S OWN factor — not on 1.0, and not on June's.
    /// <code>
    ///   April (joined the 16th): paid on the OLD package  → BASIC paid 5,000
    ///           entitlement now effective for April = (12,000+2,000+1,000) × 15/30 = 7,500
    ///           split: housing 1,000, transport 500, BASIC 6,000
    ///           arrears(April) = 6,000 − 5,000 = 1,000       ← NOT 12,000 − 5,000 = 7,000
    ///   May   (whole month):    12,000 − 10,000 = 2,000
    ///   total = 3,000
    /// </code>
    /// Ignoring the covered period's own window would pay this employee 7 times too much for April.
    /// </summary>
    [Fact]
    public async Task Arrears_ForAMonthTheEmployeeOnlyPartlyWorked_UseThatMonthsOwnProrationFactor()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 4, 16);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        foreach (var m in new[] { 4, 5 })
        {
            var r = await AddRunAsync(db, f, 2026, m);
            await ProcessAsync(db, tid, r.Id);
            (await db.PayrollRuns.FirstAsync(x => x.Id == r.Id)).Status = "Locked";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        (await Slip(db, (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Month == 4)).Id, f.Joiner.Id))
            .BasicSalary.Should().Be(5_000m, "April was itself a prorated joining month");

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = f.Joiner.Id, SalaryStructureId = f.StructureId,
            BasicSalary = 12_000m, HousingAllowance = Housing, TransportAllowance = Transport,
            EffectiveDate = new DateOnly(2026, 4, 1), IsActive = true,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var june = await AddRunAsync(db, f, 2026, 6);

        await ProcessAsync(db, tid, june.Id);

        var lines = await db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.PayrollRunId == june.Id).OrderBy(a => a.CoveredMonth).ToListAsync();
        lines.Should().HaveCount(2);
        var april = lines[0];
        april.CoveredMonth.Should().Be(4);
        april.ProrationFactor.Should().Be(0.5m, "April's OWN employment window, not June's and not 1.0");
        april.EntitledAmount.Should().Be(6_000m, "the new package prorated by APRIL's factor, basic-absorbed");
        april.PaidAmount.Should().Be(5_000m);
        april.Amount.Should().Be(1_000m,
            "settling 12,000 − 5,000 = 7,000 here would pay a joiner seven times what they are owed");
        lines[1].CoveredMonth.Should().Be(5);
        lines[1].Amount.Should().Be(2_000m, "May was a whole month");
        lines.Sum(a => a.Amount).Should().Be(3_000m);
        (await Slip(db, june.Id, f.Joiner.Id)).ArrearsAmount.Should().Be(3_000m);
    }

    /// <summary>
    /// THE OPT-IN POLICY, tied out independently: with <c>proration_gosi_base = 'Prorated'</c> the
    /// contributory wage follows the wage actually paid. Recomputed here from first principles rather
    /// than asserted as a delta.
    /// <code>
    ///   joiner  covered = prorated basic 5,000 + prorated housing 1,000 = 6,000
    ///                     → EE 585.00   ER 705.00
    ///   control covered = 12,000 → EE 1,170.00  ER 1,410.00
    /// </code>
    /// </summary>
    [Fact]
    public async Task A1TieOut_HoldsForAProratedJoiner_UnderTheOptInProratedContributoryBase()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tid, CompanyId = f.CompanyId, RateKey = ProrationRateKeys.GosiBase,
            RateCategory = "PayParameter", RateValue = ProrationGosiBases.Prorated, DataType = "string",
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
        });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessApproveLock(db, tid, run.Id);

        const decimal proratedEe = 6_000m * 0.0975m;   // 585.00
        const decimal proratedEr = 6_000m * 0.1175m;   // 705.00
        (await Slip(db, run.Id, f.Joiner.Id)).EmployeeStatutoryTotal.Should().Be(proratedEe);
        await AssertTiesOut(db, tid, run.Id, proratedEe + StatEe, proratedEr + StatEr,
            "the contributory wage follows the wage actually paid");
        await AssertLedgerBalances(db, tid, "the Prorated contributory policy must still balance");
    }

    /// <summary>
    /// THE PERIOD TIE-OUT — what a GOSI filing is actually built from — across a period that mixes a
    /// prorated joiner with a mid-month leaver in the SAME month. Per-run reconciliation can be
    /// meaningful while the PERIOD aggregate silently double-counts or drops a slip.
    /// </summary>
    [Fact]
    public async Task A1PeriodTieOut_HoldsForAMonthContainingBothAJoinerAndALeaver()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 6, 16);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tid, EmployeeId = f.Control.Id, EmployeeCode = f.Control.EmployeeCode,
            EmployeeName = f.Control.FullName, NoticeDate = new DateOnly(2026, 5, 1),
            LastWorkingDay = new DateOnly(2026, 6, 20), Status = "InProgress",
        });
        await db.SaveChangesAsync();
        var run = await AddRunAsync(db, f, 2026, 6);

        await ProcessApproveLock(db, tid, run.Id);

        var joiner = await Slip(db, run.Id, f.Joiner.Id);
        var leaver = await Slip(db, run.Id, f.Control.Id);
        joiner.PaidDays.Should().Be(15);
        leaver.PaidDays.Should().Be(20, "1 June → 20 June inclusive");
        leaver.GrossSalary.Should().Be(Math.Round(Package * Math.Round(20m / 30m, 6), 2),
            "13,000 × 20/30 — the leaver's own window, not the joiner's");

        var period = await new GosiReconciliationService(db, new _C3iPackResolver())
            .ReconcilePeriodAsync(tid, f.CompanyId, 2026, 6, CancellationToken.None);
        period.ActualEmployeeTotal.Should().Be(2 * StatEe, "two FullMonth contributory wages of 12,000");
        period.ExpectedEmployeeTotal.Should().Be(2 * StatEe);
        period.ActualEmployerTotal.Should().Be(2 * StatEr);
        period.ExpectedEmployerTotal.Should().Be(2 * StatEr);
        period.ExpectedVsActualEmployeeDelta.Should().Be(0m);
        period.ExpectedVsActualEmployerDelta.Should().Be(0m);
        period.GlEmployeeDelta.Should().Be(0m);
        period.GlEmployerDelta.Should().Be(0m);
        period.VarianceCount.Should().Be(0);

        await AssertLedgerBalances(db, tid, "a month with a joiner AND a leaver must still balance");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (g) THE GL JOURNAL STAYS BALANCED — INCLUDING THE POD-B3 RECEIVABLE HANDOFF
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE POD-B3 HANDOFF, END TO END AND THROUGH THE REAL VOID. A month is locked, PAID (the cash
    /// leaves the bank) and then voided with <c>FundsDisbursed</c> — so POD-B3 recognises a
    /// <c>DR 1420 Employee Overpayment Receivable</c> that, before C3, could never be netted into
    /// anything because FinanceGlEntry has no employee dimension. The replacement run must net it,
    /// CREDIT the 1420 asset (never a payable), and leave 1420 flat.
    /// </summary>
    [Fact]
    public async Task VoidedDisbursedCash_BecomesAPerEmployeeReceivable_AndTheReplacementRunNetsItTo1420()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await AddRunAsync(db, f, 2026, 6);
        await ProcessApproveLock(db, tid, june.Id);

        // Pay it: the cash really leaves the bank.
        var ctrl = Payroll(db, tid, Checker);
        (await ctrl.CreatePaymentBatch(june.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        db.ChangeTracker.Clear();
        var batch = await db.PayrollPaymentBatches.FirstAsync(b => b.PayrollRunId == june.Id);
        batch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).SettlePaymentBatch(
            batch.Id, new SettlePaymentBatchRequest("BANKREF-C3", null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Void it, stating that the salaries were NOT recalled.
        (await Payroll(db, tid, Checker).VoidRun(
            june.Id, new PayrollDecisionRequest("wrong month processed"), CancellationToken.None,
            settlementDisposition: PayrollVoidDispositions.FundsDisbursed, settlementReference: null,
            remittanceDisposition: PayrollVoidDispositions.FundsRecalled, remittanceReference: "REFUND-C3"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var receivables = await db.PayrollEmployeeReceivables.AsNoTracking().Where(r => r.TenantId == tid).ToListAsync();
        receivables.Should().HaveCount(2, "one row per employee actually paid — the attribution the 1420 debit lacks");
        receivables.Should().OnlyContain(r => r.Status == PayrollReceivableStatuses.Outstanding && r.EmployeeId != 0);
        var ledgerAfterVoid = await Ledger(db, tid);
        receivables.Sum(r => r.Amount).Should().Be(GlControlAccounts.Balance(ledgerAfterVoid, EmpOverpaid),
            "Σ(sub-ledger) must equal the aggregate 1420 debit, to the halala");
        await AssertLedgerBalances(db, tid, "the void must leave the ledger balanced");

        // The reason the month was wrong: June was run on a stale (too low) package. The corrected
        // package is what makes this a REALISTIC recovery — a replacement whose net is smaller than the
        // cash already disbursed would leave the employee on zero net, which the shipped
        // ZERO_NET_WITH_GROSS rule correctly BLOCKS at Approve (verified: it does).
        foreach (var e in new[] { f.Joiner, f.Control })
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tid, EmployeeId = e.Id, SalaryStructureId = f.StructureId,
                BasicSalary = 16_000m, HousingAllowance = Housing, TransportAllowance = Transport,
                EffectiveDate = new DateOnly(2026, 6, 1), IsActive = true,
                CreatedAtUtc = new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The replacement run, explicitly opting in to recover it.
        var created = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Replacement, june.Id,
                NetsPriorReceivable: true),
            CancellationToken.None);
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();
        await ProcessApproveLock(db, tid, replacement.Id);

        var recovery = await Deduction(db, replacement.Id, f.Joiner.Id, PayrollRecoveryComponents.ReceivableRecovery);
        recovery.Should().NotBeNull("the receivable must be netted, not left to age forever");
        recovery!.Source.Should().Be(PayrollRecoveryComponents.RecoverySource);
        recovery.Amount.Should().Be(Package - StatEe, "the whole net that was already disbursed: 13,000 − 1,170");
        (await Slip(db, replacement.Id, f.Joiner.Id)).NetSalary.Should().Be(5_415.00m,
            "corrected 19,000 gross − 1,755 statutory − 11,830 already disbursed; recovery never drives net negative");

        (await db.PayrollEmployeeReceivables.AsNoTracking().Where(r => r.TenantId == tid).ToListAsync())
            .Should().OnlyContain(r => r.Status == PayrollReceivableStatuses.Recovered
                                    && r.RecoveredByRunId == replacement.Id);
        var finalLedger = await Ledger(db, tid);
        GlControlAccounts.Balance(finalLedger, EmpOverpaid).Should().Be(0m,
            "the receivable the void recognised is fully relieved — POD-B3's ageing balance, closed");
        await AssertLedgerBalances(db, tid, "the recovery must credit an ASSET, not create a phantom liability");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (h) THE PAYSLIP EXPLAINS ITSELF
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Requirement 7, on the EMPLOYEE-FACING artefact. <c>PayslipComponent</c> rows are what ESS reads
    /// (EmployeeSelfServiceController.DownloadPayslip projects them verbatim into the PDF's line items,
    /// EmployeeSelfServiceController.cs:270-277), so a narrative that reaches them reaches the employee.
    ///
    /// <para>Asserted here: the days-paid/basis narrative rides the BASIC line, and each arrears amount
    /// is its OWN line naming the month it covers — never one opaque "arrears" figure.</para>
    /// </summary>
    [Fact]
    public async Task ThePayslipShowsDaysPaidAndBasis_AndItemisesArrearsPerCoveredPeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Joiner.Id)).JoiningDate = new DateTime(2026, 3, 16);
        await db.SaveChangesAsync();
        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessAsync(db, tid, june.Id);
        (await Payroll(db, tid, Maker).GeneratePayslips(june.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payslip = await db.Payslips.AsNoTracking().FirstAsync(p => p.PayrollRunId == june.Id && p.EmployeeId == f.Joiner.Id);
        var components = await db.PayslipComponents.AsNoTracking().Where(c => c.PayslipId == payslip.Id).ToListAsync();
        components.Where(c => c.ComponentType == "Earning").Select(c => c.ComponentName).Should()
            .Contain("Arrears — Basic salary (2026-04)").And.Contain("Arrears — Basic salary (2026-05)",
                "an employee must be able to see WHICH months a retro payment covers");
        components.Where(c => c.ComponentName.StartsWith("Arrears")).Sum(c => c.Amount).Should().Be(4_000m);

        // And the days/basis narrative, on a month that IS prorated (March — the joining month).
        var march = await AddRunAsync(db, f, 2026, 3);
        await ProcessAsync(db, tid, march.Id);
        (await Payroll(db, tid, Maker).GeneratePayslips(march.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var marchPayslip = await db.Payslips.AsNoTracking().FirstAsync(p => p.PayrollRunId == march.Id && p.EmployeeId == f.Joiner.Id);
        var marchNames = await db.PayslipComponents.AsNoTracking()
            .Where(c => c.PayslipId == marchPayslip.Id).Select(c => c.ComponentName).ToListAsync();
        marchNames.Should().Contain(n => n.Contains("16/30 days")
                                      && n.Contains("30-day month")
                                      && n.Contains("joined 2026-03-16"),
            "days paid, the NAMED basis, and why — all three, on the line the employee reads");
    }

    /// <summary>
    /// THE AUDITOR'S VIEW. <c>GET runs/{id}/arrears</c> must show the arithmetic behind every retro
    /// amount — entitled, paid, previously settled, the covered period, and the assignment that caused
    /// it — not just the total.
    /// </summary>
    [Fact]
    public async Task TheArrearsEndpoint_ShowsTheArithmeticBehindEveryRetroAmount()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessAsync(db, tid, june.Id);

        var result = await Payroll(db, tid, Checker).GetRunArrears(june.Id, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var body = Body(result);

        body.Should().Contain("\"coveredPeriod\":\"2026-04\"").And.Contain("\"coveredPeriod\":\"2026-05\"");
        body.Should().Contain("\"EntitledAmount\":12000").And.Contain("\"PaidAmount\":10000");
        body.Should().Contain("\"PreviouslySettledAmount\":0");
        body.Should().Contain("\"totalSettled\":4000");
        body.Should().Contain("\"totalGosiBearing\":4000");
        body.Should().Contain("SourceEffectiveDate", "the audit trail from a number back to the decision");
        body.Should().Contain("FLAG-COMPLIANCE-KSA",
            "the ceiling-absorption caveat must travel with the number, not live in a design document");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // GL EXPENSE ROUTING OF ARREARS — the one place the shipped behaviour and its own documentation
    // disagree. See the assertion's `because` for the exact claim being tested.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// POD-C3 states, at PayrollController.cs:5845-5854 and again in the arrears emission block, that
    /// "arrears are the SAME expense as the component they settle … Routing them to EARN:OTHER (5099)
    /// would move a retro basic-salary increase out of Basic Salary Expense and quietly distort every
    /// payroll cost report."
    ///
    /// <para>That is true only for a tenant with NO persisted GL drivers. Every provisioned tenant HAS
    /// them (<see cref="GlDriverSeeder"/> runs at tenant creation), and BuildPayrollGlEntries prefers a
    /// persisted driver over the compiled <c>EarningDriverKey</c> switch. <c>EARN:BASIC</c> matches
    /// <c>Exact "BASIC"</c> and so does NOT match <c>ARREARS_BASIC</c>; the catch-all <c>EARN:OTHER</c>
    /// (MatchMode "Any") does. The POD-C3 deduction side handled exactly this problem by adding an
    /// <c>Exact</c> seed row for <c>DED:RECEIVABLE_RECOVERY</c>; the four <c>ARREARS_*</c> earning codes
    /// got no such row.</para>
    ///
    /// <para>This test posts a real arrears journal on a driver-seeded tenant and pins where the debit
    /// actually lands.</para>
    /// </summary>
    [Fact]
    public async Task ArrearsExpense_LandsInTheComponentsOwnAccount_OnADriverSeededTenant()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, seedGlDrivers: true);
        var june = await SeedBackdatedIncrementAsync(db, f);

        await ProcessApproveLock(db, tid, june.Id);

        // Asserted FIRST so the record shows the journal is sound: this is a routing defect, not a
        // balance one, and the two must not be confused when triaging the failure below.
        await AssertLedgerBalances(db, tid, "the journal balances whichever expense account the arrears land in");

        var arrearsLines = (await Ledger(db, tid))
            .Where(l => l.SourceEntityId == june.Id && l.Description.Contains("ARREARS_BASIC"))
            .ToList();
        arrearsLines.Should().ContainSingle("the 4,000 of retro BASIC posts as one debit group");
        arrearsLines[0].Amount.Should().Be(4_000m);
        arrearsLines[0].DebitAccount.Should().Be(BasicExpense,
            "POD-C3's own stated contract: 'arrears are the SAME expense as the component they settle … " +
            "routing them to EARN:OTHER (5099) would move a retro basic-salary increase out of Basic Salary " +
            "Expense and quietly distort every payroll cost report' (PayrollController.cs:5845-5854). " +
            "With gl_drivers seeded — i.e. on every provisioned tenant — ResolveDriverForComponent is " +
            "consulted BEFORE the compiled EarningDriverKey switch, EARN:BASIC matches Exact 'BASIC' and " +
            "therefore not 'ARREARS_BASIC', and the Any catch-all EARN:OTHER wins. The deduction side was " +
            "given an Exact seed row (DED:RECEIVABLE_RECOVERY); the four ARREARS_* earning codes were not. " +
            "FIX: add four Exact seed rows (ARREARS_BASIC → EARN:BASIC, ARREARS_HOUSING → EARN:HOUSING, " +
            "ARREARS_TRANSPORT → EARN:TRANSPORT, ARREARS_OTHER_ALLOWANCES → EARN:OTHER_ALLOWANCES) to " +
            "PayrollGlCatalog.SystemDriverSeeds plus a NOT-EXISTS-guarded backfill for existing tenants, " +
            "or give the ARREARS_* codes a Prefix-match driver.");
    }
}

// ── File-scoped service stubs ────────────────────────────────────────────────────────────────────

file static class _C3iRules
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

file sealed class _C3iScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(
        ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _C3iHttp : IHttpContextAccessor
{
    public _C3iHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _C3iNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct)
        => Task.CompletedTask;
}

file sealed class _C3iPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_C3iRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _C3iLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _C3iStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
