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
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/payroll")]
[Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
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

    public PayrollController(ZayraDbContext db, IDataScopeService scopeService, IHttpContextAccessor http,
        INotificationService notifications, ICountryPackResolver packResolver, IStatutoryRuleReader ruleReader,
        ILetterService letters, IDocumentStorage storage, PdfRenderGate pdfGate)
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
    }

    [HttpGet("salary-structures")]
    public async Task<IActionResult> SalaryStructures([FromQuery] Guid? companyId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var q = _db.SalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (companyId.HasValue)
            q = q.Where(x => x.CompanyId == companyId || x.CompanyId == null);
        return Ok(await q.OrderBy(x => x.Name).ToListAsync(cancellationToken));
    }

    [HttpPost("salary-structures")]
    public async Task<IActionResult> CreateSalaryStructure(SalaryStructureRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (await _db.SalaryStructures.AnyAsync(x => x.TenantId == tenantId && x.Code == req.Code && !x.IsDeleted, cancellationToken))
            return Conflict(new { message = "Salary structure code already exists." });
        var structure = new SalaryStructure { TenantId = tenantId, Code = req.Code.Trim(), Name = req.Name.Trim(), Currency = req.Currency ?? "USD", EffectiveDate = req.EffectiveDate, CreatedBy = GetUserId() };
        _db.SalaryStructures.Add(structure);
        foreach (var component in req.Components ?? Array.Empty<SalaryComponentRequest>())
            _db.SalaryComponents.Add(new SalaryComponent { TenantId = tenantId, SalaryStructureId = structure.Id, Code = component.Code, Name = component.Name, ComponentType = component.ComponentType, CalculationType = component.CalculationType, Amount = component.Amount, Percentage = component.Percentage, IsTaxable = component.IsTaxable });
        await PayrollAudit("payroll.salary_structure.created", "SalaryStructure", structure.Id.ToString(), null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/payroll/salary-structures/{structure.Id}", structure);
    }

    [HttpPost("employee-salary-structures")]
    public async Task<IActionResult> AssignEmployeeSalary(EmployeeSalaryStructureRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // M3: reject zero/negative basic salary at assignment time
        if (req.BasicSalary <= 0)
            return BadRequest(new { message = "Basic salary must be greater than zero." });
        if (!await _db.Employees.AnyAsync(x => x.TenantId == tenantId && x.Id == req.EmployeeId && !x.IsDeleted, cancellationToken)) return BadRequest(new { message = "Employee not found." });
        if (!await _db.SalaryStructures.AnyAsync(x => x.TenantId == tenantId && x.Id == req.SalaryStructureId && !x.IsDeleted, cancellationToken)) return BadRequest(new { message = "Salary structure not found." });
        await _db.EmployeeSalaryStructures.Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false), cancellationToken);
        var assignment = new EmployeeSalaryStructure { TenantId = tenantId, EmployeeId = req.EmployeeId, SalaryStructureId = req.SalaryStructureId, BasicSalary = req.BasicSalary, HousingAllowance = req.HousingAllowance, TransportAllowance = req.TransportAllowance, FoodAllowance = req.FoodAllowance, MobileAllowance = req.MobileAllowance, OtherAllowance = req.OtherAllowance, FixedDeduction = req.FixedDeduction, EffectiveDate = req.EffectiveDate, Currency = req.Currency ?? "USD", CreatedBy = GetUserId() };
        _db.EmployeeSalaryStructures.Add(assignment);
        await PayrollAudit("payroll.employee_salary.assigned", "EmployeeSalaryStructure", assignment.Id.ToString(), new { employeeId = req.EmployeeId, basicSalary = req.BasicSalary }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/payroll/employee-salary-structures/{assignment.Id}", SalaryStructureAssignmentDto.Project(assignment, true));
    }

    [HttpGet("runs")]
    public async Task<IActionResult> ListRuns([FromQuery] Guid? companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var query = _db.PayrollRuns.Where(r => r.TenantId == tenantId);
        if (companyId.HasValue) query = query.Where(r => r.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return Ok(new PagedResult<PayrollRun>(items, total, page, pageSize));
    }

    [HttpPost("runs")]
    public async Task<IActionResult> CreateRun([FromBody] CreatePayrollRunRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (req.Month < 1 || req.Month > 12)
            return BadRequest(new { message = "Month must be between 1 and 12." });
        if (req.Year < 2000 || req.Year > 2100)
            return BadRequest(new { message = "Year is out of range." });
        if (req.CompanyId.HasValue && !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == req.CompanyId.Value && c.IsActive, cancellationToken))
            return BadRequest(new { message = "Company not found or not active." });
        // Guard at the period level — one run per (tenant, year, month) regardless of company_id.
        // A null company_id falls back to the first active company during Process(), so a
        // null-company run and an explicit-company run for the same period would double-pay.
        var existing = await _db.PayrollRuns
            .Where(r => r.TenantId == tenantId && r.Year == req.Year && r.Month == req.Month)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return Conflict(new { message = $"A payroll run for {req.Year}/{req.Month:D2} already exists.", runId = existing.Id });

        var run = new PayrollRun
        {
            TenantId = tenantId,
            CompanyId = req.CompanyId,
            Year = req.Year,
            Month = req.Month,
            CreatedByUserId = GetUserId(),
        };
        _db.PayrollRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/payroll/runs/{run.Id}", run);
    }

    [HttpPost("runs/{id:guid}/process")]
    public async Task<IActionResult> Process(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        // C2: approved runs cannot be silently overwritten
        if (run.Status is "Locked" or "Approved")
            return BadRequest(new { message = $"A run in '{run.Status}' status cannot be reprocessed. To reprocess, the approval must be revoked first." });

        var periodStart = new DateOnly(run.Year, run.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // Delete existing generated rows so reprocessing is idempotent.
        var existingSlips = _db.PayrollSlips.Where(s => s.RunId == id && s.TenantId == tenantId);
        _db.PayrollSlips.RemoveRange(existingSlips);
        _db.PayrollRunEmployees.RemoveRange(_db.PayrollRunEmployees.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        _db.PayrollEarnings.RemoveRange(_db.PayrollEarnings.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        _db.PayrollDeductions.RemoveRange(_db.PayrollDeductions.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));
        _db.PayrollValidationResults.RemoveRange(_db.PayrollValidationResults.Where(x => x.TenantId == tenantId && x.PayrollRunId == id));

        var employees = await _db.Employees.Where(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted).ToListAsync(cancellationToken);
        var salaryAssignments = await _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive).ToListAsync(cancellationToken);

        // Load salary structure components (for IsTaxable-based tax deduction)
        var structureIds = salaryAssignments.Select(x => x.SalaryStructureId).Distinct().ToList();
        var salaryComponents = await _db.SalaryComponents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SalaryStructureId.HasValue && structureIds.Contains(x.SalaryStructureId!.Value))
            .ToListAsync(cancellationToken);

        // Resolve company → country pack for statutory deduction.
        // CompanyId on the run determines which CountryCode + Jurisdiction drives GOSI/GPSSA/GRSIA.
        // If the run has no CompanyId (created without selecting one from the UI), fall back to the
        // tenant's first active company so the correct pack (KSA/UAE/Qatar) is still applied.
        var company = run.CompanyId.HasValue
            ? await _db.Companies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId.Value, cancellationToken)
            : await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.IsActive)
                .OrderBy(c => c.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

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

        // Income tax rate from System Settings (0 if not configured — GCC has no personal income tax by default)
        var taxRateSetting = await _db.SystemSettings.AsNoTracking()
            .Where(x => x.Category == "Payroll" && x.SettingKey == "IncomeTaxRate")
            .Select(x => x.SettingValue)
            .FirstOrDefaultAsync(cancellationToken);
        decimal.TryParse(taxRateSetting, out var incomeTaxRate); // 0 if unset
        var attendanceImpacts = await _db.AttendancePayrollImpacts.AsNoTracking().Where(x => x.TenantId == tenantId && x.WorkDate >= periodStart && x.WorkDate <= periodEnd && x.Status != "Processed").ToListAsync(cancellationToken);
        var leaveImpacts = await _db.LeavePayrollImpacts.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayPeriod == $"{run.Year}-{run.Month:00}" && x.Status != "Processed").ToListAsync(cancellationToken);

        // COMPLIANCE: Load active loans and salary advances per employee for EMI deduction
        var activeLoans = await _db.EmployeeLoans.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Status == "Active" && l.EmployeeIntId != null && l.OutstandingBalance > 0)
            .ToListAsync(cancellationToken);
        var activeAdvances = await _db.SalaryAdvances.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null && a.OutstandingBalance > 0)
            .ToListAsync(cancellationToken);

        // BONUS: Load approved bonuses for this pay period — consumed here, blocked from MarkBatchPaid.
        var periodStr = $"{run.Year}-{run.Month:D2}";
        var pendingBonuses = await _db.EmployeeBonuses
            .Where(x => x.TenantId == tenantId && !x.IsDeleted
                && x.Status == "Approved"
                && x.PaymentPeriod == periodStr
                && x.PayrollRunId == null
                && x.EmployeeIntId != null)
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

        // COMPLIANCE: YTD — sum of all locked runs in the same year (before this month)
        var ytdSlips = await _db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Join(_db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tenantId && r.Year == run.Year && r.Month < run.Month && r.Status == "Locked"),
                  s => s.RunId, r => r.Id, (s, r) => s)
            .ToListAsync(cancellationToken);

        // COMPLIANCE: Load payroll profiles for MolId / RoutingCode (keyed by Employee.Id)
        var payrollProfiles = await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .ToDictionaryAsync(p => p.EmployeeId, cancellationToken);

        // C4: filter overtime impacts to the current pay period only (via WorkDate on the originating request)
        var periodOvertimeRequestIds = await _db.OvertimeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.WorkDate >= periodStart && r.WorkDate <= periodEnd)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        var overtimeImpacts = await _db.OvertimePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != "Processed" && periodOvertimeRequestIds.Contains(x.OvertimeRequestId))
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
            .Where(r => r.TenantId == tenantId && r.WorkDate >= periodStart && r.WorkDate <= periodEnd)
            .Select(r => r.EmployeeId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        var slips = new List<PayrollSlip>();
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
            var overtimePay = otHours > 0m && hourlyRate > 0m
                ? Math.Round(otHours * hourlyRate * otMultiplier, 2)
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

            // BONUS: collect this employee's approved bonuses for the period.
            var empBonuses = bonusesByEmployee.TryGetValue(e.Id, out var eb) ? eb : new List<EmployeeBonus>();
            // Gross bonus amounts that are part of the social insurance base (e.g. GOSI/GPSSA/GRSIA).
            decimal gosiIncludedBonusTotal = empBonuses
                .Where(b => bonusTypeMap.TryGetValue(b.BonusTypeId, out var bt) && bt.IsIncludedInGosiBase)
                .Sum(b => b.GrossBonusAmount);
            // Net bonus earnings added to employee take-home this period.
            decimal totalBonusNet = empBonuses.Sum(b => b.BonusAmount);

            // Statutory deduction via country pack — rates from tenant-overridable StatutoryRule rows.
            // GosiCalculationService is retained for parity testing; it is no longer called in the run path.
            // GOSI-included bonus is added to housing slot so GosiCoveredWage = Basic + Housing + Bonus.
            var statutoryInput = new StatutoryDeductionInput(
                EmployeeId:   Guid.Empty, // Employee PK is int; Guid field not used in pack calculations
                CompanyId:    run.CompanyId ?? Guid.Empty,
                Salary:       new SalaryBreakdown(basic, housing + gosiIncludedBonusTotal, transport, otherAllowances),
                Nationality:  e.Nationality ?? string.Empty,
                ContractType: e.ContractType ?? "Indefinite",
                PeriodYear:   run.Year,
                PeriodMonth:  run.Month);
            var statutoryResult   = await deductionCalc.CalculateAsync(statutoryInput, cancellationToken);
            var gosiEmployeeTotal = statutoryResult.TotalEmployeeDeduction;

            // COMPLIANCE: Loan & advance EMI deduction
            var empLoans   = activeLoans.Where(l => l.EmployeeIntId == e.Id).ToList();
            var empAdv     = activeAdvances.Where(a => a.EmployeeIntId == e.Id).ToList();
            var loanEmi    = empLoans.Sum(l => Math.Min(l.InstallmentAmount, l.OutstandingBalance));
            var advEmi     = empAdv.Sum(a => Math.Min(a.InstallmentAmount, a.OutstandingBalance));
            var totalLoanDeduction = Math.Round(loanEmi + advEmi, 2);

            var deductions = fixedDeduction + attendanceDeduction + lopDeduction + leaveDeduction + taxDeduction + gosiEmployeeTotal + totalLoanDeduction;
            // C3: net salary cannot be negative (GCC labour law); engine Rule 3 will flag this.
            var netSalary = Math.Max(0m, gross + overtimePay + totalBonusNet - deductions);

            // COMPLIANCE: YTD — sum all locked slips for this employee earlier in the same year
            var empYtdSlips = ytdSlips.Where(s => s.EmployeeId == e.Id).ToList();
            var ytdGross    = empYtdSlips.Sum(s => s.GrossSalary);
            var ytdDeduct   = empYtdSlips.Sum(s => s.Deductions);
            var ytdNet      = empYtdSlips.Sum(s => s.NetSalary);

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
                OtherAllowances = otherAllowances + overtimePay + totalBonusNet,
                GrossSalary = gross + overtimePay + totalBonusNet,
                Deductions = deductions,
                NetSalary = netSalary,
                EmployeeStatutoryTotal = statutoryResult.TotalEmployeeDeduction,
                EmployerStatutoryTotal = statutoryResult.TotalEmployerContribution,
                LoanDeductions = totalLoanDeduction,
                YtdGross = ytdGross + gross + overtimePay,
                YtdDeductions = ytdDeduct + deductions,
                YtdNet = ytdNet + netSalary,
                Status = "Draft",
            };
            slips.Add(slip);
            _db.PayrollRunEmployees.Add(new PayrollRunEmployee { TenantId = tenantId, PayrollRunId = id, EmployeeId = e.Id, GrossEarnings = slip.GrossSalary, TotalDeductions = deductions, NetPay = slip.NetSalary });
            // Bonus earning lines (one per bonus in the batch, gross amount for GL expense tracking).
            foreach (var bonus in empBonuses)
                AddEarning(tenantId, id, e.Id, $"BONUS_{bonus.BonusTypeName.ToUpperInvariant().Replace(' ', '_')}", bonus.BonusTypeName, bonus.GrossBonusAmount, "Bonus");
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
            if (fixedDeduction > 0) AddDeduction(tenantId, id, e.Id, "FIXED_DEDUCTION", "Fixed deduction", fixedDeduction, "Salary");
            if (taxDeduction > 0) AddDeduction(tenantId, id, e.Id, "INCOME_TAX", $"Income tax ({incomeTaxRate}%)", taxDeduction, "Tax");
            if (attendanceDeduction > 0) AddDeduction(tenantId, id, e.Id, "ATTENDANCE", "Late/early attendance deduction", attendanceDeduction, "Attendance");
            if (lopDeduction > 0)
                AddDeduction(tenantId, id, e.Id, "LOP_DEDUCTION",
                    $"Loss of Pay ({lopDays:N2} d × {Math.Round(lopDayRate, 2):N2}/d)",
                    lopDeduction, "Attendance");
            if (leaveDeduction > 0) AddDeduction(tenantId, id, e.Id, "LEAVE", "Leave deduction", leaveDeduction, "Leave");
            if (loanEmi > 0) AddDeduction(tenantId, id, e.Id, "LOAN_EMI", "Loan instalment", loanEmi, "Loan");
            if (advEmi > 0) AddDeduction(tenantId, id, e.Id, "ADVANCE_EMI", "Salary advance repayment", advEmi, "Loan");
            // Statutory deduction lines from pack — employee contributions reduce net pay.
            // Code/Label come from the pack: "GOSI-ANN-EE" / "GOSI Annuities (Employee)" for KSA,
            // "GPSSA-EE" / "GPSSA (Employee)" for UAE, "GRSIA-EE" / "GRSIA (Employee)" for Qatar.
            // Employer lines already carry ER-suffixed codes (e.g. "GOSI-ANN-ER", "GOSI-OH-ER") —
            // the GL router keys on .EndsWith("-ER") to route them to Social Insurance Employer Payable.
            foreach (var line in statutoryResult.Lines.Where(l => l.EmployeeAmount > 0))
                AddDeduction(tenantId, id, e.Id, line.Code, line.Label, line.EmployeeAmount, "Statutory");
            foreach (var line in statutoryResult.Lines.Where(l => l.EmployerAmount > 0))
                AddDeduction(tenantId, id, e.Id, line.Code, line.Label, line.EmployerAmount, "Statutory");
        }

        _db.PayrollSlips.AddRange(slips);
        run.Status = "Processed";
        run.ProcessedAtUtc = DateTime.UtcNow;
        run.ProcessedByUserId = GetUserId();
        run.EmployeeCount = slips.Count;
        run.TotalGrossSalary = slips.Sum(s => s.GrossSalary);
        run.TotalDeductions = slips.Sum(s => s.Deductions);
        run.TotalNetSalary = slips.Sum(s => s.NetSalary);
        run.TotalEmployerStatutoryCost = slips.Sum(s => s.EmployerStatutoryTotal);
        await _db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId && x.WorkDate >= periodStart && x.WorkDate <= periodEnd && x.Status != "Processed").ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"), cancellationToken);
        await _db.LeavePayrollImpacts.Where(x => x.TenantId == tenantId && x.PayPeriod == $"{run.Year}-{run.Month:00}" && x.Status != "Processed").ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);
        await _db.OvertimePayrollImpacts
            .Where(x => x.TenantId == tenantId && x.Status != "Processed" && periodOvertimeRequestIds.Contains(x.OvertimeRequestId))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed").SetProperty(p => p.PayrollRunId, id).SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow), cancellationToken);

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

        var validationCtx = new PayrollValidationContext(
            run, slips, employees, salaryAssignments, valProfiles, valDeductions, valEarnings, company)
        {
            OvertimeHoursByEmployee      = otHoursByEmpForValidation,
            AttendanceProcessedEmployeeIds = attendanceProcessedEmpIds,
        };
        foreach (var r in PayrollValidationEngine.Run(validationCtx))
            _db.PayrollValidationResults.Add(r);

        var activeLoansMutable = await _db.EmployeeLoans
            .Where(l => l.TenantId == tenantId && l.Status == "Active" && l.EmployeeIntId != null && l.OutstandingBalance > 0)
            .ToListAsync(cancellationToken);
        var activeAdvMutable = await _db.SalaryAdvances
            .Where(a => a.TenantId == tenantId && a.Status == "Active" && a.EmployeeIntId != null && a.OutstandingBalance > 0)
            .ToListAsync(cancellationToken);

        foreach (var loan in activeLoansMutable)
        {
            var deducted = Math.Min(loan.InstallmentAmount, loan.OutstandingBalance);
            if (deducted <= 0) continue;
            loan.OutstandingBalance -= deducted;
            if (loan.OutstandingBalance <= 0) loan.Status = "Closed";
            // Record the paid installment
            var inst = await _db.LoanInstallments.FirstOrDefaultAsync(i => i.LoanId == loan.Id && i.Status == "Pending", cancellationToken);
            if (inst is not null) { inst.Status = "Paid"; inst.PaidDate = DateOnly.FromDateTime(DateTime.UtcNow); inst.PayrollRunId = id; inst.AmountPaid = deducted; }
        }
        foreach (var adv in activeAdvMutable)
        {
            var deducted = Math.Min(adv.InstallmentAmount, adv.OutstandingBalance);
            if (deducted <= 0) continue;
            adv.OutstandingBalance -= deducted;
            if (adv.OutstandingBalance <= 0) adv.Status = "Closed";
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
            await _db.EmployeeBonuses
                .Where(b => consumedBonusIds.Contains(b.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "PaidInPayroll")
                    .SetProperty(b => b.PayrollRunId, id), cancellationToken);
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

        await PayrollAudit("payroll.run.processed", "PayrollRun", run.Id.ToString(), new { employeeCount = slips.Count, totalNet = run.TotalNetSalary, bonusesConsumed = toConsumeBonuses.Count }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(run);
    }

    [HttpPost("runs/{id:guid}/lock")]
    public async Task<IActionResult> Lock(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status is not ("Processed" or "Approved" or "PendingFinanceReview")) return BadRequest(new { message = "Only processed, pending finance review, or approved runs can be locked." });

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
        var period = $"{run.Year}-{run.Month:D2}";
        var alreadyPosted = await _db.FinanceGlEntries
            .AnyAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == id && x.TenantId == tenantId, cancellationToken);
        if (!alreadyPosted)
        {
            var earnings   = await _db.PayrollEarnings.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.PayrollRunId == id).ToListAsync(cancellationToken);
            var dedxns     = await _db.PayrollDeductions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.PayrollRunId == id).ToListAsync(cancellationToken);
            var totalNet   = run.TotalNetSalary;
            var uid        = GetUserId();
            var uname      = GetUserName();
            var (glLines, totalDebits, totalCredits) = BuildPayrollGlEntries(
                tenantId, id, period, earnings, dedxns, totalNet, uid, uname);
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
        await _db.PayrollSlips.Where(s => s.RunId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Final"), cancellationToken);
        await _db.Payslips.Where(s => s.PayrollRunId == id && s.TenantId == tenantId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsPublishedToEss, true).SetProperty(p => p.PublishedAtUtc, DateTime.UtcNow), cancellationToken);
        await PayrollAudit("payroll.run.locked", "PayrollRun", id.ToString(), new { glPosted = !alreadyPosted, period }, cancellationToken);
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
        return Ok(run);
    }

    [HttpGet("runs/{id:guid}/slips")]
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
    public async Task<IActionResult> Validate(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();
        if (run.Status == "Draft")
            return BadRequest(new { message = "Run must be Processed before it can be validated." });

        // Load all data needed by the engine and re-run from scratch.
        var slips       = await _db.PayrollSlips.AsNoTracking().Where(s => s.TenantId == tenantId && s.RunId == id).ToListAsync(cancellationToken);
        var employees   = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted).ToListAsync(cancellationToken);
        var salaries    = await _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive).ToListAsync(cancellationToken);
        var profiles    = await _db.EmployeePayrollProfiles.AsNoTracking().Where(p => p.TenantId == tenantId && !p.IsDeleted).ToListAsync(cancellationToken);
        var deductions  = await _db.PayrollDeductions.AsNoTracking().Where(d => d.TenantId == tenantId && d.PayrollRunId == id).ToListAsync(cancellationToken);
        var earnings    = await _db.PayrollEarnings.AsNoTracking().Where(e => e.TenantId == tenantId && e.PayrollRunId == id).ToListAsync(cancellationToken);
        var company     = run.CompanyId.HasValue
            ? await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == run.CompanyId.Value, cancellationToken)
            : await _db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && c.IsActive).OrderBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);

        // Load OT/attendance data so Rules 10+11 fire correctly on /validate too.
        var valPeriodStart = new DateOnly(run.Year, run.Month, 1);
        var valPeriodEnd   = valPeriodStart.AddMonths(1).AddDays(-1);
        var valOtRequestIds = await _db.OvertimeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.WorkDate >= valPeriodStart && r.WorkDate <= valPeriodEnd)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        var valOtImpacts = await _db.OvertimePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && valOtRequestIds.Contains(x.OvertimeRequestId))
            .ToListAsync(cancellationToken);
        var valAttendanceEmpIds = (await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.WorkDate >= valPeriodStart && r.WorkDate <= valPeriodEnd)
            .Select(r => r.EmployeeId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        var valOtHoursByEmp = valOtImpacts
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));

        var ctx     = new PayrollValidationContext(run, slips, employees, salaries, profiles, deductions, earnings, company)
        {
            OvertimeHoursByEmployee        = valOtHoursByEmp,
            AttendanceProcessedEmployeeIds = valAttendanceEmpIds,
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
    [Authorize(Roles = "Admin,HR Manager,Finance Approver,Finance Controller,Payroll Manager")]
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
    [Authorize(Roles = "Admin,Finance Controller,Finance Approver")]
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
    [Authorize(Roles = "Admin,Finance Controller")]
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

        return Ok(new
        {
            runId             = id,
            period            = result.Period,
            status            = "Voided",
            glEntriesReversed = result.GlReversed,
            reason            = req.Notes,
        });
    }

    [HttpGet("runs/{id:guid}/gl-journal")]
    [Authorize(Roles = "Admin,HR Manager,Finance Approver,Finance Controller,Payroll Manager")]
    public async Task<IActionResult> GlJournal(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (run is null) return NotFound();

        var earnings  = await _db.PayrollEarnings.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken);
        var deductions = await _db.PayrollDeductions.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).ToListAsync(cancellationToken);
        var totalNet   = await _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == id).SumAsync(x => x.NetSalary, cancellationToken);

        // Build entries using source-based routing — works for both legacy codes and new pack codes
        // (e.g. "GOSI-ANN-EE", "GPSSA-ER", "GRSIA-EE") without per-code dictionary changes.
        var entries = new List<(string Code, string Name, string Account, string AccountName, string EntryType, decimal Amount)>();

        foreach (var grp in earnings.GroupBy(e => e.ComponentCode))
        {
            var first = grp.First();
            var (acct, aName) = (first.Source, grp.Key) switch
            {
                ("Bonus", _)            => ("6100", "Employee Bonus Expense"),
                (_, "BASIC")            => ("5001", "Basic Salary Expense"),
                (_, "HOUSING")          => ("5002", "Housing Allowance Expense"),
                (_, "TRANSPORT")        => ("5003", "Transport Allowance Expense"),
                (_, "OTHER_ALLOWANCES") => ("5004", "Other Allowances Expense"),
                (_, "OVERTIME")         => ("5005", "Overtime Expense"),
                _                       => ("5099", $"Other Earnings — {grp.Key}"),
            };
            entries.Add((grp.Key, first.ComponentName, acct, aName, "DR", grp.Sum(e => e.Amount)));
        }

        var employerStatutoryTotal = 0m;
        foreach (var grp in deductions.GroupBy(d => new { d.ComponentCode, d.Source }))
        {
            var first = grp.First();
            var isEr  = first.Source == "Statutory" && first.ComponentCode.EndsWith("-ER");
            if (isEr) employerStatutoryTotal += grp.Sum(d => d.Amount);

            var (acct, aName) = (first.Source, isEr) switch
            {
                ("Statutory", true)  => ("2106", "Social Insurance Employer Payable"),
                ("Statutory", false) => ("2101", "Social Insurance Payable (Employee)"),
                ("Tax", _)           => ("2102", "Income Tax Payable"),
                ("Loan", _)          => ("2107", "Loan & Advance Deductions Payable"),
                ("Attendance", _)    => ("2104", "Attendance Adjustment Payable"),
                ("Leave", _)         => ("2105", "Leave Deduction Payable"),
                _ => first.ComponentCode switch
                {
                    "FIXED_DEDUCTION" => ("2103", "Fixed Deductions Payable"),
                    _                 => ("2199", $"Other Deductions — {first.ComponentCode}"),
                },
            };
            entries.Add((grp.Key.ComponentCode, first.ComponentName, acct, aName, "CR", grp.Sum(d => d.Amount)));
        }

        // Employer statutory expense DR balances the CR 2106 liability posted above.
        if (employerStatutoryTotal > 0)
            entries.Add(("SOCIAL_INS_ER_DR", "Employer Social Insurance Expense", "5101", "Employer Social Insurance Expense", "DR", employerStatutoryTotal));

        // Net salary payable CR balances all earning DRs net of deduction CRs.
        entries.Add(("NET_SALARY", "Net Salary Payable", "2100", "Salaries Payable", "CR", totalNet));

        var totalDebits  = entries.Where(e => e.EntryType == "DR").Sum(e => e.Amount);
        var totalCredits = entries.Where(e => e.EntryType == "CR").Sum(e => e.Amount);

        return Ok(new
        {
            runId    = id,
            period   = $"{run.Year}-{run.Month:D2}",
            entries  = entries.Select(e => new { componentCode = e.Code, componentName = e.Name, glAccount = e.Account, glAccountName = e.AccountName, entryType = e.EntryType, amount = e.Amount }),
            totalDebits, totalCredits,
            isBalanced = Math.Abs(totalDebits - totalCredits) < 0.01m
        });
    }

    [HttpPost("runs/{id:guid}/payslips/generate")]
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
        var batch    = new PayrollPaymentBatch
        {
            TenantId      = tenantId,
            PayrollRunId  = id,
            BatchNumber   = $"PAY-{run.Year}{run.Month:00}-{DateTime.UtcNow:HHmmss}",
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
        await PayrollAudit("payroll.payment_batch.created", "PayrollPaymentBatch", batch.Id.ToString(), new { totalAmount = batch.TotalAmount, method = batch.PaymentMethod }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/payroll/payment-batches/{batch.Id}", batch);
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

        var result = Infrastructure.Payroll.WpsSifValidator.Validate(run, slips, profiles, employees);

        return Ok(new
        {
            runId        = id,
            canExport    = result.CanExport,
            errorCount   = result.ErrorCount,
            warningCount = result.WarningCount,
            blockingErrors = result.BlockingErrors,
            warnings       = result.Warnings,
        });
    }

    /// <summary>
    /// Generates the SIF file for a WPS payment batch using the isolated SifFileGenerator.
    /// Stores metadata (hash, format version, employee count, total) on WPSFileBatch.
    /// Blocks re-generation once a file has been created — use retry after Rejected if needed.
    /// Requires payroll.export permission.
    /// </summary>
    [HttpPost("payment-batches/{id:guid}/wps-file")]
    public async Task<IActionResult> GenerateWps(Guid id, CancellationToken cancellationToken)
    {
        if (!HasPermission("payroll.export")) return Forbid();

        var tenantId = GetTenantId();
        var batch = await _db.PayrollPaymentBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (batch is null) return NotFound();

        // Idempotency guard: block silent re-generation of an existing file.
        if (await _db.WPSFileBatches.AnyAsync(x => x.TenantId == tenantId && x.PaymentBatchId == id, cancellationToken))
            return Conflict(new
            {
                error   = "already_generated",
                message = "A WPS file already exists for this batch. Download the existing file or update the batch status to Rejected to allow a new export.",
            });

        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == batch.PayrollRunId, cancellationToken);

        // Run-level eligibility check (backend-enforced, never trusts frontend).
        if (run is null || (run.Status is not ("Approved" or "Locked" or "Paid")))
            return BadRequest(new { error = "run_not_exportable", message = "Payroll run must be Approved (or Locked/Paid) before WPS export." });

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
        var validation = Infrastructure.Payroll.WpsSifValidator.Validate(run, slips, profiles, employees);
        if (!validation.CanExport)
            return BadRequest(new
            {
                error          = "validation_failed",
                message        = "WPS export blocked by validation errors. Resolve all blocking issues and retry.",
                errorCount     = validation.ErrorCount,
                blockingErrors = validation.BlockingErrors,
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

        batch.WpsStatus = req.Status;
        await PayrollAudit("payroll.wps.status_changed", "PayrollPaymentBatch", batchId.ToString(),
            new { from, to = req.Status, notes = req.Notes }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { batchId, wpsStatus = batch.WpsStatus });
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
        if (!await _db.PayrollPaymentBatches.AnyAsync(x => x.TenantId == tenantId && x.Id == batchId, cancellationToken))
            return NotFound();

        var history = await _db.WPSFileBatches.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PaymentBatchId == batchId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Ok(new { batchId, exportCount = history.Count, history });
    }

    [HttpGet("employee-salary-structures")]
    public async Task<IActionResult> ListEmployeeSalaryStructures([FromQuery] int? employeeId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // M4: scope to allowed employees
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var query = _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (employeeId.HasValue) query = query.Where(x => x.EmployeeId == employeeId.Value);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var structs = await query.OrderByDescending(x => x.EffectiveDate).ToListAsync(cancellationToken);
        return Ok(structs.Select(s => SalaryStructureAssignmentDto.Project(s, true)).ToList());
    }

    [HttpGet("payment-batches")]
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
    public async Task<IActionResult> ListRunApprovals(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // SAFE-SERIALIZATION: PayrollApproval is a workflow record (who approved, when, status) — no salary amounts.
        return Ok(await _db.PayrollApprovals.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == id).OrderByDescending(x => x.DecidedAtUtc).ToListAsync(cancellationToken));
    }

    [HttpGet("groups")]
    public async Task<IActionResult> ListGroups(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        // SAFE-SERIALIZATION: PayrollGroup is config (Code, Name, Currency) — no personal PII.
        return Ok(await _db.PayrollGroups.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive).ToListAsync(cancellationToken));
    }

    [HttpPost("groups")]
    public async Task<IActionResult> CreateGroup(PayrollGroupRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var group = new PayrollGroup { TenantId = tenantId, Code = req.Code.Trim(), Name = req.Name.Trim(), Currency = req.Currency ?? "USD" };
        _db.PayrollGroups.Add(group);
        await _db.SaveChangesAsync(cancellationToken);
        // SAFE-SERIALIZATION: PayrollGroup is config (Code, Name, Currency) — no personal PII.
        return Created($"/api/payroll/groups/{group.Id}", group);
    }

    // H3: salary register is scoped — managers cannot see all employee salaries
    [HttpGet("reports/register")]
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
        Response.Headers["Content-Disposition"] = $"attachment; filename=salary-register-{run.Year}-{run.Month:D2}.csv";
        return Content(csv, "text/csv");
    }

    [HttpGet("reports/summary")]
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
    public async Task<IActionResult> CalculateEosb([FromBody] EosbCalculationRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) return NotFound(new { message = "Employee not found." });

        var gcc = await _db.GCCComplianceSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (gcc is null || !gcc.EosbEnabled)
            return BadRequest(new { message = "EOSB is not enabled for this tenant. Enable it in GCC Settings first." });

        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive)
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);

        var eligibleSalary = salary?.BasicSalary ?? employee.Salary ?? 0m;
        var joiningDate = employee.JoiningDate;
        var calcDate = req.AsOfDate ?? DateTime.UtcNow;
        var totalYears = (calcDate - joiningDate).Days / 365.0;
        var minYears = gcc.EosbMinYears > 0 ? gcc.EosbMinYears : 1;

        if (totalYears < minYears)
            return Ok(new { employeeId = req.EmployeeId, employeeName = employee.FullName, eligibleSalary, totalYears, eosbAmount = 0m, message = $"Employee has {totalYears:F1} years of service. Minimum required: {minYears} year(s)." });

        var dailySalary = eligibleSalary * 12 / 365m;
        var monthlySalary = eligibleSalary;

        decimal eosbAmount;
        string eosbFormula;

        // COMPLIANCE: KSA rule — Saudi Labor Law Art. 84 (month-fraction, not day-based).
        // Applies when GCCComplianceSetting.CountryCode is "SA" and rates are at UAE defaults (i.e. not explicitly overridden for KSA).
        // KSA: ≤5 yrs → (1/3 month × years), 5-10 yrs → (2/3 month × years), 10+ yrs → (1 month × years)
        bool isKsa = string.Equals(gcc.CountryCode, "SA", StringComparison.OrdinalIgnoreCase);
        bool uaeRatesUnchanged = (gcc.EosbYears1To5Rate is <= 0 or 21m) && (gcc.EosbYearsAbove5Rate is <= 0 or 30m);

        if (isKsa && uaeRatesUnchanged)
        {
            // KSA: 1/3 month per year (≤5 yrs), 2/3 month per year (5-10 yrs), 1 full month per year (10+ yrs)
            // Saudi rule is applied on TOTAL years (no marginal stacking between brackets)
            if (totalYears <= 5)
            {
                eosbAmount = monthlySalary * (1m / 3m) * (decimal)totalYears;
                eosbFormula = "KSA_ART84_LT5";
            }
            else if (totalYears <= 10)
            {
                eosbAmount = monthlySalary * (2m / 3m) * (decimal)totalYears;
                eosbFormula = "KSA_ART84_5TO10";
            }
            else
            {
                eosbAmount = monthlySalary * 1m * (decimal)totalYears;
                eosbFormula = "KSA_ART84_ABOVE10";
            }
        }
        else
        {
            // UAE / configurable day-based: 21 days/yr (≤5) then 30 days/yr (>5), marginal stacking
            var rate1 = gcc.EosbYears1To5Rate > 0 ? gcc.EosbYears1To5Rate : 21m;
            var rate2 = gcc.EosbYearsAbove5Rate > 0 ? gcc.EosbYearsAbove5Rate : 30m;
            if (totalYears <= 5)
                eosbAmount = dailySalary * rate1 * (decimal)totalYears;
            else
                eosbAmount = dailySalary * rate1 * 5 + dailySalary * rate2 * (decimal)(totalYears - 5);
            eosbFormula = isKsa ? "KSA_CUSTOM" : "UAE_DEFAULT";
        }

        eosbAmount = Math.Round(eosbAmount, 2);

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
            currency = salary?.Currency ?? "USD",
            message = $"Calculated EOSB/Gratuity for {employee.FullName}: {salary?.Currency ?? "USD"} {eosbAmount:N2}"
        });
    }

    [HttpGet("eosb/list")]
    public async Task<IActionResult> ListEosb([FromQuery] int? employeeId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var query = _db.EOSBCalculations.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (employeeId.HasValue) query = query.Where(x => x.EmployeeId == employeeId.Value);
        var eosbList = await query.OrderByDescending(x => x.CalculationDate).ToListAsync(cancellationToken);
        return Ok(eosbList.Select(EosbCalculationDto.Project).ToList());
    }

    [HttpGet("ai-validation")]
    public async Task<IActionResult> AiValidation([FromQuery] Guid runId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var warnings = await _db.PayrollValidationResults.AsNoTracking().Where(x => x.TenantId == tenantId && x.PayrollRunId == runId).ToListAsync(cancellationToken);
        return Ok(new { advisoryOnly = true, warnings, summary = "AI payroll validation is advisory. It does not approve payroll or change salaries automatically." });
    }

    private void AddEarning(Guid tenantId, Guid runId, int employeeId, string code, string name, decimal amount, string source) =>
        _db.PayrollEarnings.Add(new PayrollEarning { TenantId = tenantId, PayrollRunId = runId, EmployeeId = employeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source });

    private void AddDeduction(Guid tenantId, Guid runId, int employeeId, string code, string name, decimal amount, string source) =>
        _db.PayrollDeductions.Add(new PayrollDeduction { TenantId = tenantId, PayrollRunId = runId, EmployeeId = employeeId, ComponentCode = code, ComponentName = name, Amount = amount, Source = source });

    // Builds the double-entry GL lines for a payroll run.
    // Uses Source-based routing so new pack codes (GOSI-ANN-EE, GPSSA-EE, etc.) map correctly
    // without requiring changes to the component code dictionary as new packs are added.
    // Returns: (lines, totalDebits, totalCredits).
    private static (List<FinanceGlEntry> Lines, decimal TotalDebits, decimal TotalCredits) BuildPayrollGlEntries(
        Guid tenantId, Guid runId, string period,
        List<PayrollEarning> earnings, List<PayrollDeduction> deductions,
        decimal totalNetSalary, Guid? postedBy, string postedByName)
    {
        var lines = new List<FinanceGlEntry>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // ── Earnings (Debit side) ──────────────────────────────────────────────
        foreach (var grp in earnings.GroupBy(e => e.ComponentCode))
        {
            var (acct, acctName) = (grp.First().Source, grp.Key) switch
            {
                ("Bonus", _)        => ("6100", "Employee Bonus Expense"),
                (_, "BASIC")        => ("5001", "Basic Salary Expense"),
                (_, "HOUSING")      => ("5002", "Housing Allowance Expense"),
                (_, "TRANSPORT")    => ("5003", "Transport Allowance Expense"),
                (_, "OTHER_ALLOWANCES") => ("5004", "Other Allowances Expense"),
                (_, "OVERTIME")     => ("5005", "Overtime Expense"),
                _                   => ("5099", $"Other Earnings — {grp.Key}"),
            };
            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = period, EventType = "PayrollLock",
                DebitAccount  = $"{acct} - {acctName}", CreditAccount = string.Empty,
                Amount = grp.Sum(e => e.Amount), Currency = "USD",
                EntryDate = today, Period = period,
                Description = $"Payroll earning: {acctName}",
                PostedBy = postedBy, PostedByName = postedByName,
            });
        }

        // ── Deductions (Credit side) ──────────────────────────────────────────
        decimal employerStatutoryTotal = 0m;
        foreach (var grp in deductions.GroupBy(d => new { d.ComponentCode, d.Source }))
        {
            var isEmployerSide = grp.Key.Source == "Statutory" && grp.Key.ComponentCode.EndsWith("-ER");
            if (isEmployerSide)
                employerStatutoryTotal += grp.Sum(d => d.Amount); // aggregated into DR/CR pair below

            var (acct, acctName) = (grp.Key.Source, isEmployerSide) switch
            {
                ("Statutory", true)  => ("2106", "Social Insurance Employer Payable"),
                ("Statutory", false) => ("2101", "Social Insurance Payable (Employee)"),
                ("Tax", _)           => ("2102", "Income Tax Payable"),
                ("Loan", _)          => ("2107", "Loan & Advance Deductions Payable"),
                ("Attendance", _)    => ("2104", "Attendance Adjustment Payable"),
                ("Leave", _)         => ("2105", "Leave Deduction Payable"),
                _ => grp.Key.ComponentCode switch
                {
                    "FIXED_DEDUCTION" => ("2103", "Fixed Deductions Payable"),
                    _                 => ("2199", $"Other Deductions — {grp.Key.ComponentCode}"),
                },
            };
            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = period, EventType = "PayrollLock",
                DebitAccount = string.Empty, CreditAccount = $"{acct} - {acctName}",
                Amount = grp.Sum(d => d.Amount), Currency = "USD",
                EntryDate = today, Period = period,
                Description = $"Payroll deduction: {acctName}",
                PostedBy = postedBy, PostedByName = postedByName,
            });
        }

        // Employer statutory contribution: DR expense to balance the CR liability above.
        if (employerStatutoryTotal > 0)
            lines.Add(new FinanceGlEntry
            {
                TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = runId,
                SourceEntityRef = period, EventType = "PayrollLock",
                DebitAccount = "5101 - Employer Social Insurance Expense", CreditAccount = string.Empty,
                Amount = employerStatutoryTotal, Currency = "USD",
                EntryDate = today, Period = period,
                Description = "Employer statutory contributions (social insurance)",
                PostedBy = postedBy, PostedByName = postedByName,
            });

        // Net salary payable balances all DR earnings vs. CR deductions.
        lines.Add(new FinanceGlEntry
        {
            TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = runId,
            SourceEntityRef = period, EventType = "PayrollLock",
            DebitAccount = string.Empty, CreditAccount = "2100 - Salaries Payable",
            Amount = totalNetSalary, Currency = "USD",
            EntryDate = today, Period = period,
            Description = "Net salaries payable",
            PostedBy = postedBy, PostedByName = postedByName,
        });

        var totalDebits  = lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var totalCredits = lines.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        return (lines, totalDebits, totalCredits);
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
        await Task.CompletedTask;
    }

    // ── Salary Structure Export / Import / Template ───────────────────────────
    private static readonly string[] SalaryStructureCsvHeaders =
        { "Code", "Name", "Currency", "EffectiveDate" };

    [HttpGet("structures/export")]
    [HttpGet("salary-structures/export")]
    public async Task<IActionResult> ExportStructures(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var structures = await _db.SalaryStructures
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var rows = structures.Select(s => (IReadOnlyList<object?>)new object?[]
        {
            s.Code, s.Name, s.Currency, s.EffectiveDate.ToString("yyyy-MM-dd")
        });
        var csv = Csv.Build(SalaryStructureCsvHeaders, rows);
        Response.Headers["Content-Disposition"] = "attachment; filename=salary_structures_export.csv";
        return Content(csv, "text/csv");
    }

    [HttpGet("structures/import-template")]
    [HttpGet("salary-structures/import-template")]
    public IActionResult StructuresImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=salary_structures_import_template.csv";
        return Content(Csv.Template(SalaryStructureCsvHeaders), "text/csv");
    }

    [HttpPost("structures/import")]
    [HttpPost("salary-structures/import")]
    public async Task<IActionResult> ImportStructures([FromBody] ImportSalaryStructuresRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);
        int created = 0, skipped = 0;
        var errors = new List<string>();
        var rowNum = 1;
        foreach (var row in rows)
        {
            rowNum++;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) { skipped++; continue; }
            if (await _db.SalaryStructures.AnyAsync(x => x.TenantId == tenantId && x.Code == code && !x.IsDeleted, cancellationToken))
            { skipped++; errors.Add($"Row {rowNum}: Code '{code}' already exists."); continue; }
            DateOnly.TryParse(row.GetValueOrDefault("EffectiveDate", string.Empty), out var effectiveDate);
            if (effectiveDate == default) effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow);
            _db.SalaryStructures.Add(new SalaryStructure
            {
                TenantId = tenantId,
                Code = code,
                Name = name,
                Currency = row.GetValueOrDefault("Currency", "USD"),
                EffectiveDate = effectiveDate,
                CreatedBy = GetUserId()
            });
            created++;
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
        var priorRun = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Year == priorYear && x.Month == priorMonth, cancellationToken);

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

    /// <summary>Final settlement calculator: pro-rata salary + EOSB + leave encashment - notice deduction.</summary>
    [HttpPost("final-settlement")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> FinalSettlement([FromBody] FinalSettlementRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == req.EmployeeId && !e.IsDeleted, cancellationToken);
        if (employee is null) return NotFound(new { message = "Employee not found." });

        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive)
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);
        var basicSalary    = salary?.BasicSalary ?? employee.Salary ?? 0m;
        var grossSalary    = basicSalary + (salary?.HousingAllowance ?? 0m) + (salary?.TransportAllowance ?? 0m)
                           + (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
        var currency       = salary?.Currency ?? "USD";

        // Pro-rata salary for partial month
        var lastDay        = req.LastWorkingDay;
        var daysInMonth    = DateTime.DaysInMonth(lastDay.Year, lastDay.Month);
        var dailyGross     = grossSalary / daysInMonth;
        var proRataSalary  = Math.Round(dailyGross * lastDay.Day, 2);

        // EOSB / Gratuity (reuse GCC compliance settings — KSA/UAE formula auto-selected)
        var gcc = await _db.GCCComplianceSettings.AsNoTracking().Where(x => x.TenantId == tenantId).FirstOrDefaultAsync(cancellationToken);
        var calcDate    = lastDay.ToDateTime(TimeOnly.MinValue);
        var totalYears  = (calcDate - employee.JoiningDate).Days / 365.0;
        var minYears    = gcc?.EosbMinYears > 0 ? gcc!.EosbMinYears : 1;
        var dailySalary = basicSalary * 12 / 365m;
        decimal eosbAmount = 0m;
        if (totalYears >= minYears)
        {
            bool settIsKsa = string.Equals(gcc?.CountryCode, "SA", StringComparison.OrdinalIgnoreCase);
            bool settUaeDefault = (gcc?.EosbYears1To5Rate is null or <= 0 or 21m) && (gcc?.EosbYearsAbove5Rate is null or <= 0 or 30m);
            if (settIsKsa && settUaeDefault)
            {
                // KSA Art. 84: fraction-of-month × total-years, tiered on total service
                eosbAmount = totalYears <= 5
                    ? Math.Round(basicSalary * (1m / 3m) * (decimal)totalYears, 2)
                    : totalYears <= 10
                        ? Math.Round(basicSalary * (2m / 3m) * (decimal)totalYears, 2)
                        : Math.Round(basicSalary * 1m * (decimal)totalYears, 2);
            }
            else
            {
                var rate1 = gcc?.EosbYears1To5Rate > 0 ? gcc!.EosbYears1To5Rate : 21m;
                var rate2 = gcc?.EosbYearsAbove5Rate > 0 ? gcc!.EosbYearsAbove5Rate : 30m;
                eosbAmount = totalYears <= 5
                    ? Math.Round(dailySalary * rate1 * (decimal)totalYears, 2)
                    : Math.Round(dailySalary * rate1 * 5 + dailySalary * rate2 * (decimal)(totalYears - 5), 2);
            }
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
            eosbAmount, totalYears = Math.Round(totalYears, 2),
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

    /// <summary>
    /// Resolves the tenant's payroll currency.
    /// Priority: Company.DefaultCurrency → TenantLocalizationSetting.CurrencyCode → "USD"
    /// </summary>
    private async Task<string> ResolveCurrencyAsync(Guid tenantId, CancellationToken ct)
    {
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.IsActive)
            .Select(c => c.DefaultCurrency)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(company)) return company;

        var loc = await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.CurrencyCode)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(loc) ? "USD" : loc;
    }

    // ── Payroll Command Center ────────────────────────────────────────────────────

    [HttpGet("companies")]
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

        var result = companies.Select(c =>
        {
            var empCount = employeesByCompany.FirstOrDefault(x => x.CompanyId == c.Id)?.Count ?? 0;
            var salaryCount = salaryAssignedByCompany.FirstOrDefault(x => x.CompanyId == c.Id)?.Count ?? 0;
            var run = runsForMonth.FirstOrDefault(r => r.CompanyId == c.Id);
            var valErr = validationErrors.FirstOrDefault(v => v.CompanyId == c.Id);
            var pendAppr = pendingApprovals.FirstOrDefault(p => p.CompanyId == c.Id);
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

        var runQuery = _db.PayrollRuns.Where(r => r.TenantId == tenantId && r.Year == targetYear && r.Month == targetMonth);
        if (companyId.HasValue) runQuery = runQuery.Where(r => r.CompanyId == companyId);
        var run = await runQuery.FirstOrDefaultAsync(cancellationToken);

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
        });
    }

    // ── Employee Salary Import / Export ───────────────────────────────────────────

    private static readonly string[] EmployeeSalaryCsvHeaders =
        { "EmployeeCode", "SalaryStructureCode", "BasicSalary", "HousingAllowance", "TransportAllowance", "FoodAllowance", "MobileAllowance", "OtherAllowance", "FixedDeduction", "Currency", "EffectiveDate" };

    [HttpGet("employee-salaries")]
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
    public IActionResult EmployeeSalariesImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=employee_salaries_import_template.csv";
        return Content(Csv.Template(EmployeeSalaryCsvHeaders), "text/csv");
    }

    [HttpPost("employee-salaries/import")]
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
            .ToDictionaryAsync(s => s.Code, cancellationToken);

        foreach (var row in rows)
        {
            rowNum++;
            var empCode = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            var structCode = row.GetValueOrDefault("SalaryStructureCode", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(empCode)) { skipped++; continue; }
            if (!allEmployees.TryGetValue(empCode, out var employee))
            { errors.Add($"Row {rowNum}: Employee code '{empCode}' not found."); skipped++; continue; }
            if (!string.IsNullOrWhiteSpace(structCode) && !allStructures.TryGetValue(structCode, out _))
            { errors.Add($"Row {rowNum}: Salary structure code '{structCode}' not found."); skipped++; continue; }
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
            var structure = !string.IsNullOrWhiteSpace(structCode) ? allStructures[structCode] : null;

            await _db.EmployeeSalaryStructures
                .Where(s => s.TenantId == tenantId && s.EmployeeId == employee.Id && s.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), cancellationToken);

            _db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId,
                EmployeeId = employee.Id,
                SalaryStructureId = structure?.Id ?? Guid.Empty,
                BasicSalary = basic,
                HousingAllowance = housing,
                TransportAllowance = transport,
                FoodAllowance = food,
                MobileAllowance = mobile,
                OtherAllowance = other,
                FixedDeduction = deduction,
                EffectiveDate = effectiveDate,
                Currency = currency,
                CreatedBy = GetUserId(),
            });
            created++;
        }

        await PayrollAudit("payroll.employee_salary.imported", "EmployeeSalary", "bulk", new { received = rows.Count, created, skipped, errors = errors.Count }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { received = rows.Count, created, updated, skipped, errors = errors.Take(20) });
    }

    // ── Admin payslip PDF download ─────────────────────────────────────────────
    // id is the Payslip.Id (formal payslip record) — the same ID the payslips table returns.
    // The endpoint resolves the matching PayrollSlip row via RunId + EmployeeId.
    [HttpGet("slips/{id:guid}/pdf")]
    [Authorize(Roles = "Admin,HR Manager,Finance Approver,Payroll Manager,Payroll Officer")]
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
    [Authorize(Roles = "Admin,HR Manager,Finance Approver,Payroll Manager,Payroll Officer")]
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
}

public record CreatePayrollRunRequest(int Year, int Month, Guid? CompanyId = null);
public record SalaryStructureRequest(string Code, string Name, string? Currency, DateOnly EffectiveDate, IReadOnlyCollection<SalaryComponentRequest>? Components);
public record SalaryComponentRequest(string Code, string Name, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable);
public record EmployeeSalaryStructureRequest(int EmployeeId, Guid SalaryStructureId, decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance, decimal FoodAllowance, decimal MobileAllowance, decimal OtherAllowance, decimal FixedDeduction, DateOnly EffectiveDate, string? Currency);
public record PayrollDecisionRequest(string? Notes);
public record PayrollPaymentBatchRequest(string? PaymentMethod, string? Currency);
public record WpsStatusRequest(string Status, string? Notes);
public record PayrollGroupRequest(string Code, string Name, string? Currency);
public record ImportSalaryStructuresRequest(string CsvContent);
public record EosbCalculationRequest(int EmployeeId, DateTime? AsOfDate);
public record FinalSettlementRequest(int EmployeeId, DateOnly LastWorkingDay, int NoticePeriodDaysShort = 0);
