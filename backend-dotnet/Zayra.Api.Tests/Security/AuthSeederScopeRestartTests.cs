using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// PRIVILEGE-ESCALATION-BY-RESTART regression.
///
/// <para>AuthSeeder ran, tenant-wide on every boot, a raw-SQL
/// <c>UPDATE users SET is_group_scope = TRUE</c> over every Admin-role user with no active entity
/// grant, plus an unconditional re-promotion of the bootstrap admin. "Admin role, no active grant"
/// is also the exact shape of a DELIBERATELY de-scoped administrator, so every restart silently
/// reversed an operator's recorded security decision. On Render that is every deploy, and this
/// service also OOM-restarted three times in four days.</para>
///
/// <para>These tests assert the OBSERVABLE consequence, not the shape of the SQL: a scope an
/// administrator deliberately narrowed is still narrow after the service restarts. "Restart" is
/// modelled the way production restarts — a further <see cref="AuthSeeder.SeedAsync"/> over the
/// same database through a FRESH <see cref="ZayraDbContext"/>, so nothing survives in the change
/// tracker and the assertion cannot be satisfied by a stale in-memory entity.</para>
///
/// <para>Real Postgres, because the defect lived in raw SQL that no in-memory provider executes,
/// and because the assertions must see what the database actually holds. The context the fixture
/// builds has no HttpContextAccessor, which is exactly how the seeder runs at boot: system scope,
/// query filters bypassed (ZayraDbContext.cs:61) — so these tests see every tenant's rows, as the
/// removed UPDATE did.</para>
/// </summary>
[Collection("Integration")]
public class AuthSeederScopeRestartTests
{
    private readonly PostgresFixture _pg;

    public AuthSeederScopeRestartTests(PostgresFixture pg) => _pg = pg;

    /// <summary>
    /// AccessController.SetGroupScope(false) — audited as "GroupScopeRevoked", refresh tokens
    /// revoked — narrows the bootstrap admin itself. A restart must not undo it.
    /// </summary>
    [Fact]
    public async Task BootstrapAdmin_DeliberatelyNarrowedScope_SurvivesRestart()
    {
        var options = NewSeedOptions();

        await using (var db = _pg.CreateDb())
            await NewSeeder(db, options).SeedAsync();

        // Provisioning the bootstrap admin AT group scope is the legitimate half of this seeder:
        // it is creating an account that did not exist. That must keep working.
        var adminId = await BootstrapAdminIdAsync(options);
        (await GroupScopeAsync(adminId)).Should().BeTrue("the bootstrap admin is created group-scoped");

        // An administrator deliberately narrows it, exactly as AccessController.SetGroupScope does.
        await using (var db = _pg.CreateDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == adminId);
            user.IsGroupScope = false;
            user.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // Restart. Twice: idempotence is part of the contract, because under
        // NpgsqlRetryingExecutionStrategy a retry re-runs the entire seeder body.
        await using (var db = _pg.CreateDb())
            await NewSeeder(db, options).SeedAsync();
        await using (var db = _pg.CreateDb())
            await NewSeeder(db, options).SeedAsync();

        (await GroupScopeAsync(adminId)).Should().BeFalse(
            "a scope reduction an administrator made deliberately must survive a restart");
    }

