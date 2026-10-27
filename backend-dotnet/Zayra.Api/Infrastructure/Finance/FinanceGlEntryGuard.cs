using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// The ledger-line guard that runs inside <c>ZayraDbContext.SaveChanges*</c> (AM-11), beside the audit-log append-only
/// check, so it cannot be left out by a context that was built without the DI interceptors (tests, services that create
/// their own options, the migrate-only entry point).
/// <para>For every <c>FinanceGlEntry</c> being ADDED it enforces:</para>
/// <list type="bullet">
/// <item>the row has a company (a null-company posting is never new data);</item>
/// <item>a reversal row points at an existing, same-tenant, same-company original that is not itself a reversal and not
/// already reversed, is side-swapped, in the same currency, carries the SAME cost centre (id and snapshots), and the
/// contras of one original never add up to more than the original;</item>
/// <item>for any other row of a company whose cost-accounting switch is ON: the cost centre (if any) is an active, live
/// cost centre of that company; at most one side of the row is profit-and-loss; the profit-and-loss side obeys its account
/// rule (Required needs a cost centre, NotAllowed refuses one); a row with no profit-and-loss side carries none.</item>
/// </list>
/// <para>A MODIFIED row may not change its cost-centre snapshots or posting key. Reversals are exempt from the rule and
/// inactivity checks, so a later rule change or a deactivated cost centre can never block a void.</para>
/// <para>Reads are batched per SaveChanges (companies, accounts, cost centres, originals, existing contras), no-tracking and
/// tenant-pinned, so a Lock with thousands of lines costs a handful of queries, not one per line. A save with no ledger rows
/// costs nothing.</para>
/// </summary>
internal static class FinanceGlEntryGuard
{
    private sealed record Acct(string Type, string Rule);

    // Types of the built-in driver catalog, for a tenant whose chart does not carry the code (rule Optional).
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CatalogTypes = new(() =>
        PayrollGlCatalogTypes());

