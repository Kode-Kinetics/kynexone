using System.Globalization;
using Zayra.Api.Infrastructure.Benefits;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Governance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Attendance;
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
[Zayra.Api.Infrastructure.Finance.GlCostCentreRuleFilter]
public partial class PayrollController : ControllerBase
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
    // The payroll-run computation (moved out of this controller); shared with the background job.
    private readonly PayrollRunProcessor _processor;

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
        _processor = new PayrollRunProcessor(db, packResolver, ruleReader, _taxResolver, _activationGuard, _prorationPolicy, _workWeek);
    }

    /// <summary>
    /// PAY INTERLOCK (§6): run the ONE readiness evaluator over the run's employees and split them into
    /// (a) HARD pay-blocks — under-documented / expired-ID / State==Blocked employees who are NOT Active
    /// (a wage file must never carry them), and (b) DRIFT warnings — already-Active employees who fell
    /// pay-blocked after a policy change: these are surfaced and require explicit acknowledgement rather
    /// than being SILENTLY dropped from a run (§6.6 — a silent exclusion could miss a mandated salary).
    /// </summary>
    private async Task<(HashSet<int> HardBlocked, HashSet<int> DriftWarn)> ComputePayReadinessAsync(
        Guid tenantId, IReadOnlyList<Employee> employees, CancellationToken ct, DateOnly? periodEnd = null)
    {
        var hard = new HashSet<int>();
        var drift = new HashSet<int>();
        foreach (var finding in await EvaluatePayReadinessAsync(tenantId, employees, ct, periodEnd))
            (finding.IsActive ? drift : hard).Add(finding.EmployeeId);
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

    /// <summary>
    /// Payroll → Grade packages (read-only): each grade's pay package as payroll will use it — the grade's lines, gross
    /// monthly cash, the employees on the grade and the grade's current salary structure — and, separately, the older
    /// structures that are not a grade's package but still have employees on them. Packages are edited in Setup → Grades
    /// (Benefits by grade for a release_a tenant, whose lines are read from the grade's structure).
    /// </summary>
    [HttpGet("grade-packages")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> GradePackages([FromQuery] Guid? companyId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var releaseA = await Infrastructure.Entitlements.EntitlementMatrixService.ReleaseAEnabledAsync(_db, tenantId, cancellationToken);
        var gradeQuery = _db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId && !g.IsDeleted);
        if (companyId.HasValue) gradeQuery = gradeQuery.Where(g => g.CompanyId == companyId || g.CompanyId == null);
        var grades = await gradeQuery.OrderBy(g => g.Level).ThenBy(g => g.Code).ToListAsync(cancellationToken);
        var gradeIds = grades.Select(g => g.Id).ToList();
        var gradeLines = releaseA
            ? new List<GradePayScaleComponent>()
            : await _db.GradePayScaleComponents.AsNoTracking()
                .Where(c => c.TenantId == tenantId && gradeIds.Contains(c.GradeId) && c.IsActive)
                .OrderBy(c => c.SortOrder).ToListAsync(cancellationToken);
        var structureQuery = _db.SalaryStructures.AsNoTracking().Where(s => s.TenantId == tenantId && !s.IsDeleted);
        if (companyId.HasValue) structureQuery = structureQuery.Where(s => s.CompanyId == companyId || s.CompanyId == null);
        var structures = await structureQuery.OrderBy(s => s.Name).ToListAsync(cancellationToken);
        var structureDtos = (await ProjectSalaryStructuresAsync(tenantId, structures, cancellationToken)).ToDictionary(d => d.Id);
        var employeeCounts = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active" && e.GradeId.HasValue && gradeIds.Contains(e.GradeId.Value))
            .GroupBy(e => e.GradeId!.Value)
            .Select(g => new { GradeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GradeId, x => x.Count, cancellationToken);
        var companyIds = grades.Where(g => g.CompanyId.HasValue).Select(g => g.CompanyId!.Value).Distinct().ToList();
        var companyNames = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.TradeName != "" ? c.TradeName : c.LegalNameEn, cancellationToken);

        var claimed = new HashSet<Guid>();
        var packages = new List<GradePackageDto>();
        foreach (var grade in grades)
        {
            var chain = structures.Where(s => s.CompanyId == grade.CompanyId && GradeSalaryStructureSync.IsGradeStructureCode(s.Code, grade.Code)).ToList();
            claimed.UnionWith(chain.Select(s => s.Id));
            var current = chain.OrderByDescending(s => s.IsActive).ThenByDescending(s => s.VersionNumber).FirstOrDefault();
            var lines = releaseA
                ? (current is null ? new List<GradePackageLineDto>() : structureDtos[current.Id].Components
                    .Where(c => c.IsActive)
                    .Select(c => new GradePackageLineDto(c.Code, c.Name, c.ComponentType, GradePackageMath.IsPercent(c.CalculationType) ? "PercentOfBasic" : "Fixed",
                        c.Amount, c.Percentage, c.IsTaxable, "Monthly", 0m)).ToList())
                : gradeLines.Where(c => c.GradeId == grade.Id)
                    .Select(c => new GradePackageLineDto(c.ComponentCode, c.ComponentName, c.ComponentType, c.CalculationType,
                        c.Amount, c.Percentage, c.IsTaxable, c.Frequency, 0m)).ToList();
            var math = lines.Select(l => new GradePackageLine(l.Code, l.ComponentType, l.CalculationType, l.Amount, l.Percentage, l.Frequency)).ToList();
            var basic = GradePackageMath.Basic(math);
            lines = lines.Select((l, i) => l with { PeriodAmount = GradePackageMath.AmountOf(math[i], basic) }).ToList();
            var gross = GradePackageMath.MonthlyGross(math);
            packages.Add(new GradePackageDto(
                grade.Id, grade.Code, grade.Name, grade.NameAr, grade.CompanyId,
                grade.CompanyId.HasValue && companyNames.TryGetValue(grade.CompanyId.Value, out var companyName) ? companyName : null,
                grade.Currency, grade.MinSalary, grade.MaxSalary, grade.IsActive, lines, gross,
                GradePackageMath.MonthlyDeductions(math),
                GradePackageMath.OutsideRange(gross, grade.MinSalary, grade.MaxSalary),
                employeeCounts.GetValueOrDefault(grade.Id),
                current?.Code, current?.VersionNumber,
                chain.Sum(s => structureDtos[s.Id].AssignedEmployeeCount)));
        }
        var older = structureDtos.Values
            .Where(d => !claimed.Contains(d.Id) && d.AssignedEmployeeCount > 0)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(new GradePackagesView(releaseA ? "BenefitsByGrade" : "GradePayScale", packages, older));
    }

    // ── Salary structures follow grades ─────────────────────────────────────────────────────────────
    // A pay package is defined on its grade (Setup → Grades); the grade's salary structure is created and updated from
    // it (GradeSalaryStructureSync). The structure endpoints therefore read only. The write routes stay mapped so an old
    // client gets a clear answer instead of a 404 or 405.

    /// <summary>The answer every salary-structure write gets: 409 salary_structures_follow_grades, pointing at Setup → Grades.</summary>
    internal static object SalaryStructuresFollowGradesBody => new
    {
        error = SalaryStructuresFollowGrades,
        message = "Salary structures are no longer created or edited here. Define the pay package on its grade in Setup → Grades; "
                  + "the grade's salary structure is created and updated from it automatically.",
        setupHref = "/setup?tab=grades",
    };

    public const string SalaryStructuresFollowGrades = "salary_structures_follow_grades";

    [HttpPost("salary-structures")]
    [HasPermission("payroll.write")]
    public IActionResult CreateSalaryStructure(SalaryStructureRequest req) => Conflict(SalaryStructuresFollowGradesBody);

    [HttpPut("salary-structures/{id:guid}")]
    [HasPermission("payroll.write")]
    public IActionResult UpdateSalaryStructure(Guid id, SalaryStructureRequest req) => Conflict(SalaryStructuresFollowGradesBody);

    [HttpDelete("salary-structures/{id:guid}")]
    [HasPermission("payroll.write")]
    public IActionResult DeleteSalaryStructure(Guid id) => Conflict(SalaryStructuresFollowGradesBody);

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

    private static string? ValidateSalaryStructureEligibility(SalaryStructure structure, Employee employee, EmployeeSalaryStructureRequest req, bool checkEffectiveDate = true)
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
        if (checkEffectiveDate && req.EffectiveDate < structure.EffectiveDate)
            return $"Salary assignment cannot start before salary structure effective date {structure.EffectiveDate:yyyy-MM-dd}.";
        return null;
    }

    /// <param name="checkEffectiveDate">False for the employee's automatic default structure: a derived structure's date is
    /// when it was derived from the grade, not a pay rule, so it must not refuse a back-dated salary.</param>
    internal static string? ValidateEmployeeSalaryAssignment(SalaryStructure structure, Employee employee, EmployeeSalaryStructureRequest req, bool checkEffectiveDate = true)
    {
        if (!structure.IsActive) return "Salary structure is inactive.";
        if (structure.CompanyId.HasValue && employee.CompanyId.HasValue && structure.CompanyId != employee.CompanyId)
            return "Salary structure belongs to a different legal entity than the employee.";
        return ValidateSalaryStructureEligibility(structure, employee, req, checkEffectiveDate);
    }

    private static HashSet<Guid> ReadGuidSet(string? json)
    {
        try { return (string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<Guid>>(json) ?? []).ToHashSet(); }
        catch { return []; }
    }

    private static string FormatGuidSet(string? json) => string.Join(';', ReadGuidSet(json));

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
        // The subject never decides: nobody proposes a change to their own pay, whatever their role.
        if (await Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, GetUserId(), employee.Id, cancellationToken))
            return BadRequest(Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.Refusal(OwnSalaryRefusal));
        // Staging is pointless if nobody else could ever approve it: say so now, not when the queue will not drain.
        if (await Zayra.Api.Infrastructure.Approvals.SecondApproverCheck.RefuseIfNoneAsync(_db, tenantId, "payroll.approve", GetUserId(), [employee.Id], employee.CompanyId,
                "proposing salary changes", "اقتراح تغييرات الرواتب", cancellationToken) is { } noApprover)
            return noApprover;
        SalaryStructure? structure;
        var automatic = req.SalaryStructureId is not { } named || named == Guid.Empty;
        if (!automatic && req.SalaryStructureId is { } requestedStructureId)
            structure = await _db.SalaryStructures.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == requestedStructureId && !x.IsDeleted, cancellationToken);
        else
        {
            // No structure named: the employee's default — the grade's package structure (created from the grade when it
            // does not exist yet), or DIRECT for an employee without a grade. Structures are not chosen by hand any more.
            try { structure = await GradeSalaryStructureSync.EnsureDefaultStructureAsync(_db, employee, GetUserId(), cancellationToken); }
            catch (SalaryStructureRefusedException ex)
            {
                return UnprocessableEntity(new { error = ex.ErrorCode, code = ex.ErrorCode, salaryStructureCode = ex.StructureCode, message = ex.Message });
            }
        }
        if (structure is null) return BadRequest(new { message = "Salary structure not found." });
        var eligibilityError = ValidateEmployeeSalaryAssignment(structure, employee, req, checkEffectiveDate: !automatic);
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
        // HR proposes; a Finance approver who is neither the maker nor this employee applies it. Nothing reaches the
        // employee's salary schedule (and so payroll) until then.
        var assignmentCurrency = !string.IsNullOrWhiteSpace(req.Currency) ? req.Currency : await ResolveCurrencyAsync(tenantId, cancellationToken);
        var change = await StageSalaryChangeAsync(tenantId, employee, structure.Id,
            new EmployeeSalaryStructureRequest(req.EmployeeId, structure.Id, req.BasicSalary, req.HousingAllowance, req.TransportAllowance, req.FoodAllowance,
                req.MobileAllowance, req.OtherAllowance, req.FixedDeduction, req.EffectiveDate, assignmentCurrency),
            "Manual", null, cancellationToken);
        await PayrollAudit("payroll.employee_salary.change_requested", "SalaryChangeRequest", change.Id.ToString(),
            new { employeeId = req.EmployeeId, basicSalary = req.BasicSalary, effectiveDate = req.EffectiveDate }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Accepted($"/api/payroll/salary-changes/{change.Id}", SalaryChangeDto.Project(change));
    }

    [HttpGet("runs")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ListRuns([FromQuery] Guid? companyId, [FromQuery] string? status, [FromQuery] string? runType, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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
    /// Queues the run for processing and answers at once (202). The computation runs on the durable
    /// background job queue (<see cref="PayrollRunProcessJobHandler"/>), so a large company no longer holds a
    /// request, a proxy connection or a double-click open. The permission, the run lookup (tenant and company
    /// scope) and the run-state refusals are unchanged and still answered here, synchronously, before anything
    /// is queued. A second POST while the run's job is still Queued or Running returns THAT job.
    /// Poll <c>GET runs/{id}/processing</c> for progress and the outcome.
    /// </summary>
    [HttpPost("runs/{id:guid}/process")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> EnqueueProcess(
        Guid id, [FromServices] PayrollRunProcessingService processing, CancellationToken cancellationToken)
    {
        var scope = this.GetEntityScope();
        var caller = new PayrollProcessCaller(
            GetTenantId(), GetUserId(), HttpContext.Connection.RemoteIpAddress?.ToString(),
            scope.IsGroupLevel, scope.IsGroupLevel ? Array.Empty<Guid>() : scope.AccessibleCompanyIds.ToArray());
        var result = await processing.EnqueueAsync(id, caller, cancellationToken);
        return result.Accepted is { } accepted
            ? Accepted($"/api/payroll/runs/{id}/processing", accepted)
            : ToActionResult(result.Refusal!);
    }

    /// <summary>
    /// Where the run's processing job stands: Idle (never queued), Queued, Running, Succeeded or Failed, with
    /// employee progress and, for a failure, a code and a message that are safe to show a user.
    /// Same read permission and the same tenant / company scope as the run itself; a run the caller cannot
    /// see is a 404.
    /// </summary>
    [HttpGet("runs/{id:guid}/processing")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> ProcessingStatus(
        Guid id, [FromServices] PayrollRunProcessingService processing, CancellationToken cancellationToken)
    {
        var status = await processing.GetStatusAsync(id, GetTenantId(), cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>
    /// Runs the pass in the calling process and returns the result the endpoint used to give. Not routable:
    /// the API queues the work (see <see cref="EnqueueProcess"/>); this exists for in-process callers and the
    /// test suites that exercise the computation directly.
    /// </summary>
    [NonAction]
    public async Task<IActionResult> Process(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await _processor.ProcessAsync(
            new PayrollProcessRequest(id, GetTenantId(), GetUserId(), _http.HttpContext?.Connection.RemoteIpAddress?.ToString()),
            hooks: null, cancellationToken);
        return ToActionResult(outcome);
    }

    private IActionResult ToActionResult(PayrollProcessOutcome outcome) => outcome.Generic ? StatusCode(outcome.StatusCode, outcome.Body) : outcome.StatusCode switch
    {
        200 => Ok(outcome.Run),
        400 => BadRequest(outcome.Body),
        404 => NotFound(),
        409 => Conflict(outcome.Body),
        422 => UnprocessableEntity(outcome.Body),
        _ => StatusCode(outcome.StatusCode, outcome.Body),
    };

    // POD-B2: run population selector (include / exclude with an audited reason).
    // Statuses in which the population may still be changed. This is Process's own refusal list plus
    // "Voided": changing who is in a run after it has produced slips requires a re-Process, and a voided
    // run is finished. Kept as one list so the selector and Process can never disagree.
    private static readonly string[] PopulationLockedStatuses =
        { "Processed", "PendingFinanceReview", "Locked", "Approved", "Paid", "Voided" };

    // ── Thin forwards: helpers the processing pass needs that other endpoints in this controller use too.
    //    The implementations live in PayrollRunProcessor (one copy). ─────────────────────────────────────

    private Task<IReadOnlyList<PayrollRunProcessor.PayReadinessFinding>> EvaluatePayReadinessAsync(
        Guid tenantId, IReadOnlyList<Employee> employees, CancellationToken ct, DateOnly? periodEnd = null)
        => _processor.EvaluatePayReadinessAsync(tenantId, employees, ct, periodEnd);

    private Task<List<PayrollValidationResult>> ReadinessPayValidationFindingsAsync(
        Guid tenantId, Guid runId, IReadOnlyList<Employee> employees, DateOnly periodEnd, CancellationToken ct)
        => _processor.ReadinessPayValidationFindingsAsync(tenantId, runId, employees, periodEnd, ct);

    private Task<PayrollRunProcessor.RunPopulation> ResolveRunPopulationAsync(
        Guid tenantId, PayrollRun run, List<Employee> eligible, CancellationToken ct)
        => _processor.ResolveRunPopulationAsync(tenantId, run, eligible, ct);

    private Task<(List<Employee> Employees, HashSet<int> LeaverIds, Dictionary<int, DateOnly> LastWorkingDays,
                        List<(Employee Employee, string Reason)> UnpayableNonActive, HashSet<int> SettlementIds)>
        LoadEligibleWithLeaversAsync(
            Guid tenantId, Guid companyId, bool allowLegacyUnscopedEmployees, bool asNoTracking,
            DateOnly? periodStart, DateOnly? periodEnd, Guid? excludeRunId, CancellationToken ct,
            bool includeSettlementLeavers = false)
        => _processor.LoadEligibleWithLeaversAsync(tenantId, companyId, allowLegacyUnscopedEmployees, asNoTracking, periodStart, periodEnd, excludeRunId, ct, includeSettlementLeavers);

    private Task<List<PayrollRun>> LoadSiblingRunsAsync(Guid tenantId, PayrollRun run, Guid companyId, CancellationToken ct)
        => _processor.LoadSiblingRunsAsync(tenantId, run, companyId, ct);

    private static Dictionary<int, decimal> BuildPriorPeriodGosiEe(
        IReadOnlyDictionary<(int EmployeeId, string Code, bool IsEmployer), decimal> priorStatutory)
        => PayrollRunProcessor.BuildPriorPeriodGosiEe(priorStatutory);

    private Task<IReadOnlySet<int>> UnverifiedImportedBankDetailsAsync(Guid tenantId, List<int> employeeIds, CancellationToken ct)
        => _processor.UnverifiedImportedBankDetailsAsync(tenantId, employeeIds, ct);

    internal static string EarningDriverKeyFor(IReadOnlyList<GlDriverRow> drivers, string componentCode, string source)
        => PayrollRunProcessor.EarningDriverKeyFor(drivers, componentCode, source);

    private static string DeductionDriverKey(string componentCode, string source, out bool isEmployerSide)
        => PayrollRunProcessor.DeductionDriverKey(componentCode, source, out isEmployerSide);

    private static GlDriverRow? ResolveDeductionDriverRow(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source)
        => PayrollRunProcessor.ResolveDeductionDriverRow(drivers, componentCode, source);

    private Task<GlResolutionContext> LoadGlResolutionContextAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
        => _processor.LoadGlResolutionContextAsync(tenantId, companyId, ct);

    private Task<IReadOnlyList<PayComponent>> LoadPayComponentsAsync(
        Guid tenantId, Guid? companyId, DateOnly periodStart, CancellationToken ct)
        => _processor.LoadPayComponentsAsync(tenantId, companyId, periodStart, ct);

    private Task<IReadOnlyList<PaidAttendanceChange>> UnresolvedPaidAttendanceChangesAsync(
        Guid tenantId, DateOnly from, DateOnly to, IReadOnlyCollection<int> employeeIds, CancellationToken ct)
        => _processor.UnresolvedPaidAttendanceChangesAsync(tenantId, from, to, employeeIds, ct);

    private Task<DateOnly?> GosiRatesEffectiveFromAsync(string? countryCode, CancellationToken ct)
        => _processor.GosiRatesEffectiveFromAsync(countryCode, ct);

    [HttpPost("runs/{id:guid}/lock")]
    [HasPermission("payroll.lock")]
    public async Task<IActionResult> Lock(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();

        // ── P0: ONE accrual per run, even under concurrent Lock requests ─────────────────────────────
        // Lock used to read the run, probe "already posted?", AddRange the journal and set Locked with no
        // transaction, no row lock and no concurrency token. Two Lock clicks (a double-submit, a retry
        // behind a slow proxy, two finance users) both read Approved, both saw no accrual, and both
        // posted a FULL accrual — every payroll liability and expense booked twice. The whole Lock now
        // runs in one transaction that first takes FOR UPDATE on the payroll_runs row: the second caller
        // waits, then reads Status == "Locked" and gets a plain 409. Everything (journal, run status,
        // slips, payslips, audit) commits or rolls back together. EnableRetryOnFailure requires the
        // transaction inside the execution strategy, and the delegate is safe to re-run from scratch.
        // EF InMemory (unit tests) has no transactions or row locks, so it runs the same body directly;
        // an ambient transaction opened by a caller is joined, and that caller owns the commit.
        LockOutcome outcome;
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            outcome = await LockCoreAsync(id, tenantId, cancellationToken);
        }
        else
        {
            var attempt = 0;
            var strategy = _db.Database.CreateExecutionStrategy();
            outcome = await strategy.ExecuteAsync(async () =>
            {
                if (attempt++ > 0) _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
                var attemptOutcome = await LockCoreAsync(id, tenantId, cancellationToken);
                // A refusal writes nothing (or, for the cash/cheque send-back, writes exactly the
                // send-back): only an outcome that asks for it is committed.
                if (attemptOutcome.Commit) await tx.CommitAsync(cancellationToken);
                return attemptOutcome;
            });
        }

        // Notifications are best-effort and happen only after the lock is durably committed.
        if (outcome.LockedRun is { } lockedRun)
            await NotifyPayslipsReadyAsync(tenantId, lockedRun, cancellationToken);
        return outcome.Result;
    }

    /// <summary>What one Lock attempt decided: the HTTP answer, whether its writes must be committed, and
    /// (on success) the run whose employees are told their payslip is ready once the commit is durable.</summary>
    private sealed record LockOutcome(IActionResult Result, bool Commit, PayrollRun? LockedRun = null);

    private async Task<LockOutcome> LockCoreAsync(Guid id, Guid tenantId, CancellationToken cancellationToken)
    {
        // FOR UPDATE: serialises every Lock (and Void, which takes the same row lock) on this run. Inside
        // the transaction opened by Lock; a no-op tag on providers without the interceptor.
        var run = await _db.PayrollRuns.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return new(NotFound(), false);
        if (run.Status == "Locked")
            return new(Conflict(new
            {
                error   = "run_already_locked",
                message = "This payroll run is already locked and its journal is already posted. Nothing was posted again.",
                status  = run.Status,
            }), false);
        if (run.Status != "Approved") return new(BadRequest(new { message = "Only approved payroll runs can be locked." }), false);

        // AM-8 / AM-29: Approve, the cost stamp and Lock hold the per-(tenant, company) cost-accounting advisory lock, so no attribution,
        // mapping, rule or switch writer can commit between the drift check below and the stamp. Released at commit or rollback.
        if (run.CompanyId is Guid costLockCompany)
            await Infrastructure.Finance.CostAccountingGuards.AcquireCompanyLockAsync(_db, tenantId, costLockCompany, cancellationToken);

        // Block-on-error: any unresolved ERROR-severity validation result blocks locking.
        var blockingErrors = await _db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.Severity == "Error" && !x.IsResolved)
            .Select(x => new { x.Code, x.Message, x.EmployeeId })
            .ToListAsync(cancellationToken);
        if (blockingErrors.Count > 0)
            return new(UnprocessableEntity(new
            {
                error   = "validation_errors",
                message = $"Run has {blockingErrors.Count} unresolved validation error(s). " +
                          "Run /validate and resolve all errors before locking.",
                errors  = blockingErrors,
            }), false);

        // ── Cash / cheque pay is FROZEN here ─────────────────────────────────────────────────────────
        // The approver acknowledged, by count, who is paid outside the bank file (PAID_OUTSIDE_BANK_FILE*
        // warnings). Lock freezes those methods into its sealed audit entry; the payment batch reads them
        // from there. If a profile changed since validation, the acknowledged set is stale: refuse rather
        // than freeze something nobody approved.
        var lockSlipEmployeeIds = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RunId == id && s.NetSalary > 0m).Select(s => s.EmployeeId).ToListAsync(cancellationToken);
        var paidOutsideAtLock = (await _db.EmployeePayrollProfiles.AsNoTracking()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && lockSlipEmployeeIds.Contains(p.EmployeeId))
                .Select(p => new { p.EmployeeId, p.PaymentMethod }).ToListAsync(cancellationToken))
            .Where(p => Infrastructure.Payroll.PaymentBatchExclusions.IsPaidOutsideBankFile(p.PaymentMethod))
            .GroupBy(p => p.EmployeeId)
            .Select(g => new { employeeId = g.Key, method = g.First().PaymentMethod.Trim() })
            .OrderBy(x => x.employeeId).ToList();
        // Compared against the list sealed into the APPROVAL audit, not the validation results: re-running
        // /validate refreshes the warnings but is not an approval, so it can never make a stale run lockable.
        var acknowledgedOutside = await Infrastructure.Payroll.PaymentBatchExclusions.ApprovedOutsideAsync(_db, tenantId, id, cancellationToken)
                                  ?? new HashSet<int>();
        if (!acknowledgedOutside.SetEquals(paidOutsideAtLock.Select(x => x.employeeId)))
        {
            // Send the run back for a fresh approval (the existing send-back transition): the approver must
            // see and acknowledge the current cash/cheque list before anything is frozen.
            _db.PayrollApprovals.Add(new PayrollApproval
            {
                TenantId = tenantId, PayrollRunId = id, ApprovalLevel = "FinanceReview", Decision = "SentBack",
                Notes = "Sent back automatically at Lock: the cash/cheque list changed after approval.",
                DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow,
            });
            run.Status = "Processed";
            await PayrollAudit("payroll.run.sent_back", "PayrollRun", id.ToString(), new
            {
                reason = "outside_bank_payments_changed",
                approved = acknowledgedOutside.OrderBy(x => x).ToList(),
                current  = paidOutsideAtLock.Select(x => x.employeeId).ToList(),
            }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            // The send-back IS the answer, so this refusal commits (it is the one that writes).
            return new(Conflict(new
            {
                error   = "outside_bank_payments_changed",
                message = "Who is paid by cash or cheque changed after this run was approved, so it has been sent back for " +
                          "approval. Run validation (POST runs/{id}/validate), approve it again acknowledging the current list, then lock.",
                approved = acknowledgedOutside.OrderBy(x => x).ToList(),
                current  = paidOutsideAtLock.Select(x => x.employeeId).ToList(),
                status   = run.Status,
            }), true);
        }

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

        // A run may only become finance-ready in its legal entity's currency. Resolve this before either
        // branch so the idempotent "already posted" path is held to the same controls as a new journal.
        // Otherwise a support-restored/legacy accrual can be stamped ReadyForErp merely because a row
        // exists, even when that row is one-sided, unmapped, or denominated in another currency.
        var glCompany = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId, cancellationToken);
        if (glCompany is null || string.IsNullOrWhiteSpace(glCompany.DefaultCurrency))
            return new(UnprocessableEntity(new
            {
                error = "gl_company_currency_missing",
                message = "Payroll cannot be locked until its legal entity and company currency are configured.",
                companyId = run.CompanyId,
            }), false);
        var glCurrency = glCompany.DefaultCurrency.Trim().ToUpperInvariant();

        // AM-14 / AM-29: a user who changed a posting input of this company since its last lock — or an API key that user created —
        // cannot lock its run, whatever permissions they hold. By identity; only while the company's switch is ON.
        if (run.CompanyId is Guid sodCompany && GetUserId() is Guid lockerId
            && await Infrastructure.Finance.CostAccountingSod.HasConflictAsync(_db, tenantId, sodCompany, period, lockerId, cancellationToken))
            return new(StatusCode(403, Infrastructure.Finance.CostAccountingSod.RefusalBody), false);

        // S-053: payroll stores amounts at 2 dp (D-1), so a 3-dp currency would silently lose fils at compute while the ledger and exports
        // carry 3. With costing on that is refused up front rather than posted wrong.
        if (glCompany.CostAccountingEnabled && CurrencyMinorUnits.Scale(glCurrency) == 3)
            return new(UnprocessableEntity(new
            {
                error = "currency_scale_unsupported_for_payroll",
                message = $"Cost accounting cannot post payroll in {glCurrency} yet: payroll amounts are stored to two decimals, so the third decimal would be lost. Switch cost accounting off for this company, or wait for the payroll storage change.",
                messageAr = $"لا يمكن ترحيل الرواتب بعملة {glCurrency} مع محاسبة التكلفة حاليًا: تُخزَّن مبالغ الرواتب بمنزلتين عشريتين فتُفقد المنزلة الثالثة. أوقف محاسبة التكلفة لهذه الشركة، أو انتظر إصدارًا لاحقًا يدعم ذلك.",
                currency = glCurrency,
            }), false);

        // The cost-accounting context the readiness checks read (account rules), only when the switch is on.
        var costingGl = glCompany.CostAccountingEnabled
            ? await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken)
            : null;

        if (alreadyPosted)
        {
            var liveAccrual = await _db.FinanceGlEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id
                         && !x.IsReversed
                         && (x.EventType == GlEventTypes.Accrual
                             || x.EventType == GlEventTypes.BonusPayrollClearing
                             || x.EventType == GlEventTypes.SettlementPayrollClearing))
                .ToListAsync(cancellationToken);
            var readiness = ValidatePayrollGl(liveAccrual, glCurrency, costingGl);
            if (!readiness.IsReady)
                return new(UnprocessableEntity(new
                {
                    error = "gl_not_finance_ready",
                    message = "The existing payroll accrual is not finance-ready. Correct or reverse it before locking.",
                    readiness.TotalDebits,
                    readiness.TotalCredits,
                    readiness.Difference,
                    expectedCurrency = glCurrency,
                    readiness.Currencies,
                    readiness.UnmappedEntryIds,
                    readiness.EmptySidedEntryIds,
                    companyBalances = readiness.CompanyBalances,
                }), false);
        }
        IReadOnlyList<PayrollGlCatchAll> lockCatchAlls = Array.Empty<PayrollGlCatchAll>();
        var lockCostPlan = PayrollCostPlan.Off;
        decimal lockRounding = 0m;
        decimal lockEosbMovement = 0m;
        var lockEosbEmployees = 0;
        if (!alreadyPosted)
        {
            // POD-B1 — a closed GL period rejects NEW postings. Dated into the accrual (run) period, so
            // this only ever fires for an explicitly closed period; a re-lock that posts nothing (already
            // posted) is unaffected because the guard sits inside the not-yet-posted branch.
            if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run.CompanyId, period, cancellationToken))
                return new(UnprocessableEntity(new
                {
                    error     = "gl_period_closed",
                    message   = $"GL period {period} is closed. Reopen it before locking payroll into this period.",
                    period,
                    companyId = run.CompanyId,
                }), false);
            // The SAME builder the GL preview projects (preview == post, settlement clearing included).
            var plan = await BuildLockJournalAsync(tenantId, run, period, glCurrency, GetUserId(), GetUserName(), cancellationToken);

            // ── AM-8: the approval hash is the control. The journal was built from ONE consistent read; if its fingerprint is not the
            // one the approver sealed — costing switched on or off since, a NULL hash with the switch on, or ANY input that changes what
            // posts — the run goes back for approval. A pre-costing run (switch off, no hash) is untouched.
            var costingNow = glCompany.CostAccountingEnabled;
            var sealedHash = run.CostCenterResolutionHash;
            if ((costingNow || sealedHash is not null)
                && (!costingNow || sealedHash is null || plan.CostPlan.Enabled != costingNow
                    || !string.Equals(plan.CostHash, sealedHash, StringComparison.Ordinal)))
                return await SendBackForCostChangeAsync(run, tenantId, sealedHash is not null, costingNow, cancellationToken);

            // Fail CLOSED on a switched-off account or system posting rule: treated exactly like an
            // unmapped account, never silently re-routed to some other account.
            if (plan.Problems.Count > 0)
                return new(UnprocessableEntity(new
                {
                    // Every problem is a code missing from the chart → its own code; anything else (a
                    // switched-off account or rule, alone or mixed) keeps the established one.
                    error    = plan.Problems.All(p => p.Kind == GlResolutionProblemKinds.NotInChart)
                        ? "gl_account_not_in_chart" : "gl_posting_rule_inactive",
                    message  = plan.Problems.Count == 1
                        ? plan.Problems[0].Message
                        : $"{plan.Problems.Count} posting rules need attention before this run can be locked. " +
                          string.Join(" ", plan.Problems.Select(p => p.Message)),
                    problems = plan.Problems.Select(p => new
                    {
                        driverKey = p.DriverKey, label = p.Label, kind = p.Kind,
                        accountCode = p.AccountCode, accountName = p.AccountName, message = p.Message,
                    }),
                }), false);
            if (plan.EosbRefusal is { } eosbRefusal)
                return new(UnprocessableEntity(new
                {
                    error   = eosbRefusal.Code,
                    message = eosbRefusal.Message,
                    companyId = run.CompanyId,
                }), false);
            // Contract 2.7: every cost-centre refusal is collected across ALL rows (not first-fail) and nothing has been written.
            if (plan.CostPlan.Blockers.Count > 0)
                return new(UnprocessableEntity(PayrollCostStamper.BuildRefusal(plan.CostPlan.Blockers, plan.CostPlan.SmallCellMin).Body), false);
            if (plan.RoundingBlock is { } roundingBlock)
                return new(UnprocessableEntity(PayrollCostStamper.RoundingRefusal(roundingBlock.AccountCode, roundingBlock.AccountName, roundingBlock.Amount, glCurrency)), false);
            var glLines = plan.Lines;
            var totalDebits = plan.TotalDebits;
            var totalCredits = plan.TotalCredits;
            var readiness = ValidatePayrollGl(glLines, glCurrency, plan.Context);
            if (!readiness.IsReady)
            {
                var currencyMismatch = readiness.Currencies.Count != 1
                    || !string.Equals(readiness.Currencies.SingleOrDefault(), glCurrency, StringComparison.OrdinalIgnoreCase);
                var companyOff = readiness.Difference <= 0.01m
                    && readiness.CompanyBalances.Any(c => c.Difference > 0.01m);
                var error = readiness.UnmappedEntryIds.Count > 0 ? "gl_mapping_missing"
                    : currencyMismatch ? "gl_currency_mismatch"
                    : readiness.EmptySidedEntryIds.Count > 0 ? "gl_invalid_entry"
                    : companyOff ? "gl_company_unbalanced"
                    : "gl_unbalanced";
                var message = error switch
                {
                    "gl_mapping_missing" => "Payroll GL contains unmapped accounts. Complete the company GL mappings before locking.",
                    "gl_currency_mismatch" => "Payroll GL must be denominated solely in the legal entity's company currency before locking.",
                    "gl_invalid_entry" => "Payroll GL contains an entry with neither a debit nor a credit account.",
                    "gl_company_unbalanced" => "Payroll GL balances in total but not for each legal entity: " +
                        string.Join("; ", readiness.CompanyBalances.Where(c => c.Difference > 0.01m)
                            .Select(c => $"{(c.CompanyId is Guid cid ? $"company {cid}" : "lines with no company")} has debits {c.Debits:0.00} and credits {c.Credits:0.00}")) +
                        ". A bonus or settlement being cleared belongs to a different company than this run. Clear it through a run of its own company.",
                    _ => "Payroll GL is not balanced in the company currency. Total debits must equal total credits before locking.",
                };
                return new(UnprocessableEntity(new
                {
                    error,
                    message,
                    totalDebits,
                    totalCredits,
                    readiness.Difference,
                    expectedCurrency = glCurrency,
                    readiness.Currencies,
                    readiness.UnmappedEntryIds,
                    readiness.EmptySidedEntryIds,
                    companyBalances = readiness.CompanyBalances,
                }), false);
            }
            lockCatchAlls = plan.CatchAlls;
            lockRounding = plan.RoundingAdjustment;
            lockCostPlan = plan.CostPlan;
            if (plan.CostPlan.Enabled)
            {
                await PersistCostStampsAsync(plan, run, tenantId, cancellationToken);
                run.CostAccountingApplied = true;
            }
            _db.FinanceGlEntries.AddRange(glLines);
            // POD-C2 — the obligation witnesses commit with the journal they explain, or not at all.
            if (plan.EosbSnapshots.Count > 0) _db.EosbProvisionSnapshots.AddRange(plan.EosbSnapshots);
            lockEosbMovement = plan.EosbSnapshots.Sum(x => x.Movement);
            lockEosbEmployees = plan.EosbSnapshots.Count;
        }

        run.Status = "Locked";
        run.LockedAtUtc = DateTime.UtcNow;
        run.ErpPostingStatus = ErpPostingStatuses.ReadyForErp;
        run.ErpPostingStatusChangedAtUtc = DateTime.UtcNow;
        await _db.PayrollSlips.Where(s => s.RunId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Final"), cancellationToken);
        // The run's employees' attendance days in its month are now payroll-locked: no range run, correction or late punch
        // may rewrite them (AttendancePayrollLock). A void releases them.
        await AttendancePayrollLock.LockAsync(_db, tenantId, id, run.Year, run.Month, GetUserId(), cancellationToken);
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
            // Frozen: PaymentBatchExclusions.PaidOutsideAtLockAsync reads exactly this.
            paidOutsideBankFile = paidOutsideAtLock,
            // Pay items that landed in a catch-all account (Other Earnings / Other Deductions) because no
            // posting rule names them. Non-blocking, but recorded where the month was sealed.
            catchAllComponents = lockCatchAlls.Select(c => new { componentCode = c.ComponentCode, label = c.Label, driverKey = c.DriverKey, amount = c.Amount }).ToList(),
            roundingAdjustment = lockRounding,
            eosbProvisionMovement = lockEosbMovement, eosbProvisionEmployees = lockEosbEmployees,
            costing = CostingSummary(lockCostPlan),
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        // JSON superset of the previous `Ok(run)` body — every PayrollRun property is still present at the
        // same path, so no existing consumer breaks.
        return new(Ok(new
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
            // Non-blocking warning: these pay items posted to a catch-all account. Map them to a
            // specific posting rule so the cost lands where it belongs next month.
            catchAllComponents  = lockCatchAlls.Select(c => new { componentCode = c.ComponentCode, label = c.Label, driverKey = c.DriverKey, amount = c.Amount }),
            // Sub-unit rounding booked to Payroll Rounding Differences so the stored journal balances
            // exactly (positive = debit, negative = credit; 0 = none needed).
            roundingAdjustment  = lockRounding,
            // POD-C2 — the end-of-service provision movement this Lock booked (0 when provisioning is off).
            eosbProvisionMovement = lockEosbMovement,
            eosbProvisionEmployees = lockEosbEmployees,
            // Cost accounting (WP-C2): whether the journal was costed, and the W-1 / W-2 warnings (counts and aggregates only).
            costing = CostingSummary(lockCostPlan),
        }), true, run);
    }

    private static object CostingSummary(PayrollCostPlan plan) => new
    {
        applied = plan.Enabled,
        fallbackEmployees = plan.FallbackEmployees, fallbackAmount = plan.FallbackAmount,
        uncostedEmployees = plan.UncostedEmployees, uncostedAmount = plan.UncostedAmount,
        rolledUpEmployees = plan.RolledUpFrom.Count,
    };

    /// <summary>AM-8: the run goes back to Processed (the outside_bank_payments_changed pattern) and its sealed hash is cleared.</summary>
    private async Task<LockOutcome> SendBackForCostChangeAsync(PayrollRun run, Guid tenantId, bool hadSealedHash, bool costingNow, CancellationToken ct)
    {
        _db.PayrollApprovals.Add(new PayrollApproval
        {
            TenantId = tenantId, PayrollRunId = run.Id, ApprovalLevel = "FinanceReview", Decision = "SentBack",
            Notes = "Sent back automatically at Lock: cost-centre assignments or posting rules changed after approval.",
            DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow,
        });
        run.Status = "Processed";
        run.CostCenterResolutionHash = null;
        await PayrollAudit("payroll.run.sent_back", "PayrollRun", run.Id.ToString(), new
        {
            reason = PayrollCostStamper.ChangedAfterApprovalCode, costingAtApproval = hadSealedHash, costingAtLock = costingNow,
        }, ct);
        await _db.SaveChangesAsync(ct);
        return new(Conflict(PayrollCostStamper.ChangedAfterApprovalBody(run.Status)), true);
    }

    /// <summary>Writes the plan's stamps: the two stamp columns of each costed earning / deduction row (the rows the journal was built
    /// from, attached and marked modified), and each slip's employee-level cost centre.</summary>
    private async Task PersistCostStampsAsync(PayrollLockJournal plan, PayrollRun run, Guid tenantId, CancellationToken ct)
    {
        foreach (var e in plan.Earnings.Where(e => e.CostCenterId != null))
        {
            _db.Attach(e);
            _db.Entry(e).Property(x => x.CostCenterId).IsModified = true;
            _db.Entry(e).Property(x => x.CostCenterSource).IsModified = true;
        }
        foreach (var d in plan.Deductions.Where(d => d.CostCenterId != null))
        {
            _db.Attach(d);
            _db.Entry(d).Property(x => x.CostCenterId).IsModified = true;
            _db.Entry(d).Property(x => x.CostCenterSource).IsModified = true;
        }
        // The slips are written through the change tracker with the rest of the Lock (no bulk statement touches the stamp columns).
        var slips = await _db.PayrollSlips.Where(s => s.TenantId == tenantId && s.RunId == run.Id).ToListAsync(ct);
        PayrollCostStamper.Apply(plan.CostPlan, Array.Empty<PayrollEarning>(), Array.Empty<PayrollDeduction>(), slips);
    }

    private async Task NotifyPayslipsReadyAsync(Guid tenantId, PayrollRun run, CancellationToken cancellationToken)
    {
        // Notify all employees with a payslip for this run
        var employeeIds = await _db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id && s.TenantId == tenantId).Select(s => s.EmployeeId).ToListAsync(cancellationToken);
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
    }

    [HttpGet("runs/{id:guid}/slips")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> Slips(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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
            .Select(d => new { d.EmployeeId, d.ComponentCode, d.ComponentName, d.Amount, d.Source, d.IsEmployerContribution })
            .ToListAsync(cancellationToken);
        var linesByEmployee = deductionsByEmployee
            .GroupBy(d => d.EmployeeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PayrollDeductionLineDto>)g.Select(d => new PayrollDeductionLineDto(d.ComponentCode, d.ComponentName, d.Amount, d.Source, d.IsEmployerContribution)).ToList());
        return Ok(new PagedResult<PayrollSlipDto>(
            items.Select(s => PayrollSlipDto.Project(s, true, linesByEmployee.TryGetValue(s.EmployeeId, out var dl) ? dl : null)).ToList(),
            total, page, pageSize));
    }

    /// <summary>Returns the last persisted validation snapshot without re-running the engine.</summary>
    [HttpGet("runs/{id:guid}/validate")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> ValidationResults(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (!await _db.PayrollRuns.AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken))
            return NotFound();

        var results = await _db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id)
            .OrderByDescending(x => x.Severity)
            .ThenBy(x => x.Code)
            .ToListAsync(cancellationToken);
        return Ok(results);
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

        var valYtdSources = await PayrollYtdBasis.LoadAsync(
            _db, tenantId, company.Id, run, slips.Select(s => s.EmployeeId).Distinct().ToList(), cancellationToken);
        var ctx     = new PayrollValidationContext(run, slips, employees, salaries, profiles, deductions, earnings, company)
        {
            UnverifiedImportedBankDetails  = await UnverifiedImportedBankDetailsAsync(
                tenantId, slips.Select(s => s.EmployeeId).ToList(), cancellationToken),
            OvertimeHoursByEmployee        = valOtHoursByEmp,
            AttendanceProcessedEmployeeIds = valAttendanceEmpIds,
            // Same source as Process, so /validate (which replaces the findings wholesale) keeps the warning.
            UnresolvedPaidAttendanceChanges = await UnresolvedPaidAttendanceChangesAsync(
                tenantId, valPeriodStart, valPeriodEnd, employeeIdsForValidation, cancellationToken),
            HeldMissingPunchDays = await AttendanceImpactReconciliation.FindHeldDaysAsync(
                _db, tenantId, employeeIdsForValidation, valPeriodStart, valPeriodEnd, cancellationToken),
            // Same source as Process, so /validate (which replaces the findings wholesale) keeps the warning.
            GosiRatesEffectiveFrom         = await GosiRatesEffectiveFromAsync(company.CountryCode, cancellationToken),
            HolidayCalendarCoversPeriod    = await CompanyPayrollRules.HasHolidayInPeriodAsync(
                _db, tenantId, company.Id, valPeriodStart, valPeriodEnd, cancellationToken),
            EmployeesAlreadyPaidRecurringThisPeriod = valAlreadyPaidEmpIds,
            // Mid-year cutover — re-derived with the SAME loader Process used; /validate replaces the
            // stored results wholesale, so omitting it would silently drop Rule 14b.
            YtdUnresolvedOverlapEmployeeIds         = valYtdSources.UnresolvedOverlapEmployeeIds,
            YtdPreCutoverExcludedEmployeeIds        = valYtdSources.PreCutoverSlipsExcludedEmployeeIds,
            YtdSuggestedCutover                     = valYtdSources.SuggestedCutover,
            Exclusions                              = validationPopulation.Exclusions,
            NotEligibleSelections                   = validationPopulation.NotEligible,
            SiblingRunCount                         = valSiblingRuns.Count,
            // Matches Process: the stored statutory amounts WERE netted incrementally whenever a sibling
            // run had already reported statutory for these employees, which is exactly this condition.
            StatutoryComputedIncrementally          = valPriorStatutory.Count > 0,
            PriorPeriodGosiEeByEmployee             = BuildPriorPeriodGosiEe(valPriorStatutory),
            // S1/A2(c) — same ceiling source as Process. /validate REPLACES the stored results
            // wholesale, so a version that read a different ceiling here would flip the warning on
            // and off depending on which endpoint last ran.
            GosiCoveredWageCeiling                  = await _ruleReader.GetDecimalAsync(
                "SAU", "KSA-mainland", "gosi.covered_wage_ceiling_sar",
                new DateOnly(run.Year, run.Month, 1), tenantId, cancellationToken) ?? 45_000m,
            GosiRetirementAgeYears                  = (int)(await _ruleReader.GetDecimalAsync(
                "SAU", "KSA-mainland", "gosi.retirement_age_years",
                new DateOnly(run.Year, run.Month, 1), tenantId, cancellationToken) ?? 0m),
            // Same platform rows as Process — /validate replaces the stored results wholesale.
            GosiMinContributoryWageAnnuities        = await _ruleReader.GetDecimalAsync(
                "SAU", "KSA-mainland", Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiMinContributoryWageAnnuitiesSar,
                new DateOnly(run.Year, run.Month, 1), null, cancellationToken) ?? 0m,
            GosiMinContributoryWageOccupationalHazards = await _ruleReader.GetDecimalAsync(
                "SAU", "KSA-mainland", Zayra.Api.Infrastructure.CountryPack.Ksa.RuleKeys.GosiMinContributoryWageOccupationalHazardsSar,
                new DateOnly(run.Year, run.Month, 1), null, cancellationToken) ?? 0m,
        };
        var results = PayrollValidationEngine.Run(ctx);
        // Same finding Process writes — /validate replaces the stored results wholesale, so it must re-add it.
        results.AddRange(await ReadinessPayValidationFindingsAsync(tenantId, id, employees, Zayra.Api.Infrastructure.Employees.BorderNumberRule.PeriodEnd(run.Year, run.Month), cancellationToken));

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
        PayrollRun? financeApproved = null;
        var result = await InRunRowLockTransactionAsync(async () =>
        {
            financeApproved = null;
            return await ApproveCoreAsync(id, tenantId, req, r => financeApproved = r, cancellationToken);
        }, cancellationToken);
        // Notifications are best-effort and only once the approval is durably committed.
        if (financeApproved is { } approved)
        {
            var runCurrency = await ResolveRunCurrencyAsync(tenantId, approved.CompanyId, cancellationToken);
            await _notifications.NotifyAsync(tenantId, GetUserId(), $"Payroll Run Approved — {approved.Year}/{approved.Month:D2}", $"Payroll run for {approved.Year}/{approved.Month:D2} has been approved by Finance. Total net: {approved.TotalNetSalary:N2} {runCurrency}.", "PayrollRun", id.ToString(), cancellationToken);
        }
        return result;
    }

    /// <summary>
    /// Runs one payroll_runs status transition in a single transaction whose body must read the run with
    /// <see cref="Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag"/> FIRST — the same row
    /// lock Lock, Void and Reopen take — so a transition decided on a stale status can never overwrite a
    /// status another request committed meanwhile (PayrollRun has no concurrency token; EF's UPDATE
    /// matches on the key alone). Refusals write nothing, so the transaction is always committed. EF
    /// InMemory has no transactions or row locks and runs the body directly, as does an ambient
    /// transaction opened by a caller (which then owns the commit).
    /// </summary>
    private async Task<IActionResult> InRunRowLockTransactionAsync(Func<Task<IActionResult>> body, CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return await body();
        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await body();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private async Task<IActionResult> ApproveCoreAsync(
        Guid id, Guid tenantId, PayrollDecisionRequest req, Action<PayrollRun> onFinanceApproved, CancellationToken cancellationToken)
    {
        var run = await _db.PayrollRuns.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status != "Processed" && run.Status != "PendingFinanceReview")
            return BadRequest(new { message = "Only a Processed or PendingFinanceReview run can be approved." });

        // AM-8 / AM-29: Approve takes the same per-company cost-accounting lock as Lock and every attribution writer, so the
        // fingerprint sealed below describes one consistent state.
        if (run.CompanyId is Guid approveCompany)
            await Infrastructure.Finance.CostAccountingGuards.AcquireCompanyLockAsync(_db, tenantId, approveCompany, cancellationToken);

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

        // ── Cash / cheque pay must be ACKNOWLEDGED the same way ───────────────────────────────────────
        // An employee paid outside the bank file is left out of the WPS file at batch creation. That is a
        // decision with Mudad consequences, so the approver states how many such employees they expect.
        var outsideBankEmployees = (await _db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.EmployeeId != null
                     && (x.Code == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideWarning || x.Code == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideWithIbanWarning))
            .Select(x => x.EmployeeId!.Value).Distinct().ToListAsync(cancellationToken)).OrderBy(x => x).ToList();
        var outsideBankCount = outsideBankEmployees.Count;
        if (outsideBankCount > 0 && req.ExpectedOutsideBankCount != outsideBankCount)
            return Conflict(new
            {
                error   = "outside_bank_payments_not_acknowledged",
                message = $"{outsideBankCount} employee(s) on this run are paid by cash or cheque and will not be in the bank/WPS file. " +
                          Infrastructure.Payroll.PaymentBatchExclusions.MudadNote + " Review the PAID_OUTSIDE_BANK_FILE warnings and " +
                          $"re-submit with expectedOutsideBankCount={outsideBankCount} to acknowledge.",
                outsideBankCount,
                acknowledged = req.ExpectedOutsideBankCount,
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

        // Maker-checker: neither the user who created the run nor the user who processed it may
        // approve it. Checking only ProcessedByUserId let a creator hand processing to a colleague
        // and then approve their own payroll proposal.
        var approverId = GetUserId();
        if (approverId.HasValue
            && (run.CreatedByUserId == approverId || run.ProcessedByUserId == approverId))
            return StatusCode(403, new { error = "maker_checker_violation", message = "The user who created or processed this run cannot approve it. A different approver is required (maker-checker policy)." });

        var isAdmin = User.IsInRole("Admin");
        var isHROrPayroll = User.IsInRole("HR Manager") || User.IsInRole("Payroll Manager");
        var isFinance = User.IsInRole("Finance Controller") || User.IsInRole("Finance Approver");

        // Admin and Finance finalise directly to Approved
        if (isAdmin || isFinance)
        {
            // AM-8: the approval hash is written ONLY on the transition to Approved (never at level 1), from the very journal Lock will
            // build. Costing off: no hash. A costed run whose approver has not acknowledged the uncosted lines is refused (S-048).
            string? sealedHash = null;
            if (run.CompanyId is Guid sealCompany
                && await _db.Companies.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == sealCompany && c.CostAccountingEnabled, cancellationToken))
            {
                var preview = await BuildLockJournalAsync(tenantId, run, GlAccrualPeriod(run),
                    await ResolveRunCurrencyAsync(tenantId, run.CompanyId, cancellationToken), null, string.Empty, cancellationToken);
                if (preview.CostPlan.UncostedEmployees > 0 && req.ExpectedUncostedCount != preview.CostPlan.UncostedEmployees)
                    return Conflict(new
                    {
                        error = "uncosted_not_acknowledged",
                        message = $"{preview.CostPlan.UncostedEmployees} employee(s) have lines on accounts where a cost centre is optional and will post WITHOUT one. " +
                                  $"Review the cost-centre assignments and re-submit with expectedUncostedCount={preview.CostPlan.UncostedEmployees} to acknowledge.",
                        uncostedEmployees = preview.CostPlan.UncostedEmployees, uncostedAmount = preview.CostPlan.UncostedAmount,
                        acknowledged = req.ExpectedUncostedCount,
                    });
                sealedHash = preview.CostHash;
            }
            run.CostCenterResolutionHash = sealedHash;
            _db.PayrollApprovals.Add(new PayrollApproval { TenantId = tenantId, PayrollRunId = id, ApprovalLevel = "FinanceReview", Decision = "Approved", Notes = req.Notes ?? string.Empty, DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow });
            run.Status = "Approved";
            // The acknowledged cash/cheque list is sealed here; Lock refuses to freeze any other list.
            await PayrollAudit("payroll.run.approved", "PayrollRun", id.ToString(),
                new { notes = req.Notes, level = "Finance", paidOutsideBankFile = outsideBankEmployees }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            onFinanceApproved(run);
            return Ok(run);
        }

        // HR Manager or Payroll Manager advances Processed → PendingFinanceReview
        if (isHROrPayroll && run.Status == "Processed")
        {
            _db.PayrollApprovals.Add(new PayrollApproval { TenantId = tenantId, PayrollRunId = id, ApprovalLevel = "PayrollReview", Decision = "Approved", Notes = req.Notes ?? string.Empty, DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow });
            run.Status = "PendingFinanceReview";
            run.CostCenterResolutionHash = null;   // AM-8: sealed only at final approval
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
        return await InRunRowLockTransactionAsync(async () =>
        {
            // FOR UPDATE: see InRunRowLockTransactionAsync.
            var run = await _db.PayrollRuns.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
            if (run is null) return NotFound();
            if (run.Status != "PendingFinanceReview")
                return BadRequest(new { message = "Only a PendingFinanceReview run can be sent back." });
            _db.PayrollApprovals.Add(new PayrollApproval { TenantId = tenantId, PayrollRunId = id, ApprovalLevel = "FinanceReview", Decision = "SentBack", Notes = req.Notes ?? string.Empty, DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow });
            run.Status = "Processed";
            run.CostCenterResolutionHash = null;
            await PayrollAudit("payroll.run.sent_back", "PayrollRun", id.ToString(), new { notes = req.Notes }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(run);
        }, cancellationToken);
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
        var strategy = _db.Database.CreateExecutionStrategy();
        object? payload = null;
        IActionResult? refused = null;
        await strategy.ExecuteAsync(async () =>
        {
            // The run read above is tracked with its pre-transaction values; a tracked entity is NOT
            // refreshed by a later query, so clear it before the locked re-read.
            _db.ChangeTracker.Clear();
            refused = null;
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            // FOR UPDATE — the same row lock Lock and Void take. The checks above ran outside any
            // transaction: a Lock committing in between (journal posted, status Locked) would otherwise be
            // wiped back to Draft with its accrual still live on the ledger. Under the lock, a concurrent
            // Lock has either committed (and is seen here) or has not started.
            run = await _db.PayrollRuns.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
                .FirstAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
            if (run.Status is not ("Draft" or "Processed" or "PendingFinanceReview" or "Approved"))
            {
                refused = run.Status == "Voided"
                    ? BadRequest(new { error = "run_voided", message = "A voided run cannot be reopened. Create a Replacement run for the period instead." })
                    : BadRequest(new
                    {
                        error   = "not_reopenable",
                        message = $"A '{run.Status}' run cannot be reopened. Void it and create a Replacement run for the period.",
                    });
                return;
            }
            var glRowsNow = await _db.FinanceGlEntries.CountAsync(
                x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id, cancellationToken);
            if (glRowsNow > 0)
            {
                refused = UnprocessableEntity(new
                {
                    error   = "run_has_gl",
                    message = $"This run has {glRowsNow} GL entr(ies) posted against it, so re-processing it would delete the " +
                              "earnings and deductions those journals are built from. Void it (POST runs/{id}/void) and create a " +
                              "Replacement run.",
                    glEntryCount = glRowsNow,
                });
                return;
            }

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
            run.CostCenterResolutionHash = null;
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
                attendanceDaysAlreadyAdjusted = opState.AttendanceDaysAlreadyAdjusted,
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

        if (refused is not null) return refused;
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
        // Employees paid by cash/cheque: the approver acknowledges this list, by count, at Approve.
        var outside = await _db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id && x.EmployeeId != null
                     && (x.Code == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideWarning || x.Code == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideWithIbanWarning))
            .OrderBy(x => x.EmployeeId)
            .Select(x => new { x.EmployeeId, x.Code, x.Message })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            runId          = id,
            paidOutsideBankFile = outside.GroupBy(x => x.EmployeeId).Select(g => g.First()).ToList(),
            mudadNote      = Infrastructure.Payroll.PaymentBatchExclusions.MudadNote,
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

        // KSA Art. 92/93 cap: the override rests on a written basis, which must be named, and the employee
        // whose wage is being deducted may never clear it for themselves.
        if (result.Code == Infrastructure.Payroll.WageDeductionClassification.DeductionsExceedHalfWageCode)
        {
            if (string.IsNullOrWhiteSpace(req.DocumentReference))
                return BadRequest(new
                {
                    error   = "document_reference_required",
                    message = "Give the reference of the decision or document this rests on (" +
                              Infrastructure.Payroll.WageDeductionClassification.CapOverrideGrounds + ").",
                });
            if (result.EmployeeId is int subjectId
                && await _db.Employees.AsNoTracking().AnyAsync(
                    e => e.TenantId == tenantId && e.Id == subjectId && e.UserAccountId == actorId, cancellationToken))
                return StatusCode(403, new
                {
                    error   = "subject_is_decider",
                    message = "You cannot override a deduction limit on your own wage. Another approver must decide it.",
                });
            reason = $"{reason} [ref: {req.DocumentReference.Trim()}]";
        }

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
        // An unidentified caller cannot prove they are not the preparer, so is refused outright.
        var actorId = GetUserId();
        if (actorId is null)
            return StatusCode(403, new
            {
                error   = "actor_unidentified",
                message = "This override cannot be recorded without an identified user, because separation of duties cannot be checked.",
            });
        if (run.ProcessedByUserId == actorId || run.CreatedByUserId == actorId)
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
        "GL_WILL_NOT_BALANCE" or "TOTALS_GROSS_MISMATCH" or "TOTALS_DEDUCTIONS_MISMATCH" or "TOTALS_NET_MISMATCH"
            or "PAYSLIP_LINES_MISMATCH" =>
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

        var entries = new List<(string Code, string Name, string Account, string AccountName, string EntryType, decimal Amount, string? CostCentreCode, string? CostCentreName)>();
        PayrollCostPlan previewCostPlan = PayrollCostPlan.Off;
        RoundingCostBlock? previewRoundingBlock = null;

        // Once a run is locked, its GL is posted to the immutable FinanceGlEntries ledger. The
        // "GL Journal" for such a run MUST reflect what was actually posted — NOT a live re-projection
        // through the *current* tenant mappings, which a Finance user may have edited in Setup after the
        // lock. Reading the posted entries keeps the accounting artifact truthful over a closed period.
        var posted = await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id)
            .ToListAsync(cancellationToken);

        var companyCurrency = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == run.CompanyId)
            .Select(c => c.DefaultCurrency)
            .FirstOrDefaultAsync(cancellationToken);
        companyCurrency = string.IsNullOrWhiteSpace(companyCurrency)
            ? string.Empty
            : companyCurrency.Trim().ToUpperInvariant();
        var postedCurrencies = posted
            .Select(e => (e.Currency ?? string.Empty).Trim().ToUpperInvariant())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (postedCurrencies.Count > 1
            || (postedCurrencies.Count == 1 && companyCurrency.Length > 0
                && !string.Equals(postedCurrencies[0], companyCurrency, StringComparison.OrdinalIgnoreCase)))
            return UnprocessableEntity(new
            {
                error = "gl_currency_mismatch",
                message = "The posted payroll journal is not denominated solely in the legal entity's company currency.",
                expectedCurrency = companyCurrency,
                currencies = postedCurrencies,
            });
        var journalCurrency = postedCurrencies.SingleOrDefault() ?? companyCurrency;

        var isPosted = posted.Count > 0;
        IReadOnlyList<PayrollGlCatchAll> previewCatchAlls = Array.Empty<PayrollGlCatchAll>();
        IReadOnlyList<GlResolutionProblem> previewProblems = Array.Empty<GlResolutionProblem>();
        decimal previewRounding = 0m;
        if (isPosted)
        {
            foreach (var e in posted)
            {
                var (compCode, compName) = DescribePostedLine(e.Description);
                // FinanceGlEntry deliberately supports both historical two-sided rows and newer
                // one-sided ledger rows. A two-sided row is TWO journal lines. Choosing the debit side
                // when it exists drops the paired credit and turns a balanced ledger into the exact
                // DR>0/CR=0 false imbalance observed in the client pilot.
                if (!string.IsNullOrWhiteSpace(e.DebitAccount))
                {
                    var (code, name) = SplitAccountLabel(e.DebitAccount);
                    entries.Add((compCode, compName, code, name, "DR", e.Amount, e.CostCenterCode, e.CostCenterName));
                }
                if (!string.IsNullOrWhiteSpace(e.CreditAccount))
                {
                    var (code, name) = SplitAccountLabel(e.CreditAccount);
                    // A two-sided row's cost centre belongs to its expense side only.
                    entries.Add((compCode, compName, code, name, "CR", e.Amount,
                        string.IsNullOrWhiteSpace(e.DebitAccount) ? e.CostCenterCode : null, string.IsNullOrWhiteSpace(e.DebitAccount) ? e.CostCenterName : null));
                }
            }
            // Deterministic display order: debits first, then credits, each by account code.
            entries = entries.OrderByDescending(x => x.EntryType).ThenBy(x => x.Account, StringComparer.Ordinal).ToList();
        }
        else
        {
            // Draft/unposted preview: the journal Lock WOULD post, built by the very same
            // BuildLockJournalAsync (bonus AND settlement clearing, routing, rounding) and projected line
            // for line. It used to be a parallel re-implementation that had drifted: it applied the bonus
            // clearing but not the settlement clearing, so a leaver's month previewed an expense the
            // posting never booked. One builder makes "what you see is what posts" true by construction.
            var plan = await BuildLockJournalAsync(
                tenantId, run, GlAccrualPeriod(run), journalCurrency, null, string.Empty, cancellationToken);
            var componentNames = plan.Earnings.Select(e => (e.ComponentCode, e.ComponentName))
                .Concat(plan.Deductions.Select(d => (d.ComponentCode, d.ComponentName)))
                .GroupBy(x => x.ComponentCode, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().ComponentName, StringComparer.Ordinal);
            foreach (var line in plan.Lines)
            {
                var (compCode, compName) = DescribePreviewLine(line, componentNames);
                if (!string.IsNullOrWhiteSpace(line.DebitAccount))
                {
                    var (code, name) = SplitAccountLabel(line.DebitAccount);
                    entries.Add((compCode, compName, code, name, "DR", line.Amount, line.CostCenterCode, line.CostCenterName));
                }
                if (!string.IsNullOrWhiteSpace(line.CreditAccount))
                {
                    var (code, name) = SplitAccountLabel(line.CreditAccount);
                    entries.Add((compCode, compName, code, name, "CR", line.Amount,
                        string.IsNullOrWhiteSpace(line.DebitAccount) ? line.CostCenterCode : null, string.IsNullOrWhiteSpace(line.DebitAccount) ? line.CostCenterName : null));
                }
            }
            previewCostPlan = plan.CostPlan;
            previewRoundingBlock = plan.RoundingBlock;
            previewCatchAlls = plan.CatchAlls;
            previewProblems = plan.Problems;
            previewRounding = plan.RoundingAdjustment;
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
            currency = journalCurrency,
            isPosted, // true = immutable as-posted ledger; false = live preview through current mappings
            entries  = entries.Select(e => new { componentCode = e.Code, componentName = e.Name, glAccount = e.Account, glAccountName = e.AccountName, entryType = e.EntryType, amount = e.Amount, costCentreCode = e.CostCentreCode, costCentreName = e.CostCentreName }),
            // Cost accounting (WP-C2, for the Lock-preview screen): what Lock would refuse and warn about, in the same shapes Lock answers with.
            costing = new
            {
                enabled = previewCostPlan.Enabled,
                blockers = previewCostPlan.Blockers.Select(b => new
                {
                    employeeId = b.EmployeeId, employeeCode = b.EmployeeCode, level = b.Level, departmentId = b.DepartmentId, positionId = b.PositionId,
                    parentDepartmentId = b.ParentDepartmentId, reason = b.Reason, accountCode = b.AccountCode,
                }),
                groups = previewCostPlan.Blockers.Count > 0 ? PayrollCostStamper.BuildRefusal(previewCostPlan.Blockers, previewCostPlan.SmallCellMin).Groups : Array.Empty<object>(),
                roundingBlocked = previewRoundingBlock is not null,
                fallbackEmployees = previewCostPlan.FallbackEmployees, fallbackAmount = previewCostPlan.FallbackAmount,
                uncostedEmployees = previewCostPlan.UncostedEmployees, uncostedAmount = previewCostPlan.UncostedAmount,
            },
            totalDebits, totalCredits,
            isBalanced = Math.Abs(totalDebits - totalCredits) < 0.01m,
            // Readiness, as Lock will judge it (preview only; empty once posted). Non-blocking warning:
            // pay items that will land in a catch-all account because no posting rule names them.
            catchAllComponents = previewCatchAlls.Select(c => new { componentCode = c.ComponentCode, label = c.Label, driverKey = c.DriverKey, amount = c.Amount }),
            // Blocking: a switched-off account or built-in posting rule this journal depends on.
            blockingProblems = previewProblems.Select(p => new { driverKey = p.DriverKey, label = p.Label, kind = p.Kind, accountCode = p.AccountCode, accountName = p.AccountName, message = p.Message }),
            roundingAdjustment = previewRounding,
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
            // An employer contribution (e.g. GOSI-OH-ER) is an employer cost, not money taken from the
            // employee: it is stored under its own type so no payslip ever counts it as a deduction.
            foreach (var d in deductions.Where(x => x.EmployeeId == slip.EmployeeId))
                _db.PayslipComponents.Add(new PayslipComponent
                {
                    TenantId = tenantId, PayslipId = payslip.Id,
                    ComponentType = d.IsEmployerContribution ? PayslipLineTypes.EmployerContribution : PayslipLineTypes.Deduction,
                    ComponentName = d.ComponentName, Amount = d.Amount,
                });
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
        // Same header-only projection as ListPayslips — carrying the employee name so the caller never has
        // to fall back to a placeholder code.
        var generated = await _db.Payslips.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PayrollRunId == id)
            .OrderBy(x => x.EmployeeId).ToListAsync(cancellationToken);
        var generatedNames = await ResolvePayslipEmployeeNamesAsync(tenantId, id, generated, cancellationToken);
        return Ok(generated
            .Select(x => PayslipListItemDto.Project(x, generatedNames[x.EmployeeId].Code, generatedNames[x.EmployeeId].Name))
            .ToList());
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
        // Cash/cheque employees and zero-net payslips are LEFT OUT of a bank batch rather than blocking
        // it: neither has anything to send to a bank, and before this one cash employee (no IBAN) or one
        // fully-deducted payslip refused the whole month's file. Each exclusion is named, with its reason
        // and amount, in the response and in this batch's sealed creation audit, and the batch total is
        // the run's net minus exactly those amounts.
        var bankBatch = !Infrastructure.Payroll.PaymentBatchExclusions.IsPaidOutsideBankFile(req.PaymentMethod);
        // The cash/cheque decision is the one FROZEN in the run's sealed Lock audit entry — the methods the
        // approver acknowledged — never the live profile, which may have been edited since Lock.
        var methodsAtLock = await Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideAtLockAsync(_db, tenantId, id, cancellationToken);
        var methodSource = "frozen-at-lock";
        if (methodsAtLock is null && bankBatch)
        {
            // A run locked before methods were frozen: fall back to the live profile, but only once the person
            // creating the batch has seen and acknowledged, by count, who will be left out of the bank file.
            var liveOutside = slips.Where(sl => sl.NetSalary > 0m)
                .Select(sl => (Slip: sl, Profile: profiles.FirstOrDefault(p => p.EmployeeId == sl.EmployeeId)))
                .Where(x => Infrastructure.Payroll.PaymentBatchExclusions.IsPaidOutsideBankFile(x.Profile?.PaymentMethod))
                .Select(x => new { x.Slip.EmployeeId, x.Slip.EmployeeCode, method = x.Profile!.PaymentMethod.Trim(), amount = x.Slip.NetSalary })
                .OrderBy(x => x.EmployeeId).ToList();
            if (liveOutside.Count > 0 && req.ExpectedOutsideBankCount != liveOutside.Count)
                return Conflict(new
                {
                    error   = "outside_bank_payments_not_acknowledged",
                    message = $"This run was locked before payment methods were frozen. {liveOutside.Count} employee(s) are set to cash or " +
                              "cheque on their profile today and will be left out of the bank file. " +
                              Infrastructure.Payroll.PaymentBatchExclusions.MudadNote +
                              $" Review the list and re-submit with expectedOutsideBankCount={liveOutside.Count}.",
                    outsideBankCount = liveOutside.Count,
                    employees = liveOutside,
                    acknowledged = req.ExpectedOutsideBankCount,
                });
            methodsAtLock = liveOutside.ToDictionary(x => x.EmployeeId, x => x.method);
            methodSource = "live-profile-acknowledged-at-batch";
        }
        var exclusions = new List<Infrastructure.Payroll.PaymentBatchExclusion>();
        var included = new List<(PayrollSlip Slip, EmployeePayrollProfile? Profile)>();
        foreach (var slip in slips)
        {
            var profile = profiles.FirstOrDefault(x => x.EmployeeId == slip.EmployeeId);
            var exclusion = bankBatch
                ? Infrastructure.Payroll.PaymentBatchExclusions.Decide(slip, methodsAtLock?.GetValueOrDefault(slip.EmployeeId))
                : null;
            if (exclusion is not null) exclusions.Add(exclusion);
            else included.Add((slip, profile));
        }
        var runNetTotal = slips.Sum(x => x.NetSalary);
        var excludedTotal = exclusions.Sum(x => x.Amount);
        var batch    = new PayrollPaymentBatch
        {
            TenantId      = tenantId,
            PayrollRunId  = id,
            BatchNumber   = run.RunType == PayrollRunTypes.Regular
                ? $"PAY-{run.Year}{run.Month:00}-{DateTime.UtcNow:HHmmss}"
                : $"PAY-{run.Year}{run.Month:00}-{runTypeTag}-{DateTime.UtcNow:HHmmss}-{id.ToString("N")[..4]}",
            PaymentMethod = req.PaymentMethod ?? "WPS",
            TotalAmount   = included.Sum(x => x.Slip.NetSalary),
            Currency      = currency,
            WpsStatus     = WpsStatuses.Draft,
        };
        _db.PayrollPaymentBatches.Add(batch);
        foreach (var (slip, profile) in included)
        {
            // The credit account is the IBAN, or — for an ANB-to-ANB credit — the 16-digit ANB account
            // number. Same rule as the bank export's "approved account" comparison.
            var account = string.IsNullOrWhiteSpace(profile?.Iban) ? (profile?.AccountNumber ?? string.Empty) : profile!.Iban;
            if (string.IsNullOrWhiteSpace(account))
                _db.PayrollValidationResults.Add(new PayrollValidationResult { TenantId = tenantId, PayrollRunId = id, EmployeeId = slip.EmployeeId, Severity = "Warning", Code = "MISSING_IBAN", Message = "Employee is missing IBAN for payment file." });
            _db.PayrollPaymentRecords.Add(new PayrollPaymentRecord { TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = slip.EmployeeId, Amount = slip.NetSalary, Iban = account, Status = "Pending", WpsReference = $"WPS-{slip.EmployeeCode}-{run.Year}{run.Month:00}" });
        }
        // POD-B2 (M5b) — restate the hold-out count at the moment money is queued for disbursement.
        var batchExcludedCount = await _db.PayrollRunEmployeeSelections.AsNoTracking()
            .CountAsync(s => s.TenantId == tenantId && s.PayrollRunId == id
                          && s.Outcome == PayrollRunSelectionOutcomes.Excluded, cancellationToken);
        await PayrollAudit(Infrastructure.Payroll.PaymentBatchExclusions.CreatedAuditAction, "PayrollPaymentBatch", batch.Id.ToString(),
            new
            {
                totalAmount = batch.TotalAmount, method = batch.PaymentMethod, runType = run.RunType, excludedCount = batchExcludedCount,
                runNetTotal, excludedTotal, paymentMethodSource = methodSource,
                acknowledgedOutsideBankCount = req.ExpectedOutsideBankCount,
                exclusions = Infrastructure.Payroll.PaymentBatchExclusions.ToAuditData(exclusions),
            }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        // `Status` is NOT optional here. ListPaymentBatches returns it, the client's
        // PayrollPaymentBatch type declares it non-nullable, and BankWpsTab renders the freshly
        // created batch straight from THIS body — `<StatusBadge status={b.status} />` calls
        // status.replace(...), so omitting the field threw a TypeError inside render and the Next
        // error boundary replaced the whole payroll module with "Something went wrong" the moment
        // an operator created a payment batch. Create and list must project the same shape.
        return Created($"/api/payroll/payment-batches/{batch.Id}", new
        {
            batch.Id, batch.TenantId, batch.PayrollRunId, batch.BatchNumber, batch.PaymentMethod,
            batch.TotalAmount, batch.Currency, batch.Status, batch.WpsStatus,
            runType       = run.RunType,
            parentRunId   = run.ParentRunId,
            excludedCount = batchExcludedCount,
            // Reconciliation, stated: totalAmount = runNetTotal − excludedTotal.
            runNetTotal,
            excludedTotal,
            paymentExclusions = exclusions,
            paymentMethodsFrozenAtLock = methodSource == "frozen-at-lock",
            paymentMethodSource = methodSource,
            mudadNote = exclusions.Any(x => x.ReasonCode == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideBankFileCode)
                ? Infrastructure.Payroll.PaymentBatchExclusions.MudadNote : null,
        });
    }

    /// <summary>The employee ID the Saudi wage file carries: the government ID, else (inside the border-number window,
    /// company setting ON) the Border Number. Empty when neither applies — the validator has already refused it.</summary>
    private static string KsaWageFileEmployeeId(
        Employee? emp, IReadOnlyDictionary<int, Zayra.Api.Infrastructure.Employees.BorderNumberRule.WageFileBorderId> borderIds)
    {
        if (emp is null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(emp.IdNumber)) return emp.IdNumber;
        return borderIds.TryGetValue(emp.Id, out var b) && b.Allowed ? b.BorderNumber : string.Empty;
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

        var wpsPeriodEnd = Zayra.Api.Infrastructure.Employees.BorderNumberRule.PeriodEnd(run.Year, run.Month);
        var (hardBlocked, driftWarn) = await ComputePayReadinessAsync(tenantId, employees, cancellationToken, wpsPeriodEnd);
        var borderIds = await Zayra.Api.Infrastructure.Employees.BorderNumberRule.ResolveWageFileIdsAsync(
            _db, tenantId, employees, wpsPeriodEnd, cancellationToken, _ruleReader);
        var result = Infrastructure.Payroll.WpsSifValidator.Validate(run, slips, profiles, employees, hardBlocked, borderIds);

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
            // What "valid" means here, precisely. This validator checks the DATA in the file and
            // cannot check that the FORMAT is one a Saudi WPS gateway will parse — and no file from
            // this generator has ever been put in front of one. An operator reading "0 errors" must
            // not conclude the submission is safe, so the scope of the guarantee travels with it.
            conformance = Infrastructure.Payroll.WpsConformance.For(null),
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
        // D3: company authorization BEFORE any read, body parse or mutation of this batch. The
        // entity comes from batch -> run -> PayrollRun.CompanyId, never from the request. Without
        // it these endpoints were tenant-only, and the run-status guards below fail OPEN
        // cross-company (PayrollRun is company-filtered, so the run reads back null).
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, id, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
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
        // Payslips left out of this bank batch when it was created (cash/cheque, zero net) are not in the
        // wage file either: they are validated out, and the run total is reconciled net of them.
        var wpsExclusions = await Infrastructure.Payroll.PaymentBatchExclusions.LoadAsync(_db, tenantId, id, cancellationToken);
        var wpsExcludedIds = wpsExclusions.Select(x => x.EmployeeId).ToHashSet();
        var fileSlips = slips.Where(x => !wpsExcludedIds.Contains(x.EmployeeId)).ToList();
        var runForFile = wpsExclusions.Count == 0 ? run : new PayrollRun
        {
            Id = run.Id, TenantId = run.TenantId, CompanyId = run.CompanyId, Year = run.Year, Month = run.Month,
            Status = run.Status, TotalNetSalary = run.TotalNetSalary - wpsExclusions.Sum(x => x.Amount),
        };
        var wpsPeriodEnd = Zayra.Api.Infrastructure.Employees.BorderNumberRule.PeriodEnd(run.Year, run.Month);
        var (hardBlocked, driftWarn) = await ComputePayReadinessAsync(tenantId, employees, cancellationToken, wpsPeriodEnd);
        var borderIds = await Zayra.Api.Infrastructure.Employees.BorderNumberRule.ResolveWageFileIdsAsync(
            _db, tenantId, employees, wpsPeriodEnd, cancellationToken, _ruleReader);
        var validation = Infrastructure.Payroll.WpsSifValidator.Validate(runForFile, fileSlips, profiles, employees, hardBlocked, borderIds);
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

        // The establishment / WPS employer id is REQUIRED. It used to fall back to "0000000000", which
        // printed a placeholder as if it were an establishment into every file of a tenant that had not
        // configured one. Now a missing or placeholder id refuses the export with a stable code.
        var isKsaRegister = exporter is Infrastructure.CountryPack.Ksa.KsaWageProtectionExporter;
        var (agentId, agentConflict) = await ResolveWpsEstablishmentIdAsync(tenantId, run.CompanyId, isKsaRegister, cancellationToken);
        if ((agentConflict ?? WpsEstablishmentError(agentId, isKsaRegister)) is { } estError)
            return UnprocessableEntity(new { error = estError.Code, message = estError.Message, field = estError.Field });
        var currency    = !string.IsNullOrWhiteSpace(batch.Currency) && batch.Currency != "USD"
                            ? batch.Currency
                            : await ResolveCurrencyAsync(tenantId, cancellationToken);
        if (isKsaRegister && !string.Equals(currency, "SAR", StringComparison.Ordinal))
            return UnprocessableEntity(new
            {
                error = Infrastructure.Payroll.SaudiBankExports.KsaWageFileRules.Codes.CurrencyNotSar,
                message = $"Saudi payroll files are in SAR only. This batch is in {currency}.",
                field = "currency",
            });

        // Build WpsEmployee list from payment records + employee snapshot data.
        var profileByEmpId = profiles.ToDictionary(p => p.EmployeeId);
        var slipByEmpId    = slips.ToDictionary(s => s.EmployeeId);
        var empById        = employees.ToDictionary(e => e.Id);

        // Ordered by employee: the file's bytes (and so its SHA-256) must not depend on database row order.
        var wpsEmployees = records.OrderBy(r => r.EmployeeId).Select(record =>
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
                // KSA: the employee's own government ID (MOL-ID), never the profile's free-text MolId.
                NationalId:     isKsaRegister ? KsaWageFileEmployeeId(emp, borderIds) : (ppf?.MolId ?? string.Empty),
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
            EstablishmentId: agentId!,
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
        // The exact bytes are kept, so a later download serves THIS file rather than re-creating it.
        Infrastructure.Payroll.GeneratedWpsFileStore.Stage(_db, tenantId, id, wps.Id,
            exportResult.FileName, exportResult.Format, exportResult.FileBytes);

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
                MolId          = isKsaRegister
                                    ? KsaWageFileEmployeeId(employees.FirstOrDefault(e => e.Id == record.EmployeeId), borderIds)
                                    : (ppf?.MolId ?? string.Empty),
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
            formatLabel = Infrastructure.Payroll.WpsConformance.LabelFor(genResult.FormatVersion),
            // "File generated" is not "wage filed". The generated artefact's layout has never been
            // accepted by a live Mudad/bank gateway, and the screen that celebrates a successful
            // generation is exactly where that has to be said.
            conformance = Infrastructure.Payroll.WpsConformance.For(genResult.FormatVersion),
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

        var tenantId = GetTenantId();
        // D3: company authorization BEFORE any read, body parse or mutation of this batch. The
        // entity comes from batch -> run -> PayrollRun.CompanyId, never from the request. Without
        // it these endpoints were tenant-only, and the run-status guards below fail OPEN
        // cross-company (PayrollRun is company-filtered, so the run reads back null).
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;

        // Validated AFTER the entity check: a cross-company caller should learn that they may not touch
        // this batch, not that their status value was spelled wrong.
        if (!WpsStatuses.All.Contains(req.Status))
            return BadRequest(new { error = "invalid_status", message = $"Status must be one of: {string.Join(", ", WpsStatuses.All)}." });
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

        // A frozen ANB Connect bank instruction is the KSA default bank file; it counts as the generated
        // file for this lifecycle even though the bank-export service deliberately never touches WpsStatus.
        var instructionGenerators = await new Infrastructure.Payroll.SaudiBankExports.SaudiBankExportService(_db)
            .InstructionGeneratorsAsync(tenantId, new[] { batchId }, cancellationToken);
        var hasBankInstruction = instructionGenerators.ContainsKey(batchId);
        if (latestFile is null && !hasBankInstruction)
            return BadRequest(new { error = "wps_file_missing", message = "Generate the bank payroll file (or the payroll register) for this batch before changing its WPS status." });
        from = Infrastructure.Payroll.WpsLifecycleView.Effective(from, hasBankInstruction);

        if (req.Status is WpsStatuses.Submitted && string.IsNullOrWhiteSpace(req.Reference))
            return BadRequest(new { error = "submission_reference_required", message = "A statutory submission reference is required when marking WPS as Submitted." });

        if (req.Status is WpsStatuses.Accepted && string.IsNullOrWhiteSpace(req.Reference))
            return BadRequest(new { error = "acknowledgement_reference_required", message = "An acknowledgement reference is required when marking WPS as Accepted." });

        // ── Accepted needs EVIDENCE, not a dropdown ───────────────────────────────────────────────────
        // Mudad accepts only the bank-signed WPS file; the product never sees Mudad's verdict. So
        // "Accepted" is recorded only against stored proof: the bank's output file or a Mudad compliance
        // screenshot/PDF, re-hashed here so the proof cannot have changed since it was uploaded.
        Infrastructure.Payroll.WpsEvidenceEnvelope? acceptanceEvidence = null;
        if (req.Status is WpsStatuses.Accepted)
        {
            if (req.EvidenceId is not Guid evidenceId)
                return BadRequest(new
                {
                    error = "acceptance_evidence_required",
                    message = "Upload the bank's WPS output file or a Mudad compliance screenshot/PDF for this batch, then mark it Accepted. The status alone is not proof.",
                });
            var evidence = EvidenceService();
            acceptanceEvidence = await evidence.FindAsync(tenantId, batchId, evidenceId, cancellationToken);
            if (acceptanceEvidence is null)
                return BadRequest(new { error = "acceptance_evidence_not_found", message = "That evidence was not found for this payment batch." });
            if (await evidence.ReadVerifiedAsync(tenantId, acceptanceEvidence, cancellationToken) is null)
                return Conflict(new { error = "acceptance_evidence_integrity_failed", message = "The stored evidence file is missing or no longer matches its recorded SHA-256. Upload it again." });

            // Maker-checker (the tenant's strict separation of duties): whoever generated the bank/WPS
            // file or uploaded this evidence may not be the one who records it as accepted. An unknown
            // caller cannot prove separation, so is refused too.
            var deciderId = GetUserId();
            var makers = new HashSet<Guid>();
            if (acceptanceEvidence.UploadedBy is Guid uploader) makers.Add(uploader);
            if (latestFile?.GeneratedByUserId is Guid fileMaker) makers.Add(fileMaker);
            if (instructionGenerators.GetValueOrDefault(batchId) is Guid instructionMaker) makers.Add(instructionMaker);
            var makerChecker = new Application.Approvals.MakerCheckerRule(
                deciderId is not Guid d || makers.Contains(d),
                new[] { WpsStatuses.Accepted },
                Infrastructure.Payroll.WpsLifecycleView.MakerCheckerMessage);
            if (makerChecker.IsViolated(req.Status))
                // 403, like the run-override maker-checker refusal: the request is valid, this caller may not make it.
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "acceptance_needs_second_person", message = makerChecker.Message });
        }

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
                  && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed
                  && x.SourceEntityRef == batch.BatchNumber, cancellationToken);
            // Every wage left out of the bank file for cash/cheque must have its payment recorded too, or the
            // month would close with Salaries Payable still owed to those employees.
            var unpaidOutside = new List<object>();
            var recordedOutside = await OutsidePaymentRefsAsync(tenantId, new[] { batch.PayrollRunId }, cancellationToken);
            foreach (var x in await Infrastructure.Payroll.PaymentBatchExclusions.LoadAsync(_db, tenantId, batch.Id, cancellationToken))
                if (x.ReasonCode == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideBankFileCode && x.Amount > 0m
                    && !recordedOutside.Contains(Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentRef(batch.BatchNumber, x.EmployeeId)))
                    unpaidOutside.Add(new { x.EmployeeId, x.EmployeeCode, x.Amount });
            if (unpaidOutside.Count > 0)
                return UnprocessableEntity(new
                {
                    error   = "outside_payments_unrecorded",
                    message = $"{unpaidOutside.Count} cash/cheque wage(s) left out of the bank file have no recorded payment. " +
                              "Record each payment outside the bank file before reconciling the batch.",
                    unpaid  = unpaidOutside,
                });
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
        if (latestFile is not null) latestFile.FilingStatus = req.Status;
        if (req.Status is WpsStatuses.Submitted)
        {
            batch.WpsSubmissionReference = req.Reference;
            if (latestFile is not null)
            {
                latestFile.SubmissionReference = req.Reference;
                latestFile.SubmittedAtUtc = DateTime.UtcNow;
            }
        }
        else if (req.Status is WpsStatuses.Accepted)
        {
            batch.WpsSubmissionReference = req.Reference;
            if (latestFile is not null)
            {
                latestFile.SubmissionReference = req.Reference;
                latestFile.AcknowledgedAtUtc = DateTime.UtcNow;
            }
        }
        else if (req.Status is WpsStatuses.Rejected)
        {
            batch.WpsRejectionReason = req.Notes;
            if (latestFile is not null)
            {
                latestFile.RejectionReason = req.Notes;
                latestFile.RejectedAtUtc = DateTime.UtcNow;
            }
        }
        await PayrollAudit("payroll.wps.status_changed", "PayrollPaymentBatch", batchId.ToString(),
            new
            {
                from, to = req.Status, reference = req.Reference, notes = req.Notes,
                evidenceId = acceptanceEvidence?.EvidenceId, evidenceKind = acceptanceEvidence?.Kind,
                evidenceSha256 = acceptanceEvidence?.Sha256,
                statusLabel = acceptanceEvidence is null ? null : Infrastructure.Payroll.WpsLifecycleView.AcceptedWithEvidenceLabel,
            }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            batchId, wpsStatus = batch.WpsStatus,
            wpsStatusLabel = Infrastructure.Payroll.WpsLifecycleView.Label(batch.WpsStatus, acceptanceEvidence is null ? 0 : 1),
        });
    }

    private Infrastructure.Payroll.WpsAcceptanceEvidenceService EvidenceService() => new(_db, _storage);

    /// <summary>
    /// Stores proof that Mudad/WPS accepted this batch: the bank's signed WPS output file (kept byte-for-
    /// byte, never parsed) or a Mudad compliance screenshot/PDF. SHA-256 is computed server-side over the
    /// bytes received. Requires payroll.export. The id returned is what marking the batch Accepted needs.
    /// </summary>
    [HttpPost("payment-batches/{batchId:guid}/wps-evidence")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(Infrastructure.Payroll.WpsAcceptanceEvidenceService.MaxBytes + Infrastructure.Documents.EssUploadPolicy.MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = Infrastructure.Payroll.WpsAcceptanceEvidenceService.MaxBytes + Infrastructure.Documents.EssUploadPolicy.MultipartOverheadBytes)]
    public async Task<IActionResult> UploadWpsEvidence(Guid batchId, [FromForm] WpsEvidenceUploadForm form, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        var tenantId = GetTenantId();
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
        var batch = await _db.PayrollPaymentBatches.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        if (batch.WpsStatus == WpsStatuses.Voided)
            return UnprocessableEntity(new { error = "run_voided", message = "This payment batch belongs to a voided run; evidence cannot be added." });
        if (form.File is null) return BadRequest(new { error = "evidence_file_required", message = "Choose the file to upload." });
        if (form.File.Length > Infrastructure.Payroll.WpsAcceptanceEvidenceService.MaxBytes)
            return BadRequest(new { error = "evidence_file_too_large", message = "The file exceeds the 10 MB limit." });

        byte[] bytes;
        await using (var input = form.File.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await input.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var companyId = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == batch.PayrollRunId).Select(r => r.CompanyId).FirstOrDefaultAsync(cancellationToken);

        // Store + insert + audit commit together (and the stored object is removed if they do not).
        Infrastructure.Payroll.WpsEvidenceUploadResult result;
        try
        {
            result = await EvidenceService().RecordAsync(tenantId, companyId, batchId, GetUserId(), form.Kind?.Trim(),
                form.File.FileName, form.File.ContentType, bytes, form.Note,
                e => PayrollAudit("payroll.wps.evidence_uploaded", "PayrollPaymentBatch", batchId.ToString(),
                    new { evidenceId = e.EvidenceId, kind = e.Kind, sha256 = e.Sha256, sizeBytes = e.SizeBytes, contentType = e.ContentType },
                    cancellationToken),
                cancellationToken);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = "evidence_storage_refused", message = ex.Message }); }
        if (result.Evidence is null)
            return result.Conflict
                ? Conflict(new { error = result.Error, message = result.Message })
                : BadRequest(new { error = result.Error, message = result.Message });

        return Ok(EvidenceDto(result.Evidence));
    }

    /// <summary>Acceptance evidence recorded for a batch (metadata only).</summary>
    [HttpGet("payment-batches/{batchId:guid}/wps-evidence")]
    public async Task<IActionResult> ListWpsEvidence(Guid batchId, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        var tenantId = GetTenantId();
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
        var items = await EvidenceService().ListAsync(tenantId, batchId, cancellationToken);
        return Ok(items.Select(EvidenceDto).ToList());
    }

    /// <summary>Downloads stored evidence after re-verifying its SHA-256. Always an attachment.</summary>
    [HttpGet("payment-batches/{batchId:guid}/wps-evidence/{evidenceId:guid}/download")]
    public async Task<IActionResult> DownloadWpsEvidence(Guid batchId, Guid evidenceId, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        var tenantId = GetTenantId();
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
        var service = EvidenceService();
        var env = await service.FindAsync(tenantId, batchId, evidenceId, cancellationToken);
        if (env is null) return NotFound();
        var bytes = await service.ReadVerifiedAsync(tenantId, env, cancellationToken);
        if (bytes is null)
            return Conflict(new { error = "acceptance_evidence_integrity_failed", message = "The stored evidence file is missing or no longer matches its recorded SHA-256." });
        await PayrollAudit("payroll.wps.evidence_downloaded", "PayrollPaymentBatch", batchId.ToString(),
            new { evidenceId, sha256 = env.Sha256 }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/octet-stream", env.FileName);
    }

    private static object EvidenceDto(Infrastructure.Payroll.WpsEvidenceEnvelope e) => new
    {
        e.EvidenceId, e.BatchId, e.Kind, kindLabel = Infrastructure.Payroll.WpsEvidenceKinds.Describe(e.Kind),
        e.Sha256, e.SizeBytes, e.ContentType, e.FileName, e.Note, e.UploadedBy, e.UploadedAtUtc,
    };

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
        // The ledger's own state machine (migration M2) refuses to walk a line back: a Posted line stays Posted, and a line that has
        // been exported or rejected cannot return to ReadyForErp. Those lines are skipped, not failed, and the answer says how many.
        static bool LineMayMove(FinanceGlEntry line, string target) => line.ErpPostingStatus switch
        {
            ErpPostingStatuses.Posted => false,
            ErpPostingStatuses.Exported or ErpPostingStatuses.Rejected => target != ErpPostingStatuses.ReadyForErp,
            _ => true,
        };
        var skippedLines = glEntries.Count(e => !LineMayMove(e, req.Status));
        foreach (var entry in glEntries.Where(e => LineMayMove(e, req.Status)))
        {
            entry.ErpPostingStatus = req.Status;
            entry.ErpStatusChangedAtUtc = DateTime.UtcNow;
            entry.ErpDocumentNumber = req.Reference;
            entry.ErpRejectionReason = req.Status == ErpPostingStatuses.Rejected ? req.Notes : null;
        }

        await PayrollAudit("payroll.erp_posting.status_changed", "PayrollRun", id.ToString(),
            new { from, to = req.Status, reference = req.Reference, notes = req.Notes, skippedLines }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { runId = id, erpPostingStatus = run.ErpPostingStatus, reference = run.ErpPostingReference, skippedLines });
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
        // D3: company authorization BEFORE any read, body parse or mutation of this batch. The
        // entity comes from batch -> run -> PayrollRun.CompanyId, never from the request. Without
        // it these endpoints were tenant-only, and the run-status guards below fail OPEN
        // cross-company (PayrollRun is company-filtered, so the run reads back null).
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
        // The already-settled check and the journal it guards run as ONE serialized unit per batch, so two
        // concurrent settles cannot both pass the check and post two journals.
        return await Infrastructure.Finance.FinanceDecisionSerializer.SerializeAsync(_db, BatchPaymentLockScope, tenantId, batchId,
            () => SettlePaymentBatchCoreAsync(tenantId, batchId, req, cancellationToken), cancellationToken);
    }

    /// <summary>Advisory-lock scope shared by every net-pay posting against one payment batch: the bank
    /// settlement, and each payment recorded (or reversed) outside the bank file. Coarser than (batch,
    /// employee) on purpose: those postings also share the leaver-settlement and receivable reclass state.</summary>
    private const string BatchPaymentLockScope = "payroll.batch-payment";

    private async Task<IActionResult> SettlePaymentBatchCoreAsync(Guid tenantId, Guid batchId, SettlePaymentBatchRequest req, CancellationToken cancellationToken)
    {
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
              && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed
              && x.SourceEntityRef == batch.BatchNumber, cancellationToken);
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

        // The bank batch settles ONLY what it carries. Employees left out of it (cash/cheque, zero net)
        // stay open in 2100 until their payment outside the bank file is recorded against this batch —
        // crediting Cash/Bank for them here would book a bank outflow that never happened.
        var batchExclusions = await Infrastructure.Payroll.PaymentBatchExclusions.LoadAsync(_db, tenantId, batch.Id, cancellationToken);
        var excludedOpen = batchExclusions.Sum(x => x.Amount);
        var batchEmployeeIds = (await _db.PayrollPaymentRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batch.Id && x.Status != PaymentRecordStatuses.Cancelled)
            .Select(x => x.EmployeeId).ToListAsync(cancellationToken)).ToHashSet();
        var toSettle = netLines.Sum(l => l.Amount) - excludedOpen;
        if (toSettle <= 0m)
            return BadRequest(new { error = "nothing_to_settle", message = "Every payslip in this run is paid outside the bank file; record those payments instead of settling the batch." });
        var clearing = TakeFromAccrual(netLines, toSettle);

        var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken);
        var (cashCode, cashName) = ResolveGlAccount("CASH_BANK", glCtx.Overrides, glCtx.DriverDefaults);
        var cashAccount = $"{cashCode} - {cashName}";
        var currency = netLines[0].Currency;

        var (lines, dr, cr) = BuildLiabilityClearingGl(
            tenantId, run.Id, settlementPeriod, batch.BatchNumber, GlEventTypes.NetSettlement,
            clearing, cashAccount, GetUserId(), GetUserName(), currency,
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
            tenantId, run, batch, glCtx, settlementPeriod, batchEmployeeIds, cancellationToken);

        await PayrollAudit("payroll.batch.settled", "PayrollPaymentBatch", batch.Id.ToString(),
            new
            {
                runId = run.Id, batch.BatchNumber, amount = cr, cashAccount, period = settlementPeriod,
                reference = req.Reference,
                leftOpenForOutsidePayment = excludedOpen,
                settlementsPaid = settlementResult.SettlementIds,
                residualDebtReclassed = settlementResult.ResidualReclassed,
                residualDebtUnbooked  = settlementResult.ResidualUnbooked,
            }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            batchId = batch.Id, runId = run.Id, wpsStatus = batch.WpsStatus, settled = cr, cashAccount,
            period = settlementPeriod,
            // Still open in Salaries Payable until each outside-the-bank-file payment is recorded.
            leftOpenForOutsidePayment = excludedOpen,
            settlementsPaid = settlementResult.SettlementIds,
            residualDebtReclassed = settlementResult.ResidualReclassed,
            residualDebtUnbooked  = settlementResult.ResidualUnbooked,
        });
    }

    /// <summary>Copies of the accrual credit lines totalling exactly <paramref name="amount"/>, taken in
    /// order. With the full accrual total it reproduces the lines unchanged, so a batch with nothing left
    /// out posts the same journal as before.</summary>
    private static List<FinanceGlEntry> TakeFromAccrual(IReadOnlyList<FinanceGlEntry> accrualLines, decimal amount)
    {
        var taken = new List<FinanceGlEntry>();
        var left = amount;
        foreach (var l in accrualLines)
        {
            if (left <= 0m) break;
            var take = Math.Min(l.Amount, left);
            taken.Add(new FinanceGlEntry
            {
                TenantId = l.TenantId, CompanyId = l.CompanyId, CreditAccount = l.CreditAccount, Amount = take,
                Currency = l.Currency, Description = l.Description,
            });
            left -= take;
        }
        return taken;
    }

    /// <summary>
    /// Records the payment of a wage left OUT of the bank file (cash or cheque) — the step that clears its
    /// part of 2100 Salaries Payable, which settling the bank batch deliberately leaves open. Posts DR 2100 /
    /// CR Cash-Bank (or the configured PETTY_CASH account for cash, when one is mapped) for exactly the
    /// excluded amount recorded on the batch, once per employee. Requires payroll.export; the employee who
    /// was paid may not record their own payment. Audited on the payroll chain.
    /// </summary>
    [HttpPost("payment-batches/{batchId:guid}/outside-payments")]
    public async Task<IActionResult> RecordOutsidePayment(Guid batchId, [FromBody] OutsidePaymentRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        var tenantId = GetTenantId();
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;

        var method = req.Method?.Trim();
        if (method is null || !Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentMethods.Contains(method))
            return BadRequest(new { error = "method_invalid", message = "Payment method must be Cash or Cheque." });
        method = Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentMethods.First(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase));
        var reference = req.Reference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > 100 || reference.Any(char.IsControl))
            return BadRequest(new { error = "reference_required", message = "Enter the cheque number or cash receipt reference (up to 100 characters)." });

        // Check-and-insert as ONE serialized unit (same lock as the batch settlement): two simultaneous
        // posts for the same employee produce exactly one journal.
        return await Infrastructure.Finance.FinanceDecisionSerializer.SerializeAsync(_db, BatchPaymentLockScope, tenantId, batchId,
            () => RecordOutsidePaymentCoreAsync(tenantId, batchId, req, method, reference, cancellationToken), cancellationToken);
    }

    private async Task<IActionResult> RecordOutsidePaymentCoreAsync(Guid tenantId, Guid batchId, OutsidePaymentRequest req,
        string method, string reference, CancellationToken cancellationToken)
    {
        var batch = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batch.PayrollRunId, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status != "Locked" || batch.WpsStatus == WpsStatuses.Voided)
            return BadRequest(new { error = "run_not_locked", message = "Payments can only be recorded against a Locked, non-voided run." });

        var exclusion = (await Infrastructure.Payroll.PaymentBatchExclusions.LoadAsync(_db, tenantId, batch.Id, cancellationToken))
            .FirstOrDefault(x => x.EmployeeId == req.EmployeeId
                && x.ReasonCode == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideBankFileCode);
        if (exclusion is null || exclusion.Amount <= 0m)
            return BadRequest(new { error = "not_paid_outside_bank_file", message = "This employee is not listed on this batch as paid by cash or cheque." });

        var actorId = GetUserId();
        if (actorId is null)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "actor_unidentified", message = "A payment can only be recorded by an identified user." });
        if (await _db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == req.EmployeeId && e.UserAccountId == actorId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "subject_is_recorder", message = "You cannot record the payment of your own wage. Another payroll user must record it." });

        var sourceRef = Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentRef(batch.BatchNumber, exclusion.EmployeeId);
        if (await _db.FinanceGlEntries.AnyAsync(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == run.Id
                && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed && x.SourceEntityRef == sourceRef, cancellationToken))
            return Conflict(new { error = "already_recorded", message = "This employee's payment outside the bank file is already recorded." });

        var netLines = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == run.Id
                     && x.EventType == GlEventTypes.Accrual && !x.IsReversed
                     && string.IsNullOrEmpty(x.DebitAccount) && x.Description == PayrollGlDescriptions.NetPayable)
            .ToListAsync(cancellationToken);
        if (netLines.Count == 0)
            return BadRequest(new { error = "gl_not_accrued", message = "This run has no net-pay accrual. Lock the run first." });

        var paidDate = req.PaidDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var period = $"{paidDate.Year}-{paidDate.Month:D2}";
        if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run.CompanyId, period, cancellationToken))
            return UnprocessableEntity(new { error = "gl_period_closed", message = $"GL period {period} is closed. Reopen it before recording payments into it.", period });

        var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken);
        const string pettyCash = "PETTY_CASH";
        var driver = method == "Cash" && (glCtx.Overrides.ContainsKey(pettyCash) || glCtx.DriverDefaults.ContainsKey(pettyCash))
            ? pettyCash : "CASH_BANK";
        var (code, name) = ResolveGlAccount(driver, glCtx.Overrides, glCtx.DriverDefaults);
        var account = $"{code} - {name}";

        var (lines, dr, cr) = BuildLiabilityClearingGl(
            tenantId, run.Id, period, sourceRef, GlEventTypes.NetSettlement,
            TakeFromAccrual(netLines, exclusion.Amount), account, actorId, GetUserName(), netLines[0].Currency,
            $"Net pay paid outside the bank file ({method}, ref {reference}) — {exclusion.EmployeeCode}, batch {batch.BatchNumber}",
            run.CompanyId);
        if (Math.Abs(dr - cr) > 0.01m || Math.Abs(cr - exclusion.Amount) > 0.01m)
            return UnprocessableEntity(new { error = "gl_unbalanced", message = "The payment journal does not balance against the excluded amount.", totalDebits = dr, totalCredits = cr });
        _db.FinanceGlEntries.AddRange(lines);

        var settlementResult = await FinalizePaidSettlementsAsync(
            tenantId, run, batch, glCtx, period, new HashSet<int> { exclusion.EmployeeId }, cancellationToken);

        await PayrollAudit("payroll.outside_payment.recorded", "PayrollPaymentBatch", batch.Id.ToString(), new
        {
            runId = run.Id, employeeId = exclusion.EmployeeId, exclusion.EmployeeCode, amount = exclusion.Amount,
            method, reference, paidDate, account, period, settlementsPaid = settlementResult.SettlementIds,
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            batchId = batch.Id, employeeId = exclusion.EmployeeId, amount = exclusion.Amount, method, reference,
            paidDate, account, period, settlementsPaid = settlementResult.SettlementIds,
        });
    }

    /// <summary>
    /// Reverses a payment recorded outside the bank file (e.g. a cheque that bounced or was cancelled), the
    /// same way a bank settlement is reversed: contra lines dated to the original period, refused if that
    /// period is closed, the originals flagged reversed. 2100 re-opens for the employee, a final settlement the
    /// payment marked Paid goes back to Disbursing, and the payment can then be recorded again. Requires
    /// payroll.export and a reason; refused once the batch is Reconciled,
    /// and never by the employee who was paid. Audited.
    /// </summary>
    [HttpPost("payment-batches/{batchId:guid}/outside-payments/{employeeId:int}/reverse")]
    public async Task<IActionResult> ReverseOutsidePayment(Guid batchId, int employeeId, [FromBody] PayrollReasonRequest req, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();
        var tenantId = GetTenantId();
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required to reverse a recorded payment." });

        return await Infrastructure.Finance.FinanceDecisionSerializer.SerializeAsync(_db, BatchPaymentLockScope, tenantId, batchId, async () =>
        {
            var batch = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
            if (batch is null) return (IActionResult)NotFound();
            if (batch.WpsStatus is WpsStatuses.Reconciled)
                return UnprocessableEntity(new { error = "batch_reconciled", message = "This batch is Reconciled; its payments can no longer be reversed." });

            var actorId = GetUserId();
            if (actorId is null)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "actor_unidentified", message = "A payment can only be reversed by an identified user." });
            if (await _db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == employeeId && e.UserAccountId == actorId, cancellationToken))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "subject_is_recorder", message = "You cannot reverse the payment of your own wage. Another payroll user must do it." });

            var sourceRef = Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentRef(batch.BatchNumber, employeeId);
            var originals = await _db.FinanceGlEntries
                .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == batch.PayrollRunId
                         && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed && x.SourceEntityRef == sourceRef)
                .ToListAsync(cancellationToken);
            if (originals.Count == 0)
                return Conflict(new { error = "not_recorded", message = "There is no recorded payment outside the bank file to reverse for this employee." });

            var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batch.PayrollRunId, cancellationToken);
            foreach (var period in originals.Select(o => o.Period).Distinct())
                if (await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run?.CompanyId, period, cancellationToken))
                    return UnprocessableEntity(new { error = "gl_period_closed", message = $"GL period {period} is closed. Reopen it before reversing this payment.", period });

            var contras = BuildContraGl(tenantId, batch.PayrollRunId, originals, GlEventTypes.NetSettlementReversal, actorId, GetUserName(), req.Reason!.Trim());
            foreach (var o in originals) o.IsReversed = true;
            _db.FinanceGlEntries.AddRange(contras);
            var amount = originals.Where(o => !string.IsNullOrEmpty(o.DebitAccount)).Sum(o => o.Amount);
            var settlementsReopened = await ReopenSettlementsPaidOutsideBankFileAsync(
                tenantId, batch, employeeId, req.Reason!.Trim(), cancellationToken);
            await PayrollAudit("payroll.outside_payment.reversed", "PayrollPaymentBatch", batch.Id.ToString(),
                new { runId = batch.PayrollRunId, employeeId, amount, reason = req.Reason!.Trim(), reversedEntries = contras.Count, settlementsReopened }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(new { batchId = batch.Id, employeeId, reversed = amount, reversedEntries = contras.Count, settlementsReopened });
        }, cancellationToken);
    }

    /// <summary>
    /// A bounced or cancelled cheque (or a cash payment recorded in error) means the leaver was NOT paid, so a
    /// final settlement this payment moved to Paid goes back to Disbursing, the state
    /// <see cref="FinalizePaidSettlementsAsync"/> took it from; recording the payment again marks it Paid again.
    /// What the payment did is undone where it describes the payment, and kept where it describes the debt:
    /// <list type="bullet">
    /// <item>PaymentBatchId and PaidAtUtc are cleared.</item>
    /// <item>The offboarding's FinalSettlementDone tick, which the payment set, is cleared while the offboarding
    ///   is still in progress. A Completed offboarding is left as it is and the audit row says so.</item>
    /// <item>A residual loan or advance already reclassified to 1420 stays there: that journal and its
    ///   sub-ledger row describe a debt that still exists whether or not the cheque cleared, and the loan no
    ///   longer carries it, so re-recording cannot reclassify it twice.</item>
    /// <item>ResidualDebtUnbooked is reset. It is a report of what stayed on the loan uncovered, and the next
    ///   payment recomputes it from the loan; keeping it would count it twice.</item>
    /// </list>
    /// Runs inside the caller's serialized unit and is saved by its SaveChanges. Each settlement is audited.
    /// </summary>
    private async Task<List<Guid>> ReopenSettlementsPaidOutsideBankFileAsync(
        Guid tenantId, PayrollPaymentBatch batch, int employeeId, string reason, CancellationToken ct)
    {
        var settlements = await _db.EmployeeFinalSettlements
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == batch.PayrollRunId && s.EmployeeId == employeeId
                     && s.PaymentBatchId == batch.Id && s.Status == FinalSettlementStatuses.Paid)
            .ToListAsync(ct);
        if (settlements.Count == 0) return new List<Guid>();

        var offboardingIds = settlements.Select(s => s.OffboardingId).Distinct().ToList();
        var offboardings = await _db.EmployeeOffboardings
            .Where(o => o.TenantId == tenantId && offboardingIds.Contains(o.Id))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var s in settlements)
        {
            var previouslyPaidAtUtc = s.PaidAtUtc;
            var unbookedCleared = s.ResidualDebtUnbooked;
            s.Status = FinalSettlementStatuses.Disbursing;
            s.PaymentBatchId = null;
            s.PaidAtUtc = null;
            s.ResidualDebtUnbooked = 0m;
            s.UpdatedAtUtc = now;

            var off = offboardings.FirstOrDefault(o => o.Id == s.OffboardingId);
            var offboardingCompleted = off?.Status == "Completed";
            var checklistReopened = false;
            if (off is not null && off.FinalSettlementDone && !offboardingCompleted)
            {
                off.FinalSettlementDone = false;
                off.UpdatedAtUtc = now;
                checklistReopened = true;
            }

            await PayrollAudit("payroll.final_settlement.payment_reversed", "EmployeeFinalSettlement", s.Id.ToString(), new
            {
                s.EmployeeId, s.EmployeeCode, from = FinalSettlementStatuses.Paid, to = FinalSettlementStatuses.Disbursing,
                runId = batch.PayrollRunId, batchId = batch.Id, previouslyPaidAtUtc, reason,
                residualReclassedKept = s.ResidualDebtReclassed, residualUnbookedCleared = unbookedCleared,
                offboardingChecklistReopened = checklistReopened, offboardingCompleted,
            }, ct);
        }
        return settlements.Select(s => s.Id).ToList();
    }

    /// <summary>The outside-the-bank-file payments recorded (live, unreversed) for a run, by GL reference.</summary>
    private async Task<HashSet<string>> OutsidePaymentRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> runIds, CancellationToken ct) =>
        (await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && runIds.Contains(x.SourceEntityId)
                     && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed
                     && x.SourceEntityRef.StartsWith(Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentRefPrefix))
            .Select(x => x.SourceEntityRef)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

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
        string settlementPeriod, IReadOnlySet<int> paidEmployeeIds, CancellationToken ct)
    {
        // Only the leavers whose money actually moved: in the settled bank batch, or with a payment
        // recorded outside the bank file. A leaver paid in cash is NOT Paid because the bank batch was.
        var settlements = (await _db.EmployeeFinalSettlements
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id
                     && s.Status == FinalSettlementStatuses.Disbursing)
            .ToListAsync(ct))
            .Where(s => paidEmployeeIds.Contains(s.EmployeeId))
            .ToList();
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
            .Where(l => l.TenantId == tenantId && l.CompanyId == run.CompanyId && !l.IsDeleted && l.CollectionStatus != "OnHold" && l.RepaymentMethod == "PayrollDeduction" && (l.Status == "Active" || l.Status == "Overdue") && l.EmployeeIntId != null
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
                    // T-037 / contract 2.8: no employee identifier on a payroll-source ledger line. The settlement id already rides in
                    // SourceEntityId; the reference is the settlement's own, never the employee's.
                    SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(s.Id),
                    EventType = GlEventTypes.SettlementResidualReclass,
                    DebitAccount = employeeReceivableAccount, CreditAccount = receivableAccount,
                    Amount = covered, Currency = s.Currency,
                    EntryDate = today, Period = settlementPeriod,
                    Description = $"{FinalSettlementGlDescriptions.ResidualReclassPrefix}{what}",
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
        var tenantId = GetTenantId();
        // D3: company authorization BEFORE any read, body parse or mutation of this batch. The
        // entity comes from batch -> run -> PayrollRun.CompanyId, never from the request. Without
        // it these endpoints were tenant-only, and the run-status guards below fail OPEN
        // cross-company (PayrollRun is company-filtered, so the run reads back null).
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;

        // Validated AFTER the entity check, for the same reason as wps-status above.
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required to reverse a settlement." });
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
                     && x.EventType == GlEventTypes.NetSettlement && !x.IsReversed
                     // This batch's settlement only — never a payment recorded outside the bank file.
                     && x.SourceEntityRef == batch.BatchNumber)
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
            // A contra copies the cost centre id and its three snapshots verbatim (AM-25), so a void nets every cost centre to zero.
            CostCenterId = orig.CostCenterId, CostCenterCode = orig.CostCenterCode, CostCenterName = orig.CostCenterName,
            CostCenterGlSegment = orig.CostCenterGlSegment, PostingKey = PayrollCostStamper.ReversalKey(orig.Id),
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
        // D3: company authorization BEFORE any read, body parse or mutation of this batch. The
        // entity comes from batch -> run -> PayrollRun.CompanyId, never from the request. Without
        // it these endpoints were tenant-only, and the run-status guards below fail OPEN
        // cross-company (PayrollRun is company-filtered, so the run reads back null).
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
        var batch    = await _db.PayrollPaymentBatches.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        // The LATEST generation for the batch (a resubmission after Rejected supersedes the original).
        var wpsFile = await _db.WPSFileBatches.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batchId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (wpsFile is null) return BadRequest(new { message = "WPS file has not been generated for this batch yet." });

        // 1. The stored bytes of this generation, when they were kept (every file generated from now on).
        Infrastructure.Payroll.GeneratedWpsFileStore.StoredFile? storedFile;
        try { storedFile = await Infrastructure.Payroll.GeneratedWpsFileStore.LoadAsync(_db, tenantId, batchId, wpsFile.Id, cancellationToken); }
        catch (InvalidDataException) { return WpsFileNotReproducible(wpsFile, batch.WpsStatus, "its stored copy no longer matches its recorded SHA-256"); }
        if (storedFile is not null)
        {
            if (!string.Equals(storedFile.Sha256, wpsFile.FileHash, StringComparison.OrdinalIgnoreCase))
                return WpsFileNotReproducible(wpsFile, batch.WpsStatus, "its stored copy does not match the fingerprint recorded at generation");
            return await ServeWpsFileAsync(tenantId, batchId, wpsFile, storedFile.Bytes, storedFile.FileName, storedFile.Format, cancellationToken);
        }

        var sifRecords  = await _db.SIFFileRecords.AsNoTracking().Where(x => x.TenantId == tenantId && x.WPSFileBatchId == wpsFile.Id).ToListAsync(cancellationToken);
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

        // 2. Files generated before bytes were stored: re-create from the frozen records, ordered by employee
        //    exactly as generation now orders them.
        var dlWpsEmployees = sifRecords.OrderBy(r => r.EmployeeId).Select(r =>
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

        var dlIsKsa = dlExporter is Infrastructure.CountryPack.Ksa.KsaWageProtectionExporter;
        var (dlEstablishmentId, dlConflict) = await ResolveWpsEstablishmentIdAsync(tenantId, run?.CompanyId, dlIsKsa, cancellationToken);
        if ((dlConflict ?? WpsEstablishmentError(dlEstablishmentId, dlIsKsa)) is { } dlEstError)
            return UnprocessableEntity(new { error = dlEstError.Code, message = dlEstError.Message, field = dlEstError.Field });

        var dlInput = new WageProtectionExportInput(
            TenantId:        tenantId,
            CompanyId:       (run?.CompanyId) ?? Guid.Empty,
            PayrollRunId:    run?.Id          ?? Guid.Empty,
            PeriodYear:      run?.Year      ?? DateTime.UtcNow.Year,
            PeriodMonth:     run?.Month     ?? DateTime.UtcNow.Month,
            EstablishmentId: dlEstablishmentId!,
            EmployerIban:    string.Empty,
            CompanyNameEn:   dlCompany?.LegalNameEn ?? string.Empty,
            CompanyNameAr:   dlCompany?.LegalNameAr ?? string.Empty,
            Employees:       dlWpsEmployees);

        // The bytes of a generated file are not stored; the download re-creates them from the frozen
        // records. That is only honest if the result IS the file that was generated: the same format and
        // the same SHA-256. A file generated in an older layout (e.g. the retired "mudad-xml"), or one
        // whose inputs have since changed, is REFUSED rather than silently served in a different form.
        var dlResult = await dlExporter.ExportAsync(dlInput, cancellationToken);
        var fileHash = Convert.ToHexString(SHA256.HashData(dlResult.FileBytes)).ToLowerInvariant();
        if (!string.Equals(wpsFile.FormatVersion, dlResult.Format, StringComparison.Ordinal))
            return WpsFileNotReproducible(wpsFile, batch.WpsStatus, "it was generated in an older file format that this version no longer produces");
        if (!string.IsNullOrEmpty(wpsFile.FileHash) && !string.Equals(wpsFile.FileHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            // Older generations did not order their rows; try the order the records were stored in.
            var unordered = await dlExporter.ExportAsync(dlInput with { Employees = sifRecords.Select(r => dlWpsEmployees.First(w => w.EmployeeId == r.EmployeeId)).ToList() }, cancellationToken);
            if (string.Equals(wpsFile.FileHash, Convert.ToHexString(SHA256.HashData(unordered.FileBytes)).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                (dlResult, fileHash) = (unordered, wpsFile.FileHash);
        }
        if (string.IsNullOrEmpty(wpsFile.FileHash) || !string.Equals(wpsFile.FileHash, fileHash, StringComparison.OrdinalIgnoreCase))
            return WpsFileNotReproducible(wpsFile, batch.WpsStatus, string.IsNullOrEmpty(wpsFile.FileHash)
                ? "no fingerprint (SHA-256) was recorded when it was generated"
                : "the employee or payroll data it was built from has changed since, so it would not match its recorded SHA-256");
        return await ServeWpsFileAsync(tenantId, batchId, wpsFile, dlResult.FileBytes, dlResult.FileName, dlResult.Format, cancellationToken);
    }

    private async Task<IActionResult> ServeWpsFileAsync(Guid tenantId, Guid batchId, WPSFileBatch wpsFile, byte[] bytes,
        string fileName, string format, CancellationToken cancellationToken)
    {
        var fileHash = wpsFile.FileHash;
        // Advance lifecycle from Generated → Downloaded on first download.
        var tracked = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken);
        if (tracked is not null && WpsTransitions.IsAllowed(tracked.WpsStatus, WpsStatuses.Downloaded))
        {
            tracked.WpsStatus = WpsStatuses.Downloaded;
            await PayrollAudit("payroll.wps.downloaded", "WPSFileBatch", wpsFile.Id.ToString(),
                new { batchId, fileHash }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var mimeType = fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : "text/plain";
        Response.Headers["Content-Disposition"] = $"attachment; filename={fileName}";
        // The artefact itself states what it is. The bytes are content-addressed by SHA-256 and
        // their determinism is pinned by tests, so the statement rides on a header rather than in
        // the payload — a WPS gateway would reject an unexpected comment line anyway. Without this,
        // the downloaded file is completely silent about never having been accepted by a gateway.
        Response.Headers[Infrastructure.Payroll.WpsConformance.DownloadHeader] =
            Infrastructure.Payroll.WpsConformance.HeaderValue(format);
        return File(bytes, mimeType, fileName);
    }

    /// <summary>The way forward depends on where the batch is: only a Submitted batch can be marked
    /// Rejected (and so regenerated); an Accepted/Paid one has already been filed.</summary>
    private IActionResult WpsFileNotReproducible(WPSFileBatch file, string? batchStatus, string why) => Conflict(new
    {
        error = "wps_file_not_reproducible",
        message = $"This file cannot be downloaded again because {why}. Only the exact file that was generated is ever served. "
                  + batchStatus switch
                  {
                      WpsStatuses.Accepted or WpsStatuses.Paid or WpsStatuses.Reconciled =>
                          "This batch has already been accepted, so nothing is regenerated: use the copy that was submitted to the bank, and the bank's output file or Mudad evidence attached to the batch.",
                      WpsStatuses.Submitted =>
                          "If the bank rejected the file, mark the batch Rejected and generate a new one; otherwise use the copy that was submitted.",
                      _ =>
                          "Use the copy already downloaded. If none was kept, contact your administrator before generating a replacement, so two different files are never sent for one month.",
                  },
        batchWpsStatus = batchStatus,
        formatVersion = file.FormatVersion,
        formatLabel = Infrastructure.Payroll.WpsConformance.LabelFor(file.FormatVersion),
        recordedSha256 = file.FileHash,
    });

    /// <summary>
    /// Returns the WPS export history for a payment batch (all WPSFileBatch records).
    /// Requires payroll.export permission.
    /// </summary>
    [HttpGet("payment-batches/{batchId:guid}/wps-export-history")]
    public async Task<IActionResult> WpsExportHistory(Guid batchId, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        // D3: company authorization BEFORE any read, body parse or mutation of this batch. The
        // entity comes from batch -> run -> PayrollRun.CompanyId, never from the request. Without
        // it these endpoints were tenant-only, and the run-status guards below fail OPEN
        // cross-company (PayrollRun is company-filtered, so the run reads back null).
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
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
        // D3: a batch inherits its legal entity from its run. The caller's entity set is applied
        // EXPLICITLY from the token rather than leaning on the ambient company filter — that filter is
        // disabled whenever the context has no request principal, so relying on it would have made this
        // scope silently absent in exactly the places it is hardest to notice. Same rule as the
        // single-batch guard: a legacy null-company run is group-only.
        var listScope = this.GetEntityScope();
        var scopedRuns = Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.PayrollRuns, tenantId,
                "The company dimension is re-applied from the caller's own token immediately below; it is "
                + "dropped first so a null-company run cannot be silently included by the ambient filter.")
            .AsNoTracking();
        if (!listScope.IsGroupLevel)
        {
            var listCompanyIds = listScope.AccessibleCompanyIds;
            scopedRuns = scopedRuns.Where(r => r.CompanyId != null && listCompanyIds.Contains(r.CompanyId.Value));
        }
        var scopedRunIds = scopedRuns.Select(r => r.Id);
        query = query.Where(x => scopedRunIds.Contains(x.PayrollRunId));
        if (runId.HasValue) query = query.Where(x => x.PayrollRunId == runId.Value);
        // SAFE-SERIALIZATION: PayrollPaymentBatch is a payment workflow aggregate (TotalAmount is batch-level) — no per-employee salary PII.
        var batches = await query.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);

        // POD-D4 — batch.WpsStatus is set to Paid AT SETTLEMENT, which happens BEFORE the bank's response
        // exists (Submitted → Accepted → settle(Paid) → bank file comes back with returns). Returning the
        // bare status therefore reports "Paid" over money that bounced. The per-employee confirmation
        // coverage rides alongside it so a batch with open returns can never present as terminal-clean.
        var d4BatchIds = batches.Select(b => b.Id).ToList();
        var evidenceByBatch = await EvidenceService().ListForBatchesAsync(tenantId, d4BatchIds, cancellationToken);
        // The lifecycle the screen shows and offers is computed HERE, once: the effective status (a frozen
        // ANB instruction counts as Generated), its plain label, and the transitions an operator may pick.
        var instructionMakers = await new Infrastructure.Payroll.SaudiBankExports.SaudiBankExportService(_db)
            .InstructionGeneratorsAsync(tenantId, d4BatchIds, cancellationToken);
        var latestWpsFiles = d4BatchIds.Count == 0
            ? new Dictionary<Guid, WPSFileBatch>()
            : (await _db.WPSFileBatches.AsNoTracking()
                    .Where(f => f.TenantId == tenantId && d4BatchIds.Contains(f.PaymentBatchId))
                    .ToListAsync(cancellationToken))
                .GroupBy(f => f.PaymentBatchId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.CreatedAtUtc).ThenByDescending(f => f.Id).First());
        var batchExclusions = await Infrastructure.Payroll.PaymentBatchExclusions.LoadAsync(_db, tenantId, d4BatchIds, cancellationToken);
        var outsideRecorded = await OutsidePaymentRefsAsync(tenantId, batches.Select(b => b.PayrollRunId).Distinct().ToList(), cancellationToken);
        var viewerId = GetUserId();
        // KSA batches: the bank file is the ANB instruction and the generated XML is an internal register.
        var batchRunIds = batches.Select(b => b.PayrollRunId).Distinct().ToList();
        var ksaRunIds = (await (from r in _db.PayrollRuns.AsNoTracking()
                                join c in _db.Companies.AsNoTracking() on r.CompanyId equals c.Id
                                where r.TenantId == tenantId && batchRunIds.Contains(r.Id)
                                      && (c.CountryCode == "SA" || c.CountryCode == "SAU" || c.CountryCode == "KSA")
                                select r.Id).ToListAsync(cancellationToken)).ToHashSet();
        var d4Records = d4BatchIds.Count == 0
            ? new List<PayrollPaymentRecord>()
            : await _db.PayrollPaymentRecords.AsNoTracking()
                .Where(r => r.TenantId == tenantId && d4BatchIds.Contains(r.PaymentBatchId))
                .ToListAsync(cancellationToken);

        return Ok(batches.Select(b =>
        {
            var cov = Infrastructure.Finance.BankConfirmationService.Coverage(
                d4Records.Where(r => r.PaymentBatchId == b.Id).ToList());
            var evidence = evidenceByBatch.GetValueOrDefault(b.Id) ?? new List<Infrastructure.Payroll.WpsEvidenceEnvelope>();
            var latestEvidence = evidence.LastOrDefault();
            var hasInstruction = instructionMakers.TryGetValue(b.Id, out var instructionMaker);
            latestWpsFiles.TryGetValue(b.Id, out var latestWpsFile);
            var hasFile = hasInstruction || latestWpsFile is not null;
            var effective = Infrastructure.Payroll.WpsLifecycleView.Effective(b.WpsStatus, hasInstruction);
            var acceptBlocked = viewerId is not Guid viewer
                || viewer == instructionMaker || viewer == latestWpsFile?.GeneratedByUserId || viewer == latestEvidence?.UploadedBy;
            var exclusions = batchExclusions.GetValueOrDefault(b.Id) ?? Array.Empty<Infrastructure.Payroll.PaymentBatchExclusion>();
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
                acceptanceEvidenceCount = evidence.Count,
                latestEvidenceId = latestEvidence?.EvidenceId,
                effectiveWpsStatus = effective,
                wpsStatusLabel = Infrastructure.Payroll.WpsLifecycleView.Label(effective, evidence.Count),
                allowedNextStatuses = Infrastructure.Payroll.WpsLifecycleView.ManualNext(effective, hasFile),
                // Why THIS viewer cannot record Accepted (maker-checker), so the screen can say so up front.
                acceptBlockedReason = acceptBlocked ? Infrastructure.Payroll.WpsLifecycleView.MakerCheckerMessage : null,
                paymentExclusions = exclusions.Select(x => new
                {
                    x.EmployeeId, x.EmployeeCode, x.Amount, x.ReasonCode, x.Reason,
                    canRecordOutsidePayment = x.ReasonCode == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideBankFileCode && x.Amount > 0m,
                    outsidePaymentRecorded = outsideRecorded.Contains(
                        Infrastructure.Payroll.PaymentBatchExclusions.OutsidePaymentRef(b.BatchNumber, x.EmployeeId)),
                }).ToList(),
                excludedTotal = exclusions.Sum(x => x.Amount),
                mudadNote = exclusions.Any(x => x.ReasonCode == Infrastructure.Payroll.PaymentBatchExclusions.PaidOutsideBankFileCode)
                    ? Infrastructure.Payroll.PaymentBatchExclusions.MudadNote : null,
                isSaudi = ksaRunIds.Contains(b.PayrollRunId),
                generatedFileLabel = ksaRunIds.Contains(b.PayrollRunId)
                    ? Infrastructure.Payroll.WpsConformance.KsaPayrollRegisterLabel
                    : "WPS/SIF file (not verified with any gateway)",
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
        // D3: company authorization BEFORE any read, body parse or mutation of this batch. The
        // entity comes from batch -> run -> PayrollRun.CompanyId, never from the request. Without
        // it these endpoints were tenant-only, and the run-status guards below fail OPEN
        // cross-company (PayrollRun is company-filtered, so the run reads back null).
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, id, cancellationToken) is { } batchScopeErr)
            return batchScopeErr;
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
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var query = _db.Payslips.Where(x => x.TenantId == tenantId && x.PayrollRunId == id);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.EmployeeId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var names = await ResolvePayslipEmployeeNamesAsync(tenantId, id, items, cancellationToken);
        // SAFE-SERIALIZATION: PayslipListItemDto is a header-only record (Id, EmployeeId, employee name/code,
        // PayslipNumber, IsPublishedToEss) — no salary amounts. The name comes from the same PayrollSlip row
        // the payslip PDF prints from, so the list and the document always agree.
        // Order stays EmployeeId (the paged DB order) — re-sorting by name inside the page would make
        // page boundaries and display order disagree once a run exceeds one page.
        var dtos = items
            .Select(x => PayslipListItemDto.Project(x, names[x.EmployeeId].Code, names[x.EmployeeId].Name))
            .ToList();
        return Ok(new PagedResult<PayslipListItemDto>(dtos, total, page, pageSize));
    }

    /// <summary>
    /// Resolves the employee name + code for a set of payslip headers.
    ///
    /// The <see cref="Payslip"/> row is header-only and stores neither, so the name is read from the
    /// PayrollSlip row for the same run + employee — the SAME denormalised value
    /// <see cref="DownloadSlipPdf"/> prints on the PDF, captured when the run was processed. Reading it
    /// from anywhere else (e.g. the live Employees row) would let a renamed or transferred employee's
    /// list entry drift away from their issued payslip document.
    ///
    /// Employees whose PayrollSlip row is gone (a reopened run mid-reprocess wipes slips but keeps
    /// payslips) fall back to the live Employee record, and only then — never silently — to the
    /// employee code. A payslip header with no resolvable identity at all is still listed, labelled by
    /// its numeric id, rather than being dropped from the page.
    /// </summary>
    private async Task<Dictionary<int, (string Code, string Name)>> ResolvePayslipEmployeeNamesAsync(
        Guid tenantId, Guid runId, IReadOnlyCollection<Payslip> payslips, CancellationToken ct)
    {
        var resolved = new Dictionary<int, (string Code, string Name)>();
        if (payslips.Count == 0) return resolved;

        var employeeIds = payslips.Select(p => p.EmployeeId).Distinct().ToList();

        foreach (var s in await _db.PayrollSlips.AsNoTracking()
                     .Where(s => s.TenantId == tenantId && s.RunId == runId && employeeIds.Contains(s.EmployeeId))
                     .Select(s => new { s.EmployeeId, s.EmployeeCode, s.EmployeeName })
                     .ToListAsync(ct))
        {
            if (!string.IsNullOrWhiteSpace(s.EmployeeName))
                resolved[s.EmployeeId] = (s.EmployeeCode, s.EmployeeName);
        }

        var missing = employeeIds.Where(eid => !resolved.ContainsKey(eid)).ToList();
        if (missing.Count > 0)
        {
            foreach (var e in await _db.Employees.AsNoTracking()
                         .Where(e => e.TenantId == tenantId && missing.Contains(e.Id))
                         .Select(e => new { e.Id, e.EmployeeCode, e.FullName })
                         .ToListAsync(ct))
            {
                if (!string.IsNullOrWhiteSpace(e.FullName))
                    resolved[e.Id] = (e.EmployeeCode, e.FullName);
                else if (!string.IsNullOrWhiteSpace(e.EmployeeCode))
                    resolved[e.Id] = (e.EmployeeCode, e.EmployeeCode);
            }
        }

        foreach (var eid in employeeIds)
        {
            if (!resolved.ContainsKey(eid)) resolved[eid] = (string.Empty, $"Employee {eid}");
        }
        return resolved;
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

        // Headers name what the columns ACTUALLY hold. `OtherAllowances` is not "other allowances": it is
        // the whole non-basic/housing/transport earnings bucket (Process, :2693 — other allowances +
        // overtime + bonuses + adjustments + arrears + settlement + configured earnings), and `Deductions`
        // already contains the loan/advance EMIs and the employee GOSI share (:2542-2543, :2635). The four earning
        // columns are disjoint and sum to Gross; Gross − Deductions = Net. The old headers invited a reader
        // to count overtime twice and to subtract loans a second time.
        var headers = new[] { "Employee Code", "Employee Name", "Department", "Basic Salary", "Housing Allowance", "Transport Allowance", "Other Earnings (incl. overtime, bonuses, arrears)", "Gross Salary", "Deductions (incl. loans and GOSI)", "Net Salary", "Status" };
        var rows = slips.Select(s => (IReadOnlyList<object?>)new object?[]
        {
            s.EmployeeCode, s.EmployeeName, s.Department,
            // Invariant culture: a server whose culture uses a decimal comma must not change the file's numbers.
            s.BasicSalary.ToString("F2", CultureInfo.InvariantCulture), s.HousingAllowance.ToString("F2", CultureInfo.InvariantCulture),
            s.TransportAllowance.ToString("F2", CultureInfo.InvariantCulture), s.OtherAllowances.ToString("F2", CultureInfo.InvariantCulture),
            s.GrossSalary.ToString("F2", CultureInfo.InvariantCulture), s.Deductions.ToString("F2", CultureInfo.InvariantCulture),
            s.NetSalary.ToString("F2", CultureInfo.InvariantCulture), s.Status
        });
        var csv = Csv.Build(headers, rows);
        // Export audit: who exported which run's salary register, how many rows, and the
        // company dimension — never the salary/IBAN values themselves.
        await PayrollAudit("payroll.register.exported", "PayrollRun", runId.ToString(),
            new { run.Year, run.Month, run.CompanyId, rowCount = slips.Count, scoped = !scope.IsUnrestricted }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        Response.Headers["Content-Disposition"] = $"attachment; filename=salary-register-{run.Year}-{run.Month:D2}.csv";
        return CsvDownload.Result(csv);
    }

    [HttpGet("reports/summary")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> ReportSummary(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var runs = await _db.PayrollRuns.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        var lockedThisYear = runs.Where(x => x.Status == "Locked" && x.Year == DateTime.UtcNow.Year).ToList();
        return Ok(new
        {
            totalRuns = runs.Count,
            lockedRuns = runs.Count(x => x.Status == "Locked"),
            totalEmployeesPaid = runs.Where(x => x.Status == "Locked").Sum(x => x.EmployeeCount),
            // Kept for older clients. In a group whose companies pay in different currencies these two
            // add SAR to AED; ytdByCompany is what a client labels and totals per currency.
            totalGrossYtd = lockedThisYear.Sum(x => x.TotalGrossSalary),
            totalNetYtd = lockedThisYear.Sum(x => x.TotalNetSalary),
            // R03 — year-to-date per legal entity. A run is paid in its company's currency, so the client
            // resolves each company's currency and never sums across two.
            ytdByCompany = lockedThisYear
                .GroupBy(x => x.CompanyId)
                .Select(g => new
                {
                    companyId = g.Key,
                    totalGrossYtd = g.Sum(x => x.TotalGrossSalary),
                    totalNetYtd = g.Sum(x => x.TotalNetSalary),
                    lockedRuns = g.Count(),
                })
                .ToList(),
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

        var (companyId, companyError) = await ResolveSettlementCompanyAsync(tenantId, employee, cancellationToken);
        if (companyError is not null) return UnprocessableEntity(companyError);
        var company = await _db.Companies.AsNoTracking()
            .SingleAsync(c => c.TenantId == tenantId && c.Id == companyId, cancellationToken);
        var gcc = await _db.GCCComplianceSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && x.CountryCode == company.CountryCode, cancellationToken);
        if (gcc is null || !gcc.EosbEnabled)
            return BadRequest(new { message = $"EOSB is not enabled for company country '{company.CountryCode}'. Configure that country in GCC Settings first." });

        var calcDate = req.AsOfDate ?? DateTime.UtcNow;
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive && x.EffectiveDate <= DateOnly.FromDateTime(calcDate))
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);

        var wageBasis = await ResolveEosbWageBasisAsync(tenantId, company.Id, salary, employee, cancellationToken);
        // M1 — the CARRIED service start wins when it is earlier than this product's joining date. See
        // ResolveEosbServiceStartAsync for why it is the earlier of the two and never the sum.
        var (serviceStart, carriedStart) = await ResolveEosbServiceStartAsync(tenantId, employee, cancellationToken);
        var joiningDate = employee.JoiningDate;

        // Resolve the separation reason once, consistently with /final-settlement:
        // explicit request override → recorded EmployeeOffboarding.SeparationType → conservative default.
        var terminationReason = await ResolveTerminationReasonAsync(tenantId, req.EmployeeId, req.TerminationReason, cancellationToken);

        // Single authoritative EOSB engine (shared verbatim with /final-settlement via
        // ComputeEndOfServiceAsync). No controller-side minYears pre-gate: KSA Art.84 grants
        // pro-rata gratuity to short-service TERMINATIONS from day one; the <2yr RESIGNATION
        // forfeiture, the Art.80 dismissal forfeiture, and the non-KSA ≥1yr eligibility floors
        // are all owned by the country packs (the single source of truth).
        var unpaidLeaveDays = await ResolveUnpaidLeaveDaysAsync(
            tenantId, employee.Id, serviceStart, DateOnly.FromDateTime(calcDate), cancellationToken);
        var (eosbResult, totalYears) = await ComputeEndOfServiceAsync(
            company.CountryCode, company.Jurisdiction, company.Id, wageBasis,
            serviceStart, calcDate, terminationReason, employee, unpaidLeaveDays, gcc, cancellationToken);
        var eosbAmount  = Math.Round(eosbResult.TotalGratuity, 2);
        var eosbFormula = eosbResult.ApplicableRule;
        // S1/A1 — the wage the PACK awarded on, not the controller's pre-narrowed scalar. When a pack
        // does not report one (Default), fall back to the configured wage so the field is never zero
        // against a non-zero award.
        var eligibleSalary = eosbResult.AppliedWageBase > 0m ? eosbResult.AppliedWageBase : wageBasis.ConfiguredWage;
        var dailySalary = eligibleSalary * 12 / 365m;
        var monthlySalary = eligibleSalary;

        // Persist the calculation
        var existing = await _db.EOSBCalculations.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.Status == "Draft", cancellationToken);
        if (existing is not null)
        {
            existing.CalculationDate = DateOnly.FromDateTime(calcDate);
            existing.EligibleSalary = eligibleSalary;
            existing.CalculatedAmount = eosbAmount;
            existing.RulesSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new { formula = eosbFormula, totalYears, dailySalary, monthlySalary, countryCode = company.CountryCode, company.Jurisdiction, wageBasis.IncludedComponents });
        }
        else
        {
            _db.EOSBCalculations.Add(new EOSBCalculation
            {
                TenantId = tenantId, EmployeeId = req.EmployeeId, CalculationDate = DateOnly.FromDateTime(calcDate),
                EligibleSalary = eligibleSalary, CalculatedAmount = eosbAmount,
                RulesSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new { formula = eosbFormula, totalYears, dailySalary, monthlySalary, countryCode = company.CountryCode, company.Jurisdiction, wageBasis.IncludedComponents })
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            employeeId = req.EmployeeId,
            employeeName = employee.FullName,
            joiningDate,
            // M1 — the date the award was actually measured from, beside the joining date it may differ
            // from, so "ten years of gratuity for a four-month employee" is explained on its face.
            serviceStartDate = serviceStart,
            carriedServiceStartDate = carriedStart,
            asOfDate = calcDate,
            totalYears = Math.Round(totalYears, 2),
            eligibleSalary,
            dailySalary = Math.Round(dailySalary, 4),
            formula = eosbFormula,
            eosbAmount,
            wageBasis = wageBasis.IncludedComponents,
            // S1/A1 — the pack's statutory notices and the compatibility-fallback reason are part of the
            // ANSWER, not a snapshot footnote. A caller that cannot see them cannot sign off on the award.
            statutoryNotices = EosbCalculateNotices(eosbResult, wageBasis, serviceStart, joiningDate),
            unpaidLeaveDays,
            countryCode = company.CountryCode,
            jurisdiction = company.Jurisdiction,
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
    // Wage base: the caller passes the legal entity's configured PayComponent.EosbIncluded sum as the
    // pack's Basic slot. Country calculators deliberately apply their formula to that single resolved
    // last-wage base; they do not reach back to tenant/global salary configuration.
    //
    // Service period: the pack derives service length from DateOnly start/end
    // (fullMonths/12 + remDays/365), eliminating the /365.0 leap drift the old inline
    // final-settlement formula suffered.
    /// <summary>
    /// S1/A1 — the EOSB wage inputs. The controller no longer DECIDES the wage base; it reports the
    /// facts (the real salary package, and whatever the tenant's component catalog flags in) and the
    /// country pack applies its own statute to them.
    ///
    /// <para>Before this change the controller collapsed the package into a single scalar and handed it
    /// to the pack as <c>new SalaryBreakdown(eligibleWage, 0, 0, 0)</c> — housing and transport
    /// explicitly zeroed — so no pack could ever see, let alone apply, the Art. 84 "last wage". That is
    /// the mechanical cause of A1.</para>
    /// </summary>
    private sealed record EosbWageBasis(
        SalaryBreakdown Package,
        decimal ConfiguredWage,
        IReadOnlyList<string> IncludedComponents,
        string? FallbackReason);

    private async Task<EosbWageBasis> ResolveEosbWageBasisAsync(
        Guid tenantId, Guid? companyId, EmployeeSalaryStructure? salary, Employee employee, CancellationToken ct)
    {
        // F2 — the catalog version in effect for the CURRENT payroll month (the EOSB wage is the last wage).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var components = await LoadPayComponentsAsync(tenantId, companyId, new DateOnly(today.Year, today.Month, 1), ct);
        return EosbWageBasisFrom(components, salary, employee);
    }

    /// <summary>The pure half of <see cref="ResolveEosbWageBasisAsync"/>: the same wage-basis rule over a
    /// catalog the caller already loaded, so the monthly provision (POD-C2) measures every employee on
    /// exactly the base a settlement would, without reloading the catalog once per employee.</summary>
    private static EosbWageBasis EosbWageBasisFrom(
        IEnumerable<PayComponent> components, EmployeeSalaryStructure? salary, Employee employee)
    {
        var basic = salary?.BasicSalary ?? employee.Salary ?? 0m;
        var housing = salary?.HousingAllowance ?? 0m;
        var transport = salary?.TransportAllowance ?? 0m;
        var other = (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
        var gross = basic + housing + transport + other;
        var package = new SalaryBreakdown(basic, housing, transport, other);
        var included = components
            .Where(c => c.ComponentType == PayComponentTypes.Earning && c.EosbIncluded && c.IsActive && !c.IsDeleted)
            .ToList();
        var computed = PayComponentEngine.Compute(included, new PayComponentContext
        {
            Basic = basic,
            Housing = housing,
            Transport = transport,
            OtherAllowances = other,
            Gross = gross,
        }).Earnings;
        var eligible = Math.Round(computed.Sum(l => l.Amount), 2);
        if (eligible > 0m)
            return new EosbWageBasis(package, eligible,
                computed.Select(l => l.Code).Distinct(StringComparer.Ordinal).ToList(), null);

        // A tenant may have a partially seeded component catalog with no EOSB flags at all. The OLD
        // behaviour here was to fall back to BASIC ALONE and record the reason in a snapshot column
        // nobody reads — a silent 40% under-award on a Saudi settlement. It now falls back to the FULL
        // PACKAGE and leaves the statutory narrowing to the pack (KSA takes basic + housing + configured
        // allowances; UAE Art. 51 and Qatar Art. 54 take basic and are unaffected), and the fallback is
        // surfaced as a settlement warning by the caller instead of being buried.
        return new EosbWageBasis(package, 0m,
            ["(compatibility fallback: no EOSB-flagged pay component — statutory base applied by the country pack)"],
            "No active pay component in this company's catalog is flagged EosbIncluded, so the gratuity was " +
            "computed on the country pack's STATUTORY base rather than on a configured one. For KSA that is " +
            "Art. 84 last wage (basic + housing, plus transport unless disabled); for UAE/Qatar it is basic. " +
            "Seed the pay-component catalog so the base is an explicit, auditable configuration.");
    }

    /// <summary>
    /// S1/A8 — calendar days of APPROVED UNPAID leave falling inside the service window.
    ///
    /// <para>UAE Decree-Law 33/2021 Art. 51 says expressly that periods of unpaid leave are not
    /// included in the service period for gratuity. The product had the data all along — an unpaid
    /// leave type already drives a payroll deduction — and simply never fed it back into service
    /// accrual, so every UAE gratuity was over-stated by the unpaid time and the EOSB provision on the
    /// balance sheet with it. KSA and Qatar turn on "continuous service" and their packs default to
    /// INCLUDING the days; each pack decides, this method only reports the fact.</para>
    ///
    /// <para>A request straddling the window boundary is apportioned by the overlapping fraction of its
    /// own span, so a leave that starts before the joining date or runs past the last working day
    /// contributes only the part actually inside service.</para>
    /// </summary>
    private async Task<int> ResolveUnpaidLeaveDaysAsync(
        Guid tenantId, int employeeId, DateOnly windowStart, DateOnly windowEnd, CancellationToken ct)
    {
        if (windowEnd < windowStart) return 0;

        var unpaidTypeIds = await _db.LeaveTypes.AsNoTracking()
            .Where(t => t.TenantId == tenantId && !t.IsPaid)
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (unpaidTypeIds.Count == 0) return 0;

        var requests = await _db.LeaveRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && r.EmployeeId == employeeId
                     && r.Status == "Approved"
                     && unpaidTypeIds.Contains(r.LeaveTypeId)
                     && r.StartDate <= windowEnd
                     && r.EndDate >= windowStart)
            .Select(r => new { r.StartDate, r.EndDate, r.TotalDays })
            .ToListAsync(ct);

        return UnpaidLeaveDaysWithin(requests.Select(r => (r.StartDate, r.EndDate, r.TotalDays)), windowStart, windowEnd);
    }

    /// <summary>The apportioning arithmetic of <see cref="ResolveUnpaidLeaveDaysAsync"/>, over requests the
    /// caller already loaded (POD-C2 loads them once for the whole run).</summary>
    private static int UnpaidLeaveDaysWithin(
        IEnumerable<(DateOnly StartDate, DateOnly EndDate, decimal TotalDays)> requests, DateOnly windowStart, DateOnly windowEnd)
    {
        if (windowEnd < windowStart) return 0;
        decimal days = 0m;
        foreach (var r in requests)
        {
            if (r.StartDate > windowEnd || r.EndDate < windowStart) continue;
            var span = r.EndDate.DayNumber - r.StartDate.DayNumber + 1;
            if (span <= 0) continue;
            var from = r.StartDate > windowStart ? r.StartDate : windowStart;
            var to   = r.EndDate   < windowEnd   ? r.EndDate   : windowEnd;
            var overlap = to.DayNumber - from.DayNumber + 1;
            if (overlap <= 0) continue;
            var counted = r.TotalDays > 0m ? r.TotalDays : span;
            days += overlap >= span ? counted : counted * overlap / span;
        }

        return (int)Math.Round(days, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// M1 — the service start the gratuity is actually measured from, and why.
    ///
    /// <para><see cref="EmployeeEosbOpeningBalance.PriorServiceStartDate"/> is the service start the
    /// PREVIOUS employer or system recognised: a TUPE-style transfer, an acquired book, or simply the
    /// real joining date of an employee whose KynexOne record starts at the migration cutover. The
    /// migration importer has validated and persisted it since the cutover stream shipped
    /// (<c>MigrationImportController.OpeningBalances.cs:642,669</c>) and, until this change, NOTHING in
    /// the solution read it back. Both EOSB entry points measured service from
    /// <c>Employee.JoiningDate</c> alone, so a customer who migrated a ten-year employee settled them
    /// as a new joiner and the gratuity was short by the carried years.</para>
    ///
    /// <para><b>WHICH DATE WINS.</b> The EARLIER of the two, never a sum. The carried date is a claim
    /// about the SAME continuous service the joining date describes, not a separate period tacked on
    /// the front — a KynexOne joining date of 2026-09-01 with a prior service start of 2016-03-01 is one
    /// employee with ten years of service, not ten years plus five months. Adding the two would be the
    /// double count. A prior date LATER than the joining date is ignored rather than honoured: it can
    /// only mean a stale or mis-keyed carried row, and shortening a statutory entitlement on that
    /// evidence is the direction that gets litigated.</para>
    ///
    /// <para><b>WHAT IS DELIBERATELY NOT USED HERE.</b> <c>AccruedMonths</c> and <c>AccruedAmount</c> on
    /// the same row are the PROVISION the outgoing system had already booked — an accounting fact about
    /// the balance sheet, consumed by <see cref="EosbProvisionLedger"/> so the expense is not booked
    /// twice. They are NOT a second input to service length. Feeding both the carried months and the
    /// carried start date into the calculator would count the pre-cutover service twice over, which is
    /// exactly the error this method exists to avoid; the entity's own doc comment says so, and it is
    /// repeated here because the two live in one imported row and look interchangeable.</para>
    ///
    /// <para>Several carried rows can exist for one employee (a re-migration restates the provision at a
    /// later AsAtDate). The most recently struck row wins, and among rows struck on the same date, the
    /// earliest prior-service claim — the one most favourable to the employee, and the one an employer
    /// cannot quietly walk back by re-importing.</para>
    ///
    /// <para><b>LEGAL BASIS.</b> Saudi Labour Law (Royal Decree M/51) Art. 84 measures the award on the
    /// worker's "period of service", and the period is the worker's continuous service with the employer,
    /// not the life of a database record. Two cases carry service in: (1) a system migration by the SAME
    /// employer — the carried date is simply the real joining date, and nothing about the service changed;
    /// (2) a transfer of the establishment (sale, merger, division, transfer of ownership) — Art. 18 keeps
    /// the contract in force with the successor, the service continuous, and the successor liable for the
    /// worker's accrued rights, end-of-service included. Either way the award is measured from the earlier
    /// date, once.</para>
    ///
    /// <para>[COUNSEL] Prior service at a DIFFERENT legal entity that is NOT an Art. 18 transfer — an
    /// intra-group move between two commercial registrations under a fresh contract, or a re-hire after a
    /// settled exit — is honoured here exactly as carried, because the importer cannot tell the cases apart.
    /// If the earlier employer already paid an end-of-service award for that period, counting it again
    /// double-pays it. Counsel to confirm: (a) whether intra-group service without an Art. 18 transfer
    /// counts toward Art. 84 absent a contractual undertaking, and (b) whether the carried row needs a
    /// "prior award settled through" date that the calculation must start after.</para>
    /// </summary>
    private async Task<(DateOnly Start, DateOnly? Carried)> ResolveEosbServiceStartAsync(
        Guid tenantId, Employee employee, CancellationToken ct)
    {
        var carried = await _db.EmployeeEosbOpeningBalances.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.PriorServiceStartDate != null)
            .OrderByDescending(x => x.AsAtDate)
            .ThenBy(x => x.PriorServiceStartDate)
            .Select(x => x.PriorServiceStartDate)
            .FirstOrDefaultAsync(ct);
        return PickEosbServiceStart(employee, carried);
    }

    /// <summary>The rule of <see cref="ResolveEosbServiceStartAsync"/> once the carried date is known:
    /// the EARLIER of the two, never the sum.</summary>
    private static (DateOnly Start, DateOnly? Carried) PickEosbServiceStart(Employee employee, DateOnly? carried)
    {
        var joining = DateOnly.FromDateTime(employee.JoiningDate);
        return carried is DateOnly prior && prior < joining ? (prior, prior) : (joining, carried);
    }

    private static List<string> EosbCalculateNotices(
        EndOfServiceResult result, EosbWageBasis wageBasis, DateOnly serviceStart, DateTime joiningDate)
    {
        var notices = result.Notices.ToList();
        if (wageBasis.FallbackReason is not null) notices.Insert(0, wageBasis.FallbackReason);
        var joining = DateOnly.FromDateTime(joiningDate);
        if (serviceStart < joining) notices.Insert(0, CarriedServiceNotice(serviceStart, joining));
        return notices;
    }

    /// <summary>M1 — the settlement-facing notice for a gratuity measured on carried service.</summary>
    private static string CarriedServiceNotice(DateOnly carried, DateOnly joining) =>
        $"[MIGRATION] Gratuity is measured from the CARRIED service start {carried:yyyy-MM-dd}, not this "
      + $"product's joining date {joining:yyyy-MM-dd}. The earlier date was carried in by the opening-balance "
      + "import as the service the previous employer or system recognised, and it is the whole service "
      + "period for end-of-service purposes. If it is wrong, correct the carried end-of-service opening "
      + "balance before approving — not the joining date, which payroll and tenure both depend on.";

    private async Task<(EndOfServiceResult Result, double ServiceYearsDisplay)> ComputeEndOfServiceAsync(
        string countryCode, string jurisdiction, Guid companyId, EosbWageBasis wageBasis,
        DateOnly serviceStartDate, DateTime asOfDate, string terminationReason, Employee employee,
        int unpaidLeaveDays, GCCComplianceSetting? gcc, CancellationToken ct)
    {
        var calc = _packResolver.ResolveEndOfServiceCalculator(countryCode, jurisdiction);

        var input = new EndOfServiceInput(
            EmployeeId:        employee.PublicId,
            CompanyId:         companyId,
            Salary:            wageBasis.Package,
            ServiceStartDate:  serviceStartDate,
            ServiceEndDate:    DateOnly.FromDateTime(asOfDate),
            TerminationReason: terminationReason,
            ContractType:      employee.ContractType ?? "Indefinite",
            Nationality:       employee.Nationality ?? string.Empty)
        {
            ConfiguredEosbWage = wageBasis.ConfiguredWage,
            UnpaidLeaveDays    = unpaidLeaveDays,
            // S1 — the three EOSB fields the country compliance-config screen has always let a customer
            // edit and which nothing read. The pack decides what it is lawful to honour.
            Policy             = gcc is null ? null : new EosbPolicyOverride(
                                     gcc.EosbYears1To5Rate, gcc.EosbYearsAbove5Rate, gcc.EosbMinYears),
        };

        var result = await calc.CalculateAsync(input, ct);
        // M1 — the displayed years are measured on the SAME start the pack awarded on, so the settlement
        // screen cannot show four years next to a ten-year award.
        // L4: show the years the pack AWARDED on (months/12 + days/365), not a separate days/365 figure that can differ by a
        // day-fraction (2024-01-01 to 2025-12-31 is exactly 2.0000 years awarded, 731/365 = 2.0027 displayed).
        var serviceYearsDisplay = result.ServiceYearsAwarded is { } awarded
            ? (double)awarded
            : ((asOfDate.Date - serviceStartDate.ToDateTime(TimeOnly.MinValue)).Days + 1) / 365.0;
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
    // dismissal-for-cause forfeiture), "Article87" / "Article81" (the express exceptions to
    // Art.85 — full award), or a pass-through employer-side reason (full award).
    internal static string NormalizeTerminationReason(string? raw)
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
        // Art.87 and Art.81 are the express EXCEPTIONS to the Art.85 resignation reduction: the
        // worker left, but the FULL Art.84 award is due. They must canonicalise to their own codes —
        // falling through to "Resignation" would cut the award by a third or two thirds, and falling
        // through as free text would work only by accident. See KsaEndOfServiceCalculator.
        if (r.Equals("Article87", StringComparison.OrdinalIgnoreCase)
            || r.Equals("Art87", StringComparison.OrdinalIgnoreCase))
            return "Article87";
        if (r.Equals("Article81", StringComparison.OrdinalIgnoreCase)
            || r.Equals("Art81", StringComparison.OrdinalIgnoreCase))
            return "Article81";
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

    // ── Shared GL routing (single source of truth for BOTH the on-screen GL Journal
    //    preview and the locked-run posting, so a preview equals what will post) ──────────
    //
    // These helpers were extracted from BuildPayrollGlEntries so GlJournal can resolve accounts
    // through the exact same driver-key routing + tenant override dictionary instead of a
    // divergent hard-coded switch. Do NOT duplicate this logic — call these from both paths.

    /// <summary>F2 — the GL driver pinned on a line group by a tenant-configured pay component, or null for
    /// every system/subsystem line (which then routes exactly as before F2).</summary>
    internal static string? PinnedGlDriver(IEnumerable<string?> lineDriverKeys)
        => lineDriverKeys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));

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

    private sealed record PayrollGlReadiness(
        bool IsReady,
        decimal TotalDebits,
        decimal TotalCredits,
        decimal Difference,
        IReadOnlyList<string> Currencies,
        IReadOnlyList<Guid> UnmappedEntryIds,
        IReadOnlyList<Guid> EmptySidedEntryIds,
        IReadOnlyList<PayrollGlCompanyBalance> CompanyBalances)
    {
        /// <summary>Cost accounting only: expense / income lines on a Required account that carry no cost centre.</summary>
        public IReadOnlyList<Guid> UncostedRequiredEntryIds { get; init; } = Array.Empty<Guid>();
    }

    /// <summary>Debits and credits of one legal entity's slice of a payroll journal.</summary>
    private sealed record PayrollGlCompanyBalance(Guid? CompanyId, decimal Debits, decimal Credits, decimal Difference);

    /// <summary>
    /// The final, storage-shape-neutral gate before a payroll run may claim <c>ReadyForErp</c>.
    /// FinanceGlEntry contains both historical two-sided rows and current one-sided rows, so totals count
    /// every populated side. This is intentionally independent of the journal export/UI projections: the
    /// persisted accounting facts themselves must balance, use real mapped accounts, and carry exactly the
    /// legal entity's currency.
    /// </summary>
    private static PayrollGlReadiness ValidatePayrollGl(
        IReadOnlyCollection<FinanceGlEntry> lines, string companyCurrency, GlResolutionContext? costing = null)
    {
        // Cost accounting ON: ZERO tolerance, in the currency's minor unit, in total and per legal entity (contract 2.4, decision 5).
        // Costing OFF keeps the historical +-0.01 so a legacy accrual restored by support is judged exactly as before.
        var exact = costing is { CostAccountingEnabled: true };
        var scale = CurrencyMinorUnits.Scale(companyCurrency);
        var tolerance = exact ? 0m : 0.01m;
        decimal R(decimal v) => exact ? Math.Round(v, scale, MidpointRounding.AwayFromZero) : Math.Round(v, 2);
        static bool IsUnmapped(string account)
        {
            if (string.IsNullOrWhiteSpace(account)) return false; // absent on the other side is legitimate
            var (code, name) = SplitAccountLabel(account);
            return string.IsNullOrWhiteSpace(code)
                   || string.IsNullOrWhiteSpace(name)
                   || code.Equals("UNMAPPED", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Unmapped", StringComparison.OrdinalIgnoreCase);
        }

        var debits = R(lines.Where(l => !string.IsNullOrWhiteSpace(l.DebitAccount)).Sum(l => l.Amount));
        var credits = R(lines.Where(l => !string.IsNullOrWhiteSpace(l.CreditAccount)).Sum(l => l.Amount));
        var difference = Math.Abs(debits - credits);
        var currencies = lines
            .Select(l => (l.Currency ?? string.Empty).Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        var unmapped = lines
            .Where(l => IsUnmapped(l.DebitAccount) || IsUnmapped(l.CreditAccount))
            .Select(l => l.Id).Distinct().ToList();
        var emptySided = lines
            .Where(l => string.IsNullOrWhiteSpace(l.DebitAccount) && string.IsNullOrWhiteSpace(l.CreditAccount))
            .Select(l => l.Id).Distinct().ToList();
        var oneCompanyCurrency = currencies.Count == 1
            && !string.IsNullOrWhiteSpace(companyCurrency)
            && string.Equals(currencies[0], companyCurrency.Trim(), StringComparison.OrdinalIgnoreCase);
        // P1 — balance PER LEGAL ENTITY, not just in total. Bonus / settlement clearing lines carry the
        // company that holds the accrual they retire, which need not be the run's company; a journal can
        // then balance in aggregate while each entity's trial balance is off by the clearing amount.
        var companyBalances = lines
            .GroupBy(l => l.CompanyId)
            .Select(g =>
            {
                var dr = R(g.Where(l => !string.IsNullOrWhiteSpace(l.DebitAccount)).Sum(l => l.Amount));
                var cr = R(g.Where(l => !string.IsNullOrWhiteSpace(l.CreditAccount)).Sum(l => l.Amount));
                return new PayrollGlCompanyBalance(g.Key, dr, cr, Math.Abs(dr - cr));
            })
            .OrderBy(c => c.CompanyId?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToList();
        var everyCompanyBalanced = companyBalances.All(c => c.Difference <= tolerance);

        // A Required expense / income account whose line carries no cost centre is not finance-ready: it would export uncosted.
        var uncostedRequired = new List<Guid>();
        if (exact)
            foreach (var l in lines.Where(l => l.CostCenterId is null && l.ReversalOfEntryId is null))
                foreach (var label in new[] { l.DebitAccount, l.CreditAccount }.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    var code = SplitAccountLabel(label).Code;
                    if (costing!.AccountRules.TryGetValue(code, out var a) && a.Rule == "Required" && FinanceGlEntryGuard.IsPnl(a.AccountType))
                        uncostedRequired.Add(l.Id);
                }

        return new PayrollGlReadiness(
            lines.Count > 0 && difference <= tolerance && oneCompanyCurrency
                && unmapped.Count == 0 && emptySided.Count == 0 && everyCompanyBalanced && uncostedRequired.Count == 0,
            debits, credits, difference, currencies, unmapped, emptySided, companyBalances)
        {
            UncostedRequiredEntryIds = uncostedRequired.Distinct().ToList(),
        };
    }

    /// <summary>A pay item that reached the journal through a catch-all posting rule (EARN:OTHER →
    /// Other Earnings, DED:OTHER → Other Deductions) because no specific rule names it.</summary>
    private sealed record PayrollGlCatchAll(string ComponentCode, string Label, string DriverKey, decimal Amount);

    /// <summary>The run's accrual journal exactly as Lock would post it, plus what Lock must say about it.</summary>
    private sealed record PayrollLockJournal(
        List<FinanceGlEntry> Lines, decimal TotalDebits, decimal TotalCredits,
        IReadOnlyList<PayrollGlCatchAll> CatchAlls, IReadOnlyList<GlResolutionProblem> Problems,
        decimal RoundingAdjustment, IReadOnlyList<PayrollEarning> Earnings, IReadOnlyList<PayrollDeduction> Deductions)
    {
        /// <summary>POD-C2 — the per-employee obligation witnesses Lock persists beside the provision lines.</summary>
        public IReadOnlyList<EosbProvisionSnapshot> EosbSnapshots { get; init; } = Array.Empty<EosbProvisionSnapshot>();
        /// <summary>POD-C2 — set when provisioning is on but cannot be measured; Lock refuses rather than skip it.</summary>
        public EosbProvisionRefusal? EosbRefusal { get; init; }
        /// <summary>Cost accounting (WP-C2): the cost-centre decisions made in the SAME pass that built <see cref="Lines"/>.</summary>
        public PayrollCostPlan CostPlan { get; init; } = PayrollCostPlan.Off;
        /// <summary>The approval fingerprint of everything this journal depends on (null when costing is off).</summary>
        public string? CostHash { get; init; }
        /// <summary>M-4: a Required rounding account with no company default cost centre.</summary>
        public RoundingCostBlock? RoundingBlock { get; init; }
        public GlResolutionContext Context { get; init; } = GlResolutionContext.Empty;
    }

    /// <summary>One earning/deduction group and the posting rule it routes to.</summary>
    private sealed record RoutedGlGroup(
        string ComponentCode, string ComponentName, string DriverKey, bool Pinned, decimal Amount,
        bool IsEmployerSide, string PairKey);

    private static readonly HashSet<string> CatchAllDriverKeys = new(StringComparer.Ordinal) { "EARN:OTHER", "DED:OTHER" };

    /// <summary>
    /// The ONE place a payroll run's accrual journal is assembled — called by Lock (to post it) and by the
    /// GL preview (to show it), so the preview can never omit something the posting does. Before this the
    /// preview re-implemented the builder and had drifted: it applied the bonus clearing but not the
    /// settlement clearing, so a leaver's run previewed a second expense the posting never booked.
    /// </summary>
    private async Task<PayrollLockJournal> BuildLockJournalAsync(
        Guid tenantId, PayrollRun run, string period, string currency, Guid? postedBy, string postedByName,
        CancellationToken ct)
    {
        var id = run.Id;
        var earnings = await _db.PayrollEarnings.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PayrollRunId == id).ToListAsync(ct);
        var dedxns = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == id).ToListAsync(ct);
        var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, ct);
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
                _db, tenantId, id, run.CompanyId, bonusEarningTotal, ct),
            SettlementClearings = await FinalSettlementGlLedger.BuildPayrollClearingAsync(
                _db, tenantId, id, settlementEarningTotal, ct),
        };
        // Cost accounting (WP-C2): ONE resolution pass. The plan stamps the in-memory rows, and the builder below reads those same
        // lists — nothing is re-queried between the decision and the journal (AM-8, C-1). Costing off: no plan, nothing stamped.
        var costPlan = run.CompanyId is Guid costCompanyId
            ? await PayrollCostStamper.PlanAsync(_db, tenantId, costCompanyId, earnings, dedxns, glCtx, ct)
            : PayrollCostPlan.Off;
        PayrollCostStamper.Apply(costPlan, earnings, dedxns);
        if (costPlan.RoutingClasses.Count > 0) glCtx = glCtx with { RowRoutingClass = costPlan.RoutingClasses };
        var (lines, _, _) = BuildPayrollGlEntries(
            tenantId, id, period, earnings, dedxns, run.TotalNetSalary, postedBy, postedByName, currency, glCtx);

        // Which posting rules this journal actually uses — a switched-off account on a rule the run
        // never touches must not block the month.
        var routed = RoutePayrollGroups(earnings, dedxns, glCtx);
        var used = new HashSet<string>(routed.Select(r => r.DriverKey), StringComparer.Ordinal) { "NET_PAYABLE" };
        foreach (var r in routed.Where(r => r.IsEmployerSide)) used.Add(r.PairKey);

        var employeeCount = earnings.Select(e => e.EmployeeId).Concat(dedxns.Select(d => d.EmployeeId)).Distinct().Count();
        var (rounding, roundingBlock) = AppendRoundingLine(lines, employeeCount, glCtx, tenantId, id, period, currency, postedBy, postedByName);
        if (rounding != 0m) used.Add(PayrollRoundingDriverKey);

        // POD-C2 — the monthly end-of-service provision movement, when the company has switched it on.
        // Appended AFTER the rounding line: it balances on its own, so it can never move the rounding.
        var eosb = await BuildEosbProvisionAsync(tenantId, run, period, currency, glCtx, earnings, postedBy, postedByName, costPlan, ct);
        if (eosb.Lines.Count > 0)
        {
            lines.AddRange(eosb.Lines);
            used.Add(EosbProvisionExpenseDriver);
            used.Add(EosbProvisionLedger.ProvisionDriverKey);
        }

        // A switched-off SYSTEM rule blocks only when this run would have used it: route the run again
        // as if those rules were on, and any pay item (or employer pair) that lands on one of them is a
        // component that is being silently re-routed (usually to a catch-all) because it is off. A rule
        // nothing on this run matches must not stop the month.
        var wouldUseInactive = new HashSet<string>(StringComparer.Ordinal);
        if (glCtx.InactiveSystemDrivers.Count > 0)
        {
            var asIfOn = RoutePayrollGroups(earnings, dedxns,
                glCtx with { Drivers = glCtx.Drivers.Concat(glCtx.InactiveSystemDrivers).ToList() });
            foreach (var r in asIfOn)
            {
                wouldUseInactive.Add(r.DriverKey);
                if (r.IsEmployerSide) wouldUseInactive.Add(r.PairKey);
            }
        }
        var problems = glCtx.Problems.Values
            .Where(p => used.Contains(p.DriverKey)
                     || (p.Kind == GlResolutionProblemKinds.InactiveDriver && wouldUseInactive.Contains(p.DriverKey)))
            .OrderBy(p => p.DriverKey, StringComparer.Ordinal)
            .ToList();
        var catchAlls = routed
            .Where(r => !r.Pinned && CatchAllDriverKeys.Contains(r.DriverKey))
            .Select(r => new PayrollGlCatchAll(r.ComponentCode, r.ComponentName, r.DriverKey, r.Amount))
            .OrderBy(c => c.DriverKey, StringComparer.Ordinal).ThenBy(c => c.ComponentCode, StringComparer.Ordinal)
            .ToList();

        var totalDebits = lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var totalCredits = lines.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        string? costHash = null;
        if (costPlan.Enabled)
        {
            var accountMasters = await PayrollCostStamper.LoadAccountMastersAsync(_db, tenantId, run.CompanyId!.Value, ct);
            var extra = problems.Select(p => $"problem:{p.DriverKey}:{p.Kind}")
                .Concat(new[] { $"rounding:{(roundingBlock is null ? "ok" : "blocked")}", $"eosb:{eosb.Refusal?.Code ?? "ok"}" });
            costHash = PayrollCostStamper.ComputeHash(costPlan, lines, glCtx, accountMasters, extra);
        }
        return new PayrollLockJournal(lines, totalDebits, totalCredits, catchAlls, problems, rounding, earnings, dedxns)
        {
            EosbSnapshots = eosb.Snapshots,
            EosbRefusal = eosb.Refusal,
            CostPlan = costPlan, CostHash = costHash, RoundingBlock = roundingBlock, Context = glCtx,
        };
    }

    /// <summary>The routing every earning/deduction group takes — the same calls BuildPayrollGlEntries makes.</summary>
    private static List<RoutedGlGroup> RoutePayrollGroups(
        IEnumerable<PayrollEarning> earnings, IEnumerable<PayrollDeduction> deductions, GlResolutionContext gl)
    {
        var routed = new List<RoutedGlGroup>();
        foreach (var grp in earnings.GroupBy(e => e.ComponentCode))
        {
            var first = grp.First();
            var pinned = PinnedGlDriver(grp.Select(e => e.GlDriverKey));
            routed.Add(new RoutedGlGroup(grp.Key, first.ComponentName,
                pinned ?? EarningDriverKeyFor(gl.Drivers, grp.Key, first.Source), pinned is not null,
                grp.Sum(e => e.Amount), false, string.Empty));
        }
        foreach (var grp in deductions.GroupBy(d => new { d.ComponentCode, d.Source }))
        {
            var pinned = PinnedGlDriver(grp.Select(d => d.GlDriverKey));
            var (driver, isEmployerSide, pairKey) = ResolveDeductionRouting(gl.Drivers, grp.Key.ComponentCode, grp.Key.Source, pinned);
            routed.Add(new RoutedGlGroup(grp.Key.ComponentCode, grp.First().ComponentName, driver, pinned is not null,
                grp.Sum(d => d.Amount), isEmployerSide, pairKey));
        }
        return routed;
    }

    /// <summary>
    /// Deduction group → (driver, employer-side?, paired expense driver). A pay component's pinned driver
    /// wins; then the persisted driver store; then the compiled switch. Shared by the posting builder, the
    /// routing analysis and the GL resolution API.
    /// </summary>
    internal static (string Driver, bool IsEmployerSide, string PairKey) ResolveDeductionRouting(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source, string? pinned)
    {
        // F2 — a tenant-configured component posts to the driver validated when it was written and
        // pinned on the line at Process. The write API refuses statutory / employer-pair / control
        // drivers, so this is never an employer-side pair.
        if (pinned is not null) return (pinned, false, "EMPLOYER_STATUTORY_EXPENSE");
        var row = ResolveDeductionDriverRow(drivers, componentCode, source);
        if (row is not null)
            return (row.Key, row.EmitsEmployerExpensePair, row.PairedExpenseDriverKey ?? "EMPLOYER_STATUTORY_EXPENSE");
        var key = DeductionDriverKey(componentCode, source, out var isEmployerSide);
        return (key, isEmployerSide, "EMPLOYER_STATUTORY_EXPENSE");
    }

    /// <summary>Posting driver that books the Lock journal's sub-unit rounding difference.</summary>
    internal const string PayrollRoundingDriverKey = "ROUNDING";

    /// <summary>
    /// P1 — make the STORED journal balance exactly. Lock used to accept |DR − CR| ≤ 0.01 and persist the
    /// journal that far out, so every downstream trial balance inherited an unexplained halala. A
    /// difference of up to one halala per employee (capped at 1.00) is the arithmetic residue of rounding
    /// each line independently; it is now booked explicitly to Payroll Rounding Differences. Anything
    /// larger is a real error and is left for ValidatePayrollGl to block, exactly as before.
    /// Returns the amount posted to the rounding account (positive = debit, negative = credit, 0 = none).
    /// </summary>
    private static (decimal Adjustment, RoundingCostBlock? Block) AppendRoundingLine(
        List<FinanceGlEntry> lines, int employeeCount, GlResolutionContext gl,
        Guid tenantId, Guid runId, string period, string currency, Guid? postedBy, string postedByName)
    {
        var debits = Math.Round(lines.Where(l => !string.IsNullOrWhiteSpace(l.DebitAccount)).Sum(l => l.Amount), 2);
        var credits = Math.Round(lines.Where(l => !string.IsNullOrWhiteSpace(l.CreditAccount)).Sum(l => l.Amount), 2);
        var difference = debits - credits;
        if (difference == 0m || lines.Count == 0) return (0m, null);
        var tolerance = Math.Min(1.00m, 0.01m * Math.Max(1, employeeCount));
        if (Math.Abs(difference) > tolerance) return (0m, null);
        var account = GlAccountResolver.AccountLabel(PayrollRoundingDriverKey, gl);

        // Cost accounting: a genuine rounding difference is an Expense line, so it lands on the company default cost centre
        // (the home of the rounding line). A Required rounding account with no usable default cannot post (M-4).
        Guid? roundingCc = null;
        if (gl.CostAccountingEnabled)
        {
            var (code, name) = GlAccountResolver.Resolve(PayrollRoundingDriverKey, gl.Overrides, gl.DriverDefaults);
            var rule = PayrollCostStamper.RuleOf(code, gl);
            if (rule != "NotAllowed" && PayrollCostStamper.IsCostableDriver(PayrollRoundingDriverKey, gl))
            {
                if (gl.CompanyDefaultCostCentreId is Guid d && gl.CostCentres.TryGetValue(d, out var dcc) && dcc.IsActive) roundingCc = d;
                else if (rule == "Required") return (0m, new RoundingCostBlock(code, name, Math.Abs(difference)));
            }
        }
        var line = new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
            SourceEntityRef = period, EventType = GlEventTypes.Accrual,
            // Debits exceed credits ⇒ the rounding account takes the missing CREDIT, and vice versa.
            DebitAccount  = difference < 0m ? account : string.Empty,
            CreditAccount = difference > 0m ? account : string.Empty,
            Amount = Math.Abs(difference), Currency = currency,
            EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = period,
            Description = PayrollGlDescriptions.Rounding,
            PostedBy = postedBy, PostedByName = postedByName,
        };
        if (gl.CostAccountingEnabled)
        {
            if (roundingCc is Guid rc && gl.CostCentres.TryGetValue(rc, out var info))
            {
                line.CostCenterId = rc; line.CostCenterCode = info.Code; line.CostCenterName = info.Name; line.CostCenterGlSegment = info.GlSegment;
            }
            line.PostingKey = PayrollCostStamper.PostingKey(runId, GlEventTypes.Accrual, period,
                difference < 0m ? "DR" : "CR", PayrollRoundingDriverKey, PayrollRoundingDriverKey, "Rounding", roundingCc, 0);
        }
        lines.Add(line);
        return (-difference, null);
    }

    /// <summary>M-4: the rounding difference the journal needs cannot be costed (Required account, no usable company default).</summary>
    private sealed record RoundingCostBlock(string AccountCode, string AccountName, decimal Amount);

    /// <summary>
    /// The posting driver a component code routes to, for the GL resolution API — the same calls the
    /// posting builder makes (no pinned driver: that is a per-line fact, reported from pay components).
    /// </summary>
    internal static string RouteComponentToDriverKey(
        IReadOnlyList<GlDriverRow> drivers, string componentCode, string source, bool isDeduction)
        => isDeduction
            ? ResolveDeductionRouting(drivers, componentCode, source, null).Driver
            : EarningDriverKeyFor(drivers, componentCode, source);

    /// <summary>The component codes (with the Source Process stamps on them) that the payroll engine emits
    /// itself, so the GL resolution API can say which known pay items each posting rule covers.</summary>
    internal static readonly IReadOnlyList<(string Code, string Source, bool IsDeduction)> KnownRoutedComponents = new[]
    {
        ("BASIC", "Salary", false), ("HOUSING", "Salary", false), ("TRANSPORT", "Salary", false),
        ("OTHER_ALLOWANCES", "Salary", false), ("OVERTIME", "Overtime", false),
        (PayrollArrearsComponents.Basic, "Arrears", false), (PayrollArrearsComponents.Housing, "Arrears", false),
        (PayrollArrearsComponents.Transport, "Arrears", false), (PayrollArrearsComponents.OtherAllowances, "Arrears", false),
        (FinalSettlementComponents.Gratuity, FinalSettlementComponents.SettlementSource, false),
        (FinalSettlementComponents.LeaveEncashment, FinalSettlementComponents.SettlementSource, false),
        (FinalSettlementComponents.NoticePay, FinalSettlementComponents.SettlementSource, false),
        (FinalSettlementComponents.OtherDues, FinalSettlementComponents.SettlementSource, false),
        ("FIXED_DEDUCTION", "Salary", true), ("INCOME_TAX", "Tax", true),
        (BonusGlDescriptions.PayrollTaxComponentCode, "Tax", true),
        ("ATTENDANCE", "Attendance", true), ("LOP_DEDUCTION", "Attendance", true), ("LEAVE", "Leave", true),
        ("LOAN_EMI", "Loan", true), ("ADVANCE_EMI", "Loan", true),
        (PayrollRecoveryComponents.ReceivableRecovery, PayrollRecoveryComponents.RecoverySource, true),
        (FinalSettlementComponents.NoticeShortfall, FinalSettlementComponents.SettlementSource, true),
        (FinalSettlementComponents.OtherDeduction, FinalSettlementComponents.SettlementSource, true),
        ("GOSI-ANN-EE", "Statutory", true), ("GOSI-SANED-EE", "Statutory", true),
        ("GOSI-ANN-ER", "Statutory", true), ("GOSI-SANED-ER", "Statutory", true), ("GOSI-OH-ER", "Statutory", true),
    };

    /// <summary>(componentCode, componentName) for a journal line built by BuildLockJournalAsync — the
    /// labels the GL preview has always shown, derived from the line itself so preview == post.</summary>
    private static (string Code, string Name) DescribePreviewLine(
        FinanceGlEntry line, IReadOnlyDictionary<string, string> componentNames)
    {
        if (line.EventType == GlEventTypes.BonusPayrollClearing) return ("BONUS_PAYABLE", line.Description);
        if (line.EventType == GlEventTypes.SettlementPayrollClearing) return ("SETTLEMENT_PAYABLE", line.Description);
        if (line.EventType == GlEventTypes.EosbProvisionAccrual) return ("EOSB_PROVISION", line.Description);
        if (line.Description == PayrollGlDescriptions.NetPayable) return ("NET_SALARY", "Net Salary Payable");
        if (line.Description == PayrollGlDescriptions.EmployerStatutory) return ("SOCIAL_INS_ER_DR", "Employer Social Insurance Expense");
        if (line.Description == PayrollGlDescriptions.Rounding) return ("ROUNDING", "Payroll rounding difference");
        const string earnPrefix = "Payroll earning: ";
        const string unaccrued = " (un-accrued)";
        string? code = line.Description.StartsWith(earnPrefix, StringComparison.Ordinal) ? line.Description[earnPrefix.Length..]
            : line.Description.StartsWith(PayrollGlDescriptions.DeductionPrefix, StringComparison.Ordinal) ? line.Description[PayrollGlDescriptions.DeductionPrefix.Length..]
            : null;
        if (code is null) return (string.Empty, line.Description);
        var isUnaccrued = code.EndsWith(unaccrued, StringComparison.Ordinal);
        if (isUnaccrued) code = code[..^unaccrued.Length];
        var name = componentNames.TryGetValue(code, out var n) ? n : code;
        return (code, isUnaccrued ? $"{name}{unaccrued}" : name);
    }

    // Builds the double-entry GL lines for a payroll run.
    // Uses Source-based routing so new pack codes (GOSI-ANN-EE, GPSSA-EE, etc.) map correctly
    // without requiring changes to the component code dictionary as new packs are added.
    // Returns: (lines, totalDebits, totalCredits).
    //
    // Cost accounting (WP-C2): with gl.CostAccountingEnabled the expense side is split per cost centre — the rows carry the
    // stamps PayrollCostStamper wrote — and every line gets a posting key; liability / provision / receivable lines stay uncosted.
    // With it OFF the rows carry no stamp, so every group collapses to the single line it always was and no key is written.
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
        var costed = gl.CostAccountingEnabled;
        var keyOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        // The bonus / settlement remainder arithmetic below has always rounded to 2 dp. Costing OFF keeps that exactly; costing ON uses the
        // currency's minor unit, so a 3-dp ledger (KWD/BHD/OMR) cannot lose a fils (T-032).
        var dp = costed ? CurrencyMinorUnits.Scale(currency) : 2;

        // Resolve a posting driver to "<code> - <name>": mapping override → persisted driver default →
        // compiled default. Delegates to the shared ResolveGlAccount so posting and the GL Journal
        // preview stay identical.
        string Account(string driverKey)
        {
            var (code, name) = ResolveGlAccount(driverKey, gl.Overrides, gl.DriverDefaults);
            return $"{code} - {name}";
        }
        // The same, routed by the functional class of the cost centre the line lands on (precedence in
        // GlAccountResolver.AccountFor). With no cost centre or no class rows this is exactly Account().
        string AccountAt(string driverKey, Guid? ccId, string? routedClass = null)
        {
            var (code, name) = GlAccountResolver.AccountFor(driverKey, routedClass ?? PayrollCostStamper.ClassOf(ccId, gl), gl);
            return $"{code} - {name}";
        }
        Guid? CcOf(Guid? stamped) => costed ? stamped : null;
        // A row's destination is decided by (stamped cost centre, routing class). The class is only carried separately when the stamp
        // was suppressed (the routed account takes no cost centre), so dropping the dimension never moves the amount to another account.
        (Guid? Cc, string? Cls) RouteOf(Guid rowId, Guid? stamped) =>
            (CcOf(stamped), costed && stamped is null && gl.RowRoutingClass.TryGetValue(rowId, out var cls) ? cls : null);
        string RouteSort((Guid? Cc, string? Cls) r) => CcCode(r.Cc) + "|" + (r.Cls ?? string.Empty);
        string CcCode(Guid? id) => id is Guid g && gl.CostCentres.TryGetValue(g, out var c) ? c.Code : string.Empty;
        FinanceGlEntry At(FinanceGlEntry line, Guid? ccId, string driverKey, string componentCode, string source)
        {
            if (!costed) return line;
            if (ccId is Guid g && gl.CostCentres.TryGetValue(g, out var info))
            {
                line.CostCenterId = g; line.CostCenterCode = info.Code; line.CostCenterName = info.Name; line.CostCenterGlSegment = info.GlSegment;
            }
            var side = string.IsNullOrEmpty(line.DebitAccount) ? "CR" : "DR";
            var basis = string.Join('|', line.EventType, line.SourceEntityRef, side, driverKey, componentCode, source, ccId?.ToString("N") ?? "-");
            var ordinal = keyOrdinals.GetValueOrDefault(basis);
            keyOrdinals[basis] = ordinal + 1;
            line.PostingKey = PayrollCostStamper.PostingKey(runId, line.EventType, line.SourceEntityRef, side, driverKey, componentCode, source, ccId, ordinal);
            return line;
        }

        // Splits a total over the cost centres of the rows behind it by LARGEST REMAINDER on the currency's minor units, so the
        // parts add up to the total to the last halala (contract 2.6). Weights are each cost centre's rows in minor units.
        List<(Guid? CcId, string? Cls, decimal Amount)> Split(decimal total, IEnumerable<PayrollEarning> rows)
        {
            if (!costed) return new() { (null, null, total) };
            // The allocation key is the cost-centre code alone unless a routing class rides with it (null-stamp rows only), so the
            // order and tie-breaks of every existing split are unchanged.
            string KeyOf((Guid? Cc, string? Cls) r) => r.Cls is null ? CcCode(r.Cc) : CcCode(r.Cc) + "|" + r.Cls;
            var byCc = rows.GroupBy(r => RouteOf(r.Id, r.CostCenterId))
                .Select(g => (Route: g.Key, Code: KeyOf(g.Key), Weight: g.Sum(r => CurrencyMinorUnits.ToMinor(r.Amount, currency))))
                .ToList();
            var parts = LargestRemainder.Allocate(CurrencyMinorUnits.ToMinor(total, currency), byCc.Select(c => (c.Code, Math.Max(0L, c.Weight))).ToList());
            var result = new List<(Guid?, string?, decimal)>();
            foreach (var (code, minor) in parts)
            {
                if (minor == 0) continue;
                var route = byCc.FirstOrDefault(c => c.Code == (code ?? string.Empty)).Route;
                result.Add((route.Cc, route.Cls, CurrencyMinorUnits.FromMinor(minor, currency)));
            }
            return result.Count == 0 ? new() { (null, null, total) } : result;
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
        var deferredBonusGroups = new List<(string Code, string Driver, decimal Amount, List<PayrollEarning> Rows)>();
        // POD-C1 — a settlement's earnings were ALREADY expensed when the settlement was approved
        // (DR 5110/5111/5112 / CR 2320). This run PAYS it, so the correct debit is the payable's own
        // stored account, not a second expense. Identical doctrine to the bonus clearing above; the plan
        // is capped by the outstanding payable AND by the run's own settlement earning total, so a
        // settlement injected into a run with no accrual is still expensed here and the journal always
        // balances.
        var settlementClearings = gl.SettlementClearings;
        var clearsSettlement = settlementClearings.Count > 0;
        decimal settlementEarningTotal = 0m;
        var deferredSettlementGroups = new List<(string Code, string Driver, decimal Amount, List<PayrollEarning> Rows)>();
        foreach (var grp in earnings.GroupBy(e => e.ComponentCode))
        {
            var src = grp.First().Source;
            var driverKey = PinnedGlDriver(grp.Select(e => e.GlDriverKey)) ?? EarningDriverKeyFor(gl.Drivers, grp.Key, src);
            if (clearsSettlement && src == FinalSettlementComponents.SettlementSource)
            {
                var settlementAmount = grp.Sum(e => e.Amount);
                settlementEarningTotal += settlementAmount;
                deferredSettlementGroups.Add((grp.Key, driverKey, settlementAmount, grp.ToList()));
                continue;   // re-emitted below as payable clearing + un-accrued remainder
            }
            if (clearsBonusAccrual && src == "Bonus")
            {
                var bonusAmount = grp.Sum(e => e.Amount);
                bonusEarningTotal += bonusAmount;
                deferredBonusGroups.Add((grp.Key, driverKey, bonusAmount, grp.ToList()));
                continue;   // re-emitted below as accrual clearing + per-component un-accrued remainder
            }
            var driver = driverKey;
            foreach (var sub in grp.GroupBy(e => RouteOf(e.Id, e.CostCenterId)).OrderBy(g => RouteSort(g.Key), StringComparer.Ordinal))
                lines.Add(At(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                    SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                    DebitAccount  = AccountAt(driver, sub.Key.Cc, sub.Key.Cls), CreditAccount = string.Empty,
                    Amount = sub.Sum(e => e.Amount), Currency = currency,
                    EntryDate = today, Period = period,
                    Description = $"Payroll earning: {grp.Key}",
                    PostedBy = postedBy, PostedByName = postedByName,
                }, sub.Key.Cc, driver, grp.Key, src));
        }

        // One un-accrued remainder slice of a deferred (bonus / settlement) component: a single line, or — costing on — one per cost
        // centre, divided by largest remainder over the employees' amounts so the parts add up to the slice exactly.
        void EmitUnaccrued(string code, string driver, string src, decimal share, IReadOnlyList<PayrollEarning> rows)
        {
            foreach (var (ccId, cls, amount) in Split(share, rows))
                lines.Add(At(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                    SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                    DebitAccount = AccountAt(driver, ccId, cls), CreditAccount = string.Empty,
                    Amount = amount, Currency = currency,
                    EntryDate = today, Period = period,
                    Description = $"Payroll earning: {code} (un-accrued)",
                    PostedBy = postedBy, PostedByName = postedByName,
                }, ccId, driver, code, src));
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
                lines.Add(At(new FinanceGlEntry
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
                }, null, "BONUS_PAYABLE", "BONUS_PAYABLE", "Bonus"));
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
            var remainder = Math.Round(bonusEarningTotal - cleared, dp);
            if (remainder > 0m && deferredBonusGroups.Count > 0 && bonusEarningTotal > 0m)
            {
                var ordered = deferredBonusGroups.OrderBy(g => g.Code, StringComparer.Ordinal).ToList();
                var shortfall = ordered
                    .Select(g => Math.Max(0m, Math.Round(g.Amount - clearedByCode.GetValueOrDefault(g.Code), dp)))
                    .ToList();
                var useShortfall = shortfall.Sum() > 0m;
                var weights = useShortfall ? shortfall : ordered.Select(g => g.Amount).ToList();
                var weightTotal = weights.Sum();
                var slice = remainder;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var share = i == ordered.Count - 1
                        ? slice
                        : Math.Min(slice, Math.Round(remainder * (weights[i] / weightTotal), dp));
                    slice = Math.Round(slice - share, dp);
                    if (share <= 0m) continue;
                    EmitUnaccrued(ordered[i].Code, ordered[i].Driver, "Bonus", share, ordered[i].Rows);
                }
            }
            else if (remainder > 0m)
            {
                // Defensive: a clearing plan with no bonus earning lines behind it. Keep the pre-B1b-FIX
                // single EARN:BONUS line so the journal can never come out unbalanced.
                lines.Add(At(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                    SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                    DebitAccount = Account("EARN:BONUS"), CreditAccount = string.Empty,
                    Amount = remainder, Currency = currency,
                    EntryDate = today, Period = period,
                    Description = "Payroll earning: BONUS (un-accrued)",
                    PostedBy = postedBy, PostedByName = postedByName,
                }, null, "EARN:BONUS", "BONUS", "Bonus"));
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
                lines.Add(At(new FinanceGlEntry
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
                }, null, "SETTLEMENT_PAYABLE", "SETTLEMENT_PAYABLE", FinalSettlementComponents.SettlementSource));
            }
            // Whatever the run pays that no live payable covers is a genuine expense of this run, debited
            // to each component's OWN driver (never one collapsed line), so a settlement paid without an
            // accrual — a seeder, a legacy fixture — behaves exactly like an ordinary earning.
            var settlementRemainder = Math.Round(settlementEarningTotal - clearedSettlement, dp);
            if (settlementRemainder > 0m && deferredSettlementGroups.Count > 0 && settlementEarningTotal > 0m)
            {
                var ordered = deferredSettlementGroups.OrderBy(g => g.Code, StringComparer.Ordinal).ToList();
                var slice = settlementRemainder;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var share = i == ordered.Count - 1
                        ? slice
                        : Math.Min(slice, Math.Round(settlementRemainder * (ordered[i].Amount / settlementEarningTotal), dp));
                    slice = Math.Round(slice - share, dp);
                    if (share <= 0m) continue;
                    EmitUnaccrued(ordered[i].Code, ordered[i].Driver, FinalSettlementComponents.SettlementSource, share, ordered[i].Rows);
                }
            }
        }

        // ── Deductions (Credit side) ──────────────────────────────────────────
        // Employer-expense pairs accumulate per paired-expense driver key (and cost centre) so a client-defined pair
        // (via a custom driver) balances the same way the system DED:STATUTORY_ER row does.
        var employerExpenseByPair = new Dictionary<(string PairKey, Guid? CcId, string? Cls), decimal>();
        foreach (var grp in deductions.GroupBy(d => new { d.ComponentCode, d.Source }))
        {
            // Pinned driver → persisted driver store → compiled switch (see ResolveDeductionRouting).
            var (driver, isEmployerSide, pairKey) = ResolveDeductionRouting(
                gl.Drivers, grp.Key.ComponentCode, grp.Key.Source, PinnedGlDriver(grp.Select(d => d.GlDriverKey)));
            if (isEmployerSide)
                foreach (var sub in grp.GroupBy(d => RouteOf(d.Id, d.CostCenterId)))
                    employerExpenseByPair[(pairKey, sub.Key.Cc, sub.Key.Cls)] = employerExpenseByPair.GetValueOrDefault((pairKey, sub.Key.Cc, sub.Key.Cls)) + sub.Sum(d => d.Amount);

            // A liability credit (and every employer-side credit) is one UNCOSTED line; a contra-EXPENSE credit
            // (attendance, leave, settlement recovery) is a cost reduction and carries the cost centre.
            var creditCosted = costed && !isEmployerSide && PayrollCostStamper.IsCostableDriver(driver, gl);
            foreach (var sub in creditCosted ? grp.GroupBy(d => RouteOf(d.Id, d.CostCenterId)).OrderBy(g => RouteSort(g.Key), StringComparer.Ordinal).ToList()
                                              : new[] { grp.GroupBy(_ => ((Guid?)null, (string?)null)).Single() }.ToList())
                lines.Add(At(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                    SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                    DebitAccount = string.Empty, CreditAccount = creditCosted ? AccountAt(driver, sub.Key.Item1, sub.Key.Item2) : Account(driver),
                    Amount = sub.Sum(d => d.Amount), Currency = currency,
                    EntryDate = today, Period = period,
                    Description = $"{PayrollGlDescriptions.DeductionPrefix}{grp.Key.ComponentCode}",
                    PostedBy = postedBy, PostedByName = postedByName,
                }, sub.Key.Item1, driver, grp.Key.ComponentCode, grp.Key.Source));
        }

        // Employer statutory contribution: DR expense to balance the CR liability above.
        foreach (var ((pairKey, ccId, cls), amount) in employerExpenseByPair
                     .OrderBy(kv => kv.Key.PairKey, StringComparer.Ordinal).ThenBy(kv => RouteSort((kv.Key.CcId, kv.Key.Cls)), StringComparer.Ordinal))
        {
            if (amount <= 0) continue;
            lines.Add(At(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                DebitAccount = AccountAt(pairKey, ccId, cls), CreditAccount = string.Empty,
                Amount = amount, Currency = currency,
                EntryDate = today, Period = period,
                Description = PayrollGlDescriptions.EmployerStatutory,
                PostedBy = postedBy, PostedByName = postedByName,
            }, ccId, pairKey, "EMPLOYER_STATUTORY", "Statutory"));
        }

        // Net salary payable balances all DR earnings vs. CR deductions.
        lines.Add(At(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = gl.CompanyId, SourceModule = "Payroll", SourceEntityId = runId,
            SourceEntityRef = period, EventType = GlEventTypes.Accrual,
            DebitAccount = string.Empty, CreditAccount = Account("NET_PAYABLE"),
            Amount = totalNetSalary, Currency = currency,
            EntryDate = today, Period = period,
            Description = PayrollGlDescriptions.NetPayable,
            PostedBy = postedBy, PostedByName = postedByName,
        }, null, "NET_PAYABLE", "NET_PAYABLE", "Net"));

        var totalDebits  = lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var totalCredits = lines.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        return (lines, totalDebits, totalCredits);
    }

    // M1: audit log now captures caller IP and structured metadata
    /// <summary>
    /// The establishment id a wage file carries (rules in <see cref="Infrastructure.Payroll.SaudiBankExports.WpsEstablishmentAuthority"/>).
    /// The company's own MOL establishment number (<c>Company.WpsEmployerId</c>), when recorded, is the authority and any
    /// other recorded id that differs refuses the file. Without it, today's rule: KSA: the MOL establishment id saved in the Saudi bank-file
    /// settings (the id the bank instruction itself carries) is preferred over the GCC WpsAgentId; when
    /// both are set and differ the export is refused, because two files for one month would name two
    /// establishments. Other countries, and KSA without bank-file settings: the legal entity's own GCC
    /// compliance row, else the tenant-wide row. Null when none is recorded — the caller refuses.
    /// </summary>
    private async Task<(string? Id, Infrastructure.Payroll.SaudiBankExports.SaudiBankExportIssueDto? Conflict)> ResolveWpsEstablishmentIdAsync(
        Guid tenantId, Guid? companyId, bool isKsa, CancellationToken ct)
    {
        var rows = await _db.GCCComplianceSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && (x.CompanyId == null || x.CompanyId == companyId))
            .Select(x => new { x.CompanyId, x.WpsAgentId })
            .ToListAsync(ct);
        var ownAgentId = rows.FirstOrDefault(x => companyId != null && x.CompanyId == companyId && !string.IsNullOrWhiteSpace(x.WpsAgentId))?.WpsAgentId;
        var tenantAgentId = rows.FirstOrDefault(x => x.CompanyId == null && !string.IsNullOrWhiteSpace(x.WpsAgentId))?.WpsAgentId;
        // Decision 10: the company's own MOL establishment number is the authority when it is recorded.
        var companyValue = companyId is Guid id
            ? await _db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == id).Select(c => c.WpsEmployerId).FirstOrDefaultAsync(ct)
            : null;
        // A recorded company number is checked only against ids recorded for THIS company. The tenant-wide row is a
        // fallback for companies with no number of their own: in a group every legal entity has its own establishment,
        // so the workspace default must not refuse a sister company's file.
        var agentId = string.IsNullOrWhiteSpace(companyValue) ? ownAgentId ?? tenantAgentId : ownAgentId;
        string? mol = null;
        if (isKsa && companyId is Guid c)
            mol = (await new Infrastructure.Payroll.SaudiBankExports.SaudiBankExportService(_db).GetSettingsAsync(tenantId, c, ct)).MolEstablishmentId;
        return Infrastructure.Payroll.SaudiBankExports.WpsEstablishmentAuthority.Resolve(companyValue, mol, agentId, isKsa && companyId is not null);
    }

    /// <summary>Null when the id is usable. KSA applies the [MOL-ESTBID] rule; every country refuses a
    /// missing or all-zero id.</summary>
    private static Infrastructure.Payroll.SaudiBankExports.SaudiBankExportIssueDto? WpsEstablishmentError(string? id, bool isKsa)
    {
        if (isKsa)
        {
            var errors = new List<Infrastructure.Payroll.SaudiBankExports.SaudiBankExportIssueDto>();
            Infrastructure.Payroll.SaudiBankExports.KsaWageFileRules.ValidateEstablishmentId(id, errors);
            return errors.FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(id) || id.Where(char.IsLetterOrDigit).All(ch => ch == '0'))
            return new("wps_employer_id_missing",
                "Enter the WPS employer / establishment ID in Setup \u2192 Compliance before creating a wage file. No placeholder is used.",
                null, "wpsAgentId");
        return null;
    }

    private async Task PayrollAudit(string action, string entity, string entityId, object? metadata, CancellationToken ct,
        Guid? auditId = null)
    {
        var ip = _http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var meta = new { ip, userId = GetUserId()?.ToString(), data = metadata };
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            // A caller-chosen id lets a transaction verify its own commit (see MutateSettlementAsync).
            Id = auditId ?? Guid.NewGuid(),
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

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with. This is
    /// the widest template in the product (20 columns) and the one where a hand-written row drifting out
    /// of step with the header would be hardest to spot by eye. The two Eligible* columns are blank
    /// because they are cross-references to grades/designations the tenant may not have imported yet.</summary>
    private static readonly string[] SalaryStructureCsvExampleRow =
        {
            "Example Company Ltd", "STD", "Standard Structure", "SAR", "2026-01-01", "true",
            "0", "0", "0", "0",
            "", "",
            "BASIC", "Basic Salary", "Earning", "Fixed",
            "8000", "0", "false", "true"
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
        return CsvDownload.Result(csv);
    }

    [HttpGet("structures/import-template")]
    [HttpGet("salary-structures/import-template")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public IActionResult StructuresImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=salary_structures_import_template.csv";
        return CsvDownload.Result(Csv.Template(SalaryStructureCsvHeaders, SalaryStructureCsvExampleRow));
    }

    [HttpPost("structures/import")]
    [HttpPost("salary-structures/import")]
    [HasPermission("payroll.write")]
    public IActionResult ImportStructures([FromBody] ImportSalaryStructuresRequest req) => Conflict(SalaryStructuresFollowGradesBody);

    /// <summary>
    /// F10 — who may read a run's per-employee reconciliation (the mismatch report and the
    /// month-over-month reconciliation). Both used to require <c>payroll.review</c>, a permission that is
    /// not in the catalog, so no role — not even Admin — could open them. They are read-only views derived
    /// from the run's slips and the salary assignments, so they take the permission that already governs
    /// those reads: <c>payroll.read</c>. The slip, register and salary-assignment GETs carry legacy role
    /// gates, but the permission-aware role handler admits any caller holding <c>payroll.read</c> to them
    /// (LegacyRolePermissionResolver maps a Payroll GET to payroll.read), so this grants nobody a figure
    /// they cannot already read: Admin, HR Director, HR Manager, Payroll Manager, Payroll Officer, Finance
    /// Approver (who needs the variance to approve a run) and the Auditor.
    /// </summary>
    private bool CanReviewRunPayDetail() => HasPermission("payroll.read");

    /// <summary>
    /// A run's slips, limited to the employees the caller may see. Organization-level callers see the
    /// whole run (the run itself is already company-filtered); a team- or department-scoped caller — only
    /// possible through a custom role — sees their own people's rows, and the totals built from them.
    /// </summary>
    private async Task<List<PayrollSlip>> VisibleRunSlipsAsync(Guid tenantId, Guid runId, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == runId);
        if (scope.Level != DataScopeLevel.Organization && scope.AllowedEmployeeIds is { } allowed)
            query = query.Where(x => allowed.Contains(x.EmployeeId));
        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// Per-employee reconciliation between contract salary and the processed payroll
    /// slip, plus WPS/GOSI/QIWA readiness flags.  Variance &gt; 5% is flagged as a warning.
    /// </summary>
    [HttpGet("runs/{id:guid}/mismatch-report")]
    public async Task<IActionResult> MismatchReport(Guid id, CancellationToken cancellationToken)
    {
        if (!CanReviewRunPayDetail()) return Forbid();

        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var slips = await VisibleRunSlipsAsync(tenantId, id, cancellationToken);
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
        if (!CanReviewRunPayDetail()) return Forbid();
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

        var currentSlips = await VisibleRunSlipsAsync(tenantId, runId, cancellationToken);
        var priorSlips   = priorRun is not null ? await VisibleRunSlipsAsync(tenantId, priorRun.Id, cancellationToken) : new List<PayrollSlip>();

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
    /// Cost-centre payroll allocation — aggregates a run's payroll cost by cost centre (contract 2.10, decision 3, AM-17, D-7).
    /// <list type="bullet">
    /// <item><b>Stamped</b> (locked run costed at Lock): gross from the stamped earnings, contra-expense deductions and employer statutory cost from
    /// the stamped deduction rows, headcount and net from the slips' stamped cost centre, names from the journal-line SNAPSHOTS. Nothing
    /// reads the live employee or cost-centre rows, so a later rename, transfer or department change cannot move the report.</item>
    /// <item><b>NotRecorded</b> (locked before cost centres existed): one honest bucket. Never the live employee row.</item>
    /// <item><b>Preview</b> (any run not yet locked): a dry-run resolution, labelled so.</item>
    /// </list>
    /// For a stamped run it also returns <c>journalNet</c> per cost centre (the run's own profit-and-loss ledger lines),
    /// <c>clearedAgainstAccrual</c> (bonus and settlement amounts the payroll cleared against an earlier accrual instead of expensing again) and a top-level
    /// <c>reconciles</c>: per cost centre <c>journalNet == earnings − contra deductions + employer statutory − clearedAgainstAccrual (+ rounding line)</c> (T-025).
    /// Needs <c>finance.gl.read</c> and company scope (S-038).
    /// </summary>
    [HttpGet("runs/{id:guid}/cost-center-allocation")]
    [HasPermission("finance.gl.read")]
    public async Task<IActionResult> CostCenterAllocation(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, cancellationToken);
        if (run is null) return NotFound();
        if (run.CompanyId is Guid runCompany && !this.GetEntityScope().CanAccessCompany(runCompany)) return NotFound();

        var slips = await _db.PayrollSlips.AsNoTracking().Where(s => s.TenantId == tenantId && s.RunId == id).ToListAsync(cancellationToken);
        var earnings = await _db.PayrollEarnings.AsNoTracking().Where(e => e.TenantId == tenantId && e.PayrollRunId == id).ToListAsync(cancellationToken);
        var deductions = await _db.PayrollDeductions.AsNoTracking().Where(d => d.TenantId == tenantId && d.PayrollRunId == id).ToListAsync(cancellationToken);
        var currency = await ResolveCurrencyAsync(tenantId, cancellationToken);
        var glCtx = await LoadGlResolutionContextAsync(tenantId, run.CompanyId, cancellationToken);

        const string NotRecordedLabel = "Not recorded (run locked before cost centres)";
        var wasLocked = run.Status is "Locked" or "Voided" || run.LockedAtUtc != null;
        var basis = !wasLocked ? "Preview" : run.CostAccountingApplied ? "Stamped" : "NotRecorded";

        var names = new Dictionary<Guid, (string Code, string Name)>();
        List<FinanceGlEntry> ledger = new();
        if (basis == "Stamped")
        {
            ledger = await _db.FinanceGlEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == id && x.ReversalOfEntryId == null)
                .ToListAsync(cancellationToken);
            foreach (var l in ledger.Where(l => l.CostCenterId != null && l.CostCenterCode != null))
                names.TryAdd(l.CostCenterId!.Value, (l.CostCenterCode!, l.CostCenterName ?? l.CostCenterCode!));
        }
        else if (basis == "Preview")
        {
            // A dry-run resolution exactly as Lock would decide it (same small-cell roll-up). In memory only: nothing is saved.
            if (run.CompanyId is Guid previewCompany)
            {
                var plan = await PayrollCostStamper.PlanAsync(_db, tenantId, previewCompany, earnings, deductions, glCtx, cancellationToken);
                if (plan.Enabled) PayrollCostStamper.Apply(plan, earnings, deductions, slips);
                else
                {
                    var resolved = await PayrollCostStamper.ResolveEmployeesAsync(_db, tenantId, previewCompany, slips.Select(s => s.EmployeeId), cancellationToken);
                    var byEmployee = resolved.ToDictionary(kv => kv.Key, kv => kv.Value.CostCenterId);
                    foreach (var e in earnings) e.CostCenterId = byEmployee.GetValueOrDefault(e.EmployeeId);
                    foreach (var d in deductions) d.CostCenterId = byEmployee.GetValueOrDefault(d.EmployeeId);
                    foreach (var s in slips) s.CostCenterId = byEmployee.GetValueOrDefault(s.EmployeeId);
                }
            }
            var previewIds = earnings.Select(e => e.CostCenterId).Concat(deductions.Select(d => d.CostCenterId)).Concat(slips.Select(s => s.CostCenterId))
                .Where(c => c != null).Select(c => c!.Value).Distinct().ToList();
            foreach (var cc in await _db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId && previewIds.Contains(c.Id)).ToListAsync(cancellationToken))
                names[cc.Id] = (cc.Code, cc.Name);
        }

        decimal Round(decimal v) => Math.Round(v, 2);
        string AccountType(string label)
        {
            var code = label.Split(' ', 2)[0];
            if (glCtx.AccountRules.TryGetValue(code, out var a)) return a.AccountType;
            return PayrollGlCatalog.Drivers.FirstOrDefault(d => d.DefaultCode == code)?.AccountType ?? string.Empty;
        }
        bool IsPnl(string label) => !string.IsNullOrWhiteSpace(label) && FinanceGlEntryGuard.IsPnl(AccountType(label));

        // ── NotRecorded: a run locked before cost centres. One bucket, from the slips only. ───────────────────────────────
        if (basis == "NotRecorded")
        {
            var gross = slips.Sum(s => s.GrossSalary);
            var employer = slips.Sum(s => s.EmployerStatutoryTotal);
            var only = new
            {
                costCenterId = (Guid?)null, costCenterCode = "—", costCenterName = NotRecordedLabel, employeeCount = slips.Count,
                grossSalary = gross, netSalary = slips.Sum(s => s.NetSalary), employerCost = employer, totalCost = gross + employer, percentOfTotal = gross + employer == 0 ? 0m : 100m,
                contraDeductions = 0m, journalNet = (decimal?)null, clearedAgainstAccrual = (decimal?)null, roundingAdjustment = (decimal?)null, smallCell = false,
            };
            return Ok(new
            {
                runId = id, period = $"{run.Year}-{run.Month:D2}", currency, basis, label = NotRecordedLabel,
                totalEmployees = slips.Count, totalGross = gross, totalNet = only.netSalary, totalEmployerCost = employer, totalCost = only.totalCost,
                reconciles = (bool?)null, retroNote = RetroNote, retroNoteAr = RetroNoteAr, allocations = new[] { only },
            });
        }

        // ── Stamped / Preview: bucket by the cost centre carried on the rows ───────────────────────────────────────────────
        var keys = earnings.Select(e => e.CostCenterId)
            .Concat(deductions.Where(d => d.IsEmployerContribution || d.CostCenterId != null).Select(d => d.CostCenterId))
            .Concat(slips.Select(s => s.CostCenterId))
            .Concat(ledger.Where(l => l.CostCenterId != null && (IsPnl(l.DebitAccount) || IsPnl(l.CreditAccount))).Select(l => l.CostCenterId))
            .Distinct().ToList();

        // Bonus and settlement earnings that payroll cleared against an accrual (the run carries the clearing lines) are not this run's expense.
        var bonusDeferred = ledger.Any(l => l.EventType == GlEventTypes.BonusPayrollClearing);
        var settlementDeferred = ledger.Any(l => l.EventType == GlEventTypes.SettlementPayrollClearing);
        bool Deferred(PayrollEarning e) => (bonusDeferred && e.Source == "Bonus") || (settlementDeferred && e.Source == FinalSettlementComponents.SettlementSource);

        var k = (int)glCtx.CostCentreSmallCellMin;
        var buckets = keys.Select(cc =>
        {
            var gross = earnings.Where(e => e.CostCenterId == cc).Sum(e => e.Amount);
            var contra = deductions.Where(d => !d.IsEmployerContribution && d.CostCenterId == cc && cc != null).Sum(d => d.Amount);
            var employer = deductions.Where(d => d.IsEmployerContribution && d.CostCenterId == cc).Sum(d => d.Amount);
            var mine = slips.Where(s => s.CostCenterId == cc).ToList();
            var deferredEarnings = earnings.Where(e => e.CostCenterId == cc && Deferred(e)).Sum(e => e.Amount);
            decimal? journalNet = null, cleared = null, rounding = null;
            if (basis == "Stamped")
            {
                var mineLines = ledger.Where(l => l.CostCenterId == cc).ToList();
                decimal Net(IEnumerable<FinanceGlEntry> ls) =>
                    ls.Sum(l => (IsPnl(l.DebitAccount) ? l.Amount : 0m) - (IsPnl(l.CreditAccount) ? l.Amount : 0m));
                journalNet = Round(Net(mineLines));
                var unaccrued = mineLines.Where(l => l.Description.EndsWith("(un-accrued)", StringComparison.Ordinal) && IsPnl(l.DebitAccount)).Sum(l => l.Amount);
                cleared = Round(deferredEarnings - unaccrued);
                rounding = Round(Net(mineLines.Where(l => l.Description == PayrollGlDescriptions.Rounding)));
            }
            names.TryGetValue(cc ?? Guid.Empty, out var nm);
            return new
            {
                costCenterId = cc,
                costCenterCode = cc is null ? "—" : nm.Code ?? "—",
                costCenterName = cc is null ? "Unassigned" : nm.Name ?? nm.Code ?? "—",
                employeeCount = mine.Count,
                grossSalary = gross,
                netSalary = mine.Sum(s => s.NetSalary),
                employerCost = employer,
                totalCost = gross + employer,
                contraDeductions = contra,
                journalNet,
                clearedAgainstAccrual = cleared,
                roundingAdjustment = rounding,
                smallCell = k > 0 && cc is not null && mine.Count < k,
            };
        }).OrderByDescending(b => b.totalCost).ThenBy(b => b.costCenterCode, StringComparer.Ordinal).ToList();

        var totalCost = buckets.Sum(b => b.totalCost);
        bool? reconciles = basis != "Stamped" ? null
            : buckets.All(b => Math.Abs(b.journalNet!.Value - (b.grossSalary - b.contraDeductions + b.employerCost - b.clearedAgainstAccrual!.Value + b.roundingAdjustment!.Value)) < 0.005m);

        return Ok(new
        {
            runId = id,
            period = $"{run.Year}-{run.Month:D2}",
            currency,
            basis,
            label = basis == "Preview" ? "Preview" : null,
            totalEmployees = slips.Count,
            totalGross = buckets.Sum(b => b.grossSalary),
            totalNet = buckets.Sum(b => b.netSalary),
            totalEmployerCost = buckets.Sum(b => b.employerCost),
            totalCost,
            reconciles,
            retroNote = RetroNote,
            retroNoteAr = RetroNoteAr,
            allocations = buckets.Select(b => new
            {
                b.costCenterId, b.costCenterCode, b.costCenterName, b.employeeCount,
                b.grossSalary, b.netSalary, b.employerCost, b.totalCost,
                percentOfTotal = totalCost == 0 ? 0m : Math.Round(b.totalCost / totalCost * 100, 2),
                b.contraDeductions, b.journalNet, b.clearedAgainstAccrual, b.roundingAdjustment, b.smallCell,
            }).ToList(),
        });
    }

    /// <summary>D-7: effective dating is out of v1, and the report says so wherever it is read.</summary>
    private const string RetroNote =
        "Pay for earlier periods (retro-pay) and back-dated transfers post to the cost centre that is current at Lock, not to the cost centre in force on the date they relate to.";
    private const string RetroNoteAr =
        "الأجور المتأخرة (الرجعية) والنقل بأثر رجعي تُرحَّل إلى مركز التكلفة الساري وقت قفل المسير، وليس إلى المركز الساري في التاريخ الذي تخصه.";

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
        var settlementCompany = await _db.Companies.AsNoTracking()
            .SingleAsync(c => c.TenantId == tenantId && c.Id == settlementCompanyId, cancellationToken);

        // ── OPEN ITEM: FAIL LOUD WHEN EOSB IS NOT ENABLED ────────────────────────────────────────────
        // /eosb/calculate refuses outright when EosbEnabled is false while the pre-C1 /final-settlement
        // silently computed ZERO gratuity. That asymmetry was survivable while the figure was only
        // displayed; accruing a real payable with a silent zero gratuity is not.
        var gcc = await _db.GCCComplianceSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && x.CountryCode == settlementCompany.CountryCode, cancellationToken);
        if (gcc is null || !gcc.EosbEnabled)
            return UnprocessableEntity(new
            {
                error   = "eosb_not_enabled",
                message = $"End-of-service is not enabled for company country '{settlementCompany.CountryCode}', so a settlement would accrue a payable " +
                          "with a silent ZERO gratuity. Enable EOSB in GCC Settings (or record the amount as an " +
                          "explicit other-dues line) before settling.",
            });

        var existingStatus = await _db.EmployeeFinalSettlements.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.OffboardingId == offboarding.Id
                     && s.Status != FinalSettlementStatuses.Cancelled)
            .Select(s => new { s.Id, s.Status })
            .FirstOrDefaultAsync(cancellationToken);
        // ── GUARD 1: CANNOT SETTLE TWICE ─────────────────────────────────────────────────────────────
        // A Draft/PendingApproval settlement is RE-COMPUTED in place (the operator is still iterating);
        // once it has accrued, it is immutable and must be cancelled (which contras the journal) first.
        // Checked here so an accrued settlement is refused before any computation, and re-checked under
        // the row lock below, where it cannot race an approval.
        if (existingStatus is not null && FinalSettlementStatuses.HasAccrued(existingStatus.Status))
            return SettlementAlreadyAccrued(existingStatus.Id, existingStatus.Status);

        var plan = await BuildFinalSettlementPlanAsync(
            tenantId, employee, offboarding, gcc, settlementCompanyId, req, cancellationToken);

        // F10 — the write runs under a row lock on the live settlement. Without it, a recompute racing an
        // approval could rewrite the lines and amounts of a settlement whose accrual had just posted on
        // the OLD figures, leaving the payable and its journal disagreeing.
        var auditId = Guid.NewGuid();
        return await MutateSettlementAsync(tenantId, auditId, async token =>
        {
            var existing = await _db.EmployeeFinalSettlements
                .TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
                .Where(s => s.TenantId == tenantId && s.OffboardingId == offboarding.Id
                         && s.Status != FinalSettlementStatuses.Cancelled)
                .OrderBy(s => s.Id)
                .FirstOrDefaultAsync(token);
            if (existing is not null && FinalSettlementStatuses.HasAccrued(existing.Status))
                return SettlementAlreadyAccrued(existing.Id, existing.Status);

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
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.Status == "Draft", token);
            if (draftEosb is not null)
            {
                settlement.EosbCalculationId = draftEosb.Id;
                draftEosb.CalculationDate = plan.LastWorkingDay;
                draftEosb.EligibleSalary = plan.BasicWage;
                draftEosb.CalculatedAmount = plan.GratuityAmount;
            }

            // Every transition routes through POD-A3's hash-chained helper. /eosb/calculate wrote NOTHING to
            // that chain at all before this pod; a settlement is a payable, so every step of its life is now
            // tamper-evident with an actor. F10: this row is also how approval knows who RECOMPUTED a
            // settlement — CreatedByUserId keeps the first author, and a recomputer is a maker too.
            await PayrollAudit(FinalSettlementDraftedAction, "EmployeeFinalSettlement", settlement.Id.ToString(), new
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
            }, token, auditId);
            await _db.SaveChangesAsync(token);

            return Ok(ProjectSettlement(settlement, lineRows, plan));
        }, cancellationToken);
    }

    private ConflictObjectResult SettlementAlreadyAccrued(Guid settlementId, string status) => Conflict(new
    {
        error   = "settlement_already_exists",
        message = $"A settlement for this separation already exists in '{status}' status and has " +
                  "posted its accrual journal. Cancel it (which posts the contra) before computing a new one.",
        settlementId,
        status,
    });

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
        var auditId = Guid.NewGuid();
        // F10 — under the settlement's row lock, so a submit cannot land after a concurrent approval and
        // put an Approved settlement back into PendingApproval.
        return await MutateSettlementAsync(tenantId, auditId, async token =>
        {
            var s = await LockSettlementAsync(tenantId, id, token);
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
            await PayrollAudit(FinalSettlementSubmittedAction, "EmployeeFinalSettlement", s.Id.ToString(),
                new { s.EmployeeId, s.NetPayable, reason = req?.Reason }, token, auditId);
            await _db.SaveChangesAsync(token);
            return Ok(new { settlementId = s.Id, status = s.Status });
        }, cancellationToken);
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
        // F10 — an approval nobody can be named for cannot be checked against the maker, and would be
        // recorded as approved by nobody. Refused outright.
        if (GetUserId() is not Guid actorId) return Forbid();

        // F10 — the whole approval runs as one transaction that starts by locking the settlement row.
        // Two approvers clicking at once used to BOTH pass the post-once check (neither had committed)
        // and BOTH post the accrual: the payable was booked twice. Now the second waits for the first,
        // then sees an Approved settlement and is refused.
        var auditId = Guid.NewGuid();
        return await MutateSettlementAsync(tenantId, auditId,
            token => ApproveLockedFinalSettlementAsync(tenantId, id, actorId, req, auditId, token),
            cancellationToken);
    }

    private async Task<IActionResult> ApproveLockedFinalSettlementAsync(
        Guid tenantId, Guid id, Guid actorId, ApproveFinalSettlementRequest req, Guid auditId,
        CancellationToken cancellationToken)
    {
        var s = await LockSettlementAsync(tenantId, id, cancellationToken);
        if (s is null) return NotFound();
        if (s.Status is not (FinalSettlementStatuses.Draft or FinalSettlementStatuses.PendingApproval))
            return BadRequest(new
            {
                error   = "invalid_transition",
                message = $"Only a Draft or PendingApproval settlement can be approved (current: '{s.Status}').",
                status  = s.Status,
            });

        // ── F10: MAKER / CHECKER, WITH NO ESCAPE ─────────────────────────────────────────────────────
        // Approval turns the figures into a booked liability, so it must come from someone who did not
        // produce them. There used to be an acknowledgeSelfApproval flag that let the creator approve their
        // own settlement by ticking a box, and the submitter and anyone who RECOMPUTED it were never checked.
        // A recompute keeps CreatedByUserId, so the audit trail's drafted/submitted rows — written with the
        // actor on every compute, recompute and submit — are read as well as the row's own stamps.
        var makerRole = await SettlementMakerRoleAsync(tenantId, s, actorId, cancellationToken);
        if (makerRole is not null)
            return Conflict(new
            {
                error   = "segregation_of_duties",
                message = $"You {makerRole} this settlement, so you cannot also approve it. A different user with " +
                          "payroll approval rights must approve it.",
                settlementId = s.Id,
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
        // The pack computes gratuity on the wage base IT applies — for KSA basic + housing, plus transport
        // unless 'eosb.include_transport' is off, with the composite "other allowances" (food, mobile, other)
        // excluded unless 'eosb.include_other_allowances' is on (KsaEndOfServiceCalculator, A1). A non-zero
        // delta means some of the package sits outside that base. A displayed shortfall was advisory; a
        // DISBURSED one may be underpayment of a statutory entitlement, evidenced by the employer's own signed
        // settlement, so it requires an explicit, RECORDED acknowledgement. The message names the base the
        // settlement actually used (its inputs snapshot), not a fixed sentence.
        if (s.WageBaseDeltaAmount > 0.01m && !req.AcknowledgeWageBaseFloor)
        {
            var wageBase = DescribeEosbWageBase(s.InputsSnapshotJson, s.Currency);
            return UnprocessableEntity(new
            {
                error   = "wage_base_floor_requires_acknowledgement",
                message = $"This settlement pays gratuity on {wageBase.Text}. Computed on the full package " +
                          $"it would be {s.WageBaseDeltaAmount:N2} {s.Currency} HIGHER. KSA " +
                          "Labour Law Art. 84 measures the award on the LAST WAGE (Art. 2: basic plus all due increments), " +
                          "so if an excluded allowance is a regular cash increment, approving this disburses a known potential shortfall. " +
                          "Either raise the settlement (add the difference as an other-dues line), or re-send with " +
                          "acknowledgeWageBaseFloor=true to record who accepted this wage base. " +
                          "[FLAG-COMPLIANCE-KSA]",
                wageBaseDelta = s.WageBaseDeltaAmount,
                appliedWageBase = wageBase.Applied,
                fullPackage = wageBase.FullPackage,
                currency = s.Currency,
            });
        }

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
        // POD-C2 — whatever is still provided for this leaver after the award is RELEASED with them
        // (DR 2310 / CR 5110), so 2310 never carries provision for someone who has left. See BuildAccrual.
        var provisionReleases = new List<(string Account, decimal Amount)>();
        while (provisionCursor.PositionFor(s.EmployeeId, s.CompanyId) is { } leftover)
        {
            var (releasedFrom, released) = provisionCursor.Take(
                s.EmployeeId, s.CompanyId, provisionCursor.RemainingOn(leftover));
            if (releasedFrom is null || released <= 0m) break;
            provisionReleases.Add((releasedFrom.ProvisionAccount, released));
        }

        // Cost accounting (WP-C2): the leaver's cost centre AT THE APPROVAL DATE (contract 2.4). A Required expense account with no cost
        // centre for the leaver refuses the approval with M-1 and nothing is written.
        Guid? settlementCostCentre = null;
        if (glCtx.CostAccountingEnabled && s.CompanyId is Guid settlementCompany)
        {
            var resolution = (await PayrollCostStamper.ResolveEmployeesAsync(_db, tenantId, settlementCompany, new[] { s.EmployeeId }, cancellationToken, glCtx))[s.EmployeeId];
            settlementCostCentre = resolution.CostCenterId;
            if (settlementCostCentre is null)
            {
                var expense = GlAccountResolver.Resolve(FinalSettlementGlLedger.ExpenseDriverFor(FinalSettlementComponents.Gratuity), glCtx.Overrides, glCtx.DriverDefaults);
                if (PayrollCostStamper.RuleOf(expense.Code, glCtx) == "Required")
                    return UnprocessableEntity(PayrollCostStamper.BuildRefusal(new[]
                    {
                        new CostBlocker(s.EmployeeId, s.EmployeeCode, resolution.Level ?? "company", null, null, null,
                            resolution.UnresolvedReason == "none" ? PayrollCostStamper.RequiredCode : PayrollCostStamper.InvalidCode,
                            expense.Code, expense.Name, s.GrossPayable),
                    }, glCtx.CostCentreSmallCellMin).Body);
            }
        }
        var (glLines, dr, cr) = FinalSettlementGlLedger.BuildAccrual(
            tenantId, s, lines, glPeriod, glCtx, actorId, GetUserName(),
            provisionTaken, provisionPosition?.ProvisionAccount, provisionReleases, settlementCostCentre);
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

        // F10 — the approval's audit names the maker, the submitter and the approver, so the separation
        // of duties can be read off the tamper-evident chain without joining back to the settlement row.
        await PayrollAudit(FinalSettlementApprovedAction, "EmployeeFinalSettlement", s.Id.ToString(), new
        {
            s.EmployeeId, s.EmployeeCode, s.TerminationReason, s.LastWorkingDay,
            gross = s.GrossPayable, deductions = s.TotalDeductions, net = s.NetPayable,
            glPeriod, totalDebits = dr, totalCredits = cr,
            provisionConsumed = provisionTaken, provisionAccount = provisionPosition?.ProvisionAccount,
            provisionReleased = provisionReleases.Sum(r => r.Amount),
            wageBaseDelta = s.WageBaseDeltaAmount,
            wageBaseAcknowledged = req.AcknowledgeWageBaseFloor,
            wagesAcknowledgedUnpaid = s.WagesAcknowledgedUnpaid,
            computedByUserId = s.CreatedByUserId,
            submittedByUserId = s.SubmittedByUserId,
            approvedByUserId = actorId,
            approvedByName = s.ApprovedByName,
            reason = req.Reason,
        }, cancellationToken, auditId);
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

        // F10 — under the settlement's row lock. A cancel that read the settlement before a concurrent
        // approval committed found no accrual to contra, then flipped the now-Approved settlement to
        // Cancelled: a cancelled settlement with a live payable on 2320. Locked, the cancel waits for the
        // approval and contras what it posted.
        var auditId = Guid.NewGuid();
        return await MutateSettlementAsync(tenantId, auditId,
            token => CancelLockedFinalSettlementAsync(tenantId, id, req, auditId, token),
            cancellationToken);
    }

    private async Task<IActionResult> CancelLockedFinalSettlementAsync(
        Guid tenantId, Guid id, PayrollReasonRequest req, Guid auditId, CancellationToken cancellationToken)
    {
        var s = await LockSettlementAsync(tenantId, id, cancellationToken);
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
                    CostCenterId = orig.CostCenterId, CostCenterCode = orig.CostCenterCode, CostCenterName = orig.CostCenterName,
                    CostCenterGlSegment = orig.CostCenterGlSegment, PostingKey = PayrollCostStamper.ReversalKey(orig.Id),
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
        }, cancellationToken, auditId);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { settlementId = s.Id, status = s.Status, reversedEntries = contras.Count });
    }

    // ── F10: settlement state changes — one locked transaction each, and maker/checker ─────────────

    private const string FinalSettlementDraftedAction = "payroll.final_settlement.drafted";
    private const string FinalSettlementSubmittedAction = "payroll.final_settlement.submitted";
    private const string FinalSettlementApprovedAction = "payroll.final_settlement.approved";

    /// <summary>
    /// Runs one settlement state change as a single transaction under the production retrying strategy.
    /// The operation starts by locking the settlement row (<see cref="LockSettlementAsync"/>), so
    /// compute, submit, approve and cancel on the same settlement are serialized: whichever commits
    /// second re-reads the first one's result and applies its own rules to it. A retry after an
    /// ambiguous commit is settled by looking for this call's own audit row (<paramref name="auditId"/>),
    /// the Offboarding controller's pattern, so an approval is never posted twice by a retry either.
    /// Refusals return without saving; the empty transaction then commits nothing.
    /// </summary>
    private async Task<IActionResult> MutateSettlementAsync(
        Guid tenantId, Guid auditId, Func<CancellationToken, Task<IActionResult>> operation, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
            return await operation(ct);

        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                // A retry starts clean; the first attempt keeps anything the caller already tracks.
                if (attempt++ > 0) _db.ChangeTracker.Clear();
                return await operation(token);
            },
            async token => await _db.PayrollAuditLogs.AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.TenantId == tenantId, token),
            System.Data.IsolationLevel.ReadCommitted,
            ct);
    }

    /// <summary>The settlement, tracked and locked FOR UPDATE until the surrounding transaction ends.</summary>
    private Task<EmployeeFinalSettlement?> LockSettlementAsync(Guid tenantId, Guid id, CancellationToken ct) =>
        _db.EmployeeFinalSettlements
            .TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);

    /// <summary>
    /// F10 — why <paramref name="actorId"/> may not approve <paramref name="s"/>, or null when they may:
    /// "calculated" if they computed it (the row's creator, or anyone whose compute/recompute is on the
    /// audit chain for it), "submitted" if they put it forward for approval.
    /// </summary>
    private async Task<string?> SettlementMakerRoleAsync(
        Guid tenantId, EmployeeFinalSettlement s, Guid actorId, CancellationToken ct)
    {
        if (s.CreatedByUserId == actorId) return "calculated";
        if (s.SubmittedByUserId == actorId) return "submitted";
        var entityId = s.Id.ToString();
        var actions = await _db.PayrollAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EntityName == "EmployeeFinalSettlement" && x.EntityId == entityId
                     && x.UserId == actorId
                     && (x.Action == FinalSettlementDraftedAction || x.Action == FinalSettlementSubmittedAction))
            .Select(x => x.Action)
            .Distinct()
            .ToListAsync(ct);
        return actions.Contains(FinalSettlementDraftedAction) ? "calculated"
             : actions.Contains(FinalSettlementSubmittedAction) ? "submitted"
             : null;
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

    /// <summary>
    /// "a wage base of 12,500.00 SAR (basic + housing + transport, as applied by the country pack), not the full
    /// package of 13,300.00 SAR — 800.00 SAR of other allowances (food, mobile, other) is excluded" — read from the
    /// settlement's own inputs snapshot (appliedWageBase, the package components), so the sentence states the base
    /// the money was computed on. Falls back to a neutral phrase when the snapshot predates those fields.
    /// </summary>
    internal static (string Text, decimal? Applied, decimal? FullPackage) DescribeEosbWageBase(string? inputsSnapshotJson, string currency)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(inputsSnapshotJson) ? "{}" : inputsSnapshotJson);
            var root = doc.RootElement;
            decimal? Read(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
            var applied = Read("appliedWageBase");
            var gross = Read("monthlyGross");
            if (applied is not decimal appliedBase || gross is not decimal fullPackage)
                return ("the wage base the country pack applied, not the full package", applied, gross);
            var basic = Read("basicWage") ?? 0m;
            var housing = Read("packageHousing") ?? 0m;
            var transport = Read("packageTransport") ?? 0m;
            // What the applied base covers, in component words: basic + housing is the Art. 84 floor; transport
            // is in when the base reaches it (the default 'eosb.include_transport' = true).
            var covered = new List<string> { "basic" };
            var running = basic;
            if (housing > 0m && appliedBase + 0.005m >= running + housing) { covered.Add("housing"); running += housing; }
            if (transport > 0m && appliedBase + 0.005m >= running + transport) { covered.Add("transport"); running += transport; }
            var excluded = Math.Max(0m, fullPackage - appliedBase);
            var excludedWhat = new List<string>();
            if (transport > 0m && !covered.Contains("transport")) excludedWhat.Add("transport");
            excludedWhat.Add("other allowances (food, mobile, other)");
            var text = $"a wage base of {appliedBase:N2} {currency} ({string.Join(" + ", covered)}, as applied by the country pack), " +
                       $"not the full package of {fullPackage:N2} {currency}" +
                       (excluded > 0.005m ? $" — {excluded:N2} {currency} of {string.Join(" and ", excludedWhat)} is excluded by default" : string.Empty);
            return (text, appliedBase, fullPackage);
        }
        catch (System.Text.Json.JsonException)
        {
            return ("the wage base the country pack applied, not the full package", null, null);
        }
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
        var company = companyId.HasValue
            ? await _db.Companies.AsNoTracking().SingleAsync(c => c.TenantId == tenantId && c.Id == companyId.Value, ct)
            : null;
        var countryCode = !string.IsNullOrWhiteSpace(company?.CountryCode) ? company!.CountryCode : gcc.CountryCode;
        var jurisdiction = company?.Jurisdiction ?? string.Empty;
        var wageBasis = await ResolveEosbWageBasisAsync(tenantId, companyId, salary, employee, ct);

        var terminationReason = await ResolveTerminationReasonAsync(tenantId, employee.Id, req.TerminationReason, ct);
        var calcDate = lastDay.ToDateTime(TimeOnly.MinValue);

        // ── POD-A2's ONE authoritative engine. The Breakdown is kept IN FULL — /eosb/calculate discards
        //    it and keeps only the rule string, so the Art.84 tier split and the Art.85/80 adjustment line
        //    were unrecoverable from anything the product persisted.
        // M1 — the carried service start wins when it is earlier than this product's joining date, so a
        // migrated leaver is settled on their whole service and not on their KynexOne tenure alone.
        var (serviceStart, _carriedStart) = await ResolveEosbServiceStartAsync(tenantId, employee, ct);
        var unpaidLeaveDays = await ResolveUnpaidLeaveDaysAsync(
            tenantId, employee.Id, serviceStart, lastDay, ct);
        var (eosbResult, serviceYears) = await ComputeEndOfServiceAsync(
            countryCode, jurisdiction, companyId ?? Guid.Empty, wageBasis,
            serviceStart, calcDate, terminationReason, employee, unpaidLeaveDays, gcc, ct);
        var gratuity = Math.Round(eosbResult.TotalGratuity, 2);
        var appliedWageBase = eosbResult.AppliedWageBase > 0m ? eosbResult.AppliedWageBase : wageBasis.ConfiguredWage;

        // ── S1/A1: the pack's own statutory notices, surfaced ON THE SETTLEMENT ──────────────────────
        // A [COUNSEL] default that changes the award (transport in / other out) and a DIFC trustee
        // position that means "do not pay this" are decisions the approver has to make consciously.
        warnings.AddRange(eosbResult.Notices);
        if (serviceStart < DateOnly.FromDateTime(employee.JoiningDate))
            warnings.Add(CarriedServiceNotice(serviceStart, DateOnly.FromDateTime(employee.JoiningDate)));
        if (wageBasis.FallbackReason is not null && gratuity > 0m)
            warnings.Add("[FLAG-COMPLIANCE] " + wageBasis.FallbackReason);

        // [FLAG-COMPLIANCE] the wage-base delta against the FULL package, computed by re-running the SAME
        // pack on a package whose every component is loaded into the pack's configured-wage slot. Purely
        // indicative and never paid automatically — it is the number the approver must consciously accept
        // or correct. Post-A1 this is ZERO for a KSA leaver on the default rules whose only allowances are
        // housing and transport, because those are now IN the statutory base; it still fires where a
        // composite "other allowance" sits outside it.
        decimal wageBaseDelta = 0m;
        if (monthlyGross > appliedWageBase && gratuity > 0m)
        {
            var fullBasis = wageBasis with { ConfiguredWage = monthlyGross };
            var (fullResult, _) = await ComputeEndOfServiceAsync(
                countryCode, jurisdiction, companyId ?? Guid.Empty, fullBasis,
                serviceStart, calcDate, terminationReason, employee, unpaidLeaveDays, gcc, ct);
            wageBaseDelta = Math.Max(0m, Math.Round(fullResult.TotalGratuity - gratuity, 2));
            if (wageBaseDelta > 0m)
                warnings.Add($"[FLAG-COMPLIANCE] Gratuity is computed on the wage base the country pack applied " +
                             $"({appliedWageBase:N2}; configured components: {string.Join(", ", wageBasis.IncludedComponents)}). On the " +
                             $"full package ({monthlyGross:N2}) it would be {wageBaseDelta:N2} {currency} higher. " +
                             "KSA Art. 84 measures the award on the LAST WAGE (Art. 2: basic plus all due increments); " +
                             "UAE Art. 51 and Qatar Art. 54 are basic-only and this delta is expected there. " +
                             "Approval requires an explicit acknowledgement of this floor.");
        }

        // ── K5: one authoritative encashable-days function ───────────────────────────────────────────
        var exitRules = await CompanyPayrollRules.ResolveAsync(_db, tenantId, companyId, ct, lastDay);
        var cashOutBase = exitRules.LeaveCashOutBasis switch
        {
            CompanyPayrollRules.BasisBasic => basicWage,
            CompanyPayrollRules.BasisBasicHousing => basicWage + (salary?.HousingAllowance ?? 0m),
            _ => monthlyGross,
        };
        var encashment = await LeaveEncashmentCalculator.ComputeAsync(
            _db, tenantId, employee.Id, companyId, lastDay, cashOutBase, ct,
            statutoryAnnualLeaveCashOut: exitRules.LeaveCashOutOnExit,
            basisLabel: exitRules.LeaveCashOutBasis switch
            {
                CompanyPayrollRules.BasisBasic => "basic/30",
                CompanyPayrollRules.BasisBasicHousing => "(basic+housing)/30",
                _ => LeaveEncashmentCalculator.DayRateBasis,
            });
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
            .Where(l => l.TenantId == tenantId && l.CompanyId == companyId && !l.IsDeleted && l.CollectionStatus != "OnHold" && l.RepaymentMethod == "PayrollDeduction" && (l.Status == "Active" || l.Status == "Overdue")
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
            // M1 — the settlement records the service period it was AWARDED on. That is also what the
            // B4 no-overlapping-service-window guard compares, so a carried period cannot be settled
            // twice by re-settling on the KynexOne joining date.
            lastDay, serviceStart, dueDate,
            terminationReason, (decimal)Math.Round(serviceYears, 4), currency,
            basicWage, monthlyGross,
            JsonSerializer.Serialize(new
            {
                totalGratuity = eosbResult.TotalGratuity,
                applicableRule = eosbResult.ApplicableRule,
                breakdown = eosbResult.Breakdown.Select(b => new { b.Label, b.Amount }).ToList(),
                notices = eosbResult.Notices,
            }),
            JsonSerializer.Serialize(new
            {
                basicWage, monthlyGross,
                // S1/A1 — the wage the PACK awarded on. `configuredEosbWage` is what the tenant's
                // component catalog contributed; `appliedWageBase` is what statute actually used, and
                // the two differ whenever statute is the more generous of the pair.
                appliedWageBase, configuredEosbWage = wageBasis.ConfiguredWage,
                packageHousing = wageBasis.Package.HousingAllowance,
                packageTransport = wageBasis.Package.TransportAllowance,
                packageOther = wageBasis.Package.OtherAllowances,
                unpaidLeaveDays,
                eosbIncludedComponents = wageBasis.IncludedComponents, currency,
                // M1 — the start the pack measured on, plus the joining date it may differ from, so the
                // snapshot alone explains a migrated leaver's award years later.
                serviceStart,
                joiningDate = DateOnly.FromDateTime(employee.JoiningDate),
                carriedServiceStart = _carriedStart,
                serviceEnd = lastDay,
                terminationReason,
                countryCode,
                jurisdiction,
                contractType = employee.ContractType,
                nationality = employee.Nationality,
                wageBase = "country-pack statutory base, raised (never lowered) by PayComponent.EosbIncluded",
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

    private async Task<string> ResolveRunCurrencyAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        if (companyId.HasValue)
        {
            var companyCurrency = await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == companyId.Value && !c.IsDeleted)
                .Select(c => c.DefaultCurrency)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(companyCurrency))
                return companyCurrency.Trim().ToUpperInvariant();
        }

        return (await _db.ResolveTenantCurrencyAsync(tenantId, ct)).Trim().ToUpperInvariant();
    }

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
        var targetPeriodEnd = new DateOnly(targetYear, targetMonth, 1).AddMonths(1).AddDays(-1);

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
            // Coverage is a period fact, not a count of every salary-history row. Use the same
            // effective-dated assignment source as Process/GOSI and count each employee once.
            .Where(s => s.TenantId == tenantId && s.IsActive && s.EffectiveDate <= targetPeriodEnd)
            .Join(_db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active"),
                  s => s.EmployeeId, e => e.Id, (s, e) => new { s.EmployeeId, e.CompanyId })
            .GroupBy(x => x.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Select(x => x.EmployeeId).Distinct().Count() })
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
        var targetPeriodEnd = new DateOnly(targetYear, targetMonth, 1).AddMonths(1).AddDays(-1);

        var hasComponents = await _db.SalaryComponents
            .AnyAsync(c => c.TenantId == tenantId && c.IsActive, cancellationToken);

        var structureQuery = _db.SalaryStructures.Where(s => s.TenantId == tenantId && !s.IsDeleted && s.IsActive
                                                           && s.EffectiveDate <= targetPeriodEnd);
        if (companyId.HasValue)
            structureQuery = structureQuery.Where(s => s.CompanyId == companyId || s.CompanyId == null);
        var hasStructures = await structureQuery.AnyAsync(cancellationToken);
        var structureCount = await structureQuery.CountAsync(cancellationToken);

        var activeEmployeeQuery = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active");
        if (companyId.HasValue) activeEmployeeQuery = activeEmployeeQuery.Where(e => e.CompanyId == companyId);
        var totalActive = await activeEmployeeQuery.CountAsync(cancellationToken);

        var assignedCount = await _db.EmployeeSalaryStructures
            .Where(s => s.TenantId == tenantId && s.IsActive && s.EffectiveDate <= targetPeriodEnd)
            .Join(activeEmployeeQuery, s => s.EmployeeId, e => e.Id, (s, e) => s.EmployeeId)
            .Distinct()
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
        var prerequisites = await BuildPaymentPrerequisitesAsync(
            tenantId, companyId, activeEmployeeQuery, targetYear, targetMonth, targetPeriodEnd, cancellationToken);
        var isReadyForProcessing = hasComponents && hasStructures && coveragePercent >= 80;
        return Ok(new
        {
            Year = targetYear,
            Month = targetMonth,
            CompanyId = companyId,
            CompletionPercent = Math.Round(completedSteps * 100.0 / steps.Length, 0),
            IsReadyForProcessing = isReadyForProcessing,
            TotalActiveEmployees = totalActive,
            EmployeesWithSalary = assignedCount,
            SalaryCoveragePercent = coveragePercent,
            ValidationErrors = validationErrors,
            PayrollRunStatus = run?.Status,
            Steps = steps,
            OffCycleRunCount = offCycleRunsForPeriod,   // POD-B2: additional runs in the period, if any
            // Salary coverage says who CAN be calculated; this says who can actually be PAID. A run can
            // be processed with gaps here, but its validation will then block approval on them.
            IsReadyToPay = isReadyForProcessing && totalActive > 0
                && prerequisites.BlockedEmployees == 0 && prerequisites.CompanyBlocking.Count == 0,
            PaymentPrerequisites = prerequisites,
        });
    }

    /// <summary>
    /// Per-employee payment prerequisites for the period, evaluated before any run exists
    /// (see <see cref="PayrollPaymentPrerequisites"/>). Every query is tenant-filtered and bounded by
    /// the same active-employee population the readiness counts use, so its numbers reconcile with
    /// <c>TotalActiveEmployees</c> and <c>EmployeesWithSalary</c>.
    /// </summary>
    private async Task<PayrollPaymentPrerequisitesDto> BuildPaymentPrerequisitesAsync(
        Guid tenantId, Guid? companyId, IQueryable<Employee> activeEmployeeQuery,
        int year, int month, DateOnly periodEnd, CancellationToken ct)
    {
        var employees = await activeEmployeeQuery.AsNoTracking().OrderBy(e => e.EmployeeCode).ToListAsync(ct);
        var employeeIds = employees.Select(e => e.Id).ToList();

        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted && (!companyId.HasValue || c.Id == companyId))
            .ToListAsync(ct);

        var withSalary = (await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && s.EffectiveDate <= periodEnd && employeeIds.Contains(s.EmployeeId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();

        var profiles = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && employeeIds.Contains(p.EmployeeId))
            .ToListAsync(ct);

        // The ONE readiness evaluator's pay-block verdict — the same one the bank file refuses on.
        var (hardBlocked, driftBlocked) = await ComputePayReadinessAsync(tenantId, employees, ct, periodEnd);
        var payBlocked = hardBlocked.Concat(driftBlocked).ToHashSet();

        // Attendance is only evidence once the period has begun; before that its absence means nothing.
        var periodStart = new DateOnly(year, month, 1);
        HashSet<int>? withAttendance = null;
        if (periodStart <= DateOnly.FromDateTime(DateTime.UtcNow))
            withAttendance = (await _db.AttendanceDailyRecords.AsNoTracking()
                .Where(r => r.TenantId == tenantId && !r.IsDeleted && r.WorkDate >= periodStart && r.WorkDate <= periodEnd
                         && employeeIds.Contains(r.EmployeeId))
                .Select(r => r.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();

        return PayrollPaymentPrerequisites.Evaluate(new PayrollPaymentPrerequisites.Input(
            employees, companies, withSalary, profiles, payBlocked, withAttendance));
    }

    // ── Employee Salary Import / Export ───────────────────────────────────────────

    private static readonly string[] EmployeeSalaryCsvHeaders =
        { "EmployeeCode", "SalaryStructureCode", "BasicSalary", "HousingAllowance", "TransportAllowance", "FoodAllowance", "MobileAllowance", "OtherAllowance", "FixedDeduction", "Currency", "EffectiveDate" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.</summary>
    private static readonly string[] EmployeeSalaryCsvExampleRow =
        { "EMP-0001", "STD", "8000", "2000", "1000", "0", "0", "0", "0", "SAR", "2026-01-01" };

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
        return CsvDownload.Result(Csv.Build(EmployeeSalaryCsvHeaders, rows));
    }

    [HttpGet("employee-salaries/import-template")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public IActionResult EmployeeSalariesImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=employee_salaries_import_template.csv";
        return CsvDownload.Result(Csv.Template(EmployeeSalaryCsvHeaders, EmployeeSalaryCsvExampleRow));
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

        var planned = new List<PlannedSalaryRow>();
        var plannedRows = new Dictionary<(int, DateOnly), int>();
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

            // One row per employee and effective date: a second row for the same pair is refused here, in memory, instead
            // of reaching the database as two active rows (refused at commit by ex_employee_salary_structures__one_active_per_date).
            if (plannedRows.TryGetValue((employee.Id, effectiveDate), out var firstRow))
            { errors.Add($"Row {rowNum}: duplicates row {firstRow} (same employee and effective date {effectiveDate:yyyy-MM-dd}); this row was not applied."); skipped++; continue; }
            plannedRows[(employee.Id, effectiveDate)] = rowNum;
            planned.Add(new PlannedSalaryRow(rowNum, employee.Id, employee.EmployeeCode, structure.Id, basic, housing, transport, food, mobile, other, deduction, effectiveDate, currency));
        }

        // Nothing in the file is applied here. Each valid row becomes a pending change; Finance decides the file
        // (or single rows) afterwards. A row about the importing user's own pay is refused outright.
        foreach (var companyId in planned.Select(p => allEmployees.Values.First(e => e.Id == p.EmployeeId).CompanyId).Distinct())
            if (await Zayra.Api.Infrastructure.Approvals.SecondApproverCheck.RefuseIfNoneAsync(_db, tenantId, "payroll.approve", GetUserId(), null, companyId,
                    "importing salary changes", "استيراد تغييرات الرواتب", cancellationToken) is { } noApprover)
                return noApprover;
        var batchId = Guid.NewGuid();
        var callerId = GetUserId();
        var pending = 0;
        var employeesById = allEmployees.Values.ToDictionary(e => e.Id);
        foreach (var row in planned)
        {
            var subjectEmployee = employeesById[row.EmployeeId];
            if (await Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, callerId, row.EmployeeId, cancellationToken))
            { errors.Add($"Row {row.RowNum}: {OwnSalaryRefusal}"); skipped++; continue; }
            await StageSalaryChangeAsync(tenantId, subjectEmployee, row.StructureId,
                new EmployeeSalaryStructureRequest(row.EmployeeId, row.StructureId, row.Basic, row.Housing, row.Transport, row.Food, row.Mobile, row.Other,
                    row.Deduction, row.EffectiveDate, row.Currency),
                "Import", batchId, cancellationToken);
            pending++;
        }
        if (pending > 0)
        {
            await PayrollAudit("payroll.employee_salary.import_staged", "SalaryChangeRequest", batchId.ToString(),
                new { received = rows.Count, pending, skipped, errors = errors.Count }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return Ok(new { received = rows.Count, created = 0, updated = 0, pendingApproval = pending, batchId = pending > 0 ? batchId : (Guid?)null, skipped, errors = errors.Take(20) });
    }

    /// <summary>A retried import found that another writer changed a key's salary rows after the first attempt.</summary>
    private sealed class SalaryImportRaceException((int EmployeeId, DateOnly EffectiveDate) key) : Exception("salary row race")
    {
        public (int EmployeeId, DateOnly EffectiveDate) Key { get; } = key;
    }

    private sealed record PlannedSalaryRow(int RowNum, int EmployeeId, string EmployeeCode, Guid StructureId, decimal Basic, decimal Housing, decimal Transport,
        decimal Food, decimal Mobile, decimal Other, decimal Deduction, DateOnly EffectiveDate, string Currency);

    /// <summary>The (employee, date) of a 23P01 on the one-active-salary-row-per-date guard, or null for anything else.
    /// Postgres reports it as "Key (tenant_id, employee_id, effective_date)=(…, 12, 2026-01-01) conflicts with existing key (…)".</summary>
    internal static (int EmployeeId, DateOnly EffectiveDate)? SalaryRowConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is not Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ExclusionViolation } pg
                || pg.ConstraintName != MeritPlanningSql.SalaryRowExclusionName) continue;
            var m = System.Text.RegularExpressions.Regex.Match(pg.Detail ?? string.Empty, @"=\([^,]+,\s*(\d+),\s*(\d{4}-\d{2}-\d{2})\)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var id) && DateOnly.TryParse(m.Groups[2].Value, out var d) ? (id, d) : (0, default);
        }
        return null;
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


    /// <summary>
    /// The run's attendance check, before Process: the days whose pending deductions no longer match their punches (which
    /// block Process with ERR_ATTENDANCE_IMPACT_STALE) and the half-pair days whose late / early-exit minutes are held
    /// (WARN_MISSING_PUNCH_HELD). Over the population Process would pay, for the run's month.
    /// </summary>
    [HttpGet("runs/{id:guid}/attendance-reconciliation")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> AttendanceReconciliation(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var scope = await AttendanceReconciliationScopeAsync(tenantId, id, ct);
        if (scope is null) return NotFound();
        var (run, employees, from, to) = scope.Value;
        var ids = employees.Select(e => e.Id).ToList();
        IReadOnlyList<StaleAttendanceDay> stale = run.IncludesRecurringPay
            ? await AttendanceImpactReconciliation.FindStaleDaysAsync(_db, tenantId, ids, from, to, run.Id, ct)
            : Array.Empty<StaleAttendanceDay>();
        var held = await AttendanceImpactReconciliation.FindHeldDaysAsync(_db, tenantId, ids, from, to, ct);
        var byId = employees.ToDictionary(e => e.Id);
        string? CodeOf(int employeeId) => byId.TryGetValue(employeeId, out var e) ? e.EmployeeCode : null;
        string? NameOf(int employeeId) => byId.TryGetValue(employeeId, out var e) ? e.FullName : null;
        return Ok(new
        {
            runId = run.Id, from, to,
            consumesAttendance = run.IncludesRecurringPay,
            stale = new
            {
                code = AttendanceImpactReconciliation.StaleCode,
                employeeCount = stale.Select(d => d.EmployeeId).Distinct().Count(),
                dayCount = stale.Count,
                days = stale.Select(d => new { d.EmployeeId, employeeCode = CodeOf(d.EmployeeId), employeeName = NameOf(d.EmployeeId), d.WorkDate, d.Reason }),
            },
            held = new
            {
                code = AttendanceImpactReconciliation.HeldCode,
                employeeCount = held.Select(d => d.EmployeeId).Distinct().Count(),
                dayCount = held.Count,
                lateMinutes = held.Sum(d => d.LateMinutes),
                earlyExitMinutes = held.Sum(d => d.EarlyExitMinutes),
                days = held.Select(d => new { d.EmployeeId, employeeCode = CodeOf(d.EmployeeId), employeeName = NameOf(d.EmployeeId) ?? d.EmployeeName, d.WorkDate, d.LateMinutes, d.EarlyExitMinutes }),
            },
        });
    }

    /// <summary>
    /// Recomputes exactly the days the run's attendance check finds stale (the server finds them again; the client names
    /// none), with the day processor — or, for a day with no raw punches, by re-deriving its deductions from the record.
    /// Each day is its own transaction under the day's lock, like a range run. Paid and payroll-locked days are never
    /// touched. Afterwards the run can be processed.
    /// </summary>
    [HttpPost("runs/{id:guid}/attendance/recompute-stale")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> RecomputeStaleAttendance(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var scope = await AttendanceReconciliationScopeAsync(tenantId, id, ct);
        if (scope is null) return NotFound();
        var (run, employees, from, to) = scope.Value;
        // The statuses Process refuses: such a run has already consumed its attendance.
        if (run.Status is "Processed" or "PendingFinanceReview" or "Locked" or "Approved" or "Paid" or "Voided")
            return BadRequest(new { error = "run_not_processable", message = $"A '{run.Status}' run has already consumed its attendance. Reopen it first." });
        var stale = await AttendanceImpactReconciliation.FindStaleDaysAsync(_db, tenantId, employees.Select(e => e.Id).ToList(), from, to, run.Id, ct);
        // The population read stamps selection outcomes on tracked rows; this endpoint must not save them.
        _db.ChangeTracker.Clear();
        var recomputed = await AttendanceService.ForRecompute(_db).RecomputeDaysAsync(tenantId,
            stale.Select(d => (d.EmployeeId, d.WorkDate)).ToList(),
            new Zayra.Api.Application.Auth.RequestContext(HttpContext?.Connection.RemoteIpAddress?.ToString(), null, GetUserId(), tenantId),
            $"payroll run {run.Id}: stale attendance recomputed before processing", ct,
            stale.Where(d => !d.FromPunches).Select(d => (d.EmployeeId, d.WorkDate)).ToHashSet());
        var remaining = await AttendanceImpactReconciliation.FindStaleDaysAsync(_db, tenantId, employees.Select(e => e.Id).ToList(), from, to, run.Id, ct);
        return Ok(new
        {
            found = stale.Count,
            recomputed,
            remaining = remaining.Count,
            message = remaining.Count == 0
                ? $"{recomputed} attendance day(s) were recomputed. Process the run again."
                : $"{recomputed} attendance day(s) were recomputed; {remaining.Count} still differ (a payroll-locked period, or a day changed again). Review them before processing.",
        });
    }

    /// <summary>The run, the population Process would pay, and its month — for the attendance check before Process.</summary>
    private async Task<(PayrollRun Run, List<Employee> Employees, DateOnly From, DateOnly To)?> AttendanceReconciliationScopeAsync(
        Guid tenantId, Guid runId, CancellationToken ct)
    {
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.TenantId == tenantId, ct);
        if (run is null) return null;
        var from = new DateOnly(run.Year, run.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        // The same company resolution and eligible population Process uses.
        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted).OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
        var legacySingleCompany = companies.Count == 1;
        var company = run.CompanyId.HasValue ? companies.FirstOrDefault(c => c.Id == run.CompanyId.Value)
            : legacySingleCompany ? companies[0] : null;
        if (company is null) return (run, new List<Employee>(), from, to);
        var anyScoped = await _db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId.HasValue, ct);
        var anyForCompany = await _db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId == company.Id, ct);
        var (eligible, _, _, _, _) = await LoadEligibleWithLeaversAsync(tenantId, company.Id, legacySingleCompany || !anyScoped || !anyForCompany,
            asNoTracking: true, from, to, excludeRunId: run.Id, ct, includeSettlementLeavers: run.SettlesFinalSettlements);
        var population = await ResolveRunPopulationAsync(tenantId, run, eligible, ct);
        return (run, population.Employees, from, to);
    }

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
/// <param name="PeriodAmount">The line's amount for its frequency (a % line resolved against the package's basic).</param>
public record GradePackageLineDto(string Code, string Name, string ComponentType, string CalculationType, decimal Amount,
    decimal Percentage, bool IsTaxable, string Frequency, decimal PeriodAmount);
