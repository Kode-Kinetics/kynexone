using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

[Collection("AuthorizationPipeline")]
public sealed class GradeBenefitHttpAuthorizationTests
{
    private readonly AuthorizationPipelineFixture _fixture;
    public GradeBenefitHttpAuthorizationTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AnonymousAndReadOnlyUsers_CannotReadHireDefaultsOrWriteExceptions()
    {
        using var anonymous = await _fixture.Client.GetAsync(PreviewUrl());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var read = await SendAsync(HttpMethod.Get, PreviewUrl(), _fixture.TenantTokenWithoutPermission);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        using var mutation = await SendAsync(HttpMethod.Patch, ExceptionUrl(), _fixture.TenantTokenWithoutPermission, ExceptionBody());
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
    }

    [Fact]
    public async Task EmployeeCreator_CanReachPreview_ButCannotGrantBenefitExceptions()
    {
        var token = await TokenAsync("employees.read", "employees.write");
        using var preview = await SendAsync(HttpMethod.Get, PreviewUrl(), token);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode); // action validates unknown grade/company
        using var mutation = await SendAsync(HttpMethod.Patch, ExceptionUrl(), token, ExceptionBody());
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
    }

    [Fact]
    public async Task ApprovalAuthority_ReachesExceptionAction_WithoutGivingOtherRolesThatAuthority()
    {
        var token = await TokenAsync("employees.read", "employees.write", "employees.approve");
        using var mutation = await SendAsync(HttpMethod.Patch, ExceptionUrl(), token, ExceptionBody());
        Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode); // authorized action cannot find another record
    }

    private static string PreviewUrl() => $"/api/compensation/benefits/grade-defaults?gradeId={Guid.NewGuid()}&companyId={Guid.NewGuid()}&effectiveFrom=2026-10-08";
    private static string ExceptionUrl() => $"/api/compensation/benefits/enrollments/{Guid.NewGuid()}/exception";
    private static object ExceptionBody() => new { reason = "Synthetic authority test", coverageTier = "Employee", entitlementTier = "Gold", maximumBenefitAmount = 1000m, requestedBenefitAmount = (decimal?)null, limitPeriod = "Annual", status = "Active", effectiveFrom = DateTime.UtcNow.ToString("yyyy-MM-dd"), expectedUpdatedAtUtc = DateTime.UtcNow };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<string> TokenAsync(params string[] permissions)
    {
        var userId = Guid.NewGuid();
        using (var scope = _fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tenant = new Tenant { Name = "Synthetic benefits auth", Slug = $"benefits-auth-{userId:N}", IsActive = true };
            var user = new User { Id = userId, TenantId = tenant.Id, Email = $"{userId:N}@benefits.invalid", NormalizedEmail = $"{userId:N}@benefits.invalid".ToUpperInvariant(), FullName = "Synthetic authority", PasswordHash = "no-login-test-harness", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true };
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            foreach (var permission in permissions)
                db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = tenant.Id, UserId = userId, PermissionKey = permission, Effect = "Allow" });
            await db.SaveChangesAsync();
        }
        using var mintScope = _fixture.Host.Services.CreateScope();
        var mintDb = mintScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var identity = await mintDb.Users.Include(x => x.Tenant).Include(x => x.PermissionOverrides)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts).Include(x => x.EntityAccesses).AsNoTracking().SingleAsync(x => x.Id == userId);
        var grants = Array.Empty<EntityAccessGrant>();
        return mintScope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(identity, [],
            AuthService.GetPermissions(identity), identity.Tenant!, grants, EntityScopeClaims.Resolve(true, grants, []), out _);
    }
}
