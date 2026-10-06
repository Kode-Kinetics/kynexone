using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

/// <summary>
/// Benefits by grade (Release A slice R1): one matrix, grade by component, for every benefit — housing and transport,
/// the air ticket, medical class and dependants, education, per diem and (read-only here) the loan facilities.
///
/// <para><b>Storage.</b> Cells are <c>grade_entitlements</c> rows, close-only and effective-dated exactly as the L1
/// loan grid writes them (<c>LoansController.GradeLimits</c>): publishing closes the version in force the day before
/// and opens a new one, in one transaction, with an audit row per cell. A company cell overrides the tenant (group)
/// cell. A company that does not offer a component holds a company <c>pay_components</c> row with
/// <c>is_offered = false</c> (a "skip marker"); statutory floors can never carry one (CHECK
/// ck_pay_components__floor_always_offered, and refused here first with the legal reason).</para>
///
/// <para><b>Modes are derived, never stored:</b> Skipped (a marker in force), Tailored (a company cell in force),
/// otherwise Adopted.</para>
///
/// <para><b>For R2.</b> <see cref="GradeStandardLinesAsync"/> is the read <c>IEntitlementResolver.GradeStandardAsync</c>
/// needs: the cell in force per component (company first) with the company's offered switch.</para>
/// </summary>
public sealed class EntitlementMatrixService(ZayraDbContext db, ITenantClock clock)
{
    /// <summary>One lock per tenant for every matrix write: publish, offering switch and import.</summary>
    public const string LockScope = "entitlements.matrix";
    public const string AuditPublished = "entitlements.matrix.published";
    public const string AuditReverted = "entitlements.matrix.reverted_to_group";
    public const string AuditSkipped = "entitlements.offering.skipped";
    public const string AuditOffered = "entitlements.offering.offered";
    public const string SourceRuleMatrix = "BenefitsByGradeMatrix";
    public const string SourceRuleImport = "LegacyPayScaleImport";
    public const string GapNotificationEntity = "EntitlementMatrixGap";

    public static class Groups
    {
        public const string Wage = "Wage";
        public const string Contract = "Contract";
        public const string Facility = "Facility";
    }

    public static class Modes
    {
        public const string Group = "Group";
        public const string Adopted = "Adopted";
        public const string Tailored = "Tailored";
        public const string Skipped = "Skipped";
    }

    public static class LegacyOutcomes
    {
        public const string Import = "Import";
        public const string Skip = "Skip";
    }

    private static readonly string[] CurrentEmployeeStatuses = [EmployeeStatuses.Active, EmployeeStatuses.Invited, EmployeeStatuses.Suspended];

