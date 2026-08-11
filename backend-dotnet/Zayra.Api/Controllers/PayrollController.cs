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
    // POD-C3 — the proration/arrears policy chain (company row → tenant default → compiled default) and
    // the working-week resolver the WorkingDays basis needs. Optional so every existing test constructor
    // keeps compiling; DI always supplies the real ones.
    private readonly IProrationPolicyResolver _prorationPolicy;
    private readonly Zayra.Api.Application.WorkWeek.IWorkWeekService _workWeek;

    public PayrollController(ZayraDbContext db, IDataScopeService scopeService, IHttpContextAccessor http,
        INotificationService notifications, ICountryPackResolver packResolver, IStatutoryRuleReader ruleReader,
        ILetterService letters, IDocumentStorage storage, PdfRenderGate pdfGate,
        ICompanyTaxPolicyResolver? taxResolver = null,
        Zayra.Api.Infrastructure.Employees.IEmployeeActivationGuard? activationGuard = null,
        IProrationPolicyResolver? prorationPolicy = null,
        Zayra.Api.Application.WorkWeek.IWorkWeekService? workWeek = null)
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
        _prorationPolicy = prorationPolicy ?? new ProrationPolicyResolver(new CompanyRatePolicyResolver(db));
        _workWeek = workWeek ?? new Zayra.Api.Infrastructure.WorkWeek.WorkWeekService(db);
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

        // ── POD-C1: a SETTLEMENT-PURPOSE run ─────────────────────────────────────────────────────
        // The purpose is carried on the run rather than inferred, for two reasons that both bite.
        //   (a) IT OPENS THE RAIL. A leaver is Offboarded/Terminated and already has a non-voided
        //       IsFinalWageMonth slip, so all three gates in LoadEligibleWithLeaversAsync exclude them
        //       from every subsequent run, and ResolveRunPopulationAsync intersects the selector WITH the
        //       eligible set — so an Include row for them is stamped NotEligible and paid nothing. Without
        //       an explicit purpose an approved settlement would accrue to 2320 and never reach anybody.
        //   (b) IT GIVES GUARD (c) A WALL. An OffCycle run may legitimately pay full recurring salary (the
        //       missed-joiner case). Attaching a settlement to such a run in a month AFTER the last
        //       working day is a live double-pay: priorStatutoryByEmp is empty, fullBasic is NOT zeroed
        //       (Process only zeroes it when includesRecurringPay is false), ProrationCalculator leaves
        //       paidTo = periodEnd because the offboarding is outside the period, and the leaver draws a
        //       whole extra month's salary AND a whole extra month's GOSI. Refused outright.
        var settlesFinalSettlements = req.SettlesFinalSettlements ?? false;
        if (settlesFinalSettlements)
        {
            if (PayrollRunTypes.IsPeriodOwning(runType))
                return BadRequest(new
                {
                    error   = "settlement_run_type_invalid",
                    message = "A termination settlement is disbursed OUT OF BAND. Use an OffCycle (or " +
                              "Supplementary) run — a Regular/Replacement run OWNS the month's recurring " +
                              "payroll and would pay the leaver a second full salary.",
                    runType,
                });
            if (includesRecurringPay)
                return UnprocessableEntity(new
                {
                    error   = "settlement_run_pays_recurring",
                    message = "A settlement run must NOT pay recurring salary (includesRecurringPay=false). " +
                              "The leaver's wages through their last working day were already paid by the " +
                              "run that produced their final-wage-month payslip (POD-C3); paying them again " +
                              "here would double the wage and charge a second full month of social insurance.",
                });
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
        // One non-voided PERIOD-OWNING run per legal entity per period. Multi-entity tenants must process
        // each company separately so statutory country packs, employees, impacts, GL and WPS remain
        // scoped. OffCycle/Supplementary/Correction runs are unconstrained — any number may coexist.
        //
        // POD-B3 — the check covers { Regular, Replacement }. A Replacement IS the month; two live ones,
        // or a Replacement alongside a live Regular, would accrue the period twice. The two partial unique
        // indexes carry the same widened predicate, so the database refuses it even if this check is
        // bypassed (a raw SaveChanges, a future code path).
        if (PayrollRunTypes.IsPeriodOwning(runType))
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
                         && (r.RunType == PayrollRunTypes.Regular || r.RunType == PayrollRunTypes.Replacement)
                         && r.Status != "Voided")
                .FirstOrDefaultAsync(cancellationToken);
            // POD-B3 — when the run in the way IS the run this Replacement names, the precise diagnosis is
            // "you have not voided it yet", not a generic period conflict. The 409 is technically true but
            // sends the operator looking for a second run that does not exist.
            if (existing is not null && runType == PayrollRunTypes.Replacement && req.ParentRunId == existing.Id)
                return BadRequest(new
                {
                    error   = "parent_not_voided",
                    message = $"A Replacement may only replace a VOIDED run (it is currently '{existing.Status}'). " +
                              "Void it first (POST runs/{id}/void) so its ledger, payments and consumed balances are " +
                              "unwound; otherwise the month would be accrued and paid twice.",
                    parentStatus = existing.Status,
                    runId        = existing.Id,
                });
            if (existing is not null)
                return Conflict(new
                {
                    error   = "regular_run_exists",
                    message = $"A {existing.RunType.ToLowerInvariant()} payroll run for {runCompany.LegalNameEn} " +
                              $"{req.Year}/{req.Month:D2} already exists. " +
                              "Create an OffCycle/Supplementary/Correction run for an additional payment in this period, " +
                              "or void the existing run first.",
                    runId   = existing.Id,
                    runType = existing.RunType,
                });
        }

        // ── POD-B2: parent-run link ──────────────────────────────────────────────────────────────
        if (req.ParentRunId.HasValue && runType == PayrollRunTypes.Regular)
            return BadRequest(new { error = "parent_not_allowed", message = "A Regular run cannot amend another run; parentRunId is only valid for Correction/Supplementary/OffCycle/Replacement runs." });
        if (runType == PayrollRunTypes.Correction && !req.ParentRunId.HasValue)
            return BadRequest(new { error = "parent_required", message = "A Correction run must name the run it amends via parentRunId." });
        // ── POD-B3: a Replacement must name the VOIDED run it replaces ───────────────────────────
        // The ordering is the contract, not paperwork. Requiring the parent to be voided FIRST means:
        //   • recovery is two separately-audited phases (unwind, then re-post) rather than one opaque act;
        //   • the period's unique index has a free slot, because it is filtered on status != 'Voided';
        //   • ALREADY_PAID_THIS_PERIOD cannot fire against the run being replaced, because
        //     LoadSiblingRunsAsync excludes voided runs — so the replacement is not born stuck.
        if (runType == PayrollRunTypes.Replacement && !req.ParentRunId.HasValue)
            return BadRequest(new
            {
                error   = "parent_required",
                message = "A Replacement run must name the VOIDED run it replaces via parentRunId. " +
                          "Void the bad run first (POST runs/{id}/void) — that is what unwinds its ledger, " +
                          "payments and consumed balances; the replacement then re-posts a clean month.",
            });
        PayrollRun? parentRun = null;
        if (req.ParentRunId.HasValue)
        {
            var parent = await _db.PayrollRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == req.ParentRunId.Value, cancellationToken);
            if (parent is null)
                return BadRequest(new { error = "parent_not_found", message = "parentRunId does not identify a payroll run in this tenant." });
            if (parent.CompanyId != runCompany.Id)
                return BadRequest(new { error = "parent_company_mismatch", message = "The parent run belongs to a different legal entity." });
            if (runType == PayrollRunTypes.Replacement)
            {
                // Type-conditional CARVE-OUT of the parent_voided rule, never a relaxation of it: an
                // AMENDING run (Correction/Supplementary) still may not hang off a voided parent, because
                // it would be counting against a month that no longer exists.
                if (parent.Status != "Voided")
                    return BadRequest(new
                    {
                        error   = "parent_not_voided",
                        message = $"A Replacement may only replace a VOIDED run (parent is '{parent.Status}'). " +
                                  "Void it first so its ledger, payments and consumed balances are unwound; " +
                                  "otherwise the month would be accrued and paid twice.",
                        parentStatus = parent.Status,
                    });
                if (parent.Year != req.Year || parent.Month != req.Month)
                    return BadRequest(new
                    {
                        error   = "parent_period_mismatch",
                        message = $"A Replacement must cover the SAME pay period as the run it replaces " +
                                  $"({parent.Year}-{parent.Month:D2}). Use a Correction/Supplementary run to " +
                                  "settle a difference in a later period.",
                        parentPeriod = $"{parent.Year}-{parent.Month:D2}",
                    });
                // The parent's own amending children must already be gone, or a live Correction would hang
                // off a voided grandparent and double-count against this replacement. Void's cascade is
                // what clears them.
                var liveChildren = await _db.PayrollRuns.AsNoTracking()
                    .Where(r => r.TenantId == tenantId && r.ParentRunId == parent.Id && r.Status != "Voided")
                    .Select(r => r.Id)
                    .ToListAsync(cancellationToken);
                if (liveChildren.Count > 0)
                    return Conflict(new
                    {
                        error   = "parent_has_live_children",
                        message = $"{liveChildren.Count} non-voided run(s) still amend the run being replaced. " +
                                  "Void them (POST runs/{id}/void with cascade=true on the parent) before creating a replacement.",
                        childRunIds = liveChildren,
                    });
            }
            else
            {
                if (parent.Status == "Voided")
                    return BadRequest(new { error = "parent_voided", message = "A voided run cannot be amended. Create a Replacement run (runType='Replacement') for the period instead." });
                if (parent.Status == "Draft")
                    return BadRequest(new { error = "parent_not_processed", message = "A Draft run has produced nothing to amend. Process the parent run first." });
            }
            // POD-B3 — cycle / depth guard. ParentRunId has no FK and nothing stopped a chain looping back
            // on itself (A→B→A), which would hang every recursive walk: the void cascade, this check, and
            // the recovery-chain view. Walk to the root with a hard cap.
            var seen = new HashSet<Guid> { parent.Id };
            var cursor = parent.ParentRunId;
            var depth = 0;
            while (cursor is Guid ancestorId)
            {
                if (!seen.Add(ancestorId) || ++depth > 10)
                    return BadRequest(new
                    {
                        error   = "parent_chain_invalid",
                        message = "The parent run chain is cyclic or deeper than 10 links. Recovery chains must be " +
                                  "walkable; break the chain before linking another run to it.",
                    });
                cursor = await _db.PayrollRuns.AsNoTracking()
                    .Where(r => r.TenantId == tenantId && r.Id == ancestorId)
                    .Select(r => r.ParentRunId)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            parentRun = parent;
            // Period is DELIBERATELY not constrained for the AMENDING types: a correction booked in M+1
            // for M is normal payroll practice. Only the LINK is POD-B2; the retro/arrears math is POD-C3.
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
            // POD-C3 — the retro/arrears math CreateRun's comment above has pointed at since B2.
            SettlesArrears = req.SettlesArrears ?? PayrollRunTypes.IsPeriodOwning(runType),
            // POD-C1 — a settlement run defaults to NETTING the leaver's outstanding receivable: a leaver's
            // debts are settled with them, and this is the LAST payment they will receive. It is still an
            // explicit flag the operator can turn off, so the C3 doctrine ("recovering an overpayment is an
            // act you choose") holds; only the default differs, and only for a settlement run.
            NetsPriorReceivable = req.NetsPriorReceivable ?? settlesFinalSettlements,
            SettlesFinalSettlements = settlesFinalSettlements,
            CreatedByUserId = GetUserId(),
        };
        _db.PayrollRuns.Add(run);

        // ── POD-B3: a Replacement INHERITS the voided run's paid population ───────────────────────
        // Materialised as explicit Include rows rather than left implicit, so a deliberate hold-out
        // survives the recovery instead of being silently re-included by "everyone eligible", and so the
        // population of the replacement is as auditable as the population of the run it replaces. The run
        // is Draft, so the operator can still edit the selector before processing.
        var inheritedPopulation = 0;
        if (runType == PayrollRunTypes.Replacement && parentRun is not null)
        {
            var parentPaidEmployeeIds = await _db.PayrollSlips.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.RunId == parentRun.Id)
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var empId in parentPaidEmployeeIds)
            {
                _db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
                {
                    TenantId      = tenantId,
                    CompanyId     = runCompany.Id,
                    PayrollRunId  = run.Id,
                    EmployeeId    = empId,
                    Mode          = PayrollRunSelectionModes.Include,
                    Reason        = $"Inherited from replaced run {parentRun.Id} ({parentRun.Year}-{parentRun.Month:D2}).",
                    CreatedByUserId = GetUserId(),
                    CreatedByName = GetUserName(),
                });
            }
            inheritedPopulation = parentPaidEmployeeIds.Count;
        }

        // CreateRun wrote no audit row before B2. A run of a non-default TYPE or BASIS is an operator
        // decision with financial consequences and must be attributable. Routed through the standard
        // helper so POD-A3's sealer stamps Seq/PreviousHash/EntryHash on the business SaveChanges.
        await PayrollAudit("payroll.run.created", "PayrollRun", run.Id.ToString(), new
        {
            runType,
            parentRunId          = req.ParentRunId,
            includesRecurringPay = includesRecurringPay,
            settlesFinalSettlements,
            glPostingPeriod,
            year                 = req.Year,
            month                = req.Month,
            companyId            = runCompany.Id,
            // POD-B3 — recovery provenance.
            replacesRunId        = runType == PayrollRunTypes.Replacement ? req.ParentRunId : null,
            inheritedPopulation,
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
        Guid tenantId, Guid companyId, bool allowLegacyUnscopedEmployees, bool asNoTracking, CancellationToken ct,
        bool includeSettlementLeavers = false)
        => (await LoadEligibleWithLeaversAsync(tenantId, companyId, allowLegacyUnscopedEmployees, asNoTracking,
                periodStart: null, periodEnd: null, excludeRunId: null, ct,
                includeSettlementLeavers)).Employees;

    /// <summary>
    /// POD-C3 — the SAME eligible query as above, UNIONED with this period's LEAVERS.
    ///
    /// <para><b>THE LIVE DEFECT THIS FIXES.</b> <c>OffboardingController</c> sets
    /// <c>Employee.Status = "Offboarded"</c> at NOTICE time, and the query above filters
    /// <c>Status == "Active"</c>. So from the day a resignation was keyed, the employee vanished from
    /// every payroll run and was paid NOTHING for the whole notice period — directly contradicting the
    /// product's own documented intent ("Excludes Offboarded — notice-period staff may still be paid",
    /// EmployeesController). Payroll never honoured it. A notice served in August with a last working day
    /// of 15 October now yields full August, full September, 15/30 October — then nothing.</para>
    ///
    /// <para><b>THE UNION IS NARROW ON PURPOSE.</b> The Active set is kept VERBATIM and only added to, so
    /// for any tenant with no offboarding in flight the population is bit-for-bit what it was. The union
    /// re-applies the run's COMPANY scope from <c>Employee.CompanyId</c> (EmployeeOffboarding is
    /// tenant-owned only and carries no company dimension, so without this a group tenant's Company A run
    /// would pull in Company B's leaver), and it STOPS once the final wage month has been paid: an
    /// employee who already has a non-voided <c>IsFinalWageMonth</c> slip is excluded, which is what stops
    /// a settled leaver drawing a second wage through the IsActive-blind salary query.</para>
    /// </summary>
    private async Task<(List<Employee> Employees, HashSet<int> LeaverIds, Dictionary<int, DateOnly> LastWorkingDays,
                        List<(Employee Employee, string Reason)> UnpayableNonActive, HashSet<int> SettlementIds)>
        LoadEligibleWithLeaversAsync(
            Guid tenantId, Guid companyId, bool allowLegacyUnscopedEmployees, bool asNoTracking,
            DateOnly? periodStart, DateOnly? periodEnd, Guid? excludeRunId, CancellationToken ct,
            bool includeSettlementLeavers = false)
    {
        var q = _db.Employees.Where(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted
            && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)));
        if (asNoTracking) q = q.AsNoTracking();
        var active = await q.ToListAsync(ct);

        var leaverIds = new HashSet<int>();
        var lwdByEmp = new Dictionary<int, DateOnly>();
        var unpayable = new List<(Employee, string)>();
        // ── POD-C1: THE DISBURSEMENT RAIL ────────────────────────────────────────────────────────────
        // A settlement-purpose run pays people the three gates below deliberately exclude, and it must:
        //   • `Status == "Active"` — a leaver past their last working day is Offboarded/Terminated/Exited;
        //   • `LastWorkingDay >= periodStart` — a settlement is normally disbursed in the month AFTER the
        //     LWD, so the leaver union never even considers them;
        //   • `alreadyFinalised` — the non-voided IsFinalWageMonth slip that Guard 4 REQUIRES before a
        //     settlement may be approved is the very thing that removes them from every later run.
        // Without this branch a settlement could accrue to 2320 and never reach anybody: the payable would
        // age forever and the leaver would still have to be paid by manual bank transfer — i.e. the exact
        // problem this pod exists to end. It adds ONLY employees with an Approved settlement and no live
        // disbursement, so for every run that is not a settlement run this is not even queried.
        var settlementIds = new HashSet<int>();
        if (includeSettlementLeavers)
        {
            var activeIds = active.Select(e => e.Id).ToHashSet();
            var awaitingIds = await _db.EmployeeFinalSettlements.AsNoTracking()
                .Where(s => s.TenantId == tenantId
                         && s.Status == FinalSettlementStatuses.Approved
                         && s.PayrollRunId == null
                         && (s.CompanyId == companyId || s.CompanyId == null))
                .Select(s => s.EmployeeId)
                .Distinct()
                .ToListAsync(ct);
            var toAdd = awaitingIds.Where(eid => !activeIds.Contains(eid)).ToList();
            if (toAdd.Count > 0)
            {
                // COMPANY SCOPE re-applied from the employee, exactly as the Active and leaver queries do.
                var settlementQ = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted
                    && toAdd.Contains(e.Id)
                    && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)));
                if (asNoTracking) settlementQ = settlementQ.AsNoTracking();
                foreach (var e in await settlementQ.ToListAsync(ct))
                {
                    active.Add(e);
                    settlementIds.Add(e.Id);
                }
            }
            foreach (var eid in awaitingIds.Where(activeIds.Contains)) settlementIds.Add(eid);
        }
        if (periodStart is not DateOnly pStart || periodEnd is not DateOnly pEnd)
            return (active, leaverIds, lwdByEmp, unpayable, settlementIds);

        // Non-cancelled offboardings whose last working day lands on/after this period's start. The
        // newest record per employee wins (a re-hire may have an older, completed one).
        var offboardings = await _db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Status != "Cancelled" && o.LastWorkingDay >= pStart)
            .Select(o => new { o.EmployeeId, o.LastWorkingDay, o.CreatedAtUtc })
            .ToListAsync(ct);
        if (offboardings.Count == 0) return (active, leaverIds, lwdByEmp, unpayable, settlementIds);

        foreach (var g in offboardings.GroupBy(o => o.EmployeeId))
            lwdByEmp[g.Key] = g.OrderByDescending(o => o.CreatedAtUtc).First().LastWorkingDay;

        var candidateIds = lwdByEmp.Keys.ToList();
        // COMPANY SCOPE re-applied from the employee, exactly as the Active query does.
        var leaverQ = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted
            && candidateIds.Contains(e.Id) && e.Status != "Active"
            && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)));
        if (asNoTracking) leaverQ = leaverQ.AsNoTracking();
        var leavers = await leaverQ.ToListAsync(ct);
        if (leavers.Count == 0) return (active, leaverIds, lwdByEmp, unpayable, settlementIds);

        // STOP CONDITION — the final wage month is paid exactly ONCE. Without it, every subsequent
        // period where periodStart <= LWD would match again and an IsActive-blind salary query would draw
        // a second (and third) final wage.
        var leaverIdList = leavers.Select(e => e.Id).ToList();
        var alreadyFinalised = (await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsFinalWageMonth && s.Status != "Voided"
                     && leaverIdList.Contains(s.EmployeeId)
                     && (excludeRunId == null || s.RunId != excludeRunId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();

        foreach (var e in leavers)
        {
            if (alreadyFinalised.Contains(e.Id)) continue;
            // POD-C1 — already admitted by the settlement branch above; adding them twice would produce
            // two identical Employee entries and therefore two payslips for one person.
            if (settlementIds.Contains(e.Id)) continue;
            var lwd = lwdByEmp[e.Id];
            var joined = e.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(e.JoiningDate);
            if (joined is DateOnly jd && jd > pEnd) continue;   // not employed in this period at all
            if (lwd < pStart) continue;                          // already left before the period
            active.Add(e);
            leaverIds.Add(e.Id);
        }

        // MF-9(c) — a NON-ACTIVE employee with NO last working day anywhere is paid nothing and nothing
        // reports it: the same silent-unpay defect re-entering through a different door. Named, capped.
        var separationStatuses = new[] { EmployeeStatuses.Offboarded, EmployeeStatuses.Terminated, EmployeeStatuses.Exited };
        var orphanQ = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && separationStatuses.Contains(e.Status)
                     && !candidateIds.Contains(e.Id)
                     && (e.CompanyId == companyId || (allowLegacyUnscopedEmployees && e.CompanyId == null)))
            .OrderBy(e => e.Id)
            .Take(25)
            .ToListAsync(ct);
        // POD-C1 — a leaver being SETTLED by this run is not an "unpayable orphan": their offboarding is
        // simply outside this period's window (a settlement is normally disbursed after the LWD month).
        foreach (var e in orphanQ.Where(e => !settlementIds.Contains(e.Id)))
            unpayable.Add((e, $"Status '{e.Status}' with no offboarding record, so no last working day exists. " +
                               "They are excluded from this run and will be paid nothing."));

        return (active, leaverIds, lwdByEmp, unpayable, settlementIds);
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

        // POD-C1 — a settlement run's eligible set INCLUDES its approved leavers, or the selector would
        // stamp them NotEligible and the run would pay nobody (ResolveRunPopulationAsync intersects
        // Include rows WITH this set).
        var eligible = await LoadEligibleEmployeesAsync(tenantId, company.Id, allowLegacyUnscoped, asNoTracking: true, cancellationToken,
            includeSettlementLeavers: run.SettlesFinalSettlements);
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

        // POD-C1 — a settlement run's eligible set INCLUDES its approved leavers, or the selector would
        // stamp them NotEligible and the run would pay nobody (ResolveRunPopulationAsync intersects
        // Include rows WITH this set).
        var eligible = await LoadEligibleEmployeesAsync(tenantId, company.Id, allowLegacyUnscoped, asNoTracking: true, cancellationToken,
            includeSettlementLeavers: run.SettlesFinalSettlements);
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
        // POD-B3 — the check covers both PERIOD-OWNING types (Regular, Replacement); see CreateRun.
        if (run.CompanyId is null && PayrollRunTypes.IsPeriodOwning(run.RunType))
        {
            var competing = await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Id != run.Id
                         && r.Year == run.Year && r.Month == run.Month
                         && r.CompanyId == company.Id
                         && (r.RunType == PayrollRunTypes.Regular || r.RunType == PayrollRunTypes.Replacement)
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

        // ── POD-C3: the NAMED, per-company proration policy for this run ─────────────────────────────
        // Resolved BEFORE any work is done and refused loudly on a bad value: a silent fallback here is
        // exactly how a tenant ends up paying joiners and leavers on a basis nobody chose.
        var workWeekConfig = await _workWeek.ResolveAsync(tenantId, company.Id, company.CountryCode, cancellationToken);
        var (prorationPolicy, prorationPolicyError) =
            await _prorationPolicy.ResolveAsync(tenantId, company.Id, periodEnd, workWeekConfig, cancellationToken);
        if (prorationPolicyError is not null)
            return UnprocessableEntity(new
            {
                error   = prorationPolicyError.Code,
                message = prorationPolicyError.Message,
                detail  = prorationPolicyError.Detail,
            });
        var policy = prorationPolicy!;

        // POD-C3 — the eligible set is now the Active set UNIONED with this period's leavers. The Active
        // half is verbatim, so a tenant with no offboarding in flight has a bit-for-bit identical
        // population. See LoadEligibleWithLeaversAsync for the silent-unpay defect this closes.
        // POD-C1 — a settlement-purpose run additionally admits leavers whose wage side is already DONE
        // (see the settlement branch in LoadEligibleWithLeaversAsync); no other run type is affected.
        var (eligibleEmployees, leaverEmployeeIds, lastWorkingDayByEmp, unpayableNonActive, settlementEligibleIds) =
            await LoadEligibleWithLeaversAsync(tenantId, company.Id, allowLegacyUnscopedEmployees,
                asNoTracking: false, periodStart, periodEnd, excludeRunId: id, cancellationToken,
                includeSettlementLeavers: run.SettlesFinalSettlements);

        // ── POD-B2: the operator's include/exclude intent decides who this run pays ──────────────────
        var runPopulation = await ResolveRunPopulationAsync(tenantId, run, eligibleEmployees, cancellationToken);

        // A non-Regular run over the WHOLE company is almost always operator error: per the supplemental
        // rules below it would consume the period's bonuses and adjustments out from under the Regular
        // run. Require the population to be stated explicitly. POST runs/{id}/selection with
        // allEligible=true materialises "everyone" in one call and still records who was intended.
        // POD-B3 — the gate applies to the SUPPLEMENTAL types only. A Replacement is period-OWNING (it IS
        // the month), so "everyone eligible" is the correct default exactly as it is for a Regular run;
        // requiring an explicit selector would make recovery of a month whose parent produced no slips
        // impossible. CreateRun still materialises the replaced run's paid population as Include rows when
        // there is one, so a hold-out survives recovery.
        if (PayrollRunTypes.RequiresExplicitPopulation(run.RunType) && runPopulation.Mode != "AllowList")
            return UnprocessableEntity(new
            {
                error   = "run_population_required",
                message = $"A '{run.RunType}' run must state its population explicitly. POST /api/payroll/runs/{run.Id}/selection " +
                          "with mode='Include' and the employee ids (or allEligible=true for the whole entity) before processing.",
                runType = run.RunType,
            });

        var employees = runPopulation.Employees;

        // ── POD-C3: EACH EMPLOYEE'S EMPLOYMENT WINDOW INSIDE THIS PERIOD ─────────────────────────────
        // Computed BEFORE employeeIdsForRun is built, because an employee who had not joined yet is
        // EXCLUDED from the run entirely — a zero payslip and an empty GL group are not a truthful
        // representation of "not employed", and marking their attendance/leave impacts Processed would
        // starve the run that finally does pay them.
        //
        // For an employee with JoiningDate before the period and no offboarding the factor is exactly
        // 1.0 and every emitted line below is byte-identical to pre-C3. That is the ~55-tenant bar.
        var prorationByEmp = new Dictionary<int, ProrationResult>();
        foreach (var e in employees)
        {
            var joined = e.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(e.JoiningDate);
            lastWorkingDayByEmp.TryGetValue(e.Id, out var lwdValue);
            var lwd = lastWorkingDayByEmp.ContainsKey(e.Id) ? lwdValue : (DateOnly?)null;
            prorationByEmp[e.Id] = ProrationCalculator.Compute(periodStart, periodEnd, joined, lwd, policy.Basis, workWeekConfig);
        }
        var excludedJoiners = employees.Where(e => prorationByEmp[e.Id].IsExcluded).ToList();
        if (excludedJoiners.Count > 0)
        {
            // Reported, never silent — an unexplained absence from a run is exactly how the notice-period
            // unpay defect hid for so long.
            foreach (var e in excludedJoiners)
            {
                var pr = prorationByEmp[e.Id];
                // POD-C3-FIX — the reason must name the ACTUAL cause. The old text branched on
                // `pr.PaidTo < pr.PaidFrom`, which is ALSO true for a post-period joiner
                // (ProrationCalculator.cs:192-193 leaves paidTo = periodEnd when there is no last working
                // day, so paidFrom = the joining date is already past it). The post-period branch was
                // therefore unreachable, and operators were told "last day 2026-06-30" — the period end,
                // dressed up as a real last working day — about someone who had simply not started yet.
                var joinedOn = e.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(e.JoiningDate);
                var reason = joinedOn is DateOnly jd && jd > periodEnd
                    ? $"Employment window is empty for {run.Year}-{run.Month:D2}: joins {jd:yyyy-MM-dd}, which is after " +
                      $"this period ends ({periodEnd:yyyy-MM-dd}). Nothing is owed for {run.Year}-{run.Month:D2}."
                    : $"Employment window is empty for {run.Year}-{run.Month:D2}: the last working day " +
                      $"({pr.PaidTo:yyyy-MM-dd}) precedes the first day of employment in this period " +
                      $"({pr.PaidFrom:yyyy-MM-dd}). Check the joining date and the offboarding record.";
                runPopulation.NotEligible.Add(new PayrollRunExclusion(e.Id, e.EmployeeCode, e.FullName, reason));
                employees.Remove(e);
            }
        }

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

        // ── POD-C3: a LEAVER's salary row survives the offboarding exit cascade ───────────────────────
        // OffboardingController.Complete → EmployeeManagementService.DeactivatePayrollFootprintAsync sets
        // EmployeeSalaryStructure.IsActive = false once FinalSettlementDone. The query above filters
        // x.IsActive, so a leaver re-included by the union would silently fall back to `e.Salary ?? 0m`
        // and be paid a wrong (usually zero) final wage. A SECOND query, applied ONLY to the leaver set,
        // fixes that without touching the primary query for anyone else.
        var leaverIdsForRun = leaverEmployeeIds.Where(employeeIdsForRun.Contains).ToList();
        if (leaverIdsForRun.Count > 0)
        {
            var knownAssignmentIds = salaryAssignments.Select(a => a.Id).ToHashSet();
            var leaverAssignments = await _db.EmployeeSalaryStructures.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.EffectiveDate <= periodEnd && leaverIdsForRun.Contains(x.EmployeeId))
                .ToListAsync(cancellationToken);
            salaryAssignments.AddRange(leaverAssignments.Where(a => !knownAssignmentIds.Contains(a.Id)));
        }

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
                .Select(s => new
                {
                    s.EmployeeId, s.BasicSalary, s.HousingAllowance,
                    // POD-C3 — the sibling's own statutory base, which under `proration_gosi_base =
                    // FullMonth` is NOT its money columns. Null on every pre-C3 slip, so the fallback
                    // below reproduces the pre-C3 read exactly.
                    s.GosiBasePolicy, s.FullBasicSalary, s.FullHousingAllowance,
                })
                .ToListAsync(cancellationToken);
            foreach (var s in siblingSlips)
            {
                var useFull = s.GosiBasePolicy == ProrationGosiBases.FullMonth;
                var sBasic   = useFull ? s.FullBasicSalary      ?? s.BasicSalary      : s.BasicSalary;
                var sHousing = useFull ? s.FullHousingAllowance ?? s.HousingAllowance : s.HousingAllowance;
                priorBasicByEmp[s.EmployeeId]   = priorBasicByEmp.GetValueOrDefault(s.EmployeeId) + sBasic;
                priorHousingByEmp[s.EmployeeId] = priorHousingByEmp.GetValueOrDefault(s.EmployeeId) + sHousing;
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
            // ── POD-C3 (MF-1) — GOSI-BEARING ARREARS A SIBLING RUN ALREADY SETTLED ───────────────────
            // This is the single most dangerous omission a proration/arrears pod can make. Arrears may be
            // settled by a Supplementary/OffCycle run (B2's natural vehicle for a retro increment), and
            // the A1 period reconciliation is taught to include them. If they are NOT also added to the
            // period-to-date base here, the pack is fed an understated base, the 45,000 ceiling nets
            // wrong, the incremental netting below is wrong — and ReconcilePeriodAsync then computes
            // `expected` on base+arrears against an `actual` computed WITHOUT them. `expected == actual`
            // fails for every affected employee, silently, on a STATUTORY FILING source. Mirrors the
            // sibling-bonus block above exactly, and rides the SAME housing slot.
            var siblingArrears = await _db.PayrollArrearsLines.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.Status == PayrollArrearsStatuses.Settled && a.IsGosiBearing
                         && a.PayrollRunId != null && siblingRunIds.Contains(a.PayrollRunId!.Value)
                         && employeeIdsForRun.Contains(a.EmployeeId))
                .Select(a => new { a.EmployeeId, a.Amount })
                .ToListAsync(cancellationToken);
            foreach (var a in siblingArrears)
                priorHousingByEmp[a.EmployeeId] = priorHousingByEmp.GetValueOrDefault(a.EmployeeId) + a.Amount;
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

        // ════ POD-C3 ══════════════════════════════════════════════════════════════════════════════
        // Everything from here to the transaction is INERT for a run with no joiner, no leaver and no
        // backdated salary change: prorationByEmp is all 1.0, the arrears engine returns zero lines, and
        // every warning list is empty.
        // ══════════════════════════════════════════════════════════════════════════════════════════

        // Statutory covered-wage ceiling — used ONLY for the arrears EARNED-BASIS delta, never to
        // duplicate the pack's own capping (which stays inside KsaDeductionCalculator).
        var statutoryCeiling = await _ruleReader.GetDecimalAsync(
            packCc, packJur, "gosi.covered_wage_ceiling_sar", eff, tenantId, cancellationToken) ?? decimal.MaxValue;

        var c3Warnings = new List<(string Code, int? EmployeeId, string Message)>();
        if (!policy.IsEnabled)
            c3Warnings.Add(("WARN_PRORATION_DISABLED", null,
                "Mid-month proration is DISABLED for this legal entity (payparameter.proration_basis = 'None'). " +
                "A joiner or leaver is being paid a FULL month. This warning fires on every run so the choice " +
                "can never be silently forgotten."));
        if (policy.Basis == ProrationBases.Calendar30 && lopDayDivisor != ProrationCalculator.Calendar30Days)
            c3Warnings.Add(("WARN_PRORATION_BASIS_MISMATCH", null,
                $"Proration uses a 30-day month but unpaid absence is charged at basic ÷ {lopDayDivisor} " +
                "(lop.monthly_day_divisor). Two different day-rates on one payslip: a joiner's absent day is " +
                "valued differently from their unworked days. Align the two before filing."));

        // ── MF-6(b): unpaid-leave days RECOMPUTED against the employment window ──────────────────────
        // LeavePayrollImpact carries NO dates (only PayPeriod/Days/Amount) and its Amount was SNAPSHOTTED
        // at approval on the FULL basic ÷ 30. For a prorated joiner/leaver that snapshot can exceed the
        // wage actually earned. The originating LeaveRequest does carry dates, so the impact is scaled by
        // the share of the request that falls inside [PaidFrom, PaidTo]. Scale is 1.0 — and the code path
        // therefore a no-op — for every employee who was employed for the whole period.
        var leaveImpactScale = new Dictionary<Guid, decimal>();
        var proratedEmpIds = employees.Where(e => prorationByEmp[e.Id].IsProrated).Select(e => e.Id).ToHashSet();
        if (proratedEmpIds.Count > 0 && leaveImpacts.Count > 0)
        {
            var reqIds = leaveImpacts.Where(x => proratedEmpIds.Contains(x.EmployeeId)).Select(x => x.LeaveRequestId).Distinct().ToList();
            var reqs = reqIds.Count == 0 ? new List<LeaveRequest>() : await _db.LeaveRequests.AsNoTracking()
                .Where(r => r.TenantId == tenantId && reqIds.Contains(r.Id)).ToListAsync(cancellationToken);
            foreach (var imp in leaveImpacts.Where(x => proratedEmpIds.Contains(x.EmployeeId)))
            {
                var req = reqs.FirstOrDefault(r => r.Id == imp.LeaveRequestId);
                if (req is null) continue;
                var pr = prorationByEmp[imp.EmployeeId];
                var reqDays = req.EndDate.DayNumber - req.StartDate.DayNumber + 1;
                if (reqDays <= 0) continue;
                var from = req.StartDate > pr.PaidFrom ? req.StartDate : pr.PaidFrom;
                var to   = req.EndDate   < pr.PaidTo   ? req.EndDate   : pr.PaidTo;
                var inside = to < from ? 0 : to.DayNumber - from.DayNumber + 1;
                var scale = Math.Round(inside / (decimal)reqDays, 6);
                if (scale >= 1m) continue;
                leaveImpactScale[imp.Id] = scale;
                c3Warnings.Add(("WARN_IMPACT_OUTSIDE_EMPLOYMENT_WINDOW", imp.EmployeeId,
                    $"Unpaid-leave request {req.StartDate:yyyy-MM-dd}→{req.EndDate:yyyy-MM-dd} extends outside the " +
                    $"employment window {pr.PaidFrom:yyyy-MM-dd}→{pr.PaidTo:yyyy-MM-dd}. The deduction was reduced to " +
                    $"{scale:P2} of the approved amount rather than charged against days the employee was not employed."));
            }
        }

        // ── THE ARREARS ENGINE ──────────────────────────────────────────────────────────────────────
        var arrears = ArrearsComputation.Empty;
        if (run.SettlesArrears)
        {
            var arrearsEmployees = employees.Select(e => new ArrearsEmployee(
                e.Id, e.EmployeeCode, e.FullName,
                e.JoiningDate == default ? null : DateOnly.FromDateTime(e.JoiningDate),
                lastWorkingDayByEmp.TryGetValue(e.Id, out var l) ? l : null)).ToList();
            arrears = await new ArrearsEngine(_db).ComputeAsync(
                tenantId, company.Id, run, arrearsEmployees, salaryAssignments,
                policy, workWeekConfig, statutoryCeiling, cancellationToken);

            // ── NEGATIVE ARREARS (a retro DECREASE) — computed, persisted, and REFUSED ───────────────
            // B2's posture, verbatim: correction runs are ADDITIVE-ONLY and a negative delta (clawback)
            // has no vehicle here. Netting it would break WPS and the control accounts; dropping it with
            // an `if (amount > 0)` guard would leave the employee permanently overpaid and INVISIBLE.
            // The lines are persisted as PendingRecovery so the amount and its covered period survive the
            // refusal, then the run refuses with a specific 422 naming every employee and period.
            if (arrears.PendingRecovery.Count > 0)
            {
                foreach (var line in arrears.PendingRecovery)
                {
                    var exists = await _db.PayrollArrearsLines.AnyAsync(a => a.TenantId == tenantId
                        && a.EmployeeId == line.EmployeeId && a.CoveredYear == line.CoveredYear
                        && a.CoveredMonth == line.CoveredMonth && a.ComponentCode == line.ComponentCode
                        && a.Status == PayrollArrearsStatuses.PendingRecovery, cancellationToken);
                    if (!exists) _db.PayrollArrearsLines.Add(line);
                }
                await _db.SaveChangesAsync(cancellationToken);
                return UnprocessableEntity(new
                {
                    error   = "retro_decrease_unsupported",
                    message = $"{arrears.PendingRecovery.Select(l => l.EmployeeId).Distinct().Count()} employee(s) have a " +
                              "BACKDATED SALARY DECREASE affecting an already-locked period. A payroll run is " +
                              "ADDITIVE-ONLY — a negative earning has no vehicle here and would break the wage file and " +
                              "the control accounts. The amounts have been recorded as PendingRecovery (visible on " +
                              $"GET /api/payroll/runs/{run.Id}/arrears) so nothing is lost. Recover them through an " +
                              "explicit deduction, or correct the effective date of the salary change, then re-process.",
                    employees = arrears.PendingRecovery
                        .GroupBy(l => l.EmployeeId)
                        .Select(g => new
                        {
                            employeeId = g.Key,
                            employeeCode = g.First().EmployeeCode,
                            periods = g.Select(l => new { period = $"{l.CoveredYear}-{l.CoveredMonth:D2}", l.ComponentCode, l.Amount }).ToList(),
                        }).ToList(),
                });
            }
            foreach (var w in arrears.Warnings) c3Warnings.Add((w.Code, w.EmployeeId, w.Message));
            if (arrears.Settled.Count > 0 && policy.ArrearsAreGosiBearing)
                c3Warnings.Add(("WARN_ARREARS_GOSI_TREATMENT_REQUIRES_SIGNOFF", null,
                    $"This run settles {arrears.TotalGosiBearing:N2} of GOSI-BEARING arrears in the period PAID. " +
                    "Whether a retro increment must instead be declared against the months EARNED (an AMENDED GOSI " +
                    "return) is a statutory question requiring a Saudi compliance officer's sign-off. The earned-basis " +
                    $"figure is {arrears.TotalEarnedBasisGosiDelta:N2} and is persisted per covered period on every " +
                    "arrears line. [FLAG-COMPLIANCE-KSA]"));
        }
        var arrearsByEmp = arrears.Settled.GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        // ── POD-B3 HANDOFF: the 1420 receivable a prior void recognised ──────────────────────────────
        // Explicit per-run flag, default OFF: recovering an overpayment out of someone's salary is an act
        // the operator chooses, never a side effect of re-running a month.
        var receivableByEmp = new Dictionary<int, List<PayrollEmployeeReceivable>>();
        if (run.NetsPriorReceivable)
        {
            var outstanding = await _db.PayrollEmployeeReceivables
                .Where(r => r.TenantId == tenantId && r.Status == PayrollReceivableStatuses.Outstanding
                         && (r.CompanyId == company.Id || r.CompanyId == null)
                         && employeeIdsForRun.Contains(r.EmployeeId) && r.Amount > r.RecoveredAmount)
                .OrderBy(r => r.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            receivableByEmp = outstanding.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        }

        // ── POD-C1: THE APPROVED SETTLEMENTS THIS RUN DISBURSES ──────────────────────────────────────
        // The run EMITS the persisted FinalSettlementLine rows VERBATIM. It does not recompute a
        // gratuity, a proration, an encashment or a notice figure — that is POD-A2's "compute once"
        // doctrine applied one layer down, and it is what makes the 2320 accrual and the payslip provably
        // the same numbers rather than two implementations that agree today.
        var settlementsByEmp = new Dictionary<int, EmployeeFinalSettlement>();
        var settlementLinesById = new Dictionary<Guid, List<FinalSettlementLine>>();
        if (run.SettlesFinalSettlements)
        {
            // Belt to CreateRun's brace: a run created before this pod (or by a direct write) could carry
            // the settlement purpose alongside a recurring basis, which is the double-pay hazard in §2.4.
            if (includesRecurringPay)
                return UnprocessableEntity(new
                {
                    error   = "settlement_run_pays_recurring",
                    message = "This run is marked as a termination-settlement run but also pays recurring salary. " +
                              "The leaver's wages through their last working day were already paid by the run that " +
                              "produced their final-wage-month payslip; paying them again here would double the wage " +
                              "and charge a second full month of social insurance.",
                    runId = run.Id,
                });

            // AsNoTracking on purpose: this is the READ-ONLY plan, loaded before the execution-strategy
            // transaction opens and therefore safe to reuse across a transient retry (which Clear()s the
            // tracker). The rows are RE-LOADED as tracked copies inside the transaction before mutation,
            // exactly as activeLoansMutable / activeAdvMutable are.
            var approved = await _db.EmployeeFinalSettlements.AsNoTracking()
                .Where(s => s.TenantId == tenantId
                         && s.Status == FinalSettlementStatuses.Approved
                         && s.PayrollRunId == null
                         && employeeIdsForRun.Contains(s.EmployeeId)
                         && (s.CompanyId == company.Id || s.CompanyId == null))
                .OrderBy(s => s.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            foreach (var s in approved)
                if (!settlementsByEmp.ContainsKey(s.EmployeeId)) settlementsByEmp[s.EmployeeId] = s;
            if (settlementsByEmp.Count > 0)
            {
                var settlementIdList = settlementsByEmp.Values.Select(s => s.Id).ToList();
                settlementLinesById = (await _db.FinalSettlementLines.AsNoTracking()
                        .Where(l => l.TenantId == tenantId && settlementIdList.Contains(l.SettlementId))
                        .OrderBy(l => l.SortOrder).ThenBy(l => l.ComponentCode)
                        .ToListAsync(cancellationToken))
                    .GroupBy(l => l.SettlementId)
                    .ToDictionary(g => g.Key, g => g.ToList());
            }
        }

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
            // POD-B3 — a re-Process re-derives what the run consumes, so the previous witnesses are stale
            // by definition. (Reachable only via POST runs/{id}/reopen, which has already REPLAYED them;
            // this is the belt to that brace and keeps the (run, artifact) unique index clean.)
            _db.PayrollRunConsumptions.RemoveRange(_db.PayrollRunConsumptions.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            // POD-C3 — this run's arrears lines are removed here, i.e. BEFORE the entitlement arithmetic
            // is used. Ordering is load-bearing: leave them and a re-Process reads its OWN previous output
            // as "already settled" and self-cancels every arrears line to zero. (The engine also excludes
            // PayrollRunId == this run explicitly — belt to this brace, because the engine now runs
            // BEFORE the transaction opens.)
            _db.PayrollArrearsLines.RemoveRange(_db.PayrollArrearsLines.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
            // A re-processed run's FACTS have changed, so a judgement recorded about the old figures no
            // longer applies. Overrides are deliberately NOT carried across a re-Process — only across a
            // /validate, which is a read-model refresh of the same facts.
            _db.PayrollValidationOverrides.RemoveRange(_db.PayrollValidationOverrides.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));

        var slips = new List<PayrollSlip>();
        // POD-B2 — per-attempt accumulators. Declared INSIDE the execution-strategy delegate so a
        // transient retry starts clean (the delegate re-runs from here).
        var negativeNetEmployees = new List<object>();
        var statutoryComputedIncrementally = false;
        // POD-C3 — per-attempt accumulators, declared INSIDE the execution-strategy delegate for the same
        // reason as the two above: a transient retry re-runs the delegate from here and must start clean.
        // The per-loan/per-advance maps carry the amount THIS run actually took (which is no longer
        // Math.Min(Installment, Outstanding) once debt is capped at affordable net), so the mutable
        // decrement block below and the POD-B3 witness both record the truth rather than a recomputation.
        var loanTakenByEmployee = new Dictionary<int, Dictionary<Guid, decimal>>();
        var advTakenByEmployee  = new Dictionary<int, Dictionary<Guid, decimal>>();
        var deferredEmiEmployees = new List<(int Id, string Code, string Name, decimal Amount)>();
        var recurringShortfallEmployees = new List<(int Id, string Code, string Name, decimal Shortfall, bool Prorated, decimal StatutoryEe)>();
        var receivableResidualEmployees = new List<(int Id, string Code, string Name, decimal Amount)>();
        // POD-C3-FIX — recovery taken out of a period OTHER than the one the receivable arose in. That is
        // a genuine deduction from wages (KSA Labour Law art. 91's cap on recovering amounts paid in
        // excess), whereas recovering inside the SAME period is merely declining to pay the same month
        // twice. The two are collected apart so only the former carries a compliance flag.
        var receivableCrossPeriodEmployees = new List<(int Id, string Code, string Name, decimal Amount, string Periods)>();
        var receivableTakenByEmployee = new List<(Guid ReceivableId, int EmployeeId, decimal Amount)>();
        var arrearsLinesToPersist = new List<PayrollArrearsLine>();
        // POD-C1 — per-attempt accumulators for the settlements this run disburses, declared INSIDE the
        // execution-strategy delegate for the same reason as everything above: a transient retry re-runs
        // the delegate from here and must start clean.
        var settlementsDisbursed = new List<EmployeeFinalSettlement>();
        var settlementEncashmentTaken = new List<(Guid BalanceId, int EmployeeId, decimal Days)>();
        var settlementRecoveryReduced = new List<(int Id, string Code, string Name, decimal Planned, decimal Taken)>();
        foreach (var e in employees)
        {
            var salary = salaryAssignments.Where(x => x.EmployeeId == e.Id && x.EffectiveDate <= periodEnd).OrderByDescending(x => x.EffectiveDate).FirstOrDefault();
            // ── POD-C3: THE FULL PACKAGE (rate basis) vs THE PRORATED PACKAGE (earning basis) ─────────
            // This split is the single most important correctness point in the pod. `full*` is the
            // employee's MONTHLY RATE and is what every per-unit rate must be derived from; the prorated
            // values are what they are ENTITLED to for the days actually employed. Conflating them
            // under-pays overtime and under-charges absence for every joiner and leaver.
            var fullBasic     = salary?.BasicSalary ?? e.Salary ?? 0m;
            var fullHousing   = salary?.HousingAllowance ?? 0m;
            var fullTransport = salary?.TransportAllowance ?? 0m;
            var fullOther     = (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
            var fullFixedDeduction = salary?.FixedDeduction ?? 0m;

            var proration = prorationByEmp.TryGetValue(e.Id, out var pr0) ? pr0
                : ProrationCalculator.Compute(periodStart, periodEnd, null, null, policy.Basis, workWeekConfig);
            var factor = proration.Factor;
            var proratedPkg = ProrationCalculator.Apply(fullBasic, fullHousing, fullTransport, fullOther, factor);

            // The configurable PRORATED SET decides which components the factor actually touches — a
            // reimbursement-style allowance is commonly paid in full in the joining month, and prorating
            // a FIXED DEDUCTION *increases* net (right for a canteen charge, wrong for a recovery
            // instalment). Default set = the whole package, i.e. the GCC convention.
            var basic     = policy.Prorates(ProratedComponentCodes.Basic)           ? proratedPkg.Basic           : fullBasic;
            var housing   = policy.Prorates(ProratedComponentCodes.Housing)         ? proratedPkg.Housing         : fullHousing;
            var transport = policy.Prorates(ProratedComponentCodes.Transport)       ? proratedPkg.Transport       : fullTransport;
            var otherAllowances = policy.Prorates(ProratedComponentCodes.OtherAllowances) ? proratedPkg.OtherAllowances : fullOther;
            var gross = basic + housing + transport + otherAllowances;
            var fixedDeduction = policy.Prorates(ProratedComponentCodes.FixedDeduction)
                ? ProrationCalculator.ApplyScalar(fullFixedDeduction, factor)
                : fullFixedDeduction;
            var prorationNote = proration.Narrative;

            // Hourly rate for short-hours (late/early) deductions and OT base — the FULL monthly basic ÷
            // standardMonthlyHours. Deriving it from the prorated basic would pay a joiner's overtime at
            // a fraction of their real hourly rate.
            var hourlyRate = standardMonthlyHours > 0 ? fullBasic / standardMonthlyHours : 0m;

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
            // POD-C3 — day rate on the FULL monthly basic (an absent day costs the same whenever you
            // joined), and absent days can never exceed the days actually employed.
            if (proration.IsProrated && lopDays > proration.PaidDays) lopDays = proration.PaidDays;
            var lopDayRate = lopDayDivisor > 0 && fullBasic > 0 ? fullBasic / lopDayDivisor : 0m;
            var lopDeduction = Math.Round(lopDays * lopDayRate, 2);

            var leaveDeduction = leaveImpacts
                .Where(x => x.EmployeeId == e.Id && x.ImpactType.Contains("Deduction", StringComparison.OrdinalIgnoreCase))
                .Sum(x => leaveImpactScale.TryGetValue(x.Id, out var sc) ? Math.Round(x.Amount * sc, 2) : x.Amount);

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
                // If no explicit taxable components defined, treat basic salary as taxable.
                // POD-C3 — the PERCENTAGE branch already rides the prorated `basic`; the ABSOLUTE branch
                // is a set of monthly amounts and must be prorated by the same factor, or a joiner would
                // be taxed on a full month of income they never received.
                var taxableBase = structureComponents.Count > 0
                    ? ProrationCalculator.ApplyScalar(
                        structureComponents.Sum(c => c.CalculationType == "Percentage" ? fullBasic * c.Percentage / 100m : c.Amount),
                        factor)
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
                // POD-C3 — a supplemental run pays no recurring wage, so the FULL package is not its
                // statutory basis either. Zeroing them here keeps the slip's proration witnesses honest
                // and stops the FullMonth GOSI branch below re-introducing a whole month's covered wage
                // on a bonus-only run.
                fullBasic = 0m; fullHousing = 0m; fullTransport = 0m; fullOther = 0m;
                prorationNote = string.Empty;
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

            // ── POD-C3: ARREARS in the statutory base ────────────────────────────────────────────────
            // GOSI-bearing arrears ride the SAME housing slot gosiIncludedBonusTotal already uses, so the
            // 45,000 ceiling is applied to (period base + bonus + arrears) in exactly ONE pack call. The
            // A1 reconstruction adds them to the same slot from the same sub-ledger, keyed on the same
            // IsGosiBearing flag — one flag governs both sides, so they cannot drift.
            // (empArrears is empty unless the run opted into settling arrears — the engine was not even
            // called otherwise — so no second gate is needed here.)
            var empArrears = arrearsByEmp.TryGetValue(e.Id, out var al) ? al : new List<PayrollArrearsLine>();
            var arrearsTotal     = Math.Round(empArrears.Sum(a => a.Amount), 2);
            var gosiArrearsTotal = Math.Round(empArrears.Where(a => a.IsGosiBearing).Sum(a => a.Amount), 2);

            // ── POD-C3 [FLAG-COMPLIANCE-KSA]: WHICH BASE THE PACK IS FED ─────────────────────────────
            // DEFAULT `FullMonth`. GOSI assesses a MONTHLY contributory wage: the month an employee is
            // registered attracts a full month's contribution on the registered wage, and the portal has
            // no partial-month proration of it. Prorating under-remits in every joining and leaving month
            // — the penalty-bearing direction — and would also CONTRADICT the treatment already shipped
            // and tested for unpaid absence, where LOP explicitly does NOT reduce the covered wage. Same
            // economic fact, opposite treatment, one payslip.
            //
            // Under FullMonth the covered wage is NOT recoverable from slip.BasicSalary /
            // slip.HousingAllowance, which is why the slip persists FullBasicSalary /
            // FullHousingAllowance / GosiBasePolicy below and BOTH reconciliation paths rebuild from them.
            var statBasic   = policy.ProratesStatutoryBase ? basic   : fullBasic;
            var statHousing = policy.ProratesStatutoryBase ? housing : fullHousing;
            var statutoryInput = new StatutoryDeductionInput(
                EmployeeId:   Guid.Empty, // Employee PK is int; Guid field not used in pack calculations
                CompanyId:    run.CompanyId ?? Guid.Empty,
                Salary:       new SalaryBreakdown(priorBasic + statBasic, priorHousing + statHousing + gosiIncludedBonusTotal + gosiArrearsTotal, transport, otherAllowances),
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
            //
            // POD-C1 — a SETTLING employee is the exception: this is their LAST payment, so their debts are
            // settled with it rather than left to a next run that will never come. They are re-loaded here
            // even though the run pays no recurring salary, and the "due" below is the WHOLE outstanding
            // balance rather than one instalment.
            var empSettlement = settlementsByEmp.TryGetValue(e.Id, out var stl) ? stl : null;
            var isSettlingEmployee = empSettlement is not null;
            var empLoans   = includesRecurringPay || isSettlingEmployee ? activeLoans.Where(l => l.EmployeeIntId == e.Id).OrderBy(l => l.Id).ToList() : new List<EmployeeLoan>();
            var empAdv     = includesRecurringPay || isSettlingEmployee ? activeAdvances.Where(a => a.EmployeeIntId == e.Id).OrderBy(a => a.Id).ToList() : new List<SalaryAdvance>();

            // POD-C1 — the settlement's OWN lines, read verbatim from the persisted plan.
            var empSettlementLines = empSettlement is not null
                    && settlementLinesById.TryGetValue(empSettlement.Id, out var sl)
                ? sl
                : new List<FinalSettlementLine>();
            var settlementEarningTotal = Math.Round(empSettlementLines
                .Where(l => l.LineType == FinalSettlementLineTypes.Earning).Sum(l => l.Amount), 2);
            // Capped at the settlement's own gross AT PLAN TIME (see the approve path), so a settlement's
            // own deductions can never drive net negative and 422 the whole batch out from under every
            // other leaver in the run.
            var settlementDeductionTotal = Math.Round(Math.Min(settlementEarningTotal, empSettlementLines
                .Where(l => l.LineType == FinalSettlementLineTypes.Deduction).Sum(l => l.Amount)), 2);

            // ── POD-C3 (MF-2): DEBT IS CAPPED AT WHAT THE PAY PERIOD CAN FUND ────────────────────────
            // Proration makes an un-prorated EMI reachable for the first time: a joiner on the 25th with
            // a 20,000 package and a 5,000 instalment earns ~4,000 for the month. Pre-C3 the shortfall
            // was silently swallowed by `Math.Max(0m, rawNet)` on a Regular run (the negative-net
            // collector was gated on `!includesRecurringPay`), so the employee was underpaid AND the
            // unfloored deduction credits made Σ DR ≠ Σ CR — a gl_unbalanced 422 at Lock on a run that
            // locks cleanly today. The instalment is therefore capped at the net available BEFORE debt,
            // and the balance CARRIES FORWARD (standard GCC practice, and the only outcome that neither
            // underpays nor unbalances). An EMI is a DEBT INSTALMENT, not a wage — it is never prorated.
            var deductionsBeforeDebt = fixedDeduction + attendanceDeduction + lopDeduction + leaveDeduction
                                     + taxDeduction + gosiEmployeeTotal + adjustmentDeductions + totalBonusTax
                                     + settlementDeductionTotal;
            var earningsForPeriod = gross + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal
                                  + settlementEarningTotal;
            var affordable = Math.Round(earningsForPeriod - deductionsBeforeDebt, 2);

            var loanTakenById = new Dictionary<Guid, decimal>();
            var advTakenById  = new Dictionary<Guid, decimal>();
            decimal loanEmi = 0m, advEmi = 0m, deferredDebt = 0m;
            var debtBudget = Math.Max(0m, affordable);
            foreach (var l in empLoans)
            {
                // POD-C1 — a leaver's debts are settled from their FINAL payment, so the whole balance is
                // due, not one instalment. The take is still re-capped against the LIVE OutstandingBalance
                // inside this transaction (after any final-wage-month EMI has already decremented it) and
                // against what the settlement can fund, so Σ recovery ≤ the original balance is arithmetic
                // rather than convention, and there is exactly ONE decrement path.
                var due = isSettlingEmployee ? l.OutstandingBalance : Math.Min(l.InstallmentAmount, l.OutstandingBalance);
                if (due <= 0m) continue;
                var take = Math.Min(due, debtBudget);
                if (take > 0m) { loanTakenById[l.Id] = take; loanEmi += take; debtBudget -= take; }
                deferredDebt += due - take;
            }
            foreach (var a in empAdv)
            {
                var due = isSettlingEmployee ? a.OutstandingBalance : Math.Min(a.InstallmentAmount, a.OutstandingBalance);
                if (due <= 0m) continue;
                var take = Math.Min(due, debtBudget);
                if (take > 0m) { advTakenById[a.Id] = take; advEmi += take; debtBudget -= take; }
                deferredDebt += due - take;
            }
            // POD-C1 — the settlement PLANNED a recovery at approve; the live balance may since have moved
            // (the final wage month's own EMI). A reduction is reported, never silently over-recovered.
            if (isSettlingEmployee)
            {
                var plannedRecovery = Math.Round(empSettlement!.PlannedLoanRecovery + empSettlement.PlannedAdvanceRecovery, 2);
                var actualRecovery  = Math.Round(loanEmi + advEmi, 2);
                if (plannedRecovery - actualRecovery > 0.01m)
                    settlementRecoveryReduced.Add((e.Id, e.EmployeeCode, e.FullName, plannedRecovery, actualRecovery));
            }
            // POD-B1b-FIX (re-audit #6) — NO rounding on the EMI path. An instalment is
            // ApprovedAmount / Installments and is routinely 4dp; the accrual debit posts it verbatim, so
            // rounding here would make Σ CR ≠ Σ DR by up to a cent on the remittance and either post a
            // journal off by a cent or 422 a legitimate one. The capping above is exact by construction
            // (Math.Min of two stored decimals), so it introduces no new precision either.
            deferredDebt = Math.Round(deferredDebt, 2);
            if (deferredDebt > 0m)
                deferredEmiEmployees.Add((e.Id, e.EmployeeCode, e.FullName, deferredDebt));
            if (loanTakenById.Count > 0) loanTakenByEmployee[e.Id] = loanTakenById;
            if (advTakenById.Count > 0)  advTakenByEmployee[e.Id]  = advTakenById;
            // Rounded exactly where it was pre-C3: the slip AGGREGATE rounds, the emitted LOAN_EMI /
            // ADVANCE_EMI lines do not.
            var totalLoanDeduction = Math.Round(loanEmi + advEmi, 2);

            // ── POD-C3 (POD-B3 handoff): net the 1420 receivable a prior void recognised ─────────────
            // min(outstanding, net before recovery) — net then equals "what we should have paid − what we
            // already paid", which is the definition of a recovery. Never drives net negative.
            decimal receivableRecovery = 0m;
            if (run.NetsPriorReceivable && receivableByEmp.TryGetValue(e.Id, out var empReceivables))
            {
                var recoverable = Math.Max(0m, Math.Round(affordable - totalLoanDeduction, 2));
                // POD-C3-FIX — how much of this employee's recovery came from a receivable recognised in a
                // DIFFERENT period, and which periods those were. Recovering inside the SAME period is not
                // a wage deduction at all (the replacement run re-pays the very month whose cash the
                // employee already holds); recovering out of a LATER month's wages is, and the two must
                // not be reported as though they were the same act.
                decimal crossPeriodTaken = 0m;
                var crossPeriods = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var rcv in empReceivables)
                {
                    if (recoverable <= 0m) break;
                    var take = Math.Min(rcv.Outstanding, recoverable);
                    if (take <= 0m) continue;
                    receivableRecovery += take;
                    recoverable -= take;
                    receivableTakenByEmployee.Add((rcv.Id, e.Id, take));
                    if (!string.Equals(rcv.Period, periodStr, StringComparison.Ordinal))
                    {
                        crossPeriodTaken += take;
                        crossPeriods.Add(string.IsNullOrWhiteSpace(rcv.Period) ? "(unknown period)" : rcv.Period);
                    }
                }
                receivableRecovery = Math.Round(receivableRecovery, 2);
                crossPeriodTaken   = Math.Round(crossPeriodTaken, 2);
                if (crossPeriodTaken > 0m)
                    receivableCrossPeriodEmployees.Add((e.Id, e.EmployeeCode, e.FullName, crossPeriodTaken,
                        string.Join(", ", crossPeriods)));
                var residual = Math.Round(empReceivables.Sum(r => r.Outstanding) - receivableRecovery, 2);
                if (residual > 0m)
                    receivableResidualEmployees.Add((e.Id, e.EmployeeCode, e.FullName, residual));
            }

            var deductions = deductionsBeforeDebt + totalLoanDeduction + receivableRecovery;
            // C3: net salary cannot be negative (GCC labour law); engine Rule 3 will flag this.
            // (gross bonus in, bonus tax out) == (net bonus in) — take-home is unchanged by POD-B1b.
            var rawNet = earningsForPeriod - deductions;
            // POD-B2 (M4): a run whose deductions exceed its earnings has no vehicle to express the
            // shortfall — the floor below silently swallows it and the run then trips GL_WILL_NOT_BALANCE
            // (an Error whose remedy, "re-process the run", can never fix it), leaving an unlockable run
            // that DeleteRun also refuses. Correction runs are ADDITIVE-ONLY: collect the offenders and
            // refuse the whole run with a specific 422 below.
            //
            // POD-C3 (MF-2): negative net is now DETECTED on recurring runs too — that gate is exactly
            // what made the magnitude of the shortfall invisible on a Regular run (the pre-existing
            // ZERO_NET_WITH_GROSS Error says net is zero, never by how much it was short, nor why).
            //
            // The RESPONSE differs by run type ON PURPOSE, and this is a deliberate narrowing of the
            // consultant's "refuse everywhere":
            //   • SUPPLEMENTAL — unchanged: throw, roll back, 422 negative_net_unsupported (POD-B2's
            //     tested contract; an additive-only run with no vehicle for a negative delta).
            //   • RECURRING — the run is still WRITTEN and net is still clamped at zero, because the
            //     existing ZERO_NET_WITH_GROSS Error already BLOCKS Approve and Lock, so the unbalanced
            //     journal the clamp would otherwise cause is unreachable. Refusing at Process instead
            //     would break that shipped, tested remedy path (the operator inspects the processed run,
            //     fixes the cause, re-processes) for a case a joiner can now legitimately reach. What C3
            //     adds is a SECOND, Error-severity result that names the exact shortfall and its most
            //     likely cause — information the operator previously had to derive by hand.
            // Debt is already capped above, so what lands here is a genuinely unfundable NON-DEBT
            // deduction, most often the FullMonth statutory contribution of a late-month joiner.
            if (rawNet < 0m)
            {
                if (!includesRecurringPay)
                    negativeNetEmployees.Add(new { employeeId = e.Id, code = e.EmployeeCode, name = e.FullName, shortfall = Math.Abs(rawNet) });
                else
                    recurringShortfallEmployees.Add((e.Id, e.EmployeeCode, e.FullName, Math.Abs(rawNet),
                        proration.IsProrated, gosiEmployeeTotal));
            }
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
                // POD-C1 — the settlement's earnings ride the SAME aggregate as bonus/adjustment/arrears do,
                // so NetSalary, the WPS/SIF amount and the payment batch total all pick them up with no new
                // machinery. (The statutory base is untouched — see the GOSI decision on FinalSettlement.)
                OtherAllowances = otherAllowances + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal + settlementEarningTotal,
                GrossSalary = gross + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal + settlementEarningTotal,
                Deductions = deductions,
                NetSalary = netSalary,
                EmployeeStatutoryTotal = statutoryResult.TotalEmployeeDeduction,
                EmployerStatutoryTotal = statutoryResult.TotalEmployerContribution,
                LoanDeductions = totalLoanDeduction,
                YtdGross = ytdGross + gross + overtimePay + totalBonusGross + adjustmentEarnings + arrearsTotal + settlementEarningTotal,
                YtdDeductions = ytdDeduct + deductions,
                YtdNet = ytdNet + netSalary,
                Status = "Draft",
                // ── POD-C3: THE PRORATION WITNESSES ─────────────────────────────────────────────────
                // These are what let POD-A1 keep its guarantee — "reconstruct expected from the run's own
                // persisted outputs" — once the money columns carry a PRORATED wage while the statutory
                // base is the FULL monthly package. Both reconciliation paths rebuild from them.
                PaidFromDate = proration.PaidFrom,
                PaidToDate = proration.PaidTo,
                PaidDays = proration.PaidDays,
                ProrationDenominatorDays = proration.DenominatorDays,
                PeriodDays = proration.PeriodDays,
                ProrationBasis = policy.Basis,
                ProrationFactor = factor,
                FullBasicSalary = fullBasic,
                FullHousingAllowance = fullHousing,
                FullTransportAllowance = fullTransport,
                GosiBasePolicy = policy.GosiBase,
                ArrearsAmount = arrearsTotal,
                // POD-C1 SEAM: the WAGE side of this leaver's final month is settled here and nowhere
                // else. C3 emits NO EOSB, no notice pay, no leave encashment, no termination payable and
                // no off-cycle settlement journal. Everything after PaidToDate is C1's.
                IsFinalWageMonth = includesRecurringPay && proration.IsFinalWageMonth,
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
            AddEarning(tenantId, id, e.Id, "BASIC", WithProrationNote("Basic salary", prorationNote), basic, "Salary");
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
            if (fixedDeduction > 0) AddDeduction(tenantId, company.Id, id, e.Id, "FIXED_DEDUCTION",
                WithProrationNote("Fixed deduction", policy.Prorates(ProratedComponentCodes.FixedDeduction) ? prorationNote : string.Empty),
                fixedDeduction, "Salary");
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
                // POD-C3 — the proration narrative is applied to the ENGINE'S OUTPUT rather than inside
                // the engine, so PayComponentEngine itself is untouched and the golden-master/equivalence
                // tests keep their meaning. WithProrationNote is the identity function when the note is
                // empty, which it always is for an employee employed the whole period.
                foreach (var line in computation.Earnings)
                    AddEarning(tenantId, id, e.Id, line.Code,
                        line.Code == "BASIC" ? WithProrationNote(line.Name, prorationNote) : line.Name,
                        line.Amount, line.Source);
                foreach (var line in computation.Deductions)
                    AddDeduction(tenantId, company.Id, id, e.Id, line.Code,
                        line.Code == "FIXED_DEDUCTION" && policy.Prorates(ProratedComponentCodes.FixedDeduction)
                            ? WithProrationNote(line.Name, prorationNote) : line.Name,
                        line.Amount, line.Source, isEmployerContribution: line.IsEmployerContribution);
            }

            // ── POD-C3: ARREARS LINES — ITEMISED PER COVERED PERIOD ─────────────────────────────────
            // Emitted OUTSIDE both branches, exactly like POD-B1b's bonus withholding, so the legacy
            // block and the component engine stay provably identical (neither owns arrears). One line per
            // (covered period, component) so an employee and an auditor can both see WHICH months the
            // number covers — never one opaque "arrears" figure. EarningDriverKey routes each to the
            // component's OWN expense account (a retro basic increase debits Basic Salary Expense).
            foreach (var line in empArrears.OrderBy(a => a.CoveredYear).ThenBy(a => a.CoveredMonth)
                                           .ThenBy(a => a.ComponentCode, StringComparer.Ordinal))
            {
                if (line.Amount <= 0m) continue;
                AddEarning(tenantId, id, e.Id, line.ComponentCode,
                    $"Arrears — {PayrollArrearsComponents.Label(line.ComponentCode)} ({line.CoveredYear}-{line.CoveredMonth:D2})",
                    line.Amount, "Arrears");
                arrearsLinesToPersist.Add(line);
            }

            // ── POD-C1: THE SETTLEMENT LINES, EMITTED VERBATIM ──────────────────────────────────────
            // Emitted OUTSIDE both emission branches, exactly like POD-B1b's bonus withholding and POD-C3's
            // arrears, so the legacy inline block and the component engine stay provably identical (neither
            // owns settlements). NOTHING IS RECOMPUTED HERE: every amount, name and quantity is the
            // persisted plan that was approved and accrued to 2320, which is what makes the payslip and the
            // journal the same numbers by construction rather than by two implementations agreeing.
            //   • Earnings carry Source="Settlement" → EarningDriverKeyFor routes each to its OWN expense
            //     account (5110/5111/5112/5099), and BuildPayrollGlEntries swaps the debit for the stored
            //     2320 payable so the cost is recognised exactly once, at approval.
            //   • The settlement's own deductions carry Source="Settlement" → DED:SETTLEMENT_RECOVERY
            //     (5113 contra-expense), never DED:OTHER (2199), which nothing would ever clear.
            //   • Loan/advance recovery is emitted below through the EXISTING LOAN_EMI/ADVANCE_EMI lines,
            //     and receivable recovery through POD-C3's existing block — one mechanism each, so there
            //     is no double-recovery to guard against.
            if (empSettlement is not null)
            {
                foreach (var line in empSettlementLines.Where(l => l.LineType == FinalSettlementLineTypes.Earning && l.Amount > 0m))
                    AddEarning(tenantId, id, e.Id, line.ComponentCode, line.ComponentName, line.Amount,
                        FinalSettlementComponents.SettlementSource);
                // Capped at the settlement's gross (settlementDeductionTotal), pro rata across the lines,
                // so the sum emitted can never exceed what the settlement earns.
                var plannedDeductions = Math.Round(empSettlementLines
                    .Where(l => l.LineType == FinalSettlementLineTypes.Deduction).Sum(l => l.Amount), 2);
                var dedScale = plannedDeductions > 0m && settlementDeductionTotal < plannedDeductions
                    ? settlementDeductionTotal / plannedDeductions
                    : 1m;
                foreach (var line in empSettlementLines.Where(l => l.LineType == FinalSettlementLineTypes.Deduction && l.Amount > 0m))
                {
                    var amount = Math.Round(line.Amount * dedScale, 2);
                    if (amount <= 0m) continue;
                    AddDeduction(tenantId, company.Id, id, e.Id, line.ComponentCode, line.ComponentName, amount,
                        FinalSettlementComponents.SettlementSource);
                }
                settlementsDisbursed.Add(empSettlement);
                // The leave-balance rows this settlement encashes, so Process can decrement the EXACT rows
                // the plan named (witnessed for the void) instead of re-deriving them.
                foreach (var line in empSettlementLines.Where(l =>
                             l.ComponentCode == FinalSettlementComponents.LeaveEncashment
                             && l.SourceEntityId is Guid && l.Quantity > 0m))
                    settlementEncashmentTaken.Add((line.SourceEntityId!.Value, e.Id, line.Quantity));
            }

            // ── POD-C3 (POD-B3 handoff): the 1420 recovery deduction ────────────────────────────────
            // Source "Recovery" routes to DED:RECEIVABLE_RECOVERY, which CREDITS the 1420 ASSET the void
            // debited. Routing it to DED:OTHER (2199) would credit a liability nobody owes and leave the
            // receivable ageing forever — the exact gap B3 handed to C3.
            if (receivableRecovery > 0m)
                AddDeduction(tenantId, company.Id, id, e.Id,
                    PayrollRecoveryComponents.ReceivableRecovery, PayrollRecoveryComponents.ReceivableRecoveryName,
                    receivableRecovery, PayrollRecoveryComponents.RecoverySource);

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
        // POD-C3 — the arrears sub-ledger, stamped with the settling run. Persisted INSIDE the run
        // transaction alongside the earning lines it produced, so a mid-run fault can never leave a
        // "Settled" line behind an earning that was rolled back.
        if (arrearsLinesToPersist.Count > 0) _db.PayrollArrearsLines.AddRange(arrearsLinesToPersist);
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
        //
        // ── POD-B3: WITNESS EVERY CONSUMPTION, IN THE SAME TRANSACTION AS THE CONSUMPTION ─────────────
        // AttendancePayrollImpact and LeavePayrollImpact carry NO PayrollRunId, so once they read
        // "Processed" nothing on earth can say WHICH run consumed them — and a void that could not
        // release them silently dropped the month's LOP, absence and OT from any re-run. The witness rows
        // written here are what the void/reopen unwind replays; they also NARROW the status writes to the
        // exact rows this run actually computed pay from (the lists were loaded above with the identical
        // predicate), so a row inserted mid-Process is no longer marked Processed without being paid.
        //
        // The company id is hoisted out of the closure deliberately. `company` is non-null by the fail-loud
        // company_not_resolved guard above, but the compiler cannot carry that flow state across a local
        // function's capture, so referencing `company.Id` inside Witness raised CS8602. Silencing it with
        // `!` would have left a genuine future null-deref in payroll's write path looking identical to this
        // false positive; binding the already-validated value once removes the warning honestly.
        var witnessCompanyId = company.Id;
        void Witness(string artifactType, Guid artifactId, int employeeId, decimal amount,
                     string? priorStatus, decimal? priorOutstanding = null, decimal? priorRepaid = null,
                     decimal? priorAmountPaid = null, Guid? priorRunId = null) =>
            _db.PayrollRunConsumptions.Add(new PayrollRunConsumption
            {
                TenantId = tenantId, CompanyId = witnessCompanyId, PayrollRunId = id,
                ArtifactType = artifactType, ArtifactId = artifactId, EmployeeId = employeeId,
                Amount = amount, PriorStatus = priorStatus,
                PriorOutstandingBalance = priorOutstanding, PriorTotalRepaid = priorRepaid,
                PriorAmountPaid = priorAmountPaid, PriorPayrollRunId = priorRunId,
            });

        if (includesRecurringPay)
        {
            var attendanceImpactIds = attendanceImpacts.Select(x => x.Id).ToList();
            foreach (var x in attendanceImpacts)
                Witness(PayrollConsumptionArtifacts.AttendanceImpact, x.Id, x.EmployeeId, 0m, x.Status);
            await _db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId && attendanceImpactIds.Contains(x.Id)).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"), cancellationToken);

            var leaveImpactIds = leaveImpacts.Select(x => x.Id).ToList();
            foreach (var x in leaveImpacts)
                Witness(PayrollConsumptionArtifacts.LeaveImpact, x.Id, x.EmployeeId, x.Amount, x.Status);
            await _db.LeavePayrollImpacts.Where(x => x.TenantId == tenantId && leaveImpactIds.Contains(x.Id)).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);

            var consumedOtImpacts = overtimeImpacts
                .Where(x => x.Status != "Processed" && employeeIdsForRun.Contains(x.EmployeeId)
                         && periodOvertimeRequestIds.Contains(x.OvertimeRequestId))
                .ToList();
            var consumedOtIds = consumedOtImpacts.Select(x => x.Id).ToList();
            foreach (var x in consumedOtImpacts)
                Witness(PayrollConsumptionArtifacts.OvertimeImpact, x.Id, x.EmployeeId, x.Amount, x.Status,
                        priorRunId: x.PayrollRunId);
            await _db.OvertimePayrollImpacts
                .Where(x => x.TenantId == tenantId && consumedOtIds.Contains(x.Id))
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.PayrollRunId, id).SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);
        }
        // Adjustments are NOT gated: they are already `PayrollRunId == id`-scoped, i.e. attached to THIS
        // run by the operator, and a supplemental run pays them.
        var consumedAdjustmentIds = approvedAdjustments
            .Where(a => employeeIdsForRun.Contains(a.EmployeeId))
            .Select(a => a.Id)
            .ToList();
        foreach (var a in approvedAdjustments.Where(a => employeeIdsForRun.Contains(a.EmployeeId)))
            Witness(PayrollConsumptionArtifacts.Adjustment, a.Id, a.EmployeeId, a.Amount, a.Status);
        await _db.PayrollAdjustments
            .Where(x => x.TenantId == tenantId && consumedAdjustmentIds.Contains(x.Id))
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

        // ── POD-C3: everything the run DECIDED, said out loud ────────────────────────────────────────
        // Added alongside the engine's output rather than inside PayrollValidationEngine, so the engine's
        // rule set (and its tests) are untouched. All Warning severity — none of these block a lock; they
        // exist so no proration, arrears or debt-deferral decision is ever invisible.
        void C3Warn(string code, int? employeeId, string message) =>
            _db.PayrollValidationResults.Add(new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = id, EmployeeId = employeeId,
                Severity = "Warning", Code = code, Message = message,
            });
        foreach (var (code, empId, msg) in c3Warnings) C3Warn(code, empId, msg);
        // POD-C3 (MF-2) — the shortfall, NAMED. Error severity, so it blocks Approve/Lock exactly as the
        // pre-existing ZERO_NET_WITH_GROSS does; what it adds is the AMOUNT and the most likely cause,
        // which the operator previously had to derive by hand from the payslip.
        foreach (var (empId, code, name, shortfall, prorated, statEe) in recurringShortfallEmployees)
            _db.PayrollValidationResults.Add(new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = id, EmployeeId = empId,
                Severity = "Error", Code = "NEGATIVE_NET_SHORTFALL",
                Message = $"{name} ({code}): deductions exceed earnings by {shortfall:N2}; net was clamped to 0.00, so " +
                          $"they are UNDERPAID by that amount. Loan/advance instalments were already capped at the " +
                          "available net and carried forward, so the remainder is non-debt." +
                          (prorated
                              ? $" This employee's wage is PRORATED for part of the month while their social-insurance " +
                                $"contribution ({statEe:N2}) is computed on the FULL registered monthly wage " +
                                $"(payparameter.proration_gosi_base = '{policy.GosiBase}'). Either set that company " +
                                "policy to 'Prorated', or add a positive adjustment covering the shortfall."
                              : " Reduce the absence/LOP or adjustment deductions, or add a positive adjustment.") +
                          " This Error blocks Approve and Lock, so no unbalanced journal can be posted.",
            });
        foreach (var (empId, code, name, amount) in deferredEmiEmployees)
            C3Warn("WARN_EMI_DEFERRED_PRORATED_PERIOD", empId,
                $"{name} ({code}): {amount:N2} of loan/advance instalment could not be funded by this period's net " +
                "pay and was CARRIED FORWARD, not written off. The outstanding balance is unchanged for that " +
                "portion, so the next run will collect it.");
        foreach (var (empId, code, name, amount) in receivableResidualEmployees)
            C3Warn("WARN_RECEIVABLE_RESIDUAL", empId,
                $"{name} ({code}): {amount:N2} of a prior voided run's disbursed net pay remains outstanding on the " +
                "Employee Overpayment Receivable after this run's recovery. It is carried on the sub-ledger and ages " +
                $"against GET /api/payroll/receivables — it is NOT forgiven.");
        // POD-C3-FIX — see receivableCrossPeriodEmployees: only a recovery taken from a period OTHER than
        // the one the cash was disbursed for is a deduction from wages, and only that one needs sign-off.
        foreach (var (empId, code, name, amount, periods) in receivableCrossPeriodEmployees)
            C3Warn("WARN_RECEIVABLE_RECOVERY_CROSS_PERIOD", empId,
                $"{name} ({code}): {amount:N2} recovered on this {periodStr} run relates to a voided run in " +
                $"{periods}, so it is a DEDUCTION FROM WAGES rather than a decision not to pay the same month " +
                "twice. This run applies NO statutory cap to it — the full outstanding amount is taken up to the " +
                "available net. KSA Labour Law limits how much of an amount paid in excess may be deducted from a " +
                "worker's wage; whether that cap binds here, and at what percentage, requires a Saudi compliance " +
                "officer's sign-off. If it does, recover across several runs by re-processing after part-recovery, " +
                "or agree a repayment plan outside payroll. [FLAG-COMPLIANCE-KSA]");
        foreach (var (emp, reason) in unpayableNonActive)
            C3Warn("WARN_LEAVER_NO_LAST_WORKING_DAY", emp.Id,
                $"{emp.FullName} ({emp.EmployeeCode}): {reason} If they are still owed wages, create an offboarding " +
                "record with the correct last working day, then re-process.");
        foreach (var e in employees.Where(x => leaverEmployeeIds.Contains(x.Id)))
        {
            var pr = prorationByEmp[e.Id];
            C3Warn("WARN_LEAVER_INCLUDED_ON_NOTICE", e.Id,
                $"{e.FullName} ({e.EmployeeCode}) has status '{e.Status}' and IS being paid for {pr.PaidFrom:yyyy-MM-dd}" +
                $"→{pr.PaidTo:yyyy-MM-dd}" + (pr.IsFinalWageMonth ? " (FINAL wage month)" : " (serving notice)") +
                ". Before POD-C3 they were silently dropped from the run and paid nothing.");
        }
        // ── POD-C1: what the SETTLEMENT side of this run decided, said out loud ──────────────────────
        foreach (var (empId, code, name, planned, taken) in settlementRecoveryReduced)
            C3Warn("WARN_SETTLEMENT_RECOVERY_REDUCED", empId,
                $"{name} ({code}): the settlement planned to recover {planned:N2} of outstanding loan/advance but only " +
                $"{taken:N2} could be taken from this payment. The balance is NOT written off — it stays on the loan " +
                "sub-ledger and is dispositioned when the settlement reaches Paid (reclassified to the Employee " +
                "Overpayment Receivable up to what 1400/1410 actually carries, and reported when it cannot be).");
        if (settlementsDisbursed.Count > 0)
        {
            foreach (var s in settlementsDisbursed)
                C3Warn("WARN_SETTLEMENT_DISBURSED", s.EmployeeId,
                    $"{s.EmployeeName} ({s.EmployeeCode}): this run disburses their FINAL SETTLEMENT of " +
                    $"{s.NetPayable:N2} {s.Currency} (last working day {s.LastWorkingDay:yyyy-MM-dd}, reason " +
                    $"'{s.TerminationReason}'). Their wages through the last working day were paid by the run that " +
                    "produced their final-wage-month payslip and are NOT repeated here.");
            // [FLAG-COMPLIANCE-KSA] THE STATUTORY DECISION, RESTATED ON EVERY RUN THAT DISBURSES ONE.
            // End-of-service gratuity, leave encashment and payment in lieu of notice are treated as NOT
            // GOSI-bearing: the contributory wage is the MONTHLY REGISTERED wage (basic + housing,
            // SalaryBreakdown.GosiCoveredWage) and a terminal lump sum is not a monthly wage. This is also
            // mechanically self-enforcing — a settlement run pays no recurring salary, so Process zeroes
            // fullBasic/fullHousing, and settlement components enter NEITHER the basic nor the housing slot
            // of the statutory input; the pack therefore returns zero and POD-A1's reconciliation, which
            // rebuilds `expected` from those same columns plus the bonus/arrears sub-ledgers, still ties out
            // with no change to A1 at all.
            C3Warn("WARN_SETTLEMENT_GOSI_TREATMENT_REQUIRES_SIGNOFF", null,
                $"This run disburses {settlementsDisbursed.Count} termination settlement(s) totalling " +
                $"{settlementsDisbursed.Sum(s => s.GrossPayable):N2} gross. NONE of it is treated as GOSI-bearing: " +
                "the contributory wage is the monthly registered wage, and a terminal lump sum is not one. Whether " +
                "leave encashment specifically is contributory, and whether the last working day requires Mudad " +
                "de-registration in this month, requires a Saudi compliance officer's sign-off. [FLAG-COMPLIANCE-KSA]");
        }
        if (policy.ProratesStatutoryBase && employees.Any(x => prorationByEmp[x.Id].IsProrated))
            C3Warn("WARN_PRORATED_GOSI_BASE_REQUIRES_SIGNOFF", null,
                "This legal entity computes the social-insurance contributory wage on the PRORATED wage in a " +
                "joining/leaving month (payparameter.proration_gosi_base = 'Prorated'). GOSI assesses a MONTHLY " +
                "contributory wage and the portal has no partial-month proration of it, so this may UNDER-REMIT. It is " +
                "also inconsistent with the treatment of unpaid absence, where LOP does not reduce the covered wage. " +
                "Requires a Saudi compliance officer's sign-off. [FLAG-COMPLIANCE-KSA]");

        // POD-B2 (M10) — the WHOLE loan/advance decrement block is gated, not each slip line. A
        // supplemental run collected no EMI (loanEmi/advEmi are 0 above), so decrementing
        // OutstandingBalance / TotalRepaid or marking a LoanInstallment "Paid" here would retire debt the
        // employee never actually repaid.
        // (Pre-existing asymmetry, NOT amplified here: loans are scoped by `employeeIdsForRun` while
        //  bonuses are scoped by `processedEmpIds` further down.)
        //
        // POD-C1 — a SETTLEMENT run reaches this block even though it pays no recurring salary: a leaver's
        // debts are recovered from their final payment. It is still per-loan and driven entirely by
        // loanTakenByEmployee (which only has entries for what was actually withheld), so no non-settling
        // employee's balance can be touched by it.
        var runDecrementsDebt = includesRecurringPay || settlementsDisbursed.Count > 0;
        var activeLoansMutable = !runDecrementsDebt ? new List<EmployeeLoan>() : await _db.EmployeeLoans
            .Where(l => l.TenantId == tenantId && l.Status == "Active" && l.EmployeeIntId != null && employeeIdsForRun.Contains(l.EmployeeIntId.Value) && l.OutstandingBalance > 0
                && (!l.RepaymentStartDate.HasValue || l.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);
        var activeAdvMutable = !runDecrementsDebt ? new List<SalaryAdvance>() : await _db.SalaryAdvances
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null && employeeIdsForRun.Contains(a.EmployeeIntId.Value) && a.OutstandingBalance > 0
                && (!a.RepaymentStartDate.HasValue || a.RepaymentStartDate.Value <= periodEnd))
            .ToListAsync(cancellationToken);

        // POD-B3 — the loan witness is UNCONDITIONAL and PER-LOAN, and it records the balances as they were
        // BEFORE this run touched them. Neither of the two things a void could otherwise reason from is
        // sufficient: the slip carries ONE aggregate LOAN_EMI deduction per EMPLOYEE (`empLoans.Sum(...)`
        // above), so an employee with two loans has no per-loan attribution; and the installment stamp
        // below only happens `if (inst is not null)`, so a loan with no schedule row — exactly what the
        // seeders create — is decremented leaving no trace at all. Recomputing `InstallmentAmount` at void
        // time is not a fallback either: a schedule edited between the run and the void would restore a
        // different number than was taken, silently corrupting 1400/1410 and the sub-ledger together.
        foreach (var loan in activeLoansMutable)
        {
            // POD-C3 (MF-2) — decrement by what the run ACTUALLY took, not by a recomputed instalment.
            // Debt is now capped at the net the period can fund, so recomputing Math.Min(Installment,
            // Outstanding) here would retire more debt than was withheld and put the loan sub-ledger and
            // the 1400 receivable permanently out of step with the payslip.
            var deducted = loanTakenByEmployee.TryGetValue(loan.EmployeeIntId ?? 0, out var lm)
                        && lm.TryGetValue(loan.Id, out var lt)
                ? lt
                : 0m;
            if (deducted <= 0) continue;
            Witness(PayrollConsumptionArtifacts.Loan, loan.Id, loan.EmployeeIntId ?? 0, deducted,
                    loan.Status, loan.OutstandingBalance, loan.TotalRepaid);
            loan.OutstandingBalance -= deducted;
            loan.TotalRepaid += deducted;
            if (loan.OutstandingBalance <= 0) loan.Status = "Closed";
            // Record the paid installment
            var inst = await _db.LoanInstallments
                .OrderBy(i => i.DueDate)
                .FirstOrDefaultAsync(i => i.LoanId == loan.Id && i.Status == "Pending" && i.DueDate <= periodEnd, cancellationToken);
            // POD-C3 — only a FULLY funded instalment closes its schedule row. When the period could not
            // fund the whole instalment the balance was carried forward, so stamping the row "Paid" would
            // understate the remaining schedule while OutstandingBalance (correctly) still carries it.
            if (inst is not null && deducted >= Math.Min(loan.InstallmentAmount, deducted + loan.OutstandingBalance))
            {
                Witness(PayrollConsumptionArtifacts.LoanInstallment, inst.Id, loan.EmployeeIntId ?? 0, deducted,
                        inst.Status, priorAmountPaid: inst.AmountPaid, priorRunId: inst.PayrollRunId);
                inst.Status = "Paid"; inst.PaidDate = DateOnly.FromDateTime(DateTime.UtcNow); inst.PayrollRunId = id; inst.AmountPaid = deducted;
            }
        }
        foreach (var adv in activeAdvMutable)
        {
            // POD-C3 (MF-2) — same rule as loans: decrement what was actually withheld.
            var deducted = advTakenByEmployee.TryGetValue(adv.EmployeeIntId ?? 0, out var am)
                        && am.TryGetValue(adv.Id, out var at)
                ? at
                : 0m;
            if (deducted <= 0) continue;
            Witness(PayrollConsumptionArtifacts.Advance, adv.Id, adv.EmployeeIntId ?? 0, deducted,
                    adv.Status, adv.OutstandingBalance, adv.TotalRepaid);
            adv.OutstandingBalance -= deducted;
            adv.TotalRepaid += deducted;
            if (adv.OutstandingBalance <= 0) adv.Status = "Closed";
            var inst = await _db.AdvanceInstallments
                .OrderBy(i => i.DueDate)
                .FirstOrDefaultAsync(i => i.AdvanceId == adv.Id && i.Status == "Pending" && i.DueDate <= periodEnd, cancellationToken);
            // POD-C3 — same partial-funding rule as loans.
            if (inst is not null && deducted >= Math.Min(adv.InstallmentAmount, deducted + adv.OutstandingBalance))
            {
                Witness(PayrollConsumptionArtifacts.AdvanceInstallment, inst.Id, adv.EmployeeIntId ?? 0, deducted,
                        inst.Status, priorAmountPaid: inst.AmountPaid, priorRunId: inst.PayrollRunId);
                inst.Status = "Paid"; inst.PaidDate = DateOnly.FromDateTime(DateTime.UtcNow); inst.PayrollRunId = id; inst.AmountPaid = deducted;
            }
        }

        // ── POD-C3: apply the 1420 RECOVERY to the sub-ledger, witnessed for the void ────────────────
        // The witness records the PRIOR RecoveredAmount so PayrollVoidService can restore it exactly,
        // using the same replay machinery B3 built for loans and advances. Without it, voiding a
        // replacement run would leave the receivable recorded as recovered by a run that no longer exists.
        if (receivableTakenByEmployee.Count > 0)
        {
            var recoveredIds = receivableTakenByEmployee.Select(r => r.ReceivableId).Distinct().ToList();
            var recoveredRows = await _db.PayrollEmployeeReceivables
                .Where(r => r.TenantId == tenantId && recoveredIds.Contains(r.Id))
                .ToListAsync(cancellationToken);
            foreach (var (rcvId, empId, amount) in receivableTakenByEmployee)
            {
                var row = recoveredRows.FirstOrDefault(r => r.Id == rcvId);
                if (row is null) continue;
                Witness(PayrollConsumptionArtifacts.EmployeeReceivable, row.Id, empId, amount,
                        row.Status, priorAmountPaid: row.RecoveredAmount);
                row.RecoveredAmount = Math.Round(row.RecoveredAmount + amount, 2);
                row.RecoveredByRunId = id;
                row.UpdatedAtUtc = DateTime.UtcNow;
                if (row.RecoveredAmount >= row.Amount) row.Status = PayrollReceivableStatuses.Recovered;
            }
        }

        // ── POD-C1: CONSUME THE SETTLEMENTS THIS RUN DISBURSES ───────────────────────────────────────
        // Stamping the run id is what makes FinalSettlementGlLedger.BuildPayrollClearingAsync find the
        // payable to clear at Lock, and what makes "a settlement can only be disbursed once" enforceable
        // (Approved + PayrollRunId == null is the eligibility predicate everywhere).
        if (settlementsDisbursed.Count > 0)
        {
            var disbursedIds = settlementsDisbursed.Select(s => s.Id).ToList();
            // Tracked copies, re-loaded INSIDE the transaction (the snapshot above is AsNoTracking so it
            // survives a retry). The Approved + PayrollRunId == null predicate is re-applied as a
            // compare-and-swap: if a concurrent settlement run stamped them first, this one takes none.
            var settlementsMutable = await _db.EmployeeFinalSettlements
                .Where(s => s.TenantId == tenantId && disbursedIds.Contains(s.Id)
                         && s.Status == FinalSettlementStatuses.Approved && s.PayrollRunId == null)
                .ToListAsync(cancellationToken);
            if (settlementsMutable.Count != settlementsDisbursed.Count)
                throw new PayrollProcessAbortException(409, new
                {
                    error    = "settlement_consumed_concurrently",
                    message  = $"{settlementsDisbursed.Count - settlementsMutable.Count} settlement(s) selected by this " +
                               "run were disbursed by another payroll run while it was processing. Nothing was written — " +
                               "re-process this run.",
                    expected = settlementsDisbursed.Count,
                    stamped  = settlementsMutable.Count,
                });
            foreach (var s in settlementsMutable)
            {
                Witness(PayrollConsumptionArtifacts.FinalSettlement, s.Id, s.EmployeeId, s.NetPayable, s.Status);
                s.PayrollRunId = id;
                s.Status = FinalSettlementStatuses.Disbursing;
                s.UpdatedAtUtc = DateTime.UtcNow;
            }

            // Leave encashment is only real once the BALANCE is decremented. The pre-C1 endpoint computed
            // an encashment figure and never wrote EmployeeLeaveBalance.Encashed back at all, so the same
            // days could be encashed again on a second call. Witnessed with the PRIOR value so POD-B3's
            // void restores the row exactly rather than subtracting a re-derived number.
            if (settlementEncashmentTaken.Count > 0)
            {
                var balanceIds = settlementEncashmentTaken.Select(x => x.BalanceId).Distinct().ToList();
                var balanceRows = await _db.EmployeeLeaveBalances
                    .Where(b => b.TenantId == tenantId && balanceIds.Contains(b.Id))
                    .ToListAsync(cancellationToken);
                foreach (var (balanceId, empId, days) in settlementEncashmentTaken)
                {
                    var row = balanceRows.FirstOrDefault(b => b.Id == balanceId);
                    if (row is null || days <= 0m) continue;
                    Witness(PayrollConsumptionArtifacts.LeaveEncashment, row.Id, empId, days,
                            priorStatus: null, priorAmountPaid: row.Encashed);
                    row.Encashed = Math.Round(row.Encashed + days, 2);
                    row.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
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
            // POD-C3 — WHICH BASIS paid this month, how many people it touched, and what it settled.
            // "Why is this number what it is?" must be answerable from the audit record alone.
            prorationBasis       = policy.Basis,
            prorationBasisSource = policy.BasisSource,
            gosiBasePolicy       = policy.GosiBase,
            arrearsGosiTreatment = policy.ArrearsGosiTreatment,
            proratedEmployees    = employees.Count(x => prorationByEmp[x.Id].IsProrated),
            leaversPaid          = employees.Count(x => leaverEmployeeIds.Contains(x.Id)),
            finalWageMonths      = slips.Count(s => s.IsFinalWageMonth),
            joinersExcluded      = excludedJoiners.Count,
            arrearsLines         = arrearsLinesToPersist.Count,
            arrearsTotal         = Math.Round(arrearsLinesToPersist.Sum(a => a.Amount), 2),
            arrearsGosiBearing   = Math.Round(arrearsLinesToPersist.Where(a => a.IsGosiBearing).Sum(a => a.Amount), 2),
            arrearsEarnedBasisGosiDelta = Math.Round(arrearsLinesToPersist.Sum(a => a.EarnedBasisGosiDelta), 2),
            receivableRecovered  = Math.Round(receivableTakenByEmployee.Sum(r => r.Amount), 2),
            emiDeferred          = Math.Round(deferredEmiEmployees.Sum(x => x.Amount), 2),
            // POD-C1 — which leavers this run settled, and for how much.
            settlementsDisbursed = settlementsDisbursed.Count,
            settlementGross      = Math.Round(settlementsDisbursed.Sum(s => s.GrossPayable), 2),
            settlementNet        = Math.Round(settlementsDisbursed.Sum(s => s.NetPayable), 2),
            settlementIds        = settlementsDisbursed.Select(s => s.Id).ToList(),
            leaveDaysEncashed    = Math.Round(settlementEncashmentTaken.Sum(x => x.Days), 2),
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
        // ── POD-B3 (D1): the idempotency probe must count LIVE ACCRUAL, not "any Payroll row ever" ─────
        //
        // The pre-B3 predicate was `SourceModule == "Payroll" && SourceEntityId == id` — no EventType, no
        // !IsReversed. A void writes its contras with the SAME SourceModule/SourceEntityId and
        // IsReversed=false (they are the reversal; nothing has reversed THEM), so after a void this probe
        // still answered "already posted". Any path that re-locked the run therefore posted ZERO GL while
        // setting Status=Locked, ErpPostingStatus=ReadyForErp, slips Final and payslips published to ESS —
        // a month that accrues nothing while every payslip asserts a liability, with the only trace a
        // `glPosted:false` buried in the audit metadata.
        //
        // HONEST REACHABILITY (do not oversell this as a live P0): no SHIPPED endpoint reaches that state
        // today. Lock requires Status=="Approved"; the void leaves the run "Voided"; ReopenRun refuses a
        // voided run outright and refuses ANY run carrying GL rows; and a Replacement run is a new Id with
        // no rows of its own, so the old predicate would have been correct for it too. The re-lock is
        // reachable only by writing Status back to "Approved" directly in the database — which is exactly
        // what a remediation sweep, a support data-fix or a future "un-void" transition would do. The
        // predicate is fixed because a GL idempotency probe must be true by construction and not by the
        // accident of which transitions happen to exist this month; it is not fixed because a tenant is
        // sitting on a zero-GL month right now.
        //
        // This is the exact predicate SettlePaymentBatch and RemitStatutory already use, so the three
        // "has this run accrued?" questions now have ONE answer. BonusPayrollClearing needs no clause of
        // its own: those lines are only ever emitted alongside the Accrual earnings/deduction/net lines by
        // BuildPayrollGlEntries, so a journal of clearing lines alone is unreachable.
        var alreadyPosted = await _db.FinanceGlEntries
            .AnyAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == id && x.TenantId == tenantId
                        && x.EventType == GlEventTypes.Accrual && !x.IsReversed, cancellationToken);
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
            // POD-C1 — settlement components this run disburses were expensed at APPROVAL (DR 5110/5111/
            // 5112 / CR 2320). Clear that payable instead of expensing them again; capped by the
            // outstanding payable AND by the run's own settlement earning total, so a settlement with no
            // live accrual is still expensed here and the journal always balances.
            var settlementEarningTotal = earnings
                .Where(e => e.Source == FinalSettlementComponents.SettlementSource).Sum(e => e.Amount);
            glCtx = glCtx with
            {
                BonusClearings = await BonusGlLedger.BuildPayrollClearingAsync(
                    _db, tenantId, id, run.CompanyId, bonusEarningTotal, cancellationToken),
                SettlementClearings = await FinalSettlementGlLedger.BuildPayrollClearingAsync(
                    _db, tenantId, id, settlementEarningTotal, cancellationToken),
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
        // POD-B3 — restate any blocking error an approver consciously OVERRODE, at the last irreversible
        // step before payment. Same doctrine as the hold-out count: an override is only a control if the
        // person locking the month sees it.
        var lockOverrides = await _db.PayrollValidationOverrides.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.PayrollRunId == id)
            .Select(o => new { o.Code, o.EmployeeId, o.Reason, o.OverriddenByName })
            .ToListAsync(cancellationToken);
        await PayrollAudit("payroll.run.locked", "PayrollRun", id.ToString(), new
        {
            glPosted = !alreadyPosted, period,
            runType = run.RunType, includesRecurringPay = run.IncludesRecurringPay,
            payPeriod = $"{run.Year}-{run.Month:D2}", excludedCount = lockExcludedCount,
            parentRunId = run.ParentRunId,
            overriddenCount = lockOverrides.Count, overrides = lockOverrides,
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
            // POD-B3 — `glPosted` was previously visible ONLY inside the audit metadata, so a lock that
            // posted nothing looked identical to one that posted a balanced journal. It is a first-class
            // part of the answer now.
            glPosted            = !alreadyPosted,
            overriddenCount     = lockOverrides.Count,
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
        // POD-C1 — same reason as Process: a settlement run's leavers must be in Validate's eligible set,
        // or Rule 1 MISSING_SALARY_STRUCTURE-style Errors would be raised for people the run legitimately
        // pays, and the run could never be approved or locked.
        var eligibleForValidation = await LoadEligibleEmployeesAsync(
            tenantId, company.Id, allowLegacyUnscopedEmployeesForValidation, asNoTracking: true, cancellationToken,
            includeSettlementLeavers: run.SettlesFinalSettlements);
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

        // ── POD-B3: re-apply the run's durable OVERRIDES to the freshly-rebuilt results ──────────────
        // This delete-and-rebuild is exactly why an override cannot live on the result row: without this
        // call, running /validate after clearing a blocking error would silently resurrect it and re-stick
        // a run the approver had already signed off, with no trace of why it came back.
        var overriddenCount = await ApplyValidationOverridesAsync(tenantId, id, results, cancellationToken);

        var errCount  = results.Count(r => r.Severity == "Error" && !r.IsResolved);
        var warnCount = results.Count(r => r.Severity == "Warning");
        await PayrollAudit("payroll.run.validated", "PayrollRun", id.ToString(),
            new { errorCount = errCount, warningCount = warnCount, overriddenCount }, cancellationToken);
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

        // ── POD-B3: OVERRIDDEN compliance errors must be acknowledged the same way ────────────────────
        // Restating overrides in a response body is not a control — nobody reads a body they did not ask
        // for. A cash-count match is: the approver must state the number of blocking errors that were
        // consciously cleared on this run, and it must equal what the run actually carries. Because
        // /validate rebuilds results wholesale, the count comes from the durable override rows.
        var overriddenErrors = await _db.PayrollValidationOverrides.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.PayrollRunId == id)
            .Select(o => new { o.Code, o.EmployeeId, o.Reason, o.OverriddenByName })
            .ToListAsync(cancellationToken);
        if (overriddenErrors.Count > 0 && req.ExpectedOverriddenCount != overriddenErrors.Count)
            return Conflict(new
            {
                error           = "overridden_errors_not_acknowledged",
                message         = $"This run carries {overriddenErrors.Count} consciously OVERRIDDEN compliance error(s). " +
                                  "Review GET /api/payroll/runs/{id}/validation-overrides and re-submit with " +
                                  $"expectedOverriddenCount={overriddenErrors.Count} to acknowledge them.",
                overriddenCount = overriddenErrors.Count,
                acknowledged    = req.ExpectedOverriddenCount,
                overrides       = overriddenErrors,
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

    /// <summary>
    /// POD-B3 — the acknowledgement/election query parameters follow the same idiom GenerateWps already
    /// uses (<c>acknowledgeReadinessDrift</c> / <c>acknowledgeSiblingWpsExport</c>): an irreversible money
    /// action refuses until the operator has STATED the thing the system cannot know. The reason stays in
    /// the body, mandatory as before.
    ///
    /// <para>CLOSED-PERIOD DOCTRINE (one, stated). The DEFAULT is (a): refuse, listing EVERY closed period
    /// the unwind would write into, and require an audited reopen → void → replace → re-close.
    /// <c>priorPeriodAdjustment=true</c> elects (b): the reversal and the replacement both book into the
    /// current open month carrying the original period as the line reference, so the closed month genuinely
    /// STAYS closed. (b) is the standard controllership answer at 55 tenants; (a) is the default because it
    /// keeps the per-period tie-out exact and is what B1's callers already expect.</para>
    /// </summary>
    [HttpPost("runs/{id:guid}/void")]
    [HasPermission("payroll.lock")]
    public async Task<IActionResult> VoidRun(
        Guid id, [FromBody] PayrollDecisionRequest req, CancellationToken cancellationToken,
        [FromQuery] string? settlementDisposition = null,
        [FromQuery] string? settlementReference = null,
        [FromQuery] string? remittanceDisposition = null,
        [FromQuery] string? remittanceReference = null,
        [FromQuery] bool cascade = false,
        [FromQuery] string? expectedChildRunIds = null,
        [FromQuery] bool acknowledgeErpPosted = false,
        [FromQuery] bool priorPeriodAdjustment = false,
        [FromQuery] string? adjustmentPeriod = null)
    {
        if (string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new
            {
                error   = "reason_required",
                message = "A void reason is required. Voiding a payroll run is an irreversible financial action and must be documented.",
            });

        List<Guid>? expectedChildren = null;
        if (!string.IsNullOrWhiteSpace(expectedChildRunIds))
        {
            expectedChildren = new List<Guid>();
            foreach (var part in expectedChildRunIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var g))
                    return BadRequest(new { error = "invalid_child_run_ids", message = $"'{part}' is not a run id. expectedChildRunIds is a comma-separated GUID list." });
                expectedChildren.Add(g);
            }
        }
        if (priorPeriodAdjustment && !string.IsNullOrWhiteSpace(adjustmentPeriod)
            && !TryParseGlPeriod(adjustmentPeriod.Trim(), out _, out _))
            return BadRequest(new { error = "invalid_adjustment_period", message = "adjustmentPeriod must be formatted 'yyyy-MM'." });

        var tenantId = GetTenantId();
        var options  = new PayrollVoidOptions
        {
            SettlementDisposition = settlementDisposition,
            SettlementReference   = settlementReference,
            RemittanceDisposition = remittanceDisposition,
            RemittanceReference   = remittanceReference,
            Cascade               = cascade,
            ExpectedChildRunIds   = expectedChildren,
            AcknowledgeErpPosted  = acknowledgeErpPosted,
            PriorPeriodAdjustment = priorPeriodAdjustment,
            AdjustmentPeriod      = adjustmentPeriod,
        };
        var result = await new PayrollVoidService(_db).VoidAsync(
            id, tenantId, GetUserId(), GetUserName(), req.Notes, cancellationToken, options);

        if (result.IsNotFound)    return NotFound();
        if (result.IsAlreadyVoid) return Conflict(new { error = "already_voided", message = "This payroll run has already been voided." });
        // POD-B1 (P0-3) — voiding would post contras into a closed GL period; require an audited reopen.
        // POD-B3 — ALL closed periods are listed: a settled+remitted run writes into up to three, and
        // reporting them one at a time turns a recovery into three round-trips.
        if (result.IsPeriodClosed)
            return UnprocessableEntity(new
            {
                error   = "gl_period_closed",
                message = $"GL period(s) {string.Join(", ", result.ClosedPeriods)} are closed. Reopen them " +
                          "(Finance → GL periods) before voiding this run so the reversal is not posted into closed books — " +
                          "or re-submit with priorPeriodAdjustment=true to book the reversal into the current open month " +
                          "as a prior-period adjustment and leave the closed month closed.",
                period  = result.Period,
                periods = result.ClosedPeriods,
            });
        // POD-B3 — the operator must state something before the unwind may proceed (a funds disposition,
        // a cascade over amending runs, an ERP acknowledgement). Refused BEFORE any write, and the
        // transaction is rolled back, so "refused ⇒ untouched" is literally true.
        if (result.IsBlocked)
            return UnprocessableEntity(new
            {
                error   = result.BlockCode,
                message = result.BlockMessage,
                detail  = result.BlockDetail,
            });

        return Ok(new
        {
            runId             = id,
            period            = result.Period,
            status            = "Voided",
            glEntriesReversed = result.GlReversed,
            reason            = req.Notes,
            // POD-B2 — non-voided runs that AMEND this one. POD-B3: cascaded (or the void refused).
            childRunIds       = result.ChildRunIds,
            cascadedRunIds    = result.CascadedChildRunIds,
            // M1 — locked/paid runs later in the same year whose FROZEN YTD now includes a voided month.
            // Recomputing issued payslips is out of B3's scope; leaving a year that cannot tie out
            // unreported is not acceptable, so it is named here and on the chain.
            staleYtdRunIds    = result.StaleYtdRunIds,
            unwind            = result.Unwind,
            nextStep          = $"Create the corrected month with POST /api/payroll/runs " +
                                $"{{ runType: 'Replacement', parentRunId: '{id}' }}.",
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // POD-B3 — REOPEN: the recovery path for a run that never posted GL
    //
    // The other half of the recovery doctrine. A run that has posted a LIVE accrual is recovered by
    // void → Replacement, because Process DELETES the slips/earnings/deductions that are the supporting
    // detail behind journals still visible in the ledger, and because one run cannot carry two ERP
    // documents or two payment batches. But a run that never locked has NO accounting identity: nothing
    // in the ledger references its detail, no payslip was published, no ERP document exists. For that run
    // the honest answer to "my inputs were wrong" is to put it back to Draft and re-process it — and
    // without this endpoint there was no such answer at all: Process refuses any non-Draft run, DeleteRun
    // is Draft-only, and the population selector is frozen from Processed onward, so the ONLY exit from a
    // blocking validation error was to void a run that had never touched the books.
    //
    // Reopen runs the SAME operational unwind as the void (one implementation, so "voided" and "reopened"
    // can never restore different things), then wipes the run's outputs so re-Process starts clean.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [HttpPost("runs/{id:guid}/reopen")]
    [HasPermission("payroll.lock")]
    public async Task<IActionResult> ReopenRun(Guid id, [FromBody] PayrollReasonRequest req, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new
            {
                error   = "reason_required",
                message = "A reason is required to reopen a payroll run: it releases consumed loan installments, " +
                          "attendance/leave/overtime impacts and bonuses, and must be attributable.",
            });

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status == "Voided")
            return BadRequest(new { error = "run_voided", message = "A voided run cannot be reopened. Create a Replacement run for the period instead." });
        if (run.Status is not ("Draft" or "Processed" or "PendingFinanceReview" or "Approved"))
            return BadRequest(new
            {
                error   = "not_reopenable",
                message = $"A '{run.Status}' run cannot be reopened. Void it and create a Replacement run for the period.",
            });

        // The hard line. ANY Payroll GL row — live or already reversed — means this run has an accounting
        // identity that outlives its detail rows, and Process is about to delete those rows. Reversed rows
        // count deliberately: an auditor can still see the original journal and must still be able to see
        // the earnings and deductions behind it.
        var glRowCount = await _db.FinanceGlEntries.CountAsync(
            x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id, cancellationToken);
        if (glRowCount > 0)
            return UnprocessableEntity(new
            {
                error   = "run_has_gl",
                message = $"This run has {glRowCount} GL entr(ies) posted against it, so re-processing it would delete the " +
                          "earnings and deductions those journals are built from. Void it (POST runs/{id}/void) and create a " +
                          "Replacement run — that keeps the bad month frozen and attributable while the corrected month gets " +
                          "its own journal, payment batch and ERP document.",
                glEntryCount = glRowCount,
            });

        var svc = new PayrollVoidService(_db);
        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        object? payload = null;
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            run = await _db.PayrollRuns.FirstAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);

            // 1. Release everything the run consumed — the same replay the void performs.
            var opState = await svc.RestoreOperationalStateAsync(tenantId, run, id, cancellationToken);

            // 2. Re-open the bonuses. Same rules as the void: a batch already paid in CASH is never
            //    re-opened (that would let the next run pay a bonus whose money has left), and a
            //    cancelled batch's children come back as Cancelled, not Approved.
            // IgnoreQueryFilters is intentional: a reopen is a SYSTEM correction over the whole run and
            // must restore every company's bonuses; TenantId + PayrollRunId keeps it tenant- and
            // run-contained and never reads another tenant.
            var reopenBatchIds = await _db.EmployeeBonuses.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.PayrollRunId == id && !b.IsDeleted && b.Status == "PaidInPayroll")
                .Select(b => b.BonusBatchId).Distinct().ToListAsync(cancellationToken);
            // IgnoreQueryFilters is intentional: the cash-payment probe must see a payment posted by ANY
            // company in the batch (CompanyId is a reporting dimension); TenantId + batch ids re-scope it.
            var cashPaid = reopenBatchIds.Count == 0 ? new List<Guid>() : await _db.FinanceGlEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.SourceModule == BonusGlLedger.SourceModule
                         && x.EventType == GlEventTypes.BonusPayment && !x.IsReversed
                         && reopenBatchIds.Contains(x.SourceEntityId))
                .Select(x => x.SourceEntityId).Distinct().ToListAsync(cancellationToken);
            var openable = reopenBatchIds.Except(cashPaid).ToList();
            var bonusesReopened = 0;
            if (openable.Count > 0)
            {
                // IgnoreQueryFilters: same system-correction rationale as the probes above.
                bonusesReopened = await _db.EmployeeBonuses.IgnoreQueryFilters()
                    .Where(b => b.TenantId == tenantId && b.PayrollRunId == id && !b.IsDeleted
                             && b.Status == "PaidInPayroll" && openable.Contains(b.BonusBatchId))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.Status, "Approved")
                        .SetProperty(b => b.PayrollRunId, (Guid?)null), cancellationToken);
                // IgnoreQueryFilters: the batch lock is a BATCH-wide flag; unlocking it must not depend on
                // the actor's company scope. TenantId + batch ids re-apply exact tenant scope.
                await _db.BonusBatches.IgnoreQueryFilters()
                    .Where(x => x.TenantId == tenantId && openable.Contains(x.Id) && !x.IsDeleted
                             && (x.IsLockedByPayroll || x.Status == "Paid"))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, "Approved")
                        .SetProperty(x => x.IsLockedByPayroll, false), cancellationToken);
            }

            // 3. Delete the run's OUTPUTS. Payslips + PayslipComponents are deleted TOO, and that is not
            //    tidiness: Process wipes PayrollSlip but never Payslip, and GeneratePayslips `continue`s
            //    for any employee who already has one — so reopen → re-process → generate-payslips would
            //    be a silent no-op leaving the ESS-visible payslip carrying the PRE-correction gross/net
            //    while PayrollSlip carried the corrected figures.
            var payslipIds = await _db.Payslips.Where(p => p.TenantId == tenantId && p.PayrollRunId == id)
                .Select(p => p.Id).ToListAsync(cancellationToken);
            var componentsDeleted = payslipIds.Count == 0 ? 0 : await _db.PayslipComponents
                .Where(c => c.TenantId == tenantId && payslipIds.Contains(c.PayslipId))
                .ExecuteDeleteAsync(cancellationToken);
            var payslipsDeleted = await _db.Payslips.Where(p => p.TenantId == tenantId && p.PayrollRunId == id)
                .ExecuteDeleteAsync(cancellationToken);
            // The payment batch is deleted rather than voided: CreatePaymentBatch 409s on ANY existing
            // batch for the run, so leaving one behind would mean the reopened run could never get a clean
            // batch. Safe by construction — no GL exists, so no batch of this run was ever settled.
            var batchIds = await _db.PayrollPaymentBatches.Where(b => b.TenantId == tenantId && b.PayrollRunId == id)
                .Select(b => b.Id).ToListAsync(cancellationToken);
            if (batchIds.Count > 0)
            {
                await _db.SIFFileRecords.Where(r => r.TenantId == tenantId
                        && _db.WPSFileBatches.Where(f => f.TenantId == tenantId && batchIds.Contains(f.PaymentBatchId))
                              .Select(f => f.Id).Contains(r.WPSFileBatchId))
                    .ExecuteDeleteAsync(cancellationToken);
                await _db.WPSFileBatches.Where(f => f.TenantId == tenantId && batchIds.Contains(f.PaymentBatchId))
                    .ExecuteDeleteAsync(cancellationToken);
                await _db.PayrollPaymentRecords.Where(r => r.TenantId == tenantId && batchIds.Contains(r.PaymentBatchId))
                    .ExecuteDeleteAsync(cancellationToken);
                await _db.PayrollPaymentBatches.Where(b => b.TenantId == tenantId && b.PayrollRunId == id)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            await _db.PayrollSlips.Where(s => s.TenantId == tenantId && s.RunId == id).ExecuteDeleteAsync(cancellationToken);
            await _db.PayrollRunEmployees.Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ExecuteDeleteAsync(cancellationToken);
            await _db.PayrollEarnings.Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ExecuteDeleteAsync(cancellationToken);
            await _db.PayrollDeductions.Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ExecuteDeleteAsync(cancellationToken);
            await _db.PayrollValidationResults.Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ExecuteDeleteAsync(cancellationToken);
            // The run's facts are about to be re-derived, so a judgement made about the OLD figures is void.
            await _db.PayrollValidationOverrides.Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ExecuteDeleteAsync(cancellationToken);
            // Selection Outcome stamps are cleared (the intent rows themselves are KEPT — that is the
            // hold-out the operator recorded — and Process re-stamps them).
            await _db.PayrollRunEmployeeSelections.Where(x => x.TenantId == tenantId && x.PayrollRunId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Outcome, (string?)null), cancellationToken);

            // 4. Back to Draft — which is also what unfreezes the population selector
            //    (PopulationLockedStatuses), so "exclude this employee and re-run" becomes reachable.
            var statusBefore = run.Status;
            run.Status              = "Draft";
            run.ProcessedAtUtc      = null;
            run.ProcessedByUserId   = null;
            run.EmployeeCount       = 0;
            run.TotalGrossSalary    = 0m;
            run.TotalDeductions     = 0m;
            run.TotalNetSalary      = 0m;
            run.TotalEmployerStatutoryCost = 0m;
            run.ErpPostingStatus    = ErpPostingStatuses.NotReady;
            run.ErpPostingStatusChangedAtUtc = DateTime.UtcNow;

            payload = new
            {
                runId = id, statusBefore, status = "Draft", reason = req.Reason,
                bonusesReopened, payslipsDeleted, componentsDeleted,
                paymentBatchesDeleted = batchIds.Count,
                opState.LoansRestored, opState.AdvancesRestored, opState.InstallmentsReopened,
                opState.ImpactsReleased, opState.AdjustmentsReleased,
                consumptionWitnessed = opState.WitnessRowCount > 0,
                loanRestores = opState.LoanRestoreDetail,
            };
            // M3 — the audit row and the business writes land in ONE SaveChanges, so POD-A3's sealer
            // stamps Seq/PreviousHash/EntryHash over state that is already committed-or-rolled-back
            // together. A separate save would let the chain record a reopen the transaction abandoned.
            await PayrollAudit("payroll.run.reopened", "PayrollRun", id.ToString(), payload, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });

        return Ok(payload);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // POD-B3 — VALIDATION OVERRIDE: the audited exit from a blocking compliance error
    //
    // PayrollValidationResult.IsResolved was declared, read at Approve/Lock/overview/readiness — and set
    // by NOTHING. These endpoints are the writer, with four controls around them so an override can never
    // become a silent bypass:
    //   1. a mandatory, substantive REASON, on the tamper-evident payroll audit chain with the actor;
    //   2. IDENTITY-based segregation of duties — the person who PROCESSED or CREATED the run may not
    //      clear its errors. Permission-based SoD would be theatre here: AuthSeeder grants BOTH
    //      payroll.write and payroll.approve to Payroll Manager AND HR Manager, so "requires
    //      payroll.approve" excludes nobody who actually runs payroll;
    //   3. a published DENY LIST (default-deny) — codes that assert an arithmetic impossibility or a
    //      cross-run double payment are not judgements and can never be overridden;
    //   4. an acknowledgement gate at Approve (expectedOverriddenCount), so the sign-off is conscious.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [HttpGet("runs/{id:guid}/validation-overrides")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Finance Controller,Finance Approver,Auditor")]
    public async Task<IActionResult> ListValidationOverrides(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var rows = await _db.PayrollValidationOverrides.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.PayrollRunId == id)
            .OrderBy(o => o.Code).ThenBy(o => o.EmployeeId)
            .Select(o => new { o.Id, o.Code, o.EmployeeId, o.Reason, o.OverriddenByUserId, o.OverriddenByName, o.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            runId          = id,
            overrides      = rows,
            overridableCodes    = PayrollValidationOverridePolicy.Overridable.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            nonOverridableCodes = PayrollValidationOverridePolicy.NonOverridable.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        });
    }

    /// <summary>
    /// POD-B3 — clears ONE blocking validation error on a run, with a mandatory reason, on the
    /// hash-chained payroll audit trail. Gated on payroll.approve AND on the actor not being the run's
    /// preparer (see the block comment above for why the permission alone is not a control).
    /// </summary>
    [HttpPost("runs/{id:guid}/validation/{resultId:guid}/resolve")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> ResolveValidationResult(
        Guid id, Guid resultId, [FromBody] PayrollReasonRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, cancellationToken);
        if (run is null) return NotFound();
        var result = await _db.PayrollValidationResults
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.PayrollRunId == id && x.Id == resultId, cancellationToken);
        if (result is null) return NotFound();

        var guard = GuardValidationOverride(run, result.Code, req.Reason);
        if (guard is not null) return guard;

        var actorId   = GetUserId();
        var actorName = GetUserName();
        var reason    = req.Reason!.Trim();

        // Upsert the DURABLE record — the result row itself is rebuilt wholesale by the next /validate.
        var existing = await _db.PayrollValidationOverrides.FirstOrDefaultAsync(
            o => o.TenantId == tenantId && o.PayrollRunId == id && o.Code == result.Code
              && o.EmployeeId == result.EmployeeId, cancellationToken);
        if (existing is null)
            _db.PayrollValidationOverrides.Add(new PayrollValidationOverride
            {
                TenantId = tenantId, CompanyId = run.CompanyId, PayrollRunId = id,
                EmployeeId = result.EmployeeId, Code = result.Code, Reason = reason,
                OverriddenByUserId = actorId, OverriddenByName = actorName,
            });
        else
        {
            existing.Reason = reason;
            existing.OverriddenByUserId = actorId;
            existing.OverriddenByName = actorName;
            existing.CreatedAtUtc = DateTime.UtcNow;
        }

        // Every currently-stored result carrying this (code, employee) is cleared, not just the one named:
        // Process and /validate can each raise the same code, and clearing one instance while an identical
        // twin still blocks would look like the override "did not work".
        var affected = await _db.PayrollValidationResults
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.Code == result.Code
                     && x.EmployeeId == result.EmployeeId)
            .ToListAsync(cancellationToken);
        foreach (var r in affected)
        {
            r.IsResolved      = true;
            r.ResolvedByUserId = actorId;
            r.ResolvedByName   = actorName;
            r.ResolvedAtUtc    = DateTime.UtcNow;
            r.ResolvedReason   = reason;
        }

        await PayrollAudit("payroll.validation.overridden", "PayrollRun", id.ToString(), new
        {
            code = result.Code, employeeId = result.EmployeeId, severity = result.Severity,
            reason, resultsCleared = affected.Count, actorName,
            runStatus = run.Status, processedByUserId = run.ProcessedByUserId,
        }, cancellationToken);
        // One SaveChanges: the override, the cleared results and the chain row commit together (M3).
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            runId = id, code = result.Code, employeeId = result.EmployeeId,
            resolved = true, resultsCleared = affected.Count, reason,
            resolvedBy = actorName, resolvedAtUtc = DateTime.UtcNow,
        });
    }

    /// <summary>POD-B3 — removes an override, re-instating the block. Same SoD and audit as setting one.</summary>
    [HttpDelete("runs/{id:guid}/validation-overrides/{overrideId:guid}")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> RevokeValidationOverride(Guid id, Guid overrideId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var row = await _db.PayrollValidationOverrides
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.PayrollRunId == id && o.Id == overrideId, cancellationToken);
        if (row is null) return NotFound();

        var affected = await _db.PayrollValidationResults
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.Code == row.Code && x.EmployeeId == row.EmployeeId)
            .ToListAsync(cancellationToken);
        foreach (var r in affected)
        {
            r.IsResolved = false; r.ResolvedByUserId = null; r.ResolvedByName = null;
            r.ResolvedAtUtc = null; r.ResolvedReason = null;
        }
        _db.PayrollValidationOverrides.Remove(row);
        await PayrollAudit("payroll.validation.override_revoked", "PayrollRun", id.ToString(),
            new { code = row.Code, employeeId = row.EmployeeId, resultsReblocked = affected.Count, actorName = GetUserName() }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { runId = id, code = row.Code, employeeId = row.EmployeeId, revoked = true, resultsReblocked = affected.Count });
    }

    /// <summary>
    /// POD-B3 — the four gates every override must pass. Extracted so set and revoke can never drift, and
    /// so the refusal messages name the ACTUAL exit rather than leaving the operator stuck.
    /// </summary>
    private IActionResult? GuardValidationOverride(PayrollRun run, string code, string? reason)
    {
        if (run.Status is "Locked" or "Paid" or "Voided")
            return BadRequest(new
            {
                error   = "run_not_overridable",
                message = $"A '{run.Status}' run's validation results are historical — overriding them changes nothing " +
                          "and would misrepresent the sign-off. Void the run and create a Replacement instead.",
            });

        // (3) Published deny list, DEFAULT-DENY: a code nobody has classified is not overridable.
        if (!PayrollValidationOverridePolicy.IsOverridable(code))
            return UnprocessableEntity(new
            {
                error   = "code_not_overridable",
                code,
                message = PayrollValidationOverridePolicy.NonOverridable.Contains(code)
                    ? OverrideRefusalMessage(code)
                    : $"'{code}' is not on the published overridable list, so it cannot be cleared by decision. " +
                      "Fix the underlying data and re-run POST runs/{id}/validate, or reopen the run.",
                overridableCodes = PayrollValidationOverridePolicy.Overridable.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            });

        // (1) A substantive reason. "ok" is not accountability.
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < PayrollValidationOverridePolicy.MinimumReasonLength)
            return BadRequest(new
            {
                error   = "reason_required",
                message = $"A reason of at least {PayrollValidationOverridePolicy.MinimumReasonLength} characters is required " +
                          "to override a blocking compliance error. It is recorded, with your identity, on the tamper-evident " +
                          "payroll audit chain.",
            });

        // (2) IDENTITY-based segregation of duties. See the block comment above these endpoints for why
        //     the payroll.approve permission alone excludes nobody.
        var actorId = GetUserId();
        if (actorId is not null && (run.ProcessedByUserId == actorId || run.CreatedByUserId == actorId))
            return StatusCode(403, new
            {
                error   = "maker_checker_violation",
                message = "The user who created or processed this run cannot clear its compliance errors. " +
                          "A different approver must review and accept the exception (maker-checker policy).",
            });

        return null;
    }

    private static string OverrideRefusalMessage(string code) => code switch
    {
        "ALREADY_PAID_THIS_PERIOD" =>
            "ALREADY_PAID_THIS_PERIOD can never be overridden: it is the only control in this product that looks ACROSS " +
            "runs, and clearing it authorises paying one person two full salaries in one month — which the next step " +
            "wires through WPS. Take one of the exits the error itself names: exclude the employee from this run " +
            "(reopen the run first, which unfreezes the population selector), switch this run to a supplemental basis, " +
            "or void the other run.",
        "NEGATIVE_NET" or "ZERO_NET_WITH_GROSS" =>
            $"{code} states an arithmetic fact about a payslip, not a business judgement. Correct the deductions and " +
            "re-process (POST runs/{id}/reopen, then process).",
        "GL_WILL_NOT_BALANCE" or "TOTALS_GROSS_MISMATCH" or "TOTALS_DEDUCTIONS_MISMATCH" or "TOTALS_NET_MISMATCH" =>
            $"{code} means the journal this run would post does not balance. Overriding it does not accept a risk, it " +
            "produces a corrupt ledger. Reopen and re-process the run so the totals are recomputed.",
        "DUPLICATE_EMPLOYEE" =>
            "DUPLICATE_EMPLOYEE means one employee has two payslips in this run — a data fault with exactly one correct " +
            "answer. Reopen and re-process the run.",
        _ =>
            $"{code} is on the published non-overridable list: it asserts an impossibility rather than a judgement. " +
            "Fix the underlying data, then reopen and re-process the run.",
    };

    /// <summary>
    /// POD-B3 — re-applies the run's durable overrides onto a freshly-built result set. Called by
    /// /validate, which deletes and rebuilds every result row: without it an override would survive
    /// exactly until the next validate and the run would silently re-stick.
    /// </summary>
    private async Task<int> ApplyValidationOverridesAsync(
        Guid tenantId, Guid runId, IReadOnlyList<PayrollValidationResult> results, CancellationToken ct)
    {
        var overrides = await _db.PayrollValidationOverrides.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.PayrollRunId == runId)
            .ToListAsync(ct);
        if (overrides.Count == 0) return 0;

        var applied = 0;
        foreach (var r in results)
        {
            var o = overrides.FirstOrDefault(x => x.Code == r.Code && x.EmployeeId == r.EmployeeId);
            if (o is null) continue;
            r.IsResolved       = true;
            r.ResolvedByUserId = o.OverriddenByUserId;
            r.ResolvedByName   = o.OverriddenByName;
            r.ResolvedAtUtc    = o.CreatedAtUtc;
            r.ResolvedReason   = o.Reason;
            applied++;
        }
        return applied;
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
                var driver = EarningDriverKeyFor(glCtx.Drivers, grp.Key, first.Source);
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
                var driverRow = ResolveDeductionDriverRow(glCtx.Drivers, grp.Key.ComponentCode, grp.Key.Source);
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

        // ── POD-B3: the RECOVERY CHAIN ────────────────────────────────────────────────────────────────
        // A recovered month legitimately has two journals in the ledger — the original (now reversed) and
        // the replacement's — plus the contras and any reclassification between them. Without this,
        // "why does August have two payroll journals and a receivable?" is a question only someone who
        // reads raw ledger rows can answer. It is the same walk the void performs, surfaced.
        var replacementRun = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ParentRunId == id
                     && r.RunType == PayrollRunTypes.Replacement && r.Status != "Voided")
            .Select(r => new { r.Id, r.Status, r.RunType })
            .FirstOrDefaultAsync(cancellationToken);
        var replacedRun = run.ParentRunId is Guid parentId && run.RunType == PayrollRunTypes.Replacement
            ? await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Id == parentId)
                .Select(r => new { r.Id, r.Status, r.RunType })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var journalsByEvent = posted
            .GroupBy(e => e.EventType)
            .Select(g => new
            {
                eventType = g.Key,
                period    = g.Select(x => x.Period).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                lines     = g.Count(),
                live      = g.Count(x => !x.IsReversed),
                debits    = g.Where(x => !string.IsNullOrEmpty(x.DebitAccount)).Sum(x => x.Amount),
                credits   = g.Where(x => !string.IsNullOrEmpty(x.CreditAccount)).Sum(x => x.Amount),
            })
            .OrderBy(x => x.eventType, StringComparer.Ordinal)
            .ToList();

        return Ok(new
        {
            runId    = id,
            period   = $"{run.Year}-{run.Month:D2}",
            isPosted, // true = immutable as-posted ledger; false = live preview through current mappings
            entries  = entries.Select(e => new { componentCode = e.Code, componentName = e.Name, glAccount = e.Account, glAccountName = e.AccountName, entryType = e.EntryType, amount = e.Amount }),
            totalDebits, totalCredits,
            isBalanced = Math.Abs(totalDebits - totalCredits) < 0.01m,
            // POD-B3 recovery chain: what this run is, what it replaced, what replaced it, and every
            // journal the ledger holds for it (accrual, settlement, remittance, contras, reclassification).
            recovery = new
            {
                runType     = run.RunType,
                runStatus   = run.Status,
                isVoided    = run.Status == "Voided",
                replacedRun,
                replacementRun,
                journals    = journalsByEvent,
            },
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

        // POD-B3 — REFRESH-idempotent, not skip-if-exists.
        //
        // The old `continue` was a silent-wrong-numbers bug the moment a run could be corrected: Process
        // wipes PayrollSlip/PayrollEarning/PayrollDeduction but NEVER Payslip/PayslipComponent, so a
        // reopened + re-processed run kept its pre-correction ESS payslip forever while PayrollSlip
        // carried the corrected figures — the employee's own view of their pay and the payroll register
        // disagreeing, permanently, with nothing reporting it. The Payslip ROW is kept (its number and
        // template version are its identity, and DownloadSlipPdf links to it); only its components are
        // rebuilt from the current lines.
        var existingPayslips = await _db.Payslips
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken);

        // POD-B3 — a payslip generated AFTER the run is locked must still reach ESS.
        //
        // Lock is the ONLY writer of IsPublishedToEss (it publishes every payslip that exists at that
        // moment) and the void is the only un-publisher. Generating slips afterwards therefore produced
        // rows the employee could never see — permanently, with nothing reporting it. That is a live trap
        // in exactly the flow this pod exists to make safe: the recovery order is void → Replacement run →
        // process → lock → payment batch (CreatePaymentBatch requires a Locked run, so "lock, then produce
        // the documents" is the natural operator sequence), and the void has already un-published the bad
        // month's payslips. Generate-after-lock would leave the employee with NO visible payslip for the
        // month at all — the corrected one invisible, the original withdrawn.
        //
        // Publishing is gated on the run being at or past Lock, so this can never publish a Draft or
        // Processed run early, and never re-publishes a Voided run (the void sets Status="Voided", which
        // is not in this set) — the un-publish stays sticky exactly as intended.
        var publishRun = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        var publishToEss = publishRun is not null && publishRun.Status is "Locked" or "Paid";

        var refreshed = 0;
        var published = 0;
        foreach (var slip in slips)
        {
            var payslip = existingPayslips.FirstOrDefault(x => x.EmployeeId == slip.EmployeeId);
            if (payslip is null)
            {
                payslip = new Payslip { TenantId = tenantId, PayrollRunId = id, EmployeeId = slip.EmployeeId, PayslipNumber = $"PS-{slip.EmployeeCode}-{DateTime.UtcNow:yyyyMMddHHmmss}", PayslipTemplateId = defaultTemplate };
                _db.Payslips.Add(payslip);
            }
            else
            {
                await _db.PayslipComponents
                    .Where(c => c.TenantId == tenantId && c.PayslipId == payslip.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                refreshed++;
            }
            foreach (var e in earnings.Where(x => x.EmployeeId == slip.EmployeeId))
                _db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Earning", ComponentName = e.ComponentName, Amount = e.Amount });
            foreach (var d in deductions.Where(x => x.EmployeeId == slip.EmployeeId))
                _db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Deduction", ComponentName = d.ComponentName, Amount = d.Amount });
            _db.PayslipComponents.Add(new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Net", ComponentName = "Net pay", Amount = slip.NetSalary });

            // Both branches are tracked entities (existingPayslips is loaded WITHOUT AsNoTracking, new rows
            // are Added), so one SaveChanges below covers refreshed and newly-created slips alike.
            if (publishToEss && !payslip.IsPublishedToEss)
            {
                payslip.IsPublishedToEss = true;
                payslip.PublishedAtUtc   = DateTime.UtcNow;
                published++;
            }
        }
        await PayrollAudit("payroll.payslips.generated", "PayrollRun", id.ToString(),
            new { generated = slips.Count - refreshed, refreshed, publishedToEss = published }, cancellationToken);
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
        // L2: prevent duplicate payment batches on the same run.
        // POD-B3 — a batch VOIDED by the run's void is not a live batch and must not block. (In practice a
        // voided run cannot reach here at all — the Locked guard above refuses it — but the predicate is
        // the honest one, and it is what lets a voided run's batch stay on the record instead of being
        // deleted to make room.)
        if (await _db.PayrollPaymentBatches.AnyAsync(
                x => x.TenantId == tenantId && x.PayrollRunId == id && x.WpsStatus != WpsStatuses.Voided, cancellationToken))
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
            PayrollRunTypes.Replacement   => "RP",   // POD-B3
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

        // ── POD-B3 (M9, recovery case): the VOIDED parent's file is still at the bank ─────────────────
        // LoadSiblingRunsAsync excludes voided runs, so the guard above goes SILENT for a Replacement —
        // exactly when the warning matters most. Voiding a run withdraws the filing IN THIS SYSTEM; it
        // does not retrieve the SIF from the bank or Mudad, and they still treat the second file for the
        // (establishment, salary month) as a replacement of the first. So the replacement probes its
        // parent's filing state DIRECTLY. `IsFiled` (not the batch's WpsStatus, which the void set to
        // Voided) is the test: the question is whether a file was ever handed over, not what we call the
        // batch now — hence SubmissionReference is deliberately preserved by the void.
        if (run.ParentRunId is Guid replacedRunId && run.RunType == PayrollRunTypes.Replacement && !acknowledgeSiblingWpsExport)
        {
            var parentFilings = await _db.WPSFileBatches.AsNoTracking()
                .Where(f => f.TenantId == tenantId
                         && _db.PayrollPaymentBatches
                                .Where(b => b.TenantId == tenantId && b.PayrollRunId == replacedRunId)
                                .Select(b => b.Id).Contains(f.PaymentBatchId))
                .Select(f => new { f.Id, f.SifFileName, f.FilingStatus, f.SubmissionReference, f.SubmittedAtUtc, f.TotalSalaryAmount })
                .ToListAsync(cancellationToken);
            var filed = parentFilings.Where(f => f.SubmittedAtUtc is not null || !string.IsNullOrWhiteSpace(f.SubmissionReference)).ToList();
            if (filed.Count > 0)
                return UnprocessableEntity(new
                {
                    error   = "replaced_run_wps_export_exists",
                    message = $"The run this replaces already had a WPS/SIF file SUBMITTED for {run.Year}-{run.Month:D2} " +
                              "(reference(s) below). Voiding that run withdrew the filing in this system — it did not " +
                              "retrieve the file from the bank/Mudad, which will treat this new SIF as a REPLACEMENT of " +
                              "the first. Confirm with your bank how the corrected file should be lodged, then re-submit " +
                              "with acknowledgeSiblingWpsExport=true.",
                    period        = $"{run.Year}-{run.Month:D2}",
                    replacedRunId,
                    priorFilings  = filed,
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

        // ── POD-B3: a VOIDED run's batch is terminal ──────────────────────────────────────────────────
        // This endpoint had NO run-status guard, and WpsTransitions allows Accepted → Paid. Before B3 a
        // void merely reverted the batch Paid → Accepted, which parked it on exactly that edge: an
        // operator could mark a voided run's batch "Paid" — money left the account, per the status —
        // with ZERO live GL behind it and no payslip that is not itself voided.
        var wpsRun = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batch.PayrollRunId, cancellationToken);
        if (wpsRun?.Status == "Voided" || batch.WpsStatus == WpsStatuses.Voided)
            return UnprocessableEntity(new
            {
                error   = "run_voided",
                message = "This payment batch belongs to a VOIDED payroll run: its WPS/SIF filing has been withdrawn " +
                          "and its status is terminal. Create a Replacement run for the period and generate a new batch.",
                runId   = batch.PayrollRunId,
            });

        var from = batch.WpsStatus;

        var latestFile = await _db.WPSFileBatches
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batchId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // ── POD-C1 (K7): a SETTLEMENT batch may be disbursed by ORDINARY BANK TRANSFER ───────────────
        // Before this pod the ONLY road to Paid ran through a generated SIF: this method 400s
        // `wps_file_missing` without one, WpsTransitions admits only Draft→Generated→Submitted→Accepted,
        // and SettlePaymentBatch refuses anything but Accepted. Filing an end-of-service lump sum through
        // the wage-protection file for an employee whose REGISTERED CONTRACT WAGE is a fraction of it is a
        // wage-file mismatch, and end-of-service is customarily paid by ordinary transfer — so the SIF-only
        // road either forces a wrong filing or leaves the settlement permanently unsettleable.
        //
        // The carve-out is deliberately NARROW and is NOT an edge in WpsTransitions (WpsTests asserts that
        // table, and a batch must never be drivable to Accepted by hand on the normal wage path):
        //   • the run must be a settlement-purpose run, and
        //   • the batch must be an explicitly non-WPS payment method, and
        //   • a bank reference is mandatory (it replaces the Mudad acknowledgement as the evidence), and
        //   • only Draft/Generated → Accepted is admitted; every other transition still goes through the
        //     table above.
        // [FLAG-COMPLIANCE-KSA] Whether a given tenant's settlements must nonetheless ride the Mudad SIF is
        // a Saudi compliance determination; this makes both roads reachable and auditable instead of
        // silently mandating one.
        var isNonWpsSettlementBatch =
            wpsRun?.SettlesFinalSettlements == true
            && !string.Equals(batch.PaymentMethod, "WPS", StringComparison.OrdinalIgnoreCase);
        if (isNonWpsSettlementBatch && req.Status == WpsStatuses.Accepted
            && (from == WpsStatuses.Draft || from == WpsStatuses.Generated))
        {
            if (string.IsNullOrWhiteSpace(req.Reference))
                return BadRequest(new
                {
                    error   = "bank_reference_required",
                    message = "A bank transfer reference is required to accept a non-WPS settlement batch — " +
                              "it is the evidence that replaces the WPS/Mudad acknowledgement.",
                });
            batch.WpsStatus = WpsStatuses.Accepted;
            batch.WpsStatusChangedAtUtc = DateTime.UtcNow;
            batch.WpsSubmissionReference = req.Reference;
            if (latestFile is not null)
            {
                latestFile.FilingStatus = WpsStatuses.Accepted;
                latestFile.SubmissionReference = req.Reference;
                latestFile.AcknowledgedAtUtc = DateTime.UtcNow;
            }
            await PayrollAudit("payroll.wps.status_changed", "PayrollPaymentBatch", batchId.ToString(),
                new
                {
                    from, to = req.Status, reference = req.Reference, notes = req.Notes,
                    route = "non_wps_settlement_bank_transfer", paymentMethod = batch.PaymentMethod,
                }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(new { batchId, wpsStatus = batch.WpsStatus, route = "non_wps_settlement_bank_transfer" });
        }

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

            // POD-D4 — Reconciled is the terminal "the month landed" state, and until now it asked only
            // whether a NetSettlement row EXISTED. It must also be unreachable while the bank has told us
            // money came back, or has not told us anything at all: a returned salary means Cash/Bank is
            // overstated for that employee, and an unconfirmed record means nobody knows.
            var d4Records = await _db.PayrollPaymentRecords.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batch.Id)
                .Select(x => new { x.EmployeeId, x.Amount, x.Status })
                .ToListAsync(cancellationToken);
            var d4Failed = d4Records.Where(x => PaymentRecordStatuses.IsFailed(x.Status)).ToList();
            var d4Unconfirmed = d4Records
                .Where(x => !PaymentRecordStatuses.IsConfirmed(x.Status) && !PaymentRecordStatuses.IsCancelled(x.Status))
                .ToList();
            if (d4Failed.Count > 0 || d4Unconfirmed.Count > 0)
                return UnprocessableEntity(new
                {
                    error   = "payments_not_reconciled",
                    message = "This batch cannot reach Reconciled while payments are returned or unconfirmed by the "
                            + "bank. Import the bank/WPS response (POST payment-batches/{id}/bank-confirmations) and "
                            + "resolve any returns first — reconciling now would close the month over money that "
                            + "came back or was never confirmed.",
                    returnedCount = d4Failed.Count,
                    returnedTotal = d4Failed.Sum(x => x.Amount),
                    unconfirmedCount = d4Unconfirmed.Count,
                    unconfirmedTotal = d4Unconfirmed.Sum(x => x.Amount),
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

        // ── POD-B3: a VOIDED run has no journal to export ─────────────────────────────────────────────
        // The probe below required only that SOME Payroll GL row existed — and a void's CONTRAS qualify —
        // so a voided run could still be driven all the way to ErpPostingStatus=Posted, exporting a
        // journal that had already been reversed. Both halves are fixed: the run must not be voided, and
        // the run must carry a LIVE accrual (the same predicate Lock/settle/remit use).
        if (run.Status == "Voided")
            return UnprocessableEntity(new
            {
                error   = "run_voided",
                message = "This payroll run has been VOIDED — its accrual is reversed, so there is no journal to export. " +
                          "Create a Replacement run for the period and export that instead.",
            });

        var glEntries = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id)
            .ToListAsync(cancellationToken);
        if (!glEntries.Any(x => x.EventType == GlEventTypes.Accrual && !x.IsReversed))
            return BadRequest(new { error = "gl_not_posted", message = "Balanced payroll GL must be persisted before ERP posting status can change." });

        var from = run.ErpPostingStatus;
        if (!ErpPostingTransitions.IsAllowed(from, req.Status))
            return BadRequest(new { error = "invalid_transition", message = $"Cannot transition ERP posting status from '{from}' to '{req.Status}'.", allowedTransitions = ErpPostingTransitions.AllowedFrom(from) });

        // ── POD-D4: Exported and Posted now require EVIDENCE, not a free-text string ──────────────────
        // This endpoint would write Posted off any non-empty Reference and stamp it onto every GL row of
        // the run, with no artifact, no file and no hash — nothing that was ever exported anywhere. The two
        // states that ASSERT something happened outside this system are therefore no longer producible by
        // hand: an export produces a real, tied-out, hash-verified journal artifact, and a confirmation
        // against that artifact carries the ERP's own document number. ReadyForErp and Rejected are
        // operational intent and stay here unchanged.
        if (req.Status is ErpPostingStatuses.Exported or ErpPostingStatuses.Posted)
            return UnprocessableEntity(new
            {
                error   = "evidence_required",
                message = $"'{req.Status}' can no longer be set by hand: it asserts that a journal was produced and "
                        + "accepted by an ERP, so it must be backed by an artifact. Produce the journal with "
                        + "POST /api/finance/gl/journal-exports (period or runId), hand the file to the ERP, then "
                        + "record the ERP's document number with "
                        + "POST /api/finance/gl/journal-exports/{exportId}/confirm. Both statuses are then derived "
                        + "from the per-line ledger evidence.",
                exportEndpoint  = "/api/finance/gl/journal-exports",
                confirmEndpoint = "/api/finance/gl/journal-exports/{exportId}/confirm",
                allowedHere     = new[] { ErpPostingStatuses.ReadyForErp, ErpPostingStatuses.Rejected },
            });
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

        // P1-7 — do not book cash that never left: block while any payment record has FAILED (bounced IBAN /
        // returned credit / refused instruction). Pending records are fine (WPS instructed but not cleared).
        //
        // POD-D4: this guard was dead. It tested Status == "Rejected", a literal NO code path ever wrote —
        // the only writers were "Pending" at batch creation and "Cancelled" by the void — so a bounced
        // salary could never block a settlement. It now reads the shared PaymentRecordStatuses.IsFailed
        // predicate, which the bank-confirmation import actually produces (Returned as well as Rejected).
        var failedStatuses = await _db.PayrollPaymentRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batch.Id)
            .Select(x => x.Status)
            .ToListAsync(cancellationToken);
        var rejectedCount = failedStatuses.Count(PaymentRecordStatuses.IsFailed);
        if (rejectedCount > 0)
            return UnprocessableEntity(new { error = "payment_records_rejected", message = $"{rejectedCount} payment record(s) were returned or rejected by the bank. Resolve/retry them before settling so Cash/Bank reflects the actual outflow.", rejectedCount });

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

        // ── POD-C1: the settlements this batch actually PAID ─────────────────────────────────────────
        // This is the ONLY place a settlement reaches Paid, and it is the ordinary net-pay settlement
        // journal that does it — no second payment mechanism anywhere in the pipeline.
        var settlementResult = await FinalizePaidSettlementsAsync(
            tenantId, run, batch, glCtx, settlementPeriod, cancellationToken);

        await PayrollAudit("payroll.batch.settled", "PayrollPaymentBatch", batch.Id.ToString(),
            new
            {
                runId = run.Id, batch.BatchNumber, amount = cr, cashAccount, period = settlementPeriod,
                reference = req.Reference,
                settlementsPaid = settlementResult.SettlementIds,
                residualDebtReclassed = settlementResult.ResidualReclassed,
                residualDebtUnbooked  = settlementResult.ResidualUnbooked,
            }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            batchId = batch.Id, runId = run.Id, wpsStatus = batch.WpsStatus, settled = cr, cashAccount,
            period = settlementPeriod,
            settlementsPaid = settlementResult.SettlementIds,
            residualDebtReclassed = settlementResult.ResidualReclassed,
            residualDebtUnbooked  = settlementResult.ResidualUnbooked,
        });
    }

    /// <summary>POD-C1 — what <see cref="FinalizePaidSettlementsAsync"/> did, for the response + audit.</summary>
    private sealed record PaidSettlementResult(
        List<Guid> SettlementIds, decimal ResidualReclassed, decimal ResidualUnbooked);

    /// <summary>
    /// POD-C1 — completes every settlement this payment batch actually paid.
    ///
    /// <para>Called from <c>SettlePaymentBatch</c> and NOWHERE else, so the ONLY way a settlement reaches
    /// <c>Paid</c> is the ordinary POD-B1 net-pay settlement journal (DR 2100 / CR Cash-Bank) clearing it
    /// like any other payroll. Three things happen here that nothing else in the product does:</para>
    /// <list type="number">
    /// <item><b>The offboarding checklist becomes truthful.</b> <c>EmployeeOffboarding.FinalSettlementDone</c>
    ///   gates the salary-structure deactivation cascade in <c>OffboardingController.Complete</c> and was
    ///   previously a manual tick-box nothing verified. It is now set by the act of paying.</item>
    /// <item><b>The leaver's RESIDUAL debt is dispositioned.</b> A loan the settlement could not fully
    ///   recover would otherwise sit <c>Active</c> forever on an ex-employee who is in NO run population,
    ///   with 1400 carrying an asset nobody can collect and nothing reporting it (the control-account
    ///   health check asserts SIGN only, so it would never fire). It is reclassified to the 1420 Employee
    ///   Overpayment Receivable — the ageing sub-ledger POD-C3 already built and already surfaces at
    ///   GET /api/payroll/receivables — so it is chased rather than forgiven.</item>
    /// <item><b>The reclass is CAPPED at what 1400/1410 actually carries.</b> Loans predating the
    ///   disbursement GL, and seeded Active loans with a real balance and no journal at all, have NO
    ///   1400 debit behind them; debiting 1420 for those would recognise an asset out of nothing, so the
    ///   uncovered part is REPORTED (<c>ResidualDebtUnbooked</c>) and the loan stays open. The GL cannot
    ///   reclassify what it never booked, and pretending otherwise is how a receivable ends up in credit.</item>
    /// </list>
    /// </summary>
    private async Task<PaidSettlementResult> FinalizePaidSettlementsAsync(
        Guid tenantId, PayrollRun run, PayrollPaymentBatch batch, GlResolutionContext glCtx,
        string settlementPeriod, CancellationToken ct)
    {
        var settlements = await _db.EmployeeFinalSettlements
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id
                     && s.Status == FinalSettlementStatuses.Disbursing)
            .ToListAsync(ct);
        if (settlements.Count == 0)
            return new PaidSettlementResult(new List<Guid>(), 0m, 0m);

        var employeeIds = settlements.Select(s => s.EmployeeId).Distinct().ToList();
        var offboardingIds = settlements.Select(s => s.OffboardingId).Distinct().ToList();
        var offboardings = await _db.EmployeeOffboardings
            .Where(o => o.TenantId == tenantId && offboardingIds.Contains(o.Id))
            .ToListAsync(ct);

        var loanReceivableAccount    = GlAccountResolver.AccountLabel("LOAN_RECEIVABLE", glCtx);
        var advanceReceivableAccount = GlAccountResolver.AccountLabel("ADVANCE_RECEIVABLE", glCtx);
        var employeeReceivableAccount = GlAccountResolver.AccountLabel(
            GlControlAccounts.EmployeeReceivableDriver, glCtx);
        // AvailableForRelief, never the raw scoped balance: the unattributed (CompanyId == null) pool sits
        // inside every company's view while the relief posted against it is stamped with the relieving
        // company, so two entities reading the same pool would each relieve it in full and drive the ASSET
        // into credit. Same clamp POD-B1b's loan remittance uses.
        var loanBalance    = await GlControlAccounts.LoadAsync(_db, tenantId, run.CompanyId, loanReceivableAccount, ct);
        var advanceBalance = await GlControlAccounts.LoadAsync(_db, tenantId, run.CompanyId, advanceReceivableAccount, ct);
        var reclassBudget = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            [loanReceivableAccount]    = Math.Max(0m, loanBalance.AvailableForRelief),
            [advanceReceivableAccount] = Math.Max(0m, advanceBalance.AvailableForRelief),
        };

        var loans = await _db.EmployeeLoans
            .Where(l => l.TenantId == tenantId && l.Status == "Active" && l.EmployeeIntId != null
                     && employeeIds.Contains(l.EmployeeIntId.Value) && l.OutstandingBalance > 0)
            .ToListAsync(ct);
        var advances = await _db.SalaryAdvances
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null
                     && employeeIds.Contains(a.EmployeeIntId.Value) && a.OutstandingBalance > 0)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        decimal totalReclassed = 0m, totalUnbooked = 0m;

        // Returns how much of the residual was actually MOVED off the loan/advance receivable (and is
        // therefore no longer owed on the loan itself); the remainder stays where it is.
        decimal Reclass(EmployeeFinalSettlement s, string receivableAccount, decimal residual, string what)
        {
            if (residual <= 0m) return 0m;
            var budget = reclassBudget.GetValueOrDefault(receivableAccount);
            var covered = Math.Min(residual, budget);
            var uncovered = Math.Round(residual - covered, 2);
            if (covered > 0m)
            {
                reclassBudget[receivableAccount] = budget - covered;
                _db.FinanceGlEntries.Add(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = s.CompanyId ?? run.CompanyId,
                    SourceModule = FinalSettlementGlDescriptions.SourceModule,
                    SourceEntityId = s.Id,
                    SourceEntityRef = EosbProvisionLedger.EmployeeRef(s.EmployeeId),
                    EventType = GlEventTypes.SettlementResidualReclass,
                    DebitAccount = employeeReceivableAccount, CreditAccount = receivableAccount,
                    Amount = covered, Currency = s.Currency,
                    EntryDate = today, Period = settlementPeriod,
                    Description = $"{FinalSettlementGlDescriptions.ResidualReclassPrefix}{what} — {s.EmployeeCode}",
                    PostedBy = GetUserId(), PostedByName = GetUserName(),
                });
                // The per-employee sub-ledger behind the aggregate 1420 debit, so the residual AGES on
                // GET /api/payroll/receivables and can be netted into any future run the same way POD-C3
                // nets a void's overpayment. Σ(sub-ledger rows written here) == the 1420 debit posted here.
                _db.PayrollEmployeeReceivables.Add(new PayrollEmployeeReceivable
                {
                    TenantId = tenantId, CompanyId = s.CompanyId ?? run.CompanyId,
                    EmployeeId = s.EmployeeId, EmployeeCode = s.EmployeeCode,
                    SourceRunId = run.Id, EventType = GlEventTypes.SettlementResidualReclass,
                    Period = settlementPeriod, Amount = covered,
                    Status = PayrollReceivableStatuses.Outstanding,
                });
                s.ResidualDebtReclassed = Math.Round(s.ResidualDebtReclassed + covered, 2);
                totalReclassed += covered;
            }
            if (uncovered > 0m)
            {
                s.ResidualDebtUnbooked = Math.Round(s.ResidualDebtUnbooked + uncovered, 2);
                totalUnbooked += uncovered;
            }
            return covered;
        }

        foreach (var s in settlements)
        {
            s.Status = FinalSettlementStatuses.Paid;
            s.PaymentBatchId = batch.Id;
            s.PaidAtUtc = DateTime.UtcNow;
            s.UpdatedAtUtc = DateTime.UtcNow;

            var off = offboardings.FirstOrDefault(o => o.Id == s.OffboardingId);
            if (off is not null && !off.FinalSettlementDone)
            {
                off.FinalSettlementDone = true;
                off.UpdatedAtUtc = DateTime.UtcNow;
            }

            // Only the RECLASSIFIED part leaves the loan. What could not be reclassified is genuinely still
            // owed on the loan itself — that is the honest state, and it keeps the 1420 sub-ledger equal to
            // the 1420 GL debit, which is the invariant POD-C3 built the sub-ledger to hold.
            foreach (var l in loans.Where(l => l.EmployeeIntId == s.EmployeeId && l.OutstandingBalance > 0m))
            {
                var moved = Reclass(s, loanReceivableAccount, Math.Round(l.OutstandingBalance, 2), "loan");
                if (moved <= 0m) continue;
                l.OutstandingBalance = Math.Round(l.OutstandingBalance - moved, 2);
                l.TotalRepaid = Math.Round(l.TotalRepaid, 2);   // unchanged: a reclass is not a repayment
                if (l.OutstandingBalance <= 0m) l.Status = "Closed";
            }
            foreach (var a in advances.Where(a => a.EmployeeIntId == s.EmployeeId && a.OutstandingBalance > 0m))
            {
                var moved = Reclass(s, advanceReceivableAccount, Math.Round(a.OutstandingBalance, 2), "advance");
                if (moved <= 0m) continue;
                a.OutstandingBalance = Math.Round(a.OutstandingBalance - moved, 2);
                if (a.OutstandingBalance <= 0m) a.Status = "Closed";
            }

            await PayrollAudit("payroll.final_settlement.paid", "EmployeeFinalSettlement", s.Id.ToString(), new
            {
                s.EmployeeId, s.EmployeeCode, s.NetPayable, s.Currency,
                runId = run.Id, batchId = batch.Id, period = settlementPeriod,
                residualReclassed = s.ResidualDebtReclassed, residualUnbooked = s.ResidualDebtUnbooked,
            }, ct);
        }

        return new PaidSettlementResult(
            settlements.Select(s => s.Id).ToList(),
            Math.Round(totalReclassed, 2), Math.Round(totalUnbooked, 2));
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
        // POD-B3 — a voided run's settlement has already been dispositioned by the void (reversed on a
        // funds recall, or reclassified to a receivable because the money genuinely left). Reversing it
        // again here would credit Cash/Bank back a second time on cash that never came back.
        if (run?.Status == "Voided")
            return UnprocessableEntity(new
            {
                error   = "run_voided",
                message = "This run has been VOIDED; its net-pay settlement was already dispositioned by the void " +
                          "(reversed on a funds recall, or reclassified as recoverable). There is nothing further to reverse.",
            });

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
        // POD-B3 — see ReverseSettlement: a voided run's remittance was already dispositioned by the void.
        if (run.Status == "Voided")
            return UnprocessableEntity(new
            {
                error   = "run_voided",
                message = "This run has been VOIDED; its statutory remittance was already dispositioned by the void " +
                          "(reversed on a funds recall, or reclassified as prepaid). There is nothing further to reverse.",
            });

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
        var batches = await query.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);

        // POD-D4 — batch.WpsStatus is set to Paid AT SETTLEMENT, which happens BEFORE the bank's response
        // exists (Submitted → Accepted → settle(Paid) → bank file comes back with returns). Returning the
        // bare status therefore reports "Paid" over money that bounced. The per-employee confirmation
        // coverage rides alongside it so a batch with open returns can never present as terminal-clean.
        var d4BatchIds = batches.Select(b => b.Id).ToList();
        var d4Records = d4BatchIds.Count == 0
            ? new List<PayrollPaymentRecord>()
            : await _db.PayrollPaymentRecords.AsNoTracking()
                .Where(r => r.TenantId == tenantId && d4BatchIds.Contains(r.PaymentBatchId))
                .ToListAsync(cancellationToken);

        return Ok(batches.Select(b =>
        {
            var cov = Infrastructure.Finance.BankConfirmationService.Coverage(
                d4Records.Where(r => r.PaymentBatchId == b.Id).ToList());
            return new
            {
                b.Id, b.TenantId, b.PayrollRunId, b.BatchNumber, b.PaymentMethod, b.TotalAmount, b.Currency,
                b.Status, b.WpsStatus, b.WpsStatusChangedAtUtc, b.WpsSubmissionReference, b.WpsRejectionReason,
                b.CreatedAtUtc,
                unresolvedReturns = new { count = cov.FailedCount, total = cov.FailedTotal },
                unconfirmedPayments = new { count = cov.UnconfirmedCount, total = cov.UnconfirmedTotal },
                confirmationCoverage = cov.ConfirmationCoverage,
                presentsCleanButIsNot = b.WpsStatus is WpsStatuses.Paid or WpsStatuses.Reconciled
                                        && (cov.FailedCount > 0 || cov.UnconfirmedCount > 0),
            };
        }).ToList());
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
        // POD-D4 — the bank's latest word on each record, so a returned salary is visible HERE and not only
        // in the confirmation history. Status alone used to be "Pending" forever: nothing ever wrote Paid.
        var d4Confirmations = await _db.BankPaymentConfirmations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.PaymentBatchId == id)
            .ToListAsync(cancellationToken);
        return Ok(records.Select(r =>
        {
            var latest = d4Confirmations.Where(c => c.PaymentRecordId == r.Id)
                .OrderByDescending(c => c.ImportedAtUtc).FirstOrDefault();
            return new
            {
                r.Id,
                r.EmployeeId,
                r.Amount,
                r.Status,
                r.WpsReference,
                Iban = canSeeIban ? r.Iban : Infrastructure.Payroll.SifFileGenerator.MaskIban(r.Iban),
                isReturned = PaymentRecordStatuses.IsFailed(r.Status),
                bankConfirmed = PaymentRecordStatuses.IsConfirmed(r.Status),
                returnReasonCode = latest is null || latest.Outcome != BankConfirmationOutcomes.Returned ? null : latest.ReasonCode,
                returnReasonText = latest is null || latest.Outcome != BankConfirmationOutcomes.Returned ? null : latest.ReasonText,
                bankReference = canSeeIban ? latest?.BankReference : null,
                bankValueDate = latest?.ValueDate,
                bankConfirmedAtUtc = latest?.ImportedAtUtc,
            };
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

    /// <summary>
    /// POD-C3 — THE AUDITOR'S VIEW of a run's retro/arrears. The payslip shows the employee one itemised
    /// line per covered period; this shows the arithmetic behind every one of them — what they were
    /// ENTITLED to for that period, what was actually PAID, what earlier runs already SETTLED, the
    /// assignment whose effective date caused it, and the earned-basis GOSI delta a compliance officer
    /// would need for an amended declaration.
    /// </summary>
    [HttpGet("runs/{id:guid}/arrears")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> GetRunArrears(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();

        // PendingRecovery lines carry no PayrollRunId (they were never paid), so they are matched by the
        // covered period instead — a retro DECREASE the run REFUSED must still be visible from the run
        // an operator was trying to process.
        var settled = await _db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.PayrollRunId == id)
            .OrderBy(a => a.EmployeeId).ThenBy(a => a.CoveredYear).ThenBy(a => a.CoveredMonth).ThenBy(a => a.ComponentCode)
            .ToListAsync(cancellationToken);
        var pending = await _db.PayrollArrearsLines.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == PayrollArrearsStatuses.PendingRecovery
                     && (a.CompanyId == run.CompanyId || a.CompanyId == null))
            .OrderBy(a => a.EmployeeId).ThenBy(a => a.CoveredYear).ThenBy(a => a.CoveredMonth)
            .ToListAsync(cancellationToken);

        object Project(PayrollArrearsLine a) => new
        {
            a.Id, a.EmployeeId, a.EmployeeCode,
            coveredPeriod = $"{a.CoveredYear}-{a.CoveredMonth:D2}",
            a.ComponentCode,
            component = PayrollArrearsComponents.Label(a.ComponentCode),
            a.EntitledAmount, a.PaidAmount, a.PreviouslySettledAmount, a.Amount,
            a.IsGosiBearing, a.EarnedBasisGosiDelta,
            a.Basis, a.ProrationFactor, a.SourceAssignmentId, a.SourceEffectiveDate, a.Status, a.CreatedAtUtc,
        };

        return Ok(new
        {
            runId = id,
            period = $"{run.Year}-{run.Month:D2}",
            settlesArrears = run.SettlesArrears,
            formula = "arrears = entitled(period) − paid(period) − previously settled(period). " +
                      "'paid' is Σ PayrollEarning over the NON-VOIDED runs of that period, keyed on component code " +
                      "(never the slip header, which folds overtime/bonus/adjustments into OtherAllowances). " +
                      "Running the same run twice yields zero the second time because the third term absorbs the first result.",
            totalSettled = Math.Round(settled.Where(a => a.Status == PayrollArrearsStatuses.Settled).Sum(a => a.Amount), 2),
            totalGosiBearing = Math.Round(settled.Where(a => a.Status == PayrollArrearsStatuses.Settled && a.IsGosiBearing).Sum(a => a.Amount), 2),
            totalEarnedBasisGosiDelta = Math.Round(settled.Sum(a => a.EarnedBasisGosiDelta), 2),
            earnedBasisNote = "[FLAG-COMPLIANCE-KSA] Arrears ride inside the CURRENT month's single statutory ceiling, " +
                              "so an employee already at the cap contributes nothing on them. The earned-basis figure is " +
                              "what each covered month's own ceiling headroom would have allowed, and is what an amended " +
                              "GOSI declaration would be prepared from.",
            lines = settled.Select(Project).ToList(),
            pendingRecovery = pending.Select(Project).ToList(),
        });
    }

    /// <summary>
    /// POD-C3 — the AGEING view of the per-employee 1420 Employee Overpayment Receivable sub-ledger.
    ///
    /// <para>POD-B3 posted an aggregate <c>DR 1420</c> whenever a void left already-disbursed cash
    /// standing, and explicitly handed the netting to C3. FinanceGlEntry has no employee dimension, so
    /// without this sub-ledger the balance could never be attributed, never be recovered, and never be
    /// chased. An employee held OUT of a replacement run by the B2 selector still owes it — which is why
    /// this view is keyed on the receivable, not on any run's population.</para>
    /// </summary>
    [HttpGet("receivables")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> GetEmployeeReceivables(
        [FromQuery] Guid? companyId, [FromQuery] bool includeRecovered = false, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var q = _db.PayrollEmployeeReceivables.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (companyId.HasValue) q = q.Where(r => r.CompanyId == companyId || r.CompanyId == null);
        if (!includeRecovered) q = q.Where(r => r.Status != PayrollReceivableStatuses.Recovered);
        var rows = await q.OrderBy(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(new
        {
            outstandingTotal = Math.Round(rows.Sum(r => r.Amount - r.RecoveredAmount), 2),
            unattributedTotal = Math.Round(rows.Where(r => r.Status == PayrollReceivableStatuses.Unattributed)
                                               .Sum(r => r.Amount - r.RecoveredAmount), 2),
            note = "Recovery is netted into a run only when that run sets netsPriorReceivable=true, capped at the " +
                   "employee's net before recovery, and it CREDITS 1420 (not a payable). A residual is carried here " +
                   "and ages — it is never forgiven.",
            rows = rows.Select(r => new
            {
                r.Id, r.EmployeeId, r.EmployeeCode, r.SourceRunId, r.EventType, r.Period,
                r.Amount, r.RecoveredAmount, outstanding = Math.Round(r.Amount - r.RecoveredAmount, 2),
                r.Status, r.RecoveredByRunId, r.CreatedAtUtc,
                ageDays = today.DayNumber - DateOnly.FromDateTime(r.CreatedAtUtc).DayNumber,
            }).ToList(),
        });
    }

    /// <summary>
    /// POD-C3 — appends the proration narrative to a component's display name, e.g.
    /// "Basic salary (18/30 days · 30-day month · joined 2026-11-13)".
    ///
    /// <para>Requirement 7: an employee and an auditor must both be able to see WHY the number is what it
    /// is. Because the note is EMPTY for anyone employed the whole period, this is the identity function
    /// on every existing payslip — which is what lets it be applied on both the legacy emission block and
    /// the component engine's output without changing either.</para>
    /// </summary>
    private static string WithProrationNote(string name, string note)
        => string.IsNullOrEmpty(note) ? name : $"{name} ({note})";

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
            // POD-C3 — arrears are the SAME expense as the component they settle, just paid late. Routing
            // them to EARN:OTHER (5099) would move a retro basic-salary increase out of Basic Salary
            // Expense and quietly distort every payroll cost report. The GL stays balanced either way —
            // this is about the expense landing in the account it belongs to.
            PayrollArrearsComponents.Basic           => "EARN:BASIC",
            PayrollArrearsComponents.Housing         => "EARN:HOUSING",
            PayrollArrearsComponents.Transport       => "EARN:TRANSPORT",
            PayrollArrearsComponents.OtherAllowances => "EARN:OTHER_ALLOWANCES",
            // POD-C1 — the termination settlement's own earnings. Before this pod there was no
            // end-of-service account in the catalog at all, so gratuity had nowhere to land but 5099
            // Other Earnings — if it ever reached the GL, which it did not.
            FinalSettlementComponents.Gratuity        => "EARN:EOSB",
            FinalSettlementComponents.LeaveEncashment => "EARN:LEAVE_ENCASHMENT",
            FinalSettlementComponents.NoticePay       => "EARN:NOTICE_PAY",
            _                  => "EARN:OTHER",
        };

    /// <summary>
    /// POD-C3-FIX — the ONE earning-driver resolution both the posted journal and the on-screen preview
    /// use. Extracted because the two call sites previously did
    /// <c>ResolveDriverForComponent(...)?.Key ?? EarningDriverKey(...)</c> inline, and that composition
    /// silently DEFEATED the arrears routing above on every provisioned tenant:
    ///
    /// <para><c>EARN:BASIC</c> is seeded <c>Exact "BASIC"</c> (FinanceGl.cs:166) so it does NOT match
    /// <c>ARREARS_BASIC</c>; the catch-all <c>EARN:OTHER</c> is seeded <c>Any</c> (FinanceGl.cs:171) and
    /// therefore does. A persisted driver is preferred over the compiled switch, so a retro basic-salary
    /// increase landed in <c>5099 Other Earnings</c> for every tenant that has gl_drivers rows — i.e.
    /// every tenant created through <c>GlDriverSeeder</c>. The journal still balanced; the EXPENSE was in
    /// the wrong account, which is precisely what the switch above says must not happen.</para>
    ///
    /// <para><b>The rule, in precedence order.</b>
    /// <list type="number">
    /// <item>A driver that CLAIMS the arrears code by a specific predicate (Exact/Prefix/Suffix) wins
    /// outright — that is a tenant deliberately giving retro pay its own account, a shipped capability
    /// (GlPhase2Tests.cs:226), and this pod must not take it away.</item>
    /// <item>Otherwise an <c>ARREARS_*</c> code resolves EXACTLY AS THE COMPONENT IT SETTLES resolves —
    /// through the tenant's own driver for BASIC/HOUSING/TRANSPORT/OTHER_ALLOWANCES, and therefore
    /// through any account remap applied to it. Deliberately NOT four new seeded driver keys: a tenant
    /// who remapped <c>EARN:BASIC</c> to their own chart would then have arrears keep posting to the
    /// seeded 5001 — the same divergence in a new disguise. Following the source component's driver is
    /// correct by construction and needs no data migration.</item>
    /// <item>Everything else is unchanged: persisted driver, else the compiled switch.</item>
    /// </list></para>
    /// </summary>
    private static string EarningDriverKeyFor(IReadOnlyList<GlDriverRow> drivers, string componentCode, string source)
    {
        var direct = ResolveDriverForComponent(drivers, componentCode, source, GlDriverCategories.Earning);
        if (direct is not null && direct.MatchMode != GlDriverMatchModes.Any) return direct.Key;

        // ── POD-C1 — the SAME defect, one pod later, and it would have been silent ────────────────────
        // EARN:OTHER is seeded `Any` with MatchSource = null (FinanceGl.cs), so on every tenant that HAS
        // gl_drivers rows but has not re-run POST /api/finance/gl/seed-defaults since this pod — i.e. all
        // ~55 on day one, because GlDriverSeeder is add-only and runs only when invoked — it MATCHES
        // EOSB_GRATUITY / LEAVE_ENCASHMENT / NOTICE_PAY, `direct` is non-null, and the compiled switch
        // below is never consulted. The gratuity would land in 5099 Other Earnings, the journal would
        // still balance, and nothing would report it. Adding the codes to EarningDriverKey alone is dead
        // code on that path; this branch is what makes it live. A tenant that deliberately gave the
        // settlement its own account by a SPECIFIC predicate still wins — that is the rule above.
        if (FinalSettlementComponents.IsSettlementEarning(componentCode))
            return EarningDriverKey(componentCode, source);

        var settles = PayrollArrearsComponents.SourceComponent(componentCode);
        if (!string.Equals(settles, componentCode, StringComparison.Ordinal))
            return ResolveDriverForComponent(drivers, settles, source, GlDriverCategories.Earning)?.Key
                   ?? EarningDriverKey(settles, source);

        return direct?.Key ?? EarningDriverKey(componentCode, source);
    }

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
            // POD-C3 — recovery of a prior void's disbursed net pay CREDITS the 1420 receivable, so it
            // must never fall through to DED:OTHER (2199): crediting a payable nobody owes would leave
            // 1420 ageing forever, which is precisely the defect B3 handed to C3.
            ("Recovery", _)      => "DED:RECEIVABLE_RECOVERY",
            // POD-C1 — a settlement-side deduction (notice shortfall under Art. 75/76, other final
            // deductions) is NOT a debt owed to a third party: it REDUCES what the employer owes. Routing
            // it to DED:OTHER (2199) would credit a payable that RemitGroups.ForSource has no remittance
            // path for, so it would age on the balance sheet forever with nothing able to clear it — the
            // same shape of defect POD-C3 fixed for the 1420 recovery. It credits a CONTRA-EXPENSE (5113)
            // instead, so net employment cost is right and every account this pod touches returns to zero.
            (FinalSettlementComponents.SettlementSource, _) => "DED:SETTLEMENT_RECOVERY",
            _ => componentCode switch
            {
                "FIXED_DEDUCTION"     => "DED:FIXED_DEDUCTION",
                PayrollRecoveryComponents.ReceivableRecovery => "DED:RECEIVABLE_RECOVERY",
                FinalSettlementComponents.NoticeShortfall    => "DED:SETTLEMENT_RECOVERY",
                FinalSettlementComponents.OtherDeduction     => "DED:SETTLEMENT_RECOVERY",
                _                     => "DED:OTHER",
            },
        };
    }

    /// <summary>
    /// POD-C1 — the deduction-side twin of <see cref="EarningDriverKeyFor"/>'s precedence rule, shared by
    /// the posted journal and the on-screen preview.
    ///
    /// <para>DED:OTHER is seeded <c>Any</c> with <c>MatchSource = null</c> (FinanceGl.cs), so on any tenant
    /// with gl_drivers rows that has not re-seeded since this pod it CLAIMS <c>NOTICE_SHORTFALL</c> and
    /// <c>SETTLEMENT_DED_OTHER</c> and sends them to 2199 — a payable nothing ever clears. Nulling an
    /// <c>Any</c>-mode claim on a settlement deduction code drops the caller through to the compiled
    /// switch, which routes it to the contra-expense. A driver that claims the code by a SPECIFIC
    /// predicate (Exact/Prefix/Suffix) is a deliberate tenant decision and still wins.</para>
    /// </summary>
    private static GlDriverRow? ResolveDeductionDriverRow(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source)
    {
        var row = ResolveDriverForComponent(drivers, componentCode, source, GlDriverCategories.Deduction);
        if (row is not null && row.MatchMode == GlDriverMatchModes.Any
            && FinalSettlementComponents.IsSettlementDeduction(componentCode)
            && !string.Equals(row.Key, "DED:SETTLEMENT_RECOVERY", StringComparison.Ordinal))
            return null;
        return row;
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
        // POD-C1 — a settlement's earnings were ALREADY expensed when the settlement was approved
        // (DR 5110/5111/5112 / CR 2320). This run PAYS it, so the correct debit is the payable's own
        // stored account, not a second expense. Identical doctrine to the bonus clearing above; the plan
        // is capped by the outstanding payable AND by the run's own settlement earning total, so a
        // settlement injected into a run with no accrual is still expensed here and the journal always
        // balances.
        var settlementClearings = gl.SettlementClearings;
        var clearsSettlement = settlementClearings.Count > 0;
        decimal settlementEarningTotal = 0m;
        var deferredSettlementGroups = new List<(string Code, string Driver, decimal Amount)>();
        foreach (var grp in earnings.GroupBy(e => e.ComponentCode))
        {
            var src = grp.First().Source;
            var driverKey = EarningDriverKeyFor(gl.Drivers, grp.Key, src);
            if (clearsSettlement && src == FinalSettlementComponents.SettlementSource)
            {
                var settlementAmount = grp.Sum(e => e.Amount);
                settlementEarningTotal += settlementAmount;
                deferredSettlementGroups.Add((grp.Key, driverKey, settlementAmount));
                continue;   // re-emitted below as payable clearing + un-accrued remainder
            }
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

        // ── POD-C1: the settlement payable clearing ───────────────────────────────────────────────────
        // DR the payable once per settlement. EventType is SettlementPayrollClearing (NOT Accrual) and the
        // settlement id rides in SourceEntityRef, so FinalSettlementGlLedger can tell exactly how much of
        // each payable this run consumed. It is an ORIGINATING journal, so a run void contras it like any
        // other payroll line (GlEventTypes.IsOriginatingPayrollJournal) and 2320 automatically re-opens —
        // which is precisely what lets the settlement be re-disbursed on a fresh OffCycle run.
        if (clearsSettlement)
        {
            decimal clearedSettlement = 0m;
            foreach (var c in settlementClearings)
            {
                if (c.Amount <= 0m) continue;
                clearedSettlement += c.Amount;
                lines.Add(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = c.CompanyId ?? gl.CompanyId,
                    SourceModule = "Payroll", SourceEntityId = runId,
                    SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(c.SettlementId),
                    EventType = GlEventTypes.SettlementPayrollClearing,
                    DebitAccount = c.PayableAccount, CreditAccount = string.Empty,
                    Amount = c.Amount, Currency = currency,
                    EntryDate = today, Period = period,
                    Description = $"{FinalSettlementGlDescriptions.PayrollClearingPrefix}{c.SettlementId}",
                    PostedBy = postedBy, PostedByName = postedByName,
                });
            }
            // Whatever the run pays that no live payable covers is a genuine expense of this run, debited
            // to each component's OWN driver (never one collapsed line), so a settlement paid without an
            // accrual — a seeder, a legacy fixture — behaves exactly like an ordinary earning.
            var settlementRemainder = Math.Round(settlementEarningTotal - clearedSettlement, 2);
            if (settlementRemainder > 0m && deferredSettlementGroups.Count > 0 && settlementEarningTotal > 0m)
            {
                var ordered = deferredSettlementGroups.OrderBy(g => g.Code, StringComparer.Ordinal).ToList();
                var slice = settlementRemainder;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var share = i == ordered.Count - 1
                        ? slice
                        : Math.Min(slice, Math.Round(settlementRemainder * (ordered[i].Amount / settlementEarningTotal), 2));
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
        }

        // ── Deductions (Credit side) ──────────────────────────────────────────
        // Employer-expense pairs accumulate per paired-expense driver key so a client-defined pair
        // (via a custom driver) balances the same way the system DED:STATUTORY_ER row does.
        var employerExpenseByPairKey = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var grp in deductions.GroupBy(d => new { d.ComponentCode, d.Source }))
        {
            var driverRow = ResolveDeductionDriverRow(gl.Drivers, grp.Key.ComponentCode, grp.Key.Source);
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

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  POD-C1 — THE TERMINATION SETTLEMENT PIPELINE
    //
    //  POD-A2 made the end-of-service NUMBER authoritative and left this half open: /final-settlement
    //  persisted no payable, posted no GL and produced no payment. It computed a pro-rata wage from
    //  scratch (a naive day-of-month fraction that ignored ProrationCalculator, the proration policy and
    //  the joining date), an encashment it never wrote back to the leave balance, and a notice deduction
    //  it applied without asking who terminated — then returned JSON and one audit line. Every leaver was
    //  settled by spreadsheet plus a manual bank transfer.
    //
    //  The endpoint keeps its URL and returns a SUPERSET of its old body (every field at the same path),
    //  so no consumer breaks — but it now PERSISTS a first-class, auditable payable that is approved,
    //  accrued to 2320, disbursed through an OffCycle run + payment batch, and cleared by POD-B1's
    //  ordinary net-pay settlement. Zero spreadsheets, zero manual journals.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes and PERSISTS a leaver's final settlement as a Draft payable (idempotent per offboarding).
    /// </summary>
    [HttpPost("final-settlement")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> FinalSettlement([FromBody] FinalSettlementRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == req.EmployeeId && !e.IsDeleted, cancellationToken);
        if (employee is null) return NotFound(new { message = "Employee not found." });

        // ── GUARD 2: CANNOT SETTLE SOMEONE WHO IS NOT ACTUALLY LEAVING ───────────────────────────────
        // Pre-C1 this endpoint would happily "settle" a fully Active employee with no offboarding record
        // at all — there was no leaver check anywhere in the method. The last working day is taken FROM
        // THE OFFBOARDING RECORD, never from the request body, so a caller cannot invent one.
        var offboarding = await _db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.EmployeeId == req.EmployeeId && o.Status != "Cancelled")
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (offboarding is null || offboarding.LastWorkingDay == default)
            return BadRequest(new
            {
                error   = "not_a_leaver",
                message = $"{employee.FullName} has no active offboarding record with a last working day, so there " +
                          "is nothing to settle. Raise the offboarding (POST /api/offboarding) first — the last " +
                          "working day is taken from that record and never from this request.",
                employeeId = req.EmployeeId,
            });

        // ── F8: EXPLICIT COMPANY RESOLUTION ──────────────────────────────────────────────────────────
        // A null CompanyId would silently change which GlAccountMapping overrides apply and which
        // period-close row binds, so it is resolved server-side exactly the way a payroll run resolves it
        // and refused rather than guessed.
        var (settlementCompanyId, companyError) = await ResolveSettlementCompanyAsync(tenantId, employee, cancellationToken);
        if (companyError is not null) return UnprocessableEntity(companyError);

        // ── OPEN ITEM: FAIL LOUD WHEN EOSB IS NOT ENABLED ────────────────────────────────────────────
        // /eosb/calculate refuses outright when EosbEnabled is false while the pre-C1 /final-settlement
        // silently computed ZERO gratuity. That asymmetry was survivable while the figure was only
        // displayed; accruing a real payable with a silent zero gratuity is not.
        var gcc = await _db.GCCComplianceSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (gcc is null || !gcc.EosbEnabled)
            return UnprocessableEntity(new
            {
                error   = "eosb_not_enabled",
                message = "End-of-service is not enabled for this tenant, so a settlement would accrue a payable " +
                          "with a silent ZERO gratuity. Enable EOSB in GCC Settings (or record the amount as an " +
                          "explicit other-dues line) before settling.",
            });

        var existing = await _db.EmployeeFinalSettlements
            .Where(s => s.TenantId == tenantId && s.OffboardingId == offboarding.Id
                     && s.Status != FinalSettlementStatuses.Cancelled)
            .FirstOrDefaultAsync(cancellationToken);
        // ── GUARD 1: CANNOT SETTLE TWICE ─────────────────────────────────────────────────────────────
        // A Draft/PendingApproval settlement is RE-COMPUTED in place (the operator is still iterating);
        // once it has accrued, it is immutable and must be cancelled (which contras the journal) first.
        if (existing is not null && FinalSettlementStatuses.HasAccrued(existing.Status))
            return Conflict(new
            {
                error   = "settlement_already_exists",
                message = $"A settlement for this separation already exists in '{existing.Status}' status and has " +
                          "posted its accrual journal. Cancel it (which posts the contra) before computing a new one.",
                settlementId = existing.Id,
                status       = existing.Status,
            });

        var plan = await BuildFinalSettlementPlanAsync(
            tenantId, employee, offboarding, gcc, settlementCompanyId, req, cancellationToken);

        var settlement = existing ?? new EmployeeFinalSettlement
        {
            TenantId = tenantId,
            CompanyId = settlementCompanyId,
            EmployeeId = employee.Id,
            OffboardingId = offboarding.Id,
            CreatedByUserId = GetUserId(),
            CreatedByName = GetUserName(),
        };
        ApplyPlan(settlement, plan);
        if (existing is null) _db.EmployeeFinalSettlements.Add(settlement);
        else
        {
            _db.FinalSettlementLines.RemoveRange(
                _db.FinalSettlementLines.Where(l => l.TenantId == tenantId && l.SettlementId == settlement.Id));
            settlement.UpdatedAtUtc = DateTime.UtcNow;
        }
        var lineRows = plan.Lines.Select((l, i) =>
        {
            l.TenantId = tenantId;
            l.SettlementId = settlement.Id;
            l.SortOrder = i;
            return l;
        }).ToList();
        _db.FinalSettlementLines.AddRange(lineRows);

        // The Draft EOSBCalculation is PROMOTED rather than left dangling: /eosb/calculate upserts one per
        // (tenant, employee) and nothing has ever advanced its Status, so every tenant carries an
        // ever-growing pile of Drafts that mean nothing. Linking it makes the settlement the thing that
        // finally gives that row a lifecycle.
        var draftEosb = await _db.EOSBCalculations
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.Status == "Draft", cancellationToken);
        if (draftEosb is not null)
        {
            settlement.EosbCalculationId = draftEosb.Id;
            draftEosb.CalculationDate = plan.LastWorkingDay;
            draftEosb.EligibleSalary = plan.BasicWage;
            draftEosb.CalculatedAmount = plan.GratuityAmount;
        }

        // Every transition routes through POD-A3's hash-chained helper. /eosb/calculate wrote NOTHING to
        // that chain at all before this pod; a settlement is a payable, so every step of its life is now
        // tamper-evident with an actor.
        await PayrollAudit("payroll.final_settlement.drafted", "EmployeeFinalSettlement", settlement.Id.ToString(), new
        {
            employeeId = employee.Id, employee.EmployeeCode,
            offboardingId = offboarding.Id,
            lastWorkingDay = plan.LastWorkingDay, plan.TerminationReason,
            gross = plan.GrossPayable, deductions = plan.TotalDeductions, net = plan.NetPayable,
            gratuity = plan.GratuityAmount, encashment = plan.LeaveEncashmentAmount,
            noticePay = plan.NoticePayAmount, noticeShortfall = plan.NoticeShortfallDeduction,
            unpaidWages = plan.UnpaidWagesAmount, wagesPaidByRunId = plan.WagesPaidByRunId,
            wageBaseDelta = plan.WageBaseDeltaAmount,
            recomputed = existing is not null,
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(ProjectSettlement(settlement, lineRows, plan));
    }

    /// <summary>POD-C1 — every settlement for the tenant, newest first, with the Art. 88 overdue flag.</summary>
    [HttpGet("final-settlements")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> ListFinalSettlements(
        [FromQuery] int? employeeId, [FromQuery] string? status, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var q = _db.EmployeeFinalSettlements.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (employeeId.HasValue) q = q.Where(s => s.EmployeeId == employeeId.Value);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(s => s.Status == status);
        var rows = await q.OrderByDescending(s => s.CreatedAtUtc).ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(new
        {
            outstandingTotal = Math.Round(rows
                .Where(s => s.Status is FinalSettlementStatuses.Approved or FinalSettlementStatuses.Disbursing)
                .Sum(s => s.NetPayable), 2),
            note = "A settlement is a PAYABLE from the moment it is approved (DR expense / CR 2320) and is " +
                   "cleared by the ordinary payroll net-pay settlement when the disbursing run's payment " +
                   "batch is paid. [FLAG-COMPLIANCE-KSA] settlementDueDate applies KSA Labour Law Art. 88 " +
                   "(one week from the end of the contract; two weeks where the WORKER terminated it).",
            rows = rows.Select(s => new
            {
                s.Id, s.EmployeeId, s.EmployeeCode, s.EmployeeName, s.CompanyId, s.OffboardingId,
                s.LastWorkingDay, s.SettlementDueDate, s.TerminationReason, s.Status, s.Currency,
                s.GratuityAmount, s.LeaveEncashmentAmount, s.LeaveEncashmentDays, s.NoticePayAmount,
                s.OtherDuesAmount, s.NoticeShortfallDeduction, s.OtherDeductionsAmount,
                s.GrossPayable, s.TotalDeductions, s.NetPayable,
                s.PayrollRunId, s.PaymentBatchId, s.PaidAtUtc, s.GlPostedAtUtc, s.GlPeriod,
                s.ResidualDebtReclassed, s.ResidualDebtUnbooked,
                isOverdue = s.Status != FinalSettlementStatuses.Paid
                         && s.Status != FinalSettlementStatuses.Cancelled
                         && s.SettlementDueDate < today,
                overdueDays = s.Status is FinalSettlementStatuses.Paid or FinalSettlementStatuses.Cancelled
                    ? 0
                    : Math.Max(0, today.DayNumber - s.SettlementDueDate.DayNumber),
            }).ToList(),
        });
    }

    /// <summary>POD-C1 — one settlement with its full component breakdown and the A2 EOSB result.</summary>
    [HttpGet("final-settlements/{id:guid}")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> GetFinalSettlement(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var s = await _db.EmployeeFinalSettlements.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (s is null) return NotFound();
        var lines = await _db.FinalSettlementLines.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.SettlementId == id)
            .OrderBy(l => l.SortOrder)
            .ToListAsync(cancellationToken);
        return Ok(ProjectSettlement(s, lines, null));
    }

    /// <summary>POD-C1 — Draft → PendingApproval. Maker/checker: the approver may not be the creator.</summary>
    [HttpPost("final-settlements/{id:guid}/submit")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> SubmitFinalSettlement(Guid id, [FromBody] PayrollReasonRequest? req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var s = await _db.EmployeeFinalSettlements
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (s is null) return NotFound();
        if (s.Status != FinalSettlementStatuses.Draft)
            return BadRequest(new
            {
                error   = "invalid_transition",
                message = $"Only a Draft settlement can be submitted for approval (current: '{s.Status}').",
                status  = s.Status,
            });
        s.Status = FinalSettlementStatuses.PendingApproval;
        s.SubmittedByUserId = GetUserId();
        s.SubmittedByName = GetUserName();
        s.SubmittedAtUtc = DateTime.UtcNow;
        s.UpdatedAtUtc = DateTime.UtcNow;
        await PayrollAudit("payroll.final_settlement.submitted", "EmployeeFinalSettlement", s.Id.ToString(),
            new { s.EmployeeId, s.NetPayable, reason = req?.Reason }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { settlementId = s.Id, status = s.Status });
    }

    /// <summary>
    /// POD-C1 — APPROVE: the moment the settlement becomes a real liability. Posts the balanced accrual
    /// journal (post-once, period-close guarded) and moves the settlement to Approved so a settlement-purpose
    /// payroll run can disburse it.
    /// </summary>
    [HttpPost("final-settlements/{id:guid}/approve")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> ApproveFinalSettlement(
        Guid id, [FromBody] ApproveFinalSettlementRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var s = await _db.EmployeeFinalSettlements
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (s is null) return NotFound();
        if (s.Status is not (FinalSettlementStatuses.Draft or FinalSettlementStatuses.PendingApproval))
            return BadRequest(new
            {
                error   = "invalid_transition",
                message = $"Only a Draft or PendingApproval settlement can be approved (current: '{s.Status}').",
                status  = s.Status,
            });

        // Segregation of duties, mirroring the payroll run's own approve gate.
        var actorId = GetUserId();
        if (actorId is Guid aid && s.CreatedByUserId == aid && !req.AcknowledgeSelfApproval)
            return UnprocessableEntity(new
            {
                error   = "self_approval",
                message = "The person who computed a settlement should not also approve it. Have a second " +
                          "approver sign it off, or pass acknowledgeSelfApproval=true to record that you did both.",
            });

        // ── K2: THE TERMINATION REASON IS A LEGAL DETERMINATION, NOT A DEFAULT ───────────────────────
        // EmployeeOffboarding.SeparationType DEFAULTS to "Resignation", and ResolveTerminationReasonAsync
        // takes it as authoritative — so a record saved without changing the default pays a genuinely
        // TERMINATED employee ⅓ (or nil) of their statutory Art.84 award via the Art.85 scale, and C1 would
        // now disburse it. Approve therefore refuses until the approver restates the reason EXACTLY.
        if (!string.Equals(req.ConfirmTerminationReason?.Trim(), s.TerminationReason, StringComparison.Ordinal))
            return UnprocessableEntity(new
            {
                error   = "termination_reason_confirmation_required",
                message = "Confirm the separation reason before approving. It drives the KSA Art.84/85/80 award " +
                          "(Art.80 dismissal forfeits the gratuity entirely; an Art.85 resignation is reduced to " +
                          "nil / ⅓ / ⅔ by tenure), and EmployeeOffboarding.SeparationType DEFAULTS to " +
                          "'Resignation' — so an untouched default silently discounts a real termination. " +
                          "Re-send with confirmTerminationReason set to the resolved reason. [FLAG-COMPLIANCE-KSA]",
                resolvedTerminationReason = s.TerminationReason,
                appliedRule = SafeEosbRule(s.EosbResultJson),
                breakdown   = SafeEosbBreakdown(s.EosbResultJson),
            });

        // ── K3: THE ART. 84 WAGE BASE ────────────────────────────────────────────────────────────────
        // The pack computes gratuity on the LAST BASIC wage; POD-A2 documented that as a per-company FLOOR,
        // not the statutory "last wage" (basic + regular allowances). A displayed shortfall was advisory;
        // a DISBURSED one is underpayment of a statutory entitlement, evidenced by the employer's own
        // signed settlement. Non-zero delta requires an explicit, RECORDED acknowledgement.
        if (s.WageBaseDeltaAmount > 0.01m && !req.AcknowledgeWageBaseFloor)
            return UnprocessableEntity(new
            {
                error   = "wage_base_floor_requires_acknowledgement",
                message = $"This settlement pays gratuity on the BASIC wage only. Computed on the full package " +
                          $"(basic + allowances) it would be {s.WageBaseDeltaAmount:N2} {s.Currency} HIGHER. KSA " +
                          "Labour Law Art. 84 measures the award on the LAST WAGE, which the courts read as " +
                          "including regular allowances — so approving this disburses a known potential shortfall. " +
                          "Either raise the settlement (add the difference as an other-dues line), or re-send with " +
                          "acknowledgeWageBaseFloor=true to record who accepted the company floor. " +
                          "[FLAG-COMPLIANCE-KSA]",
                wageBaseDelta = s.WageBaseDeltaAmount,
                currency = s.Currency,
            });

        // ── GUARD 4: THE WAGE SIDE MUST BE DONE (the POD-C3 seam, as an assertion) ───────────────────
        // C3 pays a leaver through their last working day inside the ordinary run and stamps
        // PayrollSlip.IsFinalWageMonth. This settlement carries UnpaidWagesAmount at ZERO in GrossPayable
        // on the strength of that. If the slip does not exist the wage is genuinely unpaid, and approving
        // silently would leave the leaver short by a whole final month.
        if (s.WagesPaidByRunId is null && !s.WagesAcknowledgedUnpaid)
        {
            if (!req.AcknowledgeWagesUnpaid)
                return UnprocessableEntity(new
                {
                    error   = "final_wage_month_not_paid",
                    message = $"No non-voided final-wage-month payslip exists for {s.EmployeeName}, so their wages " +
                              $"through {s.LastWorkingDay:yyyy-MM-dd} have NOT been paid. Process the payroll run " +
                              "that covers their last working day first (POD-C3 prorates it and stamps the slip), " +
                              "or re-send with acknowledgeWagesUnpaid=true and a reason to ADD those wages to this " +
                              "settlement as an explicit other-dues line.",
                    unpaidWagesEstimate = s.UnpaidWagesAmount,
                    lastWorkingDay = s.LastWorkingDay,
                });
            if (string.IsNullOrWhiteSpace(req.WagesUnpaidReason))
                return BadRequest(new
                {
                    error   = "reason_required",
                    message = "A reason is required when adding the leaver's unpaid wages to the settlement.",
                });
            // Add the wage as a REAL settlement line and record who authorised it. It rides
            // SETTLEMENT_OTHER, which routes to EARN:OTHER — the same account an ad-hoc final due lands in.
            var wageAmount = Math.Round(s.UnpaidWagesAmount, 2);
            if (wageAmount > 0m)
            {
                var maxSort = await _db.FinalSettlementLines
                    .Where(l => l.TenantId == tenantId && l.SettlementId == s.Id)
                    .Select(l => (int?)l.SortOrder).MaxAsync(cancellationToken) ?? 0;
                _db.FinalSettlementLines.Add(new FinalSettlementLine
                {
                    TenantId = tenantId, SettlementId = s.Id,
                    ComponentCode = FinalSettlementComponents.OtherDues,
                    ComponentName = $"Unpaid wages to {s.LastWorkingDay:yyyy-MM-dd}",
                    LineType = FinalSettlementLineTypes.Earning,
                    Source = FinalSettlementComponents.SettlementSource,
                    Amount = wageAmount, SortOrder = maxSort + 1,
                    Narrative = $"Added at approval — {req.WagesUnpaidReason}",
                });
                s.OtherDuesAmount = Math.Round(s.OtherDuesAmount + wageAmount, 2);
                s.GrossPayable = Math.Round(s.GrossPayable + wageAmount, 2);
                s.NetPayable = Math.Round(s.GrossPayable - s.TotalDeductions, 2);
            }
            s.WagesAcknowledgedUnpaid = true;
            s.WagesAcknowledgementReason = req.WagesUnpaidReason;
        }

        // ── B4: NO OVERLAPPING SERVICE WINDOW ────────────────────────────────────────────────────────
        // Uniqueness is keyed on the OFFBOARDING so a re-hire can be settled again; that alone would let a
        // re-hire whose JoiningDate was never reset be paid gratuity for service ALREADY settled, because
        // ComputeEndOfServiceAsync derives service from employee.JoiningDate. The window assertion is what
        // closes that.
        var overlapping = await _db.EmployeeFinalSettlements.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == s.EmployeeId && x.Id != s.Id
                     && x.Status != FinalSettlementStatuses.Cancelled
                     && x.ServiceStartDate <= s.LastWorkingDay && x.LastWorkingDay >= s.ServiceStartDate)
            .Select(x => new { x.Id, x.ServiceStartDate, x.LastWorkingDay, x.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (overlapping is not null)
            return Conflict(new
            {
                error   = "service_period_already_settled",
                message = $"Settlement {overlapping.Id} already covers service from " +
                          $"{overlapping.ServiceStartDate:yyyy-MM-dd} to {overlapping.LastWorkingDay:yyyy-MM-dd}, " +
                          $"which overlaps this one ({s.ServiceStartDate:yyyy-MM-dd} → {s.LastWorkingDay:yyyy-MM-dd}). " +
                          "For a re-hire, reset the employee's joining date to the RE-HIRE date so the gratuity is " +
                          "computed on the new service period only.",
                overlappingSettlementId = overlapping.Id,
                overlappingStatus = overlapping.Status,
            });

        // ── POST-ONCE (mirrors Lock / SettlePaymentBatch / RemitStatutory verbatim) ──────────────────
        if (await FinalSettlementGlLedger.HasLiveAccrualAsync(_db, tenantId, s.Id, cancellationToken))
            return Conflict(new
            {
                error   = "already_posted",
                message = "This settlement's accrual journal has already been posted to GL.",
                settlementId = s.Id,
            });

        var accrualDate = req.AccrualDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var glPeriod = $"{accrualDate.Year}-{accrualDate.Month:D2}";
        // ── GUARD 3: cannot accrue into a CLOSED period ──────────────────────────────────────────────
        if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, s.CompanyId, glPeriod, cancellationToken))
            return UnprocessableEntity(new
            {
                error   = "gl_period_closed",
                message = $"GL period {glPeriod} is closed. Reopen it before accruing a settlement into it.",
                period  = glPeriod, companyId = s.CompanyId,
            });

        var lines = await _db.FinalSettlementLines.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.SettlementId == s.Id)
            .OrderBy(l => l.SortOrder)
            .ToListAsync(cancellationToken);
        // Re-read the just-added unpaid-wage line, which is tracked but not yet saved.
        var pendingLines = _db.ChangeTracker.Entries<FinalSettlementLine>()
            .Where(e => e.State == EntityState.Added && e.Entity.SettlementId == s.Id)
            .Select(e => e.Entity)
            .ToList();
        if (pendingLines.Count > 0) lines = lines.Concat(pendingLines).ToList();

        var glCtx = await LoadGlResolutionContextAsync(tenantId, s.CompanyId, cancellationToken);

        // ── THE POD-C2 SEAM ─────────────────────────────────────────────────────────────────────────
        // Consume whatever POD-C2 has PROVIDED for THIS employee before expensing anything. With no C2 the
        // cursor is empty, nothing is relieved, and the whole gratuity hits 5110 — which is exactly what a
        // tenant with no monthly accrual expects. When C2 starts posting DR 5110 / CR 2310 monthly, this
        // same code consumes the provision first and expenses only the shortfall, with no rework here.
        // (It is a PER-EMPLOYEE positional sub-ledger, not ControlAccountBalance.AvailableForRelief: that
        // clamp is for receivables — on a credit-balance LIABILITY it is always 0, and a tenant-wide pool
        // would let one employee's settlement relieve another employee's provision.)
        var provisionCursor = await EosbProvisionLedger.LoadCursorAsync(_db, tenantId, s.EmployeeId, cancellationToken);
        var (provisionPosition, provisionTaken) =
            provisionCursor.Take(s.EmployeeId, s.CompanyId, s.GratuityAmount);

        var (glLines, dr, cr) = FinalSettlementGlLedger.BuildAccrual(
            tenantId, s, lines, glPeriod, glCtx, actorId, GetUserName(),
            provisionTaken, provisionPosition?.ProvisionAccount);
        if (Math.Abs(dr - cr) > 0.01m)
            return UnprocessableEntity(new
            {
                error = "gl_unbalanced",
                message = "Final settlement GL is not balanced. Total debits must equal total credits before approving.",
                totalDebits = dr, totalCredits = cr, difference = Math.Abs(dr - cr),
            });
        _db.FinanceGlEntries.AddRange(glLines);

        s.Status = FinalSettlementStatuses.Approved;
        s.ConfirmedTerminationReason = s.TerminationReason;
        s.ApprovedByUserId = actorId;
        s.ApprovedByName = GetUserName();
        s.ApprovedAtUtc = DateTime.UtcNow;
        s.GlPostedAtUtc = DateTime.UtcNow;
        s.GlPeriod = glPeriod;
        s.UpdatedAtUtc = DateTime.UtcNow;
        if (req.AcknowledgeWageBaseFloor && s.WageBaseDeltaAmount > 0m)
        {
            s.WageBaseAcknowledgedByUserId = actorId;
            s.WageBaseAcknowledgedByName = GetUserName();
            s.WageBaseAcknowledgedAtUtc = DateTime.UtcNow;
        }

        await PayrollAudit("payroll.final_settlement.approved", "EmployeeFinalSettlement", s.Id.ToString(), new
        {
            s.EmployeeId, s.EmployeeCode, s.TerminationReason, s.LastWorkingDay,
            gross = s.GrossPayable, deductions = s.TotalDeductions, net = s.NetPayable,
            glPeriod, totalDebits = dr, totalCredits = cr,
            provisionConsumed = provisionTaken, provisionAccount = provisionPosition?.ProvisionAccount,
            wageBaseDelta = s.WageBaseDeltaAmount,
            wageBaseAcknowledged = req.AcknowledgeWageBaseFloor,
            wagesAcknowledgedUnpaid = s.WagesAcknowledgedUnpaid,
            selfApproved = actorId is Guid a2 && s.CreatedByUserId == a2,
            reason = req.Reason,
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            settlementId = s.Id,
            status = s.Status,
            glPeriod,
            totalDebits = dr,
            totalCredits = cr,
            provisionConsumed = provisionTaken,
            netPayable = s.NetPayable,
            journal = glLines.Select(l => new
            {
                l.EventType, debit = l.DebitAccount, credit = l.CreditAccount, l.Amount, l.Description,
            }).ToList(),
            nextStep = "Create an OffCycle run with settlesFinalSettlements=true and includesRecurringPay=false, " +
                       "add this employee to its selection, then Process → Approve → Lock → payment batch → " +
                       "WPS/bank acceptance → settle. POD-B1's net-pay settlement clears 2320 like any other payable.",
        });
    }

    /// <summary>
    /// POD-C1 — CANCEL: fully unwinds a settlement. An accrued settlement is contra'd (re-opening 2310 and
    /// closing 2320 to zero) before the status flips, so the books and the operational state can never
    /// disagree. Refused once a live payroll clearing exists — at that point the correct unwind is POD-B3's
    /// run void, which contras the clearing and replays the witness to restore the settlement to Approved.
    /// </summary>
    [HttpPost("final-settlements/{id:guid}/cancel")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> CancelFinalSettlement(
        Guid id, [FromBody] PayrollReasonRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (string.IsNullOrWhiteSpace(req?.Reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required to cancel a settlement." });

        var s = await _db.EmployeeFinalSettlements
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (s is null) return NotFound();
        if (s.Status == FinalSettlementStatuses.Cancelled)
            return Conflict(new { error = "already_cancelled", message = "This settlement is already cancelled." });

        if (await FinalSettlementGlLedger.HasLiveClearingAsync(_db, tenantId, s.Id, cancellationToken))
            return UnprocessableEntity(new
            {
                error   = "settlement_disbursed",
                message = "This settlement has already been consumed by a payroll run's accrual journal, so it " +
                          "cannot be cancelled in isolation — cancelling would leave the run's clearing debiting a " +
                          "payable that no longer exists. VOID the disbursing run instead (POST runs/{id}/void): " +
                          "that contras the clearing, re-opens the payable and restores this settlement to Approved, " +
                          "after which it can be cancelled.",
                payrollRunId = s.PayrollRunId,
            });

        var originals = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId
                     && x.SourceModule == FinalSettlementGlDescriptions.SourceModule
                     && x.SourceEntityId == s.Id && !x.IsReversed
                     && (x.EventType == GlEventTypes.SettlementAccrual
                      || x.EventType == GlEventTypes.EosbProvisionConsumption))
            .ToListAsync(cancellationToken);

        // Do not silently rewrite closed books — every period the contra would write into is guarded.
        foreach (var closedPeriod in originals.Select(o => o.Period).Distinct())
            if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, s.CompanyId, closedPeriod, cancellationToken))
                return UnprocessableEntity(new
                {
                    error   = "gl_period_closed",
                    message = $"GL period {closedPeriod} is closed. Reopen it before cancelling this settlement.",
                    period  = closedPeriod,
                });

        var contras = new List<FinanceGlEntry>();
        if (originals.Count > 0)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var orig in originals)
                contras.Add(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = orig.CompanyId,
                    SourceModule = FinalSettlementGlDescriptions.SourceModule,
                    SourceEntityId = s.Id, SourceEntityRef = orig.SourceEntityRef,
                    // A provision consumption is undone by its OWN reversal tag, so the per-employee
                    // provision position re-opens (LoadPositionsAsync filters !IsReversed on both legs).
                    EventType = orig.EventType == GlEventTypes.EosbProvisionConsumption
                        ? GlEventTypes.EosbProvisionConsumptionReversal
                        : GlEventTypes.SettlementAccrualReversal,
                    DebitAccount = orig.CreditAccount, CreditAccount = orig.DebitAccount,
                    Amount = orig.Amount, Currency = orig.Currency,
                    EntryDate = today, Period = orig.Period,
                    Description = $"{FinalSettlementGlDescriptions.ReversalPrefix}{orig.Description} — {req.Reason}",
                    PostedBy = GetUserId(), PostedByName = GetUserName(),
                    IsReversed = false, ReversalOfEntryId = orig.Id,
                });
            foreach (var o in originals) o.IsReversed = true;
            _db.FinanceGlEntries.AddRange(contras);
        }

        var priorStatus = s.Status;
        s.Status = FinalSettlementStatuses.Cancelled;
        s.CancelledByUserId = GetUserId();
        s.CancelledByName = GetUserName();
        s.CancelledAtUtc = DateTime.UtcNow;
        s.CancelReason = req.Reason;
        s.UpdatedAtUtc = DateTime.UtcNow;

        await PayrollAudit("payroll.final_settlement.cancelled", "EmployeeFinalSettlement", s.Id.ToString(), new
        {
            s.EmployeeId, s.EmployeeCode, priorStatus, reason = req.Reason,
            contraEntries = contras.Count,
            contraAmount = Math.Round(contras.Sum(c => c.Amount), 2),
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { settlementId = s.Id, status = s.Status, reversedEntries = contras.Count });
    }

    // ── POD-C1 internals ─────────────────────────────────────────────────────────────────────────

    /// <summary>The computed settlement, before it is written to a row.</summary>
    private sealed record FinalSettlementPlan(
        string EmployeeCode, string EmployeeName, Guid? CompanyId,
        DateOnly LastWorkingDay, DateOnly ServiceStartDate, DateOnly SettlementDueDate,
        string TerminationReason, decimal ServiceYears, string Currency,
        decimal BasicWage, decimal MonthlyGross,
        string EosbResultJson, string InputsSnapshotJson,
        decimal GratuityAmount, decimal LeaveEncashmentAmount, decimal LeaveEncashmentDays,
        decimal NoticePayAmount, decimal OtherDuesAmount,
        decimal NoticeShortfallDeduction, decimal OtherDeductionsAmount,
        decimal PlannedLoanRecovery, decimal PlannedAdvanceRecovery, decimal PlannedReceivableRecovery,
        decimal GrossPayable, decimal TotalDeductions, decimal NetPayable,
        decimal UnpaidWagesAmount, Guid? WagesPaidByRunId, DateOnly? WagesPaidThroughDate,
        decimal WageBaseDeltaAmount,
        List<FinalSettlementLine> Lines, List<string> Warnings,
        int NoticePeriodDaysShort, int DaysInMonth, int DaysWorkedInMonth);

    private static void ApplyPlan(EmployeeFinalSettlement s, FinalSettlementPlan p)
    {
        s.EmployeeCode = p.EmployeeCode;
        s.EmployeeName = p.EmployeeName;
        s.LastWorkingDay = p.LastWorkingDay;
        s.ServiceStartDate = p.ServiceStartDate;
        s.SettlementDueDate = p.SettlementDueDate;
        s.TerminationReason = p.TerminationReason;
        s.ServiceYears = p.ServiceYears;
        s.Currency = p.Currency;
        s.EosbResultJson = p.EosbResultJson;
        s.InputsSnapshotJson = p.InputsSnapshotJson;
        s.GratuityAmount = p.GratuityAmount;
        s.LeaveEncashmentAmount = p.LeaveEncashmentAmount;
        s.LeaveEncashmentDays = p.LeaveEncashmentDays;
        s.NoticePayAmount = p.NoticePayAmount;
        s.OtherDuesAmount = p.OtherDuesAmount;
        s.NoticeShortfallDeduction = p.NoticeShortfallDeduction;
        s.OtherDeductionsAmount = p.OtherDeductionsAmount;
        s.PlannedLoanRecovery = p.PlannedLoanRecovery;
        s.PlannedAdvanceRecovery = p.PlannedAdvanceRecovery;
        s.PlannedReceivableRecovery = p.PlannedReceivableRecovery;
        s.GrossPayable = p.GrossPayable;
        s.TotalDeductions = p.TotalDeductions;
        s.NetPayable = p.NetPayable;
        s.UnpaidWagesAmount = p.UnpaidWagesAmount;
        s.WagesPaidByRunId = p.WagesPaidByRunId;
        s.WagesPaidThroughDate = p.WagesPaidThroughDate;
        s.WageBaseDeltaAmount = p.WageBaseDeltaAmount;
        s.WarningsJson = JsonSerializer.Serialize(p.Warnings);
    }

    /// <summary>
    /// POD-C1 — resolves the legal entity a settlement belongs to. Explicit and refused rather than
    /// guessed: a null CompanyId silently changes which GlAccountMapping overrides apply and which
    /// period-close row binds. Mirrors ResolveRunCompanyScopeAsync — the employee's own company, else the
    /// tenant's single active company (the legacy-unscoped case), else refuse.
    /// </summary>
    private async Task<(Guid? CompanyId, object? Error)> ResolveSettlementCompanyAsync(
        Guid tenantId, Employee employee, CancellationToken ct)
    {
        var activeCompanies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (employee.CompanyId is Guid cid && activeCompanies.Contains(cid)) return (cid, null);
        if (activeCompanies.Count == 1) return (activeCompanies[0], null);
        return (null, new
        {
            error   = "company_not_resolved",
            message = $"{employee.FullName} is not linked to an active legal entity and this tenant has " +
                      $"{activeCompanies.Count} of them, so the settlement's company cannot be resolved. The company " +
                      "decides which chart-of-accounts overrides apply and which GL period-close row binds, so it is " +
                      "refused rather than guessed. Set the employee's company and retry.",
            employeeId = employee.Id,
        });
    }

    private static string SafeEosbRule(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("applicableRule", out var r) ? r.GetString() ?? string.Empty : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static object SafeEosbBreakdown(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("breakdown", out var b)
                ? JsonSerializer.Deserialize<List<Dictionary<string, object>>>(b.GetRawText()) ?? new()
                : new List<Dictionary<string, object>>();
        }
        catch { return new List<Dictionary<string, object>>(); }
    }

    /// <summary>JSON body for a settlement, a strict SUPERSET of the pre-C1 /final-settlement response.</summary>
    private static object ProjectSettlement(
        EmployeeFinalSettlement s, IReadOnlyList<FinalSettlementLine> lines, FinalSettlementPlan? plan)
        => new
        {
            // ── every field the pre-C1 endpoint returned, at the SAME path ────────────────────────────
            employeeId = s.EmployeeId,
            employeeName = s.EmployeeName,
            lastWorkingDay = s.LastWorkingDay,
            currency = s.Currency,
            basicSalary = plan?.BasicWage ?? 0m,
            grossSalary = plan?.MonthlyGross ?? 0m,
            // The old "pro-rata salary" is now the C3 seam: it is the leaver's wage through their last
            // working day, which the ordinary run has already PAID (hence zero here unless the operator
            // explicitly acknowledged that it had not).
            proRataSalary = s.WagesAcknowledgedUnpaid ? s.UnpaidWagesAmount : 0m,
            daysWorkedInMonth = plan?.DaysWorkedInMonth ?? s.LastWorkingDay.Day,
            daysInMonth = plan?.DaysInMonth ?? DateTime.DaysInMonth(s.LastWorkingDay.Year, s.LastWorkingDay.Month),
            eosbAmount = s.GratuityAmount,
            totalYears = Math.Round(s.ServiceYears, 2),
            terminationReason = s.TerminationReason,
            leaveBalanceDays = s.LeaveEncashmentDays,
            leaveEncashment = s.LeaveEncashmentAmount,
            noticePeriodDaysShort = plan?.NoticePeriodDaysShort ?? 0,
            noticePeriodDeduction = s.NoticeShortfallDeduction,
            totalPayable = s.NetPayable,
            breakdown = lines.Select(l => new
            {
                component = l.ComponentName,
                amount = l.LineType == FinalSettlementLineTypes.Deduction ? -l.Amount : l.Amount,
            }).ToList(),

            // ── POD-C1 additions ──────────────────────────────────────────────────────────────────────
            settlementId = s.Id,
            status = s.Status,
            companyId = s.CompanyId,
            offboardingId = s.OffboardingId,
            serviceStartDate = s.ServiceStartDate,
            settlementDueDate = s.SettlementDueDate,
            grossPayable = s.GrossPayable,
            totalDeductions = s.TotalDeductions,
            netPayable = s.NetPayable,
            noticePay = s.NoticePayAmount,
            otherDues = s.OtherDuesAmount,
            otherDeductions = s.OtherDeductionsAmount,
            plannedLoanRecovery = s.PlannedLoanRecovery,
            plannedAdvanceRecovery = s.PlannedAdvanceRecovery,
            plannedReceivableRecovery = s.PlannedReceivableRecovery,
            unpaidWagesAmount = s.UnpaidWagesAmount,
            wagesPaidByRunId = s.WagesPaidByRunId,
            wagesPaidThroughDate = s.WagesPaidThroughDate,
            wageBaseDelta = s.WageBaseDeltaAmount,
            glPeriod = s.GlPeriod,
            glPostedAtUtc = s.GlPostedAtUtc,
            payrollRunId = s.PayrollRunId,
            paymentBatchId = s.PaymentBatchId,
            paidAtUtc = s.PaidAtUtc,
            eosbResult = SafeEosbBreakdown(s.EosbResultJson),
            appliedRule = SafeEosbRule(s.EosbResultJson),
            warnings = plan?.Warnings ?? new List<string>(),
            lines = lines.Select(l => new
            {
                l.ComponentCode, l.ComponentName, l.LineType, l.Source, l.Amount, l.Quantity,
                l.SourceEntityId, l.Narrative,
            }).ToList(),
        };

    /// <summary>
    /// POD-C1 — computes the whole settlement, ONCE, from POD-A2's engine and POD-C3's persisted outputs.
    /// Every number the disbursing run emits comes from here and is never recomputed downstream.
    /// </summary>
    private async Task<FinalSettlementPlan> BuildFinalSettlementPlanAsync(
        Guid tenantId, Employee employee, EmployeeOffboarding offboarding, GCCComplianceSetting gcc,
        Guid? companyId, FinalSettlementRequest req, CancellationToken ct)
    {
        var warnings = new List<string>();
        var lastDay = offboarding.LastWorkingDay;
        if (req.LastWorkingDay != default && req.LastWorkingDay != lastDay)
            warnings.Add($"The requested last working day ({req.LastWorkingDay:yyyy-MM-dd}) was IGNORED: the " +
                         $"settlement uses the offboarding record's {lastDay:yyyy-MM-dd}. Correct the offboarding " +
                         "record if that date is wrong.");

        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.EffectiveDate <= lastDay)
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync(ct);
        var basicWage = salary?.BasicSalary ?? employee.Salary ?? 0m;
        var monthlyGross = basicWage + (salary?.HousingAllowance ?? 0m) + (salary?.TransportAllowance ?? 0m)
                         + (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
        var currency = !string.IsNullOrWhiteSpace(salary?.Currency)
            ? salary!.Currency
            : await ResolveCurrencyAsync(tenantId, ct);

        var terminationReason = await ResolveTerminationReasonAsync(tenantId, employee.Id, req.TerminationReason, ct);
        var calcDate = lastDay.ToDateTime(TimeOnly.MinValue);

        // ── POD-A2's ONE authoritative engine. The Breakdown is kept IN FULL — /eosb/calculate discards
        //    it and keeps only the rule string, so the Art.84 tier split and the Art.85/80 adjustment line
        //    were unrecoverable from anything the product persisted.
        var (eosbResult, serviceYears) = await ComputeEndOfServiceAsync(
            gcc, basicWage, employee.JoiningDate, calcDate, terminationReason, employee, ct);
        var gratuity = Math.Round(eosbResult.TotalGratuity, 2);

        // [FLAG-COMPLIANCE-KSA] the Art. 84 wage-base delta, computed by re-running the SAME pack on the
        // full package. Purely indicative and never paid automatically — it is the number the approver
        // must consciously accept or correct.
        decimal wageBaseDelta = 0m;
        if (monthlyGross > basicWage && gratuity > 0m)
        {
            var (fullResult, _) = await ComputeEndOfServiceAsync(
                gcc, monthlyGross, employee.JoiningDate, calcDate, terminationReason, employee, ct);
            wageBaseDelta = Math.Max(0m, Math.Round(fullResult.TotalGratuity - gratuity, 2));
            if (wageBaseDelta > 0m)
                warnings.Add($"[FLAG-COMPLIANCE-KSA] Gratuity is computed on the BASIC wage ({basicWage:N2}). On the " +
                             $"full package ({monthlyGross:N2}) it would be {wageBaseDelta:N2} {currency} higher. " +
                             "KSA Art. 84 measures the award on the LAST WAGE, which is read as including regular " +
                             "allowances. Approval requires an explicit acknowledgement of this floor.");
        }

        // ── K5: one authoritative encashable-days function ───────────────────────────────────────────
        var encashment = await LeaveEncashmentCalculator.ComputeAsync(
            _db, tenantId, employee.Id, companyId, lastDay, monthlyGross, ct);
        warnings.AddRange(encashment.Warnings);

        // ── K1: THE NOTICE RULES DEPEND ON WHO TERMINATED (Art. 75/76) ───────────────────────────────
        // The notice compensation flows to whichever party did NOT give notice: an employer terminating
        // without notice OWES the employee; only a WORKER resigning short owes the employer. The pre-C1
        // endpoint applied noticePeriodDaysShort as a deduction unconditionally, with no reference to the
        // termination reason resolved immediately above it.
        var isResignation = string.Equals(terminationReason, "Resignation", StringComparison.OrdinalIgnoreCase);
        var dailyRate = monthlyGross > 0m ? monthlyGross / 30m : 0m;
        decimal noticeShortfall = 0m;
        var noticeDaysShort = Math.Max(0, req.NoticePeriodDaysShort);
        if (noticeDaysShort > 0)
        {
            if (isResignation)
                noticeShortfall = Math.Round(noticeDaysShort * dailyRate, 2);
            else
                warnings.Add($"[FLAG-COMPLIANCE-KSA] A notice shortfall of {noticeDaysShort} day(s) was requested but " +
                             $"the separation reason is '{terminationReason}', not a resignation. Under Art. 75/76 the " +
                             "notice compensation is owed BY the party that failed to give notice — an employer-side " +
                             "termination cannot deduct it from the worker. It has been set to ZERO. If the employer " +
                             "terminated without serving notice, record noticePayDays instead (payment IN LIEU).");
        }
        var noticePayDays = Math.Max(0, req.NoticePayDays);
        var noticePay = noticePayDays > 0 ? Math.Round(noticePayDays * dailyRate, 2) : 0m;
        if (noticePayDays > 0 && isResignation)
            warnings.Add($"Payment in lieu of notice ({noticePayDays} day(s)) is being paid on a RESIGNATION. That is " +
                         "unusual — payment in lieu is normally owed by an employer who terminated without serving " +
                         "notice. It has been included as instructed; confirm it is intended.");
        if (noticePayDays == 0 && !isResignation && offboarding.NoticePeriodDays > 0
            && offboarding.NoticeDate.AddDays(offboarding.NoticePeriodDays) > lastDay)
            warnings.Add("[FLAG-COMPLIANCE-KSA] The employer terminated and the last working day falls BEFORE the end " +
                         "of the recorded notice period, but no payment in lieu of notice has been included. Under " +
                         "Art. 75/76 the employer owes compensation for the unserved notice — set noticePayDays if so.");

        // ── THE POD-C3 WAGE SEAM: the leaver's final wages, and who paid them ────────────────────────
        var finalWageSlip = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.EmployeeId == employee.Id
                     && s.IsFinalWageMonth && s.Status != "Voided")
            .OrderByDescending(s => s.PaidToDate)
            .Select(s => new { s.RunId, s.PaidToDate, s.GrossSalary })
            .FirstOrDefaultAsync(ct);
        decimal unpaidWages = 0m;
        Guid? wagesPaidByRunId = null;
        DateOnly? wagesPaidThrough = null;
        if (finalWageSlip is not null)
        {
            wagesPaidByRunId = finalWageSlip.RunId;
            wagesPaidThrough = finalWageSlip.PaidToDate;
            unpaidWages = 0m;   // PAID — carried at ZERO in GrossPayable, which is the whole point
        }
        else
        {
            // An INDICATIVE figure only, so the approver can see what is at stake. It is not added to the
            // settlement unless they explicitly acknowledge that the wage side is not done.
            var daysInLwdMonth = DateTime.DaysInMonth(lastDay.Year, lastDay.Month);
            unpaidWages = Math.Round(monthlyGross / daysInLwdMonth * lastDay.Day, 2);
            warnings.Add($"No non-voided final-wage-month payslip exists, so wages through {lastDay:yyyy-MM-dd} appear " +
                         $"UNPAID (indicatively {unpaidWages:N2} {currency}). They are carried at ZERO in this " +
                         "settlement: the correct fix is to process the payroll run covering the last working day, " +
                         "which prorates them properly (POD-C3). Approval refuses until that run exists, or until an " +
                         "operator explicitly acknowledges the wages are unpaid and authorises adding them here.");
        }

        // ── PLANNED debt recovery. Planned only: the run RE-CAPS against the live balance and is the sole
        //    decrement path, so there is exactly one recovery mechanism and no double-recovery to guard. ─
        var loans = await _db.EmployeeLoans.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Status == "Active"
                     && l.EmployeeIntId == employee.Id && l.OutstandingBalance > 0)
            .ToListAsync(ct);
        var advances = await _db.SalaryAdvances.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == "Active"
                     && a.EmployeeIntId == employee.Id && a.OutstandingBalance > 0)
            .ToListAsync(ct);
        var receivables = await _db.PayrollEmployeeReceivables.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employee.Id
                     && r.Status == PayrollReceivableStatuses.Outstanding && r.Amount > r.RecoveredAmount)
            .ToListAsync(ct);

        // ── Build the lines. THE RUN EMITS THESE VERBATIM. ───────────────────────────────────────────
        var lines = new List<FinalSettlementLine>();
        void Earning(string code, string name, decimal amount, decimal qty = 0m, Guid? sourceId = null, string? narrative = null)
        {
            if (amount <= 0m) return;
            lines.Add(new FinalSettlementLine
            {
                ComponentCode = code, ComponentName = name,
                LineType = FinalSettlementLineTypes.Earning,
                Source = FinalSettlementComponents.SettlementSource,
                Amount = Math.Round(amount, 2), Quantity = qty,
                SourceEntityId = sourceId, Narrative = narrative,
            });
        }
        void Deduction(string code, string name, decimal amount, string? narrative = null)
        {
            if (amount <= 0m) return;
            lines.Add(new FinalSettlementLine
            {
                ComponentCode = code, ComponentName = name,
                LineType = FinalSettlementLineTypes.Deduction,
                Source = FinalSettlementComponents.SettlementSource,
                Amount = Math.Round(amount, 2), Narrative = narrative,
            });
        }

        Earning(FinalSettlementComponents.Gratuity,
            $"{FinalSettlementComponents.Label(FinalSettlementComponents.Gratuity)} ({eosbResult.ApplicableRule})",
            gratuity, narrative: string.Join("; ", eosbResult.Breakdown.Select(b => $"{b.Label}: {b.Amount:N2}")));
        foreach (var enc in encashment.Lines)
            Earning(FinalSettlementComponents.LeaveEncashment,
                $"Leave encashment — {enc.LeaveTypeName} ({enc.EncashableDays:N2} days, {enc.Year})",
                enc.Amount, enc.EncashableDays, enc.BalanceId,
                $"{enc.EncashableDays:N2} of {enc.AvailableDays:N2} available days at {enc.Basis}");
        Earning(FinalSettlementComponents.NoticePay,
            $"{FinalSettlementComponents.Label(FinalSettlementComponents.NoticePay)} ({noticePayDays} days)", noticePay);
        Earning(FinalSettlementComponents.OtherDues, "Other final dues", Math.Max(0m, req.OtherDuesAmount),
            narrative: req.Notes);

        Deduction(FinalSettlementComponents.NoticeShortfall,
            $"{FinalSettlementComponents.Label(FinalSettlementComponents.NoticeShortfall)} ({noticeDaysShort} days)",
            noticeShortfall);
        Deduction(FinalSettlementComponents.OtherDeduction, "Other final deductions",
            Math.Max(0m, req.OtherDeductionsAmount), req.Notes);

        var gross = Math.Round(lines.Where(l => l.LineType == FinalSettlementLineTypes.Earning).Sum(l => l.Amount), 2);
        var rawDeductions = Math.Round(lines.Where(l => l.LineType == FinalSettlementLineTypes.Deduction).Sum(l => l.Amount), 2);
        // ── F1: THE SETTLEMENT'S OWN DEDUCTIONS ARE CAPPED AT ITS GROSS, AT PLAN TIME ────────────────
        // Debt is capped later by the run's `affordable` budget, but a settlement-side deduction is not —
        // it lands in deductionsBeforeDebt, and on a supplemental run a negative net throws
        // negative_net_unsupported and rolls back EVERY OTHER LEAVER in the batch. "The batch fails" is not
        // an acceptable answer to "the notice shortfall exceeds the settlement".
        var deductions = Math.Min(gross, rawDeductions);
        if (rawDeductions > gross)
            warnings.Add($"Settlement deductions ({rawDeductions:N2}) exceed the settlement's gross ({gross:N2}) and " +
                         $"have been CAPPED at it. {rawDeductions - gross:N2} is not written off — recover it through " +
                         "the loan/advance sub-ledger or an explicit agreement outside payroll.");
        var net = Math.Round(gross - deductions, 2);

        // Recovery is planned against what the settlement can actually fund, in a fixed order.
        var recoveryBudget = net;
        decimal loanPlan = 0m, advPlan = 0m, rcvPlan = 0m;
        foreach (var l in loans.OrderBy(l => l.Id))
        {
            if (recoveryBudget <= 0m) break;
            var take = Math.Min(l.OutstandingBalance, recoveryBudget);
            loanPlan += take; recoveryBudget -= take;
        }
        foreach (var a in advances.OrderBy(a => a.Id))
        {
            if (recoveryBudget <= 0m) break;
            var take = Math.Min(a.OutstandingBalance, recoveryBudget);
            advPlan += take; recoveryBudget -= take;
        }
        foreach (var r in receivables.OrderBy(r => r.CreatedAtUtc))
        {
            if (recoveryBudget <= 0m) break;
            var take = Math.Min(r.Outstanding, recoveryBudget);
            rcvPlan += take; recoveryBudget -= take;
        }
        var totalDebt = Math.Round(loans.Sum(l => l.OutstandingBalance) + advances.Sum(a => a.OutstandingBalance), 2);
        if (totalDebt > Math.Round(loanPlan + advPlan, 2))
            warnings.Add($"{totalDebt - Math.Round(loanPlan + advPlan, 2):N2} of outstanding loan/advance cannot be " +
                         "funded by this settlement. It is NOT written off: when the settlement is paid, the residual " +
                         "is reclassified to the Employee Overpayment Receivable (1420) up to what 1400/1410 actually " +
                         "carries, and reported when it cannot be.");

        // [FLAG-COMPLIANCE-KSA] Art. 88 — one week from the end of the contract, two where the WORKER
        // terminated it. Persisted so an overdue settlement is visible on the list view.
        var dueDate = lastDay.AddDays(isResignation ? 14 : 7);

        return new FinalSettlementPlan(
            employee.EmployeeCode, employee.FullName, companyId,
            lastDay, DateOnly.FromDateTime(employee.JoiningDate), dueDate,
            terminationReason, (decimal)Math.Round(serviceYears, 4), currency,
            basicWage, monthlyGross,
            JsonSerializer.Serialize(new
            {
                totalGratuity = eosbResult.TotalGratuity,
                applicableRule = eosbResult.ApplicableRule,
                breakdown = eosbResult.Breakdown.Select(b => new { b.Label, b.Amount }).ToList(),
            }),
            JsonSerializer.Serialize(new
            {
                basicWage, monthlyGross, currency,
                serviceStart = DateOnly.FromDateTime(employee.JoiningDate),
                serviceEnd = lastDay,
                terminationReason,
                countryCode = gcc.CountryCode,
                contractType = employee.ContractType,
                nationality = employee.Nationality,
                wageBase = "basic",
            }),
            gratuity, encashment.TotalAmount, encashment.TotalDays,
            noticePay, Math.Max(0m, req.OtherDuesAmount),
            noticeShortfall, Math.Max(0m, req.OtherDeductionsAmount),
            Math.Round(loanPlan, 2), Math.Round(advPlan, 2), Math.Round(rcvPlan, 2),
            gross, deductions, net,
            unpaidWages, wagesPaidByRunId, wagesPaidThrough,
            wageBaseDelta,
            lines, warnings,
            noticeDaysShort,
            DateTime.DaysInMonth(lastDay.Year, lastDay.Month), lastDay.Day);
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

        // POD-B3 — exclude VOIDED runs. A voided run's validation results are historical (the run they
        // describe no longer exists), but this join had no status filter while the very next query does,
        // so a voided month kept the company's payroll card permanently red — and the only way to clear
        // it was to void a run that had already been voided.
        var validationErrors = await _db.PayrollValidationResults
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && !v.IsResolved)
            .Join(_db.PayrollRuns.Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth && r.Status != "Voided"),
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

        // ── POD-C3-FIX: the AUDITOR's payslip must explain itself too ────────────────────────────────
        // Requirement 7 says an employee AND an auditor must both be able to see why a number is what it
        // is. The employee's half already worked — ESS renders the stored PayslipComponent rows, whose
        // names carry the proration note and one "Arrears — <component> (YYYY-MM)" line per covered
        // period. THIS endpoint builds its items from the slip HEADER columns instead, so arrears folded
        // silently into "Other Allowances" and the days/basis narrative never appeared at all. Both are
        // read from persisted state (no recompute), so this PDF cannot disagree with the ESS one.
        var arrearsLines = slip.ArrearsAmount == 0m
            ? new List<PayrollArrearsLine>()
            : await _db.PayrollArrearsLines.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.PayrollRunId == payslip.PayrollRunId
                         && a.EmployeeId == slip.EmployeeId && a.Status == PayrollArrearsStatuses.Settled)
                .OrderBy(a => a.CoveredYear).ThenBy(a => a.CoveredMonth).ThenBy(a => a.ComponentCode)
                .ToListAsync(ct);

        // The 1420 recovery, named rather than swallowed by the "Other Deductions" residual — without it
        // a zero-net replacement payslip shows a gross, a residual lump, and no reason.
        // Summed client-side over a handful of rows: SQLite (the test provider) cannot translate a decimal
        // SUM, and the same client-side pattern is already used for statutoryLines just above.
        var recoveryTotal = (await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == payslip.PayrollRunId
                     && d.EmployeeId == slip.EmployeeId
                     && (d.Source == PayrollRecoveryComponents.RecoverySource
                         || d.ComponentCode == PayrollRecoveryComponents.ReceivableRecovery))
            .Select(d => d.Amount)
            .ToListAsync(ct)).Sum();

        // No run row ⇒ no period ⇒ no honest "joined on" test, so the narrative is skipped rather than
        // guessed. Unreachable in practice (the payslip carries PayrollRunId), and silent when it happens.
        var prorationNote = run is null
            ? string.Empty
            : ProrationCalculator.NarrativeFromSlip(slip, new DateOnly(run.Year, run.Month, 1));
        string Label(string name) => string.IsNullOrEmpty(prorationNote) ? name : $"{name} ({prorationNote})";

        // Build items from stored slip fields (no recompute)
        var items = new List<PayslipLineItem>();
        if (slip.BasicSalary > 0)        items.Add(new(Label("Basic Salary"),  canSeeSalary ? slip.BasicSalary        : 0m, "Earning"));
        if (slip.HousingAllowance > 0)   items.Add(new("Housing Allowance",    canSeeSalary ? slip.HousingAllowance   : 0m, "Earning"));
        if (slip.TransportAllowance > 0) items.Add(new("Transport Allowance",  canSeeSalary ? slip.TransportAllowance : 0m, "Earning"));
        // slip.OtherAllowances INCLUDES the arrears (PayrollController.cs:2316), so the arrears are lifted
        // OUT of it before the residual is shown — itemising them on top of the unchanged bucket would
        // overstate the gross on the page by exactly the arrears amount.
        var otherAllowancesExArrears = slip.OtherAllowances - slip.ArrearsAmount;
        if (otherAllowancesExArrears > 0) items.Add(new("Other Allowances", canSeeSalary ? otherAllowancesExArrears : 0m, "Earning"));
        foreach (var a in arrearsLines)
            items.Add(new($"Arrears — {PayrollArrearsComponents.Label(a.ComponentCode)} ({a.CoveredYear}-{a.CoveredMonth:D2})",
                canSeeSalary ? a.Amount : 0m, "Earning"));
        // Any arrears the slip carries that no line accounts for is still shown, never dropped silently.
        var unitemisedArrears = slip.ArrearsAmount - arrearsLines.Sum(a => a.Amount);
        if (unitemisedArrears > 0) items.Add(new("Arrears", canSeeSalary ? unitemisedArrears : 0m, "Earning"));

        foreach (var line in statutoryLines.Where(l => l.ComponentCode.EndsWith("-EE", StringComparison.OrdinalIgnoreCase) && l.Amount > 0))
            items.Add(new(line.ComponentName, canSeeSalary ? line.Amount : 0m, "Deduction"));

        if (recoveryTotal > 0)
            items.Add(new(PayrollRecoveryComponents.ReceivableRecoveryName, canSeeSalary ? recoveryTotal : 0m, "Deduction"));

        // Non-statutory deductions (loans, advances, fixed)
        var otherDeductions = slip.Deductions
                            - statutoryLines.Where(l => l.ComponentCode.EndsWith("-EE", StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount)
                            - recoveryTotal;
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
    string? GlPostingPeriod = null,
    /// <summary>POD-C3 — does this run SETTLE retro/arrears? Defaults to true for the period-owning types
    /// (Regular, Replacement) and false for the supplemental ones, where paying a backdated increment out
    /// of band is a deliberate act. With no retro-effective salary assignment in existence the engine
    /// produces zero lines either way.</summary>
    bool? SettlesArrears = null,
    /// <summary>POD-C3 — does this run NET the 1420 Employee Overpayment Receivable a prior void
    /// recognised? Default OFF: recovering an overpayment out of someone's salary is an act the operator
    /// chooses, never a side effect of re-running a month.</summary>
    bool? NetsPriorReceivable = null,
    /// <summary>POD-C1 — is this run the DISBURSEMENT VEHICLE for approved termination settlements? Only
    /// an OffCycle or Supplementary run may be one, it must NOT pay recurring salary, and it is what makes
    /// a leaver (Offboarded, final wage month already paid) eligible for a run at all.</summary>
    bool? SettlesFinalSettlements = null);

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
// POD-B3 — ExpectedOverriddenCount is the same acknowledgement for consciously CLEARED compliance
// errors. Optional and defaulted, so a run with no overrides (i.e. every run today) is unaffected.
public record PayrollDecisionRequest(string? Notes, int? ExpectedExcludedCount = null, int? ExpectedOverriddenCount = null);
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
/// <summary>
/// POD-C1 — every new member is optional and defaulted, so the pre-C1 caller still works verbatim.
/// <paramref name="LastWorkingDay"/> is retained for wire compatibility but is NO LONGER AUTHORITATIVE:
/// the date comes from the employee's offboarding record, and a divergence is reported as a warning.
/// </summary>
public record FinalSettlementRequest(
    int EmployeeId,
    DateOnly LastWorkingDay,
    /// <summary>Days of notice the WORKER failed to serve. Only lawful on a resignation (Art. 75/76) —
    /// see the compliance warning raised when the separation is employer-side.</summary>
    int NoticePeriodDaysShort = 0,
    string? TerminationReason = null,
    /// <summary>Days of notice the EMPLOYER did not serve, paid IN LIEU to the worker.</summary>
    int NoticePayDays = 0,
    decimal OtherDuesAmount = 0m,
    decimal OtherDeductionsAmount = 0m,
    string? Notes = null);

/// <summary>POD-C1 — the explicit determinations an approver must make before a settlement becomes a
/// real, disbursable liability. None of them are defaulted to "yes".</summary>
public record ApproveFinalSettlementRequest(
    /// <summary>Must equal the RESOLVED termination reason exactly. EmployeeOffboarding.SeparationType
    /// defaults to "Resignation", which silently applies the Art. 85 haircut — this is what stops an
    /// untouched default discounting a genuine termination.</summary>
    string? ConfirmTerminationReason = null,
    /// <summary>Records that the approver accepts gratuity computed on the BASIC wage floor rather than
    /// the Art. 84 "last wage". Required whenever the computed delta is non-zero.</summary>
    bool AcknowledgeWageBaseFloor = false,
    /// <summary>Records that the leaver's final-month wages are genuinely UNPAID and authorises adding
    /// them to this settlement as an explicit other-dues line.</summary>
    bool AcknowledgeWagesUnpaid = false,
    string? WagesUnpaidReason = null,
    /// <summary>Records that the same person computed and approved the settlement.</summary>
    bool AcknowledgeSelfApproval = false,
    /// <summary>GL date for the accrual journal. Defaults to today; the period is guarded for close.</summary>
    DateOnly? AccrualDate = null,
    string? Reason = null);
