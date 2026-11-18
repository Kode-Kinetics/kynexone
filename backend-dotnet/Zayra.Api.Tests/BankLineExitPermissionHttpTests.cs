using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// Final gate security P2-D through the REAL HTTP pipeline (routing, JWT, claims, model binding): a bank-confirmation import
/// that carries reasoned exits needs payroll.wps_status.update (the manual-Paid permission) on top of the import's own
/// payroll.export. The plain import, without exits, still needs payroll.export only. No role's grants change: the test gives a
/// synthetic custom role exactly payroll.export. SQLite stands in for Postgres.
/// </summary>
public sealed class BankLineExitPermissionHttpTests(BankLineExitHttpFixture f) : IClassFixture<BankLineExitHttpFixture>
{
    private string Path => $"/api/payroll/payment-batches/{f.BatchId}/bank-confirmations";

    private static string Payload(bool withExit, Guid recordId) => JsonSerializer.Serialize(new
    {
        fileName = $"letter-{Guid.NewGuid():N}.json",
        rows = new[] { new { wpsReference = "W1", amount = 100m, status = "PENDING", valueDate = "2026-07-30", bankReference = Guid.NewGuid().ToString("N") } },
        exits = withExit
            ? new[] { new { kind = "allow_paid_to_rejected", paymentRecordId = recordId, reason = "Bank letter", bankReference = "BNK-1" } }
            : null,
    });

    private async Task<(HttpStatusCode Status, string Body)> Post(string tokenKey, bool withExit)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(Payload(withExit, f.RecordId), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(tokenKey));
        using var response = await f.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ImportOnly_WithAnExit_IsForbidden_WithAClearCode()
    {
        var (status, body) = await Post("ImportOnly", withExit: true);
        status.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("exit_needs_payment_status_permission");
    }

    [Fact]
    public async Task ImportOnly_WithoutAnExit_StillImports()
    {
        var (status, body) = await Post("ImportOnly", withExit: false);
        status.Should().Be(HttpStatusCode.OK, body);
    }

    [Fact]
    public async Task ImportAndPaymentStatus_WithAnExit_PassesThePermission()
    {
        var (status, body) = await Post("ImportAndStatus", withExit: true);
        // Past the permission: the exit is then judged on its merits (no open hold here, so 400), never 403.
        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("exit_matches_no_open_hold");
    }

    [Fact]
    public async Task PaymentStatusOnly_CannotImportAtAll()
    {
        var (status, body) = await Post("StatusOnly", withExit: true);
        status.Should().Be(HttpStatusCode.Forbidden, body);
    }
}

public sealed class BankLineExitHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid BatchId { get; private set; }
    public Guid RecordId { get; private set; }
    public string Token(string key) => _tokens[key];

    private static readonly (string Key, string[] Permissions)[] Users =
    {
        ("ImportOnly", new[] { "payroll.export" }),
        ("ImportAndStatus", new[] { "payroll.export", "payroll.wps_status.update" }),
        ("StatusOnly", new[] { "payroll.wps_status.update" }),
    };

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:bank-exit-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Exits", Slug = $"ex-{TenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "Exit Co", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true });
        var run = new PayrollRun { TenantId = TenantId, CompanyId = CompanyId, Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch { TenantId = TenantId, PayrollRunId = run.Id, BatchNumber = "PB-EXIT-1", Currency = "SAR", TotalAmount = 100m };
        var record = new PayrollPaymentRecord
        {
            TenantId = TenantId, PaymentBatchId = batch.Id, EmployeeId = 9001, Amount = 100m, Status = PaymentRecordStatuses.Pending,
            Iban = "SA0380000000608010167519", WpsReference = "W1",
        };
        db.AddRange(run, batch, record);
        await db.SaveChangesAsync();
        BatchId = batch.Id;
        RecordId = record.Id;
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);
        foreach (var (key, permissions) in Users) await AddUser(db, key, permissions);
        foreach (var (key, _) in Users) await IssueToken(scope, key);
    }

    /// <summary>A synthetic custom role holding exactly <paramref name="permissions"/> (no seeded role is changed).</summary>
    private async Task AddUser(ZayraDbContext db, string key, string[] permissions)
    {
        var role = new Role { TenantId = TenantId, Name = $"Synthetic {key}", NormalizedName = $"SYNTHETIC {key}".ToUpperInvariant(), AuthorityLevel = 50 };
        db.Roles.Add(role);
        foreach (var p in permissions)
            db.Set<RolePermission>().Add(new RolePermission { RoleId = role.Id, PermissionId = (await db.Permissions.SingleAsync(x => x.Key == p)).Id });
        var email = $"{key}-{TenantId:N}@exit.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
    }

    private async Task IssueToken(IServiceScope scope, string key)
    {
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{key}-{TenantId:N}@exit.local";
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
            EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
