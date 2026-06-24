using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Organization;

public class OrganizationSetupService : IOrganizationSetupService
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    public OrganizationSetupService(ZayraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<PagedResult<CompanyDto>> GetCompaniesAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Companies.Where(x => x.TenantId == tenantId && !x.IsDeleted).OrderBy(x => x.LegalNameEn);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<CompanyDto>(items, total, page, pageSize);
    }

    public async Task<CompanyDto?> GetCompanyAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        return company?.ToDto();
    }

    public async Task<CompanyDto> CreateCompanyAsync(Guid tenantId, CompanyRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await EnsureCompanyUnique(tenantId, request.RegistrationNumber, null, cancellationToken);
        var company = new Company { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(company, request);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.company_created", nameof(Company), company.Id.ToString(), context, null, cancellationToken);
        return company.ToDto();
    }

    public async Task<CompanyDto?> UpdateCompanyAsync(Guid tenantId, Guid id, CompanyRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (company is null) return null;
        await EnsureCompanyUnique(tenantId, request.RegistrationNumber, id, cancellationToken);
        Apply(company, request);
        company.UpdatedAtUtc = DateTime.UtcNow;
        company.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.company_updated", nameof(Company), company.Id.ToString(), context, null, cancellationToken);
        return company.ToDto();
    }

    public async Task<PagedResult<BranchDto>> GetBranchesAsync(Guid tenantId, Guid? companyId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Branches.Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (companyId.HasValue) query = query.Where(x => x.CompanyId == companyId.Value);
        var ordered = query.OrderBy(x => x.Code);
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<BranchDto>(items, total, page, pageSize);
    }

    public async Task<BranchDto?> GetBranchAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var branch = await _db.Branches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        return branch?.ToDto();
    }

    public async Task<BranchDto> CreateBranchAsync(Guid tenantId, BranchRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await EnsureCompanyExists(tenantId, request.CompanyId, cancellationToken);
        await EnsureBranchCodeUnique(tenantId, request.Code, null, cancellationToken);
        var branch = new Branch { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(branch, request);
        _db.Branches.Add(branch);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.branch_created", nameof(Branch), branch.Id.ToString(), context, null, cancellationToken);
        return branch.ToDto();
    }

    public async Task<BranchDto?> UpdateBranchAsync(Guid tenantId, Guid id, BranchRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var branch = await _db.Branches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (branch is null) return null;
        await EnsureCompanyExists(tenantId, request.CompanyId, cancellationToken);
        await EnsureBranchCodeUnique(tenantId, request.Code, id, cancellationToken);
        Apply(branch, request);
        branch.UpdatedAtUtc = DateTime.UtcNow;
        branch.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.branch_updated", nameof(Branch), branch.Id.ToString(), context, null, cancellationToken);
        return branch.ToDto();
    }

    public async Task<PagedResult<DepartmentDto>> GetDepartmentsAsync(Guid tenantId, Guid? branchId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Departments.Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue) query = query.Where(x => x.BranchId == branchId.Value);
        var ordered = query.OrderBy(x => x.Code);
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<DepartmentDto>(items, total, page, pageSize);
    }

    public async Task<DepartmentDto?> GetDepartmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        return department?.ToDto();
    }

    public async Task<DepartmentDto> CreateDepartmentAsync(Guid tenantId, DepartmentRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await EnsureBranchExists(tenantId, request.BranchId, cancellationToken);
        await EnsureDepartmentExists(tenantId, request.ParentDepartmentId, cancellationToken);
        await EnsureDepartmentCodeUnique(tenantId, request.Code, null, cancellationToken);
        var department = new Department { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(department, request);
        _db.Departments.Add(department);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.department_created", nameof(Department), department.Id.ToString(), context, null, cancellationToken);
        return department.ToDto();
    }

    public async Task<DepartmentDto?> UpdateDepartmentAsync(Guid tenantId, Guid id, DepartmentRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (department is null) return null;
        await EnsureBranchExists(tenantId, request.BranchId, cancellationToken);
        await EnsureDepartmentExists(tenantId, request.ParentDepartmentId, cancellationToken);
        await EnsureDepartmentCodeUnique(tenantId, request.Code, id, cancellationToken);
        Apply(department, request);
        department.UpdatedAtUtc = DateTime.UtcNow;
        department.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.department_updated", nameof(Department), department.Id.ToString(), context, null, cancellationToken);
        return department.ToDto();
    }

    public async Task<PagedResult<DesignationDto>> GetDesignationsAsync(Guid tenantId, Guid? departmentId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Designations.Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
        var ordered = query.OrderBy(x => x.Code);
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<DesignationDto>(items, total, page, pageSize);
    }

    public async Task<DesignationDto?> GetDesignationAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var designation = await _db.Designations.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        return designation?.ToDto();
    }

    public async Task<DesignationDto> CreateDesignationAsync(Guid tenantId, DesignationRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await EnsureDepartmentExists(tenantId, request.DepartmentId, cancellationToken);
        await EnsureDesignationCodeUnique(tenantId, request.Code, null, cancellationToken);
        var designation = new Designation { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(designation, request);
        _db.Designations.Add(designation);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.designation_created", nameof(Designation), designation.Id.ToString(), context, null, cancellationToken);
        return designation.ToDto();
    }

    public async Task<DesignationDto?> UpdateDesignationAsync(Guid tenantId, Guid id, DesignationRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var designation = await _db.Designations.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (designation is null) return null;
        await EnsureDepartmentExists(tenantId, request.DepartmentId, cancellationToken);
        await EnsureDesignationCodeUnique(tenantId, request.Code, id, cancellationToken);
        Apply(designation, request);
        designation.UpdatedAtUtc = DateTime.UtcNow;
        designation.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.designation_updated", nameof(Designation), designation.Id.ToString(), context, null, cancellationToken);
        return designation.ToDto();
    }

    public async Task<PagedResult<GradeDto>> GetGradesAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Grades.Where(x => x.TenantId == tenantId && !x.IsDeleted).OrderBy(x => x.Level).ThenBy(x => x.Code);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<GradeDto>(items, total, page, pageSize);
    }

    public async Task<GradeDto?> GetGradeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var grade = await _db.Grades.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        return grade?.ToDto();
    }

    public async Task<GradeDto> CreateGradeAsync(Guid tenantId, GradeRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await EnsureGradeCodeUnique(tenantId, request.Code, null, cancellationToken);
        var grade = new Grade { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(grade, request);
        _db.Grades.Add(grade);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.grade_created", nameof(Grade), grade.Id.ToString(), context, null, cancellationToken);
        return grade.ToDto();
    }

    public async Task<GradeDto?> UpdateGradeAsync(Guid tenantId, Guid id, GradeRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var grade = await _db.Grades.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (grade is null) return null;
        await EnsureGradeCodeUnique(tenantId, request.Code, id, cancellationToken);
        Apply(grade, request);
        grade.UpdatedAtUtc = DateTime.UtcNow;
        grade.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.grade_updated", nameof(Grade), grade.Id.ToString(), context, null, cancellationToken);
        return grade.ToDto();
    }

    public async Task<PagedResult<CostCenterDto>> GetCostCentersAsync(Guid tenantId, Guid? companyId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.CostCenters.Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (companyId.HasValue) query = query.Where(x => x.CompanyId == companyId.Value);
        var ordered = query.OrderBy(x => x.Code);
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<CostCenterDto>(items, total, page, pageSize);
    }

    public async Task<CostCenterDto?> GetCostCenterAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var costCenter = await _db.CostCenters.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        return costCenter?.ToDto();
    }

    public async Task<CostCenterDto> CreateCostCenterAsync(Guid tenantId, CostCenterRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await EnsureCostCenterCodeUnique(tenantId, request.Code, null, cancellationToken);
        var costCenter = new CostCenter { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(costCenter, request);
        _db.CostCenters.Add(costCenter);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.cost_center_created", nameof(CostCenter), costCenter.Id.ToString(), context, null, cancellationToken);
        return costCenter.ToDto();
    }

    public async Task<CostCenterDto?> UpdateCostCenterAsync(Guid tenantId, Guid id, CostCenterRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var costCenter = await _db.CostCenters.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (costCenter is null) return null;
        await EnsureCostCenterCodeUnique(tenantId, request.Code, id, cancellationToken);
        Apply(costCenter, request);
        costCenter.UpdatedAtUtc = DateTime.UtcNow;
        costCenter.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.cost_center_updated", nameof(CostCenter), costCenter.Id.ToString(), context, null, cancellationToken);
        return costCenter.ToDto();
    }

    public async Task<bool> DeleteCompanyAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters is intentional: counting all active employees in this company regardless of the
        // calling user's company-scope JWT claims — this is a system-level integrity check, not a user query.
        var activeCount = await _db.Employees
            .IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == tenantId && e.CompanyId == id
                             && !e.IsDeleted
                             && e.Status != "Archived" && e.Status != "Terminated" && e.Status != "Exited",
                cancellationToken);
        if (activeCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete company: {activeCount} active employee{(activeCount == 1 ? "" : "s")} still belong to it. " +
                $"Reassign or deactivate all employees before deleting the company.");
        return await SoftDelete(_db.Companies, tenantId, id, "organization.company_deleted", context, cancellationToken);
    }
    public Task<bool> DeleteBranchAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Branches, tenantId, id, "organization.branch_deleted", context, cancellationToken);
    public Task<bool> DeleteDepartmentAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Departments, tenantId, id, "organization.department_deleted", context, cancellationToken);
    public Task<bool> DeleteDesignationAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Designations, tenantId, id, "organization.designation_deleted", context, cancellationToken);
    public Task<bool> DeleteGradeAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Grades, tenantId, id, "organization.grade_deleted", context, cancellationToken);
    public Task<bool> DeleteCostCenterAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.CostCenters, tenantId, id, "organization.cost_center_deleted", context, cancellationToken);

    private static void Apply(Company company, CompanyRequest request)
    {
        company.LegalNameEn = Clean(request.LegalNameEn);
        company.LegalNameAr = Clean(request.LegalNameAr);
        company.TradeName = Clean(request.TradeName);
        company.CountryCode  = Clean(request.CountryCode).ToUpperInvariant();
        company.Jurisdiction = Clean(request.Jurisdiction);
        company.RegistrationNumber = Clean(request.RegistrationNumber);
        company.TaxNumber = Clean(request.TaxNumber);
        company.WpsEmployerId = Clean(request.WpsEmployerId);
        company.GosiEmployerId = Clean(request.GosiEmployerId);
        company.QiwaEstablishmentId = Clean(request.QiwaEstablishmentId);
        company.DefaultCurrency = Clean(request.DefaultCurrency).ToUpperInvariant();
        company.IsActive = request.IsActive;
    }

    private static void Apply(Branch branch, BranchRequest request)
    {
        branch.CompanyId = request.CompanyId;
        branch.Code = Clean(request.Code).ToUpperInvariant();
        branch.NameEn = Clean(request.NameEn);
        branch.NameAr = Clean(request.NameAr);
        branch.CountryCode = Clean(request.CountryCode).ToUpperInvariant();
        branch.City = Clean(request.City);
        branch.AddressLine1 = Clean(request.AddressLine1);
        branch.AddressLine2 = Clean(request.AddressLine2);
        branch.TimeZoneId = Clean(request.TimeZoneId);
        branch.LaborOfficeCode = Clean(request.LaborOfficeCode);
        branch.IsHeadOffice = request.IsHeadOffice;
        branch.IsActive = request.IsActive;
    }

    private static void Apply(Department department, DepartmentRequest request)
    {
        department.BranchId = request.BranchId;
        department.ParentDepartmentId = request.ParentDepartmentId;
        department.CostCenterId = request.CostCenterId;
        department.Code = Clean(request.Code).ToUpperInvariant();
        department.NameEn = Clean(request.NameEn);
        department.NameAr = Clean(request.NameAr);
        department.ManagerEmployeeId = request.ManagerEmployeeId;
        department.IsActive = request.IsActive;
    }

    private static void Apply(Designation designation, DesignationRequest request)
    {
        designation.DepartmentId = request.DepartmentId;
        designation.Code = Clean(request.Code).ToUpperInvariant();
        designation.TitleEn = Clean(request.TitleEn);
        designation.TitleAr = Clean(request.TitleAr);
        designation.JobGrade = Clean(request.JobGrade);
        designation.GradeId = request.GradeId;
        designation.JobLevel = Clean(request.JobLevel);
        designation.JobDescription = Clean(request.JobDescription);
        designation.IsManagerRole = request.IsManagerRole;
        designation.IsActive = request.IsActive;
    }

    private static void Apply(Grade grade, GradeRequest request)
    {
        grade.Code = Clean(request.Code).ToUpperInvariant();
        grade.Name = Clean(request.Name);
        grade.Band = Clean(request.Band);
        grade.Level = request.Level;
        grade.IsActive = request.IsActive;
    }

    private static void Apply(CostCenter costCenter, CostCenterRequest request)
    {
        costCenter.CompanyId = request.CompanyId;
        costCenter.Code = Clean(request.Code).ToUpperInvariant();
        costCenter.Name = Clean(request.Name);
        costCenter.IsActive = request.IsActive;
    }

    private async Task EnsureCompanyUnique(Guid tenantId, string registrationNumber, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(registrationNumber);
        var exists = await _db.Companies.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.RegistrationNumber == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Company registration number already exists in this tenant.");
    }

    private async Task EnsureBranchCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(code).ToUpperInvariant();
        var exists = await _db.Branches.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Branch code already exists in this tenant.");
    }

    private async Task EnsureDepartmentCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(code).ToUpperInvariant();
        var exists = await _db.Departments.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Department code already exists in this tenant.");
    }

    private async Task EnsureDesignationCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(code).ToUpperInvariant();
        var exists = await _db.Designations.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Designation code already exists in this tenant.");
    }

    private async Task EnsureGradeCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(code).ToUpperInvariant();
        var exists = await _db.Grades.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Grade code already exists in this tenant.");
    }

    private async Task EnsureCostCenterCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(code).ToUpperInvariant();
        var exists = await _db.CostCenters.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Cost center code already exists in this tenant.");
    }

    private async Task EnsureCompanyExists(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await _db.Companies.AnyAsync(x => x.TenantId == tenantId && x.Id == companyId && !x.IsDeleted, cancellationToken))
        {
            throw new InvalidOperationException("Company not found in this tenant.");
        }
    }

    private async Task EnsureBranchExists(Guid tenantId, Guid? branchId, CancellationToken cancellationToken)
    {
        if (branchId.HasValue && !await _db.Branches.AnyAsync(x => x.TenantId == tenantId && x.Id == branchId.Value && !x.IsDeleted, cancellationToken))
        {
            throw new InvalidOperationException("Branch not found in this tenant.");
        }
    }

    private async Task EnsureDepartmentExists(Guid tenantId, Guid? departmentId, CancellationToken cancellationToken)
    {
        if (departmentId.HasValue && !await _db.Departments.AnyAsync(x => x.TenantId == tenantId && x.Id == departmentId.Value && !x.IsDeleted, cancellationToken))
        {
            throw new InvalidOperationException("Department not found in this tenant.");
        }
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private async Task<bool> SoftDelete<T>(DbSet<T> set, Guid tenantId, Guid id, string action, RequestContext context, CancellationToken cancellationToken) where T : class
    {
        var entity = await set.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        if ((Guid?)entity.GetType().GetProperty("TenantId")?.GetValue(entity) != tenantId) return false;
        entity.GetType().GetProperty("IsDeleted")?.SetValue(entity, true);
        entity.GetType().GetProperty("DeletedAtUtc")?.SetValue(entity, DateTime.UtcNow);
        entity.GetType().GetProperty("DeletedBy")?.SetValue(entity, context.UserId);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(action, typeof(T).Name, id.ToString(), context, null, cancellationToken);
        return true;
    }
}
