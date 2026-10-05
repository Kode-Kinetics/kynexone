using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// Slice L1 — loan limits by grade. The company loan policy says WHETHER and on what rules a type is
/// offered; this grid says HOW MUCH, per grade. Effective limits are the strictest of the two, enforced by
/// <see cref="LoanEligibilityService"/> through <see cref="GradeLoanLimitResolver"/>.
/// </summary>
public partial class LoansController
{
    private const string GradeLimitLockScope = "finance.grade-limits";

    /// <summary>The grid for one loan type: one row per active grade, by level. With <c>companyId</c>, each row
    /// is the cell in force for that company — its own override, else the tenant-wide cell
    /// (<c>isCompanyOverride</c> says which). A grade with no cell in force has <c>cellId: null</c>.</summary>
    [HttpGet("grade-limits")]
    [HasPermission("loans.read", "loans.write")]
    public async Task<IActionResult> GetGradeLimits([FromQuery] Guid loanTypeId, [FromQuery] Guid? companyId,
        [FromQuery] DateOnly? asOf, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (companyId.HasValue && !this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        var type = await _db.LoanTypes.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == loanTypeId && !x.IsDeleted, ct);
        if (type == null) return NotFound(new { error = "loan_type_not_found", message = "That loan type doesn't exist." });
        return Ok(await GradeLimitRowsAsync(tid, type, companyId, asOf ?? Today(), ct));
    }

    /// <summary>
    /// Publishes changed grades from <c>effectiveFrom</c>, in one transaction: for each row the cell in force in
    /// this scope is closed the day before and a new cell is opened. Grades not in <c>rows</c> are untouched.
    /// Values are never edited in place, so a loan's witness keeps pointing at the figures it was decided under.
    /// </summary>
    [HttpPut("grade-limits")]
    [Authorize(Roles = "Admin,HR Manager,HR Director")]
    public Task<IActionResult> PublishGradeLimits([FromBody] PublishGradeLimitsRequest req, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync<IActionResult>(_db, GradeLimitLockScope, GetTenantId(), req.LoanTypeId, async () =>
        {
            // Same owner as the company loan policy: HR decides how much a grade may borrow; Finance does not.
            if (!IsHrLoanActor()) return HrPolicyOwnerRequired();
            var tid = GetTenantId();
            var scope = this.GetEntityScope();
            if (req.CompanyId is null && !scope.IsGroupLevel)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "group_scope_required",
                    message = "Limits for all companies can only be changed by someone with access to every company. Choose your company instead." });
            if (req.CompanyId is Guid cid)
            {
                if (!scope.CanAccessCompany(cid)) return Forbid();
                if (!await _db.Companies.AnyAsync(x => x.TenantId == tid && x.Id == cid && !x.IsDeleted, ct))
                    return BadRequest(new { error = "company_not_found", message = "That company doesn't exist." });
            }
            var type = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == req.LoanTypeId && !x.IsDeleted, ct);
            if (type == null) return NotFound(new { error = "loan_type_not_found", message = "That loan type doesn't exist." });
            if (req.EffectiveFrom < Today())
                return BadRequest(new { error = "effective_from_in_past", message = "Limits can't be changed for past dates. Choose today or a later date." });
            if (req.Rows is not { Count: > 0 })
                return BadRequest(new { error = "no_rows", message = "Change at least one grade before publishing." });

            var grades = await _db.Grades.AsNoTracking().Where(x => x.TenantId == tid && !x.IsDeleted && x.IsActive).ToListAsync(ct);
            var errors = new List<object>();
            foreach (var group in req.Rows.GroupBy(r => r.GradeId).Where(g => g.Count() > 1))
                errors.Add(new { gradeId = group.Key, message = "This grade appears more than once." });
            foreach (var row in req.Rows)
            {
                var grade = grades.FirstOrDefault(g => g.Id == row.GradeId);
                if (grade == null) { errors.Add(new { gradeId = row.GradeId, message = "This grade doesn't exist or is no longer in use." }); continue; }
                if (ValidateGradeLimitRow(row) is { } problem) errors.Add(new { gradeId = row.GradeId, gradeName = grade.Name, message = problem });
            }
            if (errors.Count > 0)
                return BadRequest(new { error = "invalid_grade_limits", message = "Some limits need correcting before they can be published.", rows = errors });

