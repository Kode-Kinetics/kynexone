using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>
/// Where an offer places the hire: its department and designation, resolved against the
/// organisation's own records when the offer is created.
///
/// <para>WHY. An offer's department and job title were free text that nothing checked. They were
/// copied onto the accepted offer's employee draft, and only activation resolved them, through
/// <see cref="EmployeeOrgFieldResolver"/>, refusing the hire with a 422 when they did not match a
/// record, after the candidate had already accepted. The offer now resolves them with that same
/// resolver (or by record id) and stores each record's own spelling, so the draft carries names
/// activation is certain to match. A name no record matches is refused at offer time, with the fix.</para>
///
/// <para>Ids are not stored: <c>OfferLetter</c> and <c>EmployeeDraft</c> have no department or
/// designation id columns, and adding them needs a migration.</para>
/// </summary>
public static class OfferPlacement
{
    public const string UnresolvedError = "offer_placement_unresolved";
    public const string WrongEntityError = "offer_placement_wrong_entity";

    public sealed record Result(string Department, string Designation, string? Field, string? Message, string? Error = null)
    {
        public bool IsResolved => Field is null;
    }

    /// <summary>
    /// Resolve an offer's placement. An id wins over a name. The department may be empty (the hire has
    /// no department); the designation is required, because an offer is always for a job.
    ///
    /// <para><paramref name="companyId"/> is the legal entity making the offer (the application's).
    /// A department that hangs off another entity's branch is refused: activation would otherwise
    /// place the hire in that entity. When a name matches departments in several entities, the
    /// offering entity's is chosen, and if its name is shared it is stored by its unique code so
    /// activation resolves the same record.</para>
    /// </summary>
    public static async Task<Result> ResolveAsync(
        ZayraDbContext db, Guid tenantId,
        Guid? departmentId, string? department,
        Guid? designationId, string? designation,
        CancellationToken ct,
        Guid? companyId = null)
    {
        var departmentName = string.Empty;
        if (departmentId is not null || !string.IsNullOrWhiteSpace(department))
        {
            var term = department?.Trim() ?? string.Empty;
            // Master data is resolved the way activation resolves it: tenant-wide, whatever the
            // caller's company scope, so the offer and the activation cannot see different records.
            var departments = ScopedBypass.TenantWide(db.Departments, tenantId,
                "Offer placement resolves a department the way activation does, independent of the caller's company scope.");
            var candidates = await departments.AsNoTracking()
                .Where(d => d.IsActive && !d.IsDeleted)
                .Where(d => departmentId != null
                    ? d.Id == departmentId
                    : d.NameEn.ToLower() == term.ToLower() || d.Code.ToUpper() == term.ToUpper())
                .Select(d => new { d.Id, d.NameEn, d.Code, d.BranchId, d.CompanyId })
                .ToListAsync(ct);
            var branchIds = candidates.Where(c => c.BranchId != null).Select(c => c.BranchId!.Value).Distinct().ToList();
            var entityByBranch = branchIds.Count == 0
                ? new Dictionary<Guid, Guid>()
                : await ScopedBypass.TenantWide(db.Branches, tenantId,
                        "The legal entity of each candidate department's branch, whatever the caller's company scope.")
                    .AsNoTracking()
                    .Where(b => branchIds.Contains(b.Id))
                    .ToDictionaryAsync(b => b.Id, b => b.CompanyId, ct);
            Guid? EntityOf(Guid? branchId) =>
                branchId is { } b && entityByBranch.TryGetValue(b, out var entity) ? entity : null;
            if (candidates.Count == 0)
                return departmentId is not null
                    ? Refused("department", "The chosen department is not an active department in your organisation. Pick another one.")
                    : Refused("department",
                        $"'{term}' is not one of your organisation's departments. Pick the department from the list, or add it under Setup first.");

            // Addendum B: a department names its own company; its branch's company is the fallback for an
            // unassigned legacy row.
            var inEntity = companyId is { } offering
                ? candidates.Where(c => (c.CompanyId ?? EntityOf(c.BranchId)) == offering).ToList()
                : candidates;
            var entityNeutral = candidates.Where(c => (c.CompanyId ?? EntityOf(c.BranchId)) is null).ToList();
            var chosen = inEntity.FirstOrDefault() ?? entityNeutral.FirstOrDefault();
            if (chosen is null)
                return new Result(string.Empty, string.Empty, "department",
                    $"'{candidates[0].NameEn}' belongs to another legal entity than the one making this offer. Pick a department of the offering entity.",
                    WrongEntityError);

            // Activation resolves by name or code. A name several departments share would let it pick
            // another entity's record, so a shared name is stored as the chosen record's unique code.
            var nameIsShared = await departments.AsNoTracking()
                .CountAsync(d => d.IsActive && !d.IsDeleted && d.NameEn.ToLower() == chosen.NameEn.ToLower(), ct) > 1;
            departmentName = nameIsShared ? chosen.Code : chosen.NameEn;
        }

        string designationTitle;
        if (designationId is { } desId)
        {
            var title = await db.Designations.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.Id == desId && d.IsActive && !d.IsDeleted)
                .Select(d => d.TitleEn)
                .FirstOrDefaultAsync(ct);
            if (title is null)
                return Refused("designation", "The chosen designation is not an active designation in your organisation. Pick another one.");
            designationTitle = title;
        }
        else if (!string.IsNullOrWhiteSpace(designation))
        {
            try
            {
                designationTitle = (await EmployeeOrgFieldResolver.ResolveDesignationAsync(db, tenantId, designation, ct, companyId)).TitleEn;
            }
            catch (InvalidOperationException)
            {
                return Refused("designation",
                    $"'{designation.Trim()}' is not one of your organisation's designations, so the hire could not be activated. Pick the designation from the list, or add it under Setup first.");
            }
        }
        else
        {
            return Refused("designation", "Choose the designation this offer is for.");
        }