/// <param name="EmployeeCount">Active employees whose grade this is.</param>
/// <param name="SalariesOnStructure">Active salary assignments on any version of the grade's salary structure.</param>
public record GradePackageDto(Guid GradeId, string GradeCode, string GradeName, string? GradeNameAr, Guid? CompanyId,
    string? CompanyName, string Currency, decimal MinSalary, decimal MaxSalary, bool IsActive,
    IReadOnlyList<GradePackageLineDto> Lines, decimal MonthlyGross, decimal MonthlyDeductions, bool OutsideRange,
    int EmployeeCount, string? StructureCode, int? StructureVersion, int SalariesOnStructure);
/// <param name="Source">GradePayScale (Setup → Grades) or BenefitsByGrade (release_a tenants).</param>
/// <param name="OlderStructures">Structures that are not a grade's package and still have employees on them; read-only.</param>
public record GradePackagesView(string Source, IReadOnlyList<GradePackageDto> Grades, IReadOnlyList<SalaryStructureDto> OlderStructures);
public record SalaryComponentRequest(string Code, string Name, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable, bool IsActive = true);
/// <param name="SalaryStructureId">Optional: blank puts the salary on the employee's default structure — the grade's
/// package structure, or DIRECT without a grade.</param>
public record EmployeeSalaryStructureRequest(int EmployeeId, Guid? SalaryStructureId, decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance, decimal FoodAllowance, decimal MobileAllowance, decimal OtherAllowance, decimal FixedDeduction, DateOnly EffectiveDate, string? Currency);
// POD-B2 (M5b) — ExpectedExcludedCount is an ACKNOWLEDGEMENT, not a warning. Approve refuses with 409
// when the run holds deliberate exclusions and the approver's expectation does not match the resolved
// count, so the person signing off cannot miss a hold-out buried among dozens of routine warnings.
// Optional and defaulted, so every existing caller (and the frontend) is unaffected on a run with none.
// POD-B3 — ExpectedOverriddenCount is the same acknowledgement for consciously CLEARED compliance
// errors. Optional and defaulted, so a run with no overrides (i.e. every run today) is unaffected.
/// <param name="ExpectedOutsideBankCount">Acknowledges, by count, the employees paid by cash or cheque (outside
/// the bank/WPS file). Required when the run has any.</param>
public record PayrollDecisionRequest(string? Notes, int? ExpectedExcludedCount = null, int? ExpectedOverriddenCount = null,
    int? ExpectedOutsideBankCount = null, int? ExpectedUncostedCount = null);
