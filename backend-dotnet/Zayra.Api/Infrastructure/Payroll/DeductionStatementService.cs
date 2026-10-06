using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The deductions statement (Release A slice R3): a READ MODEL over the deduction lines payroll already persisted.
/// It changes no payroll figure and recomputes nothing. For each payslip it says what every deduction is, why it is
/// lawful, whether it counts toward the Art. 93 half-wage limit, and — for loans and advances — what is left.
///
/// <para><b>One classifier.</b> Whether a line counts toward the cap is <see cref="WageDeductionClassification.IsDebtType(PayrollDeduction)"/>,
/// the same rule the validation engine and the bank export apply. This file only adds a display category and the
/// legal-basis key; it never decides the cap differently. GOSI is therefore not counted (owner decision, #177).</para>
///
/// <para><b>Per-loan split.</b> Payroll writes ONE aggregate <c>LOAN_EMI</c> (and <c>ADVANCE_EMI</c>) line per employee.
/// It is split per loan from the run's own witnesses: the <see cref="PayrollRunConsumption"/> rows Process wrote for each
/// loan/advance it decremented (amount taken and the balance before), falling back to the schedule rows stamped with
/// the run (<c>loan_installments.payroll_run_id</c>) for a run that wrote no witness. The split is used only when it adds
/// up to the line to the cent; otherwise the line stays one total and <see cref="ReleaseABlockReasons.DeductionSplitUnreconciled"/>
/// is raised — a balance is never guessed.</para>
///
/// <para><b>Fail-closed.</b> A negative adjustment of an unrecognised type is counted (the classifier's rule) and flagged
/// <see cref="ReleaseABlockReasons.DeductionUnclassifiedCounted"/>; debt against a zero wage is over the limit.</para>
/// </summary>
public sealed class DeductionStatementService(ZayraDbContext db) : IDeductionStatementService
{
    /// <summary>"Near the limit": more than this share of the 50% limit is already used (debt above 40% of the wage).</summary>
    public const decimal NearCapShareOfLimit = 0.8m;

    /// <summary>Art. 92: an employer-loan instalment above this percentage of the wage needs written consent.</summary>
    public const decimal LoanConsentThresholdPercent = 10m;

    public async Task<DeductionStatement> ForSlipAsync(Guid tenantId, Guid slipId, DeductionAudience who, CancellationToken ct) =>
        (await DetailForSlipAsync(tenantId, slipId, who, ct))?.Statement
        ?? throw new KeyNotFoundException("No payslip with that id is visible to this caller.");

