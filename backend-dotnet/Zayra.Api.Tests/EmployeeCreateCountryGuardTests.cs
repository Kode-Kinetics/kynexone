using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// SERVER-SIDE home jurisdiction guard on employee creation.
///
/// <para>The Add Employee modal already warned that a country-less company has "nothing to require and
/// nothing to save", but its Create Employee button stayed ENABLED — so the operator clicked it and got
/// a failure instead of the warning. The button is now disabled on that same condition, and this pins
/// the other half: a disabled button is a UX affordance, not authorization. A direct API call, a stale
/// tab or an integration must hit the SAME refusal, worded by the SAME
/// <see cref="HomeJurisdiction"/> helper so the two surfaces can never disagree.</para>
///
/// <para>Both directions are proved: blocked without a country, and created with one. A guard only ever
/// seen to block is as untrustworthy as one only ever seen to pass.</para>
/// </summary>
public class EmployeeCreateCountryGuardTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenant(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Test for Claude", Slug = $"t-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        await db.SaveChangesAsync();
        return id;
    }

    /// <param name="countryCode">Empty string is exactly how a tenant's first company used to be created.</param>
    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string name, string countryCode)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = name, TradeName = name, CountryCode = countryCode,
            Jurisdiction = "test", RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
            IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static EmployeeManagementService Svc(ZayraDbContext db) =>
        new(db, new AuditService(db), new CgDocs(), new CgNotifications());

    private static RequestContext Ctx(Guid tenantId) => new(null, "test", Guid.NewGuid(), tenantId);

    private static EmployeeCreateRequest Req(string englishName, Guid? companyId) =>
        new(
            EmployeeCode: null, ManualEmployeeCode: false, EnglishName: englishName, ArabicName: null,
            PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null,
            PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: companyId, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: null,
            CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-time", ContractType: "Unlimited", JoiningDate: DateTime.UtcNow.Date,
            ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null,
            WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null,
            AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);

    // ── Blocked: the company has no country ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_UnderACompanyWithNoCountry_IsRefusedWithTheMessageTheModalShows()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var company = await SeedCompany(db, tenantId, "Test for Claude Ltd", countryCode: string.Empty);

        var act = () => Svc(db).CreateAsync(tenantId, Req("Blocked Hire", company.Id), Ctx(tenantId), default);

        var ex = (await act.Should().ThrowAsync<CompanyCountryMissingException>(
            "a disabled button is a UX affordance, not authorization — the API must refuse on its own"))
            .Subject.Single();
        ex.CompanyId.Should().Be(company.Id);
        ex.Message.Should().Be(HomeJurisdiction.CompanyMessage("Test for Claude Ltd"),
            "the API and the modal must speak one wording, from one helper");
        ex.Message.Should().Contain(HomeJurisdiction.CompanyFixLocation, "the refusal must say where the fix lives");
    }

    [Fact]
    public async Task ARefusedCreate_PersistsNothing_AndBurnsNoEmployeeCode()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var company = await SeedCompany(db, tenantId, "Test for Claude Ltd", countryCode: "   ");

        await Assert.ThrowsAsync<CompanyCountryMissingException>(
            () => Svc(db).CreateAsync(tenantId, Req("Blocked Hire", company.Id), Ctx(tenantId), default));

        (await db.Employees.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await db.EmployeeIdRules.IgnoreQueryFilters().CountAsync()).Should()
            .Be(0, "the guard runs before GenerateEmployeeCode, so a refusal never bumps a code sequence");
    }

    [Fact]
    public async Task ATenantWhoseOnlyCompanyHasNoCountry_IsRefusedEvenWhenTheRequestNamesNoCompany()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        await SeedCompany(db, tenantId, "Test for Claude Ltd", countryCode: string.Empty);

        // CompanyId: null — the service would assign this very company via ResolveDefaultCompanyId.
        var act = () => Svc(db).CreateAsync(tenantId, Req("Blocked Hire", companyId: null), Ctx(tenantId), default);

        await act.Should().ThrowAsync<CompanyCountryMissingException>();
    }

    // ── Allowed: the company has a country ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_UnderACompanyWithACountry_Succeeds()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var company = await SeedCompany(db, tenantId, "Test for Claude Ltd", countryCode: "SA");

        var created = await Svc(db).CreateAsync(tenantId, Req("Allowed Hire", company.Id), Ctx(tenantId), default);

        created.Id.Should().BeGreaterThan(0);
        (await db.Employees.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnIso3CountryIsAccepted_BecauseTheGuardReusesTheProductsOneCountryList()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var company = await SeedCompany(db, tenantId, "Test for Claude Ltd", countryCode: "SAU");

        var created = await Svc(db).CreateAsync(tenantId, Req("Allowed Hire", company.Id), Ctx(tenantId), default);

        created.Id.Should().BeGreaterThan(0);
        HomeJurisdiction.IsMissing("SAU").Should().BeFalse();
    }
}

// Local doubles (file-scoped, as in WorkEmailDerivationTests): the guard under test touches neither
// document storage nor notifications, and the service takes them as constructor dependencies.
file sealed class CgDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument("f", "t", "u", "p"));
    public string ResolvePath(string storageUrl) => "/tmp";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class CgNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}
