namespace Zayra.Api.Application.Common;

/// <summary>
/// Where a request's entity scope came from. Recorded so an authorization decision can be explained
/// after the fact — "denied" with no reason is not an auditable control.
/// </summary>
public enum ScopeResolutionSource
{
    /// <summary>No <c>HttpContext</c> at all — seeding, migrations, tests, hosted services at startup.</summary>
    NoHttpContext,
    /// <summary>An explicit <see cref="Zayra.Api.Infrastructure.Scope.SystemScopeContext"/> block.</summary>
    ExplicitSystemScope,
    /// <summary>Authenticated platform administrator holding the platform audience.</summary>
    PlatformAdministrator,
    /// <summary>The v2 <c>entity_scope</c> claim — the only format new tokens carry.</summary>
    ClaimV2,
    /// <summary>Pre-cutover token: <c>entity_access</c> rows and/or <c>is_group_scope</c>.</summary>
    LegacyClaimV1,
    /// <summary>No scope claim of any kind, resolved under non-strict backward compatibility.</summary>
    LegacyAbsentNonStrict,
    /// <summary>Request carries no authenticated identity.</summary>
    Unauthenticated,
    /// <summary>Resolution failed closed. <see cref="RequestEntityScope.DenialReason"/> says why.</summary>
    Denied,
}

/// <summary>
/// Why a scope resolved to "sees nothing". <see cref="None"/> means it did not.
/// </summary>
public enum ScopeDenialReason
{
    None,
    /// <summary>The v2 claim was present but not parseable.</summary>
    MalformedV2Claim,
    /// <summary>A v2 claim with a version this build does not understand.</summary>
    UnknownV2Version,
    /// <summary>A v2 claim with a mode this build does not understand.</summary>
    UnknownV2Mode,
    /// <summary>Explicit <c>m=none</c> — the issuer decided this token sees no company data.</summary>
    ExplicitNone,
    /// <summary>Strict mode, and the token carries no scope claim.</summary>
    StrictMissingClaim,
    /// <summary>The <c>X-Company-Id</c> header was not a GUID.</summary>
    MalformedCompanyHeader,
    /// <summary>The <c>X-Company-Id</c> header named a company this token may not access.</summary>
    UnauthorizedCompanyHeader,
    /// <summary>Authenticated, but no <c>tenant_id</c> claim and not a platform administrator.</summary>
    NoTenantOnPrincipal,
    /// <summary>Selected-company mode with an empty company list.</summary>
    EmptySelectedCompanySet,
}

/// <summary>
/// THE authoritative, immutable answer to "what may this request see?".
///
/// <para><b>Why this type exists.</b> Before Wave 1 there were three independent resolutions of the
/// same question and they disagreed on two axes:</para>
/// <list type="bullet">
///   <item><c>ControllerTenantExtensions.GetEntityScope()</c> — <c>strictMode: false</c>, header ignored.</item>
///   <item><c>DataScopeService</c> — <c>strictMode: false</c>, header ignored.</item>
///   <item><c>ZayraDbContext.ResolveRequestScope()</c> — strict from options, header applied.</item>
/// </list>
/// <para>So a controller could authorize a request that the database layer would then filter to
/// nothing — or, far worse, authorize against a WIDER scope than the caller actually selected, because
/// the company switcher narrowed the data but not the permission check. On the payment-batch,
/// GL-export and report paths the controller check is the ONLY company control, since those tables are
/// <c>ITenantOwned</c> with no ambient company filter — there is no database backstop to catch a
/// controller that decided too generously.</para>
///
/// <para>One resolution, computed once per request and cached, is the fix. Everything downstream reads
/// this record.</para>
/// </summary>
public sealed record RequestEntityScope(
    Guid? TenantId,
    bool IsGroupLevel,
    IReadOnlyList<Guid> AuthorizedCompanyIds,
    Guid? SelectedCompanyId,
    bool IsStrict,
    bool IsSystemScope,
    ScopeResolutionSource Source,
    ScopeDenialReason DenialReason,
    bool LegacyCompatibilityUsed)
{
    /// <summary>
    /// May this request touch <paramref name="companyId"/>?
    ///
    /// <para>A null company is NOT "everyone's". On operational tables a null <c>CompanyId</c> is either
    /// a pre-company-dimension legacy row or a backfill transient, and it is tenant-wide by definition —
    /// so it is visible to group scope only. This mirrors the <c>ICompanyScopedOperational</c> read
    /// filter exactly, which is the point: the controller and the database must agree.</para>
    /// </summary>
    public bool CanAccessCompany(Guid? companyId)
    {
        if (IsSystemScope) return true;
        if (IsGroupLevel) return true;
        return companyId.HasValue && AuthorizedCompanyIds.Contains(companyId.Value);
    }

    /// <summary>True when this scope can see no company-owned data at all.</summary>
    public bool SeesNothing => !IsSystemScope && !IsGroupLevel && AuthorizedCompanyIds.Count == 0;

    /// <summary>
    /// The legacy shape, for the many call sites that still take an <see cref="EntityScopeContext"/>.
    /// Keeping one conversion point means those sites inherit strict mode and header narrowing for
    /// free, instead of each re-deciding them.
    /// </summary>
    public EntityScopeContext ToEntityScopeContext() =>
        IsGroupLevel || IsSystemScope
            ? EntityScopeContext.GroupLevel
            : EntityScopeContext.ForCompanies(AuthorizedCompanyIds);

    /// <summary>A safe one-line summary for logs and traces. Contains NO claim values and NO token material.</summary>
    public string ToAuditString() =>
        $"tenant={(TenantId?.ToString() ?? "none")} group={IsGroupLevel} companies={AuthorizedCompanyIds.Count} "
        + $"selected={(SelectedCompanyId?.ToString() ?? "none")} strict={IsStrict} system={IsSystemScope} "
        + $"source={Source} denial={DenialReason} legacy={LegacyCompatibilityUsed}";

    /// <summary>Background/system work: sees everything, deliberately and explicitly.</summary>
    public static RequestEntityScope System(ScopeResolutionSource source) => new(
        TenantId: null, IsGroupLevel: true, AuthorizedCompanyIds: Array.Empty<Guid>(),
        SelectedCompanyId: null, IsStrict: false, IsSystemScope: true,
        Source: source, DenialReason: ScopeDenialReason.None, LegacyCompatibilityUsed: false);

    /// <summary>Default-deny. Every failure path in the resolver lands here.</summary>
    public static RequestEntityScope Denied(Guid? tenantId, bool strict, ScopeDenialReason reason) => new(
        TenantId: tenantId, IsGroupLevel: false, AuthorizedCompanyIds: Array.Empty<Guid>(),
        SelectedCompanyId: null, IsStrict: strict, IsSystemScope: false,
        Source: ScopeResolutionSource.Denied, DenialReason: reason, LegacyCompatibilityUsed: false);
}
