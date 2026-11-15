using System.Linq.Expressions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Which salary advances a company's payroll may recover: the one rule shared by the final-settlement plan
/// (<c>PayrollController.FinalSettlement</c>), the run that collects (<c>PayrollRunProcessor</c>, collectableAdvance) and the
/// settlement payment that moves a leaver's leftover to 1420 (<c>PayrollController.FinalizePaidSettlementsAsync</c>). Kept in one
/// place so the plan never counts an advance the run will not take, nor the payment move one the run never saw.
///
/// <para>An advance is the 1410 receivable of the legal entity that paid it, so only that company's advances are recovered, as
/// for loans. A legacy advance with no company (written before company scope was required, not yet backfilled) is still
/// recovered: the ledger treats the unattributed 1410 pool as part of every company's view (GlControlAccounts), and dropping it
/// would silently stop collecting a debt.</para>
/// </summary>
public static class AdvanceRecoveryScope
{
    /// <summary>Open (Active, with a balance) advances of <paramref name="tenantId"/> that <paramref name="companyId"/>'s payroll
    /// recovers. Callers add their own employee and date conditions with a further <c>Where</c>.</summary>
    public static Expression<Func<SalaryAdvance, bool>> RecoverableBy(Guid tenantId, Guid? companyId) =>
        a => a.TenantId == tenantId && !a.IsDeleted && (a.CompanyId == companyId || a.CompanyId == null)
          && a.Status == "Active" && a.OutstandingBalance > 0;

    /// <summary>
    /// The recoverable advances, read the same way whoever asks. The set must not depend on the caller's company filter: a
    /// company-scoped caller's filter (or the background job's explicit company list) would drop the legacy advances with no
    /// company that a group-level caller collects, so the plan, the run and the payment would disagree by caller. The read is
    /// tenant-pinned and the company and legacy predicate is explicit; the run, settlement or payment it serves has already been
    /// authorised for its company.
    /// </summary>
    public static IQueryable<SalaryAdvance> Query(ZayraDbContext db, Guid tenantId, Guid? companyId) =>
        ScopedBypass.TenantWide(db.SalaryAdvances, tenantId,
                "Advance recovery: the advances a company's payroll recovers (its own and legacy unattributed ones), the same set for every caller; the run, settlement or payment was authorised for that company.")
            .Where(RecoverableBy(tenantId, companyId));
}
