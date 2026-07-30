using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.WorkWeek;

/// <summary>
/// Config-driven working-week resolver. Replaces the four divergent hard-coded weekend
/// literals (LeaveService, OvertimeController, ShiftsController) with one owner. This is a
/// SYSTEM read: <see cref="IgnoreQueryFilters"/> is used with an explicit TenantId (and,
/// where relevant, company) scope so payroll/leave background work resolves the same answer
/// regardless of the caller's company claims — it never reads another tenant's rows and
/// never accepts a caller-supplied weekend set.
/// </summary>
public sealed class WorkWeekService : IWorkWeekService
{
    private readonly ZayraDbContext _db;
    public WorkWeekService(ZayraDbContext db) => _db = db;

    public async Task<WorkWeekConfig> ResolveAsync(
        Guid tenantId, Guid? companyId, string? countryCode = null, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return WorkWeekConfig.GccDefault;

        // Resolve the effective country: explicit arg → company's country → null (skip country rule).
        var cc = countryCode;
        if (string.IsNullOrWhiteSpace(cc) && companyId is Guid cid)
        {
            // IgnoreQueryFilters is intentional: system read — the WHERE re-applies the exact tenant+company scope; never reads another tenant.
            cc = await _db.Companies.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == cid)
                .Select(c => c.CountryCode)
                .FirstOrDefaultAsync(ct);
        }

        // ── GCCComplianceSetting (company override → tenant default), country-matched first ──
        // IgnoreQueryFilters is intentional: system read — explicitly scoped by TenantId below; never reads another tenant.
        var settings = await _db.GCCComplianceSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new SettingRow(s.CompanyId, s.CountryCode, s.WeekendDays, s.WorkWeek))
            .ToListAsync(ct);

        bool CountryMatch(SettingRow r) =>
            string.IsNullOrWhiteSpace(cc) || string.Equals(r.CountryCode, cc, StringComparison.OrdinalIgnoreCase);

        var pick =
            (companyId is not null ? settings.FirstOrDefault(s => s.CompanyId == companyId && CountryMatch(s)) : null)
            ?? (companyId is not null ? settings.FirstOrDefault(s => s.CompanyId == companyId) : null)
            ?? settings.FirstOrDefault(s => s.CompanyId == null && CountryMatch(s))
            ?? settings.FirstOrDefault(s => s.CompanyId == null);

        if (pick is not null)
        {
            var wk = WorkWeekParser.ParseWeekendDays(pick.WeekendDays)
                     ?? WorkWeekParser.ParseWorkWeekToWeekend(pick.WorkWeek);
            if (wk is { Count: > 0 })
                return new WorkWeekConfig(wk, pick.CompanyId is not null ? "gcc:company" : "gcc:tenant");
        }

        // ── CountryPayrollRule.weekend_days for the resolved country ──
        if (!string.IsNullOrWhiteSpace(cc))
        {
            // IgnoreQueryFilters is intentional: system read — explicitly scoped by TenantId + country; never reads another tenant.
            var raw = await _db.CountryPayrollRules.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.CountryCode == cc && r.RuleKey == "weekend_days")
                .OrderByDescending(r => r.EffectiveFrom)
                .Select(r => r.RuleValue)
                .FirstOrDefaultAsync(ct);
            var wk = WorkWeekParser.ParseWeekendDays(raw);
            if (wk is { Count: > 0 })
                return new WorkWeekConfig(wk, "country_payroll_rule");
        }

        // ── Nothing configured: the single, well-known GCC fallback ──
        return WorkWeekConfig.GccDefault;
    }

    private sealed record SettingRow(Guid? CompanyId, string CountryCode, string WeekendDays, string WorkWeek);
}
