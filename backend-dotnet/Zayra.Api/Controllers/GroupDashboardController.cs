using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers;

/// <summary>
/// Consolidated group overview: one row per ACCESSIBLE company. Scoped users see only
/// their granted companies (the ambient company filter plus explicit scope predicates);
/// group-scope users see every active company in the tenant. Aggregates come straight
/// from the operational tables — no warehouse, no fake placeholders.
/// </summary>
[ApiController]
[Route("api/group/dashboard")]
[Authorize(Roles = "Admin,HR Director,HR Manager,Finance Approver,Compliance Officer,Auditor")]
public class GroupDashboardController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public GroupDashboardController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = this.GetEntityScope();
        var companiesQuery = _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted);
        if (!scope.IsGroupLevel)
        {
            var accessible = scope.AccessibleCompanyIds;
            companiesQuery = companiesQuery.Where(c => accessible.Contains(c.Id));
        }
        var companies = await companiesQuery.OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
        var companyIds = companies.Select(c => c.Id).ToList();
        if (companyIds.Count == 0)
            return Ok(new { accountType = await AccountTypeAsync(tenantId.Value, ct), companies = Array.Empty<object>() });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthAgo = today.AddDays(-30);
        var docHorizon = today.AddDays(60);
        var pendingLeaveStatuses = new[] { "Submitted", "Pending", "PendingManagerApproval", "PendingHRApproval" };

        // One grouped query per metric — company filter is also enforced by the ambient
        // EF filter; the explicit predicates keep the aggregates index-friendly.
        var headcount = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active" && e.CompanyId != null && companyIds.Contains(e.CompanyId.Value))
            .GroupBy(e => e.CompanyId!.Value)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count, ct);

        var pendingLeave = await _db.LeaveRequests.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CompanyId != null && companyIds.Contains(l.CompanyId.Value) && pendingLeaveStatuses.Contains(l.Status))
            .GroupBy(l => l.CompanyId!.Value)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count, ct);

        var absences = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.CompanyId != null && companyIds.Contains(a.CompanyId.Value) && a.WorkDate >= monthAgo && a.Status == "Absent")
            .GroupBy(a => a.CompanyId!.Value)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count, ct);

        var pendingRegularizations = await _db.AttendanceRegularizationRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId != null && companyIds.Contains(r.CompanyId.Value) && r.Status == "Submitted")
            .GroupBy(r => r.CompanyId!.Value)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count, ct);

        var expiringDocs = await _db.EmployeeDocuments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.CompanyId != null && companyIds.Contains(d.CompanyId.Value)
                     && d.ExpiryDate != null && d.ExpiryDate >= today && d.ExpiryDate <= docHorizon)
            .GroupBy(d => d.CompanyId!.Value)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count, ct);

        var latestRuns = (await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.CompanyId != null && companyIds.Contains(r.CompanyId.Value))
                .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
                .Select(r => new { r.CompanyId, r.Status, r.Year, r.Month })
                .ToListAsync(ct))
            .GroupBy(r => r.CompanyId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var profiles = (await _db.CompanyComplianceProfiles.AsNoTracking()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.Status == Models.CompanyPolicyStatuses.Active
                         && p.CompanyId != null && companyIds.Contains(p.CompanyId.Value))
                .ToListAsync(ct))
            .GroupBy(p => p.CompanyId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.EffectiveFrom).First());

        var statutoryFields = await ComplianceReadinessCalculator.LoadEmployeeStatutoryFieldsAsync(_db, tenantId.Value, companyIds, ct);

        var rows = companies.Select(c =>
        {
            var run = latestRuns.GetValueOrDefault(c.Id);
            profiles.TryGetValue(c.Id, out var profile);
            var missingFieldCount = profile is null
                ? 0
                : ComplianceReadinessCalculator
                    .Evaluate(profile.RequiredFieldsJson, statutoryFields.GetValueOrDefault(c.Id) ?? [])
                    .Count(f => f.MissingEmployeeCount > 0);
            return new
            {
                companyId = c.Id,
                name = c.TradeName != "" ? c.TradeName : c.LegalNameEn,
                code = c.LegalNameEn,
                countryCode = c.CountryCode,
                isActive = c.IsActive,
                approvalStatus = c.ApprovalStatus,
                headcount = headcount.GetValueOrDefault(c.Id),
                latestPayrollStatus = run?.Status,
                latestPayrollPeriod = run is null ? null : $"{run.Year}-{run.Month:D2}",
                pendingLeave = pendingLeave.GetValueOrDefault(c.Id),
                absences30d = absences.GetValueOrDefault(c.Id),
                expiringDocs60d = expiringDocs.GetValueOrDefault(c.Id),
                pendingApprovals = pendingLeave.GetValueOrDefault(c.Id) + pendingRegularizations.GetValueOrDefault(c.Id),
                complianceReady = profile is not null && missingFieldCount == 0,
                complianceMissingCount = missingFieldCount,
            };
        }).ToList();

        return Ok(new { accountType = await AccountTypeAsync(tenantId.Value, ct), companies = rows });
    }

    private Task<string> AccountTypeAsync(Guid tenantId, CancellationToken ct) =>
        _db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.AccountType).FirstAsync(ct);
}
