using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Cost-centre accounting v1, WP-A (contract 1.12): <c>IAuditService.Stage</c> adds the audit row to the context WITHOUT saving,
/// so the business change and its audit row commit in the same SaveChanges.</summary>
public class AuditServiceStageTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Stage_AddsTheRowWithoutSaving_AndStampsCompanyAndBeforeAfter()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var user = Guid.NewGuid();
        IAuditService audit = new AuditService(db);

        audit.Stage("organization.cost_center_updated", "CostCenter", "abc", new RequestContext("1.2.3.4", "agent", user, tenant),
            "{\"before\":{\"name\":\"A\"},\"after\":{\"name\":\"B\"}}", company);

        (await db.AuditLogs.CountAsync()).Should().Be(0, "staging must not save");
        await db.SaveChangesAsync();

        var row = await db.AuditLogs.SingleAsync();
        (row.TenantId, row.CompanyId, row.UserId, row.Action, row.EntityName, row.EntityId).Should()
            .Be((tenant, company, user, "organization.cost_center_updated", "CostCenter", "abc"));
        row.Metadata.Should().Contain("\"before\"").And.Contain("\"after\"");
        row.IpAddress.Should().Be("1.2.3.4");
    }

    [Fact]
    public async Task StagedRows_ChainFromEachOtherAndFromTheDatabase_SoTheChainVerifies()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var ctx = new RequestContext("ip", "ua", Guid.NewGuid(), tenant);
        IAuditService audit = new AuditService(db);
        await audit.WriteAsync("seed", "X", "1", ctx, null, default); // an existing row the staged ones must chain from

        audit.Stage("a", "X", "2", ctx, "{}", null);
        audit.Stage("b", "X", "3", ctx, "{}", null);
        audit.Stage("c", "X", "4", ctx, "{}", null);
        await db.SaveChangesAsync();

        var rows = await db.AuditLogs.Where(x => x.TenantId == tenant).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
        rows.Should().HaveCount(4);
        AuditService.VerifyChain(rows).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Stage_RefusesARowWithoutATenant()
    {
        await using var db = Db();
        IAuditService audit = new AuditService(db);
        var act = () => audit.Stage("a", "X", "1", new RequestContext("ip", "ua", null, null), "{}", null);
        act.Should().Throw<InvalidOperationException>();
    }
}