        return new Result(departmentName, designationTitle, null, null);
    }

    private sealed record DepartmentRow(Guid Id, string Code, string NameEn, string NameAr, Guid? CompanyId);

    /// <summary>The department an offer's stored text names: by name first, else by code, the offering company's own row first. Null when nothing
    /// matches or the match is ambiguous.</summary>
    private static async Task<DepartmentRow?> FindStoredDepartmentAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, string? stored, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var term = stored.Trim();
        var rows = await ScopedBypass.TenantWide(db.Departments, tenantId,
                "Display only: the name of the department an offer or hire draft holds, resolved the way activation resolves it, whatever the caller's company scope.")
            .AsNoTracking().Where(d => !d.IsDeleted && d.IsActive && (d.NameEn.ToLower() == term.ToLower() || d.Code.ToUpper() == term.ToUpper()))
            .Select(d => new DepartmentRow(d.Id, d.Code, d.NameEn, d.NameAr, d.CompanyId)).ToListAsync(ct);
        var byName = rows.Where(r => string.Equals(r.NameEn, term, StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = byName.Count > 0 ? byName : rows;
        foreach (var scope in new Func<DepartmentRow, bool>[] { r => companyId != null && r.CompanyId == companyId, r => r.CompanyId == null })
        {
            var inScope = candidates.Where(scope).ToList();
            if (inScope.Count == 1) return inScope[0];
            if (inScope.Count > 1) return null;
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// The department as people read it: its name (the Arabic name for an Arabic letter, when it has one), not the code the offer may hold. An offer
    /// stores the code when a name is shared, so that activation finds the same record; display never changes what is stored. Falls back to the stored
    /// text when no single department matches.
    /// </summary>
    public static async Task<string> DisplayDepartmentAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, string? stored, bool arabic, CancellationToken ct)
    {
        var found = await FindStoredDepartmentAsync(db, tenantId, companyId, stored, ct);
        if (found is null) return stored ?? string.Empty;
        return arabic && !string.IsNullOrWhiteSpace(found.NameAr) ? found.NameAr : found.NameEn;
    }

    /// <summary>
    /// The department text for the hire draft of an accepted offer: the name, but only when activation, which resolves the draft's text by name or
    /// code within the offer's company, would land on the same department. Otherwise the stored text (the code) is kept, so the hire is never placed
    /// in a different department than the one the offer named.
    /// </summary>
    public static async Task<string> DepartmentForHireDraftAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, string? stored, CancellationToken ct)
    {
        var found = await FindStoredDepartmentAsync(db, tenantId, companyId, stored, ct);
        if (found is null) return stored ?? string.Empty;
        var landing = await ScopedBypass.TenantWide(db.Departments, tenantId, "Checks which department the hire draft's text resolves to at activation, whatever the caller's company scope.")
            .AsNoTracking().Where(d => !d.IsDeleted && d.IsActive && (d.NameEn.ToLower() == found.NameEn.ToLower() || d.Code.ToUpper() == found.NameEn.ToUpper()))
            .Where(d => companyId == null || d.CompanyId == companyId || d.CompanyId == null)
            .Select(d => new { d.Id, d.CompanyId }).ToListAsync(ct);
        if (companyId is null && landing.Select(d => d.CompanyId).Where(c => c != null).Distinct().Count() > 1) return stored ?? string.Empty;
        var first = landing.OrderBy(d => d.CompanyId == null ? 1 : 0).ThenBy(d => d.Id).FirstOrDefault();
        return first?.Id == found.Id ? found.NameEn : stored ?? string.Empty;
    }

    private static Result Refused(string field, string message) => new(string.Empty, string.Empty, field, message, UnresolvedError);
}
