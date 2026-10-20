using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>
/// Small-group suppression for pay statistics. LOCAL GUARD: main has no shared cohort-suppression service yet
/// (ENG builds Infrastructure/Privacy); unify with it when it lands. Rule: a group is shown only when it has at least
/// <see cref="MinimumCohort"/> people; when any group of a cohort is suppressed the cohort's own total and mean are
/// withheld too, so a suppressed group cannot be recovered by difference.
/// </summary>
public static class CompCohortGuard
{
    public const int MinimumCohort = 5;
    public static bool Shown(int count) => count >= MinimumCohort;
}

/// <summary>
/// Pay equity within cohorts (company × grade × designation): per gender — and per national/expatriate when the
/// cycle enables it — count, mean and median compa-ratio, mean monthly gross in cycle currency, and the unadjusted gap
/// = (mean compa men − mean compa women) ÷ mean compa men × 100, 2 dp. "Projected" applies the primary worksheets'
/// proposed new gross. Reading it needs compensation.equity.read and is audited.
/// </summary>
public sealed class PayEquityService(ZayraDbContext db, IAuditService audit)
{
    public const string Notice =
        "Authorised use only. These figures describe pay positions within like-for-like cohorts for pay-equity review. " +
        "They are not adjusted for tenure, performance or location and must not be used to make decisions about an individual.";

