using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-A1 — the SINGLE source of GOSI/statutory reconciliation truth for a locked payroll run.
///
/// This service unifies what used to be three divergent reconciliation paths
/// (GosiController.contribution-summary, GosiController.variance-report,
/// SaudiComplianceDashboardService.varianceCount). Every one of them previously
/// computed "expected" through <see cref="GosiCalculationService"/> on basic-salary-only against
/// the <c>GosiContributionRule</c> table, while the RUN itself deducted via the country pack
/// (<c>KsaDeductionCalculator</c>) on the GOSI covered wage (basic + housing + eligible bonus,
/// capped at the statutory ceiling) against the <c>StatutoryRule</c> table. The two engines used a
/// different base and a different rate store, so "expected" could never equal "actual" and the
/// reports flagged 100% of Saudi employees.
///
/// The fix here computes "expected" with the SAME pack + base the run used, reconstructed from the
/// run's own persisted <see cref="PayrollSlip"/> (drift-proof: basic/housing come straight from the
/// slip the run wrote, so a post-run salary edit or a non-deterministic salary re-query can never
/// introduce a phantom variance) plus the run's consumed GOSI-eligible bonuses. "Actual" is always
/// the persisted <see cref="PayrollDeduction"/> rows (<c>Source == "Statutory"</c>). A GL tie-out
/// reconciles those actuals to the posted <see cref="FinanceGlEntry"/> liabilities (default 2101 EE /
/// 2106 ER) by COMPONENT CODE — never by account number — so it stays correct even when a company
/// remaps the account, while still reporting which account received the credit.
///
/// Two guarantees, kept distinct on purpose:
///  • actual == GL  is the financial control that must always be zero (GL is derived from the same
///    deduction rows at lock, idempotent) — the halala tie-out.
///  • expected == actual  holds only while salary/bonus/nationality/rates are unchanged since the run;
///    its purpose is to DETECT drift, so a non-zero value is a true finding, surfaced as a named delta.
///
/// <see cref="GosiCalculationService"/> / <c>GosiContributionRule</c> are intentionally retained for
/// GOSI READINESS/PREVIEW (a distinct pre-run view) — they are no longer on any reconciliation path.
/// </summary>
public sealed class GosiReconciliationService
{
    private readonly ZayraDbContext _db;
    private readonly ICountryPackResolver _packResolver;

    public GosiReconciliationService(ZayraDbContext db, ICountryPackResolver packResolver)
    {
        _db = db;
        _packResolver = packResolver;
    }

    // "Payroll deduction: <ComponentCode>" — the exact Description the GL poster stamps on a
    // per-deduction credit line (PayrollController.BuildPayrollGlEntries). The balancing employer
    // expense DR to 5101 uses a DIFFERENT description ("Employer statutory contributions …"), so
    // matching this prefix reconciles to the 2106 CR liability only and never double-counts the 5101 DR.
    private const string DeductionDescriptionPrefix = "Payroll deduction: ";

    /// <summary>
    /// Reconciles one payroll run's statutory deductions: recomputes "expected" with the run's own
    /// country pack + covered-wage base, reads "actual" from the persisted deductions, and ties both
    /// out to the posted GL liabilities. Never mutates anything.
    /// </summary>
    public async Task<GosiRunReconciliation> ReconcileAsync(Guid tenantId, PayrollRun run, CancellationToken ct)
    {
        // ── Resolve the run's company → country pack (mirror PayrollController.Process:553-586) ──
        // Reconciliation MUST use the same pack (country/jurisdiction) the run used, resolved from the
        // RUN's company — never the caller's company — so multi-entity tenants reconcile per entity.
        var activeCompanies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);
        var company = run.CompanyId.HasValue
            ? activeCompanies.FirstOrDefault(c => c.Id == run.CompanyId.Value)
            : activeCompanies.Count == 1 ? activeCompanies[0] : null;

        // ── POD-B2: does this run share its period? ─────────────────────────────────────────────
        // Same scoping rule as PayrollController.LoadSiblingRunsAsync (null-company runs are in scope for
        // every company, because Process collapses them onto one). Zero for every run in every tenant
        // before B2, so nothing below it changes for them.
        var scopeCompanyId = run.CompanyId ?? company?.Id;
        var siblingRunCount = await _db.PayrollRuns.AsNoTracking()
            .CountAsync(r => r.TenantId == tenantId && r.Id != run.Id
                          && r.Year == run.Year && r.Month == run.Month
                          && (scopeCompanyId == null || r.CompanyId == scopeCompanyId || r.CompanyId == null)
                          && r.Status != "Voided", ct);

