using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public sealed class PolicyDocumentGovernanceHttpTests
{
    [Fact]
    public async Task HrWithoutAiQueryCanPublishAndEmployeeWithoutAiQuerySeesOnlyPublishedPolicy()
    {
        var connection = $"Data Source=file:policy-http-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connection);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connection,
            hostSettings: new Dictionary<string, string?> { ["AI_PROVIDER"] = "none" });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tenantId = Guid.NewGuid(); var companyId = Guid.NewGuid(); var otherCompanyId = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Policy HTTP", Slug = $"policy-{tenantId:N}", IsActive = true });
            db.Companies.Add(new Company { Id = companyId, TenantId = tenantId, LegalNameEn = "Policy Company", CountryCode = "SA", IsActive = true });
            db.Companies.Add(new Company { Id = otherCompanyId, TenantId = tenantId, LegalNameEn = "Another Company", CountryCode = "SA", IsActive = true });
            var hr = User("hr"); var employee = User("employee"); var denied = User("denied");
            db.Users.AddRange(hr, employee, denied);
            var role = new Role { TenantId = tenantId, Name = "HR Manager", NormalizedName = "HR MANAGER" };
            db.Roles.Add(role);
            db.Set<UserRole>().AddRange(new UserRole { UserId = hr.Id, RoleId = role.Id }, new UserRole { UserId = denied.Id, RoleId = role.Id });
            db.Set<UserPermissionOverride>().AddRange(Grant(hr.Id, "organization.write"), Grant(employee.Id, "ess.read"));
            var record = new Employee { TenantId = tenantId, CompanyId = companyId, FullName = "Linked employee", EnglishName = "Linked employee",
                EmployeeCode = "POLICY-1", Status = "Active", JoiningDate = DateTime.UtcNow, UserAccountId = employee.Id };
            db.Employees.Add(record);
            await db.SaveChangesAsync();
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = tenantId, EmployeeId = record.Id, UserId = employee.Id,
                AccessMode = "FullPortal", Status = "Active", IsPrimary = true, RequiresPasswordSetup = false });
            await db.SaveChangesAsync();
        }
        var hrToken = await Token("hr"); var employeeToken = await Token("employee"); var deniedToken = await Token("denied");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/ai/policy/documents/upload");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hrToken);
        upload.Content = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Annual leave is thirty days.")), "file", "handbook.txt" } };
        using var uploaded = await client.SendAsync(upload);
        uploaded.StatusCode.Should().Be(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());
        var doc = (await uploaded.Content.ReadFromJsonAsync<PolicyDocumentDto>())!;
        doc.PublicationStatus.Should().Be("Draft");
        (await Documents()).Should().BeEmpty();
        using var deniedPublish = await Send(HttpMethod.Post, $"/api/ai/policy/documents/{doc.Id}/publish", deniedToken,
            new PublishPolicyRequest(companyId, doc.ContentSha256));
        deniedPublish.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var stale = await Send(HttpMethod.Post, $"/api/ai/policy/documents/{doc.Id}/publish", hrToken,
            new PublishPolicyRequest(companyId, new string('f', 64)));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict, await stale.Content.ReadAsStringAsync());
        using var publish = await Send(HttpMethod.Post, $"/api/ai/policy/documents/{doc.Id}/publish", hrToken,
            new PublishPolicyRequest(companyId, doc.ContentSha256));
        publish.StatusCode.Should().Be(HttpStatusCode.OK, await publish.Content.ReadAsStringAsync());
        using var reassign = await Send(HttpMethod.Post, $"/api/ai/policy/documents/{doc.Id}/publish", hrToken,
            new PublishPolicyRequest(otherCompanyId, doc.ContentSha256));
        reassign.StatusCode.Should().Be(HttpStatusCode.Conflict, await reassign.Content.ReadAsStringAsync());
        (await Documents()).Should().ContainSingle().Which.Id.Should().Be(doc.Id);
        using var question = await Send(HttpMethod.Post, "/api/ai/policy/employee/ask", employeeToken,
            new PolicyAskRequest("What is the annual leave allowance?"));
        question.StatusCode.Should().Be(HttpStatusCode.OK, await question.Content.ReadAsStringAsync());
        var answer = (await question.Content.ReadFromJsonAsync<PolicyAskResponse>())!;
        answer.Citations.Should().ContainSingle().Which.DocumentId.Should().Be(doc.Id);
        answer.Provider.Should().Be("fallback");
        using var text = await Send(HttpMethod.Get, $"/api/ai/policy/employee/documents/{doc.Id}/text", employeeToken);
        text.StatusCode.Should().Be(HttpStatusCode.OK, await text.Content.ReadAsStringAsync());
        using var withdraw = await Send(HttpMethod.Post, $"/api/ai/policy/documents/{doc.Id}/withdraw", hrToken);
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK, await withdraw.Content.ReadAsStringAsync());
        (await Documents()).Should().BeEmpty();
        using var hidden = await Send(HttpMethod.Get, $"/api/ai/policy/employee/documents/{doc.Id}/text", employeeToken);
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId && a.Action == "policy.document_published")).Should().Be(1);
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId && a.Action == "policy.document_withdrawn")).Should().Be(1);
        }

        User User(string name) => new() { TenantId = tenantId, Email = $"{name}@policy-http.local", NormalizedEmail = $"{name}@policy-http.local".ToUpperInvariant(),
            FullName = name, PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true };
        UserPermissionOverride Grant(Guid user, string permission) => new() { TenantId = tenantId, UserId = user, PermissionKey = permission, Effect = "Allow", IsActive = true };
        async Task<string> Token(string name)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
                .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
                .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
                .SingleAsync(u => u.TenantId == tenantId && u.Email == $"{name}@policy-http.local");
            return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user), AuthService.GetPermissions(user),
                user.Tenant!, Array.Empty<EntityAccessGrant>(), EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
        }
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body);
            return await client.SendAsync(request);
        }
        async Task<List<PolicyDocumentDto>> Documents()
        {
            using var response = await Send(HttpMethod.Get, "/api/ai/policy/employee/documents", employeeToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<List<PolicyDocumentDto>>())!;
        }
    }
}
