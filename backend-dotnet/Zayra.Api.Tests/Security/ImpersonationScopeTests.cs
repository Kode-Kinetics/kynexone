using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Temporary fail-closed boundary for privileged tenant sessions. Issuance stays disabled until
/// the support-session revocation ledger is authoritative on every tenant request.
/// </summary>
[Collection("AuthorizationPipeline")]
public sealed class ImpersonationScopeTests : PlatformTestBase
{
    private const string DisabledError = "privileged_tenant_access_disabled";
    private readonly AuthorizationPipelineFixture _fixture;

    public ImpersonationScopeTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    [Fact]
    public void Impersonate_ReturnsExplicit403_WithoutLookupOrMutation()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        var result = controller.Impersonate(
            Guid.NewGuid(),
            new ImpersonateRequest(Guid.NewGuid()),
            CancellationToken.None);

        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonSerializer.SerializeToElement(forbidden.Value)
            .GetProperty("error").GetString().Should().Be(DisabledError);
        db.ChangeTracker.HasChanges().Should().BeFalse();
        db.AdminAuditLogs.Should().BeEmpty();
    }

    [Fact]
    public void StartSupportAccess_ReturnsExplicit403_WithoutSessionOrAudit()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        var result = controller.StartSupportAccess(
            new StartSupportAccessRequest(
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                "incident"),
            CancellationToken.None);

        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonSerializer.SerializeToElement(forbidden.Value)
            .GetProperty("error").GetString().Should().Be(DisabledError);
        db.PlatformSupportSessions.Should().BeEmpty();
        db.AdminAuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task EndSupportAccess_RemainsAvailable_ForExistingIncidentClosure()
    {
        await using var db = CreateDb();
        var session = new PlatformSupportSession
        {
            TenantId = Guid.NewGuid(),
            TargetUserId = Guid.NewGuid(),
            TargetUserEmail = "affected@example.test",
            Reason = "incident",
            StartedByEmail = "support@example.test",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            TokenHash = "contained"
        };
        db.PlatformSupportSessions.Add(session);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.EndSupportAccess(
            new EndSupportAccessRequest(session.Id.ToString()),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        session.EndedAtUtc.Should().NotBeNull();
        db.AdminAuditLogs.Should().ContainSingle(x => x.Action == "SupportAccessEnded");

        var replay = await controller.EndSupportAccess(
            new EndSupportAccessRequest(session.Id.ToString()),
            CancellationToken.None);

        replay.Should().BeOfType<OkObjectResult>();
        db.AdminAuditLogs.Should().ContainSingle(x => x.Action == "SupportAccessEnded");
    }

    [Fact]
    public async Task DisabledIssuance_RealPipeline_ReturnsStable403Contract()
    {
        var impersonation = await SendPlatformAsync(
            HttpMethod.Post,
            $"/api/platform/tenants/{Guid.NewGuid():D}/impersonate",
            new { userId = Guid.NewGuid() });
        var support = await SendPlatformAsync(
            HttpMethod.Post,
            "/api/platform/support-access/start",
            new
            {
                tenantId = Guid.NewGuid().ToString(),
                userId = Guid.NewGuid().ToString(),
                reason = "containment contract test"
            });

        await AssertDisabledAsync(impersonation);
        await AssertDisabledAsync(support);
    }

    [Fact]
    public async Task SupportEnd_RealPipeline_IsReplaySafeSinglyAuditedAndHistoryIsSanitized()
    {
        var session = new PlatformSupportSession
        {
            TenantId = Guid.NewGuid(),
            TargetUserId = Guid.NewGuid(),
            TargetUserEmail = $"affected-{Guid.NewGuid():N}@example.test",
            Reason = "historical incident",
            StartedByEmail = "support@example.test",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            TokenHash = $"must-not-leak-{Guid.NewGuid():N}"
        };
        using (var seedScope = _fixture.Host.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<Zayra.Api.Data.ZayraDbContext>();
            db.PlatformSupportSessions.Add(session);
            await db.SaveChangesAsync();
        }

        var first = await SendPlatformAsync(
            HttpMethod.Post,
            "/api/platform/support-access/end",
            new { sessionId = session.Id.ToString() });
        var replay = await SendPlatformAsync(
            HttpMethod.Post,
            "/api/platform/support-access/end",
            new { sessionId = session.Id.ToString() });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await SendPlatformAsync(
            HttpMethod.Get,
            $"/api/platform/support-access?tenantId={session.TenantId:D}");
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var historyBody = await history.Content.ReadAsStringAsync();
        historyBody.Should().Contain(session.Id.ToString(), Exactly.Once());
        historyBody.Should().NotContain(session.TokenHash);
        historyBody.Contains("tokenHash", StringComparison.OrdinalIgnoreCase).Should().BeFalse();

        using var verifyScope = _fixture.Host.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<Zayra.Api.Data.ZayraDbContext>();
        (await verify.PlatformSupportSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id))
            .EndedAtUtc.Should().NotBeNull();
        (await verify.AdminAuditLogs.AsNoTracking().Where(x =>
                x.Action == "SupportAccessEnded" && x.EntityId == session.Id.ToString()).ToListAsync())
            .Should().ContainSingle();
    }

    [Fact]
    public async Task TenantSessionSecurity_RejectsLegacyImpersonationJwt_EvenWithCurrentStamp()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Name = "Contained Tenant", Slug = $"contained-{Guid.NewGuid():N}" };
        var user = new User
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Email = "user@example.test",
            NormalizedEmail = "USER@EXAMPLE.TEST",
            FullName = "Contained User",
            PasswordHash = "unused"
        };
        db.AddRange(tenant, user);
        await db.SaveChangesAsync();
        var principal = Principal(
            (JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            ("tenant_id", tenant.Id.ToString()),
            (TenantSessionSecurity.SessionStampClaim, TenantSessionSecurity.StampValue(user)),
            ("impersonated_by", "platform_admin"));

        (await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public void EntityScopeContext_StrictMarker_NoClaims_DeniesInsteadOfGroupFallback()
    {
        var principal = Principal(
            ("tenant_id", Guid.NewGuid().ToString()),
            (EntityScopeContext.StrictScopeClaim, "true"));

        var scope = EntityScopeContext.FromClaims(principal, strictMode: false);

        scope.IsGroupLevel.Should().BeFalse();
        scope.AccessibleCompanyIds.Should().BeEmpty();
        scope.CanAccessCompany(Guid.NewGuid()).Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Test"));

    private async Task<HttpResponseMessage> SendPlatformAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.PlatformOwnerToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _fixture.Client.SendAsync(request);
    }

    private static async Task AssertDisabledAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be(DisabledError);
    }
}
