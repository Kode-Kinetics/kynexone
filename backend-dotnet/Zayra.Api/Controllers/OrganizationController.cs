using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/organization")]
[Authorize]
public class OrganizationController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;
    private readonly Zayra.Api.Data.ZayraDbContext _db;

    public OrganizationController(IOrganizationSetupService organization, Zayra.Api.Data.ZayraDbContext db)
    {
        _organization = organization;
        _db = db;
    }

    [HttpGet("companies")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<CompanyDto>>> Companies([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        // Same scope as /api/companies: a company-scoped caller lists only the companies they hold.
        var scope = this.GetEntityScope();
        return Ok(await _organization.GetCompaniesAsync(RequireTenant(), page, pageSize, cancellationToken,
            scope.IsGroupLevel ? null : scope.AccessibleCompanyIds));
    }

    [HttpGet("companies/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public Task<ActionResult<CompanyDto>> Company(Guid id, CancellationToken cancellationToken)
        => CompanyEndpoints.GetAsync(this, _organization, id, cancellationToken);

    // Create and update go through the same rules as /api/companies (CompanyEndpoints): a group grant
    // and the company-creation gate to create; the company within the caller's grant to update.
    [HttpPost("companies")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<CompanyDto>> CreateCompany(CompanyRequest request, CancellationToken cancellationToken)
        => CompanyEndpoints.CreateAsync(this, _organization, _db, request, Context(),
            company => Created($"/api/organization/companies/{company.Id}", company), cancellationToken);

    [HttpPut("companies/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<CompanyDto>> UpdateCompany(Guid id, CompanyRequest request, CancellationToken cancellationToken)
        => CompanyEndpoints.UpdateAsync(this, _organization, _db, id, request, Context(), cancellationToken);

    [HttpGet("branches")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<BranchDto>>> Branches([FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        // Same company scoping as /api/branches: a company-scoped caller lists only their companies' branches.
        var scope = this.GetEntityScope();
        return Ok(await _organization.GetBranchesAsync(RequireTenant(), companyId, page, pageSize, cancellationToken,
            scope.IsGroupLevel ? null : scope.AccessibleCompanyIds));
    }

    [HttpPost("branches")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<BranchDto>> CreateBranch(BranchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Same rule as POST /api/branches: the company must be within the caller's grant.
            if (!OrgWriteRules.MayWriteCompany(this, request.CompanyId)) return Forbid();
            var branch = await _organization.CreateBranchAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/organization/branches/{branch.Id}", branch);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("departments")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<DepartmentDto>>> Departments([FromQuery] Guid? branchId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetDepartmentsAsync(RequireTenant(), branchId, page, pageSize, cancellationToken));

    // Department, designation and grade writes need a group grant on this door exactly as on their
    // dedicated controllers (OrgWriteRules); roles match the dedicated controllers too.
    [HttpPost("departments")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<DepartmentDto>> CreateDepartment(DepartmentRequest request, CancellationToken cancellationToken)
        => OrgWriteRules.GroupWriteAsync<DepartmentDto>(this,
            tenantId => _organization.CreateDepartmentAsync(tenantId, request, Context(), cancellationToken)!,
            department => Created($"/api/organization/departments/{department.Id}", department));

    [HttpGet("designations")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<DesignationDto>>> Designations([FromQuery] Guid? departmentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetDesignationsAsync(RequireTenant(), departmentId, page, pageSize, cancellationToken));

    [HttpPost("designations")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<DesignationDto>> CreateDesignation(DesignationRequest request, CancellationToken cancellationToken)
        => OrgWriteRules.GroupWriteAsync<DesignationDto>(this,
            tenantId => _organization.CreateDesignationAsync(tenantId, request, Context(), cancellationToken)!,
            designation => Created($"/api/organization/designations/{designation.Id}", designation));

    [HttpGet("grades")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<GradeDto>>> Grades([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetGradesAsync(RequireTenant(), page, pageSize, cancellationToken));

    [HttpPost("grades")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<GradeDto>> CreateGrade(GradeRequest request, CancellationToken cancellationToken)
        => OrgWriteRules.GroupWriteAsync<GradeDto>(this,
            tenantId => _organization.CreateGradeAsync(tenantId, request, Context(), cancellationToken)!,
            grade => Created($"/api/organization/grades/{grade.Id}", grade));

    [HttpGet("cost-centers")]
    [HasPermission("organization.read", "finance.gl.read")]
    public async Task<ActionResult<PagedResult<CostCenterDto>>> CostCenters([FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetCostCentersAsync(RequireTenant(), companyId, page, pageSize, cancellationToken));

    [HttpPost("cost-centers")]
    [Authorize(Roles = "Admin,HR Manager")]
    public Task<ActionResult<CostCenterDto>> CreateCostCenter(CostCenterRequest request, CancellationToken cancellationToken)
        => CostCenterEndpoints.CreateAsync(this, _organization, request, Context(),
            costCenter => Created($"/api/organization/cost-centers/{costCenter.Id}", costCenter), cancellationToken);

    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant claim missing.");
    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), RequireTenant());
}
