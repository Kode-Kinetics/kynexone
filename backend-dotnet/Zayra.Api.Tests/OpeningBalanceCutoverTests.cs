using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Mid-year cutover — the end-to-end proof, driven through the real controllers against SQLite so the
/// payroll engine that consumes the imported balances is the production one and not a stub.
///
/// <para>READ THIS BEFORE THE GOSI TEST. The brief for this work assumed the GOSI ceiling is ANNUAL and
/// applied period-to-date, and that a mid-year start without carried YTD would therefore under-contribute
/// for the rest of the year. That is not what this product implements, and it is not what KSA GOSI is.
/// <c>KsaDeductionCalculator</c> caps the MONTHLY covered wage at SAR 45,000
/// (<c>coveredWage = Math.Min(coveredWage, ceiling)</c>); there is no annual accumulator anywhere in the
/// codebase — a repo-wide search for an annual cap returns nothing. The "period-to-date" mechanism the
/// assessment refers to (POD-B2/M8) nets SIBLING RUNS WITHIN ONE MONTH off each other so a supplementary
/// run cannot re-consume the same monthly ceiling; <c>siblingRunIds</c> is scoped to one
/// (company, year, month). It is a within-month device, not a within-year one.</para>
///
/// <para>The consequence is important and commercially good news: a September go-live does NOT
/// under-contribute, because September's GOSI does not depend on January-to-August at all.
/// <see cref="Gosi_CeilingIsMonthly_AndCarriedYtdCannotMoveIt"/> pins that down so nobody "fixes" it into
/// an annual ceiling by accident. What carried YTD is genuinely needed for is the payslip's YTD block,
/// the annual GOSI reconciliation and any future progressive-tax jurisdiction — which is what
/// <see cref="MidYearCutover_CarriedYtd_ReachesTheNextRunsPayslip"/> proves.</para>
/// </summary>
public class OpeningBalanceCutoverTests
{
    private static readonly Guid Maker = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Checker = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ══ Harness ═════════════════════════════════════════════════════════════════

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static ClaimsPrincipal Principal(Guid tenantId, Guid userId, string name) =>
        new(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "payroll.read"),
            new("permission", "payroll.write"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.approve"),
        }, "test"));

    private static MigrationImportController Migration(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new MigrationImportController(db, new Pbkdf2PasswordHasher(), new AuditService(db));
        ctrl.ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = Principal(tenantId, Maker, "consultant") } };
        return ctrl;
    }

    private static ParallelRunController Parallel(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new ParallelRunController(db);
        ctrl.ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = Principal(tenantId, Maker, "consultant") } };
        return ctrl;
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId, string name = "maker")
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, userId, name) };
        var ctrl = new PayrollController(
            db, new _ObScope(), new _ObHttp(httpCtx), new _ObNotifications(),
            new _ObKsaResolver(), _ObRules.Rules, new _ObLetters(), new _ObDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string registration = "1010101010")
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Cutover KSA Co", CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
            RegistrationNumber = registration,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return company;
    }

    private static async Task<Employee> SeedEmployee(
        ZayraDbContext db, Guid tenantId, Guid companyId, string code,
        decimal basic = 10_000m, decimal housing = 2_000m, string nationality = "SAU")
    {
        var structure = await db.SalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (structure is null)
        {
            structure = new SalaryStructure
            {
                TenantId = tenantId, CompanyId = companyId, Code = "STR-BASE", Name = "Base",
                Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            };
            db.SalaryStructures.Add(structure);
            await db.SaveChangesAsync();
        }
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"Emp {code}",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = $"{code}@cutover.test", Nationality = nationality, ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = basic, HousingAllowance = housing, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = $"MOL-{code}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return emp;
    }

    private static string Describe(IActionResult r) => r switch
    {
        ObjectResult o => $"{o.GetType().Name} {o.StatusCode}: {JsonSerializer.Serialize(o.Value)}",
        _ => r.GetType().Name,
    };

    private static async Task<PayrollRun> CreateRun(ZayraDbContext db, Guid tenantId, int year, int month, Guid companyId)
    {
        var res = await Payroll(db, tenantId, Maker).CreateRun(
            new CreatePayrollRunRequest(year, month, companyId, null, null, null, null), CancellationToken.None);
        res.Should().BeOfType<CreatedResult>($"CreateRun must succeed — got {Describe(res)}");
        var run = (PayrollRun)((CreatedResult)res).Value!;
        db.ChangeTracker.Clear();
        return run;
    }

    private static async Task ProcessApproveLock(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        var processed = await Payroll(db, tenantId, Maker).Process(runId, CancellationToken.None);
        processed.Should().BeOfType<OkObjectResult>($"Process must succeed — got {Describe(processed)}");
        db.ChangeTracker.Clear();
        var approved = await Payroll(db, tenantId, Checker, "checker")
            .Approve(runId, new PayrollDecisionRequest("ok", null), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>($"Approve must succeed — got {Describe(approved)}");
        db.ChangeTracker.Clear();
        var locked = await Payroll(db, tenantId, Checker, "checker").Lock(runId, CancellationToken.None);
        locked.Should().BeOfType<OkObjectResult>($"Lock must succeed — got {Describe(locked)}");
        db.ChangeTracker.Clear();
    }

    private const string CutoverCsv =
        "CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n" +
        "1010101010,,2026-09-01,SAP,Active,Wave 1\n";

    private static MigrationPackageRequest Package(string externalId, params (string Section, string Csv)[] sections) =>
        new(externalId, sections.ToDictionary(s => s.Section, s => s.Csv), false);

    private static async Task<MigrationReconciliationDto> Commit(
        ZayraDbContext db, Guid tenantId, MigrationPackageRequest package)
    {
        var res = await Migration(db, tenantId).Commit(package, CancellationToken.None);
        var ok = res.Result.Should().BeOfType<OkObjectResult>(
            $"Commit must succeed — got {Describe(res.Result!)}").Subject;
        var dto = (MigrationReconciliationDto)ok.Value!;
        dto.Errors.Should().BeEmpty("every row must import cleanly");
        db.ChangeTracker.Clear();
        return dto;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  1 — a mid-year cutover produces a correct next run
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The September run after an August cutover must carry the January-to-August figures on its
    /// payslip's year-to-date block. Without this the first payslip an employee receives from the new
    /// system claims they have earned nothing all year, which is the single most visible cutover defect
    /// and the one that generates a hundred HR tickets on day one.
    /// </summary>
    [Fact]
    public async Task MidYearCutover_CarriedYtd_ReachesTheNextRunsPayslip()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "EMP-001");

        // Eight months of history from the outgoing system: 8 x 13,000 gross.
        await Commit(db, tid, Package("cutover-ytd",
            ("companyCutover", CutoverCsv),
            ("payrollOpeningBalances",
                "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026,YTD_GROSS,TOTAL,104000,SAR,SAP,OB-1\n" +
                "EMP-001,2026,YTD_DEDUCTIONS,TOTAL,9360,SAR,SAP,OB-2\n" +
                "EMP-001,2026,YTD_NET,TOTAL,94640,SAR,SAP,OB-3\n" +
                "EMP-001,2026,YTD_STATUTORY_EE,GOSI-ANN-EE,8640,SAR,SAP,OB-4\n" +
                "EMP-001,2026,YTD_STATUTORY_ER,GOSI-ANN-ER,8640,SAR,SAP,OB-5\n")));

        var run = await CreateRun(db, tid, 2026, 9, co.Id);
        (await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id);

        // The carried aggregates are folded into the payslip's YTD block on top of September's own
        // figures. September is the only run in this product, so YTD = carried + this month.
        slip.YtdGross.Should().Be(104_000m + slip.GrossSalary,
            "the eight months carried in must appear in year-to-date gross alongside September's own");
        slip.YtdNet.Should().Be(94_640m + slip.NetSalary);
        slip.YtdDeductions.Should().Be(9_360m + slip.Deductions);

        // The statutory detail is carried and queryable, but deliberately NOT folded into YtdDeductions
        // — it is already inside the 9,360 aggregate, and adding it again would over-report every
        // payslip by the statutory amount for the rest of the year.
        var statutory = (await db.PayrollOpeningBalances.AsNoTracking()
            .Where(x => x.TenantId == tid && x.EmployeeId == emp.Id && x.BalanceType == OpeningBalanceTypes.YtdStatutoryEmployee)
            .Select(x => x.Amount).ToListAsync()).Sum();
        statutory.Should().Be(8_640m, "YTD employee-side social insurance must be carried for the annual reconciliation");
        slip.YtdDeductions.Should().NotBe(9_360m + 8_640m + slip.Deductions,
            "carrying the statutory split must not double-count it into the payslip's YTD deductions");
    }

    /// <summary>
    /// THE STATUTORY TRUTH, PINNED. The KSA covered-wage ceiling is MONTHLY. An employee paid above it
    /// contributes the capped amount in September whether or not eight months of YTD were carried in,
    /// because the calculator caps <c>Basic + Housing</c> for the period and consults no annual total.
    ///
    /// <para>This test exists to stop a well-meaning change turning the monthly cap into an annual one.
    /// If it ever fails, the product has started treating GOSI as an annual ceiling, which would
    /// under-remit for every employee below the cap and is the penalty-bearing direction.</para>
    /// </summary>
    [Fact]
    public async Task Gosi_CeilingIsMonthly_AndCarriedYtdCannotMoveIt()
    {
        // Two identical tenants. One has eight months of YTD carried in — including YTD covered wage
        // far beyond any conceivable annual cap — and the other has nothing at all.
        var withYtd = await GosiForSeptember(carryYtd: true);
        var without = await GosiForSeptember(carryYtd: false);

        withYtd.Should().Be(without,
            "the covered-wage ceiling is applied to the MONTH, so carried year-to-date cannot change September's GOSI");

        // And it is the ceiling that bound, not the wage: basic 50,000 + housing 10,000 = 60,000
        // covered wage, capped to 45,000, at 9.75% employee side (9% annuities + 0.75% SANED).
        withYtd.Should().Be(Math.Round(45_000m * 0.09m, 2) + Math.Round(45_000m * 0.0075m, 2),
            "September must contribute on the 45,000 ceiling, not on the 60,000 package");
    }

    private static async Task<decimal> GosiForSeptember(bool carryYtd)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001", basic: 50_000m, housing: 10_000m);

        if (carryYtd)
        {
            await Commit(db, tid, Package("cutover-ceiling",
                ("companyCutover", CutoverCsv),
                ("payrollOpeningBalances",
                    "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
                    "EMP-001,2026,YTD_GROSS,TOTAL,488000,SAR,SAP,OB-1\n" +
                    "EMP-001,2026,YTD_COVERED_WAGE,TOTAL,360000,SAR,SAP,OB-2\n" +
                    "EMP-001,2026,YTD_STATUTORY_EE,GOSI-ANN-EE,35100,SAR,SAP,OB-3\n")));
        }

        var run = await CreateRun(db, tid, 2026, 9, co.Id);
        (await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Summed client-side: SQLite cannot translate SUM over a decimal column.
        return (await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tid && d.PayrollRunId == run.Id
                     && d.Source == "Statutory" && !d.IsEmployerContribution)
            .Select(d => d.Amount).ToListAsync()).Sum();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  2 — a loan carried in at 7 of 24 finishes at 24, not 31
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The failure this guards against is the obvious one: importing the loan at its ORIGINAL amount
    /// and letting the system generate a fresh 24-instalment schedule on top of the seven already
    /// repaid, so the employee pays 31 instalments. The import states the mid-life position directly,
    /// so the schedule is 24 rows with 7 already settled and 17 outstanding.
    /// </summary>
    [Fact]
    public async Task Loan_CarriedInAtSevenOfTwentyFour_HasSeventeenRemainingThatSumToTheBalance()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        await Commit(db, tid, Package("cutover-loan",
            ("companyCutover", CutoverCsv),
            ("loans",
                "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n")));

        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.TenantId == tid);
        loan.Status.Should().Be("Active");
        loan.OutstandingBalance.Should().Be(17_000m);
        loan.TotalRepaid.Should().Be(7_000m, "derived as OriginalAmount - OutstandingBalance");
        loan.ApprovedInstallments.Should().Be(24);

        var schedule = await db.LoanInstallments.AsNoTracking()
            .Where(i => i.LoanId == loan.Id).OrderBy(i => i.InstallmentNumber).ToListAsync();

        schedule.Should().HaveCount(24, "the schedule is 24 long in total — NOT 7 already paid plus a fresh 24");
        schedule.Count(i => i.Status == "Paid").Should().Be(7);
        schedule.Count(i => i.Status == "Pending").Should().Be(17);
        schedule.Where(i => i.Status == "Pending").Sum(i => i.AmountDue).Should().Be(17_000m,
            "the remaining schedule must discharge the carried balance exactly, to the fils");
        schedule.Where(i => i.Status == "Paid").Should().OnlyContain(i => i.PayrollRunId == null,
            "instalments repaid in the previous system must not be attributed to a run in this one");
        schedule.Single(i => i.InstallmentNumber == 8).DueDate.Should().Be(new DateOnly(2026, 9, 25));
        schedule.Single(i => i.InstallmentNumber == 24).DueDate.Should().Be(new DateOnly(2028, 1, 25));
    }

    /// <summary>
    /// And it really does continue. Three consecutive live payroll runs after cutover must take
    /// instalments 8, 9 and 10 — one per month, a thousand each — and leave the balance at 14,000.
    /// </summary>
    [Fact]
    public async Task Loan_CarriedInMidLife_ContinuesDeductingFromTheNextInstalment()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        await Commit(db, tid, Package("cutover-loan-live",
            ("companyCutover", CutoverCsv),
            ("loans",
                "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n")));

        foreach (var month in new[] { 9, 10, 11 })
        {
            var run = await CreateRun(db, tid, 2026, month, co.Id);
            await ProcessApproveLock(db, tid, run.Id);
        }

        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.TenantId == tid);
        loan.OutstandingBalance.Should().Be(14_000m, "three months at a thousand each off a 17,000 balance");
        loan.TotalRepaid.Should().Be(10_000m);
        loan.Status.Should().Be("Active", "fourteen instalments remain");

        var paid = await db.LoanInstallments.AsNoTracking()
            .Where(i => i.LoanId == loan.Id && i.Status == "Paid").OrderBy(i => i.InstallmentNumber).ToListAsync();
        paid.Select(i => i.InstallmentNumber).Should().Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            "the schedule resumed at 8 — it did not restart at 1 and it did not run past 24");
        paid.Where(i => i.InstallmentNumber > 7).Should().OnlyContain(i => i.PayrollRunId != null,
            "instalments taken HERE are attributed to the run that took them");

        // The remaining schedule still ends at 24.
        var pending = await db.LoanInstallments.AsNoTracking()
            .Where(i => i.LoanId == loan.Id && i.Status == "Pending").ToListAsync();
        pending.Should().HaveCount(14);
        pending.Max(i => i.InstallmentNumber).Should().Be(24);
        pending.Sum(i => i.AmountDue).Should().Be(14_000m);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3 — idempotence and resume
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Re-importing the same file is a no-op, not a second set of loans, provisions and origins.</summary>
    [Fact]
    public async Task ReimportingTheSameFile_IsANoOp()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");
        var package = FullCutoverPackage("cutover-idempotent");

        var first = await Commit(db, tid, package);
        var second = await Commit(db, tid, package);

        second.BatchId.Should().Be(first.BatchId, "the same package under the same batch id is one import");
        await AssertExactlyOneOfEverything(db, tid);
    }

    /// <summary>
    /// An import that dies partway must resume without double-applying a single row.
    ///
    /// <para>The engine commits per section, so a crash in section 5 leaves sections 1-4 durably
    /// applied — that is the real state a resume has to cope with, and it is reproduced here by
    /// forcing the batch back to Failed with its counters cleared while the rows it already wrote stay
    /// on disk. Resume then re-runs every section over data that is already there.</para>
    /// </summary>
    [Fact]
    public async Task InterruptedImport_ResumesWithoutDoubleApplyingAnyRow()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");
        var package = FullCutoverPackage("cutover-resume");

        var first = await Commit(db, tid, package);
        await AssertExactlyOneOfEverything(db, tid);

        // Crash remnant: the batch never reached Completed, but its durable effects are on disk.
        var batch = await db.MigrationImportBatches.SingleAsync(b => b.Id == first.BatchId);
        batch.Status = "Failed";
        batch.CompletedAtUtc = null;
        batch.ReceivedRows = 0; batch.CreatedRows = 0; batch.UpdatedRows = 0; batch.SkippedRows = 0;
        batch.ReconciliationJson = "{}"; batch.ResultJson = "{}";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var resumed = await Migration(db, tid).Resume(first.BatchId, package, CancellationToken.None);
        var dto = (MigrationReconciliationDto)resumed.Result.Should()
            .BeOfType<OkObjectResult>($"Resume must succeed — got {Describe(resumed.Result!)}").Subject.Value!;
        dto.Status.Should().Be("Completed");
        dto.Errors.Should().BeEmpty();
        db.ChangeTracker.Clear();

        await AssertExactlyOneOfEverything(db, tid);

        // The loan in particular must not have been re-scheduled into a second set of instalments.
        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.TenantId == tid);
        (await db.LoanInstallments.AsNoTracking().CountAsync(i => i.LoanId == loan.Id))
            .Should().Be(24, "a resume rebuilds the schedule in place, it does not append a second one");
        loan.OutstandingBalance.Should().Be(17_000m, "and it does not re-apply the balance either");
    }

    private static MigrationPackageRequest FullCutoverPackage(string externalId) => Package(externalId,
        ("companyCutover", CutoverCsv),
        ("payrollOpeningBalances",
            "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
            "EMP-001,2026,YTD_GROSS,TOTAL,104000,SAR,SAP,OB-1\n"),
        ("loans",
            "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
            "EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n"),
        ("advances",
            "EmployeeCode,AdvanceNumber,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,Currency,SourceSystem,SourceRecordId\n" +
            "EMP-001,LEG-ADV-0001,6000,1000,6,2,4000,2026-09-25,SAR,SAP,ADV-001\n"),
        ("eosbOpeningProvision",
            "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n" +
            "EMP-001,2026-08-31,42,52500,2023-03-01,SAR,SAP,EOSB-001\n"));

    private static async Task AssertExactlyOneOfEverything(ZayraDbContext db, Guid tid)
    {
        (await db.CompanyCutovers.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
        (await db.EmployeeLoans.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
        (await db.SalaryAdvances.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
        (await db.EmployeeEosbOpeningBalances.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
        (await db.PayrollOpeningBalances.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
        (await db.LoanTypes.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
        // Four carried-in entities: the loan, the advance, the provision and the YTD row.
        (await db.OpeningBalanceOrigins.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(4);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  4 — carried-in figures stay distinguishable forever
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An audit must be able to see that 17,000 of loan balance was carried in rather than lent here,
    /// and it must still see it after payroll has worked the balance down. The provenance row is frozen
    /// at import; the live row moves.
    /// </summary>
    [Fact]
    public async Task CarriedInBalances_StayDistinguishableFromEarnedOnes_InDataAndInAReport()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");
        await Commit(db, tid, FullCutoverPackage("cutover-provenance"));

        // ── In the data ──
        var origins = await db.OpeningBalanceOrigins.AsNoTracking().Where(x => x.TenantId == tid).ToListAsync();
        origins.Should().HaveCount(4);
        origins.Should().OnlyContain(o => o.CutoverDate == new DateOnly(2026, 9, 1));
        origins.Should().OnlyContain(o => o.SourceSystem == "SAP");
        origins.Should().OnlyContain(o => o.MigrationBatchId != Guid.Empty);
        origins.Single(o => o.EntityType == OpeningBalanceEntityTypes.Loan).CarriedAmount.Should().Be(17_000m);
        origins.Single(o => o.EntityType == OpeningBalanceEntityTypes.EosbProvision).CarriedAmount.Should().Be(52_500m);

        // Run a month so the loan balance moves away from what was carried in.
        var run = await CreateRun(db, tid, 2026, 9, co.Id);
        await ProcessApproveLock(db, tid, run.Id);

        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.TenantId == tid);
        loan.OutstandingBalance.Should().Be(16_000m);
        var loanOrigin = await db.OpeningBalanceOrigins.AsNoTracking()
            .SingleAsync(o => o.TenantId == tid && o.EntityType == OpeningBalanceEntityTypes.Loan);
        loanOrigin.CarriedAmount.Should().Be(17_000m,
            "the carried figure is frozen — it answers what came in, not what is left");

        // ── In a report ──
        var report = await Parallel(db, tid).CarriedIn("EMP-001", CancellationToken.None);
        var json = JsonSerializer.Serialize(((OkObjectResult)report).Value);
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("carriedIn").EnumerateArray().ToList();
        rows.Should().HaveCount(4);

        var loanRow = rows.Single(r => r.GetProperty("entityType").GetString() == OpeningBalanceEntityTypes.Loan);
        loanRow.GetProperty("carriedAmount").GetDecimal().Should().Be(17_000m);
        loanRow.GetProperty("currentBalance").GetDecimal().Should().Be(16_000m);
        loanRow.GetProperty("movementSinceCutover").GetDecimal().Should().Be(1_000m,
            "the report states what came in, what it is now, and therefore what this product is responsible for");
        loanRow.GetProperty("sourceSystem").GetString().Should().Be("SAP");
        loanRow.GetProperty("sourceRecordId").GetString().Should().Be("LN-001");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  5 — an import into a locked period is refused, by name
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ImportIntoALockedPeriod_IsRefusedWithANamedReason()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        // September is already run, approved and locked in THIS product.
        var september = await CreateRun(db, tid, 2026, 9, co.Id);
        await ProcessApproveLock(db, tid, september.Id);

        // Now a consultant tries to carry in opening balances as at a 1 September cutover.
        var res = await Migration(db, tid).Commit(FullCutoverPackage("cutover-locked"), CancellationToken.None);

        var conflict = res.Result.Should().BeOfType<ConflictObjectResult>(
            $"the import must be refused — got {Describe(res.Result!)}").Subject;
        // Serialised the way ASP.NET actually returns it, so the property names asserted below are
        // the ones a consultant's client really sees.
        var json = JsonSerializer.Serialize(conflict.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().Should().Be("cutover_period_locked");

        var refusals = doc.RootElement.GetProperty("refusals").EnumerateArray().ToList();
        refusals.Should().ContainSingle();
        var refusal = refusals[0];
        refusal.GetProperty("period").GetString().Should().Be("2026-09");
        refusal.GetProperty("payrollRunId").GetGuid().Should().Be(september.Id);
        refusal.GetProperty("companyName").GetString().Should().Be("Cutover KSA Co");
        refusal.GetProperty("reason").GetString().Should()
            .Contain("Locked").And.Contain("2026-09").And.Contain("cutover",
                "the refusal has to tell a consultant which run, which period and what to do — not just 'conflict'");

        // And nothing was written.
        (await db.EmployeeLoans.CountAsync(l => l.TenantId == tid)).Should().Be(0);
        (await db.OpeningBalanceOrigins.CountAsync(o => o.TenantId == tid)).Should().Be(0);
    }

    /// <summary>A locked period BEFORE the cutover is not a refusal — that period legitimately belonged
    /// to this product, and refusing on it would make a second migration wave impossible.</summary>
    [Fact]
    public async Task ImportIsAllowed_WhenTheLockedPeriodPrecedesTheCutover()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        var july = await CreateRun(db, tid, 2026, 7, co.Id);
        await ProcessApproveLock(db, tid, july.Id);

        await Commit(db, tid, FullCutoverPackage("cutover-prior-lock"));
        (await db.EmployeeLoans.CountAsync(l => l.TenantId == tid)).Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  6 — validation is strict and loud
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    // A balance the remaining schedule cannot discharge — the classic transcription error.
    [InlineData("EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,23000,2026-09-25,2026-02-25,SAR,SAP,LN-001",
        "cannot be discharged")]
    // More instalments repaid than exist.
    [InlineData("EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,24,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001",
        "InstallmentsPaid must be between")]
    // A blank outstanding balance is NOT read as zero.
    [InlineData("EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,,2026-09-25,2026-02-25,SAR,SAP,LN-001",
        "OutstandingBalance is required and was blank")]
    // A thousands separator the source system emitted.
    [InlineData("EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17 000,2026-09-25,2026-02-25,SAR,SAP,LN-001",
        "OutstandingBalance is not a number")]
    public async Task LoanRowsThatCannotBeTrue_AreRejectedWithTheCellNamed(string dataRow, string expected)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        var res = await Migration(db, tid).Commit(Package("cutover-bad-loan",
            ("companyCutover", CutoverCsv),
            ("loans",
                "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n"
                + dataRow + "\n")), CancellationToken.None);

        var dto = (MigrationReconciliationDto)((OkObjectResult)res.Result!).Value!;
        dto.Errors.Should().ContainSingle().Which.Should().Contain(expected);
        (await db.EmployeeLoans.CountAsync(l => l.TenantId == tid)).Should().Be(0,
            "a rejected row must leave nothing behind");
    }

    /// <summary>
    /// An unrecognised YTD bucket is refused rather than stored and silently ignored. The old
    /// behaviour would accept any BalanceType string and then never read it, so a consultant who typed
    /// YTD_GOSI instead of YTD_STATUTORY_EE got a clean import and a missing figure.
    /// </summary>
    [Fact]
    public async Task AnUnknownYtdBucket_IsRefusedRatherThanStoredAndIgnored()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        var res = await Migration(db, tid).Commit(Package("cutover-bad-bucket",
            ("companyCutover", CutoverCsv),
            ("payrollOpeningBalances",
                "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026,YTD_GOSI,GOSI-ANN-EE,8640,SAR,SAP,OB-1\n")), CancellationToken.None);

        var dto = (MigrationReconciliationDto)((OkObjectResult)res.Result!).Value!;
        dto.Errors.Should().ContainSingle().Which.Should()
            .Contain("YTD_GOSI").And.Contain("not a recognised opening-balance bucket");
        (await db.PayrollOpeningBalances.CountAsync(x => x.TenantId == tid)).Should().Be(0);
    }

    /// <summary>
    /// Two rows in one file carrying the same loan number. The upsert lookup reads the database and the
    /// section is not saved until it finishes, so without an in-file guard both rows miss, both create,
    /// and the employee is deducted for the same loan twice every month.
    /// </summary>
    [Fact]
    public async Task ADuplicateKeyWithinOneFile_IsRefusedRatherThanImportedTwice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        var res = await Migration(db, tid).Commit(Package("cutover-dupe",
            ("companyCutover", CutoverCsv),
            ("loans",
                "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n" +
                "EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n")),
            CancellationToken.None);

        var dto = (MigrationReconciliationDto)((OkObjectResult)res.Result!).Value!;
        dto.Errors.Should().ContainSingle().Which.Should()
            .Contain("loans row 3").And.Contain("repeats a key already used earlier in the same file");
        (await db.EmployeeLoans.CountAsync(l => l.TenantId == tid)).Should().Be(1,
            "the first occurrence imports; the second is refused, not applied on top");
        (await db.LoanInstallments.CountAsync()).Should().Be(24, "and it did not double the schedule either");
    }

    /// <summary>Opening balances for an entity that has not declared its wave are refused by name —
    /// this is what makes the cutover per legal entity rather than per tenant.</summary>
    [Fact]
    public async Task BalancesForAnEntityWithNoDeclaredCutover_AreRefusedByName()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var waveOne = await SeedCompany(db, tid, "1010101010");
        var waveTwo = new Company
        {
            TenantId = tid, LegalNameEn = "Wave Two Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            IsActive = true, DefaultCurrency = "SAR", RegistrationNumber = "2020202020",
        };
        db.Companies.Add(waveTwo);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await SeedEmployee(db, tid, waveOne.Id, "EMP-001");
        await SeedEmployee(db, tid, waveTwo.Id, "EMP-002");

        // Only wave one declares a cutover; both entities' employees have loans in the file.
        var res = await Migration(db, tid).Commit(Package("cutover-waves",
            ("companyCutover", CutoverCsv),
            ("loans",
                "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n" +
                "EMP-002,LEG-LN-0002,PERSONAL,Personal Loan,12000,1000,12,3,9000,2026-09-25,2026-05-25,SAR,SAP,LN-002\n")),
            CancellationToken.None);

        var dto = (MigrationReconciliationDto)((OkObjectResult)res.Result!).Value!;
        dto.Errors.Should().ContainSingle().Which.Should()
            .Contain("EMP-002").And.Contain("No Active cutover is declared");
        (await db.EmployeeLoans.CountAsync(l => l.TenantId == tid)).Should().Be(1,
            "wave one imports; wave two waits for its own cutover row");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  7 — preview reconciliation
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Preview must give the consultant counts AND control totals per section, plus the named list of
    /// what will be rejected, without writing anything. Counts alone do not catch a misplaced decimal
    /// point; a control total tied to the source system's report bottom line does.
    /// </summary>
    [Fact]
    public async Task Preview_GivesCountsControlTotalsAndNamedRejections_WithoutWriting()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        var package = new MigrationPackageRequest("cutover-preview", new Dictionary<string, string>
        {
            ["companyCutover"] = CutoverCsv,
            ["loans"] =
                "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n" +
                "EMP-404,LEG-LN-0002,PERSONAL,Personal Loan,12000,1000,12,3,9000,2026-09-25,2026-05-25,SAR,SAP,LN-002\n",
            ["eosbOpeningProvision"] =
                "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026-08-31,42,52500,2023-03-01,SAR,SAP,EOSB-001\n",
        }, DryRun: true);

        var res = await Migration(db, tid).Preview(package, CancellationToken.None);
        var dto = (MigrationReconciliationDto)((OkObjectResult)res.Result!).Value!;

        dto.Status.Should().Be("Previewed");
        dto.SectionCounts["loans"].Should().Be(2);
        dto.SectionCounts["eosbOpeningProvision"].Should().Be(1);

        // The control total is what the FILE claims, over every row — including the one that will be
        // rejected — because that is the figure the consultant ties to the outgoing system's report.
        // What will not land is named separately in Errors.
        dto.SectionTotals["loans"].Should().Be(26_000m, "17,000 + 9,000 — the figure to tie to the loan report");
        dto.SectionTotals["eosbOpeningProvision"].Should().Be(52_500m, "the provision on the customer's balance sheet");

        dto.Errors.Should().ContainSingle().Which.Should()
            .Contain("EMP-404").And.Contain("was not found", "rejections are named, with the row number");
        dto.Errors[0].Should().StartWith("loans row 3", "the consultant needs the line of the file to fix");

        // Nothing was written.
        (await db.EmployeeLoans.CountAsync(l => l.TenantId == tid)).Should().Be(0);
        (await db.CompanyCutovers.CountAsync(c => c.TenantId == tid)).Should().Be(0);
        (await db.OpeningBalanceOrigins.CountAsync(o => o.TenantId == tid)).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  8 — parallel run: variance against the outgoing register
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The reconciliation a payroll go-live actually turns on: our September run against the register
    /// the old system produced for September, per employee and per component, with a tolerance.
    /// </summary>
    [Fact]
    public async Task VarianceReport_FlagsOnlyWhatIsOutsideTolerance()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");
        await SeedEmployee(db, tid, co.Id, "EMP-002");

        var run = await CreateRun(db, tid, 2026, 9, co.Id);
        (await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var slips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id)
            .ToDictionaryAsync(s => s.EmployeeCode, s => s);
        var one = slips["EMP-001"];
        var two = slips["EMP-002"];

        // EMP-001 agrees to the fils. EMP-002's legacy net is 250 lower — the kind of gap a parallel
        // run exists to surface.
        var register =
            "EmployeeCode,ComponentCode,Amount\n" +
            $"EMP-001,GROSS,{one.GrossSalary}\n" +
            $"EMP-001,NET,{one.NetSalary}\n" +
            $"EMP-002,GROSS,{two.GrossSalary}\n" +
            $"EMP-002,NET,{two.NetSalary - 250m}\n";

        var res = await Parallel(db, tid).Variance(run.Id,
            new ParallelRunController.VarianceRequest(register, Tolerance: 0.01m), CancellationToken.None);
        var report = (ParallelRunController.VarianceReport)((OkObjectResult)res.Result!).Value!;

        report.Period.Should().Be("2026-09");
        report.MatchedEmployees.Should().Be(2);
        report.EmployeesOnlyInRegister.Should().Be(0);

        var flagged = report.Lines.Where(l => l.IsOutsideTolerance).ToList();
        flagged.Should().ContainSingle(l => l.EmployeeCode == "EMP-002" && l.ComponentCode == "NET",
            "only the genuine gap is flagged");
        flagged.Single(l => l.ComponentCode == "NET").Variance.Should().Be(250m);

        report.Lines.Should().Contain(l => l.EmployeeCode == "EMP-001" && l.ComponentCode == "NET" && !l.IsOutsideTolerance);

        // Components the run produced but the register did not are surfaced rather than hidden — a
        // deduction the old system never took is exactly what a parallel run must catch.
        report.Lines.Should().Contain(l => l.Presence == "OnlyInKynexOne",
            "statutory lines present here and absent there must appear on the report");
    }

    /// <summary>
    /// A percentage tolerance is what a consultant actually uses on the first cycle, when they expect
    /// small rounding differences everywhere and are hunting for the material ones.
    /// </summary>
    [Fact]
    public async Task VarianceReport_HonoursAPercentageTolerance()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        var run = await CreateRun(db, tid, 2026, 9, co.Id);
        (await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id);

        // A 1% difference on net.
        var legacyNet = Math.Round(slip.NetSalary * 0.99m, 2);
        var register = $"EmployeeCode,ComponentCode,Amount\nEMP-001,NET,{legacyNet}\n";

        var tight = (ParallelRunController.VarianceReport)((OkObjectResult)(await Parallel(db, tid).Variance(
            run.Id, new ParallelRunController.VarianceRequest(register, 0.5m, "Percentage"), CancellationToken.None)).Result!).Value!;
        tight.Lines.Single(l => l.ComponentCode == "NET").IsOutsideTolerance.Should().BeTrue("1% exceeds a 0.5% tolerance");

        var loose = (ParallelRunController.VarianceReport)((OkObjectResult)(await Parallel(db, tid).Variance(
            run.Id, new ParallelRunController.VarianceRequest(register, 2m, "Percentage"), CancellationToken.None)).Result!).Value!;
        loose.Lines.Single(l => l.ComponentCode == "NET").IsOutsideTolerance.Should().BeFalse("1% is inside a 2% tolerance");
    }

    /// <summary>
    /// The trial-run claim, proved rather than asserted: a processed-but-unlocked run writes no GL, and
    /// its figures do not reach anybody's year to date. That is what makes a parallel month safe to run.
    /// </summary>
    [Fact]
    public async Task AProcessedButUnlockedRun_PostsNoGlAndDoesNotPolluteYtd()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "EMP-001");

        var trial = await CreateRun(db, tid, 2026, 9, co.Id);
        (await Payroll(db, tid, Maker).Process(trial.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollSlips.CountAsync(s => s.RunId == trial.Id)).Should().Be(1, "the numbers exist to look at");
        (await db.FinanceGlEntries.CountAsync(g => g.TenantId == tid)).Should().Be(0,
            "but nothing has reached the ledger, so the month can be re-run freely");

        // A later run in the same year sees nothing from the unlocked trial in its YTD.
        var october = await CreateRun(db, tid, 2026, 10, co.Id);
        (await Payroll(db, tid, Maker).Process(october.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var octoberSlip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == october.Id);
        octoberSlip.YtdGross.Should().Be(octoberSlip.GrossSalary,
            "an unlocked trial run must not appear in anybody's year to date");
    }
}

// ── Test doubles (file-scoped) ─────────────────────────────────────────────────

file static class _ObRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m);
}

file sealed class _ObScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _ObHttp : IHttpContextAccessor
{
    public _ObHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _ObNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _ObKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_ObRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _ObLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _ObDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