    /// <summary>One slip's statement with the facts the API shows beside it, or null when the slip is not visible:
    /// another tenant, outside the caller's company scope (query filters), or — for an employee — not final or voided.</summary>
    public async Task<DeductionStatementDetail?> DetailForSlipAsync(Guid tenantId, Guid slipId, DeductionAudience who, CancellationToken ct)
    {
        var slip = await db.PayrollSlips.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == slipId, ct);
        if (slip is null) return null;
        var run = await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == slip.RunId, ct);
        if (who == DeductionAudience.Employee && !VisibleToEmployee(slip, run)) return null;
        return (await BuildAsync(tenantId, [(slip, run)], ct)).Single();
    }

    /// <summary>Every slip of one run the caller may see (<paramref name="allowedEmployeeIds"/> null = all in scope).
    /// Null when the run itself is not visible.</summary>
    public async Task<IReadOnlyList<DeductionStatementDetail>?> ForRunAsync(Guid tenantId, Guid runId,
        IReadOnlyCollection<int>? allowedEmployeeIds, CancellationToken ct)
    {
        var run = await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null) return null;
        var query = db.PayrollSlips.AsNoTracking().Where(s => s.TenantId == tenantId && s.RunId == runId);
        if (allowedEmployeeIds is not null) query = query.Where(s => allowedEmployeeIds.Contains(s.EmployeeId));
        var slips = await query.OrderBy(s => s.EmployeeCode).ThenBy(s => s.EmployeeId).ToListAsync(ct);
        return await BuildAsync(tenantId, slips.Select(s => (s, (PayrollRun?)run)).ToList(), ct);
    }

    /// <summary>One employee's statements for the last <paramref name="months"/> pay periods, newest first. Voided runs are
    /// never shown; an employee additionally sees only final (locked) slips.</summary>
    public async Task<IReadOnlyList<DeductionStatementDetail>> ForEmployeeAsync(Guid tenantId, int employeeId, int months,
        DeductionAudience who, DateOnly today, CancellationToken ct)
    {
        months = Math.Clamp(months, 1, 24);
        var cutoff = today.Year * 12 + today.Month - 1 - (months - 1);
        var slipQuery = db.PayrollSlips.AsNoTracking().Where(s => s.TenantId == tenantId && s.EmployeeId == employeeId);
        if (who == DeductionAudience.Employee) slipQuery = slipQuery.Where(s => s.Status == FinalSlipStatus);
        var rows = await (
                from slip in slipQuery
                join run in db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tenantId) on slip.RunId equals run.Id
                where run.Status != VoidedStatus && run.Year * 12 + run.Month - 1 >= cutoff
                select new { slip, run })
            .ToListAsync(ct);
        var ordered = rows
            .Where(x => who == DeductionAudience.Hr || VisibleToEmployee(x.slip, x.run))
            .OrderByDescending(x => x.run.Year).ThenByDescending(x => x.run.Month).ThenByDescending(x => x.run.CreatedAtUtc)
            .Select(x => (x.slip, (PayrollRun?)x.run)).ToList();
        return await BuildAsync(tenantId, ordered, ct);
    }

    /// <summary>The employee's open loans and advances today: what is owed, the instalment, and what is left.</summary>
    public async Task<DeductionBalances> BalancesAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var loans = await db.EmployeeLoans.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EmployeeIntId == employeeId && !l.IsDeleted
                        && (l.Status == "Active" || l.Status == "Overdue") && l.OutstandingBalance > 0)
            .OrderBy(l => l.CreatedAtUtc).ToListAsync(ct);
        var loanIds = loans.Select(l => l.Id).ToList();
        var loanNext = (await db.LoanInstallments.AsNoTracking()
                .Where(i => i.TenantId == tenantId && loanIds.Contains(i.LoanId) && i.Status == "Pending")
                .Select(i => new { i.LoanId, i.DueDate }).ToListAsync(ct))
            .GroupBy(i => i.LoanId).ToDictionary(g => g.Key, g => g.Min(i => i.DueDate));

        var advances = await db.SalaryAdvances.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeIntId == employeeId && !a.IsDeleted
                        && a.Status == "Active" && a.OutstandingBalance > 0)
            .OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
        var advanceIds = advances.Select(a => a.Id).ToList();
        var advanceNext = (await db.AdvanceInstallments.AsNoTracking()
                .Where(i => i.TenantId == tenantId && advanceIds.Contains(i.AdvanceId) && i.Status == "Pending")
                .Select(i => new { i.AdvanceId, i.DueDate }).ToListAsync(ct))
            .GroupBy(i => i.AdvanceId).ToDictionary(g => g.Key, g => g.Min(i => i.DueDate));

        var typeIds = loans.Select(l => l.LoanTypeId).Distinct().ToList();
        var arabic = await db.LoanTypes.AsNoTracking().Where(t => t.TenantId == tenantId && typeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.NameAr, ct);
        return new DeductionBalances(
            loans.Select(l => new DebtBalance(l.Id, l.LoanNumber, l.LoanTypeName, l.Currency, l.OutstandingBalance, l.InstallmentAmount,
                Remaining(l.OutstandingBalance, l.InstallmentAmount), Math.Max(l.ApprovedInstallments, 0),
                loanNext.TryGetValue(l.Id, out var n) ? n : null, l.ConsentDocumentId.HasValue,
                l.RepaymentMethod == "PayrollDeduction",
                arabic.TryGetValue(l.LoanTypeId, out var ar) && !string.IsNullOrWhiteSpace(ar) ? ar : null)).ToList(),
            advances.Select(a => new DebtBalance(a.Id, a.AdvanceNumber, "Salary advance", null, a.OutstandingBalance, a.InstallmentAmount,
                Remaining(a.OutstandingBalance, a.InstallmentAmount), Math.Max(a.Installments, 0),
                advanceNext.TryGetValue(a.Id, out var n) ? n : null, null, true)).ToList());
    }

    // ── Loading ─────────────────────────────────────────────────────────────────────────────────────

    private const string FinalSlipStatus = "Final";
    private const string VoidedStatus = "Voided";

    /// <summary>An employee sees a slip only once it is final (the run locked) and only while its run is not voided.</summary>
    public static bool VisibleToEmployee(PayrollSlip slip, PayrollRun? run) =>
        slip.Status == FinalSlipStatus && run is not null && run.Status != VoidedStatus;

    private async Task<IReadOnlyList<DeductionStatementDetail>> BuildAsync(Guid tenantId,
        IReadOnlyList<(PayrollSlip Slip, PayrollRun? Run)> slips, CancellationToken ct)
    {
        if (slips.Count == 0) return [];
        var runIds = slips.Select(s => s.Slip.RunId).Distinct().ToList();
        var employeeIds = slips.Select(s => s.Slip.EmployeeId).Distinct().ToList();

        // The Art. 93 limit belongs to the PAY PERIOD: every other non-voided run of the same month (off-cycle,
        // supplementary, correction) shares it. Their slips are loaded with the employee's own.
        var periods = slips.Where(s => s.Run is not null).Select(s => s.Run!.Year * 12 + s.Run.Month).Distinct().ToList();
        var periodRuns = periods.Count == 0
            ? []
            : await db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Status != VoidedStatus && periods.Contains(r.Year * 12 + r.Month))
                .Select(r => new PeriodRun(r.Id, r.Year * 12 + r.Month)).ToListAsync(ct);
        var siblingRunIds = periodRuns.Select(r => r.Id).Where(id => !runIds.Contains(id)).ToList();
        var siblingSlips = siblingRunIds.Count == 0
            ? []
            : await db.PayrollSlips.AsNoTracking()
                .Where(s => s.TenantId == tenantId && siblingRunIds.Contains(s.RunId) && employeeIds.Contains(s.EmployeeId))
                .ToListAsync(ct);
        var livePeriodOf = periodRuns.ToDictionary(r => r.Id, r => r.Period);
        var allRunIds = runIds.Concat(siblingRunIds).ToList();

        // Employee-side lines only: an employer contribution (e.g. the employer's GOSI share) is not taken from pay.
        var lines = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && allRunIds.Contains(d.PayrollRunId) && employeeIds.Contains(d.EmployeeId)
                        && !d.IsEmployerContribution)
            .ToListAsync(ct);
        var witnesses = await db.PayrollRunConsumptions.AsNoTracking()
            .Where(w => w.TenantId == tenantId && runIds.Contains(w.PayrollRunId) && employeeIds.Contains(w.EmployeeId)
                        && (w.ArtifactType == PayrollConsumptionArtifacts.Loan || w.ArtifactType == PayrollConsumptionArtifacts.Advance))
            .ToListAsync(ct);
        var nullableEmployeeIds = employeeIds.Select(id => (int?)id).ToList();
        var loans = await db.EmployeeLoans.AsNoTracking()
            .Where(l => l.TenantId == tenantId && nullableEmployeeIds.Contains(l.EmployeeIntId))
            .ToListAsync(ct);
        var advances = await db.SalaryAdvances.AsNoTracking()
            .Where(a => a.TenantId == tenantId && nullableEmployeeIds.Contains(a.EmployeeIntId))
            .ToListAsync(ct);
        var loanTypeIds = loans.Select(l => l.LoanTypeId).Distinct().ToList();
        var arabicTypeNames = loanTypeIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.LoanTypes.AsNoTracking()
                .Where(t => t.TenantId == tenantId && loanTypeIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.NameAr, ct);
        var loanIds = loans.Select(l => l.Id).ToList();
        var advanceIds = advances.Select(a => a.Id).ToList();
        var nullableRunIds = runIds.Select(id => (Guid?)id).ToList();
        var loanInstalments = await db.LoanInstallments.AsNoTracking()
            .Where(i => i.TenantId == tenantId && loanIds.Contains(i.LoanId) && nullableRunIds.Contains(i.PayrollRunId))
            .ToListAsync(ct);
        var advanceInstalments = await db.AdvanceInstallments.AsNoTracking()
            .Where(i => i.TenantId == tenantId && advanceIds.Contains(i.AdvanceId) && nullableRunIds.Contains(i.PayrollRunId))
            .ToListAsync(ct);
        // Art. 92's fallback basis when a loan carries no cap_base_wage witness: the salary structure in force.
        var structures = await db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && employeeIds.Contains(x.EmployeeId) && x.IsActive)
            .ToListAsync(ct);

        var facts = new Dictionary<Guid, DebtFacts>();
        foreach (var l in loans)
            facts[l.Id] = new DebtFacts(l.Id, l.LoanNumber, l.LoanTypeName, Math.Max(l.ApprovedInstallments, 0), l.InstallmentAmount,
                l.ConsentDocumentId.HasValue, IsAdvance: false, l.CapBaseWage,
                arabicTypeNames.TryGetValue(l.LoanTypeId, out var ar) && !string.IsNullOrWhiteSpace(ar) ? ar : null);
        foreach (var a in advances)
            facts[a.Id] = new DebtFacts(a.Id, a.AdvanceNumber, "Salary advance", Math.Max(a.Installments, 0), a.InstallmentAmount,
                null, IsAdvance: true);

        var allSlips = siblingSlips.Concat(slips.Select(x => x.Slip)).DistinctBy(x => x.Id).ToList();
        var currencies = new Dictionary<Guid, string>();
        var result = new List<DeductionStatementDetail>(slips.Count);
        foreach (var (slip, run) in slips)
        {
            var empLines = lines.Where(d => d.PayrollRunId == slip.RunId && d.EmployeeId == slip.EmployeeId).ToList();
            var empWitnesses = witnesses.Where(w => w.PayrollRunId == slip.RunId && w.EmployeeId == slip.EmployeeId).ToList();
            var empLoanIds = loans.Where(l => l.EmployeeIntId == slip.EmployeeId).Select(l => l.Id).ToHashSet();
            var empAdvanceIds = advances.Where(a => a.EmployeeIntId == slip.EmployeeId).Select(a => a.Id).ToHashSet();
            var loanTakes = Takes(empWitnesses, PayrollConsumptionArtifacts.Loan,
                loanInstalments.Where(i => i.PayrollRunId == slip.RunId && empLoanIds.Contains(i.LoanId)).Select(i => (i.LoanId, i.AmountPaid)));
            var advanceTakes = Takes(empWitnesses, PayrollConsumptionArtifacts.Advance,
                advanceInstalments.Where(i => i.PayrollRunId == slip.RunId && empAdvanceIds.Contains(i.AdvanceId)).Select(i => (i.AdvanceId, i.AmountPaid)));

            var voided = run?.Status == VoidedStatus;
            int? period = run is null ? null : run.Year * 12 + run.Month;
            // The same employee's slips on the period's OTHER non-voided runs. None for a voided run: it no longer applies.
            var siblings = voided || period is null
                ? []
                : allSlips.Where(o => o.EmployeeId == slip.EmployeeId && o.RunId != slip.RunId
                                      && livePeriodOf.TryGetValue(o.RunId, out var p) && p == period).ToList();
            var siblingRuns = siblings.Select(o => o.RunId).ToHashSet();
            var siblingLines = lines.Where(d => d.EmployeeId == slip.EmployeeId && siblingRuns.Contains(d.PayrollRunId)).ToList();
            var periodEnd = run is null ? DateOnly.MaxValue : new DateOnly(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month));
            var structure = structures.Where(x => x.EmployeeId == slip.EmployeeId && x.EffectiveDate <= periodEnd)
                .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            var structureWage = Zayra.Api.Infrastructure.Finance.LoanEligibilityService.MonthlyWage(structure);

            var context = new StatementPeriod(
                OtherRunsDebt: siblingLines.Where(WageDeductionClassification.IsDebtType).Sum(l => l.Amount),
                OtherRunsWageDue: siblings.Sum(o => WageDeductionClassification.WageDue(o.GrossSalary, siblingLines.Where(d => d.PayrollRunId == o.RunId))),
                OtherRuns: siblingRuns.Count,
                StructureWage: structureWage > 0m ? structureWage : null,
                Voided: voided);
            var statement = DeductionStatementBuilder.Build(slip, run?.Year ?? 0, run?.Month ?? 0, empLines, loanTakes, advanceTakes, facts, context);
            var companyKey = slip.CompanyId ?? Guid.Empty;
            if (!currencies.TryGetValue(companyKey, out var currency))
                currencies[companyKey] = currency = await GlAccountResolver.ResolveCurrencyAsync(db, tenantId, slip.CompanyId, ct);
            var debts = statement.Lines.Where(l => l.LoanId is Guid id && facts.ContainsKey(id))
                .Select(l => facts[l.LoanId!.Value]).DistinctBy(f => f.Id).ToDictionary(f => f.Id);
            result.Add(new DeductionStatementDetail(statement, slip, run, currency, debts, context,
                empLines.Where(WageDeductionClassification.IsWageReduction).Sum(l => l.Amount)));
        }
        return result;
    }

    private sealed record PeriodRun(Guid Id, int Period);

    /// <summary>Per-debt amounts one run took for one employee: the witnesses when the run wrote them, else the schedule
    /// rows the run stamped. Never both (a witnessed run also stamps its schedule rows).</summary>
    private static IReadOnlyList<DebtTake> Takes(IReadOnlyList<PayrollRunConsumption> witnesses, string artifact,
        IEnumerable<(Guid DebtId, decimal Amount)> stamped)
    {
        var witnessed = witnesses.Where(w => w.ArtifactType == artifact)
            .Select(w => new DebtTake(w.ArtifactId, w.Amount, w.PriorOutstandingBalance)).ToList();
        return witnessed.Count > 0 ? witnessed : stamped.Select(s => new DebtTake(s.DebtId, s.Amount, null)).ToList();
    }

    /// <summary>Instalments left at the current instalment: ⌈balance ÷ instalment⌉; 0 once repaid; null when unknown.</summary>
    internal static int? Remaining(decimal? balance, decimal instalment)
    {
        if (balance is not decimal b) return null;
        if (b <= 0.005m) return 0;
        return instalment > 0m ? (int)Math.Ceiling(b / instalment) : null;
    }
}

