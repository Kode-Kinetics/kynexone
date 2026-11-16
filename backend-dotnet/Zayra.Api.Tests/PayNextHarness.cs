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
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Shared fixture for the held-pay and unpaid-leave payroll tests (PayHeldPayTests, PayLeaveArrearsTests,
/// PayLeaveFullPeriodCapTests, PayLeaveCancelRefundTests, LeaveSliceSplitReportTests). A KSA company on SQLite in
/// memory, driven through PayrollController exactly as PayPopulationRunTests is. Basic 6,000 + housing 1,500.
/// </summary>
internal static class PayNext
{
    public static readonly Guid Maker = Guid.NewGuid();
    public static readonly Guid Checker = Guid.NewGuid();
    public const decimal Basic = 6_000m;
    public const decimal Housing = 1_500m;

    public static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    public static DefaultHttpContext Ctx(Guid tenantId, Guid userId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userId == Maker ? "maker" : "checker"),
            new(ClaimTypes.Role, "Admin"),
        };
        foreach (var p in permissions.Length > 0 ? permissions : new[] { "payroll.read", "payroll.write", "payroll.approve", "payroll.lock" })
            claims.Add(new Claim("permission", p));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

    public static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var http = Ctx(tenantId, userId);
        return new PayrollController(
            db, new PnScope(), new PnHttp(http), new PnNotifications(),
            new PnPackResolver(), PnRules.Rules, new PnLetters(), new PnStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    public sealed record Co(Guid TenantId, Guid CompanyId, Guid StructureId);

    public static async Task<Co> SeedCompany(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Held Pay KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "PN" + Guid.NewGuid().ToString("N")[..6], Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Co(tenantId, company.Id, structure.Id);
    }

    private static int _seq;

    public static async Task<Employee> AddEmployee(ZayraDbContext db, Co co, string status, DateTime? joined = null)
    {
        var n = Interlocked.Increment(ref _seq);
        var e = new Employee
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeCode = $"PN{n:D4}", FullName = $"Employee {n} ({status})",
            Status = status, JoiningDate = joined ?? new DateTime(2023, 1, 1),
            WorkEmail = $"pn{n}@pn.test", Nationality = "Saudi", ContractType = "Indefinite",
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = co.TenantId, EmployeeId = e.Id, SalaryStructureId = co.StructureId,
            BasicSalary = Basic, HousingAllowance = Housing,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = co.TenantId, EmployeeId = e.Id,
            Iban = "SA4420000001234567891234", MolId = $"MOL-{e.EmployeeCode}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return e;
    }

    public static async Task<PayrollRun> AddRun(ZayraDbContext db, Co co, int year, int month, string status = "Draft")
    {
        var run = new PayrollRun
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, Year = year, Month = month,
            Status = status, RunType = PayrollRunTypes.Regular, IncludesRecurringPay = true,
            CreatedByUserId = Maker,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return run;
    }

    public static async Task Process(ZayraDbContext db, Co co, Guid runId)
    {
        var result = await Payroll(db, co.TenantId, Maker).Process(runId, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((result as ObjectResult)?.Value));
        db.ChangeTracker.Clear();
    }

    /// <summary>Moves a run past processing (as Approve would), so nothing can process it again.</summary>
    public static async Task SetStatus(ZayraDbContext db, Guid runId, string status)
    {
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == runId);
        run.Status = status;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public static Task<PayrollSlip?> Slip(ZayraDbContext db, Guid runId, int employeeId) =>
        db.PayrollSlips.AsNoTracking().FirstOrDefaultAsync(s => s.RunId == runId && s.EmployeeId == employeeId);

    public static Task<List<PayrollValidationResult>> Findings(ZayraDbContext db, Guid runId) =>
        db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == runId).ToListAsync();

    public static Task<List<PayrollDeduction>> Deductions(ZayraDbContext db, Guid runId, int employeeId) =>
        db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == runId && d.EmployeeId == employeeId).ToListAsync();

    public static Task<List<PayrollEarning>> Earnings(ZayraDbContext db, Guid runId, int employeeId) =>
        db.PayrollEarnings.AsNoTracking().Where(d => d.PayrollRunId == runId && d.EmployeeId == employeeId).ToListAsync();

    public static async Task<decimal> DeductionSum(ZayraDbContext db, Guid runId, int employeeId, string code) =>
        (await Deductions(db, runId, employeeId)).Where(d => d.ComponentCode == code).Sum(d => d.Amount);

    public static async Task<decimal> EarningSum(ZayraDbContext db, Guid runId, int employeeId, string code) =>
        (await Earnings(db, runId, employeeId)).Where(d => d.ComponentCode == code).Sum(d => d.Amount);

    public static JsonElement Json(IActionResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize((result as ObjectResult)!.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    public static int Status(IActionResult result) => (result as ObjectResult)?.StatusCode ?? (result is OkResult ? 200 : 0);

    /// <summary>An approved unpaid leave written directly as its month slices.</summary>
    public static async Task<LeaveRequest> AddUnpaidLeave(ZayraDbContext db, Co co, int employeeId, DateOnly start, DateOnly end,
        params (string Period, decimal Days, decimal Amount, DateOnly From, DateOnly To, string Status)[] slices)
    {
        var leave = new LeaveRequest
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = employeeId, StartDate = start, EndDate = end,
            TotalDays = slices.Sum(s => s.Days), Status = "Approved", DayType = "Full",
        };
        db.LeaveRequests.Add(leave);
        foreach (var s in slices)
            db.LeavePayrollImpacts.Add(new LeavePayrollImpact
            {
                TenantId = co.TenantId, LeaveRequestId = leave.Id, EmployeeId = employeeId, PayPeriod = s.Period,
                ImpactType = CompanyPayrollRules.UnpaidLeaveImpactType, Days = s.Days, Amount = s.Amount, Status = s.Status,
                SliceFrom = s.From, SliceTo = s.To,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return leave;
    }

    public static async Task SetPayrollRule(ZayraDbContext db, Co co, string key, string value)
    {
        db.CompanyPayrollRules.Add(new CompanyPayrollRule
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, RuleKey = key, RuleValue = value,
            EffectiveFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}

internal static class PnRules
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

internal sealed class PnScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

internal sealed class PnHttp : IHttpContextAccessor
{
    public PnHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

internal sealed class PnNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct)
        => Task.CompletedTask;
}

internal sealed class PnPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(PnRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

internal sealed class PnLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

internal sealed class PnStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
