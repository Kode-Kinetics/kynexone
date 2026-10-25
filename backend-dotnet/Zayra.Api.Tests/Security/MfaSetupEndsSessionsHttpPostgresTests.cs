using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Turning two-step on ends the user's other sessions, through the real Program.cs pipeline over a real PostgreSQL built
/// by every EF migration: a refresh token taken from an earlier sign-in is refused afterwards and its whole family is
/// revoked. Enrollment setup (signed out) re-issues nothing; signed-in setup re-issues the caller's own session, which
/// keeps working while the earlier access token does not.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MfaSetupEndsSessionsHttpPostgresTests : IClassFixture<MfaSetupHostFixture>
{
    private const string Password = "Correct-Horse-Battery-9!";
    private readonly MfaSetupHostFixture _fx;
    public MfaSetupEndsSessionsHttpPostgresTests(MfaSetupHostFixture fx) => _fx = fx;

    [Fact]
    public async Task EnrollmentSetup_EndsTheUsersEarlierSession_ItsRefreshTokenIs401_AndItsFamilyIsRevoked()
    {
        var (slug, email, userId) = await _fx.SeedUserAsync("enrol");
        var login = await PostAsync("/api/auth/login", new { email, password = Password, tenantSlug = slug });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await Json(login);
        var access = session.GetProperty("accessToken").GetString()!;
        var refresh = session.GetProperty("refreshToken").GetString()!;

        var start = await PostAsync("/api/auth/mfa/enrollment/start", new { }, access);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var enrollmentToken = (await Json(start)).GetProperty("enrollmentToken").GetString()!;
        var setup = await PostAsync("/api/auth/mfa/enrollment/setup", new { enrollmentToken });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var secret = SecretOf((await Json(setup)).GetProperty("provisioningUri").GetString()!);
        var verify = await PostAsync("/api/auth/mfa/enrollment/verify-setup",
            new { enrollmentToken, tempSecret = secret, totpCode = Totp(secret) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal(10, (await Json(verify)).GetProperty("backupCodes").GetArrayLength());

        var refreshed = await PostAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
        await AssertFamilyRevokedAsync(userId, refresh);
        await using var db = _fx.CreateDb();
        Assert.False(await db.RefreshTokens.AnyAsync(r => r.UserId == userId && r.RevokedAtUtc == null),
            "enrollment setup is signed out: nothing is re-issued");
    }

    [Fact]
    public async Task SignedInSetup_EndsTheEarlierSession_AndTheReissuedSessionWorks()
    {
        var (slug, email, userId) = await _fx.SeedUserAsync("signedin");
        var login = await PostAsync("/api/auth/login", new { email, password = Password, tenantSlug = slug });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await Json(login);
        var access = session.GetProperty("accessToken").GetString()!;
        var refresh = session.GetProperty("refreshToken").GetString()!;

        var setup = await PostAsync("/api/auth/mfa/setup", new { }, access);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var secret = SecretOf((await Json(setup)).GetProperty("provisioningUri").GetString()!);
        var verify = await PostAsync("/api/auth/mfa/verify-setup", new { tempSecret = secret, totpCode = Totp(secret) }, access);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var body = await Json(verify);
        Assert.Equal(10, body.GetProperty("backupCodes").GetArrayLength());
        var newAccess = body.GetProperty("accessToken").GetString()!;
        var newRefresh = body.GetProperty("refreshToken").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/api/auth/refresh", new { refreshToken = refresh })).StatusCode);
        await AssertFamilyRevokedAsync(userId, refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/auth/mfa/status", access)).StatusCode);

        var status = await GetAsync("/api/auth/mfa/status", newAccess);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True((await Json(status)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/auth/refresh", new { refreshToken = newRefresh })).StatusCode);
    }

    private async Task AssertFamilyRevokedAsync(Guid userId, string rawRefreshToken)
    {
        var hash = new JwtTokenService(AuthHardeningTestKit.Jwt).HashToken(rawRefreshToken);
        await using var db = _fx.CreateDb();
        var token = await db.RefreshTokens.AsNoTracking().SingleAsync(r => r.TokenHash == hash);
        Assert.Equal(userId, token.UserId);
        var family = await db.RefreshTokens.AsNoTracking().Where(r => r.FamilyId == token.FamilyId).ToListAsync();
        Assert.NotEmpty(family);
        Assert.All(family, r => Assert.NotNull(r.RevokedAtUtc));
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _fx.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> GetAsync(string path, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _fx.Client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string SecretOf(string provisioningUri)
    {
        var query = new Uri(provisioningUri).Query.TrimStart('?').Split('&');
        return Uri.UnescapeDataString(query.Single(p => p.StartsWith("secret=", StringComparison.OrdinalIgnoreCase))["secret=".Length..]);
    }

    private static string Totp(string secret)
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
}

/// <summary>Its own PostgreSQL 16, migrated by every EF migration, behind the real Program.cs pipeline.</summary>
public sealed class MfaSetupHostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MfaSetupHost _host = null!;
    private string _connectionString = string.Empty;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _connectionString = _pg.GetConnectionString();
        await using (var migrate = CreateDb())
            await migrate.Database.MigrateAsync();
        _host = new MfaSetupHost(_connectionString);
        Client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        _host?.Dispose();
        return _pg.DisposeAsync().AsTask();
    }

    public ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseNpgsql(_connectionString, PostgresFixture.ProductionProviderOptions)
        .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
        .Options);

    /// <summary>An active tenant user with a password and no factor.</summary>
    public async Task<(string Slug, string Email, Guid UserId)> SeedUserAsync(string label)
    {
        var tenantId = Guid.NewGuid();
        var slug = $"mfa-{label}-{tenantId:N}"[..24];
        var email = $"{label}@mfa-setup.test";
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = label,
            PasswordHash = new Pbkdf2PasswordHasher().Hash("Correct-Horse-Battery-9!"), Status = "Active", IsActive = true,
            IsEmailConfirmed = true, IsGroupScope = true,
        };
        await using var db = CreateDb();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"MFA {label}", Slug = slug, IsActive = true });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (slug, email, user.Id);
    }
}

/// <summary>The real Program.cs host over the fixture's database: background workers removed, login limits raised.</summary>
internal sealed class MfaSetupHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    public MfaSetupHost(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Database:RunMigrationsOnStartup", "false");
        builder.UseSetting("RateLimit:LoginPermitLimit", "1000");
        builder.ConfigureTestServices(services =>
        {
            // Not a licence test: an unlimited licence, so no request is refused 402 for want of one.
            Zayra.Api.Tests.Licensing.LicenceTestServices.UseUnlimitedLicence(services);
            var apiAssembly = typeof(ZayraDbContext).Assembly;
            foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Assembly == apiAssembly).ToList())
                services.Remove(worker);
        });
    }
}
