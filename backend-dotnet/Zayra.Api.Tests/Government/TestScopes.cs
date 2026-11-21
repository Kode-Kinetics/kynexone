using Zayra.Api.Infrastructure.Government;

namespace Zayra.Api.Tests.Government;

/// <summary>The tenant, company and environment a test decision is made for (a decision is for exactly one scope).</summary>
internal static class TestScopes
{
    public static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Company = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The synthetic tenant, tenant-wide, in <paramref name="environment"/>.</summary>
    public static GovernmentDecisionScope For(string environment) => new(Tenant, null, environment);

    public static GovernmentDecisionScope For(Guid tenant, Guid? company, string environment) => new(tenant, company, environment);
}