    private static IReadOnlyDictionary<string, string> PayrollGlCatalogTypes()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in Zayra.Api.Models.PayrollGlCatalog.Drivers) d.TryAdd(x.DefaultCode, x.AccountType);
        return d;
    }

    public static async Task EnforceAsync(ZayraDbContext db, bool sync, CancellationToken ct)
    {
        var entries = db.ChangeTracker.Entries<FinanceGlEntry>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        if (entries.Count == 0) return;

        foreach (var e in entries.Where(e => e.State == EntityState.Modified))
            if (new[] { nameof(FinanceGlEntry.CostCenterId), nameof(FinanceGlEntry.CostCenterCode), nameof(FinanceGlEntry.CostCenterName),
                        nameof(FinanceGlEntry.CostCenterGlSegment), nameof(FinanceGlEntry.PostingKey) }.Any(n => e.Property(n).IsModified))
                throw new GlCostCentreRuleException("finance_gl_entry_cost_centre_immutable",
                    "A posted ledger line's cost centre and posting key cannot be changed. Post a reversal instead.");

        var added = entries.Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (added.Count == 0) return;

        foreach (var tenant in added.GroupBy(r => r.TenantId))
            await EnforceTenantAsync(db, tenant.Key, tenant.ToList(), sync, ct);
    }

    private static async Task EnforceTenantAsync(ZayraDbContext db, Guid tid, List<FinanceGlEntry> rows, bool sync, CancellationToken ct)
    {
        var reversals = rows.Where(r => r.ReversalOfEntryId != null).ToList();
        if (reversals.Count > 0) await ValidateReversalsAsync(db, tid, rows, reversals, sync, ct);

        // A new line must name its legal entity once the tenant uses cost accounting: an unattributed line would otherwise skip every
        // Required / NotAllowed rule. (A tenant that has never switched costing on keeps posting unattributed lines exactly as before;
        // a reversal is checked against its original instead, so a legacy null-company line can still be voided.)
        if (rows.Any(r => r.CompanyId is null && r.ReversalOfEntryId is null))
        {
            var anyCosting = await ToList(ScopedBypass.TenantWide(db.Companies, tid, "Does any company of the tenant have cost accounting on; tenant-pinned and read-only.")
                .AsNoTracking().Where(c => c.CostAccountingEnabled).Select(c => c.Id).Take(1), sync, ct);
            if (anyCosting.Count > 0)
                throw new GlCostCentreRuleException("finance_gl_entry_company_required",
                    "A ledger line must belong to a company. Post it against the legal entity it belongs to.");
        }

        var forward = rows.Where(r => r.ReversalOfEntryId == null && r.CompanyId != null).ToList();
        if (forward.Count == 0) return;

        var companyIds = forward.Select(r => r.CompanyId!.Value).Distinct().ToList();
        var costing = (await ToList(ScopedBypass.TenantWide(db.Companies, tid, "Whether the row's company has cost accounting on; read-only, tenant-pinned, regardless of the caller's company claims.")
                .AsNoTracking().Where(c => companyIds.Contains(c.Id)).Select(c => new { c.Id, c.CostAccountingEnabled }), sync, ct))
            .ToDictionary(c => c.Id, c => c.CostAccountingEnabled);
        var costed = forward.Where(r => costing.TryGetValue(r.CompanyId!.Value, out var on) && on).ToList();
        if (costed.Count == 0) return;

        var codes = costed.SelectMany(r => new[] { CodeOf(r.DebitAccount), CodeOf(r.CreditAccount) }).Where(c => c.Length > 0).Distinct().ToList();
        var costedCompanies = costed.Select(r => r.CompanyId!.Value).Distinct().ToList();
        var accountRows = await ToList(ScopedBypass.TenantWide(db.GlAccounts, tid, "Account type and cost-centre rule for the lines being posted; tenant-pinned and read-only.")
            .AsNoTracking().Where(a => codes.Contains(a.Code) && (a.CompanyId == null || costedCompanies.Contains(a.CompanyId.Value)))
            .Select(a => new { a.Code, a.CompanyId, a.IsActive, a.AccountType, a.CostCenterRule }), sync, ct);

        var ccIds = costed.Where(r => r.CostCenterId != null).Select(r => r.CostCenterId!.Value).Distinct().ToList();
        var ccRows = await ToList(ScopedBypass.TenantWide(db.CostCenters, tid, "Cost centres stamped on the lines being posted; tenant-pinned and read-only.")
            .AsNoTracking().Where(c => ccIds.Contains(c.Id)).Select(c => new { c.Id, c.CompanyId, c.Code, c.IsActive, c.IsDeleted }), sync, ct);
        var ccById = ccRows.ToDictionary(c => c.Id);

        Acct? Lookup(Guid companyId, string label)
        {
            var code = CodeOf(label);
            if (code.Length == 0) return null;
            var a = accountRows.Where(x => x.Code == code && (x.CompanyId == companyId || x.CompanyId == null))
                .OrderByDescending(x => x.CompanyId != null).ThenByDescending(x => x.IsActive).FirstOrDefault();
            if (a is not null) return new Acct(a.AccountType, a.CostCenterRule);
            return CatalogTypes.Value.TryGetValue(code, out var t) ? new Acct(t, "Optional") : null;
        }

        foreach (var r in costed)
        {
            var companyId = r.CompanyId!.Value;
            if (r.CostCenterId is { } ccId)
            {
                if (!ccById.TryGetValue(ccId, out var cc) || cc.IsDeleted || !cc.IsActive || cc.CompanyId != companyId
                    || (r.CostCenterCode is not null && !string.Equals(r.CostCenterCode, cc.Code, StringComparison.Ordinal)))
                    throw new GlCostCentreRuleException("cost_centre_invalid",
                        "The cost centre on this ledger line is inactive, deleted or does not belong to the posting company.");
            }

            var debit = Lookup(companyId, r.DebitAccount);
            var credit = Lookup(companyId, r.CreditAccount);
            var debitPnl = debit is not null && IsPnl(debit.Type);
            var creditPnl = credit is not null && IsPnl(credit.Type);
            if (debitPnl && creditPnl)
                throw new GlCostCentreRuleException("cost_centre_both_sides_pnl",
                    "A ledger line cannot have profit-and-loss accounts on both sides when cost accounting is on. Split it into two lines.");

            var pnl = debitPnl ? debit : creditPnl ? credit : null;
            var pnlLabel = debitPnl ? r.DebitAccount : r.CreditAccount;
            if (r.CostCenterId is not null)
            {
                if (pnl is null || pnl.Rule == "NotAllowed")
                    throw new GlCostCentreRuleException("cost_centre_not_allowed",
                        pnl is null ? "A cost centre can only be stamped on a line that has an expense or income account."
                                    : $"Account {pnlLabel} does not accept a cost centre. Remove it from this posting rule or change the account's cost centre rule.");
            }
            else if (pnl is { Rule: "Required" })
                throw new GlCostCentreRuleException("cost_centre_required",
                    $"Account {pnlLabel} requires a cost centre. Assign one to the employee, their position or department, or set a company default cost centre.");
        }
    }

    private static async Task ValidateReversalsAsync(ZayraDbContext db, Guid tid, List<FinanceGlEntry> batch, List<FinanceGlEntry> reversals, bool sync, CancellationToken ct)
    {
        var ids = reversals.Select(r => r.ReversalOfEntryId!.Value).Distinct().ToList();

        // Originals: the unit of work's own tracked rows first (a void flags the original reversed in the same save), then the database.
        var tracked = db.ChangeTracker.Entries<FinanceGlEntry>().Where(e => e.State != EntityState.Deleted && ids.Contains(e.Entity.Id)).ToDictionary(e => e.Entity.Id);
        var missing = ids.Where(i => !tracked.ContainsKey(i)).ToList();
        var stored = await ToList(ScopedBypass.TenantWide(db.FinanceGlEntries, tid, "Originals of the reversals being posted; tenant-pinned and read-only.")
            .AsNoTracking().Where(e => missing.Contains(e.Id)), sync, ct);
        var storedById = stored.ToDictionary(e => e.Id);

        // Contras already stored against these originals (partial reversals are legitimate; the sum is what is bounded).
        var priorContras = (await ToList(ScopedBypass.TenantWide(db.FinanceGlEntries, tid, "Contras already posted against the originals; tenant-pinned and read-only.")
                .AsNoTracking().Where(e => e.ReversalOfEntryId != null && ids.Contains(e.ReversalOfEntryId.Value)).Select(e => new { Origin = e.ReversalOfEntryId!.Value, e.Amount }), sync, ct))
            .GroupBy(x => x.Origin).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        foreach (var r in reversals)
        {
            var id = r.ReversalOfEntryId!.Value;
            FinanceGlEntry? orig; bool persistedReversed;
            if (tracked.TryGetValue(id, out var te))
            {
                orig = te.Entity;
                persistedReversed = te.State switch
                {
                    EntityState.Added => false,
                    EntityState.Modified => te.OriginalValues.GetValue<bool>(nameof(FinanceGlEntry.IsReversed)),
                    _ => te.Entity.IsReversed,
                };
            }
            else if (storedById.TryGetValue(id, out var se)) { orig = se; persistedReversed = se.IsReversed; }
            else throw Bad("The entry being reversed does not exist.");

            if (orig.TenantId != r.TenantId || orig.CompanyId != r.CompanyId) throw Bad("A reversal must belong to the same company as the entry it reverses.");
            if (orig.ReversalOfEntryId != null) throw Bad("A reversal cannot itself be reversed.");
            if (persistedReversed) throw Bad("The entry has already been reversed.");
            if (!string.Equals(orig.Currency, r.Currency, StringComparison.Ordinal)) throw Bad("A reversal must be in the same currency as the entry it reverses.");
            if (r.Amount < 0m || r.Amount > orig.Amount) throw Bad("A reversal cannot exceed the amount of the entry it reverses.");
            var swapped = (r.DebitAccount.Length == 0 || r.DebitAccount == orig.CreditAccount)
                          && (r.CreditAccount.Length == 0 || r.CreditAccount == orig.DebitAccount)
                          && (r.DebitAccount.Length > 0 || r.CreditAccount.Length > 0);
            if (!swapped) throw Bad("A reversal must post the opposite side of the entry it reverses.");
            if (r.CostCenterId != orig.CostCenterId || r.CostCenterCode != orig.CostCenterCode
                || r.CostCenterName != orig.CostCenterName || r.CostCenterGlSegment != orig.CostCenterGlSegment)
                throw Bad("A reversal must carry exactly the cost centre of the entry it reverses.");

            var total = (priorContras.TryGetValue(id, out var prior) ? prior : 0m)
                        + batch.Where(b => b.ReversalOfEntryId == id).Sum(b => b.Amount);
            if (total > orig.Amount) throw Bad("The reversals of an entry cannot add up to more than the entry itself.");
        }

        static GlCostCentreRuleException Bad(string message) => new("reversal_invalid", message);
    }

    private static async Task<List<T>> ToList<T>(IQueryable<T> q, bool sync, CancellationToken ct)
        => sync ? q.ToList() : await q.ToListAsync(ct);

    private static string CodeOf(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var i = label.IndexOf(" - ", StringComparison.Ordinal);
        return (i < 0 ? label : label[..i]).Trim();
    }

    /// <summary>Profit and loss for every name the chart and imports use (the chart calls revenue "Income").</summary>
    internal static bool IsPnl(string type) =>
        string.Equals(type, "Expense", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Income", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "Revenue", StringComparison.OrdinalIgnoreCase);
}
