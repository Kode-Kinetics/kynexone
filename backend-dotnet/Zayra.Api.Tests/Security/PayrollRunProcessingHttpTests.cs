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
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// <c>POST /api/payroll/runs/{id}/process</c> and <c>GET /api/payroll/runs/{id}/processing</c> through the REAL
/// Program.cs pipeline (AuthorizationPipelineHost): routing, the permission policies, tenant scoping and the JSON
/// contract the UI builds against. The host's background workers are removed, so a queued job stays Queued.
/// </summary>
public sealed class PayrollRunProcessingHttpTests(PayrollRunProcessingHttpFixture f) : IClassFixture<PayrollRunProcessingHttpFixture>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Process_answers_202_with_the_agreed_body_and_a_double_post_returns_the_same_job()
    {
        using var first = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.DraftRunId}/process", f.Token("writer"));
        var text = await first.Content.ReadAsStringAsync();
        first.StatusCode.Should().Be(HttpStatusCode.Accepted, text);
        using var body = JsonDocument.Parse(text);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("runId", "jobId", "status");
        body.RootElement.GetProperty("runId").GetGuid().Should().Be(f.DraftRunId);
        body.RootElement.GetProperty("status").GetString().Should().Be("Queued");
        var jobId = body.RootElement.GetProperty("jobId").GetGuid();

        using var second = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.DraftRunId}/process", f.Token("writer"));
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetGuid().Should().Be(jobId);

        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.BackgroundJobs.IgnoreQueryFilters().CountAsync(j => j.TenantId == f.TenantId && j.IdempotencyKey == $"payroll.run.process:{f.DraftRunId:D}"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Status_reports_idle_then_queued_with_the_agreed_fields()
    {
        using var idle = await f.Send(HttpMethod.Get, $"/api/payroll/runs/{f.IdleRunId}/processing", f.Token("reader"));
        idle.StatusCode.Should().Be(HttpStatusCode.OK);
        var idleBody = JsonDocument.Parse(await idle.Content.ReadAsStringAsync()).RootElement;
        idleBody.GetProperty("status").GetString().Should().Be("Idle");
        idleBody.GetProperty("runId").GetGuid().Should().Be(f.IdleRunId);
        idleBody.GetProperty("jobId").ValueKind.Should().Be(JsonValueKind.Null);
        idleBody.GetProperty("processedEmployees").GetInt32().Should().Be(0);
        idleBody.GetProperty("totalEmployees").GetInt32().Should().Be(0);
        foreach (var name in new[] { "startedAtUtc", "finishedAtUtc", "errorCode", "errorMessage" })
            idleBody.GetProperty(name).ValueKind.Should().Be(JsonValueKind.Null, name);

        using var queue = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.IdleRunId}/process", f.Token("writer"));
        queue.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var queued = await f.Send(HttpMethod.Get, $"/api/payroll/runs/{f.IdleRunId}/processing", f.Token("reader"));
        var queuedBody = JsonDocument.Parse(await queued.Content.ReadAsStringAsync()).RootElement;
        queuedBody.GetProperty("status").GetString().Should().Be("Queued");
        queuedBody.GetProperty("jobId").GetGuid().Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_run_that_is_already_processed_is_refused_400_synchronously_and_queues_nothing()
    {
        using var response = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.ProcessedRunId}/process", f.Token("writer"));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        JsonDocument.Parse(text).RootElement.GetProperty("message").GetString().Should().Contain("cannot be reprocessed");

        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.BackgroundJobs.IgnoreQueryFilters().AnyAsync(j => j.IdempotencyKey == $"payroll.run.process:{f.ProcessedRunId:D}"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Queueing_needs_payroll_write_and_reading_the_status_needs_payroll_read()
    {
        // A reader may look but not queue.
        using (var forbiddenPost = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.DraftRunId}/process", f.Token("reader")))
            forbiddenPost.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Someone with neither may do neither.
        using (var noPost = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.DraftRunId}/process", f.Token("nobody")))
            noPost.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var noGet = await f.Send(HttpMethod.Get, $"/api/payroll/runs/{f.DraftRunId}/processing", f.Token("nobody")))
            noGet.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var anonymous = await f.Send(HttpMethod.Get, $"/api/payroll/runs/{f.DraftRunId}/processing", null))
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // A writer without the read key cannot read the status either (permissions are not implied).
        using (var writerOnlyGet = await f.Send(HttpMethod.Get, $"/api/payroll/runs/{f.DraftRunId}/processing", f.Token("writeOnly")))
            writerOnlyGet.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Another_tenants_user_gets_404_for_status_and_for_queueing_not_403()
    {
        using var status = await f.Send(HttpMethod.Get, $"/api/payroll/runs/{f.DraftRunId}/processing", f.Token("otherTenant"));
        status.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var process = await f.Send(HttpMethod.Post, $"/api/payroll/runs/{f.DraftRunId}/process", f.Token("otherTenant"));
        process.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var unknown = await f.Send(HttpMethod.Get, $"/api/payroll/runs/{Guid.NewGuid()}/processing", f.Token("reader"));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class PayrollRunProcessingHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid DraftRunId { get; private set; }
    public Guid IdleRunId { get; private set; }
    public Guid ProcessedRunId { get; private set; }

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:payroll-run-processing-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        // Nothing may claim the jobs: a worker that finishes the first job between two POSTs frees its key, and the
        // second POST then (correctly) queues a new one, which is not what the idempotency tests are about.
        Host = new AuthorizationPipelineHost(connection,
            hostSettings: new Dictionary<string, string?> { ["BackgroundJobs:WorkerEnabled"] = "false" });
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            foreach (var (tenant, name) in new[] { (TenantId, "payroll-run-processing"), (OtherTenantId, "payroll-run-processing-other") })
                db.Tenants.Add(new Tenant { Id = tenant, Name = name, Slug = $"{name}-{tenant:N}", IsActive = true });
            var company = new Company { TenantId = TenantId, LegalNameEn = "Run Co", CountryCode = "SA", IsActive = true };
            db.Companies.Add(company);
            db.PayrollRuns.AddRange(
                new PayrollRun { TenantId = TenantId, CompanyId = company.Id, Year = 2026, Month = 5, Status = "Draft" },
                new PayrollRun { TenantId = TenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft" },
                new PayrollRun { TenantId = TenantId, CompanyId = company.Id, Year = 2026, Month = 4, Status = "Processed" });
            await db.SaveChangesAsync();
            DraftRunId = db.PayrollRuns.Local.Single(r => r.Month == 5).Id;
            IdleRunId = db.PayrollRuns.Local.Single(r => r.Month == 6).Id;
            ProcessedRunId = db.PayrollRuns.Local.Single(r => r.Month == 4).Id;

            var users = new Dictionary<string, (Guid Tenant, string[] Permissions)>
            {
                ["writer"] = (TenantId, ["payroll.read", "payroll.write"]),
                ["reader"] = (TenantId, ["payroll.read"]),
                ["writeOnly"] = (TenantId, ["payroll.write"]),
                ["nobody"] = (TenantId, []),
                ["otherTenant"] = (OtherTenantId, ["payroll.read", "payroll.write"]),
            };
            foreach (var (name, (tenant, permissions)) in users)
            {
                var email = Email(name, tenant);
                var user = new User
                {
                    TenantId = tenant, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name,
                    PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true,
                    IsEmailConfirmed = true, IsGroupScope = true, MFAEnabled = true, MfaSecretEncrypted = "enrolled-for-test",
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
                db.Set<UserPermissionOverride>().AddRange(permissions.Select(p => new UserPermissionOverride
                {
                    TenantId = tenant, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "payroll processing http test",
                }));
                await db.SaveChangesAsync();
            }
        }
        foreach (var name in new[] { "writer", "reader", "writeOnly", "nobody" }) _tokens[name] = await MintAsync(name, TenantId);
        _tokens["otherTenant"] = await MintAsync("otherTenant", OtherTenantId);
    }

    private static string Email(string name, Guid tenant) => $"{name}-{tenant:N}@payroll-run-http.local";

    public string Token(string who) => _tokens[who];

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    private async Task<string> MintAsync(string name, Guid tenant)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = Email(name, tenant);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == tenant && u.Email == email);
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
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
