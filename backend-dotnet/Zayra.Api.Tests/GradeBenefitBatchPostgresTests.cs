using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class GradeBenefitBatchPostgresTests
{
    private readonly PostgresFixture _fixture;
    public GradeBenefitBatchPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Bulk250Hires_UsesOneCatalogReadSet_AndPreservesGradeAndCompanyBoundaries()
    {
        var reads = new BenefitReadCounter();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(reads).Options);
        var tenant = new Tenant { Name = "Synthetic bulk benefits", Slug = $"bulk-benefits-{Guid.NewGuid():N}" };
        var company = new Company { TenantId = tenant.Id, LegalNameEn = "Bulk benefit company", CountryCode = "SA" };
        var otherCompany = new Company { TenantId = tenant.Id, LegalNameEn = "Other benefit company", CountryCode = "SA" };
        var grade = new Grade { TenantId = tenant.Id, Code = "BULK-G5", Name = "Professional", Level = 5 };
        var otherGrade = new Grade { TenantId = tenant.Id, Code = "BULK-G8", Name = "Director", Level = 8 };
        var plan = new BenefitPlan { TenantId = tenant.Id, CompanyId = company.Id, Code = "BULK-MED", Name = "Bulk Medical", EffectiveFrom = new DateOnly(2026, 1, 1) };
        db.Tenants.Add(tenant); db.Companies.AddRange(company, otherCompany); db.Grades.AddRange(grade, otherGrade); db.BenefitPlans.Add(plan);
        db.BenefitEligibilityRules.Add(new BenefitEligibilityRule { TenantId = tenant.Id, CompanyId = company.Id, BenefitPlanId = plan.Id, GradeId = grade.Id, EffectiveFrom = new DateOnly(2026, 1, 1) });
        var employees = Enumerable.Range(1,250).Select(i => new Employee { TenantId = tenant.Id, CompanyId = i % 2 == 0 ? company.Id : otherCompany.Id, GradeId = i % 3 == 0 ? grade.Id : otherGrade.Id, EmployeeCode = $"GB-BULK-{i:D3}", FullName = $"Synthetic bulk hire {i}", Status = "Draft", JoiningDate = new DateTime(2026,10,8,0,0,0,DateTimeKind.Utc) }).ToList();
        db.Employees.AddRange(employees);
        await db.SaveChangesAsync();
        reads.Count = 0;
        var assigned = await GradeBenefitDefaults.StageDefaultsForEmployeesAsync(db, employees, null, default);
        Assert.Equal(41, assigned.Count); // every sixth employee meets both scopes
        Assert.InRange(reads.Count, 1, 5); // flag + plans + rules + grades + existing enrollments
        Assert.All(assigned, row => Assert.Equal(company.Id, row.CompanyId));
        await db.SaveChangesAsync();
        Assert.Equal(41, await db.BenefitEnrollments.CountAsync(x => x.TenantId == tenant.Id));
        Assert.Empty(await GradeBenefitDefaults.StageDefaultsForEmployeesAsync(db, employees, null, default));
    }
}

file sealed class BenefitReadCounter : DbCommandInterceptor
{
    public int Count { get; set; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        Count++;
        return ValueTask.FromResult(result);
    }
}
