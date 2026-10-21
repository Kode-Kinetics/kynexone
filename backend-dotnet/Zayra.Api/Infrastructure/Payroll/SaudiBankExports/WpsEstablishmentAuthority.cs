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
            if (mol is not null && !string.Equals(mol, company, StringComparison.Ordinal))
                return (null, Mismatch(company, mol, "the Saudi bank-file settings"));
            if (agent is not null && !string.Equals(agent, company, StringComparison.Ordinal))
                return (null, Mismatch(company, agent, "the WPS employer ID in Setup → Compliance"));
            return (company, null);
        }

        if (!isKsa) return (agent, null);
        if (mol is not null && agent is not null && !string.Equals(mol, agent, StringComparison.Ordinal))
            return (null, new(ConflictCode,
                $"Two different establishment IDs are recorded for this company: {mol} in the Saudi bank-file settings and {agent} "
                + "as the WPS employer ID in Setup → Compliance. Make them the same (the MOL establishment ID shown in Qiwa), then try again.",
                null, "molEstablishmentId"));
        return (mol ?? agent, null);
    }

    private static SaudiBankExportIssueDto Mismatch(string company, string other, string where) =>
        new(ConflictCode,
            $"Two different establishment IDs are recorded for this company: {company} as the company's MOL establishment number "
            + $"(Setup → Companies) and {other} in {where}. The wage file uses the company's number; make the other one the same "
            + "(the MOL establishment ID shown in Qiwa), or clear it, then try again.",
            null, "molEstablishmentId");
}
