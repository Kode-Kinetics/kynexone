using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Xunit;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// A realistic world for the selfie-attendance and geofence tests, following <c>Security/SecurityReviewGapTests</c>:
/// every linked login carries the <c>employee_id</c> claim backed by an <c>EmployeeUserAccounts</c> row, the real
/// <see cref="DataScopeService"/> resolves scope, and each role's permissions are what <see cref="AuthSeeder"/> seeds
/// (plus the access-mode bundle). Deliberately written only against types that also exist on main, so the refusal
/// tests in <see cref="SelfieAttendanceRefusalOnMainTests"/> can be compiled and run against main's production code.
/// </summary>
internal sealed class SelfieWorld
{
    // Feature keys as strings: main has no constants for them, and the refusal file must compile there too.
    public const string SelfieKey = "selfie_attendance";
    public const string GeofenceKey = "attendance_geofence";

    // A site in Riyadh. 0.001° of latitude is ~111 m.
    public const decimal SiteLat = 24.7136m;
    public const decimal SiteLon = 46.6753m;
    public const decimal SiteRadius = 150m;

    public required ZayraDbContext Db { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid CompanyId { get; init; }
    public required Guid CallerUserId { get; init; }
    public required Guid ColleagueUserId { get; init; }
    /// <summary>The caller's own employee, linked to <see cref="CallerUserId"/>.</summary>
    public required Employee Caller { get; init; }
    /// <summary>Another employee of the same company, linked to <see cref="ColleagueUserId"/>.</summary>
    public required Employee Colleague { get; init; }
    public required MemoryDocumentStorage Storage { get; init; }
    /// <summary>Storage that IS on the KSA allow-list (endpoint host listed under Storage:ResidencyAllowList:KSA).</summary>
    public StorageResidency Residency { get; init; } = ResidentKsa;
    /// <summary>This world's own image gate, so parallel test classes never share the process-wide slots.</summary>
    public SelfieImageGate Gate { get; init; } = new();

    public const string KsaEndpoint = "https://s3.ksa-region.example.test";
    /// <summary>A neutral placeholder for an in-Kingdom bucket region (no real region is named anywhere).</summary>
    public const string KsaBucketRegion = "ksa-region-placeholder";
    /// <summary>Endpoint listed AND the bucket reports a listed region: resident.</summary>
    public static readonly StorageResidency ResidentKsa = new(new StorageOptions
    {
        Provider = "s3", Bucket = "b", Endpoint = KsaEndpoint, Region = "auto",
        ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = ["s3.ksa-region.example.test", KsaBucketRegion] },
    }, _ => Task.FromResult<string?>(KsaBucketRegion));
    /// <summary>What the platform endpoint stamps for <see cref="ResidentKsa"/> (endpoint, bucket and the bucket's region).</summary>
    public static readonly string LocalLocation = StorageResidency.Canonical("local", "", "local");
    public static readonly string ResidentKsaLocation = StorageResidency.Canonical("s3.ksa-region.example.test", "b", KsaBucketRegion);

    public AttendanceVerificationService Verification => new(Db, Residency);

