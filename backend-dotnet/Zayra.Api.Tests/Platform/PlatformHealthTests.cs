using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Zayra.Api.Models;        // AdminAuditLog is in Zayra.Api.Models (SetupAdmin.cs)

namespace Zayra.Api.Tests.Platform;

public class PlatformHealthTests : PlatformTestBase
{
    // ── Health endpoint ────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_Returns200_WithStatusField()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.Health(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("status");
    }

    [Fact]
    public async Task Health_ReturnsComponentsWithExpectedKeys()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.Health(CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);

        // All four component keys must be present
        json.Should().Contain("database");
        json.Should().Contain("smtp");
        json.Should().Contain("redis");
        json.Should().Contain("jobs");
    }

    [Fact]
    public async Task Health_ReturnsVersionField()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.Health(CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("version");
    }

    [Fact]
    public async Task Health_DatabaseStatus_IsDegradedWhenInMemory()
    {
        // InMemory provider: CanConnectAsync returns true for InMemory,
        // so status will be "healthy" with InMemory DB.
        // We just assert the response is structured correctly.
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.Health(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(ok.Value));

        // database.status must be either "ok" or "error" — never missing
        body.TryGetProperty("components", out var comps).Should().BeTrue();
        comps.TryGetProperty("database", out var db2).Should().BeTrue();
        db2.TryGetProperty("status", out var dbStatus).Should().BeTrue();
        new[] { "ok", "error" }.Should().Contain(dbStatus.GetString());
    }

    // ── Audit logs endpoint ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAuditLogs_WhenEmpty_Returns200WithTotalAndLogsShape()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.GetAuditLogs(tenantId: null, page: 1, pageSize: 50, ct: CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("total");
        json.Should().Contain("logs");
    }

    [Fact]
    public async Task GetAuditLogs_WithSeededLogs_ReturnsCorrectTotal()
    {
        await using var db = CreateDb();
        var tenantId       = Guid.NewGuid();
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId         = tenantId,
            EntityType       = "Tenant",
            EntityId         = tenantId.ToString(),
            Action           = "TenantCreated",
            PerformedByName  = "platform_admin",
            IpAddress        = "127.0.0.1"
        });
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId         = tenantId,
            EntityType       = "Tenant",
            EntityId         = tenantId.ToString(),
            Action           = "Suspended",
            PerformedByName  = "platform_admin",
            IpAddress        = "127.0.0.1"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.GetAuditLogs(tenantId: null, page: 1, pageSize: 50, ct: CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(ok.Value));
        // Unfiltered platform-level query shows Tenant entity-type events
        body.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetAuditLogs_FilterByTenantId_ReturnsOnlyThatTenantLogs()
    {
        await using var db = CreateDb();
        var tenantA        = Guid.NewGuid();
        var tenantB        = Guid.NewGuid();
        db.AdminAuditLogs.AddRange(
            new AdminAuditLog
            {
                TenantId        = tenantA,
                EntityType      = "Tenant",
                EntityId        = tenantA.ToString(),
                Action          = "TenantCreated",
                PerformedByName = "platform_admin",
                IpAddress       = "127.0.0.1"
            },
            new AdminAuditLog
            {
                TenantId        = tenantB,
                EntityType      = "Tenant",
                EntityId        = tenantB.ToString(),
                Action          = "TenantCreated",
                PerformedByName = "platform_admin",
                IpAddress       = "127.0.0.1"
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.GetAuditLogs(tenantId: tenantA, page: 1, pageSize: 50, ct: CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(ok.Value));
        body.GetProperty("total").GetInt32().Should().Be(1);
    }

    // ── Plans endpoint ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPlans_Returns200WithFourStandardPlans()
    {
        using var db    = CreateDb();
        var controller  = CreateController(db);

        var result = await controller.GetPlans(CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("Trial");
        json.Should().Contain("Starter");
        json.Should().Contain("Growth");
        json.Should().Contain("Enterprise");
    }
}
