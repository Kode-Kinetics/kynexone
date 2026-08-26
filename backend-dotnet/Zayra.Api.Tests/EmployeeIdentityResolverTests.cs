using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class EmployeeIdentityResolverTests
{
    [Fact]
    public async Task ResolveEmployee_AcceptsPublicOrInternalId_AndReturnsCanonicalPair()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = Employee(tenantId, "EMP-001");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var byPublic = await db.ResolveEmployeeAsync(tenantId, employee.PublicId, null, default);
        var byInternal = await db.ResolveEmployeeAsync(tenantId, Guid.Empty, employee.Id, default);

        byPublic.IsSuccess.Should().BeTrue();
        byInternal.IsSuccess.Should().BeTrue();
        byPublic.Employee!.Id.Should().Be(employee.Id);
        byInternal.Employee!.PublicId.Should().Be(employee.PublicId);
    }

    [Fact]
    public async Task ResolveEmployee_RejectsMismatchedPublicAndInternalIdentifiers()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var first = Employee(tenantId, "EMP-001");
        var second = Employee(tenantId, "EMP-002");
        db.Employees.AddRange(first, second);
        await db.SaveChangesAsync();

        var result = await db.ResolveEmployeeAsync(tenantId, first.PublicId, second.Id, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("do not identify the same employee");
    }

    [Fact]
    public async Task ResolveEmployee_NeverCrossesTenantBoundary()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var employee = Employee(tenantA, "EMP-001");
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await db.ResolveEmployeeAsync(tenantB, employee.PublicId, employee.Id, default);

        result.IsSuccess.Should().BeFalse();
        result.Employee.Should().BeNull();
    }

    [Fact]
    public async Task ResolveEmployee_RejectsMissingIdentity_InsteadOfGeneratingPlaceholder()
    {
        await using var db = CreateDb();

        var result = await db.ResolveEmployeeAsync(Guid.NewGuid(), Guid.Empty, null, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("valid employeeId or employeeIntId");
    }

    [Fact]
    public async Task ActivatedDraft_LinksApplicationTasksToEmployeePublicId()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var employee = Employee(tenantId, "EMP-ONBOARD");
        var application = new JobApplication
        {
            TenantId = tenantId,
            OnboardingDraftId = draftId,
            CandidateName = "New Hire",
        };
        var task = new OnboardingTask
        {
            TenantId = tenantId,
            ApplicationId = application.Id,
            TaskTitle = "Issue laptop",
        };
        db.AddRange(employee, application, task);
        await db.SaveChangesAsync();

        var linked = await db.LinkOnboardingTasksForActivatedDraftAsync(
            tenantId, draftId, employee, default);
        await db.SaveChangesAsync();

        linked.Should().Be(1);
        (await db.OnboardingTasks.SingleAsync()).EmployeeId.Should().Be(employee.PublicId);
    }

    [Fact]
    public async Task ActivatedDraft_RejectsTaskAlreadyLinkedToDifferentEmployee()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var employee = Employee(tenantId, "EMP-ONBOARD");
        var application = new JobApplication { TenantId = tenantId, OnboardingDraftId = draftId };
        db.AddRange(employee, application, new OnboardingTask
        {
            TenantId = tenantId,
            ApplicationId = application.Id,
            EmployeeId = Guid.NewGuid(),
            TaskTitle = "Issue laptop",
        });
        await db.SaveChangesAsync();

        Func<Task> action = async () => await db.LinkOnboardingTasksForActivatedDraftAsync(
            tenantId, draftId, employee, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different employee identity*");
    }

    private static Employee Employee(Guid tenantId, string code) => new()
    {
        TenantId = tenantId,
        EmployeeCode = code,
        FullName = $"Employee {code}",
        EnglishName = $"Employee {code}",
        Status = EmployeeStatuses.Active,
    };

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
