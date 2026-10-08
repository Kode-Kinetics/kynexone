using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Review 1, item 12: selfie attendance through the REAL Program.cs pipeline (authentication, the opt-in feature guard,
/// request limits, model binding, the platform role filter), over <see cref="AuthorizationPipelineHost"/>. Every token
/// is minted by the application's own token service from seeded rows, so the per-request session re-check passes.
/// Speaks HTTP only, so it also runs against the reviewed backend (d43b3943) — where the refusals here fail.
/// </summary>
public sealed class SelfieHttpPipelineTests : IClassFixture<SelfieHttpPipelineFixture>
{
    private readonly SelfieHttpPipelineFixture _fx;
    public SelfieHttpPipelineTests(SelfieHttpPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task FlagOff_TheGlobalGuard_RefusesTheUpload()
    {
        var response = await _fx.UploadAsync(_fx.FlagOffEmployeeToken, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("feature_not_enabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FlagOn_AValidSelfie_IsStored_AndItsIdBacksOnePunch()
    {
        var response = await _fx.UploadAsync(_fx.UploaderToken, SelfieAttendanceTests.SelfieJpeg());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var evidenceId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("evidenceId").GetGuid();

        using var punch = new HttpRequestMessage(HttpMethod.Post, "/api/attendance/punch/mobile")
        {
            Content = JsonContent.Create(new { employeeId = 0, punchDirection = "In", evidenceId }),
        };
        punch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fx.UploaderToken);
        var punched = await _fx.Client.SendAsync(punch);
        Assert.Equal(HttpStatusCode.OK, punched.StatusCode);
        Assert.Equal("Selfie", (await punched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verificationMethod").GetString());
    }

    [Fact]
    public async Task ARequestOver8MB_IsRefusedAs413()
    {
        var response = await _fx.UploadAsync(_fx.UploaderToken, new byte[9 * 1024 * 1024]);

        Assert.True(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task AHugeCanvasJpeg_IsRefusedAsTooLarge_NotDecoded_AndAPngIsRefusedAsNotAJpeg()
    {
        var response = await _fx.UploadAsync(_fx.UploaderToken, SelfieReviewFixesTests.HugeCanvasJpeg(30_000, 30_000));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("selfie_too_large", await response.Content.ReadAsStringAsync());

        // Review 2, item 3: JPEG only, by magic bytes — a PNG (which decodes at full size) is refused whatever its label.
        var png = await _fx.UploadAsync(_fx.UploaderToken, SelfieReviewFixesTestsPng.HugeCanvas(30_000, 30_000), "image/jpeg", "s.jpg");
        Assert.Equal(HttpStatusCode.BadRequest, png.StatusCode);
        Assert.Contains("selfie_invalid", await png.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Review2_TheSelfieView_IsRefusedWithoutTheEvidenceViewPermission_EvenWithTheFlagOn()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/attendance/evidence/{Guid.NewGuid()}/selfie");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fx.UploaderToken);

        var response = await _fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Nothing of the image path is reached: no audit row, no body beyond the refusal.
        Assert.DoesNotContain("image/jpeg", response.Content.Headers.ContentType?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task ARateLimitedEmployee_Gets429_BeforeTheirBodyIsEvenLookedAt()
    {
        var response = await _fx.UploadAsync(_fx.RateLimitedToken, "this is not an image"u8.ToArray());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("selfie_rate_limited", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APlatformAdmin_CannotEnableSelfieAttendance_ButAnOwnerCan()
    {
        var admin = await _fx.SetSelfieFlagAsync(_fx.PlatformAdminToken, _fx.EnableTenantId, _fx.EnableConfig(_fx.PlatformOwnerId));
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);

        var owner = await _fx.SetSelfieFlagAsync(_fx.PlatformOwnerToken, _fx.EnableTenantId, _fx.EnableConfig(_fx.PlatformOwnerId));
        Assert.True(owner.StatusCode == HttpStatusCode.OK, await owner.Content.ReadAsStringAsync());
    }
}

/// <summary>The huge-canvas PNG builder, shared with the HTTP tests without depending on the unit-test class.</summary>
internal static class SelfieReviewFixesTestsPng
{
    public static byte[] HugeCanvas(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var deflate = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(new byte[64]);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeAndData)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        var crcBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        s.Write(crcBytes);
    }
}

/// <summary>
/// Boots Program.cs once (SQLite, Development, the KSA residency allow-list naming this deploy's local storage, document
/// storage swapped for memory) and seeds: an employee with selfie attendance on and consent (the uploader), one already at
/// ten attempts this hour, an employee of a tenant without the flag, a tenant to enable it on, and a platform Owner and Admin.
/// </summary>
public sealed class SelfieHttpPipelineFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    internal MemoryDocumentStorage Storage { get; } = new();

    public string UploaderToken { get; private set; } = null!;
    public string RateLimitedToken { get; private set; } = null!;
    public string FlagOffEmployeeToken { get; private set; } = null!;
    public string PlatformOwnerToken { get; private set; } = null!;
    public string PlatformAdminToken { get; private set; } = null!;
    public Guid PlatformOwnerId { get; private set; }
    public Guid EnableTenantId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:selfie-http-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connectionString);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connectionString,
            services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(IDocumentStorage)).ToList()) services.Remove(d);
                services.AddSingleton<IDocumentStorage>(Storage);
            },
            hostSettings: new Dictionary<string, string?> { ["Storage:ResidencyAllowList:KSA:0"] = "local" });
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            var on = await SeedTenantAsync(db, "selfie-on", selfieOn: true);
            var off = await SeedTenantAsync(db, "selfie-off", selfieOn: false);
            var uploader = await SeedEmployeeUserAsync(db, on, "uploader");
            var limited = await SeedEmployeeUserAsync(db, on, "limited");
            var flagOff = await SeedEmployeeUserAsync(db, off, "flagoff");
            db.BiometricConsents.AddRange(
                new BiometricConsent { TenantId = on, EmployeeId = uploader.EmployeeId, PolicyVersion = "1", Channel = "Mobile" },
                new BiometricConsent { TenantId = on, EmployeeId = limited.EmployeeId, PolicyVersion = "1", Channel = "Mobile" });
            for (var i = 0; i < 10; i++)
            {
                var created = DateTime.UtcNow.AddMinutes(-20 - i);
                db.AttendanceEvidence.Add(new AttendanceEvidence
                {
                    TenantId = on, EmployeeId = limited.EmployeeId, StorageKey = $"storage/documents/{on:N}/seed{i}.jpg", Sha256 = new string('a', 64),
                    ByteSize = 3, CreatedAtUtc = created, ExpiresAtUtc = created.AddMinutes(10), PurgeState = "Active",
                });
            }
            EnableTenantId = await SeedTenantAsync(db, "selfie-enable", selfieOn: false);
            var owner = new PlatformUser { Email = "owner@selfie-http.platform", FullName = "Owner", PasswordHash = "no-login", Role = PlatformRoles.Owner, IsActive = true };
            var admin = new PlatformUser { Email = "admin@selfie-http.platform", FullName = "Admin", PasswordHash = "no-login", Role = PlatformRoles.Admin, IsActive = true };
            db.PlatformUsers.AddRange(owner, admin);
            await db.SaveChangesAsync();
            PlatformOwnerId = owner.Id;
            _uploaderUserId = uploader.UserId;
            _limitedUserId = limited.UserId;
            _flagOffUserId = flagOff.UserId;
            _adminId = admin.Id;
        }

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
            var jwt = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
            UploaderToken = await MintAsync(db, tokens, _uploaderUserId);
            RateLimitedToken = await MintAsync(db, tokens, _limitedUserId);
            FlagOffEmployeeToken = await MintAsync(db, tokens, _flagOffUserId);
            PlatformOwnerToken = MintPlatform(await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Id == PlatformOwnerId), jwt);
            PlatformAdminToken = MintPlatform(await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Id == _adminId), jwt);
        }
    }

    private Guid _uploaderUserId, _limitedUserId, _flagOffUserId, _adminId;

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }

    public async Task<HttpResponseMessage> UploadAsync(string token, byte[] bytes, string contentType = "image/jpeg", string fileName = "selfie.jpg")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/attendance/evidence/selfie") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> SetSelfieFlagAsync(string platformToken, Guid tenantId, string configJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/platform/tenants/{tenantId}/features/selfie_attendance")
        {
            Content = JsonContent.Create(new { isEnabled = true, configJson }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        return await Client.SendAsync(request);
    }

    /// <summary>What an Owner sends: the DPIA block (signed by a real platform user) and the region.</summary>
    public string EnableConfig(Guid signedOffBy) => JsonSerializer.Serialize(new
    {
        dpia = new { signedOffBy = signedOffBy.ToString(), signedOffAtUtc = "2026-10-01T09:00:00Z", reference = "DPIA-2026-007" },
        dataResidency = new { region = "KSA" },
    });

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db, string slug, bool selfieOn)
    {
        var tenant = new Tenant { Name = slug, Slug = $"{slug}-{Guid.NewGuid():N}"[..30], IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenant.Id);
        if (selfieOn)
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant.Id, FeatureKey = "selfie_attendance", IsEnabled = true, ConfigJson = SelfieWorld.SignedOffConfig(storageLocation: SelfieWorld.LocalLocation) });
            await db.SaveChangesAsync();
        }
        return tenant.Id;
    }

    internal static async Task<(Guid UserId, int EmployeeId)> SeedEmployeeUserAsync(ZayraDbContext db, Guid tenantId, string name)
    {
        var company = new Company { TenantId = tenantId, LegalNameEn = $"{name} Co", TradeName = name, CountryCode = "SA", IsActive = true };
        db.Companies.Add(company);
        var email = $"{name}-{Guid.NewGuid():N}@selfie-http.local";
        var user = new User
        {
            TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, PasswordHash = "no-login",
            Status = "Active", AccessMode = "Mobile", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, UserAccountId = user.Id, EmployeeCode = name.ToUpperInvariant(), FullName = name, EnglishName = name,
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-1), Salary = 8000m, WorkLocation = "HQ",
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var role = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == "Employee");
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = tenantId, EmployeeId = employee.Id, UserId = user.Id, AccessMode = AccessModes.Mobile, Status = "Active", RequiresPasswordSetup = false, IsPrimary = true,
        });
        await db.SaveChangesAsync();
        return (user.Id, employee.Id);
    }

    /// <summary>Mirrors AuthService.BuildAuthResponse for a group-scoped user with no entity grants.</summary>
    internal static async Task<string> MintAsync(ZayraDbContext db, ITokenService tokens, Guid userId)
    {
        var user = await db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(x => x.Id == userId);
        var grants = Array.Empty<EntityAccessGrant>();
        var scope = EntityScopeClaims.Resolve(user.IsGroupScope, grants, Array.Empty<Guid>());
        return tokens.CreateAccessToken(user, AuthService.GetRoles(user), AuthService.GetPermissions(user), user.Tenant!, grants, scope, out _);
    }

    /// <summary>Mirror of PlatformController.CreatePlatformToken (as in AuthorizationPipelineFixture).</summary>
    private static string MintPlatform(PlatformUser user, JwtOptions jwt)
    {
        var stamp = user.UpdatedAtUtc ?? throw new InvalidOperationException("Platform user session stamp was not initialised.");
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, "PlatformAdmin"),
            new("is_platform_admin", "true"),
            new("platform_role", user.Role),
            new(PlatformSessionSecurity.SessionStampClaim, PlatformSessionSecurity.StampValue(stamp)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(jwt.Issuer, jwt.PlatformAudience, claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: credentials));
    }
}
