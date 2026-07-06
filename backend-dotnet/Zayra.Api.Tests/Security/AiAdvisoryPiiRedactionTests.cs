using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 1A P0: the employee_profile_summary AI context previously carried RAW
/// IqamaNumber / PassportNumber for Admin/HR callers. That context is serialized into
/// the LLM prompt (an external service) and persisted in the AI audit log. These tests
/// prove raw identity values never reach the LLM request, the answer, or the audit trail
/// — only last-4 masked forms.
/// </summary>
public class AiAdvisoryPiiRedactionTests
{
    private const string RawIqama = "2456789012";
    private const string RawPassport = "P123456789";

    [Fact]
    public async Task EmployeeProfileSummary_AdminCaller_LlmPayloadAndAnswerContainNoRawIdentityValues()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedSensitiveEmployee(db, tenantId);

        var llm = new StubLlmClient(new LlmResponse(false, "fallback", string.Empty, string.Empty));
        var audit = new CapturingAiAuditService();
        var service = CreateService(db, llm, audit);

        var response = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Admin" }, Array.Empty<string>(), null),
            new AIQueryRequest("Show the profile for this employee", employee.Id),
            CancellationToken.None);

        response.WasBlocked.Should().BeFalse();

        // 1. The exact payload sent to the LLM provider carries no raw identity numbers.
        llm.Requests.Should().NotBeEmpty("the query must have reached the LLM client");
        var llmPayload = JsonSerializer.Serialize(llm.Requests);
        llmPayload.Should().NotContain(RawIqama);
        llmPayload.Should().NotContain(RawPassport);

        // 2. The rendered answer (deterministic fallback serializes the same context).
        response.Answer.Should().NotContain(RawIqama);
        response.Answer.Should().NotContain(RawPassport);
        response.Answer.Should().Contain("***9012", "Admin/HR still see a last-4 mask for correlation");
        response.Answer.Should().Contain("***6789");

        // 3. The persisted AI audit trail carries no raw identity numbers either.
        var auditPayload = JsonSerializer.Serialize(audit.Entries);
        auditPayload.Should().NotContain(RawIqama);
        auditPayload.Should().NotContain(RawPassport);
    }

    [Fact]
    public async Task EmployeeProfileSummary_ManagerWithoutHrRole_GetsNoIdentityNumbersAtAll()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedSensitiveEmployee(db, tenantId);

        var llm = new StubLlmClient(new LlmResponse(false, "fallback", string.Empty, string.Empty));
        var service = CreateService(db, llm, new CapturingAiAuditService());

        var response = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Manager" }, Array.Empty<string>(), null),
            new AIQueryRequest("Show the profile for this employee", employee.Id),
            CancellationToken.None);

        response.WasBlocked.Should().BeFalse();
        response.Answer.Should().NotContain(RawIqama);
        response.Answer.Should().NotContain(RawPassport);
        response.Answer.Should().NotContain("***9012", "non-HR managers must not receive even masked identity numbers");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(options);
    }

    private static Employee SeedSensitiveEmployee(ZayraDbContext db, Guid tenantId)
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", Slug = $"t-{Guid.NewGuid():N}" });
        var employee = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = "AI-PII-1",
            FullName = "Amina Hassan",
            Department = "Finance",
            Designation = "Accountant",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-2),
            IqamaNumber = RawIqama,
            PassportNumber = RawPassport
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static AiAdvisoryService CreateService(ZayraDbContext db, StubLlmClient llm, IAiAuditService audit)
    {
        var options = new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, false);
        return new AiAdvisoryService(
            db,
            new AiGovernanceService(),
            new AiPromptBuilder(new AiRedactionService(), new AiTokenBudgetService(), options),
            llm,
            audit,
            new AiResponseCacheService(db, NullLogger<AiResponseCacheService>.Instance),
            options,
            new AiRedactionService(),
            new AiTokenBudgetService());
    }

    private sealed class CapturingAiAuditService : IAiAuditService
    {
        public List<AiAuditEntry> Entries { get; } = [];
        public Task LogAsync(AiAuditEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class StubLlmClient : ILlmClient
    {
        public StubLlmClient(LlmResponse response) => Response = response;
        public List<LlmRequest> Requests { get; } = [];
        public LlmResponse Response { get; }
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Response);
        }
    }
}
