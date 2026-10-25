using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>Whether the installation is in maintenance (an upgrade or restore in progress), and what to tell people.</summary>
public sealed record MaintenanceStatus(bool On, string? Message);

/// <summary>
/// Maintenance mode (implementation prompt WP8). Stored in the installation configuration store
/// (<c>platform_config_entries</c> keys <c>maintenance_mode</c> / <c>maintenance_message</c>), read through a
/// 10-second cache so the gate costs one query per 10 seconds, not one per request. Switched by
/// <c>--maintenance on|off</c> (upgrade.sh) and by <c>PUT /api/organisation-admin/maintenance</c>.
/// </summary>
public interface IMaintenanceState
{
    Task<MaintenanceStatus> GetAsync(CancellationToken ct);
    Task SetAsync(ZayraDbContext db, bool on, string? message, Guid? actorUserId, CancellationToken ct);
}

public sealed class MaintenanceState : IMaintenanceState
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    public const string DefaultMessage = "KynexOne is being updated. Please try again in a few minutes.";

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<DateTime> _clock;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private (MaintenanceStatus Status, DateTime ReadAtUtc)? _cached;

    public MaintenanceState(IServiceScopeFactory scopes) : this(scopes, () => DateTime.UtcNow) { }

    internal MaintenanceState(IServiceScopeFactory scopes, Func<DateTime> clock)
    {
        _scopes = scopes;
        _clock = clock;
    }

    public async Task<MaintenanceStatus> GetAsync(CancellationToken ct)
    {
        var now = _clock();
        if (_cached is { } c && now - c.ReadAtUtc < CacheTtl) return c.Status;
        await _refresh.WaitAsync(ct);
        try
        {
            if (_cached is { } again && now - again.ReadAtUtc < CacheTtl) return again.Status;
            using var scope = _scopes.CreateScope();
            var status = await ReadAsync(scope.ServiceProvider.GetRequiredService<ZayraDbContext>(), ct);
            _cached = (status, now);
            return status;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async Task SetAsync(ZayraDbContext db, bool on, string? message, Guid? actorUserId, CancellationToken ct)
    {
        await WriteAsync(db, on, message, ct);
        // The process that switched it sees the change at once; other processes within the cache window.
        _cached = (new MaintenanceStatus(on, on ? (string.IsNullOrWhiteSpace(message) ? DefaultMessage : message.Trim()) : null), _clock());
    }

    public static async Task<MaintenanceStatus> ReadAsync(ZayraDbContext db, CancellationToken ct)
    {
        var entries = await db.PlatformConfigEntries.AsNoTracking()
            .Where(e => e.Key == PlatformConfigKeys.MaintenanceMode || e.Key == PlatformConfigKeys.MaintenanceMessage)
            .ToDictionaryAsync(e => e.Key, e => e.Value, ct);
        var on = entries.TryGetValue(PlatformConfigKeys.MaintenanceMode, out var mode)
                 && string.Equals(mode, "true", StringComparison.OrdinalIgnoreCase);
        var message = entries.TryGetValue(PlatformConfigKeys.MaintenanceMessage, out var m) && !string.IsNullOrWhiteSpace(m) ? m : DefaultMessage;
        return new MaintenanceStatus(on, on ? message : null);
    }

    public static async Task WriteAsync(ZayraDbContext db, bool on, string? message, CancellationToken ct)
    {
        await Upsert(db, PlatformConfigKeys.MaintenanceMode, on ? "true" : "false", ct);
        await Upsert(db, PlatformConfigKeys.MaintenanceMessage, on && !string.IsNullOrWhiteSpace(message) ? message.Trim() : string.Empty, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task Upsert(ZayraDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.PlatformConfigEntries.FirstOrDefaultAsync(e => e.Key == key, ct);
        if (row is null) db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = key, Value = value });
        else
        {
            row.Value = value;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
    }
}

/// <summary>
/// While maintenance is on, every <c>/api/*</c> request answers 503 <c>{ error: "maintenance", message }</c> with
/// <c>Retry-After: 300</c> — except the public installation endpoint, health, and the maintenance switch itself, so
/// an administrator can always turn it off. Registered after routing and before the controllers.
/// </summary>
public static class MaintenanceModeGate
{
    public const string ErrorCode = "maintenance";

    private static readonly string[] Exempt =
    {
        "/api/public/installation",
        "/api/organisation-admin/maintenance",
        "/api/auth",
        "/api/health",
        "/api/version",
    };

    public static bool IsExempt(PathString path) =>
        !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || Exempt.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsExempt(context.Request.Path))
        {
            var state = context.RequestServices.GetService<IMaintenanceState>();
            if (state is not null && await state.GetAsync(context.RequestAborted) is { On: true } maintenance)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "300";
                await context.Response.WriteAsJsonAsync(new { error = ErrorCode, message = maintenance.Message });
                return;
            }
        }
        await next(context);
    }
}

/// <summary>
/// <c>--maintenance on|off [--message "..."]</c>: switch maintenance mode from the command line (upgrade.sh). Handled at
/// the top of Program.cs before the web host is built, like <c>--migrate</c>: it needs a connection string and nothing
/// else, so it works from the old image before an upgrade and from the new one after.
/// </summary>
public static class MaintenanceCommand
{
    public const string Argument = "--maintenance";

    public static bool ShouldHandle(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        IConfiguration? configuration = null, CancellationToken ct = default)
    {
        var at = Array.IndexOf(args, Argument);
        var verb = at + 1 < args.Length ? args[at + 1].ToLowerInvariant() : null;
        if (verb is not ("on" or "off"))
        {
            await error.WriteLineAsync($"[{Argument}] usage: --maintenance on|off [--message \"...\"]");
            return 2;
        }
        var messageAt = Array.IndexOf(args, "--message");
        var message = messageAt >= 0 && messageAt + 1 < args.Length ? args[messageAt + 1] : null;

        configuration ??= MigrateOnlyEntryPoint.BuildConfiguration();
        var connectionString = MigrateOnlyEntryPoint.ResolveConnectionString(configuration);
        if (connectionString is null)
        {
            await error.WriteLineAsync($"[{Argument}] FAILED: ConnectionStrings__Default is not set.");
            return 1;
        }

        try
        {
            await using var db = new ZayraDbContext(MigrateOnlyEntryPoint.BuildOptions(connectionString));
            await MaintenanceState.WriteAsync(db, verb == "on", message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type and message only: driver text can carry host and user names.
            await error.WriteLineAsync($"[{Argument}] FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        output.WriteLine(verb == "on"
            ? $"[{Argument}] Maintenance mode is ON: {(string.IsNullOrWhiteSpace(message) ? MaintenanceState.DefaultMessage : message)}"
            : $"[{Argument}] Maintenance mode is OFF.");
        return 0;
    }
}
