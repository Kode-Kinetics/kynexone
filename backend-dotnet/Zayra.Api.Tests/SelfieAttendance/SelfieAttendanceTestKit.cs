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
            new DataScopeService(Db), new HrmHierarchyService(Db, new NullAudit()), Db), user);

    public MobileController Mobile(ClaimsPrincipal user) => With(new MobileController(Db), user);

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

    /// <summary>Both owner sign-offs, as the platform endpoint requires them.</summary>
    public static string SignedOffConfig(bool requireSelfieForConsented = false) => JsonSerializer.Serialize(new
    {
        dpia = new { signedOffBy = "owner@kynexone.test", signedOffAtUtc = "2026-10-08T09:00:00Z", reference = "DPIA-2026-007" },
        dataResidency = new { region = "KSA", confirmedBy = "owner@kynexone.test", confirmedAtUtc = "2026-10-08T09:05:00Z" },
        requireSelfieForConsented,
        consentPolicyVersion = "1",
    });

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

/// <summary>Document storage in memory: records what was stored and what was deleted.</summary>
internal sealed class MemoryDocumentStorage : IDocumentStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public List<string> Deleted { get; } = [];
    public bool FailDeletes { get; set; }

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
        if (FailDeletes) throw new IOException("storage unavailable");
        Deleted.Add(storageUrl);
        return Task.FromResult(Objects.Remove(storageUrl));
    }
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
