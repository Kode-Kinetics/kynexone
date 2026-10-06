using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Ess;

/// <summary>
/// "My package / باقتي" (Release A slice R2): the caller's OWN package. There is no employee id anywhere in the route or
/// query — the employee is the one linked to the signed-in user — so there is no way to ask for someone else's. The DTO
/// is a whitelist: labels, values, "fixed until", the caller's own dependants covered and the facilities available; no
/// cell, row, grade or employee ids. Closed unless the tenant has release_a on (FeatureFlagGuardFilter, /api/ess/package).
/// </summary>
[ApiController]
[Route("api/ess/package")]
[Authorize]
public sealed class EssPackageController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IEntitlementResolver _resolver;
    private readonly ITenantClock _clock;
    private readonly IRenewalDeadlineCalculator _deadlines;

    public EssPackageController(ZayraDbContext db, IEntitlementResolver resolver, ITenantClock clock, IRenewalDeadlineCalculator deadlines)
    {
        _db = db;
        _resolver = resolver;
        _clock = clock;
        _deadlines = deadlines;
    }

    /// <summary>GET /api/ess/package — the caller's package today.</summary>
    [HttpGet]
    [HasPermission("ess.read")]
    public async Task<ActionResult<EssPackageDto>> Mine(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return BadRequest(new { error = "access_mode", message = "This access mode cannot use self-service." });
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employeeId = await SelfAsync(tid, ct);
        if (employeeId is null)
            return Conflict(new { error = "no_employee_record", message = "Your login is not linked to an employee record, so there is no package to show. Ask HR to link it." });

        var today = await _clock.TodayAsync(tid, ct);
        var resolved = await _resolver.ResolveDetailedAsync(tid, employeeId.Value, today, ct);
        var package = resolved.Package;
        var context = await PackageViewContext.LoadAsync(_db, tid, package, ct);
        DateOnly? reviewOpensOn = null;
        if (context.Contract is { EndDate: not null } contract)
        {
            // The renewal calendar belongs to R4; until it ships (or if it cannot answer) the line is simply omitted.
            try { reviewOpensOn = (await _deadlines.ComputeAsync(tid, contract, ct)).OpensOn; }
            catch (Exception ex) when (ex is NotImplementedException or InvalidOperationException) { }
        }
        return Ok(EssPackageDto.From(resolved, context, reviewOpensOn));
    }

    /// <summary>The employee linked to the signed-in user: the signed employee_id claim, else the user-account link.</summary>
    private async Task<int?> SelfAsync(Guid tid, CancellationToken ct)
    {
        if (int.TryParse(User.FindFirstValue("employee_id"), out var claimed)
            && await _db.Employees.AsNoTracking().AnyAsync(x => x.TenantId == tid && x.Id == claimed && !x.IsDeleted, ct))
            return claimed;
        if (this.GetUserId() is not Guid userId) return null;
        return await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tid && x.UserAccountId == userId && !x.IsDeleted)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
    }
}

/// <summary>The employee's own package. Group: pay (in the Qiwa contract), contract (fixed for the term), facility (policy).</summary>
public sealed record EssPackageDto(
    DateOnly AsOf,
    string Currency,
    string? GradeName,
    string? GradeNameAr,
    string? CompanyName,
    string? CompanyNameAr,
    DateOnly? TermStartsOn,
    DateOnly? FixedUntil,
    DateOnly? RenewalReviewOpensOn,
    int DependantsOnFile,
    IReadOnlyList<EssPackageLineDto> Lines)
{
    public static EssPackageDto From(ResolvedPackage resolved, PackageViewContext c, DateOnly? reviewOpensOn) => From(resolved.Package, resolved, c, reviewOpensOn);

    private static EssPackageDto From(EmployeePackage package, ResolvedPackage resolved, PackageViewContext c, DateOnly? reviewOpensOn) =>
        new(package.AsOf, c.Currency, c.Grade?.Name, c.Grade?.NameAr, c.Company?.LegalNameEn, c.Company?.LegalNameAr,
            c.Contract?.StartDate, package.TermEndsOn, reviewOpensOn,
            c.Dependants.Count(d => PackageRules.IsSpouse(d.Relationship) || PackageRules.IsChild(d.Relationship)),
            package.Lines.Select(line => EssPackageLineDto.From(line, c,
                resolved.Reasons.TryGetValue(line.ComponentCode, out var r) ? r.Criterion : null)).ToList());
}

