using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Which loans a leaver's exit recovers, and how every open debt is listed at exit: the one rule shared by the final settlement
/// plan (<c>PayrollController.BuildFinalSettlementPlanAsync</c>), the settlement run (<c>PayrollRunProcessor</c>, collectableLoan),
/// the settlement payment that moves what is left to 1420 (<c>PayrollController.FinalizePaidSettlementsAsync</c>) and the
/// offboarding card (<c>OffboardingController.OpenDebts</c>).
///
/// <para>A month's run collects only payroll-deduction loans. At EXIT every open loan of the paying company that is not on hold
/// is recovered from the final pay, whatever its repayment method: a loan repaid by bank transfer (the request form's default)
/// used to be left out of the plan, the run and the payment, so the leaver was paid in full and the loan stayed open on an
/// ex-employee with nothing reporting it. Recovery follows the cap advances already use there (the settlement's net before
/// debt, and for a Saudi entity the half-wage-due headroom, PayrollRunProcessor), and what is left moves to the 1420 Employee
/// Receivable when the settlement is paid. A loan ON HOLD (an estate or review case, LoanLifecycleService) is listed and warned
/// about by name and amount, never recovered: releasing it is an HR decision.</para>
/// </summary>
public static class LoanRecoveryScope
{
    public const string OnHold = "OnHold";
    public const string PayrollDeduction = "PayrollDeduction";

    /// <summary>Open loans (Active or Overdue, with a balance) of <paramref name="companyId"/>, any repayment method, held or not.</summary>
    public static IQueryable<EmployeeLoan> OpenLoans(IQueryable<EmployeeLoan> loans, Guid tenantId, Guid? companyId) =>
        loans.Where(l => l.TenantId == tenantId && l.CompanyId == companyId && !l.IsDeleted
                      && (l.Status == "Active" || l.Status == "Overdue") && l.EmployeeIntId != null && l.OutstandingBalance > 0);

    public static bool IsHeld(EmployeeLoan loan) => loan.CollectionStatus == OnHold;

    /// <summary>
    /// One line per open loan and salary advance of the employee: what is owed, in which currency, and whether it is held.
    /// <paramref name="companyId"/> null lists the employee's debts in every company the caller's query filters admit (the
    /// offboarding card); a company lists only that legal entity's (the settlement, which recovers only its own receivables).
    /// </summary>
    public static async Task<List<ExitDebtLine>> ListAsync(ZayraDbContext db, Guid tenantId, int employeeId, Guid? companyId, bool allCompanies, CancellationToken ct)
    {
        var loanQuery = db.EmployeeLoans.AsNoTracking().Where(l => l.TenantId == tenantId && !l.IsDeleted && l.EmployeeIntId == employeeId
            && (l.Status == "Active" || l.Status == "Overdue") && l.OutstandingBalance > 0);
        if (!allCompanies) loanQuery = loanQuery.Where(l => l.CompanyId == companyId);
        var loans = await loanQuery.OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).ToListAsync(ct);
        var advanceQuery = allCompanies
            ? db.SalaryAdvances.AsNoTracking().Where(a => a.TenantId == tenantId && !a.IsDeleted && a.Status == "Active" && a.OutstandingBalance > 0)
            : AdvanceRecoveryScope.Query(db, tenantId, companyId).AsNoTracking();
        var advances = await advanceQuery.Where(a => a.EmployeeIntId == employeeId).OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToListAsync(ct);
        var currencies = await CompanyCurrenciesAsync(db, tenantId, loans.Select(l => l.CompanyId).Concat(advances.Select(a => a.CompanyId)), ct);
        return loans.Select(l => new ExitDebtLine("Loan", l.Id, l.LoanNumber, l.RepaymentMethod, IsHeld(l), Math.Round(l.OutstandingBalance, 2),
                    string.IsNullOrWhiteSpace(l.Currency) ? currencies.GetValueOrDefault(l.CompanyId ?? Guid.Empty) : l.Currency, 0m))
            .Concat(advances.Select(a => new ExitDebtLine("Advance", a.Id, a.AdvanceNumber, null, false, Math.Round(a.OutstandingBalance, 2),
                    currencies.GetValueOrDefault(a.CompanyId ?? Guid.Empty), 0m)))
            .ToList();
    }

    private static async Task<Dictionary<Guid, string?>> CompanyCurrenciesAsync(ZayraDbContext db, Guid tenantId, IEnumerable<Guid?> companyIds, CancellationToken ct)
    {
        var ids = companyIds.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var map = ids.Count == 0 ? new Dictionary<Guid, string?>() : await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => (string?)c.DefaultCurrency, ct);
        // A legacy debt with no company is shown in the tenant's currency.
        map[Guid.Empty] = await db.ResolveTenantCurrencyAsync(tenantId, ct);
        return map;
    }
}

/// <summary>One open loan or advance at exit. <c>PlannedRecovery</c> is what the final settlement plans to take from it.</summary>
public sealed record ExitDebtLine(
    string Kind, Guid Id, string Reference, string? RepaymentMethod, bool Held, decimal OutstandingBalance, string? Currency, decimal PlannedRecovery);
