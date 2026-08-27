using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// THE missing integration harness.
///
/// A mutation audit proved that <c>grep -rn "WebApplicationFactory" Zayra.Api.Tests</c> returned
/// ZERO hits: every controller test hand-constructed a controller with a fabricated
/// <c>ControllerContext</c>, so <c>Program.cs</c>'s composition — <c>UseAuthentication()</c>, the JWT
/// <see cref="TokenValidationParameters"/>, the default-deny <c>FallbackPolicy</c>, the
/// <c>PlatformAdmin</c> policy, and every <c>[Authorize]</c> / <c>[HasPermission]</c> /
/// <c>[RequirePlatformRole]</c> attribute — never executed. Authentication could be DELETED
/// outright and all 1817 tests stayed green.
///
/// This factory boots the real <c>Program.cs</c> in-process over <c>TestServer</c>. It changes
/// exactly three things, none of which touch the security pipeline:
///   1. the database provider (Npgsql → a private shared-cache SQLite instance);
///   2. the application's background workers (removed — they are irrelevant here and would
///      otherwise hammer the test database);
///   3. one EXTRA controller, defined in this test assembly, carrying NO authorization attribute
///      (see <see cref="AuthorizationFallbackProbeController"/>).
/// Everything else — middleware order, JWT validation flags, both authorization policies, the
/// audience-route guard, both global action filters — is the production composition, untouched.
/// </summary>
public sealed class AuthorizationPipelineHost : WebApplicationFactory<Program>
{
    private readonly string _sqliteConnectionString;

    public AuthorizationPipelineHost(string sqliteConnectionString)
        => _sqliteConnectionString = sqliteConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs fails fast in every NON-Development environment when Jwt:SigningKey is still
        // the committed "CHANGE_ME…" placeholder (Program.cs:74-92), and DocumentStorageRegistration
        // refuses to boot outside Development without a real S3 bucket. Development is therefore the
        // only environment in which the UNMODIFIED production composition can boot from the
        // committed appsettings.json — which is precisely what we want to test.
        builder.UseEnvironment(Environments.Development);

        // ConfigureTestServices runs AFTER every builder.Services.* call in Program.cs and BEFORE the
        // container is built, so these three edits land on the finished production registration set.
        builder.ConfigureTestServices(services =>
        {
            ReplacePostgresWithSqlite(services, _sqliteConnectionString);
            RemoveApplicationBackgroundWorkers(services);
            AddFallbackPolicyProbeController(services);
        });
    }

    /// <summary>
    /// Swaps the pooled Npgsql <see cref="ZayraDbContext"/> for SQLite. AddDbContextPool registers
    /// five descriptors (options, the pool, the scoped lease, and the context itself); all of them
    /// must go or the surviving ones keep handing out Npgsql-configured contexts.
    /// </summary>
    private static void ReplacePostgresWithSqlite(IServiceCollection services, string connectionString)
    {
        var doomed = services.Where(descriptor =>
        {
            var serviceType = descriptor.ServiceType;
            if (serviceType == typeof(ZayraDbContext)
                || serviceType == typeof(DbContextOptions)
                || serviceType == typeof(DbContextOptions<ZayraDbContext>))
                return true;

            var name = serviceType.FullName ?? string.Empty;
            return name.Contains("DbContextPool", StringComparison.Ordinal)
                || name.Contains("IScopedDbContextLease", StringComparison.Ordinal)
                || name.Contains("IDbContextFactory", StringComparison.Ordinal);
        }).ToList();

        foreach (var descriptor in doomed) services.Remove(descriptor);

        services.AddDbContext<ZayraDbContext>(options => options
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId
                .PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
    }

    /// <summary>
    /// Drops only the API's OWN hosted services (Qiwa sync, report scheduler, AI insights,
    /// notification delivery, compliance reminders). The web host's own GenericWebHostService is
    /// registered from a different assembly and is deliberately left alone.
    /// </summary>
    private static void RemoveApplicationBackgroundWorkers(IServiceCollection services)
    {
        var apiAssembly = typeof(ZayraDbContext).Assembly;
        var workers = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType is not null
                        && d.ImplementationType.Assembly == apiAssembly)
            .ToList();
        foreach (var worker in workers) services.Remove(worker);
    }

    /// <summary>
    /// Registers <see cref="AuthorizationFallbackProbeController"/> — and ONLY that type — with the
    /// application's real MVC part manager, so it is routed by the production
    /// <c>app.MapControllers()</c> and traverses the production middleware pipeline.
    /// </summary>
    private static void AddFallbackPolicyProbeController(IServiceCollection services)
    {
        var partManager = services
            .LastOrDefault(d => d.ServiceType == typeof(ApplicationPartManager))
            ?.ImplementationInstance as ApplicationPartManager
            ?? throw new InvalidOperationException(
                "MVC's ApplicationPartManager was not found in the service collection, so the "
                + "FallbackPolicy probe endpoint could not be registered. Refusing to run a control "
                + "that would silently test nothing.");

        partManager.FeatureProviders.Add(new FallbackProbeControllerFeatureProvider());
    }

    private sealed class FallbackProbeControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var probe = typeof(AuthorizationFallbackProbeController).GetTypeInfo();
            if (!feature.Controllers.Contains(probe)) feature.Controllers.Add(probe);
        }
    }
}

