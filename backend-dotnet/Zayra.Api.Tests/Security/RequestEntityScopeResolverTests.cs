using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Scope;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// WAVE 1 B1 — the ONE authoritative entity-scope resolver.
///
/// <para>THE DEFECT. The same question — "what may this request see?" — was answered independently in
/// three places, and they disagreed on two axes:</para>
/// <list type="bullet">
///   <item><c>ControllerTenantExtensions.GetEntityScope()</c>: <c>strictMode: false</c>, header ignored.</item>
///   <item><c>DataScopeService</c>: <c>strictMode: false</c>, header ignored.</item>
///   <item><c>ZayraDbContext.ResolveRequestScope()</c>: strict from options, header applied.</item>
/// </list>
/// <para>So in production — where <c>EntityScopeOptions.ResolveStrictMode</c> forces strict — a token
/// with no scope claim was <b>default-deny at the database and group-level at the controller</b>. And a
/// user who had switched the company selector had their DATA narrowed while their AUTHORIZATION check
/// still ran against the full token scope. On the payment-batch, GL-export and report paths the
/// controller check is the only company control that exists, because those tables are
/// <c>ITenantOwned</c> with no ambient company filter — so there was no backstop to catch the
/// difference.</para>
///
/// <para>These tests pin each invariant against the resolver, and then pin that a controller and the
/// DbContext receive the SAME answer.</para>
/// </summary>
public class RequestEntityScopeResolverTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();
    private const string PlatformAudience = "kynexone-platform";

    private static RequestEntityScopeResolver Resolver(bool strictMode = false, IHttpContextAccessor? http = null) =>
        new(http,
            Options.Create(new EntityScopeOptions { StrictMode = strictMode }),
            Options.Create(new JwtOptions { PlatformAudience = PlatformAudience }));

    private static ClaimsPrincipal Principal(params Claim[] claims)
    {
        var baseClaims = new List<Claim> { new("tenant_id", Tenant.ToString()) };
        baseClaims.AddRange(claims);
        return new ClaimsPrincipal(new ClaimsIdentity(baseClaims, "test"));
    }

    private static Claim V2(string mode, params Guid[] companies) =>
        new(EntityScopeContext.V2ClaimType,
            JsonSerializer.Serialize(new { v = 2, m = mode, c = companies.Select(c => c.ToString()).ToArray() }));

    private static Claim RawV2(string json) => new(EntityScopeContext.V2ClaimType, json);

    // ── v2 claim: the happy paths ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidGroupV2_SeesTheWholeTenantGroup()
    {
        var scope = Resolver().ResolveFor(Principal(V2(EntityScopeModes.Group)), null);

        scope.IsGroupLevel.Should().BeTrue();
        scope.TenantId.Should().Be(Tenant);
        scope.Source.Should().Be(ScopeResolutionSource.ClaimV2);
        scope.DenialReason.Should().Be(ScopeDenialReason.None);
        scope.LegacyCompatibilityUsed.Should().BeFalse();
    }

    [Fact]
    public void ValidSelectedCompanyV2_SeesExactlyThoseCompanies()
    {
        var scope = Resolver().ResolveFor(Principal(V2(EntityScopeModes.Companies, CompanyA, CompanyB)), null);

        scope.IsGroupLevel.Should().BeFalse();
        scope.AuthorizedCompanyIds.Should().BeEquivalentTo(new[] { CompanyA, CompanyB });
        scope.CanAccessCompany(CompanyA).Should().BeTrue();
        scope.CanAccessCompany(Guid.NewGuid()).Should().BeFalse();
        // A null company is tenant-wide residue, and is group-only — matching ICompanyScopedOperational.
        scope.CanAccessCompany(null).Should().BeFalse();
    }

    // ── v2 claim: every failure path fails CLOSED, each with its own reason ───────────────────────

    [Fact]
    public void ExplicitNone_SeesNoCompanyData()
    {
        var scope = Resolver().ResolveFor(Principal(V2(EntityScopeModes.None)), null);

        scope.SeesNothing.Should().BeTrue();
        scope.DenialReason.Should().Be(ScopeDenialReason.ExplicitNone);
    }

    [Fact]
    public void EmptySelectedCompanySet_FailsClosed_WithItsOwnReason()
    {
        var scope = Resolver().ResolveFor(Principal(V2(EntityScopeModes.Companies)), null);

        scope.SeesNothing.Should().BeTrue();
        // Distinguishable from m=none: same effect, different cause, and an operator debugging a
        // "sees nothing" report needs to know which happened.
        scope.DenialReason.Should().Be(ScopeDenialReason.EmptySelectedCompanySet);
    }

    [Fact]
    public void MalformedV2Json_FailsClosed_AndNeverFallsBackToLegacy()
    {
        // The legacy fallback is the danger: a token with BOTH a broken v2 claim and a legacy
        // group grant must not be rescued into group access by the parse failure.
        var principal = Principal(
            RawV2("{ this is not json"),
            new Claim("is_group_scope", "true"));

        var scope = Resolver().ResolveFor(principal, null);

        scope.SeesNothing.Should().BeTrue("a claim we cannot read is not a claim granting access");
        scope.IsGroupLevel.Should().BeFalse();
        scope.DenialReason.Should().Be(ScopeDenialReason.MalformedV2Claim);
    }

    [Fact]
    public void UnknownV2Version_FailsClosed()
    {
        var scope = Resolver().ResolveFor(
            Principal(RawV2(JsonSerializer.Serialize(new { v = 99, m = "group" }))), null);

        scope.SeesNothing.Should().BeTrue();
        scope.DenialReason.Should().Be(ScopeDenialReason.UnknownV2Version);
    }

    [Fact]
    public void UnknownV2Mode_FailsClosed()
    {
        var scope = Resolver().ResolveFor(
            Principal(RawV2(JsonSerializer.Serialize(new { v = 2, m = "everything" }))), null);

        scope.SeesNothing.Should().BeTrue();
        scope.DenialReason.Should().Be(ScopeDenialReason.UnknownV2Mode);
    }

    // ── strict mode and the documented legacy cutover ────────────────────────────────────────────

    [Fact]
    public void StrictMode_WithNoScopeClaim_FailsClosed()
    {
        var scope = Resolver(strictMode: true).ResolveFor(Principal(), null);

        scope.SeesNothing.Should().BeTrue();
        scope.IsStrict.Should().BeTrue();
        scope.DenialReason.Should().Be(ScopeDenialReason.StrictMissingClaim);
    }

    [Fact]
    public void NonStrict_WithNoScopeClaim_KeepsDocumentedLegacyCompatibility_AndRecordsIt()
    {
        var scope = Resolver(strictMode: false).ResolveFor(Principal(), null);

        scope.IsGroupLevel.Should().BeTrue("the documented pre-cutover behaviour");
        scope.Source.Should().Be(ScopeResolutionSource.LegacyAbsentNonStrict);
        // Recorded rather than silent: this is the one remaining permissive path and evidence must show it.
        scope.LegacyCompatibilityUsed.Should().BeTrue();
    }

    [Fact]
    public void ImpersonationToken_IsAlwaysStrict_EvenWhenGlobalStrictModeIsOff()
    {
        var principal = Principal(new Claim(EntityScopeContext.StrictScopeClaim, "true"));

        var scope = Resolver(strictMode: false).ResolveFor(principal, null);

        scope.IsStrict.Should().BeTrue("the per-token marker overrides the global cutover flag");
        scope.SeesNothing.Should().BeTrue("an impersonated session never inherits access by claim omission");
        scope.DenialReason.Should().Be(ScopeDenialReason.StrictMissingClaim);
    }

    [Fact]
    public void BreakGlassToken_IsStrict_AndStillHonoursAnExplicitGrant()
    {
        // Strict does not mean "denied"; it means "absence is denial". An explicit grant still works.
        var principal = Principal(
            new Claim(EntityScopeContext.StrictScopeClaim, "true"),
            V2(EntityScopeModes.Companies, CompanyA));

        var scope = Resolver(strictMode: false).ResolveFor(principal, null);

        scope.IsStrict.Should().BeTrue();
        scope.AuthorizedCompanyIds.Should().ContainSingle().Which.Should().Be(CompanyA);
    }

    // ── the company switcher: may ONLY narrow ────────────────────────────────────────────────────

    [Fact]
    public void ValidHeader_NarrowsAGroupCallerToOneCompany()
    {
        var scope = Resolver().ResolveFor(Principal(V2(EntityScopeModes.Group)), CompanyA.ToString());

        scope.IsGroupLevel.Should().BeFalse("selecting a company is a narrowing, not a no-op");
        scope.AuthorizedCompanyIds.Should().ContainSingle().Which.Should().Be(CompanyA);
        scope.SelectedCompanyId.Should().Be(CompanyA);
        scope.CanAccessCompany(CompanyB).Should().BeFalse();
    }

    [Fact]
    public void MalformedHeader_FailsClosed()
    {
        var scope = Resolver().ResolveFor(Principal(V2(EntityScopeModes.Group)), "not-a-guid");

        scope.SeesNothing.Should().BeTrue();
        scope.DenialReason.Should().Be(ScopeDenialReason.MalformedCompanyHeader);
    }

    [Fact]
    public void UnauthorizedHeader_FailsClosed()
    {
        var scope = Resolver().ResolveFor(
            Principal(V2(EntityScopeModes.Companies, CompanyA)), CompanyB.ToString());

        scope.SeesNothing.Should().BeTrue();
        scope.DenialReason.Should().Be(ScopeDenialReason.UnauthorizedCompanyHeader);
    }

    [Fact]
    public void HeaderCannotWidenTokenScope()
    {
        // A company-A token asking for company B gets nothing — not B, and not A either.
        var scope = Resolver().ResolveFor(
            Principal(V2(EntityScopeModes.Companies, CompanyA)), CompanyB.ToString());

        scope.CanAccessCompany(CompanyB).Should().BeFalse();
        scope.CanAccessCompany(CompanyA).Should().BeFalse();
        scope.IsGroupLevel.Should().BeFalse();
    }

    // ── identity edge cases ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthenticatedTenantlessNonPlatformPrincipal_SeesNothing()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), V2(EntityScopeModes.Group) },
            "test"));

        var scope = Resolver().ResolveFor(principal, null);

        scope.SeesNothing.Should().BeTrue("no tenant means no tenant data, whatever the scope claim says");
        scope.TenantId.Should().BeNull();
        scope.DenialReason.Should().Be(ScopeDenialReason.NoTenantOnPrincipal);
    }

    [Fact]
    public void UnauthenticatedPrincipal_SeesNothing()
    {
        var scope = Resolver().ResolveFor(new ClaimsPrincipal(new ClaimsIdentity()), null);

        scope.SeesNothing.Should().BeTrue();
        scope.IsSystemScope.Should().BeFalse("an anonymous caller is not a system worker");
        scope.Source.Should().Be(ScopeResolutionSource.Unauthenticated);
    }

    [Fact]
    public void PlatformAdmin_RequiresBothTheClaimAndThePlatformAudience()
    {
        var claimOnly = Principal(new Claim("is_platform_admin", "true"));
        var withWrongAudience = Principal(
            new Claim("is_platform_admin", "true"), new Claim("aud", "kynexone-tenant"));
        var proper = Principal(
            new Claim("is_platform_admin", "true"), new Claim("aud", PlatformAudience));

        Resolver().ResolveFor(claimOnly, null).IsSystemScope
            .Should().BeFalse("the claim alone is not a platform session");
        Resolver().ResolveFor(withWrongAudience, null).IsSystemScope
            .Should().BeFalse("a tenant-audience token carrying the claim is not a platform session");

        var admin = Resolver().ResolveFor(proper, null);
        admin.IsSystemScope.Should().BeTrue();
        admin.Source.Should().Be(ScopeResolutionSource.PlatformAdministrator);
    }

    [Fact]
    public void ExplicitSystemWorker_GetsSystemScope_AndOnlyWhenAsked()
    {
        var resolver = Resolver();

        // A normal authenticated user is never a system worker.
        resolver.ResolveFor(Principal(V2(EntityScopeModes.Group)), null).IsSystemScope.Should().BeFalse();

        using (Zayra.Api.Infrastructure.Scope.SystemScopeContext.Begin())
        {
            var scope = resolver.ResolveFor(Principal(V2(EntityScopeModes.None)), null);
            scope.IsSystemScope.Should().BeTrue();
            scope.Source.Should().Be(ScopeResolutionSource.ExplicitSystemScope);
        }

        // ...and it does not leak past the block.
        resolver.ResolveFor(Principal(V2(EntityScopeModes.None)), null).IsSystemScope.Should().BeFalse();
    }

    [Fact]
    public void ScopeAuditString_CarriesNoClaimValuesOrTokenMaterial()
    {
        var principal = Principal(V2(EntityScopeModes.Companies, CompanyA));
        var audit = Resolver().ResolveFor(principal, null).ToAuditString();

        // Invariant 19: scope resolution must not log raw tokens or sensitive claim values.
        audit.Should().NotContain("entity_scope");
        audit.Should().NotContain("eyJ");             // no JWT segment
        audit.Should().NotContain("{\"v\":2");        // no raw claim payload
        audit.Should().Contain("source=ClaimV2");     // but it IS explainable
        audit.Should().Contain("companies=1");
    }

    // ── parity: the whole point of the exercise ──────────────────────────────────────────────────

    [Fact]
    public void ControllerAndDbContext_ResolveTheSameScope_UnderStrictModeAndASwitcherHeader()
    {
        // The exact combination that used to diverge: strict mode ON (as in production) and a company
        // selected. Controllers previously saw non-strict + no narrowing; the DbContext saw both.
        var principal = Principal(V2(EntityScopeModes.Group));
        var httpCtx = new DefaultHttpContext { User = principal };
        httpCtx.Request.Headers[ZayraDbContext.CompanySelectionHeader] = CompanyA.ToString();

        var accessor = new _ScopeHttpAccessor(httpCtx);
        var resolver = Resolver(strictMode: true, http: accessor);

        var fromRequest = resolver.Resolve();
        var fromExplicit = resolver.ResolveFor(principal, CompanyA.ToString());

        fromRequest.IsGroupLevel.Should().Be(fromExplicit.IsGroupLevel);
        fromRequest.AuthorizedCompanyIds.Should().BeEquivalentTo(fromExplicit.AuthorizedCompanyIds);
        fromRequest.SelectedCompanyId.Should().Be(CompanyA);
        fromRequest.IsGroupLevel.Should().BeFalse("the switcher narrows BOTH the data and the authorization check");
    }

    [Fact]
    public void ResolutionIsCachedPerRequest_SoRepeatedAsksCannotDisagree()
    {
        var httpCtx = new DefaultHttpContext { User = Principal(V2(EntityScopeModes.Group)) };
        var resolver = Resolver(http: new _ScopeHttpAccessor(httpCtx));

        var first = resolver.Resolve();

        // Mutating the header mid-request must not change the decision already handed out — otherwise
        // a controller and the DbContext could act on two different scopes within one request.
        httpCtx.Request.Headers[ZayraDbContext.CompanySelectionHeader] = CompanyA.ToString();
        var second = resolver.Resolve();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void APooledContextCannotServeAPreviousRequestsScope()
    {
        // The cache lives on HttpContext.Items, not on the resolver or the DbContext, so a second
        // request through the same pooled instance resolves afresh.
        var resolver1 = Resolver(http: new _ScopeHttpAccessor(
            new DefaultHttpContext { User = Principal(V2(EntityScopeModes.Companies, CompanyA)) }));
        var resolver2 = Resolver(http: new _ScopeHttpAccessor(
            new DefaultHttpContext { User = Principal(V2(EntityScopeModes.Companies, CompanyB)) }));

        resolver1.Resolve().AuthorizedCompanyIds.Should().ContainSingle().Which.Should().Be(CompanyA);
        resolver2.Resolve().AuthorizedCompanyIds.Should().ContainSingle().Which.Should().Be(CompanyB);
    }

    [Fact]
    public async Task CrossTenantDenial_AScopedCallerReadsNoneOfAnotherTenantsRows()
    {
        // End-to-end through the real query filters: the resolver's decision must actually bound data.
        var store = Guid.NewGuid().ToString();
        var otherTenant = Guid.NewGuid();

        await using (var seed = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options))
        {
            seed.Companies.Add(new Zayra.Api.Models.Company
            { Id = CompanyA, TenantId = Tenant, LegalNameEn = "Company A", TradeName = "A", DefaultCurrency = "SAR" });
            seed.Companies.Add(new Zayra.Api.Models.Company
            { Id = Guid.NewGuid(), TenantId = otherTenant, LegalNameEn = "Foreign Co", TradeName = "F", DefaultCurrency = "SAR" });
            await seed.SaveChangesAsync();
        }

        var httpCtx = new DefaultHttpContext { User = Principal(V2(EntityScopeModes.Group)) };
        await using var db = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options,
            new _ScopeHttpAccessor(httpCtx));

        var visible = await db.Companies.AsNoTracking().ToListAsync();

        visible.Should().ContainSingle("a tenant-scoped principal sees only its own tenant");
        visible[0].TenantId.Should().Be(Tenant);
    }

    /// <summary>
    /// A defect this work uncovered rather than introduced. The device-ingest webhook is
    /// [AllowAnonymous]; <c>Employee</c> is <c>ICompanyScopedOperational</c>; and the COMPANY clause of
    /// the read filter does not care about the system-scope bypass — it derives from the request
    /// principal. So under strict mode (which Production forces) an anonymous request resolved to an
    /// empty company scope and employee matching returned nothing for every punch.
    ///
    /// <para>This test pins the mechanism at the scope layer: an anonymous principal must resolve to a
    /// scope that can see no company-owned rows. The service-level fix is the explicit
    /// <c>IgnoreQueryFilters()</c> + <c>TenantId == device.TenantId</c> in <c>ResolveEmployee</c>.</para>
    /// </summary>
    [Fact]
    public void DeviceIngest_AnonymousPrincipalUnderStrictMode_ResolvesToNoCompanyAccess()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // exactly what an [AllowAnonymous] request carries

        var scope = Resolver(strictMode: true).ResolveFor(anonymous, null);

        scope.IsGroupLevel.Should().BeFalse();
        scope.AuthorizedCompanyIds.Should().BeEmpty();
        scope.CanAccessCompany(CompanyA).Should().BeFalse(
            "which is why any company-scoped read on an anonymous webhook path must re-apply its own "
            + "server-derived tenant predicate instead of relying on the ambient filter");
    }
}

file sealed class _ScopeHttpAccessor : IHttpContextAccessor
{
    public _ScopeHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}
