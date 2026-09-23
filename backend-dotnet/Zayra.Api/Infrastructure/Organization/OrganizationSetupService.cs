using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Jobs;
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

    public async Task<CompanyDto> CreateCompanyAsync(Guid tenantId, CompanyRequest request, RequestContext context, CancellationToken cancellationToken, bool asDraft = false)
    {
        ValidateCountryCode(request.CountryCode);
        ValidateEmailDomain(request.EmailDomain);
        await EnsureCompanyUnique(tenantId, request.RegistrationNumber, null, cancellationToken);
        var company = new Company { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(company, request);
        if (asDraft)
        {
            // GroupDraftPlatformApproval mode: created inactive, awaiting platform approval.
            company.ApprovalStatus = CompanyApprovalStatuses.Draft;
            company.IsActive = false;
        }
        _db.Companies.Add(company);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.company_created", nameof(Company), company.Id.ToString(), context, null, cancellationToken);
        return company.ToDto();
    }

    public async Task<CompanyDto?> UpdateCompanyAsync(Guid tenantId, Guid id, CompanyRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        ValidateCountryCode(request.CountryCode);
        ValidateEmailDomain(request.EmailDomain);
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        CompanyDto? result = null;
        var found = false;

        async Task<bool> UpdateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
            if (tenant is null) return true;
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var company = await _db.Companies.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
            if (company is null) return true;
            found = true;

            if (request.IsActive != company.IsActive)
                throw new InvalidOperationException(
                    "Company activation cannot be changed through the general editor. Use the controlled company-status workflow.");

            await EnsureCompanyUnique(tenantId, request.RegistrationNumber, id, ct);
            Apply(company, request, applyLifecycle: false);
            company.UpdatedAtUtc = changedAtUtc;
            company.UpdatedBy = context.UserId;
            result = company.ToDto();
            await AddCompanyAuditAsync(
                auditId,
                changedAtUtc,
                "organization.company_updated",
                company.Id,
                context with { TenantId = tenantId },
                ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteCompanyMutationAsync(
            auditId,
            "organization.company_updated",
            tenantId,
            context.UserId,
            id,
            UpdateOnceAsync,
            cancellationToken);
        return found ? result : null;
    }

    /// <summary>
    /// Company.CountryCode anchors statutory-pack resolution and the new company
    /// governance tables, so free text is rejected: non-empty values must be a
    /// recognized ISO code (ISO-2 canonical; ISO-3 accepted and mapped).
    /// </summary>
    private static void ValidateCountryCode(string? countryCode)
    {
        if (!CountryCodeStandard.IsValidOrEmpty(countryCode))
            throw new InvalidOperationException(
                $"Unrecognized country code '{countryCode}'. Use an ISO 3166-1 code (e.g. SA, AE, IN, GB).");
    }

    /// <summary>
    /// Company.EmailDomain auto-derives employee work emails, so a malformed value would produce broken
    /// addresses. Empty is ALLOWED (edge-7 manual-entry fallback — never forced); a non-empty value must be
    /// a valid host domain (labels of a-z/0-9/hyphen, a TLD of 2+ letters, ≤253 chars). Compared lowercased.
    /// </summary>
    private static void ValidateEmailDomain(string? emailDomain)
    {
        if (!IsValidEmailDomainOrEmpty(emailDomain))
            throw new InvalidOperationException(
                $"Invalid email domain '{emailDomain}'. Use a domain like 'acme.sa' (letters, digits, hyphens; a valid TLD).");
    }

    /// <summary>Shared domain validator so the Platform company-create path (which hand-maps CompanyRequest)
    /// enforces the SAME rule as the tenant setup path instead of a divergent copy. Empty ⇒ valid.</summary>
    public static bool IsValidEmailDomainOrEmpty(string? emailDomain)
    {
        var value = (emailDomain ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 || EmailDomainPattern.IsMatch(value);
    }

    private static readonly System.Text.RegularExpressions.Regex EmailDomainPattern = new(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        var deleted = false;

        async Task<bool> DeleteOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
            if (tenant is null) return true;

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var companies = await _db.Companies.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            var company = companies.SingleOrDefault(x => x.Id == id);
            if (company is null) return true;

            if (company.IsActive && companies.Count(x => x.IsActive) <= 1)
                throw new InvalidOperationException(
                    "Cannot delete the only active company. Activate another company first.");

            // Lock the complete employee cohort before checking the delete guard, so a concurrent
            // transfer/create cannot slip an active employee into the company after the check.
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var activeEmployees = await _db.Employees.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(e => e.TenantId == tenantId && e.CompanyId == id
                    && !e.IsDeleted
                    && e.Status != "Archived" && e.Status != "Terminated" && e.Status != "Exited")
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .ToListAsync(ct);
            if (activeEmployees.Count > 0)
                throw new InvalidOperationException(
                    $"Cannot delete company: {activeEmployees.Count} active employee{(activeEmployees.Count == 1 ? "" : "s")} still belong to it. " +
                    "Reassign or deactivate all employees before deleting the company.");

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var users = await _db.Users.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            await InvalidateCompanyAuthorizationAsync(users, changedAtUtc, context with { TenantId = tenantId }, ct);

            company.IsActive = false;
            company.IsDeleted = true;
            company.DeletedAtUtc = changedAtUtc;
            company.DeletedBy = context.UserId;
            company.UpdatedAtUtc = changedAtUtc;
            company.UpdatedBy = context.UserId;
            deleted = true;
            await AddCompanyAuditAsync(
                auditId,
                changedAtUtc,
                "organization.company_deleted",
                company.Id,
                context with { TenantId = tenantId },
                ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteCompanyMutationAsync(
            auditId,
            "organization.company_deleted",
            tenantId,
            context.UserId,
            id,
            DeleteOnceAsync,
            cancellationToken);
        return deleted;
    }
    public Task<bool> DeleteBranchAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Branches, tenantId, id, "organization.branch_deleted", context, cancellationToken);
    public Task<bool> DeleteDepartmentAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Departments, tenantId, id, "organization.department_deleted", context, cancellationToken);
    public Task<bool> DeleteDesignationAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Designations, tenantId, id, "organization.designation_deleted", context, cancellationToken);
    public Task<bool> DeleteGradeAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.Grades, tenantId, id, "organization.grade_deleted", context, cancellationToken);
    public Task<bool> DeleteCostCenterAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken) => SoftDelete(_db.CostCenters, tenantId, id, "organization.cost_center_deleted", context, cancellationToken);

    private static void Apply(Company company, CompanyRequest request, bool applyLifecycle = true)
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
        // Work-email auto-derivation config. EmailDomain is validated by ValidateEmailDomain (empty allowed —
        // the manual-entry fallback); the pattern is coerced to a recognized value (never rejected). Domain is
        // lowercased so derivation/collision keys are canonical.
        company.EmailDomain = Clean(request.EmailDomain).ToLowerInvariant();
        company.WorkEmailPattern = WorkEmailPatterns.Normalize(request.WorkEmailPattern);
        if (applyLifecycle) company.IsActive = request.IsActive;
    }

    private static void Apply(Branch branch, BranchRequest request)
    {
        branch.CompanyId = request.CompanyId;
        branch.Code = OrgCodes.Normalize(request.Code);
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
        department.Code = OrgCodes.Normalize(request.Code);
        department.NameEn = Clean(request.NameEn);
        department.NameAr = Clean(request.NameAr);
        department.ManagerEmployeeId = request.ManagerEmployeeId;
        department.IsActive = request.IsActive;
    }

    private static void Apply(Designation designation, DesignationRequest request)
    {
        designation.DepartmentId = request.DepartmentId;
        designation.Code = OrgCodes.Normalize(request.Code);
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
        grade.Code = OrgCodes.Normalize(request.Code);
        grade.Name = Clean(request.Name);
        grade.Band = Clean(request.Band);
        grade.Level = request.Level;

        // Pay-scale band — must be ordered Min ≤ Mid ≤ Max when supplied.
        if (request.MaxSalary > 0 && request.MinSalary > request.MaxSalary)
            throw new InvalidOperationException("MinSalary cannot exceed MaxSalary.");
        if (request.MidSalary > 0 && (request.MidSalary < request.MinSalary || (request.MaxSalary > 0 && request.MidSalary > request.MaxSalary)))
            throw new InvalidOperationException("MidSalary must fall between MinSalary and MaxSalary.");
        grade.MinSalary = request.MinSalary;
        grade.MidSalary = request.MidSalary;
        grade.MaxSalary = request.MaxSalary;
        grade.Currency = string.IsNullOrWhiteSpace(request.Currency) ? "SAR" : Clean(request.Currency).ToUpperInvariant();

        grade.IsActive = request.IsActive;
    }

    private static void Apply(CostCenter costCenter, CostCenterRequest request)
    {
        costCenter.CompanyId = request.CompanyId;
        costCenter.Code = OrgCodes.Normalize(request.Code);
        costCenter.Name = Clean(request.Name);
        costCenter.IsActive = request.IsActive;
    }

    private async Task ExecuteCompanyMutationAsync(
        Guid auditId,
        string auditAction,
        Guid tenantId,
        Guid? userId,
        Guid entityId,
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            await operation(cancellationToken);
            return;
        }

        var entityIdText = entityId.ToString();
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            operation,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId
                    && x.Action == auditAction
                    && x.EntityName == nameof(Company)
                    && x.EntityId == entityIdText
                    && x.TenantId == tenantId
                    && x.UserId == userId, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    private async Task AddCompanyAuditAsync(
        Guid auditId,
        DateTime createdAtUtc,
        string action,
        Guid companyId,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        var previousHash = await _db.AuditLogs.IgnoreQueryFilters()
            .Where(x => x.TenantId == context.TenantId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => x.EntryHash)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var audit = new AuditLog
        {
            Id = auditId,
            TenantId = context.TenantId,
            UserId = context.UserId,
            Action = action,
            EntityName = nameof(Company),
            EntityId = companyId.ToString(),
            IpAddress = context.IpAddress,
            UserAgent = context.UserAgent,
            Metadata = System.Text.Json.JsonSerializer.Serialize(new { source = "organization_setup" }),
            PreviousHash = previousHash,
            CreatedAtUtc = createdAtUtc
        };
        audit.EntryHash = AuditService.ComputeHash(audit);
        _db.AuditLogs.Add(audit);
    }

    private async Task InvalidateCompanyAuthorizationAsync(
        IReadOnlyCollection<Zayra.Api.Domain.Entities.User> users,
        DateTime changedAtUtc,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var userIds = users.Select(x => x.Id).OrderBy(x => x).ToList();
        foreach (var user in users)
            TenantSessionSecurity.RotateStamp(user, changedAtUtc);

        // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
        await _db.MfaChallengeTokens.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => userIds.Contains(x.UserId) && x.RevokedAtUtc == null)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (_db.Database.IsRelational())
        {
            // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
            await _db.MfaChallengeTokens.IgnoreQueryFilters()
                .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, changedAtUtc), cancellationToken);
            await _db.RefreshTokens
                .Where(x => userIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.RevokedAtUtc, changedAtUtc)
                    .SetProperty(x => x.RevokedByIp, context.IpAddress), cancellationToken);
            return;
        }

        // IgnoreQueryFilters is intentional: challenge rows are pinned to user ids taken from the tenant-locked graph above (register §6).
        foreach (var challenge in await _db.MfaChallengeTokens.IgnoreQueryFilters()
            .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
            .ToListAsync(cancellationToken))
            challenge.UsedAtUtc = changedAtUtc;
        foreach (var token in await _db.RefreshTokens
            .Where(x => userIds.Contains(x.UserId) && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken))
        {
            token.RevokedAtUtc = changedAtUtc;
            token.RevokedByIp = context.IpAddress;
        }
    }

    private async Task EnsureCompanyUnique(Guid tenantId, string registrationNumber, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(registrationNumber);
        var exists = await _db.Companies.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.RegistrationNumber == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Company registration number already exists in this tenant.");
    }

    private async Task EnsureBranchCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        var exists = await _db.Branches.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Branch code already exists in this tenant.");
    }

    private async Task EnsureDepartmentCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        var exists = await _db.Departments.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Department code already exists in this tenant.");
    }

    private async Task EnsureDesignationCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        var exists = await _db.Designations.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Designation code already exists in this tenant.");
    }

    private async Task EnsureGradeCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        var exists = await _db.Grades.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Grade code already exists in this tenant.");
    }

    private async Task EnsureCostCenterCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        var exists = await _db.CostCenters.AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
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
