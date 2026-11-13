using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F3: a second approver decides the pay. The hire an accepted offer becomes must not carry its pay package straight into
/// payroll when the HR approver activates the draft. The package is staged as the same first-package
/// <see cref="SalaryChangeRequest"/> the Add-Employee form stages, the employee waits (not Active, no salary rows) and a
/// different approver with pay-approval rights, who is neither the HR approver nor anyone who made the hire, applies it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OfferHirePayApprovalPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OfferHireApprovalStagesThePackageForASecondApproverAndTheEmployeeWaits()
    {
        var (seed, id) = await SeedConfirmedAsync();
        await SeedPayApproverAsync(seed.Actor.TenantId);
        var hrApprover = Guid.NewGuid();
        await using var db = fixture.CreateDb();

        var approval = await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, hrApprover).ApproveDraft(id, default);

        approval.Result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((approval.Result as ObjectResult)?.Value));
        var employee = await db.Employees.SingleAsync(e => e.TenantId == seed.Actor.TenantId);
        employee.Status.Should().Be(EmployeeStatuses.Draft, "an employee whose pay waits for approver is not in post yet");
        (await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == seed.Actor.TenantId))
            .Should().Be(0, "no salary row exists, so payroll cannot see the figures, until approver approves");
        employee.Salary.Should().BeNull();
        var request = await db.SalaryChangeRequests.SingleAsync(r => r.TenantId == seed.Actor.TenantId);
        request.Status.Should().Be(SalaryChangeRequest.Pending);
        request.Source.Should().Be(SalaryChangeSources.NewHire);
        request.EmployeeId.Should().Be(employee.Id);
        request.BasicSalary.Should().Be(1000m);
        request.FoodAllowance.Should().Be(150m);
        request.Currency.Should().Be("SAR");
        request.EffectiveDate.Should().Be(new DateOnly(2027, 1, 1));
        request.RequestedByUserId.Should().Be(hrApprover);
        (await CompensationApprovalGate.IsOpenAsync(db, seed.Actor.TenantId, employee.Id, default)).Should().BeTrue();
        (await CompensationApprovalGate.NeedsApprovalAsync(db, seed.Actor.TenantId, employee, default)).Should().BeTrue();
        (await db.EmployeeDrafts.SingleAsync(d => d.Id == id)).Status.Should().Be(EmployeeDraftStatuses.Activated);
    }

    /// <summary>The approver here holds the HR Manager role with payroll.approve and made nothing of the hire: an HR Manager CAN approve an offer hire's pay.</summary>
    [Fact]
    public async Task OnlyAPayApproverWhoMadeNothingOfTheHireAppliesThePackage()
    {
        var (seed, id) = await SeedConfirmedAsync();
        var approver = await SeedPayApproverAsync(seed.Actor.TenantId);
        var hrApprover = Guid.NewGuid();
        await using var db = fixture.CreateDb();
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, hrApprover).ApproveDraft(id, default))
            .Result.Should().BeOfType<OkObjectResult>();
        var employee = await db.Employees.SingleAsync(e => e.TenantId == seed.Actor.TenantId);
        var requestId = (await db.SalaryChangeRequests.SingleAsync(r => r.TenantId == seed.Actor.TenantId)).Id;
        var service = new SalaryChangeDecisionService(db, new EmployeeActivationGuard(db));

        // The HR approver entered the package; the recruiter who sent/accepted the offer and confirmed its pay made it.
        var self = await service.ApproveAsync(seed.Actor.TenantId, requestId, new SalaryDecisionActor(hrApprover, "HR", "127.0.0.1"), null, false, null, default);
        self.Should().NotBeOfType<OkObjectResult>("the person who staged the package cannot approve it");
        (await CompensationApprovalGate.IsBarredAsync(db, seed.Actor.TenantId, employee, seed.Actor.UserId, default))
            .Should().BeTrue("whoever made the offer's pay is a maker of the package too");
        (await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == seed.Actor.TenantId)).Should().Be(0);

        db.ChangeTracker.Clear();
        var ok = await new SalaryChangeDecisionService(db, new EmployeeActivationGuard(db))
            .ApproveAsync(seed.Actor.TenantId, requestId, new SalaryDecisionActor(approver, "Pay Approver", "127.0.0.1"), "ok", false, null, default);
        ok.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((ok as ObjectResult)?.Value));

        db.ChangeTracker.Clear();
        var row = await db.EmployeeSalaryStructures.SingleAsync(s => s.TenantId == seed.Actor.TenantId);
        new[] { row.BasicSalary, row.FoodAllowance, row.HousingAllowance, row.TransportAllowance, row.MobileAllowance, row.OtherAllowance }
            .Should().Equal(1000m, 150m, 0m, 0m, 0m, 0m);
        row.EffectiveDate.Should().Be(new DateOnly(2027, 1, 1));
        (await db.Employees.SingleAsync(e => e.Id == employee.Id)).Salary.Should().Be(1150m);
        (await CompensationApprovalGate.IsOpenAsync(db, seed.Actor.TenantId, employee.Id, default)).Should().BeFalse();
        (await CompensationApprovalGate.NeedsApprovalAsync(db, seed.Actor.TenantId, await db.Employees.SingleAsync(e => e.Id == employee.Id), default))
            .Should().BeFalse("once a second approver has decided, the hire can be activated");
    }

    [Fact]
    public async Task WithNoSecondPayApproverTheDraftIsRefusedBeforeAnythingIsWritten()
    {
        var (seed, id) = await SeedConfirmedAsync();
        await using var db = fixture.CreateDb();

        var approval = await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default);

        var refused = approval.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var body = JsonSerializer.Serialize(refused.Value);
        body.Should().Contain("no_eligible_approver").And.Contain("pay-approval rights").And.Contain("did not make this hire").And.NotContain("Finance");
        (await db.Employees.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await db.SalaryChangeRequests.CountAsync(r => r.TenantId == seed.Actor.TenantId)).Should().Be(0);
        EmployeeDraftStatuses.IsOpen((await db.EmployeeDrafts.SingleAsync(d => d.Id == id)).Status).Should().BeTrue("the draft stays open to be approved once a approver approver exists");
    }

    [Fact]
    public async Task AnApproverWhoMadeTheOfferDoesNotCountAsTheSecondApprover()
    {
        var (seed, id) = await SeedConfirmedAsync();
        // The only holder of payroll.approve is the recruiter who sent and accepted the offer and confirmed its pay.
        await GrantPayApproverAsync(seed.Actor.TenantId, seed.Actor.UserId);
        await using var db = fixture.CreateDb();

        var approval = await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default);

        approval.Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await db.Employees.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    private async Task<(OfferLifecycleSeed Seed, Guid Id)> SeedConfirmedAsync()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        offer.OfferedDepartment = ""; offer.OfferedJobTitle = "";
        var draft = OfferCompensationHandoffTests.Draft();
        OfferCompensation.ApplySnapshot(offer, JsonSerializer.Deserialize<OfferCompensationSnapshot>(draft.AcceptedOfferCompensationJson!, OfferCompensation.Json)!);
        await db.SaveChangesAsync();
        var accepted = await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Recruiter");
        var id = accepted.OnboardingDraftId!.Value;
        await DraftStatutoryIdentity.ApplyAsync(db, id, "SA");
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId)
            .PutDraftOfferCompensation(id, new(1, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<OkObjectResult>();
        return (seed, id);
    }

    private Task<Guid> SeedPayApproverAsync(Guid tenantId) => PayApprover.SeedAsync(fixture, tenantId);
    private Task<Guid> GrantPayApproverAsync(Guid tenantId, Guid userId) => PayApprover.GrantAsync(fixture, tenantId, userId);
}

