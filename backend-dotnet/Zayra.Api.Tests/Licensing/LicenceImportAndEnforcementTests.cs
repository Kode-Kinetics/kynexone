using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Licensing;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Licensing;

/// <summary>WP3.4–3.7: importing, re-verifying on every load, the guard, the module cap and the employee seat.</summary>
public class LicenceImportAndEnforcementTests : IDisposable
{
    private readonly LicenceTestKit _kit = new();
    private readonly ServiceProvider _services;
    private readonly Guid _organisationId;

    public LicenceImportAndEnforcementTests()
    {
        var databaseName = $"licence-{Guid.NewGuid():N}";
        var collection = new ServiceCollection();
        collection.AddDbContext<ZayraDbContext>(o => o.UseInMemoryDatabase(databaseName));
        collection.AddMemoryCache();
        _services = collection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var organisation = new Tenant { Name = "Organisation", Slug = "main", AccountType = TenantAccountTypes.SingleCompany };
        db.Tenants.Add(organisation);
        db.Companies.Add(new Company { TenantId = organisation.Id, LegalNameEn = "First Co", CountryCode = "SA", IsActive = true });
        db.SaveChanges();
        _organisationId = organisation.Id;
    }

    public void Dispose()
    {
        _services.Dispose();
        _kit.Dispose();
    }

    private (LicenceService Licence, LicenceImporter Importer, ZayraDbContext Db, IServiceScope Scope) Arrange(Func<DateTime>? clock = null)
    {
        var licence = LicenceTestServices.RealService(_services.GetRequiredService<IServiceScopeFactory>(), _kit, clock);
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return (licence, new LicenceImporter(db, _kit.Verifier(), licence, _services.GetRequiredService<IMemoryCache>()), db, scope);
    }

    // ── Import ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Importing_InstallsTheLicence_AppliesTheAccountType_AndAudits()
    {
        var (licence, importer, db, scope) = Arrange();
        using var _ = scope;

        var result = await importer.ImportAsync(_kit.SignJson(LicenceTestKit.Payload()), null, "test-admin", default);

        result.Outcome.Should().Be(LicenceImportOutcome.Imported, string.Join(" ", result.Problems));
        licence.State.Should().Be(LicenceState.Valid);
        licence.Current.MaxEmployees.Should().Be(100);
        (await db.InstallationLicences.SingleAsync()).Status.Should().Be(InstallationLicenceStatuses.Active);
        (await db.Tenants.AsNoTracking().SingleAsync()).AccountType.Should().Be(TenantAccountTypes.Group);
        (await db.AdminAuditLogs.IgnoreQueryFilters().SingleAsync()).Action.Should().Be("LicenceImported");
        (await InstallationIdentity.GetAsync(db, default)).Should().NotBeNull("the installation id exists once a licence is used");
    }