    /// <summary>
    /// The second escalation path, and the worse one: AccessController's grant delete sets
    /// UserEntityAccess.IsActive = false. Revoking a non-bootstrap Admin's LAST company grant left
    /// them matching the seeder's "no active grant" predicate, so the next boot did not restore
    /// their one company — it handed them the whole group. Revocation became escalation.
    /// </summary>
    [Fact]
    public async Task TenantAdmin_WhoseLastGrantWasRevoked_IsNotPromotedToGroupScopeByRestart()
    {
        var options = NewSeedOptions();

        await using (var db = _pg.CreateDb())
            await NewSeeder(db, options).SeedAsync();

        var tenantId = await SeededTenantIdAsync(options);
        var adminRoleId = await AdminRoleIdAsync(tenantId);

        // A second tenant administrator, narrowed to a single company.
        var narrowedId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var email = $"narrowed-{narrowedId:N}@example.com";
        await using (var db = _pg.CreateDb())
        {
            db.Companies.Add(new Company
            {
                Id = companyId,
                TenantId = tenantId,
                LegalNameEn = "Narrowed Co",
                TradeName = "Narrowed Co",
                CountryCode = "SA",
            });
            db.Users.Add(new User
            {
                Id = narrowedId,
                TenantId = tenantId,
                Email = email,
                NormalizedEmail = AuthService.Normalize(email),
                FullName = "Narrowed Admin",
                PasswordHash = "not-a-login-path-in-this-test",
                AccessMode = "FullPortal",
                Status = "Active",
                IsActive = true,
                IsEmailConfirmed = true,
                IsGroupScope = false,
            });
            db.UserRoles.Add(new UserRole { UserId = narrowedId, RoleId = adminRoleId });
            db.UserEntityAccesses.Add(new UserEntityAccess
            {
                TenantId = tenantId,
                UserId = narrowedId,
                CompanyId = companyId,
                Role = "Admin",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        // Their one grant is revoked the way AccessController revokes it: deactivated, not deleted.
        await using (var db = _pg.CreateDb())
        {
            var grant = await db.UserEntityAccesses.SingleAsync(g => g.UserId == narrowedId);
            grant.IsActive = false;
            grant.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateDb())
            await NewSeeder(db, options).SeedAsync();

        (await GroupScopeAsync(narrowedId)).Should().BeFalse(
            "revoking an administrator's last company grant must not widen them to the whole group");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Unique per test: every class in the Integration collection shares one database.</summary>
    private static SeedAdminOptions NewSeedOptions()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new SeedAdminOptions
        {
            TenantSlug = $"seedscope{suffix}",
            TenantName = $"Seed Scope {suffix}",
            Email = $"admin-{suffix}@example.com",
            FullName = "Bootstrap Admin",
            Password = "A-strong-bootstrap-password-1!",
            SeedDemoData = false,
        };
    }

    private static AuthSeeder NewSeeder(ZayraDbContext db, SeedAdminOptions options) =>
        new(db, new Pbkdf2PasswordHasher(), Options.Create(options));

    private async Task<Guid> SeededTenantIdAsync(SeedAdminOptions options)
    {
        var slug = options.TenantSlug.ToLowerInvariant();
        await using var db = _pg.CreateDb();
        return await db.Tenants.Where(t => t.Slug == slug).Select(t => t.Id).SingleAsync();
    }

    private async Task<Guid> AdminRoleIdAsync(Guid tenantId)
    {
        await using var db = _pg.CreateDb();
        return await db.Roles
            .Where(r => r.TenantId == tenantId && r.NormalizedName == "ADMIN")
            .Select(r => r.Id)
            .SingleAsync();
    }

    private async Task<Guid> BootstrapAdminIdAsync(SeedAdminOptions options)
    {
        var normalized = AuthService.Normalize(options.Email);
        await using var db = _pg.CreateDb();
        return await db.Users.Where(u => u.NormalizedEmail == normalized).Select(u => u.Id).SingleAsync();
    }

    /// <summary>
    /// Read is_group_scope straight out of Postgres. The escalation was a raw-SQL UPDATE, so the
    /// assertion reads the column rather than trusting a tracked entity.
    /// </summary>
    private async Task<bool> GroupScopeAsync(Guid userId)
    {
        await using var db = _pg.CreateDb();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT COALESCE(is_group_scope, FALSE) FROM users WHERE id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = userId;
        cmd.Parameters.Add(p);
        var value = await cmd.ExecuteScalarAsync();
        value.Should().NotBeNull("the user must still exist");
        return (bool)value!;
    }
}
