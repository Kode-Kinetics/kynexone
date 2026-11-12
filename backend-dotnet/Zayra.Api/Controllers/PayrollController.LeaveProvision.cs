using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// The MONTHLY ANNUAL-LEAVE PROVISION, measured at a payroll Lock — the leave counterpart of
/// <c>PayrollController.EosbProvision.cs</c>, and built the same way.
///
/// <para><b>WHAT IS MEASURED.</b> For a company whose <c>payparameter.leave_provisioning</c> resolves to
/// <c>BalanceMonthly</c> at the pay period's last day, every employee paid by a Regular (or the Replacement of a voided Regular) run
/// is valued at: <i>unused annual-leave days x day rate</i>. Both factors come from the code final settlement uses to encash leave, so
/// the provision and the payout on exit agree to the halala: the days and the per-row rounding from
/// <see cref="LeaveEncashmentCalculator"/>, the day rate from <see cref="LeaveCashOutRate"/> (the company's <c>leave_cashout_basis</c>
/// rule divided by 30 — the EXIT basis, which is what will actually be paid, not the in-service encashment rate). The movement booked
/// is that value minus what <see cref="LeaveProvisionLedger"/> already carries for the employee, so the ledger — never the snapshot
/// table — is the balance. Leave taken lowers the next month's value, so the liability falls without a separate entry.</para>
///
/// <para><b>THE BALANCE IS READ AT LOCK.</b> The leave engine keeps a live balance per employee, type and year, not a month-end history,
/// so the days are the balance on the day the run is locked. Lock a month promptly after it ends and the two are the same.</para>
///
/// <para><b>CARRIED-OVER DAYS COUNT ONCE.</b> The calculator is asked to net carried-forward days (see
/// <see cref="LeaveEncashmentCalculator.ComputeAsync"/>); a final settlement keeps its own behaviour.</para>
///
/// <para><b>WHO IS SKIPPED.</b> A leaver whose settlement is approved (its approval has already consumed and released the provision);
/// anyone already measured for this period or a later one; anyone with no salary record (no day rate can be derived — counted in the
/// Lock audit as unmeasured rather than provided at zero).</para>
/// </summary>
public partial class PayrollController
{
    private sealed record LeaveProvisionBuild(
        List<FinanceGlEntry> Lines, List<LeaveProvisionSnapshot> Snapshots, int CatchUpEmployees, int UnmeasuredEmployees, bool On = false)
    {
        public static LeaveProvisionBuild None() => new(new List<FinanceGlEntry>(), new List<LeaveProvisionSnapshot>(), 0, 0);
    }

    private async Task<LeaveProvisionBuild> BuildLeaveProvisionAsync(
        Guid tenantId, PayrollRun run, string period, string currency, GlResolutionContext glCtx,
        IReadOnlyList<PayrollEarning> earnings, Guid? postedBy, string postedByName, PayrollCostPlan costPlan, CancellationToken ct)
    {
        if (run.CompanyId is not Guid companyId) return LeaveProvisionBuild.None();
        if (run.RunType != PayrollRunTypes.Regular && run.RunType != PayrollRunTypes.Replacement)
            return LeaveProvisionBuild.None();

        var periodEnd = new DateOnly(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month));
        // The measurement is keyed on the PAY period; `period` is the GL posting period the lines book into (a Replacement may post later).
        var payPeriod = $"{run.Year}-{run.Month:D2}";
        var mode = await new CompanyRatePolicyResolver(_db).ResolveAsync(
            tenantId, companyId, LeaveProvisioningRateKeys.Mode, periodEnd, ct);
        if (mode is null || !LeaveProvisioningRateKeys.IsOn(mode.RateValue)) return LeaveProvisionBuild.None();

        var employeeIds = earnings.Select(e => e.EmployeeId).Where(id => id > 0).Distinct().ToList();
        if (employeeIds.Count == 0) return LeaveProvisionBuild.None();

