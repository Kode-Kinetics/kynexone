using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-12 (P2): the pending-overtime counters on the analytics KPIs and the AI "pending approvals" answer
/// filtered on statuses overtime never has ("Pending", "PendingApproval"), so they always said 0. Overtime
/// waits as PendingManager, then PendingHR.
/// </summary>
public sealed class OvertimePendingCounterTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Counters", Slug = $"counters-{tid:N}" });
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, DefaultTimezone = "UTC" });
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var (employee, status) in new[] { (1, "PendingManager"), (2, "PendingHR"), (3, "Approved"), (4, "Rejected") })
            db.OvertimeRequests.Add(new OvertimeRequest
            {
                TenantId = tid, EmployeeId = employee, EmployeeName = $"E{employee}", WorkDate = day, RequestedMinutes = 60, Status = status,
            });
        await db.SaveChangesAsync();
        return tid;
    }

    [Fact]
    public async Task AnalyticsKpis_CountPendingManagerAndPendingHr_AsPendingOvertime()
    {
        await using var db = NewDb();
        var tid = await SeedAsync(db);
        var claims = new List<Claim> { new("tenant_id", tid.ToString()) };
        claims.AddRange(new[] { "reports.read", "employees.read", "overtime.read" }.Select(p => new Claim("permission", p)));
        var ctrl = new AnalyticsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };

        var ok = Assert.IsType<OkObjectResult>(await ctrl.GetKPIs(default));
        var json = JsonSerializer.SerializeToElement(ok.Value);

        Assert.Equal(2, json.GetProperty("overtime").GetProperty("pendingOT").GetInt32());
    }

    [Fact]
    public async Task AiPendingApprovals_CountPendingManagerAndPendingHr_AsPendingOvertime()
    {
        await using var db = NewDb();
        var tid = await SeedAsync(db);
        var options = new AiOptions("fallback", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 4096, true, false);
        var service = new AiAdvisoryService(
            db, new AiGovernanceService(), new AiPromptBuilder(new AiRedactionService(), new AiTokenBudgetService(), options),
            new NoLlm(), new NoAudit(), new AiResponseCacheService(db, NullLogger<AiResponseCacheService>.Instance),
            options, new AiRedactionService(), new AiTokenBudgetService());

        var response = await service.QueryAsync(
            new AiUserContext(tid, Guid.NewGuid(), new[] { "HR Manager" }, Array.Empty<string>(), null),
            new AIQueryRequest("What pending approvals do I have?", null), CancellationToken.None);

        Assert.False(response.WasBlocked);
        Assert.Contains("2 pending overtime requests", response.Answer);
    }

    private sealed class NoAudit : IAiAuditService
    {
        public Task LogAsync(AiAuditEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LlmResponse(false, "fallback", string.Empty, string.Empty));
    }
}
