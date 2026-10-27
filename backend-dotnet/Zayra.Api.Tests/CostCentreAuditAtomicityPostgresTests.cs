using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A (contract 1.12, 2.9) on real Postgres: T-045 (the audit row and the change commit together, and
/// a failing audit insert rolls the change back), S-045 (cost-centre part), S-044 (concurrent writers do not fork the audit chain).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CostCentreAuditAtomicityPostgresTests(PostgresFixture fixture)
{
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);
    private static OrganizationSetupService Svc(ZayraDbContext db) => new(db, new AuditService(db));

    private async Task<(Guid Tenant, Guid Company)> TenantAsync()
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = $"A-{tenant:N}", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenant, company.Id);
    }

    /// <summary>Makes every audit_logs insert for the tenant fail, returning the cleanup.</summary>
    private async Task<Func<Task>> BreakAuditInsertsAsync(Guid tenant)
    {
        var name = $"trg_break_audit_{tenant:N}";
        await using var db = fixture.CreateDb();
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE FUNCTION {name}() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'audit insert blocked for test'; END $$ LANGUAGE plpgsql; " +
            $"CREATE TRIGGER {name} BEFORE INSERT ON audit_logs FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}') EXECUTE FUNCTION {name}();");
        return async () =>
        {
            await using var d = fixture.CreateDb();
            await d.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS {name} ON audit_logs; DROP FUNCTION IF EXISTS {name}();");
        };
    }

    [Fact]
    public async Task T045_TheChangeAndItsAuditRowCommitTogether_WithBeforeAfterAndTheCompany()
    {
        var (tenant, company) = await TenantAsync();
        Guid id;
        await using (var db = fixture.CreateDb())
            id = (await Svc(db).CreateCostCenterAsync(tenant, new CostCenterRequest(company, "OPS", "Operations"), Ctx(tenant), default)).Id;
        await using (var db = fixture.CreateDb())
            await Svc(db).UpdateCostCenterAsync(tenant, id, new CostCenterRequest(company, "OPS", "Renamed"), Ctx(tenant), default);

        await using var read = fixture.CreateDb();
        var rows = await read.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant && a.EntityId == id.ToString()).OrderBy(a => a.CreatedAtUtc).ToListAsync();
        rows.Select(r => r.Action).Should().Equal("organization.cost_center_created", "organization.cost_center_updated");
        rows.Should().OnlyContain(r => r.CompanyId == company);
        rows[1].Metadata.Should().Contain("Operations").And.Contain("Renamed");
    }

    [Fact]
    public async Task T045_S045_WhenTheAuditInsertFails_TheCreateUpdateAndDeleteAreRolledBack()
    {
        var (tenant, company) = await TenantAsync();
        Guid existing;
        await using (var db = fixture.CreateDb())
            existing = (await Svc(db).CreateCostCenterAsync(tenant, new CostCenterRequest(company, "KEEP", "Keep"), Ctx(tenant), default)).Id;

        var cleanup = await BreakAuditInsertsAsync(tenant);
        try
        {
            await using (var db = fixture.CreateDb())
                await FluentActions.Awaiting(() => Svc(db).CreateCostCenterAsync(tenant, new CostCenterRequest(company, "NEW", "New"), Ctx(tenant), default)).Should().ThrowAsync<Exception>();
            await using (var db = fixture.CreateDb())
                await FluentActions.Awaiting(() => Svc(db).UpdateCostCenterAsync(tenant, existing, new CostCenterRequest(company, "KEEP", "Changed"), Ctx(tenant), default)).Should().ThrowAsync<Exception>();
            await using (var db = fixture.CreateDb())
                await FluentActions.Awaiting(() => Svc(db).DeleteCostCenterAsync(tenant, existing, Ctx(tenant), default)).Should().ThrowAsync<Exception>();
        }
        finally { await cleanup(); }

        await using var read = fixture.CreateDb();
        (await read.CostCenters.AsNoTracking().Where(c => c.TenantId == tenant && c.Code == "NEW").CountAsync()).Should().Be(0, "the create rolled back");
        var kept = await read.CostCenters.AsNoTracking().SingleAsync(c => c.Id == existing);
        (kept.Name, kept.IsDeleted).Should().Be(("Keep", false), "the update and the delete rolled back");
    }

    [Fact]
    public async Task S044_ConcurrentWritersForOneTenant_DoNotForkTheAuditChain()
    {
        var (tenant, company) = await TenantAsync();
        var ids = new List<Guid>();
        await using (var db = fixture.CreateDb())
            for (var i = 0; i < 6; i++)
                ids.Add((await Svc(db).CreateCostCenterAsync(tenant, new CostCenterRequest(company, $"C{i}", $"C{i}"), Ctx(tenant), default)).Id);

        async Task Worker(int offset)
        {
            for (var round = 0; round < 6; round++)
            {
                await using var db = fixture.CreateDb();
                var id = ids[(offset + round) % ids.Count];
                var current = await db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == id);
                await Svc(db).UpdateCostCenterAsync(tenant, id, new CostCenterRequest(company, current.Code, $"{current.Name}.{offset}"), Ctx(tenant), default);
            }
        }
        await Task.WhenAll(Worker(0), Worker(2), Worker(4));

        await using var read = fixture.CreateDb();
        var rows = await read.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant).OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToListAsync();
        rows.Should().HaveCount(6 + 18);
        AuditService.VerifyChain(rows).IsValid.Should().BeTrue("PreviousHash must be unique per tenant: the chain never forks");
        rows.Select(r => r.PreviousHash).Should().OnlyHaveUniqueItems();
    }
}
