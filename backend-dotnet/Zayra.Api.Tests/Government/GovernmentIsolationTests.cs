using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// CTO gate C12: two-tenant AND two-company isolation for both new tables and the credential endpoint, through the real global
/// query filters and write guards (a context carrying a signed-in user's tenant and company scope).
/// </summary>
public sealed class GovernmentIsolationTests
{
    private const string SecretA = "synthetic-secret-company-a-0001";
    private const string SecretB = "synthetic-secret-company-b-0002";

    private sealed record World(FeKit Kit, Guid TenantId, Guid CompanyA, Guid CompanyB, Guid OtherTenantId);

    /// <summary>One tenant with two active companies (so the company write guards apply) and a second tenant.</summary>
    private static async Task<World> SeedAsync()
    {
        var kit = new FeKit();
        var world = new World(kit, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = kit.Db(interceptors: false);
        db.Companies.Add(new Company { Id = world.CompanyA, TenantId = world.TenantId, LegalNameEn = "Synthetic A", IsActive = true });
        db.Companies.Add(new Company { Id = world.CompanyB, TenantId = world.TenantId, LegalNameEn = "Synthetic B", IsActive = true });
        await db.SaveChangesAsync();
        return world;
    }

    private static ClaimsPrincipal User(Guid tenant, Guid? company, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.Add(new Claim(EntityScopeContext.V2ClaimType, company is Guid c
            ? JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { c.ToString() } })
            : JsonSerializer.Serialize(new { v = 2, m = "group" })));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>A context over the world's database that sees what this signed-in user sees.</summary>
    private static (ZayraDbContext Db, DefaultHttpContext Http) ContextFor(World w, ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(w.Kit.DbName).Options,
            new HttpContextAccessor { HttpContext = http });
        return (db, http);
    }

    private static GovernmentCredentialsController Controller(World w, ZayraDbContext db, DefaultHttpContext http) =>
        new(new GovernmentCredentialStore(db, w.Kit.Keys, w.Kit.Kek, w.Kit.Options, new AuditService(db)), StepUpTestKit.AlwaysConfirmed(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

    private static async Task SeedCredentialAsync(World w, Guid tenant, Guid? company, string secret)
    {
        await using var db = w.Kit.Db(interceptors: false);
        await new GovernmentCredentialStore(db, w.Kit.Keys, w.Kit.Kek, w.Kit.Options, new AuditService(db)).SaveAsync(tenant, company,
            GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, secret, null, new RequestContext("ip", "ua", Guid.NewGuid(), tenant), default);
    }

    // ── Credentials ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompanyScopedUser_CannotReadWriteOrRemove_ASisterCompanysCredential()
    {
        var w = await SeedAsync();
        await SeedCredentialAsync(w, w.TenantId, w.CompanyB, SecretB);
        var (db, http) = ContextFor(w, User(w.TenantId, w.CompanyA, GovernmentCredentialsController.Permission));
        await using var _ = db;
        var controller = Controller(w, db, http);

        (await controller.Get(GovernmentBodies.Gosi, "sandbox", w.CompanyB, default)).Should().BeOfType<NotFoundObjectResult>();
        var put = await controller.Put(GovernmentBodies.Gosi, new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, w.CompanyB, null), default);
        ((ObjectResult)put).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await controller.Delete(GovernmentBodies.Gosi, "sandbox", w.CompanyB, default)).Should().BeOfType<NotFoundObjectResult>();

        // Its own company works, and company B's credential is untouched.
        (await controller.Put(GovernmentBodies.Gosi, new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, w.CompanyA, null), default))
            .Should().BeOfType<OkObjectResult>();
        await using var verify = w.Kit.Db(interceptors: false);
        var store = new GovernmentCredentialStore(verify, w.Kit.Keys, w.Kit.Kek, w.Kit.Options, new AuditService(verify));
        (await store.OpenAsync(w.TenantId, w.CompanyB, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretB);
        (await store.OpenAsync(w.TenantId, w.CompanyA, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA);
    }

    [Fact]
    public async Task AnotherTenantsUser_SeesNothing_AndCannotOpenTheCredential()
    {
        var w = await SeedAsync();
        await SeedCredentialAsync(w, w.TenantId, null, SecretA);
        var (db, http) = ContextFor(w, User(w.OtherTenantId, null, GovernmentCredentialsController.Permission));
        await using var _ = db;

        (await Controller(w, db, http).Get(GovernmentBodies.Gosi, "sandbox", null, default)).Should().BeOfType<NotFoundObjectResult>();
        (await Controller(w, db, http).Delete(GovernmentBodies.Gosi, "sandbox", null, default)).Should().BeOfType<NotFoundObjectResult>();
        var store = new GovernmentCredentialStore(db, w.Kit.Keys, w.Kit.Kek, w.Kit.Options, new AuditService(db));
        (await store.OpenAsync(w.OtherTenantId, null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeNull();
        // Even asking for the first tenant's id from the second tenant's context finds nothing: the filter pins the tenant.
        (await store.OpenAsync(w.TenantId, null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeNull();
        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ACompanyScopedUser_CannotOverwriteTheTenantWideCredential()
    {
        var w = await SeedAsync();
        await SeedCredentialAsync(w, w.TenantId, null, SecretA);
        var (db, http) = ContextFor(w, User(w.TenantId, w.CompanyA, GovernmentCredentialsController.Permission));
        await using var _ = db;

        var put = await Controller(w, db, http).Put(GovernmentBodies.Gosi,
            new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, SecretB, null, null), default);

        ((ObjectResult)put).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        await using var verify = w.Kit.Db(interceptors: false);
        (await new GovernmentCredentialStore(verify, w.Kit.Keys, w.Kit.Kek, w.Kit.Options, new AuditService(verify))
            .OpenAsync(w.TenantId, null, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA);
    }

    // ── Exchange ledger ────────────────────────────────────────────────────────────────────────────────

    private static GovernmentExchangeAttempt Attempt(Guid tenant, Guid? company, string key) =>
        new(tenant, company, GovernmentBodies.BankAnb, "payroll.submit", GovernmentExchangeChannels.FileOut, key, GovernmentExchangeStates.Recorded,
            new RequestContext("ip", "ua", Guid.NewGuid(), tenant));

    [Fact]
    public async Task TheLedger_IsTenantPinned_AndCompanyScoped()
    {
        var w = await SeedAsync();
        Guid rowB;
        await using (var system = w.Kit.Db(interceptors: false))
            rowB = (await new GovernmentExchangeRecorder(system, new AuditService(system)).BeginAsync(Attempt(w.TenantId, w.CompanyB, "k-b"), default)).Exchange.Id;

        // A company-A user: company B's attempt is invisible, cannot be moved on, and A cannot write into B.
        var (dbA, _) = ContextFor(w, User(w.TenantId, w.CompanyA));
        await using (dbA)
        {
            var recorderA = new GovernmentExchangeRecorder(dbA, new AuditService(dbA));
            (await dbA.GovernmentExchanges.CountAsync()).Should().Be(0);
            var move = () => recorderA.UpdateAsync(w.TenantId, rowB, new GovernmentExchangeOutcome(GovernmentExchangeStates.Failed, new RequestContext("ip", "ua", null, w.TenantId)), default);
            await move.Should().ThrowAsync<KeyNotFoundException>();
            var intoB = () => recorderA.BeginAsync(Attempt(w.TenantId, w.CompanyB, "k-a-into-b"), default);
            await intoB.Should().ThrowAsync<UnauthorizedAccessException>();
            (await recorderA.BeginAsync(Attempt(w.TenantId, w.CompanyA, "k-a"), default)).Created.Should().BeTrue();
        }

        // Another tenant: nothing visible, and its own key "k-b" is its own attempt.
        var (dbOther, _) = ContextFor(w, User(w.OtherTenantId, null));
        await using (dbOther)
        {
            (await dbOther.GovernmentExchanges.CountAsync()).Should().Be(0);
            var recorder = new GovernmentExchangeRecorder(dbOther, new AuditService(dbOther));
            var other = () => recorder.UpdateAsync(w.TenantId, rowB, new GovernmentExchangeOutcome(GovernmentExchangeStates.Failed, new RequestContext("ip", "ua", null, w.OtherTenantId)), default);
            await other.Should().ThrowAsync<KeyNotFoundException>();
        }

        await using var all = w.Kit.Db(interceptors: false);
        (await all.GovernmentExchanges.SingleAsync(x => x.Id == rowB)).State.Should().Be(GovernmentExchangeStates.Recorded);
    }
}
