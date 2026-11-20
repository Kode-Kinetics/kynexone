using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Recruitment;

/// <summary>
/// Create Opening is for an APPROVED requisition only. Before the guard, POST api/recruitment/openings with a requisition that was
/// already Converted (or still Draft, Rejected, ...) answered 201 and created a second, stray opening. Through the REAL pipeline
/// (real JWT, real role gates, real company filters): only an Approved requisition opens, the source becomes Converted in the same
/// save, a second create for it is a 409 that names why, and an opening created WITHOUT a requisition behaves as before.
/// The concurrent exactly-one-opening proof needs real Postgres: <see cref="OpeningFromRequisitionConcurrencyPostgresTests"/>.
/// </summary>
public sealed class OpeningFromRequisitionGuardTests(OpeningGuardFixture f) : IClassFixture<OpeningGuardFixture>
{
    private const string AlreadyHasOpening = "This requisition already has an opening.";
    private const string OnlyApproved = "Only an approved requisition can be opened as a job.";

    [Fact]
    public async Task ApprovedRequisition_BecomesAnOpening_AndTheRequisitionIsConverted()
    {
        var id = await f.NewRequisition("Approved");
        using var r = await f.Create(id);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var openings = await f.OpeningsOf(id);
        openings.Should().ContainSingle();
        openings[0].Status.Should().Be("Open");
        openings[0].CompanyId.Should().Be(f.CompanyA);
        (await f.StatusOf(id)).Should().Be("Converted");
    }

    [Fact]
    public async Task ConvertedRequisition_IsRefused_With409_AndNoSecondOpening()
    {
        var id = await f.NewRequisition("Converted");
        var before = await f.OpeningCount();
        using var r = await f.Create(id);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("message").GetString().Should().Be(AlreadyHasOpening);
        (await f.OpeningsOf(id)).Should().BeEmpty();
        (await f.OpeningCount()).Should().Be(before, "a refused create must not burn a job code or leave a stray row");
        (await f.StatusOf(id)).Should().Be("Converted");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Submitted")]
    [InlineData("PendingApproval")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public async Task NotYetApprovedRequisition_IsRefused_With409_AndStaysAsItWas(string status)
    {
        var id = await f.NewRequisition(status);
        using var r = await f.Create(id);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("message").GetString().Should().Be(OnlyApproved);
        body.GetProperty("status").GetString().Should().Be(status, "the answer names the status that blocked it");
        (await f.OpeningsOf(id)).Should().BeEmpty();
        (await f.StatusOf(id)).Should().Be(status);
    }

    [Fact]
    public async Task CreatingTwiceForTheSameApprovedRequisition_YieldsExactlyOneOpening()
    {
        var id = await f.NewRequisition("Approved");
        using var first = await f.Create(id);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        using var second = await f.Create(id);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString().Should().Be(AlreadyHasOpening);
        (await f.OpeningsOf(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task OpeningWithoutARequisition_IsCreatedAsBefore()
    {
        using var r = await f.Create(null);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var created = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        created.GetProperty("status").GetString().Should().Be("Open");
        created.GetProperty("requisitionId").ValueKind.Should().Be(JsonValueKind.Null);
        created.GetProperty("jobCode").GetString().Should().StartWith("JOB-");
        using var again = await f.Create(null);
        again.StatusCode.Should().Be(HttpStatusCode.Created, "a stand-alone opening has no requisition to guard");
    }

    [Fact]
    public async Task UnknownRequisition_IsStillABadRequest()
    {
        using var r = await f.Create(Guid.NewGuid());
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class OpeningGuardFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private string _hr = "";
    private readonly ErrorCapture _errors = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    private int _sequence;

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:opening-guard-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection, services => services.AddLogging(b => b.AddProvider(_errors)));
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Guard", Slug = $"guard-{TenantId:N}", IsActive = true, AccountType = TenantAccountTypes.Group });
        db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Alpha", CountryCode = "SA", IsActive = true });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        var email = $"hr-{TenantId:N}@guard.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = "HR", PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = false,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var role = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == "HR Manager");
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = TenantId, UserId = user.Id, CompanyId = CompanyA, Role = "HR", IsActive = true });
        await db.SaveChangesAsync();
        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        var grants = loaded.EntityAccesses.Where(a => a.IsActive).Select(a => new EntityAccessGrant(a.CompanyId, a.Role, a.GrantMode)).ToList();
        _hr = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(loaded, AuthService.GetRoles(loaded),
            AuthService.GetPermissions(loaded), loaded.Tenant!, grants,
            EntityScopeClaims.Resolve(loaded.IsGroupScope, grants, Array.Empty<Guid>()), out _);
    }

    public async Task<Guid> NewRequisition(string status)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var n = Interlocked.Increment(ref _sequence);
        var r = new ManpowerRequisition
        {
            TenantId = TenantId, CompanyId = CompanyA, RequisitionNumber = $"MRQ-G-{n:000}", Status = status,
            DepartmentName = "Operations", DesignationTitle = "Site Supervisor", HeadCount = 1,
        };
        db.ManpowerRequisitions.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    public async Task<HttpResponseMessage> Create(Guid? requisitionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/recruitment/openings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _hr);
        request.Content = JsonContent.Create(new
        {
            requisitionId, title = "Site Supervisor", departmentName = "Operations", designationTitle = "Site Supervisor", employmentType = "Full-Time",
            headCount = 1, description = "d", requirements = "r", responsibilities = "p", salaryFrom = 1000m, salaryTo = 2000m, location = "HQ",
            assignedHrName = "HR", companyId = (Guid?)null,
        });
        var response = await Client.SendAsync(request);
        if ((int)response.StatusCode >= 500)
            throw new Xunit.Sdk.XunitException($"POST openings answered {(int)response.StatusCode}: {_errors.Last()}");
        return response;
    }

    public async Task<List<JobOpening>> OpeningsOf(Guid requisitionId)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.JobOpenings.IgnoreQueryFilters().AsNoTracking().Where(o => o.TenantId == TenantId && o.RequisitionId == requisitionId).ToListAsync();
    }

    public async Task<int> OpeningCount()
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.JobOpenings.IgnoreQueryFilters().AsNoTracking().CountAsync(o => o.TenantId == TenantId);
    }

    public async Task<string> StatusOf(Guid requisitionId)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return (await db.ManpowerRequisitions.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == requisitionId)).Status;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
