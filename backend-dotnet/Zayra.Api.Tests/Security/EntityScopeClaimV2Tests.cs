using System.Security.Claims;
using FluentAssertions;
using Zayra.Api.Application.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 2: claim format v2 — grant-mode resolution at issuance, emission, parsing, and
/// the fail-closed rules for malformed/missing claims.
/// </summary>
public class EntityScopeClaimV2Tests
{
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();
    private static readonly Guid CompanyC = Guid.NewGuid();

    // ── Grant-mode resolution (spec 1) ──────────────────────────────────────────

    [Fact]
    public void Resolve_SelectedCompanies_YieldsExactlyTheListedCompanies()
    {
        var scope = EntityScopeClaims.Resolve(false, new[]
        {
            new EntityAccessGrant(CompanyA, "Admin", EntityGrantModes.SelectedCompanies),
            new EntityAccessGrant(CompanyB, "Viewer", EntityGrantModes.SelectedCompanies),
        }, activeCompanyIdsAtIssuance: new[] { CompanyA, CompanyB, CompanyC });

        scope.Mode.Should().Be(EntityScopeModes.Companies);
        scope.CompanyIds.Should().BeEquivalentTo(new[] { CompanyA, CompanyB },
            "SelectedCompanies grants access ONLY to explicitly listed companies — never company C");
    }

    [Fact]
    public void Resolve_AllCurrentCompanies_FreezesTheIssuanceSnapshot()
    {
        var scope = EntityScopeClaims.Resolve(false,
            new[] { new EntityAccessGrant(null, "Admin", EntityGrantModes.AllCurrentCompanies) },
            activeCompanyIdsAtIssuance: new[] { CompanyA, CompanyB });

        scope.Mode.Should().Be(EntityScopeModes.Companies,
            "AllCurrent is a SNAPSHOT — companies created after issuance are not included");
        scope.CompanyIds.Should().BeEquivalentTo(new[] { CompanyA, CompanyB });
    }

    [Fact]
    public void Resolve_AllCurrentAndFuture_IsDynamicGroupAccess()
    {
        var scope = EntityScopeClaims.Resolve(false,
            new[] { new EntityAccessGrant(null, "Admin", EntityGrantModes.AllCurrentAndFutureCompanies) },
            Array.Empty<Guid>());

        scope.Mode.Should().Be(EntityScopeModes.Group);
    }

    [Fact]
    public void Resolve_MisconfiguredGrants_ResolveToNone_NeverWiden()
    {
        // An AllCurrent grant in a tenant with zero active companies must deny, not widen.
        var scope = EntityScopeClaims.Resolve(false,
            new[] { new EntityAccessGrant(null, "Admin", EntityGrantModes.AllCurrentCompanies) },
            Array.Empty<Guid>());

        scope.Mode.Should().Be(EntityScopeModes.None);
    }

    [Fact]
    public void Resolve_LegacyNullCompanySelectedRow_PreservesGroupBehavior()
    {
        var scope = EntityScopeClaims.Resolve(false,
            new[] { new EntityAccessGrant(null, "Admin", EntityGrantModes.SelectedCompanies) },
            Array.Empty<Guid>());

        scope.Mode.Should().Be(EntityScopeModes.Group,
            "pre-migration null-company rows always meant dynamic group access");
    }

    [Fact]
    public void Resolve_NoGrants_DefaultsToNone()
    {
        EntityScopeClaims.Resolve(false, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>())
            .Mode.Should().Be(EntityScopeModes.None,
                "enterprise HR access must be explicit: group scope or selected company grants");
    }

    // ── Emission + round-trip parsing (spec 2) ──────────────────────────────────

    [Fact]
    public void BuildAndParse_CompaniesMode_RoundTrips()
    {
        var claims = EntityScopeClaims.Build(
            EntityScopeDescriptor.Companies(new[] { CompanyA, CompanyB }),
            new[] { new EntityAccessGrant(CompanyA, "Admin"), new EntityAccessGrant(CompanyB, "Viewer") });

        claims.Should().Contain(c => c.Type == EntityScopeContext.V2ClaimType);
        claims.Should().Contain(c => c.Type == "entity_access", "legacy v1 claims co-emitted during rollout");
        claims.Should().NotContain(c => c.Type == "is_group_scope");

        var scope = EntityScopeContext.FromClaims(Principal(claims));
        scope.IsGroupLevel.Should().BeFalse();
        scope.AccessibleCompanyIds.Should().BeEquivalentTo(new[] { CompanyA, CompanyB });
        scope.CanAccessCompany(CompanyC).Should().BeFalse();
    }

    [Fact]
    public void BuildAndParse_GroupMode_RoundTrips_WithExplicitLegacyFlag()
    {
        var claims = EntityScopeClaims.Build(EntityScopeDescriptor.Group, Array.Empty<EntityAccessGrant>());

        claims.Should().Contain(c => c.Type == "is_group_scope" && c.Value == "true");
        EntityScopeContext.FromClaims(Principal(claims)).IsGroupLevel.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoneMode_DeniesEverything()
    {
        var claims = EntityScopeClaims.Build(EntityScopeDescriptor.None, Array.Empty<EntityAccessGrant>());
        var scope = EntityScopeContext.FromClaims(Principal(claims));

        scope.IsGroupLevel.Should().BeFalse();
        scope.CanAccessCompany(CompanyA).Should().BeFalse();
    }

    // ── Fail-closed rules (spec 2) ──────────────────────────────────────────────

    [Theory]
    [InlineData("{broken json")]
    [InlineData("""{"v":1,"m":"group"}""")]        // unknown version
    [InlineData("""{"v":2,"m":"everything"}""")]   // unknown mode
    [InlineData("""{"v":2}""")]                    // missing mode
    public void MalformedOrUnknownV2Claim_AlwaysFailsClosed_EvenWithoutStrictMode(string payload)
    {
        // Even alongside a legacy group flag: a malformed v2 must never fall back to v1.
        var principal = Principal(new[]
        {
            new Claim(EntityScopeContext.V2ClaimType, payload),
            new Claim("is_group_scope", "true"),
        });

        var scope = EntityScopeContext.FromClaims(principal, strictMode: false);
        scope.IsGroupLevel.Should().BeFalse();
        scope.AccessibleCompanyIds.Should().BeEmpty();
    }

    [Fact]
    public void MissingAllClaims_FailsClosed_UnderGlobalStrictMode()
    {
        EntityScopeContext.FromClaims(Principal(Array.Empty<Claim>()), strictMode: true)
            .CanAccessCompany(CompanyA).Should().BeFalse();
    }

    [Fact]
    public void LegacyV1Tokens_StillParse_OnTheCompatPath()
    {
        // Pre-cutover token: no v2 claim, one legacy entity_access row.
        var principal = Principal(new[]
        {
            new Claim("entity_access", $$"""{"c":"{{CompanyA}}","r":"Admin"}"""),
        });

        var scope = EntityScopeContext.FromClaims(principal, strictMode: true);
        scope.CanAccessCompany(CompanyA).Should().BeTrue("documented legacy compat path");
        scope.CanAccessCompany(CompanyB).Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(IEnumerable<Claim> claims) =>
        new(new ClaimsIdentity(claims, "Test"));
}
