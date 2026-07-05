using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Infrastructure.Boot;

/// <summary>
/// Boot-time guard for the company dimension, mirroring TenantOwnershipBootAssertion:
/// a CompanyId column that isn't wired into ICompanyScoped silently escapes the company
/// query filter — exactly how the LeavePolicy.CompanyId column sat unenforced for months.
///
/// Checks:
///   1. Entity has a CompanyId property but implements no ICompanyScoped variant → violation
///      (unless allow-listed below with a structural justification).
///   2. Entity implements ICompanyScoped but has no Guid? CompanyId property, or the EF
///      model does not map a CompanyId property → violation.
///
/// ROLLOUT BEHAVIOR: strict (throw ⇒ failed boot) everywhere except Production.
/// In Production it logs errors only, unless ZAYRA_COMPANY_SCOPE_ASSERT=strict is set —
/// flip that env var once a deploy cycle has proven clean, then it hard-fails there too.
/// Tests call Assert(db, strict: true) directly, so CI is always strict.
/// </summary>
public static class CompanyScopeBootAssertion
{
    public const string StrictEnvVar = "ZAYRA_COMPANY_SCOPE_ASSERT";

    /// <summary>
    /// Entities that legitimately carry CompanyId without the scoped-filter interface.
    /// Every entry needs a structural justification — this list is reviewed, not a dumping ground.
    /// </summary>
    private static readonly Dictionary<string, string> AllowList = new()
    {
        ["Branch"] = "Org-structure child: non-nullable Guid CompanyId (a branch cannot exist " +
                     "outside a company); visibility follows its parent Company, and the " +
                     "nullable-CompanyId interface contract cannot apply.",
        ["UserEntityAccess"] = "Access-grant record: CompanyId IS the grant payload. Filtering " +
                               "grants by company scope would be circular (grants define the scope) " +
                               "and would hide grant administration from group admins.",
    };

    public static IReadOnlyList<string> FindViolations(DbContext db)
    {
        var violations = new List<string>();

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            if (entityType.IsOwned() || entityType.BaseType is not null) continue;

            var clr = entityType.ClrType;
            var companyIdProp = clr.GetProperty("CompanyId", BindingFlags.Public | BindingFlags.Instance);
            var implementsScoped = typeof(ICompanyScoped).IsAssignableFrom(clr);

            // Case 1: CompanyId exists but no scope interface — the filter silently won't apply.
            if (companyIdProp is not null && !implementsScoped && !AllowList.ContainsKey(clr.Name))
            {
                violations.Add(
                    $"  {clr.FullName}: has CompanyId (type={companyIdProp.PropertyType.Name}) but does not " +
                    $"implement ICompanyScoped/ICompanyScopedOperational. The company query filter will NOT " +
                    $"apply. Implement the interface (Operational for records, plain for config/templates) " +
                    $"or add a justified entry to CompanyScopeBootAssertion.AllowList.");
            }

            if (implementsScoped)
            {
                // Case 2a: interface without the property (interface guarantees it exists, but a
                // shadowing/static mistake could break it) or wrong CLR type.
                if (companyIdProp is null || companyIdProp.PropertyType != typeof(Guid?))
                {
                    violations.Add(
                        $"  {clr.FullName}: implements ICompanyScoped but CompanyId is missing or not Guid? " +
                        $"(found {companyIdProp?.PropertyType.Name ?? "nothing"}).");
                }
                // Case 2b: EF model must actually map CompanyId (an [NotMapped] or ignored property
                // would leave the filter querying a column that doesn't exist).
                else if (entityType.FindProperty("CompanyId") is null)
                {
                    violations.Add(
                        $"  {clr.FullName}: implements ICompanyScoped but the EF model does not map a " +
                        $"CompanyId property — the query filter would reference an unmapped column.");
                }
            }
        }

        return violations;
    }

    public static void Assert(DbContext db, bool strict, ILogger? logger = null)
    {
        var violations = FindViolations(db);
        if (violations.Count == 0) return;

        var message =
            $"Company-scope boot assertion failed — {violations.Count} entity type(s) are mis-declared:\n" +
            string.Join("\n", violations) +
            "\n\nFix: implement ICompanyScopedOperational (operational records) or ICompanyScoped " +
            "(config/templates with tenant-wide null semantics), or allow-list with justification.";

        if (strict)
            throw new InvalidOperationException(message);
        logger?.LogError("{Message}", message);
    }

    /// <summary>Strict everywhere except Production; Production opts in via ZAYRA_COMPANY_SCOPE_ASSERT=strict.</summary>
    public static bool ResolveStrictMode(bool isProduction) =>
        !isProduction ||
        string.Equals(Environment.GetEnvironmentVariable(StrictEnvVar), "strict", StringComparison.OrdinalIgnoreCase);
}
