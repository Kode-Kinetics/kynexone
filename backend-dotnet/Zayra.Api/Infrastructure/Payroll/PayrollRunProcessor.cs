using System.Globalization;
using Zayra.Api.Infrastructure.Benefits;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Governance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// What one processing pass needs besides the run itself: the tenant, who asked, and from where. The same
/// values the HTTP request carried (<c>tenant_id</c>, the signed-in user, the caller's address), so the audit
/// rows a background pass writes are attributed exactly as the synchronous pass attributed them.
/// </summary>
/// <param name="GroupScope">False when the asker is limited to some companies. The pass then sees exactly what the
/// request path showed that caller: company-attributed rows of their companies only, never unassigned
/// (null-company) rows, because the worker has no principal and so no row filter of its own.</param>
public sealed record PayrollProcessRequest(
    Guid RunId, Guid TenantId, Guid? ActorUserId, string? IpAddress,
    bool GroupScope = true, IReadOnlyList<Guid>? CompanyIds = null);

/// <summary>
/// Optional seams for a host that runs the pass somewhere other than a request (the background job).
/// <see cref="Progress"/> is told (employees done, employees total) at coarse intervals;
/// <see cref="BeforeCommit"/> runs INSIDE the run's transaction, after the last write and immediately before
/// COMMIT, and may throw to abort the whole pass (the lease fence uses that to refuse a stale worker).
/// </summary>
public sealed class PayrollProcessHooks
{
    public Func<int, int, CancellationToken, Task>? Progress { get; init; }
    public Func<CancellationToken, Task>? BeforeCommit { get; init; }
}

/// <summary>
/// The result of a pass, independent of HTTP: the status code the endpoint used to answer with, its body, and
/// (on success) the processed run.
/// </summary>
/// <param name="Generic">True for the deliberate aborts raised inside the transaction (answered as a bare status
/// code with a body rather than as a named result), so the endpoint keeps the exact result type it always gave.</param>
public sealed record PayrollProcessOutcome(int StatusCode, object? Body = null, PayrollRun? Run = null, bool Generic = false)
{
    public bool Succeeded => StatusCode == 200;
}

/// <summary>
/// POD-B2 — a deliberate abort from inside the processing execution-strategy transaction. Thrown (not
/// returned) because the delegate cannot return an outcome; throwing is also what guarantees the
/// transaction is rolled back rather than partially committed.
/// </summary>
internal sealed class PayrollProcessAbortException : Exception
{
    public int StatusCode { get; }
    public object Payload { get; }
    public PayrollProcessAbortException(int statusCode, object payload)
        : base("Payroll processing aborted.") { StatusCode = statusCode; Payload = payload; }
}

/// <summary>
/// Computes a payroll run: payslips, earning and deduction lines, statutory amounts, loan and advance
/// ledger movements, validation results and the audit entry, as ONE all-or-nothing transaction.
///
/// <para>This is the body that used to live inside <c>PayrollController.Process</c>, moved here unchanged so
/// the HTTP request no longer has to hold it open. It is called from two places: the background job
/// (<see cref="PayrollRunProcessJobHandler"/>), which is what the API runs, and the controller's in-process
/// entry point that the existing test suites drive. Both get the same results, the same audit rows and the
/// same refusals.</para>
///
/// <para>ATOMIC, NOT RESUMABLE. A pass either commits everything or nothing, so a failure leaves the run
/// untouched and a retry is just another pass. See <see cref="PayrollRunProcessJobHandler"/> for how the
/// background host fences and checkpoints that single commit.</para>
/// </summary>
public sealed class PayrollRunProcessor
{
    private readonly ZayraDbContext _db;
    private readonly ICountryPackResolver _packResolver;
    private readonly IStatutoryRuleReader _ruleReader;
    private readonly ICompanyTaxPolicyResolver _taxResolver;
    private readonly IEmployeeActivationGuard _activationGuard;
    private readonly IProrationPolicyResolver _prorationPolicy;
    private readonly Zayra.Api.Application.WorkWeek.IWorkWeekService _workWeek;

    public PayrollRunProcessor(ZayraDbContext db, ICountryPackResolver packResolver, IStatutoryRuleReader ruleReader,
        ICompanyTaxPolicyResolver taxResolver, IEmployeeActivationGuard activationGuard,
        IProrationPolicyResolver prorationPolicy, Zayra.Api.Application.WorkWeek.IWorkWeekService workWeek)
    {
        _db = db;
        _packResolver = packResolver;
        _ruleReader = ruleReader;
        _taxResolver = taxResolver;
        _activationGuard = activationGuard;
        _prorationPolicy = prorationPolicy;
        _workWeek = workWeek;
    }

    // The asker's company scope for this pass. Group-level (the default, and always the case for the in-process
    // entry point, whose DbContext carries the real filters) adds nothing.
    private bool _groupScope = true;
    private List<Guid> _scopeCompanyIds = [];

    /// <summary>The company row filter a non-group request principal would have had on operational tables.</summary>
    private IQueryable<T> Op<T>(IQueryable<T> source) where T : class, ICompanyScopedOperational
    {
        if (_groupScope) return source;
        var ids = _scopeCompanyIds;
        return source.Where(x => x.CompanyId != null && ids.Contains(x.CompanyId.Value));
    }

    // The outcome constructors keep the moved body's `return UnprocessableEntity(...)` lines as they were.
    private static PayrollProcessOutcome NotFound() => new(404);
    private static PayrollProcessOutcome BadRequest(object body) => new(400, body);
    private static PayrollProcessOutcome UnprocessableEntity(object body) => new(422, body);
    private static PayrollProcessOutcome Conflict(object body) => new(409, body);
    private static PayrollProcessOutcome StatusCode(int statusCode, object body) => new(statusCode, body, Generic: true);
    private static PayrollProcessOutcome Ok(PayrollRun run) => new(200, run, run);

    /// <summary>
    /// The run-state refusals, in one place so the endpoint can answer them synchronously (before queueing)
    /// with exactly the 4xx the pass itself would give. Null means the run may be processed.
    /// </summary>
    public static PayrollProcessOutcome? CheckRunState(PayrollRun run)
    {
        // Processing mutates attendance/leave impacts and loan/advance balances, so completed states must use a correction workflow.
        if (run.Status is "Processed" or "PendingFinanceReview" or "Locked" or "Approved" or "Paid")
            return BadRequest(new { message = $"A run in '{run.Status}' status cannot be reprocessed. Void/delete and recreate the run, or use the correction workflow." });
        // POD-B2: "Voided" was missing from the list above. Pre-B2 that silently RESURRECTED a voided run
        // (Voided → Processed). Post-B2 it is worse: flipping the status pulls the row back INTO the
        // partial unique index alongside the live Regular run, so it would fail as a DbUpdateException 500
        // instead of a clean 4xx. This is a hardening of the void rules, never a relaxation.
        if (run.Status == "Voided")
            return BadRequest(new
            {
                error   = "run_voided",
                message = "A voided payroll run cannot be reprocessed. Create a replacement run for the period instead.",
            });

        return null;
    }

    /// <summary>Exception.Data keys that name the employee whose calculation was running when a pass failed.</summary>
    public const string FailingEmployeeIdKey = "payroll.failing_employee_id";
    public const string FailingEmployeeCodeKey = "payroll.failing_employee_code";

    /// <summary>
    /// Exception-filter helper: tags a failure that escaped from inside the per-employee loop with that
    /// employee, WITHOUT catching or wrapping it (so exception types, and every test that asserts them, are
    /// unchanged). Always answers false.
    /// </summary>
    private static bool AnnotateEmployeeFailure(Exception ex, int? employeeId, string? employeeCode)
    {
        if (employeeId is { } id && !ex.Data.Contains(FailingEmployeeIdKey))
        {
            ex.Data[FailingEmployeeIdKey] = id.ToString(CultureInfo.InvariantCulture);
            ex.Data[FailingEmployeeCodeKey] = employeeCode ?? string.Empty;
        }
        return false;
    }

