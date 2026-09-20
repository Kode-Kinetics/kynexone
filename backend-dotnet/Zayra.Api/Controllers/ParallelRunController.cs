using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Parallel run — comparing a KynexOne run against the register the customer's OUTGOING system produced
/// for the same month.
///
/// <para>Every serious payroll go-live runs both systems side by side for one to three months and
/// reconciles to the fils. Before this existed the only way to do that was to export both registers to
/// CSV and reconcile in Excel with VLOOKUP: three cycles at a thousand employees is three to five
/// consultant-days per parallel month, and the artefact the customer signs off is a spreadsheet nobody
/// can reproduce.</para>
///
/// <para>This deliberately lives OUTSIDE <c>PayrollController</c>. It reads payroll output and writes
/// nothing, so it has no business inside a 9,000-line controller that a statutory stream is actively
/// rewriting.</para>
///
/// <para>ON TRIAL RUNS — the question "can a run be executed without posting?" has a better answer than
/// expected, and it needed no new code. <c>POST /api/payroll/runs/{id}/process</c> computes everything
/// and writes slips, earnings, deductions and validation results, but NO GL is written until
/// <c>Lock</c>, payslips are not published to ESS until <c>Lock</c>, and the payslip YTD figures sum
/// only runs whose Status is <c>Locked</c>. So a processed-but-unlocked run is already a true
/// simulation: nothing reaches the ledger, nothing reaches the employee, and it does not pollute
/// anybody's year to date. <c>POST runs/{id}/reopen</c> refuses outright once a GL row exists, which is
/// exactly the parallel-run case, and restores loan/advance/attendance/leave state on the way back to
/// Draft — so process → compare → reopen → correct → re-process iterates without limit. What does NOT
/// exist is a second concurrent <c>Regular</c> run for one period (two partial unique indexes forbid
/// it), so "run it two ways at once and diff" is unavailable; you iterate one run instead. The one real
/// cost is that <c>reopen</c> discards the validation overrides, so a judgement consciously made in
/// iteration 3 must be re-entered in iteration 4.</para>
/// </summary>
[ApiController]
[Route("api/payroll/parallel-run")]
[Authorize]
public sealed class ParallelRunController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public ParallelRunController(ZayraDbContext db) => _db = db;

    /// <summary>
    /// The register the legacy system produced, in long form. Long rather than wide because "per
    /// component" is the requirement and a wide sheet cannot carry an arbitrary component catalogue;
    /// the three reserved codes GROSS, DEDUCTIONS and NET carry the totals every source system has.
    /// </summary>
    public sealed record VarianceRequest(
        string RegisterCsv,
        decimal Tolerance = 0.01m,
        string ToleranceType = VarianceToleranceTypes.Absolute);

    public static class VarianceToleranceTypes
    {
        public const string Absolute = "Absolute";
        public const string Percentage = "Percentage";
    }

    /// <summary>Reserved component codes that map onto the payslip's own totals.</summary>
    private const string TotalGross = "GROSS";
    private const string TotalDeductions = "DEDUCTIONS";
    private const string TotalNet = "NET";

    public sealed record VarianceLine(
        string EmployeeCode,
        string EmployeeName,
        string ComponentCode,
        decimal KynexOneAmount,
        decimal ExternalAmount,
        decimal Variance,
        decimal VariancePct,
        bool IsOutsideTolerance,
        string Presence);

    public sealed record VarianceReport(
        Guid RunId,
        string Period,
        decimal Tolerance,
        string ToleranceType,
        int EmployeesInRun,
        int EmployeesInRegister,
        int MatchedEmployees,
        int EmployeesOnlyInRun,
        int EmployeesOnlyInRegister,
        int LinesCompared,
        int LinesOutsideTolerance,
        decimal TotalAbsoluteVariance,
        decimal KynexOneNetTotal,
        decimal ExternalNetTotal,
        IReadOnlyList<VarianceLine> Lines,
        IReadOnlyList<string> RegisterErrors);

    /// <summary>
    /// Compare a processed run against an uploaded external register, per employee and per component.
    ///
    /// <para>Read-only. It does not require the run to be Locked — the whole point is to look at a
    /// trial run before anybody commits to it.</para>
    /// </summary>
    [HttpPost("{runId:guid}/variance")]
    [HasPermission("payroll.read")]
    public async Task<ActionResult<VarianceReport>> Variance(Guid runId, VarianceRequest request, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null) return NotFound(new { message = "Payroll run not found." });

        if (!VarianceToleranceTypes.Absolute.Equals(request.ToleranceType, StringComparison.OrdinalIgnoreCase)
         && !VarianceToleranceTypes.Percentage.Equals(request.ToleranceType, StringComparison.OrdinalIgnoreCase))
            return UnprocessableEntity(new { message = $"ToleranceType must be {VarianceToleranceTypes.Absolute} or {VarianceToleranceTypes.Percentage}." });
        if (request.Tolerance < 0)
            return UnprocessableEntity(new { message = "Tolerance cannot be negative." });

        var (external, registerErrors) = ParseRegister(request.RegisterCsv);
        if (external.Count == 0 && registerErrors.Count > 0)
            return UnprocessableEntity(new { message = "The external register could not be read.", errors = registerErrors });

        var slips = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RunId == runId)
            .Select(s => new { s.EmployeeId, s.EmployeeCode, s.EmployeeName, s.GrossSalary, s.Deductions, s.NetSalary })
            .ToListAsync(ct);
        var earnings = await _db.PayrollEarnings.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PayrollRunId == runId)
            .Select(e => new { e.EmployeeId, e.ComponentCode, e.Amount })
            .ToListAsync(ct);
        var deductions = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == runId)
            .Select(d => new { d.EmployeeId, d.ComponentCode, d.Amount })
            .ToListAsync(ct);

        // Build the run side as (EmployeeCode, ComponentCode) -> amount, in the same shape as the
        // register, so the comparison below is a plain outer join and not a special case per column.
        var mine = new Dictionary<(string Code, string Component), decimal>();
        var nameByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var codeByEmployeeId = new Dictionary<int, string>();
        foreach (var s in slips)
        {
            var code = s.EmployeeCode ?? string.Empty;
            if (code.Length == 0) continue;
            codeByEmployeeId[s.EmployeeId] = code;
            nameByCode[code] = s.EmployeeName ?? string.Empty;
            Accumulate(mine, code, TotalGross, s.GrossSalary);
            Accumulate(mine, code, TotalDeductions, s.Deductions);
            Accumulate(mine, code, TotalNet, s.NetSalary);
        }
        foreach (var e in earnings)
            if (codeByEmployeeId.TryGetValue(e.EmployeeId, out var code))
                Accumulate(mine, code, Normalise(e.ComponentCode), e.Amount);
        foreach (var d in deductions)
            if (codeByEmployeeId.TryGetValue(d.EmployeeId, out var code))
                Accumulate(mine, code, Normalise(d.ComponentCode), d.Amount);

        var runEmployees = codeByEmployeeId.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registerEmployees = external.Keys.Select(k => k.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isPercentage = VarianceToleranceTypes.Percentage.Equals(request.ToleranceType, StringComparison.OrdinalIgnoreCase);
        var lines = new List<VarianceLine>();
        foreach (var key in mine.Keys.Union(external.Keys).OrderBy(k => k.Code, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Component, StringComparer.Ordinal))
        {
            var hasMine = mine.TryGetValue(key, out var mineAmount);
            var hasTheirs = external.TryGetValue(key, out var theirAmount);
            var variance = Math.Round(mineAmount - theirAmount, 2);
            // Percentage variance is expressed against THEIR figure, because the external register is
            // the thing being reconciled TO. A component present here and absent there is 100% by
            // definition, which is the right answer and what a reviewer expects to see flagged.
            var variancePct = theirAmount == 0m
                ? (variance == 0m ? 0m : 100m)
                : Math.Round(variance / Math.Abs(theirAmount) * 100m, 4);

            var outside = isPercentage
                ? Math.Abs(variancePct) > request.Tolerance
                : Math.Abs(variance) > request.Tolerance;

            var presence = (hasMine, hasTheirs) switch
            {
                (true, true) => "Both",
                (true, false) => "OnlyInKynexOne",
                (false, true) => "OnlyInRegister",
                _ => "Neither"
            };
            // A line absent from BOTH sides cannot occur (the key came from one of them), but a line
            // that is zero on both is noise on a 25,000-row report and is dropped rather than shown.
            if (mineAmount == 0m && theirAmount == 0m) continue;

            lines.Add(new VarianceLine(
                key.Code,
                nameByCode.GetValueOrDefault(key.Code, string.Empty),
                key.Component,
                mineAmount, theirAmount, variance, variancePct, outside, presence));
        }

        var report = new VarianceReport(
            RunId: runId,
            Period: $"{run.Year}-{run.Month:D2}",
            Tolerance: request.Tolerance,
            ToleranceType: isPercentage ? VarianceToleranceTypes.Percentage : VarianceToleranceTypes.Absolute,
            EmployeesInRun: runEmployees.Count,
            EmployeesInRegister: registerEmployees.Count,
            MatchedEmployees: runEmployees.Intersect(registerEmployees, StringComparer.OrdinalIgnoreCase).Count(),
            EmployeesOnlyInRun: runEmployees.Except(registerEmployees, StringComparer.OrdinalIgnoreCase).Count(),
            EmployeesOnlyInRegister: registerEmployees.Except(runEmployees, StringComparer.OrdinalIgnoreCase).Count(),
            LinesCompared: lines.Count,
            LinesOutsideTolerance: lines.Count(l => l.IsOutsideTolerance),
            TotalAbsoluteVariance: Math.Round(lines.Sum(l => Math.Abs(l.Variance)), 2),
            KynexOneNetTotal: Math.Round(lines.Where(l => l.ComponentCode == TotalNet).Sum(l => l.KynexOneAmount), 2),
            ExternalNetTotal: Math.Round(lines.Where(l => l.ComponentCode == TotalNet).Sum(l => l.ExternalAmount), 2),
            Lines: lines,
            RegisterErrors: registerErrors);

        return Ok(report);
    }

    /// <summary>
    /// Cutover readiness and the carried-in position for one legal entity — the screen a consultant
    /// shows the customer's payroll lead to prove what landed.
    /// </summary>
    [HttpGet("cutover")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> CutoverStatus([FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var cutovers = await _db.CompanyCutovers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (companyId == null || x.CompanyId == companyId))
            .ToListAsync(ct);

        var companyIds = cutovers.Where(c => c.CompanyId != null).Select(c => c.CompanyId!.Value).ToList();
        var companyNames = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);

        // Carried-in position, straight off the provenance ledger. This is the answer to "how much of
        // what is in this system came from somewhere else" and it is a single grouped read because the
        // provenance is a first-class table rather than a flag scattered over five others.
        var origins = await _db.OpeningBalanceOrigins.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (companyId == null || x.CompanyId == companyId))
            .GroupBy(x => new { x.CompanyId, x.EntityType })
            .Select(g => new
            {
                g.Key.CompanyId,
                g.Key.EntityType,
                Rows = g.Count(),
                Employees = g.Select(x => x.EmployeeId).Distinct().Count(),
                CarriedTotal = g.Sum(x => x.CarriedAmount)
            })
            .ToListAsync(ct);

        return Ok(new
        {
            entities = cutovers.Select(c => new
            {
                companyId = c.CompanyId,
                companyName = c.CompanyId is null ? "(unassigned)" : companyNames.GetValueOrDefault(c.CompanyId.Value, "(unnamed entity)"),
                cutoverDate = c.CutoverDate,
                status = c.Status,
                sourceSystem = c.SourceSystem,
                notes = c.Notes,
                carriedIn = origins
                    .Where(o => o.CompanyId == c.CompanyId)
                    .Select(o => new { entityType = o.EntityType, rows = o.Rows, employees = o.Employees, carriedTotal = o.CarriedTotal })
            })
        });
    }

    /// <summary>
    /// Everything carried in for one employee, with the live value beside the value as at cutover.
    /// This is the report that makes opening balances distinguishable in practice and not just in the
    /// schema — an auditor reads it rather than the tables.
    /// </summary>
    [HttpGet("employees/{employeeCode}/carried-in")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> CarriedIn(string employeeCode, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var origins = await _db.OpeningBalanceOrigins.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeCode == employeeCode)
            .OrderBy(x => x.EntityType)
            .ToListAsync(ct);
        if (origins.Count == 0)
            return Ok(new { employeeCode, carriedIn = Array.Empty<object>(), message = "Nothing was carried in for this employee — every figure was earned in KynexOne." });

        var loanIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Loan).Select(o => o.EntityId).ToList();
        var advanceIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Advance).Select(o => o.EntityId).ToList();
        var loans = await _db.EmployeeLoans.AsNoTracking()
            .Where(l => l.TenantId == tenantId && loanIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => new { l.LoanNumber, l.OutstandingBalance, l.Status }, ct);
        var advances = await _db.SalaryAdvances.AsNoTracking()
            .Where(a => a.TenantId == tenantId && advanceIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => new { a.AdvanceNumber, a.OutstandingBalance, a.Status }, ct);

        return Ok(new
        {
            employeeCode,
            carriedIn = origins.Select(o => new
            {
                entityType = o.EntityType,
                entityId = o.EntityId,
                cutoverDate = o.CutoverDate,
                // Frozen at import. This is the number that proves the figure was NOT earned here.
                carriedAmount = o.CarriedAmount,
                currency = o.Currency,
                sourceSystem = o.SourceSystem,
                sourceRecordId = o.SourceRecordId,
                migrationBatchId = o.MigrationBatchId,
                reference = o.EntityType == OpeningBalanceEntityTypes.Loan
                        ? loans.GetValueOrDefault(o.EntityId)?.LoanNumber
                    : o.EntityType == OpeningBalanceEntityTypes.Advance
                        ? advances.GetValueOrDefault(o.EntityId)?.AdvanceNumber
                    : null,
                currentBalance = o.EntityType == OpeningBalanceEntityTypes.Loan
                        ? loans.GetValueOrDefault(o.EntityId)?.OutstandingBalance
                    : o.EntityType == OpeningBalanceEntityTypes.Advance
                        ? advances.GetValueOrDefault(o.EntityId)?.OutstandingBalance
                    : (decimal?)null,
                // How much of the carried amount this product has since worked off. An audit asks for
                // exactly this: what came in, what has moved, and therefore what we are responsible for.
                movementSinceCutover = o.EntityType == OpeningBalanceEntityTypes.Loan && loans.ContainsKey(o.EntityId)
                        ? o.CarriedAmount - loans[o.EntityId].OutstandingBalance
                    : o.EntityType == OpeningBalanceEntityTypes.Advance && advances.ContainsKey(o.EntityId)
                        ? o.CarriedAmount - advances[o.EntityId].OutstandingBalance
                    : (decimal?)null
            })
        });
    }

    // ── Register parsing ────────────────────────────────────────────────────────────────────────────

    private static (Dictionary<(string Code, string Component), decimal> Rows, List<string> Errors) ParseRegister(string csv)
    {
        var rows = new Dictionary<(string, string), decimal>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            errors.Add("The external register was empty.");
            return (rows, errors);
        }

        List<Dictionary<string, string>> parsed;
        try { parsed = Csv.Parse(csv); }
        catch (Exception ex) { errors.Add($"The external register is not readable as CSV: {ex.Message}"); return (rows, errors); }

        foreach (var (row, index) in parsed.Select((r, i) => (r, i + 2)))
        {
            var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            var component = Normalise(row.GetValueOrDefault("ComponentCode", string.Empty));
            var amountRaw = row.GetValueOrDefault("Amount", string.Empty).Trim();
            if (code.Length == 0) { errors.Add($"register row {index}: EmployeeCode is required."); continue; }
            if (component.Length == 0) { errors.Add($"register row {index}: ComponentCode is required (use GROSS, DEDUCTIONS or NET for the totals)."); continue; }
            if (amountRaw.Length == 0) { errors.Add($"register row {index}: Amount is required and was blank — a blank is not read as zero."); continue; }
            if (!decimal.TryParse(amountRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            { errors.Add($"register row {index}: Amount is not a number (found '{amountRaw}')."); continue; }

            var key = (code, component);
            rows[key] = rows.GetValueOrDefault(key) + amount;
        }
        return (rows, errors);
    }

    private static void Accumulate(Dictionary<(string, string), decimal> map, string code, string component, decimal amount)
        => map[(code, component)] = map.GetValueOrDefault((code, component)) + amount;

    /// <summary>Component codes are compared case- and separator-insensitively: a legacy register
    /// writes "Basic Salary" where the catalogue says "BASIC_SALARY", and refusing to match those
    /// would make the report unusable on the first real file.</summary>
    private static string Normalise(string value)
    {
        var chars = (value ?? string.Empty).Trim().ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
