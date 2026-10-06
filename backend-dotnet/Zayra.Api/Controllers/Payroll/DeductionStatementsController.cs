using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Payroll;

/// <summary>
/// HR deductions statements (Release A slice R3): every deduction on a payslip with its category, legal basis, the
/// Art. 93 half-wage limit and, for loans and advances, the balance left. Read-only over persisted slip lines.
///
/// <para>Access: <c>payroll.read</c>; company scope comes from the tenant/company query filters on every payroll table,
/// and the caller's data scope (<see cref="IDataScopeService"/>) narrows it to the employees they may see. A slip or
/// employee outside either answers 404. The whole controller is closed unless the tenant has release_a on.</para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/payroll")]
[RequireOptInFeature(FeatureKeys.ReleaseA)]
public sealed class DeductionStatementsController(ZayraDbContext db, IDataScopeService scopeService) : ControllerBase
{
    private DeductionStatementService Statements => new(db);

    /// <summary>One payslip's deductions statement (the "Deductions" drawer on a payroll run row).</summary>
    [HttpGet("slips/{slipId:guid}/deduction-statement")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> ForSlip(Guid slipId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tenantId) return Unauthorized();
        var employeeId = await db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Id == slipId).Select(s => (int?)s.EmployeeId).FirstOrDefaultAsync(ct);
        if (employeeId is null) return NotFound();
        var scope = await scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(employeeId.Value)) return NotFound();
        var detail = await Statements.DetailForSlipAsync(tenantId, slipId, DeductionAudience.Hr, ct);
        return detail is null ? NotFound() : Ok(DeductionStatementDto.From(detail));
    }

    /// <summary>
    /// The run's employees against the Art. 93 limit. <paramref name="nearCap"/> = true returns only those near or over
    /// it (more than 80% of the limit used), the exception-first view; false or absent returns everyone.
    /// </summary>
    [HttpGet("runs/{runId:guid}/deduction-statements")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> ForRun(Guid runId, [FromQuery] bool? nearCap, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tenantId) return Unauthorized();
        var scope = await scopeService.ResolveAsync(User, tenantId, ct);
        var details = await Statements.ForRunAsync(tenantId, runId, scope.AllowedEmployeeIds, ct);
        if (details is null) return NotFound();
        var rows = details.Select(RunDeductionRowDto.From);
        // The same exception rule as the screen: anything not plainly within the limit, or carrying a flag.
        if (nearCap == true) rows = rows.Where(r => DeductionStatementBuilder.NeedsAttention(r.CapStatus, r.Flags));
        return Ok(rows.ToList());
    }

    /// <summary>One employee's statements for the last <paramref name="months"/> periods and their open loans and advances
    /// (the Deductions panel on the employee profile). Voided runs are left out; in-progress runs are shown with their status.</summary>
    [HttpGet("employees/{employeeId:int}/deduction-statements")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> ForEmployee(int employeeId, [FromServices] ITenantClock clock, [FromQuery] int months = 6,
        CancellationToken ct = default)
    {
        if (this.GetTenantId() is not Guid tenantId) return Unauthorized();
        var scope = await scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(employeeId)) return NotFound();
        if (!await db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct))
            return NotFound();
        var today = await clock.TodayAsync(tenantId, ct);
        var details = await Statements.ForEmployeeAsync(tenantId, employeeId, months, DeductionAudience.Hr, today, ct);
        var balances = await Statements.BalancesAsync(tenantId, employeeId, ct);
        return Ok(EmployeeDeductionsDto.From(details, balances));
    }
}

// ── Response shapes (shared with EssDeductionsController) ──────────────────────────────────────────────