        // ── Load the run's persisted outputs (the immutable witnesses) ──────────────────────────
        var slips = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RunId == run.Id)
            .ToListAsync(ct);

        var deductions = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == run.Id && d.Source == "Statutory")
            .ToListAsync(ct);

        var hasStatutoryData = deductions.Count > 0;

        // Actual (persisted) split. IsEmployerContribution is the authoritative flag set at write time
        // (PayrollController:977-979); the -EE/-ER suffix is a defensive cross-check for the pack codes.
        var actualEeByEmp = deductions.Where(d => !d.IsEmployerContribution)
            .GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        var actualErByEmp = deductions.Where(d => d.IsEmployerContribution)
            .GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

        // Distinct statutory component code → employer-side flag, used to classify the posted GL lines.
        var statutoryCodeIsEmployer = deductions
            .GroupBy(d => d.ComponentCode)
            .ToDictionary(g => g.Key, g => g.Any(d => d.IsEmployerContribution));

        // ── Re-derive the run's GOSI-eligible bonus per employee (base reconstruction, part 2) ──
        // The slip stores NET bonus folded into OtherAllowances, not the GROSS GOSI-base amount, so the
        // covered-wage bonus must be re-derived from the run's consumed bonuses. Keyed on the now-stamped
        // PayrollRunId (PayrollController:1123-1127) — reliable post-run, unlike the run-time
        // "Approved && PayrollRunId==null" query which is empty once the run has consumed them.
        var runBonuses = await _db.EmployeeBonuses.AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted && b.PayrollRunId == run.Id && b.EmployeeIntId != null)
            .ToListAsync(ct);
        var gosiBonusTypeIds = runBonuses.Select(b => b.BonusTypeId).Distinct().ToList();
        var gosiIncludedBonusTypes = gosiBonusTypeIds.Count > 0
            ? (await _db.BonusTypes.AsNoTracking()
                    .Where(t => gosiBonusTypeIds.Contains(t.Id) && t.IsIncludedInGosiBase)
                    .Select(t => t.Id)
                    .ToListAsync(ct))
                .ToHashSet()
            : new HashSet<Guid>();
        var gosiBonusByEmp = runBonuses
            .Where(b => gosiIncludedBonusTypes.Contains(b.BonusTypeId))
            .GroupBy(b => b.EmployeeIntId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.GrossBonusAmount));

        // Employees for nationality / contract type (the only run inputs not persisted on the slip).
        var slipEmpIds = slips.Select(s => s.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && slipEmpIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        // ── Resolve the pack; degrade gracefully (still report actual + GL) if it can't be resolved ──
        IStatutoryDeductionCalculator? calc = null;
        string packCc = company?.CountryCode ?? string.Empty;
        string packJur = company?.Jurisdiction ?? string.Empty;
        string? packNote = null;
        if (company is null)
            packNote = "Run company could not be resolved; expected GOSI cannot be recomputed. Actual and GL figures are still reported.";
        else if (string.IsNullOrWhiteSpace(packCc))
            packNote = $"Company '{company.LegalNameEn}' has no CountryCode; expected GOSI cannot be recomputed.";
        else
        {
            calc = _packResolver.ResolveDeductionCalculator(packCc, packJur);
            if (calc is DefaultStatutoryDeductionCalculator)
            {
                calc = null;
                packNote = $"No statutory pack registered for '{packCc}'/'{packJur}'; expected GOSI cannot be recomputed.";
            }
        }
        var packResolved = calc is not null;

        // ── Per-employee reconciliation rows ────────────────────────────────────────────────────
        var rows = new List<GosiReconciliationRow>(slips.Count);
        foreach (var slip in slips)
        {
            employees.TryGetValue(slip.EmployeeId, out var emp);
            var nationality = emp?.Nationality ?? string.Empty;

            var actualEe = actualEeByEmp.GetValueOrDefault(slip.EmployeeId);
            var actualEr = actualErByEmp.GetValueOrDefault(slip.EmployeeId);

            decimal expectedEe = 0m, expectedEr = 0m;
            IReadOnlyList<StatutoryDeductionLine> expectedLines = Array.Empty<StatutoryDeductionLine>();
            decimal coveredWageBase = 0m;

            if (calc is not null)
            {
                var gosiBonus = gosiBonusByEmp.GetValueOrDefault(slip.EmployeeId);
                // Byte-identical to PayrollController:879-886: GOSI bonus is folded into the housing slot
                // so SalaryBreakdown.GosiCoveredWage = Basic + (Housing + eligible bonus). basic/housing
                // come from the slip the run wrote (drift-proof). Transport/other do not affect the KSA/UAE
                // covered wage (Basic + Housing) — passed for completeness only.
                var breakdown = new SalaryBreakdown(
                    slip.BasicSalary,
                    slip.HousingAllowance + gosiBonus,
                    slip.TransportAllowance,
                    0m);
                var input = new StatutoryDeductionInput(
                    EmployeeId:   Guid.Empty,
                    CompanyId:    run.CompanyId ?? Guid.Empty,
                    Salary:       breakdown,
                    Nationality:  nationality,
                    ContractType: emp?.ContractType ?? "Indefinite",
                    PeriodYear:   run.Year,
                    PeriodMonth:  run.Month);
                var expected = await calc.CalculateAsync(input, ct);
                expectedEe = expected.TotalEmployeeDeduction;
                expectedEr = expected.TotalEmployerContribution;
                expectedLines = expected.Lines;
                // Pre-ceiling contributory base (basic + housing + eligible bonus). The statutory ceiling
                // is applied INSIDE the pack to the amounts; we report the base rather than duplicate the
                // pack's ceiling logic (which would risk drifting from it).
                coveredWageBase = breakdown.GosiCoveredWage;
            }

            rows.Add(new GosiReconciliationRow(
                EmployeeId:            slip.EmployeeId,
                EmployeeCode:          slip.EmployeeCode,
                EmployeeName:          slip.EmployeeName,
                // Display-only label (pure nationality classifier). The MONEY comes from the pack above;
                // this never feeds the expected amount, so it cannot reintroduce the two-engine divergence.
                Classification:        GosiCalculationService.DeriveClassification(nationality),
                CoveredWageBase:       coveredWageBase,
                ExpectedEmployee:      expectedEe,
                ExpectedEmployer:      expectedEr,
                ActualEmployee:        actualEe,
                ActualEmployer:        actualEr,
                SlipEmployeeStatutory: slip.EmployeeStatutoryTotal,
                SlipEmployerStatutory: slip.EmployerStatutoryTotal,
                ExpectedLines:         expectedLines));
        }

        // ── GL liability tie-out (requirement #3) — account-agnostic, matched by component code ──
        var glEntries = await _db.FinanceGlEntries.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.SourceModule == "Payroll" && e.SourceEntityId == run.Id)
            .ToListAsync(ct);
        var glPosted = glEntries.Count > 0;

        decimal glEe = 0m, glEr = 0m;
        string? glEeAccount = null, glErAccount = null;
        foreach (var e in glEntries)
        {
            if (!e.Description.StartsWith(DeductionDescriptionPrefix, StringComparison.Ordinal)) continue;
            var code = e.Description[DeductionDescriptionPrefix.Length..];
            if (!statutoryCodeIsEmployer.TryGetValue(code, out var isEmployer)) continue; // only statutory codes
            if (isEmployer) { glEr += e.Amount; glErAccount ??= e.CreditAccount; }
            else            { glEe += e.Amount; glEeAccount ??= e.CreditAccount; }
        }

        // ── Aggregates + named deltas ───────────────────────────────────────────────────────────
        var actualEeTotal = deductions.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount);
        var actualErTotal = deductions.Where(d => d.IsEmployerContribution).Sum(d => d.Amount);
        var expectedEeTotal = rows.Sum(r => r.ExpectedEmployee);
        var expectedErTotal = rows.Sum(r => r.ExpectedEmployer);
        var slipEeTotal = slips.Sum(s => s.EmployeeStatutoryTotal);
        var slipErTotal = slips.Sum(s => s.EmployerStatutoryTotal);

        return new GosiRunReconciliation(
            RunId:                run.Id,
            Period:               $"{run.Year}-{run.Month:D2}",
            CompanyId:            company?.Id ?? run.CompanyId,
            PackResolved:         packResolved,
            PackCountryCode:      packCc,
            PackJurisdiction:     packJur,
            PackStatusNote:       packNote,
            HasStatutoryData:     hasStatutoryData,
            GlPosted:             glPosted,
            ActualEmployeeTotal:  Math.Round(actualEeTotal, 2),
            ActualEmployerTotal:  Math.Round(actualErTotal, 2),
            ExpectedEmployeeTotal: Math.Round(expectedEeTotal, 2),
            ExpectedEmployerTotal: Math.Round(expectedErTotal, 2),
            SlipEmployeeStatutoryTotal: Math.Round(slipEeTotal, 2),
            SlipEmployerStatutoryTotal: Math.Round(slipErTotal, 2),
            RunEmployerStatutoryCost:   Math.Round(run.TotalEmployerStatutoryCost, 2),
            GlEmployeeLiability:  glPosted ? Math.Round(glEe, 2) : (decimal?)null,
            GlEmployerLiability:  glPosted ? Math.Round(glEr, 2) : (decimal?)null,
            GlEmployeeAccount:    glEeAccount,
            GlEmployerAccount:    glErAccount,
            ExpectedVsActualEmployeeDelta: Math.Round(expectedEeTotal - actualEeTotal, 2),
            ExpectedVsActualEmployerDelta: Math.Round(expectedErTotal - actualErTotal, 2),
            GlEmployeeDelta:      glPosted ? Math.Round(actualEeTotal - glEe, 2) : (decimal?)null,
            GlEmployerDelta:      glPosted ? Math.Round(actualErTotal - glEr, 2) : (decimal?)null,
            Deductions:           deductions,
            Rows:                 rows)
        {
            // POD-B2 — flag a period that holds more than one run. The per-run "expected" above is a
            // STANDALONE recomputation, but Process computes a sibling run's statutory INCREMENTALLY
            // (period-to-date base, ceiling applied once, siblings netted off). Below the ceiling both
            // agree exactly; at or above it they cannot, and reporting that as a silent variance would
            // send a compliance officer chasing a filing error that does not exist. Reproducing the delta
            // here is not possible either — it depends on the ORDER the period's runs were processed in,
            // which is not persisted — so the honest contract is: per-run expected is meaningful for a
            // single-run period, and ReconcilePeriodAsync is the tie-out that always holds.
            SiblingRunCount = siblingRunCount,
        };
    }

    /// <summary>
    /// POD-B2 (M8) — PERIOD-level statutory reconciliation across every non-voided run for a
    /// (company, year, month).
    ///
    /// POD-A1 established "deducted == report == GL". That invariant survives per RUN, but once B2 allows
    /// several runs in one month it no longer survives per PERIOD, and a period is what gets FILED: the
    /// GOSI submission for 2026-08 would otherwise be whichever run the preparer happened to open.
    ///
    /// ACTUAL is the union of every run's persisted statutory deduction rows — the filing figure, correct
    /// by construction. EXPECTED is recomputed ONCE per employee on the PERIOD-AGGREGATED covered wage
    /// (Σ slip basic, Σ slip housing + Σ GOSI-eligible bonus over all the period's runs), so the statutory
    /// CEILING — a period concept — is applied to the period total exactly once. That matches what
    /// PayrollController.Process's incremental statutory base produces, so expected == actual holds across
    /// a Regular + OffCycle pair the same way it holds for a single run.
    /// </summary>
    public async Task<GosiPeriodReconciliation> ReconcilePeriodAsync(
        Guid tenantId, Guid? companyId, int year, int month, CancellationToken ct)
    {
        var period = $"{year}-{month:D2}";
        var runs = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Year == year && r.Month == month && r.Status != "Voided"
                     && (companyId == null || r.CompanyId == companyId || r.CompanyId == null))
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);

        var runReconciliations = new List<GosiRunReconciliation>(runs.Count);
        foreach (var r in runs)
            runReconciliations.Add(await ReconcileAsync(tenantId, r, ct));

        var runIds = runs.Select(r => r.Id).ToList();
        var allDeductions = runReconciliations.SelectMany(x => x.Deductions).ToList();

        var actualEeByEmp = allDeductions.Where(d => !d.IsEmployerContribution)
            .GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        var actualErByEmp = allDeductions.Where(d => d.IsEmployerContribution)
            .GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

        // ── Period-aggregated covered wage, per employee ─────────────────────────────────────────
        var slips = runIds.Count == 0 ? new List<PayrollSlip>() : await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && runIds.Contains(s.RunId))
            .ToListAsync(ct);
        var periodBonuses = runIds.Count == 0 ? new List<EmployeeBonus>() : await _db.EmployeeBonuses.AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted && b.PayrollRunId != null
                     && runIds.Contains(b.PayrollRunId!.Value) && b.EmployeeIntId != null)
            .ToListAsync(ct);
        var periodBonusTypeIds = periodBonuses.Select(b => b.BonusTypeId).Distinct().ToList();
        var periodGosiTypeIds = periodBonusTypeIds.Count > 0
            ? (await _db.BonusTypes.AsNoTracking()
                .Where(t => periodBonusTypeIds.Contains(t.Id) && t.IsIncludedInGosiBase)
                .Select(t => t.Id).ToListAsync(ct)).ToHashSet()
            : new HashSet<Guid>();
        var periodGosiBonusByEmp = periodBonuses
            .Where(b => periodGosiTypeIds.Contains(b.BonusTypeId))
            .GroupBy(b => b.EmployeeIntId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.GrossBonusAmount));

        // Resolve the pack once from the period's company (same rule as ReconcileAsync).
        var activeCompanies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
        var company = companyId.HasValue
            ? activeCompanies.FirstOrDefault(c => c.Id == companyId.Value)
            : runs.Select(r => r.CompanyId).FirstOrDefault(cid => cid.HasValue) is Guid rc
                ? activeCompanies.FirstOrDefault(c => c.Id == rc)
                : activeCompanies.Count == 1 ? activeCompanies[0] : null;
        IStatutoryDeductionCalculator? calc = null;
        string? packNote = null;
        if (company is null)
            packNote = "No legal entity could be resolved for this period; expected GOSI cannot be recomputed. Actual and GL figures are still reported.";
        else if (string.IsNullOrWhiteSpace(company.CountryCode))
            packNote = $"Company '{company.LegalNameEn}' has no CountryCode; expected GOSI cannot be recomputed.";
        else
        {
            calc = _packResolver.ResolveDeductionCalculator(company.CountryCode, company.Jurisdiction ?? string.Empty);
            if (calc is DefaultStatutoryDeductionCalculator) { calc = null; packNote = $"No statutory pack registered for '{company.CountryCode}'/'{company.Jurisdiction}'."; }
        }

        var empIds = slips.Select(s => s.EmployeeId).Distinct().ToList();
        var employees = empIds.Count == 0 ? new Dictionary<int, Employee>() : await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && empIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        var rows = new List<GosiPeriodRow>(empIds.Count);
        foreach (var empId in empIds.OrderBy(x => x))
        {
            var empSlips = slips.Where(s => s.EmployeeId == empId).ToList();
            var basic    = empSlips.Sum(s => s.BasicSalary);
            var housing  = empSlips.Sum(s => s.HousingAllowance) + periodGosiBonusByEmp.GetValueOrDefault(empId);
            var transport = empSlips.Sum(s => s.TransportAllowance);
            employees.TryGetValue(empId, out var emp);

            decimal expectedEe = 0m, expectedEr = 0m, coveredWage = 0m;
            if (calc is not null)
            {
                var breakdown = new SalaryBreakdown(basic, housing, transport, 0m);
                var expected = await calc.CalculateAsync(new StatutoryDeductionInput(
                    EmployeeId: Guid.Empty, CompanyId: company!.Id, Salary: breakdown,
                    Nationality: emp?.Nationality ?? string.Empty,
                    ContractType: emp?.ContractType ?? "Indefinite",
                    PeriodYear: year, PeriodMonth: month), ct);
                expectedEe  = expected.TotalEmployeeDeduction;
                expectedEr  = expected.TotalEmployerContribution;
                coveredWage = breakdown.GosiCoveredWage;
            }

            var first = empSlips[0];
            rows.Add(new GosiPeriodRow(
                empId, first.EmployeeCode, first.EmployeeName,
                GosiCalculationService.DeriveClassification(emp?.Nationality ?? string.Empty),
                coveredWage, expectedEe, expectedEr,
                actualEeByEmp.GetValueOrDefault(empId), actualErByEmp.GetValueOrDefault(empId),
                empSlips.Select(s => s.RunId).Distinct().Count()));
        }

        var actualEeTotal = allDeductions.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount);
        var actualErTotal = allDeductions.Where(d => d.IsEmployerContribution).Sum(d => d.Amount);
        var glEe = runReconciliations.Sum(x => x.GlEmployeeLiability ?? 0m);
        var glEr = runReconciliations.Sum(x => x.GlEmployerLiability ?? 0m);
        var anyGlPosted = runReconciliations.Any(x => x.GlPosted);

        return new GosiPeriodReconciliation(
            Period:                period,
            CompanyId:             company?.Id ?? companyId,
            RunCount:              runs.Count,
            RunIds:                runIds,
            PackResolved:          calc is not null,
            PackStatusNote:        packNote,
            HasStatutoryData:      allDeductions.Count > 0,
            GlPosted:              anyGlPosted,
            ActualEmployeeTotal:   Math.Round(actualEeTotal, 2),
            ActualEmployerTotal:   Math.Round(actualErTotal, 2),
            ExpectedEmployeeTotal: Math.Round(rows.Sum(r => r.ExpectedEmployee), 2),
            ExpectedEmployerTotal: Math.Round(rows.Sum(r => r.ExpectedEmployer), 2),
            GlEmployeeLiability:   anyGlPosted ? Math.Round(glEe, 2) : (decimal?)null,
            GlEmployerLiability:   anyGlPosted ? Math.Round(glEr, 2) : (decimal?)null,
            Deductions:            allDeductions,
            Rows:                  rows,
            RunSummaries:          runs.Select((r, i) => new GosiPeriodRunSummary(
                                        r.Id, r.RunType, r.Status, r.IncludesRecurringPay,
                                        Math.Round(runReconciliations[i].ActualEmployeeTotal, 2),
                                        Math.Round(runReconciliations[i].ActualEmployerTotal, 2))).ToList());
    }
}

