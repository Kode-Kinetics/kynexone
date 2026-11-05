using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;
using V = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>What the wizard decides about a company's cutover, from the cutover as it is NOW and what the user asked for.</summary>
/// <param name="Action">create | update | unchanged (<see cref="V"/>).</param>
/// <param name="Promote">An existing Planned cutover becomes Active (the user chose "switching").</param>
/// <param name="SourceChange">The cutover's SourceSystem is written.</param>
/// <param name="RefusalCode">Null, or why nothing may be written.</param>
public sealed record CutoverEvaluation(string Action, bool Promote, bool SourceChange, string? RefusalCode);

/// <summary>
/// The importer's cutover rules (MigrationImportController.FindLockedPeriodRefusalsAsync), asked by the wizard.
/// A cutover that GOVERNS (Active/Closed) splits year-to-date at its month, so its boundary may not move while a payroll run
/// that is not Draft, Voided or Cancelled exists in or after the EARLIER of the old and new months. A Planned cutover governs
/// nothing and moves freely (the importer counts a change only when the old cutover governs); promoting it to Active is allowed
/// only while no such run exists from its month on. One function serves the planner AND the commit-time re-check.
/// </summary>
public static class CutoverLockRule
{
    private const string Why = "Create-company wizard: whether a payroll run pins a company's cutover boundary (company-scoped by id, read whatever company the caller's switcher selects).";

    /// <summary>Run statuses that do NOT pin a boundary: nothing was processed, or the run was thrown away.</summary>
    public static readonly string[] NonPinningStatuses = ["Draft", "Voided", "Cancelled"];

    public static async Task<bool> HasPinningRunFromAsync(ZayraDbContext db, Guid tenantId, Guid companyId, DateOnly fromMonth, CancellationToken ct)
    {
        var year = fromMonth.Year;
        var month = fromMonth.Month;
        var ignored = NonPinningStatuses;
        return await ScopedBypass.TenantWide(db.PayrollRuns, tenantId, Why).AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && !ignored.Contains(r.Status) && (r.Year > year || (r.Year == year && r.Month >= month)), ct);
    }

    /// <summary>Read side: the go-live month of this cutover can no longer change.</summary>
    public static async Task<bool> IsPinnedAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CompanyCutover? cutover, CancellationToken ct) =>
        cutover is not null && CutoverStatuses.Governs(cutover.Status)
        && await HasPinningRunFromAsync(db, tenantId, companyId, Month(cutover.CutoverDate), ct);

    /// <summary>The earliest month in which a Locked run exists in <paramref name="fromYear"/> or later, null when none (the importer's
    /// first-declaration rule: a first cutover may not be later than a run already locked).</summary>
    public static async Task<DateOnly?> EarliestLockedMonthFromYearAsync(ZayraDbContext db, Guid tenantId, Guid companyId, int fromYear, CancellationToken ct)
    {
        var run = await ScopedBypass.TenantWide(db.PayrollRuns, tenantId, Why).AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.Status == "Locked" && r.Year >= fromYear)
            .OrderBy(r => r.Year).ThenBy(r => r.Month).Select(r => new { r.Year, r.Month }).FirstOrDefaultAsync(ct);
        return run is null ? null : new DateOnly(run.Year, run.Month, 1);
    }

    private static DateOnly Month(DateOnly d) => new(d.Year, d.Month, 1);
    private static bool Differs(string? a, string? b) => !string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);

    /// <param name="existing">The cutover as it is now (null = none).</param>
    /// <param name="newMonth">First day of the requested go-live month.</param>
    /// <param name="effectiveSwitching">The user chose "switching", or the existing cutover already governs.</param>
    /// <param name="plannedPrevious">The previous system to write; null = not supplied (never clears).</param>
    public static async Task<CutoverEvaluation> EvaluateAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CompanyCutover? existing,
        DateOnly newMonth, bool effectiveSwitching, string? plannedPrevious, CancellationToken ct)
    {
        if (existing is null)
        {
            if (effectiveSwitching && await EarliestLockedMonthFromYearAsync(db, tenantId, companyId, newMonth.Year, ct) is { } earliest && newMonth > earliest)
                return new(V.ActionCreate, false, false, CutoverStatuses.FirstDeclarationTooLateCode);
            return new(V.ActionCreate, false, false, null);
        }
        var oldMonth = Month(existing.CutoverDate);
        var earlier = oldMonth < newMonth ? oldMonth : newMonth;
        var dateChanged = existing.CutoverDate != newMonth;
        var sourceWanted = plannedPrevious is not null && Differs(plannedPrevious, existing.SourceSystem);

        if (CutoverStatuses.Governs(existing.Status))
        {
            if (!dateChanged && !sourceWanted) return new(V.ActionUnchanged, false, false, null);
            var pinned = await HasPinningRunFromAsync(db, tenantId, companyId, earlier, ct);
            if (pinned && dateChanged) return new(V.ActionUpdate, false, false, V.FirstPayrollMonthLockedCode);
            if (pinned) return new(V.ActionUpdate, false, false, V.PreviousSystemLockedCode);
            return new(V.ActionUpdate, false, sourceWanted, null);
        }

        // Planned: governs nothing.
        if (effectiveSwitching)
        {
            if (await HasPinningRunFromAsync(db, tenantId, companyId, earlier, ct))
                return new(V.ActionUpdate, true, false, V.SwitchingBlockedByRunsCode);
            return new(V.ActionUpdate, true, sourceWanted, null);
        }
        return new(dateChanged ? V.ActionUpdate : V.ActionUnchanged, false, false, null);
    }

    /// <summary>Bilingual wording of a refusal, shared by the plan and the commit.</summary>
    public static (string En, string Ar) Words(string code, CompanyCutover? existing) => code switch
    {
        V.FirstPayrollMonthLockedCode => (
            $"Payroll for this company has already started (cutover {existing?.CutoverDate:yyyy-MM-dd}, {existing?.Status.ToLowerInvariant()}), so the first payroll month can no longer change.",
            "بدأت رواتب هذه الشركة، لذا لا يمكن تغيير أول شهر للرواتب."),
        V.PreviousSystemLockedCode => (
            "Payroll for this company has already started from the go-live month, so the previous system can no longer change.",
            "بدأت رواتب هذه الشركة منذ شهر بدء التشغيل، لذا لا يمكن تغيير النظام السابق."),
        V.SwitchingBlockedByRunsCode => (
            "This company already has payroll runs from its go-live month on, so it cannot be switched to a migrated company here. Use Setup → Opening balances (the migration importer) instead.",
            "لدى هذه الشركة مسيرات رواتب منذ شهر بدء التشغيل، لذا لا يمكن تحويلها إلى شركة منتقلة من هنا. استخدم الإعداد ← الأرصدة الافتتاحية (مستورد الانتقال)."),
        _ => ("The company's cutover cannot be changed.", "لا يمكن تغيير تاريخ انتقال الشركة."),
    };
}

/// <summary>Raised inside the commit transaction when the re-check under the company lock refuses (or finds the cutover changed). Rolls everything back.</summary>
public sealed class CutoverRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
