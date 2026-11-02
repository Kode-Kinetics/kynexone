namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Explicit eviction for SET-BASED writers (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL) on the tables the cached
/// session verdict reads. Those writes bypass the ChangeTracker, so <see cref="SessionVerdictInvalidationInterceptor"/>
/// never sees them. Call the matching method right after the write; <c>RawSqlExecutionRatchetTests</c> fails the build for a
/// set-based write on such a table that neither calls this helper nor is on its reviewed allow-list.
/// Safe to call with no cache registered (tests, tools): it does nothing.
/// </summary>
public static class SessionVerdictInvalidation
{
    public static void User(IServiceProvider? services, Guid userId) => Cache(services)?.InvalidateUser(userId);
    public static void Tenant(IServiceProvider? services, Guid tenantId) => Cache(services)?.InvalidateTenant(tenantId);
    public static void All(IServiceProvider? services) => Cache(services)?.InvalidateAll();

    private static ISessionVerdictCache? Cache(IServiceProvider? services) =>
        services?.GetService(typeof(ISessionVerdictCache)) as ISessionVerdictCache;
}
