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

    /// <summary>
    /// Configuration faults found while resolving, keyed by driver key: a mapping (or default account)
    /// that points at an account switched off in the chart, or a SYSTEM posting rule that has been
    /// switched off. Resolution itself is unchanged (so every non-payroll caller posts exactly as
    /// before); the payroll Lock and GL preview read this and refuse to post rather than quietly
    /// falling back to some other account. Empty by default.
    /// </summary>
    public IReadOnlyDictionary<string, GlResolutionProblem> Problems { get; init; }
        = new Dictionary<string, GlResolutionProblem>(StringComparer.Ordinal);

    /// <summary>
    /// The SYSTEM drivers that are switched off (no active row for the key), flattened exactly as an
    /// active row would be. Routing never sees them; the payroll Lock uses them only to ask "would any
    /// pay item on THIS run have gone to that rule had it been on?" so a switched-off rule the run
    /// never touches does not block the month. Empty by default.
    /// </summary>
    public IReadOnlyList<GlDriverRow> InactiveSystemDrivers { get; init; } = Array.Empty<GlDriverRow>();

    // ── Cost accounting (WP-B). All `init`, so neither this record's constructor nor BuildPayrollGlEntries changes arity. ──
    public IReadOnlyDictionary<string, (string AccountType, string Rule)> AccountRules { get; init; }
        = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
    public IReadOnlyDictionary<(string DriverKey, string Class), (string Code, string Name)> ClassOverrides { get; init; }
        = new Dictionary<(string, string), (string, string)>();
    public IReadOnlyDictionary<string, Guid> FixedCostCentres { get; init; } = new Dictionary<string, Guid>(StringComparer.Ordinal);
    public IReadOnlyDictionary<Guid, GlCostCentreInfo> CostCentres { get; init; } = new Dictionary<Guid, GlCostCentreInfo>();
    public bool CostAccountingEnabled { get; init; }
    /// <summary>
    /// Payroll row id to the functional class its destination account was routed by, for rows whose cost-centre DIMENSION was
    /// suppressed (the routed account does not accept one). The stamp on such a row is null, so without this the journal builder would
    /// fall back to the classless account; with it, suppressing the dimension never changes the destination. Empty unless costing is on.
    /// </summary>
    public IReadOnlyDictionary<Guid, string> RowRoutingClass { get; init; } = new Dictionary<Guid, string>();
    public Guid? CompanyDefaultCostCentreId { get; init; }
    public short CostCentreSmallCellMin { get; init; }
    /// <summary>Driver keys whose classless override is a COMPANY mapping (not the tenant default). Used by
    /// <see cref="GlAccountResolver.AccountFor"/> for the precedence company+class &gt; company &gt; tenant+class &gt; tenant.</summary>
    public IReadOnlySet<string> CompanyScopedOverrides { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>The (driver, class) pairs whose class override is a COMPANY mapping.</summary>
    public IReadOnlySet<(string DriverKey, string Class)> CompanyScopedClassOverrides { get; init; } = new HashSet<(string, string)>();

    public static readonly GlResolutionContext Empty = new(
        new Dictionary<string, (string, string)>(),
        new Dictionary<string, (string, string)>(),
        Array.Empty<GlDriverRow>());
}

/// <summary>A cost centre as the posting builders see it (live row of the posting company).</summary>
public sealed record GlCostCentreInfo(string Code, string Name, string? GlSegment, string? FunctionalClass, Guid? ParentId, bool IsActive);

/// <summary>The kinds of problem the resolver can report for a driver. Stable strings: the GL resolution
/// API returns them verbatim.</summary>
public static class GlResolutionProblemKinds
{
    /// <summary>Nothing resolves the driver to a real account (no mapping, no default, no built-in).</summary>
    public const string Unmapped = "Unmapped";
    /// <summary>The account the driver resolves to exists in the chart but is switched off.</summary>
    public const string InactiveAccount = "InactiveAccount";
    /// <summary>The resolved account code is not in this company's chart of accounts. The payroll Lock
    /// refuses to post a rule the run uses in this state (the ERP would receive an account the chart does
    /// not have), except for a tenant with no chart at all, which posts the built-in labels as before.</summary>
    public const string NotInChart = "NotInChart";
    /// <summary>A system posting rule has been switched off, so its components silently re-route.</summary>
    public const string InactiveDriver = "InactiveDriver";
}

