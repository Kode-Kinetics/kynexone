using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Organization;

public interface IOrganizationSetupService
{
    Task<PagedResult<CompanyDto>> GetCompaniesAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);
    Task<CompanyDto?> GetCompanyAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<CompanyDto> CreateCompanyAsync(Guid tenantId, CompanyRequest request, RequestContext context, CancellationToken cancellationToken, bool asDraft = false);
    Task<CompanyDto?> UpdateCompanyAsync(Guid tenantId, Guid id, CompanyRequest request, RequestContext context, CancellationToken cancellationToken);

    Task<PagedResult<BranchDto>> GetBranchesAsync(Guid tenantId, Guid? companyId, int page, int pageSize, CancellationToken cancellationToken);
    Task<BranchDto?> GetBranchAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<BranchDto> CreateBranchAsync(Guid tenantId, BranchRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<BranchDto?> UpdateBranchAsync(Guid tenantId, Guid id, BranchRequest request, RequestContext context, CancellationToken cancellationToken);

    Task<PagedResult<DepartmentDto>> GetDepartmentsAsync(Guid tenantId, Guid? branchId, int page, int pageSize, CancellationToken cancellationToken);
    Task<DepartmentDto?> GetDepartmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<DepartmentDto> CreateDepartmentAsync(Guid tenantId, DepartmentRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<DepartmentDto?> UpdateDepartmentAsync(Guid tenantId, Guid id, DepartmentRequest request, RequestContext context, CancellationToken cancellationToken);

    Task<PagedResult<DesignationDto>> GetDesignationsAsync(Guid tenantId, Guid? departmentId, int page, int pageSize, CancellationToken cancellationToken);
    Task<DesignationDto?> GetDesignationAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<DesignationDto> CreateDesignationAsync(Guid tenantId, DesignationRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<DesignationDto?> UpdateDesignationAsync(Guid tenantId, Guid id, DesignationRequest request, RequestContext context, CancellationToken cancellationToken);

    Task<PagedResult<GradeDto>> GetGradesAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);
    Task<GradeDto?> GetGradeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<GradeDto> CreateGradeAsync(Guid tenantId, GradeRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<GradeDto?> UpdateGradeAsync(Guid tenantId, Guid id, GradeRequest request, RequestContext context, CancellationToken cancellationToken);

    Task<PagedResult<CostCenterDto>> GetCostCentersAsync(Guid tenantId, Guid? companyId, int page, int pageSize, CancellationToken cancellationToken);
    Task<CostCenterDto?> GetCostCenterAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<CostCenterDto> CreateCostCenterAsync(Guid tenantId, CostCenterRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<CostCenterDto?> UpdateCostCenterAsync(Guid tenantId, Guid id, CostCenterRequest request, RequestContext context, CancellationToken cancellationToken);

    Task<bool> DeleteCompanyAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeleteBranchAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeleteDepartmentAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeleteDesignationAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeleteGradeAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeleteCostCenterAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken);
}
