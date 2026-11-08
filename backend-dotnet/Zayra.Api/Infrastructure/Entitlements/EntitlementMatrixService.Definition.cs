using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

/// <summary>
/// Benefit definition (feature <c>benefit_definition_v1</c>, default OFF): the richer Add benefit contract and the edit of a customer-added benefit.
///
/// <para><b>One authority.</b> A definition lives on the tenant <c>pay_components</c> row (<c>BEN_*</c> / <c>ALW_*</c>), never on a second row: the
/// legacy benefit plan is read-only history. The extra facts are four additive nullable columns (category, policy class, requires-enrolment which is
/// always false, source plan) plus the row's existing effective window and active switch, which are now <i>consulted</i>, not just stored:
/// the matrix read, the publish and the entitlement resolver treat a benefit outside its window, or switched off, as not offered.</para>
///
/// <para><b>Nothing is stored and ignored.</b> A delivery the Release A engine cannot execute (reimbursement, payroll deduction, an annual or one-time
/// allowance), a per-benefit enrolment, a mandatory class, and any JSON field this contract does not know are refused with a plain code.</para>
///
/// <para><b>Money.</b> A cash allowance stays all-companies, monthly, Finance-confirmed (maker is not checker). Its GL driver and end-of-service setting
/// of a <i>confirmed</i> allowance change only through Finance's effective-dated correction; its dates, active switch and policy class change here, and
/// when someone without the Finance permission changes them the allowance is pending Finance again, with that person as its maker.</para>
/// </summary>
public sealed partial class EntitlementMatrixService
{
    /// <summary>Code prefixes the customer can never take (statutory components and loans). Checked on the part the customer types.</summary>
    public static readonly IReadOnlyList<string> ReservedCodePrefixes = ["STATUTORY_", "GOSI", "GPSSA", "GRSIA", "SANED", "LOAN_"];
    public const int CategoryMaxLength = 120;
    /// <summary>How long a request id is honoured for replay (the audit rows read are the actor's most recent ones).</summary>
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromDays(2);
    private const int ReplayCandidates = 200;
    public const string DeliveryCoverage = "Coverage";
    public const string DeliverySalaryAllowance = "SalaryAllowance";

