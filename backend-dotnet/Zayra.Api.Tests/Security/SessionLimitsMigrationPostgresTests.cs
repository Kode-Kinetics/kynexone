using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261023134050_AddSessionMaxLengthAndEpoch on the real migration chain and real PostgreSQL: Up over existing rows
/// (an upgrade in place), Down, and Up again; the backfill and defaults sign nobody out — a session that existed before
/// the deploy refreshes through the real pipeline afterwards — and the epoch trigger exists exactly while the column does.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionLimitsMigrationPostgresTests
{
    private const string ThisMigration = "20261023134050_AddSessionMaxLengthAndEpoch";
    private const string PreviousMigration = "20261023031347_AddMfaBackupCodesAndAdminReset";
    private const string Password = "Correct-Horse-Battery-9!";
    private const string RawRefresh = "pre-deploy-refresh-token-for-the-backfill-test-0001";

    [Fact]
    public async Task UpDownUp_BackfillsWithoutEndingAnySession_AndThePreDeploySessionStillRefreshes()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var cs = container.GetConnectionString();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(cs).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        Assert.Equal(all.IndexOf(PreviousMigration) + 1, all.IndexOf(ThisMigration));

        // Seed through the current model, then step back: the rows exist before the columns do.
        await migrator.MigrateAsync(null);
        var tenantId = Guid.NewGuid();
        var slug = $"ses-mig-{tenantId:N}"[..24];
        var userId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Session migration", Slug = slug, IsActive = true });
        db.Users.Add(new User
        {
            Id = userId, TenantId = tenantId, Email = "pre@sessions.test", NormalizedEmail = "PRE@SESSIONS.TEST", FullName = "Pre",
            PasswordHash = new Pbkdf2PasswordHasher().Hash(Password), Status = "Active", IsActive = true, IsEmailConfirmed = true,
            IsGroupScope = true,
        });
        db.SecuritySettings.Add(new SecuritySetting { TenantId = tenantId });
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId, TokenHash = new JwtTokenService(AuthHardeningTestKit.Jwt).HashToken(RawRefresh),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(20),
        });
        await db.SaveChangesAsync();
        await migrator.MigrateAsync(PreviousMigration);
        Assert.Equal(0L, await Scalar<long>(cs, "SELECT count(*) FROM information_schema.columns WHERE column_name IN ('session_epoch','session_started_at_utc','session_max_hours')"));
        // The last rotation was seven hours ago (inside the 8-hour idle timeout); the original sign-in is unknowable.
        await Exec(cs, "UPDATE refresh_tokens SET created_at_utc = now() - interval '7 hours'");

        await migrator.MigrateAsync(ThisMigration);
        await AssertUpgradedAsync(cs, userId, tenantId);

        await migrator.MigrateAsync(PreviousMigration);
        Assert.Equal(0L, await Scalar<long>(cs, "SELECT count(*) FROM information_schema.columns WHERE column_name IN ('session_epoch','session_started_at_utc','session_max_hours')"));
        Assert.Equal(0L, await Scalar<long>(cs, "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_users__session_epoch_increments'"));
        Assert.Equal(0L, await Scalar<long>(cs, "SELECT count(*) FROM pg_proc WHERE proname = 'fn_users__session_epoch_increments'"));

        await migrator.MigrateAsync(ThisMigration);
        await AssertUpgradedAsync(cs, userId, tenantId);
        // Later migrations exist (they shipped in the same release); the real pipeline below runs the CURRENT model,
        // so bring the database to the latest schema first rather than assume this migration is the newest.
        await migrator.MigrateAsync(null);

        // A row written by an earlier release (no new columns named) still gets a usable session start and epoch.
        await Exec(cs, $"INSERT INTO refresh_tokens (id, family_id, user_id, token_hash, expires_at_utc, created_at_utc) " +
                       $"VALUES (gen_random_uuid(), gen_random_uuid(), '{userId}', 'LEGACY-WRITER', now() + interval '1 day', now())");
        Assert.Equal(1L, await Scalar<long>(cs, "SELECT count(*) FROM refresh_tokens WHERE token_hash = 'LEGACY-WRITER' AND session_epoch = 0 AND session_started_at_utc > now() - interval '1 minute'"));
        // The trigger: an epoch can only move one step up from the stored row, whatever value is written.
        await Exec(cs, $"UPDATE users SET session_epoch = 41 WHERE id = '{userId}'");
        await Exec(cs, $"UPDATE users SET session_epoch = 0 WHERE id = '{userId}'");
        Assert.Equal(2L, await Scalar<long>(cs, $"SELECT session_epoch FROM users WHERE id = '{userId}'"));
        await Exec(cs, $"UPDATE users SET full_name = 'Renamed' WHERE id = '{userId}'");
        Assert.Equal(2L, await Scalar<long>(cs, $"SELECT session_epoch FROM users WHERE id = '{userId}'"));
        await Exec(cs, $"UPDATE users SET session_epoch = 0 WHERE id = '{userId}'"); // reset is impossible: 3
        Assert.Equal(3L, await Scalar<long>(cs, $"SELECT session_epoch FROM users WHERE id = '{userId}'"));
        await Exec(cs, $"UPDATE refresh_tokens SET session_epoch = 3 WHERE user_id = '{userId}'");

        // The session that existed before the deploy refreshes through the real pipeline, and keeps its backfilled start.
        using var host = new SessionLimitsHost(cs);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = RawRefresh });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var next = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()).RootElement.GetProperty("refreshToken").GetString()!;
        var hash = new JwtTokenService(AuthHardeningTestKit.Jwt).HashToken(next);
        Assert.Equal(1L, await Scalar<long>(cs,
            $"SELECT count(*) FROM refresh_tokens c JOIN refresh_tokens p ON p.replaced_by_token_hash = c.token_hash " +
            $"WHERE c.token_hash = '{hash}' AND c.session_started_at_utc = p.session_started_at_utc AND c.session_epoch = 3"));
    }

    private static async Task AssertUpgradedAsync(string cs, Guid userId, Guid tenantId)
    {
        Assert.Equal(0L, await Scalar<long>(cs, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{userId}' AND session_started_at_utc <> created_at_utc"));
        Assert.Equal(0L, await Scalar<long>(cs, $"SELECT session_epoch FROM users WHERE id = '{userId}'"));
        Assert.Equal(0L, await Scalar<long>(cs, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{userId}' AND session_epoch <> 0"));
        Assert.Equal(12, await Scalar<int>(cs, $"SELECT session_max_hours FROM security_settings WHERE tenant_id = '{tenantId}'"));
        Assert.Equal(1L, await Scalar<long>(cs, "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_users__session_epoch_increments' AND NOT tgisinternal"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(cs, $"UPDATE security_settings SET session_max_hours = 0 WHERE tenant_id = '{tenantId}'"));
        Assert.Equal("23514", ex.SqlState);
    }

    private static async Task Exec(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
