using Zayra.Api.Application.CountryPack;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Centralised payroll validation engine. Runs all 9 compliance rules against a
/// processed payroll run and returns <see cref="PayrollValidationResult"/> rows.
///
/// Severity = "Error"   → blocks Approve and Lock (enforced server-side).
/// Severity = "Warning" → informational; workflow may proceed with acknowledgment.
///
/// Rules:
///   1  Missing salary structure OR missing payroll profile for any active employee
///   2  GOSI: Saudi/GCC employee must have non-zero GOSI employee deductions;
///            expat must have zero; 45,000 SAR covered-wage ceiling flagged;
///            Saudi national's GOSI entrant cohort: unknown → Warning, new entrant → Error (F02)
///   3  Net salary not negative; not zero when gross > 0
///   4  Duplicate employee entry in run
///   5  WPS readiness: IBAN present + valid Saudi format (24 characters total) and mod-97;
///            MOL ID present on payroll profile for KSA runs
///   6  Nationality present on employee record (drives GOSI branch)
///   7  Run-level totals reconcile: Σ(gross), Σ(deductions), Σ(net) match header
///   8  GL pre-check: TotalGross = TotalDeductions + TotalNet (journal will balance)
///   9  Salary currency matches company default currency
/// </summary>
public static class PayrollValidationEngine
{
    /// <summary>
    /// S1/A2(c) — the LAST-RESORT default only. The ceiling was a compiled constant checked against a
    /// tenant-overridable statutory rule: when GOSI moves the ceiling, the rule updates and a compiled
    /// constant keeps warning on the old number forever. Callers now pass the resolved
    /// <c>gosi.covered_wage_ceiling_sar</c> through <see cref="PayrollValidationContext"/>, so there is
    /// one ceiling in the system rather than a third store of it.
    /// </summary>
    private const decimal DefaultGosiCoveredWageCeiling = 45_000m;

    /// <summary>Rule 5c — the employee is paid by bank with imported bank details nobody has confirmed (Warning).</summary>
    public const string ImportedBankDetailsUnverified = "IMPORTED_BANK_DETAILS_UNVERIFIED";

    /// <summary>F02 — a Saudi national's slip was computed with no known GOSI cohort (Warning).</summary>
    public const string GosiCohortNotRecorded = "GOSI_COHORT_NOT_RECORDED";

    /// <summary>F02 — a Saudi national is a post-3-July-2024 new entrant and that schedule is not modelled
    /// (Error; published as non-overridable in <see cref="PayrollValidationOverridePolicy"/>).</summary>
    public const string GosiNewEntrantScheduleNotModelled = "GOSI_NEW_ENTRANT_SCHEDULE_NOT_MODELLED";
    private const int GosiRateStalenessThresholdMonths = 18;

