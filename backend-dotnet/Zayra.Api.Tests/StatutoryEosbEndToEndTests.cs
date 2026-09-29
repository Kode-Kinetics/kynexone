using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// S1/A1 END TO END, through the controller rather than the pack — because A1 lived as much in the
/// controller as in the calculator: <c>ResolveEosbWageBasisAsync</c> collapsed the package into a
/// scalar and handed the pack <c>new SalaryBreakdown(wage, 0, 0, 0)</c>, so a pack-only test could
/// never have caught it.
///
/// <para>Also answers the question the brief asks about already-computed settlements: what happens to
/// the money that has already been promised.</para>
/// </summary>
public class StatutoryEosbEndToEndTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Test User"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(
            db,
            new _S1Scope(),
            new _S1Accessor(httpCtx),
            new _S1Notifications(),
            new _S1KsaResolver(new StubRuleReader()),
            new StubRuleReader(),
            new _S1Letters(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    /// <summary>A KSA employee on basic 18,000 + housing 7,500 + transport 4,500 = SAR 30,000.</summary>
    private static int SeedKsaEmployee(ZayraDbContext db, Guid tenantId, DateTime joiningDate)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "S1 KSA Entity", CountryCode = "SA",
            DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "S1-001", FullName = "Noura Al-Qahtani",
            Status = "Active", Nationality = "SAU", JoiningDate = joiningDate, CompanyId = company.Id,
        };
        db.Employees.Add(emp);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, EosbMinYears = 1,
        });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            BasicSalary = 18_000m, HousingAllowance = 7_500m, TransportAllowance = 4_500m,
            Currency = "SAR", EffectiveDate = DateOnly.FromDateTime(joiningDate), IsActive = true,
        });
        db.SaveChanges();
        return emp.Id;
    }

    private static decimal EosbFrom(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return (decimal)ok.Value!.GetType().GetProperty("eosbAmount")!.GetValue(ok.Value)!;
    }

    private static T? Prop<T>(IActionResult result, string name)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return (T?)ok.Value!.GetType().GetProperty(name)?.GetValue(ok.Value);
    }

    [Fact]
    public async Task CalculateEosb_AwardsOnTheFullLastWage_NotOnBasicAlone()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employeeId = SeedKsaEmployee(db, tenantId, new DateTime(2014, 1, 1));
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.CalculateEosb(
            new EosbCalculationRequest(employeeId, new DateTime(2026, 1, 1), null),
            CancellationToken.None);

        // 12 years: 5 × ½ month + 7 × 1 month = 9.5 months of the LAST WAGE (SAR 30,000).
        Assert.Equal(9.5m * 30_000m, EosbFrom(result));
        Assert.Equal(30_000m, Prop<decimal>(result, "eligibleSalary"));
        // The pre-S1 answer, on basic alone.
        Assert.NotEqual(9.5m * 18_000m, EosbFrom(result));
    }

    [Fact]
    public async Task CalculateEosb_AnnouncesTheCounselDefaultsInsteadOfApplyingThemSilently()
    {
        // The wage base is now part of the ANSWER. Pre-S1 the only record of how the base was chosen
        // was a reason string written into a snapshot column nobody reads; a [COUNSEL] default that
        // moves the award has to be visible to whoever signs it off.
        //
        // Note the interaction worth knowing: with no pay_components rows for this tenant,
        // LoadPayComponentsAsync falls back to the COMPILED catalog, so the configured wage is
        // BASIC + HOUSING = 25,500 — and the statutory base (which also includes transport by the
        // [COUNSEL] default) is 30,000. max(statutory, configured) = 30,000. Configuration cannot
        // lower the award below statute; here statute is the higher of the two and wins.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employeeId = SeedKsaEmployee(db, tenantId, new DateTime(2014, 1, 1));
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.CalculateEosb(
            new EosbCalculationRequest(employeeId, new DateTime(2026, 1, 1), null),
            CancellationToken.None);

        var notices = Prop<object>(result, "statutoryNotices");
        Assert.NotNull(notices);
        var text = string.Join(" | ", ((System.Collections.IEnumerable)notices!).Cast<object>().Select(o => o.ToString()));
        Assert.Contains("[COUNSEL-KSA]", text);
        Assert.Contains("transport allowance", text);
        Assert.Contains("eosb.include_transport", text);   // the rule key to flip is named
        Assert.Equal(30_000m, Prop<decimal>(result, "eligibleSalary"));
    }

    /// <summary>
    /// THE MONEY QUESTION. A settlement that has posted its accrual journal is IMMUTABLE: the
    /// endpoint 409s before <c>BuildFinalSettlementPlanAsync</c> is reached, so no already-promised
    /// figure is silently restated by this change. Only a Draft or PendingApproval settlement — one
    /// the operator is still iterating on and which has accrued nothing — recomputes, and it
    /// recomputes to the lawful number. A settlement that has accrued must be CANCELLED (which posts
    /// the contra) before a corrected one can be computed, so every correction leaves an audit trail.
    /// </summary>
    [Fact]
    public async Task FinalSettlement_ThatHasAlreadyAccrued_IsNotRecomputed()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employeeId = SeedKsaEmployee(db, tenantId, new DateTime(2014, 1, 1));
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employeeId, SeparationType = "Termination",
            Status = "InProgress", LastWorkingDay = new DateOnly(2026, 1, 1), CreatedAtUtc = DateTime.UtcNow,
        };
        db.EmployeeOffboardings.Add(offboarding);

        // A settlement computed and APPROVED under the old basic-only rule.
        const decimal preFixGratuity = 9.5m * 18_000m;
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement
        {
            TenantId = tenantId, EmployeeId = employeeId, OffboardingId = offboarding.Id,
            Status = FinalSettlementStatuses.Approved, GratuityAmount = preFixGratuity,
            NetPayable = preFixGratuity, Currency = "SAR",
        });
        db.SaveChanges();

        var ctrl = MakeCtrl(db, tenantId);
        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(employeeId, default, 0, null, 0, 0m, 0m, null), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        var stored = db.EmployeeFinalSettlements.Single(s => s.EmployeeId == employeeId);
        Assert.Equal(preFixGratuity, stored.GratuityAmount);   // untouched
    }
}

file sealed class _S1Scope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _S1Accessor : IHttpContextAccessor
{
    public _S1Accessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _S1Notifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _S1Letters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _S1KsaResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _S1KsaResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaDeductionCalculator(_rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(_rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
