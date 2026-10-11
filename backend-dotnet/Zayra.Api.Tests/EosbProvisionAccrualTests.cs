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
/// POD-C2 — the monthly end-of-service provision booked at payroll Lock, its void, its consumption and
/// release at settlement, and the maker-checker actuarial true-up.
///
/// <para>The worked case: a Saudi employee who joined on 2023-06-30 on basic 8,000 + housing 2,000 (the
/// Art. 84 last wage is 10,000; no transport, so nothing arguable is in the base). At 2026-06-30 they
/// have exactly three years: ½ month × 3 × 10,000 = <b>15,000.00</b> — the June catch-up. Every later
/// month is measured with the same formula and only the difference is booked.</para>
/// </summary>
public class EosbProvisionAccrualTests
{
    private static readonly DateOnly Joined = new(2023, 6, 30);
    private const string Provision = "2310 - End of Service Benefit Provision";
    private const string Expense = "5110 - End of Service Benefit Expense";

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
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, "Admin"),
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
            db, new _EpScope(), new _EpHttp(http), new F2NullNotifications(),
            new _EpKsaResolver(_EpRules.Rules), _EpRules.Rules, new F2NullLetters(), new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    private static GlAdjustmentsController Gl(ZayraDbContext db, Guid tenantId, Guid userId, string name = "finance")
    {
        var ctrl = new GlAdjustmentsController(db);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = User(tenantId, userId, name, "finance.gl.manage", "finance.gl.read") },
        };
        return ctrl;
    }

    private sealed record Fixture(Guid TenantId, Company Company, Employee Employee);

    private static async Task<Fixture> SeedAsync(ZayraDbContext db, bool provisioningOn = true,
        decimal basic = 8_000m, decimal housing = 2_000m)
    {
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Provision KSA Co", RegistrationNumber = $"CR-{Guid.NewGuid():N}"[..12],
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = "SAU", EosbEnabled = true });
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2023, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();

        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E001", FullName = "Sara Al-Qahtani",
            Status = "Active", JoiningDate = Joined.ToDateTime(TimeOnly.MinValue), WorkEmail = "sara@test.com",
            Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = basic, HousingAllowance = housing, TransportAllowance = 0m,
            Currency = "SAR", EffectiveDate = new DateOnly(2023, 6, 30), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = "MOL-E001", SalaryCurrency = "SAR",
        });
        if (provisioningOn)
            db.CompanyRatePolicies.Add(new CompanyRatePolicy
            {
                TenantId = tenantId, CompanyId = company.Id, RateKey = EosbProvisioningRateKeys.Mode,
                RateCategory = "PayParameter", RateValue = EosbProvisioningRateKeys.StatutoryMonthly,
                DataType = "string", Unit = "enum", EffectiveFrom = new DateOnly(2026, 6, 1),
                Status = CompanyPolicyStatuses.Active,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, company, emp);
    }

    private static async Task ChangeWageAsync(ZayraDbContext db, Fixture fx, DateOnly from, decimal basic, decimal housing)
    {
        var structureId = await db.SalaryStructures.Where(s => s.TenantId == fx.TenantId).Select(s => s.Id).FirstAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = fx.TenantId, EmployeeId = fx.Employee.Id, SalaryStructureId = structureId,
            BasicSalary = basic, HousingAllowance = housing, TransportAllowance = 0m,
            Currency = "SAR", EffectiveDate = from, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Drafts, processes, approves and locks one Regular month; returns the run id.</summary>
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

        var ctrl = Payroll(db, fx.TenantId);
        var processed = await ctrl.Process(run.Id, CancellationToken.None);
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

    private static Task<List<FinanceGlEntry>> ProvisionLinesAsync(ZayraDbContext db, Guid runId) =>
        db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceEntityId == runId && x.EventType == GlEventTypes.EosbProvisionAccrual)
            .ToListAsync();

    /// <summary>CR − DR on 2310 over every row: the provision the GL carries.</summary>
    private static async Task<decimal> ProvisionBalanceAsync(ZayraDbContext db, Guid tenantId)
    {
        var rows = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.DebitAccount == Provision || x.CreditAccount == Provision))
            .ToListAsync();
        return rows.Sum(r => r.CreditAccount == Provision ? r.Amount : -r.Amount);
    }

    private static async Task<decimal> ExpenseNetAsync(ZayraDbContext db, Guid tenantId)
    {
        var rows = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.DebitAccount == Expense || x.CreditAccount == Expense))
            .ToListAsync();
        return rows.Sum(r => r.DebitAccount == Expense ? r.Amount : -r.Amount);
    }

    /// <summary>Independent oracle for the KSA Art. 84 full award (tier 1 only: under five years here).</summary>
    private static decimal Art84(DateOnly start, DateOnly end, decimal wage)
    {
        var months = (end.Year - start.Year) * 12 + (end.Month - start.Month);
        var days = end.Day - start.Day;
        if (days < 0)
        {
            months--;
            days += DateTime.DaysInMonth(end.Year, end.Month == 1 ? 12 : end.Month - 1);
        }
        var years = months / 12m + days / 365m;
        return Math.Round(Math.Min(years, 5m) * 0.5m * wage, 2) + Math.Round(Math.Max(0m, years - 5m) * wage, 2);
    }

    private static readonly DateOnly June = new(2026, 6, 30), July = new(2026, 7, 31), August = new(2026, 8, 31);

    // ── 1. Three months: catch-up, then the formula's monthly deltas ─────────────

    [Fact]
    public async Task ThreeMonths_BookTheCatchUp_ThenExactlyTheFormulaDeltas_AndTheLedgerCarriesTheObligation()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);

        var jun = await LockMonthAsync(db, fx, 2026, 6);
        var jul = await LockMonthAsync(db, fx, 2026, 7);
        var aug = await LockMonthAsync(db, fx, 2026, 8);

        var oJun = Art84(Joined, June, 10_000m);
        var oJul = Art84(Joined, July, 10_000m);
        var oAug = Art84(Joined, August, 10_000m);
        oJun.Should().Be(15_000.00m, "exactly three years at half a month of a 10,000 last wage");

        var junLines = await ProvisionLinesAsync(db, jun);
        junLines.Single(l => l.CreditAccount == Provision).Amount.Should().Be(oJun);
        junLines.Single(l => l.DebitAccount == Expense).Description.Should().Be(EosbProvisionPlanner.CatchUpLabel,
            "the first month after switching on books the whole obligation to date as an opening catch-up");
        junLines.Single(l => l.CreditAccount == Provision).SourceEntityRef.Should().Be(fx.Employee.Id.ToString());

        (await ProvisionLinesAsync(db, jul)).Single(l => l.CreditAccount == Provision).Amount.Should().Be(oJul - oJun);
        var augLines = await ProvisionLinesAsync(db, aug);
        augLines.Single(l => l.CreditAccount == Provision).Amount.Should().Be(oAug - oJul);
        augLines.Single(l => l.DebitAccount == Expense).Description.Should().Be(EosbProvisionPlanner.MonthlyLabel);

        foreach (var run in new[] { jun, jul, aug })
        {
            var lines = await ProvisionLinesAsync(db, run);
            lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount)
                .Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount), "each month's provision balances on its own");
            lines.Select(l => l.CompanyId).Distinct().Should().Equal(new Guid?[] { fx.Company.Id }, "booked in the run's legal entity only");
        }

        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(oAug);
        var position = (await EosbProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Employee.Id }, CancellationToken.None)).Single();
        position.Remaining.Should().Be(oAug, "the sub-ledger the settlement consumes carries exactly the obligation");

        var snapshots = await db.EosbProvisionSnapshots.AsNoTracking().OrderBy(s => s.Period).ToListAsync();
        snapshots.Select(s => s.Obligation).Should().Equal(oJun, oJul, oAug);
        snapshots.Select(s => s.IsCatchUp).Should().Equal(true, false, false);
        snapshots.All(s => s.WageBase == 10_000m && s.Rule == "KSA-LaborLaw-Art84").Should().BeTrue();
    }

    // ── 2. Off company books nothing ──────────────────────────────────────────────

    [Fact]
    public async Task ProvisioningOff_LockPostsNoProvision_AndTheJournalIsUnchanged()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db, provisioningOn: false);

        var run = await LockMonthAsync(db, fx, 2026, 6);

        (await ProvisionLinesAsync(db, run)).Should().BeEmpty("provisioning defaults to Off");
        (await db.EosbProvisionSnapshots.CountAsync()).Should().Be(0);
        (await db.FinanceGlEntries.AnyAsync(x => x.DebitAccount == Expense || x.CreditAccount == Provision)).Should().BeFalse();
    }

    // ── 3. A pay rise is trued up next month ───────────────────────────────────────

    [Fact]
    public async Task WageIncrease_NextMonthMovement_RemeasuresTheWholeServiceOnTheNewWage()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await ChangeWageAsync(db, fx, new DateOnly(2026, 7, 1), basic: 9_000m, housing: 3_000m);

        var jul = await LockMonthAsync(db, fx, 2026, 7);

        var expected = Art84(Joined, July, 12_000m) - Art84(Joined, June, 10_000m);
        (await ProvisionLinesAsync(db, jul)).Single(l => l.CreditAccount == Provision).Amount.Should().Be(expected,
            "the Art. 84 award is on the LAST wage, so a rise re-prices every year already served");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(Art84(Joined, July, 12_000m));
    }

    // ── 4. A pay cut lowers the provision (DR 2310 / CR 5110) ─────────────────────

    [Fact]
    public async Task WageDecrease_BooksANegativeMovement_DebitingTheProvision()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await ChangeWageAsync(db, fx, new DateOnly(2026, 7, 1), basic: 6_000m, housing: 1_500m);

        var jul = await LockMonthAsync(db, fx, 2026, 7);

        var oJul = Art84(Joined, July, 7_500m);
        var fall = 15_000m - oJul;
        fall.Should().BePositive();
        var lines = await ProvisionLinesAsync(db, jul);
        lines.Single(l => l.DebitAccount == Provision).Amount.Should().Be(fall);
        var credit = lines.Single(l => l.CreditAccount == Expense);
        credit.Amount.Should().Be(fall);
        credit.Description.Should().Be(EosbProvisionPlanner.ReleaseLabel);
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(oJul);
        (await EosbProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Employee.Id }, CancellationToken.None))
            .Single().Remaining.Should().Be(oJul, "the fall nets against the same employee's position");
    }

    // ── 5. Void reverses the provision and forgets the measurement ─────────────────

    [Fact]
    public async Task Void_ReversesTheProvisionLines_AndDeletesThatRunsSnapshots()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        var jul = await LockMonthAsync(db, fx, 2026, 7);

        var voided = await Payroll(db, fx.TenantId).VoidRun(jul, new PayrollDecisionRequest("wrong July"), CancellationToken.None);
        voided.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((voided as ObjectResult)?.Value));
        db.ChangeTracker.Clear();

        (await ProvisionLinesAsync(db, jul)).Should().OnlyContain(l => l.IsReversed, "every July provision line is contra'd");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(15_000m, "back to the June obligation exactly");
        (await EosbProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Employee.Id }, CancellationToken.None))
            .Single().Remaining.Should().Be(15_000m);
        (await db.EosbProvisionSnapshots.Select(s => s.Period).ToListAsync()).Should().Equal("2026-06");
    }

    // ── 6. Lock retried: never provides twice ──────────────────────────────────────

    [Fact]
    public async Task LockRetry_DoesNotProvideTwice()
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

        (await ProvisionLinesAsync(db, jun)).Count(l => l.CreditAccount == Provision).Should().Be(1);
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(15_000m);
        (await db.EosbProvisionSnapshots.CountAsync()).Should().Be(1);
    }

    // ── 7. Settlement consumes the provision and releases what is left ─────────────

    private static async Task<EmployeeFinalSettlement> SeedPendingSettlementAsync(
        ZayraDbContext db, Fixture fx, decimal gratuity, string reason, Guid maker)
    {
        var lastDay = new DateOnly(2026, 9, 15);
        var offboarding = new EmployeeOffboarding
        {
            TenantId = fx.TenantId, EmployeeId = fx.Employee.Id, EmployeeCode = fx.Employee.EmployeeCode,
            EmployeeName = fx.Employee.FullName, SeparationType = reason, Status = "InProgress",
            NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, EmployeeId = fx.Employee.Id,
            EmployeeCode = fx.Employee.EmployeeCode, EmployeeName = fx.Employee.FullName, OffboardingId = offboarding.Id,
            LastWorkingDay = lastDay, ServiceStartDate = Joined, SettlementDueDate = lastDay.AddDays(7),
            TerminationReason = reason, ServiceYears = 3.2m, Currency = "SAR",
            GratuityAmount = gratuity, GrossPayable = gratuity, TotalDeductions = 0m, NetPayable = gratuity,
            WagesPaidByRunId = Guid.NewGuid(), WageBaseDeltaAmount = 0m,
            Status = FinalSettlementStatuses.PendingApproval,
            CreatedByUserId = maker, CreatedByName = "Maker", SubmittedByUserId = maker, SubmittedByName = "Maker",
            SubmittedAtUtc = DateTime.UtcNow,
        };
        db.EmployeeOffboardings.Add(offboarding);
        db.EmployeeFinalSettlements.Add(settlement);
        db.FinalSettlementLines.Add(new FinalSettlementLine
        {
            TenantId = fx.TenantId, SettlementId = settlement.Id,
            ComponentCode = FinalSettlementComponents.Gratuity, ComponentName = "End-of-service gratuity",
            LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource,
            Amount = gratuity, SortOrder = 0,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return settlement;
    }

    private static async Task ApproveSettlementAsync(ZayraDbContext db, Fixture fx, EmployeeFinalSettlement s)
    {
        var body = new ApproveFinalSettlementRequest(ConfirmTerminationReason: s.TerminationReason, AccrualDate: new DateOnly(2026, 9, 15));
        var result = await Payroll(db, fx.TenantId, Guid.NewGuid()).ApproveFinalSettlement(s.Id, body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((result as ObjectResult)?.Value));
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Settlement_AfterThreeMonths_ConsumesTheProvisionFirst_AndExpensesOnlyTheShortfall()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await LockMonthAsync(db, fx, 2026, 7);
        await LockMonthAsync(db, fx, 2026, 8);
        var provided = Art84(Joined, August, 10_000m);
        var award = Art84(Joined, new DateOnly(2026, 9, 15), 10_000m);
        award.Should().BeGreaterThan(provided);

        var s = await SeedPendingSettlementAsync(db, fx, award, "Termination", Guid.NewGuid());
        await ApproveSettlementAsync(db, fx, s);

        var accrual = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        accrual.Single(l => l.DebitAccount == Provision).Amount.Should().Be(provided, "the whole provision is consumed first");
        accrual.Single(l => l.DebitAccount == Expense).Amount.Should().Be(award - provided, "only the half-month since August is new expense");
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(0m, "the leaver's provision is gone from 2310");
        (await ExpenseNetAsync(db, fx.TenantId)).Should().Be(award, "total EOSB expense recognised = the award, exactly once");

        // A later month for the same company must not re-provide the settled leaver.
        var sep = await LockMonthAsync(db, fx, 2026, 9);
        (await ProvisionLinesAsync(db, sep)).Should().BeEmpty();
    }

    [Fact]
    public async Task Settlement_BelowTheProvision_ReleasesTheRemainderToExpense()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await LockMonthAsync(db, fx, 2026, 7);
        await LockMonthAsync(db, fx, 2026, 8);
        var provided = Art84(Joined, August, 10_000m);

        // An Art. 85 resignation (2–5 years ⇒ one third) — the provision was measured on the full award.
        var award = 5_300m;
        var s = await SeedPendingSettlementAsync(db, fx, award, "Resignation", Guid.NewGuid());
        await ApproveSettlementAsync(db, fx, s);

        var accrual = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == s.Id).ToListAsync();
        accrual.Where(l => l.DebitAccount == Provision).Sum(l => l.Amount).Should().Be(provided,
            "consumed (5,300) + released (the rest) = everything provided for the leaver");
        accrual.Single(l => l.CreditAccount == Expense).Amount.Should().Be(provided - award);
        accrual.Single(l => l.CreditAccount == Expense).Description.Should().StartWith(FinalSettlementGlDescriptions.ProvisionReleasePrefix);
        accrual.Where(l => l.DebitAccount != "").Sum(l => l.Amount)
            .Should().Be(accrual.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(0m);
        (await ExpenseNetAsync(db, fx.TenantId)).Should().Be(award, "the over-provision is reversed out of expense");
        (await EosbProvisionLedger.LoadPositionsAsync(db, fx.TenantId, new[] { fx.Employee.Id }, CancellationToken.None))
            .Single().Remaining.Should().Be(0m);
    }

    // ── 8. Actuarial true-up: strict maker ≠ checker ───────────────────────────────

    [Fact]
    public async Task TrueUp_IsPendingUntilASecondPersonApproves_ThenPostsToTheProvision()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        var maker = Guid.NewGuid();
        var checker = Guid.NewGuid();

        var created = await Gl(db, fx.TenantId, maker, "maker").RequestTrueUp(
            new EosbTrueUpRequest(fx.Company.Id, "2026-06", 1_250.50m, "Actuarial valuation FY2026 (discounting + salary growth)", "ACT-2026-06"),
            CancellationToken.None);
        created.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((created as ObjectResult)?.Value));
        var request = await db.GlAdjustmentRequests.AsNoTracking().SingleAsync();
        request.Status.Should().Be(GlAdjustmentStatuses.Pending);
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == request.Id)).Should().BeFalse("nothing posts on request");

        var self = await Gl(db, fx.TenantId, maker, "maker").Approve(request.Id, null, CancellationToken.None);
        self.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonSerializer.Serialize(((ObjectResult)self).Value).Should().Contain("GL_ADJUSTMENT_SOD");
        db.ChangeTracker.Clear();
        (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Status.Should().Be(GlAdjustmentStatuses.Pending);
        JsonSerializer.Serialize(((OkObjectResult)await Gl(db, fx.TenantId, maker).ListTrueUps(fx.Company.Id, CancellationToken.None)).Value)
            .Should().Contain("\"canApprove\":false", "the server tells the requester they may not approve");
        JsonSerializer.Serialize(((OkObjectResult)await Gl(db, fx.TenantId, checker).ListTrueUps(fx.Company.Id, CancellationToken.None)).Value)
            .Should().Contain("\"canApprove\":true");

        (await Gl(db, fx.TenantId, checker, "checker").Approve(request.Id, new GlAdjustmentDecisionRequest("agreed to report"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var posted = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == request.Id).ToListAsync();
        posted.Should().HaveCount(2);
        posted.Single(l => l.DebitAccount == Expense).Amount.Should().Be(1_250.50m);
        posted.Single(l => l.CreditAccount == Provision).EventType.Should().Be(GlEventTypes.EosbActuarialTrueUp);
        (await ProvisionBalanceAsync(db, fx.TenantId)).Should().Be(15_000m + 1_250.50m);
        (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Status.Should().Be(GlAdjustmentStatuses.Approved);
        (await db.AuditLogs.AsNoTracking().Select(a => a.Action).ToListAsync())
            .Should().Contain(new[] { "finance.gl.eosb_true_up.requested", "finance.gl.eosb_true_up.approved" });

        // A second approval of the same request is refused, and posts nothing more.
        (await Gl(db, fx.TenantId, Guid.NewGuid()).Approve(request.Id, null, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        (await db.FinanceGlEntries.CountAsync(x => x.SourceEntityId == request.Id)).Should().Be(2);

        // The panel reads the layer separately from the per-employee obligation.
        var panel = await Gl(db, fx.TenantId, checker).GetProvision(fx.Company.Id, CancellationToken.None);
        var json = JsonSerializer.Serialize(((OkObjectResult)panel).Value);
        json.Should().Contain("\"glBalance\":16250.5").And.Contain("\"trueUpLayer\":1250.5").And.Contain("\"enabled\":true");
    }

    [Fact]
    public async Task TrueUp_ThatWouldTakeTheProvisionNegative_IsRefusedAtApproval()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await LockMonthAsync(db, fx, 2026, 6);
        await Gl(db, fx.TenantId, Guid.NewGuid()).RequestTrueUp(
            new EosbTrueUpRequest(fx.Company.Id, "2026-06", -20_000m, "valuation lower", "ACT-1"), CancellationToken.None);
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;

        (await Gl(db, fx.TenantId, Guid.NewGuid()).Approve(id, null, CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == id)).Should().BeFalse();
    }

    [Fact]
    public void Planner_IsBalancedPerCompany_AndKeepsTheExpenseSideOffTheEmployeeSubLedger()
    {
        var company = Guid.NewGuid();
        var (lines, snapshots) = EosbProvisionPlanner.Build(
            Guid.NewGuid(), company, Guid.NewGuid(), "2026-07", July, "SAR", Expense, Provision,
            new[]
            {
                new EosbObligation(1, "A", 10_000m, 0m, true, 10_000m, 2m, "KSA-LaborLaw-Art84"),
                new EosbObligation(2, "B", 5_000m, 4_000m, false, 10_000m, 1m, "KSA-LaborLaw-Art84"),
                new EosbObligation(3, "C", 3_000m, 3_500m, false, 6_000m, 1m, "KSA-LaborLaw-Art84"),
                new EosbObligation(4, "D", 2_000m, 2_000m, false, 6_000m, 1m, "KSA-LaborLaw-Art84"),
            },
            null, "system", July);

        lines.Where(l => l.DebitAccount != "").Sum(l => l.Amount).Should().Be(lines.Where(l => l.CreditAccount != "").Sum(l => l.Amount));
        lines.Should().OnlyContain(l => l.CompanyId == company);
        lines.Where(l => l.DebitAccount == Expense || l.CreditAccount == Expense)
            .Should().OnlyContain(l => l.SourceEntityRef == "2026-07", "the expense side never carries an employee id");
        lines.Should().NotContain(l => l.SourceEntityRef == "4", "no movement, no line");
        snapshots.Should().HaveCount(4, "a no-movement month is still a measurement");
        snapshots.Single(s => s.EmployeeId == 3).Movement.Should().Be(-500m);
    }
}

// ── File-scoped doubles ───────────────────────────────────────────────────────

file static class _EpRules
{
    internal static readonly StubRuleReader Rules = PayComponentNetPayDefectTests.KsaRules();
}

file sealed class _EpScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _EpHttp : IHttpContextAccessor
{
    public _EpHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _EpKsaResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _EpKsaResolver(StubRuleReader rules) => _rules = rules;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaDeductionCalculator(_rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(_rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
