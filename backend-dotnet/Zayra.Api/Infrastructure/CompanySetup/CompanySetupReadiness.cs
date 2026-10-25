using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;
using V = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>
/// The readiness checklist after a company is created (design contract step 7): what is still missing before the
/// company can run, each item read from the rows that decide it, with a deep link to the screen that fixes it.
/// Company items are always listed (done / todo / recommended). Workspace-wide items ("Applies to all companies")
/// appear only while unconfigured.
/// </summary>
public static class CompanySetupReadiness
{
    private const string Why = "Create-company readiness: what is configured for one company (and the workspace defaults it inherits), read whatever company the caller's switcher selects.";

    public static string SetupHref(string tab, Guid companyId) => $"/setup?tab={tab}&companyId={companyId}";

    public static async Task<IReadOnlyList<ReadinessItem>> ComputeAsync(
        ZayraDbContext db, IEmailService email, Guid tenantId, Company company, CancellationToken ct)
    {
        var items = new List<ReadinessItem>();
        var id = company.Id;
        var isSa = string.Equals(company.CountryCode, "SA", StringComparison.OrdinalIgnoreCase);
        void Add(string key, string label, string labelAr, bool done, string missing, string href, string scope = V.ScopeCompany) =>
            items.Add(new ReadinessItem(key, label, labelAr, done ? V.ReadinessDone : missing, scope, href));

        var gl = await ScopedBypass.TenantWide(db.GlAccountMappings, tenantId, Why)
            .AnyAsync(m => m.IsActive && (m.CompanyId == id || m.CompanyId == null), ct);
        Add("glMapping", "GL posting accounts", "حسابات الترحيل المحاسبي", gl, V.ReadinessTodo, SetupHref("glMapping", id));

        var establishment = await ScopedBypass.TenantWide(db.Positions, tenantId, Why).AnyAsync(p => p.CompanyId == id, ct);
        Add("establishment", "Approved positions and budget", "الوظائف المعتمدة والميزانية", establishment, V.ReadinessRecommended, SetupHref("establishment", id));

        var branchIds = await db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId && b.CompanyId == id && !b.IsDeleted)
            .Select(b => b.Id).ToListAsync(ct);
        var locations = await db.Locations.AsNoTracking()
            .AnyAsync(l => l.TenantId == tenantId && !l.IsDeleted && l.BranchId != null && branchIds.Contains(l.BranchId.Value), ct);
        Add("locations", "Work locations and geofences", "مواقع العمل والنطاقات الجغرافية", locations, V.ReadinessRecommended, SetupHref("locations", id));

        var country = (company.CountryCode ?? string.Empty).Trim().ToUpperInvariant();
        var gcc = await ScopedBypass.TenantWide(db.GCCComplianceSettings, tenantId, Why)
            .AnyAsync(g => g.CountryCode == country && (g.CompanyId == id || g.CompanyId == null), ct);
        Add("gccSettings", "Country settings (end of service, weekend)", "إعدادات الدولة (نهاية الخدمة، العطلة الأسبوعية)", gcc, V.ReadinessTodo, SetupHref("gccSettings", id));

        var iso3 = country switch { "SA" => "SAU", "AE" => "ARE", "QA" => "QAT", "KW" => "KWT", "BH" => "BHR", "OM" => "OMN", _ => country };
        var statutory = await ScopedBypass.NullableTenantWide(db.StatutoryRules, null,
                    "Platform statutory rules (tenant_id IS NULL) are the defaults every tenant inherits; readiness asks only whether any exist for the country.")
                .AnyAsync(r => r.CountryCode == country || r.CountryCode == iso3, ct)
            || await ScopedBypass.NullableTenantWide(db.StatutoryRules, tenantId, Why).AnyAsync(r => r.CountryCode == country || r.CountryCode == iso3, ct);
        Add("statutoryRules", "Statutory rules for the country", "الأنظمة النظامية للدولة", statutory, V.ReadinessTodo, SetupHref("statutoryRules", id));

        bool bank;
        if (isSa)
            bank = !string.IsNullOrWhiteSpace((await new SaudiBankExportService(db).GetSettingsAsync(tenantId, id, ct)).MainAccountNumber);
        else
            bank = !string.IsNullOrWhiteSpace(company.WpsEmployerId);
        Add("bankWps", "Bank and WPS settings (before the first payment)", "إعدادات البنك وحماية الأجور (قبل أول دفعة)", bank, V.ReadinessTodo,
            $"/payroll?companyId={id}");

        if (isSa)
        {
            var nitaqat = await ScopedBypass.TenantWide(db.NitaqatEstablishmentProfiles, tenantId, Why).AnyAsync(p => p.CompanyId == id, ct);
            Add("nitaqat", "Nitaqat establishment profile", "ملف المنشأة في نطاقات", nitaqat, V.ReadinessRecommended, $"/saudi-compliance?companyId={id}");
        }

