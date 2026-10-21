using System.Globalization;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Application.Organization;
using Zayra.Api.Application.Setup;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;
using V = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary;
using Areas = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary.Areas;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>Who is asking, as the wizard's rules need to know it. Built by the controller from the request.</summary>
/// <param name="IsGroupScope">The token grants tenant-wide company authority (before the switcher narrows reads).</param>
/// <param name="IsNarrowedBySwitcher">An <c>X-Company-Id</c> header narrowed this request to one company. The
/// commit writes a company-scoped operational row (the cutover) for a company the selection does not name, which
/// the DbContext write guard refuses — so a narrowed request is refused up front, by name.</param>
public sealed record CompanySetupCaller(Guid TenantId, Guid UserId, bool IsGroupScope, bool IsNarrowedBySwitcher, RequestContext Context);

/// <summary>What the commit will do, resolved by the planner. The commit executes exactly this, nothing more.</summary>
public sealed class CompanySetupPlanModel
{
    public required string Country { get; init; }
    public required string Currency { get; init; }
    public required CompanyRequest CompanyRequest { get; init; }
    public required List<CompanySetupBranch> Branches { get; init; }
    public required List<CompanySetupCostCentre> CostCentres { get; init; }
    /// <summary>New departments only, parents before children.</summary>
    public required List<CompanySetupDepartment> NewDepartments { get; init; }
    public required Dictionary<string, Guid> ExistingDepartmentIds { get; init; }
    public required List<CompanySetupDesignation> NewDesignations { get; init; }
    public required List<CompanySetupGrade> NewGrades { get; init; }
    public required Dictionary<string, Guid> ExistingGradeIds { get; init; }
    public required string GradeCurrency { get; init; }
    public required DraftEmployeeIdRule EmployeeIdRule { get; init; }
    public required DateOnly CutoverDate { get; init; }
    public DraftHolidayCalendar? HolidayCalendar { get; init; }
    public required IReadOnlySet<string> MissingLeaveDefaults { get; init; }
    /// <summary>Non-null when the tenant-default GCC row for <see cref="Country"/> must be created (Decision 11).</summary>
    public GCCComplianceSetting? NewGccDefault { get; init; }
    /// <summary>Non-null when the user changed the working week: the company override row to write.</summary>
    public GCCComplianceSetting? WorkWeekOverride { get; init; }
    public required CompanySetupCompanyAdmin CompanyAdmin { get; init; }
    /// <summary>The admin step's planned action: create (invite / grant) or reuse (group admins, an existing group admin).</summary>
    public required string CompanyAdminAction { get; init; }
    public required IReadOnlyList<PlanItem> Items { get; init; }
}

/// <summary>
/// THE planner of the create-company wizard (design contract Decision 5). <c>POST /dry-run</c> and the commit
/// both call <see cref="PlanAsync"/>, so the review screen renders exactly what the commit will do: every row is
/// <c>create</c>, <c>reuse</c> or <c>conflict</c>, and any conflict or problem blocks the commit.
///
/// <para>It is deterministic and model-free (Decision 1): it reads the tenant's own rows and the country pack,
/// and nothing else. Every tenant-wide read goes through <see cref="ScopedBypass.TenantWide{T}"/> on purpose:
/// a code clash with a sister company's cost centre, or a soft-deleted department still holding a unique code,
/// must be seen whatever company the caller's switcher selects — a clash the planner cannot see is a 500 at
/// commit.</para>
/// </summary>
public sealed class CompanySetupPlanner
{
    public const int MaxBodyBytes = 131072;
    private const string Why = "Create-company wizard planning: uniqueness and reuse of tenant-wide codes and the company register must be judged across every legal entity of the tenant, including soft-deleted rows that still hold a unique code.";

    private readonly ZayraDbContext _db;
    private readonly IAccessManagementService _access;
    private readonly IWorkWeekService _workWeek;

    public CompanySetupPlanner(ZayraDbContext db, IAccessManagementService access, IWorkWeekService workWeek)
    {
        _db = db;
        _access = access;
        _workWeek = workWeek;
    }

    private sealed class Collector
    {
        public readonly List<PlanItem> Items = [];
        public readonly List<Problem> Problems = [];
        public readonly List<Problem> Warnings = [];
        public void Item(string area, string code, string label, string action, string? reason = null, string? labelAr = null) =>
            Items.Add(new(area, code, label, action, reason, Blank(labelAr)));
        public void Problem(int step, string field, string code, string message, string? ar = null) => Problems.Add(new(step, Path(step, field), code, message, ar));
        public void Warn(int step, string field, string code, string message, string? ar = null) => Warnings.Add(new(step, Path(step, field), code, message, ar));
        /// <summary>A conflict is both a plan row and a field problem, so the review and the step both show it.</summary>
        public void Conflict(string area, string itemCode, string label, int step, string field, string code, string message, string? ar = null, string? labelAr = null)
        {
            Items.Add(new(area, itemCode, label, V.ActionConflict, message, Blank(labelAr)));
            Problems.Add(new(step, Path(step, field), code, message, ar));
        }
        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        /// <summary>The contract's field path: dotted lower camel with numeric indexes ("branches.0.code"); a
        /// step-2 field names the company ("company.registrationNumber").</summary>
        private static string Path(int step, string field)
        {
            var dotted = System.Text.RegularExpressions.Regex.Replace(field, @"\[(\d+)\]", ".$1");
            return step == 2 && !dotted.StartsWith("company.", StringComparison.Ordinal) && dotted != "company" ? $"company.{dotted}" : dotted;
        }
    }

