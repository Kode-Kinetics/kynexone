using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>One employee's employment dates, as the arrears engine needs them for a PAST period.</summary>
public sealed record ArrearsEmployee(
    int EmployeeId, string EmployeeCode, string EmployeeName,
    DateOnly? JoiningDate, DateOnly? LastWorkingDay);

/// <summary>What the engine produced for one run: the lines to persist, plus everything that has to be
/// SAID rather than silently absorbed.</summary>
public sealed record ArrearsComputation(
    IReadOnlyList<PayrollArrearsLine> Settled,
    IReadOnlyList<PayrollArrearsLine> PendingRecovery,
    IReadOnlyList<ArrearsWarning> Warnings)
{
    public static readonly ArrearsComputation Empty =
        new(Array.Empty<PayrollArrearsLine>(), Array.Empty<PayrollArrearsLine>(), Array.Empty<ArrearsWarning>());

    public decimal TotalSettled       => Math.Round(Settled.Sum(l => l.Amount), 2);
    public decimal TotalGosiBearing   => Math.Round(Settled.Where(l => l.IsGosiBearing).Sum(l => l.Amount), 2);
    public decimal TotalEarnedBasisGosiDelta => Math.Round(Settled.Sum(l => l.EarnedBasisGosiDelta), 2);
}

public sealed record ArrearsWarning(string Code, int? EmployeeId, string Message);

/// <summary>
/// POD-C3 — the RETRO / BACKDATED-INCREMENT ARREARS ENGINE.
///
/// <para><b>THE CONTRACT.</b> For each PAST period P inside the lookback window that had a finalised
/// (non-voided, Locked/Paid, period-owning) run:</para>
/// <code>
///   entitled(P)  = package now effective for P  ×  P's OWN proration factor (P's joiner/leaver window)
///   paid(P)      = Σ PayrollEarning.Amount over the NON-VOIDED runs of P, keyed on COMPONENT CODE
///   settled(P)   = Σ non-voided PayrollArrearsLine already settled for (employee, P, component)
///   arrears(P)   = entitled(P) − paid(P) − settled(P)
/// </code>
///
/// <para><b>WHY paid(P) IS READ FROM PayrollEarning AND KEYED ON COMPONENT CODE.</b> The slip header is
/// unusable — <c>PayrollSlip.OtherAllowances</c> carries <c>otherAllowances + overtimePay +
/// totalBonusGross + adjustmentEarnings</c>. And keying on <c>Source == "Salary"</c> is not safe either:
/// the data-driven component-engine path sets <c>Source</c> from the component DEFINITION, so a tenant
/// whose BASIC carries a different source would read paid(P) = 0 and the arrears would RE-PAY A WHOLE
/// MONTH — exactly what this pod must never do. When a period demonstrably HAS a slip for the employee
/// but resolves to zero matching earnings, the period is SKIPPED with
/// <c>WARN_ARREARS_PAID_BASIS_UNRESOLVED</c> naming it, never paid on a zero basis.</para>
///
/// <para><b>ANTI-DOUBLE-PAY.</b> Three independent layers: (1) the <c>settled(P)</c> term makes the
/// formula self-correcting — run the same run twice and the second result is 0; (2) a unique index on
/// (tenant, run, employee, covered period, component) makes a duplicate line impossible; (3) Process's
/// idempotent wipe removes THIS run's lines BEFORE entitlement is recomputed, so a re-Process rebuilds
/// instead of reading its own prior output as "already settled".</para>
///
/// <para><b>NEGATIVE ARREARS.</b> A retro DECREASE is computed, PERSISTED as
/// <c>PendingRecovery</c>, and the run is REFUSED. See <see cref="PayrollArrearsStatuses.PendingRecovery"/>.</para>
/// </summary>
public sealed class ArrearsEngine
{
    private static readonly string[] SalaryComponentCodes = { "BASIC", "HOUSING", "TRANSPORT", "OTHER_ALLOWANCES" };

    private readonly ZayraDbContext _db;
    public ArrearsEngine(ZayraDbContext db) => _db = db;

