using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>POD-C2 — why a Lock refused to post the end-of-service provision (it never silently skips it).</summary>
public sealed record EosbProvisionRefusal(string Code, string Message);

/// <summary>
/// POD-C2 — the MONTHLY END-OF-SERVICE PROVISION, measured at a payroll Lock.
///
/// <para><b>WHAT IS MEASURED.</b> For a company whose <c>payparameter.eosb_provisioning</c> resolves to
/// <c>StatutoryMonthly</c> at the pay period's last day, every employee paid by a Regular (or the
/// Replacement of a voided Regular) run is measured with the SAME engine a settlement uses —
/// <see cref="ComputeEndOfServiceAsync"/> over the same wage basis, carried service start and unpaid
/// leave — under the employer-termination / end-of-contract scenario (full Art. 84 award; see
/// <see cref="EosbProvisioningRateKeys.ObligationScenario"/>). The movement booked is that obligation
/// minus what <see cref="EosbProvisionLedger"/> already carries for the employee (accruals, carried
/// opening balance, less consumption), so the ledger — never the snapshot table — is the balance.</para>
///
/// <para><b>WHO IS SKIPPED.</b> A leaver whose settlement is approved (its accrual has already consumed
/// and released the provision — measuring them again would re-provide a liability that has been paid);
/// anyone already measured for this period (a retried Lock, a second run in the month); anyone measured
/// for a LATER period (an out-of-order Lock must not wind the balance back to an older figure).</para>
/// </summary>
public partial class PayrollController
{
    /// <summary>The expense driver the provision's 5110 side resolves through (the settlement's gratuity driver).</summary>
    private const string EosbProvisionExpenseDriver = "EARN:EOSB";

    private sealed record EosbProvisionBuild(
        List<FinanceGlEntry> Lines, List<EosbProvisionSnapshot> Snapshots, EosbProvisionRefusal? Refusal)
    {
        public static EosbProvisionBuild None() => new(new List<FinanceGlEntry>(), new List<EosbProvisionSnapshot>(), null);
        public static EosbProvisionBuild Refused(string code, string message) =>
            new(new List<FinanceGlEntry>(), new List<EosbProvisionSnapshot>(), new EosbProvisionRefusal(code, message));
    }

    private static readonly string[] SettlementStatusesThatConsumedProvision =
    {
        FinalSettlementStatuses.Approved, FinalSettlementStatuses.Disbursing, FinalSettlementStatuses.Paid,
    };

    private async Task<EosbProvisionBuild> BuildEosbProvisionAsync(
        Guid tenantId, PayrollRun run, string period, string currency, GlResolutionContext glCtx,
        IReadOnlyList<PayrollEarning> earnings, Guid? postedBy, string postedByName, CancellationToken ct)
    {
        if (run.CompanyId is not Guid companyId) return EosbProvisionBuild.None();
        if (run.RunType != PayrollRunTypes.Regular && run.RunType != PayrollRunTypes.Replacement)
            return EosbProvisionBuild.None();

        var periodStart = new DateOnly(run.Year, run.Month, 1);
        var periodEnd = new DateOnly(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month));
        var mode = await new CompanyRatePolicyResolver(_db).ResolveAsync(
            tenantId, companyId, EosbProvisioningRateKeys.Mode, periodEnd, ct);
        if (mode is null || !EosbProvisioningRateKeys.IsOn(mode.RateValue)) return EosbProvisionBuild.None();

