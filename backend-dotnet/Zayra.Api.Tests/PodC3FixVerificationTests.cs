using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-C3 — REGRESSION COVER FOR THE FIX PASS. One test per defect the independent SDE-test SME
/// reported, each written so it FAILS against the pre-fix code and passes after. Driven end-to-end
/// through the shipped endpoints, never against a calculator in isolation.
///
/// <list type="number">
///   <item><b>Arrears GL routing</b> (the one hard failure). <c>ARREARS_BASIC</c> was matched by the
///     seeded <c>Any</c> catch-all <c>EARN:OTHER</c> on every provisioned tenant, so a retro basic
///     increase debited 5099 Other Earnings. Fixed by routing an <c>ARREARS_*</c> code through the
///     driver of the component it settles — which is why the two tests here assert the account FOLLOWS
///     A TENANT'S OWN REMAP, something four extra seeded driver keys could not have done.</item>
///   <item><b>The B3→C3 receivable dead-end</b> (HIGH). A replacement whose net equalled the cash
///     already disbursed netted to zero, tripped the NON-OVERRIDABLE <c>ZERO_NET_WITH_GROSS</c> Error,
///     and could never be locked — so 1420 could never be relieved. The only exit restored the
///     receivable: a loop.</item>
///   <item><b>The auditor's surfaces</b> (MEDIUM): the payroll register grid and the admin payslip
///     PDF, neither of which showed the proration or the arrears the employee's own ESS payslip did.</item>
///   <item><b>Two misleading operator messages</b> (LOW).</item>
/// </list>
///
/// <para>Fixture: Saudi national, package 13,000 = basic 10,000 + housing 2,000 + transport 1,000.
/// KSA covered wage = basic + housing = 12,000 ⇒ EE 12,000 × 9.75% = 1,170.00, ER 12,000 × 11.75% =
/// 1,410.00. Every asserted figure is derived from those and stated in the assertion.</para>
/// </summary>
public class PodC3FixVerificationTests
{
    private static readonly Guid Maker   = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    private const decimal Basic     = 10_000m;
    private const decimal Housing   =  2_000m;
    private const decimal Transport =  1_000m;
    private const decimal Package   = Basic + Housing + Transport;   // 13,000
    private const decimal StatEe    = 1_170.00m;                     // (10,000 + 2,000) × 9.75%

    private const string BasicExpense  = "5001 - Basic Salary Expense";
    private const string OtherEarnings = "5099 - Other Earnings";
    private const string EmpOverpaid   = "1420 - Employee Overpayment Receivable";

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

    private static PayrollController Payroll(
        ZayraDbContext db, Guid tenantId, Guid userId, ILetterService? letters = null)
    {
        var http = Ctx(tenantId, userId);
        return new PayrollController(
            db, new _C3fScope(), new _C3fHttp(http), new _C3fNotifications(),
            new _C3fPackResolver(), _C3fRules.Rules, letters ?? new _C3fLetters(), new _C3fStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed record Fixture(Guid TenantId, Guid CompanyId, Employee Emp, Employee Peer, Guid StructureId);

    private static async Task<Fixture> Seed(ZayraDbContext db, Guid tenantId, bool seedGlDrivers = false)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "C3 Fix KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "C3F", Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();

        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "F001", FullName = "Noura Al-Qahtani",
            Status = "Active", JoiningDate = new DateTime(2022, 1, 1),
            WorkEmail = "noura@c3f.test", Nationality = "Saudi", ContractType = "Indefinite",
        };
        var peer = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "F002", FullName = "Omar Al-Dosari",
            Status = "Active", JoiningDate = new DateTime(2022, 1, 1),
            WorkEmail = "omar@c3f.test", Nationality = "Saudi", ContractType = "Indefinite",
        };
        db.Employees.AddRange(emp, peer);
        await db.SaveChangesAsync();

