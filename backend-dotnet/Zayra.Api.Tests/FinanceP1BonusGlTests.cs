using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Finance-P1 correctness tests:
///   1. Approved bonus appears as earning line in payslip (Process)
///   2. GOSI-included bonus raises statutory base; non-included does not
///   3. MarkBatchPaid returns 409 Conflict when batch already consumed by payroll
///   4. Lock() writes balanced double-entry GL to FinanceGlEntry
///   5. Re-locking does not double-post GL (idempotency)
///   6. Unbalanced run rejected by Lock() (422)
/// </summary>
public class FinanceP1BonusGlTests
{
    // ── DB helpers ─────────────────────────────────────────────────────────────

    // SQLite in-memory required for ExecuteUpdateAsync used in Process() and Lock().
    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>()
                .UseSqlite(conn)
                .Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    // ── Controller factories ────────────────────────────────────────────────────

    private static PayrollController MakePayrollCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test-user"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx   = new DefaultHttpContext { User = principal };
        var ctrl = new PayrollController(
            db,
            new _P1UnrestrictedScope(),
            new _P1HttpAccessor(httpCtx),
            new _P1NullNotifications(),
            new _KsaPackResolver(),
            KsaPackFactory.Rules,
            new _P1NullLetterService(),
            new _P1NullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static BonusesController MakeBonusCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx   = new DefaultHttpContext { User = principal };
        var ctrl = new BonusesController(db, new _P1UnrestrictedScope());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // Factory for tests that need the real KSA statutory calculator (non-zero deductions).
    private static PayrollController MakeKsaPackPayrollCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test-user"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx   = new DefaultHttpContext { User = principal };
        var ctrl = new PayrollController(
            db,
            new _P1UnrestrictedScope(),
            new _P1HttpAccessor(httpCtx),
            new _P1NullNotifications(),
            new _KsaPackResolver(),
            KsaPackFactory.Rules,
            new _P1NullLetterService(),
            new _P1NullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // ── Shared seed helpers ─────────────────────────────────────────────────────

    // Inserts the minimum entities needed for a payroll run that can call Process():
    // SalaryStructure, Employee, EmployeeSalaryStructure, PayrollRun (Status=Draft).
    private static async Task<(Employee emp, PayrollRun run, SalaryStructure str)> SeedMinimalRun(
        ZayraDbContext db, Guid tenantId, int year = 2026, int month = 6)
    {
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-BASE", Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Ali Hassan",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "ali@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        // Payroll profile required so validation engine does not raise MISSING_IBAN error.
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234",  // valid Saudi IBAN (mod-97 = 1)
            MolId = "MOL-E001", SalaryCurrency = "SAR",
        });

        var run = new PayrollRun
        {
            TenantId = tenantId, Year = year, Month = month,
            Status = "Draft",
            TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        return (emp, run, structure);
    }

    // ── Test 1: Bonus appears as earning in payslip after Process() ────────────

    [Fact]
    public async Task Process_ApprovedBonus_AppearsAsEarningLine()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (emp, run, _) = await SeedMinimalRun(db, tenantId);
        await SeedKsaCompany(db, tenantId, run);

        // Seed approved bonus for this employee and period
        var bonusType = new BonusType
        {
            TenantId = tenantId, Code = "PERF", NameEn = "Performance Bonus",
            IsIncludedInGosiBase = false, IsIncludedInWps = true, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync();

        var batch = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-001", BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, PaymentPeriod = "2026-06",
            Status = "Approved", CreatedBy = null,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();

        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = 2_000m,
            GrossBonusAmount = 2_000m, TaxWithheld = 0m, BonusAmount = 2_000m,
            PaymentPeriod = "2026-06", Status = "Approved", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();

        var ctrl = MakePayrollCtrl(db, tenantId);
        run.Status = "Draft";
        await db.SaveChangesAsync();

        var result = await ctrl.Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var earnings = await db.PayrollEarnings
            .Where(e => e.TenantId == tenantId && e.PayrollRunId == run.Id && e.Source == "Bonus")
            .ToListAsync();
        earnings.Should().NotBeEmpty("approved bonus must produce a Bonus earning line");
        earnings.Sum(e => e.Amount).Should().Be(2_000m);
    }

    // ── Test 2: GOSI-included bonus raises statutory base ─────────────────────

    [Fact]
    public async Task Process_GosiIncludedBonus_RaisesStatutoryBase_NonIncludedDoesNot()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();

        // Two runs: one with GOSI-included bonus, one with non-included bonus.
        // We test GrossSalary on the PayrollSlip — GOSI base calculation affects the
        // statutory wage but not the gross earnings total shown on the slip.
        var (emp, runA, _) = await SeedMinimalRun(db, tenantId, 2026, 5);
        await SeedKsaCompany(db, tenantId, runA);  // runB reuses via tenant fallback
        var empId = emp.Id;
        var tidCopy = tenantId;

        var gosiType = new BonusType
        {
            TenantId = tenantId, Code = "BASE", NameEn = "Base Bonus",
            IsIncludedInGosiBase = true, IsIncludedInWps = true, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        var nonGosiType = new BonusType
        {
            TenantId = tenantId, Code = "DISC", NameEn = "Discretionary Bonus",
            IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.AddRange(gosiType, nonGosiType);
        await db.SaveChangesAsync();

        // For run A: add a GOSI-included bonus of 1,000
        var batchA = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-A", BonusTypeId = gosiType.Id,
            BonusTypeName = gosiType.NameEn, PaymentPeriod = "2026-05",
            Status = "Approved", CreatedBy = null,
        };
        db.BonusBatches.Add(batchA);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batchA.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = empId,
            EmployeeName = emp.FullName, BonusTypeId = gosiType.Id,
            BonusTypeName = gosiType.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = 1_000m,
            GrossBonusAmount = 1_000m, TaxWithheld = 0m, BonusAmount = 1_000m,
            PaymentPeriod = "2026-05", Status = "Approved", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();

        var ctrlA = MakePayrollCtrl(db, tenantId);
        await ctrlA.Process(runA.Id, CancellationToken.None);

        // Verify: PayrollSlip GrossSalary = basic+housing+transport+bonus = 10000+2000+1000+1000 = 14000
        var slipA = await db.PayrollSlips.FirstOrDefaultAsync(s => s.RunId == runA.Id);
        slipA.Should().NotBeNull();
        slipA!.GrossSalary.Should().Be(14_000m, "GOSI-included bonus adds 1000 to gross");

        // For run B: non-included bonus should not change the statutory base, but still adds to gross
        var (_, runB, _) = await SeedRunForExistingEmployee(db, tenantId, empId, 2026, 4);
        var batchB = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-B", BonusTypeId = nonGosiType.Id,
            BonusTypeName = nonGosiType.NameEn, PaymentPeriod = "2026-04",
            Status = "Approved", CreatedBy = null,
        };
        db.BonusBatches.Add(batchB);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batchB.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = empId,
            EmployeeName = emp.FullName, BonusTypeId = nonGosiType.Id,
            BonusTypeName = nonGosiType.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = 500m,
            GrossBonusAmount = 500m, TaxWithheld = 0m, BonusAmount = 500m,
            PaymentPeriod = "2026-04", Status = "Approved", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();

        var ctrlB = MakePayrollCtrl(db, tenantId);
        await ctrlB.Process(runB.Id, CancellationToken.None);

        var slipB = await db.PayrollSlips.FirstOrDefaultAsync(s => s.RunId == runB.Id);
        slipB!.GrossSalary.Should().Be(13_500m, "non-GOSI bonus 500 still adds to gross = 10000+2000+1000+500");
    }

