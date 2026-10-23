using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Two customer-defined leave policy rules, end to end through request submission:
/// a service requirement ("annual leave after 11 months") and a locals/expats scope, so a company can give
/// the two groups different notice, days or encashment as two policies.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LeavePolicyEligibilityPostgresTests
{
    private static readonly DateOnly Day = new(2026, 11, 18); // a Wednesday
    private readonly PostgresFixture _fx;
    public LeavePolicyEligibilityPostgresTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData(6, false)]
    [InlineData(11, true)]   // joined exactly 11 months before the leave: eligible from that day
    [InlineData(24, true)]
    public async Task AServiceRequirement_RefusesLeaveBeforeIt_AndAllowsItAfter(int monthsServed, bool allowed)
    {
        await using var db = _fx.CreateDb();
        var (tenantId, type, company) = await SeedAsync(db, "SA");
        var employee = await EmployeeAsync(db, tenantId, type, company, "Saudi", Day.AddMonths(-monthsServed));
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, LeaveTypeId = type.Id, Name = "Annual after 11 months", Status = "Active",
            AnnualEntitlementDays = 22, AppliesOnProbation = true, MinimumServiceMonths = 11,
        });
        await db.SaveChangesAsync();

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId, "HR");
        var submit = () => service.SubmitRequestAsync(tenantId, Request(tenantId, employee, type));

        if (allowed) await submit.Should().NotThrowAsync();
        else (await submit.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("after 11 month(s) of service").And.Contain(Day.AddMonths(-monthsServed).AddMonths(11).ToString("dd MMM yyyy"));
    }

    [Theory]
    [InlineData("SA", "Saudi", "Locals")]
    [InlineData("SA", "سعودي", "Locals")]
    [InlineData("SA", "Egyptian", "Expats")]
    [InlineData("AE", "AE", "Locals")]
    [InlineData("AE", "IN", "Expats")]
    [InlineData("AE", "Indian", "Expats")]
    [InlineData("AE", "Emirati", "Locals")]
    [InlineData("SA", "Kuwaiti", "Expats")]
    // A typo is unknown, not an expat.
    [InlineData("SA", "Suadi", "All staff")]
    // A nationality the platform cannot place against a UAE company is never guessed into a group.
    [InlineData("AE", "Martian", "All staff")]
    [InlineData("SA", "", "All staff")]
    public async Task LocalsAndExpats_ResolveToTheirOwnPolicy(string companyCountry, string nationality, string expected)
    {
        await using var db = _fx.CreateDb();
        var (tenantId, type, company) = await SeedAsync(db, companyCountry);
        var employee = await EmployeeAsync(db, tenantId, type, company, nationality, new DateOnly(2020, 1, 1));
        LeavePolicy Policy(string name, string scope) => new()
        {
            TenantId = tenantId, LeaveTypeId = type.Id, Name = name, Status = "Active", AnnualEntitlementDays = 30,
            AppliesOnProbation = true, NationalityScope = scope,
        };
        var policies = new[]
        {
            Policy("All staff", LeaveNationalityScopes.Any),
            Policy("Locals", LeaveNationalityScopes.National),
            Policy("Expats", LeaveNationalityScopes.Expatriate),
        };
        db.LeavePolicies.AddRange(policies);
        await db.SaveChangesAsync();

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId, "HR");
        var submitted = await service.SubmitRequestAsync(tenantId, Request(tenantId, employee, type));

        submitted.PolicyId.Should().Be(policies.Single(p => p.Name == expected).Id);
    }

    [Fact]
    public async Task Migration_DefaultsExistingPolicies_AndRollbackRefusesToDiscardConfiguredRules()
    {
        const string migrationId = "20261023144637_AddLeavePolicyEligibilityAndNationality";
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        var previous = migrations[migrations.IndexOf(migrationId) - 1];
        await migrator.MigrateAsync(migrationId);

        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var type = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true, IsPaid = true };
        db.LeaveTypes.Add(type);
        var policy = new LeavePolicy { TenantId = tenantId, LeaveTypeId = type.Id, Name = "Expats", Status = "Active", MinimumServiceMonths = 12, NationalityScope = LeaveNationalityScopes.Expatriate };
        db.LeavePolicies.Add(policy);
        await db.SaveChangesAsync();

        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(previous));
        refused.SqlState.Should().Be(Npgsql.PostgresErrorCodes.RaiseException);

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE leave_policies SET minimum_service_months = 0, nationality_scope = 'Any' WHERE id = {policy.Id}");
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync(migrationId);
        await migrator.MigrateAsync(migrationId); // replay is a no-op

        (await db.Database.SqlQuery<int>($"SELECT minimum_service_months AS \"Value\" FROM leave_policies WHERE id = {policy.Id}").SingleAsync()).Should().Be(0);
        (await db.Database.SqlQuery<string>($"SELECT nationality_scope AS \"Value\" FROM leave_policies WHERE id = {policy.Id}").SingleAsync()).Should().Be("Any");
        var badScope = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE leave_policies SET nationality_scope = 'Saudi' WHERE id = {policy.Id}"));
        badScope.SqlState.Should().Be(Npgsql.PostgresErrorCodes.CheckViolation);
    }

    private static async Task<(Guid TenantId, LeaveType Type, Company Company)> SeedAsync(ZayraDbContext db, string country)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var type = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true, IsPaid = true };
        var company = new Company { TenantId = tenantId, LegalNameEn = $"Co {country}", CountryCode = country };
        db.LeaveTypes.Add(type);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenantId, type, company);
    }

    private static async Task<Employee> EmployeeAsync(ZayraDbContext db, Guid tenantId, LeaveType type, Company company, string nationality, DateOnly joined)
    {
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"EL-{Guid.NewGuid():N}"[..14], FullName = "Eligibility Person",
            UserAccountId = Guid.NewGuid(), Status = "Active", WorkEmail = $"{Guid.NewGuid():N}@p.test", Nationality = nationality,
            JoiningDate = joined.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, LeaveTypeId = type.Id,
            LeaveTypeName = type.NameEn, Year = Day.Year, Entitled = 30,
        });
        return employee;
    }

    private static LeaveRequest Request(Guid tenantId, Employee employee, LeaveType type) => new()
    {
        TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = type.Id, StartDate = Day, EndDate = Day, DayType = "Full",
    };
}