        // A leaver whose settlement accrual has posted: the provision was consumed and released with them.
        var settled = (await ScopedBypass.TenantWide(_db.EmployeeFinalSettlements, tenantId,
                    "Leave provision: a leaver settled under ANY legal entity has had their provision consumed; " +
                    "re-measuring them would re-provide a liability that has already been paid.")
                .AsNoTracking()
                .Where(s => employeeIds.Contains(s.EmployeeId) && SettlementStatusesThatConsumedProvision.Contains(s.Status))
                .Select(s => s.EmployeeId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var measured = await ScopedBypass.TenantWide(_db.LeaveProvisionSnapshots, tenantId,
                "Leave provision: whether an employee was already measured must be answered across every legal " +
                "entity, or a transferred employee's catch-up would be booked a second time.")
            .AsNoTracking()
            .Where(x => employeeIds.Contains(x.EmployeeId))
            .Select(x => new { x.EmployeeId, x.Period })
            .ToListAsync(ct);

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id))
            .ToListAsync(ct);
        // The salary in force on the period's last day — the SAME pick final settlement makes for its last working day.
        var salaries = (await _db.EmployeeSalaryStructures.AsNoTracking()
                .Where(x => x.TenantId == tenantId && employeeIds.Contains(x.EmployeeId) && x.EffectiveDate <= periodEnd)
                .ToListAsync(ct))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveDate).First());
        var salaryIds = salaries.Values.Select(x => x.Id).ToList();
        var cashAllowanceTotals = salaryIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await _db.EmployeeSalaryAllowances.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && salaryIds.Contains(x.SalaryStructureId))
                    .Select(x => new { x.SalaryStructureId, x.Amount })
                    .ToListAsync(ct))
                .GroupBy(x => x.SalaryStructureId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var rules = await CompanyPayrollRules.ResolveAsync(_db, tenantId, companyId, ct, periodEnd);
        var positions = await LeaveProvisionLedger.LoadPositionsAsync(_db, tenantId, employeeIds, ct);

        var obligations = new List<LeaveObligation>();
        var unmeasured = 0;
        foreach (var employee in employees.OrderBy(e => e.Id))
        {
            if (settled.Contains(employee.Id)) continue;
            var history = measured.Where(m => m.EmployeeId == employee.Id).ToList();
            // A Replacement re-measures its OWN month even when a later month is already measured; every other run must not wind back.
            if (run.RunType == PayrollRunTypes.Replacement
                ? history.Any(m => m.Period == payPeriod)
                : history.Any(m => string.CompareOrdinal(m.Period, payPeriod) >= 0)) continue;

            salaries.TryGetValue(employee.Id, out var salary);
            if (salary is null && (employee.Salary ?? 0m) <= 0m) { unmeasured++; continue; }
            var basicWage = salary?.BasicSalary ?? employee.Salary ?? 0m;
            var monthlyGross = LeaveCashOutRate.MonthlyGross(
                salary, basicWage, salary is null ? 0m : cashAllowanceTotals.GetValueOrDefault(salary.Id));
            var dayRateBase = LeaveCashOutRate.BaseFor(rules.LeaveCashOutBasis, basicWage, salary?.HousingAllowance ?? 0m, monthlyGross);
            var encashment = await LeaveEncashmentCalculator.ComputeAsync(
                _db, tenantId, employee.Id, companyId, periodEnd, dayRateBase, ct,
                statutoryAnnualLeaveCashOut: rules.LeaveCashOutOnExit,
                basisLabel: LeaveCashOutRate.LabelFor(rules.LeaveCashOutBasis),
                netCarriedForward: true);
            var carried = LeaveProvisionLedger.CarriedFor(positions, employee.Id, companyId);
            obligations.Add(new LeaveObligation(
                employee.Id, employee.EmployeeCode, encashment.TotalDays,
                dayRateBase > 0m ? Math.Round(dayRateBase / 30m, 6) : 0m, rules.LeaveCashOutBasis,
                encashment.TotalAmount, carried, IsCatchUp: history.Count == 0,
                CostCentreId: costPlan.Enabled && costPlan.Employees.TryGetValue(employee.Id, out var stamp) ? stamp.CostCenterId : null));
        }

        if (obligations.Count == 0) return new LeaveProvisionBuild(new List<FinanceGlEntry>(), new List<LeaveProvisionSnapshot>(), 0, unmeasured, On: true);
        var (lines, snapshots) = LeaveProvisionPlanner.Build(
            tenantId, companyId, run.Id, period, periodEnd, currency,
            GlAccountResolver.AccountLabel(LeaveProvisioningRateKeys.ExpenseDriverKey, glCtx),
            GlAccountResolver.AccountLabel(LeaveProvisioningRateKeys.ProvisionDriverKey, glCtx),
            obligations, postedBy, postedByName, DateOnly.FromDateTime(DateTime.UtcNow),
            costPlan.Enabled ? new LeaveCostContext(glCtx) : null, payPeriod);
        return new LeaveProvisionBuild(lines, snapshots, snapshots.Count(s => s.IsCatchUp), unmeasured, On: true);
    }
}
