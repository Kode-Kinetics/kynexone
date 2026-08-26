using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Compliance;

[Authorize]
[ApiController]
[Route("api/compliance/reports")]
public class ComplianceReportsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public ComplianceReportsController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    // GET /api/compliance/reports/dashboard
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var tid = GetTenantId();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var in30 = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var in60 = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60));
        var in90 = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90));

        // Active contracts
        var activeContracts = await _db.EmployeeContracts
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active", ct);

        // Expiring visas (30 / 60 / 90 days)
        var visasExpiring30 = await _db.VisaRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted
                && x.ExpiryDate >= today && x.ExpiryDate <= in30
                && x.Status == "Active", ct);

        var visasExpiring60 = await _db.VisaRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted
                && x.ExpiryDate > in30 && x.ExpiryDate <= in60
                && x.Status == "Active", ct);

        var passportsExpiring90 = await _db.PassportRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted
                && x.ExpiryDate >= today && x.ExpiryDate <= in90
                && x.Status == "Active", ct);

        var expiredVisas = await _db.VisaRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.ExpiryDate < today && x.Status == "Active", ct);

        var pendingRenewals = await _db.ComplianceRenewals
            .CountAsync(x => x.TenantId == tid && (x.Status == "Pending" || x.Status == "InProgress"), ct);

        var passportsHeldByCompany = await _db.PassportRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.IsHeldByCompany && x.Status == "Active", ct);

        return Ok(new
        {
            activeContracts,
            visasExpiring30,
            visasExpiring60,
            passportsExpiring90,
            expiredVisas,
            pendingRenewals,
            passportsHeldByCompany,
        });
    }

    // GET /api/compliance/reports/employee-identity-integrity
    // Surfaces legacy GUID-only rows that could not be proven against Employee.PublicId. These rows
    // are never repaired by name; an administrator must reconcile them using authoritative source IDs.
    [HttpGet("employee-identity-integrity")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> EmployeeIdentityIntegrity(CancellationToken ct)
    {
        var tid = GetTenantId();
        var contracts = await _db.EmployeeContracts.CountAsync(x => x.TenantId == tid && !x.IsDeleted &&
            !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted && e.PublicId == x.EmployeeId), ct);
        var visas = await _db.VisaRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted &&
            !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted && e.PublicId == x.EmployeeId), ct);
        var passports = await _db.PassportRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted &&
            !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted && e.PublicId == x.EmployeeId), ct);
        var workPermits = await _db.WorkPermitRecords.CountAsync(x => x.TenantId == tid && !x.IsDeleted &&
            !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted && e.PublicId == x.EmployeeId), ct);
        var renewals = await _db.ComplianceRenewals.CountAsync(x => x.TenantId == tid &&
            !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted && e.PublicId == x.EmployeeId), ct);
        var reminders = await _db.ComplianceReminders.CountAsync(x => x.TenantId == tid &&
            !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted && e.PublicId == x.EmployeeId), ct);
        var onboardingTasks = await _db.OnboardingTasks.CountAsync(x => x.TenantId == tid && x.EmployeeId.HasValue &&
            !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted && e.PublicId == x.EmployeeId.Value), ct);
        var loans = await _db.EmployeeLoans.CountAsync(x => x.TenantId == tid && !x.IsDeleted &&
            (!x.EmployeeIntId.HasValue || !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted &&
                e.Id == x.EmployeeIntId.Value && e.PublicId == x.EmployeeId)), ct);
        var advances = await _db.SalaryAdvances.CountAsync(x => x.TenantId == tid && !x.IsDeleted &&
            (!x.EmployeeIntId.HasValue || !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted &&
                e.Id == x.EmployeeIntId.Value && e.PublicId == x.EmployeeId)), ct);
        var bonuses = await _db.EmployeeBonuses.CountAsync(x => x.TenantId == tid && !x.IsDeleted &&
            (!x.EmployeeIntId.HasValue || !_db.Employees.Any(e => e.TenantId == tid && !e.IsDeleted &&
                e.Id == x.EmployeeIntId.Value && e.PublicId == x.EmployeeId)), ct);

        var total = contracts + visas + passports + workPermits + renewals + reminders + onboardingTasks + loans + advances + bonuses;
        return Ok(new
        {
            status = total == 0 ? "Healthy" : "RequiresReconciliation",
            totalUnresolved = total,
            counts = new { contracts, visas, passports, workPermits, renewals, reminders, onboardingTasks, loans, advances, bonuses },
            remediation = "Reconcile only from authoritative employee public/internal IDs; never infer from employee name.",
        });
    }

    // GET /api/compliance/reports/expiry-alerts
    [HttpGet("expiry-alerts")]
    public async Task<IActionResult> ExpiryAlerts([FromQuery] int withinDays = 90, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(withinDays));

        var visaAlerts = await _db.VisaRecords
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active"
                && x.ExpiryDate >= today && x.ExpiryDate <= cutoff)
            .Select(x => new { x.EmployeeId, x.EmployeeName, type = "Visa", subType = x.VisaType, x.ExpiryDate, daysLeft = (x.ExpiryDate.DayNumber - today.DayNumber) })
            .ToListAsync(ct);

        var passportAlerts = await _db.PassportRecords
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active"
                && x.ExpiryDate >= today && x.ExpiryDate <= cutoff)
            .Select(x => new { x.EmployeeId, x.EmployeeName, type = "Passport", subType = x.Nationality, x.ExpiryDate, daysLeft = (x.ExpiryDate.DayNumber - today.DayNumber) })
            .ToListAsync(ct);

        var permitAlerts = await _db.WorkPermitRecords
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active"
                && x.ExpiryDate >= today && x.ExpiryDate <= cutoff)
            .Select(x => new { x.EmployeeId, x.EmployeeName, type = "WorkPermit", subType = x.PermitType, x.ExpiryDate, daysLeft = (x.ExpiryDate.DayNumber - today.DayNumber) })
            .ToListAsync(ct);

        var contractAlerts = await _db.EmployeeContracts
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active"
                && x.EndDate.HasValue && x.EndDate.Value >= today && x.EndDate.Value <= cutoff)
            .Select(x => new { x.EmployeeId, x.EmployeeName, type = "Contract", subType = x.ContractType, ExpiryDate = x.EndDate!.Value, daysLeft = (x.EndDate!.Value.DayNumber - today.DayNumber) })
            .ToListAsync(ct);

        var all = visaAlerts.Cast<object>()
            .Concat(passportAlerts.Cast<object>())
            .Concat(permitAlerts.Cast<object>())
            .Concat(contractAlerts.Cast<object>())
            .OrderBy(x => x.GetType().GetProperty("daysLeft")?.GetValue(x))
            .ToList();

        return Ok(new { withinDays, total = all.Count, alerts = all });
    }

    // GET /api/compliance/reports/ai-insights
    [HttpGet("ai-insights")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> AIInsights(CancellationToken ct)
    {
        var tid = GetTenantId();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Expired (overdue) items
        var expiredVisas = await _db.VisaRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.ExpiryDate < today && x.Status == "Active", ct);

        var expiredPassports = await _db.PassportRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.ExpiryDate < today && x.Status == "Active", ct);

        var expiredPermits = await _db.WorkPermitRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.ExpiryDate < today && x.Status == "Active", ct);

        // Employees without passports
        var allEmpIds = await _db.Employees
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Active")
            .Select(x => x.Id).ToListAsync(ct);

        var empIdsWithPassport = await _db.PassportRecords
            .Where(x => x.TenantId == tid && !x.IsDeleted)
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct);

        var missingPassports = allEmpIds.Count - empIdsWithPassport.Count;

        // Passports held by company
        var heldPassports = await _db.PassportRecords
            .CountAsync(x => x.TenantId == tid && !x.IsDeleted && x.IsHeldByCompany && x.Status == "Active", ct);

        var insights = new List<object>
        {
            new
            {
                type = "ExpiryRisk", severity = expiredVisas > 0 ? "Critical" : "Low",
                title = "Expired Visa Records",
                description = $"[ADVISORY] {expiredVisas} employee visa record(s) have passed their expiry date with status still 'Active'. Legal and compliance risk — update or renew immediately.",
                isAdvisory = true
            },
            new
            {
                type = "ExpiryRisk", severity = expiredPassports > 0 ? "High" : "Low",
                title = "Expired Passport Records",
                description = $"[ADVISORY] {expiredPassports} passport record(s) are past expiry. Employees may face travel or residency issues.",
                isAdvisory = true
            },
            new
            {
                type = "MissingDocument", severity = missingPassports > 5 ? "Medium" : "Low",
                title = "Employees Without Passport Records",
                description = $"[ADVISORY] {missingPassports} active employees have no passport record in the system. Consider collecting and digitising for compliance.",
                isAdvisory = true
            },
            new
            {
                type = "ComplianceGap", severity = heldPassports > 0 ? "Medium" : "Low",
                title = "Passports Held by Company",
                description = $"[ADVISORY] {heldPassports} passports are marked as held by the company. This practice may violate labour law in certain jurisdictions. Please verify legal standing.",
                isAdvisory = true
            },
            new
            {
                type = "ExpiryRisk", severity = expiredPermits > 0 ? "Critical" : "Low",
                title = "Expired Work Permits",
                description = $"[ADVISORY] {expiredPermits} work permit record(s) have passed their expiry date. Employees working without valid permits risk legal penalties.",
                isAdvisory = true
            },
        };

        // Persist AI insights
        var now = DateTime.UtcNow;
        foreach (var i in insights.Cast<dynamic>())
        {
            _db.ComplianceAIInsights.Add(new ComplianceAIInsight
            {
                TenantId = tid, InsightType = i.type, Severity = i.severity,
                Title = i.title, Description = i.description,
                RecommendedAction = "Review and act on the identified compliance gap immediately.",
                IsAdvisory = true,
                GeneratedAtUtc = now,
                ExpiresAtUtc = now.AddDays(7),
            });
        }
        await _db.SaveChangesAsync(ct);

        return Ok(new { generatedAt = now, isAdvisory = true, insights });
    }

    // GET /api/compliance/reports/doc-types
    [HttpGet("/api/compliance/doc-types")]
    public async Task<IActionResult> ListDocTypes([FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.DocTypes.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (activeOnly) q = q.Where(x => x.IsActive);
        var items = await q.OrderBy(x => x.NameEn).ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("/api/compliance/doc-types")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateDocType([FromBody] CreateDocTypeRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var docType = new DocType
        {
            TenantId = tid, Code = req.Code, NameEn = req.NameEn, NameAr = req.NameAr ?? string.Empty,
            Category = req.Category, ExpiryRequired = req.ExpiryRequired,
            AlertDaysBeforeExpiry = req.AlertDaysBeforeExpiry, IsMandatory = req.IsMandatory,
            ApplicableCountries = req.ApplicableCountries ?? string.Empty,
        };
        _db.DocTypes.Add(docType);
        await _db.SaveChangesAsync(ct);
        return Ok(docType);
    }

    // GET /api/compliance/reports/requirements
    [HttpGet("/api/compliance/requirements")]
    public async Task<IActionResult> ListRequirements([FromQuery] string? countryCode = null, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.ComplianceRequirements.Where(x => x.TenantId == tid && x.IsActive);
        if (!string.IsNullOrEmpty(countryCode)) q = q.Where(x => x.CountryCode == countryCode);
        var items = await q.ToListAsync(ct);
        return Ok(items);
    }
}

public record CreateDocTypeRequest(
    string Code, string NameEn, string? NameAr, string Category,
    bool ExpiryRequired, int AlertDaysBeforeExpiry, bool IsMandatory, string? ApplicableCountries);
