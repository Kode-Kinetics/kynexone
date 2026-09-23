using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Supplemental M1-G2 evidence for the complete employee-invitation lifecycle.
///
/// These tests intentionally exercise production services against PostgreSQL configured with the
/// production retrying execution strategy. The race proof uses five independently connected
/// DbContexts, an async start barrier, and 20 fresh invitation cycles. It contains no sleeps and no
/// retries-to-green.
///
/// Evidence covered here:
/// - issuance creates only a dormant, setup-pending identity with exact least privilege;
/// - reissue removes stale privilege and invalidates every older bearer credential;
/// - acceptance returns exactly HTTP 204 and creates no authenticated session artifact;
/// - one invitation has exactly one winner under five-client PostgreSQL contention;
/// - an audit-write failure rolls the complete issuance unit back;
/// - a simulated post-commit timeout is reconciled by the stable audit marker without duplication.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class InvitationLifecycleSecurityPostgresTests
{
    private const int AcceptanceCycles = 20;
    private const int ConcurrentClients = 5;
    private const string IssuanceAuditFailure = "INVITATION_ISSUANCE_AUDIT_FAILURE";
    private const string UnknownCommitMarker = "INVITATION_POST_COMMIT_TIMEOUT";

    private static readonly RequestContext Request = new(
        "203.0.113.126",
        "m1-g2-invitation-lifecycle",
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Invitation.Lifecycle.Tests",
        TenantAudience = "kynexone-tenant-invitation-tests",
        PlatformAudience = "kynexone-platform-invitation-tests",
        SigningKey = "INVITATION_LIFECYCLE_TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_2026",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    });

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APP_URL"] = "https://app.example.test"
        })
        .Build();

    private readonly PostgresFixture _fixture;

    public InvitationLifecycleSecurityPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Issuance_NewIdentity_IsDormantSetupPendingAndExactlyScoped()
    {
        var seed = await SeedEmployeeAsync(existingStagedIdentity: false);

        EmployeeLoginInvitationDto invitation;
        await using (var issueDb = _fixture.CreateRetryingDb())
        {
            invitation = await BuildAccessService(issueDb).InviteEmployeeLoginAsync(
                seed.TenantId,
                new InviteEmployeeLoginRequest(
                    seed.EmployeeId,
                    seed.Email,
                    AccessModes.EssOnly,
                    new[] { "Employee" },
                    InvitationHours: 24),
                EntityScopeContext.GroupLevel,
                Request with { TenantId = seed.TenantId },
                CancellationToken.None);
        }

        Assert.False(string.IsNullOrWhiteSpace(invitation.InvitationToken));
        Assert.Equal("Invited", invitation.Status);
        Assert.Equal(AccessModes.EssOnly, invitation.AccessMode);
        Assert.Contains("/accept-invitation", invitation.InvitationUrl, StringComparison.Ordinal);
        Assert.Contains("#token=", invitation.InvitationUrl, StringComparison.Ordinal);

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == invitation.UserId);
        var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.TenantId == seed.TenantId && x.EmployeeId == seed.EmployeeId);
        var employee = await verify.Employees.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.TenantId == seed.TenantId && x.Id == seed.EmployeeId);
        var roleNames = await verify.UserRoles.AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .Join(verify.Roles.AsNoTracking(), userRole => userRole.RoleId, role => role.Id,
                (_, role) => role.NormalizedName)
            .ToListAsync();
        var grants = await verify.UserEntityAccesses.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.UserId == user.Id)
            .ToListAsync();

        Assert.False(user.IsActive);
        Assert.False(user.IsEmailConfirmed);
        Assert.False(user.MustChangePassword);
        Assert.False(user.IsGroupScope);
        Assert.Equal("Invited", user.Status);
        Assert.Equal(AccessModes.NoLogin, user.AccessMode);
        Assert.Equal(user.Id, employee.UserAccountId);
        Assert.Equal(user.Id, link.UserId);
        Assert.Equal("Invited", link.Status);
        Assert.True(link.RequiresPasswordSetup);
        Assert.Equal(AccessModes.EssOnly, link.AccessMode);
        Assert.Equal(Hash(invitation.InvitationToken), link.InvitationTokenHash);
        Assert.Equal(new[] { "EMPLOYEE" }, roleNames);
        Assert.Empty(await verify.UserPermissionOverrides.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.UserId == user.Id).ToListAsync());
        var grant = Assert.Single(grants);
        Assert.Equal(seed.CompanyId, grant.CompanyId);
        Assert.Equal(EntityGrantModes.SelectedCompanies, grant.GrantMode);
        Assert.True(grant.IsActive);
        Assert.Empty(await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == user.Id).ToListAsync());
        Assert.Empty(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.UserId == user.Id).ToListAsync());
        Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId
                && x.Action == "access.employee_invited"
                && x.EntityId == link.Id.ToString()).ToListAsync());
    }

    [Fact]
    public async Task Reissue_StagedIdentity_RemovesPrivilegeInvalidatesCredentialsAndRejectsOldInvitation()
    {
        var seed = await SeedEmployeeAsync(existingStagedIdentity: true);

        EmployeeLoginInvitationDto reissued;
        await using (var issueDb = _fixture.CreateRetryingDb())
        {
            reissued = await BuildAccessService(issueDb).InviteEmployeeLoginAsync(
                seed.TenantId,
                new InviteEmployeeLoginRequest(
                    seed.EmployeeId,
                    seed.Email,
                    AccessModes.EssOnly,
                    new[] { "Employee" },
                    InvitationHours: 24),
                EntityScopeContext.GroupLevel,
                Request with { TenantId = seed.TenantId },
                CancellationToken.None);
        }

        Assert.NotNull(seed.UserId);
        Assert.NotEqual(seed.OldInvitation, reissued.InvitationToken);

        await using (var rejectDb = _fixture.CreateRetryingDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildAuthService(rejectDb)
                .AcceptInvitationAsync(
                    new AcceptInvitationRequest(seed.OldInvitation!, "RejectedOldPassword1!", seed.TenantSlug),
                    Request,
                    CancellationToken.None));
        }

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == seed.UserId!.Value);
        var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.TenantId == seed.TenantId && x.EmployeeId == seed.EmployeeId);
        var roleNames = await verify.UserRoles.AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .Join(verify.Roles.AsNoTracking(), userRole => userRole.RoleId, role => role.Id,
                (_, role) => role.NormalizedName)
            .ToListAsync();
        var grants = await verify.UserEntityAccesses.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.UserId == user.Id)
            .ToListAsync();

        Assert.False(user.IsActive);
        Assert.False(user.IsEmailConfirmed);
        Assert.False(user.IsGroupScope);
        Assert.Equal("Invited", user.Status);
        Assert.Equal(AccessModes.NoLogin, user.AccessMode);
        Assert.Equal(Hash(reissued.InvitationToken), link.InvitationTokenHash);
        Assert.NotEqual(Hash(seed.OldInvitation!), link.InvitationTokenHash);
        Assert.Equal(new[] { "EMPLOYEE" }, roleNames);
        Assert.Empty(await verify.UserPermissionOverrides.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.UserId == user.Id).ToListAsync());
        var grant = Assert.Single(grants);
        Assert.Equal(seed.CompanyId, grant.CompanyId);
        Assert.Equal(EntityGrantModes.SelectedCompanies, grant.GrantMode);

        var refresh = await verify.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == seed.RefreshId);
        var reset = await verify.PasswordResetTokens.AsNoTracking().SingleAsync(x => x.Id == seed.ResetId);
        var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == seed.ChallengeId);
        Assert.NotNull(refresh.RevokedAtUtc);
        Assert.NotNull(reset.UsedAtUtc);
        Assert.NotNull(challenge.UsedAtUtc);
        Assert.Empty(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.Action == "auth.invitation_accepted")
            .ToListAsync());
    }

    [Fact]
    public async Task Acceptance_ControllerReturnsExact204_AndCreatesNoSessionArtifacts()
    {
        var seed = await SeedEmployeeAsync(existingStagedIdentity: false);
        var invitation = await IssueAsync(seed);

        IActionResult response;
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.126");
        httpContext.Request.Headers.UserAgent = "m1-g2-invitation-http";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        await using (var acceptDb = _fixture.CreateDbWithAccessor(accessor))
        {
            var controller = new AuthController(BuildAuthService(acceptDb))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };

            response = await controller.AcceptInvitation(
                new AcceptInvitationRequest(
                    invitation.InvitationToken,
                    "AcceptedPassword1!",
                    seed.TenantSlug),
                CancellationToken.None);
        }

        var noContent = Assert.IsType<NoContentResult>(response);
        Assert.Equal(StatusCodes.Status204NoContent, noContent.StatusCode);

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == invitation.UserId);
        var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.TenantId == seed.TenantId && x.EmployeeId == seed.EmployeeId);
        Assert.True(user.IsActive);
        Assert.True(user.IsEmailConfirmed);
        Assert.Equal("Active", user.Status);
        Assert.Equal(AccessModes.EssOnly, user.AccessMode);
        Assert.Equal(string.Empty, link.InvitationTokenHash);
        Assert.False(link.RequiresPasswordSetup);
        Assert.NotNull(link.InvitationAcceptedAtUtc);
        Assert.Empty(await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == user.Id).ToListAsync());
        Assert.Empty(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.UserId == user.Id).ToListAsync());
        Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId
                && x.Action == "auth.invitation_accepted"
                && x.EntityId == user.Id.ToString()).ToListAsync());
    }

    [Fact]
    public async Task Acceptance_SameInvitationFiveClients_HasExactlyOneWinner_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedEmployeeAsync(existingStagedIdentity: false, cycle);
            var invitation = await IssueAsync(seed);
            var password = $"RacePassword-{cycle}-{Guid.NewGuid():N}!Aa1";

            var outcomes = await RaceFiveAsync(async db =>
            {
                await BuildAuthService(db).AcceptInvitationAsync(
                    new AcceptInvitationRequest(invitation.InvitationToken, password, seed.TenantSlug),
                    Request,
                    CancellationToken.None);
                return true;
            });

            Assert.Single(outcomes, x => x.Succeeded);
            Assert.Equal(ConcurrentClients - 1,
                outcomes.Count(x => x.Error is UnauthorizedAccessException));
            Assert.DoesNotContain(outcomes,
                x => !x.Succeeded && x.Error is not UnauthorizedAccessException);

            await using var verify = _fixture.CreateRetryingDb();
            var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == invitation.UserId);
            var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.TenantId == seed.TenantId && x.EmployeeId == seed.EmployeeId);
            Assert.True(new Pbkdf2PasswordHasher().Verify(password, user.PasswordHash));
            Assert.True(user.IsActive);
            Assert.Equal(string.Empty, link.InvitationTokenHash);
            Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId
                    && x.Action == "auth.invitation_accepted"
                    && x.EntityId == user.Id.ToString()).ToListAsync());
            Assert.Empty(await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == user.Id).ToListAsync());
            Assert.Empty(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == user.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task Issuance_InjectedAuditFailure_RollsBackIdentityLinkPrivilegeAndEmployeePointer()
    {
        var seed = await SeedEmployeeAsync(existingStagedIdentity: false);
        await InstallIssuanceAuditFailureTriggerAsync(seed.TenantId);
        Exception? failure;
        try
        {
            await using var issueDb = _fixture.CreateRetryingDb();
            failure = await Record.ExceptionAsync(() => BuildAccessService(issueDb)
                .InviteEmployeeLoginAsync(
                    seed.TenantId,
                    new InviteEmployeeLoginRequest(
                        seed.EmployeeId,
                        seed.Email,
                        AccessModes.EssOnly,
                        new[] { "Employee" },
                        InvitationHours: 24),
                    EntityScopeContext.GroupLevel,
                    Request with { TenantId = seed.TenantId },
                    CancellationToken.None));
        }
        finally
        {
            await DropIssuanceAuditFailureTriggerAsync();
        }

        Assert.NotNull(failure);
        Assert.Contains(IssuanceAuditFailure, failure!.ToString(), StringComparison.Ordinal);

        await using var verify = _fixture.CreateRetryingDb();
        Assert.Empty(await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.NormalizedEmail == AuthService.Normalize(seed.Email))
            .ToListAsync());
        Assert.Empty(await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.EmployeeId == seed.EmployeeId)
            .ToListAsync());
        var employee = await verify.Employees.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.TenantId == seed.TenantId && x.Id == seed.EmployeeId);
        Assert.Null(employee.UserAccountId);
        Assert.Empty(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.Action == "access.employee_invited")
            .ToListAsync());
    }

    [Fact]
    public async Task Issuance_PostCommitTimeout_UsesStableMarkerAndDoesNotDuplicateEffects()
    {
        var seed = await SeedEmployeeAsync(existingStagedIdentity: false);
        var fault = new ThrowOnceAfterCommitInterceptor();

        EmployeeLoginInvitationDto invitation;
        await using (var issueDb = CreateUnknownCommitDb(fault))
        {
            invitation = await BuildAccessService(issueDb).InviteEmployeeLoginAsync(
                seed.TenantId,
                new InviteEmployeeLoginRequest(
                    seed.EmployeeId,
                    seed.Email,
                    AccessModes.EssOnly,
                    new[] { "Employee" },
                    InvitationHours: 24),
                EntityScopeContext.GroupLevel,
                Request with { TenantId = seed.TenantId },
                CancellationToken.None);
        }

        Assert.Equal(1, fault.InjectedFaults);
        Assert.False(string.IsNullOrWhiteSpace(invitation.InvitationToken));

        await using var verify = _fixture.CreateRetryingDb();
        var users = await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.NormalizedEmail == AuthService.Normalize(seed.Email))
            .ToListAsync();
        var user = Assert.Single(users);
        var link = Assert.Single(await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.EmployeeId == seed.EmployeeId)
            .ToListAsync());
        Assert.Equal(user.Id, invitation.UserId);
        Assert.Equal(Hash(invitation.InvitationToken), link.InvitationTokenHash);
        Assert.Single(await verify.UserRoles.AsNoTracking()
            .Where(x => x.UserId == user.Id).ToListAsync());
        Assert.Single(await verify.UserEntityAccesses.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.UserId == user.Id).ToListAsync());
        Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId
                && x.Action == "access.employee_invited"
                && x.EntityId == link.Id.ToString()).ToListAsync());
    }

    private async Task<EmployeeLoginInvitationDto> IssueAsync(InvitationSeed seed)
    {
        await using var db = _fixture.CreateRetryingDb();
        return await BuildAccessService(db).InviteEmployeeLoginAsync(
            seed.TenantId,
            new InviteEmployeeLoginRequest(
                seed.EmployeeId,
                seed.Email,
                AccessModes.EssOnly,
                new[] { "Employee" },
                InvitationHours: 24),
            EntityScopeContext.GroupLevel,
            Request with { TenantId = seed.TenantId },
            CancellationToken.None);
    }

    private async Task<InvitationSeed> SeedEmployeeAsync(
        bool existingStagedIdentity,
        int? cycle = null)
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = $"{cycle?.ToString() ?? "single"}-{Guid.NewGuid():N}";
        var tenant = new Tenant
        {
            Name = $"Invitation Lifecycle {suffix}",
            Slug = $"invitation-lifecycle-{suffix}"
        };
        var company = new Company
        {
            TenantId = tenant.Id,
            LegalNameEn = $"Invitation Company {suffix}",
            TradeName = "Invitation Test",
            CountryCode = "AE",
            Jurisdiction = "AE"
        };
        var employeeRole = new Role
        {
            TenantId = tenant.Id,
            Name = "Employee",
            NormalizedName = "EMPLOYEE",
            Description = "Employee"
        };
        var adminRole = new Role
        {
            TenantId = tenant.Id,
            Name = "Admin",
            NormalizedName = "ADMIN",
            Description = "Administrator",
            AuthorityLevel = 1
        };
        db.AddRange(tenant, company, employeeRole, adminRole);
        await db.SaveChangesAsync();

        var email = $"invitation-{suffix}@example.test";
        var employee = new Employee
        {
            TenantId = tenant.Id,
            CompanyId = company.Id,
            EmployeeCode = $"INV-{Guid.NewGuid():N}",
            FullName = "Invitation Lifecycle Employee",
            WorkEmail = email,
            Status = EmployeeStatuses.Invited,
            JoiningDate = DateTime.UtcNow.AddDays(-1)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        if (!existingStagedIdentity)
        {
            return new InvitationSeed(
                tenant.Id,
                tenant.Slug,
                company.Id,
                employee.Id,
                email,
                null,
                null,
                Guid.Empty,
                Guid.Empty,
                Guid.Empty);
        }

        var oldInvitation = $"old-invitation-{Guid.NewGuid():N}-{Guid.NewGuid():N}";
        var user = new User
        {
            TenantId = tenant.Id,
            Email = email,
            NormalizedEmail = AuthService.Normalize(email),
            FullName = employee.FullName,
            PasswordHash = new Pbkdf2PasswordHasher().Hash("UnreachableOldPassword1!"),
            Status = "Invited",
            AccessMode = AccessModes.NoLogin,
            IsActive = false,
            IsEmailConfirmed = false,
            IsGroupScope = true
        };
        var link = new EmployeeUserAccount
        {
            TenantId = tenant.Id,
            EmployeeId = employee.Id,
            UserId = user.Id,
            AccessMode = AccessModes.EssOnly,
            Status = "Invited",
            RequiresPasswordSetup = true,
            InvitationTokenHash = Hash(oldInvitation),
            InvitationExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            InvitedAtUtc = DateTime.UtcNow
        };
        var oldGroupGrant = new UserEntityAccess
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            CompanyId = null,
            GrantMode = EntityGrantModes.AllCurrentAndFutureCompanies,
            Role = "Admin",
            IsActive = true
        };
        var oldOverride = new UserPermissionOverride
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            PermissionKey = "access.users.manage",
            Effect = "Allow",
            Reason = "stale privilege that reissue must remove"
        };
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash($"old-refresh-{suffix}"),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
            CreatedByIp = "203.0.113.126"
        };
        var reset = new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = Hash($"old-reset-{suffix}"),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            CreatedByIp = "203.0.113.126"
        };
        var challenge = new MfaChallengeToken
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            TokenHash = Hash($"old-challenge-{suffix}"),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            CreatedByIp = "203.0.113.126"
        };
        employee.UserAccountId = user.Id;
        db.AddRange(
            user,
            new UserRole { UserId = user.Id, RoleId = adminRole.Id },
            link,
            oldGroupGrant,
            oldOverride,
            refresh,
            reset,
            challenge);
        await db.SaveChangesAsync();

        return new InvitationSeed(
            tenant.Id,
            tenant.Slug,
            company.Id,
            employee.Id,
            email,
            user.Id,
            oldInvitation,
            refresh.Id,
            reset.Id,
            challenge.Id);
    }

    private async Task<IReadOnlyList<RaceOutcome<T>>> RaceFiveAsync<T>(
        Func<ZayraDbContext, Task<T>> operation)
    {
        var barrier = new AsyncStartBarrier(ConcurrentClients);
        var clients = Enumerable.Range(0, ConcurrentClients).Select(async _ =>
        {
            await using var db = _fixture.CreateRetryingDb();
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            await barrier.SignalAndWaitAsync();
            try
            {
                return RaceOutcome<T>.Success(await operation(db));
            }
            catch (Exception error)
            {
                return RaceOutcome<T>.Failure(error);
            }
        }).ToArray();
        return await Task.WhenAll(clients);
    }

    private ZayraDbContext CreateUnknownCommitDb(DbTransactionInterceptor interceptor) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null))
            .AddInterceptors(RowLockingInterceptor.Instance, interceptor)
            .Options);

    private static AccessManagementService BuildAccessService(ZayraDbContext db)
    {
        var tokenService = new JwtTokenService(Jwt);
        return new AccessManagementService(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            tokenService,
            Configuration);
    }

    private static AuthService BuildAuthService(ZayraDbContext db)
    {
        var tokenService = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        var totp = new TotpService(new EphemeralDataProtectionProvider());
        return new AuthService(
            db,
            new Pbkdf2PasswordHasher(),
            tokenService,
            audit,
            new NoopEmailService(),
            Jwt,
            new MfaService(db, totp, tokenService, audit),
            totp,
            NullLogger<AuthService>.Instance,
            Configuration);
    }

    private async Task InstallIssuanceAuditFailureTriggerAsync(Guid tenantId)
    {
        await using var db = _fixture.CreateRetryingDb();
        await db.Database.ExecuteSqlRawAsync(@"
DROP TRIGGER IF EXISTS trg_invitation_lifecycle_fail_issuance_audit ON audit_logs;
DROP FUNCTION IF EXISTS invitation_lifecycle_fail_issuance_audit();
CREATE FUNCTION invitation_lifecycle_fail_issuance_audit() RETURNS trigger AS $$
BEGIN
    IF NEW.action = 'access.employee_invited'
       AND NEW.tenant_id = '" + tenantId + @"' THEN
        RAISE EXCEPTION '" + IssuanceAuditFailure + @"';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER trg_invitation_lifecycle_fail_issuance_audit
    BEFORE INSERT ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION invitation_lifecycle_fail_issuance_audit();");
    }

    private async Task DropIssuanceAuditFailureTriggerAsync()
    {
        await using var db = _fixture.CreateRetryingDb();
        await db.Database.ExecuteSqlRawAsync(@"
DROP TRIGGER IF EXISTS trg_invitation_lifecycle_fail_issuance_audit ON audit_logs;
DROP FUNCTION IF EXISTS invitation_lifecycle_fail_issuance_audit();");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class AsyncStartBarrier
    {
        private readonly int _participants;
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public AsyncStartBarrier(int participants) => _participants = participants;

        public Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == _participants)
                _release.TrySetResult();
            return _release.Task;
        }
    }

    private sealed class ThrowOnceAfterCommitInterceptor : DbTransactionInterceptor
    {
        private int _armed = 1;
        private int _injectedFaults;

        public int InjectedFaults => Volatile.Read(ref _injectedFaults);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Interlocked.Increment(ref _injectedFaults);
                throw new TimeoutException(UnknownCommitMarker);
            }

            return Task.CompletedTask;
        }
    }

    private sealed record RaceOutcome<T>(bool Succeeded, T? Value, Exception? Error)
    {
        public static RaceOutcome<T> Success(T value) => new(true, value, null);
        public static RaceOutcome<T> Failure(Exception error) => new(false, default, error);
    }

    private sealed record InvitationSeed(
        Guid TenantId,
        string TenantSlug,
        Guid CompanyId,
        int EmployeeId,
        string Email,
        Guid? UserId,
        string? OldInvitation,
        Guid RefreshId,
        Guid ResetId,
        Guid ChallengeId);

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendAsync(
            string toAddress,
            string toName,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
