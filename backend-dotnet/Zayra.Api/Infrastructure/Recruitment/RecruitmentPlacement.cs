using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>
/// The placement rule openings and requisitions share: department, designation and grade are the organisation's own records, so each id
/// given must be an ACTIVE record of this tenant, and the department name and designation title come from that record, not typed text.
/// </summary>
public static class RecruitmentPlacement
{
    public sealed record Result(Department? Department, Designation? Designation, string? Error);

    /// <summary>Resolves the ids given (null ones are skipped). <see cref="Result.Error"/> is the 400 message when one is not an active record.</summary>
    public static async Task<Result> ResolveAsync(ZayraDbContext db, Guid tenantId, Guid? departmentId, Guid? designationId, Guid? gradeId, CancellationToken ct)
    {
        var department = departmentId is Guid deptId
            ? await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deptId && d.TenantId == tenantId && d.IsActive && !d.IsDeleted, ct) : null;
        if (departmentId is not null && department is null)
            return new(null, null, "The chosen department is not an active department in your organisation.");
        var designation = designationId is Guid desigId
            ? await db.Designations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == desigId && d.TenantId == tenantId && d.IsActive && !d.IsDeleted, ct) : null;
        if (designationId is not null && designation is null)
            return new(null, null, "The chosen designation is not an active designation in your organisation.");
        if (gradeId is Guid gid && !await db.Grades.AsNoTracking().AnyAsync(g => g.Id == gid && g.TenantId == tenantId && g.IsActive && !g.IsDeleted, ct))
            return new(null, null, "The chosen grade is not an active grade in your organisation.");
        return new(department, designation, null);
    }
}
