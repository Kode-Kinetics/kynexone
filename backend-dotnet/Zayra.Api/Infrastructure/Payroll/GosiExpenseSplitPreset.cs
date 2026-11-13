using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// L1.4 — the one-click "split employer GOSI expense by scheme" preset.
///
/// <para><b>What it changes.</b> By default the three employer GOSI components (annuities, SANED, occupational hazards) all debit one
/// pooled expense account (5101, driver <c>EMPLOYER_STATUTORY_EXPENSE</c>). The preset gives each scheme its own expense account
/// (5102 / 5103 / 5104) so the expense reconciles to the GOSI invoice by scheme. It is OFF for every company until someone presses it,
/// so a company that never does sees the exact journal it always saw.</para>
///
/// <para><b>How, with no new posting code.</b> The posting builder already supports a per-component "employer expense pair": a deduction
/// driver that matches one component code, credits its liability and emits the balancing expense debit to a named paired driver. The
/// preset creates, for the chosen scope (one company, or the whole group):
/// three Exact-match deduction drivers (<c>DED:GOSI_ANN_ER</c>, <c>DED:GOSI_SANED_ER</c>, <c>DED:GOSI_OH_ER</c>) that are MORE SPECIFIC than
/// the system <c>DED:STATUTORY_ER</c> Suffix rule, so they win routing for exactly those three component codes; three balancing expense
/// drivers (<c>EXP:GOSI_ANN</c> ...) that carry the new accounts; and the three expense accounts themselves at the tenant default
/// when absent.</para>
///
/// <para><b>The credit side never moves.</b> Each new deduction driver is given the SAME liability account as the system employer-social-
/// insurance driver resolves to in that scope (a copy of its mapping row where one exists, else its default), so a tenant that had
/// remapped 2106 keeps crediting that account. Every credit still lands on the same GOSI payable with the same component code, so the
/// GOSI reconciliation, the remittance (which clears the STORED credit accounts) and a void are untouched. The debit total equals the
/// credit total by construction: each credit group emits one expense debit of the same amount.</para>
///
/// <para><b>Never takes a code a tenant uses for something else.</b> If a code 5102 / 5103 / 5104 already exists in the company's chart
/// (or the tenant default) under another name or type, the preset refuses and changes nothing; it never repurposes an account.</para>
///
/// <para><b>Turning it off</b> switches the three routing drivers off again (and leaves the accounts and the paired drivers, which post
/// nothing on their own). History is never rewritten: lines already posted keep the accounts they were posted to.</para>
/// </summary>
public static class GosiExpenseSplitPreset
{
    public sealed record Scheme(string Component, string CreditDriverKey, string ExpenseDriverKey, string AccountCode, string AccountName, string CreditLabel);

    public static readonly IReadOnlyList<Scheme> Schemes = new[]
    {
        new Scheme("GOSI-ANN-ER",   "DED:GOSI_ANN_ER",   "EXP:GOSI_ANN",   "5102", "Employer GOSI - Annuities Expense",             "Deduction - GOSI Annuities (Employer)"),
        new Scheme("GOSI-SANED-ER", "DED:GOSI_SANED_ER", "EXP:GOSI_SANED", "5103", "Employer GOSI - SANED Expense",                 "Deduction - GOSI SANED (Employer)"),
        new Scheme("GOSI-OH-ER",    "DED:GOSI_OH_ER",    "EXP:GOSI_OH",    "5104", "Employer GOSI - Occupational Hazards Expense", "Deduction - GOSI Occupational Hazards (Employer)"),
    };

    /// <summary>The system driver whose liability account the three credit drivers must keep using.</summary>
    public const string PooledCreditDriverKey = "DED:STATUTORY_ER";

    public const string CodeTakenCode = "gosi_split_account_code_taken";
    public const string NotSeededCode = "gosi_split_gl_not_seeded";

    public sealed record Result(bool Enabled, int AccountsCreated, int DriversCreated, int MappingsCreated, string? ErrorCode, string? ErrorMessage)
    {
        public bool Ok => ErrorCode is null;
    }

