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

    public sealed record Result(string Department, string Designation, string? Field, string? Message)
    {
        public bool IsResolved => Field is null;
    }

    /// <summary>
    /// Resolve an offer's placement. An id wins over a name. The department may be empty (the hire has
    /// no department); the designation is required, because an offer is always for a job.
    /// </summary>
    public static async Task<Result> ResolveAsync(
        ZayraDbContext db, Guid tenantId,
        Guid? departmentId, string? department,
        Guid? designationId, string? designation,
        CancellationToken ct)
    {
        var departmentName = string.Empty;
        if (departmentId is { } depId)
        {
            var name = await db.Departments.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.Id == depId && d.IsActive && !d.IsDeleted)
                .Select(d => d.NameEn)
                .FirstOrDefaultAsync(ct);
            if (name is null)
                return Refused("department", "The chosen department is not an active department in your organisation. Pick another one.");
            departmentName = name;
        }
        else if (!string.IsNullOrWhiteSpace(department))
        {
            try
            {
                departmentName = (await EmployeeOrgFieldResolver.ResolveDepartmentAsync(db, tenantId, department, ct)).NameEn;
            }
            catch (InvalidOperationException)
            {
                return Refused("department",
                    $"'{department.Trim()}' is not one of your organisation's departments. Pick the department from the list, or add it under Setup first.");
            }
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

    private static Result Refused(string field, string message) => new(string.Empty, string.Empty, field, message);
}
