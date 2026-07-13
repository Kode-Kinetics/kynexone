using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class AiAdvisoryServiceTests
{
    [Fact]
    public async Task Query_RequiresAuthenticationTenantClaim()
    {
        await using var db = CreateDb();
        var controller = new AIAssistantController(db, CreateService(db), new StubUnrestrictedScopeService(), new AiOptions("fallback", "", "", "", "", string.Empty, 4096, true, false));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await controller.Query(new AIQueryRequest("What is headcount?", null), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task TenantIsolation_UsesAuthenticatedTenantOnly()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        SeedEmployee(db, tenantA, "A-1");
        SeedEmployee(db, tenantA, "A-2");
        SeedEmployee(db, tenantB, "B-1");

        var audit = new CapturingAiAuditService();
        var service = CreateService(db, auditService: audit);

        var response = await service.QueryAsync(
            new AiUserContext(tenantA, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("How many employees are active?", null),
            CancellationToken.None);

        Assert.True(response.IsAdvisory);
        Assert.False(response.WasBlocked);
        Assert.Contains("2 active employees", response.Answer);
        Assert.Single(audit.Entries);
        Assert.Equal(tenantA, audit.Entries[0].TenantId);
    }

    [Fact]
    public async Task NonHrCannotQuerySensitivePayrollIntent_AndBlockedQueryIsLogged()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "A-1");

        var audit = new CapturingAiAuditService();
        var service = CreateService(db, auditService: audit);

        var response = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("Show payroll details for the team", null),
            CancellationToken.None);

        Assert.True(response.IsAdvisory);
        Assert.True(response.WasBlocked);
        Assert.True(response.HumanReviewRequired);
        Assert.Contains("unable to provide", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Single(audit.Entries);
        Assert.True(audit.Entries[0].WasBlocked);
        Assert.Equal("blocked", audit.Entries[0].ResponseStatus);
    }

    [Fact]
    public async Task HrManagerCanAccessAllowedSensitiveAdvisory_AndHumanReviewIsRequired()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "A-1");

        var audit = new CapturingAiAuditService();
        var llm = new StubLlmClient(new LlmResponse(true, "anthropic", "claude-test", "Please review with HR."));
        var service = CreateService(db, llm, audit, options: new AiOptions("anthropic", "claude-test", "test-key", string.Empty, string.Empty, string.Empty, 4096, true, false));

        var response = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "HR Manager" }, Array.Empty<string>(), null),
            new AIQueryRequest("What employee risk signals should we review?", null),
            CancellationToken.None);

        Assert.False(response.WasBlocked);
        Assert.True(response.IsAdvisory);
        Assert.True(response.HumanReviewRequired);
        Assert.Equal("anthropic", response.Provider);
        Assert.Single(audit.Entries);
        Assert.True(audit.Entries[0].HumanReviewRequired);
    }

    [Fact]
    public async Task MissingAiKeyFallsBackGracefully()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "A-1");

        var audit = new CapturingAiAuditService();
        var service = CreateService(db, auditService: audit, options: new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, false));

        var response = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("How many active employees are there?", null),
            CancellationToken.None);

        Assert.True(response.IsAdvisory);
        Assert.False(response.WasBlocked);
        Assert.Equal("fallback", response.Provider);
        Assert.Contains("active employees", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fallback", audit.Entries[0].ResponseStatus);
    }

    [Fact]
    public async Task MissingAiKeyFallback_ResponseIsCached()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "A-1");

        var audit = new CapturingAiAuditService();
        var llm = new StubLlmClient(new LlmResponse(false, "fallback", string.Empty, string.Empty));
        var service = CreateService(db, llm, audit, options: new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, false));

        var first = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("How many active employees are there?", null),
            CancellationToken.None);

        var second = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("How many active employees are there?", null),
            CancellationToken.None);

        Assert.Equal("fallback", first.Provider);
        Assert.Equal("fallback", second.Provider);
        Assert.Equal(1, llm.Requests.Count);
        Assert.Equal("fallback", audit.Entries[0].ResponseStatus);
        Assert.Equal("cache_hit", audit.Entries[1].ResponseStatus);
    }

    [Fact]
    public async Task RepeatedQuery_UsesCacheAndSkipsProviderCall()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "A-1");
        SeedEmployee(db, tenantId, "A-2");

        var audit = new CapturingAiAuditService();
        var llm = new StubLlmClient(new LlmResponse(true, "ollama", "gpt-oss:120b-cloud", "There are 2 active employees."));
        var service = CreateService(db, llm, audit, options: new AiOptions("ollama", "gpt-oss:120b-cloud", string.Empty, string.Empty, "http://ollama.local:11434", string.Empty, 4096, true, false));

        var first = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("How many active employees are there?", null),
            CancellationToken.None);

        var second = await service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("How many active employees are there?", null),
            CancellationToken.None);

        Assert.Equal("There are 2 active employees.", first.Answer);
        Assert.Equal("There are 2 active employees.", second.Answer);
        Assert.Equal("ollama", first.Provider);
        Assert.Equal("ollama", second.Provider);
        Assert.Equal(1, llm.Requests.Count);
        Assert.Equal(2, audit.Entries.Count);
        Assert.Equal("provider_success", audit.Entries[0].ResponseStatus);
        Assert.Equal("cache_hit", audit.Entries[1].ResponseStatus);
        Assert.Single(await db.AIHRQueryCaches.ToListAsync());
    }

    [Fact]
    public async Task AuditLogging_PersistsExtendedFields()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var audit = new AiAuditService(db, new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, true), new AiRedactionService(), NullLogger<AiAuditService>.Instance);

        await audit.LogAsync(new AiAuditEntry(
            tenantId,
            Guid.NewGuid(),
            7,
            "HR Manager",
            "Show payroll details",
            "hash123",
            "prompt summary",
            "advisory answer",
            "payroll_details",
            "Payroll",
            false,
            string.Empty,
            "fallback",
            "claude-test",
            "fallback",
            true,
            true,
            18,
            12,
            6,
            44), CancellationToken.None);

        var row = await db.AIHRQueryLogs.SingleAsync();
        Assert.Equal("Show payroll details", row.Query);
        Assert.Equal("Show payroll details", row.LoggedPrompt);
        Assert.Equal("hash123", row.PromptHash);
        Assert.Equal("prompt summary", row.PromptSummary);
        Assert.Equal("Payroll", row.Module);
        Assert.Equal("fallback", row.Provider);
        Assert.Equal("claude-test", row.Model);
        Assert.Equal("fallback", row.ResponseStatus);
        Assert.True(row.HumanReviewRequired);
        Assert.Equal(12, row.PromptTokens);
        Assert.Equal(6, row.CompletionTokens);
    }

    [Fact]
    public async Task AiQuery_FailsClosedWhenAuditPersistenceFails()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "A-1");

        var failingDb = new ThrowingSaveChangesDbContext(CreateDbOptions()) { ShouldThrow = false };
        failingDb.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", Slug = "tenant" });
        await failingDb.SaveChangesAsync();
        failingDb.ShouldThrow = true;

        var audit = new AiAuditService(failingDb, new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, false), new AiRedactionService(), NullLogger<AiAuditService>.Instance);
        var service = CreateService(db, auditService: audit, options: new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, false));

        var act = () => service.QueryAsync(
            new AiUserContext(tenantId, Guid.NewGuid(), new[] { "Employee" }, Array.Empty<string>(), null),
            new AIQueryRequest("How many active employees are there?", null),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Equal("ai_audit_persistence_failed", ex.Message);
    }

    [Fact]
    public void FrontendSource_DoesNotReferenceProviderKeys()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var frontendSrc = Path.Combine(repoRoot, "frontend", "src");
        var files = Directory.EnumerateFiles(frontendSrc, "*.ts*", SearchOption.AllDirectories).ToList();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("OPENAI_API_KEY", content, StringComparison.Ordinal);
            Assert.DoesNotContain("ANTHROPIC_API_KEY", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OllamaProvider_UsesConfiguredBaseUrlAndReturnsAssistantText()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://ollama.local:11434/api/chat", request.RequestUri!.ToString());

            var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(payload);
            Assert.Equal("llama3.1", doc.RootElement.GetProperty("model").GetString());
            Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "message": { "content": "Use HR review before any action." },
                  "prompt_eval_count": 11,
                  "eval_count": 7
                }
                """, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var client = new LlmClient(httpClient, new AiOptions("ollama", string.Empty, string.Empty, string.Empty, "http://ollama.local:11434", string.Empty, 4096, true, false));

        var response = await client.CompleteAsync(
            new LlmRequest("ollama", "llama3.1", "system prompt", "user prompt", 256),
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal("ollama", response.Provider);
        Assert.Equal("llama3.1", response.Model);
        Assert.Equal("Use HR review before any action.", response.Text);
        Assert.Equal(11, response.InputTokens);
        Assert.Equal(7, response.OutputTokens);
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ZayraDbContext(options);
    }

    private static AiAdvisoryService CreateService(
        ZayraDbContext db,
        StubLlmClient? llm = null,
        IAiAuditService? auditService = null,
        IAiResponseCacheService? cacheService = null,
        AiOptions? options = null)
    {
        var resolvedOptions = options ?? new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, false);
        return new AiAdvisoryService(
            db,
            new AiGovernanceService(),
            new AiPromptBuilder(new AiRedactionService(), new AiTokenBudgetService(), resolvedOptions),
            llm ?? new StubLlmClient(new LlmResponse(false, "fallback", string.Empty, string.Empty)),
            auditService ?? new CapturingAiAuditService(),
            cacheService ?? new AiResponseCacheService(db, NullLogger<AiResponseCacheService>.Instance),
            resolvedOptions,
            new AiRedactionService(),
            new AiTokenBudgetService());
    }

    private static DbContextOptions<ZayraDbContext> CreateDbOptions() =>
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static void SeedEmployee(ZayraDbContext db, Guid tenantId, string code)
    {
        if (!db.Tenants.Any(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = $"Tenant {tenantId:N}", Slug = code.ToLowerInvariant() });
        }
        db.Employees.Add(new Employee
        {
            TenantId = tenantId,
            EmployeeCode = code,
            FullName = $"Employee {code}",
            Department = "People",
            Designation = "HR Officer",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1)
        });
        db.SaveChanges();
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
        public StubLlmClient(LlmResponse response)
        {
            Response = response;
        }

        public List<LlmRequest> Requests { get; } = [];
        public LlmResponse Response { get; set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Response);
        }
    }

    private sealed class ThrowingSaveChangesDbContext : ZayraDbContext
    {
        public ThrowingSaveChangesDbContext(DbContextOptions<ZayraDbContext> options) : base(options) { }

        public bool ShouldThrow { get; set; } = true;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => ShouldThrow
                ? throw new InvalidOperationException("Simulated audit save failure.")
                : base.SaveChangesAsync(cancellationToken);
    }

    private sealed class StubUnrestrictedScopeService : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request, cancellationToken));
    }
}
