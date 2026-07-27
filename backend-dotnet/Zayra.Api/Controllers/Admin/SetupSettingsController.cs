using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Admin;

[Authorize]
[ApiController]
public class SetupSettingsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public SetupSettingsController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var id) ? id : null;

    // ── Numbering Rules ─────────────────────────────────────────────────────

    [HttpGet("api/admin/numbering-rules")]
    public async Task<IActionResult> ListNumberingRules(CancellationToken ct)
    {
        var tid = GetTenantId();
        return Ok(await _db.NumberingRules.Where(x => x.TenantId == tid).OrderBy(x => x.EntityType).ToListAsync(ct));
    }

    [HttpPost("api/admin/numbering-rules")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpsertNumberingRule([FromBody] NumberingRuleRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var existing = await _db.NumberingRules.FirstOrDefaultAsync(x => x.TenantId == tid && x.EntityType == req.EntityType, ct);
        if (existing != null)
        {
            existing.Prefix = req.Prefix; existing.Suffix = req.Suffix ?? string.Empty;
            existing.PaddingLength = req.PaddingLength; existing.Separator = req.Separator;
            existing.IncludeYear = req.IncludeYear; existing.IncludeMonth = req.IncludeMonth;
            existing.ResetYearly = req.ResetYearly; existing.UpdatedAtUtc = DateTime.UtcNow; existing.UpdatedBy = uid;
            await _db.SaveChangesAsync(ct);
            return Ok(existing);
        }
        var r = new NumberingRule
        {
            TenantId = tid, EntityType = req.EntityType, Prefix = req.Prefix, Suffix = req.Suffix ?? string.Empty,
            PaddingLength = req.PaddingLength, Separator = req.Separator,
            IncludeYear = req.IncludeYear, IncludeMonth = req.IncludeMonth,
            ResetYearly = req.ResetYearly, CreatedBy = uid,
        };
        _db.NumberingRules.Add(r);
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }

    // ── System Settings ─────────────────────────────────────────────────────

    // Secret settings (SMTP password, API keys…) must never leave the server in plaintext.
    // Reads replace the value with this sentinel; the upsert treats the sentinel as
    // "unchanged" so a round-tripped admin form cannot clobber the real secret with the mask.
    private const string SecretValueMask = "********";
    private static bool IsSecretSetting(string settingKey) =>
        settingKey.Contains("password", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("token", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("connectionstring", StringComparison.OrdinalIgnoreCase);

    [HttpGet("api/admin/system-settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetSystemSettings([FromQuery] string? category, CancellationToken ct)
    {
        var tid = GetTenantId();
        var q = _db.SystemSettings.AsNoTracking().Where(x => x.TenantId == tid);
        if (!string.IsNullOrEmpty(category)) q = q.Where(x => x.Category == category);
        var settings = await q.OrderBy(x => x.Category).ThenBy(x => x.SettingKey).ToListAsync(ct);
        foreach (var s in settings)
        {
            if (IsSecretSetting(s.SettingKey) && !string.IsNullOrEmpty(s.SettingValue))
                s.SettingValue = SecretValueMask; // AsNoTracking: mutation is serialization-only, never persisted
        }
        return Ok(settings);
    }

    [HttpPost("api/admin/system-settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpsertSystemSetting([FromBody] SystemSettingRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var existing = await _db.SystemSettings.FirstOrDefaultAsync(
            x => x.TenantId == tid && x.Category == req.Category && x.SettingKey == req.SettingKey, ct);
        if (existing != null)
        {
            if (existing.IsReadOnly) return BadRequest("This setting is read-only.");
            // The masked sentinel means "unchanged" — never overwrite a real secret with the mask.
            if (!(IsSecretSetting(existing.SettingKey) && req.SettingValue == SecretValueMask))
                existing.SettingValue = req.SettingValue;
            existing.UpdatedAtUtc = DateTime.UtcNow; existing.UpdatedBy = uid;
            await _db.SaveChangesAsync(ct);
            if (IsSecretSetting(existing.SettingKey) && !string.IsNullOrEmpty(existing.SettingValue))
                existing.SettingValue = SecretValueMask; // mask the response too
            return Ok(existing);
        }
        var s = new SystemSetting
        {
            TenantId = tid, Category = req.Category, SettingKey = req.SettingKey,
            SettingValue = req.SettingValue, DataType = req.DataType ?? "string",
            Description = req.Description ?? string.Empty, UpdatedBy = uid,
        };
        _db.SystemSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return Ok(s);
    }

    // Sends a real test email through the configured SMTP server so admins can
    // verify connectivity (host/port/credentials/TLS) instead of guessing.
    [HttpPost("api/admin/system-settings/smtp/test")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> TestSmtp([FromServices] IEmailService email, CancellationToken ct)
    {
        if (!await email.IsConfiguredAsync(ct))
            return Ok(new { ok = false, message = "SMTP is not configured. Enter and save the host and from-address first." });

        var to = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                 ?? User.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(to))
            return Ok(new { ok = false, message = "Your account has no email address on file to send the test to." });

        try
        {
            await email.SendAsync(to, "KynexOne Admin", "KynexOne — SMTP test",
                "<p>This is a test message confirming your KynexOne SMTP settings are working.</p>"
                + "<p>If you received this, outbound email (payslips, alerts, letters) is configured correctly.</p>",
                null, ct);
            return Ok(new { ok = true, message = $"Test email sent to {to}. Check that inbox to confirm delivery." });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, message = $"SMTP test failed: {ex.Message}" });
        }
    }

    // ── GCC Compliance Settings ─────────────────────────────────────────────

    [HttpGet("api/admin/gcc-settings")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> GetGCCSettings([FromQuery] string? countryCode, CancellationToken ct)
    {
        var tid = GetTenantId();
        var q = _db.GCCComplianceSettings.Where(x => x.TenantId == tid);
        if (!string.IsNullOrEmpty(countryCode)) q = q.Where(x => x.CountryCode == countryCode);
        return Ok(await q.ToListAsync(ct));
    }

    [HttpPost("api/admin/gcc-settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpsertGCCSetting([FromBody] GCCSettingRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var existing = await _db.GCCComplianceSettings.FirstOrDefaultAsync(
            x => x.TenantId == tid && x.CountryCode == req.CountryCode, ct);
        if (existing != null)
        {
            existing.WpsEnabled = req.WpsEnabled; existing.WpsAgentId = req.WpsAgentId ?? string.Empty;
            existing.WpsMolCode = req.WpsMolCode ?? string.Empty; existing.SifEnabled = req.SifEnabled;
            existing.EosbEnabled = req.EosbEnabled; existing.EosbYears1To5Rate = req.EosbYears1To5Rate;
            existing.EosbYearsAbove5Rate = req.EosbYearsAbove5Rate; existing.EosbMinYears = req.EosbMinYears;
            existing.WorkWeek = req.WorkWeek; existing.WeekendDays = req.WeekendDays;
            existing.VisaTrackingEnabled = req.VisaTrackingEnabled; existing.VisaAlertDays = req.VisaAlertDays;
            existing.IqamaRequired = req.IqamaRequired; existing.IqamaAlertDays = req.IqamaAlertDays;
            existing.EmiratesIdRequired = req.EmiratesIdRequired;
            existing.RamadanHoursEnabled = req.RamadanHoursEnabled; existing.RamadanReducedHoursPerDay = req.RamadanReducedHoursPerDay;
            existing.UpdatedAtUtc = DateTime.UtcNow; existing.UpdatedBy = uid;
            await _db.SaveChangesAsync(ct);
            return Ok(existing);
        }
        var g = new GCCComplianceSetting
        {
            TenantId = tid, CountryCode = req.CountryCode,
            WpsEnabled = req.WpsEnabled, WpsAgentId = req.WpsAgentId ?? string.Empty, WpsMolCode = req.WpsMolCode ?? string.Empty, SifEnabled = req.SifEnabled,
            EosbEnabled = req.EosbEnabled, EosbYears1To5Rate = req.EosbYears1To5Rate,
            EosbYearsAbove5Rate = req.EosbYearsAbove5Rate, EosbMinYears = req.EosbMinYears,
            WorkWeek = req.WorkWeek, WeekendDays = req.WeekendDays,
            VisaTrackingEnabled = req.VisaTrackingEnabled, VisaAlertDays = req.VisaAlertDays,
            IqamaRequired = req.IqamaRequired, IqamaAlertDays = req.IqamaAlertDays,
            EmiratesIdRequired = req.EmiratesIdRequired,
            RamadanHoursEnabled = req.RamadanHoursEnabled, RamadanReducedHoursPerDay = req.RamadanReducedHoursPerDay,
            UpdatedBy = uid,
        };
        _db.GCCComplianceSettings.Add(g);
        await _db.SaveChangesAsync(ct);
        return Ok(g);
    }

    // ── Fiscal Years ────────────────────────────────────────────────────────

    [HttpGet("api/admin/fiscal-years")]
    public async Task<IActionResult> ListFiscalYears(CancellationToken ct)
    {
        var tid = GetTenantId();
        return Ok(await _db.FiscalYears.Where(x => x.TenantId == tid).OrderByDescending(x => x.Year).ToListAsync(ct));
    }

    [HttpPost("api/admin/fiscal-years")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateFiscalYear([FromBody] FiscalYearRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (await _db.FiscalYears.AnyAsync(x => x.TenantId == tid && x.Year == req.Year, ct))
            return Conflict("Fiscal year already exists.");
        var fy = new FiscalYear
        {
            TenantId = tid, Code = $"FY{req.Year}", Year = req.Year,
            StartDate = req.StartDate, EndDate = req.EndDate, Status = "Open", CreatedBy = uid,
        };
        _db.FiscalYears.Add(fy);
        await _db.SaveChangesAsync(ct);
        return Ok(fy);
    }

    [HttpPatch("api/admin/fiscal-years/{id:guid}/close")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CloseFiscalYear(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var fy = await _db.FiscalYears.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (fy == null) return NotFound();
        fy.Status = "Closed"; fy.ClosedAtUtc = DateTime.UtcNow; fy.ClosedBy = uid;
        await _db.SaveChangesAsync(ct);
        return Ok(fy);
    }

    // ── Locations ────────────────────────────────────────────────────────────

    [HttpGet("api/admin/locations")]
    public async Task<IActionResult> ListLocations([FromQuery] Guid? branchId, CancellationToken ct)
    {
        var tid = GetTenantId();
        var q = _db.Locations.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId);
        return Ok(await q.OrderBy(x => x.NameEn).ToListAsync(ct));
    }

    [HttpPost("api/admin/locations")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateLocation([FromBody] LocationRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (await _db.Locations.AnyAsync(x => x.TenantId == tid && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("Location code already exists.");
        var loc = new Location
        {
            TenantId = tid, BranchId = req.BranchId, Code = req.Code, NameEn = req.NameEn,
            NameAr = req.NameAr ?? string.Empty, AddressLine1 = req.AddressLine1 ?? string.Empty,
            City = req.City ?? string.Empty, CountryCode = req.CountryCode ?? string.Empty,
            PostalCode = req.PostalCode ?? string.Empty,
            Latitude = req.Latitude, Longitude = req.Longitude,
            GeofenceRadiusMeters = req.GeofenceRadiusMeters, CreatedBy = uid,
        };
        _db.Locations.Add(loc);
        await _db.SaveChangesAsync(ct);
        return Ok(loc);
    }

    [HttpPut("api/admin/locations/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdateLocation(Guid id, [FromBody] LocationRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var loc = await _db.Locations.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (loc == null) return NotFound();
        loc.NameEn = req.NameEn; loc.NameAr = req.NameAr ?? string.Empty;
        loc.AddressLine1 = req.AddressLine1 ?? string.Empty; loc.City = req.City ?? string.Empty;
        loc.CountryCode = req.CountryCode ?? string.Empty; loc.PostalCode = req.PostalCode ?? string.Empty;
        loc.Latitude = req.Latitude; loc.Longitude = req.Longitude;
        loc.GeofenceRadiusMeters = req.GeofenceRadiusMeters; loc.IsActive = req.IsActive;
        loc.UpdatedAtUtc = DateTime.UtcNow; loc.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        return Ok(loc);
    }

    [HttpDelete("api/admin/locations/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DeleteLocation(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var loc = await _db.Locations.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (loc == null) return NotFound();
        loc.IsDeleted = true;
        loc.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("api/admin/fiscal-years/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteFiscalYear(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var fy = await _db.FiscalYears.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (fy == null) return NotFound();
        if (fy.IsCurrent) return BadRequest(new { message = "Cannot delete the current fiscal year." });
        _db.FiscalYears.Remove(fy);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("api/admin/numbering-rules/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteNumberingRule(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var rule = await _db.NumberingRules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (rule == null) return NotFound();
        _db.NumberingRules.Remove(rule);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Notification Templates ───────────────────────────────────────────────

    [HttpGet("api/admin/notification-templates")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ListNotificationTemplates([FromQuery] string? channel, CancellationToken ct)
    {
        var tid = GetTenantId();
        var q = _db.NotificationTemplates.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (!string.IsNullOrEmpty(channel)) q = q.Where(x => x.Channel == channel);
        return Ok(await q.OrderBy(x => x.EventType).ToListAsync(ct));
    }

    [HttpPost("api/admin/notification-templates")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateNotificationTemplate([FromBody] NotificationTemplateRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (await _db.NotificationTemplates.AnyAsync(x => x.TenantId == tid && x.Code == req.Code && x.Channel == req.Channel && !x.IsDeleted, ct))
            return Conflict("Template with this code and channel already exists.");
        var t = new NotificationTemplate
        {
            TenantId = tid, Code = req.Code, EventType = req.EventType, Channel = req.Channel,
            SubjectEn = req.SubjectEn ?? string.Empty, SubjectAr = req.SubjectAr ?? string.Empty,
            BodyEn = req.BodyEn, BodyAr = req.BodyAr ?? string.Empty,
            Variables = req.Variables ?? string.Empty, CreatedBy = uid,
        };
        _db.NotificationTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return Ok(t);
    }

    [HttpPut("api/admin/notification-templates/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateNotificationTemplate(Guid id, [FromBody] NotificationTemplateRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var t = await _db.NotificationTemplates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        t.EventType = req.EventType; t.SubjectEn = req.SubjectEn ?? string.Empty;
        t.SubjectAr = req.SubjectAr ?? string.Empty; t.BodyEn = req.BodyEn;
        t.BodyAr = req.BodyAr ?? string.Empty; t.Variables = req.Variables ?? string.Empty;
        t.IsActive = req.IsActive; t.UpdatedAtUtc = DateTime.UtcNow; t.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        return Ok(t);
    }

    [HttpDelete("api/admin/notification-templates/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteNotificationTemplate(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var t = await _db.NotificationTemplates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        t.IsDeleted = true;
        t.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Admin Audit Logs ─────────────────────────────────────────────────────

    [HttpGet("api/admin/audit-logs")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAdminAuditLogs(
        [FromQuery] string? entityType, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.AdminAuditLogs.Where(x => x.TenantId == tid);
        if (!string.IsNullOrEmpty(entityType)) q = q.Where(x => x.EntityType == entityType);
        if (from.HasValue) q = q.Where(x => x.CreatedAtUtc >= from.Value);
        if (to.HasValue) q = q.Where(x => x.CreatedAtUtc <= to.Value);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }
}

public record NumberingRuleRequest(string EntityType, string Prefix, string? Suffix, int PaddingLength, string Separator, bool IncludeYear, bool IncludeMonth, bool ResetYearly);
public record SystemSettingRequest(string Category, string SettingKey, string SettingValue, string? DataType, string? Description);
public record GCCSettingRequest(
    string CountryCode,
    bool WpsEnabled, string? WpsAgentId, string? WpsMolCode, bool SifEnabled,
    bool EosbEnabled, decimal EosbYears1To5Rate, decimal EosbYearsAbove5Rate, int EosbMinYears,
    string WorkWeek, string WeekendDays,
    bool VisaTrackingEnabled, int VisaAlertDays,
    bool IqamaRequired, int IqamaAlertDays, bool EmiratesIdRequired,
    bool RamadanHoursEnabled, int RamadanReducedHoursPerDay);
public record FiscalYearRequest(int Year, DateOnly StartDate, DateOnly EndDate);
public record LocationRequest(Guid? BranchId, string Code, string NameEn, string? NameAr, string? AddressLine1, string? City, string? CountryCode, string? PostalCode, decimal? Latitude, decimal? Longitude, decimal? GeofenceRadiusMeters, bool IsActive = true);
public record NotificationTemplateRequest(string Code, string EventType, string Channel, string? SubjectEn, string? SubjectAr, string BodyEn, string? BodyAr, string? Variables, bool IsActive = true);
