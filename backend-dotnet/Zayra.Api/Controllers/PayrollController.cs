using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Governance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/payroll")]
// Mutations move to permission gates ([HasPermission] per action below); the controller-level gate
// is relaxed to "authenticated" so a custom payroll role granted the right key actually gets in.
// Reads keep their exact original role list (re-applied per GET method) — deferred to a later module.
// WPS/payment/ERP-export mutations retain their in-body HasPermission("payroll.export") gate unchanged.
[Authorize]
public class PayrollController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    private readonly IHttpContextAccessor _http;
    private readonly INotificationService _notifications;
    private readonly ICountryPackResolver _packResolver;
    private readonly IStatutoryRuleReader _ruleReader;
    private readonly ILetterService _letters;
    private readonly IDocumentStorage _storage;
    private readonly PdfRenderGate _pdfGate;
    private readonly ICompanyTaxPolicyResolver _taxResolver;
    private readonly Zayra.Api.Infrastructure.Employees.IEmployeeActivationGuard _activationGuard;

    public PayrollController(ZayraDbContext db, IDataScopeService scopeService, IHttpContextAccessor http,
        INotificationService notifications, ICountryPackResolver packResolver, IStatutoryRuleReader ruleReader,
        ILetterService letters, IDocumentStorage storage, PdfRenderGate pdfGate,
        ICompanyTaxPolicyResolver? taxResolver = null,
        Zayra.Api.Infrastructure.Employees.IEmployeeActivationGuard? activationGuard = null)
    {
        _db = db;
        _scopeService = scopeService;
        _http = http;
        _notifications = notifications;
        _packResolver = packResolver;
        _ruleReader = ruleReader;
        _letters = letters;
        _storage = storage;
        _pdfGate = pdfGate;
        // Optional so existing test constructors keep working; DI always supplies the real one.
        _taxResolver = taxResolver ?? new CompanyTaxPolicyResolver(db);
        _activationGuard = activationGuard ?? new Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard(db);
    }

    /// <summary>
    /// PAY INTERLOCK (§6): run the ONE readiness evaluator over the run's employees and split them into
    /// (a) HARD pay-blocks — under-documented / expired-ID / State==Blocked employees who are NOT Active
    /// (a wage file must never carry them), and (b) DRIFT warnings — already-Active employees who fell
    /// pay-blocked after a policy change: these are surfaced and require explicit acknowledgement rather
    /// than being SILENTLY dropped from a run (§6.6 — a silent exclusion could miss a mandated salary).
    /// </summary>
    private async Task<(HashSet<int> HardBlocked, HashSet<int> DriftWarn)> ComputePayReadinessAsync(
        Guid tenantId, IReadOnlyList<Employee> employees, CancellationToken ct)
    {
        var hard = new HashSet<int>();
        var drift = new HashSet<int>();
        if (employees.Count == 0) return (hard, drift);
        // Batch-load snapshots (3 bulk queries), resolve policy per distinct (company, country, nationality).
        var snapshots = await new Zayra.Api.Infrastructure.Employees.EmployeeReadinessEvaluator(_db)
            .LoadSnapshotsAsync(tenantId, employees.Select(e => e.Id).ToList(), ct);
        var policyCache = new Dictionary<string, Zayra.Api.Infrastructure.Employees.ResolvedReadinessPolicy>();
        foreach (var emp in employees)
        {
            if (!snapshots.TryGetValue(emp.Id, out var snap)) continue;
            var key = $"{emp.CompanyId}|{emp.CountryCode}|{Zayra.Api.Infrastructure.Employees.GccReadinessFloor.NormalizeNationality(emp.Nationality)}";
            if (!policyCache.TryGetValue(key, out var policy))
            {
                policy = await _activationGuard.ResolvePolicyAsync(tenantId, emp.CompanyId, emp.CountryCode, emp.Nationality, ct);
                policyCache[key] = policy;
            }
            var readiness = _activationGuard.Evaluate(snap, policy);
            if (readiness.PayBlocking.Count == 0 && !readiness.IsBlocked) continue;
            if (string.Equals(emp.Status, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase)) drift.Add(emp.Id);
            else hard.Add(emp.Id);
        }
        return (hard, drift);
    }

    [HttpGet("salary-structures")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> SalaryStructures([FromQuery] Guid? companyId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var q = _db.SalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (companyId.HasValue)
            q = q.Where(x => x.CompanyId == companyId || x.CompanyId == null);
        var structures = await q.OrderBy(x => x.Name).ToListAsync(cancellationToken);
        return Ok(await ProjectSalaryStructuresAsync(tenantId, structures, cancellationToken));
    }

    [HttpGet("salary-structures/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> GetSalaryStructure(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var structure = await _db.SalaryStructures.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (structure is null) return NotFound(new { message = "Salary structure not found." });
        var dto = (await ProjectSalaryStructuresAsync(tenantId, new[] { structure }, cancellationToken)).Single();
        return Ok(dto);
    }

    [HttpPost("salary-structures")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CreateSalaryStructure(SalaryStructureRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var validationError = await ValidateSalaryStructureRequestAsync(tenantId, req, cancellationToken);
        if (validationError is not null) return validationError;
        if (req.CompanyId.HasValue && !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == req.CompanyId.Value && c.IsActive && !c.IsDeleted, cancellationToken))
            return BadRequest(new { message = "Company not found or not active." });
        var code = req.Code.Trim();
        if (await _db.SalaryStructures.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == req.CompanyId && x.Code == code && !x.IsDeleted, cancellationToken))
            return Conflict(new { message = "Salary structure code already exists." });
        var structureCurrency = !string.IsNullOrWhiteSpace(req.Currency) ? req.Currency : await ResolveCurrencyAsync(tenantId, cancellationToken);
        var structure = new SalaryStructure { TenantId = tenantId, CompanyId = req.CompanyId, Code = code, Name = req.Name.Trim(), Currency = structureCurrency.Trim().ToUpperInvariant(), EffectiveDate = req.EffectiveDate, CreatedBy = GetUserId() };
        ApplySalaryStructureRules(structure, req);
        _db.SalaryStructures.Add(structure);
        foreach (var component in req.Components ?? Array.Empty<SalaryComponentRequest>())
            _db.SalaryComponents.Add(BuildSalaryComponent(tenantId, structure.Id, component));
        await PayrollAudit("payroll.salary_structure.created", "SalaryStructure", structure.Id.ToString(), null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        var dto = (await ProjectSalaryStructuresAsync(tenantId, new[] { structure }, cancellationToken)).Single();
        return Created($"/api/payroll/salary-structures/{structure.Id}", dto);
    }

    [HttpPut("salary-structures/{id:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> UpdateSalaryStructure(Guid id, SalaryStructureRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var validationError = await ValidateSalaryStructureRequestAsync(tenantId, req, cancellationToken);
        if (validationError is not null) return validationError;

        var structure = await _db.SalaryStructures
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (structure is null) return NotFound(new { message = "Salary structure not found." });
        if (req.CompanyId.HasValue && !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == req.CompanyId.Value && c.IsActive && !c.IsDeleted, cancellationToken))
            return BadRequest(new { message = "Company not found or not active." });

        var code = req.Code.Trim();
        if (await _db.SalaryStructures.AnyAsync(x => x.TenantId == tenantId && x.Id != id && x.CompanyId == req.CompanyId && x.Code == code && !x.IsDeleted, cancellationToken))
            return Conflict(new { message = "Salary structure code already exists." });

        var activeAssignments = await _db.EmployeeSalaryStructures
            .CountAsync(x => x.TenantId == tenantId && x.SalaryStructureId == id && x.IsActive, cancellationToken);
        if (activeAssignments > 0)
        {
            structure.IsActive = false;
            var versionNumber = structure.VersionNumber + 1;
            var versionCode = await ResolveVersionCodeAsync(tenantId, req.CompanyId, code, versionNumber, cancellationToken);
            var version = new SalaryStructure
            {
                TenantId = tenantId,
                CompanyId = req.CompanyId,
                Code = versionCode,
                Name = req.Name.Trim(),
                Currency = (!string.IsNullOrWhiteSpace(req.Currency) ? req.Currency : await ResolveCurrencyAsync(tenantId, cancellationToken)).Trim().ToUpperInvariant(),
                EffectiveDate = req.EffectiveDate,
                CreatedBy = GetUserId(),
                VersionNumber = versionNumber,
                PreviousVersionId = structure.Id,
                IsActive = req.IsActive
            };
            ApplySalaryStructureRules(version, req);
            _db.SalaryStructures.Add(version);
            foreach (var component in req.Components ?? Array.Empty<SalaryComponentRequest>())
                _db.SalaryComponents.Add(BuildSalaryComponent(tenantId, version.Id, component));
            await PayrollAudit("payroll.salary_structure.version_created", "SalaryStructure", version.Id.ToString(), new { previousVersionId = structure.Id, version.Code, version.CompanyId }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            var versionDto = (await ProjectSalaryStructuresAsync(tenantId, new[] { version }, cancellationToken)).Single();
            return Ok(versionDto);
        }

        structure.CompanyId = req.CompanyId;
        structure.Code = code;
        structure.Name = req.Name.Trim();
        structure.Currency = (!string.IsNullOrWhiteSpace(req.Currency) ? req.Currency : await ResolveCurrencyAsync(tenantId, cancellationToken)).Trim().ToUpperInvariant();
        structure.EffectiveDate = req.EffectiveDate;
        structure.IsActive = req.IsActive;
        ApplySalaryStructureRules(structure, req);

        var existingComponents = await _db.SalaryComponents
            .Where(c => c.TenantId == tenantId && c.SalaryStructureId == structure.Id)
            .ToListAsync(cancellationToken);
        _db.SalaryComponents.RemoveRange(existingComponents);
        foreach (var component in req.Components ?? Array.Empty<SalaryComponentRequest>())
            _db.SalaryComponents.Add(BuildSalaryComponent(tenantId, structure.Id, component));

        await PayrollAudit("payroll.salary_structure.updated", "SalaryStructure", structure.Id.ToString(), new { structure.Code, structure.CompanyId }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        var dto = (await ProjectSalaryStructuresAsync(tenantId, new[] { structure }, cancellationToken)).Single();
        return Ok(dto);
    }

    [HttpDelete("salary-structures/{id:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> DeleteSalaryStructure(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var structure = await _db.SalaryStructures
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (structure is null) return NotFound(new { message = "Salary structure not found." });

        var activeAssignments = await _db.EmployeeSalaryStructures
            .CountAsync(x => x.TenantId == tenantId && x.SalaryStructureId == id && x.IsActive, cancellationToken);
        if (activeAssignments > 0)
            return BadRequest(new { message = $"Cannot delete salary structure while {activeAssignments} active employee salary assignment(s) use it. Deactivate it or reassign employees first." });

        structure.IsDeleted = true;
        structure.IsActive = false;
        await _db.SalaryComponents
            .Where(c => c.TenantId == tenantId && c.SalaryStructureId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.IsActive, false), cancellationToken);
        await PayrollAudit("payroll.salary_structure.deleted", "SalaryStructure", structure.Id.ToString(), new { structure.Code, structure.CompanyId }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<IReadOnlyList<SalaryStructureDto>> ProjectSalaryStructuresAsync(Guid tenantId, IReadOnlyCollection<SalaryStructure> structures, CancellationToken cancellationToken)
    {
        if (structures.Count == 0) return Array.Empty<SalaryStructureDto>();
        var ids = structures.Select(s => s.Id).ToList();
        var components = await _db.SalaryComponents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.SalaryStructureId.HasValue && ids.Contains(c.SalaryStructureId.Value))
            .OrderBy(c => c.Code)
            .ToListAsync(cancellationToken);
        var componentsByStructure = components
            .GroupBy(c => c.SalaryStructureId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(SalaryComponentDto.Project).ToList());
        var assignedCounts = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && ids.Contains(s.SalaryStructureId))
            .GroupBy(s => s.SalaryStructureId)
            .Select(g => new { SalaryStructureId = g.Key, Count = g.Select(x => x.EmployeeId).Distinct().Count() })
            .ToDictionaryAsync(x => x.SalaryStructureId, x => x.Count, cancellationToken);
        var companyIds = structures.Where(s => s.CompanyId.HasValue).Select(s => s.CompanyId!.Value).Distinct().ToList();
        var companies = companyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && companyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.TradeName != "" ? c.TradeName : c.LegalNameEn, cancellationToken);

        return structures.Select(s => SalaryStructureDto.Project(
                s,
                s.CompanyId.HasValue && companies.TryGetValue(s.CompanyId.Value, out var companyName) ? companyName : null,
                componentsByStructure.GetValueOrDefault(s.Id) ?? new List<SalaryComponentDto>(),
                assignedCounts.GetValueOrDefault(s.Id)))
            .ToList();
    }

    private async Task<IActionResult?> ValidateSalaryStructureRequestAsync(Guid tenantId, SalaryStructureRequest req, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest(new { message = "Salary structure code is required." });
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Salary structure name is required." });
        if (req.EffectiveDate == default) return BadRequest(new { message = "Effective date is required." });
        if (req.MinGrossSalary < 0 || req.MaxGrossSalary < 0 || req.MinBasicSalary < 0 || req.MaxBasicSalary < 0)
            return BadRequest(new { message = "Salary structure range values cannot be negative." });
        if (req.MaxGrossSalary > 0 && req.MinGrossSalary > req.MaxGrossSalary)
            return BadRequest(new { message = "Minimum gross salary cannot exceed maximum gross salary." });
        if (req.MaxBasicSalary > 0 && req.MinBasicSalary > req.MaxBasicSalary)
            return BadRequest(new { message = "Minimum basic salary cannot exceed maximum basic salary." });

        if (req.CompanyId.HasValue && !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == req.CompanyId.Value && c.IsActive && !c.IsDeleted, cancellationToken))
            return BadRequest(new { message = "Company not found or not active." });
        if (req.EligibleGradeIds?.Count > 0)
        {
            var found = await _db.Grades.CountAsync(g => g.TenantId == tenantId && req.EligibleGradeIds.Contains(g.Id) && !g.IsDeleted, cancellationToken);
            if (found != req.EligibleGradeIds.Distinct().Count()) return BadRequest(new { message = "One or more eligible grades were not found." });
        }
        if (req.EligibleDesignationIds?.Count > 0)
        {
            var found = await _db.Designations.CountAsync(d => d.TenantId == tenantId && req.EligibleDesignationIds.Contains(d.Id) && !d.IsDeleted, cancellationToken);
            if (found != req.EligibleDesignationIds.Distinct().Count()) return BadRequest(new { message = "One or more eligible designations were not found." });
        }

        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in req.Components ?? Array.Empty<SalaryComponentRequest>())
        {
            if (string.IsNullOrWhiteSpace(component.Code)) return BadRequest(new { message = "Every salary component requires a code." });
            if (string.IsNullOrWhiteSpace(component.Name)) return BadRequest(new { message = $"Component {component.Code} requires a name." });
            if (!seenCodes.Add(component.Code.Trim())) return BadRequest(new { message = $"Duplicate component code '{component.Code}'." });
            if (component.Amount < 0 || component.Percentage < 0) return BadRequest(new { message = $"Component {component.Code} amount and percentage cannot be negative." });
            if (!string.Equals(component.ComponentType, "Earning", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(component.ComponentType, "Deduction", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(component.ComponentType, "EmployerContribution", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = $"Component {component.Code} type must be Earning, Deduction, or EmployerContribution." });
            if (!string.Equals(component.CalculationType, "Fixed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(component.CalculationType, "Percentage", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(component.CalculationType, "Formula", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = $"Component {component.Code} calculation type must be Fixed, Percentage, or Formula." });
        }

        return null;
    }

    private static void ApplySalaryStructureRules(SalaryStructure structure, SalaryStructureRequest req)
    {
        structure.MinGrossSalary = req.MinGrossSalary;
        structure.MaxGrossSalary = req.MaxGrossSalary;
        structure.MinBasicSalary = req.MinBasicSalary;
        structure.MaxBasicSalary = req.MaxBasicSalary;
        structure.EligibleGradeIdsJson = JsonSerializer.Serialize((req.EligibleGradeIds ?? Array.Empty<Guid>()).Distinct().ToList());
        structure.EligibleDesignationIdsJson = JsonSerializer.Serialize((req.EligibleDesignationIds ?? Array.Empty<Guid>()).Distinct().ToList());
    }

    private async Task<string> ResolveVersionCodeAsync(Guid tenantId, Guid? companyId, string requestedCode, int versionNumber, CancellationToken cancellationToken)
    {
        var candidate = requestedCode;
        var exists = await _db.SalaryStructures.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == companyId && x.Code == candidate && !x.IsDeleted, cancellationToken);
        if (!exists) return candidate;
        candidate = $"{requestedCode}-V{versionNumber}";
        var n = versionNumber;
        while (await _db.SalaryStructures.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == companyId && x.Code == candidate && !x.IsDeleted, cancellationToken))
            candidate = $"{requestedCode}-V{++n}";
        return candidate;
    }

    private static SalaryComponent BuildSalaryComponent(Guid tenantId, Guid structureId, SalaryComponentRequest component) => new()
    {
        TenantId = tenantId,
        SalaryStructureId = structureId,
        Code = component.Code.Trim().ToUpperInvariant(),
        Name = component.Name.Trim(),
        ComponentType = NormalizeComponentType(component.ComponentType),
        CalculationType = NormalizeCalculationType(component.CalculationType),
        Amount = component.Amount,
        Percentage = component.Percentage,
        IsTaxable = component.IsTaxable,
        IsActive = component.IsActive
    };

    private static string NormalizeComponentType(string value) =>
        string.Equals(value, "Deduction", StringComparison.OrdinalIgnoreCase) ? "Deduction"
        : string.Equals(value, "EmployerContribution", StringComparison.OrdinalIgnoreCase) ? "EmployerContribution"
        : "Earning";

    private static string NormalizeCalculationType(string value) =>
        string.Equals(value, "Percentage", StringComparison.OrdinalIgnoreCase) ? "Percentage"
        : string.Equals(value, "Formula", StringComparison.OrdinalIgnoreCase) ? "Formula"
        : "Fixed";

    private static string? ValidateSalaryStructureEligibility(SalaryStructure structure, Employee employee, EmployeeSalaryStructureRequest req)
    {
        var gradeIds = ReadGuidSet(structure.EligibleGradeIdsJson);
        if (gradeIds.Count > 0 && (!employee.GradeId.HasValue || !gradeIds.Contains(employee.GradeId.Value)))
            return "Employee grade is not eligible for the selected salary structure.";
        var designationIds = ReadGuidSet(structure.EligibleDesignationIdsJson);
        if (designationIds.Count > 0 && (!employee.DesignationId.HasValue || !designationIds.Contains(employee.DesignationId.Value)))
            return "Employee designation is not eligible for the selected salary structure.";
        var gross = req.BasicSalary + req.HousingAllowance + req.TransportAllowance + req.FoodAllowance + req.MobileAllowance + req.OtherAllowance - req.FixedDeduction;
        if (structure.MinBasicSalary > 0 && req.BasicSalary < structure.MinBasicSalary)
            return $"Basic salary is below salary structure minimum {structure.MinBasicSalary:N2} {structure.Currency}.";
        if (structure.MaxBasicSalary > 0 && req.BasicSalary > structure.MaxBasicSalary)
            return $"Basic salary exceeds salary structure maximum {structure.MaxBasicSalary:N2} {structure.Currency}.";
        if (structure.MinGrossSalary > 0 && gross < structure.MinGrossSalary)
            return $"Gross salary is below salary structure minimum {structure.MinGrossSalary:N2} {structure.Currency}.";
        if (structure.MaxGrossSalary > 0 && gross > structure.MaxGrossSalary)
            return $"Gross salary exceeds salary structure maximum {structure.MaxGrossSalary:N2} {structure.Currency}.";
        if (req.EffectiveDate < structure.EffectiveDate)
            return $"Salary assignment cannot start before salary structure effective date {structure.EffectiveDate:yyyy-MM-dd}.";
        return null;
    }

    private static string? ValidateEmployeeSalaryAssignment(SalaryStructure structure, Employee employee, EmployeeSalaryStructureRequest req)
    {
        if (!structure.IsActive) return "Salary structure is inactive.";
        if (structure.CompanyId.HasValue && employee.CompanyId.HasValue && structure.CompanyId != employee.CompanyId)
            return "Salary structure belongs to a different legal entity than the employee.";
        return ValidateSalaryStructureEligibility(structure, employee, req);
    }

    private static HashSet<Guid> ReadGuidSet(string? json)
    {
        try { return (string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<Guid>>(json) ?? []).ToHashSet(); }
        catch { return []; }
    }

    private static string FormatGuidSet(string? json) => string.Join(';', ReadGuidSet(json));

    private static bool TryReadDecimal(
        IReadOnlyDictionary<string, string> row,
        string column,
        int rowNum,
        ICollection<string> errors,
        out decimal value)
    {
        var raw = row.GetValueOrDefault(column, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = 0m;
            return true;
        }
        if (decimal.TryParse(raw, out value)) return true;
        errors.Add($"Row {rowNum}: {column} must be a number.");
        return false;
    }

    private static bool TryReadGuidList(
        IReadOnlyDictionary<string, string> row,
        string column,
        int rowNum,
        ICollection<string> errors,
        out List<Guid> values)
    {
        values = new List<Guid>();
        var raw = row.GetValueOrDefault(column, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return true;
        foreach (var token in raw.Split(new[] { ';', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(token, out var value))
            {
                values.Add(value);
                continue;
            }
            errors.Add($"Row {rowNum}: {column} contains invalid id '{token}'.");
            return false;
        }
        return true;
    }

    [HttpPost("employee-salary-structures")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> AssignEmployeeSalary(EmployeeSalaryStructureRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // M3: reject zero/negative basic salary at assignment time
        if (req.BasicSalary <= 0)
            return BadRequest(new { message = "Basic salary must be greater than zero." });
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) return BadRequest(new { message = "Employee not found." });
        var structure = await _db.SalaryStructures.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.SalaryStructureId && !x.IsDeleted, cancellationToken);
        if (structure is null) return BadRequest(new { message = "Salary structure not found." });
        var eligibilityError = ValidateEmployeeSalaryAssignment(structure, employee, req);
        if (eligibilityError is not null) return BadRequest(new { message = eligibilityError });
        if (employee.GradeId.HasValue)
        {
            var grade = await _db.Grades.AsNoTracking().FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Id == employee.GradeId && !g.IsDeleted, cancellationToken);
            var gross = req.BasicSalary + req.HousingAllowance + req.TransportAllowance + req.FoodAllowance + req.MobileAllowance + req.OtherAllowance - req.FixedDeduction;
            if (grade is not null && grade.MinSalary > 0 && gross < grade.MinSalary)
                return BadRequest(new { message = $"Salary package is below grade {grade.Code} minimum {grade.MinSalary:N2} {grade.Currency}." });
            if (grade is not null && grade.MaxSalary > 0 && gross > grade.MaxSalary)
                return BadRequest(new { message = $"Salary package exceeds grade {grade.Code} maximum {grade.MaxSalary:N2} {grade.Currency}." });
        }
        // Salary assignments are an effective-dated schedule. Replacing one date must
        // not deactivate the current package when the new package starts in the future.
        await _db.EmployeeSalaryStructures
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive && x.EffectiveDate == req.EffectiveDate)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false), cancellationToken);
        var assignmentCurrency = !string.IsNullOrWhiteSpace(req.Currency) ? req.Currency : await ResolveCurrencyAsync(tenantId, cancellationToken);
        var assignment = new EmployeeSalaryStructure { TenantId = tenantId, EmployeeId = req.EmployeeId, SalaryStructureId = req.SalaryStructureId, BasicSalary = req.BasicSalary, HousingAllowance = req.HousingAllowance, TransportAllowance = req.TransportAllowance, FoodAllowance = req.FoodAllowance, MobileAllowance = req.MobileAllowance, OtherAllowance = req.OtherAllowance, FixedDeduction = req.FixedDeduction, EffectiveDate = req.EffectiveDate, Currency = assignmentCurrency, CreatedBy = GetUserId() };
        _db.EmployeeSalaryStructures.Add(assignment);
        await PayrollAudit("payroll.employee_salary.assigned", "EmployeeSalaryStructure", assignment.Id.ToString(), new { employeeId = req.EmployeeId, basicSalary = req.BasicSalary }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/payroll/employee-salary-structures/{assignment.Id}", SalaryStructureAssignmentDto.Project(assignment, true));
    }

    [HttpGet("runs")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListRuns([FromQuery] Guid? companyId, [FromQuery] string? status, [FromQuery] string? runType, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var query = _db.PayrollRuns.Where(r => r.TenantId == tenantId);
        if (companyId.HasValue) query = query.Where(r => r.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        // POD-B2: optional type filter. Unknown values 400 rather than silently returning everything.
        if (!string.IsNullOrWhiteSpace(runType))
        {
            var normalizedFilter = PayrollRunTypes.Normalize(runType);
            if (normalizedFilter is null)
                return BadRequest(new { error = "invalid_run_type", message = $"runType must be one of: {string.Join(", ", PayrollRunTypes.All)}." });
            query = query.Where(r => r.RunType == normalizedFilter);
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .ThenBy(r => r.RunType == PayrollRunTypes.Regular ? 0 : 1).ThenBy(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        // POD-B2 (M5b) — hold-out visibility on the RUN HEADER. Before Process an Exclude row has a null
        // Outcome and produces no validation result at all, so the intent would be completely invisible
        // until someone opened GET runs/{id}/population. The counts ride alongside the unchanged
        // items/total/page/pageSize shape, so this is a pure JSON superset for the frontend.
        var runIds = items.Select(r => r.Id).ToList();
        var selectionRows = runIds.Count == 0
            ? new List<PayrollRunEmployeeSelection>()
            : await _db.PayrollRunEmployeeSelections.AsNoTracking()
                .Where(s => s.TenantId == tenantId && runIds.Contains(s.PayrollRunId))
                .ToListAsync(cancellationToken);
        var selectionSummary = selectionRows
            .GroupBy(s => s.PayrollRunId)
            .Select(g => new
            {
                runId          = g.Key,
                includeCount   = g.Count(s => s.Mode == PayrollRunSelectionModes.Include),
                excludeCount   = g.Count(s => s.Mode == PayrollRunSelectionModes.Exclude),
                excludedCount  = g.Count(s => s.Outcome == PayrollRunSelectionOutcomes.Excluded),
                notEligibleCount = g.Count(s => s.Outcome == PayrollRunSelectionOutcomes.NotEligible),
                // Rows whose intent has not yet been applied by a Process pass.
                pendingCount   = g.Count(s => s.Outcome == null),
            })
            .ToList();

        var paged = new PagedResult<PayrollRun>(items, total, page, pageSize);
        return Ok(new { paged.Items, paged.Total, paged.Page, paged.PageSize, selectionSummary });
    }

    [HttpPost("runs")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CreateRun([FromBody] CreatePayrollRunRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (req.Month < 1 || req.Month > 12)
            return BadRequest(new { message = "Month must be between 1 and 12." });
        // Reject far-future/typo years (e.g. "2099"). Payroll can be run at most one year
        // ahead of the current year; anything beyond that is almost certainly a data-entry slip.
        var currentYear = DateTime.UtcNow.Year;
        if (req.Year < 2000 || req.Year > currentYear + 1)
            return BadRequest(new { message = $"Year must be between 2000 and {currentYear + 1}." });
        var activeCompanies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var runCompany = req.CompanyId.HasValue
            ? activeCompanies.FirstOrDefault(c => c.Id == req.CompanyId.Value)
            : activeCompanies.Count == 1 ? activeCompanies[0] : null;
        if (req.CompanyId.HasValue && runCompany is null)
            return BadRequest(new { message = "Company not found or not active." });
        if (runCompany is null)
            return BadRequest(new { message = "Select a legal entity before creating payroll. Multi-entity tenants cannot create tenant-wide payroll runs." });

        // ── POD-B2: run TYPE ──────────────────────────────────────────────────────────────────────
        var runType = PayrollRunTypes.Normalize(req.RunType);
        if (runType is null)
            return BadRequest(new
            {
                error   = "invalid_run_type",
                message = $"runType must be one of: {string.Join(", ", PayrollRunTypes.All)}.",
            });

        // ── POD-B2: pay BASIS, persisted on the run, NOT derived from the type (M2) ───────────────
        // Regular is always full-recurring. Supplementary/Correction are always supplemental (a top-up
        // to an already-paid period). OffCycle is the operator's choice: supplemental by default (the
        // mid-month bonus), full-recurring on request (the MISSED JOINER, which the type-derived design
        // made impossible — a zero-recurring slip trips ZERO_NET_WITH_GROSS / GL_WILL_NOT_BALANCE).
        var includesRecurringPay = PayrollRunTypes.DefaultIncludesRecurringPay(runType);
        if (req.IncludesRecurringPay.HasValue && req.IncludesRecurringPay.Value != includesRecurringPay)
        {
            if (!PayrollRunTypes.AllowsBasisOverride(runType))
                return BadRequest(new
                {
                    error   = "basis_not_overridable",
                    message = $"A '{runType}' run always uses {(includesRecurringPay ? "a full recurring" : "a supplemental")} pay basis. " +
                              "Only an OffCycle run may choose its basis.",
                });
            includesRecurringPay = req.IncludesRecurringPay.Value;
        }

        // ── POD-B2 (M7): GL posting period for a prior-period correction ─────────────────────────
        // Once a month is closed, no correction for it can ever post (PeriodCloseGuard, correctly,
        // rejects it). Real payroll shops book a prior-period correction into the CURRENT OPEN period
        // carrying a prior-period reference — the run still reports under Year/Month, only the accrual
        // journal moves. A Regular run may never do this: its accrual belongs to its own period.
        string? glPostingPeriod = null;
        if (!string.IsNullOrWhiteSpace(req.GlPostingPeriod))
        {
            if (runType == PayrollRunTypes.Regular)
                return BadRequest(new
                {
                    error   = "gl_posting_period_not_allowed",
                    message = "A Regular run always accrues into its own pay period. glPostingPeriod is only valid for OffCycle/Supplementary/Correction runs.",
                });
            var pp = req.GlPostingPeriod.Trim();
            if (!TryParseGlPeriod(pp, out var ppYear, out var ppMonth))
                return BadRequest(new { error = "invalid_gl_posting_period", message = "glPostingPeriod must be formatted 'yyyy-MM'." });
            // Posting BACKWARDS (into a period earlier than the pay period) is never the correction use
            // case and would silently pre-date the expense.
            if (ppYear * 12 + ppMonth < req.Year * 12 + req.Month)
                return BadRequest(new
                {
                    error   = "gl_posting_period_before_pay_period",
                    message = $"glPostingPeriod ({pp}) cannot precede the pay period ({req.Year}-{req.Month:D2}).",
                });
            if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, runCompany.Id, pp, cancellationToken))
                return UnprocessableEntity(new
                {
                    error   = "gl_period_closed",
                    message = $"GL period {pp} is closed. Choose an open period to book this run's journal into.",
                    period  = pp,
                });
            glPostingPeriod = pp;
        }

        // ── POD-B2: type-aware conflict ──────────────────────────────────────────────────────────
        // One non-voided REGULAR run per legal entity per period. Multi-entity tenants must process each
        // company separately so statutory country packs, employees, impacts, GL and WPS remain scoped.
        // OffCycle/Supplementary/Correction runs are unconstrained — any number may coexist.
        if (runType == PayrollRunTypes.Regular)
        {
            // The `Status != "Voided"` predicate is the API catching up with the index, which has carried
            // `WHERE status != 'Voided'` since 20260624000001 precisely so a voided run does not block
            // re-running the period. Without it the 409 here contradicted the DB and bricked the period
            // forever. This is also the seam POD-B3's void→recreate recovery needs.
            //
            // `|| r.CompanyId == null` closes the null-company hole: Postgres unique indexes treat NULL as
            // distinct, so the company-scoped index constrains NOTHING for the null-company runs both
            // seeders create (AuthSeeder / DemoDataSeeder). An unscoped Regular run competes with EVERY
            // company in the tenant — it is exactly what Process's `run.CompanyId ??= company.Id` will
            // collapse onto.
            var existing = await _db.PayrollRuns
                .Where(r => r.TenantId == tenantId && r.Year == req.Year && r.Month == req.Month
                         && (r.CompanyId == runCompany.Id || r.CompanyId == null)
                         && r.RunType == PayrollRunTypes.Regular
                         && r.Status != "Voided")
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
                return Conflict(new
                {
                    error   = "regular_run_exists",
                    message = $"A regular payroll run for {runCompany.LegalNameEn} {req.Year}/{req.Month:D2} already exists. " +
                              "Create an OffCycle/Supplementary/Correction run for an additional payment in this period, " +
                              "or void the existing run first.",
                    runId   = existing.Id,
                });
        }

        // ── POD-B2: parent-run link ──────────────────────────────────────────────────────────────
        if (req.ParentRunId.HasValue && runType == PayrollRunTypes.Regular)
            return BadRequest(new { error = "parent_not_allowed", message = "A Regular run cannot amend another run; parentRunId is only valid for Correction/Supplementary/OffCycle runs." });
        if (runType == PayrollRunTypes.Correction && !req.ParentRunId.HasValue)
            return BadRequest(new { error = "parent_required", message = "A Correction run must name the run it amends via parentRunId." });
        if (req.ParentRunId.HasValue)
        {
            var parent = await _db.PayrollRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == req.ParentRunId.Value, cancellationToken);
            if (parent is null)
                return BadRequest(new { error = "parent_not_found", message = "parentRunId does not identify a payroll run in this tenant." });
            if (parent.CompanyId != runCompany.Id)
                return BadRequest(new { error = "parent_company_mismatch", message = "The parent run belongs to a different legal entity." });
            if (parent.Status == "Voided")
                return BadRequest(new { error = "parent_voided", message = "A voided run cannot be amended. Recreate the run instead." });
            if (parent.Status == "Draft")
                return BadRequest(new { error = "parent_not_processed", message = "A Draft run has produced nothing to amend. Process the parent run first." });
            // Period is DELIBERATELY not constrained: a correction booked in M+1 for M is normal payroll
            // practice. Only the LINK is POD-B2; the retro/arrears math for it is POD-C3.
        }

        var run = new PayrollRun
        {
            TenantId = tenantId,
            CompanyId = runCompany.Id,
            Year = req.Year,
            Month = req.Month,
            RunType = runType,
            ParentRunId = req.ParentRunId,
            IncludesRecurringPay = includesRecurringPay,
            GlPostingPeriod = glPostingPeriod,
            CreatedByUserId = GetUserId(),
        };
        _db.PayrollRuns.Add(run);
        // CreateRun wrote no audit row before B2. A run of a non-default TYPE or BASIS is an operator
        // decision with financial consequences and must be attributable. Routed through the standard
        // helper so POD-A3's sealer stamps Seq/PreviousHash/EntryHash on the business SaveChanges.
        await PayrollAudit("payroll.run.created", "PayrollRun", run.Id.ToString(), new
        {
            runType,
            parentRunId          = req.ParentRunId,
            includesRecurringPay = includesRecurringPay,
            glPostingPeriod,
            year                 = req.Year,
            month                = req.Month,
            companyId            = runCompany.Id,
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/payroll/runs/{run.Id}", run);
    }

    /// <summary>POD-B2 — "yyyy-MM" parser for GlPostingPeriod, matching the format every GL period uses.</summary>
    private static bool TryParseGlPeriod(string value, out int year, out int month)
    {
        year = 0; month = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[4] != '-') return false;
        return int.TryParse(value.AsSpan(0, 4), out year)
            && int.TryParse(value.AsSpan(5, 2), out month)
            && year >= 2000 && month >= 1 && month <= 12;
    }

    /// <summary>
    /// POD-B2 (M7) — the period a run's ACCRUAL journal (Lock) and its void contra post into.
    /// Defaults to the run's own pay period; a non-Regular run may book into a later open period.
    /// </summary>
    internal static string GlAccrualPeriod(PayrollRun run) =>
        string.IsNullOrWhiteSpace(run.GlPostingPeriod) ? $"{run.Year}-{run.Month:D2}" : run.GlPostingPeriod!;

    /// <summary>
    /// POD-B2 — is this pay component payable on a SUPPLEMENTAL-basis run (one that pays no recurring
    /// salary)? Only the bonus, adjustment and statutory families are: everything else is either the
    /// recurring wage itself or a recurring deduction whose period was already consumed by the run that
    /// paid that wage. Mirrors the legacy emission block's `if (includesRecurringPay)` gate exactly, so
    /// both paths still produce the same rows. Client-added components default to NOT supplemental —
    /// a new custom allowance is recurring unless someone deliberately models it as a bonus/adjustment.
    /// </summary>
    private static bool IsSupplementalPayComponent(PayComponent c)
    {
        if (c.IsStatutory
            || c.CalcMethod == PayComponentCalcMethods.Statutory
            || c.ProviderKey == PayComponentProviders.Statutory)
            return true;
        return c.ProviderKey is PayComponentProviders.Bonus or PayComponentProviders.Adjustment;
    }

    // ── POD-B2: run population selector (include / exclude with an audited reason) ────────────────
    //
    // Statuses in which the population may still be changed. This is Process's own refusal list plus
    // "Voided": changing who is in a run after it has produced slips requires a re-Process, and a voided
    // run is finished. Kept as one list so the selector and Process can never disagree.
    private static readonly string[] PopulationLockedStatuses =
        { "Processed", "PendingFinanceReview", "Locked", "Approved", "Paid", "Voided" };

    /// <summary>The resolved population of a run plus every unhonoured/deliberate deviation from it.</summary>
    private sealed record RunPopulation(
        List<Employee> Employees,
        List<PayrollRunExclusion> Exclusions,
        List<PayrollRunExclusion> NotEligible,
        List<PayrollRunEmployeeSelection> Selections,
        string Mode);

    /// <summary>
    /// POD-B2 — the SINGLE resolver used by BOTH Process and Validate.
    ///
    /// Validate re-deriving the population independently was a hard break: Rule 1 MISSING_SALARY_STRUCTURE
    /// is an Error raised over ctx.ActiveEmployees, so a run with a hold-out would accumulate blocking
    /// Errors for the very people it deliberately excluded and could NEVER be approved or locked.
    ///
    ///   1. eligible  = the caller's existing Active/not-deleted/company-scoped query (UNCHANGED)
    ///   2. any Include row → ALLOW-LIST: population = eligible ∩ include; else population = eligible
    ///   3. minus every Exclude row, always
    ///   4. each selection row is stamped Included / Excluded / NotEligible
    /// </summary>
    private async Task<RunPopulation> ResolveRunPopulationAsync(
        Guid tenantId, PayrollRun run, List<Employee> eligible, CancellationToken ct)
    {
        var selections = await _db.PayrollRunEmployeeSelections
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id)
            .ToListAsync(ct);

        if (selections.Count == 0)
            return new RunPopulation(eligible, new(), new(), selections, "AllEligible");

        var eligibleById = eligible.ToDictionary(e => e.Id);
        var includeIds = selections.Where(s => s.Mode == PayrollRunSelectionModes.Include).Select(s => s.EmployeeId).ToHashSet();
        var excludeIds = selections.Where(s => s.Mode == PayrollRunSelectionModes.Exclude).Select(s => s.EmployeeId).ToHashSet();

        var mode = includeIds.Count > 0 ? "AllowList" : "DenyList";
        var population = (includeIds.Count > 0 ? eligible.Where(e => includeIds.Contains(e.Id)) : eligible.AsEnumerable())
            .Where(e => !excludeIds.Contains(e.Id))
            .ToList();
        var populationIds = population.Select(e => e.Id).ToHashSet();

        var exclusions  = new List<PayrollRunExclusion>();
        var notEligible = new List<PayrollRunExclusion>();
        foreach (var s in selections)
        {
            var emp = eligibleById.TryGetValue(s.EmployeeId, out var e) ? e : null;
            var code = emp?.EmployeeCode ?? s.EmployeeId.ToString();
            var name = emp?.FullName ?? "(not in this run's eligible set)";
            if (populationIds.Contains(s.EmployeeId))
            {
                s.Outcome = PayrollRunSelectionOutcomes.Included;
            }
            else if (emp is null)
            {
                // Named in the selector but not eligible at all — wrong company, not Active, or deleted.
                s.Outcome = PayrollRunSelectionOutcomes.NotEligible;
                notEligible.Add(new PayrollRunExclusion(s.EmployeeId, code, name, s.Reason));
            }
            else
            {
                s.Outcome = PayrollRunSelectionOutcomes.Excluded;
                exclusions.Add(new PayrollRunExclusion(s.EmployeeId, code, name, s.Reason));
            }
        }

        // An eligible employee dropped by ALLOW-LIST mode without ever being named is still a hold-out and
        // must be reported — silence here is exactly the "silently unpay the company" failure mode.
        if (includeIds.Count > 0)
        {
            foreach (var e in eligible)
            {
                if (populationIds.Contains(e.Id) || excludeIds.Contains(e.Id)) continue;
                exclusions.Add(new PayrollRunExclusion(e.Id, e.EmployeeCode, e.FullName,
                    "Not named in this run's allow-list."));
            }
        }

        return new RunPopulation(population, exclusions, notEligible, selections, mode);
    }

    /// <summary>
    /// POD-B2 — the eligible-employee query, verbatim from Process, factored out so Process, Validate and
    /// the population preview all start from the SAME set. Behaviour is unchanged from
    /// PayrollController.Process's original inline query, including the legacy-unscoped fallback.
    /// </summary>
    private async Task<List<Employee>> LoadEligibleEmployeesAsync(
        Guid tenantId, Guid companyId, bool allowLegacyUnscopedEmployees, bool asNoTracking, CancellationToken ct)
    {
        var q = _db.Employees.Where(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted
            && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)));
        if (asNoTracking) q = q.AsNoTracking();
        return await q.ToListAsync(ct);
    }

    /// <summary>
    /// POD-B2 — resolve the run's legal entity and the legacy-unscoped-employee allowance exactly the way
    /// Process/Validate do, so the selector endpoints scope to the same eligible set.
    /// </summary>
    private async Task<(Company? Company, bool AllowLegacyUnscoped)> ResolveRunCompanyScopeAsync(
        Guid tenantId, PayrollRun run, CancellationToken ct)
    {
        var activeCompanies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);
        var legacySingleCompanyScope = activeCompanies.Count == 1;
        var company = run.CompanyId.HasValue
            ? activeCompanies.FirstOrDefault(c => c.Id == run.CompanyId.Value)
            : legacySingleCompanyScope ? activeCompanies[0] : null;
        if (company is null) return (null, false);

        var hasAnyCompanyScoped = await _db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId.HasValue, ct);
        var hasForCompany = await _db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId == company.Id, ct);
        return (company, legacySingleCompanyScope || !hasAnyCompanyScoped || !hasForCompany);
    }

    /// <summary>
    /// POD-B2 — upsert include/exclude intent for a run's population, with a mandatory reason.
    ///
    /// REGULAR RUNS ARE DENY-LIST ONLY. One stray Include row on the monthly run would silently reduce
    /// the month to one person, and the partial unique index would then refuse a second Regular run to
    /// catch everyone else — the only remedy being Void + recreate + re-Process.
    /// </summary>
    [HttpPost("runs/{id:guid}/selection")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> UpsertRunSelection(Guid id, [FromBody] PayrollRunSelectionRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();

        var mode = PayrollRunSelectionModes.Normalize(req.Mode);
        if (mode is null)
            return BadRequest(new { error = "invalid_mode", message = "mode must be 'Include' or 'Exclude'." });
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required. Holding an employee out of (or into) a payroll run must be documented." });
        if (req.Reason.Length > 500)
            return BadRequest(new { error = "reason_too_long", message = "Reason must be 500 characters or fewer." });

        if (PopulationLockedStatuses.Contains(run.Status))
            return Conflict(new
            {
                error   = "population_locked",
                message = $"A run in '{run.Status}' status has already produced (or finished with) its payslips. " +
                          "Changing its population requires re-processing the run.",
                status  = run.Status,
            });

        if (mode == PayrollRunSelectionModes.Include && run.RunType == PayrollRunTypes.Regular)
            return BadRequest(new
            {
                error   = "include_not_allowed_on_regular_run",
                message = "A Regular run is deny-list only: it always covers every eligible employee minus explicit " +
                          "exclusions. Allow-list selection is available on OffCycle/Supplementary/Correction runs, " +
                          "where a narrow population is the point.",
            });

        var (company, allowLegacyUnscoped) = await ResolveRunCompanyScopeAsync(tenantId, run, cancellationToken);
        if (company is null)
            return UnprocessableEntity(new { error = "company_not_resolved", message = "The run must be linked to an active legal entity before its population can be scoped." });

        var eligible = await LoadEligibleEmployeesAsync(tenantId, company.Id, allowLegacyUnscoped, asNoTracking: true, cancellationToken);
        List<int> targetIds;
        if (req.AllEligible)
        {
            // Materialises "everyone" into REAL Include rows in one call, so the legitimate company-wide
            // off-cycle bonus run costs one click and still leaves an auditable record of who was intended.
            if (mode != PayrollRunSelectionModes.Include)
                return BadRequest(new { error = "all_eligible_requires_include", message = "allEligible is only meaningful with mode 'Include'." });
            targetIds = eligible.Select(e => e.Id).ToList();
        }
        else
        {
            targetIds = (req.EmployeeIds ?? new List<int>()).Distinct().ToList();
            if (targetIds.Count == 0)
                return BadRequest(new { error = "employees_required", message = "Provide employeeIds, or allEligible=true for an Include over the whole eligible set." });
        }

        var existing = await _db.PayrollRunEmployeeSelections
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == id)
            .ToListAsync(cancellationToken);
        var byEmployee = existing.ToDictionary(s => s.EmployeeId);
        var now = DateTime.UtcNow;
        var created = 0; var updated = 0;
        foreach (var empId in targetIds)
        {
            if (byEmployee.TryGetValue(empId, out var row))
            {
                row.Mode = mode;
                row.Reason = req.Reason.Trim();
                row.Outcome = null;               // intent changed → outcome is stale until the next Process
                row.UpdatedAtUtc = now;
                updated++;
            }
            else
            {
                _db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
                {
                    TenantId        = tenantId,
                    CompanyId       = run.CompanyId,   // stamped from the run → company write guard applies
                    PayrollRunId    = id,
                    EmployeeId      = empId,
                    Mode            = mode,
                    Reason          = req.Reason.Trim(),
                    CreatedByUserId = GetUserId(),
                    CreatedByName   = GetUserName(),
                    CreatedAtUtc    = now,
                });
                created++;
            }
        }

        await PayrollAudit("payroll.run.selection.changed", "PayrollRun", id.ToString(), new
        {
            mode, reason = req.Reason.Trim(), employeeIds = targetIds, allEligible = req.AllEligible,
            created, updated, runType = run.RunType,
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { runId = id, mode, created, updated, totalSelections = existing.Count + created });
    }

    /// <summary>POD-B2 — withdraw one employee's include/exclude intent (audited).</summary>
    [HttpDelete("runs/{id:guid}/selection/{employeeId:int}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> DeleteRunSelection(Guid id, int employeeId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        if (PopulationLockedStatuses.Contains(run.Status))
            return Conflict(new { error = "population_locked", message = $"A run in '{run.Status}' status cannot have its population changed.", status = run.Status });

        var row = await _db.PayrollRunEmployeeSelections
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.PayrollRunId == id && s.EmployeeId == employeeId, cancellationToken);
        if (row is null) return NotFound();

        _db.PayrollRunEmployeeSelections.Remove(row);
        await PayrollAudit("payroll.run.selection.removed", "PayrollRun", id.ToString(),
            new { employeeId, mode = row.Mode, reason = row.Reason }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>POD-B2 — effective population preview: who this run will pay, and who it will not (with why).</summary>
    [HttpGet("runs/{id:guid}/population")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer,Finance Controller,Finance Approver,Auditor")]
    public async Task<IActionResult> GetRunPopulation(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();

        var (company, allowLegacyUnscoped) = await ResolveRunCompanyScopeAsync(tenantId, run, cancellationToken);
        if (company is null)
            return UnprocessableEntity(new { error = "company_not_resolved", message = "The run must be linked to an active legal entity." });

        var eligible = await LoadEligibleEmployeesAsync(tenantId, company.Id, allowLegacyUnscoped, asNoTracking: true, cancellationToken);
        // AsNoTracking selections: this is a preview, the Outcome stamps must not leak into a SaveChanges.
        var pop = await ResolveRunPopulationAsync(tenantId, run, eligible, cancellationToken);
        foreach (var s in pop.Selections) _db.Entry(s).State = EntityState.Detached;

        return Ok(new
        {
            runId                = id,
            runType              = run.RunType,
            includesRecurringPay = run.IncludesRecurringPay,
            populationMode       = pop.Mode,
            eligibleCount        = eligible.Count,
            includedCount        = pop.Employees.Count,
            excludedCount        = pop.Exclusions.Count,
            notEligibleCount     = pop.NotEligible.Count,
            included    = pop.Employees.Select(e => new { employeeId = e.Id, code = e.EmployeeCode, name = e.FullName }).ToList(),
            excluded    = pop.Exclusions.Select(x => new { employeeId = x.EmployeeId, code = x.EmployeeCode, name = x.EmployeeName, reason = x.Reason }).ToList(),
            notEligible = pop.NotEligible.Select(x => new { employeeId = x.EmployeeId, code = x.EmployeeCode, name = x.EmployeeName, reason = x.Reason }).ToList(),
        });
    }

    /// <summary>
    /// POD-B2 — other non-voided runs for the same (company, year, month). The basis for the
    /// ALREADY_PAID_THIS_PERIOD control, the incremental statutory base, the sibling-run warning and the
    /// WPS duplicate-export guard.
    /// </summary>
    private Task<List<PayrollRun>> LoadSiblingRunsAsync(Guid tenantId, PayrollRun run, Guid companyId, CancellationToken ct) =>
        _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id != run.Id
                     && r.Year == run.Year && r.Month == run.Month
                     && (r.CompanyId == companyId || r.CompanyId == null)
                     && r.Status != "Voided")
            .ToListAsync(ct);

    /// <summary>
    /// POD-B2 — collapse the period-to-date statutory map to employee-side GOSI per employee, for
    /// PayrollValidationContext.PriorPeriodGosiEeByEmployee. Classification uses
    /// PayrollValidationEngine.IsGosiEmployeeCode, i.e. the SAME predicate Rule 2 applies to this run's
    /// own deductions, so the period total and the per-run total can never drift apart.
    /// </summary>
    private static Dictionary<int, decimal> BuildPriorPeriodGosiEe(
        IReadOnlyDictionary<(int EmployeeId, string Code, bool IsEmployer), decimal> priorStatutory)
    {
        var map = new Dictionary<int, decimal>();
        foreach (var kv in priorStatutory)
        {
            if (kv.Key.IsEmployer) continue;
            if (!PayrollValidationEngine.IsGosiEmployeeCode(kv.Key.Code)) continue;
            map[kv.Key.EmployeeId] = map.GetValueOrDefault(kv.Key.EmployeeId) + kv.Value;
        }
        return map;
    }

    [HttpPost("runs/{id:guid}/process")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> Process(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        // Processing mutates attendance/leave impacts and loan/advance balances, so completed states must use a correction workflow.
        if (run.Status is "Processed" or "PendingFinanceReview" or "Locked" or "Approved" or "Paid")
            return BadRequest(new { message = $"A run in '{run.Status}' status cannot be reprocessed. Void/delete and recreate the run, or use the correction workflow." });
        // POD-B2: "Voided" was missing from the list above. Pre-B2 that silently RESURRECTED a voided run
        // (Voided → Processed). Post-B2 it is worse: flipping the status pulls the row back INTO the
        // partial unique index alongside the live Regular run, so it would fail as a DbUpdateException 500
        // instead of a clean 4xx. This is a hardening of the void rules, never a relaxation.
        if (run.Status == "Voided")
            return BadRequest(new
            {
                error   = "run_voided",
                message = "A voided payroll run cannot be reprocessed. Create a replacement run for the period instead.",
            });

        var periodStart = new DateOnly(run.Year, run.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // NOTE (P0-2 atomicity): the idempotent delete of previously-generated rows was moved
        // INTO the execution-strategy transaction below so a mid-run fault never commits the
        // deletes (or the slip writes) without the loan-ledger decrement.

        // Resolve company → country pack for statutory deduction.
        var activeCompaniesForTenant = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var legacySingleCompanyScope = activeCompaniesForTenant.Count == 1;
        var company = run.CompanyId.HasValue
            ? activeCompaniesForTenant.FirstOrDefault(c => c.Id == run.CompanyId.Value)
            : legacySingleCompanyScope ? activeCompaniesForTenant[0] : null;
        var hasAnyCompanyScopedEmployees = await _db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId.HasValue, cancellationToken);

        // P0 FAIL-LOUD GUARD — abort before any payslips are written.
        // A payroll run must never proceed when the statutory pack cannot be resolved;
        // silently producing zero-deduction payslips violates GCC labour law.
        if (company is null)
            return UnprocessableEntity(new
            {
                error   = "company_not_resolved",
                message = "No active company found for this tenant. Cannot resolve a country pack for statutory deductions. " +
                          "Create and activate a company with a CountryCode before processing payroll.",
            });
        if (string.IsNullOrWhiteSpace(company.CountryCode))
            return UnprocessableEntity(new
            {
                error       = "country_code_missing",
                message     = $"Company '{company.LegalNameEn}' (id: {company.Id}) has no CountryCode set. " +
                              "Set the company country in Setup → Companies and retry.",
                companyId   = company.Id,
                companyName = company.LegalNameEn,
            });

        var packCc  = company.CountryCode;
        var packJur = company.Jurisdiction ?? string.Empty;
        var deductionCalc = _packResolver.ResolveDeductionCalculator(packCc, packJur);

        if (deductionCalc is DefaultStatutoryDeductionCalculator)
            return UnprocessableEntity(new
            {
                error        = "statutory_pack_not_configured",
                message      = $"No statutory deduction pack is registered for country '{packCc}' / jurisdiction '{packJur}'. " +
                               "Register a country pack for this jurisdiction before processing payroll. " +
                               "Payroll run aborted — no payslips written.",
                countryCode  = packCc,
                jurisdiction = packJur,
                companyId    = company.Id,
            });

        var hasEmployeesForRunCompany = await _db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId == company.Id, cancellationToken);
        var allowLegacyUnscopedEmployees = legacySingleCompanyScope || !hasAnyCompanyScopedEmployees || !hasEmployeesForRunCompany;

        // ── POD-B2 (M1): a null-company Regular run is UNSCOPED and competes with every company ──────
        // Process is about to stamp `run.CompanyId ??= company.Id`, collapsing this run onto that
        // company's period key. If a Regular run already owns that key, the ??= would throw a
        // DbUpdateException 500 at commit. Surface it as a clean 409 BEFORE any work is done.
        if (run.CompanyId is null && run.RunType == PayrollRunTypes.Regular)
        {
            var competing = await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Id != run.Id
                         && r.Year == run.Year && r.Month == run.Month
                         && r.CompanyId == company.Id
                         && r.RunType == PayrollRunTypes.Regular
                         && r.Status != "Voided")
                .Select(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (competing != Guid.Empty)
                return Conflict(new
                {
                    error     = "regular_run_exists",
                    message   = $"This run has no legal entity and would be scoped to '{company.LegalNameEn}' on processing, " +
                                $"but a regular run already exists there for {run.Year}/{run.Month:D2}. Void one of them first.",
                    runId     = competing,
                    companyId = company.Id,
                });
        }

        var eligibleEmployees = await LoadEligibleEmployeesAsync(tenantId, company.Id, allowLegacyUnscopedEmployees, asNoTracking: false, cancellationToken);

        // ── POD-B2: the operator's include/exclude intent decides who this run pays ──────────────────
        var runPopulation = await ResolveRunPopulationAsync(tenantId, run, eligibleEmployees, cancellationToken);

        // A non-Regular run over the WHOLE company is almost always operator error: per the supplemental
        // rules below it would consume the period's bonuses and adjustments out from under the Regular
        // run. Require the population to be stated explicitly. POST runs/{id}/selection with
        // allEligible=true materialises "everyone" in one call and still records who was intended.
        if (PayrollRunTypes.IsNonRegular(run.RunType) && runPopulation.Mode != "AllowList")
            return UnprocessableEntity(new
            {
                error   = "run_population_required",
                message = $"A '{run.RunType}' run must state its population explicitly. POST /api/payroll/runs/{run.Id}/selection " +
                          "with mode='Include' and the employee ids (or allEligible=true for the whole entity) before processing.",
                runType = run.RunType,
            });

        var employees = runPopulation.Employees;
        var employeeIdsForRun = employees.Select(e => e.Id).ToHashSet();
        if (employeeIdsForRun.Count == 0)
            return UnprocessableEntity(new
            {
                error = "no_company_employees",
                message = runPopulation.Mode == "AllEligible"
                    ? $"No active employees are linked to legal entity '{company.LegalNameEn}'. Payroll run aborted."
                    : $"This run's include/exclude selection resolves to zero employees for '{company.LegalNameEn}'. Payroll run aborted.",
                companyId = company.Id,
            });

        var salaryAssignments = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && x.EffectiveDate <= periodEnd && employeeIdsForRun.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);

        // Load salary structure components (for IsTaxable-based tax deduction)
        var structureIds = salaryAssignments.Select(x => x.SalaryStructureId).Distinct().ToList();
        var salaryComponents = await _db.SalaryComponents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SalaryStructureId.HasValue && structureIds.Contains(x.SalaryStructureId!.Value))
            .ToListAsync(cancellationToken);

        // FIX 3 (program P12): the effective income-tax rate for THIS legal entity is resolved
        // via CompanyTaxPolicyResolver — company override → tenant default (server-derived
        // company.Id, never client input). The legacy SystemSettings ["Payroll"/"IncomeTaxRate"]
        // magic key is now a LOGGED DEPRECATION FALLBACK, used only when no CompanyTaxPolicy is
        // configured, so payroll actually enforces the per-company policy instead of ignoring it.
        var taxPolicy = await _taxResolver.ResolveAsync(tenantId, company.Id, periodEnd, cancellationToken);
        decimal incomeTaxRate;
        if (taxPolicy?.IncomeTaxRatePercent is decimal policyRate)
        {
            incomeTaxRate = policyRate;
        }
        else
        {
            var taxRateSetting = await _db.SystemSettings.AsNoTracking()
                .Where(x => x.Category == "Payroll" && x.SettingKey == "IncomeTaxRate")
                .Select(x => x.SettingValue)
                .FirstOrDefaultAsync(cancellationToken);
            decimal.TryParse(taxRateSetting, out incomeTaxRate); // 0 if unset
            if (incomeTaxRate > 0m)
                Console.WriteLine($"[Payroll] DEPRECATION: tenant {tenantId} income tax resolved from the legacy SystemSettings magic key (no CompanyTaxPolicy). Migrate to a CompanyTaxPolicy for company {company.Id}.");
        }
        var attendanceImpacts = await _db.AttendancePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.WorkDate >= periodStart && x.WorkDate <= periodEnd && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);
        var leaveImpacts = await _db.LeavePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayPeriod == $"{run.Year}-{run.Month:00}" && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);

        // COMPLIANCE: Load active loans and salary advances per employee for EMI deduction
        var activeLoans = await _db.EmployeeLoans.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Status == "Active" && l.EmployeeIntId != null && employeeIdsForRun.Contains(l.EmployeeIntId.Value) && l.OutstandingBalance > 0
                && (!l.RepaymentStartDate.HasValue || l.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);
        var activeAdvances = await _db.SalaryAdvances.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null && employeeIdsForRun.Contains(a.EmployeeIntId.Value) && a.OutstandingBalance > 0
                && (!a.RepaymentStartDate.HasValue || a.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);

        var approvedAdjustments = await _db.PayrollAdjustments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.PayrollRunId == id && a.Status == "Approved" && employeeIdsForRun.Contains(a.EmployeeId))
            .ToListAsync(cancellationToken);

        // ── POD-B2: sibling runs for this (company, year, month) ─────────────────────────────────────
        // Everything below short-circuits to today's exact behaviour when there are none, which is the
        // case for every run in every tenant before B2 — so a Regular run is provably unchanged.
        var siblingRuns   = await LoadSiblingRunsAsync(tenantId, run, company.Id, cancellationToken);
        var siblingRunIds = siblingRuns.Select(r => r.Id).ToList();

        // (M2) Employees already paid RECURRING salary this period by another non-voided run. Feeds the
        // Error-severity ALREADY_PAID_THIS_PERIOD rule — the real cross-run double-pay control.
        var recurringSiblingIds = siblingRuns.Where(r => r.IncludesRecurringPay).Select(r => r.Id).ToList();
        var alreadyPaidRecurringEmpIds = recurringSiblingIds.Count == 0
            ? new HashSet<int>()
            : (await _db.PayrollSlips.AsNoTracking()
                .Where(s => s.TenantId == tenantId && recurringSiblingIds.Contains(s.RunId) && employeeIdsForRun.Contains(s.EmployeeId))
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();

        // (M8) Period-to-date statutory position from sibling runs, per employee.
        //
        // The statutory ceiling (GOSI covered wage = basic + housing, capped) is a PERIOD concept: computing
        // a supplemental run against a zero base applies the ceiling to the supplemental amount alone and
        // over-deducts near the cap, while ignoring siblings entirely would double-deduct the monthly wage.
        // Both are wrong. Instead the run is computed INCREMENTALLY: the pack is fed
        // (period-to-date covered wage + this run's own amounts), the ceiling therefore applies to the
        // period total, and the amounts sibling runs already deducted are netted off per line code
        // (floored at zero). Exact, because SalaryBreakdown.GosiCoveredWage == Basic + HousingAllowance and
        // both components are recoverable: the recurring halves from the sibling slips' own columns, the
        // GOSI-eligible bonus half by re-reading the bonuses those runs consumed.
        var priorBasicByEmp   = new Dictionary<int, decimal>();
        var priorHousingByEmp = new Dictionary<int, decimal>();
        var priorStatutoryByEmp = new Dictionary<(int EmployeeId, string Code, bool IsEmployer), decimal>();
        if (siblingRunIds.Count > 0)
        {
            var siblingSlips = await _db.PayrollSlips.AsNoTracking()
                .Where(s => s.TenantId == tenantId && siblingRunIds.Contains(s.RunId) && employeeIdsForRun.Contains(s.EmployeeId))
                .Select(s => new { s.EmployeeId, s.BasicSalary, s.HousingAllowance })
                .ToListAsync(cancellationToken);
            foreach (var s in siblingSlips)
            {
                priorBasicByEmp[s.EmployeeId]   = priorBasicByEmp.GetValueOrDefault(s.EmployeeId) + s.BasicSalary;
                priorHousingByEmp[s.EmployeeId] = priorHousingByEmp.GetValueOrDefault(s.EmployeeId) + s.HousingAllowance;
            }
            // GOSI-eligible bonus already covered by a sibling run rides in the housing slot, exactly as
            // this run's own gosiIncludedBonusTotal does below.
            var siblingBonuses = await _db.EmployeeBonuses.AsNoTracking()
                .Where(b => b.TenantId == tenantId && !b.IsDeleted && b.PayrollRunId != null
                         && siblingRunIds.Contains(b.PayrollRunId!.Value)
                         && b.EmployeeIntId != null && employeeIdsForRun.Contains(b.EmployeeIntId!.Value))
                .Select(b => new { b.EmployeeIntId, b.BonusTypeId, b.GrossBonusAmount })
                .ToListAsync(cancellationToken);
            if (siblingBonuses.Count > 0)
            {
                var siblingBonusTypeIds = siblingBonuses.Select(b => b.BonusTypeId).Distinct().ToList();
                var gosiEligibleTypeIds = (await _db.BonusTypes.AsNoTracking()
                    .Where(t => siblingBonusTypeIds.Contains(t.Id) && t.IsIncludedInGosiBase)
                    .Select(t => t.Id)
                    .ToListAsync(cancellationToken)).ToHashSet();
                foreach (var b in siblingBonuses)
                {
                    if (!gosiEligibleTypeIds.Contains(b.BonusTypeId)) continue;
                    var eid = b.EmployeeIntId!.Value;
                    priorHousingByEmp[eid] = priorHousingByEmp.GetValueOrDefault(eid) + b.GrossBonusAmount;
                }
            }
            var siblingStatutory = await _db.PayrollDeductions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && siblingRunIds.Contains(d.PayrollRunId)
                         && d.Source == "Statutory" && employeeIdsForRun.Contains(d.EmployeeId))
                .Select(d => new { d.EmployeeId, d.ComponentCode, d.IsEmployerContribution, d.Amount })
                .ToListAsync(cancellationToken);
            foreach (var d in siblingStatutory)
            {
                var key = (d.EmployeeId, d.ComponentCode, d.IsEmployerContribution);
                priorStatutoryByEmp[key] = priorStatutoryByEmp.GetValueOrDefault(key) + d.Amount;
            }
        }

        // POD-B2 — does this run pay recurring salary? Persisted on the run (M2), not derived from its
        // type, so an OffCycle run for a MISSED JOINER can pay a full salary while a mid-month bonus run
        // pays only the bonus. Regular runs are always true, so their emission path is byte-identical.
        var includesRecurringPay = run.IncludesRecurringPay;

        // BONUS: Load approved bonuses for this pay period — consumed here, blocked from MarkBatchPaid.
        var periodStr = $"{run.Year}-{run.Month:D2}";
        var pendingBonuses = await _db.EmployeeBonuses
            .Where(x => x.TenantId == tenantId && !x.IsDeleted
                && x.Status == "Approved"
                && x.PaymentPeriod == periodStr
                && x.PayrollRunId == null
                && x.EmployeeIntId != null
                && employeeIdsForRun.Contains(x.EmployeeIntId.Value))
            .ToListAsync(cancellationToken);
        var bonusTypeIds = pendingBonuses.Select(b => b.BonusTypeId).Distinct().ToList();
        var bonusTypeMap = bonusTypeIds.Count > 0
            ? await _db.BonusTypes.AsNoTracking()
                .Where(t => bonusTypeIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, cancellationToken)
            : new Dictionary<Guid, BonusType>();
        var bonusesByEmployee = pendingBonuses
            .Where(b => b.EmployeeIntId.HasValue)
            .GroupBy(b => b.EmployeeIntId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // COMPLIANCE: YTD — sum of all locked runs in the same year (before this month).
        // POD-B2: `r.Month < run.Month` alone made two runs in the SAME month invisible to each other, so
        // the payslip's YTD under-reported by the whole earlier run. Now: everything earlier in the year,
        // PLUS any other locked run in this same month. Zero effect on existing data — a second Locked
        // run in one month was impossible before B2.
        var ytdSlips = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Join(_db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tenantId && r.CompanyId == company.Id && r.Year == run.Year
                    && (r.Month < run.Month || (r.Month == run.Month && r.Id != run.Id))
                    && r.Status == "Locked"),
                  s => s.RunId, r => r.Id, (s, r) => s)
            .ToListAsync(cancellationToken);

        var openingBalancesByEmployee = await _db.PayrollOpeningBalances.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.Year == run.Year
                && employeeIdsForRun.Contains(x.EmployeeId)
                && (x.CompanyId == company.Id || x.CompanyId == null))
            .GroupBy(x => x.EmployeeId)
            .ToDictionaryAsync(x => x.Key, x => x.ToList(), cancellationToken);

        // COMPLIANCE: Load payroll profiles for MolId / RoutingCode (keyed by Employee.Id)
        var payrollProfiles = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && employeeIdsForRun.Contains(p.EmployeeId))
            .ToDictionaryAsync(p => p.EmployeeId, cancellationToken);

        // C4: filter overtime impacts to the current pay period only (via WorkDate on the originating request)
        var periodOvertimeRequestIds = await _db.OvertimeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.CompanyId == company.Id || (allowLegacyUnscopedEmployees && r.CompanyId == null)) && r.WorkDate >= periodStart && r.WorkDate <= periodEnd)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        var overtimeImpacts = await _db.OvertimePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId) && periodOvertimeRequestIds.Contains(x.OvertimeRequestId))
            .ToListAsync(cancellationToken);

        // L1: use policy-configured monthly hours as divisor; fall back to 240
        var standardMonthlyHours = await _db.OvertimePolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.IsActive && !p.IsDeleted)
            .OrderBy(p => p.CreatedAtUtc)
            .Select(p => (int?)p.StandardMonthlyHours)
            .FirstOrDefaultAsync(cancellationToken) ?? 240;

        // ── OT/LOP statutory rules from country pack config ───────────────────
        // Read from StatutoryRule table (tenant-overridable).  Fallback defaults are
        // directional KSA values — FLAG FOR COMPLIANCE SIGN-OFF before production filing.
        var eff = new DateOnly(run.Year, run.Month, 1);
        // OT multiplier: KSA Labour Law Art.107 = 1.5× for regular overtime days.
        // [FLAG-COMPLIANCE-KSA: weekend/holiday OT multipliers may differ — Art.107 baseline only]
        var otMultiplier = await _ruleReader.GetDecimalAsync(
            packCc, packJur, "ot.standard_multiplier", eff, tenantId, cancellationToken) ?? 1.5m;
        // Standard monthly hours for hourly-rate divisor (overrides policy value if configured).
        var otMonthlyHoursRule = await _ruleReader.GetDecimalAsync(
            packCc, packJur, "ot.standard_monthly_hours", eff, tenantId, cancellationToken);
        if (otMonthlyHoursRule.HasValue && otMonthlyHoursRule.Value > 0)
            standardMonthlyHours = (int)otMonthlyHoursRule.Value;
        // LOP day-rate divisor: basic ÷ lopDayDivisor per absent day.
        // [FLAG-COMPLIANCE-KSA: basic/30 is common KSA practice but court precedent varies — VERIFY]
        var lopDayDivisor = (int)(await _ruleReader.GetDecimalAsync(
            packCc, packJur, "lop.monthly_day_divisor", eff, tenantId, cancellationToken) ?? 30m);
        // Standard work minutes per day: used to convert absence minutes to LOP days.
        // [FLAG-COMPLIANCE-KSA: 480 min (8h); adjust for Ramadan or sector shift patterns]
        var lopStdMinutesPerDay = (int)(await _ruleReader.GetDecimalAsync(
            packCc, packJur, "lop.standard_work_minutes_per_day", eff, tenantId, cancellationToken) ?? 480m);

        // ── Attendance: employees who had daily records processed in this period ──
        // Used in Rule 10 (WARN_NO_ATTENDANCE) to detect employees skipped by attendance system.
        var attendanceProcessedEmpIds = (await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.WorkDate >= periodStart && r.WorkDate <= periodEnd && employeeIdsForRun.Contains(r.EmployeeId))
            .Select(r => r.EmployeeId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        // ── Configurability keystone: the data-driven pay-component set for this run ─────────────
        // The pay engine replaces the fixed compiled earning/deduction sequence below with a company-first
        // seeded catalog (PayComponent). Loaded ONCE per run (company is fixed for the run) as an immutable
        // read-only snapshot, safe to reuse across an execution-strategy retry. Empty store ⇒ compiled
        // PayComponentCatalog fallback (mirrors the gl_drivers store), so an un-seeded tenant is unaffected.
        // Payroll/UseComponentEngine is a per-tenant kill-switch (default ON): the engine is proven
        // byte-identical to the legacy inline block by the golden-master + equivalence-twin tests and never
        // changes any output, so "false" only exists for instant per-tenant rollback to the legacy path.
        var useComponentEngine = await ResolveUseComponentEngineAsync(tenantId, cancellationToken);
        var payComponents = useComponentEngine
            ? await LoadPayComponentsAsync(tenantId, company.Id, cancellationToken)
            : (IReadOnlyList<PayComponent>)Array.Empty<PayComponent>();

        // ── P0-2: ALL-OR-NOTHING MUTATION ──────────────────────────────────────────
        // Everything from here (idempotent delete → slip writes → statutory/loan/advance
        // ledger decrements → audit) commits as one transaction, so a cancel/crash/DB error
        // mid-run rolls the whole thing back: no payslips persist, the run stays in its prior
        // (Draft/Failed) status, and the loan/advance OutstandingBalance is unchanged — leaving
        // the run fully re-processable. AddDbContextPool + EnableRetryOnFailure requires the
        // whole tx to live inside the execution-strategy delegate; a bare BeginTransactionAsync
        // would throw. Mirrors EstablishmentGuardService.EnforceAndExecuteAsync — the delegate
        // must be safe to re-run from scratch, so every tracked-and-mutated entity (run, the
        // idempotent deletes, loans/advances/installments) is (re)loaded INSIDE after Clear();
        // read-only snapshots loaded above are immutable and safe to reuse across a retry.
        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
            // Retry-safety: on a transient RETRY, discard the failed attempt's tracked state so the
            // rebuild starts clean. We do NOT clear on the first attempt — that would detach entities
            // the caller is still holding (the request's scoped context), changing observable behaviour.
            if (attempt++ > 0)
                _db.ChangeTracker.Clear();
            // (Re)load the run we mutate. First attempt: returns the already-tracked instance.
            // Retry: re-materialises it after the Clear() above.
            run = await _db.PayrollRuns.FirstAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);

            // The transaction MUST open BEFORE the ExecuteUpdateAsync impact-status writes below,
            // otherwise that raw SQL auto-commits on its own connection and re-opens the split.
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Idempotent delete of previously-generated rows (enrolls in this tx).
            _db.PayrollSlips.RemoveRange(_db.PayrollSlips.Where(s => s.RunId == id && s.TenantId == tenantId));
            _db.PayrollRunEmployees.RemoveRange(_db.PayrollRunEmployees.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            _db.PayrollEarnings.RemoveRange(_db.PayrollEarnings.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            _db.PayrollDeductions.RemoveRange(_db.PayrollDeductions.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            _db.PayrollValidationResults.RemoveRange(_db.PayrollValidationResults.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));

        var slips = new List<PayrollSlip>();
        // POD-B2 — per-attempt accumulators. Declared INSIDE the execution-strategy delegate so a
        // transient retry starts clean (the delegate re-runs from here).
        var negativeNetEmployees = new List<object>();
        var statutoryComputedIncrementally = false;
        foreach (var e in employees)
        {
            var salary = salaryAssignments.Where(x => x.EmployeeId == e.Id && x.EffectiveDate <= periodEnd).OrderByDescending(x => x.EffectiveDate).FirstOrDefault();
            var basic = salary?.BasicSalary ?? e.Salary ?? 0m;
            var housing = salary?.HousingAllowance ?? 0m;
            var transport = salary?.TransportAllowance ?? 0m;
            var otherAllowances = (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
            var gross = basic + housing + transport + otherAllowances;
            var fixedDeduction = salary?.FixedDeduction ?? 0m;
            // Hourly rate for short-hours (late/early) deductions and OT base — basic ÷ standardMonthlyHours.
            var hourlyRate = standardMonthlyHours > 0 ? basic / standardMonthlyHours : 0m;

            // ── Short-hours deduction (late/early) at hourly rate ─────────────
            var attendanceDeduction = Math.Round(
                attendanceImpacts
                    .Where(x => x.EmployeeId == e.Id && x.ImpactType.Contains("deduction", StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.Minutes) / 60m * hourlyRate, 2);

            // ── LOP (Loss of Pay): absent days at day-rate (basic ÷ lopDayDivisor) ──
            // [FLAG-COMPLIANCE-KSA: LOP does NOT reduce GOSI covered wage in this implementation.
            //  Whether unpaid absence reduces the covered wage base is a statutory question
            //  requiring sign-off before any live payroll filing.]
            var absenceMinutes = attendanceImpacts
                .Where(x => x.EmployeeId == e.Id && x.ImpactType.Contains("Absence", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Minutes);
            var lopDays = lopStdMinutesPerDay > 0 && absenceMinutes > 0
                ? Math.Round((decimal)absenceMinutes / lopStdMinutesPerDay, 4)
                : 0m;
            var lopDayRate = lopDayDivisor > 0 && basic > 0 ? basic / lopDayDivisor : 0m;
            var lopDeduction = Math.Round(lopDays * lopDayRate, 2);

            var leaveDeduction = leaveImpacts.Where(x => x.EmployeeId == e.Id && x.ImpactType.Contains("Deduction", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Amount);

            // ── Overtime pay: approved hours × hourly rate × statutory multiplier ──
            // Recomputed from OvertimePayrollImpacts.Hours (not .Amount) so the statutory
            // multiplier from StatutoryRule drives the rate, not the policy-level multiplier
            // stored at approval time.
            // [FLAG-COMPLIANCE-KSA: OT is excluded from GOSI covered wage in this implementation.
            //  Art.107 sets 1.5× for regular OT; weekend/holiday rates may require separate rules.
            //  Whether OT pay is included in the GOSI covered wage requires sign-off before filing.]
            var empOtImpacts = overtimeImpacts.Where(x => x.EmployeeId == e.Id).ToList();
            var otHours = empOtImpacts.Sum(x => x.Hours);
            // Use per-impact approved multiplier if set (> 0); fall back to statutory standard multiplier.
            // This supports holiday/rest-day OT at 2× per KSA Art.107 where ApprovedMultiplier = 2.0.
            var overtimePay = empOtImpacts.Count > 0 && hourlyRate > 0m
                ? Math.Round(empOtImpacts.Sum(x =>
                    x.Hours * hourlyRate * (x.ApprovedMultiplier > 0m ? x.ApprovedMultiplier : otMultiplier)), 2)
                : 0m;
            // Tax deduction: apply income tax rate to taxable components only
            decimal taxDeduction = 0m;
            if (incomeTaxRate > 0 && salary is not null)
            {
                var structureComponents = salaryComponents.Where(c => c.SalaryStructureId == salary.SalaryStructureId && c.IsTaxable).ToList();
                // If no explicit taxable components defined, treat basic salary as taxable
                var taxableBase = structureComponents.Count > 0
                    ? structureComponents.Sum(c => c.CalculationType == "Percentage" ? basic * c.Percentage / 100m : c.Amount)
                    : basic;
                taxDeduction = Math.Round(taxableBase * incomeTaxRate / 100m, 2);
            }

            // ── POD-B2: SUPPLEMENTAL BASIS ───────────────────────────────────────────────────────────
            // A run that does not pay recurring salary pays supplemental items ONLY: bonuses, the
            // adjustments attached to this run, and statutory on the supplemental base. Without this gate
            // a mid-month bonus run pays the employee their WHOLE monthly salary a second time (the loop
            // has no `continue` and BASIC is emitted unconditionally at any amount), plus a second loan
            // EMI. Every recurring scalar is zeroed here, before it reaches the statutory input, the slip
            // aggregates or either emission path; the recurring LINES are skipped (not emitted at 0.00) so
            // no empty GL group is produced. `includesRecurringPay` is always true for a Regular run, so
            // this block is dead code on every existing run.
            if (!includesRecurringPay)
            {
                basic = 0m; housing = 0m; transport = 0m; otherAllowances = 0m; gross = 0m;
                fixedDeduction = 0m; hourlyRate = 0m;
                attendanceDeduction = 0m; lopDays = 0m; lopDayRate = 0m; lopDeduction = 0m;
                leaveDeduction = 0m; overtimePay = 0m; otHours = 0m; taxDeduction = 0m;
            }

            // BONUS: collect this employee's approved bonuses for the period.
            var empBonuses = bonusesByEmployee.TryGetValue(e.Id, out var eb) ? eb : new List<EmployeeBonus>();
            // Gross bonus amounts that are part of the social insurance base (e.g. GOSI/GPSSA/GRSIA).
            decimal gosiIncludedBonusTotal = empBonuses
                .Where(b => bonusTypeMap.TryGetValue(b.BonusTypeId, out var bt) && bt.IsIncludedInGosiBase)
                .Sum(b => b.GrossBonusAmount);
            // Net bonus earnings added to employee take-home this period.
            decimal totalBonusNet = empBonuses.Sum(b => b.BonusAmount);
            // POD-B1b — the bonus EARNING line has always been emitted GROSS (see AddEarning below), but
            // the slip aggregates counted it NET and the withheld tax was never emitted as a deduction at
            // all. That made the Lock journal short by exactly Σ TaxWithheld on any taxable bonus, so a
            // non-GCC tenant could never lock (gl_unbalanced 422). Carry the bonus GROSS through the slip
            // and emit the withholding as a real Tax deduction: gross − tax == net, so NetSalary, WPS/SIF
            // and the GOSI base are arithmetically unchanged, and for GCC (tax = 0) every emitted line and
            // every aggregate is byte-identical to before.
            decimal totalBonusGross = empBonuses.Sum(b => b.GrossBonusAmount);
            decimal totalBonusTax   = empBonuses.Sum(b => b.TaxWithheld);
            var empAdjustments = approvedAdjustments.Where(a => a.EmployeeId == e.Id).ToList();
            var adjustmentEarnings = empAdjustments.Where(a => a.Amount > 0m).Sum(a => a.Amount);
            var adjustmentDeductions = Math.Abs(empAdjustments.Where(a => a.Amount < 0m).Sum(a => a.Amount));

            // Statutory deduction via country pack — rates from tenant-overridable StatutoryRule rows.
            // GosiCalculationService is retained for parity testing; it is no longer called in the run path.
            // GOSI-included bonus is added to housing slot so GosiCoveredWage = Basic + Housing + Bonus.
            //
            // POD-B2 (M8): the covered-wage CEILING is a period concept, so once a sibling run exists for
            // this (company, year, month) the pack is fed the PERIOD-TO-DATE base and the amounts the
            // siblings already deducted are netted off per line. With no siblings — every run in every
            // tenant before B2 — priorBasic/priorHousing are 0 and this is the original call, unchanged.
            var priorBasic   = priorBasicByEmp.GetValueOrDefault(e.Id);
            var priorHousing = priorHousingByEmp.GetValueOrDefault(e.Id);
            var statutoryInput = new StatutoryDeductionInput(
                EmployeeId:   Guid.Empty, // Employee PK is int; Guid field not used in pack calculations
                CompanyId:    run.CompanyId ?? Guid.Empty,
                Salary:       new SalaryBreakdown(priorBasic + basic, priorHousing + housing + gosiIncludedBonusTotal, transport, otherAllowances),
                Nationality:  e.Nationality ?? string.Empty,
                ContractType: e.ContractType ?? "Indefinite",
                PeriodYear:   run.Year,
                PeriodMonth:  run.Month);
            var statutoryResult = await deductionCalc.CalculateAsync(statutoryInput, cancellationToken);
            if (priorStatutoryByEmp.Count > 0)
            {
                // Net off the period-to-date statutory already deducted by sibling runs, per (code, side),
                // floored at zero. What remains is THIS run's incremental statutory obligation, so the
                // period total ties out to a single ceiling-capped computation across all its runs.
                var netted = new List<StatutoryDeductionLine>();
                foreach (var line in statutoryResult.Lines)
                {
                    var priorEe = priorStatutoryByEmp.GetValueOrDefault((e.Id, line.Code, false));
                    var priorEr = priorStatutoryByEmp.GetValueOrDefault((e.Id, line.Code, true));
                    var ee = Math.Max(0m, line.EmployeeAmount - priorEe);
                    var er = Math.Max(0m, line.EmployerAmount - priorEr);
                    netted.Add(line with { EmployeeAmount = ee, EmployerAmount = er });
                }
                statutoryResult = statutoryResult with
                {
                    Lines                      = netted,
                    TotalEmployeeDeduction     = netted.Sum(l => l.EmployeeAmount),
                    TotalEmployerContribution  = netted.Sum(l => l.EmployerAmount),
                };
                statutoryComputedIncrementally = true;
            }
            var gosiEmployeeTotal = statutoryResult.TotalEmployeeDeduction;

            // COMPLIANCE: Loan & advance EMI deduction.
            // POD-B2: a supplemental run takes NO recurring deduction — an off-cycle bonus run must not
            // collect a second EMI for the period. The mutable decrement block after the loop is gated on
            // the same flag, so balances and installments are untouched too.
            var empLoans   = includesRecurringPay ? activeLoans.Where(l => l.EmployeeIntId == e.Id).ToList() : new List<EmployeeLoan>();
            var empAdv     = includesRecurringPay ? activeAdvances.Where(a => a.EmployeeIntId == e.Id).ToList() : new List<SalaryAdvance>();
            var loanEmi    = empLoans.Sum(l => Math.Min(l.InstallmentAmount, l.OutstandingBalance));
            var advEmi     = empAdv.Sum(a => Math.Min(a.InstallmentAmount, a.OutstandingBalance));
            var totalLoanDeduction = Math.Round(loanEmi + advEmi, 2);

            var deductions = fixedDeduction + attendanceDeduction + lopDeduction + leaveDeduction + taxDeduction + gosiEmployeeTotal + totalLoanDeduction + adjustmentDeductions + totalBonusTax;
            // C3: net salary cannot be negative (GCC labour law); engine Rule 3 will flag this.
            // (gross bonus in, bonus tax out) == (net bonus in) — take-home is unchanged by POD-B1b.
            var rawNet = gross + overtimePay + totalBonusGross + adjustmentEarnings - deductions;
            // POD-B2 (M4): a supplemental run whose deductions exceed its earnings has no vehicle to
            // express the shortfall — the floor below silently swallows it and the run then trips
            // GL_WILL_NOT_BALANCE (an Error whose remedy, "re-process the run", can never fix it), leaving
            // an unlockable run that DeleteRun also refuses. B2 Corrections are ADDITIVE-ONLY: collect the
            // offenders and refuse the whole run with a specific 422 below. Clawback/negative delta is B3.
            if (!includesRecurringPay && rawNet < 0m)
                negativeNetEmployees.Add(new { employeeId = e.Id, code = e.EmployeeCode, name = e.FullName, shortfall = Math.Abs(rawNet) });
            var netSalary = Math.Max(0m, rawNet);

            // COMPLIANCE: YTD — sum all locked slips for this employee earlier in the same year
            var empYtdSlips = ytdSlips.Where(s => s.EmployeeId == e.Id).ToList();
            openingBalancesByEmployee.TryGetValue(e.Id, out var openingBalances);
            var ytdGross    = empYtdSlips.Sum(s => s.GrossSalary) + SumOpeningBalance(openingBalances, "YTD_GROSS", "GROSS", "EARNINGS");
            var ytdDeduct   = empYtdSlips.Sum(s => s.Deductions) + SumOpeningBalance(openingBalances, "YTD_DEDUCTIONS", "YTD_DEDUCTION", "DEDUCTIONS", "DEDUCTION");
            var ytdNet      = empYtdSlips.Sum(s => s.NetSalary) + SumOpeningBalance(openingBalances, "YTD_NET", "NET");

            var slip = new PayrollSlip
            {
                TenantId = tenantId,
                RunId = id,
                EmployeeId = e.Id,
                EmployeeCode = e.EmployeeCode,
                EmployeeName = e.FullName,
                Department = e.Department,
                BasicSalary = basic,
                HousingAllowance = housing,
                TransportAllowance = transport,
                OtherAllowances = otherAllowances + overtimePay + totalBonusGross + adjustmentEarnings,
                GrossSalary = gross + overtimePay + totalBonusGross + adjustmentEarnings,
                Deductions = deductions,
                NetSalary = netSalary,
                EmployeeStatutoryTotal = statutoryResult.TotalEmployeeDeduction,
                EmployerStatutoryTotal = statutoryResult.TotalEmployerContribution,
                LoanDeductions = totalLoanDeduction,
                YtdGross = ytdGross + gross + overtimePay + totalBonusGross + adjustmentEarnings,
                YtdDeductions = ytdDeduct + deductions,
                YtdNet = ytdNet + netSalary,
                Status = "Draft",
            };
            slips.Add(slip);
            slip.CompanyId = company.Id;
            _db.PayrollRunEmployees.Add(new PayrollRunEmployee { TenantId = tenantId, PayrollRunId = id, EmployeeId = e.Id, GrossEarnings = slip.GrossSalary, TotalDeductions = deductions, NetPay = slip.NetSalary });
            // ── LINE EMISSION ────────────────────────────────────────────────────────────────────
            // The legacy inline block (kept VERBATIM under the kill-switch, so its behaviour is provably
            // unchanged) and the data-driven PayComponentEngine emit the SAME PayrollEarning/PayrollDeduction
            // rows for the seeded component set. Both call the same AddEarning/AddDeduction helpers, so GL
            // routing, WPS and the payslip builder downstream are identical either way. The slip header +
            // every aggregate (deductions/net/statutory totals) are projected above from the same scalars in
            // BOTH paths, so the run is byte-identical regardless of which branch runs.
            if (!useComponentEngine)
            {
            // Bonus earning lines (one per bonus in the batch, gross amount for GL expense tracking).
            foreach (var bonus in empBonuses)
                AddEarning(tenantId, id, e.Id, BonusGlLedger.EarningComponentCode(bonus.BonusTypeName), bonus.BonusTypeName, bonus.GrossBonusAmount, "Bonus");
            foreach (var adjustment in empAdjustments.Where(a => a.Amount > 0m))
                AddEarning(tenantId, id, e.Id, $"ADJ_{NormalizeCode(adjustment.AdjustmentType)}", AdjustmentLabel(adjustment), adjustment.Amount, "Adjustment");
            // POD-B2: BASIC is the ONLY line emitted unconditionally (every other recurring line already
            // carries an `if (x > 0)` guard, and the scalars above are zeroed on a supplemental basis).
            // Skipping it — rather than emitting 0.00 — keeps a supplemental run's journal free of an
            // empty EARN:BASIC debit group in BuildPayrollGlEntries.
            if (includesRecurringPay)
            AddEarning(tenantId, id, e.Id, "BASIC", "Basic salary", basic, "Salary");
            if (housing > 0) AddEarning(tenantId, id, e.Id, "HOUSING", "Housing allowance", housing, "Salary");
            if (transport > 0) AddEarning(tenantId, id, e.Id, "TRANSPORT", "Transport allowance", transport, "Salary");
            if (otherAllowances > 0) AddEarning(tenantId, id, e.Id, "OTHER_ALLOWANCES", "Other allowances", otherAllowances, "Salary");
            if (overtimePay > 0)
            {
                var otRateDisplay = Math.Round(hourlyRate * otMultiplier, 2);
                AddEarning(tenantId, id, e.Id, "OVERTIME",
                    $"Overtime ({otHours:N2} h × {Math.Round(hourlyRate, 2):N2}/h × {otMultiplier:N2})",
                    overtimePay, "Overtime");
            }
            if (fixedDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "FIXED_DEDUCTION", "Fixed deduction", fixedDeduction, "Salary");
            if (taxDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "INCOME_TAX", $"Income tax ({incomeTaxRate}%)", taxDeduction, "Tax");
            if (attendanceDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "ATTENDANCE", "Late/early attendance deduction", attendanceDeduction, "Attendance");
            if (lopDeduction > 0)
                AddDeduction(tenantId, company.Id, id, e.Id, "LOP_DEDUCTION",
                    $"Loss of Pay ({lopDays:N2} d × {Math.Round(lopDayRate, 2):N2}/d)",
                    lopDeduction, "Attendance");
            if (leaveDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "LEAVE", "Leave deduction", leaveDeduction, "Leave");
            if (loanEmi > 0) AddDeduction(tenantId, company.Id, id, e.Id, "LOAN_EMI", "Loan instalment", loanEmi, "Loan");
            if (advEmi > 0) AddDeduction(tenantId, company.Id, id, e.Id, "ADVANCE_EMI", "Salary advance repayment", advEmi, "Loan");
            foreach (var adjustment in empAdjustments.Where(a => a.Amount < 0m))
                AddDeduction(tenantId, company.Id, id, e.Id, $"ADJ_{NormalizeCode(adjustment.AdjustmentType)}", AdjustmentLabel(adjustment), Math.Abs(adjustment.Amount), "Adjustment");
            // Statutory deduction lines from pack — employee contributions reduce net pay.
            // Code/Label come from the pack: "GOSI-ANN-EE" / "GOSI Annuities (Employee)" for KSA,
            // "GPSSA-EE" / "GPSSA (Employee)" for UAE, "GRSIA-EE" / "GRSIA (Employee)" for Qatar.
            // Employer lines already carry ER-suffixed codes (e.g. "GOSI-ANN-ER", "GOSI-OH-ER") —
            // the GL router keys on .EndsWith("-ER") to route them to Social Insurance Employer Payable.
            foreach (var line in statutoryResult.Lines.Where(l => l.EmployeeAmount > 0))
                AddDeduction(tenantId, company.Id, id, e.Id, line.Code, line.Label, line.EmployeeAmount, "Statutory");
            foreach (var line in statutoryResult.Lines.Where(l => l.EmployerAmount > 0))
                AddDeduction(tenantId, company.Id, id, e.Id, line.Code, line.Label, line.EmployerAmount, "Statutory", isEmployerContribution: true);
            }
            else
            {
                // Data-driven path: build the pure engine context from the SAME computed scalars +
                // subsystem/pack results the legacy block uses (statutory amounts stay pack-owned; the
                // engine never touches the social-insurance base), then emit the ordered lines through the
                // same AddEarning/AddDeduction helpers. Family/statutory codes+labels are pre-built here so
                // NormalizeCode/AdjustmentLabel/pack labels produce byte-identical output.
                var payCtx = new PayComponentContext
                {
                    Basic = basic, Housing = housing, Transport = transport,
                    OtherAllowances = otherAllowances, FixedDeduction = fixedDeduction, Gross = gross,
                    OvertimePay = overtimePay, OtHours = otHours, HourlyRate = hourlyRate, OtMultiplier = otMultiplier,
                    TaxDeduction = taxDeduction, IncomeTaxRate = incomeTaxRate,
                    AttendanceDeduction = attendanceDeduction,
                    LopDeduction = lopDeduction, LopDays = lopDays, LopDayRate = lopDayRate,
                    LeaveDeduction = leaveDeduction, LoanEmi = loanEmi, AdvEmi = advEmi,
                    BonusLines = empBonuses
                        .Select(b => new PayComponentLine(BonusGlLedger.EarningComponentCode(b.BonusTypeName), b.BonusTypeName, b.GrossBonusAmount, "Bonus", false))
                        .ToList(),
                    AdjustmentEarningLines = empAdjustments.Where(a => a.Amount > 0m)
                        .Select(a => new PayComponentLine($"ADJ_{NormalizeCode(a.AdjustmentType)}", AdjustmentLabel(a), a.Amount, "Adjustment", false))
                        .ToList(),
                    AdjustmentDeductionLines = empAdjustments.Where(a => a.Amount < 0m)
                        .Select(a => new PayComponentLine($"ADJ_{NormalizeCode(a.AdjustmentType)}", AdjustmentLabel(a), Math.Abs(a.Amount), "Adjustment", false))
                        .ToList(),
                    StatutoryLines = statutoryResult.Lines,
                };
                // POD-B2: the engine's mirror of the legacy `if (includesRecurringPay)` gate. Filtering the
                // component SET (rather than zeroing amounts) is what makes the two paths still emit the
                // same rows: BASIC's EmitWhenZero flag would otherwise emit a 0.00 BASIC line here while
                // the legacy branch skipped it entirely. Bonus / Adjustment / Statutory providers survive —
                // those are exactly the supplemental families a non-recurring run pays.
                var effectiveComponents = includesRecurringPay
                    ? payComponents
                    : payComponents.Where(c => IsSupplementalPayComponent(c)).ToList();
                var computation = PayComponentEngine.Compute(effectiveComponents, payCtx);
                foreach (var line in computation.Earnings)
                    AddEarning(tenantId, id, e.Id, line.Code, line.Name, line.Amount, line.Source);
                foreach (var line in computation.Deductions)
                    AddDeduction(tenantId, company.Id, id, e.Id, line.Code, line.Name, line.Amount, line.Source, isEmployerContribution: line.IsEmployerContribution);
            }

            // POD-B1b — bonus withholding. Emitted OUTSIDE the branch so the legacy block and the
            // component engine stay provably identical (neither owns bonus tax). Source="Tax" routes it
            // to DED:TAX (2102) via the same driver every other PIT line uses, so it accrues on Lock and
            // clears through the existing POD-B1 TAX remittance with no new machinery. Zero rows emitted
            // for GCC tenants (ResolveTaxRate → 0), which is why no existing run changes shape.
            if (totalBonusTax > 0m)
                AddDeduction(tenantId, company.Id, id, e.Id,
                    BonusGlDescriptions.PayrollTaxComponentCode, BonusGlDescriptions.PayrollTaxComponentName,
                    totalBonusTax, "Tax");
        }

        // POD-B2 (M4) — refuse the whole run BEFORE anything is written, rather than shipping an
        // unlockable one. Throws out of the execution strategy so the transaction rolls back and the
        // caller gets a specific 422 naming every employee and their shortfall.
        if (negativeNetEmployees.Count > 0)
            throw new PayrollProcessAbortException(422, new
            {
                error   = "negative_net_unsupported",
                message = $"{negativeNetEmployees.Count} employee(s) on this supplemental run have deductions exceeding " +
                          "their supplemental earnings. POD-B2 correction/supplementary runs are ADDITIVE-ONLY — a " +
                          "negative delta (clawback) has no vehicle here and would produce a run that can never be " +
                          "locked or deleted. Reduce the negative adjustments, or recover the amount through the " +
                          "next regular run's deductions.",
                employees = negativeNetEmployees,
            });

        _db.PayrollSlips.AddRange(slips);
        // POD-B1b-FIX (P2-1) — pin the run's legal entity NOW, at Process, instead of re-deriving it at
        // Lock. BonusGlLedger.BuildPayrollClearingAsync fell back to "the tenant's single active company"
        // when run.CompanyId was null (BonusGlLedger.cs:174-183); if a second company were activated
        // between Process and Lock that fallback would resolve to nothing, no clearing would be planned,
        // and the bonus would be expensed a second time — the exact double-count this pod exists to kill.
        // `company` is the same value that fallback would compute and it has already been fail-loud
        // validated above (:566 company_not_resolved), so persisting it removes the race entirely.
        // Assigning a previously-null CompanyId is the sanctioned "repair" path in the write-side company
        // guard (ZayraDbContext.cs:366-368) and is access-validated there.
        run.CompanyId ??= company.Id;
        run.Status = "Processed";
        run.ProcessedAtUtc = DateTime.UtcNow;
        run.ProcessedByUserId = GetUserId();
        run.EmployeeCount = slips.Count;
        run.TotalGrossSalary = slips.Sum(s => s.GrossSalary);
        run.TotalDeductions = slips.Sum(s => s.Deductions);
        run.TotalNetSalary = slips.Sum(s => s.NetSalary);
        run.TotalEmployerStatutoryCost = slips.Sum(s => s.EmployerStatutoryTotal);
        // POD-B2 — impact CONSUMPTION is gated on the run's basis. These writes are already scoped by
        // `employeeIdsForRun` (so the selector narrows them for free), but a SUPPLEMENTAL run paid none of
        // the attendance/LOP/leave/overtime this period produced, so marking them "Processed" would starve
        // the Regular run of the period's impacts — the employee would silently lose their OT or gain
        // free absence. Unchanged for every Regular run (includesRecurringPay is always true there).
        if (includesRecurringPay)
        {
            await _db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId && x.WorkDate >= periodStart && x.WorkDate <= periodEnd && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId)).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"), cancellationToken);
            await _db.LeavePayrollImpacts.Where(x => x.TenantId == tenantId && x.PayPeriod == $"{run.Year}-{run.Month:00}" && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId)).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);
            await _db.OvertimePayrollImpacts
                .Where(x => x.TenantId == tenantId && x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId) && periodOvertimeRequestIds.Contains(x.OvertimeRequestId))
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.PayrollRunId, id).SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);
        }
        // Adjustments are NOT gated: they are already `PayrollRunId == id`-scoped, i.e. attached to THIS
        // run by the operator, and a supplemental run pays them.
        await _db.PayrollAdjustments
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.Status == "Approved" && employeeIdsForRun.Contains(x.EmployeeId))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"), cancellationToken);

        // COMPLIANCE: Update loan/advance outstanding balances after payroll deduction.
        // Re-load mutable copies for update (AsNoTracking above was read-only).
        await _db.SaveChangesAsync(cancellationToken); // flush slips + deductions first so engine can read them

        // Run centralised validation engine — replaces the old inline result adds.
        // Results are saved in the second SaveChangesAsync below.
        var valDeductions = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == id).ToListAsync(cancellationToken);
        var valEarnings = await _db.PayrollEarnings.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PayrollRunId == id).ToListAsync(cancellationToken);
        var valProfiles = payrollProfiles.Values.ToList();
        // Build period-level OT hours map for Rule 11 (OT_RATE_UNRESOLVED).
        var otHoursByEmpForValidation = overtimeImpacts
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));

        // GOSI staleness check: look up the most-recent system-default GOSI effective date
        // for this company's country pack so validation engine can warn if rates are stale.
        DateOnly? gosiRatesEffectiveFrom = null;
        if (string.Equals(company?.CountryCode, "SAU", StringComparison.OrdinalIgnoreCase)
         || string.Equals(company?.CountryCode, "SA",  StringComparison.OrdinalIgnoreCase))
        {
            // IgnoreQueryFilters is intentional: GosiContributionRules platform defaults use TenantId == Guid.Empty
            // which is excluded by the per-tenant global query filter. This query reads system-wide default
            // rates (not tenant data), so bypassing the tenant filter is correct and safe here.
            var latestGosiRule = await _db.GosiContributionRules.IgnoreQueryFilters()
                .Where(r => r.TenantId == Guid.Empty && r.CountryCode == "SA")
                .OrderByDescending(r => r.EffectiveFrom)
                .FirstOrDefaultAsync(cancellationToken);
            gosiRatesEffectiveFrom = latestGosiRule?.EffectiveFrom;
        }

        var validationCtx = new PayrollValidationContext(
            run, slips, employees, salaryAssignments, valProfiles, valDeductions, valEarnings, company)
        {
            OvertimeHoursByEmployee        = otHoursByEmpForValidation,
            AttendanceProcessedEmployeeIds = attendanceProcessedEmpIds,
            GosiRatesEffectiveFrom         = gosiRatesEffectiveFrom,
            // POD-B2 — multi-run-per-period facts. Written INSIDE the Process transaction, after the wipe
            // at the top of the strategy delegate, so re-processing never duplicates them.
            EmployeesAlreadyPaidRecurringThisPeriod = alreadyPaidRecurringEmpIds,
            Exclusions                              = runPopulation.Exclusions,
            NotEligibleSelections                   = runPopulation.NotEligible,
            SiblingRunCount                         = siblingRuns.Count,
            StatutoryComputedIncrementally          = statutoryComputedIncrementally,
            // Period-to-date employee-side GOSI from the sibling runs, so Rule 2 judges the PERIOD rather
            // than this run in isolation. Derived from the same priorStatutoryByEmp map the incremental
            // netting above used, so the figure Rule 2 credits is exactly the figure that was netted off.
            PriorPeriodGosiEeByEmployee             = BuildPriorPeriodGosiEe(priorStatutoryByEmp),
        };
        foreach (var r in PayrollValidationEngine.Run(validationCtx))
            _db.PayrollValidationResults.Add(r);

        // POD-B2 (M10) — the WHOLE loan/advance decrement block is gated, not each slip line. A
        // supplemental run collected no EMI (loanEmi/advEmi are 0 above), so decrementing
        // OutstandingBalance / TotalRepaid or marking a LoanInstallment "Paid" here would retire debt the
        // employee never actually repaid.
        // (Pre-existing asymmetry, NOT amplified here: loans are scoped by `employeeIdsForRun` while
        //  bonuses are scoped by `processedEmpIds` further down.)
        var activeLoansMutable = !includesRecurringPay ? new List<EmployeeLoan>() : await _db.EmployeeLoans
            .Where(l => l.TenantId == tenantId && l.Status == "Active" && l.EmployeeIntId != null && employeeIdsForRun.Contains(l.EmployeeIntId.Value) && l.OutstandingBalance > 0
                && (!l.RepaymentStartDate.HasValue || l.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);
        var activeAdvMutable = !includesRecurringPay ? new List<SalaryAdvance>() : await _db.SalaryAdvances
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null && employeeIdsForRun.Contains(a.EmployeeIntId.Value) && a.OutstandingBalance > 0
                && (!a.RepaymentStartDate.HasValue || a.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);

        foreach (var loan in activeLoansMutable)
        {
            var deducted = Math.Min(loan.InstallmentAmount, loan.OutstandingBalance);
            if (deducted <= 0) continue;
            loan.OutstandingBalance -= deducted;
            loan.TotalRepaid += deducted;
            if (loan.OutstandingBalance <= 0) loan.Status = "Closed";
            // Record the paid installment
            var inst = await _db.LoanInstallments
                .OrderBy(i => i.DueDate)
                .FirstOrDefaultAsync(i => i.LoanId == loan.Id && i.Status == "Pending" && i.DueDate <= periodEnd, cancellationToken);
            if (inst is not null) { inst.Status = "Paid"; inst.PaidDate = DateOnly.FromDateTime(DateTime.UtcNow); inst.PayrollRunId = id; inst.AmountPaid = deducted; }
        }
        foreach (var adv in activeAdvMutable)
        {
            var deducted = Math.Min(adv.InstallmentAmount, adv.OutstandingBalance);
            if (deducted <= 0) continue;
            adv.OutstandingBalance -= deducted;
            adv.TotalRepaid += deducted;
            if (adv.OutstandingBalance <= 0) adv.Status = "Closed";
            var inst = await _db.AdvanceInstallments
                .OrderBy(i => i.DueDate)
                .FirstOrDefaultAsync(i => i.AdvanceId == adv.Id && i.Status == "Pending" && i.DueDate <= periodEnd, cancellationToken);
            if (inst is not null) { inst.Status = "Paid"; inst.PaidDate = DateOnly.FromDateTime(DateTime.UtcNow); inst.PayrollRunId = id; inst.AmountPaid = deducted; }
        }

        // BONUS: mark consumed bonuses as PaidInPayroll so MarkBatchPaid() cannot double-pay.
        // Only bonuses for employees that were actually processed (had a payslip generated) are
        // consumed here. Employees with no salary assignment are skipped in the per-employee loop,
        // so their pending bonuses stay Approved for the next period or manual payment.
        var processedEmpIds = slips.Select(s => s.EmployeeId).ToHashSet();
        var toConsumeBonuses = pendingBonuses
            .Where(b => processedEmpIds.Contains(b.EmployeeIntId!.Value))
            .ToList();
        if (toConsumeBonuses.Count > 0)
        {
            var consumedBonusIds = toConsumeBonuses.Select(b => b.Id).ToHashSet();
            var consumedBatches  = toConsumeBonuses.Select(b => b.BonusBatchId).Distinct().ToList();
            // POD-B2 (M3) — CONDITIONAL stamp + affected-row assertion.
            //
            // The selection at the top of Process filters `PayrollRunId == null` at READ time; this stamp
            // used to key only on the id set. Pre-B2 that was unreachable (one run per period), but a
            // Regular and an OffCycle run for the same period can now be Processed CONCURRENTLY: both
            // would read the bonus as unconsumed, both would emit a Bonus earning, both would pay it into
            // net and WPS, and the stamp would be last-writer-wins. The accrual side then HIDES it —
            // BonusGlLedger.BuildPayrollClearingAsync reads `PayrollRunId == runId` and the cursor caps at
            // the outstanding position, so the payable clears exactly once, the losing run's bonus falls
            // through to the un-accrued EARN:BONUS remainder debit, and the journal still BALANCES. Cash
            // leaves twice, the accrual retires once, nothing reconciles: POD-B1b's double-count coming
            // back through a door B2 opens.
            //
            // The re-checked predicate makes the stamp a compare-and-swap, and the row count proves we won
            // it. ExecuteUpdateAsync runs inside this transaction, so aborting rolls the slips back too.
            var stampedBonusCount = await _db.EmployeeBonuses
                .Where(b => consumedBonusIds.Contains(b.Id) && b.PayrollRunId == null && b.Status == "Approved")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "PaidInPayroll")
                    .SetProperty(b => b.PayrollRunId, id), cancellationToken);
            if (stampedBonusCount != toConsumeBonuses.Count)
                throw new PayrollProcessAbortException(409, new
                {
                    error    = "bonus_consumed_concurrently",
                    message  = $"{toConsumeBonuses.Count - stampedBonusCount} bonus(es) selected by this run were consumed " +
                               "by another payroll run while it was processing. Nothing was written — re-process this run.",
                    expected = toConsumeBonuses.Count,
                    stamped  = stampedBonusCount,
                });
            // Lock the batch if all its approved bonuses are now consumed.
            foreach (var batchId2 in consumedBatches)
            {
                var batchHasUnpaid = await _db.EmployeeBonuses.AnyAsync(
                    b => b.BonusBatchId == batchId2 && !b.IsDeleted
                       && b.Status == "Approved" && b.PayrollRunId == null, cancellationToken);
                if (!batchHasUnpaid)
                    await _db.BonusBatches
                        .Where(x => x.Id == batchId2 && x.TenantId == tenantId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Status, "Paid")
                            .SetProperty(x => x.IsLockedByPayroll, true), cancellationToken);
            }
        }

        await PayrollAudit("payroll.run.processed", "PayrollRun", run.Id.ToString(), new
        {
            employeeCount   = slips.Count,
            totalNet        = run.TotalNetSalary,
            bonusesConsumed = toConsumeBonuses.Count,
            // POD-B2 — a run's TYPE, BASIS and the population it actually paid are the facts an auditor
            // needs to explain why two runs exist for one month and why one of them paid no salary.
            runType              = run.RunType,
            includesRecurringPay = includesRecurringPay,
            populationMode       = runPopulation.Mode,
            eligibleCount        = eligibleEmployees.Count,
            excludedCount        = runPopulation.Exclusions.Count,
            notEligibleCount     = runPopulation.NotEligible.Count,
            siblingRunCount      = siblingRuns.Count,
            statutoryIncremental = statutoryComputedIncrementally,
        }, cancellationToken);
            // The selector rows are TRACKED, so the Outcome stamps applied by ResolveRunPopulationAsync
            // persist with this save — inside the run transaction, alongside the slips they describe.
            await _db.SaveChangesAsync(cancellationToken); // second save: validation results + loan/advance decrements

            await tx.CommitAsync(cancellationToken); // commit the whole run atomically
            });
        }
        catch (PayrollProcessAbortException abort)
        {
            // POD-B2 — a deliberate, pre-commit abort (negative supplemental net, or a bonus consumed by a
            // concurrent run). The transaction was never committed, so nothing was written; surface the
            // specific 4xx instead of letting it escape as a 500.
            return StatusCode(abort.StatusCode, abort.Payload);
        }
        return Ok(run);
    }

    /// <summary>
    /// POD-B2 — a deliberate abort from inside Process's execution-strategy transaction. Thrown (not
    /// returned) because the delegate cannot return an IActionResult; throwing is also what guarantees the
    /// transaction is rolled back rather than partially committed.
    /// </summary>
    private sealed class PayrollProcessAbortException : Exception
    {
        public int StatusCode { get; }
        public object Payload { get; }
        public PayrollProcessAbortException(int statusCode, object payload)
            : base("Payroll processing aborted.") { StatusCode = statusCode; Payload = payload; }
    }

    [HttpPost("runs/{id:guid}/lock")]
    [HasPermission("payroll.lock")]
    public async Task<IActionResult> Lock(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status != "Approved") return BadRequest(new { message = "Only approved payroll runs can be locked." });

        // Block-on-error: any unresolved ERROR-severity validation result blocks locking.
        var blockingErrors = await _db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.Severity == "Error" && !x.IsResolved)
            .Select(x => new { x.Code, x.Message, x.EmployeeId })
            .ToListAsync(cancellationToken);
        if (blockingErrors.Count > 0)
            return UnprocessableEntity(new
            {
                error   = "validation_errors",
                message = $"Run has {blockingErrors.Count} unresolved validation error(s). " +
                          "Run /validate and resolve all errors before locking.",
                errors  = blockingErrors,
            });

        // FINANCE-P1: Persist double-entry GL on lock (idempotent — skip if already posted).
        // POD-B2 (M7): a non-Regular run may book its ACCRUAL into a later open period (set at Create and
        // validated open there) so a prior-period correction is possible after the month is closed. A
        // Regular run's GlPostingPeriod is always null, so `period` is byte-identical to before for them.
        var period = GlAccrualPeriod(run);
        var alreadyPosted = await _db.FinanceGlEntries
            .AnyAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == id && x.TenantId == tenantId, cancellationToken);
        if (!alreadyPosted)
        {
            // POD-B1 — a closed GL period rejects NEW postings. Dated into the accrual (run) period, so
            // this only ever fires for an explicitly closed period; a re-lock that posts nothing (already
            // posted) is unaffected because the guard sits inside the not-yet-posted branch.
            if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run.CompanyId, period, cancellationToken))
                return UnprocessableEntity(new
                {
                    error     = "gl_period_closed",
                    message   = $"GL period {period} is closed. Reopen it before locking payroll into this period.",
                    period,
                    companyId = run.CompanyId,
                });
            var earnings   = await _db.PayrollEarnings.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.PayrollRunId == id).ToListAsync(cancellationToken);
            var dedxns     = await _db.PayrollDeductions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.PayrollRunId == id).ToListAsync(cancellationToken);
            var totalNet   = run.TotalNetSalary;
            var uid        = GetUserId();
            var uname      = GetUserName();
            // Use company currency for GL entries — not hard-coded "USD".
            var glCompany  = await _db.Companies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId, cancellationToken);
            var glCurrency = glCompany?.DefaultCurrency ?? "SAR";
            var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken);
            // POD-B1b — bonuses this run PAYS were already expensed when their batch was approved
            // (DR bonus expense / CR Bonus Payable). Clear that payable instead of expensing them again;
            // the plan is capped by the outstanding accrual AND by the run's own bonus earning total, so
            // an un-accrued bonus is still expensed here and the journal always balances.
            var bonusEarningTotal = earnings.Where(e => e.Source == "Bonus").Sum(e => e.Amount);
            glCtx = glCtx with
            {
                BonusClearings = await BonusGlLedger.BuildPayrollClearingAsync(
                    _db, tenantId, id, run.CompanyId, bonusEarningTotal, cancellationToken),
            };
            var (glLines, totalDebits, totalCredits) = BuildPayrollGlEntries(
                tenantId, id, period, earnings, dedxns, totalNet, uid, uname, glCurrency, glCtx);
            if (Math.Abs(totalDebits - totalCredits) > 0.01m)
                return UnprocessableEntity(new
                {
                    error         = "gl_unbalanced",
                    message       = "Payroll GL is not balanced. Total debits must equal total credits before locking.",
                    totalDebits,
                    totalCredits,
                    difference    = Math.Abs(totalDebits - totalCredits),
                });
            _db.FinanceGlEntries.AddRange(glLines);
        }

        run.Status = "Locked";
        run.LockedAtUtc = DateTime.UtcNow;
        run.ErpPostingStatus = ErpPostingStatuses.ReadyForErp;
        run.ErpPostingStatusChangedAtUtc = DateTime.UtcNow;
        await _db.PayrollSlips.Where(s => s.RunId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Final"), cancellationToken);
        await _db.Payslips.Where(s => s.PayrollRunId == id && s.TenantId == tenantId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsPublishedToEss, true).SetProperty(p => p.PublishedAtUtc, DateTime.UtcNow), cancellationToken);
        // POD-B2 (M5b) — echo the hold-out count on the Lock audit and response so the last irreversible
        // step before payment restates who is NOT being paid.
        var lockExcludedCount = await _db.PayrollRunEmployeeSelections.AsNoTracking()
            .CountAsync(s => s.TenantId == tenantId && s.PayrollRunId == id
                          && s.Outcome == PayrollRunSelectionOutcomes.Excluded, cancellationToken);
        await PayrollAudit("payroll.run.locked", "PayrollRun", id.ToString(), new
        {
            glPosted = !alreadyPosted, period,
            runType = run.RunType, includesRecurringPay = run.IncludesRecurringPay,
            payPeriod = $"{run.Year}-{run.Month:D2}", excludedCount = lockExcludedCount,
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        // Notify all employees with a payslip for this run
        var employeeIds = await _db.PayrollSlips.AsNoTracking().Where(s => s.RunId == id && s.TenantId == tenantId).Select(s => s.EmployeeId).ToListAsync(cancellationToken);
        var usersByEmployee = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .Join(_db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)), u => u.Email, e => e.WorkEmail, (u, e) => new { u.Id, u.Email, u.FullName })
            .ToListAsync(cancellationToken);
        foreach (var user in usersByEmployee)
        {
            try
            {
                await _notifications.SendEmailAsync(tenantId, "PAYSLIP_READY", user.Email, user.FullName,
                    new Dictionary<string, string> { ["EmployeeName"] = user.FullName, ["Month"] = run.Month.ToString("D2"), ["Year"] = run.Year.ToString(), ["Subject"] = $"Your payslip for {run.Year}/{run.Month:D2} is ready" },
                    cancellationToken);
            }
            catch { /* best-effort per employee */ }
        }
        // JSON superset of the previous `Ok(run)` body — every PayrollRun property is still present at the
        // same path, so no existing consumer breaks.
        return Ok(new
        {
            run.Id, run.TenantId, run.CompanyId, run.Year, run.Month, run.Status,
            run.RunType, run.ParentRunId, run.IncludesRecurringPay, run.GlPostingPeriod,
            run.TotalGrossSalary, run.TotalDeductions, run.TotalNetSalary, run.TotalEmployerStatutoryCost,
            run.EmployeeCount, run.CreatedByUserId, run.ProcessedByUserId, run.CreatedAtUtc,
            run.ProcessedAtUtc, run.LockedAtUtc, run.ErpPostingStatus, run.ErpPostingStatusChangedAtUtc,
            run.ErpPostingReference, run.ErpPostingFailureReason,
            run.VoidReason, run.VoidedAtUtc, run.VoidedByUserId, run.VoidedByName,
            glPostingPeriodUsed = period,
            excludedCount       = lockExcludedCount,
        });
    }

    [HttpGet("runs/{id:guid}/slips")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> Slips(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var query = _db.PayrollSlips.Where(s => s.RunId == id && s.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(s => scope.AllowedEmployeeIds!.Contains(s.EmployeeId));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(s => s.EmployeeCode).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var employeeIds = items.Select(s => s.EmployeeId).ToList();
        // Load deduction lines for this page of slips to build the breakdown.
        var deductionsByEmployee = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == id && employeeIds.Contains(d.EmployeeId))
            .Select(d => new { d.EmployeeId, d.ComponentCode, d.ComponentName, d.Amount, d.Source })
            .ToListAsync(cancellationToken);
        var linesByEmployee = deductionsByEmployee
            .GroupBy(d => d.EmployeeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PayrollDeductionLineDto>)g.Select(d => new PayrollDeductionLineDto(d.ComponentCode, d.ComponentName, d.Amount, d.Source)).ToList());
        return Ok(new PagedResult<PayrollSlipDto>(
            items.Select(s => PayrollSlipDto.Project(s, true, linesByEmployee.TryGetValue(s.EmployeeId, out var dl) ? dl : null)).ToList(),
            total, page, pageSize));
    }

    [HttpPost("runs/{id:guid}/validate")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> Validate(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status == "Draft")
            return BadRequest(new { message = "Run must be Processed before it can be validated." });

        // Load all data needed by the engine and re-run from scratch.
        var activeCompaniesForValidation = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var legacySingleCompanyValidationScope = activeCompaniesForValidation.Count == 1;
        var company = run.CompanyId.HasValue
            ? activeCompaniesForValidation.FirstOrDefault(c => c.Id == run.CompanyId.Value)
            : legacySingleCompanyValidationScope ? activeCompaniesForValidation[0] : null;
        if (company is null)
            return UnprocessableEntity(new { error = "company_not_resolved", message = "Payroll validation requires a legal entity on the run." });
        var hasAnyCompanyScopedEmployeesForValidation = await _db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId.HasValue, cancellationToken);
        var hasEmployeesForValidationCompany = await _db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId == company.Id, cancellationToken);
        var allowLegacyUnscopedEmployeesForValidation = legacySingleCompanyValidationScope || !hasAnyCompanyScopedEmployeesForValidation || !hasEmployeesForValidationCompany;
        // ── POD-B2: Validate MUST resolve the population through the SAME resolver as Process ────────
        // Re-deriving it independently (every active company employee, selector-blind) is a hard break:
        // Rule 1 MISSING_SALARY_STRUCTURE is an Error raised over ctx.ActiveEmployees, so a run with a
        // hold-out would accumulate blocking Errors for the very people it deliberately excluded and could
        // never be approved or locked. Rule 10 WARN_NO_ATTENDANCE noises up the same way.
        var eligibleForValidation = await LoadEligibleEmployeesAsync(
            tenantId, company.Id, allowLegacyUnscopedEmployeesForValidation, asNoTracking: true, cancellationToken);
        var validationPopulation = await ResolveRunPopulationAsync(tenantId, run, eligibleForValidation, cancellationToken);
        // /validate is a read-model refresh; the Outcome stamps belong to Process (inside its transaction).
        foreach (var s in validationPopulation.Selections) _db.Entry(s).State = EntityState.Detached;
        var employees = validationPopulation.Employees;
        var employeeIdsForValidation = employees.Select(e => e.Id).ToHashSet();
        var slips       = await _db.PayrollSlips.AsNoTracking().Where(s => s.TenantId == tenantId && s.RunId == id && employeeIdsForValidation.Contains(s.EmployeeId)).ToListAsync(cancellationToken);
        var validationAsOf = new DateOnly(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month));
        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && x.EffectiveDate <= validationAsOf && employeeIdsForValidation.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);
        var profiles    = await _db.EmployeePayrollProfiles.AsNoTracking().Where(p => p.TenantId == tenantId && !p.IsDeleted && employeeIdsForValidation.Contains(p.EmployeeId)).ToListAsync(cancellationToken);
        var deductions  = await _db.PayrollDeductions.AsNoTracking().Where(d => d.TenantId == tenantId && d.PayrollRunId == id && employeeIdsForValidation.Contains(d.EmployeeId)).ToListAsync(cancellationToken);
        var earnings    = await _db.PayrollEarnings.AsNoTracking().Where(e => e.TenantId == tenantId && e.PayrollRunId == id && employeeIdsForValidation.Contains(e.EmployeeId)).ToListAsync(cancellationToken);

        // Load OT/attendance data so Rules 10+11 fire correctly on /validate too.
        var valPeriodStart = new DateOnly(run.Year, run.Month, 1);
        var valPeriodEnd   = valPeriodStart.AddMonths(1).AddDays(-1);
        var valOtRequestIds = await _db.OvertimeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.CompanyId == company.Id || (allowLegacyUnscopedEmployeesForValidation && r.CompanyId == null)) && r.WorkDate >= valPeriodStart && r.WorkDate <= valPeriodEnd)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        var valOtImpacts = await _db.OvertimePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && employeeIdsForValidation.Contains(x.EmployeeId) && valOtRequestIds.Contains(x.OvertimeRequestId))
            .ToListAsync(cancellationToken);
        var valAttendanceEmpIds = (await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.WorkDate >= valPeriodStart && r.WorkDate <= valPeriodEnd && employeeIdsForValidation.Contains(r.EmployeeId))
            .Select(r => r.EmployeeId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        var valOtHoursByEmp = valOtImpacts
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));

        // POD-B2 — the cross-run facts Process computes must be recomputed here, or /validate would clear
        // an ALREADY_PAID_THIS_PERIOD Error that Process legitimately raised and hand the run to Approve.
        var valSiblingRuns = await LoadSiblingRunsAsync(tenantId, run, company.Id, cancellationToken);
        var valRecurringSiblingIds = valSiblingRuns.Where(r => r.IncludesRecurringPay).Select(r => r.Id).ToList();
        var valAlreadyPaidEmpIds = valRecurringSiblingIds.Count == 0
            ? new HashSet<int>()
            : (await _db.PayrollSlips.AsNoTracking()
                .Where(s => s.TenantId == tenantId && valRecurringSiblingIds.Contains(s.RunId) && employeeIdsForValidation.Contains(s.EmployeeId))
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();

        // Period-to-date statutory from the sibling runs. /validate DELETES this run's stored results and
        // replaces them wholesale, so any fact Process fed the engine must be reproduced here or /validate
        // silently re-raises an Error that Process correctly withheld — and, because nothing ever sets
        // IsResolved, that Error would be unclearable. Same shape as the map Process builds.
        var valSiblingRunIds = valSiblingRuns.Select(r => r.Id).ToList();
        var valPriorStatutory = new Dictionary<(int EmployeeId, string Code, bool IsEmployer), decimal>();
        if (valSiblingRunIds.Count > 0)
        {
            var valSiblingStatutory = await _db.PayrollDeductions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && valSiblingRunIds.Contains(d.PayrollRunId)
                         && d.Source == "Statutory" && employeeIdsForValidation.Contains(d.EmployeeId))
                .Select(d => new { d.EmployeeId, d.ComponentCode, d.IsEmployerContribution, d.Amount })
                .ToListAsync(cancellationToken);
            foreach (var d in valSiblingStatutory)
            {
                var key = (d.EmployeeId, d.ComponentCode, d.IsEmployerContribution);
                valPriorStatutory[key] = valPriorStatutory.GetValueOrDefault(key) + d.Amount;
            }
        }

        var ctx     = new PayrollValidationContext(run, slips, employees, salaries, profiles, deductions, earnings, company)
        {
            OvertimeHoursByEmployee        = valOtHoursByEmp,
            AttendanceProcessedEmployeeIds = valAttendanceEmpIds,
            EmployeesAlreadyPaidRecurringThisPeriod = valAlreadyPaidEmpIds,
            Exclusions                              = validationPopulation.Exclusions,
            NotEligibleSelections                   = validationPopulation.NotEligible,
            SiblingRunCount                         = valSiblingRuns.Count,
            // Matches Process: the stored statutory amounts WERE netted incrementally whenever a sibling
            // run had already reported statutory for these employees, which is exactly this condition.
            StatutoryComputedIncrementally          = valPriorStatutory.Count > 0,
            PriorPeriodGosiEeByEmployee             = BuildPriorPeriodGosiEe(valPriorStatutory),
        };
        var results = PayrollValidationEngine.Run(ctx);

        // Replace stored results with fresh engine output.
        await _db.PayrollValidationResults
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id)
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var r in results)
            _db.PayrollValidationResults.Add(r);

        var errCount  = results.Count(r => r.Severity == "Error");
        var warnCount = results.Count(r => r.Severity == "Warning");
        await PayrollAudit("payroll.run.validated", "PayrollRun", id.ToString(),
            new { errorCount = errCount, warningCount = warnCount }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(results.OrderByDescending(r => r.Severity).ThenBy(r => r.Code).ToList());
    }

    // C1: segregation of duties — Payroll Officer who processes cannot self-approve.
    // Two-step: Payroll Manager/HR advances Processed → PendingFinanceReview (level 1);
    // Finance Controller/Approver finalises PendingFinanceReview → Approved (level 2).
    // Admin bypasses all levels.
    [HttpPost("runs/{id:guid}/approve")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> Approve(Guid id, PayrollDecisionRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status != "Processed" && run.Status != "PendingFinanceReview")
            return BadRequest(new { message = "Only a Processed or PendingFinanceReview run can be approved." });

        // Block-on-error: any unresolved ERROR-severity validation result blocks approval.
        var blockingErrors = await _db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.Severity == "Error" && !x.IsResolved)
            .Select(x => new { x.Code, x.Message, x.EmployeeId })
            .ToListAsync(cancellationToken);
        if (blockingErrors.Count > 0)
            return UnprocessableEntity(new
            {
                error   = "validation_errors",
                message = $"Run has {blockingErrors.Count} unresolved validation error(s). " +
                          "Run /validate and resolve all errors before approving.",
                errors  = blockingErrors,
            });

        // ── POD-B2 (M5b): deliberate hold-outs must be ACKNOWLEDGED, not merely warned ───────────────
        // EMPLOYEE_EXCLUDED_FROM_RUN is a Warning by design (an exclusion is intentional and must not
        // block), but a warning is invisible in practice: this endpoint and Lock only block on
        // Severity == "Error", and a normal monthly run already carries dozens of warnings
        // (MISSING_PAYROLL_PROFILE, WARN_NO_ATTENDANCE, MISSING_NATIONALITY, NON_SAUDI_IBAN…). The
        // approver is not the person who set the exclusion. So the approver must state the number they
        // expect and it must match, exactly like a cash count.
        var resolvedExcludedCount = await _db.PayrollRunEmployeeSelections.AsNoTracking()
            .CountAsync(s => s.TenantId == tenantId && s.PayrollRunId == id
                          && s.Outcome == PayrollRunSelectionOutcomes.Excluded, cancellationToken);
        if (resolvedExcludedCount > 0 && req.ExpectedExcludedCount != resolvedExcludedCount)
            return Conflict(new
            {
                error          = "excluded_employees_not_acknowledged",
                message        = $"This run deliberately excludes {resolvedExcludedCount} employee(s) from payment. " +
                                 "Review GET /api/payroll/runs/{id}/population and re-submit with " +
                                 $"expectedExcludedCount={resolvedExcludedCount} to acknowledge.",
                excludedCount  = resolvedExcludedCount,
                acknowledged   = req.ExpectedExcludedCount,
            });

        // Maker-checker: the user who processed the run cannot approve it.
        var approverId = GetUserId();
        if (run.ProcessedByUserId.HasValue && run.ProcessedByUserId == approverId)
            return StatusCode(403, new { error = "maker_checker_violation", message = "The user who processed this run cannot approve it. A different approver is required (maker-checker policy)." });

        var isAdmin = User.IsInRole("Admin");
        var isHROrPayroll = User.IsInRole("HR Manager") || User.IsInRole("Payroll Manager");
        var isFinance = User.IsInRole("Finance Controller") || User.IsInRole("Finance Approver");

        // Admin and Finance finalise directly to Approved
        if (isAdmin || isFinance)
        {
            _db.PayrollApprovals.Add(new PayrollApproval { TenantId = tenantId, PayrollRunId = id, ApprovalLevel = "FinanceReview", Decision = "Approved", Notes = req.Notes ?? string.Empty, DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow });
            run.Status = "Approved";
            await PayrollAudit("payroll.run.approved", "PayrollRun", id.ToString(), new { notes = req.Notes, level = "Finance" }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await _notifications.NotifyAsync(tenantId, GetUserId(), $"Payroll Run Approved — {run.Year}/{run.Month:D2}", $"Payroll run for {run.Year}/{run.Month:D2} has been approved by Finance. Total net: {run.TotalNetSalary:N2} AED.", "PayrollRun", id.ToString(), cancellationToken);
            return Ok(run);
        }

        // HR Manager or Payroll Manager advances Processed → PendingFinanceReview
        if (isHROrPayroll && run.Status == "Processed")
        {
            _db.PayrollApprovals.Add(new PayrollApproval { TenantId = tenantId, PayrollRunId = id, ApprovalLevel = "PayrollReview", Decision = "Approved", Notes = req.Notes ?? string.Empty, DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow });
            run.Status = "PendingFinanceReview";
            await PayrollAudit("payroll.run.payroll_approved", "PayrollRun", id.ToString(), new { notes = req.Notes }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(run);
        }

        return BadRequest(new { message = "You cannot approve this run at its current stage." });
    }

    [HttpPost("runs/{id:guid}/send-back")]
    [HasPermission("payroll.lock")]
    public async Task<IActionResult> SendBack(Guid id, PayrollDecisionRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status != "PendingFinanceReview")
            return BadRequest(new { message = "Only a PendingFinanceReview run can be sent back." });
        _db.PayrollApprovals.Add(new PayrollApproval { TenantId = tenantId, PayrollRunId = id, ApprovalLevel = "FinanceReview", Decision = "SentBack", Notes = req.Notes ?? string.Empty, DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow });
        run.Status = "Processed";
        await PayrollAudit("payroll.run.sent_back", "PayrollRun", id.ToString(), new { notes = req.Notes }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(run);
    }

    // ── VoidRun ───────────────────────────────────────────────────────────────────
    // Soft-delete a payroll run with full audit trail:
    //   • status → "Voided" (never hard-deleted — financial records are immutable)
    //   • payslips → "Voided"
    //   • GL contra-entries written for any already-posted GL (Locked runs)
    //   • audit-logged with who/when/why
    //   • partial unique index allows a replacement run to be created for the same period
    //
    // RBAC: Admin and Finance Controller only — Finance Controller is the
    // designated financial-control role, not ordinary payroll processors.

    [HttpPost("runs/{id:guid}/void")]
    [HasPermission("payroll.lock")]
    public async Task<IActionResult> VoidRun(Guid id, [FromBody] PayrollDecisionRequest req, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new
            {
                error   = "reason_required",
                message = "A void reason is required. Voiding a payroll run is an irreversible financial action and must be documented.",
            });

        var tenantId = GetTenantId();
        var result   = await new PayrollVoidService(_db).VoidAsync(
            id, tenantId, GetUserId(), GetUserName(), req.Notes, cancellationToken);

        if (result.IsNotFound)    return NotFound();
        if (result.IsAlreadyVoid) return Conflict(new { error = "already_voided", message = "This payroll run has already been voided." });
        // POD-B1 (P0-3) — voiding would post contras into a closed GL period; require an audited reopen.
        if (result.IsPeriodClosed)
            return UnprocessableEntity(new
            {
                error   = "gl_period_closed",
                message = $"GL period {result.Period} is closed. Reopen it (Finance → GL periods) before voiding this run so the reversal is not posted into closed books.",
                period  = result.Period,
            });

        return Ok(new
        {
            runId             = id,
            period            = result.Period,
            status            = "Voided",
            glEntriesReversed = result.GlReversed,
            reason            = req.Notes,
            // POD-B2 — non-voided runs that AMEND this one are now orphaned. Surfaced, not cascaded:
            // recovery of a correction chain is POD-B3.
            childRunIds       = result.ChildRunIds,
        });
    }

    /// <summary>
    /// Hard-deletes an unprocessed (Draft) payroll run. Only Draft runs qualify — they
    /// carry no payslips, GL entries, or other financial records, so removing them raises
    /// no audit-immutability concern. Processed/Locked/Approved runs must be Voided instead.
    /// Use case: a run created for the wrong period (e.g. a typo'd year) can be removed cleanly.
    /// </summary>
    [HttpDelete("runs/{id:guid}")]
    [HasPermission("payroll.run_delete")]
    public async Task<IActionResult> DeleteRun(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status != "Draft")
            return BadRequest(new
            {
                error   = "not_deletable",
                message = $"Only Draft runs can be deleted. A '{run.Status}' run must be voided instead to preserve the financial audit trail.",
            });

        // Defensive cleanup — a Draft run should have no children, but remove any stragglers.
        _db.PayrollSlips.RemoveRange(_db.PayrollSlips.Where(s => s.TenantId == tenantId && s.RunId == id));
        _db.PayrollRunEmployees.RemoveRange(_db.PayrollRunEmployees.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        _db.PayrollEarnings.RemoveRange(_db.PayrollEarnings.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        _db.PayrollDeductions.RemoveRange(_db.PayrollDeductions.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        _db.PayrollValidationResults.RemoveRange(_db.PayrollValidationResults.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        // POD-B2 — the run's population selections have no FK (nothing in this schema does), so remove
        // them here or they would outlive the run as orphans and be re-resolved onto a recycled id.
        _db.PayrollRunEmployeeSelections.RemoveRange(_db.PayrollRunEmployeeSelections.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        _db.PayrollRuns.Remove(run);
        await PayrollAudit("payroll.run.deleted", "PayrollRun", id.ToString(), new { run.Year, run.Month, run.RunType }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("runs/{id:guid}/gl-journal")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> GlJournal(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var entries = new List<(string Code, string Name, string Account, string AccountName, string EntryType, decimal Amount)>();

        // Once a run is locked, its GL is posted to the immutable FinanceGlEntries ledger. The
        // "GL Journal" for such a run MUST reflect what was actually posted — NOT a live re-projection
        // through the *current* tenant mappings, which a Finance user may have edited in Setup after the
        // lock. Reading the posted entries keeps the accounting artifact truthful over a closed period.
        var posted = await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id)
            .ToListAsync(cancellationToken);

        var isPosted = posted.Count > 0;
        if (isPosted)
        {
            foreach (var e in posted)
            {
                var isDebit = !string.IsNullOrEmpty(e.DebitAccount);
                var (code, name) = SplitAccountLabel(isDebit ? e.DebitAccount : e.CreditAccount);
                var (compCode, compName) = DescribePostedLine(e.Description);
                entries.Add((compCode, compName, code, name, isDebit ? "DR" : "CR", e.Amount));
            }
            // Deterministic display order: debits first, then credits, each by account code.
            entries = entries.OrderByDescending(x => x.EntryType).ThenBy(x => x.Account, StringComparer.Ordinal).ToList();
        }
        else
        {
            // Draft/unposted preview: resolve every line through the SAME tenant GL mappings + routing
            // the lock path uses (LoadGlOverridesAsync + the shared driver-key helpers), so what a Finance
            // user maps in Setup is exactly what they see here — and exactly what will post on lock.
            var earnings  = await _db.PayrollEarnings.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken);
            var deductions = await _db.PayrollDeductions.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken);
            var totalNet   = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == id).SumAsync(x => x.NetSalary, cancellationToken);
            var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken);
            // POD-B1b — preview MUST mirror the posted journal (see the doctrine note above
            // AddEarning/AddDeduction): a bonus covered by a live accrual will DR the payable on lock,
            // not the bonus expense, so the preview shows exactly that.
            var previewBonusTotal = earnings.Where(e => e.Source == "Bonus").Sum(e => e.Amount);
            var previewClearings = await BonusGlLedger.BuildPayrollClearingAsync(
                _db, tenantId, id, run.CompanyId, previewBonusTotal, cancellationToken);
            var previewClearsBonus = previewClearings.Count > 0;

            decimal previewBonusSkipped = 0m;
            // POD-B1b-FIX (P1-7) — mirror the posted journal's per-component remainder routing exactly.
            var previewDeferredBonus = new List<(string Code, string Name, string Driver, decimal Amount)>();
            foreach (var grp in earnings.GroupBy(e => e.ComponentCode))
            {
                var first = grp.First();
                var driver = ResolveDriverForComponent(glCtx.Drivers, grp.Key, first.Source, GlDriverCategories.Earning)?.Key
                             ?? EarningDriverKey(grp.Key, first.Source);
                if (previewClearsBonus && first.Source == "Bonus")
                {
                    var bonusAmount = grp.Sum(e => e.Amount);
                    previewBonusSkipped += bonusAmount;
                    previewDeferredBonus.Add((grp.Key, first.ComponentName, driver, bonusAmount));
                    continue;
                }
                var (acct, aName) = ResolveGlAccount(driver, glCtx.Overrides, glCtx.DriverDefaults);
                entries.Add((grp.Key, first.ComponentName, acct, aName, "DR", grp.Sum(e => e.Amount)));
            }

            if (previewClearsBonus)
            {
                decimal previewCleared = 0m;
                var previewClearedByCode = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var c in previewClearings)
                {
                    if (c.Amount <= 0m) continue;
                    previewCleared += c.Amount;
                    foreach (var kv in c.ByComponentCode)
                        previewClearedByCode[kv.Key] = previewClearedByCode.GetValueOrDefault(kv.Key) + kv.Value;
                    var (code, name) = SplitAccountLabel(c.AccrualAccount);
                    entries.Add(("BONUS_PAYABLE", $"Bonus payable cleared ({c.BatchRef})", code, name, "DR", c.Amount));
                }
                var previewRemainder = Math.Round(previewBonusSkipped - previewCleared, 2);
                if (previewRemainder > 0m && previewDeferredBonus.Count > 0 && previewBonusSkipped > 0m)
                {
                    // Same shortfall-first allocation and same ordering as BuildPayrollGlEntries (re-audit
                    // #5), so preview == posting line for line.
                    var ordered = previewDeferredBonus.OrderBy(g => g.Code, StringComparer.Ordinal).ToList();
                    var shortfall = ordered
                        .Select(g => Math.Max(0m, Math.Round(g.Amount - previewClearedByCode.GetValueOrDefault(g.Code), 2)))
                        .ToList();
                    var useShortfall = shortfall.Sum() > 0m;
                    var weights = useShortfall ? shortfall : ordered.Select(g => g.Amount).ToList();
                    var weightTotal = weights.Sum();
                    var slice = previewRemainder;
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var share = i == ordered.Count - 1
                            ? slice
                            : Math.Min(slice, Math.Round(previewRemainder * (weights[i] / weightTotal), 2));
                        slice = Math.Round(slice - share, 2);
                        if (share <= 0m) continue;
                        var (rc, rn) = ResolveGlAccount(ordered[i].Driver, glCtx.Overrides, glCtx.DriverDefaults);
                        entries.Add((ordered[i].Code, $"{ordered[i].Name} (un-accrued)", rc, rn, "DR", share));
                    }
                }
                else if (previewRemainder > 0m)
                {
                    var (rc, rn) = ResolveGlAccount("EARN:BONUS", glCtx.Overrides, glCtx.DriverDefaults);
                    entries.Add(("BONUS", "Bonus (un-accrued)", rc, rn, "DR", previewRemainder));
                }
            }

            var employerExpenseByPairKey = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var grp in deductions.GroupBy(d => new { d.ComponentCode, d.Source }))
            {
                var first = grp.First();
                var driverRow = ResolveDriverForComponent(glCtx.Drivers, grp.Key.ComponentCode, grp.Key.Source, GlDriverCategories.Deduction);
                string driver;
                if (driverRow is not null)
                {
                    driver = driverRow.Key;
                    if (driverRow.EmitsEmployerExpensePair)
                    {
                        var pk = driverRow.PairedExpenseDriverKey ?? "EMPLOYER_STATUTORY_EXPENSE";
                        employerExpenseByPairKey[pk] = employerExpenseByPairKey.GetValueOrDefault(pk) + grp.Sum(d => d.Amount);
                    }
                }
                else
                {
                    driver = DeductionDriverKey(grp.Key.ComponentCode, grp.Key.Source, out var isEr);
                    if (isEr) employerExpenseByPairKey["EMPLOYER_STATUTORY_EXPENSE"] = employerExpenseByPairKey.GetValueOrDefault("EMPLOYER_STATUTORY_EXPENSE") + grp.Sum(d => d.Amount);
                }
                var (acct, aName) = ResolveGlAccount(driver, glCtx.Overrides, glCtx.DriverDefaults);
                entries.Add((grp.Key.ComponentCode, first.ComponentName, acct, aName, "CR", grp.Sum(d => d.Amount)));
            }

            // Employer statutory expense DR balances the employer-social-insurance CR liability posted above.
            foreach (var (pairKey, amount) in employerExpenseByPairKey.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (amount <= 0) continue;
                var (c, n) = ResolveGlAccount(pairKey, glCtx.Overrides, glCtx.DriverDefaults);
                entries.Add(("SOCIAL_INS_ER_DR", "Employer Social Insurance Expense", c, n, "DR", amount));
            }

            // Net salary payable CR balances all earning DRs net of deduction CRs.
            var (netCode, netName) = ResolveGlAccount("NET_PAYABLE", glCtx.Overrides, glCtx.DriverDefaults);
            entries.Add(("NET_SALARY", "Net Salary Payable", netCode, netName, "CR", totalNet));
        }

        var totalDebits  = entries.Where(e => e.EntryType == "DR").Sum(e => e.Amount);
        var totalCredits = entries.Where(e => e.EntryType == "CR").Sum(e => e.Amount);

        return Ok(new
        {
            runId    = id,
            period   = $"{run.Year}-{run.Month:D2}",
            isPosted, // true = immutable as-posted ledger; false = live preview through current mappings
            entries  = entries.Select(e => new { componentCode = e.Code, componentName = e.Name, glAccount = e.Account, glAccountName = e.AccountName, entryType = e.EntryType, amount = e.Amount }),
            totalDebits, totalCredits,
            isBalanced = Math.Abs(totalDebits - totalCredits) < 0.01m
        });
    }

    [HttpPost("runs/{id:guid}/payslips/generate")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> GeneratePayslips(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var slips = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == id).ToListAsync(cancellationToken);
        // M2: load itemized earnings and deductions for proper payslip line items
        var earnings = await _db.PayrollEarnings.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken);
        var deductions = await _db.PayrollDeductions.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken);
        // Stamp the current default template version on each generated payslip for immutable history
        var defaultTemplate = await _db.PayslipTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.IsDefault)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        foreach (var slip in slips)
        {
            if (await _db.Payslips.AnyAsync(x => x.TenantId == tenantId && x.PayrollRunId == id && x.EmployeeId == slip.EmployeeId, cancellationToken)) continue;
            var payslip = new Payslip { TenantId = tenantId, PayrollRunId = id, EmployeeId = slip.EmployeeId, PayslipNumber = $"PS-{slip.EmployeeCode}-{DateTime.UtcNow:yyyyMMddHHmmss}", PayslipTemplateId = defaultTemplate };
            _db.Payslips.Add(payslip);
            foreach (var e in earnings.Where(x => x.EmployeeId == slip.EmployeeId))
                _db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Earning", ComponentName = e.ComponentName, Amount = e.Amount });
            foreach (var d in deductions.Where(x => x.EmployeeId == slip.EmployeeId))
                _db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Deduction", ComponentName = d.ComponentName, Amount = d.Amount });
            _db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Net", ComponentName = "Net pay", Amount = slip.NetSalary });
        }
        await PayrollAudit("payroll.payslips.generated", "PayrollRun", id.ToString(), null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await _db.Payslips.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken));
    }

    /// <summary>
    /// Creates a WPS payment batch for a Locked payroll run.
    /// Requires payroll.export permission (export role) and a fully locked run.
    /// </summary>
    [HttpPost("runs/{id:guid}/payment-batches")]
    public async Task<IActionResult> CreatePaymentBatch(Guid id, PayrollPaymentBatchRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();
        // C5: payment batch requires a locked run — approval workflow must complete first
        if (run.Status != "Locked")
            return BadRequest(new { message = "Payment batches can only be created for Locked runs. Approve and lock the run before creating a payment batch." });
        // L2: prevent duplicate payment batches on the same run
        if (await _db.PayrollPaymentBatches.AnyAsync(x => x.TenantId == tenantId && x.PayrollRunId == id, cancellationToken))
            return Conflict(new { message = "A payment batch already exists for this payroll run." });
        var slips    = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == id).ToListAsync(cancellationToken);
        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(cancellationToken);
        var currency = req.Currency ?? await ResolveCurrencyAsync(tenantId, cancellationToken);
        // POD-B2 — BatchNumber has no unique index and two runs in a period differ only by seconds. A
        // 2-char type tag plus a short id fragment makes an off-cycle batch legible to an operator and
        // collision-proof between concurrent batches in the same second.
        var runTypeTag = run.RunType switch
        {
            PayrollRunTypes.OffCycle      => "OC",
            PayrollRunTypes.Supplementary => "SU",
            PayrollRunTypes.Correction    => "CO",
            _                             => "RG",
        };
        var batch    = new PayrollPaymentBatch
        {
            TenantId      = tenantId,
            PayrollRunId  = id,
            BatchNumber   = run.RunType == PayrollRunTypes.Regular
                ? $"PAY-{run.Year}{run.Month:00}-{DateTime.UtcNow:HHmmss}"
                : $"PAY-{run.Year}{run.Month:00}-{runTypeTag}-{DateTime.UtcNow:HHmmss}-{id.ToString("N")[..4]}",
            PaymentMethod = req.PaymentMethod ?? "WPS",
            TotalAmount   = slips.Sum(x => x.NetSalary),
            Currency      = currency,
            WpsStatus     = WpsStatuses.Draft,
        };
        _db.PayrollPaymentBatches.Add(batch);
        foreach (var slip in slips)
        {
            var profile = profiles.FirstOrDefault(x => x.EmployeeId == slip.EmployeeId);
            if (string.IsNullOrWhiteSpace(profile?.Iban))
                _db.PayrollValidationResults.Add(new PayrollValidationResult { TenantId = tenantId, PayrollRunId = id, EmployeeId = slip.EmployeeId, Severity = "Warning", Code = "MISSING_IBAN", Message = "Employee is missing IBAN for payment file." });
            _db.PayrollPaymentRecords.Add(new PayrollPaymentRecord { TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = slip.EmployeeId, Amount = slip.NetSalary, Iban = profile?.Iban ?? string.Empty, Status = "Pending", WpsReference = $"WPS-{slip.EmployeeCode}-{run.Year}{run.Month:00}" });
        }
        // POD-B2 (M5b) — restate the hold-out count at the moment money is queued for disbursement.
        var batchExcludedCount = await _db.PayrollRunEmployeeSelections.AsNoTracking()
            .CountAsync(s => s.TenantId == tenantId && s.PayrollRunId == id
                          && s.Outcome == PayrollRunSelectionOutcomes.Excluded, cancellationToken);
        await PayrollAudit("payroll.payment_batch.created", "PayrollPaymentBatch", batch.Id.ToString(),
            new { totalAmount = batch.TotalAmount, method = batch.PaymentMethod, runType = run.RunType, excludedCount = batchExcludedCount }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/payroll/payment-batches/{batch.Id}", new
        {
            batch.Id, batch.TenantId, batch.PayrollRunId, batch.BatchNumber, batch.PaymentMethod,
            batch.TotalAmount, batch.Currency, batch.WpsStatus,
            runType       = run.RunType,
            parentRunId   = run.ParentRunId,
            excludedCount = batchExcludedCount,
        });
    }

    /// <summary>
    /// Pre-export WPS validation using the WpsSifValidator.
    /// Returns blocking errors and warnings. The same checks are re-enforced inside
    /// GenerateWps — the frontend result is advisory only.
    /// Requires payroll.export permission.
    /// </summary>
    [HttpPost("runs/{id:guid}/wps-validation")]
    public async Task<IActionResult> WpsValidation(Guid id, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var slips    = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == id).ToListAsync(cancellationToken);
        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(cancellationToken);
        var empIds   = slips.Select(s => s.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && empIds.Contains(e.Id)).ToListAsync(cancellationToken);

        var (hardBlocked, driftWarn) = await ComputePayReadinessAsync(tenantId, employees, cancellationToken);
        var result = Infrastructure.Payroll.WpsSifValidator.Validate(run, slips, profiles, employees, hardBlocked);

        return Ok(new
        {
            runId        = id,
            canExport    = result.CanExport,
            errorCount   = result.ErrorCount,
            warningCount = result.WarningCount,
            blockingErrors = result.BlockingErrors,
            warnings       = result.Warnings,
            // §6.6: already-Active employees who drifted pay-blocked after a policy change. Surfaced
            // (never silently dropped); GenerateWps requires explicit acknowledgement to proceed.
            readinessDrift = driftWarn.ToArray(),
        });
    }

    /// <summary>
    /// Generates the SIF file for a WPS payment batch using the isolated SifFileGenerator.
    /// Stores metadata (hash, format version, employee count, total) on WPSFileBatch.
    /// Blocks re-generation once a file has been created — use retry after Rejected if needed.
    /// Requires payroll.export permission.
    /// </summary>
    [HttpPost("payment-batches/{id:guid}/wps-file")]
    public async Task<IActionResult> GenerateWps(Guid id, [FromQuery] bool acknowledgeReadinessDrift, [FromQuery] bool acknowledgeSiblingWpsExport, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var batch = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (batch is null) return NotFound();

        var existingWpsFiles = await _db.WPSFileBatches
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == id)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        // Idempotency guard: block silent re-generation except after a rejected statutory filing.
        if (existingWpsFiles.Count > 0 && batch.WpsStatus != WpsStatuses.Rejected)
            return Conflict(new
            {
                error   = "already_generated",
                message = "A WPS file already exists for this batch. Download the existing file or update the batch status to Rejected to allow a new export.",
            });

        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batch.PayrollRunId, cancellationToken);

        // Run-level eligibility check (backend-enforced, never trusts frontend).
        if (run is null || (run.Status is not ("Approved" or "Locked" or "Paid")))
            return BadRequest(new { error = "run_not_exportable", message = "Payroll run must be Approved (or Locked/Paid) before WPS export." });

        // ── POD-B2 (M9): a second SIF for the same establishment + salary month ──────────────────────
        // Mudad / UAE WPS treat a second file for the same (establishment, salary month) as a
        // REPLACEMENT, not an addition. Generating one silently after a sibling run's batch was already
        // accepted or paid invites an underpayment the bank accepts without complaint. This is a payment-
        // integrity risk, not a labelling problem, so it hard-blocks until acknowledged.
        // [PRODUCT DECISION OUTSTANDING: whether a period's runs should be exported as ONE aggregated SIF
        //  or as separate files is bank/MOL-specific and needs sign-off before the first tenant does this
        //  in anger. Until then the operator must consciously choose.]
        var siblingRunsForWps = await LoadSiblingRunsAsync(tenantId, run, run.CompanyId ?? Guid.Empty, cancellationToken);
        if (siblingRunsForWps.Count > 0 && !acknowledgeSiblingWpsExport)
        {
            var siblingRunIdsForWps = siblingRunsForWps.Select(r => r.Id).ToList();
            var conflictingBatches = await _db.PayrollPaymentBatches.AsNoTracking()
                .Where(b => b.TenantId == tenantId && siblingRunIdsForWps.Contains(b.PayrollRunId)
                         && (b.WpsStatus == WpsStatuses.Accepted || b.WpsStatus == WpsStatuses.Paid
                          || b.WpsStatus == WpsStatuses.Submitted || b.WpsStatus == WpsStatuses.Reconciled))
                .Select(b => new { b.Id, b.BatchNumber, b.WpsStatus, b.PayrollRunId, b.TotalAmount })
                .ToListAsync(cancellationToken);
            if (conflictingBatches.Count > 0)
                return UnprocessableEntity(new
                {
                    error   = "sibling_wps_export_exists",
                    message = $"Another payroll run for {run.Year}-{run.Month:D2} in this legal entity already has a " +
                              "submitted/accepted/paid WPS batch. Banks and Mudad treat a second SIF for the same " +
                              "salary month as a REPLACEMENT of the first, which would under-pay the employees in " +
                              "that batch. Confirm with your bank whether a second file is additive, then re-submit " +
                              "with acknowledgeSiblingWpsExport=true.",
                    period  = $"{run.Year}-{run.Month:D2}",
                    runType = run.RunType,
                    conflictingBatches,
                });
        }

        // Resolve company → pack exporter; guard if no pack configured for this jurisdiction.
        var wpsCompany = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId, cancellationToken);
        var wpsCc  = wpsCompany?.CountryCode  ?? string.Empty;
        var wpsJur = wpsCompany?.Jurisdiction ?? string.Empty;
        var exporter = _packResolver.ResolveWageProtectionExporter(wpsCc, wpsJur);

        // P5 guard: block export if no jurisdiction-specific pack is registered.
        if (exporter is DefaultWageProtectionExporter)
            return UnprocessableEntity(new
            {
                error       = "no_wps_pack_configured",
                message     = $"No WPS exporter is configured for company jurisdiction '{wpsCc}/{wpsJur}'. " +
                              "Configure the company's Country and Jurisdiction in Setup → Companies before exporting.",
                countryCode = wpsCc,
                jurisdiction = wpsJur,
            });

        var records  = await _db.PayrollPaymentRecords.AsNoTracking().Where(x => x.TenantId == tenantId && x.PaymentBatchId == id).ToListAsync(cancellationToken);
        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(cancellationToken);
        var empIds   = records.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && empIds.Contains(e.Id)).ToListAsync(cancellationToken);

        // Full validator: same rules as WpsValidation endpoint.
        var slips = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == run.Id).ToListAsync(cancellationToken);
        var (hardBlocked, driftWarn) = await ComputePayReadinessAsync(tenantId, employees, cancellationToken);
        var validation = Infrastructure.Payroll.WpsSifValidator.Validate(run, slips, profiles, employees, hardBlocked);
        if (!validation.CanExport)
            return BadRequest(new
            {
                error          = "validation_failed",
                message        = "WPS export blocked by validation errors. Resolve all blocking issues and retry.",
                errorCount     = validation.ErrorCount,
                blockingErrors = validation.BlockingErrors,
            });
        // §6.6: already-Active employees who drifted pay-blocked after a policy change are NEVER silently
        // dropped from a wage file (that could miss a mandated salary). They are surfaced and require an
        // explicit acknowledgement to proceed — the export then includes them (their pay is not withheld).
        if (driftWarn.Count > 0 && !acknowledgeReadinessDrift)
            return UnprocessableEntity(new
            {
                error = "readiness_drift_acknowledgement_required",
                message = $"{driftWarn.Count} active employee(s) no longer meet the current readiness policy. "
                          + "Review and re-submit with acknowledgeReadinessDrift=true to include them, or fix their details first.",
                driftedEmployeeIds = driftWarn.ToArray(),
            });

        var gcc         = await _db.GCCComplianceSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        var agentId     = gcc?.WpsAgentId ?? "0000000000";
        var currency    = !string.IsNullOrWhiteSpace(batch.Currency) && batch.Currency != "USD"
                            ? batch.Currency
                            : await ResolveCurrencyAsync(tenantId, cancellationToken);

        // Build WpsEmployee list from payment records + employee snapshot data.
        var profileByEmpId = profiles.ToDictionary(p => p.EmployeeId);
        var slipByEmpId    = slips.ToDictionary(s => s.EmployeeId);
        var empById        = employees.ToDictionary(e => e.Id);

        var wpsEmployees = records.Select(record =>
        {
            var emp     = empById.TryGetValue(record.EmployeeId, out var em) ? em : null;
            var ppf     = profileByEmpId.TryGetValue(record.EmployeeId, out var pr) ? pr : null;
            var slip    = slipByEmpId.TryGetValue(record.EmployeeId, out var sl) ? sl : null;
            var code    = emp?.EmployeeCode ?? record.EmployeeId.ToString();
            return new WpsEmployee(
                EmployeeId:     record.EmployeeId,
                EmployeeCode:   code,
                FullNameEn:     emp?.FullName    ?? code,
                FullNameAr:     string.Empty,
                Nationality:    emp?.Nationality ?? string.Empty,
                NationalId:     ppf?.MolId       ?? string.Empty,
                IbanOrAccount:  record.Iban,
                BankCode:       ppf?.BankRoutingCode ?? string.Empty,
                Salary: new SalaryBreakdown(
                    slip?.BasicSalary         ?? 0m,
                    slip?.HousingAllowance    ?? 0m,
                    slip?.TransportAllowance  ?? 0m,
                    slip?.OtherAllowances     ?? 0m),
                NetPay: record.Amount);
        }).ToList();

        var exportInput = new WageProtectionExportInput(
            TenantId:        tenantId,
            CompanyId:       run.CompanyId ?? Guid.Empty,
            PayrollRunId:    run.Id,
            PeriodYear:      run.Year,
            PeriodMonth:     run.Month,
            EstablishmentId: agentId,
            EmployerIban:    string.Empty,
            CompanyNameEn:   wpsCompany?.LegalNameEn ?? string.Empty,
            CompanyNameAr:   wpsCompany?.LegalNameAr ?? string.Empty,
            Employees:       wpsEmployees);

        var exportResult = await exporter.ExportAsync(exportInput, cancellationToken);

        // Compute SHA-256 of generated bytes for integrity tracking.
        var fileHash = Convert.ToHexString(SHA256.HashData(exportResult.FileBytes)).ToLowerInvariant();

        // Build SIF records in DB for audit snapshot (IBAN/NetPay/MolId preserved at time of export).
        var employeeCodeMap = employees.ToDictionary(e => e.Id, e => e.EmployeeCode);
        var wps = new WPSFileBatch
        {
            TenantId           = tenantId,
            PaymentBatchId     = id,
            SifFileName        = exportResult.FileName,
            GeneratedByUserId  = GetUserId(),
            FormatVersion      = exportResult.Format,   // e.g. "mudad-xml", "mohre-sif", "qcb-sif"
            FilingStatus       = WpsStatuses.Generated,
            ResubmissionOfWpsFileBatchId = existingWpsFiles.FirstOrDefault()?.Id,
            ResubmissionNumber = existingWpsFiles.Count,
        };
        _db.WPSFileBatches.Add(wps);

        var profileByEmpId2 = profiles.ToDictionary(p => p.EmployeeId);
        var sifRows = new List<SIFFileRecord>();
        foreach (var record in records)
        {
            var code   = employeeCodeMap.TryGetValue(record.EmployeeId, out var c) ? c : record.EmployeeId.ToString();
            var ppf    = profileByEmpId2.TryGetValue(record.EmployeeId, out var pr) ? pr : null;
            var row    = new SIFFileRecord
            {
                TenantId       = tenantId,
                WPSFileBatchId = wps.Id,
                EmployeeId     = record.EmployeeId,
                EmployeeCode   = code,
                Iban           = record.Iban,
                NetPay         = record.Amount,
                MolId          = ppf?.MolId ?? string.Empty,
                RoutingCode    = ppf?.BankRoutingCode ?? string.Empty,
            };
            _db.SIFFileRecords.Add(row);
            sifRows.Add(row);
        }

        // Pack the metadata from the exporter result (replaces hardcoded SifFileGenerator.FormatVersion).
        var genResult = (
            EmployeeCount:     exportResult.RecordCount,
            TotalSalaryAmount: wpsEmployees.Sum(w => w.NetPay),
            FileHash:          fileHash,
            FormatVersion:     exportResult.Format,
            ContentBytes:      exportResult.FileBytes
        );

        wps.EmployeeCount     = genResult.EmployeeCount;
        wps.TotalSalaryAmount = genResult.TotalSalaryAmount;
        wps.FileHash          = genResult.FileHash;

        batch.Status    = "FileGenerated";
        batch.WpsStatus = WpsStatuses.Generated;
        batch.WpsStatusChangedAtUtc = DateTime.UtcNow;

        await PayrollAudit("payroll.wps.generated", "WPSFileBatch", wps.Id.ToString(), new
        {
            batchId       = id,
            employeeCount = genResult.EmployeeCount,
            totalAmount   = genResult.TotalSalaryAmount,
            fileHash      = genResult.FileHash,
            formatVersion = genResult.FormatVersion,
        }, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            wps.Id,
            wps.SifFileName,
            wps.Status,
            wps.FormatVersion,
            wps.FileHash,
            wps.EmployeeCount,
            wps.TotalSalaryAmount,
            wps.GeneratedByUserId,
            wps.CreatedAtUtc,
        });
    }

    /// <summary>
    /// Updates the WPS submission status with lifecycle transition enforcement.
    /// Only allowed transitions are accepted (e.g. Generated → Downloaded, Submitted → Accepted|Rejected).
    /// Requires payroll.export permission.
    /// </summary>
    [HttpPost("payment-batches/{batchId:guid}/wps-status")]
    public async Task<IActionResult> UpdateWpsStatus(Guid batchId, [FromBody] WpsStatusRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        if (!WpsStatuses.All.Contains(req.Status))
            return BadRequest(new { error = "invalid_status", message = $"Status must be one of: {string.Join(", ", WpsStatuses.All)}." });

        var tenantId = GetTenantId();
        var batch    = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();

        var from = batch.WpsStatus;

        var latestFile = await _db.WPSFileBatches
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batchId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestFile is null)
            return BadRequest(new { error = "wps_file_missing", message = "A generated WPS file is required before statutory filing status can be changed." });

        if (req.Status is WpsStatuses.Submitted && string.IsNullOrWhiteSpace(req.Reference))
            return BadRequest(new { error = "submission_reference_required", message = "A statutory submission reference is required when marking WPS as Submitted." });

        if (req.Status is WpsStatuses.Accepted && string.IsNullOrWhiteSpace(req.Reference))
            return BadRequest(new { error = "acknowledgement_reference_required", message = "An acknowledgement reference is required when marking WPS as Accepted." });

        if (req.Status is WpsStatuses.Rejected && string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new { error = "rejection_reason_required", message = "A rejection reason is required when marking WPS as Rejected." });

        // Enforce lifecycle: only allowed transitions are accepted.
        if (!WpsTransitions.IsAllowed(from, req.Status))
        {
            var allowed = WpsTransitions.AllowedFrom(from);
            return BadRequest(new
            {
                error   = "invalid_transition",
                message = $"Cannot transition WPS status from '{from}' to '{req.Status}'.",
                allowedTransitions = allowed,
            });
        }

        // POD-B1 (P1-6) — a batch may not reach the terminal Reconciled state until net pay has actually
        // been settled to GL (2100 Salaries Payable cleared against Cash/Bank). This gates the legacy
        // Accepted→Reconciled edge at runtime without deleting it from the transition table (WpsTests
        // still asserts the edge). The supported path is Accepted →(settle)→ Paid → Reconciled.
        if (req.Status is WpsStatuses.Reconciled)
        {
            var settlementPosted = await _db.FinanceGlEntries.AnyAsync(
                x => x.TenantId == tenantId && x.SourceModule == "Payroll"
                  && x.SourceEntityId == batch.PayrollRunId
                  && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed, cancellationToken);
            if (!settlementPosted)
                return UnprocessableEntity(new
                {
                    error   = "settlement_required",
                    message = "Settle the net pay (POST payment-batches/{id}/settle) before reconciling — "
                            + "reconciling now would close the batch with Salaries Payable (2100) still open.",
                });
        }

        batch.WpsStatus = req.Status;
        batch.WpsStatusChangedAtUtc = DateTime.UtcNow;
        latestFile.FilingStatus = req.Status;
        if (req.Status is WpsStatuses.Submitted)
        {
            batch.WpsSubmissionReference = req.Reference;
            latestFile.SubmissionReference = req.Reference;
            latestFile.SubmittedAtUtc = DateTime.UtcNow;
        }
        else if (req.Status is WpsStatuses.Accepted)
        {
            batch.WpsSubmissionReference = req.Reference;
            latestFile.SubmissionReference = req.Reference;
            latestFile.AcknowledgedAtUtc = DateTime.UtcNow;
        }
        else if (req.Status is WpsStatuses.Rejected)
        {
            batch.WpsRejectionReason = req.Notes;
            latestFile.RejectionReason = req.Notes;
            latestFile.RejectedAtUtc = DateTime.UtcNow;
        }
        await PayrollAudit("payroll.wps.status_changed", "PayrollPaymentBatch", batchId.ToString(),
            new { from, to = req.Status, reference = req.Reference, notes = req.Notes }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { batchId, wpsStatus = batch.WpsStatus });
    }

    [HttpPost("runs/{id:guid}/erp-posting-status")]
    public async Task<IActionResult> UpdateErpPostingStatus(Guid id, [FromBody] ErpPostingStatusRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        if (!ErpPostingStatuses.All.Contains(req.Status))
            return BadRequest(new { error = "invalid_status", message = $"Status must be one of: {string.Join(", ", ErpPostingStatuses.All)}." });

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var glEntries = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id)
            .ToListAsync(cancellationToken);
        if (glEntries.Count == 0)
            return BadRequest(new { error = "gl_not_posted", message = "Balanced payroll GL must be persisted before ERP posting status can change." });

        var from = run.ErpPostingStatus;
        if (!ErpPostingTransitions.IsAllowed(from, req.Status))
            return BadRequest(new { error = "invalid_transition", message = $"Cannot transition ERP posting status from '{from}' to '{req.Status}'.", allowedTransitions = ErpPostingTransitions.AllowedFrom(from) });

        if (req.Status is ErpPostingStatuses.Exported or ErpPostingStatuses.Posted)
        {
            if (string.IsNullOrWhiteSpace(req.Reference))
                return BadRequest(new { error = "erp_reference_required", message = "ERP document/export reference is required for this status." });
        }
        if (req.Status is ErpPostingStatuses.Rejected && string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new { error = "erp_rejection_reason_required", message = "ERP rejection reason is required." });

        run.ErpPostingStatus = req.Status;
        run.ErpPostingStatusChangedAtUtc = DateTime.UtcNow;
        run.ErpPostingReference = req.Reference;
        run.ErpPostingFailureReason = req.Status == ErpPostingStatuses.Rejected ? req.Notes : null;
        foreach (var entry in glEntries)
        {
            entry.ErpPostingStatus = req.Status;
            entry.ErpStatusChangedAtUtc = DateTime.UtcNow;
            entry.ErpDocumentNumber = req.Reference;
            entry.ErpRejectionReason = req.Status == ErpPostingStatuses.Rejected ? req.Notes : null;
        }

        await PayrollAudit("payroll.erp_posting.status_changed", "PayrollRun", id.ToString(),
            new { from, to = req.Status, reference = req.Reference, notes = req.Notes }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { runId = id, erpPostingStatus = run.ErpPostingStatus, reference = run.ErpPostingReference });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // POD-B1 — GL SETTLEMENT & STATUTORY REMITTANCE
    //
    // Lifecycle: Lock (accrue liabilities) → Pay (settle net) → Remit (settle statutory). The accrual
    // (Lock) CREATES the control-account liabilities (2100 net, 2101/2106 GOSI, 2102 tax, 2107 loan).
    // These endpoints CLEAR them against Cash/Bank so — after Pay + Remit — every control account nets
    // to zero and 1000 Cash/Bank carries the actual outflow, with ZERO manual journal entries.
    //
    // Remap-immunity (consultant P0-1): both builders CLEAR THE EXACT ACCRUAL LINES — they DR each
    // accrual credit line's STORED account/amount verbatim and only RESOLVE Cash/Bank for the credit
    // side. So a chart-of-accounts remap between Lock and Pay/Remit can never make a control account
    // drift off zero (the gl_unbalanced guard only proves per-journal balance, never cross-journal
    // tie-out — which is exactly why we clear the stored lines instead of re-resolving accounts).
    //
    // Period (consultant P0-2): the cash entry is dated into the PAYMENT / REMIT period, not the
    // accrual period, and the closed-period guard tests THAT period — so an accrual month can close on
    // schedule with the payable legitimately carrying into the settlement month (standard accrual
    // accounting). "Net to zero" holds cumulatively across the run's ledger.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NET-PAY SETTLEMENT (Pay). When a payment batch is confirmed paid (WPS/bank Accepted), posts a
    /// balanced DR 2100 Salaries Payable / CR 1000 Cash-Bank journal for the accrued net — clearing 2100
    /// and booking the outflow. Idempotent (mirrors the Lock guard), and reversible via
    /// settle/reverse. Requires payroll.export.
    /// </summary>
    [HttpPost("payment-batches/{batchId:guid}/settle")]
    public async Task<IActionResult> SettlePaymentBatch(Guid batchId, [FromBody] SettlePaymentBatchRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var batch = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batch.PayrollRunId, cancellationToken);
        if (run is null) return NotFound();

        if (run.Status != "Locked")
            return BadRequest(new { error = "run_not_locked", message = "Only a Locked run can be settled." });
        if (batch.WpsStatus != WpsStatuses.Accepted)
            return BadRequest(new { error = "batch_not_accepted", message = $"Only an Accepted payment batch can be settled (current: {batch.WpsStatus}). Mark the WPS/bank file Accepted first." });

        // Accrual must exist — cannot settle an un-accrued run.
        var accrualLines = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == run.Id
                     && x.EventType == GlEventTypes.Accrual && !x.IsReversed)
            .ToListAsync(cancellationToken);
        if (accrualLines.Count == 0)
            return BadRequest(new { error = "gl_not_accrued", message = "This run has no accrual GL. Lock the run before settling." });

        // P1-7 — do not book cash that never left: block while any payment record is Rejected (bounced
        // IBAN / failed transfer). Pending records are fine (WPS instructed but not yet cleared).
        var rejectedCount = await _db.PayrollPaymentRecords
            .CountAsync(x => x.TenantId == tenantId && x.PaymentBatchId == batch.Id && x.Status == "Rejected", cancellationToken);
        if (rejectedCount > 0)
            return UnprocessableEntity(new { error = "payment_records_rejected", message = $"{rejectedCount} payment record(s) are Rejected. Resolve/retry them before settling so Cash/Bank reflects the actual outflow.", rejectedCount });

        // Idempotency (mirror Lock).
        var alreadySettled = await _db.FinanceGlEntries.AnyAsync(
            x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == run.Id
              && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed, cancellationToken);
        if (alreadySettled)
            return Conflict(new { error = "already_settled", message = "This batch's net pay has already been settled to GL." });

        // Date into the PAYMENT period; guard THAT period (P0-2).
        var paidDate = req.PaidDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var settlementPeriod = $"{paidDate.Year}-{paidDate.Month:D2}";
        if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run.CompanyId, settlementPeriod, cancellationToken))
            return UnprocessableEntity(new { error = "gl_period_closed", message = $"GL period {settlementPeriod} is closed. Reopen it before settling payments into it.", period = settlementPeriod, companyId = run.CompanyId });

        // Clear the EXACT accrual NET_PAYABLE line(s) verbatim (remap-immune), CR Cash/Bank.
        var netLines = accrualLines
            .Where(l => string.IsNullOrEmpty(l.DebitAccount) && l.Description == PayrollGlDescriptions.NetPayable)
            .ToList();
        if (netLines.Count == 0 || netLines.Sum(l => l.Amount) <= 0m)
            return BadRequest(new { error = "nothing_to_settle", message = "No open net-pay liability was found on the accrual to settle." });

        var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken);
        var (cashCode, cashName) = ResolveGlAccount("CASH_BANK", glCtx.Overrides, glCtx.DriverDefaults);
        var cashAccount = $"{cashCode} - {cashName}";
        var currency = netLines[0].Currency;

        var (lines, dr, cr) = BuildLiabilityClearingGl(
            tenantId, run.Id, settlementPeriod, batch.BatchNumber, GlEventTypes.NetSettlement,
            netLines, cashAccount, GetUserId(), GetUserName(), currency,
            $"Net pay settlement — batch {batch.BatchNumber}", run.CompanyId);
        if (Math.Abs(dr - cr) > 0.01m)
            return UnprocessableEntity(new { error = "gl_unbalanced", message = "Net-pay settlement GL is not balanced.", totalDebits = dr, totalCredits = cr });

        _db.FinanceGlEntries.AddRange(lines);
        batch.WpsStatus = WpsStatuses.Paid;
        batch.WpsStatusChangedAtUtc = DateTime.UtcNow;
        await PayrollAudit("payroll.batch.settled", "PayrollPaymentBatch", batch.Id.ToString(),
            new { runId = run.Id, batch.BatchNumber, amount = cr, cashAccount, period = settlementPeriod, reference = req.Reference }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { batchId = batch.Id, runId = run.Id, wpsStatus = batch.WpsStatus, settled = cr, cashAccount, period = settlementPeriod });
    }

    /// <summary>
    /// Reverses a net-pay settlement via contra entries (re-opening 2100) and reverts the batch to
    /// Accepted so operational state matches the ledger. Blocks if the settlement period is closed
    /// (P0-3 — force an audited reopen). Requires payroll.export.
    /// </summary>
    [HttpPost("payment-batches/{batchId:guid}/settle/reverse")]
    public async Task<IActionResult> ReverseSettlement(Guid batchId, [FromBody] PayrollReasonRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required to reverse a settlement." });

        var tenantId = GetTenantId();
        var batch = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batch.PayrollRunId, cancellationToken);

        var originals = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == batch.PayrollRunId
                     && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed)
            .ToListAsync(cancellationToken);
        if (originals.Count == 0)
            return Conflict(new { error = "not_settled", message = "There is no active net-pay settlement to reverse for this batch." });

        // P0-3 — do not silently rewrite closed books; require an audited reopen first.
        foreach (var closedPeriod in originals.Select(o => o.Period).Distinct())
            if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run?.CompanyId, closedPeriod, cancellationToken))
                return UnprocessableEntity(new { error = "gl_period_closed", message = $"GL period {closedPeriod} is closed. Reopen it before reversing this settlement.", period = closedPeriod });

        var contras = BuildContraGl(tenantId, batch.PayrollRunId, originals, GlEventTypes.NetSettlementReversal, GetUserId(), GetUserName(), req.Reason);
        foreach (var o in originals) o.IsReversed = true;
        _db.FinanceGlEntries.AddRange(contras);
        if (batch.WpsStatus == WpsStatuses.Paid)
        {
            batch.WpsStatus = WpsStatuses.Accepted;
            batch.WpsStatusChangedAtUtc = DateTime.UtcNow;
        }
        await PayrollAudit("payroll.batch.settlement_reversed", "PayrollPaymentBatch", batch.Id.ToString(),
            new { runId = batch.PayrollRunId, reversed = contras.Count, reason = req.Reason }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { batchId = batch.Id, wpsStatus = batch.WpsStatus, reversedEntries = contras.Count });
    }

    /// <summary>
    /// STATUTORY REMITTANCE (Remit). When GOSI/Tax/Loan is filed + remitted, clears the corresponding
    /// control liabilities (GOSI → 2101 + 2106; TAX → 2102; LOAN → 2107) against Cash/Bank. Clears the
    /// EXACT accrual credit lines verbatim (remap-immune), classifying them by the run's own deduction
    /// Source. The employer expense (5101) is a P&amp;L line and is intentionally NOT cleared. Idempotent
    /// per group and reversible. group ∈ {GOSI, TAX, LOAN, All}. Requires payroll.export.
    /// </summary>
    [HttpPost("runs/{id:guid}/remit")]
    public async Task<IActionResult> RemitStatutory(Guid id, [FromBody] RemitStatutoryRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status != "Locked")
            return BadRequest(new { error = "run_not_locked", message = "Only a Locked run can be remitted." });

        var groupArg = (req.Group ?? RemitGroups.All).Trim();
        var groupsRequested = string.Equals(groupArg, RemitGroups.All, StringComparison.OrdinalIgnoreCase)
            ? RemitGroups.Concrete
            : RemitGroups.Concrete.Where(g => string.Equals(g, groupArg, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (groupsRequested.Length == 0)
            return BadRequest(new { error = "invalid_group", message = $"Group must be one of: {string.Join(", ", RemitGroups.Concrete)}, {RemitGroups.All}." });

        var accrualLines = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id
                     && x.EventType == GlEventTypes.Accrual && !x.IsReversed)
            .ToListAsync(cancellationToken);
        if (accrualLines.Count == 0)
            return BadRequest(new { error = "gl_not_accrued", message = "This run has no accrual GL. Lock the run before remitting." });

        // Classify each accrual deduction CR line into a remit group using the run's OWN deduction
        // records (which carry Source). Remap-immune: we then DR the line's stored account, never a
        // re-resolved one. Driver ROUTING (component→driver) is stable/add-only; only account MAPPING
        // drifts, and that is exactly what clearing-by-stored-account defends against.
        var deductions = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == id)
            .Select(d => new { d.ComponentCode, d.Source })
            .ToListAsync(cancellationToken);
        var groupByCode = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in deductions)
        {
            var g = RemitGroups.ForSource(d.Source);
            if (g is not null) groupByCode[d.ComponentCode] = g;   // codes are group-stable per source
        }

        // Date into the REMIT period; guard THAT period (P0-2).
        var remitDate = req.RemitDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var remitPeriod = $"{remitDate.Year}-{remitDate.Month:D2}";
        if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run.CompanyId, remitPeriod, cancellationToken))
            return UnprocessableEntity(new { error = "gl_period_closed", message = $"GL period {remitPeriod} is closed. Reopen it before remitting into it.", period = remitPeriod, companyId = run.CompanyId });

        var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken);
        var (cashCode, cashName) = ResolveGlAccount("CASH_BANK", glCtx.Overrides, glCtx.DriverDefaults);
        var cashAccount = $"{cashCode} - {cashName}";

        // POD-B1b — a LOAN "remittance" is not a cash payment. The EMI was WITHHELD from net pay, so the
        // 2107 clearing must credit the receivable the money is owed against (1400 loans / 1410 advances),
        // not Cash/Bank — cash already left at disbursement (LoansController.cs:181,
        // AdvancesController.cs:183). Without this the outflow is booked twice and 1400/1410 never
        // amortise. GOSI/TAX are genuine cash remittances and keep crediting Cash/Bank unchanged.
        //
        // POD-B1b-FIX (P0-2) — but ONLY up to what the receivable actually holds. Loans that predate the
        // disbursement GL (LoansController is older than 72d3983), seeded Active loans/advances with a
        // real OutstandingBalance and no GL at all (AuthSeeder.cs:1132/:1155), and an ApprovedAmount
        // raised by a second Approved decision past the post-once probe (LoansController.cs:237-247) all
        // carry an operational balance with NO 1400/1410 debit behind it. Crediting them anyway drove an
        // ASSET into a credit balance, and BuildLiabilityClearingGl being balanced by construction meant
        // the gl_unbalanced 422 below could never catch it. So: CAP the receivable credit at the account's
        // real carrying balance and route the EXCESS to Cash/Bank — which is exactly the pre-B1b treatment
        // for the uncovered portion. Metered across the whole remittance by the splitter (the P0-1 lesson:
        // never re-read an immutable balance per iteration).
        var loanReceivableAccount    = GlAccountResolver.AccountLabel("LOAN_RECEIVABLE", glCtx);
        var advanceReceivableAccount = GlAccountResolver.AccountLabel("ADVANCE_RECEIVABLE", glCtx);
        var loanContraByCode = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LOAN_EMI"]    = loanReceivableAccount,
            ["ADVANCE_EMI"] = advanceReceivableAccount,
        };
        //
        // POD-B1b-FIX (re-audit #1) — the cap is AvailableForRelief, not the scoped balance. Every
        // pre-B1b disbursement carries CompanyId = NULL, and that unattributed pool sits inside EVERY
        // company's scoped view while the relief posted against it is stamped with the relieving company
        // and therefore invisible to the next one — so companies A and B against one 10,000 pool would
        // each read 10,000 and each relieve 6,000, leaving the ASSET with a 2,000 CREDIT balance. Clamping
        // to the tenant-wide balance nets every other company's relief and makes Σ(relief) ≤ Σ(debits)
        // arithmetically certain whatever order the entities remit in.
        var loanBalance    = await GlControlAccounts.LoadAsync(_db, tenantId, run.CompanyId, loanReceivableAccount, cancellationToken);
        var advanceBalance = await GlControlAccounts.LoadAsync(_db, tenantId, run.CompanyId, advanceReceivableAccount, cancellationToken);
        var receivableSplitter = new ReceivableClearingSplitter(cashAccount, new[]
        {
            new KeyValuePair<string, decimal>(loanReceivableAccount,    loanBalance.AvailableForRelief),
            new KeyValuePair<string, decimal>(advanceReceivableAccount, advanceBalance.AvailableForRelief),
        });
        IReadOnlyList<(string Account, decimal Amount)> LoanContraFor(FinanceGlEntry accrual)
        {
            var code = accrual.Description.StartsWith(PayrollGlDescriptions.DeductionPrefix, StringComparison.Ordinal)
                ? accrual.Description[PayrollGlDescriptions.DeductionPrefix.Length..]
                : string.Empty;
            // An unrecognised Loan-source component (e.g. a third-party garnishment routed to DED:LOAN)
            // is a real cash payout — null tells the splitter to send the whole line to Cash/Bank rather
            // than guess a receivable. (Dispatch mechanism unchanged; only the amount routing is capped.)
            var receivable = loanContraByCode.TryGetValue(code, out var r) ? r : null;
            return receivableSplitter.Split(receivable, accrual.Amount);
        }

        var posted = new List<object>();
        var skipped = new List<object>();
        foreach (var g in groupsRequested)
        {
            var already = await _db.FinanceGlEntries.AnyAsync(
                x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id
                  && x.EventType == GlEventTypes.RemitPrefix + g && !x.IsReversed, cancellationToken);
            if (already) { skipped.Add(new { group = g, reason = "already_remitted" }); continue; }

            var groupLines = accrualLines.Where(l =>
                    string.IsNullOrEmpty(l.DebitAccount)
                    && l.Description.StartsWith(PayrollGlDescriptions.DeductionPrefix, StringComparison.Ordinal)
                    && groupByCode.TryGetValue(l.Description[PayrollGlDescriptions.DeductionPrefix.Length..], out var lg)
                    && lg == g)
                .ToList();
            if (groupLines.Count == 0 || groupLines.Sum(l => l.Amount) <= 0m)
            { skipped.Add(new { group = g, reason = "nothing_to_remit" }); continue; }

            var currency = groupLines[0].Currency;
            var (lines, dr, cr) = BuildLiabilityClearingGl(
                tenantId, id, remitPeriod, req.Reference ?? g, GlEventTypes.Remit(g),
                groupLines, cashAccount, GetUserId(), GetUserName(), currency,
                $"{g} remittance — ref {req.Reference ?? "(none)"}", run.CompanyId,
                g == RemitGroups.Loan ? LoanContraFor : null);
            if (Math.Abs(dr - cr) > 0.01m)
                return UnprocessableEntity(new { error = "gl_unbalanced", group = g, message = "Statutory remittance GL is not balanced.", totalDebits = dr, totalCredits = cr });

            _db.FinanceGlEntries.AddRange(lines);
            // POD-B1b-FIX (P0-2) — surface the cap-and-route-excess split explicitly: how much of the LOAN
            // group amortised a real receivable vs. how much was an uncovered cash payout. Finance can
            // reconcile 1400/1410 against the loan sub-ledger from this, and it makes the split testable
            // through the API rather than only by inspecting raw ledger rows.
            var creditBreakdown = lines
                .Where(l => !string.IsNullOrEmpty(l.CreditAccount))
                .GroupBy(l => l.CreditAccount, StringComparer.Ordinal)
                .Select(x => new { account = x.Key, amount = x.Sum(l => l.Amount) })
                .OrderBy(x => x.account, StringComparer.Ordinal)
                .ToList();
            posted.Add(new { group = g, amount = cr, credits = creditBreakdown });
            await PayrollAudit("payroll.statutory.remitted", "PayrollRun", id.ToString(),
                new { group = g, reference = req.Reference, amount = cr, cashAccount, period = remitPeriod, credits = creditBreakdown }, cancellationToken);
        }

        if (posted.Count == 0)
            return Conflict(new { error = "nothing_remitted", message = "No statutory liabilities were remitted (already remitted, or none in scope).", skipped });

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { runId = id, posted, skipped, cashAccount, period = remitPeriod });
    }

    /// <summary>
    /// Reverses a statutory remittance group via contra entries (re-opening the control liabilities).
    /// Blocks if the remittance period is closed (P0-3). group ∈ {GOSI, TAX, LOAN}. Requires payroll.export.
    /// </summary>
    [HttpPost("runs/{id:guid}/remit/reverse")]
    public async Task<IActionResult> ReverseRemittance(Guid id, [FromBody] RemitReverseRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required to reverse a remittance." });

        var groupArg = (req.Group ?? "").Trim();
        var group = RemitGroups.Concrete.FirstOrDefault(g => string.Equals(g, groupArg, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return BadRequest(new { error = "invalid_group", message = $"Group must be one of: {string.Join(", ", RemitGroups.Concrete)}." });

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var originals = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id
                     && x.EventType == GlEventTypes.RemitPrefix + group && !x.IsReversed)
            .ToListAsync(cancellationToken);
        if (originals.Count == 0)
            return Conflict(new { error = "not_remitted", message = $"There is no active {group} remittance to reverse for this run." });

        foreach (var closedPeriod in originals.Select(o => o.Period).Distinct())
            if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run.CompanyId, closedPeriod, cancellationToken))
                return UnprocessableEntity(new { error = "gl_period_closed", message = $"GL period {closedPeriod} is closed. Reopen it before reversing this remittance.", period = closedPeriod });

        var contras = BuildContraGl(tenantId, id, originals, GlEventTypes.RemitReversal(group), GetUserId(), GetUserName(), req.Reason);
        foreach (var o in originals) o.IsReversed = true;
        _db.FinanceGlEntries.AddRange(contras);
        await PayrollAudit("payroll.statutory.remittance_reversed", "PayrollRun", id.ToString(),
            new { group, reversed = contras.Count, reason = req.Reason }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { runId = id, group, reversedEntries = contras.Count });
    }

    // ── POD-B1 GL builders (settlement / remittance / reversal) ─────────────────────────────────────

    /// <summary>
    /// Builds a balanced "clear liabilities" journal: for each accrual CREDIT line it posts
    /// DR &lt;that line's STORED CreditAccount&gt; for the STORED amount (so the control account nets to zero
    /// regardless of any later CoA remap), then CREDITS the contra side for the total. Reused by net-pay
    /// settlement and each remittance group. Returns (lines, DR, CR) so the caller reuses the
    /// gl_unbalanced 422 guard. Balanced by construction (Σ DR == Σ CR == total).
    ///
    /// <para>POD-B1b — <paramref name="creditSplitFor"/> selects the credit account(s) PER accrual line
    /// instead of always using Cash/Bank. Cash is right for GOSI/TAX/net pay (money really leaves the
    /// bank), but WRONG for the LOAN group: an employer-granted loan's cash already left at disbursement,
    /// so crediting cash again double-counts the outflow and leaves 1400/1410 to amortise forever.
    /// A LOAN_EMI / ADVANCE_EMI withholding instead credits the RECEIVABLE, which is what finally drives
    /// it to zero. Credits are consolidated per distinct account so the journal keeps one CR per contra
    /// account; with the default selector this is a single CR line — byte-identical to POD-B1.</para>
    ///
    /// <para>POD-B1b-FIX (P0-2) — the selector returns a SPLIT, not one account, because crediting a
    /// receivable is only valid up to what that receivable actually holds; the uncovered excess must fall
    /// back to Cash/Bank. Σ(split) is asserted to equal the line amount, so an ill-behaved selector shows
    /// up in the caller's dr/cr 422 instead of silently unbalancing the journal.</para>
    /// </summary>
    private static (List<FinanceGlEntry> Lines, decimal TotalDebits, decimal TotalCredits) BuildLiabilityClearingGl(
        Guid tenantId, Guid runId, string entryPeriod, string sourceRef, string eventType,
        IReadOnlyList<FinanceGlEntry> accrualCreditLines, string cashAccount,
        Guid? postedBy, string postedByName, string currency, string description,
        Guid? companyId = null,
        Func<FinanceGlEntry, IReadOnlyList<(string Account, decimal Amount)>>? creditSplitFor = null)
    {
        var lines = new List<FinanceGlEntry>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var creditTotals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var creditOrder = new List<string>();
        foreach (var accrual in accrualCreditLines)
        {
            if (accrual.Amount <= 0m) continue;
            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = companyId ?? accrual.CompanyId,
                SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = sourceRef, EventType = eventType,
                DebitAccount = accrual.CreditAccount, CreditAccount = string.Empty,
                Amount = accrual.Amount, Currency = currency,
                EntryDate = today, Period = entryPeriod,
                Description = description,
                PostedBy = postedBy, PostedByName = postedByName,
            });
            var split = creditSplitFor?.Invoke(accrual)
                        ?? new[] { (Account: cashAccount, Amount: accrual.Amount) };
            foreach (var (contra, amount) in split)
            {
                if (amount <= 0m) continue;
                if (!creditTotals.ContainsKey(contra)) creditOrder.Add(contra);
                creditTotals[contra] = creditTotals.GetValueOrDefault(contra) + amount;
            }
        }
        foreach (var contra in creditOrder)
        {
            var total = creditTotals[contra];
            if (total <= 0m) continue;
            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = companyId,
                SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = sourceRef, EventType = eventType,
                DebitAccount = string.Empty, CreditAccount = contra,
                Amount = total, Currency = currency,
                EntryDate = today, Period = entryPeriod,
                Description = description,
                PostedBy = postedBy, PostedByName = postedByName,
            });
        }
        var dr = lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = lines.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        return (lines, dr, cr);
    }

    /// <summary>Builds contra entries reversing <paramref name="originals"/> (swap DR/CR, same amount,
    /// dated to the original period, ReversalOfEntryId set) — the PayrollVoidService pattern, filtered to
    /// one EventType. The caller flags the originals IsReversed=true.</summary>
    private static List<FinanceGlEntry> BuildContraGl(
        Guid tenantId, Guid runId, IReadOnlyList<FinanceGlEntry> originals, string eventType,
        Guid? postedBy, string postedByName, string reason)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return originals.Select(orig => new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = orig.CompanyId,
            SourceModule = "Payroll", SourceEntityId = runId,
            SourceEntityRef = orig.SourceEntityRef, EventType = eventType,
            DebitAccount = orig.CreditAccount, CreditAccount = orig.DebitAccount,
            Amount = orig.Amount, Currency = orig.Currency,
            EntryDate = today, Period = orig.Period,
            Description = $"REVERSAL — {orig.Description} — {reason}",
            PostedBy = postedBy, PostedByName = postedByName,
            IsReversed = false, ReversalOfEntryId = orig.Id,
        }).ToList();
    }

    /// <summary>
    /// Downloads the SIF file using the isolated SifFileGenerator.
    /// Output is deterministic — same input always produces the same bytes and hash.
    /// Marks batch as Downloaded on first download.
    /// Requires payroll.export permission.
    /// </summary>
    [HttpGet("payment-batches/{batchId:guid}/wps-file/download")]
    public async Task<IActionResult> DownloadWpsFile(Guid batchId, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var batch    = await _db.PayrollPaymentBatches.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        var wpsFile = await _db.WPSFileBatches.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.PaymentBatchId == batchId, cancellationToken);
        if (wpsFile is null) return BadRequest(new { message = "WPS file has not been generated for this batch yet." });

        var sifRecords  = await _db.SIFFileRecords.AsNoTracking().Where(x => x.TenantId == tenantId && x.WPSFileBatchId == wpsFile.Id).ToListAsync(cancellationToken);
        var gcc         = await _db.GCCComplianceSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        var run         = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == batch.PayrollRunId && x.TenantId == tenantId, cancellationToken);

        // Resolve exporter via pack for deterministic regeneration.
        var dlCompany = run is not null
            ? await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId, cancellationToken)
            : null;
        var dlExporter = _packResolver.ResolveWageProtectionExporter(
            dlCompany?.CountryCode  ?? string.Empty,
            dlCompany?.Jurisdiction ?? string.Empty);

        // Rebuild WpsEmployee list from DB snapshots — IBAN/NetPay from SIFFileRecord (frozen at generation),
        // names/nationality from Employee, salary breakdown from PayrollSlip.
        var empIds    = sifRecords.Select(r => r.EmployeeId).Distinct().ToList();
        var dlEmps    = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && empIds.Contains(e.Id)).ToListAsync(cancellationToken);
        var dlSlips   = run is not null
            ? await _db.PayrollSlips.AsNoTracking().Where(s => s.TenantId == tenantId && s.RunId == run.Id && empIds.Contains(s.EmployeeId)).ToListAsync(cancellationToken)
            : new();
        var dlEmpById   = dlEmps.ToDictionary(e => e.Id);
        var dlSlipById  = dlSlips.ToDictionary(s => s.EmployeeId);

        var dlWpsEmployees = sifRecords.Select(r =>
        {
            var emp  = dlEmpById.TryGetValue(r.EmployeeId, out var em) ? em : null;
            var slip = dlSlipById.TryGetValue(r.EmployeeId, out var sl) ? sl : null;
            return new WpsEmployee(
                EmployeeId:    r.EmployeeId,
                EmployeeCode:  r.EmployeeCode,
                FullNameEn:    emp?.FullName   ?? r.EmployeeCode,
                FullNameAr:    string.Empty,
                Nationality:   emp?.Nationality ?? string.Empty,
                NationalId:    r.MolId,
                IbanOrAccount: r.Iban,
                BankCode:      r.RoutingCode,
                Salary: new SalaryBreakdown(
                    slip?.BasicSalary        ?? 0m,
                    slip?.HousingAllowance   ?? 0m,
                    slip?.TransportAllowance ?? 0m,
                    slip?.OtherAllowances    ?? 0m),
                NetPay: r.NetPay);
        }).ToList();

        var dlInput = new WageProtectionExportInput(
            TenantId:        tenantId,
            CompanyId:       (run?.CompanyId) ?? Guid.Empty,
            PayrollRunId:    run?.Id          ?? Guid.Empty,
            PeriodYear:      run?.Year      ?? DateTime.UtcNow.Year,
            PeriodMonth:     run?.Month     ?? DateTime.UtcNow.Month,
            EstablishmentId: gcc?.WpsAgentId ?? "0000000000",
            EmployerIban:    string.Empty,
            CompanyNameEn:   dlCompany?.LegalNameEn ?? string.Empty,
            CompanyNameAr:   dlCompany?.LegalNameAr ?? string.Empty,
            Employees:       dlWpsEmployees);

        var dlResult = await dlExporter.ExportAsync(dlInput, cancellationToken);
        var fileHash = Convert.ToHexString(SHA256.HashData(dlResult.FileBytes)).ToLowerInvariant();

        // Advance lifecycle from Generated → Downloaded on first download.
        var tracked = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (tracked is not null && WpsTransitions.IsAllowed(tracked.WpsStatus, WpsStatuses.Downloaded))
        {
            tracked.WpsStatus = WpsStatuses.Downloaded;
            await PayrollAudit("payroll.wps.downloaded", "WPSFileBatch", wpsFile.Id.ToString(),
                new { batchId, fileHash }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var mimeType = dlResult.Format == "mudad-xml" ? "application/xml" : "text/plain";
        Response.Headers["Content-Disposition"] = $"attachment; filename={dlResult.FileName}";
        return File(dlResult.FileBytes, mimeType, dlResult.FileName);
    }

    /// <summary>
    /// Returns the WPS export history for a payment batch (all WPSFileBatch records).
    /// Requires payroll.export permission.
    /// </summary>
    [HttpGet("payment-batches/{batchId:guid}/wps-export-history")]
    public async Task<IActionResult> WpsExportHistory(Guid batchId, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var wpsBatch = await _db.PayrollPaymentBatches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (wpsBatch is null) return NotFound();

        var history = await _db.WPSFileBatches.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batchId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        // POD-B2 (M9) — stamp the run's type/parent into the export history so an operator reconciling a
        // month against the bank can tell which SIF belongs to which run.
        var historyRun = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == wpsBatch.PayrollRunId)
            .Select(r => new { r.Id, r.RunType, r.ParentRunId, r.Year, r.Month })
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new
        {
            batchId, exportCount = history.Count, history,
            runId       = historyRun?.Id,
            runType     = historyRun?.RunType,
            parentRunId = historyRun?.ParentRunId,
            period      = historyRun is null ? null : $"{historyRun.Year}-{historyRun.Month:D2}",
        });
    }

    [HttpGet("employee-salary-structures")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListEmployeeSalaryStructures([FromQuery] int? employeeId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // M4: scope to allowed employees
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var query = _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (employeeId.HasValue) query = query.Where(x => x.EmployeeId == employeeId.Value);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        // Only surface salary rows whose employee still exists, is not soft-deleted, and is within the
        // caller's company scope. EmployeeSalaryStructure has no IsDeleted/CompanyId of its own, and
        // delete/offboard/terminate never cascade to it, so without this guard the assignments of removed
        // employees linger as phantom "Active" rows (the "Emp #47xx" entries). The Employees subquery
        // carries the global tenant + !IsDeleted + company-scope query filter, so it excludes orphaned,
        // deleted, and out-of-scope employees in one shot.
        query = query.Where(x => _db.Employees.Any(e => e.Id == x.EmployeeId));
        var structs = await query.OrderByDescending(x => x.EffectiveDate).ToListAsync(cancellationToken);
        return Ok(structs.Select(s => SalaryStructureAssignmentDto.Project(s, true)).ToList());
    }

    [HttpGet("payment-batches")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListPaymentBatches([FromQuery] Guid? runId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var query = _db.PayrollPaymentBatches.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (runId.HasValue) query = query.Where(x => x.PayrollRunId == runId.Value);
        // SAFE-SERIALIZATION: PayrollPaymentBatch is a payment workflow aggregate (TotalAmount is batch-level) — no per-employee salary PII.
        return Ok(await query.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken));
    }

    /// <summary>
    /// Lists payment records for a batch.
    /// Full IBAN is only returned to users with payroll.export permission;
    /// others see a masked version (first 4 + last 4, middle asterisked).
    /// </summary>
    [HttpGet("payment-batches/{id:guid}/records")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> PaymentRecords(Guid id, CancellationToken cancellationToken)
    {
        var tenantId  = GetTenantId();
        var records   = await _db.PayrollPaymentRecords.AsNoTracking().Where(x => x.TenantId == tenantId && x.PaymentBatchId == id).ToListAsync(cancellationToken);
        var canSeeIban = HasPermission("payroll.export");
        return Ok(records.Select(r => new
        {
            r.Id,
            r.EmployeeId,
            r.Amount,
            r.Status,
            r.WpsReference,
            Iban = canSeeIban ? r.Iban : Infrastructure.Payroll.SifFileGenerator.MaskIban(r.Iban),
        }));
    }

    [HttpGet("runs/{id:guid}/payslips")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListPayslips(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var query = _db.Payslips.Where(x => x.TenantId == tenantId && x.PayrollRunId == id);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.EmployeeId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        // SAFE-SERIALIZATION: Payslip is a header-only record (Id, EmployeeId, PayslipNumber, IsPublishedToEss) — no salary amounts.
        return Ok(new PagedResult<Payslip>(items, total, page, pageSize));
    }

    [HttpGet("runs/{id:guid}/approvals")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListRunApprovals(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // SAFE-SERIALIZATION: PayrollApproval is a workflow record (who approved, when, status) — no salary amounts.
        return Ok(await _db.PayrollApprovals.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).OrderByDescending(x => x.DecidedAtUtc).ToListAsync(cancellationToken));
    }

    [HttpGet("groups")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListGroups(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // SAFE-SERIALIZATION: PayrollGroup is config (Code, Name, Currency) — no personal PII.
        return Ok(await _db.PayrollGroups.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive).ToListAsync(cancellationToken));
    }

    [HttpPost("groups")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CreateGroup(PayrollGroupRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var groupCurrency = !string.IsNullOrWhiteSpace(req.Currency) ? req.Currency : await ResolveCurrencyAsync(tenantId, cancellationToken);
        var group = new PayrollGroup { TenantId = tenantId, Code = req.Code.Trim(), Name = req.Name.Trim(), Currency = groupCurrency };
        _db.PayrollGroups.Add(group);
        await _db.SaveChangesAsync(cancellationToken);
        // SAFE-SERIALIZATION: PayrollGroup is config (Code, Name, Currency) — no personal PII.
        return Created($"/api/payroll/groups/{group.Id}", group);
    }

    // H3: salary register is scoped — managers cannot see all employee salaries
    [HttpGet("reports/register")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> Register([FromQuery] Guid runId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var query = _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == runId);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var slipList = await query.OrderBy(x => x.EmployeeCode).ToListAsync(cancellationToken);
        return Ok(slipList.Select(s => PayrollSlipDto.Project(s, true)).ToList()); // no deduction-lines in register export (performance)
    }

    [HttpGet("reports/register/export")]
    public async Task<IActionResult> ExportRegister([FromQuery] Guid runId, CancellationToken cancellationToken)
    {
        // Salary registers carry compensation data — exporting requires the explicit
        // payroll export permission, same as every other payroll export endpoint.
        if (!HasPermission("payroll.export")) return Forbid();
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId && x.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        var query = _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == runId);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var slips = await query.OrderBy(x => x.EmployeeCode).ToListAsync(cancellationToken);

        var headers = new[] { "Employee Code", "Employee Name", "Department", "Basic Salary", "Housing Allowance", "Transport Allowance", "Other Allowances", "Gross Salary", "Deductions", "Net Salary", "Status" };
        var rows = slips.Select(s => (IReadOnlyList<object?>)new object?[]
        {
            s.EmployeeCode, s.EmployeeName, s.Department,
            s.BasicSalary.ToString("F2"), s.HousingAllowance.ToString("F2"), s.TransportAllowance.ToString("F2"), s.OtherAllowances.ToString("F2"),
            s.GrossSalary.ToString("F2"), s.Deductions.ToString("F2"), s.NetSalary.ToString("F2"), s.Status
        });
        var csv = Csv.Build(headers, rows);
        // Export audit: who exported which run's salary register, how many rows, and the
        // company dimension — never the salary/IBAN values themselves.
        await PayrollAudit("payroll.register.exported", "PayrollRun", runId.ToString(),
            new { run.Year, run.Month, run.CompanyId, rowCount = slips.Count, scoped = !scope.IsUnrestricted }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        Response.Headers["Content-Disposition"] = $"attachment; filename=salary-register-{run.Year}-{run.Month:D2}.csv";
        return Content(csv, "text/csv");
    }

    [HttpGet("reports/summary")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ReportSummary(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var runs = await _db.PayrollRuns.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        return Ok(new
        {
            totalRuns = runs.Count,
            lockedRuns = runs.Count(x => x.Status == "Locked"),
            totalEmployeesPaid = runs.Where(x => x.Status == "Locked").Sum(x => x.EmployeeCount),
            totalGrossYtd = runs.Where(x => x.Status == "Locked" && x.Year == DateTime.UtcNow.Year).Sum(x => x.TotalGrossSalary),
            totalNetYtd = runs.Where(x => x.Status == "Locked" && x.Year == DateTime.UtcNow.Year).Sum(x => x.TotalNetSalary),
        });
    }

    // ── EOSB / Gratuity ──────────────────────────────────────────────────────────

    /// <summary>Calculate EOSB/Gratuity for a single employee using tenant GCC settings.</summary>
    [HttpPost("eosb/calculate")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CalculateEosb([FromBody] EosbCalculationRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound(new { message = "Employee not found." });

        var gcc = await _db.GCCComplianceSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (gcc is null || !gcc.EosbEnabled)
            return BadRequest(new { message = "EOSB is not enabled for this tenant. Enable it in GCC Settings first." });

        var calcDate = req.AsOfDate ?? DateTime.UtcNow;
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive && x.EffectiveDate <= DateOnly.FromDateTime(calcDate))
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);

        var eligibleSalary = salary?.BasicSalary ?? employee.Salary ?? 0m;
        var joiningDate = employee.JoiningDate;
        var dailySalary = eligibleSalary * 12 / 365m;
        var monthlySalary = eligibleSalary;

        // Resolve the separation reason once, consistently with /final-settlement:
        // explicit request override → recorded EmployeeOffboarding.SeparationType → conservative default.
        var terminationReason = await ResolveTerminationReasonAsync(tenantId, req.EmployeeId, req.TerminationReason, cancellationToken);

        // Single authoritative EOSB engine (shared verbatim with /final-settlement via
        // ComputeEndOfServiceAsync). No controller-side minYears pre-gate: KSA Art.84 grants
        // pro-rata gratuity to short-service TERMINATIONS from day one; the <2yr RESIGNATION
        // forfeiture, the Art.80 dismissal forfeiture, and the non-KSA ≥1yr eligibility floors
        // are all owned by the country packs (the single source of truth).
        var (eosbResult, totalYears) = await ComputeEndOfServiceAsync(
            gcc, eligibleSalary, joiningDate, calcDate, terminationReason, employee, cancellationToken);
        var eosbAmount  = Math.Round(eosbResult.TotalGratuity, 2);
        var eosbFormula = eosbResult.ApplicableRule;

        // Persist the calculation
        var existing = await _db.EOSBCalculations.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.Status == "Draft", cancellationToken);
        if (existing is not null)
        {
            existing.CalculationDate = DateOnly.FromDateTime(calcDate);
            existing.EligibleSalary = eligibleSalary;
            existing.CalculatedAmount = eosbAmount;
            existing.RulesSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new { formula = eosbFormula, totalYears, dailySalary, monthlySalary, countryCode = gcc.CountryCode });
        }
        else
        {
            _db.EOSBCalculations.Add(new EOSBCalculation
            {
                TenantId = tenantId, EmployeeId = req.EmployeeId, CalculationDate = DateOnly.FromDateTime(calcDate),
                EligibleSalary = eligibleSalary, CalculatedAmount = eosbAmount,
                RulesSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new { formula = eosbFormula, totalYears, dailySalary, monthlySalary, countryCode = gcc.CountryCode })
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            employeeId = req.EmployeeId,
            employeeName = employee.FullName,
            joiningDate,
            asOfDate = calcDate,
            totalYears = Math.Round(totalYears, 2),
            eligibleSalary,
            dailySalary = Math.Round(dailySalary, 4),
            formula = eosbFormula,
            eosbAmount,
            terminationReason,
            currency = salary?.Currency ?? "SAR",
            message = $"Calculated EOSB/Gratuity for {employee.FullName}: {salary?.Currency ?? "SAR"} {eosbAmount:N2}"
        });
    }

    // ── Shared EOSB engine seam ───────────────────────────────────────────────────
    // The ONE authoritative EOSB computation used by BOTH /eosb/calculate and
    // /final-settlement, so identical inputs provably yield identical figures. It resolves
    // the tenant's country-pack IEndOfServiceCalculator (KSA/UAE/Qatar/Default) and returns
    // the structured EndOfServiceResult — the clean seam the Wave-C settlement pipeline
    // (POD-C1 payable/GL/off-cycle WPS) can consume directly (this pod computes ONLY).
    //
    // Wage base: the pack computes gratuity on the LAST BASIC wage (input.Salary.Basic).
    // This is a per-company-policy FLOOR — it is NOT the full statutory Art.84 "last wage"
    // (basic + regular allowances such as housing), which materially exceeds basic-only.
    // Wiring eligible allowances (PayComponent.EosbIncluded / IsIncludedInEosb, and the
    // above-floor company rates) is deliberately DEFERRED here to avoid changing the live
    // /eosb figure; it needs product/legal sign-off. The SalaryBreakdown seam below can
    // carry those allowances when that decision is made, without changing this contract.
    //
    // Service period: the pack derives service length from DateOnly start/end
    // (fullMonths/12 + remDays/365), eliminating the /365.0 leap drift the old inline
    // final-settlement formula suffered.
    private async Task<(EndOfServiceResult Result, double ServiceYearsDisplay)> ComputeEndOfServiceAsync(
        GCCComplianceSetting gcc, decimal basicWage, DateTime joiningDate,
        DateTime asOfDate, string terminationReason, Employee employee, CancellationToken ct)
    {
        // Map GCC CountryCode (ISO-2 stored) → pack key; the resolver maps ISO-2→ISO-3.
        var packCc = gcc.CountryCode switch
        {
            "SA" => CountryCodes.Saudi,
            "AE" or "UAE" => CountryCodes.UAE,
            "QA" or "QAT" => CountryCodes.Qatar,
            _ => gcc.CountryCode ?? CountryCodes.Saudi,
        };
        const string jur = "mainland"; // GCCComplianceSetting has no jurisdiction field; default mainland
        var calc = _packResolver.ResolveEndOfServiceCalculator(packCc, jur);

        var input = new EndOfServiceInput(
            EmployeeId:        Guid.Empty,
            CompanyId:         Guid.Empty,
            Salary:            new SalaryBreakdown(basicWage, 0m, 0m, 0m),
            ServiceStartDate:  DateOnly.FromDateTime(joiningDate),
            ServiceEndDate:    DateOnly.FromDateTime(asOfDate),
            TerminationReason: terminationReason,
            ContractType:      employee.ContractType ?? "Indefinite",
            Nationality:       employee.Nationality ?? string.Empty);

        var result = await calc.CalculateAsync(input, ct);
        var serviceYearsDisplay = (asOfDate - joiningDate).Days / 365.0;
        return (result, serviceYearsDisplay);
    }

    // Resolves the EOSB separation reason with precedence:
    //   1. explicit request override (EosbCalculationRequest/FinalSettlementRequest.TerminationReason)
    //   2. the recorded EmployeeOffboarding.SeparationType for this employee (the authoritative
    //      domain fact — a resignation on record is discounted per Art.85 WITHOUT the caller
    //      having to re-state it, and Art.80 dismissals are forfeited rather than paid full)
    //   3. a conservative default ("Termination") when nothing is on record.
    // The resolved string is normalized to the vocabulary the packs understand.
    private async Task<string> ResolveTerminationReasonAsync(
        Guid tenantId, int employeeId, string? explicitReason, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitReason))
            return NormalizeTerminationReason(explicitReason);

        var separationType = await _db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.EmployeeId == employeeId && o.Status != "Cancelled")
            .OrderByDescending(o => o.CreatedAtUtc)
            .Select(o => o.SeparationType)
            .FirstOrDefaultAsync(ct);

        return NormalizeTerminationReason(separationType);
    }

    // Maps free-form / domain separation strings to the canonical reason vocabulary the
    // country packs branch on: "Resignation" (Art.85 reduction), "Article80" (Art.80
    // dismissal-for-cause forfeiture), or a pass-through employer-side reason (full award).
    private static string NormalizeTerminationReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Termination"; // conservative default: employer-side, full award
        var r = raw.Trim();
        if (r.Equals("Resignation", StringComparison.OrdinalIgnoreCase)
            || r.Equals("Resign", StringComparison.OrdinalIgnoreCase)
            || r.Equals("Resigned", StringComparison.OrdinalIgnoreCase))
            return "Resignation";
        if (r.Equals("Article80", StringComparison.OrdinalIgnoreCase)
            || r.Equals("Art80", StringComparison.OrdinalIgnoreCase)
            || r.Equals("DismissalForCause", StringComparison.OrdinalIgnoreCase)
            || r.Equals("SummaryDismissal", StringComparison.OrdinalIgnoreCase))
            return "Article80";
        // Termination / EndOfContract / Non-renewal / Retirement / Other / … → full award.
        return r;
    }

    [HttpGet("eosb/list")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListEosb([FromQuery] int? employeeId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var query = _db.EOSBCalculations.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (employeeId.HasValue) query = query.Where(x => x.EmployeeId == employeeId.Value);
        var eosbList = await query.OrderByDescending(x => x.CalculationDate).ToListAsync(cancellationToken);
        return Ok(eosbList.Select(EosbCalculationDto.Project).ToList());
    }

    [HttpGet("ai-validation")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> AiValidation([FromQuery] Guid runId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var warnings = await _db.PayrollValidationResults.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == runId).ToListAsync(cancellationToken);
        return Ok(new { advisoryOnly = true, warnings, summary = "AI payroll validation is advisory. It does not approve payroll or change salaries automatically." });
    }

    private void AddEarning(Guid tenantId, Guid runId, int employeeId, string code, string name, decimal amount, string source) =>
        _db.PayrollEarnings.Add(new PayrollEarning { TenantId = tenantId, PayrollRunId = runId, EmployeeId = employeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source });

    private void AddDeduction(Guid tenantId, Guid companyId, Guid runId, int employeeId, string code, string name, decimal amount, string source, bool isEmployerContribution = false) =>
        _db.PayrollDeductions.Add(new PayrollDeduction { TenantId = tenantId, CompanyId = companyId, PayrollRunId = runId, EmployeeId = employeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source, IsEmployerContribution = isEmployerContribution });

    // ── Shared GL routing (single source of truth for BOTH the on-screen GL Journal
    //    preview and the locked-run posting, so a preview equals what will post) ──────────
    //
    // These helpers were extracted from BuildPayrollGlEntries so GlJournal can resolve accounts
    // through the exact same driver-key routing + tenant override dictionary instead of a
    // divergent hard-coded switch. Do NOT duplicate this logic — call these from both paths.

    /// <summary>Earning component group → GL driver key. Bonus source wins over the component code.</summary>
    private static string EarningDriverKey(string componentCode, string source) =>
        source == "Bonus" ? "EARN:BONUS" : componentCode switch
        {
            "BASIC"            => "EARN:BASIC",
            "HOUSING"          => "EARN:HOUSING",
            "TRANSPORT"        => "EARN:TRANSPORT",
            "OTHER_ALLOWANCES" => "EARN:OTHER_ALLOWANCES",
            "OVERTIME"         => "EARN:OVERTIME",
            _                  => "EARN:OTHER",
        };

    /// <summary>
    /// Deduction component group → GL driver key. Also reports the employer side
    /// (Statutory + "-ER" suffix) so the caller can accumulate the paired employer-expense DR.
    /// </summary>
    private static string DeductionDriverKey(string componentCode, string source, out bool isEmployerSide)
    {
        isEmployerSide = source == "Statutory" && componentCode.EndsWith("-ER");
        return (source, isEmployerSide) switch
        {
            ("Statutory", true)  => "DED:STATUTORY_ER",
            ("Statutory", false) => "DED:STATUTORY_EE",
            ("Tax", _)           => "DED:TAX",
            ("Loan", _)          => "DED:LOAN",
            ("Attendance", _)    => "DED:ATTENDANCE",
            ("Leave", _)         => "DED:LEAVE",
            _ => componentCode == "FIXED_DEDUCTION" ? "DED:FIXED_DEDUCTION" : "DED:OTHER",
        };
    }

    // POD-B1b — the driver routing + company-first account resolution that used to live here as private
    // statics now lives in Infrastructure/Payroll/GlAccountResolver.cs, so the bonus, loan and advance
    // posting paths resolve accounts through the EXACT same code instead of hard-coded label strings.
    // These remain as thin delegating wrappers: every existing call site (and the reflection-driven
    // golden master) is untouched, and there is still only ONE implementation.

    /// <summary>Driver key → (code, name): company/tenant mapping override wins, else the persisted
    /// driver default (company row → tenant row), else the compiled catalog default, else Unmapped.</summary>
    private static (string Code, string Name) ResolveGlAccount(
        string driverKey,
        IReadOnlyDictionary<string, (string Code, string Name)>? overrides,
        IReadOnlyDictionary<string, (string Code, string Name)>? driverDefaults = null)
        => GlAccountResolver.Resolve(driverKey, overrides, driverDefaults);

    /// <summary>Most-specific driver for a component within a category, or null (→ compiled fallback).</summary>
    private static GlDriverRow? ResolveDriverForComponent(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source, string category)
        => GlAccountResolver.ResolveDriverForComponent(drivers, componentCode, source, category);

    /// <summary>Splits a persisted "<code> - <name>" account label on the FIRST " - " only
    /// (account names may legitimately contain " - ").</summary>
    private static (string Code, string Name) SplitAccountLabel(string label)
    {
        var idx = label.IndexOf(" - ", StringComparison.Ordinal);
        return idx < 0 ? (label, string.Empty) : (label[..idx], label[(idx + 3)..]);
    }

    /// <summary>Best-effort (componentCode, componentName) for an as-posted ledger line, derived
    /// from its stored Description ("Payroll earning: X" / "Payroll deduction: X" / balancing lines).</summary>
    private static (string Code, string Name) DescribePostedLine(string description)
    {
        const string earnPrefix = "Payroll earning: ";
        const string dedPrefix   = "Payroll deduction: ";
        if (description.StartsWith(earnPrefix, StringComparison.Ordinal)) return (description[earnPrefix.Length..], description);
        if (description.StartsWith(dedPrefix, StringComparison.Ordinal))   return (description[dedPrefix.Length..], description);
        return (string.Empty, description);
    }

    // Builds the double-entry GL lines for a payroll run.
    // Uses Source-based routing so new pack codes (GOSI-ANN-EE, GPSSA-EE, etc.) map correctly
    // without requiring changes to the component code dictionary as new packs are added.
    // Returns: (lines, totalDebits, totalCredits).
    private static (List<FinanceGlEntry> Lines, decimal TotalDebits, decimal TotalCredits) BuildPayrollGlEntries(
        Guid tenantId, Guid runId, string period,
        List<PayrollEarning> earnings, List<PayrollDeduction> deductions,
        decimal totalNetSalary, Guid? postedBy, string postedByName,
        string currency = "SAR",  // default SAR since this is primarily a Saudi HRM
        GlResolutionContext? gl = null)
    {
        gl ??= GlResolutionContext.Empty;
        var lines = new List<FinanceGlEntry>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Resolve a posting driver to "<code> - <name>": mapping override → persisted driver default →
        // compiled default. Delegates to the shared ResolveGlAccount so posting and the GL Journal
        // preview stay identical.
        string Account(string driverKey)
        {
            var (code, name) = ResolveGlAccount(driverKey, gl.Overrides, gl.DriverDefaults);
            return $"{code} - {name}";
        }

        // ── Earnings (Debit side) ──────────────────────────────────────────────
        // POD-B1b — BONUS DOUBLE-COUNT FIX. A bonus that was ACCRUED by the bonus module
        // (ApproveBatch: DR bonus expense / CR Bonus Payable) must NOT be expensed a second time here;
        // this run PAYS it, so the correct debit is the accrual's own payable account. gl.BonusClearings
        // carries, per batch, how much of that accrual is still outstanding (capped by
        // BonusGlLedger.BuildPayrollClearingAsync) and the STORED account it was credited to, which makes
        // the clearing immune to a chart-of-accounts remap between approve and lock.
        //
        // Anything NOT covered by a live accrual (bonus injected straight into a run with no accrual at
        // all, or the tax slice of a pre-B1b accrual booked at net) still debits EARN:BONUS exactly as
        // before — so tenants with no bonus accruals see a byte-identical journal.
        var bonusClearings = gl.BonusClearings;
        var clearsBonusAccrual = bonusClearings.Count > 0;
        decimal bonusEarningTotal = 0m;
        // POD-B1b-FIX (P1-7) — keep each deferred bonus component's OWN resolved driver. The remainder used
        // to be re-posted as one hard-coded Account("EARN:BONUS") line, which silently discarded a tenant's
        // custom Phase-2 Earning driver (a shipped feature — ResolveDriverForComponent matches e.g.
        // BONUS_EID by Exact/Prefix/Suffix) and collapsed per-component detail into a single line.
        var deferredBonusGroups = new List<(string Code, string Driver, decimal Amount)>();
        foreach (var grp in earnings.GroupBy(e => e.ComponentCode))
        {
            var src = grp.First().Source;
            var driverKey = ResolveDriverForComponent(gl.Drivers, grp.Key, src, GlDriverCategories.Earning)?.Key
                            ?? EarningDriverKey(grp.Key, src);
            if (clearsBonusAccrual && src == "Bonus")
            {
                var bonusAmount = grp.Sum(e => e.Amount);
                bonusEarningTotal += bonusAmount;
                deferredBonusGroups.Add((grp.Key, driverKey, bonusAmount));
                continue;   // re-emitted below as accrual clearing + per-component un-accrued remainder
            }
            var driver = driverKey;
            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                DebitAccount  = Account(driver), CreditAccount = string.Empty,
                Amount = grp.Sum(e => e.Amount), Currency = currency,
                EntryDate = today, Period = period,
                Description = $"Payroll earning: {grp.Key}",
                PostedBy = postedBy, PostedByName = postedByName,
            });
        }

        if (clearsBonusAccrual)
        {
            // DR the payable once per batch. EventType is BonusPayrollClearing (not Accrual) and the
            // batch id rides in SourceEntityRef, so BonusGlLedger can tell exactly how much of each
            // accrual this run consumed; a void contras these lines like any other payroll line
            // (PayrollVoidService.cs:63-84) and re-opens the payable automatically.
            decimal cleared = 0m;
            // POD-B1b-FIX (re-audit #5) — how much of each EARNING COMPONENT the clearings covered.
            var clearedByCode = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var c in bonusClearings)
            {
                if (c.Amount <= 0m) continue;
                cleared += c.Amount;
                foreach (var kv in c.ByComponentCode)
                    clearedByCode[kv.Key] = clearedByCode.GetValueOrDefault(kv.Key) + kv.Value;
                lines.Add(new FinanceGlEntry
                {
                    // The clearing carries the ACCRUAL's legal entity (falling back to the run's) so a
                    // per-company trial balance nets to zero and the payable sub-ledger can match this
                    // line to the exact position it retires — see BonusAccrualClearing.CompanyId.
                    TenantId = tenantId, CompanyId = c.CompanyId ?? gl.CompanyId,
                    SourceModule = "Payroll", SourceEntityId = runId,
                    SourceEntityRef = BonusGlLedger.BatchRef(c.BatchId),
                    EventType = GlEventTypes.BonusPayrollClearing,
                    DebitAccount = c.AccrualAccount, CreditAccount = string.Empty,
                    Amount = c.Amount, Currency = currency,
                    EntryDate = today, Period = period,
                    Description = $"{BonusGlDescriptions.PayrollClearingPrefix}{c.BatchRef}",
                    PostedBy = postedBy, PostedByName = postedByName,
                });
            }
            // POD-B1b-FIX (P1-7 + re-audit #5). Each component debits its OWN driver for what is left,
            // keeping a custom Earning driver (BONUS_EID etc.) authoritative and preserving per-component
            // detail. The remainder is allocated to the components that are genuinely STILL UN-ACCRUED —
            // BonusAccrualClearing.ByComponentCode carries the batch→component link out of the consumed
            // EmployeeBonus rows (the earning code is derived from the bonus type,
            // BonusGlLedger.EarningComponentCode, so the link was never actually missing). Spreading the
            // remainder pro rata instead put half of a never-accrued batch's expense on a FULLY-accrued
            // component's account: total expense right, both accounts wrong, and the accrued component
            // expensed twice at account level.
            //
            // Pro rata over gross remains the fallback for a legacy/incoherent plan that carries no
            // component map, so the journal balances by construction either way
            // (Σ(clearing) + Σ(remainder slices) == Σ(bonus earnings), always).
            var remainder = Math.Round(bonusEarningTotal - cleared, 2);
            if (remainder > 0m && deferredBonusGroups.Count > 0 && bonusEarningTotal > 0m)
            {
                var ordered = deferredBonusGroups.OrderBy(g => g.Code, StringComparer.Ordinal).ToList();
                var shortfall = ordered
                    .Select(g => Math.Max(0m, Math.Round(g.Amount - clearedByCode.GetValueOrDefault(g.Code), 2)))
                    .ToList();
                var useShortfall = shortfall.Sum() > 0m;
                var weights = useShortfall ? shortfall : ordered.Select(g => g.Amount).ToList();
                var weightTotal = weights.Sum();
                var slice = remainder;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var share = i == ordered.Count - 1
                        ? slice
                        : Math.Min(slice, Math.Round(remainder * (weights[i] / weightTotal), 2));
                    slice = Math.Round(slice - share, 2);
                    if (share <= 0m) continue;
                    lines.Add(new FinanceGlEntry
                    {
                        TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                        SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                        DebitAccount = Account(ordered[i].Driver), CreditAccount = string.Empty,
                        Amount = share, Currency = currency,
                        EntryDate = today, Period = period,
                        Description = $"Payroll earning: {ordered[i].Code} (un-accrued)",
                        PostedBy = postedBy, PostedByName = postedByName,
                    });
                }
            }
            else if (remainder > 0m)
            {
                // Defensive: a clearing plan with no bonus earning lines behind it. Keep the pre-B1b-FIX
                // single EARN:BONUS line so the journal can never come out unbalanced.
                lines.Add(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                    SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                    DebitAccount = Account("EARN:BONUS"), CreditAccount = string.Empty,
                    Amount = remainder, Currency = currency,
                    EntryDate = today, Period = period,
                    Description = "Payroll earning: BONUS (un-accrued)",
                    PostedBy = postedBy, PostedByName = postedByName,
                });
            }
        }

        // ── Deductions (Credit side) ──────────────────────────────────────────
        // Employer-expense pairs accumulate per paired-expense driver key so a client-defined pair
        // (via a custom driver) balances the same way the system DED:STATUTORY_ER row does.
        var employerExpenseByPairKey = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var grp in deductions.GroupBy(d => new { d.ComponentCode, d.Source }))
        {
            var driverRow = ResolveDriverForComponent(gl.Drivers, grp.Key.ComponentCode, grp.Key.Source, GlDriverCategories.Deduction);
            string driver;
            bool isEmployerSide;
            string pairKey;
            if (driverRow is not null)
            {
                driver = driverRow.Key;
                isEmployerSide = driverRow.EmitsEmployerExpensePair;
                pairKey = driverRow.PairedExpenseDriverKey ?? "EMPLOYER_STATUTORY_EXPENSE";
            }
            else
            {
                driver = DeductionDriverKey(grp.Key.ComponentCode, grp.Key.Source, out isEmployerSide);
                pairKey = "EMPLOYER_STATUTORY_EXPENSE";
            }
            if (isEmployerSide)
                employerExpenseByPairKey[pairKey] = employerExpenseByPairKey.GetValueOrDefault(pairKey) + grp.Sum(d => d.Amount);

            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                DebitAccount = string.Empty, CreditAccount = Account(driver),
                Amount = grp.Sum(d => d.Amount), Currency = currency,
                EntryDate = today, Period = period,
                Description = $"{PayrollGlDescriptions.DeductionPrefix}{grp.Key.ComponentCode}",
                PostedBy = postedBy, PostedByName = postedByName,
            });
        }

        // Employer statutory contribution: DR expense to balance the CR liability above.
        foreach (var (pairKey, amount) in employerExpenseByPairKey.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (amount <= 0) continue;
            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                DebitAccount = Account(pairKey), CreditAccount = string.Empty,
                Amount = amount, Currency = currency,
                EntryDate = today, Period = period,
                Description = "Employer statutory contributions (social insurance)",
                PostedBy = postedBy, PostedByName = postedByName,
            });
        }

        // Net salary payable balances all DR earnings vs. CR deductions.
        lines.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
            SourceEntityRef = period, EventType = GlEventTypes.Accrual,
            DebitAccount = string.Empty, CreditAccount = Account("NET_PAYABLE"),
            Amount = totalNetSalary, Currency = currency,
            EntryDate = today, Period = period,
            Description = PayrollGlDescriptions.NetPayable,
            PostedBy = postedBy, PostedByName = postedByName,
        });

        var totalDebits  = lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var totalCredits = lines.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        return (lines, totalDebits, totalCredits);
    }

    /// <summary>
    /// Loads the company-first GL resolution context for a run: mapping overrides (company row wins
    /// over tenant default per driver), the persisted driver defaults (company row → tenant row), and
    /// the driver set for data-driven routing. IgnoreQueryFilters is intentional and mirrors
    /// CompanyTaxPolicyResolver: GL posting is a SYSTEM read that must see BOTH the run's company rows
    /// and the tenant-default (CompanyId == null) rows regardless of the caller's own company claims;
    /// the WHERE re-applies exact tenant + company/default scope and never reads another tenant.
    /// </summary>
    private Task<GlResolutionContext> LoadGlResolutionContextAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
        => GlAccountResolver.LoadAsync(_db, tenantId, companyId, ct);

    /// <summary>Per-tenant kill-switch for the data-driven pay-component engine. Default ON: the engine is
    /// proven byte-identical to the legacy compiled sequence by the golden-master + equivalence-twin tests
    /// and falls back to the compiled PayComponentCatalog when the store is empty, so enabling it changes NO
    /// output for any tenant (seeded or not). Setting Payroll/UseComponentEngine to "false"/"0"/"off"
    /// reverts a tenant to the legacy inline block for instant rollback. Explicitly tenant-scoped (does not
    /// rely on the ambient global query filter) so the switch is correct regardless of the resolving
    /// context — this is a per-tenant behaviour toggle, never a cross-tenant read.</summary>
    private async Task<bool> ResolveUseComponentEngineAsync(Guid tenantId, CancellationToken ct)
    {
        var val = await _db.SystemSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Category == "Payroll" && x.SettingKey == "UseComponentEngine")
            .Select(x => x.SettingValue)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(val)) return true; // default ON
        return val.Trim().ToLowerInvariant() is not ("false" or "0" or "off" or "no" or "disabled");
    }

    /// <summary>Loads the active pay-component definitions for a run (company-first). Mirrors
    /// LoadGlResolutionContextAsync / CompanyTaxPolicyResolver: IgnoreQueryFilters is intentional — payroll
    /// processing is a SYSTEM read that must see BOTH the run's company rows AND the tenant-default
    /// (CompanyId == null) rows regardless of the caller's own company claims; the WHERE re-applies exact
    /// tenant + company/default scope and never reads another tenant. The company row wins over the
    /// tenant-default per (Code, ComponentType). When the store is empty the compiled PayComponentCatalog
    /// system seeds are returned so an un-seeded tenant is byte-identical to the legacy path (the same
    /// empty-store fallback the gl_drivers store uses).</summary>
    private async Task<IReadOnlyList<PayComponent>> LoadPayComponentsAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var rows = await _db.PayComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted
                     && (c.CompanyId == companyId || c.CompanyId == null))
            .ToListAsync(ct);
        if (rows.Count == 0)
            return PayComponentCatalog.SystemComponentSeeds(tenantId); // compiled fallback (empty store)
        return rows
            .GroupBy(c => (c.Code, c.ComponentType))
            .Select(g => g.OrderByDescending(c => c.CompanyId != null).First())
            .ToList();
    }

    // M1: audit log now captures caller IP and structured metadata
    private async Task PayrollAudit(string action, string entity, string entityId, object? metadata, CancellationToken ct)
    {
        var ip = _http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var meta = new { ip, userId = GetUserId()?.ToString(), data = metadata };
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = GetTenantId(),
            Action = action,
            EntityName = entity,
            EntityId = entityId,
            UserId = GetUserId(),
            MetadataJson = JsonSerializer.Serialize(meta),
        });
        // Seq / PreviousHash / EntryHash are stamped by the ZayraDbContext sealer when the row is
        // flushed by the business SaveChangesAsync — never set here (this helper defers the save).
        await Task.CompletedTask;
    }

    /// <summary>
    /// POD-A3: verifies the tamper-evident payroll audit chain for the caller's tenant and reports
    /// the first break (unsealed row, altered content, reordering, or a broken previous-hash link).
    /// Segregation-of-duties gate: restricted to Admin and the read-only Auditor role — the Payroll
    /// Manager/Officer roles that CREATE payroll-audit events cannot self-attest their integrity.
    /// The report exposes only Action/EntityName/timestamp/Id/reason (no payroll metadata).
    /// </summary>
    [HttpGet("audit/integrity")]
    [Authorize(Roles = "Admin,Auditor")]
    public async Task<IActionResult> AuditIntegrity(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var logs = await _db.PayrollAuditLogs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Seq)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        return Ok(Zayra.Api.Infrastructure.Audit.AuditService.VerifyPayrollChain(logs));
    }

    // ── Salary Structure Export / Import / Template ───────────────────────────
    private static readonly string[] SalaryStructureCsvHeaders =
        {
            "CompanyLegalName", "Code", "Name", "Currency", "EffectiveDate", "IsActive",
            "MinGrossSalary", "MaxGrossSalary", "MinBasicSalary", "MaxBasicSalary",
            "EligibleGradeIds", "EligibleDesignationIds",
            "ComponentCode", "ComponentName", "ComponentType", "CalculationType",
            "Amount", "Percentage", "IsTaxable", "ComponentIsActive"
        };

    [HttpGet("structures/export")]
    [HttpGet("salary-structures/export")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ExportStructures(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var structures = await _db.SalaryStructures
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .GroupJoin(_db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted),
                s => s.CompanyId, c => c.Id, (s, companies) => new { s, company = companies.FirstOrDefault() })
            .OrderBy(x => x.s.Name)
            .ToListAsync(cancellationToken);
        var structureIds = structures.Select(x => x.s.Id).ToList();
        var components = await _db.SalaryComponents
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.SalaryStructureId.HasValue && structureIds.Contains(c.SalaryStructureId.Value))
            .OrderBy(c => c.Code)
            .ToListAsync(cancellationToken);
        var componentsByStructure = components
            .GroupBy(c => c.SalaryStructureId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var rows = structures.SelectMany(x =>
        {
            var components = componentsByStructure.GetValueOrDefault(x.s.Id);
            if (components is null || components.Count == 0)
                return [(IReadOnlyList<object?>)new object?[]
                {
                    x.company?.LegalNameEn ?? string.Empty, x.s.Code, x.s.Name, x.s.Currency,
                    x.s.EffectiveDate.ToString("yyyy-MM-dd"), x.s.IsActive ? "true" : "false",
                    x.s.MinGrossSalary, x.s.MaxGrossSalary, x.s.MinBasicSalary, x.s.MaxBasicSalary,
                    FormatGuidSet(x.s.EligibleGradeIdsJson), FormatGuidSet(x.s.EligibleDesignationIdsJson),
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty
                }];
            return components.Select(c => (IReadOnlyList<object?>)new object?[]
            {
                x.company?.LegalNameEn ?? string.Empty, x.s.Code, x.s.Name, x.s.Currency,
                x.s.EffectiveDate.ToString("yyyy-MM-dd"), x.s.IsActive ? "true" : "false",
                x.s.MinGrossSalary, x.s.MaxGrossSalary, x.s.MinBasicSalary, x.s.MaxBasicSalary,
                FormatGuidSet(x.s.EligibleGradeIdsJson), FormatGuidSet(x.s.EligibleDesignationIdsJson),
                c.Code, c.Name, c.ComponentType, c.CalculationType, c.Amount, c.Percentage,
                c.IsTaxable ? "true" : "false", c.IsActive ? "true" : "false"
            });
        });
        var csv = Csv.Build(SalaryStructureCsvHeaders, rows);
        Response.Headers["Content-Disposition"] = "attachment; filename=salary_structures_export.csv";
        return Content(csv, "text/csv");
    }

    [HttpGet("structures/import-template")]
    [HttpGet("salary-structures/import-template")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public IActionResult StructuresImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=salary_structures_import_template.csv";
        return Content(Csv.Template(SalaryStructureCsvHeaders), "text/csv");
    }

    [HttpPost("structures/import")]
    [HttpPost("salary-structures/import")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> ImportStructures([FromBody] ImportSalaryStructuresRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);
        int created = 0, skipped = 0;
        var errors = new List<string>();
        var rowNum = 1;
        var companiesByName = await _db.Companies
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.LegalNameEn.ToUpperInvariant(), cancellationToken);
        var touchedStructureIds = new HashSet<Guid>();
        var batchStructures = new Dictionary<(Guid? CompanyId, string Code), SalaryStructure>();
        foreach (var row in rows)
        {
            rowNum++;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) { skipped++; continue; }
            var companyName = row.GetValueOrDefault("CompanyLegalName", string.Empty).Trim();
            Guid? companyId = null;
            if (!string.IsNullOrWhiteSpace(companyName))
            {
                if (!companiesByName.TryGetValue(companyName.ToUpperInvariant(), out var company))
                { skipped++; errors.Add($"Row {rowNum}: CompanyLegalName '{companyName}' not found."); continue; }
                companyId = company.Id;
            }
            DateOnly.TryParse(row.GetValueOrDefault("EffectiveDate", string.Empty), out var effectiveDate);
            if (effectiveDate == default) effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow);
            if (!TryReadDecimal(row, "MinGrossSalary", rowNum, errors, out var minGross)
                || !TryReadDecimal(row, "MaxGrossSalary", rowNum, errors, out var maxGross)
                || !TryReadDecimal(row, "MinBasicSalary", rowNum, errors, out var minBasic)
                || !TryReadDecimal(row, "MaxBasicSalary", rowNum, errors, out var maxBasic)
                || !TryReadGuidList(row, "EligibleGradeIds", rowNum, errors, out var eligibleGradeIds)
                || !TryReadGuidList(row, "EligibleDesignationIds", rowNum, errors, out var eligibleDesignationIds))
            {
                skipped++;
                continue;
            }
            if (minGross < 0 || maxGross < 0 || minBasic < 0 || maxBasic < 0)
            { errors.Add($"Row {rowNum}: Salary structure range values cannot be negative."); skipped++; continue; }
            if (maxGross > 0 && minGross > maxGross)
            { errors.Add($"Row {rowNum}: Minimum gross salary cannot exceed maximum gross salary."); skipped++; continue; }
            if (maxBasic > 0 && minBasic > maxBasic)
            { errors.Add($"Row {rowNum}: Minimum basic salary cannot exceed maximum basic salary."); skipped++; continue; }
            if (eligibleGradeIds.Count > 0)
            {
                var found = await _db.Grades.CountAsync(g => g.TenantId == tenantId && eligibleGradeIds.Contains(g.Id) && !g.IsDeleted, cancellationToken);
                if (found != eligibleGradeIds.Distinct().Count())
                { errors.Add($"Row {rowNum}: One or more eligible grades were not found."); skipped++; continue; }
            }
            if (eligibleDesignationIds.Count > 0)
            {
                var found = await _db.Designations.CountAsync(d => d.TenantId == tenantId && eligibleDesignationIds.Contains(d.Id) && !d.IsDeleted, cancellationToken);
                if (found != eligibleDesignationIds.Distinct().Count())
                { errors.Add($"Row {rowNum}: One or more eligible designations were not found."); skipped++; continue; }
            }

            var key = (companyId, code.ToUpperInvariant());
            if (!batchStructures.TryGetValue(key, out var structure))
            {
                structure = await _db.SalaryStructures
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CompanyId == companyId && x.Code == code && !x.IsDeleted, cancellationToken);
                if (structure is not null)
                    batchStructures[key] = structure;
            }
            if (structure is null)
            {
                structure = new SalaryStructure
                {
                    TenantId = tenantId,
                    CompanyId = companyId,
                    Code = code,
                    CreatedBy = GetUserId()
                };
                _db.SalaryStructures.Add(structure);
                batchStructures[key] = structure;
                created++;
            }

            structure.Name = name;
            structure.Currency = row.GetValueOrDefault("Currency", "USD").Trim();
            if (string.IsNullOrWhiteSpace(structure.Currency)) structure.Currency = await ResolveCurrencyAsync(tenantId, cancellationToken);
            structure.EffectiveDate = effectiveDate;
            structure.IsActive = !row.TryGetValue("IsActive", out var activeRaw) || !string.Equals(activeRaw, "false", StringComparison.OrdinalIgnoreCase);

            structure.MinGrossSalary = minGross;
            structure.MaxGrossSalary = maxGross;
            structure.MinBasicSalary = minBasic;
            structure.MaxBasicSalary = maxBasic;
            structure.EligibleGradeIdsJson = JsonSerializer.Serialize(eligibleGradeIds.Distinct().ToList());
            structure.EligibleDesignationIdsJson = JsonSerializer.Serialize(eligibleDesignationIds.Distinct().ToList());

            if (touchedStructureIds.Add(structure.Id))
            {
                var existingComponents = await _db.SalaryComponents
                    .Where(c => c.TenantId == tenantId && c.SalaryStructureId == structure.Id)
                    .ToListAsync(cancellationToken);
                _db.SalaryComponents.RemoveRange(existingComponents);
            }

            var componentCode = row.GetValueOrDefault("ComponentCode", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(componentCode))
            {
                decimal.TryParse(row.GetValueOrDefault("Amount", "0"), out var amount);
                decimal.TryParse(row.GetValueOrDefault("Percentage", "0"), out var percentage);
                _db.SalaryComponents.Add(new SalaryComponent
                {
                    TenantId = tenantId,
                    SalaryStructureId = structure.Id,
                    Code = componentCode,
                    Name = row.GetValueOrDefault("ComponentName", componentCode).Trim(),
                    ComponentType = row.GetValueOrDefault("ComponentType", "Earning").Trim(),
                    CalculationType = row.GetValueOrDefault("CalculationType", "Fixed").Trim(),
                    Amount = amount,
                    Percentage = percentage,
                    IsTaxable = row.TryGetValue("IsTaxable", out var taxableRaw) && string.Equals(taxableRaw, "true", StringComparison.OrdinalIgnoreCase),
                    IsActive = !row.TryGetValue("ComponentIsActive", out var componentActiveRaw) || !string.Equals(componentActiveRaw, "false", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { received = rows.Count, created, skipped, errors = errors.Take(20) });
    }

    /// <summary>
    /// Per-employee reconciliation between contract salary and the processed payroll
    /// slip, plus WPS/GOSI/QIWA readiness flags.  Variance &gt; 5% is flagged as a warning.
    /// </summary>
    [HttpGet("runs/{id:guid}/mismatch-report")]
    public async Task<IActionResult> MismatchReport(Guid id, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.review")) return Forbid();

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var slips = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == id).ToListAsync(cancellationToken);
        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(cancellationToken);
        var empIds = slips.Select(s => s.EmployeeId).ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && empIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        var rows = new List<object>();
        foreach (var slip in slips)
        {
            var contractBasic = salaries
                .Where(s => s.EmployeeId == slip.EmployeeId)
                .OrderByDescending(s => s.EffectiveDate)
                .Select(s => s.BasicSalary)
                .FirstOrDefault();
            var variance = slip.BasicSalary - contractBasic;
            var variancePercent = contractBasic == 0 ? 0 : Math.Round((double)(variance / contractBasic) * 100, 2);

            var iban = profiles.FirstOrDefault(p => p.EmployeeId == slip.EmployeeId)?.Iban;
            var hasValidIban = Infrastructure.Payroll.IbanValidator.IsValid(iban);
            var emp = employees.FirstOrDefault(e => e.Id == slip.EmployeeId);
            var missingGosiRef = emp is null || string.IsNullOrWhiteSpace(emp.GosiReference);
            var missingQiwa = emp is null ? new List<string>() : Infrastructure.Qiwa.QiwaIntegrationService.MissingQiwaFields(emp);

            var issues = new List<string>();
            if (Math.Abs(variancePercent) > 5) issues.Add($"Payroll basic differs from contract salary by {variancePercent}%.");
            if (!hasValidIban) issues.Add("Missing or invalid IBAN.");
            if (missingGosiRef) issues.Add("Missing GOSI reference.");
            if (missingQiwa.Count > 0) issues.Add($"Missing QIWA fields: {string.Join(", ", missingQiwa)}.");

            rows.Add(new
            {
                employeeId = slip.EmployeeId,
                employeeCode = slip.EmployeeCode,
                employeeName = slip.EmployeeName,
                contractSalary = contractBasic,
                payrollBasic = slip.BasicSalary,
                variance,
                variancePercent,
                hasValidIban,
                missingGosiRef,
                missingQiwaFields = missingQiwa,
                isWarning = Math.Abs(variancePercent) > 5,
                issues
            });
        }

        return Ok(new { runId = id, period = $"{run.Year}-{run.Month:D2}", employeeCount = rows.Count, employees = rows });
    }

    /// <summary>Month-over-month headcount and compensation reconciliation vs the prior payroll run.</summary>
    [HttpGet("reports/reconciliation")]
    public async Task<IActionResult> Reconciliation([FromQuery] Guid runId, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.review")) return Forbid();
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == runId, cancellationToken);
        if (run is null) return NotFound();

        var (priorYear, priorMonth) = run.Month == 1 ? (run.Year - 1, 12) : (run.Year, run.Month - 1);
        // POD-B2 — this lookup was company-, type- AND status-blind: in a multi-entity tenant it could
        // already pick another company's run as "last month", and post-B2 it could pick an off-cycle or
        // voided run, producing a joiners/leavers/variance report against a bonus-only population. The
        // month-over-month comparison is only meaningful against the prior REGULAR run of the SAME entity.
        var priorRun = await _db.PayrollRuns.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Year == priorYear && x.Month == priorMonth
                     && x.CompanyId == run.CompanyId
                     && x.RunType == PayrollRunTypes.Regular
                     && x.Status != "Voided")
            .FirstOrDefaultAsync(cancellationToken);

        var currentSlips = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == runId).ToListAsync(cancellationToken);
        var priorSlips   = priorRun is not null ? await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == priorRun.Id).ToListAsync(cancellationToken) : new List<PayrollSlip>();

        var currentIds = currentSlips.Select(s => s.EmployeeId).ToHashSet();
        var priorIds   = priorSlips.Select(s => s.EmployeeId).ToHashSet();

        var joiners    = currentIds.Except(priorIds).ToList();
        var leavers    = priorIds.Except(currentIds).ToList();
        var continuing = currentIds.Intersect(priorIds).ToList();

        var variances = continuing.Select(empId =>
        {
            var cur  = currentSlips.First(s => s.EmployeeId == empId);
            var prev = priorSlips.First(s => s.EmployeeId == empId);
            var grossDelta       = cur.GrossSalary - prev.GrossSalary;
            var grossVariancePct = prev.GrossSalary == 0 ? 0.0 : Math.Round((double)(grossDelta / prev.GrossSalary) * 100, 2);
            return new
            {
                employeeId = empId, employeeName = cur.EmployeeName, employeeCode = cur.EmployeeCode,
                priorGross = prev.GrossSalary, currentGross = cur.GrossSalary, grossDelta, grossVariancePct,
                priorNet = prev.NetSalary, currentNet = cur.NetSalary, netDelta = cur.NetSalary - prev.NetSalary,
                isVarianceFlag = Math.Abs(grossVariancePct) > 5
            };
        }).ToList();

        return Ok(new
        {
            runId, period = $"{run.Year}-{run.Month:D2}",
            priorPeriod   = priorRun is not null ? $"{priorRun.Year}-{priorRun.Month:D2}" : null,
            currentHeadcount = currentIds.Count, priorHeadcount = priorIds.Count,
            joinerCount = joiners.Count, leaverCount = leavers.Count,
            currentTotalGross = currentSlips.Sum(s => s.GrossSalary), priorTotalGross = priorSlips.Sum(s => s.GrossSalary),
            currentTotalNet   = currentSlips.Sum(s => s.NetSalary),   priorTotalNet   = priorSlips.Sum(s => s.NetSalary),
            flaggedVariances  = variances.Count(v => v.isVarianceFlag),
            variances
        });
    }

    /// <summary>
    /// Cost-centre payroll allocation — aggregates a run's payroll cost by cost centre
    /// (resolved via each employee's CostCenterId). Enterprise finance reporting: answers
    /// "what did we spend per cost centre this period?". Employees with no cost centre roll
    /// up into an "Unassigned" bucket so the totals always reconcile to the run total.
    /// TotalCost = gross pay + employer statutory cost (the true cost to the company).
    /// </summary>
    [HttpGet("runs/{id:guid}/cost-center-allocation")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> CostCenterAllocation(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var slips = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RunId == id).ToListAsync(cancellationToken);
        var empIds = slips.Select(s => s.EmployeeId).Distinct().ToList();
        var ccByEmp = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && empIds.Contains(e.Id))
            .Select(e => new { e.Id, e.CostCenterId })
            .ToDictionaryAsync(e => e.Id, e => e.CostCenterId, cancellationToken);
        var costCenters = await _db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        var currency = await ResolveCurrencyAsync(tenantId, cancellationToken);

        var grouped = slips
            .GroupBy(s => ccByEmp.TryGetValue(s.EmployeeId, out var cc) ? cc : null)
            .Select(g =>
            {
                var ccId = g.Key;
                var code = "—";
                var name = "Unassigned";
                if (ccId.HasValue && costCenters.TryGetValue(ccId.Value, out var cc)) { code = cc.Code; name = cc.Name; }
                var gross = g.Sum(s => s.GrossSalary);
                var employer = g.Sum(s => s.EmployerStatutoryTotal);
                return new
                {
                    costCenterId = ccId,
                    costCenterCode = code,
                    costCenterName = name,
                    employeeCount = g.Count(),
                    grossSalary = gross,
                    netSalary = g.Sum(s => s.NetSalary),
                    employerCost = employer,
                    totalCost = gross + employer,
                };
            })
            .OrderByDescending(a => a.totalCost)
            .ToList();

        var totalCost = grouped.Sum(a => a.totalCost);
        var allocations = grouped.Select(a => new
        {
            a.costCenterId, a.costCenterCode, a.costCenterName, a.employeeCount,
            a.grossSalary, a.netSalary, a.employerCost, a.totalCost,
            percentOfTotal = totalCost == 0 ? 0m : Math.Round(a.totalCost / totalCost * 100, 2),
        }).ToList();

        return Ok(new
        {
            runId = id,
            period = $"{run.Year}-{run.Month:D2}",
            currency,
            totalEmployees = slips.Count,
            totalGross = grouped.Sum(a => a.grossSalary),
            totalNet = grouped.Sum(a => a.netSalary),
            totalEmployerCost = grouped.Sum(a => a.employerCost),
            totalCost,
            allocations,
        });
    }

    /// <summary>Final settlement calculator: pro-rata salary + EOSB + leave encashment - notice deduction.</summary>
    [HttpPost("final-settlement")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> FinalSettlement([FromBody] FinalSettlementRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == req.EmployeeId && !e.IsDeleted, cancellationToken);
        if (employee is null) return NotFound(new { message = "Employee not found." });

        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive && x.EffectiveDate <= req.LastWorkingDay)
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);
        var basicSalary    = salary?.BasicSalary ?? employee.Salary ?? 0m;
        var grossSalary    = basicSalary + (salary?.HousingAllowance ?? 0m) + (salary?.TransportAllowance ?? 0m)
                           + (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
        var currency       = !string.IsNullOrWhiteSpace(salary?.Currency) ? salary!.Currency : await ResolveCurrencyAsync(tenantId, cancellationToken);

        // Pro-rata salary for partial month
        var lastDay        = req.LastWorkingDay;
        var daysInMonth    = DateTime.DaysInMonth(lastDay.Year, lastDay.Month);
        var dailyGross     = grossSalary / daysInMonth;
        var proRataSalary  = Math.Round(dailyGross * lastDay.Day, 2);

        // EOSB / Gratuity — routed through the SAME single authoritative engine as
        // /eosb/calculate (ComputeEndOfServiceAsync → country-pack IEndOfServiceCalculator).
        // The old inline flat-fraction formula (½-then-1 rule mis-implemented as a flat
        // fraction on TOTAL years, with no Art.85 resignation discount) is DELETED. Reason
        // precedence and the pack's Art.80/84/85 tiers now own every EOSB figure, so equal
        // (basic, dates, reason, country) yield byte-identical results on both endpoints.
        var gcc = await _db.GCCComplianceSettings.AsNoTracking().Where(x => x.TenantId == tenantId).FirstOrDefaultAsync(cancellationToken);
        var calcDate = lastDay.ToDateTime(TimeOnly.MinValue);
        var terminationReason = await ResolveTerminationReasonAsync(tenantId, req.EmployeeId, req.TerminationReason, cancellationToken);

        decimal eosbAmount = 0m;
        double totalYears = (calcDate - employee.JoiningDate).Days / 365.0;
        // EOSB is computed only when the tenant has a country pack configured AND EOSB enabled —
        // matching /eosb/calculate, which refuses when EOSB is not enabled. (Behavior change vs
        // the old path, which computed a UAE-style figure even with no GCC row / EOSB disabled;
        // flagged for the lead. The rest of the settlement still computes regardless.)
        if (gcc is not null && gcc.EosbEnabled)
        {
            var (eosbResult, svcYears) = await ComputeEndOfServiceAsync(
                gcc, basicSalary, employee.JoiningDate, calcDate, terminationReason, employee, cancellationToken);
            eosbAmount = Math.Round(eosbResult.TotalGratuity, 2);
            totalYears = svcYears;
        }

        // Leave encashment: remaining balance × daily gross (30-day basis)
        var leaveBalances = await _db.EmployeeLeaveBalances.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.Year == lastDay.Year)
            .ToListAsync(cancellationToken);
        var leaveBalanceDays = Math.Max(0m, leaveBalances.Sum(b => b.Accrued + b.CarriedForward + b.ManualAdjustment - b.Used - b.Pending - b.Encashed - b.Expired));
        var leaveEncashment  = Math.Round(leaveBalanceDays * grossSalary / 30m, 2);

        // Notice period deduction for days short
        var noticePeriodDeduction = Math.Round(req.NoticePeriodDaysShort * grossSalary / 30m, 2);

        var totalPayable = proRataSalary + eosbAmount + leaveEncashment - noticePeriodDeduction;

        await PayrollAudit("payroll.final_settlement.calculated", "Employee", req.EmployeeId.ToString(), new { lastWorkingDay = req.LastWorkingDay, totalPayable }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            employeeId = req.EmployeeId, employeeName = employee.FullName,
            lastWorkingDay = req.LastWorkingDay, currency,
            basicSalary, grossSalary,
            proRataSalary, daysWorkedInMonth = lastDay.Day, daysInMonth,
            eosbAmount, totalYears = Math.Round(totalYears, 2), terminationReason,
            leaveBalanceDays, leaveEncashment,
            noticePeriodDaysShort = req.NoticePeriodDaysShort, noticePeriodDeduction,
            totalPayable = Math.Round(totalPayable, 2),
            breakdown = new[]
            {
                new { component = "Pro-rata Salary",          amount =  proRataSalary },
                new { component = "EOSB / Gratuity",          amount =  eosbAmount },
                new { component = "Leave Encashment",         amount =  leaveEncashment },
                new { component = "Notice Period Deduction",  amount = -noticePeriodDeduction },
            }
        });
    }

    private Task<string> ResolveCurrencyAsync(Guid tenantId, CancellationToken ct)
        => _db.ResolveTenantCurrencyAsync(tenantId, ct);

    // ── Payroll Command Center ────────────────────────────────────────────────────

    [HttpGet("companies")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListPayrollCompanies(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var companies = await _db.Companies
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.LegalNameEn)
            .Select(c => new { c.Id, Name = c.LegalNameEn, TradeName = c.TradeName, c.DefaultCurrency, c.WpsEmployerId, c.GosiEmployerId })
            .ToListAsync(cancellationToken);
        return Ok(companies);
    }

    [HttpGet("overview")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> PayrollOverview([FromQuery] Guid? companyId, [FromQuery] int? year, [FromQuery] int? month, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var now = DateTime.UtcNow;
        var targetYear = year ?? now.Year;
        var targetMonth = month ?? now.Month;

        var companies = await _db.Companies
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .ToListAsync(cancellationToken);

        if (companyId.HasValue)
            companies = companies.Where(c => c.Id == companyId.Value).ToList();

        var employeesByCompany = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .GroupBy(e => e.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var salaryAssignedByCompany = await _db.EmployeeSalaryStructures
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .Join(_db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active"),
                  s => s.EmployeeId, e => e.Id, (s, e) => new { e.CompanyId })
            .GroupBy(x => x.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var runsForMonth = await _db.PayrollRuns
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth)
            .ToListAsync(cancellationToken);

        var validationErrors = await _db.PayrollValidationResults
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && !v.IsResolved)
            .Join(_db.PayrollRuns.Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth),
                  v => v.PayrollRunId, r => r.Id, (v, r) => new { r.CompanyId, v.Severity })
            .GroupBy(x => x.CompanyId)
            .Select(g => new { CompanyId = g.Key, Errors = g.Count(x => x.Severity == "Error"), Warnings = g.Count(x => x.Severity == "Warning") })
            .ToListAsync(cancellationToken);

        var pendingApprovals = await _db.PayrollApprovals
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Decision == "Pending")
            .Join(_db.PayrollRuns.Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth),
                  a => a.PayrollRunId, r => r.Id, (a, r) => new { r.CompanyId })
            .GroupBy(x => x.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // POD-B2 (M5b) — deliberate hold-outs, surfaced on the dashboard card. `Pending` counts intent
        // that has NOT yet been applied by a Process pass: before Process an Exclude row has a null
        // Outcome and produces no validation result at all, so it would otherwise be invisible until
        // someone opened GET runs/{id}/population.
        var exclusionsByCompany = await _db.PayrollRunEmployeeSelections
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Join(_db.PayrollRuns.Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth && r.Status != "Voided"),
                  s => s.PayrollRunId, r => r.Id, (s, r) => new { r.CompanyId, s.Mode, s.Outcome })
            .GroupBy(x => x.CompanyId)
            .Select(g => new
            {
                CompanyId = g.Key,
                Excluded  = g.Count(x => x.Outcome == PayrollRunSelectionOutcomes.Excluded),
                Pending   = g.Count(x => x.Outcome == null),
            })
            .ToListAsync(cancellationToken);

        var result = companies.Select(c =>
        {
            var empCount = employeesByCompany.FirstOrDefault(x => x.CompanyId == c.Id)?.Count ?? 0;
            var salaryCount = salaryAssignedByCompany.FirstOrDefault(x => x.CompanyId == c.Id)?.Count ?? 0;
            // POD-B2 — the monthly cycle card describes the REGULAR run. FirstOrDefault used to pick an
            // arbitrary run for the company, which post-B2 could be an off-cycle bonus run and would make
            // the dashboard report a company's gross payroll as a few thousand in bonuses.
            var companyRuns = runsForMonth.Where(r => r.CompanyId == c.Id && r.Status != "Voided").ToList();
            var run = companyRuns.FirstOrDefault(r => r.RunType == PayrollRunTypes.Regular)
                   ?? runsForMonth.FirstOrDefault(r => r.CompanyId == c.Id && r.RunType == PayrollRunTypes.Regular);
            var offCycleRunCount = companyRuns.Count(r => r.RunType != PayrollRunTypes.Regular);
            var valErr = validationErrors.FirstOrDefault(v => v.CompanyId == c.Id);
            var pendAppr = pendingApprovals.FirstOrDefault(p => p.CompanyId == c.Id);
            var exclusions = exclusionsByCompany.FirstOrDefault(x => x.CompanyId == c.Id);
            return new
            {
                CompanyId = c.Id,
                CompanyName = c.LegalNameEn,
                TradeName = c.TradeName,
                Currency = c.DefaultCurrency,
                ActiveEmployees = empCount,
                EmployeesWithSalary = salaryCount,
                EmployeesMissingSalary = Math.Max(0, empCount - salaryCount),
                SalaryCoveragePercent = empCount > 0 ? Math.Round(salaryCount * 100.0 / empCount, 1) : 0.0,
                PayrollRunStatus = run?.Status,
                GrossPayroll = run?.TotalGrossSalary ?? 0,
                TotalDeductions = run?.TotalDeductions ?? 0,
                NetPayroll = run?.TotalNetSalary ?? 0,
                ValidationErrors = valErr?.Errors ?? 0,
                ValidationWarnings = valErr?.Warnings ?? 0,
                PendingApprovals = pendAppr?.Count ?? 0,
                WpsEmployerId = c.WpsEmployerId,
                GosiEmployerId = c.GosiEmployerId,
                HasPayrollRun = run != null,
                // POD-B2 — additional runs in the period, counted separately so the monthly figures above
                // stay a like-for-like month-over-month series.
                OffCycleRunCount = offCycleRunCount,
                OffCycleGross    = companyRuns.Where(r => r.RunType != PayrollRunTypes.Regular).Sum(r => r.TotalGrossSalary),
                OffCycleNet      = companyRuns.Where(r => r.RunType != PayrollRunTypes.Regular).Sum(r => r.TotalNetSalary),
                // Employees deliberately held out of this period's runs (and intent not yet applied).
                ExcludedEmployees         = exclusions?.Excluded ?? 0,
                PendingExclusionSelections = exclusions?.Pending ?? 0,
            };
        }).ToList();

        return Ok(new
        {
            Year = targetYear,
            Month = targetMonth,
            TotalCompanies = result.Count,
            TotalActiveEmployees = result.Sum(r => r.ActiveEmployees),
            TotalGrossPayroll = result.Sum(r => r.GrossPayroll),
            TotalNetPayroll = result.Sum(r => r.NetPayroll),
            TotalValidationErrors = result.Sum(r => r.ValidationErrors),
            TotalPendingApprovals = result.Sum(r => r.PendingApprovals),
            Companies = result,
        });
    }

    [HttpGet("readiness")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> PayrollReadiness([FromQuery] Guid? companyId, [FromQuery] int? year, [FromQuery] int? month, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var now = DateTime.UtcNow;
        var targetYear = year ?? now.Year;
        var targetMonth = month ?? now.Month;

        var hasComponents = await _db.SalaryComponents
            .AnyAsync(c => c.TenantId == tenantId && c.IsActive, cancellationToken);

        var structureQuery = _db.SalaryStructures.Where(s => s.TenantId == tenantId && !s.IsDeleted && s.IsActive);
        if (companyId.HasValue)
            structureQuery = structureQuery.Where(s => s.CompanyId == companyId || s.CompanyId == null);
        var hasStructures = await structureQuery.AnyAsync(cancellationToken);
        var structureCount = await structureQuery.CountAsync(cancellationToken);

        var activeEmployeeQuery = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active");
        if (companyId.HasValue) activeEmployeeQuery = activeEmployeeQuery.Where(e => e.CompanyId == companyId);
        var totalActive = await activeEmployeeQuery.CountAsync(cancellationToken);

        var assignedCount = await _db.EmployeeSalaryStructures
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .Join(activeEmployeeQuery, s => s.EmployeeId, e => e.Id, (s, e) => s.Id)
            .CountAsync(cancellationToken);

        var coveragePercent = totalActive > 0 ? Math.Round(assignedCount * 100.0 / totalActive, 1) : 0.0;

        // POD-B2 — the 6-step readiness checklist describes the MONTHLY cycle, so it must track the
        // Regular run. Without the filter an off-cycle bonus run could mark "Payroll Run Created"
        // complete and report its status as the month's, while the real monthly run had not been started.
        var runQuery = _db.PayrollRuns.Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth
                                              && r.RunType == PayrollRunTypes.Regular && r.Status != "Voided");
        if (companyId.HasValue) runQuery = runQuery.Where(r => r.CompanyId == companyId);
        var run = await runQuery.FirstOrDefaultAsync(cancellationToken);
        var offCycleRunsForPeriod = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth
                     && r.RunType != PayrollRunTypes.Regular && r.Status != "Voided"
                     && (!companyId.HasValue || r.CompanyId == companyId))
            .CountAsync(cancellationToken);

        var validationErrors = run != null
            ? await _db.PayrollValidationResults
                .CountAsync(v => v.TenantId == tenantId && v.PayrollRunId == run.Id && !v.IsResolved && v.Severity == "Error", cancellationToken)
            : 0;

        var steps = new[]
        {
            new { Step = 1, Label = "Salary Components", Complete = hasComponents, Detail = hasComponents ? "Components configured" : "No salary components found" },
            new { Step = 2, Label = "Salary Structures", Complete = hasStructures, Detail = hasStructures ? $"{structureCount} structure(s) active" : "No salary structures found" },
            new { Step = 3, Label = "Employee Salary Assignment", Complete = coveragePercent >= 80, Detail = $"{assignedCount}/{totalActive} employees assigned ({coveragePercent}%)" },
            new { Step = 4, Label = "Payroll Run Created", Complete = run != null, Detail = run != null ? $"Run status: {run.Status}" : "No payroll run for this period" },
            new { Step = 5, Label = "Validation Passed", Complete = run != null && validationErrors == 0, Detail = validationErrors > 0 ? $"{validationErrors} unresolved error(s)" : "No blocking errors" },
            new { Step = 6, Label = "Ready for Approval", Complete = run?.Status == "Processed" || run?.Status == "PendingFinanceReview" || run?.Status == "Approved" || run?.Status == "Locked", Detail = run?.Status == "Locked" ? "Payroll locked and complete" : "Awaiting processing or approval" },
        };

        var completedSteps = steps.Count(s => s.Complete);
        return Ok(new
        {
            Year = targetYear,
            Month = targetMonth,
            CompanyId = companyId,
            CompletionPercent = Math.Round(completedSteps * 100.0 / steps.Length, 0),
            IsReadyForProcessing = hasComponents && hasStructures && coveragePercent >= 80,
            TotalActiveEmployees = totalActive,
            EmployeesWithSalary = assignedCount,
            SalaryCoveragePercent = coveragePercent,
            ValidationErrors = validationErrors,
            PayrollRunStatus = run?.Status,
            Steps = steps,
            OffCycleRunCount = offCycleRunsForPeriod,   // POD-B2: additional runs in the period, if any
        });
    }

    // ── Employee Salary Import / Export ───────────────────────────────────────────

    private static readonly string[] EmployeeSalaryCsvHeaders =
        { "EmployeeCode", "SalaryStructureCode", "BasicSalary", "HousingAllowance", "TransportAllowance", "FoodAllowance", "MobileAllowance", "OtherAllowance", "FixedDeduction", "Currency", "EffectiveDate" };

    [HttpGet("employee-salaries")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListEmployeeSalaries([FromQuery] Guid? companyId, [FromQuery] string? departmentId, [FromQuery] bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var empQuery = _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && !e.IsDeleted);
        if (companyId.HasValue) empQuery = empQuery.Where(e => e.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(departmentId)) empQuery = empQuery.Where(e => e.Department == departmentId);

        var salaryQuery = _db.EmployeeSalaryStructures.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (activeOnly) salaryQuery = salaryQuery.Where(s => s.IsActive);

        var result = await salaryQuery
            .Join(empQuery, s => s.EmployeeId, e => e.Id, (s, e) => new
            {
                s.Id, s.EmployeeId, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName,
                e.Department, e.CompanyId, s.SalaryStructureId, s.BasicSalary, s.HousingAllowance,
                s.TransportAllowance, s.FoodAllowance, s.MobileAllowance, s.OtherAllowance,
                s.FixedDeduction, s.Currency, s.EffectiveDate, s.IsActive, s.CreatedAtUtc,
            })
            .OrderBy(x => x.EmployeeCode)
            .ToListAsync(cancellationToken);

        return Ok(result);
    }

    [HttpGet("employee-salaries/export")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ExportEmployeeSalaries([FromQuery] Guid? companyId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var empQuery = _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && !e.IsDeleted);
        if (companyId.HasValue) empQuery = empQuery.Where(e => e.CompanyId == companyId);

        var rows = await _db.EmployeeSalaryStructures
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .Join(empQuery, s => s.EmployeeId, e => e.Id, (s, e) => new { s, e })
            .Join(_db.SalaryStructures.Where(st => st.TenantId == tenantId && !st.IsDeleted),
                  x => x.s.SalaryStructureId, st => st.Id, (x, st) => new { x.s, x.e, st })
            .OrderBy(x => x.e.EmployeeCode)
            .Select(x => (IReadOnlyList<object?>)new object?[]
            {
                x.e.EmployeeCode, x.st.Code, x.s.BasicSalary, x.s.HousingAllowance, x.s.TransportAllowance,
                x.s.FoodAllowance, x.s.MobileAllowance, x.s.OtherAllowance, x.s.FixedDeduction,
                x.s.Currency, x.s.EffectiveDate.ToString("yyyy-MM-dd")
            })
            .ToListAsync(cancellationToken);

        await PayrollAudit("payroll.employee_salary.exported", "EmployeeSalary", "bulk", new { count = rows.Count, companyId }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        Response.Headers["Content-Disposition"] = "attachment; filename=employee_salaries_export.csv";
        return Content(Csv.Build(EmployeeSalaryCsvHeaders, rows), "text/csv");
    }

    [HttpGet("employee-salaries/import-template")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public IActionResult EmployeeSalariesImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=employee_salaries_import_template.csv";
        return Content(Csv.Template(EmployeeSalaryCsvHeaders), "text/csv");
    }

    [HttpPost("employee-salaries/import")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> ImportEmployeeSalaries([FromBody] ImportSalaryStructuresRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);
        int created = 0, skipped = 0, updated = 0;
        var errors = new List<string>();
        var rowNum = 1;

        var allEmployees = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .ToDictionaryAsync(e => e.EmployeeCode, cancellationToken);
        var allStructures = await _db.SalaryStructures
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            rowNum++;
            var empCode = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            var structCode = row.GetValueOrDefault("SalaryStructureCode", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(empCode)) { skipped++; continue; }
            if (!allEmployees.TryGetValue(empCode, out var employee))
            { errors.Add($"Row {rowNum}: Employee code '{empCode}' not found."); skipped++; continue; }
            if (string.IsNullOrWhiteSpace(structCode))
            { errors.Add($"Row {rowNum}: SalaryStructureCode is required."); skipped++; continue; }
            var structure = allStructures
                .Where(s => string.Equals(s.Code, structCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.CompanyId == employee.CompanyId)
                .ThenByDescending(s => s.CompanyId == null)
                .FirstOrDefault();
            if (structure is null)
            { errors.Add($"Row {rowNum}: Salary structure code '{structCode}' not found for employee legal entity."); skipped++; continue; }
            if (structure.CompanyId.HasValue && employee.CompanyId.HasValue && structure.CompanyId != employee.CompanyId)
            { errors.Add($"Row {rowNum}: Salary structure belongs to a different legal entity than the employee."); skipped++; continue; }
            if (!decimal.TryParse(row.GetValueOrDefault("BasicSalary", "0"), out var basic) || basic <= 0)
            { errors.Add($"Row {rowNum}: BasicSalary must be a positive number."); skipped++; continue; }

            decimal.TryParse(row.GetValueOrDefault("HousingAllowance", "0"), out var housing);
            decimal.TryParse(row.GetValueOrDefault("TransportAllowance", "0"), out var transport);
            decimal.TryParse(row.GetValueOrDefault("FoodAllowance", "0"), out var food);
            decimal.TryParse(row.GetValueOrDefault("MobileAllowance", "0"), out var mobile);
            decimal.TryParse(row.GetValueOrDefault("OtherAllowance", "0"), out var other);
            decimal.TryParse(row.GetValueOrDefault("FixedDeduction", "0"), out var deduction);
            DateOnly.TryParse(row.GetValueOrDefault("EffectiveDate", string.Empty), out var effectiveDate);
            if (effectiveDate == default) effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow);
            var currency = row.GetValueOrDefault("Currency", "USD");
            var assignmentRequest = new EmployeeSalaryStructureRequest(
                employee.Id,
                structure.Id,
                basic,
                housing,
                transport,
                food,
                mobile,
                other,
                deduction,
                effectiveDate,
                currency);
            var assignmentError = ValidateEmployeeSalaryAssignment(structure, employee, assignmentRequest);
            if (assignmentError is not null)
            { errors.Add($"Row {rowNum}: {assignmentError}"); skipped++; continue; }

            var gross = basic + housing + transport + food + mobile + other - deduction;
            if (employee.GradeId.HasValue)
            {
                var grade = await _db.Grades.AsNoTracking().FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Id == employee.GradeId && !g.IsDeleted, cancellationToken);
                if (grade is not null && grade.MinSalary > 0 && gross < grade.MinSalary)
                { errors.Add($"Row {rowNum}: Salary package is below grade {grade.Code} minimum {grade.MinSalary:N2} {grade.Currency}."); skipped++; continue; }
                if (grade is not null && grade.MaxSalary > 0 && gross > grade.MaxSalary)
                { errors.Add($"Row {rowNum}: Salary package exceeds grade {grade.Code} maximum {grade.MaxSalary:N2} {grade.Currency}."); skipped++; continue; }
            }

            var existing = await _db.EmployeeSalaryStructures.FirstOrDefaultAsync(s =>
                s.TenantId == tenantId
                && s.EmployeeId == employee.Id
                && s.EffectiveDate == effectiveDate, cancellationToken);
            var isNew = existing is null;
            existing ??= new EmployeeSalaryStructure
            {
                TenantId = tenantId,
                EmployeeId = employee.Id,
                EffectiveDate = effectiveDate,
                CreatedBy = GetUserId(),
            };

            existing.SalaryStructureId = structure.Id;
            existing.BasicSalary = basic;
            existing.HousingAllowance = housing;
            existing.TransportAllowance = transport;
            existing.FoodAllowance = food;
            existing.MobileAllowance = mobile;
            existing.OtherAllowance = other;
            existing.FixedDeduction = deduction;
            existing.Currency = currency;
            existing.IsActive = true;

            await _db.EmployeeSalaryStructures
                .Where(s => s.TenantId == tenantId && s.EmployeeId == employee.Id && s.IsActive && s.EffectiveDate == effectiveDate && s.Id != existing.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), cancellationToken);

            if (isNew)
            {
                _db.EmployeeSalaryStructures.Add(existing);
                created++;
            }
            else
            {
                updated++;
            }
        }

        await PayrollAudit("payroll.employee_salary.imported", "EmployeeSalary", "bulk", new { received = rows.Count, created, updated, skipped, errors = errors.Count }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { received = rows.Count, created, updated, skipped, errors = errors.Take(20) });
    }

    // ── Admin payslip PDF download ─────────────────────────────────────────────
    // id is the Payslip.Id (formal payslip record) — the same ID the payslips table returns.
    // The endpoint resolves the matching PayrollSlip row via RunId + EmployeeId.
    [HttpGet("slips/{id:guid}/pdf")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> DownloadSlipPdf(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // Look up the formal payslip record (this is what the UI exposes)
        var payslip = await _db.Payslips.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (payslip is null) return NotFound();

        // Derive the computed PayrollSlip row via RunId + EmployeeId
        var slip = await _db.PayrollSlips.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RunId == payslip.PayrollRunId && x.EmployeeId == payslip.EmployeeId, ct);
        if (slip is null) return NotFound();

        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == payslip.PayrollRunId, ct);

        var company = run?.CompanyId.HasValue == true
            ? await _db.Companies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId!.Value, ct)
            : await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
                .OrderBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(ct);

        var emp = await _db.Employees.AsNoTracking()
            .Select(e => new { e.Id, e.Designation })
            .FirstOrDefaultAsync(e => e.Id == slip.EmployeeId, ct);

        var profile = await _db.EmployeePayrollProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == slip.EmployeeId && !p.IsDeleted, ct);

        // Individual statutory EE deduction lines (GOSI-ANN-EE, GOSI-SANED-EE, etc.)
        var statutoryLines = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == payslip.PayrollRunId
                        && d.EmployeeId == slip.EmployeeId && d.Source == "Statutory")
            .Select(d => new { d.ComponentCode, d.ComponentName, d.Amount })
            .ToListAsync(ct);

        var canSeeSalary = HasPermission("payroll.export");

        // Build items from stored slip fields (no recompute)
        var items = new List<PayslipLineItem>();
        if (slip.BasicSalary > 0)        items.Add(new("Basic Salary",        canSeeSalary ? slip.BasicSalary        : 0m, "Earning"));
        if (slip.HousingAllowance > 0)   items.Add(new("Housing Allowance",   canSeeSalary ? slip.HousingAllowance   : 0m, "Earning"));
        if (slip.TransportAllowance > 0) items.Add(new("Transport Allowance", canSeeSalary ? slip.TransportAllowance : 0m, "Earning"));
        if (slip.OtherAllowances > 0)    items.Add(new("Other Allowances",    canSeeSalary ? slip.OtherAllowances    : 0m, "Earning"));

        foreach (var line in statutoryLines.Where(l => l.ComponentCode.EndsWith("-EE", StringComparison.OrdinalIgnoreCase) && l.Amount > 0))
            items.Add(new(line.ComponentName, canSeeSalary ? line.Amount : 0m, "Deduction"));

        // Non-statutory deductions (loans, advances, fixed)
        var otherDeductions = slip.Deductions - statutoryLines.Where(l => l.ComponentCode.EndsWith("-EE", StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount);
        if (otherDeductions > 0) items.Add(new("Other Deductions", canSeeSalary ? otherDeductions : 0m, "Deduction"));

        items.Add(new("Net Pay", canSeeSalary ? slip.NetSalary : 0m, "Net"));

        // Load the template version that was stamped at generate time, falling back to current default
        var templateId = payslip.PayslipTemplateId
            ?? await _db.PayslipTemplates.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.IsDefault)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(ct);

        PayslipBrandingConfig? branding = null;
        if (templateId.HasValue)
        {
            // IgnoreQueryFilters is intentional: archived payslip templates are soft-deleted but must remain readable for PDF generation.
            var tmpl = await _db.PayslipTemplates.AsNoTracking()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == templateId.Value && t.TenantId == tenantId, ct);
            if (tmpl is not null)
            {
                try { branding = System.Text.Json.JsonSerializer.Deserialize<PayslipBrandingConfig>(tmpl.BrandingJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); } catch { }
                if (branding?.LogoStorageUrl is not null)
                {
                    try
                    {
                        var logoBytes = await _storage.GetBytesAsync(tenantId, branding.LogoStorageUrl, ct);
                        branding = branding with { LogoBytes = logoBytes };
                    }
                    catch { /* non-fatal: logo is optional */ }
                }
            }
        }

        var generatedOn = DateTime.UtcNow;
        var data = new PayslipData(
            PayslipNumber: payslip.PayslipNumber,
            EmployeeCode:  slip.EmployeeCode,
            EmployeeName:  slip.EmployeeName,
            Department:    slip.Department,
            Designation:   emp?.Designation ?? string.Empty,
            PayYear:       run?.Year  ?? generatedOn.Year,
            PayMonth:      run?.Month ?? generatedOn.Month,
            Currency:      profile?.SalaryCurrency ?? company?.DefaultCurrency ?? "SAR",
            Items:         items,
            CompanyName:   company?.LegalNameEn ?? string.Empty,
            CompanyNameAr: company?.LegalNameAr ?? string.Empty,
            GeneratedOn:   generatedOn,
            Branding:      branding
        );

        byte[] pdf;
        try { pdf = await _pdfGate.RenderAsync(() => _letters.GeneratePayslipPdfAsync(data, ct), ct); }
        catch (PdfConcurrencyException ex)
        {
            Response.Headers["Retry-After"] = "10";
            return StatusCode(429, new { error = "pdf_render_busy", message = ex.Message });
        }

        await PayrollAudit("payroll.payslip.download", "Payslip", id.ToString(),
            new { employee = slip.EmployeeCode, period = $"{run?.Year}-{run?.Month:00}" }, ct);
        await _db.SaveChangesAsync(ct);

        return File(pdf, "application/pdf", $"payslip-{slip.EmployeeCode}-{run?.Year}{run?.Month:00}.pdf");
    }

    // ── Bulk payslip PDF bundle (ZIP) ──────────────────────────────────────────
    [HttpGet("runs/{id:guid}/pdf-bundle")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> DownloadRunPdfBundle(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (run is null) return NotFound();

        var payslips = await _db.Payslips.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.PayrollRunId == id)
            .ToListAsync(ct);
        if (payslips.Count == 0)
            return NotFound(new { message = "No payslips generated for this run. Use Generate Payslips first." });

        var slips = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RunId == id)
            .ToListAsync(ct);

        var company = run.CompanyId.HasValue
            ? await _db.Companies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId.Value, ct)
            : await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
                .OrderBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(ct);

        var allStatutory = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == id && d.Source == "Statutory")
            .ToListAsync(ct);

        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .Select(p => new { p.EmployeeId, p.SalaryCurrency })
            .ToListAsync(ct);

        var designations = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Select(e => new { e.Id, e.Designation })
            .ToListAsync(ct);

        var canSeeSalary = HasPermission("payroll.export");
        var generatedOn = DateTime.UtcNow;
        var period = $"{run.Year}{run.Month:00}";

        // Preload all unique template versions referenced by this run's payslips.
        // IgnoreQueryFilters is intentional: archived payslip templates are soft-deleted but must remain readable for bulk PDF export.
        var templateIds = payslips.Where(p => p.PayslipTemplateId.HasValue)
            .Select(p => p.PayslipTemplateId!.Value).Distinct().ToList();
        var templateCache = templateIds.Count > 0
            ? await _db.PayslipTemplates.AsNoTracking().IgnoreQueryFilters()
                .Where(t => templateIds.Contains(t.Id) && t.TenantId == tenantId)
                .ToDictionaryAsync(t => t.Id, ct)
            : new Dictionary<Guid, PayslipTemplate>();

        // Fall back to current default for payslips that have no template stamp
        PayslipTemplate? defaultTmpl = null;
        if (payslips.Any(p => !p.PayslipTemplateId.HasValue))
        {
            defaultTmpl = await _db.PayslipTemplates.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.IsDefault, ct);
        }

        // Pre-load logo bytes for each unique template (one S3/disk fetch per template, not per slip).
        var logoBytesCache = new Dictionary<Guid, byte[]?>();
        foreach (var (tmplId, tmpl) in templateCache)
        {
            try
            {
                var b = System.Text.Json.JsonSerializer.Deserialize<PayslipBrandingConfig>(tmpl.BrandingJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (b?.LogoStorageUrl is not null)
                    logoBytesCache[tmplId] = await _storage.GetBytesAsync(tenantId, b.LogoStorageUrl, ct);
            }
            catch { logoBytesCache[tmplId] = null; }
        }
        byte[]? defaultLogoBytes = null;
        if (defaultTmpl is not null)
        {
            try
            {
                var b = System.Text.Json.JsonSerializer.Deserialize<PayslipBrandingConfig>(defaultTmpl.BrandingJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (b?.LogoStorageUrl is not null)
                    defaultLogoBytes = await _storage.GetBytesAsync(tenantId, b.LogoStorageUrl, ct);
            }
            catch { /* non-fatal */ }
        }

        using var ms = new System.IO.MemoryStream();
        try
        {
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var payslip in payslips)
            {
                var slip = slips.FirstOrDefault(s => s.EmployeeId == payslip.EmployeeId);
                if (slip is null) continue;

                var profile = profiles.FirstOrDefault(p => p.EmployeeId == slip.EmployeeId);
                var desig = designations.FirstOrDefault(e => e.Id == slip.EmployeeId);
                var empStatutory = allStatutory.Where(d => d.EmployeeId == slip.EmployeeId).ToList();

                var items = new List<PayslipLineItem>();
                if (slip.BasicSalary > 0)        items.Add(new("Basic Salary",        canSeeSalary ? slip.BasicSalary        : 0m, "Earning"));
                if (slip.HousingAllowance > 0)   items.Add(new("Housing Allowance",   canSeeSalary ? slip.HousingAllowance   : 0m, "Earning"));
                if (slip.TransportAllowance > 0) items.Add(new("Transport Allowance", canSeeSalary ? slip.TransportAllowance : 0m, "Earning"));
                if (slip.OtherAllowances > 0)    items.Add(new("Other Allowances",    canSeeSalary ? slip.OtherAllowances    : 0m, "Earning"));

                foreach (var line in empStatutory.Where(l => l.ComponentCode.EndsWith("-EE", StringComparison.OrdinalIgnoreCase) && l.Amount > 0))
                    items.Add(new(line.ComponentName, canSeeSalary ? line.Amount : 0m, "Deduction"));

                var otherDed = slip.Deductions - empStatutory.Where(l => l.ComponentCode.EndsWith("-EE", StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount);
                if (otherDed > 0) items.Add(new("Other Deductions", canSeeSalary ? otherDed : 0m, "Deduction"));
                items.Add(new("Net Pay", canSeeSalary ? slip.NetSalary : 0m, "Net"));

                var tmpl = payslip.PayslipTemplateId.HasValue
                    ? templateCache.GetValueOrDefault(payslip.PayslipTemplateId.Value, defaultTmpl!)
                    : defaultTmpl;
                PayslipBrandingConfig? branding = null;
                if (tmpl is not null)
                {
                    try { branding = System.Text.Json.JsonSerializer.Deserialize<PayslipBrandingConfig>(tmpl.BrandingJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); } catch { }
                    if (branding is not null)
                    {
                        var logoBytes = payslip.PayslipTemplateId.HasValue
                            ? logoBytesCache.GetValueOrDefault(payslip.PayslipTemplateId.Value)
                            : defaultLogoBytes;
                        if (logoBytes is { Length: > 0 }) branding = branding with { LogoBytes = logoBytes };
                    }
                }

                var data = new PayslipData(
                    PayslipNumber: payslip.PayslipNumber,
                    EmployeeCode:  slip.EmployeeCode,
                    EmployeeName:  slip.EmployeeName,
                    Department:    slip.Department,
                    Designation:   desig?.Designation ?? string.Empty,
                    PayYear:       run.Year,
                    PayMonth:      run.Month,
                    Currency:      profile?.SalaryCurrency ?? company?.DefaultCurrency ?? "SAR",
                    Items:         items,
                    CompanyName:   company?.LegalNameEn ?? string.Empty,
                    CompanyNameAr: company?.LegalNameAr ?? string.Empty,
                    GeneratedOn:   generatedOn,
                    Branding:      branding
                );

                var pdf = await _pdfGate.RenderAsync(() => _letters.GeneratePayslipPdfAsync(data, ct), ct);
                var entry = zip.CreateEntry($"payslip-{slip.EmployeeCode}-{period}.pdf",
                    System.IO.Compression.CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(pdf, ct);
            }
        }
        }
        catch (PdfConcurrencyException ex)
        {
            Response.Headers["Retry-After"] = "30";
            return StatusCode(429, new { error = "pdf_render_busy", message = ex.Message });
        }

        await PayrollAudit("payroll.payslip.bulk_download", "PayrollRun", id.ToString(),
            new { period, count = payslips.Count }, ct);
        await _db.SaveChangesAsync(ct);

        ms.Position = 0;
        return File(ms.ToArray(), "application/zip", $"payslips-{period}.zip");
    }

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private string GetUserName() => User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "system";
    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
    private static decimal SumOpeningBalance(IEnumerable<PayrollOpeningBalance>? balances, params string[] balanceTypes)
    {
        if (balances is null) return 0m;
        var accepted = balanceTypes.Select(NormalizeCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return balances
            .Where(x => accepted.Contains(NormalizeCode(x.BalanceType)))
            .Sum(x => x.Amount);
    }

    private static string NormalizeCode(string value)
    {
        var chars = (value ?? string.Empty).Trim().ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var code = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(code) ? "PAYROLL_ADJUSTMENT" : code;
    }
    private static string AdjustmentLabel(PayrollAdjustment adjustment)
        => string.IsNullOrWhiteSpace(adjustment.Reason)
            ? $"Payroll adjustment - {adjustment.AdjustmentType}"
            : $"Payroll adjustment - {adjustment.AdjustmentType}: {adjustment.Reason}";
}

// POD-B2 — every new member is optional and defaulted, so the existing frontend call
// (frontend/src/api/payroll.ts createRun) keeps producing a Regular, full-basis, run-period run.
public record CreatePayrollRunRequest(
    int Year,
    int Month,
    Guid? CompanyId = null,
    string? RunType = null,
    Guid? ParentRunId = null,
    bool? IncludesRecurringPay = null,
    string? GlPostingPeriod = null);

/// <summary>POD-B2 — upsert include/exclude intent for a run's population.</summary>
public record PayrollRunSelectionRequest(
    string Mode,
    string Reason,
    List<int>? EmployeeIds = null,
    bool AllEligible = false);
public record SalaryStructureRequest(
    string Code,
    string Name,
    string? Currency,
    DateOnly EffectiveDate,
    IReadOnlyCollection<SalaryComponentRequest>? Components,
    Guid? CompanyId = null,
    bool IsActive = true,
    decimal MinGrossSalary = 0m,
    decimal MaxGrossSalary = 0m,
    decimal MinBasicSalary = 0m,
    decimal MaxBasicSalary = 0m,
    IReadOnlyCollection<Guid>? EligibleGradeIds = null,
    IReadOnlyCollection<Guid>? EligibleDesignationIds = null);
public record SalaryStructureDto(
    Guid Id,
    Guid? CompanyId,
    string? CompanyName,
    string Code,
    string Name,
    string Currency,
    DateOnly EffectiveDate,
    decimal MinGrossSalary,
    decimal MaxGrossSalary,
    decimal MinBasicSalary,
    decimal MaxBasicSalary,
    IReadOnlyCollection<Guid> EligibleGradeIds,
    IReadOnlyCollection<Guid> EligibleDesignationIds,
    int VersionNumber,
    Guid? PreviousVersionId,
    bool IsActive,
    DateTime CreatedAtUtc,
    int AssignedEmployeeCount,
    IReadOnlyList<SalaryComponentDto> Components)
{
    public static SalaryStructureDto Project(SalaryStructure s, string? companyName, IReadOnlyList<SalaryComponentDto> components, int assignedEmployeeCount) =>
        new(s.Id, s.CompanyId, companyName, s.Code, s.Name, s.Currency, s.EffectiveDate,
            s.MinGrossSalary, s.MaxGrossSalary, s.MinBasicSalary, s.MaxBasicSalary,
            ReadGuidList(s.EligibleGradeIdsJson), ReadGuidList(s.EligibleDesignationIdsJson),
            s.VersionNumber, s.PreviousVersionId, s.IsActive, s.CreatedAtUtc, assignedEmployeeCount, components);

    private static IReadOnlyCollection<Guid> ReadGuidList(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new List<Guid>() : JsonSerializer.Deserialize<List<Guid>>(json) ?? new List<Guid>(); }
        catch { return new List<Guid>(); }
    }
}
public record SalaryComponentDto(Guid Id, Guid? SalaryStructureId, string Code, string Name, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable, bool IsActive)
{
    public static SalaryComponentDto Project(SalaryComponent c) =>
        new(c.Id, c.SalaryStructureId, c.Code, c.Name, c.ComponentType, c.CalculationType, c.Amount, c.Percentage, c.IsTaxable, c.IsActive);
}
public record SalaryComponentRequest(string Code, string Name, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable, bool IsActive = true);
public record EmployeeSalaryStructureRequest(int EmployeeId, Guid SalaryStructureId, decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance, decimal FoodAllowance, decimal MobileAllowance, decimal OtherAllowance, decimal FixedDeduction, DateOnly EffectiveDate, string? Currency);
// POD-B2 (M5b) — ExpectedExcludedCount is an ACKNOWLEDGEMENT, not a warning. Approve refuses with 409
// when the run holds deliberate exclusions and the approver's expectation does not match the resolved
// count, so the person signing off cannot miss a hold-out buried among dozens of routine warnings.
// Optional and defaulted, so every existing caller (and the frontend) is unaffected on a run with none.
public record PayrollDecisionRequest(string? Notes, int? ExpectedExcludedCount = null);
public record PayrollPaymentBatchRequest(string? PaymentMethod, string? Currency);
public record WpsStatusRequest(string Status, string? Notes, string? Reference = null);
public record ErpPostingStatusRequest(string Status, string? Reference = null, string? Notes = null);
// POD-B1 — settlement / remittance / reversal request bodies.
public record SettlePaymentBatchRequest(string? Reference = null, DateOnly? PaidDate = null);
public record RemitStatutoryRequest(string? Group = null, string? Reference = null, DateOnly? RemitDate = null);
public record PayrollReasonRequest(string? Reason = null);
public record RemitReverseRequest(string? Group = null, string? Reason = null);
public record PayrollGroupRequest(string Code, string Name, string? Currency);
public record ImportSalaryStructuresRequest(string CsvContent);
public record EosbCalculationRequest(int EmployeeId, DateTime? AsOfDate, string? TerminationReason = null);
public record FinalSettlementRequest(int EmployeeId, DateOnly LastWorkingDay, int NoticePeriodDaysShort = 0, string? TerminationReason = null);
