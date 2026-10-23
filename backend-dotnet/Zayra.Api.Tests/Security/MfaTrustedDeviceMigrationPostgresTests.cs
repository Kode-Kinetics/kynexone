using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261022183516_AddMfaTrustedDevices on the real migration chain and real PostgreSQL: applies as an upgrade over an
/// existing tenant's settings (they get 30 days), the CHECKs and the unique hash hold, Down removes it and it
/// re-applies. Then the concurrency contract: parallel verifies of one challenge with rememberDevice never throw
/// anything but the normal 401, and exactly one session and one trusted browser come out of it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MfaTrustedDeviceMigrationPostgresTests
{
    private const string ThisMigration = "20261022183516_AddMfaTrustedDevices";
    private const string PreviousMigration = "20261021033332_AddCompanySetupDrafts";
    private const string Password = "Correct-Horse-Battery-9!";

    [Fact]
    public async Task Migration_UpgradesInPlace_EnforcesItsConstraints_RollsBack_AndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        all.Should().Contain(ThisMigration);
        all.IndexOf(ThisMigration).Should().Be(all.IndexOf(PreviousMigration) + 1);

        await migrator.MigrateAsync(PreviousMigration);
        var tenantId = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO security_settings (id, tenant_id, allow_multiple_sessions, lockout_duration_minutes, max_failed_login_attempts, mfa_required, " +
            "password_expiry_days, password_history_count, password_min_length, password_require_digit, password_require_lowercase, " +
            "password_require_special, password_require_uppercase, refresh_token_expiry_days, session_timeout_minutes, updated_at_utc) " +
            "VALUES (gen_random_uuid(), {0}, true, 30, 5, false, 90, 5, 10, true, true, true, true, 30, 480, now())", tenantId);

        await migrator.MigrateAsync(ThisMigration);
        (await Scalar<int>(container, "SELECT mfa_trusted_device_days FROM security_settings")).Should().Be(30, "an existing tenant gets the default");
        var tooLong = async () => await Exec(container, "UPDATE security_settings SET mfa_trusted_device_days = 91");
        (await tooLong.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        await Exec(container, "UPDATE security_settings SET mfa_trusted_device_days = 0");

        // A real tenant and user so the FKs hold; then the table's own CHECKs and the unique hash.
        var userId = await SeedTenantUserAsync(db, "upgrade");
        string Insert(string hash, string extra = "NULL, NULL") =>
            "INSERT INTO mfa_trusted_devices (id, tenant_id, user_id, token_hash, created_at_utc, expires_at_utc, revoked_at_utc, revoked_reason) " +
            $"SELECT gen_random_uuid(), tenant_id, id, '{hash}', now(), now() + interval '30 days', {extra} FROM users WHERE id = '{userId}'";
        await Exec(container, Insert(new string('A', 64)));
        var duplicate = async () => await Exec(container, Insert(new string('A', 64)));
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        var badReason = async () => await Exec(container, Insert(new string('B', 64), "now(), 'because'"));
        (await badReason.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        var halfRevoked = async () => await Exec(container, Insert(new string('C', 64), "now(), NULL"));
        (await halfRevoked.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        await Exec(container, Insert(new string('D', 64), "now(), 'user_revoked'"));
        (await Scalar<string>(container,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_mfa_trusted_devices__tenant_user_active'"))
            .Should().Contain("WHERE (revoked_at_utc IS NULL)");

        // Deleting the login deletes its trusted browsers.
        await Exec(container, $"DELETE FROM mfa_trusted_devices WHERE user_id = '{userId}' AND revoked_at_utc IS NOT NULL");
        (await Scalar<long>(container, "SELECT count(*) FROM mfa_trusted_devices")).Should().Be(1);

        await migrator.MigrateAsync(PreviousMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'mfa_trusted_devices'")).Should().Be(0);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'security_settings' AND column_name = 'mfa_trusted_device_days'"))
            .Should().Be(0);

        await migrator.MigrateAsync(null);
        (await Scalar<int>(container, "SELECT mfa_trusted_device_days FROM security_settings WHERE tenant_id = '" + tenantId + "'")).Should().Be(30);
    }

    [Fact]
    public async Task ParallelVerifiesOfOneChallenge_WithRememberDevice_YieldOneSessionAndOneTrust_AndNeverThrowOtherwise()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .Options;
        Guid userId;
        await using (var setup = new ZayraDbContext(options))
        {
            await setup.Database.MigrateAsync();
            userId = await SeedTenantUserAsync(setup, "parallel");
        }
        var protection = DataProtectionProvider.Create("mfa-trust-postgres");
        var totp = new TotpService(protection);
        var secret = totp.GenerateBase32Secret();
        await using (var db = new ZayraDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.MFAEnabled = true;
            user.MfaSecretEncrypted = totp.EncryptSecret(secret);
            user.MfaConfiguredAtUtc = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        AuthService Auth(ZayraDbContext db)
        {
            var tokens = new JwtTokenService(AuthHardeningTestKit.Jwt);
            var audit = new AuditService(db);
            return new AuthService(db, new Pbkdf2PasswordHasher(), tokens, audit, new NoopEmail(), AuthHardeningTestKit.Jwt,
                new MfaService(db, totp, tokens, audit), totp, NullLogger<AuthService>.Instance, new ConfigurationBuilder().Build());
        }

        string challenge;
        await using (var db = new ZayraDbContext(options))
            challenge = (await Auth(db).LoginAsync(new LoginRequest("parallel@pg.local", Password, "parallel"),
                AuthHardeningTestKit.Ctx, CancellationToken.None)).Challenge!.ChallengeToken;
        var code = Code(secret);

        var attempts = Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = new ZayraDbContext(options);
            try
            {
                var response = await Auth(db).CompleteMfaLoginAsync(challenge, code, AuthHardeningTestKit.Ctx, rememberDevice: true, CancellationToken.None);
                return response.MfaTrustToken is null ? "ok-without-trust" : "ok";
            }
            catch (UnauthorizedAccessException) { return "401"; }
        }).ToList();
        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(o => o == "ok").Should().Be(1, string.Join(",", outcomes));
        outcomes.Count(o => o == "401").Should().Be(5);
        await using var verify = new ZayraDbContext(options);
        (await verify.MfaTrustedDevices.CountAsync(x => x.UserId == userId)).Should().Be(1);

        // Revoke all, twice: the second is a no-op with no second audit row.
        for (var i = 0; i < 2; i++)
        {
            await using var db = new ZayraDbContext(options);
            var tenantId = await db.Users.Where(u => u.Id == userId).Select(u => u.TenantId).SingleAsync();
            await MfaTrustedDevices.StageRevokeAllAsync(db, tenantId, [userId], MfaTrustRevokeReasons.UserRevokedAll,
                DateTime.UtcNow, AuthHardeningTestKit.Ctx, CancellationToken.None);
            await db.SaveChangesAsync();
        }
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == MfaTrustedDevices.RevokedAuditAction)).Should().Be(1);
    }

    private static async Task<Guid> SeedTenantUserAsync(ZayraDbContext db, string slug)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = slug, Slug = slug, IsActive = true };
        db.Tenants.Add(tenant);
        var email = $"{slug}@pg.local";
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = email,
            PasswordHash = new Pbkdf2PasswordHasher().Hash(Password), Status = "Active", IsActive = true, IsEmailConfirmed = true,
            IsGroupScope = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static string Code(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var input = secret.TrimEnd('=').ToUpperInvariant();
        var key = new byte[input.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in input)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; key[index++] = (byte)((buffer >> bits) & 0xFF); }
        }
        var msg = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16) | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        return (bin % 1_000_000).ToString("D6");
    }

    private static async Task Exec(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class NoopEmail : Zayra.Api.Infrastructure.Email.IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<Zayra.Api.Infrastructure.Email.EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
