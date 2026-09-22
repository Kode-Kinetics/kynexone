using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;
using Zayra.Api.Tests;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AuthWorkspaceBindingPostgresTests : PlatformTestBase
{
    private static readonly RequestContext Context = new("203.0.113.40", "workspace-pg-tests");
    private readonly PostgresFixture _fixture;

    public AuthWorkspaceBindingPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_DuplicateEmailAcrossTenants_BindsNamedWorkspace(bool insertTenantBFirst)
    {
        var seed = await SeedDuplicateEmailUsersAsync(insertTenantBFirst);

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            var result = await BuildService(actionDb).LoginAsync(
                new LoginRequest($"  {seed.Email.ToUpperInvariant()}  ", seed.PasswordA, $"  {seed.SlugA.ToUpperInvariant()}  "),
                Context,
                CancellationToken.None);

            Assert.NotNull(result.Tokens);
            Assert.Equal(seed.UserAId, result.Tokens!.User.Id);
            Assert.Equal(seed.TenantAId, result.Tokens.User.TenantId);
            Assert.Equal(seed.SlugA, result.Tokens.User.TenantSlug);
        }

        await using var verify = _fixture.CreateRetryingDb();
        Assert.Single(await verify.RefreshTokens.AsNoTracking().Where(x => x.UserId == seed.UserAId).ToListAsync());
        Assert.Empty(await verify.RefreshTokens.AsNoTracking().Where(x => x.UserId == seed.UserBId).ToListAsync());
        Assert.Single(await verify.LoginActivities.AsNoTracking().Where(x =>
            x.UserId == seed.UserAId && x.EventType == LoginEventTypes.LoginSuccess).ToListAsync());
        Assert.Empty(await verify.LoginActivities.AsNoTracking().Where(x => x.UserId == seed.UserBId).ToListAsync());
        Assert.Single(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.login" && x.EntityId == seed.UserAId.ToString()).ToListAsync());
        var untouched = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == seed.UserBId);
        Assert.Equal(seed.UserBPasswordHash, untouched.PasswordHash);
        Assert.Equal(0, untouched.FailedLoginCount);
        Assert.False(untouched.IsLocked);
        Assert.Null(untouched.LastLoginAtUtc);
        Assert.Equal(seed.UserBUpdatedAtUtc, untouched.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgotPassword_DuplicateEmailAcrossTenants_MutatesNamedWorkspaceOnly(bool insertTenantBFirst)
    {
        var seed = await SeedDuplicateEmailUsersAsync(insertTenantBFirst);
        var email = new CapturingEmailService();

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await BuildService(actionDb, email).ForgotPasswordAsync(
                new ForgotPasswordRequest($"  {seed.Email.ToUpperInvariant()}  ", $"  {seed.SlugA.ToUpperInvariant()}  "),
                Context,
                CancellationToken.None);
        }

        await using var verify = _fixture.CreateRetryingDb();
        var token = Assert.Single(await verify.PasswordResetTokens.AsNoTracking()
            .Where(x => x.UserId == seed.UserAId).ToListAsync());
        Assert.NotEmpty(token.TokenHash);
        Assert.Empty(await verify.PasswordResetTokens.AsNoTracking().Where(x => x.UserId == seed.UserBId).ToListAsync());
        Assert.Single(await verify.LoginActivities.AsNoTracking().Where(x =>
            x.UserId == seed.UserAId && x.EventType == LoginEventTypes.PasswordResetRequested).ToListAsync());
        Assert.Empty(await verify.LoginActivities.AsNoTracking().Where(x => x.UserId == seed.UserBId).ToListAsync());
        Assert.Single(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId
            && x.Action == "auth.password_reset_requested"
            && x.EntityId == seed.UserAId.ToString()).ToListAsync());
        var sent = Assert.Single(email.Messages);
        Assert.Equal(seed.Email, sent.To);
        Assert.Contains($"https://app.example.test/reset-password?workspace={seed.SlugA}#token=", sent.Html);
        Assert.DoesNotContain(seed.Email, sent.Html.Split("#token=", StringSplitOptions.None)[0]);
    }

    [Fact]
    public async Task ResetPassword_TokenForTenantA_WithTenantBWorkspace_HasZeroPersistedEffects()
    {
        var seed = await SeedRecoveryUserAsync(includeInvitation: false);

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(actionDb).ResetPasswordAsync(
                new ResetPasswordRequest(seed.RawCredential, "DifferentPassword1!", seed.SlugB),
                Context,
                CancellationToken.None));
        }

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
        var token = await verify.PasswordResetTokens.AsNoTracking().SingleAsync(x => x.Id == seed.CredentialId);
        var refresh = await verify.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == seed.RefreshTokenId);
        Assert.Equal(seed.PasswordHash, user.PasswordHash);
        Assert.Equal(seed.UserUpdatedAtUtc, user.UpdatedAtUtc);
        Assert.Null(token.UsedAtUtc);
        Assert.Equal(Hash(seed.RawCredential), token.TokenHash);
        Assert.Null(refresh.RevokedAtUtc);
        Assert.Empty(await verify.LoginActivities.AsNoTracking().Where(x =>
            x.UserId == seed.UserId && x.EventType == LoginEventTypes.PasswordResetCompleted).ToListAsync());
        Assert.Empty(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.password_reset").ToListAsync());
    }

    [Fact]
    public async Task AcceptInvitation_TokenForTenantA_WithTenantBWorkspace_HasZeroPersistedEffects()
    {
        var seed = await SeedRecoveryUserAsync(includeInvitation: true);

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(actionDb).AcceptInvitationAsync(
                new AcceptInvitationRequest(seed.RawCredential, "DifferentPassword1!", seed.SlugB),
                Context,
                CancellationToken.None));
        }

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
        var link = await verify.EmployeeUserAccounts.AsNoTracking().SingleAsync(x => x.Id == seed.CredentialId);
        var refresh = await verify.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == seed.RefreshTokenId);
        Assert.Equal(seed.PasswordHash, user.PasswordHash);
        Assert.False(user.IsEmailConfirmed);
        Assert.Equal(seed.UserUpdatedAtUtc, user.UpdatedAtUtc);
        Assert.Equal(Hash(seed.RawCredential), link.InvitationTokenHash);
        Assert.Equal("Invited", link.Status);
        Assert.True(link.RequiresPasswordSetup);
        Assert.Null(link.InvitationAcceptedAtUtc);
        Assert.Null(refresh.RevokedAtUtc);
        Assert.Empty(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.invitation_accepted").ToListAsync());
    }

    [Fact]
    public async Task ForgotPassword_InactiveTenant_HasZeroPersistedEffects()
    {
        var seed = await SeedDuplicateEmailUsersAsync(insertTenantBFirst: false);
        await using (var seedDb = _fixture.CreateRetryingDb())
        {
            var tenant = await seedDb.Tenants.SingleAsync(x => x.Id == seed.TenantAId);
            tenant.IsActive = false;
            await seedDb.SaveChangesAsync();
        }
        var email = new CapturingEmailService();

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await BuildService(actionDb, email).ForgotPasswordAsync(
                new ForgotPasswordRequest(seed.Email, seed.SlugA),
                Context,
                CancellationToken.None);
        }

        await using var verify = _fixture.CreateRetryingDb();
        Assert.Empty(await verify.PasswordResetTokens.AsNoTracking().Where(x => x.UserId == seed.UserAId).ToListAsync());
        Assert.Empty(await verify.LoginActivities.AsNoTracking().Where(x => x.UserId == seed.UserAId).ToListAsync());
        Assert.Empty(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.password_reset_requested").ToListAsync());
        Assert.Empty(email.Messages);
    }

    [Fact]
    public async Task Login_CrossTenantRoleLink_FailsClosedWithoutSession()
    {
        var seed = await SeedSimpleAuthUserAsync();
        await using (var corruptDb = _fixture.CreateRetryingDb())
        {
            var role = new Role
            {
                TenantId = seed.TenantBId,
                Name = "Foreign Admin",
                NormalizedName = $"FOREIGN-{Guid.NewGuid():N}",
                Description = "Cross-tenant adversarial fixture"
            };
            corruptDb.Roles.Add(role);
            corruptDb.UserRoles.Add(new UserRole { UserId = seed.UserId, Role = role });
            await corruptDb.SaveChangesAsync();
        }

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(actionDb).LoginAsync(
                new LoginRequest(seed.Email, seed.Password, seed.SlugA),
                Context,
                CancellationToken.None));
        }

        await using var verify = _fixture.CreateRetryingDb();
        Assert.Empty(await verify.RefreshTokens.AsNoTracking().Where(x => x.UserId == seed.UserId).ToListAsync());
        Assert.Empty(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.login").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgotPassword_InvalidEmployeeRelation_FailsClosedWithoutCredential(bool crossTenantEmployee)
    {
        var seed = await SeedSimpleAuthUserAsync();
        await using (var corruptDb = _fixture.CreateRetryingDb())
        {
            var employeeId = int.MaxValue;
            if (crossTenantEmployee)
            {
                var employee = new Employee
                {
                    TenantId = seed.TenantBId,
                    EmployeeCode = $"FOREIGN-{Guid.NewGuid():N}",
                    FullName = "Foreign Employee",
                    Status = "Active",
                    JoiningDate = DateTime.UtcNow.AddYears(-1)
                };
                corruptDb.Employees.Add(employee);
                await corruptDb.SaveChangesAsync();
                employeeId = employee.Id;
            }
            corruptDb.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = seed.TenantAId,
                UserId = seed.UserId,
                EmployeeId = employeeId,
                AccessMode = AccessModes.EssOnly,
                Status = "Active",
                RequiresPasswordSetup = false
            });
            await corruptDb.SaveChangesAsync();
        }
        var email = new CapturingEmailService();

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await BuildService(actionDb, email).ForgotPasswordAsync(
                new ForgotPasswordRequest(seed.Email, seed.SlugA),
                Context,
                CancellationToken.None);
        }

        await using var verify = _fixture.CreateRetryingDb();
        Assert.Empty(await verify.PasswordResetTokens.AsNoTracking().Where(x => x.UserId == seed.UserId).ToListAsync());
        Assert.Empty(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.password_reset_requested").ToListAsync());
        Assert.Empty(email.Messages);
    }

    [Fact]
    public async Task Refresh_CrossTenantPermissionOverride_FailsClosedWithoutRotation()
    {
        var seed = await SeedSimpleAuthUserAsync();
        string refreshToken;
        await using (var loginDb = _fixture.CreateRetryingDb())
        {
            var login = await BuildService(loginDb).LoginAsync(
                new LoginRequest(seed.Email, seed.Password, seed.SlugA),
                Context,
                CancellationToken.None);
            refreshToken = login.Tokens!.RefreshToken;
        }
        await using (var corruptDb = _fixture.CreateRetryingDb())
        {
            corruptDb.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                TenantId = seed.TenantBId,
                UserId = seed.UserId,
                PermissionKey = "employees.read",
                Effect = "Allow",
                IsActive = true
            });
            await corruptDb.SaveChangesAsync();
        }

        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(actionDb).RefreshAsync(
                new RefreshTokenRequest(refreshToken),
                Context,
                CancellationToken.None));
        }

        await using var verify = _fixture.CreateRetryingDb();
        var stored = Assert.Single(await verify.RefreshTokens.AsNoTracking().Where(x => x.UserId == seed.UserId).ToListAsync());
        Assert.Null(stored.RevokedAtUtc);
        Assert.Empty(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.refresh").ToListAsync());
    }

    [Fact]
    public async Task Refresh_ValidEmployeeAndPermissionGraph_PreservesLoginClaims()
    {
        var seed = await SeedSimpleAuthUserAsync();
        await using (var graphDb = _fixture.CreateRetryingDb())
        {
            var employee = new Employee
            {
                TenantId = seed.TenantAId,
                EmployeeCode = $"VALID-{Guid.NewGuid():N}",
                FullName = "Valid Employee",
                Status = "Active",
                JoiningDate = DateTime.UtcNow.AddYears(-1)
            };
            graphDb.Employees.Add(employee);
            await graphDb.SaveChangesAsync();
            graphDb.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = seed.TenantAId,
                UserId = seed.UserId,
                EmployeeId = employee.Id,
                AccessMode = AccessModes.EssOnly,
                Status = "Active",
                RequiresPasswordSetup = false
            });
            graphDb.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                TenantId = seed.TenantAId,
                UserId = seed.UserId,
                PermissionKey = "employees.read",
                Effect = "Allow",
                IsActive = true
            });
            await graphDb.SaveChangesAsync();
        }

        AuthResponse login;
        await using (var loginDb = _fixture.CreateRetryingDb())
        {
            login = (await BuildService(loginDb).LoginAsync(
                new LoginRequest(seed.Email, seed.Password, seed.SlugA),
                Context,
                CancellationToken.None)).Tokens!;
        }
        AuthResponse refreshed;
        await using (var refreshDb = _fixture.CreateRetryingDb())
        {
            refreshed = await BuildService(refreshDb).RefreshAsync(
                new RefreshTokenRequest(login.RefreshToken),
                Context,
                CancellationToken.None);
        }

        Assert.Equal(login.User.EmployeeId, refreshed.User.EmployeeId);
        Assert.Equal(AccessModes.EssOnly, refreshed.User.AccessMode);
        Assert.Equal(login.User.Permissions.Order(), refreshed.User.Permissions.Order());
        Assert.Contains("employees.read", refreshed.User.Permissions);
    }

    [Fact]
    public async Task Refresh_ReplayedAncestorWithCorruptGraph_StillRevokesFamily()
    {
        var seed = await SeedSimpleAuthUserAsync();
        string ancestor;
        await using (var loginDb = _fixture.CreateRetryingDb())
        {
            var login = await BuildService(loginDb).LoginAsync(
                new LoginRequest(seed.Email, seed.Password, seed.SlugA),
                Context,
                CancellationToken.None);
            ancestor = login.Tokens!.RefreshToken;
            await BuildService(loginDb).RefreshAsync(
                new RefreshTokenRequest(ancestor),
                Context,
                CancellationToken.None);
        }
        await using (var corruptDb = _fixture.CreateRetryingDb())
        {
            corruptDb.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                TenantId = seed.TenantBId,
                UserId = seed.UserId,
                PermissionKey = "employees.read",
                Effect = "Allow",
                IsActive = true
            });
            await corruptDb.SaveChangesAsync();
        }

        await using (var replayDb = _fixture.CreateRetryingDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(replayDb).RefreshAsync(
                new RefreshTokenRequest(ancestor),
                Context,
                CancellationToken.None));
        }

        await using var verify = _fixture.CreateRetryingDb();
        var family = await verify.RefreshTokens.AsNoTracking().Where(x => x.UserId == seed.UserId).ToListAsync();
        Assert.Equal(2, family.Count);
        Assert.DoesNotContain(family, x => x.RevokedAtUtc is null);
        Assert.Single(await verify.AuditLogs.AsNoTracking().Where(x =>
            x.TenantId == seed.TenantAId && x.Action == "auth.refresh_reuse_detected").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Me_InvalidCompanyRelation_FailsClosed(bool crossTenantCompany)
    {
        var seed = await SeedSimpleAuthUserAsync();
        await using (var corruptDb = _fixture.CreateRetryingDb())
        {
            var company = new Company
            {
                TenantId = crossTenantCompany ? seed.TenantBId : seed.TenantAId,
                LegalNameEn = $"Adversarial Company {Guid.NewGuid():N}",
                IsActive = crossTenantCompany
            };
            corruptDb.Companies.Add(company);
            await corruptDb.SaveChangesAsync();
            corruptDb.UserEntityAccesses.Add(new UserEntityAccess
            {
                TenantId = seed.TenantAId,
                UserId = seed.UserId,
                CompanyId = company.Id,
                GrantMode = EntityGrantModes.SelectedCompanies,
                Role = "Employee",
                IsActive = true
            });
            await corruptDb.SaveChangesAsync();
        }

        await using var actionDb = _fixture.CreateRetryingDb();
        Assert.Null(await BuildService(actionDb).GetCurrentUserAsync(seed.UserId, CancellationToken.None));
        Assert.Empty(await actionDb.RefreshTokens.AsNoTracking().Where(x => x.UserId == seed.UserId).ToListAsync());
    }

    [Fact]
    public async Task SupportEnd_ConcurrentPostgresCalls_Return200AndWriteOneAudit()
    {
        var session = new PlatformSupportSession
        {
            TenantId = Guid.NewGuid(),
            TargetUserId = Guid.NewGuid(),
            TargetUserEmail = $"concurrent-{Guid.NewGuid():N}@example.test",
            Reason = "concurrent containment proof",
            StartedByEmail = "support@example.test",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            TokenHash = $"contained-{Guid.NewGuid():N}"
        };
        await using (var seedDb = _fixture.CreateRetryingDb())
        {
            seedDb.PlatformSupportSessions.Add(session);
            await seedDb.SaveChangesAsync();
        }

        // Hold the target row while both real controller calls reach their conditional UPDATE.
        // pg_stat_activity confirms both are waiting on the lock before it is released, making
        // this a deterministic collision rather than two merely adjacent calls.
        await using var lockConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT 1 FROM platform_support_sessions WHERE id = @id FOR UPDATE",
            lockConnection,
            lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("id", session.Id);
            await lockCommand.ExecuteScalarAsync();
        }

        await using var firstDb = _fixture.CreateRetryingDb();
        await using var secondDb = _fixture.CreateRetryingDb();
        var firstCall = CreateController(firstDb).EndSupportAccess(
            new EndSupportAccessRequest(session.Id.ToString()), CancellationToken.None);
        var secondCall = CreateController(secondDb).EndSupportAccess(
            new EndSupportAccessRequest(session.Id.ToString()), CancellationToken.None);

        await using var observer = new NpgsqlConnection(_fixture.ConnectionString);
        await observer.OpenAsync();
        var waiting = 0L;
        for (var attempt = 0; attempt < 100 && waiting < 2; attempt++)
        {
            await using var waitCommand = new NpgsqlCommand("""
                SELECT count(*)
                FROM pg_stat_activity
                WHERE pid <> pg_backend_pid()
                  AND datname = current_database()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%platform_support_sessions%'
                """, observer);
            waiting = (long)(await waitCommand.ExecuteScalarAsync() ?? 0L);
            if (waiting < 2) await Task.Delay(25);
        }
        await lockTransaction.CommitAsync();
        var results = await Task.WhenAll(firstCall, secondCall);
        Assert.Equal(2L, waiting);
        Assert.All(results, result => Assert.IsType<OkObjectResult>(result));

        await using var verify = _fixture.CreateRetryingDb();
        Assert.NotNull((await verify.PlatformSupportSessions.AsNoTracking()
            .SingleAsync(x => x.Id == session.Id)).EndedAtUtc);
        Assert.Single(await verify.AdminAuditLogs.AsNoTracking().Where(x =>
            x.Action == "SupportAccessEnded" && x.EntityId == session.Id.ToString()).ToListAsync());
    }

    [Fact]
    public async Task SupportEnd_TransientRetry_DiscardsAbandonedAuditAndWritesOneAudit()
    {
        var session = new PlatformSupportSession
        {
            TenantId = Guid.NewGuid(),
            TargetUserId = Guid.NewGuid(),
            TargetUserEmail = $"retry-{Guid.NewGuid():N}@example.test",
            Reason = "retry containment proof",
            StartedByEmail = "support@example.test",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            TokenHash = $"contained-{Guid.NewGuid():N}"
        };
        await using (var seedDb = _fixture.CreateRetryingDb())
        {
            seedDb.PlatformSupportSessions.Add(session);
            await seedDb.SaveChangesAsync();
        }

        var fault = new ThrowOnceWhenSupportEndAuditIsPending();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, provider => provider.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null))
            .AddInterceptors(
                Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                fault)
            .Options;
        await using (var actionDb = new ZayraDbContext(options))
        {
            var result = await CreateController(actionDb).EndSupportAccess(
                new EndSupportAccessRequest(session.Id.ToString()), CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }
        Assert.Equal(1, fault.Injections);

        await using var verify = _fixture.CreateRetryingDb();
        Assert.NotNull((await verify.PlatformSupportSessions.AsNoTracking()
            .SingleAsync(x => x.Id == session.Id)).EndedAtUtc);
        Assert.Single(await verify.AdminAuditLogs.AsNoTracking().Where(x =>
            x.Action == "SupportAccessEnded" && x.EntityId == session.Id.ToString()).ToListAsync());
    }

    private async Task<DuplicateSeed> SeedDuplicateEmailUsersAsync(bool insertTenantBFirst)
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = new Tenant { Name = "Workspace A", Slug = $"workspace-a-{suffix}" };
        var tenantB = new Tenant { Name = "Workspace B", Slug = $"workspace-b-{suffix}" };
        db.AddRange(tenantA, tenantB);
        db.SecuritySettings.AddRange(
            new SecuritySetting { TenantId = tenantA.Id },
            new SecuritySetting { TenantId = tenantB.Id });
        await db.SaveChangesAsync();

        var email = $"same-{suffix}@example.test";
        const string passwordA = "TenantAPassword1!";
        const string passwordB = "TenantBPassword1!";
        var hasher = new Pbkdf2PasswordHasher();
        var userA = MakeUser(tenantA, email, "Workspace A User", hasher.Hash(passwordA));
        var userB = MakeUser(tenantB, email, "Workspace B User", hasher.Hash(passwordB));
        foreach (var user in insertTenantBFirst ? new[] { userB, userA } : new[] { userA, userB })
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        return new DuplicateSeed(
            tenantA.Id, tenantB.Id, userA.Id, userB.Id,
            tenantA.Slug, tenantB.Slug, email, passwordA, userB.PasswordHash, userB.UpdatedAtUtc);
    }

    private async Task<SimpleSeed> SeedSimpleAuthUserAsync()
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = new Tenant { Name = "Graph A", Slug = $"graph-a-{suffix}" };
        var tenantB = new Tenant { Name = "Graph B", Slug = $"graph-b-{suffix}" };
        db.AddRange(tenantA, tenantB);
        db.SecuritySettings.Add(new SecuritySetting { TenantId = tenantA.Id });
        await db.SaveChangesAsync();
        const string password = "GraphPassword1!";
        var user = MakeUser(
            tenantA,
            $"graph-{suffix}@example.test",
            "Graph User",
            new Pbkdf2PasswordHasher().Hash(password));
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new SimpleSeed(tenantA.Id, tenantB.Id, user.Id, tenantA.Slug, user.Email, password);
    }

    private async Task<RecoverySeed> SeedRecoveryUserAsync(bool includeInvitation)
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = new Tenant { Name = "Recovery A", Slug = $"recovery-a-{suffix}" };
        var tenantB = new Tenant { Name = "Recovery B", Slug = $"recovery-b-{suffix}" };
        db.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();
        var hasher = new Pbkdf2PasswordHasher();
        var user = MakeUser(tenantA, $"recovery-{suffix}@example.test", "Recovery User", hasher.Hash("OriginalPassword1!"));
        user.IsEmailConfirmed = false;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        const string rawCredential = "tenant-a-one-use-credential";
        Guid credentialId;
        if (includeInvitation)
        {
            var employee = new Employee
            {
                TenantId = tenantA.Id,
                EmployeeCode = $"AUTH-{suffix}",
                FullName = user.FullName,
                Status = "Active",
                JoiningDate = DateTime.UtcNow.AddYears(-1)
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            var link = new EmployeeUserAccount
            {
                TenantId = tenantA.Id,
                EmployeeId = employee.Id,
                UserId = user.Id,
                AccessMode = AccessModes.EssOnly,
                Status = "Invited",
                RequiresPasswordSetup = true,
                InvitationTokenHash = Hash(rawCredential),
                InvitationExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
            db.EmployeeUserAccounts.Add(link);
            credentialId = link.Id;
        }
        else
        {
            var token = new PasswordResetToken
            {
                UserId = user.Id,
                TokenHash = Hash(rawCredential),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
            db.PasswordResetTokens.Add(token);
            credentialId = token.Id;
        }

        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash($"refresh-{suffix}"),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        };
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync();
        return new RecoverySeed(
            tenantA.Id, user.Id, tenantA.Slug, tenantB.Slug,
            user.PasswordHash, user.UpdatedAtUtc, rawCredential, credentialId, refresh.Id);
    }

    private static User MakeUser(Tenant tenant, string email, string name, string passwordHash) => new()
    {
        TenantId = tenant.Id,
        Tenant = tenant,
        Email = email,
        NormalizedEmail = AuthService.Normalize(email),
        FullName = name,
        PasswordHash = passwordHash
    };

    private static AuthService BuildService(ZayraDbContext db, IEmailService? email = null)
    {
        var jwt = Options.Create(new JwtOptions
        {
            Issuer = "Zayra.Tests",
            TenantAudience = "kynexone-tenant-test",
            PlatformAudience = "kynexone-platform-test",
            SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_AUTH_TESTS",
            AccessTokenMinutes = 30,
            RefreshTokenDays = 7
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APP_URL"] = "https://app.example.test"
            })
            .Build();
        return new AuthService(
            db,
            new Pbkdf2PasswordHasher(),
            new JwtTokenService(jwt),
            new AuditService(db),
            email ?? new CapturingEmailService(configured: false),
            jwt,
            new NullPostgresMfaService(),
            NullLogger<AuthService>.Instance,
            configuration);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record DuplicateSeed(
        Guid TenantAId,
        Guid TenantBId,
        Guid UserAId,
        Guid UserBId,
        string SlugA,
        string SlugB,
        string Email,
        string PasswordA,
        string UserBPasswordHash,
        DateTime? UserBUpdatedAtUtc);

    private sealed record RecoverySeed(
        Guid TenantAId,
        Guid UserId,
        string SlugA,
        string SlugB,
        string PasswordHash,
        DateTime? UserUpdatedAtUtc,
        string RawCredential,
        Guid CredentialId,
        Guid RefreshTokenId);

    private sealed record SimpleSeed(
        Guid TenantAId,
        Guid TenantBId,
        Guid UserId,
        string SlugA,
        string Email,
        string Password);

    private sealed class CapturingEmailService : IEmailService
    {
        private readonly bool _configured;
        public CapturingEmailService(bool configured = true) => _configured = configured;
        public List<(string To, string Html)> Messages { get; } = [];

        public Task SendAsync(
            string toAddress,
            string toName,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add((toAddress, htmlBody));
            return Task.CompletedTask;
        }

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_configured);
    }

    private sealed class ThrowOnceWhenSupportEndAuditIsPending : SaveChangesInterceptor
    {
        private int _remaining = 1;
        public int Injections { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _remaining, 0) == 1
                && eventData.Context!.ChangeTracker.Entries<AdminAuditLog>().Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.Action == "SupportAccessEnded"))
            {
                Injections++;
                throw new TimeoutException("Injected transient failure before the support-end audit save.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class NullPostgresMfaService : IMfaService
    {
        public Task<MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => throw new NotImplementedException();
        public Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string token, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> VerifyEnrollmentSetupAsync(string token, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> VerifyChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DisableAsync(Guid userId, Guid tenantId, string code, CancellationToken ct) => throw new NotImplementedException();
        public Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> VerifyPlatformSetupAsync(Guid id, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreatePlatformChallengeAsync(Guid id, string ip, CancellationToken ct) => throw new NotImplementedException();
        public Task<PlatformUser?> VerifyPlatformChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DisablePlatformAsync(Guid id, string code, CancellationToken ct) => throw new NotImplementedException();
    }
}