/// <summary>Test support: a approver approver (payroll.approve) for tenants whose hires carry an offer's pay package.</summary>
internal static class PayApprover
{
    public static Task<Guid> SeedAsync(PostgresFixture fixture, Guid tenantId) => GrantAsync(fixture, tenantId, Guid.NewGuid());

    /// <summary>approver decides the hire's staged first package, as the pay-approval screen does. Returns the decision result.</summary>
    public static async Task<Microsoft.AspNetCore.Mvc.IActionResult> ApproveStagedPackageAsync(PostgresFixture fixture, Guid tenantId, Guid approverUserId, int employeeId)
    {
        await using var db = fixture.CreateDb();
        var requestId = (await db.SalaryChangeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId && r.Status == SalaryChangeRequest.Pending).SingleAsync()).Id;
        return await new SalaryChangeDecisionService(db, new EmployeeActivationGuard(db))
            .ApproveAsync(tenantId, requestId, new SalaryDecisionActor(approverUserId, "Pay Approver", "127.0.0.1"), "ok", false, null, default);
    }

    /// <summary>A login holding the HR Manager role with payroll.approve in the tenant, group scope: an approver who made nothing of the hire.</summary>
    public static async Task<Guid> GrantAsync(PostgresFixture fixture, Guid tenantId, Guid userId)
    {
        await using var db = fixture.CreateDb();
        var permission = await db.Permissions.SingleOrDefaultAsync(p => p.Key == "payroll.approve");
        if (permission is null)
        {
            permission = new Permission { Key = "payroll.approve", Module = "Test", Description = "payroll.approve" };
            db.Permissions.Add(permission);
            await db.SaveChangesAsync();
        }
        var role = new Role
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "HR Manager", NormalizedName = AuthService.Normalize("HR Manager"),
            Description = "HR Manager", IsSystem = true, IsEditable = false, IsActive = true, AuthorityLevel = 1,
        };
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        var email = $"pay.approver.{userId:N}@group.example";
        if (!await db.Users.AnyAsync(u => u.Id == userId))
            db.Users.Add(new User
            {
                Id = userId, TenantId = tenantId, Email = email, NormalizedEmail = AuthService.Normalize(email), FullName = "Pay Approver",
                PasswordHash = "test-only-hash", Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
                IsEmailConfirmed = true, IsGroupScope = true,
            });
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();
        return userId;
    }
}
