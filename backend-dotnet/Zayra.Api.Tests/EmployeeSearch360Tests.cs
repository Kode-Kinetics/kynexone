using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// "360" employee search (GET /api/employees?search=): one predicate, <see cref="EmployeeSearch"/>, shared by the group-level
/// list, the company-scoped list and the bulk-action resolver.
///
/// <para>Everyone who may list employees matches on code, English/Arabic/full name, work email, job title, department and branch,
/// case-insensitively and in part. Phone, personal email and ID/Iqama number are personal fields the record view shows only with
/// <c>employees.sensitive</c>; matching on them without it would let a caller confirm a number they may not see, so they are
/// searched only for that permission, and the ID number only on its exact full value.</para>
/// </summary>
public class EmployeeSearch360Tests
{
    public static TheoryData<bool> BothListPaths => new() { false, true };

    [Theory]
    [MemberData(nameof(BothListPaths))]
    public async Task MatchesTheOrdinaryFields_CaseInsensitively_AndInPart(bool companyScoped)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        foreach (var term in new[] { "fahad", "FAHAD ALH", "فهد", "evt2-0001", "f.alharbi@evostel", "senior ACCOUNT", "finance", "jeddah" })
            (await CodesAsync(db, f, term, companyScoped, sensitive: false)).Should().Contain("EVT2-0001", $"'{term}' should find Fahad");

        (await CodesAsync(db, f, "operations", companyScoped, sensitive: false)).Should().Equal("EVT2-0002");
    }

    [Theory]
    [MemberData(nameof(BothListPaths))]
    public async Task MatchesTheBranchName_WhenOnlyTheBranchLinkIsStored(bool companyScoped)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        (await CodesAsync(db, f, "riyadh tower", companyScoped, sensitive: false)).Should().Equal("EVT2-0002");
    }

    [Theory]
    [MemberData(nameof(BothListPaths))]
    public async Task WithoutEmployeesSensitive_PhonePersonalEmailAndIdNumber_MatchNothing(bool companyScoped)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        foreach (var term in new[] { "0551234567", "fahad.home@gmail", "1012345678" })
            (await CodesAsync(db, f, term, companyScoped, sensitive: false)).Should().BeEmpty($"'{term}' is a personal field the caller may not see");
    }

    [Theory]
    [MemberData(nameof(BothListPaths))]
    public async Task WithEmployeesSensitive_PhoneMatchesInAnyCommonFormat(bool companyScoped)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db); // Fahad's phone is stored as "+966 55 123 4567"

        foreach (var term in new[] { "0551234567", "+966551234567", "00966 55 123 4567", "055-123-4567", "1234567" })
            (await CodesAsync(db, f, term, companyScoped, sensitive: true)).Should().Equal(new[] { "EVT2-0001" }, $"'{term}' is Fahad's number");
    }

    [Theory]
    [MemberData(nameof(BothListPaths))]
    public async Task WithEmployeesSensitive_PersonalEmailMatches(bool companyScoped)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        (await CodesAsync(db, f, "FAHAD.HOME@gmail", companyScoped, sensitive: true)).Should().Equal("EVT2-0001");
    }

    [Theory]
    [MemberData(nameof(BothListPaths))]
    public async Task WithEmployeesSensitive_IdNumberMatchesOnlyTheExactFullNumber(bool companyScoped)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db); // Fahad's ID is 1012345678

        (await CodesAsync(db, f, " 1012345678 ", companyScoped, sensitive: true)).Should().Equal("EVT2-0001");
        (await CodesAsync(db, f, "101234567", companyScoped, sensitive: true)).Should().BeEmpty("a partial ID number is not a match");
        (await CodesAsync(db, f, "1012", companyScoped, sensitive: true)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(BothListPaths))]
    public async Task ShortDigitRuns_DoNotMatchEveryPhone(bool companyScoped)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        (await CodesAsync(db, f, "55", companyScoped, sensitive: true)).Should().BeEmpty("fewer than 4 digits is too broad to search phone numbers");
    }

    [Fact]
    public void LikeWildcardsInTheTerm_AreLiteral()
    {
        var people = new[]
        {
            new Employee { EmployeeCode = "A%1", FullName = "Percent" },
            new Employee { EmployeeCode = "AX1", FullName = "Other" },
        }.AsQueryable();

        EmployeeSearch.Apply(people, Enumerable.Empty<Branch>().AsQueryable(), "a%1", includeSensitive: false)
            .Select(e => e.EmployeeCode).Should().Equal("A%1");
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────────────

    private sealed record Fixture(Guid TenantId, Guid CompanyId);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static async Task<Fixture> SeedAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Evostel", Slug = $"e-{tenantId:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Evostel", CountryCode = "SA", Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        var riyadh = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "RUH", NameEn = "Riyadh Tower", NameAr = "برج الرياض", IsActive = true };
        db.Branches.Add(riyadh);
        await db.SaveChangesAsync();

        db.Employees.AddRange(
            new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "EVT2-0001",
                FullName = "Fahad Alharbi", EnglishName = "Fahad Alharbi", ArabicName = "فهد الحربي",
                WorkEmail = "f.alharbi@evostel.com", PersonalEmail = "Fahad.Home@gmail.com", Phone = "+966 55 123 4567", IdNumber = "1012345678",
                Department = "Finance", Designation = "Senior Accountant", Branch = "Jeddah HQ",
                Status = "Active", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc),
            },
            new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "EVT2-0002",
                FullName = "Khalid Alotaibi", EnglishName = "Khalid Alotaibi", ArabicName = "خالد العتيبي",
                WorkEmail = "k.alotaibi@evostel.com", PersonalEmail = "khalid@outlook.com", Phone = "0509876543", IdNumber = "2098765432",
                Department = "Operations", Designation = "Site Supervisor", Branch = string.Empty, BranchId = riyadh.Id,
                Status = "Active", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        return new Fixture(tenantId, company.Id);
    }

    private static async Task<List<string>> CodesAsync(ZayraDbContext db, Fixture f, string term, bool companyScoped, bool sensitive)
    {
        var controller = new EmployeesController(
            db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db),
            new SearchHijriDateService(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new SearchLetterService());
        var claims = new List<Claim>
        {
            new("tenant_id", f.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"),
        };
        if (sensitive) claims.Add(new Claim("permission", "employees.sensitive"));
        claims.Add(companyScoped
            ? new Claim(EntityScopeContext.V2ClaimType, System.Text.Json.JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { f.CompanyId.ToString() } }))
            : new Claim("is_group_scope", "true"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };

        var result = await controller.Search(Service(db), term, null, null, null, null, null, 1, 25, CancellationToken.None);
        var page = Assert.IsType<PagedResult<EmployeeListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        return page.Items.Select(i => i.EmployeeCode).OrderBy(c => c).ToList();
    }
}

file sealed class SearchHijriDateService : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class SearchLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
}