    /// <param name="ceiling">Statutory covered-wage ceiling used ONLY for the earned-basis delta
    /// (MF-4). Pass <c>decimal.MaxValue</c> for a jurisdiction with no ceiling.</param>
    public async Task<ArrearsComputation> ComputeAsync(
        Guid tenantId, Guid companyId, PayrollRun run,
        IReadOnlyList<ArrearsEmployee> employees,
        IReadOnlyList<EmployeeSalaryStructure> assignments,
        ProrationPolicy policy, WorkWeekConfig workWeek, decimal ceiling,
        CancellationToken ct)
    {
        if (employees.Count == 0 || policy.ArrearsMaxLookbackMonths <= 0) return ArrearsComputation.Empty;

        var runPeriodStart = new DateOnly(run.Year, run.Month, 1);
        var windowStart    = runPeriodStart.AddMonths(-policy.ArrearsMaxLookbackMonths);
        var windowEnd      = runPeriodStart.AddDays(-1);           // strictly BEFORE this run's period
        if (windowEnd < windowStart) return ArrearsComputation.Empty;

        var empIds = employees.Select(e => e.EmployeeId).ToList();

        // ── Every non-voided run in the window, for this legal entity ────────────────────────────────
        // Same scoping rule as LoadSiblingRunsAsync: a null-company run competes with every company
        // because Process collapses it onto one.
        var windowRuns = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Status != "Voided"
                     && (r.CompanyId == companyId || r.CompanyId == null)
                     && (r.Year > windowStart.Year || (r.Year == windowStart.Year && r.Month >= windowStart.Month))
                     && (r.Year < windowEnd.Year   || (r.Year == windowEnd.Year   && r.Month <= windowEnd.Month)))
            .Select(r => new { r.Id, r.Year, r.Month, r.RunType, r.Status })
            .ToListAsync(ct);
        if (windowRuns.Count == 0) return ArrearsComputation.Empty;

        // A period is SETTLEABLE only when it holds a finalised period-owning run. A month that was never
        // locked is not "underpaid" — it is unpaid, and paying it as arrears would bypass the whole run
        // lifecycle (validation, GOSI, WPS, approval).
        var settleablePeriods = windowRuns
            .Where(r => PayrollRunTypes.IsPeriodOwning(r.RunType) && r.Status is "Locked" or "Paid")
            .Select(r => (r.Year, r.Month))
            .Distinct()
            .OrderBy(p => p.Year).ThenBy(p => p.Month)
            .ToList();
        if (settleablePeriods.Count == 0) return ArrearsComputation.Empty;

        var windowRunIds = windowRuns.Select(r => r.Id).ToList();
        var periodOfRun  = windowRuns.ToDictionary(r => r.Id, r => (r.Year, r.Month));

        // ── paid(P): the CLEAN witness ───────────────────────────────────────────────────────────────
        var paidRows = await _db.PayrollEarnings.AsNoTracking()
            .Where(e => e.TenantId == tenantId && windowRunIds.Contains(e.PayrollRunId)
                     && empIds.Contains(e.EmployeeId) && SalaryComponentCodes.Contains(e.ComponentCode))
            .Select(e => new { e.PayrollRunId, e.EmployeeId, e.ComponentCode, e.Amount })
            .ToListAsync(ct);
        var paid = new Dictionary<(int Emp, int Y, int M, string Code), decimal>();
        foreach (var r in paidRows)
        {
            if (!periodOfRun.TryGetValue(r.PayrollRunId, out var p)) continue;
            var key = (r.EmployeeId, p.Year, p.Month, r.ComponentCode);
            paid[key] = paid.GetValueOrDefault(key) + r.Amount;
        }

