using Zayra.Api.Application.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

/// <summary>One catalogue component and the rules a grade cell for it must follow.</summary>
/// <param name="Class">A <see cref="PayEntitlementClasses"/> value.</param>
/// <param name="Floor">A <see cref="PayStatutoryFloors"/> value.</param>
/// <param name="ComponentType">The pay_components type it is seeded with (<see cref="PayComponentTypes"/>).</param>
/// <param name="AllowedDependantScopes">Always includes None unless the floor requires a scope.</param>
/// <param name="IsLoanFacility">A LOAN_* code: edited in Loans (grade limits), shown read-only in the matrix.</param>
public sealed record EntitlementComponentRule(
    string Code,
    string NameEn,
    string NameAr,
    string Class,
    string Floor,
    string ComponentType,
    IReadOnlyList<string> AllowedValueTypes,
    IReadOnlyList<string> AllowedCoverageTiers,
    IReadOnlyList<string> AllowedDependantScopes,
    IReadOnlyList<string> AllowedLimitPeriods,
    string? DefaultLimitPeriod,
    bool IsLoanFacility)
{
    public bool AllowsDependants => AllowedDependantScopes.Any(s => s != DependantScopes.None);

    /// <summary>Floor components are eligible for every grade and can never be skipped by a company.</summary>
    public bool IsFloor => Floor != PayStatutoryFloors.None;
}

/// <summary>
/// The Release A entitlement catalogue (plan §1.1): which value types, tiers, dependants and periods each
/// component allows, and its statutory floor. The database enforces the cell SHAPE; these rules — which need the
/// component code, and which a composite FK cannot carry because live <c>grade_entitlements</c> has no FK to
/// <c>pay_components</c> (the L1 DEVIATION) — are enforced by the matrix service (R1) and proven by test.
///
/// <para>Criteria are only months of service, after probation, and nationality (with a recorded legal basis).
/// Age, gender, marital status and disability are never criteria; dependants are a coverage basis, never an
/// eligibility criterion.</para>
/// </summary>
public static class EntitlementComponentRules
{
    public const string Housing = "HOUSING";
    public const string Transport = "TRANSPORT";
    public const string OtherAllowances = "OTHER_ALLOWANCES";
    public const string AirTicket = "AIR_TICKET";
    public const string Medical = "MEDICAL";
    public const string Education = "EDUCATION";
    public const string PerDiem = "PER_DIEM";
    public const string LoanPrefix = "LOAN_";
    public const string LoanHousingAdvance = "LOAN_HOUSING_ADVANCE";

    /// <summary>Criteria a cell may never use (non-discrimination). R1 refuses them with ENTITLEMENT_BANNED_CRITERION.</summary>
    public static readonly IReadOnlyList<string> BannedCriteria = ["age", "gender", "maritalStatus", "disability"];

    private static readonly string[] NoDependants = [DependantScopes.None];
    private static readonly string[] Monthly = [EntitlementLimitPeriods.Monthly];

    public static readonly IReadOnlyList<EntitlementComponentRule> Catalogue =
    [
        new(Housing, "Housing allowance", "بدل السكن", PayEntitlementClasses.QiwaWage, PayStatutoryFloors.Housing, PayComponentTypes.Earning,
            [GradeEntitlementValueTypes.Amount, GradeEntitlementValueTypes.PercentOfBasic, GradeEntitlementValueTypes.InKind],
            [], NoDependants, Monthly, EntitlementLimitPeriods.Monthly, false),
        new(Transport, "Transport allowance", "بدل النقل", PayEntitlementClasses.QiwaWage, PayStatutoryFloors.Transport, PayComponentTypes.Earning,
            [GradeEntitlementValueTypes.Amount, GradeEntitlementValueTypes.PercentOfBasic, GradeEntitlementValueTypes.InKind],
            [], NoDependants, Monthly, EntitlementLimitPeriods.Monthly, false),
        new(OtherAllowances, "Other allowances", "بدلات أخرى", PayEntitlementClasses.QiwaWage, PayStatutoryFloors.None, PayComponentTypes.Earning,
            [GradeEntitlementValueTypes.Amount, GradeEntitlementValueTypes.PercentOfBasic],
            [], NoDependants, Monthly, EntitlementLimitPeriods.Monthly, false),
        new(AirTicket, "Annual air ticket", "تذكرة السفر السنوية", PayEntitlementClasses.Contractual, PayStatutoryFloors.None, PayComponentTypes.Benefit,
            [GradeEntitlementValueTypes.Quantity], CoverageTiers.Travel,
            [DependantScopes.None, DependantScopes.Spouse, DependantScopes.Children, DependantScopes.Family],
            [EntitlementLimitPeriods.Annual, EntitlementLimitPeriods.PerTerm], EntitlementLimitPeriods.Annual, false),
        // Owner decision: the medical floor is the employee AND family at CCHI basic for every grade, so the scope is
        // always Family and a grade can only enhance the tier.
        new(Medical, "Medical insurance", "التأمين الطبي", PayEntitlementClasses.Contractual, PayStatutoryFloors.Medical, PayComponentTypes.Benefit,
            [GradeEntitlementValueTypes.CoverageTier], CoverageTiers.Medical, [DependantScopes.Family],
            [EntitlementLimitPeriods.PerTerm], EntitlementLimitPeriods.PerTerm, false),
        new(Education, "Children's education allowance", "بدل تعليم الأبناء", PayEntitlementClasses.Contractual, PayStatutoryFloors.None, PayComponentTypes.Benefit,
            [GradeEntitlementValueTypes.Amount], [], [DependantScopes.Children],
            [EntitlementLimitPeriods.Annual], EntitlementLimitPeriods.Annual, false),
        new(PerDiem, "Per diem", "بدل انتداب", PayEntitlementClasses.Facility, PayStatutoryFloors.None, PayComponentTypes.Facility,
            [GradeEntitlementValueTypes.Amount], [], NoDependants,
            [EntitlementLimitPeriods.PerDay], EntitlementLimitPeriods.PerDay, false),
        new(LoanHousingAdvance, "Housing advance", "سلفة السكن", PayEntitlementClasses.Facility, PayStatutoryFloors.None, PayComponentTypes.Facility,
            [GradeEntitlementValueTypes.Amount, GradeEntitlementValueTypes.MultipleOfBasic, GradeEntitlementValueTypes.MultipleOfGross,
             GradeEntitlementValueTypes.MultipleOfHousing, GradeEntitlementValueTypes.EligibilityOnly],
            [], NoDependants, [], null, true),
    ];

