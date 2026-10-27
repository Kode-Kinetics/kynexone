using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// The ledger triggers (append-only, reversal mirror, run-lock witness) hold against every role except the table owner, its members and
/// superusers, which can switch them off. If the application's own database role is one of those, the ledger is append-only by convention
/// only. That is reported, never enforced by refusing to start: <c>/health/ready</c> carries <c>ledgerProtection</c> ("degraded" or "enforced")
/// next to an unchanged <c>status</c> (deploy scripts read status), and a CRITICAL message is logged once per process.
/// When the bypass function does not exist (an older schema) there is nothing to be degraded about and the answer is "enforced".
/// </summary>
public static class LedgerProtection
{
    public const string Enforced = "enforced";
    public const string Degraded = "degraded";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private static readonly object Gate = new();
    private static string _cached = Enforced;
    private static DateTime _cachedAtUtc = DateTime.MinValue;
    private static bool _loggedCritical;

    public static void ResetForTests()
    {
        lock (Gate) { _cached = Enforced; _cachedAtUtc = DateTime.MinValue; _loggedCritical = false; }
    }

    /// <summary>The uncached check, against the connection <paramref name="db"/> uses (so the answer is about the application's own role).</summary>
    public static async Task<string> ProbeAsync(ZayraDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return Enforced;
        var exists = await db.Database.SqlQueryRaw<bool>("SELECT to_regproc('finance_ledger_trigger_bypass_roles') IS NOT NULL AS \"Value\"").SingleAsync(ct);
        if (!exists) return Enforced;
        var bypass = await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM finance_ledger_trigger_bypass_roles() WHERE role_name::text = current_user) AS \"Value\"").SingleAsync(ct);
        return bypass ? Degraded : Enforced;
    }

    /// <summary>Runs <paramref name="probe"/> (at most once a minute unless forced), logs CRITICAL once when degraded, never throws.</summary>
    public static async Task<string> ReportAsync(Func<Task<string>> probe, ILogger log, bool force = false)
    {
        lock (Gate)
        {
            if (!force && DateTime.UtcNow - _cachedAtUtc < Ttl) return _cached;
        }
        string result;
        try { result = await probe(); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The ledger protection check could not run; reporting the last known answer.");
            lock (Gate) return _cached;
        }
        var announce = false;
        lock (Gate)
        {
            _cached = result; _cachedAtUtc = DateTime.UtcNow;
            if (result == Degraded && !_loggedCritical) { _loggedCritical = true; announce = true; }
        }
        if (announce)
            log.LogCritical("LEDGER PROTECTION DEGRADED: the application's database role can switch off the ledger triggers (it owns the ledger table, is a member of its owner, or is a superuser; see finance_ledger_trigger_bypass_roles()). The ledger is append-only by convention only. Connect the application as a role that does not own the tables and holds SELECT, INSERT, UPDATE.");
        return result;
    }

    public static Task<string> ReportAsync(ZayraDbContext db, ILogger log, CancellationToken ct) => ReportAsync(() => ProbeAsync(db, ct), log);
}
