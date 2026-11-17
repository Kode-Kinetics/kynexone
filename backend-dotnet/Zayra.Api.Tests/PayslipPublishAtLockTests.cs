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
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Lock generates and publishes every slip's payslip row, once.
///
/// <para>Before: the web self-service listed a slip from Lock onward (PayrollSlip.Status == "Final"), while the mobile app needed a
/// Payslip row that only the manual "Generate payslips" button created, and Lock only published rows that already existed. An
/// operator who locked without clicking the button left every employee with a payslip on the web and nothing on the phone.</para>
///
/// <para>Now: Lock, inside its row-locked transaction, creates the Payslip row (with its lines) for every slip that lacks one,
/// rebuilds the lines of any row generated earlier from the locked figures, and publishes all of them. One row per employee per
/// run, however often it is generated or locked. Web and mobile then show the same payslips with the same net.</para>
/// </summary>
public class PayslipPublishAtLockTests
{
    private static readonly Guid Preparer = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    // ══ 1. Lock alone produces a published payslip for every employee ══════════════════════════════

    [Fact]
    public async Task LockWithoutGeneratingPayslips_PublishesOnePayslipPerEmployee_WithTheLockedNet()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var tid = Guid.NewGuid();
        var (emps, run) = await SeedRun(db, tid);

        await Process(db, tid, run.Id);
        await ApproveDirect(db, run.Id);
        (await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payslips = await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).ToListAsync();
        payslips.Select(p => p.EmployeeId).Should().BeEquivalentTo(emps.Select(e => e.Id), "Lock creates a payslip for every slip");
        payslips.Should().OnlyContain(p => p.IsPublishedToEss && p.PublishedAtUtc != null, "a locked run's payslips are published");
        payslips.Should().OnlyContain(p => p.TenantId == tid);