    public static async Task<SelfieWorld> CreateAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Selfie world", Slug = $"selfie-{tenantId:N}" });
        var callerUserId = Guid.NewGuid();
        var colleagueUserId = Guid.NewGuid();
        var caller = NewEmployee(tenantId, companyId, "CALLER", callerUserId);
        var colleague = NewEmployee(tenantId, companyId, "COLLEAGUE", colleagueUserId);
        db.Employees.AddRange(caller, colleague);
        await db.SaveChangesAsync();
        db.EmployeeUserAccounts.AddRange(Link(tenantId, caller.Id, callerUserId), Link(tenantId, colleague.Id, colleagueUserId));
        await db.SaveChangesAsync();
        return new SelfieWorld
        {
            Db = db, TenantId = tenantId, CompanyId = companyId, CallerUserId = callerUserId, ColleagueUserId = colleagueUserId,
            Caller = caller, Colleague = colleague, Storage = new MemoryDocumentStorage(),
        };
    }

    private static Employee NewEmployee(Guid tenantId, Guid companyId, string code, Guid userId) => new()
    {
        TenantId = tenantId, CompanyId = companyId, UserAccountId = userId, EmployeeCode = code, FullName = code,
        EnglishName = code, Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 9_000m,
        WorkLocation = "HQ",
    };

    private static EmployeeUserAccount Link(Guid tenantId, int employeeId, Guid userId) => new()
    {
        TenantId = tenantId, EmployeeId = employeeId, UserId = userId, AccessMode = AccessModes.Mobile,
        Status = "Active", RequiresPasswordSetup = false,
    };

    /// <summary>The token a Mobile-mode employee really gets: the seeded Employee role's keys plus the Mobile bundle.</summary>
    public async Task<ClaimsPrincipal> EmployeeAsync(Employee employee, Guid userId, string accessMode = AccessModes.Mobile)
    {
        var permissions = (await SeededAsync("Employee")).Concat(AuthService.AccessModePermissions(accessMode)).Distinct();
        return Principal("Employee", userId, employee.Id, permissions, accessMode);
    }

    public async Task<ClaimsPrincipal> RoleAsync(string role, Employee employee, Guid userId) =>
        Principal(role, userId, employee.Id, await SeededAsync(role), null);

    public ClaimsPrincipal Principal(string role, Guid userId, int? employeeId, IEnumerable<string> permissions, string? accessMode)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString()),
            new(ClaimTypes.Role, role),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Group })),
        };
        if (accessMode is not null) claims.Add(new Claim("access_mode", accessMode));
        if (employeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
        claims.AddRange(permissions.Distinct().Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public T With<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "test";
        return controller;
    }

    public AttendanceController Attendance(ClaimsPrincipal user) =>
        With(new AttendanceController(new AttendanceService(Db, new NullNotifications(), new NullHttpClients()),
            new DataScopeService(Db), new HrmHierarchyService(Db, new NullAudit()), Db, Verification), user);

    public MobileController Mobile(ClaimsPrincipal user) => With(new MobileController(Db, Verification), user);

    public AttendanceEvidenceController Evidence(ClaimsPrincipal user) =>
        With(new AttendanceEvidenceController(Db, Storage, Verification, Gate), user);

    public EssAttendanceVerificationController Ess(ClaimsPrincipal user) =>
        With(new EssAttendanceVerificationController(Db, Verification, Storage), user);

    /// <summary>POSTs <paramref name="bytes"/> as the multipart <c>file</c> field, the way the HTTP body arrives.</summary>
    public Task<IActionResult> UploadAsync(ClaimsPrincipal user, byte[] bytes, string contentType = "image/jpeg", string fileName = "selfie.jpg")
    {
        var controller = Evidence(user);
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName) { Headers = new HeaderDictionary(), ContentType = contentType };
        controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(), new FormFileCollection { file });
        return controller.UploadSelfie();
    }

    /// <summary>A Setup → Locations row (what Setup writes) with coordinates and a radius, matched by WorkLocation "HQ".</summary>
    public async Task<Location> AddSiteAsync(string code = "HQ", decimal? lat = SiteLat, decimal? lon = SiteLon, decimal? radius = SiteRadius)
    {
        var site = new Location
        {
            TenantId = TenantId, Code = code, NameEn = $"{code} office", NameAr = code, Latitude = lat, Longitude = lon,
            GeofenceRadiusMeters = radius, IsActive = true,
        };
        Db.Locations.Add(site);
        await Db.SaveChangesAsync();
        return site;
    }

    public async Task SetFlagAsync(string key, bool enabled, string? configJson)
    {
        var flag = await Db.TenantFeatureFlags.FirstOrDefaultAsync(f => f.TenantId == TenantId && f.FeatureKey == key);
        if (flag is null)
        {
            flag = new TenantFeatureFlag { TenantId = TenantId, FeatureKey = key };
            Db.TenantFeatureFlags.Add(flag);
        }
        flag.IsEnabled = enabled;
        flag.ConfigJson = configJson;
        await Db.SaveChangesAsync();
    }

    public Task EnforceGeofenceAsync(int maxAccuracy = 100, bool allowMocked = false) =>
        SetFlagAsync(GeofenceKey, true, JsonSerializer.Serialize(new { geofenceEnforced = true, maxAccuracyMeters = maxAccuracy, allowMockedLocation = allowMocked }));

    public static readonly Guid SigningOwnerId = Guid.Parse("0b1d0b1d-0000-4000-8000-000000000001");

    /// <summary>Both owner sign-offs as the platform endpoint STORES them (the residency block server-stamped).</summary>
    public static string SignedOffConfig(bool requireSelfieForConsented = false, string? storageLocation = null) => JsonSerializer.Serialize(new
    {
        dpia = new { signedOffBy = SigningOwnerId.ToString(), signedOffAtUtc = "2026-10-01T09:00:00Z", reference = "DPIA-2026-007" },
        dataResidency = new { region = "KSA", confirmedBy = SigningOwnerId.ToString(), confirmedAtUtc = "2026-10-01T09:05:00Z", storageLocation = storageLocation ?? ResidentKsaLocation },
        requireSelfieForConsented,
        consentPolicyVersion = "1",
    });

    /// <summary>What an Owner SENDS to enable it: the DPIA block and the region; the server stamps the rest.</summary>
    public static string EnableRequestConfig(Guid signedOffBy, string reference = "DPIA-2026-007", string signedOffAtUtc = "2026-10-01T09:00:00Z") =>
        JsonSerializer.Serialize(new
        {
            dpia = new { signedOffBy = signedOffBy.ToString(), signedOffAtUtc, reference },
            dataResidency = new { region = "KSA" },
            requireSelfieForConsented = false,
            consentPolicyVersion = "1",
        });

    /// <summary>
    /// Makes <paramref name="controller"/> act as a named platform Owner (a Guid subject, as real platform tokens carry)
    /// on a deploy with <paramref name="residency"/>, and records that Owner as a platform user.
    /// </summary>
    public static async Task<T> AsPlatformOwnerAsync<T>(T controller, ZayraDbContext db, Guid ownerId, StorageResidency? residency = null,
        string role = PlatformRoles.Owner) where T : ControllerBase
    {
        if (!await db.PlatformUsers.AnyAsync(u => u.Id == ownerId))
        {
            db.PlatformUsers.Add(new PlatformUser { Id = ownerId, Email = $"{ownerId:N}@platform.test", FullName = "Platform person", PasswordHash = "x", Role = role, IsActive = true });
            await db.SaveChangesAsync();
        }
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingletonResidency(residency ?? ResidentKsa);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", ownerId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email, $"{ownerId:N}@platform.test"),
            new Claim(ClaimTypes.Role, "PlatformAdmin"),
            new Claim("is_platform_admin", "true"),
            new Claim("platform_role", role),
        ], "Test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal, RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services) },
        };
        return controller;
    }

    /// <summary>The permissions each seeded role really holds, read once from AuthSeeder.</summary>
    private static readonly Lazy<Task<IReadOnlyDictionary<string, string[]>>> SeededRoles = new(async () =>
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"selfie-seeded-roles-{Guid.NewGuid()}").Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Seeded roles", Slug = $"seeded-roles-{tenantId:N}" });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId);
        var rows = await db.Roles.Where(r => r.TenantId == tenantId)
            .Select(r => new { r.Name, Keys = r.RolePermissions.Select(rp => rp.Permission!.Key).ToList() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Name, r => r.Keys.ToArray());
    });

    public static async Task<string[]> SeededAsync(string role)
    {
        var roles = await SeededRoles.Value;
        Assert.True(roles.TryGetValue(role, out var keys), $"AuthSeeder seeds no role named {role}");
        return keys!;
    }

    /// <summary>The refusal code in a 400 body ({ code, message, messageAr }), or null.</summary>
    public static string? CodeOf(IActionResult? result) =>
        result is ObjectResult { Value: { } body } && JsonSerializer.SerializeToElement(body).TryGetProperty("code", out var c) ? c.GetString() : null;

    public static string? MessageOf(IActionResult? result) =>
        result is ObjectResult { Value: { } body } && JsonSerializer.SerializeToElement(body).TryGetProperty("message", out var m) ? m.GetString() : null;
}

