using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
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
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Ess;

/// <summary>Counts every outbound HttpClient call made through IHttpClientFactory (AC62: the flow calls no external service).</summary>
public sealed class RecordingHandler : DelegatingHandler
{
    public static readonly List<string> Calls = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add(request.RequestUri?.ToString() ?? "?");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

public sealed class SwitchableScanner : IMalwareScanner
{
    public ScanResult Result { get; set; } = new(ScanVerdict.Clean);
    public ScanVerdict Verdict { get => Result.Verdict; set => Result = new ScanResult(value); }
    public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> bytes, string fileName, CancellationToken ct) => Task.FromResult(Result);
}

/// <summary>Records which entity types each SaveChanges wrote, so a test can prove a decision, its effect and its audit land together.</summary>
public sealed class SaveRecorder : SaveChangesInterceptor
{
    public static readonly List<HashSet<string>> Saves = [];
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var names = eventData.Context!.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Metadata.ClrType.Name).ToHashSet();
        lock (Saves) Saves.Add(names);
        return base.SavingChangesAsync(eventData, result, ct);
    }
}

public sealed record EssEmployeeHandle(int Id, Guid UserId, string Token, string Email);

/// <summary>
/// One SQLite-backed host for the P2 HTTP tests: a Saudi company, an Emirati company, HR users with and without the
/// sensitive/override permissions, a user scoped to the other company, and a factory for linked ESS employees.
/// </summary>
public sealed class EssFixture : IAsyncLifetime
{
    public const string Password = "Passw0rd!xyz-Ess";
    private SqliteConnection _anchor = null!;
    private HttpClient _client = null!;
    private int _seq = 1000;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid OtherCompanyId { get; private set; }
    public SwitchableScanner Scanner { get; } = new();
    public string HrToken { get; private set; } = null!;          // employees.write + sensitive, HR Manager
    public string HrNoSensitiveToken { get; private set; } = null!; // employees.write, HR Officer
    public string HrOverrideToken { get; private set; } = null!;  // employees.write + sensitive + override_validation
    public string FinanceToken { get; private set; } = null!;     // approvals.decide + payroll.approve
    public string HrScopedOtherToken { get; private set; } = null!; // HR limited to the other company
    public string HrDocumentsToken { get; private set; } = null!;   // employees.read + employees.documents + sensitive (the workspace's document download)
    public string HrNoDocumentsToken { get; private set; } = null!;  // employees.write + read + sensitive, but no employees.documents
    public string HrSecondApproverToken { get; private set; } = null!;  // HR Manager + approvals.decide (a second, different step-2 decider)
    public string FinanceApproverToken { get; private set; } = null!;   // Finance Approver + approvals.decide + payroll.approve
    public string FinanceNoPayrollToken { get; private set; } = null!;  // Finance Approver + approvals.decide, but NOT payroll.approve
    public Guid HrUserId { get; private set; }
    public Guid FinanceUserId { get; private set; }

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:ess-p2-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection, services =>
        {
            services.AddSingleton<IMalwareScanner>(Scanner);
            services.RemoveAll<DbContextOptions<ZayraDbContext>>(); services.RemoveAll<DbContextOptions>(); services.RemoveAll<ZayraDbContext>();
            services.AddDbContext<ZayraDbContext>(o => o.UseSqlite(connection).AddInterceptors(new SaveRecorder())
                .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
            services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.AdditionalHandlers.Add(new RecordingHandler())));
        });
        _client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        var tenant = new Tenant { Name = "ESS P2", Slug = $"ess-p2-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        TenantId = tenant.Id;
        var company = new Company { TenantId = TenantId, LegalNameEn = "Saudi Co", TradeName = "Saudi Co", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
        var other = new Company { TenantId = TenantId, LegalNameEn = "UAE Co", TradeName = "UAE Co", CountryCode = "AE", IsActive = true, DefaultCurrency = "AED" };
        db.Companies.AddRange(company, other);
        CompanyId = company.Id; OtherCompanyId = other.Id;
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = TenantId, Plan = "Enterprise", Status = "Active", MaxEmployees = 1000 });
        var hrManager = new Role { TenantId = TenantId, Name = "HR Manager", NormalizedName = "HR MANAGER" };
        var hrOfficer = new Role { TenantId = TenantId, Name = "HR Officer", NormalizedName = "HR OFFICER" };
        var financeApprover = new Role { TenantId = TenantId, Name = "Finance Approver", NormalizedName = "FINANCE APPROVER" };
        db.Roles.AddRange(hrManager, hrOfficer, financeApprover);
        var hr = Staff("hr"); var docs = Staff("hrdocs"); var noSens = Staff("hrnosens"); var ovr = Staff("hroverride"); var fin = Staff("finance"); var scoped = Staff("hrscoped"); var noDocs = Staff("hrnodocs"); var hr2 = Staff("hr2"); var finAppr = Staff("finappr"); var finNoPay = Staff("finnopay");
        scoped.IsGroupScope = false;   // limited to one company (UserEntityAccess below)
        db.Users.AddRange(hr, noSens, ovr, fin, scoped, docs, noDocs, hr2, finAppr, finNoPay);
        db.Set<UserRole>().AddRange(new UserRole { UserId = hr.Id, RoleId = hrManager.Id }, new UserRole { UserId = noSens.Id, RoleId = hrOfficer.Id },
            new UserRole { UserId = ovr.Id, RoleId = hrManager.Id }, new UserRole { UserId = fin.Id, RoleId = hrManager.Id }, new UserRole { UserId = scoped.Id, RoleId = hrOfficer.Id }, new UserRole { UserId = docs.Id, RoleId = hrManager.Id }, new UserRole { UserId = noDocs.Id, RoleId = hrManager.Id },
            new UserRole { UserId = hr2.Id, RoleId = hrManager.Id }, new UserRole { UserId = finAppr.Id, RoleId = financeApprover.Id }, new UserRole { UserId = finNoPay.Id, RoleId = financeApprover.Id });
        db.Set<UserPermissionOverride>().AddRange(
            Grant(hr.Id, "employees.write"), Grant(hr.Id, "employees.read"), Grant(hr.Id, "employees.sensitive"), Grant(hr.Id, "approvals.decide"), Grant(hr.Id, "employees.documents"),
            Grant(hr2.Id, "approvals.decide"), Grant(finAppr.Id, "approvals.decide"), Grant(finAppr.Id, "payroll.approve"), Grant(finNoPay.Id, "approvals.decide"),
            Grant(noDocs.Id, "employees.write"), Grant(noDocs.Id, "employees.read"), Grant(noDocs.Id, "employees.sensitive"),
            Grant(noSens.Id, "employees.write"), Grant(noSens.Id, "employees.read"), Grant(noSens.Id, "employees.documents"),
            Grant(ovr.Id, "employees.documents"), Grant(ovr.Id, "employees.write"), Grant(ovr.Id, "employees.read"), Grant(ovr.Id, "employees.sensitive"), Grant(ovr.Id, "employees.override_validation"),
            Grant(fin.Id, "approvals.decide"), Grant(fin.Id, "payroll.approve"), Grant(fin.Id, "employees.read"),
            Grant(scoped.Id, "employees.documents"), Grant(scoped.Id, "employees.write"), Grant(scoped.Id, "employees.read"),
            Grant(docs.Id, "employees.read"), Grant(docs.Id, "employees.documents"), Grant(docs.Id, "employees.sensitive"));
        db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = TenantId, UserId = scoped.Id, CompanyId = other.Id, Role = "HR Officer", IsActive = true });
        HrUserId = hr.Id; FinanceUserId = fin.Id;
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDeciderAsync(db, TenantId);
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        HrToken = await MintAsync(db, tokens, hr.Id, true);
        HrNoSensitiveToken = await MintAsync(db, tokens, noSens.Id, true);
        HrOverrideToken = await MintAsync(db, tokens, ovr.Id, true);
        FinanceToken = await MintAsync(db, tokens, fin.Id, true);
        HrScopedOtherToken = await MintAsync(db, tokens, scoped.Id, false, [OtherCompanyId]);
        HrDocumentsToken = await MintAsync(db, tokens, docs.Id, true);
        HrNoDocumentsToken = await MintAsync(db, tokens, noDocs.Id, true);
        HrSecondApproverToken = await MintAsync(db, tokens, hr2.Id, true);
        FinanceApproverToken = await MintAsync(db, tokens, finAppr.Id, true);
        FinanceNoPayrollToken = await MintAsync(db, tokens, finNoPay.Id, true);
    }

    private User Staff(string name) => new()
    {
        TenantId = TenantId, Email = $"{name}@ess-p2.local", NormalizedEmail = $"{name}@ess-p2.local".ToUpperInvariant(), FullName = name,
        PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
    };

    private UserPermissionOverride Grant(Guid userId, string permission) => new()
    {
        TenantId = TenantId, UserId = userId, PermissionKey = permission, Effect = "Allow", IsActive = true, Reason = "ESS P2 test",
    };

    private async Task<string> MintAsync(ZayraDbContext db, ITokenService tokens, Guid userId, bool groupScope, Guid[]? companies = null)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == userId);
        var grants = companies is null ? Array.Empty<EntityAccessGrant>() : companies.Select(c => new EntityAccessGrant(c, "HR Officer")).ToArray();
        var scope = EntityScopeClaims.Resolve(groupScope, grants, companies ?? Array.Empty<Guid>());
        return tokens.CreateAccessToken(user, AuthService.GetRoles(user), AuthService.GetPermissions(user), user.Tenant!, grants, scope, out _);
    }

    /// <summary>An ESS-only login linked to a new employee. <paramref name="status"/> Active or Invited.</summary>
    public async Task<EssEmployeeHandle> NewEmployeeAsync(string nationalityCode = "SA", string status = "Active", Guid? companyId = null,
        Action<Employee>? configure = null, bool withPassword = true)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var n = Interlocked.Increment(ref _seq);
        var user = new User
        {
            TenantId = TenantId, Email = $"emp{n}@ess-p2.local", NormalizedEmail = $"EMP{n}@ESS-P2.LOCAL", FullName = $"Employee {n}",
            PasswordHash = withPassword ? scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password) : "no-login-path",
            Status = "Active", AccessMode = "ESSOnly", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        var employee = new Employee
        {
            TenantId = TenantId, CompanyId = companyId ?? CompanyId, EmployeeCode = $"E{n}", FullName = $"Employee {n}", EnglishName = $"Employee {n}",
            Status = status, JoiningDate = DateTime.UtcNow.Date, NationalityCode = nationalityCode, UserAccountId = user.Id,
            Nationality = nationalityCode == "SA" ? "Saudi" : "Pakistani",
        };
        configure?.Invoke(employee);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = TenantId, EmployeeId = employee.Id, UserId = user.Id, AccessMode = "ESSOnly", Status = "Active", IsPrimary = true, RequiresPasswordSetup = false });
        foreach (var permission in new[] { "ess.read", "ess.write" }) db.Set<UserPermissionOverride>().Add(Grant(user.Id, permission));
        await db.SaveChangesAsync();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return new EssEmployeeHandle(employee.Id, user.Id, await MintAsync(db, tokens, user.Id, true), user.Email);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null, string? stepUp = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (stepUp is not null) request.Headers.Add(EssCompletion.StepUpHeader, stepUp);
        return _client.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, string? token, string? stepUp = null)
    {
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (stepUp is not null) request.Headers.Add(EssCompletion.StepUpHeader, stepUp);
        return _client.SendAsync(request);
    }

    /// <summary>The employee re-enters their password; returns the X-Step-Up token.</summary>
    public async Task<string> StepUpAsync(EssEmployeeHandle e, string purpose = StepUpPurposes.EssSensitive)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/auth/step-up", e.Token, new { purpose, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stepUpToken").GetString()!;
    }

    public async Task<T> WithDbAsync<T>(Func<ZayraDbContext, Task<T>> work)
    {
        using var scope = Host.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ZayraDbContext>());
    }

    public async Task WithDbAsync(Func<ZayraDbContext, Task> work) => await WithDbAsync(async db => { await work(db); return 0; });

    /// <summary>HR asks the employee for sections (the additive body of POST /api/employee-completion/{id}).</summary>
    public async Task RequestSectionsAsync(int employeeId, params string[] sections)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/employee-completion/{employeeId}", HrToken, sections.Length == 0 ? null : new { sections });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
