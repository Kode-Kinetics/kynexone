using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-B1 — the single choke point that answers "is GL posting into this scope+period blocked?".
/// Called at the top of every GL posting path (Lock accrual, net-pay settlement, statutory remittance,
/// void contra, and the Loans/Advances/Bonus disbursement posts) so a closed period rejects new
/// journals uniformly.
///
/// Semantics: a period is CLOSED for a (tenant, company) when EITHER a company-specific Closed row
/// (CompanyId == companyId) OR a group-wide Closed row (CompanyId == null) exists for that period.
/// The absence of any Closed row means OPEN — so with no period ever closed today, adding this guard
/// changes nothing for the ~55 live tenants; it only ever fires for an explicitly closed period.
///
/// IgnoreQueryFilters is intentional and mirrors LoadGlResolutionContextAsync: this is a SYSTEM read
/// that must see the run's company rows AND the tenant-wide (CompanyId == null) rows regardless of the
/// caller's own company claims. The explicit WHERE re-applies exact tenant + company/group scope and
/// never reads another tenant.
/// </summary>
public static class PeriodCloseGuard
{
    public static Task<bool> IsClosedAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, string period, CancellationToken ct = default)
        => db.GlPeriodCloses.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.TenantId == tenantId
                        && p.Period == period
                        && p.Status == GlPeriodStatuses.Closed
                        && (p.CompanyId == companyId || p.CompanyId == null), ct);

    /// <summary>
    /// Throwing variant for posting paths that funnel through a shared, non-IActionResult helper
    /// (Loans/Advances PostGlEntry, Bonus inline inserts). The thrown <see cref="PeriodClosedException"/>
    /// is mapped to a 422 gl_period_closed by the global exception handler, and — because it throws
    /// BEFORE SaveChanges — no partial write is committed. Passing companyId=null guards against a
    /// group-wide (tenant) close only; per-company attribution for these modules is POD-B1b.
    /// </summary>
    public static async Task ThrowIfClosedAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, string period, CancellationToken ct = default)
    {
        if (await IsClosedAsync(db, tenantId, companyId, period, ct))
            throw new PeriodClosedException(period);
    }
}

/// <summary>Raised when a GL post is attempted into a closed period through a non-IActionResult path.
/// Mapped to HTTP 422 { error = "gl_period_closed" } by the global exception handler in Program.cs.</summary>
public sealed class PeriodClosedException : Exception
{
    public string Period { get; }
    public PeriodClosedException(string period)
        : base($"GL period {period} is closed. Reopen it (Finance → GL periods) before posting into it.")
        => Period = period;
}