    /// <summary>Whether the tenant has Release A switched on. Opt-in: no row means off (<c>OptInFeatures</c>).
    /// Used by the legacy writers R1 freezes, so a tenant without the flag keeps working exactly as before.</summary>
    public static Task<bool> ReleaseAEnabledAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) =>
        db.TenantFeatureFlags.AsNoTracking()
            .AnyAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.ReleaseA && f.IsEnabled, ct);

    /// <summary>A wage component pays through payroll, so a company row for it would shadow the payroll catalogue.
    /// The matrix therefore never writes a skip marker for one; "Not offered" cells express it instead.</summary>
    public static bool CanSkipHere(EntitlementComponentRule rule) =>
        EntitlementComponentRules.CanBeSkipped(rule) && rule.Class != PayEntitlementClasses.QiwaWage;

    public static string GroupOf(EntitlementComponentRule rule) => rule.Class switch
    {
        PayEntitlementClasses.QiwaWage => Groups.Wage,
        PayEntitlementClasses.Contractual => Groups.Contract,
        _ => Groups.Facility,
    };

    public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => clock.TodayAsync(tenantId, ct);

    // ── Read ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The matrix as of <paramref name="asOf"/>: the tenant grid, or one company's view (its own cells over the
    /// group defaults). The caller has authorised the company.</summary>
    public async Task<EntitlementMatrixDto> ReadAsync(Guid tid, Guid? companyId, DateOnly asOf, CancellationToken ct)
    {
        var today = await clock.TodayAsync(tid, ct);
        var grades = await ActiveGradesAsync(tid, ct);
        var rules = await ComponentRulesAsync(tid, ct);
        var codes = rules.Select(r => r.Code).ToArray();
        var cells = await ScopedBypass.TenantWide(db.GradeEntitlements, tid,
                "The matrix shows the group cells and the requested company's own; company access was checked by the caller.")
            .AsNoTracking()
            .Where(x => codes.Contains(x.PayComponentCode) && (x.CompanyId == null || x.CompanyId == companyId))
            .ToListAsync(ct);
        var markers = companyId is Guid cid ? await SkipMarkersAsync(tid, cid, ct) : [];
        var companyIds = companyId is null ? await ActiveCompanyIdsAsync(tid, ct) : [];
        var companyCellsInForce = companyId is null
            ? (await ScopedBypass.TenantWide(db.GradeEntitlements, tid, "Group completeness counts every company's own cells.")
                .AsNoTracking()
                .Where(x => x.CompanyId != null && codes.Contains(x.PayComponentCode)
                    && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
                .Select(x => new { x.GradeId, x.PayComponentCode, x.CompanyId })
                .ToListAsync(ct))
                .Select(x => (x.GradeId, x.PayComponentCode, CompanyId: x.CompanyId!.Value)).ToHashSet()
            : [];

        var cellDtos = new List<MatrixCellDto>();
        var gaps = new List<MatrixGapDto>();
        var offerings = new List<MatrixOfferingDto>();
        foreach (var rule in rules)
        {
            var offered = true;
            if (companyId is not null)
            {
                var marker = markers.FirstOrDefault(m => m.Code == rule.Code && m.IsInEffect(asOf));
                var scheduled = markers.Where(m => m.Code == rule.Code && m.EffectiveFrom > asOf).Select(m => m.EffectiveFrom).Min();
                var reOffered = marker?.EffectiveTo is DateOnly to ? to.AddDays(1) : (DateOnly?)null;
                offered = marker == null;
                var tailored = cells.Any(c => c.CompanyId == companyId && c.PayComponentCode == rule.Code && c.IsInEffect(asOf));
                offerings.Add(new MatrixOfferingDto(rule.Code,
                    !offered ? Modes.Skipped : tailored ? Modes.Tailored : Modes.Adopted,
                    offered, marker?.EffectiveFrom, scheduled ?? reOffered, CanSkipHere(rule)));
            }
            else offerings.Add(new MatrixOfferingDto(rule.Code, Modes.Group, true, null, null, CanSkipHere(rule)));

            foreach (var grade in grades)
            {
                var versions = cells.Where(c => c.GradeId == grade.Id && c.PayComponentCode == rule.Code).ToList();
                var own = versions.FirstOrDefault(c => c.CompanyId != null && c.IsInEffect(asOf));
                var group = versions.FirstOrDefault(c => c.CompanyId == null && c.IsInEffect(asOf));
                var cell = own ?? group;
                if (cell != null)
                {
                    var nextChange = versions.Where(c => c.CompanyId == cell.CompanyId && c.EffectiveFrom > asOf)
                        .Select(c => (DateOnly?)c.EffectiveFrom).Min();
                    cellDtos.Add(ToDto(cell, companyId, nextChange));
                }
                if (rule.IsLoanFacility || !offered) continue;
                var missing = companyId is not null
                    ? cell == null
                    : group == null && (companyIds.Count == 0
                        || companyIds.Any(co => !companyCellsInForce.Contains((grade.Id, rule.Code, co))));
                if (missing) gaps.Add(new MatrixGapDto(grade.Id, rule.Code));
            }
        }

        string? currency;
        if (companyId is Guid id)
            currency = await db.Companies.AsNoTracking().Where(c => c.TenantId == tid && c.Id == id).Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
        else
        {
            var currencies = await ScopedBypass.TenantWide(db.Companies, tid, "The group grid states the one currency its amounts mean, if there is one.")
                .AsNoTracking().Where(c => !c.IsDeleted && c.IsActive).Select(c => c.DefaultCurrency).Distinct().ToListAsync(ct);
            currency = currencies.Count == 1 ? currencies[0] : null;
        }

        return new EntitlementMatrixDto(asOf, today, companyId, currency,
            grades.Select(g => new MatrixGradeDto(g.Id, g.Code, g.Name, g.NameAr, g.Level)).ToList(),
            rules.Select(ToDto).ToList(), cellDtos, offerings, gaps);
    }

    /// <summary>
    /// The grade's standard for one company on a date: per component, the company cell in force where one exists,
    /// else the tenant cell; <c>Offered</c> is false while the company skips the component. Loan facilities are
    /// always reported offered here — loans keep their own per-company switch (<c>loan_policies.is_offered</c>).
    /// This is the read <see cref="IEntitlementResolver.GradeStandardAsync"/> (R2) is built on.
    /// </summary>
    public async Task<IReadOnlyList<GradeStandardLine>> GradeStandardLinesAsync(Guid tid, Guid gradeId, Guid companyId, DateOnly asOf, CancellationToken ct)
    {
        var cells = await ScopedBypass.TenantWide(db.GradeEntitlements, tid,
                "The grade standard reads the group cell and this company's own; the caller authorised the company.")
            .AsNoTracking()
            .Where(x => x.GradeId == gradeId && (x.CompanyId == null || x.CompanyId == companyId)
                && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
            .ToListAsync(ct);
        var markers = await SkipMarkersAsync(tid, companyId, ct);
        return cells
            .GroupBy(c => c.PayComponentCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.CompanyId.HasValue).First())
            .Select(c =>
            {
                var rule = EntitlementComponentRules.For(c.PayComponentCode);
                var offered = !markers.Any(m => string.Equals(m.Code, c.PayComponentCode, StringComparison.OrdinalIgnoreCase) && m.IsInEffect(asOf));
                return new GradeStandardLine(c.PayComponentCode, c.EntitlementClass, rule?.Floor ?? PayStatutoryFloors.None, offered,
                    c.Eligible, c.ValueType, c.Amount, c.Rate, c.MaxOutstandingAmount, c.CoverageTier, c.Quantity, c.DependantScope,
                    c.MaxDependants, c.LimitPeriod, c.MinServiceMonths, c.AfterProbation, c.NationalityScope, c.Id,
                    c.CompanyId.HasValue, c.EffectiveFrom);
            })
            .OrderBy(l => l.ComponentCode, StringComparer.Ordinal)
            .ToList();
    }

    // ── Publish ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes changed cells from <c>EffectiveFrom</c> (today or later, in the tenant's own calendar). Tenant cells need
    /// group scope; company cells need access to that company. With <paramref name="dryRun"/> nothing is written and the
    /// result says what would change and for how many employees.
    /// </summary>
    public async Task<MatrixResult> PublishAsync(MatrixActor actor, PublishMatrixRequest req, bool dryRun, CancellationToken ct)
    {
        if (dryRun) return await PublishCoreAsync(actor, req, SourceRuleMatrix, dryRun: true, ct);
        return await FinanceDecisionSerializer.SerializeAsync(db, LockScope, actor.TenantId, actor.TenantId,
            () => PublishCoreAsync(actor, req, SourceRuleMatrix, dryRun: false, ct), ct);
    }

    private async Task<MatrixResult> PublishCoreAsync(MatrixActor actor, PublishMatrixRequest req, string sourceRule, bool dryRun, CancellationToken ct)
    {
        var tid = actor.TenantId;
        if (await ScopeProblemAsync(actor, req.CompanyId, ct) is { } scopeProblem) return scopeProblem;
        var today = await clock.TodayAsync(tid, ct);
        if (req.EffectiveFrom < today)
            return Bad("effective_from_in_past", "Benefits can't be changed for past dates. Choose today or a later date.");
        if (req.Cells is not { Count: > 0 })
            return Bad("no_cells", "Change at least one benefit before publishing.");

        var grades = await ActiveGradesAsync(tid, ct);
        var errors = new List<MatrixCellError>();
        foreach (var cell in req.Cells) cell.ComponentCode = (cell.ComponentCode ?? string.Empty).Trim().ToUpperInvariant();
        foreach (var dup in req.Cells.GroupBy(c => (c.GradeId, c.ComponentCode)).Where(g => g.Count() > 1))
            errors.Add(new MatrixCellError(dup.Key.GradeId, dup.Key.ComponentCode, "duplicate_cell", "This benefit appears more than once for the grade.", null));
        var accepted = new List<(MatrixCellInput Input, EntitlementComponentRule Rule, Grade Grade)>();
        foreach (var cell in req.Cells)
        {
            var grade = grades.FirstOrDefault(g => g.Id == cell.GradeId);
            if (grade == null)
            {
                errors.Add(Error(cell, ReleaseABlockReasons.GradeMissing, "This grade doesn't exist or is no longer in use."));
                continue;
            }
            var rule = EntitlementComponentRules.For(cell.ComponentCode);
            if (rule == null)
            {
                errors.Add(Error(cell, "unknown_component", "This benefit isn't in the catalogue."));
                continue;
            }
            if (cell.UseGroupDefault && req.CompanyId is null)
            {
                errors.Add(Error(cell, "group_default_needs_company", "Only a company's own value can be dropped in favour of the group default."));
                continue;
            }
            var problem = cell.UseGroupDefault
                ? rule.IsLoanFacility ? Error(cell, "loan_component_read_only", "Loan limits are set in Loans → Loan policies → Limits by grade.") : null
                : ValidateCell(rule, cell);
            if (problem != null)
            {
                errors.Add(problem);
                continue;
            }
            accepted.Add((cell, rule, grade));
        }
        if (errors.Count > 0)
            return new MatrixResult(StatusCodes.Status400BadRequest, new
            {
                error = "invalid_cells",
                message = "Some benefits need correcting before they can be published.",
                errors,
            });

        // A cell carries no currency: a fixed amount means "in the employee's company currency". For the group grid that
        // is ambiguous when companies pay in different currencies (L1 parity). Percentages, tiers and counts are neutral.
        if (req.CompanyId is null && accepted.Any(n => n.Input.Eligible && n.Input.Amount is not null))
        {
            var currencies = await ScopedBypass.TenantWide(db.Companies, tid, "A group-wide fixed amount must mean one currency across every active company.")
                .Where(x => !x.IsDeleted && x.IsActive).Select(x => x.DefaultCurrency).Distinct().ToListAsync(ct);
            if (currencies.Select(c => (c ?? string.Empty).Trim().ToUpperInvariant()).Distinct().Count() > 1)
                return new MatrixResult(StatusCodes.Status422UnprocessableEntity, new
                {
                    error = "currency_ambiguous",
                    message = $"Your companies pay in different currencies ({string.Join(", ", currencies.OrderBy(c => c))}), so a fixed amount for all "
                        + "companies is ambiguous. Set fixed amounts per company instead, or use a percentage of basic salary.",
                    currencies = currencies.OrderBy(c => c),
                });
        }

        var gradeIds = accepted.Select(n => n.Grade.Id).Distinct().ToArray();
        var codes = accepted.Select(n => n.Rule.Code).Distinct().ToArray();
        // Exact scope (this company, or the group), every version — so a scheduled later version is seen.
        var existing = await ScopedBypass.TenantWide(db.GradeEntitlements, tid,
                "Publishing must see every version in the target scope; scope was authorised above.")
            .Where(x => codes.Contains(x.PayComponentCode) && x.CompanyId == req.CompanyId && gradeIds.Contains(x.GradeId))
            .ToListAsync(ct);

        var closes = new List<GradeEntitlement>();
        var inserts = new List<(GradeEntitlement Cell, GradeEntitlement? Previous)>();
        var reverted = new List<GradeEntitlement>();
        var unchanged = 0;
        foreach (var (input, rule, grade) in accepted)
        {
            var versions = existing.Where(x => x.GradeId == grade.Id && x.PayComponentCode == rule.Code).ToList();
            var later = versions.Where(x => x.EffectiveFrom > req.EffectiveFrom).OrderBy(x => x.EffectiveFrom).FirstOrDefault();
            if (later != null)
                return Conflict("later_version_exists",
                    $"{grade.Name} already has a {rule.NameEn.ToLowerInvariant()} value starting {later.EffectiveFrom:yyyy-MM-dd}. Publish from that date or later.");
            var current = versions.FirstOrDefault(x => x.IsInEffect(req.EffectiveFrom));
            if (input.UseGroupDefault)
            {
                if (current == null) { unchanged++; continue; }
                if (current.EffectiveFrom == req.EffectiveFrom)
                    return Conflict("same_day_version_exists",
                        $"{grade.Name}'s own {rule.NameEn.ToLowerInvariant()} value starts {req.EffectiveFrom:yyyy-MM-dd}. Values are never overwritten; publish the change from the next day.");
                reverted.Add(current);
                continue;
            }
            var next = NewCell(tid, req.CompanyId, grade.Id, rule, input, req.EffectiveFrom, sourceRule, actor.UserId);
            if (current != null && SameValues(current, next)) { unchanged++; continue; }
            if (current != null && current.EffectiveFrom == req.EffectiveFrom)
                return Conflict("same_day_version_exists",
                    $"{grade.Name} already has a {rule.NameEn.ToLowerInvariant()} value starting {req.EffectiveFrom:yyyy-MM-dd}. Values are never overwritten; publish the correction from the next day.");
            if (current != null) closes.Add(current);
            inserts.Add((next, current));
        }

        var touched = inserts.Select(i => (i.Cell.GradeId, i.Cell.PayComponentCode))
            .Concat(reverted.Select(r => (r.GradeId, r.PayComponentCode))).Distinct().ToList();
        var (affectedNow, affectedAtRenewal) = await ImpactAsync(tid, req.CompanyId, req.EffectiveFrom, touched, ct);

        if (dryRun)
            return Ok(new PublishMatrixResult(true, inserts.Count, unchanged, reverted.Count, affectedNow, affectedAtRenewal, 0, null));

        await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, tid, ct);
        foreach (var current in closes) current.EffectiveTo = req.EffectiveFrom.AddDays(-1);
        foreach (var cell in reverted)
        {
            cell.EffectiveTo = req.EffectiveFrom.AddDays(-1);
            db.AuditLogs.Add(Audit(actor, req.CompanyId, AuditReverted, cell.Id, new
            {
                componentCode = cell.PayComponentCode, cell.GradeId, cell.CompanyId, closedOn = cell.EffectiveTo, groupDefaultFrom = req.EffectiveFrom,
            }));
        }
        // Close first, then open: the no-overlap EXCLUDE is checked per statement, and EF does not promise to order an
        // UPDATE before an INSERT on the same table within one SaveChanges. Both saves run in the serializer's
        // transaction, so the publish is all-or-nothing.
        await db.SaveChangesAsync(ct);
        foreach (var (cell, previous) in inserts)
        {
            db.GradeEntitlements.Add(cell);
            db.AuditLogs.Add(Audit(actor, req.CompanyId, AuditPublished, cell.Id, new
            {
                componentCode = cell.PayComponentCode, cell.GradeId, cell.CompanyId, cell.EffectiveFrom, sourceRule,
                previous = previous == null ? null : Snapshot(previous),
                next = Snapshot(cell),
            }));
        }
        await db.SaveChangesAsync(ct);

        var matrix = await ReadAsync(tid, req.CompanyId, req.EffectiveFrom, ct);
        if (matrix.Gaps.Count > 0 && await NotifyGapsAsync(tid, req.CompanyId, matrix.Gaps.Count, ct) > 0)
            await db.SaveChangesAsync(ct);
        return Ok(new PublishMatrixResult(false, inserts.Count, unchanged, reverted.Count, affectedNow, affectedAtRenewal, matrix.Gaps.Count, matrix));
    }

    /// <summary>
    /// Plain-language validation of one cell against its component's rules (plan §1.1); null when it can be published.
    /// Mirrors the database CHECKs, adds the rules a CHECK cannot carry (value types per component, floors, banned
    /// criteria), and normalises the input (a "Not offered" cell drops its figures; defaults for scope and period).
    /// </summary>
    internal static MatrixCellError? ValidateCell(EntitlementComponentRule rule, MatrixCellInput c)
    {
        static bool Money(decimal v) => v > 0 && decimal.Round(v, 2) == v && v <= 999_999_999_999.99m;

        if (c.Unrecognised is { Count: > 0 } extra && extra.Keys.FirstOrDefault(IsBannedCriterion) is { } banned)
            return Error(c, ReleaseABlockReasons.EntitlementBannedCriterion, $"\"{banned}\" can't be a condition for a benefit.");
        if (rule.IsLoanFacility)
            return Error(c, "loan_component_read_only", "Loan limits are set in Loans → Loan policies → Limits by grade.");
        if (rule.Floor == PayStatutoryFloors.Art40)
            return Error(c, ReleaseABlockReasons.EntitlementArt40ReadOnly, "This cost is the employer's by law and can't be set here.");
        if (c.Note is { Length: > 500 }) return Error(c, "note_too_long", "Keep the note under 500 characters.");
        c.Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim();

        if (!c.Eligible)
        {
            // "Not offered" for this grade: no figures, no coverage, no conditions.
            c.ValueType = GradeEntitlementValueTypes.EligibilityOnly;
            c.Amount = c.Rate = null; c.CoverageTier = null; c.Quantity = null; c.MaxDependants = null;
            c.DependantScope = DependantScopes.None; c.LimitPeriod = null; c.MinServiceMonths = null; c.AfterProbation = false;
            c.NationalityScope = NationalityScopes.Any; c.NationalityBasis = null;
            return FloorError(rule, c);
        }

        if (c.ValueType is null || !rule.AllowedValueTypes.Contains(c.ValueType))
            return Error(c, "value_type_not_allowed", $"Choose how {rule.NameEn.ToLowerInvariant()} is given: {string.Join(", ", rule.AllowedValueTypes)}.");
        c.DependantScope ??= rule.AllowedDependantScopes.Count == 1 ? rule.AllowedDependantScopes[0] : DependantScopes.None;
        c.LimitPeriod ??= rule.DefaultLimitPeriod;
        c.NationalityScope ??= NationalityScopes.Any;
        c.NationalityBasis = string.IsNullOrWhiteSpace(c.NationalityBasis) ? null : c.NationalityBasis.Trim();

        var shape = c.ValueType switch
        {
            GradeEntitlementValueTypes.Amount when c.Amount is not decimal a || !Money(a) || c.Rate is not null =>
                "Enter a positive amount with at most two decimals.",
            GradeEntitlementValueTypes.PercentOfBasic when c.Rate is not decimal r || r <= 0 || r > 1 || decimal.Round(r, 4) != r || c.Amount is not null =>
                "Enter a percentage of basic salary above 0 and at most 100.",
            GradeEntitlementValueTypes.InKind when c.Amount is not null || c.Rate is not null =>
                "Provided in kind has no amount or percentage.",
            GradeEntitlementValueTypes.CoverageTier when c.CoverageTier is null || !rule.AllowedCoverageTiers.Contains(c.CoverageTier) || c.Amount is not null || c.Rate is not null =>
                "Choose the class of cover.",
            GradeEntitlementValueTypes.Quantity when c.Quantity is not short q || q <= 0 || q > 99 || c.Amount is not null || c.Rate is not null =>
                "Enter how many (1 to 99).",
            _ => null,
        };
        if (shape != null) return Error(c, "invalid_value", shape);
        if (c.Quantity is not null && c.ValueType != GradeEntitlementValueTypes.Quantity)
            return Error(c, "invalid_value", "A count only goes with a benefit given as a number of items. Use 'dependants covered' for people.");
        if (c.CoverageTier is not null && !rule.AllowedCoverageTiers.Contains(c.CoverageTier))
            return Error(c, "invalid_value", "That class doesn't apply to this benefit.");
        if (!rule.AllowedDependantScopes.Contains(c.DependantScope))
            return Error(c, "dependants_not_allowed", $"Choose who is covered: {string.Join(", ", rule.AllowedDependantScopes)}.");
        if (c.MaxDependants is short max && (max < 0 || max > 20 || c.DependantScope == DependantScopes.None))
            return Error(c, "dependants_invalid", "Dependants covered must be between 0 and 20, and only when family members are covered.");
        if (c.LimitPeriod is not null && !rule.AllowedLimitPeriods.Contains(c.LimitPeriod))
            return Error(c, "limit_period_not_allowed", $"Choose when it resets: {string.Join(", ", rule.AllowedLimitPeriods)}.");
        if (c.MinServiceMonths is short months && (months < 0 || months > 600))
            return Error(c, "service_months_invalid", "Months of service must be between 0 and 600.");
        if (c.MinServiceMonths == 0) c.MinServiceMonths = null;
        if (!NationalityScopes.All.Contains(c.NationalityScope))
            return Error(c, "nationality_invalid", "Choose Any, Saudi or Non-Saudi.");
        if (c.NationalityScope == NationalityScopes.Any) c.NationalityBasis = null;
        else if (c.NationalityBasis is null)
            return Error(c, ReleaseABlockReasons.EntitlementReasonRequired, "A nationality condition needs its legal basis recorded.");
        if (c.NationalityBasis is { Length: > 300 }) return Error(c, "basis_too_long", "Keep the legal basis under 300 characters.");
        return FloorError(rule, c);
    }

    private static MatrixCellError? FloorError(EntitlementComponentRule rule, MatrixCellInput c)
    {
        var codes = EntitlementComponentRules.FloorViolations(rule, c.Eligible, c.ValueType ?? GradeEntitlementValueTypes.EligibilityOnly,
            c.Amount, c.Rate, c.CoverageTier, c.DependantScope ?? DependantScopes.None, c.AfterProbation, c.NationalityScope ?? NationalityScopes.Any);
        if (codes.Count == 0) return null;
        var reason = BlockReasonDto.For(codes[0]);
        return new MatrixCellError(c.GradeId, c.ComponentCode, codes[0], reason?.TitleEn ?? codes[0], reason);
    }

    /// <summary>Age, gender (or sex), marital status and disability are never conditions, whatever the property is called.</summary>
    internal static bool IsBannedCriterion(string key)
    {
        var k = new string(key.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return k.StartsWith("age", StringComparison.Ordinal) || k.StartsWith("gender", StringComparison.Ordinal)
            || k.StartsWith("sex", StringComparison.Ordinal) || k.StartsWith("marital", StringComparison.Ordinal)
            || k.StartsWith("disab", StringComparison.Ordinal)
            || EntitlementComponentRules.BannedCriteria.Any(b => string.Equals(b, key, StringComparison.OrdinalIgnoreCase));
    }

    // ── Offerings (adopt / skip per company) ─────────────────────────────────────────────────────

    /// <summary>
    /// A company's "we offer / don't offer this benefit" switch, from the first day of a month (pay-component versions
    /// cover whole payroll periods). Statutory floors are refused with the legal reason; loans are switched in Loans.
    /// </summary>
    public Task<MatrixResult> SetOfferingAsync(MatrixActor actor, SetOfferingRequest req, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync(db, LockScope, actor.TenantId, actor.TenantId, async () =>
        {
            var tid = actor.TenantId;
            if (req.CompanyId == Guid.Empty) return Bad("company_required", "Choose the company.");
            if (await ScopeProblemAsync(actor, req.CompanyId, ct) is { } scopeProblem) return scopeProblem;
            var rule = EntitlementComponentRules.For((req.ComponentCode ?? string.Empty).Trim());
            if (rule == null)
                return new MatrixResult(StatusCodes.Status404NotFound, new { error = "unknown_component", message = "This benefit isn't in the catalogue." });
            if (rule.IsLoanFacility)
                return Conflict("loan_offering_in_loans", "Each company's loan types are switched on or off in Loans → Loan policies.");
            if (rule.IsFloor)
            {
                var code = FloorCode(rule.Floor)!;
                var reason = BlockReasonDto.For(code)!;
                return new MatrixResult(StatusCodes.Status409Conflict, new { error = code, message = reason.TitleEn + ". " + reason.WhyEn, reason });
            }
            if (!CanSkipHere(rule))
                return Conflict("wage_component_not_skippable",
                    $"{rule.NameEn} is paid through payroll, so it can't be switched off per company. Mark the grades 'Not offered' for this company instead.");
            var today = await clock.TodayAsync(tid, ct);
            if (req.EffectiveFrom.Day != 1)
                return Bad("effective_from_not_month_start", "Offering changes start on the first day of a month.");
            if (req.EffectiveFrom < today)
                return Bad("effective_from_in_past", "Offering changes can't start in the past. Choose the first day of a coming month.");

            await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, tid, ct);
            await db.SaveChangesAsync(ct);
            var template = await ScopedBypass.TenantWide(db.PayComponents, tid, "The group catalogue row is the template for a company switch.")
                .AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == null && x.Code == rule.Code && !x.IsDeleted, ct);
            if (template == null)
                return Conflict("catalogue_row_missing", "This benefit isn't in your pay component catalogue yet.");
            var rows = await ScopedBypass.TenantWide(db.PayComponents, tid, "A company's own versions of a catalogue code; company access checked above.")
                .Where(x => x.CompanyId == req.CompanyId && x.Code == rule.Code && x.ComponentType == template.ComponentType)
                .ToListAsync(ct);
            var live = rows.Where(x => !x.IsDeleted).ToList();
            var later = live.Where(x => x.EffectiveFrom > req.EffectiveFrom).OrderBy(x => x.EffectiveFrom).FirstOrDefault();
            if (later != null)
                return Conflict("later_change_exists",
                    $"A change for {rule.NameEn.ToLowerInvariant()} is already scheduled from {later.EffectiveFrom:yyyy-MM-dd}. Choose that month or a later one.");
            var marker = live.FirstOrDefault(x => !x.IsOffered && x.IsInEffect(req.EffectiveFrom));

            if (!req.Offered)
            {
                if (marker != null) return Ok(await OfferingAsync(tid, req.CompanyId, rule, today, ct));
                // The unique key is (tenant, company, code, type, effective_from) and counts deleted rows: reuse one.
                var row = rows.FirstOrDefault(x => x.EffectiveFrom == req.EffectiveFrom);
                if (row == null)
                {
                    row = new PayComponent { TenantId = tid, CompanyId = req.CompanyId, Code = rule.Code, CreatedBy = actor.UserId };
                    db.PayComponents.Add(row);
                }
                row.NameEn = template.NameEn; row.NameAr = template.NameAr; row.ComponentType = template.ComponentType;
                row.CalcMethod = template.CalcMethod; row.Value = null; row.DisplayOrder = template.DisplayOrder;
                row.EntitlementClass = template.EntitlementClass; row.StatutoryFloor = template.StatutoryFloor;
                row.WpsIncluded = false; row.IsSystem = false; row.IsActive = true; row.IsDeleted = false;
                row.IsOffered = false; row.EffectiveFrom = req.EffectiveFrom; row.EffectiveTo = null;
                row.UpdatedAtUtc = DateTime.UtcNow; row.UpdatedBy = actor.UserId;
                db.AuditLogs.Add(Audit(actor, req.CompanyId, AuditSkipped, row.Id, new { componentCode = rule.Code, req.CompanyId, req.EffectiveFrom }));
            }
            else
            {
                if (marker == null) return Ok(await OfferingAsync(tid, req.CompanyId, rule, today, ct));
                // A skip that has not started by then is withdrawn; one already in force ends the day before.
                var withdrawn = marker.EffectiveFrom >= req.EffectiveFrom;
                if (withdrawn) marker.IsDeleted = true;
                else marker.EffectiveTo = req.EffectiveFrom.AddDays(-1);
                marker.UpdatedAtUtc = DateTime.UtcNow; marker.UpdatedBy = actor.UserId;
                db.AuditLogs.Add(Audit(actor, req.CompanyId, AuditOffered, marker.Id, new
                {
                    componentCode = rule.Code, req.CompanyId, req.EffectiveFrom, skippedFrom = marker.EffectiveFrom, withdrawn,
                }));
            }
            await db.SaveChangesAsync(ct);
            return Ok(await OfferingAsync(tid, req.CompanyId, rule, today, ct));
        }, ct);

    private async Task<MatrixOfferingDto> OfferingAsync(Guid tid, Guid companyId, EntitlementComponentRule rule, DateOnly today, CancellationToken ct)
    {
        var matrix = await ReadAsync(tid, companyId, today, ct);
        return matrix.Offerings.Single(o => o.ComponentCode == rule.Code);
    }

    // ── Legacy import (grade_pay_scale_components, benefit_eligibility_rules) ────────────────────

    /// <summary>
    /// Brings the frozen legacy grade mechanisms into the matrix as group cells. Preview (<paramref name="commit"/> false)
    /// and commit return the same items; commit publishes the "Import" ones through the matrix publish (same rules, audit
    /// and close-only dating). Rows that cannot be mapped without guessing — a medical class, a ticket count, an
    /// eligibility rule that has no value — are listed with the reason and never written. Idempotent: a second commit
    /// finds the cells already set and imports nothing. Nothing legacy is changed or dropped.
    /// </summary>
    public async Task<MatrixResult> ImportLegacyAsync(MatrixActor actor, DateOnly? effectiveFrom, bool commit, CancellationToken ct)
    {
        if (!actor.Scope.IsGroupLevel) return GroupScopeRequired();
        var tid = actor.TenantId;
        var today = await clock.TodayAsync(tid, ct);
        var from = effectiveFrom ?? today;
        if (from < today) return Bad("effective_from_in_past", "Choose today or a later date for the imported values to start.");
        if (!commit) return Ok(await LegacyPlanAsync(tid, from, today, ct));
        return await FinanceDecisionSerializer.SerializeAsync(db, LockScope, tid, tid, async () =>
        {
            var plan = await LegacyPlanAsync(tid, from, today, ct);
            var cells = plan.Items.Where(i => i.Outcome == LegacyOutcomes.Import).Select(i => Clone(i.Cell!)).ToList();
            if (cells.Count == 0) return Ok(plan with { Committed = true });
            var published = await PublishCoreAsync(actor, new PublishMatrixRequest(null, from, cells), SourceRuleImport, dryRun: false, ct);
            if (!published.IsSuccess) return published;
            var result = (PublishMatrixResult)published.Body;
            return Ok(plan with { Committed = true, Imported = result.Published });
        }, ct);
    }

    private async Task<LegacyImportResult> LegacyPlanAsync(Guid tid, DateOnly from, DateOnly today, CancellationToken ct)
    {
        var grades = await ActiveGradesAsync(tid, ct);
        var gradeIds = grades.Select(g => g.Id).ToArray();
        var lines = await db.GradePayScaleComponents.AsNoTracking()
            .Where(x => x.TenantId == tid && gradeIds.Contains(x.GradeId)).ToListAsync(ct);
        var groupCells = (await ScopedBypass.TenantWide(db.GradeEntitlements, tid, "The import never overwrites a group cell already set in the matrix.")
                .AsNoTracking()
                .Where(x => x.CompanyId == null && (x.EffectiveTo == null || x.EffectiveTo >= from))
                .Select(x => new { x.GradeId, x.PayComponentCode }).ToListAsync(ct))
            .Select(x => (x.GradeId, x.PayComponentCode)).ToHashSet();

        var proposals = lines
            .OrderBy(l => grades.FindIndex(g => g.Id == l.GradeId)).ThenBy(l => l.SortOrder).ThenBy(l => l.ComponentCode)
            .Select(line => (Line: line, Grade: grades.First(g => g.Id == line.GradeId), Map: MapPayScaleLine(line)))
            .ToList();
        var items = new List<LegacyImportItem>();
        foreach (var p in proposals)
        {
            var (code, reasonCode, reason, cell) = p.Map;
            if (cell != null && proposals.Count(o => o.Grade.Id == p.Grade.Id && o.Map.Code == code && o.Map.Cell != null) > 1)
                (reasonCode, reason, cell) = ("duplicate_mapping", $"More than one line of this grade maps to {code}. Set the value in the matrix.", null);
            if (cell != null && groupCells.Contains((p.Grade.Id, code!)))
                (reasonCode, reason, cell) = ("already_set", "The matrix already has a value for this grade. It is kept.", null);
            if (cell != null && ValidateCell(EntitlementComponentRules.For(code!)!, cell) is { } problem)
                (reasonCode, reason, cell) = (problem.Code, problem.Message, null);
            var detail = p.Line.CalculationType == "PercentOfBasic"
                ? $"{p.Line.Percentage:0.##}% of basic · {p.Line.Frequency}"
                : $"{p.Line.Amount:N2} · {p.Line.Frequency}";
            items.Add(new LegacyImportItem("PayScale", p.Line.Id, p.Grade.Id, p.Grade.Code, p.Grade.Name, p.Grade.NameAr,
                p.Line.ComponentCode, p.Line.ComponentName, detail, code,
                cell != null ? LegacyOutcomes.Import : LegacyOutcomes.Skip, cell != null ? null : reasonCode, cell != null ? null : reason, cell));
        }

        // Eligibility rules say WHO may enrol, never HOW MUCH: they are listed for HR to carry over, never guessed into a value.
        var rules = await (from r in db.BenefitEligibilityRules.AsNoTracking()
                           join plan in db.BenefitPlans.AsNoTracking() on r.BenefitPlanId equals plan.Id
                           where r.TenantId == tid && r.IsActive && r.GradeId != null && !plan.IsDeleted
                               && (r.EffectiveTo == null || r.EffectiveTo >= today)
                           select new { r.Id, GradeId = r.GradeId!.Value, r.CompanyId, plan.Code, plan.Name, plan.PlanType, r.EffectiveFrom })
            .ToListAsync(ct);
        foreach (var r in rules.OrderBy(r => r.Code).ThenBy(r => r.Id))
        {
            var grade = grades.FirstOrDefault(g => g.Id == r.GradeId);
            if (grade == null) continue;
            var suggested = MapLegacyCode(r.PlanType) ?? MapLegacyCode(r.Code);
            items.Add(new LegacyImportItem("Eligibility", r.Id, grade.Id, grade.Code, grade.Name, grade.NameAr, r.Code, r.Name,
                $"{r.PlanType} · from {r.EffectiveFrom:yyyy-MM-dd}" + (r.CompanyId is null ? string.Empty : " · one company"), suggested,
                LegacyOutcomes.Skip, "eligibility_only",
                "An eligibility rule says who may enrol, not what they get. Set the value for this grade in the matrix.", null));
        }
        var toImport = items.Count(i => i.Outcome == LegacyOutcomes.Import);
        return new LegacyImportResult(false, from, toImport, items.Count - toImport, 0, items);
    }

    /// <summary>Maps one legacy pay-scale line to a cell, or says plainly why it can't be mapped without guessing.</summary>
    internal static (string? Code, string? ReasonCode, string? Reason, MatrixCellInput? Cell) MapPayScaleLine(GradePayScaleComponent line)
    {
        if (Normalise(line.ComponentCode) is "BASIC" or "BASICSALARY")
            return (null, "basic_salary", "Basic salary is set on the salary record and the grade's pay range, not in the benefits matrix.", null);
        var code = MapLegacyCode(line.ComponentCode) ?? MapLegacyCode(line.ComponentName);
        if (code == null) return (null, "no_matching_benefit", "No benefit in the matrix matches this line.", null);
        if (!line.IsActive) return (code, "inactive_line", "This line was switched off.", null);
        var percent = line.CalculationType == "PercentOfBasic";
        var monthly = string.Equals(line.Frequency, "Monthly", StringComparison.OrdinalIgnoreCase);
        var annual = string.Equals(line.Frequency, "Annual", StringComparison.OrdinalIgnoreCase);
        var cell = new MatrixCellInput { GradeId = line.GradeId, ComponentCode = code, Eligible = true, Note = "Imported from the grade pay scale" };
        switch (code)
        {
            case EntitlementComponentRules.Housing or EntitlementComponentRules.Transport or EntitlementComponentRules.OtherAllowances:
                if (!monthly) return (code, "not_monthly", "This allowance is paid monthly, but the line holds a yearly figure. Enter the monthly value in the matrix.", null);
                if (percent)
                {
                    if (line.Percentage <= 0 || line.Percentage > 100) return (code, "percentage_invalid", "The percentage must be above 0 and at most 100.", null);
                    cell.ValueType = GradeEntitlementValueTypes.PercentOfBasic;
                    cell.Rate = decimal.Round(line.Percentage / 100m, 4);
                }
                else
                {
                    if (line.Amount <= 0) return (code, "zero_amount", "The line has no amount.", null);
                    cell.ValueType = GradeEntitlementValueTypes.Amount;
                    cell.Amount = line.Amount;
                }
                return (code, null, null, cell);
            case EntitlementComponentRules.Education:
                if (percent || !annual || line.Amount <= 0)
                    return (code, "needs_annual_amount", "Education is a yearly amount per child. Enter it in the matrix.", null);
                cell.ValueType = GradeEntitlementValueTypes.Amount;
                cell.Amount = line.Amount;
                cell.DependantScope = DependantScopes.Children;
                return (code, null, null, cell);
            case EntitlementComponentRules.AirTicket:
                return (code, "needs_ticket_details", "A ticket needs a count, a class and who travels. Set them in the matrix.", null);
            case EntitlementComponentRules.Medical:
                return (code, "needs_medical_class", "Medical cover needs its class (basic, C, B, A or VIP). Set it in the matrix.", null);
            default:
                return (code, "needs_daily_rate", "Per diem is a daily rate, but the line holds a monthly or yearly figure. Enter the daily rate in the matrix.", null);
        }
    }

    internal static string? MapLegacyCode(string? raw) => Normalise(raw) switch
    {
        "HOUSING" or "HOUSINGALLOWANCE" or "HRA" or "ACCOMMODATION" => EntitlementComponentRules.Housing,
        "TRANSPORT" or "TRANSPORTATION" or "TRANSPORTALLOWANCE" => EntitlementComponentRules.Transport,
        "OTHER" or "OTHERALLOWANCE" or "OTHERALLOWANCES" => EntitlementComponentRules.OtherAllowances,
        "TICKET" or "TICKETS" or "AIRTICKET" or "AIRFARE" => EntitlementComponentRules.AirTicket,
        "INSURANCE" or "HEALTH" or "MEDICAL" or "MEDICALINSURANCE" or "HEALTHINSURANCE" => EntitlementComponentRules.Medical,
        "EDUCATION" or "SCHOOL" or "SCHOOLING" or "EDUCATIONALLOWANCE" => EntitlementComponentRules.Education,
        "PERDIEM" => EntitlementComponentRules.PerDiem,
        _ => null,
    };

    private static string Normalise(string? raw) => new string((raw ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<MatrixResult?> ScopeProblemAsync(MatrixActor actor, Guid? companyId, CancellationToken ct)
    {
        if (companyId is null) return actor.Scope.IsGroupLevel ? null : GroupScopeRequired();
        if (!actor.Scope.CanAccessCompany(companyId))
            return new MatrixResult(StatusCodes.Status403Forbidden, new { error = "company_not_accessible", message = "You don't have access to that company." });
        if (!await db.Companies.AnyAsync(c => c.TenantId == actor.TenantId && c.Id == companyId && !c.IsDeleted, ct))
            return Bad("company_not_found", "That company doesn't exist.");
        return null;
    }

    private static MatrixResult GroupScopeRequired() => new(StatusCodes.Status403Forbidden, new
    {
        error = "group_scope_required",
        message = "Benefits for all companies can only be changed by someone with access to every company. Choose your company instead.",
    });

    private static MatrixResult Ok(object body) => new(StatusCodes.Status200OK, body);
    private static MatrixResult Bad(string error, string message) => new(StatusCodes.Status400BadRequest, new { error, message });
    private static MatrixResult Conflict(string error, string message) => new(StatusCodes.Status409Conflict, new { error, message });

    private static MatrixCellError Error(MatrixCellInput c, string code, string message) =>
        new(c.GradeId, c.ComponentCode, code, message, BlockReasonDto.For(code));

    private static string? FloorCode(string floor) => floor switch
    {
        PayStatutoryFloors.Housing => ReleaseABlockReasons.EntitlementFloorHousing,
        PayStatutoryFloors.Transport => ReleaseABlockReasons.EntitlementFloorTransport,
        PayStatutoryFloors.Medical => ReleaseABlockReasons.EntitlementFloorMedical,
        PayStatutoryFloors.Art40 => ReleaseABlockReasons.EntitlementArt40ReadOnly,
        _ => null,
    };

    private async Task<List<Grade>> ActiveGradesAsync(Guid tid, CancellationToken ct) =>
        await db.Grades.AsNoTracking().Where(x => x.TenantId == tid && !x.IsDeleted && x.IsActive)
            .OrderBy(x => x.Level).ThenBy(x => x.Code).ToListAsync(ct);

    private async Task<List<Guid>> ActiveCompanyIdsAsync(Guid tid, CancellationToken ct) =>
        await ScopedBypass.TenantWide(db.Companies, tid, "Group completeness is checked against every active company.")
            .Where(x => !x.IsDeleted && x.IsActive).Select(x => x.Id).ToListAsync(ct);

    /// <summary>The catalogue, plus the loan facilities of grade-limited loan types (shown read-only).</summary>
    private async Task<List<EntitlementComponentRule>> ComponentRulesAsync(Guid tid, CancellationToken ct)
    {
        var rules = EntitlementComponentRules.Catalogue.ToList();
        var loans = await db.LoanTypes.AsNoTracking()
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.EntitlementComponentCode != null)
            .Select(x => new { Code = x.EntitlementComponentCode!, x.NameEn, x.NameAr }).ToListAsync(ct);
        foreach (var loan in loans.OrderBy(l => l.Code, StringComparer.Ordinal))
        {
            if (rules.Any(r => string.Equals(r.Code, loan.Code, StringComparison.OrdinalIgnoreCase))) continue;
            if (EntitlementComponentRules.For(loan.Code) is { } rule)
                rules.Add(rule with { NameEn = loan.NameEn, NameAr = string.IsNullOrWhiteSpace(loan.NameAr) ? loan.NameEn : loan.NameAr });
        }
        return rules;
    }

    private async Task<List<PayComponent>> SkipMarkersAsync(Guid tid, Guid companyId, CancellationToken ct) =>
        await ScopedBypass.TenantWide(db.PayComponents, tid, "A company's offered switches; the caller authorised the company.")
            .AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && !x.IsOffered)
            .ToListAsync(ct);

    private static MatrixComponentDto ToDto(EntitlementComponentRule r) =>
        new(r.Code, r.NameEn, r.NameAr, r.Class, r.Floor, GroupOf(r), r.AllowedValueTypes, r.AllowedCoverageTiers,
            r.AllowedDependantScopes, r.AllowedLimitPeriods, r.DefaultLimitPeriod, r.IsFloor, CanSkipHere(r), r.IsLoanFacility,
            BlockReasonDto.For(FloorCode(r.Floor)));

    private static MatrixCellDto ToDto(GradeEntitlement c, Guid? viewCompanyId, DateOnly? nextChange) =>
        new(c.GradeId, c.PayComponentCode, c.Id, c.CompanyId.HasValue, viewCompanyId.HasValue && !c.CompanyId.HasValue, c.Eligible, c.ValueType,
            c.Amount, c.Rate, c.MaxOutstandingAmount, c.CoverageTier, c.Quantity, c.DependantScope, c.MaxDependants, c.LimitPeriod,
            c.MinServiceMonths, c.AfterProbation, c.NationalityScope, c.NationalityBasis, c.SourceRule, c.Note, c.EffectiveFrom,
            c.EffectiveTo, nextChange);

    private static GradeEntitlement NewCell(Guid tid, Guid? companyId, Guid gradeId, EntitlementComponentRule rule, MatrixCellInput c,
        DateOnly from, string sourceRule, Guid? userId) => new()
    {
        TenantId = tid, CompanyId = companyId, GradeId = gradeId, PayComponentCode = rule.Code, EntitlementClass = rule.Class,
        Eligible = c.Eligible, ValueType = c.ValueType ?? GradeEntitlementValueTypes.EligibilityOnly, Amount = c.Amount, Rate = c.Rate,
        CoverageTier = c.CoverageTier, Quantity = c.Quantity, DependantScope = c.DependantScope ?? DependantScopes.None,
        MaxDependants = c.MaxDependants, LimitPeriod = c.LimitPeriod, MinServiceMonths = c.MinServiceMonths,
        AfterProbation = c.AfterProbation, NationalityScope = c.NationalityScope ?? NationalityScopes.Any,
        NationalityBasis = c.NationalityBasis, Note = c.Note, SourceRule = sourceRule, EffectiveFrom = from, CreatedBy = userId,
    };

    private static bool SameValues(GradeEntitlement a, GradeEntitlement b) =>
        a.Eligible == b.Eligible && a.ValueType == b.ValueType && a.Amount == b.Amount && a.Rate == b.Rate
        && a.CoverageTier == b.CoverageTier && a.Quantity == b.Quantity && a.DependantScope == b.DependantScope
        && a.MaxDependants == b.MaxDependants && a.LimitPeriod == b.LimitPeriod && a.MinServiceMonths == b.MinServiceMonths
        && a.AfterProbation == b.AfterProbation && a.NationalityScope == b.NationalityScope && a.NationalityBasis == b.NationalityBasis
        && (a.Note ?? string.Empty) == (b.Note ?? string.Empty);

    private static object Snapshot(GradeEntitlement c) => new
    {
        c.Id, c.Eligible, c.ValueType, c.Amount, c.Rate, c.CoverageTier, c.Quantity, c.DependantScope, c.MaxDependants, c.LimitPeriod,
        c.MinServiceMonths, c.AfterProbation, c.NationalityScope, c.NationalityBasis, c.Note, c.EffectiveFrom, c.EffectiveTo,
    };

    private static MatrixCellInput Clone(MatrixCellInput c) => new()
    {
        GradeId = c.GradeId, ComponentCode = c.ComponentCode, Eligible = c.Eligible, ValueType = c.ValueType, Amount = c.Amount, Rate = c.Rate,
        CoverageTier = c.CoverageTier, Quantity = c.Quantity, DependantScope = c.DependantScope, MaxDependants = c.MaxDependants,
        LimitPeriod = c.LimitPeriod, MinServiceMonths = c.MinServiceMonths, AfterProbation = c.AfterProbation,
        NationalityScope = c.NationalityScope, NationalityBasis = c.NationalityBasis, Note = c.Note,
    };

    private static AuditLog Audit(MatrixActor actor, Guid? companyId, string action, Guid entityId, object metadata) => new()
    {
        TenantId = actor.TenantId, CompanyId = companyId, UserId = actor.UserId, Action = action,
        EntityName = action.StartsWith("entitlements.offering", StringComparison.Ordinal) ? "PayComponent" : "GradeEntitlement",
        EntityId = entityId.ToString(), IpAddress = actor.IpAddress, CreatedAtUtc = DateTime.UtcNow,
        Metadata = JsonSerializer.Serialize(metadata),
    };

    /// <summary>
    /// How many current employees the change reaches, and when. Facilities (per diem) are read when used, so they change
    /// on the effective date; the wage standard and contract benefits reach an employee at their next contract year (a
    /// running year keeps the package fixed for it). A group change skips companies with their own value for that cell.
    /// </summary>
    private async Task<(int Now, int AtRenewal)> ImpactAsync(Guid tid, Guid? companyId, DateOnly from,
        List<(Guid GradeId, string Code)> touched, CancellationToken ct)
    {
        if (touched.Count == 0) return (0, 0);
        var gradeIds = touched.Select(t => t.GradeId).Distinct().ToArray();
        var employees = await ScopedBypass.NullableTenantWide(db.Employees, tid, "Counting the employees a published grade change reaches; scope authorised by the caller.")
            .AsNoTracking()
            .Where(e => !e.IsDeleted && e.GradeId != null && gradeIds.Contains(e.GradeId.Value) && CurrentEmployeeStatuses.Contains(e.Status)
                && (companyId == null || e.CompanyId == companyId))
            .Select(e => new { e.Id, GradeId = e.GradeId!.Value, e.CompanyId })
            .ToListAsync(ct);
        var overrides = companyId is null
            ? (await ScopedBypass.TenantWide(db.GradeEntitlements, tid, "A group change does not reach companies with their own value.")
                .AsNoTracking()
                .Where(x => x.CompanyId != null && gradeIds.Contains(x.GradeId) && x.EffectiveFrom <= from && (x.EffectiveTo == null || x.EffectiveTo >= from))
                .Select(x => new { x.GradeId, x.PayComponentCode, x.CompanyId }).ToListAsync(ct))
                .Select(x => (x.GradeId, x.PayComponentCode, x.CompanyId)).ToHashSet()
            : [];
        var now = new HashSet<int>();
        var atRenewal = new HashSet<int>();
        foreach (var (gradeId, code) in touched)
        {
            var facility = EntitlementComponentRules.For(code)?.Class == PayEntitlementClasses.Facility;
            foreach (var e in employees.Where(e => e.GradeId == gradeId))
            {
                if (companyId is null && overrides.Contains((gradeId, code, e.CompanyId))) continue;
                (facility ? now : atRenewal).Add(e.Id);
            }
        }
        return (now.Count, atRenewal.Count);
    }

    /// <summary>
    /// "Benefits not set" raises one in-app item for each Admin / HR Manager / HR Director who can act for the scope, at
    /// most once per scope in 24 hours. Raised on publish only, never on a read. Does not save.
    /// </summary>
    private async Task<int> NotifyGapsAsync(Guid tid, Guid? companyId, int gapCount, CancellationToken ct)
    {
        var entityId = companyId?.ToString() ?? "group";
        var since = DateTime.UtcNow.AddHours(-24);
        if (await db.Notifications.AnyAsync(x => x.TenantId == tid && x.EntityName == GapNotificationEntity && x.EntityId == entityId && x.CreatedAtUtc >= since, ct))
            return 0;
        var roles = new[] { "Admin", "HR Manager", "HR Director" };
        var recipients = await (from user in db.Users.AsNoTracking()
                                join assignment in db.UserRoles on user.Id equals assignment.UserId
                                join role in db.Roles on assignment.RoleId equals role.Id
                                where user.TenantId == tid && user.IsActive && !user.IsDeleted
                                    && (role.TenantId == tid || role.TenantId == null) && role.IsActive && !role.IsDeleted && roles.Contains(role.Name)
                                    && (user.IsGroupScope || (companyId != null && db.UserEntityAccesses.Any(x => x.TenantId == tid && x.UserId == user.Id && x.IsActive
                                        && (x.CompanyId == companyId || x.GrantMode == "AllCurrentCompanies" || x.GrantMode == "AllCurrentAndFutureCompanies"))))
                                select user.Id).Distinct().ToListAsync(ct);
        if (recipients.Count == 0) return 0;
        var message = $"{gapCount} benefit value(s) are not set for some grades, so those employees' packages can't be worked out. "
            + "Open Benefits by grade and set a value, or 'Not offered', for every grade.";
        db.Notifications.AddRange(recipients.Select(userId => new Notification
        {
            TenantId = tid, UserId = userId, Title = "Benefits by grade incomplete", Message = message,
            EntityName = GapNotificationEntity, EntityId = entityId,
        }));
        return recipients.Count;
    }
}
