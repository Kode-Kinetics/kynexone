using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Compliance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Controllers;

/// <summary>
/// "Rules below the legal minimum": every configured rule that sits below a statutory minimum, whether or not an
/// administrator has acknowledged it, so nothing is hidden (owner decision 2026-10-25: statutory values are defaults,
/// lower is allowed with an audited acknowledgement). Read-only; assembled live from the leave policies, the
/// end-of-service rates and the company payroll rules the caller may see.
/// </summary>
[ApiController]
[Route("api/compliance")]
[Authorize(Roles = "Admin,HR Manager")]
public class FloorComplianceController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public FloorComplianceController(ZayraDbContext db) => _db = db;

    public sealed record BelowMinimumItem(
        string Source, string Code, Guid? CompanyId, string? CompanyName, string Rule, string RuleAr, string? Subject,
        decimal? Configured, decimal? Statutory, string? Citation, string Message, string? MessageAr,
        bool Acknowledged, string? AcknowledgedBy, DateTime? AcknowledgedAtUtc, string? Note);

    [HttpGet("below-minimum")]
    public async Task<IActionResult> BelowMinimum(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = this.GetEntityScope();
        bool Sees(Guid? companyId) => scope.IsGroupLevel || (companyId is Guid c && scope.CanAccessCompany(c));
        var companies = await _db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .Select(c => new { c.Id, c.LegalNameEn, c.CountryCode }).ToListAsync(ct);
        string? NameOf(Guid? id) => id is null ? null : companies.FirstOrDefault(c => c.Id == id)?.LegalNameEn;
        var items = new List<BelowMinimumItem>();

        // Leave policies.
        var types = await _db.LeaveTypes.AsNoTracking().Where(t => t.TenantId == tenantId).ToListAsync(ct);
        var policies = await _db.LeavePolicies.AsNoTracking().Where(p => p.TenantId == tenantId && p.Status != "Archived").ToListAsync(ct);
        foreach (var policy in policies.Where(p => p.CompanyId is null ? scope.IsGroupLevel : Sees(p.CompanyId)))
        {
            var type = types.FirstOrDefault(t => t.Id == policy.LeaveTypeId);
            if (type is null) continue;
            var stored = FloorAckStore.Parse(policy.BelowMinimumAcksJson);
            foreach (var check in (await KsaLeaveFloorChecks.ChecksAsync(_db, tenantId.Value, type, policy, ct)).Where(c => c.Below))
            {
                var w = check.Warning;
                var ack = FloorAckStore.Covering(stored, w);
                items.Add(new("leave_policy", w.Code, policy.CompanyId, NameOf(policy.CompanyId), $"{type.NameEn}: {check.Label}", $"{type.NameAr}: {check.LabelAr}",
                    policy.Name, w.ConfiguredValue, w.StatutoryValue, w.Citation, w.Message, w.MessageAr,
                    ack is not null, ack?.AcknowledgedBy, ack?.AcknowledgedAtUtc, ack?.Reason));
            }
        }

        // End-of-service rates.
        var rules = new StatutoryRuleReader(_db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var gccRows = await _db.GCCComplianceSettings.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.IsActive && g.EosbEnabled).ToListAsync(ct);
        foreach (var g in gccRows.Where(g => g.CompanyId is null ? scope.IsGroupLevel : Sees(g.CompanyId)))
        {
            var stored = FloorAckStore.Parse(g.EosbBelowFloorAcksJson);
            foreach (var check in (await KsaEosbFloorChecks.ChecksAsync(rules, g.CountryCode, g.EosbYears1To5Rate, g.EosbYearsAbove5Rate, today, ct)).Where(c => c.Below))
            {
                var w = check.Warning;
                var ack = FloorAckStore.Covering(stored, w);
                items.Add(new("eosb", w.Code, g.CompanyId, NameOf(g.CompanyId), $"End of service: {check.Label}", $"مكافأة نهاية الخدمة: {check.LabelAr}",
                    g.CountryCode, w.ConfiguredValue, w.StatutoryValue, w.Citation, w.Message, w.MessageAr,
                    ack is not null, ack?.AcknowledgedBy, ack?.AcknowledgedAtUtc, ack?.Reason));
            }
        }

        // Company payroll rules (their own acknowledgement flow; listed here so one screen shows everything).
        foreach (var company in companies.Where(c => Sees(c.Id)))
            foreach (var v in (await CompanyPayrollRules.ListAsync(_db, tenantId.Value, company.Id, ct)).Where(v => v.WarningCode is not null))
            {
                decimal? configured = decimal.TryParse(v.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                decimal? statutory = v.WarningCode == CompanyPayrollRules.WarnBelowOvertimeFloor ? CompanyPayrollRules.StatutoryOvertimeFloor : null;
                items.Add(new("payroll_rule", v.WarningCode!, company.Id, company.LegalNameEn, v.Key, v.Key, null, configured, statutory,
                    null, v.Warning ?? string.Empty, null, v.Acknowledged, null, v.AcknowledgedAtUtc, v.AcknowledgementNote));
            }

        return Ok(new { items = items.OrderBy(i => i.CompanyName).ThenBy(i => i.Source).ThenBy(i => i.Rule).ToList() });
    }
}
