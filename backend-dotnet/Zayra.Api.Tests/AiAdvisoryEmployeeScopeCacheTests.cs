using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A cached answer is shared only between callers whose employee scope is the same. Two callers with the same roles, permissions and
/// company scope but a different team each get their own answer; the same team still shares one.
/// </summary>
public class AiAdvisoryEmployeeScopeCacheTests
{
    private const string Question = "How many active employees are there?";

    [Fact]
    public async Task SameRolesDifferentTeam_DoNotShareACachedAnswer()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var llm = new CountingLlm();
        var service = CreateService(db, llm);

        await service.QueryAsync(Caller(tenantId, [1, 2]), new AIQueryRequest(Question, null), CancellationToken.None);
        await service.QueryAsync(Caller(tenantId, [3]), new AIQueryRequest(Question, null), CancellationToken.None);

        Assert.Equal(2, llm.Calls);
        Assert.Equal(2, await db.AIHRQueryCaches.CountAsync());
    }

    [Fact]
    public async Task SameTeam_StillSharesOneCachedAnswer_RegardlessOfIdOrder()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var llm = new CountingLlm();
        var service = CreateService(db, llm);

        await service.QueryAsync(Caller(tenantId, [1, 2]), new AIQueryRequest(Question, null), CancellationToken.None);
        await service.QueryAsync(Caller(tenantId, [2, 1]), new AIQueryRequest(Question, null), CancellationToken.None);

        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task UnrestrictedCaller_DoesNotShareWithATeamScopedCaller()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var llm = new CountingLlm();
        var service = CreateService(db, llm);

        await service.QueryAsync(Caller(tenantId, null), new AIQueryRequest(Question, null), CancellationToken.None);
        await service.QueryAsync(Caller(tenantId, [3]), new AIQueryRequest(Question, null), CancellationToken.None);

        Assert.Equal(2, llm.Calls);
    }

    private static AiUserContext Caller(Guid tenantId, int[]? team) =>
        new(tenantId, Guid.NewGuid(), new[] { "Manager" }, new[] { "manager.read" }, null)
        {
            ScopeEmployeeIds = team,
            CompanyScopeSignature = "company-a",
        };

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AiAdvisoryService CreateService(ZayraDbContext db, ILlmClient llm)
    {
        var options = new AiOptions("ollama", "gpt-oss:120b-cloud", string.Empty, string.Empty, "http://ollama.local:11434", string.Empty, 4096, true, false);
        return new AiAdvisoryService(
            db, new AiGovernanceService(), new AiPromptBuilder(new AiRedactionService(), new AiTokenBudgetService(), options), llm,
            new NullAudit(), new AiResponseCacheService(db, NullLogger<AiResponseCacheService>.Instance), options,
            new AiRedactionService(), new AiTokenBudgetService());
    }

    private sealed class CountingLlm : ILlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new LlmResponse(true, "ollama", "gpt-oss:120b-cloud", "There are some active employees."));
        }
    }

    private sealed class NullAudit : IAiAuditService
    {
        public Task LogAsync(AiAuditEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