/// <summary>
/// THE endpoint the default-deny <c>FallbackPolicy</c> (Program.cs:316-318) exists for.
///
/// PINNED BY NAME: <c>GET /api/test-harness/no-authorization-attribute</c> —
/// <see cref="AuthorizationFallbackProbeController.NoAuthorizationAttribute"/>.
///
/// It carries NO <c>[Authorize]</c>, NO <c>[AllowAnonymous]</c>, NO <c>[HasPermission]</c> and NO
/// <c>[RequirePlatformRole]</c>. Nothing but the FallbackPolicy stands between an anonymous caller
/// and its body. Delete the FallbackPolicy and this endpoint answers 200 to the whole internet.
///
/// Why it lives in the TEST assembly rather than pointing at a shipped route: a scan of all 102
/// controllers (see <c>AuthorizationPipelineTests.NoShippedEndpointRelies…</c>) shows every action
/// today carries an explicit attribute, so there is no production endpoint that isolates the
/// fallback. That is a property of today's source, not a guarantee — the fallback's entire purpose
/// is the attribute somebody FORGETS to write tomorrow. This controller is that forgotten attribute,
/// made permanent and testable, and it is routed by the production <c>MapControllers()</c> through
/// the production middleware pipeline exactly as a real one would be.
/// </summary>
[ApiController]
[Route("api/test-harness")]
public sealed class AuthorizationFallbackProbeController : ControllerBase
{
    [HttpGet("no-authorization-attribute")]
    public IActionResult NoAuthorizationAttribute()
        => Ok(new { probe = "reached", note = "only the default-deny FallbackPolicy guards this route" });
}

