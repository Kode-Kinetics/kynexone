using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>A new payroll record takes the employing company's currency; it is never silently written in AED.</summary>
public class CompanyCurrencyDefaultsTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, Guid Company, int Employee)> SeedAsync(ZayraDbContext db, string countryCode, string currency)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Co", CountryCode = countryCode, DefaultCurrency = currency };
        db.Companies.Add(company);
        var emp = new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "E1", FullName = "A", Status = "Active" };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        return (tenant, company.Id, emp.Id);
    }

    [Fact]
    public async Task KsaCompany_NewPayrollProfile_IsSar()
    {
        await using var db = CreateDb();
        var (tenant, _, emp) = await SeedAsync(db, "SAU", "SAR");

        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = emp });
        await db.SaveChangesAsync();

        (await db.EmployeePayrollProfiles.SingleAsync()).SalaryCurrency.Should().Be("SAR");
    }

    [Fact]
    public async Task UaeCompany_NewPayrollProfile_IsAed()
    {
        await using var db = CreateDb();
        var (tenant, _, emp) = await SeedAsync(db, "ARE", "AED");
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = emp });
        await db.SaveChangesAsync();
        (await db.EmployeePayrollProfiles.SingleAsync()).SalaryCurrency.Should().Be("AED");
    }

    [Fact]
    public async Task ACurrencyTheCallerSet_IsNeverOverwritten()
    {
        await using var db = CreateDb();
        var (tenant, _, emp) = await SeedAsync(db, "SAU", "SAR");
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = emp, SalaryCurrency = "USD" });
        await db.SaveChangesAsync();
        (await db.EmployeePayrollProfiles.SingleAsync()).SalaryCurrency.Should().Be("USD");
    }

    [Fact]
    public async Task CompanyScopedRecords_TakeTheirCompanysCurrency()
    {
        await using var db = CreateDb();
        var (tenant, company, _) = await SeedAsync(db, "SAU", "SAR");
        db.SalaryStructures.Add(new SalaryStructure { TenantId = tenant, CompanyId = company, Code = "S1", Name = "S1" });
        await db.SaveChangesAsync();
        (await db.SalaryStructures.SingleAsync()).Currency.Should().Be("SAR");
    }

    [Fact]
    public async Task NoCompany_FallsBackToTheTenantCurrency_NotAed()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenant, CurrencyCode = "SAR" });
        await db.SaveChangesAsync();
        db.PayrollGroups.Add(new PayrollGroup { TenantId = tenant, Code = "G", Name = "G" });
        await db.SaveChangesAsync();
        (await db.PayrollGroups.SingleAsync()).Currency.Should().Be("SAR");
    }

    [Fact]
    public async Task UaeCompany_NewContractGradeAndPosition_AreAed_NotTheOldSarDefault()
    {
        await using var db = CreateDb();
        var (tenant, company, emp) = await SeedAsync(db, "ARE", "AED");
        db.Grades.Add(new Grade { TenantId = tenant, CompanyId = company, Code = "G1", Name = "G1" });
        db.Positions.Add(new Position { TenantId = tenant, CompanyId = company, Code = "P1" });
        db.EmployeeContracts.Add(new EmployeeContract { TenantId = tenant, EmployeeId = Guid.NewGuid(), CompanyId = company, ContractNumber = "C1" });
        await db.SaveChangesAsync();
        (await db.Grades.SingleAsync()).Currency.Should().Be("AED");
        (await db.Positions.SingleAsync()).Currency.Should().Be("AED");
        (await db.EmployeeContracts.SingleAsync()).CurrencyCode.Should().Be("AED");
    }

    [Fact]
    public async Task KsaCompany_NewContractAndWorkforcePlan_AreSar_NotAed()
    {
        await using var db = CreateDb();
        var (tenant, company, emp) = await SeedAsync(db, "SAU", "SAR");
        db.EmployeeContracts.Add(new EmployeeContract { TenantId = tenant, EmployeeId = Guid.NewGuid(), CompanyId = company, ContractNumber = "C1" });
        db.WorkforcePlans.Add(new WorkforcePlan { TenantId = tenant, PlanName = "FY27" });
        await db.SaveChangesAsync();
        (await db.EmployeeContracts.SingleAsync()).CurrencyCode.Should().Be("SAR");
        (await db.WorkforcePlans.SingleAsync()).CurrencyCode.Should().Be("SAR");
    }

    [Fact]
    public void NoModelCarriesAHardCodedCurrencyDefault_ExceptPlatformBilling()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "Zayra.Api", "Models"))) root = Path.GetDirectoryName(root);
        var offenders = Directory.GetFiles(Path.Combine(root!, "Zayra.Api", "Models"), "*.cs")
            .Where(f => Path.GetFileName(f) is not ("SaasPlatform.cs" or "Company.cs"))
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: Path.GetFileName(f), Line: i + 1, Text: l)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Text, @"Currenc\w*\s*\{\s*get;\s*set;\s*\}\s*=\s*""[A-Z]{3}"""))
            .Select(x => $"{x.File}:{x.Line} {x.Text.Trim()}").ToList();
        offenders.Should().BeEmpty("a currency literal in a model silently writes that currency for every company; resolve it from the company at save (CompanyCurrencyDefaults)");
    }

    // ── L6: a covered record can never be stored with an empty currency ─────────────────────────────

    [Fact]
    public void EveryCoveredRecord_HasACurrencyColumn_ThatIsEmptyUntilSaved_NotAHardCodedLiteral()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var type in CompanyCurrencyDefaults.CoveredTypes)
        {
            var entityType = db.Model.FindEntityType(type);
            entityType.Should().NotBeNull($"{type.Name} is a mapped entity");
            var column = CompanyCurrencyDefaults.CurrencyPropertyName(entityType!);
            column.Should().NotBeNull($"{type.Name} must carry a currency column for the default to fill");
            var fresh = Activator.CreateInstance(type)!;
            type.GetProperty(column!)!.GetValue(fresh).Should().Be(string.Empty,
                $"{type.Name}.{column} must start empty so the company's currency (not a literal) is what gets stored");
        }
    }

    [Fact]
    public async Task EveryCoveredRecord_ThatCanBeSavedBare_IsStoredWithTheCompanyCurrency()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (tenant, company, _) = await SeedAsync(db, "SAU", "SAR");
        var saved = 0;
        foreach (var type in CompanyCurrencyDefaults.CoveredTypes)
        {
            var entity = Activator.CreateInstance(type)!;
            type.GetProperty("TenantId")?.SetValue(entity, tenant);
            if (type.GetProperty("CompanyId") is { } cp && (cp.PropertyType == typeof(Guid) || cp.PropertyType == typeof(Guid?))) cp.SetValue(entity, company);
            db.Add(entity);
            try { await db.SaveChangesAsync(); }
            catch (Exception) { db.ChangeTracker.Clear(); continue; }   // needs related rows this bare fixture does not build
            var column = CompanyCurrencyDefaults.CurrencyPropertyName(db.Model.FindEntityType(type)!)!;
            type.GetProperty(column)!.GetValue(entity).Should().Be("SAR", $"{type.Name}.{column}");
            saved++;
            db.ChangeTracker.Clear();
        }
        saved.Should().BeGreaterThan(5, "enough covered records must be saveable bare for this to prove the default is applied");
    }
}