/// <param name="Why">What the value is based on, in words the employee can check — never an id.</param>
public sealed record EssPackageLineDto(
    string ComponentCode,
    string LabelEn,
    string LabelAr,
    string Group,
    bool Eligible,
    bool Offered,
    bool Fixed,
    string? ValueType,
    decimal? Amount,
    decimal? Rate,
    decimal? MonthlyCash,
    string? CoverageTier,
    short? Quantity,
    string DependantScope,
    short? MaxDependants,
    int DependantsCovered,
    string? LimitPeriod,
    string? ReasonCode,
    string? ReasonCriterion,
    DateOnly? EligibleFrom,
    decimal? ResolvedAmount,
    EssPackageWhyDto Why)
{
    public static EssPackageLineDto From(PackageLine line, PackageViewContext c, string? criterion)
    {
        var group = line.Class switch
        {
            PayEntitlementClasses.QiwaWage => "pay",
            PayEntitlementClasses.Facility => "facility",
            _ => "contract",
        };
        c.Cells.TryGetValue(line.GradeEntitlementId ?? Guid.Empty, out var cell);
        c.FrozenRows.TryGetValue(line.EmployeeEntitlementId ?? Guid.Empty, out var row);
        var why = line.Source switch
        {
            PackageLineSources.Salary => new EssPackageWhyDto("salary", c.Grade?.Name, c.Grade?.NameAr, line.IsCompanyOverride,
                c.Salary?.EffectiveDate, null),
            PackageLineSources.ContractFrozen => new EssPackageWhyDto("contract", c.Grade?.Name, c.Grade?.NameAr, line.IsCompanyOverride,
                row?.EffectiveFrom, row?.EffectiveTo),
            _ => new EssPackageWhyDto(line.Source == PackageLineSources.Facility ? "policy" : "grade", c.Grade?.Name, c.Grade?.NameAr,
                line.IsCompanyOverride, cell?.EffectiveFrom, null),
        };
        var label = c.Labels.TryGetValue(line.ComponentCode, out var l) ? l : new ComponentLabel(line.ComponentCode, line.ComponentCode);
        // "Fixed for this contract year" only for a row that is frozen AND verified (the resolver returns ContractFrozen for
        // nothing else); a proposal or an unverified row reads as the grade standard, not yet fixed.
        var isFixed = line.Source == PackageLineSources.ContractFrozen && row is { VerificationState: EntitlementVerificationStates.Verified };
        return new EssPackageLineDto(line.ComponentCode, label.En, label.Ar, group, line.Eligible, line.Offered, isFixed,
            line.ValueType, line.Amount, line.Rate, line.MonthlyCash, line.CoverageTier, line.Quantity, line.DependantScope, line.MaxDependants,
            line.DependantsCovered, line.LimitPeriod, line.ReasonCode, criterion, line.EligibleFrom, line.ResolvedAmount,
            isFixed || line.Source != PackageLineSources.ContractFrozen ? why : why with { Basis = "grade" });
    }
}

/// <param name="Basis">salary (paid with your salary, as in your Qiwa contract) · contract (fixed for this contract year) ·
/// grade (your grade's standard, not yet fixed in your contract) · policy (current company policy for your grade).</param>
/// <param name="CompanyRule">True when your company sets its own value instead of the group's.</param>
public sealed record EssPackageWhyDto(string Basis, string? GradeName, string? GradeNameAr, bool CompanyRule, DateOnly? Since, DateOnly? Until);