        foreach (var e in new[] { emp, peer })
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
        return new Fixture(tenantId, company.Id, emp, peer, structure.Id);
    }

    private static async Task<PayrollRun> AddRunAsync(
        ZayraDbContext db, Fixture f, int year, int month, string runType = PayrollRunTypes.Regular)
    {
        var run = new PayrollRun
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, Year = year, Month = month,
            Status = "Draft", RunType = runType, IncludesRecurringPay = true,
            SettlesArrears = true, CreatedByUserId = Maker,
        };
        db.PayrollRuns.Add(run);
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

    private static async Task AssertLedgerBalances(ZayraDbContext db, Guid tenantId, string because)
    {
        var rows = await Ledger(db, tenantId);
        rows.Should().NotBeEmpty("a locked run must have posted a journal");
        rows.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount)
            .Should().Be(rows.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount), because);
        GlControlAccounts.FindSignViolations(rows).Should().BeEmpty(because);
    }

    private static Task<PayrollSlip> Slip(ZayraDbContext db, Guid runId, int employeeId) =>
        db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == runId && s.EmployeeId == employeeId);

    private static Task<List<PayrollValidationResult>> Validations(ZayraDbContext db, Guid runId) =>
        db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == runId).ToListAsync();

    /// <summary>Runs April and May at the original package, locks them, keys a backdated increment
    /// (basic 10,000 → 12,000) effective 1 April, and returns the unprocessed June run.</summary>
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
            TenantId = f.TenantId, EmployeeId = f.Emp.Id, SalaryStructureId = f.StructureId,
            BasicSalary = 12_000m, HousingAllowance = Housing, TransportAllowance = Transport,
            EffectiveDate = new DateOnly(2026, 4, 1), IsActive = true,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await AddRunAsync(db, f, 2026, 6);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (1) THE ARREARS EXPENSE LANDS IN THE COMPONENT'S OWN ACCOUNT — AND FOLLOWS A REMAP OF IT
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The reported defect, at its root. <c>EARN:BASIC</c> is seeded <c>Exact "BASIC"</c> so it does not
    /// match <c>ARREARS_BASIC</c>; <c>EARN:OTHER</c> is seeded <c>Any</c> and did. Before the fix a retro
    /// basic increase debited 5099 on every tenant with gl_drivers rows — i.e. every provisioned tenant.
    /// </summary>
    [Fact]
    public async Task RetroBasicArrears_DebitBasicSalaryExpense_OnADriverSeededTenant()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, seedGlDrivers: true);
        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessApproveLock(db, tid, june.Id);

        var arrears = (await Ledger(db, tid))
            .Where(l => l.SourceEntityId == june.Id && l.Description.Contains(PayrollArrearsComponents.Basic))
            .ToList();
        arrears.Should().ContainSingle("the 4,000 of retro BASIC posts as one debit group");
        arrears[0].Amount.Should().Be(4_000m, "April 2,000 + May 2,000");
        arrears[0].DebitAccount.Should().Be(BasicExpense,
            "arrears are the SAME expense as the component they settle, just paid late");
        arrears[0].DebitAccount.Should().NotBe(OtherEarnings,
            "5099 would move a retro basic-salary increase out of Basic Salary Expense and distort every cost report");
        await AssertLedgerBalances(db, tid, "routing must not disturb the journal");
    }

    /// <summary>
    /// WHY THE FIX IS NOT FOUR MORE SEEDED DRIVER KEYS. A tenant who has remapped <c>EARN:BASIC</c> to
    /// their own chart of accounts must see retro basic land THERE too. Seeding
    /// <c>EARN:ARREARS_BASIC → 5001</c> would have satisfied the failing assertion above while
    /// reintroducing the very divergence it describes, in a new disguise: ordinary basic in the tenant's
    /// account, retro basic in the shipped default. Routing through the SOURCE component's driver is
    /// correct by construction.
    /// </summary>
    [Fact]
    public async Task RetroBasicArrears_FollowTheTenantsOwnRemapOfBasicSalaryExpense()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, seedGlDrivers: true);

        // Remap EARN:BASIC the way the Finance → GL screen does: repoint the tenant-default mapping
        // GlDriverSeeder already created (adding a SECOND row for the same driver key is not a remap —
        // GlAccountResolver groups by driver key and one of the two would win arbitrarily).
        var custom = new GlAccount
        {
            TenantId = tid, CompanyId = null, Code = "6001", Name = "Payroll — Basic (Group Chart)",
            AccountType = "Expense", IsActive = true,
        };
        db.GlAccounts.Add(custom);
        (await db.GlAccountMappings.FirstAsync(m => m.TenantId == tid && m.DriverKey == "EARN:BASIC"))
            .AccountId = custom.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessApproveLock(db, tid, june.Id);

        var lines = (await Ledger(db, tid)).Where(l => l.SourceEntityId == june.Id).ToList();
        lines.Single(l => l.Description.Contains(PayrollArrearsComponents.Basic)).DebitAccount
            .Should().Be("6001 - Payroll — Basic (Group Chart)",
                "retro basic must follow the SAME remap ordinary basic follows");
        lines.Single(l => l.Description == "Payroll earning: BASIC").DebitAccount
            .Should().Be("6001 - Payroll — Basic (Group Chart)",
                "…and the two must be the same account, which is the whole point");
        await AssertLedgerBalances(db, tid, "a remap must not disturb the journal");
    }

    /// <summary>
    /// The escape hatch survives. A tenant who DELIBERATELY authors a driver claiming the arrears code
    /// (a shipped capability — an Exact/Prefix/Suffix custom Earning driver) still wins: the fix only
    /// takes precedence over the <c>Any</c> catch-all, never over an explicit choice.
    /// </summary>
    [Fact]
    public async Task ATenantAuthoredArrearsDriver_StillBeatsTheComponentItSettles()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, seedGlDrivers: true);
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = "EARN:RETRO_BASIC", Label = "Earning — Retro Basic",
            Category = GlDriverCategories.Earning, PostingSide = "DR", AccountType = "Expense",
            DefaultCode = "5011", DefaultName = "Retro Pay Expense",
            MatchSource = null, MatchMode = GlDriverMatchModes.Exact,
            MatchComponentCode = PayrollArrearsComponents.Basic,
            IsSystem = false, IsActive = true, SortOrder = 5,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessApproveLock(db, tid, june.Id);

        (await Ledger(db, tid))
            .Single(l => l.SourceEntityId == june.Id && l.Description.Contains(PayrollArrearsComponents.Basic))
            .DebitAccount.Should().Be("5011 - Retro Pay Expense",
                "an explicit tenant driver on the arrears code is a decision, and outranks the default");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (2) THE B3 → C3 RECEIVABLE HANDOFF NO LONGER DEAD-ENDS
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ORDINARY CORRECTION: wrong month processed, salaries unchanged. The void recognises a 1420
    /// receivable equal to the net already in the employee's bank account; the replacement re-pays the
    /// SAME period, so its net after recovery is exactly zero.
    ///
    /// <para>Before the fix that tripped <c>ZERO_NET_WITH_GROSS</c> — an Error, and on the
    /// <c>NonOverridable</c> list — so Approve 422'd, Lock refused, and the only exit (void the
    /// replacement) RESTORED the receivable. 1420 could never be relieved. The zero is not an
    /// over-deduction: it is a refusal to pay the same month twice.</para>
    /// </summary>
    [Fact]
    public async Task ReplacementRunWhoseNetEqualsTheDisbursedCash_LocksAndClears1420ToZero()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await AddRunAsync(db, f, 2026, 6);
        await ProcessApproveLock(db, tid, june.Id);

        var ctrl = Payroll(db, tid, Checker);
        (await ctrl.CreatePaymentBatch(june.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        db.ChangeTracker.Clear();
        var batch = await db.PayrollPaymentBatches.FirstAsync(b => b.PayrollRunId == june.Id);
        batch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).SettlePaymentBatch(
            batch.Id, new SettlePaymentBatchRequest("BANKREF-FIX", null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker).VoidRun(
            june.Id, new PayrollDecisionRequest("wrong cost centre — salaries unchanged"), CancellationToken.None,
            settlementDisposition: PayrollVoidDispositions.FundsDisbursed, settlementReference: null,
            remittanceDisposition: PayrollVoidDispositions.FundsRecalled, remittanceReference: "REFUND-FIX"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        GlControlAccounts.Balance(await Ledger(db, tid), EmpOverpaid)
            .Should().Be(2 * (Package - StatEe), "two employees, each holding 11,830 of disbursed net");

        // The replacement pays the IDENTICAL package — no raise papering over the recovery.
        var created = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Replacement, june.Id,
                NetsPriorReceivable: true),
            CancellationToken.None);
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();

        await ProcessApproveLock(db, tid, replacement.Id);

        var slip = await Slip(db, replacement.Id, f.Emp.Id);
        slip.GrossSalary.Should().Be(Package, "the replacement re-pays the same month at the same package");
        slip.NetSalary.Should().Be(0m, "11,830 already disbursed − 11,830 recovered: nothing further is due");

        var results = await Validations(db, replacement.Id);
        results.Should().Contain(r => r.Code == "ZERO_NET_FROM_RECEIVABLE_RECOVERY"
                                   && r.Severity == "Warning" && r.EmployeeId == f.Emp.Id,
            "the zero must be EXPLAINED, not merely permitted");
        results.Should().NotContain(r => r.Code == "ZERO_NET_WITH_GROSS",
            "a recovery is a non-duplication of payment, not an over-deduction");

        GlControlAccounts.Balance(await Ledger(db, tid), EmpOverpaid).Should().Be(0m,
            "B3's ageing 1420 balance is what C3 was handed; this is it closed");
        (await db.PayrollEmployeeReceivables.AsNoTracking().Where(r => r.TenantId == tid).ToListAsync())
            .Should().OnlyContain(r => r.Status == PayrollReceivableStatuses.Recovered
                                    && r.RecoveredByRunId == replacement.Id);
        await AssertLedgerBalances(db, tid, "the recovery must credit an ASSET, never a phantom liability");
    }

    /// <summary>
    /// THE CARVE-OUT IS NARROW. A genuine over-deduction that happens to sit on a slip alongside a
    /// recovery still raises the non-overridable Error — the guard is that the NON-recovery deductions
    /// on their own leave a non-negative net, so only the recovery may be what took the slip to zero.
    /// </summary>
    [Fact]
    public void AnOverDeductionAlongsideARecovery_StillRaisesTheNonOverridableError()
    {
        var tid = Guid.NewGuid();
        var run = new PayrollRun
        {
            TenantId = tid, Year = 2026, Month = 6, Status = "Processed",
            RunType = PayrollRunTypes.Replacement, IncludesRecurringPay = true,
        };
        var company = new Company { TenantId = tid, LegalNameEn = "X", CountryCode = "SAU", DefaultCurrency = "SAR" };

        // Gross 10,000; 9,000 of ordinary deductions PLUS a 2,000 recovery = 11,000 ⇒ net clamped to 0.
        // Net before recovery is −1,000: something other than the recovery over-deducted.
        var slip = new PayrollSlip
        {
            TenantId = tid, RunId = run.Id, EmployeeId = 1, EmployeeCode = "E1", EmployeeName = "E One",
            GrossSalary = 10_000m, Deductions = 11_000m, NetSalary = 0m, BasicSalary = 10_000m,
        };
        var deductions = new List<PayrollDeduction>
        {
            new() { TenantId = tid, PayrollRunId = run.Id, EmployeeId = 1, ComponentCode = "FIXED_DEDUCTION",
                    ComponentName = "Fixed", Amount = 9_000m, Source = "Manual" },
            new() { TenantId = tid, PayrollRunId = run.Id, EmployeeId = 1,
                    ComponentCode = PayrollRecoveryComponents.ReceivableRecovery,
                    ComponentName = PayrollRecoveryComponents.ReceivableRecoveryName,
                    Amount = 2_000m, Source = PayrollRecoveryComponents.RecoverySource },
        };

        var results = PayrollValidationEngine.Run(new PayrollValidationContext(
            run, new[] { slip }, Array.Empty<Employee>(), Array.Empty<EmployeeSalaryStructure>(),
            Array.Empty<EmployeePayrollProfile>(), deductions, Array.Empty<PayrollEarning>(), company));

        results.Should().Contain(r => r.Code == "ZERO_NET_WITH_GROSS" && r.Severity == "Error",
            "the recovery does not account for the gap, so this IS an over-deduction");
        results.Should().NotContain(r => r.Code == "ZERO_NET_FROM_RECEIVABLE_RECOVERY");
        PayrollValidationOverridePolicy.IsOverridable("ZERO_NET_WITH_GROSS").Should().BeFalse(
            "the genuine case keeps its non-overridable status verbatim");
    }

    /// <summary>
    /// [FLAG-COMPLIANCE-KSA] SAME-PERIOD vs CROSS-PERIOD RECOVERY. Recovering inside the period the cash
    /// was disbursed for is not a deduction from wages at all — it is declining to pay the same month
    /// twice — so it carries no flag. The test above proves that; this one proves the OTHER half: a
    /// recovery taken out of a LATER month's wages IS a deduction and is flagged for sign-off.
    /// </summary>
    [Fact]
    public async Task RecoveringAPriorMonthsOverpaymentOutOfALaterWage_IsFlaggedForKsaSignOff()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);

        // A May receivable (recognised in period 2026-05) recovered by the JUNE run.
        db.PayrollEmployeeReceivables.Add(new PayrollEmployeeReceivable
        {
            TenantId = tid, CompanyId = f.CompanyId, EmployeeId = f.Emp.Id, EmployeeCode = f.Emp.EmployeeCode,
            SourceRunId = Guid.NewGuid(), EventType = "NetSettlementReclass", Period = "2026-05",
            Amount = 1_500m, RecoveredAmount = 0m, Status = PayrollReceivableStatuses.Outstanding,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var june = await AddRunAsync(db, f, 2026, 6);
        (await db.PayrollRuns.FirstAsync(r => r.Id == june.Id)).NetsPriorReceivable = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ProcessAsync(db, tid, june.Id);

        var results = await Validations(db, june.Id);
        var flag = results.SingleOrDefault(r => r.Code == "WARN_RECEIVABLE_RECOVERY_CROSS_PERIOD");
        flag.Should().NotBeNull("recovering a PRIOR period's overpayment out of this month's wage is a wage deduction");
        flag!.EmployeeId.Should().Be(f.Emp.Id);
        flag.Message.Should().Contain("2026-05", "the operator must be told which period the money came from")
                    .And.Contain("[FLAG-COMPLIANCE-KSA]");
        results.Should().NotContain(r => r.Code == "WARN_RECEIVABLE_RECOVERY_CROSS_PERIOD" && r.EmployeeId == f.Peer.Id,
            "the peer has no receivable, so nothing about them needs sign-off");

        (await Slip(db, june.Id, f.Emp.Id)).NetSalary.Should().Be(Package - StatEe - 1_500m,
            "13,000 − 1,170 statutory − 1,500 recovered");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (3) THE AUDITOR'S SURFACES EXPLAIN THE NUMBER, NOT ONLY THE EMPLOYEE'S
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The payroll register grid (GET runs/{id}/slips) projected none of the proration witnesses, so a
    /// wage that halved looked like an unexplained halving. Every field here is read straight off the
    /// persisted slip, so the grid can never disagree with the payslip.
    /// </summary>
    [Fact]
    public async Task ThePayrollRegisterGrid_ShowsDaysPaidBasisFactorAndArrears()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Emp.Id)).JoiningDate = new DateTime(2026, 6, 16);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var june = await AddRunAsync(db, f, 2026, 6);
        await ProcessAsync(db, tid, june.Id);

        var page = (await Payroll(db, tid, Maker).Slips(june.Id, 1, 50, CancellationToken.None))
            .As<OkObjectResult>().Value.As<PagedResult<PayrollSlipDto>>();

        var joiner = page.Items.Single(s => s.EmployeeId == f.Emp.Id);
        joiner.PaidDays.Should().Be(15, "joined 16 June — 16→30 inclusive");
        joiner.ProrationDenominatorDays.Should().Be(30);
        joiner.ProrationBasis.Should().Be(ProrationBases.Calendar30);
        joiner.ProrationFactor.Should().Be(0.5m);
        joiner.PaidFromDate.Should().Be(new DateOnly(2026, 6, 16));
        joiner.PaidToDate.Should().Be(new DateOnly(2026, 6, 30));
        joiner.BasicSalary.Should().Be(5_000m, "the grid's money and its explanation must agree");
        joiner.ArrearsAmount.Should().Be(0m);
        joiner.IsFinalWageMonth.Should().BeFalse();

        var peer = page.Items.Single(s => s.EmployeeId == f.Peer.Id);
        peer.ProrationFactor.Should().Be(1m, "an untouched employee must read as a full month");
        peer.PaidDays.Should().Be(30);
        peer.BasicSalary.Should().Be(Basic);
    }

    /// <summary>
    /// The ADMIN payslip PDF built its line items from the slip HEADER columns, so arrears folded
    /// silently into "Other Allowances" and the days/basis narrative never appeared — requirement 7 was
    /// met for the employee (ESS reads the stored PayslipComponent rows) and not for the auditor.
    /// </summary>
    [Fact]
    public async Task TheAdminPayslipPdf_ItemisesArrearsPerCoveredPeriod_AndCarriesTheProrationNarrative()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Emp.Id)).JoiningDate = new DateTime(2026, 3, 16);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var june = await SeedBackdatedIncrementAsync(db, f);
        await ProcessAsync(db, tid, june.Id);
        (await Payroll(db, tid, Maker).GeneratePayslips(june.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payslipId = (await db.Payslips.AsNoTracking()
            .FirstAsync(p => p.PayrollRunId == june.Id && p.EmployeeId == f.Emp.Id)).Id;
        var capture = new _C3fCapturingLetters();
        (await Payroll(db, tid, Maker, capture).DownloadSlipPdf(payslipId, CancellationToken.None))
            .Should().BeOfType<FileContentResult>();

        capture.Last.Should().NotBeNull();
        var names = capture.Last!.Items.Select(i => i.Name).ToList();
        names.Should().Contain("Arrears — Basic salary (2026-04)")
             .And.Contain("Arrears — Basic salary (2026-05)",
                 "an auditor must see WHICH months the retro amount covers, not one opaque bucket");
        capture.Last.Items.Where(i => i.Name.StartsWith("Arrears —")).Sum(i => i.Amount).Should().Be(4_000m);

        // The gross on the page must still be the slip's gross — arrears are LIFTED OUT of Other
        // Allowances, not added on top of an unchanged bucket.
        var slip = await Slip(db, june.Id, f.Emp.Id);
        capture.Last.Items.Where(i => i.Type == "Earning").Sum(i => i.Amount)
            .Should().Be(slip.GrossSalary, "itemising must not inflate the gross by the arrears amount");

        // …and the days/basis narrative, on a month that IS prorated (March, the joining month).
        var march = await AddRunAsync(db, f, 2026, 3);
        await ProcessAsync(db, tid, march.Id);
        (await Payroll(db, tid, Maker).GeneratePayslips(march.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var marchPayslipId = (await db.Payslips.AsNoTracking()
            .FirstAsync(p => p.PayrollRunId == march.Id && p.EmployeeId == f.Emp.Id)).Id;
        (await Payroll(db, tid, Maker, capture).DownloadSlipPdf(marchPayslipId, CancellationToken.None))
            .Should().BeOfType<FileContentResult>();

        var basicLabel = capture.Last!.Items.Single(i => i.Name.StartsWith("Basic Salary")).Name;
        basicLabel.Should().Contain("16/30 days").And.Contain("30-day month").And.Contain("joined 2026-03-16",
            "the auditor's PDF must explain a halved wage exactly as the employee's does");
    }

    /// <summary>An unprorated month must be byte-identical on the PDF — no note, no arrears lines. This
    /// is the ~55-tenant bar for a change to a document every tenant downloads.</summary>
    [Fact]
    public async Task AnOrdinaryFullMonthPayslipPdf_IsUnchangedByThisPod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var june = await AddRunAsync(db, f, 2026, 6);
        await ProcessAsync(db, tid, june.Id);
        (await Payroll(db, tid, Maker).GeneratePayslips(june.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payslipId = (await db.Payslips.AsNoTracking()
            .FirstAsync(p => p.PayrollRunId == june.Id && p.EmployeeId == f.Emp.Id)).Id;
        var capture = new _C3fCapturingLetters();
        (await Payroll(db, tid, Maker, capture).DownloadSlipPdf(payslipId, CancellationToken.None))
            .Should().BeOfType<FileContentResult>();

        var items = capture.Last!.Items;
        items.Select(i => i.Name).Should().Contain("Basic Salary", "no proration note on a full month")
             .And.NotContain(l => l.StartsWith("Arrears"));
        items.Single(i => i.Name == "Basic Salary").Amount.Should().Be(Basic);
        items.Single(i => i.Name == "Housing Allowance").Amount.Should().Be(Housing);
        items.Single(i => i.Name == "Transport Allowance").Amount.Should().Be(Transport);
        items.Single(i => i.Type == "Net").Amount.Should().Be(Package - StatEe);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (4) THE OPERATOR MESSAGES SAY WHAT ACTUALLY HAPPENED
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A post-period joiner was reported with "last day 2026-06-30" — the PERIOD END, dressed up as a
    /// real last working day, about someone who had simply not started yet. The dedicated branch that
    /// said so correctly was unreachable, because <c>paidTo &lt; paidFrom</c> is already true for this
    /// case (ProrationCalculator.cs:192-193).
    /// </summary>
    [Fact]
    public async Task APostPeriodJoinersExclusion_NamesTheJoiningDate_NotAFabricatedLastWorkingDay()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        (await db.Employees.FirstAsync(e => e.Id == f.Emp.Id)).JoiningDate = new DateTime(2026, 7, 2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var june = await AddRunAsync(db, f, 2026, 6);
        await ProcessAsync(db, tid, june.Id);

        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == june.Id && s.EmployeeId == f.Emp.Id))
            .Should().BeFalse("nothing is owed for a month before the employee joined");

        var msg = (await Validations(db, june.Id))
            .Single(r => r.Code == "EMPLOYEE_SELECTION_NOT_ELIGIBLE" && r.EmployeeId == f.Emp.Id).Message;
        msg.Should().Contain("2026-07-02", "the reason must name the date that caused it")
           .And.Contain("Employment window is empty for 2026-06")
           .And.Contain("after this period ends");
        msg.Should().NotContain("last day 2026-06-30",
            "the period end is not a last working day, and calling it one sends operators to the offboarding record");
        msg.Should().NotContain("was named in this run's population selector",
            "this run is AllEligible — no selector row exists, so the warning must not assert one does");
    }
}

// ── File-scoped service stubs ────────────────────────────────────────────────────────────────────

file static class _C3fRules
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

file sealed class _C3fScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(
        ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _C3fHttp : IHttpContextAccessor
{
    public _C3fHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _C3fNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct)
        => Task.CompletedTask;
}

file sealed class _C3fPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_C3fRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _C3fLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

/// <summary>Captures the PayslipData handed to the renderer — the only way to assert what a PDF SAYS
/// without parsing a PDF.</summary>
file sealed class _C3fCapturingLetters : ILetterService
{
    public PayslipData? Last { get; private set; }
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default)
    {
        Last = d;
        return Task.FromResult(new byte[] { 1 });
    }
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _C3fStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
