using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>Optional conditionality for a requirement, evaluated against the employee snapshot.
/// Absent ⇒ always applies. Conditionality is a COMPLIANCE requirement, not a nicety — requiring an
/// Iqama for a Saudi national is wrong and would produce false blocks. The split is THREE-WAY, not
/// binary: host-national (Nationality==country), GCC-national (a different GCC state — GCC common-market
/// rules waive host work-permit/visa/residency + host social insurance), and non-GCC expat (the only
/// class that needs a host work permit / residence visa). <see cref="NonGccExpatOnly"/> encodes that
/// third class so a Saudi working in the UAE is never falsely required to hold a UAE work permit.</summary>
public sealed record AppliesWhen(
    string? Nationality = null,       // GCC nationality (ISO-2 or common name) equals this — the host-national row
    string? NationalityNot = null,    // GCC nationality NOT this (legacy binary expat split; kept for profile JSON compat)
    bool? WpsEnabled = null,          // resolved from the effective policy layer (GCC setting/profile)
    bool? NonGccExpatOnly = null);    // applies ONLY to a non-GCC expat (host + other-GCC nationals excluded)

/// <summary>
/// One required-to-be-ready field, jurisdiction-tagged. FailClosed=true blocks (hard gate);
/// false = recommended (counts toward %, amber/optional, never blocks). Gate "activate" blocks
/// activation; "pay" blocks payroll inclusion only.
/// </summary>
public sealed record ReadinessRequirement(
    string Key,
    string Category,
    bool FailClosed,
    string Gate,                      // "activate" | "pay"
    string Source,                    // floor | tenant | company | gcc-setting
    string? Jurisdiction = null,
    bool? RequireVerified = null,
    AppliesWhen? AppliesWhen = null);

/// <summary>
/// The always-present, code-owned statutory readiness floor keyed by ISO-2 + nationality (§3.4).
/// Promotes the EnterpriseGroupSeeder literals into a floor that gates a settings-untouched tenant.
/// Kept in code (like CountryTier / StatutoryRateGuard) so the gate is fail-safe and cannot be
/// widened by editing a tenant's rows. DELIBERATELY CONSERVATIVE (§11): the hard (FailClosed) set is
/// the identity + work-authorization + social-insurance items a client can only make STRICTER via
/// config, never weaker. Non-GCC countries get an EMPTY floor (config profiles alone apply).
/// </summary>
public static class GccReadinessFloor
{
    /// <summary>The retained compliance disclaimer, mirrored on every readiness response
    /// (CompanyGovernanceController.cs:200).</summary>
    public const string Disclaimer =
        "Compliance profiles are configurable readiness tools — not legal certification. Country rule packs require legal validation.";

    /// <summary>Resolve free-text nationality to a GCC ISO-2 code where recognisable, so the
    /// national/expat split is reliable ("UAE"/"Emirati" → AE). Unrecognised nationality is returned
    /// uppercased as-is — an unknown nationality in a GCC state is then treated as an expat
    /// (fail-safe: work-authorization requirements apply).</summary>
    public static string NormalizeNationality(string? nationality)
    {
        var n = (nationality ?? string.Empty).Trim().ToUpperInvariant();
        if (n.Length == 0) return string.Empty;
        return n switch
        {
            "SA" or "SAU" or "KSA" or "SAUDI" or "SAUDI ARABIA" or "SAUDIARABIA" => "SA",
            "AE" or "ARE" or "UAE" or "EMIRATI" or "EMIRATES" or "UNITED ARAB EMIRATES" => "AE",
            "QA" or "QAT" or "QATAR" or "QATARI" => "QA",
            "KW" or "KWT" or "KUWAIT" or "KUWAITI" => "KW",
            "OM" or "OMN" or "OMAN" or "OMANI" => "OM",
            "BH" or "BHR" or "BAHRAIN" or "BAHRAINI" => "BH",
            _ => CountryCodeStandard.NormalizeToIso2(n) ?? n,
        };
    }