/// <summary>A statement plus what the API shows beside it: the slip (its header deductions total is what the lines must
/// reconcile to), its run, the company currency, and the loans/advances its lines belong to.</summary>
public sealed record DeductionStatementDetail(
    DeductionStatement Statement,
    PayrollSlip Slip,
    PayrollRun? Run,
    string Currency,
    IReadOnlyDictionary<Guid, DebtFacts> Debts,
    StatementPeriod Period,
    decimal PayNotEarned)
{
    /// <summary>Within / Near / Over / NeedsReview / Voided (<see cref="CapStatuses"/>).</summary>
    public string CapStatus => DeductionStatementBuilder.CapStatus(Statement, Period.Voided);
}

/// <summary>What the statement knows beyond its own slip: the period's other non-voided runs (the limit is per pay period),
/// the salary-structure wage (Art. 92's fallback basis), and whether this slip's run was voided.</summary>
public sealed record StatementPeriod(decimal OtherRunsDebt = 0m, decimal OtherRunsWageDue = 0m, int OtherRuns = 0,
    decimal? StructureWage = null, bool Voided = false);

/// <summary>The facts of one loan or advance a statement line belongs to.</summary>
/// <param name="CapBaseWage">The wage the loan's 10% test was computed against at request/approval (the Art. 92 basis).</param>
public sealed record DebtFacts(Guid Id, string Number, string TypeName, int InstalmentsTotal, decimal Instalment, bool? ConsentOnFile, bool IsAdvance,
    decimal? CapBaseWage = null, string? TypeNameAr = null);

