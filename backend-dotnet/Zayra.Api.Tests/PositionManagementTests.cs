using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class PositionManagementTests
{
    [Fact]
    public async Task CreateThenFreeze_PositionHasGovernedLifecycleAndAudit()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var controller = CreateController(db, tenantId);
        var request = new PositionRequest("POS-HR-001", "HR Officer", null, null, null, null, null, null, 1m, 12_000m, "SAR", new DateOnly(2026, 1, 1), null);

        var created = await controller.Create(request, CancellationToken.None);
        var position = ((CreatedResult)created).Value.Should().BeOfType<Position>().Subject;
        position.Status.Should().Be(PositionStatuses.Open);

        var frozen = await controller.Freeze(position.Id, CancellationToken.None);
        ((OkObjectResult)frozen).Value.Should().BeOfType<Position>().Subject.Status.Should().Be(PositionStatuses.Frozen);
        (await db.AuditLogs.CountAsync(x => x.TenantId == tenantId && x.EntityName == "Position")).Should().Be(2);
    }

    [Fact]
    public async Task Create_RejectsDuplicateCodeWithinTenant()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        db.Positions.Add(new Position { TenantId = tenantId, Code = "POS-001", Title = "Analyst" });
        await db.SaveChangesAsync();

        var result = await CreateController(db, tenantId).Create(
            new PositionRequest("POS-001", "Duplicate", null, null, null, null, null, null, 1m, 0m, "SAR", new DateOnly(2026, 1, 1), null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static PositionsController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "organization.read"),
            new Claim("permission", "organization.write"),
            new Claim("is_group_scope", "true")
        }, "test"));
        return new PositionsController(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }
}