/// <summary>
/// Document storage in memory: records what was stored and what was deleted. Its strict delete behaves like the real
/// one (gone or absent = success; <see cref="FailDeletes"/> throws), and <see cref="TryDeleteAsync"/> can be made to
/// RETURN false while keeping the object (<see cref="TryDeleteReturnsFalse"/>), the way the S3 adapter answers a 403.
/// </summary>
internal sealed class MemoryDocumentStorage : IDocumentStorage
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public System.Collections.Concurrent.ConcurrentQueue<string> DeletedLog { get; } = new();
    public List<string> Deleted => DeletedLog.ToList();
    public bool FailDeletes { get; set; }
    public bool TryDeleteReturnsFalse { get; set; }
    public bool FailPuts { get; set; }

    public async Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var key = $"storage/documents/{tenantId:N}/{Guid.NewGuid():N}_{file.FileName}";
        Objects[key] = buffer.ToArray();
        return new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", key, string.Empty);
    }

    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
        Objects.TryGetValue(storageUrl, out var bytes) ? Task.FromResult(bytes) : throw new FileNotFoundException(storageUrl);

    public string ResolvePath(string storageUrl) => storageUrl;

    public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        if (TryDeleteReturnsFalse) return Task.FromResult(false);
        if (FailDeletes) throw new IOException("storage unavailable");
        DeletedLog.Enqueue(storageUrl);
        return Task.FromResult(Objects.TryRemove(storageUrl, out _));
    }

    public string TenantKey(Guid tenantId, string relativeName) => $"storage/documents/{tenantId:N}/{relativeName}";

    public Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
    {
        if (FailPuts) throw new IOException("storage unavailable");
        Assert.StartsWith($"storage/documents/{tenantId:N}/", storageKey);
        Objects[storageKey] = content;
        return Task.CompletedTask;
    }

    public Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        if (FailDeletes) throw new IOException("storage unavailable");
        if (TryDeleteReturnsFalse) throw new IOException("403 Forbidden");
        Assert.StartsWith($"storage/documents/{tenantId:N}/", storageUrl);
        DeletedLog.Enqueue(storageUrl);
        Objects.TryRemove(storageUrl, out _);
        return Task.CompletedTask;
    }
}

/// <summary>A store built before the strict delete existed: TryDelete answers false (as S3 does on a 403) and nothing else.</summary>
internal sealed class LegacyFalseReturningStorage : IDocumentStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Objects[storageUrl]);
    public string ResolvePath(string storageUrl) => storageUrl;
    public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class NullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class NullHttpClients : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

internal sealed class NullAudit : IAuditService
{
    public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class SelfieServiceCollectionExtensions
{
    public static void AddSingletonResidency(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, StorageResidency residency) =>
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, residency);
}
