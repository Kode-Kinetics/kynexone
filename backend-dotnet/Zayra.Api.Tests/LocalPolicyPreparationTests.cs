using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

public sealed class LocalPolicyPreparationTests
{
    private sealed class NeverLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Local preparation must never call AI.");
    }
    private sealed class Recorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct)
            => throw new InvalidOperationException("Local preparation must not consume AI usage.");
    }
    // Catches gating local preparation on provider/consent, or writing operational records.
    [Fact]
    public async Task WithoutAiConsentOrProvider_PreparesSourceBackedDraftsWithoutWrites()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid(); var user = Guid.NewGuid();
        var options = new AiOptions("none", "", "", "", "", "", 1, true, false);
        var documents = new PolicyDocumentService(db, new NeverLlm(), options, new Recorder(), NullLogger<PolicyDocumentService>.Instance);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("Onboarding\nHR arranges contract signing."));
        var document = await documents.UploadAsync(tenant, user, content, "policy.txt", "text/plain", default);
        var controller = new SetupPolicyExtractionController(documents, new(new NeverLlm(), options, new Recorder()), db);
        void Identity(Guid tenantId, Guid userId, bool permission = true) {
            var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
                new(ClaimTypes.Role, "HR Officer"), new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })) };
            if (permission) claims.Add(new("permission", "organization.write"));
            controller.ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "Test")) } };
        }
        Identity(tenant, user);
        var request = new PolicyExtractionRequest(document.Id, false, SetupConfigurationTests.Profile());
        var result = (await controller.Extract(request, default)).Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PolicyExtractionResult>().Subject;
        result.Provider.Should().Be("local-rules");
        result.OnboardingTaskDrafts.Should().NotBeEmpty();
        result.OnboardingTaskDrafts.Should().OnlyContain(task => task.SourceQuote.Contains("contract signing"));
        db.ChangeTracker.Entries().Should().OnlyContain(entry => entry.State == EntityState.Unchanged);
        db.OnboardingTasks.Should().BeEmpty(); db.OnboardingChecklists.Should().BeEmpty();
        Identity(Guid.NewGuid(), user);
        (await controller.Extract(request, default)).Should().BeOfType<NotFoundResult>();
        Identity(tenant, Guid.NewGuid());
        (await controller.Extract(request, default)).Should().BeOfType<NotFoundResult>();
        Identity(tenant, user, false);
        (await controller.Extract(request, default)).Should().BeOfType<ForbidResult>();
    }
}
