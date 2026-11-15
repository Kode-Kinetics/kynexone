using System.Security.Claims;
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
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The collection path that remains once an advance installment can no longer be marked paid by hand
/// (<see cref="AdvanceRepaymentControlTests"/>): an advance requested, approved and paid out through the hardened endpoints is
/// deducted by <c>PayrollController.Process</c>, which retires the balance and stamps the installment it funded. An advance
/// that is approved but not yet paid out is never collected; an Active advance created by the old approve-and-disburse flow
/// (no payout record) still is.
/// </summary>
public class AdvancePayrollDeductionTests
{
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly Guid Payer = Guid.NewGuid();
    private static readonly Guid PayrollMaker = Guid.NewGuid();
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task APaidOutAdvance_IsDeductedByPayroll_AndItsFirstInstallmentIsStampedPaid()
    {
        var (db, conn, tenantId, emp, runs) = await SeedAsync(runMonths: 1, maxInstallments: 4);
        await using var _ = conn; await using var __ = db;
        var run = runs[0];

        // Request (HR, on the employee's behalf), approve (a different HR user), pay out (Finance) through the real endpoints.
        var advanceId = await RequestApproveAndPayAsync(db, tenantId, emp, 2_000m, 4);

        (await Payroll(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
        adv.TotalRepaid.Should().Be(500m, "one 500 installment was withheld from this month's pay");
        adv.OutstandingBalance.Should().Be(1_500m);
        adv.Status.Should().Be("Active");
        (adv.ApprovedAmount - adv.TotalRepaid - adv.OutstandingBalance).Should().Be(0m, "the advance still reconciles");

        var installments = await db.AdvanceInstallments.AsNoTracking()
            .Where(x => x.AdvanceId == advanceId).OrderBy(x => x.InstallmentNumber).ToListAsync();
        installments.Should().HaveCount(4);
        installments[0].Status.Should().Be("Paid");
        installments[0].AmountPaid.Should().Be(500m);
        installments[0].PayrollRunId.Should().Be(run.Id);
        installments.Skip(1).Should().OnlyContain(i => i.Status == "Pending" && i.AmountPaid == 0m);
    }

    [Fact]
    public async Task APayrollRun_NeverCollectsAnApprovedAdvanceAwaitingPayout_AndCollectsItOncePaidOut()
    {
        var (db, conn, tenantId, emp, runs) = await SeedAsync(runMonths: 2, maxInstallments: 4);
        await using var _ = conn; await using var __ = db;

        var advanceId = await RequestAndApproveAsync(db, tenantId, emp, 2_000m, 4);

        // Month 1: the advance is approved but nobody has paid it. Payroll must not recover money the employee never received.
        (await Payroll(db, tenantId).Process(runs[0].Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var approved = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
        approved.Status.Should().Be("Approved");
        approved.TotalRepaid.Should().Be(0m);
        approved.OutstandingBalance.Should().Be(0m);
        (await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == advanceId).ToListAsync())
            .Should().OnlyContain(i => i.Status == "Pending" && i.AmountPaid == 0m && i.PayrollRunId == null);

        // Finance pays it out; month 2 collects the first instalment, once.
        (await Finance(db, tenantId).Payout(advanceId, new AdvancePayoutRequest(Today, "BANK-PAY-1", null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId).Process(runs[1].Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var paid = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
        paid.Status.Should().Be("Active");
        paid.TotalRepaid.Should().Be(500m);
        paid.OutstandingBalance.Should().Be(1_500m);
        var rows = await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == advanceId).OrderBy(x => x.InstallmentNumber).ToListAsync();
        rows[0].PayrollRunId.Should().Be(runs[1].Id);
        rows.Skip(1).Should().OnlyContain(i => i.Status == "Pending");
    }

    [Fact]
    public async Task ALegacyActiveAdvance_FromTheOldApproveAndDisburseFlow_IsStillCollected()
    {
        var (db, conn, tenantId, emp, runs) = await SeedAsync(runMonths: 1, maxInstallments: 4);
        await using var _ = conn; await using var __ = db;
        // The old flow made an advance Active at approval with no payout record: PaidAtUtc and PaymentReference are null.
        var legacy = new SalaryAdvance
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.PublicId, EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
            AdvanceNumber = "ADV-LEGACY-1", RequestedAmount = 1_000m, ApprovedAmount = 1_000m, OutstandingBalance = 1_000m,
            Installments = 2, InstallmentAmount = 500m, RepaymentType = "Installments", RepaymentStartDate = Today,
            Status = "Active", CreatedBy = Requester,
        };
        db.SalaryAdvances.Add(legacy);
        db.AdvanceInstallments.AddRange(
            new AdvanceInstallment { TenantId = tenantId, AdvanceId = legacy.Id, InstallmentNumber = 1, DueDate = Today, AmountDue = 500m, Status = "Pending" },
            new AdvanceInstallment { TenantId = tenantId, AdvanceId = legacy.Id, InstallmentNumber = 2, DueDate = Today.AddMonths(1), AmountDue = 500m, Status = "Pending" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tenantId).Process(runs[0].Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == legacy.Id);
        adv.PaidAtUtc.Should().BeNull("a legacy row is not backfilled");
        adv.Status.Should().Be("Active");
        adv.TotalRepaid.Should().Be(500m);
        adv.OutstandingBalance.Should().Be(500m);
        (await db.AdvanceInstallments.AsNoTracking().SingleAsync(x => x.AdvanceId == legacy.Id && x.InstallmentNumber == 1))
            .Status.Should().Be("Paid");
    }

    // The schedule rounds each installment DOWN to cents and puts the remainder in the LAST row, so the payroll
    // deduction must recover that remainder with the final installment: no stray balance, no extra deduction.
    [Theory]
    [InlineData(100, 6)]   // 16.66 x 5 + 16.70
    [InlineData(100, 3)]   // 33.33 x 2 + 33.34
    [InlineData(2000, 4)]  // evenly divisible: 500 x 4, unchanged
    [InlineData(100, 1)]   // a one-installment advance
    public async Task TheFinalInstallment_RecoversTheCentRemainder_SoTheAdvanceClosesAtExactlyZero(int principal, int installments)
    {
        var (db, conn, tenantId, emp, runs) = await SeedAsync(runMonths: installments, maxInstallments: 6);
        await using var _ = conn; await using var __ = db;

        var advanceId = await RequestApproveAndPayAsync(db, tenantId, emp, principal, installments);

        var scheduleTotal = (await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == advanceId).ToListAsync()).Sum(x => x.AmountDue);
        scheduleTotal.Should().Be(principal, "the schedule itself sums exactly to the principal");

        foreach (var run in runs)
        {
            (await Payroll(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
            db.ChangeTracker.Clear();
        }

        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == advanceId);
        adv.TotalRepaid.Should().Be(principal, "exactly the principal was withheld across the scheduled deductions");
        adv.OutstandingBalance.Should().Be(0m, "no cent is left for an extra deduction");
        adv.Status.Should().Be("Closed");

        var rows = await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == advanceId).OrderBy(x => x.InstallmentNumber).ToListAsync();
        rows.Should().OnlyContain(i => i.Status == "Paid");
        rows.Sum(i => i.AmountPaid).Should().Be(principal);
        rows.Should().OnlyContain(i => i.AmountPaid == i.AmountDue, "each row recovers exactly what the schedule says is due");
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One SAR company in KSA, one employee on 15,800 a month, an active policy (50%, up to
    /// <paramref name="maxInstallments"/> instalments) and one Draft payroll run per month starting this month.</summary>
    private static async Task<(ZayraDbContext Db, SqliteConnection Conn, Guid TenantId, Employee Employee, List<PayrollRun> Runs)> SeedAsync(
        int runMonths, int maxInstallments)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var firstMonth = new DateOnly(Today.Year, Today.Month, 1);
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Deduction Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            IsActive = true, DefaultCurrency = "SAR",
        };
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "BASE", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.AddRange(company, structure);
        await db.SaveChangesAsync();
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "D001", FullName = "Reem Al-Harbi",
            Status = "Active", JoiningDate = new DateTime(2022, 3, 1), WorkEmail = "reem@deduction.test",
            Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id, Currency = "SAR",
            BasicSalary = 12_000m, HousingAllowance = 3_000m, TransportAllowance = 800m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = "MOL-D001", SalaryCurrency = "SAR",
        });
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tenantId, PolicyName = "Standard", MaxPercentageOfSalary = 50, MaxAdvancesPerYear = 2,
            MinServiceMonths = 6, AllowInstallments = true, MaxInstallments = maxInstallments, CooldownMonths = 3, RequiresApproval = true, IsActive = true,
        });
        var runs = Enumerable.Range(0, runMonths).Select(i => firstMonth.AddMonths(i)).Select(month => new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = month.Year, Month = month.Month, Status = "Draft", CreatedByUserId = PayrollMaker,
        }).ToList();
        db.PayrollRuns.AddRange(runs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, conn, tenantId, emp, runs);
    }

    /// <summary>Requested by HR with today as the first repayment date, then approved by a different HR user. Nothing is paid.</summary>
    private static async Task<Guid> RequestAndApproveAsync(ZayraDbContext db, Guid tenantId, Employee emp, decimal principal, int installments)
    {
        (await Advances(db, tenantId, Requester).Create(
            new CreateAdvanceRequest(emp.PublicId, emp.FullName, principal, installments == 1 ? "OneTime" : "Installments", installments, Today, "school fees", emp.Id),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var advanceId = (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Id;
        (await Advances(db, tenantId, Approver).Approve(
            advanceId, new AdvanceApproveRequest(principal, installments, Today), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        return advanceId;
    }

    /// <summary><see cref="RequestAndApproveAsync"/>, then Finance records the payout today: the agreed first repayment date
    /// (today) is kept, so this month's run collects the first instalment.</summary>
    private static async Task<Guid> RequestApproveAndPayAsync(ZayraDbContext db, Guid tenantId, Employee emp, decimal principal, int installments)
    {
        var advanceId = await RequestAndApproveAsync(db, tenantId, emp, principal, installments);
        (await Finance(db, tenantId).Payout(advanceId, new AdvancePayoutRequest(Today, $"BANK-{advanceId:N}"[..20], null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        return advanceId;
    }

    private static AdvancesController Advances(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithUser(new AdvancesController(db, new _DedScope()), tenantId, userId, "HR Manager");

    private static AdvancesController Finance(ZayraDbContext db, Guid tenantId) =>
        WithUser(new AdvancesController(db, new _DedScope()), tenantId, Payer, "Finance");

    private static AdvancesController WithUser(AdvancesController controller, Guid tenantId, Guid userId, string role)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "hr"),
                    new Claim(ClaimTypes.Role, role),
                    new Claim("permission", "loans.write"),
                }, "test")),
            },
        };
        return controller;
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
            {
                new("tenant_id", tenantId.ToString()),
                new(ClaimTypes.NameIdentifier, PayrollMaker.ToString()),
                new(ClaimTypes.Name, "maker"),
                new(ClaimTypes.Role, "Payroll Officer"),
                new("permission", "payroll.write"),
                new("permission", "payroll.approve"),
            }, "test")),
        };
        return new PayrollController(
            db, new _DedScope(), new _DedHttp(http), new _DedNotifications(),
            new _DedPackResolver(), _DedRules.Rules, new _DedLetters(), new _DedStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }
}

// ── Test doubles (file-scoped; same shape as PayrollRecoveryB3SmeTests) ─────────────────────────────

file static class _DedRules
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

file sealed class _DedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _DedHttp : IHttpContextAccessor
{
    public _DedHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _DedNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct)
        => Task.CompletedTask;
}

file sealed class _DedPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_DedRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _DedLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _DedStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
