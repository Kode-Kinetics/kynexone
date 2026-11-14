using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The monthly ANNUAL-LEAVE provision booked at payroll Lock: catch-up, remeasurement deltas, the company switch,
/// idempotency, the closed-period guard, void, and the settlement that consumes it. Mirrors
/// <see cref="EosbProvisionAccrualTests"/>.
///
/// <para>Worked numbers (Gross basis, package / 30): A holds 10.5 unused days at 300 a day, B none at 250, C 21 at 200 -
/// see <see cref="Employees"/>. June books 3,150 + 4,200 = 7,350; July moves only the difference.</para>
/// </summary>
public class LeaveProvisionAccrualTests
{
    private const string Provision = "2330 - Accrued Annual Leave Provision";
    private const string Expense = "5115 - Annual Leave Provision Expense";
    private const string EncashmentExpense = "5111 - Leave Encashment Expense";

    /// <summary>(code, basic, housing, transport, unused days at June month end). Day rate on the Gross basis = package / 30.</summary>
    private static readonly (string Code, decimal Basic, decimal Housing, decimal Transport, decimal Days)[] Employees =
    {
        ("A", 6_000m, 2_000m, 1_000m, 10.5m),   // 9,000 -> 300/day  x 10.5 = 3,150
        ("B", 5_000m, 1_500m, 1_000m, 0m),      // 7,500 -> 250/day  x 0    = 0
        ("C", 4_000m, 1_000m, 1_000m, 21m),     // 6,000 -> 200/day  x 21   = 4,200
    };

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static ClaimsPrincipal User(Guid tenantId, Guid userId, string name, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, name), new(ClaimTypes.Role, "Admin"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid? userId = null)
    {
        var http = new DefaultHttpContext
        {
            User = User(tenantId, userId ?? Guid.NewGuid(), "payroll-user",
                "payroll.read", "payroll.write", "payroll.approve", "payroll.lock", "payroll.export"),
        };
        var ctrl = new PayrollController(
            db, new _LpScope(), new _LpHttp(http), new F2NullNotifications(),
            new _LpKsaResolver(_LpRules.Rules), _LpRules.Rules, new F2NullLetters(), new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    /// <summary>A Saudi IBAN with a correct ISO 13616 checksum, unique per employee id.</summary>
    private static string ValidIban(int n)
    {
        var bban = "20" + n.ToString("D18");
        var check = 98 - (int)(System.Numerics.BigInteger.Parse(bban + "281000") % 97);
        return $"SA{check:D2}{bban}";
    }

    private sealed record Fixture(Guid TenantId, Company Company, Dictionary<string, Employee> Emp, Guid AnnualTypeId);

    private static async Task<Fixture> SeedAsync(ZayraDbContext db, bool provisioningOn = true, Guid? tenantIdOverride = null,
        string companyName = "Leave Provision KSA Co", Fixture? sameTenant = null)
    {
        var tenantId = sameTenant?.TenantId ?? tenantIdOverride ?? Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = companyName, RegistrationNumber = $"CR-{Guid.NewGuid():N}"[..12],
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        if (sameTenant is null)
            db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = "SAU", EosbEnabled = true });
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = $"STR-{Guid.NewGuid():N}"[..10], Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2023, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        var annual = new LeaveType
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "ANNUAL", NameEn = "Annual leave", NameAr = "إجازة سنوية",
            Category = "Annual", IsPaid = true, IsActive = true,
        };
        db.LeaveTypes.Add(annual);
        await db.SaveChangesAsync();

