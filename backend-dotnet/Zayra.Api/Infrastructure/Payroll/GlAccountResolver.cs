using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>A persisted posting driver flattened for routing (company row already won over tenant row).</summary>
public sealed record GlDriverRow(
    string Key, string Category, string PostingSide, string? MatchSource, string MatchMode,
    string? MatchComponentCode, bool EmitsEmployerExpensePair, string? PairedExpenseDriverKey,
    bool IsSystem, int SortOrder);

/// <summary>
/// POD-B1b — one batch's outstanding bonus accrual, and how much of it THIS payroll run clears.
/// <para>
/// <see cref="AccrualAccount"/> is the accrual line's STORED CreditAccount, not a freshly resolved one:
/// the payroll clearing DR must hit the exact account the accrual credited, so a chart-of-accounts remap
/// between approve and lock can never leave Bonus Payable off zero (same doctrine as the POD-B1
/// settlement/remittance builders, PayrollController.cs:2081-2085).
/// </para>
/// </summary>
/// <param name="CompanyId">
/// The legal entity that holds the accrual being retired — which is NOT always the payroll run's own
/// company: a legacy/seeded run can carry no CompanyId while the accrual it pays is company-stamped
/// (see BonusGlLedger.BuildPayrollClearingAsync). The emitted clearing line is stamped with this, so the
/// payable sub-ledger matches the clearing to the exact position it retires.
/// </param>
/// <param name="ClearedByComponentCode">
/// POD-B1b-FIX (re-audit #5) — how much of <paramref name="Amount"/> belongs to each payroll EARNING
/// component code (<c>BONUS_&lt;TYPE&gt;</c>, <see cref="BonusGlLedger.EarningComponentCode"/>).
/// <para>The un-accrued remainder used to be spread PRO RATA across every deferred bonus component,
/// which mis-states the accounts whenever the components are accrued unevenly: a fully-accrued "Eid"
/// batch (custom driver → 6110) alongside a never-accrued "Perf" batch (→ 6100) put half the remainder
/// on 6110, overstating it and understating 6100 — the Eid bonus expensed twice at account level. The
/// batch→component link is not missing at all: the earning code is derived from the bonus type, and this
/// map carries that derivation out of the same consumed <c>EmployeeBonus</c> rows the clearing is built
/// from, so the remainder can be allocated to the components that are genuinely still un-accrued.</para>
/// <para>Σ(values) == <paramref name="Amount"/>. Null/empty ⇒ no linkage available (legacy plan), and the
/// caller falls back to the pro-rata split.</para>
/// </param>
public sealed record BonusAccrualClearing(
    Guid BatchId, string BatchRef, string AccrualAccount, decimal Amount, Guid? CompanyId = null,
    IReadOnlyDictionary<string, decimal>? ClearedByComponentCode = null)
{
    private static readonly IReadOnlyDictionary<string, decimal> NoComponents =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    /// <summary>Never-null view of <see cref="ClearedByComponentCode"/>.</summary>
    public IReadOnlyDictionary<string, decimal> ByComponentCode => ClearedByComponentCode ?? NoComponents;
}

/// <summary>
/// POD-C1 — one settlement's outstanding payable, and how much of it THIS payroll run clears.
///
/// <para><see cref="PayableAccount"/> is the accrual line's STORED CreditAccount, never a freshly
/// resolved one: the clearing DEBIT must hit the exact account the approval credited, so a
/// chart-of-accounts remap between approve and lock can never leave 2320 Final Settlement Payable off
/// zero. Identical doctrine to <see cref="BonusAccrualClearing.AccrualAccount"/>.</para>
/// </summary>
/// <param name="CompanyId">The legal entity holding the payable being retired — which is not always the
/// run's own company (a legacy/seeded run can carry none while the settlement it pays is stamped).</param>
public sealed record SettlementPayableClearing(
    Guid SettlementId, string EmployeeRef, string PayableAccount, decimal Amount, Guid? CompanyId = null);