/// <summary>
/// Boots the host once, seeds the identities the authorization tests need, and mints the tokens.
///
/// Every token below is produced by the APPLICATION's own issuing code — <see cref="ITokenService"/>
/// for tenant tokens, and a byte-for-byte mirror of <c>PlatformController.CreatePlatformToken</c>
/// for platform tokens — so a token that is supposed to be valid really is valid all the way through
/// <c>JwtBearerEvents.OnTokenValidated</c>, which re-checks the session stamp, roles and effective
/// permissions against the database on EVERY request.
/// </summary>
public sealed class AuthorizationPipelineFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    /// <summary>The REAL JwtOptions the booted application is using.</summary>
    public JwtOptions Jwt { get; private set; } = null!;

    /// <summary>Tenant token whose effective permissions include <c>notifications.manage</c>.</summary>
    public string TenantTokenWithPermission { get; private set; } = null!;

    /// <summary>Tenant token for a real, active, non-revoked user who does NOT hold
    /// <c>notifications.manage</c> (they hold only <c>employees.read</c>).</summary>
    public string TenantTokenWithoutPermission { get; private set; } = null!;

    /// <summary>Platform Owner, correct platform audience — the platform positive control.</summary>
    public string PlatformOwnerToken { get; private set; } = null!;

    /// <summary>The SAME platform Owner claims carried on a TENANT-audience token. Rejected only by
    /// the PlatformAdmin policy's <c>.RequireClaim("aud", jwtOptions.PlatformAudience)</c>.</summary>
    public string PlatformOwnerTokenOnTenantAudience { get; private set; } = null!;

    /// <summary>Platform Marketing operator, correct platform audience. Passes the PlatformAdmin
    /// policy and is rejected only by <c>RequirePlatformRoleAttribute</c>'s 403 branch.</summary>
    public string PlatformMarketingToken { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // A private, shared-cache in-memory SQLite database. The anchor connection must stay open
        // for the database to exist at all; every DbContext opens its own connection to it.
        var databaseName = $"authz-pipeline-{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{databaseName}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connectionString);
        await _anchor.OpenAsync();

        Host = new AuthorizationPipelineHost(connectionString);

        // CreateClient() is what actually runs Program.cs: builder configuration, the JWT and
        // SeedAdmin fail-fast guards, DI ValidateOnBuild/ValidateScopes, the boot assertions,
        // AuthSeeder (which calls EnsureCreated), and the full middleware pipeline.
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            Jwt = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
            await SeedIdentitiesAsync(db);
        }

        // Re-read every identity from the database in a FRESH scope before minting. The session
        // stamps in the tokens are derived from the persisted UpdatedAtUtc values, and
        // Platform/TenantSessionSecurity re-derive them from a fresh read on every request — minting
        // from the same materialised values is what makes the two agree.
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();

            TenantTokenWithPermission = await MintTenantTokenAsync(db, tokenService, PermittedUserEmail);
            TenantTokenWithoutPermission = await MintTenantTokenAsync(db, tokenService, UnpermittedUserEmail);

            var owner = await ReadPlatformUserAsync(db, OwnerEmail);
            var marketing = await ReadPlatformUserAsync(db, MarketingEmail);

            PlatformOwnerToken = MintPlatformToken(owner, Jwt, Jwt.PlatformAudience);
            PlatformOwnerTokenOnTenantAudience = MintPlatformToken(owner, Jwt, Jwt.TenantAudience);
            PlatformMarketingToken = MintPlatformToken(marketing, Jwt, Jwt.PlatformAudience);
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }

    // ── Seed data ────────────────────────────────────────────────────────────────────────────

    private const string TenantSlug = "authz-pipeline-harness";
    private const string PermittedUserEmail = "notifications.manager@authz-harness.local";
    private const string UnpermittedUserEmail = "employee.reader@authz-harness.local";
    private const string OwnerEmail = "owner@authz-harness.platform";
    private const string MarketingEmail = "marketing@authz-harness.platform";

    /// <summary>The permission gating <c>GET /api/notifications/deliveries</c>.</summary>
    public const string GatedPermission = "notifications.manage";

    private static async Task SeedIdentitiesAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Name = "Authorization Pipeline Harness", Slug = TenantSlug, IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Both users are group-scoped with NO roles: their effective permission set is exactly the
        // Allow-overrides below. That keeps AuthService.GetPermissions (used at issuance) and
        // TenantSessionSecurity (which recomputes it per request and demands set equality)
        // trivially in agreement, so the ONLY difference between these two identities — and
        // therefore the only thing the 403 test can be measuring — is one permission claim.
        db.Users.Add(BuildUser(tenant.Id, PermittedUserEmail, "Harness Notifications Manager"));
        db.Users.Add(BuildUser(tenant.Id, UnpermittedUserEmail, "Harness Employee Reader"));
        await db.SaveChangesAsync();

        var permitted = await db.Users.FirstAsync(x => x.Email == PermittedUserEmail);
        var unpermitted = await db.Users.FirstAsync(x => x.Email == UnpermittedUserEmail);

        db.Set<UserPermissionOverride>().AddRange(
            new UserPermissionOverride
            {
                TenantId = tenant.Id, UserId = permitted.Id, PermissionKey = GatedPermission,
                Effect = "Allow", IsActive = true, Reason = "authorization pipeline positive control",
            },
            new UserPermissionOverride
            {
                TenantId = tenant.Id, UserId = unpermitted.Id, PermissionKey = "employees.read",
                Effect = "Allow", IsActive = true, Reason = "authenticated but not permitted",
            });

        db.PlatformUsers.AddRange(
            BuildPlatformUser(OwnerEmail, "Harness Platform Owner", PlatformRoles.Owner),
            BuildPlatformUser(MarketingEmail, "Harness Platform Marketing", PlatformRoles.Marketing));

        await db.SaveChangesAsync();
    }

    private static User BuildUser(Guid tenantId, string email, string fullName) => new()
    {
        TenantId = tenantId,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FullName = fullName,
        // Never used: these tests mint tokens through ITokenService rather than logging in, so no
        // credential is ever verified. Deliberately not a hashable value.
        PasswordHash = "no-login-path-in-this-harness",
        Status = "Active",
        AccessMode = "FullPortal",
        IsActive = true,
        IsEmailConfirmed = true,
        IsGroupScope = true,
    };

    private static PlatformUser BuildPlatformUser(string email, string fullName, string role) => new()
    {
        Email = email,
        FullName = fullName,
        PasswordHash = "no-login-path-in-this-harness",
        Role = role,
        IsActive = true,
    };

    // ── Token minting ────────────────────────────────────────────────────────────────────────

    private static async Task<string> MintTenantTokenAsync(ZayraDbContext db, ITokenService tokenService, string email)
    {
        var user = await db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .AsNoTracking()
            .FirstAsync(x => x.Email == email);

        // Mirrors AuthService.BuildAuthResponse exactly.
        var permissions = AuthService.GetPermissions(user);
        var grants = Array.Empty<EntityAccessGrant>();
        var entityScope = EntityScopeClaims.Resolve(user.IsGroupScope, grants, Array.Empty<Guid>());
        return tokenService.CreateAccessToken(
            user, Array.Empty<string>(), permissions, user.Tenant!, grants, entityScope, out _);
    }

    private static async Task<PlatformUser> ReadPlatformUserAsync(ZayraDbContext db, string email)
        => await db.PlatformUsers.AsNoTracking().FirstAsync(x => x.Email == email);

    /// <summary>
    /// Byte-for-byte mirror of <c>PlatformController.CreatePlatformToken</c>, with the audience
    /// left as a parameter so the platform-audience requirement can be isolated.
    /// </summary>
    private static string MintPlatformToken(PlatformUser user, JwtOptions jwt, string audience)
    {
        var stampSource = user.UpdatedAtUtc
            ?? throw new InvalidOperationException("Platform user session stamp was not initialised.");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, "PlatformAdmin"),
            new("is_platform_admin", "true"),
            new("platform_role", user.Role),
            new(PlatformSessionSecurity.SessionStampClaim, PlatformSessionSecurity.StampValue(stampSource)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        return Sign(claims, jwt.Issuer, audience, jwt.SigningKey, DateTime.UtcNow.AddHours(8));
    }

    // ── One-flag-at-a-time token forgery ─────────────────────────────────────────────────────

    /// <summary>
    /// Re-signs an existing token's claim set, changing EXACTLY ONE of issuer / audience / signing
    /// key / expiry. Copying the payload rather than fabricating a new one is what makes these
    /// single-variable experiments: everything the pipeline could otherwise object to is identical
    /// to a token that is known to work.
    /// </summary>
    public static string ReissueWith(
        string sourceToken,
        string issuer,
        string audience,
        string signingKey,
        DateTime expiresUtc)
    {
        var source = new JwtSecurityTokenHandler().ReadJwtToken(sourceToken);
        var claims = source.Claims
            .Where(c => c.Type is not (JwtRegisteredClaimNames.Aud
                                       or JwtRegisteredClaimNames.Iss
                                       or JwtRegisteredClaimNames.Exp
                                       or JwtRegisteredClaimNames.Nbf
                                       or JwtRegisteredClaimNames.Iat))
            .ToList();
        return Sign(claims, issuer, audience, signingKey, expiresUtc);
    }

    private static string Sign(IEnumerable<Claim> claims, string issuer, string audience, string signingKey, DateTime expiresUtc)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(issuer, audience, claims, expires: expiresUtc, signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

[CollectionDefinition("AuthorizationPipeline")]
public sealed class AuthorizationPipelineCollection : ICollectionFixture<AuthorizationPipelineFixture> { }
