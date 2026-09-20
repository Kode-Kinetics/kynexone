using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Deterministic, rule-based GOSI contribution calculator.
///
/// Consumes a pre-loaded list of <see cref="GosiContributionRule"/> records
/// (global defaults + tenant overrides) and produces per-branch contribution lines
/// for one employee for one pay period.
///
/// This class is stateless and has no database dependency — load rules once per
/// payroll run and reuse across all employees.
/// </summary>
public static class GosiCalculationService
{
    // Saudi Arabia ISO-3166-1 alpha-2 code
    private const string SaudiCode = "SA";

    // GCC member states (ISO-3166-1 alpha-2)
    private static readonly HashSet<string> GccCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BH", "KW", "OM", "QA", "AE",
    };

    // Normalised nationality strings that map to Saudi classification
    private static readonly HashSet<string> SaudiNationalityTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "SA", "SAU", "Saudi", "Saudi Arabia", "Saudi Arabian",
    };

    // GCC country names in common HR system spellings
    private static readonly Dictionary<string, string> GccNationalityTerms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BH"]      = "GCC", ["Bahrain"]             = "GCC", ["Bahraini"]      = "GCC",
            ["KW"]      = "GCC", ["Kuwait"]               = "GCC", ["Kuwaiti"]        = "GCC",
            ["OM"]      = "GCC", ["Oman"]                 = "GCC", ["Omani"]          = "GCC",
            ["QA"]      = "GCC", ["Qatar"]                = "GCC", ["Qatari"]         = "GCC",
            ["AE"]      = "GCC", ["United Arab Emirates"] = "GCC", ["Emirati"]        = "GCC",
            ["UAE"]     = "GCC",
        };

    // S1/A4 — the HOME STATE behind a GCC classification. Under the GCC Unified Insurance Extension
    // Scheme a GCC national employed in Saudi Arabia is insured under their OWN state's scheme at
    // their OWN state's rates, collected by GOSI — so "GCC" alone is not enough to compute anything.
    // Every spelling in GccNationalityTerms maps here to an ISO-3166-1 alpha-2 home state.
    private static readonly Dictionary<string, string> GccHomeStates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BH"] = "BH", ["Bahrain"]             = "BH", ["Bahraini"] = "BH",
            ["KW"] = "KW", ["Kuwait"]              = "KW", ["Kuwaiti"]  = "KW",
            ["OM"] = "OM", ["Oman"]                = "OM", ["Omani"]    = "OM",
            ["QA"] = "QA", ["Qatar"]               = "QA", ["Qatari"]   = "QA",
            ["AE"] = "AE", ["United Arab Emirates"] = "AE", ["Emirati"] = "AE", ["UAE"] = "AE",
        };

    /// <summary>
    /// Derives GosiClassifications.Saudi / GCC / NonSaudi from a raw nationality string.
    /// </summary>
    public static string DeriveClassification(string? nationality)
    {
        if (string.IsNullOrWhiteSpace(nationality))
            return GosiClassifications.NonSaudi;

        if (SaudiNationalityTerms.Contains(nationality))
            return GosiClassifications.Saudi;

        if (GccNationalityTerms.ContainsKey(nationality))
            return GosiClassifications.GCC;

        return GosiClassifications.NonSaudi;
    }

    /// <summary>
    /// S1/A4 — the ISO-3166-1 alpha-2 home state for a GCC national, or null for anyone who is not
    /// one. Kept beside <see cref="DeriveClassification"/> so the two can never disagree about who
    /// counts as GCC.
    /// </summary>
    public static string? DeriveGccHomeState(string? nationality)
        => !string.IsNullOrWhiteSpace(nationality)
           && !SaudiNationalityTerms.Contains(nationality)
           && GccHomeStates.TryGetValue(nationality, out var iso)
            ? iso
            : null;

    /// <summary>
    /// Selects active rules from <paramref name="allRules"/> that apply to
    /// <paramref name="classification"/> on <paramref name="periodDate"/>.
    ///
    /// Tenant-specific rules take precedence over system-default rules for the
    /// same (Branch, Payer) combination.  System defaults have TenantId == Guid.Empty.
    /// </summary>
    public static IReadOnlyList<GosiContributionRule> SelectActiveRules(
        string                       classification,
        IReadOnlyList<GosiContributionRule> allRules,
        DateOnly                     periodDate,
        Guid                         tenantId)
    {
        var effective = allRules
            .Where(r => r.Classification == classification
                     && r.EffectiveFrom <= periodDate
                     && (r.EffectiveTo == null || r.EffectiveTo >= periodDate))
            .ToList();

        // Group by (Branch, Payer) and prefer tenant override over system default
        return effective
            .GroupBy(r => (r.Branch, r.Payer))
            .Select(g =>
                g.FirstOrDefault(r => r.TenantId == tenantId)   // tenant override wins
             ?? g.First(r => r.TenantId == Guid.Empty))         // fall back to global default
            .ToList();
    }

    /// <summary>
    /// Calculates GOSI contributions for a single employee for one pay period.
    /// </summary>
    /// <param name="nationality">Raw nationality string from the Employee record.</param>
    /// <param name="contributoryWage">
    /// S1/A2(b) — the GOSI CONTRIBUTORY WAGE, which for a Saudi national is <b>basic + housing</b>
    /// (cash or in-kind), not basic alone.
    ///
    /// <para>This parameter used to be named <c>basicSalary</c> and every caller passed basic. The
    /// country-pack engine (<c>KsaDeductionCalculator</c>, via <c>SalaryBreakdown.GosiCoveredWage</c>)
    /// has always used basic + housing, so the two engines disagreed by the whole housing allowance:
    /// the payslip deducted one number and the GOSI readiness report, the Saudi compliance dashboard
    /// and the GOSI preview all showed a smaller one. The dashboard is the figure a customer's finance
    /// team reconciles against the GOSI portal, so it was the wrong one that got trusted.</para>
    /// </param>
    /// <param name="allRules">All active rules for the tenant — preloaded once per run.</param>
    /// <param name="periodDate">The last date of the pay period (used for effective-date selection).</param>
    /// <param name="tenantId">The tenant ID for override precedence resolution.</param>
    /// <param name="bounds">
    /// The MONTHLY contributory-wage bounds, resolved from the effective-dated statutory rules
    /// engine by <see cref="KsaGosiWageBounds.ResolveAsync"/> — the same call, the same rule key and
    /// the same effective date the payroll run's country pack uses. Every caller must resolve them
    /// that way; a caller that passes <see cref="GosiWageBounds.Unbounded"/> is asking for the
    /// uncapped figure and will disagree with the payslip.
    /// </param>
    public static GosiContributionResult Calculate(
        string?                             nationality,
        decimal                             contributoryWage,
        IReadOnlyList<GosiContributionRule> allRules,
        DateOnly                            periodDate,
        Guid                                tenantId,
        GosiWageBounds                      bounds)
    {
        var classification = DeriveClassification(nationality);
        var rules          = SelectActiveRules(classification, allRules, periodDate, tenantId);

        var lines = new List<GosiContributionLine>();

        foreach (var rule in rules)
        {
            if (rule.Rate <= 0m) continue;

            // Clamp to the statutory MONTHLY contributory-wage bounds.
            //
            // GosiContributionRule.MinContributoryWage / MaxContributoryWage are deliberately NOT
            // read here. They were a second, per-row store for a statutory value, and because
            // GosiRuleSeeder never populated them the platform-default path computed uncapped while
            // the payroll run's country pack capped at SAR 45,000 off the statutory rules engine.
            // One number, one way: the bounds come from KsaGosiWageBounds and nowhere else.
            // GosiCeilingSingleSourceTests pins that those two columns stay unread.
            var wage = bounds.Clamp(contributoryWage);

            var amount = Math.Round(wage * rule.Rate / 100m, 2);
            if (amount <= 0m) continue;

            lines.Add(new GosiContributionLine(
                Branch:           rule.Branch,
                Payer:            rule.Payer,
                Rate:             rule.Rate,
                ContributoryWage: wage,
                Amount:           amount,
                RuleId:           rule.Id.ToString()));
        }

        var employeeTotal = lines.Where(l => l.Payer == GosiPayers.Employee).Sum(l => l.Amount);
        var employerTotal = lines.Where(l => l.Payer == GosiPayers.Employer).Sum(l => l.Amount);

        return new GosiContributionResult(
            Classification: classification,
            EmployeeTotal:  employeeTotal,
            EmployerTotal:  employerTotal,
            Lines:          lines);
    }

    /// <summary>
    /// Returns the PayrollDeduction component code for a GOSI contribution line.
    /// Employee codes are deducted from net pay; employer codes are tracked separately.
    /// </summary>
    public static string ToComponentCode(string branch, string payer)
    {
        var suffix = payer == GosiPayers.Employee ? "EMP" : "ER";
        return branch switch
        {
            GosiBranches.Annuities           => $"GOSI_ANNUITIES_{suffix}",
            GosiBranches.SANED               => $"GOSI_SANED_{suffix}",
            GosiBranches.OccupationalHazards => $"GOSI_OCHAZARDS_{suffix}",
            _                                => $"GOSI_{branch.ToUpperInvariant()}_{suffix}",
        };
    }

    /// <summary>
    /// Returns a human-readable component name for a GOSI contribution line.
    /// </summary>
    public static string ToComponentName(string branch, string payer, decimal rate)
    {
        var payerLabel = payer == GosiPayers.Employee ? "employee" : "employer";
        return branch switch
        {
            GosiBranches.Annuities           => $"GOSI Annuities ({payerLabel} {rate}%)",
            GosiBranches.SANED               => $"GOSI SANED ({payerLabel} {rate}%)",
            GosiBranches.OccupationalHazards => $"GOSI Occupational Hazards ({payerLabel} {rate}%)",
            _                                => $"GOSI {branch} ({payerLabel} {rate}%)",
        };
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

public record GosiContributionResult(
    string                          Classification,
    decimal                         EmployeeTotal,
    decimal                         EmployerTotal,
    IReadOnlyList<GosiContributionLine> Lines
);

public record GosiContributionLine(
    string  Branch,
    string  Payer,
    decimal Rate,
    decimal ContributoryWage,
    decimal Amount,
    string  RuleId
);