/// <summary>Everything the shared GL routing needs, loaded once per posting (company-first).</summary>
public sealed record GlResolutionContext(
    IReadOnlyDictionary<string, (string Code, string Name)> Overrides,
    IReadOnlyDictionary<string, (string Code, string Name)> DriverDefaults,
    IReadOnlyList<GlDriverRow> Drivers)
{
    /// <summary>
    /// POD-B1b — per-batch bonus accrual clearing for the run being posted. Carried on the context
    /// (rather than as a new BuildPayrollGlEntries parameter) so the builder's arity — and therefore the
    /// reflection-driven golden master, PayComponentGoldenMasterTests.cs:356 — is unchanged. Empty by
    /// default, which reproduces the pre-B1b journal exactly.
    /// </summary>
    public IReadOnlyList<BonusAccrualClearing> BonusClearings { get; init; } = Array.Empty<BonusAccrualClearing>();

    /// <summary>
    /// POD-C1 — per-settlement payable clearing for the run being posted. Carried on the context for the
    /// SAME reason <see cref="BonusClearings"/> is (an <c>init</c> property, not a new
    /// <c>BuildPayrollGlEntries</c> parameter) so the builder's arity — and therefore the
    /// reflection-driven golden master, PayComponentGoldenMasterTests — is unchanged. Empty by default,
    /// which reproduces the pre-C1 journal exactly.
    /// </summary>
    public IReadOnlyList<SettlementPayableClearing> SettlementClearings { get; init; }
        = Array.Empty<SettlementPayableClearing>();

    /// <summary>POD-B1b — the legal entity this context was resolved for. Stamped onto every line the
    /// posting builders emit (a plain FinanceGlEntry dimension, never an amount or account change).</summary>
    public Guid? CompanyId { get; init; }

    public static readonly GlResolutionContext Empty = new(
        new Dictionary<string, (string, string)>(),
        new Dictionary<string, (string, string)>(),
        Array.Empty<GlDriverRow>());
}

/// <summary>
/// Company-first GL account resolution — the single implementation shared by the payroll accrual /
/// settlement / remittance paths (PayrollController) and, since POD-B1b, by the bonus, loan and advance
/// posting paths (BonusesController / LoansController / AdvancesController).
///
/// Before B1b those modules hard-coded account label strings and the tenant currency, so in a
/// multi-company tenant they posted to the tenant-default account and stamped the wrong currency. They
/// now call <see cref="LoadAsync"/> + <see cref="Resolve"/> exactly like payroll does, which makes a
/// per-company GlAccountMapping override authoritative for them too.
/// </summary>
public static class GlAccountResolver
{
    /// <summary>Driver key → (code, name): company/tenant mapping override wins, else the persisted
    /// driver default (company row → tenant row), else the compiled catalog default, else Unmapped.</summary>
    public static (string Code, string Name) Resolve(
        string driverKey,
        IReadOnlyDictionary<string, (string Code, string Name)>? overrides,
        IReadOnlyDictionary<string, (string Code, string Name)>? driverDefaults = null)
    {
        if (overrides is not null && overrides.TryGetValue(driverKey, out var o)) return o;
        if (driverDefaults is not null && driverDefaults.TryGetValue(driverKey, out var dd)) return dd;
        if (PayrollGlCatalog.Defaults.TryGetValue(driverKey, out var d)) return d;
        return ("9999", "Unmapped");
    }

    /// <summary>Driver key → the persisted "&lt;code&gt; - &lt;name&gt;" account label written to the ledger.</summary>
    public static string AccountLabel(string driverKey, GlResolutionContext ctx)
    {
        var (code, name) = Resolve(driverKey, ctx.Overrides, ctx.DriverDefaults);
        return $"{code} - {name}";
    }

