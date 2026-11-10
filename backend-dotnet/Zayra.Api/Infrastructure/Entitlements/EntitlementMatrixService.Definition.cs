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
/// of a <i>confirmed</i> allowance change only through Finance's effective-dated correction; its dates and active switch change here (the policy class is only a label), and
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
        if (CashAllowanceDefinitions.HasInvisibleOrDirectionCharacters(category)) return (Bad("category_invalid", "The category can't contain control, invisible or text-direction characters."), null);
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

    /// <summary>A benefit's window as payroll sees it: offered only while active, from <c>From</c> (null = since the start) to <c>To</c> (null = no end).</summary>
    internal readonly record struct Window(bool Active, DateOnly? From, DateOnly? To);

    private static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);

    private static bool InWindow(Window w, DateOnly monthStart) =>
        w.Active && (w.From is null || w.From <= monthStart)
        && (w.To is null || w.To >= new DateOnly(monthStart.Year, monthStart.Month, DateTime.DaysInMonth(monthStart.Year, monthStart.Month)));

    /// <summary>
    /// Whether going from <paramref name="old"/> to <paramref name="next"/> changes the membership of any month at or before
    /// <paramref name="committed"/> (the first day of the latest payroll month any company has approved, locked or closed). Comparing the two windows —
    /// not just the new values — catches every way into a paid month: clearing the start (the benefit then applies to all the past), moving the start later
    /// or earlier across a paid month, extending the end across a paid gap or cutting it inside a paid month, and switching it off or on.
    /// <paramref name="openPastCounts"/> false (a new contract benefit, which changes no paid month) ignores "since the start".
    /// </summary>
    internal static bool ChangeReachesCommitted(DateOnly committed, Window old, Window next, bool openPastCounts = true)
    {
        // "Since the start" reaches every past month at once.
        if (openPastCounts && (old.Active && old.From is null) != (next.Active && next.From is null)) return true;
        var finite = new[] { old.From, old.To, next.From, next.To }.Where(d => d.HasValue).Select(d => d!.Value).ToList();
        if (finite.Count == 0) return false;
        // Past the last bound (plus one month, where an end appears or disappears) both windows agree again.
        var start = MonthStart(finite.Min());
        var stop = MonthStart(finite.Max()).AddMonths(1);
        if (stop > committed) stop = committed;
        for (var month = start; month <= stop; month = month.AddMonths(1))
            if (InWindow(old, month) != InWindow(next, month)) return true;
        return false;
    }

    private async Task<MatrixResult?> WindowChangeLockProblemAsync(Guid tid, Window old, Window next, bool openPastCounts, CancellationToken ct)
    {
        if (old == next) return null;
        if (await PayComponentPolicy.LatestCommittedPeriodAsync(db, tid, null, ct) is not DateOnly committed) return null;
        if (!ChangeReachesCommitted(committed, old, next, openPastCounts)) return null;
        return new MatrixResult(StatusCodes.Status409Conflict, new
        {
            error = "effective_in_locked_period",
            message = $"Payroll for {committed:yyyy-MM} is already approved, locked or paid for at least one company. A change to when this benefit is in effect, or whether it is switched on, can only reach months after it.",
            earliestEffectiveFrom = committed.AddMonths(1),
        });
    }

    // ── Scope ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The companies that do NOT offer the benefit, from a scope. Null = every company offers it. A refusal when the scope has a key it does not
    /// know, is empty, lists more companies than the tenant has, names a company that is not the tenant's (all checked against ONE read of the tenant's
    /// companies), or when a cash allowance (never skippable per company) is scoped to a subset.</summary>
    private async Task<(MatrixResult? Problem, List<Guid>? Excluded)> ExcludedCompaniesAsync(Guid tid, BenefitScopeInput? scope, bool cash, DateOnly? from, CancellationToken ct)
    {
        if (scope is null) return (null, null);
        if (scope.Unrecognised is { Count: > 0 }) return (UnrecognisedField(scope.Unrecognised.Keys), null);
        if (scope.CompanyIds is not { } ids) return (null, null);
        if (ids.Count == 0) return (Bad("scope_empty", "Choose at least one company, or leave the scope as all companies."), null);
        var companies = await ScopedBypass.TenantWide(db.Companies, tid, "A scope may name only the tenant's own companies; checked against one read of them.")
            .AsNoTracking().Where(c => !c.IsDeleted).Select(c => new { c.Id, c.IsActive }).ToListAsync(ct);
        if (ids.Count > companies.Count) return (Bad("scope_too_large", "The scope lists more companies than you have."), null);
        var known = companies.Select(c => c.Id).ToHashSet();
        var chosen = ids.Distinct().ToList();
        if (chosen.Any(id => !known.Contains(id))) return (Bad("company_not_found", "One of the companies doesn't exist."), null);
        var excluded = companies.Where(c => c.IsActive && !chosen.Contains(c.Id)).Select(c => c.Id).ToList();
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
        // A new definition has no earlier window: every month it covers changes. A new cash allowance open to the past would be in effect for paid months.
        if (await WindowChangeLockProblemAsync(actor.TenantId, new Window(false, null, null), new Window(req.IsActive ?? true, req.EffectiveFrom, req.EffectiveTo), openPastCounts: cash, ct) is { } locked)
            return (locked, null);
        return (null, new CreateDefinition(category, policyClass, req.EffectiveFrom, req.EffectiveTo, req.IsActive ?? true, part, excluded));
    }

    // ── Replay ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A short, stable hash of what a request asked for (its request id left out): a replay must be the same request, not just the same id.</summary>
    private static string Fingerprint(object body) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body))))[..32];

    private static MatrixResult RequestIdReused() => new(StatusCodes.Status422UnprocessableEntity, new
    {
        error = "request_id_reused",
        message = "This request id was already used for a different request. Send a new request id for a new change.",
    });

    /// <summary>The audit rows of an earlier request by the same person with this id: the stored metadata, and whether the body hash agrees.</summary>
    private static bool SameRequest(JsonElement stored, string fingerprint) =>
        !stored.TryGetProperty("requestHash", out var h) || h.ValueKind != JsonValueKind.String || h.GetString() == fingerprint;

    // ── Edit ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Edits the definition of a benefit or cash allowance the customer added (PUT /api/entitlements/components/{code}). Needs the feature (read inside the
    /// lock), access to every company and the version the screen read (<see cref="UpdateBenefitRequest.ExpectedVersion"/>); serialised with every other matrix
    /// write, one save with its audit row. The kind and delivery never change; the code only while nothing uses it (contract benefits); the company scope is
    /// switched in the offerings. A cash allowance: see the class remarks.
    /// </summary>
    public async Task<MatrixResult> UpdateBenefitAsync(MatrixActor actor, string code, UpdateBenefitRequest req, CancellationToken ct)
    {
        if (!actor.Scope.IsGroupLevel) return GroupScopeRequired();
        return await FinanceDecisionSerializer.SerializeAsync(db, LockScope, actor.TenantId, actor.TenantId, () => UpdateBenefitCoreAsync(actor, code, req, ct), ct);
    }

    private async Task<MatrixResult> UpdateBenefitCoreAsync(MatrixActor actor, string code, UpdateBenefitRequest req, CancellationToken ct)
    {
        var tid = actor.TenantId;
        if (AfterLockHook is { } afterLock) await afterLock();
        if (!await BenefitDefinitionEnabledAsync(db, tid, ct)) return FeatureNotEnabled();
        if (req.Unrecognised is { Count: > 0 }) return UnrecognisedField(req.Unrecognised.Keys);
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        var fingerprint = Fingerprint(new
        {
            urlCode = code, req.ExpectedVersion, req.NameEn, req.NameAr, req.Code, req.Kind, req.Delivery, req.Frequency, req.Category, req.PolicyClass, req.EffectiveFrom,
            req.EffectiveTo, req.IsActive, req.RequiresEnrolment, scope = req.Scope?.CompanyIds?.Order().ToList(), scopeSent = req.Scope is not null, req.GlDriverKey,
            req.EosbIncluded, req.EosbExclusionCategory, req.EosbExclusionReason, sent = req.SentFields(),
        });

        // A replay of an edit this person already made, found BEFORE the code lookup (a rename moved the code): the stored result, whatever the version
        // has since become. The same id for a different request is refused.
        if (req.RequestId is Guid requestId && await FindReplayAsync(actor, AuditBenefitUpdated, requestId, ct) is { } replay)
        {
            var was = replay.TryGetProperty("componentCode", out var c0) ? c0.GetString() : null;
            var movedTo = replay.TryGetProperty("newCode", out var c1) ? c1.GetString() : was;
            if (!SameRequest(replay, fingerprint) || (code != was && code != movedTo)) return RequestIdReused();
            return await CurrentAsync(tid, movedTo ?? code, ct);
        }

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

        var head = versions[^1];
        var today = await clock.TodayAsync(tid, ct);

        // Names are replaced when sent (each language on its own); one of them is required. Never the same as another benefit's.
        var nameEn = Blank(req.NameEn) ?? head.NameEn;
        var nameAr = Blank(req.NameAr) ?? head.NameAr;
        if (Blank(req.NameEn) is null && Blank(req.NameAr) is null) return Bad("name_required", "Give the benefit a name.");
        if (nameEn.Length > 100 || nameAr.Length > 100) return Bad("name_too_long", "Keep each name under 100 characters.");
        // The strict rule applies to a name that is being CHANGED (per language): one saved earlier, while the feature was off, stays as it is and
        // never blocks an edit of something else.
        if ((nameEn != head.NameEn && CashAllowanceDefinitions.HasInvisibleOrDirectionCharacters(nameEn))
            || (nameAr != head.NameAr && CashAllowanceDefinitions.HasInvisibleOrDirectionCharacters(nameAr)))
            return Bad("name_invalid", "A name can't contain control, invisible or text-direction characters.");
        var others = await ScopedBypass.TenantWide(db.PayComponents, tid, "The other benefits the customer added, removed ones excluded, to keep names unique.")
            .AsNoTracking().Where(IsCustomRow()).Where(x => !x.IsDeleted && x.Code != code).ToListAsync(ct);
        bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        if (others.Any(x => Same(x.NameEn, nameEn) || Same(x.NameAr, nameAr) || Same(x.NameEn, nameAr) || Same(x.NameAr, nameEn)))
            return Conflict("benefit_exists", $"A benefit called \"{nameEn}\" already exists. Choose a different name.");

        // Omitted = unchanged; an explicit null = cleared.
        var category = head.BenefitCategory;
        if (req.Sent(nameof(req.Category)))
        {
            var (categoryProblem, parsed) = CheckCategory(req.Category);
            if (categoryProblem is not null) return categoryProblem;
            category = parsed;
        }
        var policyClass = head.PolicyClass;
        if (req.Sent(nameof(req.PolicyClass)))
        {
            var (classProblem, parsed) = CheckPolicyClass(req.PolicyClass);
            if (classProblem is not null) return classProblem;
            policyClass = parsed;
        }
        var newFrom = req.Sent(nameof(req.EffectiveFrom)) ? req.EffectiveFrom : head.EffectiveFrom;
        var newTo = req.Sent(nameof(req.EffectiveTo)) ? req.EffectiveTo : head.EffectiveTo;
        var (shapeProblem, _) = CheckWindowShape(newFrom, newTo);
        if (shapeProblem is not null) return shapeProblem;

        var ctx = await DefinitionContextAsync(tid, [code], ct);

        // The scope is the offerings' business; restating the current one is fine.
        if (req.Scope is not null)
        {
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

        // Dates and the active switch: the window as payroll sees it may not change the membership of a month already paid. For a cash allowance the
        // window and the switch are money terms (payroll reads them); the policy class is a label.
        var newActive = req.IsActive ?? head.IsActive;
        var datesChanged = newFrom != head.EffectiveFrom || newTo != head.EffectiveTo;
        var activeChanged = newActive != head.IsActive;
        var classChanged = policyClass != head.PolicyClass;
        if (datesChanged || activeChanged)
        {
            if (versions.Count > 1)
                return Conflict("dates_locked_by_versions", "Finance has corrected this allowance's terms over time, so its dates and active switch can't be changed here.");
            if (await WindowChangeLockProblemAsync(tid, new Window(head.IsActive, head.EffectiveFrom, head.EffectiveTo), new Window(newActive, newFrom, newTo), openPastCounts: true, ct) is { } locked)
                return locked;
            if (newFrom != head.EffectiveFrom
                && await ScopedBypass.TenantWide(db.PayComponents, tid, "The unique key counts removed rows: a start date another version of the code already holds is taken.")
                    .AsNoTracking().AnyAsync(x => x.CompanyId == null && x.Code == code && x.ComponentType == head.ComponentType && x.Id != head.Id && x.EffectiveFrom == newFrom, ct))
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
                // "Confirmed" is not just the state it is in now: an edit can send a confirmed allowance back to pending, and rewriting its terms in place
                // would then read the new terms over months it already paid. Once confirmed, always a Finance correction (effective-dated, with a reason).
                if (head.FinanceConfirmedAtUtc is not null || await EverConfirmedAsync(tid, versions, ct))
                    return Conflict("finance_correction_required", "Finance has confirmed this allowance, so its GL driver and end-of-service setting are changed by a Finance correction, from a month on, with a reason.");
                newTerms = terms;
            }
        }

        // Is this a change at all?
        var nameChanged = nameEn != head.NameEn || nameAr != head.NameAr;
        var categoryChanged = category != head.BenefitCategory;
        if (!nameChanged && !categoryChanged && !classChanged && !datesChanged && !activeChanged && !renaming && newTerms is null)
            return await CurrentAsync(tid, code, ct);

        // A cash allowance's money terms (dates, active, GL/EOSB) — and a name that changes its end-of-service cautions — changed by someone
        // without the Finance permission put it back to pending Finance, and the editor is then its maker, who can never confirm it. A pending one stays
        // pending with the editor as its maker. A Finance editor keeps it confirmed and the original maker.
        var cautionsChanged = cash && nameChanged
            && !CashAllowanceDefinitions.Warnings(head.NameEn, head.NameAr, head.EosbIncluded, head.EosbExclusionCategory, atCreation: false).Select(w => w.Code).Order()
                .SequenceEqual(CashAllowanceDefinitions.Warnings(nameEn, nameAr, head.EosbIncluded, head.EosbExclusionCategory, atCreation: false).Select(w => w.Code).Order());
        // (The policy class is a descriptive label payroll never reads, so it is not a money term.)
        var moneyChanged = cash && (datesChanged || activeChanged || newTerms is not null || cautionsChanged);
        var repended = moneyChanged && head.FinanceConfirmedAtUtc is not null && !actor.IsFinance;
        var before = new { name = head.NameEn, nameAr = head.NameAr, category = head.BenefitCategory, policyClass = head.PolicyClass, effectiveFrom = head.EffectiveFrom, effectiveTo = head.EffectiveTo, isActive = head.IsActive, code };
        var renamedRows = renaming
            ? await ScopedBypass.TenantWide(db.PayComponents, tid, "A renamed code moves with its company switches, removed ones too (the unique key counts them).")
                .Where(x => x.Code == code && x.ComponentType == PayComponentTypes.Benefit).ToListAsync(ct)
            : [];
        // The last look before anything is changed (nothing below awaits until the save): a payroll approval that landed since the first check is caught.
        if (BeforeDefinitionSaveHook is { } beforeSave) await beforeSave();
        if ((datesChanged || activeChanged)
            && await WindowChangeLockProblemAsync(tid, new Window(head.IsActive, head.EffectiveFrom, head.EffectiveTo), new Window(newActive, newFrom, newTo), openPastCounts: true, ct) is { } lateLock)
        {
            db.ChangeTracker.Clear();
            return lateLock;
        }
        var now = DateTime.UtcNow;
        foreach (var version in versions)
        {
            version.NameEn = nameEn; version.NameAr = nameAr; version.BenefitCategory = category; version.PolicyClass = policyClass;
            version.RequiresEnrolment = false; version.UpdatedAtUtc = now; version.UpdatedBy = actor.UserId;
        }
        if (datesChanged || activeChanged)
        {
            head.EffectiveFrom = newFrom; head.EffectiveTo = newTo; head.IsActive = newActive;
        }
        if (newTerms is not null)
        {
            head.GlDriverKey = newTerms.Driver; head.EosbIncluded = newTerms.EosbIncluded;
            head.EosbExclusionCategory = newTerms.Category; head.EosbExclusionReason = newTerms.Note;
        }
        if (repended) { head.FinanceConfirmedAtUtc = null; head.FinanceConfirmedByUserId = null; }
        if (moneyChanged && head.FinanceConfirmedAtUtc is null) head.CreatedBy = actor.UserId;
        foreach (var row in renamedRows) { row.Code = newCode; row.UpdatedAtUtc = now; row.UpdatedBy = actor.UserId; }
        var after = new { name = head.NameEn, nameAr = head.NameAr, category = head.BenefitCategory, policyClass = head.PolicyClass, effectiveFrom = head.EffectiveFrom, effectiveTo = head.EffectiveTo, isActive = head.IsActive, code = newCode };
        db.AuditLogs.Add(Audit(actor, null, AuditBenefitUpdated, head.Id, new
        {
            componentCode = code, newCode, before, after, repended, financeTermsChanged = newTerms is not null, requestId = req.RequestId, requestHash = fingerprint,
        }));
        await db.SaveChangesAsync(ct);
        return await CurrentAsync(tid, newCode, ct);
    }

    /// <summary>Whether Finance has ever confirmed this allowance: some version is confirmed now, or its audit history shows a confirmation, a Finance
    /// correction, an add that was confirmed at once, or an edit that sent a confirmed allowance back to pending.</summary>
    private async Task<bool> EverConfirmedAsync(Guid tid, IReadOnlyCollection<PayComponent> versions, CancellationToken ct)
    {
        if (versions.Any(v => v.FinanceConfirmedAtUtc is not null)) return true;
        var ids = versions.Select(v => v.Id.ToString()).ToList();
        var history = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tid && a.EntityId != null && ids.Contains(a.EntityId)
                && (a.Action == AuditBenefitConfirmed || a.Action == AuditBenefitCorrected || a.Action == AuditBenefitAdded || a.Action == AuditBenefitUpdated))
            .Select(a => new { a.Action, a.Metadata }).ToListAsync(ct);
        foreach (var row in history)
        {
            if (row.Action is AuditBenefitConfirmed or AuditBenefitCorrected) return true;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.Metadata) ? "{}" : row.Metadata);
                var key = row.Action == AuditBenefitAdded ? "financeConfirmed" : "repended";
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True) return true;
            }
            catch (JsonException) { /* unreadable history: read as "not confirmed by this row" and let the other rows speak */ }
        }
        return false;
    }

    /// <summary>200 with the benefit as it stands now, or 404 when it is not there.</summary>
    private async Task<MatrixResult> CurrentAsync(Guid tid, string code, CancellationToken ct) =>
        await ComponentDtoAsync(tid, code, null, ct) is { } dto ? Ok(dto) : NotFound();
}
