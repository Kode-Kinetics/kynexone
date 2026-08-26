using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Verifies that the AuditService writes correctly-scoped audit records and that
/// audit logs cannot be accessed across tenant boundaries.
/// Uses in-memory DB — no external dependencies.
/// </summary>
public class AuditLoggingTests
{
    private static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    // ── AuditService writes records ───────────────────────────────────────────

    [Fact]
    public async Task AuditService_WritesAction_WithCorrectFields()
    {
        await using var db = CreateDb();
        var svc      = new AuditService(db);
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();

        var ctx = new RequestContext("192.168.1.1", "TestAgent/1.0", userId, tenantId);
        await svc.WriteAsync("auth.login", "User", userId.ToString(), ctx,
            metadata: null, cancellationToken: CancellationToken.None);

        var log = await db.AuditLogs
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Action == "auth.login");

        log.Should().NotBeNull();
        log!.UserId.Should().Be(userId);
        log.EntityName.Should().Be("User");
        log.IpAddress.Should().Be("192.168.1.1");
        log.UserAgent.Should().Be("TestAgent/1.0");
        log.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AuditService_MultipleLogs_AllPersisted()
    {
        await using var db = CreateDb();
        var svc      = new AuditService(db);
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();
        var ctx      = new RequestContext("10.0.0.1", "Agent/2", userId, tenantId);

        await svc.WriteAsync("employee.created", "Employee", "emp-1", ctx, null, CancellationToken.None);
        await svc.WriteAsync("employee.updated", "Employee", "emp-1", ctx, null, CancellationToken.None);
        await svc.WriteAsync("employee.deleted", "Employee", "emp-1", ctx, null, CancellationToken.None);

        var count = await db.AuditLogs.CountAsync(l => l.TenantId == tenantId);
        count.Should().Be(3);
        var chain = await db.AuditLogs.Where(l => l.TenantId == tenantId)
            .OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).ToListAsync();
        AuditService.VerifyChain(chain).IsValid.Should().BeTrue();
    }

    // ── Audit log tenant isolation ────────────────────────────────────────────

    [Fact]
    public async Task AuditLogs_IsolatedByTenantId()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userId  = Guid.NewGuid();
        var svc     = new AuditService(db);

        var ctxA = new RequestContext("127.0.0.1", "TestAgent", userId, tenantA);
        var ctxB = new RequestContext("127.0.0.1", "TestAgent", userId, tenantB);

        await svc.WriteAsync("employee.created", "Employee", "emp-a",   ctxA, null, CancellationToken.None);
        await svc.WriteAsync("leave.approved",   "Leave",    "leave-a", ctxA, null, CancellationToken.None);
        await svc.WriteAsync("employee.created", "Employee", "emp-b",   ctxB, null, CancellationToken.None);

        var tenantALogs = await db.AuditLogs.Where(l => l.TenantId == tenantA).ToListAsync();
        var tenantBLogs = await db.AuditLogs.Where(l => l.TenantId == tenantB).ToListAsync();

        tenantALogs.Should().HaveCount(2);
        tenantBLogs.Should().HaveCount(1);
        tenantALogs.Should().NotContain(l => l.TenantId == tenantB);
        tenantBLogs.Should().NotContain(l => l.TenantId == tenantA);
    }

    [Fact]
    public async Task AuditLog_TenantBFilter_CannotSeeTenantARecords()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userId  = Guid.NewGuid();
        var svc     = new AuditService(db);

        var ctxA = new RequestContext("127.0.0.1", "Agent", userId, tenantA);
        await svc.WriteAsync("payroll.approved", "Payroll", "payroll-001", ctxA,
            metadata: "{\"amount\":250000,\"currency\":\"USD\"}",
            cancellationToken: CancellationToken.None);

        var tenantBLogs = await db.AuditLogs.Where(l => l.TenantId == tenantB).ToListAsync();
        tenantBLogs.Should().BeEmpty("Tenant B must never access Tenant A's payroll audit events");
    }

    // ── AdminAuditLog (platform-level) isolation ──────────────────────────────

    [Fact]
    public async Task AdminAuditLogs_FilteredByTenantId_OnlyReturnThatTenantsEvents()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.AdminAuditLogs.AddRange(
            new AdminAuditLog { TenantId = tenantA, EntityType = "Tenant", EntityId = tenantA.ToString(), Action = "TenantCreated", PerformedByName = "platform_admin", IpAddress = "1.2.3.4" },
            new AdminAuditLog { TenantId = tenantA, EntityType = "Tenant", EntityId = tenantA.ToString(), Action = "Suspended",     PerformedByName = "platform_admin", IpAddress = "1.2.3.4" },
            new AdminAuditLog { TenantId = tenantB, EntityType = "Tenant", EntityId = tenantB.ToString(), Action = "TenantCreated", PerformedByName = "platform_admin", IpAddress = "1.2.3.4" });
        await db.SaveChangesAsync();

        var aLogs = await db.AdminAuditLogs.Where(l => l.TenantId == tenantA).ToListAsync();
        var bLogs = await db.AdminAuditLogs.Where(l => l.TenantId == tenantB).ToListAsync();

        aLogs.Should().HaveCount(2);
        bLogs.Should().HaveCount(1);
        aLogs.Select(l => l.Action).Should().Contain("TenantCreated").And.Contain("Suspended");
        bLogs.Should().NotContain(l => l.TenantId == tenantA);
    }

    // ── Audit log ordering ────────────────────────────────────────────────────

    [Fact]
    public async Task AuditLogs_OrderedByDescendingTime()
    {
        await using var db = CreateDb();
        var svc      = new AuditService(db);
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();
        var ctx      = new RequestContext("127.0.0.1", "Agent", userId, tenantId);

        await svc.WriteAsync("first",  "Event", "1", ctx, null, CancellationToken.None);
        await Task.Delay(5);
        await svc.WriteAsync("second", "Event", "2", ctx, null, CancellationToken.None);
        await Task.Delay(5);
        await svc.WriteAsync("third",  "Event", "3", ctx, null, CancellationToken.None);

        var ordered = await db.AuditLogs
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Select(l => l.Action)
            .ToListAsync();

        ordered[0].Should().Be("third");
        ordered[2].Should().Be("first");
    }
}

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CentralAuditChainPostgresTests
{
    private readonly PostgresFixture _fixture;
    public CentralAuditChainPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ConcurrentWriters_CreateOneForkFreeTenantChain()
    {
        var tenantId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Write(string action)
        {
            await using var db = _fixture.CreateDb();
            await gate.Task;
            await new AuditService(db).WriteAsync(action, "ConcurrencyProbe", action,
                new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId), null,
                CancellationToken.None);
        }

        var first = Write("audit.concurrent.first");
        var second = Write("audit.concurrent.second");
        gate.SetResult();
        await Task.WhenAll(first, second);

        await using var verify = _fixture.CreateDb();
        var rows = await verify.AuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .ToListAsync();
        rows.Should().HaveCount(2);
        AuditService.VerifyChain(rows).IsValid.Should().BeTrue();
        rows.Select(x => x.EntryHash).Should().OnlyHaveUniqueItems();
    }
}