    public async Task<CompEquityDto> GetAsync(CompCaller caller, Guid cycleId, CancellationToken ct)
    {
        if (!caller.Can(CompPermissions.EquityRead)) throw new CompForbiddenException("forbidden", "Pay-equity analysis needs the pay-equity permission.");
        var cycle = await db.CompCycles.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == caller.TenantId && c.Id == cycleId, ct)
            ?? throw new CompNotFoundException("Merit cycle not found.");
        var ctx = await new MeritBasisLoader(db).LoadAsync(cycle, ct);
        var primary = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == cycle.Id && w.IsPrimary && w.Status != CompWorksheetStatuses.Cancelled).Select(w => w.Id).ToListAsync(ct);
        var proposed = (await db.CompWorksheetLines.AsNoTracking().Where(l => primary.Contains(l.WorksheetId) && l.ProratedIncreasePay > 0)
                .Select(l => new { l.EmployeeId, l.NewCompaRatio }).ToListAsync(ct))
            .GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => g.First().NewCompaRatio);

        var population = ctx.Employees.Values.Where(b => !MeritBasisLoader.SeparatedStatuses.Contains(b.Employee.Status)
                                                         && string.Equals(b.Employee.Status, "Active", StringComparison.OrdinalIgnoreCase)).ToList();
        var noSalary = population.Count(b => b.SalaryRow is null);
        var noGrade = population.Count(b => b.SalaryRow is not null && b.CompaRatio is null);
        var people = population.Where(b => b.SalaryRow is not null && b.CompaRatio is not null).ToList();
        var designations = await db.Designations.AsNoTracking().Where(d => d.TenantId == caller.TenantId).Select(d => new { d.Id, d.TitleEn }).ToDictionaryAsync(d => d.Id, d => d.TitleEn, ct);
        var countryIso2 = CountryCodeStandard.NormalizeToIso2(ctx.Company.CountryCode);

        List<CompEquityCohortDto> Build(Func<EmployeeBasis, string> group, string dimension, string a, string b) =>
            people.GroupBy(p => (p.Employee.GradeId, p.Employee.DesignationId))
                .Select(cohort => Cohort(cohort.ToList(), group, dimension, a, b, proposed,
                    cohort.First().Grade?.Name ?? "", cohort.Key.DesignationId is Guid d ? designations.GetValueOrDefault(d) ?? "" : "No designation"))
                .OrderBy(c => c.GradeName).ThenBy(c => c.DesignationName).ToList();

        // Employees store the gender code (MALE) or, on older rows, its label (Male): read both through the tenant's list.
        var genders = await ReferenceList.LoadAsync(db, caller.TenantId, ReferenceList.GenderType, ct);
        var gender = Build(p => Gender(p.Employee.Gender, genders), "gender", "Male", "Female");
        var nationality = cycle.EquityIncludesNationality
            ? Build(p => IsNational(p.Employee.Nationality, countryIso2) ? "National" : "Expatriate", "nationality", "National", "Expatriate")
            : [];
        await audit.WriteAsync("compensation.equity.viewed", nameof(CompCycle), cycle.Id.ToString(), caller.Context,
            JsonSerializer.Serialize(new { cohorts = gender.Count, nationality = cycle.EquityIncludesNationality }), ct);
        return new CompEquityDto(cycle.Id, cycle.Currency, CompCohortGuard.MinimumCohort, gender, nationality, noGrade, noSalary, Notice);
    }

    private static CompEquityCohortDto Cohort(List<EmployeeBasis> members, Func<EmployeeBasis, string> group, string dimension, string a, string b,
        IReadOnlyDictionary<int, decimal?> proposed, string gradeName, string designationName)
    {
        if (!CompCohortGuard.Shown(members.Count))
            return new CompEquityCohortDto(gradeName, designationName, true, $"fewer than {CompCohortGuard.MinimumCohort}", [], null, null, dimension);
        var groups = members.GroupBy(group).OrderBy(g => g.Key).Select(g =>
        {
            var list = g.ToList();
            if (!CompCohortGuard.Shown(list.Count)) return new CompEquityGroupDto(g.Key, true, null, null, null, null, null);
            var compa = list.Select(p => p.CompaRatio!.Value).ToList();
            var projected = list.Select(p => proposed.TryGetValue(p.Employee.Id, out var n) && n is decimal v ? v : p.CompaRatio!.Value).ToList();
            var gross = list.Where(p => p.GrossCycle != null).Select(p => p.GrossCycle!.Value).ToList();
            return new CompEquityGroupDto(g.Key, false, list.Count, MeritMath.Ratio(compa.Average()), MeritMath.Ratio(MeritMath.Median(compa)),
                gross.Count == list.Count ? MeritMath.Money(gross.Average()) : null, MeritMath.Ratio(projected.Average()));
        }).ToList();
        var anySuppressed = groups.Any(g => g.Suppressed);
        var ga = groups.FirstOrDefault(g => g.Group == a && !g.Suppressed);
        var gb = groups.FirstOrDefault(g => g.Group == b && !g.Suppressed);
        decimal? Gap(decimal? x, decimal? y) => x is decimal xa && y is decimal yb && xa > 0 ? Math.Round((xa - yb) / xa * 100m, 2, MidpointRounding.AwayFromZero) : null;
        return new CompEquityCohortDto(gradeName, designationName, false, anySuppressed ? $"{CompCohortGuard.MinimumCohort} or more" : members.Count.ToString(),
            groups, Gap(ga?.MeanCompaRatio, gb?.MeanCompaRatio), Gap(ga?.ProjectedMeanCompaRatio, gb?.ProjectedMeanCompaRatio), dimension);
    }

    internal static string Gender(string? value) => Gender(value, ReferenceList.Default(ReferenceList.GenderType));

    /// <summary>The cohort a stored gender falls in. The group names stay "Male"/"Female"/"Not stated" (the report's
    /// contract); which row lands where is decided by the code, so "Male", "MALE", "m" and "ذكر" are one group.</summary>
    internal static string Gender(string? value, ReferenceList genders)
    {
        if (genders.ToCode(value) is not { } code) return "Not stated";
        if (genders.Same(code, ReferenceList.Male)) return "Male";
        if (genders.Same(code, ReferenceList.Female)) return "Female";
        return "Not stated";
    }

    private static bool IsNational(string? nationality, string? companyIso2) =>
        companyIso2 == "SA" ? Compliance.SaudiNationality.IsSaudi(nationality)
        : companyIso2 is not null && CountryCodeStandard.NormalizeToIso2(nationality) == companyIso2;
}
