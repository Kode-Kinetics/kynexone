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
/// <param name="CompanyIds">The companies this request may write: null = every company (group scope, not narrowed);
/// otherwise the switcher's selection or the caller's own company grants. Existing-company mode (Addendum A5) may
/// only target one of these.</param>
public sealed record CompanySetupCaller(Guid TenantId, Guid UserId, bool IsGroupScope, bool IsNarrowedBySwitcher, RequestContext Context,
    IReadOnlyCollection<Guid>? CompanyIds = null);

/// <summary>A branch row of the plan: new (<see cref="Existing"/> null) or an existing row, changed or not.</summary>
public sealed record PlannedBranch(CompanySetupBranch Body, Branch? Existing, bool Changed, List<PlannedLocation> Locations);
/// <summary>A location row of the plan: new, or an existing row of the same branch, changed or not.</summary>
public sealed record PlannedLocation(CompanySetupLocation Body, Location? Existing, bool Changed);
/// <summary>A cost-centre row of the plan: new, or an existing row of this company, changed or not.</summary>
public sealed record PlannedCostCentre(CompanySetupCostCentre Body, CostCenter? Existing, bool Changed);
/// <summary>A department of the plan (Addendum B: this company's own): new, or an existing row, changed or not. Codes and
/// links are normalized; a null link is "none".</summary>
public sealed record PlannedDepartment(CompanySetupDepartment Body, Department? Existing, bool Changed);
public sealed record PlannedDesignation(CompanySetupDesignation Body, Designation? Existing, bool Changed);
public sealed record PlannedGrade(CompanySetupGrade Body, Grade? Existing, bool Changed);