    public static List<PayrollValidationResult> Run(PayrollValidationContext ctx)
    {
        var results = new List<PayrollValidationResult>();
        var tid = ctx.Run.TenantId;
        var rid = ctx.Run.Id;
        var now = DateTime.UtcNow;

        void Err(string code, string message, int? empId = null) =>
            results.Add(new PayrollValidationResult
            {
                TenantId = tid, PayrollRunId = rid, EmployeeId = empId,
                Severity = "Error", Code = code, Message = message, CreatedAtUtc = now,
            });

        void Warn(string code, string message, int? empId = null) =>
            results.Add(new PayrollValidationResult
            {
                TenantId = tid, PayrollRunId = rid, EmployeeId = empId,
                Severity = "Warning", Code = code, Message = message, CreatedAtUtc = now,
            });

        // ── Run-level company/pack guards (must precede per-slip rules) ───────────
        // These mirror the fail-loud abort in Process(); a second pass here catches
        // edge cases where the run was created before the guard was added.
        if (ctx.Company is null)
            Err("COMPANY_NOT_RESOLVED",
                "No active company is linked to this payroll run, so statutory deductions cannot be worked out. " +
                "Link an active company with a country set in Setup → Companies, then reprocess the run.");
        else if (string.IsNullOrWhiteSpace(ctx.Company.CountryCode))
            Err("COUNTRY_CODE_MISSING",
                $"{ctx.Company.LegalNameEn} has no country set, so statutory deductions cannot be worked out. " +
                "Set it in Setup → Companies, then reprocess the run.");

        // Accept both ISO 3166-1 alpha-2 ("SA") and alpha-3 ("SAU") for KSA — data may use either.
        var isKsa = string.Equals(ctx.Company?.CountryCode, "SAU", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(ctx.Company?.CountryCode, "SA",  StringComparison.OrdinalIgnoreCase);
        var companyCurrency = ctx.Company?.DefaultCurrency ?? "SAR";

        // ── WARN_GOSI_RATES_REQUIRE_SIGNOFF ─────────────────────────────────
        // Fire at the start of every validation for KSA runs when the GOSI rate
        // effective date is older than the staleness threshold.
        if (isKsa && ctx.GosiRatesEffectiveFrom.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var ageMonths = (today.Year - ctx.GosiRatesEffectiveFrom.Value.Year) * 12
                          + (today.Month - ctx.GosiRatesEffectiveFrom.Value.Month);
            if (ageMonths >= GosiRateStalenessThresholdMonths)
                Warn("WARN_GOSI_RATES_REQUIRE_SIGNOFF",
                    $"GOSI contribution rates in use (effective from {ctx.GosiRatesEffectiveFrom.Value:yyyy-MM-dd}) have not been confirmed against " +
                    $"current GOSI circulars. Ensure rates are reviewed by a Saudi compliance officer before " +
                    $"locking this payroll run.");
        }

        // ── Indexes ───────────────────────────────────────────────────────────
        var salaryByEmp  = ctx.SalaryAssignments.GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key);
        var profileByEmp = ctx.Profiles.ToDictionary(p => p.EmployeeId);
        var empById      = ctx.ActiveEmployees.ToDictionary(e => e.Id);

        // Fill in employees that appear in slips but may be inactive/deleted (edge-case guard).
        foreach (var s in ctx.Slips)
        {
            empById.TryAdd(s.EmployeeId,
                new Employee { Id = s.EmployeeId, EmployeeCode = s.EmployeeCode, FullName = s.EmployeeName ?? string.Empty });
        }

        // POD-B2 — does this run pay recurring salary? Drives which rules are applicable. A supplemental
        // run (bonus-only / adjustment-only) pays no recurring component, so rules whose premise is
        // "this run pays the monthly wage" are demoted to Warning rather than blocking Approve/Lock.
        // For a Regular run IncludesRecurringPay is always true, so nothing about an existing run changes.
        var paysRecurring = ctx.Run.IncludesRecurringPay;

        // ── F02: the entrant cohort is judged PER EMPLOYEE, inside Rule 2 below ─────────────────────
        // This used to be ONE run-wide WARN_GOSI_ENTRANT_COHORT_NOT_MODELLED that assumed every insured
        // person was on the pre-3-July-2024 schedule, named nobody, blocked nothing, and could be silenced
        // for the whole population by one tenant flag (gosi.new_entrant_scheme_acknowledged, no longer
        // read). The cohort is now a person-level fact (Employee.GosiFirstRegisteredOn), each slip records
        // the cohort it was computed on (PayrollSlip.GosiCohort), and Rule 2 raises a finding per person.

        // ── Rule 1: Missing salary structure / payroll profile ────────────────
        foreach (var emp in ctx.ActiveEmployees)
        {
            if (!salaryByEmp.ContainsKey(emp.Id))
            {
                var missingStructureMsg =
                    $"Employee {emp.EmployeeCode} ({emp.FullName}) has no active salary structure. " +
                    "All active employees in the run must have a salary assignment before payroll is processed.";
                // POD-B2: on a supplemental-basis run no recurring pay is derived from the structure, so a
                // lapsed assignment cannot produce a wrong figure — it must not brick a bonus-only run.
                if (paysRecurring)
                    Err("MISSING_SALARY_STRUCTURE", missingStructureMsg, emp.Id);
                else
                    Warn("MISSING_SALARY_STRUCTURE",
                        missingStructureMsg + " (Advisory only: this run pays supplemental items, not recurring salary.)",
                        emp.Id);
            }

            if (!profileByEmp.ContainsKey(emp.Id))
                Warn("MISSING_PAYROLL_PROFILE",
                    $"Employee {emp.EmployeeCode} ({emp.FullName}) has no payroll profile. " +
                    "Bank details and payment settings are required for disbursement.",
                    emp.Id);
        }

        // ── Rule 4: Duplicate employee in run ────────────────────────────────
        var seen = new HashSet<int>();
        foreach (var slip in ctx.Slips)
        {
            if (!seen.Add(slip.EmployeeId))
                Err("DUPLICATE_EMPLOYEE",
                    $"Employee {slip.EmployeeCode} appears more than once in this payroll run. " +
                    "Each employee must have exactly one payslip per run.",
                    slip.EmployeeId);
        }

        // ── GOSI EE deduction indexes (for Rule 2) ───────────────────────────
        // Employee-side GOSI codes end with "-EE" (e.g. GOSI-ANN-EE, GOSI-SANED-EE).
        var gosiEeByEmp = ctx.Deductions
            .Where(d => d.Source == "Statutory" && IsGosiEeCode(d.ComponentCode))
            .GroupBy(d => d.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

        // ── POD-C3-FIX: the 1420 recovery each slip carries ───────────────────────
        // Needed by Rule 3 below to tell an OVER-DEDUCTION apart from a NON-DUPLICATION OF PAYMENT.
        // See the carve-out there for why the difference is not cosmetic.
        var recoveryByEmp = ctx.Deductions
            .Where(d => d.Source == PayrollRecoveryComponents.RecoverySource
                     || d.ComponentCode == PayrollRecoveryComponents.ReceivableRecovery)
            .GroupBy(d => d.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

        // ── Per-slip rules ────────────────────────────────────────────────────
        foreach (var slip in ctx.Slips.GroupBy(s => s.EmployeeId).Select(g => g.First()))
        {
            empById.TryGetValue(slip.EmployeeId, out var emp);
            profileByEmp.TryGetValue(slip.EmployeeId, out var profile);

            // Rule 3: net not negative; not zero when gross > 0
            if (slip.NetSalary < 0m)
                Err("NEGATIVE_NET",
                    $"Employee {slip.EmployeeCode} net salary is negative ({slip.NetSalary:N2}). " +
                    "Deductions exceed gross pay; deduction amounts must be reviewed.",
                    slip.EmployeeId);

            // POD-B2 (M4): on a supplemental run a small bonus fully consumed by withholding tax + GOSI
            // legitimately nets to zero. Gross == deductions still satisfies Rule 8, so the journal
            // balances — blocking it as an Error would leave an unlockable, undeletable run whose only
            // exit is Void. Demoted to Warning for supplemental basis ONLY; unchanged for Regular runs.
            // A genuinely NEGATIVE raw net is refused earlier, at Process, with 422 negative_net_unsupported.
            var slipRecovery = recoveryByEmp.GetValueOrDefault(slip.EmployeeId);

            if (slip.NetSalary == 0m && slip.GrossSalary > 0m && !paysRecurring)
                Warn("ZERO_NET_WITH_GROSS",
                    $"Employee {slip.EmployeeCode} net salary is zero but gross is {slip.GrossSalary:N2}. " +
                    "On a supplemental run this normally means the supplemental amount was fully consumed by " +
                    "withholding tax and statutory contributions. Verify the deduction amounts.",
                    slip.EmployeeId);
            // ── POD-C3-FIX: zero net EXPLAINED BY A 1420 RECOVERY is not an over-deduction ────────
            // The B3→C3 handoff: a run voided with settlementDisposition=FundsDisbursed leaves a
            // per-employee 1420 Employee Overpayment Receivable equal to the net ALREADY IN THE
            // EMPLOYEE'S BANK ACCOUNT. The replacement run re-pays the SAME period, so when its net
            // equals what was disbursed the correct answer IS zero: the employee has already been paid
            // once, the expense is re-recognised, and 1420 is relieved. That is the ORDINARY correction
            // ("wrong cost centre / wrong attendance code, same salary"), not an edge case.
            //
            // Raising the non-overridable ZERO_NET_WITH_GROSS Error there dead-ended the entire
            // handoff: Approve 422s, Lock refuses, the only exit is to void the replacement — which
            // RESTORES the receivable. A loop, with 1420 never relieved. The Error is diagnosing an
            // "over-deduction" that did not happen; a recovery is a NON-DUPLICATION OF PAYMENT, not a
            // deduction from wages.
            //
            // Deliberately a DISTINCT code, not a demotion of ZERO_NET_WITH_GROSS: the genuine
            // over-deduction case on a recurring run keeps its non-overridable Error verbatim (an
            // LOP-exceeds-salary month still blocks, PayrollOvertimeLopTests.cs:147). The carve-out is
            // gated on the recovery ACCOUNTING FOR THE WHOLE GAP — a slip that is zero for any other
            // reason, with a recovery merely also present, still raises the Error. The guard is that the
            // slip's arithmetic is EXACT — gross − deductions == net, with nothing swallowed by the
            // net-cannot-go-negative clamp (PayrollController.cs:2243-2267). A slip whose deductions
            // genuinely exceed its gross reads 0 only because it was clamped, and that shortfall is a real
            // over-deduction the recovery does not explain, so the Error below still fires.
            else if (slip.NetSalary == 0m && slip.GrossSalary > 0m
                     && slipRecovery > 0m && slip.GrossSalary - slip.Deductions == slip.NetSalary)
                Warn("ZERO_NET_FROM_RECEIVABLE_RECOVERY",
                    $"Employee {slip.EmployeeCode} nets zero because {slipRecovery:N2} of a prior voided run's " +
                    $"ALREADY-DISBURSED net pay was recovered on this run (gross {slip.GrossSalary:N2}). No cash is " +
                    "due: they were paid once, this run re-recognises the expense and relieves the 1420 Employee " +
                    "Overpayment Receivable. Nothing is owed and nothing is forgiven — any un-recovered remainder " +
                    "is reported separately as WARN_RECEIVABLE_RESIDUAL and still ages on " +
                    "GET /api/payroll/receivables. A zero-net employee has no payable line: exclude them from the " +
                    "WPS file (WpsSifValidator blocks a non-positive net by design).",
                    slip.EmployeeId);
            else if (slip.NetSalary == 0m && slip.GrossSalary > 0m)
                Err("ZERO_NET_WITH_GROSS",
                    $"Employee {slip.EmployeeCode} net salary is zero but gross is {slip.GrossSalary:N2}. " +
                    "This usually indicates an over-deduction; verify deduction amounts.",
                    slip.EmployeeId);

            // Rule 6: nationality present
            if (emp is not null && string.IsNullOrWhiteSpace(emp.Nationality))
                Warn("MISSING_NATIONALITY",
                    $"Employee {slip.EmployeeCode} has no nationality recorded. " +
                    "Nationality drives GOSI classification (Saudi vs GCC vs expat) and must be set before processing.",
                    slip.EmployeeId);

            // Rule 2: GOSI rate check — KSA runs only
            if (isKsa && emp is not null)
            {
                var classification = GosiCalculationService.DeriveClassification(emp.Nationality);
                var gosiEeAmount   = gosiEeByEmp.TryGetValue(slip.EmployeeId, out var g) ? g : 0m;
                var hasGosiEe      = gosiEeAmount > 0m;

                if (classification is "Saudi" or "GCC")
                {
                    // POD-B2 — GOSI is a PERIOD obligation, not a per-run one. Once a period may hold
                    // several runs, "THIS run deducted zero" stops being evidence of non-compliance:
                    //
                    //   • a supplemental run whose earnings sit outside the GOSI base has no covered wage
                    //     to contribute on at all — Process zeroes basic/housing for that basis
                    //     (PayrollController, the SUPPLEMENTAL BASIS block), so the pack is fed a zero
                    //     base and correctly returns zero; and
                    //   • the incremental statutory basis (M8) nets off what sibling runs already
                    //     deducted, so whichever run arrives after the 45 k ceiling is fully consumed
                    //     legitimately nets to zero. That can be the REGULAR run — a >45 k GOSI-base
                    //     bonus paid off-cycle first consumes the whole period ceiling.
                    //
                    // Raising an Error in either case STRANDS the run: Approve and Lock 422 on it,
                    // re-Process is refused once the run left Draft/Processed, DeleteRun is Draft-only,
                    // and nothing in this codebase ever sets PayrollValidationResult.IsResolved — so Void
                    // would be the only exit from a payroll that is in fact perfectly correct.
                    //
                    // The Error premise is therefore narrowed to what it always meant: this employee
                    // contributed NOTHING to GOSI for the whole PERIOD, on a run that pays the monthly
                    // wage. For every pre-B2 run PriorPeriodGosiEeByEmployee is empty and this run pays
                    // recurring, so periodGosiEe == gosiEeAmount and the rule is byte-identical.
                    var priorGosiEe  = ctx.PriorPeriodGosiEeByEmployee.TryGetValue(slip.EmployeeId, out var pg) ? pg : 0m;
                    var periodGosiEe = gosiEeAmount + priorGosiEe;

                    // ── S1/A4: a GCC national is a DIFFERENT failure with a DIFFERENT exit ─────────
                    // GOSI_MISSING_FOR_SAUDI told the preparer to make the employee contribute to
                    // GOSI-ANN-EE and GOSI-SANED-EE — contributions the KSA calculator structurally
                    // could not produce for a GCC national, because those are the SAUDI branches and
                    // a GCC national is insured under their HOME state's scheme. The instruction was
                    // impossible to follow, so the run was stranded: Approve and Lock 422, re-Process
                    // is refused once the run leaves Draft/Processed, and nothing ever sets IsResolved.
                    // The customer's only exits were to void the run or to override and file short.
                    //
                    // It is still an Error, and deliberately so — under-contributing for a GCC national
                    // accrues back-contributions with a monthly surcharge and costs the establishment
                    // its GOSI compliance certificate, which gates Qiwa services and visa issuance. But
                    // it now names the two rows that make it go away.
                    var gccHome = GosiCalculationService.DeriveGccHomeState(emp.Nationality);
                    if (!hasGosiEe && periodGosiEe <= 0m && paysRecurring && gccHome is not null)
                        Err("GOSI_GCC_SCHEME_NOT_CONFIGURED",
                            $"Employee {slip.EmployeeCode} is a {gccHome} national working in Saudi Arabia, and no " +
                            "contribution was calculated for them. Under the GCC Unified Insurance Extension Scheme " +
                            $"they are insured under {gccHome}'s own scheme, at {gccHome}'s rates, collected by GOSI — " +
                            "NOT under the Saudi Annuities/SANED branches, and NOT as an expatriate on occupational " +
                            "hazard alone, which is what this product used to do silently. This product does not ship " +
                            $"{gccHome} rates. To clear this and complete the run, seed two effective-dated statutory " +
                            $"rules for SAU / KSA-mainland from the current {gccHome} circular: " +
                            $"'gosi.gcc.{gccHome}.employee_rate' and 'gosi.gcc.{gccHome}.employer_rate' (decimal " +
                            "fractions, e.g. 0.07). The employer share is automatically capped at the Saudi employer " +
                            "rate and the excess charged to the employee, per the scheme. [COUNSEL] confirm the " +
                            $"{gccHome} branch rates and which branches the extension scheme covers before filing.",
                            slip.EmployeeId);
                    else if (!hasGosiEe && periodGosiEe <= 0m && paysRecurring)
                        Err("GOSI_MISSING_FOR_SAUDI",
                            $"Employee {slip.EmployeeCode} is classified as {classification} but has zero GOSI employee deductions. " +
                            "Saudi and GCC nationals must contribute to GOSI Annuities (GOSI-ANN-EE) and SANED (GOSI-SANED-EE).",
                            slip.EmployeeId);
                    else if (!hasGosiEe && periodGosiEe <= 0m)
                        Warn("GOSI_MISSING_FOR_SAUDI",
                            $"Employee {slip.EmployeeCode} is classified as {classification} and no GOSI employee " +
                            $"contribution has been deducted anywhere in {ctx.Run.Year}-{ctx.Run.Month:D2}. " +
                            "This run pays supplemental items only, so it has no covered wage of its own to " +
                            "contribute on — but the period's regular run must still deduct GOSI Annuities " +
                            "(GOSI-ANN-EE) and SANED (GOSI-SANED-EE) for this employee. Advisory on a " +
                            "supplemental run; it will block the regular run for the period until resolved.",
                            slip.EmployeeId);
                    // periodGosiEe > 0 with zero on THIS run needs no result: the period obligation is
                    // demonstrably met, and SUPPLEMENTAL_STATUTORY_BASE already tells the preparer the
                    // per-run figure is a period delta.

                    // ── F02: the GOSI entrant cohort, per employee ────────────────────────────────────
                    // Saudi nationals only: the cohort split is an annuities fact, and a GCC national is
                    // insured under their home state's scheme (above), not the Saudi branches. Judged on the
                    // cohort the slip was COMPUTED on — not the employee's current record — so a date
                    // recorded after Process is honoured by re-processing, never by re-validating a slip
                    // whose figures predate it. A run that computed no GOSI for this person (a supplemental
                    // run outside the GOSI base) has no rate to be wrong about.
                    if (classification == GosiClassifications.Saudi && (paysRecurring || hasGosiEe))
                    {
                        var recordedOn = emp.GosiFirstRegisteredOn;
                        if (slip.GosiCohort == GosiCohorts.NewEntrant)
                            Err(GosiNewEntrantScheduleNotModelled,
                                $"Employee {slip.EmployeeCode} ({emp.FullName}) first registered with GOSI on " +
                                $"{(recordedOn is DateOnly d ? IsoDate(d) : "a date on or after 3 July 2024")}, " +
                                "so they are a NEW ENTRANT on the separate new-entrant contribution schedule that " +
                                "applies from 3 July 2024. That schedule is not modelled in this product yet, so the " +
                                "GOSI on this payslip was computed on the pre-3-July-2024 schedule and is wrong for " +
                                "this person. This blocks approval and locking, and cannot be overridden. Next " +
                                "action: if the first-registration date is wrong, correct it on the employee record " +
                                "(the change needs approval), then reopen this run and process it again; if it is " +
                                "right, reopen the run, exclude this employee, process it again, and calculate their " +
                                "GOSI outside the system until the new-entrant schedule is modelled.",
                                slip.EmployeeId);
                        else if (slip.GosiCohort != GosiCohorts.PreJuly2024)
                            Warn(GosiCohortNotRecorded,
                                recordedOn is DateOnly since
                                    ? $"Employee {slip.EmployeeCode} ({emp.FullName}): GOSI cohort not recorded, " +
                                      "contribution basis unverified. This payslip was computed before the GOSI " +
                                      $"first-registration date ({IsoDate(since)}) was on record, so its GOSI used " +
                                      "the pre-3-July-2024 schedule without checking that it applies. Next action: " +
                                      "reopen this run and process it again (re-process), so the contribution is " +
                                      "computed on the recorded cohort."
                                    : $"Employee {slip.EmployeeCode} ({emp.FullName}): GOSI cohort not recorded, " +
                                      "contribution basis unverified. There is no GOSI first-registration date on the " +
                                      "employee record, so this payslip's GOSI was computed on the pre-3-July-2024 " +
                                      "schedule without knowing whether that is this person's schedule. If they first " +
                                      "registered with GOSI on or after 3 July 2024 the contribution is wrong and GOSI " +
                                      "will correct it later with a surcharge. Next action: record the employee's GOSI " +
                                      "first-registration date from their GOSI record (the change needs approval); the " +
                                      "next run picks it up, or reopen this run and process it again (re-process).",
                                slip.EmployeeId);
                    }

                    // 45 k ceiling warning
                    var coveredWage = slip.BasicSalary + slip.HousingAllowance;
                    var ceiling = ctx.GosiCoveredWageCeiling > 0m ? ctx.GosiCoveredWageCeiling : DefaultGosiCoveredWageCeiling;
                    if (coveredWage > ceiling)
                        Warn("GOSI_CEILING_EXCEEDED",
                            $"Employee {slip.EmployeeCode} covered wage (Basic + Housing = {coveredWage:N2} SAR) exceeds the GOSI {ceiling:N0} SAR ceiling. " +
                            $"Verify that contributions were calculated on {ceiling:N0} SAR, not {coveredWage:N2} SAR.",
                            slip.EmployeeId);

                    // S1/A3 corollary — GOSI annuities and SANED have AGE-based eligibility and cease
                    // at retirement age. With no age test anywhere, the product deducts SANED from a
                    // 62-year-old Saudi indefinitely. DateOfBirth exists on the employee record, so
                    // the condition is at least detectable even though the cessation rule is not
                    // implemented. Silent when no retirement age is configured.
                    if (ctx.GosiRetirementAgeYears > 0 && hasGosiEe && emp.DateOfBirth is DateOnly dob)
                    {
                        var periodStartDate = new DateOnly(ctx.Run.Year, ctx.Run.Month, 1);
                        var age = periodStartDate.Year - dob.Year
                                - (periodStartDate < dob.AddYears(periodStartDate.Year - dob.Year) ? 1 : 0);
                        if (age >= ctx.GosiRetirementAgeYears)
                            Warn("WARN_GOSI_AGE_ELIGIBILITY_NOT_MODELLED",
                                $"Employee {slip.EmployeeCode} is {age} and GOSI employee contributions of " +
                                $"{gosiEeAmount:N2} SAR were still deducted. GOSI annuities and SANED have " +
                                $"age-based eligibility and cease at retirement age ({ctx.GosiRetirementAgeYears}); " +
                                "this product does not model the cessation, so the deduction may be an " +
                                "OVER-deduction from the employee's net pay. [COUNSEL] confirm the cessation rule " +
                                "for someone who continues working past retirement age, then correct this slip.",
                                slip.EmployeeId);
                    }
                }
                else  // NonSaudi / expat
                {
                    if (hasGosiEe)
                        Err("GOSI_APPLIED_TO_EXPAT",
                            $"Employee {slip.EmployeeCode} is classified as {classification} (expat) but has GOSI employee deductions of {gosiEeAmount:N2} SAR. " +
                            "Expatriate employees must not contribute to GOSI Annuities or SANED.",
                            slip.EmployeeId);
                }
            }

            // Rule 5a: IBAN present + valid Saudi format. Not asked of an employee paid by cash or cheque
            // (they are left out of the bank batch, by name, instead), nor of an ANB-to-ANB credit to a
            // 16-digit ANB account number (the bank export's own account rules check that).
            var iban = profile?.Iban ?? string.Empty;
            var paidOutsideBankFile = PaymentBatchExclusions.IsPaidOutsideBankFile(profile?.PaymentMethod);
            var anbInternalAccount = SaudiBankExports.SaudiBeneficiaryBic.IsAnbInternalCredit(emp, profile);
            // Cash / cheque pay is surfaced, never silent: it leaves the bank/WPS file, and the approver must
            // acknowledge the count at Approve. Stronger when a valid IBAN is on file — paying that person
            // in cash is then a choice, not a necessity.
            if (paidOutsideBankFile && slip.NetSalary > 0m)
            {
                var hasIban = !string.IsNullOrWhiteSpace(iban) && IbanValidator.IsValid(iban);
                var method = profile!.PaymentMethod.Trim().ToLowerInvariant();
                var mudad = isKsa ? " " + PaymentBatchExclusions.MudadNote : string.Empty;
                if (hasIban)
                    Warn(PaymentBatchExclusions.PaidOutsideWithIbanWarning,
                        $"Employee {slip.EmployeeCode} is set to be paid by {method}, although a valid IBAN is on file. " +
                        $"They will be left out of the bank/WPS file and their wage must be paid and recorded separately.{mudad} " +
                        "If this is not intended, change the payment method to bank transfer before locking.",
                        slip.EmployeeId);
                else
                    Warn(PaymentBatchExclusions.PaidOutsideWarning,
                        $"Employee {slip.EmployeeCode} is paid by {method}: they will be left out of the bank/WPS file and " +
                        $"their wage must be paid and recorded separately.{mudad}",
                        slip.EmployeeId);
            }
            if (paidOutsideBankFile || anbInternalAccount || slip.NetSalary <= 0m) { }
            else if (string.IsNullOrWhiteSpace(iban))
                Err("MISSING_IBAN",
                    $"Employee {slip.EmployeeCode} has no IBAN on their payroll profile. " +
                    "Bank details are required for WPS payment disbursement.",
                    slip.EmployeeId);
            else if (!IbanValidator.IsValid(iban))
                Err("INVALID_IBAN",
                    $"Employee {slip.EmployeeCode} IBAN '{iban}' fails country format/length or ISO 13616 mod-97 validation. " +
                    "Correct the IBAN before approving this run.",
                    slip.EmployeeId);
            else if (isKsa && !IbanValidator.IsSaudiIban(iban))
                Warn("NON_SAUDI_IBAN",
                    $"Employee {slip.EmployeeCode} IBAN does not start with 'SA'. " +
                    "For a Saudi payroll run, confirm the bank account is held in Saudi Arabia.",
                    slip.EmployeeId);

            // Rule 5c: bank details a NEW employee was imported with, which no second person has confirmed yet
            // (EmployeeImportGap.BankDetailsUnverified). A Warning: the details may well be right, but the run
            // is about to send money to them, so HR confirms them (or corrects them) before Lock.
            if (!paidOutsideBankFile && slip.NetSalary > 0m && ctx.UnverifiedImportedBankDetails.Contains(slip.EmployeeId))
                Warn(ImportedBankDetailsUnverified,
                    $"Employee {slip.EmployeeCode}'s bank details came from an employee import and have not been confirmed " +
                    "by a second person. Verify them with the employee, then confirm them on the employee's record " +
                    "(People → the employee → Confirm bank details) before this run is locked.",
                    slip.EmployeeId);

            // Rule 5b: MOL ID required for KSA regulatory reporting
            if (isKsa)
            {
                var molId = profile?.MolId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(molId))
                    Warn("MISSING_MOL_ID",
                        $"Employee {slip.EmployeeCode} has no MOL ID on their payroll profile. " +
                        "MOL ID is required for WPS (Mudad) regulatory reporting in Saudi Arabia.",
                        slip.EmployeeId);
            }

            // Rule 9: salary currency must match company default
            var empCurrency = profile?.SalaryCurrency ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(empCurrency) &&
                !string.Equals(empCurrency, companyCurrency, StringComparison.OrdinalIgnoreCase))
                Warn("CURRENCY_MISMATCH",
                    $"Employee {slip.EmployeeCode} salary currency '{empCurrency}' differs from company default '{companyCurrency}'. " +
                    "All payslips in a run should use the company's reporting currency.",
                    slip.EmployeeId);
        }

        // ── Rule 7: Run-level totals reconcile ───────────────────────────────
        if (ctx.Slips.Count > 0)
        {
            var sumGross = ctx.Slips.Sum(s => s.GrossSalary);
            var sumDed   = ctx.Slips.Sum(s => s.Deductions);
            var sumNet   = ctx.Slips.Sum(s => s.NetSalary);

            if (Math.Abs(ctx.Run.TotalGrossSalary - sumGross) > 0.01m)
                Err("TOTALS_GROSS_MISMATCH",
                    $"Run header gross ({ctx.Run.TotalGrossSalary:N2}) does not match sum of payslip gross ({sumGross:N2}). " +
                    "Re-process the run to recalculate totals.");

            if (Math.Abs(ctx.Run.TotalDeductions - sumDed) > 0.01m)
                Err("TOTALS_DEDUCTIONS_MISMATCH",
                    $"Run header deductions ({ctx.Run.TotalDeductions:N2}) does not match sum of payslip deductions ({sumDed:N2}). " +
                    "Re-process the run to recalculate totals.");

            if (Math.Abs(ctx.Run.TotalNetSalary - sumNet) > 0.01m)
                Err("TOTALS_NET_MISMATCH",
                    $"Run header net ({ctx.Run.TotalNetSalary:N2}) does not match sum of payslip net ({sumNet:N2}). " +
                    "Re-process the run to recalculate totals.");
        }

        // ── Rule 8: GL pre-check ─────────────────────────────────────────────
        // Accounting equation: Σ(earnings DR) = Σ(employee deductions CR) + net salary payable CR.
        // Employer statutory contributions cancel (DR expense = CR liability).
        // This reduces to: TotalGross = TotalDeductions + TotalNet at the run level.
        if (ctx.Slips.Count > 0)
        {
            var glImbalance = ctx.Run.TotalGrossSalary - (ctx.Run.TotalDeductions + ctx.Run.TotalNetSalary);
            if (Math.Abs(glImbalance) > 0.01m)
                Err("GL_WILL_NOT_BALANCE",
                    $"GL pre-check failed: gross ({ctx.Run.TotalGrossSalary:N2}) ≠ deductions ({ctx.Run.TotalDeductions:N2}) + net ({ctx.Run.TotalNetSalary:N2}). " +
                    $"Difference: {glImbalance:N2}. The journal will not balance on lock; re-process the run.");
        }

        // ── Rule 10: No attendance or OT data processed for active employee ──────
        // WARN only — full salary is still paid; payroll can proceed.
        // Trigger: employee has no AttendanceDailyRecord AND no OT impact in period.
        // Absence of data may mean attendance was never processed (not "perfect attendance").
        foreach (var emp in ctx.ActiveEmployees)
        {
            var hasAttendance = ctx.AttendanceProcessedEmployeeIds.Contains(emp.Id);
            var hasOt         = ctx.OvertimeHoursByEmployee.ContainsKey(emp.Id);
            if (!hasAttendance && !hasOt)
                Warn("WARN_NO_ATTENDANCE",
                    $"No attendance or overtime data was processed for employee {emp.EmployeeCode} ({emp.FullName}) " +
                    $"in {ctx.Run.Year}-{ctx.Run.Month:D2}. Full salary assumed. " +
                    "Verify attendance processing ran before approving this run.",
                    emp.Id);
        }

        // ── Rule 11: OT hours exist but hourly rate cannot be resolved ────────
        // ERROR — blocks approve/lock; prevents silent zero-pay for approved overtime.
        // Trigger: employee has approved OT hours but basic salary is zero (rate = 0).
        foreach (var kvp in ctx.OvertimeHoursByEmployee)
        {
            if (kvp.Value <= 0m) continue;
            var sal   = ctx.SalaryAssignments
                .Where(x => x.EmployeeId == kvp.Key)
                .OrderByDescending(x => x.EffectiveDate)
                .FirstOrDefault();
            var basic = sal?.BasicSalary ?? 0m;
            if (basic <= 0m)
            {
                empById.TryGetValue(kvp.Key, out var emp2);
                Err("OT_RATE_UNRESOLVED",
                    $"Employee {emp2?.EmployeeCode ?? kvp.Key.ToString()} has {kvp.Value:N2} approved " +
                    "overtime hours but basic salary is zero — hourly rate cannot be computed. " +
                    "Set basic salary before approving this run.",
                    kvp.Key);
            }
        }

        // ── Rule 12 (POD-B2 / M2): cross-run double-pay of recurring salary ───────
        // ERROR — the only control in this codebase that looks ACROSS runs. Rule 4's DUPLICATE_EMPLOYEE
        // is within-run only, and "non-Regular runs skip recurring pay" is a convention, not a guard: one
        // operator setting includesRecurringPay on an off-cycle run would otherwise pay a second full
        // salary with nothing objecting. Symmetric by construction — it fires on whichever recurring-pay
        // run is processed SECOND, so it protects the Regular run too.
        if (paysRecurring && ctx.EmployeesAlreadyPaidRecurringThisPeriod.Count > 0)
        {
            foreach (var slip in ctx.Slips.GroupBy(s => s.EmployeeId).Select(g => g.First()))
            {
                if (!ctx.EmployeesAlreadyPaidRecurringThisPeriod.Contains(slip.EmployeeId)) continue;
                Err("ALREADY_PAID_THIS_PERIOD",
                    $"Employee {slip.EmployeeCode} was already paid recurring salary for " +
                    $"{ctx.Run.Year}-{ctx.Run.Month:D2} by another non-voided payroll run. " +
                    "Paying recurring salary twice in one period is never correct: either exclude this " +
                    "employee from this run (Include/Exclude selector), switch this run to a supplemental " +
                    "basis, or void the other run.",
                    slip.EmployeeId);
            }
        }

        // ── Rule 13 (POD-B2): deliberate hold-outs are REPORTED, never silently dropped ───────────
        // Warning severity by design — an exclusion is intentional, so it must not block the workflow.
        // The control that makes it *seen* is the acknowledgement gate at Approve
        // (expectedExcludedCount), plus the exclusion counts echoed on the run header, Lock and the
        // payment batch. A warning alone is not a control; these four channels together are.
        foreach (var x in ctx.Exclusions)
            Warn("EMPLOYEE_EXCLUDED_FROM_RUN",
                $"Employee {x.EmployeeCode} ({x.EmployeeName}) was deliberately excluded from this run. " +
                $"Reason: {x.Reason}",
                x.EmployeeId);

        // Someone the run would otherwise have paid but could NOT — the operator's intent (or the
        // default "everyone eligible") was not honoured, which is a different and more dangerous fact
        // than a deliberate exclusion.
        //
        // POD-C3-FIX — the message no longer ASSERTS where the row came from. It used to open "was named
        // in this run's population selector", which is simply untrue for the channel C3 added: a
        // post-period joiner is auto-excluded by Process (PayrollController.cs:1456) on an AllEligible
        // run where no selector row exists at all. The recorded Reason, which is the part that actually
        // identifies the cause, was and remains correct — so the fix is to stop stating a provenance this
        // rule cannot know rather than to add a second code.
        foreach (var x in ctx.NotEligibleSelections)
            Warn("EMPLOYEE_SELECTION_NOT_ELIGIBLE",
                $"Employee {x.EmployeeCode} ({x.EmployeeName}) is in scope for this run but was NOT paid by it: " +
                "they are not eligible (not Active, deleted, belongs to another legal entity, or not employed " +
                $"during this period). Reason recorded: {x.Reason}",
                x.EmployeeId);

        // ── Rule 14 (POD-B2 / M8): the period holds more than one run ─────────────
        // Statutory reporting (GosiController's contribution-summary / variance-report) is runId-keyed, so
        // a per-run GOSI figure is only PART of the period's filing once siblings exist. Surface that to
        // the preparer here; the period-level rollup endpoint is the correct filing source.
        if (ctx.SiblingRunCount > 0)
            Warn("PERIOD_HAS_SIBLING_RUNS",
                $"{ctx.SiblingRunCount} other non-voided payroll run(s) exist for {ctx.Run.Year}-{ctx.Run.Month:D2} " +
                "in this legal entity. Per-run statutory reports cover THIS run only — use the period-level " +
                "GOSI rollup (GET /api/gosi/periods/{year}/{month}/contribution-summary) for filing.");

        // ── Rule 15 (KSA): Labour Law Art. 92/93 — DEBT-type deductions ≤ half the wage due ──────
        // Checked BEFORE Lock so it surfaces here, not on export day. Only debt-type lines count (loan and
        // advance instalments, penalties/fines, damages — WageDeductionClassification); statutory GOSI,
        // absence/loss-of-pay and unpaid leave do not, so a five-paid-day joiner whose GOSI is on the
        // full-month base, or a long unpaid absence, passes. Overridable with a recorded reason and the
        // reference of its written basis; the bank export honours that override.
        if (isKsa)
        {
            // PER SLIP for now. The limit belongs to the pay period, so an off-cycle or supplementary run in the same
            // month shares it; the deductions statement (DeductionStatementService) already sums the period's non-voided
            // runs. Moving this rule to the period is queued as a separate engine change.
            var debtByEmp = WageDeductionClassification.DebtTotalsByEmployee(ctx.Deductions);
            foreach (var slip in ctx.Slips.GroupBy(s => s.EmployeeId).Select(g => g.First()))
            {
                var debt = debtByEmp.GetValueOrDefault(slip.EmployeeId);
                // Wage due = gross minus absence/LOP and unpaid leave (one definition, WageDeductionClassification.WageDue).
                var wageDue = WageDeductionClassification.WageDue(slip.GrossSalary, ctx.Deductions.Where(d => d.EmployeeId == slip.EmployeeId));
                if (!WageDeductionClassification.ExceedsHalfWage(debt, wageDue)) continue;
                Err(WageDeductionClassification.DeductionsExceedHalfWageCode,
                    $"Employee {slip.EmployeeCode}: loan, advance, penalty and damages deductions ({debt:N2}) are more " +
                    $"than half of the wage due after absence ({wageDue:N2}). Saudi Labour Law Art. 92/93 caps them at 50% " +
                    "(statutory GOSI is not counted; absence and unpaid leave reduce the wage due). Reschedule the instalment or reduce the " +
                    "deduction and re-process, or override it citing " + WageDeductionClassification.CapOverrideGrounds +
                    " and its reference.",
                    slip.EmployeeId);
            }
        }

        if (ctx.StatutoryComputedIncrementally)
            Warn("SUPPLEMENTAL_STATUTORY_BASE",
                "Statutory contributions on this run were computed INCREMENTALLY: the covered wage already " +
                "reported by sibling runs for this period was added to the base, the statutory ceiling was " +
                "applied to the period total, and the amounts sibling runs already deducted were netted off. " +
                "The per-run figure is therefore a period delta, not a standalone computation. " +
                "[FLAG-COMPLIANCE-KSA: incremental period-to-date statutory basis requires sign-off before filing.]");

        return results;
    }

