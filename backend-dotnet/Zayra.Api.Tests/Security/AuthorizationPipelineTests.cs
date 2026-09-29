using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// End-to-end proof that the ASP.NET authentication/authorization pipeline composed by
/// <c>Program.cs</c> actually runs and actually denies.
///
/// Every test here was watched FAIL against the specific mutation it exists to catch. The audit
/// that produced this file found six controls that could each be deleted or inverted with all 1817
/// existing tests still green:
///
///   #1 Program.cs:693      app.UseAuthentication()                        → every 2xx and every 403 below (but see the note)
///   #2 Program.cs:275-278  Validate{Issuer,Audience,Lifetime}             → one test per flag, one flag changed per token
///                          ValidateIssuerSigningKey                       → see TokenSignedWithADifferentKey_Is401: that
///                                                                           flag turns out NOT to be the control it looks like
///   #3 Program.cs:316-318  default-deny FallbackPolicy                    → the attribute-less probe endpoint + the policy assertion
///   #4 PermissionAuthorizationHandler:20-23                               → the 403 (not 401, not 200) permission test
///   #5 Program.cs:307-309  PlatformAdmin .RequireClaim("aud", …)          → platform claims on a TENANT-audience token
///   #6 RequirePlatformRoleAttribute:35 403 branch                         → platform token whose role is not in the allow-list
///
/// NOTE ON #1 — measured, not assumed. Deleting <c>app.UseAuthentication()</c> from this app does
/// NOT disable authentication and CANNOT be caught by any test, because minimal hosting puts it
/// back: <c>WebApplicationBuilder.ConfigureApplication</c> re-adds UseAuthentication/UseAuthorization
/// whenever the app did not call them itself (it tracks that via the <c>__AuthenticationMiddlewareSet</c>
/// / <c>__AuthorizationMiddlewareSet</c> properties, both present as literals in
/// Microsoft.AspNetCore.dll). Suppressing ONLY that compensation — setting the marker without adding
/// the middleware, the sole difference from the audited mutation — turns nine of the tests below red,
/// every authenticated request collapsing to 401. So the harness does detect authentication actually
/// being absent; the audited edit simply is not that.
///
/// A test nobody has watched fail is not evidence. Each test's comment names the mutation it was
/// watched failing against.
/// </summary>
[Collection("AuthorizationPipeline")]
public sealed class AuthorizationPipelineTests
{
    private readonly AuthorizationPipelineFixture _fixture;

    public AuthorizationPipelineTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    // ── The four endpoints under test ────────────────────────────────────────────────────────

    /// <summary>NO authorization attribute of any kind. Guarded only by the FallbackPolicy.</summary>
    private const string AttributeLessProbe = "/api/test-harness/no-authorization-attribute";

    /// <summary>NotificationsController — class-level [Authorize], no permission gate.</summary>
    private const string AuthorizeOnly = "/api/notifications";

    /// <summary>NotificationsController.Deliveries — [HasPermission("notifications.manage")].</summary>
    private const string PermissionGated = "/api/notifications/deliveries";

    /// <summary>PlatformController.Health — [Authorize(Policy="PlatformAdmin")] on the controller
    /// plus [RequirePlatformRole(Owner, Admin, Support, Auditor)] on the action.</summary>
    private const string PlatformRoleGated = "/api/platform/health";

    private async Task<HttpResponseMessage> GetAsync(string path, string? bearerToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await _fixture.Client.SendAsync(request);
    }

    // ══ No credentials at all → 401 ══════════════════════════════════════════════════════════