/// <summary>One driver's configuration fault, phrased for the person who has to fix it.</summary>
public sealed record GlResolutionProblem(
    string DriverKey, string Label, string Kind, string? AccountCode, string? AccountName, string Message);

/// <summary>How one posting driver resolves for a company, and where that answer came from.</summary>
public sealed record GlDriverResolution(
    string DriverKey, string Label, string Category, string Side, bool IsSystem,
    Guid? AccountId, string AccountCode, string AccountName,
    string Source, bool ExistsInChart, bool IsActive, string? Problem);

/// <summary>Where a resolved account came from, in precedence order.</summary>
public static class GlResolutionSources
{
    public const string CompanyMapping = "CompanyMapping";
    public const string GroupMapping = "GroupMapping";
    public const string CompanyDriverDefault = "CompanyDriverDefault";
    public const string GroupDriverDefault = "GroupDriverDefault";
    public const string BuiltIn = "BuiltIn";
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

    /// <summary>
    /// Class-aware account choice. Precedence: company+class &gt; company &gt; tenant+class &gt; tenant &gt; driver
    /// default &gt; built-in catalog. With no class (or no class rows) this is exactly <see cref="Resolve"/>, so a
    /// tenant that never configures a class posts to the same accounts as before.
    /// </summary>
    public static (string Code, string Name) AccountFor(string driverKey, string? ccClass, GlResolutionContext ctx)
    {
        if (!string.IsNullOrEmpty(ccClass))
        {
            var key = (driverKey, ccClass);
            var hasClass = ctx.ClassOverrides.TryGetValue(key, out var byClass);
            if (hasClass && ctx.CompanyScopedClassOverrides.Contains(key)) return byClass;
            if (ctx.CompanyScopedOverrides.Contains(driverKey) && ctx.Overrides.TryGetValue(driverKey, out var companyPlain)) return companyPlain;
            if (hasClass) return byClass;
        }
        return Resolve(driverKey, ctx.Overrides, ctx.DriverDefaults);
    }

    /// <summary>Driver key → the persisted "&lt;code&gt; - &lt;name&gt;" account label written to the ledger.</summary>
    public static string AccountLabel(string driverKey, GlResolutionContext ctx)
    {
        var (code, name) = Resolve(driverKey, ctx.Overrides, ctx.DriverDefaults);
        return $"{code} - {name}";
    }

    // ── Raw configuration, loaded once and read by BOTH LoadAsync and DescribeAsync ─────────────
    // One loader so the posting path and the "how does each driver resolve?" API can never disagree.
    private sealed record MappingRow(string DriverKey, Guid? CompanyId, Guid AccountId, string Code, string Name, bool AccountIsActive,
        string? FunctionalClass = null, Guid? FixedCostCenterId = null);
    private sealed record AccountRow(Guid Id, Guid? CompanyId, string Code, string Name, bool IsActive,
        string AccountType = "Expense", string CostCenterRule = "Optional");
    private sealed record CompanyCosting(bool Enabled, Guid? DefaultCostCenterId, short SmallCellMin);
    /// <summary><see cref="Mappings"/> holds the CLASSLESS rows only (so every pre-existing reader is unchanged);
    /// the functional-class rows ride separately in <see cref="ClassMappings"/>.</summary>
    private sealed record Snapshot(
        IReadOnlyList<MappingRow> Mappings, IReadOnlyList<GlDriver> Drivers, IReadOnlyList<AccountRow> Accounts,
        IReadOnlyList<MappingRow>? ClassMappings = null, CompanyCosting? Costing = null,
        IReadOnlyList<(Guid Id, GlCostCentreInfo Info)>? CostCentres = null);

