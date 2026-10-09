using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PolicyDocumentPublicationPostgresTests(PostgresFixture fixture)
{
    private sealed class NoLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
            => throw new InvalidOperationException("No AI call in publication test.");
    }
    private sealed class Recorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task StalePublishCannotOverwriteWithdrawalOrCommitItsAuditRow()
    {
        Guid tenant; Guid documentId; Guid companyId;
        await using (var seed = fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company { TenantId = tenant, LegalNameEn = "Publication race", CountryCode = "SA", IsActive = true };
            seed.Companies.Add(company);
            companyId = company.Id;
            var doc = new PolicyDocument { TenantId = tenant, CompanyId = company.Id, OriginalName = "Reviewed policy", Status = "Ready",
                PublicationStatus = "Published", PublishedAtUtc = DateTime.UtcNow.AddDays(-2), EffectiveFromUtc = DateTime.UtcNow.AddDays(-2),
                ContentSha256 = new string('a',64), UpdatedAtUtc = DateTime.UtcNow.AddDays(-2) };
            documentId = doc.Id;
            seed.PolicyDocuments.Add(doc);
            seed.DocumentChunks.Add(new() { TenantId = tenant, DocumentId = doc.Id, Document = doc, Content = "Annual leave policy." });
            await seed.SaveChangesAsync();
        }
        await using var staleDb = fixture.CreateDb();
        _ = await staleDb.PolicyDocuments.SingleAsync(d => d.Id == documentId);
        await using (var currentDb = fixture.CreateDb())
        {
            var current = await currentDb.PolicyDocuments.SingleAsync(d => d.Id == documentId);
            current.PublicationStatus = "Withdrawn";
            current.UpdatedAtUtc = DateTime.UtcNow;
            await currentDb.SaveChangesAsync();
        }
        var service = new PolicyDocumentService(staleDb, new NoLlm(), new AiOptions("none", "", "", "", "", "", 4096, true, false),
            new Recorder(), NullLogger<PolicyDocumentService>.Instance);
        var controller = new PolicyDocumentController(service, staleDb, new AuditService(staleDb))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "organization.write") }, "test")) } }
        };
        var result = await controller.Publish(documentId, new PublishPolicyRequest(companyId, new string('a',64)), default);
        result.Should().BeOfType<ConflictObjectResult>();
        await using var check = fixture.CreateDb();
        (await check.PolicyDocuments.SingleAsync(d => d.Id == documentId)).PublicationStatus.Should().Be("Withdrawn");
        (await check.AuditLogs.CountAsync(a => a.TenantId == tenant && a.Action == "policy.document_published")).Should().Be(0);
    }
}
