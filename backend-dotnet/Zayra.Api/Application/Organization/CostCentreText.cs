using System.Text;

namespace Zayra.Api.Application.Organization;

/// <summary>
/// Plain single-line text for a cost-centre name (it travels into ERP files). Tab, CR and LF become a space; left-to-right /
/// right-to-left marks, embeddings, overrides and isolates are dropped; any other control character is refused.
/// </summary>
public static class CostCentreText
{
    public static bool IsDirectionControl(char c) =>
        c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏';

    /// <summary>Removes direction controls only (used where text already stored must be neutralised on the way out).</summary>
    public static string StripDirectionControls(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Any(IsDirectionControl)) return value ?? string.Empty;
        return string.Concat(value.Where(c => !IsDirectionControl(c)));
    }

    public static string Normalise(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (IsDirectionControl(c)) continue;
            if (c is '\t' or '\r' or '\n') { sb.Append(' '); continue; }
            if (char.IsControl(c)) throw CostCentreRuleException.NameInvalid();
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