        // Company super admin: an active Admin login, not group-wide, granted this company. Until that person has
        // signed in, the item stays todo and carries their user id: the screen sends them the single-use sign-in link
        // (or, for a caller who cannot issue links, opens User Management at the href).
        var admins = await ScopedBypass.TenantWide(db.Users, tenantId, Why).AsNoTracking()
            .Where(u => u.IsActive && !u.IsDeleted && !u.IsGroupScope
                && u.UserRoles.Any(r => r.Role != null && r.Role.NormalizedName == "ADMIN" && r.Role.IsActive && !r.Role.IsDeleted)
                && u.EntityAccesses.Any(e => e.IsActive && e.CompanyId == id && e.GrantMode == EntityGrantModes.SelectedCompanies))
            .OrderBy(u => u.CreatedAtUtc).ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.FullName, u.LastLoginAtUtc }).ToListAsync(ct);
        var adminHref = $"/user-management?companyId={id}&openCreate=1&purpose=companyAdmin";
        if (admins.Count > 0)
        {
            var signedIn = admins.Any(a => a.LastLoginAtUtc != null);
            var pending = admins[0];
            items.Add(signedIn
                ? new ReadinessItem("companyAdmin", "Company super admin", "مسؤول الشركة الأعلى", V.ReadinessDone, V.ScopeCompany,
                    $"/user-management?companyId={id}")
                : new ReadinessItem("companyAdmin", $"Send {pending.FullName} their sign-in link", $"أرسل رابط تسجيل الدخول إلى {pending.FullName}",
                    V.ReadinessTodo, V.ScopeCompany, $"/user-management?companyId={id}", pending.Id));
        }
        else
        {
            var mode = await RequestedAdminModeAsync(db, tenantId, id, ct);
            var wanted = mode is V.AdminInvite or V.AdminExisting;
            var seatLimit = wanted && await LastAdminFailureCodeAsync(db, tenantId, id, ct) == UserSeatLimit.Refusal.Code;
            items.Add(new ReadinessItem("companyAdmin",
                seatLimit ? "Company super admin not set up: the licence's user limit is reached. Free a seat or extend the licence, then add them in User Management"
                    : wanted ? "Company super admin could not be set up — retry from User Management" : "Company super admin (recommended)",
                seatLimit ? "لم يُعَدّ مسؤول الشركة: بلغت حد المستخدمين في اشتراكك. حرّر مقعدًا أو قم بالترقية، ثم أضفه من إدارة المستخدمين"
                    : wanted ? "تعذر إعداد مسؤول الشركة — أعد المحاولة من إدارة المستخدمين" : "مسؤول الشركة الأعلى (موصى به)",
                wanted ? V.ReadinessTodo : V.ReadinessRecommended, V.ScopeCompany, adminHref));
        }

        // ── Workspace-wide: listed only while unconfigured ───────────────────────────────────────────────────
        void Workspace(string key, string label, string labelAr, bool configured)
        {
            if (configured) return;
            items.Add(new ReadinessItem(key, $"{label} (applies to all companies)", $"{labelAr} (ينطبق على جميع الشركات)", V.ReadinessTodo, V.ScopeWorkspace,
                SetupHref(key, id)));
        }
        Workspace("emailConfig", "Email / SMTP", "البريد الإلكتروني", await email.IsConfiguredAsync(tenantId, ct));
        Workspace("notificationTemplates", "Notification templates", "قوالب الإشعارات", await db.NotificationTemplates.AnyAsync(x => x.TenantId == tenantId, ct));
        Workspace("systemSettings", "System settings", "إعدادات النظام", await db.SystemSettings.AnyAsync(x => x.TenantId == tenantId, ct));
        Workspace("masterData", "Employee reference lists", "القوائم المرجعية للموظفين", await db.MasterDataTypes.AnyAsync(x => x.TenantId == tenantId, ct));
        Workspace("numberingRules", "Numbering", "الترقيم", await db.NumberingRules.AnyAsync(x => x.TenantId == tenantId, ct));
        Workspace("fiscalYears", "Fiscal years", "السنوات المالية", await db.FiscalYears.AnyAsync(x => x.TenantId == tenantId, ct));
        return items;
    }

    /// <summary>The company-admin mode the latest wizard draft submitted for this company asked for (the one that created
    /// it, or a later existing-company completion), if any.</summary>
    private static async Task<string?> RequestedAdminModeAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var body = await db.CompanySetupDrafts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.ResultCompanyId == companyId && d.Status == CompanySetupDraftStatuses.Submitted)
            .OrderByDescending(d => d.SubmittedAtUtc).ThenByDescending(d => d.Id)
            .Select(d => d.Body).FirstOrDefaultAsync(ct);
        if (body is null) return null;
        var row = new CompanySetupDraftRecord { Body = body };
        return CompanySetupService.ReadBody(row)?.CompanyAdmin?.Mode;
    }

    /// <summary>The refusal code the wizard recorded the last time it could not write this company's super admin.</summary>
    private static async Task<string?> LastAdminFailureCodeAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var metadata = await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId,
                "Create-company readiness: why the wizard could not write this company's super admin (its own failure audit).")
            .AsNoTracking()
            .Where(a => a.Action == CompanySetupService.AdminFailedAction && a.EntityId == companyId.ToString())
            .OrderByDescending(a => a.CreatedAtUtc).Select(a => a.Metadata).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(metadata)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadata);
            return doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == System.Text.Json.JsonValueKind.String ? code.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
