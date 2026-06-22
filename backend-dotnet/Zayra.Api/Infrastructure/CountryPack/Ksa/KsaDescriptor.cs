using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

public sealed class KsaDescriptor : ICountryPackDescriptor
{
    public PackDescriptor GetDescriptor() => new(
        CountryCode:              CountryCodes.Saudi,
        CountryNameEn:            "Saudi Arabia",
        CountryNameAr:            "المملكة العربية السعودية",
        SocialInsuranceScheme:    "GOSI",
        SocialInsuranceDescription:
            "General Organization for Social Insurance — Saudi nationals: Annuities (9% EE + 9% ER) + SANED (0.75% each) + OH (2% ER) on covered wage (basic+housing, ≤ SAR 45,000); total EE 9.75%, ER 11.75%. Expatriates: Occupational Hazard only (2% employer).",
        EosbFormula:
            "KSA Labor Law Art. 84 — ½ month basic/yr (yrs 1–5), 1 full month basic/yr (after 5 yrs), pro-rated by days. Resignation discount: 0% (<2 yrs), ⅓ (2–5 yrs), ⅔ (5–10 yrs), full (>10 yrs).",
        WpsFormat:     "mudad-xml",
        WpsFormatLabel: "Mudad XML v1 (Ministry of Human Resources — VERIFY specification before live integration)",
        NationalizationScheme: "Nitaqat");
}