/// <param name="ExpectedOutsideBankCount">Only for a run locked before payment methods were frozen: acknowledges,
/// by count, the cash/cheque employees the batch will leave out of the bank file.</param>
public record PayrollPaymentBatchRequest(string? PaymentMethod, string? Currency, int? ExpectedOutsideBankCount = null);
/// <summary><paramref name="EvidenceId"/> is REQUIRED to mark a WPS batch Accepted: the id of evidence
/// uploaded through POST payment-batches/{id}/wps-evidence (the bank's WPS output file or a Mudad
/// compliance screenshot/PDF). A status picked from a dropdown is not proof that Mudad accepted anything.</summary>
public record WpsStatusRequest(string Status, string? Notes, string? Reference = null, Guid? EvidenceId = null);

/// <summary>Multipart body for POST payment-batches/{id}/wps-evidence.</summary>
public sealed class WpsEvidenceUploadForm
{
    public string? Kind { get; set; }
    public string? Note { get; set; }
    public IFormFile? File { get; set; }
}
public record ErpPostingStatusRequest(string Status, string? Reference = null, string? Notes = null);
// POD-B1 — settlement / remittance / reversal request bodies.
/// <summary>A wage paid outside the bank file: Cash or Cheque, its cheque number / receipt reference, the date paid.</summary>
public record OutsidePaymentRequest(int EmployeeId, string? Method, string? Reference, DateOnly? PaidDate = null);
public record SettlePaymentBatchRequest(string? Reference = null, DateOnly? PaidDate = null);
public record RemitStatutoryRequest(string? Group = null, string? Reference = null, DateOnly? RemitDate = null);
/// <param name="DocumentReference">The decision or document the reason rests on. REQUIRED to override the
/// KSA Art. 92/93 cap (DEDUCTIONS_EXCEED_HALF_WAGE): a labour court / commission decision or other lawful
/// written basis, by its reference number.</param>
public record PayrollReasonRequest(string? Reason = null, string? DocumentReference = null);
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
    /// <summary>Records that the approver accepts gratuity computed on the wage base the country pack applied
    /// (KSA: basic + housing + transport by default; other allowances excluded) rather than the full package.
    /// Required whenever the computed delta is non-zero.</summary>
    bool AcknowledgeWageBaseFloor = false,
    /// <summary>Records that the leaver's final-month wages are genuinely UNPAID and authorises adding
    /// them to this settlement as an explicit other-dues line.</summary>
    bool AcknowledgeWagesUnpaid = false,
    string? WagesUnpaidReason = null,
    /// <summary>GL date for the accrual journal. Defaults to today; the period is guarded for close.</summary>
    DateOnly? AccrualDate = null,
    string? Reason = null);