        var slips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).ToDictionaryAsync(s => s.EmployeeId);
        foreach (var p in payslips)
        {
            var lines = await db.PayslipComponents.AsNoTracking().Where(c => c.PayslipId == p.Id).ToListAsync();
            lines.Where(c => c.ComponentType == "Net").Sum(c => c.Amount).Should().Be(slips[p.EmployeeId].NetSalary, "the payslip's net is the locked slip's net");
            lines.Should().Contain(c => c.ComponentType == "Earning", "the payslip carries its earning lines, as the Generate button builds them");
        }
    }

    // ══ 2. Idempotent: generate-then-lock, lock twice, re-lock after reopen ═══════════════════════

    [Fact]
    public async Task GenerateThenLock_AndLockTwice_NeverDuplicateAPayslip()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var tid = Guid.NewGuid();
        var (emps, run) = await SeedRun(db, tid);

        await Process(db, tid, run.Id);
        (await Payroll(db, tid, Preparer).GeneratePayslips(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var before = await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).Select(p => new { p.Id, p.PayslipNumber }).ToListAsync();
        await ApproveDirect(db, run.Id);
        (await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>("a second lock is refused");
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Preparer).GeneratePayslips(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var after = await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).ToListAsync();
        after.Should().HaveCount(emps.Count, "one payslip per employee, however often it is generated or locked");
        after.Select(p => new { p.Id, p.PayslipNumber }).Should().BeEquivalentTo(before, "the payslip generated before Lock keeps its identity");
        after.Should().OnlyContain(p => p.IsPublishedToEss);
        foreach (var p in after)
            (await db.PayslipComponents.AsNoTracking().CountAsync(c => c.PayslipId == p.Id && c.ComponentType == "Net")).Should().Be(1, "lines are rebuilt, never appended twice");
    }

    [Fact]
    public async Task PayslipGeneratedBeforeAReopen_IsRebuiltAtLock_FromTheReprocessedFigures()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var tid = Guid.NewGuid();
        var (emps, run) = await SeedRun(db, tid);
        var first = emps[0];

        await Process(db, tid, run.Id);
        (await Payroll(db, tid, Preparer).GeneratePayslips(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        await ApproveDirect(db, run.Id);

        // Reopen, correct the salary, re-process, approve, then lock — WITHOUT pressing Generate again.
        (await Payroll(db, tid, Approver).ReopenRun(run.Id, new PayrollReasonRequest("Correcting the basic salary before lock."), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var structure = await db.EmployeeSalaryStructures.SingleAsync(s => s.EmployeeId == first.Id);
        structure.BasicSalary += 1_000m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Process(db, tid, run.Id);
        await ApproveDirect(db, run.Id);
        (await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == first.Id);
        var payslip = await db.Payslips.AsNoTracking().SingleAsync(p => p.PayrollRunId == run.Id && p.EmployeeId == first.Id);
        (await db.PayslipComponents.AsNoTracking().Where(c => c.PayslipId == payslip.Id && c.ComponentType == "Net").ToListAsync()).Sum(c => c.Amount)
            .Should().Be(slip.NetSalary, "the published payslip shows the locked figures, not the ones generated before the reopen");
        (await db.Payslips.AsNoTracking().CountAsync(p => p.PayrollRunId == run.Id)).Should().Be(emps.Count);
    }

    // ══ 3. Web and mobile show the same payslips ══════════════════════════════════════════════════

    [Fact]
    public async Task AfterLock_TheEmployeeSeesTheSamePayslip_OnWebAndMobile()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var tid = Guid.NewGuid();
        var (emps, run) = await SeedRun(db, tid);

        await Process(db, tid, run.Id);
        await ApproveDirect(db, run.Id);
        (await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        foreach (var e in emps)
        {
            var web = await WebPayslips(db, tid, e.Id);
            var mobile = await MobilePayslips(db, tid, e.Id);
            web.Should().ContainSingle("the web shows the locked month");
            mobile.Should().BeEquivalentTo(web, "the phone shows exactly what the web shows: same month, same net");
        }
    }

    // ══ 4. A voided run: withdrawn on both channels, as today ═════════════════════════════════════

    [Fact]
    public async Task VoidedRun_ItsPayslipsAreWithdrawn_OnWebAndMobile()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var tid = Guid.NewGuid();
        var (emps, run) = await SeedRun(db, tid);

        await Process(db, tid, run.Id);
        await ApproveDirect(db, run.Id);
        (await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("wrong month"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).ToListAsync())
            .Should().HaveCount(emps.Count).And.OnlyContain(p => !p.IsPublishedToEss, "the void un-publishes every payslip Lock published");
        foreach (var e in emps)
        {
            (await WebPayslips(db, tid, e.Id)).Should().BeEmpty("a voided month is not shown on the web");
            (await MobilePayslips(db, tid, e.Id)).Should().BeEmpty("a voided month is not shown on the phone");
        }
    }

    // ══ Harness ═══════════════════════════════════════════════════════════════════════════════════

    private sealed record Seen(int Year, int Month, decimal Net);

    private static async Task<List<Seen>> WebPayslips(ZayraDbContext db, Guid tid, int employeeId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("employee_id", employeeId.ToString()), new("permission", "ess.read"),
        };
        var ctrl = new EmployeeSelfServiceController(db, new _PlLetters(), new PdfRenderGate(1),
            new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db)),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(db, new _PlNotifications(), new _PlHttp()),
            new HrLetterIssuer(db, new _PlLetters(), new NullDocumentStorage()), new NullDocumentStorage())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
        var res = await ctrl.Payslips(CancellationToken.None);
        var rows = (IEnumerable<EssPayslipSummaryDto>)((OkObjectResult)res.Result!).Value!;
        db.ChangeTracker.Clear();
        return rows.Select(r => new Seen(r.Year, r.Month, r.NetSalary)).ToList();
    }

    private static async Task<List<Seen>> MobilePayslips(ZayraDbContext db, Guid tid, int employeeId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new("employee_id", employeeId.ToString()),
        };
        var ctrl = new MobileController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
        var res = await ctrl.MyPayslips(employeeId, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(((OkObjectResult)res).Value);
        db.ChangeTracker.Clear();
        return json.EnumerateArray()
            .Select(x => new Seen(x.GetProperty("Year").GetInt32(), x.GetProperty("Month").GetInt32(), x.GetProperty("TotalNetSalary").GetDecimal()))
            .ToList();
    }

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static async Task Process(ZayraDbContext db, Guid tid, Guid runId)
    {
        (await Payroll(db, tid, Preparer).Process(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    /// <summary>The approval ladder is not what this class tests; the run is moved to Approved as PayrollRecoveryB3Tests does.</summary>
    private static async Task ApproveDirect(ZayraDbContext db, Guid runId)
    {
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == runId);
        run.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userId == Preparer ? "preparer" : "approver"),
            new(ClaimTypes.Role, userId == Preparer ? "Payroll Officer" : "Finance Controller"),
            new("permission", "payroll.export"), new("permission", "payroll.lock"),
            new("permission", "payroll.write"), new("permission", "payroll.approve"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new PayrollController(db, new _PlScope(), new HttpContextAccessor { HttpContext = httpCtx }, new _PlNotifications(),
            new _PlPackResolver(), _PlRules.Rules, new _PlLetters(), new NullDocumentStorage(), new PdfRenderGate(4))
        {
            ControllerContext = new ControllerContext { HttpContext = httpCtx },
        };
    }

    private static async Task<(List<Employee> emps, PayrollRun run)> SeedRun(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Payslip Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();

        var emps = new List<Employee>();
        foreach (var (code, name, nationality, basic) in new[] { ("E001", "Ali Hassan", "SAU", 10_000m), ("E002", "Ravi Kumar", "Indian", 6_543.21m) })
        {
            var e = new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = code, FullName = name, Status = "Active",
                JoiningDate = new DateTime(2023, 1, 1), WorkEmail = $"{code}@test.com", Nationality = nationality, ContractType = "Indefinite",
            };
            db.Employees.Add(e);
            await db.SaveChangesAsync();
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = structure.Id,
                BasicSalary = basic, HousingAllowance = 2_000m, TransportAllowance = 500m, EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{code}", SalaryCurrency = "SAR",
            });
            emps.Add(e);
        }
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft", CreatedByUserId = Preparer };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (emps, run);
    }
}

file static class _PlRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);
}

file sealed class _PlScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _PlNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _PlHttp : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

file sealed class _PlPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_PlRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _PlLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