        // Which (employee, period) pairs actually HAD a payslip: the guard that stops a component-code
        // mismatch being read as "nothing was paid".
        var slipRows = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && windowRunIds.Contains(s.RunId) && empIds.Contains(s.EmployeeId))
            .Select(s => new { s.RunId, s.EmployeeId })
            .ToListAsync(ct);
        var hadSlip = new HashSet<(int, int, int)>();
        foreach (var s in slipRows)
            if (periodOfRun.TryGetValue(s.RunId, out var p)) hadSlip.Add((s.EmployeeId, p.Year, p.Month));

        // ── settled(P): what earlier, still-live arrears runs already paid ───────────────────────────
        // Excludes THIS run explicitly: Process wipes its own lines before calling us, but a caller that
        // did not would otherwise read its own previous output and self-cancel to zero.
        var priorSettled = await _db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == PayrollArrearsStatuses.Settled
                     && empIds.Contains(a.EmployeeId) && a.PayrollRunId != run.Id
                     && (a.CoveredYear > windowStart.Year || (a.CoveredYear == windowStart.Year && a.CoveredMonth >= windowStart.Month))
                     && (a.CoveredYear < windowEnd.Year   || (a.CoveredYear == windowEnd.Year   && a.CoveredMonth <= windowEnd.Month)))
            .Select(a => new { a.EmployeeId, a.CoveredYear, a.CoveredMonth, a.ComponentCode, a.Amount })
            .ToListAsync(ct);
        var settled = new Dictionary<(int Emp, int Y, int M, string Code), decimal>();
        foreach (var a in priorSettled)
        {
            var key = (a.EmployeeId, a.CoveredYear, a.CoveredMonth, PayrollArrearsComponents.SourceComponent(a.ComponentCode));
            settled[key] = settled.GetValueOrDefault(key) + a.Amount;
        }

        var assignmentsByEmp = assignments.GroupBy(a => a.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.EffectiveDate).ThenByDescending(a => a.CreatedAtUtc).ToList());

        var settledLines = new List<PayrollArrearsLine>();
        var pendingLines = new List<PayrollArrearsLine>();
        var warnings     = new List<ArrearsWarning>();

        foreach (var emp in employees)
        {
            if (!assignmentsByEmp.TryGetValue(emp.EmployeeId, out var empAssignments)) continue;

            // ── Lookback truncation, named rather than silent ────────────────────────────────────────
            // A genuinely BACKDATED assignment is one created materially after the date it takes effect.
            // If the earliest such reach predates the window, the uncovered periods are NAMED.
            var backdatedReach = empAssignments
                .Where(a => a.EffectiveDate < runPeriodStart && DateOnly.FromDateTime(a.CreatedAtUtc) > a.EffectiveDate)
                .Select(a => a.EffectiveDate)
                .DefaultIfEmpty(default)
                .Min();
            if (backdatedReach != default && backdatedReach < windowStart)
            {
                var uncovered = PeriodsBetween(backdatedReach, windowStart);
                if (uncovered.Count > 0)
                    warnings.Add(new ArrearsWarning("WARN_ARREARS_LOOKBACK_TRUNCATED", emp.EmployeeId,
                        $"{emp.EmployeeName} ({emp.EmployeeCode}) has a salary change effective {backdatedReach:yyyy-MM-dd}, " +
                        $"which is older than the {policy.ArrearsMaxLookbackMonths}-month arrears lookback. " +
                        $"These finalised period(s) were NOT settled by this run: {string.Join(", ", uncovered)}. " +
                        "Raise a supplementary run, or widen payparameter.arrears_max_lookback_months."));
            }

            foreach (var (py, pm) in settleablePeriods)
            {
                var pStart = new DateOnly(py, pm, 1);
                var pEnd   = pStart.AddMonths(1).AddDays(-1);
                if (emp.JoiningDate is DateOnly jd && jd > pEnd) continue;   // not employed yet

                var assignment = empAssignments.FirstOrDefault(a => a.EffectiveDate <= pEnd);
                if (assignment is null) continue;                            // no entitlement basis for P

                var pr = ProrationCalculator.Compute(pStart, pEnd, emp.JoiningDate, emp.LastWorkingDay, policy.Basis, workWeek);
                if (pr.IsExcluded) continue;

                var pkg = ProrationCalculator.Apply(
                    assignment.BasicSalary, assignment.HousingAllowance, assignment.TransportAllowance,
                    assignment.FoodAllowance + assignment.MobileAllowance + assignment.OtherAllowance,
                    pr.Factor);
                // Honour the configurable prorated SET: a component outside it is entitled in FULL.
                var entitled = new Dictionary<string, decimal>(StringComparer.Ordinal)
                {
                    ["BASIC"]            = policy.Prorates(ProratedComponentCodes.Basic)           ? pkg.Basic           : assignment.BasicSalary,
                    ["HOUSING"]          = policy.Prorates(ProratedComponentCodes.Housing)         ? pkg.Housing         : assignment.HousingAllowance,
                    ["TRANSPORT"]        = policy.Prorates(ProratedComponentCodes.Transport)       ? pkg.Transport       : assignment.TransportAllowance,
                    ["OTHER_ALLOWANCES"] = policy.Prorates(ProratedComponentCodes.OtherAllowances) ? pkg.OtherAllowances : assignment.FoodAllowance + assignment.MobileAllowance + assignment.OtherAllowance,
                };

                // ── ARREARS TOP UP A PAID MONTH. THEY NEVER PAY AN UNPAID ONE. ───────────────────────
                // Requirement 4 is explicit: arrears "must never silently re-pay a whole month". If the
                // employee has NO salary earning rows for P, entitled − 0 is a FULL MONTH and settling it
                // would pay a whole month's wage as an "arrears" line — bypassing the entire run
                // lifecycle (population, validation, statutory, WPS, approval, GL). That happens for
                // perfectly ordinary reasons: the employee was a deliberate hold-out under B2's selector,
                // was excluded as a post-period joiner, or the period's run produced no lines for them at
                // all. A genuinely UNDER-paid month always has SOME salary paid; a month with nothing
                // paid is a MISSED month, and paying it is an OffCycle run's job (B2 supports exactly
                // that via IncludesRecurringPay), not the arrears engine's. Clean seam, enforced here.
                var paidTotal = SalaryComponentCodes.Sum(c => paid.GetValueOrDefault((emp.EmployeeId, py, pm, c)));
                if (paidTotal == 0m)
                {
                    // Only WARN when a payslip demonstrably exists — that is the component-code mismatch
                    // case (a tenant whose engine emits BASIC under a different code), which is a real
                    // configuration defect worth naming. A period the employee simply was not paid in is
                    // silent by design, or every hold-out would generate noise on every future run.
                    if (hadSlip.Contains((emp.EmployeeId, py, pm)))
                        warnings.Add(new ArrearsWarning("WARN_ARREARS_PAID_BASIS_UNRESOLVED", emp.EmployeeId,
                            $"{emp.EmployeeName} ({emp.EmployeeCode}) has a payslip for {py}-{pm:D2} but no BASIC/HOUSING/" +
                            "TRANSPORT/OTHER_ALLOWANCES earning rows, so what was actually paid cannot be established. " +
                            "The period was SKIPPED rather than settled as if nothing had been paid. Check the tenant's " +
                            "pay-component codes."));
                    continue;
                }

                // ── Per-component deltas, then ONE decision per (employee, period) ───────────────────
                var deltas = new Dictionary<string, (decimal Entitled, decimal Paid, decimal Settled, decimal Delta)>(StringComparer.Ordinal);
                decimal net = 0m;
                foreach (var code in SalaryComponentCodes)
                {
                    var e = Math.Round(entitled[code], 2);
                    var p = Math.Round(paid.GetValueOrDefault((emp.EmployeeId, py, pm, code)), 2);
                    var s = Math.Round(settled.GetValueOrDefault((emp.EmployeeId, py, pm, code)), 2);
                    var d = Math.Round(e - p - s, 2);
                    deltas[code] = (e, p, s, d);
                    net += d;
                }
                net = Math.Round(net, 2);
                if (net == 0m) continue;

                var basisFactor = pr.Factor;

                if (net < 0m)
                {
                    // Retro DECREASE. Persisted with its covered period, never netted into pay, and the
                    // run refuses. B2's ADDITIVE-ONLY posture, applied verbatim.
                    foreach (var code in SalaryComponentCodes)
                    {
                        var d = deltas[code];
                        if (d.Delta == 0m) continue;
                        pendingLines.Add(NewLine(tenantId, companyId, run, emp, py, pm,
                            PayrollArrearsComponents.ForSourceComponent(code), d.Entitled, d.Paid, d.Settled, d.Delta,
                            assignment, isGosiBearing: false, earnedDelta: 0m, policy.Basis, basisFactor,
                            PayrollArrearsStatuses.PendingRecovery));
                    }
                    continue;
                }

                // net > 0: distribute it over the POSITIVE components pro rata, so a component that went
                // DOWN (a package restructure moving money between basic and housing) offsets the increase
                // instead of being paid twice. Σ(settled lines) == net, exactly.
                var positives = SalaryComponentCodes.Where(c => deltas[c].Delta > 0m).ToList();
                var positiveTotal = positives.Sum(c => deltas[c].Delta);
                if (positives.Count == 0 || positiveTotal <= 0m) continue;

                decimal remaining = net;
                for (var i = 0; i < positives.Count; i++)
                {
                    var code = positives[i];
                    var d = deltas[code];
                    var share = i == positives.Count - 1
                        ? remaining
                        : Math.Min(remaining, Math.Round(net * (d.Delta / positiveTotal), 2, MidpointRounding.AwayFromZero));
                    remaining = Math.Round(remaining - share, 2);
                    if (share <= 0m) continue;
                    settledLines.Add(NewLine(tenantId, companyId, run, emp, py, pm,
                        PayrollArrearsComponents.ForSourceComponent(code), d.Entitled, d.Paid, d.Settled, share,
                        assignment,
                        isGosiBearing: policy.ArrearsAreGosiBearing
                                       && PayrollArrearsComponents.IsGosiCovered(PayrollArrearsComponents.ForSourceComponent(code)),
                        earnedDelta: 0m, policy.Basis, basisFactor, PayrollArrearsStatuses.Settled));
                }

                // ── [FLAG-COMPLIANCE-KSA] the earned-basis GOSI delta, as a NUMBER ───────────────────
                // Carried on the FIRST contributory line of this (employee, period) group so a sum over
                // lines is the period total and never double-counts.
                var entitledGosi = Math.Round(deltas["BASIC"].Entitled + deltas["HOUSING"].Entitled, 2);
                var paidGosi     = Math.Round(deltas["BASIC"].Paid + deltas["HOUSING"].Paid
                                            + deltas["BASIC"].Settled + deltas["HOUSING"].Settled, 2);
                var earnedDelta  = Math.Max(0m, Math.Round(Math.Min(entitledGosi, ceiling) - Math.Min(paidGosi, ceiling), 2));
                if (earnedDelta > 0m)
                {
                    var carrier = settledLines.LastOrDefault(l => l.CoveredYear == py && l.CoveredMonth == pm
                                                              && l.EmployeeId == emp.EmployeeId
                                                              && PayrollArrearsComponents.IsGosiCovered(l.ComponentCode))
                               ?? settledLines.LastOrDefault(l => l.CoveredYear == py && l.CoveredMonth == pm
                                                              && l.EmployeeId == emp.EmployeeId);
                    if (carrier is not null) carrier.EarnedBasisGosiDelta = earnedDelta;
                }
            }
        }

        return new ArrearsComputation(settledLines, pendingLines, warnings);
    }

    /// <summary>Period labels in [from, to) — the months a lookback cap left uncovered, named explicitly
    /// so truncation is never silent.</summary>
    private static List<string> PeriodsBetween(DateOnly from, DateOnly to) => Enumerable
        .Range(0, Math.Max(0, (to.Year - from.Year) * 12 + to.Month - from.Month))
        .Select(i => from.AddMonths(i))
        .Select(d => $"{d.Year}-{d.Month:D2}")
        .ToList();

    private static PayrollArrearsLine NewLine(
        Guid tenantId, Guid companyId, PayrollRun run, ArrearsEmployee emp, int year, int month,
        string componentCode, decimal entitled, decimal paid, decimal previouslySettled, decimal amount,
        EmployeeSalaryStructure assignment, bool isGosiBearing, decimal earnedDelta,
        string basis, decimal factor, string status) => new()
    {
        TenantId = tenantId,
        CompanyId = companyId,
        EmployeeId = emp.EmployeeId,
        EmployeeCode = emp.EmployeeCode,
        PayrollRunId = status == PayrollArrearsStatuses.Settled ? run.Id : null,
        CoveredYear = year,
        CoveredMonth = month,
        ComponentCode = componentCode,
        EntitledAmount = entitled,
        PaidAmount = paid,
        PreviouslySettledAmount = previouslySettled,
        Amount = amount,
        SourceAssignmentId = assignment.Id,
        SourceEffectiveDate = assignment.EffectiveDate,
        IsGosiBearing = isGosiBearing,
        EarnedBasisGosiDelta = earnedDelta,
        Basis = basis,
        ProrationFactor = factor,
        Status = status,
    };
}
