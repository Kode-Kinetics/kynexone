using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/organization")]
[Authorize]
public class OrganizationController : ControllerBase
{
    private readonly IOrganizationSetupService _organization;

    public OrganizationController(IOrganizationSetupService organization)
    {
        _organization = organization;
    }

    [HttpGet("companies")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<CompanyDto>>> Companies([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetCompaniesAsync(RequireTenant(), page, pageSize, cancellationToken));

    [HttpGet("companies/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<CompanyDto>> Company(Guid id, CancellationToken cancellationToken)
        => await _organization.GetCompanyAsync(RequireTenant(), id, cancellationToken) is { } company ? Ok(company) : NotFound();

    [HttpPost("companies")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<CompanyDto>> CreateCompany(CompanyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var company = await _organization.CreateCompanyAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/organization/companies/{company.Id}", company);
        }
        catch (Zayra.Api.Infrastructure.Organization.EmailDomainRefusedException ex) { return StatusCode(ex.Status, new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("companies/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<CompanyDto>> UpdateCompany(Guid id, CompanyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _organization.UpdateCompanyAsync(RequireTenant(), id, request, Context(), cancellationToken) is { } company ? Ok(company) : NotFound();
        }
        catch (Zayra.Api.Infrastructure.Organization.EmailDomainRefusedException ex) { return StatusCode(ex.Status, new { code = ex.Code, message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("branches")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<BranchDto>>> Branches([FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetBranchesAsync(RequireTenant(), companyId, page, pageSize, cancellationToken));

    [HttpPost("branches")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<BranchDto>> CreateBranch(BranchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var branch = await _organization.CreateBranchAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/organization/branches/{branch.Id}", branch);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("departments")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<DepartmentDto>>> Departments([FromQuery] Guid? branchId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetDepartmentsAsync(RequireTenant(), branchId, page, pageSize, cancellationToken));

    [HttpPost("departments")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<DepartmentDto>> CreateDepartment(DepartmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var department = await _organization.CreateDepartmentAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/organization/departments/{department.Id}", department);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("designations")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<DesignationDto>>> Designations([FromQuery] Guid? departmentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetDesignationsAsync(RequireTenant(), departmentId, page, pageSize, cancellationToken));

    [HttpPost("designations")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<DesignationDto>> CreateDesignation(DesignationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var designation = await _organization.CreateDesignationAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/organization/designations/{designation.Id}", designation);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("grades")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<GradeDto>>> Grades([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetGradesAsync(RequireTenant(), page, pageSize, cancellationToken));

    [HttpPost("grades")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<GradeDto>> CreateGrade(GradeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var grade = await _organization.CreateGradeAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/organization/grades/{grade.Id}", grade);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("cost-centers")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<CostCenterDto>>> CostCenters([FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        => Ok(await _organization.GetCostCentersAsync(RequireTenant(), companyId, page, pageSize, cancellationToken));

    [HttpPost("cost-centers")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<CostCenterDto>> CreateCostCenter(CostCenterRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var costCenter = await _organization.CreateCostCenterAsync(RequireTenant(), request, Context(), cancellationToken);
            return Created($"/api/organization/cost-centers/{costCenter.Id}", costCenter);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant claim missing.");
    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), RequireTenant());
}
