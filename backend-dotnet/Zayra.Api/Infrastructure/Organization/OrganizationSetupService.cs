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
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Payroll;
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

    public async Task<PagedResult<CompanyDto>> GetCompaniesAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? accessibleCompanyIds = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var filtered = _db.Companies.Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (accessibleCompanyIds is not null)
        {
            var ids = accessibleCompanyIds.ToList();
            filtered = filtered.Where(x => ids.Contains(x.Id));
        }
        var query = filtered.OrderBy(x => x.LegalNameEn);
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
        if (SaudiIdentifierProblem(request, creating: true) is { } saudiProblem) throw new InvalidOperationException(saudiProblem);
        await CompanyEmailDomainRules.EnsureCallerMaySetAsync(_db, tenantId, context.UserId, null, request.EmailDomain, cancellationToken);
        await CompanyEmailDomainRules.EnsureClaimableAsync(_db, tenantId, request.EmailDomain, cancellationToken);
        await EnsureCompanyUnique(tenantId, request.RegistrationNumber, null, cancellationToken);
        await EnsureErpCompanyCodeAllowedAsync(tenantId, null, null, request.ErpCompanyCode, context, cancellationToken);
        var company = new Company { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(company, request);
        if (asDraft)
        {
            // GroupDraftPlatformApproval mode: created inactive, awaiting platform approval.
            company.ApprovalStatus = CompanyApprovalStatuses.Draft;
            company.IsActive = false;
        }
        _db.Companies.Add(company);
        await EnsureExportedCompanyCodeUniqueAsync(tenantId, company, cancellationToken);
        await SaveCompanyAsync(company.ErpCompanyCode, cancellationToken);
        await _audit.WriteAsync("organization.company_created", nameof(Company), company.Id.ToString(), context, null, cancellationToken);
        if (company.ErpCompanyCode is not null)
            await _audit.WriteAsync(ErpCompanyCodeChangedAction, nameof(Company), company.Id.ToString(), context,
                System.Text.Json.JsonSerializer.Serialize(new { oldErpCompanyCode = (string?)null, newErpCompanyCode = company.ErpCompanyCode }), cancellationToken);
        if (company.EmailDomain.Length > 0)
            await _audit.WriteAsync(CompanyEmailDomainRules.ChangedAction, nameof(Company), company.Id.ToString(), context,
                System.Text.Json.JsonSerializer.Serialize(new { oldEmailDomain = (string?)null, newEmailDomain = company.EmailDomain }), cancellationToken);
        return company.ToDto();
    }

    public async Task<CompanyDto?> UpdateCompanyAsync(Guid tenantId, Guid id, CompanyRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        ValidateCountryCode(request.CountryCode);
        ValidateEmailDomain(request.EmailDomain);
        if (SaudiIdentifierProblem(request, creating: false) is { } saudiProblem) throw new InvalidOperationException(saudiProblem);
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        CompanyDto? result = null;
        var found = false;

        async Task<bool> UpdateOnceAsync(CancellationToken ct)
        {
            // Cleared for this command's own retries only. Inside a caller's transaction (the setup wizard's commit,
            // see ExecuteCompanyMutationAsync) the caller's tracked rows are part of the same unit of work.
            if (_db.Database.CurrentTransaction is null) _db.ChangeTracker.Clear();
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
            // The MOL establishment number is the wage file's authority (design Decision 10): a CHANGED value must be one
            // the file accepts, as on create. An unchanged value saved before the rule is left alone.
            if (SaudiEstablishmentChangeProblem(request, company.WpsEmployerId) is { } molProblem)
                throw new InvalidOperationException(molProblem);

            await EnsureCompanyUnique(tenantId, request.RegistrationNumber, id, ct);
            var priorEmailDomain = company.EmailDomain ?? string.Empty;
            await CompanyEmailDomainRules.EnsureCallerMaySetAsync(_db, tenantId, context.UserId, priorEmailDomain, request.EmailDomain, ct);
            // Claimability (public mail, another tenant's domain) is judged only when the domain actually CHANGES: a company
            // saved before these rules may hold a domain they now refuse, and editing its address or name must still work.
            if (!string.Equals(CompanyEmailDomainRules.Normalize(priorEmailDomain), CompanyEmailDomainRules.Normalize(request.EmailDomain), StringComparison.Ordinal))
                await CompanyEmailDomainRules.EnsureClaimableAsync(_db, tenantId, request.EmailDomain, ct);
            var priorErpCode = company.ErpCompanyCode;
            await EnsureErpCompanyCodeAllowedAsync(tenantId, company.Id, priorErpCode, request.ErpCompanyCode, context, ct);
            Apply(company, request, applyLifecycle: false);
            // Before/after of the code journal exports file this company's postings under: who changed it, from what.
            if (!string.Equals(priorErpCode, company.ErpCompanyCode, StringComparison.Ordinal))
                _db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, UserId = context.UserId, Action = ErpCompanyCodeChangedAction,
                    EntityName = nameof(Company), EntityId = company.Id.ToString(), IpAddress = context.IpAddress, UserAgent = context.UserAgent,
                    Metadata = System.Text.Json.JsonSerializer.Serialize(new { oldErpCompanyCode = priorErpCode, newErpCompanyCode = company.ErpCompanyCode }),
                    CreatedAtUtc = changedAtUtc,
                });
            if (!string.Equals(priorEmailDomain, company.EmailDomain, StringComparison.Ordinal))
                _db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, UserId = context.UserId, Action = CompanyEmailDomainRules.ChangedAction,
                    EntityName = nameof(Company), EntityId = company.Id.ToString(), IpAddress = context.IpAddress, UserAgent = context.UserAgent,
                    Metadata = System.Text.Json.JsonSerializer.Serialize(new { oldEmailDomain = priorEmailDomain, newEmailDomain = company.EmailDomain }),
                    CreatedAtUtc = changedAtUtc,
                });
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
            await EnsureExportedCompanyCodeUniqueAsync(tenantId, company, ct);
            await SaveCompanyAsync(company.ErpCompanyCode, ct);
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
        if (CountryCodeProblem(countryCode) is { } problem) throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// Saudi establishment identifiers, as a message (null when valid). The MOL establishment number on the
    /// company (<see cref="Company.WpsEmployerId"/>) is the authority the wage file prefers (design contract
    /// Decision 10), so a Saudi company created with one must carry a value the wage file would accept —
    /// <see cref="Payroll.SaudiBankExports.KsaWageFileRules.ValidateEstablishmentId"/>, the same rule the file
    /// applies. Empty stays allowed here (today's behaviour; the wizard requires it separately). The Unified
    /// National Number, when given, is 10 digits (<c>ValidateNationalUnifiedNo</c>).
    /// </summary>
    public static string? SaudiIdentifierProblem(CompanyRequest request, bool creating)
    {
        if (CountryCodeStandard.NormalizeToIso2(request.CountryCode) != "SA") return null;
        var errors = new List<Payroll.SaudiBankExports.SaudiBankExportIssueDto>();
        if (creating && !string.IsNullOrWhiteSpace(request.WpsEmployerId))
            Payroll.SaudiBankExports.KsaWageFileRules.ValidateEstablishmentId(request.WpsEmployerId.Trim(), errors);
        if (!string.IsNullOrWhiteSpace(request.UnifiedNationalNumber))
            Payroll.SaudiBankExports.KsaWageFileRules.ValidateNationalUnifiedNo(request.UnifiedNationalNumber.Trim(), required: false, errors);
        return errors.FirstOrDefault()?.Message;
    }

    /// <summary>
    /// The update half of <see cref="SaudiIdentifierProblem"/>: a Saudi company's MOL establishment number, validated
    /// with the wage file's own rule when it changes to a non-empty value (spaces ignored). Null when valid or unchanged.
    /// </summary>
    public static string? SaudiEstablishmentChangeProblem(CompanyRequest request, string? storedWpsEmployerId)
    {
        if (CountryCodeStandard.NormalizeToIso2(request.CountryCode) != "SA") return null;
        var next = (request.WpsEmployerId ?? string.Empty).Trim();
        if (next.Length == 0 || string.Equals(next, (storedWpsEmployerId ?? string.Empty).Trim(), StringComparison.Ordinal)) return null;
        var errors = new List<Payroll.SaudiBankExports.SaudiBankExportIssueDto>();
        Payroll.SaudiBankExports.KsaWageFileRules.ValidateEstablishmentId(next, errors);
        return errors.FirstOrDefault()?.Message;
    }

    /// <summary>The form's country-code rule as a message (null when valid), shared with the bulk
    /// organization-structure import so both doors refuse the same values with the same words.</summary>
    public static string? CountryCodeProblem(string? countryCode) =>
        CountryCodeStandard.IsValidOrEmpty(countryCode)
            ? null
            : $"Unrecognized country code '{countryCode}'. Use an ISO 3166-1 code (e.g. SA, AE, IN, GB).";

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

    public async Task<PagedResult<BranchDto>> GetBranchesAsync(Guid tenantId, Guid? companyId, int page, int pageSize, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? accessibleCompanyIds = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Branches.Where(x => x.TenantId == tenantId && !x.IsDeleted);
        // Branch is tenant-owned with no ambient company filter, so the caller's companies are applied
        // here: a company-scoped caller lists only the branches of the companies they hold.
        if (accessibleCompanyIds is not null)
        {
            var ids = accessibleCompanyIds.ToList();
            query = query.Where(x => ids.Contains(x.CompanyId));
        }
        if (companyId.HasValue) query = query.Where(x => x.CompanyId == companyId.Value);
        var ordered = query.OrderBy(x => x.Code);
        var total = await ordered.CountAsync(cancellationToken);
        var branches = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var names = await CompanyNamesAsync(tenantId, branches.Select(b => b.CompanyId), cancellationToken);
        var items = branches.Select(b => WithCompanyName(b, names)).ToList();
        return new PagedResult<BranchDto>(items, total, page, pageSize);
    }

    public async Task<BranchDto?> GetBranchAsync(Guid tenantId, Guid id, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? accessibleCompanyIds = null)
    {
        var branch = await _db.Branches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (branch is null) return null;
        if (accessibleCompanyIds is not null && !accessibleCompanyIds.Contains(branch.CompanyId)) return null;
        return WithCompanyName(branch, await CompanyNamesAsync(tenantId, new[] { branch.CompanyId }, cancellationToken));
    }

    /// <summary>Legal names of the given companies, tenant-filtered. Branch lists carry the name so a
    /// screen never has to show a raw CompanyId when it has not loaded that company.</summary>
    private async Task<Dictionary<Guid, string>> CompanyNamesAsync(Guid tenantId, IEnumerable<Guid> companyIds, CancellationToken cancellationToken)
    {
        var ids = companyIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, cancellationToken);
    }

    private static BranchDto WithCompanyName(Branch branch, IReadOnlyDictionary<Guid, string> names) =>
        branch.ToDto() with { CompanyName = names.TryGetValue(branch.CompanyId, out var name) ? name : string.Empty };

    public async Task<BranchDto> CreateBranchAsync(Guid tenantId, BranchRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        ValidateCountryCode(request.CountryCode);
        await EnsureCompanyExists(tenantId, request.CompanyId, cancellationToken);
        await EnsureBranchCodeUnique(tenantId, request.CompanyId, request.Code, null, cancellationToken);
        var branch = new Branch { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(branch, request);
        _db.Branches.Add(branch);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.branch_created", nameof(Branch), branch.Id.ToString(), context, null, cancellationToken);
        return WithCompanyName(branch, await CompanyNamesAsync(tenantId, new[] { branch.CompanyId }, cancellationToken));
    }

    public async Task<BranchDto?> UpdateBranchAsync(Guid tenantId, Guid id, BranchRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var branch = await _db.Branches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (branch is null) return null;
        // A changed country must be a real one. An unchanged legacy value is let through: the wizard re-sends a
        // branch's stored country when it edits only the name or city, and that must not start failing.
        if (!string.Equals(CountryCodeStandard.ToStored(request.CountryCode), branch.CountryCode, StringComparison.OrdinalIgnoreCase))
            ValidateCountryCode(request.CountryCode);
        await EnsureCompanyExists(tenantId, request.CompanyId, cancellationToken);
        await EnsureBranchCodeUnique(tenantId, request.CompanyId, request.Code, id, cancellationToken);
        Apply(branch, request);
        branch.UpdatedAtUtc = DateTime.UtcNow;
        branch.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.branch_updated", nameof(Branch), branch.Id.ToString(), context, null, cancellationToken);
        return WithCompanyName(branch, await CompanyNamesAsync(tenantId, new[] { branch.CompanyId }, cancellationToken));
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
        var companyId = await ResolveOwningCompanyAsync(tenantId, request.CompanyId, cancellationToken);
        await EnsureDepartmentLinksAsync(tenantId, companyId, null, request, cancellationToken);
        await EnsureDepartmentCodeUnique(tenantId, companyId, request.Code, null, cancellationToken);
        var department = new Department { TenantId = tenantId, CompanyId = companyId, CreatedBy = context.UserId };
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
        var companyId = await ResolveCompanyOnUpdateAsync(tenantId, department.CompanyId, request.CompanyId, cancellationToken);
        await EnsureDepartmentLinksAsync(tenantId, companyId, id, request, cancellationToken);
        await EnsureDepartmentCodeUnique(tenantId, companyId, request.Code, id, cancellationToken);
        department.CompanyId = companyId;
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
        var companyId = await ResolveOwningCompanyAsync(tenantId, request.CompanyId, cancellationToken);
        await EnsureDesignationLinksAsync(tenantId, companyId, request, cancellationToken);
        await EnsureDesignationCodeUnique(tenantId, companyId, request.Code, null, cancellationToken);
        var designation = new Designation { TenantId = tenantId, CompanyId = companyId, CreatedBy = context.UserId };
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
        var companyId = await ResolveCompanyOnUpdateAsync(tenantId, designation.CompanyId, request.CompanyId, cancellationToken);
        await EnsureDesignationLinksAsync(tenantId, companyId, request, cancellationToken);
        await EnsureDesignationCodeUnique(tenantId, companyId, request.Code, id, cancellationToken);
        designation.CompanyId = companyId;
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
        var companyId = await ResolveOwningCompanyAsync(tenantId, request.CompanyId, cancellationToken);
        await EnsureGradeCodeUnique(tenantId, companyId, request.Code, null, cancellationToken);
        var grade = new Grade { TenantId = tenantId, CompanyId = companyId, CreatedBy = context.UserId };
        Apply(grade, request);
        // Addendum B: a grade's currency defaults to its company's, not to a tenant-wide one.
        if (string.IsNullOrWhiteSpace(request.Currency)) grade.Currency = await CompanyCurrencyAsync(tenantId, companyId, cancellationToken) ?? grade.Currency;
        _db.Grades.Add(grade);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("organization.grade_created", nameof(Grade), grade.Id.ToString(), context, null, cancellationToken);
        return grade.ToDto();
    }

    public async Task<GradeDto?> UpdateGradeAsync(Guid tenantId, Guid id, GradeRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var grade = await _db.Grades.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (grade is null) return null;
        var companyId = await ResolveCompanyOnUpdateAsync(tenantId, grade.CompanyId, request.CompanyId, cancellationToken);
        await EnsureGradeCodeUnique(tenantId, companyId, request.Code, id, cancellationToken);
        grade.CompanyId = companyId;
        var previousCurrency = grade.Currency;
        Apply(grade, request);
        if (string.IsNullOrWhiteSpace(request.Currency)) grade.Currency = previousCurrency;
        grade.UpdatedAtUtc = DateTime.UtcNow;
        grade.UpdatedBy = context.UserId;
        // The grade's salary structure is derived from the grade: a renamed grade, a new pay range or currency follows
        // into it in the same save. Only an existing structure is kept in step; the package editor creates it.
        var packageLines = await _db.GradePayScaleComponents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.GradeId == grade.Id && c.IsActive).ToListAsync(cancellationToken);
        await GradeSalaryStructureSync.StageAsync(_db, grade, packageLines, context.UserId, createIfMissing: false, cancellationToken);
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
        var code = OrgCodes.Normalize(request.Code);
        if (!CostCentreCode.IsValid(code)) throw CostCentreRuleException.CodeInvalid();
        var functionalClass = NormalizeClass(request.FunctionalClass, out var classGiven);
        if (classGiven && functionalClass is not null && !CostCentreFunctionalClasses.IsKnown(functionalClass)) throw CostCentreRuleException.FunctionalClassInvalid();
        var glSegment = NormalizeGlSegment(request.GlSegment, out var segmentGiven);
        if (segmentGiven && glSegment is not null && !CostCentreCode.IsValidGlSegment(glSegment)) throw CostCentreRuleException.GlSegmentInvalid();

        await EnsureCostCenterCodeUnique(tenantId, request.CompanyId, request.Code, null, cancellationToken);
        if (glSegment is not null) await EnsureGlSegmentUnique(tenantId, request.CompanyId, glSegment, null, cancellationToken);

        var costCenter = new CostCenter { TenantId = tenantId, CreatedBy = context.UserId };
        Apply(costCenter, request);
        costCenter.FunctionalClass = functionalClass;
        costCenter.GlSegment = glSegment;
        await ApplyParentAsync(tenantId, costCenter, request.ParentId, cancellationToken);
        _db.CostCenters.Add(costCenter);
        // The audit row is staged, not written: it commits in the same SaveChanges as the cost centre.
        _audit.Stage("organization.cost_center_created", nameof(CostCenter), costCenter.Id.ToString(), context with { TenantId = tenantId },
            CostCentreAuditJson(null, costCenter, request.Reason), costCenter.CompanyId);
        await _db.SaveChangesAsync(cancellationToken);
        return costCenter.ToDto();
    }

    public async Task<CostCenterDto?> UpdateCostCenterAsync(Guid tenantId, Guid id, CostCenterRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var costCenter = await _db.CostCenters.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (costCenter is null) return null;
        var before = CostCentreSnapshot(costCenter);

        // The same code in another case is not a change; the stored spelling is kept so a posted line's code never differs from the master.
        var requestedCode = OrgCodes.Normalize(request.Code);
        var codeChanged = !string.Equals(requestedCode, costCenter.Code, StringComparison.OrdinalIgnoreCase);
        if (codeChanged && !CostCentreCode.IsValid(requestedCode)) throw CostCentreRuleException.CodeInvalid();
        var companyChanged = request.CompanyId != costCenter.CompanyId;

        var functionalClass = NormalizeClass(request.FunctionalClass, out var classGiven);
        if (classGiven && functionalClass is not null && !CostCentreFunctionalClasses.IsKnown(functionalClass)) throw CostCentreRuleException.FunctionalClassInvalid();
        var classChanged = classGiven && !string.Equals(functionalClass, costCenter.FunctionalClass, StringComparison.Ordinal);
        if (classChanged && string.IsNullOrWhiteSpace(request.Reason)) throw CostCentreRuleException.ReasonRequired();
        var glSegment = NormalizeGlSegment(request.GlSegment, out var segmentGiven);
        if (segmentGiven && glSegment is not null && !CostCentreCode.IsValidGlSegment(glSegment)) throw CostCentreRuleException.GlSegmentInvalid();

        // M-9: once a posted line carries the cost centre, its code and company are frozen (the line holds a snapshot of both).
        if ((codeChanged || companyChanged)
            && await _db.FinanceGlEntries.AnyAsync(e => e.TenantId == tenantId && e.CostCenterId == id, cancellationToken))
            throw CostCentreRuleException.CodeLocked(costCenter.Code);
        if (codeChanged) await EnsureCostCenterCodeUnique(tenantId, request.CompanyId, request.Code, id, cancellationToken);
        if (segmentGiven && glSegment is not null && !string.Equals(glSegment, costCenter.GlSegment, StringComparison.OrdinalIgnoreCase))
            await EnsureGlSegmentUnique(tenantId, request.CompanyId, glSegment, id, cancellationToken);
        if ((before.IsActive && !request.IsActive) || companyChanged)
            await EnsureNotInUseAsync(tenantId, costCenter, cancellationToken);

        var keepCode = costCenter.Code;
        Apply(costCenter, request);
        if (!codeChanged) costCenter.Code = keepCode;
        if (classGiven) costCenter.FunctionalClass = functionalClass;
        if (segmentGiven) costCenter.GlSegment = glSegment;
        await ApplyParentAsync(tenantId, costCenter, request.ParentId, cancellationToken);
        costCenter.UpdatedAtUtc = DateTime.UtcNow;
        costCenter.UpdatedBy = context.UserId;
        _audit.Stage("organization.cost_center_updated", nameof(CostCenter), costCenter.Id.ToString(), context with { TenantId = tenantId },
            CostCentreAuditJson(before, costCenter, request.Reason), costCenter.CompanyId);
        await _db.SaveChangesAsync(cancellationToken);
        return costCenter.ToDto();
    }

    public async Task<bool> DeleteCostCenterAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken)
    {
        var costCenter = await _db.CostCenters.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (costCenter is null) return false;
        await EnsureNotInUseAsync(tenantId, costCenter, cancellationToken);
        var before = CostCentreSnapshot(costCenter);
        costCenter.IsDeleted = true;
        costCenter.DeletedAtUtc = DateTime.UtcNow;
        costCenter.DeletedBy = context.UserId;
        _audit.Stage("organization.cost_center_deleted", nameof(CostCenter), costCenter.Id.ToString(), context with { TenantId = tenantId },
            CostCentreAuditJson(before, costCenter, null), costCenter.CompanyId);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Null = the request did not mention the field (leave it alone); empty = clear it.</summary>
    private static string? NormalizeClass(string? value, out bool given)
    {
        given = value is not null;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private static string? NormalizeGlSegment(string? value, out bool given)
    {
        given = value is not null;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task EnsureGlSegmentUnique(Guid tenantId, Guid? companyId, string segment, Guid? excludedId, CancellationToken cancellationToken)
    {
        var upper = segment.ToUpperInvariant();
        var exists = await ScopedBypass.TenantWide(_db.CostCenters, tenantId, OrgScopeJustification)
            .AnyAsync(x => x.CompanyId == companyId && !x.IsDeleted && x.GlSegment != null && x.GlSegment.ToUpper() == upper && x.Id != excludedId, cancellationToken);
        if (exists) throw CostCentreRuleException.GlSegmentTaken();
    }

    /// <summary>Sets the parent (null = unchanged, Guid.Empty = clear) after the reference and tree rules: same company, active when new, no loop, at most ten levels.</summary>
    private async Task ApplyParentAsync(Guid tenantId, CostCenter costCenter, Guid? requestedParent, CancellationToken cancellationToken)
    {
        if (requestedParent is null || requestedParent == costCenter.ParentId) return;
        if (requestedParent == Guid.Empty) { costCenter.ParentId = null; return; }

        var parentId = requestedParent.Value;
        if (parentId == costCenter.Id)
            throw CostCentreRuleException.Parent("cost_centre_parent_self", CostCentreRuleException.ParentMessage("cost_centre_parent_self"));
        var check = await new CostCentreRefValidator(_db).ValidateAsync(tenantId, costCenter.CompanyId, parentId, forNewAssignment: true, cancellationToken);
        if (!check.IsValid) throw CostCentreRuleException.Parent(check.Code!, check.Message!);

        var parentOf = (await _db.CostCenters.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.ParentId }).ToListAsync(cancellationToken)).ToDictionary(x => x.Id, x => x.ParentId);
        var refusal = CostCentreHierarchy.Check(costCenter.Id, parentId, parentOf);
        if (refusal is not null) throw CostCentreRuleException.Parent(refusal, CostCentreRuleException.ParentMessage(refusal));
        costCenter.ParentId = parentId;
    }

    /// <summary>M-8: live references to the cost centre, by kind. Historical journal lines are not live references (they hold their own snapshot).</summary>
    private async Task<Dictionary<string, int>> CostCentreUsageAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) => new()
    {
        ["department"] = await ScopedBypass.TenantWide(_db.Departments, tenantId, OrgScopeJustification).CountAsync(x => x.CostCenterId == id && !x.IsDeleted, cancellationToken),
        ["employee"] = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, OrgScopeJustification).CountAsync(x => x.CostCenterId == id && !x.IsDeleted, cancellationToken),
        ["position"] = await ScopedBypass.TenantWide(_db.Positions, tenantId, OrgScopeJustification).CountAsync(x => x.CostCenterId == id && !x.IsDeleted, cancellationToken),
        ["mapping"] = await ScopedBypass.TenantWide(_db.GlAccountMappings, tenantId, OrgScopeJustification).CountAsync(x => x.SegmentCostCenterId == id && x.IsActive, cancellationToken),
        ["companyDefault"] = await ScopedBypass.TenantWide(_db.Companies, tenantId, OrgScopeJustification).CountAsync(x => x.DefaultCostCenterId == id && !x.IsDeleted, cancellationToken),
        ["activeChild"] = await ScopedBypass.TenantWide(_db.CostCenters, tenantId, OrgScopeJustification).CountAsync(x => x.ParentId == id && !x.IsDeleted && x.IsActive, cancellationToken),
    };

    private async Task EnsureNotInUseAsync(Guid tenantId, CostCenter costCenter, CancellationToken cancellationToken)
    {
        var counts = await CostCentreUsageAsync(tenantId, costCenter.Id, cancellationToken);
        if (counts.Values.Any(n => n > 0)) throw CostCentreRuleException.InUse(costCenter.Code, counts);
    }

    private static CostCentreSnapshotRow CostCentreSnapshot(CostCenter c) => new(
        c.Id, c.Code, c.Name, c.NameAr, c.CompanyId, c.ParentId, c.IsActive, c.FunctionalClass, c.GlSegment, c.IsDeleted);

    private sealed record CostCentreSnapshotRow(Guid Id, string Code, string Name, string? NameAr, Guid? CompanyId, Guid? ParentId,
        bool IsActive, string? FunctionalClass, string? GlSegment, bool IsDeleted);

    private static string CostCentreAuditJson(CostCentreSnapshotRow? before, CostCenter after, string? reason) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            before,
            after = CostCentreSnapshot(after),
            reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

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

    private static void Apply(Company company, CompanyRequest request, bool applyLifecycle = true)
    {
        company.LegalNameEn = Clean(request.LegalNameEn);
        company.LegalNameAr = Clean(request.LegalNameAr);
        company.TradeName = Clean(request.TradeName);
        company.CountryCode  = CountryCodeStandard.ToStored(request.CountryCode);
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
        if (request.ErpCompanyCode is not null)
            company.ErpCompanyCode = CleanErpCode(request.ErpCompanyCode);
        if (request.UnifiedNationalNumber is not null)
            company.UnifiedNationalNumber = string.IsNullOrWhiteSpace(request.UnifiedNationalNumber) ? null : request.UnifiedNationalNumber.Trim();
        if (applyLifecycle) company.IsActive = request.IsActive;
    }

    private static void Apply(Branch branch, BranchRequest request)
    {
        branch.CompanyId = request.CompanyId;
        branch.Code = OrgCodes.Normalize(request.Code);
        branch.NameEn = Clean(request.NameEn);
        branch.NameAr = Clean(request.NameAr);
        branch.CountryCode = CountryCodeStandard.ToStored(request.CountryCode);
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
        grade.NameAr = string.IsNullOrWhiteSpace(request.NameAr) ? null : request.NameAr.Trim();
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
        costCenter.NameAr = string.IsNullOrWhiteSpace(request.NameAr) ? null : request.NameAr.Trim();
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
        // A caller that already holds a transaction (the setup wizard's commit, under the tenant setup lock) owns
        // atomicity and retries: this mutation joins it instead of opening a nested one, which Npgsql refuses.
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
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

    // ── ERP company code ─────────────────────────────────────────────────────────────────────────

    /// <summary>The key that may set or change a company's ERP company code: whoever maintains the general ledger
    /// the journal exports are built from. HR may edit the rest of the company record.</summary>
    public const string ErpCompanyCodePermission = "finance.gl.manage";
    public const string ErpCompanyCodeChangedAction = "organization.company_erp_code_changed";
    /// <summary>Codes the journal export already uses as labels for "no company" and "the whole group".</summary>
    private static readonly string[] ReservedErpCodes = { "GROUP", "UNATTRIBUTED" };

    private static string? CleanErpCode(string? value) => Clean(value) is { Length: > 0 } erp ? erp : null;

    /// <summary>
    /// A company's ERP company code decides which legal entity's ledger the ERP books its journals into, so:
    /// <list type="bullet">
    /// <item>only a holder of <see cref="ErpCompanyCodePermission"/> may set or change it — a request that repeats the
    /// stored value (or omits it) passes for anyone, so HR can still edit the rest of the form (403 otherwise);</item>
    /// <item>it is unique per tenant, case-insensitively, among live companies — and may not equal the registration
    /// number another company without a code is exported under (409, naming that company);</item>
    /// <item>GROUP and UNATTRIBUTED are reserved (400).</item>
    /// </list>
    /// The database backs the uniqueness with ux_companies_tenant_erp_company_code (see <see cref="SaveCompanyAsync"/>).
    /// Refusals use <see cref="EmailDomainRefusedException"/>, the (code, status, message) refusal both company doors
    /// already map to an HTTP status.
    /// </summary>
    private async Task EnsureErpCompanyCodeAllowedAsync(
        Guid tenantId, Guid? companyId, string? current, string? requested, RequestContext context, CancellationToken ct)
    {
        if (requested is null) return; // not sent: the stored code is kept
        var next = CleanErpCode(requested);
        if (string.Equals(next, current, StringComparison.Ordinal)) return;

        if (context.UserId is Guid actor)
        {
            var caller = await Zayra.Api.Infrastructure.Auth.PrivilegeCeilingGraph.TryLoadCallerAsync(_db, tenantId, actor, ct);
            if (caller is null || !caller.Held.Contains(ErpCompanyCodePermission))
                throw new EmailDomainRefusedException("erp_company_code_needs_finance", 403,
                    "Only a finance user who manages the general ledger can set or change a company's ERP company code, "
                    + "because your ERP files each company's journals under it. Leave it as it is, or ask finance to change it.");
        }
        if (next is null) return; // clearing the code: the registration number becomes the exported code again

        if (ReservedErpCodes.Contains(next, StringComparer.OrdinalIgnoreCase))
            throw new EmailDomainRefusedException("erp_company_code_reserved", 400,
                $"ERP company code {next} is reserved for journals that belong to no single company. Choose another code.");
        if (next.Length > 30 || !next.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new EmailDomainRefusedException("erp_company_code_invalid", 400,
                "An ERP company code may be at most 30 characters: letters, digits, '.', '_' and '-'.");

        var lower = next.ToLowerInvariant();
        // Tenant-wide: a company-scoped editor must still collide with a sibling company they cannot see.
        // Only the colliding company's name leaves this query.
        var other = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.Companies, tenantId,
                "ERP company code uniqueness across every legal entity of the tenant (only the colliding company's name is read)")
            .AsNoTracking()
            .Where(x => !x.IsDeleted && x.Id != companyId
                     && ((x.ErpCompanyCode != null && x.ErpCompanyCode.ToLower() == lower)
                         || ((x.ErpCompanyCode == null || x.ErpCompanyCode == "") && x.RegistrationNumber.ToLower() == lower)))
            .Select(x => x.LegalNameEn)
            .FirstOrDefaultAsync(ct);
        if (other is not null)
            throw new EmailDomainRefusedException("erp_company_code_taken", 409,
                $"ERP company code {next} is already used by {other}. Each company needs its own code, or your ERP would book "
                + "both companies' journals into one ledger.");
    }

    /// <summary>
    /// Journal exports label each company with its ERP code, or — when it has none — its registration number
    /// (<see cref="Zayra.Api.Infrastructure.Finance.JournalExportBuilder.CompanyCodeOf"/>). The ERP-code check above only
    /// runs when a code is set; this one runs on every save, on the code the company will ACTUALLY be exported under,
    /// so creating a company, changing its registration number or clearing its ERP code can never make two legal
    /// entities export under the same company code (409, naming the other company).
    /// </summary>
    private async Task EnsureExportedCompanyCodeUniqueAsync(Guid tenantId, Company company, CancellationToken ct)
    {
        var exported = Zayra.Api.Infrastructure.Finance.JournalExportBuilder.CompanyCodeOf(company);
        if (string.IsNullOrWhiteSpace(exported)) return;
        var lower = exported.ToLowerInvariant();
        var other = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.Companies, tenantId,
                "Exported company-code uniqueness across every legal entity of the tenant (only the colliding company's name is read)")
            .AsNoTracking()
            .Where(x => !x.IsDeleted && x.Id != company.Id
                     && ((x.ErpCompanyCode != null && x.ErpCompanyCode != "" && x.ErpCompanyCode.ToLower() == lower)
                         || ((x.ErpCompanyCode == null || x.ErpCompanyCode == "") && x.RegistrationNumber.ToLower() == lower)))
            .Select(x => x.LegalNameEn)
            .FirstOrDefaultAsync(ct);
        if (other is not null)
            throw new EmailDomainRefusedException("erp_company_code_taken", 409,
                $"{company.LegalNameEn} would be exported to your ERP under company code {exported}, which {other} already uses. "
                + "Give one of them its own ERP company code, or your ERP would book both companies' journals into one ledger.");
    }

    /// <summary>Saves, turning a lost race on ux_companies_tenant_erp_company_code into the same 409 the pre-check gives.</summary>
    private async Task SaveCompanyAsync(string? erpCompanyCode, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
                ConstraintName: Zayra.Api.Data.DatabaseIndexNames.CompanyTenantErpCode,
            })
        {
            throw new EmailDomainRefusedException("erp_company_code_taken", 409,
                $"ERP company code {erpCompanyCode} is already used by another company. Each company needs its own code.");
        }
    }

    private async Task EnsureCompanyUnique(Guid tenantId, string registrationNumber, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(registrationNumber);
        var holder = await _db.Companies
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.RegistrationNumber == clean && x.Id != excludedId)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (holder is not null) throw new CompanyRegistrationNumberTakenException(holder.Value);
    }

    private async Task EnsureBranchCodeUnique(Guid tenantId, Guid companyId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        var exists = await _db.Branches.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == companyId
            && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Branch code already exists for this company.");
    }

    private async Task EnsureDepartmentCodeUnique(Guid tenantId, Guid? companyId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        // Per company (Addendum B): the same code may exist in a sister company. Tenant-wide read so a row of a
        // company the caller cannot see still counts — the unique index would refuse it anyway.
        var exists = await ScopedBypass.TenantWide(_db.Departments, tenantId, OrgScopeJustification).AnyAsync(x => x.CompanyId == companyId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Department code already exists in this company.");
    }

    private async Task EnsureDesignationCodeUnique(Guid tenantId, Guid? companyId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        // Per company (Addendum B): the same code may exist in a sister company. Tenant-wide read so a row of a
        // company the caller cannot see still counts — the unique index would refuse it anyway.
        var exists = await ScopedBypass.TenantWide(_db.Designations, tenantId, OrgScopeJustification).AnyAsync(x => x.CompanyId == companyId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Designation code already exists in this company.");
    }

    private async Task EnsureGradeCodeUnique(Guid tenantId, Guid? companyId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        // Per company (Addendum B): the same code may exist in a sister company. Tenant-wide read so a row of a
        // company the caller cannot see still counts — the unique index would refuse it anyway.
        var exists = await ScopedBypass.TenantWide(_db.Grades, tenantId, OrgScopeJustification).AnyAsync(x => x.CompanyId == companyId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Grade code already exists in this company.");
    }

    private async Task EnsureCostCenterCodeUnique(Guid tenantId, Guid? companyId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = OrgCodes.Normalize(code);
        // Compared case-INSENSITIVELY on purpose. The column is normalised on write now, but a
        // tenant onboarded before that still holds rows the old importer stored verbatim; an exact
        // match would let "OPS" be created beside a legacy "ops" and re-open the collision.
        // Per company (owner decision 2026-10-23): the same code may exist in a sister company. Tenant-wide read so a
        // row of a company the caller cannot see still counts — the unique index would refuse it anyway.
        var exists = await ScopedBypass.TenantWide(_db.CostCenters, tenantId, OrgScopeJustification)
            .AnyAsync(x => x.CompanyId == companyId && !x.IsDeleted && x.Code.ToUpper() == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Cost center code already exists in this company.");
    }

    private async Task EnsureCompanyExists(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        if (!await _db.Companies.AnyAsync(x => x.TenantId == tenantId && x.Id == companyId && !x.IsDeleted, cancellationToken))
        {
            throw new InvalidOperationException("Company not found in this tenant.");
        }
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    // ── Addendum B: per-company organisation rows ────────────────────────────────────────────────────────

    private const string OrgScopeJustification =
        "Organisation setup checks a row of any company of the tenant: codes are unique per company and links must stay inside one company, whatever the caller's company switcher shows.";

    /// <summary>The company that owns a new department, designation or grade: the one the request names (it must be a
    /// live company of the tenant), else the company the request is narrowed to (the switcher), else the tenant's only company. A tenant with several companies must say which —
    /// <see cref="OrganizationRuleException.CompanyRequired"/>.</summary>
    public async Task<Guid> ResolveOwningCompanyAsync(Guid tenantId, Guid? requested, CancellationToken cancellationToken)
    {
        if (requested is Guid id)
        {
            await EnsureCompanyExists(tenantId, id, cancellationToken);
            return id;
        }
        // The company switcher's selection (or a caller holding one company) is the next best answer.
        if (_db.SingleScopedCompanyId is Guid selected
            && await ScopedBypass.TenantWide(_db.Companies, tenantId, OrgScopeJustification).AnyAsync(x => x.Id == selected && !x.IsDeleted, cancellationToken))
            return selected;
        var companies = await ScopedBypass.TenantWide(_db.Companies, tenantId, OrgScopeJustification)
                .Where(x => !x.IsDeleted)
            .Select(x => x.Id).Take(2).ToListAsync(cancellationToken);
        if (companies.Count == 1) return companies[0];
        throw new OrganizationRuleException(OrganizationRuleException.CompanyRequired,
            companies.Count == 0
                ? "Create a company first: departments, designations and grades belong to a company."
                : "Choose the company this belongs to.");
    }

    /// <summary>On edit the company is fixed once set. An unassigned legacy row (company NULL) takes the company the
    /// request names — that is how Setup assigns it.</summary>
    private async Task<Guid?> ResolveCompanyOnUpdateAsync(Guid tenantId, Guid? current, Guid? requested, CancellationToken cancellationToken)
    {
        if (current is Guid fixedCompany)
        {
            if (requested is Guid other && other != fixedCompany)
                throw new OrganizationRuleException(OrganizationRuleException.CompanyLocked,
                    "The company of this row can't be changed. Copy it to the other company instead.");
            return fixedCompany;
        }
        if (requested is Guid assign)
        {
            await EnsureCompanyExists(tenantId, assign, cancellationToken);
            return assign;
        }
        return null;
    }

    private async Task<string?> CompanyCurrencyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        var currency = await ScopedBypass.TenantWide(_db.Companies, tenantId, OrgScopeJustification)
                .Where(x => x.Id == companyId)
            .Select(x => x.DefaultCurrency).FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
    }

    private static OrganizationRuleException Mismatch(string what) =>
        new(OrganizationRuleException.CompanyMismatch, $"The {what} belongs to another company. Choose one of this company's.");

    /// <summary>A department's branch, parent and cost centre must belong to the department's own company.</summary>
    private async Task EnsureDepartmentLinksAsync(Guid tenantId, Guid? companyId, Guid? selfId, DepartmentRequest request, CancellationToken cancellationToken)
    {
        if (request.BranchId is Guid branchId)
        {
            var branchCompany = await ScopedBypass.TenantWide(_db.Branches, tenantId, OrgScopeJustification)
                .Where(x => x.Id == branchId && !x.IsDeleted)
                .Select(x => (Guid?)x.CompanyId).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Branch not found in this tenant.");
            if (branchCompany != companyId) throw Mismatch("branch");
        }
        if (request.ParentDepartmentId is Guid parentId)
        {
            if (parentId == selfId) throw new InvalidOperationException("A department cannot be its own parent.");
            var parent = await ScopedBypass.TenantWide(_db.Departments, tenantId, OrgScopeJustification)
                .Where(x => x.Id == parentId && !x.IsDeleted)
                .Select(x => new { x.CompanyId }).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Department not found in this tenant.");
            if (parent.CompanyId != companyId) throw Mismatch("parent department");
        }
        if (request.CostCenterId is Guid costCenterId)
        {
            var costCenter = await ScopedBypass.TenantWide(_db.CostCenters, tenantId, OrgScopeJustification)
                .Where(x => x.Id == costCenterId && !x.IsDeleted)
                .Select(x => new { x.CompanyId }).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Cost center not found in this tenant.");
            if (costCenter.CompanyId != companyId) throw Mismatch("cost centre");
        }
    }

    /// <summary>A designation's department and grade must belong to the designation's own company.</summary>
    private async Task EnsureDesignationLinksAsync(Guid tenantId, Guid? companyId, DesignationRequest request, CancellationToken cancellationToken)
    {
        if (request.DepartmentId is Guid departmentId)
        {
            var department = await ScopedBypass.TenantWide(_db.Departments, tenantId, OrgScopeJustification)
                .Where(x => x.Id == departmentId && !x.IsDeleted)
                .Select(x => new { x.CompanyId }).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Department not found in this tenant.");
            if (department.CompanyId != companyId) throw Mismatch("department");
        }
        if (request.GradeId is Guid gradeId)
        {
            var grade = await ScopedBypass.TenantWide(_db.Grades, tenantId, OrgScopeJustification)
                .Where(x => x.Id == gradeId && !x.IsDeleted)
                .Select(x => new { x.CompanyId }).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Grade not found in this tenant.");
            if (grade.CompanyId != companyId) throw Mismatch("grade");
        }
    }

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

/// <summary>A company registration number that another company of the tenant already holds. Still an
/// <see cref="InvalidOperationException"/> (with the same message) so every existing caller - importers,
/// the setup assistant - keeps working; the company forms catch it to put the message on the field.</summary>
public sealed class CompanyRegistrationNumberTakenException(Guid holderCompanyId)
    : InvalidOperationException("Company registration number already exists in this tenant.")
{
    public const string Code = "registration_number_taken";
    public const string Field = "registrationNumber";
    public const string UnavailableMessage = "This registration number can't be used.";

    /// <summary>The company that already holds the number.</summary>
    public Guid HolderCompanyId { get; } = holderCompanyId;

    /// <param name="holderVisible">Whether the caller may see the holding company. When not, the
    /// message does not confirm that another company holds the number.</param>
    public object ToPayload(bool holderVisible = true) =>
        new { error = Code, field = Field, message = holderVisible ? Message : UnavailableMessage };
}
