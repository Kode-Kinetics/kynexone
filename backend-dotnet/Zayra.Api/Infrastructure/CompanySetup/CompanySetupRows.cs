using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>
/// Addendum B: one company's Organisation rows (cost centres, departments, designations, grades) as draft rows. ONE
/// builder serves the existing-mode prefill, the rebase after a live section save (B2) and copy from a sister company
/// (B3), so a row reads the same whichever door produced it — and the prefill's baseline and the planner's comparison
/// can never project a row differently.
/// </summary>
public static class CompanySetupRows
{
    private const string Why = "Company setup wizard: a company's organisation rows and the codes of the rows they link to, read across the tenant's legal entities whatever company the switcher selects.";

    /// <summary>Id → normalized code of every row a department or designation can link to (tenant-wide, removed rows
    /// included), so a link projects to the code it points at even when that row is no longer active.</summary>
    public sealed record CodeMaps(
        IReadOnlyDictionary<Guid, string> Branches,
        IReadOnlyDictionary<Guid, string> CostCentres,
        IReadOnlyDictionary<Guid, string> Departments,
        IReadOnlyDictionary<Guid, string> Grades);

    public static async Task<CodeMaps> CodeMapsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        static Dictionary<Guid, string> Map(IEnumerable<(Guid Id, string Code)> rows) => rows.ToDictionary(x => x.Id, x => OrgCodes.Normalize(x.Code));
        var branches = await ScopedBypass.TenantWide(db.Branches, tenantId, Why).AsNoTracking().Select(x => new { x.Id, x.Code }).ToListAsync(ct);
        var costCentres = await ScopedBypass.TenantWide(db.CostCenters, tenantId, Why).AsNoTracking().Select(x => new { x.Id, x.Code }).ToListAsync(ct);
        var departments = await ScopedBypass.TenantWide(db.Departments, tenantId, Why).AsNoTracking().Select(x => new { x.Id, x.Code }).ToListAsync(ct);
        var grades = await ScopedBypass.TenantWide(db.Grades, tenantId, Why).AsNoTracking().Select(x => new { x.Id, x.Code }).ToListAsync(ct);
        return new CodeMaps(
            Map(branches.Select(x => (x.Id, x.Code))), Map(costCentres.Select(x => (x.Id, x.Code))),
            Map(departments.Select(x => (x.Id, x.Code))), Map(grades.Select(x => (x.Id, x.Code))));
    }

    /// <summary>The company's own rows of each Organisation table, removed ones included (a removed row still holds its
    /// code within the company).</summary>
    public sealed record CompanyRows(List<CostCenter> CostCentres, List<Department> Departments, List<Designation> Designations, List<Grade> Grades);

    public static async Task<CompanyRows> CompanyRowsAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct) => new(
        await ScopedBypass.TenantWide(db.CostCenters, tenantId, Why).AsNoTracking().Where(x => x.CompanyId == companyId).OrderBy(x => x.Code).ToListAsync(ct),
        await ScopedBypass.TenantWide(db.Departments, tenantId, Why).AsNoTracking().Where(x => x.CompanyId == companyId).OrderBy(x => x.Code).ToListAsync(ct),
        await ScopedBypass.TenantWide(db.Designations, tenantId, Why).AsNoTracking().Where(x => x.CompanyId == companyId).OrderBy(x => x.Code).ToListAsync(ct),
        await ScopedBypass.TenantWide(db.Grades, tenantId, Why).AsNoTracking().Where(x => x.CompanyId == companyId)
            .OrderBy(x => x.Level).ThenBy(x => x.Code).ToListAsync(ct));

    /// <summary>What still points at each of the company's organisation rows, by row id (rows nothing points at are absent).
    /// One read for the planner (code lock) and the draft (the "in use" badge), so they cannot disagree.</summary>
    public static async Task<IReadOnlyDictionary<Guid, OrgRowUsage>> UsageAsync(ZayraDbContext db, Guid tenantId, CompanyRows org, CancellationToken ct)
    {
        var removal = new Zayra.Api.Infrastructure.Organization.OrgRowRemoval(db, null);
        var all = new Dictionary<Guid, OrgRowUsage>();
        async Task Add(OrgRowKind kind, IEnumerable<Guid> ids)
        {
            foreach (var (id, usage) in await removal.UsageManyAsync(kind, tenantId, ids.ToList(), ct)) all[id] = usage;
        }
        await Add(OrgRowKind.CostCenter, org.CostCentres.Where(x => !x.IsDeleted).Select(x => x.Id));
        await Add(OrgRowKind.Department, org.Departments.Where(x => !x.IsDeleted).Select(x => x.Id));
        await Add(OrgRowKind.Designation, org.Designations.Where(x => !x.IsDeleted).Select(x => x.Id));
        await Add(OrgRowKind.Grade, org.Grades.Where(x => !x.IsDeleted).Select(x => x.Id));
        return all;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Link(Guid? id, IReadOnlyDictionary<Guid, string> codes) => id is Guid x && codes.TryGetValue(x, out var c) ? c : null;

    /// <summary>An existing row as the draft carries it: its values, its id and its A9 baseline.</summary>
    public static CompanySetupCostCentre CostCentre(CostCenter x) =>
        new(OrgCodes.Normalize(x.Code), x.Name, Blank(x.NameAr), x.Id, CompanySetupBaseline.CostCentre(x));

    public static CompanySetupDepartment Department(Department d, CodeMaps maps) =>
        new(OrgCodes.Normalize(d.Code), d.NameEn, Blank(d.NameAr), Link(d.ParentDepartmentId, maps.Departments), Link(d.BranchId, maps.Branches),
            Link(d.CostCenterId, maps.CostCentres), d.Id, CompanySetupBaseline.Department(d, maps.Departments, maps.Branches, maps.CostCentres));

    public static CompanySetupDesignation Designation(Designation d, CodeMaps maps) =>
        new(OrgCodes.Normalize(d.Code), d.TitleEn, Blank(d.TitleAr), Link(d.DepartmentId, maps.Departments), Link(d.GradeId, maps.Grades),
            d.Id, CompanySetupBaseline.Designation(d, maps.Departments, maps.Grades));

    public static CompanySetupGrade Grade(Grade g) =>
        new(OrgCodes.Normalize(g.Code), g.Name, Blank(g.NameAr), g.Level, g.MinSalary, g.MidSalary, g.MaxSalary, g.Id, CompanySetupBaseline.Grade(g));

    /// <summary>A row of another company as a NEW draft row (Addendum B3): no id, no baseline.</summary>
    public static CompanySetupCostCentre AsNew(CompanySetupCostCentre x) => x with { ExistingId = null, Baseline = null };
    public static CompanySetupDepartment AsNew(CompanySetupDepartment x) => x with { ExistingId = null, Baseline = null };
    public static CompanySetupDesignation AsNew(CompanySetupDesignation x) => x with { ExistingId = null, Baseline = null };
    public static CompanySetupGrade AsNew(CompanySetupGrade x) => x with { ExistingId = null, Baseline = null };

    /// <summary>The first of <c>CODE</c>, <c>CODE-2</c>, <c>CODE-3</c>… not in <paramref name="used"/>; claims it.</summary>
    public static string Unique(HashSet<string> used, string code)
    {
        var baseCode = OrgCodes.Normalize(code);
        var candidate = baseCode;
        for (var n = 2; used.Contains(candidate); n++) candidate = $"{baseCode}-{n}";
        used.Add(candidate);
        return candidate;
    }
}