        var emps = new Dictionary<string, Employee>();
        foreach (var (code, basic, housing, transport, days) in Employees)
        {
            var emp = new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"{code}-{Guid.NewGuid():N}"[..8], FullName = $"Employee {code}",
                Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = $"{code.ToLowerInvariant()}{Guid.NewGuid():N}@test.com"[..20] + ".com",
                Nationality = "SAU", ContractType = "Indefinite",
            };
            db.Employees.Add(emp);
            await db.SaveChangesAsync();
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
                BasicSalary = basic, HousingAllowance = housing, TransportAllowance = transport,
                Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = emp.Id, Iban = ValidIban(emp.Id), MolId = $"MOL-{code}", SalaryCurrency = "SAR",
            });
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName, LeaveTypeId = annual.Id,
                LeaveTypeName = annual.NameEn, Year = 2026, Accrued = days,
            });
            emps[code] = emp;
        }
        if (provisioningOn) db.CompanyRatePolicies.Add(Switch(tenantId, company.Id, LeaveProvisioningRateKeys.BalanceMonthly, new DateOnly(2026, 6, 1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, company, emps, annual.Id);
    }

    private static CompanyRatePolicy Switch(Guid tenantId, Guid companyId, string value, DateOnly from) => new()
    {
        TenantId = tenantId, CompanyId = companyId, RateKey = LeaveProvisioningRateKeys.Mode, RateCategory = "PayParameter",
        RateValue = value, DataType = "string", Unit = "enum", EffectiveFrom = from, Status = CompanyPolicyStatuses.Active,
    };

    /// <summary>Moves an employee's balance row: leave taken (Used) and leave accrued (Accrued).</summary>
    private static async Task SetBalanceAsync(ZayraDbContext db, Fixture fx, string code, decimal accrued, decimal used)
    {
        var row = await db.EmployeeLeaveBalances.SingleAsync(b => b.TenantId == fx.TenantId && b.EmployeeId == fx.Emp[code].Id);
        row.Accrued = accrued; row.Used = used;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> LockMonthAsync(ZayraDbContext db, Fixture fx, int year, int month)
    {
        var run = new PayrollRun
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, Year = year, Month = month,
            Status = "Draft", RunType = PayrollRunTypes.Regular,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var processed = await Payroll(db, fx.TenantId).Process(run.Id, CancellationToken.None);
        if (processed is ObjectResult { StatusCode: >= 400 } bad)
            Assert.Fail($"Process refused: {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
        db.ChangeTracker.Clear();
        var stored = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        stored.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var locked = await Payroll(db, fx.TenantId).Lock(run.Id, CancellationToken.None);
        if (locked is not OkObjectResult)
            Assert.Fail($"Lock refused: {JsonSerializer.Serialize((locked as ObjectResult)?.Value)}");
        db.ChangeTracker.Clear();
        return run.Id;
    }

    private static Task<List<FinanceGlEntry>> LeaveLinesAsync(ZayraDbContext db, Guid runId) =>
        db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceEntityId == runId && x.EventType == GlEventTypes.LeaveProvisionAccrual)
            .ToListAsync();

    /// <summary>CR − DR on 2330 over every row for a company: the provision the GL carries.</summary>
    private static async Task<decimal> ProvisionBalanceAsync(ZayraDbContext db, Guid tenantId, Guid? companyId = null)
    {
        var rows = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (companyId == null || x.CompanyId == companyId)
                     && (x.DebitAccount == Provision || x.CreditAccount == Provision))
            .ToListAsync();
        return rows.Sum(r => r.CreditAccount == Provision ? r.Amount : -r.Amount);
    }

    private static async Task<decimal> NetDebitAsync(ZayraDbContext db, Guid tenantId, string account)
    {
        var rows = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.DebitAccount == account || x.CreditAccount == account))
            .ToListAsync();
        return rows.Sum(r => r.DebitAccount == account ? r.Amount : -r.Amount);
    }

    // ── 1. First month: the catch-up, on the worked numbers ─────────────────────

    [Fact]
    public async Task FirstMonth_BooksTheWholeUnusedLeaveValue_AsACatchUp_PerEmployeeCredits_OneExpenseLine()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);

        var jun = await LockMonthAsync(db, fx, 2026, 6);

        var lines = await LeaveLinesAsync(db, jun);
        var credits = lines.Where(l => l.CreditAccount == Provision).ToList().OrderBy(l => l.Amount).ToList();
        credits.Select(l => l.Amount).Should().Equal(3_150m, 4_200m);
        credits.Select(l => l.SourceEntityRef).Should().BeEquivalentTo(new[] { fx.Emp["A"].Id.ToString(), fx.Emp["C"].Id.ToString() });
        var expense = lines.Single(l => l.DebitAccount == Expense);
        expense.Amount.Should().Be(7_350m);
        expense.Description.Should().Be(LeaveProvisionPlanner.CatchUpLabel);
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        lines.Select(l => l.CompanyId).Distinct().Should().Equal(new Guid?[] { fx.Company.Id });
        lines.Should().OnlyContain(l => l.Currency == "SAR" && l.Period == "2026-06");

        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(7_350m);
        var positions = await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, fx.Emp.Values.Select(e => e.Id).ToList(), default);
        positions.Select(p => (p.EmployeeId, p.Remaining)).Should().BeEquivalentTo(new[] { (fx.Emp["A"].Id, 3_150m), (fx.Emp["C"].Id, 4_200m) });

        var snapshots = (await db.LeaveProvisionSnapshots.AsNoTracking().ToListAsync()).OrderBy(s => s.Obligation).ToList();
        snapshots.Should().HaveCount(3);
        snapshots.Should().OnlyContain(s => s.IsCatchUp && s.Basis == "Gross" && s.Period == "2026-06" && s.PayrollRunId == jun && s.MeasuredAt == new DateOnly(2026, 6, 30));
        var a = snapshots.Single(s => s.EmployeeId == fx.Emp["A"].Id);
        (a.Days, a.DailyRate, a.Obligation, a.Movement).Should().Be((10.5m, 300m, 3_150m, 3_150m));
    }

    // ── 2. Second month: only the change posts ──────────────────────────────────

    [Fact]
    public async Task SecondMonth_AfterLeaveTakenAndAccrual_PostsOnlyTheDelta()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        // July: A took 3 days and accrued 1.75; B and C accrued 1.75.
        await SetBalanceAsync(db, fx, "A", accrued: 12.25m, used: 3m);
        await SetBalanceAsync(db, fx, "B", accrued: 1.75m, used: 0m);
        await SetBalanceAsync(db, fx, "C", accrued: 22.75m, used: 0m);

        var jul = await LockMonthAsync(db, fx, 2026, 7);

        var lines = await LeaveLinesAsync(db, jul);
        lines.Single(l => l.DebitAccount == Provision).Amount.Should().Be(375m, "A: 9.25 days x 300 = 2,775 against 3,150 carried");
        lines.Where(l => l.CreditAccount == Provision).Select(l => l.Amount).Should().BeEquivalentTo(new[] { 437.50m, 350m });
        var up = lines.Single(l => l.DebitAccount == Expense);
        (up.Amount, up.Description).Should().Be((787.50m, LeaveProvisionPlanner.MonthlyLabel));
        var down = lines.Single(l => l.CreditAccount == Expense);
        (down.Amount, down.Description).Should().Be((375m, LeaveProvisionPlanner.ReleaseLabel));
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));

        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(7_762.50m, "2,775 + 437.50 + 4,550");
        (await NetDebitAsync(db, fx.TenantId, Expense)).Should().Be(7_762.50m, "the expense account carries exactly what the provision carries");
        var snapshots = await db.LeaveProvisionSnapshots.AsNoTracking().Where(s => s.Period == "2026-07").ToListAsync();
        snapshots.Should().OnlyContain(s => !s.IsCatchUp);
        snapshots.Sum(s => s.Obligation).Should().Be(7_762.50m);
        snapshots.Sum(s => s.CarriedBefore).Should().Be(7_350m);
    }

    // ── 2b. A carried-over balance is provided once ─────────────────────────────

    [Fact]
    public async Task ACarriedOverBalance_WithLastYearsRowStillOpen_IsProvidedOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        // C: last year's row still shows 15 (21 granted, 6 used) and this year's row carries those 15 forward plus 5 accrued.
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = fx.TenantId, EmployeeId = fx.Emp["C"].Id, EmployeeName = "Employee C", LeaveTypeId = fx.AnnualTypeId,
            LeaveTypeName = "Annual leave", Year = 2025, Accrued = 21m, Used = 6m,
        });
        var current = await db.EmployeeLeaveBalances.SingleAsync(b => b.TenantId == fx.TenantId && b.EmployeeId == fx.Emp["C"].Id && b.Year == 2026);
        current.Accrued = 5m; current.CarriedForward = 15m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await LockMonthAsync(db, fx, 2026, 6);

        var c = await db.LeaveProvisionSnapshots.AsNoTracking().SingleAsync(s => s.EmployeeId == fx.Emp["C"].Id);
        (c.Days, c.Obligation).Should().Be((20m, 4_000m), "the 15 carried days are counted in this year's row only: 15 + 5 at 200 a day, not 35 days");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(3_150m + 4_000m);
    }

    // ── 3. Company switch ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProvisioningOff_LockPostsNoLeaveLines_AndWritesNoSnapshot()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db, provisioningOn: false);

        var run = await LockMonthAsync(db, fx, 2026, 6);

        (await LeaveLinesAsync(db, run)).Should().BeEmpty("the switch defaults to Off");
        (await db.LeaveProvisionSnapshots.CountAsync()).Should().Be(0);
        (await db.FinanceGlEntries.AnyAsync(x => x.DebitAccount == Expense || x.CreditAccount == Provision)).Should().BeFalse();
    }

    [Fact]
    public async Task SwitchedOffInALaterMonth_StopsProvisioning_TheCarriedBalanceStays()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        db.CompanyRatePolicies.Add(Switch(fx.TenantId, fx.Company.Id, LeaveProvisioningRateKeys.Off, new DateOnly(2026, 7, 1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await SetBalanceAsync(db, fx, "C", accrued: 22.75m, used: 0m);

        var jul = await LockMonthAsync(db, fx, 2026, 7);

        (await LeaveLinesAsync(db, jul)).Should().BeEmpty();
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(7_350m);
    }

    // ── 4. THE SAME day rate and days as final settlement ───────────────────────

    [Theory]
    [InlineData("Gross", 3_150.00)]          // 9,000 / 30 = 300 x 10.5
    [InlineData("Basic", 2_100.00)]          // 6,000 / 30 = 200 x 10.5
    [InlineData("BasicHousing", 2_800.00)]   // 8,000 / 30 = 266.666667 x 10.5 = 2,800.00
    public async Task Provision_EqualsWhatFinalSettlementWouldEncash_OnEveryBasis(string basis, double expected)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        if (basis != "Gross")
        {
            db.CompanyPayrollRules.Add(new CompanyPayrollRule
            {
                TenantId = fx.TenantId, CompanyId = fx.Company.Id, RuleKey = PayrollRuleKeys.LeaveCashOutBasis, RuleValue = basis,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        await LockMonthAsync(db, fx, 2026, 6);
        var snapshot = await db.LeaveProvisionSnapshots.AsNoTracking().SingleAsync(s => s.EmployeeId == fx.Emp["A"].Id);
        snapshot.Basis.Should().Be(basis);
        snapshot.Obligation.Should().Be((decimal)expected);

        // The leaver's settlement, computed for the same day by the real endpoint.
        var emp = fx.Emp["A"];
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = fx.TenantId, EmployeeId = emp.Id, EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
            SeparationType = "Termination", Status = "InProgress", NoticeDate = new DateOnly(2026, 5, 31), NoticePeriodDays = 30,
            LastWorkingDay = new DateOnly(2026, 6, 30),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var computed = await Payroll(db, fx.TenantId).FinalSettlement(
            new FinalSettlementRequest(emp.Id, new DateOnly(2026, 6, 30), 0, "Termination"), CancellationToken.None);
        computed.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((computed as ObjectResult)?.Value));
        db.ChangeTracker.Clear();
        var settlement = await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(s => s.EmployeeId == emp.Id);

        settlement.LeaveEncashmentAmount.Should().Be(snapshot.Obligation,
            "provision and settlement value the same days at the same rate, so they agree to the halala");
        settlement.LeaveEncashmentDays.Should().Be(snapshot.Days);
    }

    // ── 5. Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LockRetry_NeverProvidesTwice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var jun = await LockMonthAsync(db, fx, 2026, 6);

        (await Payroll(db, fx.TenantId).Lock(jun, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        // A support data-fix that flips the run back to Approved must still not post a second provision.
        db.ChangeTracker.Clear();
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == jun);
        run.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, fx.TenantId).Lock(jun, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await LeaveLinesAsync(db, jun)).Count(l => l.CreditAccount == Provision).Should().Be(2);
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(7_350m);
        (await db.LeaveProvisionSnapshots.CountAsync()).Should().Be(3);
    }

    // ── 6. Void reverses the provision and forgets the measurement ──────────────

    [Fact]
    public async Task Void_ReversesTheLeaveLines_AndDeletesThatRunsSnapshots_SoAReplacementCanMeasureAgain()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await SetBalanceAsync(db, fx, "C", accrued: 22.75m, used: 0m);
        var jul = await LockMonthAsync(db, fx, 2026, 7);
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(7_700m);

        var voided = await Payroll(db, fx.TenantId).VoidRun(jul, new PayrollDecisionRequest("wrong July"), CancellationToken.None);
        voided.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((voided as ObjectResult)?.Value));
        db.ChangeTracker.Clear();

        (await LeaveLinesAsync(db, jul)).Should().OnlyContain(l => l.IsReversed, "every July leave line is contra'd");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(7_350m, "back to the June provision exactly");
        (await NetDebitAsync(db, fx.TenantId, Expense)).Should().Be(7_350m);
        (await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, fx.Emp.Values.Select(e => e.Id).ToList(), default))
            .Sum(p => p.Remaining).Should().Be(7_350m);
        (await db.LeaveProvisionSnapshots.Select(s => s.Period).Distinct().ToListAsync()).Should().Equal("2026-06");
    }

    // ── 6b. A void must not strand a provision a settlement already consumed ───

    [Fact]
    public async Task Void_IsRefused_WhenALaterSettlementAlreadyConsumedTheLeaveProvision_AndGoesThroughOnceItIsCancelled()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await SetBalanceAsync(db, fx, "A", accrued: 12.25m, used: 3m);
        var jul = await LockMonthAsync(db, fx, 2026, 7);
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 3_825m, encashmentDays: 12.75m);
        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var voided = await Payroll(db, fx.TenantId).VoidRun(jul, new PayrollDecisionRequest("wrong July"), CancellationToken.None);

        var refused = voided.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var body = JsonSerializer.Serialize(refused.Value);
        body.Should().Contain("provision_consumed_by_settlement").And.Contain("cancel the final settlement").And.Contain("Employee A");
        db.ChangeTracker.Clear();
        (await LeaveLinesAsync(db, jul)).Should().OnlyContain(l => !l.IsReversed, "a refused void writes nothing");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(4_200m, "2330 is not pushed into debit");

        (await Payroll(db, fx.TenantId).CancelFinalSettlement(s.Id, new PayrollReasonRequest("entered in error"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, fx.TenantId).VoidRun(jul, new PayrollDecisionRequest("wrong July"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    // ── 6c. Settlement counts a carried-forward day once ───────────────────────

    [Fact]
    public async Task FinalSettlement_CountsCarriedForwardDaysOnce_SoItAgreesWithTheProvision()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        // A: last year's row still shows 15 (21 granted, 6 used); this year's row carries those 15 forward plus 5 accrued.
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = fx.TenantId, EmployeeId = fx.Emp["A"].Id, EmployeeName = "Employee A", LeaveTypeId = fx.AnnualTypeId,
            LeaveTypeName = "Annual leave", Year = 2025, Accrued = 21m, Used = 6m,
        });
        var current = await db.EmployeeLeaveBalances.SingleAsync(b => b.TenantId == fx.TenantId && b.EmployeeId == fx.Emp["A"].Id && b.Year == 2026);
        current.Accrued = 5m; current.CarriedForward = 15m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await LockMonthAsync(db, fx, 2026, 6);
        var emp = fx.Emp["A"];
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = fx.TenantId, EmployeeId = emp.Id, EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
            SeparationType = "Termination", Status = "InProgress", NoticeDate = new DateOnly(2026, 5, 31), NoticePeriodDays = 30,
            LastWorkingDay = new DateOnly(2026, 6, 30),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, fx.TenantId).FinalSettlement(new FinalSettlementRequest(emp.Id, new DateOnly(2026, 6, 30), 0, "Termination"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var settlement = await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(x => x.EmployeeId == emp.Id);
        var snapshot = await db.LeaveProvisionSnapshots.AsNoTracking().SingleAsync(x => x.EmployeeId == emp.Id);
        (settlement.LeaveEncashmentDays, settlement.LeaveEncashmentAmount).Should().Be((20m, 6_000m), "15 carried + 5 accrued, not 35 days");
        (snapshot.Days, snapshot.Obligation).Should().Be((settlement.LeaveEncashmentDays, settlement.LeaveEncashmentAmount));
    }

    // ── 6d. A replacement run measures its own month, and keys the measurement on the pay period ──

    [Fact]
    public async Task ReplacementRun_IsMeasured_EvenWhenALaterMonthIsAlreadyMeasured_AndTheSnapshotKeepsThePayPeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var jun = await LockMonthAsync(db, fx, 2026, 6);
        await LockMonthAsync(db, fx, 2026, 7);
        (await Payroll(db, fx.TenantId).VoidRun(jun, new PayrollDecisionRequest("wrong June"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // The corrected June is a Replacement whose journal books into July (the pay period stays June).
        var created = await Payroll(db, fx.TenantId).CreateRun(
            new CreatePayrollRunRequest(2026, 6, fx.Company.Id, PayrollRunTypes.Replacement, jun, GlPostingPeriod: "2026-07"), CancellationToken.None);
        var replacement = (created as CreatedResult)?.Value.Should().BeOfType<PayrollRun>().Subject
            ?? throw new Xunit.Sdk.XunitException("Replacement not created: " + JsonSerializer.Serialize((created as ObjectResult)?.Value));
        db.ChangeTracker.Clear();
        (await Payroll(db, fx.TenantId).Process(replacement.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.FirstAsync(r => r.Id == replacement.Id)).Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var locked = await Payroll(db, fx.TenantId).Lock(replacement.Id, CancellationToken.None);
        locked.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((locked as ObjectResult)?.Value));
        db.ChangeTracker.Clear();

        var snapshots = await db.LeaveProvisionSnapshots.AsNoTracking().Where(s => s.PayrollRunId == replacement.Id).ToListAsync();
        snapshots.Should().HaveCount(3, "a later month's measurement must not stop the replacement of an earlier month");
        snapshots.Should().OnlyContain(s => s.Period == "2026-06", "the measurement is keyed on the pay period, not the GL posting period");
        var lines = await LeaveLinesAsync(db, replacement.Id);
        lines.Should().NotBeEmpty().And.OnlyContain(l => l.Period == "2026-07", "the journal itself posts into the GL period");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(7_350m);
    }

    // ── 7. Closed period ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ClosedGlPeriod_RefusesTheLock_AndPostsNoLeaveProvision()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = fx.TenantId, CompanyId = fx.Company.Id, Period = "2026-06", Status = GlPeriodStatuses.Closed });
        var run = new PayrollRun { TenantId = fx.TenantId, CompanyId = fx.Company.Id, Year = 2026, Month = 6, Status = "Draft", RunType = PayrollRunTypes.Regular };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, fx.TenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.FirstAsync(r => r.Id == run.Id)).Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var locked = await Payroll(db, fx.TenantId).Lock(run.Id, CancellationToken.None);

        locked.Should().BeOfType<UnprocessableEntityObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)locked).Value).Should().Contain("gl_period_closed");
        (await db.LeaveProvisionSnapshots.CountAsync()).Should().Be(0);
        (await db.FinanceGlEntries.AnyAsync(x => x.EventType == GlEventTypes.LeaveProvisionAccrual)).Should().BeFalse();
    }

    // ── 8. Audit row ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLockAudit_RecordsTheLeaveProvisionMovement_AndThatItWasTheCatchUp()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);

        await LockMonthAsync(db, fx, 2026, 6);

        var audit = await db.PayrollAuditLogs.AsNoTracking().SingleAsync(a => a.Action == "payroll.run.locked");
        using var doc = JsonDocument.Parse(audit.MetadataJson);
        var json = doc.RootElement.GetProperty("data");
        json.GetProperty("leaveProvisionMovement").GetDecimal().Should().Be(7_350m);
        json.GetProperty("leaveProvisionEmployees").GetInt32().Should().Be(3);
        json.GetProperty("leaveProvisionCatchUpEmployees").GetInt32().Should().Be(3);
    }

    // ── 9. Isolation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoCompanies_EachBooksItsOwnProvision_AndTheOtherLedgerIsUntouched()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var one = await SeedAsync(db);
        var two = await SeedAsync(db, sameTenant: one, companyName: "Sister Co");
        await SetBalanceAsync(db, two, "A", accrued: 20m, used: 0m);   // 20 x 300 = 6,000 on the sister company

        var runOne = await LockMonthAsync(db, one, 2026, 6);
        var runTwo = await LockMonthAsync(db, two, 2026, 6);

        (await LeaveLinesAsync(db, runOne)).Select(l => l.CompanyId).Distinct().Should().Equal(new Guid?[] { one.Company.Id });
        (await LeaveLinesAsync(db, runTwo)).Select(l => l.CompanyId).Distinct().Should().Equal(new Guid?[] { two.Company.Id });
        (await ProvisionBalanceAsync(db, one.TenantId, one.Company.Id)).Should().Be(7_350m);
        (await ProvisionBalanceAsync(db, two.TenantId, two.Company.Id)).Should().Be(4_200m + 6_000m);
        // Each company's own journal balances on its own.
        foreach (var run in new[] { runOne, runTwo })
        {
            var lines = await LeaveLinesAsync(db, run);
            lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        }
    }

    [Fact]
    public async Task AnotherTenant_NeverSeesThisTenantsProvisionPosition()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var other = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);

        var theirs = await LeaveProvisionLedger.LoadPositionsAsync(db, other.TenantId, fx.Emp.Values.Select(e => e.Id).ToList(), default);
        var mine = await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, fx.Emp.Values.Select(e => e.Id).ToList(), default);

        theirs.Should().BeEmpty("the sub-ledger reads exactly one tenant, whatever employee ids are asked for");
        mine.Sum(p => p.Remaining).Should().Be(7_350m);
    }

    // ── 10. Settlement consumes the provision first ─────────────────────────────

    private static async Task<EmployeeFinalSettlement> SeedPendingSettlementAsync(
        ZayraDbContext db, Fixture fx, string employeeCode, decimal encashment, decimal encashmentDays)
    {
        var emp = fx.Emp[employeeCode];
        var lastDay = new DateOnly(2026, 9, 15);
        var maker = Guid.NewGuid();
        var offboarding = new EmployeeOffboarding
        {
            TenantId = fx.TenantId, EmployeeId = emp.Id, EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
            SeparationType = "Resignation", Status = "InProgress", NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, EmployeeId = emp.Id, EmployeeCode = emp.EmployeeCode,
            EmployeeName = emp.FullName, OffboardingId = offboarding.Id, LastWorkingDay = lastDay,
            ServiceStartDate = new DateOnly(2024, 1, 1), SettlementDueDate = lastDay.AddDays(7), TerminationReason = "Resignation",
            ServiceYears = 1.7m, Currency = "SAR", GratuityAmount = 0m, LeaveEncashmentAmount = encashment, LeaveEncashmentDays = encashmentDays,
            GrossPayable = encashment, TotalDeductions = 0m, NetPayable = encashment, WagesPaidByRunId = Guid.NewGuid(), WageBaseDeltaAmount = 0m,
            Status = FinalSettlementStatuses.PendingApproval,
            CreatedByUserId = maker, CreatedByName = "Maker", SubmittedByUserId = maker, SubmittedByName = "Maker", SubmittedAtUtc = DateTime.UtcNow,
        };
        db.EmployeeOffboardings.Add(offboarding);
        db.EmployeeFinalSettlements.Add(settlement);
        db.FinalSettlementLines.Add(new FinalSettlementLine
        {
            TenantId = fx.TenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.LeaveEncashment,
            ComponentName = "Leave encashment — Annual leave", LineType = FinalSettlementLineTypes.Earning,
            Source = FinalSettlementComponents.SettlementSource, Amount = encashment, Quantity = encashmentDays, SortOrder = 0,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return settlement;
    }

    private static async Task<IActionResult> ApproveAsync(ZayraDbContext db, Fixture fx, EmployeeFinalSettlement s)
    {
        var body = new ApproveFinalSettlementRequest(ConfirmTerminationReason: s.TerminationReason, AccrualDate: new DateOnly(2026, 9, 15));
        var result = await Payroll(db, fx.TenantId, Guid.NewGuid()).ApproveFinalSettlement(s.Id, body, CancellationToken.None);
        db.ChangeTracker.Clear();
        return result;
    }

    private async Task<Fixture> ThreeMonthsAsync(ZayraDbContext db)
    {
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await SetBalanceAsync(db, fx, "A", accrued: 12.25m, used: 3m);   // 9.25 days -> 2,775 provided
        await LockMonthAsync(db, fx, 2026, 7);
        return fx;
    }

    [Fact]
    public async Task Settlement_ConsumesTheProvisionFirst_AndExpensesOnlyTheDifference_SoLeaveCostIsNeverDoubled()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ThreeMonthsAsync(db);
        // By September A accrued 3.5 more days: 12.75 days x 300 = 3,825 encashed against 2,775 provided.
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 3_825m, encashmentDays: 12.75m);

        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var accrual = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        accrual.Single(l => l.DebitAccount == Provision).Amount.Should().Be(2_775m, "the whole provision for A is consumed first");
        accrual.Single(l => l.DebitAccount == Provision).EventType.Should().Be(GlEventTypes.LeaveProvisionConsumption);
        accrual.Single(l => l.DebitAccount == EncashmentExpense).Amount.Should().Be(1_050m, "only the part not yet provided is new expense");
        accrual.Single(l => l.CreditAccount.StartsWith("2320")).Amount.Should().Be(3_825m);
        accrual.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(accrual.Where(l => l.CreditAccount != "").Sum(l => l.Amount));

        var positions = await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Emp["A"].Id }, default);
        positions.Sum(p => p.Remaining).Should().Be(0m, "A's provision is gone");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(4_200m, "only C's provision remains in 2330");
        // Total leave cost recognised for A = the encashment, once: provision-time expense (2,775 in 5115) + settlement-time (1,050 in 5111).
        (await NetDebitAsync(db, fx.TenantId, EncashmentExpense)).Should().Be(1_050m);
        (await NetDebitAsync(db, fx.TenantId, Expense)).Should().Be(4_200m + 2_775m, "5115 keeps A's 2,775 (now settled) and C's 4,200");
    }

    [Fact]
    public async Task Settlement_BelowTheProvision_ReleasesTheRemainder_BackOutOfTheProvisionExpense()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ThreeMonthsAsync(db);
        // A used most of the balance after July: only 6 days (1,800) are left to encash against 2,775 provided.
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 1_800m, encashmentDays: 6m);

        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var accrual = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        accrual.Where(l => l.DebitAccount == Provision).Sum(l => l.Amount).Should().Be(2_775m, "consumed (1,800) + released (975) = everything provided for A");
        accrual.Should().NotContain(l => l.DebitAccount == EncashmentExpense, "the provision already covered the whole encashment");
        var release = accrual.Single(l => l.CreditAccount == Expense);
        release.Amount.Should().Be(975m);
        release.Description.Should().StartWith(FinalSettlementGlDescriptions.LeaveProvisionReleasePrefix);
        accrual.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(accrual.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        (await NetDebitAsync(db, fx.TenantId, Expense)).Should().Be(4_200m + 1_800m, "A's expense nets down to what was actually paid; C's provision is untouched");
        (await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Emp["A"].Id }, default)).Sum(p => p.Remaining).Should().Be(0m);
    }

    [Fact]
    public async Task Settlement_WithNoProvision_ExpensesTheWholeEncashment_ExactlyAsBefore()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db, provisioningOn: false);
        await LockMonthAsync(db, fx, 2026, 6);
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 3_825m, encashmentDays: 12.75m);

        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var accrual = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        accrual.Should().HaveCount(2);
        accrual.Single(l => l.DebitAccount == EncashmentExpense).Amount.Should().Be(3_825m);
        accrual.Should().NotContain(l => l.EventType == GlEventTypes.LeaveProvisionConsumption);
    }

    [Fact]
    public async Task CancellingTheSettlement_ReopensTheLeaveProvision_ForTheSameEmployee()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ThreeMonthsAsync(db);
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 3_825m, encashmentDays: 12.75m);
        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var cancelled = await Payroll(db, fx.TenantId).CancelFinalSettlement(s.Id, new PayrollReasonRequest("entered in error"), CancellationToken.None);
        cancelled.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((cancelled as ObjectResult)?.Value));
        db.ChangeTracker.Clear();

        (await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Emp["A"].Id }, default))
            .Sum(p => p.Remaining).Should().Be(2_775m, "the consumption is contra'd, so the provision is carried again");
        (await db.FinanceGlEntries.AsNoTracking().AnyAsync(x => x.EventType == GlEventTypes.LeaveProvisionConsumptionReversal)).Should().BeTrue();
    }

    [Fact]
    public async Task ALeaverWhoseSettlementIsApproved_IsNotProvidedForAgain()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ThreeMonthsAsync(db);
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 3_825m, encashmentDays: 12.75m);
        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();
        await SetBalanceAsync(db, fx, "A", accrued: 12.25m, used: 3m);

        var sep = await LockMonthAsync(db, fx, 2026, 9);

        (await LeaveLinesAsync(db, sep)).Should().NotContain(l => l.SourceEntityRef == fx.Emp["A"].Id.ToString());
    }

    /// <summary>A is settled (last working day 15 Sep, paid) and rehired from 1 November with the same employee id; 2 new days by the
    /// November month end (2 x 300 = 600).</summary>
    private async Task<Fixture> SettleThenRehireAAsync(ZayraDbContext db)
    {
        var fx = await ThreeMonthsAsync(db);
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 2_775m, encashmentDays: 9.25m);
        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();
        var settled = await db.EmployeeFinalSettlements.SingleAsync(x => x.Id == s.Id);
        settled.Status = FinalSettlementStatuses.Paid;
        var a = await db.Employees.SingleAsync(e => e.Id == fx.Emp["A"].Id);
        a.OriginalJoiningDate = DateOnly.FromDateTime(a.JoiningDate);
        a.JoiningDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        a.Status = "Active";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await SetBalanceAsync(db, fx, "A", accrued: 2m, used: 0m);
        return fx;
    }

    [Fact]
    public async Task ARehiredEmployee_WhoseOnlySettlementClosedAnEarlierServicePeriod_IsProvidedForAgain()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SettleThenRehireAAsync(db);

        var nov = await LockMonthAsync(db, fx, 2026, 11);

        var aLines = (await LeaveLinesAsync(db, nov)).Where(l => l.SourceEntityRef == fx.Emp["A"].Id.ToString()).ToList();
        aLines.Should().ContainSingle("the settlement closed the EARLIER service period; the new one is provided for")
            .Which.Should().Match<FinanceGlEntry>(l => l.CreditAccount == Provision && l.Amount == 600m);
        (await db.LeaveProvisionSnapshots.AsNoTracking().SingleAsync(x => x.EmployeeId == fx.Emp["A"].Id && x.Period == "2026-11"))
            .Obligation.Should().Be(600m);
    }

    [Fact]
    public async Task ASettlementInsideTheCurrentServicePeriod_StillExcludesTheLeaver_EvenAfterARehireRecord()
    {
        // The settlement's last working day (15 Sep) is on or after the current joining date (1 Sep): it belongs to THIS period.
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ThreeMonthsAsync(db);
        var a = await db.Employees.SingleAsync(e => e.Id == fx.Emp["A"].Id);
        a.JoiningDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 2_775m, encashmentDays: 9.25m);
        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();
        await SetBalanceAsync(db, fx, "A", accrued: 12.25m, used: 3m);

        var oct = await LockMonthAsync(db, fx, 2026, 10);

        (await LeaveLinesAsync(db, oct)).Should().NotContain(l => l.SourceEntityRef == fx.Emp["A"].Id.ToString());
    }

    // ── A settlement consumes a provision split over two accounts ───────────────

    /// <summary>June credits A's 3,150 to 2330; LEAVE_PROVISION is then remapped to 2331 and July's rise to 13 days (3,900) credits
    /// the extra 750 to 2331. A now holds 3,150 on 2330 and 750 on 2331.</summary>
    private async Task<Fixture> ProvisionSplitOverTwoAccountsAsync(ZayraDbContext db)
    {
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await RemapLeaveProvisionAsync(db, fx);
        await SetBalanceAsync(db, fx, "A", accrued: 13m, used: 0m);
        await LockMonthAsync(db, fx, 2026, 7);
        (await ProvisionByAccountAsync(db, fx, "A")).Should().Equal(new Dictionary<string, decimal> { ["2330"] = 3_150m, [RemappedProvisionCode] = 750m });
        return fx;
    }

    [Fact]
    public async Task Settlement_ConsumesAProvisionSplitOverTwoAccounts_LargestFirst_AndExpensesNothingTheProvisionCovers()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ProvisionSplitOverTwoAccountsAsync(db);
        // 12.75 days x 300 = 3,825 encashed against 3,900 provided over the two accounts.
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 3_825m, encashmentDays: 12.75m);

        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var journal = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        var relief = journal.Where(l => l.Description.StartsWith(FinalSettlementGlDescriptions.LeaveProvisionReliefPrefix)).ToList();
        relief.Should().OnlyContain(l => l.EventType == GlEventTypes.LeaveProvisionConsumption && l.CreditAccount == "");
        relief.Select(l => (l.DebitAccount[..4], l.Amount)).Should().BeEquivalentTo(new[] { ("2330", 3_150m), (RemappedProvisionCode, 675m) },
            "the largest holding is consumed first, then the next, each up to its own balance");
        journal.Should().NotContain(l => l.DebitAccount.StartsWith("5111"), "the provision covers the whole encashment");
        var release = journal.Where(l => l.Description.StartsWith(FinalSettlementGlDescriptions.LeaveProvisionReleasePrefix)).ToList();
        release.Where(l => l.DebitAccount != "").Select(l => (l.DebitAccount[..4], l.Amount)).Should().Equal((RemappedProvisionCode, 75m));
        release.Single(l => l.CreditAccount != "").Amount.Should().Be(75m);
        journal.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(journal.Where(l => l.CreditAccount != "").Sum(l => l.Amount));

        (await ProvisionByAccountAsync(db, fx, "A")).Values.Should().OnlyContain(v => v == 0m, "both accounts are relieved to zero, neither below");
        (await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Emp["A"].Id }, default)).Sum(p => p.Remaining).Should().Be(0m);
    }

    [Fact]
    public async Task Settlement_AboveAProvisionSplitOverTwoAccounts_ConsumesBoth_AndExpensesOnlyTheTrueShortfall()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ProvisionSplitOverTwoAccountsAsync(db);
        // 14 days x 300 = 4,200 encashed against 3,900 provided: 300 is new expense.
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 4_200m, encashmentDays: 14m);

        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var journal = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        journal.Where(l => l.Description.StartsWith(FinalSettlementGlDescriptions.LeaveProvisionReliefPrefix))
            .Select(l => (l.DebitAccount[..4], l.Amount)).Should().BeEquivalentTo(new[] { ("2330", 3_150m), (RemappedProvisionCode, 750m) });
        journal.Single(l => l.DebitAccount.StartsWith("5111")).Amount.Should().Be(300m, "only the part not provided on either account is expensed");
        journal.Should().NotContain(l => l.Description.StartsWith(FinalSettlementGlDescriptions.LeaveProvisionReleasePrefix));
        journal.Single(l => l.CreditAccount.StartsWith("2320")).Amount.Should().Be(4_200m);
        journal.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(journal.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        (await ProvisionByAccountAsync(db, fx, "A")).Values.Should().OnlyContain(v => v == 0m);
    }

    /// <summary>
    /// The provision nets per ACCOUNT, so a fall booked on a remapped account (B, debit 30) reduces what the employee carries even
    /// though B's own position clamps to zero. A +100 and B -30 carry 70; an encashment of 50 consumes 50 from A, and only the 20
    /// left may be released. Releasing per position released 50 and drove the employee's provision 30 below zero.
    /// </summary>
    [Fact]
    public async Task Settlement_ReleasesOnlyWhatTheEmployeeStillCarries_AfterAFallOnAnotherAccount()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db, provisioningOn: false);
        var emp = fx.Emp["A"];
        FinanceGlEntry Line(string debit, string credit, decimal amount) => new()
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, SourceModule = FinalSettlementGlDescriptions.SourceModule,
            SourceEntityRef = LeaveProvisionLedger.EmployeeRef(emp.Id), EventType = GlEventTypes.LeaveProvisionAccrual,
            DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = "SAR",
            EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07", Description = "Leave provision",
        };
        db.FinanceGlEntries.AddRange(
            Line(string.Empty, Provision, 100m),
            Line("2331 - Leave Provision (remapped)", string.Empty, 30m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 50m, encashmentDays: 0.5m);

        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var journal = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        journal.Where(l => l.Description.StartsWith(FinalSettlementGlDescriptions.LeaveProvisionReliefPrefix)).Sum(l => l.Amount)
            .Should().Be(50m, "the encashment is covered by A's provision");
        var release = journal.Where(l => l.Description.StartsWith(FinalSettlementGlDescriptions.LeaveProvisionReleasePrefix)).ToList();
        release.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(20m, "70 carried less 50 taken");
        release.Where(l => l.CreditAccount != "").Sum(l => l.Amount).Should().Be(20m);
        journal.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(journal.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        var positions = await LeaveProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { emp.Id }, default);
        LeaveProvisionLedger.CarriedFor(positions, emp.Id, fx.Company.Id).Should().Be(0m);
        positions.Sum(p => p.Accrued - p.Consumed).Should().Be(0m, "the employee's provision closes at zero, not 30 below it");
    }

    // ── A rehire after an encashed exit is provided for the new period's days only ──

    [Fact]
    public async Task AfterAnExitWhoseEncashmentTookTheDaysOffTheBalance_ARehireIsProvidedForTheNewPeriodsDaysOnly()
    {
        // Either payment rail (payroll run, or outside payroll — see LeaverLifecycleS2Tests) increments Encashed on the row the
        // settlement names; this pins what the provision then reads for the same employee id after a rehire.
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SettleThenRehireAAsync(db);
        var row = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == fx.Emp["A"].Id);
        row.Accrued = 14.25m; row.Used = 3m; row.Encashed = 9.25m;   // 9.25 paid at the exit, 2 accrued since 1 November
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var nov = await LockMonthAsync(db, fx, 2026, 11);

        (await LeaveLinesAsync(db, nov)).Where(l => l.SourceEntityRef == fx.Emp["A"].Id.ToString())
            .Should().ContainSingle().Which.Should().Match<FinanceGlEntry>(l => l.CreditAccount == Provision && l.Amount == 600m,
                "2 days x 300, not the 9.25 days already paid");
        var snapshot = await db.LeaveProvisionSnapshots.AsNoTracking().SingleAsync(x => x.EmployeeId == fx.Emp["A"].Id && x.Period == "2026-11");
        (snapshot.Days, snapshot.Obligation).Should().Be((2m, 600m));
    }

    // ── The provision panel follows the same service-period rule ────────────────

    private static async Task<JsonElement> LeavePanelAsync(ZayraDbContext db, Fixture fx)
    {
        var ctrl = new GlAdjustmentsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = User(fx.TenantId, Guid.NewGuid(), "finance", "finance.gl.read") },
            },
        };
        var result = await ctrl.GetLeaveProvision(fx.Company.Id, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((result as ObjectResult)?.Value));
        return JsonSerializer.SerializeToElement(((OkObjectResult)result).Value);
    }

    private static JsonElement? PanelRow(JsonElement panel, Employee employee) =>
        panel.GetProperty("employees").EnumerateArray()
            .Cast<JsonElement?>()
            .FirstOrDefault(e => e!.Value.GetProperty("employeeId").GetInt32() == employee.Id);

    [Fact]
    public async Task ThePanel_ShowsARehiredEmployee_WhoseOnlySettlementClosedAnEarlierServicePeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SettleThenRehireAAsync(db);
        await LockMonthAsync(db, fx, 2026, 11);

        var row = PanelRow(await LeavePanelAsync(db, fx), fx.Emp["A"]);

        row.Should().NotBeNull("the settlement closed the earlier service period; the panel shows the new one");
        row!.Value.GetProperty("lastMeasuredPeriod").GetString().Should().Be("2026-11");
        row.Value.GetProperty("obligation").GetDecimal().Should().Be(600m);
        row.Value.GetProperty("provisionCarried").GetDecimal().Should().Be(600m);
    }

    /// <summary>
    /// Rehired on 1 November, before any lock has measured the new period: the only snapshot is July's, which measured the service
    /// the settlement closed. The panel shows no obligation for the new period yet and says it is not yet measured.
    /// </summary>
    [Fact]
    public async Task ThePanel_ShowsARehireBeforeTheFirstNewLock_AsNotYetMeasured_WithNoObligation()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SettleThenRehireAAsync(db);

        var panel = await LeavePanelAsync(db, fx);
        var row = PanelRow(panel, fx.Emp["A"]);

        row.Should().NotBeNull("a rehired employee is in the panel");
        row!.Value.GetProperty("notYetMeasured").GetBoolean().Should().BeTrue();
        row.Value.GetProperty("obligation").GetDecimal().Should().Be(0m, "July's figure measured the service the settlement already paid");
        row.Value.GetProperty("days").GetDecimal().Should().Be(0m);
        row.Value.GetProperty("lastMovement").GetDecimal().Should().Be(0m);
        PanelRow(panel, fx.Emp["C"])!.Value.GetProperty("notYetMeasured").GetBoolean().Should().BeFalse();
        var others = panel.GetProperty("employees").EnumerateArray()
            .Where(e => e.GetProperty("employeeId").GetInt32() != fx.Emp["A"].Id)
            .Sum(e => e.GetProperty("obligation").GetDecimal());
        panel.GetProperty("employeeObligation").GetDecimal().Should().Be(others, "A adds nothing to the total until the new period is measured");
    }

    [Fact]
    public async Task ThePanel_StillHidesALeaverSettledInTheCurrentServicePeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await ThreeMonthsAsync(db);
        var s = await SeedPendingSettlementAsync(db, fx, "A", encashment: 2_775m, encashmentDays: 9.25m);
        (await ApproveAsync(db, fx, s)).Should().BeOfType<OkObjectResult>();

        var panel = await LeavePanelAsync(db, fx);

        PanelRow(panel, fx.Emp["A"]).Should().BeNull("A's settlement (last working day 15 Sep) is inside the service that began 1 Jan 2024");
        PanelRow(panel, fx.Emp["C"]).Should().NotBeNull();
    }

    // ── A remap of LEAVE_PROVISION between months ───────────────────────────────

    private const string RemappedProvisionCode = "2331";

    private static async Task RemapLeaveProvisionAsync(ZayraDbContext db, Fixture fx)
    {
        var account = new GlAccount
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, Code = RemappedProvisionCode, Name = "Leave Provision (remapped)", AccountType = "Liability",
        };
        db.GlAccounts.Add(account);
        db.GlAccountMappings.Add(new GlAccountMapping
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, DriverKey = LeaveProvisioningRateKeys.ProvisionDriverKey, AccountId = account.Id,
        });
        await db.SaveChangesAsync();
        // A tenant with a chart must hold every account its Lock posts to; the seeder never overwrites the remap.
        await Zayra.Api.Infrastructure.Seed.GlDriverSeeder.SeedTenantDefaultsAsync(db, fx.TenantId, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>CR − DR per provision account code (233x) over every leave-provision line, for one employee.</summary>
    private static async Task<Dictionary<string, decimal>> ProvisionByAccountAsync(ZayraDbContext db, Fixture fx, string code)
    {
        var employeeRef = fx.Emp[code].Id.ToString();
        var rows = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == fx.TenantId && x.SourceEntityRef == employeeRef
                     && (x.EventType == GlEventTypes.LeaveProvisionAccrual || x.EventType == GlEventTypes.LeaveProvisionConsumption))
            .ToListAsync();
        return rows
            .Select(r => string.IsNullOrEmpty(r.CreditAccount) ? (Account: r.DebitAccount, Amount: -r.Amount) : (Account: r.CreditAccount, r.Amount))
            .Where(r => r.Account.StartsWith("233"))   // the provision side only (a release's 5115 credit also carries the employee ref)
            .GroupBy(r => r.Account[..4])
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    [Fact]
    public async Task AFallAfterTheProvisionAccountIsRemapped_DebitsTheAccountHoldingTheProvision_AndARepeatMonthPostsNothing()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);               // A: 3,150 credited to 2330; C: 4,200 to 2330
        await RemapLeaveProvisionAsync(db, fx);              // LEAVE_PROVISION -> 2331 from here on
        await SetBalanceAsync(db, fx, "A", accrued: 12.25m, used: 3m);   // A falls to 9.25 days -> 2,775
        await SetBalanceAsync(db, fx, "C", accrued: 22.75m, used: 0m);   // C rises to 22.75 days -> 4,550

        var jul = await LockMonthAsync(db, fx, 2026, 7);

        var lines = await LeaveLinesAsync(db, jul);
        var aLine = lines.Single(l => l.SourceEntityRef == fx.Emp["A"].Id.ToString());
        aLine.DebitAccount.Should().StartWith("2330", "the fall relieves the account that actually carries A's provision");
        aLine.Amount.Should().Be(375m);
        var cLine = lines.Single(l => l.SourceEntityRef == fx.Emp["C"].Id.ToString());
        cLine.CreditAccount.Should().StartWith(RemappedProvisionCode, "a rise goes to the account mapped now");
        cLine.Amount.Should().Be(350m);
        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));

        var aByAccount = await ProvisionByAccountAsync(db, fx, "A");
        aByAccount.Should().Equal(new Dictionary<string, decimal> { ["2330"] = 2_775m });
        aByAccount.Values.Should().OnlyContain(v => v >= 0m, "no account is driven below zero");

        // August, nothing changed: the ledger reads exactly what is carried, so nothing posts again.
        var aug = await LockMonthAsync(db, fx, 2026, 8);
        (await LeaveLinesAsync(db, aug)).Should().BeEmpty();
        (await ProvisionByAccountAsync(db, fx, "A")).Should().Equal(new Dictionary<string, decimal> { ["2330"] = 2_775m });
        (await ProvisionByAccountAsync(db, fx, "C")).Should().Equal(new Dictionary<string, decimal> { ["2330"] = 4_200m, [RemappedProvisionCode] = 350m });
    }

    // ── M6 go-live period: the opening-balance route is keyed on the PERIOD, not on "nothing measured yet" ─────────────

    [Fact]
    public async Task OpeningRoute_AppliesToEveryPayGroupLockingInTheGoLivePeriod_NotOnlyTheFirstToLock()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, RateKey = ProvisionCatchUpRoute.Key, RateCategory = "PayParameter",
            RateValue = ProvisionCatchUpRoute.OpeningBalance, DataType = "string", Unit = "enum",
            EffectiveFrom = new DateOnly(2026, 6, 1), Status = CompanyPolicyStatuses.Active,
        });
        // Another pay group of the same company locked June first and measured employee A.
        var otherGroupRun = new PayrollRun
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, Year = 2026, Month = 6, Status = "Locked", RunType = PayrollRunTypes.OffCycle,
        };
        db.PayrollRuns.Add(otherGroupRun);
        await db.SaveChangesAsync();
        db.LeaveProvisionSnapshots.Add(new LeaveProvisionSnapshot
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, EmployeeId = fx.Emp["A"].Id, Period = "2026-06",
            MeasuredAt = new DateOnly(2026, 6, 30), PayrollRunId = otherGroupRun.Id, Days = 10.5m, DailyRate = 300m, Basis = "Gross",
            Obligation = 3_150m, CarriedBefore = 0m, Movement = 3_150m, IsCatchUp = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var jun = await LockMonthAsync(db, fx, 2026, 6);

        var lines = await LeaveLinesAsync(db, jun);
        lines.Should().NotContain(l => l.SourceEntityRef == fx.Emp["A"].Id.ToString(), "A was measured by the other pay group");
        lines.Where(l => l.CreditAccount == Provision).Should().ContainSingle().Which.Amount.Should().Be(4_200m);
        lines.Should().ContainSingle(l => l.DebitAccount == "3900 - Opening Balance Equity" && l.Amount == 4_200m,
            "C's first measurement in the go-live period is an opening balance, whichever run locks first");
        lines.Should().NotContain(l => l.DebitAccount == Expense, "nothing is left to expense");
    }

    [Theory]
    [InlineData("OpeningBalance", "2026-06-01", "2026-06", null, true)]        // the company's first ever run
    [InlineData("OpeningBalance", "2026-06-01", "2026-06", "2026-06", true)]   // a second pay group in the same go-live period
    [InlineData("OpeningBalance", "2026-06-01", "2026-07", "2026-06", false)]  // any later period is expense
    [InlineData("OpeningBalance", "2026-12-01", "2026-12", "2026-10", true)]   // the route dated to a go-live chosen after earlier measurements
    [InlineData("OpeningBalance", "2026-12-01", "2026-11", "2026-10", false)]
    [InlineData("Expense", "2026-06-01", "2026-06", null, false)]
    [InlineData(null, null, "2026-06", null, false)]
    public void OpeningRoute_AppliesToThePeriod_ByTheGoLivePeriod(string? route, string? effective, string payPeriod, string? earliest, bool expected)
    {
        var from = effective is null ? (DateOnly?)null : DateOnly.Parse(effective);
        ProvisionCatchUpRoute.AppliesToPeriod(route, from, payPeriod, earliest).Should().Be(expected);
    }
}

// ── File-scoped doubles ───────────────────────────────────────────────────────

file static class _LpRules
{
    internal static readonly StubRuleReader Rules = PayComponentNetPayDefectTests.KsaRules();
}

file sealed class _LpScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _LpHttp : IHttpContextAccessor
{
    public _LpHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _LpKsaResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _LpKsaResolver(StubRuleReader rules) => _rules = rules;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaDeductionCalculator(_rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(_rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
