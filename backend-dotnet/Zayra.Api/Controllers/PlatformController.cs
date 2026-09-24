using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Documents.Invoices;
using Zayra.Api.Infrastructure.Subscriptions;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/platform")]
[Authorize(Policy = "PlatformAdmin")]
public class PlatformController : ControllerBase
{
    // Same wire format as JwtTokenService entity_access claims ({"c":..,"r":..}).
    private static readonly JsonSerializerOptions EntityAccessClaimJson = new(JsonSerializerDefaults.Web);

    private readonly ZayraDbContext _db;
    private readonly JwtOptions _jwt;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuthSeeder _authSeeder;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _config;
    private readonly IMfaService _mfa;
    private readonly IAccessManagementService _accessManagement;
    private readonly ILogger<PlatformController> _log;
    private readonly IMemoryCache _cache;
    private readonly string _appUrl;

    /// <summary>
    /// Key read (never written) by the <c>/platform/health</c> distributed-cache probe. Carries the
    /// same "kynexone:" prefix AddStackExchangeRedisCache applies (Program.cs:285) so the probe is
    /// visibly ours in a shared Redis; a miss is the expected and successful outcome.
    /// </summary>
    internal const string RedisProbeKey = "health:probe";

    public PlatformController(
        ZayraDbContext db,
        IOptions<JwtOptions> jwt,
        IPasswordHasher passwordHasher,
        IAuthSeeder authSeeder,
        ITokenService tokenService,
        IEmailService emailService,
        IConfiguration config,
        IMfaService mfa,
        IAccessManagementService accessManagement,
        ILogger<PlatformController> log,
        IMemoryCache cache)
    {
        _db = db;
        _jwt = jwt.Value;
        _passwordHasher = passwordHasher;
        _authSeeder = authSeeder;
        _tokenService = tokenService;
        _emailService = emailService;
        _config = config;
        _mfa = mfa;
        _accessManagement = accessManagement;
        _log = log;
        _cache = cache;
        _appUrl = AuthLinkBuilder.ResolvePublicAppUrl(
            _config["APP_URL"] ?? Environment.GetEnvironmentVariable("APP_URL"));
    }

    private string PlatformAdminEmail =>
        Environment.GetEnvironmentVariable("PLATFORM_ADMIN_EMAIL") ?? string.Empty;

    // ── Auth ─────────────────────────────────────────────────────────────────

