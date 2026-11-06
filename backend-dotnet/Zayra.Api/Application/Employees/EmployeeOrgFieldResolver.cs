using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>
/// ONE shared component for resolving free-text org fields (department / designation / branch)
/// to their IDs. Used by every path that historically wrote string-only org data and thereby
/// manufactured uncountable employees (spec Conflict R7 defect class):
///  - EmployeesController.ApplyChanges (direct PUT + sensitive-change approve),
///  - ApprovalWorkflowService.ApplyEmployeeChange (generic approvals decide),
///  - EmployeesController.ApproveDraft (onboarding),
///  - EmployeeManagementService.RequestTransferAsync / EmployeesController.ApproveHrTransfer.
/// A NON-EMPTY name that resolves to nothing throws (surfaced as 422): master data must be fixed,
/// not silently bypassed. Empty/absent names leave the field unchanged.
/// </summary>
public static class EmployeeOrgFieldResolver
{
    public sealed record ResolvedOrgRefs(Guid? DepartmentId, string? DepartmentName,
        Guid? DesignationId, string? DesignationTitle, Guid? BranchId, string? BranchName);

    /// <param name="companyId">Addendum B: departments belong to one company. When the employee's company is
    /// known the name resolves within it (an unassigned legacy row is the fallback); when it is not, a name that
    /// matches departments of several companies is refused rather than guessed.</param>
    public static async Task<(Guid Id, string NameEn)> ResolveDepartmentAsync(
        ZayraDbContext db, Guid tenantId, string name, CancellationToken ct, Guid? companyId = null)
    {
        var term = name.Trim();
        // IgnoreQueryFilters is intentional: master-data name→ID resolution must not vary with the caller's company scope; explicit TenantId + !IsDeleted + IsActive filters are applied inline.
        var matches = await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive
                     && (d.NameEn.ToLower() == term.ToLower() || d.Code.ToUpper() == term.ToUpper()))
            .Where(d => companyId == null || d.CompanyId == companyId || d.CompanyId == null)
            .Select(d => new { d.Id, d.NameEn, d.CompanyId })
            .ToListAsync(ct);
        var match = PickWithinCompany(matches, m => m.CompanyId, m => m.Id, companyId, "Department", term);
        if (match is null)
            throw new InvalidOperationException(
                $"Department '{term}' was not found in this tenant's organization master data. Create the department (or fix the name) before assigning employees to it.");
        return (match.Id, match.NameEn);
    }

    /// <param name="companyId">See <see cref="ResolveDepartmentAsync"/>.</param>
    public static async Task<(Guid Id, string TitleEn)> ResolveDesignationAsync(
        ZayraDbContext db, Guid tenantId, string title, CancellationToken ct, Guid? companyId = null)
    {
        var term = title.Trim();
        // IgnoreQueryFilters is intentional: master-data name→ID resolution must not vary with the caller's company scope; explicit TenantId + !IsDeleted + IsActive filters are applied inline.
        var matches = await db.Designations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive
                     && (d.TitleEn.ToLower() == term.ToLower() || d.Code.ToUpper() == term.ToUpper()))
            .Where(d => companyId == null || d.CompanyId == companyId || d.CompanyId == null)
            .Select(d => new { d.Id, d.TitleEn, d.CompanyId })
            .ToListAsync(ct);
        var match = PickWithinCompany(matches, m => m.CompanyId, m => m.Id, companyId, "Designation", term);
        if (match is null)
            throw new InvalidOperationException(
                $"Designation '{term}' was not found in this tenant's organization master data. Create the designation (or fix the title) before assigning employees to it.");
        return (match.Id, match.TitleEn);
    }

    /// <summary>The company's own row first, then an unassigned legacy row; lowest id breaks a tie so the pick is
    /// stable. Without a company, matches in several companies are ambiguous and refused.</summary>
    private static T? PickWithinCompany<T>(List<T> matches, Func<T, Guid?> company, Func<T, Guid> id, Guid? companyId, string label, string term)
        where T : class
    {
        if (matches.Count == 0) return null;
        if (companyId is null && matches.Select(company).Where(c => c is not null).Distinct().Count() > 1)
            throw new InvalidOperationException(
                $"{label} '{term}' exists in more than one company. Choose the employee's company first, or use the {label.ToLowerInvariant()} code.");
        return matches.OrderBy(m => company(m) is null ? 1 : 0).ThenBy(id).First();
    }

    public static async Task<(Guid Id, string NameEn)> ResolveBranchAsync(
        ZayraDbContext db, Guid tenantId, string name, CancellationToken ct)
    {
        var term = name.Trim();
        // IgnoreQueryFilters is intentional: master-data name→ID resolution must not vary with the caller's company scope; explicit TenantId + !IsDeleted + IsActive filters are applied inline.
        var match = await db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted && b.IsActive
                     && (b.NameEn.ToLower() == term.ToLower() || b.Code.ToUpper() == term.ToUpper()))
            .Select(b => new { b.Id, b.NameEn })
            .FirstOrDefaultAsync(ct);
        if (match is null)
            throw new InvalidOperationException(
                $"Branch '{term}' was not found in this tenant's organization master data. Create the branch (or fix the name) before assigning employees to it.");
        return (match.Id, match.NameEn);
    }

    /// <summary>
    /// Post-processes a change-set application: whenever the change dictionary carried
    /// "department" / "designation" / "branch" free-text values, resolves them tenant-scoped and
    /// stamps the corresponding *Id columns (and canonical display strings) on the employee.
    /// Unresolvable non-empty name ⇒ InvalidOperationException (callers surface 422).
    /// Clearing (empty string) nulls the ID — an explicit, countable "no department".
    /// </summary>
    public static async Task ResolveAppliedChangesAsync(ZayraDbContext db, Guid tenantId, Employee employee,
        IEnumerable<string> changedFields, CancellationToken ct, PreparedCostCentre? costCentre = null)
    {
        var fields = new HashSet<string>(changedFields, StringComparer.OrdinalIgnoreCase);

        if (fields.Contains("department"))
        {
            if (string.IsNullOrWhiteSpace(employee.Department))
            {
                // Clearing a department the person has is a move (to "no department"); clearing one they never had moves nothing.
                if (employee.DepartmentId is not null) await MoveDepartmentAsync(db, tenantId, employee, null, keepOverride: false, ct);
                if (!fields.Contains("managerEmployeeId")) employee.ManagerEmployeeId = null;
            }
            else
            {
                var (id, nameEn) = await ResolveDepartmentAsync(db, tenantId, employee.Department, ct, employee.CompanyId);
                var unchanged = employee.DepartmentId == id;
                employee.Department = nameEn;
                // Re-sending the CURRENT department is not a transfer: the cost centre, its override flag and the manager stay as they are.
                if (!unchanged)
                {
                    // A move re-derives the cost centre from the new department. An override survives only on an explicit keep, which the
                    // prepared cost centre re-applies below (PrepareAttributionAsync refuses a move of an overridden person without a decision).
                    var departmentManager = await MoveDepartmentAsync(db, tenantId, employee, id, keepOverride: false, ct);
                    if (!fields.Contains("managerEmployeeId"))
                        employee.ManagerEmployeeId = departmentManager;
                }
            }
        }

        if (fields.Contains("designation"))
        {
            if (string.IsNullOrWhiteSpace(employee.Designation)) employee.DesignationId = null;
            else
            {
                var (id, titleEn) = await ResolveDesignationAsync(db, tenantId, employee.Designation, ct, employee.CompanyId);
                employee.DesignationId = id;
                employee.Designation = titleEn;
            }
        }

        if (fields.Contains("branch"))
        {
            if (string.IsNullOrWhiteSpace(employee.Branch)) employee.BranchId = null;
            else
            {
                var (id, nameEn) = await ResolveBranchAsync(db, tenantId, employee.Branch, ct);
                employee.BranchId = id;
                employee.Branch = nameEn;
            }
        }

        if (costCentre is not null) await ApplyCostCentreAsync(db, tenantId, employee, costCentre, ct);
        if (costCentre is not null || fields.Contains("department") || fields.Contains("costCenter"))
            await RefreshCachedCostCentreAsync(db, tenantId, employee, ct);
    }

    /// <summary>
    /// THE department-move rule, shared by every door that moves an existing employee (the PUT and approval paths through
    /// <see cref="ResolveAppliedChangesAsync"/>, and the HR transfer approval). The stored cost centre becomes the new department's copy and the
    /// override flag drops, unless <paramref name="keepOverride"/> and the person has one (the transfer approval's implicit keep). The cached
    /// code is refreshed. Returns the new department's default manager. Writes nothing to the database.
    /// </summary>
    public static async Task<int?> MoveDepartmentAsync(ZayraDbContext db, Guid tenantId, Employee employee, Guid? newDepartmentId, bool keepOverride, CancellationToken ct)
    {
        Guid? departmentCostCentre = null;
        int? departmentManager = null;
        if (newDepartmentId is Guid id)
        {
            var defaults = await ScopedBypass.TenantWide(db.Departments, tenantId,
                    "Department defaults follow the department just resolved for this employee, which is tenant master data independent of the caller's company scope.")
                .AsNoTracking()
                .Where(d => !d.IsDeleted && d.Id == id)
                .Select(d => new { d.CostCenterId, d.ManagerEmployeeId })
                .FirstOrDefaultAsync(ct);
            (departmentCostCentre, departmentManager) = (defaults?.CostCenterId, defaults?.ManagerEmployeeId);
        }
        employee.DepartmentId = newDepartmentId;
        if (!(keepOverride && employee.CostCenterIsOverride))
        {
            employee.CostCenterId = departmentCostCentre;
            employee.CostCenterIsOverride = false;
        }
        await RefreshCachedCostCentreAsync(db, tenantId, employee, ct);
        return departmentManager;
    }

    /// <summary>
    /// Wave A D3: the cached <c>Employee.CostCenter</c> string holds the RESOLVED code (override, position, department, parent department or
    /// company default), empty when nothing resolves. Nothing user-facing reads it (EmployeeCostCentreCacheReadRatchetTests); it is kept
    /// consistent for legacy readers. Writes nothing to the database.
    /// </summary>
    public static async Task RefreshCachedCostCentreAsync(ZayraDbContext db, Guid tenantId, Employee employee, CancellationToken ct)
    {
        employee.CostCenter = employee.CompanyId is Guid companyId
            ? (await EmployeeCostCentreView.DescribeAsync(db, tenantId, companyId, ResolverEmployee.From(employee), ct)).Code ?? string.Empty
            : string.Empty;
    }

    /// <summary>A "costCenter" patch value, resolved and validated BEFORE any column is touched. <see cref="Clear"/> = the value was blank: drop the override and inherit.</summary>
    public sealed record PreparedCostCentre(bool Clear, Guid? Id, string Code);

    /// <summary>
    /// The free-text "costCenter" patch key is a cost-centre CODE: it must resolve, in the employee's own company, to a live cost centre (active when
    /// it is a new assignment), else the whole patch is refused with nothing written. It used to be stored as a string and never read.
    /// </summary>
    public static async Task<PreparedCostCentre?> PrepareCostCentreAsync(ZayraDbContext db, Guid tenantId, Employee employee,
        IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (!changes.TryGetValue("costCenter", out var element)) return null;
        var raw = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() ?? string.Empty : string.Empty;
        if (raw.Length == 0) return new PreparedCostCentre(true, null, string.Empty);

        var code = OrgCodes.Normalize(raw);
        var match = await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.CompanyId == employee.CompanyId && c.Code.ToUpper() == code)
            .Select(c => new { c.Id, c.Code })
            .FirstOrDefaultAsync(ct);
        if (match is null)
            throw new InvalidOperationException($"Cost centre '{raw}' was not found among this employee's company's cost centres. No change was applied.");
        await new CostCentreRefValidator(db).EnsureAsync(tenantId, employee.CompanyId, match.Id, forNewAssignment: match.Id != employee.CostCenterId, ct);
        return new PreparedCostCentre(false, match.Id, match.Code);
    }

    public const string KeepDecision = "keep";
    public const string DropDecision = "drop";

    /// <summary>
    /// What a patch would do to where the person's cost goes, worked out from the SERVER's state before anything is written (Wave A D1/D2):
    /// whether the department moves (compared by id, never by the cached name), the prepared cost centre (an explicit "costCenter" key, or the
    /// keep/drop decision turned into one) and the decision itself.
    /// </summary>
    public sealed record PreparedAttribution(bool DepartmentMoves, Guid? NewDepartmentId, PreparedCostCentre? CostCentre, string? Decision);

    /// <summary>
    /// Prepares the attribution part of a patch. Throws (nothing written) when it cannot go ahead:
    /// <list type="bullet">
    /// <item>a decision that is not exactly "keep" or "drop" — 422 <c>cost_centre_decision_invalid</c>;</item>
    /// <item>a decision next to an explicit "costCenter" key, or where nobody with an override moves department — 422 <c>cost_centre_decision_invalid</c>;</item>
    /// <item>a decision made on a stale form (<paramref name="expectedOverrideId"/> is not the override stored now) — 409 <c>cost_centre_state_changed</c>;</item>
    /// <item>an overridden person moving department with no decision and no explicit cost centre — 422 <c>cost_centre_decision_required</c>;</item>
    /// <item>keep of an override that is no longer a live cost centre of the person's company — 422 <c>cost_centre_inactive|other_company|not_found</c>;</item>
    /// <item>a department or cost-centre code that does not resolve — <see cref="InvalidOperationException"/>.</item>
    /// </list>
    /// KEEP pins the override id read HERE, before any department reset replaces the stored id; DROP is a clear (the person inherits).
    /// </summary>
    public static async Task<PreparedAttribution> PrepareAttributionAsync(ZayraDbContext db, Guid tenantId, Employee employee,
        IReadOnlyDictionary<string, JsonElement> changes, string? decision, Guid? expectedOverrideId, CancellationToken ct)
    {
        if (decision is not null && decision is not (KeepDecision or DropDecision)) throw CostCentreRuleException.DecisionValueInvalid();

        var departmentMoves = false;
        Guid? newDepartmentId = employee.DepartmentId;
        if (changes.TryGetValue("department", out var department))
        {
            if (department.ValueKind == JsonValueKind.String)
            {
                var name = department.GetString()!.Trim();
                newDepartmentId = name.Length == 0 ? null : (await ResolveDepartmentAsync(db, tenantId, name, ct, employee.CompanyId)).Id;
                departmentMoves = newDepartmentId != employee.DepartmentId;
            }
            else if (department.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("The department must be text. No change was applied.");
        }

        var costCentre = await PrepareCostCentreAsync(db, tenantId, employee, changes, ct);
        var namesCostCentre = changes.ContainsKey("costCenter");
        if (decision is null)
        {
            if (employee.CostCenterIsOverride && departmentMoves && !namesCostCentre) throw CostCentreRuleException.DecisionRequired();
            return new(departmentMoves, newDepartmentId, costCentre, null);
        }

        if (namesCostCentre) throw CostCentreRuleException.DecisionWithCostCentre();
        var currentOverride = employee.CostCenterIsOverride ? employee.CostCenterId : null;
        if (expectedOverrideId != currentOverride) throw CostCentreRuleException.StateChanged();
        if (!employee.CostCenterIsOverride || !departmentMoves) throw CostCentreRuleException.DecisionNotApplicable();

        if (decision == DropDecision) return new(true, newDepartmentId, new PreparedCostCentre(true, null, string.Empty), decision);

        if (employee.CostCenterId is not Guid originalOverrideId)
            throw new CostCentreRefException(CostCentreRefCodes.NotFound, "The cost centre was not found in this tenant.");
        // Re-validated as a NEW assignment against the person's company: keeping must not carry an inactive or foreign cost centre into a move.
        await new CostCentreRefValidator(db).EnsureAsync(tenantId, employee.CompanyId, originalOverrideId, forNewAssignment: true, ct);
        var code = await db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == originalOverrideId).Select(c => c.Code).FirstAsync(ct);
        return new(true, newDepartmentId, new PreparedCostCentre(false, originalOverrideId, code), decision);
    }

    /// <summary>
    /// M-10 and Wave A reason hygiene: setting, re-pointing or removing the override, or deciding keep/drop, needs a reason (422
    /// <c>cost_centre_reason_required</c>); a reason holding control characters (other than line breaks and tabs) or bidirectional overrides
    /// (U+202A..U+202E, U+2066..U+2069) is refused (422 <c>reason_invalid_characters</c>). The 500-character cap is the <c>ReasonLimit</c> filter's.
    /// </summary>
    public static void EnsureAttributionReason(Employee employee, PreparedAttribution prepared, string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason) && HasInvalidReasonCharacters(reason)) throw CostCentreRuleException.ReasonInvalidCharacters();
        var needsReason = prepared.Decision is not null || (prepared.CostCentre is { } costCentre && ChangesOverride(employee, costCentre));
        if (needsReason && string.IsNullOrWhiteSpace(reason)) throw CostCentreRuleException.ReasonRequired();
    }

    public static bool HasInvalidReasonCharacters(string reason) =>
        reason.Any(c => (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || c is >= '\u202A' and <= '\u202E' || c is >= '\u2066' and <= '\u2069');

    /// <summary>Whether applying the prepared "costCenter" patch would set, re-point or remove the employee's override (so it needs a reason, M-10).</summary>
    public static bool ChangesOverride(Employee employee, PreparedCostCentre costCentre) =>
        costCentre.Clear ? employee.CostCenterIsOverride : !employee.CostCenterIsOverride || employee.CostCenterId != costCentre.Id;

    private static async Task ApplyCostCentreAsync(ZayraDbContext db, Guid tenantId, Employee employee, PreparedCostCentre costCentre, CancellationToken ct)
    {
        if (!costCentre.Clear)
        {
            employee.CostCenterId = costCentre.Id;
            employee.CostCenterIsOverride = true;
            employee.CostCenter = costCentre.Code;
            return;
        }

        // Blank: back to inheriting. The stored id becomes the department's copy (the resolver ignores it without the flag); the cached code is
        // the RESOLVED one, refreshed by the caller (ResolveAppliedChangesAsync), so it is right whichever level the person now inherits from.
        Guid? inherited = null;
        if (employee.DepartmentId is Guid departmentId)
            inherited = await db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && d.Id == departmentId)
                .Select(d => d.CostCenterId).FirstOrDefaultAsync(ct);
        employee.CostCenterId = inherited;
        employee.CostCenterIsOverride = false;
    }

    /// <summary>
    /// AM-9: after a change has been applied in memory (nothing saved), refuse it when the employee is in a run awaiting finance review or lock
    /// and the change moves where they resolve to. The tracked employee is reloaded first, so a caller that saves afterwards writes nothing.
    /// </summary>
    public static async Task EnsureNotLockedIntoRunAsync(ZayraDbContext db, Guid tenantId, Employee employee, AttributionState before, CancellationToken ct)
    {
        if (employee.Id == 0 || before == AttributionState.Of(employee)) return;
        try
        {
            await new CostAttributionGuard(db).EnsureEmployeeUnchangedAsync(tenantId, employee.CompanyId, employee.Id,
                new ResolverEmployee(before.DepartmentId, before.PositionId, before.CostCenterId, before.IsOverride),
                new ResolverEmployee(employee.DepartmentId, employee.PositionId, employee.CostCenterId, employee.CostCenterIsOverride), ct);
        }
        catch (CostCentreRuleException)
        {
            await db.Entry(employee).ReloadAsync(ct);
            throw;
        }
    }

    /// <summary>The attribution inputs of an employee, captured before a change is applied.</summary>
    public sealed record AttributionState(Guid? DepartmentId, Guid? PositionId, Guid? CostCenterId, bool IsOverride)
    {
        public static AttributionState Of(Employee e) => new(e.DepartmentId, e.PositionId, e.CostCenterId, e.CostCenterIsOverride);
    }

    /// <summary>
    /// Stages ONE audit row when a change moved where the employee's cost goes: the resolved cost centre, the override flag or the stored copy
    /// differs. It records the resolved cost centre before and after (contract AM-9 / S-029), whichever path caused the change. Does not save.
    /// <para><paramref name="initial"/> marks the first attribution of a newly created employee (a hire). It is recorded as
    /// <see cref="InitialAttributionAction"/>, which is deliberately NOT in <c>CostAccountingSod.RelevantActions</c> (CTO scope decision): hiring
    /// must not bar the admin from locking the month, whereas <see cref="AttributionChangedAction"/> (a change to an existing employee) does.
    /// <paramref name="overrideAlreadyAudited"/> is for the form doors, which stage the override row themselves with the reason.</para>
    /// </summary>
    public const string AttributionChangedAction = "employee.cost_attribution_changed";
    public const string InitialAttributionAction = "employee.cost_attribution_initialised";

    /// <param name="decision">Wave A D2: the keep/drop decision of a department move of an overridden person (explicit on the PUT, implicit
    /// "keep" on the HR transfer approval). With a decision the override row is written even when the override itself did not change, so the
    /// reason for keeping it is on the trail.</param>
    /// <param name="transferId">The transfer request an HR approval applied.</param>
    public static async Task StageAttributionAuditAsync(ZayraDbContext db, Guid tenantId, Employee employee, AttributionState before, Guid? actorUserId, CancellationToken ct, string? reason = null,
        bool initial = false, bool overrideAlreadyAudited = false, string? decision = null, Guid? transferId = null)
    {
        var after = AttributionState.Of(employee);
        if (before == after) return;
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        // The audit action finance's separation-of-duties check reads: an override was set, removed, re-pointed, or kept through a move.
        if (!overrideAlreadyAudited && (before.IsOverride != after.IsOverride || (after.IsOverride && before.CostCenterId != after.CostCenterId) || decision is not null))
        {
            var overrideRow = new Dictionary<string, object?>
            {
                ["before"] = new { @override = before.IsOverride, costCenterId = before.CostCenterId },
                ["after"] = new { @override = after.IsOverride, costCenterId = after.CostCenterId },
                ["reason"] = trimmedReason,
            };
            if (decision is not null) overrideRow["decision"] = decision;
            if (transferId is not null) overrideRow["transferId"] = transferId;
            new AuditService(db).Stage("employee.cost_centre_override.changed", nameof(Employee), employee.Id.ToString(),
                new RequestContext(null, null, actorUserId, tenantId), JsonSerializer.Serialize(overrideRow), employee.CompanyId);
        }

        Guid? resolvedBefore = null, resolvedAfter = null;
        string? sourceBefore = null, sourceAfter = null;
        if (employee.CompanyId is Guid companyId)
        {
            var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, companyId, ct);
            var b = CostCentreResolver.Resolve(new ResolverEmployee(before.DepartmentId, before.PositionId, before.CostCenterId, before.IsOverride), snapshot);
            var a = CostCentreResolver.Resolve(new ResolverEmployee(after.DepartmentId, after.PositionId, after.CostCenterId, after.IsOverride), snapshot);
            (resolvedBefore, sourceBefore) = (b.CostCenterId, b.Source ?? b.UnresolvedReason);
            (resolvedAfter, sourceAfter) = (a.CostCenterId, a.Source ?? a.UnresolvedReason);
            if (resolvedBefore == resolvedAfter && before.IsOverride == after.IsOverride && before.DepartmentId == after.DepartmentId
                && before.PositionId == after.PositionId) return; // only the cached copy moved: no cost moved
        }

        var beforeJson = new { departmentId = before.DepartmentId, positionId = before.PositionId, costCenterId = before.CostCenterId, @override = before.IsOverride, resolvedCostCenterId = resolvedBefore, source = sourceBefore };
        var afterJson = new { departmentId = after.DepartmentId, positionId = after.PositionId, costCenterId = after.CostCenterId, @override = after.IsOverride, resolvedCostCenterId = resolvedAfter, source = sourceAfter };
        // The reason (a hire's for an explicit cost centre, a move's for keeping or dropping an override, a transfer's own) and the decision
        // ride on the attribution row, so the row finance reads explains itself.
        var row = new Dictionary<string, object?> { ["before"] = beforeJson, ["after"] = afterJson };
        if (trimmedReason is not null) row["reason"] = trimmedReason;
        if (decision is not null) row["decision"] = decision;
        if (transferId is not null) row["transferId"] = transferId;
        var json = JsonSerializer.Serialize(row);
        new AuditService(db).Stage(initial ? InitialAttributionAction : AttributionChangedAction, nameof(Employee), employee.Id.ToString(),
            new RequestContext(null, null, actorUserId, tenantId), json, employee.CompanyId);
    }
}