/// <summary>What one run took from one loan or advance, and the balance before (null when no witness recorded it).</summary>
public sealed record DebtTake(Guid DebtId, decimal Amount, decimal? PriorOutstanding);

public sealed record DeductionBalances(IReadOnlyList<DebtBalance> Loans, IReadOnlyList<DebtBalance> Advances);

/// <param name="ConsentOnFile">Loans only: the employee's written consent to an instalment above 10% of the wage.</param>
/// <param name="DeductedFromPay">True when the instalment is taken through payroll.</param>
public sealed record DebtBalance(Guid Id, string Number, string TypeName, string? Currency, decimal Outstanding, decimal Instalment,
    int? InstalmentsRemaining, int InstalmentsTotal, DateOnly? NextDueOn, bool? ConsentOnFile, bool DeductedFromPay, string? TypeNameAr = null);

/// <summary>The pure part of the statement: categories, legal basis, the per-loan split, the cap and its flags.</summary>
public static class DeductionStatementBuilder
{
    /// <summary>Adjustment types (normalised, after <c>ADJ_</c>) known to be a penalty, fine or damages (Art. 92).</summary>
    public static readonly IReadOnlySet<string> PenaltyAdjustmentTypeCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PENALTY", "FINE", "DAMAGE", "DAMAGES", "DISCIPLINARY", "DISCIPLINARY_PENALTY", "DISCIPLINARY_DEDUCTION",
    };

    /// <summary>Adjustment types (normalised) known to be a debt ordered by a court.</summary>
    public static readonly IReadOnlySet<string> CourtOrderAdjustmentTypeCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "COURT_ORDER", "COURT", "JUDGMENT", "JUDGEMENT", "JUDGMENT_DEBT", "JUDGEMENT_DEBT", "GARNISHMENT",
    };

    public const string LoanCode = "LOAN_EMI";
    public const string AdvanceCode = "ADVANCE_EMI";
    private const decimal Cent = 0.005m;

    /// <summary>The statement for one slip on its own (no other runs in the period, no structure wage).</summary>
    public static DeductionStatement Build(PayrollSlip slip, int year, int month, IReadOnlyList<PayrollDeduction> lines,
        IReadOnlyList<DebtTake> loanTakes, IReadOnlyList<DebtTake> advanceTakes, IReadOnlyDictionary<Guid, DebtFacts> facts) =>
        Build(slip, year, month, lines, loanTakes, advanceTakes, facts, new StatementPeriod());

    /// <summary>Component code of the line that carries the debt deducted on the period's other runs.</summary>
    public const string OtherRunsCode = "OTHER_RUNS_THIS_PERIOD";

    public static DeductionStatement Build(PayrollSlip slip, int year, int month, IReadOnlyList<PayrollDeduction> lines,
        IReadOnlyList<DebtTake> loanTakes, IReadOnlyList<DebtTake> advanceTakes, IReadOnlyDictionary<Guid, DebtFacts> facts,
        StatementPeriod period)
    {
        // Art. 93 wage due: gross minus pay not earned (absence/LOP, unpaid leave) — one definition, shared with the
        // validation engine and the bank export — over the period's non-voided runs.
        var wage = WageDeductionClassification.WageDue(slip.GrossSalary, lines) + period.OtherRunsWageDue;
        var flags = new List<string>();
        void Flag(string code) { if (!flags.Contains(code)) flags.Add(code); }
        var output = new List<DeductionStatementLine>();

        foreach (var line in lines.Where(l => !IsEmi(l) && !l.IsEmployerContribution)
                     .OrderBy(Order).ThenBy(l => l.ComponentCode, StringComparer.Ordinal))
        {
            var (category, basis, unclassified) = Classify(line);
            var counts = WageDeductionClassification.IsDebtType(line);
            if (unclassified && counts) Flag(ReleaseABlockReasons.DeductionUnclassifiedCounted);
            output.Add(new DeductionStatementLine(line.ComponentCode, line.ComponentName, category, line.Amount, counts, basis,
                null, null, null, null, null));
        }

        foreach (var (code, takes) in new[] { (LoanCode, loanTakes), (AdvanceCode, advanceTakes) })
        {
            var emi = lines.Where(l => IsEmi(l) && !l.IsEmployerContribution && l.ComponentCode == code).ToList();
            if (emi.Count == 0) continue;
            var total = emi.Sum(l => l.Amount);
            var counts = emi.All(WageDeductionClassification.IsDebtType);
            var isAdvance = code == AdvanceCode;
            var category = isAdvance ? DeductionCategories.SalaryAdvance : DeductionCategories.EmployerLoan;
            var basis = isAdvance ? DeductionLegalBasis.SalaryAdvance : DeductionLegalBasis.EmployerLoan;
            var positive = takes.Where(t => t.Amount > 0m).ToList();
            if (positive.Count == 0 || Math.Abs(positive.Sum(t => t.Amount) - total) > Cent)
            {
                // Cannot attribute the aggregate to loans: show it as one total and assume no balance.
                Flag(ReleaseABlockReasons.DeductionSplitUnreconciled);
                output.Add(new DeductionStatementLine(code, emi[0].ComponentName, category, total, counts, basis,
                    null, null, null, Share(total, period.StructureWage), null));
                continue;
            }
            foreach (var take in positive)
            {
                facts.TryGetValue(take.DebtId, out var debt);
                decimal? balanceAfter = take.PriorOutstanding is decimal prior ? Math.Max(0m, prior - take.Amount) : null;
                // Art. 92's 10% is against the wage the loan was assessed on (its cap_base_wage witness), falling back to
                // the salary structure — never this slip's gross. Compared unrounded: 10.0004% is above 10%.
                var basisWage = debt?.CapBaseWage is > 0m ? debt.CapBaseWage : period.StructureWage;
                bool? consent = isAdvance ? null : debt?.ConsentOnFile ?? false;
                if (!isAdvance && consent != true && AboveConsentThreshold(take.Amount, basisWage))
                    Flag(ReleaseABlockReasons.LoanInstalmentOver10PctNoConsent);
                output.Add(new DeductionStatementLine(code, debt?.TypeName ?? emi[0].ComponentName, category, take.Amount, counts, basis,
                    take.DebtId, balanceAfter, DeductionStatementService.Remaining(balanceAfter, debt?.Instalment ?? 0m),
                    Share(take.Amount, basisWage), consent));
            }
        }

        // Every employee-side line must be on the statement. Lines that do not add up to the slip's own deductions total
        // (a summary-only slip) can never read as "within the limit".
        if (Math.Abs(output.Sum(l => l.Amount) - slip.Deductions) >= 0.01m)
            Flag(ReleaseABlockReasons.DeductionLinesMissing);

        if (period.OtherRunsDebt > 0m)
            output.Add(new DeductionStatementLine(OtherRunsCode, string.Empty, DeductionCategories.Other, period.OtherRunsDebt, true,
                DeductionLegalBasis.OtherRunsThisPeriod, null, null, null, null, null));

        var debtTotal = output.Where(l => l.CountsTowardCap).Sum(l => l.Amount);
        var capLimit = WageDeductionClassification.HalfWageLimit(wage);
        if (WageDeductionClassification.ExceedsHalfWage(debtTotal, wage))
            Flag(ReleaseABlockReasons.DeductionsOverHalfWage);
        // A voided run no longer applies: no flags, no advice.
        if (period.Voided) flags.Clear();
        // Over-the-limit first: it is the one a payroll manager must act on before Lock.
        var ordered = flags.OrderBy(f => f == ReleaseABlockReasons.DeductionsOverHalfWage ? 0 : 1).ToList();
        return new DeductionStatement(slip.Id, slip.EmployeeId, year, month, wage, debtTotal, capLimit, capLimit - debtTotal,
            output, ordered);
    }

    /// <summary>Within / Near / Over / NeedsReview / Voided. Near = more than <see cref="DeductionStatementService.NearCapShareOfLimit"/>
    /// of the limit used; NeedsReview = the lines do not add up to the slip, so "within" cannot be claimed.</summary>
    public static string CapStatus(DeductionStatement s, bool voided = false)
    {
        if (voided) return CapStatuses.Voided;
        if (s.Flags.Contains(ReleaseABlockReasons.DeductionsOverHalfWage)) return CapStatuses.Over;
        if (s.Flags.Contains(ReleaseABlockReasons.DeductionLinesMissing)) return CapStatuses.NeedsReview;
        return s.CapLimit > 0m && s.DebtTotal > s.CapLimit * DeductionStatementService.NearCapShareOfLimit ? CapStatuses.Near : CapStatuses.Within;
    }

    /// <summary>The exception-first rule, identical on the server's nearCap filter and in the UI: anything not plainly within
    /// the limit, or carrying a flag. A voided run is never an exception.</summary>
    public static bool NeedsAttention(string capStatus, IReadOnlyCollection<string> flags) =>
        capStatus != CapStatuses.Voided && (capStatus != CapStatuses.Within || flags.Count > 0);

    /// <summary>Art. 92: an instalment above 10% of the basis wage — or with no known basis (fail-closed). Unrounded.</summary>
    public static bool AboveConsentThreshold(decimal instalment, decimal? basisWage) =>
        basisWage is not decimal w || w <= 0m || instalment > w * DeductionStatementService.LoanConsentThresholdPercent / 100m;

    /// <summary>An amount as a percentage (0–100) of a basis, UNROUNDED (display floors it); null without a basis.</summary>
    public static decimal? Share(decimal amount, decimal? basis) => basis is > 0m ? amount / basis.Value * 100m : null;

    /// <summary>A percentage for display: floored to two decimals, so 50.009% never shows as 50.01% and 10.0004% never
    /// rounds past what it is.</summary>
    public static decimal? FloorPercent(decimal? pct) => pct is decimal p ? decimal.Floor(p * 100m) / 100m : null;

    /// <summary>Debt as a percentage (0–100) of the wage due, floored for display; null when there is no wage.</summary>
    public static decimal? Percent(decimal amount, decimal wage) => FloorPercent(Share(amount, wage));

    private static bool IsEmi(PayrollDeduction l) =>
        string.Equals(l.Source, WageDeductionClassification.LoanSource, StringComparison.Ordinal)
        && (l.ComponentCode == LoanCode || l.ComponentCode == AdvanceCode);

    /// <summary>Display category and legal basis of one line, and whether its type is unrecognised. Never decides the cap.</summary>
    public static (string Category, string Basis, bool Unclassified) Classify(PayrollDeduction line)
    {
        switch (line.Source)
        {
            case "Statutory":
            case "Tax":
                return (DeductionCategories.Statutory, DeductionLegalBasis.Statutory, false);
            case "Attendance":
            case "Leave":
                return (DeductionCategories.Absence, DeductionLegalBasis.Absence, false);
            case WageDeductionClassification.LoanSource:
                return line.ComponentCode == AdvanceCode
                    ? (DeductionCategories.SalaryAdvance, DeductionLegalBasis.SalaryAdvance, false)
                    : (DeductionCategories.EmployerLoan, DeductionLegalBasis.EmployerLoan, false);
            case WageDeductionClassification.AdjustmentSource:
            {
                var type = line.ComponentCode.StartsWith(WageDeductionClassification.AdjustmentCodePrefix, StringComparison.Ordinal)
                    ? line.ComponentCode[WageDeductionClassification.AdjustmentCodePrefix.Length..]
                    : string.Empty;
                if (WageDeductionClassification.NonDebtAdjustmentTypeCodes.Contains(type))
                    return (DeductionCategories.PenaltyOrAdjustment, DeductionLegalBasis.PayCorrection, false);
                if (CourtOrderAdjustmentTypeCodes.Contains(type))
                    return (DeductionCategories.CourtOrder, DeductionLegalBasis.CourtOrder, false);
                if (PenaltyAdjustmentTypeCodes.Contains(type))
                    return (DeductionCategories.PenaltyOrAdjustment, DeductionLegalBasis.Penalty, false);
                return (DeductionCategories.PenaltyOrAdjustment, DeductionLegalBasis.Unclassified, true);
            }
            case PayrollRecoveryComponents.RecoverySource:
                return (DeductionCategories.Other, DeductionLegalBasis.OverpaymentRecovery, false);
        }
        if (string.Equals(line.GlDriverKey, WageDeductionClassification.LoanGlDriver, StringComparison.Ordinal))
            return (DeductionCategories.EmployerLoan, DeductionLegalBasis.EmployerLoan, false);
        return (DeductionCategories.Other, DeductionLegalBasis.Other, false);
    }

    private static int Order(PayrollDeduction l) => Classify(l).Category switch
    {
        DeductionCategories.Statutory => 0,
        DeductionCategories.Absence => 1,
        DeductionCategories.PenaltyOrAdjustment => 2,
        DeductionCategories.CourtOrder => 3,
        _ => 4,
    };
}

