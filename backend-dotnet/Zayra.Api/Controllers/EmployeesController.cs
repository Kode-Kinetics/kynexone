using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

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
        "gosiReference", "qiwaContractNumber", "emiratesId", "laborCardNumber", "visaFileNumber", "qid", "civilId", "residencyNumber",
        "residencyIssueDate", "workPermitNumber", "workPermitIssueDate", "medicalInformation", "disciplinaryRecords",
        "terminationReason"
    };

    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditService _audit;
    private readonly IDocumentStorage _documents;
    private readonly INotificationService _notifications;
    private readonly IHijriDateService _hijri;
    private readonly IDataScopeService _scopeService;
    private readonly ILetterService _letters;
    private readonly IApprovalWorkflowService _approvalWorkflow;
    private readonly ILogger<EmployeesController>? _logger;
    private readonly IEstablishmentGuard _establishmentGuard;
    private readonly IEmployeeActivationGuard _activationGuard;
    private readonly IEmployeeDuplicateDetector _duplicateDetector;

    public EmployeesController(ZayraDbContext db, IPasswordHasher passwordHasher, IAuditService audit, IDocumentStorage documents, INotificationService notifications, IHijriDateService hijri, IDataScopeService scopeService, ILetterService letters, IApprovalWorkflowService? approvalWorkflow = null, ILogger<EmployeesController>? logger = null, IEstablishmentGuard? establishmentGuard = null, IEmployeeActivationGuard? activationGuard = null, IEmployeeDuplicateDetector? duplicateDetector = null)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _documents = documents;
        _notifications = notifications;
        _hijri = hijri;
        _scopeService = scopeService;
        _letters = letters;
        _approvalWorkflow = approvalWorkflow ?? new Zayra.Api.Infrastructure.Approvals.ApprovalWorkflowService(db, audit);
        _logger = logger;
        // Optional with concrete fallback (same pattern as _approvalWorkflow): DI supplies the
        // registered guard in production; direct constructions in tests keep compiling AND enforcing.
        _establishmentGuard = establishmentGuard ?? new EstablishmentGuardService(db);
        _activationGuard = activationGuard ?? new EmployeeActivationGuard(db);
        _duplicateDetector = duplicateDetector ?? new EmployeeDuplicateDetector(db);
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<EmployeeListItemDto>>> Search([FromServices] IEmployeeManagementService employeeManagement, [FromQuery] string? search, [FromQuery] string? status, [FromQuery] string? department, [FromQuery] string? readiness = null, [FromQuery] Guid? importBatchId = null, [FromQuery] string? gapType = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
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
        var items = await query.OrderBy(e => e.FullName).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new EmployeeListItemDto(e.Id, e.EmployeeCode, e.FullName, e.ArabicName ?? string.Empty, e.Department ?? string.Empty, e.Designation ?? string.Empty, e.Branch ?? string.Empty, e.ManagerEmployeeId, e.Status, e.ProfileCompletenessScore, e.VisaExpiryDate, e.PassportExpiryDate, e.ReadinessState, e.ActivationBlockersCount))
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

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Auditor")]
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
        var csv = await BuildEmployeesCsvAsync(emps, tenantId, ct);
        // Export audit: actor, row count, and company-scope dimension — no PII values.
        await _audit.WriteAsync("employees.exported", "Employee", "bulk", Context(),
            JsonSerializer.Serialize(new
            {
                rowCount = emps.Count,
                groupScope = entityScope.IsGroupLevel,
                companyIds = entityScope.IsGroupLevel ? null : entityScope.AccessibleCompanyIds,
                exportType = "employees_csv",
            }), ct);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"employees_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Builds the employee CSV (registry-ordered columns + payroll/salary/compliance joins) for a resolved
    /// set of employees. Extracted from <see cref="Export"/> so the People-list export and the bulk
    /// "export selected" path emit byte-identical output from ONE builder — a column can never drift between
    /// the two surfaces. Loads only the given rows' related data (never an unfiltered tenant scan).
    /// </summary>
    private async Task<string> BuildEmployeesCsvAsync(IReadOnlyList<Employee> emps, Guid tenantId, CancellationToken ct)
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
        var headers = EmployeeCsvHeaders;
        var rows = emps.Select(e =>
        {
            profiles.TryGetValue(e.Id, out var profile);
            salaries.TryGetValue(e.Id, out var salary);
            var structureCode = salary is not null && structures.TryGetValue(salary.SalaryStructureId, out var structure) ? structure.Code : string.Empty;
            var positionCode = e.PositionId is not null && positionCodes.TryGetValue(e.PositionId.Value, out var pc) ? pc : string.Empty;
            // Value map keyed by CSV header — the row is projected in registry order below, so a header
            // added/reordered in the catalog can never misalign the export (replaces the old positional
            // object[] that had to be kept in lock-step with the header array by hand).
            static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? string.Empty;
            var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EmployeeCode"] = e.EmployeeCode,
                ["CompanyLegalName"] = string.Empty,
                ["BranchCode"] = string.Empty,
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
                ["DepartmentCode"] = string.Empty,
                ["Designation"] = e.Designation,
                ["JobTitle"] = e.JobTitle,
                ["EmploymentType"] = e.EmploymentType,
                ["ContractType"] = e.ContractType,
                ["Grade"] = e.Grade,
                ["PositionCode"] = positionCode,
                ["ManagerEmployeeCode"] = string.Empty,
                ["SupervisorEmployeeCode"] = string.Empty,
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
            return (IReadOnlyList<object?>)headers.Select(h => v.GetValueOrDefault(h)).ToList();
        });
        return Csv.Build(headers, rows);
    }

    /// <summary>Downloadable blank template — the shareable "data format" to fill and import.</summary>
    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(EmployeeCsvHeaders)), "text/csv", "employees_import_template.csv");

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
        var iso = (countryCode ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(iso) && companyId is Guid cid)
            iso = await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == cid)
                .Select(c => c.CountryCode).FirstOrDefaultAsync(ct) ?? string.Empty;
        var iso2 = (Zayra.Api.Application.Common.CountryCodeStandard.NormalizeToIso2(iso) ?? iso).Trim().ToUpperInvariant();

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
                    entityKey = d.ComplianceFieldKey is null ? null : CamelKey(d.Key),
                    expiryEntityKey = d.ExpiryKey is null ? null : CamelKey(d.ExpiryKey),
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

    [HttpPost("import")]
    [HasPermission("employees.bulk_import")]
    public async Task<IActionResult> Import([FromBody] ImportEmployeesRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);

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
        // THE LAW: a row is dropped ONLY for (a) no name or (b) a duplicate EmployeeCode. Split the two
        // lawful reasons so the summary can prove no other drop happened (accept-never-block assertion).
        int skippedNoName = 0, skippedDupCode = 0;
        var errors = new List<string>();
        // Non-fatal notices: the row IS imported, but an optional reference could not be resolved.
        var warnings = new List<string>();
        var rowNum = 1;
        // Per-created-row org-skeleton/payroll gaps (typed), keyed by the row's FINAL employee code
        // (auto-generated included) — persisted as EmployeeImportGap after Id assignment; Pass 2 appends
        // link:manager/supervisor gaps to the same map before persistence.
        var gapsByCode = new Dictionary<string, List<ImportGap>>(StringComparer.OrdinalIgnoreCase);
        var rowNumByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Rows whose salary is HELD (no valid grade): Pass 1b must skip the salary-structure insert.
        var heldSalaryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Final advisory re-stamp inputs: base readiness per created row (merged with all gaps at the end).
        var createdRowMeta = new List<(Employee Emp, EmployeeReadiness Readiness, bool HasPolicy, string FinalCode)>();

        // ── Readiness (§7): import stays name-only lenient but NEVER silently lands Active. Policy is
        // resolved once per (company, country, nationality) and cached; each row is evaluated with the
        // pure Evaluate primitive (never aborts the file). Blank status ⇒ Draft; explicit Active with
        // activate-blockers ⇒ downgraded to Draft + warning (row still created).
        var readinessPolicyCache = new Dictionary<string, ResolvedReadinessPolicy>();
        var importBatchId = Guid.NewGuid();

        async Task<(string Landing, EmployeeReadiness Readiness, bool HasPolicy)> ResolveRowLandingAsync(
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
            string landing = string.IsNullOrWhiteSpace(csvStatus)
                ? EmployeeStatuses.Draft                                   // blank ⇒ Draft, never Active
                : string.Equals(csvStatus, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)
                    ? (readiness.IsBlocked ? EmployeeStatuses.Draft : EmployeeStatuses.Active)  // Active + blockers ⇒ Draft
                    : csvStatus;                                           // any other status honoured as-is
            return (landing, readiness, policy.Items.Count > 0);
        }
        // Track employee codes created in this batch for Pass 2 resolution
        var batchCodes = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
        var batchPayroll = new Dictionary<string, (Employee emp, Dictionary<string, string> rowData)>(StringComparer.OrdinalIgnoreCase);
        var claimedPositionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Existing employee codes (case-insensitive), loaded once so the existing-DB dup check uses the SAME
        // case-folding as the in-file batch dedup (batchCodes, OrdinalIgnoreCase) and as ImportPreview — a code
        // differing only in case from an existing one is a duplicate in BOTH dry-run and commit (previously
        // commit's DB check was case-sensitive and would create it, diverging from preview). The unique
        // (TenantId, EmployeeCode) index still backstops any cross-scope edge as a legible 422.
        var existingCodes = new HashSet<string>(
            await _db.Employees.Where(e => e.TenantId == tenantId).Select(e => e.EmployeeCode).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

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

        // Persist pending changes; convert a constraint violation (e.g. a duplicate
        // employee code slipping through) into a legible 422 rather than a raw 500.
        // The raw exception detail is logged server-side only — it is never returned
        // to the client, to avoid leaking schema/constraint internals.
        async Task<IActionResult?> PersistAsync()
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                return null;
            }
            catch (DbUpdateException ex)
            {
                _logger?.LogError(ex, "Employee CSV import failed to persist for tenant {TenantId}.", tenantId);
                return UnprocessableEntity(new
                {
                    error = "import_persist_failed",
                    message = "Import could not be saved because a database constraint was violated. "
                              + "This can happen when an employee code is duplicated — please review the file and retry."
                });
            }
        }

        // ── Pass 1: create all employee records ──────────────────────────────────
        foreach (var row in rows)
        {
            rowNum++;
            var name = row.GetValueOrDefault("FullName", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) { skipped++; skippedNoName++; errors.Add($"Row {rowNum}: missing FullName; row skipped."); continue; }
            var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(code))
            {
                // A second row in this same file with an already-added code would both pass the
                // DB check and violate the unique (TenantId, EmployeeCode) index at SaveChanges.
                if (batchCodes.ContainsKey(code))
                { skipped++; skippedDupCode++; errors.Add($"Row {rowNum}: EmployeeCode '{code}' is duplicated within the import file; row skipped."); continue; }
                if (existingCodes.Contains(code))
                { skipped++; skippedDupCode++; errors.Add($"Row {rowNum}: EmployeeCode '{code}' already exists."); continue; }
            }

            DateTime.TryParse(row.GetValueOrDefault("JoiningDate", string.Empty), out var jdRaw);
            var jd = DateTime.SpecifyKind(jdRaw == default ? DateTime.UtcNow : jdRaw, DateTimeKind.Utc);
            var statusVal = row.GetValueOrDefault("Status", string.Empty).Trim();
            var deptNameRaw = row.GetValueOrDefault("Department", string.Empty).Trim();
            var desigTitleRaw = row.GetValueOrDefault("Designation", string.Empty).Trim();

            // ── ACCEPT-NEVER-BLOCK resolution (SHARED with ImportPreview) ─────────────────────────
            // The single source of truth for every org/grade/position/salary decision. It NEVER drops:
            // unknown company → default (or null); unknown grade → null; ineligible designation → dropped
            // designation link; bad/occupied/ineligible position → null; salary w/o grade → HELD; salary
            // out of band → REVIEW. Each failure is a typed gap + a warning; the person still imports.
            var resolved = EmployeeImportRowResolver.ResolveRow(row, lookups, claimedPositionCodes);
            var resolvedDeptId = resolved.DepartmentId;
            var resolvedDesigId = resolved.DesignationId;
            var finalGradeId = resolved.GradeId;
            var finalGradeCode = resolved.FinalGradeCode;
            var grossSalary = resolved.GrossSalary;
            foreach (var w in resolved.Warnings) warnings.Add($"Row {rowNum}: {w}");

            // ── Readiness landing decision (§7): blank ⇒ Draft; Active + activate-blockers ⇒ Draft + warning ──
            var (rowStatus, rowReadiness, rowHasPolicy) = await ResolveRowLandingAsync(
                row, resolved.CompanyId, resolvedDeptId, resolvedDesigId, jd, statusVal);
            if (!string.IsNullOrWhiteSpace(statusVal)
                && string.Equals(statusVal, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)
                && rowStatus == EmployeeStatuses.Draft)
                warnings.Add($"Row {rowNum}: {name} imported as Draft — cannot be Active until: "
                             + $"{string.Join(", ", rowReadiness.Blocking.Select(b => b.Label))}. Fix in the People list.");

            // ── Active-seat budget (P1-4) ─────────────────────────────────────────────
            // MaxEmployees caps ACTIVE seats. A complete row that would land Active but has no seat left is
            // imported as Draft (inactive) instead of rejecting the whole file — Draft rows consume no seat.
            if (rowStatus == EmployeeStatuses.Active)
            {
                if (activeSeatsConsumed < activeSeatsBudget) activeSeatsConsumed++;
                else
                {
                    rowStatus = EmployeeStatuses.Draft;
                    warnings.Add($"Row {rowNum}: {name} imported as Draft — active seat limit reached ({sub?.MaxEmployees}); activate once an active seat is available.");
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
                    warnings.Add($"Row {rowNum}: {estDecision.Detail}");
                }
                else
                {
                    rowStatus = EmployeeStatuses.Draft;
                    warnings.Add($"Row {rowNum}: {name} imported as Draft — {estDecision.Detail} Assign within budget to activate.");
                }
            }

            var finalCode = string.IsNullOrWhiteSpace(code) ? await GenerateEmployeeCode(tenantId, ct) : code;

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
                        warnings.Add($"Row {rowNum}: Work email '{workEmail}' is already in use — imported as-is and flagged.");
                    }
                    else claimedEmailNorm.Add(norm);
                }
            }

            var employee = new Employee
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
                DateOfBirth = ReadCsvDate(row, "DateOfBirth"),
                Nationality = row.GetValueOrDefault("Nationality", string.Empty),
                MaritalStatus = row.GetValueOrDefault("MaritalStatus", string.Empty),
                CountryCode = row.GetValueOrDefault("CountryCode", string.Empty).Trim().ToUpperInvariant(),
                Department = deptNameRaw,
                DepartmentId = resolvedDeptId,
                Designation = desigTitleRaw,
                DesignationId = resolvedDesigId,
                GradeId = finalGradeId,
                PositionId = resolved.PositionId,
                Grade = finalGradeCode,
                JobTitle = row.GetValueOrDefault("JobTitle", desigTitleRaw),
                EmploymentType = row.GetValueOrDefault("EmploymentType", "Full-time"),
                ContractType = row.GetValueOrDefault("ContractType", string.Empty),
                Status = rowStatus,
                ReadinessState = rowReadiness.State,
                ActivationBlockersCount = rowReadiness.Blocking.Count,
                ReadinessEvaluatedAtUtc = DateTime.UtcNow,
                ProfileCompletenessScore = rowHasPolicy ? rowReadiness.Score : 0m,
                JoiningDate = jd == default ? DateTime.UtcNow : jd,
                ConfirmationDate = ReadCsvDate(row, "ConfirmationDate"),
                ProbationStartDate = ReadCsvDate(row, "ProbationStartDate"),
                ProbationEndDate = ReadCsvDate(row, "ProbationEndDate"),
                NoticePeriodDays = ReadCsvInt(row, "NoticePeriodDays"),
                Branch = resolved.BranchNameEn,
                CostCenter = resolved.CostCenterCode,
                WorkLocation = row.GetValueOrDefault("WorkLocation", string.Empty).Trim(),
                ShiftPolicyCode = row.GetValueOrDefault("ShiftPolicyCode", string.Empty).Trim(),
                LeavePolicyCode = row.GetValueOrDefault("LeavePolicyCode", string.Empty).Trim(),
                AttendancePolicyCode = row.GetValueOrDefault("AttendancePolicyCode", string.Empty).Trim(),
                PassportNumber = row.GetValueOrDefault("PassportNumber", string.Empty).Trim(),
                PassportIssueDate = ReadCsvDate(row, "PassportIssueDate"),
                PassportExpiryDate = ReadCsvDate(row, "PassportExpiryDate"),
                VisaNumber = row.GetValueOrDefault("VisaNumber", string.Empty).Trim(),
                VisaIssueDate = ReadCsvDate(row, "VisaIssueDate"),
                VisaExpiryDate = ReadCsvDate(row, "VisaExpiryDate"),
                IqamaNumber = row.GetValueOrDefault("IqamaNumber", string.Empty).Trim(),
                MuqeemNumber = row.GetValueOrDefault("MuqeemNumber", string.Empty).Trim(),
                GosiReference = row.GetValueOrDefault("GosiReference", string.Empty).Trim(),
                EmiratesId = row.GetValueOrDefault("EmiratesId", string.Empty).Trim(),
                LaborCardNumber = row.GetValueOrDefault("LaborCardNumber", string.Empty).Trim(),
                VisaFileNumber = row.GetValueOrDefault("VisaFileNumber", string.Empty).Trim(),
                Qid = row.GetValueOrDefault("Qid", string.Empty).Trim(),
                CivilId = row.GetValueOrDefault("CivilId", string.Empty).Trim(),
                ResidencyNumber = row.GetValueOrDefault("ResidencyNumber", string.Empty).Trim(),
                ResidencyIssueDate = ReadCsvDate(row, "ResidencyIssueDate"),
                WorkPermitNumber = row.GetValueOrDefault("WorkPermitNumber", string.Empty).Trim(),
                WorkPermitIssueDate = ReadCsvDate(row, "WorkPermitIssueDate"),
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
                ContractStartDate = ReadCsvDate(row, "ContractStartDate"),
                ContractEndDate = ReadCsvDate(row, "ContractEndDate"),
                IqamaExpiryDate = ReadCsvDate(row, "IqamaExpiry"),
                EmiratesIdExpiryDate = ReadCsvDate(row, "EmiratesIdExpiry"),
                QidExpiryDate = ReadCsvDate(row, "QidExpiry"),
                CivilIdExpiryDate = ReadCsvDate(row, "CivilIdExpiry"),
                QiwaContractNumber = row.GetValueOrDefault("QiwaContractNumber", string.Empty).Trim(),
            };
            _db.Employees.Add(employee);
            batchCodes[finalCode] = employee;
            batchPayroll[finalCode] = (employee, row);
            // Track this row's typed gaps against its FINAL code (auto-generated included) for persistence,
            // the summary, and the advisory readiness re-stamp; Pass 2 appends link:manager/supervisor gaps.
            gapsByCode[finalCode] = new List<ImportGap>(resolved.Gaps);
            rowNumByCode[finalCode] = rowNum;
            if (resolved.SalaryDecision == ImportSalaryDecision.Hold) heldSalaryCodes.Add(finalCode);
            createdRowMeta.Add((employee, rowReadiness, rowHasPolicy, finalCode));
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
                    warnings.Add($"Row {rowNum}: {detail}");
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
                    warnings.Add($"Row {earlierRow}: {bDetail}");
                }
            }
            dupMatcher.Register(dupProbe);
        }
        // First persist: when level slots were claimed and enforcement is on, serialize with the
        // same per-cell advisory locks the single-hire paths use and RE-VERIFY each claimed cell
        // against a fresh count inside the transaction — a concurrent import/hire racing for the
        // last slot loses with the structured 409 instead of silently overshooting (AC7).
        if (_db.Database.IsRelational() && claimedLevelSlots.Count > 0
            && establishmentMode == EstablishmentGuardService.ModeEnforced)
        {
            IActionResult? raceLoss = null;
            var strategy = _db.Database.CreateExecutionStrategy();
            var persistError = await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
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
                        raceLoss = this.EstablishmentConflict(new EstablishmentBudgetExceededException(
                            new EstablishmentBlock(deptId, deptName, levelId, names.Code, names.NameEn, names.NameAr,
                                cellBudget, freshCurrent, claimed, 0)));
                        return null;
                    }
                }
                var innerError = await PersistAsync();
                if (innerError is null) await tx.CommitAsync(ct);
                return innerError;
            });
            if (raceLoss is not null) { _db.ChangeTracker.Clear(); return raceLoss; }
            if (persistError is not null) return persistError;
        }
        else if (await PersistAsync() is { } saveError) return saveError;
        // Blocked rows are audited AFTER the successful persist (an audit write mid-loop would
        // flush partially-built rows). Every path logs identically — this is the demand signal.
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
        var importedPositionAssignments = batchCodes.Values.Where(e => e.PositionId is not null).ToList();
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
            if (await PersistAsync() is { } positionSaveError) return positionSaveError;
        }

        // ── Pass 1b: payroll profiles + salary structures ────────────────────────
        int payrollProfilesCreated = 0;
        foreach (var (payrollCode, (emp, rowData)) in batchPayroll)
        {
            var ibanRaw = rowData.GetValueOrDefault("IBAN", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ibanRaw) && !Zayra.Api.Infrastructure.Payroll.IbanValidator.IsValid(ibanRaw))
                warnings.Add($"Employee {emp.EmployeeCode}: IBAN '{ibanRaw}' fails the ISO 13616 mod-97 checksum — imported, but it must be corrected before this employee can be included in a payroll run.");
            var bankNameRaw = rowData.GetValueOrDefault("BankName", string.Empty).Trim();
            var molIdRaw = rowData.GetValueOrDefault("MolId", string.Empty).Trim();
            var accountRaw = rowData.GetValueOrDefault("AccountNumber", string.Empty).Trim();
            var routingRaw = rowData.GetValueOrDefault("BankRoutingCode", string.Empty).Trim();
            var payrollGroupRaw = rowData.GetValueOrDefault("PayrollGroup", string.Empty).Trim();
            var paymentMethodRaw = rowData.GetValueOrDefault("PaymentMethod", string.Empty).Trim();
            var socialInsuranceRaw = rowData.GetValueOrDefault("SocialInsuranceReference", string.Empty).Trim();
            var structureCodeRaw = rowData.GetValueOrDefault("SalaryStructureCode", string.Empty).Trim();
            var currencyRaw = rowData.GetValueOrDefault("Currency", string.Empty).Trim();
            var tenantCurrency = await _db.ResolveTenantCurrencyAsync(tenantId, ct);
            var currency = string.IsNullOrWhiteSpace(currencyRaw)
                ? defaultCompany is null && string.Equals(tenantCurrency, "USD", StringComparison.OrdinalIgnoreCase) ? "SAR" : tenantCurrency
                : currencyRaw.ToUpperInvariant();
            _ = decimal.TryParse(rowData.GetValueOrDefault("BasicSalary", string.Empty), out var basicSalary);
            _ = decimal.TryParse(rowData.GetValueOrDefault("HousingAllowance", string.Empty), out var housing);
            _ = decimal.TryParse(rowData.GetValueOrDefault("TransportAllowance", string.Empty), out var transport);
            _ = decimal.TryParse(rowData.GetValueOrDefault("FoodAllowance", string.Empty), out var food);
            _ = decimal.TryParse(rowData.GetValueOrDefault("MobileAllowance", string.Empty), out var mobile);
            _ = decimal.TryParse(rowData.GetValueOrDefault("OtherAllowance", string.Empty), out var other);
            _ = decimal.TryParse(rowData.GetValueOrDefault("FixedDeduction", string.Empty), out var fixedDeduction);
            var gross = basicSalary + housing + transport + food + mobile + other;

            var grade = emp.GradeId is not null ? lookups.GradeById.GetValueOrDefault(emp.GradeId.Value) : null;

            bool hasPayroll = !string.IsNullOrEmpty(ibanRaw) || !string.IsNullOrEmpty(bankNameRaw) ||
                              !string.IsNullOrEmpty(molIdRaw) || !string.IsNullOrEmpty(socialInsuranceRaw) || gross > 0;
            if (!hasPayroll) continue;

            _db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = emp.Id,
                BankName = bankNameRaw, Iban = ibanRaw, SalaryCurrency = currency,
                AccountNumber = accountRaw, BankRoutingCode = routingRaw,
                PaymentMethod = string.IsNullOrWhiteSpace(paymentMethodRaw) ? "BankTransfer" : paymentMethodRaw,
                PayrollGroup = payrollGroupRaw, SalaryStructureReference = structureCodeRaw,
                SocialInsuranceReference = socialInsuranceRaw,
                MolId = molIdRaw, WpsEligible = true, EosbEligible = true, CreatedBy = GetUserId()
            });

            // Salary HELD (no valid grade): person is imported, but the salary structure is withheld until a
            // grade is assigned (pay:salaryHeld gap already recorded). Everything else on the profile stands.
            if (gross > 0 && !heldSalaryCodes.Contains(payrollCode))
            {
                var structure = await ResolveImportSalaryStructureAsync(tenantId, emp.CompanyId, grade, structureCodeRaw, currency, ct);
                _db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
                {
                    TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
                    BasicSalary = basicSalary, HousingAllowance = housing, TransportAllowance = transport,
                    FoodAllowance = food, MobileAllowance = mobile, OtherAllowance = other,
                    FixedDeduction = fixedDeduction, Currency = currency,
                    EffectiveDate = DateOnly.FromDateTime(emp.JoiningDate), IsActive = true, CreatedBy = GetUserId()
                });
                emp.Salary = gross;
                if (!string.IsNullOrEmpty(bankNameRaw)) emp.BankName = bankNameRaw;
                if (!string.IsNullOrEmpty(ibanRaw)) emp.BankIban = ibanRaw;
                if (!string.IsNullOrWhiteSpace(payrollGroupRaw)) emp.PayrollProfileCode = payrollGroupRaw;
                _db.Employees.Update(emp);
            }
            payrollProfilesCreated++;
        }
        if (payrollProfilesCreated > 0 && await PersistAsync() is { } payrollSaveError)
            return payrollSaveError;

        // ── Pass 2: resolve manager/supervisor refs → IDs (CODE first, EMAIL fallback) ────────────
        // Iterates the rows CREATED in Pass 1 keyed by their FINAL employee code (auto-generated included),
        // so an auto-coded row now links its manager too. Manager/supervisor NOT found is a WARNING + a
        // link:* gap — NEVER an error, and never a row drop (the person already imported in Pass 1).
        int hierarchyLinked = 0;
        int managersUnresolved = 0;
        var hierarchyWarnings = new List<string>();
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
            var rn = rowNumByCode.GetValueOrDefault(finalCode, 0);
            var mgrCode = row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim();
            var mgrEmail = row.GetValueOrDefault("ManagerEmail", string.Empty).Trim();
            var supCode = row.GetValueOrDefault("SupervisorEmployeeCode", string.Empty).Trim();
            var supEmail = row.GetValueOrDefault("SupervisorEmail", string.Empty).Trim();
            var gaps = gapsByCode.TryGetValue(finalCode, out var gl) ? gl : (gapsByCode[finalCode] = new List<ImportGap>());
            bool changed = false;

            if (!string.IsNullOrEmpty(mgrCode) || !string.IsNullOrEmpty(mgrEmail))
            {
                var mgrLabel = !string.IsNullOrEmpty(mgrCode) ? mgrCode : mgrEmail;
                var mgr = ResolveRef(mgrCode, mgrEmail);
                if (mgr is null)
                {
                    managersUnresolved++;
                    hierarchyWarnings.Add($"Row {rn}: Manager '{mgrLabel}' not found — imported without a manager link.");
                    gaps.Add(new ImportGap("link:manager", "link", $"Manager '{mgrLabel}' not found — not linked.", mgrLabel));
                }
                else if (mgr.Id == emp.Id)
                {
                    managersUnresolved++;
                    hierarchyWarnings.Add($"Row {rn}: Employee cannot be their own manager — manager link skipped.");
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
                        hierarchyWarnings.Add($"Row {rn}: Setting '{mgrLabel}' as manager of '{finalCode}' would create a circular hierarchy — manager link skipped.");
                        gaps.Add(new ImportGap("link:manager", "link", $"Setting '{mgrLabel}' as manager would create a circular hierarchy — not linked.", mgrLabel));
                    }
                    else
                    {
                        emp.ManagerEmployeeId = mgr.Id;
                        _db.ReportingLines.Add(new ReportingLine
                        {
                            TenantId = tenantId, EmployeeId = emp.Id, ManagerEmployeeId = mgr.Id,
                            RelationshipType = "SolidLine", EffectiveFrom = emp.JoiningDate, IsPrimary = true, IsActive = true
                        });
                        changed = true;
                        hierarchyLinked++;
                    }
                }
            }

            if (!string.IsNullOrEmpty(supCode) || !string.IsNullOrEmpty(supEmail))
            {
                var supLabel = !string.IsNullOrEmpty(supCode) ? supCode : supEmail;
                var sup = ResolveRef(supCode, supEmail);
                if (sup is null)
                {
                    hierarchyWarnings.Add($"Row {rn}: Supervisor '{supLabel}' not found — imported without a supervisor link.");
                    gaps.Add(new ImportGap("link:supervisor", "link", $"Supervisor '{supLabel}' not found — not linked.", supLabel));
                }
                else if (sup.Id != emp.Id)
                {
                    emp.SupervisorEmployeeId = sup.Id;
                    _db.ReportingLines.Add(new ReportingLine
                    {
                        TenantId = tenantId, EmployeeId = emp.Id, ManagerEmployeeId = sup.Id,
                        RelationshipType = "DottedLine", EffectiveFrom = emp.JoiningDate, IsPrimary = false, IsActive = true
                    });
                    changed = true;
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

        // Single final persist covering Pass 2 links, the advisory re-stamp, and the gap rows.
        if (await PersistAsync() is { } finalSaveError) return finalSaveError;

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
        int newBranches = allGaps.Where(g => g.Type == "org:branch").Select(g => (g.RawValue ?? string.Empty).ToUpperInvariant()).Distinct().Count();
        // "N possible duplicates" — rows imported (never dropped) but flagged as a possible existing person.
        int possibleDuplicates = allGaps.Count(g => g.Type is "dup:strong" or "dup:possible");

        var allErrors = errors.Take(30).ToList();
        var allWarnings = warnings.Concat(hierarchyWarnings).Take(30).ToList();
        if (createdIncomplete.Count > 0)
            await _audit.WriteAsync("employee.import_created_incomplete", "Employee", importBatchId.ToString(), Context(),
                JsonSerializer.Serialize(new { importBatchId, createdIncompleteCount = createdIncomplete.Count, managersUnresolved, salariesHeld }), ct);
        return Ok(new
        {
            received = rows.Count,
            imported = created,
            created,
            skipped,
            skippedNoName,
            skippedDupCode,
            incompleteDraft = createdIncomplete.Count,
            managersUnresolved,
            newDepartments,
            newBranches,
            salariesHeld,
            possibleDuplicates,
            hierarchyLinked,
            payrollProfilesCreated,
            importBatchId,
            errors = allErrors,
            warnings = allWarnings,
            createdIncomplete,
        });
    }

    public record ImportEmployeesRequest(string CsvContent);

    private static decimal GrossSalaryFromRow(Dictionary<string, string> row)
    {
        static decimal Amount(Dictionary<string, string> source, string key) =>
            decimal.TryParse(source.GetValueOrDefault(key, string.Empty), out var value) ? value : 0m;
        return Amount(row, "BasicSalary")
               + Amount(row, "HousingAllowance")
               + Amount(row, "TransportAllowance")
               + Amount(row, "FoodAllowance")
               + Amount(row, "MobileAllowance")
               + Amount(row, "OtherAllowance");
    }

    private static DateOnly? ReadCsvDate(Dictionary<string, string> row, string key)
    {
        var value = row.GetValueOrDefault(key, string.Empty).Trim();
        return DateOnly.TryParse(value, out var date) ? date : null;
    }

    private static int? ReadCsvInt(Dictionary<string, string> row, string key)
    {
        var value = row.GetValueOrDefault(key, string.Empty).Trim();
        return int.TryParse(value, out var number) ? number : null;
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
            HasSalary = GrossSalaryFromRow(row) > 0m,
        };
    }

    private async Task<SalaryStructure> ResolveImportSalaryStructureAsync(Guid tenantId, Guid? companyId, Grade? grade, string requestedCode, string currency, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(requestedCode)
            ? grade is not null ? $"GRADE-{grade.Code}" : "EMPLOYEE-IMPORT"
            : requestedCode.Trim();

        var existing = await _db.SalaryStructures
            .Where(s => s.TenantId == tenantId && s.Code == code && !s.IsDeleted && (s.CompanyId == companyId || s.CompanyId == null))
            .OrderByDescending(s => s.CompanyId == companyId)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

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

        if (grade is not null)
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
    /// applicable to the row's (country, nationality) → warning (never persisted to the wrong typed column);
    /// a populated identity value that fails the country pack FORMAT regex → warning with the hint. Blank
    /// irrelevant columns produce nothing. Warnings only — the file is never rejected on these.</summary>
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
                warnings.Add($"{d.CsvHeader} '{val}' is not applicable to {natLabel} in {iso2} — it will be ignored, not imported.");
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
        var tenantId = RequireTenant();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);

        var sub = await _db.TenantSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        int currentCount = await _db.Employees
            .CountAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active, ct);
        int remaining = sub is not null && sub.MaxEmployees > 0 ? sub.MaxEmployees - currentCount : int.MaxValue;

        // SHARED master-data lookups — the SAME loader Import (commit) uses → dry-run resolution == commit.
        var lookups = await EmployeeImportRowResolver.LoadImportLookupsAsync(_db, tenantId, ct);
        // Cumulative in-file position claims (file order wins) — mirrors commit exactly.
        var claimedPositionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // SHARED establishment preloads — the SAME loader Import (commit) uses, so the over-budget
        // downgrade PROJECTED here == the commit landing. Cumulative claims mirror commit (file order wins).
        var establishmentContext = await EmployeeImportEstablishmentEvaluator.LoadAsync(_db, _establishmentGuard, tenantId, ct);
        var claimedLevelSlots = new Dictionary<(Guid Dept, Guid Level), int>();

        var existingCodesList = await _db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .Select(e => e.EmployeeCode.ToUpperInvariant())
            .ToListAsync(ct);
        var existingCodes = new HashSet<string>(existingCodesList);
        // Duplicate-person detection PREVIEW parity (§4d): the SAME preloaded matcher commit uses, so the
        // dry-run flags the identical rows. Persists nothing — feeds the "most common gaps" strip + per-row
        // warnings only. Register non-error rows in file order so intra-file matches surface as in commit.
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
        var dupImporterScope = this.GetEntityScope();
        // Work emails already in the tenant — the email fallback the commit Pass 2 uses, so a "manager
        // known" test here matches commit's link outcome.
        var existingEmails = new HashSet<string>(await _db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.WorkEmail != "")
            .Select(e => e.WorkEmail.ToUpper())
            .ToListAsync(ct), StringComparer.OrdinalIgnoreCase);
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Work-email derivation collision sets (login-normalized), mirroring commit so the derived address +
        // email:duplicate flag projected here == the commit landing (dry-run == commit).
        // IgnoreQueryFilters is intentional: work-email uniqueness is tenant-wide (company-agnostic), matching
        // the User login boundary — a company-scoped caller must still collide against every company's rows.
        var existingEmailNorm = new HashSet<string>(
            (await _db.Employees.AsNoTracking().IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.WorkEmail != "")
                .Select(e => e.WorkEmail).ToListAsync(ct)).Select(AuthService.Normalize),
            StringComparer.Ordinal);
        var claimedEmailNorm = new HashSet<string>(StringComparer.Ordinal);

        // Pre-pass: collect all batch code→managerCode so circular detection works even when
        // both employee and manager are new in the same import batch. Also index batch emails so the
        // manager email-fallback is recognized in preview.
        var batchManagerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var batchEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var c = r.GetValueOrDefault("EmployeeCode", string.Empty).Trim().ToUpperInvariant();
            var m = r.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(c) && !string.IsNullOrEmpty(m))
                batchManagerMap[c] = m;
            var em = r.GetValueOrDefault("WorkEmail", string.Empty).Trim();
            if (!string.IsNullOrEmpty(em)) batchEmails.Add(em);
        }

        var previewRows = new List<object>();
        var seen = new HashSet<string>();
        int wouldCreate = 0, wouldSkip = 0, wouldCreateActive = 0, wouldCreateDraft = 0;
        int activeSeatsProjected = 0; // Active-landing rows counted against the active-seat budget (P1-4).
        // Dry-run readiness projection (§7.1): per non-error row, the landing state it WOULD get
        // (Active vs Draft) + why. Persists nothing. Policy per (company, country, nationality).
        var previewPolicyCache = new Dictionary<string, ResolvedReadinessPolicy>();
        // Per-field aggregation across rows ("ideally which fields" — owner's ask). Keyed by readiness
        // field key so the modal can render a "most common missing fields" strip. gate/label come off the
        // readiness item; rowCount is how many rows are missing it.
        var fieldGapAgg = new Dictionary<string, (string Label, string Gate, string Kind, int Count)>(StringComparer.OrdinalIgnoreCase);
        void RecordGaps(IEnumerable<ReadinessItem> items, string kind)
        {
            foreach (var it in items)
            {
                var cur = fieldGapAgg.GetValueOrDefault(it.Key);
                fieldGapAgg[it.Key] = (it.Label, it.Gate, kind, cur.Count + 1);
            }
        }

        int rowNum = 1;
        foreach (var row in rows)
        {
            rowNum++;
            var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            var name = row.GetValueOrDefault("FullName", string.Empty).Trim();
            var mgrCode = row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim();
            var mgrEmail = row.GetValueOrDefault("ManagerEmail", string.Empty).Trim();
            var supCode = row.GetValueOrDefault("SupervisorEmployeeCode", string.Empty).Trim();
            var supEmail = row.GetValueOrDefault("SupervisorEmail", string.Empty).Trim();
            var email = row.GetValueOrDefault("WorkEmail", string.Empty).Trim();

            var rowWarnings = new List<string>();
            var rowErrors = new List<string>();
            string status;

            // The two lawful drops are the only preview ERRORS (⇒ wouldSkip), so dry-run skips == commit
            // skips: (a) missing FullName, (b) duplicate EmployeeCode. Self/circular manager are WARNINGS —
            // commit does NOT drop those rows (Pass 1 created the person; Pass 2 only skips the link).
            if (string.IsNullOrWhiteSpace(name))
            { rowErrors.Add("Missing FullName"); }
            if (!string.IsNullOrEmpty(code) && (existingCodes.Contains(code.ToUpperInvariant()) || seen.Contains(code.ToUpperInvariant())))
            { rowErrors.Add($"Duplicate EmployeeCode '{code}'"); }

            // ── SHARED accept-never-block resolution (IDENTICAL to commit) → org/grade/position/salary gaps.
            var resolved = EmployeeImportRowResolver.ResolveRow(row, lookups, claimedPositionCodes);
            rowWarnings.AddRange(resolved.Warnings);

            if (!string.IsNullOrEmpty(mgrCode) || !string.IsNullOrEmpty(mgrEmail))
            {
                var mgrUpper = mgrCode.ToUpperInvariant();
                var mgrLabel = !string.IsNullOrEmpty(mgrCode) ? mgrCode : mgrEmail;
                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(mgrCode) && mgrCode.Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    rowWarnings.Add("Employee cannot be their own manager — manager link will be skipped");
                }
                else
                {
                    bool mgrKnown = (!string.IsNullOrEmpty(mgrCode) && (existingCodes.Contains(mgrUpper) || seen.Contains(mgrUpper) || batchManagerMap.ContainsKey(mgrUpper)))
                                 || (!string.IsNullOrEmpty(mgrEmail) && (existingEmails.Contains(mgrEmail) || seenEmails.Contains(mgrEmail) || batchEmails.Contains(mgrEmail)));
                    if (!mgrKnown)
                        rowWarnings.Add($"Manager '{mgrLabel}' not found — manager link will be skipped");

                    if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(mgrCode))
                    {
                        // Circular check using the full batch map — detects A→B→A even when both are new
                        var visited2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { code.ToUpperInvariant() };
                        var cursor2 = mgrUpper;
                        bool circular2 = false;
                        for (int d = 0; d < 50; d++)
                        {
                            if (!visited2.Add(cursor2)) { circular2 = true; break; }
                            if (batchManagerMap.TryGetValue(cursor2, out var next)) cursor2 = next;
                            else break;
                        }
                        if (circular2) rowWarnings.Add($"Setting '{mgrLabel}' as manager would create a circular hierarchy — manager link will be skipped");
                    }
                }
            }

            if (!string.IsNullOrEmpty(supCode) || !string.IsNullOrEmpty(supEmail))
            {
                var supLabel = !string.IsNullOrEmpty(supCode) ? supCode : supEmail;
                bool supKnown = (!string.IsNullOrEmpty(supCode) && (existingCodes.Contains(supCode.ToUpperInvariant()) || seen.Contains(supCode.ToUpperInvariant())))
                             || (!string.IsNullOrEmpty(supEmail) && (existingEmails.Contains(supEmail) || seenEmails.Contains(supEmail) || batchEmails.Contains(supEmail)));
                if (!supKnown)
                    rowWarnings.Add($"Supervisor '{supLabel}' not found — supervisor link will be skipped");
            }

            var ibanPreview = row.GetValueOrDefault("IBAN", string.Empty).Trim();
            // Use the real ISO 13616 mod-97 check (not just structure) so a bad checksum is caught in
            // preview, matching what the payroll-run/WPS gate enforces later.
            if (!string.IsNullOrEmpty(ibanPreview) && !Zayra.Api.Infrastructure.Payroll.IbanValidator.IsValid(ibanPreview))
                rowWarnings.Add($"IBAN '{ibanPreview}' is invalid — fails ISO 13616 (mod-97) validation and will be stored as-is but must be corrected before this employee can be paid via WPS");
            var basicSalaryPreview = row.GetValueOrDefault("BasicSalary", string.Empty).Trim();
            if (!string.IsNullOrEmpty(basicSalaryPreview) && !decimal.TryParse(basicSalaryPreview, out _))
                rowWarnings.Add($"BasicSalary '{basicSalaryPreview}' is not a valid number — salary will not be imported");

            bool hasErrors = rowErrors.Count > 0;

            // Readiness projection for a row that WILL be created.
            string projectedStatus = string.Empty;
            List<string> projBlocking = new(), projRecommended = new();
            if (!hasErrors)
            {
                var csvStatus = row.GetValueOrDefault("Status", string.Empty).Trim();
                var country = row.GetValueOrDefault("CountryCode", string.Empty).Trim();
                var nationality = row.GetValueOrDefault("Nationality", string.Empty).Trim();
                // Resolve the row's company (P1-2) — same rule as commit's ResolveRowLandingAsync — so a
                // company-scoped policy applies here exactly as it will at commit; cache key mirrors commit.
                // The row's resolved company is the SAME the shared resolver used (default when unknown), so
                // a company-scoped policy applies here exactly as it will at commit.
                var rowCompanyId = resolved.CompanyId;
                var key = $"{rowCompanyId}|{country.ToUpperInvariant()}|{Zayra.Api.Infrastructure.Employees.GccReadinessFloor.NormalizeNationality(nationality)}";
                if (!previewPolicyCache.TryGetValue(key, out var policy))
                {
                    policy = await _activationGuard.ResolvePolicyAsync(tenantId, rowCompanyId, country, nationality, ct);
                    previewPolicyCache[key] = policy;
                }
                // Country-aware column validation (§4): warn on values in columns that don't apply to this
                // row's (country, nationality) and on identity numbers failing the pack format — never errors.
                Zayra.Api.Application.CountryPack.IIdentityDocumentFormat fmt =
                    countryPacks?.ResolveIdentityDocumentFormat(policy.CountryCode, string.Empty)
                    ?? new Zayra.Api.Infrastructure.CountryPack.DefaultIdentityDocumentFormat();
                rowWarnings.AddRange(CountryAwareRowWarnings(row, policy.CountryCode, nationality, fmt));
                DateTime.TryParse(row.GetValueOrDefault("JoiningDate", string.Empty), out var pjd);
                var snap = ImportReadinessSnapshot(row, resolved.DepartmentId, resolved.DesignationId, pjd == default ? DateTime.UtcNow : pjd);
                var readiness = _activationGuard.Evaluate(snap, policy);
                projBlocking = readiness.Blocking.Select(b => b.Label).ToList();
                projRecommended = readiness.Recommended.Select(b => b.Label).ToList();
                RecordGaps(readiness.Blocking, "blocking");
                RecordGaps(readiness.Recommended, "recommended");
                // Advisory org-skeleton / payroll import gaps also feed the "most common gaps" strip (Part E3).
                // (email:needs-info / email:domain-mismatch are already in resolved.Gaps + resolved.Warnings.)
                RecordGaps(resolved.Gaps.Select(g => EmployeeReadinessEvaluator.ImportGapToItem(g.Type, g.Category)), "recommended");

                // Work-email derive/duplicate projection (mirrors commit → dry-run == commit).
                if (!string.IsNullOrEmpty(resolved.WorkEmailLocalPart))
                {
                    var derived = WorkEmailDeriver.Uniqueify(resolved.WorkEmailLocalPart, resolved.WorkEmailDomain,
                        addr => existingEmailNorm.Contains(AuthService.Normalize(addr)) || claimedEmailNorm.Contains(AuthService.Normalize(addr)));
                    claimedEmailNorm.Add(AuthService.Normalize(derived));
                }
                else if (!string.IsNullOrWhiteSpace(resolved.WorkEmailProvided))
                {
                    var norm = AuthService.Normalize(resolved.WorkEmailProvided);
                    if (existingEmailNorm.Contains(norm) || claimedEmailNorm.Contains(norm))
                    {
                        RecordGaps(new[] { EmployeeReadinessEvaluator.ImportGapToItem("email:duplicate", "readiness") }, "recommended");
                        rowWarnings.Add($"Work email '{resolved.WorkEmailProvided}' is already in use — imported as-is and flagged.");
                    }
                    else claimedEmailNorm.Add(norm);
                }
                projectedStatus = string.IsNullOrWhiteSpace(csvStatus)
                    ? EmployeeStatuses.Draft
                    : string.Equals(csvStatus, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)
                        ? (readiness.IsBlocked ? EmployeeStatuses.Draft : EmployeeStatuses.Active)
                        : csvStatus;

                // Seat gate (P1-4): only Active-landing rows consume an active seat. A complete row that
                // would land Active but has no seat left is DOWNGRADED to Draft (imported inactive), never
                // errored — Draft rows never consume a seat, matching the inactive-until-complete model and
                // the commit path. Unlimited plans have remaining == int.MaxValue so this never fires.
                if (string.Equals(projectedStatus, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase))
                {
                    if (activeSeatsProjected < remaining) activeSeatsProjected++;
                    else
                    {
                        projectedStatus = EmployeeStatuses.Draft;
                        rowWarnings.Add($"Active seat limit reached ({sub?.MaxEmployees}) — imported as inactive until an active seat is available.");
                    }
                }

                // Establishment budget projection (§5.2 / AC9) — SHARED evaluator, byte-identical to commit.
                // Runs AFTER the seat gate so it sees the final landing status (commit does the same). An
                // over-budget row is NEVER skipped: Advisory consumes+warns; Enforced downgrades to Draft with
                // an org:establishment gap. This is the parity fix — preview now matches commit's landing.
                var estDecision = EmployeeImportEstablishmentEvaluator.Evaluate(
                    resolved.DepartmentId, resolved.DesignationId, projectedStatus, establishmentContext,
                    claimedLevelSlots, row.GetValueOrDefault("Department", string.Empty).Trim());
                if (estDecision.OverBudget)
                {
                    // Feed the org:establishment gap into the "most common gaps" strip, exactly as commit persists it.
                    RecordGaps(new[] { EmployeeReadinessEvaluator.ImportGapToItem("org:establishment", "org") }, "recommended");
                    if (estDecision.Advisory)
                    {
                        rowWarnings.Add(estDecision.Detail);
                    }
                    else
                    {
                        projectedStatus = EmployeeStatuses.Draft;
                        rowWarnings.Add($"{name} imported as Draft — {estDecision.Detail} Assign within budget to activate.");
                    }
                }

                // ── DUPLICATE-PERSON detection (dry-run parity with commit) ──────────────────────────
                var dupBatchKey = !string.IsNullOrWhiteSpace(code) ? code : $"__row{rowNum}";
                var dupProbe = DuplicateCandidateBuilder.Build(
                    null, dupBatchKey, code, name, name, row.GetValueOrDefault("ArabicName", string.Empty),
                    resolved.CompanyId, nationality, ReadCsvDate(row, "DateOfBirth"),
                    row.GetValueOrDefault("IqamaNumber", string.Empty), row.GetValueOrDefault("EmiratesId", string.Empty),
                    row.GetValueOrDefault("Qid", string.Empty), row.GetValueOrDefault("CivilId", string.Empty),
                    row.GetValueOrDefault("IdNumber", string.Empty), row.GetValueOrDefault("PassportNumber", string.Empty));
                var dupMatches = dupMatcher.Match(dupProbe);
                if (dupMatches.Count > 0)
                {
                    var strongest = dupMatches[0];
                    var gapType = strongest.MatchType == DuplicateMatchTypes.Strong ? "dup:strong" : "dup:possible";
                    RecordGaps(new[] { EmployeeReadinessEvaluator.ImportGapToItem(gapType, "dup") }, "recommended");
                    var (detail, _) = DupGapText(dupImporterScope, strongest.Counterpart, strongest.Signals);
                    rowWarnings.Add(detail);
                }
                dupMatcher.Register(dupProbe);
            }

            if (hasErrors) { status = "Error"; wouldSkip++; }
            else
            {
                status = "WillCreate";
                wouldCreate++;
                if (string.Equals(projectedStatus, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)) wouldCreateActive++;
                else if (string.Equals(projectedStatus, EmployeeStatuses.Draft, StringComparison.OrdinalIgnoreCase)) wouldCreateDraft++;
                if (!string.IsNullOrWhiteSpace(code)) seen.Add(code.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(email)) seenEmails.Add(email);
            }

            previewRows.Add(new
            {
                row = rowNum,
                employeeCode = code,
                fullName = name,
                status,
                projectedStatus,
                blocking = projBlocking,
                recommended = projRecommended,
                errors = rowErrors,
                warnings = rowWarnings
            });
        }

        // Per-field summary ("most common missing fields" strip) — blockers first, then by frequency.
        var fieldGaps = fieldGapAgg
            .Select(kv => new { field = kv.Key, label = kv.Value.Label, gate = kv.Value.Gate, kind = kv.Value.Kind, rowCount = kv.Value.Count })
            .OrderByDescending(g => g.kind == "blocking")
            .ThenByDescending(g => g.rowCount)
            .ThenBy(g => g.label)
            .ToList();

        return Ok(new
        {
            received = rows.Count,
            wouldCreate,
            wouldSkip,
            wouldCreateActive,
            wouldCreateDraft,
            fieldGaps,
            rows = previewRows
        });
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

            var employee = await employeeManagement.CreateAsync(tenantId, request, Context(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = employee.Id }, employee);
        }
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
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

    [HttpPost("drafts")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<EmployeeDraftDto>> CreateDraft(EmployeeDraftRequest request, CancellationToken cancellationToken)
    {
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
        var draft = await _db.EmployeeDrafts.FirstOrDefaultAsync(x => x.Id == draftId && x.TenantId == tenantId, cancellationToken);
        if (draft is null) return NotFound();
        ApplyDraft(draft, request);
        var docs = await _db.EmployeeDocuments.CountAsync(x => x.TenantId == tenantId && x.DraftId == draftId, cancellationToken);
        draft.ProfileCompletenessScore = CalculateCompleteness(draft, docs);
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.draft_updated", "EmployeeDraft", draft.Id.ToString(), cancellationToken);
        return Ok(EmployeeDraftDto.Project(draft, CanViewSensitive()));
    }

    [HttpPost("drafts/{draftId:guid}/documents")]
    [HasPermission("employees.documents")]
    public async Task<ActionResult<EmployeeDocumentDto>> AddDraftDocument(Guid draftId, EmployeeDocumentRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        if (!await _db.EmployeeDrafts.AnyAsync(x => x.Id == draftId && x.TenantId == tenantId, cancellationToken)) return NotFound();
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            DraftId = draftId,
            DocumentType = request.DocumentType.Trim(),
            FileName = request.FileName.Trim(),
            ContentType = request.ContentType.Trim(),
            StorageUrl = request.StorageUrl.Trim(),
            IsRequired = request.IsRequired,
            ExpiryDate = request.ExpiryDate
        };
        _db.EmployeeDocuments.Add(document);
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.document_uploaded", "EmployeeDraft", draftId.ToString(), cancellationToken);
        return Created($"/api/employees/documents/{document.Id}", EmployeeDocumentDto.Project(document));
    }

    [HttpPost("drafts/{draftId:guid}/documents/upload")]
    [HasPermission("employees.documents")]
    [RequestSizeLimit(10_485_760)]
    public async Task<ActionResult<EmployeeDocumentDto>> UploadDraftDocument(Guid draftId, [FromForm] EmployeeDocumentUploadRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        if (request.File is null) return BadRequest(new { message = "Document file is required." });
        if (!await _db.EmployeeDrafts.AnyAsync(x => x.Id == draftId && x.TenantId == tenantId, cancellationToken)) return NotFound();
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
        _db.EmployeeDocuments.Add(document);
        await _db.SaveChangesAsync(cancellationToken);
        await Notify("Document uploaded", $"{request.DocumentType} was uploaded for draft {draftId}.", "EmployeeDraft", draftId.ToString(), cancellationToken);
        await Audit("employee.document_file_uploaded", "EmployeeDraft", draftId.ToString(), cancellationToken);
        return Created($"/api/employees/documents/{document.Id}", EmployeeDocumentDto.Project(document));
    }

    [HttpGet("documents/{documentId:guid}/download")]
    public async Task<IActionResult> DownloadDocument(Guid documentId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var document = await _db.EmployeeDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.TenantId == tenantId && !x.IsDeleted, cancellationToken);
        if (document is null) return NotFound();

        // Enforce access: unrestricted roles pass; otherwise caller must be in scope for this employee.
        if (document.EmployeeId.HasValue)
        {
            var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
            if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(document.EmployeeId.Value))
                return Forbid();
        }
        else if (document.DraftId.HasValue)
        {
            var actorId = GetUserId();
            var draft = await _db.EmployeeDrafts.AsNoTracking()
                .Where(x => x.Id == document.DraftId.Value && x.TenantId == tenantId)
                .Select(x => new { x.CreatedByUserId })
                .FirstOrDefaultAsync(cancellationToken);
            if (draft is null) return NotFound();
            if (!this.GetEntityScope().IsGroupLevel && draft.CreatedByUserId != actorId)
                return Forbid();
        }

        var path = _documents.ResolvePath(document.StorageUrl);
        if (!System.IO.File.Exists(path)) return NotFound(new { message = "Stored document file was not found." });

        document.LastDownloadedAtUtc = DateTime.UtcNow;
        document.LastDownloadedBy = GetUserId();
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.document_downloaded", "EmployeeDocument", documentId.ToString(), cancellationToken);

        return PhysicalFile(path, document.ContentType, document.FileName);
    }

    [HttpPost("drafts/{draftId:guid}/submit")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> SubmitDraft(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await FindDraft(draftId, cancellationToken);
        if (draft is null) return NotFound();
        draft.Status = "PendingHrApproval";
        draft.CurrentStep = "HrApproval";
        draft.SubmittedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await Notify("Employee draft submitted", "A draft is waiting for HR approval.", "EmployeeDraft", draftId.ToString(), cancellationToken);
        await Audit("employee.draft_submitted", "EmployeeDraft", draftId.ToString(), cancellationToken);
        return NoContent();
    }

    [HttpPost("drafts/{draftId:guid}/approve")]
    [HasPermission("employees.approve")]
    public async Task<ActionResult<EmployeeDetailDto>> ApproveDraft(Guid draftId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var draft = await FindDraft(draftId, cancellationToken);
        if (draft is null) return NotFound();
        if (draft.Status != "PendingHrApproval" && draft.Status != "Draft") return BadRequest(new { message = "Draft is not ready for HR approval." });

        // Onboarding integrity (consultant B1/R-A): EmployeeDraft stores free-text org fields only,
        // so this hire path used to create Active employees with string-only department/designation
        // — permanently Unclassified and invisible to every headcount control. Resolve to IDs at
        // approve time; a non-empty name that doesn't resolve is a 422 (fix master data first).
        Guid? draftDeptId = null; var draftDeptName = draft.Department;
        Guid? draftDesigId = null; var draftDesigTitle = draft.Designation;
        Guid? draftBranchId = null; var draftBranchName = draft.Branch;
        try
        {
            if (!string.IsNullOrWhiteSpace(draft.Department))
                (draftDeptId, draftDeptName) = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(_db, tenantId, draft.Department, cancellationToken);
            if (!string.IsNullOrWhiteSpace(draft.Designation))
                (draftDesigId, draftDesigTitle) = await EmployeeOrgFieldResolver.ResolveDesignationAsync(_db, tenantId, draft.Designation, cancellationToken);
            if (!string.IsNullOrWhiteSpace(draft.Branch))
                (draftBranchId, draftBranchName) = await EmployeeOrgFieldResolver.ResolveBranchAsync(_db, tenantId, draft.Branch, cancellationToken);
        }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new { message = ex.Message }); }

        var employee = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = await GenerateEmployeeCode(tenantId, cancellationToken),
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
            CountryCode = draft.CountryCode,
            Department = draftDeptName,
            DepartmentId = draftDeptId,
            Designation = draftDesigTitle,
            DesignationId = draftDesigId,
            WorkLocation = draft.WorkLocation,
            Branch = draftBranchName,
            BranchId = draftBranchId,
            ManagerEmployeeId = draft.ManagerEmployeeId,
            Status = "Active",
            JoiningDate = draft.JoiningDate ?? DateTime.UtcNow.Date,
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
            ActivatedAtUtc = DateTime.UtcNow
        };

        // Auto-hierarchy: if no manager was set during onboarding, default to the head of the
        // department the employee is joining, and inherit that head's manager as second-level.
        if (employee.ManagerEmployeeId is null && !string.IsNullOrWhiteSpace(employee.Department))
        {
            var deptHeadId = await _db.Departments.AsNoTracking()
                .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.NameEn == employee.Department)
                .Select(d => d.ManagerEmployeeId).FirstOrDefaultAsync(cancellationToken);
            if (deptHeadId is { } headId && headId != 0)
            {
                employee.ManagerEmployeeId = headId;
                employee.SecondLevelManagerEmployeeId = await _db.Employees.AsNoTracking()
                    .Where(e => e.TenantId == tenantId && e.Id == headId)
                    .Select(e => e.ManagerEmployeeId).FirstOrDefaultAsync(cancellationToken);
            }
        }

        // READINESS GATE (§5.3, 3rd Active path): the draft is approved straight to Active, so it must
        // satisfy the resolved readiness policy first. Snapshot is built AT THE CALL-SITE from the
        // in-memory employee (Id==0) + the draft's documents (keyed by DraftId) + the draft IBAN string
        // (no payroll row exists yet) — never DB-load-by-id (§5.4). A block leaves the draft UNTOUCHED
        // and returns the structured 422; the draft can still be saved, just not approved-to-Active.
        var draftDocsForGate = await _db.EmployeeDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DraftId == draftId && !x.IsDeleted)
            .Select(x => new { x.DocumentType, x.ApprovalStatus, x.ExpiryDate })
            .ToListAsync(cancellationToken);
        var gateDocs = draftDocsForGate
            .Select(x => new DocumentPresence(x.DocumentType, string.Equals(x.ApprovalStatus, "Verified", StringComparison.OrdinalIgnoreCase), x.ExpiryDate))
            .ToList();
        var draftSnapshot = EmployeeReadinessEvaluator.BuildFromEmployee(
            employee, null, gateDocs, new Dictionary<string, DateOnly?>(), (employee.Salary ?? 0m) > 0m);
        EmployeeReadiness draftReadiness;
        try
        {
            draftReadiness = await _activationGuard.EnsureActivatableAsync(tenantId, employee.CompanyId, draftSnapshot, Context(), cancellationToken);
        }
        catch (EmployeeActivationBlockedException ex)
        {
            _db.ChangeTracker.Clear();
            await Audit("employee.activation_blocked", "EmployeeDraft", draftId.ToString(), cancellationToken);
            return this.NotActivatable(ex);
        }
        employee.ReadinessState = draftReadiness.State;
        employee.ActivationBlockersCount = draftReadiness.Blocking.Count;
        employee.ReadinessEvaluatedAtUtc = DateTime.UtcNow;

        // ESTABLISHMENT GUARD (path "draft_approve"): this creates an OCCUPYING employee
        // (Status = Active) directly, so the seat is consumed here — transaction + slot lock +
        // enforce + insert are atomic; a block leaves the draft untouched and returns the
        // structured 409 for the popup.
        try
        {
            await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
                excludeEmployeeId: null, path: "draft_approve", Context(), async () =>
                {
                    _db.Employees.Add(employee);
                    await _db.SaveChangesAsync(cancellationToken);

                    var draftDocuments = await _db.EmployeeDocuments.Where(x => x.TenantId == tenantId && x.DraftId == draftId).ToListAsync(cancellationToken);
                    foreach (var document in draftDocuments) document.EmployeeId = employee.Id;
                    employee.UserAccountId = await CreateEmployeeUserAccount(employee, cancellationToken);
                    draft.Status = "Activated";
                    draft.CurrentStep = "Activated";
                    draft.ApprovedAtUtc = DateTime.UtcNow;
                    draft.ActivatedAtUtc = DateTime.UtcNow;
                    await AddHistory(employee, "Activated", DateOnly.FromDateTime(employee.JoiningDate), cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    return true;
                }, cancellationToken);
        }
        catch (EstablishmentBudgetExceededException ex)
        {
            _db.ChangeTracker.Clear();
            return this.EstablishmentConflict(ex);
        }
        await Notify("Employee activated", $"{employee.FullName} was activated with ID {employee.EmployeeCode}.", "Employee", employee.Id.ToString(), cancellationToken);
        await Audit("employee.activated", "Employee", employee.Id.ToString(), cancellationToken);
        var documents = await _db.EmployeeDocuments.Where(x => x.EmployeeId == employee.Id).ToListAsync(cancellationToken);
        var histories = await _db.EmployeeHistories.Where(x => x.EmployeeId == employee.Id).ToListAsync(cancellationToken);
        return Ok(EmployeeDetailDto.Project(employee, CanViewSensitive(), documents: documents, history: histories));
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
        var sensitive = request.Changes.Keys.Where(SensitiveFields.Contains).ToList();
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

                if (immediateChanges.Count > 0)
                {
                    ApplyChanges(employee, immediateChanges);
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
                RequireTenant(), id, request with { SeparationType = null }, Context(), cancellationToken);
            return employee is null ? NotFound() : Ok(employee);
        }
        // Readiness block MUST be caught before InvalidOperationException (which would swallow the
        // structured body into a generic 400) — EmployeeActivationBlockedException is standalone (§5.6).
        catch (EmployeeActivationBlockedException ex) { await Audit("employee.activation_blocked", "Employee", id.ToString(), cancellationToken); return this.NotActivatable(ex); }
        catch (EstablishmentBudgetExceededException ex) { return this.EstablishmentConflict(ex); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{id:int}/documents")]
    [HasPermission("employees.documents")]
    [RequestSizeLimit(10_485_760)]
    public async Task<ActionResult<EmployeeDocumentDto>> UploadEmployeeDocument(int id, [FromForm] EmployeeDocumentUploadMetadata request, [FromForm] IFormFile file, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            var document = await employeeManagement.UploadDocumentAsync(RequireTenant(), id, request, file, Context(), cancellationToken);
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
            var employee = await employeeManagement.ActivateAsync(RequireTenant(), id, request, Context(), cancellationToken);
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
        var employee = await employeeManagement.TerminateAsync(RequireTenant(), id, request, Context(), cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

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
    private static readonly string[] BulkExportRoles = { "Admin", "HR Manager", "HR Officer", "Payroll Officer", "Auditor" };
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
            "export" => BulkExportRoles.Any(r => User.IsInRole(r)),
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
            var csv = await BuildEmployeesCsvAsync(emps, tenantId, cancellationToken);
            await _audit.WriteAsync("employees.exported", "Employee", "bulk", Context(), JsonSerializer.Serialize(new
            {
                rowCount = emps.Count,
                mode = "selected",
                selectionType = idsMode ? "idset" : "allMatching",
                groupScope = entityScope.IsGroupLevel,
                companyIds = entityScope.IsGroupLevel ? null : entityScope.AccessibleCompanyIds,
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

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employees.deleted", "Employee", id.ToString(), context, JsonSerializer.Serialize(new
        {
            employee.PrivacyStatus,
            employee.RetentionUntilUtc,
            Reason = reason,
            CancelledApprovalRequests = cancelledApprovals,
            ReroutedApprovalRequests = reroutedApprovals,
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

    [HttpPost("changes/{changeId:guid}/approve")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> ApproveChange(Guid changeId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var change = await _db.EmployeeChangeRequests.FirstOrDefaultAsync(x => x.Id == changeId && x.TenantId == tenantId, cancellationToken);
        if (change is null) return NotFound();
        if (!string.Equals(change.Status, "PendingApproval", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Change request has already been decided." });
        var approverId = GetUserId();
        if (approverId is not null && change.RequestedByUserId == approverId)
            return BadRequest(new { message = "Maker-checker violation: requester cannot approve their own sensitive change." });
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        if (change.EffectiveDate > today)
            return BadRequest(new { message = "Future-dated sensitive changes cannot be applied before their effective date." });
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.Id == change.EmployeeId && x.TenantId == tenantId, cancellationToken);
        if (employee is null) return NotFound();
        var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson) ?? new();
        var priorDeptId = employee.DepartmentId;
        var priorDesigId = employee.DesignationId;
        try
        {
            ApplyChanges(employee, changes);
            await EmployeeOrgFieldResolver.ResolveAppliedChangesAsync(_db, tenantId, employee, changes.Keys, cancellationToken);
            // Keep the payroll profile's bank columns in step with the employee scalar so an IBAN fixed
            // via the checklist actually reaches the WPS/payroll run (Δ13 / P1-1).
            await EmployeeBankProfileSync.SyncAsync(_db, employee, changes.Keys, cancellationToken);
            employee.UpdatedAtUtc = DateTime.UtcNow;
            change.Status = "ApprovedApplied";
            change.ApprovedByUserId = approverId;
            change.ApprovedAtUtc = DateTime.UtcNow;
            change.AppliedAtUtc = DateTime.UtcNow;
            await AddHistory(employee, "SensitiveChangeApproved", change.EffectiveDate, cancellationToken);
            // ESTABLISHMENT GUARD (path "approval"): authoritative re-check AT APPLY — the slot may
            // have been consumed since submission. On a block nothing is persisted (throws before
            // save / transaction rolls back), so the change request stays PendingApproval and can
            // be re-approved after a budget raise.
            if (employee.DepartmentId != priorDeptId || employee.DesignationId != priorDesigId)
            {
                await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
                    excludeEmployeeId: employee.Id, path: "approval", Context(), async () =>
                    {
                        await _db.SaveChangesAsync(cancellationToken);
                        return true;
                    }, cancellationToken);
            }
            else
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            await Audit("employee.change_approved", "EmployeeChangeRequest", change.Id.ToString(), cancellationToken);
            return Ok(EmployeeDetailDto.Project(employee, CanViewSensitive()));
        }
        catch (EstablishmentBudgetExceededException ex)
        {
            // Discard the half-applied tracked mutations BEFORE any further write on this context
            // (Notify saves): the change request must remain PendingApproval untouched.
            _db.ChangeTracker.Clear();
            await Notify("Employee change blocked by staffing budget",
                $"The approved change for {employee.EmployeeCode} could not be applied: {ex.Block.DepartmentName} already has {ex.Block.Current} of {ex.Block.Budgeted} budgeted {ex.Block.LevelNameEn}(s). Raise the budget or amend the change; the request remains pending.",
                "EmployeeChangeRequest", change.Id.ToString(), cancellationToken);
            return this.EstablishmentConflict(ex);
        }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new { message = ex.Message }); }
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
        return Ok(await employeeManagement.ExpiringDocumentsAsync(RequireTenant(), days, cancellationToken));
    }

    [HttpGet("reports/missing-documents")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Auditor")]
    public async Task<ActionResult<IReadOnlyCollection<EmployeeMissingDocumentsReportDto>>> MissingDocuments([FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        return Ok(await employeeManagement.MissingDocumentsAsync(RequireTenant(), cancellationToken));
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

    [HttpGet("ai/insights")]
    public async Task<ActionResult<EmployeeAiResponseDto>> AiInsights([FromQuery] string query, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var normalized = (query ?? string.Empty).ToLowerInvariant();
        var employees = _db.Employees.Where(x => x.TenantId == tenantId);
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

    [HttpGet("{id:int}/letters/appointment")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> AppointmentLetter(int id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound();
        var tenant = await _db.Tenants.AsNoTracking().Select(t => new { t.Id, t.Name }).FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == id && x.IsActive && x.EffectiveDate <= today).OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(cancellationToken);
        var apptCurrency = !string.IsNullOrWhiteSpace(salary?.Currency)
            ? salary.Currency
            : await _db.ResolveTenantCurrencyAsync(tenantId, cancellationToken);
        var data = new LetterData(
            EmployeeName: employee.FullName,
            EmployeeCode: employee.EmployeeCode,
            Department: employee.Department,
            Designation: employee.Designation,
            JoiningDate: employee.JoiningDate,
            LeavingDate: null,
            BasicSalary: salary?.BasicSalary ?? employee.Salary ?? 0m,
            Currency: apptCurrency,
            CompanyName: tenant?.Name ?? "KynexOne Technologies",
            IssuedBy: "HR Department",
            IssuedDate: DateTime.UtcNow
        );
        var pdf = await _letters.GenerateAppointmentLetterAsync(data, cancellationToken);
        await Audit("employee.letter.appointment", "Employee", id.ToString(), cancellationToken);
        return File(pdf, "application/pdf", $"appointment-letter-{employee.EmployeeCode}.pdf");
    }

    [HttpGet("{id:int}/letters/experience")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ExperienceLetter(int id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound();
        var tenant = await _db.Tenants.AsNoTracking().Select(t => new { t.Id, t.Name }).FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == id && x.IsActive && x.EffectiveDate <= today).OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(cancellationToken);
        var expCurrency = !string.IsNullOrWhiteSpace(salary?.Currency)
            ? salary.Currency
            : await _db.ResolveTenantCurrencyAsync(tenantId, cancellationToken);
        var data = new LetterData(
            EmployeeName: employee.FullName,
            EmployeeCode: employee.EmployeeCode,
            Department: employee.Department,
            Designation: employee.Designation,
            JoiningDate: employee.JoiningDate,
            LeavingDate: employee.ContractEndDate.HasValue ? employee.ContractEndDate.Value.ToDateTime(TimeOnly.MinValue) : null,
            BasicSalary: salary?.BasicSalary ?? employee.Salary ?? 0m,
            Currency: expCurrency,
            CompanyName: tenant?.Name ?? "KynexOne Technologies",
            IssuedBy: "HR Department",
            IssuedDate: DateTime.UtcNow
        );
        var pdf = await _letters.GenerateExperienceLetterAsync(data, cancellationToken);
        await Audit("employee.letter.experience", "Employee", id.ToString(), cancellationToken);
        return File(pdf, "application/pdf", $"experience-letter-{employee.EmployeeCode}.pdf");
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

    private async Task<int?> GetCallerEmployeeId(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null) return null;
        return await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == RequireTenant() && !e.IsDeleted && e.UserAccountId == userId)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<EmployeeDraft?> FindDraft(Guid draftId, CancellationToken cancellationToken) => await _db.EmployeeDrafts.FirstOrDefaultAsync(x => x.Id == draftId && x.TenantId == RequireTenant(), cancellationToken);

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

    private async Task<string> GenerateEmployeeCode(Guid tenantId, CancellationToken cancellationToken)
    {
        var rule = await _db.EmployeeIdRules
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted, cancellationToken);
        if (rule is null)
        {
            rule = new EmployeeIdRule { TenantId = tenantId, CreatedBy = GetUserId() };
            _db.EmployeeIdRules.Add(rule);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var parts = new List<string> { rule.CompanyPrefix };
        if (rule.UseYear) parts.Add(DateTime.UtcNow.Year.ToString());
        var code = string.Join('-', parts.Where(x => !string.IsNullOrWhiteSpace(x)))
            + "-"
            + rule.NextSequence.ToString().PadLeft(rule.PaddingLength, '0');
        rule.NextSequence += 1;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedBy = GetUserId();
        await _db.SaveChangesAsync(cancellationToken);
        return code;
    }

    private async Task<Guid?> CreateEmployeeUserAccount(Employee employee, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employee.WorkEmail) || employee.TenantId is null) return null;
        var normalized = AuthService.Normalize(employee.WorkEmail);
        var exists = await _db.Users.Include(x => x.EntityAccesses).FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.NormalizedEmail == normalized, cancellationToken);
        if (exists is not null)
        {
            if (!exists.IsActive)
            {
                exists.IsActive = true;
                exists.FullName = employee.FullName;
            }
            EnsureEmployeeCompanyGrant(exists, employee);
            await _db.SaveChangesAsync(cancellationToken);
            return exists.Id;
        }
        var role = await _db.Roles.FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.NormalizedName == "EMPLOYEE", cancellationToken);
        var user = new User { TenantId = employee.TenantId.Value, Email = employee.WorkEmail.Trim().ToLowerInvariant(), NormalizedEmail = normalized, FullName = employee.FullName, PasswordHash = _passwordHasher.Hash("ChangeMe123!"), IsActive = true, IsEmailConfirmed = false };
        _db.Users.Add(user);
        if (role is not null) user.UserRoles.Add(new UserRole { User = user, Role = role });
        EnsureEmployeeCompanyGrant(user, employee);
        await _db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    private void EnsureEmployeeCompanyGrant(User user, Employee employee)
    {
        if (employee.TenantId is null || !employee.CompanyId.HasValue) return;
        if (user.EntityAccesses.Any(x =>
                x.TenantId == employee.TenantId.Value
                && x.IsActive
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
            CreatedBy = GetUserId(),
            GrantedBy = GetUserId(),
            GrantedAt = DateTime.UtcNow
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

    private void ApplyChanges(Employee employee, Dictionary<string, JsonElement> changes)
    {
        foreach (var (field, value) in changes)
        {
            switch (field)
            {
                case "englishName":
                    employee.EnglishName = value.GetString() ?? employee.EnglishName;
                    employee.FullName = employee.EnglishName;
                    break;
                case "arabicName": employee.ArabicName = value.GetString() ?? employee.ArabicName; break;
                case "preferredName": employee.PreferredName = value.GetString() ?? employee.PreferredName; break;
                case "gender": employee.Gender = value.GetString() ?? employee.Gender; break;
                case "nationality": employee.Nationality = value.GetString() ?? employee.Nationality; break;
                case "personalEmail": employee.PersonalEmail = value.GetString() ?? employee.PersonalEmail; break;
                case "workEmail": employee.WorkEmail = value.GetString() ?? employee.WorkEmail; break;
                case "phone": employee.Phone = value.GetString() ?? employee.Phone; break;
                case "jobTitle": employee.JobTitle = value.GetString() ?? employee.JobTitle; break;
                case "employmentType": employee.EmploymentType = value.GetString() ?? employee.EmploymentType; break;
                case "joiningDate":
                    if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var joining))
                        employee.JoiningDate = DateTime.SpecifyKind(joining, DateTimeKind.Utc);
                    break;
                case "department": employee.Department = value.GetString() ?? employee.Department; break;
                case "designation": employee.Designation = value.GetString() ?? employee.Designation; break;
                case "branch": employee.Branch = value.GetString() ?? employee.Branch; break;
                case "workLocation": employee.WorkLocation = value.GetString() ?? employee.WorkLocation; break;
                case "managerEmployeeId": employee.ManagerEmployeeId = value.ValueKind == JsonValueKind.Null ? null : value.GetInt32(); break;
                case "dateOfBirth": employee.DateOfBirth = ReadDateOnly(value); break;
                case "maritalStatus": employee.MaritalStatus = value.GetString() ?? employee.MaritalStatus; break;
                case "emergencyContactName": employee.EmergencyContactName = value.GetString() ?? employee.EmergencyContactName; break;
                case "emergencyContactPhone": employee.EmergencyContactPhone = value.GetString() ?? employee.EmergencyContactPhone; break;
                case "contractType": employee.ContractType = value.GetString() ?? employee.ContractType; break;
                case "grade": employee.Grade = value.GetString() ?? employee.Grade; break;
                case "costCenter": employee.CostCenter = value.GetString() ?? employee.CostCenter; break;
                case "salary": employee.Salary = value.GetDecimal(); break;
                case "bankName": employee.BankName = value.GetString() ?? employee.BankName; break;
                case "bankIban": employee.BankIban = value.GetString() ?? employee.BankIban; break;
                case "wpsBankDetails": employee.WpsBankDetails = value.GetString() ?? employee.WpsBankDetails; break;
                case "passportNumber": employee.PassportNumber = value.GetString() ?? employee.PassportNumber; break;
                case "passportIssueDate": employee.PassportIssueDate = ReadDateOnly(value); break;
                case "passportExpiryDate": employee.PassportExpiryDate = ReadDateOnly(value); break;
                case "visaNumber": employee.VisaNumber = value.GetString() ?? employee.VisaNumber; break;
                case "visaIssueDate": employee.VisaIssueDate = ReadDateOnly(value); break;
                case "visaExpiryDate": employee.VisaExpiryDate = ReadDateOnly(value); break;
                case "iqamaNumber": employee.IqamaNumber = value.GetString() ?? employee.IqamaNumber; break;
                case "muqeemNumber": employee.MuqeemNumber = value.GetString() ?? employee.MuqeemNumber; break;
                case "gosiReference": employee.GosiReference = value.GetString() ?? employee.GosiReference; break;
                case "emiratesId": employee.EmiratesId = value.GetString() ?? employee.EmiratesId; break;
                case "laborCardNumber": employee.LaborCardNumber = value.GetString() ?? employee.LaborCardNumber; break;
                case "visaFileNumber": employee.VisaFileNumber = value.GetString() ?? employee.VisaFileNumber; break;
                case "qid": employee.Qid = value.GetString() ?? employee.Qid; break;
                case "workPermitNumber": employee.WorkPermitNumber = value.GetString() ?? employee.WorkPermitNumber; break;
                case "workPermitIssueDate": employee.WorkPermitIssueDate = ReadDateOnly(value); break;
                case "civilId": employee.CivilId = value.GetString() ?? employee.CivilId; break;
                case "residencyNumber": employee.ResidencyNumber = value.GetString() ?? employee.ResidencyNumber; break;
                case "residencyIssueDate": employee.ResidencyIssueDate = ReadDateOnly(value); break;
                case "terminationReason": employee.TerminationReason = value.GetString() ?? employee.TerminationReason; break;
            }
        }
    }

    private static DateOnly? ReadDateOnly(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return DateOnly.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

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

    private bool CanEditSensitive() => User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.HasClaim("permission", "employees.sensitive");
    private bool CanViewSensitive() => CanEditSensitive() || User.IsInRole("Payroll Officer") || User.HasClaim("permission", "employees.sensitive");
    private Task Notify(string title, string message, string entity, string? entityId, CancellationToken cancellationToken) => _notifications.NotifyAsync(RequireTenant(), null, title, message, entity, entityId, cancellationToken);

    private EmployeeListItemDto ToListItem(Employee employee) => new(employee.Id, employee.EmployeeCode, employee.FullName, employee.ArabicName, employee.Department, employee.Designation, employee.Branch, employee.ManagerEmployeeId, employee.Status, employee.ProfileCompletenessScore, employee.VisaExpiryDate, employee.PassportExpiryDate, employee.ReadinessState, employee.ActivationBlockersCount);
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

public record EmployeeListItemDto(int Id, string EmployeeCode, string FullName, string ArabicName, string Department, string Designation, string Branch, int? ManagerEmployeeId, string Status, decimal ProfileCompletenessScore, DateOnly? VisaExpiryDate, DateOnly? PassportExpiryDate, string ReadinessState, int ActivationBlockersCount);

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
public record EmployeeDocumentRequest(string DocumentType, string FileName, string ContentType, string StorageUrl, bool IsRequired, DateOnly? ExpiryDate);
public record EmployeeTransferRequestDto(string NewDepartment, string NewBranch, int? NewManagerEmployeeId, DateOnly EffectiveDate);
public record EmployeeUpdateRequest(DateOnly EffectiveDate, Dictionary<string, JsonElement> Changes);
public record GroupCountDto(string Name, int Count);
public record EmployeeReportsDto(int TotalHeadcount, int ActiveEmployees, int NewJoiners, int Exits, int ProbationEmployees, IReadOnlyCollection<GroupCountDto> DepartmentHeadcount, IReadOnlyCollection<GroupCountDto> BranchHeadcount, IReadOnlyCollection<GroupCountDto> NationalityMix, IReadOnlyCollection<GroupCountDto> GenderMix, int ContractExpiringSoon, int VisaOrPassportExpiringSoon, int MissingDocumentsOrIncompleteProfiles);
public record EmployeeAiResponseDto(string Answer, IReadOnlyCollection<EmployeeListItemDto> Employees);
public record EmployeeDraftRequest(string? CurrentStep, string? EnglishName, string? ArabicName, string? PersonalEmail, string? WorkEmail, string? Phone, string? Gender, DateOnly? DateOfBirth, string? MaritalStatus, string? EmergencyContactName, string? EmergencyContactPhone, string? Nationality, string? CountryCode, string? Department, string? Designation, string? Branch, string? WorkLocation, int? ManagerEmployeeId, DateTime? JoiningDate, string? ContractType, string? Grade, string? CostCenter, DateOnly? ContractStartDate, DateOnly? ContractEndDate, DateOnly? ProbationEndDate, string? PayrollProfileCode, decimal? Salary, string? BankName, string? BankIban, string? WpsBankDetails, string? ShiftPolicyCode, string? LeavePolicyCode, string? SponsorName, DateOnly? PassportIssueDate, string? PassportNumber, DateOnly? PassportExpiryDate, DateOnly? VisaIssueDate, string? VisaNumber, DateOnly? VisaExpiryDate, string? IqamaNumber, string? MuqeemNumber, string? GosiReference, string? QiwaContractNumber, string? EmiratesId, string? LaborCardNumber, string? VisaFileNumber, string? Qid, string? WorkPermitNumber, DateOnly? WorkPermitIssueDate, string? CivilId, string? ResidencyNumber, DateOnly? ResidencyIssueDate);
public record EmployeeDocumentUploadRequest(string DocumentType, bool IsRequired, DateOnly? ExpiryDate, IFormFile File);
public record EmployeeTemplateDto(string TemplateType, string Language, string Title, string Body);
