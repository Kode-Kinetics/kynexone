using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// The reconciliation endpoints through the REAL pipeline (Program.cs composition over TestServer, JWT validation, the
/// default-deny fallback, [HasPermission], tenant filters), with tokens minted for users holding the seeded role bundles.
/// The controller tests call the actions directly and so never execute any of that.
/// </summary>
public sealed class ReconHttpAuthorizationTests(ReconHttpFixture f) : IClassFixture<ReconHttpFixture>
{
    private string Recon(Guid? run = null) => $"/api/payroll/runs/{run ?? f.RunId}/recon";

    private static string Read(string body, string property) => JsonDocument.Parse(body).RootElement.GetProperty(property).GetString()!;

    // ── Authentication ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/employees?section=volume&category=NEW_JOINER")]
    [InlineData("GET", "/export?format=csv")]
    [InlineData("GET", "/signoff")]
    [InlineData("POST", "/signoff")]
    [InlineData("DELETE", "/signoff")]
    public async Task WithoutAToken_EveryReconEndpoint_Answers401(string method, string tail)
    {
        using var r = await f.Send(new HttpMethod(method), Recon() + tail, bearer: null, body: method == "GET" ? null : new { });
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {tail}");
    }

    [Fact]
    public async Task AnInvalidToken_Answers401()
    {
        using var r = await f.Send(HttpMethod.Get, Recon(), bearer: "not-a-jwt");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── payroll.read ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("/employees?section=volume&category=NEW_JOINER")]
    [InlineData("/export?format=csv")]
    [InlineData("/signoff")]
    public async Task WithoutPayrollRead_TheReadEndpoints_Answer403(string tail)
    {
        using var r = await f.Send(HttpMethod.Get, Recon() + tail, f.Token("Employee"));
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, tail);
    }

    [Fact]
    public async Task WithPayrollRead_TheReconciliationIsReadable_AndStatesWhetherASignoffIsRequired()
    {
        using var r = await f.Send(HttpMethod.Get, Recon(), f.Token("Auditor"));
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("mode").GetString().Should().Be("Full");
        body.GetProperty("signoffRequired").GetBoolean().Should().BeTrue();
        body.GetProperty("runId").GetGuid().Should().Be(f.RunId);
    }

    // ── payroll.approve ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutPayrollApprove_PostAndDeleteSignoff_Answer403_EvenForAReader()
    {
        using var post = await f.Send(HttpMethod.Post, Recon() + "/signoff", f.Token("Auditor"), new { });
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var delete = await f.Send(HttpMethod.Delete, Recon() + "/signoff", f.Token("Auditor"), new { reason = "Reprocessed after a late joiner" });
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var employeePost = await f.Send(HttpMethod.Post, Recon() + "/signoff", f.Token("Employee"), new { });
        employeePost.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Maker-checker ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Creator")]
    [InlineData("Processor")]
    public async Task TheUserWhoCreatedOrProcessedTheRun_CannotSignItOff(string who)
    {
        using var read = await f.Send(HttpMethod.Get, Recon(), f.Token(who));
        read.StatusCode.Should().Be(HttpStatusCode.OK, "the creator and the processor may read it; they may not sign it");
        var dto = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;

        using var sign = await f.Send(HttpMethod.Post, Recon() + "/signoff", f.Token(who), new
        {
            expectedProcessVersion = dto.GetProperty("processVersion").GetInt32(),
            expectedSnapshotHash = dto.GetProperty("snapshotHash").GetString(),
            acknowledgedFlaggedEmployeeCount = 0,
            explanations = Array.Empty<object>(),
        });
        var text = await sign.Content.ReadAsStringAsync();
        sign.StatusCode.Should().Be(HttpStatusCode.Forbidden, text);
        Read(text, "error").Should().Be("maker_checker_violation");
    }

    // ── Tenant isolation ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/employees?section=volume&category=NEW_JOINER")]
    [InlineData("GET", "/export?format=csv")]
    [InlineData("GET", "/signoff")]
    [InlineData("POST", "/signoff")]
    [InlineData("DELETE", "/signoff")]
    public async Task AnotherTenantsRunId_IsNotFound_NeverForbiddenOrReadable(string method, string tail)
    {
        using var r = await f.Send(new HttpMethod(method), Recon(f.OtherTenantRunId) + tail, f.Token("Finance Approver"),
            method == "GET" ? null : new { expectedProcessVersion = 1, expectedSnapshotHash = "x", acknowledgedFlaggedEmployeeCount = 0, explanations = Array.Empty<object>(), reason = "Reprocessed after a late joiner" });
        r.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {tail}: {await r.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task ARunIdThatDoesNotExist_IsAlsoNotFound()
    {
        using var r = await f.Send(HttpMethod.Get, Recon(Guid.NewGuid()), f.Token("Finance Approver"));
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Approve without a sign-off ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("Finance Approver")]
    public async Task ApproveWithoutASignoff_Answers409_ReconciliationNotReviewed_AndChangesNothing(string role)
    {
        using var r = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.RunId}/approve", f.Token(role), new { comments = "Looks right" });
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.Conflict, text);
        Read(text, "error").Should().Be("reconciliation_not_reviewed");
        Read(text, "reason").Should().Be("Missing");

        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.PayrollRuns.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == f.RunId)).Status.Should().Be("Processed");
    }

    [Fact]
    public async Task ApproveWithoutPayrollApprove_IsRefusedBeforeTheReconciliationIsConsulted()
    {
        using var r = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.RunId}/approve", f.Token("Auditor"), new { comments = "x" });
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── The positive control: the whole path works for a different approver ──────────────────────

    [Fact]
    public async Task ADifferentApprover_CanSignTheRunOff_OverHttp_AndTheSignoffIsThenVisible()
    {
        using var read = await f.Send(HttpMethod.Get, Recon(f.SignableRunId), f.Token("Finance Approver"));
        read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        var dto = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;

        var explanations = new List<object>();
        foreach (var section in dto.GetProperty("value").GetProperty("sections").EnumerateArray())
            foreach (var item in section.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("flagged").GetBoolean()))
                explanations.Add(new { scope = "Item", key = $"{section.GetProperty("kind").GetString()}:{item.GetProperty("code").GetString()}", text = "Reviewed: expected this month." });

        using var flagged = await f.Send(HttpMethod.Get, Recon(f.SignableRunId) + "/employees?section=value&bucket=flagged&page=1&pageSize=1", f.Token("Finance Approver"));
        var flaggedTotal = JsonDocument.Parse(await flagged.Content.ReadAsStringAsync()).RootElement.GetProperty("total").GetInt32();

        using var sign = await f.Send(HttpMethod.Post, Recon(f.SignableRunId) + "/signoff", f.Token("Finance Approver"), new
        {
            expectedProcessVersion = dto.GetProperty("processVersion").GetInt32(),
            expectedSnapshotHash = dto.GetProperty("snapshotHash").GetString(),
            acknowledgedFlaggedEmployeeCount = flaggedTotal,
            explanations,
        });
        sign.StatusCode.Should().BeOneOf(new[] { HttpStatusCode.OK, HttpStatusCode.Created }, await sign.Content.ReadAsStringAsync());

        using var after = await f.Send(HttpMethod.Get, Recon(f.SignableRunId) + "/signoff", f.Token("Auditor"));
        Read(await after.Content.ReadAsStringAsync(), "status").Should().Be("Active");
    }
}

