using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public sealed class EmployeeEmailDomainIndependenceTests
{
    [Theory]
    [InlineData("company.test")]
    [InlineData(null)]
    public async Task Provisioner_StagesAnExternalAddress_WithoutActivatingAccess(string? companyDomain)
    {
        await using var db = CreateDb();
        var employee = await SeedAsync(db, companyDomain, "noah@issued.example");
        var tenantId = employee.TenantId!.Value;
        var result = await new EmployeeLoginProvisioner(db).EnsureStagedLoginAsync(tenantId, employee, Actor(tenantId), default);
        result.Result.Should().Be("staged");
        await db.SaveChangesAsync();
        var login = await db.Users.IgnoreQueryFilters().SingleAsync();
        login.Email.Should().Be("noah@issued.example");
        login.Status.Should().Be("PendingPasswordSetup");
        login.IsActive.Should().BeFalse();
        login.IsEmailConfirmed.Should().BeFalse();
        login.AccessMode.Should().Be(AccessModes.NoLogin);
        new Pbkdf2PasswordHasher().Verify("anything", login.PasswordHash).Should().BeFalse();
        (await db.UserEntityAccesses.IgnoreQueryFilters().SingleAsync()).IsActive.Should().BeFalse();
        var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync();
        link.RequiresPasswordSetup.Should().BeTrue();
        link.Status.Should().Be("PendingPasswordSetup");
        (await EmployeeAccessStates.EvaluateAsync(db, tenantId, [employee.Id], DateTime.UtcNow, default))[employee.Id].State.State
            .Should().Be(EmployeeAccessStates.NotStarted);
    }

    [Theory]
    [InlineData("company.test")]
    [InlineData(null)]
    public async Task Backfill_AcceptsExternalAddress_AndStagesOnlyOnSave(string? companyDomain)
    {
        await using var db = CreateDb();
        var employee = await SeedAsync(db, companyDomain, "");
        var tenantId = employee.TenantId!.Value;
        var service = new EmployeeAccessService(db, new EmployeeAccessTests.RecordingEmail(false), Options.Create(new JwtOptions()));
        var request = new WorkEmailBackfillRequest([new(employee.EmployeeCode, "noah@issued.example")], true);
        var preview = await service.BackfillWorkEmailsAsync(tenantId, request, EntityScopeContext.GroupLevel, Actor(tenantId), default);
        preview.Matched.Should().ContainSingle();
        preview.WrongDomain.Should().BeEmpty();
        preview.Conflicts.Should().BeEmpty();
        (await db.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        var saved = await service.BackfillWorkEmailsAsync(tenantId, request with { DryRun = false }, EntityScopeContext.GroupLevel, Actor(tenantId), default);
        saved.Saved.Should().Be(1);
        var login = await db.Users.IgnoreQueryFilters().SingleAsync();
        login.Status.Should().Be("PendingPasswordSetup");
        login.AccessMode.Should().Be(AccessModes.NoLogin);
        login.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData("noah")]
    [InlineData("noah@")]
    [InlineData("noah@@issued.example")]
    public async Task MalformedAddresses_AreBlockedInProvisioningAndBackfill(string email)
    {
        await using var db = CreateDb();
        var employee = await SeedAsync(db, "company.test", email);
        var tenantId = employee.TenantId!.Value;
        var outcome = await new EmployeeLoginProvisioner(db).EnsureStagedLoginAsync(tenantId, employee, Actor(tenantId), default);
        outcome.BlockedCode.Should().Be("work_email_invalid_format");
        var service = new EmployeeAccessService(db, new EmployeeAccessTests.RecordingEmail(false), Options.Create(new JwtOptions()));
        var result = await service.BackfillWorkEmailsAsync(tenantId, new([new(employee.EmployeeCode, email)], false), EntityScopeContext.GroupLevel, Actor(tenantId), default);
        result.Conflicts.Should().ContainSingle(x => x.Reason == "work_email_invalid_format");
        result.Saved.Should().Be(0);
        (await db.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ExternalAddressAlreadyOwnedByLogin_IsNeverAdoptedOrBackfilled()
    {
        await using var db = CreateDb();
        var employee = await SeedAsync(db, null, "noah@issued.example");
        var tenantId = employee.TenantId!.Value;
        db.Users.Add(new User { TenantId = tenantId, Email = "noah@issued.example", NormalizedEmail = "NOAH@ISSUED.EXAMPLE", FullName = "Existing owner" });
        await db.SaveChangesAsync();
        var outcome = await new EmployeeLoginProvisioner(db).EnsureStagedLoginAsync(tenantId, employee, Actor(tenantId), default);
        outcome.BlockedCode.Should().Be("email_belongs_to_existing_login");
        (await db.EmployeeUserAccounts.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        employee.WorkEmail = "";
        await db.SaveChangesAsync();
        var service = new EmployeeAccessService(db, new EmployeeAccessTests.RecordingEmail(false), Options.Create(new JwtOptions()));
        var result = await service.BackfillWorkEmailsAsync(tenantId, new([new(employee.EmployeeCode, "noah@issued.example")], false), EntityScopeContext.GroupLevel, Actor(tenantId), default);
        result.Conflicts.Should().ContainSingle(x => x.Reason == "email_belongs_to_existing_login");
        result.Saved.Should().Be(0);
        (await db.Users.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    private static RequestContext Actor(Guid tenantId) => new("127.0.0.1", "tests", Guid.NewGuid(), tenantId);
    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Employee> SeedAsync(ZayraDbContext db, string? companyDomain, string email)
    {
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Email company", EmailDomain = companyDomain ?? string.Empty };
        db.Companies.Add(company);
        db.Roles.Add(new Role { TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", IsActive = true });
        var employee = new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "MAIL-1", FullName = "Noah Williams", WorkEmail = email, Status = "Active" };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }
}