    [HttpPost("auth/login")]
    [AllowAnonymous]
    [EnableRateLimiting("platform_login")]
    public async Task<IActionResult> Login([FromBody] PlatformLoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "Email and password are required." });

        PlatformUser authenticatedUser;

        // 1. Try DB-based platform user lookup first. Include inactive rows so the bootstrap
        // environment credential can never resurrect a deliberately deactivated owner.
        var dbUser = await _db.PlatformUsers
            .FirstOrDefaultAsync(u => u.Email.ToLower() == req.Email.ToLower(), ct);

        if (dbUser is not null)
        {
            if (!dbUser.IsActive || !PlatformRoles.All.Contains(dbUser.Role))
                return Unauthorized(new { message = "Invalid platform admin credentials." });

            // Brute-force lockout (see PlatformUser.FailedLoginCount): reject while a lockout is active.
            if (dbUser.LockoutEndUtc.HasValue && dbUser.LockoutEndUtc > DateTime.UtcNow)
            {
                _db.LoginActivities.Add(new LoginActivity
                {
                    UserId = dbUser.Id, EmailAttempted = dbUser.Email,
                    EventType = LoginEventTypes.PlatformLoginFailed, FailureReason = "account_locked",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = HttpContext.Request.Headers.UserAgent.ToString(),
                });
                await _db.SaveChangesAsync(ct);
                return Unauthorized(new { message = "Invalid platform admin credentials." });
            }

            if (!_passwordHasher.Verify(req.Password, dbUser.PasswordHash))
            {
                dbUser.FailedLoginCount++;
                if (dbUser.FailedLoginCount >= PlatformUser.MaxFailedLogins)
                    dbUser.LockoutEndUtc = DateTime.UtcNow.AddMinutes(PlatformUser.LockoutMinutes);
                _db.LoginActivities.Add(new LoginActivity
                {
                    UserId        = dbUser.Id,
                    EmailAttempted = dbUser.Email,
                    EventType     = LoginEventTypes.PlatformLoginFailed,
                    FailureReason = dbUser.LockoutEndUtc.HasValue ? "account_locked_after_repeated_failures" : "password_mismatch",
                    IpAddress     = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent     = HttpContext.Request.Headers.UserAgent.ToString(),
                });
                await _db.SaveChangesAsync(ct);
                return Unauthorized(new { message = "Invalid platform admin credentials." });
            }

            // Successful password: clear any lockout state.
            dbUser.FailedLoginCount = 0;
            dbUser.LockoutEndUtc = null;

            // MFA challenge: if the DB platform user has TOTP configured, issue a challenge
            // token instead of the full JWT. The client must complete /api/platform/auth/mfa/challenge/verify.
            if (dbUser.MfaEnabled && !string.IsNullOrEmpty(dbUser.MfaSecretEncrypted))
            {
                var challengeToken = await _mfa.CreatePlatformChallengeAsync(
                    dbUser.Id, HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty, ct);
                return Ok(new { mfaRequired = true, challengeToken, expiresInSeconds = 300 });
            }

            // Update last login audit fields
            dbUser.LastLoginAtUtc = DateTime.UtcNow;
            dbUser.LastLoginIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!dbUser.UpdatedAtUtc.HasValue)
                PlatformSessionSecurity.RotateStamp(dbUser);
            await _db.SaveChangesAsync(ct);

            authenticatedUser = dbUser;
        }
        else
        {
            // Environment credentials are maintenance bootstrap inputs only. A public login
            // request must never materialize a privileged principal or mint its first session.
            // Once any platform principal exists, an unknown email is indistinguishable from a
            // wrong password (401), so this endpoint is not an account-enumeration oracle.
            if (await _db.PlatformUsers.AnyAsync(ct))
                return Unauthorized(new { message = "Invalid platform admin credentials." });

            return StatusCode(503, new
            {
                error = "platform_principal_not_provisioned",
                message = "The platform operator must be provisioned through the maintenance workflow before sign-in."
            });
        }

        // Record successful platform login
        _db.LoginActivities.Add(new LoginActivity
        {
            UserId        = authenticatedUser.Id,
            EmailAttempted = authenticatedUser.Email,
            EventType     = LoginEventTypes.PlatformLoginSuccess,
            IpAddress     = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent     = HttpContext.Request.Headers.UserAgent.ToString(),
        });
        await _db.SaveChangesAsync(ct);

        return Ok(CreatePlatformToken(authenticatedUser));
    }

    // ── Platform MFA ─────────────────────────────────────────────────────────

    [HttpPost("auth/mfa/setup")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> PlatformMfaSetup(CancellationToken ct)
    {
        var platformUserId = GetPlatformUserId();
        if (platformUserId is null) return Unauthorized();
        try
        {
            var dto = await _mfa.InitiatePlatformSetupAsync(platformUserId.Value, ct);
            return Ok(new MfaSetupInitResponse(dto.ProvisioningUri));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("auth/mfa/verify-setup")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> PlatformMfaVerifySetup([FromBody] MfaVerifySetupRequest request, CancellationToken ct)
    {
        var platformUserId = GetPlatformUserId();
        if (platformUserId is null) return Unauthorized();
        var ok = await _mfa.VerifyPlatformSetupAsync(platformUserId.Value, request, ct);
        return ok ? NoContent() : BadRequest(new { message = "Invalid TOTP code." });
    }

    [HttpPost("auth/mfa/challenge/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("platform_mfa_verify")]
    public async Task<IActionResult> PlatformMfaChallengeVerify([FromBody] MfaChallengeVerifyRequest request, CancellationToken ct)
    {
        var pu = await _mfa.CompletePlatformChallengeAsync(
            request.ChallengeToken,
            request.TotpCode,
            new RequestContext(
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                HttpContext.Request.Headers.UserAgent.ToString(),
                null,
                null),
            ct);
        if (pu is null) return Unauthorized(new { message = "Invalid or expired MFA challenge." });
        return Ok(CreatePlatformToken(pu));
    }

    [HttpPost("auth/mfa/disable")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> PlatformMfaDisable([FromBody] MfaDisableRequest request, CancellationToken ct)
    {
        var platformUserId = GetPlatformUserId();
        if (platformUserId is null) return Unauthorized();
        var ok = await _mfa.DisablePlatformAsync(platformUserId.Value, request.TotpCode, ct);
        return ok ? NoContent() : BadRequest(new { message = "Invalid TOTP code or MFA not enabled." });
    }

    [HttpPost("auth/logout")]
    public async Task<IActionResult> PlatformLogout(CancellationToken ct)
    {
        var platformUserId = GetPlatformUserId();
        if (platformUserId is null) return Unauthorized();

        var user = await _db.PlatformUsers.FirstOrDefaultAsync(x => x.Id == platformUserId.Value, ct);
        if (user is null || !user.IsActive) return Unauthorized();

        PlatformSessionSecurity.RotateStamp(user);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = Guid.Empty,
            EntityType = "PlatformSession",
            EntityId = user.Id.ToString(),
            Action = "PlatformLogout",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { allSessionsRevoked = true }),
            PerformedByName = user.Email,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private object CreatePlatformToken(PlatformUser user)
    {
        if (!user.UpdatedAtUtc.HasValue)
            throw new InvalidOperationException("Platform user session stamp was not initialized.");

        var expiresAt = DateTime.UtcNow.AddHours(8);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, "PlatformAdmin"),
            new("is_platform_admin", "true"),
            new("platform_role", user.Role),
            new(PlatformSessionSecurity.SessionStampClaim, PlatformSessionSecurity.StampValue(user.UpdatedAtUtc.Value)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_jwt.Issuer, _jwt.PlatformAudience, claims, expires: expiresAt, signingCredentials: credentials);
        return new { token = new JwtSecurityTokenHandler().WriteToken(token), expiresAt, role = user.Role };
    }

    private Guid? GetPlatformUserId()
    {
        var v = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(v, out var id) && id != Guid.Empty ? id : null;
    }

    /// <summary>
    /// Platform operators are not tenant users. In particular, the platform principal id must
    /// never be copied into RequestContext.UserId because audit/user foreign keys point at the
    /// tenant Users table. Actor identity remains available through the authenticated platform
    /// principal and request telemetry, while the tenant is explicitly bound to the target row.
    /// </summary>
    private RequestContext PlatformTenantMutationContext(Guid tenantId) => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        HttpContext.Request.Headers.UserAgent.ToString(),
        UserId: null,
        TenantId: tenantId);

    // ── Health ────────────────────────────────────────────────────────────────

    [HttpGet("health")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support, PlatformRoles.Auditor)]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var dbOk = false;
        try { await _db.Database.CanConnectAsync(ct); dbOk = true; } catch { }

        // Reads the saved relay, not just Smtp:Host from the environment — System Health reported
        // "not_configured" for every deployment that configured SMTP through Platform Settings.
        var smtpConfigured = await _emailService.IsPlatformConfiguredAsync(ct);

        // Distributed-cache health check — optional dependency.
        //
        // This used to resolve IConnectionMultiplexer. Nothing in the composition root has ever
        // registered that interface: AddStackExchangeRedisCache (Program.cs:282) registers
        // IDistributedCache backed by a RedisCache and nothing else, and the only reference to
        // IConnectionMultiplexer in the repository was this line. GetService therefore returned
        // null unconditionally and the check reported "not_configured" whether Redis was live,
        // dead or absent — a check that could never pass, and never fail either.
        //
        // Interrogate what is actually registered instead. Program.cs picks exactly one of two
        // implementations at boot from REDIS_URL, so the registered instance is the honest answer
        // to "is this deployment using Redis?", and a round-trip through it is the honest answer
        // to "is that Redis reachable?". The verdicts share ProductionReadinessEvidence's
        // vocabulary so /platform/health and /health/ready cannot disagree.
        string redisStatus;
        try
        {
            var cache = HttpContext.RequestServices.GetService<IDistributedCache>();
            if (cache is null)
            {
                // Program.cs always registers one of the two, so this means a composition-root
                // problem rather than an absent Redis. Kept distinct so the two never get confused.
                redisStatus = "not_configured";
            }
            else if (cache is MemoryDistributedCache)
            {
                // No REDIS_URL at boot, so Program.cs:288 registered the in-memory fallback.
                redisStatus = "fallback_memory";
            }
            else
            {
                // A real round trip. A miss is a success: it still required a live connection.
                await cache.GetAsync(RedisProbeKey, ct);
                redisStatus = "ok";
            }
        }
        catch
        {
            redisStatus = "error";
        }

        return Ok(new
        {
            status = dbOk ? "healthy" : "degraded",
            components = new
            {
                database = new { status = dbOk ? "ok" : "error" },
                smtp     = new { status = smtpConfigured ? "configured" : "not_configured" },
                redis    = new { status = redisStatus },
                jobs     = new { status = "unknown" },
            },
            version     = "1.0.0",
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            checkedAtUtc = DateTime.UtcNow,
        });
    }

    // ── Platform Team ─────────────────────────────────────────────────────────

    [HttpGet("team")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> ListTeam(CancellationToken ct)
    {
        var users = await _db.PlatformUsers
            .AsNoTracking()
            .OrderBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FullName,
                u.Role,
                u.IsActive,
                u.LastLoginAtUtc,
                u.LastLoginIp,
                u.CreatedAtUtc,
                u.UpdatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost("team")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> CreateTeamMember([FromBody] CreatePlatformUserRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest(new { message = "A valid email is required." });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters." });

        var role = string.IsNullOrWhiteSpace(req.Role) ? PlatformRoles.Admin : req.Role;
        if (!PlatformRoles.All.Contains(role))
            return BadRequest(new { message = $"Invalid role. Valid roles: {string.Join(", ", PlatformRoles.All)}" });

        var email = req.Email.Trim().ToLowerInvariant();
        if (await _db.PlatformUsers.AsNoTracking().AnyAsync(u => u.Email == email, ct))
            return Conflict(new { message = "A platform user with this email already exists." });

        var user = new PlatformUser
        {
            Email = email,
            FullName = string.IsNullOrWhiteSpace(req.FullName) ? email : req.FullName.Trim(),
            PasswordHash = _passwordHasher.Hash(req.Password),
            Role = role,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.PlatformUsers.Add(user);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformUser",
            EntityId        = user.Id.ToString(),
            Action          = "PlatformUserCreated",
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { user.Email, user.Role }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new { user.Id, user.Email, user.FullName, user.Role, user.IsActive, user.CreatedAtUtc });
    }

    [HttpPatch("team/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateTeamMember(Guid id, [FromBody] UpdatePlatformUserRequest req, CancellationToken ct)
    {
        var user = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound(new { message = "Platform user not found." });

        var oldRole   = user.Role;
        var oldActive = user.IsActive;

        if (req.Role is not null)
        {
            if (!PlatformRoles.All.Contains(req.Role))
                return BadRequest(new { message = $"Invalid role. Valid roles: {string.Join(", ", PlatformRoles.All)}" });
            user.Role = req.Role;
        }

        if (req.IsActive.HasValue) user.IsActive = req.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(req.FullName)) user.FullName = req.FullName.Trim();

        PlatformSessionSecurity.RotateStamp(user);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformUser",
            EntityId        = id.ToString(),
            Action          = "PlatformUserUpdated",
            OldValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { Role = oldRole, IsActive = oldActive }),
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { user.Role, user.IsActive }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new { user.Id, user.Email, user.FullName, user.Role, user.IsActive, user.UpdatedAtUtc });
    }

    // Admin: deactivation only (no true delete). Owner-only for true delete scenarios (n/a here).
    [HttpDelete("team/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner)]
    public async Task<IActionResult> DeactivateTeamMember(Guid id, CancellationToken ct)
    {
        var user = await _db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound(new { message = "Platform user not found." });

        // Soft deactivate — never hard-delete platform users
        user.IsActive = false;
        PlatformSessionSecurity.RotateStamp(user);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformUser",
            EntityId        = id.ToString(),
            Action          = "PlatformUserDeactivated",
            OldValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { user.Email }),
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { IsActive = false }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new { user.Id, user.Email, isActive = false, message = "Platform user deactivated." });
    }

    // ── Tenants List ─────────────────────────────────────────────────────────

    [HttpGet("tenants")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Support, PlatformRoles.Auditor)]
    public async Task<IActionResult> ListTenants(CancellationToken ct)
    {
        var tenants = await _db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var tenantIds = tenants.Select(t => t.Id).ToList();

        var subscriptions = await _db.TenantSubscriptions
            .AsNoTracking()
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToDictionaryAsync(s => s.TenantId, ct);

        var userCounts = await _db.Users
            .AsNoTracking()
            .Where(u => tenantIds.Contains(u.TenantId) && u.IsActive && !u.IsDeleted)
            .GroupBy(u => u.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, ct);

        var employeeCounts = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId.HasValue && tenantIds.Contains(e.TenantId.Value) && !e.IsDeleted)
            .GroupBy(e => e.TenantId!.Value)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, ct);

        var result = tenants.Select(t =>
        {
            subscriptions.TryGetValue(t.Id, out var sub);
            userCounts.TryGetValue(t.Id, out var users);
            employeeCounts.TryGetValue(t.Id, out var emps);
            return new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.IsActive,
                t.CreatedAtUtc,
                subscription = sub is null ? null : new
                {
                    sub.Plan,
                    sub.Status,
                    sub.MaxEmployees,
                    sub.MaxUsers,
                    sub.ExpiresAtUtc,
                    sub.MonthlyAmount,
                    sub.CurrencyCode,
                    sub.BillingEmail,
                    sub.BillingCycle
                },
                activeUserCount = users,
                activeEmployeeCount = emps,
                // Soft-deleted tenants are deactivated with a "__deleted_" slug suffix so the
                // console can filter/restore them instead of showing them as plain "Cancelled".
                isDeleted = !t.IsActive && t.Slug.Contains("__deleted_")
            };
        });

        return Ok(result);
    }

    // ── Tenant Detail ─────────────────────────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Support, PlatformRoles.Auditor)]
    public async Task<IActionResult> GetTenant(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var sub = await _db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var flags = await _db.TenantFeatureFlags.AsNoTracking().Where(f => f.TenantId == tenantId).OrderBy(f => f.FeatureKey).ToListAsync(ct);
        var loc = await _db.TenantLocalizationSettings.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenantId, ct);
        var branding = await _db.TenantBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);

        var userCount = await _db.Users.AsNoTracking()
            .CountAsync(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted, ct);

        var employeeCount = await _db.Employees.AsNoTracking()
            .CountAsync(e => e.TenantId == tenantId && !e.IsDeleted, ct);

        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new
            {
                c.Id,
                name = c.TradeName != "" ? c.TradeName : c.LegalNameEn,
                code = c.LegalNameEn,
                c.CountryCode,
                c.IsActive,
                c.ApprovalStatus,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.IsActive,
            tenant.CreatedAtUtc,
            tenant.AccountType,
            tenant.CompanyCreationMode,
            companies,
            subscription = sub,
            featureFlags = flags,
            localization = loc,
            branding,
            userCount,
            employeeCount
        });
    }

    // ── Subscription Upsert ───────────────────────────────────────────────────

    [HttpPut("tenants/{tenantId:guid}/subscription")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> UpsertSubscription(Guid tenantId, [FromBody] UpsertSubscriptionRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var sub = await _db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var oldSnap = sub is null ? null : new { sub.Plan, sub.Status, sub.MaxUsers, sub.MaxEmployees };

        if (sub is null)
        {
            sub = new TenantSubscription { TenantId = tenantId };
            _db.TenantSubscriptions.Add(sub);
        }

        sub.Plan = req.Plan;
        sub.Status = req.Status;
        sub.MaxEmployees = req.MaxEmployees;
        sub.MaxUsers = req.MaxUsers;
        sub.MaxCompanies = req.MaxCompanies > 0 ? req.MaxCompanies : 1;
        sub.MaxAdminUsers = req.MaxAdminUsers > 0 ? req.MaxAdminUsers : 10;
        sub.BillingEmail = req.BillingEmail;
        sub.BillingCycle = req.BillingCycle;
        sub.MonthlyAmount = req.MonthlyAmount;
        sub.CurrencyCode = req.CurrencyCode;
        sub.ExpiresAtUtc = req.ExpiresAtUtc;
        sub.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "Subscription",
            EntityId = tenantId.ToString(),
            Action = "SubscriptionUpdated",
            OldValuesJson = oldSnap is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(oldSnap),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                plan = req.Plan,
                status = req.Status,
                maxUsers = req.MaxUsers,
                maxEmployees = req.MaxEmployees,
                expiresAtUtc = req.ExpiresAtUtc
            }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        return Ok(sub);
    }

    // ── Feature Flag Toggle ───────────────────────────────────────────────────

    [HttpPut("tenants/{tenantId:guid}/features/{featureKey}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> SetFeatureFlag(Guid tenantId, string featureKey, [FromBody] SetFeatureFlagRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var flag = await _db.TenantFeatureFlags
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == featureKey, ct);

        if (flag is null)
        {
            flag = new TenantFeatureFlag { TenantId = tenantId, FeatureKey = featureKey };
            _db.TenantFeatureFlags.Add(flag);
        }

        var oldEnabled = flag.IsEnabled;
        flag.IsEnabled = req.IsEnabled;
        flag.ConfigJson = req.ConfigJson;
        flag.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "FeatureFlag",
            EntityId = $"{tenantId}/{featureKey}",
            Action = req.IsEnabled ? "FeatureEnabled" : "FeatureDisabled",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { featureKey, isEnabled = oldEnabled }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { featureKey, isEnabled = req.IsEnabled }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        FeatureFlagGuardFilter.InvalidateCache(_cache, tenantId, featureKey);
        return Ok(flag);
    }

    // ── Branding & Localization ───────────────────────────────────────────────

    [HttpPut("tenants/{tenantId:guid}/branding")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateBranding(Guid tenantId, [FromBody] UpdateBrandingRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var branding = await _db.TenantBrandings.FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);
        if (branding is null)
        {
            branding = new TenantBranding { TenantId = tenantId };
            _db.TenantBrandings.Add(branding);
        }

        if (req.LogoUrl is not null)       branding.LogoUrl       = req.LogoUrl;
        if (req.FaviconUrl is not null)    branding.FaviconUrl    = req.FaviconUrl;
        if (req.PrimaryColor is not null)  branding.PrimaryColor  = req.PrimaryColor;
        if (req.AccentColor is not null)   branding.AccentColor   = req.AccentColor;
        if (req.PortalTitle is not null)   branding.PortalTitle   = req.PortalTitle;
        if (req.CompanyNameEn is not null) branding.CompanyNameEn = req.CompanyNameEn;
        if (req.CompanyNameAr is not null) branding.CompanyNameAr = req.CompanyNameAr;
        branding.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "TenantBranding",
            EntityId = tenantId.ToString(),
            Action = "BrandingUpdated",
            OldValuesJson = "{}",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(req),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);
        return Ok(branding);
    }

    [HttpPut("tenants/{tenantId:guid}/localization")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateLocalization(Guid tenantId, [FromBody] UpdateLocalizationRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var loc = await _db.TenantLocalizationSettings.FirstOrDefaultAsync(l => l.TenantId == tenantId, ct);
        if (loc is null)
        {
            loc = new TenantLocalizationSetting { TenantId = tenantId };
            _db.TenantLocalizationSettings.Add(loc);
        }

        if (req.DefaultLanguage is not null)  loc.DefaultLanguage  = req.DefaultLanguage;
        if (req.DefaultTimezone is not null)  loc.DefaultTimezone  = req.DefaultTimezone;
        if (req.DateFormat is not null)       loc.DateFormat       = req.DateFormat;
        if (req.CurrencyCode is not null)     loc.CurrencyCode     = req.CurrencyCode;
        if (req.CountryCode is not null)      loc.CountryCode      = req.CountryCode;
        if (req.CalendarSystem is not null)   loc.CalendarSystem   = req.CalendarSystem;
        if (req.WorkWeek is not null)         loc.WorkWeek         = req.WorkWeek;
        if (req.WeekStartDay is not null)     loc.WeekStartDay     = req.WeekStartDay;
        if (req.RtlEnabled.HasValue)          loc.RtlEnabled       = req.RtlEnabled.Value;
        if (req.HijriDatesEnabled.HasValue)   loc.HijriDatesEnabled = req.HijriDatesEnabled.Value;
        loc.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "TenantLocalization",
            EntityId = tenantId.ToString(),
            Action = "LocalizationUpdated",
            OldValuesJson = "{}",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(req),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);
        return Ok(loc);
    }

    [HttpDelete("tenants/{tenantId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner)]
    public async Task<IActionResult> DeleteTenant(Guid tenantId, [FromQuery] string? confirm, CancellationToken ct)
    {
        _ = tenantId;
        _ = confirm;
        _ = ct;
        await Task.CompletedTask;
        return Conflict(new
        {
            error = "tenant_delete_disabled",
            message = "Tenant deletion is temporarily disabled pending an atomic credential-revocation and recovery workflow."
        });
    }

    // ── Soft-delete lifecycle: restore + permanent purge ──────────────────────

    /// <summary>Reverses a soft-delete: reactivates the tenant, restores its original slug,
    /// and reactivates the subscription. Idempotent-ish (no-op message if not deleted).</summary>
    [HttpPost("tenants/{tenantId:guid}/restore")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> RestoreTenant(Guid tenantId, CancellationToken ct)
    {
        _ = tenantId;
        _ = ct;
        await Task.CompletedTask;
        return Conflict(new
        {
            error = "tenant_restore_disabled",
            message = "Deleted-tenant restoration is temporarily disabled pending credential re-provisioning controls."
        });
    }

    [HttpPost("tenants/bulk/restore")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> BulkRestoreTenants([FromBody] BulkTenantActionRequest req, CancellationToken ct)
    {
        _ = req;
        _ = ct;
        await Task.CompletedTask;
        return Conflict(new
        {
            error = "bulk_tenant_restore_disabled",
            message = "Bulk deleted-tenant restoration is temporarily disabled pending credential re-provisioning controls."
        });
    }

    /// <summary>GDPR "right to erasure": PERMANENTLY deletes a soft-deleted tenant and all its
    /// data. Owner-only, irreversible, requires the tenant to already be soft-deleted, and a
    /// confirm token. This is the hard-delete that the normal "delete" intentionally avoids.</summary>
    [HttpDelete("tenants/{tenantId:guid}/purge")]
    [RequirePlatformRole(PlatformRoles.Owner)]
    public async Task<IActionResult> PurgeTenant(Guid tenantId, [FromQuery] string? confirm, CancellationToken ct)
    {
        if (confirm != "PURGE")
            return BadRequest(new { message = "Pass ?confirm=PURGE to permanently erase this tenant. This cannot be undone." });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { message = "Tenant not found." });
        if (tenant.IsActive || !tenant.Slug.Contains("__deleted_"))
            return BadRequest(new { message = "Only a soft-deleted tenant can be purged. Delete it first." });

        var name = tenant.Name;
        // Audit BEFORE erasure (the audit log row is tenant-scoped but retained as the legal record).
        AuditTenant(tenantId, "TenantPurged",
            new { tenantName = name, slug = tenant.Slug },
            new { purgedBy = "platform_admin", purgedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync(ct);

        // Hard-delete tenant-owned data. Phase 2: the erasure set is derived from the EF
        // MODEL (every ITenantOwned/INullableTenantOwned entity), not a hand-maintained
        // table list — the old fixed list silently orphaned employees, companies, payroll
        // and every other operational table. Audit logs are retained as the legal record.
        var userIds = await _db.Users.Where(u => u.TenantId == tenantId).Select(u => u.Id).ToListAsync(ct);
        if (userIds.Count > 0)
            await _db.RefreshTokens.Where(t => userIds.Contains(t.UserId)).ExecuteDeleteAsync(ct);

        var retained = new HashSet<Type> { typeof(Tenant), typeof(AuditLog), typeof(AdminAuditLog), typeof(User) };
        var sweepTypes = _db.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.BaseType is null)
            .Select(t => t.ClrType)
            .Where(clr => !retained.Contains(clr)
                          && (typeof(ITenantOwned).IsAssignableFrom(clr) || typeof(INullableTenantOwned).IsAssignableFrom(clr)))
            .ToList();

        // Two passes: FK dependents that block a parent delete on pass 1 usually succeed
        // on pass 2 once children are gone. Anything still failing is reported, not hidden.
        var failures = new List<string>();
        for (var pass = 0; pass < 2; pass++)
        {
            failures.Clear();
            foreach (var clr in sweepTypes)
            {
                try { await PurgeTenantRowsAsync(clr, tenantId, ct); }
                catch (Exception ex) { failures.Add($"{clr.Name}: {ex.GetBaseException().Message}"); }
            }
            if (failures.Count == 0) break;
        }

        await _db.Users.Where(u => u.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await _db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync(ct);

        AuditTenant(tenantId, "TenantPurgeCompleted",
            new { tables = sweepTypes.Count },
            new { unresolved = failures.Count, failures = failures.Take(10) });
        await _db.SaveChangesAsync(ct);

        if (failures.Count > 0)
            return Ok(new { tenantId, purged = true, incomplete = true, unresolvedTables = failures, message = $"Tenant '{name}' erased with {failures.Count} unresolved table(s) — review audit log." });
        return Ok(new { tenantId, purged = true, message = $"Tenant '{name}' and its data have been permanently erased." });
    }

    private static readonly System.Reflection.MethodInfo _purgeRowsMethod =
        typeof(PlatformController).GetMethod(nameof(PurgeTenantRowsCoreAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private Task PurgeTenantRowsAsync(Type entityClrType, Guid tenantId, CancellationToken ct) =>
        (Task)_purgeRowsMethod.MakeGenericMethod(entityClrType).Invoke(this, new object[] { tenantId, ct })!;

    private async Task PurgeTenantRowsCoreAsync<TEntity>(Guid tenantId, CancellationToken ct) where TEntity : class
    {
        // IgnoreQueryFilters is intentional: GDPR erasure must reach soft-deleted and
        // company-scoped rows too; the TenantId predicate fully scopes the delete.
        await _db.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => EF.Property<Guid?>(e, "TenantId") == tenantId)
            .ExecuteDeleteAsync(ct);
    }

    // ── Account type (SingleCompany | Group) ──────────────────────────────────

    /// <summary>
    /// Sets the tenant's product account type. Distinct from MaxCompanies (commercial
    /// limit): AccountType drives group behavior (multi-company provisioning, switcher).
    /// Downgrading to SingleCompany is blocked while multiple active companies exist.
    /// </summary>
    [HttpPut("tenants/{tenantId:guid}/account-type")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> SetAccountType(Guid tenantId, [FromBody] SetAccountTypeRequest req, CancellationToken ct)
    {
        if (!TenantAccountTypes.IsValid(req.AccountType))
            return BadRequest(new { message = "AccountType must be 'SingleCompany' or 'Group'." });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { message = "Tenant not found." });

        if (req.AccountType == TenantAccountTypes.SingleCompany)
        {
            var activeCompanies = await _db.Companies
                .CountAsync(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted, ct);
            if (activeCompanies > 1)
                return Conflict(new
                {
                    error = "multiple_active_companies",
                    activeCompanies,
                    message = "Cannot downgrade to SingleCompany while the tenant has multiple active companies. Deactivate the extra companies first.",
                });
        }

        var previous = tenant.AccountType;
        tenant.AccountType = req.AccountType;
        AuditTenant(tenantId, "AccountTypeChanged",
            new { accountType = previous },
            new { accountType = req.AccountType });
        await _db.SaveChangesAsync(ct);
        return Ok(new { tenantId, accountType = tenant.AccountType });
    }

    /// <summary>Sets who may create companies for this tenant and how they activate.</summary>
    [HttpPut("tenants/{tenantId:guid}/company-creation-mode")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> SetCompanyCreationMode(Guid tenantId, [FromBody] SetCompanyCreationModeRequest req, CancellationToken ct)
    {
        if (!CompanyCreationModes.IsValid(req.Mode))
            return BadRequest(new { message = "Mode must be PlatformControlled, GroupSelfServiceWithinLimit or GroupDraftPlatformApproval." });
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { message = "Tenant not found." });

        var previous = tenant.CompanyCreationMode;
        tenant.CompanyCreationMode = req.Mode;
        AuditTenant(tenantId, "CompanyCreationModeChanged",
            new { mode = previous }, new { mode = req.Mode });
        await _db.SaveChangesAsync(ct);
        return Ok(new { tenantId, companyCreationMode = tenant.CompanyCreationMode });
    }

    /// <summary>Approves a Draft company created under GroupDraftPlatformApproval — activates it.</summary>
    [HttpPost("tenants/{tenantId:guid}/companies/{companyId:guid}/approve")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> ApproveCompany(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        _ = tenantId;
        _ = companyId;
        _ = ct;
        await Task.CompletedTask;
        return Conflict(new
        {
            error = "company_activation_disabled",
            message = "Company activation is temporarily disabled pending atomic authorization invalidation."
        });
    }

    /// <summary>
    /// Platform-side company creation (used for PlatformControlled tenants). Bypasses the
    /// tenant-side creation-mode gate but still honors MaxCompanies.
    /// </summary>
    [HttpPost("tenants/{tenantId:guid}/companies")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> CreatePlatformCompany(Guid tenantId, [FromBody] Zayra.Api.Application.Organization.CompanyRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { message = "Tenant not found." });
        if (!Application.Common.CountryCodeStandard.IsValidOrEmpty(req.CountryCode))
            return BadRequest(new { message = $"Unrecognized country code '{req.CountryCode}'." });
        // Same domain rule as the tenant setup path (shared validator, not a divergent copy).
        if (!Zayra.Api.Infrastructure.Organization.OrganizationSetupService.IsValidEmailDomainOrEmpty(req.EmailDomain))
            return BadRequest(new { message = $"Invalid email domain '{req.EmailDomain}'. Use a domain like 'acme.sa'." });

        var sub = await _db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        // SYSTEM CONTEXT: tenant scope intentionally bypassed; explicit TenantId predicate.
        var count = await _db.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId && !c.IsDeleted, ct);
        if (sub is not null && sub.MaxCompanies > 0 && count >= sub.MaxCompanies)
            return StatusCode(402, new { error = "company_limit_reached", maxAllowed = sub.MaxCompanies });
        if (count >= 1 && tenant.AccountType != TenantAccountTypes.Group)
            return Conflict(new { error = "account_type_single_company", message = "Enable the Group account type before adding more companies." });

        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = req.LegalNameEn,
            LegalNameAr = req.LegalNameAr ?? string.Empty,
            TradeName = req.TradeName ?? string.Empty,
            CountryCode = req.CountryCode,
            Jurisdiction = req.Jurisdiction ?? string.Empty,
            RegistrationNumber = req.RegistrationNumber,
            TaxNumber = req.TaxNumber ?? string.Empty,
            DefaultCurrency = req.DefaultCurrency,
            // Work-email auto-derivation config — this path hand-maps CompanyRequest (does NOT call the
            // shared Apply), so the two new fields must be set explicitly or they silently would not persist.
            EmailDomain = (req.EmailDomain ?? string.Empty).Trim().ToLowerInvariant(),
            WorkEmailPattern = WorkEmailPatterns.Normalize(req.WorkEmailPattern),
            IsActive = true,
            ApprovalStatus = CompanyApprovalStatuses.Active,
        };
        _db.Companies.Add(company);
        AuditTenant(tenantId, "CompanyCreatedByPlatform", new { }, new { companyId = company.Id, code = company.LegalNameEn });
        await _db.SaveChangesAsync(ct);
        return Ok(new { companyId = company.Id, company.LegalNameEn, company.ApprovalStatus });
    }

    /// <summary>
    /// Shared claim-v2 scope emission for minted (impersonation / break-glass) tokens —
    /// same resolver as normal login, so all three token types can never drift apart.
    /// </summary>
    private async Task<List<Claim>> BuildEntityScopeClaimsAsync(User user, Guid tenantId, CancellationToken ct)
    {
        var entityAccess = user.EntityAccesses
            .Where(e => e.IsActive)
            .Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode))
            .ToList();
        IReadOnlyCollection<Guid> activeCompanyIds = Array.Empty<Guid>();
        if (entityAccess.Any(g => g.Mode == EntityGrantModes.AllCurrentCompanies))
            activeCompanyIds = await _db.Companies
                .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
                .Select(c => c.Id)
                .ToListAsync(ct);
        var scope = EntityScopeClaims.Resolve(user.IsGroupScope, entityAccess, activeCompanyIds);
        return EntityScopeClaims.Build(scope, entityAccess);
    }

    // ── Impersonation ─────────────────────────────────────────────────────────

    [HttpPost("tenants/{tenantId:guid}/impersonate")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public IActionResult Impersonate(Guid tenantId, [FromBody] ImpersonateRequest req, CancellationToken ct)
    {
        _ = tenantId;
        _ = req;
        _ = ct;
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "privileged_tenant_access_disabled",
            message = "Tenant impersonation is temporarily disabled pending server-side revocation controls."
        });
    }

    // ── Client Provisioning ───────────────────────────────────────────────────

    /// <summary>Provision a new client: tenant + full role set + tenant admin ("sub admin") + subscription.</summary>
    [HttpPost("tenants")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantRequest req, CancellationToken ct)
    {
        var name = req.Name?.Trim();
        var slug = req.Slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "Tenant name is required." });
        if (string.IsNullOrWhiteSpace(slug) || !System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$"))
            return BadRequest(new { message = "Slug must be 3-40 chars: lowercase letters, digits and hyphens." });
        if (string.IsNullOrWhiteSpace(req.AdminEmail) || !req.AdminEmail.Contains('@'))
            return BadRequest(new { message = "A valid admin email is required." });
        if (string.IsNullOrWhiteSpace(req.AdminPassword) || req.AdminPassword.Length < 10)
            return BadRequest(new { message = "Admin password must be at least 10 characters." });

        if (await _db.Tenants.AsNoTracking().AnyAsync(t => t.Slug == slug && t.IsActive, ct))
            return Conflict(new { message = $"A tenant with slug '{slug}' already exists." });

        if (req.AccountType is not null && !TenantAccountTypes.IsValid(req.AccountType))
            return BadRequest(new { message = "AccountType must be 'SingleCompany' or 'Group'." });
        if (req.CompanyCreationMode is not null && !CompanyCreationModes.IsValid(req.CompanyCreationMode))
            return BadRequest(new { message = "CompanyCreationMode must be PlatformControlled, GroupSelfServiceWithinLimit or GroupDraftPlatformApproval." });

        if (_db.Database.IsRelational())
        {
            // The provisioning bundle performs several SaveChanges calls. Execute all of them
            // inside one retry-aware database transaction so a seeder/config/admin/subscription
            // failure cannot leave a tenant shell that cannot be logged into or re-provisioned.
            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                try
                {
                    var result = await CreateTenantCore(req, name, slug, ct);
                    await transaction.CommitAsync(ct);
                    return result;
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            });
        }

        return await CreateTenantCore(req, name, slug, ct);
    }

    private async Task<IActionResult> CreateTenantCore(
        CreateTenantRequest req,
        string name,
        string slug,
        CancellationToken ct)
    {

        var tenant = new Tenant
        {
            Name = name,
            Slug = slug,
            AccountType = req.AccountType ?? TenantAccountTypes.SingleCompany,
            CompanyCreationMode = req.CompanyCreationMode ?? CompanyCreationModes.GroupSelfServiceWithinLimit,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        // Every tenant is born with its first company, inside this transaction. This used to be
        // left to CompanyScopeBackfill at the next boot, which created data outside the platform
        // admin (docs/DATA_ENTRY_PATHS.md); the backfill now only repairs, never creates.
        _db.Companies.Add(new Company
        {
            TenantId = tenant.Id,
            LegalNameEn = name,
            TradeName = name,
            IsActive = true,
        });
        await _db.SaveChangesAsync(ct);

        // Full standard RBAC set (Admin → Employee), same as the seeded tenant
        var adminRole = await _authSeeder.EnsureTenantRolesAsync(tenant.Id, ct);
        // Phase 2: eager-seed the GL defaults (chart of accounts, tenant-default mappings, the 17
        // system posting drivers, and the client-configurable rate allow-list) for the new tenant.
        await Zayra.Api.Infrastructure.Seed.GlDriverSeeder.SeedTenantDefaultsAsync(_db, tenant.Id, ct);
        // Configurability keystone: eager-seed the data-driven pay-component defaults (the system rows
        // that reproduce the current payslip earning/deduction set) so a new tenant is born with an
        // editable component catalog rather than a hard-coded sequence.
        await Zayra.Api.Infrastructure.Seed.PayComponentSeeder.SeedTenantDefaultsAsync(_db, tenant.Id, ct);
        // FIX 1 (C1): the idempotent per-tenant provisioning bundle — country rules, MasterData,
        // HR categories, default attendance/leave/approval policies, and bilingual notification
        // templates. Ensures no tenant is born without its statutory/reference/config foundation.
        await Zayra.Api.Infrastructure.Seed.TenantProvisioningBundle.ProvisionAsync(_db, tenant.Id, ct);
        await _db.SaveChangesAsync(ct);

        var admin = new User
        {
            TenantId = tenant.Id,
            Email = req.AdminEmail.Trim().ToLowerInvariant(),
            NormalizedEmail = AuthService.Normalize(req.AdminEmail),
            FullName = string.IsNullOrWhiteSpace(req.AdminFullName) ? "Tenant Administrator" : req.AdminFullName.Trim(),
            PasswordHash = _passwordHasher.Hash(req.AdminPassword),
            AccessMode = "FullPortal",
            Status = "Active",
            IsActive = true,
            IsEmailConfirmed = true,
            IsGroupScope = true
        };
        admin.UserRoles.Add(new UserRole { User = admin, Role = adminRole });
        _db.Users.Add(admin);

        var plan = string.IsNullOrWhiteSpace(req.Plan) ? "Trial" : req.Plan;
        var (defaultMaxUsers, defaultMaxEmployees) = SubscriptionTiers.GetDefaults(plan);
        int defaultMaxCompanies = plan switch { "Enterprise" => 0, "Growth" => 3, _ => 1 };
        int defaultMaxAdminUsers = plan switch { "Enterprise" => 0, "Growth" => 25, _ => 10 };
        _db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenant.Id,
            Plan = plan,
            Status = "Active",
            MaxUsers = req.MaxUsers ?? defaultMaxUsers,
            MaxEmployees = req.MaxEmployees ?? defaultMaxEmployees,
            MaxCompanies = req.MaxCompanies ?? defaultMaxCompanies,
            MaxAdminUsers = req.MaxAdminUsers ?? defaultMaxAdminUsers,
            BillingEmail = req.BillingEmail ?? req.AdminEmail.Trim().ToLowerInvariant(),
            BillingCycle = req.BillingCycle ?? "Monthly",
            MonthlyAmount = req.MonthlyAmount ?? 0,
            CurrencyCode = req.CurrencyCode ?? "USD",
            ExpiresAtUtc = req.ExpiresAtUtc
        });

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenant.Id,
            EntityType = "Tenant",
            EntityId = tenant.Id.ToString(),
            Action = "TenantCreated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                name = tenant.Name,
                slug = tenant.Slug,
                plan,
                adminEmail = admin.Email,
                maxUsers = req.MaxUsers ?? SubscriptionTiers.GetDefaults(plan).MaxUsers,
                maxEmployees = req.MaxEmployees ?? SubscriptionTiers.GetDefaults(plan).MaxEmployees
            }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenant.Id,
            EntityType = "User",
            EntityId = admin.Id.ToString(),
            Action = "AdminCreated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { email = admin.Email, role = "Admin" }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetTenant), new { tenantId = tenant.Id }, new
        {
            tenantId = tenant.Id,
            tenant.Name,
            tenant.Slug,
            adminUserId = admin.Id,
            adminEmail = admin.Email,
            plan,
            loginHint = $"Tenant slug '{tenant.Slug}' + admin email/password on the login page."
        });
    }

    // ── Tenant Admins ("sub admins") ─────────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}/admins")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> ListTenantAdmins(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var admins = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted
                && u.UserRoles.Any(ur => ur.Role != null && ur.Role.Name == "Admin"))
            .OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.FullName, u.IsActive, u.Status, u.CreatedAtUtc })
            .ToListAsync(ct);

        return Ok(admins);
    }

    [HttpPost("tenants/{tenantId:guid}/admins")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> AddTenantAdmin(Guid tenantId, [FromBody] AddTenantAdminRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { message = "Tenant not found." });

        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest(new { message = "A valid email is required." });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 10)
            return BadRequest(new { message = "Password must be at least 10 characters." });

        var normalizedEmail = AuthService.Normalize(req.Email);
        if (await _db.Users.AsNoTracking().AnyAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalizedEmail, ct))
            return Conflict(new { message = "A user with this email already exists in the tenant." });

        var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == "Admin" && !r.IsDeleted, ct)
            ?? await _authSeeder.EnsureTenantRolesAsync(tenantId, ct);

        var user = new User
        {
            TenantId = tenantId,
            Email = req.Email.Trim().ToLowerInvariant(),
            NormalizedEmail = normalizedEmail,
            FullName = string.IsNullOrWhiteSpace(req.FullName) ? "Tenant Administrator" : req.FullName.Trim(),
            PasswordHash = _passwordHasher.Hash(req.Password),
            AccessMode = "FullPortal",
            Status = "Active",
            IsActive = true,
            IsEmailConfirmed = true,
            IsGroupScope = true
        };
        user.UserRoles.Add(new UserRole { User = user, Role = adminRole });
        _db.Users.Add(user);

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "User",
            EntityId = user.Id.ToString(),
            Action = "AdminCreated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { email = user.Email, fullName = user.FullName, role = "Admin" }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);

        return Ok(new { user.Id, user.Email, user.FullName, tenantSlug = tenant.Slug });
    }

    // ── Create Tenant User (any role) ─────────────────────────────────────────

    /// <summary>The legal-entity scope a platform-created account will be born with, plus whether
    /// the operator asked for it or it came from the role default.</summary>
    private readonly record struct CreatedUserScope(string Mode, IReadOnlyList<Guid> CompanyIds, bool WasExplicit);

    /// <summary>
    /// Resolves <c>entityScope</c> (+ <c>companyIds</c>) onto a real <see cref="EntityGrantModes"/>
    /// decision, or returns the refusal that explains why the account would have been blind.
    ///
    /// <para>Accepted values, in the operator's vocabulary: <c>group</c> (every company, now and in
    /// future — what a tenant Admin gets), <c>allCurrentCompanies</c> (the companies that exist
    /// today), and <c>companies</c> with an explicit <c>companyIds</c> list.</para>
    ///
    /// <para>Default when the caller says nothing: <c>group</c> for the tenant Admin role, which is
    /// what this endpoint has always done and what an Admin means; <c>allCurrentCompanies</c> for
    /// every other role, because a tenant-level role such as HR Manager or a payroll approver is
    /// hired to work across the tenant's entities, and the previous default — nothing at all — is
    /// not a safer answer, it is a broken account. RBAC still decides what they may DO; this decides
    /// only which legal entities they can SEE.</para>
    ///
    /// <para>FAIL CLOSED: any non-group resolution that would reach zero companies is refused, so
    /// the failure surfaces to the operator creating the account instead of to the user as a 404.</para>
    /// </summary>
    private async Task<(CreatedUserScope Scope, IActionResult? Error)> ResolveCreatedUserScopeAsync(
        Guid tenantId, string normalizedRoleName, CreateTenantUserRequest req, CancellationToken ct)
    {
        var requested = req.EntityScope?.Trim();
        var explicitScope = !string.IsNullOrWhiteSpace(requested);
        var requestedIds = (req.CompanyIds ?? Array.Empty<Guid>()).Distinct().ToList();

        string mode;
        if (!explicitScope)
        {
            mode = normalizedRoleName == "ADMIN"
                ? EntityGrantModes.AllCurrentAndFutureCompanies
                : EntityGrantModes.AllCurrentCompanies;
        }
        else if (string.Equals(requested, "group", StringComparison.OrdinalIgnoreCase))
            mode = EntityGrantModes.AllCurrentAndFutureCompanies;
        else if (string.Equals(requested, "allCurrentCompanies", StringComparison.OrdinalIgnoreCase))
            mode = EntityGrantModes.AllCurrentCompanies;
        else if (string.Equals(requested, "companies", StringComparison.OrdinalIgnoreCase))
            mode = EntityGrantModes.SelectedCompanies;
        else
            return (default, BadRequest(new
            {
                error = "invalid_entity_scope",
                message = $"entityScope '{requested}' is not recognised. Use 'group' (all companies, now and in future), "
                        + "'allCurrentCompanies', or 'companies' together with companyIds.",
            }));

        if (mode != EntityGrantModes.SelectedCompanies && requestedIds.Count > 0)
            return (default, BadRequest(new
            {
                error = "company_ids_not_applicable",
                message = "companyIds may only be supplied with entityScope 'companies'.",
            }));

        // The companies this tenant actually has. A platform operator carries no tenant claim and no
        // entity scope of its own, so the company filter has to come off — through the sanctioned
        // ScopedBypass.TenantWide, which re-applies the tenant predicate itself.
        var activeCompanyIds = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide<Company>(_db.Companies, tenantId,
                "Platform operator provisioning a tenant user: it holds no entity scope of its own, so the "
                + "legal entities available to grant must be read across the tenant. Tenant is re-applied by the helper.")
            .AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (mode == EntityGrantModes.SelectedCompanies)
        {
            if (requestedIds.Count == 0)
                return (default, BadRequest(new
                {
                    error = "company_ids_required",
                    message = "entityScope 'companies' needs at least one company id — a user granted no company sees nothing.",
                }));
            var unknown = requestedIds.Where(id => !activeCompanyIds.Contains(id)).ToList();
            if (unknown.Count > 0)
                return (default, BadRequest(new
                {
                    error = "unknown_company",
                    message = "Every companyId must be an active legal entity of this tenant.",
                    unknownCompanyIds = unknown,
                }));
            return (new CreatedUserScope(mode, requestedIds, explicitScope), null);
        }

        if (mode == EntityGrantModes.AllCurrentCompanies && activeCompanyIds.Count == 0)
            return (default, Conflict(new
            {
                error = "tenant_has_no_companies",
                message = "This tenant has no active legal entity, so a company-scoped account would be able to see "
                        + "nothing at all. Create the legal entity first, or create this user with entityScope 'group' "
                        + "so it follows the tenant as entities are added.",
            }));

        return (new CreatedUserScope(mode, Array.Empty<Guid>(), explicitScope), null);
    }

    [HttpPost("tenants/{tenantId:guid}/users")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> CreateTenantUser(Guid tenantId, [FromBody] CreateTenantUserRequest req, CancellationToken ct)
    {
        if (req.MustChangePassword == true)
            return Conflict(new
            {
                error = "forced_password_change_flow_disabled",
                message = "Creating a temporary-password account is disabled. Use controlled invitation onboarding."
            });
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { message = "Tenant not found." });

        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest(new { message = "A valid email is required." });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 10)
            return BadRequest(new { message = "Password must be at least 10 characters." });

        var normalizedEmail = AuthService.Normalize(req.Email);

        var roleName = string.IsNullOrWhiteSpace(req.RoleName) ? "Admin" : req.RoleName.Trim();
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == roleName && !r.IsDeleted, ct);
        if (role is null)
        {
            // Ensure the standard tenant roles exist, then re-resolve the requested role.
            await _authSeeder.EnsureTenantRolesAsync(tenantId, ct);
            role = await _db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == roleName && !r.IsDeleted, ct);
            if (role is null) return BadRequest(new { message = $"Role '{roleName}' not found for this tenant." });
        }

        // ── ENTITY (legal-entity) SCOPE ──────────────────────────────────────────────────────────
        // A user with no scope is invisible to itself: every company-owned row is filtered out of
        // its queries, so a payroll approver got a flat 404 from /runs/{id}/approve for a run that
        // plainly existed. This endpoint used to set IsGroupScope for the Admin role and nothing at
        // all for any other, and had no scope parameter — so the operator could not have got it
        // right even if they had known. The scope is now an explicit, audited part of the request,
        // and a request that would resolve to zero accessible companies is REFUSED rather than
        // creating an account that cannot see the tenant it belongs to.
        var scopeResolution = await ResolveCreatedUserScopeAsync(tenantId, role.NormalizedName, req, ct);
        if (scopeResolution.Error is { } scopeError) return scopeError;
        var scope = scopeResolution.Scope;

        // There is a UNIQUE index on (TenantId, NormalizedEmail) that ignores IsDeleted,
        // so a previously soft-deleted user with this email still occupies the slot.
        // Match including soft-deleted rows: block live duplicates, but resurrect a removed one.
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        var existing = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalizedEmail, ct);

        if (existing is not null)
            return Conflict(new { message = "A user with this email already exists in the tenant." });

        var fullName = string.IsNullOrWhiteSpace(req.FullName) ? req.Email.Trim() : req.FullName.Trim();
        var displayEmail = req.Email.Trim().ToLowerInvariant();
        User user;
        bool restored = false;

        if (existing is not null)
        {
            // Resurrect the soft-deleted row in place (avoids the unique-constraint collision).
            restored = true;
            user = existing;
            user.IsDeleted = false;
            user.DeletedAtUtc = null;
            user.Email = displayEmail;
            user.NormalizedEmail = normalizedEmail;
            user.FullName = fullName;
            user.PasswordHash = _passwordHasher.Hash(req.Password);
            user.AccessMode = "FullPortal";
            user.Status = "Active";
            user.IsActive = true;
            user.IsEmailConfirmed = true;
            user.IsLocked = false;
            user.LockoutEnd = null;
            user.FailedLoginCount = 0;
            user.MustChangePassword = req.MustChangePassword ?? false;
            user.IsGroupScope = scope.Mode == EntityGrantModes.AllCurrentAndFutureCompanies;
            user.UpdatedAtUtc = DateTime.UtcNow;
            _db.UserRoles.RemoveRange(user.UserRoles);
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }
        else
        {
            user = new User
            {
                TenantId = tenantId,
                Email = displayEmail,
                NormalizedEmail = normalizedEmail,
                FullName = fullName,
                PasswordHash = _passwordHasher.Hash(req.Password),
                AccessMode = "FullPortal",
                Status = "Active",
                IsActive = true,
                IsEmailConfirmed = true,
                IsGroupScope = scope.Mode == EntityGrantModes.AllCurrentAndFutureCompanies,
                MustChangePassword = req.MustChangePassword ?? false
            };
            user.UserRoles.Add(new UserRole { User = user, Role = role });
            _db.Users.Add(user);
        }

        // The grants that make the account able to see its own tenant. Group scope needs none —
        // EntityScopeClaims.Resolve short-circuits on User.IsGroupScope — so writing one would be a
        // second, divergent source of truth for the same decision.
        if (scope.Mode != EntityGrantModes.AllCurrentAndFutureCompanies)
        {
            _db.UserEntityAccesses.RemoveRange(
                await _db.UserEntityAccesses.Where(g => g.TenantId == tenantId && g.UserId == user.Id).ToListAsync(ct));
            foreach (var companyId in scope.Mode == EntityGrantModes.SelectedCompanies
                         ? scope.CompanyIds.Select(id => (Guid?)id)
                         : new Guid?[] { null })
            {
                _db.UserEntityAccesses.Add(new UserEntityAccess
                {
                    TenantId = tenantId,
                    UserId = user.Id,
                    CompanyId = companyId,
                    GrantMode = scope.Mode,
                    Role = roleName,
                    IsActive = true,
                    GrantedAt = DateTime.UtcNow,
                });
            }
        }

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "User",
            EntityId = user.Id.ToString(),
            Action = restored ? "UserRestored" : "UserCreated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                email = user.Email, fullName = user.FullName, role = roleName, restored,
                entityScope = scope.Mode,
                companyIds = scope.CompanyIds,
                entityScopeSource = scope.WasExplicit ? "request" : "role-default",
            }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            user.Id, user.Email, user.FullName, role = roleName, restored, tenantSlug = tenant.Slug,
            // Echoed so the operator can see what the account can actually reach, rather than
            // discovering it as a 404 on the user's first working day.
            entityScope = scope.Mode,
            companyIds = scope.CompanyIds,
        });
    }

    // ── Tenant Suspend / Reactivate ───────────────────────────────────────────

    [HttpPost("tenants/{tenantId:guid}/suspend")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> SuspendTenant(Guid tenantId, [FromBody] TenantActionRequest req, CancellationToken ct)
    {
        var outcome = await ApplyTenantLifecycleAsync(tenantId, false, req.Reason, ct);
        if (!outcome.TenantFound) return NotFound(new { message = "Tenant not found." });
        if (!outcome.Allowed) return Conflict(new { error = "deleted_tenant_lifecycle_blocked", message = outcome.BlockReason });
        if (!outcome.SubscriptionFound) return BadRequest(new { message = "Tenant has no subscription record." });
        return Ok(new { tenantId, status = "Suspended" });
    }

    [HttpPost("tenants/{tenantId:guid}/reactivate")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> ReactivateTenant(Guid tenantId, [FromBody] TenantActionRequest req, CancellationToken ct)
    {
        var outcome = await ApplyTenantLifecycleAsync(tenantId, true, req.Reason, ct);
        if (!outcome.TenantFound) return NotFound(new { message = "Tenant not found." });
        if (!outcome.Allowed) return Conflict(new { error = "deleted_tenant_lifecycle_blocked", message = outcome.BlockReason });
        if (!outcome.SubscriptionFound) return BadRequest(new { message = "Tenant has no subscription record." });
        return Ok(new { tenantId, status = "Active" });
    }

    // ── Bulk Tenant Operations ────────────────────────────────────────────────
    // All bulk endpoints are idempotent per-tenant and return a per-tenant result
    // breakdown so the console can surface partial successes. They reuse the exact
    // status/audit semantics of the single-tenant endpoints above.

    [HttpPost("tenants/bulk/suspend")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> BulkSuspendTenants([FromBody] BulkTenantActionRequest req, CancellationToken ct)
    {
        var ids = NormalizeTenantIds(req.TenantIds);
        if (ids.Count == 0) return BadRequest(new { message = "No tenants selected." });

        var results = new List<BulkOpItem>();
        foreach (var id in ids)
        {
            var outcome = await ApplyTenantLifecycleAsync(id, false, req.Reason, ct, skipIfAlreadyInTargetState: true);
            if (!outcome.TenantFound) { results.Add(BulkOpItem.Skip(id, "Tenant not found.")); continue; }
            if (!outcome.Allowed) { results.Add(BulkOpItem.Skip(id, outcome.BlockReason ?? "Deleted tenant lifecycle is blocked.")); continue; }
            if (!outcome.SubscriptionFound) { results.Add(BulkOpItem.Skip(id, "No subscription record.")); continue; }
            if (outcome.AlreadyInTargetState) { results.Add(BulkOpItem.Skip(id, "Already suspended.")); continue; }
            results.Add(BulkOpItem.Ok(id, outcome.TenantName));
        }
        return Ok(BulkSummary("suspend", results));
    }

    [HttpPost("tenants/bulk/reactivate")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> BulkReactivateTenants([FromBody] BulkTenantActionRequest req, CancellationToken ct)
    {
        var ids = NormalizeTenantIds(req.TenantIds);
        if (ids.Count == 0) return BadRequest(new { message = "No tenants selected." });

        var results = new List<BulkOpItem>();
        foreach (var id in ids)
        {
            var outcome = await ApplyTenantLifecycleAsync(id, true, req.Reason, ct);
            if (!outcome.TenantFound) { results.Add(BulkOpItem.Skip(id, "Tenant not found.")); continue; }
            if (!outcome.Allowed) { results.Add(BulkOpItem.Skip(id, outcome.BlockReason ?? "Deleted tenant lifecycle is blocked.")); continue; }
            if (!outcome.SubscriptionFound) { results.Add(BulkOpItem.Skip(id, "No subscription record.")); continue; }
            results.Add(BulkOpItem.Ok(id, outcome.TenantName));
        }
        return Ok(BulkSummary("reactivate", results));
    }

    [HttpPost("tenants/bulk/delete")]
    [RequirePlatformRole(PlatformRoles.Owner)]
    public async Task<IActionResult> BulkDeleteTenants([FromBody] BulkDeleteTenantsRequest req, CancellationToken ct)
    {
        _ = req;
        _ = ct;
        await Task.CompletedTask;
        return Conflict(new
        {
            error = "bulk_tenant_delete_disabled",
            message = "Bulk tenant deletion is temporarily disabled. Use the reviewed single-tenant lifecycle workflow."
        });
    }

    /// <summary>Enable/disable a single feature across many tenants at once, or platform-wide.
    /// Pass either an explicit TenantIds list or ApplyToAll=true (every active tenant).</summary>
    [HttpPost("tenants/bulk/features")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> BulkSetFeatureFlag([FromBody] BulkFeatureFlagRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.FeatureKey))
            return BadRequest(new { message = "featureKey is required." });

        List<Guid> ids;
        if (req.ApplyToAll)
            ids = await _db.Tenants.Where(t => t.IsActive).Select(t => t.Id).ToListAsync(ct);
        else
            ids = NormalizeTenantIds(req.TenantIds);

        if (ids.Count == 0) return BadRequest(new { message = "No tenants selected." });

        var results = new List<BulkOpItem>();
        foreach (var id in ids)
        {
            var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null) { results.Add(BulkOpItem.Skip(id, "Tenant not found.")); continue; }

            var flag = await _db.TenantFeatureFlags
                .FirstOrDefaultAsync(f => f.TenantId == id && f.FeatureKey == req.FeatureKey, ct);
            if (flag is null)
            {
                flag = new TenantFeatureFlag { TenantId = id, FeatureKey = req.FeatureKey };
                _db.TenantFeatureFlags.Add(flag);
            }

            var oldEnabled = flag.IsEnabled;
            flag.IsEnabled = req.IsEnabled;
            if (req.ConfigJson is not null) flag.ConfigJson = req.ConfigJson;
            flag.UpdatedAtUtc = DateTime.UtcNow;

            AuditTenant(id, req.IsEnabled ? "FeatureEnabled" : "FeatureDisabled",
                new { featureKey = req.FeatureKey, isEnabled = oldEnabled },
                new { featureKey = req.FeatureKey, isEnabled = req.IsEnabled },
                entityType: "FeatureFlag", entityId: $"{id}/{req.FeatureKey}");
            results.Add(BulkOpItem.Ok(id, tenant.Name));
        }

        await _db.SaveChangesAsync(ct);
        foreach (var item in results.Where(r => r.Status == "ok"))
            FeatureFlagGuardFilter.InvalidateCache(_cache, item.TenantId, req.FeatureKey);

        return Ok(BulkSummary(req.IsEnabled ? "feature-enable" : "feature-disable", results, featureKey: req.FeatureKey, appliedToAll: req.ApplyToAll));
    }

    // ── Bulk helpers ──────────────────────────────────────────────────────────

    private sealed record TenantLifecycleOutcome(
        bool TenantFound,
        bool SubscriptionFound,
        string TenantName,
        int UsersInvalidated,
        int SessionsRevoked,
        bool Allowed,
        string? BlockReason,
        bool AlreadyInTargetState = false);

    private async Task<TenantLifecycleOutcome> ApplyTenantLifecycleAsync(
        Guid tenantId,
        bool activate,
        string? reason,
        CancellationToken ct,
        bool skipIfAlreadyInTargetState = false)
    {
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        var action = activate ? "Reactivated" : "Suspended";
        var targetStatus = activate ? "Active" : "Suspended";
        var actor = PlatformActorEmail();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        TenantLifecycleOutcome outcome = new(false, false, string.Empty, 0, 0, false, null);

        async Task<bool> ApplyOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
            if (tenant is null)
            {
                outcome = new(false, false, string.Empty, 0, 0, false, null);
                return true;
            }

            if (tenant.PurgedAtUtc.HasValue
                || tenant.SoftDeletedAtUtc.HasValue
                || tenant.Slug.Contains("__deleted_", StringComparison.Ordinal))
            {
                outcome = new(
                    true,
                    true,
                    tenant.Name,
                    0,
                    0,
                    false,
                    "Deleted or purged tenants cannot use the subscription suspend/reactivate workflow.");
                return true;
            }

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var subscription = await _db.TenantSubscriptions.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
            if (subscription is null)
            {
                outcome = new(true, false, tenant.Name, 0, 0, true, null);
                return true;
            }

            // Bulk operations are idempotent: a tenant already in the target state is skipped
            // with no mutation, no audit row and no session revocation.
            if (skipIfAlreadyInTargetState && subscription.Status == targetStatus)
            {
                outcome = new(true, true, tenant.Name, 0, 0, true, null, AlreadyInTargetState: true);
                return true;
            }

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var users = await _db.Users.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId)
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);
            var userIds = users.Select(x => x.Id).ToList();

            await _db.MfaChallengeTokens.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => userIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var user in users)
                TenantSessionSecurity.RotateStamp(user, changedAtUtc);

            var revoked = 0;
            if (_db.Database.IsRelational())
            {
                // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
                await _db.MfaChallengeTokens.IgnoreQueryFilters()
                    .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, changedAtUtc), cancellationToken);
                revoked = await _db.RefreshTokens
                    .Where(x => userIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, changedAtUtc)
                        .SetProperty(x => x.RevokedByIp, ip), cancellationToken);
            }
            else
            {
                // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
                foreach (var challenge in await _db.MfaChallengeTokens.IgnoreQueryFilters()
                    .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                    .ToListAsync(cancellationToken))
                    challenge.UsedAtUtc = changedAtUtc;
                foreach (var token in await _db.RefreshTokens
                    .Where(x => userIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                    .ToListAsync(cancellationToken))
                {
                    token.RevokedAtUtc = changedAtUtc;
                    token.RevokedByIp = ip;
                    revoked++;
                }
            }

            var previousStatus = subscription.Status;
            tenant.IsActive = activate;
            subscription.Status = targetStatus;
            subscription.UpdatedAtUtc = changedAtUtc;
            _db.AdminAuditLogs.Add(new AdminAuditLog
            {
                Id = auditId,
                TenantId = tenantId,
                EntityType = "Tenant",
                EntityId = tenantId.ToString(),
                Action = action,
                OldValuesJson = JsonSerializer.Serialize(new { status = previousStatus }),
                NewValuesJson = JsonSerializer.Serialize(new
                {
                    status = targetStatus,
                    reason,
                    usersInvalidated = users.Count,
                    sessionsRevoked = revoked
                }),
                PerformedByName = actor,
                IpAddress = ip,
                CreatedAtUtc = changedAtUtc
            });
            outcome = new(true, true, tenant.Name, users.Count, revoked, true, null);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                ApplyOnceAsync,
                // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
                async cancellationToken => await _db.AdminAuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(x => x.Id == auditId && x.Action == action, cancellationToken),
                IsolationLevel.ReadCommitted,
                ct);
        }
        else
        {
            await ApplyOnceAsync(ct);
        }

        return outcome;
    }

    private static List<Guid> NormalizeTenantIds(IEnumerable<Guid>? ids)
        => ids is null ? new() : ids.Where(g => g != Guid.Empty).Distinct().ToList();

    private void AuditTenant(Guid tenantId, string action, object oldVals, object newVals,
        string entityType = "Tenant", string? entityId = null)
    {
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = entityType,
            EntityId = entityId ?? tenantId.ToString(),
            Action = action,
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(oldVals),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(newVals),
            PerformedByName = PlatformActorEmail(),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
    }

    /// <summary>Email of the acting platform admin (from the platform JWT), for audit attribution.</summary>
    private string PlatformActorEmail() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? "platform_admin";

    /// <summary>
    /// Writes an attributable platform-admin audit record (SOC2 privileged-access evidence). Unlike the
    /// tenant-scoped writes, this records WHICH platform admin performed the action. Caller must SaveChanges.
    /// </summary>
    private void AuditPlatformAction(Guid tenantId, string action, string entityType, string entityId, object details)
    {
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { actingAdminId = GetPlatformUserId(), actingAdminEmail = PlatformActorEmail() }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(details),
            PerformedByName = PlatformActorEmail(),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
    }

    private static object BulkSummary(string operation, List<BulkOpItem> results,
        string? featureKey = null, bool? appliedToAll = null) => new
    {
        operation,
        featureKey,
        appliedToAll,
        requested = results.Count,
        succeeded = results.Count(r => r.Status == "ok"),
        skipped   = results.Count(r => r.Status == "skipped"),
        failed    = results.Count(r => r.Status == "failed"),
        results
    };

    // ── Tenant Profile Edit ───────────────────────────────────────────────────

    [HttpPatch("tenants/{tenantId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateTenant(Guid tenantId, [FromBody] UpdateTenantRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { message = "Tenant not found." });

        var oldName = tenant.Name;
        if (!string.IsNullOrWhiteSpace(req.Name)) tenant.Name = req.Name.Trim();

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "Tenant",
            EntityId = tenantId.ToString(),
            Action = "Updated",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { name = oldName }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { name = tenant.Name }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { tenant.Id, tenant.Name, tenant.Slug });
    }

    // ── Tenant Users ──────────────────────────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}/roles")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> ListTenantRoles(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();
        var roles = await _db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && !r.IsDeleted && r.IsActive)
            .OrderBy(r => r.AuthorityLevel)
            .Select(r => new { r.Id, r.Name, r.Description, r.IsSystem })
            .ToListAsync(ct);
        return Ok(roles);
    }

    [HttpGet("tenants/{tenantId:guid}/users")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> ListTenantUsers(Guid tenantId, [FromQuery] string? search, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var q = _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Where(u => u.TenantId == tenantId && !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            q = q.Where(u => u.Email.Contains(s) || u.FullName.ToLower().Contains(s));
        }

        var users = await q
            .OrderBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FullName,
                u.IsActive,
                u.IsLocked,
                u.LockoutEnd,
                u.MFAEnabled,
                u.MustChangePassword,
                u.Status,
                u.CreatedAtUtc,
                Roles = u.UserRoles.Where(ur => ur.Role != null).Select(ur => ur.Role!.Name).ToList()
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    // ── Password Reset (platform-initiated) ───────────────────────────────────

    /// <summary>
    /// TODO: Implement email delivery.
    /// Currently generates a reset token stored on the user. The actual email send
    /// requires an IEmailService (SMTP/SendGrid) not yet wired to the platform portal.
    /// When email service is available: call IAuthService.ForgotPasswordAsync with the user's email.
    /// </summary>
    [HttpPost("users/{userId:guid}/send-password-reset")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> SendPasswordReset(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(
            u => u.Id == userId && !u.IsDeleted && u.IsActive,
            ct);
        if (user is null) return NotFound(new { message = "User not found." });
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == user.TenantId && t.IsActive, ct);
        if (tenant is null) return Conflict(new { message = "The user's workspace is unavailable." });

        // Safety: block reset attempts targeting the platform admin credential (env-var based, not in DB)
        if (user.Email.Equals(PlatformAdminEmail, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        // Generate reset token, store it, and email the link
        var resetToken = _tokenService.CreateSecureToken();
        var expiresAt = DateTime.UtcNow.AddHours(1);
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = _tokenService.HashToken(resetToken),
            ExpiresAtUtc = expiresAt,
            CreatedByIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = user.TenantId,
            EntityType = "User",
            EntityId = userId.ToString(),
            Action = "PasswordResetRequested",
            OldValuesJson = "{}",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { userEmail = user.Email, initiatedBy = "platform_admin" }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);

        // Build reset link and send email (falls back gracefully if SMTP not configured)
        var resetUrl = AuthLinkBuilder.ResetPassword(
            _appUrl, tenant.Slug, resetToken);

        var html = $"""
            <p>Hi {System.Web.HttpUtility.HtmlEncode(user.FullName)},</p>
            <p>A platform administrator has requested a password reset for your account. Click the link below to set a new password. This link expires in <strong>1 hour</strong>.</p>
            <p><a href="{resetUrl}" style="background:#2563EB;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;display:inline-block">Reset Password</a></p>
            <p>If you did not expect this, contact your platform administrator immediately.</p>
            <hr/>
            <p style="font-size:12px;color:#666">This action was initiated by the platform super-admin · KynexOne Workforce</p>
            """;

        // TENANT-EXPLICIT: the recipient is a tenant user, so their own tenant's relay is the right
        // sender. Under the old ambient call this request (a platform admin, no tenant_id claim)
        // bypassed the query filter and matched every tenant's Email settings at once.
        bool smtpConfigured = await _emailService.IsConfiguredAsync(user.TenantId, ct);
        bool emailSent = false;

        if (smtpConfigured)
        {
            try
            {
                await _emailService.SendAsync(user.TenantId, user.Email, user.FullName, "Your KynexOne password reset (admin-initiated)", html, cancellationToken: ct);
                emailSent = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Platform password reset email failed for {Email}. Token saved.", user.Email);
            }
        }
        else
        {
            _log.LogInformation("SMTP not configured — reset token saved for {Email}, no email sent.", user.Email);
        }

        return Ok(new
        {
            userId,
            userEmail = user.Email,
            emailSent,
            smtpConfigured,
            message = emailSent
                ? $"Password reset email sent to {user.Email}. Link expires in 1 hour."
                : smtpConfigured
                    ? "SMTP is configured but email delivery failed — check server logs."
                    : "Reset token saved and logged. SMTP is not configured — share the reset link directly or configure SMTP in Setup → Email Settings.",
            emailDeliveryAvailable = smtpConfigured,
            resetTokenExpiresAt = expiresAt
        });
    }

    [HttpPost("users/{userId:guid}/force-password-reset")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> ForcePasswordReset(Guid userId, [FromBody] ForcePasswordResetRequest req, CancellationToken ct)
    {
        await Task.CompletedTask;
        return Conflict(new
        {
            error = "temporary_password_flow_disabled",
            message = "Direct temporary-password resets are disabled. Send the controlled password-reset link."
        });
    }

    [HttpPatch("users/{userId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> EditUser(Guid userId, [FromBody] EditUserRequest req, CancellationToken ct)
    {
        // Eligibility and authorization mutations need the locked, session-invalidating access
        // workflows. This generic editor cannot safely reactivate an identity or replace roles,
        // so fail the whole request before loading or changing any tracked row.
        if (req.Status is not null || req.IsActive.HasValue || req.RoleName is not null)
        {
            return Conflict(new
            {
                error = "controlled_identity_workflow_required",
                message = "Status, activation, and role changes are disabled in this editor. Use the approved access-management workflow."
            });
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null) return NotFound(new { message = "User not found." });
        if (user.Email.Equals(PlatformAdminEmail, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var changes = new System.Text.Json.Nodes.JsonObject();

        if (!string.IsNullOrWhiteSpace(req.FullName) && req.FullName != user.FullName)
        {
            changes["fullName"] = new System.Text.Json.Nodes.JsonArray(user.FullName, req.FullName);
            user.FullName = req.FullName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(req.Email) && !req.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedNewEmail = Infrastructure.Auth.AuthService.Normalize(req.Email);
            var emailTaken = await _db.Users.AnyAsync(
                u => u.TenantId == user.TenantId
                    && u.NormalizedEmail == normalizedNewEmail
                    && u.Id != userId
                    && !u.IsDeleted,
                ct);
            if (emailTaken) return BadRequest(new { message = "Email address is already in use by another user." });
            changes["email"] = new System.Text.Json.Nodes.JsonArray(user.Email, req.Email.Trim());
            user.Email = req.Email.Trim();
            user.NormalizedEmail = normalizedNewEmail;
        }

        if (changes.Count == 0)
            return Ok(new { userId, email = user.Email, fullName = user.FullName, status = user.Status, isActive = user.IsActive });

        user.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = user.TenantId,
            EntityType = "User",
            EntityId = userId.ToString(),
            Action = "UserEdited",
            OldValuesJson = "{}",
            NewValuesJson = changes.ToJsonString(),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { userId, email = user.Email, fullName = user.FullName, status = user.Status, isActive = user.IsActive });
    }

    [HttpDelete("users/{userId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> DeleteTenantUser(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null) return NotFound(new { message = "User not found." });
        if (user.Email.Equals(PlatformAdminEmail, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var isAdmin = user.UserRoles.Any(ur => ur.Role != null && ur.Role.Name == "Admin");
        if (isAdmin)
        {
            // Never strand a tenant without an admin — block removing the last active admin.
            var otherAdmins = await _db.Users
                .Where(u => u.TenantId == user.TenantId && u.Id != userId && !u.IsDeleted && u.IsActive
                    && u.UserRoles.Any(ur => ur.Role != null && ur.Role.Name == "Admin"))
                .CountAsync(ct);
            if (otherAdmins == 0)
                return BadRequest(new { message = "Cannot remove the last administrator. Add another admin first." });
        }

        try
        {
            var deleted = await _accessManagement.DeleteUserAsync(
                user.TenantId,
                userId,
                EntityScopeContext.GroupLevel,
                PlatformTenantMutationContext(user.TenantId),
                ct);
            if (!deleted) return NotFound(new { message = "User not found." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "User not found.")
        {
            return NotFound(new { message = "User not found." });
        }

        return Ok(new { userId, userEmail = user.Email, deleted = true });
    }

    [HttpPost("users/{userId:guid}/unlock")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> UnlockUser(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null) return NotFound(new { message = "User not found." });
        if (user.Email.Equals(PlatformAdminEmail, StringComparison.OrdinalIgnoreCase)) return Forbid();

        try
        {
            await _accessManagement.UnlockUserAsync(
                user.TenantId,
                userId,
                EntityScopeContext.GroupLevel,
                PlatformTenantMutationContext(user.TenantId),
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "User not found.")
        {
            return NotFound(new { message = "User not found." });
        }

        var current = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        return Ok(new
        {
            userId,
            userEmail = current.Email,
            unlocked = !current.IsLocked,
            isActive = current.IsActive,
            status = current.Status
        });
    }

    [HttpPost("users/{userId:guid}/disable-mfa")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> DisableMfa(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null) return NotFound(new { message = "User not found." });
        if (user.Email.Equals(PlatformAdminEmail, StringComparison.OrdinalIgnoreCase)) return Forbid();

        var disabled = await _mfa.AdminDisableAsync(
            userId,
            user.TenantId,
            PlatformTenantMutationContext(user.TenantId),
            ct);
        if (!disabled)
        {
            return Conflict(new
            {
                error = "mfa_disable_not_applied",
                message = "MFA recovery was not applied. The tenant, user, or enrolled factor is not currently eligible."
            });
        }

        return Ok(new { userId, userEmail = user.Email, mfaDisabled = true });
    }

    [HttpPost("users/{userId:guid}/revoke-sessions")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> RevokeSessions(Guid userId, CancellationToken ct)
    {
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var actor = PlatformActorEmail();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = HttpContext.Request.Headers.UserAgent.ToString();
        string? userEmail = null;
        var revoked = 0;
        var found = false;
        var forbidden = false;

        async Task<bool> RevokeOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            // IgnoreQueryFilters is intentional: pinned to a unique key or an id set resolved from the tenant-scoped/locked graph above (register §6).
            var identity = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == userId && !x.IsDeleted)
                .Select(x => new { x.TenantId, x.Email })
                .SingleOrDefaultAsync(cancellationToken);
            if (identity is null) return true;

            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == identity.TenantId, cancellationToken);
            if (tenant is null) return true;
            var user = await _db.Users.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.Id == userId && !x.IsDeleted, cancellationToken);
            if (user is null) return true;
            found = true;
            userEmail = user.Email;
            if (user.Email.Equals(PlatformAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                forbidden = true;
                return true;
            }

            // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
            await _db.MfaChallengeTokens.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == userId && x.UsedAtUtc == null)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == userId && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            TenantSessionSecurity.RotateStamp(user, changedAtUtc);
            if (_db.Database.IsRelational())
            {
                // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
                await _db.MfaChallengeTokens.IgnoreQueryFilters()
                    .Where(x => x.UserId == userId && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, changedAtUtc), cancellationToken);
                revoked = await _db.RefreshTokens
                    .Where(x => x.UserId == userId && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, changedAtUtc)
                        .SetProperty(x => x.RevokedByIp, ip), cancellationToken);
            }
            else
            {
                // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
                foreach (var challenge in await _db.MfaChallengeTokens.IgnoreQueryFilters()
                    .Where(x => x.UserId == userId && x.UsedAtUtc == null)
                    .ToListAsync(cancellationToken))
                    challenge.UsedAtUtc = changedAtUtc;
                foreach (var token in await _db.RefreshTokens
                    .Where(x => x.UserId == userId && x.RevokedAtUtc == null)
                    .ToListAsync(cancellationToken))
                {
                    token.RevokedAtUtc = changedAtUtc;
                    token.RevokedByIp = ip;
                    revoked++;
                }
            }

            _db.LoginActivities.Add(new LoginActivity
            {
                Id = activityId,
                TenantId = user.TenantId,
                UserId = user.Id,
                EmailAttempted = user.Email,
                EventType = LoginEventTypes.SessionRevoked,
                IpAddress = ip,
                UserAgent = userAgent,
                OccurredAtUtc = changedAtUtc
            });
            _db.AdminAuditLogs.Add(new AdminAuditLog
            {
                Id = auditId,
                TenantId = user.TenantId,
                EntityType = "User",
                EntityId = userId.ToString(),
                Action = "SessionsRevoked",
                OldValuesJson = "{}",
                NewValuesJson = JsonSerializer.Serialize(new
                {
                    userEmail = user.Email,
                    initiatedBy = actor,
                    sessionsRevoked = revoked,
                    challengesInvalidated = true,
                    sessionStampRotated = true
                }),
                PerformedByName = actor,
                IpAddress = ip ?? string.Empty,
                CreatedAtUtc = changedAtUtc
            });
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                RevokeOnceAsync,
                // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
                async cancellationToken => await _db.AdminAuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(x => x.Id == auditId && x.Action == "SessionsRevoked", cancellationToken),
                IsolationLevel.ReadCommitted,
                ct);
        }
        else
        {
            await RevokeOnceAsync(ct);
        }

        if (!found) return NotFound(new { message = "User not found." });
        if (forbidden) return Forbid();
        return Ok(new { userId, userEmail, sessionsRevoked = revoked });
    }

    // ── Platform Audit Logs ───────────────────────────────────────────────────

    [HttpGet("audit-logs")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support, PlatformRoles.Auditor)]
    public async Task<IActionResult> GetAuditLogs([FromQuery] Guid? tenantId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var q = _db.AdminAuditLogs.AsNoTracking().AsQueryable();

        if (tenantId.HasValue) q = q.Where(l => l.TenantId == tenantId.Value);

        // When no tenant filter, show only platform-level events (tenant lifecycle, support sessions, password resets)
        if (!tenantId.HasValue)
            q = q.Where(l =>
                l.EntityType == "Tenant" ||
                l.EntityType == "SupportSession" ||
                (l.EntityType == "User" && (l.Action.Contains("Password") || l.Action.Contains("Suspend") || l.Action.Contains("Reactivat"))));

        var total = await q.CountAsync(ct);

        var rawLogs = await q
            .OrderByDescending(l => l.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new {
                l.Id,
                l.TenantId,
                l.EntityType,
                l.EntityId,
                l.Action,
                l.OldValuesJson,
                l.NewValuesJson,
                l.PerformedByName,
                l.IpAddress,
                l.CreatedAtUtc,
            })
            .ToListAsync(ct);

        // Resolve tenant names in a single query rather than N+1
        var tenantIds = rawLogs.Select(l => l.TenantId).Distinct().ToList();
        var tenantNames = await _db.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var logs = rawLogs.Select(l => new {
            l.Id,
            l.TenantId,
            TenantName = tenantNames.TryGetValue(l.TenantId, out var n) ? n : null,
            l.EntityType,
            l.EntityId,
            l.Action,
            l.OldValuesJson,
            l.NewValuesJson,
            l.PerformedByName,
            l.IpAddress,
            l.CreatedAtUtc,
        });

        return Ok(new { total, page, pageSize, logs });
    }

    // ── Plans Catalog ─────────────────────────────────────────────────────────

    private static readonly (string Name, int MaxUsers, int MaxEmployees, decimal DefaultPrice, string Description)[] _planDefs = new[]
    {
        ("Trial",      3,  10,   0m,   "Free evaluation. No payment required. 10 employees, 3 users, core modules only."),
        ("Starter",    10, 50,   149m, "Up to 50 employees. Core HR, attendance, leave, payroll."),
        ("Growth",     50, 250,  499m, "Up to 250 employees. All Starter modules plus recruitment, performance, loans, analytics."),
        ("Enterprise", 0,  0,    0m,   "Unlimited employees and users. All modules, custom branding, dedicated support. Custom pricing."),
    };

    [HttpGet("plans")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Auditor)]
    public async Task<IActionResult> GetPlans(CancellationToken ct)
    {
        var keys = _planDefs.Select(p => $"plan.price.{p.Name}").ToList();
        var stored = await _db.PlatformConfigEntries
            .AsNoTracking()
            .Where(e => keys.Contains(e.Key))
            .ToDictionaryAsync(e => e.Key, e => e.Value, ct);

        var plans = _planDefs.Select(p =>
        {
            var key = $"plan.price.{p.Name}";
            var price = stored.TryGetValue(key, out var v) && decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : p.DefaultPrice;
            return new { p.Name, p.MaxUsers, p.MaxEmployees, MonthlyPrice = price, p.Description };
        });
        return Ok(plans);
    }

    [HttpPut("plans/{planName}/price")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> UpdatePlanPrice(string planName, [FromBody] UpdatePlanPriceRequest req, CancellationToken ct)
    {
        if (!_planDefs.Any(p => p.Name.Equals(planName, StringComparison.OrdinalIgnoreCase)))
            return NotFound(new { message = $"Plan '{planName}' not found." });
        if (req.MonthlyPrice < 0)
            return BadRequest(new { message = "Price cannot be negative." });

        var configKey = $"plan.price.{planName}";
        var entry = await _db.PlatformConfigEntries.FirstOrDefaultAsync(e => e.Key == configKey, ct);
        if (entry is null)
            _db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = configKey, Value = req.MonthlyPrice.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        else
        {
            entry.Value = req.MonthlyPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
            entry.UpdatedAtUtc = DateTime.UtcNow;
        }

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            EntityType = "PlanConfig",
            EntityId = planName,
            Action = "PlanPriceUpdated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { planName, monthlyPrice = req.MonthlyPrice }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { planName, monthlyPrice = req.MonthlyPrice });
    }

    // ── Support Access (structured break-glass) ───────────────────────────────

    [HttpPost("support-access/start")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public IActionResult StartSupportAccess([FromBody] StartSupportAccessRequest req, CancellationToken ct)
    {
        _ = req;
        _ = ct;
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "privileged_tenant_access_disabled",
            message = "Support-session issuance is temporarily disabled pending server-side revocation controls."
        });
    }

    [HttpPost("support-access/end")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> EndSupportAccess([FromBody] EndSupportAccessRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(req.SessionId, out var sessionId))
            return BadRequest(new { message = "Invalid sessionId." });

        // Production uses a conditional UPDATE inside one transaction. Under PostgreSQL, a
        // concurrent replay waits on the row lock and then re-checks EndedAtUtc, so exactly one
        // caller changes the row and writes the audit. The other caller returns the committed
        // result without a second side effect. The non-relational branch exists only for focused
        // in-memory unit tests; the real HTTP harness exercises the relational branch.
        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            var attempt = 0;
            return await strategy.ExecuteAsync(async () =>
            {
                // A transient failure can leave an Added audit entry in the scoped context even
                // though the transaction was rolled back. Discard attempt-local tracked state
                // before reloading the authoritative row, otherwise the retry could persist both
                // the abandoned audit and the new one.
                if (attempt++ > 0) _db.ChangeTracker.Clear();

                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                var session = await _db.PlatformSupportSessions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
                if (session is null)
                {
                    await transaction.RollbackAsync(ct);
                    return (IActionResult)NotFound(new { message = "Support session not found." });
                }

                if (session.EndedAtUtc is not null)
                {
                    await transaction.CommitAsync(ct);
                    return (IActionResult)Ok(new { sessionId, endedAt = session.EndedAtUtc });
                }

                var endedAt = DateTime.UtcNow;
                var changed = await _db.PlatformSupportSessions
                    .Where(s => s.Id == sessionId && s.EndedAtUtc == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.EndedAtUtc, endedAt), ct);

                if (changed == 0)
                {
                    var replayed = await _db.PlatformSupportSessions
                        .AsNoTracking()
                        .SingleAsync(s => s.Id == sessionId, ct);
                    await transaction.CommitAsync(ct);
                    return (IActionResult)Ok(new { sessionId, endedAt = replayed.EndedAtUtc });
                }

                _db.AdminAuditLogs.Add(BuildSupportAccessEndedAudit(session, endedAt));
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (IActionResult)Ok(new { sessionId, endedAt });
            });
        }

        var tracked = await _db.PlatformSupportSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (tracked is null) return NotFound(new { message = "Support session not found." });
        if (tracked.EndedAtUtc is not null)
            return Ok(new { sessionId, endedAt = tracked.EndedAtUtc });

        tracked.EndedAtUtc = DateTime.UtcNow;
        _db.AdminAuditLogs.Add(BuildSupportAccessEndedAudit(tracked, tracked.EndedAtUtc.Value));
        await _db.SaveChangesAsync(ct);
        return Ok(new { sessionId, endedAt = tracked.EndedAtUtc });
    }

    private AdminAuditLog BuildSupportAccessEndedAudit(PlatformSupportSession session, DateTime endedAt) => new()
    {
        TenantId = session.TenantId,
        EntityType = "SupportSession",
        EntityId = session.Id.ToString(),
        Action = "SupportAccessEnded",
        OldValuesJson = "{}",
        NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            targetUserEmail = session.TargetUserEmail,
            reason = session.Reason,
            durationMinutes = (int)(endedAt - session.StartedAtUtc).TotalMinutes
        }),
        PerformedByName = "platform_admin",
        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
    };

    [HttpGet("support-access")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support, PlatformRoles.Auditor)]
    public async Task<IActionResult> ListSupportSessions([FromQuery] Guid? tenantId, [FromQuery] bool activeOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var q = _db.PlatformSupportSessions.AsNoTracking().AsQueryable();
        if (tenantId.HasValue) q = q.Where(s => s.TenantId == tenantId.Value);
        if (activeOnly) q = q.Where(s => s.EndedAtUtc == null && s.ExpiresAtUtc > DateTime.UtcNow);

        var total = await q.CountAsync(ct);
        var sessions = await q
            .OrderByDescending(s => s.StartedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.TenantId,
                s.TargetUserId,
                s.TargetUserEmail,
                s.Reason,
                s.StartedByEmail,
                s.StartedByIp,
                s.StartedAtUtc,
                s.ExpiresAtUtc,
                s.EndedAtUtc,
                IsActive = s.EndedAtUtc == null && s.ExpiresAtUtc > DateTime.UtcNow
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, sessions });
    }

    // ── Tenant Invoices ───────────────────────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}/invoices")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> ListTenantInvoices(Guid tenantId, CancellationToken ct)
    {
        if (!await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct)) return NotFound();
        var invoices = await _db.TenantInvoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.InvoiceDate)
            .ToListAsync(ct);

        // Line-level breakdown (subtotal / tax) so the console can show a proper VAT summary,
        // and the paid-to-date so it can render the outstanding balance per invoice.
        var ids = invoices.Select(i => i.Id).ToList();
        var lineAgg = await _db.TenantInvoiceLines.AsNoTracking()
            .Where(l => ids.Contains(l.InvoiceId))
            .GroupBy(l => l.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Sub = g.Sum(x => x.LineTotal - x.TaxAmount), Tax = g.Sum(x => x.TaxAmount) })
            .ToDictionaryAsync(x => x.InvoiceId, ct);
        var paidAgg = await _db.TenantPayments.AsNoTracking()
            .Where(p => ids.Contains(p.InvoiceId!.Value) && p.Status == PaymentStatuses.Completed)
            .GroupBy(p => p.InvoiceId!.Value)
            .Select(g => new { InvoiceId = g.Key, Paid = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, ct);

        var result = invoices.Select(i =>
        {
            lineAgg.TryGetValue(i.Id, out var la);
            paidAgg.TryGetValue(i.Id, out var pa);
            var paid = pa?.Paid ?? 0m;
            return new
            {
                i.Id, i.TenantId, i.InvoiceNumber, i.Amount, i.CurrencyCode, i.Status,
                i.PaymentMethod, i.PaymentReference, i.PeriodDescription,
                i.InvoiceDate, i.DueDate, i.PaidDate, i.Notes, i.RecipientEmail,
                i.CreatedAtUtc, i.UpdatedAtUtc,
                effectiveStatus = EffectiveInvoiceStatus(i),
                subtotal = la?.Sub ?? 0m,
                taxTotal = la?.Tax ?? 0m,
                paidToDate = paid,
                balanceDue = Math.Max(0m, i.Amount - paid),
            };
        });
        return Ok(result);
    }

    [HttpPost("tenants/{tenantId:guid}/invoices")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> CreateInvoice(Guid tenantId, [FromBody] CreateInvoiceRequest req, CancellationToken ct)
    {
        if (!await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct)) return NotFound();
        if (req.Amount < 0)
            return BadRequest(new { message = "Amount must be non-negative." });

        // Best practice: invoice numbers are sequential and gap-less per tenant per year.
        // Auto-generate when not supplied; always enforce uniqueness so we never issue duplicates.
        var number = req.InvoiceNumber?.Trim();
        if (string.IsNullOrWhiteSpace(number))
            number = await GenerateInvoiceNumberAsync(tenantId, req.InvoiceDate.Year, ct);
        if (await _db.TenantInvoices.AnyAsync(i => i.TenantId == tenantId && i.InvoiceNumber == number, ct))
            return Conflict(new { message = $"Invoice number '{number}' already exists for this tenant." });

        var invoice = new TenantInvoice
        {
            TenantId = tenantId,
            InvoiceNumber = number,
            Amount = req.Amount,
            CurrencyCode = string.IsNullOrWhiteSpace(req.CurrencyCode) ? "USD" : req.CurrencyCode.Trim().ToUpperInvariant(),
            Status = string.IsNullOrWhiteSpace(req.Status) ? InvoiceStatuses.Draft : req.Status,
            PaymentMethod = req.PaymentMethod?.Trim(),
            PaymentReference = req.PaymentReference?.Trim(),
            PeriodDescription = req.PeriodDescription?.Trim(),
            InvoiceDate = req.InvoiceDate,
            DueDate = req.DueDate,
            PaidDate = req.PaidDate,
            Notes = req.Notes?.Trim(),
            RecipientEmail = string.IsNullOrWhiteSpace(req.RecipientEmail) ? null : req.RecipientEmail.Trim().ToLowerInvariant()
        };
        _db.TenantInvoices.Add(invoice);

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "Invoice",
            EntityId = invoice.Id.ToString(),
            Action = "InvoiceCreated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                invoiceNumber = invoice.InvoiceNumber,
                amount = invoice.Amount,
                currency = invoice.CurrencyCode,
                status = invoice.Status,
                dueDate = invoice.DueDate
            }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(ListTenantInvoices), new { tenantId }, invoice);
    }

    [HttpPut("tenants/{tenantId:guid}/invoices/{invoiceId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> UpdateInvoice(Guid tenantId, Guid invoiceId, [FromBody] UpdateInvoiceRequest req, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();

        var oldStatus = invoice.Status;
        if (req.Status is not null) invoice.Status = req.Status;
        if (req.PaymentMethod is not null) invoice.PaymentMethod = req.PaymentMethod.Trim();
        if (req.PaymentReference is not null) invoice.PaymentReference = req.PaymentReference.Trim();
        if (req.PaidDate.HasValue) invoice.PaidDate = req.PaidDate;
        if (req.Notes is not null) invoice.Notes = req.Notes.Trim();
        if (req.RecipientEmail is not null) invoice.RecipientEmail = string.IsNullOrWhiteSpace(req.RecipientEmail) ? null : req.RecipientEmail.Trim().ToLowerInvariant();
        invoice.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "Invoice",
            EntityId = invoiceId.ToString(),
            Action = "InvoiceUpdated",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { status = oldStatus }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = invoice.Status,
                paymentMethod = invoice.PaymentMethod,
                paymentReference = invoice.PaymentReference,
                paidDate = invoice.PaidDate
            }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        return Ok(invoice);
    }

    [HttpDelete("tenants/{tenantId:guid}/invoices/{invoiceId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> DeleteInvoice(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        // Best practice: an issued invoice is a financial record — it is VOIDED (Cancelled), never
        // hard-deleted. Only a Draft (never issued) can be physically removed.
        if (invoice.Status != InvoiceStatuses.Draft)
            return BadRequest(new { message = $"A {invoice.Status} invoice cannot be deleted — set its status to Cancelled to void it instead. Only Draft invoices can be removed." });

        var snap = new { invoiceNumber = invoice.InvoiceNumber, amount = invoice.Amount, status = invoice.Status };
        _db.TenantInvoices.Remove(invoice);

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "Invoice",
            EntityId = invoiceId.ToString(),
            Action = "InvoiceDeleted",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(snap),
            NewValuesJson = "{}",
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/pdf")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> DownloadInvoicePdf(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();

        var sub    = await _db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        var lines  = await LoadInvoiceLinesAsync(invoice.Id, ct);

        var data = BuildInvoiceData(invoice, tenant, sub, lines);
        var pdfBytes = InvoicePdfService.Generate(data);

        return File(pdfBytes, "application/pdf", $"Invoice_{invoice.InvoiceNumber}.pdf");
    }

    [HttpPost("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/send")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> SendInvoiceEmail(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();

        var sub    = await _db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        var toEmail = (!string.IsNullOrWhiteSpace(invoice.RecipientEmail) ? invoice.RecipientEmail : sub?.BillingEmail) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(toEmail))
            return BadRequest(new { message = "No recipient email on this invoice and no billing email on the tenant subscription. Set an email on the invoice or update the tenant's billing email." });

        // PLATFORM-EXPLICIT: a subscription invoice is the platform billing the tenant, so it must
        // go out from the platform relay — never from the customer's own mail server.
        var isConfigured = await _emailService.IsPlatformConfiguredAsync(ct);
        if (!isConfigured)
            return BadRequest(new
            {
                message = "SMTP is not configured on this platform. Go to Platform Settings → Email to configure SMTP, or download the PDF and send it manually.",
                smtpRequired = true
            });

        // Generate the PDF invoice
        var lines    = await LoadInvoiceLinesAsync(invoice.Id, ct);
        var data     = BuildInvoiceData(invoice, tenant, sub, lines);
        var pdfBytes = InvoicePdfService.Generate(data);
        var fileName = $"Invoice_{invoice.InvoiceNumber}.pdf";

        var amountFmt = $"{invoice.Amount:N2} {invoice.CurrencyCode}";
        var dueDateFmt = invoice.DueDate.ToString("dd MMM yyyy");
        var periodLine = invoice.PeriodDescription is { Length: > 0 } p
            ? $"<p><strong>Service period:</strong> {System.Web.HttpUtility.HtmlEncode(p)}</p>"
            : "";

        var htmlBody = $"""
            <div style="font-family:Arial,sans-serif;max-width:600px;margin:auto">
              <div style="background:#1e3a5f;padding:20px 24px">
                <h2 style="color:#fff;margin:0;font-size:20px">KynexOne Workforce</h2>
                <p style="color:#93c5fd;margin:4px 0 0">Invoice {System.Web.HttpUtility.HtmlEncode(invoice.InvoiceNumber)}</p>
              </div>
              <div style="padding:24px;background:#f8fafc">
                <p style="font-size:14px;color:#374151">
                  Dear <strong>{System.Web.HttpUtility.HtmlEncode(tenant?.Name ?? "Client")}</strong>,
                </p>
                <p style="font-size:14px;color:#374151">
                  Please find your invoice attached as a PDF. A summary is shown below.
                </p>
                <table style="border-collapse:collapse;width:100%;font-size:14px;margin-top:12px">
                  <tr style="background:#1e3a5f;color:#fff">
                    <td style="padding:10px 14px;font-weight:600">Invoice #</td>
                    <td style="padding:10px 14px">{System.Web.HttpUtility.HtmlEncode(invoice.InvoiceNumber)}</td>
                  </tr>
                  <tr style="background:#f1f5f9">
                    <td style="padding:10px 14px;font-weight:600;color:#374151">Amount Due</td>
                    <td style="padding:10px 14px;font-size:18px;color:#1e3a5f;font-weight:700">{System.Web.HttpUtility.HtmlEncode(amountFmt)}</td>
                  </tr>
                  <tr>
                    <td style="padding:10px 14px;font-weight:600;color:#374151">Due Date</td>
                    <td style="padding:10px 14px;color:#374151">{System.Web.HttpUtility.HtmlEncode(dueDateFmt)}</td>
                  </tr>
                </table>
                {periodLine}
                <p style="font-size:13px;color:#374151;margin-top:20px">
                  Please arrange payment by the due date. For questions, reply to this email or contact your account manager.
                </p>
              </div>
              <div style="padding:12px 24px;background:#e2e8f0">
                <p style="font-size:11px;color:#64748b;margin:0">
                  KynexOne Workforce · This is an automated invoice notification. The PDF invoice is attached.
                </p>
              </div>
            </div>
            """;

        var attachment = new EmailAttachment(fileName, pdfBytes, "application/pdf");
        await _emailService.SendPlatformAsync(
            toEmail,
            tenant?.Name ?? toEmail,
            $"Invoice {invoice.InvoiceNumber} — {amountFmt} due {dueDateFmt}",
            htmlBody,
            [attachment],
            ct);

        invoice.Status = InvoiceStatuses.Sent;
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "Invoice",
            EntityId = invoiceId.ToString(),
            Action = "InvoiceSent",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { toEmail, invoiceNumber = invoice.InvoiceNumber, pdfAttached = true }),
            PerformedByName = "platform_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { sent = true, billingEmail = toEmail, invoiceNumber = invoice.InvoiceNumber, pdfAttached = true });
    }

    // ── Invoice Line Items ────────────────────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/lines")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> ListInvoiceLines(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        return Ok(await LoadInvoiceLinesAsync(invoiceId, ct));
    }

    [HttpPost("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/lines")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> AddInvoiceLine(Guid tenantId, Guid invoiceId, [FromBody] AddInvoiceLineRequest req, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        if (invoice.Status != InvoiceStatuses.Draft)
            return BadRequest(new { message = "Line items can only be changed while the invoice is a Draft. Once issued an invoice is immutable — issue a credit note or void it to adjust." });
        if (string.IsNullOrWhiteSpace(req.Description))
            return BadRequest(new { message = "Description is required." });
        if (req.Quantity <= 0)
            return BadRequest(new { message = "Quantity must be greater than 0." });
        if (req.UnitPrice < 0 || req.DiscountAmount < 0 || req.TaxRate < 0)
            return BadRequest(new { message = "Price, discount, and tax rate must be non-negative." });

        var (taxAmount, lineTotal) = CalcLineTotals(req.UnitPrice, req.Quantity, req.DiscountAmount, req.TaxRate);
        var line = new TenantInvoiceLine
        {
            InvoiceId      = invoiceId,
            TenantId       = tenantId,
            Description    = req.Description.Trim(),
            Quantity       = req.Quantity,
            UnitPrice      = Math.Round(req.UnitPrice, 2),
            DiscountAmount = Math.Round(req.DiscountAmount, 2),
            TaxRate        = Math.Round(req.TaxRate, 4),
            TaxAmount      = taxAmount,
            LineTotal      = lineTotal,
            SortOrder      = req.SortOrder,
        };
        _db.TenantInvoiceLines.Add(line);
        await RecalculateInvoiceTotalAsync(invoice, ct);

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId, EntityType = "InvoiceLine", EntityId = line.Id.ToString(),
            Action = "LineAdded",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { invoiceId, req.Description, req.Quantity, req.UnitPrice, lineTotal }),
            PerformedByName = "platform_admin", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);

        // Recalculate with fresh lines (total was set on unsaved entity before)
        invoice.Amount = (await _db.TenantInvoiceLines.Where(l => l.InvoiceId == invoiceId).ToListAsync(ct)).Sum(l => l.LineTotal);
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(ListInvoiceLines), new { tenantId, invoiceId }, line);
    }

    [HttpPut("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/lines/{lineId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> UpdateInvoiceLine(Guid tenantId, Guid invoiceId, Guid lineId, [FromBody] UpdateInvoiceLineRequest req, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        if (invoice.Status != InvoiceStatuses.Draft)
            return BadRequest(new { message = "Line items can only be changed while the invoice is a Draft. Once issued an invoice is immutable — issue a credit note or void it to adjust." });

        var line = await _db.TenantInvoiceLines.FirstOrDefaultAsync(l => l.Id == lineId && l.InvoiceId == invoiceId, ct);
        if (line is null) return NotFound();

        if (req.Description is not null) line.Description    = req.Description.Trim();
        if (req.Quantity    is not null) line.Quantity       = req.Quantity.Value;
        if (req.UnitPrice   is not null) line.UnitPrice      = Math.Round(req.UnitPrice.Value, 2);
        if (req.DiscountAmount is not null) line.DiscountAmount = Math.Round(req.DiscountAmount.Value, 2);
        if (req.TaxRate     is not null) line.TaxRate        = Math.Round(req.TaxRate.Value, 4);
        if (req.SortOrder   is not null) line.SortOrder      = req.SortOrder.Value;

        var (taxAmount, lineTotal) = CalcLineTotals(line.UnitPrice, line.Quantity, line.DiscountAmount, line.TaxRate);
        line.TaxAmount  = taxAmount;
        line.LineTotal  = lineTotal;
        line.UpdatedAtUtc = DateTime.UtcNow;

        await RecalculateInvoiceTotalAsync(invoice, ct);

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId, EntityType = "InvoiceLine", EntityId = lineId.ToString(),
            Action = "LineUpdated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { line.Description, line.Quantity, line.UnitPrice, line.LineTotal }),
            PerformedByName = "platform_admin", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);
        return Ok(line);
    }

    [HttpDelete("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/lines/{lineId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> DeleteInvoiceLine(Guid tenantId, Guid invoiceId, Guid lineId, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        if (invoice.Status != InvoiceStatuses.Draft)
            return BadRequest(new { message = "Line items can only be changed while the invoice is a Draft. Once issued an invoice is immutable — issue a credit note or void it to adjust." });

        var line = await _db.TenantInvoiceLines.FirstOrDefaultAsync(l => l.Id == lineId && l.InvoiceId == invoiceId, ct);
        if (line is null) return NotFound();

        _db.TenantInvoiceLines.Remove(line);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId, EntityType = "InvoiceLine", EntityId = lineId.ToString(),
            Action = "LineDeleted",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { line.Description, line.LineTotal }),
            PerformedByName = "platform_admin", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);

        // Recalculate total after deletion
        invoice.Amount = (await _db.TenantInvoiceLines.Where(l => l.InvoiceId == invoiceId).SumAsync(l => l.LineTotal, ct));
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Payments ──────────────────────────────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}/payments")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> ListTenantPayments(Guid tenantId, CancellationToken ct)
    {
        if (!await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct)) return NotFound();
        var payments = await _db.TenantPayments
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync(ct);
        return Ok(payments);
    }

    [HttpGet("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/payments")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> ListInvoicePayments(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        var payments = await _db.TenantPayments
            .AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync(ct);
        return Ok(payments);
    }

    [HttpPost("tenants/{tenantId:guid}/invoices/{invoiceId:guid}/payments")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> CreatePayment(Guid tenantId, Guid invoiceId, [FromBody] CreatePaymentRequest req, CancellationToken ct)
    {
        var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        if (req.Amount <= 0)
            return BadRequest(new { message = "Payment amount must be greater than 0." });

        var receiverIdClaim = HttpContext.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        Guid.TryParse(receiverIdClaim, out var receiverId);

        var payment = new TenantPayment
        {
            TenantId  = tenantId,
            InvoiceId = invoiceId,
            Amount    = Math.Round(req.Amount, 2),
            CurrencyCode = string.IsNullOrWhiteSpace(req.CurrencyCode) ? invoice.CurrencyCode : req.CurrencyCode.Trim().ToUpperInvariant(),
            Method    = req.Method.Trim(),
            Reference = req.Reference?.Trim(),
            Status    = req.Status ?? PaymentStatuses.Completed,
            PaidAt    = req.PaidAt ?? (req.Status == PaymentStatuses.Completed ? DateTime.UtcNow : null),
            ReceivedByPlatformUserId = receiverId == Guid.Empty ? null : receiverId,
            Notes     = req.Notes?.Trim(),
            CreatedBy = receiverId == Guid.Empty ? null : receiverId,
        };
        _db.TenantPayments.Add(payment);

        // Derive invoice status from total payments
        await RecalculateInvoiceStatusAsync(invoice, ct);

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId, EntityType = "Payment", EntityId = payment.Id.ToString(),
            Action = "PaymentCreated",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { payment.Amount, payment.Method, payment.Status, payment.Reference }),
            PerformedByName = "platform_admin", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);

        // Recalculate after save (new payment row is now in DB)
        await RecalculateInvoiceStatusAsync(invoice, ct);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(ListInvoicePayments), new { tenantId, invoiceId }, payment);
    }

    [HttpPut("tenants/{tenantId:guid}/payments/{paymentId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> UpdatePayment(Guid tenantId, Guid paymentId, [FromBody] UpdatePaymentRequest req, CancellationToken ct)
    {
        var payment = await _db.TenantPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.TenantId == tenantId, ct);
        if (payment is null) return NotFound();

        var oldStatus = payment.Status;
        if (req.Status    is not null) payment.Status    = req.Status;
        if (req.Amount    is not null) payment.Amount    = Math.Round(req.Amount.Value, 2);
        if (req.Reference is not null) payment.Reference = req.Reference.Trim();
        if (req.PaidAt    is not null) payment.PaidAt    = req.PaidAt;
        if (req.Notes     is not null) payment.Notes     = req.Notes.Trim();
        payment.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId, EntityType = "Payment", EntityId = paymentId.ToString(),
            Action = "PaymentUpdated",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { status = oldStatus }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { payment.Status, payment.Amount, payment.Reference }),
            PerformedByName = "platform_admin", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);

        if (payment.InvoiceId.HasValue)
        {
            var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == payment.InvoiceId.Value, ct);
            if (invoice is not null)
            {
                await RecalculateInvoiceStatusAsync(invoice, ct);
                await _db.SaveChangesAsync(ct);
            }
        }

        return Ok(payment);
    }

    [HttpDelete("tenants/{tenantId:guid}/payments/{paymentId:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> DeletePayment(Guid tenantId, Guid paymentId, CancellationToken ct)
    {
        var payment = await _db.TenantPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.TenantId == tenantId, ct);
        if (payment is null) return NotFound();

        var invoiceId = payment.InvoiceId;
        _db.TenantPayments.Remove(payment);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId, EntityType = "Payment", EntityId = paymentId.ToString(),
            Action = "PaymentDeleted",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { payment.Amount, payment.Method, payment.Status }),
            PerformedByName = "platform_admin", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });
        await _db.SaveChangesAsync(ct);

        if (invoiceId.HasValue)
        {
            var invoice = await _db.TenantInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId.Value, ct);
            if (invoice is not null)
            {
                await RecalculateInvoiceStatusAsync(invoice, ct);
                await _db.SaveChangesAsync(ct);
            }
        }
        return NoContent();
    }

    // ── Login Activity ─────────────────────────────────────────────────────────

    [HttpGet("login-activity")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support, PlatformRoles.Auditor)]
    public async Task<IActionResult> ListLoginActivity(
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? userId,
        [FromQuery] string? eventType,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page     = Math.Max(1, page);

        var q = _db.LoginActivities.AsNoTracking().AsQueryable();

        if (tenantId.HasValue)   q = q.Where(a => a.TenantId == tenantId.Value);
        if (userId.HasValue)     q = q.Where(a => a.UserId == userId.Value);
        if (!string.IsNullOrWhiteSpace(eventType))
            q = q.Where(a => a.EventType == eventType);
        if (from.HasValue) q = q.Where(a => a.OccurredAtUtc >= from.Value);
        if (to.HasValue)   q = q.Where(a => a.OccurredAtUtc <= to.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(a => a.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    // ── Marketing / Announcements ─────────────────────────────────────────────

    [HttpGet("marketing/announcements")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> ListAnnouncements([FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.PlatformAnnouncements.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(a => a.Status == status);

        var items = await q.OrderByDescending(a => a.CreatedAtUtc).ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("marketing/announcements")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> CreateAnnouncement([FromBody] CreateAnnouncementRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { message = "Title is required." });
        if (string.IsNullOrWhiteSpace(req.Body))
            return BadRequest(new { message = "Body is required." });

        var creatorEmail = HttpContext.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value
            ?? HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? "platform";

        var announcement = new PlatformAnnouncement
        {
            Title = req.Title.Trim(),
            Body = req.Body.Trim(),
            TargetPlan = string.IsNullOrWhiteSpace(req.TargetPlan) ? "All" : req.TargetPlan.Trim(),
            Status = string.IsNullOrWhiteSpace(req.Status) ? "Draft" : req.Status.Trim(),
            PublishedAtUtc = req.PublishedAtUtc,
            ExpiresAtUtc = req.ExpiresAtUtc,
            CreatedByEmail = creatorEmail
        };
        _db.PlatformAnnouncements.Add(announcement);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformAnnouncement",
            EntityId        = announcement.Id.ToString(),
            Action          = "AnnouncementCreated",
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { announcement.Title, announcement.Status, announcement.TargetPlan }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Ok(announcement);
    }

    [HttpPatch("marketing/announcements/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> PatchAnnouncement(Guid id, [FromBody] PatchAnnouncementRequest req, CancellationToken ct)
    {
        var ann = await _db.PlatformAnnouncements.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ann is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.Title)) ann.Title = req.Title.Trim();
        if (!string.IsNullOrWhiteSpace(req.Body)) ann.Body = req.Body.Trim();
        if (req.Status is not null) ann.Status = req.Status.Trim();
        if (req.ExpiresAtUtc.HasValue) ann.ExpiresAtUtc = req.ExpiresAtUtc;
        ann.UpdatedAtUtc = DateTime.UtcNow;

        if (ann.Status == "Published" && ann.PublishedAtUtc is null)
            ann.PublishedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformAnnouncement",
            EntityId        = id.ToString(),
            Action          = ann.Status == "Published" ? "AnnouncementPublished" : "AnnouncementUpdated",
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { ann.Status, ann.Title }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Ok(ann);
    }

    [HttpDelete("marketing/announcements/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> DeleteAnnouncement(Guid id, CancellationToken ct)
    {
        var ann = await _db.PlatformAnnouncements.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ann is null) return NotFound();

        // Soft delete: archive
        ann.Status = "Archived";
        ann.UpdatedAtUtc = DateTime.UtcNow;
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformAnnouncement",
            EntityId        = id.ToString(),
            Action          = "AnnouncementArchived",
            OldValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { ann.Title }),
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { Status = "Archived" }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { id, status = "Archived" });
    }

    // ── Leads ─────────────────────────────────────────────────────────────────

    [HttpGet("leads")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> ListLeads([FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.PlatformLeads.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(l => l.Status == status);

        var items = await q.OrderByDescending(l => l.CreatedAtUtc).ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("leads")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> CreateLead([FromBody] CreateLeadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.CompanyName))
            return BadRequest(new { message = "CompanyName is required." });
        if (string.IsNullOrWhiteSpace(req.ContactEmail) || !req.ContactEmail.Contains('@'))
            return BadRequest(new { message = "A valid ContactEmail is required." });

        var lead = new PlatformLead
        {
            CompanyName = req.CompanyName.Trim(),
            ContactName = (req.ContactName ?? req.ContactEmail).Trim(),
            ContactEmail = req.ContactEmail.Trim().ToLowerInvariant(),
            Phone = req.Phone?.Trim(),
            Message = req.Message?.Trim(),
            Source = string.IsNullOrWhiteSpace(req.Source) ? "Manual" : req.Source.Trim()
        };
        _db.PlatformLeads.Add(lead);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformLead",
            EntityId        = lead.Id.ToString(),
            Action          = "LeadCreated",
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { lead.CompanyName, lead.ContactEmail, lead.Source }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Ok(lead);
    }

    [HttpPatch("leads/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> PatchLead(Guid id, [FromBody] PatchLeadRequest req, CancellationToken ct)
    {
        var lead = await _db.PlatformLeads.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        var oldStatus = lead.Status;
        if (req.Status is not null) lead.Status = req.Status.Trim();
        if (req.Notes is not null) lead.Notes = req.Notes.Trim();
        if (req.AssignedTo is not null) lead.AssignedTo = req.AssignedTo.Trim();
        lead.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformLead",
            EntityId        = id.ToString(),
            Action          = "LeadUpdated",
            OldValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { Status = oldStatus }),
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { lead.Status, lead.AssignedTo }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Ok(lead);
    }

    [HttpPost("leads/{id:guid}/convert")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Marketing)]
    public async Task<IActionResult> ConvertLead(Guid id, [FromBody] ConvertLeadRequest req, CancellationToken ct)
    {
        var lead = await _db.PlatformLeads.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound(new { message = "Lead not found." });
        if (lead.ConvertedToTenantId is not null)
            return BadRequest(new { message = "Lead has already been converted to a tenant." });

        // Reuse the existing CreateTenant logic inline
        var name = req.TenantName?.Trim();
        var slug = req.TenantSlug?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "TenantName is required." });
        if (string.IsNullOrWhiteSpace(slug))
            return BadRequest(new { message = "TenantSlug is required." });
        if (string.IsNullOrWhiteSpace(req.AdminEmail) || !req.AdminEmail.Contains('@'))
            return BadRequest(new { message = "A valid AdminEmail is required." });
        if (string.IsNullOrWhiteSpace(req.AdminPassword) || req.AdminPassword.Length < 10)
            return BadRequest(new { message = "AdminPassword must be at least 10 characters." });

        if (await _db.Tenants.AsNoTracking().AnyAsync(t => t.Slug == slug && t.IsActive, ct))
            return Conflict(new { message = $"A tenant with slug '{slug}' already exists." });

        var tenant = new Tenant { Name = name, Slug = slug };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        var adminRole = await _authSeeder.EnsureTenantRolesAsync(tenant.Id, ct);
        // Phase 2: eager-seed the GL defaults (chart of accounts, tenant-default mappings, the 17
        // system posting drivers, and the client-configurable rate allow-list) for the new tenant.
        await Zayra.Api.Infrastructure.Seed.GlDriverSeeder.SeedTenantDefaultsAsync(_db, tenant.Id, ct);
        // Configurability keystone: eager-seed the data-driven pay-component defaults (the system rows
        // that reproduce the current payslip earning/deduction set) so a new tenant is born with an
        // editable component catalog rather than a hard-coded sequence.
        await Zayra.Api.Infrastructure.Seed.PayComponentSeeder.SeedTenantDefaultsAsync(_db, tenant.Id, ct);
        // FIX 1 (C1): the idempotent per-tenant provisioning bundle — country rules, MasterData,
        // HR categories, default attendance/leave/approval policies, and bilingual notification
        // templates. Ensures no tenant is born without its statutory/reference/config foundation.
        await Zayra.Api.Infrastructure.Seed.TenantProvisioningBundle.ProvisionAsync(_db, tenant.Id, ct);
        await _db.SaveChangesAsync(ct);
        var plan = string.IsNullOrWhiteSpace(req.Plan) ? "Trial" : req.Plan;
        var (defaultMaxUsers, defaultMaxEmployees) = SubscriptionTiers.GetDefaults(plan);

        var admin = new User
        {
            TenantId = tenant.Id,
            Email = req.AdminEmail.Trim().ToLowerInvariant(),
            NormalizedEmail = Infrastructure.Auth.AuthService.Normalize(req.AdminEmail),
            FullName = lead.ContactName,
            PasswordHash = _passwordHasher.Hash(req.AdminPassword),
            AccessMode = "FullPortal",
            Status = "Active",
            IsActive = true,
            IsEmailConfirmed = true,
            IsGroupScope = true
        };
        admin.UserRoles.Add(new UserRole { User = admin, Role = adminRole });
        _db.Users.Add(admin);

        _db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenant.Id,
            Plan = plan,
            Status = "Active",
            MaxUsers = defaultMaxUsers,
            MaxEmployees = defaultMaxEmployees,
            BillingEmail = req.BillingEmail ?? req.AdminEmail.Trim().ToLowerInvariant(),
            BillingCycle = "Monthly",
            MonthlyAmount = 0,
            CurrencyCode = "USD"
        });

        lead.ConvertedToTenantId = tenant.Id;
        lead.Status = "Converted";
        lead.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(new { leadId = id, tenantId = tenant.Id, tenantSlug = tenant.Slug, adminEmail = admin.Email });
    }

    // ── Feature Flags Catalog ─────────────────────────────────────────────────

    [HttpGet("feature-flags")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public IActionResult GetFeatureFlags()
    {
        // Canonical list of feature flag keys and their display names.
        // This is the single source of truth shared between backend and frontend.
        var flags = new[]
        {
            new { key = "payroll",                   label = "Payroll",                   category = "Core" },
            new { key = "recruitment",               label = "Recruitment",               category = "Core" },
            new { key = "performance",               label = "Performance",               category = "Core" },
            new { key = "compliance",                label = "Compliance",                category = "Core" },
            new { key = "finance",                   label = "Finance & GL",              category = "Finance" },
            new { key = "shifts",                    label = "Shifts & Rosters",          category = "Time" },
            new { key = "overtime",                  label = "Overtime",                  category = "Time" },
            new { key = "ai_assistant",              label = "AI Assistant",              category = "Intelligence" },
            new { key = "resume_screening",          label = "AI Resume Screening",       category = "Intelligence" },
            new { key = "payroll_ai_validation",     label = "AI Payroll Validation",     category = "Intelligence" },
            new { key = "risk_scores",               label = "Employee Risk Scores",      category = "Intelligence" },
            new { key = "wps_export",                label = "WPS Export",                category = "Compliance" },
            new { key = "eosb_calc",                 label = "EOSB Calculation",          category = "Compliance" },
            new { key = "qiwa_integration",          label = "Qiwa Integration",          category = "Compliance" },
            new { key = "hijri_calendar",            label = "Hijri Calendar",            category = "Localisation" },
            new { key = "mobile_app",                label = "Mobile App",                category = "Access" },
        };
        return Ok(flags);
    }

    // ── Platform Settings ─────────────────────────────────────────────────────

    [HttpGet("settings")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> GetSettings(
        [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection,
        CancellationToken ct)
    {
        var smtp = await PlatformSmtpConfig.LoadAsync(_db, protection, _config, _log, ct);
        var preset = EmailProviderPresets.ByKey(smtp.ProviderKey);

        return Ok(new
        {
            smtp = new
            {
                host      = smtp.Host,
                port      = smtp.Port,
                username  = smtp.Username,
                useSsl    = smtp.UseTls,
                fromEmail = smtp.FromAddress,
                fromName  = smtp.FromName,
                provider  = smtp.ProviderKey,
                // Never return the actual password — only whether one is stored.
                password  = string.IsNullOrEmpty(smtp.Password) ? "" : "***",
                hasPassword = !string.IsNullOrEmpty(smtp.Password),
                // The banner used to read settings.smtp.isConfigured, which this endpoint never
                // returned — so it was always undefined and the amber "SMTP not configured" warning
                // showed even on a fully configured relay. It is a real value now.
                isConfigured = smtp.IsUsable,
                // Where each value came from, so the admin can tell saved config from an env fallback.
                source = smtp.Source,
                providerLabel = preset?.Label,
            },
            ai = new
            {
                model = _config["Ai:ModelId"] ?? _config["AI:ModelId"] ?? "claude-3-5-sonnet-20241022"
            },
            platform = new
            {
                trialDurationDays = int.TryParse(_config["Platform:TrialDurationDays"], out var td) ? td : 14,
                environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"
            }
        });
    }

    /// <summary>
    /// Auto-configuration catalog for the SMTP form: picking a provider fills in host, port and TLS
    /// so an admin never has to look up "what is GoDaddy's SMTP server" mid-setup. The credentials
    /// are still theirs to supply, and nothing is trusted until the test email lands.
    /// </summary>
    [HttpGet("settings/email-providers")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public IActionResult GetEmailProviders() =>
        Ok(EmailProviderPresets.All.Select(p => new
        {
            key            = p.Key,
            label          = p.Label,
            host           = p.Host,
            port           = p.Port,
            useSsl         = p.UseTls,
            usernamePattern = p.UsernamePattern,
            guidance       = p.Guidance,
            alternatePorts = p.AlternatePorts,
            docsUrl        = p.DocsUrl,
            category       = p.Category,
        }));

    [HttpPut("settings/smtp")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateSmtp(
        [FromBody] UpdateSmtpRequest req,
        [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Host))
            return BadRequest(new { message = "SMTP host is required." });
        if (req.Port is < 1 or > 65535)
            return BadRequest(new { message = "Port must be between 1 and 65535." });

        // A blank From address is not a partial save — MimeKit throws on it, so the relay would
        // accept the config and then fail on every send. Rejected here instead.
        if (string.IsNullOrWhiteSpace(req.FromEmail) || !IsValidEmail(req.FromEmail))
            return BadRequest(new { message = "A valid From Email is required — it is the address recipients will see." });

        var providerKey = EmailProviderPresets.ByKey(req.Provider)?.Key
                          ?? EmailProviderPresets.DetectByHost(req.Host)?.Key
                          ?? EmailProviderPresets.CustomKey;

        await UpsertConfigAsync(PlatformSmtpConfig.KeyHost,        req.Host.Trim(), ct);
        await UpsertConfigAsync(PlatformSmtpConfig.KeyPort,        req.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await UpsertConfigAsync(PlatformSmtpConfig.KeyUsername,    req.Username?.Trim() ?? "", ct);
        await UpsertConfigAsync(PlatformSmtpConfig.KeyFromAddress, req.FromEmail.Trim(), ct);
        await UpsertConfigAsync(PlatformSmtpConfig.KeyFromName,    req.FromName?.Trim() ?? "", ct);
        await UpsertConfigAsync(PlatformSmtpConfig.KeyUseTls,      req.UseSsl ? "true" : "false", ct);
        await UpsertConfigAsync(PlatformSmtpConfig.KeyProvider,    providerKey, ct);

        // Password: only update if a new one was supplied. A blank field means "keep the current
        // one", which is what the form's placeholder promises.
        if (!string.IsNullOrEmpty(req.Password) && req.Password != "***")
        {
            // Was Base64 — which the old code itself flagged as obfuscation, not encryption.
            // Now IDataProtection, the same mechanism the notification-provider secrets use.
            await UpsertConfigAsync(PlatformSmtpConfig.KeyPassword,
                PlatformSmtpConfig.ProtectPassword(protection, req.Password), ct);
        }

        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Platform SMTP saved: provider={Provider} host={Host} port={Port} tls={Tls}",
            providerKey, req.Host, req.Port, req.UseSsl);

        return Ok(new
        {
            message = "SMTP configuration saved.",
            host = req.Host, port = req.Port, useSsl = req.UseSsl, provider = providerKey,
        });
    }

    /// <summary>
    /// Sends a real message through the saved relay. The recipient is chosen by the admin so the
    /// test can be delivered to an inbox they can actually open — a test that only ever goes to the
    /// signed-in account cannot prove deliverability to a customer domain.
    /// </summary>
    [HttpPost("settings/smtp/test")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> TestSmtp(
        [FromBody] TestSmtpRequest? req,
        [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection,
        CancellationToken ct)
    {
        var smtp = await PlatformSmtpConfig.LoadAsync(_db, protection, _config, _log, ct);
        if (!smtp.IsUsable)
            return BadRequest(new
            {
                sent = false,
                message = "SMTP is not configured yet. Fill in the host and From Email above, save, then test.",
            });

        // Default to the signed-in admin; the form pre-fills this, so an explicit value is the norm.
        var to = req?.To?.Trim();
        if (string.IsNullOrWhiteSpace(to))
            to = HttpContext.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value
                 ?? HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        if (string.IsNullOrWhiteSpace(to))
            return BadRequest(new { sent = false, message = "Enter the email address the test should be delivered to." });
        if (!IsValidEmail(to))
            return BadRequest(new { sent = false, message = $"\"{to}\" is not a valid email address." });

        var sentAtUtc = DateTime.UtcNow;
        var providerLabel = EmailProviderPresets.ByKey(smtp.ProviderKey)?.Label ?? smtp.ProviderKey;

        try
        {
            await _emailService.SendPlatformAsync(
                to,
                "KynexOne Platform Admin",
                "KynexOne — SMTP test message",
                $"""
                 <p>This is a test message from <strong>KynexOne Platform Settings</strong>.</p>
                 <p>If you are reading it, outbound email is working: invoices, password resets and
                 notifications will reach their recipients.</p>
                 <table cellpadding="4" style="font-family:system-ui,sans-serif;font-size:13px;border-collapse:collapse">
                   <tr><td><strong>Provider</strong></td><td>{System.Net.WebUtility.HtmlEncode(providerLabel)}</td></tr>
                   <tr><td><strong>Relay</strong></td><td>{System.Net.WebUtility.HtmlEncode(smtp.Host)}:{smtp.Port}</td></tr>
                   <tr><td><strong>Encryption</strong></td><td>{(smtp.Port == 465 ? "Implicit TLS" : smtp.UseTls ? "STARTTLS" : "None")}</td></tr>
                   <tr><td><strong>From</strong></td><td>{System.Net.WebUtility.HtmlEncode(smtp.FromAddress)}</td></tr>
                   <tr><td><strong>Sent at (UTC)</strong></td><td>{sentAtUtc:yyyy-MM-dd HH:mm:ss}</td></tr>
                 </table>
                 <p style="color:#64748b;font-size:12px">If this landed in spam, add an SPF record for
                 {System.Net.WebUtility.HtmlEncode(smtp.Host)} and a DKIM record from your provider.</p>
                 """,
                cancellationToken: ct);

            _log.LogInformation("Platform SMTP test sent via {Host}:{Port}.", smtp.Host, smtp.Port);

            return Ok(new
            {
                sent = true,
                to,
                host = smtp.Host,
                port = smtp.Port,
                provider = smtp.ProviderKey,
                sentAtUtc,
                message = $"Test email accepted by {smtp.Host} for {to}. Open that inbox to confirm it arrived — check spam if it is not there within a minute.",
            });
        }
        catch (Exception ex)
        {
            // The relay's own words are the single most useful thing here ("535 authentication
            // failed", "relay access denied"), so they are surfaced rather than swallowed.
            _log.LogWarning(ex, "Platform SMTP test failed via {Host}:{Port}.", smtp.Host, smtp.Port);
            return Ok(new
            {
                sent = false,
                to,
                host = smtp.Host,
                port = smtp.Port,
                provider = smtp.ProviderKey,
                error = ex.Message,
                message = $"Could not send through {smtp.Host}:{smtp.Port} — {ex.Message}",
            });
        }
    }

    /// <summary>Strict enough to catch typos, lenient enough not to reject valid unusual addresses.</summary>
    private static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains(' ') || value.Contains(',') || value.Contains(';')) return false;
        try
        {
            var parsed = new System.Net.Mail.MailAddress(value.Trim());
            return parsed.Address == value.Trim() && parsed.Host.Contains('.');
        }
        catch (FormatException) { return false; }
    }


    [HttpGet("settings/version")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Auditor)]
    public async Task<IActionResult> GetVersion(CancellationToken ct)
    {
        string? lastMigration = null;
        try
        {
            var migrations = await _db.Database.GetAppliedMigrationsAsync(ct);
            lastMigration = migrations.LastOrDefault();
        }
        catch { /* non-fatal */ }

        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVersion = assembly.GetName().Version?.ToString() ?? "unknown";
        var infoVersion = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)
            ?.InformationalVersion ?? fileVersion;

        return Ok(new
        {
            version     = infoVersion,
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            dotnetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            lastMigration,
            checkedAtUtc = DateTime.UtcNow
        });
    }

    // ── Billing Summary ───────────────────────────────────────────────────────

    [HttpGet("billing/summary")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> BillingSummary(CancellationToken ct)
    {
        var allSubs = await _db.TenantSubscriptions
            .AsNoTracking()
            .Select(s => new { s.Status, s.MonthlyAmount })
            .ToListAsync(ct);

        var totalMrr = allSubs
            .Where(s => s.Status is "Active" or "Trial" or "PastDue")
            .Sum(s => s.MonthlyAmount);
        var totalArr = totalMrr * 12;

        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var allInvoices = await _db.TenantInvoices
            .AsNoTracking()
            .Select(i => new { i.Status, i.Amount, i.CreatedAtUtc, i.UpdatedAtUtc })
            .ToListAsync(ct);

        var overdueTotal = allInvoices.Where(i => i.Status == "Overdue").Sum(i => i.Amount);
        var overdueCount = allInvoices.Count(i => i.Status == "Overdue");
        var totalInvoices = allInvoices.Count;
        var paidThisMonth = allInvoices.Count(i => i.Status == "Paid" && i.UpdatedAtUtc >= startOfMonth);
        var sentThisMonth = allInvoices.Count(i => i.Status == "Sent" && i.CreatedAtUtc >= startOfMonth);

        return Ok(new
        {
            totalMrr,
            totalArr,
            overdueTotal,
            overdueCount,
            totalInvoices,
            paidThisMonth,
            sentThisMonth
        });
    }

    [HttpGet("billing/invoices")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> AllInvoices(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (pageSize > 200) pageSize = 200;

        // Build tenant name lookup
        var tenants = await _db.Tenants.AsNoTracking()
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var q = _db.TenantInvoices.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(i => i.Status == status);

        var total = await q.CountAsync(ct);
        var invoices = await q
            .OrderByDescending(i => i.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var result = invoices.Select(i => new
        {
            i.Id,
            i.TenantId,
            tenantName = tenants.TryGetValue(i.TenantId, out var n) ? n : null,
            i.InvoiceNumber,
            i.Amount,
            i.CurrencyCode,
            i.Status,
            i.InvoiceDate,
            i.DueDate,
            i.PaidDate,
            i.PaymentMethod,
            i.PeriodDescription,
            i.CreatedAtUtc
        });

        return Ok(new { total, page, pageSize, invoices = result });
    }

    // ── Security Center ───────────────────────────────────────────────────────

    [HttpGet("security/summary")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> SecuritySummary(CancellationToken ct)
    {
        var tenants = await _db.Tenants.AsNoTracking()
            .Select(t => new { t.Id, t.Name, t.IsActive })
            .ToListAsync(ct);

        var tenantIds = tenants.Select(t => t.Id).ToList();

        var securitySettings = await _db.SecuritySettings
            .AsNoTracking()
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToDictionaryAsync(s => s.TenantId, ct);

        var subs = await _db.TenantSubscriptions
            .AsNoTracking()
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToDictionaryAsync(s => s.TenantId, ct);

        var result = tenants.Select(t =>
        {
            securitySettings.TryGetValue(t.Id, out var sec);
            subs.TryGetValue(t.Id, out var sub);

            // Risk heuristic: suspended or expired → high; default policy still in place → medium; secure → low
            var riskLevel = "Low";
            if (sub?.Status == "Suspended") riskLevel = "High";
            else if (sec is null) riskLevel = "Medium";

            return new
            {
                tenantId = t.Id,
                tenantName = t.Name,
                isActive = t.IsActive,
                subscriptionStatus = sub?.Status,
                hasMfaEnabled = sec?.MfaRequired ?? false,
                mfaStatus = sec?.MfaRequired == true ? "required_for_all" : "optional",
                hasCustomPasswordPolicy = sec is not null && (sec.PasswordMinLength != 10 || !sec.PasswordRequireUppercase),
                maxFailedLoginAttempts = sec?.MaxFailedLoginAttempts ?? 5,
                sessionTimeoutMinutes = sec?.SessionTimeoutMinutes ?? 480,
                riskLevel
            };
        });

        return Ok(result);
    }

    // ── Tenant Security Policy ────────────────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}/security-policy")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Auditor)]
    public async Task<IActionResult> GetTenantSecurityPolicy(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound();

        var sec = await _db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        return Ok(new
        {
            tenantId,
            tenantName = tenant.Name,
            passwordMinLength          = sec?.PasswordMinLength ?? 10,
            passwordRequireUppercase   = sec?.PasswordRequireUppercase ?? true,
            passwordRequireLowercase   = sec?.PasswordRequireLowercase ?? true,
            passwordRequireDigit       = sec?.PasswordRequireDigit ?? true,
            passwordRequireSpecial     = sec?.PasswordRequireSpecial ?? true,
            passwordExpiryDays         = sec?.PasswordExpiryDays ?? 90,
            passwordHistoryCount       = sec?.PasswordHistoryCount ?? 5,
            maxFailedLoginAttempts     = sec?.MaxFailedLoginAttempts ?? 5,
            lockoutDurationMinutes     = sec?.LockoutDurationMinutes ?? 30,
            sessionTimeoutMinutes      = sec?.SessionTimeoutMinutes ?? 480,
            refreshTokenExpiryDays     = sec?.RefreshTokenExpiryDays ?? 30,
            allowMultipleSessions      = sec?.AllowMultipleSessions ?? true,
            isCustomPolicy             = sec is not null,
            updatedAtUtc               = sec?.UpdatedAtUtc
        });
    }

    [HttpPut("tenants/{tenantId:guid}/security-policy")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateTenantSecurityPolicy(Guid tenantId, [FromBody] UpdateSecurityPolicyRequest body, CancellationToken ct)
    {
        if (!await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct)) return NotFound();

        var sec = await _db.SecuritySettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (sec is null)
        {
            sec = new SecuritySetting { TenantId = tenantId };
            _db.SecuritySettings.Add(sec);
        }

        if (body.PasswordMinLength.HasValue)          sec.PasswordMinLength          = Math.Max(6, Math.Min(32, body.PasswordMinLength.Value));
        if (body.PasswordRequireUppercase.HasValue)   sec.PasswordRequireUppercase   = body.PasswordRequireUppercase.Value;
        if (body.PasswordRequireLowercase.HasValue)   sec.PasswordRequireLowercase   = body.PasswordRequireLowercase.Value;
        if (body.PasswordRequireDigit.HasValue)       sec.PasswordRequireDigit       = body.PasswordRequireDigit.Value;
        if (body.PasswordRequireSpecial.HasValue)     sec.PasswordRequireSpecial     = body.PasswordRequireSpecial.Value;
        if (body.PasswordExpiryDays.HasValue)         sec.PasswordExpiryDays         = Math.Max(0, body.PasswordExpiryDays.Value);
        if (body.PasswordHistoryCount.HasValue)       sec.PasswordHistoryCount       = Math.Max(0, Math.Min(24, body.PasswordHistoryCount.Value));
        if (body.MaxFailedLoginAttempts.HasValue)     sec.MaxFailedLoginAttempts     = Math.Max(3, Math.Min(20, body.MaxFailedLoginAttempts.Value));
        if (body.LockoutDurationMinutes.HasValue)     sec.LockoutDurationMinutes     = Math.Max(5, body.LockoutDurationMinutes.Value);
        if (body.SessionTimeoutMinutes.HasValue)      sec.SessionTimeoutMinutes      = Math.Max(15, body.SessionTimeoutMinutes.Value);
        if (body.RefreshTokenExpiryDays.HasValue)     sec.RefreshTokenExpiryDays     = Math.Max(1, body.RefreshTokenExpiryDays.Value);
        if (body.AllowMultipleSessions.HasValue)      sec.AllowMultipleSessions      = body.AllowMultipleSessions.Value;

        sec.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _db.AuditLogs.AddAsync(new AuditLog
        {
            TenantId     = tenantId,
            UserId       = Guid.Empty,
            Action       = "platform.security_policy.updated",
            EntityName   = "SecuritySetting",
            EntityId     = sec.Id.ToString(),
            Metadata     = $"{{\"action\":\"security_policy.updated\",\"tenantId\":\"{tenantId}\"}}",
            IpAddress    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            CreatedAtUtc = DateTime.UtcNow
        }, ct);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    private static InvoiceData BuildInvoiceData(
        TenantInvoice invoice,
        Tenant? tenant,
        TenantSubscription? sub,
        IReadOnlyList<TenantInvoiceLine>? lines = null)
    {
        List<InvoiceLineItem> lineItems;

        if (lines is { Count: > 0 })
        {
            lineItems = lines
                .OrderBy(l => l.SortOrder)
                .Select(l => new InvoiceLineItem(
                    l.Description,
                    l.Quantity,
                    l.UnitPrice,
                    l.DiscountAmount,
                    l.TaxAmount,
                    l.LineTotal))
                .ToList();
        }
        else
        {
            // Backward-compat fallback: legacy invoices with no line items
            lineItems =
            [
                new(
                    $"KynexOne Workforce Platform — {sub?.Plan ?? "Subscription"}" +
                    (invoice.PeriodDescription is { Length: > 0 } p ? $" ({p})" : string.Empty),
                    Quantity:       1,
                    UnitPrice:      invoice.Amount,
                    DiscountAmount: 0m,
                    TaxAmount:      0m,
                    LineTotal:      invoice.Amount)
            ];
        }

        return new InvoiceData(
            InvoiceNumber:     invoice.InvoiceNumber,
            InvoiceDate:       invoice.InvoiceDate,
            DueDate:           invoice.DueDate,
            PeriodDescription: invoice.PeriodDescription,
            Amount:            invoice.Amount,
            CurrencyCode:      invoice.CurrencyCode,
            TenantName:        tenant?.Name ?? "Client",
            BillingEmail:      sub?.BillingEmail ?? string.Empty,
            BillingCycle:      sub?.BillingCycle ?? string.Empty,
            Notes:             invoice.Notes,
            LineItems:         lineItems
        );
    }

    private async Task<List<TenantInvoiceLine>> LoadInvoiceLinesAsync(Guid invoiceId, CancellationToken ct)
        => await _db.TenantInvoiceLines
            .AsNoTracking()
            .Where(l => l.InvoiceId == invoiceId)
            .OrderBy(l => l.SortOrder)
            .ToListAsync(ct);

    private async Task RecalculateInvoiceTotalAsync(TenantInvoice invoice, CancellationToken ct)
    {
        var lines = await _db.TenantInvoiceLines
            .Where(l => l.InvoiceId == invoice.Id)
            .ToListAsync(ct);
        invoice.Amount = lines.Sum(l => l.LineTotal);
        invoice.UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Next gap-less sequential invoice number for a tenant+year, e.g. INV-2026-0007.</summary>
    private async Task<string> GenerateInvoiceNumberAsync(Guid tenantId, int year, CancellationToken ct)
    {
        var prefix = $"INV-{year}-";
        var existing = await _db.TenantInvoices
            .Where(i => i.TenantId == tenantId && i.InvoiceNumber.StartsWith(prefix))
            .Select(i => i.InvoiceNumber)
            .ToListAsync(ct);
        var maxSeq = existing
            .Select(s => int.TryParse(s[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefix}{maxSeq + 1:D4}";
    }

    /// <summary>Effective status for display: an issued, not-fully-paid invoice past its due date
    /// reads as Overdue without mutating the stored status.</summary>
    private static string EffectiveInvoiceStatus(TenantInvoice i)
    {
        var isOpen = i.Status is InvoiceStatuses.Sent or InvoiceStatuses.PartiallyPaid;
        if (isOpen && i.DueDate < DateOnly.FromDateTime(DateTime.UtcNow.Date))
            return InvoiceStatuses.Overdue;
        return i.Status;
    }

    private async Task RecalculateInvoiceStatusAsync(TenantInvoice invoice, CancellationToken ct)
    {
        if (invoice.Status is InvoiceStatuses.Draft or InvoiceStatuses.Cancelled)
            return;

        var totalPaid = await _db.TenantPayments
            .Where(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatuses.Completed)
            .SumAsync(p => p.Amount, ct);

        if (totalPaid >= invoice.Amount && invoice.Amount > 0)
        {
            invoice.Status = InvoiceStatuses.Paid;
            invoice.PaidDate ??= DateOnly.FromDateTime(DateTime.UtcNow);
        }
        else if (totalPaid > 0)
        {
            invoice.Status = InvoiceStatuses.PartiallyPaid;
        }
        else if (invoice.Status == InvoiceStatuses.Paid || invoice.Status == InvoiceStatuses.PartiallyPaid)
        {
            invoice.Status = InvoiceStatuses.Sent;
            invoice.PaidDate = null;
        }
        invoice.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static (decimal taxAmount, decimal lineTotal) CalcLineTotals(
        decimal unitPrice, int quantity, decimal discount, decimal taxRate)
    {
        var sub = unitPrice * quantity - discount;
        var tax = sub * taxRate / 100m;
        return (Math.Round(tax, 2), Math.Round(sub + tax, 2));
    }

    // ── Tenant AI Usage (platform view) ──────────────────────────────────────

    [HttpGet("tenants/{tenantId:guid}/ai-usage")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Auditor)]
    public async Task<IActionResult> GetTenantAiUsage(Guid tenantId, [FromQuery] int? yearMonth, CancellationToken ct)
    {
        if (!await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct)) return NotFound();

        var sub = await _db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var plan = sub?.Plan ?? "Starter";
        var limit = AiPlanLimits.GetMonthlyTokenLimit(plan);
        var ym = yearMonth ?? int.Parse(DateTime.UtcNow.ToString("yyyyMM"));

        var usage = await _db.TenantAiUsages
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.YearMonth == ym, ct);

        return Ok(new
        {
            tenantId,
            plan,
            yearMonth = ym,
            tokensUsed = usage?.TokensUsed ?? 0,
            requestCount = usage?.RequestCount ?? 0,
            blockedCount = usage?.BlockedCount ?? 0,
            monthlyTokenLimit = limit,
            isUnlimited = limit == 0,
            usagePct = limit > 0 ? Math.Min(100.0, (double)(usage?.TokensUsed ?? 0) / limit * 100) : 0.0
        });
    }

    // ── Platform Stats ────────────────────────────────────────────────────────

    [HttpGet("stats")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Auditor)]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var totalTenants = await _db.Tenants.AsNoTracking().CountAsync(ct);
        var activeTenants = await _db.Tenants.AsNoTracking().CountAsync(t => t.IsActive, ct);
        var totalUsers = await _db.Users.AsNoTracking().CountAsync(u => !u.IsDeleted, ct);
        var totalEmployees = await _db.Employees.AsNoTracking().CountAsync(e => !e.IsDeleted, ct);

        var allSubs = await _db.TenantSubscriptions
            .AsNoTracking()
            .Select(s => new { s.Plan, s.Status, s.MonthlyAmount, s.ExpiresAtUtc })
            .ToListAsync(ct);

        int PlanCount(string plan) => allSubs.Count(s => string.Equals(s.Plan, plan, StringComparison.OrdinalIgnoreCase));

        // MRR: active/trial subscriptions with a monthly amount
        var estimatedMrr = allSubs
            .Where(s => s.Status is "Active" or "Trial" or "PastDue")
            .Sum(s => s.MonthlyAmount);

        // Clients expiring within 7 days (non-null expiry)
        var now = DateTime.UtcNow;
        var expiringCount = allSubs.Count(s =>
            s.ExpiresAtUtc.HasValue &&
            s.ExpiresAtUtc.Value > now &&
            s.ExpiresAtUtc.Value <= now.AddDays(7));

        var suspendedCount = allSubs.Count(s => string.Equals(s.Status, "Suspended", StringComparison.OrdinalIgnoreCase));
        var overdueCount   = await _db.TenantInvoices.AsNoTracking().CountAsync(i => i.Status == "Overdue", ct);

        return Ok(new
        {
            totalTenants,
            activeTenants,
            totalUsers,
            totalEmployees,
            estimatedMrr,
            expiringCount,
            suspendedCount,
            overdueCount,
            tenantsByPlan = new
            {
                trial = PlanCount("Trial"),
                starter = PlanCount("Starter"),
                growth = PlanCount("Growth"),
                enterprise = PlanCount("Enterprise")
            }
        });
    }

    // ── Pricing Config ────────────────────────────────────────────────────────

    [HttpGet("pricing/config")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> GetPricingConfig(CancellationToken ct)
    {
        var configs = await _db.PricingConfigs.AsNoTracking().OrderBy(c => c.Group).ThenBy(c => c.Key).ToListAsync(ct);
        return Ok(configs);
    }

    [HttpPut("pricing/config/{key}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> UpdatePricingConfig(string key, [FromBody] UpdatePricingConfigRequest req, CancellationToken ct)
    {
        var config = await _db.PricingConfigs.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (config is null) return NotFound(new { message = $"Config key '{key}' not found." });
        config.Value = req.Value;
        config.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(config);
    }

    [HttpGet("pricing/modules")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> GetPricingModules(CancellationToken ct)
    {
        var modules = await _db.PricingModuleConfigs.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync(ct);
        return Ok(modules);
    }

    [HttpPut("pricing/modules/{moduleKey}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance)]
    public async Task<IActionResult> UpdatePricingModule(string moduleKey, [FromBody] UpdatePricingModuleRequest req, CancellationToken ct)
    {
        var module = await _db.PricingModuleConfigs.FirstOrDefaultAsync(m => m.ModuleKey == moduleKey, ct);
        if (module is null) return NotFound(new { message = $"Module '{moduleKey}' not found." });

        if (req.IncludedInTrial.HasValue)    module.IncludedInTrial    = req.IncludedInTrial.Value;
        if (req.IncludedInStarter.HasValue)  module.IncludedInStarter  = req.IncludedInStarter.Value;
        if (req.IncludedInGrowth.HasValue)   module.IncludedInGrowth   = req.IncludedInGrowth.Value;
        if (req.IncludedInEnterprise.HasValue) module.IncludedInEnterprise = req.IncludedInEnterprise.Value;
        if (req.IsEnterpriseOnly.HasValue)   module.IsEnterpriseOnly   = req.IsEnterpriseOnly.Value;
        if (req.AddonPriceMonthly.HasValue)  module.AddonPriceMonthly  = req.AddonPriceMonthly.Value;
        module.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(module);
    }

    // ── Pricing Quotes ────────────────────────────────────────────────────────

    [HttpGet("quotes")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Support)]
    public async Task<IActionResult> ListQuotes([FromQuery] string? status, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var q = _db.PricingQuotes.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc);
        var filtered = string.IsNullOrWhiteSpace(status) ? q : q.Where(x => x.Status == status);
        var total = await filtered.CountAsync(ct);
        var items = await filtered.Skip((page - 1) * 25).Take(25).ToListAsync(ct);
        return Ok(new { total, page, items });
    }

    [HttpGet("quotes/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Support)]
    public async Task<IActionResult> GetQuote(Guid id, CancellationToken ct)
    {
        var quote = await _db.PricingQuotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, ct);
        if (quote is null) return NotFound();
        return Ok(quote);
    }

    [HttpPatch("quotes/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Support)]
    public async Task<IActionResult> PatchQuote(Guid id, [FromBody] PatchQuoteRequest req, CancellationToken ct)
    {
        var quote = await _db.PricingQuotes.FirstOrDefaultAsync(q => q.Id == id, ct);
        if (quote is null) return NotFound();
        if (req.Status is not null) quote.Status = req.Status;
        if (req.Notes is not null) quote.Notes = req.Notes;
        quote.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(quote);
    }

    [HttpPost("quotes")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Finance, PlatformRoles.Marketing)]
    public async Task<IActionResult> CreateQuote([FromBody] CreateQuoteRequest req, CancellationToken ct)
    {
        var quote = new PricingQuote
        {
            CompanyName           = req.CompanyName.Trim(),
            ContactName           = req.ContactName?.Trim() ?? string.Empty,
            ContactEmail          = req.ContactEmail.Trim(),
            Phone                 = req.Phone?.Trim(),
            OrgType               = "single",
            NumCompanies          = 1,
            NumBranches           = 0,
            NumEmployees          = req.NumEmployees ?? 0,
            NumAdminUsers         = 1,
            NumCountries          = 1,
            NeedsArabic           = false,
            SelectedModulesJson   = "[]",
            EstimatedMonthlyAmount= req.EstimatedMonthlyAmount ?? 0,
            EstimatedAnnualAmount = (req.EstimatedMonthlyAmount ?? 0) * 12,
            Notes                 = req.Notes?.Trim(),
            Status                = QuoteStatuses.New,
        };
        _db.PricingQuotes.Add(quote);
        await _db.SaveChangesAsync(ct);
        return Ok(quote);
    }

    [HttpPost("quotes/{id:guid}/convert")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> ConvertQuoteToTenant(Guid id, [FromBody] ConvertQuoteRequest req, CancellationToken ct)
    {
        var quote = await _db.PricingQuotes.FirstOrDefaultAsync(q => q.Id == id, ct);
        if (quote is null) return NotFound();
        if (quote.Status == QuoteStatuses.Converted)
            return BadRequest(new { error = "Quote is already converted." });

        // Reuse existing tenant creation logic by delegating to CreateTenant
        var createReq = new CreateTenantRequest(
            Name: quote.CompanyName,
            Slug: req.Slug,
            AdminEmail: req.AdminEmail,
            AdminFullName: req.AdminFullName,
            AdminPassword: req.AdminPassword,
            Plan: req.Plan ?? "Starter",
            MaxUsers: req.MaxUsers,
            MaxEmployees: req.MaxEmployees,
            BillingEmail: quote.ContactEmail,
            BillingCycle: req.BillingCycle ?? "Monthly",
            MonthlyAmount: quote.EstimatedMonthlyAmount,
            CurrencyCode: "USD",
            ExpiresAtUtc: req.ExpiresAtUtc,
            MaxCompanies: req.MaxCompanies,
            MaxAdminUsers: req.MaxAdminUsers);

        var createResult = await CreateTenant(createReq, ct);

        if (createResult is OkObjectResult ok)
        {
            var tenantIdProp = ok.Value?.GetType().GetProperty("tenantId")?.GetValue(ok.Value);
            if (tenantIdProp is Guid newTenantId)
            {
                quote.Status = QuoteStatuses.Converted;
                quote.ConvertedToTenantId = newTenantId;
                quote.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }

        return createResult;
    }

    // ── Compliance Controls ───────────────────────────────────────────────────────

    [HttpGet("compliance/controls")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Auditor)]
    public async Task<IActionResult> ListComplianceControls(CancellationToken ct)
    {
        var controls = await _db.PlatformComplianceControls
            .AsNoTracking()
            .OrderBy(x => x.Category).ThenBy(x => x.ControlId)
            .Select(x => new {
                x.Id, x.Category, x.ControlId, x.Title, x.Description,
                x.Status, x.Owner, x.EvidenceNote, x.EvidenceUrl,
                x.ReviewedAtUtc, x.UpdatedAtUtc, x.CreatedAtUtc
            })
            .ToListAsync(ct);
        return Ok(controls);
    }

    [HttpPost("compliance/controls")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> CreateComplianceControl([FromBody] ComplianceControlRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Category) || string.IsNullOrWhiteSpace(req.ControlId) || string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { message = "Category, ControlId and Title are required." });

        var category  = req.Category.Trim();
        var controlId = req.ControlId.Trim().ToUpperInvariant();
        // (Category, ControlId) is uniquely indexed — return a clean 409 instead of a raw DB 500.
        if (await _db.PlatformComplianceControls.AnyAsync(c => c.Category == category && c.ControlId == controlId, ct))
            return Conflict(new { message = $"A control '{controlId}' already exists in category '{category}'." });

        var control = new PlatformComplianceControl
        {
            Category    = category,
            ControlId   = controlId,
            Title       = req.Title.Trim(),
            Description = req.Description?.Trim(),
            Status      = req.Status ?? ComplianceControlStatus.NotStarted,
            Owner       = req.Owner?.Trim(),
        };
        _db.PlatformComplianceControls.Add(control);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId         = Guid.Empty,
            EntityType       = "PlatformComplianceControl",
            EntityId         = control.Id.ToString(),
            Action           = "ControlCreated",
            NewValuesJson    = System.Text.Json.JsonSerializer.Serialize(new { control.ControlId, control.Title, control.Status }),
            PerformedByName  = "platform_admin",
            IpAddress        = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Created($"/api/platform/compliance/controls/{control.Id}", new { control.Id });
    }

    [HttpPatch("compliance/controls/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateComplianceControl(Guid id, [FromBody] PatchComplianceControlRequest req, CancellationToken ct)
    {
        var control = await _db.PlatformComplianceControls.FindAsync([id], ct);
        if (control is null) return NotFound();
        var oldSnap = new { control.Status, control.Owner, control.EvidenceNote };
        if (req.Status  is not null) control.Status      = req.Status;
        if (req.Owner   is not null) control.Owner       = req.Owner.Trim();
        if (req.EvidenceNote is not null) control.EvidenceNote = req.EvidenceNote.Trim();
        if (req.EvidenceUrl  is not null) control.EvidenceUrl  = req.EvidenceUrl.Trim();
        if (req.Description  is not null) control.Description  = req.Description.Trim();
        if (req.Title is { Length: > 0 }) control.Title = req.Title.Trim();
        control.UpdatedAtUtc = DateTime.UtcNow;
        if (req.Reviewed == true)
        {
            control.ReviewedAtUtc            = DateTime.UtcNow;
            control.ReviewedByPlatformUserId = GetPlatformUserId();
        }
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformComplianceControl",
            EntityId        = id.ToString(),
            Action          = "ControlUpdated",
            OldValuesJson   = System.Text.Json.JsonSerializer.Serialize(oldSnap),
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { control.Status, control.Owner, control.EvidenceNote }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("compliance/summary")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Auditor)]
    public async Task<IActionResult> GetComplianceSummary(CancellationToken ct)
    {
        var controls  = await _db.PlatformComplianceControls.AsNoTracking().ToListAsync(ct);
        var incidents = await _db.PlatformSecurityIncidents.AsNoTracking().ToListAsync(ct);
        var summary = new
        {
            TotalControls      = controls.Count,
            Implemented        = controls.Count(c => c.Status == ComplianceControlStatus.Implemented),
            InProgress         = controls.Count(c => c.Status == ComplianceControlStatus.InProgress),
            NotStarted         = controls.Count(c => c.Status == ComplianceControlStatus.NotStarted),
            Waived             = controls.Count(c => c.Status == ComplianceControlStatus.Waived),
            ImplementationPct  = controls.Count == 0 ? 0 :
                                 (int)Math.Round(controls.Count(c => c.Status == ComplianceControlStatus.Implemented) * 100.0 / controls.Count),
            OpenIncidents      = incidents.Count(i => i.Status == "Open" || i.Status == "Investigating"),
            CriticalIncidents  = incidents.Count(i => i.Severity == "Critical" && (i.Status == "Open" || i.Status == "Investigating")),
        };
        return Ok(summary);
    }

    // ── Security Incidents ────────────────────────────────────────────────────────

    [HttpGet("compliance/incidents")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Auditor)]
    public async Task<IActionResult> ListSecurityIncidents(CancellationToken ct)
    {
        var incidents = await _db.PlatformSecurityIncidents
            .AsNoTracking()
            .OrderByDescending(x => x.OccurredAtUtc)
            .Select(x => new {
                x.Id, x.Title, x.Description, x.Severity, x.Status,
                x.Reporter, x.AffectedSystems, x.OccurredAtUtc,
                x.ResolvedAtUtc, x.Resolution, x.CreatedAtUtc
            })
            .ToListAsync(ct);
        return Ok(incidents);
    }

    [HttpPost("compliance/incidents")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin, PlatformRoles.Support)]
    public async Task<IActionResult> CreateSecurityIncident([FromBody] SecurityIncidentRequest req, CancellationToken ct)
    {
        var incident = new PlatformSecurityIncident
        {
            Title           = (req.Title ?? string.Empty).Trim(),
            Description     = req.Description?.Trim(),
            Severity        = req.Severity ?? "Low",
            Reporter        = req.Reporter?.Trim(),
            AffectedSystems = req.AffectedSystems?.Trim(),
            OccurredAtUtc   = req.OccurredAtUtc ?? DateTime.UtcNow,
            CreatedByPlatformUserId = GetPlatformUserId(),
        };
        _db.PlatformSecurityIncidents.Add(incident);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformSecurityIncident",
            EntityId        = incident.Id.ToString(),
            Action          = "IncidentCreated",
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { incident.Title, incident.Severity }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Created($"/api/platform/compliance/incidents/{incident.Id}", new { incident.Id });
    }

    [HttpPatch("compliance/incidents/{id:guid}")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> UpdateSecurityIncident(Guid id, [FromBody] SecurityIncidentRequest req, CancellationToken ct)
    {
        var incident = await _db.PlatformSecurityIncidents.FindAsync([id], ct);
        if (incident is null) return NotFound();
        var oldStatus = incident.Status;
        if (req.Status   is not null) incident.Status   = req.Status;
        if (req.Severity is not null) incident.Severity = req.Severity;
        if (req.Resolution      is not null) incident.Resolution      = req.Resolution.Trim();
        if (req.AffectedSystems is not null) incident.AffectedSystems = req.AffectedSystems.Trim();
        if (req.Status is "Resolved" or "Closed" && incident.ResolvedAtUtc is null)
            incident.ResolvedAtUtc = DateTime.UtcNow;
        incident.UpdatedAtUtc = DateTime.UtcNow;
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformSecurityIncident",
            EntityId        = id.ToString(),
            Action          = "IncidentUpdated",
            OldValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { Status = oldStatus }),
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { incident.Status, incident.Severity }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Settings: Diagnostics & Maintenance ──────────────────────────────────────

    [HttpGet("settings/diagnostics")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> GetDiagnostics(CancellationToken ct)
    {
        var tenantCount   = await _db.Tenants.CountAsync(ct);
        var activeCount   = await _db.Tenants.CountAsync(t => t.IsActive, ct);
        var employeeCount = await _db.Employees.CountAsync(e => !e.IsDeleted && e.Status == "Active", ct);
        var aiProvider    = _config["AI_PROVIDER"] ?? "none";
        var maintenanceEntry = await _db.PlatformConfigEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == PlatformConfigKeys.MaintenanceMode, ct);
        var diagnostics = new
        {
            TenantCount    = tenantCount,
            ActiveTenants  = activeCount,
            EmployeeCount  = employeeCount,
            DatabaseOk     = true,
            AiProvider     = aiProvider,
            AiConfigured   = aiProvider != "none" && aiProvider != "fallback",
            Maintenance    = maintenanceEntry?.Value == "true",
            MaintenanceMsg = (await _db.PlatformConfigEntries.AsNoTracking()
                                .FirstOrDefaultAsync(e => e.Key == PlatformConfigKeys.MaintenanceMessage, ct))?.Value ?? "",
            ServerTimeUtc  = DateTime.UtcNow,
        };
        return Ok(diagnostics);
    }

    [HttpPut("settings/maintenance")]
    [RequirePlatformRole(PlatformRoles.Owner, PlatformRoles.Admin)]
    public async Task<IActionResult> SetMaintenanceMode([FromBody] MaintenanceModeRequest req, CancellationToken ct)
    {
        await UpsertConfigAsync(PlatformConfigKeys.MaintenanceMode, req.Enabled ? "true" : "false", ct);
        if (req.Message is not null)
            await UpsertConfigAsync(PlatformConfigKeys.MaintenanceMessage, req.Message.Trim(), ct);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = Guid.Empty,
            EntityType      = "PlatformConfig",
            EntityId        = PlatformConfigKeys.MaintenanceMode,
            Action          = req.Enabled ? "MaintenanceModeEnabled" : "MaintenanceModeDisabled",
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { req.Enabled, req.Message }),
            PerformedByName = "platform_admin",
            IpAddress       = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { enabled = req.Enabled });
    }

    private async Task UpsertConfigAsync(string key, string value, CancellationToken ct)
    {
        var entry = await _db.PlatformConfigEntries.FirstOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null)
        {
            _db.PlatformConfigEntries.Add(new PlatformConfigEntry
            {
                Key = key, Value = value,
                UpdatedByPlatformUserId = GetPlatformUserId(),
            });
        }
        else
        {
            entry.Value = value;
            entry.UpdatedAtUtc = DateTime.UtcNow;
            entry.UpdatedByPlatformUserId = GetPlatformUserId();
        }
    }
}

public record PlatformLoginRequest(string Email, string Password);

public record ComplianceControlRequest(
    string Category, string ControlId, string Title,
    string? Description, string? Status, string? Owner,
    string? EvidenceNote, string? EvidenceUrl, bool? Reviewed);

/// <summary>Partial update for a compliance control — every field optional so the
/// console can PATCH just a status/owner/evidence without resending Category/ControlId/Title.</summary>
public record PatchComplianceControlRequest(
    string? Title, string? Description, string? Status, string? Owner,
    string? EvidenceNote, string? EvidenceUrl, bool? Reviewed);

public record SecurityIncidentRequest(
    string? Title, string? Description, string? Severity, string? Status,
    string? Reporter, string? AffectedSystems, string? Resolution,
    DateTime? OccurredAtUtc);

public record MaintenanceModeRequest(bool Enabled, string? Message);
public record ImpersonateRequest(Guid UserId);

public record CreateTenantRequest(
    string Name,
    string Slug,
    string AdminEmail,
    string? AdminFullName,
    string AdminPassword,
    string? Plan,
    int? MaxUsers,
    int? MaxEmployees,
    string? BillingEmail,
    string? BillingCycle,
    decimal? MonthlyAmount,
    string? CurrencyCode,
    DateTime? ExpiresAtUtc,
    int? MaxCompanies  = null,
    int? MaxAdminUsers = null,
    // SingleCompany (default) | Group — product behavior, distinct from MaxCompanies.
    string? AccountType = null,
    // PlatformControlled | GroupSelfServiceWithinLimit (default) | GroupDraftPlatformApproval
    string? CompanyCreationMode = null);

public record SetAccountTypeRequest(string AccountType);
public record SetCompanyCreationModeRequest(string Mode);

public record AddTenantAdminRequest(string Email, string? FullName, string Password);
/// <param name="EntityScope">
/// Which legal entities the new account may see: "group" (every company, now and in future),
/// "allCurrentCompanies", or "companies" with <paramref name="CompanyIds"/>. Omitted, it defaults to
/// "group" for the tenant Admin role and "allCurrentCompanies" for every other role. It is NOT
/// optional in effect — an account with no scope resolves to zero companies and every
/// company-owned row disappears from its queries — which is why the endpoint now refuses a request
/// that would land there rather than creating an account that cannot see its own tenant.
/// </param>
public record CreateTenantUserRequest(
    string Email, string? FullName, string Password, string? RoleName, bool? MustChangePassword,
    string? EntityScope = null, IReadOnlyCollection<Guid>? CompanyIds = null);
public record TenantActionRequest(string? Reason);

// ── Bulk tenant operation DTOs ──────────────────────────────────────────────
public record BulkTenantActionRequest(List<Guid> TenantIds, string? Reason);
public record BulkDeleteTenantsRequest(List<Guid> TenantIds, string Confirm);
public record BulkFeatureFlagRequest(
    List<Guid>? TenantIds,
    bool ApplyToAll,
    string FeatureKey,
    bool IsEnabled,
    string? ConfigJson);

/// <summary>Per-tenant outcome of a bulk operation. Status is "ok" | "skipped" | "failed".</summary>
public sealed class BulkOpItem
{
    public Guid TenantId { get; init; }
    public string? Name { get; init; }
    public string Status { get; init; } = "ok";
    public string? Reason { get; init; }

    public static BulkOpItem Ok(Guid id, string? name) => new() { TenantId = id, Name = name, Status = "ok" };
    public static BulkOpItem Skip(Guid id, string reason) => new() { TenantId = id, Status = "skipped", Reason = reason };
    public static BulkOpItem Fail(Guid id, string reason) => new() { TenantId = id, Status = "failed", Reason = reason };
}
public record UpdateTenantRequest(string? Name);
public record ForcePasswordResetRequest(string TempPassword);
public record StartSupportAccessRequest(string TenantId, string UserId, string Reason);
public record EndSupportAccessRequest(string SessionId);

public record CreateInvoiceRequest(
    string? InvoiceNumber,   // optional — auto-generated (INV-YYYY-NNNN) when blank
    decimal Amount,
    string? CurrencyCode,
    string? Status,
    string? PaymentMethod,
    string? PaymentReference,
    string? PeriodDescription,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    DateOnly? PaidDate,
    string? Notes,
    string? RecipientEmail);

public record UpdateInvoiceRequest(
    string? Status,
    string? PaymentMethod,
    string? PaymentReference,
    DateOnly? PaidDate,
    string? Notes,
    string? RecipientEmail);

public record UpdatePlanPriceRequest(decimal MonthlyPrice);
public record CreatePlatformUserRequest(string Email, string? FullName, string Password, string? Role);
public record UpdatePlatformUserRequest(string? Role, bool? IsActive, string? FullName);
public record EditUserRequest(string? FullName, string? Email, string? Status, bool? IsActive, string? RoleName);
public record UpdateBrandingRequest(string? LogoUrl, string? FaviconUrl, string? PrimaryColor, string? AccentColor, string? PortalTitle, string? CompanyNameEn, string? CompanyNameAr);
public record UpdateLocalizationRequest(string? DefaultLanguage, string? DefaultTimezone, string? DateFormat, string? CurrencyCode, string? CountryCode, string? CalendarSystem, string? WorkWeek, string? WeekStartDay, bool? RtlEnabled, bool? HijriDatesEnabled);

// ── New endpoint request records ──────────────────────────────────────────────

public record CreateAnnouncementRequest(
    string Title,
    string Body,
    string? TargetPlan,
    string? Status,
    DateTime? PublishedAtUtc,
    DateTime? ExpiresAtUtc);

public record PatchAnnouncementRequest(
    string? Title,
    string? Body,
    string? Status,
    DateTime? ExpiresAtUtc);

public record CreateLeadRequest(
    string CompanyName,
    string ContactName,
    string ContactEmail,
    string? Phone,
    string? Message,
    string? Source);

public record PatchLeadRequest(
    string? Status,
    string? Notes,
    string? AssignedTo);

public record UpdateSmtpRequest(
    string Host,
    int Port,
    string? Username,
    string? Password,
    bool UseSsl,
    string? FromEmail = null,
    string? FromName = null,
    /// <summary>Key from <see cref="EmailProviderPresets"/>; blank is inferred from the host.</summary>
    string? Provider = null);

/// <summary>Body of the SMTP test. <c>To</c> is optional — it defaults to the signed-in admin.</summary>
public record TestSmtpRequest(string? To);

public record ConvertLeadRequest(
    string TenantName,
    string TenantSlug,
    string AdminEmail,
    string AdminPassword,
    string? Plan,
    string? BillingEmail);

public record UpdateSecurityPolicyRequest(
    int? PasswordMinLength,
    bool? PasswordRequireUppercase,
    bool? PasswordRequireLowercase,
    bool? PasswordRequireDigit,
    bool? PasswordRequireSpecial,
    int? PasswordExpiryDays,
    int? PasswordHistoryCount,
    int? MaxFailedLoginAttempts,
    int? LockoutDurationMinutes,
    int? SessionTimeoutMinutes,
    int? RefreshTokenExpiryDays,
    bool? AllowMultipleSessions);

public record AddInvoiceLineRequest(
    string Description,
    int Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate,
    int SortOrder);

public record UpdateInvoiceLineRequest(
    string? Description,
    int? Quantity,
    decimal? UnitPrice,
    decimal? DiscountAmount,
    decimal? TaxRate,
    int? SortOrder);

public record CreatePaymentRequest(
    decimal Amount,
    string? CurrencyCode,
    string Method,
    string? Reference,
    string? Status,
    DateTime? PaidAt,
    Guid? ReceivedByPlatformUserId,
    string? Notes);

public record UpdatePaymentRequest(
    string? Status,
    decimal? Amount,
    string? Reference,
    DateTime? PaidAt,
    string? Notes);

public record UpdatePricingConfigRequest(decimal Value);

public record UpdatePricingModuleRequest(
    bool? IncludedInTrial,
    bool? IncludedInStarter,
    bool? IncludedInGrowth,
    bool? IncludedInEnterprise,
    bool? IsEnterpriseOnly,
    decimal? AddonPriceMonthly);

public record PatchQuoteRequest(string? Status, string? Notes);

public record CreateQuoteRequest(
    string CompanyName,
    string ContactEmail,
    string? ContactName,
    string? Phone,
    int? NumEmployees,
    decimal? EstimatedMonthlyAmount,
    string? Notes);

public record ConvertQuoteRequest(
    string Slug,
    string AdminEmail,
    string? AdminFullName,
    string AdminPassword,
    string? Plan,
    int? MaxUsers,
    int? MaxEmployees,
    int? MaxCompanies,
    int? MaxAdminUsers,
    string? BillingCycle,
    DateTime? ExpiresAtUtc);
