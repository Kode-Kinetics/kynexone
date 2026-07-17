using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
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

    public EmployeesController(ZayraDbContext db, IPasswordHasher passwordHasher, IAuditService audit, IDocumentStorage documents, INotificationService notifications, IHijriDateService hijri, IDataScopeService scopeService, ILetterService letters, IApprovalWorkflowService? approvalWorkflow = null)
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
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Manager,Auditor")]
    public async Task<ActionResult<PagedResult<EmployeeListItemDto>>> Search([FromServices] IEmployeeManagementService employeeManagement, [FromQuery] string? search, [FromQuery] string? status, [FromQuery] string? department, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var entityScope = this.GetEntityScope();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);

        if (scope.IsUnrestricted && entityScope.IsGroupLevel)
            return Ok(await employeeManagement.SearchAsync(tenantId, search, status, department, page, pageSize, cancellationToken));

        // Restricted scope: query directly and apply AllowedEmployeeIds and/or entity scope filter
        var query = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted);
        if (!scope.IsUnrestricted)
            query = query.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));
        if (!entityScope.IsGroupLevel)
        {
            var accessibleIds = entityScope.AccessibleCompanyIds;
            query = query.Where(e => e.CompanyId.HasValue && accessibleIds.Contains(e.CompanyId.Value));
        }
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e => e.FullName.Contains(search) || e.EmployeeCode.Contains(search) || (e.WorkEmail != null && e.WorkEmail.Contains(search)));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(department)) query = query.Where(e => e.Department == department);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(e => e.FullName).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new EmployeeListItemDto(e.Id, e.EmployeeCode, e.FullName, e.ArabicName ?? string.Empty, e.Department ?? string.Empty, e.Designation ?? string.Empty, e.Branch ?? string.Empty, e.ManagerEmployeeId, e.Status, e.ProfileCompletenessScore, e.VisaExpiryDate, e.PassportExpiryDate))
            .ToListAsync(cancellationToken);
        return Ok(new PagedResult<EmployeeListItemDto>(items, total, page, pageSize));
    }

    // ── Configurable export / import / shareable template ────────────────────────
    private static readonly string[] EmployeeCsvHeaders =
        {
            "EmployeeCode", "CompanyLegalName", "BranchCode", "CostCenterCode", "WorkLocation",
            "FullName", "ArabicName", "PreferredName", "WorkEmail", "PersonalEmail", "Phone", "Gender",
            "DateOfBirth", "Nationality", "MaritalStatus", "CountryCode",
            "Department", "DepartmentCode", "Designation", "JobTitle", "EmploymentType", "ContractType",
            "Grade", "PositionCode", "Status", "JoiningDate", "ConfirmationDate", "ProbationStartDate",
            "ProbationEndDate", "NoticePeriodDays", "ShiftPolicyCode", "LeavePolicyCode", "AttendancePolicyCode",
            // Hierarchy columns — resolved in Pass 2
            "ManagerEmployeeCode", "SupervisorEmployeeCode",
            // Payroll columns — creates EmployeePayrollProfile + EmployeeSalaryStructure on import
            "SalaryStructureCode", "BasicSalary", "HousingAllowance", "TransportAllowance", "FoodAllowance",
            "MobileAllowance", "OtherAllowance", "FixedDeduction", "Currency", "PayrollGroup",
            "PaymentMethod", "IBAN", "AccountNumber", "BankName", "BankRoutingCode", "MolId",
            // GCC/statutory identity columns stored on the employee master record.
            "PassportNumber", "PassportIssueDate", "PassportExpiryDate", "VisaNumber", "VisaIssueDate",
            "VisaExpiryDate", "IqamaNumber", "MuqeemNumber", "GosiReference", "EmiratesId", "LaborCardNumber",
            "VisaFileNumber", "Qid", "CivilId", "ResidencyNumber", "ResidencyIssueDate", "WorkPermitNumber",
            "WorkPermitIssueDate", "SponsorName", "SaudiOrNonSaudi", "IdType", "IdNumber", "OccupationCode",
            "EstablishmentId", "WorkLocationId", "ContractReference", "WorkPermitReference", "QiwaEmployeeReference",
            "QiwaSyncStatus"
        };

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
        var rows = emps.Select(e =>
        {
            profiles.TryGetValue(e.Id, out var profile);
            salaries.TryGetValue(e.Id, out var salary);
            var structureCode = salary is not null && structures.TryGetValue(salary.SalaryStructureId, out var structure) ? structure.Code : string.Empty;
            return (IReadOnlyList<object?>)new object?[]
        {
            e.EmployeeCode, string.Empty, string.Empty, e.CostCenter, e.WorkLocation,
            e.FullName, e.ArabicName, e.PreferredName, e.WorkEmail, e.PersonalEmail, e.Phone, e.Gender,
            e.DateOfBirth, e.Nationality, e.MaritalStatus, e.CountryCode,
            e.Department, string.Empty, e.Designation, e.JobTitle, e.EmploymentType, e.ContractType,
            e.Grade, e.PositionId is not null && positionCodes.TryGetValue(e.PositionId.Value, out var positionCode) ? positionCode : string.Empty, e.Status, e.JoiningDate.ToString("yyyy-MM-dd"),
            e.ConfirmationDate, e.ProbationStartDate, e.ProbationEndDate, e.NoticePeriodDays, e.ShiftPolicyCode, e.LeavePolicyCode, e.AttendancePolicyCode,
            string.Empty, string.Empty,
            structureCode, salary?.BasicSalary, salary?.HousingAllowance, salary?.TransportAllowance,
            salary?.FoodAllowance, salary?.MobileAllowance, salary?.OtherAllowance, salary?.FixedDeduction,
            salary?.Currency ?? profile?.SalaryCurrency, profile?.PayrollGroup, profile?.PaymentMethod,
            profile?.Iban, profile?.AccountNumber, profile?.BankName, profile?.BankRoutingCode, profile?.MolId,
            e.PassportNumber, e.PassportIssueDate, e.PassportExpiryDate, e.VisaNumber, e.VisaIssueDate,
            e.VisaExpiryDate, e.IqamaNumber, e.MuqeemNumber, e.GosiReference, e.EmiratesId, e.LaborCardNumber,
            e.VisaFileNumber, e.Qid, e.CivilId, e.ResidencyNumber, e.ResidencyIssueDate, e.WorkPermitNumber,
            e.WorkPermitIssueDate, e.SponsorName, e.SaudiOrNonSaudi, e.IdType, e.IdNumber, e.OccupationCode,
            e.EstablishmentId, e.WorkLocationId, e.ContractReference, e.WorkPermitReference, e.QiwaEmployeeReference,
            e.QiwaSyncStatus
        };
        });
        var csv = Csv.Build(EmployeeCsvHeaders, rows);
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

    /// <summary>Downloadable blank template — the shareable "data format" to fill and import.</summary>
    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate() =>
        File(Encoding.UTF8.GetBytes(Csv.Template(EmployeeCsvHeaders)), "text/csv", "employees_import_template.csv");

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] ImportEmployeesRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);

        // Enforce employee limit before processing any rows.
        var sub = await _db.TenantSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        if (sub is not null && sub.MaxEmployees > 0)
        {
            var current = await _db.Employees.CountAsync(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted, ct);
            var available = sub.MaxEmployees - current;
            if (rows.Count > available)
                return UnprocessableEntity(new
                {
                    error = "employee_limit_reached",
                    message = $"Import would exceed your employee limit ({current} active / {sub.MaxEmployees} max). You have {available} seat(s) remaining — remove {rows.Count - available} row(s) and retry.",
                    current,
                    limit = sub.MaxEmployees,
                    available,
                    rowsInFile = rows.Count
                });
        }

        var companiesByName = await _db.Companies
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .ToDictionaryAsync(c => c.LegalNameEn.ToUpperInvariant(), ct);
        var defaultCompany = companiesByName.Values.OrderBy(c => c.CreatedAtUtc).FirstOrDefault();
        var branchesByCode = (await _db.Branches
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.IsActive && !b.IsDeleted)
            .ToListAsync(ct))
            .GroupBy(b => (b.CompanyId, Code: b.Code.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).First());
        var branchesByCompany = branchesByCode.Values
            .GroupBy(b => b.CompanyId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).ToList());
        var costCentersByCode = (await _db.CostCenters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .ToListAsync(ct))
            .GroupBy(c => (c.CompanyId, Code: c.Code.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).First());

        // Pre-load department and designation lookups for FK resolution
        var deptByCode = await _db.Departments
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.Code.ToUpperInvariant(), d => d.Id, ct);
        var deptByName = await _db.Departments
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.NameEn.ToLowerInvariant(), d => d.Id, ct);
        var desigByTitle = await _db.Designations
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.TitleEn.ToLowerInvariant(), d => new { d.Id, d.GradeId, d.TitleEn }, ct);
        var gradeByCode = await _db.Grades
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId && !g.IsDeleted)
            .ToDictionaryAsync(g => g.Code.ToUpperInvariant(), g => g, ct);
        var gradeByName = await _db.Grades
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId && !g.IsDeleted)
            .ToDictionaryAsync(g => g.Name.ToLowerInvariant(), g => g, ct);
        var positionsByCode = await _db.Positions
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Code.ToUpperInvariant(), ct);

        int created = 0, skipped = 0;
        var errors = new List<string>();
        var rowNum = 1;
        // Track employee codes created in this batch for Pass 2 resolution
        var batchCodes = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
        var batchPayroll = new Dictionary<string, (Employee emp, Dictionary<string, string> rowData)>(StringComparer.OrdinalIgnoreCase);
        var claimedPositionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Pass 1: create all employee records ──────────────────────────────────
        foreach (var row in rows)
        {
            rowNum++;
            var name = row.GetValueOrDefault("FullName", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; }
            var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(code) && await _db.Employees.AnyAsync(e => e.TenantId == tenantId && e.EmployeeCode == code, ct))
            { skipped++; errors.Add($"Row {rowNum}: EmployeeCode '{code}' already exists."); continue; }

            DateTime.TryParse(row.GetValueOrDefault("JoiningDate", string.Empty), out var jdRaw);
            var jd = DateTime.SpecifyKind(jdRaw == default ? DateTime.UtcNow : jdRaw, DateTimeKind.Utc);
            var statusVal = row.GetValueOrDefault("Status", string.Empty).Trim();
            var companyNameRaw = row.GetValueOrDefault("CompanyLegalName", string.Empty).Trim();
            var branchCodeRaw = row.GetValueOrDefault("BranchCode", string.Empty).Trim().ToUpperInvariant();
            var costCenterCodeRaw = row.GetValueOrDefault("CostCenterCode", string.Empty).Trim().ToUpperInvariant();
            var resolvedCompany = !string.IsNullOrWhiteSpace(companyNameRaw)
                ? companiesByName.GetValueOrDefault(companyNameRaw.ToUpperInvariant())
                : defaultCompany;
            if (!string.IsNullOrWhiteSpace(companyNameRaw) && resolvedCompany is null)
            {
                skipped++;
                errors.Add($"Row {rowNum}: CompanyLegalName '{companyNameRaw}' not found.");
                continue;
            }
            var resolvedBranch = resolvedCompany is not null
                ? !string.IsNullOrWhiteSpace(branchCodeRaw)
                    ? branchesByCode.GetValueOrDefault((resolvedCompany.Id, branchCodeRaw))
                    : branchesByCompany.GetValueOrDefault(resolvedCompany.Id)?.FirstOrDefault()
                : null;
            if (!string.IsNullOrWhiteSpace(branchCodeRaw) && resolvedBranch is null)
            {
                skipped++;
                errors.Add(resolvedCompany is null
                    ? $"Row {rowNum}: BranchCode '{branchCodeRaw}' supplied but no active company could be resolved."
                    : $"Row {rowNum}: BranchCode '{branchCodeRaw}' not found for company '{resolvedCompany.LegalNameEn}'.");
                continue;
            }
            var resolvedCostCenter = resolvedCompany is not null && !string.IsNullOrWhiteSpace(costCenterCodeRaw)
                ? costCentersByCode.GetValueOrDefault((resolvedCompany.Id, costCenterCodeRaw))
                : null;
            if (!string.IsNullOrWhiteSpace(costCenterCodeRaw) && resolvedCostCenter is null)
            {
                skipped++;
                errors.Add(resolvedCompany is null
                    ? $"Row {rowNum}: CostCenterCode '{costCenterCodeRaw}' supplied but no active company could be resolved."
                    : $"Row {rowNum}: CostCenterCode '{costCenterCodeRaw}' not found for company '{resolvedCompany.LegalNameEn}'.");
                continue;
            }
            var deptNameRaw = row.GetValueOrDefault("Department", string.Empty).Trim();
            var deptCodeRaw = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim().ToUpperInvariant();

            Guid? resolvedDeptId = null;
            if (!string.IsNullOrEmpty(deptCodeRaw) && deptByCode.TryGetValue(deptCodeRaw, out var dId1))
                resolvedDeptId = dId1;
            else if (!string.IsNullOrEmpty(deptNameRaw) && deptByName.TryGetValue(deptNameRaw.ToLowerInvariant(), out var dId2))
                resolvedDeptId = dId2;

            var desigTitleRaw = row.GetValueOrDefault("Designation", string.Empty).Trim();
            Guid? resolvedDesigId = null;
            Guid? designationGradeId = null;
            if (!string.IsNullOrEmpty(desigTitleRaw) && desigByTitle.TryGetValue(desigTitleRaw.ToLowerInvariant(), out var designation))
            {
                resolvedDesigId = designation.Id;
                designationGradeId = designation.GradeId;
            }

            var gradeRaw = row.GetValueOrDefault("Grade", string.Empty).Trim();
            Grade? resolvedGrade = null;
            if (!string.IsNullOrWhiteSpace(gradeRaw))
            {
                if (!gradeByCode.TryGetValue(gradeRaw.ToUpperInvariant(), out resolvedGrade))
                    gradeByName.TryGetValue(gradeRaw.ToLowerInvariant(), out resolvedGrade);
                if (resolvedGrade is null)
                {
                    skipped++;
                    errors.Add($"Row {rowNum}: Grade '{gradeRaw}' not found.");
                    continue;
                }
            }
            if (designationGradeId is not null && resolvedGrade is not null && designationGradeId != resolvedGrade.Id)
            {
                skipped++;
                errors.Add($"Row {rowNum}: Designation '{desigTitleRaw}' is not eligible for grade '{resolvedGrade.Code}'.");
                continue;
            }
            var finalGradeId = resolvedGrade?.Id ?? designationGradeId;
            var finalGrade = resolvedGrade ?? (finalGradeId is not null ? gradeByCode.Values.FirstOrDefault(g => g.Id == finalGradeId) : null);
            var finalGradeCode = finalGrade?.Code ?? string.Empty;
            var positionCodeRaw = row.GetValueOrDefault("PositionCode", string.Empty).Trim().ToUpperInvariant();
            Position? position = null;
            if (!string.IsNullOrWhiteSpace(positionCodeRaw))
            {
                if (!positionsByCode.TryGetValue(positionCodeRaw, out position))
                {
                    skipped++; errors.Add($"Row {rowNum}: PositionCode '{positionCodeRaw}' not found."); continue;
                }
                if (position.Status is PositionStatuses.Frozen or PositionStatuses.Closed || position.IncumbentEmployeeId is not null || !claimedPositionCodes.Add(positionCodeRaw))
                {
                    skipped++; errors.Add($"Row {rowNum}: PositionCode '{positionCodeRaw}' is not available for assignment."); continue;
                }
                if ((position.CompanyId is not null && position.CompanyId != resolvedCompany?.Id) ||
                    (position.BranchId is not null && position.BranchId != resolvedBranch?.Id) ||
                    (position.DepartmentId is not null && position.DepartmentId != resolvedDeptId) ||
                    (position.DesignationId is not null && position.DesignationId != resolvedDesigId) ||
                    (position.GradeId is not null && position.GradeId != finalGradeId))
                {
                    skipped++; errors.Add($"Row {rowNum}: PositionCode '{positionCodeRaw}' is not eligible for the supplied organization, designation, or grade."); continue;
                }
            }
            var grossSalary = GrossSalaryFromRow(row);
            if (grossSalary > 0 && finalGrade is null)
            {
                skipped++;
                errors.Add($"Row {rowNum}: Salary package requires a valid grade so salary eligibility can be enforced.");
                continue;
            }
            if (finalGrade is not null && grossSalary > 0 && ((finalGrade.MinSalary > 0 && grossSalary < finalGrade.MinSalary) || (finalGrade.MaxSalary > 0 && grossSalary > finalGrade.MaxSalary)))
            {
                skipped++;
                errors.Add($"Row {rowNum}: Salary package {grossSalary:N2} is outside grade {finalGrade.Code} range {finalGrade.MinSalary:N2}-{finalGrade.MaxSalary:N2}.");
                continue;
            }

            var finalCode = string.IsNullOrWhiteSpace(code) ? await GenerateEmployeeCode(tenantId, ct) : code;
            var employee = new Employee
            {
                TenantId = tenantId,
                CompanyId = resolvedCompany?.Id,
                BranchId = resolvedBranch?.Id,
                CostCenterId = resolvedCostCenter?.Id,
                EmployeeCode = finalCode,
                FullName = name,
                EnglishName = name,
                ArabicName = row.GetValueOrDefault("ArabicName", string.Empty),
                PreferredName = row.GetValueOrDefault("PreferredName", string.Empty),
                PersonalEmail = row.GetValueOrDefault("PersonalEmail", string.Empty),
                WorkEmail = row.GetValueOrDefault("WorkEmail", string.Empty),
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
                PositionId = position?.Id,
                Grade = finalGradeCode,
                JobTitle = row.GetValueOrDefault("JobTitle", desigTitleRaw),
                EmploymentType = row.GetValueOrDefault("EmploymentType", "Full-time"),
                ContractType = row.GetValueOrDefault("ContractType", string.Empty),
                Status = string.IsNullOrWhiteSpace(statusVal) ? "Active" : statusVal,
                JoiningDate = jd == default ? DateTime.UtcNow : jd,
                ConfirmationDate = ReadCsvDate(row, "ConfirmationDate"),
                ProbationStartDate = ReadCsvDate(row, "ProbationStartDate"),
                ProbationEndDate = ReadCsvDate(row, "ProbationEndDate"),
                NoticePeriodDays = ReadCsvInt(row, "NoticePeriodDays"),
                Branch = resolvedBranch?.NameEn ?? string.Empty,
                CostCenter = resolvedCostCenter?.Code ?? string.Empty,
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
            };
            _db.Employees.Add(employee);
            batchCodes[finalCode] = employee;
            batchPayroll[finalCode] = (employee, row);
            created++;
        }
        await _db.SaveChangesAsync(ct);
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
            await _db.SaveChangesAsync(ct);
        }

        // ── Pass 1b: payroll profiles + salary structures ────────────────────────
        int payrollProfilesCreated = 0;
        foreach (var (_, (emp, rowData)) in batchPayroll)
        {
            var ibanRaw = rowData.GetValueOrDefault("IBAN", string.Empty).Trim();
            var bankNameRaw = rowData.GetValueOrDefault("BankName", string.Empty).Trim();
            var molIdRaw = rowData.GetValueOrDefault("MolId", string.Empty).Trim();
            var accountRaw = rowData.GetValueOrDefault("AccountNumber", string.Empty).Trim();
            var routingRaw = rowData.GetValueOrDefault("BankRoutingCode", string.Empty).Trim();
            var payrollGroupRaw = rowData.GetValueOrDefault("PayrollGroup", string.Empty).Trim();
            var paymentMethodRaw = rowData.GetValueOrDefault("PaymentMethod", string.Empty).Trim();
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

            var grade = emp.GradeId is not null ? gradeByCode.Values.FirstOrDefault(g => g.Id == emp.GradeId) : null;

            bool hasPayroll = !string.IsNullOrEmpty(ibanRaw) || !string.IsNullOrEmpty(bankNameRaw) ||
                              !string.IsNullOrEmpty(molIdRaw) || gross > 0;
            if (!hasPayroll) continue;

            _db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = emp.Id,
                BankName = bankNameRaw, Iban = ibanRaw, SalaryCurrency = currency,
                AccountNumber = accountRaw, BankRoutingCode = routingRaw,
                PaymentMethod = string.IsNullOrWhiteSpace(paymentMethodRaw) ? "BankTransfer" : paymentMethodRaw,
                PayrollGroup = payrollGroupRaw, SalaryStructureReference = structureCodeRaw,
                MolId = molIdRaw, WpsEligible = true, EosbEligible = true, CreatedBy = GetUserId()
            });

            if (gross > 0)
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
        if (payrollProfilesCreated > 0)
            await _db.SaveChangesAsync(ct);

        // ── Pass 2: resolve manager/supervisor codes → IDs ────────────────────────
        int hierarchyLinked = 0;
        var hierarchyErrors = new List<string>();
        rowNum = 1;
        // Re-load all tenant employees (includes just-created ones)
        var allByCode = await _db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .ToDictionaryAsync(e => e.EmployeeCode.ToUpperInvariant(), ct);

        foreach (var row in rows)
        {
            rowNum++;
            var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code) || !batchCodes.TryGetValue(code, out var emp)) continue; // skipped or auto-generated in pass 1

            var mgrCode = row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim();
            var supCode  = row.GetValueOrDefault("SupervisorEmployeeCode", string.Empty).Trim();

            bool changed = false;

            if (!string.IsNullOrEmpty(mgrCode))
            {
                if (!allByCode.TryGetValue(mgrCode.ToUpperInvariant(), out var mgr))
                    hierarchyErrors.Add($"Row {rowNum}: ManagerEmployeeCode '{mgrCode}' not found — hierarchy skipped.");
                else if (mgr.Id == emp.Id)
                    hierarchyErrors.Add($"Row {rowNum}: Employee cannot be their own manager — hierarchy skipped.");
                else
                {
                    // Validate no circular chain would be created
                    bool circular = false;
                    var visited = new HashSet<int> { emp.Id };
                    var cursor = (int?)mgr.Id;
                    for (int depth = 0; cursor.HasValue && depth < 50; depth++)
                    {
                        if (visited.Contains(cursor.Value)) { circular = true; break; }
                        visited.Add(cursor.Value);
                        cursor = allByCode.Values.FirstOrDefault(e => e.Id == cursor.Value)?.ManagerEmployeeId;
                    }

                    if (circular)
                        hierarchyErrors.Add($"Row {rowNum}: Setting '{mgrCode}' as manager of '{code}' would create a circular hierarchy — skipped.");
                    else
                    {
                        emp.ManagerEmployeeId = mgr.Id;
                        _db.ReportingLines.Add(new ReportingLine
                        {
                            TenantId = tenantId,
                            EmployeeId = emp.Id,
                            ManagerEmployeeId = mgr.Id,
                            RelationshipType = "SolidLine",
                            EffectiveFrom = emp.JoiningDate,
                            IsPrimary = true,
                            IsActive = true
                        });
                        changed = true;
                        hierarchyLinked++;
                    }
                }
            }

            if (!string.IsNullOrEmpty(supCode))
            {
                if (!allByCode.TryGetValue(supCode.ToUpperInvariant(), out var sup))
                    hierarchyErrors.Add($"Row {rowNum}: SupervisorEmployeeCode '{supCode}' not found — skipped.");
                else if (sup.Id != emp.Id)
                {
                    emp.SupervisorEmployeeId = sup.Id;
                    _db.ReportingLines.Add(new ReportingLine
                    {
                        TenantId = tenantId,
                        EmployeeId = emp.Id,
                        ManagerEmployeeId = sup.Id,
                        RelationshipType = "DottedLine",
                        EffectiveFrom = emp.JoiningDate,
                        IsPrimary = false,
                        IsActive = true
                    });
                    changed = true;
                }
            }

            if (changed) _db.Employees.Update(emp);
        }
        if (hierarchyLinked > 0 || hierarchyErrors.Count > 0)
            await _db.SaveChangesAsync(ct);

        var allErrors = errors.Concat(hierarchyErrors).Take(30).ToList();
        return Ok(new { received = rows.Count, created, skipped, hierarchyLinked, payrollProfilesCreated, errors = allErrors });
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

    [HttpPost("import-preview")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ImportPreview([FromBody] ImportEmployeesRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);

        var sub = await _db.TenantSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        int currentCount = await _db.Employees
            .CountAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active, ct);
        int remaining = sub is not null && sub.MaxEmployees > 0 ? sub.MaxEmployees - currentCount : int.MaxValue;

        var deptByCode = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && d.IsActive)
            .ToDictionaryAsync(d => d.Code.ToUpperInvariant(), ct);
        var deptByName = await _db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && d.IsActive)
            .ToDictionaryAsync(d => d.NameEn.ToUpperInvariant(), ct);
        var desigByTitle = await _db.Designations.AsNoTracking().Where(d => d.TenantId == tenantId && d.IsActive)
            .ToDictionaryAsync(d => d.TitleEn.ToUpperInvariant(), ct);
        var existingCodesList = await _db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .Select(e => e.EmployeeCode.ToUpperInvariant())
            .ToListAsync(ct);
        var existingCodes = new HashSet<string>(existingCodesList);

        // Pre-pass: collect all batch code→managerCode so circular detection works even when
        // both employee and manager are new in the same import batch.
        var batchManagerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var c = r.GetValueOrDefault("EmployeeCode", string.Empty).Trim().ToUpperInvariant();
            var m = r.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(c) && !string.IsNullOrEmpty(m))
                batchManagerMap[c] = m;
        }

        var previewRows = new List<object>();
        var seen = new HashSet<string>();
        int wouldCreate = 0, wouldSkip = 0;

        int rowNum = 1;
        foreach (var row in rows)
        {
            rowNum++;
            var code = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            var name = row.GetValueOrDefault("FullName", string.Empty).Trim();
            var mgrCode = row.GetValueOrDefault("ManagerEmployeeCode", string.Empty).Trim();
            var supCode = row.GetValueOrDefault("SupervisorEmployeeCode", string.Empty).Trim();

            var rowWarnings = new List<string>();
            var rowErrors = new List<string>();
            string status;

            if (!string.IsNullOrEmpty(code) && (existingCodes.Contains(code.ToUpperInvariant()) || seen.Contains(code.ToUpperInvariant())))
            { rowErrors.Add($"Duplicate EmployeeCode '{code}'"); }

            if (!string.IsNullOrEmpty(mgrCode))
            {
                var mgrUpper = mgrCode.ToUpperInvariant();
                var codeUpper = code.ToUpperInvariant();

                if (!string.IsNullOrEmpty(code) && mgrCode.Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    rowErrors.Add("Employee cannot be their own manager");
                }
                else
                {
                    bool mgrKnown = existingCodes.Contains(mgrUpper) || seen.Contains(mgrUpper) || batchManagerMap.ContainsKey(mgrUpper);
                    if (!mgrKnown)
                        rowWarnings.Add($"ManagerEmployeeCode '{mgrCode}' not found — hierarchy will be skipped");

                    if (!string.IsNullOrEmpty(code))
                    {
                        // Circular check using the full batch map — detects A→B→A even when both are new
                        var visited2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { codeUpper };
                        var cursor2 = mgrUpper;
                        bool circular2 = false;
                        for (int d = 0; d < 50; d++)
                        {
                            if (!visited2.Add(cursor2)) { circular2 = true; break; }
                            if (batchManagerMap.TryGetValue(cursor2, out var next)) cursor2 = next;
                            else break;
                        }
                        if (circular2) rowErrors.Add($"Setting '{mgrCode}' as manager would create a circular hierarchy");
                    }
                }
            }

            if (!string.IsNullOrEmpty(supCode) && !existingCodes.Contains(supCode.ToUpperInvariant()) && !seen.Contains(supCode.ToUpperInvariant()))
                rowWarnings.Add($"SupervisorEmployeeCode '{supCode}' not found — will be skipped");

            var deptColCode = row.GetValueOrDefault("DepartmentCode", string.Empty).Trim().ToUpperInvariant();
            var deptColName = row.GetValueOrDefault("Department", string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(deptColCode) && !deptByCode.ContainsKey(deptColCode))
                rowWarnings.Add($"DepartmentCode '{deptColCode}' not found — will be unlinked");
            else if (!string.IsNullOrEmpty(deptColName) && !deptByName.ContainsKey(deptColName) && string.IsNullOrEmpty(deptColCode))
                rowWarnings.Add($"Department '{row.GetValueOrDefault("Department", "")}' not found — will be unlinked");

            var desigCol = row.GetValueOrDefault("Designation", string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(desigCol) && !desigByTitle.ContainsKey(desigCol))
                rowWarnings.Add($"Designation '{row.GetValueOrDefault("Designation", "")}' not found — will be unlinked");

            var ibanPreview = row.GetValueOrDefault("IBAN", string.Empty).Trim();
            if (!string.IsNullOrEmpty(ibanPreview) && !IsValidIbanFormat(ibanPreview))
                rowWarnings.Add($"IBAN '{ibanPreview}' format looks invalid — will be stored as-is but may fail WPS validation");
            var basicSalaryPreview = row.GetValueOrDefault("BasicSalary", string.Empty).Trim();
            if (!string.IsNullOrEmpty(basicSalaryPreview) && !decimal.TryParse(basicSalaryPreview, out _))
                rowWarnings.Add($"BasicSalary '{basicSalaryPreview}' is not a valid number — salary will not be imported");

            bool hasErrors = rowErrors.Count > 0;
            if (!hasErrors)
            {
                if (wouldCreate >= remaining)
                {
                    rowErrors.Add($"Subscription limit reached ({sub?.MaxEmployees} employees)");
                    hasErrors = true;
                }
            }

            if (hasErrors) { status = "Error"; wouldSkip++; }
            else
            {
                status = "WillCreate";
                wouldCreate++;
                if (!string.IsNullOrWhiteSpace(code)) seen.Add(code.ToUpperInvariant());
            }

            previewRows.Add(new
            {
                row = rowNum,
                employeeCode = code,
                fullName = name,
                status,
                errors = rowErrors,
                warnings = rowWarnings
            });
        }

        return Ok(new
        {
            received = rows.Count,
            wouldCreate,
            wouldSkip,
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("drafts")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<EmployeeDetailDto>> ApproveDraft(Guid draftId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var draft = await FindDraft(draftId, cancellationToken);
        if (draft is null) return NotFound();
        if (draft.Status != "PendingHrApproval" && draft.Status != "Draft") return BadRequest(new { message = "Draft is not ready for HR approval." });

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
            Department = draft.Department,
            Designation = draft.Designation,
            WorkLocation = draft.WorkLocation,
            Branch = draft.Branch,
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
            await _db.SaveChangesAsync(cancellationToken);
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
        employee.UpdatedAtUtc = DateTime.UtcNow;
        await AddHistory(employee, "Updated", request.EffectiveDate, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.updated", "Employee", employee.Id.ToString(), cancellationToken);
        return Ok(EmployeeDetailDto.Project(employee, CanViewSensitive()));
    }

    [HttpPatch("{id:int}/status")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<EmployeeDetailDto>> ChangeStatus(int id, EmployeeStatusChangeRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        try
        {
            var employee = await employeeManagement.ChangeStatusAsync(RequireTenant(), id, request, Context(), cancellationToken);
            return employee is null ? NotFound() : Ok(employee);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{id:int}/documents")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<EmployeeDocumentDto>> UpdateEmployeeDocument(int id, Guid docId, [FromBody] UpdateDocumentMetadataRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var doc = await employeeManagement.UpdateDocumentAsync(RequireTenant(), id, docId, request, Context(), cancellationToken);
        return doc is null ? NotFound() : Ok(EmployeeDocumentDto.Project(doc));
    }

    [HttpPost("{id:int}/documents/{docId:guid}/verify")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<EmployeeDocumentDto>> VerifyEmployeeDocument(int id, Guid docId, [FromBody] DocumentVerifyRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var doc = await employeeManagement.VerifyDocumentAsync(RequireTenant(), id, docId, request.Notes, Context(), cancellationToken);
        return doc is null ? NotFound() : Ok(EmployeeDocumentDto.Project(doc));
    }

    [HttpPost("{id:int}/documents/{docId:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<EmployeeDocumentDto>> RejectEmployeeDocument(int id, Guid docId, [FromBody] DocumentRejectRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var doc = await employeeManagement.RejectDocumentAsync(RequireTenant(), id, docId, request.Reason, Context(), cancellationToken);
        return doc is null ? NotFound() : Ok(EmployeeDocumentDto.Project(doc));
    }

    [HttpDelete("{id:int}/documents/{docId:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
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
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<EmployeeDetailDto>> Activate(int id, EmployeeStatusChangeRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var employee = await employeeManagement.ActivateAsync(RequireTenant(), id, request, Context(), cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpPost("{id:int}/terminate")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<ActionResult<EmployeeDetailDto>> Terminate(int id, EmployeeStatusChangeRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var employee = await employeeManagement.TerminateAsync(RequireTenant(), id, request, Context(), cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    /// <summary>Soft-deletes an employee record (audit trail preserved; hidden from all lists).</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound();

        var context = Context();
        var deletedAt = DateTime.UtcNow;
        employee.IsDeleted = true;
        employee.DeletedAtUtc = deletedAt;
        employee.DeletedBy = context.UserId;
        employee.Status = "Inactive";
        employee.PrivacyStatus = "RetainedForStatutoryAudit";
        employee.RetentionUntilUtc = deletedAt.AddYears(7);

        var pendingChanges = await _db.EmployeeChangeRequests
            .Where(x => x.TenantId == tenantId && x.EmployeeId == id && x.Status == "PendingApproval")
            .ToListAsync(cancellationToken);
        foreach (var change in pendingChanges)
        {
            change.Status = "Cancelled";
            change.RejectionReason = "Employee record was deleted before approval.";
            change.ApprovedAtUtc = deletedAt;
        }

        var pendingChangeIds = pendingChanges.Select(x => x.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingApprovals = await _db.ApprovalRequests
            .Where(x => x.TenantId == tenantId
                && x.Status == "Pending"
                && (x.RequestedForEmployeeId == id
                    || (x.EntityName == nameof(EmployeeChangeRequest) && pendingChangeIds.Contains(x.EntityId))))
            .ToListAsync(cancellationToken);
        foreach (var approval in pendingApprovals)
        {
            approval.Status = "Cancelled";
            approval.CompletedAtUtc = deletedAt;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employees.deleted", "Employee", id.ToString(), context, JsonSerializer.Serialize(new
        {
            employee.PrivacyStatus,
            employee.RetentionUntilUtc,
            CancelledApprovalRequests = pendingApprovals.Count
        }), cancellationToken);
        return NoContent();
    }

    [HttpPost("changes/{changeId:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager")]
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
        ApplyChanges(employee, changes);
        employee.UpdatedAtUtc = DateTime.UtcNow;
        change.Status = "ApprovedApplied";
        change.ApprovedByUserId = approverId;
        change.ApprovedAtUtc = DateTime.UtcNow;
        change.AppliedAtUtc = DateTime.UtcNow;
        await AddHistory(employee, "SensitiveChangeApproved", change.EffectiveDate, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await Audit("employee.change_approved", "EmployeeChangeRequest", change.Id.ToString(), cancellationToken);
        return Ok(EmployeeDetailDto.Project(employee, CanViewSensitive()));
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
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<ActionResult<EmployeeTransferDto>> RequestTransfer(int id, EmployeeTransferCreateRequest request, [FromServices] IEmployeeManagementService employeeManagement, CancellationToken cancellationToken)
    {
        var transfer = await employeeManagement.RequestTransferAsync(RequireTenant(), id, request, Context(), cancellationToken);
        if (transfer is null) return NotFound();
        return Created($"/api/employees/transfers/{transfer.Id}", EmployeeTransferDto.Project(transfer));
    }

    [HttpPost("transfers/{transferId:guid}/approve-current-manager")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public Task<IActionResult> ApproveCurrentManager(Guid transferId, CancellationToken cancellationToken) =>
        AdvanceTransfer(transferId, "PendingCurrentManager", "PendingNewManager", x => x.CurrentManagerEmployeeId, x => x.CurrentManagerApprovedAtUtc = DateTime.UtcNow, cancellationToken);

    [HttpPost("transfers/{transferId:guid}/approve-new-manager")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public Task<IActionResult> ApproveNewManager(Guid transferId, CancellationToken cancellationToken) =>
        AdvanceTransfer(transferId, "PendingNewManager", "PendingHrApproval", x => x.NewManagerEmployeeId, x => x.NewManagerApprovedAtUtc = DateTime.UtcNow, cancellationToken);

    [HttpPost("transfers/{transferId:guid}/approve-hr")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ApproveHrTransfer(Guid transferId, [FromServices] IHrmHierarchyService hierarchy, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var transfer = await _db.EmployeeTransferRequests.FirstOrDefaultAsync(x => x.Id == transferId && x.TenantId == tenantId, cancellationToken);
        if (transfer is null) return NotFound();
        if (transfer.Status != "PendingHrApproval")
            return BadRequest(new { message = $"Transfer is in '{transfer.Status}' status and cannot be HR-approved." });
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.Id == transfer.EmployeeId && x.TenantId == tenantId, cancellationToken);
        if (employee is null) return NotFound();
        employee.Department = transfer.NewDepartment;
        employee.Branch = transfer.NewBranch;
        employee.UpdatedAtUtc = DateTime.UtcNow;
        if (employee.ManagerEmployeeId != transfer.NewManagerEmployeeId)
            await hierarchy.SetManagerAsync(tenantId, employee.Id, transfer.NewManagerEmployeeId, Context(), cancellationToken);
        transfer.Status = "ApprovedApplied";
        transfer.HrApprovedAtUtc = DateTime.UtcNow;
        await AddHistory(employee, "TransferApproved", transfer.EffectiveDate, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await Notify("Employee transfer approved", $"Transfer for employee {employee.EmployeeCode} was applied.", "EmployeeTransferRequest", transfer.Id.ToString(), cancellationToken);
        await Audit("employee.transfer_approved", "EmployeeTransferRequest", transfer.Id.ToString(), cancellationToken);
        return Ok(EmployeeDetailDto.Project(employee, CanViewSensitive()));
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

    private EmployeeListItemDto ToListItem(Employee employee) => new(employee.Id, employee.EmployeeCode, employee.FullName, employee.ArabicName, employee.Department, employee.Designation, employee.Branch, employee.ManagerEmployeeId, employee.Status, employee.ProfileCompletenessScore, employee.VisaExpiryDate, employee.PassportExpiryDate);
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

public record EmployeeListItemDto(int Id, string EmployeeCode, string FullName, string ArabicName, string Department, string Designation, string Branch, int? ManagerEmployeeId, string Status, decimal ProfileCompletenessScore, DateOnly? VisaExpiryDate, DateOnly? PassportExpiryDate);
public record EmployeeDocumentRequest(string DocumentType, string FileName, string ContentType, string StorageUrl, bool IsRequired, DateOnly? ExpiryDate);
public record EmployeeTransferRequestDto(string NewDepartment, string NewBranch, int? NewManagerEmployeeId, DateOnly EffectiveDate);
public record EmployeeUpdateRequest(DateOnly EffectiveDate, Dictionary<string, JsonElement> Changes);
public record GroupCountDto(string Name, int Count);
public record EmployeeReportsDto(int TotalHeadcount, int ActiveEmployees, int NewJoiners, int Exits, int ProbationEmployees, IReadOnlyCollection<GroupCountDto> DepartmentHeadcount, IReadOnlyCollection<GroupCountDto> BranchHeadcount, IReadOnlyCollection<GroupCountDto> NationalityMix, IReadOnlyCollection<GroupCountDto> GenderMix, int ContractExpiringSoon, int VisaOrPassportExpiringSoon, int MissingDocumentsOrIncompleteProfiles);
public record EmployeeAiResponseDto(string Answer, IReadOnlyCollection<EmployeeListItemDto> Employees);
public record EmployeeDraftRequest(string? CurrentStep, string? EnglishName, string? ArabicName, string? PersonalEmail, string? WorkEmail, string? Phone, string? Gender, DateOnly? DateOfBirth, string? MaritalStatus, string? EmergencyContactName, string? EmergencyContactPhone, string? Nationality, string? CountryCode, string? Department, string? Designation, string? Branch, string? WorkLocation, int? ManagerEmployeeId, DateTime? JoiningDate, string? ContractType, string? Grade, string? CostCenter, DateOnly? ContractStartDate, DateOnly? ContractEndDate, DateOnly? ProbationEndDate, string? PayrollProfileCode, decimal? Salary, string? BankName, string? BankIban, string? WpsBankDetails, string? ShiftPolicyCode, string? LeavePolicyCode, string? SponsorName, DateOnly? PassportIssueDate, string? PassportNumber, DateOnly? PassportExpiryDate, DateOnly? VisaIssueDate, string? VisaNumber, DateOnly? VisaExpiryDate, string? IqamaNumber, string? MuqeemNumber, string? GosiReference, string? QiwaContractNumber, string? EmiratesId, string? LaborCardNumber, string? VisaFileNumber, string? Qid, string? WorkPermitNumber, DateOnly? WorkPermitIssueDate, string? CivilId, string? ResidencyNumber, DateOnly? ResidencyIssueDate);
public record EmployeeDocumentUploadRequest(string DocumentType, bool IsRequired, DateOnly? ExpiryDate, IFormFile File);
public record EmployeeTemplateDto(string TemplateType, string Language, string Title, string Body);
