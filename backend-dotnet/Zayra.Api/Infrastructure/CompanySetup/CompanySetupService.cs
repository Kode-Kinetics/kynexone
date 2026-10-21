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

    private readonly ZayraDbContext _db;
    private readonly IOrganizationSetupService _organization;
    private readonly IAccessManagementService _access;
    private readonly IEmailService _email;
    private readonly IWorkWeekService _workWeek;

    public CompanySetupService(ZayraDbContext db, IOrganizationSetupService organization, IAccessManagementService access,
        IWorkWeekService workWeek, IEmailService email)
    {
        _db = db;
        _organization = organization;
        _access = access;
        _workWeek = workWeek;
        _email = email;
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
        var canGrantCompanyAdmin = false;
        if (caller.IsGroupScope)
        {
            // The same role ceiling User Management applies (and the planner's step 6 uses).
            try
            {
                var ceiling = await _access.GetAccessCeilingAsync(tenantId, caller.Context with { TenantId = tenantId }, ct);
                canGrantCompanyAdmin = CompanySetupPlanner.MayGrantCompanyAdmin(caller, ceiling);
            }
            catch (PrivilegeCeilingException) { canGrantCompanyAdmin = false; }
        }

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
            tenant?.CompanyCreationMode ?? string.Empty,
            tenant?.AccountType ?? string.Empty,
            used,
            sub is { MaxCompanies: > 0 } ? sub.MaxCompanies : null,
            caller.IsGroupScope,
            sisters,
            new CompanySetupSharedCatalog(departments, designations, grades),
            gradesCurrency,
            canGrantCompanyAdmin);
    }

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

    // ── Drafts ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CompanySetupDraftSummary>> ListDraftsAsync(CompanySetupCaller caller, CancellationToken ct)
    {
        var rows = await _db.CompanySetupDrafts.AsNoTracking()
            .Where(d => d.TenantId == caller.TenantId && d.Status == CompanySetupDraftStatuses.Open)
            .OrderByDescending(d => d.UpdatedAtUtc).Take(50).ToListAsync(ct);
        var names = await NamesAsync(caller.TenantId, rows.Select(r => r.CreatedByUserId), ct);
        return rows.Select(r => new CompanySetupDraftSummary(r.Id, ReadBody(r)?.Company?.LegalNameEn ?? string.Empty, r.Step, r.UpdatedAtUtc,
            names.GetValueOrDefault(r.CreatedByUserId, string.Empty))).ToList();
    }

    public async Task<CompanySetupDraft?> GetDraftAsync(CompanySetupCaller caller, Guid id, CancellationToken ct)
    {
        var row = await _db.CompanySetupDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == caller.TenantId && d.Id == id, ct);
        return row is null ? null : await ToDtoAsync(row, ct);
    }

    public async Task<CompanySetupOutcome<CompanySetupDraft>> CreateDraftAsync(CompanySetupCaller caller, CompanySetupDraftBody? body, CancellationToken ct)
    {
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

    public async Task<CompanySetupOutcome<CompanySetupDraft>> UpdateDraftAsync(CompanySetupCaller caller, Guid id, CompanySetupDraftUpdate? update, CancellationToken ct)
    {
        if (update?.Body is null) return CompanySetupOutcome<CompanySetupDraft>.Fail(400, "body_required", "The draft body is required.");
        if (update.Step is < 1 or > 7) return CompanySetupOutcome<CompanySetupDraft>.Fail(400, "step_invalid", "The step is 1 to 7.", field: "step");
        var row = await _db.CompanySetupDrafts.SingleOrDefaultAsync(d => d.TenantId == caller.TenantId && d.Id == id, ct);
        if (row is null) return CompanySetupOutcome<CompanySetupDraft>.Fail(404, "draft_not_found", "Draft not found.");
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
        if (row is null) return CompanySetupOutcome<bool>.Fail(404, "draft_not_found", "Draft not found.");
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
        if (idempotencyKey is null || idempotencyKey != request.DraftId)
            return CompanySetupOutcome<CompanySetupResult>.Fail(400, "idempotency_key_mismatch", "Send the draft id as the Idempotency-Key header.", field: "draftId");

        var tenantId = caller.TenantId;
        CompanySetupOutcome<CompanySetupResult>? refusal = null;
        CompanySetupResult? result = null;
        CompanySetupPlanModel? executed = null;

        async Task RunAsync()
        {
            _db.ChangeTracker.Clear();
            refusal = null; result = null; executed = null;
            await SetupTenantLock.AcquireAsync(_db, tenantId, ct);
            var draft = await _db.CompanySetupDrafts.SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == request.DraftId, ct);
            if (draft is null) { refusal = CompanySetupOutcome<CompanySetupResult>.Fail(404, "draft_not_found", "Draft not found."); return; }
            if (draft.Status == CompanySetupDraftStatuses.Submitted)
            {
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

            var companyId = await ExecuteAsync(caller, model, ct);
            var (created, reused) = Counts(plan.Items);
            var now = DateTime.UtcNow;
            draft.Status = CompanySetupDraftStatuses.Submitted;
            draft.ResultCompanyId = companyId;
            draft.SubmittedAtUtc = now;
            draft.UpdatedAtUtc = now;
            draft.Step = 7;
            var marker = AuthAuditEntry.Create(Guid.NewGuid(), now, SetupCreatedAction, nameof(CompanySetupDraftRecord), draft.Id.ToString(),
                caller.Context with { TenantId = tenantId },
                JsonSerializer.Serialize(new SetupCreatedMarker(draft.Id, companyId, created, reused,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Body))).ToLowerInvariant(),
                    model.CompanyAdmin.Mode, model.CompanyAdminAction), BodyJson));
            marker.CompanyId = companyId;
            _db.AuditLogs.Add(marker);
            await _db.SaveChangesAsync(ct);
            executed = model;
            result = new CompanySetupResult(companyId, created, reused, [], Replayed: false);
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

    private sealed record SetupCreatedMarker(Guid DraftId, Guid CompanyId, IReadOnlyDictionary<string, int> Created,
        IReadOnlyDictionary<string, int> Reused, string BodySha256, string CompanyAdminMode, string CompanyAdminAction);

    private async Task<CompanySetupResult?> ReplayAsync(CompanySetupDraftRecord draft, CancellationToken ct)
    {
        if (draft.ResultCompanyId is not Guid companyId) return null;
        var markerJson = await ScopedBypass.NullableTenantWide(_db.AuditLogs, draft.TenantId,
                "Replay of a committed create-company draft: its own commit marker, found by the draft id.")
            .AsNoTracking()
            .Where(a => a.Action == SetupCreatedAction && a.EntityId == draft.Id.ToString())
            .OrderBy(a => a.CreatedAtUtc).Select(a => a.Metadata).FirstOrDefaultAsync(ct);
        var marker = markerJson is null ? null : JsonSerializer.Deserialize<SetupCreatedMarker>(markerJson, BodyJson);
        return new CompanySetupResult(companyId,
            marker?.Created ?? new Dictionary<string, int>(), marker?.Reused ?? new Dictionary<string, int>(), [], Replayed: true);
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

        // Decision 2: the ONE authoritative create path, with all its validations, inside this transaction.
        var company = await _organization.CreateCompanyAsync(tenantId, m.CompanyRequest, ctx, ct, asDraft: false);
        var companyId = company.Id;

        var branchIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        Guid? headOffice = null;
        foreach (var b in m.Branches)
        {
            var dto = await _organization.CreateBranchAsync(tenantId, new BranchRequest(companyId, b.Code, b.NameEn.Trim(), b.NameAr?.Trim(),
                m.Country, b.City.Trim(), null, null, b.TimeZoneId, null, b.IsHeadOffice), ctx, ct);
            branchIds[b.Code] = dto.Id;
            if (b.IsHeadOffice) headOffice = dto.Id;
            foreach (var l in b.Locations ?? [])
            {
                var location = new Location
                {
                    TenantId = tenantId, BranchId = dto.Id, Code = l.Code, NameEn = l.NameEn.Trim(), NameAr = l.NameAr?.Trim() ?? string.Empty,
                    City = b.City.Trim(), CountryCode = m.Country, Latitude = l.Latitude, Longitude = l.Longitude,
                    GeofenceRadiusMeters = l.RadiusMeters, IsActive = true, CreatedBy = caller.UserId,
                };
                _db.Locations.Add(location);
                Audit("organization.location_created", nameof(Location), location.Id, companyId, new { l.Code, branch = b.Code });
            }
        }
        await _db.SaveChangesAsync(ct);

        var costCentreIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var cc in m.CostCentres)
            costCentreIds[cc.Code] = (await _organization.CreateCostCenterAsync(tenantId, new CostCenterRequest(companyId, cc.Code, cc.NameEn.Trim()), ctx, ct)).Id;

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

        var (rule, _) = await SetupWriters.UpsertEmployeeIdRuleAsync(_db, tenantId, companyId, m.EmployeeIdRule, caller.UserId, ct);
        Audit("organization.employee_id_rule_created", nameof(EmployeeIdRule), rule.Id, companyId, m.EmployeeIdRule);

        var cutover = new CompanyCutover
        {
            TenantId = tenantId, CompanyId = companyId, CutoverDate = m.CutoverDate, Status = CutoverStatuses.Planned,
            Notes = "Planned by the create-company wizard: the first payroll month.", CreatedBy = caller.UserId,
        };
        _db.CompanyCutovers.Add(cutover);
        Audit("organization.company_cutover_planned", nameof(CompanyCutover), cutover.Id, companyId, new { cutoverDate = m.CutoverDate });

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
        try
        {
            if (admin.Mode == V.AdminInvite)
            {
                if (await UserSeatLimit.CheckAsync(_db, caller.TenantId, ct) is { } seats) failure = seats.Message;
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
        }
        if (failure is null) return null;

        _db.ChangeTracker.Clear();
        var row = AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, "company.setup_admin_failed", nameof(Company), companyId.ToString(), ctx,
            JsonSerializer.Serialize(new { mode = admin.Mode, reason = failure }, BodyJson));
        row.CompanyId = companyId;
        _db.AuditLogs.Add(row);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* the readiness item still shows the admin as missing */ }
        return failure;
    }

    // ── GET /readiness/{companyId} ───────────────────────────────────────────────────────────────────

    /// <summary>The server-computed checklist. Null when the company is not in this tenant.</summary>
    public async Task<IReadOnlyList<ReadinessItem>?> ReadinessAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var company = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (company is null) return null;
        return await CompanySetupReadiness.ComputeAsync(_db, _email, tenantId, company, ct);
    }
}
