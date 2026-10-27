using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Admin;

[Authorize]
[ApiController]
public class SetupSettingsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    public SetupSettingsController(ZayraDbContext db, IAuditService audit) { _db = db; _audit = audit; }

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
    [HasPermission("security.manage")]
    public async Task<IActionResult> UpsertNumberingRule([FromBody] NumberingRuleRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var uid = GetUserId();
        var existing = await _db.NumberingRules.FirstOrDefaultAsync(x => x.TenantId == tid && x.EntityType == req.EntityType, ct);
        if (existing != null)
        {
            var before = NumberingAudit(existing);
            existing.Prefix = req.Prefix; existing.Suffix = req.Suffix ?? string.Empty;
            existing.PaddingLength = req.PaddingLength; existing.Separator = req.Separator;
            existing.IncludeYear = req.IncludeYear; existing.IncludeMonth = req.IncludeMonth;
            existing.ResetYearly = req.ResetYearly; existing.UpdatedAtUtc = DateTime.UtcNow; existing.UpdatedBy = uid;
            this.RecordChange(_db, tid, null, "NumberingRule", existing.Id.ToString(), "Updated", before, NumberingAudit(existing));
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
        this.RecordChange(_db, tid, null, "NumberingRule", r.Id.ToString(), "Created", null, NumberingAudit(r));
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }

    // ── System Settings ─────────────────────────────────────────────────────

    // Secret settings (SMTP password, API keys…) must never leave the server in plaintext.
    // Reads replace the value with this sentinel; the upsert treats the sentinel as
    // "unchanged" so a round-tripped admin form cannot clobber the real secret with the mask.
    private const string SecretValueMask = "********";
    // POD-D5: the substring match below does NOT catch every notification-provider credential —
    // "Push.ServiceAccountJson" (a Firebase service account containing a PEM private key) and
    // "Push.P8Key" (an APNs signing key) both contain none of these words and would have been
    // returned in PLAINTEXT. NotificationProviderSecrets.Keys is an explicit allow-list that a
    // test asserts covers every registered provider credential key.
    internal static bool IsSecretSetting(string settingKey) =>
        settingKey.Contains("password", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("token", StringComparison.OrdinalIgnoreCase)
        || settingKey.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
        || Zayra.Api.Infrastructure.Notifications.NotificationProviderSecrets.IsSecretKey(settingKey);

    [HttpGet("api/admin/system-settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetSystemSettings([FromQuery] string? category, CancellationToken ct)
    {
        var tid = GetTenantId();
        // Bank-export settings are company-scoped financial facts. They may only be read through
        // SaudiBankExportsController, which applies payroll permissions AND legal-entity scope.
        var q = _db.SystemSettings.AsNoTracking().Where(x => x.TenantId == tid
            && x.Category != Zayra.Api.Infrastructure.Payroll.SaudiBankExports.SaudiBankExportService.SettingsCategory
            && !x.SettingKey.StartsWith("saudi-bank-export.company."));
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
    [HasPermission("security.manage")]
    public async Task<IActionResult> UpsertSystemSetting([FromBody] SystemSettingRequest req,
        [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var uid = GetUserId();
        // Generic settings writes must not bypass the payroll-export company and permission gates.
        if (string.Equals(req.Category, Zayra.Api.Infrastructure.Payroll.SaudiBankExports.SaudiBankExportService.SettingsCategory, StringComparison.OrdinalIgnoreCase)
            || req.SettingKey.StartsWith("saudi-bank-export.company.", StringComparison.OrdinalIgnoreCase))
            return Forbid();

        // POD-D5: notification-provider credentials are encrypted at rest with IDataProtection —
        // the same prior art QiwaIntegrationService uses. SystemSetting.IsEncrypted is decorative
        // in this codebase (declared, never read, never written), so it is not the mechanism.
        var protectNotificationSecret =
            string.Equals(req.Category, Zayra.Api.Infrastructure.Notifications.NotificationProviderConfigReader.SettingsCategory, StringComparison.OrdinalIgnoreCase)
            && Zayra.Api.Infrastructure.Notifications.NotificationProviderSecrets.IsSecretKey(req.SettingKey);
        string Store(string value) => protectNotificationSecret && !string.IsNullOrEmpty(value)
            ? protection.CreateProtector(Zayra.Api.Infrastructure.Notifications.NotificationProviderSecrets.ProtectorPurpose).Protect(value)
            : value;

        var existing = await _db.SystemSettings.FirstOrDefaultAsync(
            x => x.TenantId == tid && x.Category == req.Category && x.SettingKey == req.SettingKey, ct);
        if (existing != null)
        {
            if (existing.IsReadOnly) return BadRequest("This setting is read-only.");
            // The masked sentinel means "unchanged" — never overwrite a real secret with the mask.
            if (!(IsSecretSetting(existing.SettingKey) && req.SettingValue == SecretValueMask))
                existing.SettingValue = Store(req.SettingValue);
            existing.IsEncrypted = existing.IsEncrypted || protectNotificationSecret;
            existing.UpdatedAtUtc = DateTime.UtcNow; existing.UpdatedBy = uid;
            await _db.SaveChangesAsync(ct);
            if (IsSecretSetting(existing.SettingKey) && !string.IsNullOrEmpty(existing.SettingValue))
                existing.SettingValue = SecretValueMask; // mask the response too
            return Ok(existing);
        }
        var s = new SystemSetting
        {
            TenantId = tid, Category = req.Category, SettingKey = req.SettingKey,
            SettingValue = Store(req.SettingValue), DataType = req.DataType ?? "string",
            Description = req.Description ?? string.Empty, UpdatedBy = uid,
            IsEncrypted = protectNotificationSecret,
        };
        _db.SystemSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        // A freshly-created secret was previously echoed back in plaintext. Detach first so the
        // serialization-only mutation can never be persisted.
        _db.Entry(s).State = EntityState.Detached;
        if (IsSecretSetting(s.SettingKey) && !string.IsNullOrEmpty(s.SettingValue))
            s.SettingValue = SecretValueMask;
        return Ok(s);
    }

    // Sends a real test email through the configured SMTP server so admins can
    // verify connectivity (host/port/credentials/TLS) instead of guessing.
    [HttpPost("api/admin/system-settings/smtp/test")]
    [HasPermission("security.manage")]
    public async Task<IActionResult> TestSmtp([FromServices] IEmailService email, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        // Tenant-explicit overload (POD-D5): the ambient-filter form only happened to be correct
        // because this runs under an HTTP principal.
        if (!await email.IsConfiguredAsync(GetTenantId(), ct))
            return Ok(new { ok = false, message = "SMTP is not configured. Enter and save the host and from-address first." });

        var to = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                 ?? User.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(to))
            return Ok(new { ok = false, message = "Your account has no email address on file to send the test to." });

        try
        {
            var delivery = await email.DeliverAsync(GetTenantId(), to, "KynexOne Admin", "KynexOne — SMTP test",
                "<p>This is a test message confirming your KynexOne SMTP settings are working.</p>"
                + "<p>If you received this, outbound email (payslips, alerts, letters) is configured correctly.</p>",
                null, ct);
            // F09: only a relay's acceptance is a passed test; a captured message proves nothing.
            return delivery.ReachedARelay
                ? Ok(new { ok = true, message = $"Test email accepted by the mail server for {to}. Check that inbox to confirm it arrived." })
                : Ok(new { ok = false, captured = delivery.Status == EmailDeliveryStatus.Captured, message = delivery.Detail });
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
    [HasPermission("security.manage")]
    public async Task<IActionResult> UpsertGCCSetting([FromBody] GCCSettingRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
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

    /// <summary>Setup's Remove for a country rule. Payroll, end-of-service and work-week history refer to it, so it is never
    /// erased: it is turned off (readers ignore it) and can be restored. Same permission and scope as editing.</summary>
    [HttpDelete("api/admin/gcc-settings/{id:guid}")]
    [HasPermission("security.manage")]
    public async Task<IActionResult> RemoveGCCSetting(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var row = await _db.GCCComplianceSettings.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound();
        if (row.IsActive)
        {
            row.IsActive = false; row.TurnedOffAtUtc = DateTime.UtcNow; row.TurnedOffBy = GetUserId();
            row.UpdatedAtUtc = DateTime.UtcNow; row.UpdatedBy = GetUserId();
            this.RecordChange(_db, tid, row.CompanyId, "GCCComplianceSetting", id.ToString(), "TurnedOff", new { row.CountryCode, IsActive = true }, new { row.CountryCode, IsActive = false });
            await _db.SaveChangesAsync(ct);
        }
        var usage = await SetupRemoval.MeasureAsync(this, new[]
        {
            SetupRemoval.Of<Company>(_db, tid, "companies", "companies", c => !c.IsDeleted && c.CountryCode == row.CountryCode && (row.CompanyId == null || c.Id == row.CompanyId)),
        }, ct);
        return SetupRemoval.TurnedOff(this, usage);
    }

    [HttpPost("api/admin/gcc-settings/{id:guid}/restore")]
    [HasPermission("security.manage")]
    public async Task<IActionResult> RestoreGCCSetting(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var row = await _db.GCCComplianceSettings.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound();
        if (!row.IsActive)
        {
            if (await _db.GCCComplianceSettings.AnyAsync(x => x.TenantId == tid && x.Id != id && x.IsActive && x.CompanyId == row.CompanyId && x.CountryCode == row.CountryCode, ct))
                return SetupRemoval.Blocked(this, "COUNTRY_RULE_EXISTS", $"Another {row.CountryCode} rule is already on for this scope. Turn it off first, then restore this one.");
            row.IsActive = true; row.TurnedOffAtUtc = null; row.TurnedOffBy = null;
            row.UpdatedAtUtc = DateTime.UtcNow; row.UpdatedBy = GetUserId();
            this.RecordChange(_db, tid, row.CompanyId, "GCCComplianceSetting", id.ToString(), "Restored", new { row.CountryCode, IsActive = false }, new { row.CountryCode, IsActive = true });
            await _db.SaveChangesAsync(ct);
        }
        return SetupRemoval.Restored(this);
    }

    // ── Fiscal Years ────────────────────────────────────────────────────────

    [HttpGet("api/admin/fiscal-years")]
    public async Task<IActionResult> ListFiscalYears(CancellationToken ct)
    {
        var tid = GetTenantId();
        var years = await _db.FiscalYears.Where(x => x.TenantId == tid).OrderByDescending(x => x.Year).ToListAsync(ct);
        var canWrite = this.HasGroupEntityScope() && User.HasClaim("permission", "security.manage");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = new List<object>();
        // Usage (and its reason text) is only computed for callers who can manage fiscal years; everyone
        // else gets one generic sentence. Evidence is loaded once, not per year.
        HashSet<string> glPeriods = new(); HashSet<int> runMonths = new();
        if (canWrite)
        {
            glPeriods = (await ScopedBypass.TenantWide(_db.GlPeriodCloses, tid, "In-use check for a removal: must see every company's rows in this tenant, not only the caller's.")
                .Select(g => g.Period).Distinct().ToListAsync(ct)).ToHashSet();
            runMonths = (await ScopedBypass.TenantWide(_db.PayrollRuns, tid, "In-use check for a removal: must see every company's rows in this tenant, not only the caller's.")
                .Select(p => p.Year * 12 + p.Month).Distinct().ToListAsync(ct)).ToHashSet();
        }
        foreach (var fy in years)
        {
            string? blocked, closeBlocked;
            if (!canWrite)
            {
                blocked = closeBlocked = "Only group administrators with security access can change fiscal years.";
            }
            else
            {
                blocked = fy.Status == "Closed" ? "This year is closed, so it is kept for the record and cannot be deleted."
                    : FiscalYearUsage(fy, glPeriods, runMonths);
                closeBlocked = fy.Status == "Closed" ? "This year is already closed." : null;
            }
            rows.Add(new
            {
                fy.Id, fy.TenantId, fy.Code, fy.Year, fy.StartDate, fy.EndDate, fy.Status,
                // Derived from the dates; the stored IsCurrent flag is never maintained.
                isCurrent = fy.StartDate <= today && today <= fy.EndDate,
                fy.CreatedAtUtc, fy.CreatedBy, fy.ClosedAtUtc, fy.ClosedBy,
                canClose = canWrite && fy.Status == "Open",
                canDelete = canWrite && blocked is null,
                closeBlockedReason = closeBlocked,
                deleteBlockedReason = blocked,
            });
        }
        return Ok(rows);
    }

    /// <summary>Plain-language reason a year has history (null when nothing refers to it).</summary>
    private static string? FiscalYearUsage(FiscalYear fy, HashSet<string> glPeriods, HashSet<int> runMonths)
    {
        var startKey = $"{fy.StartDate.Year:D4}-{fy.StartDate.Month:D2}";
        var endKey = $"{fy.EndDate.Year:D4}-{fy.EndDate.Month:D2}";
        if (glPeriods.Any(p => string.Compare(p, startKey, StringComparison.Ordinal) >= 0 && string.Compare(p, endKey, StringComparison.Ordinal) <= 0))
            return "Accounting periods inside this year already exist, so it cannot be deleted.";
        var ys = fy.StartDate.Year * 12 + fy.StartDate.Month; var ye = fy.EndDate.Year * 12 + fy.EndDate.Month;
        if (runMonths.Any(m => m >= ys && m <= ye))
            return "Payroll runs inside this year already exist, so it cannot be deleted.";
        return null;
    }

    private async Task<string?> FiscalYearUsageAsync(Guid tid, FiscalYear fy, CancellationToken ct)
    {
        var startKey = $"{fy.StartDate.Year:D4}-{fy.StartDate.Month:D2}";
        var endKey = $"{fy.EndDate.Year:D4}-{fy.EndDate.Month:D2}";
        if (await ScopedBypass.TenantWide(_db.GlPeriodCloses, tid, "In-use check for a removal: must see every company's rows in this tenant, not only the caller's.").AnyAsync(g => g.TenantId == tid
                && string.Compare(g.Period, startKey) >= 0 && string.Compare(g.Period, endKey) <= 0, ct))
            return "Accounting periods inside this year already exist, so it cannot be deleted.";
        var ys = fy.StartDate.Year * 12 + fy.StartDate.Month; var ye = fy.EndDate.Year * 12 + fy.EndDate.Month;
        if (await ScopedBypass.TenantWide(_db.PayrollRuns, tid, "In-use check for a removal: must see every company's rows in this tenant, not only the caller's.").AnyAsync(p => p.TenantId == tid
                && p.Year * 12 + p.Month >= ys && p.Year * 12 + p.Month <= ye, ct))
            return "Payroll runs inside this year already exist, so it cannot be deleted.";
        return null;
    }

    private static object NumberingAudit(NumberingRule r) => new
    {
        r.EntityType, r.Prefix, r.Suffix, r.PaddingLength, r.Separator, r.IncludeYear, r.IncludeMonth, r.ResetYearly, r.IsActive, r.CurrentSequence,
    };

    [HttpPost("api/admin/fiscal-years")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> CreateFiscalYear([FromBody] FiscalYearRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
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
        this.RecordChange(_db, tid, null, "FiscalYear", fy.Id.ToString(), "Created", null, new { fy.Code, fy.Year, fy.StartDate, fy.EndDate });
        await _db.SaveChangesAsync(ct);
        return Ok(fy);
    }

    [HttpPatch("api/admin/fiscal-years/{id:guid}/close")]
    [HasPermission("security.manage")]
    public Task<IActionResult> CloseFiscalYear(Guid id, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, GetTenantId(), () => CloseFiscalYearCore(id, ct), ct);

    private async Task<IActionResult> CloseFiscalYearCore(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var uid = GetUserId();
        var fy = await _db.FiscalYears.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (fy == null) return NotFound();
        if (fy.Status == "Closed")
            return Conflict(new { code = "FISCAL_YEAR_ALREADY_CLOSED", message = "This fiscal year is already closed." });
        fy.Status = "Closed"; fy.ClosedAtUtc = DateTime.UtcNow; fy.ClosedBy = uid;
        this.RecordChange(_db, tid, null, "FiscalYear", fy.Id.ToString(), "Closed", new { Status = "Open" }, new { fy.Status, fy.ClosedAtUtc });
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
    [HasPermission("organization.write")]
    public async Task<IActionResult> CreateLocation([FromBody] LocationRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
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
    [HasPermission("organization.write")]
    public async Task<IActionResult> UpdateLocation(Guid id, [FromBody] LocationRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var uid = GetUserId();
        var loc = await _db.Locations.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (loc == null) return NotFound();
        loc.NameEn = req.NameEn; loc.NameAr = req.NameAr ?? string.Empty;
        loc.AddressLine1 = req.AddressLine1 ?? string.Empty; loc.City = req.City ?? string.Empty;
        loc.CountryCode = req.CountryCode ?? string.Empty; loc.PostalCode = req.PostalCode ?? string.Empty;
        loc.Latitude = req.Latitude; loc.Longitude = req.Longitude;
        loc.GeofenceRadiusMeters = req.GeofenceRadiusMeters;
        loc.UpdatedAtUtc = DateTime.UtcNow; loc.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        return Ok(loc);
    }

    [HttpDelete("api/admin/locations/{id:guid}")]
    [HasPermission("organization.delete")]
    public Task<IActionResult> DeleteLocation(Guid id, CancellationToken ct)
        => OrgWriteRules.RemoveAsync(this, new OrgRowRemoval(_db, _audit), OrgRowKind.Location, id,
            new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), GetTenantId()), ct);

    [HttpPost("api/admin/locations/{id:guid}/restore")]
    [HasPermission("organization.delete")]
    public Task<IActionResult> RestoreLocation(Guid id, CancellationToken ct)
        => OrgWriteRules.RestoreAsync(this, new OrgRowRemoval(_db, _audit), OrgRowKind.Location, id,
            new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), GetTenantId()), ct);

    [HttpDelete("api/admin/fiscal-years/{id:guid}")]
    [HasPermission("security.manage")]
    public Task<IActionResult> DeleteFiscalYear(Guid id, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, GetTenantId(), () => DeleteFiscalYearCore(id, ct), ct);

    private async Task<IActionResult> DeleteFiscalYearCore(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var fy = await _db.FiscalYears.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (fy == null) return NotFound();
        if (fy.Status == "Closed")
            return Conflict(new { code = "FISCAL_YEAR_CLOSED", message = "A closed fiscal year is part of the record and cannot be deleted." });
        if (await FiscalYearUsageAsync(tid, fy, ct) is { } usage)
            return Conflict(new { code = "FISCAL_YEAR_IN_USE", message = usage });
        _db.FiscalYears.Remove(fy);
        this.RecordChange(_db, tid, null, "FiscalYear", fy.Id.ToString(), "Deleted", new { fy.Code, fy.Year, fy.StartDate, fy.EndDate, fy.Status }, null);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("api/admin/numbering-rules/{id:guid}")]
    [HasPermission("security.manage")]
    public async Task<IActionResult> DeleteNumberingRule(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var rule = await _db.NumberingRules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (rule == null) return NotFound();
        // No issuing code reads or advances numbering rules today (CurrentSequence is never incremented),
        // so there is no history to protect; the delete is audited like every other change.
        _db.NumberingRules.Remove(rule);
        this.RecordChange(_db, tid, null, "NumberingRule", rule.Id.ToString(), "Deleted", NumberingAudit(rule), null);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPatch("api/admin/numbering-rules/{id:guid}/active")]
    [HasPermission("security.manage")]
    public async Task<IActionResult> SetNumberingRuleActive(Guid id, [FromQuery] bool active, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var rule = await _db.NumberingRules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (rule == null) return NotFound();
        if (rule.IsActive == active) return Ok(rule);
        rule.IsActive = active; rule.UpdatedAtUtc = DateTime.UtcNow; rule.UpdatedBy = GetUserId();
        this.RecordChange(_db, tid, null, "NumberingRule", rule.Id.ToString(), active ? "Activated" : "Deactivated", new { IsActive = !active }, new { IsActive = active });
        await _db.SaveChangesAsync(ct);
        return Ok(rule);
    }

    // ── Notification Templates ───────────────────────────────────────────────

    [HttpGet("api/admin/notification-templates")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ListNotificationTemplates([FromQuery] string? channel, [FromQuery] bool includeRemoved, CancellationToken ct)
    {
        var tid = GetTenantId();
        // "Show turned-off" also lists removed templates, so they can be restored.
        // Only someone who could actually restore a row (same permission and group scope as Restore) is shown removed ones.
        var q = includeRemoved && this.HasGroupEntityScope() && User.HasPermission("notifications.manage") ? ScopedBypass.TenantWide(_db.NotificationTemplates, tid, "Setup lists and restores removed rows; the tenant is pinned here and the caller's company scope is checked by the action.") : _db.NotificationTemplates.Where(x => x.TenantId == tid);
        if (!string.IsNullOrEmpty(channel)) q = q.Where(x => x.Channel == channel);
        return Ok(await q.OrderBy(x => x.EventType).ToListAsync(ct));
    }

    [HttpPost("api/admin/notification-templates")]
    [HasPermission("notifications.manage")]
    public async Task<IActionResult> CreateNotificationTemplate([FromBody] NotificationTemplateRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
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
    [HasPermission("notifications.manage")]
    public async Task<IActionResult> UpdateNotificationTemplate(Guid id, [FromBody] NotificationTemplateRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
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
    [HasPermission("notifications.manage")]
    public async Task<IActionResult> DeleteNotificationTemplate(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var t = await _db.NotificationTemplates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        t.IsDeleted = true;
        t.UpdatedAtUtc = DateTime.UtcNow;
        this.RecordChange(_db, tid, null, "NotificationTemplate", id.ToString(), "Removed", new { t.Code, t.Channel }, null);
        await _db.SaveChangesAsync(ct);
        // Nothing references a template by id (events look it up by code and channel), so Remove never needs to keep a used row.
        return SetupRemoval.Deleted(this);
    }

    [HttpPost("api/admin/notification-templates/{id:guid}/restore")]
    [HasPermission("notifications.manage")]
    public async Task<IActionResult> RestoreNotificationTemplate(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var t = await ScopedBypass.TenantWide(_db.NotificationTemplates, tid, "Setup lists and restores removed rows; the tenant is pinned here and the caller's company scope is checked by the action.").FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return NotFound();
        if (t.IsDeleted && await _db.NotificationTemplates.AnyAsync(x => x.TenantId == tid && x.Id != id && x.Code == t.Code && x.Channel == t.Channel && !x.IsDeleted, ct))
            return SetupRemoval.Blocked(this, "NOTIFICATION_TEMPLATE_CODE_TAKEN", $"A template with code {t.Code} on {t.Channel} now exists, so this one cannot be restored as it is.");
        if (t.IsDeleted || !t.IsActive)
        {
            t.IsDeleted = false; t.IsActive = true; t.UpdatedAtUtc = DateTime.UtcNow; t.UpdatedBy = GetUserId();
            this.RecordChange(_db, tid, null, "NotificationTemplate", id.ToString(), "Restored", null, new { t.Code, t.Channel });
            await _db.SaveChangesAsync(ct);
        }
        return SetupRemoval.Restored(this);
    }

    // ── Admin Audit Logs ─────────────────────────────────────────────────────

    [HttpGet("api/admin/audit-logs")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAdminAuditLogs(
        [FromQuery] string? entityType, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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
