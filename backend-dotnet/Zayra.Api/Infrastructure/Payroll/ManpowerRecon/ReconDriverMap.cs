namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>Why a continuing employee's line moved. <see cref="Proration"/> only ever arises as the residual of a package split.</summary>
public enum ReconDriver { Package, Proration, Variable, Arrears, Attendance, Loans, Statutory, Other }

/// <summary>The pay-component catalogue facts that refine a driver: how the component is fed and how its amount is derived.</summary>
public sealed record ComponentHint(string Code, string? ProviderKey, string? CalcMethod);

public static class ReconDriverMap
{
    private static readonly HashSet<string> PackageCalcMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fixed", "StructureField", "PercentOfBasic", "PercentOfGross",
    };

    /// <summary>
    /// The driver for one payroll line, from its Source and (when the code is a catalogue component) how that
    /// component is fed. Unrecognised lines are <see cref="ReconDriver.Other"/>: they are shown, flagged, and have
    /// to be explained, never guessed into a friendlier bucket.
    /// </summary>
    public static ReconDriver Classify(string? source, string code, ComponentHint? hint)
    {
        var s = source?.Trim();
        if (string.Equals(s, "Arrears", StringComparison.OrdinalIgnoreCase)) return ReconDriver.Arrears;
        if (In(s, "Overtime", "Bonus", "Adjustment")) return ReconDriver.Variable;
        if (In(s, "Attendance", "Leave")) return ReconDriver.Attendance;
        if (In(s, "Loan", "Recovery")) return ReconDriver.Loans;
        if (In(s, "Statutory", "Tax")) return ReconDriver.Statutory;
        if (string.Equals(s, "Salary", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrEmpty(hint?.ProviderKey) ? ReconDriver.Variable : ReconDriver.Package;

        // A source this map does not know: fall back on how the catalogue says the component is fed.
        if (!string.IsNullOrEmpty(hint?.ProviderKey)) return ReconDriver.Variable;
        if (hint?.CalcMethod is { } calc && PackageCalcMethods.Contains(calc)) return ReconDriver.Package;
        return ReconDriver.Other;
    }

    private static bool In(string? value, params string[] set) =>
        value is not null && set.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The contract property name of a driver bucket under <c>continuing</c>.</summary>
    public static string BucketName(ReconDriver d) => d switch
    {
        ReconDriver.Package => "package",
        ReconDriver.Proration => "proration",
        ReconDriver.Variable => "variable",
        ReconDriver.Arrears => "arrears",
        ReconDriver.Attendance => "attendance",
        ReconDriver.Loans => "loans",
        ReconDriver.Statutory => "statutory",
        ReconDriver.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };
}