// ── Result types ────────────────────────────────────────────────────────────────

/// <summary>One employee's GOSI reconciliation for a run: expected (recomputed via the run's pack) vs
/// actual (persisted deductions) vs the slip witnesses.</summary>
public sealed record GosiReconciliationRow(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Classification,
    decimal CoveredWageBase,
    decimal ExpectedEmployee,
    decimal ExpectedEmployer,
    decimal ActualEmployee,
    decimal ActualEmployer,
    decimal SlipEmployeeStatutory,
    decimal SlipEmployerStatutory,
    IReadOnlyList<StatutoryDeductionLine> ExpectedLines)
{
    public decimal EmployeeVariance => Math.Round(ActualEmployee - ExpectedEmployee, 2);
    public decimal EmployerVariance => Math.Round(ActualEmployer - ExpectedEmployer, 2);
    public bool HasVariance => Math.Abs(EmployeeVariance) > 0.01m || Math.Abs(EmployerVariance) > 0.01m;
}

/// <summary>Run-level GOSI reconciliation: actual vs expected vs GL, with every delta surfaced as a
/// named field. Callers (contribution-summary, variance-report, the compliance dashboard) project the
/// slice they need; the numbers are computed once here from a single engine + base.</summary>
public sealed record GosiRunReconciliation(
    Guid RunId,
    string Period,
    Guid? CompanyId,
    bool PackResolved,
    string PackCountryCode,
    string PackJurisdiction,
    string? PackStatusNote,
    bool HasStatutoryData,
    bool GlPosted,
    decimal ActualEmployeeTotal,
    decimal ActualEmployerTotal,
    decimal ExpectedEmployeeTotal,
    decimal ExpectedEmployerTotal,
    decimal SlipEmployeeStatutoryTotal,
    decimal SlipEmployerStatutoryTotal,
    decimal RunEmployerStatutoryCost,
    decimal? GlEmployeeLiability,
    decimal? GlEmployerLiability,
    string? GlEmployeeAccount,
    string? GlEmployerAccount,
    decimal ExpectedVsActualEmployeeDelta,
    decimal ExpectedVsActualEmployerDelta,
    decimal? GlEmployeeDelta,
    decimal? GlEmployerDelta,
    IReadOnlyList<PayrollDeduction> Deductions,
    IReadOnlyList<GosiReconciliationRow> Rows)
{
    /// <summary>Convenience: employees whose recomputed-expected diverges from the persisted actual.</summary>
    public int VarianceCount => Rows.Count(r => r.HasVariance);

    /// <summary>POD-B2 — other non-voided runs in this run's (company, year, month).</summary>
    public int SiblingRunCount { get; init; }

    /// <summary>
    /// POD-B2 — true when this run shares its period with another run, which makes the per-run EXPECTED
    /// figure above a standalone recomputation while the run's ACTUAL was computed as a period DELTA
    /// (PayrollController.Process nets the statutory ceiling across the period's runs). Below the ceiling
    /// the two still agree exactly; at or above it they cannot, because "this run's share of a capped
    /// period total" is not a standalone quantity. A variance reported here is therefore not evidence of
    /// a defect — <see cref="GosiPeriodReconciliation"/> is the tie-out that holds, and is what a filing
    /// must be built from.
    /// </summary>
    public bool ExpectedIsPeriodPartial => SiblingRunCount > 0;

    /// <summary>POD-B2 — human-readable form of <see cref="ExpectedIsPeriodPartial"/>; null when the run
    /// is the period's only run, i.e. for every run in every tenant before B2.</summary>
    public string? PeriodScopeNote => ExpectedIsPeriodPartial
        ? $"{SiblingRunCount} other non-voided run(s) exist for {Period}. This run's statutory amounts were " +
          "computed as a PERIOD DELTA (the covered-wage ceiling is applied once across the period), so the " +
          "per-run 'expected' recomputation here is standalone and may differ near the ceiling. Use " +
          $"GET /api/gosi/periods/{Period[..4]}/{Period[5..].TrimStart('0')}/contribution-summary as the " +
          "filing source and the authoritative tie-out."
        : null;
}