    /// <summary>The audit row the pass writes, attributed to the user who asked (see <see cref="PayrollProcessRequest"/>).</summary>
    private Task WritePayrollAuditAsync(PayrollProcessRequest request, string action, string entity, string entityId,
        object? metadata, CancellationToken ct)
    {
        var meta = new { ip = request.IpAddress ?? "unknown", userId = request.ActorUserId?.ToString(), data = metadata };
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            Action = action,
            EntityName = entity,
            EntityId = entityId,
            UserId = request.ActorUserId,
            MetadataJson = JsonSerializer.Serialize(meta),
        });
        // Seq / PreviousHash / EntryHash are stamped by the ZayraDbContext sealer when the row is flushed by
        // the pass's own SaveChanges — never set here.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Coarse progress for the per-employee loop: once at the start, then at most every max(25, 5%) employees
    /// and no more than once a second, then once at the end. Never per row.
    /// </summary>
    private sealed class ProgressThrottle
    {
        private readonly int _total;
        private readonly int _step;
        private readonly Func<int, int, CancellationToken, Task>? _sink;
        private int _started;
        private int _lastReported = -1;
        private DateTime _lastAtUtc = DateTime.MinValue;

        public ProgressThrottle(int total, Func<int, int, CancellationToken, Task>? sink)
        {
            _total = total;
            _sink = sink;
            _step = Math.Max(25, total / 20);
        }

        public Task StartAsync(CancellationToken ct) => ReportAsync(0, ct);

        /// <summary>Called at the top of each employee's iteration; <c>done</c> employees are finished.</summary>
        public async Task AdvanceAsync(CancellationToken ct)
        {
            var done = _started++;
            if (done == 0 || _sink is null) return;
            if (done - _lastReported >= _step && DateTime.UtcNow - _lastAtUtc >= TimeSpan.FromSeconds(1))
                await ReportAsync(done, ct);
        }

        public Task FinishAsync(CancellationToken ct) => ReportAsync(_total, ct);

        private async Task ReportAsync(int done, CancellationToken ct)
        {
            if (_sink is null) return;
            _lastReported = done;
            _lastAtUtc = DateTime.UtcNow;
            await _sink(done, _total, ct);
        }
    }

    /// <summary>
    /// Processes one run: the former body of <c>POST /api/payroll/runs/{id}/process</c>, with its
    /// 4xx answers returned as a <see cref="PayrollProcessOutcome"/>.
    /// </summary>
    public async Task<PayrollProcessOutcome> ProcessAsync(
        PayrollProcessRequest request, PayrollProcessHooks? hooks, CancellationToken cancellationToken)
    {
        var id = request.RunId;
        var tenantId = request.TenantId;
        _groupScope = request.GroupScope;
        _scopeCompanyIds = (request.CompanyIds ?? []).ToList();
        var run = await Op(_db.PayrollRuns).FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        if (CheckRunState(run) is { } refused) return refused;
        var periodStart = new DateOnly(run.Year, run.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // NOTE (P0-2 atomicity): the idempotent delete of previously-generated rows was moved
        // INTO the execution-strategy transaction below so a mid-run fault never commits the
        // deletes (or the slip writes) without the loan-ledger decrement.

        // Resolve company → country pack for statutory deduction.
        var activeCompaniesForTenant = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var legacySingleCompanyScope = activeCompaniesForTenant.Count == 1;
        var company = run.CompanyId.HasValue
            ? activeCompaniesForTenant.FirstOrDefault(c => c.Id == run.CompanyId.Value)
            : legacySingleCompanyScope ? activeCompaniesForTenant[0] : null;
        var hasAnyCompanyScopedEmployees = await Op(_db.Employees).AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId.HasValue, cancellationToken);

        // P0 FAIL-LOUD GUARD — abort before any payslips are written.
        // A payroll run must never proceed when the statutory pack cannot be resolved;
        // silently producing zero-deduction payslips violates GCC labour law.
        if (company is null)
            return UnprocessableEntity(new
            {
                error   = "company_not_resolved",
                message = "No active company was found for this account, so statutory deductions cannot be worked out. " +
                          "Create and activate a company with a country set in Setup → Companies before running payroll.",
            });
        if (string.IsNullOrWhiteSpace(company.CountryCode))
            return UnprocessableEntity(new
            {
                error       = "country_code_missing",
                message     = $"{company.LegalNameEn} has no country set, so statutory deductions cannot be worked out. " +
                              "Set it in Setup → Companies, then try again.",
                companyId   = company.Id,
                companyName = company.LegalNameEn,
            });

        var packCc  = company.CountryCode;
        var packJur = company.Jurisdiction ?? string.Empty;
        var deductionCalc = _packResolver.ResolveDeductionCalculator(packCc, packJur);

        if (deductionCalc is DefaultStatutoryDeductionCalculator)
            return UnprocessableEntity(new
            {
                error        = "statutory_pack_not_configured",
                message      = $"No statutory deduction pack is registered for country '{packCc}' / jurisdiction '{packJur}'. " +
                               "Register a country pack for this jurisdiction before processing payroll. " +
                               "Payroll run aborted — no payslips written.",
                countryCode  = packCc,
                jurisdiction = packJur,
                companyId    = company.Id,
            });

        var hasEmployeesForRunCompany = await Op(_db.Employees).AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId == company.Id, cancellationToken);
        // A caller limited to some companies never sees unassigned employees (the request-path row filter hid them).
        var allowLegacyUnscopedEmployees = _groupScope && (legacySingleCompanyScope || !hasAnyCompanyScopedEmployees || !hasEmployeesForRunCompany);

        // ── POD-B2 (M1): a null-company Regular run is UNSCOPED and competes with every company ──────
        // Process is about to stamp `run.CompanyId ??= company.Id`, collapsing this run onto that
        // company's period key. If a Regular run already owns that key, the ??= would throw a
        // DbUpdateException 500 at commit. Surface it as a clean 409 BEFORE any work is done.
        // POD-B3 — the check covers both PERIOD-OWNING types (Regular, Replacement); see CreateRun.
        if (run.CompanyId is null && PayrollRunTypes.IsPeriodOwning(run.RunType))
        {
            var competing = await Op(_db.PayrollRuns).AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Id != run.Id
                         && r.Year == run.Year && r.Month == run.Month
                         && r.CompanyId == company.Id
                         && (r.RunType == PayrollRunTypes.Regular || r.RunType == PayrollRunTypes.Replacement)
                         && r.Status != "Voided")
                .Select(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (competing != Guid.Empty)
                return Conflict(new
                {
                    error     = "regular_run_exists",
                    message   = $"This run has no legal entity and would be scoped to '{company.LegalNameEn}' on processing, " +
                                $"but a regular run already exists there for {run.Year}/{run.Month:D2}. Void one of them first.",
                    runId     = competing,
                    companyId = company.Id,
                });
        }

        // ── POD-C3: the NAMED, per-company proration policy for this run ─────────────────────────────
        // Resolved BEFORE any work is done and refused loudly on a bad value: a silent fallback here is
        // exactly how a tenant ends up paying joiners and leavers on a basis nobody chose.
        var workWeekConfig = await Zayra.Api.Infrastructure.WorkWeek.CompanyWorkWeek.ResolveAsync(_workWeek, tenantId, company.Id, cancellationToken);
        var (prorationPolicy, prorationPolicyError) =
            await _prorationPolicy.ResolveAsync(tenantId, company.Id, periodEnd, workWeekConfig, cancellationToken);
        if (prorationPolicyError is not null)
            return UnprocessableEntity(new
            {
                error   = prorationPolicyError.Code,
                message = prorationPolicyError.Message,
                detail  = prorationPolicyError.Detail,
            });
        var policy = prorationPolicy!;
        // The company's own Payroll rules (Setup -> Payroll rules). A rule HR has set wins; where it chose nothing the
        // rate-policy chain above is unchanged, so a company that never opens the screen is paid exactly as before.
        var companyPayrollRules = await CompanyPayrollRules.ResolveAsync(_db, tenantId, company.Id, cancellationToken, periodStart);
        if (companyPayrollRules.ProrationBasis is { } chosenProrationBasis
            && ProrationBases.Normalize(chosenProrationBasis) is { } canonicalProrationBasis)
            policy = policy with { Basis = canonicalProrationBasis, BasisSource = "company payroll rule" };

        // POD-C3 — the eligible set is now the Active set UNIONED with this period's leavers. The Active
        // half is verbatim, so a tenant with no offboarding in flight has a bit-for-bit identical
        // population. See LoadEligibleWithLeaversAsync for the silent-unpay defect this closes.
        // POD-C1 — a settlement-purpose run additionally admits leavers whose wage side is already DONE
        // (see the settlement branch in LoadEligibleWithLeaversAsync); no other run type is affected.
        var (eligibleEmployees, leaverEmployeeIds, lastWorkingDayByEmp, unpayableNonActive, settlementEligibleIds) =
            await LoadEligibleWithLeaversAsync(tenantId, company.Id, allowLegacyUnscopedEmployees,
                asNoTracking: false, periodStart, periodEnd, excludeRunId: id, cancellationToken,
                includeSettlementLeavers: run.SettlesFinalSettlements);

        // ── POD-B2: the operator's include/exclude intent decides who this run pays ──────────────────
        var runPopulation = await ResolveRunPopulationAsync(tenantId, run, eligibleEmployees, cancellationToken);

        // A non-Regular run over the WHOLE company is almost always operator error: per the supplemental
        // rules below it would consume the period's bonuses and adjustments out from under the Regular
        // run. Require the population to be stated explicitly. POST runs/{id}/selection with
        // allEligible=true materialises "everyone" in one call and still records who was intended.
        // POD-B3 — the gate applies to the SUPPLEMENTAL types only. A Replacement is period-OWNING (it IS
        // the month), so "everyone eligible" is the correct default exactly as it is for a Regular run;
        // requiring an explicit selector would make recovery of a month whose parent produced no slips
        // impossible. CreateRun still materialises the replaced run's paid population as Include rows when
        // there is one, so a hold-out survives recovery.
        if (PayrollRunTypes.RequiresExplicitPopulation(run.RunType) && runPopulation.Mode != "AllowList")
            return UnprocessableEntity(new
            {
                error   = "run_population_required",
                message = $"A '{run.RunType}' run must state its population explicitly. POST /api/payroll/runs/{run.Id}/selection " +
                          "with mode='Include' and the employee ids (or allEligible=true for the whole entity) before processing.",
                runType = run.RunType,
            });

        var employees = runPopulation.Employees;

        // ── POD-C3: EACH EMPLOYEE'S EMPLOYMENT WINDOW INSIDE THIS PERIOD ─────────────────────────────
        // Computed BEFORE employeeIdsForRun is built, because an employee who had not joined yet is
        // EXCLUDED from the run entirely — a zero payslip and an empty GL group are not a truthful
        // representation of "not employed", and marking their attendance/leave impacts Processed would
        // starve the run that finally does pay them.
        //
        // For an employee with JoiningDate before the period and no offboarding the factor is exactly
        // 1.0 and every emitted line below is byte-identical to pre-C3. That is the ~55-tenant bar.
        var prorationByEmp = new Dictionary<int, ProrationResult>();
        foreach (var e in employees)
        {
            var joined = e.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(e.JoiningDate);
            lastWorkingDayByEmp.TryGetValue(e.Id, out var lwdValue);
            var lwd = lastWorkingDayByEmp.ContainsKey(e.Id) ? lwdValue : (DateOnly?)null;
            prorationByEmp[e.Id] = ProrationCalculator.Compute(periodStart, periodEnd, joined, lwd, policy.Basis, workWeekConfig);
        }
        var excludedJoiners = employees.Where(e => prorationByEmp[e.Id].IsExcluded).ToList();
        if (excludedJoiners.Count > 0)
        {
            // Reported, never silent — an unexplained absence from a run is exactly how the notice-period
            // unpay defect hid for so long.
            foreach (var e in excludedJoiners)
            {
                var pr = prorationByEmp[e.Id];
                // POD-C3-FIX — the reason must name the ACTUAL cause. The old text branched on
                // `pr.PaidTo < pr.PaidFrom`, which is ALSO true for a post-period joiner
                // (ProrationCalculator.cs:192-193 leaves paidTo = periodEnd when there is no last working
                // day, so paidFrom = the joining date is already past it). The post-period branch was
                // therefore unreachable, and operators were told "last day 2026-06-30" — the period end,
                // dressed up as a real last working day — about someone who had simply not started yet.
                var joinedOn = e.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(e.JoiningDate);
                var reason = joinedOn is DateOnly jd && jd > periodEnd
                    ? $"Employment window is empty for {run.Year}-{run.Month:D2}: joins {jd:yyyy-MM-dd}, which is after " +
                      $"this period ends ({periodEnd:yyyy-MM-dd}). Nothing is owed for {run.Year}-{run.Month:D2}."
                    : $"Employment window is empty for {run.Year}-{run.Month:D2}: the last working day " +
                      $"({pr.PaidTo:yyyy-MM-dd}) precedes the first day of employment in this period " +
                      $"({pr.PaidFrom:yyyy-MM-dd}). Check the joining date and the offboarding record.";
                runPopulation.NotEligible.Add(new PayrollRunExclusion(e.Id, e.EmployeeCode, e.FullName, reason));
                employees.Remove(e);
            }
        }

        var employeeIdsForRun = employees.Select(e => e.Id).ToHashSet();
        if (employeeIdsForRun.Count == 0)
        {
            // Refusing here is correct — there is no such thing as a payroll run over nobody — but a tenant
            // that has not added anyone yet reaches this on its very first Process, so the 422 body is a
            // user-facing empty state, not an error trace. PayrollPage renders `message` verbatim
            // (PayrollPage.tsx processRun), so it names the company, the period, and the one next action;
            // `error` stays the stable machine code it always was.
            var periodLabel = $"{System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(run.Month)} {run.Year}";
            var setAsideCount = runPopulation.Exclusions.Count + runPopulation.NotEligible.Count;
            return UnprocessableEntity(new
            {
                error = "no_company_employees",
                message = runPopulation.Mode != "AllEligible"
                    ? $"There are no active employees to pay in '{company.LegalNameEn}' for {periodLabel}: this run's include/exclude selection leaves nobody in it. Include at least one active employee, then process the run again."
                    : setAsideCount > 0
                        ? $"There are no active employees to pay in '{company.LegalNameEn}' for {periodLabel}. {setAsideCount} employee(s) were set aside for this period — the run's population panel lists each one and why. Correct those records, then process the run again."
                        : $"There are no active employees to pay in '{company.LegalNameEn}' for {periodLabel}. Add employees to this company and give them a salary, then process the run again.",
                companyId = company.Id,
                period = periodLabel,
                setAsideCount,
            });
        }

        var salaryAssignments = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && x.EffectiveDate <= periodEnd && employeeIdsForRun.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);

        // ── POD-C3: a LEAVER's salary row survives the offboarding exit cascade ───────────────────────
        // OffboardingController.Complete → EmployeeManagementService.DeactivatePayrollFootprintAsync sets
        // EmployeeSalaryStructure.IsActive = false once FinalSettlementDone. The query above filters
        // x.IsActive, so a leaver re-included by the union would silently fall back to `e.Salary ?? 0m`
        // and be paid a wrong (usually zero) final wage. A SECOND query, applied ONLY to the leaver set,
        // fixes that without touching the primary query for anyone else.
        var leaverIdsForRun = leaverEmployeeIds.Where(employeeIdsForRun.Contains).ToList();
        if (leaverIdsForRun.Count > 0)
        {
            var knownAssignmentIds = salaryAssignments.Select(a => a.Id).ToHashSet();
            var leaverAssignments = await _db.EmployeeSalaryStructures.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.EffectiveDate <= periodEnd && leaverIdsForRun.Contains(x.EmployeeId))
                .ToListAsync(cancellationToken);
            salaryAssignments.AddRange(leaverAssignments.Where(a => !knownAssignmentIds.Contains(a.Id)));
        }

        // Load salary structure components (for IsTaxable-based tax deduction)
        var structureIds = salaryAssignments.Select(x => x.SalaryStructureId).Distinct().ToList();
        var salaryComponents = await _db.SalaryComponents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SalaryStructureId.HasValue && structureIds.Contains(x.SalaryStructureId!.Value))
            .ToListAsync(cancellationToken);

        // FIX 3 (program P12): the effective income-tax rate for THIS legal entity is resolved
        // via CompanyTaxPolicyResolver — company override → tenant default (server-derived
        // company.Id, never client input). The legacy SystemSettings ["Payroll"/"IncomeTaxRate"]
        // magic key is now a LOGGED DEPRECATION FALLBACK, used only when no CompanyTaxPolicy is
        // configured, so payroll actually enforces the per-company policy instead of ignoring it.
        var taxPolicy = await _taxResolver.ResolveAsync(tenantId, company.Id, periodEnd, cancellationToken);
        decimal incomeTaxRate;
        if (taxPolicy?.IncomeTaxRatePercent is decimal policyRate)
        {
            incomeTaxRate = policyRate;
        }
        else
        {
            // F2 — EXPLICITLY tenant-scoped, like ResolveUseComponentEngineAsync. This read relied on the
            // ambient global query filter alone; wherever that filter is not bound to the request tenant (the
            // integration fixture, any non-HTTP caller) FirstOrDefault returned ANOTHER tenant's rate and
            // taxed this tenant's payroll with it.
            var taxRateSetting = await _db.SystemSettings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Category == "Payroll" && x.SettingKey == "IncomeTaxRate")
                .Select(x => x.SettingValue)
                .FirstOrDefaultAsync(cancellationToken);
            decimal.TryParse(taxRateSetting, out incomeTaxRate); // 0 if unset
            if (incomeTaxRate > 0m)
                Console.WriteLine($"[Payroll] DEPRECATION: tenant {tenantId} income tax resolved from the legacy SystemSettings magic key (no CompanyTaxPolicy). Migrate to a CompanyTaxPolicy for company {company.Id}.");
        }
        // A RUN MUST NOT CONSUME WHAT IT DOES NOT PAY.
        //
        // "Overtime payable" impacts were loaded here, matched NEITHER money bucket below (the
        // short-hours bucket wants "deduction", the LOP bucket wants "Absence"), and were then flipped
        // to "Processed" with the rest of the list at the end of the run. The `Status != "Processed"`
        // predicate on this very query then hid them from every future run: attendance-derived
        // overtime was silently destroyed, unpaid, by the first run that covered its period. The
        // consumption witness even records Amount = 0m for it, so the audit trail could not show the
        // loss either.
        //
        // Overtime is PAID from OvertimePayrollImpacts, which are created only when an overtime
        // REQUEST is approved (OvertimeController). The bridge from attendance into that path is the
        // explicit POST /api/overtime/detect-from-attendance. Until a product decision says attendance
        // overtime should pay automatically, the correct behaviour is to leave those rows untouched and
        // still claimable — not to swallow them. Excluding them here is what makes that true, because
        // this list is exactly what the end of the run marks Processed.
        var attendanceImpacts = await _db.AttendancePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.WorkDate >= periodStart && x.WorkDate <= periodEnd
                     && x.Status != "Processed"
                     && x.ImpactType != AttendanceImpactTypes.OvertimePayable
                     && employeeIdsForRun.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);
        var leaveImpacts = await _db.LeavePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayPeriod == $"{run.Year}-{run.Month:00}" && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);

        // ── ERR_ATTENDANCE_IMPACT_STALE: never deduct attendance impacts that no longer match their days ─────────
        // This run is about to deduct the pending impacts loaded above exactly as they stand. One written by an older
        // rule (an Out-only day's 480-minute absence, a late deduction on an In-only day), left behind by an undone run,
        // or dated by a time zone or roster that has since changed would be deducted wrongly, and nothing downstream
        // compares it with the day. Refuse instead, name the days, and offer the recompute of exactly those days.
        if (run.IncludesRecurringPay)
        {
            var staleAttendance = await AttendanceImpactReconciliation.FindStaleDaysAsync(
                _db, tenantId, employeeIdsForRun, periodStart, periodEnd, id, cancellationToken);
            if (staleAttendance.Count > 0)
                return UnprocessableEntity(StaleAttendanceRefusal(run, staleAttendance, employees));
        }

        // COMPLIANCE: Load active loans and salary advances per employee for EMI deduction
        var loanCompanyId = company.Id;
        var activeLoans = await Op(_db.EmployeeLoans).AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CompanyId == loanCompanyId && !l.IsDeleted && l.CollectionStatus != "OnHold" && l.RepaymentMethod == "PayrollDeduction" && (l.Status == "Active" || l.Status == "Overdue") && l.EmployeeIntId != null && employeeIdsForRun.Contains(l.EmployeeIntId.Value) && l.OutstandingBalance > 0
                && (!l.RepaymentStartDate.HasValue || l.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);
        var activeAdvances = await Op(_db.SalaryAdvances).AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null && employeeIdsForRun.Contains(a.EmployeeIntId.Value) && a.OutstandingBalance > 0
                && (!a.RepaymentStartDate.HasValue || a.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);

        var approvedAdjustments = await _db.PayrollAdjustments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.PayrollRunId == id && a.Status == "Approved" && employeeIdsForRun.Contains(a.EmployeeId))
            .ToListAsync(cancellationToken);

        // ── POD-B2: sibling runs for this (company, year, month) ─────────────────────────────────────
        // Everything below short-circuits to today's exact behaviour when there are none, which is the
        // case for every run in every tenant before B2 — so a Regular run is provably unchanged.
        var siblingRuns   = await LoadSiblingRunsAsync(tenantId, run, company.Id, cancellationToken);
        var siblingRunIds = siblingRuns.Select(r => r.Id).ToList();

        // (M2) Employees already paid RECURRING salary this period by another non-voided run. Feeds the
        // Error-severity ALREADY_PAID_THIS_PERIOD rule — the real cross-run double-pay control.
        var recurringSiblingIds = siblingRuns.Where(r => r.IncludesRecurringPay).Select(r => r.Id).ToList();
        var alreadyPaidRecurringEmpIds = recurringSiblingIds.Count == 0
            ? new HashSet<int>()
            : (await Op(_db.PayrollSlips).AsNoTracking()
                .Where(s => s.TenantId == tenantId && recurringSiblingIds.Contains(s.RunId) && employeeIdsForRun.Contains(s.EmployeeId))
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();

        // (M8) Period-to-date statutory position from sibling runs, per employee.
        //
        // The statutory ceiling (GOSI covered wage = basic + housing, capped) is a PERIOD concept: computing
        // a supplemental run against a zero base applies the ceiling to the supplemental amount alone and
        // over-deducts near the cap, while ignoring siblings entirely would double-deduct the monthly wage.
        // Both are wrong. Instead the run is computed INCREMENTALLY: the pack is fed
        // (period-to-date covered wage + this run's own amounts), the ceiling therefore applies to the
        // period total, and the amounts sibling runs already deducted are netted off per line code
        // (floored at zero). Exact, because SalaryBreakdown.GosiCoveredWage == Basic + HousingAllowance and
        // both components are recoverable: the recurring halves from the sibling slips' own columns, the
        // GOSI-eligible bonus half by re-reading the bonuses those runs consumed.
        var priorBasicByEmp   = new Dictionary<int, decimal>();
        var priorHousingByEmp = new Dictionary<int, decimal>();
        var priorStatutoryByEmp = new Dictionary<(int EmployeeId, string Code, bool IsEmployer), decimal>();
        if (siblingRunIds.Count > 0)
        {
            var siblingSlips = await Op(_db.PayrollSlips).AsNoTracking()
                .Where(s => s.TenantId == tenantId && siblingRunIds.Contains(s.RunId) && employeeIdsForRun.Contains(s.EmployeeId))
                .Select(s => new
                {
                    s.EmployeeId, s.BasicSalary, s.HousingAllowance,
                    // POD-C3 — the sibling's own statutory base, which under `proration_gosi_base =
                    // FullMonth` is NOT its money columns. Null on every pre-C3 slip, so the fallback
                    // below reproduces the pre-C3 read exactly.
                    s.GosiBasePolicy, s.FullBasicSalary, s.FullHousingAllowance,
                })
                .ToListAsync(cancellationToken);
            foreach (var s in siblingSlips)
            {
                var useFull = s.GosiBasePolicy == ProrationGosiBases.FullMonth;
                var sBasic   = useFull ? s.FullBasicSalary      ?? s.BasicSalary      : s.BasicSalary;
                var sHousing = useFull ? s.FullHousingAllowance ?? s.HousingAllowance : s.HousingAllowance;
                priorBasicByEmp[s.EmployeeId]   = priorBasicByEmp.GetValueOrDefault(s.EmployeeId) + sBasic;
                priorHousingByEmp[s.EmployeeId] = priorHousingByEmp.GetValueOrDefault(s.EmployeeId) + sHousing;
            }
            // GOSI-eligible bonus already covered by a sibling run rides in the housing slot, exactly as
            // this run's own gosiIncludedBonusTotal does below.
            var siblingBonuses = await Op(_db.EmployeeBonuses).AsNoTracking()
                .Where(b => b.TenantId == tenantId && !b.IsDeleted && b.PayrollRunId != null
                         && siblingRunIds.Contains(b.PayrollRunId!.Value)
                         && b.EmployeeIntId != null && employeeIdsForRun.Contains(b.EmployeeIntId!.Value))
                .Select(b => new { b.EmployeeIntId, b.BonusTypeId, b.GrossBonusAmount })
                .ToListAsync(cancellationToken);
            if (siblingBonuses.Count > 0)
            {
                var siblingBonusTypeIds = siblingBonuses.Select(b => b.BonusTypeId).Distinct().ToList();
                var gosiEligibleTypeIds = (await _db.BonusTypes.AsNoTracking()
                    .Where(t => t.TenantId == tenantId && siblingBonusTypeIds.Contains(t.Id) && t.IsIncludedInGosiBase)
                    .Select(t => t.Id)
                    .ToListAsync(cancellationToken)).ToHashSet();
                foreach (var b in siblingBonuses)
                {
                    if (!gosiEligibleTypeIds.Contains(b.BonusTypeId)) continue;
                    var eid = b.EmployeeIntId!.Value;
                    priorHousingByEmp[eid] = priorHousingByEmp.GetValueOrDefault(eid) + b.GrossBonusAmount;
                }
            }
            // ── POD-C3 (MF-1) — GOSI-BEARING ARREARS A SIBLING RUN ALREADY SETTLED ───────────────────
            // This is the single most dangerous omission a proration/arrears pod can make. Arrears may be
            // settled by a Supplementary/OffCycle run (B2's natural vehicle for a retro increment), and
            // the A1 period reconciliation is taught to include them. If they are NOT also added to the
            // period-to-date base here, the pack is fed an understated base, the 45,000 ceiling nets
            // wrong, the incremental netting below is wrong — and ReconcilePeriodAsync then computes
            // `expected` on base+arrears against an `actual` computed WITHOUT them. `expected == actual`
            // fails for every affected employee, silently, on a STATUTORY FILING source. Mirrors the
            // sibling-bonus block above exactly, and rides the SAME housing slot.
            var siblingArrears = await Op(_db.PayrollArrearsLines).AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.Status == PayrollArrearsStatuses.Settled && a.IsGosiBearing
                         && a.PayrollRunId != null && siblingRunIds.Contains(a.PayrollRunId!.Value)
                         && employeeIdsForRun.Contains(a.EmployeeId))
                .Select(a => new { a.EmployeeId, a.Amount })
                .ToListAsync(cancellationToken);
            foreach (var a in siblingArrears)
                priorHousingByEmp[a.EmployeeId] = priorHousingByEmp.GetValueOrDefault(a.EmployeeId) + a.Amount;
            var siblingStatutory = await Op(_db.PayrollDeductions).AsNoTracking()
                .Where(d => d.TenantId == tenantId && siblingRunIds.Contains(d.PayrollRunId)
                         && d.Source == "Statutory" && employeeIdsForRun.Contains(d.EmployeeId))
                .Select(d => new { d.EmployeeId, d.ComponentCode, d.IsEmployerContribution, d.Amount })
                .ToListAsync(cancellationToken);
            foreach (var d in siblingStatutory)
            {
                var key = (d.EmployeeId, d.ComponentCode, d.IsEmployerContribution);
                priorStatutoryByEmp[key] = priorStatutoryByEmp.GetValueOrDefault(key) + d.Amount;
            }
        }

        // POD-B2 — does this run pay recurring salary? Persisted on the run (M2), not derived from its
        // type, so an OffCycle run for a MISSED JOINER can pay a full salary while a mid-month bonus run
        // pays only the bonus. Regular runs are always true, so their emission path is byte-identical.
        var includesRecurringPay = run.IncludesRecurringPay;

        // BONUS: Load approved bonuses for this pay period — consumed here, blocked from MarkBatchPaid.
        var periodStr = $"{run.Year}-{run.Month:D2}";
        var pendingBonuses = await Op(_db.EmployeeBonuses)
            .Where(x => x.TenantId == tenantId && !x.IsDeleted
                && x.Status == "Approved"
                && x.PaymentPeriod == periodStr
                && x.PayrollRunId == null
                && x.EmployeeIntId != null
                && employeeIdsForRun.Contains(x.EmployeeIntId.Value))
            .ToListAsync(cancellationToken);
        var bonusTypeIds = pendingBonuses.Select(b => b.BonusTypeId).Distinct().ToList();
        var bonusTypeMap = bonusTypeIds.Count > 0
            ? await _db.BonusTypes.AsNoTracking()
                .Where(t => t.TenantId == tenantId && bonusTypeIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, cancellationToken)
            : new Dictionary<Guid, BonusType>();
        var bonusesByEmployee = pendingBonuses
            .Where(b => b.EmployeeIntId.HasValue)
            .GroupBy(b => b.EmployeeIntId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // COMPLIANCE: YTD — locked runs earlier in the year (POD-B2: plus any other locked run in this same
        // month) and the carried opening balances, PARTITIONED at the legal entity's cutover month so no
        // period is counted twice: before the cutover the opening balance already contains every month,
        // including any month this product also ran. See PayrollYtdBasis.
        var ytdSources = await PayrollYtdBasis.LoadAsync(
            _db, tenantId, company.Id, run, employeeIdsForRun, cancellationToken);
        var ytdSlips = ytdSources.PriorSlips;
        var openingBalancesByEmployee = ytdSources.OpeningBalancesByEmployee;

        // COMPLIANCE: Load payroll profiles for MolId / RoutingCode (keyed by Employee.Id)
        var payrollProfiles = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && employeeIdsForRun.Contains(p.EmployeeId))
            .ToDictionaryAsync(p => p.EmployeeId, cancellationToken);

        // C4: filter overtime impacts to the current pay period only (via WorkDate on the originating request)
        // S1/A5 — the WorkDate is now KEPT, not discarded. It is what lets the run tell a rest-day or
        // public-holiday overtime hour from an ordinary one, which is the distinction that made
        // ot.restday_multiplier and ot.holiday_multiplier dead rules: the seeder wrote them and nothing
        // in the payroll run could ever have read them, because the run did not know the day type.
        var periodOvertimeRequests = await Op(_db.OvertimeRequests).AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.CompanyId == company.Id || (allowLegacyUnscopedEmployees && r.CompanyId == null)) && r.WorkDate >= periodStart && r.WorkDate <= periodEnd)
            .Select(r => new { r.Id, r.WorkDate })
            .ToListAsync(cancellationToken);
        var periodOvertimeRequestIds = periodOvertimeRequests.Select(r => r.Id).ToList();
        var overtimeWorkDates = periodOvertimeRequests.ToDictionary(r => r.Id, r => r.WorkDate);
        // The company's own holidays (active calendars, company-wide or this company's), read by the same function attendance uses.
        var periodPublicHolidays = await CompanyPayrollRules.HolidayDatesAsync(_db, tenantId, company.Id, periodStart, periodEnd, cancellationToken);
        var overtimeImpacts = await _db.OvertimePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId) && periodOvertimeRequestIds.Contains(x.OvertimeRequestId))
            .ToListAsync(cancellationToken);

        // L1: use policy-configured monthly hours as divisor; fall back to 240.
        // The SAME policy row also carries the tenant's configured OT hourly BASIS. The run used to
        // read only the divisor off it, so a tenant on HourlyRateBasis = FixedHourlyRate (or
        // GrossSalary in a basic-base jurisdiction) was shown one overtime rate by OvertimeController
        // and paid a different one here. Both now resolve the hour through
        // OvertimeStatutoryCalculator.ResolveHourRate with these same two fields.
        var otPolicy = await OvertimePolicyDefault.Order(_db.OvertimePolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.IsActive && !p.IsDeleted))
            .Select(p => new { p.StandardMonthlyHours, p.HourlyRateBasis, p.FixedHourlyRate })
            .FirstOrDefaultAsync(cancellationToken);
        var standardMonthlyHours = otPolicy?.StandardMonthlyHours ?? 240;
        var otPolicyBasis = otPolicy?.HourlyRateBasis;
        var otPolicyFixedHourlyRate = otPolicy?.FixedHourlyRate ?? 0m;

        // ── OT/LOP statutory rules from country pack config ───────────────────
        // Read from StatutoryRule table (tenant-overridable).  Fallback defaults are
        // directional KSA values — FLAG FOR COMPLIANCE SIGN-OFF before production filing.
        var eff = new DateOnly(run.Year, run.Month, 1);
        // ── S1/A5 + the OT hourly BASE, resolved by the SHARED context ───────────────────────────
        // KSA Labour Law Art. 107: the worker is paid for an overtime hour "an additional amount equal
        // to the hourly wage plus 50% of his basic wage". The base is the WAGE (Art. 2: basic plus all
        // due increments); the 50% uplift is on BASIC. Pre-S1 the run computed basic/240 × 1.5 for
        // everyone — on a 60/40 package that is about 30% short on every KSA overtime hour — and the
        // seeded ot.restday_multiplier / ot.holiday_multiplier rates were read by nothing at all.
        //
        // The general form covers all three jurisdictions with one expression:
        //     hour pay = baseHourly + basicHourly × (multiplier − 1)
        //   KSA  base = "wage":  wageHourly + 0.5 × basicHourly           (Art. 107)
        //   UAE  base = "basic": basicHourly × 1.25                        (basic + 25%)
        //   QAT  base = "basic": basicHourly × 1.25                        (Art. 74, +25% minimum)
        // With base = "basic" the expression collapses to basicHourly × multiplier, i.e. EXACTLY the
        // pre-S1 arithmetic — so nothing moves for UAE or Qatar.
        //
        // These five rule keys, their fallbacks and the arithmetic they feed now live in
        // OvertimeStatutoryContext / OvertimeStatutoryCalculator and are shared verbatim with
        // OvertimeController, which used to duplicate the calculation on the pre-S1 basis and
        // therefore reported a different number for the same overtime hour.
        // [FLAG-COMPLIANCE-KSA: weekend/holiday OT multipliers may differ — Art.107 baseline only]
        var otContext = await _ruleReader.ResolveAsync(packCc, packJur, eff, tenantId, cancellationToken, companyId: company.Id);
        var otMultiplier = otContext.StandardMultiplier;
        // The company's own rest-day / holiday premium wins where HR set one; otherwise the rule value that already applied.
        var otRestDayMultiplier = companyPayrollRules.RestDayMultiplier ?? otContext.RestDayMultiplier;
        var otHolidayMultiplier = companyPayrollRules.HolidayMultiplier ?? otContext.HolidayMultiplier;
        var otBaseIsFullWage = otContext.BaseIsFullWage;
        // Hourly-rate divisor: the statutory hours, unless the company's OT policy is more generous.
        standardMonthlyHours = OvertimeStatutoryContextResolver.EffectiveMonthlyHours(standardMonthlyHours, otContext.StandardMonthlyHoursOverride);
        // LOP day-rate divisor: basic ÷ lopDayDivisor per absent day.
        // [FLAG-COMPLIANCE-KSA: basic/30 is common KSA practice but court precedent varies — VERIFY]
        var lopDayDivisor = (int)(await _ruleReader.GetDecimalAsync(
            packCc, packJur, "lop.monthly_day_divisor", eff, tenantId, cancellationToken, companyId: company.Id) ?? 30m);
        // Standard work minutes per day: used to convert absence minutes to LOP days.
        // [FLAG-COMPLIANCE-KSA: 480 min (8h); adjust for Ramadan or sector shift patterns]
        var lopStdMinutesPerDay = (int)(await _ruleReader.GetDecimalAsync(
            packCc, packJur, "lop.standard_work_minutes_per_day", eff, tenantId, cancellationToken, companyId: company.Id) ?? 480m);

        // ── Attendance: employees who had daily records processed in this period ──
        // Used in Rule 10 (WARN_NO_ATTENDANCE) to detect employees skipped by attendance system.
        var attendanceProcessedEmpIds = (await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.WorkDate >= periodStart && r.WorkDate <= periodEnd && employeeIdsForRun.Contains(r.EmployeeId))
            .Select(r => r.EmployeeId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        // ── Configurability keystone: the data-driven pay-component set for this run ─────────────
        // The pay engine replaces the fixed compiled earning/deduction sequence below with a company-first
        // seeded catalog (PayComponent). Loaded ONCE per run (company is fixed for the run) as an immutable
        // read-only snapshot, safe to reuse across an execution-strategy retry. Empty store ⇒ compiled
        // PayComponentCatalog fallback (mirrors the gl_drivers store), so an un-seeded tenant is unaffected.
        // Payroll/UseComponentEngine is a per-tenant kill-switch (default ON): the engine is proven
        // byte-identical to the legacy inline block by the golden-master + equivalence-twin tests and never
        // changes any output, so "false" only exists for instant per-tenant rollback to the legacy path.
        var useComponentEngine = await ResolveUseComponentEngineAsync(tenantId, cancellationToken);
        var payComponents = useComponentEngine
            ? await LoadPayComponentsAsync(tenantId, company.Id, periodStart, cancellationToken)
            : (IReadOnlyList<PayComponent>)Array.Empty<PayComponent>();

        // ════ POD-C3 ══════════════════════════════════════════════════════════════════════════════
        // Everything from here to the transaction is INERT for a run with no joiner, no leaver and no
        // backdated salary change: prorationByEmp is all 1.0, the arrears engine returns zero lines, and
        // every warning list is empty.
        // ══════════════════════════════════════════════════════════════════════════════════════════

        // Statutory covered-wage ceiling — used ONLY for the arrears EARNED-BASIS delta, never to
        // duplicate the pack's own capping (which stays inside KsaDeductionCalculator).
        var statutoryCeiling = await _ruleReader.GetDecimalAsync(
            packCc, packJur, "gosi.covered_wage_ceiling_sar", eff, tenantId, cancellationToken) ?? decimal.MaxValue;

        var c3Warnings = new List<(string Code, int? EmployeeId, string Message)>();
        if (!policy.IsEnabled)
            c3Warnings.Add(("WARN_PRORATION_DISABLED", null,
                "Mid-month proration is DISABLED for this legal entity (payparameter.proration_basis = 'None'). " +
                "A joiner or leaver is being paid a FULL month. This warning fires on every run so the choice " +
                "can never be silently forgotten."));
        if (policy.Basis == ProrationBases.Calendar30 && lopDayDivisor != ProrationCalculator.Calendar30Days)
            c3Warnings.Add(("WARN_PRORATION_BASIS_MISMATCH", null,
                $"Proration uses a 30-day month but unpaid absence is charged at basic ÷ {lopDayDivisor} " +
                "(lop.monthly_day_divisor). Two different day-rates on one payslip: a joiner's absent day is " +
                "valued differently from their unworked days. Align the two before filing."));

        // ── MF-6(b): unpaid-leave days RECOMPUTED against the employment window ──────────────────────
        // LeavePayrollImpact carries NO dates (only PayPeriod/Days/Amount) and its Amount was SNAPSHOTTED
        // at approval on the FULL basic ÷ 30. For a prorated joiner/leaver that snapshot can exceed the
        // wage actually earned. The originating LeaveRequest does carry dates, so the impact is scaled by
        // the share of the request that falls inside [PaidFrom, PaidTo]. Scale is 1.0 — and the code path
        // therefore a no-op — for every employee who was employed for the whole period.
        var leaveImpactScale = new Dictionary<Guid, decimal>();
        var proratedEmpIds = employees.Where(e => prorationByEmp[e.Id].IsProrated).Select(e => e.Id).ToHashSet();
        if (proratedEmpIds.Count > 0 && leaveImpacts.Count > 0)
        {
            var reqIds = leaveImpacts.Where(x => proratedEmpIds.Contains(x.EmployeeId)).Select(x => x.LeaveRequestId).Distinct().ToList();
            var reqs = reqIds.Count == 0 ? new List<LeaveRequest>() : await Op(_db.LeaveRequests).AsNoTracking()
                .Where(r => r.TenantId == tenantId && reqIds.Contains(r.Id)).ToListAsync(cancellationToken);
            foreach (var imp in leaveImpacts.Where(x => proratedEmpIds.Contains(x.EmployeeId)))
            {
                var req = reqs.FirstOrDefault(r => r.Id == imp.LeaveRequestId);
                if (req is null) continue;
                var pr = prorationByEmp[imp.EmployeeId];
                var reqDays = req.EndDate.DayNumber - req.StartDate.DayNumber + 1;
                if (reqDays <= 0) continue;
                var from = req.StartDate > pr.PaidFrom ? req.StartDate : pr.PaidFrom;
                var to   = req.EndDate   < pr.PaidTo   ? req.EndDate   : pr.PaidTo;
                var inside = to < from ? 0 : to.DayNumber - from.DayNumber + 1;
                var scale = Math.Round(inside / (decimal)reqDays, 6);
                if (scale >= 1m) continue;
                leaveImpactScale[imp.Id] = scale;
                c3Warnings.Add(("WARN_IMPACT_OUTSIDE_EMPLOYMENT_WINDOW", imp.EmployeeId,
                    $"Unpaid-leave request {req.StartDate:yyyy-MM-dd}→{req.EndDate:yyyy-MM-dd} extends outside the " +
                    $"employment window {pr.PaidFrom:yyyy-MM-dd}→{pr.PaidTo:yyyy-MM-dd}. The deduction was reduced to " +
                    $"{scale:P2} of the approved amount rather than charged against days the employee was not employed."));
            }
        }

        // ── THE ARREARS ENGINE ──────────────────────────────────────────────────────────────────────
        var arrears = ArrearsComputation.Empty;
        if (run.SettlesArrears)
        {
            var arrearsEmployees = employees.Select(e => new ArrearsEmployee(
                e.Id, e.EmployeeCode, e.FullName,
                e.JoiningDate == default ? null : DateOnly.FromDateTime(e.JoiningDate),
                lastWorkingDayByEmp.TryGetValue(e.Id, out var l) ? l : null)).ToList();
            arrears = await new ArrearsEngine(_db).ComputeAsync(
                tenantId, company.Id, run, arrearsEmployees, salaryAssignments,
                policy, workWeekConfig, statutoryCeiling, cancellationToken);

            // ── NEGATIVE ARREARS (a retro DECREASE) — computed, persisted, and REFUSED ───────────────
            // B2's posture, verbatim: correction runs are ADDITIVE-ONLY and a negative delta (clawback)
            // has no vehicle here. Netting it would break WPS and the control accounts; dropping it with
            // an `if (amount > 0)` guard would leave the employee permanently overpaid and INVISIBLE.
            // The lines are persisted as PendingRecovery so the amount and its covered period survive the
            // refusal, then the run refuses with a specific 422 naming every employee and period.
            if (arrears.PendingRecovery.Count > 0)
            {
                foreach (var line in arrears.PendingRecovery)
                {
                    var exists = await Op(_db.PayrollArrearsLines).AnyAsync(a => a.TenantId == tenantId
                        && a.EmployeeId == line.EmployeeId && a.CoveredYear == line.CoveredYear
                        && a.CoveredMonth == line.CoveredMonth && a.ComponentCode == line.ComponentCode
                        && a.Status == PayrollArrearsStatuses.PendingRecovery, cancellationToken);
                    if (!exists) _db.PayrollArrearsLines.Add(line);
                }
                await _db.SaveChangesAsync(cancellationToken);
                return UnprocessableEntity(new
                {
                    error   = "retro_decrease_unsupported",
                    message = $"{arrears.PendingRecovery.Select(l => l.EmployeeId).Distinct().Count()} employee(s) have a " +
                              "BACKDATED SALARY DECREASE affecting an already-locked period. A payroll run is " +
                              "ADDITIVE-ONLY — a negative earning has no vehicle here and would break the wage file and " +
                              "the control accounts. The amounts have been recorded as PendingRecovery (visible on " +
                              $"GET /api/payroll/runs/{run.Id}/arrears) so nothing is lost. Recover them through an " +
                              "explicit deduction, or correct the effective date of the salary change, then re-process.",
                    employees = arrears.PendingRecovery
                        .GroupBy(l => l.EmployeeId)
                        .Select(g => new
                        {
                            employeeId = g.Key,
                            employeeCode = g.First().EmployeeCode,
                            periods = g.Select(l => new { period = $"{l.CoveredYear}-{l.CoveredMonth:D2}", l.ComponentCode, l.Amount }).ToList(),
                        }).ToList(),
                });
            }
            foreach (var w in arrears.Warnings) c3Warnings.Add((w.Code, w.EmployeeId, w.Message));
            if (arrears.Settled.Count > 0 && policy.ArrearsAreGosiBearing)
                c3Warnings.Add(("WARN_ARREARS_GOSI_TREATMENT_REQUIRES_SIGNOFF", null,
                    $"This run settles {arrears.TotalGosiBearing:N2} of GOSI-BEARING arrears in the period PAID. " +
                    "Whether a retro increment must instead be declared against the months EARNED (an AMENDED GOSI " +
                    "return) is a statutory question requiring a Saudi compliance officer's sign-off. The earned-basis " +
                    $"figure is {arrears.TotalEarnedBasisGosiDelta:N2} and is persisted per covered period on every " +
                    "arrears line. [FLAG-COMPLIANCE-KSA]"));
        }
        var arrearsByEmp = arrears.Settled.GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        // ── POD-B3 HANDOFF: the 1420 receivable a prior void recognised ──────────────────────────────
        // Explicit per-run flag, default OFF: recovering an overpayment out of someone's salary is an act
        // the operator chooses, never a side effect of re-running a month.
        var receivableByEmp = new Dictionary<int, List<PayrollEmployeeReceivable>>();
        if (run.NetsPriorReceivable)
        {
            var outstanding = await Op(_db.PayrollEmployeeReceivables)
                .Where(r => r.TenantId == tenantId && r.Status == PayrollReceivableStatuses.Outstanding
                         && (r.CompanyId == company.Id || r.CompanyId == null)
                         && employeeIdsForRun.Contains(r.EmployeeId) && r.Amount > r.RecoveredAmount)
                .OrderBy(r => r.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            receivableByEmp = outstanding.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        }

        // ── POD-C1: THE APPROVED SETTLEMENTS THIS RUN DISBURSES ──────────────────────────────────────
        // The run EMITS the persisted FinalSettlementLine rows VERBATIM. It does not recompute a
        // gratuity, a proration, an encashment or a notice figure — that is POD-A2's "compute once"
        // doctrine applied one layer down, and it is what makes the 2320 accrual and the payslip provably
        // the same numbers rather than two implementations that agree today.
        var settlementsByEmp = new Dictionary<int, EmployeeFinalSettlement>();
        var settlementLinesById = new Dictionary<Guid, List<FinalSettlementLine>>();
        if (run.SettlesFinalSettlements)
        {
            // Belt to CreateRun's brace: a run created before this pod (or by a direct write) could carry
            // the settlement purpose alongside a recurring basis, which is the double-pay hazard in §2.4.
            if (includesRecurringPay)
                return UnprocessableEntity(new
                {
                    error   = "settlement_run_pays_recurring",
                    message = "This run is marked as a termination-settlement run but also pays recurring salary. " +
                              "The leaver's wages through their last working day were already paid by the run that " +
                              "produced their final-wage-month payslip; paying them again here would double the wage " +
                              "and charge a second full month of social insurance.",
                    runId = run.Id,
                });

            // AsNoTracking on purpose: this is the READ-ONLY plan, loaded before the execution-strategy
            // transaction opens and therefore safe to reuse across a transient retry (which Clear()s the
            // tracker). The rows are RE-LOADED as tracked copies inside the transaction before mutation,
            // exactly as activeLoansMutable / activeAdvMutable are.
            var approved = await Op(_db.EmployeeFinalSettlements).AsNoTracking()
                .Where(s => s.TenantId == tenantId
                         && s.Status == FinalSettlementStatuses.Approved
                         && s.PayrollRunId == null
                         && employeeIdsForRun.Contains(s.EmployeeId)
                         && (s.CompanyId == company.Id || s.CompanyId == null))
                .OrderBy(s => s.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            foreach (var s in approved)
                if (!settlementsByEmp.ContainsKey(s.EmployeeId)) settlementsByEmp[s.EmployeeId] = s;
            if (settlementsByEmp.Count > 0)
            {
                var settlementIdList = settlementsByEmp.Values.Select(s => s.Id).ToList();
                settlementLinesById = (await _db.FinalSettlementLines.AsNoTracking()
                        .Where(l => l.TenantId == tenantId && settlementIdList.Contains(l.SettlementId))
                        .OrderBy(l => l.SortOrder).ThenBy(l => l.ComponentCode)
                        .ToListAsync(cancellationToken))
                    .GroupBy(l => l.SettlementId)
                    .ToDictionary(g => g.Key, g => g.ToList());
            }
        }

        // ── P0-2: ALL-OR-NOTHING MUTATION ──────────────────────────────────────────
        // Everything from here (idempotent delete → slip writes → statutory/loan/advance
        // ledger decrements → audit) commits as one transaction, so a cancel/crash/DB error
        // mid-run rolls the whole thing back: no payslips persist, the run stays in its prior
        // (Draft/Failed) status, and the loan/advance OutstandingBalance is unchanged — leaving
        // the run fully re-processable. AddDbContextPool + EnableRetryOnFailure requires the
        // whole tx to live inside the execution-strategy delegate; a bare BeginTransactionAsync
        // would throw. Mirrors EstablishmentGuardService.EnforceAndExecuteAsync — the delegate
        // must be safe to re-run from scratch, so every tracked-and-mutated entity (run, the
        // idempotent deletes, loans/advances/installments) is (re)loaded INSIDE after Clear();
        // read-only snapshots loaded above are immutable and safe to reuse across a retry.
        int? failingEmployeeId = null;
        string? failingEmployeeCode = null;
        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
            // Retry-safety: on a transient RETRY, discard the failed attempt's tracked state so the
            // rebuild starts clean. We do NOT clear on the first attempt — that would detach entities
            // the caller is still holding (the request's scoped context), changing observable behaviour.
            if (attempt++ > 0)
                _db.ChangeTracker.Clear();
            // (Re)load the run we mutate. First attempt: returns the already-tracked instance.
            // Retry: re-materialises it after the Clear() above.
            run = await Op(_db.PayrollRuns).FirstAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);

            // The transaction MUST open BEFORE the ExecuteUpdateAsync impact-status writes below,
            // otherwise that raw SQL auto-commits on its own connection and re-opens the split.
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Idempotent delete of previously-generated rows (enrolls in this tx).
            _db.PayrollSlips.RemoveRange(Op(_db.PayrollSlips).Where(s => s.RunId == id && s.TenantId == tenantId));
            _db.PayrollRunEmployees.RemoveRange(_db.PayrollRunEmployees.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            _db.PayrollEarnings.RemoveRange(_db.PayrollEarnings.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            _db.PayrollDeductions.RemoveRange(Op(_db.PayrollDeductions).Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            _db.PayrollValidationResults.RemoveRange(_db.PayrollValidationResults.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            // POD-B3 — a re-Process re-derives what the run consumes, so the previous witnesses are stale
            // by definition. (Reachable only via POST runs/{id}/reopen, which has already REPLAYED them;
            // this is the belt to that brace and keeps the (run, artifact) unique index clean.)
            _db.PayrollRunConsumptions.RemoveRange(Op(_db.PayrollRunConsumptions).Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            // POD-C3 — this run's arrears lines are removed here, i.e. BEFORE the entitlement arithmetic
            // is used. Ordering is load-bearing: leave them and a re-Process reads its OWN previous output
            // as "already settled" and self-cancels every arrears line to zero. (The engine also excludes
            // PayrollRunId == this run explicitly — belt to this brace, because the engine now runs
            // BEFORE the transaction opens.)
            _db.PayrollArrearsLines.RemoveRange(Op(_db.PayrollArrearsLines).Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            // A re-processed run's FACTS have changed, so a judgement recorded about the old figures no
            // longer applies. Overrides are deliberately NOT carried across a re-Process — only across a
            // /validate, which is a read-model refresh of the same facts.
            _db.PayrollValidationOverrides.RemoveRange(Op(_db.PayrollValidationOverrides).Where(x => x.TenantId == tenantId && x.PayrollRunId == id));

        // Benefit execution is part of the payroll transaction and uses only frozen employee terms.
        // The employee locks also serialize receipt approvals and dated HR benefit changes.
        var benefitAdjustments = await BenefitPayroll.StageAsync(_db, run, company.Id, company.DefaultCurrency,
            employees, includesRecurringPay, periodStart, periodEnd, cancellationToken);
        approvedAdjustments.RemoveAll(BenefitPayroll.IsBenefit);
        approvedAdjustments.AddRange(benefitAdjustments);
        var benefitGl = benefitAdjustments.Count > 0
            ? await LoadGlResolutionContextAsync(tenantId, company.Id, cancellationToken) : null;
        PayComponentLine AdjustmentLine(PayrollAdjustment adjustment)
        {
            var witness = BenefitPayroll.Read(adjustment);
            if (witness is null) return new PayComponentLine($"ADJ_{NormalizeCode(adjustment.AdjustmentType)}",
                AdjustmentLabel(adjustment), Math.Abs(adjustment.Amount), "Adjustment", false);
            var code = $"BENEFIT_{NormalizeCode(witness.Component.Code)}";
            var driver = adjustment.Amount > 0
                ? EarningDriverKeyFor(benefitGl!.Drivers, code, "Adjustment")
                : ResolveDeductionDriverRow(benefitGl!.Drivers, code, "Adjustment")?.Key ?? DeductionDriverKey(code, "Adjustment", out _);
            return new PayComponentLine(code, $"{witness.PlanName} — {witness.Component.Name}",
                Math.Abs(adjustment.Amount), "Adjustment", false) { GlDriverKey = driver };
        }

        var slips = new List<PayrollSlip>();
        // POD-B2 — per-attempt accumulators. Declared INSIDE the execution-strategy delegate so a
        // transient retry starts clean (the delegate re-runs from here).
        var negativeNetEmployees = new List<object>();
        var statutoryComputedIncrementally = false;
        // POD-C3 — per-attempt accumulators, declared INSIDE the execution-strategy delegate for the same
        // reason as the two above: a transient retry re-runs the delegate from here and must start clean.
        // The per-loan/per-advance maps carry the amount THIS run actually took (which is no longer
        // Math.Min(Installment, Outstanding) once debt is capped at affordable net), so the mutable
        // decrement block below and the POD-B3 witness both record the truth rather than a recomputation.
        var loanTakenByEmployee = new Dictionary<int, Dictionary<Guid, decimal>>();
        var advTakenByEmployee  = new Dictionary<int, Dictionary<Guid, decimal>>();
        var deferredEmiEmployees = new List<(int Id, string Code, string Name, decimal Amount)>();
        var recurringShortfallEmployees = new List<(int Id, string Code, string Name, decimal Shortfall, bool Prorated, decimal StatutoryEe)>();
        var receivableResidualEmployees = new List<(int Id, string Code, string Name, decimal Amount)>();
        // POD-C3-FIX — recovery taken out of a period OTHER than the one the receivable arose in. That is
        // a genuine deduction from wages (KSA Labour Law art. 91's cap on recovering amounts paid in
        // excess), whereas recovering inside the SAME period is merely declining to pay the same month
        // twice. The two are collected apart so only the former carries a compliance flag.
        var receivableCrossPeriodEmployees = new List<(int Id, string Code, string Name, decimal Amount, string Periods)>();
        var receivableTakenByEmployee = new List<(Guid ReceivableId, int EmployeeId, decimal Amount)>();
        var arrearsLinesToPersist = new List<PayrollArrearsLine>();
        // POD-C1 — per-attempt accumulators for the settlements this run disburses, declared INSIDE the
        // execution-strategy delegate for the same reason as everything above: a transient retry re-runs
        // the delegate from here and must start clean.
        var settlementsDisbursed = new List<EmployeeFinalSettlement>();
        var settlementEncashmentTaken = new List<(Guid BalanceId, int EmployeeId, decimal Days)>();
        var settlementRecoveryReduced = new List<(int Id, string Code, string Name, decimal Planned, decimal Taken)>();
        // One pass over each per-employee source, instead of a full scan per employee (employees x rows).
        // Lookups keep source order, so every per-employee sequence is exactly what the scan produced.
        var salaryByEmp = salaryAssignments.ToLookup(x => x.EmployeeId);
        var attendanceByEmp = attendanceImpacts.ToLookup(x => x.EmployeeId);
        var leaveByEmp = leaveImpacts.ToLookup(x => x.EmployeeId);
        var overtimeByEmp = overtimeImpacts.ToLookup(x => x.EmployeeId);
        var adjustmentsByEmp = approvedAdjustments.ToLookup(a => a.EmployeeId);
        var loansByEmp = activeLoans.ToLookup(l => l.EmployeeIntId);
        var advancesByEmp = activeAdvances.ToLookup(a => a.EmployeeIntId);
        var ytdSlipsByEmp = ytdSlips.ToLookup(x => x.EmployeeId);
        var progress = new ProgressThrottle(employees.Count, hooks?.Progress);
        await progress.StartAsync(cancellationToken);
        foreach (var e in employees)
        {
            failingEmployeeId = e.Id; failingEmployeeCode = e.EmployeeCode;
            await progress.AdvanceAsync(cancellationToken);
            var salary = salaryByEmp[e.Id].Where(x => x.EffectiveDate <= periodEnd).OrderByDescending(x => x.EffectiveDate).FirstOrDefault();
            // ── POD-C3: THE FULL PACKAGE (rate basis) vs THE PRORATED PACKAGE (earning basis) ─────────
            // This split is the single most important correctness point in the pod. `full*` is the
            // employee's MONTHLY RATE and is what every per-unit rate must be derived from; the prorated
            // values are what they are ENTITLED to for the days actually employed. Conflating them
            // under-pays overtime and under-charges absence for every joiner and leaver.
            var fullBasic     = salary?.BasicSalary ?? e.Salary ?? 0m;
            var fullHousing   = salary?.HousingAllowance ?? 0m;
            var fullTransport = salary?.TransportAllowance ?? 0m;
            var fullOther     = (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
            var fullFixedDeduction = salary?.FixedDeduction ?? 0m;

            var proration = prorationByEmp.TryGetValue(e.Id, out var pr0) ? pr0
                : ProrationCalculator.Compute(periodStart, periodEnd, null, null, policy.Basis, workWeekConfig);
            var factor = proration.Factor;
            var proratedPkg = ProrationCalculator.Apply(fullBasic, fullHousing, fullTransport, fullOther, factor);

            // The configurable PRORATED SET decides which components the factor actually touches — a
            // reimbursement-style allowance is commonly paid in full in the joining month, and prorating
            // a FIXED DEDUCTION *increases* net (right for a canteen charge, wrong for a recovery
            // instalment). Default set = the whole package, i.e. the GCC convention.
            var basic     = policy.Prorates(ProratedComponentCodes.Basic)           ? proratedPkg.Basic           : fullBasic;
            var housing   = policy.Prorates(ProratedComponentCodes.Housing)         ? proratedPkg.Housing         : fullHousing;
            var transport = policy.Prorates(ProratedComponentCodes.Transport)       ? proratedPkg.Transport       : fullTransport;
            var otherAllowances = policy.Prorates(ProratedComponentCodes.OtherAllowances) ? proratedPkg.OtherAllowances : fullOther;
            var gross = basic + housing + transport + otherAllowances;
            var fixedDeduction = policy.Prorates(ProratedComponentCodes.FixedDeduction)
                ? ProrationCalculator.ApplyScalar(fullFixedDeduction, factor)
                : fullFixedDeduction;
            var prorationNote = proration.Narrative;

            // Hourly rate for short-hours (late/early) deductions and OT base — the FULL monthly basic ÷
            // standardMonthlyHours. Deriving it from the prorated basic would pay a joiner's overtime at
            // a fraction of their real hourly rate.
            var hourlyRate = standardMonthlyHours > 0 ? fullBasic / standardMonthlyHours : 0m;

            // The wage every absence-type deduction is priced on: basic by default, or the company's full monthly package
            // (Setup -> Payroll rules). ONE base feeds the absent day, late/early short hours and unpaid leave, so the
            // three never disagree on the same payslip.
            var lopBase = CompanyPayrollRules.AbsenceDeductionBaseAmount(
                companyPayrollRules.AbsenceDeductionBase, fullBasic, fullHousing, fullTransport, fullOther);
            var shortHoursHourlyRate = standardMonthlyHours > 0 ? lopBase / standardMonthlyHours : 0m;

            // ── Short-hours deduction (late/early) at hourly rate ─────────────
            var attendanceDeduction = Math.Round(
                attendanceByEmp[e.Id]
                    // "Absence deduction" also contains "deduction", so an absent day was charged
                    // here at the hourly rate AND again as LOP twelve lines below. With the shipped KSA
                    // defaults the two are identical (480/60 x basic/240 == basic/30), so every absent
                    // day cost two days' basic and the payslip captioned it "Late/early" to an employee
                    // who was never late. Absences belong to the LOP bucket only.
                    .Where(x => true
                                && x.ImpactType.Contains("deduction", StringComparison.OrdinalIgnoreCase)
                                && !x.ImpactType.Contains("Absence", StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.Minutes) / 60m * shortHoursHourlyRate, 2);

            // ── LOP (Loss of Pay): absent days at day-rate (basic ÷ lopDayDivisor) ──
            // [FLAG-COMPLIANCE-KSA: LOP does NOT reduce GOSI covered wage in this implementation.
            //  Whether unpaid absence reduces the covered wage base is a statutory question
            //  requiring sign-off before any live payroll filing.]
            var absenceMinutes = attendanceByEmp[e.Id]
                .Where(x => x.ImpactType.Contains("Absence", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Minutes);
            var lopDays = lopStdMinutesPerDay > 0 && absenceMinutes > 0
                ? Math.Round((decimal)absenceMinutes / lopStdMinutesPerDay, 4)
                : 0m;
            // POD-C3 — day rate on the FULL monthly basic (an absent day costs the same whenever you
            // joined), and absent days can never exceed the days actually employed.
            if (proration.IsProrated && lopDays > proration.PaidDays) lopDays = proration.PaidDays;
            // An absent day is priced on the same base as short hours and unpaid leave (computed above).
            var lopDayRate = lopDayDivisor > 0 && lopBase > 0 ? lopBase / lopDayDivisor : 0m;
            var lopDeduction = Math.Round(lopDays * lopDayRate, 2);

            var leaveDeduction = leaveByEmp[e.Id]
                .Where(x => x.ImpactType.Contains("Deduction", StringComparison.OrdinalIgnoreCase))
                .Sum(x =>
                {
                    // Unpaid leave was priced on basic when the leave was approved; when the company prices absence on its
                    // full package, unpaid leave follows (statutory sick-leave bands keep their own wage-base rule).
                    var amount = CompanyPayrollRules.UnpaidLeaveDeduction(companyPayrollRules.AbsenceDeductionBase, x.Amount, x.Days, lopDayRate, x.ImpactType);
                    return leaveImpactScale.TryGetValue(x.Id, out var sc) ? Math.Round(amount * sc, 2) : amount;
                });

            // ── Overtime pay: approved MINUTES × hourly rate × statutory multiplier ──
            // Recomputed from OvertimePayrollImpacts (not .Amount) so the statutory multiplier
            // from StatutoryRule drives the rate, not the policy-level multiplier stored at
            // approval time.
            // `.Hours` is now DERIVED from the impact's int minutes (Minutes / 60m, no rounding),
            // so the quantity reaches this expression intact and the only rounding is the
            // Math.Round on the summed line below. It was a numeric(8,2) column written as
            // Math.Round(ApprovedMinutes / 60m, 2): 50 approved minutes were paid as 0.83 h.
            // [FLAG-COMPLIANCE-KSA: OT is excluded from GOSI covered wage in this implementation.
            //  Art.107 sets 1.5× for regular OT; weekend/holiday rates may require separate rules.
            //  Whether OT pay is included in the GOSI covered wage requires sign-off before filing.]
            var empOtImpacts = overtimeByEmp[e.Id].ToList();
            var otHours = empOtImpacts.Sum(x => x.Hours);
            // ── S1/A5: Art. 107 base + the day-category statutory FLOOR ───────────────────────────
            // `hourlyRate` above is basic ÷ standardMonthlyHours and stays the uplift base, because
            // Art. 107's 50% is expressly "of his basic wage". What changes is the FIRST term: for a
            // jurisdiction whose ot.hourly_base is "wage" it is the full-wage hourly rate, not basic.
            // The full monthly wage is used for the same reason the basic hourly rate is: a joiner's
            // overtime must not be paid at a fraction of their real hourly rate.
            var fullWageForOt = fullBasic + fullHousing + fullTransport + fullOther;
            var otBaseHourly = standardMonthlyHours > 0
                ? (otBaseIsFullWage ? fullWageForOt : fullBasic) / standardMonthlyHours
                : 0m;
            // The full-wage hourly rate, for the GrossSalary policy basis. Identical to otBaseHourly
            // wherever ot.hourly_base is already "wage" (KSA), so nothing moves there.
            var otWageHourly = standardMonthlyHours > 0 ? fullWageForOt / standardMonthlyHours : 0m;
            // A per-request ApprovedMultiplier is honoured only where it is at or above the statutory
            // floor for the day actually worked. It was previously preferred unconditionally whenever
            // non-zero, which made it a back door around the statutory rate: an approved request on a
            // policy configured at 1.0 paid 1.0, with no floor check anywhere in the system.
            //
            // The tenant's OvertimePolicy basis is applied HERE, through the same shared resolver
            // OvertimeController calls, and is honoured only where it is worth at least the Art. 107
            // hour. Before this the run ignored the policy basis outright, so a FixedHourlyRate
            // tenant was shown one number by the overtime module and paid this one.
            // The `otBaseHourly > 0` gate is widened to admit a configured fixed rate: with no salary
            // structure the statutory base is zero, but a configured rate is still a real amount owed.
            var overtimePay = empOtImpacts.Count > 0 && (otBaseHourly > 0m || otPolicyFixedHourlyRate > 0m)
                ? Math.Round(empOtImpacts.Sum(x => x.Hours * OvertimeStatutoryCalculator.ResolveHourRate(
                    otPolicyBasis, otPolicyFixedHourlyRate,
                    otBaseHourly, otWageHourly, hourlyRate, OtEffectiveMultiplier(x)).HourPay), 2)
                : 0m;

            // The payslip line must describe the arithmetic that actually produced the money. The
            // weighted-average effective multiplier collapses to otMultiplier exactly when every
            // impact sits on the ordinary-day floor with no approved override, so an unaffected run's
            // label is byte-identical; it diverges only where the rate genuinely did.
            var otEffectiveMultiplier = otHours > 0m
                ? empOtImpacts.Sum(x => x.Hours * OtEffectiveMultiplier(x)) / otHours
                : otMultiplier;

            // For the same reason, the hourly rate on the line and on the payslip record must be the
            // base the money was actually computed on — the statutory base, or the tenant's
            // configured base where it beat it. Weighted the same way, and collapsing to
            // otBaseHourly exactly when no configured base applies, so an unaffected run's label and
            // PayrollSlip.HourlyRate are byte-identical.
            var otEffectiveBaseHourly = otHours > 0m
                ? empOtImpacts.Sum(x => x.Hours * OvertimeStatutoryCalculator.ResolveHourRate(
                      otPolicyBasis, otPolicyFixedHourlyRate,
                      otBaseHourly, otWageHourly, hourlyRate, OtEffectiveMultiplier(x)).BaseHourly) / otHours
                : otBaseHourly;

            // The SECOND term of the hour, weighted identically: what the × (multiplier − 1) uplift is
            // measured on. Art. 107's "50% of his BASIC wage" is a separate base from the first term,
            // and without it on the line the payslip could only state a rate × multiplier shape that
            // the money was never computed with. Collapses to `hourlyRate` (basic hourly) wherever no
            // configured base applies, which is every statutory run.
            var otEffectiveUpliftHourly = otHours > 0m
                ? empOtImpacts.Sum(x => x.Hours * OvertimeStatutoryCalculator.ResolveHourRate(
                      otPolicyBasis, otPolicyFixedHourlyRate,
                      otBaseHourly, otWageHourly, hourlyRate, OtEffectiveMultiplier(x)).UpliftBasisHourly) / otHours
                : hourlyRate;

            decimal OtEffectiveMultiplier(OvertimePayrollImpact impact)
            {
                var floor = otMultiplier;
                if (overtimeWorkDates.TryGetValue(impact.OvertimeRequestId, out var workDate))
                    floor = OvertimeStatutoryCalculator.StatutoryFloor(
                        OvertimeStatutoryCalculator.DayCategory(
                            workDate,
                            periodPublicHolidays.Contains(workDate),
                            workWeekConfig.IsWeekend(workDate.DayOfWeek)),
                        otMultiplier, otRestDayMultiplier, otHolidayMultiplier);
                return OvertimeStatutoryCalculator.EffectiveMultiplier(impact.ApprovedMultiplier, floor);
            }
            // ── F2: CONFIGURED-VALUE PAY COMPONENTS — THE ENGINE IS THE SOLE SOURCE OF THEIR AMOUNTS ─────
            // Every other payslip line re-emits an amount computed HERE (structure columns, tax, attendance,
            // leave, loans, bonus, adjustments, the country pack), so it is already in the aggregates below.
            // A tenant-configured Fixed / PercentOfBasic / PercentOfGross component exists ONLY in the
            // engine's output. Pre-F2 those lines were emitted onto the payslip but never reached gross,
            // deductions or net — net pay ignored them and the Lock journal failed gl_unbalanced by exactly
            // their amount. They are now resolved by the SAME engine call the emission below makes (same
            // components, same Basic/Gross inputs ⇒ same amounts), folded into every aggregate, and the
            // post-loop PAYSLIP_LINES_MISMATCH check proves Σ emitted lines == the slip aggregates.
            //   • Supplemental runs carry none (they pay no recurring component), mirroring the effective
            //     set filter at emission.
            //   • They are NEVER in the statutory covered wage: the SalaryBreakdown fed to the pack below is
            //     unchanged, so GOSI/GPSSA/GRSIA and the 45,000 ceiling are untouched by construction.
            //   • Fixed amounts are monthly and NOT prorated; PercentOf* follow the prorated basic/gross.
            //   • Empty for every tenant without such a component, and for the legacy kill-switch path, so
            //     every existing run's aggregates are the pre-F2 expressions + 0.
            var configuredComponents = includesRecurringPay
                ? payComponents.Where(PayComponentEngine.IsConfiguredValue).ToList()
                : new List<PayComponent>();
            var configuredCtx = new PayComponentContext { Basic = basic, Gross = gross };
            var configured = PayComponentEngine.Compute(configuredComponents, configuredCtx);
            var configuredEarnings = configured.Earnings.Sum(l => l.Amount);
            var configuredDeductions = configured.Deductions.Where(l => !l.IsEmployerContribution).Sum(l => l.Amount);
            var configuredTaxableEarnings = configuredComponents.Count == 0 ? 0m : PayComponentEngine.Compute(
                    configuredComponents.Where(c => c.IsTaxable && c.ComponentType == PayComponentTypes.Earning).ToList(),
                    configuredCtx).Earnings.Sum(l => l.Amount);

            var benefitTaxableEarnings = adjustmentsByEmp[e.Id].Where(a => a.Amount > 0
                && BenefitPayroll.Read(a)?.Component.IsTaxable == true).Sum(a => a.Amount);
            // Tax deduction: apply income tax rate to taxable components only
            decimal taxDeduction = 0m;
            if (incomeTaxRate > 0 && salary is not null)
            {
                var structureComponents = salaryComponents.Where(c => c.SalaryStructureId == salary.SalaryStructureId && c.IsTaxable).ToList();
                // If no explicit taxable components defined, treat basic salary as taxable.
                // POD-C3 — the PERCENTAGE branch already rides the prorated `basic`; the ABSOLUTE branch
                // is a set of monthly amounts and must be prorated by the same factor, or a joiner would
                // be taxed on a full month of income they never received.
                var taxableBase = structureComponents.Count > 0
                    ? ProrationCalculator.ApplyScalar(
                        structureComponents.Sum(c => c.CalculationType == "Percentage" ? fullBasic * c.Percentage / 100m : c.Amount),
                        factor)
                    : basic;
                // F2 — a configured earning flagged IsTaxable is taxable income. 0 for every tenant without one.
                taxableBase += configuredTaxableEarnings + benefitTaxableEarnings;
                taxDeduction = Math.Round(taxableBase * incomeTaxRate / 100m, 2);
            }

            // ── POD-B2: SUPPLEMENTAL BASIS ───────────────────────────────────────────────────────────
            // A run that does not pay recurring salary pays supplemental items ONLY: bonuses, the
            // adjustments attached to this run, and statutory on the supplemental base. Without this gate
            // a mid-month bonus run pays the employee their WHOLE monthly salary a second time (the loop
            // has no `continue` and BASIC is emitted unconditionally at any amount), plus a second loan
            // EMI. Every recurring scalar is zeroed here, before it reaches the statutory input, the slip
            // aggregates or either emission path; the recurring LINES are skipped (not emitted at 0.00) so
            // no empty GL group is produced. `includesRecurringPay` is always true for a Regular run, so
            // this block is dead code on every existing run.
            if (!includesRecurringPay)
            {
                basic = 0m; housing = 0m; transport = 0m; otherAllowances = 0m; gross = 0m;
                fixedDeduction = 0m; hourlyRate = 0m;
                attendanceDeduction = 0m; lopDays = 0m; lopDayRate = 0m; lopDeduction = 0m;
                leaveDeduction = 0m; overtimePay = 0m; otHours = 0m; taxDeduction = 0m;
                // POD-C3 — a supplemental run pays no recurring wage, so the FULL package is not its
                // statutory basis either. Zeroing them here keeps the slip's proration witnesses honest
                // and stops the FullMonth GOSI branch below re-introducing a whole month's covered wage
                // on a bonus-only run.
                fullBasic = 0m; fullHousing = 0m; fullTransport = 0m; fullOther = 0m;
                prorationNote = string.Empty;
            }

            // Supplemental and salary-less runs still tax configured taxable benefit payments.
            if ((!includesRecurringPay || salary is null) && incomeTaxRate > 0)
                taxDeduction = Math.Round(benefitTaxableEarnings * incomeTaxRate / 100m, 2);

            // BONUS: collect this employee's approved bonuses for the period.
            var empBonuses = bonusesByEmployee.TryGetValue(e.Id, out var eb) ? eb : new List<EmployeeBonus>();
            // Gross bonus amounts that are part of the social insurance base (e.g. GOSI/GPSSA/GRSIA).
            decimal gosiIncludedBonusTotal = empBonuses
                .Where(b => bonusTypeMap.TryGetValue(b.BonusTypeId, out var bt) && bt.IsIncludedInGosiBase)
                .Sum(b => b.GrossBonusAmount);
            // Net bonus earnings added to employee take-home this period.
            decimal totalBonusNet = empBonuses.Sum(b => b.BonusAmount);
            // POD-B1b — the bonus EARNING line has always been emitted GROSS (see AddEarning below), but
            // the slip aggregates counted it NET and the withheld tax was never emitted as a deduction at
            // all. That made the Lock journal short by exactly Σ TaxWithheld on any taxable bonus, so a
            // non-GCC tenant could never lock (gl_unbalanced 422). Carry the bonus GROSS through the slip
            // and emit the withholding as a real Tax deduction: gross − tax == net, so NetSalary, WPS/SIF
            // and the GOSI base are arithmetically unchanged, and for GCC (tax = 0) every emitted line and
            // every aggregate is byte-identical to before.
            decimal totalBonusGross = empBonuses.Sum(b => b.GrossBonusAmount);
            decimal totalBonusTax   = empBonuses.Sum(b => b.TaxWithheld);
            var empAdjustments = adjustmentsByEmp[e.Id].ToList();
            var adjustmentEarnings = empAdjustments.Where(a => a.Amount > 0m).Sum(a => a.Amount);
            var adjustmentDeductions = Math.Abs(empAdjustments.Where(a => a.Amount < 0m).Sum(a => a.Amount));

            // Statutory deduction via country pack — rates from tenant-overridable StatutoryRule rows.
            // GosiCalculationService is retained for parity testing; it is no longer called in the run path.
            // GOSI-included bonus is added to housing slot so GosiCoveredWage = Basic + Housing + Bonus.
            //
            // POD-B2 (M8): the covered-wage CEILING is a period concept, so once a sibling run exists for
            // this (company, year, month) the pack is fed the PERIOD-TO-DATE base and the amounts the
            // siblings already deducted are netted off per line. With no siblings — every run in every
            // tenant before B2 — priorBasic/priorHousing are 0 and this is the original call, unchanged.
            var priorBasic   = priorBasicByEmp.GetValueOrDefault(e.Id);
            var priorHousing = priorHousingByEmp.GetValueOrDefault(e.Id);

            // ── POD-C3: ARREARS in the statutory base ────────────────────────────────────────────────
            // GOSI-bearing arrears ride the SAME housing slot gosiIncludedBonusTotal already uses, so the
            // 45,000 ceiling is applied to (period base + bonus + arrears) in exactly ONE pack call. The
            // A1 reconstruction adds them to the same slot from the same sub-ledger, keyed on the same
            // IsGosiBearing flag — one flag governs both sides, so they cannot drift.
            // (empArrears is empty unless the run opted into settling arrears — the engine was not even
            // called otherwise — so no second gate is needed here.)
            var empArrears = arrearsByEmp.TryGetValue(e.Id, out var al) ? al : new List<PayrollArrearsLine>();
            var arrearsTotal     = Math.Round(empArrears.Sum(a => a.Amount), 2);
            var gosiArrearsTotal = Math.Round(empArrears.Where(a => a.IsGosiBearing).Sum(a => a.Amount), 2);

            // ── POD-C3 [FLAG-COMPLIANCE-KSA]: WHICH BASE THE PACK IS FED ─────────────────────────────
            // DEFAULT `FullMonth`. GOSI assesses a MONTHLY contributory wage: the month an employee is
            // registered attracts a full month's contribution on the registered wage, and the portal has
            // no partial-month proration of it. Prorating under-remits in every joining and leaving month
            // — the penalty-bearing direction — and would also CONTRADICT the treatment already shipped
            // and tested for unpaid absence, where LOP explicitly does NOT reduce the covered wage. Same
            // economic fact, opposite treatment, one payslip.
            //
            // Under FullMonth the covered wage is NOT recoverable from slip.BasicSalary /
            // slip.HousingAllowance, which is why the slip persists FullBasicSalary /
            // FullHousingAllowance / GosiBasePolicy below and BOTH reconciliation paths rebuild from them.
            var statBasic   = policy.ProratesStatutoryBase ? basic   : fullBasic;
            var statHousing = policy.ProratesStatutoryBase ? housing : fullHousing;
            var statutoryInput = new StatutoryDeductionInput(
                EmployeeId:   Guid.Empty, // Employee PK is int; Guid field not used in pack calculations
                CompanyId:    run.CompanyId ?? Guid.Empty,
                Salary:       new SalaryBreakdown(priorBasic + statBasic, priorHousing + statHousing + gosiIncludedBonusTotal + gosiArrearsTotal, transport, otherAllowances),
                Nationality:  e.Nationality ?? string.Empty,
                ContractType: e.ContractType ?? "Indefinite",
                PeriodYear:   run.Year,
                PeriodMonth:  run.Month)
            {
                // S1/A3 — the person dimension. DateOfBirth exists on Employee today and drives the
                // age-based eligibility corollary (annuities and SANED cease at retirement age).
                // F02 — the GOSI first-registration date resolves the person's cohort in the pack; null
                // means UNKNOWN, and no pack may read unknown as "new entrant".
                DateOfBirth = e.DateOfBirth,
                SocialInsuranceFirstRegisteredOn = e.GosiFirstRegisteredOn,
            };
            var statutoryResult = await deductionCalc.CalculateAsync(statutoryInput, cancellationToken);
            if (priorStatutoryByEmp.Count > 0)
            {
                // Net off the period-to-date statutory already deducted by sibling runs, per (code, side),
                // floored at zero. What remains is THIS run's incremental statutory obligation, so the
                // period total ties out to a single ceiling-capped computation across all its runs.
                var netted = new List<StatutoryDeductionLine>();
                foreach (var line in statutoryResult.Lines)
                {
                    var priorEe = priorStatutoryByEmp.GetValueOrDefault((e.Id, line.Code, false));
                    var priorEr = priorStatutoryByEmp.GetValueOrDefault((e.Id, line.Code, true));
                    var ee = Math.Max(0m, line.EmployeeAmount - priorEe);
                    var er = Math.Max(0m, line.EmployerAmount - priorEr);
                    netted.Add(line with { EmployeeAmount = ee, EmployerAmount = er });
                }
                statutoryResult = statutoryResult with
                {
                    Lines                      = netted,
                    TotalEmployeeDeduction     = netted.Sum(l => l.EmployeeAmount),
                    TotalEmployerContribution  = netted.Sum(l => l.EmployerAmount),
                };
                statutoryComputedIncrementally = true;
            }
            var gosiEmployeeTotal = statutoryResult.TotalEmployeeDeduction;

            // COMPLIANCE: Loan & advance EMI deduction.
            // POD-B2: a supplemental run takes NO recurring deduction — an off-cycle bonus run must not
            // collect a second EMI for the period. The mutable decrement block after the loop is gated on
            // the same flag, so balances and installments are untouched too.
            //
            // POD-C1 — a SETTLING employee is the exception: this is their LAST payment, so their debts are
            // settled with it rather than left to a next run that will never come. They are re-loaded here
            // even though the run pays no recurring salary, and the "due" below is the WHOLE outstanding
            // balance rather than one instalment.
            var empSettlement = settlementsByEmp.TryGetValue(e.Id, out var stl) ? stl : null;
            var isSettlingEmployee = empSettlement is not null;
            var empLoans   = includesRecurringPay || isSettlingEmployee ? loansByEmp[e.Id].Where(l => true).OrderBy(l => l.Id).ToList() : new List<EmployeeLoan>();
            var empAdv     = includesRecurringPay || isSettlingEmployee ? advancesByEmp[e.Id].Where(a => true).OrderBy(a => a.Id).ToList() : new List<SalaryAdvance>();

            // POD-C1 — the settlement's OWN lines, read verbatim from the persisted plan.
            var empSettlementLines = empSettlement is not null
                    && settlementLinesById.TryGetValue(empSettlement.Id, out var sl)
                ? sl
                : new List<FinalSettlementLine>();
            var settlementEarningTotal = Math.Round(empSettlementLines
                .Where(l => l.LineType == FinalSettlementLineTypes.Earning).Sum(l => l.Amount), 2);
            // Capped at the settlement's own gross AT PLAN TIME (see the approve path), so a settlement's
            // own deductions can never drive net negative and 422 the whole batch out from under every
            // other leaver in the run.
            var settlementDeductionTotal = Math.Round(Math.Min(settlementEarningTotal, empSettlementLines
                .Where(l => l.LineType == FinalSettlementLineTypes.Deduction).Sum(l => l.Amount)), 2);

            // ── POD-C3 (MF-2): DEBT IS CAPPED AT WHAT THE PAY PERIOD CAN FUND ────────────────────────
            // Proration makes an un-prorated EMI reachable for the first time: a joiner on the 25th with
            // a 20,000 package and a 5,000 instalment earns ~4,000 for the month. Pre-C3 the shortfall
            // was silently swallowed by `Math.Max(0m, rawNet)` on a Regular run (the negative-net
            // collector was gated on `!includesRecurringPay`), so the employee was underpaid AND the
            // unfloored deduction credits made Σ DR ≠ Σ CR — a gl_unbalanced 422 at Lock on a run that
            // locks cleanly today. The instalment is therefore capped at the net available BEFORE debt,
            // and the balance CARRIES FORWARD (standard GCC practice, and the only outcome that neither
            // underpays nor unbalances). An EMI is a DEBT INSTALMENT, not a wage — it is never prorated.
            var deductionsBeforeDebt = fixedDeduction + attendanceDeduction + lopDeduction + leaveDeduction
                                     + taxDeduction + gosiEmployeeTotal + adjustmentDeductions + totalBonusTax
                                     + settlementDeductionTotal + configuredDeductions;
            var earningsForPeriod = gross + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal
                                  + settlementEarningTotal + configuredEarnings;
            var affordable = Math.Round(earningsForPeriod - deductionsBeforeDebt, 2);

            var loanTakenById = new Dictionary<Guid, decimal>();
            var advTakenById  = new Dictionary<Guid, decimal>();
            decimal loanEmi = 0m, advEmi = 0m, deferredDebt = 0m;
            var debtBudget = Math.Max(0m, affordable);
            foreach (var l in empLoans)
            {
                // POD-C1 — a leaver's debts are settled from their FINAL payment, so the whole balance is
                // due, not one instalment. The take is still re-capped against the LIVE OutstandingBalance
                // inside this transaction (after any final-wage-month EMI has already decremented it) and
                // against what the settlement can fund, so Σ recovery ≤ the original balance is arithmetic
                // rather than convention, and there is exactly ONE decrement path.
                // The last installment owns the cent residual from an exact-cent schedule.
                // Recover it with that installment rather than creating an extra salary deduction.
                var isFinalInstallment = l.ApprovedInstallments > 0 && l.InstallmentAmount > 0m
                    // Imported legacy loans can carry stale installment counts. Never use that
                    // count to accelerate a material balance or change a legacy fractional EMI.
                    && decimal.Round(l.InstallmentAmount, 2) == l.InstallmentAmount
                    && l.OutstandingBalance < l.InstallmentAmount + l.ApprovedInstallments * .01m
                    && l.TotalRepaid >= l.InstallmentAmount * (l.ApprovedInstallments - 1);
                var due = isSettlingEmployee || isFinalInstallment ? l.OutstandingBalance : Math.Min(l.InstallmentAmount, l.OutstandingBalance);
                if (due <= 0m) continue;
                var take = Math.Min(due, debtBudget);
                if (take > 0m) { loanTakenById[l.Id] = take; loanEmi += take; debtBudget -= take; }
                deferredDebt += due - take;
            }
            foreach (var a in empAdv)
            {
                var due = isSettlingEmployee ? a.OutstandingBalance : Math.Min(a.InstallmentAmount, a.OutstandingBalance);
                if (due <= 0m) continue;
                var take = Math.Min(due, debtBudget);
                if (take > 0m) { advTakenById[a.Id] = take; advEmi += take; debtBudget -= take; }
                deferredDebt += due - take;
            }
            // POD-C1 — the settlement PLANNED a recovery at approve; the live balance may since have moved
            // (the final wage month's own EMI). A reduction is reported, never silently over-recovered.
            if (isSettlingEmployee)
            {
                var plannedRecovery = Math.Round(empSettlement!.PlannedLoanRecovery + empSettlement.PlannedAdvanceRecovery, 2);
                var actualRecovery  = Math.Round(loanEmi + advEmi, 2);
                if (plannedRecovery - actualRecovery > 0.01m)
                    settlementRecoveryReduced.Add((e.Id, e.EmployeeCode, e.FullName, plannedRecovery, actualRecovery));
            }
            // POD-B1b-FIX (re-audit #6) — NO rounding on the EMI path. An instalment is
            // ApprovedAmount / Installments and is routinely 4dp; the accrual debit posts it verbatim, so
            // rounding here would make Σ CR ≠ Σ DR by up to a cent on the remittance and either post a
            // journal off by a cent or 422 a legitimate one. The capping above is exact by construction
            // (Math.Min of two stored decimals), so it introduces no new precision either.
            deferredDebt = Math.Round(deferredDebt, 2);
            if (deferredDebt > 0m)
                deferredEmiEmployees.Add((e.Id, e.EmployeeCode, e.FullName, deferredDebt));
            if (loanTakenById.Count > 0) loanTakenByEmployee[e.Id] = loanTakenById;
            if (advTakenById.Count > 0)  advTakenByEmployee[e.Id]  = advTakenById;
            // Rounded exactly where it was pre-C3: the slip AGGREGATE rounds, the emitted LOAN_EMI /
            // ADVANCE_EMI lines do not.
            var totalLoanDeduction = Math.Round(loanEmi + advEmi, 2);

            // ── POD-C3 (POD-B3 handoff): net the 1420 receivable a prior void recognised ─────────────
            // min(outstanding, net before recovery) — net then equals "what we should have paid − what we
            // already paid", which is the definition of a recovery. Never drives net negative.
            decimal receivableRecovery = 0m;
            if (run.NetsPriorReceivable && receivableByEmp.TryGetValue(e.Id, out var empReceivables))
            {
                var recoverable = Math.Max(0m, Math.Round(affordable - totalLoanDeduction, 2));
                // POD-C3-FIX — how much of this employee's recovery came from a receivable recognised in a
                // DIFFERENT period, and which periods those were. Recovering inside the SAME period is not
                // a wage deduction at all (the replacement run re-pays the very month whose cash the
                // employee already holds); recovering out of a LATER month's wages is, and the two must
                // not be reported as though they were the same act.
                decimal crossPeriodTaken = 0m;
                var crossPeriods = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var rcv in empReceivables)
                {
                    if (recoverable <= 0m) break;
                    var take = Math.Min(rcv.Outstanding, recoverable);
                    if (take <= 0m) continue;
                    receivableRecovery += take;
                    recoverable -= take;
                    receivableTakenByEmployee.Add((rcv.Id, e.Id, take));
                    if (!string.Equals(rcv.Period, periodStr, StringComparison.Ordinal))
                    {
                        crossPeriodTaken += take;
                        crossPeriods.Add(string.IsNullOrWhiteSpace(rcv.Period) ? "(unknown period)" : rcv.Period);
                    }
                }
                receivableRecovery = Math.Round(receivableRecovery, 2);
                crossPeriodTaken   = Math.Round(crossPeriodTaken, 2);
                if (crossPeriodTaken > 0m)
                    receivableCrossPeriodEmployees.Add((e.Id, e.EmployeeCode, e.FullName, crossPeriodTaken,
                        string.Join(", ", crossPeriods)));
                var residual = Math.Round(empReceivables.Sum(r => r.Outstanding) - receivableRecovery, 2);
                if (residual > 0m)
                    receivableResidualEmployees.Add((e.Id, e.EmployeeCode, e.FullName, residual));
            }

            var deductions = deductionsBeforeDebt + totalLoanDeduction + receivableRecovery;
            // C3: net salary cannot be negative (GCC labour law); engine Rule 3 will flag this.
            // (gross bonus in, bonus tax out) == (net bonus in) — take-home is unchanged by POD-B1b.
            var rawNet = earningsForPeriod - deductions;
            // POD-B2 (M4): a run whose deductions exceed its earnings has no vehicle to express the
            // shortfall — the floor below silently swallows it and the run then trips GL_WILL_NOT_BALANCE
            // (an Error whose remedy, "re-process the run", can never fix it), leaving an unlockable run
            // that DeleteRun also refuses. Correction runs are ADDITIVE-ONLY: collect the offenders and
            // refuse the whole run with a specific 422 below.
            //
            // POD-C3 (MF-2): negative net is now DETECTED on recurring runs too — that gate is exactly
            // what made the magnitude of the shortfall invisible on a Regular run (the pre-existing
            // ZERO_NET_WITH_GROSS Error says net is zero, never by how much it was short, nor why).
            //
            // The RESPONSE differs by run type ON PURPOSE, and this is a deliberate narrowing of the
            // consultant's "refuse everywhere":
            //   • SUPPLEMENTAL — unchanged: throw, roll back, 422 negative_net_unsupported (POD-B2's
            //     tested contract; an additive-only run with no vehicle for a negative delta).
            //   • RECURRING — the run is still WRITTEN and net is still clamped at zero, because the
            //     existing ZERO_NET_WITH_GROSS Error already BLOCKS Approve and Lock, so the unbalanced
            //     journal the clamp would otherwise cause is unreachable. Refusing at Process instead
            //     would break that shipped, tested remedy path (the operator inspects the processed run,
            //     fixes the cause, re-processes) for a case a joiner can now legitimately reach. What C3
            //     adds is a SECOND, Error-severity result that names the exact shortfall and its most
            //     likely cause — information the operator previously had to derive by hand.
            // Debt is already capped above, so what lands here is a genuinely unfundable NON-DEBT
            // deduction, most often the FullMonth statutory contribution of a late-month joiner.
            if (rawNet < 0m)
            {
                if (!includesRecurringPay)
                    negativeNetEmployees.Add(new { employeeId = e.Id, code = e.EmployeeCode, name = e.FullName, shortfall = Math.Abs(rawNet) });
                else
                    recurringShortfallEmployees.Add((e.Id, e.EmployeeCode, e.FullName, Math.Abs(rawNet),
                        proration.IsProrated, gosiEmployeeTotal));
            }
            var netSalary = Math.Max(0m, rawNet);

            // COMPLIANCE: YTD — sum all locked slips for this employee earlier in the same year
            var empYtdSlips = ytdSlipsByEmp[e.Id].ToList();
            openingBalancesByEmployee.TryGetValue(e.Id, out var openingBalances);
            var ytdGross    = empYtdSlips.Sum(s => s.GrossSalary) + SumOpeningBalance(openingBalances, PayrollYtdBasis.YtdGrossTypes);
            var ytdDeduct   = empYtdSlips.Sum(s => s.Deductions) + SumOpeningBalance(openingBalances, PayrollYtdBasis.YtdDeductionTypes);
            var ytdNet      = empYtdSlips.Sum(s => s.NetSalary) + SumOpeningBalance(openingBalances, PayrollYtdBasis.YtdNetTypes);

            var slip = new PayrollSlip
            {
                TenantId = tenantId,
                RunId = id,
                EmployeeId = e.Id,
                EmployeeCode = e.EmployeeCode,
                EmployeeName = e.FullName,
                Department = e.Department,
                BasicSalary = basic,
                HousingAllowance = housing,
                TransportAllowance = transport,
                // POD-C1 — the settlement's earnings ride the SAME aggregate as bonus/adjustment/arrears do,
                // so NetSalary, the WPS/SIF amount and the payment batch total all pick them up with no new
                // machinery. (The statutory base is untouched — see the GOSI decision on FinalSettlement.)
                OtherAllowances = otherAllowances + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal + settlementEarningTotal + configuredEarnings,
                GrossSalary = gross + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal + settlementEarningTotal + configuredEarnings,
                Deductions = deductions,
                NetSalary = netSalary,
                EmployeeStatutoryTotal = statutoryResult.TotalEmployeeDeduction,
                EmployerStatutoryTotal = statutoryResult.TotalEmployerContribution,
                LoanDeductions = totalLoanDeduction,
                YtdGross = ytdGross + gross + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal + settlementEarningTotal + configuredEarnings,
                YtdDeductions = ytdDeduct + deductions,
                YtdNet = ytdNet + netSalary,
                Status = "Draft",
                // ── POD-C3: THE PRORATION WITNESSES ─────────────────────────────────────────────────
                // These are what let POD-A1 keep its guarantee — "reconstruct expected from the run's own
                // persisted outputs" — once the money columns carry a PRORATED wage while the statutory
                // base is the FULL monthly package. Both reconciliation paths rebuild from them.
                PaidFromDate = proration.PaidFrom,
                PaidToDate = proration.PaidTo,
                PaidDays = proration.PaidDays,
                ProrationDenominatorDays = proration.DenominatorDays,
                PeriodDays = proration.PeriodDays,
                ProrationBasis = policy.Basis,
                ProrationFactor = factor,
                FullBasicSalary = fullBasic,
                FullHousingAllowance = fullHousing,
                FullTransportAllowance = fullTransport,
                GosiBasePolicy = policy.GosiBase,
                ArrearsAmount = arrearsTotal,
                // POD-C1 SEAM: the WAGE side of this leaver's final month is settled here and nowhere
                // else. C3 emits NO EOSB, no notice pay, no leave encashment, no termination payable and
                // no off-cycle settlement journal. Everything after PaidToDate is C1's.
                IsFinalWageMonth = includesRecurringPay && proration.IsFinalWageMonth,
                // F02 — the calculation explanation: the cohort and rate basis the statutory lines above
                // were computed on, frozen with them. The validator judges THIS, not the live record.
                GosiCohort = statutoryResult.SocialInsuranceCohort,
                StatutoryBasis = statutoryResult.Basis,
            };
            slips.Add(slip);
            slip.CompanyId = company.Id;
            _db.PayrollRunEmployees.Add(new PayrollRunEmployee { TenantId = tenantId, PayrollRunId = id, EmployeeId = e.Id, GrossEarnings = slip.GrossSalary, TotalDeductions = deductions, NetPay = slip.NetSalary });
            // ── LINE EMISSION ────────────────────────────────────────────────────────────────────
            // The legacy inline block (kept VERBATIM under the kill-switch, so its behaviour is provably
            // unchanged) and the data-driven PayComponentEngine emit the SAME PayrollEarning/PayrollDeduction
            // rows for the seeded component set. Both call the same AddEarning/AddDeduction helpers, so GL
            // routing, WPS and the payslip builder downstream are identical either way. The slip header +
            // every aggregate (deductions/net/statutory totals) are projected above from the same scalars in
            // BOTH paths, so the run is byte-identical regardless of which branch runs.
            if (!useComponentEngine)
            {
            // Bonus earning lines (one per bonus in the batch, gross amount for GL expense tracking).
            foreach (var bonus in empBonuses)
                AddEarning(tenantId, id, e.Id, BonusGlLedger.EarningComponentCode(bonus.BonusTypeName), bonus.BonusTypeName, bonus.GrossBonusAmount, "Bonus");
            foreach (var adjustment in empAdjustments.Where(a => a.Amount > 0m))
            {
                var line = AdjustmentLine(adjustment);
                AddEarning(tenantId, id, e.Id, line.Code, line.Name, line.Amount, line.Source, glDriverKey: line.GlDriverKey);
            }
            // POD-B2: BASIC is the ONLY line emitted unconditionally (every other recurring line already
            // carries an `if (x > 0)` guard, and the scalars above are zeroed on a supplemental basis).
            // Skipping it — rather than emitting 0.00 — keeps a supplemental run's journal free of an
            // empty EARN:BASIC debit group in BuildPayrollGlEntries.
            if (includesRecurringPay)
            AddEarning(tenantId, id, e.Id, "BASIC", WithProrationNote("Basic salary", prorationNote), basic, "Salary");
            if (housing > 0) AddEarning(tenantId, id, e.Id, "HOUSING", "Housing allowance", housing, "Salary");
            if (transport > 0) AddEarning(tenantId, id, e.Id, "TRANSPORT", "Transport allowance", transport, "Salary");
            if (otherAllowances > 0) AddEarning(tenantId, id, e.Id, "OTHER_ALLOWANCES", "Other allowances", otherAllowances, "Salary");
            if (overtimePay > 0)
                // ONE definition, shared with PayComponentEngine and sitting beside the HourPay
                // expression it describes — `otRateDisplay` (hourlyRate × otMultiplier), computed here
                // and read by nothing, was the last trace of the shape that did not add up.
                AddEarning(tenantId, id, e.Id, "OVERTIME",
                    OvertimeStatutoryCalculator.PayslipLabel(
                        otHours, otEffectiveBaseHourly, otEffectiveUpliftHourly, otEffectiveMultiplier),
                    overtimePay, "Overtime");
            if (fixedDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "FIXED_DEDUCTION",
                WithProrationNote("Fixed deduction", policy.Prorates(ProratedComponentCodes.FixedDeduction) ? prorationNote : string.Empty),
                fixedDeduction, "Salary");
            if (taxDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "INCOME_TAX", $"Income tax ({incomeTaxRate}%)", taxDeduction, "Tax");
            if (attendanceDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "ATTENDANCE", "Late/early attendance deduction", attendanceDeduction, "Attendance");
            if (lopDeduction > 0)
                AddDeduction(tenantId, company.Id, id, e.Id, "LOP_DEDUCTION",
                    $"Loss of Pay ({lopDays:N2} d × {Math.Round(lopDayRate, 2):N2}/d)",
                    lopDeduction, "Attendance");
            if (leaveDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "LEAVE", "Leave deduction", leaveDeduction, "Leave");
            if (loanEmi > 0) AddDeduction(tenantId, company.Id, id, e.Id, "LOAN_EMI", "Loan instalment", loanEmi, "Loan");
            if (advEmi > 0) AddDeduction(tenantId, company.Id, id, e.Id, "ADVANCE_EMI", "Salary advance repayment", advEmi, "Loan");
            foreach (var adjustment in empAdjustments.Where(a => a.Amount < 0m))
            {
                var line = AdjustmentLine(adjustment);
                AddDeduction(tenantId, company.Id, id, e.Id, line.Code, line.Name, line.Amount, line.Source, glDriverKey: line.GlDriverKey);
            }
            // Statutory deduction lines from pack — employee contributions reduce net pay.
            // Code/Label come from the pack: "GOSI-ANN-EE" / "GOSI Annuities (Employee)" for KSA,
            // "GPSSA-EE" / "GPSSA (Employee)" for UAE, "GRSIA-EE" / "GRSIA (Employee)" for Qatar.
            // Employer lines already carry ER-suffixed codes (e.g. "GOSI-ANN-ER", "GOSI-OH-ER") —
            // the GL router keys on .EndsWith("-ER") to route them to Social Insurance Employer Payable.
            foreach (var line in statutoryResult.Lines.Where(l => l.EmployeeAmount > 0))
                AddDeduction(tenantId, company.Id, id, e.Id, line.Code, line.Label, line.EmployeeAmount, "Statutory");
            foreach (var line in statutoryResult.Lines.Where(l => l.EmployerAmount > 0))
                AddDeduction(tenantId, company.Id, id, e.Id, line.Code, line.Label, line.EmployerAmount, "Statutory", isEmployerContribution: true);
            }
            else
            {
                // Data-driven path: build the pure engine context from the SAME computed scalars +
                // subsystem/pack results the legacy block uses (statutory amounts stay pack-owned; the
                // engine never touches the social-insurance base), then emit the ordered lines through the
                // same AddEarning/AddDeduction helpers. Family/statutory codes+labels are pre-built here so
                // NormalizeCode/AdjustmentLabel/pack labels produce byte-identical output.
                var payCtx = new PayComponentContext
                {
                    Basic = basic, Housing = housing, Transport = transport,
                    OtherAllowances = otherAllowances, FixedDeduction = fixedDeduction, Gross = gross,
                    OvertimePay = overtimePay, OtHours = otHours, HourlyRate = otEffectiveBaseHourly,
                    OtUpliftHourly = otEffectiveUpliftHourly, OtMultiplier = otEffectiveMultiplier,
                    TaxDeduction = taxDeduction, IncomeTaxRate = incomeTaxRate,
                    AttendanceDeduction = attendanceDeduction,
                    LopDeduction = lopDeduction, LopDays = lopDays, LopDayRate = lopDayRate,
                    LeaveDeduction = leaveDeduction, LoanEmi = loanEmi, AdvEmi = advEmi,
                    BonusLines = empBonuses
                        .Select(b => new PayComponentLine(BonusGlLedger.EarningComponentCode(b.BonusTypeName), b.BonusTypeName, b.GrossBonusAmount, "Bonus", false))
                        .ToList(),
                    AdjustmentEarningLines = empAdjustments.Where(a => a.Amount > 0m)
                        .Select(AdjustmentLine)
                        .ToList(),
                    AdjustmentDeductionLines = empAdjustments.Where(a => a.Amount < 0m)
                        .Select(AdjustmentLine)
                        .ToList(),
                    StatutoryLines = statutoryResult.Lines,
                };
                // POD-B2: the engine's mirror of the legacy `if (includesRecurringPay)` gate. Filtering the
                // component SET (rather than zeroing amounts) is what makes the two paths still emit the
                // same rows: BASIC's EmitWhenZero flag would otherwise emit a 0.00 BASIC line here while
                // the legacy branch skipped it entirely. Bonus / Adjustment / Statutory providers survive —
                // those are exactly the supplemental families a non-recurring run pays.
                var effectiveComponents = includesRecurringPay
                    ? payComponents
                    : payComponents.Where(c => IsSupplementalPayComponent(c)).ToList();
                var computation = PayComponentEngine.Compute(effectiveComponents, payCtx);
                // POD-C3 — the proration narrative is applied to the ENGINE'S OUTPUT rather than inside
                // the engine, so PayComponentEngine itself is untouched and the golden-master/equivalence
                // tests keep their meaning. WithProrationNote is the identity function when the note is
                // empty, which it always is for an employee employed the whole period.
                foreach (var line in computation.Earnings)
                    AddEarning(tenantId, id, e.Id, line.Code,
                        line.Code == "BASIC" ? WithProrationNote(line.Name, prorationNote) : line.Name,
                        line.Amount, line.Source, glDriverKey: line.GlDriverKey);
                foreach (var line in computation.Deductions)
                    AddDeduction(tenantId, company.Id, id, e.Id, line.Code,
                        line.Code == "FIXED_DEDUCTION" && policy.Prorates(ProratedComponentCodes.FixedDeduction)
                            ? WithProrationNote(line.Name, prorationNote) : line.Name,
                        line.Amount, line.Source, isEmployerContribution: line.IsEmployerContribution,
                        glDriverKey: line.GlDriverKey);
            }

            // ── POD-C3: ARREARS LINES — ITEMISED PER COVERED PERIOD ─────────────────────────────────
            // Emitted OUTSIDE both branches, exactly like POD-B1b's bonus withholding, so the legacy
            // block and the component engine stay provably identical (neither owns arrears). One line per
            // (covered period, component) so an employee and an auditor can both see WHICH months the
            // number covers — never one opaque "arrears" figure. EarningDriverKey routes each to the
            // component's OWN expense account (a retro basic increase debits Basic Salary Expense).
            foreach (var line in empArrears.OrderBy(a => a.CoveredYear).ThenBy(a => a.CoveredMonth)
                                           .ThenBy(a => a.ComponentCode, StringComparer.Ordinal))
            {
                if (line.Amount <= 0m) continue;
                AddEarning(tenantId, id, e.Id, line.ComponentCode,
                    $"Arrears — {PayrollArrearsComponents.Label(line.ComponentCode)} ({line.CoveredYear}-{line.CoveredMonth:D2})",
                    line.Amount, "Arrears");
                arrearsLinesToPersist.Add(line);
            }

            // ── POD-C1: THE SETTLEMENT LINES, EMITTED VERBATIM ──────────────────────────────────────
            // Emitted OUTSIDE both emission branches, exactly like POD-B1b's bonus withholding and POD-C3's
            // arrears, so the legacy inline block and the component engine stay provably identical (neither
            // owns settlements). NOTHING IS RECOMPUTED HERE: every amount, name and quantity is the
            // persisted plan that was approved and accrued to 2320, which is what makes the payslip and the
            // journal the same numbers by construction rather than by two implementations agreeing.
            //   • Earnings carry Source="Settlement" → EarningDriverKeyFor routes each to its OWN expense
            //     account (5110/5111/5112/5099), and BuildPayrollGlEntries swaps the debit for the stored
            //     2320 payable so the cost is recognised exactly once, at approval.
            //   • The settlement's own deductions carry Source="Settlement" → DED:SETTLEMENT_RECOVERY
            //     (5113 contra-expense), never DED:OTHER (2199), which nothing would ever clear.
            //   • Loan/advance recovery is emitted below through the EXISTING LOAN_EMI/ADVANCE_EMI lines,
            //     and receivable recovery through POD-C3's existing block — one mechanism each, so there
            //     is no double-recovery to guard against.
            if (empSettlement is not null)
            {
                foreach (var line in empSettlementLines.Where(l => l.LineType == FinalSettlementLineTypes.Earning && l.Amount > 0m))
                    AddEarning(tenantId, id, e.Id, line.ComponentCode, line.ComponentName, line.Amount,
                        FinalSettlementComponents.SettlementSource);
                // Capped at the settlement's gross (settlementDeductionTotal), pro rata across the lines,
                // so the sum emitted can never exceed what the settlement earns.
                var plannedDeductions = Math.Round(empSettlementLines
                    .Where(l => l.LineType == FinalSettlementLineTypes.Deduction).Sum(l => l.Amount), 2);
                var dedScale = plannedDeductions > 0m && settlementDeductionTotal < plannedDeductions
                    ? settlementDeductionTotal / plannedDeductions
                    : 1m;
                foreach (var line in empSettlementLines.Where(l => l.LineType == FinalSettlementLineTypes.Deduction && l.Amount > 0m))
                {
                    var amount = Math.Round(line.Amount * dedScale, 2);
                    if (amount <= 0m) continue;
                    AddDeduction(tenantId, company.Id, id, e.Id, line.ComponentCode, line.ComponentName, amount,
                        FinalSettlementComponents.SettlementSource);
                }
                settlementsDisbursed.Add(empSettlement);
                // The leave-balance rows this settlement encashes, so Process can decrement the EXACT rows
                // the plan named (witnessed for the void) instead of re-deriving them.
                foreach (var line in empSettlementLines.Where(l =>
                             l.ComponentCode == FinalSettlementComponents.LeaveEncashment
                             && l.SourceEntityId is Guid && l.Quantity > 0m))
                    settlementEncashmentTaken.Add((line.SourceEntityId!.Value, e.Id, line.Quantity));
            }

            // ── POD-C3 (POD-B3 handoff): the 1420 recovery deduction ────────────────────────────────
            // Source "Recovery" routes to DED:RECEIVABLE_RECOVERY, which CREDITS the 1420 ASSET the void
            // debited. Routing it to DED:OTHER (2199) would credit a liability nobody owes and leave the
            // receivable ageing forever — the exact gap B3 handed to C3.
            if (receivableRecovery > 0m)
                AddDeduction(tenantId, company.Id, id, e.Id,
                    PayrollRecoveryComponents.ReceivableRecovery, PayrollRecoveryComponents.ReceivableRecoveryName,
                    receivableRecovery, PayrollRecoveryComponents.RecoverySource);

            // POD-B1b — bonus withholding. Emitted OUTSIDE the branch so the legacy block and the
            // component engine stay provably identical (neither owns bonus tax). Source="Tax" routes it
            // to DED:TAX (2102) via the same driver every other PIT line uses, so it accrues on Lock and
            // clears through the existing POD-B1 TAX remittance with no new machinery. Zero rows emitted
            // for GCC tenants (ResolveTaxRate → 0), which is why no existing run changes shape.
            if (totalBonusTax > 0m)
                AddDeduction(tenantId, company.Id, id, e.Id,
                    BonusGlDescriptions.PayrollTaxComponentCode, BonusGlDescriptions.PayrollTaxComponentName,
                    totalBonusTax, "Tax");
        }
        failingEmployeeId = null; failingEmployeeCode = null;
        await progress.FinishAsync(cancellationToken);

        // ── F2: PAYSLIP LINES == SLIP AGGREGATES, PER EMPLOYEE, ON EVERY RUN ─────────────────────────
        // See PayslipLineInvariant: Σ lines ≠ the slip aggregates means the Lock journal cannot balance —
        // the pre-F2 defect. Checked HERE, at Process, against the lines actually emitted (both emission
        // paths), so a divergence is NAMED per employee as a non-overridable Error that blocks Approve and
        // Lock, instead of surfacing later as an anonymous gl_unbalanced 422. The check itself is a pure
        // per-employee function; this block only groups the staged lines by employee (one pass, O(lines)).
        var lineMismatches = new List<(int EmpId, string Code, string Name, PayslipLineInvariant.Mismatch M)>();
        {
            var earnByEmp = _db.ChangeTracker.Entries<PayrollEarning>()
                .Where(x => x.State == EntityState.Added && x.Entity.PayrollRunId == id)
                .Select(x => x.Entity).ToLookup(x => x.EmployeeId);
            var dedByEmp = _db.ChangeTracker.Entries<PayrollDeduction>()
                .Where(x => x.State == EntityState.Added && x.Entity.PayrollRunId == id)
                .Select(x => x.Entity).ToLookup(x => x.EmployeeId);
            foreach (var slip in slips)
                if (PayslipLineInvariant.Check(slip, earnByEmp[slip.EmployeeId], dedByEmp[slip.EmployeeId]) is { } m)
                    lineMismatches.Add((slip.EmployeeId, slip.EmployeeCode, slip.EmployeeName, m));
        }

        // POD-B2 (M4) — refuse the whole run BEFORE anything is written, rather than shipping an
        // unlockable one. Throws out of the execution strategy so the transaction rolls back and the
        // caller gets a specific 422 naming every employee and their shortfall.
        if (negativeNetEmployees.Count > 0)
            throw new PayrollProcessAbortException(422, new
            {
                error   = "negative_net_unsupported",
                message = $"{negativeNetEmployees.Count} employee(s) on this supplemental run have deductions exceeding " +
                          "their supplemental earnings. POD-B2 correction/supplementary runs are ADDITIVE-ONLY — a " +
                          "negative delta (clawback) has no vehicle here and would produce a run that can never be " +
                          "locked or deleted. Reduce the negative adjustments, or recover the amount through the " +
                          "next regular run's deductions.",
                employees = negativeNetEmployees,
            });

        _db.PayrollSlips.AddRange(slips);
        // POD-C3 — the arrears sub-ledger, stamped with the settling run. Persisted INSIDE the run
        // transaction alongside the earning lines it produced, so a mid-run fault can never leave a
        // "Settled" line behind an earning that was rolled back.
        if (arrearsLinesToPersist.Count > 0) _db.PayrollArrearsLines.AddRange(arrearsLinesToPersist);
        // POD-B1b-FIX (P2-1) — pin the run's legal entity NOW, at Process, instead of re-deriving it at
        // Lock. BonusGlLedger.BuildPayrollClearingAsync fell back to "the tenant's single active company"
        // when run.CompanyId was null (BonusGlLedger.cs:174-183); if a second company were activated
        // between Process and Lock that fallback would resolve to nothing, no clearing would be planned,
        // and the bonus would be expensed a second time — the exact double-count this pod exists to kill.
        // `company` is the same value that fallback would compute and it has already been fail-loud
        // validated above (:566 company_not_resolved), so persisting it removes the race entirely.
        // Assigning a previously-null CompanyId is the sanctioned "repair" path in the write-side company
        // guard (ZayraDbContext.cs:366-368) and is access-validated there.
        run.CompanyId ??= company.Id;
        run.Status = "Processed";
        run.ProcessedAtUtc = DateTime.UtcNow;
        run.ProcessedByUserId = request.ActorUserId;
        run.EmployeeCount = slips.Count;
        run.TotalGrossSalary = slips.Sum(s => s.GrossSalary);
        run.TotalDeductions = slips.Sum(s => s.Deductions);
        run.TotalNetSalary = slips.Sum(s => s.NetSalary);
        run.TotalEmployerStatutoryCost = slips.Sum(s => s.EmployerStatutoryTotal);
        // POD-B2 — impact CONSUMPTION is gated on the run's basis. These writes are already scoped by
        // `employeeIdsForRun` (so the selector narrows them for free), but a SUPPLEMENTAL run paid none of
        // the attendance/LOP/leave/overtime this period produced, so marking them "Processed" would starve
        // the Regular run of the period's impacts — the employee would silently lose their OT or gain
        // free absence. Unchanged for every Regular run (includesRecurringPay is always true there).
        //
        // ── POD-B3: WITNESS EVERY CONSUMPTION, IN THE SAME TRANSACTION AS THE CONSUMPTION ─────────────
        // AttendancePayrollImpact and LeavePayrollImpact carry NO PayrollRunId, so once they read
        // "Processed" nothing on earth can say WHICH run consumed them — and a void that could not
        // release them silently dropped the month's LOP, absence and OT from any re-run. The witness rows
        // written here are what the void/reopen unwind replays; they also NARROW the status writes to the
        // exact rows this run actually computed pay from (the lists were loaded above with the identical
        // predicate), so a row inserted mid-Process is no longer marked Processed without being paid.
        //
        // The company id is hoisted out of the closure deliberately. `company` is non-null by the fail-loud
        // company_not_resolved guard above, but the compiler cannot carry that flow state across a local
        // function's capture, so referencing `company.Id` inside Witness raised CS8602. Silencing it with
        // `!` would have left a genuine future null-deref in payroll's write path looking identical to this
        // false positive; binding the already-validated value once removes the warning honestly.
        var witnessCompanyId = company.Id;
        void Witness(string artifactType, Guid artifactId, int employeeId, decimal amount,
                     string? priorStatus, decimal? priorOutstanding = null, decimal? priorRepaid = null,
                     decimal? priorAmountPaid = null, Guid? priorRunId = null) =>
            _db.PayrollRunConsumptions.Add(new PayrollRunConsumption
            {
                TenantId = tenantId, CompanyId = witnessCompanyId, PayrollRunId = id,
                ArtifactType = artifactType, ArtifactId = artifactId, EmployeeId = employeeId,
                Amount = amount, PriorStatus = priorStatus,
                PriorOutstandingBalance = priorOutstanding, PriorTotalRepaid = priorRepaid,
                PriorAmountPaid = priorAmountPaid, PriorPayrollRunId = priorRunId,
            });

        if (includesRecurringPay)
        {
            // ── THE MARK IS A COMPARE-AND-SWAP, AND THE ROW COUNT PROVES WE WON IT ─────────────────────
            // The impact lists were read BEFORE this transaction opened, and payroll takes no attendance
            // day lock. In between, an attendance recompute (AttendanceDerivedArtifacts.UpsertImpacts,
            // under AttendanceDailyRecordLock) can delete a loaded impact and insert its replacement, and
            // a leave cancellation deletes a pending leave deduction. Marking by id alone then matched
            // fewer rows and committed anyway: the witnesses named deleted impacts (nothing for void/
            // reopen to release) and the replacement stayed pending for the next off-cycle run to deduct
            // AGAIN. The same holds for a concurrent sibling run that consumed the row first.
            //
            // Re-checking the load predicate (still not Processed) on the UPDATE makes it a CAS: under
            // READ COMMITTED a row a concurrent writer deleted (or is deleting, in which case we wait for
            // it) is simply not matched. A short count aborts the whole run; the throw rolls back slips,
            // witnesses and every other write in this transaction, exactly like bonus_consumed_concurrently.
            // No attendance lock is taken here on purpose: it would add a second lock order to payroll's
            // transaction for no gain, since a replaced row is detected rather than prevented.
            var attendanceImpactIds = attendanceImpacts.Select(x => x.Id).ToList();
            foreach (var x in attendanceImpacts)
                Witness(PayrollConsumptionArtifacts.AttendanceImpact, x.Id, x.EmployeeId, 0m, x.Status);
            var markedAttendance = await _db.AttendancePayrollImpacts
                .Where(x => x.TenantId == tenantId && attendanceImpactIds.Contains(x.Id) && x.Status != "Processed")
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"), cancellationToken);

            var leaveImpactIds = leaveImpacts.Select(x => x.Id).ToList();
            foreach (var x in leaveImpacts)
                Witness(PayrollConsumptionArtifacts.LeaveImpact, x.Id, x.EmployeeId, x.Amount, x.Status);
            var markedLeave = await _db.LeavePayrollImpacts
                .Where(x => x.TenantId == tenantId && leaveImpactIds.Contains(x.Id) && x.Status != "Processed")
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);


            var consumedOtImpacts = overtimeImpacts
                .Where(x => x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId)
                         && periodOvertimeRequestIds.Contains(x.OvertimeRequestId))
                .ToList();
            var consumedOtIds = consumedOtImpacts.Select(x => x.Id).ToList();
            foreach (var x in consumedOtImpacts)
                Witness(PayrollConsumptionArtifacts.OvertimeImpact, x.Id, x.EmployeeId, x.Amount, x.Status,
                        priorRunId: x.PayrollRunId);
            // Same compare-and-swap: two concurrent runs (or a double-submit) could otherwise both pay this
            // overtime, with the PayrollRunId stamp last-writer-wins.
            var markedOvertime = await _db.OvertimePayrollImpacts
                .Where(x => x.TenantId == tenantId && consumedOtIds.Contains(x.Id) && x.Status != "Processed")
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.PayrollRunId, id).SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);

            // The message deliberately does not claim WHO changed the impacts: an ambiguous-commit retry of a
            // run that did commit also lands here (its own commit marked them), and reloading shows that.
            if (markedAttendance != attendanceImpactIds.Count || markedLeave != leaveImpactIds.Count
                || markedOvertime != consumedOtIds.Count)
                throw new PayrollProcessAbortException(409, new
                {
                    error    = "payroll_impacts_changed_concurrently",
                    message  = "The impacts this run uses changed while it was processing. Reload the run and process it again.",
                    attendanceExpected = attendanceImpactIds.Count,
                    attendanceMarked   = markedAttendance,
                    leaveExpected      = leaveImpactIds.Count,
                    leaveMarked        = markedLeave,
                    overtimeExpected   = consumedOtIds.Count,
                    overtimeMarked     = markedOvertime,
                });
        }
        // Adjustments are NOT gated: they are already `PayrollRunId == id`-scoped, i.e. attached to THIS
        // run by the operator, and a supplemental run pays them.
        var consumedAdjustmentIds = approvedAdjustments
            .Where(a => employeeIdsForRun.Contains(a.EmployeeId))
            .Select(a => a.Id)
            .ToList();
        foreach (var a in approvedAdjustments.Where(a => employeeIdsForRun.Contains(a.EmployeeId)))
            Witness(PayrollConsumptionArtifacts.Adjustment, a.Id, a.EmployeeId, a.Amount, a.Status);
        await _db.PayrollAdjustments
            .Where(x => x.TenantId == tenantId && consumedAdjustmentIds.Contains(x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"), cancellationToken);
        var consumedEncashmentIds = approvedAdjustments
            .Where(a => employeeIdsForRun.Contains(a.EmployeeId)
                && a.SourceType == PayrollAdjustmentSources.LeaveEncashment
                && a.SourceId.HasValue)
            .Select(a => a.SourceId!.Value)
            .ToList();
        if (consumedEncashmentIds.Count > 0)
        {
            // The request state follows the normal adjustment inside the same payroll transaction.
            // No separate payment flag can claim success if payroll processing rolls back.
            await Op(_db.LeaveEncashmentRequests)
                .Where(e => e.TenantId == tenantId && consumedEncashmentIds.Contains(e.Id)
                    && e.PayrollRunId == id && e.Status == LeaveEncashmentStatuses.PayrollApproved)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, LeaveEncashmentStatuses.Processed)
                    .SetProperty(e => e.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);
        }

        // COMPLIANCE: Update loan/advance outstanding balances after payroll deduction.
        // Re-load mutable copies for update (AsNoTracking above was read-only).
        await _db.SaveChangesAsync(cancellationToken); // flush slips + deductions first so engine can read them

        // Run centralised validation engine — replaces the old inline result adds.
        // Results are saved in the second SaveChangesAsync below.
        var valDeductions = await Op(_db.PayrollDeductions).AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == id).ToListAsync(cancellationToken);
        var valEarnings = await _db.PayrollEarnings.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PayrollRunId == id).ToListAsync(cancellationToken);
        var valProfiles = payrollProfiles.Values.ToList();
        // Build period-level OT hours map for Rule 11 (OT_RATE_UNRESOLVED).
        var otHoursByEmpForValidation = overtimeImpacts
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));

        // GOSI staleness check: look up the most-recent system-default GOSI effective date
        // for this company's country pack so validation engine can warn if rates are stale.
        DateOnly? gosiRatesEffectiveFrom = await GosiRatesEffectiveFromAsync(company?.CountryCode, cancellationToken);

        var validationCtx = new PayrollValidationContext(
            run, slips, employees, salaryAssignments, valProfiles, valDeductions, valEarnings, company)
        {
            UnverifiedImportedBankDetails  = await UnverifiedImportedBankDetailsAsync(
                tenantId, slips.Select(s => s.EmployeeId).ToList(), cancellationToken),
            OvertimeHoursByEmployee        = otHoursByEmpForValidation,
            AttendanceProcessedEmployeeIds = attendanceProcessedEmpIds,
            UnresolvedPaidAttendanceChanges = await UnresolvedPaidAttendanceChangesAsync(
                tenantId, periodStart, periodEnd, employeeIdsForRun, cancellationToken),
            HeldMissingPunchDays = await AttendanceImpactReconciliation.FindHeldDaysAsync(
                _db, tenantId, employeeIdsForRun, periodStart, periodEnd, cancellationToken),
            GosiRatesEffectiveFrom         = gosiRatesEffectiveFrom,
            HolidayCalendarCoversPeriod    = await CompanyPayrollRules.HasHolidayInPeriodAsync(
                _db, tenantId, company?.Id, periodStart, periodEnd, cancellationToken),
            // POD-B2 — multi-run-per-period facts. Written INSIDE the Process transaction, after the wipe
            // at the top of the strategy delegate, so re-processing never duplicates them.
            EmployeesAlreadyPaidRecurringThisPeriod = alreadyPaidRecurringEmpIds,
            // Mid-year cutover — the same partition decision the YTD figures above were computed from.
            YtdUnresolvedOverlapEmployeeIds         = ytdSources.UnresolvedOverlapEmployeeIds,
            YtdPreCutoverExcludedEmployeeIds        = ytdSources.PreCutoverSlipsExcludedEmployeeIds,
            YtdSuggestedCutover                     = ytdSources.SuggestedCutover,
            Exclusions                              = runPopulation.Exclusions,
            NotEligibleSelections                   = runPopulation.NotEligible,
            SiblingRunCount                         = siblingRuns.Count,
            StatutoryComputedIncrementally          = statutoryComputedIncrementally,
            // Period-to-date employee-side GOSI from the sibling runs, so Rule 2 judges the PERIOD rather
            // than this run in isolation. Derived from the same priorStatutoryByEmp map the incremental
            // netting above used, so the figure Rule 2 credits is exactly the figure that was netted off.
            PriorPeriodGosiEeByEmployee             = BuildPriorPeriodGosiEe(priorStatutoryByEmp),
            // S1/A2(c) — the ONE ceiling: the same effective-dated rule the country pack caps on,
            // instead of a compiled 45,000 the validator would keep warning on after GOSI moves it.
            // statutoryCeiling is decimal.MaxValue when no rule exists (the arrears delta wants "no
            // cap" there). Zero means "not supplied" to the engine, which then applies its own
            // default — so an unseeded tenant keeps the 45,000 warning it has always had rather than
            // silently losing it.
            GosiCoveredWageCeiling                  = statutoryCeiling == decimal.MaxValue ? 0m : statutoryCeiling,
            // S1/A3 — the retirement-age corollary is checked only when an age is configured. (The
            // GOSI entrant cohort is judged per employee from the slips — F02 — so the tenant-wide
            // gosi.new_entrant_scheme_acknowledged flag that used to silence it is no longer read.)
            GosiRetirementAgeYears                  = (int)(await _ruleReader.GetDecimalAsync(
                packCc, packJur, "gosi.retirement_age_years", eff, tenantId, cancellationToken) ?? 0m),
            // GOSI minimum contributory wage — a non-blocking warning only. Platform rows (tenantId: null):
            // GOSI values are statutory and never tenant-specific. /validate reads the same rows.
            GosiMinContributoryWageAnnuities        = await _ruleReader.GetDecimalAsync(
                "SAU", "KSA-mainland", Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiMinContributoryWageAnnuitiesSar,
                eff, null, cancellationToken) ?? 0m,
            GosiMinContributoryWageOccupationalHazards = await _ruleReader.GetDecimalAsync(
                "SAU", "KSA-mainland", Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiMinContributoryWageOccupationalHazardsSar,
                eff, null, cancellationToken) ?? 0m,
        };
        foreach (var r in PayrollValidationEngine.Run(validationCtx))
            _db.PayrollValidationResults.Add(r);

        // ── POD-C3: everything the run DECIDED, said out loud ────────────────────────────────────────
        // Added alongside the engine's output rather than inside PayrollValidationEngine, so the engine's
        // rule set (and its tests) are untouched. All Warning severity — none of these block a lock; they
        // exist so no proration, arrears or debt-deferral decision is ever invisible.
        void C3Warn(string code, int? employeeId, string message) =>
            _db.PayrollValidationResults.Add(new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = id, EmployeeId = employeeId,
                Severity = "Warning", Code = code, Message = message,
            });
        foreach (var (code, empId, msg) in c3Warnings) C3Warn(code, empId, msg);
        // The pay interlock, named per employee and document (Warning — visibility only; see the helper).
        foreach (var finding in await ReadinessPayValidationFindingsAsync(tenantId, id, employees, Zayra.Api.Infrastructure.Employees.BorderNumberRule.PeriodEnd(run.Year, run.Month), cancellationToken))
            _db.PayrollValidationResults.Add(finding);
        // POD-C3 (MF-2) — the shortfall, NAMED. Error severity, so it blocks Approve/Lock exactly as the
        // pre-existing ZERO_NET_WITH_GROSS does; what it adds is the AMOUNT and the most likely cause,
        // which the operator previously had to derive by hand from the payslip.
        foreach (var (empId, code, name, shortfall, prorated, statEe) in recurringShortfallEmployees)
            _db.PayrollValidationResults.Add(new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = id, EmployeeId = empId,
                Severity = "Error", Code = "NEGATIVE_NET_SHORTFALL",
                Message = $"{name} ({code}): deductions exceed earnings by {shortfall:N2}; net was clamped to 0.00, so " +
                          $"they are UNDERPAID by that amount. Loan/advance instalments were already capped at the " +
                          "available net and carried forward, so the remainder is non-debt." +
                          (prorated
                              ? $" This employee's wage is PRORATED for part of the month while their social-insurance " +
                                $"contribution ({statEe:N2}) is computed on the FULL registered monthly wage " +
                                $"(payparameter.proration_gosi_base = '{policy.GosiBase}'). Either set that company " +
                                "policy to 'Prorated', or add a positive adjustment covering the shortfall."
                              : " Reduce the absence/LOP or adjustment deductions, or add a positive adjustment.") +
                          " This Error blocks Approve and Lock, so no unbalanced journal can be posted.",
            });
        // F2 — see lineMismatches. Error severity, and NON-overridable (PayrollValidationOverridePolicy):
        // it states that the journal this run would post cannot balance, which is not a judgement.
        foreach (var (empId, code, name, (earnLines, grossAgg, dedLines, dedAgg)) in lineMismatches)
            _db.PayrollValidationResults.Add(new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = id, EmployeeId = empId,
                Severity = "Error", Code = "PAYSLIP_LINES_MISMATCH",
                Message = $"{name} ({code}): the payslip lines do not add up to the payslip totals — earning lines " +
                          $"{earnLines:N2} vs gross {grossAgg:N2}, deduction lines {dedLines:N2} vs deductions {dedAgg:N2}. " +
                          "Net pay and the GL journal are derived from the totals, so this run cannot post a balanced " +
                          "journal. The usual cause is a pay component whose amount duplicates or bypasses the " +
                          "salary structure (check the pay-component catalog for this company). This Error blocks " +
                          "Approve and Lock and cannot be overridden.",
            });
        foreach (var (empId, code, name, amount) in deferredEmiEmployees)
            C3Warn("WARN_EMI_DEFERRED_PRORATED_PERIOD", empId,
                $"{name} ({code}): {amount:N2} of loan/advance instalment could not be funded by this period's net " +
                "pay and was CARRIED FORWARD, not written off. The outstanding balance is unchanged for that " +
                "portion, so the next run will collect it.");
        foreach (var (empId, code, name, amount) in receivableResidualEmployees)
            C3Warn("WARN_RECEIVABLE_RESIDUAL", empId,
                $"{name} ({code}): {amount:N2} of a prior voided run's disbursed net pay remains outstanding on the " +
                "Employee Overpayment Receivable after this run's recovery. It is carried on the sub-ledger and ages " +
                $"against GET /api/payroll/receivables — it is NOT forgiven.");
        // POD-C3-FIX — see receivableCrossPeriodEmployees: only a recovery taken from a period OTHER than
        // the one the cash was disbursed for is a deduction from wages, and only that one needs sign-off.
        foreach (var (empId, code, name, amount, periods) in receivableCrossPeriodEmployees)
            C3Warn("WARN_RECEIVABLE_RECOVERY_CROSS_PERIOD", empId,
                $"{name} ({code}): {amount:N2} recovered on this {periodStr} run relates to a voided run in " +
                $"{periods}, so it is a DEDUCTION FROM WAGES rather than a decision not to pay the same month " +
                "twice. This run applies NO statutory cap to it — the full outstanding amount is taken up to the " +
                "available net. KSA Labour Law limits how much of an amount paid in excess may be deducted from a " +
                "worker's wage; whether that cap binds here, and at what percentage, requires a Saudi compliance " +
                "officer's sign-off. If it does, recover across several runs by re-processing after part-recovery, " +
                "or agree a repayment plan outside payroll. [FLAG-COMPLIANCE-KSA]");
        foreach (var (emp, reason) in unpayableNonActive)
            C3Warn("WARN_LEAVER_NO_LAST_WORKING_DAY", emp.Id,
                $"{emp.FullName} ({emp.EmployeeCode}): {reason} If they are still owed wages, create an offboarding " +
                "record with the correct last working day, then re-process.");
        foreach (var e in employees.Where(x => leaverEmployeeIds.Contains(x.Id)))
        {
            var pr = prorationByEmp[e.Id];
            C3Warn("WARN_LEAVER_INCLUDED_ON_NOTICE", e.Id,
                $"{e.FullName} ({e.EmployeeCode}) has status '{e.Status}' and IS being paid for {pr.PaidFrom:yyyy-MM-dd}" +
                $"→{pr.PaidTo:yyyy-MM-dd}" + (pr.IsFinalWageMonth ? " (FINAL wage month)" : " (serving notice)") +
                ". Before POD-C3 they were silently dropped from the run and paid nothing.");
        }
        // ── POD-C1: what the SETTLEMENT side of this run decided, said out loud ──────────────────────
        foreach (var (empId, code, name, planned, taken) in settlementRecoveryReduced)
            C3Warn("WARN_SETTLEMENT_RECOVERY_REDUCED", empId,
                $"{name} ({code}): the settlement planned to recover {planned:N2} of outstanding loan/advance but only " +
                $"{taken:N2} could be taken from this payment. The balance is NOT written off — it stays on the loan " +
                "sub-ledger and is dispositioned when the settlement reaches Paid (reclassified to the Employee " +
                "Overpayment Receivable up to what 1400/1410 actually carries, and reported when it cannot be).");
        if (settlementsDisbursed.Count > 0)
        {
            foreach (var s in settlementsDisbursed)
                C3Warn("WARN_SETTLEMENT_DISBURSED", s.EmployeeId,
                    $"{s.EmployeeName} ({s.EmployeeCode}): this run disburses their FINAL SETTLEMENT of " +
                    $"{s.NetPayable:N2} {s.Currency} (last working day {s.LastWorkingDay:yyyy-MM-dd}, reason " +
                    $"'{s.TerminationReason}'). Their wages through the last working day were paid by the run that " +
                    "produced their final-wage-month payslip and are NOT repeated here.");
            // [FLAG-COMPLIANCE-KSA] THE STATUTORY DECISION, RESTATED ON EVERY RUN THAT DISBURSES ONE.
            // End-of-service gratuity, leave encashment and payment in lieu of notice are treated as NOT
            // GOSI-bearing: the contributory wage is the MONTHLY REGISTERED wage (basic + housing,
            // SalaryBreakdown.GosiCoveredWage) and a terminal lump sum is not a monthly wage. This is also
            // mechanically self-enforcing — a settlement run pays no recurring salary, so Process zeroes
            // fullBasic/fullHousing, and settlement components enter NEITHER the basic nor the housing slot
            // of the statutory input; the pack therefore returns zero and POD-A1's reconciliation, which
            // rebuilds `expected` from those same columns plus the bonus/arrears sub-ledgers, still ties out
            // with no change to A1 at all.
            C3Warn("WARN_SETTLEMENT_GOSI_TREATMENT_REQUIRES_SIGNOFF", null,
                $"This run disburses {settlementsDisbursed.Count} termination settlement(s) totalling " +
                $"{settlementsDisbursed.Sum(s => s.GrossPayable):N2} gross. NONE of it is treated as GOSI-bearing: " +
                "the contributory wage is the monthly registered wage, and a terminal lump sum is not one. Whether " +
                "leave encashment specifically is contributory, and whether the last working day requires Mudad " +
                "de-registration in this month, requires a Saudi compliance officer's sign-off. [FLAG-COMPLIANCE-KSA]");
        }
        if (policy.ProratesStatutoryBase && employees.Any(x => prorationByEmp[x.Id].IsProrated))
            C3Warn("WARN_PRORATED_GOSI_BASE_REQUIRES_SIGNOFF", null,
                "This legal entity computes the social-insurance contributory wage on the PRORATED wage in a " +
                "joining/leaving month (payparameter.proration_gosi_base = 'Prorated'). GOSI assesses a MONTHLY " +
                "contributory wage and the portal has no partial-month proration of it, so this may UNDER-REMIT. It is " +
                "also inconsistent with the treatment of unpaid absence, where LOP does not reduce the covered wage. " +
                "Requires a Saudi compliance officer's sign-off. [FLAG-COMPLIANCE-KSA]");

        // POD-B2 (M10) — the WHOLE loan/advance decrement block is gated, not each slip line. A
        // supplemental run collected no EMI (loanEmi/advEmi are 0 above), so decrementing
        // OutstandingBalance / TotalRepaid or marking a LoanInstallment "Paid" here would retire debt the
        // employee never actually repaid.
        // (Pre-existing asymmetry, NOT amplified here: loans are scoped by `employeeIdsForRun` while
        //  bonuses are scoped by `processedEmpIds` further down.)
        //
        // POD-C1 — a SETTLEMENT run reaches this block even though it pays no recurring salary: a leaver's
        // debts are recovered from their final payment. It is still per-loan and driven entirely by
        // loanTakenByEmployee (which only has entries for what was actually withheld), so no non-settling
        // employee's balance can be touched by it.
        var runDecrementsDebt = includesRecurringPay || settlementsDisbursed.Count > 0;
        var activeLoansMutable = !runDecrementsDebt ? new List<EmployeeLoan>() : await Op(_db.EmployeeLoans)
            .Where(l => l.TenantId == tenantId && l.CompanyId == loanCompanyId && !l.IsDeleted && l.CollectionStatus != "OnHold" && l.RepaymentMethod == "PayrollDeduction" && (l.Status == "Active" || l.Status == "Overdue") && l.EmployeeIntId != null && employeeIdsForRun.Contains(l.EmployeeIntId.Value) && l.OutstandingBalance > 0
                && (!l.RepaymentStartDate.HasValue || l.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);
        var activeAdvMutable = !runDecrementsDebt ? new List<SalaryAdvance>() : await Op(_db.SalaryAdvances)
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null && employeeIdsForRun.Contains(a.EmployeeIntId.Value) && a.OutstandingBalance > 0
                && (!a.RepaymentStartDate.HasValue || a.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);

        // POD-B3 — the loan witness is UNCONDITIONAL and PER-LOAN, and it records the balances as they were
        // BEFORE this run touched them. Neither of the two things a void could otherwise reason from is
        // sufficient: the slip carries ONE aggregate LOAN_EMI deduction per EMPLOYEE (`empLoans.Sum(...)`
        // above), so an employee with two loans has no per-loan attribution; and the installment stamp
        // below only happens `if (inst is not null)`, so a loan with no schedule row — exactly what the
        // seeders create — is decremented leaving no trace at all. Recomputing `InstallmentAmount` at void
        // time is not a fallback either: a schedule edited between the run and the void would restore a
        // different number than was taken, silently corrupting 1400/1410 and the sub-ledger together.
        foreach (var loan in activeLoansMutable)
        {
            // POD-C3 (MF-2) — decrement by what the run ACTUALLY took, not by a recomputed instalment.
            // Debt is now capped at the net the period can fund, so recomputing Math.Min(Installment,
            // Outstanding) here would retire more debt than was withheld and put the loan sub-ledger and
            // the 1400 receivable permanently out of step with the payslip.
            var deducted = loanTakenByEmployee.TryGetValue(loan.EmployeeIntId ?? 0, out var lm)
                        && lm.TryGetValue(loan.Id, out var lt)
                ? lt
                : 0m;
            if (deducted <= 0) continue;
            Witness(PayrollConsumptionArtifacts.Loan, loan.Id, loan.EmployeeIntId ?? 0, deducted,
                    loan.Status, loan.OutstandingBalance, loan.TotalRepaid);
            loan.OutstandingBalance -= deducted;
            loan.TotalRepaid += deducted;
            if (loan.OutstandingBalance <= 0) loan.Status = "Closed";
            // Record the paid installment
            var inst = await _db.LoanInstallments
                .OrderBy(i => i.DueDate)
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.LoanId == loan.Id && i.Status == "Pending" && i.DueDate <= periodEnd, cancellationToken);
            // POD-C3 — only a FULLY funded instalment closes its schedule row. When the period could not
            // fund the whole instalment the balance was carried forward, so stamping the row "Paid" would
            // understate the remaining schedule while OutstandingBalance (correctly) still carries it.
            if (inst is not null && deducted >= Math.Min(loan.InstallmentAmount, deducted + loan.OutstandingBalance))
            {
                Witness(PayrollConsumptionArtifacts.LoanInstallment, inst.Id, loan.EmployeeIntId ?? 0, deducted,
                        inst.Status, priorAmountPaid: inst.AmountPaid, priorRunId: inst.PayrollRunId);
                inst.Status = "Paid"; inst.PaidDate = DateOnly.FromDateTime(DateTime.UtcNow); inst.PayrollRunId = id; inst.AmountPaid = deducted;
            }
        }
        foreach (var adv in activeAdvMutable)
        {
            // POD-C3 (MF-2) — same rule as loans: decrement what was actually withheld.
            var deducted = advTakenByEmployee.TryGetValue(adv.EmployeeIntId ?? 0, out var am)
                        && am.TryGetValue(adv.Id, out var at)
                ? at
                : 0m;
            if (deducted <= 0) continue;
            Witness(PayrollConsumptionArtifacts.Advance, adv.Id, adv.EmployeeIntId ?? 0, deducted,
                    adv.Status, adv.OutstandingBalance, adv.TotalRepaid);
            adv.OutstandingBalance -= deducted;
            adv.TotalRepaid += deducted;
            if (adv.OutstandingBalance <= 0) adv.Status = "Closed";
            var inst = await _db.AdvanceInstallments
                .OrderBy(i => i.DueDate)
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.AdvanceId == adv.Id && i.Status == "Pending" && i.DueDate <= periodEnd, cancellationToken);
            // POD-C3 — same partial-funding rule as loans.
            if (inst is not null && deducted >= Math.Min(adv.InstallmentAmount, deducted + adv.OutstandingBalance))
            {
                Witness(PayrollConsumptionArtifacts.AdvanceInstallment, inst.Id, adv.EmployeeIntId ?? 0, deducted,
                        inst.Status, priorAmountPaid: inst.AmountPaid, priorRunId: inst.PayrollRunId);
                inst.Status = "Paid"; inst.PaidDate = DateOnly.FromDateTime(DateTime.UtcNow); inst.PayrollRunId = id; inst.AmountPaid = deducted;
            }
        }

        // ── POD-C3: apply the 1420 RECOVERY to the sub-ledger, witnessed for the void ────────────────
        // The witness records the PRIOR RecoveredAmount so PayrollVoidService can restore it exactly,
        // using the same replay machinery B3 built for loans and advances. Without it, voiding a
        // replacement run would leave the receivable recorded as recovered by a run that no longer exists.
        if (receivableTakenByEmployee.Count > 0)
        {
            var recoveredIds = receivableTakenByEmployee.Select(r => r.ReceivableId).Distinct().ToList();
            var recoveredRows = await Op(_db.PayrollEmployeeReceivables)
                .Where(r => r.TenantId == tenantId && recoveredIds.Contains(r.Id))
                .ToListAsync(cancellationToken);
            foreach (var (rcvId, empId, amount) in receivableTakenByEmployee)
            {
                var row = recoveredRows.FirstOrDefault(r => r.Id == rcvId);
                if (row is null) continue;
                Witness(PayrollConsumptionArtifacts.EmployeeReceivable, row.Id, empId, amount,
                        row.Status, priorAmountPaid: row.RecoveredAmount);
                row.RecoveredAmount = Math.Round(row.RecoveredAmount + amount, 2);
                row.RecoveredByRunId = id;
                row.UpdatedAtUtc = DateTime.UtcNow;
                if (row.RecoveredAmount >= row.Amount) row.Status = PayrollReceivableStatuses.Recovered;
            }
        }

        // ── POD-C1: CONSUME THE SETTLEMENTS THIS RUN DISBURSES ───────────────────────────────────────
        // Stamping the run id is what makes FinalSettlementGlLedger.BuildPayrollClearingAsync find the
        // payable to clear at Lock, and what makes "a settlement can only be disbursed once" enforceable
        // (Approved + PayrollRunId == null is the eligibility predicate everywhere).
        if (settlementsDisbursed.Count > 0)
        {
            var disbursedIds = settlementsDisbursed.Select(s => s.Id).ToList();
            // ── F10: CLAIM THE SETTLEMENTS UNDER A ROW LOCK ──────────────────────────────────────────
            // Tracked copies, re-loaded INSIDE the transaction (the snapshot above is AsNoTracking so it
            // survives a retry), and now LOCKED before they are checked. This used to be a plain read: a
            // run that read an Approved settlement while an external payment was committing went on to
            // overwrite the Paid settlement with Disbursing, so the leaver was paid by bank transfer AND by
            // this run. The run now takes the same locks, in the same order, as recording an external
            // payment (and offboarding Complete / Cancel): the settlements' offboardings, then the
            // settlements, each in id order so two runs cannot deadlock either. Whoever locks second waits,
            // then sees the first one's result: a run that finds a Paid (or another run's) settlement
            // writes nothing; a payment that finds a Disbursing settlement is refused.
            var disbursedOffboardingIds = settlementsDisbursed.Select(s => s.OffboardingId).Distinct().ToList();
            await Op(_db.EmployeeOffboardings)
                .TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
                .Where(o => o.TenantId == tenantId && disbursedOffboardingIds.Contains(o.Id))
                .OrderBy(o => o.Id)
                .Select(o => o.Id)
                .ToListAsync(cancellationToken);
            var lockedSettlements = await Op(_db.EmployeeFinalSettlements)
                .TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
                .Where(s => s.TenantId == tenantId && disbursedIds.Contains(s.Id))
                .OrderBy(s => s.Id)
                .ToListAsync(cancellationToken);
            // The Approved + PayrollRunId == null predicate, re-checked on the LOCKED rows: a compare-and-swap.
            var settlementsMutable = lockedSettlements
                .Where(s => s.Status == FinalSettlementStatuses.Approved && s.PayrollRunId == null)
                .ToList();
            if (settlementsMutable.Count != settlementsDisbursed.Count)
            {
                var lost = lockedSettlements.Where(s => !settlementsMutable.Contains(s)).ToList();
                var paidOutside = lost.Count(s => s.PaidOutsidePayroll);
                throw new PayrollProcessAbortException(409, new
                {
                    error    = "settlement_consumed_concurrently",
                    message  = $"{settlementsDisbursed.Count - settlementsMutable.Count} settlement(s) selected by this " +
                               "run were " + (paidOutside > 0 ? "recorded as paid outside payroll" : "disbursed by another payroll run") +
                               " (or cancelled) while it was processing. Nothing was written — re-process this run.",
                    expected = settlementsDisbursed.Count,
                    stamped  = settlementsMutable.Count,
                    settlements = lost.Select(s => new { settlementId = s.Id, s.EmployeeCode, s.Status, s.PaidOutsidePayroll }).ToList(),
                });
            }
            foreach (var s in settlementsMutable)
            {
                Witness(PayrollConsumptionArtifacts.FinalSettlement, s.Id, s.EmployeeId, s.NetPayable, s.Status);
                s.PayrollRunId = id;
                s.Status = FinalSettlementStatuses.Disbursing;
                s.UpdatedAtUtc = DateTime.UtcNow;
            }

            // Leave encashment is only real once the BALANCE is decremented. The pre-C1 endpoint computed
            // an encashment figure and never wrote EmployeeLeaveBalance.Encashed back at all, so the same
            // days could be encashed again on a second call. Witnessed with the PRIOR value so POD-B3's
            // void restores the row exactly rather than subtracting a re-derived number.
            if (settlementEncashmentTaken.Count > 0)
            {
                var balanceIds = settlementEncashmentTaken.Select(x => x.BalanceId).Distinct().ToList();
                var balanceRows = await _db.EmployeeLeaveBalances
                    .Where(b => b.TenantId == tenantId && balanceIds.Contains(b.Id))
                    .ToListAsync(cancellationToken);
                foreach (var (balanceId, empId, days) in settlementEncashmentTaken)
                {
                    var row = balanceRows.FirstOrDefault(b => b.Id == balanceId);
                    if (row is null || days <= 0m) continue;
                    Witness(PayrollConsumptionArtifacts.LeaveEncashment, row.Id, empId, days,
                            priorStatus: null, priorAmountPaid: row.Encashed);
                    row.Encashed = Math.Round(row.Encashed + days, 2);
                    row.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        // BONUS: mark consumed bonuses as PaidInPayroll so MarkBatchPaid() cannot double-pay.
        // Only bonuses for employees that were actually processed (had a payslip generated) are
        // consumed here. Employees with no salary assignment are skipped in the per-employee loop,
        // so their pending bonuses stay Approved for the next period or manual payment.
        var processedEmpIds = slips.Select(s => s.EmployeeId).ToHashSet();
        var toConsumeBonuses = pendingBonuses
            .Where(b => processedEmpIds.Contains(b.EmployeeIntId!.Value))
            .ToList();
        if (toConsumeBonuses.Count > 0)
        {
            var consumedBonusIds = toConsumeBonuses.Select(b => b.Id).ToHashSet();
            var consumedBatches  = toConsumeBonuses.Select(b => b.BonusBatchId).Distinct().ToList();
            // POD-B2 (M3) — CONDITIONAL stamp + affected-row assertion.
            //
            // The selection at the top of Process filters `PayrollRunId == null` at READ time; this stamp
            // used to key only on the id set. Pre-B2 that was unreachable (one run per period), but a
            // Regular and an OffCycle run for the same period can now be Processed CONCURRENTLY: both
            // would read the bonus as unconsumed, both would emit a Bonus earning, both would pay it into
            // net and WPS, and the stamp would be last-writer-wins. The accrual side then HIDES it —
            // BonusGlLedger.BuildPayrollClearingAsync reads `PayrollRunId == runId` and the cursor caps at
            // the outstanding position, so the payable clears exactly once, the losing run's bonus falls
            // through to the un-accrued EARN:BONUS remainder debit, and the journal still BALANCES. Cash
            // leaves twice, the accrual retires once, nothing reconciles: POD-B1b's double-count coming
            // back through a door B2 opens.
            //
            // The re-checked predicate makes the stamp a compare-and-swap, and the row count proves we won
            // it. ExecuteUpdateAsync runs inside this transaction, so aborting rolls the slips back too.
            var stampedBonusCount = await Op(_db.EmployeeBonuses)
                .Where(b => b.TenantId == tenantId && consumedBonusIds.Contains(b.Id) && b.PayrollRunId == null && b.Status == "Approved")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "PaidInPayroll")
                    .SetProperty(b => b.PayrollRunId, id), cancellationToken);
            if (stampedBonusCount != toConsumeBonuses.Count)
                throw new PayrollProcessAbortException(409, new
                {
                    error    = "bonus_consumed_concurrently",
                    message  = $"{toConsumeBonuses.Count - stampedBonusCount} bonus(es) selected by this run were consumed " +
                               "by another payroll run while it was processing. Nothing was written — re-process this run.",
                    expected = toConsumeBonuses.Count,
                    stamped  = stampedBonusCount,
                });
            // Lock the batch if all its approved bonuses are now consumed.
            foreach (var batchId2 in consumedBatches)
            {
                var batchHasUnpaid = await Op(_db.EmployeeBonuses).AnyAsync(
                    b => b.TenantId == tenantId && b.BonusBatchId == batchId2 && !b.IsDeleted
                       && b.Status == "Approved" && b.PayrollRunId == null, cancellationToken);
                if (!batchHasUnpaid)
                    await _db.BonusBatches
                        .Where(x => x.Id == batchId2 && x.TenantId == tenantId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Status, "Paid")
                            .SetProperty(x => x.IsLockedByPayroll, true), cancellationToken);
            }
        }

        await WritePayrollAuditAsync(request, "payroll.run.processed", "PayrollRun", run.Id.ToString(), new
        {
            employeeCount   = slips.Count,
            totalNet        = run.TotalNetSalary,
            bonusesConsumed = toConsumeBonuses.Count,
            // POD-B2 — a run's TYPE, BASIS and the population it actually paid are the facts an auditor
            // needs to explain why two runs exist for one month and why one of them paid no salary.
            runType              = run.RunType,
            includesRecurringPay = includesRecurringPay,
            populationMode       = runPopulation.Mode,
            eligibleCount        = eligibleEmployees.Count,
            excludedCount        = runPopulation.Exclusions.Count,
            notEligibleCount     = runPopulation.NotEligible.Count,
            siblingRunCount      = siblingRuns.Count,
            statutoryIncremental = statutoryComputedIncrementally,
            // POD-C3 — WHICH BASIS paid this month, how many people it touched, and what it settled.
            // "Why is this number what it is?" must be answerable from the audit record alone.
            prorationBasis       = policy.Basis,
            prorationBasisSource = policy.BasisSource,
            gosiBasePolicy       = policy.GosiBase,
            arrearsGosiTreatment = policy.ArrearsGosiTreatment,
            proratedEmployees    = employees.Count(x => prorationByEmp[x.Id].IsProrated),
            leaversPaid          = employees.Count(x => leaverEmployeeIds.Contains(x.Id)),
            finalWageMonths      = slips.Count(s => s.IsFinalWageMonth),
            joinersExcluded      = excludedJoiners.Count,
            arrearsLines         = arrearsLinesToPersist.Count,
            arrearsTotal         = Math.Round(arrearsLinesToPersist.Sum(a => a.Amount), 2),
            arrearsGosiBearing   = Math.Round(arrearsLinesToPersist.Where(a => a.IsGosiBearing).Sum(a => a.Amount), 2),
            arrearsEarnedBasisGosiDelta = Math.Round(arrearsLinesToPersist.Sum(a => a.EarnedBasisGosiDelta), 2),
            receivableRecovered  = Math.Round(receivableTakenByEmployee.Sum(r => r.Amount), 2),
            emiDeferred          = Math.Round(deferredEmiEmployees.Sum(x => x.Amount), 2),
            // POD-C1 — which leavers this run settled, and for how much.
            settlementsDisbursed = settlementsDisbursed.Count,
            settlementGross      = Math.Round(settlementsDisbursed.Sum(s => s.GrossPayable), 2),
            settlementNet        = Math.Round(settlementsDisbursed.Sum(s => s.NetPayable), 2),
            settlementIds        = settlementsDisbursed.Select(s => s.Id).ToList(),
            leaveDaysEncashed    = Math.Round(settlementEncashmentTaken.Sum(x => x.Days), 2),
        }, cancellationToken);
            // The selector rows are TRACKED, so the Outcome stamps applied by ResolveRunPopulationAsync
            // persist with this save — inside the run transaction, alongside the slips they describe.
            await _db.SaveChangesAsync(cancellationToken); // second save: validation results + loan/advance decrements

            // The job host fences the lease and records the checkpoint here, INSIDE this transaction, so the
            // run's effects, the checkpoint and the lease fence commit together or not at all.
            if (hooks?.BeforeCommit is { } beforeCommit) await beforeCommit(cancellationToken);
            await tx.CommitAsync(cancellationToken); // commit the whole run atomically
            });
        }
        catch (BenefitPaymentException error)
        {
            return Conflict(new { error = "benefit_payroll_reconciliation_required", message = error.Message });
        }
        catch (PayrollProcessAbortException abort)
        {
            // POD-B2 — a deliberate, pre-commit abort (negative supplemental net, or a bonus consumed by a
            // concurrent run). The transaction was never committed, so nothing was written; surface the
            // specific 4xx instead of letting it escape as a 500.
            return StatusCode(abort.StatusCode, abort.Payload);
        }
        catch (Exception ex) when (AnnotateEmployeeFailure(ex, failingEmployeeId, failingEmployeeCode))
        {
            throw; // never reached: the filter only tags the exception and answers false
        }
        return Ok(run);
    }

    // ─────────────────────── helpers shared with PayrollController (moved verbatim) ───────────────────────

    /// <summary>One employee the pay interlock holds, with WHAT is missing (the readiness items, by label).</summary>
    internal sealed record PayReadinessFinding(
        int EmployeeId, bool IsActive, IReadOnlyList<Zayra.Api.Infrastructure.Employees.ReadinessItem> Items);

    /// <summary>The per-employee detail behind <see cref="ComputePayReadinessAsync"/> — the same evaluator, the
    /// same policy cache, the same split rule (Active ⇒ drift, otherwise hard).</summary>
    internal async Task<IReadOnlyList<PayReadinessFinding>> EvaluatePayReadinessAsync(
        Guid tenantId, IReadOnlyList<Employee> employees, CancellationToken ct, DateOnly? periodEnd = null)
    {
        var findings = new List<PayReadinessFinding>();
        if (employees.Count == 0) return findings;
        // Batch-load snapshots (3 bulk queries), resolve policy per distinct (company, country, nationality).
        var snapshots = await new Zayra.Api.Infrastructure.Employees.EmployeeReadinessEvaluator(_db)
            .LoadSnapshotsAsync(tenantId, employees.Select(e => e.Id).Distinct().ToList(), ct, periodEnd);
        var policyCache = new Dictionary<string, Zayra.Api.Infrastructure.Employees.ResolvedReadinessPolicy>();
        foreach (var emp in employees.GroupBy(e => e.Id).Select(g => g.First()))
        {
            if (!snapshots.TryGetValue(emp.Id, out var snap)) continue;
            var key = $"{emp.CompanyId}|{emp.CountryCode}|{Zayra.Api.Infrastructure.Employees.GccReadinessFloor.NormalizeNationality(emp.Nationality)}";
            if (!policyCache.TryGetValue(key, out var policy))
            {
                policy = await _activationGuard.ResolvePolicyAsync(tenantId, emp.CompanyId, emp.CountryCode, emp.Nationality, ct);
                policyCache[key] = policy;
            }
            // Measured at the pay period's END (a run or wage file is for a period, not for the day it is asked).
            var readiness = _activationGuard.Evaluate(snap, policy, periodEnd);
            if (readiness.PayBlocking.Count == 0 && !readiness.IsBlocked) continue;
            findings.Add(new PayReadinessFinding(
                emp.Id,
                string.Equals(emp.Status, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase),
                readiness.PayBlocking.Concat(readiness.Blocking).ToList()));
        }

        // The Saudi wage file needs an ID for a border-number employee inside the window. When the company has
        // turned "use the Border Number as the employee ID" OFF, that employee is held exactly like any other
        // pay-blocked one (non-Active ⇒ hard, Active ⇒ drift needing acknowledgement).
        var borderIds = await Zayra.Api.Infrastructure.Employees.BorderNumberRule.ResolveWageFileIdsAsync(
            _db, tenantId, employees, periodEnd ?? DateOnly.FromDateTime(DateTime.UtcNow.Date), ct, _ruleReader);
        foreach (var emp in employees.GroupBy(e => e.Id).Select(g => g.First()))
        {
            if (!borderIds.TryGetValue(emp.Id, out var border) || border.Allowed) continue;
            var item = new Zayra.Api.Infrastructure.Employees.ReadinessItem(
                "IqamaNumber", "Iqama (this company does not use the Border Number as the wage-file employee ID)",
                "identity", "statutory", "SA", "pay", "field", "iqamaNumber", null);
            var existing = findings.FindIndex(f => f.EmployeeId == emp.Id);
            if (existing >= 0)
                findings[existing] = findings[existing] with { Items = findings[existing].Items.Concat(new[] { item }).ToList() };
            else
                findings.Add(new PayReadinessFinding(emp.Id,
                    string.Equals(emp.Status, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase), new[] { item }));
        }
        return findings;
    }

    /// <summary>
    /// The pay interlock, NAMED on the run's Validation tab: one finding per held employee saying who they are
    /// and exactly which documents are missing (e.g. "GOSI reference, Qiwa contract number"). WARNING severity on
    /// purpose — this adds visibility, not a new gate. What the hold does is unchanged and stated in the message:
    /// the WPS / bank file refuses a non-Active employee, and asks for explicit acknowledgement before including
    /// an Active one (§6.6 — a mandated salary is never silently withheld). Approve/Lock are not affected.
    /// </summary>
    internal async Task<List<PayrollValidationResult>> ReadinessPayValidationFindingsAsync(
        Guid tenantId, Guid runId, IReadOnlyList<Employee> employees, DateOnly periodEnd, CancellationToken ct)
    {
        var byId = employees.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var results = new List<PayrollValidationResult>();
        foreach (var f in await EvaluatePayReadinessAsync(tenantId, employees, ct, periodEnd))
        {
            var emp = byId[f.EmployeeId];
            var name = string.IsNullOrWhiteSpace(emp.FullName) ? emp.EnglishName : emp.FullName;
            var missing = string.Join(", ", f.Items.Select(i => i.Label).Distinct(StringComparer.OrdinalIgnoreCase));
            results.Add(new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = runId, EmployeeId = f.EmployeeId,
                Severity = "Warning", Code = Infrastructure.Payroll.PayrollPaymentPrerequisites.ReadinessPayBlocked,
                Message = $"{name} ({emp.EmployeeCode}): required before payroll — {missing}. " +
                          (f.IsActive
                              ? "Until it is recorded, exporting the WPS / bank file needs an explicit acknowledgement to include this employee."
                              : "The WPS / bank file will refuse this employee until this is recorded."),
            });
        }
        return results;
    }

    /// <summary>
    /// POD-B2 — is this pay component payable on a SUPPLEMENTAL-basis run (one that pays no recurring
    /// salary)? Only the bonus, adjustment and statutory families are: everything else is either the
    /// recurring wage itself or a recurring deduction whose period was already consumed by the run that
    /// paid that wage. Mirrors the legacy emission block's `if (includesRecurringPay)` gate exactly, so
    /// both paths still produce the same rows. Client-added components default to NOT supplemental —
    /// a new custom allowance is recurring unless someone deliberately models it as a bonus/adjustment.
    /// </summary>
    internal static bool IsSupplementalPayComponent(PayComponent c)
    {
        if (c.IsStatutory
            || c.CalcMethod == PayComponentCalcMethods.Statutory
            || c.ProviderKey == PayComponentProviders.Statutory)
            return true;
        return c.ProviderKey is PayComponentProviders.Bonus or PayComponentProviders.Adjustment or PayComponentProviders.Tax;
    }

    /// <summary>The resolved population of a run plus every unhonoured/deliberate deviation from it.</summary>
    internal sealed record RunPopulation(
        List<Employee> Employees,
        List<PayrollRunExclusion> Exclusions,
        List<PayrollRunExclusion> NotEligible,
        List<PayrollRunEmployeeSelection> Selections,
        string Mode);

    /// <summary>
    /// POD-B2 — the SINGLE resolver used by BOTH Process and Validate.
    ///
    /// Validate re-deriving the population independently was a hard break: Rule 1 MISSING_SALARY_STRUCTURE
    /// is an Error raised over ctx.ActiveEmployees, so a run with a hold-out would accumulate blocking
    /// Errors for the very people it deliberately excluded and could NEVER be approved or locked.
    ///
    ///   1. eligible  = the caller's existing Active/not-deleted/company-scoped query (UNCHANGED)
    ///   2. any Include row → ALLOW-LIST: population = eligible ∩ include; else population = eligible
    ///   3. minus every Exclude row, always
    ///   4. each selection row is stamped Included / Excluded / NotEligible
    /// </summary>
    internal async Task<RunPopulation> ResolveRunPopulationAsync(
        Guid tenantId, PayrollRun run, List<Employee> eligible, CancellationToken ct)
    {
        var selections = await Op(_db.PayrollRunEmployeeSelections)
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id)
            .ToListAsync(ct);

        if (selections.Count == 0)
            return new RunPopulation(eligible, new(), new(), selections, "AllEligible");

        var eligibleById = eligible.ToDictionary(e => e.Id);
        var includeIds = selections.Where(s => s.Mode == PayrollRunSelectionModes.Include).Select(s => s.EmployeeId).ToHashSet();
        var excludeIds = selections.Where(s => s.Mode == PayrollRunSelectionModes.Exclude).Select(s => s.EmployeeId).ToHashSet();

        var mode = includeIds.Count > 0 ? "AllowList" : "DenyList";
        var population = (includeIds.Count > 0 ? eligible.Where(e => includeIds.Contains(e.Id)) : eligible.AsEnumerable())
            .Where(e => !excludeIds.Contains(e.Id))
            .ToList();
        var populationIds = population.Select(e => e.Id).ToHashSet();

        var exclusions  = new List<PayrollRunExclusion>();
        var notEligible = new List<PayrollRunExclusion>();
        foreach (var s in selections)
        {
            var emp = eligibleById.TryGetValue(s.EmployeeId, out var e) ? e : null;
            var code = emp?.EmployeeCode ?? s.EmployeeId.ToString();
            var name = emp?.FullName ?? "(not in this run's eligible set)";
            if (populationIds.Contains(s.EmployeeId))
            {
                s.Outcome = PayrollRunSelectionOutcomes.Included;
            }
            else if (emp is null)
            {
                // Named in the selector but not eligible at all — wrong company, not Active, or deleted.
                s.Outcome = PayrollRunSelectionOutcomes.NotEligible;
                notEligible.Add(new PayrollRunExclusion(s.EmployeeId, code, name, s.Reason));
            }
            else
            {
                s.Outcome = PayrollRunSelectionOutcomes.Excluded;
                exclusions.Add(new PayrollRunExclusion(s.EmployeeId, code, name, s.Reason));
            }
        }

        // An eligible employee dropped by ALLOW-LIST mode without ever being named is still a hold-out and
        // must be reported — silence here is exactly the "silently unpay the company" failure mode.
        if (includeIds.Count > 0)
        {
            foreach (var e in eligible)
            {
                if (populationIds.Contains(e.Id) || excludeIds.Contains(e.Id)) continue;
                exclusions.Add(new PayrollRunExclusion(e.Id, e.EmployeeCode, e.FullName,
                    "Not named in this run's allow-list."));
            }
        }

        return new RunPopulation(population, exclusions, notEligible, selections, mode);
    }

    /// <summary>
    /// POD-C3 — the SAME eligible query as above, UNIONED with this period's LEAVERS.
    ///
    /// <para><b>THE LIVE DEFECT THIS FIXES.</b> <c>OffboardingController</c> sets
    /// <c>Employee.Status = "Offboarded"</c> at NOTICE time, and the query above filters
    /// <c>Status == "Active"</c>. So from the day a resignation was keyed, the employee vanished from
    /// every payroll run and was paid NOTHING for the whole notice period — directly contradicting the
    /// product's own documented intent ("Excludes Offboarded — notice-period staff may still be paid",
    /// EmployeesController). Payroll never honoured it. A notice served in August with a last working day
    /// of 15 October now yields full August, full September, 15/30 October — then nothing.</para>
    ///
    /// <para><b>THE UNION IS NARROW ON PURPOSE.</b> The Active set is kept VERBATIM and only added to, so
    /// for any tenant with no offboarding in flight the population is bit-for-bit what it was. The union
    /// re-applies the run's COMPANY scope from <c>Employee.CompanyId</c> (EmployeeOffboarding is
    /// tenant-owned only and carries no company dimension, so without this a group tenant's Company A run
    /// would pull in Company B's leaver), and it STOPS once the final wage month has been paid: an
    /// employee who already has a non-voided <c>IsFinalWageMonth</c> slip is excluded, which is what stops
    /// a settled leaver drawing a second wage through the IsActive-blind salary query.</para>
    /// </summary>
    internal async Task<(List<Employee> Employees, HashSet<int> LeaverIds, Dictionary<int, DateOnly> LastWorkingDays,
                        List<(Employee Employee, string Reason)> UnpayableNonActive, HashSet<int> SettlementIds)>
        LoadEligibleWithLeaversAsync(
            Guid tenantId, Guid companyId, bool allowLegacyUnscopedEmployees, bool asNoTracking,
            DateOnly? periodStart, DateOnly? periodEnd, Guid? excludeRunId, CancellationToken ct,
            bool includeSettlementLeavers = false)
    {
        var q = Op(_db.Employees).Where(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted
            && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)));
        if (asNoTracking) q = q.AsNoTracking();
        var active = await q.ToListAsync(ct);

        var leaverIds = new HashSet<int>();
        var lwdByEmp = new Dictionary<int, DateOnly>();
        var unpayable = new List<(Employee, string)>();
        // ── POD-C1: THE DISBURSEMENT RAIL ────────────────────────────────────────────────────────────
        // A settlement-purpose run pays people the three gates below deliberately exclude, and it must:
        //   • `Status == "Active"` — a leaver past their last working day is Offboarded/Terminated/Exited;
        //   • `LastWorkingDay >= periodStart` — a settlement is normally disbursed in the month AFTER the
        //     LWD, so the leaver union never even considers them;
        //   • `alreadyFinalised` — the non-voided IsFinalWageMonth slip that Guard 4 REQUIRES before a
        //     settlement may be approved is the very thing that removes them from every later run.
        // Without this branch a settlement could accrue to 2320 and never reach anybody: the payable would
        // age forever and the leaver would still have to be paid by manual bank transfer — i.e. the exact
        // problem this pod exists to end. It adds ONLY employees with an Approved settlement and no live
        // disbursement, so for every run that is not a settlement run this is not even queried.
        var settlementIds = new HashSet<int>();
        if (includeSettlementLeavers)
        {
            var activeIds = active.Select(e => e.Id).ToHashSet();
            var awaitingIds = await Op(_db.EmployeeFinalSettlements).AsNoTracking()
                .Where(s => s.TenantId == tenantId
                         && s.Status == FinalSettlementStatuses.Approved
                         && s.PayrollRunId == null
                         && (s.CompanyId == companyId || s.CompanyId == null))
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToListAsync(ct);
            var toAdd = awaitingIds.Where(eid => !activeIds.Contains(eid)).ToList();
            if (toAdd.Count > 0)
            {
                // COMPANY SCOPE re-applied from the employee, exactly as the Active and leaver queries do.
                var settlementQ = Op(_db.Employees).Where(e => e.TenantId == tenantId && !e.IsDeleted
                    && toAdd.Contains(e.Id)
                    && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)));
                if (asNoTracking) settlementQ = settlementQ.AsNoTracking();
                foreach (var e in await settlementQ.ToListAsync(ct))
                {
                    active.Add(e);
                    settlementIds.Add(e.Id);
                }
            }
            foreach (var eid in awaitingIds.Where(activeIds.Contains)) settlementIds.Add(eid);
        }
        if (periodStart is not DateOnly pStart || periodEnd is not DateOnly pEnd)
            return (active, leaverIds, lwdByEmp, unpayable, settlementIds);

        // Non-cancelled offboardings whose last working day lands on/after this period's start. The
        // newest record per employee wins (a re-hire may have an older, completed one).
        var offboardings = await Op(_db.EmployeeOffboardings).AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Status != "Cancelled" && o.LastWorkingDay >= pStart)
            .Select(o => new { o.EmployeeId, o.LastWorkingDay, o.CreatedAtUtc })
            .ToListAsync(ct);
        if (offboardings.Count == 0) return (active, leaverIds, lwdByEmp, unpayable, settlementIds);

        foreach (var g in offboardings.GroupBy(o => o.EmployeeId))
            lwdByEmp[g.Key] = g.OrderByDescending(o => o.CreatedAtUtc).First().LastWorkingDay;

        var candidateIds = lwdByEmp.Keys.ToList();
        // COMPANY SCOPE re-applied from the employee, exactly as the Active query does.
        var leaverQ = Op(_db.Employees).Where(e => e.TenantId == tenantId && !e.IsDeleted
            && candidateIds.Contains(e.Id) && e.Status != "Active"
            && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)));
        if (asNoTracking) leaverQ = leaverQ.AsNoTracking();
        var leavers = await leaverQ.ToListAsync(ct);
        if (leavers.Count == 0) return (active, leaverIds, lwdByEmp, unpayable, settlementIds);

        // STOP CONDITION — the final wage month is paid exactly ONCE. Without it, every subsequent
        // period where periodStart <= LWD would match again and an IsActive-blind salary query would draw
        // a second (and third) final wage.
        var leaverIdList = leavers.Select(e => e.Id).ToList();
        var alreadyFinalised = (await Op(_db.PayrollSlips).AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsFinalWageMonth && s.Status != "Voided"
                     && leaverIdList.Contains(s.EmployeeId)
                     && (excludeRunId == null || s.RunId != excludeRunId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();

        foreach (var e in leavers)
        {
            if (alreadyFinalised.Contains(e.Id)) continue;
            // POD-C1 — already admitted by the settlement branch above; adding them twice would produce
            // two identical Employee entries and therefore two payslips for one person.
            if (settlementIds.Contains(e.Id)) continue;
            var lwd = lwdByEmp[e.Id];
            var joined = e.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(e.JoiningDate);
            if (joined is DateOnly jd && jd > pEnd) continue;   // not employed in this period at all
            if (lwd < pStart) continue;                          // already left before the period
            active.Add(e);
            leaverIds.Add(e.Id);
        }

        // MF-9(c) — a NON-ACTIVE employee with NO last working day anywhere is paid nothing and nothing
        // reports it: the same silent-unpay defect re-entering through a different door. Named, capped.
        var separationStatuses = new[] { EmployeeStatuses.Offboarded, EmployeeStatuses.Terminated, EmployeeStatuses.Exited };
        var orphanQ = await Op(_db.Employees).AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && separationStatuses.Contains(e.Status)
                     && !candidateIds.Contains(e.Id)
                     && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)))
            .OrderBy(e => e.Id)
            .Take(25)
            .ToListAsync(ct);
        // POD-C1 — a leaver being SETTLED by this run is not an "unpayable orphan": their offboarding is
        // simply outside this period's window (a settlement is normally disbursed after the LWD month).
        foreach (var e in orphanQ.Where(e => !settlementIds.Contains(e.Id)))
            unpayable.Add((e, $"Status '{e.Status}' with no offboarding record, so no last working day exists. " +
                               "They are excluded from this run and will be paid nothing."));

        return (active, leaverIds, lwdByEmp, unpayable, settlementIds);
    }

    /// <summary>
    /// POD-B2 — other non-voided runs for the same (company, year, month). The basis for the
    /// ALREADY_PAID_THIS_PERIOD control, the incremental statutory base, the sibling-run warning and the
    /// WPS duplicate-export guard.
    /// </summary>
    internal Task<List<PayrollRun>> LoadSiblingRunsAsync(Guid tenantId, PayrollRun run, Guid companyId, CancellationToken ct) =>
        Op(_db.PayrollRuns).AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id != run.Id
                     && r.Year == run.Year && r.Month == run.Month
                     && (r.CompanyId == companyId || r.CompanyId == null)
                     && r.Status != "Voided")
            .ToListAsync(ct);

    /// <summary>
    /// POD-B2 — collapse the period-to-date statutory map to employee-side GOSI per employee, for
    /// PayrollValidationContext.PriorPeriodGosiEeByEmployee. Classification uses
    /// PayrollValidationEngine.IsGosiEmployeeCode, i.e. the SAME predicate Rule 2 applies to this run's
    /// own deductions, so the period total and the per-run total can never drift apart.
    /// </summary>
    internal static Dictionary<int, decimal> BuildPriorPeriodGosiEe(
        IReadOnlyDictionary<(int EmployeeId, string Code, bool IsEmployer), decimal> priorStatutory)
    {
        var map = new Dictionary<int, decimal>();
        foreach (var kv in priorStatutory)
        {
            if (kv.Key.IsEmployer) continue;
            if (!PayrollValidationEngine.IsGosiEmployeeCode(kv.Key.Code)) continue;
            map[kv.Key.EmployeeId] = map.GetValueOrDefault(kv.Key.EmployeeId) + kv.Value;
        }
        return map;
    }

    /// <summary>Rule 5c input: the employees whose imported bank details are still waiting for a second person.</summary>
    internal async Task<IReadOnlySet<int>> UnverifiedImportedBankDetailsAsync(Guid tenantId, List<int> employeeIds, CancellationToken ct) =>
        (await Op(_db.EmployeeImportGaps).AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.ResolvedAtUtc == null
                     && g.GapType == EmployeeImportGap.BankDetailsUnverified && employeeIds.Contains(g.EmployeeId))
            .Select(g => g.EmployeeId)
            .ToListAsync(ct)).ToHashSet();

    /// <summary>
    /// POD-C3 — appends the proration narrative to a component's display name, e.g.
    /// "Basic salary (18/30 days · 30-day month · joined 2026-11-13)".
    ///
    /// <para>Requirement 7: an employee and an auditor must both be able to see WHY the number is what it
    /// is. Because the note is EMPTY for anyone employed the whole period, this is the identity function
    /// on every existing payslip — which is what lets it be applied on both the legacy emission block and
    /// the component engine's output without changing either.</para>
    /// </summary>
    internal static string WithProrationNote(string name, string note)
        => string.IsNullOrEmpty(note) ? name : $"{name} ({note})";

    internal void AddEarning(Guid tenantId, Guid runId, int employeeId, string code, string name, decimal amount, string source, string? glDriverKey = null) =>
        _db.PayrollEarnings.Add(new PayrollEarning { TenantId = tenantId, PayrollRunId = runId, EmployeeId = employeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source, GlDriverKey = glDriverKey });

    internal void AddDeduction(Guid tenantId, Guid companyId, Guid runId, int employeeId, string code, string name, decimal amount, string source, bool isEmployerContribution = false, string? glDriverKey = null) =>
        _db.PayrollDeductions.Add(new PayrollDeduction { TenantId = tenantId, CompanyId = companyId, PayrollRunId = runId, EmployeeId = employeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source, IsEmployerContribution = isEmployerContribution, GlDriverKey = glDriverKey });

    /// <summary>Earning component group → GL driver key. Bonus source wins over the component code.</summary>
    internal static string EarningDriverKey(string componentCode, string source) =>
        source == "Bonus" ? "EARN:BONUS" : componentCode switch
        {
            "BASIC"            => "EARN:BASIC",
            "HOUSING"          => "EARN:HOUSING",
            "TRANSPORT"        => "EARN:TRANSPORT",
            "OTHER_ALLOWANCES" => "EARN:OTHER_ALLOWANCES",
            "OVERTIME"         => "EARN:OVERTIME",
            // POD-C3 — arrears are the SAME expense as the component they settle, just paid late. Routing
            // them to EARN:OTHER (5099) would move a retro basic-salary increase out of Basic Salary
            // Expense and quietly distort every payroll cost report. The GL stays balanced either way —
            // this is about the expense landing in the account it belongs to.
            PayrollArrearsComponents.Basic           => "EARN:BASIC",
            PayrollArrearsComponents.Housing         => "EARN:HOUSING",
            PayrollArrearsComponents.Transport       => "EARN:TRANSPORT",
            PayrollArrearsComponents.OtherAllowances => "EARN:OTHER_ALLOWANCES",
            // POD-C1 — the termination settlement's own earnings. Before this pod there was no
            // end-of-service account in the catalog at all, so gratuity had nowhere to land but 5099
            // Other Earnings — if it ever reached the GL, which it did not.
            FinalSettlementComponents.Gratuity        => "EARN:EOSB",
            FinalSettlementComponents.LeaveEncashment => "EARN:LEAVE_ENCASHMENT",
            FinalSettlementComponents.NoticePay       => "EARN:NOTICE_PAY",
            _                  => "EARN:OTHER",
        };

    /// <summary>
    /// POD-C3-FIX — the ONE earning-driver resolution both the posted journal and the on-screen preview
    /// use. Extracted because the two call sites previously did
    /// <c>ResolveDriverForComponent(...)?.Key ?? EarningDriverKey(...)</c> inline, and that composition
    /// silently DEFEATED the arrears routing above on every provisioned tenant:
    ///
    /// <para><c>EARN:BASIC</c> is seeded <c>Exact "BASIC"</c> (FinanceGl.cs:166) so it does NOT match
    /// <c>ARREARS_BASIC</c>; the catch-all <c>EARN:OTHER</c> is seeded <c>Any</c> (FinanceGl.cs:171) and
    /// therefore does. A persisted driver is preferred over the compiled switch, so a retro basic-salary
    /// increase landed in <c>5099 Other Earnings</c> for every tenant that has gl_drivers rows — i.e.
    /// every tenant created through <c>GlDriverSeeder</c>. The journal still balanced; the EXPENSE was in
    /// the wrong account, which is precisely what the switch above says must not happen.</para>
    ///
    /// <para><b>The rule, in precedence order.</b>
    /// <list type="number">
    /// <item>A driver that CLAIMS the arrears code by a specific predicate (Exact/Prefix/Suffix) wins
    /// outright — that is a tenant deliberately giving retro pay its own account, a shipped capability
    /// (GlPhase2Tests.cs:226), and this pod must not take it away.</item>
    /// <item>Otherwise an <c>ARREARS_*</c> code resolves EXACTLY AS THE COMPONENT IT SETTLES resolves —
    /// through the tenant's own driver for BASIC/HOUSING/TRANSPORT/OTHER_ALLOWANCES, and therefore
    /// through any account remap applied to it. Deliberately NOT four new seeded driver keys: a tenant
    /// who remapped <c>EARN:BASIC</c> to their own chart would then have arrears keep posting to the
    /// seeded 5001 — the same divergence in a new disguise. Following the source component's driver is
    /// correct by construction and needs no data migration.</item>
    /// <item>Everything else is unchanged: persisted driver, else the compiled switch.</item>
    /// </list></para>
    /// </summary>
    internal static string EarningDriverKeyFor(IReadOnlyList<GlDriverRow> drivers, string componentCode, string source)
    {
        var direct = ResolveDriverForComponent(drivers, componentCode, source, GlDriverCategories.Earning);
        if (direct is not null && direct.MatchMode != GlDriverMatchModes.Any) return direct.Key;

        // ── POD-C1 — the SAME defect, one pod later, and it would have been silent ────────────────────
        // EARN:OTHER is seeded `Any` with MatchSource = null (FinanceGl.cs), so on every tenant that HAS
        // gl_drivers rows but has not re-run POST /api/finance/gl/seed-defaults since this pod — i.e. all
        // ~55 on day one, because GlDriverSeeder is add-only and runs only when invoked — it MATCHES
        // EOSB_GRATUITY / LEAVE_ENCASHMENT / NOTICE_PAY, `direct` is non-null, and the compiled switch
        // below is never consulted. The gratuity would land in 5099 Other Earnings, the journal would
        // still balance, and nothing would report it. Adding the codes to EarningDriverKey alone is dead
        // code on that path; this branch is what makes it live. A tenant that deliberately gave the
        // settlement its own account by a SPECIFIC predicate still wins — that is the rule above.
        if (FinalSettlementComponents.IsSettlementEarning(componentCode))
            return EarningDriverKey(componentCode, source);

        var settles = PayrollArrearsComponents.SourceComponent(componentCode);
        if (!string.Equals(settles, componentCode, StringComparison.Ordinal))
            return ResolveDriverForComponent(drivers, settles, source, GlDriverCategories.Earning)?.Key
                   ?? EarningDriverKey(settles, source);

        return direct?.Key ?? EarningDriverKey(componentCode, source);
    }

    /// <summary>
    /// Deduction component group → GL driver key. Also reports the employer side
    /// (Statutory + "-ER" suffix) so the caller can accumulate the paired employer-expense DR.
    /// </summary>
    internal static string DeductionDriverKey(string componentCode, string source, out bool isEmployerSide)
    {
        isEmployerSide = source == "Statutory" && componentCode.EndsWith("-ER");
        return (source, isEmployerSide) switch
        {
            ("Statutory", true)  => "DED:STATUTORY_ER",
            ("Statutory", false) => "DED:STATUTORY_EE",
            ("Tax", _)           => "DED:TAX",
            ("Loan", _)          => "DED:LOAN",
            ("Attendance", _)    => "DED:ATTENDANCE",
            ("Leave", _)         => "DED:LEAVE",
            // POD-C3 — recovery of a prior void's disbursed net pay CREDITS the 1420 receivable, so it
            // must never fall through to DED:OTHER (2199): crediting a payable nobody owes would leave
            // 1420 ageing forever, which is precisely the defect B3 handed to C3.
            ("Recovery", _)      => "DED:RECEIVABLE_RECOVERY",
            // POD-C1 — a settlement-side deduction (notice shortfall under Art. 75/76, other final
            // deductions) is NOT a debt owed to a third party: it REDUCES what the employer owes. Routing
            // it to DED:OTHER (2199) would credit a payable that RemitGroups.ForSource has no remittance
            // path for, so it would age on the balance sheet forever with nothing able to clear it — the
            // same shape of defect POD-C3 fixed for the 1420 recovery. It credits a CONTRA-EXPENSE (5113)
            // instead, so net employment cost is right and every account this pod touches returns to zero.
            (FinalSettlementComponents.SettlementSource, _) => "DED:SETTLEMENT_RECOVERY",
            _ => componentCode switch
            {
                "FIXED_DEDUCTION"     => "DED:FIXED_DEDUCTION",
                PayrollRecoveryComponents.ReceivableRecovery => "DED:RECEIVABLE_RECOVERY",
                FinalSettlementComponents.NoticeShortfall    => "DED:SETTLEMENT_RECOVERY",
                FinalSettlementComponents.OtherDeduction     => "DED:SETTLEMENT_RECOVERY",
                _                     => "DED:OTHER",
            },
        };
    }

    /// <summary>
    /// POD-C1 — the deduction-side twin of <see cref="EarningDriverKeyFor"/>'s precedence rule, shared by
    /// the posted journal and the on-screen preview.
    ///
    /// <para>DED:OTHER is seeded <c>Any</c> with <c>MatchSource = null</c> (FinanceGl.cs), so on any tenant
    /// with gl_drivers rows that has not re-seeded since this pod it CLAIMS <c>NOTICE_SHORTFALL</c> and
    /// <c>SETTLEMENT_DED_OTHER</c> and sends them to 2199 — a payable nothing ever clears. Nulling an
    /// <c>Any</c>-mode claim on a settlement deduction code drops the caller through to the compiled
    /// switch, which routes it to the contra-expense. A driver that claims the code by a SPECIFIC
    /// predicate (Exact/Prefix/Suffix) is a deliberate tenant decision and still wins.</para>
    /// </summary>
    internal static GlDriverRow? ResolveDeductionDriverRow(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source)
    {
        var row = ResolveDriverForComponent(drivers, componentCode, source, GlDriverCategories.Deduction);
        if (row is not null && row.MatchMode == GlDriverMatchModes.Any
            && FinalSettlementComponents.IsSettlementDeduction(componentCode)
            && !string.Equals(row.Key, "DED:SETTLEMENT_RECOVERY", StringComparison.Ordinal))
            return null;
        return row;
    }

    /// <summary>
    /// Loads the company-first GL resolution context for a run: mapping overrides (company row wins
    /// over tenant default per driver), the persisted driver defaults (company row → tenant row), and
    /// the driver set for data-driven routing. IgnoreQueryFilters is intentional and mirrors
    /// CompanyTaxPolicyResolver: GL posting is a SYSTEM read that must see BOTH the run's company rows
    /// and the tenant-default (CompanyId == null) rows regardless of the caller's own company claims;
    /// the WHERE re-applies exact tenant + company/default scope and never reads another tenant.
    /// </summary>
    internal Task<GlResolutionContext> LoadGlResolutionContextAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
        => GlAccountResolver.LoadAsync(_db, tenantId, companyId, ct);

    /// <summary>Per-tenant kill-switch for the data-driven pay-component engine. Default ON: the engine is
    /// proven byte-identical to the legacy compiled sequence by the golden-master + equivalence-twin tests
    /// and falls back to the compiled PayComponentCatalog when the store is empty, so enabling it changes NO
    /// output for any tenant (seeded or not). Setting Payroll/UseComponentEngine to "false"/"0"/"off"
    /// reverts a tenant to the legacy inline block for instant rollback. Explicitly tenant-scoped (does not
    /// rely on the ambient global query filter) so the switch is correct regardless of the resolving
    /// context — this is a per-tenant behaviour toggle, never a cross-tenant read.</summary>
    internal async Task<bool> ResolveUseComponentEngineAsync(Guid tenantId, CancellationToken ct)
    {
        var val = await _db.SystemSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Category == "Payroll" && x.SettingKey == "UseComponentEngine")
            .Select(x => x.SettingValue)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(val)) return true; // default ON
        return val.Trim().ToLowerInvariant() is not ("false" or "0" or "off" or "no" or "disabled");
    }

    /// <summary>Loads the pay-component definitions IN EFFECT for the payroll period starting
    /// <paramref name="periodStart"/> (company-first). Mirrors LoadGlResolutionContextAsync /
    /// CompanyTaxPolicyResolver: IgnoreQueryFilters is intentional — payroll processing is a SYSTEM read
    /// that must see BOTH the run's company rows AND the tenant-default (CompanyId == null) rows regardless
    /// of the caller's own company claims; the WHERE re-applies exact tenant + company/default scope and
    /// never reads another tenant. Version selection, company-first precedence and the compiled-catalog
    /// fallback live in <see cref="PayComponentEngine.ResolveInEffect"/> — the ONE resolver the run and the
    /// catalog API share, so what the API reports as "in effect" is exactly what a run pays.</summary>
    internal async Task<IReadOnlyList<PayComponent>> LoadPayComponentsAsync(
        Guid tenantId, Guid? companyId, DateOnly periodStart, CancellationToken ct)
    {
        var rows = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.PayComponents, tenantId,
                "Payroll pass: the run's company components and the tenant defaults (CompanyId null) together; company re-applied below.")
            .AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted
                     && (c.CompanyId == companyId || c.CompanyId == null))
            .ToListAsync(ct);
        return PayComponentEngine.ResolveInEffect(rows, tenantId, periodStart);
    }

    private const int StaleDaysListed = 200;

    /// <summary>The 422 Process answers when pending attendance impacts no longer match their days (ERR_ATTENDANCE_IMPACT_STALE).</summary>
    internal static object StaleAttendanceRefusal(PayrollRun run, IReadOnlyList<StaleAttendanceDay> stale, IReadOnlyCollection<Employee> employees)
    {
        var employeeCount = stale.Select(d => d.EmployeeId).Distinct().Count();
        var byId = employees.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        return new
        {
            error = "attendance_impact_stale",
            code = AttendanceImpactReconciliation.StaleCode,
            message = $"{stale.Count} attendance day(s) for {employeeCount} employee(s) in {run.Year}-{run.Month:D2} have deductions that " +
                      "no longer match their punches, so this run would deduct the wrong amounts. Recompute those days " +
                      "(Payroll → Validation → Recompute these days), then process the run again. Nothing was saved.",
            employeeCount,
            dayCount = stale.Count,
            days = stale.Take(StaleDaysListed).Select(d => new
            {
                d.EmployeeId,
                employeeCode = byId.TryGetValue(d.EmployeeId, out var e) ? e.EmployeeCode : null,
                employeeName = byId.TryGetValue(d.EmployeeId, out var n) ? n.FullName : null,
                workDate = d.WorkDate, reason = d.Reason,
            }),
            recompute = new { method = "POST", path = $"/api/payroll/runs/{run.Id}/attendance/recompute-stale" },
        };
    }

    /// <summary>
    /// Unresolved "paid attendance changed" attendance exceptions for these employees in the period — Rule 10b's
    /// WARN_PAID_ATTENDANCE_CHANGED. One source for Process and /validate.
    /// </summary>
    internal async Task<IReadOnlyList<PaidAttendanceChange>> UnresolvedPaidAttendanceChangesAsync(
        Guid tenantId, DateOnly from, DateOnly to, IReadOnlyCollection<int> employeeIds, CancellationToken ct)
    {
        var rows = await _db.AttendanceExceptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged && !x.IsResolved
                     && x.WorkDate >= from && x.WorkDate <= to && employeeIds.Contains(x.EmployeeId))
            .OrderBy(x => x.EmployeeId).ThenBy(x => x.WorkDate)
            .Select(x => new { x.EmployeeId, x.WorkDate, x.Details })
            .ToListAsync(ct);
        return rows.Select(r => new PaidAttendanceChange(r.EmployeeId, r.WorkDate, AttendanceDerivedArtifacts.DescribePaidAttendanceChange(r.Details))).ToList();
    }

    /// <summary>
    /// The effective date of the newest platform GOSI rate row — what the staleness warning
    /// (WARN_GOSI_RATES_REQUIRE_SIGNOFF) ages. Read from statutory_rules, the store the payslip computes
    /// from, not from the retired gosi_contribution_rules. Null outside KSA.
    /// </summary>
    internal async Task<DateOnly?> GosiRatesEffectiveFromAsync(string? countryCode, CancellationToken ct)
    {
        if (!string.Equals(countryCode, "SAU", StringComparison.OrdinalIgnoreCase)
         && !string.Equals(countryCode, "SA", StringComparison.OrdinalIgnoreCase)) return null;
        var keys = new[]
        {
            Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiSaudiEmployeeRate, Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiSaudiEmployerRate,
            Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiSanedRate, Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiExpOhRate,
        };
        // Platform statutory rules carry TenantId == null: system-wide reference data, never tenant data.
        var latest = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(_db.StatutoryRules, null,
                "Platform KSA statutory reference rules (TenantId null) for the payroll pass; no tenant rows are read.")
            .AsNoTracking()
            .Where(r => r.CountryCode == CountryCodes.Saudi
                     && r.Jurisdiction == Jurisdictions.KsaMainland && keys.Contains(r.RuleKey))
            .OrderByDescending(r => r.EffectiveFrom)
            .Select(r => (DateTime?)r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        return latest is DateTime d ? DateOnly.FromDateTime(d) : null;
    }

    internal static decimal SumOpeningBalance(IEnumerable<PayrollOpeningBalance>? balances, params string[] balanceTypes)
    {
        if (balances is null) return 0m;
        var accepted = balanceTypes.Select(NormalizeCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return balances
            .Where(x => accepted.Contains(NormalizeCode(x.BalanceType)))
            .Sum(x => x.Amount);
    }

    internal static string NormalizeCode(string value)
    {
        var chars = (value ?? string.Empty).Trim().ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var code = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(code) ? "PAYROLL_ADJUSTMENT" : code;
    }

    internal static string AdjustmentLabel(PayrollAdjustment adjustment)
        => string.IsNullOrWhiteSpace(adjustment.Reason)
            ? $"Payroll adjustment - {adjustment.AdjustmentType}"
            : $"Payroll adjustment - {adjustment.AdjustmentType}: {adjustment.Reason}";

    private static GlDriverRow? ResolveDriverForComponent(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source, string category)
        => GlAccountResolver.ResolveDriverForComponent(drivers, componentCode, source, category);
}
