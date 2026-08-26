using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Tests;

/// <summary>
/// Builds a real <see cref="NotificationService"/> over a single test-owned <see cref="ZayraDbContext"/>.
///
/// POD-D5 moved NotificationService off the caller's request-scoped context and onto its own child
/// scope, so it now takes an <see cref="IServiceScopeFactory"/> instead of a DbContext. Unit tests
/// that construct the service directly have no DI container, so this helper supplies a scope factory
/// whose every scope hands back the SAME context the test is asserting against — which is what those
/// tests were already relying on before the refactor.
///
/// Use this rather than hand-rolling a scope factory per test: it keeps all direct-construction sites
/// on one seam, so the next constructor change is a one-file fix instead of a broken test project.
/// </summary>
internal static class TestNotifications
{
    /// <summary>Real service, real recipient resolver, bound to <paramref name="db"/>.</summary>
    public static NotificationService For(ZayraDbContext db) =>
        new(new SingleContextScopeFactory(db),
            new NotificationRecipientResolver(),
            NullLogger<NotificationService>.Instance);

    /// <summary>
    /// An <see cref="IServiceScopeFactory"/> that resolves <see cref="ZayraDbContext"/> to one fixed
    /// instance. Disposal is a no-op: the test owns the context lifetime, and disposing it mid-test
    /// would break the assertions that run after the notification call.
    /// </summary>
    private sealed class SingleContextScopeFactory(ZayraDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ZayraDbContext) ? db : null;

        public void Dispose() { }
    }
}
