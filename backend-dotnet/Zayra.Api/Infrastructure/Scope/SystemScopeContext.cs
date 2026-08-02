namespace Zayra.Api.Infrastructure.Scope;

/// <summary>
/// Explicit, ambient "system scope" flag for work that runs UNDER an authenticated principal
/// but must legitimately bypass the tenant read filter and tenant write guard — for example a
/// background job or cross-tenant maintenance task that resolves its own tenant boundaries.
///
/// This is deliberately distinct from the IMPLICIT system context (no HttpContext at all —
/// seeders, boot, background workers, pre-auth login/refresh), which the DbContext already
/// treats as system scope. Use this flag only when a principal IS present on the ambient
/// HttpContext but the operation is genuinely system-level and cross-tenant by design.
///
/// Backed by <see cref="AsyncLocal{T}"/> so it flows across awaits within the logical call and
/// never leaks between concurrent requests. Always use it inside a <c>using</c> block:
/// <code>
/// using (SystemScopeContext.Begin())
/// {
///     // cross-tenant system work here — the tenant filter/guard is bypassed
/// }
/// </code>
/// </summary>
public static class SystemScopeContext
{
    private static readonly AsyncLocal<bool> _active = new();

    /// <summary>True while a <see cref="Begin"/> scope is open on the current async flow.</summary>
    public static bool IsActive => _active.Value;

    /// <summary>Opens a system scope; dispose the returned handle to restore the previous state.</summary>
    public static IDisposable Begin()
    {
        var previous = _active.Value;
        _active.Value = true;
        return new Handle(previous);
    }

    private sealed class Handle : IDisposable
    {
        private readonly bool _previous;
        private bool _disposed;

        public Handle(bool previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _active.Value = _previous;
        }
    }
}