    /// <summary>
    /// Loads the company-first GL resolution context: mapping overrides (company row wins over tenant
    /// default per driver), the persisted driver defaults (company row → tenant row), and the driver set
    /// for data-driven routing. IgnoreQueryFilters is intentional and mirrors CompanyTaxPolicyResolver:
    /// GL posting is a SYSTEM read that must see BOTH the company's rows and the tenant-default
    /// (CompanyId == null) rows regardless of the caller's own company claims; the WHERE re-applies exact
    /// tenant + company/default scope and never reads another tenant.
    /// </summary>
    public static async Task<GlResolutionContext> LoadAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        // Mapping overrides joined to active accounts; company-scoped mapping/account beat tenant default.
        var mapRows = await db.GlAccountMappings.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.IsActive && (m.CompanyId == companyId || m.CompanyId == null))
            // IgnoreQueryFilters is intentional (both sides of this join): see the summary above —
            // posting must resolve the company's accounts AND the tenant-default accounts regardless of
            // the caller's claims; the WHERE re-applies exact tenant + company/default scope.
            .Join(db.GlAccounts.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.TenantId == tenantId && a.IsActive && (a.CompanyId == companyId || a.CompanyId == null)),
                  m => m.AccountId, a => a.Id,
                  (m, a) => new { m.DriverKey, m.CompanyId, a.Code, a.Name })
            .ToListAsync(ct);
        var overrides = mapRows
            .GroupBy(r => r.DriverKey)
            .ToDictionary(g => g.Key, g =>
            {
                var win = g.OrderByDescending(r => r.CompanyId != null).First();
                return (win.Code, win.Name);
            });

        // IgnoreQueryFilters is intentional: same system-read rationale as the mapping load above —
        // must see the company drivers AND tenant-default drivers regardless of caller claims.
        var driverRows = await db.GlDrivers.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.IsActive && (d.CompanyId == companyId || d.CompanyId == null))
            .ToListAsync(ct);

        // Company driver row wins over the tenant driver row for both routing and default account.
        var driverDefaults = driverRows
            .GroupBy(d => d.Key)
            .ToDictionary(g => g.Key, g =>
            {
                var win = g.OrderByDescending(d => d.CompanyId != null).First();
                return (win.DefaultCode, win.DefaultName);
            });
        var drivers = driverRows
            .GroupBy(d => d.Key)
            .Select(g => g.OrderByDescending(d => d.CompanyId != null).First())
            .Select(d => new GlDriverRow(d.Key, d.Category, d.PostingSide, d.MatchSource, d.MatchMode,
                d.MatchComponentCode, d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey, d.IsSystem, d.SortOrder))
            .ToList();

        return new GlResolutionContext(overrides, driverDefaults, drivers) { CompanyId = companyId };
    }

    /// <summary>
    /// POD-B1b — the currency a GL line for this company must be stamped with: the company's
    /// DefaultCurrency (what the payroll accrual already uses, PayrollController.cs:1199-1201), falling
    /// back to the tenant currency when the row has no company attribution (legacy, pre-backfill data) —
    /// which is byte-identical to the pre-B1b behaviour of the bonus/loan/advance modules.
    /// </summary>
    public static async Task<string> ResolveCurrencyAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        if (companyId is Guid cid)
        {
            // IgnoreQueryFilters is intentional: same system-read rationale as LoadAsync — posting must
            // read the posting company's currency regardless of the caller's claims; TenantId applies.
            var currency = await db.Companies.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == cid)
                .Select(c => c.DefaultCurrency)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(currency)) return currency!;
        }
        return await Zayra.Api.Application.Common.ControllerTenantExtensions.ResolveTenantCurrencyAsync(db, tenantId, ct);
    }

    // ── Data-driven driver routing (Phase 2 driver store) ────────────────────────

    private static int SpecificityRank(string matchMode) => matchMode switch
    {
        GlDriverMatchModes.Exact => 3,
        GlDriverMatchModes.Suffix => 2,
        GlDriverMatchModes.Prefix => 2,
        _ => 1, // Any
    };

    private static bool DriverMatches(GlDriverRow d, string componentCode, string source)
    {
        if (d.MatchSource is not null && !string.Equals(d.MatchSource, source, StringComparison.Ordinal))
            return false;
        return d.MatchMode switch
        {
            GlDriverMatchModes.Exact => d.MatchComponentCode is not null && componentCode == d.MatchComponentCode,
            GlDriverMatchModes.Suffix => d.MatchComponentCode is not null && componentCode.EndsWith(d.MatchComponentCode, StringComparison.Ordinal),
            GlDriverMatchModes.Prefix => d.MatchComponentCode is not null && componentCode.StartsWith(d.MatchComponentCode, StringComparison.Ordinal),
            _ => d.MatchComponentCode is null, // Any
        };
    }

    /// <summary>Most-specific driver for a component within a category, or null (→ compiled fallback).
    /// Add-only authority: system wins on a specificity tie so a custom driver can only WIN by being
    /// strictly more specific.</summary>
    public static GlDriverRow? ResolveDriverForComponent(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source, string category)
    {
        if (drivers.Count == 0) return null;
        return drivers
            .Where(d => d.Category == category && DriverMatches(d, componentCode, source))
            .OrderByDescending(d => SpecificityRank(d.MatchMode))
            .ThenByDescending(d => d.MatchSource != null)
            .ThenBy(d => d.SortOrder)
            .ThenByDescending(d => d.IsSystem)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
