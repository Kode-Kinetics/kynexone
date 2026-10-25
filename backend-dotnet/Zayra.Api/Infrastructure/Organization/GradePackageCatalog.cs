using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Organization;

/// <summary>A pay component a grade's package line may use: identity, type and taxable come from the catalogue.</summary>
public sealed record GradePackageComponentOption(
    Guid? Id, Guid? CompanyId, string Code, string NameEn, string NameAr, string ComponentType, bool IsTaxable, bool IsSystem);

/// <summary>The inline "+ New component" of the grade editor. A blank code is derived from the English name.</summary>
public sealed record GradePackageComponentCreateRequest(string? Code, string? NameEn, string? NameAr, string? ComponentType, bool IsTaxable = false);

/// <summary>
/// The pay components a grade's package (Setup → Grades) is built from, and the one way to add one from there.
///
/// <para><b>What may be on a grade.</b> The grade's lines become the employee's salary columns (basic, housing,
/// transport, food, mobile, other, fixed deduction), so a line may use only a component whose amount payroll takes
/// from those columns or from nowhere else:</para>
/// <list type="bullet">
/// <item>the system salary-column components (BASIC, HOUSING, TRANSPORT, OTHER_ALLOWANCES, FIXED_DEDUCTION), and</item>
/// <item>grade-package definitions (<see cref="PayComponentCalcMethods.GradePackage"/>), added from the grade editor.</item>
/// </list>
/// <para>A tenant <i>configured-value</i> component (Fixed / % of basic / % of gross, from the payroll component API) is
/// NOT offered: payroll already pays it to every employee of its scope, so putting it on a grade too would pay it twice.
/// Statutory, provider and family rows are never pay-package lines.</para>
///
/// <para><b>Scope.</b> Company-first, like every catalogue read: the company's own row wins over the tenant default per
/// (code, type). A component added from the grade editor is the grade's company's row.</para>
/// </summary>
public static class GradePackageCatalog
{
    public static readonly string[] Types = [PayComponentTypes.Earning, PayComponentTypes.Benefit, PayComponentTypes.Deduction];

    private static readonly HashSet<string> SalaryColumnFields = new(StringComparer.Ordinal)
    {
        PayComponentStructureFields.BasicSalary, PayComponentStructureFields.HousingAllowance,
        PayComponentStructureFields.TransportAllowance, PayComponentStructureFields.OtherAllowancesComposite,
        PayComponentStructureFields.FixedDeduction,
    };