/// <summary>What the commit will do, resolved by the planner. The commit executes exactly this, nothing more.</summary>
public sealed class CompanySetupPlanModel
{
    public required string Country { get; init; }
    public required string Currency { get; init; }
    public required CompanyRequest CompanyRequest { get; init; }
    /// <summary>Addendum A1: the company being completed; null = create mode.</summary>
    public Guid? ExistingCompanyId { get; init; }
    /// <summary>Existing mode: whether the company row changes (it is then updated through UpdateCompanyAsync).</summary>
    public bool CompanyChanged { get; init; }
    /// <summary>Every branch the commit touches or creates, in body order (create mode: all new).</summary>
    public required List<PlannedBranch> Branches { get; init; }
    /// <summary>Existing mode: the company's branches by code (for departments' links), including untouched ones.</summary>
    public Dictionary<string, Guid> ExistingBranchIds { get; init; } = new(StringComparer.Ordinal);
    public required List<PlannedCostCentre> CostCentres { get; init; }
    public Dictionary<string, Guid> ExistingCostCentreIds { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Every department the commit touches or creates: new ones parents first, then edits of existing ones.</summary>
    public required List<PlannedDepartment> Departments { get; init; }
    /// <summary>The company's own departments by code (removed ones excluded), for links.</summary>
    public required Dictionary<string, Guid> ExistingDepartmentIds { get; init; }
    public required List<PlannedDesignation> Designations { get; init; }
    public required List<PlannedGrade> Grades { get; init; }
    public required Dictionary<string, Guid> ExistingGradeIds { get; init; }
    public required DraftEmployeeIdRule EmployeeIdRule { get; init; }
    /// <summary>create | update | unchanged.</summary>
    public string EmployeeIdRuleAction { get; init; } = V.ActionCreate;
    public required DateOnly CutoverDate { get; init; }
    /// <summary>create | update | unchanged. Update only while the existing cutover is Planned (Addendum A4).</summary>
    public string CutoverAction { get; init; } = V.ActionCreate;
    public Guid? ExistingCutoverId { get; init; }
    public DraftHolidayCalendar? HolidayCalendar { get; init; }
    public required IReadOnlySet<string> MissingLeaveDefaults { get; init; }
    /// <summary>Non-null when the tenant-default GCC row for <see cref="Country"/> must be created (Decision 11).</summary>
    public GCCComplianceSetting? NewGccDefault { get; init; }
    /// <summary>Non-null when the user changed the working week: the company override row to write.</summary>
    public GCCComplianceSetting? WorkWeekOverride { get; init; }
    /// <summary>Existing mode: the company's own override row whose weekend changes, and the new weekend.</summary>
    public (Guid Id, string WeekendDays, string WorkWeek)? WorkWeekUpdate { get; init; }
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
/// <para>One planner, both modes (Addendum A). With <c>existingCompanyId</c> it plans the completion of that company:
/// existing rows are <c>update</c> (changed fields in <c>reason</c>) or <c>unchanged</c>, new rows <c>create</c>,
/// workspace-wide rows (country settings, default leave) <c>reuse</c>; nothing is ever deleted, and the country and
/// currency are locked. Departments, designations and grades are the company's own (Addendum B), like cost centres.</para>
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
        /// <summary>A plan row. Its reason always carries Arabic too: <paramref name="reasonAr"/>, or the Arabic of the
        /// planner's own English (<see cref="CompanySetupReasons"/>).</summary>
        public void Item(string area, string code, string label, string action, string? reason = null, string? labelAr = null, string? reasonAr = null) =>
            Items.Add(new(area, code, label, action, reason, Blank(labelAr)) { ReasonAr = reason is null ? null : reasonAr ?? CompanySetupReasons.Arabic(reason) });
        public void Problem(int step, string field, string code, string message, string? ar = null) => Problems.Add(new(step, Path(step, field), code, message, ar));
        /// <summary>A9: a field changed both here and elsewhere since the draft started; carries the value there now.</summary>
        public void Changed(int step, string field, string message, string ar, string? current) =>
            Problems.Add(new(step, Path(step, field), CompanySetupBaseline.ChangedCode, message, ar) { Current = current });
        public void Warn(int step, string field, string code, string message, string? ar = null) => Warnings.Add(new(step, Path(step, field), code, message, ar));
        /// <summary>A conflict is both a plan row and a field problem, so the review and the step both show it.</summary>
        public void Conflict(string area, string itemCode, string label, int step, string field, string code, string message, string? ar = null, string? labelAr = null)
        {
            Items.Add(new(area, itemCode, label, V.ActionConflict, message, Blank(labelAr)) { ReasonAr = ar ?? CompanySetupReasons.Arabic(message) });
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

    /// <summary>Why <paramref name="caller"/> may not work on existing company <paramref name="companyId"/>; null = allowed.</summary>
    public sealed record TargetRefusal(int Status, string Code, string Message, string? MessageAr);

    /// <summary>
    /// Addendum A5, the scope half: a group-scope caller may complete any company (the one the switcher selects, when it
    /// narrows the request); a company-scoped caller only a company of their own grants, and only as a company super
    /// admin (the Admin role, read from the database the way the writers read it). Everyone else is refused — a
    /// company-scoped HR Manager or a custom role holding <c>organization.setup.apply</c> included.
    /// </summary>
    public async Task<TargetRefusal?> ScopeRefusalAsync(CompanySetupCaller caller, Guid companyId, AccessCeilingDto? ceiling, CancellationToken ct)
    {
        if (!caller.IsGroupScope)
        {
            if (caller.CompanyIds is null || !caller.CompanyIds.Contains(companyId))
                return new(403, CompanySetupGates.CompanyNotManageableCode,
                    "You can complete the setup of your own company only.", "يمكنك إكمال إعداد شركتك فقط.");
            ceiling ??= await TryCeilingAsync(caller, ct);
            if (ceiling is not { IsAdmin: true })
                return new(403, CompanySetupGates.CompanyAdminRequiredCode,
                    "Completing a company's setup needs its company super admin (the Admin role for this company) or a group administrator.",
                    "إكمال إعداد الشركة يتطلب مسؤول الشركة الأعلى أو مسؤول المجموعة.");
        }
        else if (caller.CompanyIds is not null && !caller.CompanyIds.Contains(companyId))
            return new(409, "company_switcher_selected",
                "Another company is selected in the company switcher. Select this company, or all companies, to complete its setup.",
                "تم اختيار شركة أخرى في مبدّل الشركات. اختر هذه الشركة أو جميع الشركات.");
        return null;
    }

    /// <summary>Addendum A5 + A6: the scope rule, then the company itself — in this tenant, not deleted, and Active
    /// (a Draft or inactive company is refused; the existing activation gap still applies).</summary>
    public async Task<TargetRefusal?> AuthorizeExistingAsync(CompanySetupCaller caller, Guid companyId, AccessCeilingDto? ceiling, CancellationToken ct)
    {
        if (await ScopeRefusalAsync(caller, companyId, ceiling, ct) is { } refused) return refused;
        var company = await ScopedBypass.TenantWide(_db.Companies, caller.TenantId, Why).AsNoTracking()
            .Where(x => x.Id == companyId && !x.IsDeleted).Select(x => new { x.IsActive, x.ApprovalStatus }).FirstOrDefaultAsync(ct);
        if (company is null) return new(404, "company_not_found", "Company not found.", "الشركة غير موجودة.");
        if (!company.IsActive || company.ApprovalStatus != CompanyApprovalStatuses.Active)
            return new(409, "company_not_active",
                "This company is not active yet (it may be waiting for activation). Its setup can be completed once it is active.",
                "هذه الشركة غير مفعلة بعد. يمكن إكمال إعدادها بعد تفعيلها.");
        return null;
    }

    private async Task<AccessCeilingDto?> TryCeilingAsync(CompanySetupCaller caller, CancellationToken ct)
    {
        try { return await _access.GetAccessCeilingAsync(caller.TenantId, caller.Context with { TenantId = caller.TenantId }, ct); }
        catch (PrivilegeCeilingException) { return null; }
    }

    /// <summary>What an existing company has today, read once per plan (Addendum A2/A3).</summary>
    private sealed class ExistingCompany
    {
        public required Company Company { get; init; }
        public required List<Branch> Branches { get; init; }
        /// <summary>Every branch code of the company, removed ones included: (tenant, company, code) is unique.</summary>
        public required HashSet<string> AllBranchCodes { get; init; }
        public required List<Location> Locations { get; init; }
        public required List<CostCenter> CostCentres { get; init; }
        /// <summary>Addendum B: the company's own organisation rows (removed ones included: they still hold their code).</summary>
        public required CompanySetupRows.CompanyRows Org { get; init; }
        /// <summary>Id → code of everything a department or designation can link to (A9 projects links as codes).</summary>
        public required CompanySetupRows.CodeMaps Maps { get; init; }
        public EmployeeIdRule? Rule { get; init; }
        public CompanyCutover? Cutover { get; init; }
        /// <summary>The working week as the prefill shows it today (A9's database side of the work-week block).</summary>
        public required (string WeekendDays, bool Own) WorkWeek { get; init; }
        public required string PackTimeZone { get; init; }
    }

    /// <summary>The working week an existing company's prefill shows: the weekend that applies to it, and whether it has
    /// its own row. One reading for the prefill and the planner (A9).</summary>
    public async Task<(string WeekendDays, bool Own)> CurrentWorkWeekAsync(Guid tenantId, Guid companyId, string country, CancellationToken ct)
    {
        var week = await _workWeek.ResolveAsync(tenantId, companyId, country, ct);
        var own = await ScopedBypass.TenantWide(_db.GCCComplianceSettings, tenantId, Why).AsNoTracking()
            .AnyAsync(x => x.CompanyId == companyId && x.CountryCode == country, ct);
        return (CompanySetupPack.FormatWeekend(week.WeekendDays), own);
    }

    private async Task<ExistingCompany> LoadExistingAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var company = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking().SingleAsync(x => x.Id == companyId, ct);
        var allBranches = await ScopedBypass.TenantWide(_db.Branches, tenantId, Why).AsNoTracking()
            .Where(b => b.CompanyId == companyId).OrderByDescending(b => b.IsHeadOffice).ThenBy(b => b.Code).ToListAsync(ct);
        var branchIds = allBranches.Where(b => !b.IsDeleted).Select(b => (Guid?)b.Id).ToList();
        var country = CountryCodeStandard.NormalizeToIso2(company.CountryCode) ?? Clean(company.CountryCode).ToUpperInvariant();
        var org = await CompanySetupRows.CompanyRowsAsync(_db, tenantId, companyId, ct);
        return new ExistingCompany
        {
            Org = org,
            Maps = await CompanySetupRows.CodeMapsAsync(_db, tenantId, ct),
            Company = company,
            WorkWeek = await CurrentWorkWeekAsync(tenantId, companyId, country, ct),
            PackTimeZone = HomeJurisdiction.TimeZoneFor(country),
            Branches = allBranches.Where(b => !b.IsDeleted).ToList(),
            AllBranchCodes = allBranches.Select(b => OrgCodes.Normalize(b.Code)).ToHashSet(StringComparer.Ordinal),
            Locations = await ScopedBypass.TenantWide(_db.Locations, tenantId, Why).AsNoTracking()
                .Where(l => !l.IsDeleted && branchIds.Contains(l.BranchId)).OrderBy(l => l.Code).ToListAsync(ct),
            CostCentres = org.CostCentres.Where(x => !x.IsDeleted).ToList(),
            Rule = await ScopedBypass.TenantWide(_db.EmployeeIdRules, tenantId, Why).AsNoTracking()
                .FirstOrDefaultAsync(r => r.CompanyId == companyId && !r.IsDeleted, ct),
            Cutover = await ScopedBypass.TenantWide(_db.CompanyCutovers, tenantId, Why).AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId, ct),
        };
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    private static bool Differs(string? a, string? b) => !string.Equals(Clean(a), Clean(b), StringComparison.Ordinal);

    /// <param name="focus">Addendum B2, a live section save: one of <see cref="V.Sections"/>. Only problems in that section
    /// and the step-1 gates block, and a link into another section must already be saved (<c>reference_not_saved</c>).
    /// The plan still lists every row and problem, so the screen shows them; the model is returned when the section
    /// may be written. Null = the whole commit.</param>
    public async Task<(CompanySetupPlan Plan, CompanySetupPlanModel? Model)> PlanAsync(
        CompanySetupCaller caller, CompanySetupDraftBody? body, CancellationToken ct, string? focus = null)
    {
        var c = new Collector();
        var tenantId = caller.TenantId;
        if (body is null || body.Company is null)
        {
            c.Problem(2, "company", "body_required", "The company details are missing.", "بيانات الشركة مفقودة.");
            return (Finish(c, focus), null);
        }

        // The caller's effective permissions, read the way the writers read them (database, not claims).
        var ceiling = await TryCeilingAsync(caller, ct);
        bool Holds(string permission) => ceiling?.HeldPermissions.Contains(permission, StringComparer.Ordinal) == true;

        // ── Step 1: who may run it, for which company (Decision 12 for a new company; Addendum A5 for an existing one) ──
        ExistingCompany? ex = null;
        if (body.ExistingCompanyId is Guid existingId)
        {
            if (await AuthorizeExistingAsync(caller, existingId, ceiling, ct) is { } refused)
            {
                c.Problem(1, "existingCompanyId", refused.Code, refused.Message, refused.MessageAr);
                return (Finish(c, focus), null);
            }
            ex = await LoadExistingAsync(tenantId, existingId, ct);
            // A9: draft vs baseline vs database, field by field, before anything else reads the body.
            var reconciled = Reconcile(c, body, ex);
            if (reconciled is null) return (Finish(c, focus), null);
            body = reconciled;
        }
        else
        {
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
        }
        var stored = ex?.Company;

        // ── Step 2: the company ────────────────────────────────────────────────────────────────────────
        var co = body.Company;
        var country = CountryCodeStandard.NormalizeToIso2(co.CountryCode) ?? string.Empty;
        var currency = (co.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (stored is not null)
        {
            // Addendum A4: country and currency of an existing company are read-only, always — payroll, GOSI and the
            // bank files depend on them. A different value is refused, never applied.
            var storedCountry = CountryCodeStandard.NormalizeToIso2(stored.CountryCode) ?? Clean(stored.CountryCode).ToUpperInvariant();
            var storedCurrency = Clean(stored.DefaultCurrency).ToUpperInvariant();
            if (!string.Equals(country, storedCountry, StringComparison.Ordinal))
                c.Problem(2, "countryCode", "country_locked",
                    $"The country of an existing company cannot be changed ({storedCountry}). Payroll, GOSI and the bank files depend on it.",
                    "لا يمكن تغيير دولة شركة قائمة.");
            if (!string.Equals(currency, storedCurrency, StringComparison.Ordinal))
                c.Problem(2, "currency", "currency_locked",
                    $"The currency of an existing company cannot be changed ({storedCurrency}). Payroll and the bank files depend on it.",
                    "لا يمكن تغيير عملة شركة قائمة.");
            country = storedCountry;
            currency = storedCurrency;
        }
        var isSa = country == "SA";
        var legalNameEn = (co.LegalNameEn ?? string.Empty).Trim();
        // Data entry: Arabic-Indic / Persian digits read as digits, and spaces (and dashes, where the format has none)
        // dropped, before anything is validated or compared (CompanySetupDataEntry).
        var registration = CompanySetupDataEntry.DigitsOnlyNumber(co.RegistrationNumber);
        var mol = CompanySetupDataEntry.EstablishmentId(co.MolEstablishmentNumber);
        var unified = CompanySetupDataEntry.DigitsOnlyNumber(co.UnifiedNationalNumber);
        var tax = CompanySetupDataEntry.DigitsOnlyNumber(co.TaxNumber);
        var emailDomain = CompanyEmailDomainRules.Normalize(co.EmailDomain);
        var erp = string.IsNullOrWhiteSpace(co.ErpCompanyCode) ? null : co.ErpCompanyCode.Trim();

        // Existing mode: a field is validated as on create when it CHANGES. A value saved before today's rules and left
        // as it is stays allowed (the company form's own rule), shown as a warning so nothing is guessed silently. A value
        // that differs from the stored one only in how it was typed is NOT a change: the stored text is kept as it is.
        bool nameArChanged = stored is null || Differs(co.LegalNameAr, stored.LegalNameAr);
        bool crChanged = stored is null || Differs(registration, CompanySetupDataEntry.DigitsOnlyNumber(stored.RegistrationNumber));
        bool molChanged = stored is null || Differs(mol, CompanySetupDataEntry.EstablishmentId(stored.WpsEmployerId));
        bool unifiedChanged = stored is null || Differs(unified, CompanySetupDataEntry.DigitsOnlyNumber(stored.UnifiedNationalNumber));
        bool taxChanged = stored is null || Differs(tax, CompanySetupDataEntry.DigitsOnlyNumber(stored.TaxNumber));
        if (stored is not null)
        {
            if (!crChanged) registration = Clean(stored.RegistrationNumber);
            if (!molChanged) mol = Clean(stored.WpsEmployerId);
            if (!unifiedChanged) unified = Clean(stored.UnifiedNationalNumber);
            if (!taxChanged) tax = Clean(stored.TaxNumber);
        }
        bool domainChanged = stored is null || !string.Equals(emailDomain, CompanyEmailDomainRules.Normalize(stored.EmailDomain), StringComparison.Ordinal);
        bool erpChanged = stored is null || Differs(erp, stored.ErpCompanyCode);
        void Check(bool changed, string field, string code, string message, string? ar = null)
        {
            if (changed) c.Problem(2, field, code, message, ar);
            else c.Warn(2, field, code, $"{message} (saved before; not changed here)", ar);
        }

        if (legalNameEn.Length == 0) c.Problem(2, "legalNameEn", "required", "Enter the legal name in English.", "أدخل الاسم القانوني بالإنجليزية.");
        if (isSa && string.IsNullOrWhiteSpace(co.LegalNameAr)) Check(nameArChanged, "legalNameAr", "required", "Enter the legal name in Arabic.", "أدخل الاسم القانوني بالعربية.");
        if (stored is null)
        {
            if (country.Length == 0) c.Problem(2, "countryCode", "country_invalid", CompanySetupDataEntry.CountryInvalidMessage, CompanySetupDataEntry.CountryInvalidMessageAr);
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) c.Problem(2, "currency", "currency_invalid", "Choose the company's currency (a 3-letter code).");
            else if (isSa && currency != "SAR") c.Problem(2, "currency", "currency_must_be_sar", "A Saudi company pays in SAR.", "الشركة السعودية تدفع بالريال السعودي.");
        }
        if (registration.Length == 0) c.Problem(2, "registrationNumber", "required", "Enter the commercial registration (CR) number.", "أدخل رقم السجل التجاري.");
        else if (isSa && (registration.Length != 10 || !registration.All(char.IsAsciiDigit)))
            Check(crChanged, "registrationNumber", "cr_number_invalid", "A Saudi CR number is 10 digits.", "رقم السجل التجاري السعودي 10 أرقام.");
        if (isSa)
        {
            var issues = new List<SaudiBankExportIssueDto>();
            KsaWageFileRules.ValidateEstablishmentId(mol.Length == 0 ? null : mol, issues);
            foreach (var issue in issues) Check(molChanged, "molEstablishmentNumber", issue.Code, issue.Message, CompanySetupDataEntry.ArabicFor(issue.Code));
            if (unified.Length > 0)
            {
                issues.Clear();
                KsaWageFileRules.ValidateNationalUnifiedNo(unified, required: false, issues);
                foreach (var issue in issues) Check(unifiedChanged, "unifiedNationalNumber", issue.Code, issue.Message, CompanySetupDataEntry.ArabicFor(issue.Code));
            }
            if (tax.Length > 0 && !CompanySetupDataEntry.IsSaudiVatNumber(tax))
                Check(taxChanged, "taxNumber", CompanySetupDataEntry.VatInvalidCode, CompanySetupDataEntry.VatInvalidMessage, CompanySetupDataEntry.VatInvalidMessageAr);
        }
        if (!OrganizationSetupService.IsValidEmailDomainOrEmpty(emailDomain))
            Check(domainChanged, "emailDomain", "email_domain_invalid", $"'{co.EmailDomain}' is not a valid email domain (for example acme.sa).");
        else if (emailDomain.Length > 0 && domainChanged)
        {
            try { await CompanyEmailDomainRules.EnsureClaimableAsync(_db, tenantId, emailDomain, ct); }
            catch (EmailDomainRefusedException ex2) { c.Problem(2, "emailDomain", ex2.Code, ex2.Message); }
        }
        if (!TryParseMonth(co.FirstPayrollMonth, out var cutoverDate))
            c.Problem(2, "firstPayrollMonth", "month_invalid", "Choose the first payroll month (YYYY-MM).", "اختر أول شهر للرواتب.");

        if (emailDomain.Length > 0 && domainChanged && !Holds(CompanyEmailDomainRules.SecurityPermission))
            c.Problem(2, "emailDomain", CompanyEmailDomainRules.NeedsSecurityAdminCode, CompanyEmailDomainRules.NeedsSecurityAdminMessage);
        if (erp is not null && erpChanged)
        {
            if (erp.Length > 30 || !erp.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
                c.Problem(2, "erpCompanyCode", "erp_company_code_invalid", "An ERP company code may be at most 30 characters: letters, digits, '.', '_' and '-'.");
        }
        if (erpChanged && (erp is not null || stored is not null) && !Holds(OrganizationSetupService.ErpCompanyCodePermission))
            c.Problem(2, "erpCompanyCode", "erp_company_code_needs_finance",
                "Only a finance user who manages the general ledger can set a company's ERP company code. Leave it empty, or ask finance to set it later.");

        var companies = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
            .Where(x => !x.IsDeleted && (stored == null || x.Id != stored.Id))
            .Select(x => new { x.Id, x.LegalNameEn, x.RegistrationNumber, x.ErpCompanyCode })
            .ToListAsync(ct);
        var label = legalNameEn.Length > 0 ? legalNameEn : "New company";
        var companyConflict = false;
        var nameChanged = stored is null || Differs(legalNameEn, stored.LegalNameEn);
        if (legalNameEn.Length > 0 && nameChanged && companies.Any(x => string.Equals(x.LegalNameEn.Trim(), legalNameEn, StringComparison.OrdinalIgnoreCase)))
        {
            companyConflict = true;
            c.Conflict(Areas.Company, legalNameEn, label, 2, "legalNameEn", CompanySetupGates.NameTakenCode,
                $"A company named '{legalNameEn}' already exists in this workspace. Each legal entity needs its own name.",
                $"توجد شركة باسم '{legalNameEn}' في مساحة العمل.", co.LegalNameAr);
        }
        if (registration.Length > 0 && crChanged && companies.Any(x => string.Equals(x.RegistrationNumber.Trim(), registration, StringComparison.OrdinalIgnoreCase)))
        {
            companyConflict = true;
            c.Conflict(Areas.Company, registration, label, 2, "registrationNumber", CompanySetupGates.CrTakenCode,
                $"CR number {registration} already belongs to a company in this workspace.",
                $"رقم السجل التجاري {registration} مستخدم لشركة أخرى.", co.LegalNameAr);
        }
        if (erp is not null && erpChanged && companies.Any(x => (x.ErpCompanyCode != null && string.Equals(x.ErpCompanyCode, erp, StringComparison.OrdinalIgnoreCase))
                                                 || (string.IsNullOrEmpty(x.ErpCompanyCode) && string.Equals(x.RegistrationNumber, erp, StringComparison.OrdinalIgnoreCase))))
        {
            companyConflict = true;
            c.Conflict(Areas.Company, erp, label, 2, "erpCompanyCode", "erp_company_code_taken",
                $"ERP company code {erp} is already used by another company. Each company needs its own code.",
                $"رمز الشركة في نظام تخطيط الموارد {erp} مستخدم لدى شركة أخرى. لكل شركة رمزها الخاص.");
        }
        var companyChanges = new List<string>();
        if (stored is not null)
        {
            void Track(string name, string? requested, string? current) { if (Differs(requested, current)) companyChanges.Add(name); }
            Track("legal name (English)", legalNameEn, stored.LegalNameEn);
            Track("legal name (Arabic)", co.LegalNameAr, stored.LegalNameAr);
            Track("trade name", co.TradeName, stored.TradeName);
            Track("CR number", registration, stored.RegistrationNumber);
            Track("MOL establishment number", mol, stored.WpsEmployerId);
            Track("GOSI number", co.GosiEmployerId, stored.GosiEmployerId);
            Track("Unified National Number", unified, stored.UnifiedNationalNumber);
            Track("VAT number", tax, stored.TaxNumber);
            Track("Qiwa establishment id", co.QiwaEstablishmentId, stored.QiwaEstablishmentId);
            Track("email domain", emailDomain, CompanyEmailDomainRules.Normalize(stored.EmailDomain));
            Track("work-email pattern", WorkEmailPatterns.Normalize(co.WorkEmailPattern), WorkEmailPatterns.Normalize(stored.WorkEmailPattern));
            Track("ERP company code", erp, stored.ErpCompanyCode);
        }
        if (!companyConflict)
        {
            if (stored is null) c.Item(Areas.Company, registration, label, V.ActionCreate, labelAr: co.LegalNameAr);
            else if (companyChanges.Count == 0) c.Item(Areas.Company, registration, label, V.ActionUnchanged, labelAr: co.LegalNameAr);
            else c.Item(Areas.Company, registration, label, V.ActionUpdate, $"Changes: {string.Join(", ", companyChanges)}.", co.LegalNameAr);
        }

        // ── Step 3: branches and locations (per company) ─────────────────────────────────────────────────
        // Existing mode (Addendum A3): a row with existingId edits that row (code read-only); a row without one is new;
        // an existing row left out of the body is kept as it is — the wizard never deletes.
        var branches = (body.Branches ?? []).ToList();
        if (ex is null)
        {
            if (branches.Count == 0) c.Problem(3, "branches", "branch_required", "Add at least one branch.", "أضف فرعاً واحداً على الأقل.");
            var heads = branches.Count(b => b.IsHeadOffice);
            if (branches.Count > 0 && heads != 1)
                c.Problem(3, "branches", "head_office_exactly_one", "Mark exactly one branch as the head office.", "حدد فرعاً واحداً فقط كمقر رئيسي.");
        }
        var branchCodes = new HashSet<string>(StringComparer.Ordinal);
        var existingLocationCodes = (await ScopedBypass.TenantWide(_db.Locations, tenantId, Why).AsNoTracking()
            .Select(x => x.Code).ToListAsync(ct)).Select(OrgCodes.Normalize).ToHashSet(StringComparer.Ordinal);
        var bodyLocationCodes = new HashSet<string>(StringComparer.Ordinal);
        var plannedBranches = new List<PlannedBranch>();
        var branchById = (ex?.Branches ?? []).ToDictionary(b => b.Id);
        var locationById = (ex?.Locations ?? []).ToDictionary(l => l.Id);
        var seenBranches = new HashSet<Guid>();
        var seenLocations = new HashSet<Guid>();
        var packTimeZone = HomeJurisdiction.TimeZoneFor(country);
        string EffectiveTz(string? tz) => string.IsNullOrWhiteSpace(tz) ? packTimeZone : tz.Trim();
        var headOfficeChanged = false;
        for (var i = 0; i < branches.Count; i++)
        {
            var b = branches[i];
            var code = OrgCodes.Normalize(b.Code);
            var f = $"branches[{i}]";
            Branch? row = null;
            if (b.ExistingId is Guid branchId)
            {
                if (!branchById.TryGetValue(branchId, out row) || !seenBranches.Add(branchId))
                {
                    c.Problem(3, $"{f}.existingId", "existing_row_unknown", $"Branch {code} is not one of this company's branches. Reload the company's setup.");
                    continue;
                }
                var storedCode = OrgCodes.Normalize(row.Code);
                if (code != storedCode)
                    c.Problem(3, $"{f}.code", "existing_code_locked",
                        $"A branch code cannot be changed here ({storedCode}); employee numbers and files use it. Change it in Setup → Branches.",
                        "لا يمكن تغيير رمز الفرع هنا.");
                code = storedCode;
            }
            if (code.Length == 0) { c.Problem(3, $"{f}.code", "required", "Enter the branch code."); continue; }
            if (code.Length > 40) c.Problem(3, $"{f}.code", "too_long", "A branch code is at most 40 characters.");
            if (!branchCodes.Add(code)) { c.Problem(3, $"{f}.code", "duplicate_code", $"Branch code {code} appears twice."); continue; }
            if (string.IsNullOrWhiteSpace(b.NameEn)) c.Problem(3, $"{f}.nameEn", "required", "Enter the branch name in English.");
            if (string.IsNullOrWhiteSpace(b.City)) c.Problem(3, $"{f}.city", "required", "Enter the branch city.");
            var tz = EffectiveTz(b.TimeZoneId);
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
                c.Problem(3, $"{f}.timeZoneId", "time_zone_invalid", $"'{tz}' is not a recognised time zone (for example Asia/Riyadh).");
            var name = (b.NameEn ?? string.Empty).Trim();
            var branchChanged = false;
            if (row is null)
            {
                if (ex is not null && ex.AllBranchCodes.Contains(code))
                    c.Conflict(Areas.Branch, code, name, 3, $"{f}.code", "branch_code_taken",
                        $"Branch code {code} is already used by this company (perhaps by a removed branch). Choose a different code.",
                        $"رمز الفرع {code} مستخدم لدى هذه الشركة (ربما لفرع محذوف). اختر رمزاً مختلفاً.", b.NameAr);
                else c.Item(Areas.Branch, code, name, V.ActionCreate, labelAr: b.NameAr);
                headOfficeChanged |= b.IsHeadOffice;
            }
            else
            {
                var changes = new List<string>();
                if (Differs(b.NameEn, row.NameEn)) changes.Add("name (English)");
                if (Differs(b.NameAr, row.NameAr)) changes.Add("name (Arabic)");
                if (Differs(b.City, row.City)) changes.Add("city");
                if (!string.Equals(tz, EffectiveTz(row.TimeZoneId), StringComparison.Ordinal)) changes.Add("time zone");
                if (b.IsHeadOffice != row.IsHeadOffice) { changes.Add("head office"); headOfficeChanged = true; }
                branchChanged = changes.Count > 0;
                c.Item(Areas.Branch, code, name, branchChanged ? V.ActionUpdate : V.ActionUnchanged,
                    branchChanged ? $"Changes: {string.Join(", ", changes)}." : null, b.NameAr);
            }

            var locations = (b.Locations ?? []).ToList();
            var plannedLocations = new List<PlannedLocation>();
            for (var j = 0; j < locations.Count; j++)
            {
                var l = locations[j];
                var lf = $"{f}.locations[{j}]";
                var lcode = OrgCodes.Normalize(l.Code);
                Location? lrow = null;
                if (l.ExistingId is Guid locationId)
                {
                    if (row is null || !locationById.TryGetValue(locationId, out lrow) || lrow.BranchId != row.Id || !seenLocations.Add(locationId))
                    {
                        c.Problem(3, $"{lf}.existingId", "existing_row_unknown", $"Location {lcode} is not one of this branch's locations. Reload the company's setup.");
                        continue;
                    }
                    var storedCode = OrgCodes.Normalize(lrow.Code);
                    if (lcode != storedCode)
                        c.Problem(3, $"{lf}.code", "existing_code_locked",
                            $"A location code cannot be changed here ({storedCode}). Change it in Setup → Locations.", "لا يمكن تغيير رمز الموقع هنا.");
                    lcode = storedCode;
                }
                if (lcode.Length == 0) { c.Problem(3, $"{lf}.code", "required", "Enter the location code."); continue; }
                if (!bodyLocationCodes.Add(lcode)) { c.Problem(3, $"{lf}.code", "duplicate_code", $"Location code {lcode} appears twice."); continue; }
                if (string.IsNullOrWhiteSpace(l.NameEn)) c.Problem(3, $"{lf}.nameEn", "required", "Enter the location name in English.");
                if (CompanySetupDataEntry.IsZeroPoint(l.Latitude, l.Longitude))
                {
                    // 0,0 is a form left at zero, not a work site: the location is kept without a geofence, never fenced at 0,0.
                    c.Warn(3, $"{lf}.latitude", CompanySetupDataEntry.CoordinatesMissingCode,
                        $"Location {lcode} has no coordinates yet (0,0 is not a work site), so no geofence is set. Add its latitude and longitude.",
                        $"لا توجد إحداثيات للموقع {lcode} بعد (0,0 ليس موقع عمل)، لذا لن يُضبط سياج جغرافي. أضف خط العرض وخط الطول.");
                    l = l with { Latitude = null, Longitude = null };
                }
                if (l.Latitude is < -90 or > 90) c.Problem(3, $"{lf}.latitude", "latitude_invalid", "Latitude must be between -90 and 90.");
                if (l.Longitude is < -180 or > 180) c.Problem(3, $"{lf}.longitude", "longitude_invalid", "Longitude must be between -180 and 180.");
                if (l.RadiusMeters is <= 0 or > 100_000) c.Problem(3, $"{lf}.radiusMeters", "radius_invalid", "The geofence radius must be between 1 and 100,000 metres.");
                if ((l.Latitude is null) != (l.Longitude is null)) c.Problem(3, $"{lf}.latitude", "coordinates_incomplete", "Enter both latitude and longitude, or neither.");
                var lname = (l.NameEn ?? string.Empty).Trim();
                var locationChanged = false;
                if (lrow is null)
                {
                    if (existingLocationCodes.Contains(lcode))
                        c.Conflict(Areas.Location, lcode, lname, 3, $"{lf}.code", "location_code_taken",
                            $"Location code {lcode} is already used in this workspace. Location codes are unique across all companies.",
                            $"رمز الموقع {lcode} مستخدم في مساحة العمل. رموز المواقع فريدة في جميع الشركات.", l.NameAr);
                    else c.Item(Areas.Location, lcode, lname, V.ActionCreate, labelAr: l.NameAr);
                }
                else
                {
                    var changes = new List<string>();
                    if (Differs(l.NameEn, lrow.NameEn)) changes.Add("name (English)");
                    if (Differs(l.NameAr, lrow.NameAr)) changes.Add("name (Arabic)");
                    if ((l.Latitude, l.Longitude) != CompanySetupDataEntry.Coordinates(lrow.Latitude, lrow.Longitude)) changes.Add("coordinates");
                    if (l.RadiusMeters != lrow.GeofenceRadiusMeters) changes.Add("geofence radius");
                    locationChanged = changes.Count > 0;
                    c.Item(Areas.Location, lcode, lname, locationChanged ? V.ActionUpdate : V.ActionUnchanged,
                        locationChanged ? $"Changes: {string.Join(", ", changes)}." : null, l.NameAr);
                }
                plannedLocations.Add(new PlannedLocation(l with { Code = lcode }, lrow, locationChanged));
            }
            if (row is not null)
                foreach (var kept in (ex?.Locations ?? []).Where(x => x.BranchId == row.Id && x.IsActive && !seenLocations.Contains(x.Id)))
                {
                    var keptCode = OrgCodes.Normalize(kept.Code);
                    bodyLocationCodes.Add(keptCode);
                    c.Item(Areas.Location, keptCode, kept.NameEn, V.ActionUnchanged, KeptReason, Blank(kept.NameAr));
                }
            var locationCount = plannedLocations.Count + (row is null ? 0 : (ex?.Locations ?? []).Count(x => x.BranchId == row.Id && x.IsActive && !seenLocations.Contains(x.Id)));
            if (locationCount == 0)
                c.Warn(3, $"{f}.locations", "branch_without_location",
                    $"Branch {code} has no location, so its employees can punch at any geofenced site in the workspace.",
                    $"لا يوجد موقع للفرع {code}، لذا يمكن لموظفيه تسجيل الحضور في أي موقع ضمن مساحة العمل.");
            plannedBranches.Add(new PlannedBranch(b with { Code = code, TimeZoneId = tz, Locations = plannedLocations.Select(p => p.Body).ToList() },
                row, branchChanged, plannedLocations));
        }
        var existingBranchIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (ex is not null)
        {
            foreach (var kept in ex.Branches.Where(x => x.IsActive && !seenBranches.Contains(x.Id)))
            {
                var keptCode = OrgCodes.Normalize(kept.Code);
                if (!branchCodes.Add(keptCode)) continue;
                c.Item(Areas.Branch, keptCode, kept.NameEn, V.ActionUnchanged, KeptReason, Blank(kept.NameAr));
            }
            foreach (var kept in ex.Branches) existingBranchIds.TryAdd(OrgCodes.Normalize(kept.Code), kept.Id);
            var activeCount = plannedBranches.Count(p => p.Existing?.IsActive ?? true)
                + ex.Branches.Count(x => x.IsActive && !seenBranches.Contains(x.Id));
            var headCount = plannedBranches.Count(p => (p.Existing?.IsActive ?? true) && p.Body.IsHeadOffice)
                + ex.Branches.Count(x => x.IsActive && !seenBranches.Contains(x.Id) && x.IsHeadOffice);
            if (activeCount == 0) c.Problem(3, "branches", "branch_required", "Add at least one branch.", "أضف فرعاً واحداً على الأقل.");
            else if (headCount != 1)
            {
                if (headOfficeChanged) c.Problem(3, "branches", "head_office_exactly_one", "Mark exactly one branch as the head office.", "حدد فرعاً واحداً فقط كمقر رئيسي.");
                else c.Warn(3, "branches", "head_office_exactly_one", "This company does not have exactly one head office. Mark one branch as the head office.",
                    "حدد فرعاً واحداً فقط كمقر رئيسي.");
            }
        }

        // ── Step 4: the organisation (Addendum B: every row is this company's) ─────────────────────────────
        // Every organisation code — cost centre, department, designation, grade — is unique within the company only
        // (owner decision 2026-10-23: cost centres too). An existing row (existingId, of THIS company) is update or unchanged; a new
        // row is create; a link resolves to this company's rows or rows new in the same draft. Nothing is deleted.
        var org = ex?.Org ?? new CompanySetupRows.CompanyRows([], [], [], []);
        var maps = ex?.Maps;
        string? CurrentLink(Guid? id, Func<CompanySetupRows.CodeMaps, IReadOnlyDictionary<Guid, string>> pick) =>
            id is Guid x && maps is not null && pick(maps).TryGetValue(x, out var code) ? code : null;
        string? NormalLink(string? code) => string.IsNullOrWhiteSpace(code) ? null : OrgCodes.Normalize(code);
        // B2: a live section save writes only its own section, so a link into another section must already be saved.
        void NotSaved(string section, string field, string what, string whatAr, string code) =>
            c.Problem(4, field, "reference_not_saved",
                $"Save {what} first: {code} is not saved yet.", $"احفظ {whatAr} أولاً: {code} غير محفوظ بعد.");

        // The company's own cost-centre codes, removed ones included (a removed row still holds its code).
        var existingCostCentres = org.CostCentres.Select(x => OrgCodes.Normalize(x.Code)).ToHashSet(StringComparer.Ordinal);
        var costCentres = new List<PlannedCostCentre>();
        var bodyCostCentreCodes = new HashSet<string>(StringComparer.Ordinal);
        var costCentreById = (ex?.CostCentres ?? []).ToDictionary(x => x.Id);
        var seenCostCentres = new HashSet<Guid>();
        foreach (var (cc, i) in (body.CostCentres ?? []).Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(cc.Code);
            var f = $"costCentres[{i}]";
            CostCenter? row = null;
            if (cc.ExistingId is Guid costCentreId)
            {
                if (!costCentreById.TryGetValue(costCentreId, out row) || !seenCostCentres.Add(costCentreId))
                {
                    c.Problem(4, $"{f}.existingId", "existing_row_unknown", $"Cost centre {code} is not one of this company's cost centres. Reload the company's setup.");
                    continue;
                }
                var storedCode = OrgCodes.Normalize(row.Code);
                if (code != storedCode)
                    c.Problem(4, $"{f}.code", "existing_code_locked",
                        $"A cost centre code cannot be changed here ({storedCode}); postings use it. Change it in Setup → Cost centres.",
                        "لا يمكن تغيير رمز مركز التكلفة هنا.");
                code = storedCode;
            }
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the cost centre code."); continue; }
            if (code.Length > 40) c.Problem(4, $"{f}.code", "too_long", "A cost centre code is at most 40 characters.");
            if (!bodyCostCentreCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Cost centre code {code} appears twice."); continue; }
            if (string.IsNullOrWhiteSpace(cc.NameEn)) c.Problem(4, $"{f}.nameEn", "required", "Enter the cost centre name in English.");
            var name = (cc.NameEn ?? string.Empty).Trim();
            var changed = false;
            if (row is not null)
            {
                var changes = new List<string>();
                if (Differs(cc.NameEn, row.Name)) changes.Add("name (English)");
                if (Differs(cc.NameAr, row.NameAr)) changes.Add("name (Arabic)");
                changed = changes.Count > 0;
                c.Item(Areas.CostCentre, code, name, changed ? V.ActionUpdate : V.ActionUnchanged, changed ? $"Changes: {string.Join(", ", changes)}." : null, cc.NameAr);
            }
            // A clash with one of the company's own codes is a conflict, never a reuse: an existing row is edited through
            // its existingId only.
            else if (existingCostCentres.Contains(code))
                c.Conflict(Areas.CostCentre, code, name, 4, $"{f}.code", "cost_centre_code_taken",
                    $"Cost centre code {code} is already used by this company (perhaps by a removed cost centre). Choose a different code, or restore that cost centre in Setup → Cost centres.",
                    $"رمز مركز التكلفة {code} مستخدم لدى هذه الشركة. اختر رمزاً مختلفاً.", cc.NameAr);
            else c.Item(Areas.CostCentre, code, name, V.ActionCreate, labelAr: cc.NameAr);
            costCentres.Add(new PlannedCostCentre(cc with { Code = code }, row, changed));
        }
        var existingCostCentreIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (ex is not null)
        {
            foreach (var kept in ex.CostCentres.Where(x => x.IsActive && !seenCostCentres.Contains(x.Id)))
            {
                var keptCode = OrgCodes.Normalize(kept.Code);
                if (!bodyCostCentreCodes.Add(keptCode)) continue;
                c.Item(Areas.CostCentre, keptCode, kept.Name, V.ActionUnchanged, KeptReason, Blank(kept.NameAr));
            }
            foreach (var kept in ex.CostCentres) existingCostCentreIds.TryAdd(OrgCodes.Normalize(kept.Code), kept.Id);
        }
        // A link may name any of the company's cost centres still on file, or one new in this draft.
        var knownCostCentreCodes = bodyCostCentreCodes.Concat(existingCostCentreIds.Keys).ToHashSet(StringComparer.Ordinal);

        // Grades: in the company's own currency (Addendum B withdrew one currency per workspace).
        var gradeById = org.Grades.Where(g => !g.IsDeleted).ToDictionary(g => g.Id);
        var takenGradeCodes = org.Grades.Select(g => OrgCodes.Normalize(g.Code)).ToHashSet(StringComparer.Ordinal);
        var plannedGrades = new List<PlannedGrade>();
        var bodyGradeCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenGrades = new HashSet<Guid>();
        foreach (var (g, i) in (body.Grades ?? []).Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(g.Code);
            var f = $"grades[{i}]";
            Grade? row = null;
            if (g.ExistingId is Guid gradeId)
            {
                if (!gradeById.TryGetValue(gradeId, out row) || !seenGrades.Add(gradeId))
                {
                    c.Problem(4, $"{f}.existingId", "existing_row_unknown", $"Grade {code} is not one of this company's grades. Reload the company's setup.");
                    continue;
                }
                var storedCode = OrgCodes.Normalize(row.Code);
                if (code != storedCode)
                    c.Problem(4, $"{f}.code", "existing_code_locked",
                        $"A grade code cannot be changed here ({storedCode}). Change it in Setup → Grades.", "لا يمكن تغيير رمز الدرجة هنا.");
                code = storedCode;
            }
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the grade code."); continue; }
            if (code.Length > 40) c.Problem(4, $"{f}.code", "too_long", "A grade code is at most 40 characters.");
            if (!bodyGradeCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Grade code {code} appears twice."); continue; }
            var name = (g.NameEn ?? string.Empty).Trim();
            if (name.Length == 0) c.Problem(4, $"{f}.nameEn", "required", "Enter the grade name in English.");
            if (g.Level is < 0 or > 100) c.Problem(4, $"{f}.level", "level_invalid", "The grade level must be between 0 and 100.");
            if (g.MinSalary < 0 || g.MidSalary < 0 || g.MaxSalary < 0 || g.MinSalary > g.MidSalary || g.MidSalary > g.MaxSalary)
                c.Problem(4, $"{f}.minSalary", "salary_band_invalid", "A salary band must be ordered minimum ≤ midpoint ≤ maximum.",
                    "يجب أن يكون نطاق الراتب: الحد الأدنى ≤ المنتصف ≤ الحد الأقصى.");
            var changed = false;
            if (row is null)
            {
                if (takenGradeCodes.Contains(code))
                    c.Conflict(Areas.Grade, code, name, 4, $"{f}.code", "grade_code_taken",
                        $"Grade code {code} is already used by this company (perhaps by a removed grade). Choose a different code, or restore that grade in Setup → Grades.",
                        $"رمز الدرجة {code} مستخدم لدى هذه الشركة. اختر رمزاً مختلفاً.", g.NameAr);
                else c.Item(Areas.Grade, code, name, V.ActionCreate, labelAr: g.NameAr);
            }
            else
            {
                var changes = new List<string>();
                if (Differs(g.NameEn, row.Name)) changes.Add("name (English)");
                if (Differs(g.NameAr, row.NameAr)) changes.Add("name (Arabic)");
                if (g.Level != row.Level) changes.Add("level");
                if (g.MinSalary != row.MinSalary || g.MidSalary != row.MidSalary || g.MaxSalary != row.MaxSalary) changes.Add("salary band");
                changed = changes.Count > 0;
                c.Item(Areas.Grade, code, name, changed ? V.ActionUpdate : V.ActionUnchanged, changed ? $"Changes: {string.Join(", ", changes)}." : null, g.NameAr);
            }
            plannedGrades.Add(new PlannedGrade(g with { Code = code }, row, changed));
        }
        foreach (var kept in gradeById.Values.Where(x => x.IsActive && !seenGrades.Contains(x.Id)).OrderBy(x => x.Level).ThenBy(x => x.Code))
        {
            var keptCode = OrgCodes.Normalize(kept.Code);
            if (!bodyGradeCodes.Add(keptCode)) continue;
            c.Item(Areas.Grade, keptCode, kept.Name, V.ActionUnchanged, KeptReason, Blank(kept.NameAr));
        }
        var existingGradeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var kept in gradeById.Values) existingGradeIds.TryAdd(OrgCodes.Normalize(kept.Code), kept.Id);
        var knownGradeCodes = bodyGradeCodes.Concat(existingGradeIds.Keys).ToHashSet(StringComparer.Ordinal);

        // Departments: this company's own, links and all (Decision 7's "never re-link" applied to shared rows; there are none now).
        var deptById = org.Departments.Where(d => !d.IsDeleted).ToDictionary(d => d.Id);
        var takenDeptCodes = org.Departments.Select(d => OrgCodes.Normalize(d.Code)).ToHashSet(StringComparer.Ordinal);
        var existingDepartmentIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var d in deptById.Values) existingDepartmentIds.TryAdd(OrgCodes.Normalize(d.Code), d.Id);
        var plannedDepartments = new List<(PlannedDepartment Row, int Index)>();
        var bodyDepartmentCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenDepartments = new HashSet<Guid>();
        foreach (var (d, i) in (body.Departments ?? []).Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(d.Code);
            var f = $"departments[{i}]";
            Department? row = null;
            if (d.ExistingId is Guid departmentId)
            {
                if (!deptById.TryGetValue(departmentId, out row) || !seenDepartments.Add(departmentId))
                {
                    c.Problem(4, $"{f}.existingId", "existing_row_unknown", $"Department {code} is not one of this company's departments. Reload the company's setup.");
                    continue;
                }
                var storedCode = OrgCodes.Normalize(row.Code);
                if (code != storedCode)
                    c.Problem(4, $"{f}.code", "existing_code_locked",
                        $"A department code cannot be changed here ({storedCode}); employee numbers and reports use it. Change it in Setup → Departments.",
                        "لا يمكن تغيير رمز القسم هنا.");
                code = storedCode;
            }
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the department code."); continue; }
            if (code.Length > 40) c.Problem(4, $"{f}.code", "too_long", "A department code is at most 40 characters.");
            if (!bodyDepartmentCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Department code {code} appears twice."); continue; }
            var name = (d.NameEn ?? string.Empty).Trim();
            if (name.Length == 0) c.Problem(4, $"{f}.nameEn", "required", "Enter the department name in English.");
            var branchCode = NormalLink(d.BranchCode);
            var ccCode = NormalLink(d.CostCentreCode);
            var parentCode = NormalLink(d.ParentCode);
            var curBranch = row is null ? null : CurrentLink(row.BranchId, m => m.Branches);
            var curCc = row is null ? null : CurrentLink(row.CostCenterId, m => m.CostCentres);
            var curParent = row is null ? null : CurrentLink(row.ParentDepartmentId, m => m.Departments);
            // A link is judged when it is written: on a new row, or when it changes. A link left as it is stays as it is.
            var branchWritten = branchCode is not null && (row is null || branchCode != curBranch);
            var ccWritten = ccCode is not null && (row is null || ccCode != curCc);
            if (branchWritten && !branchCodes.Contains(branchCode!))
                c.Problem(4, $"{f}.branchCode", "branch_unknown", $"Branch {branchCode} is not one of this company's branches.");
            else if (branchWritten && focus == V.Sections.Departments && !existingBranchIds.ContainsKey(branchCode!))
                c.Problem(4, $"{f}.branchCode", "reference_not_saved",
                    $"Branch {branchCode} is not saved yet. It is created when you finish; link it to this department then.",
                    $"الفرع {branchCode} غير محفوظ بعد. يتم إنشاؤه عند الإنهاء؛ اربطه بهذا القسم حينها.");
            if (ccWritten && !knownCostCentreCodes.Contains(ccCode!))
                c.Problem(4, $"{f}.costCentreCode", "cost_centre_unknown", $"Cost centre {ccCode} is not one of this company's cost centres.");
            else if (ccWritten && focus == V.Sections.Departments && !existingCostCentreIds.ContainsKey(ccCode!))
                NotSaved(V.Sections.CostCentres, $"{f}.costCentreCode", "Cost centres", "مراكز التكلفة", ccCode!);
            var changed = false;
            if (row is null)
            {
                if (takenDeptCodes.Contains(code))
                    c.Conflict(Areas.Department, code, name, 4, $"{f}.code", "department_code_taken",
                        $"Department code {code} is already used by this company (perhaps by a removed department). Choose a different code, or restore that department in Setup → Departments.",
                        $"رمز القسم {code} مستخدم لدى هذه الشركة. اختر رمزاً مختلفاً.", d.NameAr);
                else c.Item(Areas.Department, code, name, V.ActionCreate, labelAr: d.NameAr);
            }
            else
            {
                var changes = new List<string>();
                if (Differs(d.NameEn, row.NameEn)) changes.Add("name (English)");
                if (Differs(d.NameAr, row.NameAr)) changes.Add("name (Arabic)");
                if (parentCode != curParent) changes.Add("parent department");
                if (branchCode != curBranch) changes.Add("branch");
                if (ccCode != curCc) changes.Add("cost centre");
                changed = changes.Count > 0;
                c.Item(Areas.Department, code, name, changed ? V.ActionUpdate : V.ActionUnchanged, changed ? $"Changes: {string.Join(", ", changes)}." : null, d.NameAr);
            }
            plannedDepartments.Add((new PlannedDepartment(d with { Code = code, BranchCode = branchCode, CostCentreCode = ccCode, ParentCode = parentCode }, row, changed), i));
        }
        foreach (var kept in deptById.Values.Where(x => x.IsActive && !seenDepartments.Contains(x.Id)).OrderBy(x => x.Code))
        {
            var keptCode = OrgCodes.Normalize(kept.Code);
            if (!bodyDepartmentCodes.Add(keptCode)) continue;
            c.Item(Areas.Department, keptCode, kept.NameEn, V.ActionUnchanged, KeptReason, Blank(kept.NameAr));
        }
        var knownDepartmentCodes = bodyDepartmentCodes.Concat(existingDepartmentIds.Keys).ToHashSet(StringComparer.Ordinal);

        // Parents: one of the company's departments, or one new in this draft (a parent may be new in the same
        // section). No cycles — judged on the whole tree as it will be, existing rows' moves included.
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var d in deptById.Values) parentOf[OrgCodes.Normalize(d.Code)] = CurrentLink(d.ParentDepartmentId, m => m.Departments);
        foreach (var (p, i) in plannedDepartments)
        {
            parentOf[p.Body.Code] = p.Body.ParentCode;
            var curParent = p.Existing is null ? null : CurrentLink(p.Existing.ParentDepartmentId, m => m.Departments);
            if (p.Body.ParentCode is { } parent && (p.Existing is null || parent != curParent) && !knownDepartmentCodes.Contains(parent))
                c.Problem(4, $"departments[{i}].parentCode", "parent_unknown", $"Department {p.Body.Code}'s parent {parent} does not exist.",
                    $"القسم الأعلى {parent} للقسم {p.Body.Code} غير موجود.");
        }
        var inCycle = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (p, _) in plannedDepartments)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { p.Body.Code };
            for (var at = parentOf.GetValueOrDefault(p.Body.Code); at is not null; at = parentOf.GetValueOrDefault(at))
            {
                if (at == p.Body.Code) { inCycle.Add(p.Body.Code); break; }
                if (!seen.Add(at)) break;
            }
        }
        if (inCycle.Count > 0)
            c.Problem(4, "departments", "parent_cycle", $"Departments {string.Join(", ", inCycle)} are each other's parents. Break the loop.",
                "هذه الأقسام أقسام عليا لبعضها. أزل الحلقة.");
        // Write order: new departments parents-first, then edits of existing ones (whose new parent may be new).
        var orderedDepartments = new List<PlannedDepartment>();
        {
            var pending = plannedDepartments.Select(x => x.Row).Where(p => p.Existing is null).ToDictionary(p => p.Body.Code, StringComparer.Ordinal);
            var progress = true;
            while (pending.Count > 0 && progress)
            {
                progress = false;
                foreach (var p in pending.Values.ToList())
                {
                    if (p.Body.ParentCode is { } parent && pending.ContainsKey(parent)) continue;
                    orderedDepartments.Add(p);
                    pending.Remove(p.Body.Code);
                    progress = true;
                }
            }
            orderedDepartments.AddRange(pending.Values);
            orderedDepartments.AddRange(plannedDepartments.Select(x => x.Row).Where(p => p.Existing is not null));
        }

        var designationById = org.Designations.Where(d => !d.IsDeleted).ToDictionary(d => d.Id);
        var takenDesignationCodes = org.Designations.Select(d => OrgCodes.Normalize(d.Code)).ToHashSet(StringComparer.Ordinal);
        var plannedDesignations = new List<PlannedDesignation>();
        var bodyDesignationCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenDesignations = new HashSet<Guid>();
        foreach (var (ds, i) in (body.Designations ?? []).Select((x, i) => (x, i)))
        {
            var code = OrgCodes.Normalize(ds.Code);
            var f = $"designations[{i}]";
            Designation? row = null;
            if (ds.ExistingId is Guid designationId)
            {
                if (!designationById.TryGetValue(designationId, out row) || !seenDesignations.Add(designationId))
                {
                    c.Problem(4, $"{f}.existingId", "existing_row_unknown", $"Designation {code} is not one of this company's designations. Reload the company's setup.");
                    continue;
                }
                var storedCode = OrgCodes.Normalize(row.Code);
                if (code != storedCode)
                    c.Problem(4, $"{f}.code", "existing_code_locked",
                        $"A designation code cannot be changed here ({storedCode}). Change it in Setup → Designations.", "لا يمكن تغيير رمز المسمى الوظيفي هنا.");
                code = storedCode;
            }
            if (code.Length == 0) { c.Problem(4, $"{f}.code", "required", "Enter the designation code."); continue; }
            if (code.Length > 40) c.Problem(4, $"{f}.code", "too_long", "A designation code is at most 40 characters.");
            if (!bodyDesignationCodes.Add(code)) { c.Problem(4, $"{f}.code", "duplicate_code", $"Designation code {code} appears twice."); continue; }
            var title = (ds.TitleEn ?? string.Empty).Trim();
            if (title.Length == 0) c.Problem(4, $"{f}.titleEn", "required", "Enter the designation title in English.");
            var deptCode = NormalLink(ds.DepartmentCode);
            var gradeCode = NormalLink(ds.GradeCode);
            var curDept = row is null ? null : CurrentLink(row.DepartmentId, m => m.Departments);
            var curGrade = row is null ? null : CurrentLink(row.GradeId, m => m.Grades);
            var deptWritten = deptCode is not null && (row is null || deptCode != curDept);
            var gradeWritten = gradeCode is not null && (row is null || gradeCode != curGrade);
            if (deptWritten && !knownDepartmentCodes.Contains(deptCode!))
                c.Problem(4, $"{f}.departmentCode", "department_unknown", $"Department {deptCode} is not one of this company's departments.");
            else if (deptWritten && focus == V.Sections.Designations && !existingDepartmentIds.ContainsKey(deptCode!))
                NotSaved(V.Sections.Departments, $"{f}.departmentCode", "Departments", "الأقسام", deptCode!);
            if (gradeWritten && !knownGradeCodes.Contains(gradeCode!))
                c.Problem(4, $"{f}.gradeCode", "grade_unknown", $"Grade {gradeCode} is not one of this company's grades.");
            else if (gradeWritten && focus == V.Sections.Designations && !existingGradeIds.ContainsKey(gradeCode!))
                NotSaved(V.Sections.Grades, $"{f}.gradeCode", "Grades", "الدرجات", gradeCode!);
            var changed = false;
            if (row is null)
            {
                if (takenDesignationCodes.Contains(code))
                    c.Conflict(Areas.Designation, code, title, 4, $"{f}.code", "designation_code_taken",
                        $"Designation code {code} is already used by this company (perhaps by a removed designation). Choose a different code, or restore that designation in Setup → Designations.",
                        $"رمز المسمى الوظيفي {code} مستخدم لدى هذه الشركة. اختر رمزاً مختلفاً.", ds.TitleAr);
                else c.Item(Areas.Designation, code, title, V.ActionCreate, labelAr: ds.TitleAr);
            }
            else
            {
                var changes = new List<string>();
                if (Differs(ds.TitleEn, row.TitleEn)) changes.Add("title (English)");
                if (Differs(ds.TitleAr, row.TitleAr)) changes.Add("title (Arabic)");
                if (deptCode != curDept) changes.Add("department");
                if (gradeCode != curGrade) changes.Add("grade");
                changed = changes.Count > 0;
                c.Item(Areas.Designation, code, title, changed ? V.ActionUpdate : V.ActionUnchanged, changed ? $"Changes: {string.Join(", ", changes)}." : null, ds.TitleAr);
            }
            plannedDesignations.Add(new PlannedDesignation(ds with { Code = code, DepartmentCode = deptCode, GradeCode = gradeCode }, row, changed));
        }
        foreach (var kept in designationById.Values.Where(x => x.IsActive && !seenDesignations.Contains(x.Id)).OrderBy(x => x.Code))
        {
            var keptCode = OrgCodes.Normalize(kept.Code);
            if (!bodyDesignationCodes.Add(keptCode)) continue;
            c.Item(Areas.Designation, keptCode, kept.TitleEn, V.ActionUnchanged, KeptReason, Blank(kept.TitleAr));
        }

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
        var ruleAction = V.ActionCreate;
        if (ex?.Rule is { } rule && numbering is not null)
        {
            var changes = new List<string>();
            if (!string.Equals(prefix, Clean(rule.CompanyPrefix).ToUpperInvariant(), StringComparison.Ordinal)) changes.Add("prefix");
            if (numbering.UseCountryPrefix != rule.UseCountryPrefix || numbering.UseBranchPrefix != rule.UseBranchPrefix
                || numbering.UseDepartmentPrefix != rule.UseDepartmentPrefix || numbering.UseYear != rule.UseYear) changes.Add("format");
            if (numbering.PaddingLength != rule.PaddingLength) changes.Add("padding");
            if (numbering.NextSequence != rule.NextSequence) changes.Add("next number");
            // Lowering the counter would hand out employee numbers that were already issued.
            if (numbering.NextSequence < rule.NextSequence)
                c.Problem(5, "rules.employeeNumbering.nextSequence", "sequence_cannot_go_back",
                    $"The next employee number is already {rule.NextSequence}. A lower number could repeat one already issued.",
                    $"رقم الموظف التالي هو {rule.NextSequence} بالفعل؛ لا يمكن إنقاصه.");
            ruleAction = changes.Count == 0 ? V.ActionUnchanged : V.ActionUpdate;
            c.Item(Areas.EmployeeIdRule, prefix, "Employee numbering for this company", ruleAction,
                changes.Count == 0 ? null : $"Changes: {string.Join(", ", changes)}.", "ترقيم الموظفين لهذه الشركة");
        }
        else c.Item(Areas.EmployeeIdRule, prefix, "Employee numbering for this company", V.ActionCreate, labelAr: "ترقيم الموظفين لهذه الشركة");

        var cutoverAction = V.ActionCreate;
        if (cutoverDate != default)
        {
            var cutoverLabel = "Planned payroll start (first day of the first payroll month)";
            const string cutoverLabelAr = "بداية الرواتب المخططة (أول يوم من أول شهر رواتب)";
            var cutoverCode = cutoverDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (ex?.Cutover is { } cutover)
            {
                if (cutover.CutoverDate == cutoverDate) cutoverAction = V.ActionUnchanged;
                else if (cutover.Status == CutoverStatuses.Planned) cutoverAction = V.ActionUpdate;
                else
                {
                    cutoverAction = V.ActionConflict;
                    // Addendum A4: editable only while the cutover is still Planned.
                    c.Problem(2, "firstPayrollMonth", "first_payroll_month_locked",
                        $"Payroll for this company started on {cutover.CutoverDate:yyyy-MM-dd} (cutover {cutover.Status.ToLowerInvariant()}), so the first payroll month can no longer change.",
                        "بدأت رواتب هذه الشركة، لذا لا يمكن تغيير أول شهر للرواتب.");
                }
                if (cutoverAction != V.ActionConflict)
                    c.Item(Areas.Cutover, cutoverCode, cutoverLabel, cutoverAction,
                        cutoverAction == V.ActionUpdate ? $"Changes: from {cutover.CutoverDate:yyyy-MM-dd}." : null, cutoverLabelAr);
            }
            else c.Item(Areas.Cutover, cutoverCode, cutoverLabel, V.ActionCreate, labelAr: cutoverLabelAr);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (cutoverAction is V.ActionCreate or V.ActionUpdate && cutoverDate < new DateOnly(today.Year, today.Month, 1))
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
            else if (ex is not null && await ScopedBypass.TenantWide(_db.PublicHolidayCalendars, tenantId, Why).AsNoTracking()
                         .AnyAsync(x => x.CompanyId == ex.Company.Id && x.CountryCode == country && x.CalendarYear == holidayCalendar.CalendarYear, ct))
            {
                // The company already has this year's calendar: kept as it is (holidays are edited in Leave → Holidays).
                c.Item(Areas.HolidayCalendar, holidayCalendar.CalendarYear.ToString(CultureInfo.InvariantCulture), holidayCalendar.Name, V.ActionUnchanged,
                    $"This company already has its {holidayCalendar.CalendarYear} holiday calendar; it is kept as it is.",
                    $"العطل الرسمية {holidayCalendar.CalendarYear}");
                holidayCalendar = null;
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

        // Statutory leave (owner decision 2026-10-23: leave types and policies are per company). "Use the country pack" is
        // judged for THIS company: a new company has none, so every pack policy is created for it; an existing company
        // reuses the ones it has and gets only the missing ones. They are the company's rows, so a company super admin
        // adds them like a group admin.
        IReadOnlySet<string> missingLeave = new HashSet<string>();
        if (rules?.LeavePolicies?.UsePack == true && country.Length > 0)
        {
            // The installer's own read-only plan, so the review names exactly what the commit installs (an unassigned
            // legacy policy of the same type and country counts as present in both). A company not created yet has
            // nothing of its own: only unassigned legacy rows can cover it.
            missingLeave = ex is null
                ? await TenantProvisioningBundle.MissingDefaultLeavePoliciesAsync(_db, tenantId, companyId: null, country, ct)
                : await TenantProvisioningBundle.MissingDefaultLeaveForCountryAsync(_db, tenantId, ex.Company.Id, country, ct);
            foreach (var (typeCode, typeLabel) in TenantProvisioningBundle.DefaultLeavePolicyTypes)
                c.Item(Areas.LeavePolicy, typeCode, $"Default {typeLabel} — {country}",
                    missingLeave.Contains(typeCode) ? V.ActionCreate : V.ActionReuse,
                    missingLeave.Contains(typeCode)
                        ? $"Statutory default for {country}, created for this company."
                        : $"This company already has its {country} default; it is kept as it is.",
                    labelAr: $"سياسة الإجازة النظامية الافتراضية ({typeCode}) — {country}");
        }

        // Decision 11: the tenant-default GCC row for the country, created only when missing, never overwritten.
        GCCComplianceSetting? newGccDefault = null;
        GCCComplianceSetting? tenantDefaultGcc = null;
        if (country.Length > 0)
        {
            tenantDefaultGcc = await ScopedBypass.TenantWide(_db.GCCComplianceSettings, tenantId, Why).AsNoTracking()
                .Where(x => x.CompanyId == null && x.CountryCode == country).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (tenantDefaultGcc is null && !caller.IsGroupScope)
                c.Warn(5, "rules.workWeek", "workspace_defaults_need_group_scope",
                    $"The workspace has no country settings for {country} (end of service, weekend). They apply to every {country} company, so a group administrator must add them in Setup → Country settings.",
                    "إعدادات الدولة تنطبق على جميع الشركات، ويضيفها مسؤول المجموعة.");
            else if (tenantDefaultGcc is null)
            {
                newGccDefault = CompanySetupPack.TenantDefaultGccRow(tenantId, country);
                c.Item(Areas.GccSetting, country, $"Country settings for {country} (end of service, weekend)", V.ActionCreate,
                    "No settings exist for this country yet; the country pack defaults are written once and never overwritten.",
                    labelAr: $"إعدادات الدولة {country} (مكافأة نهاية الخدمة، العطلة الأسبوعية)");
            }
            else c.Item(Areas.GccSetting, country, $"Country settings for {country}", V.ActionReuse, "Already configured for this workspace.",
                $"إعدادات الدولة {country}");
        }

        GCCComplianceSetting? workWeekOverride = null;
        (Guid, string, string)? workWeekUpdate = null;
        if (country.Length > 0)
        {
            var resolved = await _workWeek.ResolveAsync(tenantId, null, country, ct);
            var resolvedWeekend = newGccDefault is not null
                ? WorkWeekParser.ParseWeekendDays(newGccDefault.WeekendDays) ?? resolved.WeekendDays
                : resolved.WeekendDays;
            var resolvedText = CompanySetupPack.FormatWeekend(resolvedWeekend);
            var requested = rules?.WorkWeek;
            // Existing mode: the company's own override row, if it has one (it is edited, never removed here).
            var companyGcc = ex is null ? null : await ScopedBypass.TenantWide(_db.GCCComplianceSettings, tenantId, Why).AsNoTracking()
                .Where(x => x.CompanyId == ex.Company.Id && x.CountryCode == country).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            var companyWeekend = companyGcc is null ? null : WorkWeekParser.ParseWeekendDays(companyGcc.WeekendDays);
            if (requested is { Override: true })
            {
                var parsed = WorkWeekParser.ParseWeekendDays(requested.WeekendDays);
                if (parsed is null || parsed.Count == 0 || parsed.Count > 3)
                    c.Problem(5, "rules.workWeek.weekendDays", "weekend_invalid", "Choose the weekend (rest) days, for example Fri,Sat.");
                else if (companyGcc is not null)
                {
                    var text = CompanySetupPack.FormatWeekend(parsed);
                    if (companyWeekend is not null && parsed.SetEquals(companyWeekend))
                        c.Item(Areas.WorkWeek, text, $"Weekend {text} for this company", V.ActionUnchanged, "This company's own weekend, kept as it is.",
                            $"العطلة الأسبوعية {text} لهذه الشركة");
                    else
                    {
                        workWeekUpdate = (companyGcc.Id, text, CompanySetupPack.WorkWeekFor(parsed));
                        c.Item(Areas.WorkWeek, text, $"Weekend {text} for this company", V.ActionUpdate, $"Changes: weekend, from {companyGcc.WeekendDays}.",
                            $"العطلة الأسبوعية {text} لهذه الشركة");
                    }
                }
                else if (parsed.SetEquals(resolvedWeekend))
                    c.Item(Areas.WorkWeek, resolvedText, $"Weekend {resolvedText}", V.ActionReuse, "Same as the workspace setting; no override is written.",
                        $"العطلة الأسبوعية {resolvedText}");
                else
                {
                    var text = CompanySetupPack.FormatWeekend(parsed);
                    workWeekOverride = CompanySetupPack.CompanyOverrideRow(tenantId, country, tenantDefaultGcc ?? newGccDefault, parsed);
                    c.Item(Areas.WorkWeek, text, $"Weekend {text} for this company", V.ActionCreate, $"Overrides the workspace weekend ({resolvedText}) for this company only.",
                        $"العطلة الأسبوعية {text} لهذه الشركة");
                }
            }
            else if (companyGcc is not null)
            {
                var text = companyWeekend is null ? companyGcc.WeekendDays : CompanySetupPack.FormatWeekend(companyWeekend);
                c.Item(Areas.WorkWeek, text, $"Weekend {text} for this company", V.ActionUnchanged, "This company's own weekend, kept as it is.",
                    $"العطلة الأسبوعية {text} لهذه الشركة");
                c.Warn(5, "rules.workWeek", "work_week_override_kept",
                    $"This company has its own weekend ({text}); it is kept. Remove it in Setup → Country settings if it should follow the workspace.",
                    "لهذه الشركة عطلة أسبوعية خاصة بها وسيتم الإبقاء عليها.");
            }
            else c.Item(Areas.WorkWeek, resolvedText, $"Weekend {resolvedText}", V.ActionReuse, "The workspace working week applies.",
                $"العطلة الأسبوعية {resolvedText}");
        }

        // ── Step 6: company super admin ──────────────────────────────────────────────────────────────────
        var admin = body.CompanyAdmin ?? new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null);
        var adminAction = await PlanCompanyAdminAsync(c, caller, admin, emailDomain, ceiling, ex?.Company.Id, ct);

        var plan = Finish(c, focus);
        if (!plan.Ok) return (plan, null);

        var request = stored is null
            ? new CompanyRequest(
                LegalNameEn: legalNameEn,
                LegalNameAr: co.LegalNameAr?.Trim(),
                TradeName: string.IsNullOrWhiteSpace(co.TradeName) ? legalNameEn : co.TradeName.Trim(),
                CountryCode: country,
                Jurisdiction: null,
                RegistrationNumber: registration,
                TaxNumber: tax.Length == 0 ? null : tax,
                WpsEmployerId: mol,
                GosiEmployerId: co.GosiEmployerId?.Trim(),
                // Decision: the Qiwa establishment id defaults to the MOL establishment number.
                QiwaEstablishmentId: string.IsNullOrWhiteSpace(co.QiwaEstablishmentId) ? mol : co.QiwaEstablishmentId.Trim(),
                DefaultCurrency: currency,
                EmailDomain: emailDomain,
                WorkEmailPattern: co.WorkEmailPattern,
                IsActive: true,
                ErpCompanyCode: erp,
                UnifiedNationalNumber: unified.Length == 0 ? null : unified)
            // Existing mode (Addendum A4): the stored country, currency, jurisdiction and activation, never the body's.
            : new CompanyRequest(
                LegalNameEn: legalNameEn,
                LegalNameAr: co.LegalNameAr?.Trim(),
                TradeName: co.TradeName?.Trim(),
                CountryCode: stored.CountryCode,
                Jurisdiction: stored.Jurisdiction,
                RegistrationNumber: registration,
                TaxNumber: tax.Length == 0 ? null : tax,
                WpsEmployerId: mol,
                GosiEmployerId: co.GosiEmployerId?.Trim(),
                QiwaEstablishmentId: co.QiwaEstablishmentId?.Trim(),
                DefaultCurrency: stored.DefaultCurrency,
                EmailDomain: emailDomain,
                WorkEmailPattern: co.WorkEmailPattern,
                IsActive: stored.IsActive,
                // null keeps the stored value; "" clears it (CompanyRequest's own convention).
                ErpCompanyCode: erpChanged ? erp ?? string.Empty : null,
                UnifiedNationalNumber: unifiedChanged ? unified : null);

        return (plan, new CompanySetupPlanModel
        {
            Country = country,
            Currency = currency,
            CompanyRequest = request,
            ExistingCompanyId = ex?.Company.Id,
            CompanyChanged = stored is not null && companyChanges.Count > 0,
            Branches = plannedBranches,
            ExistingBranchIds = existingBranchIds,
            CostCentres = costCentres,
            ExistingCostCentreIds = existingCostCentreIds,
            Departments = orderedDepartments,
            ExistingDepartmentIds = existingDepartmentIds,
            Designations = plannedDesignations,
            Grades = plannedGrades,
            ExistingGradeIds = existingGradeIds,
            // A section save (focus) may go ahead while step 5 still has problems; its model never writes the rule.
            EmployeeIdRule = numbering is null
                ? new DraftEmployeeIdRule(prefix, false, false, false, false, 1, 1, AllowManualOverride: false)
                : new DraftEmployeeIdRule(prefix, numbering.UseCountryPrefix, numbering.UseBranchPrefix, numbering.UseDepartmentPrefix,
                    numbering.UseYear, numbering.PaddingLength, numbering.NextSequence, AllowManualOverride: ex?.Rule?.AllowManualOverride ?? false),
            EmployeeIdRuleAction = ruleAction,
            CutoverDate = cutoverDate,
            CutoverAction = cutoverAction,
            ExistingCutoverId = ex?.Cutover?.Id,
            HolidayCalendar = holidayCalendar,
            MissingLeaveDefaults = missingLeave,
            NewGccDefault = newGccDefault,
            WorkWeekOverride = workWeekOverride,
            WorkWeekUpdate = workWeekUpdate,
            CompanyAdmin = admin,
            CompanyAdminAction = adminAction,
            Items = plan.Items,
        });
    }

    /// <summary>
    /// Addendum A9: the three-way comparison of existing mode (<see cref="CompanySetupBaseline"/>). Returns the body the
    /// rest of the planner reads: each field of each existing row is the draft's value where only the user changed it,
    /// and the database's value now where the user did not (so an edit made elsewhere since the draft started stands).
    /// A field changed on both sides to different values is a <c>changed_since_draft_started</c> problem. A row or
    /// block with no baseline (a draft saved before A9) refuses the whole plan with <c>draft_needs_refresh</c>: null.
    /// </summary>
    private static CompanySetupDraftBody? Reconcile(Collector c, CompanySetupDraftBody body, ExistingCompany ex)
    {
        var refresh = false;
        HashSet<string> Merge(int step, string path, string what, string whatAr, Dictionary<string, string?>? baseline,
            Dictionary<string, string?> draft, Dictionary<string, string?> database, string area = "")
        {
            var takeDatabase = new HashSet<string>(StringComparer.Ordinal);
            if (baseline is null) { refresh = true; return takeDatabase; }
            foreach (var (key, now) in database)
            {
                string? before = null;
                if (!baseline.TryGetValue(key, out before) && !CompanySetupBaseline.AbsentMeansEmpty(area, key)) { refresh = true; continue; }
                var mine = draft.GetValueOrDefault(key);
                switch (CompanySetupBaseline.Decide(mine, before, now))
                {
                    case CompanySetupBaseline.Outcome.TakeDatabase: takeDatabase.Add(key); break;
                    case CompanySetupBaseline.Outcome.Conflict:
                        var (en, ar) = CompanySetupBaseline.FieldWords(key);
                        var shown = now ?? "(empty)";
                        c.Changed(step, $"{path}.{key}",
                            $"{what}: the {en} was changed elsewhere since this draft started. It is now “{shown}”. Take the current value, or keep yours.",
                            $"{whatAr}: تم تغيير {ar} في مكان آخر منذ بدء هذه المسودة. القيمة الحالية «{now ?? "(فارغ)"}». اعتمد القيمة الحالية أو احتفظ بقيمتك.",
                            now);
                        break;
                }
            }
            return takeDatabase;
        }

        // Company (step 2). Country and currency are locked (A4), so they are not part of the comparison.
        var co = body.Company;
        var stored = ex.Company;
        var take = Merge(2, "company", "Company", "الشركة", co.Baseline, CompanySetupBaseline.Company(co), CompanySetupBaseline.Company(stored, ex.Cutover));
        string? Pick(string key, string? mine, string? theirs) => take.Contains(key) ? theirs : mine;
        co = co with
        {
            LegalNameEn = Pick("legalNameEn", co.LegalNameEn, stored.LegalNameEn) ?? string.Empty,
            LegalNameAr = Pick("legalNameAr", co.LegalNameAr, stored.LegalNameAr),
            TradeName = Pick("tradeName", co.TradeName, stored.TradeName),
            RegistrationNumber = Pick("registrationNumber", co.RegistrationNumber, stored.RegistrationNumber) ?? string.Empty,
            MolEstablishmentNumber = Pick("molEstablishmentNumber", co.MolEstablishmentNumber, stored.WpsEmployerId),
            GosiEmployerId = Pick("gosiEmployerId", co.GosiEmployerId, stored.GosiEmployerId),
            UnifiedNationalNumber = Pick("unifiedNationalNumber", co.UnifiedNationalNumber, stored.UnifiedNationalNumber),
            TaxNumber = Pick("taxNumber", co.TaxNumber, stored.TaxNumber),
            QiwaEstablishmentId = Pick("qiwaEstablishmentId", co.QiwaEstablishmentId, stored.QiwaEstablishmentId),
            EmailDomain = Pick("emailDomain", co.EmailDomain, stored.EmailDomain),
            WorkEmailPattern = Pick("workEmailPattern", co.WorkEmailPattern, stored.WorkEmailPattern),
            ErpCompanyCode = Pick("erpCompanyCode", co.ErpCompanyCode, stored.ErpCompanyCode),
            FirstPayrollMonth = take.Contains("firstPayrollMonth") && ex.Cutover is { } cut
                ? cut.CutoverDate.ToString("yyyy-MM", CultureInfo.InvariantCulture) : co.FirstPayrollMonth,
        };

        // Branches and their locations (step 3): existing rows only; a new row has nothing to compare with.
        var branchById = ex.Branches.ToDictionary(b => b.Id);
        var locationById = ex.Locations.ToDictionary(l => l.Id);
        var branches = (body.Branches ?? []).Select((b, i) =>
        {
            var merged = b;
            if (b.ExistingId is Guid id && branchById.TryGetValue(id, out var row))
            {
                var t = Merge(3, $"branches[{i}]", $"Branch {OrgCodes.Normalize(row.Code)}", $"الفرع {OrgCodes.Normalize(row.Code)}", b.Baseline,
                    CompanySetupBaseline.Branch(b, ex.PackTimeZone), CompanySetupBaseline.Branch(row, ex.PackTimeZone));
                merged = b with
                {
                    NameEn = t.Contains("nameEn") ? row.NameEn : b.NameEn,
                    NameAr = t.Contains("nameAr") ? row.NameAr : b.NameAr,
                    City = t.Contains("city") ? row.City : b.City,
                    TimeZoneId = t.Contains("timeZoneId") ? (string.IsNullOrWhiteSpace(row.TimeZoneId) ? ex.PackTimeZone : row.TimeZoneId.Trim()) : b.TimeZoneId,
                    IsHeadOffice = t.Contains("isHeadOffice") ? row.IsHeadOffice : b.IsHeadOffice,
                };
            }
            var locations = (b.Locations ?? []).Select((l, j) =>
            {
                if (l.ExistingId is not Guid lid || !locationById.TryGetValue(lid, out var lrow)) return l;
                var t = Merge(3, $"branches[{i}].locations[{j}]", $"Location {OrgCodes.Normalize(lrow.Code)}", $"الموقع {OrgCodes.Normalize(lrow.Code)}", l.Baseline,
                    CompanySetupBaseline.Location(l), CompanySetupBaseline.Location(lrow));
                return l with
                {
                    NameEn = t.Contains("nameEn") ? lrow.NameEn : l.NameEn,
                    NameAr = t.Contains("nameAr") ? lrow.NameAr : l.NameAr,
                    Latitude = t.Contains("latitude") ? lrow.Latitude : l.Latitude,
                    Longitude = t.Contains("longitude") ? lrow.Longitude : l.Longitude,
                    RadiusMeters = t.Contains("radiusMeters") ? lrow.GeofenceRadiusMeters : l.RadiusMeters,
                };
            }).ToList();
            return merged with { Locations = locations };
        }).ToList();

        // Cost centres (step 4).
        var costCentreById = ex.CostCentres.ToDictionary(x => x.Id);
        var costCentres = (body.CostCentres ?? []).Select((cc, i) =>
        {
            if (cc.ExistingId is not Guid id || !costCentreById.TryGetValue(id, out var row)) return cc;
            var t = Merge(4, $"costCentres[{i}]", $"Cost centre {OrgCodes.Normalize(row.Code)}", $"مركز التكلفة {OrgCodes.Normalize(row.Code)}", cc.Baseline,
                CompanySetupBaseline.CostCentre(cc), CompanySetupBaseline.CostCentre(row), Areas.CostCentre);
            return cc with { NameEn = t.Contains("nameEn") ? row.Name : cc.NameEn, NameAr = t.Contains("nameAr") ? row.NameAr : cc.NameAr };
        }).ToList();

        // Departments, designations and grades (step 4; Addendum B: the company's own rows, same decision table).
        var maps = ex.Maps;
        string? LinkNow(Guid? id, IReadOnlyDictionary<Guid, string> codes) => id is Guid x && codes.TryGetValue(x, out var code) ? code : null;
        var departmentById = ex.Org.Departments.Where(d => !d.IsDeleted).ToDictionary(d => d.Id);
        var departments = (body.Departments ?? []).Select((d, i) =>
        {
            if (d.ExistingId is not Guid id || !departmentById.TryGetValue(id, out var row)) return d;
            var code = OrgCodes.Normalize(row.Code);
            var t = Merge(4, $"departments[{i}]", $"Department {code}", $"القسم {code}", d.Baseline,
                CompanySetupBaseline.Department(d), CompanySetupBaseline.Department(row, maps.Departments, maps.Branches, maps.CostCentres));
            return d with
            {
                NameEn = t.Contains("nameEn") ? row.NameEn : d.NameEn,
                NameAr = t.Contains("nameAr") ? row.NameAr : d.NameAr,
                ParentCode = t.Contains("parentCode") ? LinkNow(row.ParentDepartmentId, maps.Departments) : d.ParentCode,
                BranchCode = t.Contains("branchCode") ? LinkNow(row.BranchId, maps.Branches) : d.BranchCode,
                CostCentreCode = t.Contains("costCentreCode") ? LinkNow(row.CostCenterId, maps.CostCentres) : d.CostCentreCode,
            };
        }).ToList();
        var designationById = ex.Org.Designations.Where(d => !d.IsDeleted).ToDictionary(d => d.Id);
        var designations = (body.Designations ?? []).Select((d, i) =>
        {
            if (d.ExistingId is not Guid id || !designationById.TryGetValue(id, out var row)) return d;
            var code = OrgCodes.Normalize(row.Code);
            var t = Merge(4, $"designations[{i}]", $"Designation {code}", $"المسمى الوظيفي {code}", d.Baseline,
                CompanySetupBaseline.Designation(d), CompanySetupBaseline.Designation(row, maps.Departments, maps.Grades));
            return d with
            {
                TitleEn = t.Contains("titleEn") ? row.TitleEn : d.TitleEn,
                TitleAr = t.Contains("titleAr") ? row.TitleAr : d.TitleAr,
                DepartmentCode = t.Contains("departmentCode") ? LinkNow(row.DepartmentId, maps.Departments) : d.DepartmentCode,
                GradeCode = t.Contains("gradeCode") ? LinkNow(row.GradeId, maps.Grades) : d.GradeCode,
            };
        }).ToList();
        var gradeById = ex.Org.Grades.Where(g => !g.IsDeleted).ToDictionary(g => g.Id);
        var grades = (body.Grades ?? []).Select((g, i) =>
        {
            if (g.ExistingId is not Guid id || !gradeById.TryGetValue(id, out var row)) return g;
            var code = OrgCodes.Normalize(row.Code);
            var t = Merge(4, $"grades[{i}]", $"Grade {code}", $"الدرجة {code}", g.Baseline, CompanySetupBaseline.Grade(g), CompanySetupBaseline.Grade(row));
            return g with
            {
                NameEn = t.Contains("nameEn") ? row.Name : g.NameEn,
                NameAr = t.Contains("nameAr") ? row.NameAr : g.NameAr,
                Level = t.Contains("level") ? row.Level : g.Level,
                MinSalary = t.Contains("minSalary") ? row.MinSalary : g.MinSalary,
                MidSalary = t.Contains("midSalary") ? row.MidSalary : g.MidSalary,
                MaxSalary = t.Contains("maxSalary") ? row.MaxSalary : g.MaxSalary,
            };
        }).ToList();

        // Employee numbering and the working week (step 5).
        var rules = body.Rules;
        if (rules is not null)
        {
            if (rules.EmployeeNumbering is { } n)
            {
                var t = Merge(5, "rules.employeeNumbering", "Employee numbering", "ترقيم الموظفين", n.Baseline,
                    CompanySetupBaseline.Numbering(n), CompanySetupBaseline.Numbering(ex.Rule));
                if (ex.Rule is { } r)
                    n = n with
                    {
                        Prefix = t.Contains("prefix") ? r.CompanyPrefix : n.Prefix,
                        UseCountryPrefix = t.Contains("useCountryPrefix") ? r.UseCountryPrefix : n.UseCountryPrefix,
                        UseBranchPrefix = t.Contains("useBranchPrefix") ? r.UseBranchPrefix : n.UseBranchPrefix,
                        UseDepartmentPrefix = t.Contains("useDepartmentPrefix") ? r.UseDepartmentPrefix : n.UseDepartmentPrefix,
                        UseYear = t.Contains("useYear") ? r.UseYear : n.UseYear,
                        PaddingLength = t.Contains("paddingLength") ? r.PaddingLength : n.PaddingLength,
                        NextSequence = t.Contains("nextSequence") ? r.NextSequence : n.NextSequence,
                    };
                rules = rules with { EmployeeNumbering = n };
            }
            if (rules.WorkWeek is { } w)
            {
                var t = Merge(5, "rules.workWeek", "Working week", "أسبوع العمل", w.Baseline,
                    CompanySetupBaseline.WorkWeek(w), CompanySetupBaseline.WorkWeek(ex.WorkWeek.WeekendDays, ex.WorkWeek.Own));
                rules = rules with
                {
                    WorkWeek = w with
                    {
                        WeekendDays = t.Contains("weekendDays") ? ex.WorkWeek.WeekendDays : w.WeekendDays,
                        Override = t.Contains("override") ? ex.WorkWeek.Own : w.Override,
                    },
                };
            }
        }

        if (refresh)
        {
            c.Problems.Clear();
            c.Problem(1, "draft", CompanySetupBaseline.RefreshCode,
                "This draft was saved before KynexOne recorded what the company had when the draft started, so it cannot tell your edits from changes made elsewhere since. Reload the company's current setup, then make your changes again. Nothing has been changed.",
                "حُفظت هذه المسودة قبل أن يسجّل النظام بيانات الشركة عند بدئها، لذا لا يمكن التمييز بين تعديلاتك والتغييرات التي تمت في مكان آخر. أعد تحميل إعداد الشركة الحالي ثم أجرِ تعديلاتك مرة أخرى. لم يتم تغيير أي شيء.");
            return null;
        }
        return body with
        {
            Company = co, Branches = branches, CostCentres = costCentres, Departments = departments, Designations = designations, Grades = grades,
            Rules = rules!,
        };
    }

    private const string KeptReason = CompanySetupReasons.Kept;
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Step 6. The admin hierarchy is Group Super Admin (Admin role, group scope) → Company Super Admin (Admin role,
    /// this company only). In KynexOne a role is held workspace-wide and company access is a separate list, so
    /// "Admin of this company only" is exactly: the Admin role + a single selected-company grant + no group scope.
    /// Existing-company mode (<paramref name="existingCompanyId"/>): a company super admin may add another
    /// company-scoped admin for their own company (Addendum A5) — never group scope; "groupOnly" changes nothing.
    /// </summary>
    private async Task<string> PlanCompanyAdminAsync(Collector c, CompanySetupCaller caller, CompanySetupCompanyAdmin admin,
        string companyEmailDomain, AccessCeilingDto? ceiling, Guid? existingCompanyId, CancellationToken ct)
    {
        var mode = (admin.Mode ?? string.Empty).Trim();
        if (mode == V.AdminGroupOnly)
        {
            if (existingCompanyId is not null)
            {
                c.Item(Areas.CompanyAdmin, V.AdminGroupOnly, "No change to this company's administrators", V.ActionUnchanged, labelAr: "لا تغيير على مسؤولي هذه الشركة");
                return V.ActionUnchanged;
            }
            c.Item(Areas.CompanyAdmin, V.AdminGroupOnly, "Group super admins will manage this company", V.ActionReuse, labelAr: "سيدير مسؤولو المجموعة هذه الشركة");
            return V.ActionReuse;
        }
        if (mode is not (V.AdminInvite or V.AdminExisting))
        {
            c.Problem(6, "companyAdmin.mode", "mode_invalid", "Choose how this company will be administered.");
            return V.ActionConflict;
        }

        // Only an Admin who may assign the Admin role grants it — the ceiling User Management enforces — and, on a new
        // company, only from group scope.
        var adminRole = ceiling?.Roles.FirstOrDefault(r => string.Equals(r.Name, "Admin", StringComparison.OrdinalIgnoreCase));
        var mayGrant = MayGrantCompanyAdmin(caller, ceiling, existingCompany: existingCompanyId is not null);
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
            .Include(u => u.EntityAccesses)
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
                $"{user.FullName} has group-wide access without the Admin role. Making them Admin would make them a GROUP super admin, not this company's. Invite a company super admin instead.",
                $"لدى {user.FullName} صلاحية على مستوى المجموعة دون دور المسؤول. منحه دور المسؤول يجعله مسؤولاً أعلى للمجموعة لا لهذه الشركة. ادعُ مسؤولاً أعلى للشركة بدلاً من ذلك.");
            return V.ActionConflict;
        }
        if (!holdsAdmin)
        {
            c.Conflict(Areas.CompanyAdmin, user.Id.ToString(), user.FullName, 6, "companyAdmin.existingUserId", "existing_user_not_admin",
                $"{user.FullName} does not hold the Admin role. Roles apply to every company a user can access, so granting Admin would also make them Admin of their other companies. Invite a new company super admin, or choose a user who is already a company admin.",
                "الأدوار تنطبق على جميع الشركات التي يصل إليها المستخدم. ادعُ مسؤولاً جديداً للشركة بدلاً من ذلك.");
            return V.ActionConflict;
        }
        if (existingCompanyId is Guid target && user.EntityAccesses.Any(e => e.IsActive && e.CompanyId == target))
        {
            c.Item(Areas.CompanyAdmin, user.Id.ToString(), user.FullName, V.ActionUnchanged, "Already a super admin of this company.");
            return V.ActionUnchanged;
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

    /// <summary>Whether <paramref name="caller"/> may make someone a company super admin: the Admin role, the Admin role
    /// assignable under the privilege ceiling, and security.manage (User Management's own gate); on a new company also
    /// group scope. For an existing company (<paramref name="existingCompany"/>) a company super admin qualifies, for
    /// the company they were already authorised for (Addendum A5).</summary>
    public static bool MayGrantCompanyAdmin(CompanySetupCaller caller, AccessCeilingDto? ceiling, bool existingCompany = false) =>
        (caller.IsGroupScope || existingCompany) && ceiling is { IsAdmin: true }
        && ceiling.Roles.Any(r => string.Equals(r.Name, "Admin", StringComparison.OrdinalIgnoreCase) && r.CanAssign)
        && ceiling.HeldPermissions.Contains("security.manage", StringComparer.Ordinal);

    private static CompanySetupPlan Finish(Collector c, string? focus = null) =>
        new(focus is null
                ? c.Problems.Count == 0 && c.Items.All(i => i.Action != V.ActionConflict)
                : !c.Problems.Any(p => Blocks(p, focus)) && c.Items.All(i => i.Action != V.ActionConflict || i.Area != V.Sections.AreaOf(focus)),
            c.Items, c.Problems, c.Warnings);

    /// <summary>B2: whether a problem blocks a live save of <paramref name="section"/>: the step-1 gates, and that section's own fields.</summary>
    public static bool Blocks(Problem p, string section) =>
        p.Step == 1 || p.Field == section || p.Field.StartsWith(section + ".", StringComparison.Ordinal);

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
    /// <summary>Addendum A5: a company-scoped caller asked for a company that is not theirs.</summary>
    public const string CompanyNotManageableCode = "company_not_manageable";
    /// <summary>Addendum A5: a company-scoped caller without the Admin role (e.g. a company-scoped HR Manager).</summary>
    public const string CompanyAdminRequiredCode = "company_admin_required";
    /// <summary>Addendum A1: one open draft per existing company; the error carries the open draft's id.</summary>
    public const string ExistingDraftOpenCode = "existing_company_draft_open";
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