public sealed class ReconHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid OtherCompanyId { get; } = Guid.NewGuid();
    /// <summary>A Processed run created by one user and processed by another; the run the refusals are tried against.</summary>
    public Guid RunId { get; private set; }
    /// <summary>Another tenant's Processed run.</summary>
    public Guid OtherTenantRunId { get; private set; }
    /// <summary>A Processed run whose creator and processor are neither of the signers: the positive control.</summary>
    public Guid SignableRunId { get; private set; }
    public string Token(string who) => _tokens[who];

    /// <summary>Seeded role bundles; "Creator" and "Processor" hold Payroll Manager and HR Manager and made the run.</summary>
    private static readonly (string Name, string Role)[] Users =
    {
        ("Creator", "Payroll Manager"), ("Processor", "HR Manager"), ("Finance Approver", "Finance Approver"),
        ("Admin", "Admin"), ("Auditor", "Auditor"), ("Employee", "Employee"),
    };

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:recon-http-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();

        foreach (var (tenant, company) in new[] { (TenantId, CompanyId), (OtherTenantId, OtherCompanyId) })
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Recon http", Slug = $"rch-{tenant:N}", IsActive = true });
            db.Companies.Add(new Company { Id = company, TenantId = tenant, LegalNameEn = "Recon Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" });
        }
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        var ids = new Dictionary<string, Guid>();
        foreach (var (name, role) in Users)
        {
            var email = $"{name.Replace(' ', '-')}-{TenantId:N}@rch.local";
            var user = new User
            {
                TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, PasswordHash = "no-login-path",
                Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
            db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
            await db.SaveChangesAsync();
            ids[name] = user.Id;
        }

        RunId = SeedPeriod(db, TenantId, CompanyId, firstEmployeeId: 1, created: ids["Creator"], processed: ids["Processor"]);
        SignableRunId = SeedPeriod(db, TenantId, CompanyId, firstEmployeeId: 100, created: ids["Creator"], processed: ids["Processor"], year: 2026, month: 12);
        OtherTenantRunId = SeedPeriod(db, OtherTenantId, OtherCompanyId, firstEmployeeId: 500, created: Guid.NewGuid(), processed: Guid.NewGuid());
        await db.SaveChangesAsync();

        foreach (var (name, _) in Users)
        {
            var email = $"{name.Replace(' ', '-')}-{TenantId:N}@rch.local";
            var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
                .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
                .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
                .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
            _tokens[name] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
                AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
                EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
        }
    }

    /// <summary>A Locked prior month and a Processed month after it, with one continuing employee and one joiner, so the bridge has movement.</summary>
    private static Guid SeedPeriod(ZayraDbContext db, Guid tenant, Guid company, int firstEmployeeId, Guid created, Guid processed, int year = 2026, int month = 10)
    {
        var priorMonth = new DateTime(year, month, 1).AddMonths(-1);
        var prior = new PayrollRun { TenantId = tenant, CompanyId = company, Year = priorMonth.Year, Month = priorMonth.Month, Status = "Locked", RunType = PayrollRunTypes.Regular, ProcessVersion = 1 };
        var current = new PayrollRun
        {
            TenantId = tenant, CompanyId = company, Year = year, Month = month, Status = "Processed", RunType = PayrollRunTypes.Regular,
            CreatedByUserId = created, ProcessedByUserId = processed, ProcessVersion = 1,
        };
        db.PayrollRuns.AddRange(prior, current);
        var continuing = firstEmployeeId;
        var joiner = firstEmployeeId + 1;
        db.Employees.Add(new Employee { Id = continuing, TenantId = tenant, CompanyId = company, EmployeeCode = $"E{continuing}", FullName = $"Employee {continuing}", Status = "Active", JoiningDate = new DateTime(2024, 1, 1) });
        db.Employees.Add(new Employee { Id = joiner, TenantId = tenant, CompanyId = company, EmployeeCode = $"E{joiner}", FullName = $"Employee {joiner}", Status = "Active", JoiningDate = new DateTime(year, month, 5) });
        Slip(db, tenant, company, prior, continuing, 1000m);
        Slip(db, tenant, company, current, continuing, 1200m);
        Slip(db, tenant, company, current, joiner, 900m);
        return current.Id;
    }

    private static void Slip(ZayraDbContext db, Guid tenant, Guid company, PayrollRun run, int emp, decimal basic)
    {
        db.PayrollSlips.Add(new PayrollSlip
        {
            TenantId = tenant, CompanyId = company, RunId = run.Id, EmployeeId = emp, EmployeeCode = $"E{emp}", EmployeeName = $"Employee {emp}",
            BasicSalary = basic, GrossSalary = basic, NetSalary = basic,
        });
        db.PayrollEarnings.Add(new PayrollEarning { TenantId = tenant, PayrollRunId = run.Id, EmployeeId = emp, ComponentCode = "BASIC", ComponentName = "Basic salary", Amount = basic, Source = "Salary" });
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
