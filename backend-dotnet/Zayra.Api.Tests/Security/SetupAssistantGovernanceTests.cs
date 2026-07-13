using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class SetupAssistantGovernanceTests
{
    private readonly PostgresFixture _fx;
    public SetupAssistantGovernanceTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Apply_WithoutSetupApplyPermission_IsForbiddenAndDoesNotMutate()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var beforeCompanies = await db.Companies.CountAsync(x => x.TenantId == tenantId);
        var controller = CreateController(db, User(tenantId));

        var result = await controller.Apply(new ApplySetupRequest(SetupDraft.Empty(), "SA", "SAR", "Blocked Legal"), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.Companies.CountAsync(x => x.TenantId == tenantId)).Should().Be(beforeCompanies);
        (await db.AuditLogs.AnyAsync(x => x.TenantId == tenantId && x.Action == "setup.assistant_applied")).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_WithSetupApplyPermission_WritesAudit()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var controller = CreateController(db, User(tenantId, "organization.setup.apply"));

        var result = await controller.Apply(new ApplySetupRequest(SetupDraft.Empty(), "SA", "SAR", "Allowed Legal"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.AuditLogs.AnyAsync(x => x.TenantId == tenantId && x.Action == "setup.assistant_applied")).Should().BeTrue();
    }

    private static SetupAssistantController CreateController(ZayraDbContext db, ClaimsPrincipal principal)
    {
        var controller = new SetupAssistantController(db, new FakeSetupAssistantService(), new AuditService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private static ClaimsPrincipal User(Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "HR Manager")
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class FakeSetupAssistantService : ISetupAssistantService
    {
        public Task<SetupPreviewResult> GenerateAsync(CompanyProfile profile, CancellationToken ct) =>
            Task.FromResult(new SetupPreviewResult(SetupDraft.Empty(), [], "test"));
    }
}