    /// <summary>
    /// MUTATION #3 — deleting <c>options.FallbackPolicy = …RequireAuthenticatedUser().Build()</c>
    /// (Program.cs:316-318) turns this into 200 for an anonymous caller. Watched RED.
    ///
    /// The endpoint is pinned by name: <c>GET /api/test-harness/no-authorization-attribute</c>
    /// (<see cref="AuthorizationFallbackProbeController.NoAuthorizationAttribute"/>). It carries no
    /// [Authorize], no [AllowAnonymous], no [HasPermission] and no [RequirePlatformRole], so the
    /// default-deny fallback is the ONLY thing standing in front of it — which is the entire reason
    /// that fallback exists.
    /// </summary>
    [Fact]
    public async Task AnonymousRequest_ToEndpointWithNoAuthorizationAttribute_Is401()
    {
        var response = await GetAsync(AttributeLessProbe);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an endpoint that carries NO authorization attribute must still fail closed via the "
            + "default-deny FallbackPolicy — that is the forgotten-attribute case the policy exists for");
    }

    /// <summary>
    /// Proves the control above is real rather than accidentally passing: the probe endpoint must
    /// genuinely carry no authorization metadata. If somebody "fixes" the 401 by decorating the
    /// probe with [Authorize], this test fails and the FallbackPolicy loses its only witness.
    /// </summary>
    [Fact]
    public void FallbackProbeEndpoint_CarriesNoAuthorizationAttributeWhatsoever()
    {
        var controller = typeof(AuthorizationFallbackProbeController);
        var action = controller.GetMethod(nameof(AuthorizationFallbackProbeController.NoAuthorizationAttribute))!;

        var attributes = controller.GetCustomAttributes(inherit: true)
            .Concat(action.GetCustomAttributes(inherit: true))
            .ToList();

        attributes.Should().NotContain(a => a is IAuthorizeData,
            "the FallbackPolicy probe must carry no [Authorize]/[HasPermission]/[AllowAnonymous]");
        attributes.Should().NotContain(a => a is RequirePlatformRoleAttribute,
            "the FallbackPolicy probe must carry no [RequirePlatformRole] either");
    }

    /// <summary>
    /// MUTATION #3, asserted directly against the composed container rather than over HTTP.
    /// Deleting the FallbackPolicy makes <c>GetFallbackPolicyAsync()</c> return null. Watched RED.
    /// </summary>
    [Fact]
    public async Task Application_ComposesADefaultDenyFallbackPolicy()
    {
        using var scope = _fixture.Host.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await policies.GetFallbackPolicyAsync();

        fallback.Should().NotBeNull(
            "without a FallbackPolicy a forgotten [Authorize] silently publishes an endpoint");
        fallback!.Requirements.Should().Contain(r => r is DenyAnonymousAuthorizationRequirement,
            "the fallback must require an authenticated user");
    }

    [Fact]
    public async Task AnonymousRequest_ToAuthorizeOnlyEndpoint_Is401()
    {
        var response = await GetAsync(AuthorizeOnly);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousRequest_ToPermissionGatedEndpoint_Is401()
    {
        var response = await GetAsync(PermissionGated);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an unauthenticated caller is 401, never 403 — 403 would mean the pipeline had already "
            + "established an identity for them");
    }

    [Fact]
    public async Task AnonymousRequest_ToPlatformEndpoint_Is401()
    {
        var response = await GetAsync(PlatformRoleGated);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ══ Token validation — ONE flag changed per token ════════════════════════════════════════
    //
    // Each token below is the KNOWN-GOOD token's own claim set, re-signed with exactly one of
    // issuer / audience / signing key / expiry changed. Everything else is byte-identical to a
    // token that is proven to return 200 by ReissuedTokenWithNothingChanged_Is2xx, so a 401 here
    // can only be attributable to the single flag under test.

    private const string ForeignSigningKey =
        "a-completely-different-64-character-hmac-key-that-the-api-never-saw-0123456789";

    /// <summary>
    /// The control for the four tests below. If <see cref="AuthorizationPipelineFixture.ReissueWith"/>
    /// produced broken tokens, every one of those tests would pass for the wrong reason.
    /// </summary>
    [Fact]
    public async Task ReissuedTokenWithNothingChanged_Is2xx()
    {
        var token = AuthorizationPipelineFixture.ReissueWith(
            _fixture.TenantTokenWithPermission,
            _fixture.Jwt.Issuer,
            _fixture.Jwt.TenantAudience,
            _fixture.Jwt.SigningKey,
            DateTime.UtcNow.AddMinutes(30));

        var response = await GetAsync(PermissionGated, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "re-signing the same claims with the same issuer/audience/key must still be accepted — "
            + "otherwise the four negative tests below prove nothing about their one changed flag");
    }

    /// <summary>
    /// Guards SIGNATURE VERIFICATION — which, measured rather than assumed, is NOT the same control
    /// as <c>ValidateIssuerSigningKey</c>.
    ///
    /// Flipping <c>ValidateIssuerSigningKey = false</c> leaves this test GREEN, and that is correct:
    /// that flag validates the KEY's own metadata (size, certificate lifetime); the signature itself
    /// is always checked against <c>IssuerSigningKey</c>. The regression this test really catches is
    /// the API trusting a key it should not — a botched rotation, or a leaked legacy key left in the
    /// trust list. Watched RED (200 instead of 401) with the foreign key added to
    /// <c>IssuerSigningKeys</c>, with every other test in this file still green.
    /// </summary>
    [Fact]
    public async Task TokenSignedWithADifferentKey_Is401()
    {
        var token = AuthorizationPipelineFixture.ReissueWith(
            _fixture.TenantTokenWithPermission,
            _fixture.Jwt.Issuer,
            _fixture.Jwt.TenantAudience,
            ForeignSigningKey,                       // ← the only change
            DateTime.UtcNow.AddMinutes(30));

        var response = await GetAsync(PermissionGated, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token anyone can forge with their own key must never authenticate (ValidateIssuerSigningKey)");
    }

    /// <summary>MUTATION #2 — <c>ValidateLifetime = false</c>. Watched RED (200 instead of 401).</summary>
    [Fact]
    public async Task TokenThatExpiredInThePast_Is401()
    {
        var token = AuthorizationPipelineFixture.ReissueWith(
            _fixture.TenantTokenWithPermission,
            _fixture.Jwt.Issuer,
            _fixture.Jwt.TenantAudience,
            _fixture.Jwt.SigningKey,
            DateTime.UtcNow.AddMinutes(-30));       // ← the only change (ClockSkew is 1 minute)

        var response = await GetAsync(PermissionGated, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a revoked-by-expiry session must not survive; without ValidateLifetime a leaked token "
            + "is valid forever");
    }

    /// <summary>MUTATION #2 — <c>ValidateIssuer = false</c>. Watched RED (200 instead of 401).</summary>
    [Fact]
    public async Task TokenWithTheWrongIssuer_Is401()
    {
        var token = AuthorizationPipelineFixture.ReissueWith(
            _fixture.TenantTokenWithPermission,
            "https://issuer.that.is.not.this.api",   // ← the only change
            _fixture.Jwt.TenantAudience,
            _fixture.Jwt.SigningKey,
            DateTime.UtcNow.AddMinutes(30));

        var response = await GetAsync(PermissionGated, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "tokens minted by any other issuer must be rejected (ValidateIssuer)");
    }

    /// <summary>MUTATION #2 — <c>ValidateAudience = false</c>. Watched RED (200 instead of 401).</summary>
    [Fact]
    public async Task TokenWithTheWrongAudience_Is401()
    {
        var token = AuthorizationPipelineFixture.ReissueWith(
            _fixture.TenantTokenWithPermission,
            _fixture.Jwt.Issuer,
            "kynexone-some-other-service",           // ← the only change
            _fixture.Jwt.SigningKey,
            DateTime.UtcNow.AddMinutes(30));

        var response = await GetAsync(PermissionGated, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token minted for a different audience must not be accepted here (ValidateAudience)");
    }

    // ══ Authenticated but not authorized → 403 ═══════════════════════════════════════════════

    /// <summary>
    /// MUTATION #4 — replacing <c>PermissionAuthorizationHandler.HandleRequirementAsync</c>'s body
    /// with an unconditional <c>context.Succeed(requirement)</c>. Watched RED (200 instead of 403).
    ///
    /// The caller is a real, active, non-revoked tenant user whose token survives
    /// <c>OnTokenValidated</c> in full — the ONLY thing it lacks is the
    /// <c>notifications.manage</c> permission claim. The assertion is deliberately three-sided:
    ///   403 → the permission handler denied (correct);
    ///   401 → the pipeline failed to authenticate a valid token (this test would be measuring
    ///         nothing about permissions);
    ///   200 → the permission gate is inert.
    /// </summary>
    [Fact]
    public async Task AuthenticatedTokenWithoutTheRequiredPermission_Is403_NotUnauthorizedAndNotOk()
    {
        var response = await GetAsync(PermissionGated, _fixture.TenantTokenWithoutPermission);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "this identity authenticates correctly — a 401 here would mean the test is not "
            + "exercising the permission gate at all");
        response.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "[HasPermission(\"notifications.manage\")] must deny a caller that does not hold it");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// MUTATION #5 — dropping <c>.RequireClaim("aud", jwtOptions.PlatformAudience)</c> from the
    /// PlatformAdmin policy (Program.cs:307-309). Watched RED (200 instead of 403).
    ///
    /// The token carries a REAL platform Owner's claims — <c>is_platform_admin=true</c>, a live
    /// <c>platform_role=Owner</c>, and a session stamp that satisfies
    /// <c>PlatformSessionSecurity.IsCurrentAsync</c> — but on the TENANT audience. The claim check
    /// alone would wave it through; only the audience requirement stops it. Owner is deliberately
    /// inside the endpoint's allow-list so that <c>RequirePlatformRoleAttribute</c> cannot mask the
    /// result: if the audience requirement goes, this request succeeds.
    /// </summary>
    [Fact]
    public async Task PlatformAdminClaimsOnATenantAudienceToken_AreRejectedWith403()
    {
        var response = await GetAsync(PlatformRoleGated, _fixture.PlatformOwnerTokenOnTenantAudience);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the PlatformAdmin policy is dual-gated: is_platform_admin AND the platform audience. "
            + "A tenant-audience token must never reach a platform route even carrying the claim");
    }

    /// <summary>
    /// MUTATION #6 — turning <c>RequirePlatformRoleAttribute</c>'s 403 branch into <c>if (false)</c>
    /// (RequirePlatformRoleAttribute.cs:35). Watched RED (200 instead of 403).
    ///
    /// This token is fully valid for the PlatformAdmin policy (correct platform audience, live
    /// platform user, matching stamp) so the request reaches the action filter. Its role is
    /// Marketing, which is NOT in <c>[RequirePlatformRole(Owner, Admin, Support, Auditor)]</c>. The
    /// role branch is the only thing left that can deny it.
    /// </summary>
    [Fact]
    public async Task PlatformTokenWhoseRoleIsNotInTheAllowList_Is403()
    {
        var response = await GetAsync(PlatformRoleGated, _fixture.PlatformMarketingToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "[RequirePlatformRole] must deny a platform operator whose role is outside the allow-list");
    }

    /// <summary>
    /// A plain tenant token — no platform claims at all — must not reach a platform route either.
    /// </summary>
    [Fact]
    public async Task OrdinaryTenantToken_OnAPlatformRoute_Is403()
    {
        var response = await GetAsync(PlatformRoleGated, _fixture.TenantTokenWithPermission);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ══ Positive controls ════════════════════════════════════════════════════════════════════
    //
    // Without these the whole file could pass by rejecting absolutely everything — including with
    // app.UseAuthentication() deleted (MUTATION #1), which turns every one of these into a 401.

    /// <summary>
    /// MUTATION #1 — watched RED (401 instead of 200) when the authentication middleware is genuinely
    /// absent (see the NOTE ON #1 on this class). Also the positive half of MUTATION #4: same
    /// endpoint, same identity shape, one extra permission claim.
    /// </summary>
    [Fact]
    public async Task ValidTenantTokenCarryingTheRequiredPermission_Is2xx()
    {
        var response = await GetAsync(PermissionGated, _fixture.TenantTokenWithPermission);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a correctly-formed token holding {0} must be allowed through — otherwise this file "
            + "proves only that the API rejects everything", AuthorizationPipelineFixture.GatedPermission);
    }

    /// <summary>
    /// MUTATION #1 — watched RED (401 instead of 200) when the authentication middleware is genuinely
    /// absent. Also the positive half of MUTATIONS #5/#6: the same platform identity, on the right
    /// audience, with an allow-listed role, succeeds.
    /// </summary>
    [Fact]
    public async Task ValidPlatformOwnerToken_OnAPlatformRoleGatedEndpoint_Is2xx()
    {
        var response = await GetAsync(PlatformRoleGated, _fixture.PlatformOwnerToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a platform Owner on the platform audience is exactly who this endpoint is for");
    }

    /// <summary>
    /// The attribute-less probe is a live, routable endpoint — it answers 200 to an authenticated
    /// caller. Without this, <see cref="AnonymousRequest_ToEndpointWithNoAuthorizationAttribute_Is401"/>
    /// could be passing because the route does not exist at all.
    /// </summary>
    [Fact]
    public async Task AuthenticatedRequest_ToTheAttributeLessProbe_Is2xx()
    {
        var response = await GetAsync(AttributeLessProbe, _fixture.TenantTokenWithPermission);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the probe must be a real routed endpoint, otherwise its 401 would be a 404 in disguise");
    }

    /// <summary>An [Authorize]-only endpoint accepts any authenticated tenant identity.</summary>
    [Fact]
    public async Task ValidTenantToken_OnAnAuthorizeOnlyEndpoint_Is2xx()
    {
        var response = await GetAsync(AuthorizeOnly, _fixture.TenantTokenWithoutPermission);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// MUTATION #4 at the unit level. <c>PermissionAuthorizationHandler</c> is fail-closed by
/// construction: it never calls <c>Succeed</c> on the miss path. Replacing its body with an
/// unconditional <c>context.Succeed(requirement)</c> — which the audit did, with all 1817 tests
/// still green — inverts the deny case, and the first test here goes red.
/// </summary>
public sealed class PermissionAuthorizationHandlerTests
{
    private const string Required = "payroll.manage";

    private static AuthorizationHandlerContext ContextFor(params string[] permissionClaims)
    {
        var requirement = new PermissionRequirement(new[] { Required });
        var identity = new ClaimsIdentity(
            permissionClaims.Select(p => new Claim(ClaimsPrincipalPermissionExtensions.PermissionClaimType, p)),
            authenticationType: "TestBearer");
        return new AuthorizationHandlerContext(
            new[] { requirement }, new ClaimsPrincipal(identity), resource: null);
    }

    [Fact]
    public async Task Denies_WhenThePrincipalHoldsADifferentPermission()
    {
        var context = ContextFor("employees.read");

        await new PermissionAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse(
            "holding employees.read must not satisfy a {0} requirement — the handler never calls "
            + "Succeed on the miss path, which is what makes [HasPermission] fail closed", Required);
    }

    [Fact]
    public async Task Succeeds_WhenThePrincipalHoldsTheRequiredPermission()
    {
        var context = ContextFor("employees.read", Required);

        await new PermissionAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(
            "the ANY-of requirement is satisfied by holding {0}", Required);
    }
}
