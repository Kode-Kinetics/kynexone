using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// The ERP company code decides which legal entity's ledger an ERP books a company's journals into. Before this,
/// any HR Manager could set it, two companies could share one (and a group journal export then merged them into one
/// ERP document, with the per-company balance gate passing on the merged pair), and a change left no before/after
/// trail. Now: only a holder of finance.gl.manage may set or change it, it is unique per tenant case-insensitively
/// (also against another company's registration-number fallback), GROUP/UNATTRIBUTED are reserved, and every change
/// is audited old → new. The database half (ux_companies_tenant_erp_company_code → 409) is proven on Postgres by
/// JournalExportDecisionConcurrencyPostgresTests.
/// </summary>
public class ErpCompanyCodeGovernanceTests
{
    [Fact]
    public async Task AnHrManagerWithoutTheGlKey_CannotChangeTheCode_ButCanStillEditTheRestOfTheCompany()
    {
        var (db, tenantId) = await NewTenantAsync("erp-code-hr");
        var company = await SeedCompanyAsync(db, tenantId, "Alpha KSA", "7001", erpCode: "SA01");
        var hr = await UserAsync(db, tenantId, "HR Manager");
        var svc = Svc(db);

        // Unchanged code (or omitted): the HR edit goes through.
        var renamed = await svc.UpdateCompanyAsync(tenantId, company.Id, Req("Alpha KSA Renamed", "7001", erpCode: "SA01"), Ctx(tenantId, hr), default);
        renamed!.LegalNameEn.Should().Be("Alpha KSA Renamed");
        (await svc.UpdateCompanyAsync(tenantId, company.Id, Req("Alpha KSA", "7001", erpCode: null), Ctx(tenantId, hr), default))!
            .ErpCompanyCode.Should().Be("SA01", "an omitted code is left as it is");

        // A changed code is refused with a plain 403.
        var change = () => svc.UpdateCompanyAsync(tenantId, company.Id, Req("Alpha KSA", "7001", erpCode: "SA99"), Ctx(tenantId, hr), default);
        var refused = (await change.Should().ThrowAsync<EmailDomainRefusedException>()).Which;
        refused.Status.Should().Be(403);
        refused.Code.Should().Be("erp_company_code_needs_finance");
        refused.Message.Should().Contain("Only a finance user who manages the general ledger");

        db.ChangeTracker.Clear();
        (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id)).ErpCompanyCode.Should().Be("SA01");
    }

    [Fact]
    public async Task AGlManager_ChangesTheCode_AndTheChangeIsAuditedBeforeAndAfter()
    {
        var (db, tenantId) = await NewTenantAsync("erp-code-admin");
        var company = await SeedCompanyAsync(db, tenantId, "Alpha KSA", "7001", erpCode: "SA01");
        var admin = await UserAsync(db, tenantId, "Admin");

        var updated = await Svc(db).UpdateCompanyAsync(tenantId, company.Id, Req("Alpha KSA", "7001", erpCode: "SA02"), Ctx(tenantId, admin), default);

        updated!.ErpCompanyCode.Should().Be("SA02");
        db.ChangeTracker.Clear();
        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.TenantId == tenantId && a.Action == OrganizationSetupService.ErpCompanyCodeChangedAction);
        audit.EntityId.Should().Be(company.Id.ToString());
        audit.UserId.Should().Be(admin);
        audit.Metadata.Should().Contain("\"oldErpCompanyCode\":\"SA01\"").And.Contain("\"newErpCompanyCode\":\"SA02\"");
    }

    [Theory]
    [InlineData("sa01", "the same code in another case is the same ERP company")]
    [InlineData("1010-555", "a company without an ERP code is exported under its registration number")]
    public async Task ACodeAnotherCompanyIsExportedUnder_IsRefusedWith409_NamingThatCompany(string code, string because)
    {
        var (db, tenantId) = await NewTenantAsync("erp-code-dup");
        await SeedCompanyAsync(db, tenantId, "Alpha KSA", "7001", erpCode: "SA01");
        await SeedCompanyAsync(db, tenantId, "Beta Services", "1010-555", erpCode: null);
        var gamma = await SeedCompanyAsync(db, tenantId, "Gamma Trading", "4030", erpCode: null);
        var admin = await UserAsync(db, tenantId, "Admin");

        var act = () => Svc(db).UpdateCompanyAsync(tenantId, gamma.Id, Req("Gamma Trading", "4030", erpCode: code), Ctx(tenantId, admin), default);

        var refused = (await act.Should().ThrowAsync<EmailDomainRefusedException>(because)).Which;
        refused.Status.Should().Be(409);
        refused.Code.Should().Be("erp_company_code_taken");
        refused.Message.Should().StartWith($"ERP company code {code} is already used by ")
            .And.Match(m => m.Contains("Alpha KSA") || m.Contains("Beta Services"));
    }

    [Fact]
    public async Task CreatingACompanyWithATakenOrReservedCode_IsRefused()
    {
        var (db, tenantId) = await NewTenantAsync("erp-code-create");
        await SeedCompanyAsync(db, tenantId, "Alpha KSA", "7001", erpCode: "SA01");
        var admin = await UserAsync(db, tenantId, "Admin");
        var svc = Svc(db);

        var taken = () => svc.CreateCompanyAsync(tenantId, Req("Delta", "9001", erpCode: "SA01"), Ctx(tenantId, admin), default);
        (await taken.Should().ThrowAsync<EmailDomainRefusedException>()).Which.Status.Should().Be(409);

        var reserved = () => svc.CreateCompanyAsync(tenantId, Req("Delta", "9001", erpCode: "group"), Ctx(tenantId, admin), default);
        (await reserved.Should().ThrowAsync<EmailDomainRefusedException>()).Which.Code.Should().Be("erp_company_code_reserved");

        var created = await svc.CreateCompanyAsync(tenantId, Req("Delta", "9001", erpCode: "SA04"), Ctx(tenantId, admin), default);
        created.ErpCompanyCode.Should().Be("SA04");
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.TenantId == tenantId && a.Action == OrganizationSetupService.ErpCompanyCodeChangedAction))
            .Should().Be(1, "setting the code at creation is recorded too");
    }

    [Fact]
    public async Task ADeletedCompanyReleasesItsCode()
    {
        var (db, tenantId) = await NewTenantAsync("erp-code-deleted");
        var old = await SeedCompanyAsync(db, tenantId, "Old Co", "7001", erpCode: "SA01");
        old.IsDeleted = true;
        await db.SaveChangesAsync();
        var gamma = await SeedCompanyAsync(db, tenantId, "Gamma Trading", "4030", erpCode: null);
        var admin = await UserAsync(db, tenantId, "Admin");

        var updated = await Svc(db).UpdateCompanyAsync(tenantId, gamma.Id, Req("Gamma Trading", "4030", erpCode: "SA01"), Ctx(tenantId, admin), default);

        updated!.ErpCompanyCode.Should().Be("SA01");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private static OrganizationSetupService Svc(ZayraDbContext db) => new(db, new AuditService(db));

    private static RequestContext Ctx(Guid tenantId, Guid userId) => new("127.0.0.1", "test", userId, tenantId);

    private static CompanyRequest Req(string name, string registration, string? erpCode) =>
        new(name, null, name, "SA", "SA", registration, null, null, null, null, "SAR", ErpCompanyCode: erpCode);

    private static async Task<Company> SeedCompanyAsync(ZayraDbContext db, Guid tenantId, string name, string registration, string? erpCode)
    {
        var c = new Company
        {
            TenantId = tenantId, LegalNameEn = name, TradeName = name, CountryCode = "SA", Jurisdiction = "SA",
            RegistrationNumber = registration, DefaultCurrency = "SAR", IsActive = true, ErpCompanyCode = erpCode,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task<Guid> UserAsync(ZayraDbContext db, Guid tenantId, string role)
    {
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == role);
        var email = $"{role.Replace(" ", "").ToLowerInvariant()}-{Guid.NewGuid():N}@example.test";
        var user = new User
        {
            TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = role,
            Status = "Active", IsActive = true, IsGroupScope = true,
        };
        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
