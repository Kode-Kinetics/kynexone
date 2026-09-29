using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// F2 — what a tenant may and may not configure in the pay-component catalog, in ONE place.
///
/// <para><b>The boundary.</b> A tenant component is a CONFIGURED-VALUE component
/// (<see cref="PayComponentEngine.IsConfiguredValue"/>): an Earning or Deduction whose amount is a Fixed
/// monthly value, or a percentage of basic or of gross. That is the only kind whose amount exists nowhere
/// but in the engine, and therefore the only kind Process folds into gross/deductions/net as a new
/// amount. Everything else is refused, for a stated reason:</para>
/// <list type="bullet">
/// <item><b>Statutory</b> (GOSI/SANED/GPSSA/GRSIA/OH, <c>STATUTORY_*</c>) — pack-owned. Rates, ceilings
///   and the covered wage come from the country pack under StatutoryRateGuard; this API can neither create
///   nor edit them, and a tenant component can never enter the covered wage (no GosiSubject input).</item>
/// <item><b>StructureField / provider / Formula</b> — a structure field is ALREADY paid by its system line
///   (a second row would pay it twice); providers are subsystem ledgers; Formula has no evaluator.</item>
/// <item><b>EmployerContribution</b> — an employer cost needs a paired expense DR the poster only builds
///   for statutory lines; accepting one would post a one-sided credit.</item>
/// <item><b>System rows</b> — read-only. They reproduce the pre-F2 payslip byte-for-byte (golden master).</item>
/// <item><b>Reserved codes</b> — every code a subsystem emits (BASIC…, BONUS_*, ADJ_*, ARREARS_*, the
///   settlement, recovery and bonus-tax codes). GL routing groups lines by code, so a collision would
///   merge a tenant line into a subsystem's journal line.</item>
/// </list>
///
/// <para><b>GL mapping is a write-time validation, not a Lock-time surprise.</b> Every tenant component
/// names the GL driver it posts to; the driver must exist, be of the matching category, resolve to a real
/// account (never <c>9999 Unmapped</c>) in the component's scope — and, for a tenant-wide component, in
/// EVERY active company — and must not land on an account the statutory tie-out or net pay depends on
/// (2101 / 2106 / 2100 and the control accounts), so GOSI deducted = recomputed = posted stays exact.</para>
/// </summary>
public static class PayComponentPolicy
{
    public const int MaxCodeLength = 40;
    public const decimal MaxFixedValue = 999_999_999.99m;

    private static readonly Regex CodePattern = new("^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Codes a subsystem emits verbatim — never available to a tenant component.</summary>
    public static readonly IReadOnlySet<string> ReservedCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        FinalSettlementComponents.Gratuity, FinalSettlementComponents.LeaveEncashment,
        FinalSettlementComponents.NoticePay, FinalSettlementComponents.OtherDues,
        FinalSettlementComponents.NoticeShortfall, FinalSettlementComponents.OtherDeduction,
        PayrollRecoveryComponents.ReceivableRecovery, BonusGlDescriptions.PayrollTaxComponentCode,
    };

    /// <summary>Code prefixes owned by a subsystem family (per-instance codes are generated from them).</summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = new[]
    {
        "BONUS_", "ADJ_", PayrollArrearsComponents.Prefix, "SETTLEMENT_",
    };

