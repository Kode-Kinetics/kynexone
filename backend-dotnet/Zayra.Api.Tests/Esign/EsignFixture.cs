using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Application.Esign;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;
using Zayra.Api.Tests.Ess;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Esign;

/// <summary>Object storage that lives in memory: the P4 tests never touch the disk and can alter "stored" bytes directly.</summary>
public sealed class InMemoryDocumentStorage : IDocumentStorage
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
    public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
        Objects.TryGetValue(storageUrl, out var b) ? Task.FromResult(b) : throw new FileNotFoundException(storageUrl);
    public string TenantKey(Guid tenantId, string relativeName) => $"storage/documents/{tenantId:N}/{relativeName}";
    public Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default) { Objects[storageKey] = content; return Task.CompletedTask; }
    public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Objects.TryRemove(storageUrl, out _));
}

public sealed record SignDoc(Guid Id, byte[] Bytes, string Sha256, string Key);

/// <summary>One SQLite-backed host for the P4 HTTP tests (the same production composition as the P2 fixture, plus in-memory storage and an HR with write + documents).</summary>
public sealed class EsignFixture : IAsyncLifetime
{
    public const string Password = EssFixture.Password;
    public static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");
    private SqliteConnection _anchor = null!;
    private HttpClient _client = null!;
    private int _seq = 3000;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public InMemoryDocumentStorage Storage { get; } = new();
    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public string HrToken { get; private set; } = null!;          // employees.read + employees.write + employees.documents + employees.sensitive
    public string HrNoDocsToken { get; private set; } = null!;    // employees.write only
    public Guid HrUserId { get; private set; }
    public string HrPassword => Password;

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:esign-p4-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection, services =>
        {
            services.RemoveAll<IDocumentStorage>();
            services.AddSingleton<IDocumentStorage>(Storage);
            services.RemoveAll<DbContextOptions<ZayraDbContext>>(); services.RemoveAll<DbContextOptions>(); services.RemoveAll<ZayraDbContext>();
            services.AddDbContext<ZayraDbContext>(o => o.UseSqlite(connection)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
            services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.AdditionalHandlers.Add(new RecordingHandler())));
        });
        _client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        var tenant = new Tenant { Name = "ESIGN P4", Slug = $"esign-p4-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        TenantId = tenant.Id;
        var company = new Company { TenantId = TenantId, LegalNameEn = "Saudi Co", TradeName = "Saudi Co", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        CompanyId = company.Id;
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = TenantId, Plan = "Enterprise", Status = "Active", MaxEmployees = 1000 });
        var hrManager = new Role { TenantId = TenantId, Name = "HR Manager", NormalizedName = "HR MANAGER" };
        db.Roles.Add(hrManager);
        var hr = Staff("hr", withPassword: true, scope); var noDocs = Staff("hrnodocs", false, scope);
        db.Users.AddRange(hr, noDocs);
        db.Set<UserRole>().AddRange(new UserRole { UserId = hr.Id, RoleId = hrManager.Id }, new UserRole { UserId = noDocs.Id, RoleId = hrManager.Id });
        db.Set<UserPermissionOverride>().AddRange(
            Grant(hr.Id, "employees.read"), Grant(hr.Id, "employees.write"), Grant(hr.Id, "employees.documents"), Grant(hr.Id, "employees.sensitive"),
            Grant(noDocs.Id, "employees.write"), Grant(noDocs.Id, "employees.read"));
        HrUserId = hr.Id;
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDeciderAsync(db, TenantId);
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        HrToken = await MintAsync(db, tokens, hr.Id);
        HrNoDocsToken = await MintAsync(db, tokens, noDocs.Id);
    }

    private User Staff(string name, bool withPassword, IServiceScope scope) => new()
    {
        TenantId = TenantId, Email = $"{name}@esign-p4.local", NormalizedEmail = $"{name}@esign-p4.local".ToUpperInvariant(), FullName = name,
        PasswordHash = withPassword ? scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password) : "no-login-path",
        Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
    };

    private UserPermissionOverride Grant(Guid userId, string permission) => new()
    {
        TenantId = TenantId, UserId = userId, PermissionKey = permission, Effect = "Allow", IsActive = true, Reason = "ESIGN P4 test",
    };

    private async Task<string> MintAsync(ZayraDbContext db, ITokenService tokens, Guid userId)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == userId);
        var scope = EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>());
        return tokens.CreateAccessToken(user, AuthService.GetRoles(user), AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(), scope, out _);
    }

    /// <summary>An ESS-only login linked to a new employee, with a real English and Arabic name to type back.</summary>
    public async Task<EssEmployeeHandle> NewEmployeeAsync(string english = "Layla Hassan", string arabic = "ليلى حسن", Action<Employee>? configure = null)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var n = Interlocked.Increment(ref _seq);
        var user = new User
        {
            TenantId = TenantId, Email = $"emp{n}@esign-p4.local", NormalizedEmail = $"EMP{n}@ESIGN-P4.LOCAL", FullName = english,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password),
            Status = "Active", AccessMode = "ESSOnly", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        var employee = new Employee
        {
            TenantId = TenantId, CompanyId = CompanyId, EmployeeCode = $"S{n}", FullName = english, EnglishName = english, ArabicName = arabic,
            Status = "Active", JoiningDate = DateTime.UtcNow.Date, NationalityCode = "SA", Nationality = "Saudi", UserAccountId = user.Id,
        };
        configure?.Invoke(employee);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = TenantId, EmployeeId = employee.Id, UserId = user.Id, AccessMode = "ESSOnly", Status = "Active", IsPrimary = true, RequiresPasswordSetup = false });
        foreach (var permission in new[] { "ess.read", "ess.write" }) db.Set<UserPermissionOverride>().Add(Grant(user.Id, permission));
        await db.SaveChangesAsync();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return new EssEmployeeHandle(employee.Id, user.Id, await MintAsync(db, tokens, user.Id), user.Email);
    }

    /// <summary>Stores a PDF for the employee. <paramref name="requireSignature"/> mirrors what HR's request does.</summary>
    public async Task<SignDoc> NewDocumentAsync(EssEmployeeHandle employee, byte[]? bytes = null, bool requireSignature = true, string type = "Employment contract", string? fileName = null)
    {
        bytes ??= [.. PdfBytes, .. Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"))];
        var id = Guid.NewGuid();
        var key = $"storage/documents/{TenantId:N}/{id:N}.pdf";
        Storage.Objects[key] = bytes;
        await WithDbAsync(async db =>
        {
            db.EmployeeDocuments.Add(new EmployeeDocument
            {
                Id = id, TenantId = TenantId, CompanyId = CompanyId, EmployeeId = employee.Id, DocumentType = type, DocumentCategory = "Contract",
                FileName = fileName ?? $"{type.Replace(' ', '_')}.pdf", ContentType = "application/pdf", StorageUrl = key, ApprovalStatus = "Approved",
                SignatureRequiredAtUtc = requireSignature ? DateTime.UtcNow : null, SignatureRequestedBy = requireSignature ? HrUserId : null,
            });
            await db.SaveChangesAsync();
        });
        return new SignDoc(id, bytes, Sha(bytes), key);
    }

    public static string Sha(byte[] bytes) => SignatureChain.Sha256Hex(bytes);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null, string? stepUp = null, string? idempotencyKey = null, string? userAgent = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (stepUp is not null) request.Headers.Add(EsignConstants.StepUpHeader, stepUp);
        if (idempotencyKey is not null) request.Headers.Add(EsignConstants.IdempotencyHeader, idempotencyKey);
        if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
        return _client.SendAsync(request);
    }

    /// <summary>The employee re-enters their password for the e-sign purpose; returns the X-Step-Up token.</summary>
    public async Task<string> StepUpAsync(EssEmployeeHandle e, string purpose = StepUpPurposes.Esign)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/auth/step-up", e.Token, new { purpose, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stepUpToken").GetString()!;
    }

    public static object Body(SignDoc doc, string typedName, string? sha = null, string consent = ConsentCatalog.Current, string language = "en", string? deviceId = "dev-1") =>
        new { documentSha256 = sha ?? doc.Sha256, consentVersion = consent, typedName, language, deviceId };

    public async Task<HttpResponseMessage> SignAsync(EssEmployeeHandle e, SignDoc doc, string typedName, string? stepUp = null, string? sha = null, string? idem = null, string consent = ConsentCatalog.Current)
    {
        stepUp ??= await StepUpAsync(e);
        return await SendAsync(HttpMethod.Post, $"/api/ess/signatures/{doc.Id}/sign", e.Token, Body(doc, typedName, sha, consent), stepUp, idem ?? Guid.NewGuid().ToString("N"), "EsignTest/1.0");
    }

    public async Task<T> WithDbAsync<T>(Func<ZayraDbContext, Task<T>> work)
    {
        using var scope = Host.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ZayraDbContext>());
    }

    public async Task WithDbAsync(Func<ZayraDbContext, Task> work) => await WithDbAsync(async db => { await work(db); return 0; });

    public static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}

[CollectionDefinition("EsignP4")]
public sealed class EsignP4Collection : ICollectionFixture<EsignFixture> { }