    private static readonly Regex CodePattern = new("^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>True when a catalogue row may be used as a grade package line (see the class summary).</summary>
    public static bool IsPackageComponent(PayComponent c) =>
        c.IsActive && !c.IsDeleted && c.IsOffered && !c.IsStatutory && !c.IsFamily && string.IsNullOrEmpty(c.ProviderKey)
        && (c.CalcMethod == PayComponentCalcMethods.GradePackage && Types.Contains(c.ComponentType)
            || c.CalcMethod == PayComponentCalcMethods.StructureField && c.StructureField is { } field && SalaryColumnFields.Contains(field)
               && c.ComponentType is PayComponentTypes.Earning or PayComponentTypes.Deduction);

    /// <summary>The components a grade of <paramref name="companyId"/> may use, in effect today, company-first.</summary>
    public static async Task<IReadOnlyList<GradePackageComponentOption>> OptionsAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var rows = await db.PayComponents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && (c.CompanyId == null || c.CompanyId == companyId) && !c.IsDeleted)
            .ToListAsync(ct);
        return Resolve(rows, tenantId, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    /// <summary>Pure resolution, shared by the read and the save-time check. A tenant whose catalogue was never seeded
    /// sees the compiled system rows (the same fallback payroll uses).</summary>
    public static IReadOnlyList<GradePackageComponentOption> Resolve(IEnumerable<PayComponent> rows, Guid tenantId, DateOnly asOf)
    {
        var inEffect = rows.Where(c => c.IsInEffect(asOf)).ToList();
        var candidates = inEffect.Select(c => (C: c, Rank: c.CompanyId is null ? 1 : 2));
        if (!inEffect.Any(c => c.IsSystem))
            candidates = PayComponentCatalog.SystemComponentSeeds(tenantId).Select(c => (C: c, Rank: 0)).Concat(candidates);
        return candidates
            .GroupBy(x => (x.C.Code, x.C.ComponentType))
            .Select(g => g.OrderByDescending(x => x.Rank).ThenByDescending(x => x.C.EffectiveFrom ?? DateOnly.MinValue).First())
            .Where(x => IsPackageComponent(x.C))
            .OrderBy(x => Array.IndexOf(Types, x.C.ComponentType))
            .ThenBy(x => x.C.DisplayOrder).ThenBy(x => x.C.Code, StringComparer.Ordinal)
            .Select(x => new GradePackageComponentOption(x.Rank == 0 ? null : x.C.Id, x.C.CompanyId, x.C.Code, x.C.NameEn,
                string.IsNullOrWhiteSpace(x.C.NameAr) ? x.C.NameEn : x.C.NameAr, x.C.ComponentType,
                x.C.ComponentType == PayComponentTypes.Earning && x.C.IsTaxable, x.C.IsSystem))
            .ToList();
    }

    /// <summary>
    /// Stages a new grade-package definition for <paramref name="companyId"/> (not saved). Returns the row, or the
    /// field → message errors that refuse it. The code is one identity in the tenant: it may not reuse a system,
    /// statutory or subsystem code, a code of another type, or a code the company or the tenant default already has —
    /// a company row with a tenant component's code would silently override (and stop) that component's payment.
    /// </summary>
    public static async Task<(PayComponent? Row, Dictionary<string, string> Errors)> StageCreateAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, GradePackageComponentCreateRequest req, Guid? userId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameEn = (req.NameEn ?? string.Empty).Trim();
        var nameAr = (req.NameAr ?? string.Empty).Trim();
        var type = Types.FirstOrDefault(t => string.Equals(t, (req.ComponentType ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (nameEn.Length is 0 or > 200) errors["nameEn"] = "Enter the component's name in English (at most 200 characters).";
        if (nameAr.Length > 200) errors["nameAr"] = "The Arabic name may be at most 200 characters.";
        if (type is null) errors["componentType"] = "Type must be Earning, Benefit or Deduction.";

        var explicitCode = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
        var derived = explicitCode.Length == 0;
        var code = derived ? DeriveCode(nameEn) : explicitCode;
        if (!derived && (code.Length is < 2 or > PayComponentPolicy.MaxCodeLength || !CodePattern.IsMatch(code)))
            errors["code"] = $"Code must be 2–{PayComponentPolicy.MaxCodeLength} characters of A–Z, 0–9 and '_', starting with a letter.";
        if (errors.Count > 0) return (null, errors);

        var taken = (await db.PayComponents.AsNoTracking()
                .Where(c => c.TenantId == tenantId && !c.IsDeleted)
                .Select(c => new { c.Code, c.CompanyId, c.ComponentType })
                .ToListAsync(ct))
            .Where(c => c.CompanyId == null || c.CompanyId == companyId || c.ComponentType != type)
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Unavailable(string candidate) => taken.Contains(candidate) || PayComponentPolicy.IsReservedCode(candidate);
        if (derived)
        {
            var stem = code;
            for (var n = 2; Unavailable(code); n++)
            {
                var suffix = $"_{n}";
                code = (stem.Length + suffix.Length > PayComponentPolicy.MaxCodeLength ? stem[..(PayComponentPolicy.MaxCodeLength - suffix.Length)] : stem) + suffix;
            }
        }
        else if (PayComponentPolicy.IsReservedCode(code))
            return (null, new() { ["code"] = $"'{code}' is reserved by the system or the statutory pack. Choose it from the list, or use another code." });
        else if (taken.Contains(code))
            return (null, new() { ["code"] = $"A pay component with code '{code}' already exists. Choose it from the list, or use another code." });

        if (!await db.PayComponents.AnyAsync(c => c.TenantId == tenantId && c.CompanyId == null && c.IsSystem, ct)
            && !db.PayComponents.Local.Any(c => c.TenantId == tenantId && c.CompanyId == null && c.IsSystem))
            // Keep the catalogue invariant the payroll component API keeps: no tenant row without the system rows beside it.
            await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, ct);

        var row = new PayComponent
        {
            TenantId = tenantId, CompanyId = companyId, Code = code, NameEn = nameEn, NameAr = nameAr.Length == 0 ? nameEn : nameAr,
            ComponentType = type!, CalcMethod = PayComponentCalcMethods.GradePackage, Value = null,
            IsTaxable = type == PayComponentTypes.Earning && req.IsTaxable, GosiSubject = false,
            WpsIncluded = type == PayComponentTypes.Earning, EosbIncluded = false, GlDriverKey = null,
            DisplayOrder = 600, EffectiveFrom = null, EffectiveTo = null,
            IsSystem = false, IsStatutory = false, IsActive = true, IsOffered = true, CreatedBy = userId,
        };
        db.PayComponents.Add(row);
        return (row, errors);
    }

    /// <summary>UPPER_SNAKE from an English name ("Car allowance" → CAR_ALLOWANCE); "COMPONENT" when nothing is left.</summary>
    public static string DeriveCode(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var code = sb.ToString().Trim('_');
        if (code.Length > 0 && !char.IsLetter(code[0])) code = "C_" + code;
        if (code.Length < 2) code = "COMPONENT";
        return code.Length > PayComponentPolicy.MaxCodeLength ? code[..PayComponentPolicy.MaxCodeLength].TrimEnd('_') : code;
    }
}
