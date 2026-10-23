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
/// (<see cref="AdvanceRepaymentControlTests"/>): an advance requested and approved through the hardened endpoints is
/// deducted by <c>PayrollController.Process</c>, which retires the balance and stamps the installment it funded.
/// </summary>
public class AdvancePayrollDeductionTests
{
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly Guid PayrollMaker = Guid.NewGuid();

    [Fact]
    public async Task AnApprovedAdvance_IsDeductedByPayroll_AndItsFirstInstallmentIsStampedPaid()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        var periodStart = new DateOnly(now.Year, now.Month, 1);
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
            MinServiceMonths = 6, AllowInstallments = true, MaxInstallments = 4, CooldownMonths = 3, RequiresApproval = true, IsActive = true,
        });
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = now.Year, Month = now.Month, Status = "Draft", CreatedByUserId = PayrollMaker,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Request (HR, on the employee's behalf) and approve (a different HR user) through the real endpoints.
        (await Advances(db, tenantId, Requester).Create(
            new CreateAdvanceRequest(emp.PublicId, emp.FullName, 2_000m, "Installments", 4, periodStart, "school fees", emp.Id),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var advanceId = (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Id;
        (await Advances(db, tenantId, Approver).Approve(
            advanceId, new AdvanceApproveRequest(2_000m, 4, periodStart), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

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

    private static AdvancesController Advances(ZayraDbContext db, Guid tenantId, Guid userId) => new(db, new _DedScope())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "hr"),
                    new Claim(ClaimTypes.Role, "HR Manager"),
                    new Claim("permission", "loans.write"),
                }, "test")),
            },
        },
    };

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