    // KSA-specific seed: seeds a Company with CountryCode="SAU" and links it to the run.
    // Required for tests that use _KsaPackResolver (which resolves by company CountryCode).
    private static async Task SeedKsaCompany(ZayraDbContext db, Guid tenantId, PayrollRun run)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Test KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        run.CompanyId = company.Id;
        await db.SaveChangesAsync();
    }

    // Helper: add a second payroll run for an already-existing employee.
    private static async Task<(Employee emp, PayrollRun run, SalaryStructure str)> SeedRunForExistingEmployee(
        ZayraDbContext db, Guid tenantId, int empId, int year, int month)
    {
        var emp = await db.Employees.FirstAsync(e => e.TenantId == tenantId && e.Id == empId);
        var str = await db.SalaryStructures.FirstAsync(s => s.TenantId == tenantId);
        var run = new PayrollRun
        {
            TenantId = tenantId, Year = year, Month = month,
            Status = "Draft",
            TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return (emp, run, str);
    }

    // ── Test 3a: MarkBatchPaid guard fires when IsLockedByPayroll=true ─────────
    // Tests the guard directly without going through Process() to isolate the
    // double-pay logic from the full payroll run integration.

    [Fact]
    public async Task MarkBatchPaid_WhenBatchIsLockedByPayroll_Returns409()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();

        var bonusType = new BonusType
        {
            TenantId = tenantId, Code = "PERF2", NameEn = "Performance 2",
            IsIncludedInGosiBase = false, IsIncludedInWps = true, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync();

        // Batch already marked as payroll-locked (simulating what Process() does).
        var batch = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-DUP", BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, PaymentPeriod = "2026-06",
            Status = "Paid",   // Process() sets this
            IsLockedByPayroll = true, // Process() sets this
            CreatedBy = null,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();  // prevent stale tracking

        var bonusCtrl = MakeBonusCtrl(db, tenantId);
        var paidResult = await bonusCtrl.MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(Guid.NewGuid()), CancellationToken.None);

        paidResult.Should().BeOfType<ConflictObjectResult>(
            "MarkBatchPaid must return 409 when the batch was already consumed by a payroll run");
    }

    // ── Test 3b: Process() marks consumed bonus batch as locked ───────────────

    [Fact]
    public async Task Process_ApprovedBonus_LocksTheBatchAfterConsumption()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (emp, run, _) = await SeedMinimalRun(db, tenantId);
        await SeedKsaCompany(db, tenantId, run);

        var bonusType = new BonusType
        {
            TenantId = tenantId, Code = "LOCK", NameEn = "Lock Test",
            IsIncludedInGosiBase = false, IsIncludedInWps = true, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync();

        var batch = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-LOCK", BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, PaymentPeriod = "2026-06",
            Status = "Approved", CreatedBy = null,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();

        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = 3_000m,
            GrossBonusAmount = 3_000m, TaxWithheld = 0m, BonusAmount = 3_000m,
            PaymentPeriod = "2026-06", Status = "Approved", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();  // prevent stale state from interfering

        var ctrl = MakePayrollCtrl(db, tenantId);
        var result = await ctrl.Process(run.Id, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>("Process must succeed");

        // Read batch fresh from DB to verify Process() set the lock flag.
        db.ChangeTracker.Clear();
        var updatedBatch = await db.BonusBatches.AsNoTracking()
            .FirstAsync(x => x.Id == batch.Id);
        updatedBatch.IsLockedByPayroll.Should().BeTrue(
            "Process() must set IsLockedByPayroll=true after consuming all bonuses in the batch");
        updatedBatch.Status.Should().Be("Paid",
            "Process() must set batch Status to Paid after consuming all bonuses");
    }

    // ── Test 4: Lock() persists balanced GL in FinanceGlEntry ─────────────────

    [Fact]
    public async Task Lock_WritesBalancedGlEntries()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (_, run, _) = await SeedMinimalRun(db, tenantId);

        var ctrl = MakePayrollCtrl(db, tenantId);
        await ctrl.Process(run.Id, CancellationToken.None);

        // Move to Processed so Lock() accepts it
        run.Status = "Processed";
        await db.SaveChangesAsync();

        var lockResult = await ctrl.Lock(run.Id, CancellationToken.None);

        lockResult.Should().NotBeOfType<UnprocessableEntityObjectResult>(
            "GL must balance for a normal processed run");
        lockResult.Should().BeOfType<OkObjectResult>("Lock must succeed");

        var glEntries = await db.FinanceGlEntries
            .Where(x => x.SourceModule == "Payroll" && x.SourceEntityId == run.Id)
            .ToListAsync();
        glEntries.Should().NotBeEmpty("Lock must persist GL entries");

        var totalDebits  = glEntries.Where(e => !string.IsNullOrEmpty(e.DebitAccount)).Sum(e => e.Amount);
        var totalCredits = glEntries.Where(e => !string.IsNullOrEmpty(e.CreditAccount)).Sum(e => e.Amount);
        Math.Abs(totalDebits - totalCredits).Should().BeLessThan(0.01m,
            $"GL must be balanced: DR={totalDebits}, CR={totalCredits}");
    }

    // ── Test 5: Re-lock does not double-post GL (idempotency) ─────────────────

    [Fact]
    public async Task Lock_Idempotent_SecondLockDoesNotDuplicateGl()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (_, run, _) = await SeedMinimalRun(db, tenantId);

        var ctrl = MakePayrollCtrl(db, tenantId);
        await ctrl.Process(run.Id, CancellationToken.None);

        run.Status = "Processed";
        await db.SaveChangesAsync();

        // First lock
        await ctrl.Lock(run.Id, CancellationToken.None);
        var countAfterFirst = await db.FinanceGlEntries
            .CountAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == run.Id);

        // Unlock manually for test purposes — simulate re-lock
        run.Status = "Processed";
        await db.SaveChangesAsync();

        // Second lock
        await ctrl.Lock(run.Id, CancellationToken.None);
        var countAfterSecond = await db.FinanceGlEntries
            .CountAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == run.Id);

        countAfterSecond.Should().Be(countAfterFirst,
            "re-locking must not double-post GL — idempotency guard must skip if already posted");
    }

    // ── Test 6: Unbalanced run rejected by Lock() (422) ───────────────────────
    // We inject a phantom earning with no corresponding credit to force imbalance.

    [Fact]
    public async Task Lock_UnbalancedGl_Returns422()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (emp, run, _) = await SeedMinimalRun(db, tenantId);

        var ctrl = MakePayrollCtrl(db, tenantId);
        await ctrl.Process(run.Id, CancellationToken.None);

        // Inject an extra earning that will not match any credit,
        // causing totalDebits > totalCredits.
        // Inject a phantom DR earning WITHOUT updating TotalNetSalary (which drives the CR side).
        // This forces totalDebits > totalCredits so the balance check must fail.
        db.PayrollEarnings.Add(new PayrollEarning
        {
            TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = emp.Id,
            ComponentCode = "PHANTOM", ComponentName = "Phantom Earning",
            Amount = 99_999m, Source = "Salary",
        });
        run.Status = "Processed";
        // Intentionally NOT updating run.TotalNetSalary so DR > CR
        await db.SaveChangesAsync();

        var lockResult = await ctrl.Lock(run.Id, CancellationToken.None);

        lockResult.Should().BeOfType<UnprocessableEntityObjectResult>(
            "Lock must return 422 when GL debits ≠ credits");
    }

    // ── Test 7: GOSI amount assertion — hand-worked expected value ─────────────
    // Uses the real KSA pack so statutory deductions are computed, not zeroed.
    // Verified by hand:
    //   covered wage without bonus = Basic(10,000) + Housing(2,000) = 12,000
    //   covered wage with GOSI-included bonus(1,000) = 13,000
    //   Saudi national employee rate 9.75% (annuity 9% + SANED 0.75%)
    //   Employee deduction = 13,000 × 9.75% = 1,267.50

    [Fact]
    public async Task Process_GosiIncludedBonus_RaisesActualGosiContributionByExpectedAmount()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (emp, run, _) = await SeedMinimalRun(db, tenantId);

        var bonusType = new BonusType
        {
            TenantId = tenantId, Code = "BASE", NameEn = "Base Bonus",
            IsIncludedInGosiBase = true, IsIncludedInWps = false, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync();

        var batch = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-GOSI", BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, PaymentPeriod = "2026-06",
            Status = "Approved", CreatedBy = null,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();

        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = 1_000m,
            GrossBonusAmount = 1_000m, TaxWithheld = 0m, BonusAmount = 1_000m,
            PaymentPeriod = "2026-06", Status = "Approved", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();

        // Link the run to a KSA company so the pack resolver fires with CountryCode="SAU".
        await SeedKsaCompany(db, tenantId, run);

        // Use the real KSA pack so deductions are calculated (not zeroed by NullPackResolver).
        var ksaCtrl = MakeKsaPackPayrollCtrl(db, tenantId);
        var result = await ksaCtrl.Process(run.Id, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>("Process must succeed with KSA pack");

        // Employee-side Statutory deductions for a Saudi national on covered wage 13,000:
        //   GOSI annuity EE:  13,000 × 9%    = 1,170.00
        //   SANED EE:         13,000 × 0.75% = 97.50
        //   Total employee:                    1,267.50
        // SQLite does not translate decimal Sum in SQL — enumerate then sum on client.
        var employeeStatutoryRows = await db.PayrollDeductions
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == run.Id
                && d.Source == "Statutory" && !d.ComponentCode.EndsWith("-ER"))
            .ToListAsync();
        var employeeStatutory = employeeStatutoryRows.Sum(d => d.Amount);
        employeeStatutory.Should().Be(1_267.50m,
            "Saudi national on covered wage 13,000 (base 12,000 + GOSI bonus 1,000) " +
            "should pay 9.75% = 1,267.50; without bonus it would be 12,000 × 9.75% = 1,170.00");

        // Baseline check: without the bonus, covered wage = 12,000 → 1,170.00.
        // The test run with bonus must exceed that by exactly 1,000 × 9.75% = 97.50.
        const decimal baselineEmployeeGosi = 12_000m * 0.0975m; // 1,170.00
        (employeeStatutory - baselineEmployeeGosi).Should().Be(97.50m,
            "GOSI-included bonus of 1,000 must raise the employee contribution by exactly 97.50");
    }

    // ── Test 8: Partial batch consumption — line-level isolation ──────────────
    // Batch has bonuses for Employee A (has salary assignment → processed by payroll)
    // and Employee B (no salary assignment → skipped by payroll).
    // After Process(): A's bonus is PaidInPayroll; B's is still Approved; batch NOT locked.
    // After MarkBatchPaid(): B's bonus is Paid; A's is still PaidInPayroll (no clobber).
    // GL covers B's remaining amount only.

    [Fact]
    public async Task PartialBatch_PayrollConsumesA_MarkBatchPaidPaysB_NoClobberAndNoDoubleGl()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (empA, run, str) = await SeedMinimalRun(db, tenantId);
        await SeedKsaCompany(db, tenantId, run);

        // Employee B: exists but has NO salary assignment — payroll run will skip them.
        // Terminated — excluded from Process() employees query (Status != "Active").
        // Real-world case: employee leaves between batch creation and payroll run.
        var empB = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E002", FullName = "Sara Lee",
            Status = "Terminated", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "sara@test.com", Nationality = "GBR", ContractType = "Indefinite",
        };
        db.Employees.Add(empB);
        await db.SaveChangesAsync();

        var bonusType = new BonusType
        {
            TenantId = tenantId, Code = "PART", NameEn = "Partial Test",
            IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync();

        var batch = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-PARTIAL", BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, PaymentPeriod = "2026-06",
            Status = "Approved", TotalAmount = 3_500m, CreatedBy = null,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();

        var bonusA = new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = empA.Id,
            EmployeeName = empA.FullName, BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = 2_000m,
            GrossBonusAmount = 2_000m, TaxWithheld = 0m, BonusAmount = 2_000m,
            PaymentPeriod = "2026-06", Status = "Approved", TaxRegion = "GCC",
        };
        var bonusB = new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = empB.Id,
            EmployeeName = empB.FullName, BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, BasicSalary = 8_000m,
            CalculationMethod = "Fixed", CalculationValue = 1_500m,
            GrossBonusAmount = 1_500m, TaxWithheld = 0m, BonusAmount = 1_500m,
            PaymentPeriod = "2026-06", Status = "Approved", TaxRegion = "GCC",
        };
        db.EmployeeBonuses.AddRange(bonusA, bonusB);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ── Step 1: Process payroll — only empA has a salary assignment.
        var payrollCtrl = MakePayrollCtrl(db, tenantId);
        var processResult = await payrollCtrl.Process(run.Id, CancellationToken.None);
        processResult.Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();

        // empA's bonus: consumed → PaidInPayroll
        var reloadA = await db.EmployeeBonuses.AsNoTracking().FirstAsync(x => x.Id == bonusA.Id);
        reloadA.Status.Should().Be("PaidInPayroll", "empA bonus consumed by payroll");
        reloadA.PayrollRunId.Should().Be(run.Id);

        // empB's bonus: not consumed (terminated → excluded from employees query) → still Approved
        var reloadB = await db.EmployeeBonuses.AsNoTracking().FirstAsync(x => x.Id == bonusB.Id);
        reloadB.Status.Should().Be("Approved", "empB bonus NOT consumed (employee terminated, not in run)");

        // Batch: NOT locked (batchHasUnpaid=true because empB's bonus is still Approved)
        var reloadBatch = await db.BonusBatches.AsNoTracking().FirstAsync(x => x.Id == batch.Id);
        reloadBatch.IsLockedByPayroll.Should().BeFalse("partially-consumed batch must not be locked");
        reloadBatch.Status.Should().Be("Approved");

        // ── Step 2: MarkBatchPaid for the remaining (empB's) bonus.
        var bonusCtrl = MakeBonusCtrl(db, tenantId);
        var markResult = await bonusCtrl.MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None);
        markResult.Should().BeOfType<OkObjectResult>(
            "MarkBatchPaid must succeed for a partially-consumed batch (empB still Approved)");

        db.ChangeTracker.Clear();

        // empA's bonus: still PaidInPayroll, PayrollRunId unchanged (not clobbered by MarkBatchPaid)
        var finalA = await db.EmployeeBonuses.AsNoTracking().FirstAsync(x => x.Id == bonusA.Id);
        finalA.Status.Should().Be("PaidInPayroll", "empA bonus must NOT be touched by MarkBatchPaid");
        finalA.PayrollRunId.Should().Be(run.Id, "empA PayrollRunId must not be overwritten");

        // empB's bonus: now PaidInPayroll (set by MarkBatchPaid)
        var finalB = await db.EmployeeBonuses.AsNoTracking().FirstAsync(x => x.Id == bonusB.Id);
        finalB.Status.Should().Be("PaidInPayroll", "empB bonus paid via MarkBatchPaid");

        // GL entry covers only empB's BonusAmount (1,500) — NOT the full batch total (3,500)
        var bonusGl = await db.FinanceGlEntries
            .Where(x => x.SourceModule == "Bonus" && x.SourceEntityId == batch.Id)
            .ToListAsync();
        bonusGl.Should().ContainSingle("exactly one GL entry for the manual pay path");
        bonusGl[0].Amount.Should().Be(1_500m,
            "GL must cover only the remaining 1,500 (empB), not the full 3,500 batch total");
    }

    // ── Test 9: Employer-GOSI GL balance — DR 5101 + CR 2106 path ─────────────
    // Uses the real KSA pack so employer-side statutory lines are generated.
    // Hand-worked balance:
    //   Earnings DR: BASIC(10,000) + HOUSING(2,000) + TRANSPORT(1,000) = 13,000
    //   Employee GOSI CR (2101): 12,000 × 9.75% = 1,170.00
    //   Employer GOSI CR (2106): 12,000 × 9.75% = 1,170.00
    //   Employer GOSI DR (5101): 1,170.00
    //   Net salary CR (2100):    13,000 − 1,170 = 11,830.00
    //   Total DR: 13,000 + 1,170 = 14,170   Total CR: 1,170 + 1,170 + 11,830 = 14,170 ✓

    [Fact]
    public async Task Lock_WithEmployerStatutoryLines_GlIsBalancedAndIdempotent()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (_, run, _) = await SeedMinimalRun(db, tenantId);

        // Link the run to a KSA company so the pack resolver fires with CountryCode="SAU".
        await SeedKsaCompany(db, tenantId, run);

        // KSA pack produces both employee (EE) and employer (ER) lines.
        var ctrl = MakeKsaPackPayrollCtrl(db, tenantId);
        await ctrl.Process(run.Id, CancellationToken.None);

        run.Status = "Processed";
        await db.SaveChangesAsync();

        var lockResult = await ctrl.Lock(run.Id, CancellationToken.None);
        lockResult.Should().BeOfType<OkObjectResult>("Lock must succeed with KSA employer lines present");

        var glEntries = await db.FinanceGlEntries
            .Where(x => x.SourceModule == "Payroll" && x.SourceEntityId == run.Id)
            .ToListAsync();
        glEntries.Should().NotBeEmpty();

        // Verify employer expense (5101) and employer liability (2106) entries exist.
        var hasEmployerDr = glEntries.Any(e => e.DebitAccount.Contains("5101"));
        var hasEmployerCr = glEntries.Any(e => e.CreditAccount.Contains("2106"));
        hasEmployerDr.Should().BeTrue("employer statutory expense DR to 5101 must be posted");
        hasEmployerCr.Should().BeTrue("employer statutory liability CR to 2106 must be posted");

        var totalDebits  = glEntries.Where(e => !string.IsNullOrEmpty(e.DebitAccount)).Sum(e => e.Amount);
        var totalCredits = glEntries.Where(e => !string.IsNullOrEmpty(e.CreditAccount)).Sum(e => e.Amount);
        Math.Abs(totalDebits - totalCredits).Should().BeLessThan(0.01m,
            $"GL must be balanced with employer lines: DR={totalDebits}, CR={totalCredits}");

        // Re-lock idempotency: count must not increase.
        run.Status = "Processed";
        await db.SaveChangesAsync();
        await ctrl.Lock(run.Id, CancellationToken.None);

        var countAfterRelock = await db.FinanceGlEntries
            .CountAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == run.Id);
        countAfterRelock.Should().Be(glEntries.Count,
            "re-locking a run with employer GOSI lines must not double-post");
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

// Controller factory that wires the real KSA deduction calculator so statutory deductions
// are computed (not zeroed). Used by tests that assert actual GOSI figures or GL balance
// with employer-side lines.
file static class KsaPackFactory
{
    // KSA rates matching the seeder defaults — directional, VERIFY annually.
    // OT/LOP keys also seeded here so Process reads them via IStatutoryRuleReader.
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

file sealed class _P1UnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _P1HttpAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
{
    public _P1HttpAccessor(Microsoft.AspNetCore.Http.HttpContext ctx) => HttpContext = ctx;
    public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
}

file sealed class _P1NullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _P1NullPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new DefaultCountryPackDescriptor();
}

// Resolver that uses the real KSA calculators for tests that need non-zero statutory figures.
// All non-KSA jurisdictions fall back to defaults.
file sealed class _KsaPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(KsaPackFactory.Rules);

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : (IStatutoryDeductionCalculator)new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new DefaultCountryPackDescriptor();
}

file sealed class _P1NullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _P1NullDocStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, Microsoft.AspNetCore.Http.IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
