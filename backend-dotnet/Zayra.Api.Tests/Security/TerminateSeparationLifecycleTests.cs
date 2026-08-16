using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using Zayra.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// D1 — a direct termination must produce authoritative separation data.
///
/// <para>THE DEFECT. <c>POST /employees/{id}/terminate</c> set <c>Status = "Terminated"</c> and created
/// nothing else. <c>/final-settlement</c> requires an <c>EmployeeOffboarding</c> carrying a
/// <c>LastWorkingDay</c> (400 <c>not_a_leaver</c> otherwise), and <c>POST /api/offboarding</c> refuses a
/// non-occupying employee — and "Terminated" is not occupying. A directly-terminated employee could
/// therefore never be settled and never be routed back into offboarding: an unsettleable dead end, and
/// a hard blocker for migrating staff already terminated in a prior system.</para>
///
/// <para>Wave 0's hardening of <c>/final-settlement</c> is what made this reachable, so it is fixed at
/// the lifecycle source rather than by relaxing that gate — the gate is correct.</para>
/// </summary>
public class TerminateSeparationLifecycleTests
{
    private static readonly DateOnly Lwd = new(2026, 7, 31);

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (EmployeeManagementService Svc, Employee Emp) Seed(ZayraDbContext db, Guid tenantId)
    {
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-100", FullName = "Ahmed Al-Rashidi",
            Department = "Finance", Designation = "Analyst",
            Status = EmployeeStatuses.Active, Nationality = "SAU",
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        db.SaveChanges();
        var svc = new EmployeeManagementService(
            db, new Zayra.Api.Infrastructure.Audit.AuditService(db), new NullDocumentStorage(),
            TestNotifications.For(db));
        return (svc, emp);
    }

    private static readonly Zayra.Api.Application.Auth.RequestContext Ctx =
        new("127.0.0.1", "tests", UserId: Guid.NewGuid());

    private static EmployeeStatusChangeRequest TerminateCmd(string? separationType = null) =>
        new("Terminated", Lwd, "Role eliminated", separationType);

    // ── One authoritative separation ──────────────────────────────────────────