    public async Task<(CompanySetupPlan Plan, CompanySetupPlanModel? Model)> PlanAsync(
        CompanySetupCaller caller, CompanySetupDraftBody? body, CancellationToken ct)
    {
        var c = new Collector();
        var tenantId = caller.TenantId;
        if (body is null || body.Company is null)
        {
            c.Problem(2, "company", "body_required", "The company details are missing.", "بيانات الشركة مفقودة.");
            return (Finish(c), null);
        }

        // ── Step 1: who may create, and whether this tenant may create one now (Decision 12) ──────────────
        if (!caller.IsGroupScope)
            c.Problem(1, "scope", CompanySetupGates.GroupScopeRequiredCode, CompanySetupGates.GroupScopeRequiredMessage,
                CompanySetupGates.GroupScopeRequiredMessageAr);
        if (caller.IsNarrowedBySwitcher)
            c.Problem(1, "companySwitcher", "company_switcher_selected",
                "A company is selected in the company switcher. Switch it to all companies before creating a new company.",
                "تم اختيار شركة في مبدّل الشركات. اختر جميع الشركات قبل إنشاء شركة جديدة.");
        var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct);
        if (!gate.Allowed)
            c.Problem(1, "creation", gate.ErrorCode, gate.Message);
        else if (gate.AsDraft)
            c.Problem(1, "creation", CompanySetupGates.PlatformActivationDisabledCode, CompanySetupGates.PlatformActivationDisabledMessage,
                CompanySetupGates.PlatformActivationDisabledMessageAr);

        // ── Step 2: the company ────────────────────────────────────────────────────────────────────────
        var co = body.Company;
        var country = CountryCodeStandard.NormalizeToIso2(co.CountryCode) ?? string.Empty;
        var isSa = country == "SA";
        var currency = (co.Currency ?? string.Empty).Trim().ToUpperInvariant();
        var legalNameEn = (co.LegalNameEn ?? string.Empty).Trim();
        var registration = (co.RegistrationNumber ?? string.Empty).Trim();
        var mol = (co.MolEstablishmentNumber ?? string.Empty).Trim();
        var unified = (co.UnifiedNationalNumber ?? string.Empty).Trim();
        var emailDomain = CompanyEmailDomainRules.Normalize(co.EmailDomain);

