using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 2 (spec 5): the Auditor role must remain strictly read-only. Controllers gate
/// mutations on write/approve/manage permissions — this pins the registry-level
/// invariant so no future seeding change can quietly grant an auditor a mutating key.
/// </summary>
public class AuditorReadOnlyTests
{
    private static readonly string[] MutatingSuffixes =
        { ".write", ".approve", ".decide", ".manage", ".delete", ".export", ".sync", ".post", ".process", ".policy_manage", ".sensitive" };

    [Fact]
    public async Task AuditorRole_HasOnlyReadPermissions()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ZayraDbContext(options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Audit T", Slug = $"aud-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();

        var seeder = new Zayra.Api.Infrastructure.Seed.AuthSeeder(db,
            new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
            Microsoft.Extensions.Options.Options.Create(new Zayra.Api.Application.Auth.SeedAdminOptions()));
        await seeder.EnsureTenantRolesAsync(tenantId, CancellationToken.None);

        var auditorPermissions = await db.Roles
            .Where(r => r.TenantId == tenantId && r.Name == "Auditor")
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key))
            .ToListAsync();

        auditorPermissions.Should().NotBeEmpty("the Auditor role must exist with read access");
        auditorPermissions.Should().OnlyContain(p => p.EndsWith(".read"),
            "an auditor is read-only by definition — a mutating permission here is a seeding regression");
        auditorPermissions.Should().NotContain(p => MutatingSuffixes.Any(s => p.EndsWith(s)));
    }
}
