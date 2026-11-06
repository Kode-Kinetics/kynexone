using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.Employees;

/// <inheritdoc cref="IEmployeeCostCentrePreviewService"/>
public sealed class EmployeeCostCentrePreviewService(ZayraDbContext db) : IEmployeeCostCentrePreviewService
{
    public Task<CostCentrePreviewSubject?> FindAsync(Guid tenantId, int employeeId, CancellationToken ct) =>
        ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Cost-centre preview: the employee's company is resolved before the ambient company filter can hide it; the caller's data and company scope checks follow in the controller.")
            .AsNoTracking()
            .Where(e => e.Id == employeeId && !e.IsDeleted)
            .Select(e => new CostCentrePreviewSubject(e.CompanyId, e.DepartmentId, e.PositionId))
            .FirstOrDefaultAsync(ct);

    public async Task<(CostCentrePreviewRefusal Refusal, EmployeeCostCentrePreviewDto? Preview)> PreviewAsync(Guid tenantId, CostCentrePreviewSubject subject,
        Guid? departmentId, Guid? positionId, Guid? costCenterId, CancellationToken ct)
    {
        if (departmentId is Guid department && department != subject.DepartmentId
            && !await ScopedBypass.TenantWide(db.Departments, tenantId,
                    "Cost-centre preview: a department is checked against the EMPLOYEE's company (explicit below), not the caller's switcher selection.")
                .AsNoTracking()
                .AnyAsync(d => d.Id == department && !d.IsDeleted && d.IsActive && (d.CompanyId == subject.CompanyId || d.CompanyId == null), ct))
            return (CostCentrePreviewRefusal.DepartmentNotFound, null);
        if (positionId is Guid position && position != subject.PositionId
            && !await ScopedBypass.TenantWide(db.Positions, tenantId,
                    "Cost-centre preview: a position is checked against the EMPLOYEE's company (explicit below), not the caller's switcher selection.")
                .AsNoTracking()
                .AnyAsync(p => p.Id == position && !p.IsDeleted && (p.CompanyId == subject.CompanyId || p.CompanyId == null), ct))
            return (CostCentrePreviewRefusal.PositionNotFound, null);

        var isOverride = costCenterId is not null;
        if (subject.CompanyId is not Guid companyId)
            return (CostCentrePreviewRefusal.None, new EmployeeCostCentrePreviewDto(null, null, null, null, "None", null, null,
                isOverride ? "employee" : null, isOverride, isOverride ? "employee_cost_centre_invalid" : null));

        // Omitted department/position = the current one; a cost centre = evaluated as the person's own override; none = what they inherit.
        var view = await EmployeeCostCentreView.DescribeAsync(db, tenantId, companyId,
            new ResolverEmployee(departmentId ?? subject.DepartmentId, positionId ?? subject.PositionId, costCenterId, isOverride), ct);
        return (CostCentrePreviewRefusal.None, new EmployeeCostCentrePreviewDto(view.CostCentreId, view.Code, view.NameEn, view.NameAr, view.Source,
            view.SourceName, view.SourceNameAr, view.Level, isOverride, view.InvalidReason));
    }
}