    /// <summary>GL drivers a tenant component may never post to: the statutory liabilities the GOSI tie-out
    /// reconciles, and the recovery / settlement contra drivers whose balances other ledgers clear.</summary>
    public static readonly IReadOnlySet<string> ForbiddenDriverKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "DED:STATUTORY_EE", "DED:STATUTORY_ER", "DED:RECEIVABLE_RECOVERY", "DED:SETTLEMENT_RECOVERY",
    };

    /// <summary>Drivers whose RESOLVED ACCOUNT a tenant component may not land on either — so a custom driver
    /// defaulting to, say, 2101 cannot smuggle a tenant deduction into the GOSI employee liability.</summary>
    public static readonly IReadOnlyList<string> ProtectedAccountDrivers = new[]
    {
        "DED:STATUTORY_EE", "DED:STATUTORY_ER", "NET_PAYABLE", "EMPLOYER_STATUTORY_EXPENSE",
        "DED:RECEIVABLE_RECOVERY", "EMPLOYEE_RECEIVABLE", "STATUTORY_PREPAID",
        "BONUS_PAYABLE", "SETTLEMENT_PAYABLE", "EOSB_PROVISION", "LOAN_RECEIVABLE", "ADVANCE_RECEIVABLE",
        "CASH_BANK",
    };

    /// <summary>The configurable fields of one component version, as written by the API.</summary>
    public sealed record Definition(
        string Code, string NameEn, string NameAr, string ComponentType, string CalcMethod, decimal Value,
        bool IsTaxable, bool EosbIncluded, string GlDriverKey, int DisplayOrder, DateOnly EffectiveFrom);

    /// <summary>True when the code is owned by the system catalog, a subsystem or the country pack.</summary>
    public static bool IsReservedCode(string code) =>
        PayComponentCatalog.SystemComponentSeeds(Guid.Empty).Any(c => string.Equals(c.Code, code, StringComparison.Ordinal))
        || ReservedCodes.Contains(code)
        || ReservedPrefixes.Any(p => code.StartsWith(p, StringComparison.Ordinal))
        || PayComponentGuard.IsStatutoryComponentCode(code);

    /// <summary>Shape validation that needs no database. Returns field → message.</summary>
    public static Dictionary<string, string> ValidateShape(Definition d)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(d.Code) || d.Code.Length < 2 || d.Code.Length > MaxCodeLength || !CodePattern.IsMatch(d.Code))
            errors["code"] = $"Code must be 2–{MaxCodeLength} characters of A–Z, 0–9 and '_', starting with a letter (e.g. UNION_DUES).";
        else if (IsReservedCode(d.Code))
            errors["code"] = $"'{d.Code}' is reserved: it is a system, statutory or subsystem component code " +
                             "(system catalog, GOSI/GPSSA/GRSIA/SANED, BONUS_*, ADJ_*, ARREARS_*, settlement, recovery or bonus-tax).";

        if (string.IsNullOrWhiteSpace(d.NameEn) || d.NameEn.Trim().Length > 200)
            errors["nameEn"] = "NameEn is required (max 200 characters).";
        if (d.NameAr is { Length: > 200 })
            errors["nameAr"] = "NameAr may be at most 200 characters.";

        if (d.ComponentType is not (PayComponentTypes.Earning or PayComponentTypes.Deduction))
            errors["componentType"] = d.ComponentType == PayComponentTypes.EmployerContribution
                ? "EmployerContribution components cannot be tenant-defined: an employer cost needs a paired expense " +
                  "posting that only the statutory pack lines receive, so it would post a one-sided credit."
                : "ComponentType must be 'Earning' or 'Deduction'.";

        switch (d.CalcMethod)
        {
            case PayComponentCalcMethods.Fixed:
                if (d.Value <= 0m || d.Value > MaxFixedValue || decimal.Round(d.Value, 2) != d.Value)
                    errors["value"] = $"A Fixed component needs a positive monthly amount with at most 2 decimals (≤ {MaxFixedValue:N2}).";
                break;
            case PayComponentCalcMethods.PercentOfBasic:
            case PayComponentCalcMethods.PercentOfGross:
                if (d.Value <= 0m || d.Value > 100m || decimal.Round(d.Value, 4) != d.Value)
                    errors["value"] = "A percentage component needs a value > 0 and ≤ 100, with at most 4 decimals.";
                break;
            case PayComponentCalcMethods.StructureField:
                errors["calcMethod"] = "StructureField components are system-owned: every salary-structure column is already " +
                                       "paid by its system line (BASIC, HOUSING, TRANSPORT, OTHER_ALLOWANCES, FIXED_DEDUCTION), " +
                                       "so a second row would pay it twice. Use Fixed, PercentOfBasic or PercentOfGross.";
                break;
            case PayComponentCalcMethods.Statutory:
                errors["calcMethod"] = "Statutory components are owned by the country pack (GOSI/GPSSA/GRSIA): their rates, " +
                                       "ceilings and covered wage cannot be configured through the pay-component catalog.";
                break;
            default:
                errors["calcMethod"] = "CalcMethod must be Fixed, PercentOfBasic or PercentOfGross.";
                break;
        }

        if (d.ComponentType == PayComponentTypes.Deduction && d.IsTaxable)
            errors["isTaxable"] = "IsTaxable applies to earnings only (it adds the earning to the income-tax base).";
        if (d.ComponentType == PayComponentTypes.Deduction && d.EosbIncluded)
            errors["eosbIncluded"] = "EosbIncluded applies to earnings only (it adds the earning to the end-of-service wage).";

        if (d.DisplayOrder is < 0 or > 100_000)
            errors["displayOrder"] = "DisplayOrder must be between 0 and 100000.";

        if (d.EffectiveFrom.Day != 1)
            errors["effectiveFrom"] = "EffectiveFrom must be the first day of a payroll month: a component version always " +
                                      "covers whole payroll periods, so a mid-period change is never half-applied.";

        if (string.IsNullOrWhiteSpace(d.GlDriverKey))
            errors["glDriverKey"] = "GlDriverKey is required: every tenant component must name the GL driver it posts to.";

        return errors;
    }

    /// <summary>
    /// Validates that <paramref name="driverKey"/> is a real, correctly-categorised, mapped posting target for
    /// a component of <paramref name="componentType"/> in the given scope. For a tenant-wide component
    /// (<paramref name="companyId"/> null) it is checked against the tenant defaults AND every active company,
    /// because a company-level account remap is what the Lock actually resolves. Returns null when valid.
    /// </summary>
    public static async Task<string?> ValidateGlDriverAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, string componentType, string driverKey, CancellationToken ct)
    {
        if (ForbiddenDriverKeys.Contains(driverKey))
            return $"GL driver '{driverKey}' is reserved for statutory / recovery postings. A tenant component posting there " +
                   "would break the GOSI tie-out (deducted = recomputed = posted to 2101/2106) or a recovery ledger.";

        var expectedCategory = componentType == PayComponentTypes.Earning ? GlDriverCategories.Earning : GlDriverCategories.Deduction;

        var scopes = new List<Guid?> { companyId };
        if (companyId is null)
            scopes.AddRange((await ScopedBypass.TenantWide(db.Companies, tenantId,
                        "a tenant-wide component posts in EVERY company, so its GL driver is validated against each one")
                    .AsNoTracking()
                    .Where(c => c.IsActive)
                    .Select(c => c.Id).ToListAsync(ct))
                .Select(id => (Guid?)id));

        foreach (var scope in scopes)
        {
            var ctx = await GlAccountResolver.LoadAsync(db, tenantId, scope, ct);
            var where = scope is null ? "the tenant defaults" : $"company {scope}";

            var row = ctx.Drivers.FirstOrDefault(d => string.Equals(d.Key, driverKey, StringComparison.Ordinal));
            string? category = row?.Category;
            if (category is null && PayrollGlCatalog.Defaults.ContainsKey(driverKey))
                category = driverKey.StartsWith("EARN:", StringComparison.Ordinal) ? GlDriverCategories.Earning
                         : driverKey.StartsWith("DED:", StringComparison.Ordinal) ? GlDriverCategories.Deduction
                         : GlDriverCategories.Balancing;
            if (category is null)
                return $"GL driver '{driverKey}' does not exist in {where}. Create it under Finance → GL drivers, or use an " +
                       "existing Earning/Deduction driver such as EARN:OTHER or DED:OTHER.";
            if (!string.Equals(category, expectedCategory, StringComparison.Ordinal))
                return $"GL driver '{driverKey}' is a {category} driver; a {componentType} component must post to a {expectedCategory} driver.";
            if (row is { EmitsEmployerExpensePair: true })
                return $"GL driver '{driverKey}' emits an employer-expense pair (a statutory employer driver); it cannot carry a tenant component.";

            var (code, name) = GlAccountResolver.Resolve(driverKey, ctx.Overrides, ctx.DriverDefaults);
            if (string.IsNullOrWhiteSpace(code) || code == "9999" || string.Equals(name, "Unmapped", StringComparison.OrdinalIgnoreCase))
                return $"GL driver '{driverKey}' resolves to no account in {where} (Unmapped). Map it to a GL account first — " +
                       "an unmapped line would make the run's journal fail the ERP-readiness check.";

            foreach (var protectedDriver in ProtectedAccountDrivers)
            {
                var (pCode, _) = GlAccountResolver.Resolve(protectedDriver, ctx.Overrides, ctx.DriverDefaults);
                if (string.Equals(pCode, code, StringComparison.Ordinal))
                    return $"GL driver '{driverKey}' resolves to account {code} in {where}, which is the account of '{protectedDriver}'. " +
                           "Tenant components may not post to the statutory liabilities, net salaries payable or control accounts.";
            }
        }
        return null;
    }

    /// <summary>
    /// The latest payroll period (first day) in the component's scope that is COMMITTED — a run that is
    /// approved, under finance review, locked or paid, or a GL period that is closed. A component version may
    /// only start AFTER it, so a catalog change can never apply to a period whose pay has been decided.
    /// A tenant-wide component is bounded by every company's runs; a company component by that company's
    /// runs and any legacy run with no company.
    /// </summary>
    public static async Task<DateOnly?> LatestCommittedPeriodAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var runs = ScopedBypass.TenantWide(db.PayrollRuns, tenantId,
                "the committed-period guard must see every run the component can reach, not only the caller's entities")
            .AsNoTracking()
            .Where(r => r.Status != "Draft" && r.Status != "Processed" && r.Status != "Voided");
        if (companyId is not null) runs = runs.Where(r => r.CompanyId == companyId || r.CompanyId == null);
        var latestRun = await runs
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .Select(r => new { r.Year, r.Month })
            .FirstOrDefaultAsync(ct);

        var closes = ScopedBypass.TenantWide(db.GlPeriodCloses, tenantId,
                "the committed-period guard must see every GL close the component can reach, including group-wide closes")
            .AsNoTracking()
            .Where(p => p.Status == GlPeriodStatuses.Closed);
        if (companyId is not null) closes = closes.Where(p => p.CompanyId == companyId || p.CompanyId == null);
        var closedPeriods = await closes.Select(p => p.Period).ToListAsync(ct);

        DateOnly? latest = latestRun is null ? null : new DateOnly(latestRun.Year, latestRun.Month, 1);
        foreach (var p in closedPeriods)
            if (DateOnly.TryParseExact(p + "-01", "yyyy-MM-dd", out var d) && (latest is null || d > latest))
                latest = d;
        return latest;
    }

    /// <summary>Draft/Processed runs in scope whose period is on or after <paramref name="from"/> — they
    /// were computed with the previous catalog and must be re-processed to pick the change up.</summary>
    public static async Task<List<object>> StaleRunsAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, DateOnly from, CancellationToken ct)
    {
        var fromKey = from.Year * 100 + from.Month;
        var q = ScopedBypass.TenantWide(db.PayrollRuns, tenantId,
                "stale-run report covers every processed run the changed component can reach")
            .AsNoTracking()
            .Where(r => r.Status == "Processed" && r.Year * 100 + r.Month >= fromKey);
        if (companyId is not null) q = q.Where(r => r.CompanyId == companyId || r.CompanyId == null);
        return (await q.Select(r => new { r.Id, r.Year, r.Month, r.Status }).ToListAsync(ct)).Cast<object>().ToList();
    }
}