    private static readonly Dictionary<string, EntitlementComponentRule> ByCode =
        Catalogue.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rule for a code. Any other <c>LOAN_&lt;type&gt;</c> code is an L1 loan facility (Amount or a multiple of
    /// basic or gross, plus the outstanding cap). NULL for a code the catalogue does not know.
    /// </summary>
    public static EntitlementComponentRule? For(string code)
    {
        if (ByCode.TryGetValue(code, out var rule)) return rule;
        if (!code.StartsWith(LoanPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        return new EntitlementComponentRule(code.ToUpperInvariant(), code, code, PayEntitlementClasses.Facility, PayStatutoryFloors.None,
            PayComponentTypes.Facility,
            [GradeEntitlementValueTypes.Amount, GradeEntitlementValueTypes.MultipleOfBasic, GradeEntitlementValueTypes.MultipleOfGross,
             GradeEntitlementValueTypes.EligibilityOnly],
            [], NoDependants, [], null, true);
    }

    /// <summary>
    /// The statutory-floor violations of a cell, as block codes (<see cref="ReleaseABlockReasons"/>). Empty when the
    /// cell respects its floor. Shape and value-type errors are separate (the database and R1 report those).
    /// </summary>
    public static IReadOnlyList<string> FloorViolations(
        EntitlementComponentRule rule, bool eligible, string valueType, decimal? amount, decimal? rate,
        string? coverageTier, string dependantScope, bool afterProbation, string nationalityScope)
    {
        var codes = new List<string>();
        switch (rule.Floor)
        {
            case PayStatutoryFloors.Housing or PayStatutoryFloors.Transport:
                // Art. 61: cash or in kind, never neither. In kind counts as provision; a zero amount does not.
                var provided = eligible && (valueType == GradeEntitlementValueTypes.InKind
                    || (valueType == GradeEntitlementValueTypes.Amount && amount > 0)
                    || (valueType == GradeEntitlementValueTypes.PercentOfBasic && rate > 0));
                if (!provided)
                    codes.Add(rule.Floor == PayStatutoryFloors.Housing
                        ? ReleaseABlockReasons.EntitlementFloorHousing
                        : ReleaseABlockReasons.EntitlementFloorTransport);
                break;
            case PayStatutoryFloors.Medical:
                // CCHI: from day one, every nationality, at least the basic class, employee and family.
                if (!eligible || afterProbation || nationalityScope != NationalityScopes.Any
                    || valueType != GradeEntitlementValueTypes.CoverageTier
                    || CoverageTiers.MedicalRank(coverageTier) < 0
                    || dependantScope != DependantScopes.Family)
                    codes.Add(ReleaseABlockReasons.EntitlementFloorMedical);
                break;
            case PayStatutoryFloors.Art40:
                codes.Add(ReleaseABlockReasons.EntitlementArt40ReadOnly);
                break;
        }
        return codes;
    }

    /// <summary>A company may skip (not offer) a component only when it carries no statutory floor.</summary>
    public static bool CanBeSkipped(EntitlementComponentRule rule) => !rule.IsFloor && !rule.IsLoanFacility;
}
