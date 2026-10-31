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
/// <c>the retired setup assistant</c> or anything in <c>Infrastructure.AI</c>; the defaults come from the static,
/// deterministic template and the country pack. <c>CompanySetupNoModelCallTests</c> asserts it.</para>
/// </summary>
public sealed class CompanySetupService
{
    internal static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);
    private const string Why = "Create-company wizard: the tenant's company register, organisation codes and drafts are read across every legal entity of the tenant whatever company the caller's switcher selects.";
    public const string SetupCreatedAction = "company.setup_created";
    /// <summary>Addendum A6: the commit marker of an existing-company completion (counts by action).</summary>
    public const string SetupCompletedAction = "company.setup_completed";
    /// <summary>The audit row written when the company super admin could not be written after the commit.</summary>
    public const string AdminFailedAction = "company.setup_admin_failed";

    private readonly ZayraDbContext _db;
    private readonly IOrganizationSetupService _organization;
    private readonly IAccessManagementService _access;
    private readonly IEmailService _email;
    private readonly IWorkWeekService _workWeek;
    private readonly IConfiguration? _config;

    public CompanySetupService(ZayraDbContext db, IOrganizationSetupService organization, IAccessManagementService access,
        IWorkWeekService workWeek, IEmailService email, IConfiguration? config = null)
    {
        _db = db;
        _organization = organization;
        _access = access;
        _workWeek = workWeek;
        _email = email;
        _config = config;
        Planner = new CompanySetupPlanner(db, access, workWeek);
    }

    public CompanySetupPlanner Planner { get; }

    // ── GET /context ─────────────────────────────────────────────────────────────────────────────────

    public async Task<CompanySetupContext> GetContextAsync(CompanySetupCaller caller, CancellationToken ct)
    {
        var tenantId = caller.TenantId;
        var tenant = await _db.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
            .Select(t => new { t.AccountType, t.CompanyCreationMode }).FirstOrDefaultAsync(ct);
        var sub = await _db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var used = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).CountAsync(c => !c.IsDeleted, ct);
        var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct);
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
        return new CompanySetupContext(
            canCreate, code, message,
            tenant?.CompanyCreationMode ?? string.Empty,
            tenant?.AccountType ?? string.Empty,
            used,
            sub is { MaxCompanies: > 0 } ? sub.MaxCompanies : null,
            caller.IsGroupScope,
            sisters,
            // Addendum B: no shared group catalog and no workspace grade currency — every company owns its own rows.
            SharedCatalog: null,
            GradesCurrency: null,
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
            var readiness = await CompanySetupReadiness.ComputeAsync(_db, _email, caller.TenantId, company, ct, _config);
            cards.Add(new CompanySetupManageableCompany(company.Id, company.LegalNameEn, string.IsNullOrWhiteSpace(company.LegalNameAr) ? null : company.LegalNameAr,
                company.CountryCode, readiness.Count(r => r.Status == V.ReadinessDone), readiness.Count, openDrafts.GetValueOrDefault(company.Id)));
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
        if (gate.AsDraft) return (false, CompanySetupGates.PlatformActivationDisabledCode, CompanySetupGates.PlatformActivationDisabledMessage,
            CompanySetupGates.PlatformActivationDisabledMessageAr);
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
            return CompanySetupOutcome<CompanySetupDraftBody>.Fail(400, "country_invalid", CompanySetupDataEntry.CountryInvalidMessage,
                CompanySetupDataEntry.CountryInvalidMessageAr, "company.countryCode", 2);
        var currency = HomeJurisdiction.CurrencyFor(country);

        // Addendum B: grade bands are in the company's own currency.
        var template = SetupDraftDefaults.DeterministicCompanyDefaults(country, currency, null);
        var resolved = await _workWeek.ResolveAsync(tenantId, null, country, ct);
        var weekend = CompanySetupPack.FormatWeekend(resolved.WeekendDays);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var firstPayrollMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var company = new CompanySetupCompany(
            LegalNameEn: string.Empty, LegalNameAr: null, TradeName: null, CountryCode: country, Currency: currency,
            RegistrationNumber: string.Empty, MolEstablishmentNumber: null, GosiEmployerId: null, UnifiedNationalNumber: null,
            TaxNumber: null, QiwaEstablishmentId: null, EmailDomain: null, WorkEmailPattern: WorkEmailPatterns.FirstLast,
            ErpCompanyCode: null, FirstPayrollMonth: firstPayrollMonth);

        List<CompanySetupBranch> branches;
        List<CompanySetupCostCentre> costCentres;
        List<CompanySetupDepartment> departments;
        List<CompanySetupDesignation> designations;
        List<CompanySetupGrade> grades;
        var idRule = template.EmployeeIdRule!;
        CompanySetupEmployeeNumbering numbering = new(idRule.CompanyPrefix, idRule.UseCountryPrefix, idRule.UseBranchPrefix,
            idRule.UseDepartmentPrefix, idRule.UseYear, idRule.PaddingLength, 1);
        var holidayUsePack = template.HolidayCalendar is not null;
        var workWeek = new CompanySetupWorkWeek(weekend, Override: false);

        if (sister is null)
        {
            // A new company has no rows of its own, so the whole pack is proposed, linked within itself.
            branches = template.Branches.Select(b => new CompanySetupBranch(OrgCodes.Normalize(b.Code), b.NameEn, null, b.City,
                HomeJurisdiction.TimeZoneFor(country), b.IsHeadOffice, [])).ToList();
            // Codes are unique within a company, and a new company has none yet: the pack's codes as they are.
            var usedCostCentres = new HashSet<string>(StringComparer.Ordinal);
            var ccByDept = template.CostCenters.ToDictionary(cc => OrgCodes.Normalize(cc.DepartmentCode),
                cc => CompanySetupRows.Unique(usedCostCentres, cc.Code), StringComparer.Ordinal);
            costCentres = template.CostCenters.Select(cc => new CompanySetupCostCentre(ccByDept[OrgCodes.Normalize(cc.DepartmentCode)], cc.Name, null)).ToList();
            var headOfficeCode = OrgCodes.Normalize(template.Branches.FirstOrDefault(b => b.IsHeadOffice)?.Code);
            departments = template.Departments.Select(d => OrgCodes.Normalize(d.Code)).Distinct(StringComparer.Ordinal)
                .Select(code => new CompanySetupDepartment(code, template.Departments.First(x => OrgCodes.Normalize(x.Code) == code).NameEn, null, null,
                    headOfficeCode.Length == 0 ? null : headOfficeCode, ccByDept.GetValueOrDefault(code))).ToList();
            grades = template.Grades.Select(g => new CompanySetupGrade(OrgCodes.Normalize(g.Code), g.Name, null, g.Level, g.MinSalary, g.MidSalary, g.MaxSalary)).ToList();
            designations = template.Designations.Select(d => new CompanySetupDesignation(OrgCodes.Normalize(d.Code), d.TitleEn, null,
                OrgCodes.Normalize(d.DepartmentCode), OrgCodes.Normalize(d.GradeCode))).ToList();
        }
        else
        {
            // Decision 8 + Addendum B3: everything is copied BY VALUE into the draft for editing — branches with renamed
            // codes, the sister's cost centres, departments, designations and grades with their own codes (every
            // organisation code is per company), links kept by code. Nothing is shared by reference.
            var sisterBranches = await _db.Branches.AsNoTracking()
                .Where(b => b.TenantId == tenantId && b.CompanyId == sister.Id && !b.IsDeleted && b.IsActive)
                .OrderByDescending(b => b.IsHeadOffice).ThenBy(b => b.Code).ToListAsync(ct);
            var branchCodes = new HashSet<string>(StringComparer.Ordinal);
            var branchRename = new Dictionary<string, string>(StringComparer.Ordinal);
            string RenameBranch(string code)
            {
                var baseCode = OrgCodes.Normalize(code);
                var candidate = $"{baseCode}-2";
                for (var n = 3; !branchCodes.Add(candidate); n++) candidate = $"{baseCode}-{n}";
                branchRename[baseCode] = candidate;
                return candidate;
            }
            branches = sisterBranches.Select(b => new CompanySetupBranch(RenameBranch(b.Code), b.NameEn, b.NameAr == "" ? null : b.NameAr, b.City,
                string.IsNullOrWhiteSpace(b.TimeZoneId) ? HomeJurisdiction.TimeZoneFor(country) : b.TimeZoneId, b.IsHeadOffice, [])).ToList();
            if (branches.Count > 0 && branches.Count(b => b.IsHeadOffice) != 1)
                branches = branches.Select((b, i) => b with { IsHeadOffice = i == 0 }).ToList();
            var rows = await SisterRowsAsync(tenantId, sister.Id, ct);
            costCentres = rows.CostCentres;
            departments = rows.Departments.Select(d => d with
            {
                BranchCode = d.BranchCode is { } b && branchRename.TryGetValue(b, out var nb) ? nb : null,
            }).ToList();
            designations = rows.Designations;
            grades = rows.Grades;
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
    /// What the company lacks is filled from the country pack exactly as on create, and carries no id (it is new).
    /// Addendum B: departments, designations and grades are the company's OWN rows (with existingId and an A9 baseline), and a
    /// company super admin may add to them like a group admin.
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

        var template = SetupDraftDefaults.DeterministicCompanyDefaults(country, currency, null);
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

        // Addendum B: the company's OWN organisation rows (cost centres, departments, designations, grades), each with its
        // existingId and A9 baseline. A section the company has nothing in yet is filled from the country pack as new rows.
        var org = await CompanySetupRows.CompanyRowsAsync(_db, tenantId, companyId, ct);
        var maps = await CompanySetupRows.CodeMapsAsync(_db, tenantId, ct);
        var costCentres = org.CostCentres.Where(x => !x.IsDeleted && x.IsActive).Select(CompanySetupRows.CostCentre).ToList();
        var ccByTemplateDept = new Dictionary<string, string>(StringComparer.Ordinal);
        if (costCentres.Count == 0)
        {
            // Codes are unique within the company: a pack code steps past the company's own codes only (removed ones too).
            var used = org.CostCentres.Select(x => OrgCodes.Normalize(x.Code)).ToHashSet(StringComparer.Ordinal);
            foreach (var cc in template.CostCenters)
            {
                var candidate = CompanySetupRows.Unique(used, cc.Code);
                ccByTemplateDept[OrgCodes.Normalize(cc.DepartmentCode)] = candidate;
                costCentres.Add(new CompanySetupCostCentre(candidate, cc.Name, null));
            }
        }

        var departments = org.Departments.Where(d => !d.IsDeleted && d.IsActive).Select(d => CompanySetupRows.Department(d, maps)).ToList();
        var grades = org.Grades.Where(g => !g.IsDeleted && g.IsActive).Select(CompanySetupRows.Grade).ToList();
        var designations = org.Designations.Where(d => !d.IsDeleted && d.IsActive).Select(d => CompanySetupRows.Designation(d, maps)).ToList();

        // Pack rows: codes are unique within the company only, so a pack code steps past the company's own codes (removed
        // rows included — they still hold theirs), and the pack's links follow the rename.
        // Linked to the head office only when it is saved: a live save of Departments (B2) needs its links in the database.
        var headOfficeCode = branches.FirstOrDefault(b => b.IsHeadOffice && b.ExistingId is not null)?.Code;
        var deptRename = new Dictionary<string, string>(StringComparer.Ordinal);
        var gradeRename = new Dictionary<string, string>(StringComparer.Ordinal);
        if (departments.Count == 0)
        {
            var used = org.Departments.Select(d => OrgCodes.Normalize(d.Code)).ToHashSet(StringComparer.Ordinal);
            foreach (var d in template.Departments)
            {
                var code = OrgCodes.Normalize(d.Code);
                if (deptRename.ContainsKey(code)) continue;
                deptRename[code] = CompanySetupRows.Unique(used, code);
                departments.Add(new CompanySetupDepartment(deptRename[code], d.NameEn, null, null, headOfficeCode, ccByTemplateDept.GetValueOrDefault(code)));
            }
        }
        if (grades.Count == 0)
        {
            var used = org.Grades.Select(g => OrgCodes.Normalize(g.Code)).ToHashSet(StringComparer.Ordinal);
            foreach (var g in template.Grades)
            {
                var code = OrgCodes.Normalize(g.Code);
                if (gradeRename.ContainsKey(code)) continue;
                gradeRename[code] = CompanySetupRows.Unique(used, code);
                grades.Add(new CompanySetupGrade(gradeRename[code], g.Name, null, g.Level, g.MinSalary, g.MidSalary, g.MaxSalary));
            }
        }
        if (designations.Count == 0)
        {
            // A pack designation is offered only when its department and grade will exist (in this body).
            var knownDepartments = departments.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);
            var knownGrades = grades.Select(g => g.Code).ToHashSet(StringComparer.Ordinal);
            var used = org.Designations.Select(d => OrgCodes.Normalize(d.Code)).ToHashSet(StringComparer.Ordinal);
            foreach (var d in template.Designations)
            {
                var dept = OrgCodes.Normalize(d.DepartmentCode);
                var grade = OrgCodes.Normalize(d.GradeCode);
                dept = deptRename.GetValueOrDefault(dept, dept);
                grade = gradeRename.GetValueOrDefault(grade, grade);
                if (!knownDepartments.Contains(dept) || !knownGrades.Contains(grade)) continue;
                designations.Add(new CompanySetupDesignation(CompanySetupRows.Unique(used, d.Code), d.TitleEn, null, dept, grade));
            }
        }

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
        var gate = await CompanyCreationGate.EvaluateAsync(_db, caller.TenantId, ct);
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

    /// <summary>
    /// The administrator a draft's commit would create or promote — a newly invited company super admin, or an existing
    /// user given the Admin role — or null when it makes nobody an Admin. The controller asks the caller to confirm it's
    /// them before such a commit (StepUpService), as for every other way of making an Admin.
    /// </summary>
    public async Task<CompanySetupCompanyAdmin?> AdminGrantAsync(Guid tenantId, Guid draftId, CancellationToken ct)
    {
        var draft = await _db.CompanySetupDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == draftId, ct);
        var admin = draft is null ? null : ReadBody(draft)?.CompanyAdmin;
        return admin?.Mode is CompanySetupVocabulary.AdminInvite or CompanySetupVocabulary.AdminExisting ? admin : null;
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

    // ── POST /drafts/{id}/sections/{section} (Addendum B2: live section save) ──────────────────────────

    /// <summary>The marker audit of a live section save, and the witness its blind retry is answered from.</summary>
    public const string SectionAppliedAction = "company.setup_section_applied";

    private sealed record SectionCounts(int Created, int Updated, int Unchanged);
    private sealed record SectionAppliedMarker(Guid DraftId, string Section, int FromVersion, SectionCounts Counts);

    /// <summary>
    /// Addendum B2. Writes ONE Organisation section of an existing-company draft now, through the same planner (focused on
    /// that section: only its problems and the step-1 gates block, and a link into another section must already be
    /// saved) and the same writers as the commit, under the tenant setup lock in one transaction. The draft is rebased in
    /// place: each row of the section gets its <c>existingId</c> and a fresh A9 baseline (and the values now saved), the
    /// other sections and the row order are untouched, version + 1, step 4 — so the final commit reads the saved section
    /// as unchanged. A retry whose version is one behind and whose (draft, section, version) marker exists answers the
    /// current draft with <c>replayed: true</c>; nothing is written twice.
    /// </summary>
    public async Task<CompanySetupOutcome<SectionApplyResult>> ApplySectionAsync(CompanySetupCaller caller, Guid draftId, string? section,
        CompanySetupSectionApplyRequest? request, CancellationToken ct)
    {
        if (!V.Sections.IsValid(section))
            return CompanySetupOutcome<SectionApplyResult>.Fail(400, "section_invalid",
                $"Choose one of the sections {string.Join(", ", V.Sections.All)}.", "اختر قسماً صحيحاً من أقسام الهيكل التنظيمي.", "section", 4);
        if (request is null) return CompanySetupOutcome<SectionApplyResult>.Fail(400, "body_required", "The draft version is required.", field: "version");
        var tenantId = caller.TenantId;
        var probe = await _db.CompanySetupDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == draftId, ct);
        if (probe is null) return CompanySetupOutcome<SectionApplyResult>.Fail(404, "draft_not_found", "Draft not found.", "المسودة غير موجودة.");
        var draftTarget = ReadBody(probe)?.ExistingCompanyId;
        // A5, the same caller rules as the commit: a company super admin, for their own company's existing-mode draft only.
        if (!caller.IsGroupScope)
        {
            if (draftTarget is null)
                return CompanySetupOutcome<SectionApplyResult>.Fail(403, CompanySetupGates.GroupScopeRequiredCode, CompanySetupGates.GroupScopeRequiredMessage,
                    CompanySetupGates.GroupScopeRequiredMessageAr, step: 1);
            if (await Planner.ScopeRefusalAsync(caller, draftTarget.Value, null, ct) is { } scope)
                return CompanySetupOutcome<SectionApplyResult>.Fail(scope.Status, scope.Code, scope.Message, scope.MessageAr, "existingCompanyId", 1);
        }
        if (draftTarget is not Guid companyId)
            return CompanySetupOutcome<SectionApplyResult>.Fail(409, "section_apply_needs_company",
                "A new company's rows are saved when you create the company. Live section saves are for an existing company.",
                "تُحفظ بيانات الشركة الجديدة عند إنشائها. الحفظ المباشر للأقسام متاح لشركة قائمة فقط.", "existingCompanyId", 1);

        var target = companyId;
        CompanySetupOutcome<SectionApplyResult>? refusal = null;
        SectionApplyResult? result = null;
        async Task RunAsync()
        {
            _db.ChangeTracker.Clear();
            refusal = null; result = null;
            await SetupTenantLock.AcquireAsync(_db, tenantId, ct);
            var draft = await _db.CompanySetupDrafts.SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == draftId, ct);
            if (draft is null) { refusal = CompanySetupOutcome<SectionApplyResult>.Fail(404, "draft_not_found", "Draft not found.", "المسودة غير موجودة."); return; }
            if (draft.Status != CompanySetupDraftStatuses.Open)
            {
                refusal = CompanySetupOutcome<SectionApplyResult>.Fail(409, "draft_not_open",
                    $"This draft is {draft.Status.ToLowerInvariant()} and can no longer be edited.", "لم تعد هذه المسودة قابلة للتعديل.");
                return;
            }
            if (draft.Version != request.Version)
            {
                // A blind retry of a save that landed: the draft moved on by exactly this save.
                if (draft.Version == request.Version + 1 && await SectionMarkerAsync(draft, section!, request.Version, ct) is { } done)
                {
                    result = new SectionApplyResult((await ToDtoAsync(draft, ct))!, section!, done.Counts.Created, done.Counts.Updated, done.Counts.Unchanged, Replayed: true);
                    return;
                }
                refusal = new(409, default, new CompanySetupError("draft_version_stale",
                    "This draft was saved somewhere else (another tab or device). Reload it to continue from the latest version.",
                    "تم حفظ هذه المسودة في مكان آخر. أعد تحميلها للمتابعة من أحدث نسخة.", "version", 4) { CurrentVersion = draft.Version });
                return;
            }
            var body = ReadBody(draft);
            if (body is null) { refusal = CompanySetupOutcome<SectionApplyResult>.Fail(422, "draft_unreadable", "This draft cannot be read. Reload the company's setup."); return; }
            var (plan, model) = await Planner.PlanAsync(caller, body, ct, focus: section);
            if (!plan.Ok || model is null) { refusal = RefuseSection(plan, section!); return; }

            var ctx = caller.Context with { TenantId = tenantId };
            switch (section)
            {
                case V.Sections.CostCentres:
                    await WriteCostCentresAsync(tenantId, target, model, ctx, ct);
                    break;
                case V.Sections.Grades:
                    await WriteGradesAsync(tenantId, target, model, ctx, ct);
                    break;
                case V.Sections.Departments:
                    await WriteDepartmentsAsync(tenantId, target, model, model.ExistingBranchIds, model.ExistingCostCentreIds, ctx, ct);
                    break;
                case V.Sections.Designations:
                    await WriteDesignationsAsync(tenantId, target, model, model.ExistingDepartmentIds, model.ExistingGradeIds, ctx, ct);
                    break;
            }
            var counts = SectionCountsOf(model, section!);

            // Rebase in place, from what is in the database now.
            var rebased = await RebaseSectionAsync(tenantId, target, body, section!, ct);
            var json = JsonSerializer.Serialize(rebased, BodyJson);
            if (Encoding.UTF8.GetByteCount(json) > CompanySetupPlanner.MaxBodyBytes)
            {
                refusal = CompanySetupOutcome<SectionApplyResult>.Fail(413, "draft_too_large", "The draft is larger than 128 KiB.");
                return;
            }
            // A writer that clears its change tracker for its own retries must not drop the draft this save rebases.
            if (_db.Entry(draft).State == EntityState.Detached) _db.CompanySetupDrafts.Attach(draft);
            var fromVersion = draft.Version;
            var now = DateTime.UtcNow;
            draft.Body = json;
            draft.Version = fromVersion + 1;
            draft.Step = 4;
            draft.UpdatedAtUtc = now;
            var marker = AuthAuditEntry.Create(Guid.NewGuid(), now, SectionAppliedAction, nameof(CompanySetupDraftRecord), draft.Id.ToString(), ctx,
                JsonSerializer.Serialize(new SectionAppliedMarker(draft.Id, section!, fromVersion, counts), BodyJson));
            marker.CompanyId = target;
            _db.AuditLogs.Add(marker);
            await _db.SaveChangesAsync(ct);
            result = new SectionApplyResult((await ToDtoAsync(draft, ct))!, section!, counts.Created, counts.Updated, counts.Unchanged, Replayed: false);
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

        return refusal ?? CompanySetupOutcome<SectionApplyResult>.Ok(result!);
    }

    private async Task<SectionAppliedMarker?> SectionMarkerAsync(CompanySetupDraftRecord draft, string section, int fromVersion, CancellationToken ct)
    {
        var rows = await ScopedBypass.NullableTenantWide(_db.AuditLogs, draft.TenantId,
                "Replay of a live company-setup section save: its own marker, found by the draft id.")
            .AsNoTracking()
            .Where(a => a.Action == SectionAppliedAction && a.EntityId == draft.Id.ToString())
            .Select(a => a.Metadata).ToListAsync(ct);
        foreach (var json in rows)
        {
            if (json is null) continue;
            SectionAppliedMarker? marker;
            try { marker = JsonSerializer.Deserialize<SectionAppliedMarker>(json, BodyJson); }
            catch (JsonException) { continue; }
            if (marker is not null && marker.Section == section && marker.FromVersion == fromVersion) return marker;
        }
        return null;
    }

    /// <summary>The section's body rows by what the save did to them (rows the body does not list are not counted).</summary>
    private static SectionCounts SectionCountsOf(CompanySetupPlanModel m, string section)
    {
        IEnumerable<(bool New, bool Changed)> rows = section switch
        {
            V.Sections.CostCentres => m.CostCentres.Select(x => (x.Existing is null, x.Changed)),
            V.Sections.Departments => m.Departments.Select(x => (x.Existing is null, x.Changed)),
            V.Sections.Grades => m.Grades.Select(x => (x.Existing is null, x.Changed)),
            _ => m.Designations.Select(x => (x.Existing is null, x.Changed)),
        };
        var list = rows.ToList();
        return new(list.Count(x => x.New), list.Count(x => !x.New && x.Changed), list.Count(x => !x.New && !x.Changed));
    }

    /// <summary>B2's rebase: the section's rows, in body order, become the saved rows — values, existingId and a fresh
    /// baseline, from the same builder the prefill uses. Every other part of the body is returned exactly as stored.</summary>
    private async Task<CompanySetupDraftBody> RebaseSectionAsync(Guid tenantId, Guid companyId, CompanySetupDraftBody body, string section, CancellationToken ct)
    {
        var org = await CompanySetupRows.CompanyRowsAsync(_db, tenantId, companyId, ct);
        var maps = await CompanySetupRows.CodeMapsAsync(_db, tenantId, ct);
        static Dictionary<string, T> ByCode<T>(IEnumerable<T> rows, Func<T, string> code) =>
            rows.GroupBy(x => OrgCodes.Normalize(code(x)), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        switch (section)
        {
            case V.Sections.CostCentres:
            {
                var saved = ByCode(org.CostCentres.Where(x => !x.IsDeleted), x => x.Code);
                return body with { CostCentres = (body.CostCentres ?? []).Select(x =>
                    saved.TryGetValue(OrgCodes.Normalize(x.Code), out var row) ? CompanySetupRows.CostCentre(row) : x).ToList() };
            }
            case V.Sections.Departments:
            {
                var saved = ByCode(org.Departments.Where(x => !x.IsDeleted), x => x.Code);
                return body with { Departments = (body.Departments ?? []).Select(x =>
                    saved.TryGetValue(OrgCodes.Normalize(x.Code), out var row) ? CompanySetupRows.Department(row, maps) : x).ToList() };
            }
            case V.Sections.Grades:
            {
                var saved = ByCode(org.Grades.Where(x => !x.IsDeleted), x => x.Code);
                return body with { Grades = (body.Grades ?? []).Select(x =>
                    saved.TryGetValue(OrgCodes.Normalize(x.Code), out var row) ? CompanySetupRows.Grade(row) : x).ToList() };
            }
            default:
            {
                var saved = ByCode(org.Designations.Where(x => !x.IsDeleted), x => x.Code);
                return body with { Designations = (body.Designations ?? []).Select(x =>
                    saved.TryGetValue(OrgCodes.Normalize(x.Code), out var row) ? CompanySetupRows.Designation(row, maps) : x).ToList() };
            }
        }
    }

    /// <summary>A refused section save: a conflict in the section is 409, any other blocking problem 422. The headline is
    /// the first blocking problem (a step-1 gate first); the plan rides along.</summary>
    private static CompanySetupOutcome<SectionApplyResult> RefuseSection(CompanySetupPlan plan, string section)
    {
        var blocking = plan.Problems.Where(p => CompanySetupPlanner.Blocks(p, section)).ToList();
        var headline = blocking.FirstOrDefault(p => p.Step == 1) ?? blocking.FirstOrDefault();
        var area = V.Sections.AreaOf(section);
        var status = headline?.Step == 1
            ? headline.Code switch
            {
                CompanySetupGates.CompanyNotManageableCode or CompanySetupGates.CompanyAdminRequiredCode => 403,
                "company_not_found" => 404,
                CompanySetupBaseline.RefreshCode => 409,
                _ => 409,
            }
            : plan.Items.Any(i => i.Area == area && i.Action == V.ActionConflict) ? 409 : 422;
        // The plan rides along limited to what blocks this save: the section's rows, its problems (plus a step-1 gate,
        // which blocks everything) and its warnings, with the dry run's field paths.
        var sectionPlan = new CompanySetupPlan(false, plan.Items.Where(i => i.Area == area).ToList(), blocking,
            plan.Warnings.Where(p => p.Field == section || p.Field.StartsWith(section + ".", StringComparison.Ordinal)).ToList());
        return CompanySetupOutcome<SectionApplyResult>.Fail(status, headline?.Code ?? "plan_conflict",
            headline?.Message ?? "Nothing was saved. Fix the highlighted rows and try again.", headline?.MessageAr, headline?.Field, headline?.Step, sectionPlan);
    }

    // ── GET /copy-rows (Addendum B3) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Addendum B3: one section's active rows of a sister company as NEW draft rows (no existingId, no baseline), to copy
    /// by value. Group scope only (the controller), and the source must be an active company of this tenant (404).
    /// Links are kept by code: a department's parent, branch and cost centre, a designation's department and grade. Every
    /// organisation code is per company, so the same codes copied over match once that section is copied and saved; a
    /// link to a row not saved yet is <c>reference_not_saved</c> at the section save.
    /// </summary>
    public async Task<CompanySetupOutcome<CopyRows>> CopyRowsAsync(CompanySetupCaller caller, Guid? fromCompanyId, string? section, CancellationToken ct)
    {
        if (!V.Sections.IsValid(section))
            return CompanySetupOutcome<CopyRows>.Fail(400, "section_invalid", $"Choose one of the sections {string.Join(", ", V.Sections.All)}.",
                "اختر قسماً صحيحاً من أقسام الهيكل التنظيمي.", "section");
        if (!caller.IsGroupScope)
            return CompanySetupOutcome<CopyRows>.Fail(403, CompanySetupGates.GroupScopeRequiredCode, CompanySetupGates.GroupScopeRequiredMessage,
                CompanySetupGates.GroupScopeRequiredMessageAr, step: 1);
        var sister = fromCompanyId is Guid id
            ? await ScopedBypass.TenantWide(_db.Companies, caller.TenantId, Why).AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted && c.IsActive && c.ApprovalStatus == CompanyApprovalStatuses.Active, ct)
            : null;
        if (sister is null)
            return CompanySetupOutcome<CopyRows>.Fail(404, "copy_source_not_found", "Choose an active company of this workspace to copy from.",
                "اختر شركة نشطة في مساحة العمل للنسخ منها.", "fromCompanyId");
        var rows = await SisterRowsAsync(caller.TenantId, sister.Id, ct);
        var result = new CopyRows(section!, sister.Id);
        switch (section)
        {
            case V.Sections.CostCentres:
                return CompanySetupOutcome<CopyRows>.Ok(result with { CostCentres = rows.CostCentres });
            case V.Sections.Departments:
                return CompanySetupOutcome<CopyRows>.Ok(result with { Departments = rows.Departments });
            case V.Sections.Grades:
                return CompanySetupOutcome<CopyRows>.Ok(result with { Grades = rows.Grades });
            default:
                return CompanySetupOutcome<CopyRows>.Ok(result with { Designations = rows.Designations });
        }
    }

    private sealed record SisterRows(List<CompanySetupCostCentre> CostCentres, List<CompanySetupDepartment> Departments,
        List<CompanySetupDesignation> Designations, List<CompanySetupGrade> Grades);

    /// <summary>A sister company's active organisation rows as new draft rows, links by the sister's own codes. A link to a
    /// row that is not copied (inactive or removed) is dropped, so a copy never points at nothing.</summary>
    private async Task<SisterRows> SisterRowsAsync(Guid tenantId, Guid sisterId, CancellationToken ct)
    {
        var org = await CompanySetupRows.CompanyRowsAsync(_db, tenantId, sisterId, ct);
        var maps = await CompanySetupRows.CodeMapsAsync(_db, tenantId, ct);
        var costCentres = org.CostCentres.Where(x => !x.IsDeleted && x.IsActive).Select(x => CompanySetupRows.AsNew(CompanySetupRows.CostCentre(x))).ToList();
        var departments = org.Departments.Where(x => !x.IsDeleted && x.IsActive).Select(x => CompanySetupRows.AsNew(CompanySetupRows.Department(x, maps))).ToList();
        var grades = org.Grades.Where(x => !x.IsDeleted && x.IsActive).Select(x => CompanySetupRows.AsNew(CompanySetupRows.Grade(x))).ToList();
        var designations = org.Designations.Where(x => !x.IsDeleted && x.IsActive).Select(x => CompanySetupRows.AsNew(CompanySetupRows.Designation(x, maps))).ToList();
        var departmentCodes = departments.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);
        var gradeCodes = grades.Select(g => g.Code).ToHashSet(StringComparer.Ordinal);
        var costCentreCodes = costCentres.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        departments = departments.Select(d => d with
        {
            ParentCode = d.ParentCode is { } p && departmentCodes.Contains(p) ? p : null,
            CostCentreCode = d.CostCentreCode is { } x && costCentreCodes.Contains(x) ? x : null,
        }).ToList();
        designations = designations.Select(d => d with
        {
            DepartmentCode = d.DepartmentCode is { } x && departmentCodes.Contains(x) ? x : null,
            GradeCode = d.GradeCode is { } g && gradeCodes.Contains(g) ? g : null,
        }).ToList();
        return new SisterRows(costCentres, departments, designations, grades);
    }

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
            "company_creation_platform_controlled" or CompanySetupGates.GroupScopeRequiredCode => 403,
            "account_type_single_company" or CompanySetupGates.PlatformActivationDisabledCode or "company_switcher_selected" => 409,
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
            companyId = (await _organization.CreateCompanyAsync(tenantId, m.CompanyRequest, ctx, ct, asDraft: false)).Id;
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

        // Step 4 through the same writers a live section save uses (Addendum B2): one writer per section, no copies.
        var costCentreIds = await WriteCostCentresAsync(tenantId, companyId, m, ctx, ct);
        var gradeIds = await WriteGradesAsync(tenantId, companyId, m, ctx, ct);
        var departmentIds = await WriteDepartmentsAsync(tenantId, companyId, m, branchIds, costCentreIds, ctx, ct);
        await WriteDesignationsAsync(tenantId, companyId, m, departmentIds, gradeIds, ctx, ct);

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
            // Per company (owner decision 2026-10-23): the statutory leave types and policies are installed FOR this company.
            await TenantProvisioningBundle.InstallDefaultLeaveForCountryAsync(_db, tenantId, companyId, m.Country, ct);
            foreach (var policy in _db.ChangeTracker.Entries<LeavePolicy>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList())
                Audit("leave.policy_created", nameof(LeavePolicy), policy.Id, companyId, new { policy.Name, policy.CountryCode, companyId });
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

    // ── Step 4 writers (the commit and the live section save) ─────────────────────────────────────────
    // Every row goes through IOrganizationSetupService, so it keeps that service's validation and audit, and is written
    // for THIS company (Addendum B: CompanyId on every request). An existing row the plan leaves unchanged is not touched;
    // an edit carries the row's own values for every field the wizard does not show. Each returns code → id of the
    // section's rows (the company's existing ones included) for the links of the sections after it.

    private async Task<Dictionary<string, Guid>> WriteCostCentresAsync(Guid tenantId, Guid companyId, CompanySetupPlanModel m, RequestContext ctx, CancellationToken ct)
    {
        var ids = new Dictionary<string, Guid>(m.ExistingCostCentreIds, StringComparer.Ordinal);
        foreach (var pc in m.CostCentres)
        {
            var cc = pc.Body;
            var nameAr = string.IsNullOrWhiteSpace(cc.NameAr) ? null : cc.NameAr.Trim();
            if (pc.Existing is { } row)
            {
                ids[cc.Code] = row.Id;
                if (pc.Changed)
                    await _organization.UpdateCostCenterAsync(tenantId, row.Id, new CostCenterRequest(companyId, row.Code, cc.NameEn.Trim(), row.IsActive, nameAr), ctx, ct);
                continue;
            }
            ids[cc.Code] = (await _organization.CreateCostCenterAsync(tenantId, new CostCenterRequest(companyId, cc.Code, cc.NameEn.Trim(), true, nameAr), ctx, ct)).Id;
        }
        return ids;
    }

    private async Task<Dictionary<string, Guid>> WriteGradesAsync(Guid tenantId, Guid companyId, CompanySetupPlanModel m, RequestContext ctx, CancellationToken ct)
    {
        var ids = new Dictionary<string, Guid>(m.ExistingGradeIds, StringComparer.Ordinal);
        foreach (var pg in m.Grades)
        {
            var g = pg.Body;
            var nameAr = string.IsNullOrWhiteSpace(g.NameAr) ? null : g.NameAr.Trim();
            if (pg.Existing is { } row)
            {
                ids[g.Code] = row.Id;
                // An existing band keeps its own currency and band label; the wizard edits names, level and amounts.
                if (pg.Changed)
                    await _organization.UpdateGradeAsync(tenantId, row.Id, new GradeRequest(row.Code, g.NameEn.Trim(), row.Band, g.Level,
                        g.MinSalary, g.MidSalary, g.MaxSalary, row.Currency, row.IsActive, nameAr, companyId), ctx, ct);
                continue;
            }
            // Addendum B: a new grade's band is in the company's own currency.
            ids[g.Code] = (await _organization.CreateGradeAsync(tenantId, new GradeRequest(g.Code, g.NameEn.Trim(), null, g.Level,
                g.MinSalary, g.MidSalary, g.MaxSalary, m.Currency, true, nameAr, companyId), ctx, ct)).Id;
        }
        return ids;
    }

    private async Task<Dictionary<string, Guid>> WriteDepartmentsAsync(Guid tenantId, Guid companyId, CompanySetupPlanModel m,
        IReadOnlyDictionary<string, Guid> branchIds, IReadOnlyDictionary<string, Guid> costCentreIds, RequestContext ctx, CancellationToken ct)
    {
        var ids = new Dictionary<string, Guid>(m.ExistingDepartmentIds, StringComparer.Ordinal);
        // The plan orders new departments parents first, then edits of existing ones (whose new parent may be new).
        foreach (var pd in m.Departments)
        {
            var d = pd.Body;
            if (pd.Existing is { } row && !pd.Changed) { ids[d.Code] = row.Id; continue; }
            Guid? parent = d.ParentCode is { } p && ids.TryGetValue(p, out var pid) ? pid : null;
            Guid? branch = d.BranchCode is { } bc && branchIds.TryGetValue(bc, out var bid) ? bid : null;
            Guid? cost = d.CostCentreCode is { } cc && costCentreIds.TryGetValue(cc, out var cid) ? cid : null;
            var nameAr = string.IsNullOrWhiteSpace(d.NameAr) ? null : d.NameAr.Trim();
            if (pd.Existing is { } existing)
            {
                ids[d.Code] = existing.Id;
                await _organization.UpdateDepartmentAsync(tenantId, existing.Id, new DepartmentRequest(branch, parent, cost, existing.Code,
                    d.NameEn.Trim(), nameAr, existing.ManagerEmployeeId, existing.IsActive, companyId), ctx, ct);
                continue;
            }
            ids[d.Code] = (await _organization.CreateDepartmentAsync(tenantId, new DepartmentRequest(branch, parent, cost, d.Code,
                d.NameEn.Trim(), nameAr, null, true, companyId), ctx, ct)).Id;
        }
        return ids;
    }

    private async Task WriteDesignationsAsync(Guid tenantId, Guid companyId, CompanySetupPlanModel m,
        IReadOnlyDictionary<string, Guid> departmentIds, IReadOnlyDictionary<string, Guid> gradeIds, RequestContext ctx, CancellationToken ct)
    {
        foreach (var pd in m.Designations)
        {
            var ds = pd.Body;
            if (pd.Existing is { } row && !pd.Changed) continue;
            Guid? dept = ds.DepartmentCode is { } dc && departmentIds.TryGetValue(dc, out var did) ? did : null;
            Guid? grade = ds.GradeCode is { } gc && gradeIds.TryGetValue(gc, out var gid) ? gid : null;
            var titleAr = string.IsNullOrWhiteSpace(ds.TitleAr) ? null : ds.TitleAr.Trim();
            if (pd.Existing is { } existing)
            {
                // The free-text job grade follows the grade link when it moves; level, description and manager flag are kept.
                var jobGrade = grade == existing.GradeId ? existing.JobGrade : ds.GradeCode;
                await _organization.UpdateDesignationAsync(tenantId, existing.Id, new DesignationRequest(dept, existing.Code, ds.TitleEn.Trim(), titleAr,
                    jobGrade, grade, existing.JobLevel, existing.JobDescription, existing.IsManagerRole, existing.IsActive, companyId), ctx, ct);
                continue;
            }
            await _organization.CreateDesignationAsync(tenantId, new DesignationRequest(dept, ds.Code, ds.TitleEn.Trim(), titleAr,
                ds.GradeCode, grade, null, null, false, true, companyId), ctx, ct);
        }
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
                if (await UserSeatLimit.CheckAsync(_db, caller.TenantId, ct) is { } seats) { failure = seats.Message; code = UserSeatLimit.Refusal.Code; }
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
        return await CompanySetupReadiness.ComputeAsync(_db, _email, tenantId, company, ct, _config);
    }
}