        var employeeIds = earnings.Select(e => e.EmployeeId).Where(id => id > 0).Distinct().ToList();
        if (employeeIds.Count == 0) return EosbProvisionBuild.None();

        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == companyId, ct);
        if (company is null)
            return EosbProvisionBuild.Refused("eosb_provision_not_measurable",
                "End-of-service provisioning is switched on, but this run's company could not be read.");
        var gcc = await _db.GCCComplianceSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CountryCode == company.CountryCode, ct);
        if (gcc is null || !gcc.EosbEnabled)
            return EosbProvisionBuild.Refused("eosb_provision_not_measurable",
                $"End-of-service provisioning is switched on for {company.LegalNameEn}, but end of service is not " +
                $"enabled for country '{company.CountryCode}' in GCC settings, so the obligation cannot be measured. " +
                "Enable end of service for that country, or switch provisioning off for this company, then lock again.");

        // A leaver whose settlement accrual has posted: the provision was consumed and released with them.
        var settled = (await ScopedBypass.TenantWide(_db.EmployeeFinalSettlements, tenantId,
                    "EOSB provision: a leaver settled under ANY legal entity has had their provision consumed; " +
                    "re-measuring them would re-provide a liability that has already been paid.")
                .AsNoTracking()
                .Where(s => employeeIds.Contains(s.EmployeeId) && SettlementStatusesThatConsumedProvision.Contains(s.Status))
                .Select(s => s.EmployeeId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var measured = await ScopedBypass.TenantWide(_db.EosbProvisionSnapshots, tenantId,
                "EOSB provision: whether an employee was already measured must be answered across every legal " +
                "entity, or a transferred employee's catch-up would be booked a second time.")
            .AsNoTracking()
            .Where(x => employeeIds.Contains(x.EmployeeId))
            .Select(x => new { x.EmployeeId, x.Period })
            .ToListAsync(ct);

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id))
            .ToListAsync(ct);
        var salaries = (await _db.EmployeeSalaryStructures.AsNoTracking()
                .Where(x => x.TenantId == tenantId && employeeIds.Contains(x.EmployeeId)
                         && x.IsActive && x.EffectiveDate <= periodEnd)
                .ToListAsync(ct))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveDate).First());
        // The catalog version in effect for the month being measured — the wage on the period-end date.
        var components = await LoadPayComponentsAsync(tenantId, companyId, periodStart, ct);
        var carriedStarts = (await _db.EmployeeEosbOpeningBalances.AsNoTracking()
                .Where(x => x.TenantId == tenantId && employeeIds.Contains(x.EmployeeId) && x.PriorServiceStartDate != null)
                .Select(x => new { x.EmployeeId, x.AsAtDate, x.PriorServiceStartDate })
                .ToListAsync(ct))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AsAtDate).ThenBy(x => x.PriorServiceStartDate).First().PriorServiceStartDate);
        var unpaidTypeIds = await _db.LeaveTypes.AsNoTracking()
            .Where(t => t.TenantId == tenantId && !t.IsPaid).Select(t => t.Id).ToListAsync(ct);
        var unpaidRequests = unpaidTypeIds.Count == 0
            ? new Dictionary<int, List<(DateOnly, DateOnly, decimal)>>()
            : (await _db.LeaveRequests.AsNoTracking()
                    .Where(r => r.TenantId == tenantId && employeeIds.Contains(r.EmployeeId) && r.Status == "Approved"
                             && unpaidTypeIds.Contains(r.LeaveTypeId) && r.StartDate <= periodEnd)
                    .Select(r => new { r.EmployeeId, r.StartDate, r.EndDate, r.TotalDays })
                    .ToListAsync(ct))
                .GroupBy(r => r.EmployeeId)
                .ToDictionary(g => g.Key, g => g.Select(r => (r.StartDate, r.EndDate, r.TotalDays)).ToList());
        var positions = await EosbProvisionLedger.LoadPositionsAsync(_db, tenantId, employeeIds, ct);

        var obligations = new List<EosbObligation>();
        foreach (var employee in employees.OrderBy(e => e.Id))
        {
            if (settled.Contains(employee.Id)) continue;
            var history = measured.Where(m => m.EmployeeId == employee.Id).ToList();
            if (history.Any(m => string.CompareOrdinal(m.Period, period) >= 0)) continue;

            var (serviceStart, _) = PickEosbServiceStart(employee, carriedStarts.GetValueOrDefault(employee.Id));
            if (serviceStart > periodEnd) continue;
            var wageBasis = EosbWageBasisFrom(components, salaries.GetValueOrDefault(employee.Id), employee);
            var unpaid = UnpaidLeaveDaysWithin(
                unpaidRequests.GetValueOrDefault(employee.Id) ?? new List<(DateOnly, DateOnly, decimal)>(),
                serviceStart, periodEnd);
            var (result, years) = await ComputeEndOfServiceAsync(
                company.CountryCode, company.Jurisdiction, companyId, wageBasis, serviceStart,
                periodEnd.ToDateTime(TimeOnly.MinValue), EosbProvisioningRateKeys.ObligationScenario,
                employee, unpaid, gcc, ct);
            var carried = positions
                .Where(p => p.EmployeeId == employee.Id && (p.CompanyId == companyId || p.CompanyId is null))
                .Sum(p => p.Remaining);
            obligations.Add(new EosbObligation(
                employee.Id, employee.EmployeeCode, result.TotalGratuity, carried,
                IsCatchUp: history.Count == 0,
                WageBase: result.AppliedWageBase > 0m ? result.AppliedWageBase : wageBasis.ConfiguredWage,
                ServiceYears: (decimal)years, Rule: result.ApplicableRule));
        }

        if (obligations.Count == 0) return EosbProvisionBuild.None();
        var (lines, snapshots) = EosbProvisionPlanner.Build(
            tenantId, companyId, run.Id, period, periodEnd, currency,
            GlAccountResolver.AccountLabel(EosbProvisionExpenseDriver, glCtx),
            GlAccountResolver.AccountLabel(EosbProvisionLedger.ProvisionDriverKey, glCtx),
            obligations, postedBy, postedByName, DateOnly.FromDateTime(DateTime.UtcNow));
        return new EosbProvisionBuild(lines, snapshots, null);
    }
}
