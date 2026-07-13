using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.AI;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/ai")]
[Authorize]
public class AIAssistantController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAiAdvisoryService _aiAdvisoryService;
    private readonly IDataScopeService _scopeService;
    private readonly AiOptions _aiOptions;

    public AIAssistantController(ZayraDbContext db, IAiAdvisoryService aiAdvisoryService, IDataScopeService scopeService, AiOptions aiOptions)
    {
        _db = db;
        _aiAdvisoryService = aiAdvisoryService;
        _scopeService = scopeService;
        _aiOptions = aiOptions;
    }

    // ── AI provider status — checked by the frontend before showing the UI ───
    // Returns enabled=false when AI_PROVIDER=none/fallback or the chosen provider isn't usable
    // (no API key for anthropic/openai, or no base URL for ollama).
    // No special permission required; any authenticated user may query this.
    [HttpGet("status")]
    [AllowAnonymous]  // front-end fetches this before the auth redirect cycle completes in some flows
    public IActionResult Status() =>
        Ok(new { enabled = _aiOptions.IsLiveProviderConfigured, provider = _aiOptions.EffectiveProvider });

    // ── AI HR Query (live DB context, advisory only) ─────────────────────────

    [HttpPost("query")]
    public async Task<IActionResult> Query([FromBody] AIQueryRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (!HasPermission("ai.query")) return Forbid();

        var userId = this.GetUserId();
        var roles = User.Claims
            .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();
        var permissions = User.Claims
            .Where(c => c.Type == "permission")
            .Select(c => c.Value)
            .ToList();
        var callerEmployeeId = GetEmployeeId();

        // Resolve data scope — enforces tenant isolation and manager team scope.
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        // Employee role: can only query their own data.
        var isEmployee = roles.Count == 1 && roles.Contains("Employee");
        if (isEmployee)
        {
            if (callerEmployeeId is null) return Forbid();
            // Override any requested employeeId to caller's own.
            req = req with { EmployeeId = callerEmployeeId.Value };
        }
        else if (!scope.IsUnrestricted && scope.AllowedEmployeeIds is not null)
        {
            // Manager/Supervisor: validate requested employeeId is within team.
            if (req.EmployeeId.HasValue && !scope.AllowedEmployeeIds.Contains(req.EmployeeId.Value))
                return Forbid();
        }

        // Per-tenant monthly usage limit check.
        var limitCheck = await CheckUsageLimitAsync(tenantId.Value, ct);
        if (limitCheck is not null) return limitCheck;

        var userContext = new AiUserContext(tenantId.Value, userId, roles, permissions, isEmployee ? callerEmployeeId : req.EmployeeId)
        {
            ScopeEmployeeIds = scope.IsUnrestricted ? null : scope.AllowedEmployeeIds?.ToList()
        };

        var response = await _aiAdvisoryService.QueryAsync(userContext, req, ct);

        return Ok(response);
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task<IActionResult?> CheckUsageLimitAsync(Guid tenantId, CancellationToken ct)
    {
        var sub = await _db.TenantSubscriptions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Status == "Active")
            .OrderByDescending(s => s.StartedAtUtc)
            .FirstOrDefaultAsync(ct);
        var plan = sub?.Plan ?? "Starter";
        var limit = AiPlanLimits.GetMonthlyTokenLimit(plan);
        if (limit == 0) return null; // Enterprise = unlimited

        var yearMonth = int.Parse(DateTime.UtcNow.ToString("yyyyMM"));
        var usage = await _db.TenantAiUsages
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.YearMonth == yearMonth, ct);

        if (usage is not null && usage.TokensUsed >= limit)
        {
            return StatusCode(429, new
            {
                error = "ai_usage_limit_exceeded",
                message = $"Your {plan} plan has reached its monthly AI token limit ({limit:N0} tokens). Upgrade your plan to continue.",
                tokensUsed = usage.TokensUsed,
                monthlyLimit = limit
            });
        }
        return null;
    }

    // ── AI Insights ──────────────────────────────────────────────────────────

    [HttpGet("insights")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Manager,Finance Controller,Finance Approver")]
    public async Task<IActionResult> ListInsights(
        [FromQuery] string? module,
        [FromQuery] string? severity,
        [FromQuery] bool? acknowledged,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.AIInsights.Where(i => i.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(module)) query = query.Where(i => i.Module == module);
        if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(i => i.Severity == severity);
        if (acknowledged.HasValue) query = query.Where(i => i.IsAcknowledged == acknowledged.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpPost("insights/{id:guid}/acknowledge")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> AcknowledgeInsight(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var insight = await _db.AIInsights
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (insight is null) return NotFound();

        insight.IsAcknowledged = true;
        insight.AcknowledgedBy = this.GetUserId();
        insight.AcknowledgedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(insight);
    }

    // ── Employee Risk Scores ─────────────────────────────────────────────────

    [HttpGet("risk-scores")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> RiskScores(
        [FromQuery] string? riskLevel,
        [FromQuery] string? department,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.EmployeeRiskScores.Where(r => r.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(riskLevel)) query = query.Where(r => r.OverallRiskLevel == riskLevel);
        if (!string.IsNullOrWhiteSpace(department)) query = query.Where(r => r.DepartmentName == department);

        var items = await query
            .OrderByDescending(r => r.ChurnRiskScore)
            .Take(100)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("risk-scores/compute")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ComputeRiskScores(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var ninetyDaysAgo = today.AddDays(-90);

        var employees = await _db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .ToListAsync(ct);

        var results = new List<EmployeeRiskScore>();

        foreach (var emp in employees)
        {
            var absenceCount = await _db.AbsenceRecords
                .CountAsync(a => a.TenantId == tenantId && a.EmployeeId == emp.Id
                    && a.AbsenceDate >= ninetyDaysAgo, ct);

            var overtimeMinutes = await _db.OvertimeRequests
                .Where(o => o.TenantId == tenantId && o.EmployeeId == emp.Id
                    && o.Status == "Approved"
                    && o.WorkDate >= ninetyDaysAgo)
                .SumAsync(o => (decimal?)o.ApprovedMinutes ?? 0, ct);
            var overtimeHours = overtimeMinutes / 60m;

            var yearsOfService = (DateTime.UtcNow - emp.JoiningDate).TotalDays / 365.0;
            // Heuristic scoring (advisory only, not automated decisions)
            var churnRisk = Math.Min(100m, absenceCount * 8m + (yearsOfService < 1 ? 30m : 0m));
            var burnoutRisk = Math.Min(100m, overtimeHours * 1.2m);
            var combinedRisk = (churnRisk + burnoutRisk) / 2m;
            var overallLevel = combinedRisk switch
            {
                > 70m => "Critical",
                > 50m => "High",
                > 30m => "Medium",
                _ => "Low"
            };

            var existing = await _db.EmployeeRiskScores
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.EmployeeId == emp.Id, ct);

            if (existing is null)
            {
                existing = new EmployeeRiskScore { TenantId = tenantId.Value, EmployeeId = emp.Id };
                _db.EmployeeRiskScores.Add(existing);
            }

            existing.EmployeeName = emp.FullName;
            existing.DepartmentName = emp.Department ?? string.Empty;
            existing.ChurnRiskScore = churnRisk;
            existing.BurnoutRiskScore = burnoutRisk;
            existing.PerformanceDeclineScore = 0;
            existing.OverallRiskLevel = overallLevel;
            existing.Recommendations = GenerateRiskRecommendation(overallLevel, absenceCount, overtimeHours);
            existing.ComputedAtUtc = DateTime.UtcNow;
            existing.IsAdvisoryOnly = true;

            results.Add(existing);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { computed = results.Count, message = "Risk scores computed. All results are advisory only." });
    }

    // ── Query History ────────────────────────────────────────────────────────

    [HttpGet("query-history")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> QueryHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var total = await _db.AIHRQueryLogs.CountAsync(l => l.TenantId == tenantId, ct);
        var items = await _db.AIHRQueryLogs
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    // ── Payroll AI Validation ────────────────────────────────────────────────

    [HttpGet("payroll-validation/{payrollRunId:guid}")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> PayrollValidation(Guid payrollRunId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var results = await _db.PayrollAIValidationResults
            .Where(r => r.TenantId == tenantId && r.PayrollRunId == payrollRunId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(results);
    }

    [HttpPost("payroll-validation/{payrollRunId:guid}/run")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> RunPayrollValidation(Guid payrollRunId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var runExists = await _db.PayrollRuns
            .AnyAsync(r => r.Id == payrollRunId && r.TenantId == tenantId, ct);
        if (!runExists) return NotFound();

        var employees = await _db.PayrollRunEmployees
            .Where(e => e.TenantId == tenantId && e.PayrollRunId == payrollRunId)
            .Join(_db.Employees.Where(emp => emp.TenantId == tenantId),
                pre => pre.EmployeeId, emp => emp.Id,
                (pre, emp) => new { pre.EmployeeId, EmployeeName = emp.FullName, pre.GrossEarnings, pre.TotalDeductions, pre.NetPay })
            .ToListAsync(ct);

        // Remove old results for this run
        var old = await _db.PayrollAIValidationResults
            .Where(r => r.TenantId == tenantId && r.PayrollRunId == payrollRunId)
            .ToListAsync(ct);
        _db.PayrollAIValidationResults.RemoveRange(old);

        var findings = new List<PayrollAIValidationResult>();

        foreach (var emp in employees)
        {
            if (emp.NetPay <= 0)
            {
                findings.Add(new PayrollAIValidationResult
                {
                    TenantId = tenantId.Value,
                    PayrollRunId = payrollRunId,
                    EmployeeId = emp.EmployeeId,
                    EmployeeName = emp.EmployeeName,
                    ValidationType = "NegativeNetPay",
                    Severity = "Critical",
                    Message = $"Employee {emp.EmployeeName} has zero or negative net pay ({emp.NetPay:C}). Review deductions.",
                    IsAdvisoryOnly = true
                });
            }

            if (emp.GrossEarnings > 0 && emp.TotalDeductions > emp.GrossEarnings * 0.5m)
            {
                findings.Add(new PayrollAIValidationResult
                {
                    TenantId = tenantId.Value,
                    PayrollRunId = payrollRunId,
                    EmployeeId = emp.EmployeeId,
                    EmployeeName = emp.EmployeeName,
                    ValidationType = "ExcessiveDeductions",
                    Severity = "Warning",
                    Message = $"Deductions ({emp.TotalDeductions:C}) exceed 50% of gross earnings ({emp.GrossEarnings:C}) for {emp.EmployeeName}.",
                    IsAdvisoryOnly = true
                });
            }
        }

        _db.PayrollAIValidationResults.AddRange(findings);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            payrollRunId,
            totalEmployees = employees.Count,
            findings = findings.Count,
            critical = findings.Count(f => f.Severity == "Critical"),
            warnings = findings.Count(f => f.Severity == "Warning"),
            message = "Validation complete. All findings are advisory only — no payroll changes were made automatically.",
            results = findings
        });
    }

    private int? GetEmployeeId() =>
        int.TryParse(User.FindFirst("employee_id")?.Value, out var id) ? id : null;

    private static string GenerateRiskRecommendation(string level, int absenceCount, decimal overtimeHours) =>
        level switch
        {
            "Critical" => $"Immediate manager check-in recommended. Employee shows {absenceCount} absences and {overtimeHours:F0}h overtime in past 90 days.",
            "High" => $"Schedule a wellbeing conversation. {absenceCount} absences and {overtimeHours:F0}h overtime detected.",
            "Medium" => "Monitor attendance and workload patterns for the next 30 days.",
            _ => "Employee engagement appears stable. Continue regular check-ins."
        };
}
