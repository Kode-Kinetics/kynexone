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
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Audit;

namespace Zayra.Api.Tests;

public sealed class PolicyReviewTests
{
    private sealed class NeverLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct) => throw new InvalidOperationException("Review must not call AI.");
    }
    private sealed class Recorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord r, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Case : IAsyncDisposable
    {
        public ZayraDbContext Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tenant = Guid.NewGuid(), User = Guid.NewGuid(), Company = Guid.NewGuid();
        public PolicyDocumentService Documents;
        public Case() => Documents = new(Db, new NeverLlm(), new("none", "", "", "", "", "", 4096, true, false), new Recorder(), NullLogger<PolicyDocumentService>.Instance, PolicyContentNeverLeaves.Instance);
        public async Task<PolicyReviewRequest> Seed()
        {
            Db.Companies.Add(new() { Id = Company, TenantId = Tenant, LegalNameEn = "Shadow", CountryCode = "SA", DefaultCurrency = "SAR" });
            await Db.SaveChangesAsync();
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("HR assigns an orientation mentor."));
            var doc = await Documents.UploadAsync(Tenant, User, input, "policy.txt", "text/plain", default);
            return new(Company, doc.Id, doc.ContentSha256!, "requirement", "orientation-mentor", "Assign orientation mentor", "NeedsClarification", "Policy owner must confirm assignment.", "HR", SourceQuote: "HR assigns an orientation mentor.", SourceStart: 0, SourceLength: 33);
        }
        public SetupPolicyReviewController Controller(Guid? tenant = null, Guid? scopedCompany = null, bool permission = true)
        {
            var claims = new List<Claim> { new("tenant_id", (tenant ?? Tenant).ToString()), new(ClaimTypes.NameIdentifier, User.ToString()), new(ClaimTypes.Role, "Admin"),
                new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { scopedCompany ?? Company } })) };
            if (permission) claims.Add(new("permission", "organization.write"));
            return new(Db, Documents) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "Test")) } } };
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task SavesReviewWithServerIdentityAndCompanyScopeWithoutApplyingConfiguration()
    {
        await using var c = new Case(); var request = await c.Seed();
        var result = await c.Controller().Save(request, default);
        var record = (result.Should().BeOfType<OkObjectResult>().Subject.Value).Should().BeOfType<PolicyReviewRecord>().Subject;
        record.ReviewedByUserId.Should().Be(c.User);
        record.CompanyId.Should().Be(c.Company);
        var log = await c.Db.AuditLogs.SingleAsync();
        log.CompanyId.Should().Be(c.Company);
        AuditService.VerifyIntegrity(log).Should().BeTrue();
        (await c.Db.OnboardingChecklists.CountAsync()).Should().Be(0);
        (await c.Db.OnboardingTasks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NewDecisionAppendsHistoryRatherThanOverwritingPreviousComment()
    {
        await using var c = new Case(); var request = await c.Seed();
        (await c.Controller().Save(request, default)).Should().BeOfType<OkObjectResult>();
        (await c.Controller().Save(request with { Disposition = "ManualProcess", Comment = "Keep manual until mapping exists." }, default)).Should().BeOfType<OkObjectResult>();
        var result = await c.Controller().History(c.Company, request.DocumentId, default);
        var rows = (result as OkObjectResult)!.Value.Should().BeAssignableTo<IEnumerable<PolicyReviewRecord>>().Subject.ToList();
        rows.Should().HaveCount(2);
        rows.Select(r => r.Comment).Should().Contain(request.Comment);
    }

    [Theory]
    [InlineData("NeedsClarification", "", "HR", null)]
    [InlineData("ManualProcess", "Handle manually", "", null)]
    [InlineData("Answered", "Confirmed", "HR", null)]
    [InlineData("Applied", "Not really applied", "HR", "approval")]
    public async Task RejectsIncompleteOrMisleadingDecisions(string disposition, string comment, string owner, string? approval)
    {
        await using var c = new Case(); var request = await c.Seed();
        (await c.Controller().Save(request with { Disposition = disposition, Comment = comment, Owner = owner, ApprovalReference = approval }, default)).Should().BeOfType<BadRequestObjectResult>();
        c.Db.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task RejectsStaleVersionAndForgedEvidence()
    {
        await using var c = new Case(); var request = await c.Seed();
        (await c.Controller().Save(request with { SourceHash = new string('0', 64) }, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.Controller().Save(request with { SourceQuote = "Invented clause" }, default)).Should().BeOfType<BadRequestObjectResult>();
        c.Db.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task OtherTenantCompanyAndMissingPermissionCannotSaveOrRead()
    {
        await using var c = new Case(); var request = await c.Seed();
        (await c.Controller(permission: false).Save(request, default)).Should().BeOfType<ForbidResult>();
        (await c.Controller(scopedCompany: Guid.NewGuid()).Save(request, default)).Should().BeOfType<ForbidResult>();
        (await c.Controller(tenant: Guid.NewGuid()).Save(request, default)).Should().BeOfType<NotFoundResult>();
        (await c.Controller(permission: false).History(c.Company, request.DocumentId, default)).Should().BeOfType<ForbidResult>();
        c.Db.AuditLogs.Should().BeEmpty();
    }
}
