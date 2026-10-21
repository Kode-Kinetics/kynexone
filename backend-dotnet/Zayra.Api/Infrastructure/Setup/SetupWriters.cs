using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Setup;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>
/// The setup writers that more than one door uses, extracted from <c>SetupAssistantController.ApplyCore</c> so
/// the setup assistant and the create-company wizard cannot drift into two rules for the same row:
/// the company's public-holiday calendar and its employee-ID rule. Each method only TRACKS changes; the caller
/// saves them in its own unit of work (and its own transaction), with its own audit rows.
/// </summary>
public static class SetupWriters
{
    public sealed record HolidayCalendarWrite(PublicHolidayCalendar Calendar, bool CalendarCreated, int HolidaysAdded);

    /// <summary>
    /// The calendar owned by <paramref name="companyId"/> (or the explicitly tenant-wide one when null) for the
    /// draft's year, created when absent, topped up with the draft's holidays it does not already hold. It never
    /// discovers a sibling or shared calendar merely because country and year match, and never rewrites a date
    /// already on it.
    /// </summary>
    public static async Task<HolidayCalendarWrite> UpsertHolidayCalendarAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, Guid? branchId, string? countryCode,
        DraftHolidayCalendar draft, CancellationToken ct)
    {
        var year = draft.CalendarYear;
        var country = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        var calendar = await db.PublicHolidayCalendars.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.CountryCode == country && x.CalendarYear == year && x.CompanyId == companyId, ct);
        var created = false;
        if (calendar is null)
        {
            calendar = new PublicHolidayCalendar
            {
                TenantId = tenantId,
                Name = draft.Name,
                CountryCode = country,
                CompanyId = companyId,
                BranchId = branchId,
                CalendarYear = year,
                IsActive = true,
            };
            db.PublicHolidayCalendars.Add(calendar);
            created = true;
        }

        var existingDates = created
            ? new HashSet<DateOnly>()
            : (await db.PublicHolidays.Where(x => x.TenantId == tenantId && x.CalendarId == calendar.Id)
                .Select(x => x.Date).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var h in draft.Holidays)
        {
            if (!DateOnly.TryParse(h.Date, out var date)) continue;
            if (!existingDates.Add(date)) continue;
            db.PublicHolidays.Add(new PublicHoliday
            {
                TenantId = tenantId,
                CalendarId = calendar.Id,
                NameEn = h.NameEn,
                NameAr = h.NameAr ?? string.Empty,
                Date = date,
                IsRecurring = h.IsRecurring,
                IsOptional = h.IsOptional,
                HolidayType = string.IsNullOrWhiteSpace(h.HolidayType) ? "National" : h.HolidayType,
                Notes = h.Notes ?? string.Empty,
            });
            added++;
        }
        return new HolidayCalendarWrite(calendar, created, added);
    }

    /// <summary>
    /// The employee-ID rule for <paramref name="companyId"/> (null = the tenant-wide rule), created when absent and
    /// otherwise updated to the draft's shape. Returns whether a row was created.
    /// </summary>
    public static async Task<(EmployeeIdRule Rule, bool Created)> UpsertEmployeeIdRuleAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, DraftEmployeeIdRule draft, Guid? actorUserId, CancellationToken ct)
    {
        var rule = await db.EmployeeIdRules.FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.CompanyId == companyId, ct);
        var created = rule is null;
        if (rule is null)
        {
            rule = new EmployeeIdRule { TenantId = tenantId, CompanyId = companyId, CreatedBy = actorUserId };
            db.EmployeeIdRules.Add(rule);
        }
        rule.CompanyPrefix = draft.CompanyPrefix;
        rule.UseCountryPrefix = draft.UseCountryPrefix;
        rule.UseBranchPrefix = draft.UseBranchPrefix;
        rule.UseDepartmentPrefix = draft.UseDepartmentPrefix;
        rule.UseYear = draft.UseYear;
        rule.PaddingLength = draft.PaddingLength;
        rule.NextSequence = Math.Max(1, draft.NextSequence);
        rule.AllowManualOverride = draft.AllowManualOverride;
        rule.IsActive = true;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedBy = actorUserId;
        return (rule, created);
    }
}