    private static bool IsGosiEeCode(string code) => IsGosiEmployeeCode(code);

    /// <summary>F02 — a Gregorian ISO date whatever the server culture (ar-SA defaults to Umm al-Qura).</summary>
    private static string IsoDate(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Employee-side GOSI component codes (GOSI-ANN-EE, GOSI-SANED-EE, …). Public so the callers that
    /// build <see cref="PayrollValidationContext.PriorPeriodGosiEeByEmployee"/> classify sibling-run
    /// deductions with the SAME predicate Rule 2 uses — a second, drifting copy of this test would put
    /// the period total and the per-run total on different definitions.
    /// </summary>
    public static bool IsGosiEmployeeCode(string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.StartsWith("GOSI-", StringComparison.OrdinalIgnoreCase) &&
        code.EndsWith("-EE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>All data required by <see cref="PayrollValidationEngine.Run"/>.</summary>
public sealed record PayrollValidationContext(
    PayrollRun                             Run,
    IReadOnlyList<PayrollSlip>             Slips,
    IReadOnlyList<Employee>                ActiveEmployees,
    IReadOnlyList<EmployeeSalaryStructure> SalaryAssignments,
    IReadOnlyList<EmployeePayrollProfile>  Profiles,
    IReadOnlyList<PayrollDeduction>        Deductions,
    IReadOnlyList<PayrollEarning>          Earnings,
    Company?                               Company)
{
    // Set from Process/Validate to enable Rules 10+11.
    // Default to empty so existing callers that don't supply these are safe.

    /// <summary>
    /// Rule 5c — employees with an open <see cref="EmployeeImportGap.BankDetailsUnverified"/> gap. MUST be
    /// populated identically by Process and by /validate (which replaces the stored results wholesale).
    /// </summary>
    public IReadOnlySet<int> UnverifiedImportedBankDetails { get; init; } = new HashSet<int>();

    /// <summary>Total approved OT hours per employee in this pay period.</summary>
    public IReadOnlyDictionary<int, decimal> OvertimeHoursByEmployee { get; init; } =
        new Dictionary<int, decimal>();

    /// <summary>
    /// Employee IDs that have at least one AttendanceDailyRecord in the pay period.
    /// An employee absent from this set has never had attendance processed.
    /// </summary>
    public IReadOnlySet<int> AttendanceProcessedEmployeeIds { get; init; } =
        new HashSet<int>();

    /// <summary>
    /// The EffectiveFrom date of the most recent system-default GOSI rate rules.
    /// If set and older than 18 months, the engine emits WARN_GOSI_RATES_REQUIRE_SIGNOFF.
    /// Populated by the Process/Validate endpoints from GosiContributionRule data.
    /// </summary>
    public DateOnly? GosiRatesEffectiveFrom { get; init; }

    // ── POD-B2: multi-run-per-period inputs ──────────────────────────────────────

    /// <summary>
    /// POD-B2 (M2) — employees who ALREADY hold a payslip from a different non-voided run that paid
    /// recurring salary for this same (company, year, month). This is the real cross-run double-pay
    /// control: "non-Regular runs skip recurring pay" is only a convention, and nothing else in this
    /// codebase checks across runs (Rule 4 DUPLICATE_EMPLOYEE is within-ctx.Slips only). Raised as an
    /// Error whenever THIS run also pays recurring, so it protects symmetrically — the Regular run is
    /// blocked when a full-basis OffCycle run got there first, and vice versa.
    /// </summary>
    public IReadOnlySet<int> EmployeesAlreadyPaidRecurringThisPeriod { get; init; } = new HashSet<int>();

    /// <summary>POD-B2 — employees the operator deliberately held OUT, with the recorded reason.</summary>
    public IReadOnlyList<PayrollRunExclusion> Exclusions { get; init; } = Array.Empty<PayrollRunExclusion>();

    /// <summary>POD-B2 — Include rows naming someone who is not in the eligible set (wrong company / not Active / deleted).</summary>
    public IReadOnlyList<PayrollRunExclusion> NotEligibleSelections { get; init; } = Array.Empty<PayrollRunExclusion>();

    /// <summary>POD-B2 (M8) — count of OTHER non-voided runs in the same (company, year, month).</summary>
    public int SiblingRunCount { get; init; }

    /// <summary>
    /// POD-B2 — employee-side GOSI already deducted for this (company, year, month) by OTHER non-voided
    /// runs. Rule 2 adds it to this run's own GOSI EE before deciding whether the employee contributed
    /// nothing, because GOSI is a period obligation and the incremental statutory basis deliberately nets
    /// a run to zero once the period ceiling is consumed. Empty for a period with a single run — i.e. for
    /// every run that exists in every tenant today — so Rule 2 is then exactly what it was.
    /// MUST be populated identically by Process and by /validate: /validate replaces the stored results
    /// wholesale, so a version that omitted this would re-raise the Error that Process correctly withheld.
    /// </summary>
    public IReadOnlyDictionary<int, decimal> PriorPeriodGosiEeByEmployee { get; init; } =
        new Dictionary<int, decimal>();

    /// <summary>
    /// POD-B2 (M8) — true when this run's statutory amounts were computed INCREMENTALLY against the
    /// period-to-date covered wage already reported by sibling runs, rather than against zero. Drives an
    /// informational warning so the preparer knows the per-run GOSI figure is a period delta.
    /// </summary>
    public bool StatutoryComputedIncrementally { get; init; }

    /// <summary>
    /// S1/A2(c) — the GOSI covered-wage ceiling actually in force for this run's period, resolved from
    /// the same effective-dated <c>gosi.covered_wage_ceiling_sar</c> rule the country pack caps on.
    /// Zero means "not supplied" and the engine falls back to its compiled default, so a caller that
    /// has not been updated behaves exactly as before.
    /// </summary>
    public decimal GosiCoveredWageCeiling { get; init; }

    /// <summary>
    /// S1/A3 (corollary) — the age at which GOSI annuities and SANED cease. Zero disables the check.
    /// Driven by <c>gosi.retirement_age_years</c>; [COUNSEL] for the current figure and for the
    /// treatment of someone who continues working past it.
    /// </summary>
    public int GosiRetirementAgeYears { get; init; }
}

/// <summary>
/// POD-B2 — one employee deliberately held out of (or wrongly named in) a run's population, carrying the
/// operator's reason so the exclusion is reported on the run rather than silently dropped.
/// </summary>
public sealed record PayrollRunExclusion(int EmployeeId, string EmployeeCode, string EmployeeName, string Reason);