    [Fact]
    public async Task DirectTerminate_CreatesExactlyOneAuthoritativeSeparation()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        var separations = await db.EmployeeOffboardings.Where(o => o.EmployeeId == emp.Id).ToListAsync();
        separations.Should().HaveCount(1);
        var sep = separations[0];
        sep.LastWorkingDay.Should().Be(Lwd, "the last working day is derived from the command's effective date");
        sep.SeparationType.Should().Be("Termination", "an employer-initiated command defaults conservatively");
        sep.Status.Should().Be("InProgress");
        sep.NoticePeriodDays.Should().Be(0, "a direct termination is immediate — no notice is being served");
    }

    [Fact]
    public async Task DirectTerminate_HonoursAnExplicitSeparationType()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd("Resignation"), Ctx, default);

        (await db.EmployeeOffboardings.SingleAsync(o => o.EmployeeId == emp.Id))
            .SeparationType.Should().Be("Resignation",
                "the separation type decides the gratuity, so it comes from the explicit command");
    }

    // ── Preserve an existing offboarding ──────────────────────────────────────

    [Fact]
    public async Task ExistingInProgressOffboarding_IsPreserved_NotOverwritten()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        var approvedLwd = new DateOnly(2026, 9, 30);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = emp.Id, SeparationType = "Resignation",
            Reason = "Approved resignation with notice", Status = "InProgress",
            LastWorkingDay = approvedLwd, NoticePeriodDays = 60, CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        var sep = await db.EmployeeOffboardings.SingleAsync(o => o.EmployeeId == emp.Id);
        sep.LastWorkingDay.Should().Be(approvedLwd, "an approved date must never be overwritten by a status command");
        sep.SeparationType.Should().Be("Resignation");
        sep.NoticePeriodDays.Should().Be(60);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatedTerminate_IsIdempotent()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        (await db.EmployeeOffboardings.CountAsync(o => o.EmployeeId == emp.Id))
            .Should().Be(1, "a retried termination must not create duplicate separations");
    }

    // ── Rehire gets a NEW service period ──────────────────────────────────────

    [Fact]
    public async Task RehireThenTerminate_CreatesANewSeparation_NotReusingTheCompletedOne()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        // First service period, closed out.
        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        var first = await db.EmployeeOffboardings.SingleAsync(o => o.EmployeeId == emp.Id);
        first.Status = "Completed";
        await db.SaveChangesAsync();

        // Rehired, then separated again later.
        await svc.ActivateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Active", new DateOnly(2026, 9, 1), "Rehired"),
            Ctx, default);
        await svc.TerminateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Terminated", new DateOnly(2027, 3, 31), "Second exit"),
            Ctx, default);

        var all = await db.EmployeeOffboardings.Where(o => o.EmployeeId == emp.Id).ToListAsync();
        all.Should().HaveCount(2, "a new service period must get its own separation, not reopen a closed one");
        all.Should().ContainSingle(o => o.Status == "Completed" && o.LastWorkingDay == Lwd);
        all.Should().ContainSingle(o => o.Status == "InProgress" && o.LastWorkingDay == new DateOnly(2027, 3, 31));
    }

    // ── Atomicity: status and separation cannot disagree ──────────────────────

    [Fact]
    public async Task StatusAndSeparation_AreCommittedTogether()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        // Re-read from a FRESH context view of the store: whatever is durable must be consistent.
        var stored = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id);
        var sep = await db.EmployeeOffboardings.AsNoTracking().SingleOrDefaultAsync(o => o.EmployeeId == emp.Id);

        stored.Status.Should().Be("Terminated");
        sep.Should().NotBeNull(
            "a terminated employee with no separation is the dead-end state D1 exists to make unreachable");
    }

    // ── The salary evidence EOSB needs must survive the termination ───────────

    [Fact]
    public async Task Terminate_DoesNotDeactivateSalaryEvidenceNeededByEosb()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, BasicSalary = 10_000m, Currency = "SAR",
            EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
        });
        await db.SaveChangesAsync();

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        (await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == emp.Id))
            .IsActive.Should().BeTrue(
                "EOSB and final settlement read the active salary row for a Terminated employee — " +
                "deactivating it here would corrupt the gratuity");
    }

    // ── THE ACCEPTANCE PROOF: terminate → final settlement no longer dead-ends ──

    [Fact]
    public async Task DirectTerminate_ThenFinalSettlement_NoLongerFailsNotALeaver()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();

        // A complete, settleable KSA employee: legal entity, EOSB enabled, an active salary row.
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Test KSA Entity", CountryCode = "SA",
            DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-200", FullName = "Ahmed Al-Rashidi",
            Status = EmployeeStatuses.Active, Nationality = "SAU", CompanyId = company.Id,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, EosbMinYears = 1,
        });
        db.SaveChanges();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, BasicSalary = 10_000m, Currency = "SAR",
            EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
        });
        db.SaveChanges();

        var svc = new EmployeeManagementService(
            db, new Zayra.Api.Infrastructure.Audit.AuditService(db), new NullDocumentStorage(),
            TestNotifications.For(db));

        // The ONLY separation action taken is the direct terminate command.
        await svc.TerminateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Terminated", new DateOnly(2023, 1, 1), "Role eliminated"),
            Ctx, default);

        var payroll = MakePayrollCtrl(db, tenantId);
        var result = await payroll.FinalSettlement(
            new FinalSettlementRequest(emp.Id, new DateOnly(2023, 1, 1)), default);

        result.Should().NotBeOfType<BadRequestObjectResult>(
            "the direct-terminate path must now satisfy the leaver gate — this is the dead end D1 closes");
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value!;
        ((decimal)body.GetType().GetProperty("eosbAmount")!.GetValue(body)!)
            .Should().Be(15_000m, "3 years × 0.5 month × 10,000 — the Art.84 award for an employer termination");
    }

    private static PayrollController MakePayrollCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.Name, "Payroll User"),
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "test"));
        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal };
        var rules = new StubRuleReader();
        var ctrl = new PayrollController(
            db,
            new _D1UnrestrictedScope(),
            new _D1HttpAccessor(httpCtx),
            new _D1NullNotifications(),
            new _D1KsaPackResolver(rules),
            rules,
            new _D1NullLetterService(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }
}

// ── File-scoped doubles (isolated from other test files) ─────────────────────

file sealed class _D1UnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(
        System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _D1HttpAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
{
    public _D1HttpAccessor(Microsoft.AspNetCore.Http.HttpContext ctx) => HttpContext = ctx;
    public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
}

file sealed class _D1NullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _D1NullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _D1KsaPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _D1KsaPackResolver(StubRuleReader rules) => _rules = rules;
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaDeductionCalculator(_rules) : new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaEndOfServiceCalculator(_rules) : new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}