            var (code, componentError) = await GradeLoanLimitResolver.EnsureFacilityComponentAsync(_db, tid, type, GetUserId(), ct);
            if (code == null) return Conflict(new { error = "component_code_taken", message = componentError });

            var gradeIds = req.Rows.Select(r => r.GradeId).ToArray();
            // Exact scope (this company, or tenant-wide), every version — so a scheduled later version is seen.
            var existing = await ScopedBypass.TenantWide(_db.GradeEntitlements, tid,
                    "Publishing the grid must see every version in the target scope; scope was authorised above.")
                .Where(x => x.PayComponentCode == code && x.CompanyId == req.CompanyId && gradeIds.Contains(x.GradeId))
                .ToListAsync(ct);
            var inserts = new List<(GradeEntitlement Cell, GradeEntitlement? Previous)>();
            foreach (var row in req.Rows)
            {
                var grade = grades.First(g => g.Id == row.GradeId);
                var versions = existing.Where(x => x.GradeId == row.GradeId).ToList();
                var later = versions.Where(x => x.EffectiveFrom > req.EffectiveFrom).OrderBy(x => x.EffectiveFrom).FirstOrDefault();
                if (later != null)
                    return Conflict(new { error = "later_version_exists",
                        message = $"{grade.Name} already has a limit starting {later.EffectiveFrom:yyyy-MM-dd}. Publish from that date or later." });
                var current = versions.FirstOrDefault(x => x.IsInEffect(req.EffectiveFrom));
                if (current != null && SameCellValues(current, row)) continue;
                if (current != null && current.EffectiveFrom == req.EffectiveFrom)
                    return Conflict(new { error = "same_day_version_exists",
                        message = $"{grade.Name} already has a limit starting {req.EffectiveFrom:yyyy-MM-dd}. Limits are never overwritten; publish the correction from the next day." });
                if (current != null) current.EffectiveTo = req.EffectiveFrom.AddDays(-1);
                inserts.Add((new GradeEntitlement
                {
                    TenantId = tid, CompanyId = req.CompanyId, GradeId = row.GradeId, PayComponentCode = code,
                    EntitlementClass = PayEntitlementClasses.Facility, Eligible = row.Eligible, ValueType = row.ValueType,
                    Amount = row.Amount, Rate = row.Rate, MaxOutstandingAmount = row.MaxOutstandingAmount,
                    Note = string.IsNullOrWhiteSpace(row.Note) ? null : row.Note.Trim(), SourceRule = "LoanGradeLimitsGrid",
                    EffectiveFrom = req.EffectiveFrom, CreatedBy = GetUserId(),
                }, current));
            }
            // Close first, then open: the no-overlap EXCLUDE is checked per statement, and EF does not promise
            // to order an UPDATE before an INSERT on the same table within one SaveChanges. Both saves run in
            // the serializer's transaction, so the publish is still all-or-nothing.
            await _db.SaveChangesAsync(ct);
            foreach (var (cell, previous) in inserts)
            {
                _db.GradeEntitlements.Add(cell);
                _db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tid, CompanyId = req.CompanyId, UserId = GetUserId(), Action = "loans.grade_limit.published",
                    EntityName = "GradeEntitlement", EntityId = cell.Id.ToString(),
                    IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(), CreatedAtUtc = DateTime.UtcNow,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        loanTypeId = type.Id, componentCode = code, cell.GradeId, cell.CompanyId, cell.EffectiveFrom,
                        previous = previous == null ? null : new { previous.Id, previous.Eligible, previous.ValueType, previous.Amount, previous.Rate, previous.MaxOutstandingAmount, previous.EffectiveFrom, previous.EffectiveTo },
                        next = new { cell.Eligible, cell.ValueType, cell.Amount, cell.Rate, cell.MaxOutstandingAmount, cell.Note },
                    }),
                });
            }
            await _db.SaveChangesAsync(ct);
            return Ok(new { changed = inserts.Count, unchanged = req.Rows.Count - inserts.Count,
                rows = await GradeLimitRowsAsync(tid, type, req.CompanyId, req.EffectiveFrom, ct) });
        }, ct);

    /// <summary>Turns grade limits on or off for a loan type. Enabling is refused while any active grade has no
    /// limit in force for some company — those employees could not apply at all.</summary>
    [HttpPatch("types/{id:guid}/grade-limited")]
    [Authorize(Roles = "Admin,HR Manager,HR Director")]
    public Task<IActionResult> SetLoanTypeGradeLimited(Guid id, [FromBody] SetGradeLimitedRequest req, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync<IActionResult>(_db, GradeLimitLockScope, GetTenantId(), id, async () =>
        {
            if (!IsHrLoanActor()) return HrPolicyOwnerRequired();
            var tid = GetTenantId();
            if (!this.GetEntityScope().IsGroupLevel)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "group_scope_required",
                    message = "Limiting a loan type by grade affects every company, so only someone with access to every company can change it." });
            var type = await _db.LoanTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id && !x.IsDeleted, ct);
            if (type == null) return NotFound(new { error = "loan_type_not_found", message = "That loan type doesn't exist." });
            if (type.GradeLimited == req.GradeLimited) return Ok(LoanTypeGradeDto(type));
            if (req.GradeLimited)
            {
                if (!type.IsInterestFree || type.InterestRate != 0)
                    return BadRequest(new { error = LoanEligibilityCodes.InterestNotPermitted, message = LoanEligibilityCodes.InterestNotPermittedText });
                var (code, componentError) = await GradeLoanLimitResolver.EnsureFacilityComponentAsync(_db, tid, type, GetUserId(), ct);
                if (code == null) return Conflict(new { error = "component_code_taken", message = componentError });
                var missing = await MissingGradeLimitsAsync(tid, code, Today(), ct);
                if (missing.Count > 0)
                    return Conflict(new
                    {
                        error = "grade_limits_missing",
                        message = "Set a limit for every grade before limiting this loan type by grade. Missing: "
                            + string.Join(", ", missing.Select(m => m.GradeName)) + ".",
                        missingGrades = missing.Select(m => new { m.GradeName, m.GradeNameAr, m.GradeCode, m.GradeId }),
                    });
            }
            var was = type.GradeLimited;
            type.GradeLimited = req.GradeLimited;
            _db.AuditLogs.Add(new AuditLog
            {
                TenantId = tid, UserId = GetUserId(), Action = req.GradeLimited ? "loans.type.grade_limited_on" : "loans.type.grade_limited_off",
                EntityName = "LoanType", EntityId = type.Id.ToString(), IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                Metadata = JsonSerializer.Serialize(new { type.Code, was, now = type.GradeLimited, type.EntitlementComponentCode }),
            });
            await _db.SaveChangesAsync(ct);
            return Ok(LoanTypeGradeDto(type));
        }, ct);

    /// <summary>The loan types one employee may apply for, with the plain reason when one is not offered —
    /// so the UI shows no Apply button rather than a refusal after the form is filled in.</summary>
    [HttpGet("types/offered")]
    [HasPermission("loans.self", "loans.read", "loans.write")]
    public async Task<IActionResult> ListOfferedLoanTypes([FromQuery] int? employeeIntId, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        employeeIntId ??= await _db.Employees.Where(x => x.TenantId == tid && x.UserAccountId == uid && !x.IsDeleted).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        if (!employeeIntId.HasValue) return BadRequest(new { error = "no_linked_employee", message = "A linked employee is required." });
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        if (!scope.CanAccessEmployee(employeeIntId.Value)) return Forbid();
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == employeeIntId.Value && !x.IsDeleted, ct);
        if (employee == null) return NotFound();
        if (!IsHrLoanActor() && !IsFinanceActor() && employee.UserAccountId != uid) return Forbid();
        var types = await _db.LoanTypes.AsNoTracking().Where(x => x.TenantId == tid && !x.IsDeleted && x.IsActive).OrderBy(x => x.NameEn).ToListAsync(ct);
        // Same selection as LoanEligibilityService: the company's own active policy wins over a group-wide one.
        var policies = (await _db.Set<LoanPolicy>().AsNoTracking()
                .Where(x => x.TenantId == tid && x.IsActive && (x.CompanyId == employee.CompanyId || x.CompanyId == null))
                .Select(x => new { x.LoanTypeId, x.CompanyId, x.IsOffered, x.Version, x.CreatedAtUtc }).ToListAsync(ct))
            .GroupBy(x => x.LoanTypeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CompanyId.HasValue).ThenByDescending(x => x.Version)
                .ThenByDescending(x => x.CreatedAtUtc).First());
        return Ok(types.Select(t =>
        {
            var policy = policies.GetValueOrDefault(t.Id);
            var (code, text) = !t.IsInterestFree || t.InterestRate != 0
                ? (LoanEligibilityCodes.InterestNotPermitted, LoanEligibilityCodes.InterestNotPermittedText)
                : (policy is { CompanyId: not null, IsOffered: false }) || (policy == null && t.GradeLimited)
                    ? (LoanEligibilityCodes.TypeNotOffered, LoanEligibilityCodes.TypeNotOfferedText)
                    : ((string?)null, (string?)null);
            return new { loanTypeId = t.Id, t.Code, t.NameEn, t.NameAr, t.GradeLimited, offered = code == null, reasonCode = code, reasonText = text };
        }));
    }

    /// <summary>
    /// The company's explicit "we offer / don't offer this loan type" switch. Stored on the company's own loan
    /// policy (<see cref="LoanPolicy.IsOffered"/>) as a new policy version — terms are copied from the policy in
    /// force for the company (its own, else the group-wide one), so switching never changes any other rule and the
    /// history stays versioned like every other policy change. A no-op when the company is already in the asked
    /// state. Switching ON a grade-limited type that has no policy at all is refused: there are no terms to offer
    /// it on, so HR publishes a real policy instead.
    /// </summary>
    [HttpPut("offerings")]
    [Authorize(Roles = "Admin,HR Manager,HR Director")]
    public Task<IActionResult> SetLoanTypeOffering([FromBody] SetLoanOfferingRequest req, CancellationToken ct) =>
        FinanceDecisionSerializer.SerializeAsync<IActionResult>(_db, "finance.loan-policies", GetTenantId(), req.CompanyId, async () =>
        {
            if (!IsHrLoanActor()) return HrPolicyOwnerRequired();
            if (!this.GetEntityScope().CanAccessCompany(req.CompanyId)) return Forbid();
            var tid = GetTenantId();
            if (req.CompanyId == Guid.Empty || !await _db.Companies.AnyAsync(x => x.TenantId == tid && x.Id == req.CompanyId && !x.IsDeleted, ct))
                return BadRequest(new { error = "company_not_found", message = "Choose a valid company." });
            var type = await _db.LoanTypes.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == req.LoanTypeId && !x.IsDeleted && x.IsActive, ct);
            if (type == null) return NotFound(new { error = "loan_type_not_found", message = "That loan type doesn't exist." });
            var current = await _db.Set<LoanPolicy>()
                .Where(x => x.TenantId == tid && x.LoanTypeId == type.Id && x.IsActive && (x.CompanyId == req.CompanyId || x.CompanyId == null))
                .OrderByDescending(x => x.CompanyId.HasValue).ThenByDescending(x => x.Version).ThenByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            var effectiveOffered = current is not { CompanyId: not null, IsOffered: false } && (current != null || !type.GradeLimited);
            if (effectiveOffered == req.Offered) return Ok(OfferingDto(type, req.CompanyId, current));
            // A grade-limited type with no policy at all has no terms to offer it on; that needs a real policy.
            if (current == null && req.Offered)
                return Conflict(new { error = "policy_required",
                    message = "Publish a loan policy for this loan type and company first. The loan type is offered from then on." });
            var version = await _db.Set<LoanPolicy>().Where(x => x.TenantId == tid && x.CompanyId == req.CompanyId && x.LoanTypeId == type.Id)
                .Select(x => (int?)x.Version).MaxAsync(ct) ?? 0;
            foreach (var previous in await _db.Set<LoanPolicy>().Where(x => x.TenantId == tid && x.CompanyId == req.CompanyId && x.LoanTypeId == type.Id && x.IsActive).ToListAsync(ct))
                previous.IsActive = false;
            await _db.SaveChangesAsync(ct);
            // Switching OFF with no policy at all: the terms are never used (nothing can be applied for), so the
            // loan-type limits are recorded only to keep the version complete.
            var basis = current ?? new LoanPolicy { PolicyName = type.NameEn, MaxAmount = type.MaxAmount,
                MaxInstallments = Math.Clamp(type.MaxInstallments, 1, 600), MinServiceMonths = type.MinServiceMonths, MaxConcurrentLoans = 1 };
            var next = JsonSerializer.Deserialize<LoanPolicy>(JsonSerializer.Serialize(basis))!;
            next.Id = Guid.NewGuid(); next.TenantId = tid; next.CompanyId = req.CompanyId; next.LoanTypeId = type.Id;
            next.Version = version + 1; next.IsActive = true; next.IsOffered = req.Offered;
            next.CreatedAtUtc = DateTime.UtcNow; next.CreatedBy = GetUserId();
            if (string.IsNullOrWhiteSpace(next.PolicyName)) next.PolicyName = type.NameEn;
            _db.Set<LoanPolicy>().Add(next);
            _db.AuditLogs.Add(new AuditLog
            {
                TenantId = tid, CompanyId = req.CompanyId, UserId = GetUserId(), Action = req.Offered ? "loans.type.offered" : "loans.type.not_offered",
                EntityName = "LoanPolicy", EntityId = next.Id.ToString(), IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                Metadata = JsonSerializer.Serialize(new { loanTypeId = type.Id, type.Code, req.Offered, copiedFromPolicyId = current?.Id, next.Version }),
            });
            await _db.SaveChangesAsync(ct);
            return Ok(OfferingDto(type, req.CompanyId, next));
        }, ct);

    /// <summary>Per company: which loan types its employees are offered, and why not.</summary>
    [HttpGet("offerings")]
    [HasPermission("loans.read", "loans.write")]
    public async Task<IActionResult> ListLoanTypeOfferings([FromQuery] Guid companyId, CancellationToken ct)
    {
        if (!this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        var tid = GetTenantId();
        var types = await _db.LoanTypes.AsNoTracking().Where(x => x.TenantId == tid && !x.IsDeleted && x.IsActive).OrderBy(x => x.NameEn).ToListAsync(ct);
        var policies = await _db.Set<LoanPolicy>().AsNoTracking()
            .Where(x => x.TenantId == tid && x.IsActive && (x.CompanyId == companyId || x.CompanyId == null)).ToListAsync(ct);
        return Ok(types.Select(t => OfferingDto(t, companyId, policies.Where(p => p.LoanTypeId == t.Id)
            .OrderByDescending(x => x.CompanyId.HasValue).ThenByDescending(x => x.Version).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault())));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private ObjectResult HrPolicyOwnerRequired() => StatusCode(StatusCodes.Status403Forbidden, new
    {
        error = "hr_policy_owner_required",
        message = "Loan limits and offerings are set by HR (Admin, HR Manager or HR Director), the same people who own company loan policies.",
    });

    private static object OfferingDto(LoanType t, Guid companyId, LoanPolicy? policy)
    {
        var explicitlyOff = policy is { CompanyId: not null, IsOffered: false };
        var offered = !explicitlyOff && (policy != null || !t.GradeLimited) && t.IsInterestFree && t.InterestRate == 0;
        return new
        {
            loanTypeId = t.Id, t.Code, t.NameEn, t.NameAr, t.GradeLimited, companyId, offered,
            source = explicitlyOff ? "CompanyNotOffered" : policy == null ? (t.GradeLimited ? "NoPolicy" : "LoanTypeBaseline")
                : policy.CompanyId.HasValue ? "CompanyPolicy" : "GroupPolicy",
            policyId = policy?.Id, policyVersion = policy?.Version,
        };
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static object LoanTypeGradeDto(LoanType t) => new { t.Id, t.Code, t.NameEn, t.NameAr, t.GradeLimited, t.EntitlementComponentCode };

    private async Task<List<GradeLimitRowDto>> GradeLimitRowsAsync(Guid tid, LoanType type, Guid? companyId, DateOnly asOf, CancellationToken ct)
    {
        var grades = await _db.Grades.AsNoTracking().Where(x => x.TenantId == tid && !x.IsDeleted && x.IsActive)
            .OrderBy(x => x.Level).ThenBy(x => x.Code).ToListAsync(ct);
        var cells = type.EntitlementComponentCode is not { } code ? new List<GradeEntitlement>()
            : await ScopedBypass.TenantWide(_db.GradeEntitlements, tid,
                    "The grid shows the tenant-wide cell and the requested company's override; company access was checked by the caller.")
                .AsNoTracking()
                .Where(x => x.PayComponentCode == code && (x.CompanyId == null || x.CompanyId == companyId)
                    && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
                .ToListAsync(ct);
        return grades.Select(g =>
        {
            var cell = cells.Where(c => c.GradeId == g.Id)
                .OrderByDescending(c => c.CompanyId.HasValue).ThenByDescending(c => c.EffectiveFrom).FirstOrDefault();
            return new GradeLimitRowDto(g.Id, g.Code, g.Name, g.NameAr, g.Level, cell?.Id, cell?.Eligible ?? false, cell?.ValueType,
                cell?.Amount, cell?.Rate, cell?.MaxOutstandingAmount, cell?.EffectiveFrom, cell?.EffectiveTo,
                cell?.CompanyId != null, cell?.Note);
        }).ToList();
    }

    /// <summary>Active grades with no cell in force on <paramref name="on"/> for at least one active company
    /// (neither that company's override nor the tenant-wide cell).</summary>
    private async Task<List<(Guid GradeId, string GradeCode, string GradeName, string? GradeNameAr)>> MissingGradeLimitsAsync(Guid tid, string code, DateOnly on, CancellationToken ct)
    {
        var grades = await _db.Grades.AsNoTracking().Where(x => x.TenantId == tid && !x.IsDeleted && x.IsActive)
            .OrderBy(x => x.Level).ThenBy(x => x.Code).ToListAsync(ct);
        var companies = await ScopedBypass.TenantWide(_db.Companies, tid, "Coverage of a tenant-wide setting is checked against every active company.")
            .Where(x => !x.IsDeleted && x.IsActive).Select(x => x.Id).ToListAsync(ct);
        var cells = await ScopedBypass.TenantWide(_db.GradeEntitlements, tid, "Coverage of a tenant-wide setting is checked across every company.")
            .AsNoTracking()
            .Where(x => x.PayComponentCode == code && x.EffectiveFrom <= on && (x.EffectiveTo == null || x.EffectiveTo >= on))
            .Select(x => new { x.GradeId, x.CompanyId }).ToListAsync(ct);
        return grades.Where(g =>
                !cells.Any(c => c.GradeId == g.Id && c.CompanyId == null)
                && (companies.Count == 0 || companies.Any(company => !cells.Any(c => c.GradeId == g.Id && c.CompanyId == company))))
            .Select(g => (g.Id, g.Code, g.Name, g.NameAr)).ToList();
    }

    private static bool SameCellValues(GradeEntitlement c, GradeLimitRowInput r) =>
        c.Eligible == r.Eligible && c.ValueType == r.ValueType && c.Amount == r.Amount && c.Rate == r.Rate
        && c.MaxOutstandingAmount == r.MaxOutstandingAmount
        && (c.Note ?? string.Empty) == (string.IsNullOrWhiteSpace(r.Note) ? string.Empty : r.Note.Trim());

    /// <summary>Plain-language validation of one grid row; null when it is publishable. Mirrors the database
    /// CHECKs so a bad row is explained rather than rejected by a constraint.</summary>
    internal static string? ValidateGradeLimitRow(GradeLimitRowInput row)
    {
        static bool Money(decimal v) => v > 0 && decimal.Round(v, 2) == v && v <= 999_999_999_999.99m;
        if (row.Note is { Length: > 500 }) return "Keep the note under 500 characters.";
        if (!GradeEntitlementValueTypes.All.Contains(row.ValueType))
            return "Choose a limit basis: fixed amount, × basic salary or × gross salary.";
        if (!row.Eligible)
            return row.ValueType == GradeEntitlementValueTypes.EligibilityOnly && row.Amount is null && row.Rate is null && row.MaxOutstandingAmount is null
                ? null : "A grade that isn't eligible can't have limit figures.";
        if (row.MaxOutstandingAmount is decimal outstanding && !Money(outstanding))
            return "The total outstanding maximum must be a positive amount with at most two decimals.";
        return row.ValueType switch
        {
            GradeEntitlementValueTypes.Amount when row.Amount is decimal a && Money(a) && row.Rate is null => null,
            GradeEntitlementValueTypes.Amount => "Enter the per-loan maximum as a positive amount with at most two decimals.",
            GradeEntitlementValueTypes.MultipleOfBasic or GradeEntitlementValueTypes.MultipleOfGross
                when row.Rate is decimal m && m > 0 && m <= 120 && decimal.Round(m, 4) == m && row.Amount is null => null,
            GradeEntitlementValueTypes.MultipleOfBasic or GradeEntitlementValueTypes.MultipleOfGross =>
                "Enter how many months of salary (more than 0, at most 120).",
            _ when row.Amount is null && row.Rate is null => null,
            _ => "With no per-loan maximum, leave the amount and multiple empty.",
        };
    }
}

public sealed record GradeLimitRowInput(Guid GradeId, bool Eligible, string ValueType, decimal? Amount = null,
    decimal? Rate = null, decimal? MaxOutstandingAmount = null, string? Note = null);
public sealed record PublishGradeLimitsRequest(Guid LoanTypeId, Guid? CompanyId, DateOnly EffectiveFrom, List<GradeLimitRowInput> Rows);
public sealed record SetGradeLimitedRequest(bool GradeLimited);
public sealed record SetLoanOfferingRequest(Guid CompanyId, Guid LoanTypeId, bool Offered);
public sealed record GradeLimitRowDto(Guid GradeId, string GradeCode, string GradeName, string? GradeNameAr, int Level, Guid? CellId, bool Eligible,
    string? ValueType, decimal? Amount, decimal? Rate, decimal? MaxOutstandingAmount, DateOnly? EffectiveFrom, DateOnly? EffectiveTo,
    bool IsCompanyOverride, string? Note);
