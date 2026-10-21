namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>
/// Which establishment id a wage file carries, when up to three places record one (design contract Decision 10):
/// the company's own MOL establishment number (<c>Company.WpsEmployerId</c>, captured once at company creation),
/// the Saudi bank-file settings' <c>MolEstablishmentId</c>, and the GCC compliance row's <c>WpsAgentId</c>.
///
/// <list type="bullet">
///   <item>The company value, when set, is the authority and the file carries it. Any other recorded value that
///     differs refuses the file, naming both places — two files for one month must never name two establishments,
///     and a silent preference would file under whichever number happened to win.</item>
///   <item>No company value: today's behaviour, unchanged — KSA prefers the bank-file settings over the GCC row and
///     refuses when they differ; other countries take the GCC row.</item>
/// </list>
/// Pure: the caller loads the three values. Null id with null conflict = nothing recorded (the caller refuses).
///
/// <para>Two ids are the same establishment when they match after removing spaces and hyphens, and nothing else:
/// Qiwa prints "7-1234567" and people type "71234567". The comparison never rewrites the id — the file carries the
/// winning value exactly as it was recorded. Letters, zeros and every other character still count.</para>
/// </summary>
public static class WpsEstablishmentAuthority
{
    public const string ConflictCode = "mol_establishment_id_conflict";

    public static (string? Id, SaudiBankExportIssueDto? Conflict) Resolve(string? companyValue, string? bankSettingsMolId, string? gccAgentId, bool isKsa)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var company = Clean(companyValue);
        var mol = isKsa ? Clean(bankSettingsMolId) : null;
        var agent = Clean(gccAgentId);

        if (company is not null)
        {
            if (mol is not null && !SameEstablishment(mol, company))
                return (null, Mismatch(company, mol, "the Saudi bank-file settings"));
            if (agent is not null && !SameEstablishment(agent, company))
                return (null, Mismatch(company, agent, "the WPS employer ID in Setup → Compliance"));
            return (company, null);
        }

        if (!isKsa) return (agent, null);
        if (mol is not null && agent is not null && !SameEstablishment(mol, agent))
            return (null, new(ConflictCode,
                $"Two different establishment IDs are recorded for this company: {mol} in the Saudi bank-file settings and {agent} "
                + "as the WPS employer ID in Setup → Compliance. Make them the same (the MOL establishment ID shown in Qiwa), then try again.",
                null, "molEstablishmentId"));
        return (mol ?? agent, null);
    }

    /// <summary>The comparison key: the id with spaces and hyphens removed. Used only to compare, never written.</summary>
    public static string ComparisonKey(string value) => value.Replace(" ", string.Empty).Replace("-", string.Empty);

    /// <summary>Whether two recorded ids name the same establishment (see <see cref="ComparisonKey"/>).</summary>
    public static bool SameEstablishment(string a, string b) => string.Equals(ComparisonKey(a), ComparisonKey(b), StringComparison.Ordinal);

    private static SaudiBankExportIssueDto Mismatch(string company, string other, string where) =>
        new(ConflictCode,
            $"Two different establishment IDs are recorded for this company: {company} as the company's MOL establishment number "
            + $"(Setup → Companies) and {other} in {where}. The wage file uses the company's number; make the other one the same "
            + "(the MOL establishment ID shown in Qiwa), or clear it, then try again.",
            null, "molEstablishmentId");
}
