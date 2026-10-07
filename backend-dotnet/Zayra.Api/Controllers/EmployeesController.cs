using Microsoft.AspNetCore.DataProtection;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

using Zayra.Api.Infrastructure.Common;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private static readonly HashSet<string> SensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "salary", "bankName", "bankIban", "wpsBankDetails", "passportNumber", "passportExpiryDate", "visaNumber",
        "dateOfBirth", "salary", "bankName", "bankIban", "wpsBankDetails", "passportNumber", "passportIssueDate",
        "passportExpiryDate", "visaNumber", "visaIssueDate", "visaExpiryDate", "iqamaNumber", "muqeemNumber",
        // F02 — the GOSI first-registration date decides which contribution schedule a Saudi national is on,
        // so it takes the same maker-checker route as the GOSI reference beside it.
        "gosiFirstRegisteredOn",
        "gosiReference", "qiwaContractNumber", "emiratesId", "laborCardNumber", "visaFileNumber", "qid", "civilId", "residencyNumber",
        "residencyIssueDate", "workPermitNumber", "workPermitIssueDate", "medicalInformation", "disciplinaryRecords",
        "terminationReason",
        // The GPSSA/GRSIA/PIFSS/SPF/SIO counterpart of gosiReference (Saudi GOSI). Same class of value — the
        // social-insurance enrolment every contribution is filed against — so it takes the same maker-checker
        // route. It became writable through PUT when EmployeeChangeApplier gained its payroll-profile key;
        // without this entry that write would have skipped the approval gosiReference always required.
        "socialInsuranceReference",
        // Where the WPS/SIF line pays: the bank's routing code and the account number (payroll profile).
        "bankRoutingCode", "accountNumber"
    };

    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditService _audit;
    private readonly IDocumentStorage _documents;
    private readonly INotificationService _notifications;
    private readonly IHijriDateService _hijri;
    private readonly IDataScopeService _scopeService;
    private readonly ILetterService _letters;
    private readonly IHrLetterIssuer _letterIssuer;
    private readonly IApprovalWorkflowService _approvalWorkflow;
    private readonly ILogger<EmployeesController>? _logger;
    private readonly IEstablishmentGuard _establishmentGuard;
    private readonly IEmployeeActivationGuard _activationGuard;
    private readonly IEmployeeDuplicateDetector _duplicateDetector;
    private readonly IDraftHireMakers _draftHireMakers;

    public EmployeesController(ZayraDbContext db, IPasswordHasher passwordHasher, IAuditService audit, IDocumentStorage documents, INotificationService notifications, IHijriDateService hijri, IDataScopeService scopeService, ILetterService letters, IApprovalWorkflowService? approvalWorkflow = null, ILogger<EmployeesController>? logger = null, IEstablishmentGuard? establishmentGuard = null, IEmployeeActivationGuard? activationGuard = null, IEmployeeDuplicateDetector? duplicateDetector = null, IHrLetterIssuer? letterIssuer = null, IDraftHireMakers? draftHireMakers = null)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _documents = documents;
        _notifications = notifications;
        _hijri = hijri;
        _scopeService = scopeService;
        _letters = letters;
        // Optional and trailing so the ~10 hand-constructed EmployeesController instances across
        // the test suite keep compiling. The fallback is not a stub: HrLetterIssuer is a
        // stateless coordinator over exactly the three dependencies already passed above, so a
        // caller that does not supply one still gets the real behaviour.
        _letterIssuer = letterIssuer ?? new HrLetterIssuer(db, letters, documents);
        _approvalWorkflow = approvalWorkflow ?? new Zayra.Api.Infrastructure.Approvals.ApprovalWorkflowService(db, audit);
        _logger = logger;
        // Optional with concrete fallback (same pattern as _approvalWorkflow): DI supplies the
        // registered guard in production; direct constructions in tests keep compiling AND enforcing.
        _establishmentGuard = establishmentGuard ?? new EstablishmentGuardService(db);
        _activationGuard = activationGuard ?? new EmployeeActivationGuard(db);
        _duplicateDetector = duplicateDetector ?? new EmployeeDuplicateDetector(db);
        // Who made a hire (maker-checker on drafts): the draft's creator and editors plus, for an accepted
        // offer, its sender and acceptor. The fallback is the same set Program.cs registers, so a
        // hand-constructed controller cannot quietly apply a narrower rule.
        _draftHireMakers = draftHireMakers ?? new Zayra.Api.Infrastructure.Recruitment.OfferDraftHireMakers(db);
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<EmployeeListItemDto>>> Search([FromServices] IEmployeeManagementService employeeManagement, [FromQuery] string? search, [FromQuery] string? status, [FromQuery] string? department, [FromQuery] string? readiness = null, [FromQuery] Guid? importBatchId = null, [FromQuery] string? gapType = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = RequireTenant();
        var entityScope = this.GetEntityScope();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);

        if (scope.IsUnrestricted && entityScope.IsGroupLevel)
            return Ok(await employeeManagement.SearchAsync(tenantId, search, status, department, readiness, importBatchId, gapType, page, pageSize, cancellationToken));

        // Restricted scope: query directly and apply AllowedEmployeeIds and/or entity scope filter.
        // Exclude former employees (terminal statuses) — they belong to the Ex-Employees archive.
        var query = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted && !ExitEmployeeStatuses.Exit.Contains(e.Status));
        if (!scope.IsUnrestricted)
            query = query.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));
        if (!entityScope.IsGroupLevel)
        {
            var accessibleIds = entityScope.AccessibleCompanyIds;
            query = query.Where(e => e.CompanyId.HasValue && accessibleIds.Contains(e.CompanyId.Value));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Predicate MUST match the group-level list (EmployeeManagementService.SearchAsync) AND the
            // bulk resolver (ResolveTargetIdsAsync) column-for-column, otherwise a scoped user's
            // "select all matching + search" resolves a broader set than this list shows.
            var term = search.Trim();
            query = query.Where(e => e.EmployeeCode.Contains(term) || e.FullName.Contains(term)
                || e.EnglishName.Contains(term) || e.ArabicName.Contains(term)
                || (e.WorkEmail != null && e.WorkEmail.Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(department)) query = query.Where(e => e.Department == department);
        // SERVER-SIDE readiness / import-gap filter (fixes the page-local "Needs info" deep-link). Must be
        // applied in BOTH scope branches or a scoped user's post-import cleanup link breaks past page 1.
        query = EmployeeReadinessQuery.ApplyReadinessFilter(query, _db, tenantId, readiness, importBatchId, gapType);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(e => e.EmployeeCode).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new EmployeeListItemDto(e.Id, e.EmployeeCode, e.FullName, e.ArabicName ?? string.Empty, e.Department ?? string.Empty, e.Designation ?? string.Empty, string.IsNullOrEmpty(e.Branch) ? (_db.Branches.Where(b => b.Id == e.BranchId).Select(b => b.NameEn).FirstOrDefault() ?? string.Empty) : e.Branch, e.ManagerEmployeeId, e.Status, e.ProfileCompletenessScore, e.VisaExpiryDate, e.PassportExpiryDate, e.ReadinessState, e.ActivationBlockersCount, e.PublicId))
            .ToListAsync(cancellationToken);
        return Ok(new PagedResult<EmployeeListItemDto>(items, total, page, pageSize));
    }

    /// <summary>
    /// Read-only Ex-Employees registry: former staff whose records are retained for statutory audit.
    /// Membership = soft-deleted OR a terminal status (Archived / Offboarded / Terminated / Exited).
    /// Surfaces only directory + lifecycle metadata (no salary/bank/identity fields), mirroring the
    /// People list's non-sensitive projection and the same tenant + data + company scoping.
    /// </summary>
    [HttpGet("ex-employees")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<PagedResult<ExEmployeeListItemDto>>> ExEmployees(
        [FromQuery] string? search, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var tenantId = RequireTenant();
        var entityScope = this.GetEntityScope();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);

        // IgnoreQueryFilters() is REQUIRED to see IsDeleted rows; tenant + data-scope + operational
        // company scope are therefore re-applied here by hand. These are the tenant-isolation boundary.
        var query = _db.Employees.AsNoTracking().IgnoreQueryFilters().Where(e => e.TenantId == tenantId);
        query = query.Where(e => e.IsDeleted || ExitEmployeeStatuses.Exit.Contains(e.Status));
        if (!scope.IsUnrestricted)
            query = query.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));
        if (!entityScope.IsGroupLevel)
        {
            // Operational scope: a null CompanyId is invisible to a scoped user (poison-default rule).
            var accessibleIds = entityScope.AccessibleCompanyIds;
            query = query.Where(e => e.CompanyId.HasValue && accessibleIds.Contains(e.CompanyId.Value));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(e => e.EmployeeCode.Contains(term) || e.FullName.Contains(term)
                || e.EnglishName.Contains(term) || e.ArabicName.Contains(term)
                || (e.WorkEmail != null && e.WorkEmail.Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);

        var total = await query.CountAsync(cancellationToken);
        var pageRows = await query
            .OrderByDescending(e => e.DeletedAtUtc ?? e.UpdatedAtUtc)   // most-recent exits first
            .ThenBy(e => e.EmployeeCode)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new
            {
                e.Id, e.EmployeeCode, e.FullName, e.ArabicName, e.Department, e.Designation, e.Branch,
                e.Status, e.IsDeleted, e.DeletedAtUtc, e.UpdatedAtUtc, e.RetentionUntilUtc, e.PrivacyStatus
            })
            .ToListAsync(cancellationToken);

        var ids = pageRows.Select(r => r.Id).ToList();

        // Exit date sources: terminate writes an EmployeeStatusHistory row; offboarding writes the
        // status DIRECTLY (no history row) but stamps EmployeeOffboarding.CompletedAtUtc / LastWorkingDay.
        var statusExit = await _db.EmployeeStatusHistories.AsNoTracking()
            .Where(h => h.TenantId == tenantId && ids.Contains(h.EmployeeId) && ExitEmployeeStatuses.Exit.Contains(h.NewStatus))
            .GroupBy(h => h.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, When = g.Max(h => h.CreatedAtUtc) })
            .ToDictionaryAsync(x => x.EmployeeId, x => (DateTime?)x.When, cancellationToken);
        var offExit = await _db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && ids.Contains(o.EmployeeId))
            .GroupBy(o => o.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Completed = g.Max(o => o.CompletedAtUtc), LastWorkingDay = g.Max(o => (DateOnly?)o.LastWorkingDay) })
            .ToDictionaryAsync(x => x.EmployeeId, x => x, cancellationToken);

        var items = pageRows.Select(r =>
        {
            DateTime? exit = r.UpdatedAtUtc; // fallback
            if (r.IsDeleted)
                exit = r.DeletedAtUtc;
            else if (string.Equals(r.Status, EmployeeStatuses.Archived, StringComparison.OrdinalIgnoreCase)
                     && offExit.TryGetValue(r.Id, out var offA) && offA.Completed.HasValue)
                exit = offA.Completed;
            else if (string.Equals(r.Status, EmployeeStatuses.Offboarded, StringComparison.OrdinalIgnoreCase)
                     && offExit.TryGetValue(r.Id, out var offO) && offO.LastWorkingDay.HasValue)
                exit = offO.LastWorkingDay.Value.ToDateTime(TimeOnly.MinValue);
            else if (statusExit.TryGetValue(r.Id, out var when))
                exit = when;
            return new ExEmployeeListItemDto(
                r.Id, r.EmployeeCode, r.FullName, r.ArabicName, r.Department, r.Designation, r.Branch,
                r.Status, r.IsDeleted, exit, r.RetentionUntilUtc, r.PrivacyStatus);
        }).ToList();

        return Ok(new PagedResult<ExEmployeeListItemDto>(items, total, page, pageSize));
    }

    // ── Configurable export / import / shareable template ────────────────────────
    // The CSV column set is DERIVED from EmployeeFieldRegistry (the single source of truth §3.2) — the
    // template, the export header, and the importer header-validation all read this ONE ordered list, so
    // a column can never exist on one surface and be missing from another. Adding a field to the catalog
    // adds it to every CSV surface automatically; there is no hand-maintained header array to drift.
    private static IReadOnlyList<string> EmployeeCsvHeaders =>
        Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.CsvHeaders;

    // The template's example row comes from the SAME registry, derived from the same ordered catalog in
    // the same pass — never a hardcoded row. A literal example row is positional, so it would silently
    // shift one column per field added to the catalog: precisely the drift the hand-maintained header
    // array was deleted to prevent, reintroduced one line lower down.
    private static IReadOnlyList<string> EmployeeCsvExampleRow =>
        Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.CsvExampleRow;

    // A directory export is useful to HR operations and auditors, but it must not silently become a
    // payroll/identity-document dump. Only the effective employees.sensitive claim (after per-user Deny
    // overrides are applied at token issuance) unlocks these columns. Role names are deliberately not a
    // bypass: denying employees.sensitive from an Admin/Payroll Officer must also mask their export.
    private static readonly HashSet<string> SensitiveEmployeeExportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "PersonalEmail", "Phone", "DateOfBirth", "MaritalStatus", "EmergencyContactName", "EmergencyContactPhone",
        "BasicSalary", "HousingAllowance", "TransportAllowance", "FoodAllowance", "MobileAllowance", "OtherAllowance",
        "FixedDeduction", "PaymentMethod", "IBAN", "AccountNumber", "BankName", "BankRoutingCode", "MolId",
        "SocialInsuranceReference", "PassportNumber", "PassportIssueDate", "PassportExpiryDate", "VisaNumber",
        "VisaIssueDate", "VisaExpiryDate", "VisaFileNumber", "IqamaNumber", "IqamaExpiry", "MuqeemNumber",
        "GosiReference", "QiwaContractNumber", "EmiratesId", "EmiratesIdExpiry", "LaborCardNumber", "Qid",
        "QidExpiry", "CivilId", "CivilIdExpiry", "WorkPermitNumber", "WorkPermitIssueDate", "ResidencyNumber",
        "ResidencyIssueDate", "IdNumber", "SponsorName", "ContractReference", "WorkPermitReference", "QiwaEmployeeReference"
    };

    // Whole-tenant people export (PII, plus payroll and bank columns with employees.sensitive). Owner decision: only
    // the HR roles that maintain the records (employees.write: Admin, HR Director, HR Manager, HR Officer). The
    // resolver inferred employees.documents, which also let Compliance Officer export the whole tenant; Payroll
    // Officer and Auditor were named but never held that key. Compliance keeps People Search.
    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var entityScope = this.GetEntityScope();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var exportQuery = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted);
        if (!scope.IsUnrestricted)
            exportQuery = exportQuery.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));
        if (!entityScope.IsGroupLevel)
        {
            var accessibleIds = entityScope.AccessibleCompanyIds;
            exportQuery = exportQuery.Where(e => e.CompanyId.HasValue && accessibleIds.Contains(e.CompanyId.Value));
        }
        var emps = await exportQuery.OrderBy(e => e.EmployeeCode).ToListAsync(ct);
        var includesSensitive = User.HasPermission("employees.sensitive");
        var csv = await BuildEmployeesCsvAsync(emps, tenantId, includesSensitive, ct);
        // Export audit: actor, row count, and company-scope dimension — no PII values.
        await _audit.WriteAsync("employees.exported", "Employee", "bulk", Context(),
            JsonSerializer.Serialize(new
            {
                rowCount = emps.Count,
                groupScope = entityScope.IsGroupLevel,
                companyIds = entityScope.IsGroupLevel ? null : entityScope.AccessibleCompanyIds,
                exportType = "employees_csv",
                includesSensitive,
            }), ct);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"employees_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Builds the employee CSV (registry-ordered columns + payroll/salary/compliance joins) for a resolved
    /// set of employees. Extracted from <see cref="Export"/> so the People-list export and the bulk
    /// "export selected" path emit byte-identical output from ONE builder — a column can never drift between
    /// the two surfaces. Loads only the given rows' related data (never an unfiltered tenant scan).
    /// </summary>
    private async Task<string> BuildEmployeesCsvAsync(IReadOnlyList<Employee> emps, Guid tenantId, bool includeSensitive, CancellationToken ct)
    {
        var empIds = emps.Select(e => e.Id).ToList();
        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && empIds.Contains(p.EmployeeId) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.EmployeeId, ct);
        var salaryRows = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && empIds.Contains(s.EmployeeId) && s.IsActive)
            .ToListAsync(ct);
        var salaries = salaryRows
            .GroupBy(s => s.EmployeeId)
            .Select(g => g.OrderByDescending(s => s.EffectiveDate).First())
            .ToDictionary(s => s.EmployeeId);
        var structures = await _db.SalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.IsDeleted)
            .ToDictionaryAsync(s => s.Id, ct);
        var positionCodes = await _db.Positions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        // ORG PLACEMENT — resolved from the FKs, not hardcoded blank. CompanyLegalName, BranchCode,
        // DepartmentCode, ManagerEmployeeCode and SupervisorEmployeeCode used to be emitted as
        // string.Empty and ManagerEmail/SupervisorEmail were absent from the value map entirely (the only
        // two of the 92 headers missing), so an export → re-import round trip reassigned every person to
        // the importer's default company and erased every reporting line. Each lookup is the exact shape
        // the importer resolves BY (EmployeeImportRowResolver: company by LegalNameEn, branch/department
        // by Code; Pass 2: manager/supervisor by employee code, email as fallback), so the file a customer
        // exports can be re-imported without moving anybody.
        var companyNames = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);
        var branchCodes = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted)
            .ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var departmentCodes = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        // Line managers/supervisors are frequently OUTSIDE the exported set (a scoped export, a single
        // department), so they are loaded by the referenced IDs rather than read off `emps`.
        var hierarchyIds = emps
            .SelectMany(e => new[] { e.ManagerEmployeeId, e.SupervisorEmployeeId })
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var hierarchyRefs = hierarchyIds.Count == 0
            ? new Dictionary<int, (string Code, string Email)>()
            : (await _db.Employees.AsNoTracking()
                .Where(x => x.TenantId == tenantId && hierarchyIds.Contains(x.Id))
                .Select(x => new { x.Id, x.EmployeeCode, x.WorkEmail })
                .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (Code: x.EmployeeCode, Email: x.WorkEmail));
        var headers = EmployeeCsvHeaders;
        var rows = emps.Select(e =>
        {
            profiles.TryGetValue(e.Id, out var profile);
            salaries.TryGetValue(e.Id, out var salary);
            var structureCode = salary is not null && structures.TryGetValue(salary.SalaryStructureId, out var structure) ? structure.Code : string.Empty;
            var positionCode = e.PositionId is not null && positionCodes.TryGetValue(e.PositionId.Value, out var pc) ? pc : string.Empty;
            var companyLegalName = e.CompanyId is not null && companyNames.TryGetValue(e.CompanyId.Value, out var cn) ? cn : string.Empty;
            var branchCode = e.BranchId is not null && branchCodes.TryGetValue(e.BranchId.Value, out var bc) ? bc : string.Empty;
            var departmentCode = e.DepartmentId is not null && departmentCodes.TryGetValue(e.DepartmentId.Value, out var dc) ? dc : string.Empty;
            var manager = e.ManagerEmployeeId is not null && hierarchyRefs.TryGetValue(e.ManagerEmployeeId.Value, out var mgr) ? mgr : default;
            var supervisor = e.SupervisorEmployeeId is not null && hierarchyRefs.TryGetValue(e.SupervisorEmployeeId.Value, out var sup) ? sup : default;
            // Value map keyed by CSV header — the row is projected in registry order below, so a header
            // added/reordered in the catalog can never misalign the export (replaces the old positional
            // object[] that had to be kept in lock-step with the header array by hand).
            static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? string.Empty;
            var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EmployeeCode"] = e.EmployeeCode,
                ["CompanyLegalName"] = companyLegalName,
                ["BranchCode"] = branchCode,
                ["CostCenterCode"] = e.CostCenter,
                ["WorkLocation"] = e.WorkLocation,
                ["FullName"] = e.FullName,
                ["ArabicName"] = e.ArabicName,
                ["PreferredName"] = e.PreferredName,
                ["WorkEmail"] = e.WorkEmail,
                ["PersonalEmail"] = e.PersonalEmail,
                ["Phone"] = e.Phone,
                ["Gender"] = e.Gender,
                ["DateOfBirth"] = Iso(e.DateOfBirth),
                ["Nationality"] = e.Nationality,
                ["MaritalStatus"] = e.MaritalStatus,
                ["CountryCode"] = e.CountryCode,
                ["EmergencyContactName"] = e.EmergencyContactName,
                ["EmergencyContactPhone"] = e.EmergencyContactPhone,
                ["Department"] = e.Department,
                ["DepartmentCode"] = departmentCode,
                ["Designation"] = e.Designation,
                ["JobTitle"] = e.JobTitle,
                ["EmploymentType"] = e.EmploymentType,
                ["ContractType"] = e.ContractType,
                ["Grade"] = e.Grade,
                ["PositionCode"] = positionCode,
                ["ManagerEmployeeCode"] = manager.Code ?? string.Empty,
                ["ManagerEmail"] = manager.Email ?? string.Empty,
                ["SupervisorEmployeeCode"] = supervisor.Code ?? string.Empty,
                ["SupervisorEmail"] = supervisor.Email ?? string.Empty,
                ["Status"] = e.Status,
                ["JoiningDate"] = e.JoiningDate.ToString("yyyy-MM-dd"),
                ["ConfirmationDate"] = Iso(e.ConfirmationDate),
                ["ProbationStartDate"] = Iso(e.ProbationStartDate),
                ["ProbationEndDate"] = Iso(e.ProbationEndDate),
                ["ContractStartDate"] = Iso(e.ContractStartDate),
                ["ContractEndDate"] = Iso(e.ContractEndDate),
                ["NoticePeriodDays"] = e.NoticePeriodDays,
                ["ShiftPolicyCode"] = e.ShiftPolicyCode,
                ["LeavePolicyCode"] = e.LeavePolicyCode,
                ["AttendancePolicyCode"] = e.AttendancePolicyCode,
                ["SalaryStructureCode"] = structureCode,
                ["BasicSalary"] = salary?.BasicSalary,
                ["HousingAllowance"] = salary?.HousingAllowance,
                ["TransportAllowance"] = salary?.TransportAllowance,
                ["FoodAllowance"] = salary?.FoodAllowance,
                ["MobileAllowance"] = salary?.MobileAllowance,
                ["OtherAllowance"] = salary?.OtherAllowance,
                ["FixedDeduction"] = salary?.FixedDeduction,
                ["Currency"] = salary?.Currency ?? profile?.SalaryCurrency,
                ["PayrollGroup"] = profile?.PayrollGroup,
                ["PaymentMethod"] = profile?.PaymentMethod,
                ["IBAN"] = profile?.Iban,
                ["AccountNumber"] = profile?.AccountNumber,
                ["BankName"] = profile?.BankName,
                ["BankRoutingCode"] = profile?.BankRoutingCode,
                ["MolId"] = profile?.MolId,
                ["SocialInsuranceReference"] = profile?.SocialInsuranceReference,
                ["PassportNumber"] = e.PassportNumber,
                ["PassportIssueDate"] = Iso(e.PassportIssueDate),
                ["PassportExpiryDate"] = Iso(e.PassportExpiryDate),
                ["VisaNumber"] = e.VisaNumber,
                ["VisaIssueDate"] = Iso(e.VisaIssueDate),
                ["VisaExpiryDate"] = Iso(e.VisaExpiryDate),
                ["VisaFileNumber"] = e.VisaFileNumber,
                ["IqamaNumber"] = e.IqamaNumber,
                ["IqamaExpiry"] = Iso(e.IqamaExpiryDate),
                ["MuqeemNumber"] = e.MuqeemNumber,
                ["GosiReference"] = e.GosiReference,
                ["QiwaContractNumber"] = e.QiwaContractNumber,
                ["EmiratesId"] = e.EmiratesId,
                ["EmiratesIdExpiry"] = Iso(e.EmiratesIdExpiryDate),
                ["LaborCardNumber"] = e.LaborCardNumber,
                ["Qid"] = e.Qid,
                ["QidExpiry"] = Iso(e.QidExpiryDate),
                ["CivilId"] = e.CivilId,
                ["CivilIdExpiry"] = Iso(e.CivilIdExpiryDate),
                ["WorkPermitNumber"] = e.WorkPermitNumber,
                ["WorkPermitIssueDate"] = Iso(e.WorkPermitIssueDate),
                ["ResidencyNumber"] = e.ResidencyNumber,
                ["ResidencyIssueDate"] = Iso(e.ResidencyIssueDate),
                ["IdNumber"] = e.IdNumber,
                ["SponsorName"] = e.SponsorName,
                ["SaudiOrNonSaudi"] = e.SaudiOrNonSaudi,
                ["IdType"] = e.IdType,
                ["OccupationCode"] = e.OccupationCode,
                ["EstablishmentId"] = e.EstablishmentId,
                ["WorkLocationId"] = e.WorkLocationId,
                ["ContractReference"] = e.ContractReference,
                ["WorkPermitReference"] = e.WorkPermitReference,
                ["QiwaEmployeeReference"] = e.QiwaEmployeeReference,
                ["QiwaSyncStatus"] = e.QiwaSyncStatus,
            };
            return (IReadOnlyList<object?>)headers
                .Select(h => !includeSensitive && SensitiveEmployeeExportHeaders.Contains(h)
                    ? string.Empty
                    : v.GetValueOrDefault(h))
                .ToList();
        });
        return Csv.Build(headers, rows);
    }

    /// <summary>Downloadable template — the shareable "data format" to fill and import, with one
    /// registry-derived placeholder row showing the expected shape of every column.</summary>
    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(EmployeeCsvHeaders, EmployeeCsvExampleRow)), "text/csv", "employees_import_template.csv");

    /// <summary>
    /// The Employee Field RESOLVER (§3.1/§3.3) — the ONE backend source of truth for the create/edit modal
    /// and the CSV template, resolved on TWO axes: COUNTRY (the employing legal entity) × NATIONALITY (the
    /// person, national vs expat). Joins the field CATALOG (shape/label/binding) ⋈ the readiness FLOOR/policy
    /// (visible/required/gate — via the SAME proven merge pipeline the activation gate uses) ⋈ the country
    /// pack (identity-document FORMAT regex). A Saudi national never receives an Iqama descriptor; a UAE hire
    /// never an Iqama; each field carries its correct local English name (Emirates ID, QID, Bahrain CPR, …).
    /// `required`/`gate` are ADVISORY UX only — the authoritative gate stays server-side EnsureActivatable at
    /// Save→Activate; this never becomes a client create-gate. Explicit countryCode wins, else the company's
    /// country; nationality defaults to non-GCC-expat treatment when blank (fail-safe).
    /// </summary>
    [HttpGet("field-catalog")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer")]
    public async Task<IActionResult> FieldCatalog(
        [FromQuery] Guid? companyId, [FromQuery] string? countryCode, [FromQuery] string? nationality,
        CancellationToken ct = default,
        [FromServices] Zayra.Api.Application.CountryPack.ICountryPackResolver? countryPacks = null)
    {
        var tenantId = RequireTenant();
        // Explicit countryCode wins, else the company's — this endpoint publishes that rule in its own
        // response, and it is now the SAME helper every write path and the readiness resolver use, so
        // the documentation and the behaviour cannot drift apart again.
        var iso = (countryCode ?? string.Empty).Trim();
        var companyCountry = string.IsNullOrEmpty(iso) && companyId is Guid cid
            ? await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == cid)
                .Select(c => c.CountryCode).FirstOrDefaultAsync(ct)
            : null;
        var iso2 = HomeJurisdiction.DeriveEmployeeCountry(iso, companyCountry);

        // Requiredness/gate from the merged policy (floor ∪ tenant ∪ company ∪ gcc-setting, strictest-wins).
        var policy = await _activationGuard.ResolvePolicyAsync(tenantId, companyId, iso2, nationality, ct);
        var reqByKey = policy.Items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
        // Identity-document FORMAT from the country pack (conservative regex + hint; null ⇒ no constraint).
        Zayra.Api.Application.CountryPack.IIdentityDocumentFormat fmt =
            countryPacks?.ResolveIdentityDocumentFormat(iso2, string.Empty)
            ?? new Zayra.Api.Infrastructure.CountryPack.DefaultIdentityDocumentFormat();

        static string CamelKey(string k) => k.Length > 0 ? char.ToLowerInvariant(k[0]) + k[1..] : k;
        var descriptors = Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.CatalogFor(iso2, nationality)
            .Select(d =>
            {
                reqByKey.TryGetValue(d.Key, out var req);
                var (pattern, hint) = fmt.GetFormat(d.Key);
                return new
                {
                    key = CamelKey(d.Key),
                    registryKey = d.Key,
                    label = Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.LabelFor(d, iso2),
                    section = d.Section,
                    inputType = d.InputType,
                    sensitive = d.Sensitive,
                    csvHeader = d.CsvHeader,
                    activationRelevant = d.ActivationRelevant,
                    countries = d.Countries,
                    binding = d.Binding,
                    applicability = d.Applicability.ToString(),
                    visible = true,                                   // CatalogFor already filtered to visible-for-(country,nationality)
                    required = req is not null && req.FailClosed,      // advisory only — server EnsureActivatable is authoritative
                    gate = req?.Gate,                                 // "activate" | "pay" | null
                    pattern,
                    patternHint = hint,
                    complianceFieldKey = d.ComplianceFieldKey,
                    // Value/expiry edit-keys the modal binds a statutory field to (FE EmployeeComplianceField).
                    // These become PATCH keys for ApplyChanges, so they must name the STORAGE TARGET — which
                    // is what `Binding` records, NOT the registry key. The two diverge for every GCC card
                    // expiry: registry keys `IqamaExpiry`/`EmiratesIdExpiry`/`QidExpiry`/`CivilIdExpiry` (the
                    // readiness keys and CSV headers) bind to columns `emp.IqamaExpiryDate`/… . Deriving from
                    // `d.Key` emitted `iqamaExpiry`, for which ApplyChanges has no case, so the edit would
                    // have been accepted with 200 and dropped. EmployeeFieldWiringTests is the CI guard.
                    entityKey = d.ComplianceFieldKey is null ? null : EmployeeEditKey(d),
                    expiryEntityKey = ExpiryEditKeyFor(d),
                };
            })
            .ToList();

        return Ok(new
        {
            countryCode = iso2,
            nationality = policy.Nationality,
            tier = policy.Tier,
            disclaimer = policy.Disclaimer,
            fields = descriptors,
        });
    }

    /// <summary>
    /// The edit-modal PATCH key for a catalog descriptor: the camelCased <c>Employee</c> column named by
    /// the descriptor's <c>Binding</c> (<c>"emp.X"</c> → <c>"x"</c>). Returns null for a field that does not
    /// live on the <c>Employee</c> entity (payroll profile, salary structure, org lookup), which the edit
    /// modal cannot patch through <see cref="ApplyChanges"/> anyway — emitting a key for one would render an
    /// input whose value goes nowhere. Public so the contract test can enumerate the same surface.
    /// </summary>
    internal static string? EmployeeEditKey(Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.EmployeeFieldDescriptor d)
    {
        const string prefix = "emp.";
        if (d.Binding is null || !d.Binding.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var column = d.Binding[prefix.Length..];
        return column.Length > 0 ? char.ToLowerInvariant(column[0]) + column[1..] : null;
    }

    /// <summary>The edit-modal PATCH key for a descriptor's PAIRED expiry, resolved through the paired
    /// descriptor's own <c>Binding</c> (never through its key — see <see cref="FieldCatalog"/>).</summary>
    internal static string? ExpiryEditKeyFor(Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.EmployeeFieldDescriptor d)
    {
        if (d.ExpiryKey is null) return null;
        var paired = Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.Catalog
            .FirstOrDefault(x => string.Equals(x.Key, d.ExpiryKey, StringComparison.Ordinal));
        return paired is null ? null : EmployeeEditKey(paired);
    }

    [HttpPost("import")]
    [HasPermission("employees.bulk_import")]
    public Task<IActionResult> Import([FromBody] ImportEmployeesRequest req, CancellationToken ct,
        [FromServices] Zayra.Api.Application.CountryPack.ICountryPackResolver? countryPacks = null) =>
        RunEmployeeImportAsync(req, dryRun: false, ct, countryPacks);

    /// <summary>
    /// The most rows one employee file may carry. The import holds the whole file — every row's employee, payroll
    /// profile, salary structure, gaps and audit — in one transaction and one change tracker, so its memory grows
    /// with the file. At 2,000 rows a 304 MB heap peaked at ~280 MB — too thin a margin for a process that shares its
    /// heap with live requests — so the cap is 1,000 (measured peak in the PR). A larger file is
    /// refused up front, with a coded message, rather than risking an out-of-memory restart half-way.
    /// </summary>
    internal const int MaxImportRows = 1_000;

    private IActionResult? TooManyRows(int count) => count <= MaxImportRows ? null : UnprocessableEntity(new
    {
        error = "import_too_many_rows",
        message = $"This file has {count:N0} rows; one employee import takes at most {MaxImportRows:N0}. "
                  + $"Split the file into parts of up to {MaxImportRows:N0} rows (keep the header row in each part) and import them one after another. "
                  + "Nothing from this file was imported.",
        received = count,
        maxRows = MaxImportRows,
        created = 0,
    });

    /// <summary>
    /// THE employee import — the commit, and the dry run the preview reports from. <paramref name="dryRun"/> runs
    /// every step of the commit (resolution, row gate, storage guard, every save, the audit rows) inside the same
    /// single transaction and then ROLLS IT BACK, so "what the preview says" is literally what the commit would
    /// do with this file against this database, not a second validator's opinion of it. A dry run ignores the
    /// import key (it neither replays nor records a batch) and is only possible on a relational provider.
    /// </summary>
    private async Task<IActionResult> RunEmployeeImportAsync(ImportEmployeesRequest req, bool dryRun, CancellationToken ct,
        Zayra.Api.Application.CountryPack.ICountryPackResolver? countryPacks = null)
    {
        var tenantId = RequireTenant();
        // HEADERS FIRST — before a single row is read, let alone written. An unrecognised column used to
        // import "cleanly" and lose its data (see EmployeeCsvHeaderValidator).
        if (HeaderRejection(req.CsvContent) is IActionResult headerRejection) return headerRejection;
        // Then the SHAPE of every row: a row with more or fewer cells than the header (an unquoted "8,000") would
        // shift every later value into the wrong column. Every such row is named; nothing is read from the file.
        if (ParseImportRows(req.CsvContent, out var rows) is IActionResult shapeRejection) return shapeRejection;
        if (TooManyRows(rows.Count) is IActionResult tooMany) return tooMany;
        if (req.ConfirmReimport && string.IsNullOrWhiteSpace(req.ReimportReason))
            return UnprocessableEntity(new
            {
                error = "reimport_reason_required",
                message = "Importing the same file again needs a reason (it is recorded in the audit trail). Nothing was imported.",
            });
        EarlierImport? earlierCommit = null;

        // ── ONE TRANSACTION FOR THE WHOLE FILE, AND A COMMIT THAT IS SAFE TO RETRY ─────────────────────
        // The import writes at several persistence boundaries (employees, position assignments, payroll
        // profiles + salary structures, Pass-2 links + readiness re-stamp + EmployeeImportGap rows, audits).
        // Each one used to commit on its own — and the per-row code generator committed inside the loop on top
        // of that — so a failure at a later boundary returned 422 "import_persist_failed" AFTER the people were
        // already committed, with no salaries and no review gaps. Everything now runs in ONE transaction that
        // commits only when the endpoint returns 200; every error path rolls the whole file back.
        //
        // RETRY. Program.cs enables EnableRetryOnFailure, so this runs inside the execution strategy and a
        // transient error re-runs the whole attempt from a clean tracker. The one dangerous case is a transient
        // error DURING CommitAsync: the commit may have landed before the connection dropped, and a blind re-run
        // would import the file a second time (every auto-coded row twice). The last write of every successful
        // attempt is therefore a marker audit row (employee.import_committed, EntityId = importBatchId) in the
        // SAME transaction; before the strategy re-runs a failed commit it asks whether that marker exists, and
        // if it does it returns the first attempt's result instead of importing again.
        //
        // CLIENT KEY. A client may send ImportKey (a Guid it generates once per file and re-sends on retry). It
        // becomes the importBatchId, so the same marker also answers "was this file already imported?" across
        // HTTP requests: a repeated key replays the recorded summary (200, replayed: true) and imports nothing;
        // the same key with a DIFFERENT file is refused (409). Omitting the key keeps the old behaviour.
        var contentSha256 = ImportContentSha256(req.CsvContent);
        var clientKey = !dryRun && req.ImportKey is { } suppliedKey && suppliedKey != Guid.Empty ? suppliedKey : (Guid?)null;
        var importBatchId = clientKey ?? Guid.NewGuid();

        if (!_db.Database.IsRelational())
        {
            // A dry run needs a transaction to roll back; the in-memory test double has none.
            if (dryRun) throw new InvalidOperationException("An import dry run needs a relational database.");
            // Non-relational providers (the in-memory test double) have no transactions: run the body directly.
            if (clientKey is not null && await FindCommittedImportAsync(tenantId, importBatchId, ct) is { } landedInMemory)
                return ImportReplay(landedInMemory, contentSha256, importBatchId);
            if (await RefuseSameContentAsync(ct) is IActionResult sameContentInMemory) return sameContentInMemory;
            return await RunImportAsync();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
        return await strategy.ExecuteAsync(
            new ImportCommitAttempt(),
            async (_, attempt, token) =>
            {
                // A retried attempt must not re-save the previous attempt's entities: every attempt starts from a
                // clean tracker and re-reads the database.
                _db.ChangeTracker.Clear();
                attempt.Outcome = null;
                attempt.CommitInFlight = false;
                await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
                IActionResult outcome;
                try
                {
                    // A PREVIEW never queues behind a running import or hire for long: it waits at most 5 s for any
                    // lock (the ID-rule row, the audit chain, the establishment cells) and then says so (import_busy).
                    if (dryRun && _db.Database.IsNpgsql())
                        await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", token);
                    if (clientKey is not null)
                    {
                        // Two submissions carrying the same key serialize here; the second one then finds the
                        // first one's marker and replays its summary instead of importing the file again.
                        if (_db.Database.IsNpgsql())
                            await _db.Database.ExecuteSqlInterpolatedAsync(
                                $"SELECT pg_advisory_xact_lock({ImportKeyLockKey(tenantId, importBatchId)})", token);
                        if (await FindCommittedImportAsync(tenantId, importBatchId, token) is { } landed)
                        {
                            await tx.RollbackAsync(token);
                            return ImportReplay(landed, contentSha256, importBatchId);
                        }
                    }
                    // Two submissions of the same file under DIFFERENT keys (or none) serialize here, so the second
                    // sees the first's commit marker and is refused as a re-import.
                    if (!dryRun && _db.Database.IsNpgsql())
                        await _db.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_advisory_xact_lock({ImportContentLockKey(tenantId, contentSha256)})", token);
                    if (await RefuseSameContentAsync(token) is IActionResult sameContent)
                    {
                        await tx.RollbackAsync(token);
                        return sameContent;
                    }
                    outcome = await RunImportAsync();
                }
                // A preview that waited its 5 s for a lock answers "busy" HERE, inside the attempt: rethrown, the
                // retrying strategy would treat the timeout as transient and wait again, six times over.
                catch (Exception ex) when (dryRun && IsLockTimeout(ex))
                {
                    _db.ChangeTracker.Clear();
                    try { await tx.RollbackAsync(token); } catch { /* the transaction is already aborted */ }
                    return ImportBusy();
                }
                catch
                {
                    _db.ChangeTracker.Clear();
                    throw;
                }
                if (outcome is OkObjectResult && !dryRun)
                {
                    attempt.Outcome = outcome;
                    attempt.CommitInFlight = true;
                    await tx.CommitAsync(token);
                    attempt.CommitInFlight = false;
                }
                else
                {
                    await tx.RollbackAsync(token);
                    _db.ChangeTracker.Clear();
                }
                return outcome;
            },
            async (_, attempt, token) =>
            {
                // Called by the strategy ONLY after a transient error escaped the operation. When that error hit
                // CommitAsync, the marker row decides whether the commit landed (see RETRY above).
                if (!attempt.CommitInFlight || attempt.Outcome is null)
                    return new ExecutionResult<IActionResult>(false, null!);
                var landed = await FindCommittedImportAsync(tenantId, importBatchId, token) is not null;
                return new ExecutionResult<IActionResult>(landed, attempt.Outcome);
            },
            ct);
        }
        catch (Exception ex) when (IsLockTimeout(ex))
        {
            _db.ChangeTracker.Clear();
            return ImportBusy();
        }

        IActionResult ImportBusy() => Conflict(new
        {
            error = "import_busy",
            message = "Another employee import or a new hire is being saved right now, so this check could not run. "
                      + "Nothing was imported — try again in a moment.",
        });

        // The re-import guard (P1): the SAME file content already committed for this tenant, under any import key.
        // Re-uploading a file without employee codes used to create every person a second time (each row got a
        // fresh generated code). Refused unless the request says, with a reason, that it means to import it again.
        async Task<IActionResult?> RefuseSameContentAsync(CancellationToken token)
        {
            // A file whose every row carries an EmployeeCode re-imports idempotently (existing codes are matched,
            // never duplicated), so only a file with code-less rows can double anyone.
            var hasCodelessRows = rows.Any(r => !string.IsNullOrWhiteSpace(r.GetValueOrDefault("FullName", string.Empty))
                                                && string.IsNullOrWhiteSpace(r.GetValueOrDefault("EmployeeCode", string.Empty)));
            if (!hasCodelessRows) return null;
            var earlier = await FindEarlierCommitOfContentAsync(tenantId, contentSha256, importBatchId, token);
            earlierCommit = earlier;
            if (earlier is null || req.ConfirmReimport) return null;
            var by = string.IsNullOrWhiteSpace(earlier.ImportedBy) ? "an unknown user" : earlier.ImportedBy;
            return Conflict(new
            {
                error = "import_already_committed",
                message = $"This exact file was already imported on {earlier.ImportedAtUtc:yyyy-MM-dd HH:mm} UTC by {by} "
                          + $"({earlier.Created} created). Importing it again would add every row without an EmployeeCode a second time. "
                          + "Nothing was imported. If you really mean to import it again, confirm the re-import and give a reason.",
                earlierImportBatchId = earlier.ImportBatchId,
                earlierImportedAtUtc = earlier.ImportedAtUtc,
                earlierImportedBy = by,
                earlierCreated = earlier.Created,
            });
        }

        async Task<IActionResult> RunImportAsync()
        {
            // Enforce employee limit before processing any rows.
            var sub = await _db.TenantSubscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

            // Active-seat budget (P1-4): MaxEmployees caps ACTIVE employees, so only rows that will land Active
            // consume a seat. Draft/incomplete rows import freely (they occupy no seat until completed+activated),
            // matching the "imported inactive until complete" model — the whole file is NOT rejected upfront.
            // A complete row that would land Active with no seat left is downgraded to Draft + warning below.
            int activeSeatsBudget = sub is not null && sub.MaxEmployees > 0
                ? Math.Max(0, sub.MaxEmployees - await _db.Employees.CountAsync(e => e.TenantId == tenantId && e.Status == EmployeeStatuses.Active && !e.IsDeleted, ct))
                : int.MaxValue;
            int activeSeatsConsumed = 0;

            // SHARED master-data lookups — the SAME loader ImportPreview uses, so dry-run resolution == commit.
            var lookups = await EmployeeImportRowResolver.LoadImportLookupsAsync(_db, tenantId, ct);
            var defaultCompany = lookups.DefaultCompany;

            // ── Establishment matrix preloads (per-level budget row check, spec §5.2) ─────
            // Same cumulative intra-batch pattern as claimedPositionCodes: file order wins — first
            // rows fit, later rows fail deterministically with counts. Loaded via the SHARED evaluator
            // ImportPreview also uses, so dry-run establishment projection == commit landing.
            var establishmentContext = await EmployeeImportEstablishmentEvaluator.LoadAsync(_db, _establishmentGuard, tenantId, ct);
            var establishmentMode = establishmentContext.Mode;
            var levelBudgets = establishmentContext.LevelBudgets;
            var levelByDesignation = establishmentContext.LevelByDesignation;
            var levelNamesById = establishmentContext.LevelNamesById;
            var deptNameById = establishmentContext.DeptNameById;
            var claimedLevelSlots = new Dictionary<(Guid Dept, Guid Level), int>();
            var establishmentBlockedRows = new List<(int RowNum, Guid DeptId, Guid LevelId, int Budgeted, int Current)>();

            int created = 0, skipped = 0;
            // THE LAW (pilot-readiness revision): a row the import cannot take refuses the WHOLE file (the ROW GATE
            // below). What is still "skipped" is never a person lost: an all-blank row (skippedNoName) or a row
            // matching an existing employee with nothing to fill / a separated one (skippedDupCode).
            int skippedNoName = 0, skippedDupCode = 0;
            var errors = new List<string>();
            // Non-fatal notices: the row IS imported, but an optional reference could not be resolved.
            var warnings = new List<string>();
            var rowNum = 1;
            // PER-ROW OUTCOMES — what the preview shows for each row, recorded by the commit itself as it decides,
            // so the preview is the dry run alone (it used to rebuild the same answers in a second, parallel pass).
            var rowOutcomes = new SortedDictionary<int, ImportRowOutcome>();
            ImportRowOutcome Outcome(int rn) => rowOutcomes.TryGetValue(rn, out var o) ? o : rowOutcomes[rn] = new ImportRowOutcome(rn);
            void RowWarn(int rn, string text) { warnings.Add($"Row {rn}: {text}"); if (rn > 0) Outcome(rn).Warnings.Add(text); }
            void RowError(int rn, string text) { errors.Add($"Row {rn}: {text}"); if (rn > 0) Outcome(rn).Errors.Add(text); }
            // Per-created-row org-skeleton/payroll gaps (typed), keyed by the row's FINAL employee code
            // (auto-generated included) — persisted as EmployeeImportGap after Id assignment; Pass 2 appends
            // link:manager/supervisor gaps to the same map before persistence.
            var gapsByCode = new Dictionary<string, List<ImportGap>>(StringComparer.OrdinalIgnoreCase);
            var rowNumByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void CodeWarn(string code, string text)
            {
                warnings.Add($"Employee {code}: {text}");
                if (rowNumByCode.TryGetValue(code, out var rn)) Outcome(rn).Warnings.Add(text);
            }
            // Rows whose salary is HELD (no valid grade): Pass 1b must skip the salary-structure insert.
            var heldSalaryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Final advisory re-stamp inputs: base readiness per created row (merged with all gaps at the end).
            var createdRowMeta = new List<(Employee Emp, EmployeeReadiness Readiness, bool HasPolicy, string FinalCode)>();

            // ── Readiness (§7): import stays name-only lenient but NEVER silently lands Active. Policy is
            // resolved once per (company, country, nationality) and cached; each row is evaluated with the
            // pure Evaluate primitive (never aborts the file). Blank status ⇒ Draft; explicit Active with
            // activate-blockers ⇒ downgraded to Draft + warning (row still created).
            var readinessPolicyCache = new Dictionary<string, ResolvedReadinessPolicy>();

            async Task<(string Landing, EmployeeReadiness Readiness, bool HasPolicy, string PolicyCountry)> ResolveRowLandingAsync(
                Dictionary<string, string> row, Guid? companyId, Guid? deptId, Guid? desigId, DateTime jd, string csvStatus)
            {
                var country = row.GetValueOrDefault("CountryCode", string.Empty).Trim();
                var nationality = row.GetValueOrDefault("Nationality", string.Empty).Trim();
                var key = $"{companyId}|{country.ToUpperInvariant()}|{Zayra.Api.Infrastructure.Employees.GccReadinessFloor.NormalizeNationality(nationality)}";
                if (!readinessPolicyCache.TryGetValue(key, out var policy))
                {
                    policy = await _activationGuard.ResolvePolicyAsync(tenantId, companyId, country, nationality, ct);
                    readinessPolicyCache[key] = policy;
                }
                var snap = ImportReadinessSnapshot(row, deptId, desigId, jd);
                var readiness = _activationGuard.Evaluate(snap, policy);
                return (ImportLandingStatus(csvStatus, readiness, jd), readiness, policy.Items.Count > 0, policy.CountryCode);
            }
            // Track employee codes created (or repaired) in this batch for Pass 2 resolution
            var batchCodes = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
            var batchPayroll = new Dictionary<string, (Employee emp, Dictionary<string, string> rowData)>(StringComparer.OrdinalIgnoreCase);
            var claimedPositionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── Existing employees ────────────────────────────────────────────────────────────────────────
            // (1) Repair candidates: the employees the importer can see (company query filter), not deleted, by
            //     code, case-insensitive — the same folding as the in-file dedup and as ImportPreview. Tracked,
            //     because a repair row fills their missing values in place.
            // (2) takenCodes: EVERY code the unique (TenantId, EmployeeCode) index holds — all companies, soft-
            //     deleted rows included. A row whose code is taken but not visible is skipped as a duplicate
            //     (it used to fail the WHOLE file with a 422 at SaveChanges), and auto-generated codes skip it.
            // Ordered by id so that, where two existing codes differ only in letter case ('emp-1' and 'EMP-1' — the
            // unique index is case-sensitive), the match is ALWAYS the earliest record, never whichever the database
            // happened to return first; the row that names such a code is flagged (see existingCodeClashes).
            var existingEmployeeGroups = (await _db.Employees
                    .Where(e => e.TenantId == tenantId && !e.IsDeleted)
                    .OrderBy(e => e.Id)
                    .ToListAsync(ct))
                .GroupBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existingEmployeesByCode = existingEmployeeGroups
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var existingCodeClashes = existingEmployeeGroups.Where(g => g.Count() > 1)
                .ToDictionary(g => g.Key, g => g.Select(e => e.EmployeeCode).ToList(), StringComparer.OrdinalIgnoreCase);
            var takenCodes = new HashSet<string>(
                await Zayra.Api.Infrastructure.Data.ScopedBypass
                    .NullableTenantWide(_db.Employees, tenantId, TakenCodesBypassJustification)
                    .Select(e => e.EmployeeCode).ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase);

            // ── ROW GATE: ALL OR NOTHING ───────────────────────────────────────────────────────────────────
            // A row the import cannot take without guessing — no name, a code repeated in this file, a code that
            // belongs to someone the importer cannot see — refuses the WHOLE file, with every such row named. Those
            // rows used to be skipped while the rest of the file landed: a real-data load that "succeeded" with
            // people missing, discovered at payroll. Rows matching a visible existing employee are a re-import
            // (repaired or skipped below) and are not errors. The preview applies this same function.
            var rowRefusals = ImportRowRefusals(rows, existingEmployeesByCode, takenCodes);
            if (rowRefusals.Count > 0) return ImportRowsRefused(rowRefusals, rows.Count, importBatchId);

            var repairLookups = await ImportRepairLookups.LoadAsync(_db, tenantId, track: true, ct);
            // Rows matched to an existing employee, and the subset where something was actually filled in.
            var repairExistingCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var repairedTouchedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Existing-employee rows whose approval-gated values were left unapplied, and separated employees skipped.
            var approvalRequired = new List<(int Row, string EmployeeCode, IReadOnlyList<string> Fields)>();
            var skippedSeparated = 0;

            // ── WORK-EMAIL derivation/uniqueness (accept-never-block) ─────────────────────────────────────
            // Existing tenant work emails keyed by the LOGIN normalization (AuthService.Normalize) + a cumulative
            // in-batch claim set (file order wins), so a DERIVED collision auto-suffixes deterministically against
            // both DB and earlier rows. IgnoreQueryFilters ⇒ tenant-wide (company-agnostic) — matches the login
            // uniqueness boundary and catches cross-company same-domain collisions in a Group tenant.
            var existingEmailNorm = new HashSet<string>(
                (await _db.Employees.AsNoTracking().IgnoreQueryFilters()
                    .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.WorkEmail != "")
                    .Select(e => e.WorkEmail).ToListAsync(ct)).Select(AuthService.Normalize),
                StringComparer.Ordinal);
            var claimedEmailNorm = new HashSet<string>(StringComparer.Ordinal);

            // ── DUPLICATE-PERSON DETECTION preload (accept-never-block) ─────────────────────────────────
            // Preloaded-dictionary path (N1): existing employees loaded ONCE into the matcher — never a DB
            // query per row. Detection is tenant-wide across companies AND across THIS batch (each new row is
            // registered so later rows see it; the earlier member of an intra-file pair is back-flagged). A
            // dup NEVER drops the row and NEVER changes its status — it only adds an advisory dup:* gap.
            var dupMatcher = new EmployeeDuplicateMatcher();
            foreach (var e in await _db.Employees.AsNoTracking()
                // IgnoreQueryFilters is intentional: duplicate detection is authoritative TENANT-WIDE across every
                // company; a scoped caller's company filter must not hide a cross-company dup (masking protects PII).
                .IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted)
                .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.EnglishName, e.ArabicName, e.CompanyId,
                    e.Nationality, e.DateOfBirth, e.IqamaNumber, e.EmiratesId, e.Qid, e.CivilId, e.IdNumber, e.PassportNumber })
                .ToListAsync(ct))
            {
                dupMatcher.Register(DuplicateCandidateBuilder.Build(e.Id, null, e.EmployeeCode, e.FullName, e.EnglishName,
                    e.ArabicName, e.CompanyId, e.Nationality, e.DateOfBirth, e.IqamaNumber, e.EmiratesId, e.Qid, e.CivilId, e.IdNumber, e.PassportNumber));
            }
            // Importer's entity scope — masks a matched counterpart in a company the importer can't access so a
            // persisted gap Detail never leaks cross-scope PII (S3).
            var dupImporterScope = this.GetEntityScope();

            // The CSV row an entity came from (an employee, or anything keyed by its EmployeeId), for naming the
            // failing row in a refusal.
            (int Row, string EmployeeCode)? RowOf(object entity)
            {
                var emp = entity as Employee ?? (entity switch
                {
                    EmployeePayrollProfile p => batchCodes.Values.FirstOrDefault(x => x.Id == p.EmployeeId),
                    EmployeeSalaryStructure s => batchCodes.Values.FirstOrDefault(x => x.Id == s.EmployeeId),
                    ReportingLine l => batchCodes.Values.FirstOrDefault(x => x.Id == l.EmployeeId),
                    EmployeeImportGap g => batchCodes.Values.FirstOrDefault(x => x.Id == g.EmployeeId),
                    _ => null,
                });
                return emp is not null && rowNumByCode.TryGetValue(emp.EmployeeCode, out var rn) ? (rn, emp.EmployeeCode) : null;
            }

            // Persist pending changes; convert a constraint violation into a legible 422 rather than a raw 500,
            // naming the failing row when the failed entries identify one. The raw exception detail is logged
            // server-side only — never returned — so schema/constraint internals do not leak.
            async Task<IActionResult?> PersistAsync(string stage)
            {
                try
                {
                    await _db.SaveChangesAsync(ct);
                    return null;
                }
                // A TRANSIENT failure (dropped connection, timeout) is rethrown so the execution strategy retries the
                // whole attempt; only a real rule violation becomes the 422.
                catch (DbUpdateException ex) when (!IsTransientDatabaseFailure(ex))
                {
                    _logger?.LogError(ex, "Employee CSV import failed to persist at stage {Stage} for tenant {TenantId}.", stage, tenantId);
                    var failedRows = ex.Entries.Select(entry => RowOf(entry.Entity)).OfType<(int Row, string EmployeeCode)>()
                        .Distinct().OrderBy(r => r.Row).ToList();
                    var where = failedRows.Count == 1
                        ? $"Row {failedRows[0].Row} (EmployeeCode '{failedRows[0].EmployeeCode}') could not be saved"
                        : failedRows.Count > 1
                            ? $"One of rows {string.Join(", ", failedRows.Take(10).Select(r => r.Row))} could not be saved"
                            : "The file could not be saved";
                    return UnprocessableEntity(new
                    {
                        error = "import_persist_failed",
                        message = $"{where} ({stage} step) because a database rule was violated. Nothing from this file was imported — correct it and import it again.",
                        stage,
                        failedRows = failedRows.Select(r => new { row = r.Row, employeeCode = r.EmployeeCode }).ToList(),
                        received = rows.Count, created = 0, repaired = 0, skipped = 0, failed = rows.Count,
                        importBatchId,
                    });
                }
            }

            // Every value a staged entity cannot store, named by row and column, BEFORE the save that would fail
            // on it (see EmployeeImportStorageGuard). The preview reports the same problems per row.
            IActionResult? RejectUnstorable(IEnumerable<object> entities, string stage)
            {
                var problems = entities
                    .SelectMany(entity => EmployeeImportStorageGuard.Check(_db.Model, entity)
                        .Select(p => (Where: RowOf(entity), p.Column, p.Problem)))
                    .Where(x => x.Where is not null)
                    .OrderBy(x => x.Where!.Value.Row)
                    .ToList();
                if (problems.Count == 0) return null;
                var first = problems[0];
                return UnprocessableEntity(new
                {
                    error = "import_row_invalid",
                    message = $"Row {first.Where!.Value.Row} (EmployeeCode '{first.Where.Value.EmployeeCode}'): {first.Column} {first.Problem}."
                              + (problems.Count > 1 ? $" {problems.Count - 1} more value(s) cannot be stored either." : string.Empty)
                              + " Nothing from this file was imported — correct it and import it again.",
                    stage,
                    failedRows = problems.Take(30).Select(x => new { row = x.Where!.Value.Row, employeeCode = x.Where.Value.EmployeeCode, column = x.Column, problem = x.Problem }).ToList(),
                    received = rows.Count, created = 0, repaired = 0, skipped = 0, failed = rows.Count,
                    importBatchId,
                });
            }

            // ── Employee codes for rows that do not bring one ─────────────────────────────────────────────
            // Dispensed from the tenant's ID rule WITHOUT a SaveChanges (the sequence bump rides on the import's own
            // save, inside its transaction), skipping every code that is already taken — in the tenant (any
            // company, deleted rows included) or as an explicit EmployeeCode elsewhere in this same file. It used to
            // hand out the next sequence number blindly, so a tenant whose sequence had fallen behind its codes (a
            // manual code, an earlier import) failed the whole file on the unique index.
            var explicitFileCodes = new HashSet<string>(
                rows.Select(r => r.GetValueOrDefault("EmployeeCode", string.Empty).Trim()).Where(c => c.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            var needsGeneratedCodes = rows.Any(r =>
                !string.IsNullOrWhiteSpace(r.GetValueOrDefault("FullName", string.Empty))
                && string.IsNullOrWhiteSpace(r.GetValueOrDefault("EmployeeCode", string.Empty)));
            var nextGeneratedCode = needsGeneratedCodes
                ? await OpenEmployeeCodeDispenserAsync(tenantId, candidate => takenCodes.Contains(candidate) || explicitFileCodes.Contains(candidate), ct)
                : null;

            // ── Pass 1: create all employee records (or match an existing one for repair) ──────────────────
            foreach (var row in rows)
            {
                rowNum++;
                var name = row.GetValueOrDefault("FullName", string.Empty).Trim();
                // Only a row with EVERY cell blank gets here (the ROW GATE refused any other nameless row): it is
                // not a person, so it is counted as skipped and noted, never reported as a rejected row.
                if (string.IsNullOrWhiteSpace(name)) { skipped++; skippedNoName++; Outcome(rowNum).Status = "Error"; RowWarn(rowNum, $"empty row ignored."); continue; }
                var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
                Outcome(rowNum).EmployeeCode = code;
                Outcome(rowNum).FullName = name;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    // A second row in this same file with an already-added code would both pass the
                    // DB check and violate the unique (TenantId, EmployeeCode) index at SaveChanges.
                    // Unreachable after the ROW GATE (a repeated code refuses the file); kept as the last line of
                    // defence against the unique (TenantId, EmployeeCode) index.
                    if (batchCodes.ContainsKey(code))
                        return ImportRowsRefused(new[] { new ImportRowRefusal(rowNum, code, $"EmployeeCode '{code}' is used by more than one row") }, rows.Count, importBatchId);
                    if (existingEmployeesByCode.TryGetValue(code, out var existingEmployee))
                    {
                        if (existingCodeClashes.TryGetValue(code, out var clashing))
                            RowWarn(rowNum, $"EmployeeCode '{code}' matches more than one existing employee ({string.Join(", ", clashing.Select(c => $"'{c}'"))} differ only in letter case) — "
                                            + $"matched to the earliest record, '{existingEmployee.EmployeeCode}'. Rename the other in the People list.");
                        // A separated employee is never changed by an import (see EmployeeImportRepairPlan.IsSeparated).
                        if (EmployeeImportRepairPlan.IsSeparated(existingEmployee))
                        {
                            skipped++; skippedDupCode++; skippedSeparated++;
                            Outcome(rowNum).Status = "Error";
                            RowError(rowNum, $"EmployeeCode '{code}' belongs to a separated employee ({existingEmployee.Status}) — an import never changes a separated employee.");
                            continue;
                        }
                        // Approval-gated values in the row (bank, IBAN, account, routing, MOL ID, payment method,
                        // social insurance, GOSI, salary) are NEVER applied to an existing employee — named here so
                        // the operator knows to submit them through the employee's profile, where they need approval.
                        var gatedNotApplied = EmployeeImportRepairPlan.ApprovalGatedValuesNotApplied(existingEmployee, row, repairLookups);
                        if (gatedNotApplied.Count > 0)
                        {
                            approvalRequired.Add((rowNum, code, gatedNotApplied));
                            RowWarn(rowNum, $"EmployeeCode '{code}' already exists — {string.Join(", ", gatedNotApplied)} in this row were NOT applied. "
                                         + "An existing employee's bank, payroll-identity and salary details change only through an approved change: edit the employee.");
                        }
                        // REPAIR, NEVER OVERWRITE (see EmployeeImportRepairPlan): the row may only fill NON-sensitive
                        // details the existing employee is missing. A row with nothing left to fill is the ordinary skip.
                        var rowJoining = ParseImportJoiningDate(row);
                        if (!EmployeeImportRepairPlan.NeedsRepair(existingEmployee, row, repairLookups,
                                rowJoining.Supplied && !rowJoining.Unparsed ? rowJoining.Value : null))
                        { skipped++; skippedDupCode++; Outcome(rowNum).Status = "Error"; RowError(rowNum, $"EmployeeCode '{code}' already exists."); continue; }
                        repairExistingCodes.Add(code);
                        Outcome(rowNum).Status = "WillRepair";
                        Outcome(rowNum).ProjectedStatus = existingEmployee.Status;
                        batchCodes[code] = existingEmployee;
                        batchPayroll[code] = (existingEmployee, row);
                        rowNumByCode[code] = rowNum;
                        gapsByCode[code] = new List<ImportGap>();
                        // An unknown joining date is filled from a readable cell — the one employee column a repair
                        // may write, because nothing else can be derived without it.
                        if (existingEmployee.JoiningDate == default && rowJoining.Supplied && !rowJoining.Unparsed)
                        {
                            existingEmployee.JoiningDate = rowJoining.Value;
                            existingEmployee.UpdatedAtUtc = DateTime.UtcNow;
                            existingEmployee.UpdatedBy = GetUserId();
                            repairedTouchedCodes.Add(code);
                        }
                        continue;
                    }
                    // Unreachable after the ROW GATE (an invisible taken code refuses the file); same last-line defence.
                    if (takenCodes.Contains(code))
                        return ImportRowsRefused(new[] { new ImportRowRefusal(rowNum, code, $"EmployeeCode '{code}' already belongs to another record") }, rows.Count, importBatchId);
                }

                // ── A JOINING DATE THAT CANNOT BE READ IS NEVER GUESSED ─────────────────────────────────
                // A non-empty cell that does not parse (the template's own "YYYY-MM-DD" placeholder included) used
                // to become TODAY. It now stays UNKNOWN (default), with a typed gap and the raw value kept; the row
                // is forced to Draft and blocked on the date (EmployeeReadinessEvaluator's integrity blocker), and
                // no other date is derived from it — no salary structure effective date, no reporting-line start.
                // A blank cell keeps its long-standing meaning: the import date.
                var joining = ParseImportJoiningDate(row);
                var jd = joining.Value;
                var statusVal = row.GetValueOrDefault("Status", string.Empty).Trim();
                var deptNameRaw = row.GetValueOrDefault("Department", string.Empty).Trim();
                var desigTitleRaw = row.GetValueOrDefault("Designation", string.Empty).Trim();

                // ── ACCEPT-NEVER-BLOCK resolution (SHARED with ImportPreview) ─────────────────────────
                // The single source of truth for every org/grade/position/salary decision. It NEVER drops:
                // unknown company → default (or null); unknown grade → null; ineligible designation → dropped
                // designation link; bad/occupied/ineligible position → null; salary w/o grade → HELD; salary
                // out of band → REVIEW. Each failure is a typed gap + a warning; the person still imports.
                var resolved = EmployeeImportRowResolver.ResolveRow(row, lookups, claimedPositionCodes, ImportJoiningDateOnly(jd));
                var resolvedDeptId = resolved.DepartmentId;
                var resolvedDesigId = resolved.DesignationId;
                AddUnparsedJoiningDateGap(resolved, joining);
                foreach (var w in resolved.Warnings) RowWarn(rowNum, $"{w}");

                // ── Readiness landing decision (§7): blank ⇒ Draft; Active + activate-blockers ⇒ Draft + warning ──
                var (rowStatus, rowReadiness, rowHasPolicy, rowPolicyCountry) = await ResolveRowLandingAsync(
                    row, resolved.CompanyId, resolvedDeptId, resolvedDesigId, jd, statusVal);
                // Country-aware column checks (values in columns that do not apply to this row's country/nationality,
                // identity numbers failing the pack's format) — warnings only, in the commit and therefore the preview.
                Zayra.Api.Application.CountryPack.IIdentityDocumentFormat rowFormat =
                    countryPacks?.ResolveIdentityDocumentFormat(rowPolicyCountry, string.Empty)
                    ?? new Zayra.Api.Infrastructure.CountryPack.DefaultIdentityDocumentFormat();
                foreach (var w in CountryAwareRowWarnings(row, rowPolicyCountry, row.GetValueOrDefault("Nationality", string.Empty).Trim(), rowFormat))
                    RowWarn(rowNum, w);
                if (!string.IsNullOrWhiteSpace(statusVal)
                    && string.Equals(statusVal, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)
                    && rowStatus == EmployeeStatuses.Draft)
                    RowWarn(rowNum, $"{name} imported as Draft — cannot be Active until: "
                                 + $"{string.Join(", ", rowReadiness.Blocking.Select(b => b.Label))}. Fix in the People list.");
                else if (joining.Unparsed && !string.IsNullOrWhiteSpace(statusVal)
                         && !string.Equals(statusVal, EmployeeStatuses.Draft, StringComparison.OrdinalIgnoreCase))
                    RowWarn(rowNum, $"{name} imported as Draft instead of '{statusVal}' — the joining date could not be read.");

                // ── Active-seat budget (P1-4) ─────────────────────────────────────────────
                // MaxEmployees caps ACTIVE seats. A complete row that would land Active but has no seat left is
                // imported as Draft (inactive) instead of rejecting the whole file — Draft rows consume no seat.
                if (rowStatus == EmployeeStatuses.Active)
                {
                    if (activeSeatsConsumed < activeSeatsBudget) activeSeatsConsumed++;
                    else
                    {
                        rowStatus = EmployeeStatuses.Draft;
                        RowWarn(rowNum, $"{name} imported as Draft — active seat limit reached ({sub?.MaxEmployees}); activate once an active seat is available.");
                    }
                }

                // ── Establishment matrix row check (row-level errors, spec §5.2 / AC9) ────
                // SHARED with ImportPreview via EmployeeImportEstablishmentEvaluator so the over-budget
                // downgrade decision and its warning/gap text are byte-identical dry-run↔commit. The
                // evaluator owns the cumulative claimedLevelSlots mutation (advisory rows consume; enforced
                // rows downgrade to Draft below and stay non-occupying, so they never claim a slot → the
                // in-transaction re-verify cannot trip on them).
                var estDecision = EmployeeImportEstablishmentEvaluator.Evaluate(
                    resolvedDeptId, resolvedDesigId, rowStatus, establishmentContext, claimedLevelSlots, deptNameRaw);
                if (estDecision.OverBudget)
                {
                    establishmentBlockedRows.Add((rowNum, estDecision.DeptId, estDecision.LevelId, estDecision.Budgeted, estDecision.Current));
                    // ACCEPT-NEVER-BLOCK: an over-budget row is NEVER dropped. Both modes emit an
                    // org:establishment gap so the row lands NeedsAttention with a deep-link.
                    resolved.Gaps.Add(new ImportGap("org:establishment", "org", estDecision.Detail, estDecision.DeptDisplay));
                    if (estDecision.Advisory)
                    {
                        RowWarn(rowNum, $"{estDecision.Detail}");
                    }
                    else
                    {
                        rowStatus = EmployeeStatuses.Draft;
                        RowWarn(rowNum, $"{name} imported as Draft — {estDecision.Detail} Assign within budget to activate.");
                    }
                }

                // Generated codes come from the dispenser opened BEFORE the loop — no SaveChanges inside the loop,
                // so a later row's failure still rolls the whole file back.
                var finalCode = string.IsNullOrWhiteSpace(code) ? nextGeneratedCode!() : code;

                // ── WORK EMAIL: derive-when-blank (auto-suffix on collision) / keep-and-flag-when-provided ──
                // Derived values are made unique against DB ∪ this batch; a PROVIDED value is never silently
                // rewritten — a collision is flagged (email:duplicate) and the row still imports. Domain-mismatch
                // on a provided value was already flagged by the shared resolver.
                string workEmail;
                if (!string.IsNullOrEmpty(resolved.WorkEmailLocalPart))
                {
                    workEmail = WorkEmailDeriver.Uniqueify(resolved.WorkEmailLocalPart, resolved.WorkEmailDomain,
                        addr => existingEmailNorm.Contains(AuthService.Normalize(addr)) || claimedEmailNorm.Contains(AuthService.Normalize(addr)));
                    claimedEmailNorm.Add(AuthService.Normalize(workEmail));
                }
                else
                {
                    workEmail = resolved.WorkEmailProvided;
                    if (!string.IsNullOrWhiteSpace(workEmail))
                    {
                        var norm = AuthService.Normalize(workEmail);
                        if (existingEmailNorm.Contains(norm) || claimedEmailNorm.Contains(norm))
                        {
                            resolved.Gaps.Add(new ImportGap("email:duplicate", "readiness",
                                $"Work email '{workEmail}' is already used by another employee in this tenant.", workEmail));
                            RowWarn(rowNum, $"Work email '{workEmail}' is already in use — imported as-is and flagged.");
                        }
                        else claimedEmailNorm.Add(norm);
                    }
                }

                // SHARED with ImportPreview: the same builder records the same data:unparsed* gaps in both.
                var employee = BuildImportedEmployee(tenantId, row, resolved, name, finalCode, workEmail, rowStatus, jd, resolved.Gaps);
                employee.ReadinessState = rowReadiness.State;
                employee.ActivationBlockersCount = rowReadiness.Blocking.Count;
                employee.ReadinessEvaluatedAtUtc = DateTime.UtcNow;
                employee.ProfileCompletenessScore = rowHasPolicy ? rowReadiness.Score : 0m;
                _db.Employees.Add(employee);
                batchCodes[finalCode] = employee;
                batchPayroll[finalCode] = (employee, row);
                // Track this row's typed gaps against its FINAL code (auto-generated included) for persistence,
                // the summary, and the advisory readiness re-stamp; Pass 2 appends link:manager/supervisor gaps.
                gapsByCode[finalCode] = new List<ImportGap>(resolved.Gaps);
                rowNumByCode[finalCode] = rowNum;
                if (resolved.SalaryDecision == ImportSalaryDecision.Hold) heldSalaryCodes.Add(finalCode);
                createdRowMeta.Add((employee, rowReadiness, rowHasPolicy, finalCode));
                {
                    var outcome = Outcome(rowNum);
                    outcome.Status = "WillCreate";
                    outcome.EmployeeCode = finalCode;
                    outcome.ProjectedStatus = rowStatus;
                    outcome.Readiness = rowReadiness;
                    outcome.FinalCode = finalCode;
                }
                created++;

                // ── DUPLICATE-PERSON detection for this row (accept-never-block: flag only, never drop/merge) ──
                var dupProbe = DuplicateCandidateBuilder.FromEmployee(employee, employeeId: null, batchKey: finalCode);
                var dupMatches = dupMatcher.Match(dupProbe);
                if (dupMatches.Count > 0)
                {
                    var strongest = dupMatches[0]; // Match() returns strong-first
                    var gapType = strongest.MatchType == DuplicateMatchTypes.Strong ? "dup:strong" : "dup:possible";
                    var (detail, raw) = DupGapText(dupImporterScope, strongest.Counterpart, strongest.Signals);
                    // One dup gap per row (keeps the "N possible duplicates" count = flagged rows).
                    if (!gapsByCode[finalCode].Any(g => g.Type is "dup:strong" or "dup:possible"))
                    {
                        gapsByCode[finalCode].Add(new ImportGap(gapType, "dup", detail, raw));
                        RowWarn(rowNum, $"{detail}");
                    }
                    // Back-flag the earlier member of any INTRA-FILE pair so BOTH rows surface (S2).
                    foreach (var m in dupMatches)
                    {
                        if (m.Counterpart.BatchKey is not string earlierKey) continue;                // only batch rows
                        if (!gapsByCode.TryGetValue(earlierKey, out var earlierGaps)) continue;
                        if (earlierGaps.Any(g => g.Type is "dup:strong" or "dup:possible")) continue;  // already flagged
                        var earlierType = m.MatchType == DuplicateMatchTypes.Strong ? "dup:strong" : "dup:possible";
                        var (bDetail, bRaw) = DupGapText(dupImporterScope, dupProbe, m.Signals); // earlier row points at THIS row
                        earlierGaps.Add(new ImportGap(earlierType, "dup", bDetail, bRaw));
                        var earlierRow = rowNumByCode.GetValueOrDefault(earlierKey, 0);
                        RowWarn(earlierRow, $"{bDetail}");
                    }
                }
                dupMatcher.Register(dupProbe);
            }

            if (RejectUnstorable(batchCodes.Where(kv => !repairExistingCodes.Contains(kv.Key)).Select(kv => (object)kv.Value), "employees") is { } unstorableEmployee)
                return unstorableEmployee;

            // First persist: when level slots were claimed and enforcement is on, serialize with the
            // same per-cell advisory locks the single-hire paths use and RE-VERIFY each claimed cell
            // against a fresh count inside the transaction — a concurrent import/hire racing for the
            // last slot loses with the structured 409 instead of silently overshooting (AC7).
            // The locks and the re-verify run in the IMPORT's single transaction (see Import) instead of
            // opening a second one of their own: the old nested transaction started AFTER the loop had already
            // committed its rows, so it could not protect them, and a race loss left them behind.
            if (_db.Database.IsRelational() && claimedLevelSlots.Count > 0
                && establishmentMode == EstablishmentGuardService.ModeEnforced)
            {
                // Deadlock-free: cells locked in stable lock-key order.
                var orderedCells = claimedLevelSlots
                    .OrderBy(kv => EstablishmentGuardService.ComputeLockKey(tenantId, kv.Key.Dept, kv.Key.Level))
                    .ToList();
                foreach (var ((deptId, levelId), _) in orderedCells)
                    await _establishmentGuard.AcquireSlotLockAsync(tenantId, deptId, levelId, ct);
                foreach (var ((deptId, levelId), claimed) in orderedCells)
                {
                    var deptName = deptNameById.GetValueOrDefault(deptId, string.Empty);
                    var levelDesignations = levelByDesignation.Where(kv => kv.Value == levelId).Select(kv => kv.Key).ToList();
                    // Counts the rows ALREADY in the database only: this batch is still unsaved in the change
                    // tracker, so `claimed` is added exactly once (the old mid-loop SaveChanges made the batch
                    // visible here and double-counted it).
                    var freshCurrent = await Zayra.Api.Application.Organization.EstablishmentOccupancy
                        // IgnoreQueryFilters is intentional: establishment budget lookups/counts must be absolute (independent of the caller's company scope) so import checks equal the guard's; explicit TenantId (+ !IsDeleted where applicable) filters are applied inline.
                        .Occupying(_db.Employees.IgnoreQueryFilters().AsNoTracking(), tenantId)
                        .Where(e => e.DesignationId != null && levelDesignations.Contains(e.DesignationId!.Value))
                        .Where(e => e.DepartmentId == deptId || (e.DepartmentId == null && e.Department == deptName))
                        .CountAsync(ct);
                    var cellBudget = levelBudgets[(deptId, levelId)];
                    if (freshCurrent + claimed > cellBudget)
                    {
                        if (!levelNamesById.TryGetValue(levelId, out var names))
                            names = (Code: "", NameEn: "budgeted-level", NameAr: "");
                        return this.EstablishmentConflict(new EstablishmentBudgetExceededException(
                            new EstablishmentBlock(deptId, deptName, levelId, names.Code, names.NameEn, names.NameAr,
                                cellBudget, freshCurrent, claimed, 0)));
                    }
                }
            }
            if (await PersistAsync("employees") is { } saveError) return saveError;

            var importedPositionAssignments = batchCodes
                .Where(kv => !repairExistingCodes.Contains(kv.Key) && kv.Value.PositionId is not null)
                .Select(kv => kv.Value).ToList();
            if (importedPositionAssignments.Count > 0)
            {
                var assignedPositionIds = importedPositionAssignments.Select(e => e.PositionId!.Value).ToList();
                var positions = await _db.Positions.Where(p => p.TenantId == tenantId && assignedPositionIds.Contains(p.Id)).ToListAsync(ct);
                foreach (var position in positions)
                {
                    var incumbent = importedPositionAssignments.Single(e => e.PositionId == position.Id);
                    position.IncumbentEmployeeId = incumbent.Id;
                    position.Status = PositionStatuses.Filled;
                    position.UpdatedAtUtc = DateTime.UtcNow;
                    position.UpdatedBy = GetUserId();
                }
                if (await PersistAsync("positions") is { } positionSaveError) return positionSaveError;
            }

            // ── Pass 1b: payroll profiles + salary structures ────────────────────────
            int payrollProfilesCreated = 0, payrollProfilesRepaired = 0, hierarchyLinksRecovered = 0;
            var payrollArtifactsChanged = false;
            var stagedPayrollEntities = new List<object>();
            // EF queries cannot see Added entities until SaveChanges: reuse each structure staged in this import so
            // a 250-row file does not stage 250 identical (TenantId, CompanyId, Code) rows. Company is part of the
            // key, so one company's structure is never shared with another's employees.
            var importStructures = new Dictionary<(Guid TenantId, Guid? CompanyId, string Code), SalaryStructure>();
            var tenantCurrency = await _db.ResolveTenantCurrencyAsync(tenantId, ct);
            foreach (var (payrollCode, (emp, rowData)) in batchPayroll)
            {
                if (repairExistingCodes.Contains(payrollCode))
                {
                    // REPAIR: fills only the NON-sensitive payroll columns an existing employee is missing — payroll
                    // group, salary-structure reference, currency — creating the profile (with NO bank details) when
                    // there is none. Bank, IBAN, account, routing, MOL ID, payment method, social insurance and salary
                    // are approval-gated for an existing employee and were reported, not applied, in Pass 1.
                    var repairGroup = rowData.GetValueOrDefault("PayrollGroup", string.Empty).Trim();
                    var repairStructure = rowData.GetValueOrDefault("SalaryStructureCode", string.Empty).Trim();
                    var repairCurrency = rowData.GetValueOrDefault("Currency", string.Empty).Trim().ToUpperInvariant();
                    repairLookups.ProfilesByEmployee.TryGetValue(emp.Id, out var existingProfile);
                    if (existingProfile is null)
                    {
                        if (repairGroup.Length == 0 && repairStructure.Length == 0 && repairCurrency.Length == 0) continue;
                        existingProfile = new EmployeePayrollProfile
                        {
                            TenantId = tenantId, EmployeeId = emp.Id,
                            SalaryCurrency = repairCurrency.Length > 0 ? repairCurrency : tenantCurrency,
                            PayrollGroup = repairGroup, SalaryStructureReference = repairStructure,
                            WpsEligible = true, EosbEligible = true, CreatedBy = GetUserId()
                        };
                        _db.EmployeePayrollProfiles.Add(existingProfile);
                        repairLookups.ProfilesByEmployee[emp.Id] = existingProfile;
                        stagedPayrollEntities.Add(existingProfile);
                        payrollProfilesCreated++;
                        payrollArtifactsChanged = true;
                        repairedTouchedCodes.Add(payrollCode);
                        continue;
                    }
                    var repairedAny = false;
                    string FillBlank(string target, string source)
                    {
                        if (!string.IsNullOrWhiteSpace(target) || source.Length == 0) return target;
                        repairedAny = true;
                        return source;
                    }
                    existingProfile.PayrollGroup = FillBlank(existingProfile.PayrollGroup, repairGroup);
                    existingProfile.SalaryStructureReference = FillBlank(existingProfile.SalaryStructureReference, repairStructure);
                    existingProfile.SalaryCurrency = FillBlank(existingProfile.SalaryCurrency, repairCurrency);
                    if (repairedAny)
                    {
                        existingProfile.UpdatedAtUtc = DateTime.UtcNow;
                        existingProfile.UpdatedBy = GetUserId();
                        stagedPayrollEntities.Add(existingProfile);
                        payrollProfilesRepaired++;
                        payrollArtifactsChanged = true;
                        repairedTouchedCodes.Add(payrollCode);
                    }
                    continue;
                }

                // ── A NEW employee: initial data entry, the same as POST /api/employees (bank and salary included). ──
                var ibanRaw = rowData.GetValueOrDefault("IBAN", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(ibanRaw) && !Zayra.Api.Infrastructure.Payroll.IbanValidator.IsValid(ibanRaw))
                    CodeWarn(emp.EmployeeCode, $"{Zayra.Api.Infrastructure.Payroll.IbanValidator.Describe(ibanRaw)}. Imported, but it must be corrected before this employee can be included in a payroll run.");
                var bankNameRaw = rowData.GetValueOrDefault("BankName", string.Empty).Trim();
                var molIdRaw = rowData.GetValueOrDefault("MolId", string.Empty).Trim();
                var accountRaw = rowData.GetValueOrDefault("AccountNumber", string.Empty).Trim();
                var routingRaw = rowData.GetValueOrDefault("BankRoutingCode", string.Empty).Trim();
                var payrollGroupRaw = rowData.GetValueOrDefault("PayrollGroup", string.Empty).Trim();
                var paymentMethodRaw = rowData.GetValueOrDefault("PaymentMethod", string.Empty).Trim();
                var socialInsuranceRaw = rowData.GetValueOrDefault("SocialInsuranceReference", string.Empty).Trim();
                var structureCodeRaw = rowData.GetValueOrDefault("SalaryStructureCode", string.Empty).Trim();
                var currencyRaw = rowData.GetValueOrDefault("Currency", string.Empty).Trim();
                var currency = string.IsNullOrWhiteSpace(currencyRaw)
                    ? defaultCompany is null && string.Equals(tenantCurrency, "USD", StringComparison.OrdinalIgnoreCase) ? "SAR" : tenantCurrency
                    : currencyRaw.ToUpperInvariant();
                // ONE salary parser for preview, commit and the readiness snapshot (ParseImportSalary): invariant
                // culture, a malformed cell reported rather than read as zero, a zero basic under non-zero
                // allowances refused — every GCC pack computes GOSI, end-of-service and loss-of-pay off basic.
                var salary = ParseImportSalary(rowData);

                // ANY supplied payroll cell creates the profile — AccountNumber, BankRoutingCode, PaymentMethod,
                // PayrollGroup, Currency and SalaryStructureCode used to be discarded unless one of five other
                // cells was present. Currency uses the RAW cell: `currency` falls back to the tenant's and is never
                // empty. The profile is written even when the salary cells are bad: that is a salary-structure
                // problem, and it used to take the row's bank account and MOL ID with it, silently.
                bool hasPayroll = !string.IsNullOrEmpty(ibanRaw) || !string.IsNullOrEmpty(bankNameRaw) ||
                                  !string.IsNullOrEmpty(molIdRaw) || !string.IsNullOrEmpty(socialInsuranceRaw) ||
                                  !string.IsNullOrEmpty(accountRaw) || !string.IsNullOrEmpty(routingRaw) ||
                                  !string.IsNullOrEmpty(paymentMethodRaw) || !string.IsNullOrEmpty(payrollGroupRaw) ||
                                  !string.IsNullOrEmpty(currencyRaw) || !string.IsNullOrEmpty(structureCodeRaw) ||
                                  salary.Gross > 0;

                EmployeePayrollProfile? payrollProfile = null;
                if (hasPayroll)
                {
                    payrollProfile = new EmployeePayrollProfile
                    {
                        TenantId = tenantId, EmployeeId = emp.Id,
                        BankName = bankNameRaw, Iban = ibanRaw, SalaryCurrency = currency,
                        AccountNumber = accountRaw, BankRoutingCode = routingRaw,
                        PaymentMethod = string.IsNullOrWhiteSpace(paymentMethodRaw) ? "BankTransfer" : paymentMethodRaw,
                        PayrollGroup = payrollGroupRaw, SalaryStructureReference = structureCodeRaw,
                        SocialInsuranceReference = socialInsuranceRaw,
                        MolId = molIdRaw, WpsEligible = true, EosbEligible = true, CreatedBy = GetUserId()
                    };
                    _db.EmployeePayrollProfiles.Add(payrollProfile);
                    repairLookups.ProfilesByEmployee[emp.Id] = payrollProfile;
                    stagedPayrollEntities.Add(payrollProfile);
                    payrollProfilesCreated++;
                    payrollArtifactsChanged = true;
                    // A NEW employee's bank details are initial data entry, but no second person has seen them:
                    // say so, so they are confirmed with the employee before the first salary is sent.
                    // Persisted as a typed gap too (not only this response), so HR can find it on the employee's
                    // readiness checklist, payroll validation can warn on it, and confirming it is audited.
                    if (!string.IsNullOrEmpty(ibanRaw) || !string.IsNullOrEmpty(accountRaw) || !string.IsNullOrEmpty(routingRaw))
                    {
                        const string bankDetail = "Bank details (IBAN/account/routing code) were set by an import without a second " +
                                                  "review. Verify them with the employee before their first payroll.";
                        CodeWarn(emp.EmployeeCode, $"{bankDetail}");
                        gapsByCode[payrollCode].Add(new ImportGap(EmployeeImportGap.BankDetailsUnverified, "pay", bankDetail, null));
                    }
                }
                // ── ONE SET OF BANK DETAILS, IN BOTH HOMES ──────────────────────────────────────────────
                // The WPS/SIF export pays from the payroll profile; the employee record, its readiness snapshot
                // and the People list read Employee.BankName/BankIban. The import used to write the Employee copy
                // only when a salary structure was ALSO created, so every row whose salary was held, under review
                // or absent carried bank details in the profile and none on the employee. A NEW employee now gets
                // exactly the profile's values.
                if (payrollProfile is not null)
                {
                    emp.BankName = payrollProfile.BankName;
                    emp.BankIban = payrollProfile.Iban;
                }

                // Every discard path below records a TYPED gap as well as a warning, so the withheld salary is
                // visible on the employee's readiness checklist and in the import summary.
                var structureGap = SalaryStructureGap(salary, emp.JoiningDate != default);
                if (structureGap is not null)
                {
                    CodeWarn(emp.EmployeeCode, $"{structureGap.Detail}");
                    gapsByCode[payrollCode].Add(structureGap);
                    continue;
                }
                if (!salary.CanAssign) continue;                                   // no salary in the file
                // Salary HELD (no valid grade): the pay:salaryHeld gap is already recorded; the profile stands.
                if (heldSalaryCodes.Contains(payrollCode)) continue;

                var grade = emp.GradeId is not null ? lookups.GradeById.GetValueOrDefault(emp.GradeId.Value) : null;
                var structure = await ResolveImportSalaryStructureAsync(tenantId, emp.CompanyId, grade, structureCodeRaw, currency, importStructures, ct,
                    DateOnly.FromDateTime(emp.JoiningDate), warnings);
                var assignment = new EmployeeSalaryStructure
                {
                    TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
                    BasicSalary = salary.BasicSalary, HousingAllowance = salary.Housing, TransportAllowance = salary.Transport,
                    FoodAllowance = salary.Food, MobileAllowance = salary.Mobile, OtherAllowance = salary.Other,
                    FixedDeduction = salary.FixedDeduction, Currency = currency,
                    // Known joining date only — SalaryStructureGap above refuses a structure without one.
                    EffectiveDate = DateOnly.FromDateTime(emp.JoiningDate), IsActive = true, CreatedBy = GetUserId()
                };
                _db.EmployeeSalaryStructures.Add(assignment);
                stagedPayrollEntities.Add(assignment);
                payrollArtifactsChanged = true;
                if (emp.Salary is null or 0m) emp.Salary = salary.Gross;
                if (string.IsNullOrWhiteSpace(emp.PayrollProfileCode) && !string.IsNullOrWhiteSpace(payrollGroupRaw)) emp.PayrollProfileCode = payrollGroupRaw;
            }
            if (RejectUnstorable(stagedPayrollEntities, "payroll") is { } unstorablePayroll) return unstorablePayroll;
            if (payrollArtifactsChanged && await PersistAsync("payroll") is { } payrollSaveError)
                return payrollSaveError;

            // ── Pass 2: resolve manager/supervisor refs → IDs (CODE first, EMAIL fallback) ────────────
            // Iterates the rows CREATED (or repaired) in Pass 1 keyed by their FINAL employee code (auto-generated
            // included), so an auto-coded row links its manager too. Manager/supervisor NOT found is a WARNING + a
            // link:* gap — NEVER an error, and never a row drop (the person already imported in Pass 1). A repair
            // row only links a manager/supervisor the existing employee does not already have.
            int hierarchyLinked = 0;
            int managersUnresolved = 0;
            var hierarchyWarnings = new List<string>();
            void HierWarn(int rn, string text) { hierarchyWarnings.Add($"Row {rn}: {text}"); if (rn > 0) Outcome(rn).Warnings.Add(text); }
            var allEmployees = await _db.Employees
                .Where(e => e.TenantId == tenantId && !e.IsDeleted)
                .ToListAsync(ct);
            var allByCode = allEmployees
                .GroupBy(e => e.EmployeeCode.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First());
            var allById = allEmployees.ToDictionary(e => e.Id);
            var allByEmail = allEmployees
                .Where(e => !string.IsNullOrWhiteSpace(e.WorkEmail))
                .GroupBy(e => e.WorkEmail.Trim().ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            Employee? ResolveRef(string? codeRef, string? emailRef)
            {
                if (!string.IsNullOrWhiteSpace(codeRef) && allByCode.TryGetValue(codeRef.Trim().ToUpperInvariant(), out var byCode))
                    return byCode;
                if (!string.IsNullOrWhiteSpace(emailRef) && allByEmail.TryGetValue(emailRef.Trim().ToUpperInvariant(), out var byEmail))
                    return byEmail;
                return null;
            }

            foreach (var (finalCode, (emp, row)) in batchPayroll)
            {
                var isRepair = repairExistingCodes.Contains(finalCode);
                var rn = rowNumByCode.GetValueOrDefault(finalCode, 0);
                var mgrCode = row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim();
                var mgrEmail = row.GetValueOrDefault("ManagerEmail", string.Empty).Trim();
                var supCode = row.GetValueOrDefault("SupervisorEmployeeCode", string.Empty).Trim();
                var supEmail = row.GetValueOrDefault("SupervisorEmail", string.Empty).Trim();
                var gaps = gapsByCode.TryGetValue(finalCode, out var gl) ? gl : (gapsByCode[finalCode] = new List<ImportGap>());
                bool changed = false;
                // A reporting line starts on the joining date — or, when that date is unknown, on the day the link
                // was recorded (today). It is never derived from an unknown date (which would read as year 1).
                var linkEffectiveFrom = emp.JoiningDate != default ? emp.JoiningDate : DateTime.UtcNow;

                if ((!string.IsNullOrEmpty(mgrCode) || !string.IsNullOrEmpty(mgrEmail))
                    && (!isRepair || emp.ManagerEmployeeId is null))
                {
                    var mgrLabel = !string.IsNullOrEmpty(mgrCode) ? mgrCode : mgrEmail;
                    var mgr = ResolveRef(mgrCode, mgrEmail);
                    if (mgr is null)
                    {
                        managersUnresolved++;
                        HierWarn(rn, $"Manager '{mgrLabel}' not found — imported without a manager link.");
                        gaps.Add(new ImportGap("link:manager", "link", $"Manager '{mgrLabel}' not found — not linked.", mgrLabel));
                    }
                    else if (mgr.Id == emp.Id)
                    {
                        managersUnresolved++;
                        HierWarn(rn, $"Employee cannot be their own manager — manager link skipped.");
                        gaps.Add(new ImportGap("link:manager", "link", "Employee cannot be their own manager — not linked.", mgrLabel));
                    }
                    else
                    {
                        bool circular = false;
                        var visited = new HashSet<int> { emp.Id };
                        var cursor = (int?)mgr.Id;
                        for (int depth = 0; cursor.HasValue && depth < 50; depth++)
                        {
                            if (!visited.Add(cursor.Value)) { circular = true; break; }
                            cursor = allById.GetValueOrDefault(cursor.Value)?.ManagerEmployeeId;
                        }
                        if (circular)
                        {
                            managersUnresolved++;
                            HierWarn(rn, $"Setting '{mgrLabel}' as manager of '{finalCode}' would create a circular hierarchy — manager link skipped.");
                            gaps.Add(new ImportGap("link:manager", "link", $"Setting '{mgrLabel}' as manager would create a circular hierarchy — not linked.", mgrLabel));
                        }
                        else
                        {
                            emp.ManagerEmployeeId = mgr.Id;
                            _db.ReportingLines.Add(new ReportingLine
                            {
                                TenantId = tenantId, EmployeeId = emp.Id, ManagerEmployeeId = mgr.Id,
                                RelationshipType = "SolidLine", EffectiveFrom = linkEffectiveFrom, IsPrimary = true, IsActive = true
                            });
                            changed = true;
                            hierarchyLinked++;
                            if (isRepair) { hierarchyLinksRecovered++; repairedTouchedCodes.Add(finalCode); }
                        }
                    }
                }

                if ((!string.IsNullOrEmpty(supCode) || !string.IsNullOrEmpty(supEmail))
                    && (!isRepair || emp.SupervisorEmployeeId is null))
                {
                    var supLabel = !string.IsNullOrEmpty(supCode) ? supCode : supEmail;
                    var sup = ResolveRef(supCode, supEmail);
                    if (sup is null)
                    {
                        HierWarn(rn, $"Supervisor '{supLabel}' not found — imported without a supervisor link.");
                        gaps.Add(new ImportGap("link:supervisor", "link", $"Supervisor '{supLabel}' not found — not linked.", supLabel));
                    }
                    else if (sup.Id != emp.Id)
                    {
                        emp.SupervisorEmployeeId = sup.Id;
                        _db.ReportingLines.Add(new ReportingLine
                        {
                            TenantId = tenantId, EmployeeId = emp.Id, ManagerEmployeeId = sup.Id,
                            RelationshipType = "DottedLine", EffectiveFrom = linkEffectiveFrom, IsPrimary = false, IsActive = true
                        });
                        changed = true;
                        if (isRepair) { hierarchyLinksRecovered++; repairedTouchedCodes.Add(finalCode); }
                    }
                }

                if (changed) _db.Employees.Update(emp);
            }

            // ── Advisory readiness re-stamp (Part E): fold ALL of a row's gaps (Pass 1 org/pay + Pass 2 link)
            // into its readiness so it lands NeedsAttention with a lowered score — WITHOUT touching Blocking /
            // ActivationBlockersCount, so activation stays unblocked. Blocked rows stay Blocked.
            foreach (var (emp, readiness, hasPolicy, finalCode) in createdRowMeta)
            {
                var gaps = gapsByCode.GetValueOrDefault(finalCode) ?? new List<ImportGap>();
                if (gaps.Count == 0) continue;
                var advisory = gaps.Select(g => EmployeeReadinessEvaluator.ImportGapToItem(g.Type, g.Category)).ToList();
                var merged = EmployeeReadinessEvaluator.MergeAdvisoryGaps(readiness, advisory);
                emp.ReadinessState = merged.State;
                emp.ProfileCompletenessScore = (hasPolicy || advisory.Count > 0) ? merged.Score : 0m;
                // ActivationBlockersCount unchanged (stamped at creation) — advisory gaps never block activation.
            }

            // ── Persist per-row typed gaps tagged with the importBatchId (Part D2). ────────────────────
            static string? Trunc(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);
            var gapEntities = new List<EmployeeImportGap>();
            foreach (var (finalCode, gaps) in gapsByCode)
            {
                if (gaps.Count == 0 || !batchCodes.TryGetValue(finalCode, out var gEmp)) continue;
                var rn = rowNumByCode.GetValueOrDefault(finalCode, 0);
                foreach (var g in gaps)
                    gapEntities.Add(new EmployeeImportGap
                    {
                        TenantId = tenantId, CompanyId = gEmp.CompanyId, ImportBatchId = importBatchId,
                        EmployeeId = gEmp.Id, RowNumber = rn,
                        GapType = g.Type, GapCategory = g.Category,
                        Detail = Trunc(g.Detail, 500) ?? string.Empty, RawValue = Trunc(g.RawValue, 200),
                    });
            }
            if (gapEntities.Count > 0) _db.EmployeeImportGaps.AddRange(gapEntities);

            // Single persist covering Pass 2 links, the advisory re-stamp, and the gap rows.
            if (await PersistAsync("links") is { } finalSaveError) return finalSaveError;

            // A repaired employee's stored readiness badge is recomputed from its now-complete record, so a filled
            // joining date / IBAN / salary clears its blocker in the People list without waiting for an edit.
            var repairedEmployees = repairedTouchedCodes.Select(c => batchCodes[c]).ToList();
            foreach (var emp in repairedEmployees)
                await _activationGuard.StampReadinessAsync(emp, ct);
            if (repairedEmployees.Count > 0 && await PersistAsync("readiness") is { } readinessSaveError) return readinessSaveError;

            // A row matched to an existing employee that had nothing fillable after all (e.g. its only gap was bank
            // details held by a pending approval) is an ordinary duplicate skip — so received always equals
            // created + repaired + skipped.
            foreach (var untouched in repairExistingCodes.Where(c => !repairedTouchedCodes.Contains(c)))
            {
                skipped++; skippedDupCode++;
                Outcome(rowNumByCode[untouched]).Status = "Error";
                RowError(rowNumByCode[untouched], $"EmployeeCode '{untouched}' already exists and this file had nothing missing it could fill.");
            }
            var repaired = repairedTouchedCodes.Count;

            // ── Countable summary (Part D3/D4) ─────────────────────────────────────────────────────
            var createdIncomplete = createdRowMeta
                .Select(m => new { m.Emp, m.Readiness, Gaps = gapsByCode.GetValueOrDefault(m.FinalCode) ?? new List<ImportGap>() })
                .Where(x => x.Gaps.Count > 0 || x.Readiness.IsBlocked)
                .Select(x => new
                {
                    employeeId = x.Emp.Id, employeeCode = x.Emp.EmployeeCode, name = x.Emp.FullName,
                    blockingCount = x.Readiness.Blocking.Count,
                    gaps = x.Gaps.Select(g => new { type = g.Type, category = g.Category, detail = g.Detail }).ToList(),
                })
                .ToList();
            var allGaps = gapsByCode.Values.SelectMany(g => g).ToList();
            int salariesHeld = allGaps.Count(g => g.Type == "pay:salaryHeld");
            int newDepartments = allGaps.Where(g => g.Type == "org:department").Select(g => (g.RawValue ?? string.Empty).ToUpperInvariant()).Distinct().Count();
            // A blank BranchCode now records an org:branch gap with a NULL RawValue (the row named no
            // branch, so none is "new"). Counting it would bucket every such row under "" and inflate
            // the preview by one phantom branch, so unnamed gaps are excluded from the new-branch count.
            int newBranches = allGaps.Where(g => g.Type == "org:branch" && !string.IsNullOrWhiteSpace(g.RawValue)).Select(g => g.RawValue!.ToUpperInvariant()).Distinct().Count();
            // "N possible duplicates" — rows imported (never dropped) but flagged as a possible existing person.
            int possibleDuplicates = allGaps.Count(g => g.Type is "dup:strong" or "dup:possible");

            var allErrors = errors.Take(30).ToList();
            var allWarnings = warnings.Concat(hierarchyWarnings).Take(30).ToList();

            // ── AUDIT, LAST ────────────────────────────────────────────────────────────────────────────
            // Every audit row is written AFTER the final data save, immediately before the commit. An audit write
            // takes the tenant's audit-chain advisory lock (ZayraDbContext) and holds it until the transaction
            // ends; the establishment block audit used to be written straight after the FIRST save, so every other
            // audited action in the tenant queued behind the rest of the import. The marker row goes last: it is
            // what the commit-retry check and a replayed ImportKey look for (see Import).
            foreach (var blocked in establishmentBlockedRows)
            {
                if (!levelNamesById.TryGetValue(blocked.LevelId, out var names))
                    names = (Code: "", NameEn: "", NameAr: "");
                await _audit.WriteAsync("establishment.assignment_blocked", "Department", blocked.DeptId.ToString(), Context(),
                    JsonSerializer.Serialize(new
                    {
                        path = "import",
                        advisory = establishmentMode == EstablishmentGuardService.ModeAdvisory,
                        rowNumber = blocked.RowNum,
                        departmentId = blocked.DeptId,
                        departmentName = deptNameById.GetValueOrDefault(blocked.DeptId, string.Empty),
                        staffingLevelId = blocked.LevelId,
                        levelCode = names.Code,
                        levelNameEn = names.NameEn,
                        levelNameAr = names.NameAr,
                        budgeted = blocked.Budgeted,
                        current = blocked.Current,
                        attempted = 1
                    }), ct);
            }
            if (createdIncomplete.Count > 0)
                await _audit.WriteAsync("employee.import_created_incomplete", "Employee", importBatchId.ToString(), Context(),
                    JsonSerializer.Serialize(new { importBatchId, createdIncompleteCount = createdIncomplete.Count, managersUnresolved, salariesHeld }), ct);
            if (req.ConfirmReimport && earlierCommit is not null)
                await _audit.WriteAsync("employee.reimport_confirmed", ImportBatchEntityName, importBatchId.ToString(), Context(),
                    JsonSerializer.Serialize(new
                    {
                        importBatchId, contentSha256, reason = req.ReimportReason!.Trim(),
                        earlierImportBatchId = earlierCommit.ImportBatchId, earlierImportedAtUtc = earlierCommit.ImportedAtUtc,
                    }), ct);
            await _audit.WriteAsync(ImportCommittedAction, ImportBatchEntityName, importBatchId.ToString(), Context(),
                JsonSerializer.Serialize(new ImportCommitRecord(importBatchId, contentSha256, rows.Count, created, repaired, skipped, 0)), ct);

            if (dryRun)
                _lastDryRun = BuildDryRunReport(rowOutcomes.Values, gapsByCode, created, repaired, skipped, approvalRequired.Count);

            return Ok(new
            {
                received = rows.Count,
                imported = created,
                created,
                repaired,
                skipped,
                failed = 0,
                replayed = false,
                skippedNoName,
                skippedDupCode,
                // Existing employees in a separated status: never changed by an import (included in skippedDupCode).
                skippedSeparated,
                // Existing-employee rows carrying approval-gated values (bank, IBAN, account, routing, MOL ID, payment
                // method, social insurance, GOSI, salary) that were NOT applied — change them through the employee,
                // where they go to approval.
                approvalRequiredCount = approvalRequired.Count,
                approvalRequired = approvalRequired.Take(100)
                    .Select(a => new { row = a.Row, employeeCode = a.EmployeeCode, fields = a.Fields }).ToList(),
                incompleteDraft = createdIncomplete.Count,
                managersUnresolved,
                newDepartments,
                newBranches,
                salariesHeld,
                possibleDuplicates,
                hierarchyLinked,
                payrollProfilesCreated,
                payrollProfilesRepaired,
                hierarchyLinksRecovered,
                importBatchId,
                errors = allErrors,
                warnings = allWarnings,
                createdIncomplete,
            });
        }
    }

    /// <param name="ImportKey">Optional client-generated id for this file, re-sent on retry. See <see cref="Import"/>.</param>
    /// <param name="ConfirmReimport">True to import a file whose exact content was already committed for this tenant
    /// (refused otherwise — see the re-import guard in <see cref="RunEmployeeImportAsync"/>).</param>
    /// <param name="ReimportReason">Required with <paramref name="ConfirmReimport"/>; recorded in the audit trail.</param>
    public record ImportEmployeesRequest(string CsvContent, Guid? ImportKey = null, bool ConfirmReimport = false, string? ReimportReason = null);

    /// <summary>An earlier committed import of the same file content, for the re-import guard.</summary>
    private sealed record EarlierImport(Guid ImportBatchId, DateTime ImportedAtUtc, string ImportedBy, int Created);

    /// <summary>The most recent committed import of this exact content (other than <paramref name="currentBatchId"/>).</summary>
    private async Task<EarlierImport?> FindEarlierCommitOfContentAsync(Guid tenantId, string contentSha256, Guid currentBatchId, CancellationToken ct)
    {
        var markers = await Zayra.Api.Infrastructure.Data.ScopedBypass
            .NullableTenantWide(_db.AuditLogs, tenantId,
                "Re-import guard: the tenant's committed employee-import markers, whatever company the importer was scoped to — the same file must be recognised tenant-wide. Tenant re-applied; markers only.")
            .AsNoTracking()
            .Where(a => a.EntityName == ImportBatchEntityName && a.Action == ImportCommittedAction)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => new { a.Metadata, a.UserId, a.CreatedAtUtc })
            .ToListAsync(ct);
        foreach (var marker in markers)
        {
            ImportCommitRecord? record;
            try { record = marker.Metadata is null ? null : JsonSerializer.Deserialize<ImportCommitRecord>(marker.Metadata); }
            catch (JsonException) { continue; }
            if (record is null || record.ImportBatchId == currentBatchId
                || !string.Equals(record.ContentSha256, contentSha256, StringComparison.OrdinalIgnoreCase)) continue;
            var who = marker.UserId is Guid uid
                ? await Zayra.Api.Infrastructure.Data.ScopedBypass
                    .TenantWide(_db.Users, tenantId, "Re-import guard: name the user who imported the earlier file, whatever company scope they had. Tenant re-applied; name and email only.")
                    .AsNoTracking().Where(u => u.Id == uid)
                    .Select(u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName + " (" + u.Email + ")")
                    .FirstOrDefaultAsync(ct)
                : null;
            return new EarlierImport(record.ImportBatchId, marker.CreatedAtUtc, who ?? string.Empty, record.Created);
        }
        return null;
    }

    /// <summary>Transaction-scoped advisory-lock key for one (tenant, file content), so two commits of the same file
    /// under different import keys serialize and the second sees the first's marker.</summary>
    private static long ImportContentLockKey(Guid tenantId, string contentSha256)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"EMPIMPRT-CONTENT:{tenantId:D}:{contentSha256}"));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
    }

    /// <summary>A statement gave up waiting for a lock (lock_timeout, SQLSTATE 55P03), however it was wrapped.</summary>
    private static bool IsLockTimeout(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is Npgsql.PostgresException { SqlState: "55P03" }) return true;
        return false;
    }

    // ── Commit marker (see Import: RETRY / CLIENT KEY) ─────────────────────────────────────────────────
    internal const string ImportCommittedAction = "employee.import_committed";

    /// <summary>Why the taken-code read drops the company filter: the (TenantId, EmployeeCode) unique index spans
    /// every company and soft-deleted rows, so "taken" must too. Only the code column is read.</summary>
    private const string TakenCodesBypassJustification =
        "Employee-code uniqueness: the unique (TenantId, EmployeeCode) index spans every company and soft-deleted rows, so the taken-code set must too. Tenant re-applied; only codes are read.";
    internal const string ImportBatchEntityName = "EmployeeImportBatch";

    /// <summary>What the marker audit row records: counts and a content hash — never a row value.</summary>
    internal sealed record ImportCommitRecord(Guid ImportBatchId, string ContentSha256, int Received, int Created, int Repaired, int Skipped, int Failed);

    /// <summary>Execution-strategy state for one import request, shared by its attempts.</summary>
    private sealed class ImportCommitAttempt
    {
        public IActionResult? Outcome { get; set; }
        public bool CommitInFlight { get; set; }
    }

    /// <summary>True when a save failed for a reason the execution strategy would retry (the provider marks the
    /// error transient, or it timed out) rather than a rule the data broke.</summary>
    private static bool IsTransientDatabaseFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is TimeoutException || e is Npgsql.NpgsqlException { IsTransient: true }) return true;
        return false;
    }

    /// <summary>
    /// The import's content fingerprint, taken over the PARSED rows, not the raw bytes: a spreadsheet re-save changes
    /// line endings, adds a BOM or a trailing newline, and must still be recognised as the same file (otherwise every
    /// code-less row is imported again). Headers are trimmed and lower-cased, cells trimmed, blank rows ignored. A file
    /// that does not parse falls back to the same normalisation on the text, so it still gets a stable fingerprint.
    /// </summary>
    internal static string ImportContentSha256(string? csv)
    {
        var text = (csv ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        string canonical;
        try
        {
            var rows = Csv.Parse(text);
            var headers = Csv.SplitRow(text.Split('\n', 2)[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
            var sb = new StringBuilder(string.Join('\u001F', headers));
            foreach (var row in rows)
            {
                var cells = headers.Select(h => row.TryGetValue(h, out var v) ? v.Trim() : string.Empty).ToList();
                if (cells.All(string.IsNullOrEmpty)) continue;
                sb.Append('\u001E').Append(string.Join('\u001F', cells));
            }
            canonical = sb.ToString();
        }
        catch (CsvShapeException)
        {
            canonical = string.Join('\n', text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Transaction-scoped advisory-lock key for one (tenant, import key), namespaced so it never collides
    /// with the other advisory-lock users (establishment cells, audit chains).</summary>
    private static long ImportKeyLockKey(Guid tenantId, Guid importBatchId)
    {
        Span<byte> buffer = stackalloc byte[40];
        Encoding.ASCII.GetBytes("EMPIMPRT", buffer[..8]);
        tenantId.TryWriteBytes(buffer.Slice(8, 16));
        importBatchId.TryWriteBytes(buffer.Slice(24, 16));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash[..8]);
    }

    /// <summary>The committed-import marker for this batch, or null when no import with this id has committed.</summary>
    private async Task<ImportCommitRecord?> FindCommittedImportAsync(Guid tenantId, Guid importBatchId, CancellationToken ct)
    {
        var entityId = importBatchId.ToString();
        var metadata = await Zayra.Api.Infrastructure.Data.ScopedBypass
            .NullableTenantWide(_db.AuditLogs, tenantId,
                "Import commit marker: looked up by exact tenant + batch id; the company stamp on the audit row must not hide it from the same tenant's retry. Tenant re-applied.")
            .AsNoTracking()
            .Where(a => a.EntityName == ImportBatchEntityName && a.EntityId == entityId && a.Action == ImportCommittedAction)
            .Select(a => a.Metadata)
            .FirstOrDefaultAsync(ct);
        if (metadata is null) return null;
        try { return JsonSerializer.Deserialize<ImportCommitRecord>(metadata); }
        catch (JsonException) { return new ImportCommitRecord(importBatchId, string.Empty, 0, 0, 0, 0, 0); }
    }

    /// <summary>The answer to a repeated ImportKey: the recorded summary for the same file (nothing is imported
    /// again), or 409 for a different file under the same key.</summary>
    private IActionResult ImportReplay(ImportCommitRecord landed, string contentSha256, Guid importBatchId)
    {
        if (!string.Equals(landed.ContentSha256, contentSha256, StringComparison.OrdinalIgnoreCase))
            return Conflict(new
            {
                error = "import_key_reused",
                message = "This import key was already used for a different file. Nothing was imported — start a new import.",
                importBatchId,
            });
        return Ok(new
        {
            received = landed.Received,
            imported = landed.Created,
            created = landed.Created,
            repaired = landed.Repaired,
            skipped = landed.Skipped,
            failed = landed.Failed,
            replayed = true,
            importBatchId,
            errors = Array.Empty<string>(),
            warnings = new[] { "This file was already imported with the same import key — nothing was imported again. The counts are from that import." },
        });
    }

    /// <summary>
    /// 400 listing every unrecognised or duplicated CSV column, or null when the header row is safe to read.
    /// Shared by <see cref="Import"/> and <see cref="ImportPreview"/> so the dry run and the commit reject
    /// exactly the same files. The response names each offending column and the nearest valid one, and says
    /// plainly that nothing was written — the previous behaviour was to accept the file and lose the column.
    /// </summary>
    private IActionResult? HeaderRejection(string? csvContent)
    {
        var problems = Zayra.Api.Infrastructure.Employees.EmployeeCsvHeaderValidator.Validate(csvContent);
        if (problems.Count == 0) return null;
        return BadRequest(new
        {
            message = problems.Count == 1
                ? $"The file's header row has 1 unusable column. Nothing was imported. {problems[0].Message}"
                : $"The file's header row has {problems.Count} unusable columns. Nothing was imported.",
            headerErrors = problems.Select(p => new { column = p.Column, suggestion = p.Suggestion, message = p.Message }).ToList(),
            // The accepted column names, so the operator can fix the file without re-downloading the template.
            validHeaders = Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.CsvHeaders,
        });
    }

    // ── CSV CELL PARSERS: INVARIANT CULTURE, AND A PARSE FAILURE IS RECORDED ────────────────────────
    // These three used to parse with the AMBIENT culture while the persisted payroll amounts parsed with
    // InvariantCulture explicitly — two different parsers reading the SAME cells, so "03/04/2025" was March 4
    // or April 3 depending on the container's ICU locale and a decimal comma flipped meaning between the
    // resolver's salary band check and the figure actually stored. Every CSV cell is now read with
    // InvariantCulture (the CSV template's own format), and an unparseable non-empty cell records a typed
    // gap instead of silently becoming null — an expiry that quietly vanished used to take its readiness
    // requirement with it.
    /// <summary>One row's salary cells, parsed ONCE by <see cref="ParseImportSalary"/> for the preview, the commit
    /// and the readiness snapshot, so the three can never disagree about the same cells.</summary>
    private sealed record ImportSalaryCells(decimal BasicSalary, decimal Housing, decimal Transport, decimal Food,
        decimal Mobile, decimal Other, decimal FixedDeduction, IReadOnlyList<string> Errors)
    {
        public decimal Gross => BasicSalary + Housing + Transport + Food + Mobile + Other;
        /// <summary>The row supplies a salary that can become a structure: every cell readable, basic positive.</summary>
        public bool CanAssign => Errors.Count == 0 && BasicSalary > 0m && Gross > 0m;
    }

    /// <summary>
    /// THE salary parser. The commit used to parse these cells with InvariantCulture and report a bad cell, while
    /// the preview parsed only BasicSalary with the server's culture and never applied the allowance or zero-basic
    /// rules, so the dry run could say "fine" about a row the commit then refused a salary for. A malformed cell
    /// is reported (never read as 0 — a zero basic means zero GOSI and zero end-of-service accrual in every GCC
    /// pack), and a zero basic under non-zero allowances is refused for the same reason.
    /// </summary>
    private static ImportSalaryCells ParseImportSalary(Dictionary<string, string> row)
    {
        var errors = new List<string>();
        decimal Amount(string column)
        {
            var raw = row.GetValueOrDefault(column, string.Empty).Trim();
            if (raw.Length == 0) return 0m;
            if (decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) return value;
            errors.Add($"{column} is not a number (found '{raw}')");
            return 0m;
        }
        var cells = new ImportSalaryCells(Amount("BasicSalary"), Amount("HousingAllowance"), Amount("TransportAllowance"),
            Amount("FoodAllowance"), Amount("MobileAllowance"), Amount("OtherAllowance"), Amount("FixedDeduction"), errors);
        if (errors.Count == 0 && cells.Gross > 0m && cells.BasicSalary <= 0m)
            errors.Add($"BasicSalary is zero but the allowances total {cells.Gross}");
        return cells;
    }

    /// <summary>The salary-structure refusal for one row, or null when a structure may be created — shared by the
    /// preview and the commit so the dry run predicts exactly the gap the commit records.</summary>
    private static ImportGap? SalaryStructureGap(ImportSalaryCells salary, bool joiningDateKnown)
    {
        if (salary.Errors.Count > 0)
            return new ImportGap("pay:salaryReview", "pay",
                $"{string.Join("; ", salary.Errors)} — no salary structure was created. Correct it in Payroll (basic salary drives GOSI, end-of-service and loss-of-pay).", null);
        if (salary.Gross > 0m && !joiningDateKnown)
            return new ImportGap("pay:salaryReview", "pay",
                "No salary structure was created: its effective date is the joining date, which could not be read. Set the joining date, then add the salary.", null);
        return null;
    }

    /// <summary>A row's joining date. <see cref="Unparsed"/>: a non-empty cell that is not a date — the value is
    /// then UNKNOWN (default), never a guess. A blank cell means the import date (<see cref="Supplied"/> false).</summary>
    private sealed record ImportJoiningDate(DateTime Value, bool Supplied, bool Unparsed, string Raw);

    private static ImportJoiningDate ParseImportJoiningDate(Dictionary<string, string> row)
    {
        var raw = row.GetValueOrDefault("JoiningDate", string.Empty).Trim();
        if (raw.Length == 0)
            return new ImportJoiningDate(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc), false, false, raw);
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return new ImportJoiningDate(DateTime.SpecifyKind(parsed, DateTimeKind.Utc), true, false, raw);
        return new ImportJoiningDate(default, true, true, raw);
    }

    /// <summary>Records the typed gap + warning for an unreadable joining date (preview and commit alike).</summary>
    private static void AddUnparsedJoiningDateGap(ResolvedImportRow resolved, ImportJoiningDate joining)
    {
        if (!joining.Unparsed) return;
        resolved.Gaps.Add(new ImportGap(UnparsedDateGapType, "data",
            $"JoiningDate '{joining.Raw}' is not a valid date (expected YYYY-MM-DD) — left unknown and flagged; "
            + "the employee stays Draft, and no salary structure or reporting-line date is derived from it.", joining.Raw));
        resolved.Warnings.Add($"JoiningDate '{joining.Raw}' is not a valid date — imported as Draft without a joining date; set it before activating.");
    }

    /// <summary>The readable joining date as a date, or null when it is unknown (an unparseable cell).</summary>
    private static DateOnly? ImportJoiningDateOnly(DateTime joiningDate) =>
        joiningDate == default ? null : DateOnly.FromDateTime(joiningDate);

    /// <summary>A row that refuses the whole import (see the ROW GATE in <see cref="RunEmployeeImportAsync"/>).</summary>
    internal sealed record ImportRowRefusal(int Row, string EmployeeCode, string Problem);

    /// <summary>
    /// The rows that make the import refuse the WHOLE file — ONE function for the commit and the preview.
    /// Row numbers count the header as row 1, as everywhere else in the import. A row whose every cell is blank
    /// (a spreadsheet's trailing ",,,,") is not a person and is ignored rather than refused.
    /// </summary>
    internal static List<ImportRowRefusal> ImportRowRefusals(
        IReadOnlyList<Dictionary<string, string>> rows,
        IReadOnlyDictionary<string, Employee> visibleExistingByCode,
        IReadOnlySet<string> takenCodes)
    {
        var refusals = new List<ImportRowRefusal>();
        var firstRowOfCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rowNum = 1;
        foreach (var row in rows)
        {
            rowNum++;
            if (row.Values.All(string.IsNullOrWhiteSpace)) continue;
            var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            var name = row.GetValueOrDefault("FullName", string.Empty).Trim();
            if (name.Length == 0)
            {
                refusals.Add(new ImportRowRefusal(rowNum, code, "FullName is empty — every row must name the person"));
                continue;
            }
            if (code.Length == 0) continue;
            if (firstRowOfCode.TryGetValue(code, out var firstRow))
            {
                refusals.Add(new ImportRowRefusal(rowNum, code,
                    $"EmployeeCode '{code}' is also used by row {firstRow} — each person needs their own code"));
                continue;
            }
            firstRowOfCode[code] = rowNum;
            if (!visibleExistingByCode.ContainsKey(code) && takenCodes.Contains(code))
                refusals.Add(new ImportRowRefusal(rowNum, code,
                    $"EmployeeCode '{code}' already belongs to an employee outside your access or a deleted record — use another code, or leave it blank to generate one"));
        }
        return refusals;
    }

    /// <summary>The 422 for <see cref="ImportRowRefusals"/>: every bad row named, nothing written.</summary>
    private UnprocessableEntityObjectResult ImportRowsRefused(IReadOnlyList<ImportRowRefusal> refusals, int received, Guid importBatchId)
    {
        var first = refusals[0];
        var where = string.IsNullOrEmpty(first.EmployeeCode) ? $"Row {first.Row}" : $"Row {first.Row} (EmployeeCode '{first.EmployeeCode}')";
        return UnprocessableEntity(new
        {
            error = "import_rows_invalid",
            message = $"{where}: {first.Problem}."
                      + (refusals.Count > 1 ? $" {refusals.Count - 1} more row(s) need fixing too." : string.Empty)
                      + " Nothing from this file was imported — correct it and import it again.",
            stage = "rows",
            failedRows = refusals.Take(100).Select(r => new { row = r.Row, employeeCode = r.EmployeeCode, problem = r.Problem }).ToList(),
            received, created = 0, repaired = 0, skipped = 0, failed = received,
            importBatchId,
        });
    }

    /// <summary>
    /// Parse the employee file, refusing it (422, every bad row named with its cell count and the header's) when
    /// any row's width differs from the header's. Shared by the commit and the preview.
    /// </summary>
    private IActionResult? ParseImportRows(string? csvContent, out List<Dictionary<string, string>> rows)
    {
        try
        {
            rows = Csv.Parse(csvContent ?? string.Empty);
            return null;
        }
        catch (CsvShapeException ex)
        {
            rows = new List<Dictionary<string, string>>();
            return UnprocessableEntity(new
            {
                error = "csv_row_shape",
                message = ex.Message,
                expectedCells = ex.HeaderCount,
                failedRows = ex.Mismatches.Take(100).Select(m => new
                {
                    row = m.RowNumber,
                    cells = m.CellCount,
                    expected = ex.HeaderCount,
                    problem = $"has {m.CellCount} cells but the header has {ex.HeaderCount} — usually an unquoted thousands separator (8,000) or a comma inside an unquoted name",
                }).ToList(),
                created = 0,
            });
        }
    }

    /// <summary>The status a row lands in (shared by preview and commit): blank ⇒ Draft; Active with an activation
    /// blocker ⇒ Draft; an unknown joining date ⇒ Draft whatever was asked; any other status as given.</summary>
    private static string ImportLandingStatus(string csvStatus, EmployeeReadiness readiness, DateTime joiningDate)
    {
        if (string.IsNullOrWhiteSpace(csvStatus) || joiningDate == default) return EmployeeStatuses.Draft;
        return string.Equals(csvStatus, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)
            ? (readiness.IsBlocked ? EmployeeStatuses.Draft : EmployeeStatuses.Active)
            : csvStatus;
    }

    /// <summary>
    /// The Employee a CSV row becomes — shared by the commit and the preview, so the preview records the same
    /// data:unparsed* gaps (<paramref name="gaps"/>) and checks the same values against the columns
    /// (EmployeeImportStorageGuard) as the commit. Readiness columns are stamped by the caller.
    /// </summary>
    private static Employee BuildImportedEmployee(Guid tenantId, Dictionary<string, string> row, ResolvedImportRow resolved,
        string name, string finalCode, string workEmail, string status, DateTime joiningDate, List<ImportGap> gaps)
    {
        var deptNameRaw = row.GetValueOrDefault("Department", string.Empty).Trim();
        var desigTitleRaw = row.GetValueOrDefault("Designation", string.Empty).Trim();
        return new Employee
        {
            TenantId = tenantId,
            CompanyId = resolved.CompanyId,
            BranchId = resolved.BranchId,
            CostCenterId = resolved.CostCenterId,
            EmployeeCode = finalCode,
            FullName = name,
            EnglishName = name,
            ArabicName = row.GetValueOrDefault("ArabicName", string.Empty),
            PreferredName = row.GetValueOrDefault("PreferredName", string.Empty),
            PersonalEmail = row.GetValueOrDefault("PersonalEmail", string.Empty),
            WorkEmail = workEmail,
            Phone = row.GetValueOrDefault("Phone", string.Empty),
            Gender = row.GetValueOrDefault("Gender", string.Empty),
            DateOfBirth = ReadCsvDate(row, "DateOfBirth", gaps),
            Nationality = row.GetValueOrDefault("Nationality", string.Empty),
            MaritalStatus = row.GetValueOrDefault("MaritalStatus", string.Empty),
            // Explicit CountryCode column wins, else the EMPLOYING company's country — the one rule, the
            // one helper. A blank country resolves an EMPTY statutory floor, so a file without the column
            // used to import a whole workforce with no jurisdiction gate applied to any of it.
            CountryCode = HomeJurisdiction.DeriveEmployeeCountry(
                row.GetValueOrDefault("CountryCode", string.Empty), resolved.CompanyCountryCode),
            Department = deptNameRaw,
            DepartmentId = resolved.DepartmentId,
            Designation = desigTitleRaw,
            DesignationId = resolved.DesignationId,
            GradeId = resolved.GradeId,
            PositionId = resolved.PositionId,
            Grade = resolved.FinalGradeCode,
            JobTitle = row.GetValueOrDefault("JobTitle", desigTitleRaw),
            EmploymentType = row.GetValueOrDefault("EmploymentType", "Full-time"),
            ContractType = row.GetValueOrDefault("ContractType", string.Empty),
            Status = status,
            // Kept EXACTLY as parsed: `default` means "the file gave no readable joining date"; the readiness
            // evaluator blocks activation on it. Re-defaulting it here would reinstate the guess.
            JoiningDate = joiningDate,
            ConfirmationDate = ReadCsvDate(row, "ConfirmationDate", gaps),
            ProbationStartDate = ReadCsvDate(row, "ProbationStartDate", gaps),
            ProbationEndDate = ReadCsvDate(row, "ProbationEndDate", gaps),
            NoticePeriodDays = ReadCsvInt(row, "NoticePeriodDays", gaps),
            Branch = resolved.BranchNameEn,
            CostCenter = resolved.CostCenterCode,
            WorkLocation = row.GetValueOrDefault("WorkLocation", string.Empty).Trim(),
            ShiftPolicyCode = row.GetValueOrDefault("ShiftPolicyCode", string.Empty).Trim(),
            LeavePolicyCode = row.GetValueOrDefault("LeavePolicyCode", string.Empty).Trim(),
            AttendancePolicyCode = row.GetValueOrDefault("AttendancePolicyCode", string.Empty).Trim(),
            PassportNumber = row.GetValueOrDefault("PassportNumber", string.Empty).Trim(),
            PassportIssueDate = ReadCsvDate(row, "PassportIssueDate", gaps),
            PassportExpiryDate = ReadCsvDate(row, "PassportExpiryDate", gaps),
            VisaNumber = row.GetValueOrDefault("VisaNumber", string.Empty).Trim(),
            VisaIssueDate = ReadCsvDate(row, "VisaIssueDate", gaps),
            VisaExpiryDate = ReadCsvDate(row, "VisaExpiryDate", gaps),
            IqamaNumber = row.GetValueOrDefault("IqamaNumber", string.Empty).Trim(),
            MuqeemNumber = row.GetValueOrDefault("MuqeemNumber", string.Empty).Trim(),
            GosiReference = row.GetValueOrDefault("GosiReference", string.Empty).Trim(),
            EmiratesId = row.GetValueOrDefault("EmiratesId", string.Empty).Trim(),
            LaborCardNumber = row.GetValueOrDefault("LaborCardNumber", string.Empty).Trim(),
            VisaFileNumber = row.GetValueOrDefault("VisaFileNumber", string.Empty).Trim(),
            Qid = row.GetValueOrDefault("Qid", string.Empty).Trim(),
            CivilId = row.GetValueOrDefault("CivilId", string.Empty).Trim(),
            ResidencyNumber = row.GetValueOrDefault("ResidencyNumber", string.Empty).Trim(),
            ResidencyIssueDate = ReadCsvDate(row, "ResidencyIssueDate", gaps),
            WorkPermitNumber = row.GetValueOrDefault("WorkPermitNumber", string.Empty).Trim(),
            WorkPermitIssueDate = ReadCsvDate(row, "WorkPermitIssueDate", gaps),
            SponsorName = row.GetValueOrDefault("SponsorName", string.Empty).Trim(),
            SaudiOrNonSaudi = row.GetValueOrDefault("SaudiOrNonSaudi", string.Empty).Trim(),
            IdType = row.GetValueOrDefault("IdType", string.Empty).Trim(),
            IdNumber = row.GetValueOrDefault("IdNumber", string.Empty).Trim(),
            OccupationCode = row.GetValueOrDefault("OccupationCode", string.Empty).Trim(),
            EstablishmentId = row.GetValueOrDefault("EstablishmentId", string.Empty).Trim(),
            WorkLocationId = row.GetValueOrDefault("WorkLocationId", string.Empty).Trim(),
            ContractReference = row.GetValueOrDefault("ContractReference", string.Empty).Trim(),
            WorkPermitReference = row.GetValueOrDefault("WorkPermitReference", string.Empty).Trim(),
            QiwaEmployeeReference = row.GetValueOrDefault("QiwaEmployeeReference", string.Empty).Trim(),
            QiwaSyncStatus = row.GetValueOrDefault("QiwaSyncStatus", string.Empty).Trim(),
            // Parity columns (registry-driven): emergency contact, contract window, GCC-ID expiries
            // (first-class scalars the readiness pay-gate reads), Qiwa contract number.
            EmergencyContactName = row.GetValueOrDefault("EmergencyContactName", string.Empty).Trim(),
            EmergencyContactPhone = row.GetValueOrDefault("EmergencyContactPhone", string.Empty).Trim(),
            ContractStartDate = ReadCsvDate(row, "ContractStartDate", gaps),
            ContractEndDate = ReadCsvDate(row, "ContractEndDate", gaps),
            IqamaExpiryDate = ReadCsvDate(row, "IqamaExpiry", gaps),
            EmiratesIdExpiryDate = ReadCsvDate(row, "EmiratesIdExpiry", gaps),
            QidExpiryDate = ReadCsvDate(row, "QidExpiry", gaps),
            CivilIdExpiryDate = ReadCsvDate(row, "CivilIdExpiry", gaps),
            QiwaContractNumber = row.GetValueOrDefault("QiwaContractNumber", string.Empty).Trim(),
        };
    }

    /// <summary>The gap type a cell that could not be read records. Category "data" so the advisory
    /// re-stamp folds it in as a recommended field fix (never a new activation blocker).</summary>
    internal const string UnparsedDateGapType = "data:unparsedDate";
    internal const string UnparsedNumberGapType = "data:unparsedNumber";

    /// <summary><paramref name="gaps"/> is the row's gap list on the commit path (null on the dry-run /
    /// snapshot paths, which only need the value).</summary>
    private static DateOnly? ReadCsvDate(Dictionary<string, string> row, string key, List<ImportGap>? gaps = null)
    {
        var value = row.GetValueOrDefault(key, string.Empty).Trim();
        if (value.Length == 0) return null;
        if (DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            return date;
        gaps?.Add(new ImportGap(UnparsedDateGapType, "data",
            $"{key} '{value}' is not a valid date (expected YYYY-MM-DD) — left blank and flagged.", value));
        return null;
    }

    private static int? ReadCsvInt(Dictionary<string, string> row, string key, List<ImportGap>? gaps = null)
    {
        var value = row.GetValueOrDefault(key, string.Empty).Trim();
        if (value.Length == 0) return null;
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number;
        gaps?.Add(new ImportGap(UnparsedNumberGapType, "data",
            $"{key} '{value}' is not a whole number — left blank and flagged.", value));
        return null;
    }

    /// <summary>Materializes a readiness snapshot from a CSV row (§5.4 — built at the call-site). No
    /// documents exist at import, so doc:* requirements evaluate as missing; identity/payroll numbers +
    /// expiries come straight off the row.</summary>
    private static Zayra.Api.Infrastructure.Employees.EmployeeReadinessSnapshot ImportReadinessSnapshot(
        Dictionary<string, string> row, Guid? deptId, Guid? desigId, DateTime jd)
    {
        string V(string k) => row.GetValueOrDefault(k, string.Empty).Trim();
        return new Zayra.Api.Infrastructure.Employees.EmployeeReadinessSnapshot
        {
            CountryCode = V("CountryCode"),
            Nationality = V("Nationality"),
            EnglishName = V("FullName"),
            FullName = V("FullName"),
            Gender = V("Gender"),
            DateOfBirth = ReadCsvDate(row, "DateOfBirth"),
            WorkEmail = V("WorkEmail"),
            Phone = V("Phone"),
            DepartmentId = deptId,
            DesignationId = desigId,
            JoiningDate = jd,
            ContractType = V("ContractType"),
            EmploymentType = V("EmploymentType"),
            PassportNumber = V("PassportNumber"),
            PassportExpiryDate = ReadCsvDate(row, "PassportExpiryDate"),
            IqamaNumber = V("IqamaNumber"),
            IqamaExpiryDate = ReadCsvDate(row, "IqamaExpiry"),
            EmiratesIdExpiryDate = ReadCsvDate(row, "EmiratesIdExpiry"),
            QidExpiryDate = ReadCsvDate(row, "QidExpiry"),
            CivilIdExpiryDate = ReadCsvDate(row, "CivilIdExpiry"),
            GosiReference = V("GosiReference"),
            EmiratesId = V("EmiratesId"),
            Qid = V("Qid"),
            CivilId = V("CivilId"),
            IdNumber = V("IdNumber"),
            VisaNumber = V("VisaNumber"),
            VisaExpiryDate = ReadCsvDate(row, "VisaExpiryDate"),
            WorkPermitNumber = V("WorkPermitNumber"),
            MuqeemNumber = V("MuqeemNumber"),
            LaborCardNumber = V("LaborCardNumber"),
            QiwaContractNumber = V("QiwaContractNumber"),
            BankIban = V("IBAN"),
            MolId = V("MolId"),
            BankRoutingCode = V("BankRoutingCode"),
            PaymentMethod = V("PaymentMethod"),
            SocialInsuranceReference = V("SocialInsuranceReference"),
            // Same parser as the commit, and no structure is created without a known joining date.
            HasSalary = ParseImportSalary(row).CanAssign && jd != default,
        };
    }

    /// <param name="importStructures">Structures already resolved or staged by THIS import. A query cannot see an
    /// Added-but-unsaved row, so without it every row of a grade staged its own copy of the same (company, code)
    /// structure — 250 duplicates in a 250-row file, all saved in one transaction.</param>
    /// <param name="asOf">The date the grade standard is read on (Release A: the matrix in force then).</param>
    /// <param name="warnings">Release A: where an allowance the grade has no matrix value for is reported.</param>
    private async Task<SalaryStructure> ResolveImportSalaryStructureAsync(Guid tenantId, Guid? companyId, Grade? grade,
        string requestedCode, string currency,
        IDictionary<(Guid TenantId, Guid? CompanyId, string Code), SalaryStructure> importStructures, CancellationToken ct,
        DateOnly? asOf = null, ICollection<string>? warnings = null)
    {
        var code = string.IsNullOrWhiteSpace(requestedCode)
            ? grade is not null ? $"GRADE-{grade.Code}" : "EMPLOYEE-IMPORT"
            : requestedCode.Trim();
        var key = (tenantId, companyId, code);
        if (importStructures.TryGetValue(key, out var staged)) return staged;

        var existing = await _db.SalaryStructures
            .Where(s => s.TenantId == tenantId && s.Code == code && !s.IsDeleted && (s.CompanyId == companyId || s.CompanyId == null))
            .OrderByDescending(s => s.CompanyId == companyId)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            importStructures[key] = existing;
            return existing;
        }

        var structure = new SalaryStructure
        {
            TenantId = tenantId,
            CompanyId = companyId,
            Code = code,
            Name = grade is not null ? $"{grade.Name} salary structure" : "Imported employee salary structure",
            Currency = string.IsNullOrWhiteSpace(currency) ? await _db.ResolveTenantCurrencyAsync(tenantId, ct) : currency,
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedBy = GetUserId()
        };
        _db.SalaryStructures.Add(structure);
        importStructures[key] = structure;

        // Release A: one fact in one place. A release_a tenant's grade standard is the matrix (Benefits by grade), never the
        // frozen legacy pay scale. An allowance the grade has no value for gets no line — it is reported, not guessed.
        if (grade is not null && await EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId, ct))
        {
            var on = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var allowances = await EntitlementMatrixService.CashAllowancesAsync(_db, tenantId, grade.Id, companyId, on, ct);
            _db.SalaryComponents.AddRange(EntitlementMatrixService.SalaryComponentsFor(allowances, tenantId, structure.Id));
            foreach (var missing in allowances.Where(a => a.Missing))
                warnings?.Add($"Salary structure {code}: grade {grade.Code} has no {EntitlementComponentRules.For(missing.ComponentCode)?.NameEn.ToLowerInvariant() ?? missing.ComponentCode} "
                    + $"in Benefits by grade on {on:yyyy-MM-dd}, so the structure has no line for it. Each employee's own figure from the file is kept; "
                    + "set the grade's value in Benefits by grade.");
        }
        else if (grade is not null)
        {
            var components = await _db.GradePayScaleComponents
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.GradeId == grade.Id && c.IsActive)
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct);
            foreach (var component in components)
            {
                _db.SalaryComponents.Add(new SalaryComponent
                {
                    TenantId = tenantId,
                    SalaryStructureId = structure.Id,
                    Code = component.ComponentCode,
                    Name = component.ComponentName,
                    ComponentType = component.ComponentType,
                    CalculationType = component.CalculationType,
                    Amount = component.Amount,
                    Percentage = component.Percentage,
                    IsTaxable = component.IsTaxable
                });
            }
        }

        return structure;
    }

    // ── Import preview (dry-run: validates without committing) ─────────────────

    /// <summary>The identity CSV columns whose applicability is COUNTRY- or NATIONALITY-conditional — the
    /// set the import preview warns on when populated for a row they don't apply to (a Saudi national's row
    /// carrying an Iqama value). Blank conditional columns are NEVER an error. Computed once from the catalog.</summary>
    private static readonly IReadOnlyList<Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.EmployeeFieldDescriptor> ConditionalIdentityColumns =
        Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.Catalog
            .Where(d => d.CsvHeader is not null && d.Section == "identity"
                && (d.Countries is not null || d.Applicability != Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.FieldApplicability.All))
            .ToList();

    /// <summary>Country-aware, per-row CSV warnings (§4): a populated cell for a column that is not
    /// applicable to the row's (country, nationality) → warning; a populated identity value that fails the
    /// country pack FORMAT regex → warning with the hint. Blank irrelevant columns produce nothing. Warnings
    /// only — the file is never rejected on these.
    ///
    /// The non-applicable warning used to read "it will be ignored, not imported". That was the opposite of
    /// what happens: <see cref="Import"/> writes every identity column unconditionally
    /// (<c>IqamaNumber = row.GetValueOrDefault("IqamaNumber").Trim()</c> and the forty like it), with no
    /// applicability check anywhere on the commit path. The dry run was telling the operator a value would be
    /// discarded when it was about to be persisted — and the dry run is the only safety mechanism a bulk
    /// write has. The wording now matches the commit. Keeping the value is the deliberate choice: this
    /// importer's law is accept-never-block (a row is dropped only for a missing name or a duplicate code),
    /// so a surprising value is surfaced for review rather than silently thrown away.</summary>
    private static List<string> CountryAwareRowWarnings(
        Dictionary<string, string> row, string iso2, string? nationality,
        Zayra.Api.Application.CountryPack.IIdentityDocumentFormat fmt)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(iso2)) return warnings;
        var visible = Zayra.Api.Infrastructure.Employees.EmployeeFieldRegistry.CatalogFor(iso2, nationality)
            .Where(d => d.CsvHeader is not null)
            .Select(d => d.CsvHeader!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var natLabel = string.IsNullOrWhiteSpace(nationality) ? "this nationality" : nationality!.Trim();

        foreach (var d in ConditionalIdentityColumns)
        {
            var val = row.GetValueOrDefault(d.CsvHeader!, string.Empty).Trim();
            if (val.Length == 0) continue;                       // blank irrelevant column is fine
            if (!visible.Contains(d.CsvHeader!))
            {
                warnings.Add($"{d.CsvHeader} '{val}' is not applicable to {natLabel} in {iso2} — it WILL still be "
                             + $"imported into the {d.CsvHeader} column. Clear the cell if that is not what you meant.");
                continue;
            }
            var (pattern, hint) = fmt.GetFormat(d.Key);          // format check only for the applicable/visible columns
            if (pattern is not null && !System.Text.RegularExpressions.Regex.IsMatch(val, pattern))
                warnings.Add($"{d.CsvHeader} '{val}' does not match the expected format ({hint}) — stored as-is but should be corrected.");
        }
        return warnings;
    }

    [HttpPost("import-preview")]
    [HasPermission("employees.bulk_import")]
    public async Task<IActionResult> ImportPreview([FromBody] ImportEmployeesRequest req,
        CancellationToken ct = default,
        [FromServices] Zayra.Api.Application.CountryPack.ICountryPackResolver? countryPacks = null)
    {
        RequireTenant();
        // THE PREVIEW IS THE DRY RUN, AND ONLY THE DRY RUN. The commit runs to the end in its one transaction and is
        // rolled back; every per-row line below was recorded by the commit as it decided that row. There used to be a
        // second, parallel per-row projection here as well — the same lookups, resolution and readiness evaluated
        // twice per preview, which doubled the memory a large file needed and could still drift from the commit.
        _lastDryRun = null;
        var dry = await RunEmployeeImportAsync(req with { ImportKey = null, ConfirmReimport = false, ReimportReason = null },
            dryRun: true, ct, countryPacks);
        _db.ChangeTracker.Clear();

        var commitCheck = DescribeImportDryRun(dry);
        if (_lastDryRun is { } report && dry is OkObjectResult)
            return Ok(new
            {
                received = report.Created + report.Repaired + report.Skipped,
                commitCheck,
                wouldCreate = report.Created,
                wouldRepair = report.Repaired,
                wouldSkip = report.Skipped,
                wouldFail = 0,
                wouldNeedApproval = report.NeedApproval,
                wouldCreateActive = report.CreateActive,
                wouldCreateDraft = report.CreateDraft,
                fieldGaps = report.FieldGaps,
                rows = report.Rows,
            });

        // Refusals about the FILE itself (unreadable header, mis-shaped rows, too many rows, another import in
        // progress) are returned as they are: there are no rows to preview.
        var body = dry is ObjectResult refusedResult && refusedResult.Value is { } value ? JsonSerializer.SerializeToElement(value) : default;
        var error = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (dry is ObjectResult { StatusCode: 400 } || error is "csv_row_shape" or "import_too_many_rows" or "import_busy")
            return dry;

        // The commit would refuse the file: preview the refused rows, with the commit's verdict on top.
        var refusedRows = new List<object>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("failedRows", out var failed) && failed.ValueKind == JsonValueKind.Array)
            foreach (var f in failed.EnumerateArray())
            {
                string Str(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
                var problem = string.Join(" ", new[] { Str("column"), Str("problem") }.Where(x => x.Length > 0));
                refusedRows.Add(new
                {
                    row = f.TryGetProperty("row", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
                    employeeCode = Str("employeeCode"),
                    fullName = string.Empty,
                    status = "WillFail",
                    projectedStatus = string.Empty,
                    blocking = Array.Empty<string>(),
                    recommended = Array.Empty<string>(),
                    errors = new[] { $"{problem} — the import will be refused until this is corrected." },
                    warnings = Array.Empty<string>(),
                });
            }
        return Ok(new
        {
            received = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("received", out var rc) && rc.ValueKind == JsonValueKind.Number ? rc.GetInt32() : 0,
            commitCheck,
            wouldCreate = 0,
            wouldRepair = 0,
            wouldSkip = 0,
            wouldFail = refusedRows.Count,
            wouldNeedApproval = 0,
            wouldCreateActive = 0,
            wouldCreateDraft = 0,
            fieldGaps = Array.Empty<object>(),
            rows = refusedRows,
        });
    }

    /// <summary>One CSV row as the commit decided it — what the preview lists for that row.</summary>
    private sealed class ImportRowOutcome(int row)
    {
        public int Row { get; } = row;
        public string EmployeeCode { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        /// <summary>WillCreate | WillRepair | Error (not imported: an existing employee with nothing to fill, a separated one, an empty row).</summary>
        public string Status { get; set; } = "Error";
        public string ProjectedStatus { get; set; } = string.Empty;
        public EmployeeReadiness? Readiness { get; set; }
        public string? FinalCode { get; set; }
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();
    }

    /// <summary>One field in the preview's "most common missing details" strip (lower-case names are the wire contract).</summary>
    private sealed record PreviewFieldGap(string field, string label, string gate, string kind, int rowCount);

    private sealed record DryRunReport(int Created, int Repaired, int Skipped, int NeedApproval, int CreateActive, int CreateDraft,
        IReadOnlyList<object> Rows, IReadOnlyList<object> FieldGaps);

    /// <summary>Set by a dry run (see <see cref="ImportPreview"/>); one controller instance serves one request.</summary>
    private DryRunReport? _lastDryRun;

    private static DryRunReport BuildDryRunReport(IEnumerable<ImportRowOutcome> outcomes, IReadOnlyDictionary<string, List<ImportGap>> gapsByCode,
        int created, int repaired, int skipped, int needApproval)
    {
        var fieldGapAgg = new Dictionary<string, (string Label, string Gate, string Kind, int Count)>(StringComparer.OrdinalIgnoreCase);
        void Record(IEnumerable<ReadinessItem> items, string kind)
        {
            foreach (var it in items)
            {
                var cur = fieldGapAgg.GetValueOrDefault(it.Key);
                fieldGapAgg[it.Key] = (it.Label, it.Gate, kind, cur.Count + 1);
            }
        }
        var rows = new List<object>();
        int active = 0, draft = 0;
        foreach (var o in outcomes)
        {
            var gaps = o.FinalCode is not null && gapsByCode.TryGetValue(o.FinalCode, out var g) ? g : new List<ImportGap>();
            var gapItems = gaps.Select(x => EmployeeReadinessEvaluator.ImportGapToItem(x.Type, x.Category)).ToList();
            var blocking = new List<string>();
            var recommended = new List<string>();
            if (o.Status == "WillCreate")
            {
                if (string.Equals(o.ProjectedStatus, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)) active++;
                else if (string.Equals(o.ProjectedStatus, EmployeeStatuses.Draft, StringComparison.OrdinalIgnoreCase)) draft++;
                if (o.Readiness is { } readiness)
                {
                    blocking = readiness.Blocking.Select(b => b.Label).ToList();
                    recommended = readiness.Recommended.Select(b => b.Label).ToList();
                    Record(readiness.Blocking, "blocking");
                    Record(readiness.Recommended, "recommended");
                }
                recommended = recommended.Concat(gapItems.Select(i => i.Label)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Record(gapItems, "recommended");
            }
            rows.Add(new
            {
                row = o.Row,
                employeeCode = o.EmployeeCode,
                fullName = o.FullName,
                status = o.Status,
                projectedStatus = o.ProjectedStatus,
                blocking,
                recommended,
                errors = o.Errors,
                warnings = o.Warnings,
            });
        }
        var fieldGaps = fieldGapAgg
            .Select(kv => new PreviewFieldGap(kv.Key, kv.Value.Label, kv.Value.Gate, kv.Value.Kind, kv.Value.Count))
            .OrderByDescending(x => x.kind == "blocking")
            .ThenByDescending(x => x.rowCount)
            .ThenBy(x => x.label, StringComparer.Ordinal)
            .Cast<object>()
            .ToList();
        return new DryRunReport(created, repaired, skipped, needApproval, active, draft, rows, fieldGaps);
    }

    /// <summary>The preview's summary of a dry-run commit: would it import (and how many), or why it would not.</summary>
    private static object DescribeImportDryRun(IActionResult outcome)
    {
        var objectResult = outcome as ObjectResult;
        var body = objectResult?.Value is { } value ? JsonSerializer.SerializeToElement(value) : default;
        int? Count(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
        string? Text(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (outcome is OkObjectResult)
            return new { outcome = "would_import", created = Count("created") ?? 0, repaired = Count("repaired") ?? 0, skipped = Count("skipped") ?? 0 };
        return new
        {
            outcome = "would_refuse",
            status = objectResult?.StatusCode ?? 500,
            error = Text("error"),
            message = Text("message") ?? "The import would be refused.",
            failedRows = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("failedRows", out var rowsElement)
                ? (object)rowsElement : Array.Empty<object>(),
        };
    }

    // ── Org chart ─────────────────────────────────────────────────────────────

    [HttpGet("org-chart")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<IActionResult> OrgChart(
        [FromServices] IHrmHierarchyService hierarchy,
        [FromQuery] int? rootEmployeeId = null,
        [FromQuery] int maxDepth = 5,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        maxDepth = Math.Clamp(maxDepth, 1, 10);
        var chart = await hierarchy.GetOrgChartAsync(tenantId, rootEmployeeId, maxDepth, ct);
        return Ok(chart);
    }

    // ── Manager assignment ────────────────────────────────────────────────────

    [HttpPut("{id:int}/manager")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> SetManager(
        int id,
        [FromBody] SetManagerRequest req,
        [FromServices] IHrmHierarchyService hierarchy,
        CancellationToken ct)
    {
        try
        {
            if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
            if (req.ManagerEmployeeId.HasValue && !await CanAccessEmployeeAsync(req.ManagerEmployeeId.Value, ct)) return Forbid();
            await hierarchy.SetManagerAsync(RequireTenant(), id, req.ManagerEmployeeId, Context(), ct);
            return NoContent();
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ── Reporting lines ───────────────────────────────────────────────────────

    [HttpGet("{id:int}/reporting-lines")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<ActionResult<IReadOnlyList<ReportingLineDto>>> GetReportingLines(
        int id,
        [FromServices] IHrmHierarchyService hierarchy,
        CancellationToken ct)
    {
        var tenantId = RequireTenant();
        if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
        return Ok(await hierarchy.GetReportingLinesAsync(tenantId, id, ct));
    }

    [HttpPost("{id:int}/reporting-lines")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<ReportingLineDto>> AddReportingLine(
        int id,
        [FromBody] AddReportingLineRequest req,
        [FromServices] IHrmHierarchyService hierarchy,
        CancellationToken ct)
    {
        try
        {
            if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
            if (!await CanAccessEmployeeAsync(req.ManagerEmployeeId, ct)) return Forbid();
            var line = await hierarchy.AddReportingLineAsync(RequireTenant(), id, req, Context(), ct);
            return Created($"/api/employees/{id}/reporting-lines/{line.Id}", line);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:int}/reporting-lines/{lineId:guid}")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> RemoveReportingLine(
        int id,
        Guid lineId,
        [FromServices] IHrmHierarchyService hierarchy,
        CancellationToken ct)
    {
        if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
        return await hierarchy.RemoveReportingLineAsync(RequireTenant(), id, lineId, Context(), ct) ? NoContent() : NotFound();
    }

    [HttpGet("{id:int}/hierarchy")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Auditor")]
    public async Task<ActionResult<HierarchyResolverDto>> ResolveHierarchy(
        int id,
        [FromServices] IHrmHierarchyService hierarchy,
        [FromQuery] int maxDepth = 10,
        CancellationToken ct = default)
    {
        if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
        try
        {
            return Ok(await hierarchy.ResolveHierarchyAsync(RequireTenant(), id, maxDepth, ct));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("{id:int}/workflow-approvers")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Payroll Manager,Payroll Officer,Finance Approver,Finance Controller,Auditor")]
    public async Task<ActionResult<WorkflowApproverResolutionDto>> ResolveWorkflowApprovers(
        int id,
        [FromQuery] string workflowType,
        [FromServices] IHrmHierarchyService hierarchy,
        CancellationToken ct)
    {
        if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
        try
        {
            return Ok(await hierarchy.ResolveWorkflowApproversAsync(RequireTenant(), id, workflowType, ct));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    public record SetManagerRequest(int? ManagerEmployeeId);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<EmployeeDetailDto>> Get(int id, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(id))
            return Forbid();
        var employee = await employeeManagement.GetAsync(tenantId, id, CanViewSensitive(), Context(), cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpPost]
    [HasPermission("employees.write")]
    public async Task<ActionResult<EmployeeDetailDto>> CreateEmployee(EmployeeCreateRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = RequireTenant();

            // Enforce employee limit
            var sub = await _db.TenantSubscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            if (sub is not null && sub.MaxEmployees > 0)
            {
                var count = await _db.Employees.CountAsync(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted, cancellationToken);
                if (count >= sub.MaxEmployees)
                    return StatusCode(402, new
                    {
                        error           = "employee_limit_reached",
                        currentCount    = count,
                        maxAllowed      = sub.MaxEmployees,
                        message         = $"Your plan allows up to {sub.MaxEmployees} active employees. You have {count}. Upgrade your plan to add more.",
                        upgradeRequired = true,
                    });
            }

            // CanViewSensitive() — the write response is a read of the record and obeys the same mask
            // gate as GET {id}; see IEmployeeManagementService.CreateAsync.
            var employee = await employeeManagement.CreateAsync(tenantId, request, Context(), cancellationToken, CanViewSensitive());
            return CreatedAtAction(nameof(Get), new { id = employee.Id }, employee);
        }
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
        // HOME JURISDICTION: the employing company has no country, so nothing can be required and nothing
        // saved. The Add Employee modal disables its submit button on the same condition — this is the
        // authoritative refusal behind that affordance, and it names the company and where the fix lives
        // (the modal renders `message` verbatim).
        catch (CompanyCountryMissingException ex)
        {
            return BadRequest(new
            {
                error       = HomeJurisdiction.MissingCompanyCountryError,
                companyId   = ex.CompanyId,
                fixLocation = HomeJurisdiction.CompanyFixLocation,
                message     = ex.Message,
            });
        }
        // Authoritative never-silent-dup backstop: a STRONG identity match with no explicit acknowledgement.
        // Advisory 409 (never a hard block) — the modal re-surfaces the SAME masked match set the pre-check
        // shows and the operator resolves (View existing / Merge / Create anyway → acknowledgeDuplicate).
        catch (DuplicatePersonException ex)
        {
            var scope = this.GetEntityScope();
            return Conflict(new { error = "possible_duplicate", matches = ex.Matches.Select(m => MaskMatch(m, scope)).ToList() });
        }
        // User-SUPPLIED work-email collision — the one deliberate stop (never silently duplicate the login
        // identity). Advisory: the modal offers the suggested next-free address. Auto-derived never hits this.
        catch (WorkEmailConflictException ex) { return Conflict(new { error = "work_email_conflict", attempted = ex.Attempted, suggestion = ex.Suggestion }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// Advisory pre-create duplicate check the create modal calls before submit. ALWAYS 200 (never blocks) —
    /// the create commit is the authoritative backstop (409 possible_duplicate). Detection is tenant-wide
    /// across companies; each match is scope-masked (a match in a company the caller cannot access returns
    /// canView=false with NO PII, only "another company"). POST (not GET) so sensitive identity values
    /// never land in a URL.
    /// </summary>
    [HttpPost("duplicate-check")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<DuplicateCheckResponse>> DuplicateCheck([FromBody] DuplicateCheckRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var identity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var iv in req.IdentityValues ?? [])
        {
            var key = (iv.FieldKey ?? string.Empty).Trim();
            var value = (iv.Value ?? string.Empty).Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            identity[key] = value;
        }
        var probe = new DuplicateProbe(
            EnglishName: req.EnglishName?.Trim(),
            ArabicName: req.ArabicName?.Trim(),
            DateOfBirth: req.DateOfBirth,
            Nationality: req.Nationality?.Trim(),
            CompanyId: req.CompanyId,
            IdentityValues: identity,
            ExcludeEmployeeId: req.ExcludeEmployeeId);

        var matches = await _duplicateDetector.FindAsync(tenantId, probe, ct);
        var scope = this.GetEntityScope();
        var masked = matches.Select(m => MaskMatch(m, scope)).ToList();
        return Ok(new DuplicateCheckResponse(
            HasStrong: masked.Any(m => m.MatchType == DuplicateMatchTypes.Strong),
            HasProbable: masked.Any(m => m.MatchType == DuplicateMatchTypes.Probable),
            Matches: masked));
    }

    /// <summary>
    /// Work-email preview the create/edit modal calls to mirror the SERVER's authoritative derivation
    /// byte-for-byte (same WorkEmailDeriver the commit uses). POST (not GET) so the name never lands in a URL.
    /// ALWAYS 200. Returns the locked <c>domain</c>, the resolved <c>localPart</c> + full <c>workEmail</c>,
    /// whether it is <c>unique</c>, a <c>suggestion</c> (next-free address on collision), and a <c>status</c>:
    ///   derived            — auto-derived from the name (unique; localPart may carry an auto-suffix),
    ///   user               — the supplied local part is available,
    ///   conflict           — the supplied local part collides (not unique; use suggestion),
    ///   manual-no-domain   — company has no email domain (edge-7 manual entry),
    ///   manual-arabic-only — no romanizable name (e.g. Arabic-only) → manual entry.
    /// </summary>
    [HttpPost("derive-work-email")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<DeriveWorkEmailResponse>> DeriveWorkEmail([FromBody] DeriveWorkEmailRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var company = req.CompanyId is Guid cid
            ? await _db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == cid && !c.IsDeleted)
                .Select(c => new { c.EmailDomain, c.WorkEmailPattern }).FirstOrDefaultAsync(ct)
            : null;
        var domain = (company?.EmailDomain ?? string.Empty).Trim().ToLowerInvariant();
        var pattern = WorkEmailPatterns.Normalize(company?.WorkEmailPattern);

        // Edge-7: no company domain → manual entry (never blocks).
        if (string.IsNullOrWhiteSpace(domain))
            return Ok(new DeriveWorkEmailResponse(domain, pattern, string.Empty, string.Empty, true, null, "manual-no-domain"));

        var providedLocal = (req.LocalPart ?? string.Empty).Trim();
        var userSupplied = providedLocal.Length > 0;
        var local = userSupplied
            ? WorkEmailDeriver.ExtractLocalPart(providedLocal)           // in case they typed a full address
            : WorkEmailDeriver.BuildLocalPart(req.EnglishName, req.ArabicName, pattern);
        if (string.IsNullOrEmpty(local))
            return Ok(new DeriveWorkEmailResponse(domain, pattern, string.Empty, string.Empty, true, null, "manual-arabic-only"));

        var taken = await LoadTenantWorkEmailNormalizedSetAsync(tenantId, req.ExcludeEmployeeId, ct);
        bool IsTaken(string addr) => taken.Contains(AuthService.Normalize(addr));
        var assembled = WorkEmailDeriver.Assemble(local, domain);
        if (!IsTaken(assembled))
            return Ok(new DeriveWorkEmailResponse(domain, pattern, local, assembled, true, null, userSupplied ? "user" : "derived"));

        var suggestion = WorkEmailDeriver.Uniqueify(local, domain, IsTaken);
        return userSupplied
            // Keep the user's local part but flag not-unique + suggest the next-free address (they adjust).
            ? Ok(new DeriveWorkEmailResponse(domain, pattern, local, assembled, false, suggestion, "conflict"))
            // Auto-derived → return the suffixed unique address directly (req 4).
            : Ok(new DeriveWorkEmailResponse(domain, pattern, WorkEmailDeriver.ExtractLocalPart(suggestion), suggestion, true, suggestion, "derived"));
    }

    /// <summary>Builds the persisted dup:* gap Detail + RawValue, scope-masked (S3): a counterpart in a
    /// company outside the importer's scope contributes NO code/name — only the no-PII "another company"
    /// form — so a company-scoped HR user's worklist never leaks a cross-scope person.</summary>
    private static (string Detail, string? Raw) DupGapText(
        Zayra.Api.Application.Common.EntityScopeContext scope, DuplicateCandidate counterpart, IReadOnlyList<string> signals)
    {
        if (!scope.CanAccessCompany(counterpart.CompanyId))
            return ("Possible existing employee in another company — contact a group administrator to resolve.", null);
        var who = string.IsNullOrWhiteSpace(counterpart.EmployeeCode)
            ? counterpart.FullName
            : $"{counterpart.FullName} ({counterpart.EmployeeCode})";
        var why = signals.Count > 0 ? $" — {string.Join("; ", signals)}" : string.Empty;
        return ($"Possible existing employee: {who}{why}.", string.IsNullOrWhiteSpace(counterpart.EmployeeCode) ? null : counterpart.EmployeeCode);
    }

    /// <summary>Scope-mask a raw detector match: cross-company matches (outside the caller's entity scope)
    /// return canView=false with code/name/branch stripped — never leak a person the caller cannot access.</summary>
    private static DuplicateMatchDto MaskMatch(DuplicateMatch m, Zayra.Api.Application.Common.EntityScopeContext scope)
    {
        var canView = scope.CanAccessCompany(m.CompanyId);
        return canView
            ? new DuplicateMatchDto(m.EmployeeId, m.EmployeeCode, m.FullName, string.IsNullOrWhiteSpace(m.Branch) ? null : m.Branch,
                m.CompanyId?.ToString(), m.Status, m.MatchType, m.Signals.ToList(), true)
            : new DuplicateMatchDto(m.EmployeeId, string.Empty, "A matching record exists in another company", null,
                null, string.Empty, m.MatchType, new[] { "Contact a group administrator" }, false);
    }

    /// <summary>
    /// The review queue for new hires: every draft the caller may see, newest first, filterable by
    /// lifecycle. <paramref name="status"/> is <c>awaiting</c> (the default: Submitted or
    /// PendingHrApproval), <c>open</c>, <c>all</c>, or one exact status. Rows carry identity,
    /// placement and lifecycle only; the counts are over the same visible set.
    /// </summary>
    [HttpGet("drafts")]
    [HasPermission("employees.write", "employees.approve")]
    public async Task<ActionResult<EmployeeDraftListResponse>> ListDrafts(
        [FromQuery] string? status = "awaiting", [FromQuery] string? search = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var visible = VisibleDrafts(tenantId);

        var statusCounts = await visible.GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int CountOf(params string[] statuses) => statusCounts.Where(x => statuses.Contains(x.Status)).Sum(x => x.Count);
        var counts = new EmployeeDraftStatusCounts(
            CountOf(EmployeeDraftStatuses.AwaitingApproval),
            CountOf(EmployeeDraftStatuses.Draft),
            CountOf(EmployeeDraftStatuses.Activated),
            CountOf(EmployeeDraftStatuses.Rejected),
            CountOf(EmployeeDraftStatuses.Cancelled));

        var query = visible;
        var filter = (status ?? "awaiting").Trim();
        if (filter.Equals("awaiting", StringComparison.OrdinalIgnoreCase))
            query = query.Where(d => EmployeeDraftStatuses.AwaitingApproval.Contains(d.Status));
        else if (filter.Equals("open", StringComparison.OrdinalIgnoreCase))
            query = query.Where(d => EmployeeDraftStatuses.Open.Contains(d.Status));
        else if (!filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(d => d.Status == filter);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(d => d.EnglishName.ToLower().Contains(term) || d.ArabicName.Contains(term)
                || d.Department.ToLower().Contains(term) || d.Designation.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var drafts = await query.AsNoTracking()
            .OrderByDescending(d => d.SubmittedAtUtc ?? d.CreatedAtUtc).ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = await ToDraftListItemsAsync(tenantId, drafts, cancellationToken);
        return Ok(new EmployeeDraftListResponse(items, total, page, pageSize, counts));
    }

    /// <summary>
    /// One draft for review: the masked draft, its lifecycle summary and, while it is still open, the
    /// activation check — every reason approval would be refused (organisation records that don't
    /// match, entity conflicts, readiness blockers, a login identity already using the work email),
    /// computed with the same resolvers and readiness policy approval uses. Read-only.
    /// </summary>
    [HttpGet("drafts/{draftId:guid}")]
    [HasPermission("employees.write", "employees.approve")]
    public async Task<ActionResult<EmployeeDraftReviewDto>> GetDraft(Guid draftId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var draft = await VisibleDrafts(tenantId).AsNoTracking().SingleOrDefaultAsync(d => d.Id == draftId, cancellationToken);
        if (draft is null) return NotFound();
        var summary = (await ToDraftListItemsAsync(tenantId, new[] { draft }, cancellationToken)).Single();
        var documentCount = await ScopedBypass.TenantWide(_db.EmployeeDocuments, tenantId,
                "Draft documents carry no company until activation; the draft's own visibility was checked above.")
            .CountAsync(x => x.DraftId == draftId && !x.IsDeleted, cancellationToken);
        var check = EmployeeDraftStatuses.IsOpen(draft.Status)
            ? await CheckDraftActivationAsync(tenantId, draft, cancellationToken)
            : null;
        return Ok(new EmployeeDraftReviewDto(summary, EmployeeDraftDto.Project(draft, CanViewSensitive()), documentCount, check));
    }

    /// <summary>
    /// A checker refuses the hire. The reason is required and kept on the audit record; the draft is
    /// closed for good (Rejected), so it can never be approved afterwards. Maker-checker applies.
    /// </summary>
    [HttpPost("drafts/{draftId:guid}/reject")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> RejectDraft(Guid draftId, EmployeeDraftDecisionRequest request, CancellationToken cancellationToken)
    {
        var reason = request?.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 5)
            return BadRequest(new { error = "reason_required", message = "Say why this hire is rejected (at least 5 characters). The reason is kept on the record." });
        if (reason.Length > 1000)
            return BadRequest(new { error = "reason_too_long", message = "Keep the rejection reason under 1,000 characters." });

        var tenantId = RequireTenant();
        var draft = await VisibleDrafts(tenantId).AsNoTracking()
            .Where(d => d.Id == draftId).Select(d => new { d.Status, d.CreatedByUserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (draft is null) return NotFound();
        if (await MakerCheckerRefusalAsync(tenantId, draftId, cancellationToken) is { } refusal) return refusal;
        if (!EmployeeDraftStatuses.IsOpen(draft.Status)) return await DraftClosedConflictAsync(tenantId, draftId, draft.Status, cancellationToken);

        var moved = await TransitionDraftAsync(tenantId, draftId, EmployeeDraftStatuses.Open,
            s => s.SetProperty(d => d.Status, EmployeeDraftStatuses.Rejected).SetProperty(d => d.CurrentStep, EmployeeDraftStatuses.Rejected),
            d => { d.Status = EmployeeDraftStatuses.Rejected; d.CurrentStep = EmployeeDraftStatuses.Rejected; },
            EmployeeDraftAuditActions.Rejected, JsonSerializer.Serialize(new { reason }), cancellationToken);
        if (!moved) return await DraftClosedConflictAsync(tenantId, draftId, await CurrentDraftStatusAsync(tenantId, draftId, cancellationToken), cancellationToken);

        await NotifyBestEffortAsync("Employee draft rejected", $"A new-hire draft was rejected: {reason}", draftId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Withdraw a draft that should not become an employee. Anyone who can see and edit the draft may
    /// withdraw it while it is open; it is then closed for good (Cancelled).
    /// </summary>
    [HttpPost("drafts/{draftId:guid}/cancel")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> CancelDraft(Guid draftId, EmployeeDraftDecisionRequest? request, CancellationToken cancellationToken)
    {
        var reason = request?.Reason?.Trim() ?? string.Empty;
        if (reason.Length > 1000)
            return BadRequest(new { error = "reason_too_long", message = "Keep the reason under 1,000 characters." });

        var tenantId = RequireTenant();
        var status = await VisibleDrafts(tenantId).AsNoTracking()
            .Where(d => d.Id == draftId).Select(d => d.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status is null) return NotFound();
        if (!EmployeeDraftStatuses.IsOpen(status)) return await DraftClosedConflictAsync(tenantId, draftId, status, cancellationToken);

        var moved = await TransitionDraftAsync(tenantId, draftId, EmployeeDraftStatuses.Open,
            s => s.SetProperty(d => d.Status, EmployeeDraftStatuses.Cancelled).SetProperty(d => d.CurrentStep, EmployeeDraftStatuses.Cancelled),
            d => { d.Status = EmployeeDraftStatuses.Cancelled; d.CurrentStep = EmployeeDraftStatuses.Cancelled; },
            EmployeeDraftAuditActions.Cancelled,
            reason.Length == 0 ? null : JsonSerializer.Serialize(new { reason }), cancellationToken);
        if (!moved) return await DraftClosedConflictAsync(tenantId, draftId, await CurrentDraftStatusAsync(tenantId, draftId, cancellationToken), cancellationToken);
        return NoContent();
    }

    [HttpPost("drafts")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<EmployeeDraftDto>> CreateDraft(EmployeeDraftRequest request, CancellationToken cancellationToken)
    {
        if (await DraftManagerRefusalAsync(RequireTenant(), request.ManagerEmployeeId, cancellationToken) is { } managerRefusal)
            return managerRefusal;
        var draft = ApplyDraft(new EmployeeDraft { TenantId = RequireTenant(), CreatedByUserId = GetUserId() }, request);
        draft.ProfileCompletenessScore = CalculateCompleteness(draft, 0);
        _db.EmployeeDrafts.Add(draft);
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.draft_created", "EmployeeDraft", draft.Id.ToString(), cancellationToken);
        return Created($"/api/employees/drafts/{draft.Id}", EmployeeDraftDto.Project(draft, CanViewSensitive()));
    }

    [HttpPut("drafts/{draftId:guid}")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<EmployeeDraftDto>> UpdateDraft(Guid draftId, EmployeeDraftRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        EmployeeDraft? saved = null;
        var refusal = await ChangeOpenDraftAsync(tenantId, draftId, EmployeeDraftAuditActions.Updated, async (draft, ct) =>
        {
            if (request.ManagerEmployeeId is { } managerId && managerId != draft.ManagerEmployeeId
                && await DraftManagerRefusalAsync(tenantId, managerId, ct) is { } managerRefusal)
                return managerRefusal;
            ApplyDraft(draft, request);
            var docs = await ScopedBypass.TenantWide(_db.EmployeeDocuments, tenantId,
                    "A draft's documents carry no company until activation; the draft's visibility was checked under its lock.")
                .CountAsync(x => x.DraftId == draftId && !x.IsDeleted, ct);
            draft.ProfileCompletenessScore = CalculateCompleteness(draft, docs);
            saved = draft;
            return null;
        }, cancellationToken);
        if (refusal is not null) return (ActionResult)refusal;
        return Ok(EmployeeDraftDto.Project(saved!, CanViewSensitive()));
    }

    [HttpPost("drafts/{draftId:guid}/documents")]
    [HasPermission("employees.documents")]
    public async Task<ActionResult<EmployeeDocumentDto>> AddDraftDocument(Guid draftId, EmployeeDocumentRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var draftStatus = await VisibleDrafts(tenantId).Where(x => x.Id == draftId).Select(x => x.Status).SingleOrDefaultAsync(cancellationToken);
        if (draftStatus is null) return NotFound();
        if (!EmployeeDraftStatuses.IsOpen(draftStatus)) return await DraftClosedConflictAsync(tenantId, draftId, draftStatus, cancellationToken);
        var storageUrl = request.StorageUrl?.Trim() ?? string.Empty;
        if (storageUrl.Length == 0)
            return BadRequest(new { message = "A storage key is required. Upload the file before attaching it to a draft." });

        try
        {
            // This metadata-only endpoint remains backwards compatible with the upload-then-attach
            // flow, but only accepts an object the active tenant can actually read.
            _ = await _documents.GetBytesAsync(tenantId, storageUrl, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return BadRequest(new { message = "The referenced document does not exist. Upload the file before attaching it to a draft." });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "The referenced document is not a valid object for the active tenant." });
        }

        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            DraftId = draftId,
            DocumentType = request.DocumentType.Trim(),
            FileName = request.FileName.Trim(),
            ContentType = request.ContentType.Trim(),
            StorageUrl = storageUrl,
            IsRequired = request.IsRequired,
            ExpiryDate = request.ExpiryDate
        };
        // Attaching a document changes the hire: it is saved with the audit row that makes its author
        // one of the hire's makers, under the draft's lock.
        var refusal = await ChangeOpenDraftAsync(tenantId, draftId, EmployeeDraftAuditActions.DocumentAttached, (_, _) =>
        {
            _db.EmployeeDocuments.Add(document);
            return Task.FromResult<IActionResult?>(null);
        }, cancellationToken);
        if (refusal is not null) return (ActionResult)refusal;
        return Created($"/api/employees/documents/{document.Id}", EmployeeDocumentDto.Project(document));
    }

    [HttpPost("drafts/{draftId:guid}/documents/upload")]
    [HasPermission("employees.documents")]
    [RequestSizeLimit(10_485_760)]
    public async Task<ActionResult<EmployeeDocumentDto>> UploadDraftDocument(Guid draftId, [FromForm] EmployeeDocumentUploadRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        if (request.File is null) return BadRequest(new { message = "Document file is required." });
        var draftStatus = await VisibleDrafts(tenantId).Where(x => x.Id == draftId).Select(x => x.Status).SingleOrDefaultAsync(cancellationToken);
        if (draftStatus is null) return NotFound();
        if (!EmployeeDraftStatuses.IsOpen(draftStatus)) return await DraftClosedConflictAsync(tenantId, draftId, draftStatus, cancellationToken);
        var stored = await _documents.SaveAsync(tenantId, request.File, cancellationToken);
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            DraftId = draftId,
            DocumentType = request.DocumentType.Trim(),
            FileName = stored.FileName,
            ContentType = stored.ContentType,
            StorageUrl = stored.StorageUrl,
            IsRequired = request.IsRequired,
            ExpiryDate = request.ExpiryDate
        };
        var refusal = await ChangeOpenDraftAsync(tenantId, draftId, EmployeeDraftAuditActions.DocumentUploaded, (_, _) =>
        {
            _db.EmployeeDocuments.Add(document);
            return Task.FromResult<IActionResult?>(null);
        }, cancellationToken);
        if (refusal is not null) return (ActionResult)refusal;
        await NotifyBestEffortAsync("Document uploaded", $"{request.DocumentType} was uploaded for draft {draftId}.", draftId, cancellationToken);
        return Created($"/api/employees/documents/{document.Id}", EmployeeDocumentDto.Project(document));
    }

    [HttpGet("documents/{documentId:guid}/download")]
    public async Task<IActionResult> DownloadDocument(Guid documentId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        // Loaded tenant-wide, then authorized below by whoever the document belongs to. The company
        // filter cannot decide this: a draft's documents have no company until the hire is activated,
        // so under that filter a company-scoped checker got 404 for a document the draft review had
        // just listed.
        var document = await ScopedBypass.TenantWide(_db.EmployeeDocuments, tenantId,
                "A draft's documents carry no company until activation; access is decided below by employee or draft scope.")
            .FirstOrDefaultAsync(x => x.Id == documentId && !x.IsDeleted, cancellationToken);
        if (document is null) return NotFound();

        if (document.EmployeeId.HasValue)
        {
            // The company rule the filter used to apply, then the caller's data scope for this employee.
            if (!this.GetEntityScope().CanAccessCompany(document.CompanyId)) return NotFound();
            var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
            if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(document.EmployeeId.Value))
                return Forbid();
        }
        else if (document.DraftId.HasValue)
        {
            // A draft's documents follow the draft's visibility: its maker, a checker whose legal
            // entities include the accepted offer's application, or group scope.
            if (!await VisibleDrafts(tenantId).AnyAsync(x => x.Id == document.DraftId.Value, cancellationToken))
                return NotFound();
        }
        else if (!this.GetEntityScope().CanAccessCompany(document.CompanyId))
        {
            return NotFound();
        }

        byte[] contents;
        try
        {
            contents = await _documents.GetBytesAsync(tenantId, document.StorageUrl, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { message = "Stored document file was not found." });
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogWarning(ex, "Rejected invalid storage key for employee document {DocumentId}", documentId);
            return NotFound(new { message = "Stored document file was not found." });
        }

        document.LastDownloadedAtUtc = DateTime.UtcNow;
        document.LastDownloadedBy = GetUserId();
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.document_downloaded", "EmployeeDocument", documentId.ToString(), cancellationToken);

        return File(contents, document.ContentType, document.FileName);
    }

    [HttpPost("drafts/{draftId:guid}/submit")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> SubmitDraft(Guid draftId, CancellationToken cancellationToken)
    {
        // Submission only ever moves an open draft forward. It used to write PendingHrApproval
        // unconditionally, so an Activated draft could be reopened and approved into a second
        // employee. Resubmitting a draft that is already waiting is a no-op, not a new submission.
        var tenantId = RequireTenant();
        var status = await VisibleDrafts(tenantId).AsNoTracking()
            .Where(x => x.Id == draftId).Select(x => x.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status is null) return NotFound();
        if (status == EmployeeDraftStatuses.PendingHrApproval) return NoContent();
        if (!EmployeeDraftStatuses.IsOpen(status)) return await DraftClosedConflictAsync(tenantId, draftId, status, cancellationToken);

        var submittedAtUtc = DateTime.UtcNow;
        var moved = await TransitionDraftAsync(tenantId, draftId, EmployeeDraftStatuses.Submittable,
            s => s.SetProperty(d => d.Status, EmployeeDraftStatuses.PendingHrApproval)
                  .SetProperty(d => d.CurrentStep, "HrApproval")
                  .SetProperty(d => d.SubmittedAtUtc, d => d.SubmittedAtUtc ?? submittedAtUtc),
            d =>
            {
                d.Status = EmployeeDraftStatuses.PendingHrApproval;
                d.CurrentStep = "HrApproval";
                d.SubmittedAtUtc ??= submittedAtUtc;
            },
            EmployeeDraftAuditActions.Submitted, null, cancellationToken);
        if (!moved)
        {
            // Lost a race: another request moved the draft first. A concurrent submission is the
            // same outcome; anything else (approved, rejected, withdrawn) is a closed draft.
            var now = await CurrentDraftStatusAsync(tenantId, draftId, cancellationToken);
            return now == EmployeeDraftStatuses.PendingHrApproval
                ? NoContent()
                : await DraftClosedConflictAsync(tenantId, draftId, now, cancellationToken);
        }

        await NotifyBestEffortAsync("Employee draft submitted", "A draft is waiting for HR approval.", draftId, cancellationToken);
        return NoContent();
    }

    [HttpPost("drafts/{draftId:guid}/approve")]
    [HasPermission("employees.approve")]
    public async Task<ActionResult<EmployeeDetailDto>> ApproveDraft(Guid draftId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var actorId = GetUserId();
        var entityScope = this.GetEntityScope();
        var requestContext = Context();
        var preflight = await VisibleDrafts(tenantId).AsNoTracking()
            .Where(x => x.Id == draftId)
            .Select(x => new { x.CreatedByUserId, x.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (preflight is null) return NotFound();
        if (await MakerCheckerRefusalAsync(tenantId, draftId, cancellationToken) is { } refusal)
            return refusal;
        if (!EmployeeDraftStatuses.IsOpen(preflight.Status))
            return await DraftClosedConflictAsync(tenantId, draftId, preflight.Status, cancellationToken);

        // The marker identity and timestamp are allocated outside the retry delegate. If COMMIT is
        // durable but its acknowledgement is lost, the execution strategy can prove this exact
        // approval and the endpoint reconstructs the result instead of replaying or reporting a
        // false failure.
        var auditId = Guid.NewGuid();
        var approvedAtUtc = DateTime.UtcNow;
        var unreachablePasswordHash = _passwordHasher.Hash(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));

        async Task<bool> ApproveOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();

            // Tenant is the serialization anchor for employee-code allocation and normalized-email
            // identity creation. The draft lock makes competing approval requests exactly-once.
            // IgnoreQueryFilters is intentional: Tenant is not company-scoped; the lock is pinned to
            // the caller's own tenantId (x.Id == tenantId), so no cross-tenant row is reachable.
            var tenantAnchor = await _db.Tenants.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.Id == tenantId && x.IsActive)
                .Select(x => x.Id)
                .SingleOrDefaultAsync(ct);
            if (tenantAnchor == Guid.Empty)
                throw new DraftApprovalNotFoundException();

            var draft = await _db.EmployeeDrafts.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == draftId && x.TenantId == tenantId, ct)
                ?? throw new DraftApprovalNotFoundException();
            // Re-checked under the lock, because the preflight read is advisory: an edit committed in
            // between makes its editor a maker. No maker of the hire ever activates it.
            if (actorId is null || (await _draftHireMakers.MakersAsync(tenantId, draftId, ct)).Contains(actorId.Value))
                throw new DraftApprovalMakerCheckerException();
            if (!entityScope.IsGroupLevel)
            {
                // A company-scoped checker decides only accepted-offer drafts whose application sits in
                // one of their legal entities. A manual draft names no entity until it is resolved
                // here, so it needs group scope (the resolved entity is checked again below).
                var originCompanyId = await ScopedBypass.TenantWide(_db.JobApplications, tenantId,
                        "The application's own company is the scope being checked, so the company filter must not pre-empt it.")
                    .AsNoTracking()
                    .Where(a => a.OnboardingDraftId == draftId)
                    .Select(a => a.CompanyId)
                    .FirstOrDefaultAsync(ct);
                if (!entityScope.CanAccessCompany(originCompanyId))
                    throw new DraftApprovalForbiddenException();
            }
            if (!EmployeeDraftStatuses.IsOpen(draft.Status))
                throw new DraftApprovalNotReadyException(draft.Status);

            // Resolve every mutable draft field again after taking the draft lock. A preflight read
            // is authorization/UX only and is never trusted for the durable employee record. The
            // review screen's activation check runs this same resolution, so it cannot disagree.
            var placement = await ResolveDraftPlacementAsync(tenantId, draft, problems: null, ct);
            if (!entityScope.IsGroupLevel && !entityScope.CanAccessCompany(placement.CompanyId))
                throw new DraftApprovalForbiddenException();
            // The draft's manager is checked again here, under the lock and against the approver's own
            // scope: a draft saved before this rule, or by another path, may name anyone.
            if (await DraftManagerRejectionAsync(tenantId, draft.ManagerEmployeeId, ct) is { } managerRejection)
            {
                if (managerRejection.OutOfScope) throw new DraftApprovalForbiddenException();
                throw new DraftApprovalValidationException(
                    managerRejection.Message + " Change the draft's manager, then approve it.");
            }

            var employee = EmployeeFromDraft(draft, tenantId, placement, approvedAtUtc);
            employee.EmployeeCode = await GenerateEmployeeCode(tenantId, ct);
            employee.ActivatedAtUtc = approvedAtUtc;

            if (employee.ManagerEmployeeId is null && employee.DepartmentId.HasValue)
            {
                // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
                var deptHeadId = await _db.Departments.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.Id == employee.DepartmentId.Value)
                    .Select(d => d.ManagerEmployeeId).SingleOrDefaultAsync(ct);
                if (deptHeadId is { } headId && headId != 0)
                {
                    employee.ManagerEmployeeId = headId;
                    employee.SecondLevelManagerEmployeeId = await _db.Employees.IgnoreQueryFilters().AsNoTracking()
                        .Where(e => e.TenantId == tenantId && e.Id == headId && !e.IsDeleted)
                        .Select(e => e.ManagerEmployeeId).FirstOrDefaultAsync(ct);
                }
            }

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var draftDocuments = await _db.EmployeeDocuments.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.DraftId == draftId && !x.IsDeleted)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            var gateDocs = draftDocuments
                .Select(x => new DocumentPresence(x.DocumentType,
                    string.Equals(x.ApprovalStatus, "Verified", StringComparison.OrdinalIgnoreCase), x.ExpiryDate))
                .ToList();
            var draftSnapshot = EmployeeReadinessEvaluator.BuildFromEmployee(
                employee, null, gateDocs, new Dictionary<string, DateOnly?>(), (employee.Salary ?? 0m) > 0m);
            var draftReadiness = await _activationGuard.EnsureActivatableAsync(
                tenantId, employee.CompanyId, draftSnapshot, requestContext, ct);
            employee.ReadinessState = draftReadiness.State;
            employee.ActivationBlockersCount = draftReadiness.Blocking.Count;
            employee.ReadinessEvaluatedAtUtc = approvedAtUtc;

            // The caller-owned transaction is deliberately outside EstablishmentGuard. The guard
            // joins it on lockable paths; Off/unclassified paths are now atomic too.
            await _establishmentGuard.EnforceAndExecuteAsync(
                tenantId, employee.DepartmentId, employee.DesignationId,
                excludeEmployeeId: null, path: "draft_approve", requestContext, async () =>
                {
                    _db.Employees.Add(employee);
                    await _db.SaveChangesAsync(ct); // allocate the internal employee key inside tx

                    foreach (var document in draftDocuments)
                    {
                        document.EmployeeId = employee.Id;
                        document.CompanyId = employee.CompanyId;
                    }

                    employee.UserAccountId = await CreateEmployeeUserAccount(
                        employee, unreachablePasswordHash, approvedAtUtc, ct);
                    await _db.LinkOnboardingTasksForActivatedDraftAsync(
                        tenantId, draftId, employee, ct);

                    draft.Status = EmployeeDraftStatuses.Activated;
                    draft.CurrentStep = EmployeeDraftStatuses.Activated;
                    draft.ApprovedAtUtc = approvedAtUtc;
                    draft.ActivatedAtUtc = approvedAtUtc;
                    await AddHistory(employee, "Activated", DateOnly.FromDateTime(employee.JoiningDate), ct);

                    // The draft-side record of the same event: which employee this draft became and
                    // who approved it. The review list and the "already activated" refusal read it by
                    // (EntityName, EntityId), which the audit index covers; the marker below is keyed
                    // by the employee and only names the draft inside its JSON.
                    var draftActivated = AuthAuditEntry.Create(
                        Guid.NewGuid(),
                        approvedAtUtc,
                        EmployeeDraftAuditActions.Activated,
                        "EmployeeDraft",
                        draftId.ToString(),
                        requestContext with { TenantId = tenantId },
                        JsonSerializer.Serialize(new { employeeId = employee.Id, employeeCode = employee.EmployeeCode, employeePublicId = employee.PublicId }));
                    draftActivated.CompanyId = employee.CompanyId;
                    _db.AuditLogs.Add(draftActivated);

                    var marker = AuthAuditEntry.Create(
                        auditId,
                        approvedAtUtc,
                        "employee.activated",
                        "Employee",
                        employee.Id.ToString(),
                        requestContext with { TenantId = tenantId },
                        JsonSerializer.Serialize(new { draftId, employeePublicId = employee.PublicId }));
                    marker.CompanyId = employee.CompanyId;
                    _db.AuditLogs.Add(marker);
                    await _db.SaveChangesAsync(ct);
                    return true;
                }, ct);
            return true;
        }

        try
        {
            if (_db.Database.IsRelational())
            {
                var strategy = _db.Database.CreateExecutionStrategy();
                await strategy.ExecuteInTransactionAsync(
                    ApproveOnceAsync,
                    // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
                    async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                        .AnyAsync(x => x.Id == auditId
                            && x.TenantId == tenantId
                            && x.Action == "employee.activated", ct),
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            }
            else
            {
                await ApproveOnceAsync(cancellationToken);
            }
        }
        catch (EstablishmentBudgetExceededException ex)
        {
            _db.ChangeTracker.Clear();
            return this.EstablishmentConflict(ex);
        }
        catch (EmployeeActivationBlockedException ex)
        {
            _db.ChangeTracker.Clear();
            await Audit("employee.activation_blocked", "EmployeeDraft", draftId.ToString(), cancellationToken);
            return this.NotActivatable(ex);
        }
        catch (IdentityProvisioningConflictException ex)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { message = ex.Message });
        }
        catch (DraftApprovalValidationException ex)
        {
            _db.ChangeTracker.Clear();
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (DraftApprovalForbiddenException)
        {
            _db.ChangeTracker.Clear();
            return Forbid();
        }
        catch (DraftApprovalMakerCheckerException)
        {
            _db.ChangeTracker.Clear();
            return MakerCheckerForbidden();
        }
        catch (DraftApprovalNotReadyException ex)
        {
            // Lost the race to another decision, or the draft closed after the preflight read.
            _db.ChangeTracker.Clear();
            return await DraftClosedConflictAsync(tenantId, draftId, ex.Status, cancellationToken);
        }
        catch (DraftApprovalNotFoundException)
        {
            _db.ChangeTracker.Clear();
            return NotFound();
        }

        // Read the durable marker even on the normal path. This is both the unknown-COMMIT
        // reconstruction path and a final assertion that no un-audited activation is returned.
        _db.ChangeTracker.Clear();
        // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
        var committedMarker = await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == auditId
                && x.TenantId == tenantId
                && x.Action == "employee.activated", cancellationToken)
            ?? throw new InvalidOperationException("Draft approval did not produce its durable completion marker.");
        if (!int.TryParse(committedMarker.EntityId, out var employeeId))
            throw new InvalidOperationException("Draft approval completion marker is invalid.");
        var committedEmployee = await _db.Employees.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, cancellationToken);
        var documents = await _db.EmployeeDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        var histories = await _db.EmployeeHistories.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

        // Delivery is intentionally post-commit and best-effort. A notification outage must not
        // turn a durably completed approval into a 500 that tempts the caller to replay it.
        try
        {
            await Notify("Employee activated",
                $"{committedEmployee.FullName} was activated with ID {committedEmployee.EmployeeCode}.",
                "Employee", committedEmployee.Id.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Employee {EmployeeId} activated from draft {DraftId}, but post-commit notification failed.",
                committedEmployee.Id, draftId);
        }

        return Ok(EmployeeDetailDto.Project(
            committedEmployee, CanViewSensitive(), documents: documents, history: histories));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer")]
    public async Task<IActionResult> UpdateEmployee(int id, EmployeeUpdateRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);
        if (employee is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        if (!scope.CanAccessEmployee(employee.Id)) return Forbid();
        // FAIL LOUD ON AN UNKNOWN KEY — before a single column is touched, so a rejected patch is never
        // half-applied. ApplyChanges had no default arm, so a key it did not recognise was accepted with 200
        // and discarded; the user saw a successful save and the value was gone. Ordinal match, because the
        // switch is ordinal: "BankIban" is NOT "bankIban" and must be rejected rather than dropped.
        var unknownFields = request.Changes.Keys.Where(k => !EditableEmployeeFields.Contains(k)).ToList();
        if (unknownFields.Count > 0)
            return BadRequest(new
            {
                message = $"Unrecognised employee field(s): {string.Join(", ", unknownFields)}. "
                          + "No change was applied. Field keys are case-sensitive.",
                unknownFields,
            });
        // A manager id is checked for tenant, data scope and reporting cycles BEFORE any column is touched —
        // the same three rules PUT {id}/manager enforces (see EmployeeChangeApplier.ValidateManagerChangeAsync).
        if (await EmployeeChangeApplier.ValidateManagerChangeAsync(
                _db, employee, request.Changes, scope.CanAccessEmployee, cancellationToken) is { } managerRejection)
        {
            if (managerRejection.OutOfScope) return Forbid();
            return UnprocessableEntity(new { error = "invalid_manager", message = managerRejection.Message + " No change was applied." });
        }
        var sensitive = request.Changes.Keys.Where(SensitiveFields.Contains).ToList();
        // F02 — refused up front, not at approval: ReadDateOnly turns an unparseable value into NULL, so a
        // bad string would be approved as "a date" and then silently clear the person's GOSI cohort.
        if (request.Changes.TryGetValue("gosiFirstRegisteredOn", out var gosiFirstRegisteredOn)
            && GosiFirstRegisteredOnError(gosiFirstRegisteredOn) is { } gosiDateError)
            return UnprocessableEntity(new { error = "invalid_gosi_first_registered_on", message = gosiDateError + " No change was applied." });
        // Establishment integrity: the free-text department/designation/branch cases in
        // ApplyChanges are resolved to IDs (shared resolver — unresolvable name ⇒ 422) and any
        // resulting (department, designation) pair change routes through the guard on BOTH
        // persistence branches below (department/designation are not SensitiveFields, so they ride
        // the immediate branch even when mixed with sensitive fields).
        var priorDeptId = employee.DepartmentId;
        var priorDesigId = employee.DesignationId;
        // Before ApplyChanges overwrites it — needed for the work-email login-identity rename guard.
        var priorWorkEmail = employee.WorkEmail;
        try
        {
            if (sensitive.Count > 0)
            {
                if (!CanEditSensitive()) return Forbid();
                var sensitiveChanges = request.Changes
                    .Where(x => SensitiveFields.Contains(x.Key))
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                var immediateChanges = request.Changes
                    .Where(x => !SensitiveFields.Contains(x.Key))
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

                // A sensitive value does not change until it is approved, so the form (and the readiness
                // fast-fix) keeps offering it, and every save used to raise ANOTHER identical approval —
                // thirteen of them for one admin's own record in production. Resubmitting exactly what is
                // already waiting now returns the waiting request instead of queueing a copy.
                if (immediateChanges.Count == 0
                    && await FindIdenticalPendingChangeAsync(tenantId, employee.Id, sensitiveChanges, cancellationToken) is { } waiting)
                {
                    return Accepted(new
                    {
                        changeRequestId = waiting.Id,
                        approvalRequestId = waiting.ApprovalRequestId,
                        requiresApproval = true,
                        alreadyPending = true,
                        sensitiveFields = sensitive,
                        appliedFields = new List<string>()
                    });
                }

                if (immediateChanges.Count > 0)
                {
                    ApplyChanges(employee, immediateChanges);
                    // Keys stored on the payroll profile (socialInsuranceReference) have no home on
                    // Employee — written here, in the same unit of work as the columns above.
                    await EmployeeChangeApplier.ApplyPayrollProfileAsync(_db, employee, immediateChanges, GetUserId(), cancellationToken);
                    await EmployeeOrgFieldResolver.ResolveAppliedChangesAsync(_db, tenantId, employee, immediateChanges.Keys, cancellationToken);
                    await ApplyWorkEmailPatchAsync(employee, immediateChanges.Keys, priorWorkEmail, cancellationToken);
                    employee.UpdatedAtUtc = DateTime.UtcNow;
                    await AddHistory(employee, "Updated", request.EffectiveDate, cancellationToken);
                }

                var change = new EmployeeChangeRequest
                {
                    TenantId = tenantId,
                    EmployeeId = employee.Id,
                    RequestedByUserId = GetUserId(),
                    EffectiveDate = request.EffectiveDate,
                    SensitiveFields = string.Join(',', sensitive),
                    ProposedChangesJson = JsonSerializer.Serialize(sensitiveChanges)
                };
                _db.EmployeeChangeRequests.Add(change);
                var workflow = await EnsureEmployeeChangeWorkflowAsync(tenantId, cancellationToken);
                var pairChanged = employee.DepartmentId != priorDeptId || employee.DesignationId != priorDesigId;
                if (pairChanged)
                {
                    await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
                        excludeEmployeeId: employee.Id, path: "update", Context(), async () =>
                        {
                            await _db.SaveChangesAsync(cancellationToken);
                            return true;
                        }, cancellationToken);
                }
                else
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                var approval = await _approvalWorkflow.CreateRequestAsync(
                    tenantId,
                    new CreateApprovalRequest(
                        workflow.Id,
                        nameof(EmployeeChangeRequest),
                        change.Id.ToString(),
                        $"Employee change approval - {employee.EmployeeCode} {employee.FullName}",
                        employee.Id,
                        employee.CompanyId,
                        "High"),
                    Context(),
                    cancellationToken);
                change.ApprovalRequestId = approval.Id;
                await _db.SaveChangesAsync(cancellationToken);
                await Notify("Sensitive employee change requires approval", $"Fields requiring approval: {change.SensitiveFields}. Routed to {approval.CurrentQueue}. Due {approval.DueAtUtc:yyyy-MM-dd HH:mm} UTC.", "ApprovalRequest", approval.Id.ToString(), cancellationToken);
                await Audit("employee.change_requested", "EmployeeChangeRequest", change.Id.ToString(), cancellationToken);
                return Accepted(new
                {
                    changeRequestId = change.Id,
                    approvalRequestId = approval.Id,
                    requiresApproval = true,
                    sensitiveFields = sensitive,
                    appliedFields = immediateChanges.Keys.ToList()
                });
            }

            ApplyChanges(employee, request.Changes);
            await EmployeeChangeApplier.ApplyPayrollProfileAsync(_db, employee, request.Changes, GetUserId(), cancellationToken);
            await EmployeeOrgFieldResolver.ResolveAppliedChangesAsync(_db, tenantId, employee, request.Changes.Keys, cancellationToken);
            await ApplyWorkEmailPatchAsync(employee, request.Changes.Keys, priorWorkEmail, cancellationToken);
            employee.UpdatedAtUtc = DateTime.UtcNow;
            await AddHistory(employee, "Updated", request.EffectiveDate, cancellationToken);
            if (employee.DepartmentId != priorDeptId || employee.DesignationId != priorDesigId)
            {
                await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
                    excludeEmployeeId: employee.Id, path: "update", Context(), async () =>
                    {
                        await _db.SaveChangesAsync(cancellationToken);
                        return true;
                    }, cancellationToken);
            }
            else
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            await Audit("employee.updated", "Employee", employee.Id.ToString(), cancellationToken);
            return Ok(EmployeeDetailDto.Project(employee, CanViewSensitive()));
        }
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
        catch (WorkEmailConflictException ex) { return Conflict(new { error = "work_email_conflict", attempted = ex.Attempted, suggestion = ex.Suggestion }); }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new { message = ex.Message }); }
    }

    /// <summary>F02 — a GOSI first-registration date is null (clear it back to Unknown) or an ISO calendar
    /// date that has already happened. Returns the reason it is not, or null when it is acceptable.</summary>
    internal static string? GosiFirstRegisteredOnError(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            return "gosiFirstRegisteredOn must be a date in YYYY-MM-DD form, or null to clear it.";
        if (date > DateOnly.FromDateTime(DateTime.UtcNow))
            return "gosiFirstRegisteredOn cannot be in the future: it is the date GOSI first registered this person.";
        return null;
    }

    [HttpPatch("{id:int}/status")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<EmployeeDetailDto>> ChangeStatus(int id, EmployeeStatusChangeRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            // D1 privilege boundary: SeparationType decides the end-of-service award (Article80 forfeits
            // it entirely), and this endpoint is only employees.write, whereas /terminate — the canonical
            // separation command — is employees.approve. Accepting it here would let a write-level user
            // mint a gratuity-determining fact. It is dropped rather than rejected so ordinary status
            // changes keep working unchanged; a caller who needs to state it uses /terminate.
            var employee = await employeeManagement.ChangeStatusAsync(
                RequireTenant(), id, request with { SeparationType = null }, Context(), cancellationToken, CanViewSensitive());
            return employee is null ? NotFound() : Ok(employee);
        }
        // Readiness block MUST be caught before InvalidOperationException (which would swallow the
        // structured body into a generic 400) — EmployeeActivationBlockedException is standalone (§5.6).
        catch (EmployeeActivationBlockedException ex) { await Audit("employee.activation_blocked", "Employee", id.ToString(), cancellationToken); return this.NotActivatable(ex); }
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        // PATCH /status reaches the identical separation insert whenever the target is an exit status, so
        // two concurrent calls hit the same partial unique index. Without this it surfaced as a 500 — the
        // defect the /terminate catch was added for, on the other door into the same code.
        catch (DbUpdateException) { return Conflict(SeparationConflictBody); }
    }

    [HttpPost("{id:int}/documents")]
    [HasPermission("employees.documents")]
    [RequestSizeLimit(10_485_760)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<EmployeeDocumentDto>> UploadEmployeeDocument(int id, [FromForm] EmployeeDocumentUploadForm form, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            var request = new EmployeeDocumentUploadMetadata(
                form.DocumentType,
                form.DocumentCategory,
                form.IssueDate,
                form.ExpiryDate,
                form.RenewalReminderDate,
                form.IsRequired,
                form.ApprovalStatus,
                form.Notes);
            var document = await employeeManagement.UploadDocumentAsync(RequireTenant(), id, request, form.File, Context(), cancellationToken);
            return Created($"/api/employees/{id}/documents/{document.Id}", EmployeeDocumentDto.Project(document));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("{id:int}/documents")]
    public async Task<ActionResult<IReadOnlyCollection<EmployeeDocumentDto>>> EmployeeDocuments(int id, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), cancellationToken);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(id))
            return Forbid();
        var docs = await employeeManagement.GetDocumentsAsync(RequireTenant(), id, cancellationToken);
        return Ok(docs.Select(EmployeeDocumentDto.Project).ToList());
    }

    [HttpPut("{id:int}/documents/{docId:guid}")]
    [HasPermission("employees.documents")]
    public async Task<ActionResult<EmployeeDocumentDto>> UpdateEmployeeDocument(int id, Guid docId, [FromBody] UpdateDocumentMetadataRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var doc = await employeeManagement.UpdateDocumentAsync(RequireTenant(), id, docId, request, Context(), cancellationToken);
        return doc is null ? NotFound() : Ok(EmployeeDocumentDto.Project(doc));
    }

    [HttpPost("{id:int}/documents/{docId:guid}/verify")]
    [HasPermission("employees.documents")]
    public async Task<ActionResult<EmployeeDocumentDto>> VerifyEmployeeDocument(int id, Guid docId, [FromBody] DocumentVerifyRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var doc = await employeeManagement.VerifyDocumentAsync(RequireTenant(), id, docId, request.Notes, Context(), cancellationToken);
        return doc is null ? NotFound() : Ok(EmployeeDocumentDto.Project(doc));
    }

    [HttpPost("{id:int}/documents/{docId:guid}/reject")]
    [HasPermission("employees.documents")]
    public async Task<ActionResult<EmployeeDocumentDto>> RejectEmployeeDocument(int id, Guid docId, [FromBody] DocumentRejectRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var doc = await employeeManagement.RejectDocumentAsync(RequireTenant(), id, docId, request.Reason, Context(), cancellationToken);
        return doc is null ? NotFound() : Ok(EmployeeDocumentDto.Project(doc));
    }

    [HttpDelete("{id:int}/documents/{docId:guid}")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> ArchiveEmployeeDocument(int id, Guid docId, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        return await employeeManagement.ArchiveDocumentAsync(RequireTenant(), id, docId, Context(), cancellationToken) ? NoContent() : NotFound();
    }

    [HttpGet("{id:int}/history")]
    public async Task<ActionResult<IReadOnlyCollection<EmployeeHistoryDto>>> EmployeeHistory(int id, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(id))
            return Forbid();
        var history = await employeeManagement.GetHistoryAsync(tenantId, id, cancellationToken);
        return Ok(history.Select(EmployeeHistoryDto.Project).ToList());
    }

    [HttpPost("{id:int}/activate")]
    [HasPermission("employees.approve")]
    public async Task<ActionResult<EmployeeDetailDto>> Activate(int id, EmployeeStatusChangeRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            var employee = await employeeManagement.ActivateAsync(RequireTenant(), id, request, Context(), cancellationToken, CanViewSensitive());
            return employee is null ? NotFound() : Ok(employee);
        }
        catch (EmployeeActivationBlockedException ex) { await Audit("employee.activation_blocked", "Employee", id.ToString(), cancellationToken); return this.NotActivatable(ex); }
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{id:int}/terminate")]
    [HasPermission("employees.approve")]
    public async Task<ActionResult<EmployeeDetailDto>> Terminate(int id, EmployeeStatusChangeRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            var employee = await employeeManagement.TerminateAsync(RequireTenant(), id, request, Context(), cancellationToken, CanViewSensitive());
            return employee is null ? NotFound() : Ok(employee);
        }
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
        // The closed separation-type vocabulary and the joining-date bound both refuse deliberately.
        // Without this they left the service as 500 internal_error, which reads as a product fault
        // rather than a rejected command.
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        // D1: two concurrent terminates both read "no live separation" and both insert; the partial
        // unique index refuses the loser. That is the constraint doing its job, and the winner's
        // termination did happen — so this is a 409 on an already-open separation, not a 500.
        catch (DbUpdateException) { return Conflict(SeparationConflictBody); }
    }

    private static object SeparationConflictBody => new
    {
        error = "separation_already_open",
        message = "A separation for this employee was opened concurrently by another request. "
                + "That termination succeeded; re-read the employee rather than retrying.",
    };

    /// <summary>Live readiness for one employee (§8.3): the itemized activation checklist + policy
    /// provenance + disclaimer. Server-computed — the single source of truth for the badge, the
    /// checklist drawer, and the inline 422 rendering.</summary>
    [HttpGet("{id:int}/readiness")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Manager,Auditor")]
    public async Task<IActionResult> Readiness(int id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        if (!await CanAccessEmployeeAsync(id, cancellationToken)) return Forbid();
        var evaluation = await _activationGuard.EvaluateEmployeeAsync(tenantId, id, cancellationToken);
        if (evaluation is null) return NotFound();
        var (readiness, policy) = evaluation.Value;
        return Ok(new
        {
            employeeId = id,
            state = readiness.State,
            score = readiness.Score,
            progress = new { present = readiness.Present.Count, requiredTotal = readiness.RequiredTotal },
            policy = new { countryCode = policy.CountryCode, tier = policy.Tier, sources = policy.Sources },
            blocking = readiness.Blocking.Select(ReadinessItemDto),
            payBlocking = readiness.PayBlocking.Select(ReadinessItemDto),
            recommended = readiness.Recommended.Select(ReadinessItemDto),
            present = readiness.Present.Select(ReadinessItemDto),
            expiringSoon = readiness.ExpiringSoon.Select(ReadinessItemDto),
            disclaimer = policy.Disclaimer,
        });
    }

    /// <summary>Multi-select bulk activation (§5.3) for the "Needs info" worklist: each employee passes
    /// the SAME guard; returns per-employee outcomes so a mixed batch never fails as a whole.
    /// SUPERSEDED by <see cref="BulkAction"/> (POST /bulk) — kept for back-compat. Scope is resolved
    /// ONCE (was an N+1) and the change-tracker is reset per row so a guard-rejected row can never leak
    /// its rolled-back mutation into the next row's SaveChanges.</summary>
    [HttpPost("bulk-activate")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> BulkActivate([FromBody] BulkActivateRequest req, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var activated = new List<int>();
        var blocked = new List<object>();
        foreach (var id in (req.EmployeeIds ?? System.Array.Empty<int>()).Distinct())
        {
            _db.ChangeTracker.Clear();
            if (!scope.CanAccessEmployee(id)) { blocked.Add(new { id, error = "forbidden" }); continue; }
            try
            {
                var dto = await employeeManagement.ActivateAsync(tenantId, id,
                    new EmployeeStatusChangeRequest("Active", DateOnly.FromDateTime(DateTime.UtcNow.Date), req.Reason ?? "Bulk activation"),
                    Context(), cancellationToken);
                if (dto is null) blocked.Add(new { id, error = "not_found" });
                else activated.Add(id);
            }
            catch (EmployeeActivationBlockedException ex)
            {
                _db.ChangeTracker.Clear();
                await Audit("employee.activation_blocked", "Employee", id.ToString(), cancellationToken);
                blocked.Add(new { id, blocking = ex.Readiness.Blocking.Select(ReadinessItemDto) });
            }
            catch (EstablishmentBudgetExceededException) { _db.ChangeTracker.Clear(); blocked.Add(new { id, error = "establishment_budget_exceeded" }); }
            catch (InvalidOperationException ex) { _db.ChangeTracker.Clear(); blocked.Add(new { id, error = ex.Message }); }
        }
        _db.ChangeTracker.Clear();
        return Ok(new { activated, blocked });
    }

    public record BulkActivateRequest(int[] EmployeeIds, string? Reason);

    // ── Unified bulk-action endpoint (multi-select on the People list) ─────────────────────────────
    // ONE authoritative entrypoint for the four list-level bulk actions. Per-action permission is
    // checked in-code (four different permissions behind one route); the target id set is resolved
    // SERVER-SIDE from either an explicit id list or a filter predicate — a client can never widen
    // beyond its tenant + data-scope + company boundary, and "select all matching" always means the
    // whole filtered set across pages, never just the visible page. Each row is processed
    // independently (no outer transaction) so one failure never rolls back the others, and every row
    // resets the change-tracker so a guard-rejected mutation can never be flushed by a later row.
    private const int BulkActionMaxIds = 5000;
    private static readonly HashSet<string> BulkDeactivateTargets = new(StringComparer.OrdinalIgnoreCase) { "Suspended", "Inactive" };

    public sealed record BulkSelectAllFilter(string? Search, string? Status, string? Readiness, Guid? ImportBatchId, string? GapType);
    public sealed record BulkActionRequest(
        string? Action,
        string? SelectionMode,          // "ids" | "allMatching" — EXPLICIT; never inferred from field-absence
        int[]? EmployeeIds,
        BulkSelectAllFilter? Filter,
        string? Reason,
        string? TargetStatus,
        int? ExpectedCount);            // required for destructive allMatching — server reconciles vs what the user saw
    public sealed record BulkItemOutcome(
        int EmployeeId, string EmployeeCode, string FullName,
        string Outcome,                 // "succeeded" | "skipped" | "failed"
        string? Reason,                 // machine code: already_active, incomplete, forbidden, establishment_budget_exceeded, ...
        IReadOnlyList<object>? Blocking);// ReadinessItemDto[] for floor-skips (drives the checklist UI)
    public sealed record BulkActionResult(
        string Action, int Requested, int Succeeded, int Skipped, int Failed,
        IReadOnlyList<BulkItemOutcome> Items, string Summary);

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkAction([FromBody] BulkActionRequest req, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var action = (req.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("activate" or "deactivate" or "delete" or "export"))
            return BadRequest(new { error = "unknown_action", message = $"Unknown bulk action '{req.Action}'." });

        // Per-action permission — checked BEFORE any row work (403 on missing).
        bool permitted = action switch
        {
            "activate" => User.HasPermission("employees.approve"),
            "deactivate" => User.HasPermission("employees.write"),
            "delete" => User.HasPermission("employees.delete"),
            // The same audience as the full people export (GET export): employees.write.
            "export" => User.HasPermission("employees.write"),
            _ => false,
        };
        if (!permitted) return Forbid();

        // Selection mode is an EXPLICIT discriminator — never inferred from which field is populated,
        // so a stale/empty predicate can never silently resolve to "the whole active tenant".
        var mode = (req.SelectionMode ?? string.Empty).Trim().ToLowerInvariant();
        bool idsMode = mode == "ids";
        bool allMatchingMode = mode == "allmatching";
        if (!idsMode && !allMatchingMode)
            return BadRequest(new { error = "invalid_selection", message = "selectionMode must be 'ids' or 'allMatching'." });
        var hasIds = req.EmployeeIds is { Length: > 0 };
        if (idsMode && !hasIds)
            return BadRequest(new { error = "invalid_selection", message = "employeeIds is required for selectionMode 'ids'." });
        if (idsMode && req.Filter is not null)
            return BadRequest(new { error = "invalid_selection", message = "Provide employeeIds OR filter, not both." });
        if (allMatchingMode && hasIds)
            return BadRequest(new { error = "invalid_selection", message = "Provide employeeIds OR filter, not both." });

        var reason = req.Reason?.Trim();
        if ((action == "deactivate" || action == "delete") && string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required for this action." });

        var targetStatus = string.IsNullOrWhiteSpace(req.TargetStatus) ? "Suspended" : req.TargetStatus.Trim();
        if (action == "deactivate" && !BulkDeactivateTargets.Contains(targetStatus))
            return BadRequest(new { error = "invalid_target_status", message = "Bulk deactivate target must be Suspended or Inactive (bulk deactivate is never a terminate)." });

        if (idsMode && req.EmployeeIds!.Distinct().Count() > BulkActionMaxIds)
            return BadRequest(new { error = "too_many", message = $"Select at most {BulkActionMaxIds} employees per bulk action." });

        // Resolve scope ONCE for the whole call.
        var entityScope = this.GetEntityScope();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);

        // Authoritative, server-side id resolution — the same active-population query for BOTH modes,
        // constrained by data-scope + company boundary. Nothing outside this set is ever acted on.
        var targetIds = await ResolveTargetIdsAsync(req, idsMode, tenantId, scope, entityScope, cancellationToken);
        if (targetIds.Count > BulkActionMaxIds)
            return BadRequest(new { error = "too_many", message = $"This filter matches {targetIds.Count} employees — narrow it to at most {BulkActionMaxIds} before a bulk action." });

        // Destructive select-all-matching must reconcile against what the user saw (TOCTOU / filter drift):
        // a silent whole-tenant hit becomes a caught 409 instead.
        if (allMatchingMode && (action == "delete" || action == "deactivate"))
        {
            if (req.ExpectedCount is null)
                return BadRequest(new { error = "expected_count_required", message = "expectedCount is required for a destructive select-all-matching action." });
            if (req.ExpectedCount.Value != targetIds.Count)
                return Conflict(new { error = "selection_changed", expected = req.ExpectedCount.Value, actual = targetIds.Count, message = "The set of matching employees changed since you selected them — review and try again." });
        }

        var items = new List<BulkItemOutcome>();

        // id-set mode: any requested id that did NOT survive the server-side population/scope filter is
        // reported as skipped:forbidden — never silently dropped, never acted on (closes the IDOR/BOLA hole).
        if (idsMode)
        {
            var resolvedSet = targetIds.ToHashSet();
            foreach (var dropped in req.EmployeeIds!.Distinct().Where(x => !resolvedSet.Contains(x)))
                items.Add(new BulkItemOutcome(dropped, string.Empty, string.Empty, "skipped", "forbidden", null));
        }

        if (action == "export")
        {
            var emps = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && targetIds.Contains(e.Id))
                .OrderBy(e => e.EmployeeCode).ToListAsync(cancellationToken);
            var includesSensitive = User.HasPermission("employees.sensitive");
            var csv = await BuildEmployeesCsvAsync(emps, tenantId, includesSensitive, cancellationToken);
            await _audit.WriteAsync("employees.exported", "Employee", "bulk", Context(), JsonSerializer.Serialize(new
            {
                rowCount = emps.Count,
                mode = "selected",
                selectionType = idsMode ? "idset" : "allMatching",
                groupScope = entityScope.IsGroupLevel,
                companyIds = entityScope.IsGroupLevel ? null : entityScope.AccessibleCompanyIds,
                includesSensitive,
            }), cancellationToken);
            return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"employees_selected_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        int succeeded = 0, failed = 0;
        int skipped = items.Count; // forbidden drops already counted
        var incompleteLabels = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        foreach (var id in targetIds)
        {
            // Reset per row: a prior row's guard-rejected (rolled-back-but-still-tracked) mutation must
            // never be flushed by this row's SaveChanges. A tx rollback does NOT clear the ChangeTracker.
            _db.ChangeTracker.Clear();
            var snap = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == id)
                .Select(e => new { e.EmployeeCode, e.FullName, e.Status })
                .FirstOrDefaultAsync(cancellationToken);
            if (snap is null) { items.Add(new(id, string.Empty, string.Empty, "failed", "not_found", null)); failed++; continue; }
            var code = snap.EmployeeCode; var name = snap.FullName;
            try
            {
                switch (action)
                {
                    case "activate":
                        if (string.Equals(snap.Status, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase))
                        { items.Add(new(id, code, name, "skipped", "already_active", null)); skipped++; break; }
                        await employeeManagement.ActivateAsync(tenantId, id,
                            new EmployeeStatusChangeRequest("Active", today, reason ?? "Bulk activation"), Context(), cancellationToken);
                        items.Add(new(id, code, name, "succeeded", null, null)); succeeded++;
                        break;
                    case "deactivate":
                        if (string.Equals(snap.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
                        { items.Add(new(id, code, name, "skipped", "already_in_target_status", null)); skipped++; break; }
                        await employeeManagement.ChangeStatusAsync(tenantId, id,
                            new EmployeeStatusChangeRequest(targetStatus, today, reason!), Context(), cancellationToken);
                        items.Add(new(id, code, name, "succeeded", null, null)); succeeded++;
                        break;
                    case "delete":
                        var tracked = await _db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id && !e.IsDeleted, cancellationToken);
                        if (tracked is null) { items.Add(new(id, code, name, "skipped", "already_deleted", null)); skipped++; break; }
                        await SoftDeleteEmployeeAsync(tenantId, tracked, reason, Context(), cancellationToken);
                        items.Add(new(id, code, name, "succeeded", null, null)); succeeded++;
                        break;
                }
            }
            catch (EmployeeActivationBlockedException ex)
            {
                // FLOOR-AWARE: a blocked activation is SKIPPED, never force-activated. The gate throws
                // before any mutation, but clear anyway before the per-row audit for uniformity.
                _db.ChangeTracker.Clear();
                await Audit("employee.activation_blocked", "Employee", id.ToString(), cancellationToken);
                items.Add(new(id, code, name, "skipped", "incomplete", ex.Readiness.Blocking.Select(ReadinessItemDto).ToList()));
                incompleteLabels.AddRange(ex.Readiness.Blocking.Select(b => b.Label));
                skipped++;
            }
            catch (EstablishmentBudgetExceededException)
            {
                _db.ChangeTracker.Clear(); // discard this row's tracked-but-rolled-back mutation
                items.Add(new(id, code, name, "skipped", "establishment_budget_exceeded", null));
                skipped++;
            }
            catch (InvalidOperationException ex)
            {
                _db.ChangeTracker.Clear();
                items.Add(new(id, code, name, "failed", ex.Message, null));
                failed++;
            }
        }
        // Ensure the batch audit's SaveChanges cannot flush a failed last row's residual mutation.
        _db.ChangeTracker.Clear();

        var requested = idsMode ? req.EmployeeIds!.Distinct().Count() : targetIds.Count;
        await _audit.WriteAsync("employees.bulk_action", "Employee", "bulk", Context(), JsonSerializer.Serialize(new
        {
            action,
            selectionType = idsMode ? "idset" : "allMatching",
            requested,
            succeeded,
            skipped,
            failed,
            targetStatus = action == "deactivate" ? targetStatus : null,
            reason,
            groupScope = entityScope.IsGroupLevel,
            companyIds = entityScope.IsGroupLevel ? null : entityScope.AccessibleCompanyIds,
            expectedCount = req.ExpectedCount,
            resolvedCount = targetIds.Count,
            ids = targetIds, // resolved set (already capped) — a single audit row is self-sufficient for forensics
        }), cancellationToken);

        return Ok(new BulkActionResult(action, requested, succeeded, skipped, failed, items,
            BuildBulkSummary(action, succeeded, skipped, failed, incompleteLabels)));
    }

    /// <summary>
    /// Resolves the authoritative target id set SERVER-SIDE for both selection modes. Starts from the
    /// EXACT active-People-list population (mirrors <see cref="Search"/>: not deleted, not a former-employee
    /// status), constrained by the caller's data scope AND company boundary, then narrows by the explicit
    /// id list (id-set mode) or the filter predicate (all-matching mode). Ids outside this set never
    /// survive — an id-set caller cannot reach an out-of-scope, other-company, or former-employee row.
    /// </summary>
    private async Task<List<int>> ResolveTargetIdsAsync(BulkActionRequest req, bool idsMode, Guid tenantId, DataScope scope, EntityScopeContext entityScope, CancellationToken ct)
    {
        var query = _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && !ExitEmployeeStatuses.Exit.Contains(e.Status));
        if (!scope.IsUnrestricted)
            query = query.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));
        if (!entityScope.IsGroupLevel)
        {
            var accessibleIds = entityScope.AccessibleCompanyIds;
            query = query.Where(e => e.CompanyId.HasValue && accessibleIds.Contains(e.CompanyId.Value));
        }

        if (idsMode)
        {
            var requested = req.EmployeeIds!.Distinct().ToList();
            query = query.Where(e => requested.Contains(e.Id));
        }
        else
        {
            var f = req.Filter ?? new BulkSelectAllFilter(null, null, null, null, null);
            if (!string.IsNullOrWhiteSpace(f.Search))
            {
                var term = f.Search.Trim();
                query = query.Where(e => e.EmployeeCode.Contains(term) || e.FullName.Contains(term)
                    || e.EnglishName.Contains(term) || e.ArabicName.Contains(term)
                    || (e.WorkEmail != null && e.WorkEmail.Contains(term)));
            }
            if (!string.IsNullOrWhiteSpace(f.Status)) query = query.Where(e => e.Status == f.Status);
            query = EmployeeReadinessQuery.ApplyReadinessFilter(query, _db, tenantId, f.Readiness, f.ImportBatchId, f.GapType);
        }

        return await query.Select(e => e.Id).ToListAsync(ct);
    }

    /// <summary>The required human summary: e.g. "12 activated, 3 skipped (incomplete: Iqama, GOSI reference), 1 failed".</summary>
    private static string BuildBulkSummary(string action, int succeeded, int skipped, int failed, IReadOnlyList<string> incompleteLabels)
    {
        var verb = action switch { "activate" => "activated", "deactivate" => "deactivated", "delete" => "deleted", _ => "processed" };
        var parts = new List<string> { $"{succeeded} {verb}" };
        if (skipped > 0)
        {
            var distinct = incompleteLabels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList();
            parts.Add(distinct.Count > 0 ? $"{skipped} skipped (incomplete: {string.Join(", ", distinct)})" : $"{skipped} skipped");
        }
        if (failed > 0) parts.Add($"{failed} failed");
        return string.Join(", ", parts);
    }

    private static object ReadinessItemDto(Zayra.Api.Infrastructure.Employees.ReadinessItem i) => new
    {
        key = i.Key,
        label = i.Label,
        category = i.Category,
        reason = i.Reason,
        jurisdiction = i.Jurisdiction,
        gate = i.Gate,
        fix = i.FixKind == "document"
            ? (object)new { kind = i.FixKind, documentType = i.DocumentType }
            : new { kind = i.FixKind, target = i.FixTarget },
    };

    /// <summary>Soft-deletes an employee record (audit trail preserved; hidden from all lists).</summary>
    [HttpDelete("{id:int}")]
    [HasPermission("employees.delete")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound();
        await SoftDeleteEmployeeAsync(tenantId, employee, reason: null, Context(), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Shared soft-remove primitive (used by the single Delete endpoint AND the bulk-action loop so the two
    /// never diverge): flags the record deleted → Ex-Employees archive, cancels + reroutes pending approval
    /// work, audits <c>employees.deleted</c> (with the caller's reason), and runs the exit payroll cascade.
    /// The employee MUST be a tracked entity (this method mutates it and SaveChanges). Idempotency: the
    /// caller is responsible for skipping rows that are already <c>IsDeleted</c>.
    /// </summary>
    private async Task SoftDeleteEmployeeAsync(Guid tenantId, Employee employee, string? reason, RequestContext context, CancellationToken cancellationToken)
    {
        var id = employee.Id;
        var deletedAt = DateTime.UtcNow;
        employee.IsDeleted = true;
        employee.DeletedAtUtc = deletedAt;
        employee.DeletedBy = context.UserId;
        employee.Status = "Inactive";
        employee.PrivacyStatus = "RetainedForStatutoryAudit";
        employee.RetentionUntilUtc = deletedAt.AddYears(7);

        var (cancelledApprovals, reroutedApprovals) = await CancelPendingApprovalWorkAsync(
            tenantId, id, employee.UserAccountId, deletedAt, "Employee record was deleted before approval.", cancellationToken);

        // ── CREDENTIAL REVOCATION ────────────────────────────────────────────────────────────────
        // Setting Status = "Inactive" above removes the person from every list, but by itself it
        // closes NO credential edge: their self-service login still authenticated and their live
        // refresh tokens kept minting access tokens after DELETE /api/employees/{id}. "Inactive" is
        // exactly the status ChangeStatusAsync revokes for, so this reuses that ONE primitive rather
        // than growing a second, weaker revocation. Staged (no ExecuteUpdate, no SaveChanges of its
        // own) so it flushes in the SAME SaveChanges as the delete — a revocation cannot commit
        // without the delete, and the delete cannot commit without the revocation.
        var (credentialLinks, credentialUsers, credentialUserIds) =
            await EmployeeManagementService.LoadCredentialGraphAsync(_db, tenantId, id, employee.UserAccountId, cancellationToken);
        // LINKED-LOGIN GATE: deleting the record always goes on; an Admin's login (or one above the actor) is left
        // active, audited and notified to the tenant's Admins instead.
        var gatedCredentials = await EmployeeManagementService.GateLinkedLoginsAsync(
            _db, tenantId, credentialLinks, credentialUsers, credentialUserIds, context,
            "employee.deleted", id, deletedAt, cancellationToken);
        var revokedCredentials = await EmployeeManagementService.StageCredentialInvalidationAsync(
            _db, gatedCredentials.Links, gatedCredentials.Users, gatedCredentials.UserIds,
            loginDisabledReason: "Employee record was deleted.",
            effectiveAtUtc: deletedAt,
            actorUserId: context.UserId,
            actorIpAddress: context.IpAddress,
            bulkTokenUpdates: false,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employees.deleted", "Employee", id.ToString(), context, JsonSerializer.Serialize(new
        {
            employee.PrivacyStatus,
            employee.RetentionUntilUtc,
            Reason = reason,
            CancelledApprovalRequests = cancelledApprovals,
            ReroutedApprovalRequests = reroutedApprovals,
            Credentials = new
            {
                links = revokedCredentials.Links,
                users = revokedCredentials.Users,
                passwordResets = revokedCredentials.PasswordResets,
                mfaChallenges = revokedCredentials.MfaChallenges,
                refreshTokens = revokedCredentials.RefreshTokens
            },
            ApproverDeletionFallback = "Pending approvals assigned to the deleted approver are rerouted to the HR Manager role queue; approvals for the deleted employee are cancelled."
        }), cancellationToken);

        // EXIT CASCADE (soft-delete): deactivate the full payroll footprint — salary structure(s) AND
        // WPS eligibility. Safe to deactivate the salary structure here because the employee is now
        // IsDeleted, and CalculateEosb / FinalSettlement filter on !IsDeleted, so they can no longer
        // read the row for this employee. Idempotent — a no-op if the employee was already terminated.
        await EmployeeManagementService.DeactivatePayrollFootprintAsync(
            _db, _audit, tenantId, id, "soft_deleted", deactivateSalaryStructure: true, context, cancellationToken);
    }

    /// <summary>
    /// Shared soft-remove cascade primitive (used by Delete and duplicate-merge so the two never diverge):
    /// cancels the employee's pending change-requests + their approval requests, and reroutes approvals the
    /// (removed) employee still owns to the HR Manager role queue so unrelated accountability survives. Does
    /// NOT SaveChanges/audit/deactivate-payroll — the caller orchestrates those so each keeps its own audit
    /// content. Returns (cancelled, rerouted) counts.
    /// </summary>
    private async Task<(int Cancelled, int Rerouted)> CancelPendingApprovalWorkAsync(
        Guid tenantId, int id, Guid? approverUserId, DateTime effectiveAt, string cancelReason, CancellationToken ct)
    {
        var pendingChanges = await _db.EmployeeChangeRequests
            .Where(x => x.TenantId == tenantId && x.EmployeeId == id && x.Status == "PendingApproval")
            .ToListAsync(ct);
        foreach (var change in pendingChanges)
        {
            change.Status = "Cancelled";
            change.RejectionReason = cancelReason;
            change.ApprovedAtUtc = effectiveAt;
        }

        var pendingChangeIds = pendingChanges.Select(x => x.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingApprovals = await _db.ApprovalRequests
            .Where(x => x.TenantId == tenantId
                && x.Status == "Pending"
                && (x.RequestedForEmployeeId == id
                    || (x.EntityName == nameof(EmployeeChangeRequest) && pendingChangeIds.Contains(x.EntityId))))
            .ToListAsync(ct);
        foreach (var approval in pendingApprovals)
        {
            approval.Status = "Cancelled";
            approval.CompletedAtUtc = effectiveAt;
        }

        var cancelledApprovalIds = pendingApprovals.Select(x => x.Id).ToHashSet();
        var assignedApprovals = await _db.ApprovalRequests
            .Where(x => x.TenantId == tenantId
                && x.Status == "Pending"
                && !cancelledApprovalIds.Contains(x.Id)
                && (x.CurrentApproverEmployeeId == id
                    || (approverUserId != null && x.CurrentApproverUserId == approverUserId)))
            .ToListAsync(ct);
        foreach (var approval in assignedApprovals)
        {
            approval.CurrentApproverEmployeeId = null;
            approval.CurrentApproverUserId = null;
            approval.CurrentApproverName = string.Empty;
            approval.CurrentApproverType = "Role";
            approval.CurrentApproverRole = "HR Manager";
            approval.CurrentQueue = "Role:HR Manager";
            approval.EscalatedAtUtc = effectiveAt;
            approval.EscalatedToRole = "HR Manager";
            approval.LastRoutedAtUtc = effectiveAt;
            approval.DueAtUtc = effectiveAt.AddHours(Math.Clamp(approval.SlaHours, 1, 720));
        }

        return (pendingApprovals.Count, assignedApprovals.Count);
    }

    /// <summary>
    /// Lightweight duplicate resolution from a flagged row / create warning. NEVER auto-merges — a human
    /// makes the call. "distinct" clears the dup:* flag(s) (both records survive; reason audited).
    /// "merge" (first slice) folds this record (the duplicate) into <c>intoEmployeeId</c> (the survivor):
    /// links via DuplicateOfEmployeeId + soft-removes exactly like Delete, preserving an audit link — NO
    /// field-by-field record copy (a later pod adds that, keyed off the link). "unmerge" reverses a merge
    /// (restores the record + link + best-effort payroll reactivation) so a mistaken twins-merge is
    /// recoverable (S4). Both records must be in the caller's scope.
    /// </summary>
    [HttpPost("{id:int}/resolve-duplicate")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> ResolveDuplicate(int id, [FromBody] ResolveDuplicateRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
        var resolution = (req.Resolution ?? string.Empty).Trim().ToLowerInvariant();
        var context = Context();

        if (resolution == "distinct")
        {
            var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
            if (employee is null) return NotFound();
            if (string.IsNullOrWhiteSpace(req.Reason))
                return BadRequest(new { message = "A reason is required to confirm this is a distinct person." });

            var open = await _db.EmployeeImportGaps
                .Where(g => g.TenantId == tenantId && g.EmployeeId == id && g.ResolvedAtUtc == null
                            && (g.GapType == "dup:strong" || g.GapType == "dup:possible"))
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var g in open) g.ResolvedAtUtc = now;
            await _db.SaveChangesAsync(ct);
            // Recompute the readiness badge so clearing the last dup flag can lift NeedsAttention.
            await RefreshReadinessByIdAsync(tenantId, id, ct);
            await _audit.WriteAsync("employee.duplicate_confirmed_distinct", "Employee", id.ToString(), context,
                JsonSerializer.Serialize(new { reason = req.Reason.Trim(), clearedGaps = open.Count }), ct);
            return Ok(new { resolved = "distinct", clearedGaps = open.Count });
        }

        if (resolution == "merge")
        {
            if (req.IntoEmployeeId is not int intoId) return BadRequest(new { message = "intoEmployeeId is required for a merge." });
            if (intoId == id) return BadRequest(new { message = "An employee cannot be merged into itself." });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return BadRequest(new { message = "A reason is required to merge a duplicate record." });
            if (!await CanAccessEmployeeAsync(intoId, ct)) return Forbid();

            var duplicate = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
            if (duplicate is null) return NotFound();
            var survivor = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == intoId && !x.IsDeleted, ct);
            if (survivor is null) return BadRequest(new { message = "The survivor record was not found." });

            var mergedAt = DateTime.UtcNow;
            var mergedCode = duplicate.EmployeeCode;
            duplicate.DuplicateOfEmployeeId = intoId;
            duplicate.IsDeleted = true;
            duplicate.DeletedAtUtc = mergedAt;
            duplicate.DeletedBy = context.UserId;
            duplicate.Status = "Inactive";
            duplicate.PrivacyStatus = "MergedDuplicate";
            duplicate.RetentionUntilUtc = mergedAt.AddYears(7);

            var (cancelled, rerouted) = await CancelPendingApprovalWorkAsync(
                tenantId, id, duplicate.UserAccountId, mergedAt, "Record merged into an existing employee as a duplicate.", ct);

            // Resolve any open dup flags on the merged record.
            var openGaps = await _db.EmployeeImportGaps
                .Where(g => g.TenantId == tenantId && g.EmployeeId == id && g.ResolvedAtUtc == null
                            && (g.GapType == "dup:strong" || g.GapType == "dup:possible"))
                .ToListAsync(ct);
            foreach (var g in openGaps) g.ResolvedAtUtc = mergedAt;

            await _db.SaveChangesAsync(ct);
            // Deactivate the merged record's payroll footprint (it is now IsDeleted, so EOSB/settlement
            // calculators can no longer read its salary structure). Reversible via unmerge (best-effort).
            await EmployeeManagementService.DeactivatePayrollFootprintAsync(
                _db, _audit, tenantId, id, "merged_duplicate", deactivateSalaryStructure: true, context, ct);
            // Full, reconstructable audit link (S4): both codes + signals, not just ids.
            await _audit.WriteAsync("employee.merged_into_existing", "Employee", id.ToString(), context, JsonSerializer.Serialize(new
            {
                intoEmployeeId = intoId,
                intoEmployeeCode = survivor.EmployeeCode,
                mergedEmployeeCode = mergedCode,
                reason = req.Reason!.Trim(),
                cancelledApprovals = cancelled,
                reroutedApprovals = rerouted,
                clearedGaps = openGaps.Count,
                reversible = "POST resolve-duplicate {resolution:'unmerge'} restores this record."
            }), ct);
            return Ok(new { resolved = "merge", intoEmployeeId = intoId });
        }

        if (resolution == "unmerge")
        {
            // Recovery path for a mistaken merge (twins wrongly merged). Reverses the link + restores the
            // record; payroll reactivation is best-effort (re-marks WPS-eligible + reactivates the latest
            // salary structure) — a deep field-level reconciliation is out of scope for this slice.
            // IgnoreQueryFilters is intentional: the merged record is soft-deleted so the global !IsDeleted
            // filter hides it — bypass to read it back (scope already enforced by CanAccessEmployeeAsync above).
            var merged = await _db.Employees.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && x.IsDeleted && x.DuplicateOfEmployeeId != null, ct);
            if (merged is null) return NotFound();
            var intoId = merged.DuplicateOfEmployeeId;
            merged.IsDeleted = false;
            merged.DeletedAtUtc = null;
            merged.DeletedBy = null;
            merged.RetentionUntilUtc = null;
            merged.PrivacyStatus = string.Empty;
            merged.Status = "Draft";
            merged.DuplicateOfEmployeeId = null;

            var profiles = await _db.EmployeePayrollProfiles
                .Where(x => x.TenantId == tenantId && x.EmployeeId == id && !x.IsDeleted).ToListAsync(ct);
            foreach (var p in profiles) { p.WpsEligible = true; p.UpdatedAtUtc = DateTime.UtcNow; p.UpdatedBy = context.UserId; }
            var latestStructure = await _db.EmployeeSalaryStructures
                .Where(x => x.TenantId == tenantId && x.EmployeeId == id)
                .OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(ct);
            if (latestStructure is not null) latestStructure.IsActive = true;

            await _db.SaveChangesAsync(ct);
            await RefreshReadinessByIdAsync(tenantId, id, ct);
            await _audit.WriteAsync("employee.duplicate_merge_reversed", "Employee", id.ToString(), context,
                JsonSerializer.Serialize(new { restoredFromMergeInto = intoId, reason = req.Reason?.Trim() }), ct);
            return Ok(new { resolved = "unmerge", restored = id });
        }

        return BadRequest(new { message = "resolution must be 'distinct', 'merge', or 'unmerge'." });
    }

    /// <summary>
    /// Clears the "imported bank details not yet verified" flag (<see cref="EmployeeImportGap.BankDetailsUnverified"/>)
    /// once HR has confirmed the IBAN/account with the employee. A second-person check, so it is refused to the
    /// user who created the imported payroll profile and to the employee themselves, and to a caller who cannot
    /// see the bank details being confirmed. Audited with the caller's note.
    /// </summary>
    [HttpPost("{id:int}/bank-details/confirm")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> ConfirmImportedBankDetails(int id, [FromBody] ConfirmBankDetailsRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        if (!await CanAccessEmployeeAsync(id, ct)) return Forbid();
        if (!CanViewSensitive())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "bank_details_not_visible",
                message = "Confirming bank details needs a role that can see them (employees.sensitive)." });
        if (string.IsNullOrWhiteSpace(req.Note))
            return BadRequest(new { error = "note_required", message = "Say how the bank details were confirmed (for example, with the employee's bank letter)." });

        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
        if (employee is null) return NotFound();
        var open = await _db.EmployeeImportGaps
            .Where(g => g.TenantId == tenantId && g.EmployeeId == id && g.ResolvedAtUtc == null
                        && g.GapType == EmployeeImportGap.BankDetailsUnverified)
            .ToListAsync(ct);
        if (open.Count == 0)
            return Conflict(new { error = "nothing_to_confirm", message = "This employee has no imported bank details waiting to be confirmed." });

        // Who imported them: the imported payroll profile's CreatedBy, AND the actor on each import's commit
        // marker (employee.import_committed, EntityId = the batch id), which is written in the import's own
        // transaction. If neither names anyone, the second-person check cannot be made, so it is refused
        // (fail closed) rather than letting an unknown importer confirm their own data.
        var callerId = GetUserId();
        var importers = new HashSet<Guid>();
        var profileCreatedBy = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.EmployeeId == id && !p.IsDeleted && p.CreatedBy != null)
            .Select(p => p.CreatedBy!.Value).ToListAsync(ct);
        importers.UnionWith(profileCreatedBy);
        var batchIds = open.Select(g => g.ImportBatchId.ToString()).Distinct().ToList();
        var markerActors = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Action == ImportCommittedAction && a.EntityName == ImportBatchEntityName
                        && a.EntityId != null && batchIds.Contains(a.EntityId) && a.UserId != null)
            .Select(a => a.UserId!.Value).ToListAsync(ct);
        importers.UnionWith(markerActors);
        if (importers.Count == 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "importer_unknown",
                message = "The import that set these bank details does not record who ran it, so a second-person check is not possible. "
                        + "Re-enter the bank details through the normal change approval instead." });
        if (callerId is null || importers.Contains(callerId.Value) || callerId == employee.UserAccountId)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "second_person_required",
                message = "Imported bank details must be confirmed by someone other than the person who imported them or the employee." });

        // ONE SaveChanges: AuditService adds its row to this same request-scoped context and saves, so the gap
        // resolution and the audit row commit together or not at all.
        var now = DateTime.UtcNow;
        foreach (var g in open) g.ResolvedAtUtc = now;
        await _audit.WriteAsync("employee.imported_bank_details_confirmed", "Employee", id.ToString(), Context(),
            JsonSerializer.Serialize(new { note = req.Note.Trim(), clearedGaps = open.Count, importBatchIds = open.Select(g => g.ImportBatchId).Distinct() }), ct);
        await RefreshReadinessByIdAsync(tenantId, id, ct);   // display badge only, best-effort
        return Ok(new { confirmed = true, clearedGaps = open.Count });
    }

    /// <summary>Refresh one employee's denormalized readiness badge after a dup-flag change (fold via the
    /// service's snapshot path). Best-effort — display only; the activation gate always recomputes live.</summary>
    private async Task RefreshReadinessByIdAsync(Guid tenantId, int id, CancellationToken ct)
    {
        try
        {
            var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
            if (employee is null) return;
            var snapshot = await _activationGuard.BuildSnapshotAsync(tenantId, id, ct);
            if (snapshot is null) return;
            var policy = await _activationGuard.ResolvePolicyAsync(tenantId, employee.CompanyId, employee.CountryCode, employee.Nationality, ct);
            var readiness = _activationGuard.Evaluate(snapshot, policy);
            readiness = await ImportGapHealer.HealAndMergeAsync(_db, employee, readiness, ct);
            employee.ReadinessState = readiness.State;
            employee.ActivationBlockersCount = readiness.Blocking.Count;
            employee.ReadinessEvaluatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch { /* best-effort badge refresh */ }
    }

    // RETIRED. This applied a sensitive change in one click: it skipped the EMPLOYEE-CHANGE workflow's
    // two steps (manager, then HR), the separation-of-duties bars (the employee the change is about
    // could approve their own salary or IBAN), and it left the change's ApprovalRequest open in the
    // Approval Center. No client calls it. Changes are decided only through ApprovalWorkflowService,
    // which is the one place those rules live. The permission stays declared so the catalog still
    // names the action that once used it.
    [HttpPost("changes/{changeId:guid}/approve")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> ApproveChange(Guid changeId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        if (!await _db.EmployeeChangeRequests.AnyAsync(x => x.Id == changeId && x.TenantId == tenantId, cancellationToken))
            return NotFound();
        return StatusCode(StatusCodes.Status410Gone, new
        {
            message = "Approving an employee change here is disabled. Decide it in the Approval Center "
                      + "(POST /api/approval-requests/{id}/decisions) so every approval step and separation-of-duties rule is enforced."
        });
    }

    private async Task<ApprovalWorkflow> EnsureEmployeeChangeWorkflowAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var workflow = await _db.ApprovalWorkflows
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.Code == "EMPLOYEE-CHANGE" && w.IsActive, cancellationToken);
        if (workflow is null)
        {
            workflow = new ApprovalWorkflow
            {
                TenantId = tenantId,
                Code = "EMPLOYEE-CHANGE",
                Name = "Employee Master Change Approval",
                EntityName = nameof(EmployeeChangeRequest),
                IsActive = true
            };
            _db.ApprovalWorkflows.Add(workflow);
        }
        if (workflow.Steps.Count != 2
            || workflow.Steps.All(x => !string.Equals(x.ApproverType, "Manager", StringComparison.OrdinalIgnoreCase))
            || workflow.Steps.All(x => !string.Equals(x.ApproverType, "Role", StringComparison.OrdinalIgnoreCase)))
        {
            _db.ApprovalWorkflowSteps.RemoveRange(workflow.Steps);
            workflow.Steps.Clear();
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId,
                WorkflowId = workflow.Id,
                StepOrder = 1,
                StepName = "Direct Manager Review",
                ApproverRole = "Manager",
                ApproverType = "Manager",
                EscalationAfterHours = 24,
                IsFinalStep = false
            });
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId,
                WorkflowId = workflow.Id,
                StepOrder = 2,
                StepName = "HR Final Approval",
                ApproverRole = "HR Manager",
                ApproverType = "Role",
                EscalationAfterHours = 48,
                IsFinalStep = true
            });
        }
        return workflow;
    }

    [HttpPost("{id:int}/transfer")]
    [HasPermission("manager.approve")]
    public async Task<ActionResult<EmployeeTransferDto>> RequestTransfer(int id, EmployeeTransferCreateRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = RequireTenant();
            var transfer = await employeeManagement.RequestTransferAsync(tenantId, id, request, Context(), cancellationToken);
            if (transfer is null) return NotFound();
            var dto = EmployeeTransferDto.Project(transfer);
            // Fast ADVISORY feedback at submission (never blocks — the authoritative check runs
            // at HR approval): would the target department/level cell be over budget today?
            if (transfer.NewDepartmentId is not null || transfer.NewDesignationId is not null)
            {
                var check = await _establishmentGuard.CheckAsync(tenantId,
                    transfer.NewDepartmentId ?? transfer.CurrentDepartmentId,
                    transfer.NewDesignationId, excludeEmployeeId: id, 1, cancellationToken);
                if (check.Block is { } block)
                    dto = dto with { EstablishmentWarning = $"{block.DepartmentName} already has {block.Current} of {block.Budgeted} budgeted {block.LevelNameEn}(s); HR approval of this transfer will be blocked unless the budget is raised or a seat frees." };
            }
            return Created($"/api/employees/transfers/{transfer.Id}", dto);
        }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new { message = ex.Message }); }
    }

    [HttpPost("transfers/{transferId:guid}/approve-current-manager")]
    [HasPermission("manager.approve")]
    public Task<IActionResult> ApproveCurrentManager(Guid transferId, CancellationToken cancellationToken) =>
        AdvanceTransfer(transferId, "PendingCurrentManager", "PendingNewManager", x => x.CurrentManagerEmployeeId, x => x.CurrentManagerApprovedAtUtc = DateTime.UtcNow, cancellationToken);

    [HttpPost("transfers/{transferId:guid}/approve-new-manager")]
    [HasPermission("manager.approve")]
    public Task<IActionResult> ApproveNewManager(Guid transferId, CancellationToken cancellationToken) =>
        AdvanceTransfer(transferId, "PendingNewManager", "PendingHrApproval", x => x.NewManagerEmployeeId, x => x.NewManagerApprovedAtUtc = DateTime.UtcNow, cancellationToken);

    [HttpPost("transfers/{transferId:guid}/approve-hr")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> ApproveHrTransfer(Guid transferId, [FromServices] IHrmHierarchyService hierarchy, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var transfer = await _db.EmployeeTransferRequests.FirstOrDefaultAsync(x => x.Id == transferId && x.TenantId == tenantId, cancellationToken);
        if (transfer is null) return NotFound();
        if (transfer.Status != "PendingHrApproval")
            return BadRequest(new { message = $"Transfer is in '{transfer.Status}' status and cannot be HR-approved." });
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.Id == transfer.EmployeeId && x.TenantId == tenantId, cancellationToken);
        if (employee is null) return NotFound();

        // Transfer-integrity apply (Batch A / spec §1.10, AC8): work off resolved IDs — prefer the
        // IDs stored at request time; legacy pre-migration rows (strings only) are re-resolved
        // here; a still-unresolvable non-empty name is a 422 telling HR to fix master data. The
        // string-only write path that manufactured uncountable employees is gone.
        try
        {
            var newDeptId = transfer.NewDepartmentId; var newDeptName = transfer.NewDepartment;
            if (newDeptId is null && !string.IsNullOrWhiteSpace(transfer.NewDepartment))
                (newDeptId, newDeptName) = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(_db, tenantId, transfer.NewDepartment, cancellationToken);
            var newBranchId = transfer.NewBranchId; var newBranchName = transfer.NewBranch;
            if (newBranchId is null && !string.IsNullOrWhiteSpace(transfer.NewBranch))
                (newBranchId, newBranchName) = await EmployeeOrgFieldResolver.ResolveBranchAsync(_db, tenantId, transfer.NewBranch, cancellationToken);
            var newDesigId = transfer.NewDesignationId; var newDesigTitle = transfer.NewDesignation;
            if (newDesigId is null && !string.IsNullOrWhiteSpace(transfer.NewDesignation))
                (newDesigId, newDesigTitle) = await EmployeeOrgFieldResolver.ResolveDesignationAsync(_db, tenantId, transfer.NewDesignation, cancellationToken);

            if (newDeptId is not null) { employee.DepartmentId = newDeptId; employee.Department = newDeptName; }
            if (newBranchId is not null) { employee.BranchId = newBranchId; employee.Branch = newBranchName; }
            if (newDesigId is not null) { employee.DesignationId = newDesigId; employee.Designation = newDesigTitle; }
            employee.UpdatedAtUtc = DateTime.UtcNow;
            transfer.Status = "ApprovedApplied";
            transfer.HrApprovedAtUtc = DateTime.UtcNow;
            await AddHistory(employee, "TransferApproved", transfer.EffectiveDate, cancellationToken);

            // ESTABLISHMENT GUARD (path "transfer"): inbound department/level consumes a seat;
            // outbound frees implicitly (target-state evaluation with self-exclusion). On a block
            // nothing persists — the transfer stays PendingHrApproval for re-approval after a raise.
            await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
                excludeEmployeeId: employee.Id, path: "transfer", Context(), async () =>
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    return true;
                }, cancellationToken);
            if (employee.ManagerEmployeeId != transfer.NewManagerEmployeeId)
                await hierarchy.SetManagerAsync(tenantId, employee.Id, transfer.NewManagerEmployeeId, Context(), cancellationToken);
            await Notify("Employee transfer approved", $"Transfer for employee {employee.EmployeeCode} was applied.", "EmployeeTransferRequest", transfer.Id.ToString(), cancellationToken);
            await Audit("employee.transfer_approved", "EmployeeTransferRequest", transfer.Id.ToString(), cancellationToken);
            return Ok(EmployeeDetailDto.Project(employee, CanViewSensitive()));
        }
        catch (EstablishmentBudgetExceededException ex)
        {
            // Discard the half-applied tracked mutations BEFORE any further write on this context
            // (Notify saves): the transfer must remain PendingHrApproval untouched.
            _db.ChangeTracker.Clear();
            await Notify("Employee transfer blocked by staffing budget",
                $"Transfer for {employee.EmployeeCode} could not be applied: {ex.Block.DepartmentName} already has {ex.Block.Current} of {ex.Block.Budgeted} budgeted {ex.Block.LevelNameEn}(s). Raise the budget or choose a different department; the transfer remains pending.",
                "EmployeeTransferRequest", transfer.Id.ToString(), cancellationToken);
            return this.EstablishmentConflict(ex);
        }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new { message = ex.Message }); }
    }

    [HttpGet("reports/summary")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Auditor")]
    public async Task<ActionResult<EmployeeReportsDto>> Reports(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var employees = _db.Employees.Where(x => x.TenantId == tenantId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var next60 = today.AddDays(60);
        return Ok(new EmployeeReportsDto(
            await employees.CountAsync(cancellationToken),
            await employees.CountAsync(x => x.Status == "Active", cancellationToken),
            await employees.CountAsync(x => x.JoiningDate >= DateTime.UtcNow.AddDays(-30), cancellationToken),
            await employees.CountAsync(x => x.Status == "Exited" || x.Status == "Terminated", cancellationToken),
            await employees.CountAsync(x => x.ProbationEndDate != null && x.ProbationEndDate >= today, cancellationToken),
            await employees.GroupBy(x => x.Department).Select(x => new GroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken),
            await employees.GroupBy(x => x.Branch).Select(x => new GroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken),
            await employees.GroupBy(x => x.Nationality).Select(x => new GroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken),
            await employees.GroupBy(x => x.Gender).Select(x => new GroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken),
            await employees.CountAsync(x => x.ContractEndDate != null && x.ContractEndDate <= next60, cancellationToken),
            await employees.CountAsync(x => (x.VisaExpiryDate != null && x.VisaExpiryDate <= next60) || (x.PassportExpiryDate != null && x.PassportExpiryDate <= next60), cancellationToken),
            await employees.CountAsync(x => x.ProfileCompletenessScore < 80, cancellationToken)));
    }

    [HttpGet("reports/headcount")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Auditor")]
    public async Task<ActionResult<EmployeeHeadcountReportDto>> Headcount([FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        return Ok(await employeeManagement.HeadcountAsync(RequireTenant(), cancellationToken));
    }

    [HttpGet("reports/expiring-documents")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Auditor")]
    public async Task<ActionResult<IReadOnlyCollection<EmployeeExpiringDocumentDto>>> ExpiringDocuments([FromServices] IEmployeeManagementService employeeManagement, [FromQuery] int days = 60, CancellationToken cancellationToken = default)
    {
        // DATA SCOPE: names with document expiry, so a team-scoped caller (a Manager or Supervisor reaching
        // this through employees.read) sees their reporting line, and a company-scoped caller their companies.
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        return Ok(await employeeManagement.ExpiringDocumentsAsync(tenantId, days, cancellationToken, scope.AllowedEmployeeIds));
    }

    [HttpGet("reports/missing-documents")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Auditor")]
    public async Task<ActionResult<IReadOnlyCollection<EmployeeMissingDocumentsReportDto>>> MissingDocuments([FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        // DATA SCOPE: same rule as expiring-documents above.
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        return Ok(await employeeManagement.MissingDocumentsAsync(tenantId, cancellationToken, scope.AllowedEmployeeIds));
    }

    [HttpGet("reports/status-summary")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Auditor")]
    public async Task<ActionResult<EmployeeStatusSummaryDto>> StatusSummary([FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        return Ok(await employeeManagement.StatusSummaryAsync(RequireTenant(), cancellationToken));
    }

    [HttpPost("reports/documents/check-expiry")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<DocumentExpiryCheckResult>> CheckDocumentExpiry([FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        return Ok(await employeeManagement.CheckDocumentExpiryAsync(RequireTenant(), cancellationToken));
    }

    /// <summary>
    /// Natural-language roster insight. This is a DIRECTORY READ dressed as a question, so it carries the
    /// EXACT authorization of the People list (<see cref="Search"/>): the same six roles at the attribute,
    /// and the same <c>_scopeService.ResolveAsync</c> + entity-scope company boundary on the query.
    ///
    /// Before this it was a bare [Authorize] over <c>_db.Employees.Where(TenantId == …)</c>, so any
    /// authenticated principal — an ESS-only employee included — could page out up to 50 colleagues per
    /// question (name, department, designation, branch, manager, status, visa/passport expiry), and
    /// <c>?query=bank</c> enumerated exactly who has no IBAN on file. The projection is the non-sensitive
    /// <see cref="EmployeeListItemDto"/>, which is why the fix is a scope gate rather than a mask.
    /// </summary>
    [HttpGet("ai/insights")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Manager,Auditor")]
    public async Task<ActionResult<EmployeeAiResponseDto>> AiInsights([FromQuery] string query, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var entityScope = this.GetEntityScope();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);

        var normalized = (query ?? string.Empty).ToLowerInvariant();
        // Same base predicate as the People list: tenant, not soft-deleted, not a former employee.
        var employees = _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && !ExitEmployeeStatuses.Exit.Contains(x.Status));
        // DATA SCOPE: a Manager/Supervisor sees their reporting tree, an unscoped caller sees nothing.
        if (!scope.IsUnrestricted)
            employees = employees.Where(x => scope.AllowedEmployeeIds!.Contains(x.Id));
        // COMPANY BOUNDARY: a company-scoped caller never sees a sibling company; a null CompanyId is
        // invisible to them (the poison-default rule the People list applies).
        if (!entityScope.IsGroupLevel)
        {
            var accessibleIds = entityScope.AccessibleCompanyIds;
            employees = employees.Where(x => x.CompanyId.HasValue && accessibleIds.Contains(x.CompanyId.Value));
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        if (normalized.Contains("iqama") || normalized.Contains("visa") || normalized.Contains("expiry"))
        {
            var days = normalized.Contains("60") ? 60 : 30;
            var until = today.AddDays(days);
            var matches = await employees.Where(x => (x.VisaExpiryDate != null && x.VisaExpiryDate <= until) || (x.PassportExpiryDate != null && x.PassportExpiryDate <= until)).Take(50).ToListAsync(cancellationToken);
            return Ok(new EmployeeAiResponseDto($"Found {matches.Count} employees with visa/passport expiry risk in the next {days} days.", matches.Select(ToListItem).ToList()));
        }
        if (normalized.Contains("bank"))
        {
            var matches = await employees.Where(x => x.BankIban == "" || x.BankName == "").Take(50).ToListAsync(cancellationToken);
            return Ok(new EmployeeAiResponseDto($"Found {matches.Count} employees missing bank details.", matches.Select(ToListItem).ToList()));
        }
        if (normalized.Contains("probation"))
        {
            var monthEnd = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
            var matches = await employees.Where(x => x.ProbationEndDate >= today && x.ProbationEndDate <= monthEnd).Take(50).ToListAsync(cancellationToken);
            return Ok(new EmployeeAiResponseDto($"Found {matches.Count} employees with probation ending this month.", matches.Select(ToListItem).ToList()));
        }
        var incomplete = await employees.Where(x => x.ProfileCompletenessScore < 80).Take(50).ToListAsync(cancellationToken);
        return Ok(new EmployeeAiResponseDto($"Found {incomplete.Count} employees with incomplete onboarding profiles.", incomplete.Select(ToListItem).ToList()));
    }

    /// <summary>
    /// Appointment letter and experience certificate.
    ///
    /// <para>These two used to call the hard-coded QuestPDF methods on <c>ILetterService</c>:
    /// no tenant-editable wording, no Arabic, a company name that fell back to the literal
    /// "KynexOne Technologies", a signature block reading "HR Department" over a row of
    /// underscores, a reference recomputed inline as <c>EXP-{code}-{yyyyMM}</c> that collided
    /// within the month, and no record anywhere that the document had been produced.</para>
    ///
    /// <para>They now go through <see cref="IHrLetterIssuer"/> like every other letter: tenant
    /// template, bilingual, stored unique reference, register row. The routes and the response
    /// shape are unchanged, so nothing calling them has to move.</para>
    /// </summary>
    // Role-gate bypass sweep (LegacyRoleGateBypassSweepTests): issuing a registered letter (appointment letter states salary) resolved to employees.read; HR roles hold employees.write.
    [HttpGet("{id:int}/letters/appointment")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public Task<IActionResult> AppointmentLetter(int id, [FromQuery] string language = HrLetterLanguages.Bilingual, CancellationToken cancellationToken = default)
        => IssueRegisteredLetterAsync(id, HrLetterTypes.AppointmentLetter, language, cancellationToken);

    [HttpGet("{id:int}/letters/experience")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public Task<IActionResult> ExperienceLetter(int id, [FromQuery] string language = HrLetterLanguages.Bilingual, CancellationToken cancellationToken = default)
        => IssueRegisteredLetterAsync(id, HrLetterTypes.ExperienceCertificate, language, cancellationToken);

    private async Task<IActionResult> IssueRegisteredLetterAsync(int id, string letterType, string language, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        if (!scope.CanAccessEmployee(id)) return Forbid();

        var issuerName = User.FindFirstValue("name") ?? User.FindFirstValue(ClaimTypes.Name) ?? "HR Department";
        if (GetUserId() is Guid uid)
        {
            var dbName = await _db.Users.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == uid).Select(x => x.FullName)
                .FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(dbName)) issuerName = dbName;
        }
        var issuerTitle = User.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .FirstOrDefault(r => r is "HR Manager" or "HR Officer" or "Admin") ?? "Human Resources";

        var result = await _letterIssuer.IssueAsync(new IssueLetterCommand(
            TenantId: tenantId,
            EmployeeId: id,
            LetterType: letterType,
            Language: language,
            Purpose: "the employee's personal records",
            AddresseeName: string.Empty,
            IssuedByUserId: GetUserId(),
            IssuerName: issuerName,
            IssuerTitle: issuerTitle), cancellationToken);

        if (!result.Ok)
        {
            var payload = new { code = result.ErrorCode, message = result.ErrorMessage, unresolvedFields = result.UnresolvedTokens };
            return result.ErrorCode == "employee_not_found" ? NotFound(payload) : Conflict(payload);
        }

        await Audit($"employee.letter.{letterType}", "Employee", id.ToString(), cancellationToken);
        Response.Headers["X-Letter-Reference"] = result.Letter!.ReferenceNumber;
        return File(result.Pdf!, "application/pdf", $"{result.Letter.ReferenceNumber}.pdf");
    }

    [HttpGet("{id:int}/templates/{templateType}")]
    public async Task<IActionResult> RenderTemplate(int id, string templateType, [FromQuery] string language = "en", CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(id))
            return Forbid();
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);
        if (employee is null) return NotFound();
        var hijriJoining = _hijri.FromGregorian(DateOnly.FromDateTime(employee.JoiningDate));
        var isArabic = language.Equals("ar", StringComparison.OrdinalIgnoreCase);
        var title = templateType.ToLowerInvariant() switch
        {
            "contract" => isArabic ? "عقد عمل" : "Employment Contract",
            "sponsorship" => isArabic ? "خطاب كفالة" : "Sponsorship Letter",
            "offer" => isArabic ? "عرض عمل" : "Offer Letter",
            _ => isArabic ? "خطاب موظف" : "Employee Letter"
        };
        var body = isArabic
            ? $"{title}\n\nالاسم: {FirstNonEmpty(employee.ArabicName, employee.EnglishName, employee.FullName)}\nالرقم الوظيفي: {employee.EmployeeCode}\nالقسم: {employee.Department}\nتاريخ الانضمام: {employee.JoiningDate:yyyy-MM-dd} / {hijriJoining.HijriDate}\nالكفيل: {employee.SponsorName}\n"
            : $"{title}\n\nName: {FirstNonEmpty(employee.EnglishName, employee.FullName)}\nEmployee ID: {employee.EmployeeCode}\nDepartment: {employee.Department}\nJoining date: {employee.JoiningDate:yyyy-MM-dd} / Hijri {hijriJoining.HijriDate}\nSponsor: {employee.SponsorName}\n";
        return Ok(new EmployeeTemplateDto(templateType, language, title, body));
    }

    [HttpGet("{id:int}/localized-dates")]
    public async Task<IActionResult> LocalizedDates(int id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(id))
            return Forbid();
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);
        if (employee is null) return NotFound();
        return Ok(new
        {
            joiningDate = _hijri.FromGregorian(DateOnly.FromDateTime(employee.JoiningDate)),
            passportExpiryDate = employee.PassportExpiryDate is null ? null : _hijri.FromGregorian(employee.PassportExpiryDate.Value),
            visaExpiryDate = employee.VisaExpiryDate is null ? null : _hijri.FromGregorian(employee.VisaExpiryDate.Value),
            contractEndDate = employee.ContractEndDate is null ? null : _hijri.FromGregorian(employee.ContractEndDate.Value)
        });
    }

    private async Task<IActionResult> AdvanceTransfer(
        Guid transferId,
        string expectedStatus,
        string nextStatus,
        Func<EmployeeTransferRequest, int?> expectedManagerId,
        Action<EmployeeTransferRequest> stamp,
        CancellationToken cancellationToken)
    {
        var transfer = await _db.EmployeeTransferRequests.FirstOrDefaultAsync(x => x.Id == transferId && x.TenantId == RequireTenant(), cancellationToken);
        if (transfer is null) return NotFound();
        if (transfer.Status != expectedStatus)
            return BadRequest(new { message = $"Transfer is in '{transfer.Status}' status and cannot move to '{nextStatus}'." });
        if (!User.IsInRole("Admin") && !User.IsInRole("HR Manager"))
        {
            var callerEmployeeId = await GetCallerEmployeeId(cancellationToken);
            if (callerEmployeeId is null || expectedManagerId(transfer) != callerEmployeeId)
                return Forbid();
        }
        stamp(transfer);
        transfer.Status = nextStatus;
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.transfer_advanced", "EmployeeTransferRequest", transfer.Id.ToString(), cancellationToken);
        return Ok(transfer);
    }

    private Task<int?> GetCallerEmployeeId(CancellationToken cancellationToken) =>
        CallerEmployeeResolver.ResolveAsync(_db, User, RequireTenant(), cancellationToken);

    // ── Employee draft lifecycle helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The drafts the caller may see. A draft carries no legal entity until approval resolves one, so
    /// the scope is: group scope sees every draft in the tenant; a company-scoped user sees the drafts
    /// they made, plus accepted-offer drafts whose job application belongs to one of their companies.
    /// Every draft endpoint (list, review, edit, documents, submit, approve, reject, withdraw) starts
    /// here, so what a user can act on is exactly what they can list.
    /// </summary>
    private IQueryable<EmployeeDraft> VisibleDrafts(Guid tenantId)
    {
        var scope = this.GetEntityScope();
        var drafts = _db.EmployeeDrafts.Where(d => d.TenantId == tenantId);
        if (scope.IsGroupLevel) return drafts;
        var actorId = GetUserId();
        var applications = ScopedBypass.ForCompanies(_db.JobApplications, tenantId, scope.AccessibleCompanyIds,
            "A company-scoped user sees an accepted offer's draft only when its application is in one of their companies.");
        return drafts.Where(d =>
            (actorId != null && d.CreatedByUserId == actorId)
            || applications.Any(a => a.OnboardingDraftId == d.Id));
    }

    private ObjectResult MakerCheckerForbidden() => StatusCode(StatusCodes.Status403Forbidden, new
    {
        error = DraftMakerChecker.Error,
        message = DraftMakerChecker.Message,
    });

    /// <summary>Maker-checker for a draft decision: no maker of the hire (<see cref="IDraftHireMakers"/>)
    /// may approve or reject it, and neither may an anonymous caller.</summary>
    private async Task<ObjectResult?> MakerCheckerRefusalAsync(Guid tenantId, Guid draftId, CancellationToken ct)
    {
        var actorId = GetUserId();
        if (actorId is null) return MakerCheckerForbidden();
        var makers = await _draftHireMakers.MakersAsync(tenantId, draftId, ct);
        return makers.Contains(actorId.Value) ? MakerCheckerForbidden() : null;
    }

    /// <summary>
    /// One change to an open draft, as a single unit. The draft row is locked, so the change cannot land
    /// on a draft an approval is activating; not-found and closed are decided under that lock; and the
    /// change is saved in the same transaction as the audit row that records who made it. That row is
    /// what makes its author a maker of the hire (<see cref="IDraftHireMakers"/>), so the two can never
    /// be split. Returns the refusal, or null when the change was saved.
    /// </summary>
    private async Task<IActionResult?> ChangeOpenDraftAsync(
        Guid tenantId, Guid draftId, string auditAction,
        Func<EmployeeDraft, CancellationToken, Task<IActionResult?>> change, CancellationToken cancellationToken)
    {
        var auditId = Guid.NewGuid();
        var context = Context() with { TenantId = tenantId };
        IActionResult? refusal = null;

        async Task<bool> OnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            refusal = null;
            var draft = await VisibleDrafts(tenantId).TagWith(RowLockingInterceptor.ForUpdateTag)
                .FirstOrDefaultAsync(d => d.Id == draftId, ct);
            if (draft is null) { refusal = NotFound(); return false; }
            if (!EmployeeDraftStatuses.IsOpen(draft.Status))
            {
                refusal = await DraftClosedConflictAsync(tenantId, draftId, draft.Status, ct);
                return false;
            }
            refusal = await change(draft, ct);
            if (refusal is not null) return false;
            _db.AuditLogs.Add(AuthAuditEntry.Create(auditId, DateTime.UtcNow, auditAction, "EmployeeDraft", draftId.ToString(), context));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (!_db.Database.IsRelational())
        {
            await OnceAsync(cancellationToken);
            return refusal;
        }
        await _db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
            OnceAsync,
            async ct => await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenantId,
                    "Commit verification of this command's own audit row by its server-generated id.")
                .AsNoTracking().AnyAsync(a => a.Id == auditId, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
        return refusal;
    }

    /// <summary>
    /// A draft's manager must be a live employee of this tenant that the caller can see: the same rule
    /// every other manager write enforces (<see cref="EmployeeChangeApplier.ValidateManagerChangeAsync"/>).
    /// The draft has no employee id yet, so self-management and reporting cycles cannot arise.
    /// </summary>
    private async Task<ObjectResult?> DraftManagerRefusalAsync(Guid tenantId, int? managerId, CancellationToken ct)
    {
        var rejection = await DraftManagerRejectionAsync(tenantId, managerId, ct);
        if (rejection is null) return null;
        var body = new { error = "invalid_manager", message = rejection.Message + " The draft was not saved." };
        return rejection.OutOfScope ? StatusCode(StatusCodes.Status403Forbidden, body) : UnprocessableEntity(body);
    }

    private async Task<EmployeeChangeApplier.ManagerChangeRejection?> DraftManagerRejectionAsync(Guid tenantId, int? managerId, CancellationToken ct)
    {
        if (managerId is null) return null;
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        return await EmployeeChangeApplier.ValidateManagerChangeAsync(
            _db, new Employee { TenantId = tenantId },
            new Dictionary<string, JsonElement> { ["managerEmployeeId"] = JsonSerializer.SerializeToElement(managerId.Value) },
            scope.CanAccessEmployee, ct);
    }

    private async Task<string> CurrentDraftStatusAsync(Guid tenantId, Guid draftId, CancellationToken ct) =>
        await _db.EmployeeDrafts.AsNoTracking()
            .Where(d => d.Id == draftId && d.TenantId == tenantId)
            .Select(d => d.Status)
            .SingleAsync(ct);

    /// <summary>409 for a draft that can no longer move: says which state it is in and, for an
    /// activated draft, which employee it became.</summary>
    private async Task<ConflictObjectResult> DraftClosedConflictAsync(Guid tenantId, Guid draftId, string status, CancellationToken ct)
    {
        int? employeeId = null;
        string? employeeCode = null;
        if (status == EmployeeDraftStatuses.Activated)
        {
            var draft = await _db.EmployeeDrafts.AsNoTracking()
                .SingleAsync(d => d.Id == draftId && d.TenantId == tenantId, ct);
            var decisions = await LoadDraftDecisionsAsync(tenantId, new[] { draft }, ct);
            if (decisions.TryGetValue(draftId, out var decision))
            {
                employeeId = decision.EmployeeId;
                employeeCode = decision.EmployeeCode;
            }
        }
        var closed = !EmployeeDraftStatuses.IsOpen(status);
        return Conflict(new
        {
            error = closed ? "draft_closed" : "draft_state_changed",
            status,
            employeeId,
            employeeCode,
            message = closed
                ? EmployeeDraftStatuses.ClosedMessage(status, employeeCode)
                : "This draft changed while you were working on it. Reload it and try again.",
        });
    }

    /// <summary>
    /// Moves a draft between lifecycle states as one compare-and-swap plus its audit row, in one
    /// transaction. The UPDATE only matches while the draft is still in one of <paramref name="from"/>,
    /// so a transition racing an approval (which holds the draft row lock) re-evaluates after that
    /// commit and matches nothing. Returns false when the draft was no longer in a <paramref name="from"/> state.
    /// </summary>
    private async Task<bool> TransitionDraftAsync(
        Guid tenantId, Guid draftId, string[] from,
        System.Linq.Expressions.Expression<Func<Microsoft.EntityFrameworkCore.Query.SetPropertyCalls<EmployeeDraft>, Microsoft.EntityFrameworkCore.Query.SetPropertyCalls<EmployeeDraft>>> setters,
        Action<EmployeeDraft> applyTracked,
        string auditAction, string? auditMetadata, CancellationToken cancellationToken)
    {
        var auditId = Guid.NewGuid();
        var at = DateTime.UtcNow;
        var context = Context() with { TenantId = tenantId };

        async Task<bool> OnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            if (_db.Database.IsRelational())
            {
                var changed = await _db.EmployeeDrafts
                    .Where(d => d.Id == draftId && d.TenantId == tenantId && from.Contains(d.Status))
                    .ExecuteUpdateAsync(setters, ct);
                if (changed == 0) return false;
            }
            else
            {
                // EF's in-memory provider (fast unit tests) cannot run set-based updates; the same
                // state check runs on the tracked row. Concurrency is proven on Postgres.
                var tracked = await _db.EmployeeDrafts.SingleOrDefaultAsync(d => d.Id == draftId && d.TenantId == tenantId, ct);
                if (tracked is null || !from.Contains(tracked.Status)) return false;
                applyTracked(tracked);
            }
            _db.AuditLogs.Add(AuthAuditEntry.Create(auditId, at, auditAction, "EmployeeDraft", draftId.ToString(), context, auditMetadata));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (!_db.Database.IsRelational()) return await OnceAsync(cancellationToken);
        return await _db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
            OnceAsync,
            async ct => await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenantId,
                    "Commit verification of this command's own audit row by its server-generated id.")
                .AsNoTracking().AnyAsync(a => a.Id == auditId, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    /// <summary>Notification is post-commit and best-effort: an outage must not turn a durable state
    /// change into an error that invites a retry.</summary>
    private async Task NotifyBestEffortAsync(string title, string message, Guid draftId, CancellationToken ct)
    {
        try { await Notify(title, message, "EmployeeDraft", draftId.ToString(), ct); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Draft {DraftId} changed state, but the notification failed.", draftId); }
    }

    private sealed record DraftDecision(string Action, Guid? UserId, DateTime AtUtc, string? Reason, int? EmployeeId, string? EmployeeCode);

    /// <summary>The latest decision recorded for each closed draft (activated, rejected, withdrawn),
    /// read from the draft's own audit rows. An activation from before those rows existed is found by
    /// its employee.activated marker, which is written at exactly the draft's ActivatedAtUtc.</summary>
    private async Task<Dictionary<Guid, DraftDecision>> LoadDraftDecisionsAsync(Guid tenantId, IReadOnlyCollection<EmployeeDraft> drafts, CancellationToken ct)
    {
        var result = new Dictionary<Guid, DraftDecision>();
        var closed = drafts.Where(d => !EmployeeDraftStatuses.IsOpen(d.Status)).ToList();
        if (closed.Count == 0) return result;

        var entityIds = closed.Select(d => d.Id.ToString()).ToList();
        var rows = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenantId,
                "Decision rows for drafts the caller can already see, whichever company they were stamped with.")
            .AsNoTracking()
            .Where(a => a.EntityName == "EmployeeDraft" && a.EntityId != null
                && entityIds.Contains(a.EntityId) && EmployeeDraftAuditActions.Decisions.Contains(a.Action))
            .Select(a => new { a.EntityId, a.Action, a.UserId, a.CreatedAtUtc, a.Metadata })
            .ToListAsync(ct);
        foreach (var row in rows.OrderBy(r => r.CreatedAtUtc))
        {
            if (!Guid.TryParse(row.EntityId, out var id)) continue;
            string? reason = null; int? employeeId = null;
            if (!string.IsNullOrWhiteSpace(row.Metadata))
            {
                try
                {
                    using var json = JsonDocument.Parse(row.Metadata);
                    if (json.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (json.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String) reason = r.GetString();
                        if (json.RootElement.TryGetProperty("employeeId", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var eid)) employeeId = eid;
                    }
                }
                catch (JsonException) { /* an unreadable row still records who and when */ }
            }
            result[id] = new DraftDecision(row.Action, row.UserId, row.CreatedAtUtc, reason, employeeId, null);
        }

        // Activations recorded before the draft-side row existed: the employee.activated marker.
        var legacy = closed.Where(d => d.Status == EmployeeDraftStatuses.Activated && d.ActivatedAtUtc.HasValue && !result.ContainsKey(d.Id)).ToList();
        if (legacy.Count > 0)
        {
            var times = legacy.Select(d => d.ActivatedAtUtc!.Value).Distinct().ToList();
            var markers = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenantId,
                    "Activation markers of drafts the caller can already see, matched by timestamp and then by the draft id inside each marker.")
                .AsNoTracking()
                .Where(a => a.Action == "employee.activated" && times.Contains(a.CreatedAtUtc))
                .Select(a => new { a.EntityId, a.UserId, a.CreatedAtUtc, a.Metadata })
                .ToListAsync(ct);
            foreach (var marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker.Metadata) || !int.TryParse(marker.EntityId, out var eid)) continue;
                try
                {
                    using var json = JsonDocument.Parse(marker.Metadata);
                    if (json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("draftId", out var d) && d.ValueKind == JsonValueKind.String
                        && Guid.TryParse(d.GetString(), out var did) && legacy.Any(x => x.Id == did))
                        result[did] = new DraftDecision(EmployeeDraftAuditActions.Activated, marker.UserId, marker.CreatedAtUtc, null, eid, null);
                }
                catch (JsonException) { /* skip an unreadable marker */ }
            }
        }

        var employeeIds = result.Values.Where(v => v.EmployeeId.HasValue).Select(v => v.EmployeeId!.Value).Distinct().ToList();
        if (employeeIds.Count > 0)
        {
            var codes = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId,
                    "The codes of the employees drafts the caller can already see became, in whichever company they landed.")
                .AsNoTracking()
                .Where(e => employeeIds.Contains(e.Id))
                .Select(e => new { e.Id, e.EmployeeCode })
                .ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);
            foreach (var (draftId, decision) in result.ToList())
                if (decision.EmployeeId is { } eid && codes.TryGetValue(eid, out var code))
                    result[draftId] = decision with { EmployeeCode = code };
        }
        return result;
    }

    private async Task<IReadOnlyList<EmployeeDraftListItemDto>> ToDraftListItemsAsync(
        Guid tenantId, IReadOnlyCollection<EmployeeDraft> drafts, CancellationToken ct)
    {
        if (drafts.Count == 0) return Array.Empty<EmployeeDraftListItemDto>();
        var actorId = GetUserId();
        var canApprovePermission = User.HasPermission("employees.approve");
        var draftIds = drafts.Select(d => d.Id).ToList();

        var origins = await ScopedBypass.TenantWide(_db.JobApplications, tenantId,
                "The job application behind each draft the caller can already see (visibility was decided by VisibleDrafts).")
            .AsNoTracking()
            .Where(a => a.OnboardingDraftId != null && draftIds.Contains(a.OnboardingDraftId.Value))
            .Select(a => new { DraftId = a.OnboardingDraftId!.Value, a.Id, a.JobTitle })
            .ToListAsync(ct);
        var originByDraft = origins.GroupBy(o => o.DraftId).ToDictionary(g => g.Key, g => g.First());

        var makers = await _draftHireMakers.MakersAsync(tenantId, draftIds, ct);

        var decisions = await LoadDraftDecisionsAsync(tenantId, drafts, ct);
        var userIds = drafts.Where(d => d.CreatedByUserId.HasValue).Select(d => d.CreatedByUserId!.Value)
            .Concat(decisions.Values.Where(v => v.UserId.HasValue).Select(v => v.UserId!.Value))
            .Distinct().ToList();
        var names = new Dictionary<Guid, string>();
        if (userIds.Count > 0)
            names = await ScopedBypass.TenantWide(_db.Users, tenantId,
                    "Display names of the users who made or decided drafts the caller can already see, including since-removed users.")
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return drafts.Select(d =>
        {
            var isMine = actorId is not null && d.CreatedByUserId == actorId;
            var isMaker = actorId is not null && makers.TryGetValue(d.Id, out var draftMakers) && draftMakers.Contains(actorId.Value);
            var open = EmployeeDraftStatuses.IsOpen(d.Status);
            string? blocked = !open ? null
                : !canApprovePermission ? "You don't have permission to approve new hires (employees.approve)."
                : isMaker ? DraftMakerChecker.Message
                : null;
            originByDraft.TryGetValue(d.Id, out var origin);
            decisions.TryGetValue(d.Id, out var decision);
            return new EmployeeDraftListItemDto(
                d.Id, d.Status, d.CurrentStep,
                FirstNonEmpty(d.EnglishName, d.ArabicName), d.ArabicName,
                d.Department, d.Designation, d.Branch, d.JoiningDate,
                origin is null ? "Manual" : "Recruitment", origin?.Id, origin?.JobTitle,
                d.CreatedByUserId,
                d.CreatedByUserId is { } c && names.TryGetValue(c, out var creator) ? creator : null,
                isMine, open && blocked is null, blocked,
                d.ProfileCompletenessScore, d.CreatedAtUtc, d.SubmittedAtUtc,
                decision?.AtUtc ?? d.ActivatedAtUtc,
                decision?.UserId is { } u && names.TryGetValue(u, out var decider) ? decider : null,
                decision?.Reason,
                decision?.EmployeeId, decision?.EmployeeCode);
        }).ToList();
    }

    /// <summary>What approval would do with this draft's placement: the organisation records its
    /// department, designation and branch resolve to, and the legal entity they imply. With
    /// <paramref name="problems"/> null this throws the first refusal, exactly as approval always has;
    /// with a list it records every problem and carries on, for the review screen.</summary>
    private async Task<DraftPlacement> ResolveDraftPlacementAsync(
        Guid tenantId, EmployeeDraft draft, List<EmployeeDraftActivationProblem>? problems, CancellationToken ct)
    {
        Guid? deptId = null; var deptName = draft.Department;
        Guid? desigId = null; var desigTitle = draft.Designation;
        Guid? branchId = null; var branchName = draft.Branch;

        async Task ResolveOne(string key, string label, string value, Func<Task> resolve)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { await resolve(); }
            catch (InvalidOperationException ex)
            {
                if (problems is null) throw new DraftApprovalValidationException(ex.Message, ex);
                problems.Add(new EmployeeDraftActivationProblem(key, label,
                    $"'{value.Trim()}' does not match any active {label.ToLowerInvariant()} in your organisation records.",
                    $"Change the draft's {label.ToLowerInvariant()} to one from the organisation list, or add '{value.Trim()}' under Setup first."));
            }
        }

        await ResolveOne("department", "Department", draft.Department,
            async () => (deptId, deptName) = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(_db, tenantId, draft.Department, ct));
        await ResolveOne("designation", "Designation", draft.Designation,
            async () => (desigId, desigTitle) = await EmployeeOrgFieldResolver.ResolveDesignationAsync(_db, tenantId, draft.Designation, ct));
        await ResolveOne("branch", "Branch", draft.Branch,
            async () => (branchId, branchName) = await EmployeeOrgFieldResolver.ResolveBranchAsync(_db, tenantId, draft.Branch, ct));

        Guid? companyId = null;
        if (branchId.HasValue)
        {
            var resolvedCompanyId = await ScopedBypass.TenantWide(_db.Branches, tenantId,
                    "Draft placement: the legal entity of the branch the draft resolved to (register section 6).")
                .AsNoTracking()
                .Where(x => x.Id == branchId.Value && !x.IsDeleted)
                .Select(x => x.CompanyId)
                .SingleAsync(ct);
            if (resolvedCompanyId != Guid.Empty) companyId = resolvedCompanyId;
        }
        if (deptId.HasValue)
        {
            var departmentBranchId = await ScopedBypass.TenantWide(_db.Departments, tenantId,
                    "Draft placement: the branch of the department the draft resolved to (register section 6).")
                .AsNoTracking()
                .Where(x => x.Id == deptId.Value && !x.IsDeleted)
                .Select(x => x.BranchId)
                .SingleAsync(ct);
            if (departmentBranchId.HasValue)
            {
                var departmentCompanyId = await ScopedBypass.TenantWide(_db.Branches, tenantId,
                        "Draft placement: the legal entity of the department's branch (register section 6).")
                    .AsNoTracking()
                    .Where(x => x.Id == departmentBranchId.Value && !x.IsDeleted)
                    .Select(x => x.CompanyId)
                    .SingleAsync(ct);
                if (departmentCompanyId != Guid.Empty)
                {
                    if (companyId.HasValue && companyId.Value != departmentCompanyId)
                    {
                        const string conflict = "The selected department and branch belong to different legal entities.";
                        if (problems is null) throw new DraftApprovalValidationException(conflict);
                        problems.Add(new EmployeeDraftActivationProblem("legalEntity", "Legal entity", conflict,
                            "Pick a branch and a department from the same company."));
                    }
                    else companyId = departmentCompanyId;
                }
            }
        }
        // The employing company's own country, carried with the placement so the employee the draft
        // becomes can derive its jurisdiction from the legal entity it actually lands in.
        var companyCountryCode = companyId is Guid placementCompanyId
            ? await ScopedBypass.TenantWide(_db.Companies, tenantId,
                    "Draft placement: the country of the legal entity the draft resolved to, which keys every statutory requirement (register section 6).")
                .AsNoTracking()
                .Where(x => x.Id == placementCompanyId && !x.IsDeleted)
                .Select(x => x.CountryCode)
                .FirstOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;
        return new DraftPlacement(deptId, deptName, desigId, desigTitle, branchId, branchName, companyId, companyCountryCode);
    }

    private sealed record DraftPlacement(
        Guid? DepartmentId, string DepartmentName, Guid? DesignationId, string DesignationTitle,
        Guid? BranchId, string BranchName, Guid? CompanyId, string CompanyCountryCode);

    /// <summary>The employee record a draft becomes (without its code, which is allocated under the
    /// tenant lock). Shared by approval and the review screen's activation check.</summary>
    private static Employee EmployeeFromDraft(EmployeeDraft draft, Guid tenantId, DraftPlacement placement, DateTime approvedAtUtc) => new()
    {
        TenantId = tenantId,
        CompanyId = placement.CompanyId,
        FullName = FirstNonEmpty(draft.EnglishName, draft.ArabicName),
        EnglishName = draft.EnglishName,
        ArabicName = draft.ArabicName,
        PersonalEmail = draft.PersonalEmail,
        WorkEmail = draft.WorkEmail,
        Phone = draft.Phone,
        Gender = draft.Gender,
        DateOfBirth = draft.DateOfBirth,
        MaritalStatus = draft.MaritalStatus,
        EmergencyContactName = draft.EmergencyContactName,
        EmergencyContactPhone = draft.EmergencyContactPhone,
        Nationality = draft.Nationality,
        // The country the draft states wins, else the EMPLOYING company's — the one rule, the one helper.
        // A draft submitted without a country used to become an employee with a blank one, which resolves
        // an EMPTY statutory floor and walks straight through the activation gate on approval.
        CountryCode = HomeJurisdiction.DeriveEmployeeCountry(draft.CountryCode, placement.CompanyCountryCode),
        Department = placement.DepartmentName,
        DepartmentId = placement.DepartmentId,
        Designation = placement.DesignationTitle,
        DesignationId = placement.DesignationId,
        WorkLocation = draft.WorkLocation,
        Branch = placement.BranchName,
        BranchId = placement.BranchId,
        ManagerEmployeeId = draft.ManagerEmployeeId,
        Status = EmployeeStatuses.Active,
        JoiningDate = draft.JoiningDate ?? approvedAtUtc.Date,
        ContractType = draft.ContractType,
        Grade = draft.Grade,
        CostCenter = draft.CostCenter,
        ContractStartDate = draft.ContractStartDate,
        ContractEndDate = draft.ContractEndDate,
        ProbationEndDate = draft.ProbationEndDate,
        PayrollProfileCode = draft.PayrollProfileCode,
        Salary = draft.Salary,
        BankName = draft.BankName,
        BankIban = draft.BankIban,
        WpsBankDetails = draft.WpsBankDetails,
        ShiftPolicyCode = draft.ShiftPolicyCode,
        LeavePolicyCode = draft.LeavePolicyCode,
        SponsorName = draft.SponsorName,
        PassportIssueDate = draft.PassportIssueDate,
        PassportNumber = draft.PassportNumber,
        PassportExpiryDate = draft.PassportExpiryDate,
        VisaIssueDate = draft.VisaIssueDate,
        VisaNumber = draft.VisaNumber,
        VisaExpiryDate = draft.VisaExpiryDate,
        ResidencyIssueDate = draft.ResidencyIssueDate,
        WorkPermitIssueDate = draft.WorkPermitIssueDate,
        IqamaNumber = draft.IqamaNumber,
        MuqeemNumber = draft.MuqeemNumber,
        GosiReference = draft.GosiReference,
        QiwaContractNumber = draft.QiwaContractNumber,
        EmiratesId = draft.EmiratesId,
        LaborCardNumber = draft.LaborCardNumber,
        VisaFileNumber = draft.VisaFileNumber,
        Qid = draft.Qid,
        WorkPermitNumber = draft.WorkPermitNumber,
        CivilId = draft.CivilId,
        ResidencyNumber = draft.ResidencyNumber,
        ProfileCompletenessScore = draft.ProfileCompletenessScore,
    };

    /// <summary>
    /// Every reason approval would refuse this draft today, found without writing anything: the same
    /// placement resolution, the same readiness policy and evaluator, and the same work-email identity
    /// rule approval applies. The establishment budget is still checked only at approval, where it is
    /// taken under a lock (a preview of it would be stale by the time anyone acts on it).
    /// </summary>
    private async Task<EmployeeDraftActivationCheck> CheckDraftActivationAsync(Guid tenantId, EmployeeDraft draft, CancellationToken ct)
    {
        var problems = new List<EmployeeDraftActivationProblem>();
        var advisories = new List<string>();
        var placement = await ResolveDraftPlacementAsync(tenantId, draft, problems, ct);
        if (await DraftManagerRejectionAsync(tenantId, draft.ManagerEmployeeId, ct) is { } managerRejection)
            problems.Add(new EmployeeDraftActivationProblem("manager", "Manager", managerRejection.Message,
                "Pick a current employee you can see as the manager, or clear it."));

        string? companyName = null;
        if (placement.CompanyId is { } companyId)
            companyName = await ScopedBypass.TenantWide(_db.Companies, tenantId,
                    "Naming the legal entity a draft's placement resolves to, before the caller's entity check at approval.")
                .AsNoTracking()
                .Where(c => c.Id == companyId)
                .Select(c => c.LegalNameEn)
                .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(draft.WorkEmail))
        {
            var normalized = AuthService.Normalize(draft.WorkEmail);
            if (await ScopedBypass.TenantWide(_db.Users, tenantId,
                    "The same tenant-wide identity check approval runs before provisioning a login, including removed users.")
                .AsNoTracking().AnyAsync(u => u.NormalizedEmail == normalized, ct))
                problems.Add(new EmployeeDraftActivationProblem("workEmail", "Work email",
                    $"A login already uses {draft.WorkEmail.Trim()}.",
                    "Change the draft's work email, or resolve the existing login first."));
        }

        var employee = EmployeeFromDraft(draft, tenantId, placement, DateTime.UtcNow);
        var documents = await ScopedBypass.TenantWide(_db.EmployeeDocuments, tenantId,
                "A draft's documents carry no company until activation; the draft's visibility was checked by the caller.")
            .AsNoTracking()
            .Where(x => x.DraftId == draft.Id && !x.IsDeleted)
            .Select(x => new { x.DocumentType, x.ApprovalStatus, x.ExpiryDate })
            .ToListAsync(ct);
        var gateDocs = documents
            .Select(x => new DocumentPresence(x.DocumentType,
                string.Equals(x.ApprovalStatus, "Verified", StringComparison.OrdinalIgnoreCase), x.ExpiryDate))
            .ToList();
        var snapshot = EmployeeReadinessEvaluator.BuildFromEmployee(
            employee, null, gateDocs, new Dictionary<string, DateOnly?>(), (employee.Salary ?? 0m) > 0m);
        var policy = await _activationGuard.ResolvePolicyAsync(tenantId, employee.CompanyId, snapshot.CountryCode, snapshot.Nationality, ct);
        var readiness = _activationGuard.Evaluate(snapshot, policy);
        foreach (var item in readiness.Blocking)
            problems.Add(new EmployeeDraftActivationProblem(item.Key, item.Label,
                $"{item.Label} is {item.Reason}.",
                item.FixKind == "document" && !string.IsNullOrWhiteSpace(item.DocumentType)
                    ? $"Attach the {item.DocumentType} document to the draft."
                    : $"Add {item.Label.ToLowerInvariant()} to the draft."));
        advisories.AddRange(readiness.Recommended.Select(i => $"{i.Label} is {i.Reason} (recommended, not required to activate)."));
        if (draft.JoiningDate is null)
            advisories.Add("No joining date is set: activation will use the approval date.");

        return new EmployeeDraftActivationCheck(problems.Count == 0, companyName, problems, advisories);
    }

    private EmployeeDraft ApplyDraft(EmployeeDraft draft, EmployeeDraftRequest request)
    {
        draft.CurrentStep = request.CurrentStep ?? draft.CurrentStep;
        draft.EnglishName = request.EnglishName ?? draft.EnglishName;
        draft.ArabicName = request.ArabicName ?? draft.ArabicName;
        draft.PersonalEmail = request.PersonalEmail ?? draft.PersonalEmail;
        draft.WorkEmail = request.WorkEmail ?? draft.WorkEmail;
        draft.Phone = request.Phone ?? draft.Phone;
        draft.Gender = request.Gender ?? draft.Gender;
        draft.DateOfBirth = request.DateOfBirth ?? draft.DateOfBirth;
        draft.MaritalStatus = request.MaritalStatus ?? draft.MaritalStatus;
        draft.EmergencyContactName = request.EmergencyContactName ?? draft.EmergencyContactName;
        draft.EmergencyContactPhone = request.EmergencyContactPhone ?? draft.EmergencyContactPhone;
        draft.Nationality = request.Nationality ?? draft.Nationality;
        draft.CountryCode = request.CountryCode ?? draft.CountryCode;
        draft.Department = request.Department ?? draft.Department;
        draft.Designation = request.Designation ?? draft.Designation;
        draft.Branch = request.Branch ?? draft.Branch;
        draft.WorkLocation = request.WorkLocation ?? draft.WorkLocation;
        draft.ManagerEmployeeId = request.ManagerEmployeeId ?? draft.ManagerEmployeeId;
        draft.JoiningDate = request.JoiningDate ?? draft.JoiningDate;
        draft.ContractType = request.ContractType ?? draft.ContractType;
        draft.Grade = request.Grade ?? draft.Grade;
        draft.CostCenter = request.CostCenter ?? draft.CostCenter;
        draft.ContractStartDate = request.ContractStartDate ?? draft.ContractStartDate;
        draft.ContractEndDate = request.ContractEndDate ?? draft.ContractEndDate;
        draft.ProbationEndDate = request.ProbationEndDate ?? draft.ProbationEndDate;
        draft.PayrollProfileCode = request.PayrollProfileCode ?? draft.PayrollProfileCode;
        draft.Salary = request.Salary ?? draft.Salary;
        draft.BankName = request.BankName ?? draft.BankName;
        draft.BankIban = request.BankIban ?? draft.BankIban;
        draft.WpsBankDetails = request.WpsBankDetails ?? draft.WpsBankDetails;
        draft.ShiftPolicyCode = request.ShiftPolicyCode ?? draft.ShiftPolicyCode;
        draft.LeavePolicyCode = request.LeavePolicyCode ?? draft.LeavePolicyCode;
        draft.SponsorName = request.SponsorName ?? draft.SponsorName;
        draft.PassportIssueDate = request.PassportIssueDate ?? draft.PassportIssueDate;
        draft.PassportNumber = request.PassportNumber ?? draft.PassportNumber;
        draft.PassportExpiryDate = request.PassportExpiryDate ?? draft.PassportExpiryDate;
        draft.VisaIssueDate = request.VisaIssueDate ?? draft.VisaIssueDate;
        draft.VisaNumber = request.VisaNumber ?? draft.VisaNumber;
        draft.VisaExpiryDate = request.VisaExpiryDate ?? draft.VisaExpiryDate;
        draft.ResidencyIssueDate = request.ResidencyIssueDate ?? draft.ResidencyIssueDate;
        draft.WorkPermitIssueDate = request.WorkPermitIssueDate ?? draft.WorkPermitIssueDate;
        draft.IqamaNumber = request.IqamaNumber ?? draft.IqamaNumber;
        draft.MuqeemNumber = request.MuqeemNumber ?? draft.MuqeemNumber;
        draft.GosiReference = request.GosiReference ?? draft.GosiReference;
        draft.QiwaContractNumber = request.QiwaContractNumber ?? draft.QiwaContractNumber;
        draft.EmiratesId = request.EmiratesId ?? draft.EmiratesId;
        draft.LaborCardNumber = request.LaborCardNumber ?? draft.LaborCardNumber;
        draft.VisaFileNumber = request.VisaFileNumber ?? draft.VisaFileNumber;
        draft.Qid = request.Qid ?? draft.Qid;
        draft.WorkPermitNumber = request.WorkPermitNumber ?? draft.WorkPermitNumber;
        draft.CivilId = request.CivilId ?? draft.CivilId;
        draft.ResidencyNumber = request.ResidencyNumber ?? draft.ResidencyNumber;
        return draft;
    }

    private static decimal CalculateCompleteness(EmployeeDraft draft, int documentCount)
    {
        var values = new[] { draft.EnglishName, draft.Department, draft.Designation, draft.WorkEmail, draft.CountryCode, draft.Nationality, draft.MaritalStatus, draft.ContractType, draft.PayrollProfileCode, draft.ShiftPolicyCode, draft.LeavePolicyCode, draft.PassportNumber, draft.SponsorName, draft.EmergencyContactName };
        var completed = values.Count(x => !string.IsNullOrWhiteSpace(x)) + (draft.DateOfBirth.HasValue ? 1 : 0) + (draft.JoiningDate.HasValue ? 1 : 0) + (documentCount > 0 ? 2 : 0);
        return Math.Round(Math.Min(100m, completed * 100m / 18m), 1);
    }

    /// <summary>A generated code for draft approval, taken under the same ID-rule lock as the import and the form
    /// (<see cref="EmployeeIdRuleLock"/>). Runs inside the approval's own transaction.</summary>
    private async Task<string> GenerateEmployeeCode(Guid tenantId, CancellationToken cancellationToken)
    {
        var rules = await EmployeeIdRuleLock.LockAsync(_db, tenantId, cancellationToken);
        var rule = rules.FirstOrDefault(r => r.CompanyId == null) ?? rules.FirstOrDefault();
        if (rule is null)
        {
            rule = new EmployeeIdRule { TenantId = tenantId, CreatedBy = GetUserId() };
            _db.EmployeeIdRules.Add(rule);
        }

        var parts = new List<string> { rule.CompanyPrefix };
        if (rule.UseYear) parts.Add(DateTime.UtcNow.Year.ToString());
        var prefix = string.Join('-', parts.Where(x => !string.IsNullOrWhiteSpace(x))) + "-";
        string code;
        do
        {
            code = prefix + rule.NextSequence.ToString().PadLeft(rule.PaddingLength, '0');
            rule.NextSequence += 1;
        }
        while (await EmployeeIdRuleLock.CodeTakenAsync(_db, tenantId, code, cancellationToken));
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedBy = GetUserId();
        await _db.SaveChangesAsync(cancellationToken);
        return code;
    }

    /// <summary>
    /// Opens a dispenser of auto-generated employee codes for one import. Nothing is saved here: the sequence
    /// bump rides on the import's own SaveChanges, inside its transaction, so a rolled-back import also rolls
    /// the sequence back.
    /// <para>
    /// WHY NOT <see cref="GenerateEmployeeCode"/>: it ends in an unconditional SaveChanges. Calling it once per
    /// blank-code row inside the bulk-import loop flushed and COMMITTED every employee staged so far, row by row,
    /// outside any transaction — a file that failed on row 20 of 33 left 19 people behind, and the retry made 19
    /// duplicates.
    /// </para>
    /// <para>
    /// SKIPS TAKEN CODES. <paramref name="isTaken"/> covers every code already in the tenant (any company,
    /// soft-deleted rows included — the unique index covers them all) and every explicit EmployeeCode in the same
    /// file. The sequence used to be trusted blindly, so a tenant whose sequence had fallen behind its codes (a
    /// hand-typed "EMP-0002", an earlier import with explicit codes) failed the whole file on the unique index.
    /// </para>
    /// <para>
    /// SERIALIZED. The rule row is read FOR UPDATE (Postgres; the tag is inert elsewhere), so a concurrent import
    /// or hire waits for this transaction instead of reading the same NextSequence and colliding on it.
    /// </para>
    /// </summary>
    private async Task<Func<string>> OpenEmployeeCodeDispenserAsync(Guid tenantId, Func<string, bool> isTaken, CancellationToken cancellationToken)
    {
        // The same lock, on the same rows, in the same order as the form and draft approval (EmployeeIdRuleLock).
        var rules = await EmployeeIdRuleLock.LockAsync(_db, tenantId, cancellationToken);
        var rule = rules.FirstOrDefault(r => r.CompanyId == null) ?? rules.FirstOrDefault();
        if (rule is null)
        {
            rule = new EmployeeIdRule { TenantId = tenantId, CreatedBy = GetUserId() };
            _db.EmployeeIdRules.Add(rule);
        }

        var parts = new List<string> { rule.CompanyPrefix };
        if (rule.UseYear) parts.Add(DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var prefix = string.Join('-', parts.Where(x => !string.IsNullOrWhiteSpace(x))) + "-";
        var padding = rule.PaddingLength;

        string Format(int sequence) =>
            prefix + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(padding, '0');

        return () =>
        {
            // Bounded: the taken set is finite, so the sequence reaches a free code; the cap only guards a bug.
            for (var attempts = 0; attempts < 1_000_000; attempts++)
            {
                var code = Format(rule.NextSequence);
                rule.NextSequence += 1;
                if (isTaken(code)) continue;
                rule.UpdatedAtUtc = DateTime.UtcNow;
                rule.UpdatedBy = GetUserId();
                return code;
            }
            throw new InvalidOperationException("No free employee code could be generated from the tenant's ID rule.");
        };
    }

    private async Task<Guid?> CreateEmployeeUserAccount(
        Employee employee,
        string unreachablePasswordHash,
        DateTime stagedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employee.WorkEmail) || employee.TenantId is null) return null;
        var normalized = AuthService.Normalize(employee.WorkEmail);
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        var matchingIdentityIds = await _db.Users.IgnoreQueryFilters()
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == employee.TenantId && x.NormalizedEmail == normalized)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (matchingIdentityIds.Count != 0)
            throw new IdentityProvisioningConflictException(
                "A login identity already uses this work email. Resolve the identity explicitly before approving the draft.");

        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        var role = await _db.Roles.IgnoreQueryFilters().AsNoTracking()
            .TagWith(RowLockingInterceptor.ForShareTag)
            .Where(x => (x.TenantId == employee.TenantId || x.TenantId == null)
                && x.NormalizedName == "EMPLOYEE"
                && x.IsActive
                && !x.IsDeleted)
            .OrderByDescending(x => x.TenantId == employee.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new IdentityProvisioningConflictException(
                "The Employee access role is unavailable. Restore the role before approving the draft.");

        var user = new User
        {
            TenantId = employee.TenantId.Value,
            Email = employee.WorkEmail.Trim().ToLowerInvariant(),
            NormalizedEmail = normalized,
            FullName = employee.FullName,
            PasswordHash = unreachablePasswordHash,
            Status = "PendingPasswordSetup",
            AccessMode = AccessModes.NoLogin,
            IsActive = false,
            IsEmailConfirmed = false,
            MustChangePassword = false,
            CreatedAtUtc = stagedAtUtc,
            UpdatedAtUtc = stagedAtUtc
        };
        _db.Users.Add(user);
        user.UserRoles.Add(new UserRole { User = user, RoleId = role.Id });
        EnsureEmployeeCompanyGrant(user, employee, stagedAtUtc);
        user.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = employee.TenantId.Value,
            EmployeeId = employee.Id,
            User = user,
            AccessMode = AccessModes.NoLogin,
            Status = "PendingPasswordSetup",
            RequiresPasswordSetup = true,
            InvitationTokenHash = string.Empty,
            InvitationExpiresAtUtc = null,
            InvitedAtUtc = null,
            CreatedAtUtc = stagedAtUtc,
            CreatedBy = GetUserId()
        });
        return user.Id;
    }

    private void EnsureEmployeeCompanyGrant(User user, Employee employee, DateTime stagedAtUtc)
    {
        if (employee.TenantId is null || !employee.CompanyId.HasValue) return;
        if (user.EntityAccesses.Any(x =>
                x.TenantId == employee.TenantId.Value
                && x.CompanyId == employee.CompanyId.Value
                && x.GrantMode == EntityGrantModes.SelectedCompanies))
            return;
        user.EntityAccesses.Add(new UserEntityAccess
        {
            TenantId = employee.TenantId.Value,
            User = user,
            CompanyId = employee.CompanyId.Value,
            GrantMode = EntityGrantModes.SelectedCompanies,
            Role = "Employee",
            // The identity has no invitation token and cannot authenticate. Keep the legal-entity
            // grant staged as well; the explicit invitation workflow replaces it with an active
            // grant only when access is intentionally issued.
            IsActive = false,
            CreatedAtUtc = stagedAtUtc,
            CreatedBy = GetUserId(),
            GrantedBy = GetUserId(),
            GrantedAt = stagedAtUtc
        });
    }

    /// <summary>
    /// Server-authoritative work-email handling for the PATCH edit path — mirrors the create/update service
    /// so the domain "lock" is enforced at the authoritative layer, not just the UI (B3). No-op unless
    /// "workEmail" is among the applied changes. When the employing company has a domain: extract the local
    /// part and RE-ASSEMBLE on the company domain (a foreign/stale domain is coerced, never persisted) and a
    /// collision throws WorkEmailConflictException (never silently duplicate). Then the login-identity rename
    /// guard keeps a linked User in sync and blocks a rename that would collide with another login (R1). Sets
    /// the STRING only — no mailbox is provisioned.
    /// </summary>
    private async Task ApplyWorkEmailPatchAsync(Employee employee, IEnumerable<string> changedKeys, string priorWorkEmail, CancellationToken ct)
    {
        if (!changedKeys.Any(k => string.Equals(k, "workEmail", StringComparison.OrdinalIgnoreCase))) return;
        var tenantId = employee.TenantId!.Value;
        var company = employee.CompanyId is Guid cid
            ? await _db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == cid && !c.IsDeleted)
                .Select(c => new { c.EmailDomain, c.WorkEmailPattern }).FirstOrDefaultAsync(ct)
            : null;
        var domain = (company?.EmailDomain ?? string.Empty).Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var pattern = WorkEmailPatterns.Normalize(company!.WorkEmailPattern);
            var taken = await LoadTenantWorkEmailNormalizedSetAsync(tenantId, employee.Id, ct);
            bool IsTaken(string addr) => taken.Contains(AuthService.Normalize(addr));
            var resolved = WorkEmailDeriver.Resolve(employee.WorkEmail, employee.EnglishName, employee.ArabicName,
                domain, pattern, IsTaken, out var outcome, out var coercedFrom);
            if (outcome != "manual") employee.WorkEmail = resolved;
            if (outcome == "derived")
                await _audit.WriteAsync("employee.work_email_derived", "Employee", employee.Id.ToString(), Context(),
                    System.Text.Json.JsonSerializer.Serialize(new { pattern, domain, workEmail = resolved, source = "name" }), ct);
            if (coercedFrom is not null)
                await _audit.WriteAsync("employee.work_email_domain_coerced", "Employee", employee.Id.ToString(), Context(),
                    System.Text.Json.JsonSerializer.Serialize(new { provided = coercedFrom, coercedTo = resolved }), ct);
        }

        // Login-identity rename guard (same rule as the service).
        if (employee.UserAccountId is Guid uid)
        {
            var newNorm = AuthService.Normalize(employee.WorkEmail);
            var oldNorm = AuthService.Normalize(priorWorkEmail);
            if (!string.IsNullOrWhiteSpace(employee.WorkEmail) && !string.Equals(newNorm, oldNorm, StringComparison.Ordinal))
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.TenantId == tenantId, ct);
                if (user is not null && !string.Equals(user.NormalizedEmail, newNorm, StringComparison.Ordinal))
                {
                    var clash = await _db.Users.AnyAsync(u => u.TenantId == tenantId && u.Id != uid && u.NormalizedEmail == newNorm, ct);
                    if (clash)
                        throw new InvalidOperationException(
                            $"Cannot rename work email to '{employee.WorkEmail}': another login already uses that address. Resolve the conflicting account first.");
                    var oldEmail = user.Email;
                    user.Email = employee.WorkEmail.Trim().ToLowerInvariant();
                    user.NormalizedEmail = newNorm;
                    await _audit.WriteAsync("employee.work_email_renamed", "Employee", employee.Id.ToString(), Context(),
                        System.Text.Json.JsonSerializer.Serialize(new { oldEmail, newEmail = user.Email, note = "HR string + login identity synced; no mailbox provisioned." }), ct);
                }
            }
        }
    }

    /// <summary>Tenant work-email collision set keyed by the login normalization (AuthService.Normalize),
    /// company-agnostic and IgnoreQueryFilters (matches the User login boundary), excluding self.</summary>
    private async Task<HashSet<string>> LoadTenantWorkEmailNormalizedSetAsync(Guid tenantId, int? excludeEmployeeId, CancellationToken ct)
    {
        var emails = await _db.Employees.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.WorkEmail != ""
                        && (excludeEmployeeId == null || e.Id != excludeEmployeeId))
            .Select(e => e.WorkEmail)
            .ToListAsync(ct);
        return emails.Select(AuthService.Normalize).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every PATCH key <see cref="ApplyChanges"/> understands. ORDINAL, because a C# string switch is
    /// ordinal — a case-insensitive set here would admit "BankIban", which the switch would then drop.
    ///
    /// This is the allow-list <see cref="UpdateEmployee"/> validates against BEFORE mutating anything, so an
    /// unrecognised key is a 400 naming it rather than a 200 that threw the value away.
    /// `EmployeeFieldWiringTests.EditableEmployeeFields_AreAllHandledByApplyChanges` proves every key here
    /// reaches a real case, and `EveryCatalogEditKey_IsApplyable` proves the field catalogue never offers the
    /// modal an input this set does not contain.
    /// </summary>
    internal static readonly IReadOnlySet<string> EditableEmployeeFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "englishName", "arabicName", "preferredName", "gender", "nationality", "personalEmail", "workEmail",
        "phone", "jobTitle", "employmentType", "joiningDate", "department", "designation", "branch",
        "workLocation", "managerEmployeeId", "dateOfBirth", "maritalStatus", "emergencyContactName",
        "emergencyContactPhone", "contractType", "grade", "costCenter", "salary", "bankName", "bankIban",
        "wpsBankDetails", "passportNumber", "passportIssueDate", "passportExpiryDate", "visaNumber",
        "visaIssueDate", "visaExpiryDate", "iqamaNumber", "iqamaExpiryDate", "muqeemNumber", "gosiReference",
        "gosiFirstRegisteredOn",
        "emiratesId", "emiratesIdExpiryDate", "laborCardNumber", "visaFileNumber", "qid", "qidExpiryDate",
        "workPermitNumber", "workPermitIssueDate", "civilId", "civilIdExpiryDate", "residencyNumber",
        "residencyIssueDate", "idNumber", "qiwaContractNumber", "sponsorName", "terminationReason",
        // Stored on EmployeePayrollProfile, not on Employee (registry binding
        // `payrollProfile.socialInsuranceReference`). It is a fail-closed PAY gate in five GCC branches
        // — GPSSA/GRSIA/PIFSS/SPF/SIO — yet it had NO write path outside CSV import, so every
        // Bahraini-company employee and every AE/QA/KW/OM national was permanently payroll-blocked on a
        // field the readiness checklist told the user to fix "in profile". Applied by
        // EmployeeChangeApplier.ApplyPayrollProfileAsync, which every apply path runs.
        "socialInsuranceReference",
        // Payroll-profile bank columns the WPS/SIF export reads; approval-gated (SensitiveFields) and applied by
        // EmployeeChangeApplier.ApplyPayrollProfileAsync.
        "bankRoutingCode", "accountNumber",
    };

    /// <summary>
    /// Applies an edit-modal patch and RETURNS the keys it did not recognise, by delegating to the ONE
    /// shared applier — <see cref="EmployeeChangeApplier"/> — that EVERY apply path now goes through
    /// (this controller's PUT and approve, plus ApprovalWorkflowService's generic decide). The approval
    /// path used to carry a hand-copied duplicate of this switch which had already lost six keys and had
    /// no <c>default</c> arm, so an approved change could be discarded in silence.
    /// Callers MUST act on the returned list: <see cref="UpdateEmployee"/> rejects up front,
    /// <see cref="ApproveChange"/> logs (its payload was already validated when it was requested, so
    /// refusing there would strand an in-flight approval).
    /// Keys stored on the payroll profile (<see cref="EmployeeChangeApplier.PayrollProfileKeys"/>) are
    /// recognised here and written by <see cref="EmployeeChangeApplier.ApplyPayrollProfileAsync"/>, which
    /// every caller runs in the same unit of work.
    /// </summary>
    private IReadOnlyList<string> ApplyChanges(Employee employee, Dictionary<string, JsonElement> changes)
        => EmployeeChangeApplier.Apply(employee, changes);

    private async Task AddHistory(Employee employee, string eventType, DateOnly effectiveDate, CancellationToken cancellationToken)
    {
        _db.EmployeeHistories.Add(new EmployeeHistory { TenantId = employee.TenantId ?? RequireTenant(), EmployeeId = employee.Id, EventType = eventType, EffectiveDate = effectiveDate, SnapshotJson = EmployeeSafeSnapshot.Serialize(employee), CreatedByUserId = GetUserId() });
        await Task.CompletedTask;
    }

    private static bool IsValidIbanFormat(string iban)
    {
        var s = iban.Replace(" ", "").ToUpperInvariant();
        return s.Length >= 15 && s.Length <= 34
            && char.IsLetter(s[0]) && char.IsLetter(s[1])
            && char.IsDigit(s[2]) && char.IsDigit(s[3]);
    }

    /// <summary>
    /// The employee's pending change whose proposed values are exactly <paramref name="proposed"/> (same
    /// keys, same values), with its approval still pending. Null when nothing identical is waiting.
    /// </summary>
    private async Task<EmployeeChangeRequest?> FindIdenticalPendingChangeAsync(
        Guid tenantId, int employeeId, IReadOnlyDictionary<string, JsonElement> proposed, CancellationToken cancellationToken)
    {
        var pending = await _db.EmployeeChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId
                && x.Status == EmployeeChangeStatuses.PendingApproval && x.ApprovalRequestId != null)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        foreach (var change in pending)
        {
            Dictionary<string, JsonElement>? stored;
            try { stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson); }
            catch (JsonException) { continue; }
            if (stored is null || stored.Count != proposed.Count) continue;
            var storedByKey = new Dictionary<string, JsonElement>(stored, StringComparer.OrdinalIgnoreCase);
            if (!proposed.All(p => storedByKey.TryGetValue(p.Key, out var v) && SameJsonValue(v, p.Value))) continue;
            var approvalPending = await _db.ApprovalRequests.AsNoTracking()
                .AnyAsync(a => a.TenantId == tenantId && a.Id == change.ApprovalRequestId && a.Status == "Pending", cancellationToken);
            if (approvalPending) return change;
        }
        return null;
    }

    private static bool SameJsonValue(JsonElement a, JsonElement b) =>
        a.ValueKind == b.ValueKind && (a.ValueKind == JsonValueKind.String
            ? string.Equals(a.GetString()?.Trim(), b.GetString()?.Trim(), StringComparison.Ordinal)
            : a.GetRawText() == b.GetRawText());

    // Sensitive fields (salary, IBAN, identity numbers) are gated on the effective employees.sensitive permission
    // ONLY. A role NAME is never a grant: a tenant can create a custom role called "Payroll Officer" without the
    // permission, and a per-user Deny of employees.sensitive must mask an Admin too. The permission is also what
    // PrivilegedMfaPolicy counts, so a name-based grant would also have shown IBANs to a user MFA never covered.
    private bool CanEditSensitive() => User.HasPermission("employees.sensitive");
    private bool CanViewSensitive() => User.HasPermission("employees.sensitive");
    private Task Notify(string title, string message, string entity, string? entityId, CancellationToken cancellationToken) => _notifications.NotifyAsync(RequireTenant(), null, title, message, entity, entityId, cancellationToken);

    private EmployeeListItemDto ToListItem(Employee employee) => new(employee.Id, employee.EmployeeCode, employee.FullName, employee.ArabicName, employee.Department, employee.Designation, employee.Branch, employee.ManagerEmployeeId, employee.Status, employee.ProfileCompletenessScore, employee.VisaExpiryDate, employee.PassportExpiryDate, employee.ReadinessState, employee.ActivationBlockersCount, employee.PublicId);
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Unnamed Employee";
    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant claim missing."));
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), RequireTenant());
    private async Task<bool> CanAccessEmployeeAsync(int employeeId, CancellationToken cancellationToken)
    {
        var scope = await _scopeService.ResolveAsync(User, RequireTenant(), cancellationToken);
        return scope.CanAccessEmployee(employeeId);
    }
    private Task Audit(string action, string entity, string? entityId, CancellationToken cancellationToken) => _audit.WriteAsync(action, entity, entityId, new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), RequireTenant()), null, cancellationToken);
}

public record EmployeeListItemDto(int Id, string EmployeeCode, string FullName, string ArabicName, string Department, string Designation, string Branch, int? ManagerEmployeeId, string Status, decimal ProfileCompletenessScore, DateOnly? VisaExpiryDate, DateOnly? PassportExpiryDate, string ReadinessState, int ActivationBlockersCount, Guid PublicId = default);

// ── Duplicate-person detection DTOs (contract mirrors frontend api/employees.ts) ──────────────────
public record DuplicateIdentityValueDto(string? FieldKey, string? Value);

public record DuplicateCheckRequest(
    string? EnglishName,
    string? ArabicName,
    DateOnly? DateOfBirth,
    string? Nationality,
    Guid? CompanyId,
    IReadOnlyList<DuplicateIdentityValueDto>? IdentityValues,
    int? ExcludeEmployeeId);

/// <summary>Scope-masked match: when CanView is false (cross-company), code/name/branch/companyId are
/// stripped and only the tier + a no-PII message survive.</summary>
public record DuplicateMatchDto(
    int EmployeeId, string EmployeeCode, string FullName, string? Branch, string? CompanyId,
    string Status, string MatchType, IReadOnlyList<string> Signals, bool CanView);

public record DuplicateCheckResponse(bool HasStrong, bool HasProbable, IReadOnlyList<DuplicateMatchDto> Matches);

/// <summary>Modal work-email preview request. LocalPart (optional) is what the user typed in the editable
/// local-part field; when blank the server derives from the name. CompanyId is the EMPLOYING company whose
/// domain is used (multi-company req 5). ExcludeEmployeeId skips self on edit.</summary>
public record DeriveWorkEmailRequest(
    string? EnglishName, string? ArabicName, Guid? CompanyId, string? LocalPart, int? ExcludeEmployeeId);

public record DeriveWorkEmailResponse(
    string Domain, string Pattern, string LocalPart, string WorkEmail, bool Unique, string? Suggestion, string Status);

public record ResolveDuplicateRequest(string Resolution, int? IntoEmployeeId, string? Reason);
public record ConfirmBankDetailsRequest(string? Note);

/// <summary>Read-only Ex-Employees archive row. Directory + lifecycle metadata only — no salary,
/// bank, or statutory-identity fields (parity with the People list's non-sensitive projection).</summary>
public record ExEmployeeListItemDto(
    int Id, string EmployeeCode, string FullName, string ArabicName,
    string Department, string Designation, string Branch,
    string LastStatus, bool IsDeleted,
    DateTime? ExitDate, DateTime? RetentionUntilUtc, string PrivacyStatus);

/// <summary>
/// Former-employee status vocabulary for the Ex-Employees archive + exit cascade.
/// Declared as <c>string[]</c> (NOT <c>IReadOnlySet&lt;string&gt;</c>) on purpose: EF Core translates
/// <c>string[].Contains(x)</c> to a SQL <c>= ANY(...)</c>, whereas <c>IReadOnlySet.Contains</c> binds
/// to the interface method and is NOT translatable on Npgsql (it would throw at runtime and, because
/// the test suite runs EF InMemory, would pass tests yet crash production).
/// </summary>
public static class ExitEmployeeStatuses
{
    /// <summary>Terminal statuses that make an employee a FORMER employee: archive membership +
    /// exclusion from the active People list. Offboarded (serving notice) is included for the archive
    /// view, but is deliberately NOT a payroll-deactivation trigger.</summary>
    public static readonly string[] Exit =
    {
        EmployeeStatuses.Archived, EmployeeStatuses.Offboarded, EmployeeStatuses.Terminated, EmployeeStatuses.Exited
    };

    /// <summary>Statuses that, when reached via a direct status change, deactivate the WPS footprint.
    /// Excludes Offboarded — notice-period staff may still be paid and a rescind must stay clean.</summary>
    public static readonly string[] PayrollDeactivation =
    {
        EmployeeStatuses.Archived, EmployeeStatuses.Terminated, EmployeeStatuses.Exited
    };
}
internal sealed class IdentityProvisioningConflictException : InvalidOperationException
{
    public IdentityProvisioningConflictException(string message) : base(message) { }
}
internal sealed class DraftApprovalValidationException : InvalidOperationException
{
    public DraftApprovalValidationException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
internal sealed class DraftApprovalForbiddenException : InvalidOperationException { }
internal sealed class DraftApprovalMakerCheckerException : InvalidOperationException { }
internal sealed class DraftApprovalNotReadyException : InvalidOperationException
{
    public DraftApprovalNotReadyException(string status) => Status = status;
    public string Status { get; }
}
internal sealed class DraftApprovalNotFoundException : InvalidOperationException { }
public record EmployeeDocumentRequest(string DocumentType, string FileName, string ContentType, string StorageUrl, bool IsRequired, DateOnly? ExpiryDate);
public record EmployeeTransferRequestDto(string NewDepartment, string NewBranch, int? NewManagerEmployeeId, DateOnly EffectiveDate);
public record EmployeeUpdateRequest(DateOnly EffectiveDate, Dictionary<string, JsonElement> Changes);
public record GroupCountDto(string Name, int Count);
public record EmployeeReportsDto(int TotalHeadcount, int ActiveEmployees, int NewJoiners, int Exits, int ProbationEmployees, IReadOnlyCollection<GroupCountDto> DepartmentHeadcount, IReadOnlyCollection<GroupCountDto> BranchHeadcount, IReadOnlyCollection<GroupCountDto> NationalityMix, IReadOnlyCollection<GroupCountDto> GenderMix, int ContractExpiringSoon, int VisaOrPassportExpiringSoon, int MissingDocumentsOrIncompleteProfiles);
public record EmployeeAiResponseDto(string Answer, IReadOnlyCollection<EmployeeListItemDto> Employees);
public record EmployeeDraftRequest(string? CurrentStep, string? EnglishName, string? ArabicName, string? PersonalEmail, string? WorkEmail, string? Phone, string? Gender, DateOnly? DateOfBirth, string? MaritalStatus, string? EmergencyContactName, string? EmergencyContactPhone, string? Nationality, string? CountryCode, string? Department, string? Designation, string? Branch, string? WorkLocation, int? ManagerEmployeeId, DateTime? JoiningDate, string? ContractType, string? Grade, string? CostCenter, DateOnly? ContractStartDate, DateOnly? ContractEndDate, DateOnly? ProbationEndDate, string? PayrollProfileCode, decimal? Salary, string? BankName, string? BankIban, string? WpsBankDetails, string? ShiftPolicyCode, string? LeavePolicyCode, string? SponsorName, DateOnly? PassportIssueDate, string? PassportNumber, DateOnly? PassportExpiryDate, DateOnly? VisaIssueDate, string? VisaNumber, DateOnly? VisaExpiryDate, string? IqamaNumber, string? MuqeemNumber, string? GosiReference, string? QiwaContractNumber, string? EmiratesId, string? LaborCardNumber, string? VisaFileNumber, string? Qid, string? WorkPermitNumber, DateOnly? WorkPermitIssueDate, string? CivilId, string? ResidencyNumber, DateOnly? ResidencyIssueDate);
public record EmployeeDocumentUploadRequest(string DocumentType, bool IsRequired, DateOnly? ExpiryDate, IFormFile File);
public record EmployeeDocumentUploadForm(
    [Required, MaxLength(80)] string DocumentType,
    [MaxLength(80)] string? DocumentCategory,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    DateOnly? RenewalReminderDate,
    bool IsRequired,
    [MaxLength(40)] string? ApprovalStatus,
    [MaxLength(1000)] string? Notes,
    [Required] IFormFile File);
public record EmployeeTemplateDto(string TemplateType, string Language, string Title, string Body);
