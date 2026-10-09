using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EmployeeCompletionHttpTests(EmployeeDraftCreateHttpFixture fx) : IClassFixture<EmployeeDraftCreateHttpFixture>
{
    [Fact]
    public async Task DraftRequestIsPersistedIdempotentlyWithoutGrantingAccessOrActivatingEmployee()
    {
        var id = await CreateDraft();
        using var first = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var state = await first.Content.ReadFromJsonAsync<EmployeeCompletionDto>();
        state!.Requested.Should().BeTrue();
        state.SelfServiceAvailable.Should().BeFalse();
        state.Status.Should().Be("Open");
        using var repeat = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.EmployeeActionItems.CountAsync(x => x.EmployeeId == id)).Should().Be(1);
        (await db.EmployeeUserAccounts.CountAsync(x => x.EmployeeId == id)).Should().Be(0);
        (await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id)).Status.Should().Be("Draft");
        (await db.EmployeeSelfServiceAuditLogs.CountAsync(x => x.EmployeeId == id && x.Action == "employee.completion.requested")).Should().Be(1);
    }

    [Fact]
    public async Task ContactSubmissionWaitsForHrApprovalAndCompletesTheSavedActionOnlyWhenApproved()
    {
        var (id, token) = await CreateLinkedEmployee();
        using var requested = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        requested.EnsureSuccessStatusCode();
        using var submitted = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile",
            new { changes = new { phone = "0509999999", personalEmail = "personal@example.test" } }, token, false);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var state = (await submitted.Content.ReadFromJsonAsync<EmployeeCompletionDto>())!;
        state.Status.Should().Be("PendingHR");
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            (await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id)).Phone.Should().BeEmpty();
        }
        using var duplicate = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile",
            new { changes = new { phone = "0509999999" } }, token, false);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var approve = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{state.RequestId}/approve", new { notes = "Verified" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        using var mine = await fx.SendAsync(HttpMethod.Get, "/api/ess/employee-completion", token: token, useWriter: false);
        var updated = (await mine.Content.ReadFromJsonAsync<EmployeeCompletionDto>())!;
        updated.Status.Should().Be("Approved"); updated.Profile!["phone"].Should().Be("0509999999");
        using var finalScope = fx.Host.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await finalDb.EmployeeActionItems.SingleAsync(x => x.EmployeeId == id)).Status.Should().Be("Completed");
    }

    [Fact]
    public async Task RejectionKeepsTaskOpenReturnsCorrectionNoteAndAllowsResubmission()
    {
        var (id, token) = await CreateLinkedEmployee();
        using var requested = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        requested.EnsureSuccessStatusCode();
        using var submitted = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile", new { changes = new { phone = "0501111111" } }, token, false);
        var state = (await submitted.Content.ReadFromJsonAsync<EmployeeCompletionDto>())!;
        using var rejected = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{state.RequestId}/reject", new { notes = "Please check the phone number." });
        rejected.StatusCode.Should().Be(HttpStatusCode.OK, await rejected.Content.ReadAsStringAsync());
        using var mine = await fx.SendAsync(HttpMethod.Get, "/api/ess/employee-completion", token: token, useWriter: false);
        var correction = (await mine.Content.ReadFromJsonAsync<EmployeeCompletionDto>())!;
        correction.Status.Should().Be("Rejected"); correction.ReviewNote.Should().Be("Please check the phone number.");
        using var resubmit = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile", new { changes = new { phone = "0502222222" } }, token, false);
        resubmit.StatusCode.Should().Be(HttpStatusCode.OK, await resubmit.Content.ReadAsStringAsync());
        ((await resubmit.Content.ReadFromJsonAsync<EmployeeCompletionDto>())!.RequestId == state.RequestId).Should().BeFalse();
    }

    [Fact]
    public async Task GenericEssRequestShowsExactlyTheEffectiveValuesThatApprovalWillApply()
    {
        var (id, token) = await CreateLinkedEmployee();
        using var requested = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        requested.EnsureSuccessStatusCode();
        // The generic route predates completion and accepts JsonElement values plus case variants.
        // Both casing variants remain distinct in its stored dictionary; approval applies them in order.
        var changes = new Dictionary<string, object?>
        {
            ["preferredName"] = "First value", ["PREFERREDNAME"] = null,
            ["PersonalEmail"] = " first@example.test ", ["personalEmail"] = " final@example.test ",
            ["phone"] = 12345, ["MaritalStatus"] = true,
            ["EmergencyContactName"] = new { first = "Relative" },
            ["EMERGENCYCONTACTPHONE"] = new object[] { " 050 ", 987 },
        };
        using var submitted = await fx.SendAsync(HttpMethod.Put, "/api/ess/profile-change-request",
            new { reason = EmployeeCompletionController.RequestReason, changes }, token, false);
        submitted.StatusCode.Should().Be(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var changeId = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var review = await fx.SendAsync(HttpMethod.Get, $"/api/employee-completion/{id}");
        review.StatusCode.Should().Be(HttpStatusCode.OK, await review.Content.ReadAsStringAsync());
        var state = (await review.Content.ReadFromJsonAsync<EmployeeCompletionDto>())!;
        state.Status.Should().Be("PendingHR");
        state.RequestId.Should().Be(changeId);
        var expected = new Dictionary<string, string>
        {
            ["preferredName"] = "", ["personalEmail"] = "final@example.test", ["phone"] = "12345",
            ["maritalStatus"] = "True", ["emergencyContactName"] = "{\"first\":\"Relative\"}",
            ["emergencyContactPhone"] = "[\" 050 \",987]",
        };
        state.Changes.Should().BeEquivalentTo(expected);
        using var approve = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{changeId}/approve", new { notes = "Reviewed the exact proposed values." });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        using var scope = fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await db.Employees.SingleAsync(x => x.Id == id);
        new Dictionary<string, string>
        {
            ["preferredName"] = employee.PreferredName, ["personalEmail"] = employee.PersonalEmail, ["phone"] = employee.Phone,
            ["maritalStatus"] = employee.MaritalStatus, ["emergencyContactName"] = employee.EmergencyContactName,
            ["emergencyContactPhone"] = employee.EmergencyContactPhone,
        }.Should().BeEquivalentTo(state.Changes);
        (await db.EmployeeActionItems.SingleAsync(x => x.EmployeeId == id)).Status.Should().Be("Completed");
    }

    [Theory]
    [InlineData("bankIban", "SA0000000000000000000000")]
    [InlineData("salary", "999999")]
    [InlineData("workEmail", "takeover@example.test")]
    [InlineData("personalEmail", "bad-email")]
    [InlineData("maritalStatus", "unknown")]
    public async Task EmployeeCannotSubmitFinancialIdentityOrInvalidContactFields(string key, string value)
    {
        var (id, token) = await CreateLinkedEmployee();
        using var requested = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        requested.EnsureSuccessStatusCode();
        using var result = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile",
            new { changes = new Dictionary<string, string> { [key] = value } }, token, false);
        result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OwnOnlyAndHrPermissionBoundariesAreEnforcedByHttpPipeline()
    {
        var id = await CreateDraft();
        var (_, token) = await CreateLinkedEmployee();
        using var anonymous = await fx.SendAsync(HttpMethod.Get, $"/api/employee-completion/{id}", useWriter: false);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var readOnly = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}", token: fx.ReaderToken, useWriter: false);
        readOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var employee = await fx.SendAsync(HttpMethod.Get, $"/api/employee-completion/{id}", token: token, useWriter: false);
        employee.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var unlinked = await fx.SendAsync(HttpMethod.Get, "/api/ess/employee-completion", token: fx.ReaderToken, useWriter: false);
        unlinked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var beforeRequest = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile", new { changes = new { phone = "0501234567" } }, token, false);
        beforeRequest.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task HrCannotReadOrRequestCompletionOutsideTenantOrCompanyScope()
    {
        var ownId = await CreateDraft();
        int foreignId;
        var pendingId = Guid.NewGuid();
        string restrictedToken;
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var foreignTenant = new Tenant { Name = "Foreign completion", Slug = $"foreign-{Guid.NewGuid():N}", IsActive = true };
            var foreignCompany = new Company { TenantId = foreignTenant.Id, LegalNameEn = "Foreign", CountryCode = "SA", IsActive = false };
            var excludedCompany = new Company { TenantId = fx.TenantId, LegalNameEn = "Excluded", CountryCode = "SA", IsActive = true };
            var foreignEmployee = new Employee { TenantId = foreignTenant.Id, CompanyId = foreignCompany.Id,
                EmployeeCode = $"FOREIGN-{Guid.NewGuid():N}", EnglishName = "Foreign employee", Status = "Draft" };
            db.Tenants.Add(foreignTenant); db.Companies.AddRange(foreignCompany, excludedCompany); db.Employees.Add(foreignEmployee);
            db.EmployeeProfileChangeRequests.Add(new EmployeeProfileChangeRequest { Id = pendingId, TenantId = fx.TenantId,
                EmployeeId = ownId, Reason = EmployeeCompletionController.RequestReason, RequestedChangesJson = "{\"phone\":\"0501111111\"}" });
            var user = new User { TenantId = fx.TenantId, Email = $"scoped-{Guid.NewGuid():N}@example.test", FullName = "Scoped HR",
                PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = false };
            user.NormalizedEmail = user.Email.ToUpperInvariant();
            db.Users.Add(user);
            var role = await db.Roles.IgnoreQueryFilters().SingleAsync(x => x.TenantId == fx.TenantId && x.Name == "HR Officer");
            db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = fx.TenantId, UserId = user.Id,
                PermissionKey = "employees.write", Effect = "Allow", IsActive = true, Reason = "Completion scope regression" });
            db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = fx.TenantId, UserId = user.Id, CompanyId = excludedCompany.Id,
                Role = "HR Officer", IsActive = true });
            await db.SaveChangesAsync();
            foreignId = foreignEmployee.Id;
            var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(x => x.Tenant).Include(x => x.PermissionOverrides)
                .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
                .Include(x => x.EmployeeUserAccounts).SingleAsync(x => x.Id == user.Id);
            var grants = new[] { new EntityAccessGrant(excludedCompany.Id, "HR Officer") };
            restrictedToken = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(loaded, AuthService.GetRoles(loaded),
                AuthService.GetPermissions(loaded), loaded.Tenant!, grants, EntityScopeClaims.Resolve(false, grants, [excludedCompany.Id]), out _);
        }
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var foreign = await fx.SendAsync(method, $"/api/employee-completion/{foreignId}");
            foreign.StatusCode.Should().Be(HttpStatusCode.NotFound, await foreign.Content.ReadAsStringAsync());
            using var excluded = await fx.SendAsync(method, $"/api/employee-completion/{ownId}", token: restrictedToken, useWriter: false);
            excluded.StatusCode.Should().Be(HttpStatusCode.NotFound, await excluded.Content.ReadAsStringAsync());
        }
        foreach (var decision in new[] { "approve", "reject" })
        {
            using var response = await fx.SendAsync(HttpMethod.Post, $"/api/ess/profile-change-requests/{pendingId}/{decision}",
                new { notes = "Out-of-company decision must be refused." }, restrictedToken, false);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        }
        using var finalScope = fx.Host.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await finalDb.EmployeeActionItems.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == ownId || x.EmployeeId == foreignId)).Should().Be(0);
        (await finalDb.EmployeeProfileChangeRequests.SingleAsync(x => x.Id == pendingId)).Status.Should().Be("PendingHR");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Terminated")]
    public async Task StaleEmployeeTokenCannotReadOrSubmitAfterEligibilityIsRemoved(string status)
    {
        var (id, token) = await CreateLinkedEmployee();
        using var requested = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        requested.EnsureSuccessStatusCode();
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            (await db.Employees.SingleAsync(x => x.Id == id)).Status = status;
            await db.SaveChangesAsync();
        }
        using var read = await fx.SendAsync(HttpMethod.Get, "/api/ess/employee-completion", token: token, useWriter: false);
        ((int)read.StatusCode).Should().BeOneOf(401, 404);
        using var submit = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile", new { changes = new { phone = "0507777777" } }, token, false);
        ((int)submit.StatusCode).Should().BeOneOf(401, 404);
        using var finalScope = fx.Host.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await finalDb.EmployeeProfileChangeRequests.CountAsync(x => x.EmployeeId == id)).Should().Be(0);
        (await finalDb.Employees.SingleAsync(x => x.Id == id)).Status.Should().Be(status);
    }

    [Fact]
    public async Task SelfSubmissionUsesLinkedEmployeeDespiteForeignEmployeeIdInPayload()
    {
        var (id, token) = await CreateLinkedEmployee();
        var foreignId = await CreateDraft();
        using var requested = await fx.SendAsync(HttpMethod.Post, $"/api/employee-completion/{id}");
        requested.EnsureSuccessStatusCode();
        using var submitted = await fx.SendAsync(HttpMethod.Post, "/api/ess/employee-completion/profile",
            new { employeeId = foreignId, changes = new { phone = "0508888888" } }, token, false);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var state = (await submitted.Content.ReadFromJsonAsync<EmployeeCompletionDto>())!;
        using var scope = fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var queued = await db.EmployeeProfileChangeRequests.SingleAsync(x => x.Id == state.RequestId);
        queued.EmployeeId.Should().Be(id);
        (await db.EmployeeProfileChangeRequests.CountAsync(x => x.EmployeeId == foreignId)).Should().Be(0);
        (await db.Employees.SingleAsync(x => x.Id == id)).Status.Should().Be("Active");
        (await db.Employees.SingleAsync(x => x.Id == foreignId)).Status.Should().Be("Draft");
    }

    private async Task<int> CreateDraft()
    {
        using var result = await fx.SendAsync(HttpMethod.Post, "/api/employees", new { englishName = $"Completion {Guid.NewGuid():N}", companyId = fx.CompanyId });
        result.EnsureSuccessStatusCode();
        return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<(int Id, string Token)> CreateLinkedEmployee()
    {
        var id = await CreateDraft();
        using var scope = fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        var user = new User
        {
            TenantId = fx.TenantId, Email = $"completion-{id}@example.test", NormalizedEmail = $"COMPLETION-{id}@EXAMPLE.TEST",
            FullName = employee.FullName, PasswordHash = "no-login-path", Status = "Active", AccessMode = "ESSOnly",
            IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        employee.Status = "Active"; employee.UserAccountId = user.Id;
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = fx.TenantId, EmployeeId = id, UserId = user.Id, AccessMode = "ESSOnly", Status = "Active", IsPrimary = true, RequiresPasswordSetup = false });
        foreach (var permission in new[] { "ess.read", "ess.write" })
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = fx.TenantId, UserId = user.Id, PermissionKey = permission, Effect = "Allow", IsActive = true, Reason = "Self-completion HTTP regression" });
        await db.SaveChangesAsync();
        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(x => x.Tenant).Include(x => x.PermissionOverrides).Include(x => x.EmployeeUserAccounts).SingleAsync(x => x.Id == user.Id);
        var grants = Array.Empty<EntityAccessGrant>();
        var entityScope = EntityScopeClaims.Resolve(true, grants, Array.Empty<Guid>());
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var token = tokens.CreateAccessToken(loaded, AuthService.GetRoles(loaded), AuthService.GetPermissions(loaded), loaded.Tenant!, grants, entityScope, out _);
        return (id, token);
    }
}