    /// <summary>The floor requirements for a jurisdiction. Empty for non-GCC countries. Covers ALL SIX
    /// GCC states on TWO axes (country × nationality). Identity CARDS (Emirates ID, QID, Civil ID, CPR)
    /// are held by citizens and residents alike ⇒ all-nationality. Work-authorization (work permit,
    /// residence visa, Iqama) applies to NON-GCC EXPATS only (GCC common-market reciprocity). Host social
    /// insurance is the national scheme for host nationals (GPSSA/GRSIA/PIFSS/SPF); KSA GOSI and Bahrain
    /// SIO extend to all residents by that country's rules.</summary>
    public static IReadOnlyList<ReadinessRequirement> Resolve(string? countryCode)
    {
        var iso2 = (CountryCodeStandard.NormalizeToIso2(countryCode) ?? countryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!CountryTier.IsGcc(iso2)) return System.Array.Empty<ReadinessRequirement>();

        var items = new List<ReadinessRequirement>();

        // Universal GCC floor (conservative): recommended-to-activate, hard-to-pay where money needs it.
        items.Add(Req("doc:Contract", "contract", failClosed: false, "activate", iso2));
        items.Add(Req("SalaryStructure", "payroll", failClosed: false, "activate", iso2));
        items.Add(Req("BankIban", "payroll", failClosed: false, "pay", iso2, wpsUpgrade: true));

        switch (iso2)
        {
            case "SA":
                items.Add(Req("GosiReference", "identity", true, "activate", iso2));                          // all residents (nationals full, expats OH)
                items.Add(Req("IdNumber", "identity", true, "activate", iso2, nationality: "SA"));            // National ID (Hawiyya) — host national
                items.Add(Req("IqamaNumber", "identity", true, "activate", iso2, nonGccExpat: true));         // residence permit — non-GCC expat
                items.Add(Req("IqamaExpiry", "identity", true, "pay", iso2, nonGccExpat: true));
                break;
            case "AE":
                items.Add(Req("EmiratesId", "identity", true, "activate", iso2));                             // all residents
                items.Add(Req("EmiratesIdExpiry", "identity", true, "pay", iso2));
                items.Add(Req("SocialInsuranceReference", "identity", true, "activate", iso2, nationality: "AE")); // GPSSA — Emirati nationals
                items.Add(Req("WorkPermitNumber", "identity", true, "activate", iso2, nonGccExpat: true));    // MOHRE work permit / labour card
                items.Add(Req("VisaExpiryDate", "identity", true, "pay", iso2, nonGccExpat: true));           // residence visa
                items.Add(Req("MolId", "payroll", false, "pay", iso2));
                break;
            case "QA":
                items.Add(Req("Qid", "identity", true, "activate", iso2));                                    // QID — all residents (nationals hold a QID too)
                items.Add(Req("QidExpiry", "identity", true, "pay", iso2));
                items.Add(Req("SocialInsuranceReference", "identity", true, "activate", iso2, nationality: "QA")); // GRSIA — Qatari nationals
                break;
            case "KW":
                items.Add(Req("CivilId", "identity", true, "activate", iso2));                                // Kuwait Civil ID — all residents
                items.Add(Req("CivilIdExpiry", "identity", true, "pay", iso2));
                items.Add(Req("SocialInsuranceReference", "identity", true, "activate", iso2, nationality: "KW")); // PIFSS — Kuwaiti nationals
                break;
            case "OM":
                items.Add(Req("CivilId", "identity", true, "activate", iso2));                                // Oman Resident Card / Civil ID — all residents
                items.Add(Req("CivilIdExpiry", "identity", true, "pay", iso2));
                items.Add(Req("SocialInsuranceReference", "identity", true, "activate", iso2, nationality: "OM")); // SPF (PASI legacy) — Omani nationals
                break;
            case "BH":
                items.Add(Req("CivilId", "identity", true, "activate", iso2));                                // Bahrain CPR — all residents
                items.Add(Req("CivilIdExpiry", "identity", true, "pay", iso2));
                items.Add(Req("SocialInsuranceReference", "identity", true, "activate", iso2));               // SIO (GOSI-BH legacy) — all residents
                break;
        }
        return items;
    }

    /// <summary>Whether a requirement's nationality conditionality applies to a normalized nationality.
    /// The ONE place nationality applicability is decided — the resolver's enforcement AND the
    /// catalog↔floor parity guard both call this, so a Saudi national never sees an Iqama field and the
    /// two surfaces can never drift. WPS conditionality is evaluated separately (needs the policy layer).</summary>
    public static bool NationalityApplies(AppliesWhen? when, string normalizedNationality)
    {
        if (when is null) return true;
        if (when.Nationality is { } eq
            && !string.Equals(NormalizeNationality(eq), normalizedNationality, System.StringComparison.OrdinalIgnoreCase))
            return false;
        if (when.NationalityNot is { } ne
            && string.Equals(NormalizeNationality(ne), normalizedNationality, System.StringComparison.OrdinalIgnoreCase))
            return false;
        // Non-GCC-expat rows (work permit / visa / residency / Iqama): host nationals AND other-GCC
        // nationals are excluded (GCC common-market reciprocity). Unknown/blank normalizes to non-GCC ⇒
        // treated as expat (fail-safe: work-authorization requirements apply).
        if (when.NonGccExpatOnly == true && CountryTier.IsGcc(normalizedNationality))
            return false;
        return true;
    }

    private static ReadinessRequirement Req(
        string key, string category, bool failClosed, string gate, string jurisdiction,
        string? nationality = null, string? nationalityNot = null, bool nonGccExpat = false, bool wpsUpgrade = false)
    {
        AppliesWhen? when = null;
        if (nationality is not null || nationalityNot is not null || nonGccExpat || wpsUpgrade)
            when = new AppliesWhen(nationality, nationalityNot, null, nonGccExpat ? true : null);
        // A wps-upgrade IBAN carries an appliesWhen so the resolver can upgrade pay→activate when WPS is on.
        return new ReadinessRequirement(key, category, failClosed, gate, "floor", jurisdiction,
            RequireVerified: null, AppliesWhen: when);
    }
}
