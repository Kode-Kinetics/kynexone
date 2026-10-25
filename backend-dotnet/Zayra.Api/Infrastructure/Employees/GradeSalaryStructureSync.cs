using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// A grade's salary structure follows its grade. One fact, one place: the grade (Setup → Grades) is where a pay package
/// is defined — its pay scale (min/max) and its package lines (<see cref="GradePayScaleComponent"/>). The grade's
/// salary structure (<c>GRADE-{code}</c>, per company) is DERIVED from it here and nowhere else is it edited: its name,
/// currency, gross range and component lines (code, name, type, calculation, amount, percentage, taxable) are a copy of
/// the grade's, written when the grade or its package is saved.
///
/// <para><b>Versioning</b> is the rule the salary-structure editor used to apply. A structure no employee is on is
/// updated in place. A structure employees are on is never rewritten under them: it is retired (inactive) and a new
/// version is opened (<c>GRADE-{code}-V{n}</c>, <see cref="SalaryStructure.PreviousVersionId"/> pointing back), which
/// <see cref="EmployeeSalaryBreakdownWriter.ResolveStructureAsync"/> already follows for the default code. Employees
/// already on the old version keep their assignment rows (payroll pays the assignment's own figures); the next salary
/// written for them lands on the new version.</para>
///
/// <para><b>Release A tenants</b> are skipped entirely: their grade cash allowances are Benefits by grade (the matrix),
/// and their grade structures keep being built from it as before.</para>
///
/// <para>Stages changes only; the caller saves them in the same SaveChanges as the grade, so the grade and its structure
/// never disagree after a failed write.</para>
/// </summary>
public static class GradeSalaryStructureSync
{
    /// <summary>
    /// Stages the grade's structure to match <paramref name="lines"/> (the grade's package as it will be saved).
    /// <paramref name="createIfMissing"/> false: only an existing structure is kept in step (a grade save that did not
    /// touch the package). Returns the structure the grade's employees should now be put on, or null when skipped.
    /// </summary>
    public static async Task<SalaryStructure?> StageAsync(ZayraDbContext db, Grade grade, IReadOnlyCollection<GradePayScaleComponent> lines,
        Guid? userId, bool createIfMissing, CancellationToken ct)
    {
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(db, grade.TenantId, ct)) return null;
        var code = EmployeeSalaryBreakdownWriter.DefaultStructureCode(grade);
        // Only lines that are part of the MONTHLY salary row become structure components. Payroll's tax base sums every
        // taxable component of the employee's structure on every regular run, so an Annual earning (a yearly bonus) or a
        // non-cash Benefit line copied here would be taxed each month although it is never paid monthly. Same rule as the
        // salary writer (MonthlySalaryField); the grade itself keeps every line, and Payroll → Grade packages shows them all.
        var desired = lines
            .Where(l => l.IsActive && !string.IsNullOrWhiteSpace(l.ComponentCode)
                && EmployeeSalaryBreakdownWriter.MonthlySalaryField(l) is not null)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.ComponentCode, StringComparer.Ordinal)
            .Select(l => new ComponentShape(l.ComponentCode.Trim().ToUpperInvariant(), l.ComponentName.Trim(), l.ComponentType,
                CalculationTypeOf(l.CalculationType), l.Amount, l.Percentage, l.IsTaxable))
            .ToList();
        var name = $"{grade.Name} salary structure";

        var current = await CurrentAsync(db, grade.TenantId, grade.CompanyId, code, ct);
        if (current is null)
        {
            if (!createIfMissing) return null;
            var created = NewStructure(grade, code, name, userId, version: 1, previous: null);
            // The first version stands for the package since the grade existed; later versions start the day they open.
            created.EffectiveDate = DateOnly.FromDateTime(grade.CreatedAtUtc == default ? DateTime.UtcNow : grade.CreatedAtUtc);
            db.SalaryStructures.Add(created);
            AddComponents(db, grade.TenantId, created.Id, desired);
            return created;
        }

        var existing = await db.SalaryComponents
            .Where(c => c.TenantId == grade.TenantId && c.SalaryStructureId == current.Id && c.IsActive)
            .ToListAsync(ct);
        var unchanged = current.Name == name && current.Currency == grade.Currency && current.IsActive
            && current.MinGrossSalary == grade.MinSalary && current.MaxGrossSalary == grade.MaxSalary
            && existing.OrderBy(c => c.Code, StringComparer.Ordinal).Select(Shape)
                .SequenceEqual(desired.OrderBy(c => c.Code, StringComparer.Ordinal));
        if (unchanged) return current;

        var inUse = await db.EmployeeSalaryStructures
            .AnyAsync(x => x.TenantId == grade.TenantId && x.SalaryStructureId == current.Id && x.IsActive, ct);
        if (!inUse)
        {
            current.Name = name;
            current.Currency = grade.Currency;
            current.MinGrossSalary = grade.MinSalary;
            current.MaxGrossSalary = grade.MaxSalary;
            current.IsActive = true;
            db.SalaryComponents.RemoveRange(await db.SalaryComponents
                .Where(c => c.TenantId == grade.TenantId && c.SalaryStructureId == current.Id).ToListAsync(ct));
            AddComponents(db, grade.TenantId, current.Id, desired);
            return current;
        }

        current.IsActive = false;
        var versionNumber = current.VersionNumber + 1;
        var version = NewStructure(grade, await VersionCodeAsync(db, grade.TenantId, grade.CompanyId, code, versionNumber, ct),
            name, userId, versionNumber, current);
        db.SalaryStructures.Add(version);
        AddComponents(db, grade.TenantId, version.Id, desired);
        return version;
    }

    /// <summary>
    /// The structure a salary assignment without a named structure goes on: the employee's default (the grade's
    /// structure, or DIRECT for a gradeless employee), created from the grade when it does not exist yet. Refuses as
    /// <see cref="EmployeeSalaryBreakdownWriter.ResolveStructureAsync"/> does.
    /// </summary>
    public static async Task<SalaryStructure> EnsureDefaultStructureAsync(ZayraDbContext db, Employee employee, Guid? userId, CancellationToken ct)
    {
        var grade = employee.GradeId is null
            ? null
            : await db.Grades.FirstOrDefaultAsync(g => g.TenantId == employee.TenantId && g.Id == employee.GradeId && !g.IsDeleted, ct);
        if (await EmployeeSalaryBreakdownWriter.ResolveStructureAsync(db, employee, grade, null, ct) is { } resolved) return resolved;
        if (grade is not null)
        {
            var lines = await db.GradePayScaleComponents.AsNoTracking()
                .Where(c => c.TenantId == grade.TenantId && c.GradeId == grade.Id && c.IsActive).ToListAsync(ct);
            // Release A: the grade's structure is built from the matrix, as the employee writer builds it.
            if (await StageAsync(db, grade, lines, userId, createIfMissing: true, ct) is { } synced) return synced;
        }
        var currency = grade?.Currency is { Length: > 0 } gradeCurrency ? gradeCurrency : await CompanyCurrencyAsync(db, employee, ct);
        var structure = new SalaryStructure
        {
            TenantId = employee.TenantId!.Value, CompanyId = employee.CompanyId,
            Code = EmployeeSalaryBreakdownWriter.DefaultStructureCode(grade),
            Name = grade is null ? "Direct salary structure" : $"{grade.Name} salary structure",
            Currency = currency, EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow), CreatedBy = userId,
        };
        if (grade is not null && await EntitlementMatrixService.ReleaseAEnabledAsync(db, grade.TenantId, ct))
            db.SalaryComponents.AddRange(EntitlementMatrixService.SalaryComponentsFor(
                await EntitlementMatrixService.CashAllowancesAsync(db, grade.TenantId, grade.Id, employee.CompanyId, structure.EffectiveDate, ct),
                grade.TenantId, structure.Id));
        db.SalaryStructures.Add(structure);
        return structure;
    }

    /// <summary>True when <paramref name="structureCode"/> is one of the grade's structure versions (GRADE-x, GRADE-x-Vn).</summary>
    public static bool IsGradeStructureCode(string structureCode, string gradeCode)
    {
        var root = $"GRADE-{gradeCode}";
        return string.Equals(structureCode, root, StringComparison.Ordinal)
            || structureCode.StartsWith(root + "-V", StringComparison.Ordinal) && int.TryParse(structureCode[(root.Length + 2)..], out _);
    }

    /// <summary>The grade's current structure in exactly the grade's company: the root code, followed forward along its
    /// versions to the active one (or the latest, when every version is inactive).</summary>
    public static async Task<SalaryStructure?> CurrentAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, string code, CancellationToken ct)
    {
        var chain = await db.SalaryStructures
            .Where(s => s.TenantId == tenantId && s.CompanyId == companyId && !s.IsDeleted && (s.Code == code || s.Code.StartsWith(code + "-V")))
            .ToListAsync(ct);
        var current = chain.Where(s => s.Code == code).OrderByDescending(s => s.IsActive).ThenByDescending(s => s.VersionNumber).FirstOrDefault();
        if (current is null) return null;
        var seen = new HashSet<Guid> { current.Id };
        while (!current.IsActive
               && chain.Where(s => s.PreviousVersionId == current.Id).OrderByDescending(s => s.VersionNumber).FirstOrDefault() is { } next
               && seen.Add(next.Id))
            current = next;
        return current;
    }

    private sealed record ComponentShape(string Code, string Name, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable);

    private static ComponentShape Shape(SalaryComponent c) =>
        new(c.Code, c.Name, c.ComponentType, c.CalculationType, c.Amount, c.Percentage, c.IsTaxable);

    /// <summary>A grade line says PercentOfBasic; a salary component says Percentage (the payroll tax base reads that).</summary>
    private static string CalculationTypeOf(string gradeCalculation) =>
        string.Equals(gradeCalculation, "PercentOfBasic", StringComparison.OrdinalIgnoreCase)
        || string.Equals(gradeCalculation, "Percentage", StringComparison.OrdinalIgnoreCase) ? "Percentage" : "Fixed";

    private static SalaryStructure NewStructure(Grade grade, string code, string name, Guid? userId, int version, SalaryStructure? previous) => new()
    {
        TenantId = grade.TenantId, CompanyId = grade.CompanyId, Code = code, Name = name, Currency = grade.Currency,
        EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
        MinGrossSalary = grade.MinSalary, MaxGrossSalary = grade.MaxSalary,
        MinBasicSalary = previous?.MinBasicSalary ?? 0m, MaxBasicSalary = previous?.MaxBasicSalary ?? 0m,
        EligibleGradeIdsJson = previous?.EligibleGradeIdsJson ?? "[]",
        EligibleDesignationIdsJson = previous?.EligibleDesignationIdsJson ?? "[]",
        VersionNumber = version, PreviousVersionId = previous?.Id, IsActive = true, CreatedBy = userId,
    };

    private static void AddComponents(ZayraDbContext db, Guid tenantId, Guid structureId, IEnumerable<ComponentShape> components)
    {
        foreach (var c in components)
            db.SalaryComponents.Add(new SalaryComponent
            {
                TenantId = tenantId, SalaryStructureId = structureId, Code = c.Code, Name = c.Name, ComponentType = c.ComponentType,
                CalculationType = c.CalculationType, Amount = c.Amount, Percentage = c.Percentage, IsTaxable = c.IsTaxable, IsActive = true,
            });
    }

    private static async Task<string> VersionCodeAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, string code, int versionNumber, CancellationToken ct)
    {
        var used = (await db.SalaryStructures
                .Where(s => s.TenantId == tenantId && s.CompanyId == companyId && !s.IsDeleted && s.Code.StartsWith(code))
                .Select(s => s.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var n = versionNumber;
        var candidate = $"{code}-V{n}";
        while (used.Contains(candidate)) candidate = $"{code}-V{++n}";
        return candidate;
    }

    private static async Task<string> CompanyCurrencyAsync(ZayraDbContext db, Employee employee, CancellationToken ct)
    {
        if (employee.CompanyId is not Guid companyId) return "AED";
        var currency = await db.Companies.AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId && x.Id == companyId).Select(x => x.DefaultCurrency).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(currency) ? "AED" : currency.Trim().ToUpperInvariant();
    }
}
