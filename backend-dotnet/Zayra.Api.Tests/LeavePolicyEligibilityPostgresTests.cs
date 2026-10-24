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
        // Raw SQL, not db.LeavePolicies: the current model selects columns (company_id, legacy_group_default, ...)
        // that later migrations add and this migration point does not have yet.
        var policyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO leave_policies
                (id, tenant_id, name, leave_type_id, country_code, department_name, grade, employment_type,
                 contract_type, gender, applies_on_probation, annual_entitlement_days, accrual_method,
                 carry_forward_max, carry_forward_expiry, encashment_allowed, encashment_max_days,
                 minimum_days_per_request, maximum_days_per_request, notice_required_days,
                 weekends_included, public_holidays_included, payroll_impact,
                 allows_hajj_beyond_statutory_eligibility, status, minimum_service_months, nationality_scope,
                 created_at_utc, updated_at_utc)
            VALUES
                ({policyId}, {tenantId}, 'Expats', {typeId}, '', '', '', '', '', '',
                 false, 24, 'Monthly', 0, 0, false, 0, 1, 30, 0, false, false, 'Full', false,
                 'Active', 12, {LeaveNationalityScopes.Expatriate}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            """);

        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(previous));
        refused.SqlState.Should().Be(Npgsql.PostgresErrorCodes.RaiseException);

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE leave_policies SET minimum_service_months = 0, nationality_scope = 'Any' WHERE id = {policyId}");
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync(migrationId);
        await migrator.MigrateAsync(migrationId); // replay is a no-op

        (await db.Database.SqlQuery<int>($"SELECT minimum_service_months AS \"Value\" FROM leave_policies WHERE id = {policyId}").SingleAsync()).Should().Be(0);
        (await db.Database.SqlQuery<string>($"SELECT nationality_scope AS \"Value\" FROM leave_policies WHERE id = {policyId}").SingleAsync()).Should().Be("Any");
        var badScope = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE leave_policies SET nationality_scope = 'Saudi' WHERE id = {policyId}"));
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