    [Fact]
    public async Task ImportingTheSameLicenceAgain_IsANoOp()
    {
        var (_, importer, db, scope) = Arrange();
        using var __ = scope;
        var json = _kit.SignJson(LicenceTestKit.Payload());

        await importer.ImportAsync(json, null, "a", default);
        var again = await importer.ImportAsync(json, null, "a", default);

        again.Outcome.Should().Be(LicenceImportOutcome.AlreadyActive);
        (await db.InstallationLicences.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ANewerLicence_SupersedesTheOld()
    {
        var (licence, importer, db, scope) = Arrange();
        using var _ = scope;

        await importer.ImportAsync(_kit.SignJson(LicenceTestKit.Payload()), null, "a", default);
        await importer.ImportAsync(_kit.SignJson(LicenceTestKit.Payload(b => b.With(p => p with { MaxEmployees = 250 }))), null, "a", default);

        var rows = await db.InstallationLicences.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Count(r => r.Status == InstallationLicenceStatuses.Active).Should().Be(1);
        licence.Current.MaxEmployees.Should().Be(250);
    }

    [Fact]
    public async Task AnInvalidLicence_IsRejected_AuditedAndChangesNothing()
    {
        var (licence, importer, db, scope) = Arrange();
        using var _ = scope;
        using var forger = new LicenceTestKit();

        var result = await importer.ImportAsync(forger.SignJson(LicenceTestKit.Payload()), null, "a", default);

        result.Outcome.Should().Be(LicenceImportOutcome.Rejected);
        (await db.InstallationLicences.CountAsync()).Should().Be(0);
        licence.State.Should().Be(LicenceState.Missing);
        (await db.AdminAuditLogs.IgnoreQueryFilters().SingleAsync()).Action.Should().Be("LicenceImportRejected");
    }

    [Fact]
    public async Task ASingleCompanyLicence_IsRejected_WhileTheOrganisationRunsSeveralCompanies()
    {
        var (_, importer, db, scope) = Arrange();
        using var _ = scope;
        db.Companies.Add(new Company { TenantId = _organisationId, LegalNameEn = "Second Co", CountryCode = "SA", IsActive = true });
        await db.SaveChangesAsync();

        var result = await importer.ImportAsync(_kit.SignJson(LicenceTestKit.Payload(b => b.With(p => p with { AccountType = "SingleCompany" }))), null, "a", default);

        result.Outcome.Should().Be(LicenceImportOutcome.Rejected);
        result.Problems.Should().ContainMatch("*2 active companies*");
    }

    // ── Tamper resistance ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EditingTheStoredLicence_UnlicensesTheInstallation_RatherThanRaisingItsLimits()
    {
        var (licence, importer, db, scope) = Arrange();
        using var _ = scope;
        await importer.ImportAsync(_kit.SignJson(LicenceTestKit.Payload()), null, "a", default);

        var row = await db.InstallationLicences.SingleAsync();
        row.Payload = Base64Url.Encode(LicenceTestKit.Payload(b => b.With(p => p with { MaxEmployees = 1_000_000 })).ToUtf8Bytes());
        await db.SaveChangesAsync();
        await licence.RefreshAsync(default);

        licence.State.Should().Be(LicenceState.Missing, "the signature no longer covers the stored payload");
        licence.Current.MaxEmployees.Should().Be(1);
    }

    [Fact]
    public async Task EditingTenantFeatureFlags_CannotTurnOnAnUnlicensedModuleOrFeature()
    {
        var (licence, importer, db, scope) = Arrange();
        using var _ = scope;
        await importer.ImportAsync(_kit.SignJson(LicenceTestKit.Payload(b => b.With(p => p with { Modules = [], OptInFeatures = [] }))), null, "a", default);
        var optional = ModuleCatalog.All.First(m => m.Lock == ModuleLock.Optional).Key;
        db.TenantFeatureFlags.AddRange(
            new TenantFeatureFlag { TenantId = _organisationId, FeatureKey = optional, IsEnabled = true },
            new TenantFeatureFlag { TenantId = _organisationId, FeatureKey = FeatureKeys.Learning, IsEnabled = true });
        await db.SaveChangesAsync();

        var state = await new TenantModuleService(db, _services.GetRequiredService<IMemoryCache>(), licence: licence)
            .GetStateAsync(_organisationId);

        state.IsEnabled(optional).Should().BeFalse("the licence does not grant it, whatever the row says");
        state.IsEnabled(FeatureKeys.Learning).Should().BeFalse();
        state.IsEnabled(ModuleCatalog.All.First(m => m.Lock == ModuleLock.Core).Key).Should().BeTrue();
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────────────

    private static async Task<(IActionResult? Result, HttpContext Http)> RunGuardAsync(ILicenceService licence, string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ResourceExecutingContext(action, [], []);
        await new LicenceGuardFilter(licence, NullLogger<LicenceGuardFilter>.Instance)
            .OnResourceExecutionAsync(context, () => Task.FromResult(new ResourceExecutedContext(action, [])));
        return (context.Result, http);
    }

    [Fact]
    public async Task WithoutALicence_BusinessRoutesAre402_ButSignInAndTheLicenceRouteStayOpen()
    {
        var none = new FixedLicenceService(null);

        var (refused, _) = await RunGuardAsync(none, "/api/employees");
        var body = refused.Should().BeOfType<ObjectResult>().Subject;
        body.StatusCode.Should().Be(402);
        System.Text.Json.JsonSerializer.Serialize(body.Value).Should().Contain("licence_missing");

        foreach (var open in new[] { "/api/auth/login", "/api/organisation-admin/licence", "/api/public/installation", "/api/tenant-admin/localization" })
            (await RunGuardAsync(none, open)).Result.Should().BeNull($"{open} must stay reachable so an administrator can import a licence");
    }

    [Fact]
    public async Task AnExpiredLicence_Is402_AndOneInGrace_PassesWithTheHeader()
    {
        var now = DateTime.UtcNow;
        var expired = new FixedLicenceService(LicenceTestKit.Payload(b => b.With(p => p with { NotBeforeUtc = now.AddYears(-1), ExpiresAtUtc = now.AddDays(-40), GraceDays = 30 })));
        var grace = new FixedLicenceService(LicenceTestKit.Payload(b => b.With(p => p with { NotBeforeUtc = now.AddYears(-1), ExpiresAtUtc = now.AddDays(-2), GraceDays = 30 })));

        var (refused, _) = await RunGuardAsync(expired, "/api/payroll/runs");
        System.Text.Json.JsonSerializer.Serialize(((ObjectResult)refused!).Value).Should().Contain("licence_expired");

        var (passed, http) = await RunGuardAsync(grace, "/api/payroll/runs");
        passed.Should().BeNull();
        http.Response.Headers[LicenceGuardFilter.StatusHeader].ToString().Should().Be("Grace");
    }

    // ── The employee seat ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheEmployeeSeat_RefusesAnActivationAtTheLimit_AndAllowsOneBelowIt()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var active = new Employee { TenantId = _organisationId, EmployeeCode = "E1", FullName = "Active One", Status = EmployeeStatuses.Active };
        var draft = new Employee { TenantId = _organisationId, EmployeeCode = "E2", FullName = "Draft Two", Status = "Draft" };
        db.Employees.AddRange(active, draft);
        await db.SaveChangesAsync();

        var act = () => EmployeeSeatLimit.EnsureSeatAsync(db, _organisationId, maxEmployees: 1, draft.Id, default);
        (await act.Should().ThrowAsync<EmployeeSeatLimitReachedException>())
            .Which.Refusal.Should().Be(new EmployeeSeatLimit.Refusal(1, 1));

        await EmployeeSeatLimit.EnsureSeatAsync(db, _organisationId, maxEmployees: 2, draft.Id, default);
        await EmployeeSeatLimit.EnsureSeatAsync(db, _organisationId, maxEmployees: 0, draft.Id, default); // 0 = unlimited
        (await EmployeeSeatLimit.CheckAsync(db, _organisationId, 1, default))!.Body.Should().BeEquivalentTo(
            new { error = "employee_limit_reached", message = new EmployeeSeatLimit.Refusal(1, 1).Message, current = 1, limit = 1 });
    }
}
