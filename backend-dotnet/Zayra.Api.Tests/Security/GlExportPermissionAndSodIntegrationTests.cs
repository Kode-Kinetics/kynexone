using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;
using static Zayra.Api.Tests.CostExportHarness;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Integration seams AM-19 / AM-20 (the finance.gl.export key) and AM-22 / AM-26 (the separation-of-duties adapter over
/// WP-B's CostAccountingSod, registered so production export create is enforced).
/// </summary>
public class GlExportPermissionAndSodIntegrationTests
{
    // ── AM-19: the permission ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheExportKey_IsInTheCatalog_AndHeldByExactlyTheRolesThatHoldGlManage()
    {
        var (db, tenantId) = await NewTenantAsync("gl-export-key");
        (await db.Permissions.AnyAsync(p => p.Key == "finance.gl.export")).Should().BeTrue("the key must be a catalog row to be grantable");

        var bundles = (await LegacyRoleGateBypassSweepTests.CallerBundlesAsync()).Where(b => b.Label == b.Role).ToList();
        var exporters = bundles.Where(b => b.Permissions.Contains("finance.gl.export")).Select(b => b.Role).OrderBy(r => r).ToList();
        var managers = bundles.Where(b => b.Permissions.Contains("finance.gl.manage")).Select(b => b.Role).OrderBy(r => r).ToList();

        managers.Should().Contain(new[] { "Admin", "Payroll Manager" });
        exporters.Should().Equal(managers, "every role that could take the file yesterday (finance.gl.manage) can still take it");
    }

    [Fact]
    public void TheExportKey_IsClassifiedPrivilegedForMfa()
        => PrivilegedMfaPolicy.PrivilegedPermissions.Should().Contain("finance.gl.export");

    [Fact]
    public async Task ASeededPayrollManager_StillDownloads_ByHoldingTheExportKey()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10_000m, "July payroll"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10_000m, "July payroll"));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var id = IdOf(await CreateAsync(db, maker));

        (await DownloadAsync(db, tenantId, id)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task HoldingOnlyGlManage_NoLongerDownloads_TheExportKeyIsWhatGatesTheFile()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10_000m, "July payroll"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10_000m, "July payroll"));
        db.SaveChanges();
        var id = IdOf(await CreateAsync(db, await CallerAsync(db, tenantId, "Payroll Manager")));

        ClaimsPrincipal Caller(params string[] perms) => new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "X"), new Claim("is_group_scope", "true"),
        }.Concat(perms.Select(p => new Claim("permission", p))), "Test"));

        Ctrl(db, Caller("finance.gl.read", "finance.gl.manage")).Download(id, default).Result.Should().BeOfType<ForbidResult>();
        Ctrl(db, Caller("finance.gl.read", "finance.gl.export")).Download(id, default).Result.Should().BeOfType<FileContentResult>();
        Ctrl(db, Caller("finance.gl.read")).Download(id, default).Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task AnErpApiKeyWithTheReadScope_StillDownloads_WithoutTheExportKey_AndTheAuditNamesKeyAndCreator()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10_000m, "July payroll"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10_000m, "July payroll"));
        db.SaveChanges();
        var id = IdOf(await CreateAsync(db, await CallerAsync(db, tenantId, "Payroll Manager")));
        var key = new ErpApiKey
        {
            TenantId = tenantId, Name = "ERP pull", Prefix = "kx_live_ab", Scopes = ErpApiKeyScopes.ExportsRead,
            CreatedByUserId = Guid.NewGuid(), CompanyId = null,
        };

        var result = await Ctrl(db, ErpApiKeyPrincipal.Build(key)).Download(id, default);

        result.Should().BeOfType<FileContentResult>("AM-20: the key's exports.read scope is the grant, not finance.gl.export");
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "finance.gl.journal_export.downloaded");
        audit.Metadata.Should().Contain(key.Id.ToString()).And.Contain(key.CreatedByUserId.ToString());
    }

    // ── AM-22 / AM-26: the adapter ────────────────────────────────────────────────────────────

    private static async Task<(Guid Tid, Company Co)> SeedCompany(ZayraDbContext db, bool costing)
    {
        var tid = Guid.NewGuid();
        return (tid, await AddCompany(db, tid, costing));
    }

    private static async Task ChangedMapping(ZayraDbContext db, Guid tid, Guid? companyId, Guid user)
    {
        db.AuditLogs.Add(new AuditLog { TenantId = tid, CompanyId = companyId, UserId = user, Action = "finance.gl.mapping.saved", EntityName = "GlMapping" });
        await db.SaveChangesAsync();
    }

    private static JournalExportSodRequest Req(Guid tid, Guid? co, string? period, Guid actor, Guid? keyCreator = null, Guid? run = null)
        => new(tid, co, period, run, actor, keyCreator);

    [Fact]
    public async Task AUserWhoChangedAMapping_IsBarredForThatCompanyAndPeriod_WhenCostingIsOn()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co) = await SeedCompany(db, costing: true);
        var u = Guid.NewGuid();
        await ChangedMapping(db, tid, co.Id, u);
        var sod = new CostAccountingJournalExportSeparationOfDuties(db);

        (await sod.IsBarredAsync(Req(tid, co.Id, "2026-09", u), default)).Should().BeTrue();
        (await sod.IsBarredAsync(Req(tid, co.Id, "2026-09", Guid.NewGuid()), default)).Should().BeFalse("someone else may export");
    }

    [Fact]
    public async Task TheSameUser_IsAllowed_WhenCostingIsOff()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co) = await SeedCompany(db, costing: false);
        var u = Guid.NewGuid();
        await ChangedMapping(db, tid, co.Id, u);

        (await new CostAccountingJournalExportSeparationOfDuties(db).IsBarredAsync(Req(tid, co.Id, "2026-09", u), default)).Should().BeFalse();
    }

    [Fact]
    public async Task AnApiKeyCreatedByThatUser_IsBarred_EvenThoughTheActorIsTheKey()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co) = await SeedCompany(db, costing: true);
        var creator = Guid.NewGuid();
        await ChangedMapping(db, tid, co.Id, creator);
        var sod = new CostAccountingJournalExportSeparationOfDuties(db);

        (await sod.IsBarredAsync(Req(tid, co.Id, "2026-09", Guid.NewGuid(), keyCreator: creator), default)).Should().BeTrue();
        (await sod.IsBarredAsync(Req(tid, co.Id, "2026-09", Guid.NewGuid(), keyCreator: Guid.NewGuid()), default)).Should().BeFalse();
    }

    [Fact]
    public async Task ATenantWideExport_ChecksEveryCompanyOfTheTenant_AndARunOnlyExportResolvesItsCompanyAndPeriod()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co) = await SeedCompany(db, costing: true);
        var other = await AddCompany(db, tid, enabled: true);
        var u = Guid.NewGuid();
        await ChangedMapping(db, tid, other.Id, u);
        var sod = new CostAccountingJournalExportSeparationOfDuties(db);

        (await sod.IsBarredAsync(Req(tid, null, "2026-09", u), default)).Should().BeTrue("a group export includes the company the user changed");
        (await sod.IsBarredAsync(Req(tid, co.Id, "2026-09", u), default)).Should().BeFalse("a different company is unaffected");
    }

    [Fact]
    public async Task AnUnknownScope_DoesNotThrow()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var sod = new CostAccountingJournalExportSeparationOfDuties(db);

        (await sod.IsBarredAsync(Req(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid()), default)).Should().BeFalse();
        (await sod.IsBarredAsync(Req(Guid.NewGuid(), null, "not-a-period", Guid.NewGuid()), default)).Should().BeFalse();
    }

    // ── AM-22: production registration (real Program.cs composition) ──────────────────────────

    [Fact]
    public async Task ProductionDi_RegistersTheAdapter_AndTheControllerBuiltByDi_RefusesAnExportBySomeoneWhoChangedTheSetup()
    {
        var connection = $"Data Source=file:gl-export-sod-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connection);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connection);
        _ = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetService<IJournalExportSeparationOfDuties>().Should().BeOfType<CostAccountingJournalExportSeparationOfDuties>();

        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Sod", Slug = $"sod-{tid:N}"[..20], IsActive = true });
        await db.SaveChangesAsync();
        var co = await AddCompany(db, tid, enabled: true);
        var user = Guid.NewGuid();
        await ChangedMapping(db, tid, co.Id, user);

        var ctrl = ActivatorUtilities.CreateInstance<GlJournalExportsController>(scope.ServiceProvider);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                    new Claim("is_group_scope", "true"), new Claim("permission", "finance.gl.manage"), new Claim("permission", "finance.gl.read"),
                }, "Test")),
            },
        };

        var result = await ctrl.Create("2026-09", null, co.Id, "SAR", false, false, "generic-csv", default);

        ((ObjectResult)result).StatusCode.Should().Be(403);
        (await db.AuditLogs.AnyAsync(a => a.Action == "finance.gl.journal_export.create_refused")).Should().BeTrue();
    }
}