/// <param name="WageDue">Art. 93 wage due: gross minus pay not earned (absence, unpaid leave), over the month's non-voided runs.
/// The worker-protective reading, pending legal confirmation (owner's counsel list).</param>
/// <param name="DebtPercentOfWage">Debt-type total ÷ wage due, 0–100, floored to two decimals; null when there is no wage.</param>
/// <param name="CapStatus">Within / Near / Over / NeedsReview / Voided (<see cref="CapStatuses"/>); Near = more than 80% of the
/// limit used; NeedsReview = the lines do not add up to the slip; Voided = the run no longer applies (no flags, no advice).</param>
/// <param name="NotCountedTotal">This slip's deductions outside the limit (GOSI, absence, corrections, …).</param>
/// <param name="SlipDeductionTotal">The payslip's own deductions total; <paramref name="Reconciles"/> says its lines add up to it.</param>
/// <param name="OtherRuns">Other non-voided payroll runs this month; their wage due and debt are inside the figures.</param>
/// <param name="Reasons">Each flag as a sentence: title, why, fix — EN and AR. The UI never shows a raw code.</param>
public sealed record DeductionStatementDto(
    Guid SlipId, Guid RunId, int EmployeeId, string EmployeeCode, string EmployeeName, int Year, int Month,
    string? RunStatus, string? RunType, string SlipStatus, string Currency,
    decimal GrossPay, decimal PayNotEarned, decimal OtherRunsWageDue, decimal OtherRunsDebt, int OtherRuns,
    decimal WageDue, decimal DebtTotal, decimal CapLimit, decimal Headroom, decimal? DebtPercentOfWage, string CapStatus,
    decimal NotCountedTotal, decimal SlipDeductionTotal, bool Reconciles,
    IReadOnlyList<DeductionLineDto> Lines, IReadOnlyList<string> Flags, IReadOnlyList<BlockReason> Reasons)
{
    public static DeductionStatementDto From(DeductionStatementDetail d)
    {
        var s = d.Statement;
        var own = s.Lines.Where(l => l.ComponentCode != DeductionStatementBuilder.OtherRunsCode).ToList();
        var ownTotal = own.Sum(l => l.Amount);
        return new DeductionStatementDto(s.SlipId, d.Slip.RunId, s.EmployeeId, d.Slip.EmployeeCode, d.Slip.EmployeeName, s.Year, s.Month,
            d.Run?.Status, d.Run?.RunType, d.Slip.Status, d.Currency,
            Money(d.Slip.GrossSalary), Money(d.PayNotEarned), Money(d.Period.OtherRunsWageDue), Money(d.Period.OtherRunsDebt), d.Period.OtherRuns,
            Money(s.WageDue), Money(s.DebtTotal), Money(s.CapLimit), Money(s.Headroom),
            DeductionStatementBuilder.Percent(s.DebtTotal, s.WageDue), d.CapStatus,
            Money(own.Where(l => !l.CountsTowardCap).Sum(l => l.Amount)), Money(d.Slip.Deductions), Math.Abs(ownTotal - d.Slip.Deductions) < 0.01m,
            s.Lines.Select(l => DeductionLineDto.From(l, d.Debts)).ToList(),
            s.Flags, s.Flags.Where(ReleaseABlockReasons.All.ContainsKey).Select(ReleaseABlockReasons.Get).ToList());
    }

    internal static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <param name="LegalBasisKey">An i18n key (src/i18n/releaseA/deductions.ts), never free text.</param>
/// <param name="InstalmentsRemaining">At the loan's instalment, after this payslip; null when the balance is unknown.</param>
/// <param name="PercentOfWage">Loan/advance lines: this instalment ÷ the Art. 92 basis (the loan's cap_base_wage witness, else
/// the salary structure), 0–100, floored to two decimals for display.</param>
/// <param name="AboveConsentThreshold">Employer loans: the UNROUNDED instalment is above 10% of that basis (or the basis is unknown).</param>
/// <param name="ConsentOnFile">Employer loans: the employee's written consent to an instalment above 10% is on file.</param>
public sealed record DeductionLineDto(
    string ComponentCode, string Label, string Category, decimal Amount, bool CountsTowardCap, string LegalBasisKey,
    Guid? LoanId, string? LoanNumber, string? LoanType, string? LoanTypeAr, decimal? BalanceAfter, int? InstalmentsRemaining,
    int? InstalmentsTotal, decimal? PercentOfWage, bool? AboveConsentThreshold, bool? ConsentOnFile)
{
    public static DeductionLineDto From(DeductionStatementLine l, IReadOnlyDictionary<Guid, DebtFacts> debts)
    {
        DebtFacts? debt = l.LoanId is Guid id && debts.TryGetValue(id, out var f) ? f : null;
        bool? above = l.Category == DeductionCategories.EmployerLoan && l.LoanId is not null
            ? l.PercentOfWage is not decimal pct || pct > DeductionStatementService.LoanConsentThresholdPercent
            : null;
        return new DeductionLineDto(l.ComponentCode, l.Label, l.Category, DeductionStatementDto.Money(l.Amount), l.CountsTowardCap,
            l.LegalBasisKey, l.LoanId, debt?.Number, debt?.TypeName, debt?.TypeNameAr,
            l.BalanceAfter is decimal b ? DeductionStatementDto.Money(b) : null, l.InstalmentsRemaining,
            debt is { InstalmentsTotal: > 0 } ? debt.InstalmentsTotal : null, DeductionStatementBuilder.FloorPercent(l.PercentOfWage),
            above, l.ConsentOnFile);
    }
}

/// <summary>One row of the run view: an employee against the Art. 93 limit.</summary>
/// <param name="Reconciles">The slip's lines add up to its deductions total; false shows as NeedsReview, never Within.</param>
public sealed record RunDeductionRowDto(
    Guid SlipId, int EmployeeId, string EmployeeCode, string EmployeeName, string Currency, string? RunStatus,
    decimal WageDue, decimal DebtTotal, decimal CapLimit, decimal Headroom, decimal? DebtPercentOfWage, string CapStatus,
    bool Reconciles, IReadOnlyList<string> Flags)
{
    public static RunDeductionRowDto From(DeductionStatementDetail d)
    {
        var s = d.Statement;
        return new RunDeductionRowDto(s.SlipId, s.EmployeeId, d.Slip.EmployeeCode, d.Slip.EmployeeName, d.Currency, d.Run?.Status,
            DeductionStatementDto.Money(s.WageDue), DeductionStatementDto.Money(s.DebtTotal), DeductionStatementDto.Money(s.CapLimit),
            DeductionStatementDto.Money(s.Headroom), DeductionStatementBuilder.Percent(s.DebtTotal, s.WageDue), d.CapStatus,
            !s.Flags.Contains(ReleaseABlockReasons.DeductionLinesMissing), s.Flags);
    }
}

/// <param name="LoanNo">The loan (or advance) number shown to people.</param>
/// <param name="Remaining">Instalments left at the current instalment.</param>
public sealed record DebtBalanceDto(Guid Id, string LoanNo, string Type, string? TypeAr, string? Currency, decimal Outstanding, decimal Instalment,
    int? Remaining, int? InstalmentsTotal, DateOnly? NextDueOn, bool? ConsentOnFile, bool DeductedFromPay)
{
    public static DebtBalanceDto From(DebtBalance b) => new(b.Id, b.Number, b.TypeName, b.TypeNameAr, b.Currency,
        DeductionStatementDto.Money(b.Outstanding), DeductionStatementDto.Money(b.Instalment), b.InstalmentsRemaining,
        b.InstalmentsTotal > 0 ? b.InstalmentsTotal : null, b.NextDueOn, b.ConsentOnFile, b.DeductedFromPay);
}

public sealed record DeductionBalancesDto(IReadOnlyList<DebtBalanceDto> Loans, IReadOnlyList<DebtBalanceDto> Advances);

/// <summary>Statements newest first, and the open loans and advances behind them.</summary>
public sealed record EmployeeDeductionsDto(IReadOnlyList<DeductionStatementDto> Statements, DeductionBalancesDto Balances)
{
    public static EmployeeDeductionsDto From(IReadOnlyList<DeductionStatementDetail> details, DeductionBalances balances) =>
        new(details.Select(DeductionStatementDto.From).ToList(),
            new DeductionBalancesDto(balances.Loans.Select(DebtBalanceDto.From).ToList(), balances.Advances.Select(DebtBalanceDto.From).ToList()));
}