    /// <summary>True when all three routing drivers exist and are active in exactly this scope.</summary>
    public static async Task<bool> IsEnabledAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var keys = Schemes.Select(s => s.CreditDriverKey).ToList();
        var active = await ScopedBypass.TenantWide(db.GlDrivers, tenantId, "The preset status asks about one exact scope of the tenant's own drivers.")
            .AsNoTracking()
            .Where(d => d.CompanyId == companyId && keys.Contains(d.Key) && d.IsActive)
            .Select(d => d.Key).ToListAsync(ct);
        return active.Count == keys.Count;
    }

    public static async Task<Result> EnableAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        // The pooled system driver must be in the store: a tenant that still runs on the compiled fallback has no rows to copy the
        // credit account from, and its chart may not exist yet. Seed the defaults first.
        var pooled = await ScopedBypass.TenantWide(db.GlDrivers, tenantId, "The preset reads the system employer-statutory driver of the tenant to keep its credit account.")
            .AsNoTracking()
            .Where(d => d.Key == PooledCreditDriverKey && d.IsSystem && d.CompanyId == null)
            .FirstOrDefaultAsync(ct);
        if (pooled is null)
            return new Result(false, 0, 0, 0, NotSeededCode,
                "The chart of accounts and posting rules have not been seeded for this workspace yet. Seed the defaults in Setup, then apply this preset.");

        // Accounts: the tenant-default chart carries them. Refuse on any clash, in the tenant default or the company's own chart.
        var codes = Schemes.Select(s => s.AccountCode).ToList();
        var existing = await ScopedBypass.TenantWide(db.GlAccounts, tenantId, "The preset checks the codes it would create against the tenant default and the company's own chart.")
            .Where(a => codes.Contains(a.Code) && (a.CompanyId == null || a.CompanyId == companyId))
            .ToListAsync(ct);
        foreach (var s in Schemes)
        {
            var clash = existing.FirstOrDefault(a => a.Code == s.AccountCode && (a.Name != s.AccountName || !string.Equals(a.AccountType, "Expense", StringComparison.OrdinalIgnoreCase)));
            if (clash is not null)
                return new Result(false, 0, 0, 0, CodeTakenCode,
                    $"Account code {s.AccountCode} is already used for '{clash.Name}'. The split would repurpose it, so nothing was changed. Rename or re-code that account first.");
        }

        var accountsCreated = 0;
        foreach (var s in Schemes)
        {
            if (existing.Any(a => a.Code == s.AccountCode && a.CompanyId == null)) continue;
            var account = new GlAccount { TenantId = tenantId, CompanyId = null, Code = s.AccountCode, Name = s.AccountName, AccountType = "Expense", IsActive = true };
            db.GlAccounts.Add(account);
            existing.Add(account);
            accountsCreated++;
        }
        // An inactive seeded account would make Lock refuse: bring it back, it is ours (same name, same type).
        foreach (var a in existing.Where(a => !a.IsActive && Schemes.Any(s => s.AccountCode == a.Code))) a.IsActive = true;
        await db.SaveChangesAsync(ct);

        // The liability the three credit drivers must keep using: the mapping the pooled driver has in this scope, else the one it has
        // at the tenant default, else its default code.
        var pooledMappings = await ScopedBypass.TenantWide(db.GlAccountMappings, tenantId, "The preset copies the employer-statutory liability mapping so the credit side does not move.")
            .AsNoTracking()
            .Where(m => m.DriverKey == PooledCreditDriverKey && m.IsActive && m.FunctionalClass == null)
            .ToListAsync(ct);
        // One target per (scope, account): what each new credit mapping must point at so the credit side never moves.
        //   • company scope: the company's own mapping, else the tenant default's (as before);
        //   • group scope: the group default is the tenant-default mapping ALONE (never another company's account), and every company that
        //     pays its employer GOSI into its OWN liability keeps it, copied onto that same company for the three new keys.
        var targets = new List<(Guid? CompanyId, Guid AccountId, Guid? SegmentCostCenterId)>();
        if (companyId is null)
        {
            var defaultRow = pooledMappings.FirstOrDefault(m => m.CompanyId == null);
            if (defaultRow is not null) targets.Add((null, defaultRow.AccountId, defaultRow.SegmentCostCenterId));
            foreach (var perCompany in pooledMappings.Where(m => m.CompanyId != null).GroupBy(m => m.CompanyId))
            {
                var row = perCompany.First();
                targets.Add((perCompany.Key, row.AccountId, row.SegmentCostCenterId));
            }
        }
        else
        {
            var scopeRow = pooledMappings.FirstOrDefault(m => m.CompanyId == companyId) ?? pooledMappings.FirstOrDefault(m => m.CompanyId == null);
            if (scopeRow is not null) targets.Add((companyId, scopeRow.AccountId, scopeRow.SegmentCostCenterId));
        }

        var driversCreated = 0;
        var mappingsCreated = 0;
        var sort = 120;
        foreach (var s in Schemes)
        {
            sort++;
            var expenseDriver = await FindDriverAsync(db, tenantId, companyId, s.ExpenseDriverKey, ct);
            if (expenseDriver is null)
            {
                db.GlDrivers.Add(new GlDriver
                {
                    TenantId = tenantId, CompanyId = companyId, Key = s.ExpenseDriverKey, Label = s.AccountName,
                    Category = GlDriverCategories.Balancing, PostingSide = "DR", AccountType = "Expense",
                    DefaultCode = s.AccountCode, DefaultName = s.AccountName,
                    MatchMode = GlDriverMatchModes.Any, IsSystem = false, IsActive = true, SortOrder = sort,
                });
                driversCreated++;
            }
            else if (!expenseDriver.IsActive) expenseDriver.IsActive = true;

            var creditDriver = await FindDriverAsync(db, tenantId, companyId, s.CreditDriverKey, ct);
            if (creditDriver is null)
            {
                db.GlDrivers.Add(new GlDriver
                {
                    TenantId = tenantId, CompanyId = companyId, Key = s.CreditDriverKey, Label = s.CreditLabel,
                    Category = GlDriverCategories.Deduction, PostingSide = "CR", AccountType = pooled.AccountType,
                    DefaultCode = pooled.DefaultCode, DefaultName = pooled.DefaultName,
                    MatchSource = "Statutory", MatchMode = GlDriverMatchModes.Exact, MatchComponentCode = s.Component,
                    EmitsEmployerExpensePair = true, PairedExpenseDriverKey = s.ExpenseDriverKey,
                    IsSystem = false, IsActive = true, SortOrder = sort,
                });
                driversCreated++;
                foreach (var t in targets)
                {
                    var already = await ScopedBypass.TenantWide(db.GlAccountMappings, tenantId, "Idempotency: the preset adds a mapping once per driver and scope.")
                        .AnyAsync(x => x.DriverKey == s.CreditDriverKey && x.CompanyId == t.CompanyId, ct);
                    if (already) continue;
                    db.GlAccountMappings.Add(new GlAccountMapping
                    {
                        TenantId = tenantId, CompanyId = t.CompanyId, DriverKey = s.CreditDriverKey,
                        AccountId = t.AccountId, SegmentCostCenterId = t.SegmentCostCenterId, IsActive = true,
                    });
                    mappingsCreated++;
                }
            }
            else if (!creditDriver.IsActive) creditDriver.IsActive = true;
        }
        await db.SaveChangesAsync(ct);
        return new Result(true, accountsCreated, driversCreated, mappingsCreated, null, null);
    }

    public static async Task<int> DisableAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var keys = Schemes.Select(s => s.CreditDriverKey).ToList();
        var rows = await ScopedBypass.TenantWide(db.GlDrivers, tenantId, "The preset switches off the three routing drivers of one exact scope.")
            .Where(d => d.CompanyId == companyId && keys.Contains(d.Key) && d.IsActive)
            .ToListAsync(ct);
        foreach (var d in rows) { d.IsActive = false; d.UpdatedAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static Task<GlDriver?> FindDriverAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, string key, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.GlDrivers, tenantId, "The preset looks for its own driver rows in one exact scope.")
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Key == key, ct);
}
