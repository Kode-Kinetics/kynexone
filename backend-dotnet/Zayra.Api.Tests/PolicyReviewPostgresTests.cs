using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PolicyReviewPostgresTests(PostgresFixture fixture)
{
    private const string Source = "HR assigns an orientation mentor.";
    private sealed class NoLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Recording a review must not call AI.");
    }
    private sealed class Recorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    // Catches a review writer bypassing the central sealer, linking off a stale tenant tail,
    // or writing workflow/configuration data while merely recording a review decision.
    [Fact]
    public async Task ConcurrentReviewsAndOrdinaryAuditShareOneValidTenantChainWithoutApplyingPolicy()
    {
        Guid tenant, otherTenant, companyId, documentId;
        var userId = Guid.NewGuid();
        await using (var seed = fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            otherTenant = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company { TenantId = tenant, LegalNameEn = "Review-only company", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
            companyId = company.Id;
            var doc = new PolicyDocument { TenantId = tenant, CompanyId = companyId, OriginalName = "Policy 3", Status = "Ready", ContentSha256 = new string('a', 64) };
            documentId = doc.Id;
            seed.Companies.Add(company);
            seed.PolicyDocuments.Add(doc);
            seed.DocumentChunks.Add(new() { TenantId = tenant, DocumentId = documentId, Document = doc, Content = Source });
            await seed.SaveChangesAsync();
            await new AuditService(seed).WriteAsync("unrelated.tenant.marker", "Test", null,
                new RequestContext(null, null, userId, otherTenant), "{}", default);
        }
        await using var initial = fixture.CreateDb();
        var untouched = await initial.AuditLogs.AsNoTracking().SingleAsync(a => a.TenantId == otherTenant);

        async Task SaveReview(int index)
        {
            await using var db = fixture.CreateDb();
            var documents = new PolicyDocumentService(db, new NoLlm(), new("none", "", "", "", "", "", 4096, true, false),
                new Recorder(), NullLogger<PolicyDocumentService>.Instance);
            var claims = new[] { new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, "HR Director"), new Claim("permission", "organization.write"),
                new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })) };
            var controller = new SetupPolicyReviewController(db, documents)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "Test")) } } };
            var request = new PolicyReviewRequest(companyId, documentId, new string('a', 64), "requirement", $"mentor-{index}",
                "Assign orientation mentor", "ManualProcess", "Record manual follow-up only.", "HR",
                SourceQuote: Source, SourceStart: 0, SourceLength: Source.Length);
            (await controller.Save(request, default)).Should().BeOfType<OkObjectResult>();
        }
        async Task WriteOrdinaryAudit()
        {
            await using var db = fixture.CreateDb();
            await new AuditService(db).WriteAsync("ordinary.concurrent.event", "Test", null,
                new RequestContext(null, null, userId, tenant), "{}", default);
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(SaveReview).Append(WriteOrdinaryAudit())).WaitAsync(TimeSpan.FromSeconds(30));

        await using var check = fixture.CreateDb();
        var chain = await check.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant)
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToListAsync();
        chain.Should().HaveCount(9);
        chain.Count(a => a.Action == "setup.policy_review_recorded" && a.CompanyId == companyId).Should().Be(8);
        AuditService.VerifyChain(chain).IsValid.Should().BeTrue("all async audit writers must share the central chain sealer");
        var other = await check.AuditLogs.AsNoTracking().SingleAsync(a => a.TenantId == otherTenant);
        other.Id.Should().Be(untouched.Id);
        other.EntryHash.Should().Be(untouched.EntryHash);
        (await check.OnboardingChecklists.CountAsync(x => x.TenantId == tenant)).Should().Be(0);
        (await check.OnboardingTasks.CountAsync(x => x.TenantId == tenant)).Should().Be(0);
        (await check.LeavePolicies.CountAsync(x => x.TenantId == tenant)).Should().Be(0);
    }
}
