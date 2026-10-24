using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// Leave types and leave policies belong to one company (owner decision 2026-10-23: companies in a group are separate
/// legal entities). This is the one place that answers "which leave types may this company's employees use" and "which
/// company owns a new row", so every reader — request validation, accrual, encashment, imports, ESS — resolves the same
/// way.
///
/// <para><b>Fallback.</b> A legacy row the per-company backfill could not attribute (<c>company_id</c> NULL, shown in
/// Setup as "Unassigned — choose the company") still matches any company that has no type of the same code, so nothing
/// that worked before the backfill stops working. A company's own row of that code always shadows it.</para>
/// </summary>
public static class LeaveCompanyScope
{
    private const string CompanyJustification =
        "Leave setup resolves the owning company of a new leave type or policy among every live company of the tenant, whatever the caller's company switcher shows.";

    /// <summary>
    /// The leave types a company's employees may use: the company's own, plus unassigned legacy types whose code the
    /// company does not use. A null company (an employee not yet placed in a legal entity) is not narrowed.
    /// Respects the caller's query filters (a company-scoped caller never sees a sister company's rows).
    /// </summary>
    public static IQueryable<LeaveType> UsableBy(ZayraDbContext db, Guid tenantId, Guid? companyId)
    {
        var all = db.LeaveTypes.Where(t => t.TenantId == tenantId);
        if (companyId is not Guid company) return all;
        return all.Where(t => t.CompanyId == company
            || (t.CompanyId == null && !db.LeaveTypes.Any(o => o.TenantId == tenantId && o.CompanyId == company && o.Code.ToUpper() == t.Code.ToUpper())));
    }

    /// <summary>Whether <paramref name="type"/> may be used for an employee of <paramref name="companyId"/>.</summary>
    public static async Task<bool> IsUsableByAsync(ZayraDbContext db, LeaveType type, Guid? companyId, CancellationToken ct)
    {
        if (companyId is not Guid company) return true;
        if (type.CompanyId is Guid owner) return owner == company;
        var code = (type.Code ?? string.Empty).ToUpper();
        return !await db.LeaveTypes.AnyAsync(o => o.TenantId == type.TenantId && o.CompanyId == company && o.Code.ToUpper() == code, ct);
    }

    /// <summary>The message every refusal of a sister company's leave type carries.</summary>
    public const string NotThisCompanysType =
        "This leave type belongs to another company. Choose one of the leave types of the employee's company.";

    /// <summary>
    /// The by-code view of a company: its own rows, plus unassigned legacy rows whose code it does not use (the same rule
    /// as <see cref="UsableBy"/>, for rows already loaded). Keys are upper-cased codes.
    /// </summary>
    public static Dictionary<string, LeaveType> ByCodeFor(IEnumerable<LeaveType> rows, Guid? companyId)
    {
        var list = rows.ToList();
        var result = new Dictionary<string, LeaveType>(StringComparer.Ordinal);
        if (companyId is Guid company)
            foreach (var t in list.Where(t => t.CompanyId == company).OrderBy(t => t.Id))
                result.TryAdd((t.Code ?? string.Empty).Trim().ToUpperInvariant(), t);
        foreach (var t in list.Where(t => t.CompanyId == null).OrderBy(t => t.Id))
            result.TryAdd((t.Code ?? string.Empty).Trim().ToUpperInvariant(), t);
        if (companyId is null)
            foreach (var t in list.Where(t => t.CompanyId != null).OrderBy(t => t.Id))
                result.TryAdd((t.Code ?? string.Empty).Trim().ToUpperInvariant(), t);
        return result;
    }

    /// <summary>
    /// In-memory form of <see cref="IsUsableByAsync"/> for batch work (the accrual job): load once, ask per employee.
    /// </summary>
    public sealed class TypeIndex
    {
        private readonly Dictionary<Guid, (Guid? Company, string Code)> _types;
        private readonly HashSet<(Guid Company, string Code)> _owned;

        private TypeIndex(IEnumerable<(Guid Id, Guid? Company, string Code)> rows)
        {
            _types = rows.ToDictionary(r => r.Id, r => (r.Company, (r.Code ?? string.Empty).ToUpperInvariant()));
            _owned = _types.Values.Where(v => v.Company is not null).Select(v => (v.Company!.Value, v.Code)).ToHashSet();
        }

        public static async Task<TypeIndex> LoadAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
        {
            var rows = await db.LeaveTypes.AsNoTracking().Where(t => t.TenantId == tenantId)
                .Select(t => new { t.Id, t.CompanyId, t.Code }).ToListAsync(ct);
            return new TypeIndex(rows.Select(r => (r.Id, r.CompanyId, r.Code)));
        }

        public bool Usable(Guid leaveTypeId, Guid? companyId)
        {
            if (companyId is not Guid company) return true;
            if (!_types.TryGetValue(leaveTypeId, out var type)) return true;   // not visible here: nothing to narrow on
            if (type.Company is Guid owner) return owner == company;
            return !_owned.Contains((company, type.Code));
        }
    }

    /// <summary>
    /// The company that owns a new leave type or policy: the one the request names (a live company of the tenant), else
    /// the company the request is narrowed to (the company switcher, or a caller holding one company), else the tenant's
    /// only company. A tenant with several companies must say which — <see cref="OrganizationRuleException.CompanyRequired"/>.
    /// </summary>
    public static async Task<Guid> ResolveOwningCompanyAsync(ZayraDbContext db, Guid tenantId, Guid? requested, CancellationToken ct)
    {
        var companies = ScopedBypass.TenantWide(db.Companies, tenantId, CompanyJustification).Where(c => !c.IsDeleted);
        if (requested is Guid id)
        {
            if (!await companies.AnyAsync(c => c.Id == id, ct))
                throw new OrganizationRuleException(OrganizationRuleException.CompanyMismatch, "Company not found in this workspace.");
            return id;
        }
        if (db.SingleScopedCompanyId is Guid selected && await companies.AnyAsync(c => c.Id == selected, ct))
            return selected;
        var ids = await companies.Select(c => c.Id).Take(2).ToListAsync(ct);
        if (ids.Count == 1) return ids[0];
        throw new OrganizationRuleException(OrganizationRuleException.CompanyRequired,
            ids.Count == 0
                ? "Create a company first: leave types and policies belong to a company."
                : "Choose the company this belongs to.");
    }

    /// <summary>
    /// On edit the company is fixed once set. An unassigned legacy row (company NULL) takes the company the request names —
    /// that is how Setup assigns it.
    /// </summary>
    public static async Task<Guid?> ResolveCompanyOnUpdateAsync(ZayraDbContext db, Guid tenantId, Guid? current, Guid? requested, CancellationToken ct)
    {
        if (current is Guid fixedCompany)
        {
            if (requested is Guid other && other != fixedCompany)
                throw new OrganizationRuleException(OrganizationRuleException.CompanyLocked,
                    "The company of this row can't be changed. Create it in the other company instead.");
            return fixedCompany;
        }
        if (requested is Guid assign)
            return await ResolveOwningCompanyAsync(db, tenantId, assign, ct);
        return null;
    }
}