/// <summary>Where a statement's debt-type total sits against the Art. 93 limit.</summary>
public static class CapStatuses
{
    public const string Within = "Within";
    public const string Near = "Near";
    public const string Over = "Over";
    /// <summary>The lines do not reconcile to the slip's deductions total, so "within" cannot be shown.</summary>
    public const string NeedsReview = "NeedsReview";
    /// <summary>The run was voided: the statement no longer applies.</summary>
    public const string Voided = "Voided";
}

/// <summary>
/// Legal-basis keys of a statement line. Each is an i18n key (frontend <c>src/i18n/releaseA/deductions.ts</c>, EN and AR),
/// never free text. They state the basis only; whether a line counts toward the limit is a separate field.
/// </summary>
public static class DeductionLegalBasis
{
    public const string Statutory = "Required by law: social insurance (GOSI) and other contributions the law makes payable (Article 92).";
    public const string EmployerLoan = "Repayment of a loan from the employer (Article 92). Each instalment may be at most 10% of the wage unless the employee agreed in writing.";
    public const string SalaryAdvance = "Repayment of a salary advance from the employer (Article 92).";
    public const string Absence = "Pay not earned for time not worked. This is not a deduction under Article 92.";
    public const string Penalty = "A penalty or damages under the approved work regulations (Article 92).";
    public const string CourtOrder = "A debt ordered by a court (Article 92).";
    public const string PayCorrection = "A correction of this month's own pay. It is not a debt owed to the employer.";
    public const string Unclassified = "A payroll adjustment of a type the system does not recognise.";
    public const string OverpaymentRecovery = "Recovery of pay overpaid in an earlier payroll run.";
    public const string Other = "Another deduction recorded on the payslip.";
    public const string OtherRunsThisPeriod = "Debt deductions on the other payroll runs for the same month. The limit applies to the whole month.";

    public static readonly string[] All =
        [Statutory, EmployerLoan, SalaryAdvance, Absence, Penalty, CourtOrder, PayCorrection, Unclassified, OverpaymentRecovery, Other,
            OtherRunsThisPeriod];
}
