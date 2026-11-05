using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <see cref="EmployeeSearch"/> on a real PostgreSQL: the InMemory tests prove the rules, these prove the predicate TRANSLATES
/// (lower(), the replace() chain that reduces a stored phone to digits, the linked-branch subquery) and that PostgreSQL's
/// case-sensitive LIKE/strpos no longer makes "fahad" miss "Fahad", and that a '%' or '_' in the term is literal.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeSearch360PostgresTests
{
    private readonly PostgresFixture _fx;
    public EmployeeSearch360PostgresTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData("fahad", false, "S-0001")]
    [InlineData("فهد", false, "S-0001")]
    [InlineData("SENIOR acc", false, "S-0001")]
    [InlineData("riyadh tower", false, "S-0002")]
    [InlineData("برج", false, "S-0002")]
    [InlineData("0551234567", true, "S-0001")]
    [InlineData("+966 55 123 4567", true, "S-0001")]
    [InlineData("fahad.home@", true, "S-0001")]
    [InlineData("1012345678", true, "S-0001")]
    public async Task FindsTheEmployee(string term, bool sensitive, string expectedCode)
    {
        var tenantId = await SeedAsync();
        (await CodesAsync(tenantId, term, sensitive)).Should().Equal(expectedCode);
    }

    [Theory]
    [InlineData("0551234567", false)]
    [InlineData("1012345678", false)]
    [InlineData("101234567", true)]
    [InlineData("fahad.home@", false)]
    [InlineData("%", false)]
    [InlineData("_", false)]
    public async Task FindsNobody(string term, bool sensitive)
    {
        var tenantId = await SeedAsync();
        (await CodesAsync(tenantId, term, sensitive)).Should().BeEmpty();
    }

    private async Task<List<string>> CodesAsync(Guid tenantId, string term, bool sensitive)
    {
        await using var db = _fx.CreateDb();
        var employees = db.Employees.IgnoreQueryFilters().Where(e => e.TenantId == tenantId);
        var branches = db.Branches.IgnoreQueryFilters().Where(b => b.TenantId == tenantId);
        return await EmployeeSearch.Apply(employees, branches, term, sensitive).Select(e => e.EmployeeCode).OrderBy(c => c).ToListAsync();
    }

    private async Task<Guid> SeedAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Search Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"SRC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        var riyadh = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "RUH", NameEn = "Riyadh Tower", NameAr = "برج الرياض", IsActive = true };
        db.Branches.Add(riyadh);
        await db.SaveChangesAsync();
        db.Employees.AddRange(
            new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "S-0001", FullName = "Fahad Alharbi", EnglishName = "Fahad Alharbi",
                ArabicName = "فهد الحربي", WorkEmail = "f.alharbi@evostel.com", PersonalEmail = "Fahad.Home@gmail.com", Phone = "+966 55 123 4567",
                IdNumber = "1012345678", Department = "Finance", Designation = "Senior Accountant", Branch = "Jeddah HQ", Status = "Active",
                Nationality = "Saudi", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "S-0002", FullName = "Khalid Alotaibi", EnglishName = "Khalid Alotaibi",
                ArabicName = "خالد العتيبي", WorkEmail = "k.alotaibi@evostel.com", PersonalEmail = "khalid@outlook.com", Phone = "0509876543",
                IdNumber = "2098765432", Department = "Operations", Designation = "Site Supervisor", Branch = string.Empty, BranchId = riyadh.Id,
                Status = "Active", Nationality = "Saudi", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        return tenantId;
    }
}
