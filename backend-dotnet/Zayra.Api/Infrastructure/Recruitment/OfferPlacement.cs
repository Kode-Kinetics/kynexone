using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;

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
            // IgnoreQueryFilters is intentional: master-data resolution must not vary with the caller's
            // company scope (the same read activation makes); tenant, activity and deletion are explicit.
            var candidates = await db.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.IsActive && !d.IsDeleted)
                .Where(d => departmentId != null
                    ? d.Id == departmentId
                    : d.NameEn.ToLower() == term.ToLower() || d.Code.ToUpper() == term.ToUpper())
                .Select(d => new
                {
                    d.Id, d.NameEn, d.Code,
                    CompanyId = db.Branches.IgnoreQueryFilters()
                        .Where(b => b.TenantId == tenantId && b.Id == d.BranchId)
                        .Select(b => (Guid?)b.CompanyId)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct);
            if (candidates.Count == 0)
                return departmentId is not null
                    ? Refused("department", "The chosen department is not an active department in your organisation. Pick another one.")
                    : Refused("department",
                        $"'{term}' is not one of your organisation's departments. Pick the department from the list, or add it under Setup first.");

            var inEntity = companyId is { } offering
                ? candidates.Where(c => c.CompanyId == offering).ToList()
                : candidates;
            var entityNeutral = candidates.Where(c => c.CompanyId is null).ToList();
            var chosen = inEntity.FirstOrDefault() ?? entityNeutral.FirstOrDefault();
            if (chosen is null)
                return new Result(string.Empty, string.Empty, "department",
                    $"'{candidates[0].NameEn}' belongs to another legal entity than the one making this offer. Pick a department of the offering entity.",
                    WrongEntityError);

            // Activation resolves by name or code. A name several departments share would let it pick
            // another entity's record, so a shared name is stored as the chosen record's unique code.
            var nameIsShared = await db.Departments.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(d => d.TenantId == tenantId && d.IsActive && !d.IsDeleted
                    && d.NameEn.ToLower() == chosen.NameEn.ToLower(), ct) > 1;
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
                designationTitle = (await EmployeeOrgFieldResolver.ResolveDesignationAsync(db, tenantId, designation, ct)).TitleEn;
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

    private static Result Refused(string field, string message) => new(string.Empty, string.Empty, field, message, UnresolvedError);
}
