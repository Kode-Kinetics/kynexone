using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Migrations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Companies and branches store canonical ISO-2. The form and both imports used to validate a code and then store
/// it as typed, so a valid ISO-3 "SAU" was saved as "SAU" and Setup crashed on it (#251). Writers now go through
/// <see cref="CountryCodeStandard.ToStored"/>, and <see cref="NormalizeCompanyCountryCodes"/> backfills old rows.
/// </summary>
public class CompanyCountryCodeCanonicalTests
{
    [Theory]
    [InlineData("SAU", "SA")]
    [InlineData(" sau ", "SA")]
    [InlineData("sa", "SA")]
    [InlineData("ARE", "AE")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData(" Saudi ", "SAUDI")] // not a code: validation refuses it before any writer stores it
    public void ToStored_IsCanonicalIso2_ForEveryRecognizedCode(string? input, string expected)
        => CountryCodeStandard.ToStored(input).Should().Be(expected);

    [Fact]
    public void TheBackfillMapping_IsTheStandardsOwn_PlusOnlyTheTwoDocumentedAliases()
    {
        var pairs = Regex.Matches(NormalizeCompanyCountryCodes.MappingValues, @"\('([A-Z]{2,3})','([A-Z]{2})'\)")
            .Select(m => (Code: m.Groups[1].Value, Iso2: m.Groups[2].Value)).ToList();

        pairs.Should().NotBeEmpty();
        pairs.Select(p => p.Code).Should().OnlyHaveUniqueItems();
        foreach (var (code, iso2) in pairs)
        {
            if (code is "KSA" or "UAE") continue;
            CountryCodeStandard.NormalizeToIso2(code).Should().Be(iso2, $"{code} must map the way the standard maps it");
        }
        pairs.Should().Contain(("KSA", "SA")).And.Contain(("UAE", "AE"));

        // Every code the standard accepts is covered, so no accepted spelling is left behind in the database.
        foreach (var country in IsoReference.Countries)
        {
            pairs.Should().Contain((country.Code, country.Code));
            pairs.Should().Contain((CountryCodeStandard.ToIso3(country.Code)!, country.Code));
        }
    }
}

[Collection("Integration")]
public sealed class CompanyCountryCodeBackfillPostgresTests
{
    private readonly PostgresFixture _fx;
    public CompanyCountryCodeBackfillPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Backfill_CanonicalisesAcceptedSpellings_LeavesTheRestAlone_AndRunsTwice()
    {
        Guid tenantId;
        var companies = new Dictionary<string, Guid>();
        Guid branchId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            foreach (var code in new[] { "SAU", " sa ", "KSA", "ARE", "SA", "Saudi Arabia", "" })
            {
                var company = new Company { TenantId = tenantId, LegalNameEn = $"Co {code}|{Guid.NewGuid():N}", CountryCode = code };
                db.Companies.Add(company);
                companies[code] = company.Id;
            }
            var branch = new Branch { TenantId = tenantId, CompanyId = companies["SAU"], Code = $"HQ{Guid.NewGuid():N}"[..10], NameEn = "Head office", CountryCode = "SAU" };
            db.Branches.Add(branch);
            branchId = branch.Id;
            await db.SaveChangesAsync();
        }

        var statements = new NormalizeCompanyCountryCodes().UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
        statements.Should().HaveCount(2);
        for (var run = 0; run < 2; run++)
        {
            await using var db = _fx.CreateDb();
            foreach (var sql in statements) await db.Database.ExecuteSqlRawAsync(sql);
        }

        await using (var db = _fx.CreateDb())
        {
            var stored = await db.Companies.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId).ToDictionaryAsync(c => c.Id, c => c.CountryCode);
            stored[companies["SAU"]].Should().Be("SA");
            stored[companies[" sa "]].Should().Be("SA");
            stored[companies["KSA"]].Should().Be("SA");
            stored[companies["ARE"]].Should().Be("AE");
            stored[companies["SA"]].Should().Be("SA");
            stored[companies["Saudi Arabia"]].Should().Be("Saudi Arabia", "free text is left for a person to correct, never guessed");
            stored[companies[""]].Should().Be("");

            (await db.Branches.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == branchId)).CountryCode.Should().Be("SA");
        }
    }
}

/// <summary>The form doors: what Setup's company and branch editors store, and which values they refuse.</summary>
public class OrganizationCountryCodeWriteTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly RequestContext Ctx = new("127.0.0.1", "test", Guid.NewGuid());

    private static BranchRequest Branch(Guid companyId, string country, string city = "Riyadh") =>
        new(companyId, "HQ", "Head office", null, country, city, null, null, "Asia/Riyadh", null, IsHeadOffice: true);

    private static async Task<(ZayraDbContext Db, OrganizationSetupService Svc, Guid Tenant, Company Company)> World(string companyCountry = "SA")
    {
        var db = Db();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Alpha Trading", RegistrationNumber = "A-1", CountryCode = companyCountry, IsActive = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (db, new OrganizationSetupService(db, new AuditService(db)), tenant, company);
    }

    [Fact]
    public async Task UpdateCompany_StoresAnIso3CodeAsIso2()
    {
        var (db, svc, tenant, company) = await World();
        await svc.UpdateCompanyAsync(tenant, company.Id, new CompanyRequest("Alpha Trading", null, null, "SAU", null, "A-1", null, null, null, null, "SAR"), Ctx, default);
        (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id)).CountryCode.Should().Be("SA");
    }

    [Fact]
    public async Task CreateBranch_StoresIso2_AndRefusesFreeText()
    {
        var (db, svc, tenant, company) = await World();
        var created = await svc.CreateBranchAsync(tenant, Branch(company.Id, " sau "), Ctx, default);
        (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == created.Id)).CountryCode.Should().Be("SA");

        var refused = () => svc.CreateBranchAsync(tenant, Branch(company.Id, "KSA") with { Code = "BR2" }, Ctx, default);
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Unrecognized country code 'KSA'*");
    }

    [Fact]
    public async Task UpdateBranch_KeepsAnUnchangedLegacyValueWorking_ButRefusesANewBadOne()
    {
        var (db, svc, tenant, company) = await World();
        var legacy = new Branch { TenantId = tenant, CompanyId = company.Id, Code = "HQ", NameEn = "Head office", CountryCode = "Saudi Arabia" };
        db.Branches.Add(legacy);
        await db.SaveChangesAsync();

        // The create-company wizard re-sends the stored country when it only renames a branch or moves its city.
        (await svc.UpdateBranchAsync(tenant, legacy.Id, Branch(company.Id, "Saudi Arabia", city: "Jeddah"), Ctx, default)).Should().NotBeNull();

        var changedToBad = () => svc.UpdateBranchAsync(tenant, legacy.Id, Branch(company.Id, "KSA"), Ctx, default);
        await changedToBad.Should().ThrowAsync<InvalidOperationException>();

        await svc.UpdateBranchAsync(tenant, legacy.Id, Branch(company.Id, "SAU"), Ctx, default);
        (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == legacy.Id)).CountryCode.Should().Be("SA");
    }
}