    private static async Task<Snapshot> LoadSnapshotAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        // Mappings joined to their accounts — INCLUDING switched-off accounts. The join used to filter
        // a.IsActive, which silently dropped a mapping whose account had been deactivated and let
        // posting fall back to the driver default: the journal went somewhere nobody chose and nothing
        // said so. The inactive row is now loaded so it can be REPORTED; see BuildContext.
        // IgnoreQueryFilters is intentional: GL posting is a SYSTEM read of the company's AND the
        // tenant-default rows regardless of the caller's claims; the WHERE re-applies exact scope.
        var mapRows = await db.GlAccountMappings.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.IsActive && (m.CompanyId == companyId || m.CompanyId == null))
            // IgnoreQueryFilters is intentional (both sides of this join): see the LoadAsync summary —
            // posting must resolve the company's accounts AND the tenant-default accounts regardless of
            // the caller's claims; the WHERE re-applies exact tenant + company/default scope.
            .Join(db.GlAccounts.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.TenantId == tenantId && (a.CompanyId == companyId || a.CompanyId == null)),
                  m => m.AccountId, a => a.Id,
                  (m, a) => new MappingRow(m.DriverKey, m.CompanyId, a.Id, a.Code, a.Name, a.IsActive, m.FunctionalClass, m.SegmentCostCenterId))
            .ToListAsync(ct);

        // IgnoreQueryFilters is intentional: same system-read rationale as the mapping load above —
        // must see the company drivers AND tenant-default drivers regardless of caller claims.
        // Inactive rows are loaded too, only so a switched-off SYSTEM driver can be reported.
        var driverRows = await db.GlDrivers.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && (d.CompanyId == companyId || d.CompanyId == null))
            .ToListAsync(ct);

        // The chart the company posts into: its own accounts plus the tenant-default (CompanyId == null)
        // accounts, whatever the caller's own company claims (same system-read rationale as above).
        var accounts = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.GlAccounts, tenantId,
                "GL resolution reads the posting company's chart plus the tenant-default chart regardless of the caller's company claims")
            .AsNoTracking()
            .Where(a => a.CompanyId == companyId || a.CompanyId == null)
            .Select(a => new AccountRow(a.Id, a.CompanyId, a.Code, a.Name, a.IsActive, a.AccountType, a.CostCenterRule))
            .ToListAsync(ct);

        // Cost accounting: the posting company's switch, default cost centre and K, and — only when the switch is on —
        // its live cost centres. A company with the switch off loads nothing extra, so nothing about its posting moves.
        CompanyCosting? costing = null;
        List<(Guid Id, GlCostCentreInfo Info)>? costCentres = null;
        if (companyId is { } cid)
        {
            costing = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.Companies, tenantId,
                    "GL resolution reads the posting company's costing switch regardless of the caller's company claims")
                .AsNoTracking().Where(c => c.Id == cid)
                .Select(c => new CompanyCosting(c.CostAccountingEnabled, c.DefaultCostCenterId, c.CostCentreSmallCellMin))
                .FirstOrDefaultAsync(ct);
            if (costing is { Enabled: true })
            {
                var rows = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.CostCenters, tenantId,
                        "GL resolution reads the posting company's own cost centres regardless of the caller's company claims")
                    .AsNoTracking().Where(c => c.CompanyId == cid && !c.IsDeleted)
                    .Select(c => new { c.Id, c.Code, c.Name, c.GlSegment, c.FunctionalClass, c.ParentId, c.IsActive })
                    .ToListAsync(ct);
                costCentres = rows.Select(c => (c.Id, new GlCostCentreInfo(c.Code, c.Name, c.GlSegment, c.FunctionalClass, c.ParentId, c.IsActive))).ToList();
            }
        }

        return new Snapshot(
            mapRows.Where(m => m.FunctionalClass == null).ToList(), driverRows, accounts,
            mapRows.Where(m => m.FunctionalClass != null).ToList(), costing, costCentres);
    }

    /// <summary>Company row beats the tenant row.</summary>
    private static T CompanyFirst<T>(IEnumerable<T> rows, Func<T, Guid?> company) =>
        rows.OrderByDescending(r => company(r) != null).First();

    private static AccountRow? ChartAccount(Snapshot s, string code) =>
        s.Accounts.Where(a => string.Equals(a.Code, code, StringComparison.Ordinal))
            .OrderByDescending(a => a.CompanyId != null).ThenByDescending(a => a.IsActive).FirstOrDefault();

    private static string DriverLabel(Snapshot s, string key) =>
        s.Drivers.Where(d => d.Key == key).Select(d => d.Label).FirstOrDefault()
        ?? PayrollGlCatalog.Drivers.FirstOrDefault(d => d.Key == key)?.Label
        ?? key;

    private static GlResolutionContext BuildContext(Snapshot s, Guid? companyId)
    {
        // Overrides: exactly the pre-existing semantics (active mapping → ACTIVE account, company wins),
        // so every caller resolves the same label it always did. Faults are reported separately below.
        var overrides = s.Mappings.Where(m => m.AccountIsActive)
            .GroupBy(r => r.DriverKey)
            .ToDictionary(g => g.Key, g =>
            {
                var win = CompanyFirst(g, r => r.CompanyId);
                return (win.Code, win.Name);
            });

        var activeDrivers = s.Drivers.Where(d => d.IsActive).ToList();
        // Company driver row wins over the tenant driver row for both routing and default account.
        var driverDefaults = activeDrivers
            .GroupBy(d => d.Key)
            .ToDictionary(g => g.Key, g =>
            {
                var win = CompanyFirst(g, d => d.CompanyId);
                return (win.DefaultCode, win.DefaultName);
            });
        var drivers = activeDrivers
            .GroupBy(d => d.Key)
            .Select(g => CompanyFirst(g, d => d.CompanyId))
            .Select(d => new GlDriverRow(d.Key, d.Category, d.PostingSide, d.MatchSource, d.MatchMode,
                d.MatchComponentCode, d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey, d.IsSystem, d.SortOrder))
            .ToList();

        // Switched-off SYSTEM keys (no active row at either scope), company row first, as routing rows.
        var inactiveSystem = s.Drivers.GroupBy(d => d.Key)
            .Where(g => !g.Any(d => d.IsActive) && g.Any(d => d.IsSystem))
            .Select(g => CompanyFirst(g.Where(d => d.IsSystem), d => d.CompanyId))
            .Select(d => new GlDriverRow(d.Key, d.Category, d.PostingSide, d.MatchSource, d.MatchMode,
                d.MatchComponentCode, d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey, d.IsSystem, d.SortOrder))
            .ToList();

        // ── cost accounting (init-only data; nothing above this line changed) ──
        var classRows = (s.ClassMappings ?? Array.Empty<MappingRow>()).Where(m => m.AccountIsActive)
            .GroupBy(m => (m.DriverKey, m.FunctionalClass!))
            .ToDictionary(g => g.Key, g => { var w = CompanyFirst(g, r => r.CompanyId); return (w.Code, w.Name); });
        var companyClass = (s.ClassMappings ?? Array.Empty<MappingRow>()).Where(m => m.AccountIsActive && m.CompanyId != null)
            .Select(m => (m.DriverKey, m.FunctionalClass!)).ToHashSet();
        var companyPlain = s.Mappings.Where(m => m.AccountIsActive && m.CompanyId != null).Select(m => m.DriverKey).ToHashSet(StringComparer.Ordinal);
        var accountRules = s.Accounts.GroupBy(a => a.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var a = g.OrderByDescending(x => x.CompanyId != null).ThenByDescending(x => x.IsActive).First();
                return (a.AccountType, a.CostCenterRule);
            }, StringComparer.Ordinal);
        // The fixed cost centre of a posting rule is a COMPANY mapping property (a tenant default cannot carry one).
        var fixedCc = companyId is null
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            : s.Mappings.Where(m => m.CompanyId == companyId && m.FixedCostCenterId != null)
                .GroupBy(m => m.DriverKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().FixedCostCenterId!.Value, StringComparer.Ordinal);

        var ctx = new GlResolutionContext(overrides, driverDefaults, drivers)
        {
            CompanyId = companyId, InactiveSystemDrivers = inactiveSystem,
            AccountRules = accountRules, ClassOverrides = classRows, FixedCostCentres = fixedCc,
            CompanyScopedOverrides = companyPlain, CompanyScopedClassOverrides = companyClass,
            CostCentres = (s.CostCentres ?? Array.Empty<(Guid, GlCostCentreInfo)>()).ToDictionary(c => c.Id, c => c.Info),
            CostAccountingEnabled = s.Costing?.Enabled ?? false,
            CompanyDefaultCostCentreId = s.Costing?.DefaultCostCenterId,
            CostCentreSmallCellMin = s.Costing?.SmallCellMin ?? 0,
        };
        return ctx with { Problems = FindProblems(s, ctx) };
    }

    /// <summary>
    /// The faults that make a driver's resolution untrustworthy. Fail CLOSED: the payroll Lock treats
    /// each of these like an unmapped account and refuses to post.
    /// <list type="bullet">
    /// <item><b>InactiveDriver</b> — a SYSTEM driver switched off with no active row for the key. Its
    /// components then fall through to the catch-all (EARN:BASIC off ⇒ basic salary lands in 5099), so
    /// the journal still balances while every cost report is wrong.</item>
    /// <item><b>InactiveAccount</b> — the winning mapping (company beats group, active or not) names an
    /// account that is switched off, or the driver's default code is an inactive account in the chart.
    /// Before this the mapping was dropped and posting fell back to the default without a word.</item>
    /// <item><b>NotInChart</b> — no mapping wins and the driver default (or the built-in default) names
    /// a code the company's chart does not have, e.g. an account re-coded after the default was set.
    /// The journal would carry a code the ERP cannot match. NOT reported for a tenant whose chart at
    /// this scope is completely empty: such a tenant has never set up a chart and has always posted the
    /// built-in labels, and blocking it would stop every payroll it locks until someone seeds a chart.</item>
    /// </list>
    /// </summary>
    private static IReadOnlyDictionary<string, GlResolutionProblem> FindProblems(Snapshot s, GlResolutionContext ctx)
    {
        var problems = new Dictionary<string, GlResolutionProblem>(StringComparer.Ordinal);

        foreach (var g in s.Drivers.GroupBy(d => d.Key))
        {
            if (g.Any(d => d.IsActive) || !g.Any(d => d.IsSystem)) continue;
            var row = CompanyFirst(g, d => d.CompanyId);
            problems[g.Key] = new GlResolutionProblem(g.Key, row.Label, GlResolutionProblemKinds.InactiveDriver, null, null,
                $"The built-in posting rule \"{row.Label}\" ({g.Key}) has been switched off, so the pay items it covers would " +
                "post to a different account without anyone choosing that. Built-in posting rules cannot be switched off: " +
                "turn it back on in Finance > GL setup, then lock again.");
        }

        var mappingWinners = s.Mappings
            .GroupBy(m => m.DriverKey)
            .ToDictionary(g => g.Key, g => CompanyFirst(g, m => m.CompanyId));
        var keys = mappingWinners.Keys
            .Concat(ctx.DriverDefaults.Keys)
            .Concat(PayrollGlCatalog.Defaults.Keys)
            .Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (problems.ContainsKey(key)) continue;
            string? code = null, name = null;
            if (mappingWinners.TryGetValue(key, out var win))
            {
                if (!win.AccountIsActive) { code = win.Code; name = win.Name; }
            }
            else
            {
                var (rc, rn) = Resolve(key, ctx.Overrides, ctx.DriverDefaults);
                var acct = string.IsNullOrWhiteSpace(rc) ? null : ChartAccount(s, rc);
                if (acct is not null && !acct.IsActive) { code = acct.Code; name = string.IsNullOrWhiteSpace(rn) ? acct.Name : rn; }
                else if (acct is null && s.Accounts.Count > 0 && !IsUnmappedPlaceholder(rc, rn))
                {
                    var missingLabel = DriverLabel(s, key);
                    problems[key] = new GlResolutionProblem(key, missingLabel, GlResolutionProblemKinds.NotInChart, rc, rn,
                        $"\"{missingLabel}\" ({key}) posts to account {rc} {rn}, which is not in the chart of accounts. " +
                        $"Add account {rc} to the chart, or map this posting rule to an account that exists, then lock again.");
                    continue;
                }
            }
            if (code is null) continue;
            var label = DriverLabel(s, key);
            problems[key] = new GlResolutionProblem(key, label, GlResolutionProblemKinds.InactiveAccount, code, name,
                $"\"{label}\" ({key}) posts to account {code} {name}, which is switched off in the chart of accounts. " +
                "Switch the account back on, or map this posting rule to an active account, then lock again.");
        }
        return problems;
    }

    /// <summary>The "nothing resolves" placeholder (reported as Unmapped, never as NotInChart).</summary>
    private static bool IsUnmappedPlaceholder(string? code, string? name) =>
        string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)
        || code.Equals("UNMAPPED", StringComparison.OrdinalIgnoreCase) || code == "9999"
        || name.Equals("Unmapped", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the company-first GL resolution context: mapping overrides (company row wins over tenant
    /// default per driver), the persisted driver defaults (company row → tenant row), the driver set
    /// for data-driven routing, and any configuration faults (<see cref="GlResolutionContext.Problems"/>).
    /// IgnoreQueryFilters is intentional and mirrors CompanyTaxPolicyResolver:
    /// GL posting is a SYSTEM read that must see BOTH the company's rows and the tenant-default
    /// (CompanyId == null) rows regardless of the caller's own company claims; the WHERE re-applies exact
    /// tenant + company/default scope and never reads another tenant.
    /// </summary>
    public static async Task<GlResolutionContext> LoadAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
        => BuildContext(await LoadSnapshotAsync(db, tenantId, companyId, ct), companyId);

    /// <summary>
    /// Every posting driver visible to the company, how it resolves, where the answer came from, and
    /// what (if anything) is wrong with it. Built from the SAME snapshot and the SAME
    /// <see cref="Resolve"/> call the posting path uses, so what this reports is what will post.
    /// </summary>
    public static async Task<IReadOnlyList<GlDriverResolution>> DescribeAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var s = await LoadSnapshotAsync(db, tenantId, companyId, ct);
        var ctx = BuildContext(s, companyId);

        var mappingWinners = s.Mappings
            .GroupBy(m => m.DriverKey).ToDictionary(g => g.Key, g => CompanyFirst(g, m => m.CompanyId));

        // The driver set: the store (company row wins per key; a key whose rows are all switched off is
        // still listed so it can be flagged) plus every built-in key the store does not carry.
        var stored = s.Drivers.GroupBy(d => d.Key)
            .Select(g => g.Any(d => d.IsActive)
                ? CompanyFirst(g.Where(d => d.IsActive), d => d.CompanyId)
                : CompanyFirst(g, d => d.CompanyId))
            .ToList();
        var storedKeys = stored.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var builtIn = PayrollGlCatalog.SystemDriverSeeds(Guid.Empty).Where(d => !storedKeys.Contains(d.Key));
        var all = stored.Concat(builtIn)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

        var rows = new List<GlDriverResolution>(all.Count);
        foreach (var d in all)
        {
            var (code, name) = Resolve(d.Key, ctx.Overrides, ctx.DriverDefaults);
            string source;
            Guid? accountId;
            bool existsInChart, isActive;
            if (mappingWinners.TryGetValue(d.Key, out var m))
            {
                // The winning mapping (company beats group). When its account is switched off, report the
                // account that was actually CHOSEN — not the fallback the posting would quietly have used.
                source = m.CompanyId != null ? GlResolutionSources.CompanyMapping : GlResolutionSources.GroupMapping;
                code = m.Code; name = m.Name;
                accountId = m.AccountId; existsInChart = true; isActive = m.AccountIsActive;
            }
            else
            {
                var winner = s.Drivers.Where(x => x.Key == d.Key && x.IsActive).OrderByDescending(x => x.CompanyId != null).FirstOrDefault();
                source = winner is null ? GlResolutionSources.BuiltIn
                    : winner.CompanyId != null ? GlResolutionSources.CompanyDriverDefault : GlResolutionSources.GroupDriverDefault;
                var acct = string.IsNullOrWhiteSpace(code) ? null : ChartAccount(s, code);
                accountId = acct?.Id; existsInChart = acct is not null; isActive = acct?.IsActive ?? false;
            }

            var unmapped = string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)
                || code.Equals("UNMAPPED", StringComparison.OrdinalIgnoreCase) || code == "9999"
                || name.Equals("Unmapped", StringComparison.OrdinalIgnoreCase);
            string? problem = ctx.Problems.TryGetValue(d.Key, out var p) && p.Kind == GlResolutionProblemKinds.InactiveDriver
                ? GlResolutionProblemKinds.InactiveDriver
                : unmapped ? GlResolutionProblemKinds.Unmapped
                : ctx.Problems.TryGetValue(d.Key, out var p2) ? p2.Kind
                : !existsInChart ? GlResolutionProblemKinds.NotInChart
                : null;
            if (unmapped) { accountId = null; existsInChart = false; isActive = false; }

            rows.Add(new GlDriverResolution(d.Key, d.Label, d.Category, d.PostingSide, d.IsSystem,
                accountId, code, name, source, existsInChart, isActive, problem));
        }
        return rows;
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
