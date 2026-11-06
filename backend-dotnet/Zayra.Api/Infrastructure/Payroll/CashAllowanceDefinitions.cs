using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>Why a customer-defined cash allowance's definition cannot be used for a date.</summary>
public enum AllowanceDefinitionFault
{
    /// <summary>No definition row for the code, or none that is switched on, not removed and in effect for the date.</summary>
    NoDefinition,
    /// <summary>A definition in effect, but Finance has not confirmed it.</summary>
    NotConfirmed,
    /// <summary>A definition in effect with no GL driver, so its expense has nowhere to post.</summary>
    NoGlDriver,
}

/// <summary>
/// The ONE place the effective-dated versions of a customer-defined cash allowance's definition (<c>pay_components</c>, tenant-wide
/// <c>ALW_*</c>, Earning, GradePackage) are read. A definition is a sequence of versions: a Finance correction closes the old row at the day
/// before and inserts a new one, so every reader names the date it asks about and gets the ONE version in effect then.
/// Versions are small in number, so they are read whole and picked in memory (no provider-specific date translation).
/// </summary>
public static class CashAllowanceDefinitions
{
    /// <summary>All non-removed versions of the given codes (tenant-wide rows only), oldest first.</summary>
    public static async Task<ILookup<string, PayComponent>> VersionsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct, bool includeRemoved = false, bool tracked = false)
    {
        if (codes.Count == 0) return Enumerable.Empty<PayComponent>().ToLookup(x => x.Code);
        var query = ScopedBypass.TenantWide(db.PayComponents, tenantId,
                "A cash allowance definition is a tenant-wide row; its effective-dated versions are read together to pick the one in effect.");
        if (!tracked) query = query.AsNoTracking();
        query = query.Where(c => c.CompanyId == null && c.ComponentType == PayComponentTypes.Earning
                              && c.CalcMethod == PayComponentCalcMethods.GradePackage && codes.Contains(c.Code));
        if (!includeRemoved) query = query.Where(c => !c.IsDeleted);
        return (await query.ToListAsync(ct)).OrderBy(c => c.EffectiveFrom ?? DateOnly.MinValue).ToLookup(c => c.Code, StringComparer.Ordinal);
    }

    /// <summary>The version of a definition in effect on <paramref name="date"/>: switched on, not removed. Null when there is none.</summary>
    public static PayComponent? InEffect(IEnumerable<PayComponent> versions, DateOnly date) =>
        versions.Where(v => v.IsActive && !v.IsDeleted && v.IsInEffect(date))
                .OrderByDescending(v => v.EffectiveFrom ?? DateOnly.MinValue).FirstOrDefault();

    /// <summary>The version a screen shows as "the definition": the one in effect on <paramref name="today"/>, else the latest not removed (a definition
    /// whose only version is scheduled for later, or was closed), else the latest of all.</summary>
    public static PayComponent? Current(IEnumerable<PayComponent> versions, DateOnly today)
    {
        var list = versions.ToList();
        return list.Where(v => !v.IsDeleted && v.IsInEffect(today)).OrderByDescending(v => v.EffectiveFrom ?? DateOnly.MinValue).FirstOrDefault()
            ?? list.Where(v => !v.IsDeleted).OrderByDescending(v => v.EffectiveFrom ?? DateOnly.MinValue).FirstOrDefault()
            ?? list.OrderByDescending(v => v.EffectiveFrom ?? DateOnly.MinValue).FirstOrDefault();
    }

    /// <summary>Whether a definition can be put on a salary line today: switched on and not removed, in effect today or later (a version that was
    /// wholly in the past does not count), and confirmed by Finance.</summary>
    public static AllowanceDefinitionFault? SalaryLineFault(IEnumerable<PayComponent> versions, DateOnly today)
    {
        var live = versions.Where(v => v.IsActive && !v.IsDeleted && (v.EffectiveTo is null || v.EffectiveTo >= today))
            .OrderByDescending(v => v.EffectiveFrom ?? DateOnly.MinValue).FirstOrDefault();
        if (live is null) return AllowanceDefinitionFault.NoDefinition;
        if (live.FinanceConfirmedAtUtc is null) return AllowanceDefinitionFault.NotConfirmed;
        return string.IsNullOrWhiteSpace(live.GlDriverKey) ? AllowanceDefinitionFault.NoGlDriver : null;
    }

    /// <summary>The fault (if any) that stops a definition being used for the pay period starting <paramref name="periodStart"/>: the version in effect then.</summary>
    public static AllowanceDefinitionFault? PeriodFault(IEnumerable<PayComponent> versions, DateOnly periodStart)
    {
        var version = InEffect(versions, periodStart);
        if (version is null) return AllowanceDefinitionFault.NoDefinition;
        if (version.FinanceConfirmedAtUtc is null) return AllowanceDefinitionFault.NotConfirmed;
        return string.IsNullOrWhiteSpace(version.GlDriverKey) ? AllowanceDefinitionFault.NoGlDriver : null;
    }

    // ── Pre-run readiness ───────────────────────────────────────────────────────────────────────

    /// <summary>One employee whose salary carries a cash allowance line the payroll run could not pay: a stable <c>Problem</c> code
    /// (<c>no_definition</c>, <c>not_confirmed</c>, <c>no_gl_driver</c>) and the words for it. No amounts.</summary>
    public sealed record ReadinessIssue(int EmployeeId, string EmployeeCode, string EmployeeName, Guid? CompanyId, string Code, string Problem, string Message);

    /// <summary>
    /// Employees (of <paramref name="employees"/>) whose salary record in force for the period carries an allowance line whose definition, for the period
    /// starting <paramref name="periodStart"/>, is missing or not in effect, is not confirmed by Finance, or has no GL driver. It lists them BEFORE payroll day,
    /// where the run would refuse (422) instead. An employee without allowance lines never appears, and the check reads no amounts.
    /// </summary>
    public static async Task<List<ReadinessIssue>> ReadinessIssuesAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<(int Id, string Code, string Name, Guid? CompanyId)> employees,
        DateOnly periodStart, DateOnly periodEnd, CancellationToken ct)
    {
        var issues = new List<ReadinessIssue>();
        if (employees.Count == 0) return issues;
        var employeeIds = employees.Select(e => e.Id).ToList();
        var records = (await db.EmployeeSalaryStructures.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.IsActive && x.EffectiveDate <= periodEnd && employeeIds.Contains(x.EmployeeId))
                .Select(x => new { x.Id, x.EmployeeId, x.EffectiveDate, x.CreatedAtUtc }).ToListAsync(ct))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).First().Id);
        if (records.Count == 0) return issues;
        var recordIds = records.Values.ToList();
        var lines = (await db.EmployeeSalaryAllowances.AsNoTracking()
                .Where(x => x.TenantId == tenantId && recordIds.Contains(x.SalaryStructureId)).Select(x => new { x.SalaryStructureId, x.Code }).ToListAsync(ct))
            .ToLookup(x => x.SalaryStructureId);
        var codes = lines.SelectMany(g => g).Select(x => x.Code).Distinct().ToList();
        if (codes.Count == 0) return issues;
        var versions = await VersionsAsync(db, tenantId, codes, ct);
        foreach (var employee in employees.OrderBy(e => e.Code, StringComparer.Ordinal))
        {
            if (!records.TryGetValue(employee.Id, out var recordId)) continue;
            foreach (var line in lines[recordId].OrderBy(l => l.Code, StringComparer.Ordinal))
            {
                if (PeriodFault(versions[line.Code], periodStart) is not { } fault) continue;
                var (problem, message) = fault switch
                {
                    AllowanceDefinitionFault.NotConfirmed => ("not_confirmed", $"{line.Code} is waiting for Finance to confirm it."),
                    AllowanceDefinitionFault.NoGlDriver => ("no_gl_driver", $"{line.Code} has no GL driver, so its expense has nowhere to post."),
                    _ => ("no_definition", $"{line.Code} was removed, switched off or is not in effect for this month."),
                };
                issues.Add(new ReadinessIssue(employee.Id, employee.Code, employee.Name, employee.CompanyId, line.Code, problem, message));
            }
        }
        return issues;
    }

    // ── The end-of-service answer: a category and a note ────────────────────────────────────────

    public const int NoteMaxLength = 500;

    /// <summary>A refused end-of-service answer: the stable code and the words.</summary>
    public sealed record EosbProblem(string Code, string Message);

    /// <summary>
    /// The end-of-service answer of a cash allowance, as the definition stores it. Included: no category and no note (anything sent is dropped).
    /// Excluded: a category in <see cref="EosbExclusionCategories"/>, and a note — required for "Other". An older caller that sends only a note is read as
    /// the category "Other" with that note; one that sends neither is told a reason is needed.
    /// </summary>
    public static (EosbProblem? Problem, string? Category, string? Note) NormalizeEosb(bool included, string? category, string? note)
    {
        if (included) return (null, null, null);
        var text = (note ?? string.Empty).Trim();
        var named = (category ?? string.Empty).Trim();
        string? normalized;
        if (named.Length == 0)
        {
            if (text.Length == 0) return (new("eosb_reason_required", "Say why this allowance is left out of the end-of-service wage."), null, null);
            normalized = EosbExclusionCategories.Other;
        }
        else
        {
            normalized = EosbExclusionCategories.Normalize(named);
            if (normalized is null)
                return (new("eosb_category_invalid", "Choose why it is left out: a reimbursement, discretionary, non-recurring, or other (with a note)."), null, null);
        }
        if (normalized == EosbExclusionCategories.Other && text.Length == 0)
            return (new("eosb_reason_required", "Say why this allowance is left out of the end-of-service wage."), null, null);
        if (text.Length > NoteMaxLength)
            return (new("eosb_reason_too_long", $"Keep the reason under {NoteMaxLength} characters."), null, null);
        return (null, normalized, text.Length == 0 ? null : text);
    }

    // ── Warnings (never block) ──────────────────────────────────────────────────────────────────

    public const string WarningReimbursive = "eosb_included_reimbursive";
    public const string WarningFixedExcluded = "eosb_excluded_fixed_monthly";
    public const string WarningCommission = "commission_name";

    private static readonly string[] ReimbursiveWords =
        ["travel", "phone", "mobile", "fuel", "per diem", "perdiem", "expense", "mileage", "reimburs", "سفر", "هاتف", "جوال", "وقود", "بدل انتداب", "مصروف", "مصاريف", "تنقل"];
    private static readonly string[] CommissionWords = ["commission", "incentive", "عمولة", "عمولات", "حافز", "حوافز"];

    private static bool Looks(string[] words, params string?[] names) =>
        names.Any(n => !string.IsNullOrWhiteSpace(n) && words.Any(w => n!.Contains(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when a name reads like a commission or incentive (English or Arabic).</summary>
    public static bool LooksLikeCommission(string? nameEn, string? nameAr) => Looks(CommissionWords, nameEn, nameAr);

    /// <summary>
    /// The cautions for a cash allowance's definition. They never block: HR and Finance decide.
    /// <paramref name="atCreation"/> adds the commission note (said once, when the name is first chosen).
    /// </summary>
    public static List<AllowanceWarningDto> Warnings(string? nameEn, string? nameAr, bool eosbIncluded, string? exclusionCategory, bool atCreation)
    {
        var warnings = new List<AllowanceWarningDto>();
        if (eosbIncluded && Looks(ReimbursiveWords, nameEn, nameAr))
            warnings.Add(new(WarningReimbursive,
                "This looks like a reimbursement (travel, phone, fuel, expenses). A reimbursement usually does not count as wage for end of service. Check that it should be included."));
        if (!eosbIncluded && exclusionCategory is EosbExclusionCategories.Other or EosbExclusionCategories.Discretionary)
            warnings.Add(new(WarningFixedExcluded,
                "A fixed monthly cash payment usually counts as wage for end of service; keep the documented basis."));
        if (atCreation && LooksLikeCommission(nameEn, nameAr))
            warnings.Add(new(WarningCommission,
                "Commissions are usually part of the GOSI wage for Saudis; a cash allowance here is never in the GOSI base."));
        return warnings;
    }
}
