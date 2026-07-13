using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.AI;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Common;

namespace Zayra.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class AiPermissionGateTests
{
    private readonly PostgresFixture _fx;
    public AiPermissionGateTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AiQuery_WithoutAiQueryPermission_IsForbidden()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var controller = new AIAssistantController(db, new ThrowingAiAdvisoryService(), new DataScopeService(db), FallbackOptions())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserWithoutPermissions(tenantId) } }
        };

        var result = await controller.Query(new AIQueryRequest("show payroll risk", null), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task PolicyAsk_WithoutAiOrPolicyPermission_IsForbidden()
    {
        var tenantId = Guid.NewGuid();
        var controller = new PolicyDocumentController(new ThrowingPolicyDocumentService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserWithoutPermissions(tenantId) } }
        };

        var result = await controller.Ask(new PolicyAskRequest("what is the leave policy?"), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task PolicyList_WithoutPolicyPermissionOrHrRole_IsForbidden()
    {
        var tenantId = Guid.NewGuid();
        var controller = new PolicyDocumentController(new ThrowingPolicyDocumentService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserWithoutPermissions(tenantId) } }
        };

        var result = await controller.List(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    private static ClaimsPrincipal UserWithoutPermissions(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Employee"),
        }, "Test"));

    private static AiOptions FallbackOptions() =>
        new("fallback", "", "", "", "", "", 4096, true, false);

    private sealed class ThrowingAiAdvisoryService : IAiAdvisoryService
    {
        public Task<AIQueryResponse> QueryAsync(AiUserContext caller, AIQueryRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("AI service should not be called when permission is missing.");
    }

    private sealed class ThrowingPolicyDocumentService : IPolicyDocumentService
    {
        public Task<PolicyDocumentDto> UploadAsync(Guid tenantId, Guid? userId, Stream content, string fileName, string mimeType, CancellationToken ct) =>
            throw new InvalidOperationException("Policy document service should not be called when permission is missing.");

        public Task<IReadOnlyList<PolicyDocumentDto>> ListAsync(Guid tenantId, CancellationToken ct) =>
            throw new InvalidOperationException("Policy document service should not be called when permission is missing.");

        public Task<bool> DeleteAsync(Guid tenantId, Guid documentId, CancellationToken ct) =>
            throw new InvalidOperationException("Policy document service should not be called when permission is missing.");

        public Task<PolicyAskResponse> AskAsync(Guid tenantId, string question, CancellationToken ct) =>
            throw new InvalidOperationException("Policy document service should not be called when permission is missing.");
    }
}
