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
/// 20261023031347_AddMfaBackupCodesAndAdminReset on the real migration chain and real PostgreSQL: the column and its
/// "never without a factor" CHECK, the widened trusted-browser reason vocabulary, Down (which maps mfa_admin_reset back
/// to the earlier vocabulary so the old CHECK can return) and re-apply. Then the concurrency contract: one backup code
/// presented on six challenges at once signs in exactly once; the rest are a plain wrong code (400), nothing else.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MfaBackupCodesPostgresTests
{
    private const string ThisMigration = "20261023031347_AddMfaBackupCodesAndAdminReset";
    private const string PreviousMigration = "20261022183516_AddMfaTrustedDevices";
    private const string Password = "Correct-Horse-Battery-9!";

    [Fact]
    public async Task Migration_EnforcesItsConstraints_RollsBack_AndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        all.IndexOf(ThisMigration).Should().Be(all.IndexOf(PreviousMigration) + 1);

        // Seed through the current model, then step back: the rows exist before the column does (an upgrade in place).
        await migrator.MigrateAsync(null);
        var userId = await SeedTenantUserAsync(db, "upgrade");
        await migrator.MigrateAsync(PreviousMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'mfa_recovery_code_hashes'"))
            .Should().Be(0);
        await migrator.MigrateAsync(ThisMigration);

        (await Scalar<long>(container, $"SELECT count(*) FROM users WHERE id = '{userId}' AND mfa_recovery_code_hashes IS NULL")).Should().Be(1,
            "an existing login upgrades with no codes");
        var noFactor = async () => await Exec(container, $"UPDATE users SET mfa_recovery_code_hashes = 'v1$1$AA==$AA==' WHERE id = '{userId}'");
        (await noFactor.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        await Exec(container, $"UPDATE users SET mfa_secret_encrypted = 'x', mfa_recovery_code_hashes = 'v1$1$AA==$AA==' WHERE id = '{userId}'");

        string Trusted(string hash, string reason) =>
            "INSERT INTO mfa_trusted_devices (id, tenant_id, user_id, token_hash, created_at_utc, expires_at_utc, revoked_at_utc, revoked_reason) " +
            $"SELECT gen_random_uuid(), tenant_id, id, '{hash}', now(), now() + interval '30 days', now(), '{reason}' FROM users WHERE id = '{userId}'";
        await Exec(container, Trusted(new string('A', 64), MfaTrustRevokeReasons.MfaAdminReset));
        var unknown = async () => await Exec(container, Trusted(new string('B', 64), "because"));
        (await unknown.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        // Every reason the code can write is accepted by the CHECK.
        var i = 0;
        foreach (var reason in MfaTrustRevokeReasons.All)
            await Exec(container, Trusted(((char)('C' + i++)).ToString().PadRight(64, '0'), reason));

        await migrator.MigrateAsync(PreviousMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'mfa_recovery_code_hashes'"))
            .Should().Be(0);
        (await Scalar<long>(container, "SELECT count(*) FROM mfa_trusted_devices WHERE revoked_reason = 'mfa_admin_reset'")).Should().Be(0);
        (await Scalar<long>(container, $"SELECT count(*) FROM mfa_trusted_devices WHERE token_hash = '{new string('A', 64)}' AND revoked_reason = 'mfa_recovery_disabled'"))
            .Should().Be(1, "Down maps the new reason to its nearest earlier one so the old CHECK can return");

        await migrator.MigrateAsync(null);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'mfa_recovery_code_hashes'"))
            .Should().Be(1);
        (await Scalar<long>(container, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_users__mfa_recovery_code_hashes_need_factor'")).Should().Be(1);
        await Exec(container, Trusted(new string('Z', 64), MfaTrustRevokeReasons.MfaAdminReset));
    }

    [Fact]
    public async Task OneBackupCode_PresentedOnSixChallengesAtOnce_SignsInExactlyOnce()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .Options;
        Guid userId, tenantId;
        await using (var setup = new ZayraDbContext(options))
        {
            await setup.Database.MigrateAsync();
            userId = await SeedTenantUserAsync(setup, "parallel");
            tenantId = await setup.Users.Where(u => u.Id == userId).Select(u => u.TenantId).SingleAsync();
        }
        var totp = new TotpService(DataProtectionProvider.Create("mfa-backup-postgres"));
        var secret = totp.GenerateBase32Secret();
        var set = MfaBackupCodes.Generate();
        await using (var db = new ZayraDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.MFAEnabled = true;
            user.MfaSecretEncrypted = totp.EncryptSecret(secret);
            user.MfaConfiguredAtUtc = DateTime.UtcNow.AddMinutes(-5);
            user.MfaRecoveryCodeHashes = set.Stored;
            await db.SaveChangesAsync();
        }

        AuthService Auth(ZayraDbContext db)
        {
            var tokens = new JwtTokenService(AuthHardeningTestKit.Jwt);
            var audit = new AuditService(db);
            return new AuthService(db, new Pbkdf2PasswordHasher(), tokens, audit, new NoopEmail(), AuthHardeningTestKit.Jwt,
                new MfaService(db, totp, tokens, audit), totp, NullLogger<AuthService>.Instance, new ConfigurationBuilder().Build());
        }

        var challenges = new List<string>();
        for (var n = 0; n < 6; n++)
        {
            await using var db = new ZayraDbContext(options);
            challenges.Add(await new MfaService(db, totp, new JwtTokenService(AuthHardeningTestKit.Jwt), new AuditService(db))
                .CreateChallengeAsync(userId, tenantId, "198.51.100.7", CancellationToken.None));
        }

        var code = set.Codes[2];
        var outcomes = await Task.WhenAll(challenges.Select(async challenge =>
        {
            await using var db = new ZayraDbContext(options);
            try
            {
                await Auth(db).CompleteMfaLoginWithBackupCodeAsync(challenge, code, AuthHardeningTestKit.Ctx, CancellationToken.None);
                return "ok";
            }
            catch (MfaCodeInvalidException) { return "400"; }
            catch (UnauthorizedAccessException) { return "401"; }
        }));

        // Exactly one session. The others lose either on the consumed code (400) or, when the winner committed first,
        // on its rotated security stamp, which ends every challenge issued before it (401). Never anything else.
        outcomes.Count(o => o == "ok").Should().Be(1, string.Join(",", outcomes));
        outcomes.Should().OnlyContain(o => o == "ok" || o == "400" || o == "401");
        await using var verify = new ZayraDbContext(options);
        MfaBackupCodes.Remaining((await verify.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).MfaRecoveryCodeHashes).Should().Be(9);
        (await verify.RefreshTokens.CountAsync(r => r.UserId == userId)).Should().Be(1, "one code, one session");
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "auth.mfa.backup_code_used")).Should().Be(1);
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "auth.mfa.backup_code_failed"))
            .Should().Be(outcomes.Count(o => o == "400"));
    }

    [Fact]
    public async Task AdminReset_OnPostgres_RevokesEverythingInOneTransaction()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .Options;
        Guid userId, tenantId, adminId;
        await using (var setup = new ZayraDbContext(options))
        {
            await setup.Database.MigrateAsync();
            userId = await SeedTenantUserAsync(setup, "reset");
            tenantId = await setup.Users.Where(u => u.Id == userId).Select(u => u.TenantId).SingleAsync();
            await new Zayra.Api.Infrastructure.Seed.AuthSeeder(setup).EnsureTenantRolesAsync(tenantId);
            var adminRole = await setup.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == "Admin");
            var admin = new User
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Email = "admin@reset.local", NormalizedEmail = "ADMIN@RESET.LOCAL", FullName = "Admin",
                PasswordHash = new Pbkdf2PasswordHasher().Hash(Password), Status = "Active", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
            };
            setup.Users.Add(admin);
            setup.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
            await setup.SaveChangesAsync();
            adminId = admin.Id;

            var user = await setup.Users.SingleAsync(u => u.Id == userId);
            user.MFAEnabled = true;
            user.MfaSecretEncrypted = "encrypted";
            user.MfaConfiguredAtUtc = DateTime.UtcNow.AddDays(-1);
            user.MfaRecoveryCodeHashes = MfaBackupCodes.Generate().Stored;
            setup.RefreshTokens.Add(new RefreshToken { UserId = userId, TokenHash = new string('R', 64), ExpiresAtUtc = DateTime.UtcNow.AddDays(1), CreatedAtUtc = DateTime.UtcNow });
            // Its own save: one made with the factor change would be revoked by the save hook as mfa_resetup.
            await setup.SaveChangesAsync();
            setup.MfaTrustedDevices.Add(new MfaTrustedDevice
            {
                TenantId = tenantId, UserId = userId, TokenHash = new string('T', 64),
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            });
            await setup.SaveChangesAsync();
        }

        await using (var db = new ZayraDbContext(options))
        {
            var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(AuthHardeningTestKit.Jwt));
            var result = await service.ResetUserMfaAsync(tenantId, userId, "Lost phone", Zayra.Api.Application.Common.EntityScopeContext.GroupLevel,
                new RequestContext("198.51.100.7", "tests", adminId, tenantId), CancellationToken.None);
            result.Outcome.Should().Be(MfaAdminResetOutcome.Reset);
        }

        await using var verify = new ZayraDbContext(options);
        var after = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        after.MFAEnabled.Should().BeFalse();
        after.MfaSecretEncrypted.Should().BeNull();
        after.MfaRecoveryCodeHashes.Should().BeNull();
        (await verify.RefreshTokens.AsNoTracking().SingleAsync(r => r.UserId == userId)).RevokedAtUtc.Should().NotBeNull();
        (await verify.MfaTrustedDevices.AsNoTracking().SingleAsync(t => t.UserId == userId)).RevokedReason.Should().Be(MfaTrustRevokeReasons.MfaAdminReset);
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "auth.mfa.admin_reset" && a.UserId == adminId)).Should().Be(1);
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
