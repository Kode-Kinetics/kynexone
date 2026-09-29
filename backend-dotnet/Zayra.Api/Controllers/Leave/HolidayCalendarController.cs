using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/holidays")]
[Authorize]
public class HolidayCalendarController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public HolidayCalendarController(ZayraDbContext db) => _db = db;

    [HttpGet("calendars")]
    public async Task<IActionResult> ListCalendars([FromQuery] int? year, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.PublicHolidayCalendars.Where(c => c.TenantId == tenantId);
        if (year.HasValue) query = query.Where(c => c.CalendarYear == year.Value);

        var items = await query
            .OrderByDescending(c => c.CalendarYear)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("calendars")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateCalendar([FromBody] CreateCalendarRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var calendar = new PublicHolidayCalendar
        {
            TenantId = tenantId.Value,
            Name = req.Name,
            CountryCode = req.CountryCode,
            CompanyId = req.CompanyId,
            BranchId = req.BranchId,
            CalendarYear = req.CalendarYear,
            IsActive = true
        };

        _db.PublicHolidayCalendars.Add(calendar);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/leave/holidays/calendars/{calendar.Id}", calendar);
    }

    [HttpGet("calendars/{id:guid}/holidays")]
    public async Task<IActionResult> ListHolidays(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var calendarExists = await _db.PublicHolidayCalendars
            .AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (!calendarExists) return NotFound();

        var holidays = await _db.PublicHolidays
            .Where(h => h.TenantId == tenantId && h.CalendarId == id)
            .OrderBy(h => h.Date)
            .ToListAsync(ct);

        return Ok(holidays);
    }

    /// <summary>
    /// "Recurring" was a badge, not a behaviour. The flag was written, round-tripped and rendered as
    /// a blue "Recurring" pill on the holiday row — and nothing has ever expanded a recurring
    /// holiday into the next year. Calendars are per CalendarYear, and there is no scheduled job of
    /// any kind that could roll one forward (the product registers exactly one background job type,
    /// attendance.process). An HR admin ticks the box on National Day, skips it next January, and
    /// arrives at an empty calendar with every leave working-day count wrong from the first day of
    /// the year, while the badge still says it recurs.
    ///
    /// <para>Refused rather than quietly accepted, per the rule this codebase states in
    /// ApprovalPoliciesController. Hijri-dated holidays are the reason "just copy the calendar" is
    /// not a safe silent default: they move about eleven days a year and must be confirmed by a
    /// person, not computed behind one.</para>
    /// </summary>
    private IActionResult? RefuseRecurring(bool isRecurring)
        => isRecurring
            ? BadRequest(new
            {
                error = "holiday_recurrence_not_implemented",
                message =
                    "Holidays do not roll forward in this build. Marking one recurring would store a flag that "
                    + "nothing reads: calendars are per year, and no process creates next year's instance — so the "
                    + "badge would promise a rollover that never happens and next January's calendar would be empty. "
                    + "Create each year's calendar explicitly. Hijri-dated holidays move every year and must be "
                    + "confirmed rather than computed.",
            })
            : null;

    [HttpPost("calendars/{id:guid}/holidays")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> AddHoliday(Guid id, [FromBody] AddHolidayRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var calendarExists = await _db.PublicHolidayCalendars
            .AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (!calendarExists) return NotFound();

        if (RefuseRecurring(req.IsRecurring) is { } recurringRefusal) return recurringRefusal;

        var holiday = new PublicHoliday
        {
            TenantId = tenantId.Value,
            CalendarId = id,
            NameEn = req.NameEn,
            NameAr = req.NameAr ?? string.Empty,
            Date = req.Date,
            HijriDate = req.HijriDate ?? string.Empty,
            IsRecurring = req.IsRecurring,
            IsOptional = req.IsOptional,
            HolidayType = req.HolidayType ?? "National",
            Notes = req.Notes ?? string.Empty
        };

        _db.PublicHolidays.Add(holiday);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/leave/holidays/calendars/{id}/holidays", holiday);
    }

    [HttpPut("calendars/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdateCalendar(Guid id, [FromBody] CreateCalendarRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var calendar = await _db.PublicHolidayCalendars
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (calendar is null) return NotFound();

        calendar.Name = req.Name;
        calendar.CountryCode = req.CountryCode;
        calendar.CalendarYear = req.CalendarYear;

        await _db.SaveChangesAsync(ct);
        return Ok(calendar);
    }

    [HttpDelete("calendars/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DeleteCalendar(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var calendar = await _db.PublicHolidayCalendars
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (calendar is null) return NotFound();

        _db.PublicHolidayCalendars.Remove(calendar);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("holidays/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdateHoliday(Guid id, [FromBody] AddHolidayRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var holiday = await _db.PublicHolidays
            .FirstOrDefaultAsync(h => h.Id == id && h.TenantId == tenantId, ct);
        if (holiday is null) return NotFound();

        if (RefuseRecurring(req.IsRecurring) is { } recurringRefusal) return recurringRefusal;

        holiday.NameEn = req.NameEn;
        holiday.NameAr = req.NameAr ?? string.Empty;
        holiday.Date = req.Date;
        holiday.HijriDate = req.HijriDate ?? string.Empty;
        holiday.IsRecurring = req.IsRecurring;
        holiday.IsOptional = req.IsOptional;
        holiday.HolidayType = req.HolidayType ?? holiday.HolidayType;
        holiday.Notes = req.Notes ?? string.Empty;

        await _db.SaveChangesAsync(ct);
        return Ok(holiday);
    }

    [HttpDelete("holidays/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DeleteHoliday(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var holiday = await _db.PublicHolidays
            .FirstOrDefaultAsync(h => h.Id == id && h.TenantId == tenantId, ct);
        if (holiday is null) return NotFound();

        _db.PublicHolidays.Remove(holiday);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("today")]
    public async Task<IActionResult> Today(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var holiday = await _db.PublicHolidays
            .Where(h => h.TenantId == tenantId && h.Date == today)
            .FirstOrDefaultAsync(ct);

        return Ok(new { isHoliday = holiday is not null, holiday });
    }

    [HttpGet("range")]
    public async Task<IActionResult> Range([FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var from = fromDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var to = toDate ?? from.AddMonths(1);

        var holidays = await _db.PublicHolidays
            .Where(h => h.TenantId == tenantId && h.Date >= from && h.Date <= to)
            .OrderBy(h => h.Date)
            .ToListAsync(ct);

        return Ok(holidays);
    }

    [HttpGet("blackouts")]
    public async Task<IActionResult> ListBlackouts(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var items = await _db.LeaveBlackoutDates
            .Where(b => b.TenantId == tenantId)
            .OrderBy(b => b.StartDate)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("blackouts")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateBlackout([FromBody] CreateBlackoutRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var blackout = new LeaveBlackoutDate
        {
            TenantId = tenantId.Value,
            NameEn = req.NameEn,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            DepartmentName = req.DepartmentName ?? string.Empty,
            Reason = req.Reason,
            IsCompanyWide = req.IsCompanyWide ?? true
        };

        _db.LeaveBlackoutDates.Add(blackout);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/leave/holidays/blackouts/{blackout.Id}", blackout);
    }
}

public record CreateCalendarRequest(string Name, string CountryCode, int CalendarYear, Guid? CompanyId, Guid? BranchId);
public record CreateBlackoutRequest(string NameEn, DateOnly StartDate, DateOnly EndDate, string Reason, string? DepartmentName, bool? IsCompanyWide);
public record AddHolidayRequest(
    string NameEn,
    string? NameAr,
    DateOnly Date,
    string? HijriDate,
    bool IsRecurring,
    bool IsOptional,
    string? HolidayType,
    string? Notes);