// ── POD-B2 (M8): period-level result types ──────────────────────────────────────

/// <summary>One employee's PERIOD statutory position: expected on the period-aggregated covered wage
/// (ceiling applied once) vs the union of what every run in the period actually deducted.</summary>
public sealed record GosiPeriodRow(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Classification,
    decimal CoveredWageBase,
    decimal ExpectedEmployee,
    decimal ExpectedEmployer,
    decimal ActualEmployee,
    decimal ActualEmployer,
    int RunCount)
{
    public decimal EmployeeVariance => Math.Round(ActualEmployee - ExpectedEmployee, 2);
    public decimal EmployerVariance => Math.Round(ActualEmployer - ExpectedEmployer, 2);
    public bool HasVariance => Math.Abs(EmployeeVariance) > 0.01m || Math.Abs(EmployerVariance) > 0.01m;
}

/// <summary>Per-run contribution to the period total, so a preparer can see which run carried what.</summary>
public sealed record GosiPeriodRunSummary(
    Guid RunId, string RunType, string Status, bool IncludesRecurringPay,
    decimal EmployeeTotal, decimal EmployerTotal);

/// <summary>
/// POD-B2 — the FILING view: every non-voided run for a (company, year, month) unioned, with expected
/// recomputed once at period scope. This is the source the statutory submission should be built from;
/// the per-run contribution-summary covers only its own run and is partial once siblings exist.
/// </summary>
public sealed record GosiPeriodReconciliation(
    string Period,
    Guid? CompanyId,
    int RunCount,
    IReadOnlyList<Guid> RunIds,
    bool PackResolved,
    string? PackStatusNote,
    bool HasStatutoryData,
    bool GlPosted,
    decimal ActualEmployeeTotal,
    decimal ActualEmployerTotal,
    decimal ExpectedEmployeeTotal,
    decimal ExpectedEmployerTotal,
    decimal? GlEmployeeLiability,
    decimal? GlEmployerLiability,
    IReadOnlyList<PayrollDeduction> Deductions,
    IReadOnlyList<GosiPeriodRow> Rows,
    IReadOnlyList<GosiPeriodRunSummary> RunSummaries)
{
    public decimal ExpectedVsActualEmployeeDelta => Math.Round(ActualEmployeeTotal - ExpectedEmployeeTotal, 2);
    public decimal ExpectedVsActualEmployerDelta => Math.Round(ActualEmployerTotal - ExpectedEmployerTotal, 2);
    public decimal? GlEmployeeDelta => GlPosted ? Math.Round(ActualEmployeeTotal - (GlEmployeeLiability ?? 0m), 2) : null;
    public decimal? GlEmployerDelta => GlPosted ? Math.Round(ActualEmployerTotal - (GlEmployerLiability ?? 0m), 2) : null;
    public int VarianceCount => Rows.Count(r => r.HasVariance);
}
