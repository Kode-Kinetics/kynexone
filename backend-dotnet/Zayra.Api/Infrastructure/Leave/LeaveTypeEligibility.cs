using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// Whether a leave type is meant for an employee, by gender. One rule for the self-service list and for every submission:
/// <list type="bullet">
/// <item>a type that classifies as maternity is for women, paternity for men (by code, name or category, any country);</item>
/// <item>a type whose active policies for the employee's company ALL restrict gender to another gender is not for them.
/// No policy, or any policy with no gender restriction or the employee's gender, keeps it open.</item>
/// </list>
/// An employee whose gender is blank or not male/female on the tenant's list is never refused anything here.
/// Genders are compared through the tenant's reference list (code or label, any case), as policy eligibility does.
/// </summary>
public static class LeaveTypeEligibility
{
    public static async Task<HashSet<Guid>> IneligibleTypeIdsAsync(
        ZayraDbContext db, Guid tenantId, string? employeeGender, Guid? employeeCompanyId, IReadOnlyCollection<LeaveType> types, CancellationToken ct)
    {
        if (types.Count == 0 || string.IsNullOrWhiteSpace(employeeGender)) return [];
        var genders = await ReferenceList.LoadAsync(db, tenantId, ReferenceList.GenderType, ct);
        var isMale = genders.Same(employeeGender, ReferenceList.Male);
        var isFemale = !isMale && genders.Same(employeeGender, ReferenceList.Female);
        if (!isMale && !isFemale) return [];

        var ids = types.Select(t => t.Id).ToList();
        var policyGenders = (await db.LeavePolicies.AsNoTracking()
                .Where(p => p.TenantId == tenantId && ids.Contains(p.LeaveTypeId) && p.Status == "Active"
                    && (p.CompanyId == null || p.CompanyId == employeeCompanyId))
                .Select(p => new { p.LeaveTypeId, p.Gender })
                .ToListAsync(ct))
            .GroupBy(p => p.LeaveTypeId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Gender).ToList());

        var refused = new HashSet<Guid>();
        foreach (var type in types)
        {
            var kind = KsaStatutorySpecialLeave.Classify(type.Code, type.NameEn, type.Category);
            if ((kind == KsaStatutoryLeaveKind.Maternity && isMale) || (kind == KsaStatutoryLeaveKind.Paternity && isFemale))
            {
                refused.Add(type.Id);
                continue;
            }
            if (policyGenders.TryGetValue(type.Id, out var restricted) && restricted.Count > 0
                && restricted.All(g => !string.IsNullOrWhiteSpace(g) && !genders.Same(g, employeeGender)))
                refused.Add(type.Id);
        }
        return refused;
    }

    public static async Task<bool> IsEligibleAsync(ZayraDbContext db, Guid tenantId, LeaveType type, Employee employee, CancellationToken ct) =>
        !(await IneligibleTypeIdsAsync(db, tenantId, employee.Gender, employee.CompanyId, [type], ct)).Contains(type.Id);
}
