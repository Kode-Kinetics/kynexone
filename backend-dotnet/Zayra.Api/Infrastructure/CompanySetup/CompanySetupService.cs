using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;
using V = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary;
using Areas = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary.Areas;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>An HTTP-shaped answer: a status and either a value or an error body. The controller only maps it.</summary>
public sealed record CompanySetupOutcome<T>(int Status, T? Value, object? Error = null)
{
    public static CompanySetupOutcome<T> Ok(T value, int status = 200) => new(status, value);
    /// <summary>Every refusal has the contract's error shape: <c>{ code, message, messageAr?, field?, step? }</c>.</summary>
    public static CompanySetupOutcome<T> Fail(int status, string code, string message, string? messageAr = null,
        string? field = null, int? step = null, CompanySetupPlan? plan = null) =>
        new(status, default, new CompanySetupError(code, message, messageAr, field, step) { Plan = plan });
}

/// <summary>
/// The create-company wizard (design contract docs/design/company-setup-wizard.md): context, deterministic
/// defaults, server-side drafts, the atomic + idempotent commit and the readiness checklist.
///
/// <para><b>Model-free by construction (Decision 1).</b> Nothing here depends on <c>ILlmClient</c>,
/// <c>ISetupAssistantService</c> or anything in <c>Infrastructure.AI</c>; the defaults come from the static,
/// deterministic template and the country pack. <c>CompanySetupNoModelCallTests</c> asserts it.</para>
/// </summary>
public sealed class CompanySetupService
{
    internal static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);
    private const string Why = "Create-company wizard: the tenant's company register, shared catalog and drafts are read across every legal entity of the tenant whatever company the caller's switcher selects.";
    public const string SetupCreatedAction = "company.setup_created";
    /// <summary>Addendum A6: the commit marker of an existing-company completion (counts by action).</summary>
    public const string SetupCompletedAction = "company.setup_completed";
    /// <summary>The audit row written when the company super admin could not be written after the commit.</summary>
    public const string AdminFailedAction = "company.setup_admin_failed";

    private readonly Zayra.Api.Infrastructure.Licensing.ILicenceService? _licence;
    private readonly ZayraDbContext _db;
    private readonly IOrganizationSetupService _organization;
    private readonly IAccessManagementService _access;
    private readonly IEmailService _email;
    private readonly IWorkWeekService _workWeek;

    public CompanySetupService(ZayraDbContext db, IOrganizationSetupService organization, IAccessManagementService access,
        IWorkWeekService workWeek, IEmailService email, Zayra.Api.Infrastructure.Licensing.ILicenceService? licence = null)
    {
        _licence = licence;
        _db = db;
        _organization = organization;
        _access = access;
        _workWeek = workWeek;
        _email = email;
        Planner = new CompanySetupPlanner(db, access, workWeek, licence);
    }

    public CompanySetupPlanner Planner { get; }

    // ── GET /context ─────────────────────────────────────────────────────────────────────────────────

    public async Task<CompanySetupContext> GetContextAsync(CompanySetupCaller caller, CancellationToken ct)
    {
        var tenantId = caller.TenantId;
        var tenant = await _db.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
            .Select(t => new { t.AccountType }).FirstOrDefaultAsync(ct);
        var maxCompanies = Zayra.Api.Infrastructure.Licensing.LicenceLimits.MaxCompanies(_licence);
        var used = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).CountAsync(c => !c.IsDeleted, ct);
        var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct, licence: _licence);
        var (canCreate, code, message, _) = Refusal(caller, gate);
        // The same role ceiling User Management applies (and the planner's step 6 uses).
        AccessCeilingDto? ceiling;
        try { ceiling = await _access.GetAccessCeilingAsync(tenantId, caller.Context with { TenantId = tenantId }, ct); }
        catch (PrivilegeCeilingException) { ceiling = null; }
        var manageable = await ManageableCompaniesAsync(caller, ceiling, ct);
        // A group admin: for a new company. A company super admin: for their own company (existing mode, Addendum A5).
        var canGrantCompanyAdmin = CompanySetupPlanner.MayGrantCompanyAdmin(caller, ceiling,
            existingCompany: !caller.IsGroupScope && manageable.Count > 0);

        var sisters = caller.IsGroupScope
            ? await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
                .Where(c => !c.IsDeleted && c.IsActive && c.ApprovalStatus == CompanyApprovalStatuses.Active)
                .OrderBy(c => c.LegalNameEn)
                .Select(c => new CompanySetupSisterCompany(c.Id, c.LegalNameEn, c.CountryCode)).ToListAsync(ct)
            : [];
        var departments = await ScopedBypass.TenantWide(_db.Departments, tenantId, Why).AsNoTracking()
            .Where(d => !d.IsDeleted && d.IsActive).OrderBy(d => d.Code)
            .Select(d => new CodeName(d.Code, d.NameEn, d.NameAr == "" ? null : d.NameAr)).ToListAsync(ct);
        var designations = await ScopedBypass.TenantWide(_db.Designations, tenantId, Why).AsNoTracking()
            .Where(d => !d.IsDeleted && d.IsActive).OrderBy(d => d.Code)
            .Select(d => new CodeName(d.Code, d.TitleEn, d.TitleAr == "" ? null : d.TitleAr)).ToListAsync(ct);
        var grades = await ScopedBypass.TenantWide(_db.Grades, tenantId, Why).AsNoTracking()
            .Where(g => !g.IsDeleted && g.IsActive).OrderBy(g => g.Level).ThenBy(g => g.Code)
            .Select(g => new GradeRef(g.Code, g.Name, g.NameAr, g.Currency)).ToListAsync(ct);
        var gradesCurrency = grades.GroupBy(g => g.Currency).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        return new CompanySetupContext(
            canCreate, code, message,
            Zayra.Api.Infrastructure.Licensing.LicenceLimits.AccountType(_licence, tenant?.AccountType ?? string.Empty),
            used,
            maxCompanies > 0 ? maxCompanies : null,
            caller.IsGroupScope,
            sisters,
            new CompanySetupSharedCatalog(departments, designations, grades),
            gradesCurrency,
            canGrantCompanyAdmin) { ManageableCompanies = manageable };
    }

    /// <summary>Addendum A5: the chooser's cards — the active companies this caller may complete, each with its
    /// readiness counted from the same checklist <c>GET /readiness</c> returns, and its open draft, if any.</summary>
    private async Task<IReadOnlyList<CompanySetupManageableCompany>> ManageableCompaniesAsync(CompanySetupCaller caller, AccessCeilingDto? ceiling, CancellationToken ct)
    {
        if (!caller.IsGroupScope && ceiling is not { IsAdmin: true }) return [];
        if (!caller.IsGroupScope && caller.CompanyIds is not { Count: > 0 }) return [];
        var ids = caller.CompanyIds?.ToList();
        var companies = await ScopedBypass.TenantWide(_db.Companies, caller.TenantId, Why).AsNoTracking()
            .Where(c => !c.IsDeleted && c.IsActive && c.ApprovalStatus == CompanyApprovalStatuses.Active && (ids == null || ids.Contains(c.Id)))
            .OrderBy(c => c.LegalNameEn).ToListAsync(ct);
        var openDrafts = await OpenExistingDraftsAsync(caller.TenantId, ct);
        var cards = new List<CompanySetupManageableCompany>(companies.Count);
        foreach (var company in companies)
        {
            var readiness = await CompanySetupReadiness.ComputeAsync(_db, _email, caller.TenantId, company, ct);
            cards.Add(new CompanySetupManageableCompany(company.Id, company.LegalNameEn, string.IsNullOrWhiteSpace(company.LegalNameAr) ? null : company.LegalNameAr,
                company.CountryCode, readiness.Count(r => r.Status == V.ReadinessDone), readiness.Count, openDrafts.GetValueOrDefault(company.Id))
            {
                NeedsLegalDetails = !CompanySetupReadiness.HasLegalDetails(company),
            });
        }
        return cards;
    }

    /// <summary>Open existing-company drafts by their target company (the newest, should two predate the rule).
    /// The target lives in the body (a shape nobody queries), so the tenant's open drafts — a handful — are read.</summary>
    private async Task<Dictionary<Guid, Guid?>> OpenExistingDraftsAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await _db.CompanySetupDrafts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Status == CompanySetupDraftStatuses.Open)
            .OrderByDescending(d => d.UpdatedAtUtc).ToListAsync(ct);
        var map = new Dictionary<Guid, Guid?>();
        foreach (var row in rows)
            if (ReadBody(row)?.ExistingCompanyId is Guid target) map.TryAdd(target, row.Id);
        return map;
    }

    /// <summary>Whether this caller may see a draft: group scope sees every draft of the tenant; a company super admin
    /// only the existing-company drafts of their own company (a new-company draft can hold a proposed admin's email).</summary>
    private async Task<bool> MaySeeDraftAsync(CompanySetupCaller caller, CompanySetupDraftRecord row, CancellationToken ct) =>
        caller.IsGroupScope
        || (ReadBody(row)?.ExistingCompanyId is Guid target && await Planner.ScopeRefusalAsync(caller, target, null, ct) is null);

    /// <summary>Decision 12 as one answer: may this caller start the wizard now, and if not, why (at step 1).</summary>
    private static (bool CanCreate, string? Code, string? Message, string? MessageAr) Refusal(CompanySetupCaller caller, CompanyCreationGateDecision gate)
    {
        if (!caller.IsGroupScope) return (false, CompanySetupGates.GroupScopeRequiredCode, CompanySetupGates.GroupScopeRequiredMessage, CompanySetupGates.GroupScopeRequiredMessageAr);
        if (!gate.Allowed) return (false, gate.ErrorCode, gate.Message, null);
        return (true, null, null, null);
    }

    // ── GET /defaults ────────────────────────────────────────────────────────────────────────────────

    public async Task<CompanySetupOutcome<CompanySetupDraftBody>> GetDefaultsAsync(
        CompanySetupCaller caller, string? countryCode, Guid? copyFrom, CancellationToken ct)
    {
        var tenantId = caller.TenantId;
        Company? sister = null;
        if (copyFrom is Guid sisterId)
        {
            // Decision 8: an ACTIVE sister company the caller can access. Copied by value, never by reference.
            sister = caller.IsGroupScope
                ? await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == sisterId && !c.IsDeleted && c.IsActive && c.ApprovalStatus == CompanyApprovalStatuses.Active, ct)
                : null;
            if (sister is null)
                return CompanySetupOutcome<CompanySetupDraftBody>.Fail(404, "copy_source_not_found", "Choose an active company you can access to copy from.");
        }
        var country = CountryCodeStandard.NormalizeToIso2(string.IsNullOrWhiteSpace(countryCode) ? sister?.CountryCode : countryCode);
        if (country is null)
            return CompanySetupOutcome<CompanySetupDraftBody>.Fail(400, "country_invalid", "Choose the company's country (ISO code, for example SA).",
                "اختر دولة الشركة.", "company.countryCode", 2);
        var currency = HomeJurisdiction.CurrencyFor(country);

        var gradeCurrency = await ScopedBypass.TenantWide(_db.Grades, tenantId, Why).AsNoTracking()
            .Where(g => !g.IsDeleted && g.IsActive).GroupBy(g => g.Currency).OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstOrDefaultAsync(ct);
        var template = SetupAssistantService.DeterministicCompanyDefaults(country, gradeCurrency ?? currency, null);
        var resolved = await _workWeek.ResolveAsync(tenantId, null, country, ct);
        var weekend = CompanySetupPack.FormatWeekend(resolved.WeekendDays);

        var usedCostCentres = (await ScopedBypass.TenantWide(_db.CostCenters, tenantId, Why).AsNoTracking()
            .Select(x => x.Code).ToListAsync(ct)).Select(OrgCodes.Normalize).ToHashSet(StringComparer.Ordinal);
        string UniqueCostCentre(string code)
        {
            var baseCode = OrgCodes.Normalize(code);
            var candidate = baseCode;
            for (var n = 2; usedCostCentres.Contains(candidate); n++) candidate = $"{baseCode}-{n}";
            usedCostCentres.Add(candidate);
            return candidate;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var firstPayrollMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var company = new CompanySetupCompany(
            LegalNameEn: string.Empty, LegalNameAr: null, TradeName: null, CountryCode: country, Currency: currency,
            RegistrationNumber: string.Empty, MolEstablishmentNumber: null, GosiEmployerId: null, UnifiedNationalNumber: null,
            TaxNumber: null, QiwaEstablishmentId: null, EmailDomain: null, WorkEmailPattern: WorkEmailPatterns.FirstLast,
            ErpCompanyCode: null, FirstPayrollMonth: firstPayrollMonth);

        List<CompanySetupBranch> branches;
        List<CompanySetupCostCentre> costCentres;
        var idRule = template.EmployeeIdRule!;
        CompanySetupEmployeeNumbering numbering = new(idRule.CompanyPrefix, idRule.UseCountryPrefix, idRule.UseBranchPrefix,
            idRule.UseDepartmentPrefix, idRule.UseYear, idRule.PaddingLength, 1);
        var holidayUsePack = template.HolidayCalendar is not null;
        var workWeek = new CompanySetupWorkWeek(weekend, Override: false);
        List<CompanySetupDepartment> departments;

        if (sister is null)
        {
            branches = template.Branches.Select(b => new CompanySetupBranch(OrgCodes.Normalize(b.Code), b.NameEn, null, b.City,
                HomeJurisdiction.TimeZoneFor(country), b.IsHeadOffice, [])).ToList();
            var ccByDept = template.CostCenters.ToDictionary(cc => OrgCodes.Normalize(cc.DepartmentCode), cc => UniqueCostCentre(cc.Code), StringComparer.Ordinal);
            costCentres = template.CostCenters.Select(cc => new CompanySetupCostCentre(ccByDept[OrgCodes.Normalize(cc.DepartmentCode)], cc.Name, null)).ToList();
            // A template department the group already has is reused as it is (Decision 7), so it carries no links here.
            var existingDepartments = (await ScopedBypass.TenantWide(_db.Departments, tenantId, Why).AsNoTracking()
                .Select(x => x.Code).ToListAsync(ct)).Select(OrgCodes.Normalize).ToHashSet(StringComparer.Ordinal);
            var headOfficeCode = OrgCodes.Normalize(template.Branches.FirstOrDefault(b => b.IsHeadOffice)?.Code);
            departments = template.Departments.Select(d => OrgCodes.Normalize(d.Code)).Select(code => existingDepartments.Contains(code)
                ? new CompanySetupDepartment(code, template.Departments.First(x => OrgCodes.Normalize(x.Code) == code).NameEn, null, null, null, null)
                : new CompanySetupDepartment(code, template.Departments.First(x => OrgCodes.Normalize(x.Code) == code).NameEn, null, null,
                    headOfficeCode, ccByDept.GetValueOrDefault(code))).ToList();
        }
        else
        {
            // Decision 8: branches (renamed codes), cost centres (renamed codes), the ID-rule shape, the holiday
            // calendar and the leave-policy selection, copied into the draft for editing.
            var sisterBranches = await _db.Branches.AsNoTracking()
                .Where(b => b.TenantId == tenantId && b.CompanyId == sister.Id && !b.IsDeleted && b.IsActive)
                .OrderByDescending(b => b.IsHeadOffice).ThenBy(b => b.Code).ToListAsync(ct);
            var branchCodes = new HashSet<string>(StringComparer.Ordinal);
            string RenameBranch(string code)
            {
                var baseCode = OrgCodes.Normalize(code);
                var candidate = $"{baseCode}-2";
                for (var n = 3; !branchCodes.Add(candidate); n++) candidate = $"{baseCode}-{n}";
                return candidate;
            }
            branches = sisterBranches.Select(b => new CompanySetupBranch(RenameBranch(b.Code), b.NameEn, b.NameAr == "" ? null : b.NameAr, b.City,
                string.IsNullOrWhiteSpace(b.TimeZoneId) ? HomeJurisdiction.TimeZoneFor(country) : b.TimeZoneId, b.IsHeadOffice, [])).ToList();
            if (branches.Count > 0 && branches.Count(b => b.IsHeadOffice) != 1)
                branches = branches.Select((b, i) => b with { IsHeadOffice = i == 0 }).ToList();
            costCentres = (await ScopedBypass.TenantWide(_db.CostCenters, tenantId, Why).AsNoTracking()
                    .Where(cc => cc.CompanyId == sister.Id && !cc.IsDeleted && cc.IsActive).OrderBy(cc => cc.Code).ToListAsync(ct))
                .Select(cc => new CompanySetupCostCentre(UniqueCostCentre(cc.Code), cc.Name, null)).ToList();
            departments = [];
            var sisterRule = await ScopedBypass.TenantWide(_db.EmployeeIdRules, tenantId, Why).AsNoTracking()
                .FirstOrDefaultAsync(r => r.CompanyId == sister.Id && !r.IsDeleted, ct);
            if (sisterRule is not null)
                numbering = new(sisterRule.CompanyPrefix, sisterRule.UseCountryPrefix, sisterRule.UseBranchPrefix, sisterRule.UseDepartmentPrefix,
                    sisterRule.UseYear, sisterRule.PaddingLength, 1);
            holidayUsePack = await ScopedBypass.TenantWide(_db.PublicHolidayCalendars, tenantId, Why).AsNoTracking()
                .AnyAsync(c => c.CompanyId == sister.Id && c.CalendarYear == today.Year, ct) && template.HolidayCalendar is not null;
            var sisterWeek = await _workWeek.ResolveAsync(tenantId, sister.Id, sister.CountryCode, ct);
            var sisterOverrides = sisterWeek.Source == "gcc:company";
            workWeek = new CompanySetupWorkWeek(CompanySetupPack.FormatWeekend(sisterWeek.WeekendDays),
                Override: sisterOverrides && !sisterWeek.WeekendDays.SetEquals(resolved.WeekendDays));
        }

        var grades = template.Grades.Select(g => new CompanySetupGrade(OrgCodes.Normalize(g.Code), g.Name, null, g.Level, g.MinSalary, g.MidSalary, g.MaxSalary)).ToList();
        var designations = template.Designations.Select(d => new CompanySetupDesignation(OrgCodes.Normalize(d.Code), d.TitleEn, null,
            OrgCodes.Normalize(d.DepartmentCode), OrgCodes.Normalize(d.GradeCode))).ToList();
        if (sister is not null)
        {
            // The shared catalog is shared: a copy reuses what the group already has rather than proposing the template.
            departments = [];
            designations = [];
            grades = [];
        }

        var body = new CompanySetupDraftBody(
            StartFrom: sister is null ? V.StartFromDefaults : V.StartFromCopy,
            CopyFromCompanyId: sister?.Id,
            Company: company,
            Branches: branches,
            CostCentres: costCentres,
            Departments: departments,
            Designations: designations,
            Grades: grades,
            Rules: new CompanySetupRules(workWeek, new CompanySetupPackChoice(holidayUsePack), new CompanySetupPackChoice(true), numbering),
            CompanyAdmin: new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null));
        return CompanySetupOutcome<CompanySetupDraftBody>.Ok(body);
    }

    /// <summary>
    /// Addendum A2: <c>GET /defaults?existingCompanyId=</c> — a body built from what the company HAS today, every existing
    /// row carrying its <c>existingId</c>, so a round trip with no edits plans nothing but <c>unchanged</c>/<c>reuse</c>.
    /// What the company lacks is filled from the country pack exactly as on create, and carries no id (it is new). For a
    /// company super admin, pack rows that would ADD to the shared group catalog are left out (only group scope adds them).
    /// </summary>
    public async Task<CompanySetupOutcome<CompanySetupDraftBody>> GetExistingDefaultsAsync(CompanySetupCaller caller, Guid companyId, CancellationToken ct)
    {
        if (await Planner.AuthorizeExistingAsync(caller, companyId, null, ct) is { } refused)
            return CompanySetupOutcome<CompanySetupDraftBody>.Fail(refused.Status, refused.Code, refused.Message, refused.MessageAr, "existingCompanyId", 1);
        var tenantId = caller.TenantId;
        var company = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking().SingleAsync(c => c.Id == companyId, ct);
        var country = CountryCodeStandard.NormalizeToIso2(company.CountryCode) ?? company.CountryCode.Trim().ToUpperInvariant();
        var currency = company.DefaultCurrency.Trim().ToUpperInvariant();
        static string? B(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        var gradeCurrency = await ScopedBypass.TenantWide(_db.Grades, tenantId, Why).AsNoTracking()
            .Where(g => !g.IsDeleted && g.IsActive).GroupBy(g => g.Currency).OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstOrDefaultAsync(ct);
        var template = SetupAssistantService.DeterministicCompanyDefaults(country, gradeCurrency ?? currency, null);
        var packTz = HomeJurisdiction.TimeZoneFor(country);

        // Branches and their locations (active rows; removed or inactive ones are managed in Setup).
        var branchRows = await ScopedBypass.TenantWide(_db.Branches, tenantId, Why).AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive)
            .OrderByDescending(b => b.IsHeadOffice).ThenBy(b => b.Code).ToListAsync(ct);
        var branchIdList = branchRows.Select(b => (Guid?)b.Id).ToList();
        var locationRows = await ScopedBypass.TenantWide(_db.Locations, tenantId, Why).AsNoTracking()
            .Where(l => !l.IsDeleted && l.IsActive && branchIdList.Contains(l.BranchId)).OrderBy(l => l.Code).ToListAsync(ct);
        List<CompanySetupBranch> branches = branchRows.Select(b => new CompanySetupBranch(OrgCodes.Normalize(b.Code), b.NameEn, B(b.NameAr), b.City,
            string.IsNullOrWhiteSpace(b.TimeZoneId) ? packTz : b.TimeZoneId.Trim(), b.IsHeadOffice,
            locationRows.Where(l => l.BranchId == b.Id).Select(l => new CompanySetupLocation(OrgCodes.Normalize(l.Code), l.NameEn, B(l.NameAr),
                l.Latitude, l.Longitude, l.GeofenceRadiusMeters, l.Id, CompanySetupBaseline.Location(l))).ToList(), b.Id,
            CompanySetupBaseline.Branch(b, packTz))).ToList();
        if (branches.Count == 0)
            branches = template.Branches.Select(b => new CompanySetupBranch(OrgCodes.Normalize(b.Code), b.NameEn, null, b.City, packTz, b.IsHeadOffice, [])).ToList();

        // Cost centres: the company's own; the pack's (renamed past any code in use) when it has none.
        var ccRows = await ScopedBypass.TenantWide(_db.CostCenters, tenantId, Why).AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.IsActive).OrderBy(x => x.Code).ToListAsync(ct);
        var costCentres = ccRows.Select(x => new CompanySetupCostCentre(OrgCodes.Normalize(x.Code), x.Name, null, x.Id, CompanySetupBaseline.CostCentre(x))).ToList();
        var ccByTemplateDept = new Dictionary<string, string>(StringComparer.Ordinal);
        if (costCentres.Count == 0)
        {
            var used = (await ScopedBypass.TenantWide(_db.CostCenters, tenantId, Why).AsNoTracking().Select(x => x.Code).ToListAsync(ct))
                .Select(OrgCodes.Normalize).ToHashSet(StringComparer.Ordinal);
            foreach (var cc in template.CostCenters)
            {
                var baseCode = OrgCodes.Normalize(cc.Code);
                var candidate = baseCode;
                for (var n = 2; used.Contains(candidate); n++) candidate = $"{baseCode}-{n}";
                used.Add(candidate);
                ccByTemplateDept[OrgCodes.Normalize(cc.DepartmentCode)] = candidate;
                costCentres.Add(new CompanySetupCostCentre(candidate, cc.Name, null));
            }
        }

        // The shared-catalog rows the company uses: referenced by its branches, its cost centres or its employees.
        var companyBranchIds = branchRows.Select(b => (Guid?)b.Id).ToList();
        var companyCcIds = ccRows.Select(x => (Guid?)x.Id).ToList();
        // The ordinary filtered read: the company is within this request's scope (AuthorizeExistingAsync above).
        var used3 = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.CompanyId == companyId && !e.IsDeleted)
            .Select(e => new { e.DepartmentId, e.DesignationId, e.GradeId }).Distinct().ToListAsync(ct);
        var employeeDepartmentIds = used3.Where(x => x.DepartmentId != null).Select(x => x.DepartmentId).Distinct().ToList();
        var allDepartments = await ScopedBypass.TenantWide(_db.Departments, tenantId, Why).AsNoTracking().Where(d => !d.IsDeleted).ToListAsync(ct);
        var deptCode = allDepartments.ToDictionary(d => d.Id, d => OrgCodes.Normalize(d.Code));
        var branchCodeOf = branchRows.ToDictionary(b => b.Id, b => OrgCodes.Normalize(b.Code));
        var ccCodeOf = ccRows.ToDictionary(x => x.Id, x => OrgCodes.Normalize(x.Code));
        var usedDepartments = allDepartments
            .Where(d => d.IsActive && (companyBranchIds.Contains(d.BranchId) || companyCcIds.Contains(d.CostCenterId) || employeeDepartmentIds.Contains(d.Id)))
            .OrderBy(d => d.Code).ToList();
        var departments = usedDepartments.Select(d => new CompanySetupDepartment(OrgCodes.Normalize(d.Code), d.NameEn, B(d.NameAr),
            d.ParentDepartmentId is Guid p && deptCode.TryGetValue(p, out var pc) ? pc : null,
            d.BranchId is Guid bid && branchCodeOf.TryGetValue(bid, out var bc) ? bc : null,
            d.CostCenterId is Guid cid && ccCodeOf.TryGetValue(cid, out var cc2) ? cc2 : null, d.Id)).ToList();

        // Designations: those its employees hold, and those of the departments it uses.
        var designationIds = used3.Where(x => x.DesignationId != null).Select(x => x.DesignationId!.Value).Distinct().ToList();
        var usedDepartmentIds = usedDepartments.Select(d => (Guid?)d.Id).ToList();
        var designationRows = await ScopedBypass.TenantWide(_db.Designations, tenantId, Why).AsNoTracking()
            .Where(d => !d.IsDeleted && d.IsActive && (designationIds.Contains(d.Id) || usedDepartmentIds.Contains(d.DepartmentId)))
            .OrderBy(d => d.Code).ToListAsync(ct);
        var gradeIds = used3.Where(x => x.GradeId != null).Select(x => x.GradeId!.Value)
            .Concat(designationRows.Where(d => d.GradeId != null).Select(d => d.GradeId!.Value)).Distinct().ToList();
        var allGrades = await ScopedBypass.TenantWide(_db.Grades, tenantId, Why).AsNoTracking().Where(g => !g.IsDeleted).ToListAsync(ct);
        var gradeCodeOf = allGrades.ToDictionary(g => g.Id, g => OrgCodes.Normalize(g.Code));
        var designations = designationRows.Select(d => new CompanySetupDesignation(OrgCodes.Normalize(d.Code), d.TitleEn, B(d.TitleAr),
            d.DepartmentId is Guid dd && deptCode.TryGetValue(dd, out var dc) ? dc : null,
            d.GradeId is Guid gg && gradeCodeOf.TryGetValue(gg, out var gc) ? gc : null, d.Id)).ToList();
        var grades = allGrades.Where(g => g.IsActive && gradeIds.Contains(g.Id)).OrderBy(g => g.Level).ThenBy(g => g.Code)
            .Select(g => new CompanySetupGrade(OrgCodes.Normalize(g.Code), g.Name, B(g.NameAr), g.Level, g.MinSalary, g.MidSalary, g.MaxSalary, g.Id)).ToList();

        // Nothing in use yet: the pack's rows, existing codes reused (with their id), new codes for group scope only.
        var deptByCode = allDepartments.GroupBy(d => OrgCodes.Normalize(d.Code)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var gradeByCode = allGrades.GroupBy(g => OrgCodes.Normalize(g.Code)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var allDesignations = await ScopedBypass.TenantWide(_db.Designations, tenantId, Why).AsNoTracking().Where(d => !d.IsDeleted)
            .Select(d => new { d.Id, d.Code }).ToListAsync(ct);
        var designationByCode = allDesignations.GroupBy(d => OrgCodes.Normalize(d.Code)).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var headOfficeCode = branches.FirstOrDefault(b => b.IsHeadOffice)?.Code;
        if (departments.Count == 0)
            departments = template.Departments.Select(d => (d, code: OrgCodes.Normalize(d.Code)))
                .Where(x => deptByCode.ContainsKey(x.code) || caller.IsGroupScope)
                .Select(x => deptByCode.TryGetValue(x.code, out var row)
                    ? new CompanySetupDepartment(x.code, row.NameEn, B(row.NameAr), null, null, null, row.Id)
                    : new CompanySetupDepartment(x.code, x.d.NameEn, null, null, headOfficeCode, ccByTemplateDept.GetValueOrDefault(x.code))).ToList();
        if (grades.Count == 0)
            grades = template.Grades.Select(g => (g, code: OrgCodes.Normalize(g.Code)))
                .Where(x => gradeByCode.ContainsKey(x.code) || caller.IsGroupScope)
                .Select(x => gradeByCode.TryGetValue(x.code, out var row)
                    ? new CompanySetupGrade(x.code, row.Name, B(row.NameAr), row.Level, row.MinSalary, row.MidSalary, row.MaxSalary, row.Id)
                    : new CompanySetupGrade(x.code, x.g.Name, null, x.g.Level, x.g.MinSalary, x.g.MidSalary, x.g.MaxSalary)).ToList();
        // A pack designation is offered only when its department and grade will exist (in the body or the catalog).
        var knownDepartments = departments.Select(d => d.Code).Concat(deptByCode.Keys).ToHashSet(StringComparer.Ordinal);
        var knownGrades = grades.Select(g => g.Code).Concat(gradeByCode.Keys).ToHashSet(StringComparer.Ordinal);
        if (designations.Count == 0)
            designations = template.Designations.Select(d => (d, code: OrgCodes.Normalize(d.Code)))
                .Where(x => designationByCode.ContainsKey(x.code)
                    || (caller.IsGroupScope && knownDepartments.Contains(OrgCodes.Normalize(x.d.DepartmentCode)) && knownGrades.Contains(OrgCodes.Normalize(x.d.GradeCode))))
                .Select(x => new CompanySetupDesignation(x.code, x.d.TitleEn, null, OrgCodes.Normalize(x.d.DepartmentCode), OrgCodes.Normalize(x.d.GradeCode),
                    designationByCode.TryGetValue(x.code, out var id) ? id : null)).ToList();

        // Work and pay rules: what the company has, else the pack.
        var rule = await ScopedBypass.TenantWide(_db.EmployeeIdRules, tenantId, Why).AsNoTracking()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && !r.IsDeleted, ct);
        var idRule = template.EmployeeIdRule!;
        var numbering = rule is null
            ? new CompanySetupEmployeeNumbering(idRule.CompanyPrefix, idRule.UseCountryPrefix, idRule.UseBranchPrefix, idRule.UseDepartmentPrefix, idRule.UseYear, idRule.PaddingLength, 1)
            : new CompanySetupEmployeeNumbering(rule.CompanyPrefix, rule.UseCountryPrefix, rule.UseBranchPrefix, rule.UseDepartmentPrefix, rule.UseYear, rule.PaddingLength, rule.NextSequence);
        // A9: every block of existing mode carries what it was read from (the numbering's fields are null when it has no rule yet).
        numbering = numbering with { Baseline = CompanySetupBaseline.Numbering(rule) };
        var cutover = await ScopedBypass.TenantWide(_db.CompanyCutovers, tenantId, Why).AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var firstPayrollMonth = (cutover?.CutoverDate ?? new DateOnly(today.Year, today.Month, 1).AddMonths(1)).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var (weekendNow, hasOwnWeek) = await Planner.CurrentWorkWeekAsync(tenantId, companyId, country, ct);
        var hasCalendar = template.HolidayCalendar is { } cal && await ScopedBypass.TenantWide(_db.PublicHolidayCalendars, tenantId, Why).AsNoTracking()
            .AnyAsync(x => x.CompanyId == companyId && x.CountryCode == country && x.CalendarYear == cal.CalendarYear, ct);

        var body = new CompanySetupDraftBody(
            StartFrom: V.StartFromDefaults,
            CopyFromCompanyId: null,
            Company: new CompanySetupCompany(company.LegalNameEn, B(company.LegalNameAr), B(company.TradeName), country, currency,
                company.RegistrationNumber, B(company.WpsEmployerId), B(company.GosiEmployerId), B(company.UnifiedNationalNumber), B(company.TaxNumber),
                B(company.QiwaEstablishmentId), B(company.EmailDomain), company.WorkEmailPattern, B(company.ErpCompanyCode), firstPayrollMonth,
                CutoverStatus: cutover?.Status, Baseline: CompanySetupBaseline.Company(company, cutover)),
            Branches: branches,
            CostCentres: costCentres,
            Departments: departments,
            Designations: designations,
            Grades: grades,
            Rules: new CompanySetupRules(new CompanySetupWorkWeek(weekendNow, Override: hasOwnWeek, CompanySetupBaseline.WorkWeek(weekendNow, hasOwnWeek)),
                new CompanySetupPackChoice(hasCalendar || template.HolidayCalendar is not null), new CompanySetupPackChoice(true), numbering),
            CompanyAdmin: new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null),
            ExistingCompanyId: companyId);
        return CompanySetupOutcome<CompanySetupDraftBody>.Ok(body);
    }

    // ── Drafts ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CompanySetupDraftSummary>> ListDraftsAsync(CompanySetupCaller caller, CancellationToken ct)
    {
        var rows = await _db.CompanySetupDrafts.AsNoTracking()
            .Where(d => d.TenantId == caller.TenantId && d.Status == CompanySetupDraftStatuses.Open)
            .OrderByDescending(d => d.UpdatedAtUtc).Take(50).ToListAsync(ct);
        if (!caller.IsGroupScope)
        {
            var visible = new List<CompanySetupDraftRecord>();
            foreach (var r in rows) if (await MaySeeDraftAsync(caller, r, ct)) visible.Add(r);
            rows = visible;
        }
        var names = await NamesAsync(caller.TenantId, rows.Select(r => r.CreatedByUserId), ct);
        return rows.Select(r =>
        {
            var body = ReadBody(r);
            return new CompanySetupDraftSummary(r.Id, body?.Company?.LegalNameEn ?? string.Empty, r.Step, r.UpdatedAtUtc,
                names.GetValueOrDefault(r.CreatedByUserId, string.Empty), body?.ExistingCompanyId);
        }).ToList();
    }

    public async Task<CompanySetupDraft?> GetDraftAsync(CompanySetupCaller caller, Guid id, CancellationToken ct)
    {
        var row = await _db.CompanySetupDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == caller.TenantId && d.Id == id, ct);
        return row is null || !await MaySeeDraftAsync(caller, row, ct) ? null : await ToDtoAsync(row, ct);
    }

    public async Task<CompanySetupOutcome<CompanySetupDraft>> CreateDraftAsync(CompanySetupCaller caller, CompanySetupDraftBody? body, CancellationToken ct)
    {
        if (body?.ExistingCompanyId is Guid target) return await CreateExistingDraftAsync(caller, body, target, ct);
        // No stranded drafts (Decision 12): a tenant that cannot create a company now cannot start one.
        var gate = await CompanyCreationGate.EvaluateAsync(_db, caller.TenantId, ct, licence: _licence);
        var (canCreate, code, message, messageAr) = Refusal(caller, gate);
        if (!canCreate) return CompanySetupOutcome<CompanySetupDraft>.Fail(409, code!, message!, messageAr, step: 1);
        if (body is null) return CompanySetupOutcome<CompanySetupDraft>.Fail(400, "body_required", "The draft body is required.");
        var json = JsonSerializer.Serialize(body, BodyJson);
        if (Encoding.UTF8.GetByteCount(json) > CompanySetupPlanner.MaxBodyBytes)
            return CompanySetupOutcome<CompanySetupDraft>.Fail(413, "draft_too_large", "The draft is larger than 128 KiB.");
        var now = DateTime.UtcNow;
        var row = new CompanySetupDraftRecord
        {
            TenantId = caller.TenantId, CreatedByUserId = caller.UserId, Status = CompanySetupDraftStatuses.Open,
            Step = 1, Version = 1, Body = json, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        _db.CompanySetupDrafts.Add(row);
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), now, "company.setup_draft_created", nameof(CompanySetupDraftRecord), row.Id.ToString(),
            caller.Context with { TenantId = caller.TenantId }, null));
        await _db.SaveChangesAsync(ct);
        return CompanySetupOutcome<CompanySetupDraft>.Ok((await ToDtoAsync(row, ct))!, 201);
    }

    /// <summary>
    /// Addendum A1: a draft that completes an existing company. No creation gate (the company exists); the caller must
    /// be allowed to complete that company (A5). One open draft per (tenant, company), enforced under the tenant setup
    /// lock — the same lock the commit takes — so two tabs racing cannot both open one: the loser gets 409
    /// <c>existing_company_draft_open</c> with the open draft's id, to resume it.
    /// </summary>
    private async Task<CompanySetupOutcome<CompanySetupDraft>> CreateExistingDraftAsync(CompanySetupCaller caller, CompanySetupDraftBody body, Guid target, CancellationToken ct)
    {
        if (await Planner.AuthorizeExistingAsync(caller, target, null, ct) is { } refused)
            return CompanySetupOutcome<CompanySetupDraft>.Fail(refused.Status, refused.Code, refused.Message, refused.MessageAr, "existingCompanyId", 1);
        var json = JsonSerializer.Serialize(body, BodyJson);
        if (Encoding.UTF8.GetByteCount(json) > CompanySetupPlanner.MaxBodyBytes)
            return CompanySetupOutcome<CompanySetupDraft>.Fail(413, "draft_too_large", "The draft is larger than 128 KiB.");

        CompanySetupDraftRecord? row = null;
        Guid? open = null;
        async Task RunAsync()
        {
            _db.ChangeTracker.Clear();
            row = null;
            await SetupTenantLock.AcquireAsync(_db, caller.TenantId, ct);
            open = (await OpenExistingDraftsAsync(caller.TenantId, ct)).GetValueOrDefault(target);
            if (open is not null) return;
            var now = DateTime.UtcNow;
            row = new CompanySetupDraftRecord
            {
                TenantId = caller.TenantId, CreatedByUserId = caller.UserId, Status = CompanySetupDraftStatuses.Open,
                Step = 1, Version = 1, Body = json, CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            _db.CompanySetupDrafts.Add(row);
            var audit = AuthAuditEntry.Create(Guid.NewGuid(), now, "company.setup_draft_created", nameof(CompanySetupDraftRecord), row.Id.ToString(),
                caller.Context with { TenantId = caller.TenantId }, JsonSerializer.Serialize(new { existingCompanyId = target }, BodyJson));
            audit.CompanyId = target;
            _db.AuditLogs.Add(audit);
            await _db.SaveChangesAsync(ct);
        }
        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await RunAsync();
                await tx.CommitAsync(ct);
            });
        }
        else await RunAsync();

        if (open is Guid openId)
            return new(409, default, new CompanySetupError(CompanySetupGates.ExistingDraftOpenCode,
                "This company already has a setup draft in progress. Resume it instead of starting another.",
                "لهذه الشركة مسودة إعداد قيد التنفيذ. تابعها بدلاً من بدء مسودة جديدة.", "existingCompanyId", 1) { DraftId = openId });
        return CompanySetupOutcome<CompanySetupDraft>.Ok((await ToDtoAsync(row!, ct))!, 201);
    }

    public async Task<CompanySetupOutcome<CompanySetupDraft>> UpdateDraftAsync(CompanySetupCaller caller, Guid id, CompanySetupDraftUpdate? update, CancellationToken ct)
    {
        if (update?.Body is null) return CompanySetupOutcome<CompanySetupDraft>.Fail(400, "body_required", "The draft body is required.");
        if (update.Step is < 1 or > 7) return CompanySetupOutcome<CompanySetupDraft>.Fail(400, "step_invalid", "The step is 1 to 7.", field: "step");
        var row = await _db.CompanySetupDrafts.SingleOrDefaultAsync(d => d.TenantId == caller.TenantId && d.Id == id, ct);
        if (row is null || !await MaySeeDraftAsync(caller, row, ct)) return CompanySetupOutcome<CompanySetupDraft>.Fail(404, "draft_not_found", "Draft not found.");
        // Addendum A1: a draft's target is fixed for its life — a new-company draft cannot become an edit of an existing
        // company (or another one), which would also step around the one-open-draft rule.
        if (update.Body.ExistingCompanyId != ReadBody(row)?.ExistingCompanyId)
            return CompanySetupOutcome<CompanySetupDraft>.Fail(409, "draft_target_locked",
                "A draft keeps the company it was started for. Start a new draft to set up a different company.",
                "تبقى المسودة مرتبطة بالشركة التي بدأت لها.", "existingCompanyId", 1);
        if (row.Status != CompanySetupDraftStatuses.Open)
            return CompanySetupOutcome<CompanySetupDraft>.Fail(409, "draft_not_open", $"This draft is {row.Status.ToLowerInvariant()} and can no longer be edited.");
        if (row.Version != update.Version)
            return Stale(row.Version);
        var json = JsonSerializer.Serialize(update.Body, BodyJson);
        if (Encoding.UTF8.GetByteCount(json) > CompanySetupPlanner.MaxBodyBytes)
            return CompanySetupOutcome<CompanySetupDraft>.Fail(413, "draft_too_large", "The draft is larger than 128 KiB.");
        row.Body = json;
        row.Step = (short)update.Step;
        row.Version = update.Version + 1;
        row.UpdatedAtUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // Another save of the same version won the race; this one read a version that no longer exists.
            _db.ChangeTracker.Clear();
            var current = await _db.CompanySetupDrafts.AsNoTracking().Where(d => d.TenantId == caller.TenantId && d.Id == id)
                .Select(d => d.Version).FirstOrDefaultAsync(ct);
            return Stale(current);
        }
        return CompanySetupOutcome<CompanySetupDraft>.Ok((await ToDtoAsync(row, ct))!);
    }

    private static CompanySetupOutcome<CompanySetupDraft> Stale(int current) =>
        new(409, default, new CompanySetupError("draft_version_stale",
            "This draft was saved somewhere else (another tab or device). Reload it to continue from the latest version.",
            "تم حفظ هذه المسودة في مكان آخر. أعد تحميلها للمتابعة من أحدث نسخة.", "version") { CurrentVersion = current });

    public async Task<CompanySetupOutcome<bool>> DiscardDraftAsync(CompanySetupCaller caller, Guid id, CancellationToken ct)
    {
        var row = await _db.CompanySetupDrafts.SingleOrDefaultAsync(d => d.TenantId == caller.TenantId && d.Id == id, ct);
        if (row is null || !await MaySeeDraftAsync(caller, row, ct)) return CompanySetupOutcome<bool>.Fail(404, "draft_not_found", "Draft not found.");
        if (row.Status == CompanySetupDraftStatuses.Submitted)
            return CompanySetupOutcome<bool>.Fail(409, "draft_submitted", "This draft already created its company and cannot be discarded.");
        if (row.Status == CompanySetupDraftStatuses.Open)
        {
            // Soft-discard: the row is kept for audit (its retention row purges idle drafts later).
            row.Status = CompanySetupDraftStatuses.Discarded;
            row.Version += 1;
            row.UpdatedAtUtc = DateTime.UtcNow;
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), row.UpdatedAtUtc, "company.setup_draft_discarded", nameof(CompanySetupDraftRecord),
                row.Id.ToString(), caller.Context with { TenantId = caller.TenantId }, null));
            await _db.SaveChangesAsync(ct);
        }
        return CompanySetupOutcome<bool>.Ok(true, 204);
    }

    private async Task<CompanySetupDraft?> ToDtoAsync(CompanySetupDraftRecord row, CancellationToken ct)
    {
        var names = await NamesAsync(row.TenantId, [row.CreatedByUserId], ct);
        var body = ReadBody(row) ?? EmptyBody();
        // Addendum A4: an existing-mode draft always reads the company's cutover status as it is NOW, so a draft saved
        // while payroll was Planned shows the first payroll month read-only once payroll has started.
        if (body.ExistingCompanyId is Guid target && body.Company is not null)
        {
            var status = await ScopedBypass.TenantWide(_db.CompanyCutovers, row.TenantId, Why).AsNoTracking()
                .Where(x => x.CompanyId == target).Select(x => x.Status).FirstOrDefaultAsync(ct);
            body = body with { Company = body.Company with { CutoverStatus = status } };
        }
        return new CompanySetupDraft(row.Id, row.Version, row.Step, row.Status, body, row.CreatedByUserId,
            names.GetValueOrDefault(row.CreatedByUserId, string.Empty), row.CreatedAtUtc, row.UpdatedAtUtc, row.ResultCompanyId);
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(Guid tenantId, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return await ScopedBypass.TenantWide(_db.Users, tenantId, Why).AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    internal static CompanySetupDraftBody? ReadBody(CompanySetupDraftRecord row)
    {
        try { return JsonSerializer.Deserialize<CompanySetupDraftBody>(row.Body, BodyJson); }
        catch (JsonException) { return null; }
    }

    private static CompanySetupDraftBody EmptyBody() => new(V.StartFromDefaults, null,
        new CompanySetupCompany("", null, null, "", "", "", null, null, null, null, null, null, null, null, ""),
        [], [], [], [], [], new CompanySetupRules(new CompanySetupWorkWeek("", false), new(false), new(false),
            new CompanySetupEmployeeNumbering("EMP", true, false, true, true, 4, 1)),
        new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null));

    // ── POST /dry-run ────────────────────────────────────────────────────────────────────────────────

    public async Task<CompanySetupPlan> DryRunAsync(CompanySetupCaller caller, CompanySetupDraftBody? body, CancellationToken ct) =>
        (await Planner.PlanAsync(caller, body, ct)).Plan;

    // ── POST / (commit) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The commit (Decision 4). One execution-strategy transaction under the tenant setup lock; the draft row is
    /// the idempotency witness, read INSIDE the lock: a draft already Submitted replays its first result
    /// (<c>replayed: true</c>) and creates nothing. The plan — including the creation gate — is recomputed under
    /// the lock, so a plan-limit race or a CR number claimed meanwhile refuses instead of over-creating.
    /// The company super admin is written after the commit (see <see cref="ProvisionCompanyAdminAsync"/>).
    /// </summary>
    public async Task<CompanySetupOutcome<CompanySetupResult>> CommitAsync(
        CompanySetupCaller caller, CompanySetupCommitRequest? request, Guid? idempotencyKey, CancellationToken ct)
    {
        if (request is null) return CompanySetupOutcome<CompanySetupResult>.Fail(400, "body_required", "The draft id and version are required.");
        if (!caller.IsGroupScope)
        {
            // Addendum A5: a company super admin commits only their own company's existing-mode draft. Anything else
            // (a new-company draft, another company's, or no such draft) is the group-scope refusal, as before.
            var probe = await _db.CompanySetupDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == caller.TenantId && d.Id == request.DraftId, ct);
            if (probe is null || ReadBody(probe)?.ExistingCompanyId is not Guid target)
                return CompanySetupOutcome<CompanySetupResult>.Fail(403, CompanySetupGates.GroupScopeRequiredCode, CompanySetupGates.GroupScopeRequiredMessage,
                    CompanySetupGates.GroupScopeRequiredMessageAr, step: 1);
            if (await Planner.ScopeRefusalAsync(caller, target, null, ct) is { } scope)
                return CompanySetupOutcome<CompanySetupResult>.Fail(scope.Status, scope.Code, scope.Message, scope.MessageAr, "existingCompanyId", 1);
        }
        if (idempotencyKey is null || idempotencyKey != request.DraftId)
            return CompanySetupOutcome<CompanySetupResult>.Fail(400, "idempotency_key_mismatch", "Send the draft id as the Idempotency-Key header.", field: "draftId");

        var tenantId = caller.TenantId;
        CompanySetupOutcome<CompanySetupResult>? refusal = null;
        CompanySetupResult? result = null;
        CompanySetupPlanModel? executed = null;
        // What the previous attempt of THIS request was committing. The execution strategy reruns RunAsync when a
        // COMMIT's outcome is unknown; if that commit landed, the draft is Submitted with this company, and the rerun is
        // this request's creation (201, and the super admin still to write), not a replay of another request's.
        (CompanySetupResult Result, CompanySetupPlanModel Model)? committing = null;

        async Task RunAsync()
        {
            _db.ChangeTracker.Clear();
            refusal = null; result = null; executed = null;
            await SetupTenantLock.AcquireAsync(_db, tenantId, ct);
            var draft = await _db.CompanySetupDrafts.SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == request.DraftId, ct);
            if (draft is null) { refusal = CompanySetupOutcome<CompanySetupResult>.Fail(404, "draft_not_found", "Draft not found."); return; }
            if (draft.Status == CompanySetupDraftStatuses.Submitted)
            {
                if (committing is { } mine && draft.ResultCompanyId == mine.Result.CompanyId)
                {
                    (result, executed) = mine;
                    return;
                }
                result = await ReplayAsync(draft, ct);
                if (result is null)
                    refusal = CompanySetupOutcome<CompanySetupResult>.Fail(409, "draft_company_removed",
                        "This draft already created a company, and that company has since been removed.");
                return;
            }
            if (draft.Status == CompanySetupDraftStatuses.Discarded)
            { refusal = CompanySetupOutcome<CompanySetupResult>.Fail(409, "draft_discarded", "This draft was discarded."); return; }
            if (draft.Version != request.Version)
            {
                refusal = new(409, default, new CompanySetupError("draft_version_stale",
                    "This draft was changed after you reviewed it. Review it again before creating the company.",
                    "تم تغيير هذه المسودة بعد مراجعتها. راجعها مرة أخرى قبل إنشاء الشركة.", "version", 7) { CurrentVersion = draft.Version });
                return;
            }
            var body = ReadBody(draft);
            var (plan, model) = await Planner.PlanAsync(caller, body, ct);
            if (!plan.Ok || model is null) { refusal = Refuse(plan); return; }

            Guid companyId;
            try { companyId = await ExecuteAsync(caller, model, ct); }
            catch (InvalidOperationException ex) when (model.ExistingCompanyId is not null)
            {
                // The company form's own validation refused an edit the planner let through (it re-reads under its row
                // lock). Nothing is written: the transaction rolls back and the user sees that service's reason.
                refusal = CompanySetupOutcome<CompanySetupResult>.Fail(ex is EmailDomainRefusedException e ? e.Status : 422,
                    ex is EmailDomainRefusedException r ? r.Code : ex is CompanyRegistrationNumberTakenException ? CompanySetupGates.CrTakenCode : "company_update_refused",
                    ex.Message, field: ex is CompanyRegistrationNumberTakenException ? "company.registrationNumber" : null, step: 2, plan: plan);
                return;
            }
            // A writer that clears its change tracker for its own retries must not drop the draft this commit marks.
            if (_db.Entry(draft).State == EntityState.Detached) _db.CompanySetupDrafts.Attach(draft);
            var (created, reused) = Counts(plan.Items);
            var existingMode = model.ExistingCompanyId is not null;
            var updated = existingMode ? CountAction(plan.Items, V.ActionUpdate) : null;
            var unchanged = existingMode ? CountAction(plan.Items, V.ActionUnchanged) : null;
            var now = DateTime.UtcNow;
            draft.Status = CompanySetupDraftStatuses.Submitted;
            draft.ResultCompanyId = companyId;
            draft.SubmittedAtUtc = now;
            draft.UpdatedAtUtc = now;
            draft.Step = 7;
            var marker = AuthAuditEntry.Create(Guid.NewGuid(), now, existingMode ? SetupCompletedAction : SetupCreatedAction,
                nameof(CompanySetupDraftRecord), draft.Id.ToString(),
                caller.Context with { TenantId = tenantId },
                JsonSerializer.Serialize(new SetupCreatedMarker(draft.Id, companyId, created, reused,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Body))).ToLowerInvariant(),
                    model.CompanyAdmin.Mode, model.CompanyAdminAction, updated, unchanged), BodyJson));
            marker.CompanyId = companyId;
            _db.AuditLogs.Add(marker);
            await _db.SaveChangesAsync(ct);
            executed = model;
            result = new CompanySetupResult(companyId, created, reused, [], Replayed: false, updated);
            committing = (result, model);
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await RunAsync();
                if (refusal is null) await tx.CommitAsync(ct);
                else { await tx.RollbackAsync(ct); _db.ChangeTracker.Clear(); }
            });
        }
        else await RunAsync();

        if (refusal is not null) return refusal;
        // Company Super Admin: AFTER the commit, in this request. The user writer runs its own execution-strategy
        // transaction (it cannot join this one); a failure never rolls the company back — readiness shows it.
        if (executed is not null && executed.CompanyAdminAction == V.ActionCreate)
            await ProvisionCompanyAdminAsync(caller, result!.CompanyId, executed.CompanyAdmin, ct);
        var readiness = await ReadinessAsync(tenantId, result!.CompanyId, ct);
        return CompanySetupOutcome<CompanySetupResult>.Ok(result with { Readiness = readiness ?? [] }, result.Replayed ? 200 : 201);
    }

    /// <summary>The commit marker (idempotency witness and audit). <see cref="Updated"/> and <see cref="Unchanged"/>
    /// are written by an existing-company completion only (Addendum A6: counts by action).</summary>
    private sealed record SetupCreatedMarker(Guid DraftId, Guid CompanyId, IReadOnlyDictionary<string, int> Created,
        IReadOnlyDictionary<string, int> Reused, string BodySha256, string CompanyAdminMode, string CompanyAdminAction,
        IReadOnlyDictionary<string, int>? Updated = null, IReadOnlyDictionary<string, int>? Unchanged = null);

    internal static Dictionary<string, int> CountAction(IEnumerable<PlanItem> items, string action) =>
        items.Where(i => i.Action == action).GroupBy(i => i.Area, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private async Task<CompanySetupResult?> ReplayAsync(CompanySetupDraftRecord draft, CancellationToken ct)
    {
        if (draft.ResultCompanyId is not Guid companyId) return null;
        var markerJson = await ScopedBypass.NullableTenantWide(_db.AuditLogs, draft.TenantId,
                "Replay of a committed create-company draft: its own commit marker, found by the draft id.")
            .AsNoTracking()
            .Where(a => (a.Action == SetupCreatedAction || a.Action == SetupCompletedAction) && a.EntityId == draft.Id.ToString())
            .OrderBy(a => a.CreatedAtUtc).Select(a => a.Metadata).FirstOrDefaultAsync(ct);
        var marker = markerJson is null ? null : JsonSerializer.Deserialize<SetupCreatedMarker>(markerJson, BodyJson);
        return new CompanySetupResult(companyId,
            marker?.Created ?? new Dictionary<string, int>(), marker?.Reused ?? new Dictionary<string, int>(), [], Replayed: true, marker?.Updated);
    }

    internal static (Dictionary<string, int> Created, Dictionary<string, int> Reused) Counts(IEnumerable<PlanItem> items)
    {
        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var reused = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var bucket = item.Action == V.ActionCreate ? created : item.Action == V.ActionReuse ? reused : null;
            if (bucket is null) continue;
            bucket[item.Area] = bucket.GetValueOrDefault(item.Area) + 1;
        }
        return (created, reused);
    }

    /// <summary>A refused plan as an HTTP answer: the creation gate keeps the status the company form uses for it;
    /// a name, CR or code clash is 409; anything else (a field the user must fix) is 422. The plan rides along.</summary>
    private static CompanySetupOutcome<CompanySetupResult> Refuse(CompanySetupPlan plan)
    {
        var first = plan.Problems.FirstOrDefault(p => p.Step == 1) ?? plan.Problems.FirstOrDefault();
        var status = first?.Code switch
        {
            "company_limit_reached" => 402,
            CompanySetupGates.GroupScopeRequiredCode => 403,
            "account_type_single_company" or "company_switcher_selected" => 409,
            _ => plan.Items.Any(i => i.Action == V.ActionConflict) ? 409 : 422,
        };
        var conflict = plan.Items.FirstOrDefault(i => i.Action == V.ActionConflict);
        // The headline: a step-1 refusal (the gate) first; then a name or CR clash; then the first problem.
        var headline = first?.Step == 1 ? first
            : plan.Problems.FirstOrDefault(p => p.Code is CompanySetupGates.NameTakenCode or CompanySetupGates.CrTakenCode) ?? first;
        var code = headline?.Code ?? "plan_conflict";
        var message = headline?.Message ?? conflict?.Reason ?? "Nothing was created. Fix the highlighted items and try again.";
        return CompanySetupOutcome<CompanySetupResult>.Fail(status, code, message, headline?.MessageAr, headline?.Field, headline?.Step, plan);
    }

    /// <summary>Executes a plan the planner accepted. Tracks and saves inside the caller's transaction.</summary>
    private async Task<Guid> ExecuteAsync(CompanySetupCaller caller, CompanySetupPlanModel m, CancellationToken ct)
    {
        var tenantId = caller.TenantId;
        var ctx = caller.Context with { TenantId = tenantId };
        var at = DateTime.UtcNow;
        void Audit(string action, string entity, Guid id, Guid companyId, object? meta = null)
        {
            var row = AuthAuditEntry.Create(Guid.NewGuid(), at, action, entity, id.ToString(), ctx,
                JsonSerializer.Serialize(new { source = "company_setup_wizard", change = meta }, BodyJson));
            row.CompanyId = companyId;
            _db.AuditLogs.Add(row);
        }

        Guid companyId;
        if (m.ExistingCompanyId is Guid existingId)
        {
            // Addendum A4/A6: the company row is UPDATED through the company form's own path (all its validations, its
            // row lock and its audit), never recreated; it joins this transaction.
            companyId = existingId;
            if (m.CompanyChanged
                && await _organization.UpdateCompanyAsync(tenantId, existingId, m.CompanyRequest, ctx, ct) is null)
                throw new InvalidOperationException("The company was removed while its setup was being completed.");
        }
        else
        {
            // Decision 2: the ONE authoritative create path, with all its validations, inside this transaction.
            companyId = (await _organization.CreateCompanyAsync(tenantId, m.CompanyRequest, ctx, ct)).Id;
        }

        var branchIds = new Dictionary<string, Guid>(m.ExistingBranchIds, StringComparer.Ordinal);
        Guid? headOffice = null;
        foreach (var pb in m.Branches)
        {
            var b = pb.Body;
            Guid branchId;
            if (pb.Existing is { } row)
            {
                branchId = row.Id;
                // Only the fields the wizard shows change; address, labour office and activation are kept.
                if (pb.Changed)
                    await _organization.UpdateBranchAsync(tenantId, row.Id, new BranchRequest(companyId, row.Code, b.NameEn.Trim(), b.NameAr?.Trim(),
                        row.CountryCode, b.City.Trim(), row.AddressLine1, row.AddressLine2, b.TimeZoneId, row.LaborOfficeCode, b.IsHeadOffice, row.IsActive), ctx, ct);
            }
            else
                branchId = (await _organization.CreateBranchAsync(tenantId, new BranchRequest(companyId, b.Code, b.NameEn.Trim(), b.NameAr?.Trim(),
                    m.Country, b.City.Trim(), null, null, b.TimeZoneId, null, b.IsHeadOffice), ctx, ct)).Id;
            branchIds[b.Code] = branchId;
            if (b.IsHeadOffice) headOffice = branchId;
            foreach (var pl in pb.Locations)
            {
                var l = pl.Body;
                if (pl.Existing is { } existingLocation)
                {
                    if (!pl.Changed) continue;
                    var tracked = await _db.Locations.SingleAsync(x => x.TenantId == tenantId && x.Id == existingLocation.Id, ct);
                    tracked.NameEn = l.NameEn.Trim();
                    tracked.NameAr = l.NameAr?.Trim() ?? string.Empty;
                    tracked.Latitude = l.Latitude;
                    tracked.Longitude = l.Longitude;
                    tracked.GeofenceRadiusMeters = l.RadiusMeters;
                    tracked.UpdatedAtUtc = at;
                    tracked.UpdatedBy = caller.UserId;
                    Audit("organization.location_updated", nameof(Location), tracked.Id, companyId, new { l.Code, branch = b.Code });
                    continue;
                }
                var location = new Location
                {
                    TenantId = tenantId, BranchId = branchId, Code = l.Code, NameEn = l.NameEn.Trim(), NameAr = l.NameAr?.Trim() ?? string.Empty,
                    City = b.City.Trim(), CountryCode = m.Country, Latitude = l.Latitude, Longitude = l.Longitude,
                    GeofenceRadiusMeters = l.RadiusMeters, IsActive = true, CreatedBy = caller.UserId,
                };
                _db.Locations.Add(location);
                Audit("organization.location_created", nameof(Location), location.Id, companyId, new { l.Code, branch = b.Code });
            }
        }
        await _db.SaveChangesAsync(ct);

        var costCentreIds = new Dictionary<string, Guid>(m.ExistingCostCentreIds, StringComparer.Ordinal);
        foreach (var pc in m.CostCentres)
        {
            var cc = pc.Body;
            if (pc.Existing is { } row)
            {
                costCentreIds[cc.Code] = row.Id;
                if (pc.Changed)
                    await _organization.UpdateCostCenterAsync(tenantId, row.Id, new CostCenterRequest(companyId, row.Code, cc.NameEn.Trim(), row.IsActive), ctx, ct);
                continue;
            }
            costCentreIds[cc.Code] = (await _organization.CreateCostCenterAsync(tenantId, new CostCenterRequest(companyId, cc.Code, cc.NameEn.Trim()), ctx, ct)).Id;
        }

        var gradeIds = new Dictionary<string, Guid>(m.ExistingGradeIds, StringComparer.Ordinal);
        foreach (var g in m.NewGrades)
            gradeIds[g.Code] = (await _organization.CreateGradeAsync(tenantId, new GradeRequest(g.Code, g.NameEn.Trim(), null, g.Level,
                g.MinSalary, g.MidSalary, g.MaxSalary, m.GradeCurrency, true, string.IsNullOrWhiteSpace(g.NameAr) ? null : g.NameAr.Trim()), ctx, ct)).Id;

        // Decision 7: existing departments are reused as they are — never re-pointed to this company's branch or cost centre.
        var departmentIds = new Dictionary<string, Guid>(m.ExistingDepartmentIds, StringComparer.Ordinal);
        foreach (var d in m.NewDepartments)
        {
            Guid? parent = d.ParentCode is { } p && departmentIds.TryGetValue(p, out var pid) ? pid : null;
            Guid? branch = d.BranchCode is { } bc && branchIds.TryGetValue(bc, out var bid) ? bid : null;
            Guid? cost = d.CostCentreCode is { } cc && costCentreIds.TryGetValue(cc, out var cid) ? cid : null;
            departmentIds[d.Code] = (await _organization.CreateDepartmentAsync(tenantId, new DepartmentRequest(branch, parent, cost, d.Code,
                d.NameEn.Trim(), d.NameAr?.Trim(), null), ctx, ct)).Id;
        }
        foreach (var ds in m.NewDesignations)
        {
            Guid? dept = ds.DepartmentCode is { } dc && departmentIds.TryGetValue(dc, out var did) ? did : null;
            Guid? grade = ds.GradeCode is { } gc && gradeIds.TryGetValue(gc, out var gid) ? gid : null;
            await _organization.CreateDesignationAsync(tenantId, new DesignationRequest(dept, ds.Code, ds.TitleEn.Trim(), ds.TitleAr?.Trim(),
                ds.GradeCode, grade, null, null), ctx, ct);
        }

        if (m.EmployeeIdRuleAction is V.ActionCreate or V.ActionUpdate)
        {
            var (rule, ruleCreated) = await SetupWriters.UpsertEmployeeIdRuleAsync(_db, tenantId, companyId, m.EmployeeIdRule, caller.UserId, ct);
            Audit(ruleCreated ? "organization.employee_id_rule_created" : "organization.employee_id_rule_updated", nameof(EmployeeIdRule), rule.Id, companyId, m.EmployeeIdRule);
        }

        if (m.CutoverAction == V.ActionCreate)
        {
            var cutover = new CompanyCutover
            {
                TenantId = tenantId, CompanyId = companyId, CutoverDate = m.CutoverDate, Status = CutoverStatuses.Planned,
                Notes = "Planned by the create-company wizard: the first payroll month.", CreatedBy = caller.UserId,
            };
            _db.CompanyCutovers.Add(cutover);
            Audit("organization.company_cutover_planned", nameof(CompanyCutover), cutover.Id, companyId, new { cutoverDate = m.CutoverDate });
        }
        else if (m.CutoverAction == V.ActionUpdate && m.ExistingCutoverId is Guid cutoverId)
        {
            // Addendum A4: only a cutover still Planned moves (the planner checked it under the setup lock).
            var cutover = await _db.CompanyCutovers.SingleAsync(x => x.TenantId == tenantId && x.Id == cutoverId && x.Status == CutoverStatuses.Planned, ct);
            var previous = cutover.CutoverDate;
            cutover.CutoverDate = m.CutoverDate;
            cutover.UpdatedAtUtc = at;
            cutover.UpdatedBy = caller.UserId;
            Audit("organization.company_cutover_updated", nameof(CompanyCutover), cutover.Id, companyId, new { from = previous, cutoverDate = m.CutoverDate });
        }

        if (m.HolidayCalendar is not null)
        {
            var written = await SetupWriters.UpsertHolidayCalendarAsync(_db, tenantId, companyId, headOffice, m.Country, m.HolidayCalendar, ct);
            Audit("leave.holiday_calendar_created", nameof(PublicHolidayCalendar), written.Calendar.Id, companyId,
                new { year = m.HolidayCalendar.CalendarYear, holidays = written.HolidaysAdded });
        }

        if (m.MissingLeaveDefaults.Count > 0)
        {
            await TenantProvisioningBundle.InstallDefaultLeaveForCountryAsync(_db, tenantId, m.Country, ct);
            foreach (var policy in _db.ChangeTracker.Entries<LeavePolicy>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList())
                Audit("leave.policy_created", nameof(LeavePolicy), policy.Id, companyId, new { policy.Name, policy.CountryCode, tenantWide = true });
        }

        if (m.NewGccDefault is not null)
        {
            var row = m.NewGccDefault;
            row.Id = Guid.NewGuid();
            row.UpdatedBy = caller.UserId;
            _db.GCCComplianceSettings.Add(row);
            Audit("setup.gcc_setting_created", nameof(GCCComplianceSetting), row.Id, companyId, new { row.CountryCode, tenantDefault = true });
        }
        if (m.WorkWeekOverride is not null)
        {
            var row = m.WorkWeekOverride;
            row.Id = Guid.NewGuid();
            row.CompanyId = companyId;
            row.UpdatedBy = caller.UserId;
            _db.GCCComplianceSettings.Add(row);
            Audit("setup.gcc_setting_created", nameof(GCCComplianceSetting), row.Id, companyId, new { row.CountryCode, row.WeekendDays, companyOverride = true });
        }
        if (m.WorkWeekUpdate is { } weekUpdate)
        {
            var row = await _db.GCCComplianceSettings.SingleAsync(x => x.TenantId == tenantId && x.Id == weekUpdate.Id && x.CompanyId == companyId, ct);
            var previous = row.WeekendDays;
            row.WeekendDays = weekUpdate.WeekendDays;
            row.WorkWeek = weekUpdate.WorkWeek;
            row.UpdatedAtUtc = at;
            row.UpdatedBy = caller.UserId;
            Audit("setup.gcc_setting_updated", nameof(GCCComplianceSetting), row.Id, companyId, new { row.CountryCode, from = previous, row.WeekendDays, companyOverride = true });
        }

        await _db.SaveChangesAsync(ct);
        return companyId;
    }

    // ── Company Super Admin (after commit) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The contract's second case: the User Management writer (<see cref="IAccessManagementService"/>) runs its own
    /// execution-strategy transaction (<c>ExecuteInTransactionAsync</c>, with its own admin-seat lock and commit
    /// reconciliation), so it cannot join the setup transaction. It runs immediately after the commit, in the same
    /// request; a failure is recorded and surfaces as the <c>companyAdmin</c> readiness item (todo, with the User
    /// Management link as the one-click retry). The company is never rolled back for it.
    ///
    /// <para>Invite: a login on the official company email with the Admin role and a grant to this company only, no
    /// group scope. Nobody types a password: the login is created with a server-generated secret nobody learns, and
    /// the person gets in through the existing single-use sign-in (password-reset) link, which User Management
    /// emails or hands over — the readiness item says so until they have signed in. KynexOne creates no mailbox.</para>
    /// </summary>
    internal async Task<string?> ProvisionCompanyAdminAsync(CompanySetupCaller caller, Guid companyId, CompanySetupCompanyAdmin admin, CancellationToken ct)
    {
        var ctx = caller.Context with { TenantId = caller.TenantId };
        string? failure = null;
        // The plan's user limit keeps the code User Management answers with (user_limit_reached), whether this preflight
        // or the writer's recount under the tenant row lock refused it; readiness reads it back to say what to do.
        string? code = null;
        try
        {
            if (admin.Mode == V.AdminInvite)
            {
                if (await UserSeatLimit.CheckAsync(_db, caller.TenantId, Zayra.Api.Infrastructure.Licensing.LicenceLimits.MaxUsers(_licence), ct) is { } seats) { failure = seats.Message; code = UserSeatLimit.Refusal.Code; }
                else
                {
                    var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) + "aA1!";
                    await _access.CreateUserAsync(caller.TenantId, new CreateUserRequest(
                        admin.Email!.Trim().ToLowerInvariant(), admin.FullNameEn!.Trim(), secret, ["Admin"], CompanyId: companyId, IsGroupScope: false), ctx, ct);
                }
            }
            else if (admin.Mode == V.AdminExisting && admin.ExistingUserId is Guid userId)
            {
                await _access.CreateEntityGrantAsync(caller.TenantId, userId, companyId, "Company Administrator", EntityGrantModes.SelectedCompanies, ctx, ct);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PrivilegeCeilingException or DbUpdateException)
        {
            failure = ex is PrivilegeCeilingException p ? p.Refusal.MessageEn : ex.Message;
            code = ex switch
            {
                UserSeatLimitReachedException => UserSeatLimit.Refusal.Code,
                PrivilegeCeilingException ceiling => ceiling.Refusal.Code,
                _ => null,
            };
        }
        if (failure is null) return null;

        _db.ChangeTracker.Clear();
        var row = AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, AdminFailedAction, nameof(Company), companyId.ToString(), ctx,
            JsonSerializer.Serialize(new { mode = admin.Mode, code, reason = failure }, BodyJson));
        row.CompanyId = companyId;
        _db.AuditLogs.Add(row);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* the readiness item still shows the admin as missing */ }
        return failure;
    }

    // ── GET /readiness/{companyId} ───────────────────────────────────────────────────────────────────

    /// <summary>The readiness checklist as a caller may read it: a company super admin for their own company only.</summary>
    public async Task<CompanySetupOutcome<IReadOnlyList<ReadinessItem>>> ReadinessForAsync(CompanySetupCaller caller, Guid companyId, CancellationToken ct)
    {
        if (!caller.IsGroupScope && await Planner.ScopeRefusalAsync(caller, companyId, null, ct) is { } refused)
            return CompanySetupOutcome<IReadOnlyList<ReadinessItem>>.Fail(refused.Status, refused.Code, refused.Message, refused.MessageAr);
        var items = await ReadinessAsync(caller.TenantId, companyId, ct);
        return items is null
            ? CompanySetupOutcome<IReadOnlyList<ReadinessItem>>.Fail(404, "company_not_found", "Company not found.", "الشركة غير موجودة.")
            : CompanySetupOutcome<IReadOnlyList<ReadinessItem>>.Ok(items);
    }

    /// <summary>The server-computed checklist. Null when the company is not in this tenant.</summary>
    public async Task<IReadOnlyList<ReadinessItem>?> ReadinessAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var company = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (company is null) return null;
        return await CompanySetupReadiness.ComputeAsync(_db, _email, tenantId, company, ct);
    }
}