        if (legalNameEn.Length == 0) c.Problem(2, "legalNameEn", "required", "Enter the legal name in English.", "أدخل الاسم القانوني بالإنجليزية.");
        if (isSa && string.IsNullOrWhiteSpace(co.LegalNameAr)) c.Problem(2, "legalNameAr", "required", "Enter the legal name in Arabic.", "أدخل الاسم القانوني بالعربية.");
        if (country.Length == 0) c.Problem(2, "countryCode", "country_invalid", $"Choose the company's country (ISO code). '{co.CountryCode}' is not recognised.");
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) c.Problem(2, "currency", "currency_invalid", "Choose the company's currency (a 3-letter code).");
        else if (isSa && currency != "SAR") c.Problem(2, "currency", "currency_must_be_sar", "A Saudi company pays in SAR.", "الشركة السعودية تدفع بالريال السعودي.");
        if (registration.Length == 0) c.Problem(2, "registrationNumber", "required", "Enter the commercial registration (CR) number.", "أدخل رقم السجل التجاري.");
        else if (isSa && (registration.Length != 10 || !registration.All(char.IsAsciiDigit)))
            c.Problem(2, "registrationNumber", "cr_number_invalid", "A Saudi CR number is 10 digits.", "رقم السجل التجاري السعودي 10 أرقام.");
        if (isSa)
        {
            var issues = new List<SaudiBankExportIssueDto>();
            KsaWageFileRules.ValidateEstablishmentId(mol.Length == 0 ? null : mol, issues);
            foreach (var issue in issues) c.Problem(2, "molEstablishmentNumber", issue.Code, issue.Message);
            if (unified.Length > 0)
            {
                issues.Clear();
                KsaWageFileRules.ValidateNationalUnifiedNo(unified, required: false, issues);
                foreach (var issue in issues) c.Problem(2, "unifiedNationalNumber", issue.Code, issue.Message);
            }
        }
        if (!OrganizationSetupService.IsValidEmailDomainOrEmpty(emailDomain))
            c.Problem(2, "emailDomain", "email_domain_invalid", $"'{co.EmailDomain}' is not a valid email domain (for example acme.sa).");
        else if (emailDomain.Length > 0)
        {
            try { await CompanyEmailDomainRules.EnsureClaimableAsync(_db, tenantId, emailDomain, ct); }
            catch (EmailDomainRefusedException ex) { c.Problem(2, "emailDomain", ex.Code, ex.Message); }
        }
        if (!TryParseMonth(co.FirstPayrollMonth, out var cutoverDate))
            c.Problem(2, "firstPayrollMonth", "month_invalid", "Choose the first payroll month (YYYY-MM).", "اختر أول شهر للرواتب.");

        // The caller's effective permissions, read the way the writers read them (database, not claims).
        AccessCeilingDto? ceiling = null;
        try { ceiling = await _access.GetAccessCeilingAsync(tenantId, caller.Context with { TenantId = tenantId }, ct); }
        catch (PrivilegeCeilingException) { ceiling = null; }
        bool Holds(string permission) => ceiling?.HeldPermissions.Contains(permission, StringComparer.Ordinal) == true;
        if (emailDomain.Length > 0 && !Holds(CompanyEmailDomainRules.SecurityPermission))
            c.Problem(2, "emailDomain", CompanyEmailDomainRules.NeedsSecurityAdminCode, CompanyEmailDomainRules.NeedsSecurityAdminMessage);
        var erp = string.IsNullOrWhiteSpace(co.ErpCompanyCode) ? null : co.ErpCompanyCode.Trim();
        if (erp is not null)
        {
            if (erp.Length > 30 || !erp.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
                c.Problem(2, "erpCompanyCode", "erp_company_code_invalid", "An ERP company code may be at most 30 characters: letters, digits, '.', '_' and '-'.");
            if (!Holds(OrganizationSetupService.ErpCompanyCodePermission))
                c.Problem(2, "erpCompanyCode", "erp_company_code_needs_finance",
                    "Only a finance user who manages the general ledger can set a company's ERP company code. Leave it empty, or ask finance to set it later.");
        }

        var companies = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
            .Where(x => !x.IsDeleted)
            .Select(x => new { x.Id, x.LegalNameEn, x.RegistrationNumber, x.ErpCompanyCode })
            .ToListAsync(ct);
        var label = legalNameEn.Length > 0 ? legalNameEn : "New company";
        var companyConflict = false;
        if (legalNameEn.Length > 0 && companies.Any(x => string.Equals(x.LegalNameEn.Trim(), legalNameEn, StringComparison.OrdinalIgnoreCase)))
        {
            companyConflict = true;
            c.Conflict(Areas.Company, legalNameEn, label, 2, "legalNameEn", CompanySetupGates.NameTakenCode,
                $"A company named '{legalNameEn}' already exists in this workspace. Each legal entity needs its own name.",
                $"توجد شركة باسم '{legalNameEn}' في مساحة العمل.", co.LegalNameAr);
        }
        if (registration.Length > 0 && companies.Any(x => string.Equals(x.RegistrationNumber.Trim(), registration, StringComparison.OrdinalIgnoreCase)))
        {
            companyConflict = true;
            c.Conflict(Areas.Company, registration, label, 2, "registrationNumber", CompanySetupGates.CrTakenCode,
                $"CR number {registration} already belongs to a company in this workspace.",
                $"رقم السجل التجاري {registration} مستخدم لشركة أخرى.", co.LegalNameAr);
        }
        if (erp is not null && companies.Any(x => (x.ErpCompanyCode != null && string.Equals(x.ErpCompanyCode, erp, StringComparison.OrdinalIgnoreCase))
                                                 || (string.IsNullOrEmpty(x.ErpCompanyCode) && string.Equals(x.RegistrationNumber, erp, StringComparison.OrdinalIgnoreCase))))
        {
            companyConflict = true;
            c.Conflict(Areas.Company, erp, label, 2, "erpCompanyCode", "erp_company_code_taken",
                $"ERP company code {erp} is already used by another company. Each company needs its own code.");
        }
        if (!companyConflict) c.Item(Areas.Company, registration, label, V.ActionCreate, labelAr: co.LegalNameAr);

        // ── Step 3: branches and locations (per company: all new) ─────────────────────────────────────────
        var branches = (body.Branches ?? []).ToList();
        if (branches.Count == 0) c.Problem(3, "branches", "branch_required", "Add at least one branch.", "أضف فرعاً واحداً على الأقل.");
        var heads = branches.Count(b => b.IsHeadOffice);
        if (branches.Count > 0 && heads != 1)
            c.Problem(3, "branches", "head_office_exactly_one", "Mark exactly one branch as the head office.", "حدد فرعاً واحداً فقط كمقر رئيسي.");
        var branchCodes = new HashSet<string>(StringComparer.Ordinal);
        var existingLocationCodes = (await ScopedBypass.TenantWide(_db.Locations, tenantId, Why).AsNoTracking()
            .Select(x => x.Code).ToListAsync(ct)).Select(OrgCodes.Normalize).ToHashSet(StringComparer.Ordinal);
        var bodyLocationCodes = new HashSet<string>(StringComparer.Ordinal);
        var normalisedBranches = new List<CompanySetupBranch>();
        for (var i = 0; i < branches.Count; i++)
        {
            var b = branches[i];
            var code = OrgCodes.Normalize(b.Code);
            var f = $"branches[{i}]";
            if (code.Length == 0) { c.Problem(3, $"{f}.code", "required", "Enter the branch code."); continue; }
            if (code.Length > 40) c.Problem(3, $"{f}.code", "too_long", "A branch code is at most 40 characters.");
            if (!branchCodes.Add(code)) { c.Problem(3, $"{f}.code", "duplicate_code", $"Branch code {code} appears twice."); continue; }
            if (string.IsNullOrWhiteSpace(b.NameEn)) c.Problem(3, $"{f}.nameEn", "required", "Enter the branch name in English.");
            if (string.IsNullOrWhiteSpace(b.City)) c.Problem(3, $"{f}.city", "required", "Enter the branch city.");
            var tz = string.IsNullOrWhiteSpace(b.TimeZoneId) ? HomeJurisdiction.TimeZoneFor(country) : b.TimeZoneId.Trim();
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
                c.Problem(3, $"{f}.timeZoneId", "time_zone_invalid", $"'{tz}' is not a recognised time zone (for example Asia/Riyadh).");
            var locations = (b.Locations ?? []).ToList();
            var normalisedLocations = new List<CompanySetupLocation>();
            c.Item(Areas.Branch, code, (b.NameEn ?? string.Empty).Trim(), V.ActionCreate, labelAr: b.NameAr);
            if (locations.Count == 0)
                c.Warn(3, $"{f}.locations", "branch_without_location",
                    $"Branch {code} has no location, so its employees can punch at any geofenced site in the workspace.",
                    $"لا يوجد موقع للفرع {code}، لذا يمكن لموظفيه تسجيل الحضور في أي موقع ضمن مساحة العمل.");
            for (var j = 0; j < locations.Count; j++)
            {
                var l = locations[j];
                var lf = $"{f}.locations[{j}]";
                var lcode = OrgCodes.Normalize(l.Code);
                if (lcode.Length == 0) { c.Problem(3, $"{lf}.code", "required", "Enter the location code."); continue; }
                if (!bodyLocationCodes.Add(lcode)) { c.Problem(3, $"{lf}.code", "duplicate_code", $"Location code {lcode} appears twice."); continue; }
                if (string.IsNullOrWhiteSpace(l.NameEn)) c.Problem(3, $"{lf}.nameEn", "required", "Enter the location name in English.");
                if (l.Latitude is < -90 or > 90) c.Problem(3, $"{lf}.latitude", "latitude_invalid", "Latitude must be between -90 and 90.");
                if (l.Longitude is < -180 or > 180) c.Problem(3, $"{lf}.longitude", "longitude_invalid", "Longitude must be between -180 and 180.");
                if (l.RadiusMeters is <= 0 or > 100_000) c.Problem(3, $"{lf}.radiusMeters", "radius_invalid", "The geofence radius must be between 1 and 100,000 metres.");
                if ((l.Latitude is null) != (l.Longitude is null)) c.Problem(3, $"{lf}.latitude", "coordinates_incomplete", "Enter both latitude and longitude, or neither.");
                if (existingLocationCodes.Contains(lcode))
                    c.Conflict(Areas.Location, lcode, (l.NameEn ?? string.Empty).Trim(), 3, $"{lf}.code", "location_code_taken",
                        $"Location code {lcode} is already used in this workspace. Location codes are unique across all companies.", labelAr: l.NameAr);
                else c.Item(Areas.Location, lcode, (l.NameEn ?? string.Empty).Trim(), V.ActionCreate, labelAr: l.NameAr);
                normalisedLocations.Add(l with { Code = lcode });
            }
            normalisedBranches.Add(b with { Code = code, TimeZoneId = tz, Locations = normalisedLocations });
        }

        // ── Step 4: cost centres (per company), shared catalog (tenant-wide) ──────────────────────────────
        var existingCostCentres = (await ScopedBypass.TenantWide(_db.CostCenters, tenantId, Why).AsNoTracking()
            .Select(x => x.Code).ToListAsync(ct)).Select(OrgCodes.Normalize).ToHashSet(StringComparer.Ordinal);
        var costCentres = new List<CompanySetupCostCentre>();
        var bodyCostCentreCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (cc, i) in (body.CostCentres ?? []).Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(cc.Code);
            var f = $"costCentres[{i}]";
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the cost centre code."); continue; }
            if (!bodyCostCentreCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Cost centre code {code} appears twice."); continue; }
            if (string.IsNullOrWhiteSpace(cc.NameEn)) c.Problem(4, $"{f}.nameEn", "required", "Enter the cost centre name in English.");
            // Decision 7: cost-centre codes are unique across the tenant; a clash is a conflict, never a reuse —
            // reusing would hand this company another company's cost centre.
            if (existingCostCentres.Contains(code))
                c.Conflict(Areas.CostCentre, code, (cc.NameEn ?? string.Empty).Trim(), 4, $"{f}.code", "cost_centre_code_taken",
                    $"Cost centre code {code} is already used by another company in this workspace. Choose a different code.",
                    $"رمز مركز التكلفة {code} مستخدم لدى شركة أخرى. اختر رمزاً مختلفاً.", cc.NameAr);
            else c.Item(Areas.CostCentre, code, (cc.NameEn ?? string.Empty).Trim(), V.ActionCreate, labelAr: cc.NameAr);
            costCentres.Add(cc with { Code = code });
        }

        var departments = await ScopedBypass.TenantWide(_db.Departments, tenantId, Why).AsNoTracking()
            .Select(x => new { x.Id, x.Code, x.IsDeleted, x.NameAr }).ToListAsync(ct);
        var deptByCode = departments.GroupBy(x => OrgCodes.Normalize(x.Code)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var grades = await ScopedBypass.TenantWide(_db.Grades, tenantId, Why).AsNoTracking()
            .Select(x => new { x.Id, x.Code, x.IsDeleted, x.IsActive, x.Currency, x.NameAr }).ToListAsync(ct);
        var gradeByCode = grades.GroupBy(x => OrgCodes.Normalize(x.Code)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var designations = await ScopedBypass.TenantWide(_db.Designations, tenantId, Why).AsNoTracking()
            .Select(x => new { x.Code, x.IsDeleted, x.TitleAr }).ToListAsync(ct);
        var designationByCode = designations.GroupBy(x => OrgCodes.Normalize(x.Code)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        void SharedAdd(string area, string code, string itemLabel, int index, string listName, string? itemLabelAr)
        {
            // Decision 6: a new shared-catalog code changes every company of the group, so only group scope adds one.
            if (!caller.IsGroupScope)
                c.Conflict(area, code, itemLabel, 4, $"{listName}[{index}].code", "shared_catalog_needs_group_scope",
                    $"{code} would be added to the shared group catalog, which only a group administrator can change.",
                    $"إضافة {code} إلى الدليل المشترك للمجموعة تتطلب مسؤول المجموعة.", itemLabelAr);
            else c.Item(area, code, itemLabel, V.ActionCreate, labelAr: itemLabelAr);
        }

        // Grades: one currency per tenant (Decision 13).
        var activeGradeCurrencies = grades.Where(g => !g.IsDeleted && g.IsActive)
            .GroupBy(g => (g.Currency ?? string.Empty).Trim().ToUpperInvariant())
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Where(k => k.Length > 0).ToList();
        var gradeCurrency = activeGradeCurrencies.FirstOrDefault() ?? currency;
        if (activeGradeCurrencies.Count > 0 && currency.Length == 3 && !string.Equals(gradeCurrency, currency, StringComparison.Ordinal))
            c.Warn(4, "grades", "grades_currency_differs",
                $"Grade salary bands in this workspace are in {gradeCurrency}, and this company pays in {currency}. Bands are one currency per workspace; new grades are created in {gradeCurrency}.",
                $"نطاقات رواتب الدرجات بعملة {gradeCurrency} وهذه الشركة تدفع بعملة {currency}.");
        var newGrades = new List<CompanySetupGrade>();
        var bodyGradeCodes = new HashSet<string>(StringComparer.Ordinal);
        var existingGradeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (g, i) in (body.Grades ?? []).Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(g.Code);
            var f = $"grades[{i}]";
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the grade code."); continue; }
            if (!bodyGradeCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Grade code {code} appears twice."); continue; }
            var name = (g.NameEn ?? string.Empty).Trim();
            if (gradeByCode.TryGetValue(code, out var existing))
            {
                if (existing.IsDeleted)
                    c.Conflict(Areas.Grade, code, name, 4, $"{f}.code", "grade_code_deleted",
                        $"Grade code {code} belongs to a deleted grade. Restore it in Setup, or choose a different code.");
                else
                {
                    existingGradeIds[code] = existing.Id;
                    c.Item(Areas.Grade, code, name, V.ActionReuse, "Existing shared grade; it is used as it is (read-only here).", existing.NameAr ?? g.NameAr);
                }
                continue;
            }
            if (name.Length == 0) c.Problem(4, $"{f}.nameEn", "required", "Enter the grade name in English.");
            if (g.Level is < 0 or > 100) c.Problem(4, $"{f}.level", "level_invalid", "The grade level must be between 0 and 100.");
            if (g.MinSalary < 0 || g.MidSalary < 0 || g.MaxSalary < 0 || g.MinSalary > g.MidSalary || g.MidSalary > g.MaxSalary)
                c.Problem(4, $"{f}.minSalary", "salary_band_invalid", "A salary band must be ordered minimum ≤ midpoint ≤ maximum.",
                    "يجب أن يكون نطاق الراتب: الحد الأدنى ≤ المنتصف ≤ الحد الأقصى.");
            SharedAdd(Areas.Grade, code, name, i, "grades", g.NameAr);
            newGrades.Add(g with { Code = code });
        }

        // Departments: existing codes are reused and NEVER re-linked (Decision 7).
        var newDepartments = new List<CompanySetupDepartment>();
        var existingDepartmentIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var bodyDepartmentCodes = new HashSet<string>(StringComparer.Ordinal);
        var departmentRows = (body.Departments ?? []).ToList();
        foreach (var (d, i) in departmentRows.Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(d.Code);
            var f = $"departments[{i}]";
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the department code."); continue; }
            if (!bodyDepartmentCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Department code {code} appears twice."); continue; }
            var name = (d.NameEn ?? string.Empty).Trim();
            if (deptByCode.TryGetValue(code, out var existing))
            {
                if (existing.IsDeleted)
                    c.Conflict(Areas.Department, code, name, 4, $"{f}.code", "department_code_deleted",
                        $"Department code {code} belongs to a deleted department. Restore it in Setup, or choose a different code.");
                else
                {
                    existingDepartmentIds[code] = existing.Id;
                    var relinks = !string.IsNullOrWhiteSpace(d.BranchCode) || !string.IsNullOrWhiteSpace(d.CostCentreCode) || !string.IsNullOrWhiteSpace(d.ParentCode);
                    c.Item(Areas.Department, code, name, V.ActionReuse,
                        "Existing shared department; it keeps its current branch, cost centre and parent.", string.IsNullOrEmpty(existing.NameAr) ? d.NameAr : existing.NameAr);
                    if (relinks)
                        c.Warn(4, $"{f}.code", "existing_department_not_relinked",
                            $"{code} already exists in the shared catalog, so the branch, cost centre and parent entered here are not applied to it.",
                            $"القسم {code} موجود مسبقاً، لذا لن يتم تغيير فرعه أو مركز تكلفته.");
                }
                continue;
            }
            if (name.Length == 0) c.Problem(4, $"{f}.nameEn", "required", "Enter the department name in English.");
            var branchCode = OrgCodes.Normalize(d.BranchCode);
            if (branchCode.Length > 0 && !branchCodes.Contains(branchCode))
                c.Problem(4, $"{f}.branchCode", "branch_unknown", $"Branch {branchCode} is not one of this company's branches.");
            var ccCode = OrgCodes.Normalize(d.CostCentreCode);
            if (ccCode.Length > 0 && !bodyCostCentreCodes.Contains(ccCode))
                c.Problem(4, $"{f}.costCentreCode", "cost_centre_unknown", $"Cost centre {ccCode} is not one of this company's cost centres.");
            SharedAdd(Areas.Department, code, name, i, "departments", d.NameAr);
            newDepartments.Add(d with { Code = code, BranchCode = branchCode.Length == 0 ? null : branchCode, CostCentreCode = ccCode.Length == 0 ? null : ccCode,
                ParentCode = string.IsNullOrWhiteSpace(d.ParentCode) ? null : OrgCodes.Normalize(d.ParentCode) });
        }
        // Parents: an existing department, or a new one created earlier in the same commit. No cycles.
        var orderedDepartments = new List<CompanySetupDepartment>();
        {
            var pending = newDepartments.ToDictionary(d => d.Code, StringComparer.Ordinal);
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in newDepartments)
                if (d.ParentCode is { } p && !pending.ContainsKey(p) && !existingDepartmentIds.ContainsKey(p) && !(deptByCode.TryGetValue(p, out var ex) && !ex.IsDeleted))
                    c.Problem(4, "departments", "parent_unknown", $"Department {d.Code}'s parent {p} does not exist.");
            var progress = true;
            while (pending.Count > 0 && progress)
            {
                progress = false;
                foreach (var d in pending.Values.ToList())
                {
                    if (d.ParentCode is { } p && pending.ContainsKey(p) && !placed.Contains(p)) continue;
                    orderedDepartments.Add(d);
                    placed.Add(d.Code);
                    pending.Remove(d.Code);
                    progress = true;
                }
            }
            if (pending.Count > 0)
                c.Problem(4, "departments", "parent_cycle", $"Departments {string.Join(", ", pending.Keys)} are each other's parents. Break the loop.");
        }

        var newDesignations = new List<CompanySetupDesignation>();
        var bodyDesignationCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ds, i) in (body.Designations ?? []).Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(ds.Code);
            var f = $"designations[{i}]";
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the designation code."); continue; }
            if (!bodyDesignationCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Designation code {code} appears twice."); continue; }
            var title = (ds.TitleEn ?? string.Empty).Trim();
            if (designationByCode.TryGetValue(code, out var existing))
            {
                if (existing.IsDeleted)
                    c.Conflict(Areas.Designation, code, title, 4, $"{f}.code", "designation_code_deleted",
                        $"Designation code {code} belongs to a deleted designation. Restore it in Setup, or choose a different code.");
                else c.Item(Areas.Designation, code, title, V.ActionReuse, "Existing shared designation; it is used as it is (read-only here).",
                    string.IsNullOrEmpty(existing.TitleAr) ? ds.TitleAr : existing.TitleAr);
                continue;
            }
            if (title.Length == 0) c.Problem(4, $"{f}.titleEn", "required", "Enter the designation title in English.");
            var deptCode = OrgCodes.Normalize(ds.DepartmentCode);
            if (deptCode.Length > 0 && !bodyDepartmentCodes.Contains(deptCode) && !(deptByCode.TryGetValue(deptCode, out var dx) && !dx.IsDeleted))
                c.Problem(4, $"{f}.departmentCode", "department_unknown", $"Department {deptCode} does not exist.");
            var gradeCode = OrgCodes.Normalize(ds.GradeCode);
            if (gradeCode.Length > 0 && !bodyGradeCodes.Contains(gradeCode) && !(gradeByCode.TryGetValue(gradeCode, out var gx) && !gx.IsDeleted))
                c.Problem(4, $"{f}.gradeCode", "grade_unknown", $"Grade {gradeCode} does not exist.");
            SharedAdd(Areas.Designation, code, title, i, "designations", ds.TitleAr);
            newDesignations.Add(ds with { Code = code, DepartmentCode = deptCode.Length == 0 ? null : deptCode, GradeCode = gradeCode.Length == 0 ? null : gradeCode });
        }
        // An existing department reused by name only still resolves for designations below.
        foreach (var (code, row) in deptByCode)
            if (!row.IsDeleted) existingDepartmentIds.TryAdd(code, row.Id);
        foreach (var (code, row) in gradeByCode)
            if (!row.IsDeleted) existingGradeIds.TryAdd(code, row.Id);

        // ── Step 5: work and pay rules ───────────────────────────────────────────────────────────────────
        var rules = body.Rules;
        var numbering = rules?.EmployeeNumbering;
        var prefix = (numbering?.Prefix ?? string.Empty).Trim().ToUpperInvariant();
        if (numbering is null || prefix.Length == 0 || prefix.Length > 10 || !prefix.All(char.IsAsciiLetterOrDigit))
            c.Problem(5, "rules.employeeNumbering.prefix", "prefix_invalid", "The employee-number prefix is 1 to 10 letters or digits.");
        if (numbering is not null && numbering.PaddingLength is < 1 or > 10)
            c.Problem(5, "rules.employeeNumbering.paddingLength", "padding_invalid", "Padding is 1 to 10 digits.");
        if (numbering is not null && numbering.NextSequence < 1)
            c.Problem(5, "rules.employeeNumbering.nextSequence", "sequence_invalid", "The next number is 1 or more.");
        c.Item(Areas.EmployeeIdRule, prefix, "Employee numbering for this company", V.ActionCreate);
        if (cutoverDate != default)
        {
            c.Item(Areas.Cutover, cutoverDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Planned payroll start (first day of the first payroll month)", V.ActionCreate);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (cutoverDate < new DateOnly(today.Year, today.Month, 1))
                c.Warn(5, "company.firstPayrollMonth", "first_payroll_month_past", "The first payroll month is in the past. Opening balances will be needed for the months before it.");
        }

        DraftHolidayCalendar? holidayCalendar = null;
        if (rules?.HolidayCalendar?.UsePack == true && country.Length > 0)
        {
            holidayCalendar = SetupAssistantService.DeterministicCompanyDefaults(country, currency, legalNameEn).HolidayCalendar;
            if (holidayCalendar is null || holidayCalendar.Holidays.Count == 0)
            {
                holidayCalendar = null;
                c.Warn(5, "rules.holidayCalendar", "no_pack_holidays",
                    $"There is no built-in holiday list for {country}. Add the year's public holidays in Setup before running attendance or payroll.");
            }
            else
            {
                c.Item(Areas.HolidayCalendar, holidayCalendar.CalendarYear.ToString(CultureInfo.InvariantCulture), holidayCalendar.Name, V.ActionCreate,
                    labelAr: $"العطل الرسمية {holidayCalendar.CalendarYear}");
                c.Warn(5, "rules.holidayCalendar", "lunar_holidays_not_drafted",
                    "Only fixed-date holidays are added. Eid al-Fitr, Eid al-Adha and other lunar holidays are announced locally — add them once announced.",
                    "تتم إضافة العطل ذات التاريخ الثابت فقط. أضف عيد الفطر وعيد الأضحى عند الإعلان عنهما.");
            }
        }

        IReadOnlySet<string> missingLeave = new HashSet<string>();
        if (rules?.LeavePolicies?.UsePack == true && country.Length > 0)
        {
            missingLeave = await TenantProvisioningBundle.MissingDefaultLeavePoliciesAsync(_db, tenantId, country, ct);
            foreach (var (typeCode, typeLabel) in TenantProvisioningBundle.DefaultLeavePolicyTypes)
                c.Item(Areas.LeavePolicy, typeCode, $"Default {typeLabel} — {country}",
                    missingLeave.Contains(typeCode) ? V.ActionCreate : V.ActionReuse,
                    missingLeave.Contains(typeCode)
                        ? $"Workspace-wide statutory default for {country}; applies to every {country} company."
                        : $"The workspace's {country} default already applies to this company.");
        }

        // Decision 11: the tenant-default GCC row for the country, created only when missing, never overwritten.
        GCCComplianceSetting? newGccDefault = null;
        GCCComplianceSetting? tenantDefaultGcc = null;
        if (country.Length > 0)
        {
            tenantDefaultGcc = await ScopedBypass.TenantWide(_db.GCCComplianceSettings, tenantId, Why).AsNoTracking()
                .Where(x => x.CompanyId == null && x.CountryCode == country).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (tenantDefaultGcc is null)
            {
                newGccDefault = CompanySetupPack.TenantDefaultGccRow(tenantId, country);
                c.Item(Areas.GccSetting, country, $"Country settings for {country} (end of service, weekend)", V.ActionCreate,
                    "No settings exist for this country yet; the country pack defaults are written once and never overwritten.");
            }
            else c.Item(Areas.GccSetting, country, $"Country settings for {country}", V.ActionReuse, "Already configured for this workspace.");
        }

        GCCComplianceSetting? workWeekOverride = null;
        if (country.Length > 0)
        {
            var resolved = await _workWeek.ResolveAsync(tenantId, null, country, ct);
            var resolvedWeekend = newGccDefault is not null
                ? WorkWeekParser.ParseWeekendDays(newGccDefault.WeekendDays) ?? resolved.WeekendDays
                : resolved.WeekendDays;
            var resolvedText = CompanySetupPack.FormatWeekend(resolvedWeekend);
            var requested = rules?.WorkWeek;
            if (requested is { Override: true })
            {
                var parsed = WorkWeekParser.ParseWeekendDays(requested.WeekendDays);
                if (parsed is null || parsed.Count == 0 || parsed.Count > 3)
                    c.Problem(5, "rules.workWeek.weekendDays", "weekend_invalid", "Choose the weekend (rest) days, for example Fri,Sat.");
                else if (parsed.SetEquals(resolvedWeekend))
                    c.Item(Areas.WorkWeek, resolvedText, $"Weekend {resolvedText}", V.ActionReuse, "Same as the workspace setting; no override is written.");
                else
                {
                    var text = CompanySetupPack.FormatWeekend(parsed);
                    workWeekOverride = CompanySetupPack.CompanyOverrideRow(tenantId, country, tenantDefaultGcc ?? newGccDefault, parsed);
                    c.Item(Areas.WorkWeek, text, $"Weekend {text} for this company", V.ActionCreate, $"Overrides the workspace weekend ({resolvedText}) for this company only.");
                }
            }
            else c.Item(Areas.WorkWeek, resolvedText, $"Weekend {resolvedText}", V.ActionReuse, "The workspace working week applies.");
        }

        // ── Step 6: company super admin ──────────────────────────────────────────────────────────────────
        var admin = body.CompanyAdmin ?? new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null);
        var adminAction = await PlanCompanyAdminAsync(c, caller, admin, emailDomain, ceiling, ct);

        var plan = Finish(c);
        if (!plan.Ok) return (plan, null);

        var request = new CompanyRequest(
            LegalNameEn: legalNameEn,
            LegalNameAr: co.LegalNameAr?.Trim(),
            TradeName: string.IsNullOrWhiteSpace(co.TradeName) ? legalNameEn : co.TradeName.Trim(),
            CountryCode: country,
            Jurisdiction: null,
            RegistrationNumber: registration,
            TaxNumber: co.TaxNumber?.Trim(),
            WpsEmployerId: mol,
            GosiEmployerId: co.GosiEmployerId?.Trim(),
            // Decision: the Qiwa establishment id defaults to the MOL establishment number.
            QiwaEstablishmentId: string.IsNullOrWhiteSpace(co.QiwaEstablishmentId) ? mol : co.QiwaEstablishmentId.Trim(),
            DefaultCurrency: currency,
            EmailDomain: emailDomain,
            WorkEmailPattern: co.WorkEmailPattern,
            IsActive: true,
            ErpCompanyCode: erp,
            UnifiedNationalNumber: unified.Length == 0 ? null : unified);

        return (plan, new CompanySetupPlanModel
        {
            Country = country,
            Currency = currency,
            CompanyRequest = request,
            Branches = normalisedBranches,
            CostCentres = costCentres,
            NewDepartments = orderedDepartments,
            ExistingDepartmentIds = existingDepartmentIds,
            NewDesignations = newDesignations,
            NewGrades = newGrades,
            ExistingGradeIds = existingGradeIds,
            GradeCurrency = gradeCurrency,
            EmployeeIdRule = new DraftEmployeeIdRule(prefix, numbering!.UseCountryPrefix, numbering.UseBranchPrefix, numbering.UseDepartmentPrefix,
                numbering.UseYear, numbering.PaddingLength, numbering.NextSequence, AllowManualOverride: false),
            CutoverDate = cutoverDate,
            HolidayCalendar = holidayCalendar,
            MissingLeaveDefaults = missingLeave,
            NewGccDefault = newGccDefault,
            WorkWeekOverride = workWeekOverride,
            CompanyAdmin = admin,
            CompanyAdminAction = adminAction,
            Items = plan.Items,
        });
    }

    /// <summary>
    /// Step 6. The admin hierarchy is Group Super Admin (Admin role, group scope) → Company Super Admin (Admin role,
    /// this company only). In KynexOne a role is held workspace-wide and company access is a separate list, so
    /// "Admin of this company only" is exactly: the Admin role + a single selected-company grant + no group scope.
    /// </summary>
    private async Task<string> PlanCompanyAdminAsync(Collector c, CompanySetupCaller caller, CompanySetupCompanyAdmin admin,
        string companyEmailDomain, AccessCeilingDto? ceiling, CancellationToken ct)
    {
        var mode = (admin.Mode ?? string.Empty).Trim();
        if (mode == V.AdminGroupOnly)
        {
            c.Item(Areas.CompanyAdmin, V.AdminGroupOnly, "Group super admins will manage this company", V.ActionReuse, labelAr: "سيدير مسؤولو المجموعة هذه الشركة");
            return V.ActionReuse;
        }
        if (mode is not (V.AdminInvite or V.AdminExisting))
        {
            c.Problem(6, "companyAdmin.mode", "mode_invalid", "Choose how this company will be administered.");
            return V.ActionConflict;
        }

        // Only a group-scope Admin who may assign the Admin role grants it — the ceiling User Management enforces.
        var adminRole = ceiling?.Roles.FirstOrDefault(r => string.Equals(r.Name, "Admin", StringComparison.OrdinalIgnoreCase));
        var mayGrant = MayGrantCompanyAdmin(caller, ceiling);
        var key = mode == V.AdminInvite ? (admin.Email ?? string.Empty).Trim().ToLowerInvariant() : admin.ExistingUserId?.ToString() ?? string.Empty;
        if (!mayGrant)
        {
            c.Conflict(Areas.CompanyAdmin, key, "Company super admin", 6, "companyAdmin.mode", "admin_grant_not_permitted",
                adminRole?.AssignRefusalEn ?? "Only a group super admin who holds the Admin role can make someone a company super admin.",
                adminRole?.AssignRefusalAr ?? "يمكن لمسؤول المجموعة الأعلى فقط تعيين مسؤول للشركة.");
            return V.ActionConflict;
        }

        if (mode == V.AdminInvite)
        {
            var email = key;
            if (string.IsNullOrWhiteSpace(admin.FullNameEn)) c.Problem(6, "companyAdmin.fullNameEn", "required", "Enter the company super admin's full name in English.");
            if (!IsEmail(email)) { c.Problem(6, "companyAdmin.email", "email_invalid", "Enter the company super admin's official work email."); return V.ActionConflict; }
            var host = email[(email.LastIndexOf('@') + 1)..];
            // Owner rule: KynexOne never creates mailboxes; a login is an official company-domain email only.
            if (CompanyEmailDomainRules.PublicDomains.Contains(host))
            {
                c.Problem(6, "companyAdmin.email", "admin_email_public_domain",
                    $"'{host}' is a public email service. Use the person's official company email.", "استخدم البريد الرسمي للشركة.");
                return V.ActionConflict;
            }
            if (companyEmailDomain.Length > 0 && !string.Equals(host, companyEmailDomain, StringComparison.Ordinal))
            {
                c.Problem(6, "companyAdmin.email", "admin_email_off_domain",
                    $"The company super admin's email must be on the company's email domain (@{companyEmailDomain}).",
                    $"يجب أن يكون البريد على نطاق الشركة (@{companyEmailDomain}).");
                return V.ActionConflict;
            }
            var normalized = AuthService.Normalize(email);
            var taken = await ScopedBypass.TenantWide(_db.Users, caller.TenantId, Why).AsNoTracking()
                .AnyAsync(u => u.NormalizedEmail == normalized, ct);
            if (taken)
            {
                c.Conflict(Areas.CompanyAdmin, email, admin.FullNameEn?.Trim() ?? email, 6, "companyAdmin.email", "admin_email_exists",
                    $"{email} already has a login in this workspace. Choose \"Make an existing user the company super admin\" instead.",
                    "لهذا البريد حساب في مساحة العمل. اختر مستخدماً حالياً بدلاً من ذلك.");
                return V.ActionConflict;
            }
            c.Item(Areas.CompanyAdmin, email, admin.FullNameEn?.Trim() ?? email, V.ActionCreate, "Invited as this company's super admin (Admin, this company only).", admin.FullNameAr);
            return V.ActionCreate;
        }

        // existing
        if (admin.ExistingUserId is not Guid userId)
        {
            c.Problem(6, "companyAdmin.existingUserId", "required", "Choose the user who will be the company super admin.");
            return V.ActionConflict;
        }
        var user = await ScopedBypass.TenantWide(_db.Users, caller.TenantId, Why).AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(r => r.Role)
            .SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.IsDeleted || !user.IsActive || user.Status != "Active")
        {
            c.Problem(6, "companyAdmin.existingUserId", "user_not_active", "Choose an active user of this workspace.");
            return V.ActionConflict;
        }
        var holdsAdmin = user.UserRoles.Any(r => r.Role is { NormalizedName: "ADMIN", IsActive: true, IsDeleted: false });
        if (user.IsGroupScope)
        {
            if (holdsAdmin)
            {
                c.Item(Areas.CompanyAdmin, user.Id.ToString(), user.FullName, V.ActionReuse, "Already a group super admin, so they manage this company already.");
                return V.ActionReuse;
            }
            c.Conflict(Areas.CompanyAdmin, user.Id.ToString(), user.FullName, 6, "companyAdmin.existingUserId", "existing_user_group_scope_not_admin",
                $"{user.FullName} has group-wide access without the Admin role. Making them Admin would make them a GROUP super admin, not this company's. Invite a company super admin instead.");
            return V.ActionConflict;
        }
        if (!holdsAdmin)
        {
            c.Conflict(Areas.CompanyAdmin, user.Id.ToString(), user.FullName, 6, "companyAdmin.existingUserId", "existing_user_not_admin",
                $"{user.FullName} does not hold the Admin role. Roles apply to every company a user can access, so granting Admin would also make them Admin of their other companies. Invite a new company super admin, or choose a user who is already a company admin.",
                "الأدوار تنطبق على جميع الشركات التي يصل إليها المستخدم. ادعُ مسؤولاً جديداً للشركة بدلاً من ذلك.");
            return V.ActionConflict;
        }
        if (user.Id == caller.UserId)
        {
            c.Problem(6, "companyAdmin.existingUserId", "self_grant_refused", "You cannot grant company access to yourself.");
            return V.ActionConflict;
        }
        try { await _access.AssertMayChangeUserAccessAsync(caller.TenantId, user.Id, caller.Context with { TenantId = caller.TenantId }, ct); }
        catch (PrivilegeCeilingException ex)
        {
            c.Conflict(Areas.CompanyAdmin, user.Id.ToString(), user.FullName, 6, "companyAdmin.existingUserId", ex.Refusal.Code, ex.Refusal.MessageEn, ex.Refusal.MessageAr);
            return V.ActionConflict;
        }
        c.Item(Areas.CompanyAdmin, user.Id.ToString(), user.FullName, V.ActionCreate,
            "Gets access to this company with their Admin role; their other access is unchanged.");
        return V.ActionCreate;
    }

    /// <summary>Whether <paramref name="caller"/> may make someone a company super admin: group scope, the Admin role,
    /// the Admin role assignable under the privilege ceiling, and security.manage (User Management's own gate).</summary>
    public static bool MayGrantCompanyAdmin(CompanySetupCaller caller, AccessCeilingDto? ceiling) =>
        caller.IsGroupScope && ceiling is { IsAdmin: true }
        && ceiling.Roles.Any(r => string.Equals(r.Name, "Admin", StringComparison.OrdinalIgnoreCase) && r.CanAssign)
        && ceiling.HeldPermissions.Contains("security.manage", StringComparer.Ordinal);

    private static CompanySetupPlan Finish(Collector c) =>
        new(c.Problems.Count == 0 && c.Items.All(i => i.Action != V.ActionConflict), c.Items, c.Problems, c.Warnings);

    internal static bool TryParseMonth(string? value, out DateOnly firstDay)
    {
        firstDay = default;
        if (!DateOnly.TryParseExact((value ?? string.Empty).Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return false;
        if (d.Year is < 2000 or > 2100) return false;
        firstDay = d;
        return true;
    }

    private static bool IsEmail(string value)
    {
        if (value.Length is < 3 or > 254 || value.Count(ch => ch == '@') != 1) return false;
        try { return new MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }
}

/// <summary>Codes and messages the controller and the planner share, so a refusal reads the same at every door.</summary>
public static class CompanySetupGates
{
    public const string NameTakenCode = "company_name_taken";
    public const string CrTakenCode = "company_registration_taken";
    // Refusal codes the UI maps (Integration Owner clarification 1). The gate's own three codes pass through as-is:
    // company_limit_reached, account_type_single_company, company_creation_platform_controlled.
    public const string GroupScopeRequiredCode = "not_group_scope";
    public const string PlatformActivationDisabledCode = "company_creation_platform_approval";
    public const string GroupScopeRequiredMessage = "Only a group administrator can create another company.";
    public const string GroupScopeRequiredMessageAr = "يمكن لمسؤول المجموعة فقط إنشاء شركة جديدة.";
    public const string PlatformActivationDisabledMessage =
        "New companies in this workspace wait for platform activation, and platform activation is currently disabled. Contact your account manager to create a company.";
    public const string PlatformActivationDisabledMessageAr =
        "الشركات الجديدة تنتظر تفعيل المنصة، والتفعيل معطل حالياً. تواصل مع مدير حسابك.";
}