    /// <summary>Whether the tenant has the benefit definition feature on. Opt-in: no row means off (<c>OptInFeatures</c>).</summary>
    public static Task<bool> BenefitDefinitionEnabledAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) =>
        db.TenantFeatureFlags.AsNoTracking()
            .AnyAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.BenefitDefinitionV1 && f.IsEnabled, ct);

    /// <summary>Whether a customer-added benefit is offered on a date: some tenant-wide version of it is switched on and in its window.
    /// Used by the resolver and the grade standard; the caller passes only that code's tenant-wide rows and has checked the feature.</summary>
    internal static bool CustomOfferedOn(IEnumerable<PayComponent> tenantWideRows, DateOnly asOf) =>
        tenantWideRows.Any(r => r.IsActive && !r.IsDeleted && r.IsInEffect(asOf));

    // ── Small refusals ───────────────────────────────────────────────────────────────────────────

    private static MatrixResult FeatureNotEnabled() => new(StatusCodes.Status403Forbidden, new
    {
        error = "feature_not_enabled",
        message = "Benefit definition isn't switched on for your company yet.",
    });

    private static MatrixResult UnrecognisedField(IEnumerable<string> names) => Bad("unrecognised_field",
        "These fields aren't part of the benefit contract and were not saved: "
        + string.Join(", ", names.Take(5).Select(n => n.Length > 40 ? n[..40] : n)) + ".");

    private static MatrixResult NotFound() =>
        new(StatusCodes.Status404NotFound, new { error = "unknown_component", message = "This benefit isn't in the catalogue." });

    // ── Pure validation (no database) ────────────────────────────────────────────────────────────

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Which kind a create asks for, from <c>kind</c> and <c>delivery</c> (which must agree when both are given), with the deliveries
    /// Release A cannot execute refused out loud.</summary>
    private static (MatrixResult? Problem, bool Cash) ResolveKind(string? kind, string? delivery, string? frequency, bool? requiresEnrolment)
    {
        if (requiresEnrolment == true)
            return (Bad("delivery_not_supported", "Employees get a benefit by grade, so it can't require enrolment."), false);
        kind = Blank(kind); delivery = Blank(delivery); frequency = Blank(frequency);
        bool? byDelivery = null;
        if (delivery is not null)
        {
            if (string.Equals(delivery, DeliveryCoverage, StringComparison.OrdinalIgnoreCase)) byDelivery = false;
            else if (string.Equals(delivery, DeliverySalaryAllowance, StringComparison.OrdinalIgnoreCase)) byDelivery = true;
            else if (string.Equals(delivery, "Reimbursement", StringComparison.OrdinalIgnoreCase) || string.Equals(delivery, "PayrollDeduction", StringComparison.OrdinalIgnoreCase))
                return (Bad("delivery_not_supported", "Reimbursements and payroll deductions can't be set up here yet. A benefit is either coverage or a monthly cash allowance."), false);
            else return (Bad("delivery_invalid", "A benefit is either coverage or a monthly cash allowance."), false);
        }
        bool? byKind = null;
        if (kind is not null)
        {
            if (string.Equals(kind, BenefitKinds.Cash, StringComparison.OrdinalIgnoreCase)) byKind = true;
            else if (string.Equals(kind, BenefitKinds.Contract, StringComparison.OrdinalIgnoreCase)) byKind = false;
            else return (Bad("kind_invalid", "A benefit is either a contract benefit or a monthly cash allowance."), false);
        }
        if (byDelivery.HasValue && byKind.HasValue && byDelivery != byKind)
            return (Bad("kind_delivery_mismatch", "The kind and the delivery describe different benefits."), false);
        var cash = byDelivery ?? byKind ?? false;
        if (frequency is not null)
        {
            if (!cash) return (Bad("frequency_not_applicable", "Coverage has no pay frequency."), false);
            if (string.Equals(frequency, "Annual", StringComparison.OrdinalIgnoreCase) || string.Equals(frequency, "OneTime", StringComparison.OrdinalIgnoreCase))
                return (Bad("delivery_not_supported", "Only a monthly cash allowance can be set up here. Annual and one-time payments aren't supported yet."), false);
            if (!string.Equals(frequency, "Monthly", StringComparison.OrdinalIgnoreCase))
                return (Bad("frequency_invalid", "A cash allowance is paid monthly."), false);
        }
        return (null, cash);
    }

    private static (MatrixResult? Problem, string? Category) CheckCategory(string? raw)
    {
        var category = Blank(raw);
        if (category is null) return (null, null);
        if (category.Length > CategoryMaxLength) return (Bad("category_too_long", $"Keep the category under {CategoryMaxLength} characters."), null);
        if (CashAllowanceDefinitions.HasUnsafeCharacters(category)) return (Bad("category_invalid", "The category can't contain control or text-direction override characters."), null);
        return (null, category);
    }

    private static (MatrixResult? Problem, string? PolicyClass) CheckPolicyClass(string? raw)
    {
        var value = Blank(raw);
        if (value is null) return (null, null);
        return BenefitPolicyClasses.Normalize(value) is { } normal
            ? (null, normal)
            : (Bad("policy_class_invalid", "The policy class is Contractual or Discretionary. A mandatory benefit is a statutory requirement, not one you add."), null);
    }

    /// <summary>The code the customer typed, without its prefix: ASCII letters, digits and underscores starting with a letter; never a reserved prefix;
    /// the finished code within the length limit. Null part = nothing typed.</summary>
    private static (MatrixResult? Problem, string? Part) CheckCode(string? raw, string prefix)
    {
        var typed = Blank(raw);
        if (typed is null) return (null, null);
        foreach (var ch in typed)
            if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                return (Bad("code_invalid", "A code uses letters, digits and underscores only."), null);
        var upper = typed.ToUpperInvariant();
        var other = prefix == EntitlementComponentRules.AllowancePrefix ? EntitlementComponentRules.CustomPrefix : EntitlementComponentRules.AllowancePrefix;
        if (upper.StartsWith(other, StringComparison.Ordinal))
            return (Bad("code_invalid", $"Enter the code without the {other} prefix; the right prefix is added for you."), null);
        if (upper.StartsWith(prefix, StringComparison.Ordinal)) upper = upper[prefix.Length..];
        if (upper.Length == 0 || !(upper[0] is >= 'A' and <= 'Z'))
            return (Bad("code_invalid", "A code starts with a letter."), null);
        if (ReservedCodePrefixes.Any(r => upper.StartsWith(r, StringComparison.Ordinal)))
            return (Bad("code_reserved", "This code is reserved for statutory components and loans. Choose another."), null);
        var max = prefix == EntitlementComponentRules.AllowancePrefix ? EntitlementComponentRules.AllowanceCodeMaxLength : EntitlementComponentRules.CustomCodeMaxLength;
        if (prefix.Length + upper.Length > max)
            return (Bad("code_too_long", $"The code, with its {prefix} prefix, can be at most {max} characters."), null);
        return (null, upper);
    }

    private static (MatrixResult? Problem, bool Ok) CheckWindowShape(DateOnly? from, DateOnly? to)
    {
        if (from is { Day: not 1 }) return (Bad("effective_from_not_month_start", "A benefit starts on the first day of a month."), false);
        if (to is DateOnly t && t != new DateOnly(t.Year, t.Month, DateTime.DaysInMonth(t.Year, t.Month)))
            return (Bad("effective_to_not_month_end", "A benefit ends on the last day of a month."), false);
        if (from is DateOnly f && to is DateOnly e && e < f) return (Bad("effective_range_invalid", "The end can't be before the start."), false);
        return (null, true);
    }

    /// <summary>A date change may not reach into a payroll period any company has approved, locked or closed: the start must be after it, and an end
    /// can't cut a month already paid. Only the dates being changed are checked.</summary>
    private async Task<MatrixResult?> WindowLockProblemAsync(Guid tid, DateOnly? from, DateOnly? to, bool checkFrom, bool checkTo, CancellationToken ct)
    {
        if (!(checkFrom && from.HasValue) && !(checkTo && to.HasValue)) return null;
        if (await PayComponentPolicy.LatestCommittedPeriodAsync(db, tid, null, ct) is not DateOnly committed) return null;
        var committedEnd = committed.AddMonths(1).AddDays(-1);
        if ((checkFrom && from is DateOnly f && f <= committed) || (checkTo && to is DateOnly t && t <= committedEnd))
            return new MatrixResult(StatusCodes.Status409Conflict, new
            {
                error = "effective_in_locked_period",
                message = $"Payroll for {committed:yyyy-MM} is already approved, locked or paid for at least one company. A benefit can only start after it and can't end before it ends.",
                earliestEffectiveFrom = committed.AddMonths(1),
            });
        return null;
    }

    // ── Scope ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The companies that do NOT offer the benefit, from a scope. Null = every company offers it. A refusal when a company is not the
    /// tenant's, when the scope is empty, or when a cash allowance (never skippable per company) is scoped to a subset.</summary>
    private async Task<(MatrixResult? Problem, List<Guid>? Excluded)> ExcludedCompaniesAsync(Guid tid, BenefitScopeInput? scope, bool cash, DateOnly? from, CancellationToken ct)
    {
        if (scope?.CompanyIds is not { } ids) return (null, null);
        if (ids.Count == 0) return (Bad("scope_empty", "Choose at least one company, or leave the scope as all companies."), null);
        var chosen = ids.Distinct().ToList();
        foreach (var id in chosen)
            if (id == Guid.Empty || !await db.Companies.AnyAsync(c => c.TenantId == tid && c.Id == id && !c.IsDeleted, ct))
                return (Bad("company_not_found", "One of the companies doesn't exist."), null);
        var excluded = (await ActiveCompanyIdsAsync(tid, ct)).Where(id => !chosen.Contains(id)).ToList();
        if (excluded.Count == 0) return (null, null);
        if (cash) return (Bad("scope_not_supported_for_cash", "A cash allowance is paid in salary, so it is offered to every company. Choose all companies."), null);
        if (from is null) return (Bad("effective_from_required_for_scope", "Say the month the benefit starts, so the companies that don't offer it can skip it from then."), null);
        return (null, excluded);
    }

    private static void FillSkipMarker(PayComponent marker, PayComponent template, DateOnly from, Guid? userId)
    {
        marker.NameEn = template.NameEn; marker.NameAr = template.NameAr; marker.ComponentType = template.ComponentType;
        marker.CalcMethod = template.CalcMethod; marker.Value = null; marker.DisplayOrder = template.DisplayOrder;
        marker.EntitlementClass = template.EntitlementClass; marker.StatutoryFloor = template.StatutoryFloor;
        marker.WpsIncluded = false; marker.IsSystem = false; marker.IsActive = true; marker.IsDeleted = false;
        marker.IsOffered = false; marker.EffectiveFrom = from; marker.EffectiveTo = null;
        marker.UpdatedAtUtc = DateTime.UtcNow; marker.UpdatedBy = userId;
    }

    // ── Reading a definition ─────────────────────────────────────────────────────────────────────

    /// <summary>What a screen needs beyond the rows: every active company, the not-offered company rows of the codes, and which codes are in use.</summary>
    private sealed record DefinitionContext(List<Guid> ActiveCompanyIds, ILookup<string, PayComponent> Markers, HashSet<string> UsedCodes);

    private async Task<DefinitionContext> DefinitionContextAsync(Guid tid, IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        var active = await ActiveCompanyIdsAsync(tid, ct);
        if (codes.Count == 0) return new(active, Enumerable.Empty<PayComponent>().ToLookup(x => x.Code), []);
        var markers = (await ScopedBypass.TenantWide(db.PayComponents, tid, "Which companies a customer-added benefit is not offered to: its not-offered company rows.")
                .AsNoTracking().Where(x => x.CompanyId != null && !x.IsDeleted && !x.IsOffered && codes.Contains(x.Code)).ToListAsync(ct))
            .ToLookup(x => x.Code, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        used.UnionWith(await ScopedBypass.TenantWide(db.GradeEntitlements, tid, "A code with any grade value, in any version and scope, can't be renamed.")
            .AsNoTracking().Where(x => codes.Contains(x.PayComponentCode)).Select(x => x.PayComponentCode).Distinct().ToListAsync(ct));
        used.UnionWith(await ScopedBypass.TenantWide(db.EmployeeEntitlements, tid, "A code in any employee's package can't be renamed.")
            .AsNoTracking().Where(x => codes.Contains(x.PayComponentCode)).Select(x => x.PayComponentCode).Distinct().ToListAsync(ct));
        used.UnionWith(await db.EmployeeSalaryAllowances.AsNoTracking().Where(x => x.TenantId == tid && codes.Contains(x.Code)).Select(x => x.Code).Distinct().ToListAsync(ct));
        return new(active, markers, used);
    }

    /// <summary>The token an edit must present: changes whenever any live version of the definition does. Microsecond resolution, because that is
    /// what the database keeps.</summary>
    internal static string VersionOf(IEnumerable<PayComponent> versions)
    {
        var latest = versions.Select(v => v.UpdatedAtUtc ?? v.CreatedAtUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        return ((latest.Ticks - DateTime.UnixEpoch.Ticks) / 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static BenefitDefinitionDto DefinitionOf(PayComponent shown, IReadOnlyCollection<PayComponent> versions, DefinitionContext ctx, DateOnly today)
    {
        var cash = EntitlementComponentRules.IsAllowance(shown.Code);
        var offered = cash
            ? ctx.ActiveCompanyIds
            : ctx.ActiveCompanyIds.Where(id => !ctx.Markers[shown.Code].Any(m => m.CompanyId == id && (m.EffectiveTo is null || m.EffectiveTo >= today))).ToList();
        return new BenefitDefinitionDto(
            cash ? DeliverySalaryAllowance : DeliveryCoverage, shown.BenefitCategory, shown.PolicyClass, shown.EffectiveFrom, shown.EffectiveTo, shown.IsActive,
            shown.RequiresEnrolment ?? false, offered, VersionOf(versions), shown.SourceBenefitPlanId, ctx.UsedCodes.Contains(shown.Code));
    }

    /// <summary>The finished component (rule, cash state, definition) of one customer-added code as it stands now, inactive ones included. Null when there is none.</summary>
    private async Task<MatrixComponentDto?> ComponentDtoAsync(Guid tid, string code, IReadOnlyList<AllowanceWarningDto>? warnings, CancellationToken ct)
    {
        var cash = EntitlementComponentRules.IsAllowance(code);
        var query = ScopedBypass.TenantWide(db.PayComponents, tid, "The tenant-wide definition rows of one customer-added benefit.")
            .AsNoTracking().Where(x => x.CompanyId == null && x.Code == code && !x.IsDeleted);
        query = cash
            ? query.Where(x => x.ComponentType == PayComponentTypes.Earning && x.CalcMethod == PayComponentCalcMethods.GradePackage)
            : query.Where(x => x.ComponentType == PayComponentTypes.Benefit);
        var versions = (await query.ToListAsync(ct)).OrderBy(x => x.EffectiveFrom ?? DateOnly.MinValue).ToList();
        if (versions.Count == 0) return null;
        var today = await clock.TodayAsync(tid, ct);
        var shown = CashAllowanceDefinitions.Current(versions, today) ?? versions[0];
        var ctx = await DefinitionContextAsync(tid, [code], ct);
        var rule = EntitlementComponentRules.CustomRuleFor(code, shown.NameEn, shown.NameAr);
        return ToDto(rule, cash ? InfoOf(shown) : null, cash ? warnings ?? CashAllowanceDefinitions.Warnings(shown.NameEn, shown.NameAr, shown.EosbIncluded, shown.EosbExclusionCategory, atCreation: false) : null,
            DefinitionOf(shown, versions, ctx, today));
    }

    // ── Replay ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The audit metadata of the actor's own earlier request with this request id (recent ones only), or null. A request id is a replay key, never
    /// an authority: it finds only what the SAME person did in the SAME tenant.</summary>
    private async Task<JsonElement?> FindReplayAsync(MatrixActor actor, string action, Guid requestId, CancellationToken ct)
    {
        if (actor.UserId is not Guid user) return null;
        var since = DateTime.UtcNow - ReplayWindow;
        var candidates = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == actor.TenantId && a.UserId == user && a.Action == action && a.CreatedAtUtc >= since)
            .OrderByDescending(a => a.CreatedAtUtc).Take(ReplayCandidates).Select(a => a.Metadata).ToListAsync(ct);
        foreach (var metadata in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadata) ? "{}" : metadata);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("requestId", out var id)
                    && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) && parsed == requestId)
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { /* an unreadable audit row is no replay */ }
        }
        return null;
    }

    // ── Create: the definition part ──────────────────────────────────────────────────────────────

    /// <summary>The validated definition of a create, ready to store.</summary>
    private sealed record CreateDefinition(
        string? Category, string? PolicyClass, DateOnly? From, DateOnly? To, bool IsActive, string? UserCode, List<Guid>? ExcludedCompanies);

    private async Task<(MatrixResult? Problem, CreateDefinition? Input)> ParseCreateDefinitionAsync(
        MatrixActor actor, CreateBenefitRequest req, bool cash, CancellationToken ct)
    {
        var (categoryProblem, category) = CheckCategory(req.Category);
        if (categoryProblem is not null) return (categoryProblem, null);
        var (classProblem, policyClass) = CheckPolicyClass(req.PolicyClass);
        if (classProblem is not null) return (classProblem, null);
        var prefix = cash ? EntitlementComponentRules.AllowancePrefix : EntitlementComponentRules.CustomPrefix;
        var (codeProblem, part) = CheckCode(req.Code, prefix);
        if (codeProblem is not null) return (codeProblem, null);
        var (shapeProblem, _) = CheckWindowShape(req.EffectiveFrom, req.EffectiveTo);
        if (shapeProblem is not null) return (shapeProblem, null);
        var (scopeProblem, excluded) = await ExcludedCompaniesAsync(actor.TenantId, req.Scope, cash, req.EffectiveFrom, ct);
        if (scopeProblem is not null) return (scopeProblem, null);
        if (await WindowLockProblemAsync(actor.TenantId, req.EffectiveFrom, req.EffectiveTo, checkFrom: true, checkTo: true, ct) is { } locked) return (locked, null);
        return (null, new CreateDefinition(category, policyClass, req.EffectiveFrom, req.EffectiveTo, req.IsActive ?? true, part, excluded));
    }

    // ── Edit ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Edits the definition of a benefit or cash allowance the customer added (PUT /api/entitlements/components/{code}). Needs the feature, access to
    /// every company and the version the screen read (<see cref="UpdateBenefitRequest.ExpectedVersion"/>); serialised with every other matrix write, one
    /// save with its audit row. The kind and delivery never change; the code only while nothing uses it (contract benefits); the company scope is
    /// switched in the offerings. A cash allowance: see the class remarks.
    /// </summary>
    public async Task<MatrixResult> UpdateBenefitAsync(MatrixActor actor, string code, UpdateBenefitRequest req, CancellationToken ct)
    {
        if (!actor.Scope.IsGroupLevel) return GroupScopeRequired();
        if (req.Unrecognised is { Count: > 0 }) return UnrecognisedField(req.Unrecognised.Keys);
        if (!await BenefitDefinitionEnabledAsync(db, actor.TenantId, ct)) return FeatureNotEnabled();
        return await FinanceDecisionSerializer.SerializeAsync(db, LockScope, actor.TenantId, actor.TenantId, () => UpdateBenefitCoreAsync(actor, code, req, ct), ct);
    }

    private async Task<MatrixResult> UpdateBenefitCoreAsync(MatrixActor actor, string code, UpdateBenefitRequest req, CancellationToken ct)
    {
        var tid = actor.TenantId;
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!EntitlementComponentRules.IsCustom(code))
            return Conflict("built_in_benefit", "Only a benefit you added yourself can be edited.");
        var cash = EntitlementComponentRules.IsAllowance(code);

        // Every live version of the definition (tracked), tenant-wide only. A code of another tenant is simply not here: 404, like a missing one.
        var rowsQuery = ScopedBypass.TenantWide(db.PayComponents, tid, "The tenant's own definition rows of one customer-added benefit, to edit.")
            .Where(x => x.CompanyId == null && x.Code == code && !x.IsDeleted);
        rowsQuery = cash
            ? rowsQuery.Where(x => x.ComponentType == PayComponentTypes.Earning && x.CalcMethod == PayComponentCalcMethods.GradePackage)
            : rowsQuery.Where(x => x.ComponentType == PayComponentTypes.Benefit);
        var versions = (await rowsQuery.ToListAsync(ct)).OrderBy(x => x.EffectiveFrom ?? DateOnly.MinValue).ToList();
        if (versions.Count == 0) return NotFound();

        // A replay of an edit this person already made: the current state, whatever the version has since become.
        if (req.RequestId is Guid requestId && await FindReplayAsync(actor, AuditBenefitUpdated, requestId, ct) is { } replay
            && replay.TryGetProperty("componentCode", out var replayed) && replayed.GetString() == code)
            return await CurrentAsync(tid, code, ct);

        if (string.IsNullOrWhiteSpace(req.ExpectedVersion))
            return Bad("expected_version_required", "Send the version of the benefit you were looking at, so a change someone else made isn't overwritten.");
        if (!string.Equals(req.ExpectedVersion.Trim(), VersionOf(versions), StringComparison.Ordinal))
            return Conflict("stale_definition", "Someone changed this benefit since you opened it. Reload it and make your change again.");

        // What cannot change here.
        if (Blank(req.Kind) is { } kindText && !string.Equals(kindText, cash ? BenefitKinds.Cash : BenefitKinds.Contract, StringComparison.OrdinalIgnoreCase))
            return Conflict("kind_immutable", "A benefit's kind can't be changed. Add a new benefit instead.");
        if (Blank(req.Delivery) is { } deliveryText)
        {
            if (string.Equals(deliveryText, "Reimbursement", StringComparison.OrdinalIgnoreCase) || string.Equals(deliveryText, "PayrollDeduction", StringComparison.OrdinalIgnoreCase))
                return Bad("delivery_not_supported", "Reimbursements and payroll deductions can't be set up here yet.");
            if (!string.Equals(deliveryText, cash ? DeliverySalaryAllowance : DeliveryCoverage, StringComparison.OrdinalIgnoreCase))
                return Conflict("kind_immutable", "A benefit's delivery can't be changed. Add a new benefit instead.");
        }
        if (req.RequiresEnrolment == true)
            return Bad("delivery_not_supported", "Employees get a benefit by grade, so it can't require enrolment.");
        if (Blank(req.Frequency) is { } frequency)
        {
            if (!cash) return Bad("frequency_not_applicable", "Coverage has no pay frequency.");
            if (!string.Equals(frequency, "Monthly", StringComparison.OrdinalIgnoreCase))
                return Bad("delivery_not_supported", "Only a monthly cash allowance can be set up here.");
        }

        // Names: either language will do, as when adding; never the same as another benefit's.
        var nameEn = (req.NameEn ?? string.Empty).Trim();
        var nameAr = (req.NameAr ?? string.Empty).Trim();
        if (nameEn.Length == 0 && nameAr.Length == 0) return Bad("name_required", "Give the benefit a name.");
        if (nameEn.Length > 100 || nameAr.Length > 100) return Bad("name_too_long", "Keep each name under 100 characters.");
        if (nameEn.Any(char.IsControl) || nameAr.Any(char.IsControl)) return Bad("name_invalid", "A name can't contain control characters.");
        if (nameEn.Length == 0) nameEn = nameAr;
        if (nameAr.Length == 0) nameAr = nameEn;
        var others = await ScopedBypass.TenantWide(db.PayComponents, tid, "The other benefits the customer added, removed ones excluded, to keep names unique.")
            .AsNoTracking().Where(IsCustomRow()).Where(x => !x.IsDeleted && x.Code != code).ToListAsync(ct);
        bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        if (others.Any(x => Same(x.NameEn, nameEn) || Same(x.NameAr, nameAr) || Same(x.NameEn, nameAr) || Same(x.NameAr, nameEn)))
            return Conflict("benefit_exists", $"A benefit called \"{nameEn}\" already exists. Choose a different name.");

        var (categoryProblem, category) = CheckCategory(req.Category);
        if (categoryProblem is not null) return categoryProblem;
        var (classProblem, policyClass) = CheckPolicyClass(req.PolicyClass);
        if (classProblem is not null) return classProblem;
        var (shapeProblem, _) = CheckWindowShape(req.EffectiveFrom, req.EffectiveTo);
        if (shapeProblem is not null) return shapeProblem;

        var head = versions[^1];
        var today = await clock.TodayAsync(tid, ct);
        var ctx = await DefinitionContextAsync(tid, [code], ct);

        // The scope is the offerings' business; restating the current one is fine.
        if (req.Scope is not null)
        {
            // A date is passed only so the "which month does a skip start" question never arises: nothing is written from it.
            var (scopeProblem, excluded) = await ExcludedCompaniesAsync(tid, req.Scope, cash, today, ct);
            if (scopeProblem is not null) return scopeProblem;
            var current = DefinitionOf(head, versions, ctx, today).OfferedCompanyIds;
            var requested = excluded is null ? ctx.ActiveCompanyIds : ctx.ActiveCompanyIds.Where(id => !excluded.Contains(id)).ToList();
            if (!current.OrderBy(x => x).SequenceEqual(requested.OrderBy(x => x)))
                return Conflict("scope_change_uses_offerings", "Switch a company on or off for this benefit in the company offerings; the scope isn't changed here.");
        }

        // The code: only a contract benefit nothing uses can be renamed.
        var prefix = cash ? EntitlementComponentRules.AllowancePrefix : EntitlementComponentRules.CustomPrefix;
        var (codeProblem, part) = CheckCode(req.Code, prefix);
        if (codeProblem is not null) return codeProblem;
        var newCode = part is null ? code : prefix + part;
        var renaming = newCode != code;
        if (renaming)
        {
            if (cash || ctx.UsedCodes.Contains(code))
                return Conflict("code_locked", "This code is already used by grade values, packages or salaries, so it can't be changed.");
            var takenByRow = await ScopedBypass.TenantWide(db.PayComponents, tid, "A new benefit code must not collide with any pay component code of the tenant.")
                .AsNoTracking().AnyAsync(x => x.Code == newCode, ct);
            if (takenByRow || await db.SalaryComponents.AnyAsync(x => x.TenantId == tid && x.Code == newCode, ct))
                return Conflict("code_taken", $"The code {newCode} is already used. Choose another code.");
        }

        // Dates, active switch, policy class. For a cash allowance these are money terms.
        var datesChanged = req.EffectiveFrom != head.EffectiveFrom || req.EffectiveTo != head.EffectiveTo;
        var activeChanged = req.IsActive.HasValue && req.IsActive.Value != head.IsActive;
        var classChanged = policyClass != head.PolicyClass;
        if (datesChanged || activeChanged)
        {
            if (versions.Count > 1)
                return Conflict("dates_locked_by_versions", "Finance has corrected this allowance's terms over time, so its dates and active switch can't be changed here.");
            if (await WindowLockProblemAsync(tid, req.EffectiveFrom, req.EffectiveTo, req.EffectiveFrom != head.EffectiveFrom, req.EffectiveTo != head.EffectiveTo, ct) is { } locked)
                return locked;
            if (req.EffectiveFrom != head.EffectiveFrom
                && await ScopedBypass.TenantWide(db.PayComponents, tid, "The unique key counts removed rows: a start date another version of the code already holds is taken.")
                    .AsNoTracking().AnyAsync(x => x.CompanyId == null && x.Code == code && x.ComponentType == head.ComponentType && x.Id != head.Id && x.EffectiveFrom == req.EffectiveFrom, ct))
                return Conflict("effective_from_taken", "A version of this benefit already starts on that date.");
        }

        // Cash terms: the GL driver and the end-of-service setting.
        AllowanceTerms? newTerms = null;
        var termsTouched = cash && (Blank(req.GlDriverKey) is not null || req.EosbIncluded.HasValue || req.EosbExclusionCategory is not null || req.EosbExclusionReason is not null);
        if (termsTouched)
        {
            var (termsProblem, terms) = await ResolveTermsAsync(tid, head, req.GlDriverKey, req.EosbIncluded, req.EosbExclusionCategory, req.EosbExclusionReason, validateUnchangedDriver: false, ct);
            if (termsProblem is not null) return termsProblem;
            if (terms != TermsOf(head))
            {
                if (head.FinanceConfirmedAtUtc is not null)
                    return Conflict("finance_correction_required", "Finance has confirmed this allowance, so its GL driver and end-of-service setting are changed by a Finance correction, from a month on, with a reason.");
                newTerms = terms;
            }
        }

        // Is this a change at all?
        var nameChanged = nameEn != head.NameEn || nameAr != head.NameAr;
        var categoryChanged = category != head.BenefitCategory;
        if (!nameChanged && !categoryChanged && !classChanged && !datesChanged && !activeChanged && !renaming && newTerms is null)
            return await CurrentAsync(tid, code, ct);

        // A cash allowance's money terms (dates, active, policy class, GL/EOSB) changed by someone without the Finance permission put it back to
        // pending Finance, and the editor is then its maker, who can never confirm it. A pending one stays pending with the editor as its maker.
        var moneyChanged = cash && (datesChanged || activeChanged || classChanged || newTerms is not null);
        var repended = moneyChanged && head.FinanceConfirmedAtUtc is not null && !actor.IsFinance;
        var before = new { name = head.NameEn, nameAr = head.NameAr, category = head.BenefitCategory, policyClass = head.PolicyClass, effectiveFrom = head.EffectiveFrom, effectiveTo = head.EffectiveTo, isActive = head.IsActive, code };
        var now = DateTime.UtcNow;
        foreach (var version in versions)
        {
            version.NameEn = nameEn; version.NameAr = nameAr; version.BenefitCategory = category; version.PolicyClass = policyClass;
            version.RequiresEnrolment = false; version.UpdatedAtUtc = now; version.UpdatedBy = actor.UserId;
        }
        if (datesChanged || activeChanged)
        {
            head.EffectiveFrom = req.EffectiveFrom; head.EffectiveTo = req.EffectiveTo;
            if (req.IsActive.HasValue) head.IsActive = req.IsActive.Value;
        }
        if (newTerms is not null)
        {
            head.GlDriverKey = newTerms.Driver; head.EosbIncluded = newTerms.EosbIncluded;
            head.EosbExclusionCategory = newTerms.Category; head.EosbExclusionReason = newTerms.Note;
        }
        if (moneyChanged)
        {
            head.CreatedBy = actor.UserId;
            if (repended) { head.FinanceConfirmedAtUtc = null; head.FinanceConfirmedByUserId = null; }
        }
        if (renaming)
        {
            var allRows = await ScopedBypass.TenantWide(db.PayComponents, tid, "A renamed code moves with its company switches, removed ones too (the unique key counts them).")
                .Where(x => x.Code == code && x.ComponentType == PayComponentTypes.Benefit).ToListAsync(ct);
            foreach (var row in allRows) { row.Code = newCode; row.UpdatedAtUtc = now; row.UpdatedBy = actor.UserId; }
        }
        var after = new { name = head.NameEn, nameAr = head.NameAr, category = head.BenefitCategory, policyClass = head.PolicyClass, effectiveFrom = head.EffectiveFrom, effectiveTo = head.EffectiveTo, isActive = head.IsActive, code = newCode };
        db.AuditLogs.Add(Audit(actor, null, AuditBenefitUpdated, head.Id, new
        {
            componentCode = code, newCode, before, after, repended, financeTermsChanged = newTerms is not null, requestId = req.RequestId,
        }));
        await db.SaveChangesAsync(ct);
        return await CurrentAsync(tid, newCode, ct);
    }

    /// <summary>200 with the benefit as it stands now, or 404 when it is not there.</summary>
    private async Task<MatrixResult> CurrentAsync(Guid tid, string code, CancellationToken ct) =>
        await ComponentDtoAsync(tid, code, null, ct) is { } dto ? Ok(dto) : NotFound();
}
